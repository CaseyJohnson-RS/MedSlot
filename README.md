# MedSlot — сервис записи к врачу

Backend-сервис онлайн-записи пациентов к врачам. Расписания врачей, бронирование слотов с защитой от двойной записи, отмены по правилам клиники, лист ожидания. Доменные события публикуются в Kafka и обрабатываются независимыми сервисами (уведомления, аналитика, аудит).

## Ключевые задачи

- **Без двойной брони**: конкурентные запросы на один слот не приводят к двум записям (exclusion constraint в PostgreSQL + optimistic concurrency).
- **Идемпотентность**: повтор POST с тем же `Idempotency-Key` не создаёт дубль.
- **Гарантированная доставка событий**: Transactional Outbox, at-least-once, идемпотентные консьюмеры.
- **Порядок событий** по врачу: партиционирование топиков по `doctorId`.
- **Персональные данные**: маскирование в логах, журнал доступа к карточке пациента.

## Домен

Подробная модель и правила — в [docs/domain.md](docs/domain.md).

| Сущность | Суть |
|---|---|
| `Doctor` | Специализация, рабочее расписание, исключения (отпуск, больничный) |
| `Service` | Тип приёма с длительностью (первичный 30 мин, повторный 15 мин) |
| `Slot` | Вычисляется из расписания и длительности услуги |
| `Appointment` | Запись: `Held → Booked → Completed / Cancelled / LateCancelled / NoShow` |
| `WaitlistEntry` | Очередь на освободившийся слот |

## Архитектура

Модульный монолит (Clean / Hexagonal) и отдельные воркеры-консьюмеры.

```
src/
  MedSlot.Api              # ASP.NET Core, эндпоинты, auth
  MedSlot.Application      # use cases, порты
  MedSlot.Contracts        # интеграционные события (конверт и схемы), без зависимостей
  MedSlot.Domain           # агрегаты, инварианты, доменные события
  MedSlot.Infrastructure   # EF Core, Outbox, Kafka producer
  MedSlot.Workers.Common         # цикл консьюмера, inbox, DLT
  MedSlot.Workers.Notifications  # уведомления: подтверждение, напоминание, отмена
  MedSlot.Workers.Audit          # append-only журнал всех событий
  MedSlot.Workers.Analytics      # заготовка (фаза 9)
tests/
  MedSlot.Domain.Tests
  MedSlot.Integration.Tests   # Testcontainers
  MedSlot.Architecture.Tests  # NetArchTest
web/                          # фронтенд (сгенерирован AI-агентом)
```

**События (топик `appointments`, ключ `doctorId`):** `AppointmentBooked`, `AppointmentCancelled`, `AppointmentCompleted`, `AppointmentNoShow`, `WaitlistSlotOffered`.

## Стек

| Область | Технологии |
|---|---|
| Платформа | .NET 10, C#, ASP.NET Core (Minimal API) |
| Данные | PostgreSQL, EF Core + Npgsql, миграции EF |
| Сообщения | Apache Kafka (KRaft), Confluent.Kafka, свой Outbox |
| Кэш / rate limit | Встроенный ASP.NET Rate Limiting (Redis — по результатам нагрузочных тестов) |
| Auth | JWT + refresh-токены, роли `Patient` / `Doctor` / `Admin` |
| Валидация | FluentValidation |
| Наблюдаемость | OpenTelemetry (трейсы через Kafka), Serilog, Prometheus + Grafana |
| Тесты | xUnit, Testcontainers (Postgres, Kafka), NetArchTest, NBomber для нагрузки |
| Документация API | OpenAPI + Scalar |
| Инфраструктура | Docker Compose, GitHub Actions (build, test, lint) |

MediatR и MassTransit не используются намеренно: в 2025–2026 оба перешли на коммерческие лицензии, а Outbox и диспетчеризацию полезно написать самому.

## Документация

- [План работ](docs/ROADMAP.md)
- [Доменная модель](docs/domain.md)
- [Architecture Decision Records](docs/adr/)

## Ветки

- `main` — стабильная ветка, сюда вливаются feature-ветки;
- `feature/*`, `docs/*`, `fix/*` — рабочие ветки;
- `deploy` — ветка развёртывания.

## Запуск

Нужны .NET SDK 10 и Docker.

```bash
docker compose up -d
```

| Сервис | Адрес |
|---|---|
| API (Scalar) | http://localhost:8080/scalar |
| Kafka UI | http://localhost:8081 |
| Mailpit (письма уведомлений) | http://localhost:8025 |
| PostgreSQL | `localhost:5432`, БД/пользователь/пароль `medslot` |
| Kafka | `localhost:9092` |

В режиме Development API сам применяет миграции и заполняет пустую БД демо-данными.

### Попробовать API

Демо-учётные записи (пароль у всех `Demo-2026!`, только для локального стенда):

| Роль | Логин |
|---|---|
| Администратор | `admin@medslot.local` |
| Врачи | `ivanova@medslot.local` (терапевт), `petrov@medslot.local`, `smirnova@medslot.local` |
| Пациенты | `alice@example.com` (уведомления письмом), `bob@example.com` (SMS-заглушка) |

Демо-данные имеют фиксированные id:

| Что | id |
|---|---|
| Терапевт | `0199c0de-0000-7000-8000-000000000101` |
| Первичный приём, 30 мин | `0199c0de-0000-7000-8000-000000000001` |

1. `POST /api/v1/auth/login` — вход; `accessToken` из ответа передаётся в заголовке `Authorization: Bearer …` (в Scalar — кнопка Authentication).
2. `GET /api/v1/doctors/{doctorId}/slots?serviceId=…&from=…&to=…` — свободные слоты (без входа).
3. `POST /api/v1/appointments` с заголовком `Idempotency-Key` — запись. Повтор с тем же ключом вернёт тот же ответ.
4. `GET /api/v1/me/appointments` — свои записи; `POST /api/v1/appointments/{id}/cancel` — отмена.
5. `POST /api/v1/auth/refresh` — новая пара токенов; refresh-токен одноразовый.

Ошибки приходят в формате `ProblemDetails` с машиночитаемым кодом в поле `code`, например `appointment.slot_already_taken`.

Только инфраструктура, API — из IDE или `dotnet run --project src/MedSlot.Api`:

```bash
docker compose up -d postgres kafka kafka-ui
```

Тесты:

```bash
dotnet test
```

# MedSlot — план работ

Оценки даны из расчёта ~10–15 часов в неделю. Каждая фаза заканчивается рабочим, протестированным состоянием и коммитом в `main` — проект можно показывать после любой из них.

Общая оценка: **12–16 недель** до полного объёма, **~6 недель** до показываемого MVP (фазы 0–4).

---

## Фаза −1. Доменные решения (до кода, 2–3 вечера) — ✅ готово

Зафиксировано в [domain.md](domain.md) и ADR 0002–0004.

- [x] **Статусы записи:** `Held`, `Booked`, `Released`, `Cancelled`, `LateCancelled`, `Completed`, `NoShow`; `Confirmed` убран ([ADR 0002](adr/0002-appointment-status-model.md)).
- [x] **Поздняя отмена** — отдельный статус `LateCancelled`, наравне с `NoShow` считается нарушением.
- [x] **NoShow** ставит врач после начала приёма; автоматически — через `NoShowAutoMarkDelay` после окончания.
- [x] **Бан:** 3 нарушения за скользящие 90 дней → 30 дней без онлайн-записи; вычисляется из истории ([ADR 0004](adr/0004-violations-derived-from-history.md)).
- [x] **Лист ожидания:** FIFO, удержание `Held` с `HoldExpiresAt`, отказ/таймаут → следующий кандидат.
- [x] **Время:** одна таймзона клиники, UTC в БД, NodaTime ([ADR 0003](adr/0003-time-handling-with-nodatime.md)).
- [x] **Шаг сетки** 15 мин, длительность услуги кратна шагу.
- [x] **Пересечения у пациента** запрещены вторым exclusion constraint.
- [x] Параметры клиники, события с payload, матрица ролей, границы проекта.

---

## Фаза 0. Каркас (1 неделя) — ✅ готово

### Задачи
- [x] `git init`, `.gitignore`, `.editorconfig`, `global.json`, `Directory.Build.props` (nullable, warnings as errors, `LangVersion latest`), `Directory.Packages.props` (central package management).
- [x] Solution (`MedSlot.slnx`) и проекты по структуре из README; ссылки строго по направлению зависимостей:
  `Domain ← Application ← Infrastructure ← Api`, воркеры → `Infrastructure`.
- [x] `MedSlot.Architecture.Tests` (NetArchTest):
  - Domain зависит только от BCL и NodaTime;
  - Application не зависит от Infrastructure, хостов, EF Core, Npgsql, Confluent.Kafka, ASP.NET Core;
  - Infrastructure не зависит от хостов;
  - Api не использует EF Core напрямую;
  - воркеры не зависят от Api и друг от друга.
- [x] Тесты на xUnit v3 + Microsoft Testing Platform (включена в `global.json`), Shouldly.
- [x] `docker-compose.yml`: API, Postgres 18, Kafka 4.3 (KRaft, один брокер), Kafka UI (kafbat). Healthchecks.
- [x] Пустой Minimal API с `/health/live`, `/health/ready`, OpenAPI + Scalar; Dockerfile; smoke-тесты через `WebApplicationFactory`.
- [x] GitHub Actions: restore → `dotnet format --verify-no-changes` → build → unit + architecture tests; интеграционные тесты и сборка Docker-образа — отдельные jobs.
- [x] `docs/adr/0001-record-architecture-decisions.md` + шаблон (сделано в фазе −1).

**Готово, когда:** `docker compose up -d` поднимает инфраструктуру, `dotnet test` зелёный, CI зелёный.

---

## Фаза 1. Домен (1–2 недели)

Чистый C#, без БД. Максимум unit-тестов.

### Задачи
- [ ] Базовые типы: `Entity`, `AggregateRoot` (список доменных событий), строго типизированные ID (`DoctorId`, `PatientId`…), `Result`/доменные ошибки (без исключений для бизнес-отказов).
- [ ] Value objects: `TimeRange`, `ServiceDuration`, `WorkingHours`, `PhoneNumber`, `Email`.
- [ ] `Doctor`: специализация, недельное расписание (`WeeklySchedule`), исключения (`ScheduleException`: отпуск, больничный, разовая смена часов).
- [ ] `Service`: название, длительность, кратность шагу сетки.
- [ ] `SlotCalculator` (доменный сервис): расписание + исключения + занятые интервалы + длительность услуги → свободные слоты на период. Учитывать DST.
- [ ] `Appointment`: методы `Book`, `Cancel(now, policy)`, `Complete`, `MarkNoShow`; инварианты переходов; события `AppointmentBooked`, `AppointmentCancelled`, `AppointmentCompleted`, `AppointmentNoShow`.
- [ ] `Patient` (или `PatientBookingRestriction`): учёт нарушений, проверка `CanBookOnline(now)`.
- [ ] `CancellationPolicy` (N часов) — как параметр, не константа.
- [ ] `IClock` в Application, `FakeClock` в тестах.

### Тесты (`MedSlot.Domain.Tests`)
- [ ] Переходы state machine: все допустимые и недопустимые.
- [ ] Отмена ровно на границе N часов (±1 секунда).
- [ ] Слоты: исключения, перерывы, услуга не влезает в конец дня, переход на летнее/зимнее время.
- [ ] Бан: 3 нарушения в окне / 2 в окне + 1 вне окна.

**Готово, когда:** покрытие домена ≥ 90 %, ни одной зависимости от инфраструктуры.

---

## Фаза 2. Персистентность и бронирование (1–2 недели)

### Задачи
- [ ] EF Core + Npgsql, конфигурации через `IEntityTypeConfiguration`, snake_case, value converters для ID и NodaTime.
- [ ] Миграция с `btree_gist` и exclusion constraints:
  ```sql
  EXCLUDE USING gist (doctor_id WITH =, tstzrange(starts_at, ends_at) WITH &&)
    WHERE (status IN ('Held','Booked'));
  EXCLUDE USING gist (patient_id WITH =, tstzrange(starts_at, ends_at) WITH &&)
    WHERE (status IN ('Held','Booked'));
  ```
- [ ] Optimistic concurrency через `xmin` для `Appointment` (гонки переходов статуса).
- [ ] Маппинг ошибок Postgres: `23P01` (exclusion violation) → доменная ошибка `SlotAlreadyTaken`; `DbUpdateConcurrencyException` → `ConcurrentModification` (409).
- [ ] Порты в Application: `IAppointmentRepository`, `IDoctorRepository`, `IUnitOfWork`.
- [ ] Use cases (свой простой диспетчер — интерфейсы `ICommandHandler<TCommand, TResult>` / `IQueryHandler<,>` + регистрация через DI, без MediatR):
  - `BookAppointment`, `CancelAppointment`, `CompleteAppointment`, `MarkNoShow`;
  - `GetAvailableSlots`, `GetDoctorSchedule`, `GetPatientAppointments`.
- [ ] Декораторы для хендлеров: логирование, транзакция, валидация.
- [ ] Сиды для разработки: 3–5 врачей, услуги, расписания.

### Тесты (`MedSlot.Integration.Tests`, Testcontainers Postgres)
- [ ] **Главный тест:** 50 параллельных `BookAppointment` на один слот → ровно 1 успех, 49 `SlotAlreadyTaken`.
- [ ] Пересекающиеся, но не совпадающие интервалы (30-мин и 15-мин услуги).
- [ ] Отменённая запись не блокирует слот.
- [ ] Одновременные `Cancel` и `Complete` → один проходит, второй получает 409.
- [ ] Миграции применяются на чистую БД.

**Готово, когда:** тест конкурентности стабильно зелёный (прогнать 20 раз подряд).
ADR: `0005-exclusion-constraint-for-double-booking.md`, `0006-own-dispatcher-instead-of-mediatr.md`.

---

## Фаза 3. API, валидация, идемпотентность (1 неделя)

### Задачи
- [ ] Эндпоинты Minimal API, сгруппированные по ресурсам (`MapGroup`), версия `/api/v1`.
- [ ] FluentValidation — фильтр эндпоинтов, единый формат ошибок `ProblemDetails` (RFC 9457) с кодами доменных ошибок.
- [ ] **Idempotency-Key** для POST-запросов, меняющих состояние:
  - таблица `idempotency_keys (key, user_id, request_hash, status, response_code, response_body, created_at)`, PK `(user_id, key)`;
  - вставка ключа в статусе `InProgress` **в той же транзакции**, что и бизнес-операция;
  - повтор с тем же ключом и тем же телом → сохранённый ответ;
  - тот же ключ, другое тело → 422;
  - ключ в статусе `InProgress` (параллельный запрос) → 409;
  - очистка ключей старше 24 ч фоновой задачей.
- [ ] Rate limiting (встроенный, in-memory) на бронирование: per-user fixed window.
- [ ] OpenAPI: описание кодов ошибок, примеры, заголовок `Idempotency-Key`.

### Тесты
- [ ] Повтор POST с тем же ключом → тот же ответ, одна запись в БД.
- [ ] Два параллельных запроса с одним ключом → одна запись.
- [ ] Валидация: невалидные данные → 400 с понятными полями.

**Готово, когда:** весь сценарий «найти слот → записаться → отменить» проходит через Scalar.
ADR: `0007-idempotency-keys-in-postgres.md`.

---

## Фаза 4. Transactional Outbox + Kafka (1–2 недели)

### Задачи
- [ ] Таблица `outbox_messages (id bigserial, event_id uuid, aggregate_key, type, payload jsonb, headers jsonb, occurred_at, published_at, attempts, last_error)`.
- [ ] `SaveChanges`-интерсептор: доменные события агрегатов → строки outbox в той же транзакции.
- [ ] **Конверт события:** `eventId`, `eventType`, `schemaVersion`, `occurredAt`, `aggregateId`, `doctorId`, `traceparent`, `payload`. **Никаких ПДн в payload** — только ID.
- [ ] `OutboxRelay` (`BackgroundService`):
  - единственный активный экземпляр — `pg_try_advisory_lock` (leader election);
  - выборка `ORDER BY id LIMIT N`, публикация, отметка `published_at`;
  - при ошибке публикации ключа — не публиковать последующие события **того же ключа** в этой итерации (сохранение порядка);
  - экспоненциальный backoff, метрики (лаг, ошибки).
- [ ] Kafka producer: `enable.idempotence=true`, `acks=all`, ключ = `doctorId`.
- [ ] Топик `appointments` (6 партиций) + DLT `appointments.dlt`; создание топиков при старте (dev) / скриптом.
- [ ] Очистка опубликованных записей старше 7 дней.

### Тесты (Testcontainers Postgres + Kafka)
- [ ] Бронирование → событие в Kafka с правильным ключом и конвертом.
- [ ] Откат транзакции → события нет.
- [ ] Kafka недоступна → события копятся в outbox, после восстановления доставлены в исходном порядке.
- [ ] Два экземпляра relay → публикует только один.

**Готово, когда:** события доставляются at-least-once с сохранением порядка по врачу. **Это MVP — можно показывать.**
ADR: `0008-transactional-outbox.md`, `0009-event-ordering-per-doctor.md`.

---

## Фаза 5. Воркеры-консьюмеры (1–2 недели)

### Общая часть (`MedSlot.Workers.Common` или в Infrastructure)
- [ ] Базовый consumer host: ручной commit offset **после** обработки, `enable.auto.commit=false`.
- [ ] **Inbox**: таблица `processed_events (consumer, event_id)` в БД воркера; проверка и бизнес-действие в одной транзакции.
- [ ] Retry с backoff → после N попыток в DLT с причиной в заголовках.
- [ ] Обработка неизвестных `eventType` / `schemaVersion` — лог + skip (forward compatibility).
- [ ] Graceful shutdown, rebalance-хендлеры.

### Notifications
- [ ] Шаблоны: запись создана, отменена, напоминание за 24 ч, предложение из листа ожидания.
- [ ] Порт `INotificationSender`; реализация — запись в лог / MailHog (SMTP в docker-compose).
- [ ] Контакты пациента получает запросом к API/БД по `patientId` (ПДн не идут через Kafka).
- [ ] Напоминания за 24 ч — таблица запланированных уведомлений + планировщик.

### Audit
- [ ] Append-only таблица аудита (без UPDATE/DELETE на уровне прав БД).
- [ ] Запись всех доменных событий.

### Analytics (можно отложить в фазу 9)
- [ ] Проекции: загрузка врачей по дням, доля no-show, отмены по причинам.
- [ ] Эндпоинт или Grafana-дашборд поверх этих таблиц.

### Тесты
- [ ] Повторная доставка одного события → один эффект.
- [ ] «Ядовитое» сообщение → попадает в DLT, остальные обрабатываются.
- [ ] Падение воркера после обработки, до commit → повтор не дублирует эффект.

**Готово, когда:** все три (или два) воркера работают в docker-compose.
ADR: `0010-inbox-for-idempotent-consumers.md`.

---

## Фаза 6. Лист ожидания (1 неделя)

### Задачи
- [ ] `WaitlistEntry`: пациент, врач, услуга, желаемый диапазон, позиция (по времени создания), статус.
- [ ] На `AppointmentCancelled` (внутри монолита, через outbox → свой consumer или in-process обработчик после коммита) — поиск первого подходящего в очереди.
- [ ] Создание `Appointment` в статусе `Held` с `expires_at` — слот защищён тем же exclusion constraint.
- [ ] Событие `WaitlistSlotOffered`; эндпоинты `accept` / `decline`.
- [ ] `HoldExpirer` (`BackgroundService`, `FOR UPDATE SKIP LOCKED`): просроченные `Held` → освобождение → предложение следующему.
- [ ] События `WaitlistOfferAccepted`, `WaitlistOfferExpired`, `WaitlistOfferDeclined`.
- [ ] Пациент с баном не получает предложений.

### Тесты
- [ ] Отмена → первый в очереди получает `Held`; посторонний не может забронировать этот слот.
- [ ] Таймаут → предложение переходит второму.
- [ ] Accept после истечения → отказ.
- [ ] Параллельные accept и expire → консистентный результат.

**Готово, когда:** полный цикл «отмена → предложение → принятие/таймаут» покрыт интеграционными тестами.

---

## Фаза 7. Аутентификация и персональные данные (1 неделя)

### Задачи
- [ ] Регистрация/логин пациента, хеширование паролей (`PasswordHasher<T>` из ASP.NET Identity Core или Argon2).
- [ ] JWT access (15 мин) + refresh (30 дней, хранится хешем в БД).
- [ ] Ротация refresh-токенов, семейства токенов, **reuse detection** → отзыв всего семейства.
- [ ] Роли `Patient` / `Doctor` / `Admin`, политики авторизации; resource-based проверки (пациент видит только свои записи, врач — только свои приёмы).
- [ ] Admin-эндпоинты: врачи, расписания, исключения, снятие бана.
- [ ] **Маскирование ПДн в логах:** Serilog destructuring policy + атрибут `[PersonalData]` на свойствах; тест, что в логах нет телефона/email.
- [ ] **Журнал доступа к карточке пациента:** кто, когда, к какой карточке, зачем — пишется **синхронно** (или через outbox), не теряется.
- [ ] Эндпоинт для пациента: «кто смотрел мою карточку».

### Тесты
- [ ] Повторное использование refresh-токена → отзыв семейства.
- [ ] Пациент A не может прочитать/отменить запись пациента B (403/404).
- [ ] Чтение карточки создаёт запись в журнале.

ADR: `0011-auth-jwt-refresh-rotation.md`, `0012-personal-data-handling.md`.

---

## Фаза 8. Наблюдаемость (1 неделя)

### Задачи
- [ ] OpenTelemetry: ASP.NET Core, HttpClient, EF Core/Npgsql, `OpenTelemetry.Instrumentation.ConfluentKafka` (или ручная пропагация `traceparent` в заголовках).
- [ ] Сквозной трейс: HTTP-запрос → транзакция → outbox → relay → Kafka → воркер (связь через `traceparent` из конверта события).
- [ ] Serilog: структурированные логи, `TraceId` в каждой записи, вывод в консоль (JSON) + Loki/Seq.
- [ ] Бизнес-метрики: брони/мин, отмены, no-show, конфликты бронирования, лаг outbox, лаг консьюмеров.
- [ ] docker-compose: OTel Collector, Prometheus, Grafana, Tempo (или Jaeger), Loki.
- [ ] Grafana-дашборды как код (provisioning): API (RED), Outbox/Kafka, бизнес.
- [ ] 2–3 алерта: лаг outbox > 1 мин, рост DLT, 5xx > 1 %.

**Готово, когда:** в Grafana виден один трейс от POST до обработки в воркере.

---

## Фаза 9. Нагрузка, полировка, документация (1–2 недели)

### Задачи
- [ ] NBomber-сценарии:
  - поиск слотов (read-heavy);
  - «гонка за слотом» — много пользователей на ограниченный набор слотов;
  - смешанный профиль.
- [ ] Результаты (RPS, p50/p95/p99, ошибки) в `docs/load-testing.md` с выводами и одной-двумя оптимизациями (индексы, проекция слотов, кэш).
- [ ] Redis — **только если** нагрузка показала необходимость (кэш слотов с инвалидацией по событиям) или нужен распределённый rate limiting при нескольких экземплярах API. Иначе убрать из стека.
- [ ] README: что это, архитектурная диаграмма (C4 context + container), state machine, ключевые решения со ссылками на ADR, как запустить, как проверить «без двойной брони» одной командой.
- [ ] `docs/` — диаграммы последовательности: бронирование, outbox, лист ожидания.
- [ ] CI: integration-тесты, сборка Docker-образов, бейджи.
- [ ] Финальный проход по коду: неиспользуемое, TODO, предупреждения.

---

## Фаза 10. Фронтенд (опционально, параллельно с фазами 6–9)

- [ ] Генерация AI-агентом по OpenAPI-спецификации (клиент генерировать из `openapi.json`).
- [ ] Экраны: поиск врача → слоты → запись; мои записи; админка расписаний.
- [ ] Сервис в docker-compose.
- [ ] Честная пометка в README, что фронтенд сгенерирован.

---

## Список ADR

| № | Решение |
|---|---|
| 0001 | Ведение ADR ✅ |
| 0002 | Модель статусов записи ✅ |
| 0003 | Работа со временем через NodaTime ✅ |
| 0004 | Нарушения и бан вычисляются из истории ✅ |
| 0005 | Exclusion constraint против двойной брони |
| 0006 | Свой диспетчер вместо MediatR |
| 0007 | Ключи идемпотентности в Postgres |
| 0008 | Transactional Outbox своими руками (без MassTransit) |
| 0009 | Порядок событий по врачу: партиционирование + единственный relay |
| 0010 | Inbox для идемпотентных консьюмеров |
| 0011 | JWT + ротация refresh-токенов |
| 0012 | Обработка персональных данных |

## Риски

| Риск | Митигация |
|---|---|
| Проект не доводится до конца | Каждая фаза — законченный результат; MVP после фазы 4 |
| Расползание стека | Технология добавляется только с ADR-обоснованием |
| Флаки интеграционные тесты | Общие контейнеры на коллекцию тестов, явные ожидания вместо `Task.Delay`, Respawn для очистки БД |
| Ошибки со временем/DST | NodaTime, `IClock`, тесты на границы |
| Сложность Kafka в Testcontainers на Windows | Docker Desktop с WSL2; CI на Linux |

## Вехи

| Неделя | Результат |
|---|---|
| 1 | Каркас, CI, docker-compose |
| 3 | Домен покрыт тестами |
| 5 | Бронирование без двойной записи, API, идемпотентность |
| 7 | **MVP:** outbox + Kafka |
| 9 | Воркеры |
| 10 | Лист ожидания |
| 11 | Auth и ПДн |
| 12 | Наблюдаемость |
| 14 | Нагрузка, документация — готово к показу |

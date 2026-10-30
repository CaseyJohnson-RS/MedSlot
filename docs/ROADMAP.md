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

## Фаза 1. Домен (1–2 недели) — ✅ готово

Чистый C#, без БД. Максимум unit-тестов.

### Задачи
- [x] Базовые типы: `Entity`, `AggregateRoot` (список доменных событий), строго типизированные ID на Guid v7, `Result` и `DomainError` (без исключений для бизнес-отказов).
- [x] Value objects: `TimeRange`, `LocalTimeRange`, `WorkingHours`, `PhoneNumber`, `Email`.
- [x] `Doctor`: специализация, недельное расписание (`WeeklySchedule`), исключения (`ScheduleOverride` — переименовано из `ScheduleException`: отпуск, больничный, особые часы), список услуг.
- [x] `Service`: название, длительность, кратность шагу сетки.
- [x] `SlotCalculator` (доменный сервис): расписание + исключения + занятые интервалы + длительность услуги → свободные слоты на период. Учитывает DST, включая интервал внутри пропущенного часа.
- [x] `Appointment`: `Book`, `Hold`, `AcceptOffer`, `DeclineOffer`, `ExpireOffer`, `CancelByPatient`, `CancelByClinic`, `Complete`, `MarkNoShow`, `AutoMarkNoShow`; события по domain.md §9.
- [x] `Patient` + `PatientBookingPolicy`: бан из истории нарушений, лимит активных записей, допуск по источнику записи.
- [x] Все правила — параметры `ClinicPolicy`, не константы.
- [x] Текущее время передаётся в домен явно (`Instant now`) — домен детерминирован без `IClock`. `NodaTime.IClock` появится в Application в фазе 2.

### Тесты (`MedSlot.Domain.Tests`, 112 тестов)
- [x] Переходы state machine: все допустимые и недопустимые (в т. ч. из каждого терминального статуса).
- [x] Отмена ровно на границе N часов (±1 секунда), границы lead time и горизонта.
- [x] Слоты: исключения, перерывы, услуга не влезает в конец дня, переход на летнее/зимнее время.
- [x] Бан: 3 нарушения в окне / 2 в окне + 1 вне окна, бан не снимается досрочно, снятие бана админом.

**Итог:** покрытие Domain — 99,8 % строк, 96,4 % ветвлений; архитектурный тест подтверждает, что Domain зависит только от BCL и NodaTime.

---

## Фаза 2. Персистентность и бронирование (1–2 недели) — ✅ готово

### Задачи
- [x] EF Core + Npgsql, конфигурации через `IEntityTypeConfiguration`, snake_case, value converters для ID и NodaTime. Расписание и исключения — в `jsonb` через отдельные DTO.
- [x] Миграция с `btree_gist` и exclusion constraints для врача и пациента (только активные статусы, интервалы `[)`).
- [x] Optimistic concurrency через `xmin` для `Appointment`.
- [x] Маппинг ошибок Postgres в `UnitOfWork`: `23P01` → `SlotAlreadyTaken` / `PatientTimeConflict` (по имени constraint); `DbUpdateConcurrencyException` → `ConcurrentModification`.
- [x] Advisory-блокировки врача и пациента при сохранении новых записей: без них параллельные вставки взаимоблокировались (`40P01`) — см. ADR 0005.
- [x] Порты в Application: репозитории врачей, услуг, пациентов, записей; `IUnitOfWork`.
- [x] Обработчики без MediatR (ADR 0006): `BookAppointment`, `CancelAppointmentByPatient`, `CancelAppointmentByClinic`, `CompleteAppointment`, `MarkAppointmentNoShow`; `GetAvailableSlots`, `GetDoctorSchedule`, `GetPatientAppointments`.
- [x] Логирующий декоратор (без содержимого команд — там ПДн). Транзакционный не нужен: одна команда — один `SaveChanges`. Валидация входа — в фазе 3 на уровне API.
- [x] Демо-данные для разработки: 3 врача, 2 услуги, 2 пациента с фиксированными id; миграции и демо-данные применяются при старте в Development.
- [x] `/health/ready` проверяет подключение к Postgres.

### Тесты (`MedSlot.Integration.Tests`, Testcontainers Postgres, 20 тестов)
- [x] **Главный тест:** 50 параллельных `BookAppointment` на один слот → ровно 1 успех, 49 `SlotAlreadyTaken`. 20 прогонов подряд зелёные; без constraint тест падает.
- [x] Пересекающиеся, но не совпадающие интервалы (30-мин и 15-мин услуги); запись встык допустима.
- [x] Пациент не может быть у двух врачей одновременно.
- [x] Отменённая запись не блокирует слот.
- [x] Параллельные изменения одной записи → второе сохранение получает `ConcurrentModification`.
- [x] Три поздние отмены → бан онлайн-записи; администратор записать может.
- [x] Миграции применяются на чистую БД; exclusion constraints существуют; расписание в `jsonb` переживает сохранение и загрузку.

ADR: [0005](adr/0005-exclusion-constraint-for-double-booking.md), [0006](adr/0006-own-dispatcher-instead-of-mediatr.md).

---

## Фаза 3. API, валидация, идемпотентность (1 неделя) — ✅ готово

### Задачи
- [x] Эндпоинты Minimal API, сгруппированные по ресурсам (`MapGroup`), версия `/api/v1`: слоты и расписание врача, запись, получение, отмены, завершение, неявка, записи пациента.
- [x] Контракты API на типах BCL (`Guid`, `DateTimeOffset`, `DateOnly`, `TimeOnly`), перечисления — строками.
- [x] FluentValidation — фильтр эндпоинтов; единый формат ошибок `ProblemDetails` (RFC 9457) с кодом доменной ошибки в поле `code`; статус по коду (404 / 403 / 409 / 422 / 429).
- [x] **Idempotency-Key** для всех изменяющих POST (ADR 0007):
  - таблица `idempotency_keys`, PK `(scope, key)`; `scope` — пользователь, до фазы 7 `anonymous`;
  - ключ сохраняется **тем же `SaveChanges`**, что и операция;
  - повтор с тем же ключом и телом → сохранённый ответ + `Idempotent-Replayed: true`;
  - тот же ключ, другое тело → 422; первый запрос ещё выполняется → 409;
  - отказы не сохраняются; ключи старше 24 ч удаляет фоновая служба.
- [x] Rate limiting на запись: fixed window на клиента (до фазы 7 — по IP), 429 с `Retry-After`.
- [x] OpenAPI: коды ответов эндпоинтов, заголовок `Idempotency-Key`, описания.

### Тесты (13 HTTP-тестов)
- [x] Сценарий «слоты → запись → просмотр → отмена» через HTTP.
- [x] Повтор POST с тем же ключом → тот же ответ, одна запись в БД.
- [x] 10 параллельных запросов с одним ключом → одна запись, остальные — сохранённый ответ или 409.
- [x] Тот же ключ с другим телом → 422; неуспешный запрос не сохраняется; ключ истекает через 24 ч.
- [x] Валидация → 400 со списком полей; доменные ошибки → 404 / 409 / 422 с кодом.
- [x] Rate limiting → 429; OpenAPI описывает заголовок и коды ответов.

**Итог:** сценарий проверен на стеке docker-compose (демо-данные, Scalar на `localhost:8080/scalar`).
ADR: [0007](adr/0007-idempotency-keys-in-postgres.md).

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
| 0005 | Exclusion constraint против двойной брони ✅ |
| 0006 | Свой диспетчер вместо MediatR ✅ |
| 0007 | Ключи идемпотентности в Postgres ✅ |
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

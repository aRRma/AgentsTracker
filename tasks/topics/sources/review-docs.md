# Ревью документации: журнал и лог (коммиты [log], 1708133..HEAD)

Метод: diff docs/README/CLAUDE.md сверен с кодом в `src` (ветка `topics`, HEAD 8dc4575).
Не проверял: поведение `Container.Detected` в самом контейнере, ACL папки `logs\`, работу
`Get-Content -Encoding utf8` вживую, формат токенов (`.Tokens`) в `run.end`, точное число символов
в `/audit` (`LineBudget = 160`, `AuditCommandHandler.cs:14` — только прочитал константу).

## Подтверждено кодом (без замечаний)

- Путь и имя `logs\gateway-ГГГГ-ММ-ДД.log`, UTF-8 без BOM, файл на день по местному времени, flush на строку, глотание сбоя записи, срок 14 дней по дате в имени, чистка при первой записи и при смене дня: `LogFile.cs:13-17, 24-52, 85-98`. В контейнере `Gateway__DataDirectory=/data`, том `data:/data` (`Dockerfile:52`, `docker-compose.yml:16`).
- Один приёмник на кольцо и файл, маска `Text.ClipRedacted(..., 16 000)`, пол Information: `RingBufferLog.cs:46, 56, 60-71`. Кольцо 500 записей: `RingBufferLog.cs:11`. `/api/log?count=&level=`: `MonitorModule.cs:102-103` (count 1..500, по умолчанию 200, level по умолчанию Information) — совпадает с `monitor.md`.
- Консоль не маскируется, маска в месте записи только в трёх вызовах: `ClaudeBackend.cs:516, 524`, `ClaudeLimits.cs:243`.
- `run.start`: вид (вопрос/контекст/сжатие/беседа), модель, режим, effort, ветка, `замок N` при ожидании >= 1 с: `ChatWorker.cs:411-424`.
- `run.end`: время, ходы, токены вх/исх, ответившая модель, причина (`/stop`, `таймаут`, начало текста ошибки 80 знаков): `ChatWorker.cs:524-536`. Исходы `ok/error/rate-limit/cancel` (`:515-521`) плюс `interrupted` из `StartupNotice.cs:90-91`. «Отмена без /stop — это таймаут»: `ClaudeBackend.cs:46, 133-136` и `ChatWorker.cs:531`, других источников отмены `runCts` нет (`:72`, `:158-160` — только `/stop`).
- Мягкая остановка: `stopped = result.Cancelled && stoppingToken.IsCancellationRequested`, `EndRun` пропущен, `return` до `run.end`: `ChatWorker.cs:464-486`. Следующий старт пишет `run.end interrupted`: `StartupNotice.cs:19-20, 90-91`.
- `gateway` «старт» (версии шлюза и CLI, `pid:N`, `container`): `ChatGatewayService.cs:47-52`. `внутренняя ошибка: <тип>: <сообщение>` (error): `ChatWorker.cs:211`. `limit.refused` — модель и первый абзац (до `\n\n`): `ChatWorker.cs:333-336`; первый абзац отказа содержит окно и сброс (`ClaudeLimits.cs:86-88`).
- `delivery`: `partial`/`lost` — `ChatWorker.cs:567-571`; `unreachable` — `:202-203`; `run.end` пишется до отправки — `:536, 557-560`.
- Паритет en/ru в изменённых местах: все 14 пар (architecture, deployment, glossary-строка, monitor, operations, safety) говорят одно и то же. README только русский, CLAUDE.md только английский — соблюдено.

## Находки

### 1. [исправить] Глоссарий: статья «журнал / лог» не знает про файл
`docs/glossary.md:108-110`, `docs/en/glossary.md:111-113` — Лог описан как «раздел «Лог», `ILogger`». План (`plan-observability.md`, таблица «Документация») обещал «у «лог» — где лежит файл». Не сделано.

ru (`docs/glossary.md:109-110`):
```
`audit\audit-<YYYY-MM>.jsonl`. Лог — то, что нужно для разбора поломки: раздел «Лог», `ILogger`,
файл `logs\gateway-<ГГГГ-ММ-ДД>.log`. Механизм сохраняет английское имя (`audit`), а текст говорит «журнал».
```
en (`docs/en/glossary.md:112-113`):
```
`audit\audit-<YYYY-MM>.jsonl`. The лог is for debugging — the monitor's «Лог», `ILogger`, the file
`logs\gateway-<YYYY-MM-DD>.log`. The mechanism keeps its English name (`audit`), the text says «журнал».
```

### 2. [исправить] «таймаут» в журнале противоречит глоссарию
Код пишет `", таймаут"` (`ChatWorker.cs:531`), документы повторяют это слово (`docs/architecture.md:134`, `docs/en/architecture.md:222-223`). Глоссарий (`docs/glossary.md:104-106`, en `:107-109`): таймаут запуска — это «предел» («Превышен предел в 45 мин»), «лимит» — только подписка. `/audit` читает человек, значит слово из журнала — текст для человека.
Решение (рекомендую): в коде `", таймаут"` -> `", предел"`; в документах:
- ru: «у `cancel` — `/stop` или `предел` (время запуска вышло)». Фразу «Таймаут живёт в бэкенде…» заменить на «Предел времени запуска проверяет бэкенд и токен запуска не трогает, поэтому отмена без `/stop` — это он.»
- en: «for `cancel` `/stop` or `предел` (the run time ran out)». «The run time limit is checked in the backend and does not touch the run's token, so a cancellation without `/stop` is it.»
Если слово в коде оставить, добавить в глоссарий исключение для журнала; хуже.

### 3. [исправить] «замок» нет в глоссарии
Журнал печатает `замок N` (`ChatWorker.cs:424`); пользователю в чате то же событие называется «⏳ В проекте идёт другой запуск» (CLAUDE.md, «How a message travels»). Либо заменить в коде и обоих документах на `ждал папку N` (и «если запуск ждал папку дольше секунды»), либо добавить строку в глоссарий. Рекомендую замену: `+ $", ждал папку {lockWait.Elapsed}"`; в `docs/architecture.md:130-131` «`ждал папку N`, если запуск ждал свою папку не меньше секунды»; en `docs/en/architecture.md:218-219` «`ждал папку N` when the run waited for its folder for at least a second». Заодно: код — `>= 1 s`, документ — «дольше секунды» (мелочь, «не меньше секунды» точнее).

### 4. [исправить] Неточность: `run.end` у запуска без расхода
`docs/architecture.md:132-133`, `docs/en/architecture.md:220-221` говорят «токены на входе и выходе и ответившая модель». У `cancel` и ошибки без `usage` токенов нет, пишется только выбранная модель (`ChatWorker.cs:526-528`; сам комментарий в коде). Исправление:
- ru: «`run.end` — время, ходы, токены на входе и выходе и ответившая модель (если запуск не сообщил расход, как `cancel`, — только выбранная модель); у `error` и `rate-limit` — начало текста ошибки, у `cancel` — `/stop` или `предел`.»
- en: «`run.end` — time, turns, tokens in/out and the model that answered (when the run reported no usage, as with `cancel`, only the selected model); for `error` and `rate-limit` the start of the error text, for `cancel` `/stop` or `предел`.»

### 5. [исправить] safety.md: фраза про консоль неверна и слишком техническая
`docs/safety.md:126-129`, `docs/en/safety.md:127-130`: «В консоли маскируется только вывод агента: текст сетевой ошибки там может остаться как есть». Код: маска в консоли стоит ровно в трёх местах — итог и ошибка запуска CLI (`ClaudeBackend.cs:516, 524`) и тело ответа эндпоинта лимитов (`ClaudeLimits.cs:243`). Тело лимитов — не «вывод агента»; всё остальное (исключения, тексты каналов) в консоль идёт как есть. Читатель — человек без программирования, ему нужен вывод, а не перечень.
- ru: «токены и пароли из команд попадают в журнал, файл лога и монитор как `***`. Шлюз узнаёт токены известного вида и значения ключей вроде `token`, `password`. В консоли и в `docker compose logs` маски почти нет: не показывайте их посторонним;»
- en: «tokens and passwords in commands reach the journal, the log file and the monitor as `***`. The gateway recognizes known token shapes and the values of keys like `token` and `password`. The console and `docker compose logs` are barely masked — do not show them to others;»

### 6. [исправить] README: `DataDirectory` и подсказка про кракозябры
- `README.md:258`: «где хранить состояние и журнал» -> «где хранить состояние, журнал и лог».
- `README.md:330`: читатель не знает, где «папка данных». Исправить: «консоль в cp866; откройте файл лога `%LOCALAPPDATA%\AgentsTracker\logs\gateway-ГГГГ-ММ-ДД.log` — он в UTF-8 (в контейнере — в томе `data`)». Проверено: путь по умолчанию — `docs/architecture.md:328` (`AppPaths.DataDirectory`).

### 7. [улучшить] Не описано: падение процесса попадает в лог
`GatewayInfrastructure.cs:96-99` пишет `LogCritical` при необработанном исключении — основная причина, зачем нужен файл у задачи Планировщика. В документах (deployment.md, architecture.md) этого нет. Добавить в `docs/deployment.md` после абзаца про чтение лога, и в en:
- ru: «Если шлюз упал мимо всех ловушек, причина — последняя запись `CRT` в файле лога.»
- en: «If the gateway crashed past every handler, the cause is the last `CRT` entry in the log file.»

### 8. [улучшить] Формат строки файла не указан
Для `grep`/`Get-Content` нужен формат: `2026-11-03 12:37:34.276 +03:00 WRN Категория: текст`, продолжение (исключение, многострочный текст) — с отступом в 4 пробела (`LogFile.cs:57-71, 15`). Одна строка в `docs/operations.md:60-62` и `docs/en/operations.md:61-63` после «UTF-8»:
- ru: «Запись начинается с даты (`ГГГГ-ММ-ДД ЧЧ:ММ:СС.ммм +03:00 WRN …`), продолжение — с отступа в 4 пробела.»
- en: «An entry starts with the date (`YYYY-MM-DD HH:MM:SS.mmm +03:00 WRN …`), a continuation is indented by 4 spaces.»

### 9. [мелочь] «Information и выше» без оговорки
Приёмник пропускает от Information (`RingBufferLog.cs:56`), но категории режутся `appsettings.json:3-9` (`Microsoft.AspNetCore`, `System.Net.Http.HttpClient`, `Polly` — Warning). В `deployment.md:226` (таблица) оговорка не нужна читателю; в `monitor.md:54` и `architecture.md:367` не критично. Оставить как есть; упомянуто, чтобы не считать это ошибкой при следующем ревью.

### 10. [глоссарий] Строка «доставка»
Нужна: у понятия нет соседа, а в журнале есть вид `delivery`. Оформление совпадает с соседними (левый столбец — понятие, правый — слово и запрещённые синонимы), обе версии согласованы. Замечание: запрещённые синонимы «отправка, получение» (`docs/glossary.md:51`) конфликтуют с законным использованием — в тех же документах «отправка файла» (`file.send`), «получение» (`file.receive`), «Не удалось отправить часть ответа» (лог). Сузить:
- ru: `| Ответ дошёл до чата (или нет — «не дошло») | **доставка** | доставленность |` — либо оставить синонимы, но добавить оговорку «про итог, а не про действие». Рекомендую: `| Итог отправки ответа в чат (дошёл или «не дошло») | **доставка** | — |`.
- en: `| The outcome of sending an answer to the chat (reached it, or «не дошло») | **доставка** | — |`
Термины в изменённых текстах: «лог/журнал», «запуск», «проект», «тема», «лента» — в порядке. Замечания только «таймаут» (п. 2) и «замок» (п. 3). «Полоса» (`docs/architecture.md:139`, «исключение в полосе») — проверил только `grep` по глоссарию: слова там нет; в `docs/architecture.md` раздел «Lanes»/«полосы» используется ранее — оставить.

### 11. [лишнее] Что сократить
Читатель `architecture.md` — разработчик, но рассуждения «почему» уже есть в комментариях кода (`LogFile.cs:5-9, 31, 48, 50-53`; `RingBufferLog.cs:38-42`). Предлагаю сократить абзац про маску лога и пункт про soft stop:

`docs/architecture.md:366-372` ->
```
Лог маскируется один раз, в приёмнике: `RingBufferLoggerProvider` собирает замаскированную запись
`LogEntry` и отдаёт её и кольцу монитора, и `LogFile` (`logs\gateway-ГГГГ-ММ-ДД.log`, Information и
выше, 14 дней по дате в имени). Файл пишется синхронно, со сбросом на каждую строку, сбой записи
глотается (причины — в комментарии `LogFile`). Консоль не маскируется, поэтому вывод CLI и тело
ошибки эндпоинта лимитов маскируются ещё и в месте записи.
```
`docs/en/architecture.md:373-381` ->
```
The log is masked once, at the sink: `RingBufferLoggerProvider` builds one masked `LogEntry` and hands
it to both the monitor's ring and `LogFile` (`logs\gateway-<YYYY-MM-DD>.log`, Information and above,
14 days by the date in the name). The file is written synchronously with a flush per line and a write
failure is swallowed (the reasons are in the `LogFile` comment). The console is not masked, so CLI
output and the limits endpoint's error body are masked at their call site as well.
```
Пункт soft stop (ru `docs/architecture.md:136-138`, en `:225-227`) ->
```
- Мягкая остановка шлюза (`docker stop`, Ctrl+C) посреди запуска не пишет `run.end` и оставляет запуск
  в `state.json`: следующий старт скажет «прервано» и запишет `run.end interrupted`.
```
```
- A soft gateway stop (`docker stop`, Ctrl+C) in the middle of a run writes no `run.end` and leaves the
  run in `state.json`: the next start says «прервано» and writes `run.end interrupted`.
```
`delivery` (ru `:142-144`, en `:231-234`): убрать «Отдельный вид, а не исход `run.end`…» — это в комментарии `AuditKinds.Delivery`; оставить одну фразу «`run.end` пишется до отправки, поэтому ответ, что не дошёл, — отдельная запись.»
`deployment.md` дублирует предложение про маску (`:170`, en `:261`): «консоль не маскируется, файл и монитор — маскируются» — это уже в safety. Заменить хвост на «В контейнере файл лежит на томе `data`, а `docker compose logs -f` работает как раньше.» / «In the container the file is on the `data` volume, and `docker compose logs -f` still works.»
README: изменения короткие, лишнего нет. CLAUDE.md: изменения по делу; единственное — переносы строк в абзаце `Sessions, state, audit` (строки 196-198) рваные («config layers end with the `Gateway__*`» отдельной строкой) — косметика, переверстать.

### 12. [устаревшее во всей документации]
`grep` по docs/README/CLAUDE.md (без release-notes): фраз «файла лога нет», «лог только в мониторе», старого формата `run.end`, «лимит тарифа» как summary не осталось. Остатки, которые стоит поправить:
- `docs/architecture.md:28` / `docs/en/architecture.md:27`: «Key — для state.json, журнала и лога» — верно, не трогать.
- `docs/architecture.md:362` (en `:369`) «`Text.Preview` тоже маскирует (лог, монитор, статус запуска)» — «лог» теперь маскирует приёмник, а `Preview` в лог не вызывается для общих записей. Заменить «(лог, монитор, статус запуска)» на «(журнал, монитор, статус запуска)»; en «(journal, monitor, run status)». Не проверял, есть ли вызов `Preview` в логировании — формулировка «журнал» верна точно: `AuditEvent`.
- `docs/cli-contract.md:15` (en `:15`): «payload логируется только на Debug» — верно: Debug в файл не идёт (`RingBufferLog.cs:56`). Можно не трогать.
- `README.md:93`, `docs/operations.md:43` — не затронуты.

## Рабочие документы

### `tasks/topics/plan-observability.md` расходится с кодом
- Строка про `cancel`: «Если отменён `stoppingToken` — `остановка шлюза`, иначе — `/stop`» — в коде такой причины нет, остановка шлюза возвращается до `run.end` (`ChatWorker.cs:486`); причины только `/stop` и `таймаут`. Удалить фразу или пометить «заменено этапом 3 пункт 3 (не пишем)».
- «`AppDomain.UnhandledException` в `Program.cs`» — в коде `GatewayInfrastructure.cs:96-99`.
- Текст `delivery`: план «ответ: не дошло 2 из 5 частей», код «ответ: частей не дошло {failed} из {total}» (`ChatWorker.cs:568`).
- «`delivery unreachable`: чат недоступен, снято из очереди: N» — код: «чат недоступен, задача брошена, снято из очереди: N» (`:202`).
- `замок N с` (план) — в коде `замок {lockWait.Elapsed}` (формат `DisplayFormat.Elapsed`, не «N с»).
- Строка таблицы «Документация»: `glossary.md` «у «лог» — где лежит файл» — не выполнена (п. 1); `safety.md:127` «консоль и docker logs — без маски» выполнено в более слабой, неточной форме (п. 5).
- Оценка «около 60 строк» для `LogFile.cs` — по факту 100; не важно.
### `tasks/topics/README.md`
Совпадает с историей коммитов (`6a9f30a`, `75dcfec`, `28b9b30`); «132 проверки» и «Пробный экземпляр 03.11» не проверял (харнес и экземпляр не запускал). Пункт «Ревью и заметки к выпуску» не отмечен — верно: заметок `docs/release-notes/<tag>.md` в diff нет.

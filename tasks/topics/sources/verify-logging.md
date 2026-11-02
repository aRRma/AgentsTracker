# Проверка плана журнала и лога: разделы «Зачем», «Решения», этапы 1–2, «Документация», «Риски»

Код на `27cd5f7`. Только чтение. Формат: утверждение → вердикт → доказательство.

## Номера строк

| Утверждение | Вердикт | Доказательство |
| --- | --- | --- |
| `RingBufferLog.cs:55-60` отдаёт без маски | ВЕРНО | `:55 buffer.Add(new LogEntry(`, `:59 formatter(state, exception)`, `:60 exception?.ToString()` |
| `/api/log` отдаёт кольцо как есть | ВЕРНО | `MonitorModule.cs:102-103`: `Results.Json(log.Tail(...))`, `Tail` фильтрует только по уровню. Маски по пути нет. Единственная `Text.Redact` в `MonitorModule.cs:178` — правила «Всегда» |
| «Дыра есть и в `master`» | ВЕРНО | `git show master:.../RingBufferLog.cs` — тот же `formatter(state, exception)` |
| `ClaudeBackend.cs:515, 523` без маски | ВЕРНО | `:515` и `:523` — `Truncate(...)`; `Truncate` (`:631`) = `value[..max] + "…"`, ничего не маскирует. Предел `ErrorDetailsLimit = 200` (`:23`) |
| `run.end` в `ChatWorker.cs:492` | ВЕРНО | `:492 Audit(prompt, AuditKinds.RunEnd, $"{result.Duration.Elapsed}, ходов ...` |
| `ChatWorker.cs:194-200`, `:202-206`, `:444`, `:896`, `:658`, `:840-845`, `:90`, `:124`, `:282`, `:323` | ВЕРНО | сверены построчно, все совпали |
| `GatewayInfrastructure.cs:43` | ВЕРНО | `:43 services.AddSingleton<ILoggerProvider, RingBufferLoggerProvider>();` |
| `Program.cs:86` | ВЕРНО | `:86 return Environment.ExitCode;` |
| `ChatGatewayService.cs:38` ставит `ExitCode = 1` | НЕТОЧНО | строка 39; файл в `Infrastructure/Chat/`. Других `Environment.ExitCode =` в `src` нет (grep) — «только там» ВЕРНО |
| `ChatGatewayService.cs:79-85` (этап 5) | НЕТОЧНО | ветка Unauthorized — `:76-89`, `LogCritical` — `:81`; не мой этап |
| `SettingsAudit.cs:10-13` | ВЕРНО | `Changed(...)` с `user, scope.Chat, ...` |
| `deployment.md:235` (EN) | ВЕРНО | «There is no log file.» на `:235`; RU — `:232`. `deployment.md:113` — абзац про код выхода 1, ВЕРНО |
| `Text.cs:79, 88` | ВЕРНО | шаблоны `SecretFlag` (`:78-80`) и `SecretAssignment` (`:87-89`); `.` без Singleline — маска до конца строки |
| `safety.md:127`, `monitor.md:55`, `README.md:330` | ВЕРНО | `:127` «tokens and passwords ... reach the journal, the log and the monitor as `***`» (сейчас неверно для лога); `monitor.md:55` «RingBufferLog (500 entries...)»; `README.md:330` «В логе кракозябры» |

## Маска в точках вызова — 96 мест

ВЕРНО. `grep -E "Log(Information|Warning|Error|Critical)\("` по `src/*.cs` без `obj` — 96. Но мимо плана:
- `ClaudeLimits.cs:241` — тело ответа эндпоинта лимитов, `Truncate(body)` (500 знаков) без маски. Закроет приёмник, но в консоль/`docker logs` пойдёт как есть. Не названо в плане.
- Приёмник заодно маскирует чужие категории (`Microsoft.*`, Kestrel) — плюс, в плане не сказан.

## Этап 1

1.1. `LogLine.Format` «общий для кольца и файла» — НЕТОЧНО. Кольцо хранит структуру `LogEntry` (JSON `/api/log`), формат строки ему не нужен; нужна только маска. Проще: один раз собрать замаскированный `LogEntry`, отдать и в кольцо, и в файл; файл форматирует из него. Или один провайдер на кольцо и файл — одна маска, один `IsEnabled`.
1.1. `Text.Redact(Text.Clip(…, 16 КБ))` — НЕТОЧНО по порядку. Такой порядок обрезает токен до маски (сам же `Text.cs:26-30` пишет «маска раньше обрезки»). Готово: `Text.ClipRedacted(text, 16*1024)` (`Text.cs:30`; внутри грубая обрезка до `limit*4`, маска, итоговая обрезка). Писать заново не нужно.
1.3. Уровень: `Logging:LogLevel:Default = Information` (`appsettings.json`), `RingLogger.IsEnabled` уже режет Debug (`RingBufferLog.cs:~50`). «Debug не пишется никогда» — ВЕРНО, но отдельный код не нужен, если взять тот же `IsEnabled`.
1.3. Образец `JsonlAuditLog`: открывает файл на каждую запись (`FileMode.Append`, `FileShare.Read`), `lock`, `Flush`, `Directory.CreateDirectory` каждый раз (`JsonlAuditLog.cs:29-39`). Годится для логики записи, НЕ годится буквально в обработке сбоя: `:43` зовёт `logger.LogError` — у файлового провайдера это рекурсия. План это знает (пункт про `Console.Error`). Открытие на запись без постоянного хэндла — плюс: файл можно читать (`Get-Content`) и удалять.
1.3. Два шлюза в одной папке (старый ещё гаснет, новый стартует) — `FileShare.Read` даст IOException у второго; сбой тихий по плану, терпимо.
1.3. Папка и ACL: `AppPaths.DataDirectory` (`AppPaths.cs:~25`) создаёт папку и берёт `Gateway__DataDirectory`; `DataDirectoryAcl.Restrict` зовётся в `ValidateStartup` (`GatewayInfrastructure.cs:138`) — после создания провайдеров. Подпапка `logs\`, созданная до `Restrict`, наследует права: `SetAccessControl` с `ContainerInherit|ObjectInherit` переносит их на потомков (поведение Windows, в коде не проверял). Файлы до `Restrict` недолго с правами по умолчанию — риск 4 верно просит проверить.
1.3. Контейнер: `Gateway__DataDirectory=/data` (`Dockerfile`), `/data` — том, принадлежит `app`. `logs\` ляжет в том `data` — менять `Dockerfile` не нужно. Но 14 суток × до 20 МБ растут в томе; в `docker-compose.yml` комментарий тома «сессии, правила «Всегда», аудит» устареет (в плане не назван).
1.4. `Logging:Console:FormatterOptions` — ВЕРНО по назначению (форматтер `simple` по умолчанию), но польза только для `docker logs` (там есть `--timestamps`). У релиза консоли нет.
1.5. `Truncate → Text.ClipRedacted` — ВЕРНО и нужно: `Text` лежит в `Agents.Abstractions` (`namespace AgentsTracker.Agents`), `ClaudeBackend` её уже использует в соседних файлах. Строк три, а не две: ещё `:547` (`Truncate(stderr.Trim(), 3000)` идёт в текст ответа в чат, не в лог — не трогать) и `ClaudeLimits.cs:241`.
Оценка «около 140 строк» — НЕ ПРОВЕРИТЬ ЧТЕНИЕМ. Но 100 строк провайдера — завышено, если без `Console.Error`/«пропущено N»/предела.

## Этап 2

2.2 `ListenAsync` не бросает — пункт БЕСПОЛЕЗЕН. `TelegramChannel.cs:118-150`: `Bot.ReceiveAsync(handler, errorHandler, ...)`; обработчик ошибок (`:130-145`) только считает паузу, пишет лог и `Task.Delay(delay, token)` — бросить может лишь `OperationCanceledException` при `/stop`. `HandleUpdateAsync` ловит всё, кроме отмены (`:202-205`). Ошибки сети, 409, 5xx, 401 уходят в бесконечный цикл с паузой до минуты (комментарий `:131-136`). Исходники `Telegram.Bot 22.10.3` не открывал (только dll в кэше) — поведение внутри `ReceiveAsync` «не проверял», но каждый путь из кода шлюза не бросает. Итого: код 1 никогда не поставится, а реальная дыра — отозванный токен (401 в polling) зацикливается вечно, шлюз «жив», чат мёртв. Если нужен выход — это порог подряд идущих 401 в обработчике ошибок (`backoff.Streak`), а не `catch` вокруг `ListenAsync`.
2.2 Любой иной сбой `ExecuteAsync`: хост по умолчанию `StopHost` и сам пишет `LogError` с исключением (MS Learn: «hosting-exception-handling», .NET 6+). Код выхода при этом 0 (MS Learn «windows-service»: «stops cleanly… won't restart»; в .NET 11 станет ненулевым) — утверждение плана про «код выхода 0» ВЕРНО по документации, в запуске не проверял. Дешёвый выход без `catch`: `HostOptions`/`Environment.ExitCode = 1` не нужен — достаточно `try/catch` вокруг всего `ExecuteAsync`, но это те же 5 строк и тот же редкий случай.
2.1 `AppDomain.UnhandledException` — разумно: падение процесса оставляет след. На Windows `Stop-Process -Force` его не вызывает (план это знает). `UnobservedTaskException` — срабатывает только при сборке мусора и может не сработать вовсе; в `src` всего 2 места `_ =`/`Task.Run`. Выкинуть.
Сообщения от `LogCritical` в `UnhandledException` — файловый провайдер синхронный, успеет. ВЕРНО.

## «Зачем» и «Что изменилось»

- П.1 «после перезапуска не остаётся ничего» — ВЕРНО (кольцо в памяти, консоли нет).
- П.2 — ВЕРНО. П.3 «cancel одинаков у /stop и таймаута» — `outcome` по `Cancelled` (`ChatWorker.cs:484-489`), раздельной причины в `run.end` нет, ВЕРНО.
- «`finally` снимает запуск` до `DeleteQuietlyAsync`; фильтр `:896` пропускает отмену» — ВЕРНО по коду (`:441-447`, `:896`); что `run.end` при этом не пишется — следует из порядка (аудит `:492` после `finally`), в харнесе не запускал. Этап 3, не мой.
- «`KnownVersions` не трогаем… `plan-rework.md:75`» — ВЕРНО (`:75`).

## Документация: что пропущено

- `docs/en/architecture.md:370` и RU-зеркало: «`Text.Preview` masks too (log, monitor, run status)» — уже утверждает, что лог маскируется; править вместе с разделом «Audit». Строка `:336` (RU `:333` примерно) — перечень папки данных (`inbox\`, `audit\`): сюда `logs\`. План пишет «Audit и папка данных» — ВЕРНО, но без строк.
- `docs/en/operations.md:61` и RU `:60`: «The console outputs Russian text in cp866 — view the log in PowerShell» — после файла лога устареет; в плане только «лог пробного экземпляра». И `README.md:330`: тут нужно не просто «лог в файле», а переписать причину кракозябр (файл UTF-8).
- `docs/en/glossary.md:49` («лог — the technical log», синоним «журнал»), `:111` («The лог is for debugging — the monitor's «Лог»»): добавить файл; RU `docs/glossary.md:108`.
- `docs/deployment.md` RU `:232-233`, EN `:235-237` ВЕРНО; строка таблицы папки данных — после `:228` (EN). Фраза «Back up state.json and audit\» (EN `:232`) — ВЕРНО.
- `docker-compose.yml` комментарий тома (см. выше); `scripts/monitor-api.ps1:13` пример `api/log` — менять не надо.
- `safety.md:127` + RU: правка нужна не только для лога, но и для экрана «монитор без пароля»: не нашёл других упоминаний.
- `CLAUDE.md:200` — ВЕРНО (правило `Text.Redact`).
- Таблица говорит «Этап 1» для `operations.md`, `monitor.md`, `safety.md`, `README`, `CLAUDE.md` — ВЕРНО, и всё русское зеркало должно идти в тот же коммит (правило CLAUDE.md «Как работать»), план это сказал.

## Риски

1. ВЕРНО. 3. Не мой этап. 5. ВЕРНО: `RestartOnFailure` Interval PT1M, Count 3 (`WindowsScheduledTaskInstaller.cs:100-103`), реакцию на код 1 после падения не проверяли. Добавить риск: цикл 401 в polling (выше).
Не названо: ротация по дате в имени + «местное время» в контейнере (UTC), часы переводятся — ничего страшного.

## Упростить (этапы 1–2)

1. Пункт 2.2 целиком — выкинуть (не срабатывает). Если нужен выход при отозванном токене — порог подряд идущих 401 в `ListenAsync`-обработчике, отдельная задача.
2. `UnobservedTaskException` — выкинуть; оставить `AppDomain.UnhandledException` (3 строки).
3. Предел 20 МБ/сутки и строка «предел размера» — выкинуть: Information+ личного бота — килобайты; защита от цикла логов стоит кода и теста. Если нужна страховка — одна проверка `FileInfo.Length > 50 МБ` → тишина, без строки.
4. «Пропущено N записей» и `Console.Error` — выкинуть: сбой записи молча глотать (как делает журнал по смыслу), максимум один `Console.Error` на процесс. Счётчик с серией — лишнее состояние.
5. Свой `LogLine.Format` — не нужен: общий `LogEntry` + форматирование строки в провайдере; маска — `Text.ClipRedacted`.
6. Пункт 1.4 (формат консоли) — выкинуть из этапа, это настройка для `docker logs`; либо только `TimestampFormat`.
7. Чистка «при смене дня» — достаточно при старте и при открытии нового файла (первая запись за день).
8. Использовать `RingBufferLoggerProvider` как единственный провайдер: добавить в `RingLogger.Log` запись в файл — одна регистрация, одна маска.

## Пропущено

- `ClaudeLimits.cs:241` — немаскированное тело (см. выше).
- Цикл 401/409 в polling без выхода (реальный сценарий «шлюз жив, бот мёртв»).
- Тест маски: харнес должен ловить именно откат (в кольце и в файле, на токене в тексте исключения).
- Не проверял: поведение ACL на `logs\` в Windows, исходники `Telegram.Bot.ReceiveAsync`, код выхода хоста .NET 10 при падении `BackgroundService` (только по MS Learn).

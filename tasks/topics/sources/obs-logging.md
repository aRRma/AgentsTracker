# Лог шлюза: B1, B2, B3, B8 — сверка аудита 28.10 с кодом на 03.11.2026

Ветка `topics`, коммит `8926a0f`. Только чтение кода; ничего не собирал и не запускал. Пути ниже — от `src/AgentsTracker.Gateway/`, если не сказано иное.

## 1. Как сейчас устроен лог (проверено)

- Провайдеры: встроенные из `WebApplication.CreateBuilder` (консоль, на Windows — ещё EventLog; это поведение .NET по документации, в этом репозитории не проверял) плюс один свой `RingBufferLoggerProvider` — `Infrastructure/GatewayInfrastructure.cs:42-43`. Других `ILoggerProvider`, `AddConsole`, `AddSimpleConsole`, `AddFile` в `src/` нет (Grep по `AddConsole|AddProvider|Logging` дал только `appsettings*.json` и эти две строки).
- Кольцо: 500 записей (`Monitoring/RingBufferLog.cs:11`), `IsEnabled` — Information и выше (`:48`), время `DateTimeOffset.UtcNow` (`:56`), сообщение и исключение кладутся как есть, без маски (`:59-60`).
- `/api/log` отдаёт кольцо как есть: `Features/Monitor/MonitorModule.cs:102-103` (`count` 1..500, `level`). Маски нет.
- `appsettings.json:2-10`: только уровни (`Default` Information, `Polly` Warning, `System.Net.Http.HttpClient` Warning, `Microsoft.AspNetCore` Warning). Секции `Logging:Console` нет — формат консоли стандартный. `appsettings.Development.json` — то же без Polly.
- Выпуск запускает Планировщик: `Infrastructure/Autostart/WindowsScheduledTaskInstaller.cs:105-110` — `<Exec>` с `<Command>` и `<WorkingDirectory>`, без `Arguments`, без перенаправления. Консоли у задачи нет, stdout никуда не пишется. Перезапуск при сбое: `RestartOnFailure` 3 раза по 1 мин (`:100-103`). `RedirectStandardOutput` в `AutostartInstaller.cs:50-51` — это чтение вывода `schtasks`, к логу шлюза не относится.
- Контейнер: `Dockerfile` — `ENTRYPOINT ["/app/AgentsTracker.Gateway"]`, `Gateway__DataDirectory=/data` (том `data` в `docker-compose.yml`, строка `- data:/data`). stdout контейнера = `docker logs`.
- Папка данных: `AppPaths.DataDirectory` (`Infrastructure/AppPaths.cs`, `LocalSettings` — `:34`). `DataDirectoryAcl.Restrict` (`Infrastructure/Security/DataDirectoryAcl.cs:14-39`) ставит наследуемые права `ContainerInherit | ObjectInherit` на папку данных. Вызывается из `ValidateStartup` (`GatewayInfrastructure.cs:138`), то есть после первых строк лога.
- Образец записи в файл — `Infrastructure/Audit/JsonlAuditLog.cs:25-46`: `lock`, `Directory.CreateDirectory`, `FileMode.Append`, `FileShare.Read`, `StreamWriter` UTF-8 без BOM, `Flush`, `try/catch` с записью в `ILogger` (для лога так делать нельзя: рекурсия).

## 2. Сверка утверждений аудита (разделы 2, 3, 4, п. 8 раздела 5)

| № | Утверждение аудита | Статус | Подтверждение |
|---|---|---|---|
| 2.1 | Лог только в консоли и в 500 строках памяти | Верно | см. раздел 1; файлового провайдера нет |
| 2.2 | Выпуск идёт из Планировщика, консоль пропадает | Верно | `WindowsScheduledTaskInstaller.cs:105-110` |
| 2.3 | Потеря части ответа: `ChatWorker.cs:739` и `:772` | Номера устарели | теперь `Features/Chat/ChatWorker.cs:844` («Не удалось отправить часть ответа в чат») и `:877` («Не удалось отправить сообщение»); ещё `:591` и `:200` (тема недоступна, Warning) |
| 2.4 | «Внутренняя ошибка шлюза» `ChatWorker.cs:184` | Номер устарел | теперь `ChatWorker.cs:204` («Ошибка при обработке сообщения») |
| 2.5 | `ClaudeBackend.cs:513` «claude завершился с кодом N» | Номер устарел | теперь `ClaudeBackend.cs:523`; Warning «Запуск завершился ошибкой» — `:554` |
| 2.6 | `ClaudeBackend.cs:75` — аргументы CLI в лог | Верно | `ClaudeBackend.cs:75-76` |
| 2.7 | Polly/канал/polling только в логе | Верно, номер устарел | polling: `Channels.Telegram/TelegramChannel.cs:140-141`; «недоступен, попытка N»: `ChatGatewayService.cs:92`, «не принял токен»: `:87` |
| 3.1 | Маска `Text.Redact` стоит в `NowByKeys`, `Preview`, `ClaudeProjectSettings`, `MonitorModule` | Частично проверил | `Text.Preview` и `ClipRedacted` — `Agents.Abstractions/Text.cs:22-31`. `NowByKeys` и `MonitorModule` не открывал (по `docs/architecture.md:360-364` верно) |
| 3.2 | В логе маски нет — главный пробел | Верно | `RingBufferLog.cs:55-60` без маски; консоль — встроенная |
| 3.3 | `ClaudeBackend.cs:505,513`: кусок stdout/stderr не маскируется | Верно, номера устарели | `:515` (`Truncate(resultLine, 200)`), `:523` (`Truncate(details, 200)`); `Truncate` — `:631`, просто обрезка. Не маскируется |
| 3.4 | `:506,514` целиком — Debug | Верно, номера `:516`, `:524` | Debug отсекается и кольцом (`RingBufferLog.cs:48`) и `Default: Information`. Файловый провайдер должен сам отсекать Debug |
| 3.5 | `ClaudePermissionTool.cs:35` — payload карточки на Debug | Верно | `Agents.Claude/Mcp/ClaudePermissionTool.cs:35` |
| 3.6 | `OperatorConsole.cs:42,58,127` — Preview, 80 знаков, команда целиком в лог не идёт | Верно | `Features/Approvals/OperatorConsole.cs:40-42`, `:58`, `:126-127` (`Text.Preview`) |
| 3.7 | `OperatorConsole.cs:276,299,310` пути; `ex.Message` не маскируется | Номера те же, верно | `:276`, `:299`, `:310` |
| 3.8 | `ProjectTrustGate.cs:44` пункты настроек — уже `ClipRedacted` | Верно по `ProjectTrustGate.cs:44`, `ClaudeProjectSettings.cs:135` не открывал |
| 3.9 | Любое `logger.LogXxx(ex, …)` не маскируется | Верно | кольцо кладёт `exception.ToString()` (`RingBufferLog.cs:60`) |
| 3.10 | Токена Telegram в `scratch.log` нет | Не проверял | файла `scratch.log` не искал |
| 3.11 | `RingBufferLog` → `/api/log` без маски; монитор без пароля | Верно | `MonitorModule.cs:102-103`, `docs/en/monitor.md:32` |
| 3.12 | `JsonlAuditLog.cs:43` пишет готовую замаскированную строку | Верно | `JsonlAuditLog.cs:43` (`line` — результат `Serialize(entry)`, `entry` собран через `NowByKeys` по `docs/architecture.md:360`) |
| 4.1 | Папка `Path.Combine(AppPaths.DataDirectory, "logs")`, в контейнере `/data/logs` | Верно | `Dockerfile` (`Gateway__DataDirectory=/data`) |
| 4.2 | «ACL наследуется, отдельной настройки не нужно» | Верно | `DataDirectoryAcl.cs:27-32` флаги наследования. Нюанс: `Restrict` вызывается позже первых строк лога, но файл создаётся в уже созданной папке данных; права придут по наследованию. Не проверял на живой Windows |
| 4.3 | Фильтры `Logging:LogLevel` действуют на любой провайдер | Верно по документации .NET (категория+уровень применяются фабрикой). Не проверял в коде |
| 4.4 | `defaults` объявлена ниже в методе | Верно | `GatewayInfrastructure.cs:52`, а строки 42-43 выше |
| 4.5 | Ключ читается через `configuration.GetValue<int?>` как `McpPort` | Верно | `GatewayInfrastructure.cs:53-58` |
| 4.6 | Синхронная запись, а не очередь: критические строки перед `return 1` | Верно | `GatewayInfrastructure.cs:107-108` (`LogCritical` → `return 1`), `ChatGatewayService.cs:81` + `:40-41` |
| 4.7 | Критичный лог теряется, «если лога в файл нет» | Верно (раздел 1, таблицы нет файла) |
| 5.8 | В коде нет `AppDomain.UnhandledException`/`UnobservedTaskException` | Верно | Grep по `src/` на `UnhandledException|UnobservedTask|BackgroundServiceExceptionBehavior` — 0 совпадений |
| 5.8а | «Фоновый сервис роняет хост с записью ILogger Critical» | Верно по документации .NET 6+ (по умолчанию `StopHost`, ошибка логируется). Но см. B8 ниже: код выхода будет 0 |

Новое, чего нет в аудите:
- `Program.cs:86` возвращает `Environment.ExitCode`; при исключении из `ChatGatewayService.ExecuteAsync` (напр. из `channel.ListenAsync`, `ChatGatewayService.cs:49`) хост по умолчанию останавливается (`StopHost`), а `ExitCode` не ставится, значит выход 0 — Планировщик не перезапустит (`RestartOnFailure` ждёт сбоя). Исключение `.NET 11` это меняет (RunAsync начнёт бросать, документация «IHost.RunAsync … throw when a BackgroundService fails»), у нас .NET 10 (`AgentsTracker.Gateway.csproj`: `net10.0`). Что именно делает Планировщик при коде 0 — не проверял.
- `ChatGatewayService.cs:44-53`: `старт` пишется в журнал только после `ConnectAsync`; `стоп` — в `finally` вокруг `ListenAsync` (при ошибке `ConnectAsync` ни одной записи).

## 3. Вызовы `logger.Log*` (Information+), где может оказаться секрет или большой сырой текст

Всего вызовов Information+ в `src/`: около 96 (по Grep). Ниже только значимые. «Риск» — что попадёт в кольцо и в файл.

| Место | Что пишется | Риск |
|---|---|---|
| `Agents.Claude/ClaudeBackend.cs:75-76` | `Text.Preview(prompt)` (замаскирован, 80) и все аргументы CLI кроме `-p` и промпта. Аргументы: `--session-id`/`--resume`, `--tools`, `--add-dir`, путь `mcp-gateway-*.json`, модель, а для вопроса и беседы — постоянная строка `--append-system-prompt` (`:456-457`, ~160 знаков) | Низкий: токена MCP нет (он в файле, `:63-64`). Строка длинная, повторяющийся текст раздувает файл |
| `ClaudeBackend.cs:515` (Warning) | до 200 знаков итоговой строки `result` — там ответ агента | Средний: `Truncate` без маски. Ответ может содержать содержимое файла/переменные |
| `ClaudeBackend.cs:523` (Error) | до 200 знаков stderr или `Noise` (шум stdout) | Средний: без маски; stderr CLI иногда печатает окружение/заголовки |
| `ClaudeBackend.cs:516, 524` (Debug) | до 2000 знаков / весь вывод | Высокий, но только Debug: отсечь в провайдере. Если кто-то включит `Default: Debug`, файл станет хранилищем сырого вывода |
| `Agents.Claude/Mcp/ClaudePermissionTool.cs:35` (Debug) | весь payload карточки (содержимое Edit/Write) | Высокий при Debug; в Information+ не попадает |
| `Agents.Claude/ClaudeLimits.cs:241` (Warning) | до 500 знаков тела ответа эндпоинта лимитов (`Truncate`, `:421`) | Низкий-средний: тело ошибки сервиса, токена там быть не должно, не проверял |
| `ClaudeLimits.cs:257, 262` (Warning, `ex`) | исключения разбора и HTTP | Низкий |
| `Agents.Claude/ClaudePermissionTool.cs:63`, `ClaudeSendFileTool.cs:44` (Error, `ex`) | `ex.ToString()` + имя инструмента/путь | Низкий; исключение может нести строки из ввода инструмента (не проверял) |
| `Features/Approvals/OperatorConsole.cs:42, 58, 127` | `Text.Preview(...)` (маска, 80) | Низкий (замаскировано) |
| `OperatorConsole.cs:276, 299, 310` | полный путь файла, причина отказа, `ex` | Низкий: пути не секрет |
| `Features/Chat/ProjectTrustGate.cs:44` | `string.Join("; ", settings.Items)` — пункты настроек проекта (команды хуков и т. п.) | Средний-низкий: по аудиту уже через `ClipRedacted` в `ClaudeProjectSettings.cs:135` (сам не открывал), но объём — все пункты через «;» |
| `Infrastructure/Chat/StartupNotice.cs:88` | `run.Prompt` | Низкий: `RunRecord.Prompt = preview` (`ChatWorker.cs:417, 499`), `preview = Text.Preview(prompt.Text)` (`:359`) — маска и 80 знаков |
| `Infrastructure/Audit/JsonlAuditLog.cs:43` (Error) | `{Line}` — готовая запись аудита | Низкий (замаскирована при сборке) |
| `Channels.Telegram/TelegramChannel.cs:140, 204, 262, 360` (`ex`) | `ex.ToString()` исключений Telegram.Bot / HTTP | Средний: URL бота `/bot<токен>/...` в сообщении исключения не гарантированно исключён; `Redact` узнаёт токен по форме (`Text.cs:68`, `KnownTokens`) |
| `ChatGatewayService.cs:81, 87, 92` | `ex`, `ex.Message` при подключении | Средний тем же способом (токен в URL) |
| `Infrastructure/State/SessionStore.cs:742, 753, 886` (Error, `ex`) | исключения чтения/записи `state.json` | Низкий: пути, но `JsonException` может процитировать кусок файла (в `state.json` секретов нет по `docs/architecture.md`, не проверял) |
| `Infrastructure/GatewayInfrastructure.cs:107, 129` (Critical) | тексты ошибок конфига | Низкий: ошибки называют ключи, не значения (`:165-166`); `options.Validate()` не читал целиком |
| `Features/Chat/ChatWorker.cs:200` | `Dropped` — список снятых из очереди | Низкий (по виду переменной; не открывал) |

Итог: критичных мест Information+ без маски три — `ClaudeBackend.cs:515`, `:523` и любое `ex` (Telegram/HTTP). Остальное либо уже `Preview`, либо не секрет.

## 4. B3: время и уровень в каждой строке

Что есть: у консоли формат по умолчанию (Simple, две строки на запись, без времени: `info: Категория[0]` и следующая строка с отступом). Этот факт — поведение .NET по умолчанию, в коде репозитория настройки формата нет (`appsettings.json` без `Logging:Console`).

Вариант A (минимум, без кода): в `appsettings.json` добавить
```json
"Console": { "FormatterName": "simple", "FormatterOptions": { "SingleLine": true, "TimestampFormat": "yyyy-MM-dd HH:mm:ss.fff ", "UseUtcTimestamp": false } }
```
Это встроенные `ConsoleFormatterOptions` (ключи `SingleLine`, `TimestampFormat`, `UseUtcTimestamp`) — по документации .NET, в этом репозитории не собирал и не пробовал. Уровень в `simple` уже есть (`info:`, `warn:`, `fail:`). Время локальное (без `UseUtcTimestamp`). Исключение выводится следующими строками. Годится для `docker logs` и ручного запуска.
Но это правит только **консоль**. Кольцо уже имеет `At` и `Level` в `LogEntry`, а новый файловый провайдер пишет строку сам (раздел 5). Значит A закрывает B3 для консоли и контейнера, файл форматируется своим кодом.

Вариант B: `AddSimpleConsole(o => ...)` в `AddGatewayInfrastructure` (`builder.Logging.AddSimpleConsole`) — то же, но в коде, а не в `appsettings.json`. Выбирать только если нужно, чтобы пользователь не мог выключить через конфиг. Не нужен.

Вывод: хватает встроенного `SimpleConsole` через `appsettings.json` для консоли; для файла формат задаёт сам провайдер (время и уровень включить в `Format`). Своего форматтера консоли писать не надо.
Риск A: в `appsettings.json` пользователь может переопределить. Проверить на пробном экземпляре, что вторая строка исключения не ломает `docker logs` — не проверял.

## 5. B1: файловый лог — сравнение вариантов

1. **Свой `FileLoggerProvider`** (рекомендую). ~80-100 строк по образцу `JsonlAuditLog.cs:25-46`, без NuGet. Работает одинаково на Windows и в контейнере (`/data/logs` на томе, а консоль/`docker logs` остаётся). Плюсы: время, уровень, маска и пол Information в одном месте; кольцо и файл делят один форматтер; нет зависимости от способа запуска (Планировщик, `dotnet run`, Docker). Минусы: код + документация + ротация.
2. **Перенаправление stdout задачи Планировщика**. Планировщик не умеет перенаправлять сам: `<Exec>` (`WindowsScheduledTaskInstaller.cs:105-110`) принимает только `Command`/`Arguments`/`WorkingDirectory`. Пришлось бы запускать `cmd /c "exe >> log 2>&1"`. Минусы: лишний процесс `cmd` (остановка задачи `Stop` = `/End` может не убить дочерний `exe` — не проверял); консоль в OEM-кодировке (cp866, см. `docs/operations.md:60`) — кириллица в файле испортится; нет ротации, нет маски (консоль не маскируется); путь с пробелами и кавычками в XML; уже установленные задачи надо пересоздать (`install`). В контейнере не помогает (там `docker logs` и так есть). Не рекомендую.
3. Что-то ещё: встроенный `EventLog`-провайдер на Windows (по документации .NET по умолчанию Warning+). Не проверял, включён ли он здесь и читаем ли. Не подходит как основа: нет Linux, неудобно читать агенту/пользователю, лимиты размера записи.

Решение по записи (из аудита, подтверждаю): синхронно, `lock`, `Flush` на запись. Причина: `LogCritical` перед `return 1` (`GatewayInfrastructure.cs:107-108`) и `StopApplication` (`ChatGatewayService.cs:81, 40`) — фоновая очередь не успеет сброситься. Объём малый (аудит: 442 строки/час смоук-теста, число не проверял).

Сбой записи: `try/catch`, не бросать, не писать в `ILogger` (рекурсия), раз в серию — `Console.Error`.

## 6. B2: маска секретов

Как устроен `Text.Redact` (`Text.cs:40-51`): шесть регулярных выражений по очереди (`GeneratedRegex`, без таймаута): пароль в URL, `Authorization`, `Bearer`, токены по форме (Telegram `\d{6,12}:[A-Za-z0-9_-]{20,}`, `sk-`, `gh*_`, AWS, Slack, JWT), флаги `--token x`, присваивания `token=…`/`"password": "…"`.

Многострочный текст:
- `.` без `RegexOptions.Singleline` не пересекает `\n`, поэтому значение в кавычках, растянутое на несколько строк, маскируется только по первую строку (`(?:(?!\k<quote>).)+` в `SecretFlag`, `SecretAssignment`, `Text.cs:79, 88`). Остальные строки значения остаются. Редкий случай (многострочные приватные ключи).
- `\s*`/`\s+` пересекают `\n`: «token:\n   at X» — в `SecretAssignment` (`[:=]\s*` затем `[^\s"'&;,)]+`) может заменить следующее слово на `***`. Ложное срабатывание, терпимо для лога. Не проверял запуском, вывод из чтения шаблона.
- Токены по форме (самые важные) однострочные, многострочность им не мешает.
- Обрезка до маски: `ClipRedacted` режет до `limit*4` до `Redact` (`Text.cs:30-31`). У `Redact` самой лимита нет. Для стека исключения на десятки КБ надо обрезать до разумного размера (напр. 8-16 КБ) до маски. Скорость на большом тексте не мерил.

Где мaскировать — в приёмниках: общая статическая функция `LogLine.Format(DateTimeOffset, LogLevel, category, message, exception)`, внутри `Text.Redact(Text.Clip(…))`, зовётся из `RingLogger.Log` (`RingBufferLog.cs:55-60`) и из файлового провайдера. Для кольца маскировать отдельно `Message` и `Exception` (поля `LogEntry`, `:3`), чтобы JSON `/api/log` остался прежним. Точки вызова не менять.
Дыра: консоль (в т. ч. `docker logs`, который контейнер сохраняет сам) остаётся без маски, если не править форматтер консоли. Дёшево закрыть по источнику три места (`ClaudeBackend.cs:515, 523` → `Text.ClipRedacted(…, 200)`), исключения Telegram в консоли — принять как есть и записать в `docs/safety.md`.

## 7. B8: необработанные исключения

- Сейчас в коде ничего нет (раздел 2, 5.8).
- `AppDomain.CurrentDomain.UnhandledException`: вешать в `Program.cs` после `builder.Build()` и `ValidateStartup`, когда `ILoggerFactory` уже есть (напр. метод в `GatewayInfrastructure`, расширение на `WebApplication`: `ValidateStartup` → рядом `app.LogUnhandledExceptions()`; либо первой строкой после `var app = builder.Build();`, `Program.cs:76`). Обработчик: `LogCritical((Exception)e.ExceptionObject, …)`. Процесс после него всё равно падает, поэтому провайдер должен писать синхронно и с `Flush` (раздел 5).
- `TaskScheduler.UnobservedTaskException`: там же, `LogError(e.Exception, …)`; `e.SetObserved()` не нужен (в .NET Core процесс не падает). Срабатывает только при сборке мусора, поэтому поздно; это сигнал, а не гарантия.
- Исключения до `Build` (разбор конфига, `AppPaths.UseConfiguredDirectory`, `ConsoleCommands.TryRun`) в файл не попадут; консоль в Планировщике пропадает. Для них — либо принять, либо `try/catch` в `Program.cs` с `Console.Error` и записью в файл напрямую. Не обязательно.
- `BackgroundServiceExceptionBehavior`: по документации .NET по умолчанию `StopHost` — исключение из `ExecuteAsync` логируется (Critical) и хост останавливается, без `Ignore`. Настраивать не надо. Но код выхода: `Program.cs:86` вернёт `Environment.ExitCode`, а он 0, если исключение не из `ConnectAsync`. Чтобы Планировщик перезапустил шлюз (`RestartOnFailure`, `WindowsScheduledTaskInstaller.cs:100-103`), надо в `ChatGatewayService.ExecuteAsync` обернуть `ListenAsync`/`SendAsync` в `catch (Exception ex) when (ex is not OperationCanceledException)` → `LogCritical`, `Environment.ExitCode = 1`, `lifetime.StopApplication()`. 5-6 строк. Оговорка: как Планировщик воспринимает код 0/1 — по `ChatGatewayService.cs:38-39` комментарий автора говорит «Ненулевой код выхода — повод для Планировщика перезапустить задачу»; живьём не проверял.
- Остальные фоновые места: `ChatWorker.cs:204` и `TelegramChannel.cs:204, 262` уже ловят и логируют (Error) — отдельных ловушек для них не нужно.

## 8. Размер и срок хранения: ключ конфига или константа

Хватит констант в классе провайдера: хранение 14 суток, предел 20 МБ в сутки (аудит). Причина: ручек у пользователя уже много, лог — служебный, худший случай 14 × 20 МБ = 280 МБ. Нет `0 — не писать` — тогда нет и ветки «лог выключен».

Если всё же нужен ключ `Gateway:LogRetentionDays` (так хотел аудит), чек-лист `docs/en/extending.md:164-172` требует:
1. свойство в `Infrastructure/Configuration/GatewayOptions.cs` + проверка в `Validate` (образец: `RetentionHours`, `:40, 49-50`, `RunTimeoutMinutes`, `:153, 198-199`);
2. значение по умолчанию в `appsettings.json`;
3. пример `"//Ключ": "…"` в `appsettings.Local.example.json`;
4. строка в README, «Основные настройки» (образец: `README.md:259-260`);
5. строка в таблице конфига `docs/deployment.md` и `docs/en/deployment.md` (оба языка в одном коммите);
6. читать до привязки `IOptions` через `configuration.GetValue<int?>`, как `McpPort` (`GatewayInfrastructure.cs:53-58`), потому что провайдер создаётся раньше.
Это ~20 строк и 6 файлов ради параметра, которым вряд ли кто-то воспользуется. Константа дешевле.

## 9. Документация: что говорит про лог сейчас и что править

Русские — `docs/*.md`, английские — `docs/en/*.md`, одним коммитом. Новых файлов нет (в `sync-wiki.ps1` менять нечего).

| Место | Сейчас | Что править |
|---|---|---|
| `docs/deployment.md:232-233` / `docs/en/deployment.md:235-237` | «Файла лога нет. Задача Планировщика пишет в консоль, которой нет…» | Заменить: лог — `logs\gateway-ГГГГ-ММ-ДД.log`, хранение, предел; `docker compose logs -f` остаётся |
| `docs/deployment.md:219-228` (таблица папки данных) / `docs/en/deployment.md:219+` | перечислены `appsettings.Local.json`, `state.json`, `audit\`, `inbox\`, `mcp-gateway-*.json` | Добавить строку `logs\gateway-ГГГГ-ММ-ДД.log`; в абзац о бэкапе — «логи класть необязательно» |
| `docs/operations.md:60` / `docs/en/operations.md:61` | «Консоль отдаёт русский в cp866 — лог смотрите в PowerShell» | Добавить: лог пробного экземпляра лежит в `<Gateway__DataDirectory>\logs\`; читать `Get-Content -Encoding utf8` |
| `docs/operations.md` (пробный экземпляр, ~`:69-110`) | `Tee-Object` в файле не найден (Grep `Tee-Object` по `docs/`, `README.md`, `CLAUDE.md` — 0) | Проверить, что в `scripts/` нет скрипта с `Tee-Object`: не проверял. Если есть — переписать на чтение файла лога |
| `docs/architecture.md:360-369` / `docs/en/architecture.md:366-375` | маска стоит в `NowByKeys`/`Preview`; «в `ILogger` — то, что нужно для разбирательства» | Добавить: лог (файл и кольцо) маскируется в приёмнике, общий форматтер; консоль не маскируется |
| `docs/architecture.md` (раздел «Состояние, секреты»; в `docs/architecture.md` не открывал точное место) | список папки данных | Добавить `logs\` |
| `docs/safety.md:126` / `docs/en/safety.md:127` | «токены и пароли из команд попадают в журнал, лог и монитор как `***`» | Теперь верно для кольца и файла; про консоль/`docker logs` — написать честно |
| `docs/monitor.md:54-55` / `docs/en/monitor.md:55-56` | «лог — `RingBufferLog` (500 записей, Information+, `ILoggerProvider`)» | Добавить «маскируется; файл лога — отдельно» |
| `docs/monitor.md:31` / `docs/en/monitor.md:32` | `/api/log` описан | Менять не надо (формат тот же) |
| `README.md:330` | «В логе кракозябры — консоль в cp866; смотрите лог в PowerShell» | Добавить «или откройте файл `logs\…`» |
| `CLAUDE.md:199-202` | «Secrets are masked in one place, `Text.Redact`… anything new that reaches the journal, the log or the monitor goes through it» | Уточнить: в лог и кольцо — автоматически в приёмнике |
| `docs/glossary.md:49-50, 107-108` / `docs/en/glossary.md:49, 112` | журнал/лог разведены | Добавить, что лог лежит в `logs\` (по желанию); термины менять не надо |
| `docs/deployment.md:262` / `docs/en/deployment.md:268` | версия в первой строке лога | Без правок |

## Рекомендация

| Пункт | Самый простой вариант | Оценка | Риски |
|---|---|---|---|
| B1 файл | Свой `FileLoggerProvider` в `Infrastructure/Monitoring/FileLoggerProvider.cs`, `logs\gateway-ГГГГ-ММ-ДД.log`, sync-запись с `Flush` (по образцу `JsonlAuditLog.cs`), чистка по дате в имени, регистрация рядом с `GatewayInfrastructure.cs:43`. Константы: 14 суток, 20 МБ/сутки | ~90-110 строк кода + ~25 строк документации в обеих версиях | Сбой записи не должен ронять и не писать в `ILogger`; Debug отсекать; права на папку приходят из `DataDirectoryAcl` (не проверял на Windows) |
| B2 маска | Статический `LogLine.Format` с `Text.Redact(Text.Clip(…,16 КБ))`; звать из `RingLogger.Log` и из файла; плюс `Text.ClipRedacted` в `ClaudeBackend.cs:515, 523` | ~25 строк | Многострочные значения в кавычках маскируются до конца строки; `\s` даёт ложные `***`; консоль и `docker logs` остаются без маски для `ex` |
| B3 время и уровень | Для консоли — `Logging:Console:FormatterOptions` (`SingleLine`, `TimestampFormat`) в `appsettings.json`; для файла и кольца — в `LogLine.Format`. Свой форматтер консоли не нужен | ~5 строк JSON | Ключи — по документации .NET, не запускал; проверить на пробном экземпляре |
| B8 исключения | `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` после `builder.Build()` в `Program.cs`/`GatewayInfrastructure`; `BackgroundServiceExceptionBehavior` не трогать; в `ChatGatewayService.ExecuteAsync` поймать, залогировать, поставить `Environment.ExitCode = 1` | ~20 строк | Падение фонового сервиса даёт код 0 (сейчас), Планировщик не перезапустит; ловушки не видят ошибок до `Build` |
| Размер/срок | Константы, без ключа конфига | 2 строки | Нельзя поменять без пересборки; если нужен ключ — 6 мест по чек-листу «a config key» |
| Планировщик с перенаправлением | Не делать | — | cp866, лишний `cmd`, нет маски, нет ротации |

Порядок: 1) `LogLine` + маска в кольце; 2) `FileLoggerProvider`; 3) `appsettings.json` формат консоли; 4) ловушки + выход 1; 5) документация, одним коммитом.
Не проверял: поведение Планировщика при коде выхода 0; живую запись лога под ACL; формат вывода `SimpleConsole` с `SingleLine` в этой сборке; наличие `Tee-Object` в `scripts/`; `NowByKeys`, `MonitorModule` (маска правил), `ClaudeProjectSettings.cs:135`.

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Что это

Шлюз между Telegram и самим Claude Code: одно долгоживущее приложение .NET 10 на постоянно
включённом Windows-ПК. Получает сообщение из чата → запускает `claude -p` в папке проекта →
спрашивает разрешения кнопками в Telegram → присылает ответ агента.

Код, комментарии, лог и тексты в чате — на русском. Держитесь этого стиля.

## Команды

```powershell
dotnet build                                    # сборка; TreatWarningsAsErrors включён
dotnet run --project src\AgentsTracker.Gateway  # запуск; нужен appsettings.Local.json (в папке данных или рядом)
dotnet run --project src\AgentsTracker.Gateway -- protect-secrets   # зашифровать BotToken/Proxy (DPAPI), перенести конфиг в %LOCALAPPDATA%
pwsh -File scripts\install-autostart.ps1        # publish + protect-secrets + ACL + задача Планировщика на вход в систему
```

Тестового проекта нет. Изменения, затрагивающие контракт с CLI (аргументы запуска, разбор JSON,
форма ответа `ClaudePermissionTool`), проверяются вручную: запустить шлюз, выполнить `claude -p …`
с теми же флагами против его MCP-эндпоинта и посмотреть лог.

Конфиг перекрывается переменными окружения — удобно для разовых проверок без правки файла:

```powershell
$env:Gateway__ProjectPath = 'C:\tmp\test'; $env:Gateway__AllowedUserIds__0 = '1'
```

Разовый прогон без правки конфига: запустить exe с `$env:Gateway__BotToken`,
`Gateway__AllowedUserIds__0` и **своими** `Gateway__McpPort`/`Gateway__MonitorPort`. Но
`mcp-gateway.json` в папке данных временный экземпляр перезапишет в любом случае (путь
один, `%LOCALAPPDATA%` берётся не из переменной окружения), и следующий `claude -p`
работающего экземпляра не достучится до подтверждений — после пробы его надо перезапустить.
Два экземпляра шлюза на одной машине не уживаются и по другой причине: бот один, `getUpdates`
отдаётся 409 (с поддельным токеном экземпляр живёт ~минуту до выхода — хватает, чтобы
дёрнуть эндпоинты монитора).

Сборка падает с `MSB3021`, если шлюз запущен: exe заблокирован. Собирайте в другую папку —
`dotnet build src\AgentsTracker.Gateway -o <временная папка>`.

Проверить логику отдельного класса, не останавливая шлюз: собрать в другую папку, а из
временного консольного проекта сослаться на **готовую dll** (`<Reference>` с `HintPath`),
не на csproj — `ProjectReference` полез бы пересобирать занятый `bin\Debug`. Так проверялся
обход `ProjectCatalog` по реальному дереву репозиториев.

Перезапуск работающего экземпляра (задачи Планировщика на машине разработки обычно нет — exe
запущен вручную из `bin\Debug`, путь покажет `Get-Process AgentsTracker.Gateway`):

```powershell
Get-Process AgentsTracker.Gateway | Stop-Process -Force   # иначе сборка упадёт с MSB3021
dotnet build src\AgentsTracker.Gateway
Start-Process src\AgentsTracker.Gateway\bin\Debug\net10.0\AgentsTracker.Gateway.exe
```

Если сессия запущена самим шлюзом (из Telegram), перезапускать его из неё нельзя: Stop-Process
убьёт и текущий `claude -p`, а отложенный перезапуск через `schtasks /SC ONCE` не срабатывал.
Дайте пользователю три команды выше и попросите выполнить руками. Симптом того, что шлюз
надо перезапустить (после пробы временного экземпляра): каждый вызов инструмента падает с
«MCP tool mcp__tg__approve not found».

Консоль отдаёт русский текст в cp866: в оболочке, ожидающей UTF-8, лог выглядит мусором —
смотрите его в PowerShell (`iconv` в Git Bash здесь не установлен).

Крупную задачу (несколько фаз, перезапуски шлюза по ходу) начинайте с worktree, а не с ветки
в этой же папке: шлюз запущен из `bin\Debug` этой папки, и переключение ветки подменяет
исходники под работающим процессом, а сломанная сборка лишает возможности его перезапустить.

```powershell
git worktree add ..\AgentsTracker-<задача> -b <ветка>   # main остаётся папкой, из которой запущен шлюз
```

Дальше работайте в новой папке (из чата — `/project`, у неё будут свои сессии), а в `main`
вливайте готовое. Мелкие правки в один-два коммита — прямо в `main`, как раньше.

## Как вносить изменения

Новая команда чата: класс с `ITelegramCommandHandler` в папке нужной фичи + строка
`services.AddSingleton<ITelegramCommandHandler, …>()` в её `*Module` + запись в
`BotCommandsCatalog` (кнопка «Меню») и в тексте `HelpCommandHandler`. Занятые команды:
`/start /help` (Help), `/new /stop /status` (Chat), `/rules` (Approvals), `/audit` (Audit),
`/menu /settings /model /effort /mode /project /sessions /skills /usage` (Settings).
Остальные слэш-команды уходят в CLI как есть.

Новый экран настроек: класс с `ISettingsScreen` в `Features/Settings/Screens/`, регистрация
в `SettingsModule`, кнопка на него — в `RootScreen`. `RenderAsync(userId, ct)` асинхронный ради
лимитов; экрану без сети хватает `Task.FromResult(Render())` поверх приватного `Render()`.
`Apply` получает и `chatId`: экран может ставить задачу в очередь `ChatWorker` (так делает
`SkillsScreen`), и ответ агента должен уйти в чат нажавшего. Экран со списком держит позицию
(группа, страница, карточка) в `ScreenNavigation` — на каждого пользователя, потому что экраны
синглтоны, а `AllowedUserIds` допускает нескольких людей; `Open(userId)` сбрасывает позицию при
входе из корня или командой, иначе показалась бы прошлая карточка. Страницы и короткие ключи
для callback_data — общие `SettingsKeyboard.Page`/`Key12`; однобуквенные префиксы аргументов
экрана не должны быть hex-символами, иначе спутаются с ключом.

Новая фича: папка в `Features/` с `*Module` и запись в списке модулей в `Program.cs`.
`ChatModule` там остаётся последним.

Новый агент (Codex, Cursor): отдельный проект `src/AgentsTracker.Agents.<Имя>` со ссылкой на
`AgentsTracker.Agents.Abstractions`, в нём `IAgentBackendModule`, регистрирующий `IAgentBackend`,
`IAgentLimits` (или `NoAgentLimits`), `IAgentSkillCatalog` (или `NoAgentSkills`) и свой канал
подтверждений, который зовёт `IOperatorConsole`; плюс строка в списке `agents` в `Program.cs`
и ссылка на проект в `AgentsTracker.Gateway.csproj`. Хост видит агента только через контракты:
`grep -rn Claude src/AgentsTracker.Gateway --include=*.cs` должен находить лишь `Program.cs`
и комментарии. Настройки агента — в подсекции `Gateway:<Id>` (`Gateway:Claude`), хост её не читает.

Новый эндпоинт монитора: `api.MapGet` в `MonitorModule.MapEndpoints` (группа уже фильтрует
порт), данные — только чтение, `Results.Json(..., Json)`; новая секция — в `index.html`.

## Архитектура

### Слои и слайсы

```
src/AgentsTracker.Agents.Abstractions/   контракты агента, без Telegram и без конкретного CLI:
  IAgentBackend         Id, DisplayName, Capabilities, Probe() (бинарник и версия), RunAsync(AgentRunRequest, IAgentRunObserver)
  AgentRun.cs           AgentRunRequest (промпт, папка, сессия, модель, effort, режим, бюджет, таймаут),
                        IAgentRunObserver (SessionStarted, Activity), AgentRunResult (+SessionLost, RateLimited)
  AgentCapabilities     AgentSetting для модели, effort (null — не умеет), режима разрешений; SupportsRunBudget
  IOperatorConsole      что агент просит у человека: ApproveAsync(ApprovalRequest) → ApprovalDecision,
                        AskAsync(вопросы) → QuestionResult; PersistentRule — правило «всегда» в формате агента
  IAgentLimits          лимиты тарифа (+NoAgentLimits), IAgentSkillCatalog — слэш-команды (+NoAgentSkills)
  IAgentBackendModule   AddServices + MapEndpoints; AgentHost — папка данных, порт и прокси от хоста
  RunActivity, RunUsage, Text — общие модели и обрезка текста
src/AgentsTracker.Agents.Claude/         Claude Code за этими контрактами:
  ClaudeAgentModule     регистрация всего ниже, MapMcp(/mcp) с фильтром токена, Dispose McpConfigFile
  ClaudeBackend         процесс claude -p, аргументы, разбор stream-json, «сессия не найдена», лимит тарифа
  ClaudeCapabilities    PermissionModes, EffortLevels, алиасы моделей → AgentCapabilities
  ClaudeOptions         секция Gateway:Claude — Executable, BuiltInSkills
  ClaudeCliLocator, ClaudeCliJson, ClaudeStreamEvent, ClaudeLimits, ClaudeSkillCatalog
  Mcp/                  McpConfigFile — mcp-gateway.json, токен в заголовке;
                        ClaudePermissionTool — MCP-инструмент: payload CLI → IOperatorConsole → JSON для CLI
src/AgentsTracker.Gateway/
  Program.cs            список IAgentBackendModule (выбор по Gateway:Agent) и IFeatureModule
                        → AddGatewayConfiguration → AddGatewayInfrastructure → ValidateStartup → MapFeatures
  GlobalUsings.cs       Agents, Domain, Infrastructure, .Configuration, .State, IOptions — доступны везде
  Domain/               чистые модели без I/O и DI: GatewayState (state.json), AuditEvent
  Infrastructure/       техническая часть, общая для фич (AppPaths, GatewayInfrastructure — в корне):
    Configuration/      GatewayOptions (+Validate, +ValidateFor(capabilities)), ProjectCatalog (Normalize/Same — ключ сессий)
    State/              SessionStore — state.json под Lock, атомарная запись
    Telegram/           TelegramBotService (роутер), TelegramClientFactory, TelegramFormatter,
                        DisplayFormat, BotCommandsCatalog,
                        Dispatch/ — ITelegramCommandHandler / ITelegramCallbackHandler / ITelegramTextHandler
    Audit/              IAuditLog, JsonlAuditLog — журнал «кто, куда, что»
    Monitoring/         RunMonitor — живое состояние (запуск, шаги, очередь, ожидание карточки) и подписка;
                        RingBufferLog — хвост ILogger в памяти для монитора
    Security/           DPAPI-шифрование конфига, ACL папки данных, команда protect-secrets
    Modules/            IFeatureModule — AddServices + MapEndpoints
  Features/             вертикальные слайсы, каждый со своим *Module:
    Approvals/          OperatorConsole (IOperatorConsole: карточки, правила «всегда», вопросы, аудит),
                        ApprovalBroker, ApprovalCardRenderer, /rules
    Chat/               ChatWorker (очередь, запуск через IAgentBackend, сессии), RunStatusMessage (живой статус),
                        /new /stop /status, fallback-обработчик текста
    Settings/           SettingsMenuCoordinator + Screens/*Screen (ISettingsScreen), /menu /model /effort /mode /skills …
    Help/               /start /help
    Audit/              /audit
    Monitor/            веб-монитор: MonitorModule (эндпоинты /, /api/*) + index.html (вшит в сборку)
```

Правила разложения: в `Domain` и `Agents.Abstractions` ничего не открывает файлы и не ходит по
сети; `Gateway` знает о конкретном агенте только в `Program.cs`, всё остальное — через
`IAgentBackend`/`IAgentLimits`/`IAgentSkillCatalog`/`AgentCapabilities`; бэкенд не знает о
Telegram и `GatewayOptions` (ему даётся `AgentHost`); `Infrastructure`
не знает о фичах (кроме контрактов `Dispatch`); фича зависит от другой фичи только через её
публичный сервис (`Settings` → `ChatWorker.IsBusy`, `Approvals` → `SettingsMenuCoordinator.CallbackPrefix`).

### Диспетчер Telegram

`TelegramBotService` сам ничего не делает: проверяет `AllowedUserIds` и `ChatType.Private`, потом
раздаёт обновления обработчикам из DI. Порядок в `HandleTextAsync` принципиален:

1. слэш-команда ищется в словаре `ITelegramCommandHandler.Commands` — **до** всего остального,
   иначе `/stop` уйдёт в ожидающий свободный ответ и прервать зависший запуск будет нечем.
   Дубликат команды у двух фич роняет старт;
2. цепочка `ITelegramTextHandler` в порядке регистрации модулей: `ApprovalTextHandler`
   (`broker.TryConsumeText` — причина отказа или свой ответ на `AskUserQuestion`) →
   `SkillArgumentsTextHandler` (аргументы скилла после кнопки «С аргументами») →
   `ChatEnqueueTextHandler` (всегда `true`). Поэтому `ChatModule` в `Program.cs` **последний**.
   Неизвестные слэш-команды сюда и попадают — это команды самого Claude Code (`/review` и прочие).

Callback-и делят один поток: `ITelegramCallbackHandler.CanHandle` — префикс `cfg:` у меню,
всё остальное (hex-id запроса) у `ApprovalBroker`.

`ActiveChatId` брокера выставляет `ChatWorker` перед самым запуском, а не обработчик сообщения:
иначе карточки уже идущего запуска ушли бы в чат другого пользователя.

### Кольцо «шлюз → CLI → шлюз»

Шлюз одновременно **запускает** агента и **обслуживает** его. Для Claude Code:

```
Telegram ──▶ TelegramBotService ──▶ ChatWorker ──▶ IAgentBackend (ClaudeBackend) ──▶ claude.exe -p
                    ▲                                                                     │
                    │       карточка с кнопками                                           │ нужно разрешение
                    └── ApprovalBroker ◀── OperatorConsole ◀── ClaudePermissionTool ◀── MCP http://127.0.0.1:<порт>/mcp
                        (Gateway)          (IOperatorConsole)  (Agents.Claude)            Authorization: Bearer <токен>
```

Граница проходит по `IOperatorConsole`: хост показывает карточку, помнит правила «всегда»
и пишет аудит, а как запрос доставлен и в каком JSON вернуть решение — знает только бэкенд.
`McpConfigFile` при старте генерирует токен, пишет `mcp-gateway.json` с заголовком
`Authorization` и удаляет файл при остановке; `ClaudeAgentModule.MapEndpoints` монтирует `/mcp`
с фильтром `McpConfigFile.Authorizes`. `ClaudeBackend` передаёт CLI `--mcp-config` с этим файлом
и `--permission-prompt-tool mcp__tg__approve`. Правя одну сторону, проверяйте вторую: имя сервера
и инструмента — константы `McpConfigFile`, атрибут `[McpServerTool]` берёт ту же константу.
Порт и папку данных бэкенд получает через `AgentHost`, а не из `GatewayOptions`.
README — простая инструкция для пользователя, без подробностей устройства; меняя защиту
эндпоинта или папку данных, проверьте его раздел «Безопасность».

### Контракт подтверждений (проверен на живом CLI, схема входа не задокументирована)

CLI зовёт инструмент с `{"tool_name":…,"input":{…},"tool_use_id":…}`. Ответ — JSON-строка:

- `{"behavior":"allow","updatedInput":{…}}` — `updatedInput` **обязателен**, без него CLI считает
  результат невалидным и отклоняет вызов;
- `{"behavior":"deny","message":"…"}`.

`ClaudePermissionTool` читает поля защитно (`Read(...)` перебирает snake_case/camelCase); сырой payload
пишется только на Debug — на Information для Edit/Write это было бы содержимое файлов.
`AskUserQuestion` приходит в тот же инструмент и требует вернуть `updatedInput` с исходным
`questions` и собранным `answers` (ключ — текст вопроса); в хост он уходит как
`IOperatorConsole.AskAsync` со списком `AgentQuestion`.

Кнопка «Всегда» ведёт себя двояко: если CLI прислал `permission_suggestions` с
`destination: localSettings`, правило записывает **сам CLI** в `.claude/settings.local.json`
проекта (обычно префиксное, шире точного совпадения), и `ApprovalCardRenderer` показывает именно
эти правила (в хост они приходят как `PersistentRule` с `Raw` — элемент в формате CLI, который
возвращается ему без изменений в `updatedPermissions`); иначе шлюз запоминает точную сигнатуру в `state.json`
(`AlwaysAllowByProject`, ключ — нормализованный путь проекта), и её видно в `/rules`.
Правила шлюза действуют только в своём проекте: `git push --force`, разрешённый в одном
репозитории, не должен молча проходить в остальных. Старый плоский список `AlwaysAllow`
при загрузке переезжает в проект, который был текущим.

Карточка режет длинные фрагменты под лимит сообщения, но одобрять команду с невидимым
хвостом нельзя: `ApprovalCardRenderer.Render` возвращает `ApprovalCard`, и когда что-то
обрезано (команда, стороны правки, остальные правки `MultiEdit`, хвост файла `Write`),
`OperatorConsole` перед карточкой шлёт полный текст файлом через
`ApprovalBroker.SendAttachmentAsync`, а карточка предупреждает «показано не всё».

Самовыдача прав агентом проверена на CLI 2.1.x: `Write` в `.claude/settings.local.json`
отклоняется даже в `acceptEdits` (виден в `permission_denials`), так что режим правок
не открывает дорогу к `permissions.allow`. После обновления CLI стоит перепроверить тем же
запуском `claude -p … --permission-mode acceptEdits --output-format json` во временной папке.

`ApprovalBroker` держит вызов MCP открытым на `TaskCompletionSource`, пока пользователь не нажмёт
кнопку, и возвращает `ChoiceResult` (ключ + кто нажал — для аудита). `WaitAsync` намеренно
различает таймаут и отмену: отменённый `/stop` запуск должен бросать `OperationCanceledException`,
а не выглядеть как «не ответил вовремя».

Таймауты: `ApprovalTimeoutMinutes` (15) — сколько брокер держит карточку, `RunTimeoutMinutes`
(60) — весь запуск `claude -p`; оба 1..1440, проверяет `Validate`. Поднимать первый выше
~5 минут бессмысленно для `AskUserQuestion` и `ExitPlanMode`: раньше сработает idle-таймаут
MCP на стороне CLI (см. «Что стоит держать в голове»).

### Статус запуска и поток событий

CLI запускается с `--output-format stream-json --verbose` (без `--verbose` CLI отказывается
писать поток в режиме `-p`). События идут по строке на каждое; `ClaudeBackend.ReadStreamAsync`
читает stdout построчно, каждую строку разбирает один раз (`ClaudeStreamEvent.Classify`),
вызовы инструментов (`assistant` → `tool_use`) отдаёт в `IAgentRunObserver.Activity`, остальные
события отбрасывает — в долгом запуске их мегабайты. Итог — последняя строка `"type":"result"`, той же формы, что
ответ `--output-format json` (`ClaudeCliJson` не менялся); `Parse` получает её отдельно от
«шума» (не-JSON строки вроде баннера обновления, а без итога — последнее событие), и шум
идёт в текст ошибки. В stream-json текст ошибки CLI часто оставляет в stderr, а `result`
присылает пустым — так с «No conversation found» при битом `--resume`; поэтому проверка
сброса сессии и лимита тарифа смотрит и в stderr, а пустой текст ошибки заменяется им.

В чате это `RunStatusMessage` (`Features/Chat/`): одно сообщение «Работаю…», которое раз
в 4 секунды редактируется — часы крутятся, время растёт, ниже счётчик вызовов и три последних
шага (`Read ChatWorker.cs`, `Bash dotnet build`, шаги сабагентов с `↳`). Иначе долгий запуск
неотличим от зависшего шлюза. `Report` зовётся из потока чтения stdout и только запоминает,
сеть — в своём цикле; `DisposeAsync` дожидается цикла (не дольше 10 с), иначе правка
догоняла бы удаление сообщения. Аргумент инструмента в статусе один и короткий (`ClaudeStreamEvent.Describe`):
полный ввод Edit/Write — это содержимое файла.

### Веб-монитор

`Features/Monitor/` — страница состояния на **отдельном** порту `Gateway:MonitorPort`
(по умолчанию 5100, `0` выключает; `Validate` не даёт совпасть с `McpPort`). Kestrel слушает
оба порта одним конвейером, поэтому группа эндпоинтов монитора фильтрует
`Connection.LocalPort` — иначе страница открылась бы и на порту MCP (проверка: `/` на порту
MCP отдаёт 404). Авторизации нет намеренно, только loopback: поэтому эндпоинты **только
читают** — `/stop`, смена проекта и прочие действия остаются в Telegram, где есть
`AllowedUserIds` и аудит «кто нажал».

Источник «что сейчас» — `RunMonitor` (`Infrastructure/Monitoring`): `ChatWorker` сообщает
очередь (`Enqueued`/`Dequeued`/`QueueCleared`), старт (`RunStarted`), каждый шаг (тот же
callback, что у `RunStatusMessage`) и финиш; `OperatorConsole` — ожидание карточки через
`using monitor.Approval(tool, brief)`, где brief — та же короткая строка, что в логе (полный
ввод Edit/Write — содержимое файлов, на страницу не идёт). Карточек в снимке список
(`Approvals`), не одна: CLI зовёт инструмент параллельно на несколько `tool_use` одного хода.
Шагов хранится 300 последних, `DroppedSteps` — разница со счётчиком вызовов. `Changes()` — подписка: канал на одного подписчика
ёмкостью 1 с вытеснением, медленный браузер получает последнее состояние, а не очередь
устаревших. `/api/events` — SSE (`TypedResults.ServerSentEvents`): снимок при подключении,
далее по изменениям, между ними `ping` раз в 5 с — без него страница не отличит тишину от
упавшего шлюза. Снимок собирается в `MonitorModule` из `RunMonitor.Current` + `SessionStore`.

История запусков — `GatewayState.RecentRuns` (200 последних, пишет `ChatWorker` через
`SessionStore.RecordRunOutcome` после запуска: исход, превью промпта через `Text.Preview`,
число вызовов, расход). Отдельно от `RecordRun`: расход приходит в `AgentRunResult.Usage`, а
исход определяет `ChatWorker`. Статистика (`/api/stats`, `/api/stats.csv` — `;`, BOM и десятичная запятая для Excel на
русской локали, иначе дробные приходят текстом)
берётся из `Snapshot()`; лимиты — `IAgentLimits.GetAsync` (у Claude кэш 3 мин, страница опрашивает
раз в минуту); аудит — `IAuditLog.Tail`; лог — `RingBufferLog` (500 записей, Information и
выше, регистрируется как `ILoggerProvider`).

`index.html` — один файл без сборки и CDN, `EmbeddedResource` в csproj: publish не зависит
от папки рядом с exe. Все данные из `/api/*` в camelCase (`JsonSerializerDefaults.Web`),
кириллица без `\u`-экранирования.

Проверка без остановки рабочего шлюза невозможна изолированно: пробный экземпляр
перезаписывает его `mcp-gateway.json` (`%LOCALAPPDATA%` берётся через
`Environment.GetFolderPath`, переменная окружения не перекрывает) — см. «Команды».

### `--permission-mode` передаётся всегда

Без явного флага действует `permissions.defaultMode` из `~/.claude/settings.json` пользователя.
У него там `auto` — решения принимает классификатор, и кнопки в чате не появляются вовсе.
Не убирайте этот аргумент из `ClaudeBackend.BuildArguments`.

Списки значений для меню и проверки конфига объявляет бэкенд в `AgentCapabilities`
(`ClaudeCapabilities`): `PermissionMode.Selectable` (`plan`/`default`/`acceptEdits`/`auto`) — то,
что можно переключать из чата. `dontAsk` и `bypassPermissions` исключены намеренно: полное снятие
подтверждений остаётся правкой конфига на самой машине. `Effort` у капабилити может быть `null` —
тогда `RootScreen` не показывает кнопку, а `/effort` отвечает отказом. `GatewayOptions.Validate`
эти значения не проверяет (агент ещё не выбран), проверяет `ValidateFor(capabilities)` в
`ValidateStartup`.

### Состояние, секреты и наслоение настроек

`%LOCALAPPDATA%\AgentsTracker\` (`AppPaths.DataDirectory`, ACL — только владелец и SYSTEM):
`state.json` (пишется атомарно), `mcp-gateway.json`, `appsettings.Local.json` с секретами,
`audit\audit-ГГГГ-ММ.jsonl`.

Конфиг слоями (`GatewayInfrastructure.AddGatewayConfiguration`): `appsettings.json` →
`appsettings.Local.json` рядом с приложением (отладка из IDE) → тот же файл в папке данных
(боевой) → переменные окружения. Значения `dpapi:…` расшифровываются при загрузке
(`ProtectedJsonConfigurationProvider`); команда `protect-secrets` шифрует `BotToken` и `Proxy`
и переносит файл в папку данных. `publish\` секретов не содержит.

Почти всё настраиваемое живёт в двух слоях: `SessionStore` (выбор из чата) поверх `GatewayOptions`
(конфиг) — `EffectiveModel`, `EffectivePermissionMode`, `EffectiveEffort`. Значение, совпадающее с
конфигом, сохраняется как `null`, чтобы правка конфига не оказалась молча перекрыта старым выбором.

Стартовая папка — `Gateway:ProjectPath`; выбранная из меню живёт в `state.json`
(`GatewayState.ProjectPath`) и переживает перезапуск, а совпадение с конфигом хранится как
`null` — по тому же правилу, что и остальные наслоения. Список для меню `ProjectCatalog` берёт
из `Gateway:Projects`, иначе обходит `Gateway:ProjectsRoot` вглубь до `ProjectsRootDepth`
(спуск прекращается на папке с признаком проекта — внутри репозитория искать нечего), иначе
смотрит соседей `ProjectPath`. `ProjectCatalog.Grouped` раскладывает найденное по папкам-владельцам
(под `ProjectsRoot` — по пути относительно корня, иначе по имени родителя), и `ProjectScreen`
выбирает в два шага: сначала папка (`ME`, `MF`, `PILX`), потом репозиторий в ней; оба списка
страницами по 12. Одна папка — промежуточный экран пропускается. Репозиториев бывает больше,
чем влезает в клавиатуру: без папок и страниц они были бы недостижимы. `Grouped` сохраняет
порядок `List` — текущий проект первым в своей группе, а после выбора страница сбрасывается
на первую: иначе на длинном списке отметка `▶` оказывалась бы за пределами экрана.

Экран «Скиллы» (`/skills`) — `IAgentSkillCatalog` (у Claude `ClaudeSkillCatalog`) собирает то же, что видит CLI: `skills/*/SKILL.md`
(один уровень) и `commands/**/*.md` (подпапка → `/папка:имя`) из `.claude` проекта и `~/.claude`,
плюс из `installPath` включённых плагинов
(`~/.claude/plugins/installed_plugins.json`, флаги `enabledPlugins` наслаиваются: профиль →
`.claude/settings.json` → `settings.local.json`; плагин без записи считается включённым).
Плагинные скиллы зовутся `/плагин:имя`; `user-invocable: false` в списке нет. Frontmatter
разбирается плоско, без YAML-библиотеки. Результат обхода кэшируется на 5 секунд: одно нажатие
в меню — это `Apply` и `Render` подряд, без кэша это два обхода диска. Встроенные скиллы CLI (`/code-review`, `/init` и т.п.)
вшиты в `claude.exe`, перечислить их CLI не умеет — группа «Встроенные» берётся из
`Gateway:Claude:BuiltInSkills` (список по умолчанию в `ClaudeOptions` под CLI 2.1.x; после обновления
CLI переопределяется конфигом без пересборки; третья часть строки — подсказка аргументов).
Нажатие скилла открывает карточку: описание, `argument-hint`, флаги `--x`, найденные в тексте
SKILL.md регуляркой (формальной схемы аргументов у скиллов нет, поэтому подписаны как
«упомянутые»). «Запустить» кладёт в очередь ровно слэш-команду, «С аргументами» — через
`SkillLauncher.Expect` запоминает команду за пользователем, и следующий его текст
`SkillArgumentsTextHandler` (зарегистрирован в `SettingsModule`, то есть раньше `ChatModule`)
превращает в `/команда текст`; «отмена» снимает ожидание. Запуски считаются в `state.json`
(`SkillUsage`, пишут `SkillLauncher` и `ChatEnqueueTextHandler` для набранных руками
слэш-команд, суффикс `@бот` отрезается), и первой группой экран выносит «⭐ Частые» — до пяти
самых запускаемых; в группах источников порядок остаётся алфавитным.

Сессии Claude Code ключуются **нормализованным путём проекта** (`ProjectCatalog.Normalize`):
`--resume` работает только в той папке, где сессия создана. Переключение репозитория из меню меняет
и активную сессию. `ChatWorker` фиксирует сессию и `ProjectPath` в `AgentRunRequest` до запуска —
иначе переключение посреди работы развело бы рабочий каталог процесса и проект, которому
пишется сессия.

Id новой сессии выдаёт **шлюз** (`AgentRunRequest.NewSessionId` → `--session-id <uuid>`) и
регистрирует её, как только бэкенд сообщит `IAgentRunObserver.SessionStarted` — сразу после
старта процесса, не дожидаясь ответа CLI: иначе `/stop`, таймаут или падение первого запуска
теряли бы ветку целиком. Продолжение идёт через `--resume`. Сессия сбрасывается только когда CLI
прямо говорит, что не нашёл её (`LooksLikeMissingSession` в бэкенде → `AgentRunResult.SessionLost`),
и только в `ChatWorker.SettleSession` через `TrySetSessionId(onlyIfActive)`, чтобы не перетереть
`/new` или смену сессии, сделанные во время запуска.

### Аудит

`IAuditLog.Write(AuditEvent.Now(kind, summary, userId, chatId, project, session, outcome))` —
короткая строка «кто, куда, что», без секретов и полных текстов (не длиннее 200 символов).
Текст пользователя — промпт, аргументы команды, свободный ответ на вопрос агента — идёт в
журнал только превью через `Text.Preview` (80 символов, одна строка): туда могли вставить токен.
Виды — константы `AuditKinds`: `access.rejected`, `message`, `run.start`/`run.end`, `approval`,
`question`, `settings`, `rules`, `session.reset`, `budget.refused`, `gateway`. Пишут: роутер
(доступ, команды), `ChatEnqueueTextHandler` (промпт), `ChatWorker` (запуски, бюджет),
`OperatorConsole` (решения по карточкам, вопросы, правила), экраны меню через `SettingsAudit.Changed`,
`ChatWorker` (сброс сессии). Смотреть — `/audit [n]` или файл. Это не замена `ILogger`:
в аудит идёт то, за что отвечает человек, в лог — то, что нужно для отладки.

### Деньги и лимиты тарифа

Три независимых ограничителя, все проверяются в `ChatWorker.ProcessAsync` перед запуском:

1. `DailyBudgetUsd` — сумма из `state.json` за календарный день пользователя;
2. `RunBudgetUsd` → `--max-budget-usd`, но не больше остатка дневного бюджета;
3. `IAgentLimits` (`ClaudeLimits`) — тарифные окна (`five_hour`, `seven_day`, `seven_day_<модель>`).

Суммы в чат не выводятся: на подписке они ничего не значат, а кредиты запрещены. Вместо них
`IAgentLimits.ShortSummaryAsync` (сводка меню, `/status`) и `RemainingLinesAsync` (экран
статистики) показывают остаток окон. Ради этого `ISettingsScreen.RenderAsync` асинхронный —
отрисовка ходит в сеть, пусть и через кэш. Оценка стоимости продолжает копиться в `state.json`:
на ней держится `DailyBudgetUsd`, единственное место, где суммы ещё видны.

`ClaudeLimits` ходит в **недокументированный** `api.anthropic.com/api/oauth/usage` с токеном
подписки из `~/.claude/.credentials.json`. Эндпоинт требует правдоподобный User-Agent, отвечает 429
на частый опрос (отсюда кэш на 3 минуты) и может исчезнуть в любой версии — при любой ошибке запуск
**пропускается**, а не блокируется, иначе шлюз замолчал бы целиком.

Кредиты («extra usage») агенту тратить запрещено: `ClaudeBackend` ставит процессу
`DISABLE_EXTRA_USAGE_COMMAND=1`, а при обрыве по лимиту `ChatWorker` снимает всю очередь —
следующие задачи упёрлись бы в тот же лимит. Переменные `Gateway__*` дочерний процесс не видит:
хост удаляет их из своего окружения в `ValidateStartup`, когда конфиг уже прочитан, — так это
не нужно помнить каждому бэкенду.

### Вывод в Telegram

`TelegramFormatter` переводит markdown в подмножество HTML (`<b> <i> <s> <code> <pre> <a>
<blockquote>`) и режет под лимит 4096 — резать нужно **исходный markdown до конвертации**, иначе
рвутся теги. Того, чего в этом подмножестве нет, форматтер добивается текстом: маркеры списка
становятся `•` и `◦` по уровню вложенности, `---` — линией из символов, а markdown-таблица
выравнивается пробелами и уходит блоком `<pre>` (иначе столбцы расползаются). Курсив разбирается
только у `*`, вплотную к содержимому и на границе слова: иначе `*.cs` и `2 * 3` становились
курсивом; `_` не разбирается вовсе — он частый гость в `__init__.py` и `snake_case`. Блок кода
длиннее лимита уходит файлом. При отказе Telegram разбирать разметку `ChatWorker` шлёт тот же текст
без `ParseMode`. Суммы, токены и время форматирует `DisplayFormat` (extension members C# 14).

Карточки подтверждений собираются через `EscapeCapped` с побюджетными лимитами на каждый фрагмент:
длинная команда иначе переполнит сообщение, отправка упадёт, а исключение превратится в отказ.

## Что стоит держать в голове

- Шлюз **не подключается** к сессии, открытой в VS Code. Это отдельная параллельная сессия на той же
  папке: общие `CLAUDE.md`, `.claude/settings.json`, хуки и MCP проекта, но своя история диалога.
- `--bare` использовать нельзя: он не читает `~/.claude` и ломает OAuth-логин по подписке.
- `claude.exe` ищет `ClaudeCliLocator` (`IAgentBackend.Probe`): `Gateway:Claude:Executable` → стандартные пути → PATH → бинарник внутри
  расширения VS Code. Последний — только чтобы шлюз завёлся на машине без своего CLI: путь
  содержит версию расширения и исчезает при его обновлении, поэтому на такой находке пишется
  предупреждение. Штатно нужен отдельный CLI (`irm https://claude.ai/install.ps1 | iex` →
  `~/.local/bin/claude.exe`) — он обновляется сам и переживает переустановку VS Code.
  Найденный путь кешируется, но перепроверяется перед каждым запуском: пропавший бинарник
  ищется заново, а не валит каждое сообщение до перезапуска шлюза. Версию (`--version`)
  `ValidateStartup` пишет в лог — контракт разбора JSON держится на поведении конкретной версии.
- Барьеры аутентификации: `AllowedUserIds` + только личные чаты; Kestrel слушает только `127.0.0.1`,
  MCP-эндпоинт требует токен в заголовке. Веб-монитор на своём порту без токена — поэтому
  он только читает.
- Аргументы CLI собираются через `ProcessStartInfo.ArgumentList` — не склеивайте командную строку
  руками.
- `Channel.CreateUnbounded` в `ChatWorker` намеренно без `SingleReader`: с ним `Reader.Count` бросает
  `NotSupportedException`, и `/status` падает.
- `McpConfigFile` и `SessionStore` — единственные классы с классическим конструктором: у обоих
  побочный эффект при создании (запись/чтение файла), который должен случиться один раз до старта.
  `McpConfigFile` создаётся, когда `ValidateStartup` запрашивает `IAgentBackend`, — до `MapFeatures`.
- Диагностика C#-LSP не подхватывает `GlobalUsings.cs` после перемещения файлов и сыплет ложными
  `CS0246`. Источник правды — `dotnet build`.
- `JsonElement.TryGetDouble`/`TryGetInt64` не спасают от `null`: на элементе не-числе они бросают
  `InvalidOperationException`. Поля недокументированного ответа лимитов читаются через
  `ClaudeLimits.Number` с проверкой `ValueKind`, иначе `utilization: null` возвращается
  пользователю как «Внутренняя ошибка шлюза».
- Правя русские тексты и windows-пути скриптом из Bash, помните про escape-последовательности:
  обратный слэш съедается и heredoc, и строкой python. `\a` сократил `audit\audit-…` в README,
  `\n` внутри правки стал настоящим переводом строки, `\repos` — возвратом каретки посреди
  строки; на глаз это неотличимо от опечатки. Надёжнее Edit/Write; если всё же скриптом —
  правьте по индексам строк, а результат проверяйте `grep … | cat -v`.
- Исходники — UTF-8 **без BOM** (`utf-8-sig` при записи добавит его молча). Лишний BOM виден
  в диффе как правка первой строки файла.
- Комментарии объясняют не что делает код, а какой отказ он предотвращает: «иначе `/stop` уйдёт
  в ожидающий свободный ответ». Комментарий-пересказ строки здесь лишний.
- Из сессии через Telegram `AskUserQuestion` и `ExitPlanMode` ждут ответа не дольше 5 минут
  (idle-таймаут MCP), потом падают: без ответа берите рекомендуемый вариант и идите дальше.
  Длинный однострочник PowerShell на подтверждении легко отклонить не глядя — многошаговую
  проверку кладите в скрипт в scratchpad и запускайте `pwsh -File`.
- `install-autostart.ps1` ставит задачу Планировщика от текущего пользователя без повышения
  прав, а не службу Windows: OAuth-логин Claude Code лежит в `%USERPROFILE%\.claude`, под
  SYSTEM или другой учёткой он не найдётся. `-Uninstall` снимает задачу.

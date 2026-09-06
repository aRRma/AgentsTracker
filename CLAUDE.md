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

Тестового проекта нет. Всё, что трогает контракт с CLI (аргументы запуска, разбор JSON, ответ
`ClaudePermissionTool`), проверяется руками: запустить шлюз, выполнить `claude -p …` с теми же
флагами против его MCP-эндпоинта и посмотреть лог.

**Пробный экземпляр.** Конфиг перекрывается переменными окружения (`$env:Gateway__ProjectPath`,
`Gateway__AllowedUserIds__0`), поэтому разовый прогон — exe с поддельным `Gateway__BotToken`
и **своими** `Gateway__McpPort`/`Gateway__MonitorPort`; с поддельным токеном он живёт ~минуту
(бот один, `getUpdates` отдаёт 409) — хватает, чтобы дёрнуть эндпоинты монитора. Но
`mcp-gateway.json` в папке данных он перезапишет в любом случае (`%LOCALAPPDATA%` берётся не
из переменной), и рабочий шлюз после пробы **надо перезапустить** — иначе каждый вызов
инструмента падает с «MCP tool mcp__tg__approve not found».

Дымовой прогон после правок инфраструктуры: скрипт в scratchpad, `pwsh -File`; через ~8 с
проверить `/api/snapshot` (`agent`, `cliVersion`), `/api/limits`, 404 на `/` порта MCP и 401
на `POST /mcp` без токена — с заголовками `Content-Type: application/json` и
`Accept: application/json, text/event-stream`, иначе придёт 415.

**Сборка при запущенном шлюзе** падает с `MSB3021` (exe заблокирован). Собирайте в другую
папку — `dotnet build src\AgentsTracker.Gateway -o <папка>`, именно проект: `-o` для `.slnx`
даёт NETSDK1194. Проверить класс без остановки шлюза: из временного консольного проекта
сослаться на **готовую dll** (`<Reference>` с `HintPath`), не на csproj — `ProjectReference`
полез бы пересобирать занятый `bin\Debug`.

**Перезапуск** (на машине разработки exe запущен вручную из `bin\Debug`, задачи Планировщика
обычно нет; путь покажет `Get-Process AgentsTracker.Gateway`):

```powershell
git status                                                # чужие незакоммиченные правки могут не собираться
Get-Process AgentsTracker.Gateway | Stop-Process -Force   # иначе MSB3021
dotnet build src\AgentsTracker.Gateway
Start-Process src\AgentsTracker.Gateway\bin\Debug\net10.0\AgentsTracker.Gateway.exe
```

- Рабочая папка не важна: `Program.cs` закрепляет content root за папкой exe
  (`ContentRootPath = AppContext.BaseDirectory`); без этого exe из другой папки молча терял
  `appsettings.json` и тонул в логах `Microsoft.AspNetCore`. Проверка — строка
  `Content root path` в логе.
- Если `main` не собирается из-за чужих правок, а шлюз уже остановлен: собрать чистый HEAD в ту
  же папку — `git worktree add --detach ..\AgentsTracker-run HEAD`,
  `dotnet build ..\AgentsTracker-run\src\AgentsTracker.Gateway -o src\AgentsTracker.Gateway\bin\Debug\net10.0`,
  `git worktree remove --force ..\AgentsTracker-run`.
- Из сессии, запущенной самим шлюзом (из Telegram), перезапускать нельзя: `Stop-Process` убьёт
  и текущий `claude -p`, а отложенный `schtasks /SC ONCE` не срабатывал. Дайте пользователю
  команды выше и попросите выполнить руками.

Консоль отдаёт русский текст в cp866 — лог смотрите в PowerShell (`iconv` в Git Bash нет).

**Крупную задачу** (несколько фаз, перезапуски по ходу) ведите в worktree: шлюз запущен из
`bin\Debug` этой папки, переключение ветки подменит исходники под работающим процессом, а
сломанная сборка лишит возможности его перезапустить.

```powershell
git worktree add ..\AgentsTracker-<задача> -b <ветка>   # main остаётся папкой, из которой запущен шлюз
```

Работайте в новой папке (из чата — `/project`, у неё свои сессии), в `main` вливайте готовое.
В `main` параллельно работают другие сессии — перед слиянием и остановкой шлюза `git status`.
Мелкие правки в один-два коммита — прямо в `main`.

## Как вносить изменения

**Команда чата:** класс с `ITelegramCommandHandler` в папке фичи + `services.AddSingleton<
ITelegramCommandHandler, …>()` в её `*Module` + запись в `BotCommandsCatalog` (кнопка «Меню»)
и в тексте `HelpCommandHandler`. Занятые: `/start /help` (Help), `/new /stop` (Chat), `/rules`
(Approvals), `/audit` (Audit), `/menu /settings /status /sessions /agent /model /effort /mode
/skills /project /usage` (Settings). У кнопки «Меню» публикуются только экраны — `/new /stop
/model /effort /mode` работают текстом, но в списке их нет (то же есть кнопками на «Сессиях» и
«Агенте»). Порядок в списке, справке и `RootScreen` один — по частоте: статус и сессии, агент
и скиллы, репозиторий, статистика и журналы. Прочие слэш-команды уходят в CLI как есть.

**Экран настроек:** класс с `ISettingsScreen` в `Features/Settings/Screens/`, регистрация в
`SettingsModule`, кнопка в `RootScreen`. `RenderAsync(userId, ct)` асинхронный ради лимитов;
экрану без сети хватает `Task.FromResult(Render())`. `RenderFramesAsync` — необязательные кадры
(координатор правит сообщение на каждом, паузу держит экран): так `StatusScreen` «заполняет»
шкалы `LimitBars` — три кадра по 350 мс, чаще нельзя, Telegram отвечает 429, координатор один
раз пережидает `RetryAfter`. Модель, effort и режим — один `AgentScreen` с аргументами
`model:…`/`effort:…`/`mode:…`; текстовые `/model x` и т.п. идут через его `Apply`. `Apply`
получает `chatId`: экран может ставить задачу в очередь `ChatWorker` (`SkillsScreen`), и ответ
должен уйти нажавшему. Позиция списка (группа, страница, карточка) — в `ScreenNavigation` на
каждого пользователя: экраны синглтоны, а `AllowedUserIds` допускает нескольких; `Open(userId)`
сбрасывает позицию при входе из корня. Страницы и ключи callback_data — общие
`SettingsKeyboard.Page`/`Key12`; однобуквенные префиксы аргументов экрана не должны быть
hex-символами, иначе спутаются с ключом.

**Фича:** папка в `Features/` с `*Module` и запись в списке модулей в `Program.cs`;
`ChatModule` остаётся последним.

**Агент (Codex, Cursor):** проект `src/AgentsTracker.Agents.<Имя>` со ссылкой на
`Agents.Abstractions`, в нём `IAgentBackendModule`, регистрирующий `IAgentBackend`,
`IAgentLimits` (или `NoAgentLimits`), `IAgentSkillCatalog` (или `NoAgentSkills`) и свой канал
подтверждений через `IOperatorConsole`; строка в списке `agents` в `Program.cs` и ссылка в
`AgentsTracker.Gateway.csproj`. `grep -rn Claude src/AgentsTracker.Gateway --include=*.cs`
должен находить лишь `Program.cs` и комментарии. Настройки агента — в подсекции
`Gateway:<Id>` (`Gateway:Claude`), хост её не читает.

**Эндпоинт монитора:** `api.MapGet` в `MonitorModule.MapEndpoints` (группа уже фильтрует порт),
только чтение, `Results.Json(..., Json)`; секция — в `index.html`.

**Ключ конфига:** свойство в `GatewayOptions` (+ `Validate`), значение по умолчанию в
`appsettings.json`, пример `"//Ключ": "…"` в `appsettings.Local.example.json`, строка в README
«Основные настройки». Ключ агента — в `ClaudeOptions` и `Gateway:Claude`.

## Архитектура

### Слои и слайсы

```
src/AgentsTracker.Agents.Abstractions/   контракты агента, без Telegram и без конкретного CLI:
  IAgentBackend         Id, DisplayName, Capabilities, Probe() (бинарник и версия), RunAsync(AgentRunRequest, IAgentRunObserver)
  AgentRun.cs           AgentRunRequest (промпт, папка, сессия, модель, effort, режим, бюджет, таймаут),
                        IAgentRunObserver (SessionStarted, Activity), AgentRunResult (+SessionLost, RateLimited)
  AgentCapabilities     AgentSetting для модели, effort (null — не умеет), режима разрешений; SupportsRunBudget
  IOperatorConsole      что агент просит у человека: ApproveAsync → ApprovalDecision, AskAsync → QuestionResult;
                        PersistentRule — правило «всегда» в формате агента
  IAgentLimits          лимиты тарифа (+NoAgentLimits); IAgentSkillCatalog — слэш-команды (+NoAgentSkills)
  IAgentBackendModule   AddServices + MapEndpoints; AgentHost — папка данных, порт и прокси от хоста
  RunActivity, RunUsage, Text — общие модели и обрезка текста
src/AgentsTracker.Agents.Claude/         Claude Code за этими контрактами:
  ClaudeAgentModule     регистрация, MapMcp(/mcp) с фильтром токена, Dispose McpConfigFile
  ClaudeBackend         процесс claude -p: аргументы, stream-json, «сессия не найдена», лимит тарифа
  ClaudeCapabilities, ClaudeOptions (Gateway:Claude — Executable, BuiltInSkills),
  ClaudeCliLocator, ClaudeCliJson, ClaudeStreamEvent, ClaudeLimits, ClaudeSkillCatalog
  Mcp/                  McpConfigFile — mcp-gateway.json с токеном; ClaudePermissionTool — payload CLI → IOperatorConsole → JSON
src/AgentsTracker.Gateway/
  Program.cs            список IAgentBackendModule (выбор по Gateway:Agent) и IFeatureModule
                        → AddGatewayConfiguration → AddGatewayInfrastructure → ValidateStartup → MapFeatures
  GlobalUsings.cs       Agents, Domain, Infrastructure, .Configuration, .State, IOptions — доступны везде
  Domain/               чистые модели без I/O и DI: GatewayState (state.json), AuditEvent
  Infrastructure/       общее для фич (AppPaths, GatewayInfrastructure — в корне):
    Configuration/      GatewayOptions (+Validate, +ValidateFor(capabilities)), ProjectCatalog (Normalize/Same — ключ сессий)
    State/              SessionStore — state.json под Lock, атомарная запись
    Telegram/           TelegramBotService (роутер), StartupNotice, TelegramFormatter, DisplayFormat, BotCommandsCatalog,
                        Dispatch/ — ITelegramCommandHandler / ITelegramCallbackHandler / ITelegramTextHandler
    Audit/              IAuditLog, JsonlAuditLog — журнал «кто, куда, что»
    Monitoring/         RunMonitor — живое состояние и подписка; RingBufferLog — хвост ILogger для монитора
    Security/           DPAPI-шифрование конфига, ACL папки данных, protect-secrets
    Modules/            IFeatureModule — AddServices + MapEndpoints
  Features/             вертикальные слайсы, каждый со своим *Module:
    Approvals/          OperatorConsole (IOperatorConsole), ApprovalBroker, ApprovalCardRenderer, /rules
    Chat/               ChatWorker (очередь, запуск, сессии), RunStatusMessage (живой статус), /new /stop, fallback текста
    Settings/           SettingsMenuCoordinator + Screens/*Screen, LimitBars; /menu /status /sessions /agent /skills /project /usage
    Help/ Audit/        /start /help; /audit
    Monitor/            MonitorModule (/, /api/*) + index.html (EmbeddedResource)
```

Правила разложения: `Domain` и `Agents.Abstractions` не открывают файлы и не ходят по сети;
`Gateway` знает о конкретном агенте только в `Program.cs`, остальное — через
`IAgentBackend`/`IAgentLimits`/`IAgentSkillCatalog`/`AgentCapabilities`; бэкенд не знает о
Telegram и `GatewayOptions` (ему даётся `AgentHost`); `Infrastructure` не знает о фичах (кроме
контрактов `Dispatch`); фича зависит от фичи только через публичный сервис
(`Settings` → `ChatWorker.IsBusy`, `Approvals` → `SettingsMenuCoordinator.CallbackPrefix`).

### Диспетчер Telegram

`TelegramBotService` проверяет `AllowedUserIds` и `ChatType.Private`, потом раздаёт обновления
обработчикам из DI. Порядок в `HandleTextAsync` принципиален:

1. слэш-команда ищется в `ITelegramCommandHandler.Commands` — **до** всего остального, иначе
   `/stop` уйдёт в ожидающий свободный ответ и прервать зависший запуск будет нечем. Дубликат
   команды у двух фич роняет старт;
2. цепочка `ITelegramTextHandler` в порядке регистрации модулей: `ApprovalTextHandler`
   (`broker.TryConsumeText` — причина отказа или ответ на `AskUserQuestion`) →
   `SkillArgumentsTextHandler` (аргументы после кнопки «С аргументами») →
   `ChatEnqueueTextHandler` (всегда `true`). Поэтому `ChatModule` в `Program.cs` **последний**.
   Неизвестные слэш-команды попадают сюда — это команды самого Claude Code.

Callback-и: `CanHandle` — префикс `cfg:` у меню, всё остальное (hex-id запроса) у
`ApprovalCallbackHandler` → `ApprovalBroker`. `ActiveChatId` брокера выставляет `ChatWorker`
перед самым запуском, не обработчик сообщения: иначе карточки идущего запуска ушли бы в чат
другого пользователя.

### Кольцо «шлюз → CLI → шлюз»

Шлюз одновременно **запускает** агента и **обслуживает** его:

```
Telegram ──▶ TelegramBotService ──▶ ChatWorker ──▶ IAgentBackend (ClaudeBackend) ──▶ claude.exe -p
                    ▲                                                                     │
                    │       карточка с кнопками                                           │ нужно разрешение
                    └── ApprovalBroker ◀── OperatorConsole ◀── ClaudePermissionTool ◀── MCP http://127.0.0.1:<порт>/mcp
                        (Gateway)          (IOperatorConsole)  (Agents.Claude)            Authorization: Bearer <токен>
```

Граница — `IOperatorConsole`: хост показывает карточку, помнит правила «всегда» и пишет аудит;
как запрос доставлен и в каком JSON вернуть решение — знает только бэкенд. `McpConfigFile` при
старте генерирует токен, пишет `mcp-gateway.json` с заголовком `Authorization` и удаляет файл
при остановке; `/mcp` монтируется с фильтром `McpConfigFile.Authorizes`; CLI получает
`--mcp-config` и `--permission-prompt-tool mcp__tg__approve`. Имя сервера и инструмента —
константы `McpConfigFile`, `[McpServerTool]` берёт ту же константу: правя одну сторону,
проверяйте вторую. README — инструкция для пользователя без устройства; меняя защиту эндпоинта
или папку данных, проверьте его раздел «Безопасность».

### Контракт подтверждений (проверен на живом CLI, схема не задокументирована)

CLI зовёт инструмент с `{"tool_name":…,"input":{…},"tool_use_id":…}`. Ответ — JSON-строка:
`{"behavior":"allow","updatedInput":{…}}` — `updatedInput` **обязателен**, без него CLI
отклоняет вызов; или `{"behavior":"deny","message":"…"}`. `ClaudePermissionTool` читает поля
защитно (`Read(...)` перебирает snake_case/camelCase); сырой payload — только на Debug: на
Information для Edit/Write это содержимое файлов. `AskUserQuestion` приходит в тот же
инструмент и требует `updatedInput` с исходным `questions` и `answers` (ключ — текст вопроса);
в хост уходит как `IOperatorConsole.AskAsync`.

Кнопка «Всегда» двояка: если CLI прислал `permission_suggestions` с `destination: localSettings`,
правило пишет **сам CLI** в `.claude/settings.local.json` проекта (обычно префиксное), карточка
показывает именно его, а в хост оно приходит как `PersistentRule.Raw` и возвращается без
изменений в `updatedPermissions`; иначе шлюз запоминает точную сигнатуру в `state.json`
(`AlwaysAllowByProject`, ключ — нормализованный путь проекта), видно в `/rules`. Правила шлюза
действуют только в своём проекте: `git push --force` из одного репозитория не должен молча
проходить в остальных.

Одобрять команду с невидимым хвостом нельзя: когда `ApprovalCard` что-то обрезал (команда,
стороны правки, остальные правки `MultiEdit`, хвост `Write`), `OperatorConsole` перед карточкой
шлёт полный текст файлом (`ApprovalBroker.SendAttachmentAsync`), карточка предупреждает
«показано не всё». Карточки собираются через `EscapeCapped` с лимитом на каждый фрагмент:
переполненное сообщение упало бы при отправке, а исключение стало бы отказом.

Самовыдача прав проверена на CLI 2.1.x: `Write` в `.claude/settings.local.json` отклоняется
даже в `acceptEdits` (виден в `permission_denials`). После обновления CLI перепроверить тем же
запуском `claude -p … --permission-mode acceptEdits --output-format json` во временной папке.

`ApprovalBroker` держит вызов MCP на `TaskCompletionSource` до нажатия и возвращает
`ChoiceResult` (ключ + кто нажал). `WaitAsync` различает таймаут и отмену: `/stop` должен
бросать `OperationCanceledException`, а не выглядеть как «не ответил вовремя». Таймауты:
`ApprovalTimeoutMinutes` (15) — карточка, `RunTimeoutMinutes` (60) — весь `claude -p`; оба
1..1440. Первый выше ~5 минут бессмыслен: раньше сработает idle-таймаут MCP на стороне CLI.

### Статус запуска и поток событий

CLI запускается с `--output-format stream-json --verbose` (без `--verbose` поток в `-p` не
пишется). `ClaudeBackend.ReadStreamAsync` разбирает stdout построчно (`ClaudeStreamEvent.Classify`),
вызовы инструментов отдаёт в `IAgentRunObserver.Activity`, остальное отбрасывает — в долгом
запуске это мегабайты. Итог — последняя строка `"type":"result"` той же формы, что
`--output-format json`; «шум» (не-JSON строки вроде баннера обновления) идёт в текст ошибки.
Текст ошибки CLI часто оставляет в stderr, а `result` присылает пустым (так с «No conversation
found» при битом `--resume`) — проверка сброса сессии и лимита смотрит и в stderr.

Идущий запуск записан в `state.json` (`GatewayState.ActiveRun`): `ChatWorker` ставит `BeginRun`
перед запуском и `EndRun` в `finally`. Запись на месте при старте — прошлый экземпляр умер
посреди работы: `StartupNotice` шлёт «🔌 Шлюз запущен» всем из `AllowedUserIds`, в чат
прерванного запуска — «прерван, напишите „продолжай“», пишет `run.end` с исходом `interrupted`.
Иначе перезапуск выглядел как молчание. Пользователю, который ещё не писал боту, Telegram не
даёт отправить первым — ошибка глотается на Debug.

В чате — `RunStatusMessage`: одно сообщение «Работаю…», раз в 4 с редактируется (время,
счётчик вызовов, три последних шага, сабагенты с `↳`), иначе долгий запуск неотличим от
зависшего шлюза. `Report` из потока stdout только запоминает, сеть — в своём цикле;
`DisposeAsync` дожидается цикла (≤10 с), иначе правка догоняла бы удаление. Аргумент
инструмента в статусе один и короткий (`ClaudeStreamEvent.Describe`): полный ввод Edit/Write —
содержимое файла.

### Веб-монитор

`Features/Monitor/` — страница на **отдельном** порту `Gateway:MonitorPort` (5100, `0`
выключает; `Validate` не даёт совпасть с `McpPort`). Kestrel слушает оба порта одним
конвейером, поэтому группа эндпоинтов фильтрует `Connection.LocalPort` — иначе страница
открылась бы и на порту MCP (`/` там отдаёт 404). Авторизации нет намеренно, только loopback —
поэтому эндпоинты **только читают**: `/stop` и смена проекта остаются в Telegram, где есть
`AllowedUserIds` и аудит. Эндпоинты: `/`, `/api/snapshot`, `/api/events` (SSE), `/api/limits`,
`/api/runs?project=&limit=`, `/api/stats`, `/api/stats.csv`, `/api/audit?count=`,
`/api/log?count=&level=`.

«Что сейчас» — `RunMonitor`: `ChatWorker` сообщает очередь, старт, шаги (тот же callback, что у
`RunStatusMessage`) и финиш; `OperatorConsole` — ожидание карточки через
`using monitor.Approval(tool, brief)`, brief — короткая строка, не полный ввод. Карточек в снимке
список: CLI зовёт инструмент параллельно на несколько `tool_use` одного хода. Шагов хранится
300, `DroppedSteps` — разница со счётчиком. `Changes()` — канал ёмкостью 1 с вытеснением:
медленный браузер получает последнее состояние, а не очередь устаревших. `/api/events` шлёт
снимок при подключении, далее по изменениям, между ними `ping` раз в 5 с — без него страница
не отличит тишину от упавшего шлюза.

История — `GatewayState.RecentRuns` (200, `SessionStore.RecordRunOutcome` после запуска;
исход определяет `ChatWorker`, расход — из `AgentRunResult.Usage`). `/api/stats.csv` — `;`,
BOM и десятичная запятая: иначе Excel на русской локали читает дробные как текст. Лимиты —
`IAgentLimits.GetAsync` (у Claude кэш 3 мин, страница опрашивает раз в минуту); лог —
`RingBufferLog` (500 записей, Information+, зарегистрирован как `ILoggerProvider`).

`index.html` — один файл без сборки и CDN, `EmbeddedResource`; данные `/api/*` в camelCase,
кириллица без `\u`. Смотреть вёрстку без шлюза: копия страницы и `scripts\monitor-mock.js` в
scratchpad, `<script src="monitor-mock.js">` перед основным скриптом (мок подменяет `fetch`
данными `/api/*` и `EventSource` снимком, `?state=run|wait|idle`; новый эндпоинт — добавить в
`routes`), `python -m http.server <порт> --bind 127.0.0.1` из scratchpad (Playwright не
открывает `file://`). Доступность — `browser_snapshot` Playwright MCP (дерево ролей и имён);
тёмная тема только скриптом Playwright через `page.emulateMedia({colorScheme:'dark'})`;
скриншоты падают в корень репозитория, они в `.gitignore`. Строй страницы — рейка слева (состояние, лимиты) и лента
шагов справа; цвет только как сигнал (`--run`/`--wait`/`--fail`), шрифты системные Windows.

Скрипт: `renderLive` перестраивает DOM только по кадру SSE, а секундомер, аптайм и «ждёт N с»
(`[data-since]`) тикает `tick()` через `textContent` — перерисовка `innerHTML` раз в секунду
сбрасывала выделение текста в ленте и прыгала скроллом; лента прокручивается вниз только при
новых шагах (`tapeKey`). В скрытой вкладке таймеры молчат, на возврат — `loadAll()`. Проверено
по modern-web-guidance: `<h1>` в рейке, селекты с `label.visually-hidden`, `caption`/`scope` у
таблиц, график `role="img"` + таблица в `<details>`, `role="status"` только на состоянии и
соединении (не на часах — спам), `tabindex="0"` у прокручиваемых областей, размеры в `rem`,
`color-scheme: light dark`, шкалы и лампа с рамкой в `forced-colors`. Селекты и ссылки живут в
`.band-head` рядом с `<h2>`, не внутри него.

### `--permission-mode` передаётся всегда

Без явного флага действует `permissions.defaultMode` из `~/.claude/settings.json` пользователя,
у него там `auto` — решает классификатор, и кнопки в чате не появляются. Не убирайте аргумент из
`ClaudeBackend.BuildArguments`. Списки значений объявляет бэкенд в `AgentCapabilities`:
`PermissionMode.Selectable` (`plan`/`default`/`acceptEdits`/`auto`) — что можно переключать из
чата; `dontAsk` и `bypassPermissions` исключены намеренно — полное снятие подтверждений остаётся
правкой конфига на самой машине. `Effort` может быть `null` — `RootScreen` не показывает
кнопку, `/effort` отвечает отказом. Эти значения проверяет `ValidateFor(capabilities)` в
`ValidateStartup`, а не `GatewayOptions.Validate` (агент ещё не выбран).

### Состояние, секреты и наслоение настроек

`%LOCALAPPDATA%\AgentsTracker\` (`AppPaths.DataDirectory`, ACL — владелец и SYSTEM):
`state.json` (атомарно), `mcp-gateway.json`, `appsettings.Local.json` с секретами,
`audit\audit-ГГГГ-ММ.jsonl`.

Конфиг слоями: `appsettings.json` → `appsettings.Local.json` рядом с приложением (IDE) → тот же
файл в папке данных (боевой) → переменные окружения. `dpapi:…` расшифровывается при загрузке
(`ProtectedJsonConfigurationProvider`); `protect-secrets` шифрует `BotToken` и `Proxy` и
переносит файл в папку данных. `publish\` секретов не содержит.

Выбор из чата (`SessionStore`) лежит поверх конфига (`GatewayOptions`) — `EffectiveModel`,
`EffectivePermissionMode`, `EffectiveEffort`, `ProjectPath`. Значение, совпадающее с конфигом,
хранится как `null`: иначе правка конфига оказалась бы молча перекрыта старым выбором.

`ProjectCatalog`: список из `Gateway:Projects`, иначе обход `Gateway:ProjectsRoot` до
`ProjectsRootDepth` (спуск прекращается на папке с признаком проекта), иначе соседи
`ProjectPath`. `Grouped` раскладывает по папкам-владельцам, `ProjectScreen` выбирает в два шага
(папка → репозиторий) страницами по 12 — репозиториев больше, чем влезает в клавиатуру. Текущий
проект первым в своей группе, после выбора страница сбрасывается на первую: иначе отметка `▶`
оказывалась бы за пределами экрана.

`/skills` — `IAgentSkillCatalog` (`ClaudeSkillCatalog`) собирает то же, что видит CLI:
`skills/*/SKILL.md` и `commands/**/*.md` из `.claude` проекта и `~/.claude`, плюс включённые
плагины (`~/.claude/plugins/installed_plugins.json`; `enabledPlugins` наслаиваются профиль →
`.claude/settings.json` → `settings.local.json`, без записи — включён). `user-invocable: false`
в списке нет. Обход кэшируется на 5 с: нажатие в меню — это `Apply` и `Render` подряд.
Встроенные скиллы CLI перечислить нельзя — группа «Встроенные» из `Gateway:Claude:BuiltInSkills`
(дефолт в `ClaudeOptions` под CLI 2.1.x, после обновления CLI — конфигом без пересборки).
«С аргументами» — `SkillLauncher.Expect` запоминает команду за пользователем, следующий текст
`SkillArgumentsTextHandler` превращает в `/команда текст`. Запуски считаются в `SkillUsage`
(суффикс `@бот` отрезается), «⭐ Частые» — до пяти самых запускаемых.

Сессии ключуются **нормализованным путём проекта** (`ProjectCatalog.Normalize`): `--resume`
работает только в папке, где сессия создана; смена репозитория меняет и активную сессию.
`ChatWorker` фиксирует сессию и `ProjectPath` в `AgentRunRequest` до запуска — иначе
переключение посреди работы развело бы рабочий каталог и проект сессии. `/sessions` показывает
до 8 последних; кнопка несёт `ShortId` (8 символов), а не номер в списке: завершившийся между
отрисовкой и нажатием запуск сдвинул бы номера.

Id новой сессии выдаёт **шлюз** (`NewSessionId` → `--session-id <uuid>`) и регистрирует её по
`IAgentRunObserver.SessionStarted` — сразу после старта процесса: иначе `/stop`, таймаут или
падение первого запуска теряли бы ветку. Сессия сбрасывается только когда CLI прямо говорит,
что не нашёл её (`LooksLikeMissingSession` → `AgentRunResult.SessionLost`), и только в
`ChatWorker.SettleSession` через `TrySetSessionId(onlyIfActive)` — чтобы не перетереть `/new`
или смену сессии, сделанные во время запуска.

### Аудит

`IAuditLog.Write(AuditEvent.Now(kind, summary, userId, chatId, project, session, outcome))` —
«кто, куда, что», без секретов и полных текстов (≤200 символов). Текст пользователя (промпт,
аргументы, свободный ответ агенту) — только превью `Text.Preview` (80 символов): туда могли
вставить токен. Виды — `AuditKinds`: `access.rejected`, `message`, `run.start`/`run.end`,
`approval`, `question`, `settings`, `rules`, `session.reset`, `budget.refused`, `gateway`.
Экраны меню пишут через `SettingsAudit.Changed`. Смотреть — `/audit [n]`. Это не замена
`ILogger`: в аудит идёт то, за что отвечает человек, в лог — то, что нужно для отладки.

### Деньги и лимиты тарифа

Три ограничителя, все проверяются в `ChatWorker.ProcessAsync` перед запуском:
`DailyBudgetUsd` (сумма из `state.json` за календарный день), `RunBudgetUsd` →
`--max-budget-usd` (не больше остатка дневного), `IAgentLimits` — тарифные окна (`five_hour`,
`seven_day`, `seven_day_<модель>`).

Суммы в чат не выводятся: на подписке они ничего не значат, а кредиты запрещены. Вместо них
`IAgentLimits.ShortSummaryAsync` (сводка меню) и `ViewAsync` (окна с остатком 0..1 — шкалы
`LimitBars` на `/status` и статистике). Оценка стоимости копится в `state.json` только ради
`DailyBudgetUsd`.

`ClaudeLimits` ходит в **недокументированный** `api.anthropic.com/api/oauth/usage` с токеном из
`~/.claude/.credentials.json`: требует правдоподобный User-Agent, отвечает 429 на частый опрос
(кэш 3 мин), может исчезнуть в любой версии — при любой ошибке запуск **пропускается**, не
блокируется, иначе шлюз замолчал бы целиком. Поля читаются через `ClaudeLimits.Number` с
проверкой `ValueKind`: `TryGetDouble` на `null` бросает, и `utilization: null` уходил бы
пользователю как «Внутренняя ошибка шлюза».

Кредиты («extra usage») агенту запрещены: `ClaudeBackend` ставит `DISABLE_EXTRA_USAGE_COMMAND=1`,
при обрыве по лимиту `ChatWorker` снимает всю очередь. Переменные `Gateway__*` дочерний процесс
не видит — хост удаляет их из окружения в `ValidateStartup`, когда конфиг уже прочитан.

### Вывод в Telegram

`TelegramFormatter` переводит markdown в подмножество HTML (`<b> <i> <s> <code> <pre> <a>
<blockquote>`) и режет под 4096 — резать **исходный markdown до конвертации**, иначе рвутся
теги. Остальное добивается текстом: списки — `•`/`◦`, `---` — линия, таблица — выровненный
`<pre>`. Курсив только у `*` вплотную к содержимому и на границе слова (иначе `*.cs` и `2 * 3`
курсивились); `_` не разбирается вовсе (`__init__.py`, `snake_case`). Блок кода длиннее лимита
уходит файлом. При отказе Telegram разбирать разметку `ChatWorker` шлёт тот же текст без
`ParseMode`. Суммы, токены и время — `DisplayFormat` (extension members C# 14).

## Что стоит держать в голове

- Шлюз **не подключается** к сессии VS Code — это параллельная сессия на той же папке: общие
  `CLAUDE.md`, `.claude/settings.json`, хуки и MCP, но своя история.
- `--bare` нельзя: не читает `~/.claude`, ломает OAuth-логин по подписке.
- `claude.exe` ищет `ClaudeCliLocator`: `Gateway:Claude:Executable` → стандартные пути → PATH →
  бинарник расширения VS Code (только чтобы завестись; путь с версией расширения исчезает при
  обновлении — пишется предупреждение). Штатно — отдельный CLI (`irm https://claude.ai/install.ps1
  | iex`). Путь кешируется, но перепроверяется перед каждым запуском. Версию `ValidateStartup`
  пишет в лог — контракт разбора JSON держится на конкретной версии.
- Барьеры: `AllowedUserIds` + только личные чаты; Kestrel только `127.0.0.1`; MCP — токен в
  заголовке; монитор без токена — поэтому только читает.
- Аргументы CLI — через `ProcessStartInfo.ArgumentList`, не склеивайте строку руками.
- `Channel.CreateUnbounded` в `ChatWorker` без `SingleReader`: с ним `Reader.Count` бросает
  `NotSupportedException`, и `/status` падает.
- `McpConfigFile` и `SessionStore` — единственные с классическим конструктором: побочный эффект
  (запись/чтение файла) должен случиться один раз до старта. `McpConfigFile` создаётся, когда
  `ValidateStartup` запрашивает `IAgentBackend`, — до `MapFeatures`.
- Русские тексты и windows-пути правьте Edit/Write, не heredoc и не строками python из Bash:
  `\a`, `\n`, `\r` в путях съедаются молча и неотличимы от опечатки. Скрипт — в scratchpad через
  Write, запуск файлом, результат проверять `grep … | cat -v` и сборкой. Исходники — UTF-8
  **без BOM** (`utf-8-sig` добавит его молча). Сообщение коммита из нескольких абзацев — файлом
  в scratchpad через Write и `git commit -F <файл>`: `-F -` с here-string из инструмента
  PowerShell stdin не получает, и текст уходит как pathspec.
- Ревьюеру-сабагенту без Bash `git show`/`git diff` недоступны: давайте пути к старым версиям
  файлов, выгруженным в scratchpad (`git show <коммит>:<путь> > …`).
- Комментарии объясняют не что делает код, а какой отказ предотвращает; пересказ строки лишний.
- Из сессии через Telegram `AskUserQuestion` и `ExitPlanMode` ждут ≤5 минут (idle-таймаут MCP):
  без ответа берите рекомендуемый вариант. Длинный однострочник PowerShell на подтверждении легко
  отклонить не глядя — многошаговую проверку кладите в скрипт и запускайте `pwsh -File`.
- `install-autostart.ps1` ставит задачу Планировщика от текущего пользователя, а не службу:
  OAuth-логин лежит в `%USERPROFILE%\.claude`, под SYSTEM он не найдётся. `-Uninstall` снимает.

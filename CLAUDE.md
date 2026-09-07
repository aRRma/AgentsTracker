# CLAUDE.md

Подсказки для Claude Code при работе с этим репозиторием.

## Что это

Мостик между Telegram и Claude Code. Одно приложение .NET 10 на всегда включённом Windows-ПК:
сообщение из чата → `claude -p` в папке проекта → вопросы «можно?» кнопками в Telegram → ответ
агента обратно в чат.

Код, комментарии, лог и тексты в чате — на русском.

## Команды

```powershell
dotnet build                                    # TreatWarningsAsErrors включён
dotnet run --project src\AgentsTracker.Gateway  # нужен appsettings.Local.json (рядом или в папке данных)
dotnet run --project src\AgentsTracker.Gateway -- protect-secrets   # зашифровать BotToken/Proxy, перенести конфиг в %LOCALAPPDATA%
pwsh -File scripts\install-autostart.ps1        # publish + protect-secrets + ACL + задача Планировщика
```

Тестов нет. Всё, что трогает контракт с CLI (аргументы, разбор JSON, ответ
`ClaudePermissionTool`), проверяется руками: запустить шлюз и смотреть лог.

### Перезапуск шлюза

На машине разработки exe запущен вручную из `bin\Debug`. Пока он работает, `dotnet build`
падает с `MSB3021` (exe занят).

```powershell
git status                                                # чужие незакоммиченные правки могут не собираться
Get-Process AgentsTracker.Gateway | Stop-Process -Force
dotnet build src\AgentsTracker.Gateway
Start-Process src\AgentsTracker.Gateway\bin\Debug\net10.0\AgentsTracker.Gateway.exe
```

- Рабочая папка не важна: `Program.cs` ставит `ContentRootPath = AppContext.BaseDirectory`.
  Проверка — строка `Content root path` в логе.
- Собрать, не останавливая шлюз: `dotnet build src\AgentsTracker.Gateway -o <папка>` (именно
  проект: `-o` для `.slnx` даёт NETSDK1194). Проверить класс отдельно — временный консольный
  проект со ссылкой на готовую dll (`<Reference>` + `HintPath`), не `ProjectReference`.
- `main` не собирается из-за чужих правок: собрать чистый HEAD из `git worktree add --detach
  ..\AgentsTracker-run HEAD` в ту же `bin\Debug\net10.0`, потом `git worktree remove --force`.
- Из сессии, запущенной самим шлюзом (из Telegram), перезапускать нельзя: `Stop-Process` убьёт и
  текущий `claude -p`. Дайте пользователю команды выше.
- Консоль отдаёт русский в cp866 — лог смотрите в PowerShell.

### Пробный экземпляр

Конфиг перекрывается переменными окружения (`$env:Gateway__ProjectPath`), поэтому можно
запустить второй exe с поддельным `Gateway__BotToken` и своими `Gateway__McpPort`/
`Gateway__MonitorPort`. Живёт ~минуту (бот один, `getUpdates` отдаёт 409) — хватает дёрнуть
монитор. Но он перезапишет `mcp-gateway.json` в папке данных, и рабочий шлюз после пробы
**надо перезапустить** — иначе каждый вызов инструмента падает с «MCP tool mcp__tg__approve
not found».

Дымовой прогон после правок инфраструктуры: скрипт в scratchpad, `pwsh -File`; через ~8 с
проверить `/api/snapshot` (`agent`, `cliVersion`), `/api/limits`, 404 на `/` порта MCP и 401 на
`POST /mcp` без токена (заголовки `Content-Type: application/json` и
`Accept: application/json, text/event-stream`, иначе 415).

### Крупные задачи — в worktree

Шлюз запущен из `bin\Debug` папки `main`; переключение ветки подменит исходники под
процессом, сломанная сборка лишит возможности перезапустить.

```powershell
git worktree add ..\AgentsTracker-<задача> -b <ветка>
```

Работайте в новой папке (из чата — `/project`), готовое вливайте в `main`. Перед слиянием и
остановкой шлюза — `git status`: в `main` параллельно работают другие сессии. Мелкие правки в
один-два коммита — прямо в `main`.

## Как добавить

**Команду чата.** Класс с `ITelegramCommandHandler` в папке фичи, `AddSingleton` в её
`*Module`, запись в `BotCommandsCatalog` (кнопка «Меню») и в текст `HelpCommandHandler`.
Заняты: `/start /help` (Help), `/new /stop` (Chat), `/rules` (Approvals), `/audit` (Audit),
`/menu /settings /status /sessions /agent /model /effort /mode /skills /project /usage`
(Settings). Дубликат у двух фич роняет старт. В «Меню» только экраны — `/new /stop /model
/effort /mode` работают текстом, но в списке их нет. Порядок в списке, справке и `RootScreen`
один: статус и сессии, агент и скиллы, репозиторий, статистика и журналы. Прочие слэш-команды
уходят в CLI как есть.

**Экран настроек.** Класс с `ISettingsScreen` в `Features/Settings/Screens/`, регистрация в
`SettingsModule`, кнопка в `RootScreen`. `RenderAsync` асинхронный ради лимитов;
`RenderFramesAsync` — необязательные кадры (`StatusScreen` «заполняет» шкалы `LimitBars`: три
кадра по 350 мс, чаще — 429 от Telegram). Модель, effort и режим — один `AgentScreen` с
аргументами `model:…`/`effort:…`/`mode:…`. `Apply` получает `chatId`: экран может ставить
задачу в очередь `ChatWorker` (`SkillsScreen`), ответ уходит нажавшему. Позиция списка — в
`ScreenNavigation` на пользователя: экраны синглтоны, пользователей может быть несколько.
Страницы и ключи callback_data — `SettingsKeyboard.Page`/`Key12`; однобуквенный префикс
аргумента экрана не должен быть hex-символом, иначе спутается с ключом.

**Фичу.** Папка в `Features/` с `*Module` и строка в списке модулей в `Program.cs`;
`ChatModule` остаётся последним.

**Агента (Codex, Cursor).** Проект `src/AgentsTracker.Agents.<Имя>` со ссылкой на
`Agents.Abstractions`: `IAgentBackendModule` регистрирует `IAgentBackend`, `IAgentLimits` (или
`NoAgentLimits`), `IAgentSkillCatalog` (или `NoAgentSkills`) и свой канал подтверждений через
`IOperatorConsole`. Строка в списке `agents` в `Program.cs`, ссылка в `Gateway.csproj`.
`grep -rn Claude src/AgentsTracker.Gateway --include=*.cs` должен находить только `Program.cs`
и комментарии. Настройки агента — в `Gateway:<Id>`, хост их не читает.

**Эндпоинт монитора.** `api.MapGet` в `MonitorModule.MapEndpoints`, только чтение,
`Results.Json(..., Json)`; секция в `index.html`.

**Ключ конфига.** Свойство в `GatewayOptions` (+ `Validate`), дефолт в `appsettings.json`,
пример `"//Ключ": "…"` в `appsettings.Local.example.json`, строка в README «Основные
настройки». Ключ агента — в `ClaudeOptions` и `Gateway:Claude`.

## Устройство

```
src/AgentsTracker.Agents.Abstractions/   контракты агента, без Telegram и без конкретного CLI:
  IAgentBackend         Probe() (бинарник, версия), RunAsync(AgentRunRequest, IAgentRunObserver)
  AgentRun.cs           запрос (промпт, папка, сессия, модель, effort, режим, таймаут), наблюдатель, результат
  AgentCapabilities     какие модели/effort/режимы умеет агент (effort null — не умеет)
  IOperatorConsole      что агент просит у человека: ApproveAsync, AskAsync; PersistentRule — правило «всегда»
  IAgentLimits          лимиты тарифа; IAgentSkillCatalog — слэш-команды
  IAgentBackendModule   AddServices + MapEndpoints; AgentHost — папка данных, порт, прокси от хоста
src/AgentsTracker.Agents.Claude/         Claude Code за этими контрактами:
  ClaudeBackend         процесс claude -p: аргументы, stream-json, «сессия не найдена», лимит
  ClaudeLimits, ClaudeSkillCatalog, ClaudePluginRegistry, ClaudeCliLocator, ClaudeStreamEvent
  Mcp/                  McpConfigFile — mcp-gateway.json с токеном; ClaudePermissionTool — payload CLI ↔ IOperatorConsole
src/AgentsTracker.Gateway/
  Program.cs            список агентов (выбор по Gateway:Agent) и фич
  Domain/               чистые модели: GatewayState (state.json), AuditEvent
  Infrastructure/       Configuration (GatewayOptions, ProjectCatalog), State (SessionStore),
                        Telegram (роутер, форматтер, Dispatch/), Audit, Monitoring (RunMonitor, RingBufferLog), Security
  Features/             вертикальные слайсы, у каждого свой *Module:
    Approvals/          карточки подтверждений, ApprovalBroker, /rules
    Chat/               ChatWorker (очередь, запуск, сессии), RunStatusMessage, /new /stop
    Settings/           SettingsMenuCoordinator + Screens/*, /menu /status /sessions /agent /skills /project /usage
    Help/ Audit/ Monitor/   /start /help; /audit; веб-страница (index.html — EmbeddedResource) и /api/*
```

Правила слоёв: `Domain` и `Abstractions` не открывают файлы и не ходят по сети; `Gateway` знает
о конкретном агенте только в `Program.cs`; бэкенд не знает о Telegram и `GatewayOptions`;
`Infrastructure` не знает о фичах (кроме контрактов `Dispatch`); фича зависит от фичи только
через публичный сервис (`Settings` → `ChatWorker.IsBusy`).

### Как ходит сообщение

`TelegramBotService` пускает только `AllowedUserIds` и личные чаты. Текст обрабатывается по
порядку:

1. слэш-команда из `ITelegramCommandHandler.Commands` — **раньше всего**, иначе `/stop` уйдёт
   в ожидающий свободный ответ и прервать зависший запуск будет нечем;
2. цепочка `ITelegramTextHandler` в порядке модулей: ответ на карточку (`ApprovalTextHandler`)
   → аргументы скилла (`SkillArgumentsTextHandler`) → в очередь агенту
   (`ChatEnqueueTextHandler`, всегда `true`). Поэтому `ChatModule` последний. Неизвестные
   слэш-команды — это команды самого Claude Code, они уходят в CLI.

Кнопки: префикс `cfg:` — меню, всё остальное (hex-id запроса) — `ApprovalBroker`.
`ActiveChatId` брокера ставит `ChatWorker` перед запуском, иначе карточки ушли бы в чат
другого пользователя.

### Кольцо «шлюз → CLI → шлюз»

Шлюз и запускает агента, и обслуживает его:

```
Telegram ──▶ TelegramBotService ──▶ ChatWorker ──▶ ClaudeBackend ──▶ claude.exe -p
                    ▲                                                     │ нужно разрешение
                    └── ApprovalBroker ◀── OperatorConsole ◀── ClaudePermissionTool ◀── MCP http://127.0.0.1:<McpPort>/mcp
                                                                              Authorization: Bearer <токен>
```

`McpConfigFile` при старте генерирует токен, пишет `mcp-gateway.json` и удаляет его при
остановке; `/mcp` монтируется с фильтром `McpConfigFile.Authorizes`; CLI получает
`--mcp-config` и `--permission-prompt-tool mcp__tg__approve`. Имя сервера и инструмента —
константы `McpConfigFile`, `[McpServerTool]` берёт ту же константу. README — инструкция для
пользователя; меняя защиту эндпоинта или папку данных, проверьте раздел «Безопасность».

### Подтверждения (контракт проверен на живом CLI, схема не задокументирована)

CLI зовёт инструмент с `{"tool_name", "input", "tool_use_id"}`. Ответ — JSON-строка:
`{"behavior":"allow","updatedInput":{…}}` (`updatedInput` **обязателен**) или
`{"behavior":"deny","message":"…"}`. Поля читаются защитно (snake_case/camelCase). Сырой payload
— только на Debug: для Edit/Write это содержимое файлов. `AskUserQuestion` приходит туда же и
требует `updatedInput` с исходным `questions` и `answers` (ключ — текст вопроса).

Кнопка «Всегда» двояка. Если CLI прислал `permission_suggestions` с `destination:
localSettings` — правило пишет **сам CLI** в `.claude/settings.local.json` проекта; карточка
показывает его, в хост оно приходит как `PersistentRule.Raw` и возвращается как есть. Иначе шлюз
запоминает точную сигнатуру в `state.json` (`AlwaysAllowByProject`), видно в `/rules`.
Правила шлюза действуют только в своём проекте.

Одобрять невидимый хвост нельзя: если карточка что-то обрезала, `OperatorConsole` перед ней
шлёт полный текст файлом (`ApprovalBroker.SendAttachmentAsync`), карточка предупреждает.
Имя и содержимое файла решает `ApprovalCardRenderer` (`ApprovalAttachment`): обычно
`<инструмент>-input.txt`, план `ExitPlanMode` — целиком как `plan.md`. Все фрагменты через
`EscapeCapped` с лимитом: переполненное сообщение упало бы при отправке, а исключение стало бы
отказом.

Самовыдача прав проверена на CLI 2.1.x: `Write` в `.claude/settings.local.json` отклоняется
даже в `acceptEdits`. После обновления CLI перепроверить: `claude -p … --permission-mode
acceptEdits --output-format json` во временной папке.

`ApprovalBroker` держит вызов MCP на `TaskCompletionSource` до нажатия. `WaitAsync` различает
таймаут и отмену: `/stop` бросает `OperationCanceledException`, а не «не ответил вовремя».
Таймауты: `ApprovalTimeoutMinutes` (15) — карточка, `RunTimeoutMinutes` (60) — весь запуск;
оба 1..1440. Первый выше ~5 минут бессмыслен: раньше сработает idle-таймаут MCP у CLI.

### Запуск и поток событий

CLI запускается с `--output-format stream-json --verbose` (без `--verbose` поток не пишется).
`ClaudeBackend.ReadStreamAsync` читает stdout построчно (`ClaudeStreamEvent.Classify`): вызовы
инструментов → `IAgentRunObserver.Activity`, остальное отбрасывается. Итог — последняя строка
`"type":"result"`. Текст ошибки CLI часто оставляет в stderr, а `result` шлёт пустым — проверка
сброса сессии и лимита смотрит и в stderr.

Идущий запуск лежит в `state.json` (`ActiveRun`): `BeginRun` перед запуском, `EndRun` в
`finally`. Если при старте запись на месте — прошлый экземпляр умер посреди работы:
`StartupNotice` шлёт «🔌 Шлюз запущен» всем, в чат прерванного запуска — «прерван, напишите
„продолжай“», пишет `run.end` с `interrupted`. Пользователю, который ещё не писал боту,
Telegram не даёт отправить первым — ошибка глотается на Debug.

В чате — `RunStatusMessage`: одно сообщение «Работаю…», раз в 4 с редактируется (время,
счётчик вызовов, три последних шага, сабагенты `↳`). `Report` только запоминает, сеть — в своём
цикле; `DisposeAsync` дожидается цикла (≤10 с), иначе правка догоняла бы удаление. Аргумент
инструмента в статусе один и короткий (`ClaudeStreamEvent.Describe`).

### `--permission-mode` передаётся всегда

Без флага действует `permissions.defaultMode` из `~/.claude/settings.json` — у пользователя там
`auto`, и кнопки в чате не появляются. Не убирайте аргумент из `ClaudeBackend.BuildArguments`.
Из чата переключаются только `PermissionMode.Selectable` (`plan`/`default`/`acceptEdits`/
`auto`); `dontAsk` и `bypassPermissions` исключены намеренно — полное снятие подтверждений
остаётся правкой конфига на машине. Значения проверяет `ValidateFor(capabilities)` в
`ValidateStartup`, а не `GatewayOptions.Validate` (агент ещё не выбран).

### Сессии

Сессии ключуются **нормализованным путём проекта** (`ProjectCatalog.Normalize`): `--resume`
работает только в папке, где сессия создана; смена репозитория меняет и активную сессию.
`ChatWorker` фиксирует сессию и `ProjectPath` в `AgentRunRequest` до запуска.

Id новой сессии выдаёт **шлюз** (`--session-id <uuid>`) и регистрирует по
`IAgentRunObserver.SessionStarted` сразу после старта процесса: иначе `/stop` или падение
первого запуска теряли бы ветку. Сессия сбрасывается только когда CLI прямо говорит, что не
нашёл её (`LooksLikeMissingSession` → `SessionLost`), и только в `ChatWorker.SettleSession`
через `TrySetSessionId(onlyIfActive)` — чтобы не перетереть `/new`, сделанный во время
запуска. `/sessions` показывает до 8; кнопка несёт `ShortId` (8 символов), а не номер в
списке: завершившийся между отрисовкой и нажатием запуск сдвинул бы номера.

Выбор из чата (`SessionStore`) лежит поверх конфига: `EffectiveModel`,
`EffectivePermissionMode`, `EffectiveEffort`, `ProjectPath`. Значение, совпадающее с конфигом,
хранится как `null`: иначе правка конфига оказалась бы молча перекрыта старым выбором.

`ProjectCatalog`: список `Gateway:Projects`, иначе обход `Gateway:ProjectsRoot` до
`ProjectsRootDepth`, иначе соседи `ProjectPath`. `ProjectScreen` выбирает в два шага (папка →
репозиторий) страницами по 12. Текущий проект первым в своей группе, после выбора страница
сбрасывается на первую — иначе отметка `▶` оказывалась бы за пределами экрана.

### Скиллы и плагины

`/skills` — `ClaudeSkillCatalog` собирает то же, что видит CLI: `skills/*/SKILL.md` и
`commands/**/*.md` из `.claude` проекта и `~/.claude`, плюс включённые плагины
(`~/.claude/plugins/installed_plugins.json`; `enabledPlugins` наслаиваются профиль →
`.claude/settings.json` → `settings.local.json`). `user-invocable: false` не показываются.
Кэш 5 с (нажатие — это `Apply` и `Render` подряд); «🔄 Обновить» — `Refresh()`.

Экран «🔌 Плагины» (`ClaudePluginRegistry`) переключает `enabledPlugins[имя@маркетплейс]`
только в личном `~/.claude/settings.json` — туда же пишет `/plugin` самого CLI. Файл
переписывается целиком через `JsonNode` (LF, без `\u`, через временный файл), комментарии не
переживут. Значение из слоя проекта помечено 🔒 и не меняется (`PluginInfo.LockedBy`). Кнопка
несёт ключ плагина, а не желаемое состояние. Действует со следующего `claude -p`.

Встроенные скиллы CLI перечислить нельзя — группа «Встроенные» из
`Gateway:Claude:BuiltInSkills` (дефолт под CLI 2.1.x, после обновления — конфигом). «С
аргументами» — `SkillLauncher.Expect` ждёт следующий текст. Запуски считаются в `SkillUsage`,
«⭐ Частые» — до пяти самых частых.

### Веб-монитор

`Features/Monitor/` — страница на **отдельном** порту `Gateway:MonitorPort` (5100, `0`
выключает; не может совпасть с `McpPort`). Kestrel слушает оба порта одним конвейером, поэтому
группа эндпоинтов фильтрует `Connection.LocalPort`. Авторизации нет, только loopback — поэтому
эндпоинты **только читают**. Эндпоинты: `/`, `/api/snapshot`, `/api/events` (SSE),
`/api/limits`, `/api/runs?project=&limit=`, `/api/stats`, `/api/stats.csv`, `/api/audit?count=`,
`/api/log?count=&level=`.

«Что сейчас» — `RunMonitor`: `ChatWorker` сообщает очередь, старт, шаги и финиш;
`OperatorConsole` — ожидание карточки (`using monitor.Approval(tool, brief)`). Карточек в
снимке список: CLI зовёт инструмент параллельно. Шагов хранится 300. `Changes()` — канал
ёмкостью 1 с вытеснением: медленный браузер получает последнее состояние. `/api/events` шлёт
снимок при подключении, дальше по изменениям, между ними `ping` раз в 5 с.

История — `GatewayState.RecentRuns` (200, `SessionStore.RecordRunOutcome`). `/api/stats.csv` —
`;`, BOM и десятичная запятая под Excel на русской локали. Лимиты — `IAgentLimits.GetAsync`
(кэш 3 мин, страница опрашивает раз в минуту); лог — `RingBufferLog` (500, Information+).

`index.html` — один файл без сборки и CDN, `EmbeddedResource`; данные в camelCase, кириллица
без `\u`; правка страницы требует `dotnet build` и перезапуска. Смотреть без шлюза: копия
страницы и `scripts\monitor-mock.js` в scratchpad, `<script src="monitor-mock.js">` перед
основным скриптом (мок подменяет `fetch` и `EventSource`, `?state=run|wait|idle`; новый
эндпоинт — добавить в `routes`), `python -m http.server <порт> --bind 127.0.0.1` из scratchpad
(Playwright не открывает `file://`). Доступность — `browser_snapshot`; тёмная тема —
`page.emulateMedia({colorScheme:'dark'})`; живую страницу проверяет `browser_run_code_unsafe`
(`curl` в разрешениях нет). Синие цифры в скриншоте таблицы — субпиксельный артефакт,
сверяйте `getComputedStyle`.

Стиль — Fluent 2 «Mica» с приёмами Grafana/Elastic: подложка `--canvas`, карточки `.card`,
акцент `--brand` только на графике, ссылках и метках инструментов; сигналы —
`--run`/`--wait`/`--fail`. Слева рейка: статус-pill `.state`, секундомер, навигация `.nav` по
якорям, лимиты как bar gauge `.gauge` (порог тот же, что у `LimitBars`). Справа панели:
«Сейчас», stat-плитки `.figures` со спарклайнами, график, таблицы с липкой шапкой. Лента
`.tape` — время · метка инструмента `.tool` · аргумент `.arg`; `splitStep` берёт первое слово
как имя инструмента, поэтому `ClaudeStreamEvent.Describe` должен начинать строку именем.

Скрипт: `renderLive` перестраивает DOM только по кадру SSE, а секундомер и «ждёт N с»
(`[data-since]`) тикают через `textContent` — перерисовка `innerHTML` раз в секунду сбрасывала
выделение и прыгала скроллом; лента прокручивается вниз только при новых шагах. Проверено по
modern-web-guidance: `<h1>` в рейке, `nav[aria-label]`, `caption`/`scope` у таблиц, график
`role="img"` + таблица в `<details>`, `role="status"` только на состоянии и соединении, размеры
в `rem`, `color-scheme: light dark`, `prefers-reduced-motion`, `forced-colors`.

### Состояние, секреты, слои конфига

`%LOCALAPPDATA%\AgentsTracker\` (`AppPaths.DataDirectory`, ACL — владелец и SYSTEM):
`state.json` (атомарно), `mcp-gateway.json`, `appsettings.Local.json` с секретами,
`audit\audit-ГГГГ-ММ.jsonl`.

Конфиг слоями: `appsettings.json` → `appsettings.Local.json` рядом с exe (IDE) → тот же файл в
папке данных (боевой) → переменные окружения. `dpapi:…` расшифровывается при загрузке
(`ProtectedJsonConfigurationProvider`); `protect-secrets` шифрует `BotToken` и `Proxy`.
`publish\` секретов не содержит. Переменные `Gateway__*` дочерний `claude` не видит — хост
удаляет их из окружения в `ValidateStartup`.

### Аудит

`IAuditLog.Write(AuditEvent.Now(kind, summary, userId, chatId, project, session, outcome))` —
«кто, куда, что», без секретов и полных текстов (≤200 символов; текст пользователя — превью
`Text.Preview`, 80 символов: туда могли вставить токен). Виды — `AuditKinds`:
`access.rejected`, `message`, `run.start`/`run.end`, `approval`, `question`, `settings`,
`rules`, `session.reset`, `limit.refused`, `gateway`. Экраны меню пишут через
`SettingsAudit.Changed`. Это не замена `ILogger`: в аудит — за что отвечает человек, в лог —
что нужно для отладки.

### Лимиты тарифа

Единственный ограничитель — `IAgentLimits`, окна `five_hour`, `seven_day`, `seven_day_<модель>`;
проверяется в `ChatWorker.ProcessAsync` перед запуском. Денег в шлюзе нет: `total_cost_usd` не
читается, `--max-budget-usd` не передаётся, в статистике — только ходы, токены и время. Не
возвращайте долларовые оценки.

`ClaudeLimits` ходит в **недокументированный** `api.anthropic.com/api/oauth/usage` с токеном из
`~/.claude/.credentials.json`: нужен правдоподобный User-Agent, 429 на частый опрос (кэш
3 мин), может исчезнуть в любой версии — при ошибке запуск **пропускается**, не блокируется.
Поля читаются через `ClaudeLimits.Number` с проверкой `ValueKind`: `utilization: null` иначе
уходил бы как «Внутренняя ошибка шлюза».

Кредиты («extra usage») запрещены: `DISABLE_EXTRA_USAGE_COMMAND=1`; при обрыве по лимиту
`ChatWorker` снимает всю очередь.

### Вывод в Telegram

`TelegramFormatter` переводит markdown в подмножество HTML и режет под 4096 — резать
**исходный markdown до конвертации**, иначе рвутся теги. Списки — `•`/`◦`, таблица —
выровненный `<pre>`. Курсив только у `*` вплотную к содержимому на границе слова (иначе `*.cs`
и `2 * 3` курсивились); `_` не разбирается (`snake_case`). Блок кода длиннее лимита уходит
файлом. Если Telegram отверг разметку — тот же текст без `ParseMode`. Суммы, токены и время —
`DisplayFormat`.

## Помнить

- Шлюз **не подключается** к сессии VS Code — это параллельная сессия на той же папке: общие
  `CLAUDE.md`, настройки, хуки и MCP, но своя история.
- `--bare` нельзя: не читает `~/.claude`, ломает OAuth-логин по подписке.
- `claude.exe` ищет `ClaudeCliLocator`: `Gateway:Claude:Executable` → стандартные пути → PATH
  → бинарник расширения VS Code (путь с версией исчезает при обновлении — пишется
  предупреждение). Штатно — отдельный CLI (`irm https://claude.ai/install.ps1 | iex`). Версия
  пишется в лог: разбор JSON держится на конкретной версии.
- Барьеры: `AllowedUserIds` + только личные чаты; Kestrel только `127.0.0.1`; MCP — токен в
  заголовке; монитор без токена — поэтому только читает.
- Аргументы CLI — через `ProcessStartInfo.ArgumentList`, не склеивайте строку.
- `HttpClient` — только через `IHttpClientFactory`. `ClaudeLimits.HttpClientName` — один таймаут,
  без ретраев. Telegram-клиент (`TelegramClientFactory`) — синглтон, DNS обновляет
  `PooledConnectionLifetime`; ретрай только на `HttpRequestException`: методы Bot API — POST без
  идемпотентности (повтор после 5xx — дубль в чате), а 429 ждёт вызывающий по `retry_after`.
  `BaseAddress` не задавать. `RemoveAllLoggers()`: токен — часть пути.
- `Channel.CreateUnbounded` в `ChatWorker` без `SingleReader`: с ним `Reader.Count` бросает, и
  `/status` падает.
- `McpConfigFile` и `SessionStore` — единственные с классическим конструктором: побочный эффект
  (файл) должен случиться один раз до старта.
- Русские тексты и windows-пути правьте Edit/Write, не heredoc и не python из Bash: `\a`, `\n`
  в путях съедаются молча. Исходники — UTF-8 **без BOM**. Сообщение коммита из нескольких
  абзацев — файлом через `git commit -F <файл>`.
- Ревьюеру-сабагенту без Bash `git show`/`git diff` недоступны: выгружайте старые версии файлов
  в scratchpad.
- Комментарии объясняют, какой отказ предотвращает код, а не что он делает.
- Из сессии через Telegram `AskUserQuestion` и `ExitPlanMode` ждут ≤5 минут: без ответа берите
  рекомендуемый вариант. Многошаговую проверку кладите в скрипт и запускайте `pwsh -File`.
- `modern-web-guidance` (`npx.cmd -y modern-web-guidance@latest search "…"`) — из инструмента
  PowerShell: из Git Bash `npx.cmd` молча отдаёт пустой вывод.
- `install-autostart.ps1` ставит задачу Планировщика от текущего пользователя, не службу: OAuth
  лежит в `%USERPROFILE%\.claude`. `-Uninstall` снимает.

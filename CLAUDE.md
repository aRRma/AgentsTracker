# CLAUDE.md

Подсказки для Claude Code при работе с этим репозиторием. Подробности — в `docs/`:

- `docs/operations.md` — перезапуск шлюза, пробный экземпляр, worktree, инструменты.
- `docs/cli-contract.md` — контракт с CLI: подтверждения, stream-json, сессии, лимиты, скиллы.
- `docs/monitor.md` — веб-монитор: эндпоинты, мок, стиль, доступность.

## Что это

Мостик между Telegram и Claude Code. Одно приложение .NET 10 на всегда включённом Windows-ПК:
сообщение из чата → `claude -p` в папке проекта → вопросы «можно?» кнопками в Telegram → ответ
агента обратно в чат.

Код, комментарии, лог и тексты в чате — на русском.

## Команды

```powershell
dotnet build                                    # TreatWarningsAsErrors включён
dotnet run --project src\AgentsTracker.Gateway  # нужен appsettings.Local.json (рядом или в папке данных)
dotnet run --project src\AgentsTracker.Gateway -- protect-secrets   # зашифровать секреты канала и Proxy, перенести конфиг в %LOCALAPPDATA%
pwsh -File scripts\install-autostart.ps1        # publish + protect-secrets + ACL + задача Планировщика
pwsh -File scripts\migrate-channel-settings.ps1 # разовый перенос BotToken/AllowedUserIds в Gateway:Channel:Settings
```

Тестов нет. Всё, что трогает контракт с CLI, проверяется руками: запустить шлюз и смотреть лог.

**Шлюз запущен из `bin\Debug` папки `main`.** Пока он работает, `dotnet build` падает с
`MSB3021`; переключение ветки подменит исходники под процессом. Перезапуск, сборка в другую
папку и работа в worktree — `docs/operations.md`. Из сессии, запущенной из Telegram,
перезапускать нельзя — дайте пользователю команды оттуда.

## Как добавить

**Команду чата.** Класс с `IChatCommandHandler` в папке фичи, `AddSingleton` в её
`*Module`, запись в `ChatCommandCatalog` (подсказка команд канала) и в текст `HelpCommandHandler`.
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
аргументами `model:…`/`effort:…`/`mode:…`. `Apply` получает `ChatId`: экран может ставить
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

**Канал чата (Slack, Discord).** Проект `src/AgentsTracker.Channels.<Имя>` со ссылкой на
`Channels.Abstractions`: `IChatChannelModule` регистрирует `IChatChannel` и всё своё, читает
настройки из `Gateway:Channel:Settings` и объявляет в `SecretKeys` то, что шифрует
`protect-secrets`. Свои типы `ChatId`/`UserId` (в `Key` — «канал:значение»), свои
`ChannelLimits`, отказы транспорта — только `ChannelRequestException`. Строка в списке
`channels` в `Program.cs` (выбор по `Gateway:Channel:Type`), ссылка в `Gateway.csproj`.
`grep -rn Telegram src/AgentsTracker.Gateway --include=*.cs` должен находить только
`Program.cs` и комментарии. Транспортный клиент канал берёт **лениво**: хост создаёт канал
раньше, чем печатает ошибки настроек, и падение конструктора на пустом токене подменило бы
понятную ошибку стектрейсом.

**Эндпоинт монитора.** `api.MapGet` в `MonitorModule.MapEndpoints`, только чтение —
`docs/monitor.md`.

**Ключ конфига.** Свойство в `GatewayOptions` (+ `Validate`), дефолт в `appsettings.json`,
пример `"//Ключ": "…"` в `appsettings.Local.example.json`, строка в README «Основные
настройки». Ключ агента — в `ClaudeOptions` и `Gateway:Claude`, ключ канала — в его
`*Options` и `Gateway:Channel:Settings`.

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
  Mcp/                  McpConfigFile — mcp-gateway-<pid>.json с токеном; ClaudePermissionTool — payload CLI ↔ IOperatorConsole
src/AgentsTracker.Channels.Abstractions/ контракты канала, без конкретного мессенджера:
  IChatChannel          адреса и лимиты канала, ConnectAsync/ListenAsync, Send/Edit/Delete/Acknowledge
  ChatId, UserId        адрес как значение; Key — «канал:значение» для state.json, аудита и лога
  Messages.cs           OutgoingMessage, Keyboard, MessageRef, IncomingMessage, ButtonPress, ChatCommand
  ChatHtml              канонический формат текста (b, i, s, code, pre, a, blockquote) и экранирование
  ChannelRequestException  единственное исключение канала наружу: RateLimited (retry_after), MarkupRejected, CannotReach
  IChatChannelModule    AddServices + MapEndpoints, SecretKeys; ChannelHost — общий прокси от хоста
src/AgentsTracker.Channels.Telegram/     Telegram за этими контрактами:
  TelegramChannel       long polling, инлайн-кнопки, HTML; «message is not modified» и retry_after — здесь
  TelegramOptions, TelegramIds, TelegramClientFactory (клиент лениво: конструктор проверяет токен)
src/AgentsTracker.Gateway/
  Program.cs            списки агентов (Gateway:Agent) и каналов (Gateway:Channel:Type), список фич
  Domain/               чистые модели: GatewayState (state.json), AuditEvent
  Infrastructure/       Configuration (GatewayOptions, ProjectCatalog), State (SessionStore),
                        Chat (ChatGatewayService, ChatDispatcher, MarkdownRenderer, Dispatch/),
                        Audit, Monitoring (RunMonitor, RingBufferLog), Security
  Features/             вертикальные слайсы, у каждого свой *Module:
    Approvals/          карточки подтверждений, ApprovalBroker, /rules
    Chat/               ChatWorker (очередь, запуск, сессии), RunStatusMessage, /new /stop
    Settings/           SettingsMenuCoordinator + Screens/*, /menu /status /sessions /agent /skills /project /usage
    Help/ Audit/ Monitor/   /start /help; /audit; веб-страница (index.html — EmbeddedResource) и /api/*
```

Правила слоёв: `Domain` и `Abstractions` не открывают файлы и не ходят по сети; `Gateway` знает
о конкретном агенте и канале только в `Program.cs`; бэкенд и канал не знают друг о друге и о
`GatewayOptions`; `Infrastructure` не знает о фичах (кроме контрактов `Dispatch`); фича зависит
от фичи только через публичный сервис (`Settings` → `ChatWorker.IsBusy`).

### Как ходит сообщение

`ChatDispatcher` пускает только `IChatChannel.AllowedUsers` и личные чаты (`ChatKind.Unknown`
— тоже отказ). Текст обрабатывается по порядку:

1. слэш-команда из `IChatCommandHandler.Commands` — **раньше всего**, иначе `/stop` уйдёт
   в ожидающий свободный ответ и прервать зависший запуск будет нечем;
2. цепочка `IChatTextHandler` в порядке модулей: ответ на карточку (`ApprovalTextHandler`)
   → аргументы скилла (`SkillArgumentsTextHandler`) → в очередь агенту
   (`ChatEnqueueTextHandler`, всегда `true`). Поэтому `ChatModule` последний. Неизвестные
   слэш-команды — это команды самого Claude Code, они уходят в CLI.

Кнопки: префикс `cfg:` — меню, всё остальное (hex-id запроса) — `ApprovalBroker`.
`ActiveChat` брокера ставит `ChatWorker` перед запуском, иначе карточки ушли бы в чат
другого пользователя.

### Кольцо «шлюз → CLI → шлюз»

Шлюз и запускает агента, и обслуживает его:

```
канал ──▶ ChatDispatcher ──▶ ChatWorker ──▶ ClaudeBackend ──▶ claude.exe -p
                    ▲                                                     │ нужно разрешение
                    └── ApprovalBroker ◀── OperatorConsole ◀── ClaudePermissionTool ◀── MCP http://127.0.0.1:<McpPort>/mcp
                                                                              Authorization: Bearer <токен>
```

`McpConfigFile` при старте генерирует токен, пишет `mcp-gateway-<pid>.json` и удаляет его при
остановке (имя с PID: общий файл второй экземпляр перезаписывал и удалял, и рабочий шлюз падал
на «mcp__tg__approve not found»; файлы мёртвых PID подметаются при старте); `/mcp` монтируется с фильтром `McpConfigFile.Authorizes`; CLI получает
`--mcp-config` и `--permission-prompt-tool mcp__tg__approve`. Имя сервера и инструмента —
константы `McpConfigFile`, `[McpServerTool]` берёт ту же константу. README — инструкция для
пользователя; меняя защиту эндпоинта или папку данных, проверьте раздел «Безопасность».

Контракт подтверждений проверен на живом CLI 2.1.x и не задокументирован — `docs/cli-contract.md`.
Главное: в ответе `allow` обязателен `updatedInput`; кнопка «Всегда» либо отдаёт правило CLI
(`permission_suggestions`), либо шлюз хранит сигнатуру в `state.json` только для своего
проекта; обрезанный ввод перед карточкой уходит файлом.

### `--permission-mode` передаётся всегда

Без флага действует `permissions.defaultMode` из `~/.claude/settings.json` — у пользователя там
`auto`, и кнопки в чате не появляются. Не убирайте аргумент из `ClaudeBackend.BuildArguments`.
Из чата переключаются только `PermissionMode.Selectable` (`plan`/`default`/`acceptEdits`/
`auto`); `dontAsk` и `bypassPermissions` исключены намеренно — полное снятие подтверждений
остаётся правкой конфига на машине. Значения проверяет `ValidateFor(capabilities)` в
`ValidateStartup`, а не `GatewayOptions.Validate` (агент ещё не выбран).

### Сессии и выбор из чата

Сессии ключуются **нормализованным путём проекта** (`ProjectCatalog.Normalize`); id новой
выдаёт шлюз (`--session-id`), сбрасывается только по `SessionLost` от CLI — подробности в
`docs/cli-contract.md`.

Выбор из чата (`SessionStore`) лежит поверх конфига: `EffectiveModel`,
`EffectivePermissionMode`, `EffectiveEffort`, `ProjectPath`. Значение, совпадающее с конфигом,
хранится как `null`: иначе правка конфига оказалась бы молча перекрыта старым выбором.

`ProjectCatalog`: список `Gateway:Projects`, иначе обход `Gateway:ProjectsRoot` до
`ProjectsRootDepth`, иначе соседи `ProjectPath`. `ProjectScreen` выбирает в два шага (папка →
репозиторий) страницами по 12. Текущий проект первым в своей группе, после выбора страница
сбрасывается на первую — иначе отметка `▶` оказывалась бы за пределами экрана.

### Состояние, секреты, слои конфига

`%LOCALAPPDATA%\AgentsTracker\` (`AppPaths.DataDirectory`, ACL — владелец и SYSTEM):
`state.json` (атомарно), `mcp-gateway-<pid>.json`, `appsettings.Local.json` с секретами,
`audit\audit-ГГГГ-ММ.jsonl`.

Конфиг слоями: `appsettings.json` → `appsettings.Local.json` рядом с exe (IDE) → тот же файл в
папке данных (боевой) → переменные окружения. `dpapi:…` расшифровывается при загрузке
(`ProtectedJsonConfigurationProvider`); `protect-secrets` шифрует `Gateway:Proxy` и ключи
`IChatChannelModule.SecretKeys` в `Gateway:Channel:Settings` (у Telegram — `BotToken`, `Proxy`).
`publish\` секретов не содержит. Переменные `Gateway__*` дочерний `claude` не видит — хост
удаляет их из окружения в `ValidateStartup`.

### Аудит

`IAuditLog.Write(AuditEvent.Now(kind, summary, user, chat, project, session, outcome))` —
«кто, куда, что»; адреса ложатся строками `UserKey`/`ChatKey` («канал:значение»), а не числами:
номера разных каналов совпадают. Есть и `NowByKeys` — для записи по готовым ключам из
`state.json`. Без секретов и полных текстов (≤200 символов; текст пользователя — превью
`Text.Preview`, 80 символов: туда могли вставить токен). Виды — `AuditKinds`:
`access.rejected`, `message`, `run.start`/`run.end`, `approval`, `question`, `settings`,
`rules`, `session.reset`, `limit.refused`, `gateway`. Экраны меню пишут через
`SettingsAudit.Changed`. Это не замена `ILogger`: в аудит — за что отвечает человек, в лог —
что нужно для отладки.

### Лимиты и деньги

Единственный ограничитель — `IAgentLimits` (окна тарифа), проверка в
`ChatWorker.ProcessAsync` перед запуском; при ошибке опроса запуск **пропускается**. Денег в
шлюзе нет: `total_cost_usd` не читается, `--max-budget-usd` не передаётся, в статистике —
только ходы, токены и время. Не возвращайте долларовые оценки. Кредиты («extra usage») агенту
запрещены. Детали — `docs/cli-contract.md`.

### Вывод в чат

`MarkdownRenderer` переводит markdown в `ChatHtml` и режет под `IChatChannel.Limits`
(`MessageLength`, у Telegram 3800) — резать **исходный markdown до конвертации**, иначе рвутся
теги. Списки — `•`/`◦`, таблица — выровненный `<pre>`. Курсив только у `*` вплотную к
содержимому на границе слова (иначе `*.cs` и `2 * 3` курсивились); `_` не разбирается
(`snake_case`). Блок кода длиннее лимита уходит файлом. Экранирование под бюджет —
`ChatHtml.EscapeCapped` (текст, уложившийся целиком, символ под многоточие не тратит). Если
канал ответил любым 400 (не только «can't parse»: неподдерживаемый тег, «too long» после
экранирования), он сам повторяет отправку без разметки (`ChatHtml.StripTags`) — иначе часть
ответа пропадает молча. При 429 посреди многочастного ответа `ChatWorker.SendPartAsync` ждёт
`RetryAfter` (≤30 с) и повторяет ту же часть. Длину callback_data (64 байта) проверяет
`TelegramChannel.Markup` и называет кнопку — это ошибка экрана, не транспорта. Суммы, токены
и время — `DisplayFormat`.

## Помнить

- Шлюз **не подключается** к сессии VS Code — это параллельная сессия на той же папке: общие
  `CLAUDE.md`, настройки, хуки и MCP, но своя история.
- `--bare` нельзя: не читает `~/.claude`, ломает OAuth-логин по подписке.
- Барьеры: `AllowedUsers` канала + только личные чаты; Kestrel только `127.0.0.1`; MCP — токен в
  заголовке; монитор без токена — поэтому только читает.
- Аргументы CLI — через `ProcessStartInfo.ArgumentList`, не склеивайте строку.
- `HttpClient` — только через `IHttpClientFactory`. `ClaudeLimits.HttpClientName` — один таймаут,
  без ретраев. Telegram-клиент (`TelegramClientFactory`) — синглтон, DNS обновляет
  `PooledConnectionLifetime`; ретрай только на `HttpRequestException`: методы Bot API — POST без
  идемпотентности (повтор после 5xx — дубль в чате), а 429 ждёт вызывающий по `retry_after`.
  `BaseAddress` не задавать. `RemoveAllLoggers()`: токен — часть пути.
- `Channel.CreateUnbounded` в `ChatWorker` без `SingleReader`: с ним `Reader.Count` бросает, и
  `/status` падает.
- `_runCts` в `ChatWorker` ставится прямо перед `agent.RunAsync`, после отправки статусного
  сообщения: снимает его только `finally` запуска, и сбой выше оставлял бы `IsBusy` до перезапуска.
- Переехавшие ключи канала — один список `ChannelOptions.MovedKeys`: его читают и проверка при
  старте, и `protect-secrets`. Проверять на копии старого конфига: `protect-secrets <путь>`
  должен вернуть 1 с подсказкой, `migrate-channel-settings.ps1 -Path <путь>` не трогает уже
  заполненное в `Channel:Settings`.
- `McpConfigFile` и `SessionStore` — единственные с классическим конструктором: побочный эффект
  (файл) должен случиться один раз до старта.
- Русские тексты и windows-пути правьте Edit/Write, не heredoc из Bash. Исходники — UTF-8
  **без BOM**. Остальное про инструменты — `docs/operations.md`.
- Комментарии объясняют, какой отказ предотвращает код, а не что он делает.
- Из сессии через Telegram `AskUserQuestion` и `ExitPlanMode` ждут ≤5 минут: без ответа берите
  рекомендуемый вариант.

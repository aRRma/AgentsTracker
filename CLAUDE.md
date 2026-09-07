# CLAUDE.md

Подсказки для Claude Code при работе с этим репозиторием. Подробности — в `docs/`:

- `docs/operations.md` — перезапуск шлюза, пробный экземпляр, worktree, инструменты.
- `docs/deployment.md` — промышленный запуск: публикация, install/uninstall, Docker, порты, секреты.
- `docs/cli-contract.md` — контракт с CLI: подтверждения, stream-json, сессии, лимиты, скиллы.
- `docs/monitor.md` — веб-монитор: эндпоинты, мок, стиль, доступность.
- `docs/claude-permissions.md` — права Claude Code на машине: `allow`/`ask`/`deny`, пример
  `docs/examples/claude-settings.example.json`, проектный `.claude/settings.json`.

## Что это

Мостик между Telegram и Claude Code. Одно приложение .NET 10 на всегда включённом ПК:
сообщение из чата → `claude -p` в папке проекта → вопросы «можно?» кнопками в Telegram → ответ
агента обратно в чат. Разрабатывается на Windows; в контейнере работает на Linux, отдельной
установки для macOS и Linux пока нет.

Код, комментарии, лог и тексты в чате — на русском.

## Команды

```powershell
dotnet build                                    # TreatWarningsAsErrors включён
dotnet run --project src\AgentsTracker.Gateway  # нужен appsettings.Local.json (рядом или в папке данных)
dotnet run --project src\AgentsTracker.Gateway -- protect-secrets   # зашифровать BotToken/Proxy, перенести конфиг в %LOCALAPPDATA%
dotnet publish src\AgentsTracker.Gateway -c Release -o C:\Apps\AgentsTracker   # установка: публикация…
C:\Apps\AgentsTracker\AgentsTracker.Gateway.exe install --start                # …и автозапуск; снять — uninstall
docker compose up -d --build                    # тот же шлюз в контейнере, нужен .env
```

Тестов нет. Всё, что трогает контракт с CLI, проверяется руками: запустить шлюз и смотреть лог.

**Шлюз запущен из `bin\Debug` папки `main`.** Пока он работает, `dotnet build` падает с
`MSB3021`; переключение ветки подменит исходники под процессом. Перезапуск, сборка в другую
папку и работа в worktree — `docs/operations.md`. Из сессии, запущенной из Telegram,
перезапускать нельзя — дайте пользователю команды оттуда.

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

**Эндпоинт монитора.** `api.MapGet` в `MonitorModule.MapEndpoints`, только чтение —
`docs/monitor.md`.

**Команду exe.** Класс в `Infrastructure/Cli/` с `public const string Name` и
`Run(string[] args, TextWriter output)`, строка в `switch` у `ConsoleCommands` и в её справке.
Команды отрабатывают до сборки хоста: DI и Telegram им недоступны, ответ — только в
`output`, код возврата 0 или 1. Заняты: `protect-secrets`, `install`, `uninstall`, `help`.
Всё, что начинается с дефиса, командой не считается — это аргументы конфигурации.

**Способ автозапуска (launchd, systemd).** Реализация `IAutostartInstaller` в
`Infrastructure/Autostart/` и строка в `AutostartInstaller.ForCurrentOs`. Приём один на все
ОС: положить файл-описание и позвать штатную утилиту через `ProcessAutostartInstaller.Run` —
решение по коду возврата, вывод у них локализован. XML для `schtasks` пишется в UTF-16:
в UTF-8 кириллица в описании задачи превращается в кракозябры.

**Ключ конфига.** Свойство в `GatewayOptions` (+ `Validate`), дефолт в `appsettings.json`,
пример `"//Ключ": "…"` в `appsettings.Local.example.json`, строка в README «Основные
настройки». Ключ агента — в `ClaudeOptions` и `Gateway:Claude`. Исключение —
`DataDirectory`: он нужен раньше конфига (в этой папке лежит сам `appsettings.Local.json`),
поэтому читается в `AppPaths.UseConfiguredDirectory` из `appsettings.json` рядом с exe
и окружения.

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
src/AgentsTracker.Gateway/
  Program.cs            папка данных, служебные команды, список агентов (выбор по Gateway:Agent) и фич
  Domain/               чистые модели: GatewayState (state.json), AuditEvent
  Infrastructure/       Configuration (GatewayOptions, ProjectCatalog), State (SessionStore),
                        Telegram (роутер, форматтер, Dispatch/), Audit, Monitoring (RunMonitor, RingBufferLog), Security,
                        Cli/ (install, uninstall, protect-secrets), Autostart/ (задача Планировщика через schtasks)
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

### Лимиты и деньги

Единственный ограничитель — `IAgentLimits` (окна тарифа), проверка в
`ChatWorker.ProcessAsync` перед запуском; при ошибке опроса запуск **пропускается**. Денег в
шлюзе нет: `total_cost_usd` не читается, `--max-budget-usd` не передаётся, в статистике —
только ходы, токены и время. Не возвращайте долларовые оценки. Кредиты («extra usage») агенту
запрещены. Детали — `docs/cli-contract.md`.

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
- Барьеры: `AllowedUserIds` + только личные чаты; MCP — всегда `127.0.0.1` плюс токен в
  заголовке; монитор без токена — поэтому только читает, а `MonitorBind: any` (нужен
  в контейнере) публикуют лишь на `127.0.0.1` хоста.
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
- Русские тексты и windows-пути правьте Edit/Write, не heredoc из Bash. Исходники — UTF-8
  **без BOM**. Остальное про инструменты — `docs/operations.md`.
- Комментарии объясняют, какой отказ предотвращает код, а не что он делает.

## Как работать

Правила от пользователя. Раньше жили в памяти, здесь надёжнее — файл читается каждой сессией.

- **Ответы — коротко и просто.** Сначала результат, потом только то, что нужно для действия.
  Без пересказа шагов и плана, жаргон — простыми словами. Ошибки, риски и «что дальше»
  не резать.
- **Длинные документы — `.md` файлом.** План, отчёт о ревью, разбор, сравнение — всё длиннее
  пары абзацев: временное в scratchpad, постоянное в `docs/` (только если это явно доки).
  Имя латиницей, kebab-case. В ответе — суть и ссылка; из Telegram — `SendUserFile`.
  Из режима плана файл тоже сохранить до `ExitPlanMode`.
- **Сабагенты только `model: "sonnet"`.** В каждом вызове `Agent` и в `agent()` Workflow.
  Не наследовать модель родителя, не брать opus/fable — экономия лимитов.
- **Среда — Windows, русская локаль, Москва (UTC+3).** Вывод консоли может прийти в
  кракозябрах — ставить UTF-8 (`chcp 65001`, `[Console]::OutputEncoding`). Сообщения
  Windows и .NET на русском — не искать по английскому тексту. В коде парсить и
  форматировать через `CultureInfo.InvariantCulture`. «Сегодня» — по Москве.
- **Коммитить самому, по логическим этапам.** Не ждать отдельной просьбы; файлы группировать
  по смыслу правки и коммитить по очереди. Заголовок на русском, одна строка, без тела.
- **Тег задачи в коммитах.** Пока задача ведётся в отдельной ветке или worktree, каждый её
  коммит начинается с короткого тега: `<тег> сообщение`. Тег произвольный, лишь бы по истории
  было видно, какие коммиты относятся к задаче.
- **Крупные задачи — в worktree.** Несколько фаз или перезапуски шлюза по ходу — начать с
  `git worktree add ..\AgentsTracker-<задача> -b <ветка>` и работать там; из Telegram
  переключить `/project` на новую папку. Мелкое в один-два коммита — прямо в `main`.
  После финального мержа ветки в `master` папку worktree удалить
  (`git worktree remove ..\AgentsTracker-<задача>`) и ветку тоже.
- **Ревью в worktree — с именем ветки.** `/code-review` без аргумента берёт незакоммиченный
  диф основного каталога, а не ветку worktree: 07.09.2026 он так отревьюировал и исправил
  чужие правки другой сессии в `master`. Вызывать `code-review medium --fix <ветка>`.
- **После правок — документация.** Проверить CLAUDE.md, README и комментарии к конфигу:
  новые флаги и команды, изменения контракта с CLI, неочевидные решения и их причины.
  Не добавлять очевидное из кода и историю правок.
- **После коммита — перезапуск шлюза.** Иначе в чате работает старый код. Из сессии через
  Telegram перезапускать самому нельзя (шлюз — родительский процесс, отложенная задача
  `schtasks` не отработала): дать пользователю три команды (Stop-Process, dotnet build,
  Start-Process) и попросить выполнить руками; временные экземпляры не поднимать без нужды.
  Из VS Code перезапускать можно, но **до** Stop-Process проверить `git status`: чужие
  незакоммиченные правки в `main` ломали сборку уже после остановки. Надёжный путь:
  `git worktree add --detach ..\AgentsTracker-run HEAD`, собрать оттуда с
  `-o src\AgentsTracker.Gateway\bin\Debug\net10.0`, worktree удалить, запустить как обычно.
- Из сессии через Telegram `AskUserQuestion` и `ExitPlanMode` ждут ≤5 минут: без ответа берите
  рекомендуемый вариант.

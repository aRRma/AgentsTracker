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
форма ответа `PermissionTool`), проверяются вручную: запустить шлюз, выполнить `claude -p …`
с теми же флагами против его MCP-эндпоинта и посмотреть лог.

Конфиг перекрывается переменными окружения — удобно для разовых проверок без правки файла:

```powershell
$env:Gateway__ProjectPath = 'C:\tmp\test'; $env:Gateway__AllowedUserIds__0 = '1'
```

Разовый прогон без правки конфига: запустить exe с `$env:Gateway__BotToken`,
`Gateway__AllowedUserIds__0` и **своим** `Gateway__McpPort` — иначе перезапишете
`mcp-gateway.json` работающего экземпляра, и его следующий `claude -p` упадёт. Два экземпляра
шлюза на одной машине не уживаются и по другой причине: бот один, `getUpdates` отдаётся 409.

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

Консоль отдаёт русский текст в cp866: в оболочке, ожидающей UTF-8, лог выглядит мусором —
смотрите его в PowerShell либо прогоняйте через `iconv -f cp866 -t utf-8`.

## Как вносить изменения

Новая команда чата: класс с `ITelegramCommandHandler` в папке нужной фичи + строка
`services.AddSingleton<ITelegramCommandHandler, …>()` в её `*Module` + запись в
`BotCommandsCatalog` (кнопка «Меню») и в тексте `HelpCommandHandler`.

Новый экран настроек: класс с `ISettingsScreen` в `Features/Settings/Screens/`, регистрация
в `SettingsModule`, кнопка на него — в `RootScreen`. `RenderAsync(ct)` асинхронный ради
лимитов; экрану без сети хватает `Task.FromResult(Render())` поверх приватного `Render()`.

Новая фича: папка в `Features/` с `*Module` и запись в списке модулей в `Program.cs`.
`ChatModule` там остаётся последним.

## Архитектура

### Слои и слайсы

```
src/AgentsTracker.Gateway/
  Program.cs            явный список IFeatureModule → AddGatewayConfiguration → AddGatewayInfrastructure
                        → ValidateStartup → MapFeatures
  GlobalUsings.cs       Domain, Infrastructure, .Configuration, .State, IOptions — доступны везде
  Domain/               чистые модели и правила без I/O и DI: PermissionModes, EffortLevels,
                        ClaudeRunResult, GatewayState (state.json), AuditEvent
  Infrastructure/       техническая часть, общая для фич (AppPaths, GatewayInfrastructure — в корне):
    Configuration/      GatewayOptions (+Validate), ProjectCatalog (Normalize/Same — ключ сессий)
    Claude/             ClaudeRunner (процесс claude -p), ClaudeCliLocator, ClaudeLimits, ClaudeCliJson
    Mcp/                McpConfigFile — mcp-gateway.json, токен в заголовке, RoutePattern = /mcp
    State/              SessionStore — state.json под Lock, атомарная запись
    Telegram/           TelegramBotService (роутер), TelegramClientFactory, TelegramFormatter,
                        DisplayFormat, BotCommandsCatalog,
                        Dispatch/ — ITelegramCommandHandler / ITelegramCallbackHandler / ITelegramTextHandler
    Audit/              IAuditLog, JsonlAuditLog — журнал «кто, куда, что»
    Security/           DPAPI-шифрование конфига, ACL папки данных, команда protect-secrets
    Modules/            IFeatureModule — AddServices + MapEndpoints
  Features/             вертикальные слайсы, каждый со своим *Module:
    Approvals/          PermissionTool (MCP), ApprovalBroker, ApprovalCardRenderer, /rules; единственный HTTP-эндпоинт
    Chat/               ChatWorker (очередь и запуск), /new /stop /status, fallback-обработчик текста
    Settings/           SettingsMenuCoordinator + Screens/*Screen (ISettingsScreen), /menu /model /effort /mode …
    Help/               /start /help
    Audit/              /audit
```

Правила разложения: в `Domain` ничего не открывает файлы и не ходит по сети; `Infrastructure`
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
   `ChatEnqueueTextHandler` (всегда `true`). Поэтому `ChatModule` в `Program.cs` **последний**.
   Неизвестные слэш-команды сюда и попадают — это команды самого Claude Code (`/review` и прочие).

Callback-и делят один поток: `ITelegramCallbackHandler.CanHandle` — префикс `cfg:` у меню,
всё остальное (hex-id запроса) у `ApprovalBroker`.

`ActiveChatId` брокера выставляет `ChatWorker` перед самым запуском, а не обработчик сообщения:
иначе карточки уже идущего запуска ушли бы в чат другого пользователя.

### Кольцо «шлюз → CLI → шлюз»

Шлюз одновременно **запускает** `claude.exe` и **обслуживает** его.

```
Telegram ──▶ TelegramBotService ──▶ ChatWorker ──▶ ClaudeRunner ──▶ claude.exe -p
                    ▲                                                    │
                    │            карточка с кнопками                     │ нужно разрешение
                    └── ApprovalBroker ◀── PermissionTool ◀── MCP http://127.0.0.1:<порт>/mcp
                                                              Authorization: Bearer <токен>
```

`McpConfigFile` при старте генерирует токен, пишет `mcp-gateway.json` с заголовком
`Authorization` и удаляет файл при остановке; `ApprovalsModule.MapEndpoints` монтирует `/mcp`
с фильтром `McpConfigFile.Authorizes`. `ClaudeRunner` передаёт CLI `--mcp-config` с этим файлом
и `--permission-prompt-tool mcp__tg__approve`. Правя одну сторону, проверяйте вторую: имя сервера
и инструмента — константы `McpConfigFile`, а атрибуту `[McpServerTool]` нужна константа времени
компиляции — отсюда переприсваивание в `PermissionTool`.
README — простая инструкция для пользователя, без подробностей устройства; меняя защиту
эндпоинта или папку данных, проверьте его раздел «Безопасность».

### Контракт подтверждений (проверен на живом CLI, схема входа не задокументирована)

CLI зовёт инструмент с `{"tool_name":…,"input":{…},"tool_use_id":…}`. Ответ — JSON-строка:

- `{"behavior":"allow","updatedInput":{…}}` — `updatedInput` **обязателен**, без него CLI считает
  результат невалидным и отклоняет вызов;
- `{"behavior":"deny","message":"…"}`.

`PermissionTool` читает поля защитно (`Read(...)` перебирает snake_case/camelCase); сырой payload
пишется только на Debug — на Information для Edit/Write это было бы содержимое файлов.
`AskUserQuestion` приходит в тот же инструмент и требует вернуть `updatedInput` с исходным
`questions` и собранным `answers`.

Кнопка «Всегда» ведёт себя двояко: если CLI прислал `permission_suggestions` с
`destination: localSettings`, правило записывает **сам CLI** в `.claude/settings.local.json`
проекта (обычно префиксное, шире точного совпадения), и `ApprovalCardRenderer` показывает именно
эти правила; иначе шлюз запоминает точную сигнатуру в `state.json`, и её видно в `/rules`.

`ApprovalBroker` держит вызов MCP открытым на `TaskCompletionSource`, пока пользователь не нажмёт
кнопку, и возвращает `ChoiceResult` (ключ + кто нажал — для аудита). `WaitAsync` намеренно
различает таймаут и отмену: отменённый `/stop` запуск должен бросать `OperationCanceledException`,
а не выглядеть как «не ответил вовремя».

### `--permission-mode` передаётся всегда

Без явного флага действует `permissions.defaultMode` из `~/.claude/settings.json` пользователя.
У него там `auto` — решения принимает классификатор, и кнопки в чате не появляются вовсе.
Не убирайте этот аргумент из `ClaudeRunner.BuildArguments`.

`PermissionModes.Selectable` (`plan`/`default`/`acceptEdits`/`auto`) — то, что можно переключать из
чата. `dontAsk` и `bypassPermissions` исключены намеренно: полное снятие подтверждений остаётся
правкой конфига на самой машине.

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
смотрит соседей `ProjectPath`. `ProjectScreen` листает список страницами по 12: репозиториев
бывает больше, чем влезает в клавиатуру, и без страниц они были бы недостижимы.

Сессии Claude Code ключуются **нормализованным путём проекта** (`ProjectCatalog.Normalize`):
`--resume` работает только в той папке, где сессия создана. Переключение репозитория из меню меняет
и активную сессию. `ClaudeRunner` фиксирует `SessionId` и `ProjectPath` в начале запуска — иначе
переключение посреди работы развело бы рабочий каталог процесса и проект, которому пишется сессия.

Id новой сессии выдаёт **шлюз** (`--session-id <uuid>`) и регистрирует её сразу после старта
процесса, не дожидаясь ответа CLI: иначе `/stop`, таймаут или падение первого запуска теряли бы
ветку целиком. Продолжение идёт через `--resume`. Сессия сбрасывается только когда CLI прямо
говорит, что не нашёл её (`LooksLikeMissingSession`), и только через `TrySetSessionId(onlyIfActive)`,
чтобы не перетереть `/new` или смену сессии, сделанные во время запуска.

### Аудит

`IAuditLog.Write(AuditEvent.Now(kind, summary, userId, chatId, project, session, outcome))` —
короткая строка «кто, куда, что», без секретов и полных текстов (не длиннее 200 символов).
Виды — константы `AuditKinds`: `access.rejected`, `message`, `run.start`/`run.end`, `approval`,
`question`, `settings`, `rules`, `session.reset`, `budget.refused`, `gateway`. Пишут: роутер
(доступ, команды), `ChatEnqueueTextHandler` (промпт), `ChatWorker` (запуски, бюджет),
`PermissionTool` (решения по карточкам, правила), экраны меню через `SettingsAudit.Changed`,
`ClaudeRunner` (сброс сессии). Смотреть — `/audit [n]` или файл. Это не замена `ILogger`:
в аудит идёт то, за что отвечает человек, в лог — то, что нужно для отладки.

### Деньги и лимиты тарифа

Три независимых ограничителя, все проверяются в `ChatWorker.ProcessAsync` перед запуском:

1. `DailyBudgetUsd` — сумма из `state.json` за календарный день пользователя;
2. `RunBudgetUsd` → `--max-budget-usd`, но не больше остатка дневного бюджета;
3. `ClaudeLimits` — тарифные окна (`five_hour`, `seven_day`, `seven_day_<модель>`).

Суммы в чат не выводятся: на подписке они ничего не значат, а кредиты запрещены. Вместо них
`ClaudeLimits.ShortSummaryAsync` (сводка меню, `/status`) и `RemainingLinesAsync` (экран
статистики) показывают остаток окон. Ради этого `ISettingsScreen.RenderAsync` асинхронный —
отрисовка ходит в сеть, пусть и через кэш. Оценка стоимости продолжает копиться в `state.json`:
на ней держится `DailyBudgetUsd`, единственное место, где суммы ещё видны.

`ClaudeLimits` ходит в **недокументированный** `api.anthropic.com/api/oauth/usage` с токеном
подписки из `~/.claude/.credentials.json`. Эндпоинт требует правдоподобный User-Agent, отвечает 429
на частый опрос (отсюда кэш на 3 минуты) и может исчезнуть в любой версии — при любой ошибке запуск
**пропускается**, а не блокируется, иначе шлюз замолчал бы целиком.

Кредиты («extra usage») агенту тратить запрещено: `ClaudeRunner` ставит процессу
`DISABLE_EXTRA_USAGE_COMMAND=1` и вычищает из его окружения `Gateway__*`, а при обрыве по лимиту
`ChatWorker` снимает всю очередь — следующие задачи упёрлись бы в тот же лимит.

### Вывод в Telegram

`TelegramFormatter` переводит markdown в подмножество HTML (`<b> <i> <s> <code> <pre> <a>`) и режет
под лимит 4096 — резать нужно **исходный markdown до конвертации**, иначе рвутся теги. Курсив
намеренно не разбирается: одиночные `*` и `_` слишком часто встречаются в путях и коде. Блок кода
длиннее лимита уходит файлом. При отказе Telegram разбирать разметку `ChatWorker` шлёт тот же текст
без `ParseMode`. Суммы, токены и время форматирует `DisplayFormat` (extension members C# 14).

Карточки подтверждений собираются через `EscapeCapped` с побюджетными лимитами на каждый фрагмент:
длинная команда иначе переполнит сообщение, отправка упадёт, а исключение превратится в отказ.

## Что стоит держать в голове

- Шлюз **не подключается** к сессии, открытой в VS Code. Это отдельная параллельная сессия на той же
  папке: общие `CLAUDE.md`, `.claude/settings.json`, хуки и MCP проекта, но своя история диалога.
- `--bare` использовать нельзя: он не читает `~/.claude` и ломает OAuth-логин по подписке.
- `claude.exe` ищет `ClaudeCliLocator`: конфиг → стандартные пути → PATH → бинарник внутри
  расширения VS Code. Последний — только чтобы шлюз завёлся на машине без своего CLI: путь
  содержит версию расширения и исчезает при его обновлении, поэтому на такой находке пишется
  предупреждение. Штатно нужен отдельный CLI (`irm https://claude.ai/install.ps1 | iex` →
  `~/.local/bin/claude.exe`) — он обновляется сам и переживает переустановку VS Code.
  Найденный путь кешируется, но перепроверяется перед каждым запуском: пропавший бинарник
  ищется заново, а не валит каждое сообщение до перезапуска шлюза. Версию (`--version`)
  `ValidateStartup` пишет в лог — контракт разбора JSON держится на поведении конкретной версии.
- Барьеры аутентификации: `AllowedUserIds` + только личные чаты; Kestrel слушает только `127.0.0.1`,
  MCP-эндпоинт требует токен в заголовке.
- Аргументы CLI собираются через `ProcessStartInfo.ArgumentList` — не склеивайте командную строку
  руками.
- `Channel.CreateUnbounded` в `ChatWorker` намеренно без `SingleReader`: с ним `Reader.Count` бросает
  `NotSupportedException`, и `/status` падает.
- `McpConfigFile` и `SessionStore` — единственные классы с классическим конструктором: у обоих
  побочный эффект при создании (запись/чтение файла), который должен случиться один раз до старта.
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

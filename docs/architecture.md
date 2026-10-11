# Как устроен шлюз

Когда читать: нужно найти, где что лежит, или понять, почему оно стоит именно здесь. Чек-листы
добавления команды, экрана, агента и канала — в [extending.md](extending.md), недокументированная
часть договора с CLI — в [cli-contract.md](cli-contract.md), слова из кнопок, сообщений и этого
документа — в [glossary.md](glossary.md).

## Проекты

```
src/AgentsTracker.Agents.Abstractions/   контракты агента, без Telegram и без конкретного CLI
  IAgentBackend         Probe(), RunAsync(AgentRunRequest, IAgentRunObserver), InspectProject (отпечаток и пункты настроек агента в проекте)
  AgentRun.cs           запрос (промпт, папка, сессия, модель, effort, режим, таймаут, папка вложений, вид), наблюдатель, результат
  AgentCapabilities     модели/effort/режимы, которые поддерживает агент (effort null — не поддерживает)
  IOperatorConsole      что агент просит у человека: ApproveAsync, AskAsync, SendFileAsync; PersistentRule — правило «всегда»
  IAgentLimits, IAgentSkillCatalog, IAgentBackendModule (AddServices + MapEndpoints; AgentHost — папка данных, порт, прокси, таймаут карточки)
  RunUsage, RunActivity, LimitMath (единственная формула процента), Text (обрезка, Text.Redact)
src/AgentsTracker.Agents.Claude/         Claude Code за этими контрактами
  ClaudeAgentModule, ClaudeBackend (процесс claude -p), ClaudeCapabilities (PermissionModes.Selectable — что можно выбрать из чата),
  ClaudeLimits, ClaudeSkillCatalog, ClaudePluginRegistry, ClaudeCliLocator, ClaudeStreamEvent, ClaudeCliJson, ClaudeOptions
  ClaudeProjectSettings хуки, allow, MCP-серверы и скиллы проекта, с отпечатком
  ClaudeProcessJob      Windows Job Object: каждый claude и его потомки умирают вместе со шлюзом
  Mcp/                  McpConfigFile (конфиг на запуск: токен и id запуска), ClaudePermissionTool (CLI ↔ IOperatorConsole), ClaudeSendFileTool
src/AgentsTracker.Channels.Abstractions/ контракты канала, без конкретного мессенджера
  IChatChannel          ConnectAsync/ListenAsync, Send/Edit/Delete/Acknowledge, SendDocumentAsync/SendPhotoAsync, DownloadAttachmentAsync;
                        ChannelLimits — пределы сообщения, подписи к файлу, надписи и данных кнопки, документа, фото и скачивания
  RateLimitRetry        одна повторная попытка после 429 с коротким retry_after, общая для всех отправок
  ChatId, UserId        Key — «канал:значение» для state.json, журнала и лога
  Messages.cs           OutgoingMessage (ReplyTo), Keyboard, MessageRef, IncomingMessage, IncomingAttachment, ButtonPress, ChatCommand
  IChatInbound          куда канал отдаёт входящее (реализует ChatDispatcher)
  ChatHtml              канонический формат текста (b, i, s, code, pre, a, blockquote) и экранирование
  ChannelRequestException  единственное исключение канала наружу: RateLimited, MarkupRejected, CannotReach, Unauthorized
  IChatChannelModule    AddServices + MapEndpoints, SecretKeys; ChannelHost — общий прокси
src/AgentsTracker.Channels.Telegram/     Telegram за этими контрактами
  TelegramChannel       long polling, инлайн-кнопки, HTML; «message is not modified» и retry_after живут здесь
  TelegramChannelModule, TelegramOptions, TelegramIds, TelegramClientFactory (клиент ленивый: конструктор проверяет токен)
src/AgentsTracker.Gateway/
  Program.cs            папка данных, служебные команды, списки агентов (Gateway:Agent), каналов (Gateway:Channel:Type) и фич
  Domain/               чистые модели: GatewayState (state.json), AuditEvent и AuditKinds
  Infrastructure/       GatewayInfrastructure.cs (слои конфигурации, DI, Kestrel, ValidateStartup, MapFeatures), AppPaths, Modules/IFeatureModule,
                        Configuration (GatewayOptions, ProjectCatalog), State (SessionStore, CacheGuard), Git, Audit, Monitoring (RunMonitor, RingBufferLog),
                        Chat (ChatGatewayService, ChatDispatcher, DisplayFormat, MarkdownRenderer,
                        StartupNotice — «запущен»/«прерван» после перезапуска, Dispatch/ — контракты обработчиков),
                        Security (секреты DPAPI, ProtectSecretsCommand, DataDirectoryAcl), Cli/ (ConsoleCommands направляет install, uninstall, protect-secrets),
                        Autostart/ (задача Планировщика через schtasks),
                        Container.Detected (в контейнере `install` отказывает, а старт предупреждает, если `MonitorBind` не подходит: там нужен `any`)
  Features/             вертикальные срезы, у каждого свой *Module
    Approvals/          ApprovalBroker, ApprovalCardRenderer, OperatorConsole, /rules
    Chat/               ChatWorker (две полосы), RunStatusMessage, /new /stop, AttachmentInbox (картинки из чата),
                        ProjectTrustGate («Доверять проекту?» до запуска задачи)
    Settings/           SettingsMenuCoordinator + Screens/*, SkillLauncher, PendingConfirmations (второе нажатие для необратимых кнопок),
                        /menu /settings /status /sessions /agent /model /effort /mode /skills /project /usage
    Question/           /ask и кнопки «Задать вопрос»: разовый запуск без сессии (QuestionLauncher)
    Help/ Audit/ Monitor/   /start /help; /audit; веб-страница (index.html — EmbeddedResource) и /api/*
```

Правила слоёв (они же в CLAUDE.md — их нарушение архитектурная ошибка, а не опечатка): `Domain` и
`Abstractions` не открывают файлы и не ходят в сеть; `Gateway` знает о конкретном агенте и канале
только в `Program.cs`; бэкенд и канал не знают друг о друге и о `GatewayOptions`; `Infrastructure`
ничего не знает о фичах (кроме контрактов `Dispatch`); фича зависит от фичи только через публичный
сервис (`Settings` → `ChatWorker.IsBusy`).

## Путь сообщения

`ChatDispatcher` пропускает только `IChatChannel.AllowedUsers` и личные чаты (`ChatKind.Unknown` —
тоже отказ). Текст разбирается по порядку:

1. слэш-команда из `IChatCommandHandler.Commands` — **в первую очередь**, иначе `/stop` ушёл бы в
   ожидающий свободный ответ, и прервать зависший запуск было бы нечем;
2. цепочка `IChatTextHandler` в порядке модулей: ответ на карточку (`ApprovalTextHandler`) →
   аргументы скилла (`SkillArgumentsTextHandler`) → текст вопроса после голого `/ask` или кнопки
   «Задать вопрос» (`QuestionTextHandler`) → очередь агента (`ChatEnqueueTextHandler`, всегда `true`).
   Поэтому `ChatModule` последний. Незнакомые слэш-команды — команды самого Claude Code, они уходят
   в CLI.

Порядок держится и для подписи к картинке: `/stop` работает с приложенной картинкой, картинка не
сохраняется, а диспетчер об этом говорит. Сообщение с вложением — не аргументы скилла:
`SkillArgumentsTextHandler` пропускает его дальше и продолжает ждать. `ApprovalTextHandler`, наоборот,
забирает его себе (повисшая карточка заморозила бы запуск) и предупреждает, что картинка никуда не пошла.

Картинки `ChatEnqueueTextHandler` берёт через `AttachmentInbox`: скачивает в
`<папка данных>\inbox\<чат>\<проект>\`, проверяет тип по сигнатурным байтам, подставляет абсолютные
пути в промпт. Запуск получает эту папку чата в `--add-dir`, и ничего больше
([cli-contract.md](cli-contract.md)).

Кнопки: цепочка `IChatButtonHandler` (`Dispatch/`), каждый узнаёт своё в `CanHandle`: `cfg:` — меню
(`SettingsCallbackHandler`), всё остальное (шестнадцатеричный id запроса) — `ApprovalCallbackHandler` →
`ApprovalBroker`.

Перед запуском задачи `ChatWorker` спрашивает `ProjectTrustGate`: если `IAgentBackend.InspectProject`
находит в проекте настройки агента и их отпечатку ещё не доверились, карточка «Доверять проекту?»
уходит через `ApprovalBroker` под своим коротким `RunScope` (поэтому `/stop` её снимает).
«❌ Отклонить», молчание и `/stop` значат одно: запуска не будет. Первые два ещё и снимают очередь задач. После
карточки проект читается заново: если `/project` сменился, проверяется и новый. Почему —
[cli-contract.md](cli-contract.md), «Настройки проекта и диалог доверия».

### Две полосы: задача и вопрос параллельно

У `ChatWorker` две полосы, у каждой своя очередь и свой `CancellationTokenSource`: задачи (и
`/context`, `/compact`) по одной, вопросы (`AgentRunKind.Question`) по одному, а задача с вопросом
идут **параллельно** — иначе вопрос, который задают как раз чтобы не ждать, ждал бы долгую задачу.
Больше двух процессов агента разом не бывает.

- `IsBusy`/`QueueLength` — только про задачи: меню, смена сессии и «Сжать» вопрос не ждут.
  `IsAnswering`/`QuestionQueueLength` — для строки статуса.
- `/stop` (`ChatWorker.Stop`) прерывает обе полосы и зовёт `ApprovalBroker.CancelAll`.
- Отказ по лимиту, `RateLimited` или отказ в доверии снимает только свою полосу: у вопроса своя
  модель, а другая полоса всё равно проверит лимит перед своим запуском.
- Вопрос не трогает `RunMonitor` (текущий запуск и очередь там — задачи), но виден в истории запусков
  и в журнале. `state.json` держит задачу в `ActiveRun`, а вопрос в `ActiveQuestion`; после
  перезапуска `StartupNotice` сообщает про оба.
- Вопрос получает ответ **ответом (reply)** на сообщение пользователя: «Вопрос принят», статус,
  каждая часть ответа, его карточки и файлы. Путь: `IncomingMessage.Message` → `QueuedPrompt.ReplyTo` →
  `OutgoingMessage.ReplyTo` и параметр `replyTo` у файловых методов канала. Telegram шлёт его с
  `AllowSendingWithoutReply`: иначе удалённый вопрос превратил бы ответ в 400.

Каждый запуск регистрируется прямо перед стартом процесса: `ApprovalBroker.BeginRun(RunScope)` — id
запуска, чат, сообщение для ответа, проект для путей на карточке и для `send_file`, сессия для
журнала. Карточки и файлы идут в чат запуска; снятие регистрации в `finally` запуска отменяет только
**его** карточки. «Текущего чата» у брокера нет.

Ожиданий свободного текста («Отклонить с причиной», «Свой ответ») много: ждать могут задача, вопрос и
параллельные сабагенты разом. `ApprovalBroker.DeliverText` отдаёт текст тому ожиданию, на чью просьбу
пользователь ответил (`IncomingMessage.InReplyTo`), иначе единственному ожиданию в этом чате. Если
ожиданий несколько и ответа нет, `ApprovalTextHandler` просит ответить на нужную карточку, а текст не
становится новой задачей.

## Петля шлюз → CLI → шлюз

Шлюз одновременно запускает агента и обслуживает его:

```
канал ─▶ ChatDispatcher ──▶ ChatWorker ──▶ ClaudeBackend ──▶ claude.exe -p
                    ▲                                                     │ нужно разрешение
                    └── ApprovalBroker ◀── OperatorConsole ◀── ClaudePermissionTool ◀── MCP http://127.0.0.1:<McpPort>/mcp
                                                                              Authorization: Bearer <токен>
```

На старте `McpConfigFile` выдаёт токен. У каждого запуска свой файл конфигурации
(`McpConfigFile.ForRun`): `mcp-gateway-<pid>-<запуск>.json` с токеном и заголовком
`X-AgentsTracker-Run: <RunId>`. `ClaudeBackend` удаляет его, когда процесс завершился, шлюз убирает
остатки при остановке. Это файл, а не JSON строкой в `--mcp-config`: аргументы попадают в лог, а с ними
попал бы и токен. В имени PID: общий файл перезаписывал и удалял второй экземпляр, и рабочий шлюз потом
умирал на «mcp__tg__approve not found». Файлы мёртвых PID подметаются на старте. `/mcp` монтируется с
фильтром `McpConfigFile.Authorizes`; CLI получает `--mcp-config` и
`--permission-prompt-tool mcp__tg__approve`. Имена сервера и инструмента — константы `McpConfigFile`,
`[McpServerTool]` берёт те же.

По id запуска вызов находит свой запуск: инструменты читают заголовок через `IHttpContextAccessor`
(SDK выполняет инструмент в `ExecutionContext` его HTTP-запроса) и кладут его в
`ApprovalRequest.RunId`/`FileSendRequest.RunId`/`AskAsync(runId, …)`. `OperatorConsole` ищет запуск
через `ApprovalBroker.Run(runId)`; незнакомый или пустой id (запуск уже кончился) — отказ, а не
карточка в чужом чате. Сабагенты живут в том же процессе CLI и шлют тот же заголовок.

`timeout` MCP-сервера в конфиге — это `ApprovalTimeoutMinutes` плюс одна минута: без него CLI обрывал
вызов после 5 минут молчания, и карточка потом была мертва ([cli-contract.md](cli-contract.md)).

Второй инструмент, `mcp__tg__send_file` (`ClaudeSendFileTool`), идёт так: `IOperatorConsole.SendFileAsync`
→ `OperatorConsole` (проверяет папку, тип и размер, пишет `file.send` в журнал) →
`ApprovalBroker.SendFileAsync` → `IChatChannel.SendDocumentAsync`/`SendPhotoAsync`. Отказ — всегда
ответ `{"sent":false,"reason":…}`, никогда не исключение. Сами проверки —
[cli-contract.md](cli-contract.md), «Файл в чат».

`McpConfigFile`, `SessionStore` и `ClaudeProcessJob` — единственные с классическим конструктором:
побочный эффект (уборка старых файлов конфигурации, чтение файла состояния, создание Job Object)
должен случиться ровно один раз, при создании синглтона.

## Сессии и выбор из чата

Сессии ключуются **нормализованным путём проекта** (`ProjectCatalog.Normalize`); id новой выдаёт шлюз
(`--session-id`) и сбрасывает только по `SessionLost` от CLI ([cli-contract.md](cli-contract.md)).

Выбор из чата (`SessionStore`) лежит поверх конфига: `EffectiveModel`, `EffectivePermissionMode`,
`EffectiveEffort`, `ProjectPath`. Проект, равный конфигу, хранится как `null` (`SetProjectPath`): иначе
правка конфига молча перебивалась бы старым выбором. Модель, effort и режим хранятся как выбраны, а
кнопки «по умолчанию» / «из конфига» (или `reset`) возвращают `null`. Модель хранится алиасом, который
уходит агенту (`opus`), но человек видит её только через `SessionStore.ModelLabel` — с версией, которой алиас ответил в последний
раз (`ModelVersions` в `state.json`), иначе по `Describe` агента.

### Контекст и виды запуска

`AgentRunRequest.Kind` говорит бэкенду, что за запуск: `Task` — задача в сессии, `Question` — вопрос
(без `--resume`, ничего не пишется на диск, `SessionId` в итоге всегда `null`, чтобы хост не сделал
разовый id активным), `ContextReport` — `/context` (модель не вызывается) и `Compact` — `/compact`
(сессия та же). Команды двух последних — свои у бэкенда, хост спрашивает только `Capabilities.Context`.
`ChatWorker` не проверяет лимит для `ContextReport` (он бесплатный и нужнее всего на исчерпанном
тарифе) и отказывает обоим видам контекста, если сессию сняли, пока они ждали в очереди, — запуск без
сессии создал бы её впустую.

Заполненность контекста берётся из самого запуска: `RunUsage.ContextTokens` — вход **последнего** хода
основного потока (у сабагентов своё окно), после `/compact` — его `post_tokens`; `ContextWindow` — окно
основной модели. `SessionStore.RecordContext` кладёт их в `SessionRecord`, и экран «Контекст» показывает
их без запуска агента.

Срок кэша тоже берётся из запуска, в том числе прерванного (`AgentRunResult.LastRequestUtc` и
`CacheTtl`, [cli-contract.md](cli-contract.md), «Контекст»). `SessionStore.RecordCache` превращает их в
`SessionRecord.CacheUntilUtc` (запуск, ничего не записавший в кэш, срока не сообщает, и берётся последний известный).
Подпись печатает его как «🧊 кэш до 15:42» — время, а не обратный отсчёт: сообщение больше не меняется.
`ContextScreen.Cache` — единственное место, где срок становится текстом для «Статуса» и «Контекста»;
`/api/stats` отдаёт его как есть в `sessions[].cacheUntilUtc`.

Ещё сессия помнит, на чём записан кэш: `CacheModel` (версия, которая отвечала) и `CacheMode` — режим,
если он из `AgentCapabilities.OwnCacheModes` (`plan` у Claude Code), иначе `null`: остальные режимы кэш
делят. Правила держит `CacheGuard`:

- `WillReset` — шаг от того, на чём записан кэш, пока кэш жив и контекст не меньше
  `MinContextTokens` (100k); шаг обратно кэш спасает и ни о чём не спрашивает. На экране «Агент» такая
  кнопка сначала спрашивает через `PendingConfirmations`; набранные руками `/model` и `/mode` — уже
  решение, они выполняются сразу (`AgentScreen.Now`), а предупреждение дописывается к ответу.
- `Mismatch` добавляет к строке кэша «…следующий запуск запишет кэш заново», если выбор в меню уже
  разошёлся с кэшем.
- `Miss` — промахи считаются, а не угадываются: `RunUsage.FirstCacheRead` — сколько первый запрос
  основного потока прочитал из кэша; продолжение задачи — промах, если это меньше половины прошлого
  контекста сессии (от `MissContextTokens`, 20k: меньше — на кону только общие инструменты и системный
  промпт). `UsageTotals.CacheMisses` выводится в `/usage` и `/api/stats` (`total`, `byDay`).

«Сжать» и «Новая сессия» на «Контексте» тоже идут через `PendingConfirmations`: «Да» срабатывает лишь
для того же действия, той же сессии и в пределах 2 минут; «Нет» или повторное открытие экрана снимают
вопрос.

`ProjectCatalog`: список `Gateway:Projects`, иначе обход `Gateway:ProjectsRoot` до `ProjectsRootDepth`,
иначе соседи `ProjectPath`. `ProjectScreen` выбирает в два шага (папка → проект) страницами по 12;
после выбора страница сбрасывается на первую, иначе метка `▶` могла бы оказаться за пределами экрана.

`GitRepository` даёт ветку проекта для «Статуса», списка проектов, статуса запуска, подписи под
ответом, `run.start` в журнале и монитора; пометка 🌳 (проект — worktree) есть только в «Статусе» и
списке проектов. `.git` ищется вверх до корня диска, как это делает git. Класс читает файлы `.git` сам,
без процесса `git`: подпись нужна на каждом такте статуса запуска. Это ветка папки проекта, а не «ветка
агента»: в другой worktree агент может уйти по абсолютным путям, и шлюз этого не увидит. Правила,
которые нельзя ослаблять:

- файл открывается с `FileShare.ReadWrite | FileShare.Delete` и только после смены времени записи
  или длины — иначе `git checkout` агента на Windows падает на переименовании `HEAD.lock`;
- UNC-путь и сетевой диск не читаются: путь в `.git` пишет тот, кто дал проект, а UNC заставил бы
  Windows отдать чужому серверу хэш учётной записи;
- показывается только `ref: refs/heads/<имя>` или SHA, и класс не бросает исключений — сбой подписи
  не должен стоить ответа агента.

Подпись (`GitHead.Label`) обрезана до 40 знаков; экранирует её вызывающий, потому что git разрешает в
имени ветки `<`, `&`, `_` и обратную кавычку. Подпись под ответом сравнивает ветку на старте и в конце
запуска и показывает `master → feature`, если агент переключил её сам.

## Состояние, секреты, слои конфигурации

`AppPaths.DataDirectory` — это `%LOCALAPPDATA%\AgentsTracker\` (ACL — владелец и SYSTEM) или
`Gateway:DataDirectory`, если он задан. Там лежат `state.json` (атомарно),
`mcp-gateway-<pid>-<запуск>.json`, пока идёт запуск, `appsettings.Local.json` с секретами, `inbox\` и
`audit\audit-ГГГГ-ММ.jsonl`.

`SessionStore.Save` пишет `state.json.tmp` с `Flush(true)` и подменяет файл через `File.Replace`,
оставляя прежнюю версию в `state.json.bak`: без сброса NTFS после отключения питания сохраняет
переименование, но не данные, и файл оказывается пустым. Файл, который не читается, откладывается как
`state.json.corrupt-<время>` (следующая запись его не затирает), состояние берётся из `.bak`, а
`SessionStore.LoadProblem` попадает в «Шлюз запущен» — так сессии, доверие и правила не теряются молча.

Конфиг собирается слоями: `appsettings.json` → `appsettings.Local.json` рядом с exe (IDE) → он же в
папке данных (production) → переменные окружения. `dpapi:…` расшифровывается при загрузке
(`ProtectedJsonConfigurationProvider`); `protect-secrets` шифрует `Gateway:Proxy` и ключи
`IChatChannelModule.SecretKeys` в `Gateway:Channel:Settings` (у Telegram — `BotToken`, `Proxy`). В
`dotnet publish` копирует рядом с exe открытый `appsettings.Local.json` из папки проекта, а `install`
переносит его в папку данных и шифрует. Переменные `Gateway__*` невидимы дочернему `claude` — хост вычищает их из
окружения в `ValidateStartup`. Пользовательская сторона — в [deployment.md](deployment.md).

Переехавшие ключи канала перечислены в одном списке, `ChannelOptions.MovedKeys`: его читают и стартовая
проверка, и `protect-secrets`. Проверять на копии старого конфига: `protect-secrets <путь>` должен
вернуть 1 с подсказкой, а `migrate-channel-settings.ps1 -Path <путь>` — не трогать то, что уже
заполнено в `Channel:Settings`.

## Журнал

`IAuditLog.Write(AuditEvent.Now(kind, summary, user, chat, project, session, outcome))` пишет журнал
(`/audit`, монитор) — «кто, где, что». Адреса хранятся строками `UserKey`/`ChatKey`
(«канал:значение»), а не числами: id из разных каналов сталкиваются. `NowByKeys` пишет по готовым
ключам из `state.json`; `Now` идёт через него, поэтому маска стоит в одном месте.

Секретов и полных текстов нет: запись — до 200 символов, текст пользователя — выжимка `Text.Preview` в
80. `NowByKeys` маскирует секреты в каждой записи через `Text.ClipRedacted`: подтверждение пишет саму
команду, а в команде так же легко оказывается токен бота из переменной окружения или заголовка curl —
журнал монитор отдаёт без пароля. `Text.Redact` узнаёт токены по форме (Telegram, `sk-`, GitHub, AWS,
Slack, JWT), `Authorization`/`Bearer`, пароль в адресе и значения ключей вроде `token=`/`--password`;
`Text.Preview` тоже маскирует (лог, монитор, статус запуска).

Виды перечислены в `AuditKinds`: `access.rejected`, `message`, `run.start`/`run.end`, `approval`,
`question`, `file.send`, `file.receive`, `settings`, `rules`, `trust`, `session.reset`,
`limit.refused`, `gateway`. Экраны меню пишут через `SettingsAudit.Changed`. В журнал идёт то, за что
отвечает человек, в `ILogger` — то, что нужно для разбирательства.

## Вывод в чат

`MarkdownRenderer` переводит markdown в `ChatHtml` и режет по `IChatChannel.Limits` (`MessageLength`, у
Telegram 3800). Обычный текст режется как **исходный markdown до преобразования**, иначе рвутся теги;
блок кода меряется готовым HTML, и блок длиннее предела уходит файлом. Списки — `•`/`◦`, таблица —
выровненный `<pre>`. Курсив только для `*`, прижатой к содержимому на границе слова (иначе курсивом
уходили `*.cs` и `2 * 3`); `_` не разбирается (`snake_case`). Карточки и строки `/audit` экранируют в
рамках бюджета через `ChatHtml.EscapeCapped` (текст, влезающий целиком, не тратит символ на
многоточие).

Любой 400 от канала, кроме «chat not found» (неподдерживаемый тег, «too long» после экранирования —
не только «can't parse»), повторяет отправку без разметки (`ChatHtml.StripTags`), иначе часть ответа
молча пропала бы. На 429
посреди многочастного ответа выжидают `RetryAfter` (≤30 с) и повторяют ту же часть —
`ChatWorker.SendPartAsync` и, для файла от агента, `ApprovalBroker`, оба через
`RateLimitRetry.OnceAsync`. Суммы, токены и время — `DisplayFormat`.

Подпись под ответом (`ChatWorker.Footer`), по порядку:

- id сессии или «💬 вопрос»;
- ветка проекта в код-спане (`🌿`);
- модель, которая **на самом деле** ответила (`RunUsage.PrimaryModel`, сокращённая до
  `opus-5-5`/`haiku-4-5`): `message.model` последнего хода основного потока, а не модель с наибольшим
  числом выходных токенов в `modelUsage` — сабагент на sonnet легко пишет больше самой сессии
  ([cli-contract.md](cli-contract.md), «Контекст»). `ModelLabel` выбранной модели — последняя замена,
  если CLI ничего не сказал;
- вплотную к модели, в скобках, **переданный** effort одной-тремя буквами (`opus-5-5(H)`;
  `DisplayFormat.EffortShort`: L, M, H, XH, MAX — CLI его обратно не сообщает);
- ходы и время, затем «🧊 кэш до 15:42» (или «🧊 кэш остыл», когда срок вышел).

У разбивки контекста модели в подписи нет: модель не вызывалась.

### Числа

Всё дробное — `decimal`, а не `double`/`float` (`LimitWindow.Used`, `LimitGauge.Used`, кадры шкал,
делители в `DisplayFormat`): `double` округляет там, где этого никто не ждёт (`0.57 * 100` — это
`56.99999999999999`), а доля лимита сравнивается с границей окна, которое останавливает запуск.
`double` остаётся только там, где так считает сам BCL (`TimeSpan.TotalSeconds` и родня) и результат
тут же обрезается до целых единиц для подписи; то, что считается, а не показывается, берётся из
`Ticks` (`RunStatusMessage`) или из `long`. Денег в шлюзе нет: `total_cost_usd` не читается
([cli-contract.md](cli-contract.md)).

Округление доли в проценты живёт **ровно в одном** месте — `LimitMath.Percent`/`Left`
(`Agents.Abstractions`): расход вверх до целого с обрезкой в 0..100, остаток — `100 - Percent`. Зовут
его все, кто показывает проценты: шкалы `/status`, сводка меню, `/api/limits` (страница монитора
получает `percent` и `left` готовыми — в JS нет `decimal`). Вторая формула — ошибка, даже если на ваших
числах она совпадает: независимые округления расхода и остатка однажды дали в одном сообщении
«67% · осталось 32%».

## Мелочи, о которых стоит знать до правки хоста

- `Channel.CreateUnbounded` в полосах `ChatWorker` без `SingleReader`: с ним `Reader.Count` бросает
  исключение и `/status` разваливается.
- `Lane.RunCts` и регистрация в брокере ставятся в `ChatWorker` прямо перед `agent.RunAsync`, после
  отправки статусного сообщения: чистит их только `finally` запуска, а падение выше оставило бы
  `IsBusy` (или зарегистрированный запуск) до перезапуска.
- `HttpClient` только через `IHttpClientFactory`. `ClaudeLimits.HttpClientName` — один таймаут, без
  повторов. Клиент Telegram (`TelegramClientFactory`) — синглтон, DNS обновляет
  `PooledConnectionLifetime`; повтор только на `HttpRequestException`: методы Bot API — POST без
  идемпотентности (повтор после 5xx — дубль в чате), а 429 выжидает вызывающий по `retry_after`.
  `BaseAddress` не задавать. `RemoveAllLoggers()`: токен — часть пути.
- У опроса своя пауза сверху (`TelegramChannel.PollingBackoff`, 1 с с удвоением до минуты или
  `retry_after` канала). Telegram.Bot повторяет `getUpdates` сразу после ошибки, а *ответ* с ошибкой
  (409 от второго экземпляра с тем же токеном, 5xx, 401) — не сетевой сбой, и обработчик устойчивости
  его не тормозит: без паузы это десятки запросов в секунду и стек в логе на каждый.
- При старте `ChatGatewayService` повторяет сетевой сбой без предела (10 с с удвоением до 5 мин). Отказ
  в токене получает 6 попыток через 10 с, после чего шлюз выходит с кодом 1.

# Как устроен шлюз

Когда читать: нужно найти, где что лежит, или понять, почему оно стоит именно здесь. Чек-листы
добавления команды, экрана, агента и канала — в [extending.md](extending.md), недокументированная
часть договора с CLI — в [cli-contract.md](cli-contract.md), слова из кнопок, сообщений и этого
документа — в [glossary.md](glossary.md).

## Проекты

```
src/AgentsTracker.Agents.Abstractions/   контракты агента, без Telegram и без конкретного CLI
  IAgentBackend         Probe(), RunAsync(AgentRunRequest, IAgentRunObserver), InspectProject (отпечаток и пункты настроек агента в проекте)
  AgentRun.cs           запрос (промпт, папка, сессия, модель, effort, режим, таймаут, папка вложений, вид, Toolset), наблюдатель, результат
  AgentCapabilities     модели/effort/режимы, которые поддерживает агент (effort null — не поддерживает); AskingPermissionMode — режим, который спрашивает про всё, что не разрешено правилами (null — «Работы» нет)
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
                        Configuration (GatewayOptions, ProjectCatalog, WorkAccess — кому можно «Работу»), State (SessionStore, CacheGuard), Git, Audit, Monitoring (RunMonitor, RingBufferLog, LogFile),
                        Chat (ChatGatewayService, ChatDispatcher, DisplayFormat, MarkdownRenderer,
                        StartupNotice — «запущен»/«прерван» после перезапуска, ChatCommandCatalog — список команд «Меню»,
                        Dispatch/ — контракты обработчиков),
                        Security (секреты DPAPI, ProtectSecretsCommand, DataDirectoryAcl), Cli/ (ConsoleCommands направляет install, uninstall, protect-secrets),
                        Autostart/ (задача Планировщика через schtasks),
                        Container.Detected (в контейнере `install` отказывает, а старт предупреждает, если `MonitorBind` не подходит: там нужен `any`)
  Features/             вертикальные срезы, у каждого свой *Module
    Approvals/          ApprovalBroker, ApprovalCardRenderer, OperatorConsole, /rules
    Chat/               ChatWorker (полоса на чат, замок на папку), RunStatusMessage, /new /stop, AttachmentInbox (картинки из чата),
                        ProjectTrustGate («Доверять проекту?» до запуска задачи)
    Settings/           SettingsMenuCoordinator + Screens/*, SkillLauncher, PendingConfirmations (второе нажатие для необратимых кнопок),
                        /menu /settings /status /sessions /agent /model /effort /mode /skills /project /usage
    Question/           /ask и кнопки «Задать вопрос»: разовый запуск без сессии (QuestionLauncher)
    Topics/             «Тип темы» (TopicScreen), /topic и текст новой темы (TopicHandler)
    Help/ Audit/ Monitor/   /start /help; /audit; веб-страница (index.html — EmbeddedResource) и /api/*
```

Правила слоёв (они же в CLAUDE.md — их нарушение архитектурная ошибка, а не опечатка): `Domain` и
`Abstractions` не открывают файлы и не ходят в сеть; `Gateway` знает о конкретном агенте и канале
только в `Program.cs`; бэкенд и канал не знают друг о друге и о `GatewayOptions`; `Infrastructure`
ничего не знает о фичах (кроме контрактов `Dispatch`); фича зависит от фичи только через публичный
сервис (`Settings` → `ChatWorker.IsBusy(chat)`).

## Путь сообщения

`ChatDispatcher` пропускает только `IChatChannel.AllowedUsers` и личные чаты (`ChatKind.Unknown` —
тоже отказ). Текст разбирается по порядку:

1. слэш-команда из `IChatCommandHandler.Commands` — **в первую очередь**, иначе `/stop` ушёл бы в
   ожидающий свободный ответ, и прервать зависший запуск было бы нечем;
2. цепочка `IChatTextHandler` в порядке модулей: ответ на карточку (`ApprovalTextHandler`) →
   аргументы скилла (`SkillArgumentsTextHandler`) → текст вопроса после голого `/ask` или кнопки
   «Задать вопрос» (`QuestionTextHandler`) → в теме без типа вместо запуска экран «Тип темы»
   (`TopicHandler`) → очередь агента (`ChatEnqueueTextHandler`, всегда `true`). Поэтому
   `ChatModule` последний. Незнакомые слэш-команды — команды самого Claude Code, они уходят в CLI.

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
уходит через `ApprovalBroker` под своим коротким `RunScope` (поэтому `/stop` её снимает). Та же
проверка идёт и для папки сессии, если агент увёл сессию в worktree. «❌ Отклонить», молчание и
`/stop` значат одно: запуска не будет. Первые два ещё и снимают очередь задач. После карточки проект
и сессия читаются заново: если `/project` или `/new` их сменили, проверяются и новые. Почему —
[cli-contract.md](cli-contract.md), «Настройки проекта и диалог доверия».

### Полосы: чаты, темы и вопрос параллельно

У `ChatWorker` полоса задач на каждый чат — ленту и каждую тему (`ChatId.Key`), она создаётся с
первой задачей чата, — и одна общая полоса вопросов. У каждой своя очередь и свой
`CancellationTokenSource`. Задачи чата (и `/context`, `/compact`) идут по одной, а полосы разных
чатов и полоса вопросов — **параллельно**: иначе вопрос, который задают как раз чтобы не ждать,
ждал бы долгую задачу, а тема ждала бы проект другой темы. Число процессов агента не ограничено.

- **Один запуск на папку.** `LockFolderAsync` держит `SemaphoreSlim` на каждую нормализованную
  папку: папку сессии (worktree, куда её увёл агент), а не проекта, потому что правит агент именно
  её. Замок берётся сразу после цикла доверия — карточка ждёт вне его, до четверти часа, — и до
  модели, лимита и обновления сессии: долгое ожидание сделало бы их устаревшими, а при лимите,
  исчерпанном за это время, CLI ушёл бы в кредиты. Ждущий запуск пишет «⏳ В проекте идёт другой
  запуск…», а когда ожидание кончилось, сообщение удаляется; `/stop` прерывает и ожидание («Мелочи»,
  `Lane.RunCts`). Под замком запуск ещё раз, без карточки, проверяет, что
  папкам по-прежнему доверяют, а проект и сессия те же: запуск соседа в папке мог положить хук, а
  `/new` — увести сессию в другую папку. Если что-то изменилось, замок отпускается и цикл доверия
  идёт заново. Замок снимается после записи сессии и расхода, до отправки ответа. Известное
  ограничение: ключ замка фиксируется на старте, и worktree, куда агент увёл сессию посреди запуска
  (`EnterWorktree`), замком не закрыт.
- `IsBusy(chat)`/`QueueLength(chat)` — только про задачи этого чата: меню, смена сессии и «Сжать»
  вопрос не ждут. `AnyBusy` — для монитора. `IsAnswering`/`QuestionQueueLength` — про полосу
  вопросов; экраны берут `IsAnsweringFor(chat)`: в теме — только свой вопрос, как и `/stop` в ней
  останавливает только свой.
- `/stop` (`ChatWorker.Stop(chat)`) в теме прерывает полосу этой темы, её карточки
  (`ApprovalBroker.Cancel(chat)`) и вопрос, если он пришёл из этой темы (`Lane.Chat`). `/stop` в
  ленте прерывает всё, как до тем.
- Отказ по лимиту, `RateLimited` или отказ в доверии снимает только свою полосу: у вопроса своя
  модель, а другие полосы всё равно проверят лимит перед своими запусками. Полоса вопросов общая,
  поэтому сообщение о сброшенных вопросах получает каждый чат, чьи вопросы сброшены.
- Вопрос не трогает `RunMonitor` (текущий запуск и очередь там — задач), но виден в истории запусков
  и в журнале. Монитор показывает один запуск — первый из идущих (`RunStarted` возвращает, занял ли
  он слот), в его очереди — задачи всех чатов.
- `state.json` держит задачи в `ActiveRuns` (по ключу чата; одиночный `ActiveRun` прошлой версии
  после обновления ещё раз читается), а вопрос — в `ActiveQuestion`. После перезапуска
  `StartupNotice` сообщает про все прерванные запуски; тема получает только «прерван», а «запущен»
  уходит в ленту.
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

В любой теме `OperatorConsole.ApproveAsync` без карточки отказывает `Edit`/`MultiEdit`/`Write`/
`NotebookEdit`, путь которого лежит внутри папки данных (`EditTarget`: префиксы `\\?\` и `\\.\` снимаются,
`~/` и `/c/` раскрываются, относительный путь считается от папки запуска): там `state.json` и
секреты, а карточка лишь приглашала бы нажать. В запуске «Работы» `ForbiddenEdit` отказывает и в
правке настроек агента — `.claude/` и `.mcp.json` её папки, `~/.claude/settings*.json`,
`~/.claude/hooks`, `agents`, `skills`, `commands`, `plugins`, `~/.claude.json`: без кнопки «Всегда» правило allow оттуда сделало бы то же самое
для всей машины. Отказ пишется в журнал как `approval` `refused`. Границы: только эти четыре
инструмента, только когда CLI спрашивает — правило `allow` пропускает и карточку, и проверку, — и
только строки путей (junction или короткое имя 8.3 получат обычную карточку); команды оболочки не
разбираются; чтение папки данных не закрыто.

`McpConfigFile`, `SessionStore` и `ClaudeProcessJob` — единственные с классическим конструктором:
побочный эффект (уборка старых файлов конфигурации, чтение файла состояния, создание Job Object)
должен случиться ровно один раз, при создании синглтона.

## Сессии и выбор из чата

Записи сессий принадлежат **нормализованному пути проекта** (`ProjectCatalog.Normalize`), а активная
сессия — чату (у темы ключ `<ключ чата>|<проект>`, см. «Темы»); id новой выдаёт шлюз
(`--session-id`) и сбрасывает только по `SessionLost` от CLI ([cli-contract.md](cli-contract.md)).

Выбор из чата лежит поверх конфига и принадлежит чату: `store.For(chat)` возвращает
`SessionStore.ChatScope` с `EffectiveModel`, `EffectivePermissionMode`, `EffectiveEffort`,
`ProjectPath`, `SessionId` и сеттерами. `store.Feed` — выбор ленты для мест без чата (старт, монитор);
где чат известен, только `For(chat)`: иначе тема показывала бы и меняла проект ленты. Для ленты
проект, равный конфигу, хранится как `null` (`SetProjectPath`): иначе правка конфига молча
перебивалась бы старым выбором. Модель, effort и режим хранятся как выбраны, а
кнопки «по умолчанию» / «из конфига» (или `reset`) возвращают `null`. Модель хранится алиасом, который
уходит агенту (`opus`), но человек видит её только через `SessionStore.ModelLabel` — с версией, которой алиас ответил в последний
раз (`ModelVersions` в `state.json`), иначе по `Describe` агента.

### Темы

Тема Telegram в личке — отдельный адрес чата: `TelegramChatId(Value, Thread)`, ключ
`telegram:123/42` (старые ключи без `/` разбираются по-прежнему). Канал берёт тред из
`IsTopicMessage ? MessageThreadId : null` сообщения или сообщения нажатой кнопки и передаёт в каждую
отправку, поэтому ответы, карточки, файлы и статус сами попадают в свою тему. Ответ на служебное
сообщение «тема создана» не считается ответом (`ReplyToMessage.MessageId == Thread`). Кнопка на
недоступном сообщении (дата 0) не знает тред и получает `ChatKind.Unknown`. Тема, удалённая в
клиенте, ничего не присылает, а отправка в неё даёт «message thread not found»
(`ChannelFailure.CannotReach`).

Лента хранит прежние корневые поля `GatewayState`; у темы есть `TopicState` в `GatewayState.Topics`
с той же формой `IChatSelection`. `ProjectPath` темы — всегда полный путь: `null` там значит
«беседа» (или «Работа» при `Work = true`), а не «проект из конфига». Тема без записи новая: её текст открывает «Тип темы»
(`TopicHandler`) вместо запуска, а команда в ней работает как в беседе, поэтому не может задеть
чужой проект. Тип теме даёт только выбор типа (экран «Тип темы» или `/project`); `/model`, `/effort`
или `/mode` до него заводят запись с `Untyped = true`, и тема остаётся новой — иначе следующий текст
молча ушёл бы беседой.

Активная сессия темы лежит в `ActiveSessions` под `<ключ чата>|<проект>`: две темы на одном проекте держат разные сессии,
а возврат проекта темы возвращает её сессию там. «Очистить» на «Сессиях» удаляет записи проекта,
кроме активных в других чатах. Список на «Сессиях» — по проекту, поэтому в нём видны и сессии других
чатов; активная в другом чате помечена «🔒» и не выбирается (`ChatScope.IsActiveElsewhere`) — иначе
две темы продолжали бы одну сессию.

Беседа идёт в своей папке `SessionStore.TalkRoot\<ключ чата>` (`%TEMP%\AgentsTracker-talk`), её
очищают перед каждым запуском и до проверки доверия, как папку вопроса; `--resume` находит сессию по
пути папки, а не по её содержимому. Запуск несёт `AgentRunRequest.Toolset =
AgentToolset.WebOnly` (вопрос всегда `WebOnly`, задача — `Project`): бэкенд даёт ему набор
инструментов вопроса ([cli-contract.md](cli-contract.md), «Вопрос без сессии»), но сохраняет сессию,
в том числе для `/context` и `/compact`. Картинки отказываются до скачивания, и отказ пишется в
журнал как `file.receive` `refused`. Модель и effort по умолчанию — `Gateway:Question`; режим —
всегда из конфига (выбор беседы стирает сохранённый, `/mode` в ней получает отказ). Беседа ли это,
решает итоговая папка запуска (`IsTalkFolder`), а не тема на момент постановки в очередь: пока
задача ждёт, у темы может появиться проект.

Тема «Работа» — третий тип, для удалённой работы на всём компьютере. Метка темы — `TopicState.Work =
true` (`ProjectPath` равен `null`, как у беседы; `SessionStore.IsWork`, ставит `SetWork`). Запуск идёт
в `<WorkRoot>\<ключ чата, не-буквы и не-цифры → _>` (`Gateway:Work:Folder`, по умолчанию
`%USERPROFILE%\AgentsTracker-work`): папку создают до запуска и никогда не очищают, а сессия, замок
папки и ворота доверия работают как у проекта. Пока «Работа» включена, `ProjectCatalog.List`
пропускает рабочие папки, иначе они попали бы в список проектов. Запуск несёт `AgentToolset.Machine`:
все инструменты и MCP-серверы пользователя ([cli-contract.md](cli-contract.md), «Работа»).

- **Доступ.** Только id из `Gateway:Work:AllowedUserIds` (`WorkAccess`; пустой список — кнопку не
  видит никто). Его проверяют на кнопке «Работа» (`TopicScreen.Apply`) и снова перед каждым запуском в
  `ChatWorker.ProcessAsync`, до карточки доверия: id мог уйти из конфига, а тема осталась «Работой».
  Запуск считается «Работой», если у темы есть метка **и** её папка лежит под корнем (`IsWorkRun`).
  Отказ — сообщение, `access.rejected` в журнале и снятая очередь задач. Проверки при старте идут
  только при непустом списке, кроме самого пути `Folder` — он проверяется всегда (`WorkAccess.Validate`, `WorkOptions.Validate`): id входят в
  `Channel:Settings:AllowedUserIds`, `Folder` — полный путь, не пересекающийся с папкой данных,
  `%TEMP%`, `ProjectPath` и `Projects` и не содержит `ProjectsRoot`. У агента без `AskingPermissionMode` «Работы» нет.
- **Режим.** Закреплён за `AgentCapabilities.AskingPermissionMode` (у Claude — `default`):
  `ChatWorker` передаёт его при любом сохранённом или заданном в конфиге режиме, `/mode` и «Агент»
  отказывают (`AgentScreen.ModeRefusal`), кнопки режима скрыты. `run.start` и монитор показывают
  `request.PermissionMode` — режим, который действительно использован.
- **Без «Всегда».** `RunScope.Work` заставляет `ApprovalCardRenderer.Render(alwaysButton: false)`
  убрать кнопку и добавить строку об этом; правила шлюза «всегда» не применяются, а `/rules` и
  «Статус» сообщают, что у «Работы» их нет. Каждое такое действие спрашивает снова.

Об удалённой теме шлюз узнаёт только по отправке: Telegram отвечает «message thread not found», канал
отдаёт `CannotReach`. Первая отправка запуска — карточка «Доверять проекту?» (если её задают) или
статусное сообщение; `ConsumeAsync` перехватывает сбой, запуск не начинает и снимает очередь задач
этой темы (полосу вопросов не трогает). `run.start` пишется только после того, как статус ушёл. 429
на статусе ждётся один раз, как у части ответа. Записи удалённой темы остаются в `state.json`, а её
активные сессии — с пометкой «🔒» на «Сессиях».

С темами лента — место для служебных сообщений: клиент переносит набранное в ней в новую тему. В
Telegram Desktop до самой ленты доходит только команда, выбранная в «Меню», а мобильный клиент
переносит и её. Поэтому «Шлюз запущен» уходит в ленту, а `/stop` в
ленте — «остановить всё» — доступен только из «Меню» в Desktop; в остальных случаях `/stop` дают в
каждой теме.

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

`GitRepository` даёт ветку для «Статуса», списка проектов, статуса запуска, подписи под ответом,
`run.start` в журнале и монитора; пометка 🌳 (проект — worktree) есть только в «Статусе» и списке
проектов. `.git` ищется вверх до корня диска, как это делает git. Класс читает файлы `.git` сам, без
процесса `git`: подпись нужна на каждом такте статуса запуска.

Всё, что относится к запуску, берёт ветку **папки сессии** (`SessionStore.SessionFolder`), а не папки
проекта. Агент может сам увести сессию в worktree (`EnterWorktree`), и CLI продолжает её там, хотя
процесс запущен в проекте, — подробности в `cli-contract.md`, «Сессия в worktree».
`ChatWorker.RunObserver` берёт папку из `IAgentRunObserver.WorkingDirectoryChanged`, статус запуска
перечитывает её каждый такт, а смена сразу пишется в `SessionRecord.WorkingDirectory` (прерванный
запуск тоже уводит сессию). «Статус» показывает такую сессию строкой «🌳 сессия в worktree <папка> ·
🌿 <ветка>». Уход по абсолютным путям шлюз по-прежнему не видит. Правила, которые нельзя ослаблять:

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
`mcp-gateway-<pid>-<запуск>.json`, пока идёт запуск, `appsettings.Local.json` с секретами, `inbox\`,
`audit\audit-ГГГГ-ММ.jsonl` и `logs\gateway-ГГГГ-ММ-ДД.log`.

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
`Text.Preview` тоже маскирует (журнал, монитор, статус запуска).

Лог маскируется один раз, в приёмнике: `RingBufferLoggerProvider` собирает одну замаскированную запись
`LogEntry` для кольца монитора и для `LogFile` (`logs\gateway-ГГГГ-ММ-ДД.log`, Information и выше,
14 дней по дате в имени). Файл пишется синхронно, поэтому запись `CRT` о необработанном исключении
успевает до выхода процесса; сбой записи глотается. Консоль не маскируется, поэтому самый сырой текст
(вывод CLI, тело ошибки эндпоинта лимитов) маскируется ещё и в месте записи.

Виды перечислены в `AuditKinds`: `access.rejected`, `message`, `run.start`/`run.end`, `approval`,
`question`, `file.send`, `file.receive`, `settings`, `rules`, `trust`, `session.reset`,
`limit.refused`, `gateway`, `delivery`. Экраны меню пишут через `SettingsAudit.Changed`. В журнал идёт то, за что
отвечает человек, в `ILogger` — то, что нужно для разбирательства.

Что оставляет запуск, цифры — первыми: предел в 200 знаков режет хвост, а `/audit` показывает 160:

- `run.start` — вид (`вопрос`, `контекст`, `сжатие`, `беседа`, `работа`), модель, режим, который действительно использован, effort, ветка и
  `ждал другой запуск N`, если замок папки ждали секунду и дольше. Ожидание в очереди — промежуток
  между `message` и `run.start` одного чата.
- `run.end` — время, ходы, токены на входе и выходе, `кэш` — чтение и запись (во вход не входят) и
  ответившая модель (у запуска без расхода —
  только выбранная); у `error` и `rate-limit` — начало текста ошибки, у `cancel` — `/stop` или
  `предел времени` (предел живёт в бэкенде и токен запуска не трогает). Исход остаётся
  `ok`/`error`/`rate-limit`/`cancel`: на нём стоит история запусков в мониторе.
- Мягкая остановка шлюза (`docker stop`, Ctrl+C) не пишет `run.end` и оставляет запуск в `state.json`:
  следующий старт скажет «прервано» и запишет `run.end interrupted`, как после убитого процесса.
- `gateway` `внутренняя ошибка: <тип>: <сообщение>` (`error`) — исключение в полосе; `gateway`
  `старт` — версии шлюза и CLI, в исходе `pid` и `container`.
- `limit.refused` — модель и первый абзац отказа (окно и сброс).
- `delivery` — только при сбое: `partial`/`lost`, если части ответа не дошли до чата, `unreachable`,
  если чата нет (тему удалили) и его очередь снята. Не исход `run.end`: `run.end` пишется до отправки.

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
- `Lane.RunCts` в `ChatWorker` ставится после проверки доверия и до замка папки — иначе `/stop` не
  прервал бы ожидание замка; снимает его `finally` в `ConsumeAsync`, поэтому сбой на любом пути
  (удалённая тема, неотправленный статус) не оставляет `IsBusy`. Регистрация в брокере ставится
  прямо перед `agent.RunAsync`, после статусного сообщения, и снимает её `finally` самого запуска.
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

# Как устроен шлюз

Когда читать: нужно найти, где что лежит, или понять, почему кусок стоит именно здесь. Чек-листы
добавления команды, экрана, агента и канала — в [extending.md](extending.md); недокументированная
часть договора с CLI — в [cli-contract.md](cli-contract.md). Слова, которыми всё это называется в
кнопках, сообщениях и здесь, — в [glossary.md](glossary.md).

## Проекты

```
src/AgentsTracker.Agents.Abstractions/   контракты агента, без Telegram и без конкретного CLI:
  IAgentBackend         Probe() (бинарник, версия), RunAsync(AgentRunRequest, IAgentRunObserver),
                        InspectProject — настройки агента в проекте (ProjectSettings: отпечаток и пункты)
  AgentRun.cs           запрос (промпт, папка, сессия, модель, усилие, режим, таймаут, папка вложений), наблюдатель, результат
  AgentCapabilities     какие модели/усилия/режимы агент поддерживает (усилие null — не поддерживает)
  IOperatorConsole      что агент просит у человека: ApproveAsync, AskAsync, SendFileAsync; PersistentRule — правило «всегда»
  IAgentLimits          лимиты тарифа; IAgentSkillCatalog — слэш-команды
  IAgentBackendModule   AddServices + MapEndpoints; AgentHost — папка данных, порт, прокси, таймаут карточки от хоста
src/AgentsTracker.Agents.Claude/         Claude Code за этими контрактами:
  ClaudeBackend         процесс claude -p: аргументы, stream-json, «сессия не найдена», лимит
  ClaudeCapabilities    какие модели, усилия и режимы принимает CLI; Selectable — что предлагается из чата
  ClaudeLimits, ClaudeSkillCatalog, ClaudePluginRegistry, ClaudeCliLocator, ClaudeStreamEvent
  ClaudeProjectSettings хуки, allow, MCP-серверы и скиллы из .claude/ и .mcp.json проекта, их отпечаток
  ClaudeProcessJob      Windows Job Object на время жизни шлюза: каждый claude и его потомки умирают вместе со шлюзом
  Mcp/                  McpConfigFile — mcp-gateway-<pid>-<запуск>.json на запуск: токен и id запуска; ClaudePermissionTool — полезная нагрузка CLI ↔ IOperatorConsole;
                        ClaudeSendFileTool — файл от агента в чат, политика остаётся на стороне хоста
src/AgentsTracker.Channels.Abstractions/ контракты канала, без конкретного мессенджера:
  IChatChannel          адреса и пределы канала, ConnectAsync/ListenAsync, Send/Edit/Delete/Acknowledge,
                        SendDocumentAsync/SendPhotoAsync — файл и картинка потоком,
                        DownloadAttachmentAsync — тело присланного вложения потоком
  ChannelLimits         границы канала: длина сообщения и подписи, размер документа, фото и скачивания
  RateLimitRetry        одна повторная попытка после 429 с коротким retry_after — общая для всех отправок
  ChatId, UserId        адрес как значение; Key — «канал:значение» для state.json, аудита и лога
  Messages.cs           OutgoingMessage (ReplyTo — ответить на сообщение), Keyboard, MessageRef,
                        IncomingMessage (текст + вложения + свой MessageRef),
                        IncomingAttachment/AttachmentKind, ButtonPress, ChatCommand
  IChatInbound          куда канал отдаёт входящее; реализует хост (ChatDispatcher)
  ChatHtml              канонический формат текста (b, i, s, code, pre, a, blockquote) и экранирование
  ChannelRequestException  единственное исключение канала наружу: RateLimited (retry_after), MarkupRejected, CannotReach, Unauthorized
  IChatChannelModule    AddServices + MapEndpoints, SecretKeys; ChannelHost — общий прокси от хоста
src/AgentsTracker.Channels.Telegram/     Telegram за этими контрактами:
  TelegramChannel       long polling, инлайн-кнопки, HTML; «message is not modified» и retry_after живут здесь
  TelegramOptions, TelegramIds, TelegramClientFactory (клиент ленивый: конструктор проверяет токен)
src/AgentsTracker.Gateway/
  Program.cs            папка данных, служебные команды, списки агентов (Gateway:Agent), каналов (Gateway:Channel:Type) и фич
  Domain/               чистые модели: GatewayState (state.json), AuditEvent
  Infrastructure/       Configuration (GatewayOptions, ProjectCatalog), State (SessionStore),
                        Chat (ChatGatewayService, ChatDispatcher, MarkdownRenderer, StartupNotice —
                        «запущен»/«прерван» после перезапуска, Dispatch/ — контракты обработчиков),
                        Container.Detected — в контейнере автозапуск не ставится, монитор слушает любой адрес,
                        Audit, Monitoring (RunMonitor, RingBufferLog), Security (там же protect-secrets),
                        AppVersion — номер сборки для лога, монитора и «Шлюз запущен»,
                        Cli/ (install, uninstall), Autostart/ (задача Планировщика через schtasks)
  Features/             вертикальные срезы, у каждого свой *Module:
    Approvals/          карточки подтверждения, ApprovalBroker, /rules
    Chat/               ChatWorker (две полосы — задачи и вопросы, запуск, сессии), RunStatusMessage, /new /stop,
                        AttachmentInbox — картинки из чата: скачивание, проверки, папка inbox, чистка,
                        ProjectTrustGate — карточка «Доверять проекту?» до запуска задачи
    Settings/           SettingsMenuCoordinator + Screens/*, /menu /status /sessions /agent /skills /project /usage;
                        PendingConfirmations — второе нажатие для необратимых кнопок («Контекст»)
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
   «Вопрос» (`QuestionTextHandler`) → в очередь агента (`ChatEnqueueTextHandler`, всегда `true`).
   Поэтому `ChatModule` последний. Незнакомые слэш-команды — команды самого Claude
   Code, они уходят в CLI.

Порядок держится и для подписи к картинке: `/stop` должен работать с приложенной картинкой, и тогда
картинка не сохраняется, а диспетчер об этом говорит, а не молча её теряет. Сообщение с вложением —
не аргументы скилла: `SkillArgumentsTextHandler` пропускает его дальше и продолжает ждать.
`ApprovalTextHandler`, наоборот, забирает его себе (повисшая карточка заморозила бы запуск) и
предупреждает, что картинка никуда не пошла.

В обработчик приходит целиком `IncomingMessage`: кроме текста в нём могут быть вложения. Картинки
забирает `ChatEnqueueTextHandler` через `AttachmentInbox` — скачивает в
`<папка данных>\inbox\<чат>\<проект>\`, проверяет тип по сигнатурным байтам и подставляет абсолютные
пути в промпт; запуск получает эту папку чата в `--add-dir`, и ничего больше. Подробности —
[cli-contract.md](cli-contract.md).

Кнопки: цепочка `IChatButtonHandler` (`Dispatch/`), каждый узнаёт своё по префиксу в `CanHandle`:
`cfg:` — меню (`SettingsCallbackHandler`), всё остальное (шестнадцатеричный id запроса) —
`ApprovalCallbackHandler` → `ApprovalBroker`.

Перед запуском задачи `ChatWorker` спрашивает `ProjectTrustGate`: если в проекте лежат настройки
агента (`IAgentBackend.InspectProject` не `null`) и их отпечатку ещё не доверились, карточка
«Доверять проекту?» уходит через `ApprovalBroker` под своим коротким `RunScope` (поэтому `/stop` её
снимает). «❌ Отклонить», молчание и `/stop` — это запуска нет. После карточки проект читается
заново: если `/project` сменился, пока она ждала, проверяется и новый. Почему — `cli-contract.md`,
«Настройки проекта и диалог доверия».

### Две полосы: задача и вопрос параллельно

У `ChatWorker` две полосы, у каждой своя очередь и свой `CancellationTokenSource`: задачи (и
`/context`, `/compact`) по одной, вопросы (`AgentRunKind.Question`) по одному, а задача с вопросом
идут **параллельно** — иначе вопрос, который задают как раз чтобы не ждать, ждал бы долгую задачу.
Больше двух процессов агента разом не бывает.

- `IsBusy`/`QueueLength` — только про задачи: меню, смена сессии и «Сжать» вопрос не ждут.
  `IsAnswering`/`QuestionQueueLength` — для строки статуса.
- `/stop` (`ChatWorker.Stop`) прерывает обе полосы и зовёт `ApprovalBroker.CancelAll`.
- Отказ по лимиту или `RateLimited` снимает только свою полосу: у вопроса своя модель, а другая
  полоса всё равно проверит лимит перед своим запуском.
- Вопрос не трогает `RunMonitor` (текущий запуск и очередь там — задачи): он виден в истории
  запусков и в журнале. Число вызовов инструментов берётся из `RunStatusMessage.ToolCalls`.
- `state.json` держит задачу в `ActiveRun`, а вопрос в `ActiveQuestion`; после перезапуска
  `StartupNotice` сообщает про оба («Вопрос прерван… задайте его снова»).
- Вопрос получает ответ **ответом (reply)** на сообщение пользователя: «Вопрос принят», статус,
  каждая часть ответа (между ними может вклиниться часть ответа задачи), его карточки и файлы.
  `IncomingMessage.Message` → `QueuedPrompt.ReplyTo` → `OutgoingMessage.ReplyTo` и параметр
  `replyTo` у файловых методов канала. Telegram шлёт его с `AllowSendingWithoutReply`: иначе
  удалённый вопрос превратил бы ответ в 400.

Каждый запуск регистрируется в брокере прямо перед стартом процесса:
`ApprovalBroker.BeginRun(RunScope)` — id запуска, чат, сообщение для ответа, проект для путей на
карточке и для `send_file`, сессия для журнала. Карточки и файлы запуска идут в его чат; снятие
регистрации в `finally` запуска отменяет только **его** карточки. «Текущего чата» у брокера больше нет.

Ожиданий свободного текста («Отклонить с причиной», «Свой ответ») много, а не одно на шлюз: ждать
могут задача, вопрос и параллельные сабагенты разом. `ApprovalBroker.DeliverText` отдаёт текст тому
ожиданию, на чьё сообщение-просьбу пользователь ответил (`IncomingMessage.InReplyTo`); без ответа —
единственному ожиданию в этом чате; несколько ожиданий и никакого ответа — `ApprovalTextHandler`
просит ответить на нужную карточку, а не угадывает, и текст не становится новой задачей.

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
`X-AgentsTracker-Run: <RunId>`; `ClaudeBackend` удаляет его, когда процесс завершился, а шлюз
убирает остатки при остановке. Файл, а не JSON строкой в `--mcp-config`: аргументы попадают в лог,
а с ними попал бы и токен. В имени PID: общий файл перезаписывал и удалял второй экземпляр, и
рабочий шлюз потом умирал на «mcp__tg__approve not found»; файлы мёртвых PID подметаются на старте
(PID читается до первого дефиса, так что старый `mcp-gateway-<pid>.json` тоже узнаётся). `/mcp`
монтируется с фильтром `McpConfigFile.Authorizes`; CLI получает `--mcp-config` и
`--permission-prompt-tool mcp__tg__approve`. Имена сервера и инструмента — константы
`McpConfigFile`, `[McpServerTool]` берёт ту же константу.

По id запуска вызов находит свой запуск: инструменты читают заголовок через `IHttpContextAccessor`
(SDK выполняет инструмент в `ExecutionContext` его HTTP-запроса) и кладут его в
`ApprovalRequest.RunId`/`FileSendRequest.RunId`/`AskAsync(runId, …)`. `OperatorConsole` ищет запуск
через `ApprovalBroker.Run(runId)`; незнакомый или пустой id (запуск уже кончился) — отказ, а не
карточка в чужом чате. Сабагенты живут в том же процессе CLI и шлют тот же заголовок — проверено
двумя параллельными запусками, один из них через сабагента.

`ApprovalTimeoutMinutes` доезжает до `AgentHost`, а оттуда в `timeout` MCP-сервера в конфиге: без
него CLI обрывал вызов после 5 минут молчания, и карточка потом была мертва
([cli-contract.md](cli-contract.md)).

Второй инструмент того же сервера — `mcp__tg__send_file` (`ClaudeSendFileTool`): агент сам зовёт
его, чтобы отправить файл в чат. Путь → `IOperatorConsole.SendFileAsync` → `OperatorConsole`
проверяет папку (только проект запуска — у вопроса проект чата, как и когда он шёл общей очередью;
папка данных запрещена, на пути нет symlink/junction), тип
(список расширений в коде) и размер (`ChannelLimits`), пишет в аудит `file.send` и отправляет через
`ApprovalBroker.SendFileAsync` → `IChatChannel.SendDocumentAsync`/`SendPhotoAsync`. Отказ — всегда
ответ `{"sent":false,"reason":…}`, никогда не исключение.

`McpConfigFile` и `SessionStore` — единственные с классическим конструктором: побочный эффект
(уборка старых файлов конфигурации, чтение файла состояния) должен случиться ровно один раз до старта.

README — руководство пользователя: меняя защиту эндпоинтов или папку данных, проверьте его раздел
«Безопасность».

## Сессии и выбор из чата

Сессии ключуются **нормализованным путём проекта** (`ProjectCatalog.Normalize`); id новой выдаёт
шлюз (`--session-id`) и сбрасывает только по `SessionLost` от CLI — подробности в
[cli-contract.md](cli-contract.md).

Выбор из чата (`SessionStore`) лежит поверх конфига: `EffectiveModel`, `EffectivePermissionMode`,
`EffectiveEffort`, `ProjectPath`. Значение, равное конфигу, хранится как `null`: иначе правка
конфига молча перебивалась бы старым выбором. Модель хранится алиасом, который уходит агенту
(`opus`), но человек видит её только через `SessionStore.ModelLabel` — с версией, которой алиас
ответил в последний раз (`ModelVersions` в `state.json`), иначе по `Describe` агента.

### Контекст и виды запуска

`AgentRunRequest.Kind` говорит бэкенду, что за запуск: `Task` — задача в сессии, `Question` — вопрос
(без `--resume`, ничего не пишется на диск, `SessionId` в итоге всегда `null`, чтобы хост не сделал
разовый id активным), `ContextReport` — `/context` (модель не вызывается) и `Compact` — `/compact`
(сессия та же). Команды двух последних — свои у бэкенда, хост спрашивает только
`Capabilities.Context`. `ChatWorker` не проверяет лимит для `ContextReport` (он бесплатный и нужнее
всего как раз на исчерпанном тарифе) и отказывает обоим видам контекста, если сессию сняли, пока они
ждали в очереди, — запуск без сессии создал бы её впустую.

Заполненность контекста берётся из самого запуска, а не отдельным вызовом: `RunUsage.ContextTokens` —
вход **последнего** хода основного потока (у сабагентов своё окно), после `/compact` — его
`post_tokens`; `ContextWindow` — окно основной модели. `SessionStore.RecordContext` кладёт их в
`SessionRecord` вместе со временем замера, и экран «Контекст» показывает их без запуска агента.
Проценты — `LimitMath.Percent`, как везде.

Срок кэша тоже берётся из запуска, в том числе прерванного: `AgentRunResult.LastRequestUtc` и `CacheTtl` (контракт — `cli-contract.md`,
«Контекст»). `SessionStore.RecordCache` превращает их в `SessionRecord.CacheUntilUtc`, а если запуск
только читал кэш — оставляет прошлый известный срок, и возвращает значение, которое подпись ответа
печатает как «🧊 кэш до 15:42» — время, а не обратный отсчёт: сообщение больше не меняется.
`ContextScreen.Cache` — единственное место, где срок становится текстом для «Статуса» и «Контекста»;
`/api/snapshot` отдаёт его как есть в `sessions[].cacheUntilUtc`.

Ещё сессия помнит, на чём записан кэш: `CacheModel` (версия, которая отвечала) и `CacheMode` — режим,
если он из `AgentCapabilities.OwnCacheModes` (`plan` у Claude Code), иначе `null`: остальные режимы
кэш делят. Правило одно, в `CacheGuard`: `WillReset` — шаг от того, на чём записан кэш, пока кэш жив
и контекст не меньше `MinContextTokens` (100k); шаг обратно кэш спасает и ни о чём не спрашивает.
На экране «Агент» такая кнопка сначала спрашивает через `PendingConfirmations` (как «Сжать»);
набранные руками `/model` и `/mode` — уже решение, они выполняются сразу (`AgentScreen.Now`), а
предупреждение дописывается к ответу. `CacheGuard.Mismatch` добавляет к строке кэша «следующий запуск
запишет кэш заново», если выбор в меню уже разошёлся с кэшем.

Промахи считаются, а не угадываются: `RunUsage.FirstCacheRead` — сколько первый запрос основного потока
прочитал из кэша, и `CacheGuard.Miss` называет продолжение задачи промахом, если это меньше половины
прошлого контекста сессии (от 20k: меньше — на кону только общие инструменты и системный промпт).
`UsageTotals.CacheMisses` выводится в `/usage` рядом с долей чтения из кэша и в `/api/snapshot` —
в `usage.total` и `usage.byDay`.

«Сжать» и «Новая сессия» на этом экране идут через `PendingConfirmations`: первое нажатие только
спрашивает, «Да» срабатывает лишь для того же действия, той же сессии и в пределах 2 минут; вопрос
снимается по «Нет» и при повторном открытии экрана.

`ProjectCatalog`: список `Gateway:Projects`, иначе обход `Gateway:ProjectsRoot` до
`ProjectsRootDepth`, иначе соседи `ProjectPath`. `ProjectScreen` выбирает в два шага (папка →
проект) страницами по 12. Текущий проект идёт первым в своей группе, а после выбора страница
сбрасывается на первую — иначе метка `▶` могла оказаться за пределами экрана.

## Состояние, секреты, слои конфигурации

`%LOCALAPPDATA%\AgentsTracker\` (`AppPaths.DataDirectory`, ACL — владелец и SYSTEM): `state.json`
(атомарно), `mcp-gateway-<pid>-<запуск>.json`, пока идёт запуск, `appsettings.Local.json` с секретами,
`audit\audit-ГГГГ-ММ.jsonl`.

`SessionStore.Save` пишет `state.json.tmp` с `Flush(true)` и подменяет файл через `File.Replace`,
оставляя прежнюю версию в `state.json.bak`: без сброса NTFS после отключения питания сохраняет
переименование, но не данные, и файл оказывается пустым. Файл, который не читается, откладывается
как `state.json.corrupt-<время>` (следующая запись его не затирает), состояние берётся из `.bak`,
а `SessionStore.LoadProblem` попадает в «Шлюз запущен» — так сессии, доверие и правила не теряются
молча.

Конфиг собирается слоями: `appsettings.json` → `appsettings.Local.json` рядом с exe (IDE) → он же в
папке данных (production) → переменные окружения. `dpapi:…` расшифровывается при загрузке
(`ProtectedJsonConfigurationProvider`); `protect-secrets` шифрует `Gateway:Proxy` и ключи
`IChatChannelModule.SecretKeys` в `Gateway:Channel:Settings` (у Telegram — `BotToken`, `Proxy`). В
`publish\` секретов нет. Переменные `Gateway__*` невидимы дочернему `claude` — хост вычищает их из
окружения в `ValidateStartup`. Пользовательская сторона всего этого — в
[deployment.md](deployment.md).

Переехавшие ключи канала перечислены в одном списке, `ChannelOptions.MovedKeys`: его читают и
стартовая проверка, и `protect-secrets`. Проверять на копии старого конфига: `protect-secrets
<путь>` должен вернуть 1 с подсказкой, а `migrate-channel-settings.ps1 -Path <путь>` — не трогать
то, что уже заполнено в `Channel:Settings`.

## Аудит

`IAuditLog.Write(AuditEvent.Now(kind, summary, user, chat, project, session, outcome))` — «кто, где,
что»; адреса хранятся строками `UserKey`/`ChatKey` («канал:значение»), а не числами: id из разных
каналов сталкиваются. Есть и `NowByKeys` — для записи по готовым ключам из `state.json`. Секретов и
полных текстов нет (≤200 символов; текст пользователя — выжимка `Text.Preview` в 80 символов: туда
мог быть вставлен токен). `NowByKeys` маскирует секреты в каждой записи через `Text.ClipRedacted`:
подтверждение пишет саму команду, а в команде так же легко оказывается токен бота из переменной
окружения или заголовка curl — журнал монитор отдаёт без пароля. `Text.Redact` узнаёт токены по
форме (Telegram, `sk-`, GitHub, AWS, Slack, JWT), `Authorization`/`Bearer`, пароль в адресе и
значения ключей вроде `token=`/`--password`; `Text.Preview` тоже маскирует, поэтому лог, монитор и
статус запуска получают то же самое.

Виды перечислены в `AuditKinds`: `access.rejected`, `message`, `run.start`/`run.end`, `approval`,
`question`, `file.send`, `file.receive`, `settings`, `rules`, `trust`, `session.reset`, `limit.refused`, `gateway`. Экраны
меню пишут через `SettingsAudit.Changed`.

Это не замена `ILogger`: в аудит идёт то, за что отвечает человек, в лог — то, что нужно для
разбирательства.

## Вывод в чат

`MarkdownRenderer` переводит markdown в `ChatHtml` и режет по `IChatChannel.Limits`
(`MessageLength`, у Telegram 3800) — резать надо **исходный markdown до преобразования**, иначе
рвутся теги. Списки — `•`/`◦`, таблица — выровненный `<pre>`. Курсив только для `*`, прижатой к
содержимому на границе слова (иначе курсивом уходили `*.cs` и `2 * 3`); `_` не разбирается
(`snake_case`). Блок кода длиннее предела уходит файлом. Экранирование в рамках бюджета —
`ChatHtml.EscapeCapped` (текст, влезающий целиком, не тратит символ на многоточие).

Если канал ответил любым 400 (не только «can't parse»: неподдерживаемый тег, «too long» после
экранирования), отправка сама повторяется без разметки (`ChatHtml.StripTags`) — иначе часть ответа
молча пропала бы. На 429 посреди многочастного ответа `ChatWorker.SendPartAsync` ждёт `RetryAfter`
(≤30 с) и повторяет ту же часть; так же для файла от агента в `ApprovalBroker` — оба через
`RateLimitRetry.OnceAsync` (`Channels.Abstractions`), один общий потолок. Суммы, токены и время —
`DisplayFormat`.

Подпись под ответом (`ChatWorker.Footer`): id сессии (или «💬 вопрос»), модель, которая
**на самом деле** ответила, — `message.model` последнего хода основного потока (не та, у
которой больше всего выходных токенов в `modelUsage`: сабагент на sonnet легко пишет больше самой
сессии; по токенам — только если поток модель не назвал), сокращённая
до `opus-5-5`/`haiku-4-5` (`RunUsage.PrimaryModel`); `ModelLabel` выбранной модели — только если CLI ничего не
сказал; к ней вплотную в скобках — **переданный** effort одной-тремя буквами (`opus-5-5(H)`;
`DisplayFormat.EffortShort`: L, M, H, XH, MAX; CLI его обратно не сообщает), дальше ходы и время. У разбивки контекста модели в подписи нет: модель
не вызывалась.

### Числа

Всё дробное — `decimal`, а не `double`/`float`: израсходованная доля окна (`LimitWindow.Used`,
`LimitGauge.Used`), кадры шкал, делители в `DisplayFormat`. `double` округляет там, где этого
никто не ждёт (`0.67 * 100` — это `67.00000000000001`), а доля лимита не просто показывается:
по ней сравнивают с границей окна, которое останавливает запуск. Денег в шлюзе нет вовсе:
`total_cost_usd` не читается (`cli-contract.md`).

Округление доли в проценты живёт **ровно в одном** месте — `LimitMath.Percent`/`Left`
(`Agents.Abstractions`): расход вверх до целого с обрезкой в 0..100, остаток — `100 - Percent`.
Зовут его все, кто показывает проценты: шкалы `/status`, сводка меню, `/api/limits` (страница
монитора получает `percent` и `left` готовыми — в JS нет `decimal`). Вторая формула округления
где-то ещё — ошибка, даже если на ваших числах она совпадает: независимые округления разошлись
на 14 значениях из 1001, и в одном сообщении вышло «67% · осталось 32%».

`double` остаётся только там, где так считает сам BCL — `TimeSpan.TotalSeconds` и родня, — и
только когда результат тут же обрезается до целых единиц для подписи. То, что считается, а не
показывается, берётся из `Ticks` (`RunStatusMessage`) или из `long`.

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
  `BaseAddress` не задавать. `RemoveAllLoggers()`: токен — часть пути. У опроса своя пауза сверху
  (`TelegramChannel.PollingBackoff`, 1 с с удвоением до минуты или `retry_after` канала):
  Telegram.Bot повторяет `getUpdates` сразу после ошибки, а *ответ* с ошибкой (409 от второго
  экземпляра с тем же токеном, 5xx, 401) — не сетевой сбой, и обработчик устойчивости его не
  тормозит: без паузы это десятки запросов в секунду и стек в логе на каждый. При старте
  `ChatGatewayService` повторяет сетевой сбой без предела (10 с с удвоением до 5 мин) и сдаётся
  только на `ChannelFailure.Unauthorized`.
- Аргументы CLI идут через `ProcessStartInfo.ArgumentList`, строку не склеивать.

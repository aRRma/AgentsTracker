# Как устроен шлюз

Когда читать: нужно найти, где что лежит, или понять, почему кусок стоит именно здесь. Чек-листы
добавления команды, экрана, агента и канала — в [extending.md](extending.md); недокументированная
часть договора с CLI — в [cli-contract.md](cli-contract.md).

## Проекты

```
src/AgentsTracker.Agents.Abstractions/   контракты агента, без Telegram и без конкретного CLI:
  IAgentBackend         Probe() (бинарник, версия), RunAsync(AgentRunRequest, IAgentRunObserver)
  AgentRun.cs           запрос (промпт, папка, сессия, модель, усилие, режим, таймаут, папка вложений), наблюдатель, результат
  AgentCapabilities     какие модели/усилия/режимы агент поддерживает (усилие null — не поддерживает)
  IOperatorConsole      что агент просит у человека: ApproveAsync, AskAsync, SendFileAsync; PersistentRule — правило «всегда»
  IAgentLimits          лимиты плана; IAgentSkillCatalog — слэш-команды
  IAgentBackendModule   AddServices + MapEndpoints; AgentHost — папка данных, порт, прокси, таймаут карточки от хоста
src/AgentsTracker.Agents.Claude/         Claude Code за этими контрактами:
  ClaudeBackend         процесс claude -p: аргументы, stream-json, «сессия не найдена», лимит
  ClaudeCapabilities    какие модели, усилия и режимы принимает CLI; Selectable — что предлагается из чата
  ClaudeLimits, ClaudeSkillCatalog, ClaudePluginRegistry, ClaudeCliLocator, ClaudeStreamEvent
  Mcp/                  McpConfigFile — mcp-gateway-<pid>.json с токеном; ClaudePermissionTool — полезная нагрузка CLI ↔ IOperatorConsole;
                        ClaudeSendFileTool — файл от агента в чат, политика остаётся на стороне хоста
src/AgentsTracker.Channels.Abstractions/ контракты канала, без конкретного мессенджера:
  IChatChannel          адреса и пределы канала, ConnectAsync/ListenAsync, Send/Edit/Delete/Acknowledge,
                        SendDocumentAsync/SendPhotoAsync — файл и картинка потоком,
                        DownloadAttachmentAsync — тело присланного вложения потоком
  ChannelLimits         границы канала: длина сообщения и подписи, размер документа, фото и скачивания
  RateLimitRetry        одна повторная попытка после 429 с коротким retry_after — общая для всех отправок
  ChatId, UserId        адрес как значение; Key — «канал:значение» для state.json, аудита и лога
  Messages.cs           OutgoingMessage, Keyboard, MessageRef, IncomingMessage (текст + вложения),
                        IncomingAttachment/AttachmentKind, ButtonPress, ChatCommand
  IChatInbound          куда канал отдаёт входящее; реализует хост (ChatDispatcher)
  ChatHtml              канонический формат текста (b, i, s, code, pre, a, blockquote) и экранирование
  ChannelRequestException  единственное исключение канала наружу: RateLimited (retry_after), MarkupRejected, CannotReach
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
    Approvals/          карточки согласований, ApprovalBroker, /rules
    Chat/               ChatWorker (очередь, запуск, сессии), RunStatusMessage, /new /stop,
                        AttachmentInbox — картинки из чата: скачивание, проверки, папка inbox, чистка
    Settings/           SettingsMenuCoordinator + Screens/*, /menu /status /sessions /agent /skills /project /usage
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
   ожидающий свободный ответ, и прервать зависший прогон было бы нечем;
2. цепочка `IChatTextHandler` в порядке модулей: ответ на карточку (`ApprovalTextHandler`) →
   аргументы скилла (`SkillArgumentsTextHandler`) → в очередь агента (`ChatEnqueueTextHandler`,
   всегда `true`). Поэтому `ChatModule` последний. Незнакомые слэш-команды — команды самого Claude
   Code, они уходят в CLI.

Порядок держится и для подписи к картинке: `/stop` должен работать с приложенной картинкой, и тогда
картинка не сохраняется, а диспетчер об этом говорит, а не молча её теряет. Сообщение с вложением —
не аргументы скилла: `SkillArgumentsTextHandler` пропускает его дальше и продолжает ждать.
`ApprovalTextHandler`, наоборот, забирает его себе (повисшая карточка заморозила бы прогон) и
предупреждает, что картинка никуда не пошла.

В обработчик приходит целиком `IncomingMessage`: кроме текста в нём могут быть вложения. Картинки
забирает `ChatEnqueueTextHandler` через `AttachmentInbox` — скачивает в
`<папка данных>\inbox\<чат>\<проект>\`, проверяет тип по сигнатурным байтам и подставляет абсолютные
пути в промпт; прогон получает эту папку чата в `--add-dir`, и ничего больше. Подробности —
[cli-contract.md](cli-contract.md).

Кнопки: цепочка `IChatButtonHandler` (`Dispatch/`), каждый узнаёт своё по префиксу в `CanHandle`:
`cfg:` — меню (`SettingsCallbackHandler`), всё остальное (шестнадцатеричный id запроса) —
`ApprovalCallbackHandler` → `ApprovalBroker`. `ActiveChat` у брокера ставит `ChatWorker` перед
прогоном, иначе карточки ушли бы в чужой чат.

## Петля шлюз → CLI → шлюз

Шлюз одновременно запускает агента и обслуживает его:

```
канал ─▶ ChatDispatcher ──▶ ChatWorker ──▶ ClaudeBackend ──▶ claude.exe -p
                    ▲                                                     │ нужно разрешение
                    └── ApprovalBroker ◀── OperatorConsole ◀── ClaudePermissionTool ◀── MCP http://127.0.0.1:<McpPort>/mcp
                                                                              Authorization: Bearer <токен>
```

На старте `McpConfigFile` выдаёт токен, пишет `mcp-gateway-<pid>.json` и удаляет его при
завершении. В имени PID: общий файл перезаписывал и удалял второй экземпляр, и рабочий шлюз потом
умирал на «mcp__tg__approve not found»; файлы мёртвых PID подметаются на старте. `/mcp`
монтируется с фильтром `McpConfigFile.Authorizes`; CLI получает `--mcp-config` и
`--permission-prompt-tool mcp__tg__approve`. Имена сервера и инструмента — константы
`McpConfigFile`, `[McpServerTool]` берёт ту же константу.

`ApprovalTimeoutMinutes` доезжает до `AgentHost`, а оттуда в `timeout` MCP-сервера в конфиге: без
него CLI обрывал вызов после 5 минут молчания, и карточка потом была мертва
([cli-contract.md](cli-contract.md)).

Второй инструмент того же сервера — `mcp__tg__send_file` (`ClaudeSendFileTool`): агент сам зовёт
его, чтобы отправить файл в чат. Путь → `IOperatorConsole.SendFileAsync` → `OperatorConsole`
проверяет папку (только текущий проект, папка данных запрещена, на пути нет symlink/junction), тип
(список расширений в коде) и размер (`ChannelLimits`), пишет в аудит `file.send` и отправляет через
`ApprovalBroker.SendFileAsync` → `IChatChannel.SendDocumentAsync`/`SendPhotoAsync`. Отказ — всегда
ответ `{"sent":false,"reason":…}`, никогда не исключение.

`McpConfigFile` и `SessionStore` — единственные с классическим конструктором: побочный эффект
(файл) должен случиться ровно один раз до старта.

README — руководство пользователя: меняя защиту эндпоинтов или папку данных, проверьте его раздел
«Безопасность».

## Сессии и выбор из чата

Сессии ключуются **нормализованным путём проекта** (`ProjectCatalog.Normalize`); id новой выдаёт
шлюз (`--session-id`) и сбрасывает только по `SessionLost` от CLI — подробности в
[cli-contract.md](cli-contract.md).

Выбор из чата (`SessionStore`) лежит поверх конфига: `EffectiveModel`, `EffectivePermissionMode`,
`EffectiveEffort`, `ProjectPath`. Значение, равное конфигу, хранится как `null`: иначе правка
конфига молча перебивалась бы старым выбором.

`ProjectCatalog`: список `Gateway:Projects`, иначе обход `Gateway:ProjectsRoot` до
`ProjectsRootDepth`, иначе соседи `ProjectPath`. `ProjectScreen` выбирает в два шага (папка →
репозиторий) страницами по 12. Текущий проект идёт первым в своей группе, а после выбора страница
сбрасывается на первую — иначе метка `▶` могла оказаться за пределами экрана.

## Состояние, секреты, слои конфигурации

`%LOCALAPPDATA%\AgentsTracker\` (`AppPaths.DataDirectory`, ACL — владелец и SYSTEM): `state.json`
(атомарно), `mcp-gateway-<pid>.json`, `appsettings.Local.json` с секретами,
`audit\audit-ГГГГ-ММ.jsonl`.

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
мог быть вставлен токен).

Виды перечислены в `AuditKinds`: `access.rejected`, `message`, `run.start`/`run.end`, `approval`,
`question`, `file.send`, `settings`, `rules`, `session.reset`, `limit.refused`, `gateway`. Экраны
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

- `Channel.CreateUnbounded` в `ChatWorker` без `SingleReader`: с ним `Reader.Count` бросает
  исключение и `/status` разваливается.
- `_runCts` в `ChatWorker` ставится прямо перед `agent.RunAsync`, после отправки статусного
  сообщения: чистит его только `finally` прогона, а падение выше оставило бы `IsBusy` до
  перезапуска.
- `HttpClient` только через `IHttpClientFactory`. `ClaudeLimits.HttpClientName` — один таймаут, без
  повторов. Клиент Telegram (`TelegramClientFactory`) — синглтон, DNS обновляет
  `PooledConnectionLifetime`; повтор только на `HttpRequestException`: методы Bot API — POST без
  идемпотентности (повтор после 5xx — дубль в чате), а 429 выжидает вызывающий по `retry_after`.
  `BaseAddress` не задавать. `RemoveAllLoggers()`: токен — часть пути.
- Аргументы CLI идут через `ProcessStartInfo.ArgumentList`, строку не склеивать.

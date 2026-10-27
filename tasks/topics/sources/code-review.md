# Ревью плана тем (topics-plan.md) против кода

Пути относительно `src\AgentsTracker.Gateway\`, если не указано иное. Метка `[?]` — не проверено по коду, зависит от пробы на Telegram.

## Реальный охват правки (счёт вызовов)

- `store.ProjectPath` / `store.SessionId` / `SetSessionId` / `SetProjectPath`: около 65 строк в 21 файле. Файлы: `ChatWorker`, `ChatCommandHandler`, `ChatEnqueueTextHandler`, `AttachmentInbox`, `ChatDispatcher`, `ChatGatewayService`, `StartupNotice`, `RulesCommandHandler`, `MonitorModule`, `QuestionLauncher`, `SkillLauncher`, `SettingsAudit`, экраны `Agent`, `Context`, `Project`, `Root`, `Sessions`, `Skills`, `Status`. Плюс внутри `SessionStore`: `AlwaysAllowRules`, `RemoveAlwaysAllow`, `ClearAlwaysAllow` берут `ProjectPathLocked()`. Плана про `/rules` в таблице нет.
- `worker.IsBusy`: 13 мест в 9 файлах. `QueueLength`: 3 (Root, Sessions, Status). `Stop()`: 3 (`ChatCommandHandler:37`, `SessionsScreen:38`, `StatusScreen:26`). `Enqueue`: 4 вызывающих (`ChatEnqueueTextHandler`, `QuestionLauncher`, `SkillLauncher`, `ContextScreen`).
- «Механическая правка» верна для вызовов с `chat` под рукой. Для экранов меню это не так, см. пункт 2.

## 1. ВЫСОКАЯ. Замок на папку: место в `ProcessAsync` решает, сломается ли сессия

`Features/Chat/ChatWorker.cs:162-183, 187, 207-213, 343`

Наивное место, «замок вокруг `agent.RunAsync`» (стр. 299), ломает сессию. Сценарий: темы A и B на одной папке. Обе читают `session = store.SessionId` (стр. 167), обе получают null. A берёт замок и запускает `--session-id X`. `RegisterSession` (стр. 503) делает X активной. B получает замок со старым `session = null` и стартует `--session-id Y`. `Touch(... activate: true)` (`SessionStore.cs:420`) перезаписывает активную сессию на Y. Сессия A осиротела: `TrySetSessionId(onlyIfActive:)` её уже не вернёт. Если читают по очереди, B с устаревшим `session` продолжит сессию, на которую `/new` из A уже сменил активную.

Где замок должен стоять: сразу после цикла доверия (после стр. 183) и до стр. 187 (`model`/`effort`) и до проверки лимита. Тогда блок «`latest != session`» (стр. 207) уже под замком и сам подхватит сессию A, потому что папка та же. Отпускать замок после `SettleSession` (стр. 343) и записи `RecordRun*`, но до `SendRenderedAsync`. Карточку доверия держать вне замка: она ждёт до 15 минут и заблокировала бы чужую тему.

Минимальная правка: `await using var folderLock = await locks.AcquireAsync(project, runCts.Token)` после цикла, `Release` в `finally` перед отправкой ответа.

Ключ замка: `project` (куда запущен процесс). Агент может увести сессию в worktree, а worktree лежат соседями в `ProjectCatalog` и выбираются как отдельные проекты. Тема на worktree W и тема на проекте P с сессией в W не пересекутся по замку. `[?]` решаемо: брать замок и по `sessionFolder`, упорядоченно.

## 2. ВЫСОКАЯ. `ISettingsScreen` не знает чат: план про это молчит

`Features/Settings/ISettingsScreen.cs:14,23,41`, `SettingsMenuCoordinator.cs:23-30,52-66`, `ScreenNavigation.cs`

`RenderAsync(UserId, ct)`, `RenderFramesAsync(UserId, ct)`, `Open(UserId)` не получают `ChatId`. Только `Apply` его получает. Все девять экранов рисуют себя из `store.ProjectPath/SessionId` без чата. Без смены интерфейса (все экраны + координатор) меню в теме покажет проект ленты, а `Apply` изменит проект темы. Правка не механическая: интерфейс, координатор и девять реализаций. Заодно `ScreenNavigation` и `PendingConfirmations` ключатся по `UserId`: открытые списки двух тем делят страницу и группу (низкая важность). План правит `PendingConfirmations`, но не `ScreenNavigation`.

## 3. ВЫСОКАЯ. Нажатие без темы ломает карточки и меню `[?]`

`Features/Approvals/ApprovalBroker.cs:224`, `AgentsTracker.Channels.Telegram/TelegramChannel.cs:315-322`

`HandlePressAsync` отвергает нажатие, если `press.Chat != pending.Message.Chat`. После добавления `Thread` в `ChatId` это сравнение строгое. `ToPress` берёт тему из `callback.Message`; это `MaybeInaccessibleMessage`, у недоступного сообщения нет `MessageThreadId`. Если тема в нажатии потеряется (старое сообщение, особенность Bot API в личке), все карточки в теме станут «адресована другому чату». Запуск повиснет до `ApprovalTimeoutMinutes`. Хуже: кнопка меню из темы придёт в ленту и `Apply` изменит проект или сессию ленты.

Минимальная правка: для карточек сравнивать id чата без темы, а `press.Chat` для `Apply` брать из `pending.Message.Chat`. Для меню: `TelegramChannel` помнит словарь «чат + id сообщения → тема» для отправленных сообщений (ограниченный) и достраивает тему нажатия. Проба этапа 1 должна проверить именно `CallbackQuery.Message.MessageThreadId`.

## 4. ВЫСОКАЯ. Полосы на чат: консьюмеры, `/stop`, ожидание замка

`Features/Chat/ChatWorker.cs:113-119, 121-144, 586-599, 297, 77`

- `ExecuteAsync` запускает ровно два консьюмера. Полоса «по требованию» без своего `Task` стоит вечно. Нужен словарь `chatKey → Lane` и лениво запущенный `ConsumeAsync` с сохранённым `stoppingToken`. `DropQueueAsync` сравнивает `lane == _questions` (стр. 589), это придётся заменить на признак полосы.
- `lane.RunCts` выставляется только на стр. 297. Пока запуск ждёт замок папки, `Cancel()` вернёт false: `/stop` в теме ответит «ничего не выполняется». Токен `runCts` нужно привязать к полосе до ожидания замка. `IsBusy` (стр. 77) в это время тоже false: следующее сообщение не получит «в очереди», а тема молчит, пока чужая тема работает в той же папке. Нужно сообщение «папка занята другой темой» до ожидания.
- `Stop(chat)` для вопроса: полоса вопросов одна, `RunCts` без чата. Хранить чат идущего вопроса в `Lane`.
- `ApprovalBroker.HasPending`/`CancelAll` глобальные (стр. 71, 244). Для `Stop(chat)` флаг «карточки были» должен считаться по чату. Запуски лежат в `_runs` по `RunId`; карточки и `TextPrompt` знают только `RunId`. `Cancel(chat)` идёт через `_runs`, где `Chat == chat`. Карточка доверия (`ProjectTrustGate:47`) регистрирует свой `RunScope` с `chat`, этот путь подходит.

## 5. СРЕДНЯЯ. Монитор: хуже, чем «последний начатый»

`Infrastructure/Monitoring/RunMonitor.cs:68-92`

Два параллельных запуска: `RunFinished()` первого ставит `_run = null` и чистит `_steps`, пока второй идёт. Монитор показывает «свободен», `IsBusy` в снимке тоже. Шаги обоих запусков идут в общий `_steps` и общий `_toolCalls`. `Dequeued()` удаляет `_queue[0]`, но очереди теперь разные, превью снимется не то. `QueueCleared()` одной темы чистит очередь всех. Правка: `_run` и `_steps` по `RunId` (словарь), `Dequeued` по превью или id. Минимум: `RunFinished` и `Step` работают только со «своим» `RunStart`.

## 6. СРЕДНЯЯ. `ActiveRun` и старт шлюза

`Domain/GatewayState.cs:84-96`, `StartupNotice.cs:36-57`, `SessionStore.cs:346-373`

- Старое поле `ActiveRun` нужно оставить читаемым: после обновления релиза запись «прервано» прошлого экземпляра лежит именно там. План говорит «`ActiveRun` → словарь» и теряет миграцию.
- `StartupNotice`: `notices.Remove(chat)` для `DirectChat` (без темы) не находит чаты тем. Цикл «остальные чаты» (стр. 56-58) шлёт в каждую тему полный `started` с `LoadProblem`. Правка: `started` — только в `DirectChat`, в темы — только тексты «прервано».
- Там же `store.ProjectPath` в тексте «Шлюз запущен» (стр. 28) — проект ленты, нормально.

## 7. СРЕДНЯЯ. Две темы на одной папке — одна сессия

`SessionStore.cs:82, 144` (`ActiveSessions` по пути)

Сессия активная одна на папку. Тема A и тема B на проекте P делят её: `/new` в A меняет сессию B, «Сессии» в B переключает A, ответы одного разговора идут в два треда. План (п. 4) считает, что сессия темы работает «без доработки», но для двух тем на одной папке это одна беседа. Простой вариант: не давать привязать вторую тему к уже занятой теме папке (отказ в `ProjectScreen.Apply` с текстом). Замок тогда нужен только между темой и лентой (и worktree), и конфликт двух тем исчезает. Либо принять как есть и написать в ограничениях.

## 8. СРЕДНЯЯ. Беседа (talk): порядок и места правок

- Очистка папки до цикла доверия. `QuestionDirectory()` вызывается первым (стр. 162) и сам проверяет `agent.InspectProject(path)`. Для беседы то же: очистка и проверка до стр. 165, иначе доверие смотрит старое содержимое. Папку создавать даже когда `%TEMP%` вычистил Storage Sense или контейнер: без неё `WorkingDirectory` не существует и `Process.Start` падает. `QuestionDirectory` уже делает `CreateDirectory`.
- Тип запуска решать в `ProcessAsync` по итоговому `project` после цикла доверия, а не при постановке в очередь. Тема могла быть перепривязана, пока сообщение ждало. `Enqueue` всегда ставит `Task`.
- `ContextReport` и `Compact` в теме-беседе идут с обычным набором аргументов: ограничения только у `Talk`. Безопасно (команды без инструментов), но `--strict-mcp-config` не применится, чужие MCP-серверы из настроек загрузятся. Если важно, добавить `Talk` во все ветки `BuildArguments`, где `Kind == Question`.
- Защита `Directory.Delete(recursive)`: `IsTalk(path)` — строгая проверка «внутри корня беседы», не равно корню, нормализованный путь (`ProjectCatalog.Normalize` + `IsInside`). Путь приходит из `state.json`.
- Картинки: скачивание происходит в `ChatEnqueueTextHandler` через `inbox.StoreAsync` до `Enqueue`, а не в `ChatWorker`. Отказ с объяснением нужен там, до скачивания. В `ChatWorker` для беседы дополнительно `AttachmentsPath: null` (стр. 255).
- `--resume` после удаления и пересоздания папки: стенограмма лежит в `~/.claude/projects/<ключ папки>`, ключ от пути, не от содержимого, так что должно работать. Проба этапа 1 это покрывает. `TrustProject` не нужен: `InspectProject` вернёт null на пустой папке.
- `send_file` у беседы: `RunScope.ProjectPath` = папка беседы, пустая, отправлять нечего. Безвредно.
- Рост `state.json`: `Sessions` режется по `SessionsPerProject` на папку, но папок бесед будет по числу тем. Низкая важность.

## 9. НИЗКАЯ. Telegram-детали

- Альбомы: ключ `_mediaGroups` — `media_group_id`, он общий и уникален между темами. `collected with {...}` сохраняет `Chat` первого элемента, тема не теряется. Но `_groupFlush` — единая цепочка на весь канал, и каждое обычное сообщение любой темы (в том числе `/stop`) ждёт `PendingGroupsAsync()`. Альбом в теме A задерживает `/stop` в теме B на время окна 1,2 с плюс скачивание в `ChatEnqueueTextHandler` (скачивание идёт внутри цепочки через `OnMessageAsync`).
- `ToIncoming` (стр. 282): в теме `ReplyToMessage` может быть служебным сообщением создания темы `[?]` (так в форум-группах; в личке не проверено). Тогда `InReplyTo` указывает на него, `DeliverText` откатывается на «единственное ожидание». Правка: игнорировать `ReplyToMessage`, если его `MessageId == MessageThreadId`.
- Темы нужны в `SendMessage`, `SendDocument`, `SendPhoto`, `SendChatAction`. `EditMessageText`, `DeleteMessage`, `AnswerCallbackQuery` тему не принимают, правка не нужна.
- `Reply()` (стр. 398) сравнивает `target.Chat == chat`: с темой в записи это работает, пока обе стороны её содержат.
- `Safe(chat.Key)` в `AttachmentInbox` превращает `telegram:123/42` в `telegram_123_42`, конфликта с лентой нет. Папка вложений на тему, `/new` чистит только свою.
- `ParseChat` старых ключей без `/` должен продолжить работать (запись `ActiveRun` из прошлого релиза).

## 10. НИЗКАЯ. Прочее

- Две темы на одной новой папке с настройками: две карточки доверия одновременно. После первого «Доверять» вторая карточка висит, пока на неё не ответят. Можно перед карточкой перепроверять `IsProjectTrusted`.
- CLAUDE.md: «два процесса `claude` разом» перестанет быть верным (число не ограничено). Обновить при слиянии.
- `MonitorModule.Snapshot` (стр. 121-122) показывает проект и сессию ленты. Для тем нужен хотя бы список привязок или явная оговорка.
- `QuestionLauncher`/`SkillLauncher`/`PendingConfirmations`: ключ `(UserId, ChatId)`, как в плане. `ScreenNavigation` забыт (пункт 2).
- Лимит тарифа общий: при исчерпании каждая тема получает отказ на своём запуске и снимает свою очередь. Поведение корректное, число сообщений вырастет.

## Проще ли иначе

Вариант: полоса не на тему, а на папку (`ProjectPath`). Тогда запуски одной папки идут строго по очереди без отдельного замка, а чтение `SessionId` происходит при извлечении из очереди. Минусы: `/stop` и `IsBusy` по чату всё равно нужны отдельно, а проект сейчас читается на момент запуска (тема может перепривязаться пока сообщение в очереди). Выигрыш неочевиден `[?]`. Надёжнее оставить полосу на чат и правильно поставить замок (пункт 1).

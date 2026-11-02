# Проверка плана журнала: «Новые находки», этапы 3-5

Только чтение. Номера строк — коммит 27cd5f7.

## 1. Остановка шлюза посреди запуска

- Бэкенд на отмене возвращает результат, а не бросает: `ClaudeBackend.cs:113-118` ловит
  `OperationCanceledException`, убивает процесс, `:132-150` отдаёт `Cancelled = true`. ВЕРНО.
- `status.DisposeAsync` не бросает: `RunStatusMessage.cs:147-153` глотает всё и ждёт цикл до 10 с. ВЕРНО.
- `TelegramChannel.DeleteAsync` (`:486-496`) ловит только `RequestException`. Отмена токена
  `Bot.DeleteMessage` выходит как `OperationCanceledException` (поведение библиотеки не проверял,
  но фильтры `TelegramChannel.cs:112, 202` и комментарий `ChatWorker.cs:884-887` исходят из этого).
- `DeleteQuietlyAsync` (`:896`) пропускает OCE при `ct.IsCancellationRequested`. ВЕРНО.
  `ConsumeAsync:190` ловит и делает `break`. Итог: `Audit(RunEnd)` (`:492`), `RecordRun`, `SettleSession`
  не выполняются. Дыра есть. ВЕРНО.
- НЕТОЧНО: потеряно не только `run.end`, но и `store.RecordRun` (расход), `SettleSession`
  (активная сессия), `RecordRunOutcome` (история в мониторе). Новая сессия уже записана через
  `observer.SessionStarted` (`ClaudeBackend.cs:94-98`), так что связь сессии не теряется.
- `store.EndRun` в `finally` сразу пишет state.json (`SessionStore.cs:506-510`, `Mutate:675-682` -> `Save`
  с `flushToDisk`). Значит, запись выживет, если её НЕ снимать.
- `StartupNotice.RecordInterrupted` (`:86-103`) пишет `run.end interrupted` и шлёт «прервано». ВЕРНО.
- Предложенное исправление «не снимать при отмене stoppingToken»:
  - Ложное «прервано» возможно: отмена пришла в узком окне после конца запуска, но до `finally`,
    `Delete` не бросил -> код идёт дальше, пишет `run.end ok`, шлёт ответ, а запись в state.json
    осталась -> при старте второй `run.end interrupted` и «прервано» при доставленном ответе.
    Лечится так: не снимать только если OCE действительно вылетела (например `catch (OCE) when stopping`
    в `finally` ставит флаг), либо снимать запись после `SendRenderedAsync`.
  - Вариант проще: в `finally` оставить `EndRun`, а OCE от `DeleteQuietlyAsync`/ответа ловить
    (`catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)`), чтобы дойти до
    `Audit(RunEnd cancel, остановка шлюза)`, `RecordRun`, `SettleSession`. Минус: человек не получит
    «прервано» при старте — ответ «Остановлено.» не уйдёт (токен отменён). Поэтому плановый путь
    (оставить запись) полезнее для человека, а `run.end` в журнале при нём дублируется
    (`cancel`-запись не пишется, пишет старт). Выбор: оставить план, но флаг ставить по факту OCE.
  - Писать `run.end` до сетевых вызовов в `finally` нельзя «как есть»: `result` в `finally` ещё
    не присвоен, если бэкенд бросил. Но при Cancelled он присвоен только после `try`. Нужен перенос
    сетевых вызовов из `finally` в блок после `try`, это больше правок, чем в плане.
- Windows: `Stop-Process -Force` не запускает `finally` (по плану; не проверял).

## 2. Причина отмены по токенам

- `runCts` создан `CreateLinkedTokenSource(stoppingToken)` (`ChatWorker.cs:239`). ВЕРНО.
- `lane.RunCts = runCts` (`:246`), `Lane.Cancel` отменяет именно его (`:66-75`). ВЕРНО.
- Другие отмены `RunCts`: только `Lane.Cancel` из `Stop` (`:157, :159`); `Stop` зовут
  `ChatCommandHandler:38`, `SessionsScreen:39`, `StatusScreen:26`. Удаление темы (`ConsumeAsync:199`),
  `DropQueueAsync` очередь снимают, `RunCts` не отменяют. `/new` — не отменяет. ВЕРНО.
- Таймаут внутри бэкенда: `timeoutCts` (`ClaudeBackend.cs:46-47`), причина в `:134`
  (`timeoutCts && !ct`). Формула «runCts не отменён -> таймаут» ВЕРНА для `Cancelled`.
- НЕТОЧНО: порядок проверок в плане: нужно сначала `stoppingToken`, потом `runCts`, иначе при остановке
  шлюза `runCts` тоже отменён и причина будет `/stop`. В тексте плана порядок такой и есть (stoppingToken
  второй пунктом, но «иначе /stop» последний) — читается верно, если проверять в этом порядке.
  Гонка «таймаут и /stop одновременно» даёт `/stop` в хосте, бэкенд сказал бы «таймаут» — мелочь.
- `lane.RunCts = null` в `finally` (`:443`) ставится до `run.end`, но `runCts` сам остаётся
  доступен локально (`using var`), читать `IsCancellationRequested` после `finally` можно.

## 3. run.start

- `QueuedPrompt` создаётся только в `Enqueue` (`:124`). ВЕРНО. Поле `QueuedAt` — одна правка.
- `ждал N с` от постановки до `Audit(RunStart)` (`:399`) включает: карточку доверия (до 15 мин),
  замок, проверку лимита (сеть), отправку статуса с повтором 429. Пункт 3 рисков это признаёт
  только для доверия.
- `замок N с`: ожидание в `LockFolderAsync` (`:282`), внутри `if (!gate.Wait(0))` ... `WaitAsync(run)`
  (`:577-621`). Цикл `while(!question)` может взять замок несколько раз (`:288-303`) -> суммировать.
  Вопрос замок не берёт (`:249` `while (!question)`), «замок» у него всегда пусто; «ждал» у вопроса —
  время в общей очереди вопросов. Верно по коду.
- «Вид запуска не виден нигде»: НЕ ПРОВЕРЯЛ полностью (в `run.start` слова «вопрос»/«беседа» уже есть,
  `:400`; для `ContextReport`/`Compact` — нет). Частично ВЕРНО.

## 4. limit.refused, file.send, предел

- `AuditEvent.SummaryLimit = 200` (`AuditEvent.cs:38`), клип `ClipRedacted` на `:54`. ВЕРНО.
- `/audit` режет до 160 (`AuditCommandHandler.cs:14, 66`), виден `kind + outcome + summary`. ВЕРНО.
  Бюджет `run.end`: «2:14, ходов 5, токенов 12,3 тыс. / 1,8 тыс., opus-5-5» ~ 55 знаков + `error`
  с `Preview(80)` ~ 140: влезает в 160 впритык с Kind/Outcome (kind+outcome ~ 15) — ок.
- `ClaudeLimits.cs:86`: отказ — многострочный; первая «строка» (до `\n\n`) — «🚦 Лимит тарифа исчерпан:
  окно «5 часов» выбрано полностью. Сброс …». ВЕРНО, но первая строка начинается с эмодзи и слов
  «Лимит тарифа исчерпан» — дублирует `kind limit.refused`. `AuditEvent` схлопывает переводы строк
  (`ReplaceLineEndings(" ")`), так что и без «первой строки» запись пойдёт целиком и обрежется на 200.
  Лучше резать до `\n\n` явно, иначе туда попадёт абзац про кредиты.
- `file.send` `OperatorConsole.cs:311`: сейчас `{Preview(path,120)}: {reason}` (путь первым). Reason
  в `Refuse` — «Не удалось отправить файл: {ex.Message}» или «Отправка отменена…»: длинные. ВЕРНО.
  Смена порядка уместна: при 160 в `/audit` причина теряется.

## 5. Внутренняя ошибка ConsumeAsync (:202)

- Доступны `prompt` и `lane`. Записи журнала нет, только лог и `SendPlainAsync`. ВЕРНО.
- Риск: `Audit(prompt, …)` зовёт `store.For(prompt.Chat).ProjectPath`; если ошибка пришла из
  `SessionStore` (например `Save`), второй сбой выйдет из `catch` и убьёт потребитель полосы
  (`ConsumeAsync` завершится, очередь чата никто не читает). Запись нужно оборачивать в try.
- НЕТОЧНО: «между `run.start` и `run.end` остаётся `run.start` без пары» — запись `gateway` пару
  не восстанавливает (kind другой). Лучше писать `run.end` с outcome `error` (если `run.start`
  был) или оставить как есть; но флага «run.start написан» нет.

## 6. Доставка

- `SendRenderedAsync` (`:832-847`) глотает `ChannelRequestException` на каждой части. ВЕРНО.
  `RateLimitRetry` вокруг каждой части (`:853-857`). ВЕРНО.
- `Translate` переводит «message thread not found»/«chat not found»/403 в `CannotReach`
  (`TelegramChannel.cs:568-587`). ВЕРНО. Важно: любой 403 (бот заблокирован) — тоже `CannotReach`;
  значит бросок наружу снимет очередь и ЛЕНТЫ (`DrainTasks`, `ConsumeAsync:199`), не только темы.
  Это новое поведение, в плане описано как «очередь удалённой темы».
- После `SendRenderedAsync` (`:516-523`) остаётся только `if (result.RateLimited) DropQueueAsync`.
  Сессия, расход, кэш, `run.end`, история, замок — всё до отправки. Файлы (`send_file`) идут во
  время запуска. Потеряется лишь `DropQueueAsync` при лимите; `ConsumeAsync` при CannotReach снимает
  очередь сам. Вывод: бросать наружу безопасно. ВЕРНО.
- Но: `SendOnceAsync` для документа (`SendFileAsync`) тоже может дать `CannotReach`; нормально.
- Пропуск остальных частей при `CannotReach` — экономия вызовов, но в плане «считает неудачные части»
  и одновременно «бросает дальше» — две ветки, нужен счётчик только для не-`CannotReach`.

## 7. Старт и стоп

- «старт» и «стоп» УЖЕ пишутся: `ChatGatewayService.cs:44, 53`, kind `gateway`, без outcome, без версии.
  В плане этап 5 подан как добавление; стоит сказать «расширить существующие».
- `стоп` в `finally` вокруг `ListenAsync`: при падении `ListenAsync` он пишется тоже — outcome
  `error` потребует `catch`. Если `ConnectAsync` вернул false, «стоп» не пишется (раньше `try`) —
  «не запустился» это и закрывает.
- `ValidateStartup` вызывается до `app.RunAsync()` (`Program.cs:78, 82`), `RunMonitor.CliVersion`
  выставляется в `ValidateStartup` (`GatewayInfrastructure.cs:120`) -> к `ExecuteAsync` задан. ВЕРНО.
  Но только если `Probe` дал версию; иначе null — нужен запасной вариант.
- `StartupNotice.SendAsync:22` уже вызывает `agent.Probe()` (не кэшируется: `ClaudeBackend.cs:31`)
  -> запуск `claude --version` там именно второй; план «не запускать третий» верен.
- `Container.Detected` есть (`Container.cs`), `AppVersion.Current` есть (`AppVersion.cs:16`). ВЕРНО.
- `ChatGatewayService.cs:79-85`: отказ токена после 6 попыток (`UnauthorizedAttempts`). ВЕРНО.
  Запись «не запустился»: `Environment.ExitCode=1` в журнале и так будет виден по «стоп» отсутствию;
  запись в журнал нужна, пробный экземпляр с выдуманным токеном (docs) будет писать её всегда — шум в
  журнале пробного экземпляра (у него свой каталог данных, так что терпимо).

## 8. Читатели журнала

- `/audit`: `AuditCommandHandler.cs:57-66` — kind и outcome как строки, любой kind. Монитор:
  `index.html:542` печатает `kind/summary/outcome` как текст. Новый kind и формат не ломают. ВЕРНО.
  `run.end`-outcome влияет только на таблицу истории (`index.html:508`, `RunRecord.Outcome`),
  не на журнал. Новый outcome у `run.end` не добавляется — ок.
- НЕ ПРОВЕРЯЛ: есть ли в `index.html` стили `.mark.<outcome>` для новых значений (`unauthorized`,
  `stopped`, `partial`) — не критично, текст выводится.

## 9. Глоссарий

- «доставка» в `docs/en/glossary.md` и `docs/glossary.md` не встречается (grep `доставк` — пусто). ВЕРНО.
  Близкие термины в глоссарии не найдены (искал «отправк», «ответ»: нашлось лишь про вопрос).
  Глагол «отправить» в UI используется для `send_file` — смотреть перед выбором слова.

## Упростить

1. Вид `delivery` не нужен: достаточно kind `gateway` (он уже есть, читатели его знают) с summary
   `ответ не доставлен: 2 из 5 частей` и outcome `partial`/`unreachable`. Минус одна константа и
   один термин в глоссарии; «доставка» в глоссарии всё равно придётся описывать, если слово видит человек.
2. «ждал» и «замок» слить: писать только `ждал N с` (если > 1 с) и, если замок был занят, метку `замок`.
   Замок — подмножество ожидания; два числа читаются как сумма.
3. «не запустился»: оставить только если нужна; `стоп` без пары `старт` и лог CRITICAL (этап 1) уже
   говорят то же. Дёшево, но шум для пробных экземпляров.
4. Этап 3.6: писать не `ActiveRun`-фокус, а ловить OCE в `finally` — см. п. 1; либо принять потерю
   и не лечить (в контейнере `docker stop` — редкость, `run.start` без `run.end` терпимо).

## Пропущено в плане

- `rate-limit` как outcome `run.end` (`:486-489`): в формулах только ok/error/cancel.
- При `cancel` `result.Usage` null (`ClaudeBackend.cs:138-149`): «те же цифры» для cancel — только
  время; токенов и ходов нет.
- Ошибка записи в `catch` внутренней ошибки (п. 5) и 403 снимает очередь ленты (п. 6).
- Не проверено: поведение Telegram.Bot 22 на отменённом токене в `DeleteMessage` (OCE vs обёртка).

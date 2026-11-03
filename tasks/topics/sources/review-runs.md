# Ревью: журнал запусков (коммиты [log], 1708133..HEAD)

Проверено по коду; харнес `tasks/topics/check.cs` запущен — «ВСЁ ХОРОШО». Сборку отдельно не гонял (харнес собирает проекты сам).
Не проверял: поведение Telegram.Bot при сетевых таймаутах (`TaskCanceledException` вместо `RequestException`), как `/audit` рисует поле Outcome, запись `state.json` на диск при мягкой остановке (Mutate синхронный?).

Ошибок, ломающих работу, не нашёл. Ниже — риски и мелочи.

## 1. stopped

**Верно (проверено):**
- `ChatWorker.cs:~465` `stopped = result.Cancelled && stoppingToken.IsCancellationRequested`. Вопрос остаётся так же: `EndRun` (`SessionStore.cs:506`) снимает `ActiveQuestion` или `ActiveRuns[chat]` по `run.Question`; `TakeInterruptedRuns` (:516) берёт оба.
- `heldLock` — `using var` (:~320), снимается при любом `return`. `registration.Dispose` и `monitor.RunFinished` стоят в `finally` до `return`, от `stopped` не зависят.
- Сессия не теряется: новая регистрируется сразу на старте процесса (`ClaudeBackend.cs:~95` `observer.SessionStarted` → `RegisterSession` → `Touch(activateIn: Slot)`), смена папки — в `RunObserver.WorkingDirectoryChanged`. `SettleSession` после отмены ничего бы не добавил (`SessionLost` у отмены false, `SessionId` тот же). «продолжай» идёт в нужную сессию.
- На практике `if (stopped) return;` почти недостижим: `DeleteQuietlyAsync(status.Message, stoppingToken)` (:~946-955) при отменённом токене перебрасывает `OperationCanceledException` из `finally`, `ConsumeAsync` ловит его и делает `break`. `ActiveRun` остаётся в обоих случаях. Не ошибка, но `return` — запасной путь.

**Находки:**

1. **риск, `ChatWorker.cs:~474`** `if (!stopped) store.EndRun(active)`. Если `RunAsync` бросит исключение (не `Cancelled`) при уже отменённом `stoppingToken`, `stopped` остаётся false: `ActiveRun` снимается, «прервано» на следующем старте не будет, `run.start` останется без конца. `ClaudeBackend` сам ловит отмену только на `WaitForExitAsync`, так что путь редкий. Исправление: `var stopping = stoppingToken.IsCancellationRequested` в `finally` вместо `stopped`: `if (!(stopping && !completedNormally)) EndRun`; проще — флаг `finished = true` ставить после `RunAsync` и снимать запись только если `finished || !stoppingToken.IsCancellationRequested`.

2. **мелочь, `ChatWorker.cs:~485` (после `if (stopped) return;`)** при остановке шлюза теряется `RecordCache` (срок кэша из `LastRequestUtc`/`CacheTtl`, которые отмена возвращает: `ClaudeBackend.cs:~153`). После перезапуска «Статус» покажет «остыл» про живой кэш, возможны лишние подтверждения смены модели/режима. Исправление: перенести блок `cacheUntil = store.RecordCache(...)` выше `if (stopped) return;` (он не зависит от `Usage`).

3. **мелочь** гонка «запуск кончился сам, шлюз встал одновременно»: `Cancelled=false` → идём обычным путём, `SendRenderedAsync` падает на `OperationCanceledException` (не `ChannelRequestException`, :~897) → `ConsumeAsync` делает `break`. `run.end` («ok») записан, `delivery lost` — нет: при остановке шлюза потерянный ответ не виден в журнале. Принимаемо, но в комментарии к `Delivery` стоит сказать, что остановка шлюза не фиксируется.

4. **мелочь** `stopped` ставится и когда запуск завершили `/stop` или таймаут за миг до остановки шлюза: следующий старт напишет «прерван перезапуском». Ложь небольшая, не чинить.

## 2. Причина отмены

- `runCts` отменяют только `Lane.Cancel()` (`ChatWorker.cs:67-76`), вызывается из `Stop(chat)` (:~158-160) ← `ChatCommandHandler.cs:38`, `SessionsScreen.cs:39`, `StatusScreen.cs:26`. Это всегда «/stop» (кнопки «Остановить» тоже). `/new`, удаление темы и `DropQueueAsync` `runCts` не трогают (`DrainTasks` только снимает очередь). Поиск по `RunCts|\.Cancel()` в `src` это подтвердил.
- `ClaudeBackend.cs:~124-150`: `timeoutCts` и `ct` (= `runCts.Token`) раздельны, причина — `timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested`. Значит «не отменён → таймаут» верно для Claude. Совпадение таймаута и /stop: бэкенд скажет «Остановлено.», журнал — «/stop». Согласовано.
- Другого бэкенда в `src` нет; контракт `AgentRunResult.Cancelled` нигде не оговаривает «отмена без токена хоста = таймаут». **риск (слабый)**: правило живёт неявно между `ChatWorker` и `ClaudeBackend`. Исправление: добавить в `AgentRunResult` поле `TimedOut` (бэкенд знает точно) и писать `", таймаут"` по нему; убирает зависимость от `runCts.IsCancellationRequested`.
- Вопрос: `StopsQuestionOf(chat)` → `_questions.Cancel()` тот же `runCts` — причина верна.

## 3. Формат summary

5. **риск, `ChatWorker.cs:~528-536`** `Ok:false => ", " + Text.Preview(result.Text)`: текст ошибки Claude начинается с `claude завершился с кодом N.` + пустая строка + «```» (`ClaudeBackend.cs:~530`). После `Preview` (80 знаков) в журнале: «claude завершился с кодом 1.  ``` <начало stderr>» — около 30 из 80 знаков уходят на шапку и забор из кавычек. Для rate-limit и `is_error` результата текст начинается сразу с сути (с `\n\n_(subtype)_` в конце). Исправление: `Text.Preview(result.Text.Replace("```", "").Replace("claude завершился с кодом", "код"))` — или проще: не оборачивать в `Preview` по умолчанию 80, а брать 100 и убрать «```»: `Text.Preview(result.Text.Replace("```", " "), 100)`.

6. **мелочь, :~530** если `used.PrimaryModel` и `modelLabel` оба null, получится хвост «токенов 1/2, » — пустое имя модели после запятой. Исправление: собирать части списком: `string.Join(", ", new[]{...}.Where(s => s is { Length: > 0 }))`.

7. **мелочь, длина.** Типичный предел: время (≤11) + «, ходов 12» (10) + «, токенов 123.4k/45.6k» (22) + модель (≤27) + «, » + 80 ≈ 152 — укладывается в `SummaryLimit` 200 и почти в 160 `/audit` (`AuditCommandHandler.cs:14`). При «1 ч 20 мин», 1.23M токенов и длинном id модели — ~165, хвост причины обрежется в `/audit` (в файле цел до 200). Порядок «цифры первыми, причина последней» плох именно для ошибок: причина — главное. Исправление: для `Ok:false`/`Cancelled` ставить причину сразу после времени: `Duration + reason + spent`.

8. Культура: `Tokens` и `Elapsed` через `InvariantCulture`/целые — от культуры не зависят (`DisplayFormat.cs:22-48`). `Tokens` у `long` — `used.InputTokens` это `long`, ок.

9. `runKind` для беседы + сжатия: сжатие идёт с `talk` только если `WebOnly`; получится «сжатие, беседа, » — читаемо, порядок разумный. Вопрос + `talk` невозможны (`talk = !question && ...`).

## 4. lockWait

- Корректно: `Stopwatch` обёрнут только вокруг `LockFolderAsync` (:~290-292), копится по кругам цикла (`+=`), карточку доверия (`TrustAsync`) не включает. Вопрос в цикл не заходит — `lockWait` = 0.
- В время входит отправка «⏳ В проекте идёт другой запуск» (внутри `LockFolderAsync`) — миллисекунды, порог 1 с делает это неважным.
- **мелочь**: `Stopwatch.GetTimestamp()` + `GetElapsedTime` — уместно; можно проще `Stopwatch.StartNew()` внутри цикла, выгоды нет.

## 5. limit.refused

10. **риск, `ChatWorker.cs:~334`** `exhausted.Split("\n\n", 2)[0]` склеен с форматом строки из `ClaudeLimits.cs:86-87` (`…{reset}\n\n…`), а бэкенд — отдельный слой. Формат сегодня верный (проверено), без `\n\n` вернётся весь текст (уложится в 200 обрезкой), `\r\n` в строке нет. Но запись начинается с «🚦 Лимит тарифа исчерпан: » — дубль вида записи `limit.refused`. Исправление коротким кодом: `var refusal = exhausted.Split("\n\n", 2)[0].Replace("🚦 Лимит тарифа исчерпан: ", "")` — или лучше: `IAgentLimits.RefusalAsync` отдаёт запись `(string Chat, string Journal)`; тогда хост не разбирает чужой текст.

## 6. delivery

11. **риск, `ChatWorker.cs:~884-903`** `SendRenderedAsync` считает недоставкой только `ChannelRequestException`. Telegram переводит `RequestException` в неё (`TelegramChannel.cs:~575`), но `TaskCanceledException` таймаута HttpClient (не `RequestException`) вылетит наружу → `ConsumeAsync` → `gateway: внутренняя ошибка` + «Внутренняя ошибка шлюза» в чат, `delivery` не пишется, `DropQueueAsync` для rate-limit не вызывается. Не регрессия, но журнал назовёт потерю ответа «внутренней ошибкой». Приемлемо.
12. `total == 0` (пустой текст): `failed > 0` ложь, ничего не пишется — верно. Но ответ всегда не пуст: `Answer` + `Footer`.
13. Если вся доставка упала с `CannotReach` (тема удалена во время запуска), `SendRenderedAsync` её проглатывает → `delivery lost`, но очередь темы не снимается (в отличие от ветки `ConsumeAsync`, которая снимает). Следующая задача из очереди запустится в мёртвую тему: статус `StartAsync` бросит `CannotReach` → тогда запись `unreachable`. Не ошибка.
14. `ConsumeAsync` `unreachable` (:~200-203): `Audit` читает `store.For(prompt.Chat).ProjectPath` — `For` только создаёт обёртку (`SessionStore.cs:40`), побочных эффектов нет; удалённая тема получит проект своей записи или null. Для общей полосы вопросов `dropped = 0`, текст «задача брошена» для вопроса неточен — **мелочь**: `lane == _questions ? "вопрос брошен" : …`.
15. Дубль с логом: `LogWarning` в той же ветке (:199) и `Audit` — разные адресаты (лог / журнал), нормально.

## 7. внутренняя ошибка

16. **риск (слабый), :~210** `ex.Message` проходит `AuditEvent.NowByKeys` → `ClipRedacted` (`AuditEvent.cs:60`), то есть токены и `token=` маскируются; пути не маскируются, но журнал и так хранит имя папки проекта. Само сообщение в чат (`SendPlainAsync "Внутренняя ошибка шлюза: {ex.Message}"`, :~212) осталось без маски — было и раньше; чат закрыт `AllowedUsers`. Исправление, если нужно: `Text.Preview(ex.Message, 200)` в чат.
17. `OperationCanceledException` при отменённом `stoppingToken` отсекается ветвью выше и в журнал не пишется. При отмене не от остановки шлюза (например, `HttpClient`-таймаут внутри `limits.RefusalAsync`) запись «OperationCanceledException: …» появится — корректно.
18. **мелочь**: запись `внутренняя ошибка` без `session`, поэтому не привязана к `run.start` без `run.end`. Исправление: передавать `session` в `Audit`, если ProcessAsync его знал (потребует вынести переменную) — не стоит.

## 8. «старт»

19. **мелочь/ошибка вида, `ChatGatewayService.cs:~48-52`** `CliVersion` к этому моменту есть: ставится в `ValidateStartup` (`GatewayInfrastructure.cs:~126`) до запуска хоста. Но `claude --version` печатает «2.1.316 (Claude Code)», и запись читается «старт 0.0.0-dev, Claude Code 2.1.316 (Claude Code)». Тот же дубль в `StartupNotice.cs:~21-27` («Шлюз запущен … — Claude Code 2.1.316 (Claude Code)»). Одинаково в обоих местах: общий помощник, например в `Infrastructure/Chat/DisplayFormat.cs`:
    ```csharp
    public static string AgentLabel(string name, string? version) =>
        version is not { Length: > 0 } ? name
        : version.Contains(name, StringComparison.OrdinalIgnoreCase) ? version
        : $"{name} {version}";
    ```
    и `AgentLabel(agent.DisplayName, monitor.CliVersion)` / `AgentLabel(agent.DisplayName, probe.Version)`.
20. **мелочь** `StartupNotice.SendAsync` всё ещё зовёт `agent.Probe()` (ещё один `claude --version`, :~21), хотя комментарий в `ChatGatewayService` говорит «третий запуск не нужен». Можно взять `monitor.CliVersion` (в `StartupNotice` добавить `RunMonitor`) — экономит процесс на старте.
21. **мелочь** `Outcome` записи «старт» = «pid:123, container» — поле исхода (ok/error/...) используется как произвольный текст; лучше положить pid в `Summary`: `старт … · pid 123 · container`. Если `/audit` или монитор разбирают `Outcome`, можно сломать фильтр — не проверял.
22. **мелочь** «старт» пишется только после успешного `ConnectAsync`; если Telegram недоступен всё время, в журнале нет ни записи о запуске, ни о причине. Версии уже в логе (`GatewayInfrastructure`), так что приемлемо.

## 9. Харнес

Запуск: все проверки OK. Мутационно рассудил (без правок кода), какие бы прошли и без исправления:
- «run.end после /stop: причина — /stop» и «таймаут» — упали бы без исправления (раньше хвоста нет). Хорошие.
- «остановка шлюза: запуск остался в state.json» — упала бы (раньше `EndRun`). Хорошая. Но она не различает путь `return` и путь исключения из `DeleteQuietlyAsync` (оба оставляют запись) — так задумано; FakeChannel с `Task.FromCanceled` как раз вынуждает путь исключения, т. е. ветка `if (stopped) return;` харнесом не исполняется. **риск**: ветку можно сломать, ничего не заметив.
- «run.end: причина ошибки, секрет замаскирован» — маска в `NowByKeys`, не в `ClaudeBackend`; маска в `ClaudeBackend`/`ClaudeLimits` харнесом не проверена (там логгер: нужен `Text.ClipRedacted` в тесте строк; харнес с фальшивым CLI из CLAUDE.md годился бы).
- Хрупкое: `Task.Delay(1100)` (check.cs:~205) — замок ленты считается от входа в `LockFolderAsync`; если лента встала в очередь позже, чем 1.1 с до `Release`, проверка станет ложной. Лента ставится в очередь раньше (до t3?) — по порядку кода верно, но завязано на порядок. Надёжнее `Until(() => channel.Sent.Any(text.Contains("⏳")))` после `Delay`.
- `LastOf(string kind, string prompt)`: параметр `prompt` не используется (check.cs:~388); `EndOf` берёт последнюю запись `t1` — работает, потому что полоса одна и запуски идут последовательно, но название вводит в заблуждение. Исправление: убрать параметр `prompt` у `LastOf` либо фильтровать по `Prompt`/полю `Session` (в Fake — `request.NewSessionId`).
- `EndOf` по счётчику записей: гонки нет (одна полоса темы 1), но если вдруг придёт чужая `run.end` темы 1 (например, оставшийся хвост предыдущего блока) — она засчитается. Блок перед ним ждёт `!worker.IsBusy(t1)`? Перед первым `EndOf` — `backend.Hold = false` без ожидания пустой полосы. Предложение: `await Until(() => !worker.IsBusy(t1))` в начале `EndOf`.
- **Не покрыто**: `file.send` (порядок «причина: путь» в `OperationConsole.Refuse`), «старт» (`ChatGatewayService`), `delivery partial` (только `lost`; нужен ответ из нескольких частей), `lockWait` при нескольких кругах, `runKind` (вопрос/контекст/сжатие/беседа), маска `внутренней ошибки` (секрет в `ex.Message`), `Ok:false` + `RateLimited`, остановка шлюза для вопроса (`ActiveQuestion`), `delivery unreachable` для общей полосы вопросов, маска в `ClaudeBackend`/`ClaudeLimits`.

## 10. Упрощение

23. Блок `spent`/`reason` в `ChatWorker.cs:~528-536` можно вынести в `RunEndSummary(result, modelLabel, runCts.IsCancellationRequested)` — статический метод, который тестируется отдельно. `ProcessAsync` и так длиннее экрана.
24. `Audit(prompt, AuditKinds.Delivery, …)` + try/catch на двух ветках `ConsumeAsync` (:~202, :~210) — одинаковый шаблон: `AuditSafe(prompt, kind, summary, outcome)` с внутренним `try/catch` и логом. Убирает два вложенных блока.
25. `runKind` через `switch` + `(talk ? …)` можно собрать списком частей `new[]{ kindLabel, talk ? "беседа" : null, modelLabel ?? "модель по умолчанию", mode, $"effort {…}" }` и `string.Join(", ", …Where(p => p is not null))` — то же, что в п. 6, один приём на обе записи.

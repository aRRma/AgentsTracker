# B7, B9, сверка старого аудита, документация (проверка по коду, ветка topics, HEAD 8926a0f)

Только чтение. Номера строк — на текущем коде.

## 1. B7: «старт» / «стоп» / «не запустился»

Что пишется сейчас (все ссылки на `src/AgentsTracker.Gateway/Infrastructure/Chat/ChatGatewayService.cs`):
- «старт» — строка 44: `AuditEvent.Now(AuditKinds.Gateway, "старт", project: store.Feed.ProjectPath)`. Только Summary и Project (в `NowByKeys` сводится к имени папки, `AuditEvent.cs:55`). Пишется после успешного `ConnectAsync` (строки 35–42) и до `notice.SendAsync` (45).
- «стоп» — строка 53, в `finally` вокруг `channel.ListenAsync` (47–54). Без полей. Пишется на любой выход из `ListenAsync`: остановка хоста, сбой канала (исключение). Не пишется, если «старт» не был.
- Не смог подключиться (токен 6 раз `Unauthorized`, строка 79–85, или отмена в ожидании, 72–74, 98–99): журнал пуст, только `LogCritical` (81) и `Environment.ExitCode = 1` + `StopApplication` (39–40). Сеть недоступна — ждёт без предела (92–101), в журнал ничего.
- Падение на `ValidateStartup` (`GatewayInfrastructure.cs:93`–150): `LogCritical` на каждую ошибку (строка ~103 `foreach ... LogCritical`), `return 1` (Program.cs:78). Журнал не пишется. Агент не найден (`InvalidOperationException` от `agent.Probe()`) — `LogCritical`, `return 1`. Ошибки неизвестного агента/канала в `Program.cs:59–61, 69–71` — только `Console.Error`.

Что есть под рукой для новых полей:
- Версия шлюза: **есть** `AppVersion.Current` (`Infrastructure/AppVersion.cs:16`), уже в сообщении «запущен» (`StartupNotice.cs:27`), логе (`GatewayInfrastructure.cs:93`), мониторе (`MonitorModule.cs:117`). Обрезает `+коммит`. Локальная сборка — `0.0.0-dev`.
- Версия CLI: `IAgentBackend.Probe()` → `AgentProbe(Executable, Version)` (`Agents.Abstractions/IAgentBackend.cs:4,35`); `ClaudeBackend.cs:31`. **Каждый вызов запускает `claude --version`** (`ClaudeCliLocator.cs:48–70`, таймаут 10 с, без кэша; `Probe` зовётся дважды за старт: `GatewayInfrastructure.cs:113` и `StartupNotice.cs:22`). Результат уже кэшируется в `RunMonitor.CliVersion` (`RunMonitor.cs:58`, ставится в `GatewayInfrastructure.cs:120`). Для «старта» дешевле всего брать `RunMonitor.CliVersion` (RunMonitor — Infrastructure, как `ChatGatewayService`, слои не нарушаются). Он `null`, если `--version` не вышла (`GatewayInfrastructure.cs:123` Warning).
- pid: `Environment.ProcessId` (нигде в журнале/старте не используется; в коде встречается только в `McpConfigFile.cs:54`, `WindowsScheduledTaskInstaller.cs:24`, `RunningGateway.cs:18`).
- Контейнер: `Container.Detected` (`Infrastructure/Container.cs:46`).
- Код выхода: `Environment.ExitCode` (ставится только в `ChatGatewayService.cs:39` и читается в `Program.cs:86`). В `finally` «стоп» штатного пути он ещё 0.
- Причина остановки: **недоступна как данные**. `UseWindowsService`/`AddWindowsService`/`ShutdownTimeout`/`AppDomain.UnhandledException` в коде нет (grep по `src`, 0 совпадений). `IHostApplicationLifetime` не говорит, кто остановил (Ctrl+C, SIGTERM, Планировщик, `StopApplication`). Различимы только: (а) `stoppingToken.IsCancellationRequested` в `finally` = штатная остановка хоста; (б) исключение из `ListenAsync` = сбой канала (в `finally` его не видно без `catch`). Telegram `ListenAsync` (`TelegramChannel.cs:118–148`) на ошибки polling не падает (повторяет с паузой), так что (б) почти недостижимо.
- «Не запустился»: единственная точка с известной причиной — `ConnectAsync` вернул false (токен `Unauthorized` — строка 76–85; отмена — 72–74). Для ошибок конфига audit можно взять из `app.Services.GetRequiredService<IAuditLog>()` в `ValidateStartup` (`JsonlAuditLog` — singleton, `GatewayInfrastructure.cs:36`, папка данных `AppPaths.DataDirectory`); он пишется до `DataDirectoryAcl.Restrict` (`GatewayInfrastructure.cs` ближе к концу `ValidateStartup`), но файл унаследует права папки.

Простейший вариант B7 (на строки):
- «старт»: `Summary = $"старт {AppVersion.Current}, {agent.DisplayName} {monitor.CliVersion ?? "?"}"`, `Outcome = $"pid:{Environment.ProcessId}"` + `" container"` при `Container.Detected`. Нужны в конструкторе `ChatGatewayService` `IAgentBackend` (уже в DI) и `RunMonitor`. Лимит 200 знаков хватает. ~5 строк.
- «стоп»: `Outcome = stoppingToken.IsCancellationRequested ? "stopped" : "error"`; код выхода не писать (он 0 на этом пути). ~3 строки.
- «не запустился»: перед `return false` на строке 84 — запись `gateway`, `"не запустился: токен не принят"`, outcome `unauthorized`. ~3 строки. Ошибки `ValidateStartup` — по желанию одной записью `"не запустился: конфиг"` (~4 строки), иначе лог.
- Побочное: на старте `Probe()` уже вызывается дважды (запуск процесса `claude --version` два раза) — можно заодно убрать второй вызов в `StartupNotice`, но это не часть B7.

(Уточнение: `LogCritical` ошибок конфига — `GatewayInfrastructure.cs:107`, ошибки агента — `:129`, `DataDirectoryAcl.Restrict` — `:138`.)

## 2. B9: `limit.refused` и `file.send`

### limit.refused
- Запись: `ChatWorker.cs:323` — `Audit(prompt, AuditKinds.LimitRefused, "лимит тарифа")`. Outcome пуст. Старый аудит указывал `:269` — строка съехала.
- Под рукой в том же методе: `model` (алиас) и `modelLabel` (с версией, `store.ModelLabel`) объявлены выше, до проверки; `exhausted` — готовая строка отказа (`ChatWorker.cs:322`).
- `IAgentLimits.RefusalAsync(model, ct)` возвращает только `string?` (`Agents.Abstractions/IAgentLimits.cs`): окно (`LimitWindow.Key`, `ResetsAt`) наружу не отдаётся. Его знает только `ClaudeLimits.cs:66–87`: окно выбирается на `:68–71`, в лог Warning идёт `{Window}, {Used}, {ResetsAt}` (`:75–77`), в строку отказа — `Describe(window.Key)` («5 часов», «неделя», «неделя, <модель>», `:189–195`) и «Сброс {Moment}» (`:79`, `Moment` — «через 2 ч 10 мин (19:40)», `:201`).
- Первая строка `exhausted` (до `\n\n`) — «🚦 Лимит тарифа исчерпан: окно «5 часов» выбрано полностью. Сброс через 2 ч 10 мин (19:40).» (`ClaudeLimits.cs:86`). Хвост (`:87`, кредиты `:81–84`) в журнале не нужен.
- Простейший вариант без смены контракта: `Audit(prompt, AuditKinds.LimitRefused, $"{modelLabel}: {Text.Preview(exhausted.Split('\n')[0], 150)}")`. Окно и сброс попадают в журнал, `IAgentLimits` не трогается. Минус: журнал привязан к тексту агента (формат человеческий, парсить его никто не будет). Чистый вариант (record вместо `string?`) меняет контракт, `NoAgentLimits`, `ClaudeLimits` и вызов — ~25 строк, для B9 избыточно.
- Сколько задач снято из очереди (`DropQueueAsync`, `ChatWorker.cs:325`) в журнал не идёт. Нужно ли — решать вам, не проверял.

### file.send
- Успех: `OperatorConsole.cs:303` — `"{имя файла} ({размер})"`, outcome `photo`/`document`.
- Отказ: `OperatorConsole.cs:308–313` (`Refuse`): `Summary = $"{Text.Preview(path, 120)}: {reason}"`, outcome `refused`. **Путь (до 120 знаков) первым, причина вторым.** Предел `AuditEvent.SummaryLimit = 200` (`AuditEvent.cs:38`) применяется в `NowByKeys` (`:54`) и режет хвост, то есть причину. Верно.
- Длинные причины (длины — моя прикидка на глаз, не измерял): `:253` «Файл вне текущего проекта ({project}). Отправлять можно только файлы из него.» — ~77 знаков плюс полный путь проекта; `:257` перечисляет все расширения из `AllowedExtensions` (~100); `:266` (~110); `:272` (~80). Путь до 120 + ": " + причина 75–110 даёт больше 200, причина обрезана «…». Короткие («Файл не найден.», «Путь к файлу пуст.») влезают.
- Простейший вариант: `$"{reason}: {Text.Preview(path, 80)}"` в `OperatorConsole.cs:311`. Причина (кроме `:253` с длинным путём проекта) укладывается целиком, режется хвост — путь файла. Одна строка. Лог Warning (`:310`) пишет путь и причину целиком — не меняется.
- `Text.ClipRedacted` с длинным путём (`Agents.Abstractions/Text.cs:30`): `Clip(Redact(Clip(text, limit*4)), limit)`. Для Summary: грубо до 800, маска, затем `Clip` до 200 (`text[..199].TrimEnd() + "…"`, `Text.cs:15`). Исключений нет, обрезанный путь остаётся с «…». Маска идёт до окончательной обрезки: токен в пути не успеет обрезаться «по форме». `Preview(path,120)` до этого: `ReplaceLineEndings`, `Trim`, `ClipRedacted(…,120)` (`Text.cs:22–23`).
- Замечание из старого аудита «нет размера у отказа» — верно: размер есть в тексте причины только на `:272`.

## 3. Сверка старого аудита (`audit-observability.md`) с текущим кодом

Вердикты: верно / неверно / исправлено. Строки — текущие.

### Таблица 1 «Инвентарь записей журнала»
| Строка | Вердикт | Где сейчас |
|---|---|---|
| Старт шлюза | верно (пробелы те же) | `ChatGatewayService.cs:44`; «не стартовал — журнал пуст» подтверждено (`:35–42`) |
| Стоп шлюза | верно | `ChatGatewayService.cs:53` |
| message text | верно, место съехало | `ChatEnqueueTextHandler.cs:43` (было 39); пользователь, чат, проект, сессия есть |
| message команда | верно | `ChatDispatcher.cs:42–43` |
| `/ask` | верно | `QuestionLauncher.cs:33` (сессии нет, проект есть) |
| Кнопки контекста | верно | `ContextScreen.cs:78` |
| run.start | верно | `ChatWorker.cs:399`; в сводке есть «вопрос,»/«беседа,», модель с версией, режим, effort, ветка. Нет ожидания, RunId, `AgentRunKind` |
| run.end | **верно, не исправлено (B4)** | `ChatWorker.cs:492`: только `{Duration}, ходов N`; outcome ok/cancel/rate-limit/error (`:482–488`) |
| Прерван рестартом | верно | `StartupNotice.cs:90–91` (с `NowByKeys`, user/chat/project/session есть) |
| limit.refused | верно | `ChatWorker.cs:323` («лимит тарифа», без модели и окна) |
| session.reset | **частично исправлено** | `ChatWorker.cs:658`: теперь `chat: chat` передан; пользователя по-прежнему нет |
| settings (SettingsAudit) | **исправлено** | `SettingsAudit.cs:10–13`: `Changed(ChatScope scope, …)` пишет `scope.Chat` (`ChatScope.Chat` — `ChatId?`, `SessionStore.cs:72`). 12 вызовов (`AgentScreen:82,96,115,127`, `ContextScreen:58`, `ProjectScreen:73`, `SessionsScreen:33,48,59`, `SkillsScreen:141`, `UsageScreen:24`, `TopicScreen:28`) |
| Тип темы | **частично исправлено** | `TopicScreen.cs:28` — чат теперь есть; Project по-прежнему имя папки беседы: `SessionStore.cs:663–669` (`TalkFolder(chat)`), `NowByKeys` берёт `GetFileName` (`AuditEvent.cs:55`) |
| `/new`, `/stop` | верно | `ChatCommandHandler.cs:32,40` (outcome `stopped`/`idle`), `SessionsScreen.cs:40`, `StatusScreen.cs:28`; числа остановленных нет |
| Карточка подтверждения | верно, место иное | запись одна `Audit` (`OperatorConsole.cs:382`), вызовы `:47,59,70,75,99`; AskUserQuestion `:132,147,152` |
| Правило «Всегда» | верно | `OperatorConsole.cs:109`, `RulesCommandHandler.cs:87` |
| Доверие | верно | `ProjectTrustGate.cs:55,80,86` |
| Файл от агента | верно | `OperatorConsole.cs:303,311`; причина режется (раздел 2) |
| Файл из чата | верно | `AttachmentInbox.cs:120,270` |
| Доступ отклонён | верно | `ChatDispatcher.cs:115,125` |
| Навыки/плагины | **неверно в части skill** | `SkillLauncher.cs:30–31` пишет `message` с `skill: …` и чатом; `SkillsScreen.cs:141` — через `Changed`, чат есть. «Тот же пробел ChatKey» — исправлено |

Не было в старой таблице, есть сейчас: `question` (`OperatorConsole.cs:192`, вид `AuditKinds.Question`), `file.receive refused` для беседы (`ChatEnqueueTextHandler.cs:26`).

### Таблица «Что нигде не пишется»
- Ожидание замка папки: верно. `ChatWorker.cs:577` (`LockFolderAsync`, было 508): шлёт в чат «⏳», в лог только ошибку отправки (`:591`); в журнал и в лог о ожидании ничего.
- Время в очереди: верно (нет).
- Запуск CLI не удался: верно, `ClaudeBackend.cs:86` LogError.
- Код выхода / subtype: верно, `ClaudeBackend.cs:523,554` в логе.
- Таймаут: верно в сути: `ClaudeBackend.cs:134–135` кладёт причину в текст; в журнале `cancel`/`error` (`ChatWorker.cs:482–488`). Не проверял, как именно `AgentRunResult` помечает таймаут (`Cancelled`?).
- Тема удалена: **частично устарело**. Теперь шлюз сам снимает очередь темы и пишет Warning (`ChatWorker.cs:195–199`, `DrainTasks`); фраза «каждое сообщение снова падает» относится к прошлому коду (в очереди — нет; будущие сообщения — не проверял). В журнале по-прежнему ничего.
- Потеря ответа: верно, `ChatWorker.cs:844` (SendRenderedAsync) и `:877` (SendPlainAsync), только LogError.
- «Внутренняя ошибка шлюза»: верно, `ChatWorker.cs:203–205`.
- Сбой записи `state.json`: верно, `SessionStore.cs:886` LogError (было 844).
- Polling Telegram: верно, `TelegramChannel.cs:140`.
- `UnhandledException`/`UnobservedTaskException`: верно, в `src` нет.
- 429/400-повторы: не проверял.

### Раздел 2 «что только в логе»
Строки съехали: «Не удалось отправить часть ответа» `ChatWorker.cs:844`, «…сообщение» `:877` (и `:591`); `claude завершился с кодом` `ClaudeBackend.cs:523` (было 513). Суть верна. Остальное (Polly, лимиты) — тема лога, не проверял.

### Раздел 5 «что ещё записывать» — статус
1. Причина провала в `run.end` — не сделано (B4).
2. Токены/модель в `run.end` — не сделано (B4).
3. Ожидание замка/очереди — не сделано (B5).
4. `ChatKey` в `SettingsAudit` — **сделано** (но сигнатура другая: `Changed(ChatScope, UserId, …)`, а не `Changed(ChatId, ChatScope, …)`; пп. аудита «12 вызовов» — совпало).
5. Версия/pid в «старт» — не сделано (B7). Предложение аудита `Outcome = pid:…`, `Environment.ExitCode` в «стоп» — см. п. 1: `ExitCode` в `finally` всегда 0, предложение неудачно.
6. Потеря ответа → журнал (`delivery`) — не сделано (B6).
7. Мёртвая тема — логика шлюза добавлена (`ChatWorker.cs:195–199`), журнал и «пометка на тему» — нет.
8. `UnhandledException` — не сделано (B8).
9. Лимит — не сделано (B9).
10. Причина файла до пути — не сделано (B9).
11. RunId в `run.start` — не сделано, в плане B нет.
12. `KnownVersions`: **подтверждаю — не тронут намеренно**. Код: `ClaudeCapabilities.cs:18–23` (`sonnet-5`, `opus-5-5`, `fable-5-1`); план: `plan-rework.md:75–77` (CLI сам ведёт `sonnet` на `claude-sonnet-5`; `sonnet-5-5` — из `ANTHROPIC_DEFAULT_SONNET_MODEL` на этой машине, подтверждено `docs/en/claude-permissions.md:199`). Строка A10 в плане (`plan-rework.md`, таблица A) ещё упоминает правку `KnownVersions`, а «Как сделан» (`:75`) её отменяет: в таблице A10 осталась устаревшая формулировка.

### Раздел 6 «Порядок работ»: п.2 `ChatKey` — сделан; остальное — задачи B1–B9.

## 4. Документация про журнал

Что есть и где (en → ru-зеркало):
- Архитектура, раздел «Audit» — `docs/en/architecture.md:358–376` → `docs/architecture.md:~353–369`. Перечисляет виды (`:372–374`), роли журнал/лог (`:374–375`). Описание полей «старт»/«стоп», `gateway`-записей, что журнал пишется и при неудачном старте, — нет. Про данные: `:336` (папка данных, `audit\…`) → `docs/architecture.md:331`.
- Структура проекта: `architecture.md:38–40, 55` (Audit, RingBufferLog).
- `deployment.md:228` (таблица файлов: `audit\audit-YYYY-MM.jsonl`), `:232` (бэкап), `:235–237` «There is no log file…» (станет неверно после B1) → `docs/deployment.md:225,229`, и русский вариант «нет файла лога» около `:231–233` (не открывал точно). `:268` «version» в первой строке лога.
- `monitor.md:31` (`/api/audit`), `:32` (`/api/log`), `:54–55` (`RingBufferLog` 500 записей, Information+) → `docs/monitor.md:30,54`.
- `glossary.md:48–49` (таблица: журнал/лог), `:110–112` (абзац «журнал / лог») → `docs/glossary.md:49–50,107–109`. Термина **«доставка»** нет ни в en, ни в ru (grep `доставк|deliver`: 0). Для B6 нужен: добавить в оба глоссария до кода (правило CLAUDE.md «Terminology comes from the glossary»). Термин «старт»/«стоп» в записях не в глоссарии (summary `старт`/`стоп` — строки кода; в UI «запущен» — `StartupNotice.cs:27`).
- `safety.md:30` (отклонённые → журнал), `:71` (`/audit`), `:127` (секреты как `***` в журнале, логе, мониторе) → `docs/safety.md:29,70,126`. Файла лога там не упомянуто — исправлять нечего, добавить абзац о том, что в файловом логе тоже `***`, если B1/B2 будут.
- `extending.md:44–45` (`IAuditLog.Changed` через `SettingsAudit` — по сути extension-метод `audit.Changed(scope, user, …)`, формулировка приблизительная) → `docs/extending.md:43–44`. **Чек-листа «добавить новый `AuditKinds`» нет** (grep `AuditKinds` в `docs/en/extending.md`: 0).
- `operations.md:134–136` (харнес, записи журнала); `cli-contract.md:137,170` (`file.send`, `file.receive`).
- README: `:90, :93, :200, :258, :273, :281, :323` — про журнал/лог (список упоминаний; описание вида записей нет).
- CLAUDE.md: правило про аудит/`Text.Redact` и «kinds in `AuditKinds`» (раздел «Sessions, state, audit»).

Расхождения документации с кодом про журнал (проверено):
1. `architecture.md:372–374` — перечень видов совпадает с `AuditKinds` (`AuditEvent.cs:6–19`, 14 видов): расхождения нет.
2. `architecture.md:364` «user's text is a Text.Preview excerpt of 80» — верно для `message` (`ChatEnqueueTextHandler.cs:43`). Но `skill: {text}` (`SkillLauncher.cs:31`) идёт без `Preview` — только общий предел 200 и маска. Мелкое расхождение, не проверял, `text` уже короткий ли.
3. `extending.md:44` — «`IAuditLog.Changed`» без указания, что это extension с `ChatScope`. Неточность, не ошибка.
4. Больше расхождений не нашёл. Не проверял: русский текст `docs/deployment.md` вокруг «нет файла лога».

Что править при B4–B9 (оба языка в одном коммите):
- B7: `architecture.md` «Audit» (одна строка: `gateway` — старт с версией шлюза и CLI, pid, стоп с причиной, «не запустился»); `glossary` не нужен. В `deployment.md` — не нужно.
- B9: ничего, кроме, возможно, `cli-contract.md:137` (если там описан формат отказа).
- B4/B5: `architecture.md` «Audit» (что в `run.start/run.end`), `monitor.md` не требует.
- B6: `glossary.md` (оба) — термин «доставка» + строка в таблицу; `architecture.md` «Audit» — вид `delivery`; `AuditKinds` (код); в `monitor.md` ничего.
- B1–B3 (не моя часть): `deployment.md:235–237`, `architecture.md`, `monitor.md:54–55`, `safety.md:127`, `operations.md`.
- B8: `architecture.md` «Audit» (вид или запись в `gateway`).
- Чек-лист `extending.md` «новый вид журнала»: нужно ли — на ваше усмотрение (добавить вид в `AuditKinds`, строку в `architecture.md` Audit, термин в глоссарий, проверить `/audit` и монитор); сейчас его нет.

## 5. Кто читает журнал и что сломается

- `/audit`: `AuditCommandHandler.cs:24–68`. Формат строки: `{время} {Kind} {Outcome} · {Summary} · {Project} · {User}`; Kind+Outcome вместе режутся до 40 знаков (`:65`), Summary — `EscapeCapped(…, 160)` (`:66`). Новый kind или длинный outcome не ломают: всё выводится текстом. Длинный outcome (например `pid:12345 container`, ~20 знаков) + kind `gateway` (7) влезает в 40. `ChatKey` в /audit не показывается.
- Монитор: `MonitorModule.cs:99–100` отдаёт `Tail` как JSON (камелКейс, `Json`); `index.html:539–544` выводит `kind`, `summary`, `outcome`, `userKey`, `project` через `esc()` в таблицу из 6 колонок; **по значениям kind не ветвится** (нет CSS-класса/фильтра по kind). Новый kind и любой формат summary/outcome безопасны. Колонки «чат» нет — `chatKey` в мониторе не виден (не баг, но B6/B7 с chat не виден).
- `StartupNotice`: журнал **не читает** (берёт `store.TakeInterruptedRuns()` из `state.json`, `StartupNotice.cs:19`) — пишет только `run.end interrupted` (`:90`). «старт»/«стоп» не влияют.
- Скрипты: grep по `*.ps1` на `AuditKinds`/`api/audit`/`audit-*.jsonl` — 0 совпадений вне .cs/.md; `monitor-api.ps1` просто ходит на `api/…`. Парсеров вида «gateway»/«старт» нет.
- Парсер файла: `JsonlAuditLog.Parse` (`:88–93`) — `Deserialize<AuditEvent>`, битая строка пропускается; новые поля в `AuditEvent` не добавляем в моих вариантах, значит старые файлы читаются как раньше.
- `Tail` читает целиком файл месяца (`ReadAllLines`, `JsonlAuditLog.cs:62`): ещё одна запись на старт/стоп/отказ лимита объём не меняет.
- Вывод: новые kind/summary/outcome безопасны для `/audit`, монитора, `StartupNotice` и парсера. Единственное ограничение — Summary ≤ 200 знаков, Kind+Outcome ≤ 40 в `/audit`.

## 6. Рекомендация

### B7 — самый простой вариант (~12 строк в `ChatGatewayService.cs`)
1. В конструктор — `IAgentBackend agent` (уже в DI) и `RunMonitor monitor` (версия CLI уже там, `GatewayInfrastructure.cs:120`; новый запуск `claude --version` не нужен).
2. «старт» (`:44`): `Summary = $"старт {AppVersion.Current}, {agent.DisplayName} {monitor.CliVersion ?? "?"}"`, `Outcome = $"pid:{Environment.ProcessId}"` плюс `" container"` при `Container.Detected`. Project оставить.
3. «стоп» (`:53`): `Outcome = stoppingToken.IsCancellationRequested ? "stopped" : "error"`. Код выхода не писать (в `finally` он 0). Причину «кто остановил» в данных взять нельзя (`UseWindowsService`/`UnhandledException` нет): честно писать только «остановка хоста» / «сбой канала».
4. «не запустился»: перед `return false` на `:84` записать `Gateway` «не запустился: токен не принят» + `Outcome = "unauthorized"` (3 строки). Ошибки конфига (`ValidateStartup`) — по желанию одной записью из `GatewayInfrastructure.cs:107/129` через `IAuditLog` (4 строки); иначе остаются только в логе — это приемлемо, потому что при ошибке конфига канал/ключи могут быть не готовы.
5. Не включать в задачу: второй вызов `agent.Probe()` в `StartupNotice.cs:22` (лишний запуск `claude --version`) — отдельная мелочь.

### B9 — самый простой вариант (~3 строки)
- `ChatWorker.cs:323`: `Audit(prompt, AuditKinds.LimitRefused, $"{modelLabel}: {Text.Preview(exhausted.Split('\n')[0], 150)}")`. Интерфейс `IAgentLimits` не менять.
- `OperatorConsole.cs:311`: `$"{reason}: {Text.Preview(path, 80)}"`. Лог (`:310`) не менять.

### Выкинуть из старого аудита как неактуальное
- Пункт 4 раздела 5 (`ChatKey` в `SettingsAudit`) и строки «Настройка изменена», «Навыки/плагины», «Тип темы» (в части чата) — сделано.
- «Тема удалена … каждое сообщение снова падает» — очередь теперь снимается (`ChatWorker.cs:195–199`).
- Все номера строк (кроме `AuditEvent`) — устарели; список выше.
- Предложение `Outcome = Environment.ExitCode` для «стоп» (раздел 5, п. 5) — не работает: в `finally` код выхода 0.
- В `plan-rework.md` таблица A10: упоминание правки `KnownVersions` противоречит «Как сделан этап A» (`:75`).
- Остальное (разделы 3–4 — лог и маска) — не моя часть, не оценивал.

### Список правок документации (оба языка, один коммит)
`docs/en/architecture.md` + `docs/architecture.md` (раздел Audit: gateway-записи, run.start/run.end после B4–B5, вид `delivery` после B6); `docs/en/glossary.md` + `docs/glossary.md` («доставка»); при B1 — `deployment`, `operations`, `monitor`, `safety` (не моя часть). `extending.md` — только если решите добавить чек-лист вида журнала. README и `safety.md` по B7/B9 не меняются.

### Оценка строк
B7 ~12, B9 ~3, документация по B7/B9 ~4 строки в каждом из двух языков. Всего по моей части ~20 строк кода, ~10 строк текста.

### Не проверял
Русский текст `deployment.md` около «нет файла лога»; как `AgentRunResult` помечает таймаут; поведение будущих сообщений в удалённую тему; 429/400-повторы; темы лога/маски (чужая часть).

# Контракт с Claude Code CLI

Когда читать: правите `ClaudeBackend`, `ClaudePermissionTool`, `ClaudeLimits`, работу с
сессиями или обновили CLI. Схема не задокументирована — всё ниже проверено на живом CLI 2.1.x.

## Подтверждения (MCP-инструмент `mcp__tg__approve`)

CLI зовёт инструмент с `{"tool_name":…,"input":{…},"tool_use_id":…}`. Ответ — JSON-строка:

- `{"behavior":"allow","updatedInput":{…}}` — `updatedInput` **обязателен**, без него CLI
  отклоняет вызов;
- `{"behavior":"deny","message":"…"}`.

`ClaudePermissionTool` читает поля защитно (`Read(...)` перебирает snake_case/camelCase). Сырой
payload логируется только на Debug: на Information для Edit/Write это содержимое файлов.

`AskUserQuestion` приходит в тот же инструмент и требует `updatedInput` с исходным `questions`
и `answers` (ключ — текст вопроса); в хост уходит как `IOperatorConsole.AskAsync`.

### Кнопка «Всегда»

Двояка:

- CLI прислал `permission_suggestions` с `destination: localSettings` — правило пишет **сам
  CLI** в `.claude/settings.local.json` проекта (обычно префиксное). Карточка показывает
  именно его, в хост оно приходит как `PersistentRule.Raw` и возвращается без изменений в
  `updatedPermissions`.
- Иначе шлюз запоминает точную сигнатуру в `state.json` (`AlwaysAllowByProject`, ключ —
  нормализованный путь проекта), видно в `/rules`. Правила шлюза действуют только в своём
  проекте: `git push --force` из одного репозитория не должен молча проходить в остальных.

### Невидимый хвост

Одобрять команду, которую не видно целиком, нельзя. Когда `ApprovalCard` что-то обрезал
(команда, стороны правки, остальные правки `MultiEdit`, хвост `Write`), `OperatorConsole`
перед карточкой шлёт полный текст файлом (`ApprovalBroker.SendAttachmentAsync`), карточка
предупреждает «показано не всё». Имя и содержимое файла решает `ApprovalCardRenderer`
(`ApprovalAttachment`): обычно `<инструмент>-input.txt` со сводкой «=== фрагмент ===», план
`ExitPlanMode` — целиком как `plan.md` (`Truncated.AddDocument`). Карточки собираются через
`EscapeCapped` с лимитом на каждый фрагмент: переполненное сообщение упало бы при отправке, а
исключение стало бы отказом.

### Самовыдача прав

Проверено на CLI 2.1.x: `Write` в `.claude/settings.local.json` отклоняется даже в
`acceptEdits` (виден в `permission_denials`). После обновления CLI перепроверить тем же
запуском во временной папке:

```powershell
claude -p "…" --permission-mode acceptEdits --output-format json
```

### Ожидание ответа

`ApprovalBroker` держит вызов MCP на `TaskCompletionSource` до нажатия и возвращает
`ChoiceResult` (ключ + кто нажал). `WaitAsync` различает таймаут и отмену: `/stop` должен
бросать `OperationCanceledException`, а не выглядеть как «не ответил вовремя».

Таймауты: `ApprovalTimeoutMinutes` (15) — карточка, `RunTimeoutMinutes` (60) — весь
`claude -p`; оба 1..1440. У CLI свой предел на вызов MCP-инструмента: по умолчанию он обрывает
вызов, если сервер молчит 5 минут (`CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT`, ошибка «sent no
response or progress for 300s»), — карточка в чате ещё висела, а агент уже получил отказ.
Поэтому `McpConfigFile` пишет серверу `timeout` = `ApprovalTimeoutMinutes` + 1 мин: это и
предел вызова, и (с CLI 2.1.203) нижняя граница порога простоя. Progress-уведомления от
сервера его не продлевают, слать их незачем. На CLI старше 2.1.203 порог простоя остаётся
5 минут, и карточка дольше не живёт.

## Запуск и поток событий

CLI запускается с `--output-format stream-json --verbose` (без `--verbose` поток в `-p` не
пишется). `ClaudeBackend.ReadStreamAsync` разбирает stdout построчно
(`ClaudeStreamEvent.Classify`): вызовы инструментов → `IAgentRunObserver.Activity`, остальное
отбрасывается — в долгом запуске это мегабайты. Итог — последняя строка `"type":"result"` той
же формы, что `--output-format json`; не-JSON строки (баннер обновления) идут в текст ошибки.
Текст ошибки CLI часто оставляет в stderr, а `result` присылает пустым (так с «No conversation
found» при битом `--resume`) — проверка сброса сессии и лимита смотрит и в stderr.

Идущий запуск записан в `state.json` (`GatewayState.ActiveRun`): `ChatWorker` ставит
`BeginRun` перед запуском и `EndRun` в `finally`. Запись на месте при старте — прошлый
экземпляр умер посреди работы: `StartupNotice` шлёт «🔌 Шлюз запущен» всем из
`IChatChannel.AllowedUsers`, в чат прерванного запуска (`ActiveRun.ChatKey` разбирает
`IChatChannel.ParseChat`) — «прерван, напишите „продолжай“», пишет `run.end` с исходом
`interrupted`. Пользователю, который ещё не писал боту, Telegram не даёт отправить первым —
`ChannelFailure.CannotReach` глотается на Debug.

В чате — `RunStatusMessage`: одно сообщение «Работаю…», раз в 4 с редактируется (время,
счётчик вызовов, три последних шага, сабагенты с `↳`). `Report` из потока stdout только
запоминает, сеть — в своём цикле; `DisposeAsync` дожидается цикла (≤10 с), иначе правка
догоняла бы удаление. Аргумент инструмента в статусе один и короткий
(`ClaudeStreamEvent.Describe`): полный ввод Edit/Write — содержимое файла.

## Сессии

Сессии ключуются **нормализованным путём проекта** (`ProjectCatalog.Normalize`): `--resume`
работает только в папке, где сессия создана; смена репозитория меняет и активную сессию.
`ChatWorker` фиксирует сессию и `ProjectPath` в `AgentRunRequest` до запуска — иначе
переключение посреди работы развело бы рабочий каталог и проект сессии.

Id новой сессии выдаёт **шлюз** (`NewSessionId` → `--session-id <uuid>`) и регистрирует по
`IAgentRunObserver.SessionStarted` сразу после старта процесса: иначе `/stop`, таймаут или
падение первого запуска теряли бы ветку. Сессия сбрасывается только когда CLI прямо говорит,
что не нашёл её (`LooksLikeMissingSession` → `AgentRunResult.SessionLost`), и только в
`ChatWorker.SettleSession` через `TrySetSessionId(onlyIfActive)` — чтобы не перетереть `/new`
или смену сессии, сделанные во время запуска.

`/sessions` показывает до 8 последних; кнопка несёт `ShortId` (8 символов), а не номер в
списке: завершившийся между отрисовкой и нажатием запуск сдвинул бы номера.

## Лимиты тарифа

Единственный ограничитель — `IAgentLimits`, окна `five_hour`, `seven_day`,
`seven_day_<модель>`; проверяется в `ChatWorker.ProcessAsync` перед запуском. Остаток —
`ShortSummaryAsync` (сводка меню) и `ViewAsync` (окна 0..1 — шкалы `LimitBars` на `/status`).

`ClaudeLimits` ходит в **недокументированный** `api.anthropic.com/api/oauth/usage` с токеном из
`~/.claude/.credentials.json`: требует правдоподобный User-Agent, отвечает 429 на частый опрос
(кэш 3 мин), может исчезнуть в любой версии — при любой ошибке запуск **пропускается**, не
блокируется, иначе шлюз замолчал бы целиком. Поля читаются через `ClaudeLimits.Number` с
проверкой `ValueKind`: `TryGetDouble` на `null` бросает, и `utilization: null` уходил бы
пользователю как «Внутренняя ошибка шлюза».

Кредиты («extra usage») агенту запрещены: `ClaudeBackend` ставит
`DISABLE_EXTRA_USAGE_COMMAND=1`, при обрыве по лимиту `ChatWorker` снимает всю очередь.

## Скиллы и плагины

`ClaudeSkillCatalog` собирает то же, что видит CLI: `skills/*/SKILL.md` и `commands/**/*.md`
из `.claude` проекта и `~/.claude`, плюс включённые плагины
(`~/.claude/plugins/installed_plugins.json`; `enabledPlugins` наслаиваются профиль →
`.claude/settings.json` → `settings.local.json`, без записи — включён). `user-invocable: false`
не показываются. Обход кэшируется на 5 с: нажатие в меню — это `Apply` и `Render` подряд;
«🔄 Обновить» — `Refresh()`.

Экран «🔌 Плагины» (`ClaudePluginRegistry`) переключает `enabledPlugins[имя@маркетплейс]`
только в личном `~/.claude/settings.json` — туда же пишет `/plugin` самого CLI. Файл
переписывается целиком через `JsonNode` (LF, без `\u`, через временный файл — соседняя сессия
CLI может читать его в этот момент), комментарии не переживут. Значение из слоя проекта
помечено 🔒 и из чата не меняется (`PluginInfo.LockedBy`). Кнопка несёт ключ плагина, а не
желаемое состояние — переворачивается действующее. Действует со следующего `claude -p`.

Встроенные скиллы CLI перечислить нельзя — группа «Встроенные» из
`Gateway:Claude:BuiltInSkills` (дефолт в `ClaudeOptions` под CLI 2.1.x, после обновления CLI —
конфигом без пересборки). «С аргументами» — `SkillLauncher.Expect` запоминает команду за
пользователем, следующий текст `SkillArgumentsTextHandler` превращает в `/команда текст`.
Запуски считаются в `SkillUsage` (суффикс `@бот` отрезается), «⭐ Частые» — до пяти самых
частых.

## Поиск бинарника

`claude.exe` ищет `ClaudeCliLocator`: `Gateway:Claude:Executable` → стандартные пути → PATH →
бинарник расширения VS Code (только чтобы завестись; путь с версией расширения исчезает при
обновлении — пишется предупреждение). Штатно — отдельный CLI
(`irm https://claude.ai/install.ps1 | iex`). Путь кешируется, но перепроверяется перед каждым
запуском. Версию `ValidateStartup` пишет в лог — разбор JSON держится на конкретной версии.

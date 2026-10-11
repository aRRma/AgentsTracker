# Как добавить в шлюз

Когда читать: добавляете команду чата, экран настроек, фичу, агента, канал, MCP-инструмент,
эндпоинт монитора, exe-команду, механизм автозапуска или ключ конфига. Каждый раздел — чек-лист:
пропущенный шаг оборачивается молчаливым падением на старте или возможностью, до которой никто не
доберётся. Расположение проектов и правила между ними — в [architecture.md](architecture.md).

## Команда чата

Класс с `IChatCommandHandler` в папке фичи, `AddSingleton` в её `*Module`, запись в тексте
`HelpCommandHandler` и, если команде место в списке «Меню» ниже, в `ChatCommandCatalog`
(`Infrastructure/Chat/`, подсказки команд в канале).

Заняты: `/start /help` (Help), `/new /stop` (Chat), `/ask` (Question), `/rules` (Approvals),
`/audit` (Audit), `/menu /settings /status /sessions /agent /model /effort /mode /skills /project
/usage` (Settings). **Дубль в двух фичах роняет запуск** (`ChatDispatcher` собирает словарь команд).

В списке «Меню» — `/menu`, экраны, `/ask`, `/rules`, `/audit` и `/help`, по частоте. `/new /stop
/model /effort /mode` работают текстом, но в список не входят: то же делают кнопки на экранах
«Сессии» и «Агент», а длинный список хуже короткого. `/ask` в списке, потому что вопрос набирают
сразу после команды. В `RootScreen` кнопки только у экранов.

Незнакомая слэш-команда уходит в CLI как есть: это команда самого Claude Code. Поэтому у экрана
«Контекст» нет своей команды `/context`: она заслонила бы команду Claude Code.

## Экран настроек

Класс с `ISettingsScreen` в `Features/Settings/Screens/`, `AddSingleton<ISettingsScreen, …>` в
`SettingsModule`, кнопка в `RootScreen`.

- `Key` — префикс экрана в данных кнопки: кнопка несёт `cfg:<Key>:<аргумент>`. Ключ уникален, а
  неизвестный молча открывает `root`: несовпадение выглядит как кнопка, которая ничего не делает.
- `RenderAsync` асинхронный из-за лимитов; `RenderFramesAsync` отдаёт необязательные кадры. Ими
  `StatusScreen` «наполняет» шкалы `LimitBars`: три кадра с паузой 350 мс, чаще — 429 от Telegram.
  Кадр, совпавший с предыдущим, пропускается вместе с паузой: шкала почти не израсходованного окна
  не двигается, и правка принесла бы только «message is not modified».
- Модель, Effort и режим — один `AgentScreen` с аргументами `model:…`/`effort:…`/`mode:…`.
- В `Apply` приходит `ChatId`: экран может поставить работу в очередь `ChatWorker`
  (`SkillsScreen`), и ответ уйдёт тому, кто нажал кнопку. Смена настройки пишется в журнал через
  `IAuditLog.Changed` (`SettingsAudit`).
- Позиция в списке живёт в `ScreenNavigation` по пользователям: экраны — синглтоны.
- Необратимая кнопка сначала спрашивает через `PendingConfirmations` (по пользователю: действие и
  сессия); «Да» несёт аргумент `yes:<действие>`, а `Open` снимает висящий вопрос. Пример —
  `ContextScreen`.
- Страницы и ключи callback_data — `SettingsKeyboard.Page`/`Key12`. Однобуквенный префикс аргумента
  экрана не должен быть шестнадцатеричным символом, иначе его спутают с ключом. Длину callback_data
  (64 байта) проверяет `TelegramChannel.Markup` и называет в ошибке кнопку — это ошибка экрана, а не
  транспорта.

## Фича

Папка в `Features/` с `*Module : IFeatureModule` (`Infrastructure/Modules/`: `AddServices` и
`MapEndpoints`, второй пустой, если монтировать нечего) и строка в списке модулей в `Program.cs`.
Текстовые обработчики (`IChatTextHandler`) опрашиваются в этом порядке, поэтому `ChatModule`
остаётся последним: его обработчик всегда возвращает `true`. Нажатия кнопок — `IChatButtonHandler`,
свои он узнаёт по префиксу данных кнопки (`cfg:` — меню).

## Проект

Новому проекту в `src/` нужны строка `<Project>` в `AgentsTracker.slnx`, `ProjectReference` в
`Gateway.csproj` и строка `COPY …/*.csproj` в `Dockerfile` (без неё restore молча пропускает проект,
а `publish --no-restore` падает).

`Directory.Build.props` и `.editorconfig` в репозитории нет: каждый csproj сам повторяет
`TargetFramework`, `Nullable`, `ImplicitUsings` и `TreatWarningsAsErrors`, а каждая библиотека
добавляет `RootNamespace`. Скопируйте этот блок у соседнего проекта — `dotnet new` выключает
предупреждения-как-ошибки и выбирает своё корневое пространство имён.

## Агент (Codex, Cursor)

Проект `src/AgentsTracker.Agents.<Name>` со ссылкой на `Agents.Abstractions` (и три шага из раздела
«Проект»). `IAgentBackendModule` регистрирует `IAgentBackend`, `IAgentLimits` (или `NoAgentLimits`),
`IAgentSkillCatalog` (или `NoAgentSkills`) и свой канал подтверждений через `IOperatorConsole`.
Строка в списке `agents` в `Program.cs`.

Каждое обращение к `IOperatorConsole` несёт обратно `AgentRunRequest.RunId`, иначе получает отказ
([architecture.md](architecture.md)). Донести id от запроса до своего канала подтверждений бэкенд
должен сам (Claude кладёт его в заголовок MCP-конфига, свой у каждого запуска).

`IAgentBackend.InspectProject` возвращает всё, что агент сам выполнит из папки проекта, с
отпечатком; `null` — только когда ничего такого нет ([cli-contract.md](cli-contract.md)). Бэкенд,
который возвращает `null`, хотя его CLI такие файлы читает, молча возвращает дыру, которую
закрывает карточка «Доверять проекту?».

`grep -rn Claude src/AgentsTracker.Gateway --include=*.cs` должен находить только `Program.cs` и
комментарии. Настройки агента лежат в `Gateway:<Id>`, хост их не читает.

## Канал чата (Slack, Discord)

Проект `src/AgentsTracker.Channels.<Name>` со ссылкой на `Channels.Abstractions` (и три шага из
раздела «Проект»). `IChatChannelModule` регистрирует `IChatChannel` и всё своё, читает настройки из
`Gateway:Channel:Settings` и перечисляет в `SecretKeys`, что шифрует `protect-secrets`. Канал несёт
свои типы `ChatId`/`UserId` (`Key` — «канал:значение») и `ChannelLimits`; сбои транспорта выходят
только как `ChannelRequestException`. Отказ в ключе бота обязан выйти как
`ChannelFailure.Unauthorized`, иначе хост примет его за сбой сети и будет повторять `ConnectAsync`
без конца вместо выхода. Строка в списке `channels` в `Program.cs` (выбор по
`Gateway:Channel:Type`).

Ответы на сообщения ([architecture.md](architecture.md)): заполнять `IncomingMessage.Message` и
`InReplyTo` (по нему свободный текст находит свою карточку, когда ждут несколько) и выполнять
`OutgoingMessage.ReplyTo` и `replyTo` файловых методов — ответ на вопрос приходит ответом. Ответ на
удалённое сообщение всё равно должен дойти (в Telegram — `AllowSendingWithoutReply`). Мессенджер
без ответов на сообщения может `ReplyTo` не выполнять.

Клиент транспорта заводите **лениво**: хост создаёт канал раньше, чем печатает ошибки конфигурации,
и конструктор, падающий на пустом токене, спрятал бы понятную ошибку за стектрейсом.

`grep -rn Telegram src/AgentsTracker.Gateway --include=*.cs` должен находить только `Program.cs`,
комментарии и несколько текстов сообщений (справка, `install`).

## MCP-инструмент

Класс с `[McpServerToolType]` в `Agents.Claude/Mcp/`, имя константой в `McpConfigFile`,
`.WithTools<…>()` в `ClaudeAgentModule`, нужный хосту метод — в `IOperatorConsole` (реализация в
`OperatorConsole`, `Features/Approvals/`).

Инструмент без карточки попадает в `--allowedTools` в `ClaudeBackend.BuildArguments`, и вся его
политика становится политикой шлюза. Там стоит только `mcp__tg__send_file`: хост сам проверяет
папку, тип и размер. Больше туда ничего нельзя — это обход карточек.

Белый список типов `send_file` живёт в нескольких местах — таблица в `OperatorConsole`, описание
инструмента в `ClaudeSendFileTool` (CLI видит описание **работающего** шлюза),
`docs/cli-contract.md` и `docs/safety.md` с их английскими близнецами в `docs/en/`, а также правило
«Длинные документы» в `CLAUDE.md`. Меняете тип — меняйте все.

## Эндпоинт монитора

`api.MapGet` в `MonitorModule.MapEndpoints`, только чтение — [monitor.md](monitor.md).

## Команда exe

Класс в `Infrastructure/Cli/` (`protect-secrets` живёт в `Security/`, рядом с `SecretsProtector`) с
`public const string Name` и `Run(string[] args, TextWriter output)` — плюс
`IReadOnlyList<IChatChannelModule> channels` перед `output`, если команде нужны ключи канала (как
`install` и `protect-secrets`). Строка в `switch` у `ConsoleCommands.TryRun` и в её `PrintUsage`.

Команды отрабатывают до сборки хоста: DI и канал недоступны, ответ идёт только в `output`, код
возврата — 0 или 1. Заняты: `protect-secrets`, `install`, `uninstall`, `help`. Аргумент с дефисом
или слэшем в начале — это конфигурация, а не команда.

## Механизм автозапуска (launchd, systemd)

Реализация `IAutostartInstaller` (наследник `ProcessAutostartInstaller`, у которого `Run`
защищённый) в `Infrastructure/Autostart/` и строка в
`AutostartInstaller.ForCurrentOs` (сегодня только Windows, на любой другой ОС —
`UnsupportedAutostartInstaller`). Положите файл-описание и позовите утилиту системы через
`ProcessAutostartInstaller.Run`: решайте по коду возврата, вывод локализован. XML для `schtasks` —
UTF-16: в UTF-8 кириллица в описании задачи превращается в кракозябры.

## Ключ конфига

Свойство в `GatewayOptions` (+ `Validate`), значение по умолчанию в `appsettings.json`, образец
`"//Ключ": "…"` в `appsettings.Local.example.json`, строка в «Основных настройках» README.

Ключ агента идёт в `ClaudeOptions` и `Gateway:Claude`, ключ канала — в его `*Options` и
`Gateway:Channel:Settings`. Исключение — `DataDirectory`: он нужен раньше конфига (в этой папке
лежит `appsettings.Local.json`), поэтому `AppPaths.UseConfiguredDirectory` читает его из
`appsettings.json` рядом с exe и из окружения.

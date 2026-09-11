# Как добавить в шлюз

Когда читать: добавляете команду чата, экран настроек, фичу, агента, канал, MCP-инструмент,
эндпоинт монитора, exe-команду, механизм автозапуска или ключ конфига. Каждый раздел — чек-лист:
пропущенный шаг оборачивается либо молчаливым падением на старте, либо возможностью, до которой
никто не может добраться. Расположение проектов и правила между ними — в [architecture.md](architecture.md).

## Команда чата

Класс с `IChatCommandHandler` в папке фичи, `AddSingleton` в её `*Module`, запись в
`ChatCommandCatalog` (подсказки команд в канале) и в тексте `HelpCommandHandler`.

Заняты: `/start /help` (Help), `/new /stop` (Chat), `/rules` (Approvals), `/audit` (Audit),
`/menu /settings /status /sessions /agent /model /effort /mode /skills /project /usage`
(Settings). **Дубль в двух фичах роняет запуск.**

В «Меню» только экраны — `/new /stop /model /effort /mode` работают текстом, но в списке их нет:
то же самое есть кнопками на экранах «Сессии» и «Агент», а длинный список хуже короткого. Список
команд и справка идут одним порядком: статус и сессии, агент и скиллы, проект, расход,
правила, журналы. `RootScreen` повторяет его до расхода включительно — кнопок для `/rules` и
`/audit` в меню нет.

Все прочие слэш-команды уходят в CLI как есть: незнакомая команда — это команда самого Claude Code.

## Экран настроек

Класс с `ISettingsScreen` в `Features/Settings/Screens/`, регистрация в `SettingsModule`, кнопка в
`RootScreen`.

- `RenderAsync` асинхронный из-за лимитов; `RenderFramesAsync` отдаёт необязательные кадры. Ими
  `StatusScreen` «наполняет» шкалы `LimitBars`: три кадра с паузой 350 мс, чаще — 429 от Telegram.
  Кадр, совпавший с предыдущим, пропускается вместе с паузой перед ним: шкала почти не
  израсходованного окна не двигается, и правка после ожидания принесла бы только «message is not
  modified».
- Модель, усилие и режим — один `AgentScreen` с аргументами `model:…`/`effort:…`/`mode:…`.
- В `Apply` приходит `ChatId`: экран может поставить работу в очередь `ChatWorker`
  (`SkillsScreen`), и ответ уйдёт тому, кто нажал кнопку.
- Позиция в списке живёт в `ScreenNavigation` по пользователям: экраны — синглтоны, пользователей
  может быть несколько.
- Страницы и ключи callback_data — `SettingsKeyboard.Page`/`Key12`. Однобуквенный префикс аргумента
  экрана не должен быть шестнадцатеричным символом, иначе его спутают с ключом. Длину callback_data
  (64 байта) проверяет `TelegramChannel.Markup` и называет в ошибке кнопку — это ошибка экрана, а не
  транспорта.

## Фича

Папка в `Features/` с `*Module : IFeatureModule` (`Infrastructure/Modules/`) и строка в списке
модулей в `Program.cs`; `ChatModule` остаётся последним — его текстовый обработчик всегда
возвращает `true`.

## Агент (Codex, Cursor)

Проект `src/AgentsTracker.Agents.<Name>` со ссылкой на `Agents.Abstractions`: `IAgentBackendModule`
регистрирует `IAgentBackend`, `IAgentLimits` (или `NoAgentLimits`), `IAgentSkillCatalog` (или
`NoAgentSkills`) и свой канал подтверждений через `IOperatorConsole`. Строка в списке `agents` в
`Program.cs`, ссылка в `Gateway.csproj` и строка `COPY` в `Dockerfile`.

`grep -rn Claude src/AgentsTracker.Gateway --include=*.cs` должен находить только `Program.cs` и
комментарии. Настройки агента лежат в `Gateway:<Id>`, хост их не читает.

## Канал чата (Slack, Discord)

Проект `src/AgentsTracker.Channels.<Name>` со ссылкой на `Channels.Abstractions`:
`IChatChannelModule` регистрирует `IChatChannel` и всё своё, читает настройки из
`Gateway:Channel:Settings` и объявляет в `SecretKeys`, что шифрует `protect-secrets`. Свои типы
`ChatId`/`UserId` (`Key` — «канал:значение»), свои `ChannelLimits`, сбои транспорта — только
`ChannelRequestException`. Строка в списке `channels` в `Program.cs` (выбор по
`Gateway:Channel:Type`), ссылка в `Gateway.csproj` и строка `COPY` в `Dockerfile`.

`grep -rn Telegram src/AgentsTracker.Gateway --include=*.cs` должен находить только `Program.cs` и
комментарии.

Клиент транспорта канал заводит **лениво**: хост создаёт канал раньше, чем печатает ошибки
конфигурации, и конструктор, падающий на пустом токене, подменил бы понятную ошибку стектрейсом.

## Проект

`Directory.Build.props` и `.editorconfig` в репозитории нет: каждый csproj сам повторяет
`TargetFramework`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors` и `RootNamespace`.
Скопируйте этот блок у соседнего проекта — `dotnet new` заводит проект без
предупреждений-как-ошибок и со своим корневым пространством имён, а всплывает это позже, никем не
замеченным предупреждением.

## MCP-инструмент

Класс с `[McpServerToolType]` в `Agents.Claude/Mcp/`, имя константой в `McpConfigFile`,
`.WithTools<…>()` в `ClaudeAgentModule`, нужный хосту метод — в `IOperatorConsole` (реализация в
`OperatorConsole`).

Если инструмент должен работать без карточки — в `--allowedTools` в
`ClaudeBackend.BuildArguments`, но тогда вся его политика ложится на шлюз. Так сделано для
`mcp__tg__send_file`: хост сам проверяет папку, тип и размер, а карточка «можно?» только
продублировала бы эту проверку лишним нажатием. Больше в `--allowedTools` не место ничему: это
обход карточек мимо настроек машины.

Белый список типов файлов `send_file` живёт сразу в пяти местах — таблица в `OperatorConsole`,
описание инструмента в `ClaudeSendFileTool` (CLI видит описание **работающего** шлюза),
`README.md`, `docs/cli-contract.md` и `docs/en/cli-contract.md`. Меняете тип — меняйте все пять.

## Эндпоинт монитора

`api.MapGet` в `MonitorModule.MapEndpoints`, только чтение — [monitor.md](monitor.md).

## Команда exe

Класс в `Infrastructure/Cli/` (`protect-secrets` живёт в `Security/`, рядом с `SecretsProtector`) с
`public const string Name` и `Run(string[] args, TextWriter output)` — плюс
`IReadOnlyList<IChatChannelModule> channels`, если команде нужны ключи канала (как `install` и
`protect-secrets`). Строка в `switch` у `ConsoleCommands` и в её справке.

Команды отрабатывают до сборки хоста: DI и канал им недоступны, ответ идёт только в `output`, код
возврата — 0 или 1. Заняты: `protect-secrets`, `install`, `uninstall`, `help`. Всё, что начинается
с дефиса, — не команда, а аргумент конфигурации.

## Механизм автозапуска (launchd, systemd)

Реализация `IAutostartInstaller` в `Infrastructure/Autostart/` и строка в
`AutostartInstaller.ForCurrentOs`. Подход везде один: положить файл-описание и позвать утилиту
системы через `ProcessAutostartInstaller.Run` — решать по коду возврата, их вывод локализован. XML
для `schtasks` пишется в UTF-16: в UTF-8 кириллица в описании задачи превращается в кракозябры.

## Ключ конфига

Свойство в `GatewayOptions` (+ `Validate`), значение по умолчанию в `appsettings.json`, образец
`"//Ключ": "…"` в `appsettings.Local.example.json`, строка в «Основных настройках» README.

Ключ агента идёт в `ClaudeOptions` и `Gateway:Claude`, ключ канала — в его `*Options` и
`Gateway:Channel:Settings`. Исключение — `DataDirectory`: он нужен раньше конфига (в этой папке
лежит сам `appsettings.Local.json`), поэтому читается в `AppPaths.UseConfiguredDirectory` из
`appsettings.json` рядом с exe и из окружения.

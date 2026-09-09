# Adding to the gateway

When to read this: you are adding a chat command, a settings screen, a feature, an agent, a
channel, an MCP tool, a monitor endpoint, an exe command, an autostart mechanism or a config key.
Every entry is a checklist — a step missed here shows up as a silent startup failure or as a
feature nobody can reach. The layout of the projects and the rules between them are in
`architecture.md`.

## A chat command

A class implementing `IChatCommandHandler` in the feature folder, `AddSingleton` in its `*Module`,
an entry in `ChatCommandCatalog` (the channel's command hints) and in the `HelpCommandHandler`
text.

Taken: `/start /help` (Help), `/new /stop` (Chat), `/rules` (Approvals), `/audit` (Audit),
`/menu /settings /status /sessions /agent /model /effort /mode /skills /project /usage`
(Settings). **A duplicate across two features kills startup.**

The «Меню» screen holds screens only — `/new /stop /model /effort /mode` work as text but are not
in the list: the same thing is available as buttons on the «Сессии» and «Агент» screens, and a
long list reads worse than a short one. The command list and the help share one order: status and
sessions, agent and skills, repository, statistics, rules, logs. `RootScreen` repeats it up to and
including statistics — there are no menu buttons for `/rules` and `/audit`.

Every other slash command goes to the CLI as-is: unknown slash commands are Claude Code's own.

## A settings screen

A class implementing `ISettingsScreen` in `Features/Settings/Screens/`, registration in
`SettingsModule`, a button in `RootScreen`.

- `RenderAsync` is async because of the limits; `RenderFramesAsync` returns optional frames.
  `StatusScreen` "fills" the `LimitBars` gauges with them: three frames 350 ms apart, more often
  gets a 429 from Telegram. A frame identical to the previous one is skipped along with the pause
  before it — the bar of a barely used window does not move, and the edit would only earn a
  «message is not modified» after a wait.
- Model, effort and mode are one `AgentScreen` with the arguments `model:…`/`effort:…`/`mode:…`.
- `Apply` receives the `ChatId`: a screen can enqueue work on `ChatWorker` (`SkillsScreen`), and
  the answer goes to whoever pressed the button.
- The list position lives in `ScreenNavigation` per user: screens are singletons, there can be
  several users.
- Pages and callback_data keys — `SettingsKeyboard.Page`/`Key12`. The one-letter prefix of a
  screen argument must not be a hex character, or it will be confused with a key. The
  callback_data length (64 bytes) is checked by `TelegramChannel.Markup`, which names the button
  in the error — that is a screen bug, not a transport one.

## A feature

A folder in `Features/` with a `*Module : IFeatureModule` (`Infrastructure/Modules/`) and a line
in the module list in `Program.cs`; `ChatModule` stays last, because its text handler always
returns `true`.

## An agent (Codex, Cursor)

A project `src/AgentsTracker.Agents.<Name>` referencing `Agents.Abstractions`: an
`IAgentBackendModule` registers `IAgentBackend`, `IAgentLimits` (or `NoAgentLimits`),
`IAgentSkillCatalog` (or `NoAgentSkills`) and its own approval channel through `IOperatorConsole`.
A line in the `agents` list in `Program.cs`, a reference in `Gateway.csproj` and a `COPY` line in
the `Dockerfile`.

`grep -rn Claude src/AgentsTracker.Gateway --include=*.cs` must only find `Program.cs` and
comments. Agent settings live in `Gateway:<Id>`, the host does not read them.

## A chat channel (Slack, Discord)

A project `src/AgentsTracker.Channels.<Name>` referencing `Channels.Abstractions`: an
`IChatChannelModule` registers `IChatChannel` and everything of its own, reads settings from
`Gateway:Channel:Settings` and declares in `SecretKeys` what `protect-secrets` encrypts. Its own
`ChatId`/`UserId` types (`Key` is "channel:value"), its own `ChannelLimits`, transport failures —
only `ChannelRequestException`. A line in the `channels` list in `Program.cs` (selected by
`Gateway:Channel:Type`), a reference in `Gateway.csproj` and a `COPY` line in the `Dockerfile`.

`grep -rn Telegram src/AgentsTracker.Gateway --include=*.cs` must only find `Program.cs` and
comments.

The channel takes its transport client **lazily**: the host creates the channel before it prints
configuration errors, and a constructor failing on an empty token would replace a readable error
with a stack trace.

## A project

There is no `Directory.Build.props` and no `.editorconfig`: every csproj repeats
`TargetFramework`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors` and `RootNamespace`
itself. Copy that block from a neighbouring project — `dotnet new` gives a project without
warnings-as-errors and with a root namespace of its own, and that only shows up later, as a
warning nobody notices.

## An MCP tool

A class with `[McpServerToolType]` in `Agents.Claude/Mcp/`, the name as a constant in
`McpConfigFile`, `.WithTools<…>()` in `ClaudeAgentModule`, and the method the host needs — in
`IOperatorConsole` (implemented in `OperatorConsole`).

If the tool must work without an approval card, add it to `--allowedTools` in
`ClaudeBackend.BuildArguments` — but then its whole policy is the gateway's own responsibility.
That is the case for `mcp__tg__send_file`: the host checks the folder, the type and the size
itself, and an "allow?" card would only duplicate that check with an extra button press. Nothing
else belongs in `--allowedTools`: it bypasses the cards past the machine's config.

## A monitor endpoint

`api.MapGet` in `MonitorModule.MapEndpoints`, read-only — `monitor.md`.

## An exe command

A class in `Infrastructure/Cli/` (`protect-secrets` lives in `Security/`, next to
`SecretsProtector`) with a `public const string Name` and `Run(string[] args, TextWriter output)`
— plus `IReadOnlyList<IChatChannelModule> channels` if the command needs channel keys (as
`install` and `protect-secrets` do). A line in the `switch` of `ConsoleCommands` and in its help.

Commands run before the host is built: DI and the channel are unavailable to them, the answer goes
to `output` only, the exit code is 0 or 1. Taken: `protect-secrets`, `install`, `uninstall`,
`help`. Anything starting with a dash is not a command — those are configuration arguments.

## An autostart mechanism (launchd, systemd)

An `IAutostartInstaller` implementation in `Infrastructure/Autostart/` and a line in
`AutostartInstaller.ForCurrentOs`. The approach is the same on every OS: drop a descriptor file
and call the platform utility through `ProcessAutostartInstaller.Run` — decide by the exit code,
their output is localized. The XML for `schtasks` is written in UTF-16: in UTF-8 the Cyrillic in
the task description turns into mojibake.

## A config key

A property in `GatewayOptions` (+ `Validate`), a default in `appsettings.json`, a `"//Ключ": "…"`
sample in `appsettings.Local.example.json`, a line in the README's «Основные настройки».

An agent key goes into `ClaudeOptions` and `Gateway:Claude`, a channel key into its `*Options` and
`Gateway:Channel:Settings`. The exception is `DataDirectory`: it is needed before the config (that
folder holds `appsettings.Local.json` itself), so it is read in `AppPaths.UseConfiguredDirectory`
from the `appsettings.json` next to the exe and from the environment.

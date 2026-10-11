# Adding to the gateway

When to read this: you are adding a chat command, a settings screen, a feature, an agent, a
channel, an MCP tool, a monitor endpoint, an exe command, an autostart mechanism or a config key.
Every entry is a checklist — a missed step shows up as a silent startup failure or a feature nobody
can reach. The project layout and the rules between projects are in `architecture.md`.

## A chat command

A class implementing `IChatCommandHandler` in the feature folder, `AddSingleton` in its `*Module`,
an entry in the `HelpCommandHandler` text and, if the command belongs in the «Меню» list below, in
`ChatCommandCatalog` (`Infrastructure/Chat/`, the channel's command hints).

Taken: `/start /help` (Help), `/new /stop` (Chat), `/ask` (Question), `/rules` (Approvals),
`/audit` (Audit), `/menu /settings /status /sessions /agent /model /effort /mode /skills /project
/usage` (Settings). **A duplicate across two features kills startup** (`ChatDispatcher` builds a
dictionary of commands).

The «Меню» list holds `/menu`, the screens, `/ask`, `/rules`, `/audit` and `/help`, ordered by
frequency. `/new /stop /model /effort /mode` work as text but stay out of the list: buttons on
«Сессии» and «Агент» do the same, and a long list reads worse. `/ask` is in because the question is
typed right after the command. `RootScreen` has buttons for the screens only.

An unknown slash command goes to the CLI as-is: it is Claude Code's own. So «Контекст» has no
`/context` command: it would shadow Claude Code's.

## A settings screen

A class implementing `ISettingsScreen` in `Features/Settings/Screens/`, `AddSingleton<ISettingsScreen, …>`
in `SettingsModule`, a button in `RootScreen`.

- `Key` is the screen's callback prefix: a button carries `cfg:<Key>:<argument>`. It must be unique,
  and an unknown key silently opens `root`, so a mismatch looks like a button that does nothing.
- `RenderAsync` is async because of the limits; `RenderFramesAsync` returns optional frames.
  `StatusScreen` "fills" the `LimitBars` gauges with them: three frames 350 ms apart, more often
  gets a 429 from Telegram. A frame identical to the previous one is skipped with its pause: the
  bar of a barely used window does not move, and the edit would only earn «message is not modified».
- Model, Effort and mode are one `AgentScreen` with the arguments `model:…`/`effort:…`/`mode:…`.
- `Apply` receives the `ChatId`: a screen can enqueue work on `ChatWorker` (`SkillsScreen`), and
  the answer goes to whoever pressed the button. A changed setting is written to the journal with
  `IAuditLog.Changed` (`SettingsAudit`).
- The list position lives in `ScreenNavigation` per user: screens are singletons.
- An irreversible button asks first through `PendingConfirmations` (per user: action and session);
  «Да» carries the argument `yes:<action>`, and `Open` drops the pending question. Example —
  `ContextScreen`.
- Pages and callback_data keys — `SettingsKeyboard.Page`/`Key12`. The one-letter prefix of a
  screen argument must not be a hex character, or it is confused with a key. `TelegramChannel.Markup`
  checks the callback_data length (64 bytes) and names the button — a screen bug, not a transport one.

## A feature

A folder in `Features/` with a `*Module : IFeatureModule` (`Infrastructure/Modules/`: `AddServices`
and `MapEndpoints`, the latter empty if there is nothing to mount) and a line in the module list in
`Program.cs`. Text handlers (`IChatTextHandler`) are polled in that order, so `ChatModule` stays
last: its handler always returns `true`. Buttons are `IChatButtonHandler`, told apart by a prefix of
the callback data (`cfg:` — the menu).

## A project

A new project under `src/` needs a `<Project>` line in `AgentsTracker.slnx`, a `ProjectReference`
in `Gateway.csproj` and a `COPY …/*.csproj` line in the `Dockerfile` (without it restore silently
skips the project and `publish --no-restore` fails).

There is no `Directory.Build.props` and no `.editorconfig`: every csproj repeats `TargetFramework`,
`Nullable`, `ImplicitUsings` and `TreatWarningsAsErrors`, and every library adds `RootNamespace`.
Copy that block from a neighbour — `dotnet new` turns warnings-as-errors off and picks its own root
namespace.

## An agent (Codex, Cursor)

A project `src/AgentsTracker.Agents.<Name>` referencing `Agents.Abstractions` (and the three steps
of «A project»). An `IAgentBackendModule` registers `IAgentBackend`, `IAgentLimits` (or
`NoAgentLimits`), `IAgentSkillCatalog` (or `NoAgentSkills`) and its own confirmation channel through
`IOperatorConsole`. A line in the `agents` list in `Program.cs`.

Every call into `IOperatorConsole` carries `AgentRunRequest.RunId` back, or it is refused
(`architecture.md`). The backend must carry the id from the request to its confirmation channel
itself (Claude puts it into a header of the run's own MCP config).

`IAgentBackend.InspectProject` returns everything the agent would run from the project folder on
its own, with a fingerprint; `null` only when there is nothing of the kind (`cli-contract.md`). A
backend that returns `null` while its CLI reads such files silently reopens the hole the
«Доверять проекту?» card closes.

`grep -rn Claude src/AgentsTracker.Gateway --include=*.cs` must only find `Program.cs` and
comments. Agent settings live in `Gateway:<Id>`, the host does not read them.

## A chat channel (Slack, Discord)

A project `src/AgentsTracker.Channels.<Name>` referencing `Channels.Abstractions` (and the three
steps of «A project»). An `IChatChannelModule` registers `IChatChannel` and everything of its own,
reads settings from `Gateway:Channel:Settings` and lists in `SecretKeys` what `protect-secrets`
encrypts. It brings its own `ChatId`/`UserId` types (`Key` is "channel:value") and `ChannelLimits`;
transport failures go out only as `ChannelRequestException`. A rejected bot key must come out as
`ChannelFailure.Unauthorized`, or the host takes it for a network failure and retries
`ConnectAsync` forever instead of exiting. A line in the `channels` list in `Program.cs` (selected
by `Gateway:Channel:Type`).

Replies (`architecture.md`): fill `IncomingMessage.Message` and `InReplyTo` (free text finds its
card by it when several wait), honour `OutgoingMessage.ReplyTo` and the `replyTo` of the file
methods — the answer to a вопрос is a reply. A reply to a deleted message must still be delivered
(Telegram: `AllowSendingWithoutReply`). A messenger without replies may ignore `ReplyTo`.

Take the transport client **lazily**: the host creates the channel before it prints configuration
errors, and a constructor failing on an empty token would hide a readable error behind a stack trace.

`grep -rn Telegram src/AgentsTracker.Gateway --include=*.cs` must only find `Program.cs`, comments
and a few message texts (help, `install`).

## An MCP tool

A class with `[McpServerToolType]` in `Agents.Claude/Mcp/`, the name as a constant in
`McpConfigFile`, `.WithTools<…>()` in `ClaudeAgentModule`, and the method the host needs — in
`IOperatorConsole` (implemented in `OperatorConsole`, `Features/Approvals/`).

A tool without an approval card goes into `--allowedTools` in `ClaudeBackend.BuildArguments`, and
its whole policy becomes the gateway's. Only `mcp__tg__send_file` is there: the host checks the
folder, the type and the size itself. Nothing else belongs there — it bypasses the cards.

The `send_file` type whitelist lives in several places — the map in `OperatorConsole`, the tool
description in `ClaudeSendFileTool` (the CLI sees the description of the **running** gateway),
`docs/cli-contract.md` and `docs/safety.md` with their twins in `docs/en/`, and the «Long documents»
rule of `CLAUDE.md`. Change a type — change all of them.

## A monitor endpoint

`api.MapGet` in `MonitorModule.MapEndpoints`, read-only — `monitor.md`.

## An exe command

A class in `Infrastructure/Cli/` (`protect-secrets` lives in `Security/`, next to `SecretsProtector`)
with a `public const string Name` and `Run(string[] args, TextWriter output)` — plus
`IReadOnlyList<IChatChannelModule> channels` before `output` if the command needs channel keys (as
`install` and `protect-secrets` do). A line in the `switch` of `ConsoleCommands.TryRun` and in its
`PrintUsage`.

Commands run before the host is built: DI and the channel are unavailable, the answer goes to
`output` only, the exit code is 0 or 1. Taken: `protect-secrets`, `install`, `uninstall`, `help`.
An argument starting with a dash or a slash is configuration, not a command.

## An autostart mechanism (launchd, systemd)

An `IAutostartInstaller` implementation (deriving from `ProcessAutostartInstaller`, whose `Run` is
protected) in `Infrastructure/Autostart/` and a line in
`AutostartInstaller.ForCurrentOs` (today only Windows; any other OS gets
`UnsupportedAutostartInstaller`). Drop a descriptor file and call the platform utility through
`ProcessAutostartInstaller.Run` — decide by the exit code, the output is localized. The XML for
`schtasks` is UTF-16: in UTF-8 the Cyrillic in the task description turns into mojibake.

## A config key

A property in `GatewayOptions` (+ `Validate`), a default in `appsettings.json`, a `"//Ключ": "…"`
sample in `appsettings.Local.example.json`, a line in the README's «Основные настройки».

An agent key goes into `ClaudeOptions` and `Gateway:Claude`, a channel key into its `*Options` and
`Gateway:Channel:Settings`. The exception is `DataDirectory`: it is needed before the config (that
folder holds `appsettings.Local.json`), so `AppPaths.UseConfiguredDirectory` reads it from the
`appsettings.json` next to the exe and from the environment.

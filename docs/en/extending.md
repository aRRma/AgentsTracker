# Adding to the gateway

When to read this: you are adding a chat command, a settings screen, a feature, an agent, a
channel, an MCP tool, a monitor endpoint, an exe command, an autostart mechanism or a config key.
The project layout and the rules between projects are in `architecture.md`.

## A chat command

A class implementing `IChatCommandHandler` in the feature folder, `AddSingleton` in its `*Module`,
an entry in the `HelpCommandHandler` text and, if the command belongs in the «Меню» list below, in
`ChatCommandCatalog` (`Infrastructure/Chat/`, the channel's command hints).

Taken names: the `Commands` of each `IChatCommandHandler`. **A duplicate across two features kills
startup** (`ChatDispatcher` builds a dictionary of commands).

The «Меню» list (`ChatCommandCatalog`) is short on purpose: `/new /model /effort /mode` work as text
but stay out of it, buttons on «Сессии» and «Агент» do the same. `RootScreen` has buttons for the
screens only. Where a command picked from «Меню» goes with topics on — `architecture.md`, "Topics".

«Контекст» has no `/context` command: an unknown slash command goes to the CLI, and it would shadow
Claude Code's own.

## A settings screen

A class implementing `ISettingsScreen` in `Features/Settings/Screens/`, `AddSingleton<ISettingsScreen, …>`
in `SettingsModule`, a button in `RootScreen`. A screen of another feature lives in that feature and
is registered by its module the same way — `TopicScreen` in `Topics`.

- `Key` is the screen's callback prefix: a button carries `cfg:<Key>:<argument>`. It must be unique,
  and an unknown key silently opens `root`, so a mismatch looks like a button that does nothing.
- `RenderAsync` is async because of the limits; `RenderFramesAsync` returns optional frames.
  `StatusScreen` "fills" the `LimitBars` gauges with them: a few frames
  `LimitBars.FrameDelay` apart (more often gets a 429 from Telegram). A frame identical to the previous one is skipped with its pause: the
  bar of a barely used window does not move, and the edit would only earn «message is not modified».
- Model, Effort and mode are one `AgentScreen` with the arguments `model:…`/`effort:…`/`mode:…`.
- `Apply` receives the `ChatId`: a screen can enqueue work on `ChatWorker` (`SkillsScreen`), and
  the answer goes to whoever pressed the button. A changed setting is written to the journal with
  `IAuditLog.Changed` (`SettingsAudit`).
- Every screen renders for a chat (`RenderAsync(ChatId)`, `Open(ChatId)`) and reads the selection
  through `store.For(chat)`: a topic has its own project, session, model, effort and mode.
- A new topic has no type yet (`IsNewTopic`). A model, effort or mode change there keeps it new
  (`TopicState.Untyped`); only `SetProjectPath` types it. Its text opens «Тип темы» (`TopicHandler`),
  and a skill there is refused (`SkillLauncher`: «Сначала выберите тип темы: /topic»). A new command
  that starts a task needs the same refusal.
- The list position lives in `ScreenNavigation` per `ChatId`: screens are singletons, and each topic
  has its own `ChatId`.
- An irreversible button asks first through `PendingConfirmations` (per `ChatId`: action and session);
  «Да» carries the argument `yes:<action>`, and `Open` drops the pending question. Example —
  `ContextScreen`.
- Pages and callback_data keys — `SettingsKeyboard.Page`/`Key12`. The one-letter prefix of a
  screen argument must not be a hex character, or it is confused with a key. `TelegramChannel.Markup`
  checks the callback_data length (64 bytes) and names the button — a screen bug, not a transport one.

## A feature

A folder in `Features/` with a `*Module : IFeatureModule` (`Infrastructure/Modules/`: `AddServices`
and `MapEndpoints`, the latter empty if there is nothing to mount) and a line in the module list in
`Program.cs`. Text handlers (`IChatTextHandler`) are polled in that order, so `ChatModule` stays
last: its handler always returns `true`. `TopicsModule` goes right before it, after `SettingsModule`
and `QuestionModule`: a skill's arguments and a вопрос after a bare `/ask` are answers, not a new
topic's text. Buttons are `IChatButtonHandler`, told apart by a prefix of
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
(`architecture.md`): the backend carries the id to its confirmation channel itself.

`AgentCapabilities.AskingPermissionMode` is the mode in which the agent asks about everything the user's rules do not allow; an
agent with no such mode leaves it `null` and has no «Работа» topic (startup refuses a non-empty
`Gateway:Work:AllowedUserIds`).

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
methods — the answer to a вопрос is a reply. A reply to a deleted message must still be delivered.
A messenger without replies may ignore `ReplyTo`.

Take the transport client **lazily**: the host creates the channel before it prints configuration
errors, and a constructor failing on an empty token would hide a readable error behind a stack trace.

`grep -rn Telegram src/AgentsTracker.Gateway --include=*.cs` must only find `Program.cs`, comments
and user-facing texts.

## An MCP tool

A class with `[McpServerToolType]` in `Agents.Claude/Mcp/`, the name as a constant in
`McpConfigFile`, `.WithTools<…>()` in `ClaudeAgentModule`, and the method the host needs — in
`IOperatorConsole` (implemented in `OperatorConsole`, `Features/Approvals/`).

A tool without an approval card goes into `--allowedTools` in `ClaudeBackend.BuildArguments`, and
its whole policy becomes the gateway's. Only `mcp__tg__send_file` and the web tools of a
вопрос/беседа/«Работа» are there (`cli-contract.md`); nothing else belongs there — it bypasses the
cards.

The `send_file` type whitelist lives in `OperatorConsole`; the tool description in
`ClaudeSendFileTool` (the CLI sees the **running** gateway's) and `cli-contract.md`, "File to chat",
repeat it — change them together.

## A monitor endpoint

`api.MapGet` in `MonitorModule.MapEndpoints` with `Results.Json(..., Json)`, plus a section in
`index.html`; read-only — `monitor.md`.

## An exe command

A class in `Infrastructure/Cli/` (`protect-secrets` lives in `Security/`, next to `SecretsProtector`)
with a `public const string Name` and `Run(string[] args, TextWriter output)` — plus
`IReadOnlyList<IChatChannelModule> channels` before `output` if the command needs channel keys (as
`install` and `protect-secrets` do). A line in the `switch` of `ConsoleCommands.TryRun` and in its
`PrintUsage`.

Commands run before the host is built: DI and the channel are unavailable, the answer goes to
`output` only, the exit code is 0 or 1. The taken names are in `ConsoleCommands.TryRun`.
An argument starting with a dash or a slash is configuration, not a command.

## An autostart mechanism (launchd, systemd)

An `IAutostartInstaller` implementation (deriving from `ProcessAutostartInstaller`, whose `Run` is
protected) in `Infrastructure/Autostart/` and a line in
`AutostartInstaller.ForCurrentOs` (Windows only; any other OS gets
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

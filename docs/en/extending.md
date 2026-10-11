# Adding to the gateway

When to read this: you are adding a chat command, a settings screen, a feature, an agent, a
channel, an MCP tool, a monitor endpoint, an exe command, an autostart mechanism or a config key.
Every entry is a checklist — a step missed here shows up as a silent startup failure or as a
feature nobody can reach. The layout of the projects and the rules between them are in
`architecture.md`.

## A chat command

A class implementing `IChatCommandHandler` in the feature folder, `AddSingleton` in its `*Module`,
an entry in `ChatCommandCatalog` (`Infrastructure/Chat/`, the channel's command hints) and in the
`HelpCommandHandler` text.

Taken: `/start /help` (Help), `/new /stop` (Chat), `/ask` (Question), `/rules` (Approvals),
`/audit` (Audit), `/menu /settings /status /sessions /agent /model /effort /mode /skills /project
/usage` (Settings). **A duplicate across two features kills startup** (`ChatDispatcher` builds a
dictionary of commands).

The «Меню» list holds `/menu`, the screens, `/ask`, `/rules`, `/audit` and `/help` — `/new /stop
/model /effort /mode` work as text but are not in the list: the same thing is available as buttons
on the «Сессии» and «Агент» screens, and a long list reads worse than a short one. `/ask` is there
because the question is typed right after the command. The order is by frequency: what every
session needs first, settings next, rules and logs last. `RootScreen` has buttons for the screens
only (none for `/rules` and `/audit`), with «Контекст» next to «Вопрос».

Every other slash command goes to the CLI as-is: unknown slash commands are Claude Code's own. That
is why the «Контекст» screen has no `/context` command of its own: it would shadow Claude Code's.

## A settings screen

A class implementing `ISettingsScreen` in `Features/Settings/Screens/`, `AddSingleton<ISettingsScreen, …>`
in `SettingsModule`, a button in `RootScreen`.

- `Key` is the screen's callback prefix: a button carries `cfg:<Key>:<argument>`. It must be unique,
  and an unknown key silently opens `root`, so a mismatched key looks like a button that does
  nothing.
- `RenderAsync` is async because of the limits; `RenderFramesAsync` returns optional frames.
  `StatusScreen` "fills" the `LimitBars` gauges with them: three frames 350 ms apart, more often
  gets a 429 from Telegram. A frame identical to the previous one is skipped along with the pause
  before it — the bar of a barely used window does not move, and the edit would only earn a
  «message is not modified» after a wait.
- Model, effort and mode are one `AgentScreen` with the arguments `model:…`/`effort:…`/`mode:…`.
- `Apply` receives the `ChatId`: a screen can enqueue work on `ChatWorker` (`SkillsScreen`), and
  the answer goes to whoever pressed the button. A changed setting is written to the journal with
  `IAuditLog.Changed` (`SettingsAudit`).
- The list position lives in `ScreenNavigation` per user: screens are singletons, there can be
  several users.
- An irreversible button (lose the context, forget the list) asks first: `PendingConfirmations`
  remembers the action and the session per user, the «Да» button carries `yes:<action>`, and
  `Open` drops the pending question. `ContextScreen` is the example.
- Pages and callback_data keys — `SettingsKeyboard.Page`/`Key12`. The one-letter prefix of a
  screen argument must not be a hex character, or it will be confused with a key. The
  callback_data length (64 bytes) is checked by `TelegramChannel.Markup`, which names the button
  in the error — that is a screen bug, not a transport one.

## A feature

A folder in `Features/` with a `*Module : IFeatureModule` (`Infrastructure/Modules/`: `AddServices`
and `MapEndpoints`, the latter empty if there is nothing to mount) and a line in the module list in
`Program.cs`. Text handlers (`IChatTextHandler`) are polled in that order, so `ChatModule` stays
last: its handler always returns `true`. Buttons are `IChatButtonHandler`, told apart by a prefix of
the callback data (`cfg:` — the menu).

## A project

A new project under `src/` needs, besides its own code: a `<Project>` line in `AgentsTracker.slnx`,
a `ProjectReference` in `Gateway.csproj` and a `COPY …/*.csproj` line in the `Dockerfile` (without
it restore silently skips the project and `publish --no-restore` fails).

There is no `Directory.Build.props` and no `.editorconfig`: every csproj repeats
`TargetFramework`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors` and `RootNamespace`
itself. Copy that block from a neighbouring project — `dotnet new` gives a project without
warnings-as-errors and with a root namespace of its own, and that only shows up later, as a
warning nobody notices.

## An agent (Codex, Cursor)

A project `src/AgentsTracker.Agents.<Name>` referencing `Agents.Abstractions` (and the three steps
of «A project»): an `IAgentBackendModule` registers `IAgentBackend`, `IAgentLimits` (or
`NoAgentLimits`), `IAgentSkillCatalog` (or `NoAgentSkills`) and its own confirmation channel through
`IOperatorConsole`. A line in the `agents` list in `Program.cs`.

Every call into `IOperatorConsole` carries `AgentRunRequest.RunId` back: a task and a вопрос run in
parallel, and the host finds the chat by that id — a call without it is refused. The backend has
to carry the id from the request to its confirmation channel itself (Claude puts it into a header
of the run's own MCP config).

`IAgentBackend.InspectProject` lists everything the agent would run from the project folder on its
own — hooks, MCP servers, pre-approved rules, files that carry them — with a fingerprint that
changes with any of those files; `null` only when there is nothing of the kind. A backend that
returns `null` while its CLI reads such files silently brings back the hole the «Доверять
проекту?» card closes.

`grep -rn Claude src/AgentsTracker.Gateway --include=*.cs` must only find `Program.cs` and
comments. Agent settings live in `Gateway:<Id>`, the host does not read them.

## A chat channel (Slack, Discord)

A project `src/AgentsTracker.Channels.<Name>` referencing `Channels.Abstractions` (and the three
steps of «A project»): an `IChatChannelModule` registers `IChatChannel` and everything of its own,
reads settings from `Gateway:Channel:Settings` and declares in `SecretKeys` what `protect-secrets`
encrypts. Its own `ChatId`/`UserId` types (`Key` is "channel:value"), its own `ChannelLimits`,
transport failures — only `ChannelRequestException`; a rejected bot key must come out as
`ChannelFailure.Unauthorized`, or the host takes it for a network failure and retries
`ConnectAsync` forever instead of exiting. A line in the `channels` list in `Program.cs` (selected
by `Gateway:Channel:Type`).

Replies: fill `IncomingMessage.Message` and `InReplyTo` (the message the user replied to — that is
how free text finds its card when several wait), honour `OutgoingMessage.ReplyTo` and the `replyTo` of the
file methods — the answer to a вопрос is a reply, otherwise it gets lost among the task's messages.
A reply to a message that no longer exists must still be delivered (Telegram:
`AllowSendingWithoutReply`). A messenger without replies may ignore `ReplyTo`.

`grep -rn Telegram src/AgentsTracker.Gateway --include=*.cs` must only find `Program.cs`, comments
and a few message texts (help, `install`).

The channel takes its transport client **lazily**: the host creates the channel before it prints
configuration errors, and a constructor failing on an empty token would replace a readable error
with a stack trace.

## An MCP tool

A class with `[McpServerToolType]` in `Agents.Claude/Mcp/`, the name as a constant in
`McpConfigFile`, `.WithTools<…>()` in `ClaudeAgentModule`, and the method the host needs — in
`IOperatorConsole` (implemented in `OperatorConsole`, `Features/Approvals/`).

If the tool must work without an approval card, add it to `--allowedTools` in
`ClaudeBackend.BuildArguments` — but then its whole policy is the gateway's own responsibility.
That is the case for `mcp__tg__send_file`: the host checks the folder, the type and the size
itself, and an "allow?" card would only duplicate that check with an extra button press. Nothing
else belongs in `--allowedTools`: it bypasses the cards past the machine's config.

The `send_file` file-type whitelist lives in five places at once — the map in `OperatorConsole`, the
tool description in `ClaudeSendFileTool` (the CLI sees the description of the **running** gateway),
`README.md`, `docs/cli-contract.md` and `docs/en/cli-contract.md`. Change a type — change all five.

## A monitor endpoint

`api.MapGet` in `MonitorModule.MapEndpoints`, read-only — `monitor.md`.

## An exe command

A class in `Infrastructure/Cli/` (`protect-secrets` lives in `Security/`, next to
`SecretsProtector`) with a `public const string Name` and `Run(string[] args, TextWriter output)`
— plus `IReadOnlyList<IChatChannelModule> channels` before `output` if the command needs channel
keys (as `install` and `protect-secrets` do). A line in the `switch` of `ConsoleCommands.TryRun`
and in its `PrintUsage`.

Commands run before the host is built: DI and the channel are unavailable to them, the answer goes
to `output` only, the exit code is 0 or 1. Taken: `protect-secrets`, `install`, `uninstall`,
`help`. Anything starting with a dash or a slash is not a command — those are configuration arguments.

## An autostart mechanism (launchd, systemd)

An `IAutostartInstaller` implementation in `Infrastructure/Autostart/` and a line in
`AutostartInstaller.ForCurrentOs` (today only Windows is there; any other OS gets
`UnsupportedAutostartInstaller`). The approach is the same on every OS: drop a descriptor file
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

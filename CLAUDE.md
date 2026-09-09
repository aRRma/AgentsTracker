# CLAUDE.md

Notes for Claude Code working in this repository. Details are in `docs/en/`:

- `docs/en/operations.md` — restarting the gateway, scratch instance, worktree, tooling.
- `docs/en/deployment.md` — production run: publish, install/uninstall, Docker, ports, secrets.
- `docs/en/cli-contract.md` — the CLI contract: approvals, stream-json, sessions, limits, skills.
- `docs/en/monitor.md` — web monitor: endpoints, mock, styling, accessibility.
- `docs/en/claude-permissions.md` — Claude Code permissions on this machine: `allow`/`ask`/`deny`,
  the `docs/examples/claude-settings.example.json` sample, the project-level `.claude/settings.json`.

Documentation lives in two languages: `docs/en/` is the English original for the agent (linked from
here), `docs/*.md` is the Russian mirror the GitHub wiki is generated from. `README.md` is always
Russian. Editing one language and not the other is a bug — see «Как работать» below.

## What this is

A bridge between Telegram and Claude Code. A single .NET 10 app on an always-on PC: a chat message →
`claude -p` in the project folder → "may I?" questions as buttons in Telegram → the agent's answer
back into the chat. Developed on Windows; runs on Linux in a container, there is no separate
installer for macOS and Linux yet.

Code, comments, logs and chat texts are in Russian.

## Commands

```powershell
dotnet build                                    # TreatWarningsAsErrors is on
dotnet build src\AgentsTracker.Gateway -o $env:TEMP\at-build   # check the build while the gateway holds bin\Debug
dotnet run --project src\AgentsTracker.Gateway  # needs appsettings.Local.json (next to it or in the data directory)
dotnet run --project src\AgentsTracker.Gateway -- protect-secrets   # encrypt channel and Proxy secrets, move the config to %LOCALAPPDATA%
pwsh -File scripts\migrate-channel-settings.ps1 # one-off move of BotToken/AllowedUserIds into Gateway:Channel:Settings
dotnet publish src\AgentsTracker.Gateway -c Release -o C:\Apps\AgentsTracker   # install: publish (the folder is an example)…
C:\Apps\AgentsTracker\AgentsTracker.Gateway.exe install --start                # …and autostart; remove with uninstall
docker compose up -d --build                    # the same gateway in a container, needs .env
```

There are no tests. Anything touching the CLI contract is checked by hand: start the gateway and
watch the log. Host logic that needs no live bot is checked by a file-based C# harness with fakes
instead of a channel — `docs/en/operations.md`.

**The working gateway on this machine is the published release** (`C:\AgentsTracker-<версия>-win-x64`;
the path is shown by `Get-Process AgentsTracker.Gateway`). Do not stop it and do not rebuild over it:
that is the stable service the user chats with, and new code reaches it only through a new release. A
change is checked live by a **scratch instance from `bin\Debug` on its own ports** — a separate build
folder, `Gateway__McpPort`/`Gateway__MonitorPort` other than `5099`/`5100`, a fake bot token and its
own `Gateway__DataDirectory`; the recipe and the cleanup are in `docs/en/operations.md`. When the
gateway does run from `bin\Debug` of the main folder (`AgentsTracker`, branch `master`; there is no
`main` branch), `dotnet build` fails with `MSB3021` while it runs and switching branches swaps the
sources out from under the process. From a session started from Telegram you must not restart it —
hand the commands to the user instead.

## How to add

**A chat command.** A class implementing `IChatCommandHandler` in the feature folder, `AddSingleton`
in its `*Module`, an entry in `ChatCommandCatalog` (the channel's command hints) and in the
`HelpCommandHandler` text. Taken: `/start /help` (Help), `/new /stop` (Chat), `/rules` (Approvals),
`/audit` (Audit), `/menu /settings /status /sessions /agent /model /effort /mode /skills /project
/usage` (Settings). A duplicate across two features kills startup. The «Меню» screen holds screens
only — `/new /stop /model /effort /mode` work as text but are not in the list. The command list and
the help share one order: status and sessions, agent and skills, repository, statistics, rules,
logs. `RootScreen` repeats it up to and including statistics: there are no menu buttons for `/rules`
and `/audit`. Every other slash command goes to the CLI as-is.

**A settings screen.** A class implementing `ISettingsScreen` in `Features/Settings/Screens/`,
registration in `SettingsModule`, a button in `RootScreen`. `RenderAsync` is async because of the
limits; `RenderFramesAsync` returns optional frames (`StatusScreen` "fills" the `LimitBars` gauges:
three frames 350 ms apart, more often gets a 429 from Telegram). A frame identical to the previous
one is skipped along with the pause before it — the bar of a barely used window does not move, and
the edit would only earn a «message is not modified» after a wait. Model, effort and mode are one
`AgentScreen` with the arguments `model:…`/`effort:…`/`mode:…`. `Apply` receives the `ChatId`: a
screen can enqueue work on `ChatWorker` (`SkillsScreen`), the answer goes to whoever pressed the
button. The list position lives in `ScreenNavigation` per user: screens are singletons, there can be
several users. Pages and callback_data keys — `SettingsKeyboard.Page`/`Key12`; the one-letter prefix
of a screen argument must not be a hex character, or it will be confused with a key.

**A feature.** A folder in `Features/` with a `*Module : IFeatureModule` (`Infrastructure/Modules/`)
and a line in the module list in `Program.cs`; `ChatModule` stays last.

**An agent (Codex, Cursor).** A project `src/AgentsTracker.Agents.<Name>` referencing
`Agents.Abstractions`: an `IAgentBackendModule` registers `IAgentBackend`, `IAgentLimits` (or
`NoAgentLimits`), `IAgentSkillCatalog` (or `NoAgentSkills`) and its own approval channel through
`IOperatorConsole`. A line in the `agents` list in `Program.cs`, a reference in `Gateway.csproj` and
a `COPY` line in the `Dockerfile`.
`grep -rn Claude src/AgentsTracker.Gateway --include=*.cs` must only find `Program.cs` and comments.
Agent settings live in `Gateway:<Id>`, the host does not read them.

**A chat channel (Slack, Discord).** A project `src/AgentsTracker.Channels.<Name>` referencing
`Channels.Abstractions`: an `IChatChannelModule` registers `IChatChannel` and everything of its own,
reads settings from `Gateway:Channel:Settings` and declares in `SecretKeys` what `protect-secrets`
encrypts. Its own `ChatId`/`UserId` types (`Key` is "channel:value"), its own `ChannelLimits`,
transport failures — only `ChannelRequestException`. A line in the `channels` list in `Program.cs`
(selected by `Gateway:Channel:Type`), a reference in `Gateway.csproj` and a `COPY` line in the
`Dockerfile`.
`grep -rn Telegram src/AgentsTracker.Gateway --include=*.cs` must only find `Program.cs` and
comments. The channel takes its transport client **lazily**: the host creates the channel before it
prints configuration errors, and a constructor failing on an empty token would replace a readable
error with a stack trace.

**A project.** There is no `Directory.Build.props` and no `.editorconfig`: every csproj repeats
`TargetFramework`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors` and `RootNamespace` itself.
Copy that block from a neighbouring project — `dotnet new` gives a project without warnings-as-errors
and with a root namespace of its own, and that only shows up later, as a warning nobody notices.

**A monitor endpoint.** `api.MapGet` in `MonitorModule.MapEndpoints`, read-only —
`docs/en/monitor.md`.

**An exe command.** A class in `Infrastructure/Cli/` (`protect-secrets` lives in `Security/`, next to
`SecretsProtector`) with a `public const string Name` and `Run(string[] args, TextWriter output)` —
plus `IReadOnlyList<IChatChannelModule> channels` if the command needs channel keys (as `install` and
`protect-secrets` do). A line in the `switch` of `ConsoleCommands` and in its help. Commands run
before the host is built: DI and Telegram are unavailable to them, the answer goes to `output` only,
the exit code is 0 or 1. Taken: `protect-secrets`, `install`, `uninstall`, `help`. Anything starting
with a dash is not a command — those are configuration arguments.

**An autostart mechanism (launchd, systemd).** An `IAutostartInstaller` implementation in
`Infrastructure/Autostart/` and a line in `AutostartInstaller.ForCurrentOs`. The approach is the same
on every OS: drop a descriptor file and call the platform utility through
`ProcessAutostartInstaller.Run` — decide by the exit code, their output is localized. The XML for
`schtasks` is written in UTF-16: in UTF-8 the Cyrillic in the task description turns into mojibake.

**A config key.** A property in `GatewayOptions` (+ `Validate`), a default in `appsettings.json`, a
`"//Ключ": "…"` sample in `appsettings.Local.example.json`, a line in the README's «Основные
настройки». An agent key goes into `ClaudeOptions` and `Gateway:Claude`, a channel key into its
`*Options` and `Gateway:Channel:Settings`. The exception is `DataDirectory`: it is needed before the
config (that folder holds `appsettings.Local.json` itself), so it is read in
`AppPaths.UseConfiguredDirectory` from the `appsettings.json` next to the exe and from the
environment.

## Layout

```
src/AgentsTracker.Agents.Abstractions/   agent contracts, no Telegram and no specific CLI:
  IAgentBackend         Probe() (binary, version), RunAsync(AgentRunRequest, IAgentRunObserver)
  AgentRun.cs           request (prompt, folder, session, model, effort, mode, timeout), observer, result
  AgentCapabilities     which models/efforts/modes the agent supports (effort null — not supported)
  IOperatorConsole      what the agent asks a human for: ApproveAsync, AskAsync, SendFileAsync; PersistentRule — an "always" rule
  IAgentLimits          plan limits; IAgentSkillCatalog — slash commands
  IAgentBackendModule   AddServices + MapEndpoints; AgentHost — data directory, port, proxy, card timeout from the host
src/AgentsTracker.Agents.Claude/         Claude Code behind those contracts:
  ClaudeBackend         the claude -p process: arguments, stream-json, "session not found", limit
  ClaudeCapabilities    which models, efforts and modes the CLI accepts; Selectable — what is offered from the chat
  ClaudeLimits, ClaudeSkillCatalog, ClaudePluginRegistry, ClaudeCliLocator, ClaudeStreamEvent
  Mcp/                  McpConfigFile — mcp-gateway-<pid>.json with a token; ClaudePermissionTool — the CLI ↔ IOperatorConsole payload;
                        ClaudeSendFileTool — a file from the agent into the chat, the policy stays on the host side
src/AgentsTracker.Channels.Abstractions/ channel contracts, no specific messenger:
  IChatChannel          channel addresses and limits, ConnectAsync/ListenAsync, Send/Edit/Delete/Acknowledge,
                        SendDocumentAsync/SendPhotoAsync — a file and a picture as a stream
  ChannelLimits         channel bounds: message and caption length, document and photo size
  RateLimitRetry        one retry after a 429 with a short retry_after — shared by every send
  ChatId, UserId        an address as a value; Key is "channel:value" for state.json, the audit and the log
  Messages.cs           OutgoingMessage, Keyboard, MessageRef, IncomingMessage, ButtonPress, ChatCommand
  IChatInbound          where the channel hands incoming traffic; implemented by the host (ChatDispatcher)
  ChatHtml              the canonical text format (b, i, s, code, pre, a, blockquote) and escaping
  ChannelRequestException  the channel's only outward exception: RateLimited (retry_after), MarkupRejected, CannotReach
  IChatChannelModule    AddServices + MapEndpoints, SecretKeys; ChannelHost — the shared proxy from the host
src/AgentsTracker.Channels.Telegram/     Telegram behind those contracts:
  TelegramChannel       long polling, inline buttons, HTML; "message is not modified" and retry_after live here
  TelegramOptions, TelegramIds, TelegramClientFactory (client is lazy: the constructor validates the token)
src/AgentsTracker.Gateway/
  Program.cs            data directory, service commands, lists of agents (Gateway:Agent), channels (Gateway:Channel:Type) and features
  Domain/               pure models: GatewayState (state.json), AuditEvent
  Infrastructure/       Configuration (GatewayOptions, ProjectCatalog), State (SessionStore),
                        Chat (ChatGatewayService, ChatDispatcher, MarkdownRenderer, StartupNotice —
                        «запущен»/«прерван» after a restart, Dispatch/ — handler contracts),
                        Container.Detected — in a container no autostart is installed, the monitor listens on any,
                        Audit, Monitoring (RunMonitor, RingBufferLog), Security (protect-secrets included),
                        AppVersion — the build number for the log, the monitor and «Шлюз запущен»,
                        Cli/ (install, uninstall), Autostart/ (a Task Scheduler task via schtasks)
  Features/             vertical slices, each with its own *Module:
    Approvals/          approval cards, ApprovalBroker, /rules
    Chat/               ChatWorker (queue, launch, sessions), RunStatusMessage, /new /stop
    Settings/           SettingsMenuCoordinator + Screens/*, /menu /status /sessions /agent /skills /project /usage
    Help/ Audit/ Monitor/   /start /help; /audit; the web page (index.html — EmbeddedResource) and /api/*
```

Layering rules: `Domain` and `Abstractions` do not open files and do not go over the network;
`Gateway` knows about a specific agent and channel only in `Program.cs`; the backend and the channel
know nothing about each other or about `GatewayOptions`; `Infrastructure` knows nothing about
features (except the `Dispatch` contracts); a feature depends on a feature only through a public
service (`Settings` → `ChatWorker.IsBusy`).

### How a message travels

`ChatDispatcher` lets through only `IChatChannel.AllowedUsers` and private chats (`ChatKind.Unknown`
is a rejection too). Text is processed in order:

1. a slash command from `IChatCommandHandler.Commands` — **first of all**, otherwise `/stop` would go
   to a waiting free-form answer and there would be nothing left to interrupt a stuck run with;
2. the `IChatTextHandler` chain in module order: an answer to a card (`ApprovalTextHandler`) → skill
   arguments (`SkillArgumentsTextHandler`) → into the agent's queue (`ChatEnqueueTextHandler`, always
   `true`). That is why `ChatModule` is last. Unknown slash commands are Claude Code's own commands,
   they go to the CLI.

Buttons: the `IChatButtonHandler` chain (`Dispatch/`), each recognizing its own by the prefix in
`CanHandle`: `cfg:` — the menu (`SettingsCallbackHandler`), everything else (a hex request id) —
`ApprovalCallbackHandler` → `ApprovalBroker`.
The broker's `ActiveChat` is set by `ChatWorker` before a run, otherwise cards would go to another
user's chat.

### The gateway → CLI → gateway loop

The gateway both launches the agent and serves it:

```
channel ─▶ ChatDispatcher ──▶ ChatWorker ──▶ ClaudeBackend ──▶ claude.exe -p
                    ▲                                                     │ needs permission
                    └── ApprovalBroker ◀── OperatorConsole ◀── ClaudePermissionTool ◀── MCP http://127.0.0.1:<McpPort>/mcp
                                                                              Authorization: Bearer <token>
```

At startup `McpConfigFile` generates a token, writes `mcp-gateway-<pid>.json` and deletes it on
shutdown (the name carries the PID: a shared file was overwritten and deleted by a second instance,
and the working gateway then died on "mcp__tg__approve not found"; files of dead PIDs are swept at
startup); `/mcp` is mounted with the `McpConfigFile.Authorizes` filter; the CLI gets `--mcp-config`
and `--permission-prompt-tool mcp__tg__approve`. The server and tool names are `McpConfigFile`
constants, `[McpServerTool]` takes the same constant. The README is the user's manual; when changing
the endpoint protection or the data directory, check its «Безопасность» section.

The second tool of the same server is `mcp__tg__send_file` (`ClaudeSendFileTool`): the agent calls it
itself to send a file into the chat. Path → `IOperatorConsole.SendFileAsync` → `OperatorConsole`
checks the folder (the current project only, the data directory is forbidden, no symlink/junction on
the path), the type (an extension allowlist in code) and the size (`ChannelLimits`), writes
`file.send` into the audit and sends it through `ApprovalBroker.SendFileAsync` →
`IChatChannel.SendDocumentAsync`/`SendPhotoAsync`. The tool is passed in `--allowedTools`: an
"allow?" card would duplicate the gateway's own check with an extra button press. A refusal is always
a `{"sent":false,"reason":…}` answer, never an exception.

**An MCP tool.** A class with `[McpServerToolType]` in `Agents.Claude/Mcp/`, the name as a constant in
`McpConfigFile`, `.WithTools<…>()` in `ClaudeAgentModule`, the method the host needs — in
`IOperatorConsole` (implemented in `OperatorConsole`). If the tool must work without a card — into
`--allowedTools` in `ClaudeBackend.BuildArguments`.

The approval contract was verified against a live CLI 2.1.x and is undocumented —
`docs/en/cli-contract.md`. The essentials: an `allow` answer requires `updatedInput`; the «Всегда»
button either hands a rule to the CLI (`permission_suggestions`) or the gateway keeps the signature
in `state.json` for its own project only; input truncated before the card is sent as a file.
`ApprovalTimeoutMinutes` reaches `AgentHost` and from there the MCP server's `timeout` in the config:
without it the CLI aborted the call after 5 minutes of silence, and the card was dead afterwards.

### `--permission-mode` is always passed

Without the flag `permissions.defaultMode` from `~/.claude/settings.json` applies — the user has
`auto` there, and no buttons show up in the chat. Do not remove the argument from
`ClaudeBackend.BuildArguments`. Only `PermissionMode.Selectable` (`plan`/`default`/`acceptEdits`/
`auto`) can be switched from the chat; `dontAsk` and `bypassPermissions` are excluded deliberately —
turning approvals off entirely stays a config edit on the machine. The values are checked by
`ValidateFor(capabilities)` in `ValidateStartup`, not by `GatewayOptions.Validate` (the agent is not
chosen yet).

### Sessions and chat-side selection

Sessions are keyed by the **normalized project path** (`ProjectCatalog.Normalize`); the id of a new
one is issued by the gateway (`--session-id`) and is reset only on `SessionLost` from the CLI —
details in `docs/en/cli-contract.md`.

The chat-side selection (`SessionStore`) sits on top of the config: `EffectiveModel`,
`EffectivePermissionMode`, `EffectiveEffort`, `ProjectPath`. A value equal to the config is stored as
`null`: otherwise a config edit would be silently overridden by an old selection.

`ProjectCatalog`: the `Gateway:Projects` list, otherwise a walk of `Gateway:ProjectsRoot` down to
`ProjectsRootDepth`, otherwise the neighbours of `ProjectPath`. `ProjectScreen` selects in two steps
(folder → repository) in pages of 12. The current project comes first in its group, and after a
selection the page resets to the first one — otherwise the `▶` marker could end up off-screen.

### State, secrets, configuration layers

`%LOCALAPPDATA%\AgentsTracker\` (`AppPaths.DataDirectory`, ACL — the owner and SYSTEM):
`state.json` (atomic), `mcp-gateway-<pid>.json`, `appsettings.Local.json` with the secrets,
`audit\audit-ГГГГ-ММ.jsonl`.

The config comes in layers: `appsettings.json` → `appsettings.Local.json` next to the exe (IDE) → the
same file in the data directory (production) → environment variables. `dpapi:…` is decrypted on load
(`ProtectedJsonConfigurationProvider`); `protect-secrets` encrypts `Gateway:Proxy` and the
`IChatChannelModule.SecretKeys` keys in `Gateway:Channel:Settings` (for Telegram — `BotToken`,
`Proxy`). `publish\` contains no secrets. `Gateway__*` variables are invisible to the child `claude`
— the host strips them from the environment in `ValidateStartup`.

### Audit

`IAuditLog.Write(AuditEvent.Now(kind, summary, user, chat, project, session, outcome))` — "who,
where, what"; addresses are stored as `UserKey`/`ChatKey` strings ("channel:value"), not as numbers:
ids from different channels collide. There is also `NowByKeys` — for writing by ready-made keys from
`state.json`. No secrets and no full texts (≤200 characters; the user's text is a `Text.Preview`
excerpt, 80 characters: a token could have been pasted there). The kinds are in `AuditKinds`:
`access.rejected`, `message`, `run.start`/`run.end`, `approval`, `question`, `file.send`, `settings`,
`rules`, `session.reset`, `limit.refused`, `gateway`. Menu screens write through
`SettingsAudit.Changed`. This is not a replacement for `ILogger`: the audit gets what a human is
answerable for, the log gets what is needed for debugging.

### Limits and money

The only limiter is `IAgentLimits` (the plan windows), checked in `ChatWorker.ProcessAsync` before a
run; if the poll fails, the run is **skipped**. There is no money in the gateway: `total_cost_usd` is
not read, `--max-budget-usd` is not passed, the statistics show only turns, tokens and time. Do not
return dollar estimates. Credits ("extra usage") are forbidden to the agent. Details —
`docs/en/cli-contract.md`.

### Chat output

`MarkdownRenderer` converts markdown into `ChatHtml` and cuts it to `IChatChannel.Limits`
(`MessageLength`, 3800 for Telegram) — cut the **source markdown before the conversion**, otherwise
tags get torn apart. Lists use `•`/`◦`, a table is an aligned `<pre>`. Italics only for a `*` tight
against the content on a word boundary (otherwise `*.cs` and `2 * 3` went italic); `_` is not parsed
(`snake_case`). A code block longer than the limit is sent as a file. Escaping within the budget —
`ChatHtml.EscapeCapped` (text that fits whole does not spend a character on the ellipsis). If the
channel answered with any 400 (not just "can't parse": an unsupported tag, "too long" after
escaping), it retries the send itself without markup (`ChatHtml.StripTags`) — otherwise part of the
answer would vanish silently. On a 429 in the middle of a multi-part answer `ChatWorker.SendPartAsync`
waits `RetryAfter` (≤30 s) and repeats the same part; the same goes for the agent's file in
`ApprovalBroker` — both through `RateLimitRetry.OnceAsync` (Channels.Abstractions), one shared
ceiling. The callback_data length (64 bytes) is checked by `TelegramChannel.Markup`, which names the
button — that is a screen bug, not a transport one. Sums, tokens and time — `DisplayFormat`.

## Keep in mind

- The gateway **does not attach** to a VS Code session — it is a parallel session on the same folder:
  shared `CLAUDE.md`, settings, hooks and MCP, but its own history.
- `--bare` is not allowed: it does not read `~/.claude` and breaks the subscription OAuth login.
- `--allowedTools` holds only `mcp__tg__send_file`: its policy belongs to the gateway. Do not add
  other tools there — that bypasses the cards past the machine's config.
- Barriers: the channel's `AllowedUsers` plus private chats only; MCP is always `127.0.0.1` plus a
  token in the header; the monitor has no token — that is why it is read-only, and `MonitorBind: any`
  (needed in a container) is published only on the host's `127.0.0.1`.
- CLI arguments go through `ProcessStartInfo.ArgumentList`, do not concatenate a string.
- `HttpClient` only through `IHttpClientFactory`. `ClaudeLimits.HttpClientName` — one timeout, no
  retries. The Telegram client (`TelegramClientFactory`) is a singleton, DNS is refreshed by
  `PooledConnectionLifetime`; retry only on `HttpRequestException`: Bot API methods are POSTs without
  idempotency (a retry after a 5xx is a duplicate in the chat), and a 429 is waited out by the caller
  per `retry_after`. Do not set `BaseAddress`. `RemoveAllLoggers()`: the token is part of the path.
- `Channel.CreateUnbounded` in `ChatWorker` without `SingleReader`: with it `Reader.Count` throws and
  `/status` falls over.
- `_runCts` in `ChatWorker` is set right before `agent.RunAsync`, after the status message is sent:
  only the run's `finally` clears it, and a failure above would leave `IsBusy` set until a restart.
- Moved channel keys live in one list, `ChannelOptions.MovedKeys`: both the startup check and
  `protect-secrets` read it. Test on a copy of the old config: `protect-secrets <path>` must return 1
  with a hint, and `migrate-channel-settings.ps1 -Path <path>` must not touch what is already filled
  in under `Channel:Settings`.
- `McpConfigFile` and `SessionStore` are the only ones with a classic constructor: the side effect (a
  file) must happen exactly once before startup.
- A release is a `v*` tag push, after which `.github/workflows/release.yml` builds the archives and
  creates the release itself. Release notes go into `docs/release-notes/<tag>.md` **before** the tag
  is pushed, otherwise a list of commits ends up in the release. Each archive holds a folder named
  after the archive plus `install.txt` next to it (source — `docs/install-quickstart.txt`). The
  version comes from the tag through `-p:Version`; the csproj keeps `0.0.0-dev`, so a build from
  source never looks like a released one. Details — `docs/en/deployment.md`.
- The GitHub wiki mirrors `README.md` and the Russian `docs/*.md`, it is never edited by hand: the
  pages are built by `scripts\sync-wiki.ps1` (the `wiki.yml` workflow on a push to `master`). A new
  file in `docs/` gets there only if it is added to the script's `$pages` table —
  `docs/en/operations.md`.
- Edit Russian texts and Windows paths with Edit/Write, not with a heredoc from Bash. Sources are
  UTF-8 **without a BOM**. The rest about tooling — `docs/en/operations.md`.
- Comments explain which failure the code prevents, not what it does.
- From a session over Telegram `AskUserQuestion` and `ExitPlanMode` wait `ApprovalTimeoutMinutes`
  (15 min): with no answer, take the recommended option.
- The monitor at `http://127.0.0.1:5100` is unreachable from a session via `curl`/`Invoke-RestMethod`
  — they are in `deny` for any address, do not try to work around it. A snapshot and the other
  `/api/*` — `pwsh -File scripts\monitor-api.ps1 /api/snapshot`.
- A monitor screenshot — Playwright MCP: `browser_navigate` to `http://127.0.0.1:5100`,
  `browser_take_screenshot` with a `filename` (the file lands in the project root), then
  `mcp__tg__send_file` and delete the file — the repository does not need it. Port `5100` is the
  **release** instance: it shows old code. A screenshot of your own change comes from the test
  instance's port, and `browser_take_screenshot` with a `target` selector (`.gauges`) gives a panel
  instead of the whole page.

## Как работать

Rules from the user. They used to live in memory, here they are safer — the file is read by every
session.

- **Answers stay short and simple.** The result first, then only what is needed to act. No retelling
  of the steps or the plan, jargon in plain words. Errors, risks and "what's next" are never cut.
- **Long documents go into an `.md` file.** A plan, a review report, an analysis, a comparison —
  anything longer than a couple of paragraphs: temporary in the scratchpad, permanent in `docs/`
  (only if it really is documentation). A Latin, kebab-case name. The answer carries the gist and the
  link; from Telegram — the file into the chat through `mcp__tg__send_file` (from the project folder
  only: `.md .txt .json .cs .js .html`, screenshots `.png .jpg .jpeg`).
  From plan mode the file is saved before `ExitPlanMode` as well.
- **Subagents only with `model: "sonnet"`.** In every `Agent` call and in a Workflow `agent()`. Do not
  inherit the parent's model, do not take opus/fable — it saves the plan limits.
- **The environment is Windows, Russian locale, Moscow (UTC+3).** Console output can arrive as
  mojibake — set UTF-8 (`chcp 65001`, `[Console]::OutputEncoding`). Windows and .NET messages are in
  Russian — do not search them by their English text. In code, parse and format through
  `CultureInfo.InvariantCulture`. "Today" is by Moscow time.
- **Commit on your own, in logical stages.** Do not wait to be asked; group files by the meaning of
  the change and commit them one batch at a time. The subject is in Russian, one line, no body —
  including no `Co-Authored-By` trailer, which the harness adds by itself.
- **A task tag in the commits.** While a task is being carried in a separate branch or worktree, each
  of its commits starts with a short tag: `<tag> message`. The tag is arbitrary, as long as the
  history shows which commits belong to the task.
- **Big tasks go into a worktree.** Several phases or gateway restarts along the way — start with
  `git worktree add ..\AgentsTracker-<task> -b <branch>` and work there; from Telegram switch
  `/project` to the new folder. Something small in one or two commits goes straight into the main
  folder. After the branch is finally merged into `master`, remove the worktree folder
  (`git worktree remove ..\AgentsTracker-<task>`) and the branch too.
- **A review in a worktree needs the branch name.** `/code-review` without an argument takes the
  uncommitted diff of the main folder, not the worktree branch: on 07.09.2026 it reviewed and fixed
  another session's changes in `master` that way. Call `code-review medium --fix <branch>`.
- **After changes — the documentation.** Check CLAUDE.md, the README and the config comments: new
  flags and commands, changes to the CLI contract, non-obvious decisions and their reasons. Do not
  add what is obvious from the code, or a history of the edits.
- **Documentation is edited in both languages.** `docs/en/*.md` is the English original the agent
  reads (and the only thing CLAUDE.md links to), `docs/*.md` is the Russian mirror the GitHub wiki is
  built from. A change to one file goes into its twin in the same commit — the two must not drift
  apart. A new document is added in both languages at once, plus a `$pages` line in
  `scripts\sync-wiki.ps1` for the Russian one. CLAUDE.md is English only, the README is Russian only.
- **After a commit — restart the gateway**, if the working instance runs from `bin\Debug`: otherwise
  the chat keeps the old code. While it is the release, a commit changes nothing in the chat — say so
  and check the change on a scratch instance instead of asking for a restart. The commands and the path
  through a clean worktree — `docs/en/operations.md`, "Restarting the gateway". From a session over
  Telegram you must not restart it yourself: give the user the commands and ask them to run them by
  hand; a scratch instance on its own ports is fine, it does not touch the working one. From VS Code you
  may, but **before** Stop-Process check `git status`: someone else's uncommitted changes have broken
  the build right after the process was stopped.
- **Terminology.** Checking by a live run of a scratch instance is «смоук-тест», not «дымовой прогон»
  and not «дымовая проверка»: in the chat and in the docs alike. The instance itself is a "scratch
  instance" in English and «пробный экземпляр» in Russian — those are the section titles in
  `operations.md`, and a third name for it only hides the section from search.

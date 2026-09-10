# How the gateway is built

When to read this: you need to find where something lives, or to understand why a piece is where
it is. The checklists for adding a command, a screen, an agent or a channel are in
`extending.md`; the undocumented parts of the CLI contract are in `cli-contract.md`.

## The projects

```
src/AgentsTracker.Agents.Abstractions/   agent contracts, no Telegram and no specific CLI:
  IAgentBackend         Probe() (binary, version), RunAsync(AgentRunRequest, IAgentRunObserver)
  AgentRun.cs           request (prompt, folder, session, model, effort, mode, timeout, attachments folder), observer, result
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
                        SendDocumentAsync/SendPhotoAsync — a file and a picture as a stream,
                        DownloadAttachmentAsync — the body of an incoming attachment into a stream
  ChannelLimits         channel bounds: message and caption length, document, photo and download size
  RateLimitRetry        one retry after a 429 with a short retry_after — shared by every send
  ChatId, UserId        an address as a value; Key is "channel:value" for state.json, the audit and the log
  Messages.cs           OutgoingMessage, Keyboard, MessageRef, IncomingMessage (text + attachments),
                        IncomingAttachment/AttachmentKind, ButtonPress, ChatCommand
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
    Chat/               ChatWorker (queue, launch, sessions), RunStatusMessage, /new /stop,
                        AttachmentInbox — pictures from the chat: download, checks, inbox folder, cleanup
    Settings/           SettingsMenuCoordinator + Screens/*, /menu /status /sessions /agent /skills /project /usage
    Help/ Audit/ Monitor/   /start /help; /audit; the web page (index.html — EmbeddedResource) and /api/*
```

Layering rules (they are in CLAUDE.md as well — breaking one is an architecture error, not a typo):
`Domain` and `Abstractions` do not open files and do not go over the network; `Gateway` knows about
a specific agent and channel only in `Program.cs`; the backend and the channel know nothing about
each other or about `GatewayOptions`; `Infrastructure` knows nothing about features (except the
`Dispatch` contracts); a feature depends on a feature only through a public service (`Settings` →
`ChatWorker.IsBusy`).

## How a message travels

`ChatDispatcher` lets through only `IChatChannel.AllowedUsers` and private chats (`ChatKind.Unknown`
is a rejection too). Text is processed in order:

1. a slash command from `IChatCommandHandler.Commands` — **first of all**, otherwise `/stop` would
   go to a waiting free-form answer and there would be nothing left to interrupt a stuck run with;
2. the `IChatTextHandler` chain in module order: an answer to a card (`ApprovalTextHandler`) →
   skill arguments (`SkillArgumentsTextHandler`) → into the agent's queue (`ChatEnqueueTextHandler`,
   always `true`). That is why `ChatModule` is last. Unknown slash commands are Claude Code's own
   commands, they go to the CLI.

The order holds for a caption too: `/stop` must work with a picture attached, and then the picture
is not saved and the dispatcher says so instead of dropping it silently. A message with an
attachment is not skill arguments — `SkillArgumentsTextHandler` lets it through and keeps waiting.
`ApprovalTextHandler`, on the contrary, keeps it (a card left hanging would freeze the run) and
warns that the picture did not go anywhere.

A handler receives the whole `IncomingMessage`: besides the text it may carry attachments. Pictures
are taken by `ChatEnqueueTextHandler` through `AttachmentInbox` — it downloads them into
`<data directory>\inbox\<chat>\<project>\`, checks the type by the signature bytes and puts the
absolute paths into the prompt; the run then gets that chat folder in `--add-dir`, and nothing else.
Details — `cli-contract.md`.

Buttons: the `IChatButtonHandler` chain (`Dispatch/`), each recognizing its own by the prefix in
`CanHandle`: `cfg:` — the menu (`SettingsCallbackHandler`), everything else (a hex request id) —
`ApprovalCallbackHandler` → `ApprovalBroker`. The broker's `ActiveChat` is set by `ChatWorker`
before a run, otherwise cards would go to another user's chat.

## The gateway → CLI → gateway loop

The gateway both launches the agent and serves it:

```
channel ─▶ ChatDispatcher ──▶ ChatWorker ──▶ ClaudeBackend ──▶ claude.exe -p
                    ▲                                                     │ needs permission
                    └── ApprovalBroker ◀── OperatorConsole ◀── ClaudePermissionTool ◀── MCP http://127.0.0.1:<McpPort>/mcp
                                                                              Authorization: Bearer <token>
```

At startup `McpConfigFile` generates a token, writes `mcp-gateway-<pid>.json` and deletes it on
shutdown. The name carries the PID: a shared file was overwritten and deleted by a second instance,
and the working gateway then died on "mcp__tg__approve not found"; files of dead PIDs are swept at
startup. `/mcp` is mounted with the `McpConfigFile.Authorizes` filter; the CLI gets `--mcp-config`
and `--permission-prompt-tool mcp__tg__approve`. The server and tool names are `McpConfigFile`
constants, `[McpServerTool]` takes the same constant.

`ApprovalTimeoutMinutes` reaches `AgentHost` and from there the MCP server's `timeout` in the
config: without it the CLI aborted the call after 5 minutes of silence, and the card was dead
afterwards (`cli-contract.md`).

The second tool of the same server is `mcp__tg__send_file` (`ClaudeSendFileTool`): the agent calls
it itself to send a file into the chat. Path → `IOperatorConsole.SendFileAsync` → `OperatorConsole`
checks the folder (the current project only, the data directory is forbidden, no symlink/junction
on the path), the type (an extension allowlist in code) and the size (`ChannelLimits`), writes
`file.send` into the audit and sends it through `ApprovalBroker.SendFileAsync` →
`IChatChannel.SendDocumentAsync`/`SendPhotoAsync`. A refusal is always a
`{"sent":false,"reason":…}` answer, never an exception.

`McpConfigFile` and `SessionStore` are the only classes with a classic constructor: the side effect
(a file) must happen exactly once before startup.

The README is the user's manual: when changing the endpoint protection or the data directory, check
its «Безопасность» section.

## Sessions and chat-side selection

Sessions are keyed by the **normalized project path** (`ProjectCatalog.Normalize`); the id of a new
one is issued by the gateway (`--session-id`) and is reset only on `SessionLost` from the CLI —
details in `cli-contract.md`.

The chat-side selection (`SessionStore`) sits on top of the config: `EffectiveModel`,
`EffectivePermissionMode`, `EffectiveEffort`, `ProjectPath`. A value equal to the config is stored
as `null`: otherwise a config edit would be silently overridden by an old selection.

`ProjectCatalog`: the `Gateway:Projects` list, otherwise a walk of `Gateway:ProjectsRoot` down to
`ProjectsRootDepth`, otherwise the neighbours of `ProjectPath`. `ProjectScreen` selects in two
steps (folder → repository) in pages of 12. The current project comes first in its group, and after
a selection the page resets to the first one — otherwise the `▶` marker could end up off-screen.

## State, secrets, configuration layers

`%LOCALAPPDATA%\AgentsTracker\` (`AppPaths.DataDirectory`, ACL — the owner and SYSTEM):
`state.json` (atomic), `mcp-gateway-<pid>.json`, `appsettings.Local.json` with the secrets,
`audit\audit-ГГГГ-ММ.jsonl`.

The config comes in layers: `appsettings.json` → `appsettings.Local.json` next to the exe (IDE) →
the same file in the data directory (production) → environment variables. `dpapi:…` is decrypted on
load (`ProtectedJsonConfigurationProvider`); `protect-secrets` encrypts `Gateway:Proxy` and the
`IChatChannelModule.SecretKeys` keys in `Gateway:Channel:Settings` (for Telegram — `BotToken`,
`Proxy`). `publish\` contains no secrets. `Gateway__*` variables are invisible to the child
`claude` — the host strips them from the environment in `ValidateStartup`. The user-facing side of
all this is in `deployment.md`.

Moved channel keys live in one list, `ChannelOptions.MovedKeys`: both the startup check and
`protect-secrets` read it. Test on a copy of the old config: `protect-secrets <path>` must return 1
with a hint, and `migrate-channel-settings.ps1 -Path <path>` must not touch what is already filled
in under `Channel:Settings`.

## Audit

`IAuditLog.Write(AuditEvent.Now(kind, summary, user, chat, project, session, outcome))` — "who,
where, what"; addresses are stored as `UserKey`/`ChatKey` strings ("channel:value"), not as
numbers: ids from different channels collide. There is also `NowByKeys` — for writing by
ready-made keys from `state.json`. No secrets and no full texts (≤200 characters; the user's text
is a `Text.Preview` excerpt, 80 characters: a token could have been pasted there).

The kinds are in `AuditKinds`: `access.rejected`, `message`, `run.start`/`run.end`, `approval`,
`question`, `file.send`, `settings`, `rules`, `session.reset`, `limit.refused`, `gateway`. Menu
screens write through `SettingsAudit.Changed`.

This is not a replacement for `ILogger`: the audit gets what a human is answerable for, the log
gets what is needed for debugging.

## Chat output

`MarkdownRenderer` converts markdown into `ChatHtml` and cuts it to `IChatChannel.Limits`
(`MessageLength`, 3800 for Telegram) — cut the **source markdown before the conversion**, otherwise
tags get torn apart. Lists use `•`/`◦`, a table is an aligned `<pre>`. Italics only for a `*` tight
against the content on a word boundary (otherwise `*.cs` and `2 * 3` went italic); `_` is not
parsed (`snake_case`). A code block longer than the limit is sent as a file. Escaping within the
budget — `ChatHtml.EscapeCapped` (text that fits whole does not spend a character on the ellipsis).

If the channel answered with any 400 (not just "can't parse": an unsupported tag, "too long" after
escaping), it retries the send itself without markup (`ChatHtml.StripTags`) — otherwise part of the
answer would vanish silently. On a 429 in the middle of a multi-part answer
`ChatWorker.SendPartAsync` waits `RetryAfter` (≤30 s) and repeats the same part; the same goes for
the agent's file in `ApprovalBroker` — both through `RateLimitRetry.OnceAsync`
(`Channels.Abstractions`), one shared ceiling. Sums, tokens and time — `DisplayFormat`.

### Numbers

Anything fractional is `decimal`, never `double`/`float`: the consumed share of a window
(`LimitWindow.Used`, `LimitGauge.Used`), the gauge frames, the divisors in `DisplayFormat`.
`double` rounds where nobody expects it — `0.67 * 100` is `67.00000000000001` — and the limit
share is not just displayed but compared against the edge of a window that stops a run.
The gateway holds no money at all: `total_cost_usd` is not read (`cli-contract.md`).

Rounding a share into percent happens in **exactly one** place — `LimitMath.Percent`/`Left`
(`Agents.Abstractions`): consumption up to a whole number and clamped to 0..100, the remainder as
`100 - Percent`. Everything that shows percentages calls it: the `/status` gauges, the menu
summary, `/api/limits` (the monitor page gets `percent` and `left` ready-made — JS has no
`decimal`). A second rounding formula somewhere is a bug even when it agrees on your numbers:
independent rounding diverged on 14 values out of 1001, and one message showed «67% · осталось 32%».

What stays `double` is the BCL's own arithmetic — `TimeSpan.TotalSeconds` and its kin — and only
where the result is immediately truncated to whole units for a caption. Anything counted, rather
than displayed, is derived from `Ticks` (`RunStatusMessage`) or from `long`.

## Details worth knowing before touching the host

- `Channel.CreateUnbounded` in `ChatWorker` without `SingleReader`: with it `Reader.Count` throws
  and `/status` falls over.
- `_runCts` in `ChatWorker` is set right before `agent.RunAsync`, after the status message is sent:
  only the run's `finally` clears it, and a failure above would leave `IsBusy` set until a restart.
- `HttpClient` only through `IHttpClientFactory`. `ClaudeLimits.HttpClientName` — one timeout, no
  retries. The Telegram client (`TelegramClientFactory`) is a singleton, DNS is refreshed by
  `PooledConnectionLifetime`; retry only on `HttpRequestException`: Bot API methods are POSTs
  without idempotency (a retry after a 5xx is a duplicate in the chat), and a 429 is waited out by
  the caller per `retry_after`. Do not set `BaseAddress`. `RemoveAllLoggers()`: the token is part
  of the path.
- CLI arguments go through `ProcessStartInfo.ArgumentList`, do not concatenate a string.

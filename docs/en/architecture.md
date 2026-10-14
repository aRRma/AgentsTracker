# How the gateway is built

When to read this: you need to find where something lives, or why it is there. Checklists for adding
a command, a screen, an agent or a channel — `extending.md`; the undocumented parts of the CLI
contract — `cli-contract.md`; the words used in buttons, messages and here — `glossary.md`.

## The projects

```
src/AgentsTracker.Agents.Abstractions/   agent contracts, no Telegram and no specific CLI
  IAgentBackend         Probe(), RunAsync(AgentRunRequest, IAgentRunObserver), InspectProject (fingerprint + items of the project's agent settings)
  AgentRun.cs           request (prompt, folder, session, model, effort, mode, timeout, attachments folder, kind), observer, result
  AgentCapabilities     models/efforts/modes the agent supports (effort null — not supported)
  IOperatorConsole      what the agent asks a human: ApproveAsync, AskAsync, SendFileAsync; PersistentRule — an "always" rule
  IAgentLimits, IAgentSkillCatalog, IAgentBackendModule (AddServices + MapEndpoints; AgentHost — data directory, port, proxy, card timeout)
  RunUsage, RunActivity, LimitMath (the one percent formula), Text (clipping, Text.Redact)
src/AgentsTracker.Agents.Claude/         Claude Code behind those contracts
  ClaudeAgentModule, ClaudeBackend (the claude -p process), ClaudeCapabilities (PermissionModes.Selectable — what the chat may switch to),
  ClaudeLimits, ClaudeSkillCatalog, ClaudePluginRegistry, ClaudeCliLocator, ClaudeStreamEvent, ClaudeCliJson, ClaudeOptions
  ClaudeProjectSettings hooks, allow, MCP servers and skills of the project, with a fingerprint
  ClaudeProcessJob      a Windows Job Object: every claude and its children die with the gateway
  Mcp/                  McpConfigFile (per-run config: token + run id), ClaudePermissionTool (CLI ↔ IOperatorConsole), ClaudeSendFileTool
src/AgentsTracker.Channels.Abstractions/ channel contracts, no specific messenger
  IChatChannel          ConnectAsync/ListenAsync, Send/Edit/Delete/Acknowledge, SendDocumentAsync/SendPhotoAsync, DownloadAttachmentAsync;
                        ChannelLimits — message, caption, button label and data, document, photo and download limits
  RateLimitRetry        one retry after a 429 with a short retry_after, shared by every send
  ChatId, UserId        Key is "channel:value" for state.json, the journal and the log
  Messages.cs           OutgoingMessage (ReplyTo), Keyboard, MessageRef, IncomingMessage, IncomingAttachment, ButtonPress, ChatCommand
  IChatInbound          where the channel hands incoming traffic (ChatDispatcher implements it)
  ChatHtml              the canonical text format (b, i, s, code, pre, a, blockquote) and escaping
  ChannelRequestException  the channel's only outward exception: RateLimited, MarkupRejected, CannotReach, Unauthorized
  IChatChannelModule    AddServices + MapEndpoints, SecretKeys; ChannelHost — the shared proxy
src/AgentsTracker.Channels.Telegram/     Telegram behind those contracts
  TelegramChannel       long polling, inline buttons, HTML; "message is not modified" and retry_after live here
  TelegramChannelModule, TelegramOptions, TelegramIds, TelegramClientFactory (the client is lazy: the constructor validates the token)
src/AgentsTracker.Gateway/
  Program.cs            data directory, service commands, lists of agents (Gateway:Agent), channels (Gateway:Channel:Type) and features
  Domain/               pure models: GatewayState (state.json), AuditEvent and AuditKinds
  Infrastructure/       GatewayInfrastructure.cs (config layers, DI, Kestrel, ValidateStartup, MapFeatures), AppPaths, Modules/IFeatureModule,
                        Configuration (GatewayOptions, ProjectCatalog), State (SessionStore, CacheGuard), Git, Audit, Monitoring (RunMonitor, RingBufferLog),
                        Chat (ChatGatewayService, ChatDispatcher, DisplayFormat, MarkdownRenderer,
                        StartupNotice — «запущен»/«прерван» after a restart, Dispatch/ — handler contracts),
                        Security (DPAPI secrets, ProtectSecretsCommand, DataDirectoryAcl), Cli/ (ConsoleCommands routes install, uninstall, protect-secrets),
                        Autostart/ (a Task Scheduler task via schtasks),
                        Container.Detected (in a container `install` refuses, and startup warns if `MonitorBind` does not fit: there it must be `any`)
  Features/             vertical slices, each with its own *Module
    Approvals/          ApprovalBroker, ApprovalCardRenderer, OperatorConsole, /rules
    Chat/               ChatWorker (two lanes), RunStatusMessage, /new /stop, AttachmentInbox (pictures from the chat),
                        ProjectTrustGate («Доверять проекту?» before a task's run)
    Settings/           SettingsMenuCoordinator + Screens/*, SkillLauncher, PendingConfirmations (the second press for irreversible buttons),
                        /menu /settings /status /sessions /agent /model /effort /mode /skills /project /usage
    Question/           /ask and the «Задать вопрос» buttons: a one-off run with no session (QuestionLauncher)
    Help/ Audit/ Monitor/   /start /help; /audit; the web page (index.html — EmbeddedResource) and /api/*
```

Layering rules (also in CLAUDE.md — breaking one is an architecture error, not a typo): `Domain` and
`Abstractions` do not open files and do not go over the network; `Gateway` knows a specific agent
and channel only in `Program.cs`; the backend and the channel know nothing about each other or
`GatewayOptions`; `Infrastructure` knows nothing about features (except the `Dispatch` contracts); a
feature depends on a feature only through a public service (`Settings` → `ChatWorker.IsBusy`).

## How a message travels

`ChatDispatcher` lets through only `IChatChannel.AllowedUsers` and private chats (`ChatKind.Unknown`
is a rejection too). Text is processed in order:

1. a slash command from `IChatCommandHandler.Commands` — **first of all**, otherwise `/stop` would
   go to a waiting free-form answer and nothing could interrupt a stuck run;
2. the `IChatTextHandler` chain in module order: an answer to a card (`ApprovalTextHandler`) →
   skill arguments (`SkillArgumentsTextHandler`) → the text of a вопрос after a bare `/ask` or the
   «Задать вопрос» button (`QuestionTextHandler`) → the agent's queue (`ChatEnqueueTextHandler`, always
   `true`). That is why `ChatModule` is last. Unknown slash commands are Claude Code's own and go to
   the CLI.

The order holds for a caption too: `/stop` works with a picture attached; the picture is not saved
and the dispatcher says so. A message with an attachment is not skill arguments —
`SkillArgumentsTextHandler` lets it through and keeps waiting. `ApprovalTextHandler`, on the
contrary, keeps it (a card left hanging would freeze the run) and warns that the picture went nowhere.

`ChatEnqueueTextHandler` takes pictures through `AttachmentInbox`: downloads them into
`<data directory>\inbox\<chat>\<project>\`, checks the type by the signature bytes, puts the absolute
paths into the prompt. The run gets that chat folder in `--add-dir`, and nothing else (`cli-contract.md`).

Buttons: the `IChatButtonHandler` chain (`Dispatch/`), each recognizing its own by `CanHandle`:
`cfg:` — the menu (`SettingsCallbackHandler`), everything else (a hex request id) —
`ApprovalCallbackHandler` → `ApprovalBroker`.

Before a task's run `ChatWorker` asks `ProjectTrustGate`: if `IAgentBackend.InspectProject` finds the
agent's settings in the project and their fingerprint is not trusted yet, a «Доверять проекту?» card
goes through `ApprovalBroker` under its own short-lived `RunScope` (so `/stop` withdraws it).
«❌ Отклонить», silence and `/stop` all mean no run; the first two also drop the task queue. The
project is re-read after the card: if `/project` changed meanwhile, the new one is checked too. Why —
`cli-contract.md`, "Project settings and the trust dialog".

### Two lanes: a task and a вопрос in parallel

`ChatWorker` has two lanes, each with its own queue and `CancellationTokenSource`: tasks (and
`/context`, `/compact`) one at a time, вопросы (`AgentRunKind.Question`) one at a time, and a task
runs **in parallel** with a вопрос — otherwise a вопрос, asked precisely so as not to wait, would
wait for a long task. Never more than two agent processes at once.

- `IsBusy`/`QueueLength` are about tasks only: the menu, a session switch and «Сжать» do not wait
  for a вопрос. `IsAnswering`/`QuestionQueueLength` — for the status line.
- `/stop` (`ChatWorker.Stop`) interrupts both lanes and calls `ApprovalBroker.CancelAll`.
- A limit refusal, `RateLimited` or a declined trust drains only its own lane: the вопрос has its
  own model, and the other lane checks the limit before its own run anyway.
- A вопрос stays out of `RunMonitor` (the current run and the queue there are the task's) but is in
  the run history and the journal. `state.json` keeps the task in `ActiveRun` and the вопрос in
  `ActiveQuestion`; after a restart `StartupNotice` reports both.
- A вопрос is answered as a **reply** to the user's message: «Вопрос принят», the status, every part
  of the answer, its cards and files. The path: `IncomingMessage.Message` → `QueuedPrompt.ReplyTo` →
  `OutgoingMessage.ReplyTo` and the `replyTo` parameter of the channel's file methods. Telegram sends
  it with `AllowSendingWithoutReply`: a deleted question would otherwise turn the answer into a 400.

Each run is registered right before the process starts: `ApprovalBroker.BeginRun(RunScope)` — the
run id, the chat, the message to reply to, the project for card paths and `send_file`, the session
for the journal. Cards and files go to the run's chat; disposing the registration in the run's
`finally` cancels only **that** run's cards. The broker has no "current chat".

Free-text waits («Отклонить с причиной», «Свой ответ») are many: a task, a вопрос and parallel
subagents may all wait at once. `ApprovalBroker.DeliverText` gives the text to the wait whose request
the user replied to (`IncomingMessage.InReplyTo`), else to the only wait in that chat. With several
waits and no reply `ApprovalTextHandler` asks to reply to the right card, and the text does not
become a new task.

## The gateway → CLI → gateway loop

The gateway both launches the agent and serves it:

```
channel ─▶ ChatDispatcher ──▶ ChatWorker ──▶ ClaudeBackend ──▶ claude.exe -p
                    ▲                                                     │ needs permission
                    └── ApprovalBroker ◀── OperatorConsole ◀── ClaudePermissionTool ◀── MCP http://127.0.0.1:<McpPort>/mcp
                                                                              Authorization: Bearer <token>
```

At startup `McpConfigFile` generates a token. Each run gets its own config file
(`McpConfigFile.ForRun`): `mcp-gateway-<pid>-<run>.json` with the token and the
`X-AgentsTracker-Run: <RunId>` header. `ClaudeBackend` deletes it when the process exits, the gateway
deletes leftovers on shutdown. It is a file, not inline JSON in `--mcp-config`: the arguments go into
the log, and the token would go with them. The name carries the PID: a shared file was overwritten
and deleted by a second instance, and the working gateway then died on "mcp__tg__approve not found".
Files of dead PIDs are swept at startup. `/mcp` is mounted with the `McpConfigFile.Authorizes` filter;
the CLI gets `--mcp-config` and `--permission-prompt-tool mcp__tg__approve`. The server and tool names
are `McpConfigFile` constants, `[McpServerTool]` takes the same ones.

The run id is how a call finds its run: the tools read the header through `IHttpContextAccessor`
(the SDK runs a tool in the `ExecutionContext` of its HTTP request) and put it into
`ApprovalRequest.RunId`/`FileSendRequest.RunId`/`AskAsync(runId, …)`. `OperatorConsole` looks the run
up in `ApprovalBroker.Run(runId)`; an unknown or missing id (the run already ended) is a refusal,
never a card in somebody else's chat. Subagents live in the same CLI process and send the same header.

The MCP server's `timeout` in the config is `ApprovalTimeoutMinutes` plus one minute: without it the
CLI aborted the call after 5 minutes of silence and the card was dead afterwards (`cli-contract.md`).

The second tool, `mcp__tg__send_file` (`ClaudeSendFileTool`), goes `IOperatorConsole.SendFileAsync`
→ `OperatorConsole` (checks the folder, type and size, writes `file.send` into the journal) →
`ApprovalBroker.SendFileAsync` → `IChatChannel.SendDocumentAsync`/`SendPhotoAsync`. A refusal is
always a `{"sent":false,"reason":…}` answer, never an exception. The checks — `cli-contract.md`,
"File to chat".

`McpConfigFile`, `SessionStore` and `ClaudeProcessJob` are the only classes with a classic
constructor: the side effect (sweeping stale config files, reading the state file, creating the Job
Object) must happen exactly once, when the singleton is created.

## Sessions and chat-side selection

Sessions are keyed by the **normalized project path** (`ProjectCatalog.Normalize`); the gateway
issues the id of a new one (`--session-id`) and resets it only on `SessionLost` from the CLI
(`cli-contract.md`).

The chat-side selection (`SessionStore`) sits on top of the config: `EffectiveModel`,
`EffectivePermissionMode`, `EffectiveEffort`, `ProjectPath`. A project equal to the config is stored as
`null` (`SetProjectPath`): otherwise a config edit would be silently overridden by an old selection.
The model, effort and mode are stored as picked, and the «по умолчанию» / «из конфига» buttons (or
`reset`) put `null` back. The model is stored as the alias that goes to the agent (`opus`), but a
person always sees it through
`SessionStore.ModelLabel` — with the version the alias last answered with (`ModelVersions` in
`state.json`), else the agent's `Describe`.

### Context and run kinds

`AgentRunRequest.Kind` tells the backend what the run is: a `Task` in the session, a `Question` (no
`--resume`, nothing written to disk, `SessionId` in the result always `null` so the host never makes a
one-off id active), a `ContextReport` (`/context`, the model is not called) or a `Compact`
(`/compact`, same session). The commands of the last two are the backend's own — the host only asks
`Capabilities.Context`. `ChatWorker` skips the limit check for `ContextReport` (it costs nothing and
helps most when the plan is exhausted) and refuses both context kinds if the session was dropped
while they waited in the queue — a run without a session would create one for nothing.

Context fill comes from the run itself: `RunUsage.ContextTokens` is the input of the **last**
main-thread turn (sub-agents have their own window), after a `/compact` — its `post_tokens`;
`ContextWindow` is the window of the primary model. `SessionStore.RecordContext` keeps them on the
`SessionRecord`, and the «Контекст» screen shows them without starting the agent.

The cache lifetime comes from the run too, a cancelled one included (`AgentRunResult.LastRequestUtc`
and `CacheTtl`, `cli-contract.md`, "Context"). `SessionStore.RecordCache` turns them into
`SessionRecord.CacheUntilUtc` (a run that wrote nothing to the cache reports no lifetime, so the last known one is used). The
footer prints it as «🧊 кэш до 15:42» — a time, not a countdown, because the message never changes.
`ContextScreen.Cache` is the only place that turns it into text for «Статус» and «Контекст»;
`/api/stats` sends it raw in `sessions[].cacheUntilUtc`.

The session also remembers what the cache was written with: `CacheModel` (the answering version) and
`CacheMode` — the mode, if it is one of `AgentCapabilities.OwnCacheModes` (`plan` for Claude Code),
otherwise `null`, because the other modes share the cache. `CacheGuard` holds the rules:

- `WillReset` — a step away from what the cache was written with, while the cache is alive and the
  context is at least `MinContextTokens` (100k); a step back saves the cache and asks nothing. On the
  «Агент» screen such a button asks first through `PendingConfirmations`; a typed `/model` or `/mode`
  is a decision already, so it runs at once (`AgentScreen.Now`) and the warning is appended to the reply.
- `Mismatch` adds «…следующий запуск запишет кэш заново» to the cache line when the menu selection
  already differs from the cache.
- `Miss` — misses are counted, not guessed: `RunUsage.FirstCacheRead` is what the first main-thread
  request read from the cache; a continued task is a miss when that is under half of the session's
  previous context (from `MissContextTokens`, 20k — below that only the shared tools and system prompt
  are at stake). `UsageTotals.CacheMisses` goes to `/usage` and `/api/stats` (`total`, `byDay`).

«Сжать» and «Новая сессия» on «Контекст» use `PendingConfirmations` too: «Да» works only for the same
action, the same session and within 2 minutes; «Нет» or reopening the screen drops the question.

`ProjectCatalog`: the `Gateway:Projects` list, otherwise a walk of `Gateway:ProjectsRoot` down to
`ProjectsRootDepth`, otherwise the neighbours of `ProjectPath`. `ProjectScreen` selects in two steps
(folder → project) in pages of 12; after a selection the page resets to the first one, or the `▶`
marker could end up off-screen.

`GitRepository` gives the branch for «Статус», the project list, the run status, the footer,
`run.start` in the journal and the monitor; the 🌳 mark (the project is a worktree) is only on
«Статус» and the project list. `.git` is looked up to the drive root, like git does. The class reads
the `.git` files itself, with no `git` process: the caption is needed on every tick of the run status.

Everything about a run reads the branch of the **session's folder** (`SessionStore.SessionFolder`),
not of the project folder. The agent can move its session into a worktree itself (`EnterWorktree`),
and the CLI resumes it there although the process starts in the project — the details are in
`docs/en/cli-contract.md`, "A session in a worktree". `ChatWorker.RunObserver` takes the folder from
`IAgentRunObserver.WorkingDirectoryChanged`, the run status re-reads it every tick, and a change is
written to `SessionRecord.WorkingDirectory` at once (an interrupted run moves the session too).
«Статус» shows such a session as «🌳 сессия в worktree <folder> · 🌿 <branch>». A move through
absolute paths is still invisible to the gateway. Rules not to loosen:

- a file is opened with `FileShare.ReadWrite | FileShare.Delete` and only after its write time or
  length changed — otherwise the agent's `git checkout` on Windows fails renaming `HEAD.lock`;
- a UNC path and a network drive are never read: the path in `.git` is written by whoever handed over
  the project, and a UNC path would make Windows give a foreign server the account's hash;
- only `ref: refs/heads/<name>` or a SHA is shown, and the class never throws — a failed caption
  must not cost the agent's answer.

The caption (`GitHead.Label`) is cut to 40 characters; the caller escapes it, since git allows `<`,
`&`, `_` and a backtick in a branch name. The footer compares the branch at the start and at the end
of the run and shows `master → feature` if the agent switched it itself.

## State, secrets, configuration layers

`AppPaths.DataDirectory` is `%LOCALAPPDATA%\AgentsTracker\` (ACL — the owner and SYSTEM), or
`Gateway:DataDirectory` when set. It holds `state.json` (atomic), `mcp-gateway-<pid>-<run>.json` while
a run lives, `appsettings.Local.json` with the secrets, `inbox\` and `audit\audit-<YYYY-MM>.jsonl`.

`SessionStore.Save` writes `state.json.tmp` with `Flush(true)` and swaps it in with `File.Replace`,
leaving the previous version in `state.json.bak`: without the flush, NTFS after a power cut keeps the
rename but not the data, and the file comes back empty. A file that does not parse is moved aside as
`state.json.corrupt-<time>` (never overwritten by the next save), the state comes from the `.bak`, and
`SessionStore.LoadProblem` goes into «Шлюз запущен» — losing sessions, trust and rules silently is the
failure this prevents.

The config comes in layers: `appsettings.json` → `appsettings.Local.json` next to the exe (IDE) → the
same file in the data directory (production) → environment variables. `dpapi:…` is decrypted on load
(`ProtectedJsonConfigurationProvider`); `protect-secrets` encrypts `Gateway:Proxy` and the
`IChatChannelModule.SecretKeys` keys in `Gateway:Channel:Settings` (for Telegram — `BotToken`,
`Proxy`). `dotnet publish` copies a plain-text `appsettings.Local.json` from the project folder next to the exe;
`install` moves it to the data directory and encrypts it. `Gateway__*` variables are invisible to the child `claude`
— the host strips them from the environment in `ValidateStartup`. The user-facing side — `deployment.md`.

Moved channel keys live in one list, `ChannelOptions.MovedKeys`: the startup check and
`protect-secrets` both read it. Test on a copy of the old config: `protect-secrets <path>` must return
1 with a hint, and `migrate-channel-settings.ps1 -Path <path>` must not touch what is already filled in
under `Channel:Settings`.

## Audit

`IAuditLog.Write(AuditEvent.Now(kind, summary, user, chat, project, session, outcome))` fills the
journal (`/audit`, the monitor) — "who, where, what". Addresses are stored as `UserKey`/`ChatKey`
strings ("channel:value"), not numbers: ids from different channels collide. `NowByKeys` writes by
ready-made keys from `state.json`; `Now` goes through it, so masking sits in one place.

No secrets and no full texts: a summary is at most 200 characters, the user's text is a `Text.Preview`
excerpt of 80. `NowByKeys` masks every summary through `Text.ClipRedacted`: an approval writes the
command itself, a command carries a bot token from an environment variable or a curl header just as
easily, and the monitor serves the journal without a password. `Text.Redact` knows token shapes
(Telegram, `sk-`, GitHub, AWS, Slack, JWT), `Authorization`/`Bearer`, a password in a URL and the
values of `token=`/`--password`-like keys; `Text.Preview` masks too (log, monitor, run status).

The kinds are in `AuditKinds`: `access.rejected`, `message`, `run.start`/`run.end`, `approval`,
`question`, `file.send`, `file.receive`, `settings`, `rules`, `trust`, `session.reset`,
`limit.refused`, `gateway`. Menu screens write through `SettingsAudit.Changed`. The journal gets what
a human is answerable for, `ILogger` gets what is needed for debugging.

## Chat output

`MarkdownRenderer` converts markdown into `ChatHtml` and cuts it to `IChatChannel.Limits`
(`MessageLength`, 3800 for Telegram). Plain text is cut as the **source markdown before the
conversion**, otherwise tags get torn apart; a code block is measured as ready HTML, and one longer
than the limit goes as a file. Lists use `•`/`◦`, a table is an aligned `<pre>`. Italics only for a
`*` tight against the content on a word boundary (otherwise `*.cs` and `2 * 3` went italic); `_` is not
parsed (`snake_case`). Cards and `/audit` rows escape within a budget through `ChatHtml.EscapeCapped`
(text that fits whole does not spend a character on the ellipsis).

Any 400 from the channel except «chat not found» (an unsupported tag, "too long" after escaping — not
only "can't parse") repeats the send without markup (`ChatHtml.StripTags`), or part of the answer
would vanish silently.
A 429 in the middle of a multi-part answer is waited out (`RetryAfter`, ≤30 s) and the same part is
repeated — `ChatWorker.SendPartAsync` and, for the agent's file, `ApprovalBroker`, both through
`RateLimitRetry.OnceAsync`. Sums, tokens and time — `DisplayFormat`.

The footer under an answer (`ChatWorker.Footer`), in this order:

- the session id, or «💬 вопрос»;
- the project's branch in a code span (`🌿`);
- the model that **actually** answered (`RunUsage.PrimaryModel`, shortened to `opus-5-5`/`haiku-4-5`):
  the `message.model` of the last main-thread turn, not the model with the most output tokens in
  `modelUsage` — a sonnet subagent easily outwrites the session itself (`cli-contract.md`, "Context").
  The chosen model's `ModelLabel` is the last resort, when the CLI said nothing;
- right after the model, in brackets, the effort that was **passed**, as one to three letters
  (`opus-5-5(H)`; `DisplayFormat.EffortShort`: L, M, H, XH, MAX — the CLI does not report it back);
- turns and time, then «🧊 кэш до 15:42» (or «🧊 кэш остыл» when the lifetime is over).

A context breakdown has no model in its footer: the model was not called.

### Numbers

Anything fractional is `decimal`, never `double`/`float` (`LimitWindow.Used`, `LimitGauge.Used`, the
gauge frames, the divisors in `DisplayFormat`): `double` rounds where nobody expects it
(`0.57 * 100` is `56.99999999999999`), and the limit share is compared against the edge of a window
that stops a run. `double` stays only where the BCL itself counts that way (`TimeSpan.TotalSeconds`
and its kin) and the result is immediately truncated to whole units for a caption; anything counted,
rather than displayed, comes from `Ticks` (`RunStatusMessage`) or from `long`. The gateway holds no
money: `total_cost_usd` is not read (`cli-contract.md`).

Rounding a share into percent happens in **exactly one** place — `LimitMath.Percent`/`Left`
(`Agents.Abstractions`): consumption up to a whole number, clamped to 0..100, the remainder as
`100 - Percent`. Everything that shows percentages calls it: the `/status` gauges, the menu summary,
`/api/limits` (the monitor page gets `percent` and `left` ready-made — JS has no `decimal`). A second
formula is a bug even when it agrees on your numbers: independent rounding of consumption and
remainder once put «67% · осталось 32%» in one message.

## Details worth knowing before touching the host

- `Channel.CreateUnbounded` in `ChatWorker` lanes without `SingleReader`: with it `Reader.Count` throws
  and `/status` falls over.
- `Lane.RunCts` and the broker registration in `ChatWorker` are set right before `agent.RunAsync`,
  after the status message is sent: only the run's `finally` clears them, and a failure above would
  leave `IsBusy` (or a registered run) set until a restart.
- `HttpClient` only through `IHttpClientFactory`. `ClaudeLimits.HttpClientName` — one timeout, no
  retries. The Telegram client (`TelegramClientFactory`) is a singleton, DNS is refreshed by
  `PooledConnectionLifetime`; retry only on `HttpRequestException`: Bot API methods are POSTs without
  idempotency (a retry after a 5xx is a duplicate in the chat), and a 429 is waited out by the caller
  per `retry_after`. Do not set `BaseAddress`. `RemoveAllLoggers()`: the token is part of the path.
- Polling has its own pause on top (`TelegramChannel.PollingBackoff`, 1 s doubling to a minute, or the
  channel's `retry_after`). Telegram.Bot repeats `getUpdates` right after an error, and an error
  *answer* (409 from a second instance on the same token, a 5xx, a 401) is not a network failure, so
  the resilience handler does not slow it down — without the pause it is dozens of requests a second
  and a stack trace each.
- On startup `ChatGatewayService` retries a network failure forever (10 s doubling to 5 min). A
  refused token gets 6 attempts 10 s apart, then the gateway exits with code 1.

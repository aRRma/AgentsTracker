# Claude Code CLI Contract

When to read this: you are editing `ClaudeBackend`, `ClaudePermissionTool`, `ClaudeLimits`,
session handling, or updated the CLI. The schema is undocumented — everything below was
verified against a live CLI 2.1.x.

## Approvals (MCP tool `mcp__tg__approve`)

The CLI calls the tool with `{"tool_name":…,"input":{…},"tool_use_id":…}`. The response is a
JSON string:

- `{"behavior":"allow","updatedInput":{…}}` — `updatedInput` is **mandatory**, without it the
  CLI rejects the call;
- `{"behavior":"deny","message":"…"}`.

`ClaudePermissionTool` reads fields defensively (`Read(...)` tries snake_case/camelCase). The
raw payload is logged only at Debug: at Information, for Edit/Write that would be file contents.

`AskUserQuestion` arrives through the same tool and requires `updatedInput` with the original
`questions` and `answers` (key — the question text); it reaches the host as
`IOperatorConsole.AskAsync`.

### The «Всегда» ("Always") button

Two cases:

- The CLI sent `permission_suggestions` with `destination: localSettings` — the rule is
  written by the **CLI itself** into the project's `.claude/settings.local.json` (usually a
  prefix rule). The card shows exactly that, it reaches the host as `PersistentRule.Raw` and is
  returned unchanged in `updatedPermissions`.
- Otherwise the gateway remembers the exact signature in `state.json`
  (`AlwaysAllowByProject`, key — normalized project path), visible in `/rules`. Gateway rules
  apply only within their own project: `git push --force` from one repository must not
  silently pass in the others.

### Truncated tail

Approving a command that isn't fully visible is not allowed. When `ApprovalCard` truncates
something (the command, the sides of an edit, the remaining `MultiEdit` edits, the tail of a
`Write`), `OperatorConsole` sends the full text as a file before the card
(`ApprovalBroker.SendAttachmentAsync`), and the card warns «показано не всё» ("not everything
is shown"). The file name and content are decided by `ApprovalCardRenderer`
(`ApprovalAttachment`): usually `<tool>-input.txt` with a «=== фрагмент ===» ("=== fragment
===") summary, an `ExitPlanMode` plan — in full, as `plan.md` (`Truncated.AddDocument`). Cards
are assembled via `EscapeCapped` with a limit per fragment: an overflowing message would fail
on send, and the exception would become a denial.

### Self-granting permissions

Verified on CLI 2.1.x: `Write` to `.claude/settings.local.json` is rejected even in
`acceptEdits` (visible in `permission_denials`). After updating the CLI, recheck with the same
run in a temp folder:

```powershell
claude -p "…" --permission-mode acceptEdits --output-format json
```

### Waiting for a response

`ApprovalBroker` holds the MCP call on a `TaskCompletionSource` until a button is pressed and
returns a `ChoiceResult` (key + who pressed it). `WaitAsync` distinguishes a timeout from a
cancellation: `/stop` must throw `OperationCanceledException`, not look like «не ответил
вовремя» ("did not respond in time").

Gateway timeouts: `ApprovalTimeoutMinutes` (15) — the card, `RunTimeoutMinutes` (60) — the
whole `claude -p`; both 1..1440.

On top of this the CLI has its own limit on an MCP tool call: by default it aborts the call if
the server is silent for 5 minutes (`CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT`, error «sent no
response or progress for 300s»). At that point the card is still hanging in the chat, but the
agent has already received a denial. That's why `McpConfigFile` writes the server's `timeout` =
`ApprovalTimeoutMinutes` + 1 min: this is both the call limit and (since CLI 2.1.203) the lower
bound of the idle threshold. Progress notifications don't extend it — there's no point sending
them. On CLI older than 2.1.203 the threshold stays at 5 minutes, and the card doesn't live
longer than that.

## File to chat (MCP tool `mcp__tg__send_file`)

The second tool of the same `tg` server, called by the agent itself (`ClaudeSendFileTool`).
Arguments: `path` (absolute or relative to the project folder), `caption` (caption, no markup),
`as_document` (send an image as a document, uncompressed). The response is a JSON string
`{"sent":true}` or `{"sent":false,"reason":"…"}`; a refusal is always a response: a tool error
looks to the CLI like a server failure, and the agent doesn't understand what went wrong.

The policy lives in `OperatorConsole.SendFileAsync`, the tool only translates: the file must be
inside the current project's folder (`ProjectCatalog.Normalize` on both sides,
`ProjectCatalog.IsInside`), the gateway's data directory is forbidden separately; neither the
file itself nor the folders between it and the project root may be a symbolic link or
junction — the path is checked as a string, while the OS opens the file following links, and a
link inside the project pointing outward would hand over someone else's file; the extension is
from a whitelist in code (`.md .txt .json .cs .js .html` — as a document, `.png .jpg .jpeg` —
as a photo); size and caption — per the channel's `ChannelLimits`. Refusals also go into the
`file.send` audit with outcome `refused`.

The tool is passed in `--allowedTools mcp__tg__send_file` so the CLI doesn't call
`mcp__tg__approve` on every send: the gateway checks the folder, type, and size itself.
Verified on CLI 2.1.261 with a trial instance: `tools/list` returns both tools, `claude -p`
with this flag calls `send_file` directly, and doesn't touch `approve` (`permission_denials` is
empty). After updating the CLI, recheck the same way: a card for `tg:send_file` means the flag
stopped working.

## Startup and event stream

The CLI is started with `--output-format stream-json --verbose` (without `--verbose` the
stream is not written in `-p`). `ClaudeBackend.ReadStreamAsync` parses stdout line by line
(`ClaudeStreamEvent.Classify`): tool calls → `IAgentRunObserver.Activity`, everything else is
discarded — in a long run that's megabytes. The result is the last line, `"type":"result"`, in
the same shape as `--output-format json`; non-JSON lines (an update banner) go into the error
text. The CLI often leaves the error text in stderr and sends `result` empty (as with «No
conversation found» on a broken `--resume`) — the session-reset and limit checks also look at
stderr.

A run in progress is recorded in `state.json` (`GatewayState.ActiveRun`): `ChatWorker` sets
`BeginRun` before starting and `EndRun` in `finally`. If the record is still there at startup,
the previous instance died mid-run: `StartupNotice` sends «🔌 Шлюз запущен» ("Gateway started")
to everyone in `IChatChannel.AllowedUsers`, and to the chat of the interrupted run
(`ActiveRun.ChatKey` is parsed with `IChatChannel.ParseChat`) — «прерван, напишите „продолжай“»
("interrupted, write \"continue\""), and writes `run.end` with outcome `interrupted`. For a
user who hasn't written to the bot yet, Telegram doesn't allow it to send first —
`ChannelFailure.CannotReach` is swallowed at Debug.

In the chat — `RunStatusMessage`: a single «Работаю…» ("Working…") message, edited every 4 s
(elapsed time, call counter, the last three steps, subagents marked with `↳`). `Report` from
the stdout stream only records; the network I/O runs in its own loop; `DisposeAsync` waits for
the loop (≤10 s), otherwise an edit would race a deletion. The tool argument shown in the
status is one, short one (`ClaudeStreamEvent.Describe`): the full Edit/Write input is file
content.

## Sessions

Sessions are keyed by the normalized project path (`ProjectCatalog.Normalize`): `--resume`
only works in the folder where the session was created; switching repositories also switches
the active session. `ChatWorker` fixes the session and `ProjectPath` in `AgentRunRequest`
before starting — otherwise switching mid-run would split the working directory from the
session's project.

The new session's id is issued by the **gateway** (`NewSessionId` → `--session-id <uuid>`) and
registered via `IAgentRunObserver.SessionStarted` right after the process starts: otherwise
`/stop`, a timeout, or a crash on the first run would lose the branch. The session is reset
only when the CLI explicitly says it couldn't find it (`LooksLikeMissingSession` →
`AgentRunResult.SessionLost`), and only inside `ChatWorker.SettleSession` via
`TrySetSessionId(onlyIfActive)` — so as not to overwrite a `/new` or a session switch made
during the run.

`/sessions` shows up to the last 8; the button carries the `ShortId` (8 characters), not the
position in the list: a run finishing between rendering and the button press would shift the
numbers.

## Plan limits

The only limiter is `IAgentLimits`, with windows `five_hour`, `seven_day`,
`seven_day_<model>`; checked in `ChatWorker.ProcessAsync` before starting a run. The remaining
budget on one line — `ShortSummaryAsync` (menu summary); the windows for the gauges —
`ViewAsync`. `LimitGauge.Used` is the **consumed** fraction 0..1: the `LimitBars` gauges on
`/status` and the meters in the monitor fill with consumption, a full bar means the window is
exhausted. Consumption is rounded up and the remainder down, so neither number flatters and the
two always add up to 100%.

`ClaudeLimits` calls the **undocumented** `api.anthropic.com/api/oauth/usage` with the token
from `~/.claude/.credentials.json`: it requires a plausible User-Agent, responds 429 on
frequent polling (3 min cache), and can disappear in any version — on any error the run is
**skipped**, not blocked, otherwise the gateway would go completely silent. Fields are read via
`ClaudeLimits.Number` with a `ValueKind` check: `TryGetDouble` on `null` throws, and
`utilization: null` would reach the user as «Внутренняя ошибка шлюза» ("Internal gateway
error").

Credits ("extra usage") are forbidden to the agent: `ClaudeBackend` sets
`DISABLE_EXTRA_USAGE_COMMAND=1`, and when a run is aborted by the limit, `ChatWorker` clears
the whole queue.

## Skills and plugins

`ClaudeSkillCatalog` collects the same thing the CLI sees: `skills/*/SKILL.md` and
`commands/**/*.md` from the project's `.claude` and `~/.claude`, plus enabled plugins
(`~/.claude/plugins/installed_plugins.json`; `enabledPlugins` layers profile →
`.claude/settings.json` → `settings.local.json`, no entry means enabled). `user-invocable:
false` ones are not shown. The scan is cached for 5 s: a menu tap is an `Apply` and a `Render`
back to back; «🔄 Обновить» ("Refresh") is `Refresh()`.

The «🔌 Плагины» ("Plugins") screen (`ClaudePluginRegistry`) toggles
`enabledPlugins[name@marketplace]` only in the personal `~/.claude/settings.json` — the CLI's
own `/plugin` writes to the same place. The file is rewritten in full via `JsonNode` (LF, no
`\u`, through a temp file — a neighboring CLI session may be reading it at that moment),
comments won't survive. A value from the project layer is marked 🔒 and can't be changed from
chat (`PluginInfo.LockedBy`). The button carries the plugin's key, not the desired state — it
flips the current one. Takes effect from the next `claude -p`.

Built-in CLI skills can't be enumerated — the «Встроенные» ("Built-in") group comes from
`Gateway:Claude:BuiltInSkills` (default in `ClaudeOptions` for CLI 2.1.x, after a CLI update —
via config, without a rebuild). «С аргументами» ("With arguments") — `SkillLauncher.Expect`
remembers the command against the user, and `SkillArgumentsTextHandler` turns the next text
into `/command text`. Runs are counted in `SkillUsage` (the `@bot` suffix is stripped), «⭐
Частые» ("Frequent") — up to the five most frequent.

## Locating the binary

`claude.exe` is located by `ClaudeCliLocator`: `Gateway:Claude:Executable` → standard paths →
PATH → the VS Code extension's binary (only to get it running; the path with the extension
version disappears on update — a warning is logged). The standard setup is a separate CLI
(`irm https://claude.ai/install.ps1 | iex`). The path is cached but rechecked before every run.
`ValidateStartup` logs the version — the JSON parsing depends on a specific version.

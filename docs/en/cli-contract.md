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
`questions` and `answers` (key — the question text); the host gets it as `IOperatorConsole.AskAsync`.
CLI 2.1.261 `--help` drifted, the flags did not: `default` shows as `manual` (the CLI still accepts
`default`) and `--permission-prompt-tool` is listed only inside `--permission-prompts`, yet the
flag works. Re-check both after a CLI update.

### The «Всегда» ("Always") button

Two cases:

- The CLI sent `permission_suggestions` with `destination: localSettings`, `type: addRules`,
  `behavior: allow` — the rule is written by the **CLI itself** into the project's
  `.claude/settings.local.json` (usually a prefix rule). The card shows exactly that; it reaches
  the host as `PersistentRule.Raw` and returns unchanged in `updatedPermissions`. Only the rules
  the card showed go back (`ApprovalCardRenderer.ShownRules`, at most 4): a compound command gets
  a suggestion per part, and the rest would be written unseen. Other suggestion types (`setMode`,
  `addDirectories`) are dropped — the card cannot show them.
- Otherwise the gateway remembers the exact signature in `state.json` (`AlwaysAllowByProject`,
  key — normalized project path), visible in `/rules`. Gateway rules apply only within their own
  project: `git push --force` from one project must not silently pass in the others. The project
  is the **run's** (`RunScope.ProjectPath`), not the chat's current one: a `/project` switched
  mid-task would otherwise lend the task the other project's rules and take its «Всегда» there.

### Truncated tail

Approving a command that isn't fully visible is not allowed. When `ApprovalCard` truncates
something (the command, the sides of an edit, the rest of a `MultiEdit`, the tail of a `Write`),
`OperatorConsole` sends the full text as a file before the card (`ApprovalBroker.SendAttachmentAsync`),
and the card warns «Показано не всё». `ApprovalCardRenderer` decides the name and content
(`ApprovalAttachment`): usually `<tool>-input.txt`, each truncated field under its own
«=== <field> ===» header («Команда», «Было» / «Станет», «Содержимое файла», «Задание сабагенту»,
«Вход целиком»); an `ExitPlanMode` plan goes in full as `plan.md` (`Truncated.AddDocument`). Cards
are assembled via `EscapeCapped` with a limit per fragment: an overflowing message would fail on
send, and the exception would become a denial.

### Self-granting permissions

Verified on CLI 2.1.x: `Write` to `.claude/settings.local.json` is rejected even in
`acceptEdits` (visible in `permission_denials`). After a CLI update, recheck with the same run in
a temp folder: `claude -p "…" --permission-mode acceptEdits --output-format json`.

### Project settings and the trust dialog

`-p` skips the workspace trust dialog (stated in `claude --help`): the project's
`.claude/settings.json` is read straight away. Verified on CLI 2.1.292 with a temp folder holding
a `SessionStart` hook — the hook ran with `--permission-mode default`, without any question; the
same run in a folder without settings did not. So a cloned foreign project would run its hooks
and MCP servers with no approval card at all. `--permission-mode`, passed explicitly, still
overrides the project's `defaultMode`.

The host asks instead, before the run: `IAgentBackend.InspectProject` (for Claude —
`ClaudeProjectSettings`) lists hooks, `allow` rules, MCP servers, command keys (`apiKeyHelper`,
`statusLine`…), `env` variables, plugins and the files of `.claude/{skills,commands,agents,hooks}`,
and gives a SHA-256 fingerprint of all of them. `ProjectTrustGate` shows the «Доверять проекту?»
card (12 items at most) and keeps the fingerprint in `GatewayState.TrustedProjects`; any change
of those files changes the fingerprint, and the card comes again. A вопрос is not checked: it
runs in an empty temp folder, recreated before every вопрос — otherwise a task's agent could plant
a `.claude/settings.json` there and its hook would run on every `/ask`; a folder that fails to
clear refuses the вопрос.

What keeps the fingerprint honest:

- Settings files (`.claude/settings.json`, `settings.local.json`, `.mcp.json`) are always parsed,
  up to 16 MB: a `settings.json` padded with spaces must not hide its hook. Above that it is an
  item «проверьте вручную».
- Hooks, command keys, MCP servers and `env` variables come first on the card (an `env` item
  shows the value with secrets masked: `NODE_OPTIONS=--require` runs code). `allow` rules go by
  name up to 3, above that they fold into one line, so a hook never drops into «и ещё N».
- Every file in the four folders counts, `node_modules`/`.pyc`/`.log` included — whatever is
  skipped by name could be changed after trust. A file over 1 MB is hashed by size and time, a
  folder is capped at 2000 files, a linked folder is not followed but becomes an item.
- `allow` rules of `settings.local.json` count too, at the price of one more card after «Всегда»
  with the agent's rule.
- After «Доверять» the project is inspected again: if the files changed while the card waited, a
  new card comes with the new list.

«Отклонить» and silence drop the task queue, like a refused limit — otherwise every queued task
would wait for its own card. After a CLI update, recheck the trust skip with the same temp-folder
run.

### Waiting for a response

`ApprovalBroker` holds the MCP call on a `TaskCompletionSource` until a button is pressed and
returns a `ChoiceResult` (key + who pressed it). `WaitAsync` distinguishes a timeout from a
cancellation: `/stop` must throw `OperationCanceledException`, not look like «не ответил
вовремя». Timeouts: `ApprovalTimeoutMinutes` (15) — the card, `RunTimeoutMinutes` (60) — the whole
`claude -p`; both 1..1440.

A timeout and `/stop` kill the process together with its sub-agents (they run inside it). What
survives: the edits on disk, the session (the next message resumes it), and every sub-agent step,
which the CLI writes as it goes to `~/.claude/projects/<project>/<session>/subagents/agent-*.jsonl`.
What is lost: the sub-agent's final report to the main agent. So the cancelled-task message tells
the user to write «продолжи» and, if sub-agents worked (a nested tool call was seen), to ask the
agent to read their steps — `ClaudeBackend.Resumable`.

The CLI also aborts an MCP tool call when the server is silent for 5 minutes
(`CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT`, error «sent no response or progress for 300s»): the card
still hangs in the chat, but the agent has already received a denial. So `McpConfigFile` writes
the server's `timeout` = `ApprovalTimeoutMinutes` + 1 min — the call limit and (since CLI 2.1.203)
the lower bound of the idle threshold. Progress notifications don't extend it. On CLI older than
2.1.203 the threshold stays at 5 minutes, and the card doesn't live longer.

## File to chat (MCP tool `mcp__tg__send_file`)

The second tool of the same `tg` server, called by the agent itself (`ClaudeSendFileTool`).
Arguments: `path` (absolute or relative to the project folder), `caption` (no markup),
`as_document` (send an image uncompressed, as a document). The response is `{"sent":true}` or
`{"sent":false,"reason":"…"}`; a refusal is always a response: a tool error looks to the CLI like
a server failure, and the agent doesn't understand what went wrong. The policy lives in
`OperatorConsole.SendFileAsync`, the tool only translates:

- The file must be inside the run's project folder (`ProjectCatalog.Normalize` on both sides,
  `ProjectCatalog.IsInside`); the gateway's data directory is forbidden separately.
- Neither the file nor the folders between it and the project root may be a symbolic link or
  junction: the path is checked as a string, while the OS follows links, and a link inside the
  project pointing outward would hand over someone else's file.
- The extension is from a whitelist in code (`.md .txt .json .cs .js .html .zip .7z` — as a
  document, `.png .jpg .jpeg` — as a photo). An archive's contents are not inspected: anything the
  agent could reach may travel inside it, files outside the project included.
- Size and caption — per the channel's `ChannelLimits`. Refusals go into the `file.send` audit
  with outcome `refused`.

The tool is passed in `--allowedTools mcp__tg__send_file` so the CLI doesn't call
`mcp__tg__approve` on every send: the gateway checks the folder, type, and size itself.
Verified on CLI 2.1.261 with a scratch instance: `tools/list` returns both tools, `claude -p`
with this flag calls `send_file` directly and never `approve` (`permission_denials` is empty).
After a CLI update recheck the same way: a card for `tg:send_file` means the flag stopped working.

## Image from chat (`--add-dir`)

The gateway downloads a picture the user sends (`AttachmentInbox`), puts the absolute path into
the prompt and opens the folder to the CLI with `--add-dir`. The agent sees the picture only when
it calls `Read` on that path itself — that is why the prompt spells the instruction out.

Layout: `<data directory>\inbox\<chat>\<project>\`. `--add-dir` gets the **chat** folder, not the
data directory (secrets and the MCP token live there) and not the project subfolder: `/project`
may change while the message waits in the queue, and the path in the prompt is already written.
The project subfolder exists so that `/new` in one project does not wipe another one's pictures.

The policy is the inbound mirror of `OperatorConsole.SendFileAsync`:

- Only PNG and JPEG, and the type is decided by the **signature bytes**, not by the MIME type or
  the file name — both are written by the sender (a `.png` with text inside is refused).
- The file name is generated by the gateway (`<timestamp>-<8 hex>.<ext>`); Telegram's own name may
  hold `..\` or a reserved device name.
- Size is capped by `Gateway:Attachments:MaxBytes` and by the channel (`ChannelLimits.AttachmentBytes`
  — Telegram hands a bot files up to 20 MB); the declared size is checked before the download, the
  actual bytes during it.
- Anything else (voice, video, a non-image document) gets a polite refusal and never reaches the
  agent's queue.

The accepted file and every refusal go into the `file.receive` audit.

Cleanup: everything older than `RetentionHours` (24) is swept at startup and before each new
attachment, and the project subfolder is deleted when the session is closed (`/new`, the «Новая»
button, a session lost by the CLI). The folder is per project, not per session: `/new` also takes
away the pictures of the project's other sessions, the ones `/sessions` can switch back to. A
picture may be gone when the agent tries to `Read` it again — it gets a read error and says so.

Verified on CLI 2.1.261: `claude -p … --add-dir <folder> --permission-mode default` reads a PNG
from that folder **without** an `mcp__tg__approve` card (`permission_denials` is empty) and
describes it correctly. Recheck after a CLI update. A hand-made 116-byte PNG came back as «media
removed — rejected by API»: real screenshots go through, an artificial one in a test may not.

## Startup and event stream

`ClaudeBackend.BuildArguments` always passes `-p <prompt>`, `--output-format stream-json
--verbose` (without `--verbose` `-p` writes no stream), `--permission-prompt-tool`,
`--permission-mode`, `--mcp-config` and `--allowedTools mcp__tg__send_file`. Conditionally:
`--resume <id>` or `--session-id <uuid>`, `--add-dir` (only if the chat folder exists), `--model`,
`--effort`. A вопрос gets its own set — "A question with no session" below.

`ClaudeBackend.StreamCollector` parses stdout line by line (`ClaudeStreamEvent.Classify`): tool
calls → `IAgentRunObserver.Activity`, everything else is discarded — in a long run that's
megabytes. The result is the last line, `"type":"result"`, in the shape of `--output-format json`;
non-JSON lines (an update banner) go into the error text. The CLI often leaves the error text in
stderr and sends `result` empty (as with «No conversation found» on a broken `--resume`), so the
session-reset and limit checks look at stderr too.

After the process exits, stdout and stderr get `DrainGrace` (5 s) to end, then the result is
built from what was read. A background process that inherits the CLI's output pipe holds it until
it exits, and a bare `await stdout` would hang the task lane forever — the run timeout and
`/stop` don't reach past the process exit. CLI 2.1.292 kills what its Bash tool started with `&`
on exit, so a live run did not reproduce the hang (a fake CLI did); the grace guards what escapes
that (a detached `Start-Process`, `start /b`, another CLI version). The pipe is left open and read
idly: closing it would break the background process on its next write.

Every `claude` is put into `ClaudeProcessJob` — one Windows Job Object for the gateway's lifetime
with `KILL_ON_JOB_CLOSE`. The gateway exiting (a `Stop-Process` included) kills every `claude` and
everything it started, background dev servers too; before this a restart left `claude.exe`
finishing the task with nobody listening and writing into the session the new instance resumes. A
failed `AssignProcessToJobObject` is logged once and the run goes on. Outside Windows the class
does nothing — a container gives the same.

A run in progress is recorded in `state.json` (`GatewayState.ActiveRun`, a вопрос — in
`ActiveQuestion`, since it runs in parallel): `ChatWorker` sets `BeginRun` before starting and
`EndRun` in `finally`. If the record is still there at startup, the previous instance died
mid-run, and `StartupNotice` sends «🔌 Шлюз запущен» to everyone in `IChatChannel.AllowedUsers`,
tells the chat of the interrupted run (`ActiveRun.ChatKey` via `IChatChannel.ParseChat`) that it
was interrupted («напишите „продолжай“»; for a вопрос — «задайте его снова через /ask») and writes
`run.end` with outcome `interrupted`. Telegram does not let a bot write first to a user who hasn't
written to it: a failed send is logged at Debug.

In the chat — `RunStatusMessage`: one «Работаю…» message, edited every 4 s (elapsed time, call
counter, the last three steps, subagents marked `↳`). `Report` from the stdout stream only
records; the network I/O runs in its own loop, and `DisposeAsync` waits for it (≤10 s), otherwise
an edit would race a deletion. The status shows one short tool argument
(`ClaudeStreamEvent.Describe`): the full Edit/Write input is file content.

## Sessions

Sessions are keyed by the normalized project path (`ProjectCatalog.Normalize`): `--resume` only
works in the folder where the session was created, so switching projects also switches the active
session. `ChatWorker` fixes the session and `ProjectPath` in `AgentRunRequest` before starting —
otherwise switching mid-run would split the working directory from the session's project.

The new session's id is issued by the **gateway** (`NewSessionId` → `--session-id <uuid>`) and
registered via `IAgentRunObserver.SessionStarted` right after the process starts: otherwise
`/stop`, a timeout, or a crash on the first run would lose the session. The session is reset
only when the CLI explicitly says it couldn't find it (`LooksLikeMissingSession` →
`AgentRunResult.SessionLost`), and only inside `ChatWorker.SettleSession` via
`TrySetSessionId(onlyIfActive)` — so as not to overwrite a `/new` or a session switch made
during the run.

`/sessions` shows up to the last 8; the button carries the `ShortId` (8 characters), not the
position: a run finishing between rendering and the press would shift the numbers.

## Context: fill, breakdown, compaction

Checked on CLI 2.1.261.

- **Fill.** The `usage` of the `result` line is the sum over all turns, not the context size. The
  size is the `usage` of the **last** `assistant` event with `parent_tool_use_id: null`:
  `input_tokens + cache_read_input_tokens + cache_creation_input_tokens`. Events with
  `"model": "<synthetic>"` are answers of local commands with zeros in `usage` — skipped. The
  window is `modelUsage.<model>.contextWindow` of the result (1 000 000 for `sonnet-5`, 200 000 for
  `haiku-4-5`); `canonicalModel` next to it is the name without the build date.
- **Model aliases.** Aliases resolve to the latest version of the family, all with 1 000 000 and
  no `[1m]` suffix (CLI 2.1.283): `sonnet` → `claude-sonnet-5`, `opus` → `claude-opus-5-5`,
  `fable` → `claude-fable-5-1`. So the menu buttons are the aliases themselves and go to `--model`
  as-is: a new version reaches the chat with a CLI update, no gateway release needed. The version
  on the screens comes from the answer (`state.json`, `ModelVersions`: alias → a `PrimaryModel` of
  the same family), before the first answer from `ClaudeCapabilities.KnownVersions`. A version
  without the prefix (`/model opus-5-5`, as in the footer) is not an alias:
  `ClaudeCapabilities.CliModel` adds `claude-` to it. CLI 2.1.261 silently replaced `fable` with
  `claude-opus-5`; `claude-opus-5-5` needs CLI 2.1.280+. A model released after the CLI
  (`claude-sonnet-5-5` on 2.1.283) is let through with an `unrecognized_model` warning on stderr
  and a 200 000 window; the alias is moved onto it by `ANTHROPIC_DEFAULT_SONNET_MODEL` — see
  `claude-permissions.md`, "Hooks".
- **Which model answered.** `modelUsage` mixes the main thread with subagents and haiku's chores
  (a sonnet subagent often writes more output tokens than the session), so the answering model is
  `message.model` of the last `assistant` event with `parent_tool_use_id: null`, and its window is
  looked up in `modelUsage` by that name. The names differ: with `--model claude-opus-5-5[1m]` the
  event says `claude-opus-5-5` while the key is `claude-opus-5-5[1m]`; for haiku the key carries
  the build date (`claude-haiku-4-5-20251001`). They are compared without the `[…]` suffix, the
  `claude-` prefix and the date. Checked on CLI 2.1.283.
- **`/context` in `-p`** works with `--resume`: the model is not called (`num_turns: 0`, empty
  `modelUsage`), the answer is markdown in `result` — a header with `Tokens: 46.6k / 200k (23%)`, the
  «Estimated usage by category» table, then per-tool tables for MCP and skills, hundreds of lines.
  `ClaudeBackend.ContextSummary` cuts everything after the category table. The numbers depend on the
  run's flags (`--mcp-config`, `--model`), so the breakdown runs with the same arguments as a task.
- **`/compact` in `-p`** works with `--resume` and keeps the session id. It emits
  `{"type":"system","subtype":"compact_boundary","compact_metadata":{"pre_tokens":…,"post_tokens":…}}`,
  the `result` text is empty, `num_turns: 0`, but `modelUsage` is not — compaction is a model call
  and spends the plan. After it the context size is `post_tokens` until the next real turn.
- **Cache.** Checked on CLI 2.1.292 with `claude -p --resume`, the way the gateway runs:
  - The main thread writes the cache with a **one-hour** lifetime (`usage.cache_creation.ephemeral_1h_input_tokens`),
    sub-agents with a **five-minute** one (`ephemeral_5m`). Billing past the subscription is said to
    drop the main thread to 5 minutes too, so the lifetime is read from the event. An event can
    carry both (after a web search the CLI adds a five-minute entry for the tail) — then the hour
    counts.
  - The lifetime runs from the **start** of the request that read or wrote the entry; every read
    extends it for free. The stream gives a turn only when it is complete, so the backend takes the
    time of the last main-thread input (a `user` event without `parent_tool_use_id`, or the process
    start) as the request start: a little early, so «🧊 кэш до …» under the answer and on the «Кэш» line errs on the safe side.
    `AgentRunResult.LastRequestUtc` + `CacheTtl` → `SessionRecord.CacheUntilUtc`.
  - The cache is a prefix match: tools → system prompt → history. A new process with `--resume`
    keeps it; so do commits, edited files, `--add-dir` appearing, `--effort` and the modes
    `default`/`acceptEdits`/`auto`. **Lost:** a model switch (caches are per model, and the other
    model's thinking blocks are dropped from the history) and `--permission-mode plan` (different
    tools — nothing is read at all; `OwnCacheModes` in `ClaudeCapabilities`). A compaction replaces
    the start of the session, so the old cache does not count after it (`LastRequestUtc = null`).
  - A run with `--resume <id> --fork-session --no-session-persistence` reads the whole cached prefix,
    extends it by another hour and leaves no turn in the session (checked over 110 minutes).
- Testing from Git Bash: MSYS turns a leading `/context` into `C:/Program Files/Git/context`, and
  the model gets a path instead of a command. Use `MSYS_NO_PATHCONV=1` or PowerShell;
  `ProcessStartInfo.ArgumentList` in the gateway is not affected.

## A question with no session (`Kind = Question`)

`--no-session-persistence` (only with `-p`: nothing is written to `~/.claude/projects`, `--resume`
is impossible), no `--resume`/`--session-id`, `--tools WebSearch,WebFetch` (built-ins only; `""`
disables all) and `--strict-mcp-config`: only the gateway's own `tg` server from `--mcp-config` is
loaded — the user's MCP servers cost ~40k tokens of context per question otherwise (48k vs 9.6k on
a one-word answer). `--permission-mode` and `--permission-prompt-tool` are passed as always; no
`--add-dir`. The working folder is an empty `%TEMP%\AgentsTracker-question`: not the project (its
CLAUDE.md would load) and not the data directory (secrets).

A вопрос runs in its own lane, **in parallel** with a task (`architecture.md`, "Two lanes"). Its
permissions are unchanged: cards and `send_file` (checked against the chat's project) work as
before, the two processes' cards told apart by the run id in the `X-AgentsTracker-Run` header of
each run's own `--mcp-config` file. There is no `Agent` tool among `--tools`, so a вопрос has no
subagents.

## Plan limits

The only limiter is `IAgentLimits`, with windows `five_hour`, `seven_day` and the per-model ones
(`seven_day_<model>`, `five_hour_<model>`). `ChatWorker.ProcessAsync` checks it before a run
(not before the `/context` breakdown: it costs nothing). The remaining budget on one line —
`ShortSummaryAsync` (menu summary); the windows for the gauges — `ViewAsync`. `LimitGauge.Used`
is the **consumed** fraction 0..1 as a `decimal`: the gauges on `/status` and the meters in the
monitor fill with consumption, a full bar means the window is exhausted. Percentages come
**only** from `LimitMath` (`Percent`, `Left`); the monitor page has no formula of its own — why,
`architecture.md`, "Numbers".

`ClaudeLimits` calls the **undocumented** `api.anthropic.com/api/oauth/usage` with the token
from `CLAUDE_CODE_OAUTH_TOKEN` or `~/.claude/.credentials.json` (an expired one is refused; the
gateway never refreshes it — that is Claude Code's job). The endpoint requires a plausible
User-Agent, responds 429 on frequent polling (3 min cache), and can disappear in any version. One
attempt, 15 s. On any error the run is **skipped**, not blocked, otherwise the gateway would go
completely silent. Fields are read via `ClaudeLimits.Number` with a `ValueKind` check:
`TryGetDecimal` on `null` throws, and `utilization: null` would reach the user as «Внутренняя
ошибка шлюза».

The scale of `utilization` (percent 0..100 or a fraction 0..1) is decided for the **whole
response at once** — `ClaudeLimits.LooksLikePercents`: any value of 1 or above, or all values
integral, means percent; only fractional values below one mean fractions. One window cannot tell
them apart: `utilization: 1` is both "1% after the reset" and "fully consumed" (decided per window,
the bot once spent an hour refusing runs on an **empty** five-hour window). An exact 1 counts as
percent on purpose: the fraction reading would produce that very refusal in an ambiguous response.
The price: on the fraction scale an exhausted window does not hold a run back — the CLI itself
runs into the limit.

Credits ("extra usage") are forbidden to the agent: `ClaudeBackend` sets
`DISABLE_EXTRA_USAGE_COMMAND=1`, and when a run is aborted by the limit, `ChatWorker` clears the
whole queue. Credits switched on in the account are logged as a warning — only the account owner
can turn them off.

## Skills and plugins

`ClaudeSkillCatalog` collects what the CLI sees: `skills/*/SKILL.md` and `commands/**/*.md` from
the project's `.claude` and `~/.claude`, plus enabled plugins (`~/.claude/plugins/installed_plugins.json`;
`enabledPlugins` layers profile → `.claude/settings.json` → `settings.local.json`, no entry means
enabled). `user-invocable: false` ones are hidden. The scan is cached for 5 s; «🔄 Обновить» is
`Refresh()`.

The «🔌 Плагины» screen (`ClaudePluginRegistry`) toggles `enabledPlugins[name@marketplace]` only
in the personal `~/.claude/settings.json`, where the CLI's own `/plugin` writes too. The file is
rewritten in full via `JsonNode` (LF, no `\u`, through a temp file — a CLI session may be reading
it), comments won't survive. A value from the project layer is marked 🔒 (`PluginInfo.LockedBy`)
and can't be changed from chat. The button carries the plugin's key, not the desired state — it
flips the current one. Takes effect from the next `claude -p`.

Built-in CLI skills can't be enumerated: the «Встроенные» group comes from
`Gateway:Claude:BuiltInSkills` (default in `ClaudeOptions` for CLI 2.1.x; after a CLI update,
change the config, no rebuild). «С аргументами»: `SkillLauncher.Expect` remembers the command
against the user, and `SkillArgumentsTextHandler` turns the next text into `/command text`.
`SkillUsage` counts runs (the `@bot` suffix is stripped) for «⭐ Частые» (top five).

## Locating the binary

`claude.exe` is located by `ClaudeCliLocator`: `Gateway:Claude:Executable` → standard paths
(`~/.local/bin`, `~/.claude/local`, the global npm folder) → PATH → the VS Code extension's binary
(only to get it running; the path with the extension version disappears on update — a warning is
logged). The standard setup is a separate CLI (`irm https://claude.ai/install.ps1 | iex`). The
path is cached but rechecked before every run. `ValidateStartup` logs the version — the JSON
parsing depends on a specific version.

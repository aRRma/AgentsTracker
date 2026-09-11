# CLAUDE.md

Notes for Claude Code working in this repository. This file holds the rules and the map; the
details are in `docs/en/`:

- `docs/en/use-cases.md` — what non-programmers use the bot for: five everyday scenarios.
- `docs/en/safety.md` — the risks of that, in plain words, and what closes each one.
- `docs/en/operations.md` — restarting the gateway, scratch instance, worktree, tooling.
- `docs/en/architecture.md` — how the gateway is built: the annotated project tree, the message
  path, the gateway ↔ CLI loop, sessions, state and secrets, audit, chat output.
- `docs/en/extending.md` — checklists for adding: a chat command, a settings screen, a feature, an
  agent, a channel, a project, an MCP tool, a monitor endpoint, an exe command, an autostart
  mechanism, a config key.
- `docs/en/deployment.md` — production run: publish, install/uninstall, Docker, ports, secrets.
- `docs/en/cli-contract.md` — the CLI contract: approvals, stream-json, sessions, limits, skills.
- `docs/en/glossary.md` — the glossary: one Russian word per concept for buttons, messages, the
  monitor and the docs, plus the synonyms not to use.
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

**The working gateway on this machine is the published release** (`C:\AgentsTracker-win-x64`; a
release archive unpacks as `AgentsTracker-<version>-win-x64`, so check the path with
`Get-Process AgentsTracker.Gateway`). Do not stop it and do not rebuild over it:
that is the stable service the user chats with, and new code reaches it only through a new release. A
change is checked live by a **scratch instance from `bin\Debug` on its own ports** — a separate build
folder, `Gateway__McpPort`/`Gateway__MonitorPort` other than `5099`/`5100`, a fake bot token and its
own `Gateway__DataDirectory`; the recipe and the cleanup are in `docs/en/operations.md`. When the
gateway does run from `bin\Debug` of the main folder (`AgentsTracker`, branch `master`; there is no
`main` branch), `dotnet build` fails with `MSB3021` while it runs and switching branches swaps the
sources out from under the process. From a session started from Telegram you must not restart it —
hand the commands to the user instead.

## Layout

```
src/AgentsTracker.Agents.Abstractions/   agent contracts, no Telegram and no specific CLI
src/AgentsTracker.Agents.Claude/         Claude Code behind those contracts, Mcp/ — the tools the CLI calls
src/AgentsTracker.Channels.Abstractions/ channel contracts, no specific messenger
src/AgentsTracker.Channels.Telegram/     Telegram behind those contracts
src/AgentsTracker.Gateway/               Program.cs (agents, channels, features), Domain/, Infrastructure/,
                                         Features/ — vertical slices: Approvals, Chat, Settings, Help, Audit, Monitor
```

What each file inside them is for — `docs/en/architecture.md`. Layering rules, and breaking one is
an architecture error rather than a typo: `Domain` and `Abstractions` do not open files and do not
go over the network; `Gateway` knows about a specific agent and channel only in `Program.cs`; the
backend and the channel know nothing about each other or about `GatewayOptions`; `Infrastructure`
knows nothing about features (except the `Dispatch` contracts); a feature depends on a feature only
through a public service (`Settings` → `ChatWorker.IsBusy`).

**Adding anything** — the checklists in `docs/en/extending.md`, do not do it from memory: a
half-registered command or screen either kills startup or cannot be reached from the chat. What
matters no matter what you add: the lists of agents, channels and features live only in
`Program.cs`, `ChatModule` stays last there, and a slash command duplicated across two features
kills startup.

### How a message travels

`ChatDispatcher` lets through only `IChatChannel.AllowedUsers` and private chats. A slash command
from `IChatCommandHandler.Commands` is matched **before** the text handlers — otherwise `/stop`
would go to a waiting free-form answer and there would be nothing left to interrupt a stuck run
with. Then the `IChatTextHandler` chain in module order: an answer to a card → skill arguments →
into the agent's queue (always `true`, hence `ChatModule` last). Unknown slash commands are Claude
Code's own, they go to the CLI. Buttons: the `IChatButtonHandler` chain, `cfg:` — the menu,
everything else — approvals.

A handler receives the whole `IncomingMessage`: besides the text it may carry attachments. Pictures
go through `AttachmentInbox` into `<data directory>\inbox\<chat>\<project>\`, and the run gets that
chat folder in `--add-dir`. A command wins over a picture — `/stop` must work with a picture
attached, and then the picture is not saved and the user is told so rather than left guessing.
Details — `docs/en/architecture.md`.

### The gateway → CLI → gateway loop

The gateway both launches the agent and serves it: `claude -p` asks for permissions back through
the gateway's own MCP server (`http://127.0.0.1:<McpPort>/mcp`, a token in the header,
`--permission-prompt-tool mcp__tg__approve`), the question becomes a card in the chat, the button
becomes the answer. The diagram, the token file and the timeouts — `docs/en/architecture.md`; the
undocumented parts of the contract (`updatedInput`, «Всегда», the truncated tail) —
`docs/en/cli-contract.md`.

- `--bare` is not allowed: it does not read `~/.claude` and breaks the subscription OAuth login.
- `--allowedTools` holds only `mcp__tg__send_file`: its policy belongs to the gateway. Do not add
  other tools there — that bypasses the cards past the machine's config.
- `--add-dir` carries exactly one folder — the chat's `inbox`. Never the data directory itself
  (`state.json`, `appsettings.Local.json` with the secrets, the MCP token) and never the project
  subfolder: `/project` may change while the message waits in the queue.
- CLI arguments go through `ProcessStartInfo.ArgumentList`, do not concatenate a string.

### `--permission-mode` is always passed

Without the flag `permissions.defaultMode` from `~/.claude/settings.json` applies — the user has
`auto` there, and no buttons show up in the chat. Do not remove the argument from
`ClaudeBackend.BuildArguments`. Only `PermissionMode.Selectable` (`plan`/`default`/`acceptEdits`/
`auto`) can be switched from the chat; `dontAsk` and `bypassPermissions` are excluded deliberately —
turning approvals off entirely stays a config edit on the machine. The values are checked by
`ValidateFor(capabilities)` in `ValidateStartup`, not by `GatewayOptions.Validate` (the agent is not
chosen yet).

### Sessions, state, audit

Sessions are keyed by the **normalized project path**; the chat-side selection (`SessionStore`)
stores a value equal to the config as `null`, otherwise a config edit would be silently overridden
by an old selection. State, secrets and the `audit\` journal live in `%LOCALAPPDATA%\AgentsTracker\`,
the config comes in layers up to the `Gateway__*` environment variables. The audit gets what a human
is answerable for ("who, where, what" — no secrets, no full texts, addresses as `channel:value`
keys, kinds in `AuditKinds`), the log gets what is needed for debugging. All three —
`docs/en/architecture.md`, the user-facing side of the config — `docs/en/deployment.md`.

### Limits and money

The only limiter is `IAgentLimits` (the plan windows), checked in `ChatWorker.ProcessAsync` before a
run; if the poll fails, the run is **skipped**, not blocked. There is no money in the gateway:
`total_cost_usd` is not read, `--max-budget-usd` is not passed, the statistics show only turns,
tokens and time. Do not return dollar estimates. Credits ("extra usage") are forbidden to the agent.
Details — `docs/en/cli-contract.md`.

A false refusal is diagnosed from the outside: `limit.refused` in `audit\audit-<YYYY-MM>.jsonl` (when
and how many times) and `monitor-api.ps1 "api/limits"` (the parsed windows with `resetsAt`). The
endpoint's raw answer is out of reach from a session — a direct HTTP call, reading
`~/.claude/.credentials.json` and grepping `claude.exe` are all denied; reason from the parsed
numbers instead, remembering that usage inside one window never falls until the reset.

### Chat output

`MarkdownRenderer` converts markdown into `ChatHtml` and cuts it to `IChatChannel.Limits`
(`MessageLength`, 3800 for Telegram) — cut the **source markdown before the conversion**, otherwise
tags get torn apart. A code block longer than the limit is sent as a file. Any 400 from the channel
makes the send retry itself without markup, a 429 is waited out and the same part repeated
(`RateLimitRetry.OnceAsync`) — otherwise part of the answer would vanish silently. Sums, tokens and
time — `DisplayFormat`. The renderer's own quirks (italics, `snake_case`, escaping budget) —
`docs/en/architecture.md`.

## Keep in mind

- The gateway **does not attach** to a VS Code session — it is a parallel session on the same folder:
  shared `CLAUDE.md`, settings, hooks and MCP, but its own history.
- Barriers: the channel's `AllowedUsers` plus private chats only; MCP is always `127.0.0.1` plus a
  token in the header; the monitor has no token — that is why it is read-only, and `MonitorBind: any`
  (needed in a container) is published only on the host's `127.0.0.1`.
- `HttpClient` only through `IHttpClientFactory`; the retry rules differ per client and are spelled
  out in `docs/en/architecture.md` — a blind retry on a Bot API POST is a duplicate in the chat.
- **Fractional numbers are `decimal`**, never `double`/`float` — including what is read out of JSON
  (`TryGetDecimal`). Rounding a share into percent happens in exactly one place,
  `LimitMath.Percent`/`Left`; do not write a second formula, and do not compute percentages in the
  monitor's JS — `/api/limits` sends them ready-made. `double` is left only where the BCL itself
  counts that way (`TimeSpan.Total*`) and the result goes straight into a caption. Why —
  `docs/en/architecture.md`, "Numbers".
- A release is a `v*` tag push, and the release notes go into `docs/release-notes/<tag>.md`
  **before** the tag, otherwise a list of commits ends up in the release. The notes are written
  **short and in plain human words**, for the person using the bot: what changed and what it gives
  them, only the essentials — no reasoning, no class names, no invisible changes. The rest —
  `docs/en/deployment.md`.
- The wiki is built from `README.md` and the Russian `docs/*.md` by `scripts\sync-wiki.ps1` and is
  never edited by hand; a new file in `docs/` reaches it only through the script's `$pages` table.
- Edit Russian texts and Windows paths with Edit/Write, not with a heredoc from Bash. Sources are
  UTF-8 **without a BOM**. The rest about tooling — `docs/en/operations.md`.
- Comments explain which failure the code prevents, not what it does.
- From a session over Telegram `AskUserQuestion` and `ExitPlanMode` wait `ApprovalTimeoutMinutes`
  (15 min): with no answer, take the recommended option.
- The monitor at `http://127.0.0.1:5100` is unreachable from a session via `curl`/`Invoke-RestMethod`
  — they are in `deny` for any address, do not try to work around it. A snapshot and the other
  `/api/*` — `pwsh -File scripts\monitor-api.ps1 "api/snapshot"` from the **PowerShell** tool, no
  leading `/` (why, plus screenshots and the page itself — `docs/en/monitor.md`). Port `5100` is the
  **release** instance: it shows old code, your own change is on the scratch instance's port.
- From a session over Telegram the MCP tool list comes from the **running** gateway: after a change
  to `send_file` (allowed types, description) this session still sees the release's old version —
  check such a change with the harness or a scratch instance, not by calling the tool.

## Как работать

Rules from the user. They used to live in memory, here they are safer — the file is read by every
session.

- **Answers stay short and simple.** The result first, then only what is needed to act. No retelling
  of the steps or the plan, jargon in plain words. Errors, risks and "what's next" are never cut.
- **Long documents go into an `.md` file.** A plan, a review report, an analysis, a comparison —
  anything longer than a couple of paragraphs: temporary in the scratchpad, permanent in `docs/`
  (only if it really is documentation). A Latin, kebab-case name. The answer carries the gist and the
  link; from Telegram — the file into the chat through `mcp__tg__send_file` (from the project folder
  only: `.md .txt .json .cs .js .html`, archives `.zip .7z`, screenshots `.png .jpg .jpeg`).
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
- **A review in a worktree needs the branch name.** `/code-review` without an argument works on the
  main folder: its uncommitted diff, or — when the tree is clean — the commits ahead of
  `origin/master`, never the worktree branch. On 07.09.2026 it reviewed and fixed another session's
  changes in `master` that way. Call `code-review medium --fix <branch>`.
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
- **Terminology comes from the glossary.** Every text a person sees — a button, a screen title, a
  chat message, a monitor caption, a line of documentation, a release note — takes its words from
  `docs/en/glossary.md`: one Russian word per concept, and the table's right column lists the
  synonyms that are not used. A new concept goes into the glossary (both languages) before the code
  that uses it. The traps that cost the most: a working folder is «проект» and never «репозиторий»;
  the user writes a «задача», the agent performs a «запуск» (never «прогон»); the card is a
  «карточка подтверждения», while «режим» means only `--permission-mode`; «лимит» is the
  subscription and «предел» is anything technical; «журнал» is `/audit` and «лог» is `ILogger`; a
  live check of a scratch instance is a «смоук-тест» and the instance itself is a «пробный
  экземпляр» — those two are the section titles in `operations.md`, and a third name for either
  only hides the section from search. A rename that touches the UI also ages the screenshots in
  `docs/images/*.png` used by the README: they cannot be retaken from a session (the monitor on
  `5100` is the release with the old code, the bot shots need a live chat), so name the stale ones
  to the user instead of leaving them. Already published `docs/release-notes/*` are history and are
  never re-worded — exclude them from a glossary sweep.

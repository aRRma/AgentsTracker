# Operations on the development machine

When to read this: you need to restart the gateway, check a change live, or run a long task
without breaking the running process.

Commands come in two blocks: PowerShell first, bash second (Git Bash). One block means the same
command in both shells.

## Which instance is running

`Get-Process AgentsTracker.Gateway` shows the path, and everything else follows from it:

- **a published release** (`C:\AgentsTracker-<version>-win-x64`) — how this machine is set up now:
  the stable service the user chats with. Do not stop it and do not rebuild over it; a commit
  changes nothing in the chat, new code gets there only with the next release
  (`docs/en/deployment.md`). A change is checked live by a scratch instance, see below;
- **`bin\Debug` of the main folder** — then the working gateway is the one being developed, and
  it is restarted after a commit as described here.

## Restarting the gateway

Applies when the exe was started manually from `bin\Debug` and there is no Task Scheduler task.
While it runs, `dotnet build` fails with `MSB3021` (the exe is locked). Both stop commands below
(`Stop-Process`, `taskkill`) match by name only: with a release running alongside they take down
the working service too — filter by `Path`.

```powershell
git status                                                # someone else's uncommitted changes may not build
Get-Process AgentsTracker.Gateway | Stop-Process -Force
dotnet build src\AgentsTracker.Gateway
Start-Process src\AgentsTracker.Gateway\bin\Debug\net10.0\AgentsTracker.Gateway.exe
```

```bash
git status                                                # someone else's uncommitted changes may not build
taskkill //F //IM AgentsTracker.Gateway.exe
dotnet build src/AgentsTracker.Gateway
cmd //c start "" "src/AgentsTracker.Gateway/bin/Debug/net10.0/AgentsTracker.Gateway.exe"
```

- The working directory does not matter: `Program.cs` sets `ContentRootPath =
  AppContext.BaseDirectory`, otherwise an exe run from another folder would silently lose
  `appsettings.json`. Check the `Content root path` line in the log.
- Build without stopping the gateway: `dotnet build src\AgentsTracker.Gateway -o <folder>` (the
  project: `-o` on the `.slnx` gives NETSDK1194). To check one class, use a temporary console
  project referencing the built dll (`<Reference>` + `HintPath`), not `ProjectReference`, which
  would try to rebuild the locked `bin\Debug`.
- The main folder does not build because of someone else's changes, and the gateway is already
  stopped: build a clean HEAD into the same folder —
  ```
  git worktree add --detach ../AgentsTracker-run HEAD
  dotnet build ../AgentsTracker-run/src/AgentsTracker.Gateway -o src/AgentsTracker.Gateway/bin/Debug/net10.0
  git worktree remove --force ../AgentsTracker-run
  ```
- Do not restart from a session the gateway itself launched (from Telegram): `Stop-Process` would
  kill the current `claude -p` too, and a deferred `schtasks /SC ONCE` did not work. Give the user
  the commands above and ask them to run them by hand.
- Stopping the gateway kills all its `claude` processes and everything they started (the Job
  Object, `cli-contract.md`): a dev server or an emulator the agent brought up must be started
  again.
- The console outputs Russian text in cp866 — view the log in PowerShell (Git Bash has no `iconv`).
- After startup the bot posts «🔌 Шлюз запущен» ("Gateway started") with the build version
  (`0.0.0-dev` from source) — it shows the new exe came up. If the restart hit a running task, its
  chat also gets «⚠️ Предыдущий запуск прерван перезапуском шлюза» with a hint to write
  «продолжай».

## Scratch instance

Any config key can be overridden by an environment variable, so a second exe can run beside the
working one. While the release is the working instance this is the only way to see a change live:
the scratch instance is built into a separate folder and started on its own ports, so it touches
neither `bin\Debug` nor the working service.

```powershell
dotnet build src\AgentsTracker.Gateway -o $env:TEMP\at-build
# fake token, but in the <number>:<string> format, otherwise the channel is not created
$env:Gateway__Channel__Settings__BotToken = '123456789:AAFakeTokenFakeTokenFakeTokenFakeTok'
# any number (an array element is an index at the end of the name); an empty list fails validation
$env:Gateway__Channel__Settings__AllowedUserIds__0 = '1'
# not the working ports, those are taken
$env:Gateway__McpPort = '5198'; $env:Gateway__MonitorPort = '5199'
# own state.json, and the production appsettings.Local.json is not read
$env:Gateway__DataDirectory = "$env:TEMP\at-scratch-data"
# an existing folder: validation requires it, and the production config that has it is not read
$env:Gateway__ProjectPath = 'C:\Users\aRRma99\source\repos\ME\AgentsTracker'
& "$env:TEMP\at-build\AgentsTracker.Gateway.exe"
```

Put those lines in a scratchpad script and start it with `pwsh -File` in the background: variables
set by the PowerShell tool do not survive to the next call, and the exe holds the console. Stop it
by path, not by name — `Get-Process AgentsTracker.Gateway | Where-Object { $_.Path -like
"$env:TEMP*" } | Stop-Process -Force` — otherwise the working release goes down too; then delete
the `Gateway__DataDirectory` folder.

With its own `DataDirectory` the scratch instance neither disturbs the working gateway nor depends
on the shape of its config. Without it `state.json` is shared: do not start the scratch instance
while a task is in progress.

With a **fake** token the scratch instance lives about a minute: `getMe` gets `Unauthorized`
(`ChannelFailure.Unauthorized`), and after `UnauthorizedAttempts` (6, ten seconds apart) the host
writes «не принимает токен бота» and exits with code 1. A network failure is different: the host
waits 10 s, 20 s… up to 5 min between attempts and never gives up, so a scratch instance with an
unreachable `Proxy` lives until stopped. A minute is enough for startup, the config and the
monitor, and the working bot notices nothing. Anything longer, or a chat scenario, needs a
**separate test bot** from @BotFather: the working bot's token gives the second process a 409 on
`getUpdates`, and the release loses its polling. The MCP config never conflicts: each instance
has its own, `mcp-gateway-<pid>-<run>.json` per run.

The scratch instance's monitor (`http://127.0.0.1:5199`) is the only place to see a change to the
page: port `5100` belongs to the release and shows the old code. From a session read it with
`monitor-api.ps1 "api/snapshot" -Port 5199`. Screenshots — `docs/en/monitor.md`.

Smoke test after infrastructure changes: a scratchpad script, `pwsh -File`; after ~8 s check:

- `/api/snapshot` — the `version` (gateway build), `agent`, `cliVersion` fields;
- `/api/limits`;
- 404 on `/` of the MCP port (the monitor must not open there), 403 on the monitor port with a
  foreign `Host` header;
- 401 on `POST /mcp` without a token, with the headers `Content-Type: application/json` and
  `Accept: application/json, text/event-stream`, otherwise you get 415.

## Checking host logic without Telegram

Everything that does not need a live bot is checked by a file-based C# app: the gateway's own
classes with fakes instead of a channel — no token, no ports, no waiting.

```csharp
#:sdk Microsoft.NET.Sdk.Web
#:project C:/Users/…/src/AgentsTracker.Gateway
```

Call `AppPaths.UseDirectory(<a temp folder>)` before anything else, then
`Options.Create(new GatewayOptions { … })`, `NullLogger<T>.Instance` and your own
`IChatChannel`/`IAuditLog` (other channel methods — `throw new NotSupportedException()`). Put the
file in the scratchpad and run `dotnet run check.cs`. One run covers, for example, incoming-file
handling: signature sniffing, size caps, the retention sweep, refusals and the audit records.

A junction left in the temp folder by a previous run makes `Directory.Delete(recursive: true)`
fail with «Access to the path 'link' is denied» — remove the link itself before the folder.

A harness that drives the real CLI (`ChatWorker` + `ClaudeBackend`) also needs
`#:property PublishAot=false` and `#:property JsonSerializerIsReflectionEnabledByDefault=true`
(`McpConfigFile` serializes by reflection), plus a stub MCP server on the harness's `McpPort` with
an `approve` tool (`AddMcpServer().WithHttpTransport().WithTools<…>()`, `MapMcp("/mcp")`): without
`mcp__tg__approve` the CLI does not start. Use `haiku` to spare the limits.

## Larger tasks — use a worktree

The main `AgentsTracker` folder stays on `master` (there is no `main` branch): switching the
branch there swaps the sources out from under a gateway running from `bin\Debug`, and a broken
build leaves you unable to restart it. The rule holds even with the release as the working
instance, because a scratch instance is built from the same folder.

```
git worktree add ../AgentsTracker-<task> -b <branch>   # the main folder stays the gateway's folder
```

Work in the new folder (from chat — `/project`, it has its own sessions). Merge in two steps:
`git merge master` in the worktree (conflicts, build and smoke test happen there), then
`git merge --ff-only <branch>` in the main folder, which changes in one atomic step. Run
`git status` before merging and before stopping the gateway: other sessions work in the main
folder in parallel. Small changes in one or two commits go straight into the main folder.

Call review skills with the branch name (`code-review medium --fix <branch>`): without an
argument they take the main folder's uncommitted diff, not your branch, and fix someone else's
work.

Traps that have already cost time or work:

- **Forward slashes only in Bash.** Git Bash turns `..\AgentsTracker-x` into `..AgentsTracker-x`,
  and the worktree ends up inside the repository.
- **Git commands through `git -C <worktree>`.** The shells and the LSP sit in the main folder:
  without `-C` the commit lands in `master`. Before a commit run
  `git -C <worktree> branch --show-current`.
- **Commit your own files by name**, not `git add -A`: the folder holds other people's notes and
  plans.
- **Stash, branches, tags and `config` are shared by all worktrees.** `git stash pop` in one
  worktree brings out the changes from another.
- **Never delete someone else's `*.lock`.** «Unable to create '…lock'» means git is running in
  another folder — repeat the command later.
- **In a detached worktree** (the scratch instance's `AgentsTracker-run`) do not commit: the
  commit goes away with the folder.
- **Remove in order:** merge → stop the scratch instance by PID → `dotnet build-server shutdown`
  → `git worktree remove` → `git branch -d`. A file in `bin\` held by a process breaks the
  removal halfway. If it failed, do not `rm -rf`: run `git worktree list` and `git status` in
  that folder.
- **Tag a release only in the main folder after the merge:** a tag in a worktree releases an
  unmerged branch.
- **Switch `/project` in the chat back before removing a worktree.** Otherwise the next task goes
  into a folder that no longer exists.

## Documentation in two languages

`docs/en/*.md` is the English original the agent reads (`CLAUDE.md` links only to it); the
neighbouring `docs/*.md` is the Russian mirror the wiki is built from (`README.md`, always Russian,
links to it). Edit both in the same commit: a document left in one language is a bug. Create a new
document in both at once, plus a `$pages` line in `scripts\sync-wiki.ps1` for the Russian file —
`docs/en/` never reaches the wiki. `CLAUDE.md` is English only, `README.md` is Russian only.

## GitHub wiki

The wiki mirrors `README.md` and the Russian `docs/*.md`. Do not edit it by hand: the pages are
rebuilt from scratch every time and manual edits are overwritten. `.github/workflows/wiki.yml`
updates them on a push to `master` that touches `README.md`, `docs/` (not `docs/en/`), the script
or the workflow itself, and on manual dispatch. The script can also run by hand:

```
pwsh -File scripts/sync-wiki.ps1 -OutDir C:/Temp/wiki-preview   # see what it would produce
pwsh -File scripts/sync-wiki.ps1                                # build and push
```

The script clears its own pages from the preview folder, so it rejects an `-OutDir` inside the
repository: `-OutDir docs` would overwrite the sources. The other parameters (`-WikiPath`,
`-NoPush`, `-Repository`, `-Branch`) are described in the script's header. If the built-in
`github.token` cannot push to the wiki, the workflow takes the `WIKI_TOKEN` secret, a personal
access token with write access to repository contents.

Page names are Latin, from the file name (`docs/monitor.md` → `monitor`), the sidebar title is the
file's first `#`. The script rewrites links to repository files and images as absolute: the wiki
is a separate git repository and cannot see relative paths into the main one. A new file in
`docs/` reaches the wiki only through the script's `$pages` table, otherwise it has no page and
no sidebar entry.

GitHub creates the first page only through the web interface: until it exists, the
`<repository>.wiki.git` repository does not exist and cloning fails with "Repository not found".

## Tools and environment

- Edit Russian text and Windows paths with Edit/Write, not a heredoc or python from Bash: `\a`,
  `\n`, `\r` in paths get silently swallowed and look like a typo. Put a script in the scratchpad
  via Write, run it as a file, check the result with `grep … | cat -v` and by building.
- Sources are UTF-8 **without BOM** (`utf-8-sig` adds one silently).
- The PowerShell tool rejects a compound command where `Remove-Item` stands next to a path under
  `C:\Program Files` (7-Zip, for example): the guard reads it as removing `C:\Program`. Split such
  a command into separate calls.
- `claude` is not on the tool shells' PATH: call it as
  `& "$env:USERPROFILE\.local\bin\claude.exe" --help` (that path does not run from Git Bash).
  Undocumented CLI behaviour is quickest to check with a bare `claude -p … --output-format json`
  and `permission_denials` in the answer — that is how `--add-dir` was verified not to raise an
  approval card.
- `git commit -F -` with a here-string gets no stdin in the PowerShell tool: put a long message
  into a scratchpad file and use `git commit -F <file>`.
- A reviewer subagent without Bash cannot run `git show`/`git diff`: dump old file versions into
  the scratchpad (`git show <commit>:<path> > …`).
- From a session over Telegram, `AskUserQuestion` and `ExitPlanMode` wait `ApprovalTimeoutMinutes`
  (15): without a response, take the recommended option. A long one-line PowerShell command in an
  approval prompt is easy to reject unread — put a multi-step check into a script and run it with
  `pwsh -File`.
- `curl`, `Invoke-WebRequest`, `Invoke-RestMethod` are in `deny`, for `127.0.0.1` too. A monitor
  snapshot from a session: `pwsh -File scripts\monitor-api.ps1 "api/snapshot"` (loopback only,
  allowed in `.claude/settings.json`) from the PowerShell tool: from Bash a leading `/` turns into
  `C:/Program Files/Git/api/…` and the script refuses the argument. The whole page — Playwright's
  `browser_run_code_unsafe`.
- `modern-web-guidance` (`npx.cmd -y modern-web-guidance@latest search "…"`) — use the PowerShell
  tool: from Git Bash `npx.cmd` silently returns empty output.
- Production install and Docker (publishing, ports, secrets, update, why a task and not a
  service) — `deployment.md`.

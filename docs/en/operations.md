# Operations on the development machine

When to read this: you need to restart the gateway, check a change live, or run a long task
without breaking the running process.

Commands come in two blocks: PowerShell first, bash second (Git Bash). Where a command is the
same in both shells, there is one block.

## Which instance is running

`Get-Process AgentsTracker.Gateway` shows the path, and everything else follows from it:

- **a published release** (`C:\AgentsTracker-<version>-win-x64`) — that is how this machine is
  set up now: the stable service the user chats with. Do not stop it and do not rebuild over it;
  a commit does not change anything in the chat, new code gets there only with the next release
  (`docs/en/deployment.md`). A change is checked live by a scratch instance, see below;
- **`bin\Debug` of the main folder** — then the working gateway is the one being developed, and
  it is restarted after a commit as described here.

## Restarting the gateway

Applies when the exe was started manually from `bin\Debug` and there is no Task Scheduler task.
While it is running, `dotnet build` fails with `MSB3021` (the exe is locked). `Get-Process
AgentsTracker.Gateway | Stop-Process -Force` has no filter: with a release running alongside it
takes down the working service too — filter by `Path`.

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
  AppContext.BaseDirectory`; without this, running the exe from another folder would silently
  lose `appsettings.json`. Check the `Content root path` line in the log.
- Build without stopping the gateway: `dotnet build src\AgentsTracker.Gateway -o <folder>` (the
  project specifically: `-o` on the `.slnx` gives NETSDK1194). To check a class separately, use
  a temporary console project referencing the built dll (`<Reference>` + `HintPath`), not
  `ProjectReference`: that would try to rebuild the locked `bin\Debug`.
- The main folder does not build because of someone else's changes, and the gateway is already
  stopped: build a clean HEAD into the same folder —
  ```powershell
  git worktree add --detach ..\AgentsTracker-run HEAD
  dotnet build ..\AgentsTracker-run\src\AgentsTracker.Gateway -o src\AgentsTracker.Gateway\bin\Debug\net10.0
  git worktree remove --force ..\AgentsTracker-run
  ```
  ```bash
  git worktree add --detach ../AgentsTracker-run HEAD
  dotnet build ../AgentsTracker-run/src/AgentsTracker.Gateway -o src/AgentsTracker.Gateway/bin/Debug/net10.0
  git worktree remove --force ../AgentsTracker-run
  ```
- Do not restart from a session launched by the gateway itself (from Telegram): `Stop-Process`
  would kill the current `claude -p` too, and a deferred `schtasks /SC ONCE` did not work. Give
  the user the commands above and ask them to run them by hand.
- The console outputs Russian text in cp866 — view the log in PowerShell (`iconv` is not
  available in Git Bash).
- After startup the bot posts «🔌 Шлюз запущен» ("Gateway started") with the build version
  (`0.0.0-dev` for a build from source) — it shows that the new exe is the one that came up;
  if the restart happened
  during a running task, its chat receives «прерван, напишите „продолжай“» ("interrupted, type
  \"continue\"").

## Scratch instance

Any config key can be overridden by an environment variable, so a second exe can be started
alongside the working one. This is the only way to see a change live while the release is the
working instance: it is built into a separate folder and started on its own ports, so it touches
neither `bin\Debug` nor the working service.

```powershell
dotnet build src\AgentsTracker.Gateway -o $env:TEMP\at-build
$env:Gateway__Channel__Settings__BotToken = '123456789:AAFakeTokenFakeTokenFakeTokenFakeTok'
$env:Gateway__McpPort = '5198'; $env:Gateway__MonitorPort = '5199'
$env:Gateway__DataDirectory = "$env:TEMP\at-scratch-data"
$env:Gateway__ProjectPath = 'C:\Users\aRRma99\source\repos\ME\AgentsTracker'
& "$env:TEMP\at-build\AgentsTracker.Gateway.exe"
```

Put those lines in a script in the scratchpad and start it with `pwsh -File` in the background:
variables set by the PowerShell tool do not survive to the next call, and the exe holds the
console. Stop it by the path, not by the name — `Get-Process AgentsTracker.Gateway | Where-Object
{ $_.Path -like "$env:TEMP*" } | Stop-Process -Force` — otherwise the working release goes down
with it; then delete the `Gateway__DataDirectory` folder.

What to set for the scratch instance:

- `Gateway__Channel__Settings__BotToken` — fake, but in the `<number>:<string>` format, otherwise
  the channel will not be created;
- `Gateway__McpPort` and `Gateway__MonitorPort` — use different ones, the working ones are
  taken;
- `Gateway__ProjectPath` and `Gateway__Channel__Settings__AllowedUserIds__0` — if the scratch
  instance needs to reply in chat; an array element is set by an index at the end of the name;
- `Gateway__DataDirectory` — a folder in the scratchpad. Then the scratch instance has its own
  `state.json` and does not read the production `appsettings.Local.json`: it does not
  interfere with the working gateway and does not depend on the shape of its config. Without
  this, `state.json` is shared — do not start the scratch instance while a task is in progress.

With a **fake** token the scratch instance lives about a minute: `getUpdates` gets an
`Unauthorized`, and after `ConnectAttempts` (6, ten seconds apart) the host writes «Не удалось
подключиться» and shuts itself down. That is enough for startup, the config and the monitor, and
the working bot notices nothing. Anything longer — or a chat scenario at all — needs a **separate
test bot** from @BotFather: the working bot's token gives the second process a 409 on
`getUpdates`, and the release loses its polling. The MCP config does not conflict either way —
each instance has its own, `mcp-gateway-<pid>.json`.

The monitor of the scratch instance (`http://127.0.0.1:5199`) is the only place where a change
to the page can be seen: port `5100` belongs to the release and shows the old code. Screenshots
— `docs/en/monitor.md`.

Smoke test after infrastructure changes: a script in the scratchpad, `pwsh -File`; after ~8 s
check:

- `/api/snapshot` — the `version` (gateway build), `agent`, `cliVersion` fields;
- `/api/limits`;
- 404 on `/` of the MCP port;
- 401 on `POST /mcp` without a token, with the headers `Content-Type: application/json` and
  `Accept: application/json, text/event-stream` — otherwise you get 415.

## Checking host logic without Telegram

Everything that does not need a live bot is checked by a file-based C# app: the gateway's own
classes with fakes instead of a channel — no token, no ports, no waiting.

```csharp
#:sdk Microsoft.NET.Sdk.Web
#:project C:/Users/…/src/AgentsTracker.Gateway
```

`AppPaths.UseDirectory(<a temp folder>)` before anything else, then
`Options.Create(new GatewayOptions { … })`, `NullLogger<T>.Instance` and your own
`IChatChannel`/`IAuditLog` (the rest of the channel's methods — `throw new NotSupportedException()`).
Put the file in the scratchpad and run `dotnet run check.cs`. That is how the incoming-file
handling was checked in a single run: signature sniffing, size caps, the retention sweep, refusals
and the audit records — and it caught a `Directory.Delete` that failed on a just-deleted file.

A junction left inside the temp folder by a previous run makes `Directory.Delete(recursive: true)`
fail with «Access to the path 'link' is denied» — remove the link itself before the folder.

## Larger tasks — use a worktree

The main `AgentsTracker` folder stays on `master` (there is no `main` branch in the repository):
switching the branch there swaps the sources out from under a gateway running from `bin\Debug`,
and a broken build leaves you unable to restart it. Even with the release as the working
instance, the rule holds — a scratch instance is built out of the same folder.

```powershell
git worktree add ..\AgentsTracker-<task> -b <branch>   # the main folder stays the gateway's folder
```

```bash
git worktree add ../AgentsTracker-<task> -b <branch>   # the main folder stays the gateway's folder
```

Work in the new folder (from chat — `/project`, it has its own sessions). Merge in two steps:
`git merge master` in the worktree (conflicts, build and smoke test happen there), then
`git merge --ff-only <branch>` in the main folder — it changes in one atomic step. Before
merging and stopping the gateway — `git status`: other sessions work in the main folder in
parallel. Small changes in one or two commits go straight into the main folder.

Call review skills with the branch name (`code-review medium --fix <branch>`): without an
argument they take the uncommitted diff of the main folder, not your branch, and fix someone
else's work.

## Documentation in two languages

`docs/en/*.md` is the English original: the agent reads it and `CLAUDE.md` links only to it. The
neighbouring `docs/*.md` is the Russian mirror: the wiki is built from it and `README.md` (always
Russian) links to it. Both are edited in the same commit — a document left in one language only is a
bug. `CLAUDE.md` is English only, `README.md` is Russian only.

A new document is created in both languages at once, plus a `$pages` line in
`scripts\sync-wiki.ps1` for the Russian file: the wiki is built from `docs/*.md`, `docs/en/` never
reaches it.

## GitHub wiki

The wiki is a mirror of `README.md` and the Russian `docs/*.md`; editing it by hand is pointless: the pages are
rebuilt from scratch every time and manual edits get overwritten. `.github/workflows/wiki.yml`
updates them on push to `master`; the same script can also be run manually:

```powershell
pwsh -File scripts\sync-wiki.ps1 -OutDir C:\Temp\wiki-preview   # see what it would produce
pwsh -File scripts\sync-wiki.ps1                                # build and push
```

```bash
pwsh -File scripts/sync-wiki.ps1 -OutDir /c/Temp/wiki-preview   # see what it would produce
pwsh -File scripts/sync-wiki.ps1                                # build and push
```

The script clears its own pages from the preview folder, so it does not allow `-OutDir` inside
the repository: `-OutDir docs` would overwrite the sources.

Page names are in Latin script, taken from the file name (`docs/monitor.md` → `monitor`), and
the sidebar title is taken from the file's first `#`. The script rewrites links to repository
files and images as absolute: the wiki lives in a separate git repository and cannot see
relative paths into the main one. A new file in `docs/` reaches the wiki only if it is added to
the script's `$pages` table — otherwise it stays without a page and without a sidebar entry.

GitHub creates the first page only through the web interface: until it exists, the
`<repository>.wiki.git` repository does not exist and cloning fails with "Repository not
found".

## Tools and environment

- Edit Russian text and Windows paths with Edit/Write, not a heredoc or python from Bash: `\a`,
  `\n`, `\r` in paths get silently swallowed and are indistinguishable from a typo. Put the
  script in the scratchpad via Write, run it as a file, check the result with `grep … | cat -v`
  and by building.
- Sources are UTF-8 **without BOM** (`utf-8-sig` adds one silently).
- The PowerShell tool rejects a compound command where `Remove-Item` stands next to a path under
  `C:\Program Files` (7-Zip, for example): the guard reads it as removing `C:\Program`. Split such
  a command into separate calls.
- `claude` is not on the tool shells' PATH: call it as
  `& "$env:USERPROFILE\.local\bin\claude.exe" --help` (from Git Bash that path does not execute).
  Undocumented CLI behaviour is quickest to check with a bare `claude -p … --output-format json`
  and `permission_denials` in the answer — that is how `--add-dir` was verified not to raise an
  approval card.
- A multi-paragraph commit message goes into a file in the scratchpad and `git commit -F
  <file>`: `-F -` with a here-string from the PowerShell tool does not get stdin.
- A reviewer subagent without Bash has no access to `git show`/`git diff`: dump old versions of
  files into the scratchpad (`git show <commit>:<path> > …`).
- From a session over Telegram, `AskUserQuestion` and `ExitPlanMode` wait for
  `ApprovalTimeoutMinutes` (15): without a response, take the recommended option. A long
  one-line PowerShell command in an approval prompt is easy to reject without reading it — put
  a multi-step check into a script and run it with `pwsh -File`.
- `curl`, `Invoke-WebRequest`, `Invoke-RestMethod` are in `deny` — for `127.0.0.1` too. A
  monitor snapshot from a session: `pwsh -File scripts\monitor-api.ps1 /api/snapshot` (loopback
  only, allowed in `.claude/settings.json`); the whole page — Playwright's
  `browser_run_code_unsafe`.
- `modern-web-guidance` (`npx.cmd -y modern-web-guidance@latest search "…"`) — use the
  PowerShell tool: from Git Bash `npx.cmd` silently returns empty output.
- Production installation and Docker (publishing, ports, secrets, upgrade) — `deployment.md`.
  Autostart is set up by the exe itself: `install` creates a Task Scheduler task under the
  current user, not a service (the OAuth login lives in `%USERPROFILE%\.claude`, which is not
  found under SYSTEM); `uninstall` removes it and also kills processes started manually from
  the same folder.

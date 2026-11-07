# Production deployment

When to read this: you set up the gateway "for good" — a published application that starts on its
own and survives a reboot, not `dotnet run` from the project folder.

Commands come in two blocks: PowerShell first, bash second (Git Bash on Windows, a plain terminal
on macOS and Linux). One block means the same command in both shells.

Three ways to run it:

| | `dotnet run` | Install on the machine | Docker |
|---|---|---|---|
| For what | development | permanent run on your own PC | isolation, plus macOS and Linux |
| What runs | `Debug` build | published exe, `Release` | container |
| Autostart | no | Task Scheduler task at logon | `restart: unless-stopped` |
| Secrets | plain text next to the project | DPAPI in the data directory | environment variables |
| What the agent sees | the whole machine | the whole machine | only mounted volumes |

## Install on the machine

### From a ready-made archive

The [Releases](https://github.com/aRRma/AgentsTracker/releases) page attaches two Windows x64
archives to each version: `…-win-x64-self-contained.zip` with the runtime included (neither the
SDK nor the Runtime is needed) and the smaller `…-win-x64.zip`, which needs the **ASP.NET Core
Runtime 10**. The gateway is built with the Web SDK and asks for two frameworks,
`Microsoft.NETCore.App` and `Microsoft.AspNetCore.App`, so the plain .NET Runtime is not enough.
Inside is the same result as `dotnet publish`: copy `appsettings.Local.example.json` next to the
exe as `appsettings.Local.json`, fill it in and run `install --start`.

Archive layout: a folder `AgentsTracker-<tag>-win-x64` (or `…-win-x64-self-contained`, named like
the archive) with the application and, beside it, `install.txt` — a short user guide (source
`../install-quickstart.txt`) — and `claude-settings.example.json`, the agent's permission rules
for `~/.claude/settings.json` (`claude-permissions.md`, "Moving to another PC"). The folder name
shows the installed version.

### From source

Publishing and registering autostart are two commands, no scripts needed:

```
dotnet publish src/AgentsTracker.Gateway -c Release -o C:/Apps/AgentsTracker
C:/Apps/AgentsTracker/AgentsTracker.Gateway.exe install --start
```

Pick an install folder outside the gateway's source folder, otherwise `git clean` wipes it with the
application. To include the runtime (no .NET needed on the machine), add
`-r win-x64 --self-contained` to the publish.

`install` registers **the exe that ran it**, wherever it is. So both a publish from this machine
and a folder brought from another one work; the second needs neither source nor SDK. It refuses
under `dotnet run` and inside a container, and warns when the exe sits in a `bin` build folder.
Along the way it also:

- moves `appsettings.Local.json` from next to the exe into the data directory and encrypts the
  channel secrets (`Channel:Settings:BotToken`) and `Proxy`, as `protect-secrets` does. If the
  data directory already has a config, that one stays and the copy next to the exe is deleted
  unread (`dotnet publish` copies there a file with a plain-text token);
- locks down the data directory permissions: owner and SYSTEM only;
- overwrites a task of the same name;
- prints a summary: the path, the data directory, the ports, whether the config is filled in.

Keys: `--start` starts it right away, `--name <name>` changes the autostart entry's name (default
`AgentsTracker Gateway`).

`uninstall` (same `--name`) stops the gateway, also one started by hand from the same exe path,
and removes autostart. Data is untouched. It frees the files before a new publish.

The task is created **under your own account, without elevation**. It can't be a service: Claude
Code reads the OAuth login from `%USERPROFILE%\.claude`, and SYSTEM has no access to it. So the
gateway runs only while you are logged into Windows. To run without a logged-in session, enable
auto-logon and lock the screen rather than moving the task to SYSTEM.

On macOS and Linux `install` refuses; use Docker there.

### Version update

1. Run `AgentsTracker.Gateway.exe uninstall` first: while the exe runs, publishing over it fails
   with `MSB3021`, and the task would stay on the old version.
2. From source: `git pull` and the same `dotnet publish` as above. From an archive: put the new
   folder in place of the old one.
3. `AgentsTracker.Gateway.exe install --start`.

The config, sessions and audit journal in the data directory stay. Do not put your own
`appsettings.Local.json` next to the new exe: with a config already in the data directory,
`install` deletes that file without applying it.

If the update lands on a running task, its chat gets «⚠️ Предыдущий запуск прерван перезапуском шлюза» (the previous run
was interrupted by the gateway restart) with a hint to write «продолжай» ("continue"); an
interrupted вопрос is asked again with `/ask`.

### Checking after the install

```powershell
Get-ScheduledTaskInfo -TaskName 'AgentsTracker Gateway' | Select-Object LastRunTime, LastTaskResult
Get-Process AgentsTracker.Gateway | Select-Object Id, Path
Invoke-RestMethod http://127.0.0.1:5100/api/snapshot | Select-Object version, agent, cliVersion
```

```bash
schtasks //Query //TN "AgentsTracker Gateway" //FO LIST
tasklist //FI "IMAGENAME eq AgentsTracker.Gateway.exe"
curl -s http://127.0.0.1:5100/api/snapshot
```

`LastTaskResult` = 0 and a process with a path from the install folder mean the task came up, and
Telegram gets «🔌 Шлюз запущен» (Gateway started) with the version. The snapshot's `version` and
the version in the message are the same string: after an update it shows the new build is
running, not the old task.

The gateway exits with code 1 on purpose when it cannot start: the config fails validation, or
Telegram rejects the token six times in a row (ten seconds apart). Then Task Scheduler restarts
the task (3 attempts, a minute apart). A missing network is no reason to exit: the gateway waits
10 s, 20 s… up to 5 min between attempts, as long as it takes.

## Config and secrets

The production config is a single file **outside** the install folder:

```
%LOCALAPPDATA%\AgentsTracker\appsettings.Local.json
```

So the install folder can be deleted and recreated without touching the token, sessions or audit
journal. Re-encrypt after an edit with `AgentsTracker.Gateway.exe protect-secrets [file]`. It
encrypts `Proxy` and the channel secrets (`BotToken`, the channel's own `Proxy`) in place. Without
a file it takes the config in the data directory, else the one next to the exe, and moves the
result into the data directory. It refuses to overwrite a config already there.

DPAPI decrypts only for **the same account on the same machine**. A config with `dpapi:…` does not
carry over to another PC or user: on a new machine put in a file with plain values and run
`protect-secrets` there. If the account password is reset (not changed), the DPAPI key is lost and
the gateway fails to start with a clear reason — fill in the values again and repeat. Outside
Windows `protect-secrets` refuses: there is no DPAPI, keep secrets in environment variables.

Config layers, each next one overrides the previous:

1. `appsettings.json` next to the exe — defaults, in the repository, no secrets;
2. `appsettings.Local.json` next to the exe — for debugging from the IDE only, on install it moves to the data directory, or is deleted if one is already there;
3. `appsettings.Local.json` in the data directory — production;
4. environment variables `Gateway__Key` (nested — `Gateway__Claude__Executable`).

The data directory is `%LOCALAPPDATA%\AgentsTracker` (on Linux and macOS
`~/.local/share/AgentsTracker`); `Gateway:DataDirectory` relocates it. The config file itself
lives there, so this key is read earlier than the rest: only from `appsettings.json` next to the
exe or from the `Gateway__DataDirectory` variable.

### Main keys

The `Gateway` section of `appsettings.Local.json`. The full list with defaults is `appsettings.json`
next to the exe.

| Key | What for |
|---|---|
| `Channel:Type` | which channel; `telegram` is the only one |
| `Channel:Settings:BotToken` | the token from @BotFather |
| `Channel:Settings:AllowedUserIds` | who may control the bot |
| `Proxy`, `Channel:Settings:Proxy` | the machine-wide proxy · a proxy for the channel only (if absent, the common one is used) |
| `ProjectPath` | the default project folder |
| `Projects` / `ProjectsRoot` | which folders the project menu shows. If neither is set — the folders next to `ProjectPath` that have `.git`, `*.sln`, `*.slnx`, `package.json` or `pyproject.toml` |
| `Claude:Executable` | path to `claude.exe` if the auto-search did not find it |
| `Claude:BuiltInSkills` | built-in skills for `/skills`, lines `"/command \| description \| argument hint"` |
| `Model`, `Effort` | the default model and effort |
| `PermissionMode` | what is allowed without asking. The chat switches `plan`, `default`, `acceptEdits`, `auto`; `dontAsk` and `bypassPermissions` only here, in the file |
| `McpPort`, `MonitorPort`, `MonitorBind` | see [Ports](#ports) |
| `DataDirectory` | where state, the audit journal and the log live; see above |
| `ApprovalTimeoutMinutes`, `RunTimeoutMinutes` | how many minutes to wait for a card answer (15) and for the whole run (60) |
| `Attachments:Enabled`, `:MaxBytes`, `:RetentionHours` | pictures from the chat: accept them (yes), the size ceiling (10 MB), how many hours to keep them (24) |
| `Question:Model`, `Question:Effort` | model and effort for `/ask` and a беседа: `sonnet` and whatever Claude Code decides by default. Not changeable from the chat for `/ask`; in a беседа `/model` and `/effort` change them |
| `Work:AllowedUserIds` | who may use the «Работа» topic (remote work on the whole computer): ids from `Channel:Settings:AllowedUserIds`, a number like `[123456789]` or `"telegram:123456789"`. Empty (default) — the button is shown to nobody. The gateway will not start if an id is not among the bot's users |
| `Work:Folder` | the root of the «Работа» topics' folders, a full path; each topic gets its own subfolder that is never emptied. Default `%USERPROFILE%\AgentsTracker-work`. The gateway will not start if the path overlaps the data folder, the temp folder, `ProjectPath` or the project list |

### Restart

The config is read once at startup: change it, restart.

```powershell
Stop-ScheduledTask -TaskName 'AgentsTracker Gateway'
Start-ScheduledTask -TaskName 'AgentsTracker Gateway'
```

```bash
schtasks //End //TN "AgentsTracker Gateway"
schtasks //Run //TN "AgentsTracker Gateway"
```

In a container — `docker compose restart`; a gateway from source — see [operations.md](operations.md).
If the restart cut off the agent's work, a warning comes into the chat: what was done is kept,
write «продолжай». Ask an interrupted вопрос again with `/ask`.

## Ports

| Key | Default | What it is |
|---|---|---|
| `McpPort` | 5099 | `claude` uses it to ask the gateway for confirmations. Always `127.0.0.1` only, locked with a token in the header. Can't be turned off — without it there are no buttons |
| `MonitorPort` | 5100 | the monitor. `0` disables it. A startup check rejects a clash with `McpPort` |
| `MonitorBind` | `loopback` | where the monitor listens. `any` is needed in a container; on a regular machine leave `loopback` |

```json
{ "Gateway": { "McpPort": 15099, "MonitorPort": 15100 } }
```

Only these keys set the ports; `--urls`, `ASPNETCORE_URLS` and `Kestrel:Endpoints` do not (the last
one only opens an extra listener). "Any free port" is not an option: the MCP port goes into the
config for the CLI and must stay fixed.

If another application holds a port, startup shows `Failed to bind to address` and the gateway
does not come up:

```powershell
Get-NetTCPConnection -LocalPort 5099,5100 -State Listen -ErrorAction SilentlyContinue |
    Select-Object LocalPort, OwningProcess
```

```bash
netstat -ano | grep -E ':(5099|5100)\s.*LISTENING'
```

No firewall rules are needed: with `MonitorBind: loopback` the ports don't face outward.

## Docker

The container gives what an on-machine install doesn't: the agent sees only mounted volumes. A
failed command and the «Всегда» (Always) button can't reach the rest of the system. The same holds
for the «Работа» topic: «the whole computer» there is only the container.

Copy `.env.example` to `.env` and fill in the token, your id and the folder with projects. Then:

```
docker compose up -d --build
docker compose exec gateway claude      # sign in to the agent's account once
```

The account login lives in the `claude-home` volume, the gateway state in the `data` volume. The
host's `~/.claude` is deliberately not mounted: the container and the working machine should not
share settings, plugins and session history.

- **Secrets — environment variables.** There is no DPAPI on Linux, so
  `Gateway__Channel__Settings__BotToken` and `Gateway__Channel__Settings__AllowedUserIds__0` live
  in `.env`, which is in `.gitignore`.
- **Monitor.** In the container it listens on `0.0.0.0` (`Gateway__MonitorBind=any` is set in the
  image), otherwise there would be nothing to publish the port from. It goes outward as
  `127.0.0.1:5100:5100`: the page has no password and must not leave your machine.
- **Project paths differ.** Inside it is `/projects/<project>` (`PROJECT` in `.env` picks the
  default one; the image sets `Gateway__ProjectsRoot=/projects` for the project menu). Sessions
  are keyed by the normalized path, so the host's session history is not picked up.
- **Tools.** The agent can do what is in the image: git, ripgrep and the .NET SDK. For other
  languages add a layer in the `Dockerfile`.
- **Autostart** is `restart: unless-stopped`; `install` inside the container refuses and says why.
- **`init: true`** reaps the agent's orphaned background processes. Do not remove it from your own
  compose file.

Update: `docker compose up -d --build`. Volumes survive a rebuild.

## Data, logs, backup

The data directory is the gateway's whole state. On Windows only the owner and SYSTEM can open it
(in the container it is the `data` volume):

| File | What |
|---|---|
| `appsettings.Local.json` | config; on Windows secrets are encrypted |
| `state.json` | the gateway's state: sessions, the choices per chat and topic, rules and trusted projects, statistics, run history |
| `audit\audit-YYYY-MM.jsonl` | the "who did what, when" journal, one file per month |
| `logs\gateway-YYYY-MM-DD.log` | the technical log, one file per day, Information and above, secrets masked; files older than 14 days are deleted |
| `inbox\<chat>\<project>\` | pictures from the chat; swept after `Attachments:RetentionHours` (24 by default) |
| `mcp-gateway-<pid>-<run>.json` | temporary, one per agent run: created at its start, removed at its end |

Back up `state.json` and `audit\`. Back up the config only if you remember that `dpapi:…`
restores only on this machine under this account. The log needs no backup.

The Task Scheduler task writes to a console that doesn't exist, so the log is read from the file
(`Get-Content -Encoding utf8 -Tail 50 <file>`), in the monitor (the «Лог» section, the last 500
entries) or with `GET /api/log?count=&level=`. In the container the file is on the `data` volume,
and `docker compose logs -f` still works. If the gateway crashed past every handler, the cause is the
last `CRT` entry in the file.

## If something is wrong

| Symptom | Cause |
|---|---|
| The downloaded exe does not start and asks to install .NET | the `…-win-x64.zip` archive works only with ASP.NET Core Runtime 10 — install it or take `…-self-contained.zip` |
| `dotnet` not found, or it cannot find the SDK | no .NET SDK; it is needed only to build from source |
| The bot is silent | your id is not in `Channel:Settings:AllowedUserIds` (the journal has the rejected message), the token is wrong, or Telegram is unreachable without a proxy |
| `Gateway:BotToken больше не читается` | the channel settings moved to `Gateway:Channel:Settings`; move them with `pwsh -File scripts\migrate-channel-settings.ps1` from the gateway's sources (a `.backup` stays next to it) |
| Not all projects in the menu | `ProjectsRoot` is not set; the list pages with `◀ ▶` |
| `claude` not found | the CLI is not installed or no new window was opened; last resort — `Claude:Executable` |
| The agent asks to log in | run `claude` and log in; the gateway uses that login |
| The build fails with `MSB3021` | the gateway is running and holds the files. Installed — `AgentsTracker.Gateway.exe uninstall` (removes and stops it), started by hand — `Get-Process AgentsTracker.Gateway \| Stop-Process -Force` (in bash `taskkill //F //IM AgentsTracker.Gateway.exe`) |
| Approval cards do not come | the mode is `auto` — picked from the chat or set in `PermissionMode`. Go back with `/mode default` or `"PermissionMode": "default"` in the file |
| Mojibake in the log | the console is in cp866; open the log file `logs\gateway-YYYY-MM-DD.log` in the data directory — it is UTF-8 (in a container — on the `data` volume) |
| The monitor in a container does not open | the port is not published as `127.0.0.1:5100:5100`, or `Gateway__MonitorBind` is overridden: the image sets `any` itself |

## Multiple instances

Under one account — **one gateway**: the data directory is shared, and a Telegram bot works with
one process only. Several bots at once need different Windows users or different containers, each
with its own volume and `~/.claude`. Spread the ports
then: loopback is shared by the whole machine. A temporary second instance for debugging —
`operations.md`.

## Release

GitHub Actions builds the release on a `v*` tag push — `../../.github/workflows/release.yml`:

```powershell
git tag v<X.Y.Z>
git push origin v<X.Y.Z>
```

It publishes both win-x64 archives (`AgentsTracker-<tag>-win-x64-self-contained.zip` with the
runtime, `AgentsTracker-<tag>-win-x64.zip` without) and creates the release. Release notes come
from `../release-notes/<tag>.md`, so create the file **before** pushing the tag; without it GitHub
collects a list of commits.

Notes are **short, in plain human words**, from the side of the person using the bot: one or two
sentences on what changed, then a «Что нового» list, one bullet per visible change. No reasoning,
class names, flags or invisible changes. The install and update sections are boilerplate: copy
them from the previous release and change the version.

The tag number without the `v` goes into `-p:Version`; the csproj keeps `0.0.0-dev`, so a build
from source is plainly distinguishable from a released one. The gateway prints that number as the
first log line and puts it in the monitor snapshot's `version` and the «🔌 Шлюз запущен» message.

Packaging builds the archive layout described above; `claude-settings.example.json` comes from
`../examples/`. The workflow removes `appsettings.Local.json` and `appsettings.Development.json`
from the output, so a token can never reach the archive.

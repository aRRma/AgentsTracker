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
for `~/.claude/settings.json` (`claude-permissions.md`, "Moving to Another PC"). The folder keeps
extraction "here" from scattering a hundred and a half files over the downloads folder, and its
name shows the installed version.

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
  unread (`dotnet publish` copies there a file with a plain-text token). A config with the old
  `Gateway:BotToken` is rejected and autostart is not registered — run
  `scripts\migrate-channel-settings.ps1` first (`-Path` for a file next to the exe);
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

On macOS and Linux `install` refuses: they need a launchd agent and a user-level systemd unit,
which will come later. Until then Docker is what is left there.

### Version update

1. Run `AgentsTracker.Gateway.exe uninstall` first: while the exe runs, publishing over it fails
   with `MSB3021`, and the task would stay on the old version.
2. From source: `git pull` and the same `dotnet publish` as above. From an archive: put the new
   folder in place of the old one.
3. `AgentsTracker.Gateway.exe install --start`.

The config, sessions and audit journal in the data directory stay. If the update lands on a
running task, its chat gets «⚠️ Предыдущий запуск прерван перезапуском шлюза» (the previous run
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
result into the data directory. It refuses to overwrite a config already there, and refuses a
config with the old `Gateway:BotToken`/`Gateway:AllowedUserIds` keys
(`scripts\migrate-channel-settings.ps1`).

DPAPI decrypts only for **the same account on the same machine**. A config with `dpapi:…` does not
carry over to another PC or user: on a new machine put in a file with plain values and run
`protect-secrets` there. If the account password is reset (not changed), the DPAPI key is lost and
the gateway fails to start with a clear reason — fill in the values again and repeat. Outside
Windows `protect-secrets` refuses: there is no DPAPI, keep secrets in environment variables.

Config layers, each next one overrides the previous:

1. `appsettings.json` next to the exe — defaults, in the repository, no secrets;
2. `appsettings.Local.json` next to the exe — for debugging from the IDE only, deleted on install;
3. `appsettings.Local.json` in the data directory — production;
4. environment variables `Gateway__Key` (nested — `Gateway__Claude__Executable`).

The config is read **once at startup**: change it, restart.

The data directory is `%LOCALAPPDATA%\AgentsTracker` (on Linux and macOS
`~/.local/share/AgentsTracker`); `Gateway:DataDirectory` relocates it. The config file itself
lives there, so this key is read earlier than the rest: only from `appsettings.json` next to the
exe or from the `Gateway__DataDirectory` variable.

## Ports

| Key | Default | What it is |
|---|---|---|
| `McpPort` | 5099 | `claude` uses it to ask the gateway for permissions. Always `127.0.0.1` only, locked with a token in the header. Can't be turned off — without it there are no buttons |
| `MonitorPort` | 5100 | the web monitor. `0` disables it. A startup check rejects a clash with `McpPort` |
| `MonitorBind` | `loopback` | where the monitor listens. `any` is needed in a container; on a regular machine leave `loopback` |

```json
{ "Gateway": { "McpPort": 15099, "MonitorPort": 15100 } }
```

Kestrel opens the ports with explicit `Listen` calls, so `--urls` and `ASPNETCORE_URLS` are
ignored — only these keys set the ports. A `Kestrel:Endpoints` section does not move them
either: ASP.NET Core adds such endpoints on top of the `Listen` ones, so it only opens an extra
listener. Don't use it. "Any free port" is not an option: the MCP port goes into the config for
the CLI and must stay fixed.

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
failed command and the «Всегда» (Always) button can't reach the rest of the system.

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
- **`init: true`** puts a tiny init before the gateway. As PID 1 the gateway would have to reap
  the agent's orphaned children (a background dev server after its run ended) and does not, so
  they would pile up as zombies. Do not remove it from your own compose file.

Update: `docker compose up -d --build`. Volumes survive a rebuild.

## Data, logs, backup

The data directory is the gateway's whole state. On Windows only the owner and SYSTEM can open it
(in the container it is the `data` volume):

| File | What |
|---|---|
| `appsettings.Local.json` | config; on Windows secrets are encrypted |
| `state.json` | sessions per project, project, model, mode and effort selection, the versions the model aliases answered with, "Always" rules and trusted projects per project, statistics, run history |
| `audit\audit-YYYY-MM.jsonl` | the "who did what, when" journal, one file per month |
| `inbox\<chat>\<project>\` | pictures from the chat; swept after `Attachments:RetentionHours` (24 by default) |
| `mcp-gateway-<pid>-<run>.json` | temporary, one per agent run: created at its start, removed at its end |

Back up `state.json` and `audit\`. Back up the config only if you remember that `dpapi:…`
restores only on this machine under this account.

There is no log file. The Task Scheduler task writes to a console that doesn't exist: watch the
log in the monitor (the «Лог» section) or `GET /api/log?count=&level=`. In the container use
`docker compose logs -f`.

## Multiple instances

Under one account — **one gateway**: the data directory is shared, and a Telegram bot hands
`getUpdates` to only one process (the second gets a 409). Several bots at once need different
Windows users or different containers, each with its own volume and `~/.claude`. Spread the ports
then: loopback is shared by the whole machine. A temporary second instance for debugging —
`operations.md`.

## Release

GitHub Actions builds the release on a `v*` tag push — `../../.github/workflows/release.yml`:

```powershell
git tag v0.1.0
git push origin v0.1.0
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
`../examples/`. `install.txt` is UTF-8 with a BOM, otherwise Cyrillic turns into mojibake in
Notepad on older Windows builds. No tokens are needed: the workflow uses the built-in
`github.token` with `contents: write`. Before packaging it removes `appsettings.Local.json` and
`appsettings.Development.json` from the publish output, so a plain-text token can never reach
the release archive.

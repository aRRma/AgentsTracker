# Production deployment

When to read this: you're setting up the gateway "for good" — not `dotnet run` from the project
folder, but a published application that comes up on its own and survives a reboot.

Commands come in two blocks: PowerShell first, bash second (Git Bash on Windows, a plain
terminal on macOS and Linux). Where a command is the same in both shells, there is one block.

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
SDK nor the Runtime is needed on the machine) and the smaller `…-win-x64.zip`, which needs the
**ASP.NET Core Runtime 10** installed: the gateway is built with the Web SDK and asks for two
frameworks, `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` — the plain .NET Runtime is
not enough. Inside is the same result as `dotnet publish`, so everything below applies
the same way: fill in `appsettings.Local.json` next to the exe and run `install --start`.

Archive layout: a folder `AgentsTracker-<tag>-<kind>` with the application and, next to it,
`install.txt` — a short guide for the user (source — `../install-quickstart.txt`). The
application sits in a folder named after the archive so that extracting "here" does not scatter
a hundred and a half files across the downloads folder, and so the installed folder's name shows
which version it is.

### From source

Publishing and registering autostart are two commands, no scripts needed:

```powershell
dotnet publish src\AgentsTracker.Gateway -c Release -o C:\Apps\AgentsTracker
C:\Apps\AgentsTracker\AgentsTracker.Gateway.exe install --start
```

```bash
dotnet publish src/AgentsTracker.Gateway -c Release -o /c/Apps/AgentsTracker
/c/Apps/AgentsTracker/AgentsTracker.Gateway.exe install --start
```

Pick an install folder outside the repository, otherwise `git clean` will wipe it along with the
application. With the runtime included (no .NET needed on the machine) — add
`-r win-x64 --self-contained` to the publish.

`install` registers **the exe that ran it**, wherever it is located. So both a publish from this
machine and a folder brought over from another one work: in the second case neither the source
nor the SDK is needed. Along the way the command also:

- moves `appsettings.Local.json` into the data directory and encrypts the channel secrets
  (`Channel:Settings:BotToken`) and `Proxy`; a config with the old `Gateway:BotToken` is rejected
  and autostart is not registered — run `scripts\migrate-channel-settings.ps1` first;
- deletes the config next to the exe — `dotnet publish` copies there a file with a plain-text
  token;
- locks down the data directory permissions: owner and SYSTEM only;
- prints a summary — the path, the data directory, the ports, and whether the config is filled
  in.

Keys: `--start` starts it right away, `--name <name>` changes the autostart entry's name
(default `AgentsTracker Gateway`).

`uninstall` stops the gateway and removes autostart, data is left untouched. It's also what
frees up the files before publishing a new version, including when the gateway was started by
hand:

```powershell
C:\Apps\AgentsTracker\AgentsTracker.Gateway.exe uninstall
```

```bash
/c/Apps/AgentsTracker/AgentsTracker.Gateway.exe uninstall
```

The task is created **under your own account, without elevation**. It can't be a service:
Claude Code reads the OAuth login from `%USERPROFILE%\.claude`, and SYSTEM doesn't have access
to it. Consequence — the gateway runs only while you're logged into Windows. If you need it to
run without a logged-in session, enable auto-logon and lock the screen, rather than moving the
task to SYSTEM.

On macOS and Linux `install` still refuses: they need a launchd agent and a user-level systemd
unit, which will come later. Until then, Docker is what's left on those systems.

### Version update

```powershell
C:\Apps\AgentsTracker\AgentsTracker.Gateway.exe uninstall
git pull
dotnet publish src\AgentsTracker.Gateway -c Release -o C:\Apps\AgentsTracker
C:\Apps\AgentsTracker\AgentsTracker.Gateway.exe install --start
```

```bash
/c/Apps/AgentsTracker/AgentsTracker.Gateway.exe uninstall
git pull
dotnet publish src/AgentsTracker.Gateway -c Release -o /c/Apps/AgentsTracker
/c/Apps/AgentsTracker/AgentsTracker.Gateway.exe install --start
```

`uninstall` goes first: while the exe is running, publishing over it fails with `MSB3021`, and
the task would be left on the old version. The config, sessions, and audit log in the data
directory are not touched. If the update lands on a running task, its chat gets
«прервана, напишите „продолжай“» (interrupted, write "continue").

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

`LastTaskResult` = 0 and a process with a path from the install folder — the task came up, and
Telegram will get «🔌 Шлюз запущен» (🔌 Gateway started) with the version. The snapshot's
`version` field and the version in the message are the same string: it shows that a new build is
running after an update, not the old task. A non-zero exit code is returned by the
gateway on purpose when it fails to connect to Telegram: the Task Scheduler uses it to restart
the task (3 attempts, a minute apart).

## Config and secrets

The production config is a single file, and it lives **outside** the install folder:

```
%LOCALAPPDATA%\AgentsTracker\appsettings.Local.json
```

That way the install folder can be deleted and recreated without touching the token, sessions,
or audit log. Re-encrypt after an edit — `AgentsTracker.Gateway.exe protect-secrets`.

About DPAPI, important: only **the same account on the same machine** can decrypt it. A config
with `dpapi:…` doesn't carry over to another PC and isn't usable by another user — on a new
machine, put a file with plain values in place and run `protect-secrets` there. If the account
password is reset (not changed), the DPAPI key is lost, and the gateway will fail to start with
a clear reason — fill in the values again and repeat. Outside Windows the command refuses: there
is no DPAPI there, secrets are kept in environment variables.

Config layers, each next one overrides the previous:

1. `appsettings.json` next to the exe — defaults, in the repository, no secrets;
2. `appsettings.Local.json` next to the exe — for debugging from the IDE only, deleted on
   install;
3. `appsettings.Local.json` in the data directory — production;
4. environment variables `Gateway__Key` (nested — `Gateway__Claude__Executable`).

The config is read **once at startup**: change it, restart.

The data directory is relocated by `Gateway:DataDirectory`. It's needed before the rest of the
config — `appsettings.Local.json` itself lives in it — so it's read only from `appsettings.json`
next to the exe or from the `Gateway__DataDirectory` variable.

## Ports

| Key | Default | What it is |
|---|---|---|
| `McpPort` | 5099 | `claude` uses it to ask the gateway for permissions. Always `127.0.0.1` only, locked with a token in the header. Can't be turned off — without it there are no buttons |
| `MonitorPort` | 5100 | the web monitor. `0` disables it. Colliding with `McpPort` is caught by a startup check |
| `MonitorBind` | `loopback` | where the monitor listens. `any` is needed in a container, on a regular machine leave `loopback` |

```json
{ "Gateway": { "McpPort": 15099, "MonitorPort": 15100 } }
```

Ports are opened by Kestrel with explicit `Listen` calls, so `--urls`, `ASPNETCORE_URLS`, and
the `Kestrel` section **don't affect them** — only these keys do. "Any free port" isn't an
option either: the MCP port number goes into the config for the CLI and has to stay fixed.

Taken by another application — startup will show `Failed to bind to address` and the gateway
won't come up:

```powershell
Get-NetTCPConnection -LocalPort 5099,5100 -State Listen -ErrorAction SilentlyContinue |
    Select-Object LocalPort, OwningProcess
```

```bash
netstat -ano | grep -E ':(5099|5100)\s.*LISTENING'
```

No firewall rules are needed: the ports don't face outward.

## Docker

The container gives you what an on-machine install doesn't: the agent sees only mounted volumes.
A failed command and the «Всегда» (Always) button can't reach the rest of the system.

```powershell
Copy-Item .env.example .env             # fill in the token, your id, and the folder with repositories
docker compose up -d --build
docker compose exec gateway claude      # sign in to the agent's account once
```

```bash
cp .env.example .env                    # fill in the token, your id, and the folder with repositories
docker compose up -d --build
docker compose exec gateway claude      # sign in to the agent's account once
```

The account login lives in the `claude-home` volume, the gateway state — in the `data` volume.
The host's `~/.claude` is deliberately not mounted through: the container and the working
machine shouldn't share settings, plugins, and session history.

What matters:

- **Secrets — as environment variables.** There's no DPAPI on Linux, so
  `Gateway__Channel__Settings__BotToken` and `Gateway__Channel__Settings__AllowedUserIds__0`
  live in `.env`, which is in `.gitignore`.
- **Monitor.** Inside the container it listens on `0.0.0.0` (`Gateway__MonitorBind=any` is set
  in the image), otherwise there'd be nothing to publish the port from. It's exposed outward as
  `127.0.0.1:5100:5100` — the page has no password, and it must not be exposed beyond your own
  machine.
- **Project paths differ.** Inside it's `/projects/<repository>`. Sessions are keyed by the
  normalized path, so session history from the host won't be picked up in the container.
- **Tools.** The agent can do exactly what's in the image: git, ripgrep, and the .NET SDK. Need
  other languages — add a layer in the `Dockerfile`.
- **Autostart** is set by `restart: unless-stopped`; the `install` command inside the container
  refuses to run and explains why.

Update: `docker compose up -d --build`. Volumes survive a rebuild.

## Data, logs, backup

The data directory is the gateway's whole state, accessible only to the owner and SYSTEM (in
the container this is the `data` volume):

| File | What |
|---|---|
| `appsettings.Local.json` | config; on Windows secrets are encrypted |
| `state.json` | sessions per project, model and mode selection, "Always" rules, run history |
| `audit\audit-YYYY-MM.jsonl` | "who did what, when" log, one file per month |
| `mcp-gateway-<pid>.json` | temporary, created on startup and removed on shutdown |

It makes sense to include `state.json` and `audit\` in a backup. The config only if you remember
that `dpapi:…` will only restore on this same machine under this same account.

There's no log file. The Task Scheduler task writes to a console that doesn't exist: watch the
log in the monitor (the «Лог» (Log) section) or `GET /api/log?count=&level=`. In the container —
`docker compose logs -f`.

## Multiple instances

Under one account — **one gateway**: the data directory is shared, and a single Telegram bot
hands `getUpdates` to only one process (the second gets a 409). Different bots at the same time
— either under different Windows users, or different containers: each with its own volume and
its own `~/.claude`. Spread out the ports in that case, loopback is shared across the whole
machine.

A temporary trial of a second instance while debugging — `operations.md`.

## Release

GitHub Actions builds the release on a `v*` tag push — `../../.github/workflows/release.yml`:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The build then publishes both win-x64 archives (with and without the runtime) and creates the
release. Release notes are taken from `../release-notes/<tag>.md`, so create the file **before**
pushing the tag; no file — GitHub will collect a list of commits instead.

The tag number without the `v` goes into `-p:Version`; the version isn't edited separately in
the csproj — it holds `0.0.0-dev` there, so a build from source is honestly distinguishable from
a released one. The gateway prints that same number as the first line of the log, puts it in the
monitor snapshot's `version` field and into the «🔌 Шлюз запущен» message.

Packaging puts the publish output into a folder `AgentsTracker-<tag>-<kind>` inside the archive
and `install.txt` from `../install-quickstart.txt` next to it (UTF-8 with a BOM: without it
Cyrillic turns into mojibake in Notepad on older Windows builds).

No tokens need to be set up: the workflow uses the built-in `github.token` with `contents:
write` permission. Before packaging, `appsettings.Local.json` is removed from the publish
output — in case the build ever runs from a machine where this file exists: a plain-text token
must not end up in the release archive.

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
the same way: copy `appsettings.Local.example.json` next to the exe as `appsettings.Local.json`,
fill it in and run `install --start`.

Archive layout: a folder `AgentsTracker-<tag>-win-x64` (or `…-win-x64-self-contained`, the same
name as the archive) with the application and, next to it, `install.txt` — a short guide for the
user (source — `../install-quickstart.txt`) and `claude-settings.example.json` — the agent's
permission rules for `~/.claude/settings.json` (`claude-permissions.md`, "Moving to Another PC").
The application sits in a folder so that extracting "here" does not scatter a hundred and a half
files across the downloads folder, and so the installed folder's name shows which version it is.

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
nor the SDK is needed. It refuses under `dotnet run` and inside a container, and warns when the
exe sits in a `bin` build folder. Along the way the command also:

- moves `appsettings.Local.json` from next to the exe into the data directory and encrypts the
  channel secrets (`Channel:Settings:BotToken`) and `Proxy` (as `protect-secrets` does). If the
  data directory already has a config, that one stays and the copy next to the exe is deleted
  unread — `dotnet publish` copies there a file with a plain-text token. A config with the old
  `Gateway:BotToken` is rejected and autostart is not registered — run
  `scripts\migrate-channel-settings.ps1` first (`-Path` for a file next to the exe);
- locks down the data directory permissions: owner and SYSTEM only;
- overwrites a task of the same name, if there is one;
- prints a summary — the path, the data directory, the ports, and whether the config is filled
  in.

Keys: `--start` starts it right away, `--name <name>` changes the autostart entry's name
(default `AgentsTracker Gateway`); `uninstall` takes the same `--name`.

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
the task would be left on the old version. From an archive the steps are the same: `uninstall`,
put the new folder in place of the old one, `install --start`. The config, sessions, and audit
log in the data directory are not touched. If the update lands on a running task, its chat gets
«⚠️ Предыдущий запуск прерван перезапуском шлюза» (the previous run was interrupted by the
gateway restart) with a hint to write «продолжай» ("continue"); an interrupted вопрос
is asked again with `/ask`.

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
running after an update, not the old task.

The gateway exits with code 1 on purpose when it cannot start: the config fails validation, or
Telegram rejects the token six times in a row (ten seconds apart). The Task Scheduler restarts
the task after that (3 attempts, a minute apart). A missing network is not a reason to exit: the
gateway waits 10 s, 20 s… up to 5 min between attempts, as long as it takes.

## Config and secrets

The production config is a single file, and it lives **outside** the install folder:

```
%LOCALAPPDATA%\AgentsTracker\appsettings.Local.json
```

That way the install folder can be deleted and recreated without touching the token, sessions,
or audit log. Re-encrypt after an edit — `AgentsTracker.Gateway.exe protect-secrets [file]`. It
encrypts `Proxy` and the channel secrets (`BotToken`, the channel's own `Proxy`) in place. Without
a file it takes the one in the data directory, else the one next to the exe, and moves the result
into the data directory. If the data directory already has a config, it refuses to overwrite it.
It also refuses a config with the old `Gateway:BotToken`/`Gateway:AllowedUserIds` keys
(`scripts\migrate-channel-settings.ps1`).

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

The data directory is `%LOCALAPPDATA%\AgentsTracker` (on Linux and macOS
`~/.local/share/AgentsTracker`). `Gateway:DataDirectory` relocates it. It's needed before the
rest of the config — `appsettings.Local.json` itself lives in it — so it's read only from
`appsettings.json` next to the exe or from the `Gateway__DataDirectory` variable.

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

No firewall rules are needed: with `MonitorBind: loopback` the ports don't face outward.

## Docker

The container gives you what an on-machine install doesn't: the agent sees only mounted volumes.
A failed command and the «Всегда» (Always) button can't reach the rest of the system.

```powershell
Copy-Item .env.example .env             # fill in the token, your id, and the folder with projects
docker compose up -d --build
docker compose exec gateway claude      # sign in to the agent's account once
```

```bash
cp .env.example .env                    # fill in the token, your id, and the folder with projects
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
- **Project paths differ.** Inside it's `/projects/<project>` (`PROJECT` in `.env` picks the
  default one; the image sets `Gateway__ProjectsRoot=/projects` for the project menu). Sessions
  are keyed by the normalized path, so session history from the host won't be picked up in the
  container.
- **Tools.** The agent can do exactly what's in the image: git, ripgrep, and the .NET SDK. Need
  other languages — add a layer in the `Dockerfile`.
- **Autostart** is set by `restart: unless-stopped`; the `install` command inside the container
  refuses to run and explains why.
- **`init: true`** puts a tiny init in front of the gateway: as PID 1 it would have to reap the
  agent's orphaned children (a background dev server after its run ended) and does not, so they
  would pile up as zombies. Do not remove it from your own compose file.

Update: `docker compose up -d --build`. Volumes survive a rebuild.

## Data, logs, backup

The data directory is the gateway's whole state. On Windows only the owner and SYSTEM can open it
(in the container it is the `data` volume):

| File | What |
|---|---|
| `appsettings.Local.json` | config; on Windows secrets are encrypted |
| `state.json` | sessions per project, project, model, mode and effort selection, the versions the model aliases answered with, "Always" rules and trusted projects per project, statistics, run history |
| `audit\audit-YYYY-MM.jsonl` | "who did what, when" log, one file per month |
| `inbox\<chat>\<project>\` | pictures from the chat; swept after `Attachments:RetentionHours` (24 by default) |
| `mcp-gateway-<pid>-<run>.json` | temporary, one per agent run: created at its start and removed at its end |

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

The build then publishes both win-x64 archives (`AgentsTracker-<tag>-win-x64-self-contained.zip`
with the runtime and `AgentsTracker-<tag>-win-x64.zip` without) and creates the release. Release
notes are taken from `../release-notes/<tag>.md`, so create the file **before** pushing the tag;
no file — GitHub will collect a list of commits instead.

How to write the notes: **short, in plain human words**, from the side of the person using the bot
rather than writing it. One or two sentences on what changed overall, then a «Что нового» list —
one bullet per change, the visible result first. Only what matters: a change the user cannot notice
does not belong in the notes. No reasoning, class names, flags or edit history. The install and
update sections are boilerplate — copy them from the previous release and change the version.

The tag number without the `v` goes into `-p:Version`; the version isn't edited separately in
the csproj — it holds `0.0.0-dev` there, so a build from source is honestly distinguishable from
a released one. The gateway prints that same number as the first line of the log, puts it in the
monitor snapshot's `version` field and into the «🔌 Шлюз запущен» message.

Packaging puts the publish output into a folder named after the archive and `install.txt` from
`../install-quickstart.txt` next to it, plus `claude-settings.example.json` from
`../examples/` (`install.txt` is UTF-8 with a BOM: without it Cyrillic turns into mojibake in
Notepad on older Windows builds).

No tokens need to be set up: the workflow uses the built-in `github.token` with `contents:
write` permission. Before packaging, `appsettings.Local.json` and `appsettings.Development.json`
are removed from the publish output — in case the build ever runs from a machine where these
files exist: a plain-text token must not end up in the release archive.

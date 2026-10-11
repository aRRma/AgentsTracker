# Claude Code Permissions on the Local Machine

When to read this: you're setting up the gateway on your own PC, or handing the agent a new
project. The gateway only relays «можно?» ("can I?") questions to Telegram: what the agent asks
about, does silently, or is forbidden is decided by Claude Code's own settings files. Below is
the recommended scheme and the author's working config as a ready-made example,
`../examples/claude-settings.example.json` (verified against CLI 2.1.x).

## Three Files

| File | Who sees it | What it's for |
|---|---|---|
| `~/.claude/settings.json` | all of this user's projects | personal denials and permissions, proxy, default mode |
| `.claude/settings.json` in the project | everyone who cloned it | rules dictated by the project itself; committed |
| `.claude/settings.local.json` in the project | only you | personal settings for this project; in `.gitignore` |

The `allow`/`ask`/`deny` lists from all files are **merged**, not overridden. The check order
is fixed: `deny` → `ask` → `allow`, the first matching rule decides. So the project file can
tighten the personal one (`ask` in the project overrides the user's `allow`), but it cannot
loosen it.

## How This Combines with the Gateway

- An action under `allow` runs without a card in Telegram. Under `ask` — a card arrives. Under
  `deny` — the CLI refuses on its own, there is no card.
- `deny` applies in any mode, including `auto` and `bypassPermissions`. The «Всегда» ("Always")
  button doesn't bypass it: the «Всегда» rule is an `allow`, and `deny` is checked first.
- `defaultMode` in the personal file has no effect on the gateway: `--permission-mode` is
  passed on every run (`/mode` in chat). In project files the CLI ignores the `auto` and
  `bypassPermissions` values — they can only be set in `~/.claude/settings.json`.
- `disableBypassPermissionsMode: "disable"` closes the no-confirmation mode entirely: the gateway
  already doesn't offer it in chat, and the flag also guards against a manual
  `--dangerously-skip-permissions` on the machine itself.
- `env` from `~/.claude/settings.json` is inherited by the `claude` the gateway launches. If the
  Claude API is only reachable through a proxy, set `HTTPS_PROXY` here, and `NO_PROXY` must
  include `127.0.0.1`: the CLI reaches the gateway for approvals at that address, and a proxy
  would break it. The example's `env` has `NO_PROXY`/`no_proxy`, `MCP_TIMEOUT` and two `DOTNET_*`
  switches, no proxy.

## What to Deny, What to Ask, What to Allow

The groups and the reasoning; the full list (most rules written for both `Bash(...)` and
`PowerShell(...)`) is in `../examples/claude-settings.example.json`.

**Deny outright (`deny`):**

- secrets: the `.env` family, `appsettings.Production.json`, `secrets.json`, key and certificate
  files, `~/.ssh`, `~/.aws`, `~/.git-credentials`. Denying `Read` shuts off the contents even
  inside the project. The `.env` family and `~/.ssh` are also denied for `Edit`: they aren't in
  git, and an `.env` overwritten by the agent has nowhere to be restored from;
- network from the shell (`curl`, `wget`, `Invoke-WebRequest`, `Invoke-RestMethod`,
  `Start-BitsTransfer`): an exfiltration channel, whatever the agent read it could send out. Its
  own tools (`WebFetch`) still reach the internet, and they are visible in the card;
- irreversible git: `push --force`/`-f`/`+refspec`, `reset --hard`, `rebase`, deleting `.git`, and
  `delete_repository` of the GitHub plugin. The patterns (`git*push*--force*`, `git*reset*--h*`…)
  catch any argument order, `git -C`, `--no-pager` and `git.exe`; `--h*`, not `--hard*`, because
  git accepts the `--ha` abbreviation;
- what runs someone else's code or changes the system: `docker run`/`exec`/`rm`/`rmi`/`volume rm`/`system
  prune`, `sudo`, `dotnet nuget add source`/`push`/`config`, `dotnet tool uninstall`;
- destroying data: `mongosh` with `drop`/`delete*`/`remove`, `rm -rf bin`/`obj`.

**Ask (`ask`):**

- git after which work is lost or leaves the machine: `push` (a `v*` tag is a release),
  `checkout`/`restore`, forced `switch`, every form of `branch -D`, `stash drop`/`clear`, `clean`,
  `worktree remove --force`;
- writes to GitHub through the plugin (`push_files`, `merge_pull_request`…): they write in your
  name past every git rule;
- editing `~/.claude/settings.json` and the hooks folder: the agent shouldn't expand its own
  permissions;
- reading .NET user secrets (`~/AppData/Roaming/Microsoft/UserSecrets`);
- PowerShell `Remove-Item`, stopping processes, stopping and unregistering scheduled tasks.

**No questions asked (`allow`):**

- reading everything (`Read(**/*)`), minus what's in `deny`;
- `dotnet build/test/format/restore/run`, `dotnet tool`, `dotnet dev-certs`;
- git that `git reflog` can undo (reading, `add`, `commit`, `reset`, `merge`, `cherry-pick`,
  `fetch`, `tag`, `switch`, `stash`, `worktree`), also through `git -C`. The dangerous variants of
  the same commands sit in `ask`/`deny` and win;
- GitHub plugin reads, tools named one by one (`get_*`, `list_*`, `search_*`, `issue_read`,
  `pull_request_read`), not by a wildcard;
- diagnostics (`tasklist`, `netstat`, `Get-Process`, `docker ps`/`logs`, `tail`, `head`…) and the
  Playwright tools, `npx playwright`, `npm run test`.

A mistake in `allow` is cheaper than one in `deny`: an extra question is annoying, an extra
permission lets an action through silently.

## Moving to Another PC

The rules live in `~/.claude/settings.json` of the Windows user the gateway runs as (`install`
creates the Task Scheduler task for the current user): `%USERPROFILE%\.claude\settings.json`.

1. Sign in as that user and run `claude` once — it creates `~/.claude`.
2. No `settings.json`, or nothing important in it — copy the example whole. In a release archive
   it sits next to `install.txt`, in the sources — in `docs\examples\`:

   ```powershell
   Copy-Item claude-settings.example.json $env:USERPROFILE\.claude\settings.json
   ```

   The file already exists (plugins, `env`, hooks) — do not overwrite it; move over only the three
   arrays `permissions.allow`, `ask`, `deny`. Duplicate lines do no harm: the lists are merged.
   The example also sets `defaultMode: "auto"` (for your own interactive sessions, not the
   gateway) and `disableBypassPermissionsMode`: take them if you want them.
3. Proxy — per your network (`HTTPS_PROXY` in `env`, `NO_PROXY` with `127.0.0.1`, see above).
4. Do not put tokens in `env`: the agent reads this file without asking.
5. The habits that keep the agent on the "no question" side go into `~/.claude/CLAUDE.md`:

   ```markdown
   # Git

   - Ветки — через `git switch`, не `git checkout`: `checkout` спрашивает всегда.
   - Убрать файл из индекса — `git reset -- <файл>`, не `git restore --staged`.
   - Удалить слитую ветку — `git branch -d`, не `-D`.
   - git вызывать как `git` — не `git.exe`, не через `wsl`, `bash -c` или `pwsh -c`.
   - Force push, `reset --hard` и `rebase` запрещены. Вместо rebase — `merge`.
   ```

6. Check: in Claude Code on that machine ask it to run `git log -1 --grep="push x --force"`.
   The right outcome is a refusal ("denied"): the deny rule fired.

**Docker.** The container has its own profile — the `claude-home` volume (`/home/app/.claude`).
Write the example there as the container's user, so the CLI can later change the file itself
(enabling plugins):

```bash
docker compose exec -T gateway sh -c "cat > /home/app/.claude/settings.json" < docs/examples/claude-settings.example.json
```

The `PowerShell(...)` and `git.exe` rules are not needed on Linux, but do no harm.

## Syntax Pitfalls

- **`Bash(...)` and `PowerShell(...)` are different tools.** A `Bash(git push -f*)` rule
  won't stop `git push -f` run through the PowerShell tool, so every deny and every ask is
  duplicated for both. The CLI resolves PowerShell aliases (`rm`, `curl`, `iwr`) to full names,
  so `PowerShell(Invoke-WebRequest *)` also catches `curl`, but not `curl.exe` — that needs its
  own rule.
- `*` works anywhere in the string (`Bash(mongosh*drop*)`), but a rule matches from the start:
  `git push --force*` misses `git -C <folder> push --force`, `git push origin master --force`,
  `git push -f` and `git push origin +master`. So the git denies are written `git*push*--force*`
  plus separate `-f` and `+` rules: a false hit (the same words in a commit message) costs one
  rewording. `ask` is written the same way (`git*checkout*`, `git*stash*drop*`), except `push` and
  `clean`: those words are common in commit messages and `git stash push` is an ordinary command,
  so they get the `git push*` + `git -C * push*` pair.
- `allow` always comes as a `git -C * …` pair and names a subcommand, never a global option:
  `git --no-pager*` would silently admit `git --no-pager push` and
  `git --no-pager checkout -- file`, which an `ask` on `git push*` matching from the start does
  not recognise. Wrappers (`wsl git`, `bash -c "git …"`, `pwsh -c`) match no `allow` and go to a
  card or the `auto` mode classifier, so they need no rules of their own.
- An `allow` on a broad prefix lets the destructive variant through too: `git switch*` admits
  `git switch -f`, `git branch*` admits `git branch -D`. `ask` and `deny` beat `allow`, so the
  dangerous variants are listed there separately; branch deletion has many forms (`-D`, `-df`,
  `-fd`, `-d -f`, `-f -d`, `--force`) and each is a rule.
- A space before `*` is part of the pattern: `Bash(tail *)` won't let `tailwind` through,
  `Bash(tail*)` will. `Bash(git status)` without an asterisk matches only that exact command
  with no arguments.
- Compound commands are parsed by parts: `deny` fires if at least one part of `a && b`, `$(…)`,
  `a; b` matches. For `allow` it's the opposite — every part must be allowed. Checked on
  11.10.2026 in both Bash and PowerShell: `git log -1; git stash drop` asked about `drop`.
- Paths: `//**/.env` — anywhere on the filesystem, `~/…` — from the home folder, `**/.env` —
  only inside the current project. A single leading `/…` is **not** the disk root, but the
  root of the settings source; don't use it.
- `mcp__server__*` in `allow` requires the server name (`mcp__plugin_playwright_playwright__*`);
  a bare `mcp__*` there silently does nothing, but in `deny` it does work.

## This Project's Rules

The `.claude/settings.json` file at the root closes off what the personal config doesn't need
to know about:

- `mcp-gateway-<pid>-<run>.json` — the token the `claude` process uses to reach the gateway:
  reading is `deny`. `.env` is denied for reading and editing here too, so the protection holds
  without the personal file;
- `appsettings.Local.json` — the bot token: reading and editing both ask. If `protect-secrets`
  has already run it holds `dpapi:…`, but the rule is cheaper than checking;
- `dotnet run`, `AgentsTracker.Gateway.exe`, `schtasks`, `docker compose`, `Stop-Process`,
  `Stop-ScheduledTask` ask: from a session started through Telegram they would stop or duplicate
  the gateway itself (`operations.md`). This `ask` beats the personal `allow` on `dotnet run`;
- `scripts\monitor-api.ps1` — no question. The `curl`/`Invoke-RestMethod` deny doesn't
  distinguish the internet from `127.0.0.1`, and the agent kept running into it trying to read the
  monitor's `/api/snapshot`. The script only reaches loopback and only reads; widening the rule to
  `Invoke-RestMethod http://127.0.0.1*` won't work — `deny` is checked before `allow`.

Project-personal settings go in `.claude/settings.local.json`, which is in `.gitignore`.

## Hooks

`hooks` in `~/.claude/settings.json` is the same restriction mechanism, but programmatic. The
author has a `PreToolUse` hook on `Agent` that swaps subagent models to `sonnet` (the "Subagents
only with `model: "sonnet"`" rule from `../../CLAUDE.md`). Hooks are scripts kept outside the
project and are not included in the example; editing the hooks folder is set to `ask` so
the agent can't rewrite its own restrictions.

The hook and `CLAUDE_CODE_SUBAGENT_MODEL` in `env` set the `sonnet` alias; the CLI decides what it
resolves to. CLI 2.1.283 maps it to `claude-sonnet-5` and does not know the later
`claude-sonnet-5-5`. So the author's own `env` (not in the example) holds
`"ANTHROPIC_DEFAULT_SONNET_MODEL": "claude-sonnet-5-5[1m]"`; subagents, the `sonnet` button in the
chat and `/ask` (default model `sonnet`) all go there. The `[1m]` is required: the CLI gives a
model it does not know a 200 000 window, not 1 000 000. The variable pins the version — once the
CLI knows `sonnet-5-5` itself or a newer one ships, remove it, or `sonnet` stays on 5-5.

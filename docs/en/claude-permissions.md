# Claude Code Permissions on the Local Machine

When to read this: you're setting up the gateway on your own PC, or handing the agent a new
project. The gateway only relays «можно?» ("can I?") questions to Telegram — what the agent
asks about, what it does silently, and what is permanently forbidden to it is decided by
Claude Code's own settings files. Below is the recommended scheme and a ready-made example:
`../examples/claude-settings.example.json`. This is the config the author runs on; verified
against CLI 2.1.x.

## Three Files

| File | Who sees it | What it's for |
|---|---|---|
| `~/.claude/settings.json` | all of this user's projects | personal denials and permissions, proxy, default mode |
| `.claude/settings.json` in the repository | everyone who cloned it | rules dictated by the project itself; committed |
| `.claude/settings.local.json` in the repository | only you | personal settings for this project; in `.gitignore` |

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
- `disableBypassPermissionsMode: "disable"` closes the no-confirmation mode entirely. The
  gateway already doesn't let you pick it from chat, but the flag also guards against a manual
  `--dangerously-skip-permissions` on the machine itself.
- `env` from `~/.claude/settings.json` is also inherited by the `claude` process the gateway
  launches. If the Claude API is only reachable through a proxy — set `HTTPS_PROXY` here, and
  `NO_PROXY` must include `127.0.0.1`: the CLI reaches the gateway for approvals at that
  address, and it won't get through a proxy.

## What to Deny, What to Ask, What to Allow

Below is the principle and representative examples, not a full list: that's in
`../examples/claude-settings.example.json`, with more detail per category.

**Deny outright (`deny`):**

- secrets: `.env*`, `secrets.json`, `*.pfx`, `*.pem`, `*.key`, `~/.ssh`, `~/.aws`,
  `~/.git-credentials`. Denying `Read` shuts off the contents even if the file is inside the
  project. For the `.env` family and `~/.ssh`, the example also denies `Edit`: these files
  aren't in git, and an `.env` overwritten by the agent has nowhere to be restored from;
- network access from the shell: `curl`, `wget`, `Invoke-WebRequest`, `Invoke-RestMethod`. This
  is an exfiltration channel: whatever the agent read, it could send out. It can still reach
  the internet through its own tools (`WebFetch`), which are visible in the card;
- irreversible git actions: `push --force`, `reset --hard`, `rebase`, deleting `.git`;
- anything that runs someone else's code or changes the system: `docker run`/`exec`, `sudo`,
  `dotnet nuget add source`, `dotnet nuget push`.

**Ask (`ask`):**

- `git push`, `git checkout`/`restore`, `git clean`, `git branch -D`, `git stash drop` —
  reversible, but easy to lose work over;
- editing `~/.claude/settings.json` and hooks: the agent shouldn't be able to expand its own
  permissions;
- deleting files from PowerShell (`Remove-Item`), stopping processes and scheduled tasks.

**No questions asked (`allow`):**

- reading everything (`Read(**/*)`) — minus what's in `deny`;
- build and tests: `dotnet build/test/format/restore`;
- read-only git: `status`, `diff`, `log`, `show`, `blame`, `branch`;
- diagnostics: `tasklist`, `netstat`, `Get-Process`, `docker ps`/`logs`, `tail`, `head`.

A mistake in `allow` is cheaper than one in `deny`: an extra question is annoying, an extra
permission lets an action through silently.

## Syntax Pitfalls

- **`Bash(...)` and `PowerShell(...)` are different tools.** A `Bash(git push -f*)` rule
  won't stop `git push -f` run through the PowerShell tool. Duplicate every deny and every ask
  for both; the example does this. The CLI resolves PowerShell aliases (`rm`, `curl`, `iwr`) to
  their full names, so `PowerShell(Invoke-WebRequest *)` also catches `curl`, but not
  `curl.exe` — that needs its own rule.
- `git push --force*` doesn't catch `git push -f` — two separate rules. On the other hand `*`
  works anywhere in the string: `Bash(mongosh*drop*)`.
- A space before `*` is part of the pattern: `Bash(tail *)` won't let `tailwind` through,
  `Bash(tail*)` will. `Bash(git status)` without an asterisk matches only that exact command
  with no arguments.
- Compound commands are parsed by parts: `deny` fires if at least one part of `a && b`, `$(…)`,
  `a; b` matches. For `allow` it's the opposite — every part must be allowed.
- Paths: `//**/.env` — anywhere on the filesystem, `~/…` — from the home folder, `**/.env` —
  only inside the current project. A single leading `/…` is **not** the disk root, but the
  root of the settings source; don't use it.
- `mcp__server__*` in `allow` requires the server name; a bare `mcp__*` there silently does
  nothing, but in `deny` it does work.

## This Repository's Rules

The `.claude/settings.json` file at the root closes off what the personal config doesn't need
to know about:

- `mcp-gateway-<pid>.json` — the token the `claude` process uses to reach the gateway: don't
  read it;
- `appsettings.Local.json` — the bot token: reading and editing both require a question. If
  `protect-secrets` has already run, it holds `dpapi:…`, but the rule is cheaper than checking;
- `dotnet run`, `AgentsTracker.Gateway.exe`, `schtasks`, `docker compose`, `Stop-Process` — with
  a question: from a session started through Telegram, these would stop or duplicate the
  gateway itself (`operations.md`);
- `scripts\monitor-api.ps1` — no question. The `curl`/`Invoke-RestMethod` deny doesn't
  distinguish the internet from `127.0.0.1`, and the agent kept running into it session after
  session trying to read the monitor's `/api/snapshot`. The script only reaches loopback and
  only reads; widening the rule to `Invoke-RestMethod http://127.0.0.1*` won't work — `deny` is
  checked before `allow`.

Project-personal settings go in `.claude/settings.local.json`, which is in `.gitignore`.

## Hooks

`hooks` in `~/.claude/settings.json` is the same restriction mechanism, but programmatic. The
author has a `PreToolUse` hook on `Agent` that swaps subagent models to `sonnet` (the
"Subagents only sonnet" rule from `../../CLAUDE.md`). Hooks are scripts kept outside the
repository and are not included in the example; editing the hooks folder is set to `ask` so
the agent can't rewrite its own restrictions.

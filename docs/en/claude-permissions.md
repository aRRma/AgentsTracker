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
- irreversible git actions: `push --force`, `reset --hard`, `rebase`, deleting `.git` — as
  `git*push*--force*`, `git*reset*--h*`, `git*rebase*` patterns: they catch any argument
  order, `git -C`, `--no-pager` and `git.exe`. `--h*`, not `--hard*`: git accepts the `--ha`
  abbreviation;
- deleting a repository through the GitHub plugin (`delete_repository`);
- anything that runs someone else's code or changes the system: `docker run`/`exec`, `sudo`,
  `dotnet nuget add source`, `dotnet nuget push`.

**Ask (`ask`):**

- git after which work is lost or leaves the machine: `git push` (a `v*` tag is a release),
  `git checkout`/`restore` (they wipe changes), `git switch -f`/`--discard-changes`,
  `git branch -D`, `git stash drop`/`clear`, `git clean`, `git worktree remove --force`;
- writes to GitHub through the plugin: `push_files`, `create_or_update_file`,
  `merge_pull_request` and the rest — they write in your name past every git rule;
- editing `~/.claude/settings.json` and hooks: the agent shouldn't be able to expand its own
  permissions;
- deleting files from PowerShell (`Remove-Item`), stopping processes and scheduled tasks.

**No questions asked (`allow`):**

- reading everything (`Read(**/*)`) — minus what's in `deny`;
- build and tests: `dotnet build/test/format/restore`;
- git that `git reflog` can undo: reading, `add`, `commit`, `reset`, `merge`, `cherry-pick`,
  `fetch`, `tag`, `switch`, `stash`, `worktree` — and all of it through `git -C`. The dangerous
  variants of the same commands sit in `ask`/`deny` and win;
- reading through the GitHub plugin: `get_*`, `list_*`, `search_*`;
- diagnostics: `tasklist`, `netstat`, `Get-Process`, `docker ps`/`logs`, `tail`, `head`.

A mistake in `allow` is cheaper than one in `deny`: an extra question is annoying, an extra
permission lets an action through silently.

## Moving to Another PC

The rules live not in the gateway but in `~/.claude/settings.json` of the Windows user the
gateway runs as. `install` creates the Task Scheduler task for the current user, so the file
belongs in that user's profile: `%USERPROFILE%\.claude\settings.json`.

1. Sign in as that user and run `claude` once — it creates `~/.claude`.
2. No `settings.json`, or nothing important in it — copy the example whole. In a release archive
   it sits next to `install.txt`, in the sources — in `docs\examples\`:

   ```powershell
   Copy-Item claude-settings.example.json $env:USERPROFILE\.claude\settings.json
   ```

   The file already exists (plugins, `env`, hooks) — do not overwrite it; move over only the three
   arrays `permissions.allow`, `ask`, `deny` from the example. Duplicate lines do no harm: the
   lists are merged.
3. Proxy — per your network: `HTTPS_PROXY` in `env`, `NO_PROXY` must include `127.0.0.1`
   (section above). The example has no proxy.
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
  won't stop `git push -f` run through the PowerShell tool. Duplicate every deny and every ask
  for both; the example does this. The CLI resolves PowerShell aliases (`rm`, `curl`, `iwr`) to
  their full names, so `PowerShell(Invoke-WebRequest *)` also catches `curl`, but not
  `curl.exe` — that needs its own rule.
- `git push --force*` doesn't catch `git push -f` — two separate rules. On the other hand `*`
  works anywhere in the string: `Bash(mongosh*drop*)`.
- A rule matches from the start of the string: `git -C <folder> push --force` and
  `git push origin master --force` do not match `git push --force*`. So the git denies are written
  as `git*push*--force*`: a false hit (the same words in a commit message) costs one rewording.
  `ask` is written the same way (`git*checkout*`, `git*stash*drop*`), except `push` and `clean`:
  those words are common in commit messages and `git stash push` is an ordinary command, so they
  get the `git push*` + `git -C * push*` pair. `allow` always comes as a `git -C * …` pair: a broad
  `allow` would admit whatever is missing from `ask`.
- Wrappers (`wsl git`, `bash -c "git …"`, `pwsh -c`) match no `allow` and go to a card or the
  `auto` mode classifier. They need no rules of their own.
- A git `allow` names a subcommand, not a global option: `git --no-pager*` would silently admit
  `git --no-pager push` and `git --no-pager checkout -- file` — an `ask` on `git push*` matching
  from the start does not recognise them. For the same reason branch deletion in `ask` is written
  as `git*branch*-D*`, `-df`, `-fd`, `-d -f`, `--force`: it has many forms.
- An `allow` on a broad prefix lets the destructive variant through too: `git switch*` admits
  `git switch -f`, `git branch*` admits `git branch -D`. `ask` and `deny` beat `allow`, so the
  dangerous variants are listed there separately.
- A space before `*` is part of the pattern: `Bash(tail *)` won't let `tailwind` through,
  `Bash(tail*)` will. `Bash(git status)` without an asterisk matches only that exact command
  with no arguments.
- Compound commands are parsed by parts: `deny` fires if at least one part of `a && b`, `$(…)`,
  `a; b` matches. For `allow` it's the opposite — every part must be allowed. Checked on
  11.10.2026 in both Bash and PowerShell: `git log -1; git stash drop` asked about `drop`.
- Paths: `//**/.env` — anywhere on the filesystem, `~/…` — from the home folder, `**/.env` —
  only inside the current project. A single leading `/…` is **not** the disk root, but the
  root of the settings source; don't use it.
- `mcp__server__*` in `allow` requires the server name; a bare `mcp__*` there silently does
  nothing, but in `deny` it does work.

## This Repository's Rules

The `.claude/settings.json` file at the root closes off what the personal config doesn't need
to know about:

- `mcp-gateway-<pid>-<run>.json` — the token the `claude` process uses to reach the gateway: don't
  read it;
- `appsettings.Local.json` — the bot token: reading and editing both require a question. If
  `protect-secrets` has already run, it holds `dpapi:…`, but the rule is cheaper than checking;
- `dotnet run`, `AgentsTracker.Gateway.exe`, `schtasks`, `docker compose`, `Stop-Process`,
  `Stop-ScheduledTask` — with a question: from a session started through Telegram, these would
  stop or duplicate the gateway itself (`operations.md`);
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

The hook and `CLAUDE_CODE_SUBAGENT_MODEL` in `env` set the `sonnet` alias, and what it resolves to
is up to the CLI. CLI 2.1.283 maps it to `claude-sonnet-5`: it does not know the later
`claude-sonnet-5-5`. So `env` holds `"ANTHROPIC_DEFAULT_SONNET_MODEL": "claude-sonnet-5-5[1m]"` —
subagents, the `sonnet` button in the chat and `/ask` all go there. The `[1m]` is required: the CLI
gives a model it does not know a 200 000 window, not 1 000 000. The variable pins the version —
once the CLI knows `sonnet-5-5` itself or a newer one ships, remove it, or `sonnet` stays on 5-5.

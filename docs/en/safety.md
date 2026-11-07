# Risks and How to Close Them

When to read this: before giving the bot access to your home folders. The bot is your computer's
command line in Telegram — convenient, and it demands care. Below are the risks, from the most
likely to the rare and expensive. The full rule set for the machine and a ready-made settings file
are in [Claude Code permissions](claude-permissions.md). This page is about what a person does.

## 1. It Understood You Differently

The most common risk. No ill intent: you said "sort these into folders", and the photos went
somewhere else.

**How to close it:**

- ask it to **move**, not delete: "duplicates into `_sort-later`, delete nothing". A move can be
  undone, a delete cannot;
- in a new project, start with "show me what you're about to do, change nothing yet";
- keep a backup of what you'd miss: OneDrive, Windows File History, an external drive;
- keep deleting files and stopping programs behind a question (`ask` in the permission settings).
  Each such action then arrives as a confirmation card.

## 2. Someone Else Got to the Bot

Anyone can find your bot in Telegram and write to it. Only the list of allowed users stands between
you and a stranger.

**How to close it:**

- fill in `Channel:Settings:AllowedUserIds`. With an empty list the gateway does not start. A
  message from anyone not on the list is ignored and written to the journal (`/audit`);
- the gateway answers in private chats only, never in groups;
- keep the bot token out of chats, screenshots and public repositories. If it leaked: `/revoke` at
  @BotFather and a new token in the settings. If you lost your phone, end its Telegram session from
  another device.

## 3. Something Private Ended Up Where It Shouldn't

Two things. The agent can **send a file into the chat**, and Telegram keeps the conversation. Also,
your task and the pieces of files the agent read go to Anthropic, otherwise it could not answer.

**How to close it:**

- decide in advance what you never send: passwords, codes, photos of documents;
- keep separate projects: "Documents for the bot" and everything else. The gateway sends a file only
  from the current project, only of the allowed types (`.md .txt .json .cs .js .html .zip .7z`,
  pictures `.png .jpg .jpeg`), and never from its own data folder;
- deny reading what must never travel anywhere (`.env`, `~/.ssh`, the password manager) with a
  one-time `deny` rule. «Всегда» cannot get around it. A secret inside the project is not covered
  by the send limit: `appsettings.Local.json` is a `.json`, and a copy of `.env` named `.txt` passes
  too. Keep secrets outside the project. A ban for one project goes into its
  `.claude/settings.local.json`:

  ```json
  { "permissions": { "deny": ["Read(./.env)"] } }
  ```
- read a "pack it and send it" request carefully. The gateway checks the archive itself, not what
  is packed inside;
- a picture from the chat lands in `inbox` of the data folder, and the agent sees only that chat's
  folder. Only PNG and JPEG are accepted, the type is checked by content. `/new` deletes the
  session's pictures, the rest are deleted after `RetentionHours` (24 hours);
- a беседа and `/ask` search the web and open sites with no card. They have no files and no
  commands, but a page can talk the model into opening an address with the conversation in it. Don't
  write in a беседа what must not leave the chat.

## 4. Permissions Piled Up and the Questions Stopped

The «Всегда» ("Always") button is convenient and quietly turns into "do whatever you want with this
command". In the `auto` mode cards almost never arrive.

**How to close it:**

- once a month look at `/rules`. `/rules del <n>` removes one rule, `/rules clear` removes all.
  `/rules` shows only the gateway's rules. Usually the agent writes «Всегда» itself, into
  `.claude/settings.local.json` in the project, so check that file too;
- «Всегда» belongs to one project. Don't copy such rules around "just in case";
- keep the `default` mode, where cards arrive. Use `auto` only while you watch the chat. Switching
  confirmations off entirely (`dontAsk`, `bypassPermissions`) is impossible from the chat, only by
  editing a file on the machine;
- if a command or an edit does not fit into the card, a file with the full text comes before it —
  don't allow without looking;
- «Плагины» in `/skills` edit your `~/.claude/settings.json` — the same as `/plugin` in Claude Code;
- `/audit` shows the latest actions: who allowed what, and what was run. A card left without an
  answer for 15 minutes (`ApprovalTimeoutMinutes`) is refused, and the agent is told not to repeat
  the action: a forgotten card cannot grant permission later.

## 5. The Agent Reached Past the Chosen Project

`/project` says where the agent works, not what it can reach. A command run from that folder can
read anything on the disk that your account can read. Only the limit on sending files into the chat
is real.

**How to close it:**

- don't let commands that go online run silently (`curl`, `wget`, `Invoke-WebRequest`). They turn
  "it read" into "it sent it out";
- for a real fence, run the gateway in Docker and mount only the folders you work with;
- if the computer holds other people's data, create a separate Windows account for the bot.

## 6. Someone Else's Project Brought Its Own Commands

A project may hold settings for the agent: the `.claude` folder and the `.mcp.json` file. The agent
runs them on its own, with no card, for example a command at the start of every task. In your own
project that is handy. In a downloaded one it is someone else's code on your computer.

**How to close it:**

- before the first task in such a project the gateway sends a «Доверять проекту?» ("Trust the
  project?") card with the list of what will run. If you don't recognize it, press «❌ Отклонить».
  The task does not start and the queued messages are dropped. No answer counts the same way;
- if those files change, the card comes again;
- read `.claude/settings.json` and `.mcp.json` of a foreign project yourself before you trust it.

## 7. A Long Task Ate the Plan's Limit

"Sort out the whole photo archive" can cost a day's worth of the subscription. By default one run is
cut off after an hour (`RunTimeoutMinutes`).

**How to close it:**

- `/usage` shows what is left in the current window. When the window is used up, the gateway starts
  no new run and drops the queue;
- ask for big things in parts: "start with the January folder";
- `/stop` interrupts a task: in a topic only that topic, in the лента everything. `/status` shows
  what is running;
- topics run in parallel, and each one spends the same limit. Keep the heavy tasks in one topic.

## 8. Someone Else Can See the Computer

The web monitor at `127.0.0.1:5100` has no password. It changes nothing, but it shows the run
history and the journal to anyone logged into this computer.

**How to close it:**

- if you don't need it, turn it off: `"MonitorPort": 0`. In a container, publish the port as
  `127.0.0.1:5100:5100`, not outward;
- a site open in your browser cannot read it: the gateway answers only to `127.0.0.1` or
  `localhost`;
- the port `claude` asks for approvals on (`McpPort`, 5099) is also open only on `127.0.0.1` and
  closed with a secret token;
- tokens and passwords in commands reach the journal, the log file and the monitor as `***`. The
  gateway recognizes known token shapes and the values of keys like `token` and `password`. The
  console and `docker compose logs` are barely masked — do not show them to others;
- after `install` (or `protect-secrets`) the bot token on disk is encrypted (DPAPI), and only your
  account on this machine can decrypt it. A container has no DPAPI, so keep its `.env` to yourself.

## 9. The «Работа» Topic Opens the Whole Computer

A «Работа» topic is remote work on the computer: commands, any files, your MCP servers (for
example the browser). Whoever gets into your Telegram account gets the PC. It is off by default.

**How to close it:**

- the topic type is turned on only by editing the config on the machine: `Gateway:Work:AllowedUserIds`
  lists who may use it. Empty means nobody sees the button. Keep the list to the people who really
  need it;
- turn on two-step verification in Telegram (the cloud password): it protects the account that
  controls the PC;
- the mode is fixed at `default` and `/mode` is refused there. What your own rules in
  `~/.claude/settings.json` do not allow comes as a card. Keep `deny` for what must never run;
- cards in «Работа» have no «Всегда» button: one tap would allow a command on the whole machine for
  good. `/rules` there says there are no rules;
- web search runs with no card, but opening a page asks. The agent can read any file, and a page
  could talk it into sending a file's contents to an address;
- access is checked on the button and again before every task. If you remove an id from the config,
  the topic's tasks are refused with «⛔ Тема «Работа» недоступна», the queue is dropped and the
  journal records it. Choose another type with `/topic`;
- the gateway refuses, with no card, an edit of files in its own data folder (state, settings, the
  token) in any topic. It does not see shell commands, so those rely on the card and your `deny`
  rules. Reading that folder is not blocked: keep secrets out of reach with `deny`;
- the agent sends a file or screenshot only from the topic's own folder (it copies it there first),
  with the same type limits;
- admin rights are not available: the gateway runs as your account without elevation and cannot
  answer a Windows UAC prompt. In Docker «the whole computer» is only the container.

## The Five-Minute Checklist

1. `AllowedUserIds` is filled in, and the token is not lying around in the open.
2. The mode is `default`, and cards arrive.
3. There is a separate "for the bot" project. Secrets and keys are outside it and in the denials.
4. Deleting files and stopping programs raise a question.
5. `/rules` and `.claude/settings.local.json` are empty or make sense.
6. You trust a foreign project only after reading its `.claude/settings.json`.
7. A backup of what you'd miss exists and does not depend on this machine.
8. `Work:AllowedUserIds` is empty or holds only the people who need it, and Telegram has the cloud
   password on.

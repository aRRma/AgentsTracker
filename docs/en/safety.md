# Risks and How to Close Them

When to read this: before giving the bot access to home folders. The bot is your computer's command
line in Telegram, and that is convenient exactly as far as it demands care. Below is an honest list
of what goes wrong, ordered by likelihood: at the top, what everyone runs into; at the bottom, what
happens rarely but expensively.

The full rule set for the machine and a ready-made settings file are in
[Claude Code permissions](claude-permissions.md); this page is about what the human does.

## 1. It Understood You Differently

The most common and most boring risk: no ill intent, just "sort these into folders" — and the
photos moved somewhere else, with the original order left to restore by hand.

**How to close it:**

- ask it to **move**, not to delete: "duplicates into `_sort-later`, delete nothing". A move can be
  undone, a delete cannot;
- on a new project, start with "show me what you're about to do, change nothing yet";
- keep a backup of what you'd miss: OneDrive, Windows File History, an external drive;
- leave deleting files and stopping programs on a question (`ask` in the permission settings) —
  then every such action arrives as a confirmation card with the list.

## 2. Someone Else Got to the Bot

Anyone can find your bot in Telegram and write to it. The only thing separating you from a stranger
is the list of allowed users.

**How to close it:**

- fill in `Channel:Settings:AllowedUserIds`. An empty list — the gateway does not start. A message
  from anyone not on the list is ignored and recorded in the journal (`/audit`);
- the gateway answers in private chats only and ignores group chats;
- the bot token is the key to the bot: don't put it in a chat, a screenshot or a repository. If it
  leaked — `/revoke` at @BotFather and a new token in the settings;
- lost your phone — end the Telegram session from another device (Settings → Devices).

## 3. Something Private Ended Up Where It Shouldn't

Two different things. First: the agent **sends a file into the chat** — and Telegram keeps the
conversation on its side. Second: your task and the pieces of files the agent read go to Anthropic,
otherwise it couldn't answer.

**How to close it:**

- decide in advance what you never send: passwords, codes, photos of documents on someone else's
  request;
- split the projects: "Documents for the bot" and everything else. The gateway sends a file into the
  chat only from the current project, only of the allowed types (`.md .txt .json .cs .js .html .zip
  .7z`, pictures `.png .jpg .jpeg`) and never from the gateway's data folder;
- deny reading what must never travel anywhere — `.env`, `~/.ssh`, the password manager, the folder
  with keys. That's a one-time setting in the permission rules. A `.env` or a config with secrets
  inside the project is not protected by the send-file limit — keep them outside the project;
- remember the archive: the gateway checks the path and type of the archive itself, but not what is
  packed inside. Read a "pack it and send it" request carefully;
- a Telegram secret chat does not apply: the bot always lives in an ordinary one.

## 4. Permissions Piled Up and the Questions Stopped

The «Всегда» ("Always") button is convenient and quietly turns into "do whatever you want with this
command". Plus the `auto` mode, where cards almost never arrive.

**How to close it:**

- once a month look at `/rules` — the rules the gateway keeps for this project; `/rules del <n>`
  removes one, `/rules clear` removes all. Usually «Всегда» is written by the agent itself into
  `.claude/settings.local.json` in the project — `/rules` does not show those, check that file too;
- «Всегда» is bound to the project: what's allowed in one doesn't apply in another. Don't copy such
  rules around "just in case";
- keep the `default` mode (cards arrive). `auto` is for when you're sitting right there watching
  the chat. Switching off confirmations entirely (`dontAsk`, `bypassPermissions`) is impossible from
  the chat — only by editing the file on the machine;
- denials beat any permission: what's in `deny` the «Всегда» button will not get around;
- `/audit` shows the latest actions — who allowed what, and what was run.

## 5. The Agent Reached Past the Chosen Project

Picking a project with `/project` says where the agent works, not what it can reach: a command run
from that folder can read anything on the disk under your account. The limit on sending files into
the chat is real; the "it only sees the project" limit is not.

**How to close it:**

- don't let commands that go online run silently (`curl`, `wget`, `Invoke-WebRequest`): those are
  what turn "it read" into "it sent it out". The agent's own tools are visible in the card — that's
  fine;
- if you want a real fence, run the gateway in Docker and mount exactly the folders you work with.
  That's the most reliable option there is;
- create a separate Windows account for the bot if the computer holds other people's data.

## 6. Someone Else's Project Brought Its Own Commands

A project may hold the agent's own settings — the `.claude` folder and the `.mcp.json` file. The
agent runs them on its own, with no confirmation card: a command at the start of every task, for
example. In your own project that's convenient; in a downloaded foreign one it's someone else's
code on your computer.

**How to close it:**

- before the first task in such a project the gateway sends a «Доверять проекту?» ("Trust the
  project?") card with the list of what the agent will run on its own. Don't recognize the
  commands — press «❌ Отклонить»: the task does not start. No answer counts the same way;
- those files changed — the card comes again, even if you trusted the project before;
- look at a foreign project with your own eyes first: open `.claude/settings.json` and
  `.mcp.json`, and only then trust it.

## 7. A Long Task Ate the Plan's Limit

"Sort out the whole photo archive" can cost a day's worth of the subscription and still hit the
one-hour ceiling (`RunTimeoutMinutes`) halfway through.

**How to close it:**

- `/usage` shows what's left in the current window. When the window is used up, the gateway starts
  no new run and drops the queue;
- ask for big things in parts: "start with the January folder";
- `/stop` interrupts a task, `/status` shows what's running.

## 8. Someone Else Can See the Computer

The web monitor at `127.0.0.1:5100` is open without a password: it changes nothing, but it shows
the run history and the journal to anyone logged into this computer.

**How to close it:**

- don't need it — turn it off: `"MonitorPort": 0`;
- in a container, publish the port as `127.0.0.1:5100:5100`, not outward;
- a site open in your browser can't read the monitor either: the gateway answers only to the
  address `127.0.0.1` or `localhost`, not to another site's name;
- tokens and passwords in commands land in the journal, the log and the monitor as `***` (known
  token shapes and values of keys like `token`, `password`);
- the bot token on disk is encrypted (DPAPI) and only your account on this machine can decrypt it;
  a container has no DPAPI — keep the `.env` to yourself there.

## 9. No Answer, and the Card Just Hangs

Not a risk, but it worries people: if no button is pressed for 15 minutes, the action is refused
and the agent is told not to repeat it. Silence means "no", and that's right — a forgotten card
can't grant permission after the fact.

## The Five-Minute Checklist

1. `AllowedUserIds` is filled in, the token isn't lying around in the open.
2. The mode is `default`, cards arrive.
3. There's a separate "for the bot" project; secrets and keys are outside it and in the denials.
4. Deleting files and stopping programs raise a question.
5. `/rules` is empty or makes sense, `.claude/settings.local.json` too, `/audit` has been looked at
   at least once.
6. You don't trust a foreign project until you've read its `.claude/settings.json`.
7. A backup of what you'd miss exists and doesn't depend on this machine.

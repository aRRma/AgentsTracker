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
- on a new folder, start with "show me what you're about to do, change nothing yet";
- keep a backup of what you'd miss: OneDrive, Windows File History, an external drive. That's worth
  having without the bot too;
- leave deleting files and stopping programs on a question (`ask` in the permission settings) —
  then every such action arrives as a card with the list.

## 2. Someone Else Got to the Bot

Anyone can find your bot in Telegram and write to it. The only thing separating you from a stranger
is the list of allowed users.

**How to close it:**

- fill in `Channel:Settings:AllowedUserIds` — without it the bot shouldn't run at all;
- group chats are ignored by the bot itself, private chats only;
- the bot token is the key to the bot: don't put it in a chat, a screenshot or a repository. If it
  leaked — `/revoke` at @BotFather and a new token in the settings;
- lost your phone — end the Telegram session from another device (Settings → Devices). Until
  someone is inside your Telegram, the bot won't listen to them.

## 3. Something Private Ended Up Where It Shouldn't

Two different things. First: the bot **sends a file into the chat** — and Telegram keeps the
conversation on its side. Second: the task and the pieces of files the agent read go to Anthropic,
otherwise it couldn't answer.

**How to close it:**

- decide in advance what you never send: passwords, codes, photos of documents on someone else's
  request;
- split the folders: "Documents for the bot" and everything else. The bot works in the chosen
  folder, and only sends files into the chat from there;
- deny reading what must never travel anywhere — `.env`, `~/.ssh`, the password manager, the folder
  with keys. That's a one-time setting;
- remember the archive: the type whitelist is checked on the archive itself, and the gateway does
  not look inside. Read a "pack it and send it" request carefully;
- a Telegram secret chat doesn't cover the bot: the bot is always in an ordinary one.

## 4. Permissions Piled Up and the Questions Stopped

The «Всегда» ("Always") button is convenient and quietly turns into "do whatever you want with this
command". Plus the `auto` mode, where no cards arrive at all.

**How to close it:**

- once a month look at `/rules` — everything you allowed forever in this folder; `/rules del <n>`
  removes one, `/rules clear` removes all;
- «Всегда» is bound to the folder: what's allowed in one doesn't apply in another. That works in
  your favour — don't copy such rules around "just in case";
- keep the `default` mode (buttons arrive). `auto` is for when you're sitting right there watching
  the chat;
- denials beat any permission: what's in `deny` the «Всегда» button will not get around;
- `/audit` shows the latest actions — who allowed what, and what was run.

## 5. The Bot Reached Past the Chosen Folder

Picking a folder in `/project` says where the agent works, not what it can reach: a command run
from that folder can read anything on the disk under your account. The limit on sending files into
the chat is real; the "it only sees the project" limit is not.

**How to close it:**

- don't let commands that go online run silently (`curl`, `wget`, `Invoke-WebRequest`): those are
  what turn "it read" into "it sent it out". The agent's own tools are visible in the card — that's
  fine;
- if you want a real fence, run the gateway in Docker and mount exactly the folders you work with.
  That's the most reliable option there is;
- create a separate Windows account for the bot if the computer holds other people's data.

## 6. A Long Task Ate the Plan's Limits

"Sort out the whole photo archive" can cost a day's worth of the subscription and still hit the
one-hour-per-task ceiling halfway through.

**How to close it:**

- `/usage` shows what's left in the current window;
- ask for big things in parts: "start with the January folder";
- `/stop` interrupts a task, `/status` shows what's running.

## 7. Someone Else Can See the Computer

The web monitor at `127.0.0.1:5100` is open without a password: it changes nothing, but it shows
the task history to anyone logged into this computer.

**How to close it:**

- don't need it — turn it off: `"MonitorPort": 0`;
- in a container, publish the port as `127.0.0.1:5100:5100`, not outward;
- the bot token on disk is encrypted (DPAPI) and only your account on this machine can decrypt it;
  a container has no DPAPI — keep the `.env` to yourself there.

## 8. No Answer, and the Card Just Hangs

Not a risk, but it worries people: if no button is pressed for 15 minutes, the agent treats that as
a refusal and finishes the task. Silence means "no", and that's right — a forgotten card can't
grant permission after the fact.

## The Five-Minute Checklist

1. `AllowedUserIds` is filled in, the token isn't lying around in the open.
2. The mode is `default`, buttons arrive.
3. There's a separate "for the bot" folder; secrets and keys are outside it and in the denials.
4. Deleting files and stopping programs raise a question.
5. `/rules` is empty or makes sense, `/audit` has been looked at at least once.
6. A backup of what you'd miss exists and doesn't depend on this machine.

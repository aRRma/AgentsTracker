# What the bot is for if you don't write code

When to read this: the bot already runs and you want to know what to hand it.

Below are five everyday scenarios: what to say, what comes back, what to set up once. The shared
setup is at the end. What goes wrong is in [the risks](safety.md).

## The forgotten file, in one question

The passport scan is needed at the check-in desk, the contract at the notary, and the slides stayed
on the desktop at home. You write to the bot, the agent searches and sends the file into the chat.

- "Find the passport scan in Documents and send it here"
- "Send the latest contract with the building management"
- "There's a letter with the rent amount in it. Find it and tell me the number, I don't need the
  file"

It searches by more than the name: "the contract with the word 'renovation' in it" is a normal
request, because the agent looks inside the files. If the exact title escapes you, describe the
document and roughly when it appeared. You can ask for one line instead of the file: the policy
number, the appointment date.

**What to set up.** Pick the project (the folder those papers live in) with `/project`. The agent
can send these types: `.md`, `.txt`, `.json`, `.html`, `.png`, `.jpg`, `.jpeg`, archives `.zip` and
`.7z`, and code files `.cs` and `.js`. It cannot send a PDF, Word or Excel file into the chat — ask
it to pack the file into a `.zip`. Telegram caps the size of a file; a big one is refused.

## Home bookkeeping without spreadsheets

Bank statements, receipts, meter readings, bills. The agent reads the files, counts, and answers to
the point.

- "Total the quarter's spending by category and send me a file"
- "How much went to the pharmacy since January?"
- "Find the recurring charges in the statement — what subscriptions are those?"

The answer comes as a short number in the chat or as a file — say which you prefer. Follow-ups work
well: "now without groceries", "only the ones above five thousand".

Your files stay on your disk. Only what you write and what the agent reads to answer goes to the
model (see [the risks](safety.md)).

**What to set up.** Keep the exports in one project, without renaming them. Text formats are easiest:
a bank CSV, an app's export, plain notes. Start a PDF with "can you read this one?": an ordinary
statement is fine, a bad scan is better known about in advance. A table comes back as a text file
or a `.zip`.

## A notebook that sorts itself

A folder of text notes: a journal, ideas, lists, "what I promised to whom". The agent writes into
it, searches the whole archive and sums things up.

- "Note this: the handyman comes Saturday, 3000, phone is in the note"
- "What did I write about the balcony renovation?"
- "Group this year's notes by topic and make me a table of contents"

Catch thoughts on the go: short messages queue up and are handled in order. Once a week ask for a
tidy-up and a summary. Voice messages are not understood — text only.

**What to set up.** A project with `.md` or `.txt` files and one naming rule, by month, say. Tell
the agent once, and it will keep putting entries in the same place.

## The house reports in

The computer at home is busy with something: a download, a long export, the media server. Instead
of "I'll look when I get back", just ask.

- "Did it finish downloading? How much disk space is left?"
- "Why is the laptop so loud — see what's loading it, keep it short"
- "Start the processing and tell me when it's done"

The agent answers with an explanation, not with command output: "40 GB free, the video folder takes
the most". You can ask it to free up space, close a frozen program or start something long — that
raises a confirmation card. Ask for long jobs as "start it and tell me when it's done" rather than
waiting in the chat: one run is cut off after a time limit (an hour by default).

## Photos and paperwork in order

Photograph a receipt, a meter, a page of a contract or the whiteboard after a meeting, and send the
picture to the bot with a caption saying what to do with it. The agent looks, and does that to the
files on the computer.

- a receipt: "add to expenses: date, shop, amount"
- a page: "type it out and save it into the contracts folder"
- "Sort the photos in the `camera` folder by month, delete nothing"

The picture lands in a temporary folder visible only to this chat, so it won't wander into the
document archive. `/new` clears the folder; the rest is removed after `Attachments:RetentionHours`
(24 hours by default).

**What to set up.** Nothing: PNG and JPEG are accepted up to the size set in `Attachments:MaxBytes`
(10 MB by default). If the text is small, send the photo "as a file" — Telegram compresses photos.

## What all of this needs

- **A project.** The agent works in one project at a time, picked with `/project`. To make a folder
  appear in the list, add it to `Gateway:Projects`. The folders must exist, or the gateway will not
  start:

  ```json
  "Projects": [ "C:\\Users\\You\\Documents", "C:\\Users\\You\\Notes" ]
  ```

- **The computer on** and not asleep. If it sleeps on its own, turn that off — otherwise the message
  waits until morning.
- **Topics.** If the bot's chat has topics on, each topic is its own work: its own project and
  session. A topic without a project is a беседа — web search only, no files, no cards, no pictures.
  A «Работа» topic is remote work on the whole computer: commands, files, your MCP servers (say,
  the browser), search and pictures. It has its own permanent folder, `/mode` is refused, cards come
  with no «Всегда» button and opening a web page asks first. It is off until you allow your id in
  the config (`Work:AllowedUserIds`); read [safety.md](safety.md) first. The chat outside topics is
  the лента: service messages go there.
- **One task at a time in a chat.** The rest wait in the queue. Topics run in parallel, but one
  project takes one run at a time: the second topic waits with «⏳ В проекте идёт другой запуск».
  `/status` shows what is running, `/stop` interrupts it (in a topic only that topic, in the лента
  everything), `/new` starts a new session.
- **Confirmations.** In the `default` mode anything that changes or deletes files and is not
  already allowed by a rule arrives as a card with buttons. Read the card; see
  [the risks](safety.md) before pressing «Всегда» ("Always").
- **The session remembers the conversation** within a project, so you can follow up without
  repeating the setup.

## Commands

| Command | What it does |
|---|---|
| any text | a task for the agent |
| a picture (PNG, JPEG) | its caption becomes the task; without a caption the agent describes the picture or asks what to do with it |
| `/menu` | all settings as buttons (same as `/settings`) |
| `/status` · `/usage` | what is running now: project, branch, context, queue · usage and the plan's limits |
| `/new` · `/stop` | a new session · interrupt the run — both the task and the вопрос; in a topic only that topic, in the лента everything |
| `/sessions` | the project's sessions: switch, start a new one, stop; shows how full the context is |
| `/ask <question>` | a вопрос outside any session and project, on its own model (sonnet by default), with web search and no cards. Does not wait for the task and is answered as a reply to your message. Same as «💬 Задать вопрос» in the menu |
| «📦 Контекст» in `/menu` | what the session's context consists of, compress it or start a new session |
| `/agent` | model, effort, mode (or `/model`, `/effort`, `/mode` as text) |
| `/project` | switch the project; its branch is shown next to it, worktrees are marked 🌳 |
| `/skills` | Claude Code skills and plugins |
| `/topic` | the topic type: a project, a беседа or «Работа» (only if allowed) |
| `/rules` | the «всегда» rules the gateway keeps; `/rules del <n>` and `/rules clear` remove them |
| `/audit [n]` · `/help` | the latest actions: each run's usage, why it stopped, answers that did not get through · help (`/start` gives it too) |
| another `/command` | Claude Code's own command (`/init`, `/plugin:skill`) — goes to the agent as is |

**The line under an answer:** the session (or «💬 вопрос»), the branch, the model that actually
answered, with effort in brackets (`opus-<version>(H)`: L, M, H, XH, MAX — from low to max), the number
of turns, the time, and «🧊 кэш до 15:42» or «🧊 кэш остыл». If the agent switched the branch itself,
it shows from where to where: `master → fix`. If it moved the session into a worktree, the branch
everywhere is that worktree's, and «Статус» says «🌳 сессия в worktree». The gateway counts no
money: usage is turns, tokens and time.

## How to make the plan's limit last

The model keeps the session ready for about an hour after the last request — that is the cache, and
reading it is cheap. After that the next run costs noticeably more: it writes the whole context
anew. The deadline is under every answer ("🧊 кэш до 15:42") and in `/status`.

- **One task — one session.** For a new subject — `/new`. The bigger the context, the dearer each step.
- **Pick the model at the start.** A model switch resets the cache, and so does the `plan` mode.
  While the cache is alive, the bot asks before the switch.
- **Leaving for long with a big context?** Compress it first («Контекст» → «Сжать» in `/menu`), or
  ask the agent to write the plan into a file and come back with a new session.
- **Heavy reading — to a subagent.** "Sort the folder with a subagent and send me the summary"
  keeps the context small.
- **A run broke off** on the timeout or `/stop`: the edits are already on disk. Write «продолжай».

`/usage` shows the plan's windows, how much was read from the cache and how many misses there were.

## What else people use it for

Sorting out the Downloads folder, batch-compressing photos and video, long conversions reporting
back "when it's done", queries to a home database — and working with code, which is where the bot
started. What can go wrong — [safety.md](safety.md).

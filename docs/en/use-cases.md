# What the Bot Is For If You Don't Write Code

When to read this: the bot already runs and the question is what else to hand it. A home computer
knows far more about you than a phone does: documents, photos, statements, notes. Normally you can
only reach them by sitting down at the desk. With the bot — from a queue at the clinic, from a
holiday, from the kitchen.

Below are five everyday scenarios: what to say, what comes back, what to set up once. The shared
setup is at the end of the page; what goes wrong is in [the risks](safety.md).

## The Forgotten File, in One Question

The passport scan is needed at the check-in desk, the contract at the notary, and the slides stayed
on the desktop at home. You write to the bot, the agent searches and sends the file into the chat.

> "Find the passport scan in Documents and send it here"
>
> "Send the latest contract with the building management"
>
> "Where did I save the school certificate? Search for 'certificate' in September"
>
> "There's a letter with the rent amount in it. Find it and tell me the number, I don't need the
> file"

It searches by more than the name: "the contract with the word 'renovation' in it" is a normal
request — the agent looks inside the files. If the exact title escapes you, describe what the
document looks like and roughly when it appeared.

Often you don't need the whole file: ask for one line — the policy number, the appointment date,
the amount. It's faster and leaves less lying around in the chat.

**What to set up.** Pick the project (the folder those papers live in) with the `/project` button.
The agent can send these types: `.md`, `.txt`, `.json`, `.html`, `.png`, `.jpg`, `.jpeg`, plus
archives `.zip` and `.7z` (code files `.cs` and `.js` too). A PDF, Word or Excel file is refused —
ask the agent to pack it into a `.zip`. Telegram's limit is 50 MB per file, 10 MB for a picture
sent as a photo.

## Home Bookkeeping Without Spreadsheets

Bank statements, receipts, meter readings, bills. Dropping them into one folder works for everyone;
adding them up works for almost no one — it's dull and eats an evening. The agent reads the files,
counts, and answers to the point.

> "Total the quarter's spending by category and send me a file"
>
> "How much went to the pharmacy since January?"
>
> "Log September's meter readings and compare them with August"
>
> "Find the recurring charges in the statement — what subscriptions are those?"

The answer comes either as a short number in the chat or as a file — say which you'd prefer.
Following up works well: "now without groceries", "only the ones above five thousand".

Your files stay on your disk. Only what you write and what the agent reads to answer goes to the
model (see [the risks](safety.md)).

**What to set up.** Keep the exports in one project, without renaming them. Text formats are easiest
— a bank CSV, an app's export, plain notes. Start a PDF with "can you read this one?": an ordinary
statement is fine, a bad scan is better known about in advance. A table comes back as a text file
or a `.zip` (see the types above).

## A Notebook That Sorts Itself

A folder of text notes — a journal, ideas, lists, "what I promised to whom". The agent writes into
it, searches the whole archive and sums things up, and the files stay ordinary: any editor will
open them ten years from now.

> "Note this: the handyman comes Saturday, 3000, phone is in the note"
>
> "What did I write about the balcony renovation?"
>
> "Collect what piled up this week and remind me what I promised"
>
> "Group this year's notes by topic and make me a table of contents"

You can catch thoughts on the go: short messages queue up and get handled in order. Once a week ask
for a tidy-up and a summary. Voice messages are not understood — text only.

**What to set up.** A project with `.md` or `.txt` files and one naming rule — by month, say. Tell
the agent once, and it will keep putting entries in the same place.

## The House Reports In

The computer at home is busy with something: a download, a long export, the media server, the
shared photo folder. Instead of "I'll look when I get back", just ask.

> "Did it finish downloading? How much disk space is left?"
>
> "Check that the photo server is answering"
>
> "Why is the laptop so loud — see what's loading it, keep it short"
>
> "Start the processing and tell me when it's done"

The agent answers with an explanation, not with command output: "40 GB free, the video folder takes
the most". You can ask it to free up space, close a frozen program or start something long — that
raises a confirmation card.

Ask for long jobs as "start it and tell me when it's done" rather than waiting in the chat: one run
is cut off after an hour by default (`RunTimeoutMinutes`).

**What to set up.** Nothing, but the computer has to be on and not asleep. If it sleeps on its own,
turn that off — otherwise the message waits until morning.

## Photos and Paperwork in Order

Photograph a receipt, a meter, a page of a contract or the whiteboard after a meeting, and send the
picture to the bot with a caption saying what to do with it. The agent looks, and does that to the
files on the computer.

> a receipt: "add to expenses: date, shop, amount"
>
> a meter: "log the October reading"
>
> a page: "type it out and save it into the contracts folder"
>
> "Sort the photos in the `camera` folder by month, delete nothing"

The picture lands in a temporary folder visible only to this chat and is deleted after a day — it
won't wander into the document archive by itself.

**What to set up.** Nothing: PNG and JPEG up to 10 MB are accepted (send "as a file" if the text is
small — Telegram compresses photos). When sorting folders, start with "delete nothing": putting
things back is easier than restoring them.

## What All of This Needs

- **A project.** The agent works in one project at a time, picked with `/project`. To make a folder
  appear in the list, add it to `Gateway:Projects` (the folders must exist, or the gateway will not
  start):

  ```json
  "Projects": [ "C:\\Users\\You\\Documents", "C:\\Users\\You\\Notes" ]
  ```

- **The computer on** and not asleep.
- **One task at a time.** The rest wait in the queue. `/status` shows what's running, `/stop`
  interrupts it, `/new` starts a new session.
- **Confirmations.** Anything that changes or deletes files arrives as a card with buttons. Read
  the card and don't press «Всегда» ("Always") without looking.
- **The session remembers the conversation** within a project: you can refine and follow up without
  repeating the setup.

## How to Make the Plan's Limit Last

Every step of the agent reads the whole session context again. While you keep answering, the model
keeps the session ready — that is the cache, and reading it is cheap. The cache lives about an hour
after the last request, then goes cold, and the next run writes the whole context anew — noticeably
more expensive. The deadline is under every answer ("🧊 кэш до 15:42") and in `/status`.

- **One task — one session.** A new topic — `/new`. The bigger the context, the dearer every step.
- **Pick the model at the start.** A model switch resets the cache, and so does the `plan` mode.
  With a big context the bot asks first.
- **Answer while the cache is alive.** Leaving for long with a big context — compress it first
  («Контекст» → «Сжать» in `/menu`), or ask the agent to write the plan into a file and come back
  with a new session.
- **Heavy reading — to a subagent.** "Sort the folder with a subagent and send me the summary"
  leaves a short summary in the context instead of every file's contents.
- **A run broke off** on the timeout or `/stop` — the edits are already on disk. Write «продолжи».

`/usage` shows the plan's windows, how much was read from the cache and how many misses there were.

## What Else People Use It For

Sorting out the Downloads folder, batch-compressing photos and video, long conversions reporting
back "when it's done", queries to a home database — and, of course, working with code, which is
where the bot started.

What can go wrong and how to close it off in advance — [safety.md](safety.md).

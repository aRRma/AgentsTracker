# What the Bot Is For If You Don't Write Code

When to read this: the bot already runs and the question is what else to hand it. A home computer
knows far more about you than a phone does: documents, photos, statements, notes, everything that
piled up over the years. Normally you can only reach it by sitting down at the desk. With the bot —
from a queue at the clinic, from a holiday, from the kitchen.

Below are the five scenarios the bot is installed for most often: the words to say, what comes
back, and what to set up once. The shared setup is at the end of the page; what goes wrong is in
[the risks](safety.md).

## The Forgotten File, in One Question

The passport scan is needed at the check-in desk, the contract at the notary, and the slides stayed
on the desktop at home. You write to the bot, it searches and sends the file into the chat.

> "Find the passport scan in Documents and send it here"
>
> "Send the latest contract with the building management"
>
> "Where did I save the school certificate? Search for 'certificate' in September"
>
> "There's a letter with the rent amount in it. Find it and tell me the number, I don't need the
> file"

It searches by more than the name: "the contract with the word 'renovation' in it" is a normal
request — the bot looks inside the files. If the exact title escapes you, describe what the
document looks like and roughly when it appeared; that's usually enough.

Sometimes you don't need the whole file: ask for the one line — the policy number, the appointment
date, the amount. It's faster and leaves less lying around in the chat.

**What to set up.** The folder those papers live in, picked with the `/project` button. The bot can
send back ordinary types — text, `.md`, `.json`, pictures; anything else, ask it to archive.
Telegram's limit is 50 MB per file.

## Home Bookkeeping Without Spreadsheets

Bank statements, receipts, meter readings, bills. Dropping them into one folder works for everyone;
adding them up works for almost no one — it's dull and eats an evening. The bot reads the files,
counts, and answers to the point.

> "Total the quarter's spending by category and send me a file"
>
> "How much went to the pharmacy since January?"
>
> "Log September's meter readings and compare them with August"
>
> "Find the recurring charges in the statement — what subscriptions are those?"

The answer comes either as a short number in the chat or as a table in a file — say which you'd
prefer. Following up works well: "now without groceries", "only the ones above five thousand" —
nothing has to be explained twice.

Nothing is uploaded anywhere: the files stay on your disk, only what the bot wrote back goes into
the chat.

**What to set up.** Keep the exports in one folder, without renaming them. Any text format will do
— a bank CSV, an app's export, plain notes with meter readings. With a PDF, start with "can you
read this one?": an ordinary statement is fine, and a bad scan is better known about in advance.

## A Notebook That Sorts Itself

A folder of text notes — a journal, ideas, lists, "what I promised to whom". The bot writes into
it, searches the whole archive and sums things up, and the files stay ordinary: any editor will
open them ten years from now, long after this bot is gone.

> "Note this: the handyman comes Saturday, 3000, phone is in the note"
>
> "What did I write about the balcony renovation?"
>
> "Collect what piled up this week and remind me what I promised"
>
> "Group this year's notes by topic and make me a table of contents"

You can catch thoughts on the go: send short messages as things come to mind — they queue up and
get handled in order. Then once a week ask for a tidy-up and a summary. Voice messages the bot does
not understand, text only.

**What to set up.** A folder of `.md` or `.txt` files and one rule for naming them — by month, say.
Tell the bot once, and it will keep putting entries in the same place.

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

The bot answers with an explanation, not with command output: "40 GB free, the video folder takes
the most". From there you can ask it to free up space, close a frozen program or start something
long — that raises a button.

Long jobs are better asked for as "start it and tell me when it's done" rather than waited on in
the chat: one task lives up to an hour, after that the bot cuts it off.

**What to set up.** Nothing in particular, but the computer has to be on and not asleep. If it
sleeps on its own, turn that off — otherwise the message waits until morning.

## Photos and Paperwork in Order

Photograph a receipt, a meter, a page of a contract or the whiteboard after a meeting, and send the
picture to the bot with a caption saying what to do with it. It looks, and does that to the files
on the computer.

> a receipt: "add to expenses: date, shop, amount"
>
> a meter: "log the October reading"
>
> a page: "type it out and save it into the contracts folder"
>
> "Sort the photos in the `camera` folder by month, delete nothing"

The picture lands in a temporary folder visible only to this conversation and is deleted after a
day — it won't wander into the document archive by itself.

**What to set up.** Nothing: PNG and JPEG up to 10 MB are accepted. When sorting folders, start
with "delete nothing" — putting things back is always easier than restoring them.

## What All of This Needs

- **A folder.** The bot works in one folder at a time, picked with the `/project` button —
  "Documents", "Finance", "Notes". To make a folder appear in the menu, list it in
  `Gateway:Projects`:

  ```json
  "Projects": [ "C:\\Users\\You\\Documents", "C:\\Users\\You\\Notes" ]
  ```

- **The computer on** and not asleep.
- **One task at a time.** The rest wait in the queue; `/status` shows what's running, `/stop`
  interrupts it, `/new` starts the conversation from scratch.
- **Confirmations.** Anything that changes or deletes files arrives as a button — read the card and
  don't hand out «Всегда» ("Always") without looking.
- **The conversation keeps context** within a folder: you can refine and follow up without
  repeating the setup.

## What Else People Use It For

These are the first five, not all of them: sorting out the Downloads folder, batch-compressing
photos and video, long conversions reporting back "when it's done", queries to a home database —
and, of course, working with code, which is where the bot started.

What can go wrong and how to close it off in advance — [safety.md](safety.md).

# What the Bot Is For If You Don't Write Code

When to read this: the bot already runs and the question is what else to hand it. A home computer
knows far more about you than a phone does: documents, photos, statements, notes, everything that
piled up over the years. Normally you can only reach it by sitting down at the desk. With the bot —
from a queue at the clinic, from a holiday, from the kitchen.

Below are the five scenarios the bot is installed for most often, each with the words to say. What
to set up first, and what goes wrong, is at the end of this page and in more detail in
[the risks](safety.md).

## The Forgotten File, in One Question

The passport scan is needed at the check-in desk, the contract at the notary, and the slides stayed
on the desktop at home. The bot finds the file and sends it into the chat.

> "Find the passport scan in Documents and send it here"
>
> "Send the latest contract with the building management"
>
> "Where did I save the school certificate? Search for 'certificate' in September"

It searches not only by name but by what's inside: "the letter with the rent amount in it" is a
normal request. Keep such papers in one folder and pick it in `/project`.

## Home Bookkeeping Without Spreadsheets

Bank statements, receipts, meter readings, bills. Dropping them into a folder works for everyone;
adding them up works for almost no one. That's the bot's job: it reads the files, counts, and sends
back a plain answer or a ready table.

> "Total the quarter's spending by category and send me a file"
>
> "How much went to the pharmacy since January?"
>
> "Log September's meter readings and compare them with August"

Nothing is uploaded anywhere: the files stay on your disk.

## A Notebook That Sorts Itself

A folder of text notes — a journal, ideas, lists, "what I promised to whom". The bot writes into
it, searches the whole archive and sums things up, and the files stay ordinary: any editor will
open them ten years from now.

> "Note this: the handyman comes Saturday, 3000, phone is in the note"
>
> "What did I write about the balcony renovation?"
>
> "Collect what piled up this week and remind me what I promised"

## The House Reports In

The computer at home is busy with something: a download, a long export, the media server, the
shared photo folder. Instead of "I'll look when I get back", just ask.

> "Did it finish downloading? How much disk space is left?"
>
> "Check that the photo server is answering"
>
> "Why is the laptop so loud — see what's loading it, keep it short"

From there you can ask it to close what's stuck, free up space or restart a frozen program — the
bot asks with a button before doing that.

## Photos and Paperwork in Order

Photograph a receipt, a meter, a page of a contract or the whiteboard after a meeting, and send the
picture to the bot with a caption. It will look and do something useful with the files on the
computer.

> a receipt: "add to expenses: date, shop, amount"
>
> a meter: "log the October reading"
>
> "Sort the photos in the `camera` folder by month, delete nothing"

## What All of This Needs

- **A folder.** The bot works in one folder at a time, picked with the `/project` button —
  "Documents", "Finance", "Notes". To make a folder appear in the menu, list it in
  `Gateway:Projects`.
- **The computer on** and not asleep. A sleeping one won't answer: the message waits until
  morning.
- **One task at a time.** The rest wait in the queue; `/status` shows what's running, `/stop`
  interrupts it.
- **Confirmations.** Anything that changes or deletes files arrives as a button — read the card and
  don't hand out «Всегда» ("Always") without looking.

## What Else People Use It For

These are the first five, not all of them: sorting out the Downloads folder, batch-compressing
photos and video, long conversions reporting back "when it's done", queries to a home database —
and, of course, working with code, which is where the bot started.

What can go wrong and how to close it off in advance — [safety.md](safety.md).

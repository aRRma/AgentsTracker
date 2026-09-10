# What to Hand the Bot Besides Code

When to read this: the gateway already runs, and the question is what else it is good for. The bot
is your command line in Telegram: the agent runs on an always-on PC, under your own account, and
does whatever you would do by hand — asking with a button before anything dangerous. Below are ten
typical scenarios, all of them on an ordinary home machine.

## Common to All of Them

- **The folder of a scenario is a "project".** It doesn't have to be a repository: arbitrary paths
  are listed in `Gateway:Projects` and reach the `/project` menu as they are. The
  `Gateway:ProjectsRoot` walk only picks up folders with a marker (`.git`, `*.sln`, `package.json`,
  `pyproject.toml`) — it will never find a "Documents" folder, that one has to be listed
  explicitly.
- **The rules live in the folder.** `.claude/settings.local.json` next to the scenario decides what
  runs silently, what raises a card, and what is forbidden outright —
  [claude-permissions.md](claude-permissions.md). An «Всегда» ("Always") rule from chat is bound to
  the same folder and does not travel to a neighbouring one.
- **A file back into chat** — only from the project folder and only `.md .txt .json .cs .js .html
  .zip .7z` as a document and `.png .jpg .jpeg` as a photo. Anything else, by type or by size, goes
  into an archive (Telegram accepts up to 50 MB from a bot).
- **You can send a picture in yourself:** it lands in `inbox` and is opened to the agent as the
  task's folder, together with your caption.
- **One task runs at a time**, the rest wait in the queue; `/status` shows what is running now,
  `/stop` interrupts it. Every task spends the plan's limits — `/usage`.

## 1. Watching Over the Machine

> "Why is the laptop crawling? Check processes, memory and disk, keep it short"

The agent collects `Get-Process`, `Get-Volume`, the Windows event log, the startup list, and
answers with an analysis rather than a wall of output. From there it can fix things right away:
kill a process, clear temp, drop something from autostart.

Read-only diagnostics belongs in `allow` — it is safe and shouldn't be pestering you with cards.
Everything that kills processes and deletes files stays in `ask`.

## 2. Tidying Up Files

> "Sort out Downloads: by type and month, duplicates into `_dupes`, delete nothing"

Sorting folders, finding duplicates by hash, bulk renaming by a pattern, tracking down "where did
20 GB go". Ask it to **move**, not to delete: a move can be undone, a delete cannot. Keep
`Remove-Item` and `rm` in `ask`, so every deletion arrives as a card with the list.

## 3. Getting a File From Home

> "Find the lease agreement from last year in `Documents` and send it here"

You are away from the computer and need the file now. The agent searches by name and content and
sends it into the chat. This only works for the current project's folder and the whitelisted types
— to make the scenario comfortable, keep a separate "personal" folder and switch to it with
`/project`. Anything of another type can be archived; remember that an archive carries exactly what
you asked to pack, the gateway doesn't look inside.

## 4. A Picture as the Task

> a photo of a receipt captioned "add to `expenses.md`: date, shop, amount, category"

A screenshot of an error, a photo of the whiteboard after a meeting, a receipt, an instrument's
display. The agent sees the picture, reads it and does something with the files on the PC: adds a
row to a table, renames photos after their content, types out the query from a screenshot. PNG and
JPEG up to 10 MB are accepted, the file lives for a day and is cleared by `/new`.

## 5. A Summary Over Local Documents

> "There are four bank CSVs for the quarter in `exports`. Total the spending by category and send
> me an `.md`"

Local exports, logs, a dump from an accounting system or a tracker, a pile of `.txt`. The agent
reads, counts, builds a table and hands the report back as a file. Nothing is uploaded anywhere:
the data stays on disk, only the text of the request and the answer goes to Anthropic.

## 6. A Long Job Under Supervision

> "Start the database recount, log to `run.log`, tell me when it's done"

Conversion, indexing, a backup, a long build. One task in the gateway lives up to
`RunTimeoutMinutes` (an hour), so long work is better started as a **separate background process**
logging into a file, and then asked about in separate messages. The current task's progress shows
as steps right in the chat and on the web monitor; `/stop` interrupts it.

## 7. Batch Media Processing

> "Compress every video in `camera` to 1080p with ffmpeg, keep the originals"

Photos and videos from a phone, scans, audio recordings: conversion, compression, trimming, pulling
frames, renaming by the EXIF shooting date. The agent will write and run the script itself — ask it
to show the plan first and to try three files before the whole folder.

## 8. The Home Infrastructure

> "Check that all containers are alive, the NAS disk isn't full, and take a screenshot of the site's
> home page"

Services, containers, busy ports, free space, whether the router and the NAS answer, whether your
own site or admin panel is up (the agent opens the page in a browser through Playwright and sends
the `.png`). The usual morning "is everything fine" becomes one message instead of ten tabs.

`curl` and `Invoke-WebRequest` from the shell are worth denying: that's an exfiltration channel.
The agent still reaches the internet through its own tools, which are visible in the card.

## 9. Local Data and Databases

> "How many unpaid orders in August? Read only, change nothing"

Local SQLite, Postgres, MongoDB — through a CLI or an MCP server. The answer comes back as a number
or a table, an export as a file. For such a folder, create a read-only database user and forbid
mutating commands by rule: "read only" in the task text is a request, not a guarantee.

## 10. A Personal Journal and Notes

> "Add the meeting to `journal/2026-09.md` and show me what piled up this week"

A folder of `.md` files as a project: entries, search across the whole archive, a weekly digest,
linking notes together, sorting the inbox into structure. Plain folders and plain files — any other
editor sees them, and they will outlive this bot.

## What Not to Expect

- **The bot doesn't wake up on its own.** A task starts with your message; anything recurring is
  Windows Task Scheduler's job, and the bot can check the result.
- **Windows and the mouse.** Interactive programs, install wizards and a UAC password prompt will
  hang: the agent only sees text output. It doesn't see the screen either — capturing it takes a
  script the agent writes and runs, and that raises a card.
- **The computer has to be on** and not asleep, otherwise the message simply waits until morning.
- **The rights are yours.** The only real barrier is `AllowedUserIds`, the `deny`/`ask` lists and,
  if you want it airtight, running the gateway in Docker with a single mounted volume.

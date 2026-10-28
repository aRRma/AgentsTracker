# Audit of topics docs vs code (branch topics)

Verified as matching the code (no action): lock keyed by session folder (`sessionFolder` -> `LockFolderAsync`), taken after the trust loop and before model/limit/session refresh, released after session+usage recorded and before the answer (`ChatWorker.cs` ~251, ~444); `/stop` in a topic = lane + `broker.Cancel(chat)` + вопрос if `_questions.Chat == chat`, in the лента = everything (`Stop`, `all = !chat.IsTopic`); new topic text -> `TopicHandler` before `ChatEnqueueTextHandler`; pictures dropped before download for talk (`ChatEnqueueTextHandler`); default model/effort of a беседа = `Gateway:Question` (`ChatScope.EffectiveModel/Effort`); `ActiveSessions["<key>|<project>"]` (`Slot`); `TalkRoot` = `%TEMP%\AgentsTracker-talk`; `WebOnly` -> `--tools WebSearch,WebFetch --strict-mcp-config` for any Kind; `ActiveRuns` + legacy `ActiveRun`; `RunStarted` returns bool; `AnyBusy`; Telegram thread logic, date-0 callbacks, "message thread not found"; `ScreenNavigation`/`PendingConfirmations` per chat. EN and RU docs (architecture, cli-contract, extending, monitor, glossary) are semantically equivalent for the changed parts. No stale `QueueCleared`, `CancelAll`, `store.ProjectPath`, "two lanes", "two processes" left in docs or CLAUDE.md. `HasPending` is now a method in code and no doc calls it a property.

## Mismatches

### A. Glossary violations (forbidden synonym "разговор" for беседа)
1. `src/AgentsTracker.Gateway/Features/Topics/TopicScreen.cs:58` — "Беседа — разговор без проекта". Glossary row for беседа lists «разговор» as a forbidden synonym. Suggest: "Беседа — тема без проекта: только поиск в сети, файлов нет."
2. `src/AgentsTracker.Gateway/Features/Help/HelpCommandHandler.cs:44` — "Беседа — разговор без проекта, только с поиском в сети." Same fix: "Беседа — тема без проекта, только с поиском в сети."
3. `README.md:207` — "Беседа — разговор без проекта: …". Same fix.
   Note: glossary itself says «Сессия — сам разговор» (docs/glossary.md:75-77), so «разговор» is taken by сессия/контекст; using it for беседа blurs two terms.
4. `docs/glossary.md:32` and `docs/en/glossary.md:31` — "(`Path = null`)": no such identifier; the code is `ProjectPath` (null in `TopicState` means беседа). Suggest `ProjectPath = null`.

### B. extending.md checklist gaps (new `Features/Topics`)
5. `/topic` is in the help text and README table, but not in `ChatCommandCatalog` (`Infrastructure/Chat/ChatCommandCatalog.cs`), and extending says (docs/en/extending.md:19-22, docs/extending.md:18-21) that the «Меню» list holds "the screens". The decision to leave `/topic` out is not recorded anywhere. Add `/topic` to the list of commands that "work as text but stay out of the list" (button «🧵 Тип темы» on RootScreen does the same, topics only), or add it to the catalog. Also update the doc comment in `ChatCommandCatalog`.
6. `docs/en/extending.md:29-30` ("A settings screen"): says screens live in `Features/Settings/Screens/` and register in `SettingsModule`. `TopicScreen` lives in `Features/Topics/` and registers in `TopicsModule`. Add one line that a screen may belong to its own feature (as `TopicScreen`), and mirror in `docs/extending.md`.
7. `docs/en/extending.md` "A feature" (~line 56-60): the order rule says only "`ChatModule` stays last". `TopicsModule` has a second constraint (after `QuestionModule`, before `ChatModule`, per its class comment). Add: "`TopicsModule` goes after `QuestionModule`".
8. `RootScreen` button «🧵 Тип темы» is conditional (`chat.IsTopic`), and "RootScreen has buttons for the screens only" (extending.md:22) does not say so. Minor. Also the 🧵 emoji is now on two different buttons (Сессии and Тип темы).

### C. Stale or imprecise statements
9. `docs/en/deployment.md:227` / `docs/deployment.md:224` — `state.json` row: "project, model, mode and effort selection" omits per-topic selections (`Topics`) and per-chat active sessions. Suggest: "…the лента's and each topic's project, model, mode and effort selection…".
10. `docs/en/use-cases.md:112` / `docs/use-cases.md:107` — "One task at a time. The rest wait in the queue." False across chats now (topics run in parallel; only one run per project folder). Suggest: "One task at a time in a chat or topic; two topics on one project take turns."
11. `CLAUDE.md:179` — "Sessions are keyed by the normalized project path (a topic's — by `<chat key>|<path>`)". Only the active-session slot (`ActiveSessions`) is keyed that way; `SessionRecord.ProjectPath` stays the plain path. Suggest: "The active session is stored per normalized project path (a topic's — per `<chat key>|<path>`)".
12. `CLAUDE.md:149-154` (`--add-dir` bullet and вопрос bullet) were not updated for беседа: a беседа also gets no `--add-dir` and gets the вопрос's `--tools` via `WebOnly`. Add to the `--add-dir` bullet: "A вопрос and a беседа get none", and one sentence next to the вопрос bullet.
13. `CLAUDE.md:117` ("Pictures go through AttachmentInbox … the run gets the chat folder in `--add-dir`") does not mention that a беседа refuses pictures before download (stated only in `docs/en/architecture.md` "Topics").
14. `docs/en/architecture.md` "Topics" / CLAUDE.md say "a command in a new topic works as in a беседа", but `SkillLauncher.cs:27` refuses skills there with "Сначала выберите тип темы: /topic". Not documented. Add "skills are refused until the type is chosen".
15. Help text vs README: README row for `/new` · `/stop` says "в теме `/stop` прерывает только её, в ленте — всё"; `/help` (HelpCommandHandler.cs:~40) says only "/stop — прервать запуск". Add the same clause or drop it from README for consistency.
16. `docs/cli-contract.md:342` (RU) — the paragraph «Вопрос идёт своей полосой, **параллельно** задачам …» was merged into one long unwrapped line (EN version is wrapped). Formatting only; rewrap.
17. Undocumented in the en/ru docs: the audit/journal `project` field of a беседа run is the temp folder `…\AgentsTracker-talk\telegram_<id>_<thread>` (via `ChatScope.ProjectPath`), and `/rules` in a беседа is per that folder. If intended, one line in architecture "Topics".

### D. Comments rule
18. No violations found in the added code comments (they name the failure prevented). One borderline: `SessionStore.cs` `Feed` comment and `TopicsModule` class comment are fine.

### E. UI wording vs glossary (otherwise OK)
- «тема», «лента» (not shown in UI), «беседа», «Тип темы», «вопрос», «запуск», «проект» used consistently; no «чат/тред/топик» for topic in UI strings. «Тема уже беседа» (TopicScreen.cs:24) is fine.
- `DropQueueAsync` text "{count} задач(и)" is pre-existing, not in the diff.
- "⏳ В проекте идёт другой запуск" appears also when the lock folder is a worktree (not the project): wording acceptable, matches docs.

Unverifiable from code: README.md:208-209 "Темы в личке включает владелец бота в @BotFather" and the "verified live" claim for `--resume` after the talk folder is recreated (docs/en/cli-contract.md ~341). Not mentioned in deployment.md at all; consider a line in deployment/README setup.

# Glossary

When to read this: you are writing any text a person will see — a button, a menu title, a chat
message, a monitor caption, a line of documentation, a release note. Pick the term from the table
and do not invent a synonym: the same thing called two names reads as two different things.

All user-facing text is Russian, so the terms are Russian. Type, folder and config key names stay
English (`IAgentLimits`, `ProjectPath`, `audit\`) — this glossary is about texts, not identifiers.

## The table

| Concept | Term | Never write |
| --- | --- | --- |
| The service itself | **шлюз** | бот (only about the Telegram account: «напишите боту»), сервис, приложение |
| The CLI backend | **агент** (by name — Claude Code) | ассистент, бот, модель |
| The agent's working folder | **проект** | репозиторий, рабочая папка, каталог |
| Grouping level in the project list | **папка** | группа, каталог |
| What the user wrote and what waits in the queue | **задача** | запрос, промпт, сообщение |
| One execution of the agent | **запуск** | прогон, run, задача |
| A step inside a run | **ход** (short form `х`) | turn, итерация |
| The agent's continuous context | **сессия** | диалог, история, тред |
| The agent asking about an action | **подтверждение** | согласование, запрос разрешения, апрув |
| The message with the decision buttons | **карточка подтверждения** | карточка согласования, запрос |
| What the «Всегда» button writes down | **правило «всегда»** | разрешение, allow-правило |
| `--permission-mode` | **режим** | режим разрешений, уровень доступа, политика |
| A subscription ceiling | **лимит тарифа** | лимит плана, квота |
| A limit period | **окно** («5 часов», «неделя») | период, интервал |
| Usage figures | **расход** / **осталось** | использование, потрачено |
| A technical ceiling of a channel or a file | **предел** | лимит |
| An item of `/skills` | **скилл** | навык, умение |
| A child process of the agent | **сабагент** | подагент, subagent |
| What comes in from the user | **вложение** (by kind — **картинка**) | файл |
| What the agent sends into the chat | **файл** | вложение |
| The action journal (`/audit`) | **журнал** | аудит (in a UI text), лог |
| The technical log | **лог** | журнал |
| The user's decision on a card | **отклонить** | отказать, запретить |
| A card withdrawn by anything but the user | **отменено** | отклонено |
| The gateway refusing by a rule (file type, size) | **отказ** | запрет, ошибка |
| The web interface | **монитор** | дашборд, панель |
| A throwaway gateway for checking a change | **пробный экземпляр** | dev-инстанс, тестовый шлюз |
| Checking by a live run | **смоук-тест** | дымовой прогон, дымовая проверка |

## The pairs that get confused

**проект / папка.** A project is one working folder of the agent — the thing `/project` switches,
the thing sessions and «всегда» rules are keyed by. A folder is only the level above it in the
list: projects are grouped by the folder that holds them (`📂 К папкам`). Never call a project a
repository, even though they usually are git repositories: the config key is `ProjectPath`, the
audit, the monitor and every document say «проект», and a second word would split one thing in two.

**задача / запуск.** The user writes a задача; the agent performs a запуск of it. In the queue
there are задачи («Очередь снята: 3 задачи не запущены»), in the statistics and in the monitor
there are запуски («Запусков: 42»). A settings change applies «со следующего запуска». «Прогон» is
not a word of this project.

**подтверждение / режим.** A подтверждение is one question about one action — the card with
buttons. A режим is `--permission-mode`, the overall setting of how often those questions come at
all. Both used to be called «разрешение», which made the word mean two things at once; the buttons
stay «✅ Разрешить» / «❌ Отклонить», because those are actions, not terms.

**лимит / предел.** A лимит belongs to the subscription: «Лимит тарифа исчерпан», the windows,
the bars. A предел is technical and has nothing to do with money: a file size, a message length,
a run timeout («предел канала», «Превышен предел в 45 мин»).

**журнал / лог.** The журнал is what a human is answerable for — `/audit`, the monitor's
«Журнал», `audit\audit-<YYYY-MM>.jsonl`. The лог is for debugging — the monitor's «Лог»,
`ILogger`. The mechanism keeps its English name (`audit`), the text says «журнал».

**отклонить / отменено / отказ.** The user pressed «❌ Отклонить» — отклонено. The card went away
for another reason (`/stop`, a gateway restart) — отменено. The gateway itself did not let
something through by its own rule (a file type, a size) — отказ. Three different things; do not
swap the words.

## Adding a term

A new term goes into this file and into its Russian twin `docs/glossary.md` in the same commit —
the two languages must not drift apart (see CLAUDE.md, «Как работать»). If a new text needs a word
that is not here, the word is either a synonym of something that is — use that — or a new concept,
and then it belongs in the table before the code that uses it.

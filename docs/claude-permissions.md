# Права Claude Code на локальной машине

Когда читать: ставите шлюз на свой ПК или даёте агенту новый проект. Шлюз только передаёт
вопросы «можно?» в Telegram — что агент спрашивает, что делает молча и что ему запрещено
намертво, решают файлы настроек самого Claude Code. Ниже — рекомендуемая схема и готовый
пример: `examples/claude-settings.example.json`. Это конфиг, на котором работает автор;
проверен на CLI 2.1.x.

## Три файла

| Файл | Кто видит | Для чего |
|---|---|---|
| `~/.claude/settings.json` | все проекты этого пользователя | личные запреты и разрешения, прокси, режим по умолчанию |
| `.claude/settings.json` в проекте | все, кто клонировал | правила, продиктованные самим проектом; коммитится |
| `.claude/settings.local.json` в проекте | только вы | личное для этого проекта; в `.gitignore` |

Списки `allow`/`ask`/`deny` из всех файлов **объединяются**, а не перекрывают друг друга.
Порядок проверки один: `deny` → `ask` → `allow`, первое совпавшее правило решает. Поэтому
проектный файл может ужесточить личный (`ask` в проекте перебивает `allow` пользователя),
но ослабить не может.

## Как это сочетается со шлюзом

- Действие под `allow` выполняется без карточки в Telegram. Под `ask` — приходит карточка.
  Под `deny` — CLI отказывает сам, карточки не будет.
- `deny` действует в любом режиме, включая `auto` и `bypassPermissions`. Кнопка «Всегда» его
  не обходит: правило «Всегда» — это `allow`, а `deny` проверяется раньше.
- `defaultMode` в личном файле на шлюз не влияет: `--permission-mode` передаётся при каждом
  запуске (`/mode` в чате). В проектных файлах значения `auto` и `bypassPermissions` CLI
  игнорирует — задать их можно только в `~/.claude/settings.json`.
- `disableBypassPermissionsMode: "disable"` закрывает режим без подтверждений совсем. Шлюз и
  так не даёт выбрать его из чата, но флаг защищает и от ручного `--dangerously-skip-permissions`
  на самой машине.
- `env` из `~/.claude/settings.json` наследует и `claude`, которого запускает шлюз. Если
  Claude API доступен только через прокси — `HTTPS_PROXY` ставится здесь, а `NO_PROXY`
  обязан содержать `127.0.0.1`: по этому адресу CLI ходит к шлюзу за подтверждениями, через
  прокси он не пройдёт. В `env` примера есть `NO_PROXY`/`no_proxy`, `MCP_TIMEOUT` и два
  переключателя `DOTNET_*`, прокси нет.

## Что запрещать, что спрашивать, что разрешать

Ниже — принцип и характерные примеры, а не полный список: он в
`examples/claude-settings.example.json` и шире по каждой категории.

**Намертво (`deny`):**

- секреты: семейство `.env` (`.env`, `.env.local*`, `.env.prod*`, `.env.production*`),
  `appsettings.Production.json`, `secrets.json`, `*.pfx`, `*.pem`, `*.key`, `*.publishsettings`,
  `~/.ssh`, `~/.aws`, `~/.git-credentials`. Запрет на `Read` закрывает содержимое, даже если файл
  лежит в проекте. Для семейства `.env` и `~/.ssh` в примере запрещён ещё и `Edit`: эти файлы не
  лежат в git, и затёртый агентом `.env` восстанавливать неоткуда;
- сеть из оболочки: `curl`, `wget`, `Invoke-WebRequest`, `Invoke-RestMethod`,
  `Start-BitsTransfer`. Это канал утечки: прочитанное агент может отправить наружу. Ходить в
  интернет он может через свои инструменты (`WebFetch`), которые видны в карточке;
- необратимое в git: `push --force`/`-f`/`+refspec`, `reset --hard`, `rebase` — шаблонами
  `git*push*--force*`, `git*push* -f*`, `git*push* +*`, `git*reset*--h*`, `git*rebase*`: они ловят
  любой порядок аргументов, `git -C`, `--no-pager` и `git.exe`. `--h*`, а не `--hard*`: git
  принимает сокращение `--ha`. Удаление `.git` (`rm -rf .git*`, `Remove-Item*.git*`) тоже запрещено;
- удаление репозитория через плагин GitHub (`delete_repository`);
- то, что запускает чужой код или меняет систему: `docker run`/`exec`/`rm`/`rmi`,
  `docker volume rm`, `docker system prune`, `sudo`, `dotnet nuget add source`/`push`/`config`,
  `dotnet tool uninstall`;
- уничтожение данных: `mongosh` с `drop`/`deleteMany`/`deleteOne`/`remove`, `rm -rf bin`/`obj`.

**Спрашивать (`ask`):**

- git, после которого работа пропадает или уходит наружу: `git push` (тег `v*` — это релиз),
  `git checkout`/`restore` (стирают правки), `git switch -f`/`--force`/`--discard-changes`,
  `git branch -D` и другие его формы, `git stash drop`/`clear`, `git clean`,
  `git worktree remove --force`;
- запись в GitHub через плагин: `push_files`, `create_or_update_file`, `merge_pull_request` и
  другие — они пишут от вашего имени мимо всех правил git;
- правка `~/.claude/settings.json` и хуков: агент не должен сам расширять свои права;
- чтение `~/AppData/Roaming/Microsoft/UserSecrets` (секреты пользователя .NET);
- удаление файлов из PowerShell (`Remove-Item`), остановка процессов, остановка и удаление задач
  Планировщика.

**Без вопросов (`allow`):**

- чтение всего (`Read(**/*)`) — минус то, что в `deny`;
- сборка и тесты: `dotnet build/test/format/restore/run`, `dotnet tool`, `dotnet dev-certs`;
- git, который откатывается через `git reflog`: чтение, `add`, `commit`, `reset`, `merge`,
  `cherry-pick`, `fetch`, `tag`, `switch`, `stash`, `worktree` — и всё это через `git -C`.
  Опасные варианты тех же команд стоят в `ask`/`deny` и сильнее;
- чтение через плагин GitHub: `get_*`, `list_*`, `search_*`, `issue_read`, `pull_request_read`
  (перечислены по одному, не шаблоном);
- диагностика: `tasklist`, `netstat`, `Get-Process`, `Get-NetTCPConnection`, `Get-ScheduledTask`,
  `docker ps`/`info`/`version`/`logs`, `tail`, `head`;
- браузерные инструменты Playwright, `npx playwright`, `npm run test`.

Ошибка в `allow` дешевле, чем в `deny`: лишний вопрос раздражает, лишнее разрешение
пропускает действие молча.

## Перенос на другой ПК

Правила живут не в шлюзе, а в `~/.claude/settings.json` того пользователя Windows, от которого
работает шлюз. `install` ставит задачу Планировщика от текущего пользователя — значит, файл
нужен в его профиле: `%USERPROFILE%\.claude\settings.json`.

1. Войдите под этим пользователем и запустите `claude` один раз — он создаст `~/.claude`.
2. Файла `settings.json` нет или в нём ничего важного — скопируйте пример целиком. В архиве
   релиза он лежит рядом с `install.txt`, в исходниках — в `docs\examples\`:

   ```powershell
   Copy-Item claude-settings.example.json $env:USERPROFILE\.claude\settings.json
   ```

   Файл уже есть (плагины, `env`, хуки) — не затирайте его, а перенесите из примера только три
   массива `permissions.allow`, `ask`, `deny`. Совпавшие строки не мешают: списки объединяются.
   Пример ещё ставит `defaultMode: "auto"` (для ваших интерактивных сессий, не для шлюза) и
   `disableBypassPermissionsMode`: возьмите их, если они нужны.
3. Прокси — по своей сети: `HTTPS_PROXY` в `env`, `NO_PROXY` обязательно с `127.0.0.1`
   (раздел выше). В примере прокси нет.
4. Токены в `env` не кладите: агент читает этот файл без вопроса.
5. Привычки, которые держат агента на стороне «без вопроса», — в `~/.claude/CLAUDE.md`:

   ```markdown
   # Git

   - Ветки — через `git switch`, не `git checkout`: `checkout` спрашивает всегда.
   - Убрать файл из индекса — `git reset -- <файл>`, не `git restore --staged`.
   - Удалить слитую ветку — `git branch -d`, не `-D`.
   - git вызывать как `git` — не `git.exe`, не через `wsl`, `bash -c` или `pwsh -c`.
   - Force push, `reset --hard` и `rebase` запрещены. Вместо rebase — `merge`.
   ```

6. Проверьте: в Claude Code на этой машине попросите выполнить
   `git log -1 --grep="push x --force"`. Правильный ответ — отказ («denied»): сработал запрет.

**Docker.** У контейнера свой профиль — том `claude-home` (`/home/app/.claude`). Запишите пример
туда от пользователя контейнера, чтобы CLI мог потом сам менять файл (включать плагины):

```bash
docker compose exec -T gateway sh -c "cat > /home/app/.claude/settings.json" < docs/examples/claude-settings.example.json
```

Правила `PowerShell(...)` и `git.exe` в Linux не нужны, но и не мешают.

## Ловушки в синтаксисе

- **`Bash(...)` и `PowerShell(...)` — разные инструменты.** Правило `Bash(git push -f*)`
  не остановит `git push -f` из инструмента PowerShell. Каждый запрет и каждое «спросить»
  дублируйте для обоих; в примере так и сделано. Алиасы PowerShell (`rm`, `curl`, `iwr`)
  CLI приводит к полным именам, поэтому `PowerShell(Invoke-WebRequest *)` ловит и `curl`,
  но не `curl.exe` — на него отдельное правило.
- `git push --force*` не ловит `git push -f` и `git push origin +master` — это отдельные правила.
  Зато `*` работает в любом месте строки: `Bash(mongosh*drop*)`.
- Правило сравнивает строку с начала: `git -C <папка> push --force` и
  `git push origin master --force` не совпадают с `git push --force*`. Поэтому запреты git
  написаны как `git*push*--force*`: ложное срабатывание (те же слова в тексте коммита) стоит
  одной переформулировки. `ask` записан так же (`git*checkout*`, `git*stash*drop*`), кроме `push`
  и `clean`: эти слова часты в тексте коммитов, а `git stash push` — обычная команда, поэтому у них
  пара `git push*` + `git -C * push*`. У `allow` — всегда пара `git -C * …`: широкий `allow`
  пропустил бы то, чего нет в `ask`.
- Обёртки (`wsl git`, `bash -c "git …"`, `pwsh -c`) не совпадают ни с одним `allow` и уходят на
  карточку или классификатор режима `auto`. Ловить их правилами не нужно.
- `allow` для git называет подкоманду, а не общий ключ: `git --no-pager*` пропустил бы молча
  `git --no-pager push` и `git --no-pager checkout -- файл` — `ask` на `git push*` с начала
  строки их не узнаёт. По той же причине удаление ветки в `ask` записано как `git*branch*-D*`,
  `-df`, `-fd`, `-d -f`, `-f -d`, `--force`: у него много форм.
- `allow` на широкий префикс пропускает и разрушительный вариант: `git switch*` пускает
  `git switch -f`, `git branch*` — `git branch -D`. `ask` и `deny` сильнее `allow`, поэтому
  опасные варианты перечислены в них отдельно.
- Пробел перед `*` — часть шаблона: `Bash(tail *)` не пустит `tailwind`, `Bash(tail*)`
  пустит. `Bash(git status)` без звёздочки — только эта команда без аргументов.
- Составные команды разбираются по частям: `deny` срабатывает, если совпала хотя бы одна
  часть `a && b`, `$(…)`, `a; b`. Для `allow` наоборот — каждая часть должна быть разрешена.
  Проверено 11.10.2026 и в Bash, и в PowerShell: `git log -1; git stash drop` спросил про `drop`.
- Пути: `//**/.env` — по всей файловой системе, `~/…` — от домашней папки, `**/.env` —
  только внутри текущего проекта. Одинарный `/…` — **не** корень диска, а корень источника
  настроек; не используйте.
- `mcp__сервер__*` в `allow` требует имени сервера (`mcp__plugin_playwright_playwright__*`);
  `mcp__*` там молча не работает, в `deny` — работает.

## Правила этого проекта

Файл `.claude/settings.json` в корне закрывает то, что личный конфиг знать не обязан:

- `mcp-gateway-<pid>-<запуск>.json` — токен, по которому `claude` ходит к шлюзу: чтение в `deny`.
  `.env` здесь тоже закрыт на чтение и правку, чтобы защита держалась и без личного файла;
- `appsettings.Local.json` — токен бота: чтение и правка только с вопросом. Если
  `protect-secrets` уже выполнен, там `dpapi:…`, но правило дешевле проверки;
- `dotnet run`, `AgentsTracker.Gateway.exe`, `schtasks`, `docker compose`, `Stop-Process`,
  `Stop-ScheduledTask` — с вопросом: из сессии, запущенной через Telegram, это остановит или
  задвоит сам шлюз (`operations.md`). Этот `ask` перебивает личный `allow` на `dotnet run`;
- `scripts\monitor-api.ps1` — без вопроса. Запрет на `curl`/`Invoke-RestMethod` не различает
  интернет и `127.0.0.1`, и агент по логам сессий раз за разом бился в него, пытаясь прочитать
  `/api/snapshot` монитора. Скрипт ходит только на loopback и только читает; расширять
  правило до `Invoke-RestMethod http://127.0.0.1*` нельзя — `deny` проверяется раньше `allow`.

Личное для проекта — в `.claude/settings.local.json`, он в `.gitignore`.

## Хуки

`hooks` в `~/.claude/settings.json` — тот же механизм ограничений, но программный. У автора
хук `PreToolUse` на `Agent` подменяет модель сабагентов на `sonnet` (правило «сабагенты только с
`model: "sonnet"`» из `CLAUDE.md` проекта). Хуки — скрипты вне проекта, в пример не входят;
правка папки хуков стоит в `ask`, чтобы агент не переписал собственные ограничители.

Хук и `CLAUDE_CODE_SUBAGENT_MODEL` в `env` задают алиас `sonnet`, а во что он раскроется, решает
CLI. CLI 2.1.283 ведёт его на `claude-sonnet-5`: вышедшую позже `claude-sonnet-5-5` он не знает.
Поэтому в собственном `env` автора (в примере его нет) стоит
`"ANTHROPIC_DEFAULT_SONNET_MODEL": "claude-sonnet-5-5[1m]"` — на неё идут сабагенты, кнопка `sonnet`
в чате и `/ask` (его модель по умолчанию — `sonnet`). `[1m]` обязателен: незнакомой модели CLI даёт
окно 200 000, а не 1 000 000. Переменная закрепляет версию — когда CLI узнает `sonnet-5-5` сам или
выйдет следующая, её надо убрать, иначе `sonnet` так и останется на 5-5.

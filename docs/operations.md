# Эксплуатация на машине разработки

Когда читать: нужно перезапустить шлюз, проверить правку живьём или вести долгую задачу,
не ломая работающий процесс.

## Перезапуск шлюза

На машине разработки exe запущен вручную из `bin\Debug`, задачи Планировщика обычно нет
(путь покажет `Get-Process AgentsTracker.Gateway`). Пока он работает, `dotnet build` падает
с `MSB3021` (exe занят).

```powershell
git status                                                # чужие незакоммиченные правки могут не собираться
Get-Process AgentsTracker.Gateway | Stop-Process -Force
dotnet build src\AgentsTracker.Gateway
Start-Process src\AgentsTracker.Gateway\bin\Debug\net10.0\AgentsTracker.Gateway.exe
```

- Рабочая папка не важна: `Program.cs` ставит `ContentRootPath = AppContext.BaseDirectory`;
  без этого exe из другой папки молча терял `appsettings.json`. Проверка — строка
  `Content root path` в логе.
- Собрать, не останавливая шлюз: `dotnet build src\AgentsTracker.Gateway -o <папка>` (именно
  проект: `-o` для `.slnx` даёт NETSDK1194). Проверить класс отдельно — временный консольный
  проект со ссылкой на готовую dll (`<Reference>` + `HintPath`), не `ProjectReference`: тот
  полез бы пересобирать занятый `bin\Debug`.
- Основная папка не собирается из-за чужих правок, а шлюз уже остановлен: собрать чистый
  HEAD в ту же папку —
  ```powershell
  git worktree add --detach ..\AgentsTracker-run HEAD
  dotnet build ..\AgentsTracker-run\src\AgentsTracker.Gateway -o src\AgentsTracker.Gateway\bin\Debug\net10.0
  git worktree remove --force ..\AgentsTracker-run
  ```
- Из сессии, запущенной самим шлюзом (из Telegram), перезапускать нельзя: `Stop-Process` убьёт
  и текущий `claude -p`, а отложенный `schtasks /SC ONCE` не срабатывал. Дайте пользователю
  команды выше и попросите выполнить руками.
- Консоль отдаёт русский в cp866 — лог смотрите в PowerShell (`iconv` в Git Bash нет).
- Бот после старта пишет «🔌 Шлюз запущен»; если перезапуск пришёлся на работающую задачу —
  в её чат уходит «прерван, напишите „продолжай“».

## Пробный экземпляр

Любой ключ конфига перекрывается переменной окружения, поэтому второй exe можно поднять рядом
с рабочим. Что задать пробе:

- `Gateway__Channel__Settings__BotToken` — поддельный, но формата `<число>:<строка>`, иначе
  канал не создастся;
- `Gateway__McpPort` и `Gateway__MonitorPort` — свои, рабочие заняты;
- `Gateway__ProjectPath` и `Gateway__Channel__Settings__AllowedUserIds__0` — если пробе нужно
  отвечать в чат; элемент массива задаётся индексом в конце имени;
- `Gateway__DataDirectory` — папка в scratchpad. Тогда у пробы свой `state.json` и она не
  читает боевой `appsettings.Local.json`: не мешает рабочему шлюзу и не зависит от формы его
  конфига. Без этого `state.json` общий — не запускайте пробу, пока идёт задача.

Живёт проба около минуты: бот один, и `getUpdates` второму процессу отдаёт 409. Этого хватает,
чтобы дёрнуть монитор. MCP-конфиг конфликта не создаёт — он у каждого свой,
`mcp-gateway-<pid>.json`.

Смоук-тест после правок инфраструктуры: скрипт в scratchpad, `pwsh -File`; через ~8 с
проверить:

- `/api/snapshot` — поля `agent`, `cliVersion`;
- `/api/limits`;
- 404 на `/` порта MCP;
- 401 на `POST /mcp` без токена, с заголовками `Content-Type: application/json` и
  `Accept: application/json, text/event-stream` — иначе придёт 415.

## Крупные задачи — в worktree

Шлюз запущен из `bin\Debug` основной папки `AgentsTracker` (ветка `master`; ветки `main` в
репозитории нет): переключение ветки подменит исходники под процессом, сломанная сборка
лишит возможности перезапустить.

```powershell
git worktree add ..\AgentsTracker-<задача> -b <ветка>   # основная папка остаётся папкой шлюза
```

Работайте в новой папке (из чата — `/project`, у неё свои сессии). Вливать — в два шага:
`git merge master` в worktree (конфликты, сборка и смоук-тест там), затем
`git merge --ff-only <ветка>` в основной папке — она меняется одним атомарным шагом.
Перед слиянием и остановкой шлюза — `git status`: в основной папке параллельно работают
другие сессии. Мелкие правки в один-два коммита — прямо в основной папке.

Скиллы ревью вызывайте с именем ветки (`code-review medium --fix <ветка>`): без аргумента они
берут незакоммиченный диф основной папки, а не вашу ветку, и правят чужую работу.

## Инструменты и окружение

- Русские тексты и windows-пути правьте Edit/Write, не heredoc и не python из Bash: `\a`, `\n`,
  `\r` в путях съедаются молча и неотличимы от опечатки. Скрипт — в scratchpad через Write,
  запуск файлом, результат проверять `grep … | cat -v` и сборкой.
- Исходники — UTF-8 **без BOM** (`utf-8-sig` добавит его молча).
- Сообщение коммита из нескольких абзацев — файлом в scratchpad и `git commit -F <файл>`:
  `-F -` с here-string из инструмента PowerShell stdin не получает.
- Ревьюеру-сабагенту без Bash `git show`/`git diff` недоступны: выгружайте старые версии
  файлов в scratchpad (`git show <коммит>:<путь> > …`).
- Из сессии через Telegram `AskUserQuestion` и `ExitPlanMode` ждут `ApprovalTimeoutMinutes`
  (15): без ответа берите рекомендуемый вариант. Длинный однострочник PowerShell на
  подтверждении легко отклонить не глядя — многошаговую проверку кладите в скрипт и
  запускайте `pwsh -File`.
- `curl`, `Invoke-WebRequest`, `Invoke-RestMethod` в `deny` — и для `127.0.0.1` тоже. Снимок
  монитора из сессии: `pwsh -File scripts\monitor-api.ps1 /api/snapshot` (только loopback,
  разрешён в `.claude/settings.json`); страницу целиком — `browser_run_code_unsafe` Playwright.
- `modern-web-guidance` (`npx.cmd -y modern-web-guidance@latest search "…"`) — из инструмента
  PowerShell: из Git Bash `npx.cmd` молча отдаёт пустой вывод.
- Промышленная установка и Docker (публикация, порты, секреты, обновление) — `deployment.md`.
  Автозапуск ставит сам exe: `install` заводит задачу Планировщика от текущего пользователя,
  не службу (OAuth-логин лежит в `%USERPROFILE%\.claude`, под SYSTEM он не найдётся),
  `uninstall` снимает её и добивает процессы, запущенные вручную из той же папки.

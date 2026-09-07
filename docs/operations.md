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
- `main` не собирается из-за чужих правок, а шлюз уже остановлен: собрать чистый HEAD в ту же
  папку —
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

Конфиг перекрывается переменными окружения (`$env:Gateway__ProjectPath`,
`Gateway__AllowedUserIds__0`), поэтому можно запустить второй exe с поддельным
`Gateway__BotToken` и своими `Gateway__McpPort`/`Gateway__MonitorPort`. Живёт ~минуту (бот
один, `getUpdates` отдаёт 409) — хватает дёрнуть монитор. MCP-конфиг у каждого экземпляра
свой (`mcp-gateway-<pid>.json`), поэтому рабочему шлюзу проба не мешает. А вот `state.json`
общий: не запускайте пробу, пока рабочий шлюз выполняет задачу.

Дымовой прогон после правок инфраструктуры: скрипт в scratchpad, `pwsh -File`; через ~8 с
проверить:

- `/api/snapshot` — поля `agent`, `cliVersion`;
- `/api/limits`;
- 404 на `/` порта MCP;
- 401 на `POST /mcp` без токена, с заголовками `Content-Type: application/json` и
  `Accept: application/json, text/event-stream` — иначе придёт 415.

## Крупные задачи — в worktree

Шлюз запущен из `bin\Debug` папки `main`: переключение ветки подменит исходники под
процессом, сломанная сборка лишит возможности перезапустить.

```powershell
git worktree add ..\AgentsTracker-<задача> -b <ветка>   # main остаётся папкой шлюза
```

Работайте в новой папке (из чата — `/project`, у неё свои сессии), готовое вливайте в `main`.
Перед слиянием и остановкой шлюза — `git status`: в `main` параллельно работают другие сессии.
Мелкие правки в один-два коммита — прямо в `main`.

## Инструменты и окружение

- Русские тексты и windows-пути правьте Edit/Write, не heredoc и не python из Bash: `\a`, `\n`,
  `\r` в путях съедаются молча и неотличимы от опечатки. Скрипт — в scratchpad через Write,
  запуск файлом, результат проверять `grep … | cat -v` и сборкой.
- Исходники — UTF-8 **без BOM** (`utf-8-sig` добавит его молча).
- Сообщение коммита из нескольких абзацев — файлом в scratchpad и `git commit -F <файл>`:
  `-F -` с here-string из инструмента PowerShell stdin не получает.
- Ревьюеру-сабагенту без Bash `git show`/`git diff` недоступны: выгружайте старые версии
  файлов в scratchpad (`git show <коммит>:<путь> > …`).
- Из сессии через Telegram `AskUserQuestion` и `ExitPlanMode` ждут ≤5 минут (idle-таймаут
  MCP): без ответа берите рекомендуемый вариант. Длинный однострочник PowerShell на
  подтверждении легко отклонить не глядя — многошаговую проверку кладите в скрипт и
  запускайте `pwsh -File`.
- `modern-web-guidance` (`npx.cmd -y modern-web-guidance@latest search "…"`) — из инструмента
  PowerShell: из Git Bash `npx.cmd` молча отдаёт пустой вывод.
- `install-autostart.ps1` ставит задачу Планировщика от текущего пользователя, не службу:
  OAuth-логин лежит в `%USERPROFILE%\.claude`, под SYSTEM он не найдётся. `-Uninstall` снимает.

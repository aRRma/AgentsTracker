# Веб-монитор

Когда читать: правите `Features/Monitor/` — `MonitorModule` или `index.html`.

## Сервер и API

Страница на **отдельном** порту `Gateway:MonitorPort` (по умолчанию 5100, `0` выключает; `Validate` не даёт
совпасть с `McpPort`). Kestrel слушает оба порта одним конвейером, поэтому группа эндпоинтов
фильтрует `Connection.LocalPort` — иначе страница открылась бы и на порту MCP (`/` там отдаёт
404). Авторизации нет намеренно, только loopback — поэтому эндпоинты **только читают**: `/stop` и
смена проекта остаются в чате, где есть разрешённые пользователи канала и журнал.

Тот же фильтр отвечает 403 на любой `Host`, кроме `localhost`, `127.0.0.1` и `[::1]`: так закрыт
DNS rebinding, когда чужой сайт направляет своё имя на `127.0.0.1` и браузер считает ответы
монитора ответами этого сайта. Порт в `Host` не проверяется — в контейнере там опубликованный порт
хоста, а не `MonitorPort`. Ответы, прошедшие фильтр, несут `X-Content-Type-Options: nosniff` и
`X-Frame-Options: DENY`.

Эндпоинты (все `GET`):

| Путь | Ответ |
| --- | --- |
| `/` | страница |
| `/api/snapshot` | `at`, `version`, `gatewayStartedUtc`, `agent`, `cliVersion`, `project`, `session`, `model`, `permissionMode`, `effort`, `isBusy`, `isAnswering`, `run`, `approvals`, `queue` |
| `/api/events` | SSE: `state` (тот же снимок) и пустой `ping` |
| `/api/limits` | `error`, `fetchedUtc`, `extraUsageEnabled`, `model`, `windows[]` (`key`, `used`, `percent`, `left`, `resetsAt`) |
| `/api/runs?project=&limit=` | последние запуски, новые первыми, `limit` 1..200 (по умолчанию 200) |
| `/api/stats` | `sinceUtc`, `total`, `byDay`, `byModel`, `skills`, `sessions`, `rules` |
| `/api/stats.csv` | расход по дням файлом |
| `/api/audit?count=` | хвост журнала, `count` 1..1000 (по умолчанию 100) |
| `/api/log?count=&level=` | хвост лога, `count` 1..500 (по умолчанию 200), `level` — минимальный (по умолчанию `Information`) |

Добавить эндпоинт — [extending.md](extending.md).

«Что сейчас» — `RunMonitor`: `ChatWorker` сообщает очередь, старт, шаги (тот же callback, что у
`RunStatusMessage`) и финиш; `OperatorConsole` — ожидание карточки через
`using monitor.Approval(tool, brief)`, brief — короткая строка, не полный ввод. Карточек в снимке
список: CLI зовёт инструменты параллельно в одном ходе, и карточки вопроса попадают туда же. `run`,
`queue` и `isBusy` — только про задачи; параллельный вопрос виден как `isAnswering` и в истории
запусков, но не как текущий запуск. С темами задач идёт несколько разом: `isBusy` — true, пока идёт
хоть одна, `run` — первая из них, в `queue` — задачи всех чатов, а `project`, `session`, `model`,
`permissionMode`, `effort` — выбор ленты. Хранятся последние шаги (`StepsKept`), `DroppedSteps` — разница со счётчиком.
`Changes()` — канал ёмкостью 1 с вытеснением: медленный браузер получает последнее состояние, а не
очередь устаревших. `/api/events` шлёт `ping` раз в 5 с между снимками — без него страница не
отличит тишину от упавшего шлюза.

`version` — `AppVersion.Current`, тот же номер, что в сообщении «🔌 Шлюз запущен»: после обновления
папки установки больше нечем отличить работающую сборку от лежащей на диске.

История — `GatewayState.RecentRuns` (`SessionStore.RecordRunOutcome` после запуска).
`/api/stats.csv` — `;`, BOM и десятичная запятая, иначе Excel на русской локали читает дробные как
текст. Лимиты — `IAgentLimits.GetAsync` (кэширует агент); страница опрашивает лимиты, журнал и лог
по своим таймерам. Лог — `RingBufferLog` (Information+, маскирует `RingBufferLoggerProvider`; те же
записи уходят в файл лога).

## Как смотреть страницу

`index.html` — один файл без сборки и CDN, `EmbeddedResource`; данные `/api/*` в camelCase,
кириллица без `\u`. Правка страницы требует `dotnet build` и перезапуска шлюза.

Без шлюза: копия страницы и `scripts\monitor-mock.js` в scratchpad,
`<script src="monitor-mock.js">` перед основным скриптом (мок подменяет `fetch` данными `/api/*` и
`EventSource` снимком, `?state=run|wait|idle`; новый эндпоинт идёт в `routes`) и
`python -m http.server <порт> --bind 127.0.0.1` из scratchpad (Playwright не открывает `file://`).

- Доступность — `browser_snapshot` Playwright MCP (дерево ролей и имён).
- Тёмная тема — только скриптом Playwright: `page.emulateMedia({colorScheme:'dark'})`.
- Скриншот целиком: `browser_navigate` на порт, `browser_take_screenshot` с `filename`. Скриншоты
  падают в корень репозитория (они в `.gitignore`); чтобы показать человеку, используйте
  `mcp__tg__send_file` и удалите файл.
- Отдельная панель — `browser_take_screenshot` с селектором в `target` (`.gauges`, `.facts`): на
  снимке всей страницы правка теряется в блоке рейки.
- Состояния, тёмная тема, узкое окно и вычисленные стили — один вызов `browser_run_code_unsafe`.
- `/api/*` без браузера — `pwsh -File scripts\monitor-api.ps1 "api/snapshot"` (`curl` и
  `Invoke-WebRequest` в `deny`, loopback не исключение); `-Port` — для пробного экземпляра (по
  умолчанию 5100). Звать из инструмента **PowerShell** без ведущего `/`: из Bash MSYS превращает
  `/api/snapshot` в `C:/Program Files/Git/api/snapshot`, и скрипт отклоняет аргумент.
- Порт `5100` — рабочий шлюз: он отдаёт страницу из своей сборки, и вашей правки там нет.
  Смотрите её и снимайте скриншот на пробном экземпляре (`docs/operations.md`).

## Стиль

Fluent 2 «Mica» с приёмами Grafana/Elastic: подложка `--canvas`, содержимое в карточках `.card`,
акцент `--brand`, сигналы `--run`/`--wait`/`--fail`. Слева рейка (статус, лимиты, факты,
навигация), справа панели.

Что стоит знать, правя разметку:

- шкалы лимитов заполняются **расходом** (полная полоса — окно исчерпано), остаток уходит в
  подпись; `/api/limits` присылает `percent` и `left` готовыми — своей формулы округления в JS нет
  ([architecture.md](architecture.md), «Числа»);
- спарклайны рисуются только там, где есть дневной ряд `stats.byDay`;
- селекты и ссылки — в `.band-head` рядом с `<h2>`, не внутри заголовка;
- лента `.tape`: время · инструмент · аргумент. Имя инструмента `splitStep` берёт как первое слово
  описания, поэтому `ClaudeStreamEvent.Describe` обязан начинать строку именем.

## Скрипт страницы

`renderLive` перестраивает DOM только по кадру SSE, а секундомер, аптайм и «ждёт N с»
(`[data-since]`) тикает `tick()` через `textContent` (перерисовка `innerHTML` раз в секунду
сбрасывала бы выделение текста и скролл). Лента прокручивается вниз только при новых
шагах (`tapeKey`). В скрытой вкладке таймеры молчат, на возврат запускается `loadAll()`.

Доступность: заголовки и `aria-label` у навигации, подписи у
селектов, `caption`/`scope` у таблиц, график — `role="img"` плюс таблица в `<details>`, размеры в
`rem`, тёмная тема через `color-scheme`, `prefers-reduced-motion` глушит лампу и плавную
прокрутку, `forced-colors` даёт шкалам рамку и заливку `Highlight`, а лампе, карточкам
подтверждения, карточкам, пилюле состояния и точкам `.mark` — рамку.

Добавляя блок: прокручиваемой области нужен
`tabindex="0"`, декоративной картинке вроде спарклайна — `aria-hidden`, а `role="status"` ставится
только на состоянии и соединении — на часах он превращается в спам для чтеца.

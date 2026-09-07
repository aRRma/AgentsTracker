# Веб-монитор

Когда читать: правите `Features/Monitor/` — `MonitorModule` или `index.html`.

## Устройство

Страница на **отдельном** порту `Gateway:MonitorPort` (5100, `0` выключает; `Validate` не даёт
совпасть с `McpPort`). Kestrel слушает оба порта одним конвейером, поэтому группа эндпоинтов
фильтрует `Connection.LocalPort` — иначе страница открылась бы и на порту MCP (`/` там отдаёт
404). Авторизации нет намеренно, только loopback — поэтому эндпоинты **только читают**: `/stop`
и смена проекта остаются в Telegram, где есть `AllowedUserIds` и аудит.

Эндпоинты: `/`, `/api/snapshot`, `/api/events` (SSE), `/api/limits`,
`/api/runs?project=&limit=`, `/api/stats`, `/api/stats.csv`, `/api/audit?count=`,
`/api/log?count=&level=`. Новый — `api.MapGet` в `MonitorModule.MapEndpoints`,
`Results.Json(..., Json)`, секция в `index.html`.

«Что сейчас» — `RunMonitor`: `ChatWorker` сообщает очередь, старт, шаги (тот же callback, что
у `RunStatusMessage`) и финиш; `OperatorConsole` — ожидание карточки через
`using monitor.Approval(tool, brief)`, brief — короткая строка, не полный ввод. Карточек в
снимке список: CLI зовёт инструмент параллельно на несколько `tool_use` одного хода. Шагов
хранится 300, `DroppedSteps` — разница со счётчиком. `Changes()` — канал ёмкостью 1 с
вытеснением: медленный браузер получает последнее состояние, а не очередь устаревших.
`/api/events` шлёт снимок при подключении, далее по изменениям, между ними `ping` раз в 5 с —
без него страница не отличит тишину от упавшего шлюза.

История — `GatewayState.RecentRuns` (200, `SessionStore.RecordRunOutcome` после запуска;
исход определяет `ChatWorker`, расход — из `AgentRunResult.Usage`). `/api/stats.csv` — `;`,
BOM и десятичная запятая: иначе Excel на русской локали читает дробные как текст. Лимиты —
`IAgentLimits.GetAsync` (у Claude кэш 3 мин, страница опрашивает раз в минуту); лог —
`RingBufferLog` (500 записей, Information+, зарегистрирован как `ILoggerProvider`).

## Как смотреть страницу

`index.html` — один файл без сборки и CDN, `EmbeddedResource`; данные `/api/*` в camelCase,
кириллица без `\u`; правка страницы требует `dotnet build` и перезапуска шлюза.

Без шлюза: копия страницы и `scripts\monitor-mock.js` в scratchpad,
`<script src="monitor-mock.js">` перед основным скриптом (мок подменяет `fetch` данными
`/api/*` и `EventSource` снимком, `?state=run|wait|idle`; новый эндпоинт — добавить в
`routes`), `python -m http.server <порт> --bind 127.0.0.1` из scratchpad (Playwright не
открывает `file://`).

- Доступность — `browser_snapshot` Playwright MCP (дерево ролей и имён).
- Тёмная тема — только скриптом Playwright: `page.emulateMedia({colorScheme:'dark'})`.
- Скриншоты падают в корень репозитория, они в `.gitignore`.
- Варианты дизайна — скрипт в scratchpad, который подменяет в копии страницы только блок
  `<style>` (разметка и скрипт общие, сравнение честное) и вставляет мок.
- Проверка состояний, тёмной темы, узкого окна и вычисленных стилей — одним вызовом
  `browser_run_code_unsafe`; он же проверяет живой `http://127.0.0.1:5100/` (`curl` и
  `Invoke-WebRequest` в разрешениях нет).
- Синие цифры в скриншоте таблицы — субпиксельный артефакт, сверяйте
  `getComputedStyle(td).color`.

## Стиль

Fluent 2 «Mica» с приёмами Grafana/Elastic: подложка `--canvas`, всё содержимое в карточках
`.card` (`--layer`/`--stroke`, тень `--shadow-2`), акцент `--brand` только на графике, ссылках
и метках инструментов; сигналы — `--run`/`--wait`/`--fail`; шрифты Segoe UI Variable и
Cascadia.

Рейка слева: статус-pill `.state` с лампой, секундомер, навигация по разделам (`.nav`, якоря
`#now #stats #runs-band …`, текущий подсвечивает `IntersectionObserver`, у `.band` есть
`scroll-margin-top`; на телефоне навигация скрыта), лимиты как bar gauge (`.gauge`: имя ·
остаток % цветом · полоса · строка сброса, порог тот же, что у `LimitBars`), факты шлюза.

Справа панели: «Сейчас» (карточка подтверждения `.approval` с «ждёт N с», промпт,
`.runmeta`-чипы, лента), stat-плитки `.figures` со спарклайнами (`spark`, 14 дней
`stats.byDay` — только где есть дневной ряд), график с двумя линиями сетки и подписями оси Y
(сегодняшний столбец `.today`), таблицы с липкой шапкой, `td.when` для времени, `.mark` —
точка + текст, промпт `td.clamp` в две строки с полным текстом в `title`, счётчик строк
`.count` у заголовка. Селекты и ссылки живут в `.band-head` рядом с `<h2>`, не внутри него.

Лента `.tape` — три колонки: время · метка инструмента `.tool` · аргумент `.arg`; `splitStep`
берёт первое слово описания как имя инструмента, поэтому `ClaudeStreamEvent.Describe` должен
начинать строку именем инструмента. Последний шаг — `.last`.

## Скрипт

`renderLive` перестраивает DOM только по кадру SSE, а секундомер, аптайм и «ждёт N с»
(`[data-since]`) тикает `tick()` через `textContent` — перерисовка `innerHTML` раз в секунду
сбрасывала выделение текста в ленте и прыгала скроллом; лента прокручивается вниз только при
новых шагах (`tapeKey`). В скрытой вкладке таймеры молчат, на возврат — `loadAll()`.

Проверено по modern-web-guidance: `<h1>` в рейке, `nav[aria-label]`, селекты с
`label.visually-hidden`, `caption`/`scope` у таблиц, график `role="img"` + таблица в
`<details>`, спарклайны `aria-hidden`, `role="status"` только на состоянии и соединении (не
на часах — спам), `tabindex="0"` у прокручиваемых областей, размеры в `rem`,
`color-scheme: light dark`, `prefers-reduced-motion` глушит анимацию лампы и
`scroll-behavior`, в `forced-colors` рамки у шкал, карточек, pill и точек `.mark`.

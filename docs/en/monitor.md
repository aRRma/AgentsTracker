# Web monitor

When to read this: you're editing `Features/Monitor/` — `MonitorModule` or `index.html`.

## Server and API

The page runs on a **separate** port `Gateway:MonitorPort` (default 5100, `0` disables it; `Validate`
won't let it collide with `McpPort`). Kestrel serves both ports through one pipeline, so the
endpoint group filters by `Connection.LocalPort` — otherwise the page would also open on the MCP
port (`/` returns 404 there). There is deliberately no authorization, only loopback — so the
endpoints **only read**: `/stop` and switching the project stay in chat, where the channel's
allowed users and the journal are.

The same filter answers 403 to any `Host` other than `localhost`, `127.0.0.1` or `[::1]`: that
closes DNS rebinding, where a foreign site points its own name at `127.0.0.1` and the browser
treats the monitor's answers as that site's own. The port in `Host` is not checked — in a
container it is the host's published port, not `MonitorPort`. Answers that pass the filter carry
`X-Content-Type-Options: nosniff` and `X-Frame-Options: DENY`.

Endpoints (all `GET`):

| Path | Answer |
| --- | --- |
| `/` | the page |
| `/api/snapshot` | `at`, `version`, `gatewayStartedUtc`, `agent`, `cliVersion`, `project`, `session`, `model`, `permissionMode`, `effort`, `isBusy`, `isAnswering`, `run`, `approvals`, `queue` |
| `/api/events` | SSE: `state` (the same snapshot) and an empty `ping` |
| `/api/limits` | `error`, `fetchedUtc`, `extraUsageEnabled`, `model`, `windows[]` (`key`, `used`, `percent`, `left`, `resetsAt`) |
| `/api/runs?project=&limit=` | recent runs, newest first, `limit` 1..200 (default 200) |
| `/api/stats` | `sinceUtc`, `total`, `byDay`, `byModel`, `skills`, `sessions`, `rules` |
| `/api/stats.csv` | usage by day as a file |
| `/api/audit?count=` | the journal tail, `count` 1..1000 (default 100) |
| `/api/log?count=&level=` | the log tail, `count` 1..500 (default 200), `level` is the minimum (default `Information`) |

Adding an endpoint — `extending.md`.

"What's happening now" is `RunMonitor`: `ChatWorker` reports the queue, start, steps (the same
callback as `RunStatusMessage`) and finish; `OperatorConsole` reports waiting for a card via
`using monitor.Approval(tool, brief)`, brief being a short string, not the full input. Cards in the
snapshot are a list: the CLI calls tools in parallel within one turn, and a вопрос's cards land
there too. `run`, `queue` and `isBusy` describe tasks only; a parallel вопрос shows as
`isAnswering` and in the run history, never as the current run. With topics several tasks run at
once: `isBusy` is true while any of them runs, `run` is the first of them, the `queue` holds every
chat's tasks, and `project`, `session`, `model`, `permissionMode`, `effort` are the лента's selection. The last steps are kept
(`StepsKept`), `DroppedSteps` is the difference against the counter. `Changes()` is a channel with capacity 1 and eviction: a slow
browser gets the latest state, not a queue of stale ones. `/api/events` pings every 5 s between
snapshots — without it the page couldn't tell silence from a crashed gateway.

`version` is `AppVersion.Current`, the same number as in the «🔌 Шлюз запущен» message: after the
install folder is updated, nothing else tells the running build from the one on disk.

History is `GatewayState.RecentRuns` (`SessionStore.RecordRunOutcome` after a run).
`/api/stats.csv` uses `;`, a BOM and a decimal comma, or Excel under the Russian locale reads
fractions as text. Limits come from `IAgentLimits.GetAsync` (cached by the agent); the page polls
limits, the journal and the log on its own timers. The log is `RingBufferLog` (Information+, masked
by `RingBufferLoggerProvider` — the same entries go to the log file).

## How to look at the page

`index.html` is a single file with no build step and no CDN, an `EmbeddedResource`; `/api/*` data
is camelCase, Cyrillic without `\u` escapes. Editing the page needs `dotnet build` and a gateway
restart.

Without the gateway: a copy of the page and `scripts\monitor-mock.js` in the scratchpad,
`<script src="monitor-mock.js">` before the main script (the mock substitutes `fetch` with `/api/*`
data and `EventSource` with a snapshot, `?state=run|wait|idle`; a new endpoint goes into `routes`),
and `python -m http.server <port> --bind 127.0.0.1` from the scratchpad (Playwright can't open
`file://`).

- Accessibility — `browser_snapshot` from Playwright MCP (the tree of roles and names).
- Dark theme — only via a Playwright script: `page.emulateMedia({colorScheme:'dark'})`.
- A whole-page shot: `browser_navigate` to the port, `browser_take_screenshot` with a `filename`.
  Screenshots land in the repository root (in `.gitignore`); to show one to a human use
  `mcp__tg__send_file`, then delete the file.
- A single panel — `browser_take_screenshot` with a `target` selector (`.gauges`, `.facts`): a
  whole-page shot buries a change in a rail block.
- States, dark theme, a narrow window and computed styles — one `browser_run_code_unsafe` call.
- `/api/*` without a browser — `pwsh -File scripts\monitor-api.ps1 "api/snapshot"` (`curl` and
  `Invoke-WebRequest` are in `deny`, loopback is no exception); `-Port` for a scratch instance
  (default 5100). Call it from the **PowerShell** tool without a leading `/`: from Bash, MSYS
  rewrites `/api/snapshot` into `C:/Program Files/Git/api/snapshot`, and the script refuses it.
- Port `5100` is the working gateway, serving the page from its own build — your edit is not
  there. Look at it, and take the screenshot, on a scratch instance (`docs/en/operations.md`).

## Style

Fluent 2 "Mica" with Grafana/Elastic techniques: `--canvas` backdrop, content in `.card` cards,
`--brand` accent, `--run`/`--wait`/`--fail` signals. A rail on the left (status, limits, facts,
navigation), panels on the right.

Worth knowing when editing the markup:

- the limit bars fill with **consumption** (a full bar means the window is exhausted), the
  remainder goes into the caption; `/api/limits` sends `percent` and `left` ready-made — no
  rounding formula in JS (`architecture.md`, "Numbers");
- sparklines are drawn only where there's a daily series `stats.byDay`;
- selects and links go in `.band-head` next to `<h2>`, not inside the heading;
- the `.tape` feed: time · tool · argument. `splitStep` takes the tool name as the first word of
  the description, so `ClaudeStreamEvent.Describe` must start the string with the name.

## Page script

`renderLive` rebuilds the DOM only on an SSE frame, while the stopwatch, uptime and «ждёт N с»
(`[data-since]`) are ticked by `tick()` through `textContent` (redrawing `innerHTML` once a second
would reset text selection and scroll). The feed scrolls down only on new steps
(`tapeKey`). In a hidden tab the timers stay quiet, and on return `loadAll()` runs.

Accessibility: headings and `aria-label` on navigation,
labels on selects, `caption`/`scope` on tables, the chart is `role="img"` plus a table in
`<details>`, sizes in `rem`, dark theme via `color-scheme`, `prefers-reduced-motion` mutes the lamp
and smooth scrolling, `forced-colors` gives the bars a border and a `Highlight` fill and the lamp,
approval cards, cards, status pill and `.mark` dots a border.

When adding a block: a scrollable area needs `tabindex="0"`, a
decorative image like a sparkline needs `aria-hidden`, and `role="status"` goes only on the state
and connection — on a clock it becomes spam for a screen reader.

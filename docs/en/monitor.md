# Web Monitor

When to read this: you're editing `Features/Monitor/` — `MonitorModule` or `index.html`.

## Design

The page runs on a **separate** port `Gateway:MonitorPort` (5100, `0` disables it; `Validate`
won't let it collide with `McpPort`). Kestrel serves both ports through one pipeline, so the
endpoint group filters by `Connection.LocalPort` — otherwise the page would also open on the
MCP port (`/` returns 404 there). There is deliberately no authorization, only loopback — so
the endpoints **only read**: `/stop` and switching the project stay in chat, where there's a
list of the channel's allowed users and an audit log.

Endpoints: `/`, `/api/snapshot`, `/api/events` (SSE), `/api/limits`,
`/api/runs?project=&limit=`, `/api/stats`, `/api/stats.csv`, `/api/audit?count=`,
`/api/log?count=&level=`. A new one is `api.MapGet` in `MonitorModule.MapEndpoints`,
`Results.Json(..., Json)`, a section in `index.html`.

"What's happening now" is `RunMonitor`: `ChatWorker` reports the queue, start, steps (the same
callback as `RunStatusMessage`) and finish; `OperatorConsole` reports waiting for an approval
card via `using monitor.Approval(tool, brief)`, where brief is a short string, not the full
input. Approval cards in the snapshot are a list: the CLI calls the tool in parallel for
several `tool_use` calls in one turn. 300 steps are kept, `DroppedSteps` is the difference
against the counter. `Changes()` is a channel with capacity 1 and eviction: a slow browser gets
the latest state, not a queue of stale ones. `/api/events` sends a snapshot on connect, then on
changes, with a `ping` between them every 5 s — without it the page couldn't tell silence apart
from a crashed gateway.

The gateway version is `AppVersion.Current` in the snapshot's `version` field, shown as the
first row of the rail's "Шлюз" block. It is the same number as in the «🔌 Шлюз запущен» message:
otherwise, after the install folder is updated, there is no way to tell the running build from
the one sitting on disk.

History is `GatewayState.RecentRuns` (200 entries, `SessionStore.RecordRunOutcome` after a run;
the outcome is decided by `ChatWorker`, usage comes from `AgentRunResult.Usage`).
`/api/stats.csv` uses `;`, a BOM, and a decimal comma: otherwise Excel under the Russian locale
reads fractions as text. Limits come from `IAgentLimits.GetAsync` (for Claude, cached for
3 min; the page polls once a minute); the log is `RingBufferLog` (500 entries, Information+,
registered as an `ILoggerProvider`).

## How to look at the page

`index.html` is a single file with no build step and no CDN, an `EmbeddedResource`; `/api/*`
data is in camelCase, Cyrillic without `\u` escapes; editing the page requires `dotnet build`
and restarting the gateway.

Without the gateway: a copy of the page and `scripts\monitor-mock.js` in the scratchpad,
`<script src="monitor-mock.js">` before the main script (the mock substitutes `fetch` with
`/api/*` data and `EventSource` with a snapshot, `?state=run|wait|idle`; a new endpoint — add
it to `routes`), `python -m http.server <port> --bind 127.0.0.1` from the scratchpad
(Playwright can't open `file://`).

- Accessibility — `browser_snapshot` from Playwright MCP (the tree of roles and names).
- Dark theme — only via a Playwright script: `page.emulateMedia({colorScheme:'dark'})`.
- A whole-page shot: `browser_navigate` to the right port, `browser_take_screenshot` with a
  `filename`. Screenshots land in the repository root, they're in `.gitignore`; to show one to a
  human — `mcp__tg__send_file`, then delete the file, the repository does not need it.
- Design variants — a script in the scratchpad that swaps only the `<style>` block in a copy
  of the page (the markup and script stay shared, so the comparison is fair) and inserts the
  mock.
- Checking states, dark theme, a narrow window, and computed styles — in one call to
  `browser_run_code_unsafe`; the same call checks the live `http://127.0.0.1:5100/`. `/api/*`
  itself without a browser — `pwsh -File scripts\monitor-api.ps1 api/snapshot` (`curl` and
  `Invoke-WebRequest` are in `deny`, loopback is not an exception).
- Port `5100` is the **working** gateway, on this machine the release: it serves the page from
  its own build, so your edit is not there. Look at your own change on a scratch instance
  (`docs/en/operations.md`) — and take the screenshot from its port too.
- A single panel — `browser_take_screenshot` with a `target` selector (`.gauges`, `.facts`):
  a whole-page shot buries a change in a rail block.
- Blue digits in a table screenshot are a subpixel artifact, check against
  `getComputedStyle(td).color`.

## Style

Fluent 2 "Mica" with Grafana/Elastic techniques: `--canvas` as the backdrop, content in `.card`
cards, the `--brand` accent only on the chart, links, and tool labels; signals are
`--run`/`--wait`/`--fail`; fonts are Segoe UI Variable and Cascadia. A rail on the left
(status, plan limits as bars, section navigation), panels on the right: «Сейчас» (Now),
statistics, tables, audit, log.

Worth knowing when editing the markup:

- the limit bars fill with **consumption** (a full bar means the window is exhausted), the
  remainder goes into the caption below; the rounding and the color threshold are the same as
  `LimitBars` in chat — the numbers on the page and in chat must not diverge;
- sparklines are drawn only where there's a daily series `stats.byDay` (14 days);
- selects and links go in `.band-head` next to `<h2>`, not inside the heading;
- the `.tape` feed: time · tool · argument. `splitStep` takes the tool name as the first word
  of the description, so `ClaudeStreamEvent.Describe` must start the string with the name.

## Script

`renderLive` rebuilds the DOM only on an SSE frame, while the stopwatch, uptime, and «ждёт N с»
(waiting N s) (`[data-since]`) are ticked by `tick()` through `textContent` — redrawing
`innerHTML` once a second reset text selection in the feed and jumped the scroll; the feed
scrolls down only on new steps (`tapeKey`). In a hidden tab the timers stay quiet, and on
return — `loadAll()`.

Accessibility was checked against modern-web-guidance: headings and `aria-label` on
navigation, labels on selects, `caption`/`scope` on tables, the chart is `role="img"` plus a
table in `<details>`, sizes in `rem`, dark theme via `color-scheme`, `prefers-reduced-motion`
mutes the lamp animation and smooth scrolling, in `forced-colors` borders are added to the
bars, cards, the status pill, and the `.mark` dots.

When adding a block, repeat three rules people forget: a scrollable area needs
`tabindex="0"`, a decorative image like a sparkline needs `aria-hidden`, and `role="status"`
is set only on the state and connection: on a clock it turns into spam for a screen reader.

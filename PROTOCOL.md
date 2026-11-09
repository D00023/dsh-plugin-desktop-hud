# DSH Desktop HUD — bridge protocol v3 (state file, multi-task)

Transport: **one small UTF-8 JSON file** that the Host plugin writes and `HUD.exe` polls.
This replaced the v1 named pipe because a child process spawned by the Host cannot reliably
connect to a pipe owned by the Host process in every environment (verified on this machine);
a file works everywhere and keeps both sides trivially simple.

- State file (Host → HUD): `%LOCALAPPDATA%\dsh-desktop-hud\state.json`
- User-quit marker (HUD → Host): `%LOCALAPPDATA%\dsh-desktop-hud\hud-bye-v1.json`
- Window position (HUD only): `%LOCALAPPDATA%\dsh-desktop-hud\window-v1.json`

CLI: `HUD.exe --state-file "<absolute path>"` — the Host passes the state-file path explicitly,
so a profile-local directory or a test directory can be used instead of `%LOCALAPPDATA%`.
`--demo`, `--demo-multi`, `--state=<mode>` and `--expanded` work without a Host. `--pipe` is
retained as a legacy fallback; the Host does not pass it.

## State file (Host writes, HUD reads)

Single line, no pretty printing, written **atomically** (`state.json.tmp` → rename):

```json
{"v":3,"mode":"attention","title":"…","detail":"…","session":"…","since":<epoch ms>,"ts":<epoch ms>,
 "tasks":[{"session":"…","title":"…","status":"running|attention|question|done|error","detail":"…","since":<epoch ms>}]}
```

| field | meaning |
|---|---|
| `v` | Protocol version. `3` = multi-task. Missing/`2` = single-task legacy (HUD renders the collapsed card only, ignores any `tasks`). |
| `mode` | Aggregated state for the **collapsed** card. Precedence: `attention` > `question` > `error` > `running` > `done` > `idle`. Unknown values fall back to `idle`. |
| `title` | Title of the task the collapsed card shows (session title, else `DeepSeek Harness`; the HUD may substitute the DSH window title, see *Title resolution*). |
| `detail` | Secondary line for `attention`/`question` (`需要你授权`/`需要你回答`); empty otherwise. |
| `session` | Session id of the collapsed card's task (opaque; for logging/debug). |
| `since` | When the collapsed state began (epoch ms). |
| `ts` | Host write time (epoch ms). Not shown; only used to compute file age. |
| `tasks` | Ordered concurrent task list; may be empty. The collapsed card always shows `tasks[0]` (the Host derives `mode/title/detail/session` from it). |

### Task list

The Host is authoritative for content **and order**:

- Order: status priority `attention` > `question` > `error` > `running` > `done`, then `since`
  newest first inside the same status.
- Contents: **root** sessions only (`session.header.origin !== 'subagent'`) that are running,
  waiting for the user, erroring, or finished within the last 15 s. Idle sessions are not listed.
  Authorizations/questions raised inside a subagent are folded onto the root session.
- The Host sends **all** valid tasks (no truncation); the HUD truncates only when rendering
  (first 4 rows + a `+N 个任务` summary row).
- Each task: `session` (root session id), `title` (session title, else `未命名会话`), `status`,
  `detail` (same strings as the collapsed card), `since` (when that status began).
- Unknown `status` values are rendered as `running`.

### Collapsed card vs expanded card

- `tasks.length <= 1` → collapsed card exactly as in v2 (small pill when idle, single 320×72 card
  otherwise); no expand button and no badge.
- `tasks.length >= 2` → the collapsed card gains a `⌄` button and a count badge; the badge shows
  the task count (`>9` → `9+`).
- Expanding grows the card **upward** (bottom edge fixed): max 4 task rows (42 DIP each) plus a
  `+N 个任务` summary row; the overall card border/glow follows the highest-priority task.

Heartbeat: the Host rewrites the file every **2 s** even when nothing changed, only to keep its
mtime fresh. In addition it writes immediately on every state change. Because the HUD may hold
the file open for a moment while reading it, the Host retries the `rename` up to 4 times (25 ms
apart) and, if Windows still refuses, falls back to an in-place rewrite of `state.json`; the HUD
must therefore ignore a half-written line and keep the last good state.

The Host also appends lifecycle lines to `%LOCALAPPDATA%\dsh-desktop-hud\hud.log`
(rotated at 256 KB) so the overlay can be diagnosed without a console.

### HUD read rules

- On start: show the small pill (`● DSH 空闲`) while the file does not exist yet; exit with code 0
  after 90 s without ever seeing a file (override for tests: `DSH_HUD_STALE_SECONDS`).
- Watch the directory (`FileSystemWatcher`: Changed/Created/Renamed) → read immediately;
  also poll every **400 ms** as a safety net.
- Open the file with `FileShare.ReadWrite | FileShare.Delete` so the Host can always replace it.
- Ignore partial/malformed reads and keep the previous state.
- Apply a state idempotently: when `mode`, `title`, `detail`, `session` **and the task list**
  (`session/title/status/detail/since` per row) are unchanged since the last applied state, do
  **not** re-render or restart animations (the 2 s heartbeat rewrite must not make the done flash
  or the attention pulse stutter).
- If the file mtime is older than **10 s** (override: `DSH_HUD_STALE_SECONDS`), the Host is gone:
  exit with code 0. Since the file is deleted on graceful shutdown, a missing file *after* a
  successful read is treated the same way (immediate exit).
- `--demo` mode: `HUD.exe --demo` cycles every 2.5 s through idle → running → attention →
  question → done → error → idle; `HUD.exe --demo --state=attention` freezes one state.
  `--demo-multi` cycles the multi-task samples (2/3/6 tasks, attention/question/done/error rows).
  `--expanded` opens in expanded mode. All are for visual verification/screenshots only.

## User-quit marker (HUD writes, Host reads)

When the user picks **「退出小窗」** from the HUD context menu (only that menu item —
"隐藏 30 分钟" does not), the HUD writes `hud-bye-v1.json`:

```json
{"t":"bye","ts":<epoch ms>}
```

and then exits with code 0. The Host, on seeing its child exit, reads the file: if `ts` is at or
after the moment that HUD was spawned, the user quit on purpose — the Host stops respawning for
the rest of this DSH process (so a manual restart of DSH brings the HUD back) and deletes the
state file. If the file is absent/stale, the exit was unexpected and the Host respawns the HUD
(up to 5 times, 2 s apart). The Host deletes any stale bye file before spawning.

## Title resolution

The Host resolves titles itself: `session/title` events are folded by the `sessionTitle` service.
For subagent sessions it walks up `parentSession` to the root session. Row titles never fall back
to the window title (they show `未命名会话` instead). Only the **collapsed card's** title falls
back to the **DSH main window title** when it is empty or the placeholder `"DeepSeek Harness"`:
find the top-level window whose process is `DeepSeek Harness` (class `Chrome_WidgetWin_1`),
`GetWindowText` it (e.g. `检查 llama.cpp 安装情况 — DeepSeek Harness`), strip the trailing
`" — DeepSeek Harness"` (also accept `" - DeepSeek Harness"`), cache it and refresh every ~2 s
and on state changes. If the window cannot be found either, display `"DSH"`.

## State semantics (Host side is authoritative)

- `idle`: no task at all → HUD shows the small pill.
- `running`: at least one agent executing → card with the newest running task's title, "执行中",
  blue breathing dot + bottom shimmer.
- `attention`: an approval request is genuinely waiting → amber border pulse, `detail = 需要你授权`.
- `question`: the agent asked the user a question and is waiting → blue highlight,
  `detail = 需要你回答`.
- `done`: a turn just finished successfully → green check, "任务完成", pulse once. The task stays
  in the list (and the collapsed card) for **15 s**, then the Host re-publishes without it; the
  HUD also collapses its own done row after 15 s as a safety net.
- `error`: the last turn failed → red "出错了"; the task stays listed until that session runs again.

## Failure behaviour

- The HUD never shows a modal error. Missing/stale state file → it shows idle (or exits per the
  rules above). A `tasks` list longer than 4 rows is truncated on screen, never an error.
- The Host never blocks DSH: if the overlay cannot spawn, it logs and retries while leaving
  approvals/questions untouched. It deletes the state file when the plugin is disposed
  (DSH shutdown), which is the HUD's cue to exit.
- Approvals that DSH auto-approves never flash on the card: the Host waits 600 ms
  (questions: 300 ms) before showing the "waiting for you" state and cancels it if the request
  settles first.
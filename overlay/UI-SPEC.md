# HUD window — UI spec

Target: Windows 11 25H2, .NET Framework 4.8 WPF, compiled with
`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (no SDK, GAC references only).
Window: `WindowStyle=None`, `AllowsTransparency=True`, `Background=Transparent`,
`Topmost=True`, `ShowInTaskbar=False`, `ResizeMode=NoResize`, `ShowActivated=False`.
Primary screen work area (1707×1067 DIPs @150% DPI in the current setup); always bottom-right,
16 DIP margin from the work-area edge, unless a saved position exists.

## Global look

- Dark, DSH-consistent palette (ARGB):
  - card background `#F21B1B1E` (near-black, 95% opaque), border `#22FFFFFF`
  - pill background `#E61B1B1E`, border `#18FFFFFF`
  - title text `#F9FAFB` (15 px, `Microsoft YaHei UI`, semibold)
  - secondary/detail text `#A0A6AD` (12 px)
  - accent blue `#7AAAFF` (running dot, question), amber `#F59E0B` (attention), green `#34D399` (done), red `#F87171` (error)
- Rounded corners: card 14 DIP, pill 20 DIP (pill height 40, so fully rounded).
- Soft drop shadow: `DropShadowEffect` blur 24, opacity 0.45, shadow depth 0.
- Card size: 320×72 DIP. Pill size: 132×40 DIP. Size changes animate over 180 ms
  (`CubicEase`, `EaseOut`) including window `Width`/`Height` (window itself is exactly the
  content size, plus 12 DIP transparent padding for the shadow on every side).
- Window opacity 0.96; on hover border opacity rises to 0.45; idle pill opacity 0.8.
- Fonts: `Microsoft YaHei UI` with `Segoe UI` fallback. No external images or icon fonts:
  the status glyph is drawn with geometry/text (●, ✓, !) or simple `Ellipse`/`Path`.

## Layout per state

Common: left status glyph (16 DIP circle / check / bang), then a vertical stack:
line 1 = session title (ellipsis at 320 width), line 2 = state label. Right side may hold a
small dot; keep it minimal.

| mode | window size | glyph | line 2 | extra |
|---|---|---|---|---|
| idle | 132×40 pill | grey `●` | `DSH 空闲` (single line, centred, 12.5 px) | no shadow pulse; tooltip "点击回到 DSH" |
| running | 320×72 | blue `●` with breathing opacity 1.0↔0.45, 1.6 s loop | `执行中` | 2 px bottom shimmer bar (blue gradient, animating from left to right, 2.2 s loop) |
| attention | 320×72 | amber `●` (breathing) | `需要你授权` | amber border `#F59E0B` opacity pulse 0.45↔1.0 (1.4 s), outer glow (DropShadowEffect with amber `ShadowDepth=0`, blur 22, opacity pulse 0.35↔0.8). Line 2 is emphasised (15 px, amber) |
| question | 320×72 | blue `●` | `需要你回答` | blue border pulse like attention but `#7AAAFF`, softer (0.35↔0.8) |
| done | 320×72 | green `✓` | `任务完成` | green border flash once (0.0→0.9→0.35 over 900 ms), then steady at 0.25 |
| error | 320×72 | red `!` | `出错了` | red border steady 0.55, no pulse |

Title: normally the Host sends the real session title (from `session/title` events). It may be
empty or the placeholder `DeepSeek Harness` before a session gets its first title; in that case
line 1 shows the **DSH main window title** with its `" — DeepSeek Harness"` suffix stripped (see
Behaviour → "DSH window title"); if that fails, show `DSH`. If `detail` is non-empty and differs
from the label above, `detail` wins for line 2 (Host sends localized copy).

## Behaviour

- Left click (mouse down, moved < 4 DIP, up) → focus DSH: find the main Electron window
  (process `DeepSeek Harness`, fallback: `Chrome_WidgetWin_1` top-level window whose title is
  non-empty) with `EnumWindows`; `ShowWindow(SW_RESTORE)` if minimised; `SetForegroundWindow`.
  Repeat the click does not toggle anything visible.
- Drag: mouse down + move > 4 DIP → `DragMove`; on release persist position.
  Position file: `%LOCALAPPDATA%\dsh-desktop-hud\window-v1.json` → `{"x":123.5,"y":456.5}`.
  Clamp into the primary work area on startup (and when the saved position is off-screen).
- Right click → context menu (dark styled, no checkmarks): `回到 DSH` (focus), `隐藏 30 分钟`
  (window hides, timer re-shows current state), `退出小窗` (send `{"t":"bye"}`, close, exit 0).
- Do not steal focus when appearing (`ShowActivated=False`, `Show()` not `ShowDialog`);
  `SetWindowPos(HWND_TOPMOST)` is already implied by `Topmost`.
- DSH window title (fallback title source): cache the DSH main window title, refresh every
  ~2 s and on each `state` message; strip a trailing `" — DeepSeek Harness"` (also accept
  `" - DeepSeek Harness"`). Reuse the same window lookup as the click-to-focus behaviour.
- When `quit` arrives or the pipe closes and reconnect is hopeless, exit gracefully.
- Multi-monitor: primary screen only (SystemParameters.WorkArea).

## Multi-task layout (protocol v3)

The state file may carry `v: 3` and a `tasks` array (see `PROTOCOL.md`). `v` missing or `< 3`
keeps the legacy single-card rendering and `tasks` is ignored.

- Collapsed: identical to the single-task card (320×72 DIP content, 344×96 DIP window), plus a
  task-count badge and a chevron in the card header's right side:
  - badge: 16 DIP tall pill (corner 8), background `#2E7AAAFF`, text `#7AAAFF` 10.5 px semibold,
    shows the count, `9+` above nine; visible when `tasks.length >= 2`; hidden otherwise
    (single task and idle look exactly like the previous version);
  - chevron: 24×24 DIP hit area holding a 16×16 path; points down when collapsed, up when expanded;
    hover brightens; tooltip "展开全部 N 个任务" / "收起".
- Expanded: the card grows **upwards** (bottom edge fixed, width unchanged) with a 180 ms
  `CubicEase` height animation. Rows are built in code into a bottom-anchored stack: status dot
  (6–8 DIP), title (`TextTrimming=CharacterEllipsis`, one line), right-aligned status text
  (12 px, coloured by status); ~28 DIP per row with a 1 px `#14FFFFFF` separator, row hover
  `#0AFFFFFF`. At most 4 rows are listed; when `tasks.length > 4` the last row is a summary
  "+N 个任务" (12 px, secondary colour). Measured window heights: 3 tasks → 178 DIP (267 px
  @150%), 6 tasks → 246 DIP (369 px @150%).
- Accent aggregation: card border, outer glow and header dot follow the **highest-priority task
  in the list** (attention > question > error > running > done), so a background approval is
  visible even while the picked (collapsed) row is "执行中". The collapsed card's line 2 stays
  neutral grey in that case (it must not claim the picked row needs attention); it is only
  tinted when the *picked* row itself is attention/question.
- Row / card click → focus DSH (same hit-testing as before). Drag still works in both states.
- Heartbeats that do not change mode/title/detail/session **or the task list** must not re-render
  or restart animations; the task list is compared row by row.
- Debug flags: `--expanded` (start expanded), `--demo-multi` (cycles 5 multi-task scenes,
  2.5 s each: 2 tasks, 3 tasks with attention, 6 tasks, title/long-title, done/error mix).

## Verification

`HUD.exe --demo` must cycle all six states (2.5 s each) with no pipe. `--demo --state=...`
freezes one; `--demo-multi` cycles the multi-task scenes. A PowerShell screenshot of the desktop
region must show the window above the maximised DSH window (set `SetProcessDPIAware()` before
`CopyFromScreen`, or the layered window is missed). Keep the build script at `overlay/build.ps1`
(`-Out <path>` for a scratch binary so a running `bin\HUD.exe` is not locked), output
`overlay/bin/HUD.exe`; run it with `-NoProfile -ExecutionPolicy Bypass`.
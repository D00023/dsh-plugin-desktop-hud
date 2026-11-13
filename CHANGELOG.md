# Changelog

All notable changes to **dsh-plugin-desktop-hud** are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project uses
[Semantic Versioning](https://semver.org/).

## [1.0.2] — 2026-11-13

### Fixed

- **The card could fail to appear at all after a DSH start.** When DSH started, the plugin often
  had nothing to publish yet (no live agents were discoverable), and `writeState()` was a no-op
  until the first publish. The overlay therefore booted against the previous run's `state.json`,
  saw a stale mtime (10 s limit), exited with code 0, and after five respawns the bridge gave up
  permanently — later sessions could not bring the window back until DSH was restarted.
  The bridge now always writes a placeholder `v3 / idle` state before spawning and starts the
  2 s heartbeat from `start()`/`revive()`, so the file cannot go stale while the Host is alive.
- **Respawn exhaustion is no longer permanent.** After the respawn budget the bridge schedules a
  retry with exponential backoff (20 s, ×1.5, capped at 60 s) instead of stopping the supervisor.
- **Duplicate-instance exits (42) are retried too.** A 42 is almost always a zombie overlay from
  the previous Host run that still holds the mutex; the bridge now retries the takeover after
  10 s (doubling up to 60 s) instead of abandoning the window for the rest of the session.
- **Overlay boot tolerance.** `HUD.exe` now tolerates a stale file at startup: it logs
  `state file stale at startup age=…s -> waiting up to 5.0s` and only fails after that grace,
  in case the Host is still writing its first state.

### Changed

- Bridge test suite grew from 8 to 10 cases (startup placeholder, retry-after-give-up). The
  duplicate-exit test now asserts a delayed takeover rather than a permanent stop.
- `HUD.exe` rebuilt (`80,896` → `81,408` bytes); behaviour otherwise unchanged.

## [1.0.1] — 2026-11-12

### Fixed

- **Two identical cards could appear at the same time.** DSH was observed applying the bundle
  twice inside one Host lifetime (`hud.log`: two `starting desktop HUD bridge` lines 2.4 s apart),
  which spawned two `HUD.exe` processes. Two independent guards now make that impossible:
  - the host half owns a single bridge per Host realm — a duplicate `apply()` logs
    `duplicate apply detected` (with its stack) and reuses the live bridge (`bridge.revive()`)
    instead of starting a second state machine;
  - `HUD.exe` holds the named mutex `Local\dsh-desktop-hud-<sha1(state-file)[0..8]>` before
    creating the window. A second overlay for the same state file exits with code **42**
    without drawing; the bridge treats 42 as "another instance owns the window" and retries the
    takeover later. Demo mode (`--demo`, `--demo-multi`) never takes the mutex.
- **Startup respawn loop.** The overlay could be spawned before the first state publish, so it
  booted against a stale `state.json` from a previous run, exited immediately, and burned the
  five-respawn budget. The plugin now seeds/publishes state *before* spawning the overlay.

### Changed

- `hud-bridge.js` gained `revive()` (restart a stopped overlay without creating a second bridge)
  and duplicate-instance (exit code 42) handling.
- Bridge test suite grew from 6 to 8 cases (duplicate exit, revive).

## [1.0.0] — 2026-11-09

- First public release: six-state HUD, multi-session task list with expand/collapse, click-to-focus,
  drag with position memory, tray-free always-on-top WPF overlay, zero-dependency Cordis host half.

[1.0.2]: https://github.com/D00023/dsh-plugin-desktop-hud/releases/tag/v1.0.2
[1.0.1]: https://github.com/D00023/dsh-plugin-desktop-hud/releases/tag/v1.0.1
[1.0.0]: https://github.com/D00023/dsh-plugin-desktop-hud/releases/tag/v1.0.0
# Changelog

All notable changes to **dsh-plugin-desktop-hud** are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project uses
[Semantic Versioning](https://semver.org/).

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
    without drawing; the bridge treats 42 as "another instance owns the window" and does not
    respawn it. Demo mode (`--demo`, `--demo-multi`) never takes the mutex.
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

[1.0.1]: https://github.com/D00023/dsh-plugin-desktop-hud/releases/tag/v1.0.1
[1.0.0]: https://github.com/D00023/dsh-plugin-desktop-hud/releases/tag/v1.0.0
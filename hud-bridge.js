/**
 * State-file bridge + HUD process supervisor.
 *
 * Owns: the state file the overlay tail-polls, the overlay child process
 * (spawn / respawn / kill), the atomic state writes with a heartbeat, and
 * shutdown. Transport is a single UTF-8 JSON file so it works across the
 * sandbox boundary (a child spawned by the Host cannot connect to a named pipe
 * owned by the Host process in this environment).
 *
 * Pure Node built-ins, no Cordis imports, so it can be exercised by
 * `test/bridge.test.mjs` and by a fake-Cordis harness.
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';

const DEFAULT_EXE = path.join('overlay', 'bin', 'HUD.exe');
/** Rewrite the state file this often so the overlay can treat an old mtime as "Host is gone". */
const HEARTBEAT_MS = 2_000;
/** The overlay may hold the file open for a moment while reading; retry the swap. */
const WRITE_ATTEMPTS = 4;
const WRITE_RETRY_MS = 25;
/** Give the overlay a moment to notice the deleted file before killing it. */
const QUIT_GRACE_MS = 800;
const RESPAWN_DELAY_MS = 2_000;
const MAX_RESPAWNS = 5;
/** HUD.exe exits with this code when another instance already owns the window (named mutex). */
const DUPLICATE_EXIT_CODE = 42;
/** While the executable is still missing (first-run/dev), retry rather than give up. */
const MISSING_EXE_RETRY_MS = 10_000;
/**
 * After the respawn budget is exhausted the bridge keeps retrying with a growing
 * backoff instead of stopping forever: a permanently dead supervisor is worse
 * than a few extra attempts, and real state changes may recover the overlay later.
 */
const GIVE_UP_RETRY_MS = 20_000;
const GIVE_UP_RETRY_MAX_MS = 60_000;
/**
 * A duplicate-instance exit (42) is usually a zombie overlay from the previous
 * Host run that still holds the mutex for a few seconds. Retry the takeover
 * instead of surrendering the window for the rest of the session.
 */
const DUPLICATE_RETRY_MS = 10_000;

/** Blocking sleep for the (rare) transient rename retry; keeps publish synchronous. */
function sleepSync(ms) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

function isTransientWriteError(error) {
  const code = error && error.code;
  return code === 'EPERM' || code === 'EACCES' || code === 'EBUSY';
}

function defaultStateDir() {
  const localAppData = process.env.LOCALAPPDATA;
  if (localAppData) return path.join(localAppData, 'dsh-desktop-hud');
  return path.join(os.homedir(), 'AppData', 'Local', 'dsh-desktop-hud');
}

const TASK_STATUSES = new Set(['running', 'attention', 'question', 'done', 'error']);

/** Keep only the fields the overlay knows and lock their types down. */
function visibleTask(task) {
  if (!task || typeof task !== 'object') return null;
  const status = TASK_STATUSES.has(task.status) ? task.status : 'running';
  return {
    session: String(task.session ?? ''),
    title: String(task.title ?? ''),
    status,
    detail: String(task.detail ?? ''),
    since: Number(task.since ?? 0),
  };
}

/** The subset of a state message the overlay reads; `t` and anything else is Host-only. */
function visibleState(message) {
  if (!message || typeof message !== 'object') return null;
  return {
    v: Number(message.v ?? 2),
    mode: String(message.mode ?? 'idle'),
    title: String(message.title ?? ''),
    detail: String(message.detail ?? ''),
    session: String(message.session ?? ''),
    since: Number(message.since ?? Date.now()),
    tasks: Array.isArray(message.tasks) ? message.tasks.map(visibleTask).filter(Boolean) : [],
    ts: Date.now(),
  };
}

export function createHudBridge({
  exePath,
  stateFile,
  logger = console,
  onUserQuit,
  spawnImpl = spawn,
  heartbeatMs = HEARTBEAT_MS,
  quitGraceMs = QUIT_GRACE_MS,
  respawnDelayMs = RESPAWN_DELAY_MS,
  maxRespawns = MAX_RESPAWNS,
  giveUpRetryMs = GIVE_UP_RETRY_MS,
  giveUpRetryMaxMs = GIVE_UP_RETRY_MAX_MS,
  duplicateRetryMs = DUPLICATE_RETRY_MS,
} = {}) {
  if (!exePath) exePath = DEFAULT_EXE;
  if (!stateFile) stateFile = process.env.DSH_HUD_STATE_FILE || path.join(defaultStateDir(), 'state.json');
  const stateDir = path.dirname(stateFile);
  const byeFile = path.join(stateDir, 'hud-bye-v1.json');

  const log = {
    info: (...args) => logger.info?.('[desktop-hud]', ...args),
    warn: (...args) => logger.warn?.('[desktop-hud]', ...args),
  };

  let lastVisible = null;
  let lastSerialized = '';
  let proc = null;
  let spawnAt = 0;
  let disposed = false;
  let userQuit = false;
  let respawns = 0;
  let respawnTimer = null;
  let giveUpDelay = giveUpRetryMs;
  let duplicateDelay = duplicateRetryMs;
  let heartbeatTimer = null;
  let everSpawned = false;

  /**
   * A minimal idle payload written before the first real publish. Without it the
   * first spawn could read a stale state file left by the previous Host run and
   * exit immediately (that is exactly the "no HUD after DSH start" bug).
   */
  function placeholderState() {
    return {
      v: 3,
      mode: 'idle',
      title: 'DeepSeek Harness',
      detail: '',
      session: '',
      since: Date.now(),
      tasks: [],
    };
  }

  function ensureDir() {
    try {
      fs.mkdirSync(stateDir, { recursive: true });
      return true;
    } catch (error) {
      log.warn('cannot create state dir:', stateDir, error && error.message);
      return false;
    }
  }

  /**
   * Replace the state file with `tmp` + rename. The overlay may hold the file
   * open while polling, and Windows refuses to rename over a handle that was not
   * opened with FILE_SHARE_DELETE, so retry briefly and then fall back to an
   * in-place rewrite (the overlay ignores a half-written line).
   */
  function writeState() {
    if (!lastVisible) lastVisible = placeholderState();
    const payload = { ...lastVisible, ts: Date.now() };
    const serialized = `${JSON.stringify(payload)}\n`;
    const tmpFile = `${stateFile}.tmp`;
    ensureDir();
    let lastError = null;
    for (let attempt = 0; attempt < WRITE_ATTEMPTS; attempt += 1) {
      try {
        fs.writeFileSync(tmpFile, serialized, 'utf8');
        fs.renameSync(tmpFile, stateFile);
        return true;
      } catch (error) {
        lastError = error;
        if (!isTransientWriteError(error)) break;
        sleepSync(WRITE_RETRY_MS);
      }
    }
    removeQuietly(tmpFile);
    if (lastError && isTransientWriteError(lastError)) {
      try {
        fs.writeFileSync(stateFile, serialized, 'utf8');
        return true;
      } catch (error) {
        lastError = error;
      }
    }
    log.warn('cannot write state file:', lastError && lastError.message);
    return false;
  }

  function removeQuietly(file) {
    try {
      fs.rmSync(file, { force: true });
    } catch {
      // ignore
    }
  }

  function readByeTs() {
    try {
      const raw = fs.readFileSync(byeFile, 'utf8');
      const parsed = JSON.parse(raw);
      const ts = Number(parsed && parsed.ts);
      return Number.isFinite(ts) ? ts : 0;
    } catch {
      return 0;
    }
  }

  function startHeartbeat() {
    if (heartbeatTimer) return;
    heartbeatTimer = setInterval(() => {
      if (disposed) return;
      writeState();
    }, heartbeatMs);
    heartbeatTimer.unref?.();
  }

  /**
   * Publish a state message (shape: `{ mode, title, detail, session, since }`,
   * see PROTOCOL.md). Returns true when the visible payload changed.
   */
  function publish(message) {
    const visible = visibleState(message);
    if (!visible) return false;
    const serialized = JSON.stringify({ ...visible, ts: 0 });
    const changed = serialized !== lastSerialized;
    lastVisible = visible;
    lastSerialized = serialized;
    writeState();
    startHeartbeat();
    return changed;
  }

  /** Re-paint the current state (used right after the overlay (re)starts). */
  function resend() {
    return writeState();
  }

  function killOverlay() {
    if (!proc) return;
    const child = proc;
    proc = null;
    try {
      child.kill();
    } catch {
      // already gone
    }
  }

  function onOverlayExit(code, signal) {
    proc = null;
    if (disposed) return;
    // The overlay writes this file only when the user picks "quit" from its menu.
    const byeTs = readByeTs();
    if (byeTs && byeTs >= spawnAt) {
      userQuit = true;
      log.info('user closed the overlay for this session');
      removeQuietly(stateFile);
      removeQuietly(byeFile);
      onUserQuit?.();
      return;
    }
    if (code === DUPLICATE_EXIT_CODE) {
      // HUD.exe holds a named mutex per state file; this means another overlay
      // already owns the window. Never fight it with a second window, but do
      // retry later: a zombie overlay from the previous Host run releases the
      // mutex as soon as its state file disappears.
      log.info('another HUD instance already owns the window; will retry later');
      const delay = duplicateDelay;
      duplicateDelay = Math.min(giveUpRetryMaxMs, Math.round(duplicateDelay * 2));
      respawnTimer = setTimeout(spawnOverlay, delay);
      respawnTimer.unref?.();
      return;
    }
    if (respawns >= maxRespawns) {
      respawns = 0;
      const delay = giveUpDelay;
      giveUpDelay = Math.min(giveUpRetryMaxMs, Math.round(giveUpDelay * 1.5));
      log.warn(
        `overlay keeps exiting (${code ?? signal}); retrying in ${Math.round(delay / 1000)}s`,
      );
      respawnTimer = setTimeout(spawnOverlay, delay);
      respawnTimer.unref?.();
      return;
    }
    respawns += 1;
    log.warn(`overlay exited (${code ?? signal}); respawn ${respawns}/${maxRespawns}`);
    respawnTimer = setTimeout(spawnOverlay, respawnDelayMs);
    respawnTimer.unref?.();
  }

  function spawnOverlay() {
    if (disposed || userQuit || proc) return;
    if (!fs.existsSync(exePath)) {
      log.warn('overlay executable missing; retrying:', exePath);
      respawnTimer = setTimeout(spawnOverlay, MISSING_EXE_RETRY_MS);
      respawnTimer.unref?.();
      return;
    }
    try {
      spawnAt = Date.now();
      const child = spawnImpl(exePath, ['--state-file', stateFile], {
        stdio: 'ignore',
        windowsHide: true,
      });
      proc = child;
      everSpawned = true;
      child.on('error', (error) => log.warn('overlay spawn failed:', error && error.message));
      child.on('exit', onOverlayExit);
      log.info('overlay started', `pid=${child.pid}`);
      writeState();
    } catch (error) {
      log.warn('overlay spawn threw:', error && error.message);
    }
  }

  /** Write a first state (if known), forget any stale bye file, then start the overlay. */
  function start() {
    if (disposed) return;
    ensureDir();
    removeQuietly(byeFile);
    giveUpDelay = giveUpRetryMs;
    duplicateDelay = duplicateRetryMs;
    writeState();
    // The heartbeat must run even before the first publish: otherwise an idle
    // startup (no session events yet) lets the file go stale while the overlay
    // is alive, and every spawned HUD exits after the stale limit.
    startHeartbeat();
    spawnOverlay();
  }

  /**
   * Make sure the overlay is alive again. Used by a duplicate bundle apply, which
   * must reuse this bridge instead of spawning a second one. Returns true when it
   * spawned an overlay.
   */
  function revive() {
    if (disposed || userQuit) return false;
    if (proc) {
      resend();
      return false;
    }
    if (respawnTimer) clearTimeout(respawnTimer);
    respawnTimer = null;
    respawns = 0;
    giveUpDelay = giveUpRetryMs;
    duplicateDelay = duplicateRetryMs;
    removeQuietly(byeFile);
    writeState();
    startHeartbeat();
    spawnOverlay();
    return true;
  }

  /** Delete the state file (the overlay exits when it disappears), then kill it. */
  function dispose() {
    if (disposed) return;
    disposed = true;
    if (respawnTimer) clearTimeout(respawnTimer);
    respawnTimer = null;
    if (heartbeatTimer) clearInterval(heartbeatTimer);
    heartbeatTimer = null;
    removeQuietly(`${stateFile}.tmp`);
    removeQuietly(stateFile);
    if (proc) {
      const child = proc;
      proc = null;
      const timer = setTimeout(() => {
        try {
          child.kill();
        } catch {
          // ignore
        }
      }, quitGraceMs);
      timer.unref?.();
    }
  }

  return {
    stateFile,
    byeFile,
    publish,
    resend,
    dispose,
    start,
    revive,
    killOverlay,
    get exitedByUser() {
      return userQuit;
    },
    get overlayRunning() {
      return Boolean(proc);
    },
    get spawnedOnce() {
      return everSpawned;
    },
  };
}

export default createHudBridge;
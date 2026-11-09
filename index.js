/**
 * Host half of the DSH desktop HUD bundle.
 *
 * Observes agent activity (running/idle, approval requests, user questions,
 * errors) and mirrors it onto the always-on-top Windows card in
 * `overlay/bin/HUD.exe` by atomically rewriting a small JSON state file that
 * the overlay polls. Purely observational: every waterfall listener delegates
 * with `next()` and never claims an approval or a question, so DSH's own flow
 * is unchanged even when the overlay is missing.
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { HudState } from './hud-state.js';
import { createHudBridge } from './hud-bridge.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const OVERLAY_EXE = path.join(HERE, 'overlay', 'bin', 'HUD.exe');

/** Small rolling log so the HUD lifecycle can be diagnosed without a console. */
const LOG_DIR = path.join(
  process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'),
  'dsh-desktop-hud',
);
const LOG_FILE = path.join(LOG_DIR, 'hud.log');
const MAX_LOG_BYTES = 256 * 1024;

function describe(value) {
  if (value instanceof Error) return value.message;
  if (typeof value === 'string') return value;
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

function createLogger(base) {
  const emit = (level, args) => {
    const target = base && base[level];
    try {
      if (typeof target === 'function') target.apply(base, args);
    } catch {
      // never let logging break the plugin
    }
    try {
      fs.mkdirSync(LOG_DIR, { recursive: true });
      try {
        if (fs.statSync(LOG_FILE).size > MAX_LOG_BYTES) fs.renameSync(LOG_FILE, `${LOG_FILE}.1`);
      } catch {
        // no previous log
      }
      const line = `${new Date().toISOString()} [${level}] ${args.map(describe).join(' ')}\n`;
      fs.appendFileSync(LOG_FILE, line);
    } catch {
      // a read-only profile must not break the HUD
    }
  };
  return {
    info: (...args) => emit('info', args),
    warn: (...args) => emit('warn', args),
    error: (...args) => emit('error', args),
  };
}

/** Wait briefly before raising an approval alert: auto-approved requests never flash the card. */
const APPROVAL_DELAY_MS = 600;
/** Questions are genuinely blocking; only debounce the burst a little. */
const QUESTION_DELAY_MS = 300;
/** Guard against a pathological parent-session chain. */
const MAX_TITLE_HOPS = 5;

export function apply(ctx) {
  const log = createLogger(ctx.logger ?? console);
  const state = new HudState();
  const bridge = createHudBridge({
    exePath: OVERLAY_EXE,
    logger: log,
    onUserQuit: () => log.info('overlay closed by the user; not respawning this session'),
  });
  let counter = 0;
  let expiryTimer = null;

  function service(name) {
    try {
      return ctx.get?.(name);
    } catch {
      return undefined;
    }
  }

  function sessionById(id) {
    const sessions = service('sessions');
    if (!sessions || !id) return null;
    try {
      return sessions.get(id) || null;
    } catch {
      return null;
    }
  }

  /**
   * Walk up from an agent's session to its root session. Approvals/questions
   * raised inside a subagent still belong to the top-level task the user sees,
   * so the HUD maps them onto the root card instead of inventing a new row.
   */
  function rootSessionOf(agent) {
    let session = agent && agent.session;
    let id = sessionIdOf(agent);
    for (let hop = 0; session && hop < MAX_TITLE_HOPS; hop += 1) {
      const header = session.header || {};
      if (header.origin !== 'subagent') break;
      const parentId = header.parentSession;
      if (!parentId) break;
      const parent = sessionById(parentId);
      if (!parent) break;
      session = parent;
      id = String(parentId);
    }
    return { id, session };
  }

  /** Resolve the human session title of the root session behind an agent. */
  function titleOf(agent) {
    const titles = service('sessionTitle');
    if (!titles) return '';
    const root = rootSessionOf(agent);
    const session = root.session || sessionById(root.id);
    if (!session && !root.id) return '';
    try {
      const snapshot = titles.get(session || root.id);
      if (snapshot && snapshot.title) return String(snapshot.title);
    } catch {
      return '';
    }
    return '';
  }

  function sessionIdOf(agent) {
    const id = agent && agent.id;
    return id === undefined || id === null ? '' : String(id);
  }

  function isSubagent(agent) {
    return Boolean(agent && agent.session && agent.session.header && agent.session.header.origin === 'subagent');
  }

  function publish() {
    try {
      bridge.publish(state.message());
    } catch (error) {
      log.warn('publish failed:', error && error.message);
    }
    scheduleExpiry();
  }

  /** Re-publish when the oldest `done` task falls out of its 15 s window. */
  function scheduleExpiry() {
    if (expiryTimer) {
      clearTimeout(expiryTimer);
      expiryTimer = null;
    }
    const remaining = state.nextDoneExpiry();
    if (remaining === null) return;
    expiryTimer = setTimeout(() => {
      expiryTimer = null;
      try {
        bridge.publish(state.message());
      } catch (error) {
        log.warn('done expiry failed:', error && error.message);
      }
      scheduleExpiry();
    }, Math.max(50, remaining + 50));
    expiryTimer.unref?.();
  }

  const offStatus = ctx.on('agent/status', (payload) => {
    try {
      if (!payload || !payload.agent) return;
      // Subagents are internal work: the root agent's own status already covers them.
      if (isSubagent(payload.agent)) return;
      state.setTitle(sessionIdOf(payload.agent), titleOf(payload.agent));
      state.apply({ type: 'agent-status', sessionId: sessionIdOf(payload.agent), running: payload.status === 'running' });
      publish();
    } catch (error) {
      log.warn('agent/status handler failed:', error && error.message);
    }
  });

  const offApproval = ctx.on('approval/request', (req, next) => {
    // Delegate IMMEDIATELY: the returned promise is the human decision, and any
    // slow work before this would delay the prompt.
    const pending = next();
    const id = `approval:${req && req.callId ? String(req.callId) : `n${++counter}`}`;
    const sessionId = rootSessionOf(req && req.agent).id;
    const title = titleOf(req && req.agent);
    let shown = false;
    const timer = setTimeout(() => {
      shown = true;
      try {
        state.apply({ type: 'approval-pending', id, sessionId, title });
        publish();
      } catch (error) {
        log.warn('approval alert failed:', error && error.message);
      }
    }, APPROVAL_DELAY_MS);
    timer.unref?.();
    const settle = () => {
      clearTimeout(timer);
      if (!shown) return;
      try {
        state.apply({ type: 'approval-settled', id });
        publish();
      } catch (error) {
        log.warn('approval settle failed:', error && error.message);
      }
    };
    pending.then(settle, settle);
    return pending;
  });

  const offQuestion = ctx.on('user-questions/request', (req, next) => {
    const pending = next();
    const callId = req && req.wait && req.wait.callId ? String(req.wait.callId) : `n${++counter}`;
    const id = `question:${callId}`;
    const sessionId = rootSessionOf(req && req.agent).id;
    const title = titleOf(req && req.agent);
    let shown = false;
    const timer = setTimeout(() => {
      shown = true;
      try {
        state.apply({ type: 'question-pending', id, sessionId, title });
        publish();
      } catch (error) {
        log.warn('question alert failed:', error && error.message);
      }
    }, QUESTION_DELAY_MS);
    timer.unref?.();
    const settle = () => {
      clearTimeout(timer);
      if (!shown) return;
      try {
        state.apply({ type: 'question-settled', id });
        publish();
      } catch (error) {
        log.warn('question settle failed:', error && error.message);
      }
    };
    pending.then(settle, settle);
    return pending;
  });

  const offError = ctx.on('agent/error', (payload) => {
    try {
      if (!payload || !payload.agent || isSubagent(payload.agent)) return;
      state.apply({ type: 'agent-error', sessionId: sessionIdOf(payload.agent) });
      publish();
    } catch (error) {
      log.warn('agent/error handler failed:', error && error.message);
    }
  });

  const offSession = ctx.on('session/event', (session, event) => {
    try {
      if (!session || !event) return;
      // Session titles: the authoritative source for the card's first line.
      if (event.type === 'session/title') {
        const data = event.data;
        if (data && data.title) {
          state.apply({ type: 'title', sessionId: String(session.id), title: String(data.title) });
          publish();
        }
        return;
      }
      if (isSubagent({ session })) return;
      if (event.type === 'turn/end' && event.data && event.data.reason) {
        const reason = event.data.reason;
        // Turn boundary is the most reliable "task finished" signal.
        if (reason.kind === 'error') {
          state.apply({ type: 'agent-error', sessionId: String(session.id) });
        } else if (reason.kind === 'completed') {
          state.apply({ type: 'agent-status', sessionId: String(session.id), running: false });
        }
        publish();
      }
    } catch (error) {
      log.warn('session/event handler failed:', error && error.message);
    }
  });

  /** Paint the HUD with whatever is already true, so a restart does not leave it blank. */
  function seedFromLiveAgents() {
    const agents = service('agents');
    if (!agents || typeof agents.list !== 'function') return;
    try {
      for (const agent of agents.list()) {
        if (isSubagent(agent)) continue;
        const sessionId = sessionIdOf(agent);
        state.setTitle(sessionId, titleOf(agent));
        state.apply({ type: 'agent-status', sessionId, running: agent.status === 'running' });
      }
      publish();
    } catch (error) {
      log.warn('seeding from live agents failed:', error && error.message);
    }
  }

  ctx.effect(() => {
    log.info('starting desktop HUD bridge', bridge.stateFile);
    bridge.start();
    seedFromLiveAgents();

    return () => {
      if (expiryTimer) clearTimeout(expiryTimer);
      expiryTimer = null;
      offStatus?.();
      offApproval?.();
      offQuestion?.();
      offError?.();
      offSession?.();
      bridge.dispose();
    };
  });
}

export default apply;
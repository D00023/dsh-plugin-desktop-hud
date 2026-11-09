/**
 * Pure state machine for the DSH desktop HUD.
 *
 * The Host plugin feeds normalized events in; the result is a v3 snapshot that
 * lists every concurrent task plus the single task the collapsed card should
 * show. No timers and no Cordis imports live here, so the logic is testable
 * with plain Node.
 *
 * Event vocabulary (all plain objects):
 *   { type: 'agent-status', sessionId, title?, running: boolean }
 *   { type: 'approval-pending', id, sessionId, title? }
 *   { type: 'approval-settled', id }
 *   { type: 'question-pending', id, sessionId, title? }
 *   { type: 'question-settled', id }
 *   { type: 'agent-error', sessionId, title? }
 *   { type: 'title', sessionId, title }
 *
 * Session ids are always **root** sessions: the Host maps subagent activity onto
 * its parent before feeding events in.
 */

export const DONE_TTL_MS = 15_000;
export const STALE_SESSION_MS = 30 * 60 * 1000;
export const DEFAULT_TITLE = 'DeepSeek Harness';
/** Row titles are always shown (no window-title fallback per row). */
export const ROW_TITLE_FALLBACK = '未命名会话';

export const MODE_DETAIL = {
  running: '执行中',
  attention: '需要你授权',
  question: '需要你回答',
  done: '任务完成',
  error: '出错了',
  idle: '',
};

export const MODE_PRIORITY = ['attention', 'question', 'error', 'running', 'done', 'idle'];
/** Task list order: most-needs-attention first, older tasks behind. */
export const TASK_PRIORITY = ['attention', 'question', 'error', 'running', 'done'];
export const TASK_STATUSES = new Set(TASK_PRIORITY);

function emptySession(id) {
  return {
    id,
    title: '',
    running: false,
    lastActive: 0,
    runningAt: 0,
    doneAt: 0,
    error: false,
    errorAt: 0,
  };
}

function earliest(map, sessionId) {
  let best = 0;
  for (const entry of map.values()) {
    if (entry.sessionId !== sessionId) continue;
    if (!best || entry.at < best) best = entry.at;
  }
  return best;
}

export class HudState {
  constructor({ now = () => Date.now(), doneTtl = DONE_TTL_MS, staleAfter = STALE_SESSION_MS } = {}) {
    this.now = now;
    this.doneTtl = doneTtl;
    this.staleAfter = staleAfter;
    this.sessions = new Map();
    this.approvals = new Map();
    this.questions = new Map();
    this.mode = 'idle';
    this.since = this.now();
  }

  ensure(sessionId) {
    const key = sessionId || 'unknown';
    let session = this.sessions.get(key);
    if (!session) {
      session = emptySession(key);
      this.sessions.set(key, session);
    }
    return session;
  }

  setTitle(sessionId, title) {
    if (!title) return;
    const session = this.ensure(sessionId);
    session.title = String(title);
  }

  /** Main entry: returns the new snapshot. */
  apply(event) {
    switch (event && event.type) {
      case 'agent-status': {
        const session = this.ensure(event.sessionId);
        if (event.title) session.title = String(event.title);
        const at = this.now();
        session.lastActive = at;
        if (event.running) {
          const wasRunning = session.running;
          session.running = true;
          session.runningAt = wasRunning ? session.runningAt || at : at;
          session.doneAt = 0;
          session.error = false;
          session.errorAt = 0;
        } else if (session.running) {
          session.running = false;
          session.doneAt = at;
        }
        break;
      }
      case 'approval-pending':
        this.setTitle(event.sessionId, event.title);
        this.ensure(event.sessionId);
        this.approvals.set(event.id, { sessionId: event.sessionId || 'unknown', at: this.now() });
        break;
      case 'approval-settled':
        this.approvals.delete(event.id);
        break;
      case 'question-pending':
        this.setTitle(event.sessionId, event.title);
        this.ensure(event.sessionId);
        this.questions.set(event.id, { sessionId: event.sessionId || 'unknown', at: this.now() });
        break;
      case 'question-settled':
        this.questions.delete(event.id);
        break;
      case 'agent-error': {
        const session = this.ensure(event.sessionId);
        if (event.title) session.title = String(event.title);
        session.error = true;
        session.errorAt = this.now();
        session.doneAt = 0;
        break;
      }
      case 'title':
        this.setTitle(event.sessionId, event.title);
        break;
      default:
        // Unknown event: state unchanged.
        break;
    }
    return this.snapshot();
  }

  /** Clear finished-turn markers (manual/test use). */
  expireDone() {
    for (const session of this.sessions.values()) session.doneAt = 0;
    return this.snapshot();
  }

  /** Drop sessions that have been quiet for a long time and hold nothing pending. */
  _prune(now) {
    for (const [id, session] of this.sessions) {
      if (session.running || session.error) continue;
      if (earliest(this.approvals, id) || earliest(this.questions, id)) continue;
      if (session.lastActive && now - session.lastActive > this.staleAfter) this.sessions.delete(id);
    }
  }

  /** Resolve one session to its current task status (or null when it has nothing to show). */
  _statusOf(session, now) {
    const approvalAt = earliest(this.approvals, session.id);
    if (approvalAt) return { status: 'attention', since: approvalAt };
    const questionAt = earliest(this.questions, session.id);
    if (questionAt) return { status: 'question', since: questionAt };
    if (session.error) return { status: 'error', since: session.errorAt || now };
    if (session.running) return { status: 'running', since: session.runningAt || now };
    if (session.doneAt && now - session.doneAt <= this.doneTtl) {
      return { status: 'done', since: session.doneAt };
    }
    return null;
  }

  /** Ordered list of concurrent tasks for the expanded card. */
  tasks(now = this.now()) {
    this._prune(now);
    const list = [];
    for (const session of this.sessions.values()) {
      const resolved = this._statusOf(session, now);
      if (!resolved) continue;
      list.push({
        session: session.id,
        title: session.title || ROW_TITLE_FALLBACK,
        status: resolved.status,
        detail: MODE_DETAIL[resolved.status] || '',
        since: resolved.since,
      });
    }
    list.sort((a, b) => {
      const pa = TASK_PRIORITY.indexOf(a.status);
      const pb = TASK_PRIORITY.indexOf(b.status);
      if (pa !== pb) return pa - pb;
      return b.since - a.since;
    });
    return list;
  }

  /** The single task the collapsed card shows: first item of the ordered list. */
  resolve(now = this.now()) {
    const list = this.tasks(now);
    if (!list.length) return { mode: 'idle', sessionId: '', session: null, task: null };
    const task = list[0];
    return { mode: task.status, sessionId: task.session, session: this.sessions.get(task.session) || null, task };
  }

  snapshot(now = this.now()) {
    const resolved = this.resolve(now);
    if (resolved.mode !== this.mode) {
      this.mode = resolved.mode;
      this.since = now;
    }
    const session = resolved.session;
    const title = (session && session.title) || DEFAULT_TITLE;
    return {
      v: 3,
      mode: this.mode,
      title,
      detail: MODE_DETAIL[this.mode] || '',
      session: resolved.sessionId || '',
      since: this.since,
      tasks: this.tasks(now),
    };
  }

  /** Milliseconds until the next `done` task expires, or null when none is pending. */
  nextDoneExpiry(now = this.now()) {
    let soonest = null;
    for (const session of this.sessions.values()) {
      if (!session.doneAt) continue;
      if (session.running || session.error) continue;
      if (earliest(this.approvals, session.id) || earliest(this.questions, session.id)) continue;
      const left = session.doneAt + this.doneTtl - now;
      if (left <= 0) return 0;
      if (soonest === null || left < soonest) soonest = left;
    }
    return soonest;
  }

  /** Snapshot formatted as the bridge message the overlay understands. */
  message() {
    return { t: 'state', ...this.snapshot() };
  }
}

export default HudState;
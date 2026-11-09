/**
 * Plain-Node tests for the HUD state machine. Run with:
 *   node --test test/state.test.mjs    (or)   node test/state.test.mjs
 */
import assert from 'node:assert/strict';
import { HudState, MODE_DETAIL, DEFAULT_TITLE, DONE_TTL_MS } from '../hud-state.js';

let clock = 1_000_000;
const now = () => clock;
const step = (ms = 100) => {
  clock += ms;
};

function fresh() {
  clock = 1_000_000;
  return new HudState({ now });
}

const tests = [];
const test = (name, fn) => tests.push([name, fn]);

test('idle when nothing happened', () => {
  const hud = fresh();
  const snap = hud.snapshot();
  assert.equal(snap.v, 3);
  assert.equal(snap.mode, 'idle');
  assert.equal(snap.title, DEFAULT_TITLE);
  assert.equal(snap.detail, '');
  assert.deepEqual(snap.tasks, []);
});

test('running shows session title and 执行中', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 's1', title: '写测试', running: true });
  const snap = hud.snapshot();
  assert.equal(snap.mode, 'running');
  assert.equal(snap.title, '写测试');
  assert.equal(snap.detail, '执行中');
  assert.equal(snap.session, 's1');
  assert.equal(snap.since, clock);
  assert.deepEqual(snap.tasks, [
    { session: 's1', title: '写测试', status: 'running', detail: '执行中', since: clock },
  ]);
});

test('running -> done with 任务完成, then idle after 15s', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 's1', title: '写测试', running: true });
  step(500);
  hud.apply({ type: 'agent-status', sessionId: 's1', running: false });
  let snap = hud.snapshot();
  assert.equal(snap.mode, 'done');
  assert.equal(snap.detail, '任务完成');
  assert.equal(snap.title, '写测试');
  assert.equal(snap.tasks.length, 1);
  assert.equal(snap.tasks[0].status, 'done');
  step(DONE_TTL_MS - 2);
  assert.equal(hud.snapshot().mode, 'done');
  step(3);
  snap = hud.snapshot();
  assert.equal(snap.mode, 'idle');
  assert.deepEqual(snap.tasks, []);
});

test('idle without prior run does not produce done', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 's1', running: false });
  assert.equal(hud.snapshot().mode, 'idle');
});

test('approval outranks running and shows attention detail', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 's1', title: '任务A', running: true });
  step(100);
  hud.apply({ type: 'approval-pending', id: 'a1', sessionId: 's1' });
  const snap = hud.snapshot();
  assert.equal(snap.mode, 'attention');
  assert.equal(snap.detail, MODE_DETAIL.attention);
  assert.equal(snap.title, '任务A');
  assert.equal(snap.tasks[0].status, 'attention');
});

test('approval settled restores running', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 's1', running: true });
  hud.apply({ type: 'approval-pending', id: 'a1', sessionId: 's1' });
  hud.apply({ type: 'approval-settled', id: 'a1' });
  assert.equal(hud.snapshot().mode, 'running');
});

test('question outranks running but not approval', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 's1', running: true });
  hud.apply({ type: 'question-pending', id: 'q1', sessionId: 's1' });
  assert.equal(hud.snapshot().mode, 'question');
  hud.apply({ type: 'approval-pending', id: 'a1', sessionId: 's1' });
  assert.equal(hud.snapshot().mode, 'attention');
});

test('error outranks done and is cleared by next run', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 's1', running: true });
  hud.apply({ type: 'agent-status', sessionId: 's1', running: false });
  hud.apply({ type: 'agent-error', sessionId: 's1' });
  assert.equal(hud.snapshot().mode, 'error');
  hud.apply({ type: 'agent-status', sessionId: 's1', running: true });
  assert.equal(hud.snapshot().mode, 'running');
  hud.apply({ type: 'agent-status', sessionId: 's1', running: false });
  assert.equal(hud.snapshot().mode, 'done');
});

test('multi-session: attention wins, then newest running', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 'a', title: 'A', running: true });
  step(100);
  hud.apply({ type: 'agent-status', sessionId: 'b', title: 'B', running: true });
  assert.equal(hud.snapshot().title, 'B');
  const two = hud.snapshot().tasks;
  assert.deepEqual(two.map((t) => t.session), ['b', 'a']); // newest running first
  hud.apply({ type: 'approval-pending', id: 'a1', sessionId: 'a' });
  const snap = hud.snapshot();
  assert.equal(snap.mode, 'attention');
  assert.equal(snap.title, 'A');
  assert.deepEqual(snap.tasks.map((t) => [t.session, t.status]), [['a', 'attention'], ['b', 'running']]);
  hud.apply({ type: 'approval-settled', id: 'a1' });
  assert.equal(hud.snapshot().title, 'B');
});

test('task list sorts by priority then newest since', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 'r-old', title: 'old running', running: true });
  step(100);
  hud.apply({ type: 'agent-status', sessionId: 'r-new', title: 'new running', running: true });
  step(100);
  hud.apply({ type: 'agent-status', sessionId: 'err', title: 'failed', running: false });
  step(50);
  hud.apply({ type: 'agent-status', sessionId: 'err', running: true });
  hud.apply({ type: 'agent-error', sessionId: 'err' });
  step(100);
  hud.apply({ type: 'question-pending', id: 'q', sessionId: 'r-old' });
  const tasks = hud.snapshot().tasks;
  assert.deepEqual(tasks.map((t) => t.status), ['question', 'error', 'running']);
  assert.deepEqual(tasks.map((t) => t.session), ['r-old', 'err', 'r-new']);
});

test('six concurrent sessions are all listed', () => {
  const hud = fresh();
  for (let i = 0; i < 6; i += 1) {
    step(10);
    hud.apply({ type: 'agent-status', sessionId: `s${i}`, title: `任务${i}`, running: true });
  }
  const tasks = hud.snapshot().tasks;
  assert.equal(tasks.length, 6);
  assert.deepEqual(tasks.map((t) => t.session), ['s5', 's4', 's3', 's2', 's1', 's0']);
  assert.equal(hud.snapshot().title, '任务5');
});

test('done task coexists with a running task in the list', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 'a', title: 'A', running: true });
  hud.apply({ type: 'agent-status', sessionId: 'a', running: false }); // done
  step(100);
  hud.apply({ type: 'agent-status', sessionId: 'b', title: 'B', running: true });
  const tasks = hud.snapshot().tasks;
  assert.deepEqual(tasks.map((t) => [t.session, t.status]), [['b', 'running'], ['a', 'done']]);
  assert.equal(hud.snapshot().mode, 'running'); // collapsed card follows the running task
});

test('title updates while idle are reflected', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 's1', running: true });
  hud.apply({ type: 'title', sessionId: 's1', title: '新标题' });
  assert.equal(hud.snapshot().title, '新标题');
  assert.equal(hud.snapshot().tasks[0].title, '新标题');
});

test('expireDone collapses a done state to idle', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 's1', running: true });
  hud.apply({ type: 'agent-status', sessionId: 's1', running: false });
  assert.equal(hud.snapshot().mode, 'done');
  hud.expireDone();
  assert.equal(hud.snapshot().mode, 'idle');
});

test('stale sessions are pruned', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 'old', title: 'old', running: true });
  hud.apply({ type: 'agent-status', sessionId: 'old', running: false });
  step(31 * 60 * 1000);
  hud.snapshot();
  assert.equal(hud.sessions.has('old'), false);
});

test('nextDoneExpiry reports the pending done window', () => {
  const hud = fresh();
  hud.apply({ type: 'agent-status', sessionId: 's1', running: true });
  assert.equal(hud.nextDoneExpiry(), null);
  hud.apply({ type: 'agent-status', sessionId: 's1', running: false });
  assert.equal(hud.nextDoneExpiry(), DONE_TTL_MS);
  step(1_000);
  assert.equal(hud.nextDoneExpiry(), DONE_TTL_MS - 1_000);
  hud.apply({ type: 'approval-pending', id: 'a1', sessionId: 's1' });
  assert.equal(hud.nextDoneExpiry(), null); // attention owns the row now
});

let failed = 0;
for (const [name, fn] of tests) {
  try {
    fn();
    console.log(`ok - ${name}`);
  } catch (error) {
    failed += 1;
    console.error(`not ok - ${name}`);
    console.error(error && error.stack ? error.stack : error);
  }
}
console.log(`# ${tests.length - failed}/${tests.length} passed`);
if (failed > 0) process.exitCode = 1;
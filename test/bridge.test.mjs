/**
 * File-transport tests without the real overlay .exe: a fake spawn stands in
 * for HUD.exe and the test reads/writes the same state file the overlay reads.
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import { createHudBridge } from '../hud-bridge.js';

const quietLog = { info() {}, warn() {} };
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function scratchDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-hud-bridge-'));
}

function fakeSpawn() {
  const calls = [];
  const children = [];
  const impl = (exePath, args, options) => {
    const child = new EventEmitter();
    child.pid = 4000 + calls.length;
    child.killed = false;
    child.kill = () => {
      child.killed = true;
    };
    calls.push({ exePath, args, options });
    children.push(child);
    return child;
  };
  return { impl, calls, children };
}

function readState(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

const tests = [];
const test = (name, fn) => tests.push([name, fn]);

test('publish writes an atomic state file and dedupes unchanged payloads', async () => {
  const dir = scratchDir();
  const stateFile = path.join(dir, 'state.json');
  const bridge = createHudBridge({ exePath: 'missing.exe', stateFile, logger: quietLog, heartbeatMs: 60_000 });
  try {
    const message = { t: 'state', mode: 'running', title: '测试', detail: '执行中', session: 's1', since: 1 };
    assert.equal(bridge.publish(message), true);
    const first = readState(stateFile);
    assert.equal(first.mode, 'running');
    assert.equal(first.title, '测试');
    assert.equal(first.ts > 0, true);
    assert.equal('t' in first, false); // host-only field never leaks into the file
    assert.equal(fs.existsSync(`${stateFile}.tmp`), false);

    assert.equal(bridge.publish(message), false); // deduplicated
    assert.equal(readState(stateFile).mode, 'running');
  } finally {
    bridge.dispose();
  }
  assert.equal(fs.existsSync(stateFile), false); // dispose removes it
  fs.rmSync(dir, { recursive: true, force: true });
});

test('heartbeat keeps rewriting the file so mtime stays fresh', async () => {
  const dir = scratchDir();
  const stateFile = path.join(dir, 'state.json');
  const bridge = createHudBridge({ exePath: 'missing.exe', stateFile, logger: quietLog, heartbeatMs: 25 });
  try {
    bridge.publish({ mode: 'idle', title: 'A', detail: '', session: '', since: 1 });
    const firstTs = readState(stateFile).ts;
    await sleep(80);
    const secondTs = readState(stateFile).ts;
    assert.equal(secondTs >= firstTs, true);
    assert.equal(secondTs > firstTs, true);
  } finally {
    bridge.dispose();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('start spawns the overlay with --state-file and seeds the state file', async () => {
  const dir = scratchDir();
  const exePath = path.join(dir, 'HUD.exe');
  fs.writeFileSync(exePath, 'fake');
  const stateFile = path.join(dir, 'state.json');
  const byeFile = path.join(dir, 'hud-bye-v1.json');
  fs.writeFileSync(byeFile, JSON.stringify({ t: 'bye', ts: Date.now() })); // stale bye must be forgotten
  const fake = fakeSpawn();
  const bridge = createHudBridge({ exePath, stateFile, logger: quietLog, spawnImpl: fake.impl, heartbeatMs: 60_000 });
  try {
    bridge.publish({ mode: 'idle', title: 'DeepSeek Harness', detail: '', session: '', since: 1 });
    bridge.start();
    assert.equal(fake.calls.length, 1);
    assert.deepEqual(fake.calls[0].args, ['--state-file', stateFile]);
    assert.equal(fake.calls[0].options.windowsHide, true);
    assert.equal(bridge.overlayRunning, true);
    assert.equal(fs.existsSync(byeFile), false);
    assert.equal(readState(stateFile).mode, 'idle');
  } finally {
    bridge.dispose();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('unexpected exit respawns; a user bye stops respawning and removes the state', async () => {
  const dir = scratchDir();
  const exePath = path.join(dir, 'HUD.exe');
  fs.writeFileSync(exePath, 'fake');
  const stateFile = path.join(dir, 'state.json');
  const byeFile = path.join(dir, 'hud-bye-v1.json');
  const fake = fakeSpawn();
  let quitCalls = 0;
  const bridge = createHudBridge({
    exePath,
    stateFile,
    logger: quietLog,
    spawnImpl: fake.impl,
    onUserQuit: () => quitCalls++,
    respawnDelayMs: 20,
    quitGraceMs: 20,
    heartbeatMs: 60_000,
  });
  try {
    bridge.publish({ mode: 'running', title: 'T', detail: '执行中', session: 's', since: 1 });
    bridge.start();
    fake.children[0].emit('exit', 1, null);
    await sleep(80);
    assert.equal(fake.calls.length, 2); // respawned once

    // The user quits from the overlay menu: it writes hud-bye-v1.json then exits.
    fs.writeFileSync(byeFile, JSON.stringify({ t: 'bye', ts: Date.now() }));
    fake.children[1].emit('exit', 0, null);
    await sleep(80);
    assert.equal(bridge.exitedByUser, true);
    assert.equal(quitCalls, 1);
    assert.equal(fs.existsSync(stateFile), false);
    assert.equal(fake.calls.length, 2); // no further respawn
  } finally {
    bridge.dispose();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('dispose deletes the state file and kills the overlay after the grace period', async () => {
  const dir = scratchDir();
  const exePath = path.join(dir, 'HUD.exe');
  fs.writeFileSync(exePath, 'fake');
  const stateFile = path.join(dir, 'state.json');
  const fake = fakeSpawn();
  const bridge = createHudBridge({
    exePath,
    stateFile,
    logger: quietLog,
    spawnImpl: fake.impl,
    quitGraceMs: 20,
    heartbeatMs: 60_000,
  });
  bridge.publish({ mode: 'running', title: 'T', detail: '', session: 's', since: 1 });
  bridge.start();
  assert.equal(fs.existsSync(stateFile), true);
  bridge.dispose();
  assert.equal(fs.existsSync(stateFile), false);
  await sleep(60);
  assert.equal(fake.children[0].killed, true);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('v3 payload keeps the ordered task list and re-writes when it changes', async () => {
  const dir = scratchDir();
  const stateFile = path.join(dir, 'state.json');
  const bridge = createHudBridge({ exePath: 'missing.exe', stateFile, logger: quietLog, heartbeatMs: 60_000 });
  try {
    const base = {
      v: 3,
      mode: 'running',
      title: 'B',
      detail: '执行中',
      session: 'b',
      since: 2,
      tasks: [
        { session: 'b', title: 'B', status: 'running', detail: '执行中', since: 2 },
        { session: 'a', title: 'A', status: 'attention', detail: '需要你授权', since: 1 },
      ],
    };
    assert.equal(bridge.publish(base), true);
    const first = readState(stateFile);
    assert.equal(first.v, 3);
    assert.equal(first.tasks.length, 2);
    assert.deepEqual(first.tasks.map((t) => t.status), ['running', 'attention']);
    assert.equal('t' in first.tasks[0], false);
    assert.equal(bridge.publish(base), false); // unchanged payload is deduplicated
    const extended = {
      ...base,
      tasks: [...base.tasks, { session: 'c', title: 'C', status: 'done', detail: '任务完成', since: 3 }],
    };
    assert.equal(bridge.publish(extended), true); // a changed task list re-writes
    assert.equal(readState(stateFile).tasks.length, 3);
  } finally {
    bridge.dispose();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('duplicate-instance exit (42) waits, then retries the takeover', async () => {
  const dir = scratchDir();
  const exePath = path.join(dir, 'HUD.exe');
  fs.writeFileSync(exePath, 'fake');
  const stateFile = path.join(dir, 'state.json');
  const fake = fakeSpawn();
  const bridge = createHudBridge({
    exePath,
    stateFile,
    logger: quietLog,
    spawnImpl: fake.impl,
    respawnDelayMs: 20,
    duplicateRetryMs: 60,
    heartbeatMs: 60_000,
  });
  try {
    bridge.publish({ mode: 'running', title: 'T', detail: '执行中', session: 's', since: 1 });
    bridge.start();
    assert.equal(fake.calls.length, 1);
    // A zombie HUD from the previous Host run still owns the window mutex.
    fake.children[0].emit('exit', 42, null);
    await sleep(30);
    assert.equal(fake.calls.length, 1); // not immediate: never two windows at once
    assert.equal(bridge.overlayRunning, false);
    await sleep(90);
    assert.equal(fake.calls.length, 2); // takeover retried once the zombie is gone
  } finally {
    bridge.dispose();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('revive restarts a stopped overlay exactly once', async () => {
  const dir = scratchDir();
  const exePath = path.join(dir, 'HUD.exe');
  fs.writeFileSync(exePath, 'fake');
  const stateFile = path.join(dir, 'state.json');
  const fake = fakeSpawn();
  const bridge = createHudBridge({ exePath, stateFile, logger: quietLog, spawnImpl: fake.impl, heartbeatMs: 60_000 });
  try {
    bridge.publish({ mode: 'running', title: 'T', detail: '执行中', session: 's', since: 1 });
    bridge.start();
    bridge.killOverlay();
    assert.equal(bridge.overlayRunning, false);
    assert.equal(bridge.revive(), true);
    assert.equal(fake.calls.length, 2);
    assert.deepEqual(fake.calls[1].args, ['--state-file', stateFile]);
    assert.equal(bridge.revive(), false); // already running: only re-paints
    assert.equal(fake.calls.length, 2);
  } finally {
    bridge.dispose();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('start writes a fresh placeholder so a stale leftover file cannot kill the overlay', async () => {
  const dir = scratchDir();
  const exePath = path.join(dir, 'HUD.exe');
  fs.writeFileSync(exePath, 'fake');
  const stateFile = path.join(dir, 'state.json');
  // A previous Host run left this behind, one hour old: the exact startup bug.
  fs.writeFileSync(
    stateFile,
    JSON.stringify({ v: 3, mode: 'idle', title: 'old', detail: '', session: '', since: 1, tasks: [] }) + '\n',
  );
  const hourAgo = new Date(Date.now() - 3_600_000);
  fs.utimesSync(stateFile, hourAgo, hourAgo);

  const fake = fakeSpawn();
  const bridge = createHudBridge({ exePath, stateFile, logger: quietLog, spawnImpl: fake.impl, heartbeatMs: 60_000 });
  try {
    bridge.start();
    const written = readState(stateFile);
    assert.equal(written.v, 3);
    assert.equal(written.mode, 'idle');
    assert.equal(written.ts > Date.now() - 10_000, true);
    assert.equal(Date.now() - fs.statSync(stateFile).mtimeMs < 10_000, true);
    assert.deepEqual(fake.calls[0].args, ['--state-file', stateFile]);
  } finally {
    bridge.dispose();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('keeps retrying after the respawn budget instead of dying forever', async () => {
  const dir = scratchDir();
  const exePath = path.join(dir, 'HUD.exe');
  fs.writeFileSync(exePath, 'fake');
  const stateFile = path.join(dir, 'state.json');
  const fake = fakeSpawn();
  const bridge = createHudBridge({
    exePath,
    stateFile,
    logger: quietLog,
    spawnImpl: fake.impl,
    respawnDelayMs: 5,
    maxRespawns: 1,
    giveUpRetryMs: 30,
    heartbeatMs: 60_000,
  });
  try {
    bridge.publish({ mode: 'running', title: 'T', detail: 'running', session: 's', since: 1 });
    bridge.start();
    assert.equal(fake.calls.length, 1);
    fake.children[0].emit('exit', 0, null); // one respawn left
    await sleep(40);
    assert.equal(fake.calls.length, 2);
    fake.children[1].emit('exit', 0, null); // budget exhausted -> backoff retry
    await sleep(90);
    assert.equal(fake.calls.length >= 3, true);
  } finally {
    bridge.dispose();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

let failed = 0;
for (const [name, fn] of tests) {
  try {
    await fn();
    console.log(`ok - ${name}`);
  } catch (error) {
    failed += 1;
    console.error(`not ok - ${name}`);
    console.error(error && error.stack ? error.stack : error);
  }
}
console.log(`# ${tests.length - failed}/${tests.length} passed`);
if (failed > 0) process.exitCode = 1;
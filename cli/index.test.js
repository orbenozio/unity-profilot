'use strict';

// Tests for the Profilot CLI. No framework - Node's built-in test runner (node 18+).
// Run with `node --test` from this folder, or `npm test`. Each test builds a throwaway
// project with a fake event store and drives the CLI as a child process, asserting on the
// JSON it prints. This is the contract the Unity side writes and Claude Code reads.

const { test } = require('node:test');
const assert = require('node:assert');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const CLI = path.join(__dirname, 'index.js');

function sampleEvent(eventId, type, capturedAt, reviewStatus = 'open') {
  return {
    schemaVersion: '1',
    eventId,
    status: 'ok',
    reviewStatus,
    stale: false,
    capturedAt,
    sessionId: 't',
    unityVersion: '6000.3',
    frameIndex: 1,
    requestedFrameIndex: 1,
    frameIndexDelta: 0,
    trigger: { type, severity: 'low', metric: 'gcAllocBytes', value: 1000, budget: 0 },
    counters: { frameTimeMs: 1, gcAllocBytes: 1000, drawCalls: 0 },
    markerTree: { name: 'PlayerLoop', selfTimeMs: 0, totalTimeMs: 1, gcAllocBytes: 1000, calls: 1 },
    topMarkers: [{ name: 'Foo.Update', selfTimeMs: 0.1, totalTimeMs: 0.1, gcAllocBytes: 1000, calls: 1 }],
    dedup: { count: 5, firstSeenFrame: 1, lastSeenFrame: 9 },
  };
}

// Build a temp Unity-project-like root with the given events in the store.
function makeProject(events) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'profilot-test-'));
  const dir = path.join(root, 'Library', 'Profilot', 'events');
  fs.mkdirSync(dir, { recursive: true });
  let latest = null;
  for (const e of events) {
    fs.writeFileSync(path.join(dir, `${e.eventId}.json`), JSON.stringify(e));
    latest = e;
  }
  if (latest) {
    fs.writeFileSync(
      path.join(dir, 'latest.json'),
      JSON.stringify({ schemaVersion: '1', eventId: latest.eventId, file: `${latest.eventId}.json`, capturedAt: latest.capturedAt }),
    );
  }
  return root;
}

function run(projectRoot, args) {
  const out = execFileSync('node', [CLI, ...args], {
    env: { ...process.env, PROFILOT_PROJECT: projectRoot },
    encoding: 'utf8',
  });
  return JSON.parse(out);
}

test('status: ok with count and latest', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const r = run(p, ['status']);
  assert.equal(r.status, 'ok');
  assert.equal(r.eventCount, 1);
  assert.equal(r.latest, 'evt_a_gc_spike');
});

test('status: no_data when the store is missing', () => {
  const empty = fs.mkdtempSync(path.join(os.tmpdir(), 'profilot-empty-'));
  const r = run(empty, ['status']);
  assert.equal(r.status, 'no_data');
});

test('list: summaries, newest first', () => {
  const p = makeProject([
    sampleEvent('evt_old_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z'),
    sampleEvent('evt_new_frame_hitch', 'frame_hitch', '2026-06-25T11:00:00Z'),
  ]);
  const r = run(p, ['list']);
  assert.equal(r.status, 'ok');
  assert.equal(r.count, 2);
  assert.equal(r.events[0].eventId, 'evt_new_frame_hitch');
  assert.equal(r.events[0].type, 'frame_hitch');
  assert.equal(r.events[0].reviewStatus, 'open');
});

test('diagnose --last: full latest record incl. nested markerTree', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const r = run(p, ['diagnose', '--last']);
  assert.equal(r.eventId, 'evt_a_gc_spike');
  assert.equal(r.trigger.type, 'gc_spike');
  assert.equal(r.markerTree.name, 'PlayerLoop');
  assert.equal(r.topMarkers[0].name, 'Foo.Update');
});

test('diagnose --id: returns the requested record', () => {
  const p = makeProject([
    sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z'),
    sampleEvent('evt_b_frame_hitch', 'frame_hitch', '2026-06-25T11:00:00Z'),
  ]);
  const r = run(p, ['diagnose', '--id', 'evt_a_gc_spike']);
  assert.equal(r.eventId, 'evt_a_gc_spike');
  assert.equal(r.trigger.type, 'gc_spike');
});

test('diagnose --id: unknown id returns error status', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const r = run(p, ['diagnose', '--id', 'nope']);
  assert.equal(r.status, 'error');
});

test('diagnose --last: no_data when nothing captured', () => {
  const empty = fs.mkdtempSync(path.join(os.tmpdir(), 'profilot-empty2-'));
  fs.mkdirSync(path.join(empty, 'Library', 'Profilot', 'events'), { recursive: true });
  const r = run(empty, ['diagnose', '--last']);
  assert.equal(r.status, 'no_data');
});

test('unknown command returns error status', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const r = run(p, ['frobnicate']);
  assert.equal(r.status, 'error');
});

test('list: skips a malformed event file rather than failing', () => {
  const p = makeProject([sampleEvent('evt_good_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const dir = path.join(p, 'Library', 'Profilot', 'events');
  fs.writeFileSync(path.join(dir, 'evt_broken_gc_spike.json'), '{ this is not json');
  const r = run(p, ['list']);
  assert.equal(r.status, 'ok');
  assert.equal(r.count, 1);
  assert.equal(r.events[0].eventId, 'evt_good_gc_spike');
});

test('diagnose --last: error when latest points to a missing file', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const dir = path.join(p, 'Library', 'Profilot', 'events');
  fs.rmSync(path.join(dir, 'evt_a_gc_spike.json')); // keep latest.json, drop the event
  const r = run(p, ['diagnose', '--last']);
  assert.equal(r.status, 'error');
});

test('diagnose --last: error on malformed latest.json', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const dir = path.join(p, 'Library', 'Profilot', 'events');
  fs.writeFileSync(path.join(dir, 'latest.json'), 'not json');
  const r = run(p, ['diagnose', '--last']);
  assert.equal(r.status, 'error');
});

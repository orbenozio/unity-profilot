'use strict';

// Tests for the Profilot CLI. No framework - Node's built-in test runner (node 18+).
// Run with `node --test` from this folder, or `npm test`. Each test builds a throwaway
// project with a fake per-run event store and drives the CLI as a child process, asserting on
// the JSON it prints. This is the contract the Unity side writes and Claude Code reads.

const { test } = require('node:test');
const assert = require('node:assert');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const CLI = path.join(__dirname, 'index.js');

function sampleEvent(eventId, type, capturedAt, reviewStatus = 'open', sessionId = '2026-06-25_10-00-00') {
  return {
    schemaVersion: '1',
    eventId,
    status: 'ok',
    reviewStatus,
    sessionId,
    capturedAt,
    unityVersion: '6000.3',
    frameIndex: 1,
    requestedFrameIndex: 1,
    frameIndexDelta: 0,
    cpuTimeMs: 1,
    trigger: { type, severity: 'low', metric: 'gcAllocBytes', value: 1000, budget: 0 },
    counters: { frameTimeMs: 1, gcAllocBytes: 1000, drawCalls: 0 },
    markerTree: { name: 'PlayerLoop', selfTimeMs: 0, totalTimeMs: 1, gcAllocBytes: 1000, calls: 1 },
    topMarkers: [{ name: 'Foo.Update', selfTimeMs: 0.1, totalTimeMs: 0.1, gcAllocBytes: 1000, calls: 1 }],
    dedup: { count: 5, firstSeenFrame: 1, lastSeenFrame: 9 },
  };
}

const baseDir = (root) => path.join(root, 'Library', 'Profilot');
const runDir = (root, run) => path.join(baseDir(root), 'runs', run);
const latestFile = (root) => path.join(baseDir(root), 'latest.json');

// Build a temp Unity-project-like root: each event goes into its run's folder
// (runs/<sessionId>/), plus a top-level latest.json pointing at the last one written.
function makeProject(events) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'profilot-test-'));
  let latest = null;
  for (const e of events) {
    const dir = runDir(root, e.sessionId);
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, `${e.eventId}.json`), JSON.stringify(e));
    latest = e;
  }
  if (latest) {
    fs.mkdirSync(baseDir(root), { recursive: true });
    fs.writeFileSync(
      latestFile(root),
      JSON.stringify({ schemaVersion: '1', eventId: latest.eventId, run: latest.sessionId, file: `${latest.eventId}.json`, capturedAt: latest.capturedAt }),
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

test('status: ok with counts and latest', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const r = run(p, ['status']);
  assert.equal(r.status, 'ok');
  assert.equal(r.eventCount, 1);
  assert.equal(r.runCount, 1);
  assert.equal(r.latest.eventId, 'evt_a_gc_spike');
});

test('status: no_data when the store is missing', () => {
  const empty = fs.mkdtempSync(path.join(os.tmpdir(), 'profilot-empty-'));
  const r = run(empty, ['status']);
  assert.equal(r.status, 'no_data');
});

test('runs: lists runs with event counts, newest first', () => {
  const p = makeProject([
    sampleEvent('evt_old_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00'),
    sampleEvent('evt_new_frame_hitch', 'frame_hitch', '2026-06-26T11:00:00Z', 'open', '2026-06-26_11-00-00'),
  ]);
  const r = run(p, ['runs']);
  assert.equal(r.status, 'ok');
  assert.equal(r.count, 2);
  assert.equal(r.runs[0].run, '2026-06-26_11-00-00'); // newest first
  assert.equal(r.runs[0].eventCount, 1);
});

test('list: summaries across runs, newest first, with run + sessionId', () => {
  const p = makeProject([
    sampleEvent('evt_old_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00'),
    sampleEvent('evt_new_frame_hitch', 'frame_hitch', '2026-06-26T11:00:00Z', 'open', '2026-06-26_11-00-00'),
  ]);
  const r = run(p, ['list']);
  assert.equal(r.status, 'ok');
  assert.equal(r.count, 2);
  assert.equal(r.events[0].eventId, 'evt_new_frame_hitch');
  assert.equal(r.events[0].run, '2026-06-26_11-00-00');
  assert.equal(r.events[0].sessionId, '2026-06-26_11-00-00');
});

test('list --run: only that run', () => {
  const p = makeProject([
    sampleEvent('evt_old_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00'),
    sampleEvent('evt_new_frame_hitch', 'frame_hitch', '2026-06-26T11:00:00Z', 'open', '2026-06-26_11-00-00'),
  ]);
  const r = run(p, ['list', '--run', '2026-06-25_10-00-00']);
  assert.equal(r.count, 1);
  assert.equal(r.events[0].eventId, 'evt_old_gc_spike');
});

test('diagnose --last: full latest record incl. nested markerTree', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const r = run(p, ['diagnose', '--last']);
  assert.equal(r.eventId, 'evt_a_gc_spike');
  assert.equal(r.trigger.type, 'gc_spike');
  assert.equal(r.markerTree.name, 'PlayerLoop');
  assert.equal(r.topMarkers[0].name, 'Foo.Update');
});

test('diagnose --id: newest run containing it', () => {
  const p = makeProject([
    sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00'),
    sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-26T11:00:00Z', 'open', '2026-06-26_11-00-00'),
  ]);
  const r = run(p, ['diagnose', '--id', 'evt_a_gc_spike']);
  assert.equal(r.eventId, 'evt_a_gc_spike');
  assert.equal(r.sessionId, '2026-06-26_11-00-00'); // newest run
});

test('diagnose --id --run: a specific run', () => {
  const p = makeProject([
    sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00'),
    sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-26T11:00:00Z', 'open', '2026-06-26_11-00-00'),
  ]);
  const r = run(p, ['diagnose', '--id', 'evt_a_gc_spike', '--run', '2026-06-25_10-00-00']);
  assert.equal(r.sessionId, '2026-06-25_10-00-00');
});

test('diagnose --id: unknown id returns error status', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const r = run(p, ['diagnose', '--id', 'nope']);
  assert.equal(r.status, 'error');
});

test('diagnose --last: no_data when nothing captured', () => {
  const empty = fs.mkdtempSync(path.join(os.tmpdir(), 'profilot-empty2-'));
  fs.mkdirSync(path.join(empty, 'Library', 'Profilot', 'runs'), { recursive: true });
  const r = run(empty, ['diagnose', '--last']);
  assert.equal(r.status, 'no_data');
});

test('reviews.json overlays reviewStatus in list and diagnose', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.writeFileSync(
    path.join(baseDir(p), 'reviews.json'),
    JSON.stringify({ items: [{ id: 'evt_a_gc_spike', status: 'not_a_real_issue' }] }),
  );
  const l = run(p, ['list']);
  assert.equal(l.events[0].reviewStatus, 'not_a_real_issue');
  const d = run(p, ['diagnose', '--id', 'evt_a_gc_spike']);
  assert.equal(d.reviewStatus, 'not_a_real_issue');
});

test('unknown command returns error status', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const r = run(p, ['frobnicate']);
  assert.equal(r.status, 'error');
});

test('list: skips a malformed event file rather than failing', () => {
  const p = makeProject([sampleEvent('evt_good_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.writeFileSync(path.join(runDir(p, '2026-06-25_10-00-00'), 'evt_broken_gc_spike.json'), '{ this is not json');
  const r = run(p, ['list']);
  assert.equal(r.status, 'ok');
  assert.equal(r.count, 1);
  assert.equal(r.events[0].eventId, 'evt_good_gc_spike');
});

test('diagnose --last: error when latest points to a missing file', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.rmSync(path.join(runDir(p, '2026-06-25_10-00-00'), 'evt_a_gc_spike.json')); // keep latest.json, drop the event
  const r = run(p, ['diagnose', '--last']);
  assert.equal(r.status, 'error');
});

test('diagnose --last: error on malformed latest.json', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.writeFileSync(latestFile(p), 'not json');
  const r = run(p, ['diagnose', '--last']);
  assert.equal(r.status, 'error');
});

// ---------------------------------------------------------------------------
// Depth: --focus / --depth
//
// The chain below is the real shape of a Unity boot frame: seven levels of engine
// scaffolding before the first line of user code, then hand-placed Profiler.BeginSample
// markers under it. That is why a fixed shallow tree could never attribute the cost - it
// ended exactly where the answer began.
// ---------------------------------------------------------------------------

const SCAFFOLD = [
  'PlayerLoop',
  'UpdateScene',
  'Update.ScriptRunBehaviourUpdate',
  'BehaviourUpdate',
  'EventSystem.Update',
  'Instantiate',
  'Instantiate.Awake',
  'Assembly-CSharp.dll!::TeamsScreen.Awake() [Invoke]', // depth 7 - the culprit
  'DIAG.TS.LoadIcons', // depth 8 - invisible at the old fixed depth
  'DIAG.TS.Inner', // depth 9
];

// A linear chain of the names above; each level also carries a cheap sibling so trimming
// has something to choose between.
function deepTree(names = SCAFFOLD) {
  let node = null;
  for (let i = names.length - 1; i >= 0; i--) {
    const children = [];
    if (node) children.push(node);
    if (i > 0) children.push({ name: 'sibling' + i, selfTimeMs: 0.1, totalTimeMs: 0.1, gcAllocBytes: 0, calls: 1 });
    node = {
      name: names[i],
      selfTimeMs: i === names.length - 1 ? 322 : 1,
      totalTimeMs: 1512 - i * 100,
      gcAllocBytes: 1000,
      calls: 1,
      ...(children.length ? { children } : {}),
    };
  }
  return node;
}

function deepEvent(eventId = 'evt_frame_hitch_TeamsScreen.Awake', sessionId = '2026-06-25_10-00-00') {
  const e = sampleEvent(eventId, 'frame_hitch', '2026-06-25T10:00:00Z', 'open', sessionId);
  e.markerTree = deepTree();
  e.markerTreeDepth = 16;
  return e;
}

// Walks down the chain (skipping the filler siblings) so a test can assert what survived.
function nodeAt(tree, depth) {
  let n = tree;
  for (let d = 0; d < depth; d++) {
    if (!n || !n.children) return null;
    n = n.children.find((c) => !String(c.name).startsWith('sibling')) || null;
  }
  return n;
}

test('diagnose: the default payload is unchanged - 6 levels, and it says it was cut', () => {
  const p = makeProject([deepEvent()]);
  const r = run(p, ['diagnose', '--last']);
  assert.equal(r.markerTree.name, 'PlayerLoop');
  assert.equal(nodeAt(r.markerTree, 6).name, 'Instantiate.Awake');
  assert.equal(nodeAt(r.markerTree, 6).children, undefined);
  assert.equal(nodeAt(r.markerTree, 6).truncated, 'depth'); // a cut, not a leaf
  assert.equal(nodeAt(r.markerTree, 7), null);
});

test('diagnose --depth: reaches markers the default depth cuts off', () => {
  const p = makeProject([deepEvent()]);
  const r = run(p, ['diagnose', '--last', '--depth', '9']);
  assert.equal(nodeAt(r.markerTree, 7).name, 'Assembly-CSharp.dll!::TeamsScreen.Awake() [Invoke]');
  assert.equal(nodeAt(r.markerTree, 9).name, 'DIAG.TS.Inner');
});

test('diagnose --depth: beyond what was captured, says so', () => {
  const p = makeProject([deepEvent()]);
  const r = run(p, ['diagnose', '--last', '--depth', '40']);
  assert.match(r.depthNote, /deeper than the captured depth 16/);
});

test('diagnose --depth: rejects a non-numeric value', () => {
  const p = makeProject([deepEvent()]);
  const r = run(p, ['diagnose', '--last', '--depth', 'lots']);
  assert.equal(r.status, 'error');
  assert.match(r.message, /Invalid --depth/);
});

test('diagnose --focus: re-roots at a substring match and returns the whole subtree', () => {
  const p = makeProject([deepEvent()]);
  const r = run(p, ['diagnose', '--last', '--focus', 'TeamsScreen.Awake']);
  assert.equal(r.markerTree.name, 'Assembly-CSharp.dll!::TeamsScreen.Awake() [Invoke]');
  assert.equal(r.focus.depth, 7);
  assert.equal(r.focus.path[0], 'PlayerLoop');
  assert.equal(r.focus.matchCount, 1);
  // Full depth under the focus root - not a 6-level slice of it.
  assert.equal(nodeAt(r.markerTree, 1).name, 'DIAG.TS.LoadIcons');
  assert.equal(nodeAt(r.markerTree, 2).name, 'DIAG.TS.Inner');
});

test('diagnose --focus: matches a deep hand-placed marker too', () => {
  const p = makeProject([deepEvent()]);
  const r = run(p, ['diagnose', '--last', '--focus', 'DIAG.TS.LoadIcons']);
  assert.equal(r.markerTree.name, 'DIAG.TS.LoadIcons');
  assert.equal(r.focus.depth, 8);
});

test('diagnose --focus: several matches - heaviest wins, the rest are listed', () => {
  const p = makeProject([deepEvent()]);
  const r = run(p, ['diagnose', '--last', '--focus', 'DIAG.TS']);
  assert.equal(r.focus.matchCount, 2);
  assert.equal(r.markerTree.name, 'DIAG.TS.LoadIcons'); // the bigger totalTimeMs
  assert.equal(r.focus.otherMatches[0].name, 'DIAG.TS.Inner');
});

test('diagnose --focus --depth: the subtree is trimmed too', () => {
  const p = makeProject([deepEvent()]);
  const r = run(p, ['diagnose', '--last', '--focus', 'TeamsScreen.Awake', '--depth', '1']);
  assert.equal(r.markerTree.name, 'Assembly-CSharp.dll!::TeamsScreen.Awake() [Invoke]');
  assert.equal(nodeAt(r.markerTree, 1).name, 'DIAG.TS.LoadIcons');
  assert.equal(nodeAt(r.markerTree, 1).children, undefined);
});

test('diagnose --focus: no match returns an error plus the names that do exist', () => {
  const p = makeProject([deepEvent()]);
  const r = run(p, ['diagnose', '--last', '--focus', 'NoSuchMarker']);
  assert.equal(r.status, 'error');
  assert.match(r.message, /No marker in .* matches "NoSuchMarker"/);
  assert.ok(r.focusCandidates.includes('DIAG.TS.Inner'));
});

test('diagnose --focus: rejects a missing value', () => {
  const p = makeProject([deepEvent()]);
  const r = run(p, ['diagnose', '--last', '--focus']);
  assert.equal(r.status, 'error');
  assert.match(r.message, /Missing value for --focus/);
});

test('diagnose --focus: clear error when the record has no marker tree', () => {
  const e = deepEvent();
  e.markerTree = null;
  e.status = 'counters_only';
  const p = makeProject([e]);
  const r = run(p, ['diagnose', '--last', '--focus', 'Anything']);
  assert.equal(r.status, 'error');
  assert.match(r.message, /has no marker tree/);
});

// ---------------------------------------------------------------------------
// Run fallback: --id silently serves an older run once the event stops reproducing
// ---------------------------------------------------------------------------

test('diagnose --id: warns when the event is missing from the newest run', () => {
  const p = makeProject([
    sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00'),
    sampleEvent('evt_b_gc_spike', 'gc_spike', '2026-06-26T11:00:00Z', 'open', '2026-06-26_11-00-00'),
  ]);
  const r = run(p, ['diagnose', '--id', 'evt_a_gc_spike']);
  assert.equal(r.runFallback, true);
  assert.equal(r.resolvedRun, '2026-06-25_10-00-00');
  assert.equal(r.latestRun, '2026-06-26_11-00-00');
  assert.match(r.warning, /not the newest run/);
});

test('diagnose --id: no warning when the event is in the newest run', () => {
  const p = makeProject([
    sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00'),
    sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-26T11:00:00Z', 'open', '2026-06-26_11-00-00'),
  ]);
  const r = run(p, ['diagnose', '--id', 'evt_a_gc_spike']);
  assert.equal(r.runFallback, undefined);
  assert.equal(r.warning, undefined);
});

test('diagnose --id --run: an explicitly pinned older run is not a fallback', () => {
  const p = makeProject([
    sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00'),
    sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-26T11:00:00Z', 'open', '2026-06-26_11-00-00'),
  ]);
  const r = run(p, ['diagnose', '--id', 'evt_a_gc_spike', '--run', '2026-06-25_10-00-00']);
  assert.equal(r.runFallback, undefined);
});

test('diagnose --last: warns when latest.json predates the newest run', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00')]);
  fs.mkdirSync(runDir(p, '2026-06-26_11-00-00'), { recursive: true }); // a newer run that caught nothing
  const r = run(p, ['diagnose', '--last']);
  assert.equal(r.runFallback, true);
  assert.equal(r.latestRun, '2026-06-26_11-00-00');
});

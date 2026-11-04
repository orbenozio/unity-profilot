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
    JSON.stringify({ items: [{ id: 'evt_a_gc_spike', status: 'regressed', value: 1000 }] }),
  );
  const l = run(p, ['list']);
  assert.equal(l.events[0].reviewStatus, 'regressed');
  const d = run(p, ['diagnose', '--id', 'evt_a_gc_spike']);
  assert.equal(d.reviewStatus, 'regressed');
});

test('reviews.json: pre-0.7 statuses read as the new pair', () => {
  const p = makeProject([
    sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z'),
    sampleEvent('evt_b_gc_spike', 'gc_spike', '2026-06-25T10:00:01Z'),
  ]);
  fs.writeFileSync(
    path.join(baseDir(p), 'reviews.json'),
    JSON.stringify({
      items: [
        { id: 'evt_a_gc_spike', status: 'reviewed' },
        { id: 'evt_b_gc_spike', status: 'not_a_real_issue' },
      ],
    }),
  );
  const byId = Object.fromEntries(run(p, ['list']).events.map((e) => [e.eventId, e.reviewStatus]));
  assert.deepEqual(byId, { evt_a_gc_spike: 'fixed', evt_b_gc_spike: 'ignored' });
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

test('diagnose --last: no_data when the only event file is gone (latest.json is not trusted)', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.rmSync(path.join(runDir(p, '2026-06-25_10-00-00'), 'evt_a_gc_spike.json')); // keep latest.json, drop the event
  const r = run(p, ['diagnose', '--last']);
  assert.equal(r.status, 'no_data');
});

test('diagnose --last: a malformed latest.json does not matter', () => {
  const p = makeProject([sampleEvent('evt_a_gc_spike', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.writeFileSync(latestFile(p), 'not json');
  const r = run(p, ['diagnose', '--last']);
  assert.equal(r.eventId, 'evt_a_gc_spike');
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
  'Assembly-CSharp.dll!::InventoryPanel.Awake() [Invoke]', // depth 7 - the culprit
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

function deepEvent(eventId = 'evt_frame_hitch_InventoryPanel.Awake', sessionId = '2026-06-25_10-00-00') {
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
  assert.equal(nodeAt(r.markerTree, 7).name, 'Assembly-CSharp.dll!::InventoryPanel.Awake() [Invoke]');
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
  const r = run(p, ['diagnose', '--last', '--focus', 'InventoryPanel.Awake']);
  assert.equal(r.markerTree.name, 'Assembly-CSharp.dll!::InventoryPanel.Awake() [Invoke]');
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
  const r = run(p, ['diagnose', '--last', '--focus', 'InventoryPanel.Awake', '--depth', '1']);
  assert.equal(r.markerTree.name, 'Assembly-CSharp.dll!::InventoryPanel.Awake() [Invoke]');
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

// --- init ----------------------------------------------------------------------------------------

function makeUnityProject(manifest = { dependencies: { 'com.unity.ugui': '2.0.0' } }) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'profilot-unity-'));
  fs.mkdirSync(path.join(root, 'Packages'));
  fs.mkdirSync(path.join(root, 'ProjectSettings'));
  fs.writeFileSync(path.join(root, 'Packages', 'manifest.json'), JSON.stringify(manifest, null, 2));
  return root;
}

const stepOf = (r, name) => r.steps.find((s) => s.step === name);

test('init: wires the package, the guide and the CLAUDE.md import into a fresh project', () => {
  const p = makeUnityProject();
  const r = run(p, ['init']);
  assert.equal(r.status, 'ok');
  assert.equal(stepOf(r, 'package').result, 'added');
  assert.equal(stepOf(r, 'guide').result, 'added');
  assert.equal(stepOf(r, 'claudeMd').result, 'created');
  const manifest = JSON.parse(fs.readFileSync(path.join(p, 'Packages', 'manifest.json'), 'utf8'));
  assert.match(manifest.dependencies['com.profilot.profilot'], /unity-profilot\.git\?path=.*#release$/);
  assert.equal(manifest.dependencies['com.unity.ugui'], '2.0.0');
  assert.ok(fs.existsSync(path.join(p, '.claude', 'profilot-diagnosis-guide.md')));
  assert.match(fs.readFileSync(path.join(p, 'CLAUDE.md'), 'utf8'), /@\.claude\/profilot-diagnosis-guide\.md/);
});

test('init: a second run changes nothing', () => {
  const p = makeUnityProject();
  run(p, ['init']);
  const claudeMd = fs.readFileSync(path.join(p, 'CLAUDE.md'), 'utf8');
  const r = run(p, ['init']);
  assert.deepEqual(r.steps.map((s) => s.result), ['unchanged', 'unchanged', 'unchanged']);
  assert.equal(fs.readFileSync(path.join(p, 'CLAUDE.md'), 'utf8'), claudeMd);
});

test('init: keeps an existing CLAUDE.md and a pinned package version', () => {
  const pinned = 'https://github.com/orbenozio/unity-profilot.git?path=/Packages/com.profilot.profilot#v0.2.0';
  const p = makeUnityProject({ dependencies: { 'com.profilot.profilot': pinned } });
  fs.writeFileSync(path.join(p, 'CLAUDE.md'), '# My game\n\nHouse rules.');
  const r = run(p, ['init']);
  assert.equal(stepOf(r, 'package').result, 'unchanged');
  assert.equal(stepOf(r, 'claudeMd').result, 'added');
  const md = fs.readFileSync(path.join(p, 'CLAUDE.md'), 'utf8');
  assert.ok(md.startsWith('# My game\n\nHouse rules.\n\n## Profilot'));
  const manifest = JSON.parse(fs.readFileSync(path.join(p, 'Packages', 'manifest.json'), 'utf8'));
  assert.equal(manifest.dependencies['com.profilot.profilot'], pinned);
});

test('init: finds the project root from a subfolder', () => {
  const p = makeUnityProject();
  const sub = path.join(p, 'Assets', 'Scripts');
  fs.mkdirSync(sub, { recursive: true });
  const r = run(p, ['init', '--path', sub, '--no-package']);
  assert.equal(r.status, 'ok');
  assert.equal(fs.realpathSync(r.projectRoot), fs.realpathSync(p));
  assert.equal(stepOf(r, 'package').result, 'skipped');
});

test('init: error outside a Unity project', () => {
  const empty = fs.mkdtempSync(path.join(os.tmpdir(), 'profilot-empty-'));
  const r = run(empty, ['init']);
  assert.equal(r.status, 'error');
  assert.match(r.message, /No Unity project found/);
});

test('--version prints the CLI version', () => {
  const r = run(os.tmpdir(), ['--version']);
  assert.equal(r.version, require('./package.json').version);
});

// --- B-1: big payloads survive a pipe -----------------------------------------------------------

test('diagnose: a payload over 200 KB arrives whole through a pipe', () => {
  const e = sampleEvent('evt_big', 'gc_spike', '2026-06-25T10:00:00Z');
  e.markerTree.children = [];
  for (let i = 0; i < 3000; i++) {
    e.markerTree.children.push({ name: `Marker${i}_${'x'.repeat(60)}`, selfTimeMs: 1, totalTimeMs: 1, gcAllocBytes: 0, calls: 1 });
  }
  const p = makeProject([e]);
  // execFileSync reads the child's stdout through a pipe - the case that was cut at 64 KB on
  // macOS/Linux when the CLI called process.exit() right after writing.
  const out = execFileSync('node', [CLI, 'diagnose', '--last', '--depth', '1'], {
    env: { ...process.env, PROFILOT_PROJECT: p },
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
  assert.ok(out.length > 200 * 1024, `payload was only ${out.length} bytes`);
  const r = JSON.parse(out);
  assert.equal(r.markerTree.children.length, 3000);
});

// --- B-4: stale is computed from the runs -------------------------------------------------------

test('stale: false in the newest run, true in an older one (list and diagnose)', () => {
  const p = makeProject([
    sampleEvent('evt_old', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00'),
    sampleEvent('evt_new', 'gc_spike', '2026-06-26T10:00:00Z', 'open', '2026-06-26_10-00-00'),
  ]);
  const byId = Object.fromEntries(run(p, ['list']).events.map((e) => [e.eventId, e]));
  assert.equal(byId.evt_new.stale, false);
  assert.equal(byId.evt_old.stale, true);
  assert.equal(run(p, ['diagnose', '--id', 'evt_old']).stale, true);
  assert.equal(run(p, ['diagnose', '--id', 'evt_new']).stale, false);
  assert.equal(run(p, ['diagnose', '--last']).stale, false);
});

test('stale is per event: a record pinned to an older run is not stale if the problem recurred', () => {
  const p = makeProject([
    sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00'),
    sampleEvent('evt_a', 'gc_spike', '2026-06-26T10:00:00Z', 'open', '2026-06-26_10-00-00'),
  ]);
  const pinned = run(p, ['diagnose', '--id', 'evt_a', '--run', '2026-06-25_10-00-00']);
  assert.equal(pinned.stale, false);
  assert.deepEqual(pinned.recurrence, {
    lastSeenRun: '2026-06-26_10-00-00',
    newestRun: '2026-06-26_10-00-00',
    seenInNewestRun: true,
    runsSeen: 2,
    runsSince: 0,
  });
  assert.equal(run(p, ['diagnose', '--id', 'evt_a']).stale, false);
});

// --- B-5: schema and version compatibility -----------------------------------------------------

test('compat: an older record lists the fields it lacks', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const d = run(p, ['diagnose', '--id', 'evt_a']);
  assert.equal(d.compat.schemaVersion, 1);
  assert.equal(d.compat.cliSchemaVersion, 3);
  assert.ok(d.compat.missingFields.includes('frameMatchesTrigger'));
  assert.ok(d.compat.missingFields.includes('markerTreeDepth'));
  assert.ok(d.compat.missingFields.includes('dedup.peakValue'));
});

test('compat: nothing added for a current record', () => {
  const e = {
    ...sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z'),
    schemaVersion: '3',
    writerVersion: '0.8.0',
  };
  e.dedup.peakValue = 1000;
  const p = makeProject([e]);
  assert.equal(run(p, ['diagnose', '--last']).compat, undefined);
});

test('compat: a newer record says to update the CLI', () => {
  const e = { ...sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z'), schemaVersion: '9' };
  const p = makeProject([e]);
  assert.match(run(p, ['diagnose', '--last']).compat.note, /npm install -g profilot/);
});

test('status: versions of the CLI, the package and the guide copy', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.writeFileSync(path.join(baseDir(p), 'meta.json'), JSON.stringify({ schemaVersion: '3', packageVersion: require('./package.json').version }));

  let s = run(p, ['status']);
  assert.equal(s.versions.cli, require('./package.json').version);
  assert.equal(s.versions.package, require('./package.json').version);
  assert.deepEqual(s.versions.schema, { cli: 3, package: 3 });
  assert.equal(s.versions.guide, 'missing');
  assert.ok(s.warnings.some((w) => w.includes('profilot init')));

  fs.mkdirSync(path.join(p, '.claude'));
  fs.writeFileSync(path.join(p, '.claude', 'profilot-diagnosis-guide.md'), 'an old guide\n');
  assert.equal(run(p, ['status']).versions.guide, 'outdated');

  // The same copy the CLI ships: next to index.js after prepack, else the repo root.
  const packed = path.join(__dirname, 'profilot-diagnosis-guide.md');
  const shipped = fs.existsSync(packed) ? packed : path.join(__dirname, '..', 'profilot-diagnosis-guide.md');
  fs.copyFileSync(shipped, path.join(p, '.claude', 'profilot-diagnosis-guide.md'));
  s = run(p, ['status']);
  assert.equal(s.versions.guide, 'current');
  assert.equal(s.warnings, undefined);
});

test('status: an older package is flagged', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.writeFileSync(path.join(baseDir(p), 'meta.json'), JSON.stringify({ schemaVersion: '1', packageVersion: '0.5.1' }));
  assert.ok(run(p, ['status']).warnings.some((w) => w.includes('Unity package is older')));
});

// --- 0.8: strict arguments, safe paths, --last / --worst, EPIPE ---------------------------------

function runRaw(projectRoot, args) {
  return execFileSync('node', [CLI, ...args], { env: { ...process.env, PROFILOT_PROJECT: projectRoot }, encoding: 'utf8' });
}

test('args: --flag=value works like --flag value', () => {
  const p = makeProject([
    sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z'),
    sampleEvent('evt_b', 'gc_spike', '2026-06-25T10:00:01Z'),
  ]);
  assert.equal(run(p, ['diagnose', '--id=evt_a']).eventId, 'evt_a');
  assert.equal(run(p, ['diagnose', '--id=evt_a', '--depth=0']).markerTree.truncated, undefined);
});

test('args: a positional id, an unknown flag or a missing value is an error, not the latest event', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const pos = run(p, ['diagnose', 'evt_a']);
  assert.equal(pos.status, 'error');
  assert.match(pos.message, /--id evt_a/);
  assert.equal(run(p, ['diagnose', '--bogus']).status, 'error');
  assert.equal(run(p, ['diagnose', '--id']).status, 'error');
  assert.equal(run(p, ['diagnose', '--id', 'evt_a', '--run']).status, 'error');
  assert.equal(run(p, ['diagnose', '--focus=']).status, 'error');
  assert.equal(run(p, ['diagnose', '--last', '--id', 'evt_a']).status, 'error');
  assert.equal(run(p, ['list', '--last']).status, 'error');
});

test('args: an unknown run is an error that lists the real ones', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const l = run(p, ['list', '--run', 'bogus']);
  assert.equal(l.status, 'error');
  assert.deepEqual(l.runs, ['2026-06-25_10-00-00']);
  assert.equal(run(p, ['diagnose', '--id', 'evt_a', '--run', '2026-01-01_00-00-00']).status, 'error');
});

test('paths: --id and --run cannot leave the store', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.writeFileSync(path.join(baseDir(p), 'reviews.json'), JSON.stringify({ items: [] }));
  fs.writeFileSync(path.join(p, 'Library', 'secret.json'), JSON.stringify({ eventId: 'secret' }));
  for (const id of ['../../reviews', '../../../secret', '..', 'a/b', 'a\\b']) {
    assert.equal(run(p, ['diagnose', '--id', id]).status, 'error', id);
  }
  assert.equal(run(p, ['diagnose', '--id', 'evt_a', '--run', '..']).status, 'error');
  assert.equal(run(p, ['list', '--run', '../runs/2026-06-25_10-00-00']).status, 'error');
});

test('runs: a folder that is not a timestamp never poses as the newest run', () => {
  const p = makeProject([
    sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00'),
    sampleEvent('evt_s', 'gc_spike', '2026-06-25T09:00:00Z', 'open', 'session'),
  ]);
  assert.deepEqual(run(p, ['runs']).runs.map((r) => r.run), ['2026-06-25_10-00-00', 'session']);
  assert.equal(run(p, ['diagnose', '--id', 'evt_a']).runFallback, undefined);
});

test('diagnose --last: skips fixed and ignored events; --id still serves them', () => {
  const p = makeProject([
    sampleEvent('evt_open', 'gc_spike', '2026-06-25T10:00:00Z'),
    sampleEvent('evt_fixed', 'gc_spike', '2026-06-25T10:00:01Z'),
    sampleEvent('evt_ignored', 'gc_spike', '2026-06-25T10:00:02Z'),
  ]);
  fs.writeFileSync(
    path.join(baseDir(p), 'reviews.json'),
    JSON.stringify({ items: [{ id: 'evt_fixed', status: 'fixed', value: 1 }, { id: 'evt_ignored', status: 'ignored' }] }),
  );
  assert.equal(run(p, ['diagnose', '--last']).eventId, 'evt_open');
  assert.equal(run(p, ['diagnose']).eventId, 'evt_open');
  assert.equal(run(p, ['diagnose', '--id', 'evt_ignored']).reviewStatus, 'ignored');
});

test('diagnose --last: everything muted says so instead of serving a muted event', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.writeFileSync(path.join(baseDir(p), 'reviews.json'), JSON.stringify({ items: [{ id: 'evt_a', status: 'ignored' }] }));
  const r = run(p, ['diagnose', '--last']);
  assert.equal(r.status, 'no_data');
  assert.deepEqual(r.muted, [{ eventId: 'evt_a', run: '2026-06-25_10-00-00', reviewStatus: 'ignored' }]);
});

test('diagnose --worst: highest severity, then most repeats, in the newest run', () => {
  const low = sampleEvent('evt_low', 'gc_spike', '2026-06-26T10:00:03Z', 'open', '2026-06-26_10-00-00');
  const highFew = sampleEvent('evt_high_few', 'frame_hitch', '2026-06-26T10:00:01Z', 'open', '2026-06-26_10-00-00');
  highFew.trigger.severity = 'high';
  highFew.dedup.count = 2;
  const highMany = sampleEvent('evt_high_many', 'gc_spike', '2026-06-26T10:00:02Z', 'open', '2026-06-26_10-00-00');
  highMany.trigger.severity = 'high';
  highMany.dedup.count = 9;
  const older = sampleEvent('evt_older', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00');
  older.trigger.severity = 'high';
  older.dedup.count = 99;
  const p = makeProject([older, low, highFew, highMany]);
  assert.equal(run(p, ['diagnose', '--worst']).eventId, 'evt_high_many');
  assert.equal(run(p, ['diagnose', '--worst', '--run', '2026-06-25_10-00-00']).eventId, 'evt_older');
});

test('help: plain text usage for a person', () => {
  const out = execFileSync('node', [CLI, '--help'], { encoding: 'utf8' });
  assert.match(out, /^profilot - /);
  assert.match(out, /--worst/);
  assert.match(execFileSync('node', [CLI], { encoding: 'utf8' }), /Usage:/);
});

test('--text: status, runs and list as lines', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.mkdirSync(runDir(p, '2026-06-26_10-00-00'), { recursive: true });
  assert.match(runRaw(p, ['status', '--text']), /^store /);
  assert.equal(runRaw(p, ['runs', '--text']), '2026-06-26_10-00-00  clean\n2026-06-25_10-00-00  1 event\n');
  assert.match(runRaw(p, ['list', '--text']), /^2026-06-25_10-00-00  gc_spike  low  open  evt_a\n$/);
});

test('pipe: a reader that closes early does not crash the CLI (no EPIPE trace, exit 0)', () => {
  const e = sampleEvent('evt_big', 'gc_spike', '2026-06-25T10:00:00Z');
  e.markerTree.children = [];
  for (let i = 0; i < 20000; i++) {
    e.markerTree.children.push({ name: `Marker${i}_${'x'.repeat(60)}`, selfTimeMs: 1, totalTimeMs: 1, gcAllocBytes: 0, calls: 1 });
  }
  const p = makeProject([e]);
  const res = require('node:child_process').spawnSync(
    'bash',
    ['-c', `node "${CLI}" diagnose --id evt_big --depth 1 2>"${p}/err.txt" | head -c 100 >/dev/null; echo \${PIPESTATUS[0]}`],
    { env: { ...process.env, PROFILOT_PROJECT: p }, encoding: 'utf8' },
  );
  assert.equal(res.stdout.trim(), '0');
  assert.equal(fs.readFileSync(path.join(p, 'err.txt'), 'utf8'), '');
});

// --- 0.8: honest version and guide checks ----------------------------------------------------------

const shippedGuide = () => {
  const packed = path.join(__dirname, 'profilot-diagnosis-guide.md');
  return fs.readFileSync(fs.existsSync(packed) ? packed : path.join(__dirname, '..', 'profilot-diagnosis-guide.md'), 'utf8');
};

function withGuide(p, text) {
  fs.mkdirSync(path.join(p, '.claude'), { recursive: true });
  fs.writeFileSync(path.join(p, '.claude', 'profilot-diagnosis-guide.md'), text);
}

test('guide: its header names the CLI version (they release together)', () => {
  assert.match(shippedGuide(), new RegExp(`^<!-- profilot-guide ${require('./package.json').version.replace(/\./g, '\\.')} -->`));
});

test('guide: a CRLF copy is current, a locally edited one is "edited" (a note, not a warning)', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.writeFileSync(path.join(baseDir(p), 'meta.json'), JSON.stringify({ schemaVersion: '3', packageVersion: require('./package.json').version }));
  withGuide(p, shippedGuide().replace(/\n/g, '\r\n'));
  assert.equal(run(p, ['status']).versions.guide, 'current');
  withGuide(p, shippedGuide() + '\n## Our team notes\n');
  const s = run(p, ['status']);
  assert.equal(s.versions.guide, 'edited');
  assert.equal(s.warnings, undefined);
  assert.ok(s.notes.some((n) => n.includes('.bak')));
});

test('guide: a copy from an older release is outdated', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  withGuide(p, '<!-- profilot-guide 0.1.0 -->\n# old\n');
  assert.equal(run(p, ['status']).versions.guide, 'outdated');
});

test('status: runs but no meta.json warns that the package predates 0.7', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const s = run(p, ['status']);
  assert.equal(s.versions.package, 'unknown');
  assert.ok(s.warnings.some((w) => w.includes('no meta.json')));
});

test('status: same schema but a different release is flagged, naming the older side', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  const meta = path.join(baseDir(p), 'meta.json');
  fs.writeFileSync(meta, JSON.stringify({ schemaVersion: '3', packageVersion: '0.6.9' }));
  assert.ok(run(p, ['status']).warnings.some((w) => w.includes('Unity package (0.6.9) is older')));
  fs.writeFileSync(meta, JSON.stringify({ schemaVersion: '3', packageVersion: '99.0.0' }));
  assert.ok(run(p, ['status']).warnings.some((w) => w.includes('is older than the Unity package (99.0.0)')));
});

test('init: an edited guide is backed up before it is replaced; line endings alone change nothing', () => {
  const root = makeUnityProject();
  const dest = path.join(root, '.claude', 'profilot-diagnosis-guide.md');
  withGuide(root, shippedGuide().replace(/\n/g, '\r\n'));
  let r = run(root, ['init', '--path', root, '--no-package']);
  assert.equal(stepOf(r, 'guide').result, 'unchanged');

  withGuide(root, 'my own notes\n');
  r = run(root, ['init', '--path', root, '--no-package']);
  const g = stepOf(r, 'guide');
  assert.equal(g.result, 'updated');
  assert.equal(fs.readFileSync(g.backup, 'utf8'), 'my own notes\n');
  assert.equal(fs.readFileSync(dest, 'utf8'), shippedGuide());

  withGuide(root, 'second edit\n');
  const g2 = stepOf(run(root, ['init', '--path', root, '--no-package']), 'guide');
  assert.notEqual(g2.backup, g.backup);
  assert.equal(fs.readFileSync(g.backup, 'utf8'), 'my own notes\n');
});

// --- 0.8: "is it fixed?" ------------------------------------------------------------------------

test('recurrence: a clean newest run makes the event stale, with the runs since', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z', 'open', '2026-06-25_10-00-00')]);
  for (const r of ['2026-06-26_10-00-00', '2026-06-27_10-00-00']) {
    fs.mkdirSync(runDir(p, r), { recursive: true });
    fs.writeFileSync(path.join(runDir(p, r), 'run.json'), JSON.stringify({ schemaVersion: '3', run: r }));
  }
  const d = run(p, ['diagnose', '--id', 'evt_a']);
  assert.equal(d.stale, true);
  assert.equal(d.recurrence.seenInNewestRun, false);
  assert.equal(d.recurrence.runsSince, 2);
  assert.equal(typeof d.ageDays, 'number');
  const l = run(p, ['list']).events[0];
  assert.equal(l.stale, true);
  assert.equal(l.lastSeenRun, '2026-06-25_10-00-00');
  assert.equal(run(p, ['runs']).runs[0].eventCount, 0);
});

test('reviews.json is the whole truth: a reopened event is open even if its record says fixed', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z', 'fixed')]);
  assert.equal(run(p, ['diagnose', '--id', 'evt_a']).reviewStatus, 'fixed'); // no reviews.json yet: the record decides
  fs.writeFileSync(path.join(baseDir(p), 'reviews.json'), JSON.stringify({ items: [] }));
  assert.equal(run(p, ['diagnose', '--id', 'evt_a']).reviewStatus, 'open');
  assert.equal(run(p, ['list']).events[0].reviewStatus, 'open');
  assert.equal(run(p, ['diagnose', '--last']).eventId, 'evt_a');
});

test('fixed, then seen again below the regression line: shown, still muted', () => {
  const p = makeProject([sampleEvent('evt_a', 'gc_spike', '2026-06-25T10:00:00Z')]);
  fs.writeFileSync(
    path.join(baseDir(p), 'reviews.json'),
    JSON.stringify({
      items: [
        {
          id: 'evt_a',
          status: 'fixed',
          value: 5000,
          markedAt: '2026-06-25T11:00:00Z',
          seenAgainCount: 4,
          seenAgainRun: '2026-06-25_10-00-00',
          seenAgainValue: 1200,
          helped: 'yes',
        },
      ],
    }),
  );
  const d = run(p, ['diagnose', '--id', 'evt_a']);
  assert.equal(d.reviewStatus, 'fixed');
  assert.deepEqual(d.review, {
    status: 'fixed',
    markedAt: '2026-06-25T11:00:00Z',
    fixedAtPeak: 5000,
    seenAgainAfterFixed: { count: 4, lastRun: '2026-06-25_10-00-00', maxValue: 1200 },
    diagnosisHelped: 'yes',
  });
  assert.deepEqual(run(p, ['list']).events[0].seenAgainAfterFixed, { count: 4, lastRun: '2026-06-25_10-00-00', maxValue: 1200 });
  assert.equal(run(p, ['diagnose', '--last']).status, 'no_data');
});

test('reference: prints the field reference the slim guide points at', () => {
  const out = execFileSync('node', [CLI, 'reference'], { encoding: 'utf8' });
  assert.match(out, /^# Profilot - field reference/);
  assert.match(out, /frameMatchesTrigger/);
  assert.match(shippedGuide(), /profilot reference/);
});

test('package: the npm files and prepack carry the guide and the field reference', () => {
  const pkg = require('./package.json');
  for (const f of ['index.js', 'profilot-diagnosis-guide.md', 'profilot-field-reference.md']) assert.ok(pkg.files.includes(f), f);
  for (const f of ['profilot-diagnosis-guide.md', 'profilot-field-reference.md', 'LICENSE']) assert.ok(pkg.scripts.prepack.includes(f), f);
  assert.ok(fs.existsSync(path.join(__dirname, '..', 'profilot-field-reference.md')));
});

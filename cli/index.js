#!/usr/bin/env node
'use strict';

// Profilot CLI (SPEC.md section 14). Pure transport: it reads the per-run file store the Unity
// editor layer writes (Library/Profilot/runs/<run>/evt_*.json, plus a top-level latest.json and
// reviews.json) and prints structured JSON to stdout for Claude Code. It never calls an LLM and
// never writes to the store (`init`, the one-time setup, writes only project config files).
// Every command exits 0; failures are reported as a "status" field.
//
// The one thing it does beyond copying bytes is choose how much of the stored marker tree to
// print. The Unity side captures the tree deep enough to get past Unity's ~7 levels of PlayerLoop
// scaffolding and reach user code; printing all of that by default would bloat every payload, so
// `diagnose` prints a shallow slice unless asked otherwise:
//   --focus <markerName>   re-root at a marker (substring match) and print its whole subtree
//   --depth <n>            print n levels instead of the default

const fs = require('fs');
const path = require('path');

function print(obj) {
  process.stdout.write(JSON.stringify(obj, null, 2) + '\n');
}

// Resolve Library/Profilot (holds runs/, latest.json, reviews.json).
function findBase() {
  if (process.env.PROFILOT_PROJECT) {
    const c = path.join(process.env.PROFILOT_PROJECT, 'Library', 'Profilot');
    return fs.existsSync(c) ? c : null;
  }
  let dir = process.cwd();
  for (;;) {
    const c = path.join(dir, 'Library', 'Profilot');
    if (fs.existsSync(c)) return c;
    const parent = path.dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function runsRoot(base) {
  return path.join(base, 'runs');
}

// Run ids, newest first (folder names are yyyy-MM-dd_HH-mm-ss, so a string sort is chronological).
function listRuns(base) {
  const root = runsRoot(base);
  if (!fs.existsSync(root)) return [];
  return fs
    .readdirSync(root, { withFileTypes: true })
    .filter((d) => d.isDirectory())
    .map((d) => d.name)
    .sort((a, b) => (a < b ? 1 : a > b ? -1 : 0));
}

function runEventFiles(base, run) {
  const dir = path.join(runsRoot(base), run);
  if (!fs.existsSync(dir)) return [];
  return fs
    .readdirSync(dir)
    .filter((f) => f.startsWith('evt_') && f.endsWith('.json') && !f.endsWith('.tmp'));
}

// Cross-run review decisions (eventId -> status), applied over the per-file value.
function reviewMap(base) {
  const map = {};
  const p = path.join(base, 'reviews.json');
  if (!fs.existsSync(p)) return map;
  try {
    const data = readJson(p);
    for (const it of data.items || []) if (it && it.id) map[it.id] = it.status;
  } catch (_) {
    // a corrupt reviews file just means no overlay
  }
  return map;
}

// "stale" = the record is from a run that is not the newest one. The Unity side cannot know
// this when it writes (it always wrote false), so the CLI decides it at read time.
function withStale(rec, run, latestRun) {
  if (rec && typeof rec === 'object') rec.stale = !!latestRun && run !== latestRun;
  return rec;
}

function summarize(rec, run, reviews, latestRun) {
  const trigger = rec.trigger || {};
  return {
    eventId: rec.eventId,
    run,
    stale: !!latestRun && run !== latestRun,
    type: trigger.type,
    severity: trigger.severity,
    capturedAt: rec.capturedAt,
    reviewStatus: reviews[rec.eventId] || rec.reviewStatus || 'open',
    sessionId: rec.sessionId,
  };
}

function withReview(rec, reviews) {
  if (rec && rec.eventId && reviews[rec.eventId]) rec.reviewStatus = reviews[rec.eventId];
  return rec;
}

// How much of the stored marker tree `diagnose` prints when nothing is asked for. The Unity
// side captures the tree far deeper than this (markerTreeDepth in the record) so that --focus
// and --depth have something to drill into; this keeps the DEFAULT payload byte-for-byte what
// it has always been, so existing consumers see no change.
const DEFAULT_PRINT_DEPTH = 6;

// Returns a copy of `node` keeping at most `depth` levels of children below it (depth 0 = the
// node alone). A node whose children are dropped is marked truncated, matching how the capture
// marks its own cuts, so a trimmed node never reads as a genuine leaf.
function trimTree(node, depth) {
  if (!node || typeof node !== 'object') return node;
  const { children, ...rest } = node;
  if (!Array.isArray(children) || children.length === 0) return node;
  if (depth <= 0) return { ...rest, truncated: rest.truncated || 'depth' };
  return { ...rest, children: children.map((c) => trimTree(c, depth - 1)) };
}

// Deepest level actually present in a tree, so the output can say what it had to work with.
function treeDepth(node) {
  if (!node || !Array.isArray(node.children) || node.children.length === 0) return 0;
  let d = 0;
  for (const c of node.children) d = Math.max(d, treeDepth(c));
  return d + 1;
}

// Every node whose name contains `needle` (case-insensitive), each with the marker path from the
// root. Substring is the right matcher here: real Unity marker names are long and decorated
// ("Assembly-CSharp.dll!::TeamsScreen.Awake() [Invoke]"), so nobody types them exactly.
function findMatches(node, needle, path = [], out = []) {
  if (!node || typeof node !== 'object') return out;
  const name = String(node.name || '');
  if (name.toLowerCase().includes(needle)) out.push({ node, path, depth: path.length });
  if (Array.isArray(node.children)) {
    const here = path.concat(name);
    for (const c of node.children) findMatches(c, needle, here, out);
  }
  return out;
}

function markerNames(node, out = []) {
  if (!node || typeof node !== 'object') return out;
  if (node.name) out.push({ name: String(node.name), totalTimeMs: node.totalTimeMs || 0 });
  if (Array.isArray(node.children)) for (const c of node.children) markerNames(c, out);
  return out;
}

// Distinct marker names in the tree, heaviest first - what we hand back when a --focus query
// matches nothing, so the retry is one step instead of a guess.
function focusCandidates(tree) {
  const seen = new Set();
  const out = [];
  for (const m of markerNames(tree).sort((a, b) => (b.totalTimeMs || 0) - (a.totalTimeMs || 0))) {
    if (seen.has(m.name)) continue;
    seen.add(m.name);
    out.push(m.name);
    if (out.length === 40) break;
  }
  return out;
}

// Applies --focus / --depth to a full event record. Focus re-roots the tree at a marker and hands
// back its whole subtree; depth trims. Both work on the STORED tree, so the answer is only as deep
// as the capture went - markerTreeDepth and any "truncated" node say exactly where that is.
function shapeTree(rec, opts) {
  if (!rec || !('markerTree' in rec)) return rec;

  const depth = opts.depth;
  const focus = opts.focus;
  const tree = rec.markerTree;

  if (focus) {
    if (!tree) {
      return {
        ...rec,
        status: 'error',
        message: `Event ${rec.eventId} has no marker tree (status "${rec.status}"), so --focus has nothing to search.`,
      };
    }
    const matches = findMatches(tree, focus.toLowerCase());
    if (matches.length === 0) {
      return {
        ...rec,
        status: 'error',
        message:
          `No marker in ${rec.eventId} matches "${focus}". The tree was captured to depth ` +
          `${rec.markerTreeDepth != null ? rec.markerTreeDepth : 'unknown'}; a marker below that is not in ` +
          `this record. See "focusCandidates" for names that are.`,
        focusCandidates: focusCandidates(tree),
        markerTree: trimTree(tree, depth == null ? DEFAULT_PRINT_DEPTH : depth),
      };
    }
    // Most significant match wins: heaviest subtree, then shallowest. A substring like "Awake"
    // legitimately hits many nodes, so the runners-up are listed and the choice stays visible.
    const ranked = matches
      .slice()
      .sort((a, b) => (b.node.totalTimeMs || 0) - (a.node.totalTimeMs || 0) || a.depth - b.depth);
    const best = ranked[0];
    const subtree = depth == null ? best.node : trimTree(best.node, depth);
    return {
      ...rec,
      focus: {
        query: focus,
        matched: best.node.name,
        path: best.path,
        depth: best.depth,
        subtreeDepth: treeDepth(best.node),
        matchCount: matches.length,
        ...(ranked.length > 1
          ? {
              otherMatches: ranked.slice(1, 10).map((m) => ({
                name: m.node.name,
                depth: m.depth,
                totalTimeMs: m.node.totalTimeMs,
              })),
            }
          : {}),
      },
      markerTree: subtree,
    };
  }

  const want = depth == null ? DEFAULT_PRINT_DEPTH : depth;
  const out = { ...rec, markerTree: trimTree(tree, want) };
  if (depth != null && rec.markerTreeDepth != null && depth > rec.markerTreeDepth) {
    out.depthNote =
      `Requested depth ${depth} is deeper than the captured depth ${rec.markerTreeDepth}; the tree ends ` +
      `where the capture cut it ("truncated": "depth").`;
  }
  return out;
}

// A record served from a run that is NOT the newest one. This is the silent trap: `diagnose --id X`
// falls back to the newest run that still CONTAINS X, so once a fix lands and the event stops being
// captured, you keep reading the old run's numbers and conclude "no change". Say so, loudly.
function runFallback(rec, resolvedRun, runs) {
  const latestRun = runs[0];
  if (!rec || !latestRun || resolvedRun === latestRun) return rec;
  const newer = runs.indexOf(resolvedRun);
  return {
    ...rec,
    runFallback: true,
    resolvedRun,
    latestRun,
    warning:
      `This record is from run ${resolvedRun}, not the newest run ${latestRun}` +
      (newer > 0 ? ` (${newer} newer run${newer === 1 ? '' : 's'})` : '') +
      `. ${rec.eventId} was not captured in the newest run, which usually means the problem stopped ` +
      `reproducing - do NOT read these numbers as the current state. Confirm with ` +
      `\`profilot list --run ${latestRun}\`.`,
  };
}

function cmdStatus(base) {
  if (!base) {
    return {
      status: 'no_data',
      message:
        'No Profilot store found. Enter Play Mode in the Unity Editor with Profilot installed so the tripwire can capture events.',
    };
  }
  const runs = listRuns(base);
  let eventCount = 0;
  for (const r of runs) eventCount += runEventFiles(base, r).length;
  let latest = null;
  const lp = path.join(base, 'latest.json');
  if (fs.existsSync(lp)) {
    try {
      const p = readJson(lp);
      latest = { eventId: p.eventId, run: p.run };
    } catch (_) {
      latest = null;
    }
  }
  return { status: runs.length > 0 ? 'ok' : 'no_data', base, runCount: runs.length, eventCount, latest };
}

function cmdRuns(base) {
  if (!base) return cmdStatus(base);
  const runs = listRuns(base).map((r) => ({ run: r, eventCount: runEventFiles(base, r).length }));
  return { status: 'ok', count: runs.length, runs };
}

function cmdList(base, runFilter) {
  if (!base) return cmdStatus(base);
  const reviews = reviewMap(base);
  const allRuns = listRuns(base);
  const runs = runFilter ? [runFilter] : allRuns;
  const events = [];
  for (const r of runs) {
    for (const f of runEventFiles(base, r)) {
      try {
        events.push(summarize(readJson(path.join(runsRoot(base), r, f)), r, reviews, allRuns[0]));
      } catch (_) {
        // skip a half-written or malformed file rather than failing the whole listing
      }
    }
  }
  events.sort((a, b) => String(b.capturedAt).localeCompare(String(a.capturedAt)));
  return { status: 'ok', count: events.length, events };
}

function cmdDiagnoseLast(base, opts) {
  if (!base) return cmdStatus(base);
  const lp = path.join(base, 'latest.json');
  if (!fs.existsSync(lp)) {
    return { status: 'no_data', message: 'No events captured yet.' };
  }
  let pointer;
  try {
    pointer = readJson(lp);
  } catch (e) {
    return { status: 'error', message: `latest.json is unreadable: ${e.message}` };
  }
  const file = path.join(runsRoot(base), pointer.run || '', pointer.file || `${pointer.eventId}.json`);
  if (!fs.existsSync(file)) {
    return { status: 'error', message: `Event file missing for ${pointer.eventId}.` };
  }
  try {
    const runs = listRuns(base);
    const rec = withStale(withReview(readJson(file), reviewMap(base)), pointer.run, runs[0]);
    // latest.json is the last event WRITTEN, which can predate the newest run entirely - a run
    // that captured nothing leaves the pointer on the previous one. Same trap as --id.
    return shapeTree(runFallback(rec, pointer.run, runs), opts);
  } catch (e) {
    return { status: 'error', message: `Event ${pointer.eventId} is unreadable: ${e.message}` };
  }
}

// --id resolves to the newest run containing that event, unless --run pins a specific run.
function cmdDiagnoseId(base, id, runFilter, opts) {
  if (!base) return cmdStatus(base);
  if (!id) return { status: 'error', message: 'Missing --id <eventId>.' };
  const name = id.endsWith('.json') ? id : `${id}.json`;
  const allRuns = listRuns(base);
  const runs = runFilter ? [runFilter] : allRuns;
  for (const r of runs) {
    const file = path.join(runsRoot(base), r, name);
    if (fs.existsSync(file)) {
      try {
        const rec = withStale(withReview(readJson(file), reviewMap(base)), r, allRuns[0]);
        // Only warn on an IMPLICIT fallback. With --run the caller chose the run, so an older
        // one is the answer they asked for, not a surprise.
        return shapeTree(runFilter ? rec : runFallback(rec, r, allRuns), opts);
      } catch (e) {
        return { status: 'error', message: `Event ${id} is unreadable: ${e.message}` };
      }
    }
  }
  return { status: 'error', message: `No event with id ${id}${runFilter ? ` in run ${runFilter}` : ''}.` };
}

// --depth <n> / --focus <markerName>, the two knobs that decide how much of the stored tree gets
// printed. Both are validated here so a typo comes back as a clear message instead of a silently
// ignored flag.
function readShapeFlags(argv) {
  const opts = { depth: null, focus: null };

  const rawDepth = getFlag(argv, '--depth');
  if (rawDepth === true) return { ...opts, error: 'Missing value for --depth <n>.' };
  if (rawDepth !== undefined) {
    const n = Number(rawDepth);
    if (!Number.isInteger(n) || n < 0) {
      return { ...opts, error: `Invalid --depth "${rawDepth}": expected a non-negative integer.` };
    }
    opts.depth = n;
  }

  const rawFocus = getFlag(argv, '--focus');
  if (rawFocus === true) return { ...opts, error: 'Missing value for --focus <markerName>.' };
  if (rawFocus !== undefined) {
    if (!String(rawFocus).trim()) return { ...opts, error: 'Empty value for --focus <markerName>.' };
    opts.focus = String(rawFocus);
  }

  return opts;
}

function getFlag(argv, name) {
  const i = argv.indexOf(name);
  if (i === -1) return undefined;
  const next = argv[i + 1];
  return next && !next.startsWith('--') ? next : true;
}

// --- init: one-step install into a Unity project ------------------------------------------------
// The only command that writes, and it never touches the event store. It wires a Unity project up
// in the three places Profilot needs: the UPM dependency in Packages/manifest.json, the diagnosis
// guide under .claude/, and an @import of that guide in CLAUDE.md. Every step is idempotent, so
// re-running it after an upgrade just refreshes the guide.

const PACKAGE_NAME = 'com.profilot.profilot';
const PACKAGE_URL = 'https://github.com/orbenozio/unity-profilot.git?path=/Packages/com.profilot.profilot#release';
const GUIDE_FILE = 'profilot-diagnosis-guide.md';
const GUIDE_IMPORT = `@.claude/${GUIDE_FILE}`;

// The published package carries the guide next to index.js (copied in by prepack); a clone run
// through `npm link` reads it from the repo root instead, so there is one source of truth.
function findGuide() {
  for (const p of [path.join(__dirname, GUIDE_FILE), path.join(__dirname, '..', GUIDE_FILE)]) {
    if (fs.existsSync(p)) return p;
  }
  return null;
}

// A Unity project root is a folder with Packages/manifest.json and ProjectSettings/. Library/ is
// not a usable marker here: it may not exist yet on a fresh clone.
function findUnityRoot(start) {
  let dir = path.resolve(start);
  for (;;) {
    if (fs.existsSync(path.join(dir, 'Packages', 'manifest.json')) && fs.existsSync(path.join(dir, 'ProjectSettings'))) {
      return dir;
    }
    const parent = path.dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

function initManifest(root, skip) {
  if (skip) return { step: 'package', result: 'skipped' };
  const file = path.join(root, 'Packages', 'manifest.json');
  let manifest;
  try {
    manifest = readJson(file);
  } catch (e) {
    return { step: 'package', result: 'error', message: `Packages/manifest.json is unreadable: ${e.message}` };
  }
  manifest.dependencies = manifest.dependencies || {};
  const existing = manifest.dependencies[PACKAGE_NAME];
  if (existing) return { step: 'package', result: 'unchanged', value: existing };
  manifest.dependencies[PACKAGE_NAME] = PACKAGE_URL;
  fs.writeFileSync(file, JSON.stringify(manifest, null, 2) + '\n');
  return { step: 'package', result: 'added', value: PACKAGE_URL };
}

function initGuide(root) {
  const src = findGuide();
  if (!src) return { step: 'guide', result: 'error', message: `${GUIDE_FILE} is missing from this install.` };
  const dest = path.join(root, '.claude', GUIDE_FILE);
  const content = fs.readFileSync(src, 'utf8');
  const before = fs.existsSync(dest) ? fs.readFileSync(dest, 'utf8') : null;
  if (before === content) return { step: 'guide', result: 'unchanged', path: dest };
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  fs.writeFileSync(dest, content);
  return { step: 'guide', result: before === null ? 'added' : 'updated', path: dest };
}

function initClaudeMd(root) {
  const file = path.join(root, 'CLAUDE.md');
  const before = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
  if (before.includes(GUIDE_IMPORT)) return { step: 'claudeMd', result: 'unchanged', path: file };
  const section = `## Profilot\n\nPerformance diagnosis with Profilot - see the guide:\n\n${GUIDE_IMPORT}\n`;
  const sep = before === '' ? '' : before.endsWith('\n\n') ? '' : before.endsWith('\n') ? '\n' : '\n\n';
  fs.writeFileSync(file, before + sep + section);
  return { step: 'claudeMd', result: before === '' ? 'created' : 'added', path: file };
}

function cmdInit(argv) {
  const pathFlag = getFlag(argv, '--path');
  if (pathFlag === true) return { status: 'error', message: 'Missing value for --path <unityProjectRoot>.' };
  const start = pathFlag || process.env.PROFILOT_PROJECT || process.cwd();
  const root = findUnityRoot(start);
  if (!root) {
    return {
      status: 'error',
      message: `No Unity project found at or above ${path.resolve(start)} (looked for Packages/manifest.json + ProjectSettings/). Run this from the Unity project folder, or pass --path.`,
    };
  }
  const steps = [initManifest(root, argv.includes('--no-package')), initGuide(root), initClaudeMd(root)];
  const failed = steps.filter((s) => s.result === 'error');
  return {
    status: failed.length ? 'error' : 'ok',
    projectRoot: root,
    steps,
    ...(failed.length ? { message: failed.map((s) => s.message).join(' ') } : {}),
    next:
      'Open the project in Unity (6000.3+) so Package Manager resolves Profilot, enter Play Mode and ' +
      'reproduce the problem, then ask Claude Code to "diagnose the last Profilot event".',
  };
}

function main() {
  const argv = process.argv.slice(2);
  const command = argv[0];
  if (command === '--version' || command === 'version') {
    print({ status: 'ok', version: require('./package.json').version });
    return;
  }
  const base = findBase();
  const runFlag = getFlag(argv, '--run');
  const run = typeof runFlag === 'string' ? runFlag : undefined;

  let result;
  switch (command) {
    case 'status':
      result = cmdStatus(base);
      break;
    case 'runs':
      result = cmdRuns(base);
      break;
    case 'list':
      result = cmdList(base, run);
      break;
    case 'init':
      result = cmdInit(argv);
      break;
    case 'diagnose': {
      const id = getFlag(argv, '--id');
      const opts = readShapeFlags(argv);
      if (opts.error) {
        result = { status: 'error', message: opts.error };
        break;
      }
      if (typeof id === 'string') result = cmdDiagnoseId(base, id, run, opts);
      else result = cmdDiagnoseLast(base, opts); // --last is the default
      break;
    }
    default:
      result = {
        status: 'error',
        message: `Unknown command "${command || ''}". Usage: profilot <diagnose [--last|--id <eventId>] [--run <id>] [--focus <markerName>] [--depth <n>]|list [--run <id>]|runs|status|init [--path <dir>] [--no-package]|--version>.`,
      };
  }

  print(result);
  // Not process.exit(): on macOS/Linux a piped stdout is async, and exiting right after the write
  // cut every payload over 64 KB mid-JSON (exit 0, unparseable output). Let the write drain.
  process.exitCode = 0;
}

main();

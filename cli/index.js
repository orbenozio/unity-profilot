#!/usr/bin/env node
'use strict';

// Profilot CLI (SPEC.md section 14). Pure transport: it reads the per-run file store the Unity
// editor layer writes (Library/Profilot/runs/<run>/evt_*.json, plus a top-level latest.json and
// reviews.json) and prints structured JSON to stdout for Claude Code. It never calls an LLM and
// never writes to the store. Every command exits 0; failures are reported as a "status" field.

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

function summarize(rec, run, reviews) {
  const trigger = rec.trigger || {};
  return {
    eventId: rec.eventId,
    run,
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
  const runs = runFilter ? [runFilter] : listRuns(base);
  const events = [];
  for (const r of runs) {
    for (const f of runEventFiles(base, r)) {
      try {
        events.push(summarize(readJson(path.join(runsRoot(base), r, f)), r, reviews));
      } catch (_) {
        // skip a half-written or malformed file rather than failing the whole listing
      }
    }
  }
  events.sort((a, b) => String(b.capturedAt).localeCompare(String(a.capturedAt)));
  return { status: 'ok', count: events.length, events };
}

function cmdDiagnoseLast(base) {
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
    return withReview(readJson(file), reviewMap(base));
  } catch (e) {
    return { status: 'error', message: `Event ${pointer.eventId} is unreadable: ${e.message}` };
  }
}

// --id resolves to the newest run containing that event, unless --run pins a specific run.
function cmdDiagnoseId(base, id, runFilter) {
  if (!base) return cmdStatus(base);
  if (!id) return { status: 'error', message: 'Missing --id <eventId>.' };
  const name = id.endsWith('.json') ? id : `${id}.json`;
  const runs = runFilter ? [runFilter] : listRuns(base);
  for (const r of runs) {
    const file = path.join(runsRoot(base), r, name);
    if (fs.existsSync(file)) {
      try {
        return withReview(readJson(file), reviewMap(base));
      } catch (e) {
        return { status: 'error', message: `Event ${id} is unreadable: ${e.message}` };
      }
    }
  }
  return { status: 'error', message: `No event with id ${id}${runFilter ? ` in run ${runFilter}` : ''}.` };
}

function getFlag(argv, name) {
  const i = argv.indexOf(name);
  if (i === -1) return undefined;
  const next = argv[i + 1];
  return next && !next.startsWith('--') ? next : true;
}

function main() {
  const argv = process.argv.slice(2);
  const command = argv[0];
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
    case 'diagnose': {
      const id = getFlag(argv, '--id');
      if (typeof id === 'string') result = cmdDiagnoseId(base, id, run);
      else result = cmdDiagnoseLast(base); // --last is the default
      break;
    }
    default:
      result = {
        status: 'error',
        message: `Unknown command "${command || ''}". Usage: profilot <diagnose [--last|--id <eventId>] [--run <id>]|list [--run <id>]|runs|status>.`,
      };
  }

  print(result);
  process.exit(0);
}

main();

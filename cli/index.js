#!/usr/bin/env node
'use strict';

// Profilot CLI (SPEC.md section 14). Pure transport: it reads the file-based event store
// the Unity editor layer writes (Library/Profilot/events) and prints structured JSON to
// stdout for Claude Code. It never calls an LLM and never writes to the store - the
// interpretation is Claude Code's job. Every command exits 0; failures are reported as a
// "status" field inside the JSON (SPEC.md section 13).

const fs = require('fs');
const path = require('path');

function print(obj) {
  process.stdout.write(JSON.stringify(obj, null, 2) + '\n');
}

// Resolve Library/Profilot/events: explicit env wins, else walk up from cwd to the project.
function findEventsDir() {
  if (process.env.PROFILOT_PROJECT) {
    return path.join(process.env.PROFILOT_PROJECT, 'Library', 'Profilot', 'events');
  }
  let dir = process.cwd();
  for (;;) {
    const candidate = path.join(dir, 'Library', 'Profilot', 'events');
    if (fs.existsSync(candidate)) return candidate;
    const parent = path.dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function eventFiles(eventsDir) {
  return fs
    .readdirSync(eventsDir)
    .filter((f) => f.startsWith('evt_') && f.endsWith('.json') && !f.endsWith('.tmp'));
}

function summarize(rec) {
  const trigger = rec.trigger || {};
  return {
    eventId: rec.eventId,
    type: trigger.type,
    severity: trigger.severity,
    capturedAt: rec.capturedAt,
    reviewStatus: rec.reviewStatus,
    stale: rec.stale,
  };
}

function cmdStatus(eventsDir) {
  if (!eventsDir) {
    return {
      status: 'no_data',
      message:
        'No Profilot event store found. Enter Play Mode in the Unity Editor with Profilot installed so the tripwire can capture events.',
    };
  }
  const files = eventFiles(eventsDir);
  let latest = null;
  const latestPath = path.join(eventsDir, 'latest.json');
  if (fs.existsSync(latestPath)) {
    try {
      latest = readJson(latestPath).eventId;
    } catch (_) {
      latest = null;
    }
  }
  return {
    status: files.length > 0 ? 'ok' : 'no_data',
    eventsDir,
    eventCount: files.length,
    latest,
  };
}

function cmdList(eventsDir) {
  if (!eventsDir) return cmdStatus(eventsDir);
  const events = [];
  for (const f of eventFiles(eventsDir)) {
    try {
      events.push(summarize(readJson(path.join(eventsDir, f))));
    } catch (_) {
      // Skip a half-written or malformed file rather than failing the whole listing.
    }
  }
  events.sort((a, b) => String(b.capturedAt).localeCompare(String(a.capturedAt)));
  return { status: 'ok', count: events.length, events };
}

function cmdDiagnoseLast(eventsDir) {
  if (!eventsDir) return cmdStatus(eventsDir);
  const latestPath = path.join(eventsDir, 'latest.json');
  if (!fs.existsSync(latestPath)) {
    return { status: 'no_data', message: 'No events captured yet in this session.' };
  }
  let pointer;
  try {
    pointer = readJson(latestPath);
  } catch (e) {
    return { status: 'error', message: `latest.json is unreadable: ${e.message}` };
  }
  const file = path.join(eventsDir, pointer.file || `${pointer.eventId}.json`);
  if (!fs.existsSync(file)) {
    return { status: 'error', message: `Event file missing for ${pointer.eventId}.` };
  }
  try {
    return readJson(file);
  } catch (e) {
    return { status: 'error', message: `Event ${pointer.eventId} is unreadable: ${e.message}` };
  }
}

function cmdDiagnoseId(eventsDir, id) {
  if (!eventsDir) return cmdStatus(eventsDir);
  if (!id) return { status: 'error', message: 'Missing --id <eventId>.' };
  const name = id.endsWith('.json') ? id : `${id}.json`;
  const file = path.join(eventsDir, name);
  if (!fs.existsSync(file)) {
    return { status: 'error', message: `No event with id ${id}.` };
  }
  try {
    return readJson(file);
  } catch (e) {
    return { status: 'error', message: `Event ${id} is unreadable: ${e.message}` };
  }
}

function getFlag(argv, name) {
  const i = argv.indexOf(name);
  if (i === -1) return undefined;
  // boolean flag if no value follows or the next token is another flag
  const next = argv[i + 1];
  return next && !next.startsWith('--') ? next : true;
}

function main() {
  const argv = process.argv.slice(2);
  const command = argv[0];
  const eventsDir = findEventsDir();

  let result;
  switch (command) {
    case 'status':
      result = cmdStatus(eventsDir);
      break;
    case 'list':
      result = cmdList(eventsDir);
      break;
    case 'diagnose': {
      const id = getFlag(argv, '--id');
      if (typeof id === 'string') result = cmdDiagnoseId(eventsDir, id);
      else result = cmdDiagnoseLast(eventsDir); // --last is the default
      break;
    }
    default:
      result = {
        status: 'error',
        message: `Unknown command "${command || ''}". Usage: profilot <diagnose [--last|--id <eventId>]|list|status>.`,
      };
  }

  print(result);
  process.exit(0);
}

main();

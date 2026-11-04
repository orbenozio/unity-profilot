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

// A reader that closes the pipe early (`| head`) makes the next write fail with EPIPE. Without a
// handler Node throws it as an unhandled 'error' event (stack trace, exit 1), which breaks the
// "every command exits 0" contract. The reader is gone, so there is nothing left to tell it.
process.stdout.on('error', (e) => {
  if (e && e.code === 'EPIPE') return;
  throw e;
});

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

// Run folders are named yyyy-MM-dd_HH-mm-ss, so a string sort is chronological.
const RUN_PATTERN = /^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$/;

// Run ids, newest first. A folder with any other name (a domain reload once wrote into "session")
// would sort above every dated run and pose as the newest one, so those go last, never first.
function listRuns(base) {
  const root = runsRoot(base);
  if (!fs.existsSync(root)) return [];
  const desc = (a, b) => (a < b ? 1 : a > b ? -1 : 0);
  const names = fs
    .readdirSync(root, { withFileTypes: true })
    .filter((d) => d.isDirectory())
    .map((d) => d.name);
  return names.filter((n) => RUN_PATTERN.test(n)).sort(desc).concat(names.filter((n) => !RUN_PATTERN.test(n)).sort(desc));
}

function runEventFiles(base, run) {
  const dir = path.join(runsRoot(base), run);
  if (!fs.existsSync(dir)) return [];
  return fs
    .readdirSync(dir)
    .filter((f) => f.startsWith('evt_') && f.endsWith('.json') && !f.endsWith('.tmp'));
}

// --id and --run end up in a filesystem path, so they are checked as names first (no path
// separators) and the joined path must stay inside the store: `--id ../../reviews` used to return
// reviews.json as an event. Letters are Unicode and ".." is allowed inside a name, because the
// package writes ids such as evt_frame_hitch_Foo..ctor and evt_gc_spike_Über.Update.
const NAME_PATTERN = /^[\p{L}\p{N}][\p{L}\p{N}._-]*$/u;

function validName(s) {
  return typeof s === 'string' && NAME_PATTERN.test(s);
}

function inside(root, file) {
  const r = path.resolve(root);
  const f = path.resolve(file);
  return f.startsWith(r + path.sep);
}

function checkId(id) {
  const bare = id.endsWith('.json') ? id.slice(0, -5) : id;
  if (validName(bare)) return null;
  return `Invalid --id "${id}": event ids look like evt_<type>_<marker> (letters, digits, ".", "_", "-").`;
}

function checkRun(base, run) {
  const runs = base ? listRuns(base) : [];
  if (validName(run) && runs.includes(run)) return null;
  return {
    status: 'error',
    message: `No run "${run}".` + (runs.length ? ' Pick one of "runs" (newest first).' : ' There are no runs yet.'),
    runs: runs.slice(0, 20),
  };
}

// Before 0.7 the review buttons wrote "reviewed" / "not_a_real_issue"; the package maps them to
// the new pair when it next saves, the CLI maps them on read so both agree meanwhile.
const LEGACY_REVIEW = { reviewed: 'fixed', not_a_real_issue: 'ignored' };
const mapStatus = (s) => LEGACY_REVIEW[s] || s;

// Cross-run review decisions, keyed by eventId. Statuses: open, fixed (muted, alerts again if it
// comes back much worse), regressed, ignored. When reviews.json exists it is the whole truth: an
// event missing from it is open. Reopen removes the entry, and falling back to the status baked
// into the record then brought back "fixed" / "ignored" for an event the user had reopened.
function reviewMap(base) {
  const p = path.join(base, 'reviews.json');
  if (!fs.existsSync(p)) return { authoritative: false, items: {} };
  try {
    const items = {};
    for (const it of readJson(p).items || []) if (it && it.id && it.status) items[it.id] = { ...it, status: mapStatus(it.status) };
    return { authoritative: true, items };
  } catch (_) {
    // a corrupt reviews file just means no overlay
    return { authoritative: false, items: {} };
  }
}

function reviewStatusOf(rec, reviews) {
  const it = reviews.items[rec.eventId];
  if (it) return it.status;
  if (reviews.authoritative) return 'open';
  return mapStatus(rec.reviewStatus) || 'open';
}

// The decision in full, for diagnose: when a fixed problem was marked, at what peak, and whether
// it has been seen again since (below the regression line it stays muted, but not invisible).
function reviewDetail(rec, reviews) {
  const it = reviews.items[rec.eventId];
  if (!it) return undefined;
  const out = { status: it.status };
  if (it.markedAt) out.markedAt = it.markedAt;
  if (it.status === 'fixed' || it.status === 'regressed') {
    if (it.value > 0) out.fixedAtPeak = it.value;
    if (it.seenAgainCount > 0) {
      out.seenAgainAfterFixed = { count: it.seenAgainCount, lastRun: it.seenAgainRun, maxValue: it.seenAgainValue };
    }
  }
  if (it.helped) out.diagnosisHelped = it.helped;
  return out;
}

// What one read needs to know about the whole store: the runs (newest first), the review
// decisions, and in which runs each event was caught.
// A run whose run.json has no endedAt is still in Play (or ended abruptly). Not being caught in
// it yet is not evidence of a fix, so recurrence measures against the newest FINISHED run then.
function runInProgress(base, run) {
  const p = path.join(runsRoot(base), run, 'run.json');
  if (!fs.existsSync(p)) return false;
  try {
    return !readJson(p).endedAt;
  } catch (_) {
    return false;
  }
}

function storeContext(base) {
  const runs = listRuns(base);
  const seenIn = new Map();
  for (const r of runs) {
    for (const f of runEventFiles(base, r)) {
      const id = f.slice(0, -5);
      if (!seenIn.has(id)) seenIn.set(id, []);
      seenIn.get(id).push(r);
    }
  }
  const inProgress = runs.length > 0 && runInProgress(base, runs[0]);
  return { runs, newestRun: runs[0], newestInProgress: inProgress, reviews: reviewMap(base), seenIn };
}

// Recurrence is per EVENT, not per record: "is this problem still happening?" is answered by the
// newest run that caught it. Since 0.8 every Play leaves a run folder, so a clean run counts as
// the newest run and an event missing from it really did not come back.
// The run "is it still happening?" is judged against. Caught in the run still in Play: that run.
// Not caught there yet: the newest finished run, because the open run is not a clean one yet.
function recurrenceReference(ctx, lastSeenRun) {
  return ctx.newestInProgress && lastSeenRun !== ctx.newestRun && ctx.runs.length > 1 ? ctx.runs[1] : ctx.newestRun;
}

function recurrence(eventId, ctx) {
  const seen = ctx.seenIn.get(eventId) || [];
  const lastSeenRun = seen[0];
  const idx = lastSeenRun ? ctx.runs.indexOf(lastSeenRun) : -1;
  const reference = recurrenceReference(ctx, lastSeenRun);
  return {
    lastSeenRun,
    newestRun: ctx.newestRun,
    comparedToRun: reference,
    ...(ctx.newestInProgress ? { newestRunInProgress: true } : {}),
    seenInNewestRun: !!lastSeenRun && lastSeenRun === reference,
    runsSeen: seen.length,
    runsSince: idx < 0 ? undefined : ctx.newestInProgress && lastSeenRun !== ctx.newestRun ? Math.max(0, idx - 1) : idx,
  };
}

function ageDays(capturedAt) {
  const t = Date.parse(capturedAt);
  return Number.isFinite(t) ? Math.round(((Date.now() - t) / 864e5) * 10) / 10 : undefined;
}

function summarize(rec, run, ctx) {
  const trigger = rec.trigger || {};
  const rc = recurrence(rec.eventId, ctx);
  const review = reviewDetail(rec, ctx.reviews);
  return {
    eventId: rec.eventId,
    run,
    stale: !rc.seenInNewestRun,
    lastSeenRun: rc.lastSeenRun,
    type: trigger.type,
    severity: trigger.severity,
    metric: trigger.metric,
    value: trigger.value,
    count: rec.dedup ? rec.dedup.count : undefined,
    topMarker: Array.isArray(rec.topMarkers) && rec.topMarkers[0] ? rec.topMarkers[0].name : undefined,
    capturedAt: rec.capturedAt,
    reviewStatus: reviewStatusOf(rec, ctx.reviews),
    ...(review && review.seenAgainAfterFixed ? { seenAgainAfterFixed: review.seenAgainAfterFixed } : {}),
    sessionId: rec.sessionId,
  };
}

// The read-time fields of a full record: review status (and the decision behind it), recurrence,
// stale (= not caught in the newest run) and age. The Unity side cannot know any of these when it
// writes, so the CLI decides them.
function annotate(rec, ctx) {
  if (!rec || typeof rec !== 'object') return rec;
  rec.reviewStatus = reviewStatusOf(rec, ctx.reviews);
  const review = reviewDetail(rec, ctx.reviews);
  if (review) rec.review = review;
  rec.recurrence = recurrence(rec.eventId, ctx);
  rec.stale = !rec.recurrence.seenInNewestRun;
  const age = ageDays(rec.capturedAt);
  if (age !== undefined) rec.ageDays = age;
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
// ("Assembly-CSharp.dll!::InventoryPanel.Awake() [Invoke]"), so nobody types them exactly.
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
function runFallback(rec, resolvedRun, ctx) {
  const runs = ctx.runs;
  const latestRun = runs[0];
  // While the newest run is still in Play, the newest finished run is the fair comparison.
  if (!rec || !latestRun || resolvedRun === latestRun || resolvedRun === recurrenceReference(ctx, resolvedRun)) return rec;
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

// --- versions and compatibility --------------------------------------------------------------
// The CLI, the Unity package and the project's copy of the guide are installed separately, so they
// drift. Records say which schema wrote them; the package writes meta.json on every Play; the guide
// copy is compared with the one this CLI ships.

const SCHEMA_VERSION = 3;

// Fields each schema added, so an older record can say exactly what it lacks instead of a field
// silently reading as missing data.
const SCHEMA_FIELDS = {
  2: ['writerVersion', 'frameGcAllocBytes', 'likelyCause', 'markerTreeDepth', 'capturedFrameTimeMs', 'frameMatchesTrigger'],
  3: ['dedup.peakValue'],
};

// "a.b" reads a nested field.
function hasField(rec, field) {
  let cur = rec;
  for (const k of field.split('.')) {
    if (!cur || typeof cur !== 'object' || !(k in cur)) return false;
    cur = cur[k];
  }
  return true;
}

function schemaOf(rec) {
  const n = parseInt(rec && rec.schemaVersion, 10);
  return Number.isFinite(n) ? n : 1;
}

// Adds a `compat` block to a record written by another schema. Nothing is added for a current one.
function withCompat(rec) {
  if (!rec || typeof rec !== 'object' || !rec.eventId) return rec;
  const schema = schemaOf(rec);
  if (schema === SCHEMA_VERSION) return rec;
  if (schema > SCHEMA_VERSION) {
    return {
      ...rec,
      compat: {
        schemaVersion: schema,
        cliSchemaVersion: SCHEMA_VERSION,
        note: 'This record was written by a newer Profilot package than this CLI knows. Update the CLI: npm install -g profilot',
      },
    };
  }
  const missingFields = [];
  for (let v = schema + 1; v <= SCHEMA_VERSION; v++) {
    for (const f of SCHEMA_FIELDS[v] || []) if (!hasField(rec, f)) missingFields.push(f);
  }
  return {
    ...rec,
    compat: {
      schemaVersion: schema,
      cliSchemaVersion: SCHEMA_VERSION,
      writerVersion: rec.writerVersion || 'unknown',
      missingFields,
      note:
        'Written by an older Profilot package. The missing fields are absent, not zero - do not infer ' +
        'them. Re-capture with the current package for the full record.',
    },
  };
}

// The shipped guide's first line names the Profilot version it belongs to, so a copy can be told
// apart as older ("outdated") or locally edited. Line endings do not count: git can rewrite them.
const GUIDE_HEADER = /^<!--\s*profilot-guide\s+([0-9][0-9.]*)\s*-->/;

function normalizeText(s) {
  return s.replace(/^\uFEFF/, '').replace(/\r\n/g, '\n').replace(/\s+$/, '');
}

function guideVersion(text) {
  const m = GUIDE_HEADER.exec(text.replace(/^\uFEFF/, ''));
  return m ? m[1] : null;
}

function compareVersions(a, b) {
  const pa = String(a).split('.').map(Number);
  const pb = String(b).split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d < 0 ? -1 : 1;
  }
  return 0;
}

// current | edited (same version, local changes) | outdated (an older guide) | newer (from a newer
// CLI than this one) | missing | unknown
function guideState(base) {
  const src = findGuide();
  if (!src || !base) return 'unknown';
  const copy = path.join(path.dirname(path.dirname(base)), '.claude', GUIDE_FILE);
  if (!fs.existsSync(copy)) return 'missing';
  const shipped = fs.readFileSync(src, 'utf8');
  const local = fs.readFileSync(copy, 'utf8');
  if (normalizeText(local) === normalizeText(shipped)) return 'current';
  const v = guideVersion(local);
  const mine = guideVersion(shipped);
  if (v && v === mine) return 'edited';
  if (v && mine && compareVersions(v, mine) > 0) return 'newer';
  return 'outdated';
}

function versions(base) {
  let meta = null;
  const mp = base && path.join(base, 'meta.json');
  if (mp && fs.existsSync(mp)) {
    try {
      meta = readJson(mp);
    } catch (_) {
      meta = null;
    }
  }
  const cli = require('./package.json').version;
  const out = {
    cli,
    package: (meta && meta.packageVersion) || 'unknown',
    schema: { cli: SCHEMA_VERSION, package: meta ? schemaOf(meta) : null },
    guide: guideState(base),
  };
  const warnings = [];
  const notes = [];
  const updatePackage = 'Update it: Package Manager > Profilot > Update (or re-resolve the #release dependency).';
  if (out.schema.package != null && out.schema.package > SCHEMA_VERSION) {
    warnings.push('The Unity package writes a newer schema than this CLI reads. Update the CLI: npm install -g profilot');
  } else if (out.schema.package != null && out.schema.package < SCHEMA_VERSION) {
    warnings.push(`The Unity package is older than this CLI. ${updatePackage}`);
  } else if (meta && out.package !== 'unknown' && compareVersions(out.package, cli) !== 0) {
    // Same schema, different release. They ship together, so one of them is behind.
    warnings.push(
      compareVersions(out.package, cli) < 0
        ? `The Unity package (${out.package}) is older than the CLI (${cli}). ${updatePackage}`
        : `The CLI (${cli}) is older than the Unity package (${out.package}). Update it: npm install -g profilot`,
    );
  }
  if (!meta && base && listRuns(base).length > 0) {
    // Every package from 0.7 on writes meta.json when Play starts.
    warnings.push(`This store has runs but no meta.json, so the Unity package is older than 0.7 (or has not entered Play since it was updated). ${updatePackage}`);
  }
  if (out.guide === 'outdated') {
    warnings.push('The project guide copy is from an older Profilot. Run `profilot init` to refresh it (your copy is kept as a .bak).');
  }
  if (out.guide === 'missing') warnings.push('The project has no Profilot guide. Run `profilot init`.');
  if (out.guide === 'newer') warnings.push('The project guide copy is from a newer Profilot than this CLI. Update the CLI: npm install -g profilot');
  if (out.guide === 'edited') {
    notes.push('The project guide copy has local edits. `profilot init` would replace it and keep your copy as a .bak.');
  }
  return { versions: out, ...(warnings.length ? { warnings } : {}), ...(notes.length ? { notes } : {}) };
}

function cmdStatus(base) {
  if (!base) {
    return {
      status: 'no_data',
      message:
        'No Profilot store found. Enter Play Mode in the Unity Editor with Profilot installed so the tripwire can capture events.',
      ...versions(null),
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
  return { status: runs.length > 0 ? 'ok' : 'no_data', base, runCount: runs.length, eventCount, latest, ...versions(base) };
}

function cmdRuns(base) {
  if (!base) return cmdStatus(base);
  const runs = listRuns(base).map((r) => ({ run: r, eventCount: runEventFiles(base, r).length }));
  return { status: 'ok', count: runs.length, runs };
}

function cmdList(base, runFilter) {
  if (!base) return cmdStatus(base);
  if (runFilter) {
    const bad = checkRun(base, runFilter);
    if (bad) return bad;
  }
  const ctx = storeContext(base);
  const runs = runFilter ? [runFilter] : ctx.runs;
  const events = [];
  for (const r of runs) {
    for (const f of runEventFiles(base, r)) {
      try {
        events.push(summarize(readJson(path.join(runsRoot(base), r, f)), r, ctx));
      } catch (_) {
        // skip a half-written or malformed file rather than failing the whole listing
      }
    }
  }
  events.sort((a, b) => String(b.capturedAt).localeCompare(String(a.capturedAt)));
  return { status: 'ok', count: events.length, events };
}

// Every readable event in `runs`, as summaries plus where its file is.
function collectEvents(base, runs, ctx) {
  const out = [];
  for (const r of runs) {
    for (const f of runEventFiles(base, r)) {
      const file = path.join(runsRoot(base), r, f);
      try {
        out.push({ ...summarize(readJson(file), r, ctx), file });
      } catch (_) {
        // skip a half-written or malformed file
      }
    }
  }
  return out;
}

const ACTIVE = new Set(['open', 'regressed']);
const SEVERITY_RANK = { high: 3, medium: 2, low: 1 };

// Nothing open to pick: say what is muted instead of handing back a record the guide tells
// Claude to skip.
function nothingOpen(where, muted) {
  return {
    status: 'no_data',
    message:
      `No open or regressed events ${where}.` +
      (muted.length
        ? ` ${muted.length} muted (fixed / ignored); a muted one can still be diagnosed by id: profilot diagnose --id <eventId>.`
        : ''),
    ...(muted.length
      ? { muted: muted.slice(0, 20).map((e) => ({ eventId: e.eventId, run: e.run, reviewStatus: e.reviewStatus })) }
      : {}),
  };
}

function diagnoseFile(file, run, ctx, opts, pinned) {
  const rec = annotate(readJson(file), ctx);
  const runs = ctx.runs;
  // Only warn on an IMPLICIT fallback. With --run the caller chose the run, so an older one is
  // the answer they asked for, not a surprise.
  return shapeTree(withCompat(pinned ? rec : runFallback(rec, run, ctx)), opts);
}

// --last: the most recently captured event that still needs a look (open or regressed). Fixed and
// ignored events are muted, and the guide tells Claude to skip them, so handing one back was a
// dead end. A named --id is always served, muted or not.
function cmdDiagnoseLast(base, runFilter, opts) {
  if (!base) return cmdStatus(base);
  if (runFilter) {
    const bad = checkRun(base, runFilter);
    if (bad) return bad;
  }
  const ctx = storeContext(base);
  const all = collectEvents(base, runFilter ? [runFilter] : ctx.runs, ctx);
  if (all.length === 0) return { status: 'no_data', message: runFilter ? `No events in run ${runFilter}.` : 'No events captured yet.' };
  const active = all.filter((e) => ACTIVE.has(e.reviewStatus));
  if (active.length === 0) return nothingOpen(runFilter ? `in run ${runFilter}` : 'in any run', all);
  active.sort((a, b) => String(b.capturedAt).localeCompare(String(a.capturedAt)));
  const pick = active[0];
  try {
    return diagnoseFile(pick.file, pick.run, ctx, opts, !!runFilter);
  } catch (e) {
    return { status: 'error', message: `Event ${pick.eventId} is unreadable: ${e.message}` };
  }
}

// --worst: the costliest open or regressed event of one run (the newest unless --run pins one):
// highest severity first, then the most repeats, then the largest trigger value.
function cmdDiagnoseWorst(base, runFilter, opts) {
  if (!base) return cmdStatus(base);
  if (runFilter) {
    const bad = checkRun(base, runFilter);
    if (bad) return bad;
  }
  const ctx = storeContext(base);
  const run = runFilter || ctx.newestRun;
  if (!run) return { status: 'no_data', message: 'No events captured yet.' };
  const all = collectEvents(base, [run], ctx);
  const active = all.filter((e) => ACTIVE.has(e.reviewStatus));
  if (active.length === 0) return nothingOpen(`in run ${run}`, all);
  active.sort(
    (a, b) =>
      (SEVERITY_RANK[b.severity] || 0) - (SEVERITY_RANK[a.severity] || 0) ||
      (b.count || 0) - (a.count || 0) ||
      (b.value || 0) - (a.value || 0),
  );
  const pick = active[0];
  try {
    return diagnoseFile(pick.file, pick.run, ctx, opts, true);
  } catch (e) {
    return { status: 'error', message: `Event ${pick.eventId} is unreadable: ${e.message}` };
  }
}

// --id resolves to the newest run containing that event, unless --run pins a specific run.
function cmdDiagnoseId(base, id, runFilter, opts) {
  if (!base) return cmdStatus(base);
  const badId = checkId(id);
  if (badId) return { status: 'error', message: badId };
  if (runFilter) {
    const bad = checkRun(base, runFilter);
    if (bad) return bad;
  }
  const name = id.endsWith('.json') ? id : `${id}.json`;
  const ctx = storeContext(base);
  const runs = runFilter ? [runFilter] : ctx.runs;
  for (const r of runs) {
    const file = path.join(runsRoot(base), r, name);
    if (!inside(runsRoot(base), file)) break;
    if (fs.existsSync(file)) {
      try {
        return diagnoseFile(file, r, ctx, opts, !!runFilter);
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
function readShapeFlags(flags) {
  const opts = { depth: null, focus: null };

  const rawDepth = flags['--depth'];
  if (rawDepth !== undefined) {
    const n = Number(rawDepth);
    if (rawDepth === '' || !Number.isInteger(n) || n < 0) {
      return { ...opts, error: `Invalid --depth "${rawDepth}": expected a non-negative integer.` };
    }
    opts.depth = n;
  }

  const rawFocus = flags['--focus'];
  if (rawFocus !== undefined) {
    if (!String(rawFocus).trim()) return { ...opts, error: 'Empty value for --focus <markerName>.' };
    opts.focus = String(rawFocus);
  }

  return opts;
}

// --- argument parsing -------------------------------------------------------------------------
// Each command lists the options it takes. Anything else is an error, never a silent fallback:
// `diagnose --id=evt_x`, `diagnose evt_x` and `diagnose --bogus` all used to quietly return the
// latest event instead. Both `--flag value` and `--flag=value` are accepted.

const COMMANDS = {
  diagnose: { '--last': 'bool', '--worst': 'bool', '--id': 'value', '--run': 'value', '--focus': 'value', '--depth': 'value' },
  list: { '--run': 'value', '--text': 'bool' },
  runs: { '--text': 'bool' },
  status: { '--text': 'bool' },
  init: { '--path': 'value', '--no-package': 'bool' },
};

function parseArgs(argv) {
  const command = argv[0];
  const spec = COMMANDS[command];
  const flags = {};
  if (!spec) return { command, flags };
  const valid = Object.keys(spec).join(', ');
  for (let i = 1; i < argv.length; i++) {
    const arg = argv[i];
    if (!arg.startsWith('--')) {
      const hint = command === 'diagnose' && arg.startsWith('evt_') ? ` Did you mean --id ${arg}?` : '';
      return { command, flags, error: `Unexpected argument "${arg}" for ${command}.${hint} Options: ${valid}.` };
    }
    const eq = arg.indexOf('=');
    const name = eq === -1 ? arg : arg.slice(0, eq);
    const kind = spec[name];
    if (!kind) return { command, flags, error: `Unknown option "${name}" for ${command}. Options: ${valid}.` };
    if (kind === 'bool') {
      if (eq !== -1) return { command, flags, error: `${name} takes no value.` };
      flags[name] = true;
      continue;
    }
    let value;
    if (eq !== -1) value = arg.slice(eq + 1);
    else if (i + 1 < argv.length && !argv[i + 1].startsWith('--')) value = argv[++i];
    if (value === undefined || value === '') return { command, flags, error: `Missing value for ${name}.` };
    flags[name] = value;
  }
  if (command === 'diagnose') {
    const picks = ['--last', '--worst', '--id'].filter((f) => f in flags);
    if (picks.length > 1) return { command, flags, error: `Use one of ${picks.join(', ')}, not several.` };
  }
  return { command, flags };
}

const HELP = `profilot - read Unity performance events captured by the Profilot package

Usage:
  profilot diagnose [--last | --worst | --id <eventId>] [--run <run>] [--focus <marker>] [--depth <n>]
      --last     newest open or regressed event (the default)
      --worst    costliest open or regressed event in the newest run (or --run)
      --id       that event, even if it is muted (fixed / ignored)
      --run      pin a run (see \`profilot runs\`)
      --focus    re-root the marker tree at a marker (substring match)
      --depth    print n levels of the marker tree (default 6)
  profilot list [--run <run>] [--text]      events, newest first
  profilot runs [--text]                    Play runs, newest first
  profilot status [--text]                  store, versions and warnings
  profilot reference                        how to read every field (Markdown, for Claude Code)
  profilot init [--path <dir>] [--no-package]
                                            wire a Unity project: package, guide, CLAUDE.md
  profilot --version

Output is JSON for Claude Code (--text prints lines for people). Every command exits 0; a
failure is reported in the "status" field. Run it from the Unity project folder, or set
PROFILOT_PROJECT to the project root.
`;

// --- --text: the same answers as lines, for a person at a terminal ------------------------------

function textOf(command, r) {
  if (r.status !== 'ok') {
    const lines = [`${r.status}: ${r.message || ''}`.trim()];
    if (r.runs) lines.push(`runs: ${r.runs.join(', ')}`);
    for (const w of r.warnings || []) lines.push(`warning: ${w}`);
    return lines.join('\n');
  }
  if (command === 'status') {
    const v = r.versions || {};
    const lines = [
      `store    ${r.base}`,
      `runs     ${r.runCount}, events ${r.eventCount}`,
      `latest   ${r.latest ? `${r.latest.eventId} (run ${r.latest.run})` : '-'}`,
      `versions cli ${v.cli}, package ${v.package}, guide ${v.guide}`,
    ];
    for (const w of r.warnings || []) lines.push(`warning  ${w}`);
    for (const n of r.notes || []) lines.push(`note     ${n}`);
    return lines.join('\n');
  }
  if (command === 'runs') {
    if (!r.runs.length) return 'No runs yet.';
    return r.runs.map((x) => `${x.run}  ${x.eventCount === 0 ? 'clean' : `${x.eventCount} event${x.eventCount === 1 ? '' : 's'}`}`).join('\n');
  }
  if (command === 'list') {
    if (!r.events.length) return 'No events.';
    return r.events
      .map((e) => [e.run, e.type, e.severity, e.reviewStatus, e.eventId].map((s) => String(s == null ? '-' : s)).join('  '))
      .join('\n');
  }
  return JSON.stringify(r, null, 2);
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

// The published package carries the guide (and the field reference) next to index.js, copied in
// by prepack; a clone run through `npm link` reads them from the repo root instead, so there is
// one source of truth.
function findShipped(file) {
  for (const p of [path.join(__dirname, file), path.join(__dirname, '..', file)]) {
    if (fs.existsSync(p)) return p;
  }
  return null;
}

function findGuide() {
  return findShipped(GUIDE_FILE);
}

// The field-by-field reading notes. They used to sit in the guide, which loads into every Claude
// session in the project; now the slim guide asks Claude to run this once, when it diagnoses.
const REFERENCE_FILE = 'profilot-field-reference.md';

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
  if (before !== null && normalizeText(before) === normalizeText(content)) return { step: 'guide', result: 'unchanged', path: dest };
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  // The copy may carry the user's own edits, so it is kept next to the new one, never lost.
  let backup;
  if (before !== null) {
    backup = `${dest}.bak`;
    if (fs.existsSync(backup)) backup = `${dest}.${new Date().toISOString().replace(/[:.]/g, '-')}.bak`;
    fs.writeFileSync(backup, before);
  }
  fs.writeFileSync(dest, content);
  return { step: 'guide', result: before === null ? 'added' : 'updated', path: dest, ...(backup ? { backup } : {}) };
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

function cmdInit(flags) {
  const pathFlag = flags['--path'];
  const start = pathFlag || process.env.PROFILOT_PROJECT || process.cwd();
  const root = findUnityRoot(start);
  if (!root) {
    return {
      status: 'error',
      message: `No Unity project found at or above ${path.resolve(start)} (looked for Packages/manifest.json + ProjectSettings/). Run this from the Unity project folder, or pass --path.`,
    };
  }
  const steps = [initManifest(root, !!flags['--no-package']), initGuide(root), initClaudeMd(root)];
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
  // Help is for a person, so it is plain text, not JSON.
  if (command === undefined || command === '--help' || command === '-h' || command === 'help') {
    process.stdout.write(HELP);
    return;
  }
  if (command === 'reference') {
    const ref = findShipped(REFERENCE_FILE);
    if (ref) process.stdout.write(fs.readFileSync(ref, 'utf8'));
    else print({ status: 'error', message: `${REFERENCE_FILE} is missing from this install.` });
    return;
  }

  const { flags, error } = parseArgs(argv);
  const base = findBase();
  const run = flags['--run'];

  let result;
  if (error) {
    result = { status: 'error', message: error };
  } else {
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
        result = cmdInit(flags);
        break;
      case 'diagnose': {
        const opts = readShapeFlags(flags);
        if (opts.error) result = { status: 'error', message: opts.error };
        else if (flags['--id'] !== undefined) result = cmdDiagnoseId(base, flags['--id'], run, opts);
        else if (flags['--worst']) result = cmdDiagnoseWorst(base, run, opts);
        else result = cmdDiagnoseLast(base, run, opts); // --last is the default
        break;
      }
      default:
        result = {
          status: 'error',
          message: `Unknown command "${command}". Commands: diagnose, list, runs, status, reference, init, --version. Run \`profilot --help\` for options.`,
        };
    }
  }

  if (flags['--text']) process.stdout.write(textOf(command, result) + '\n');
  else print(result);
  // Not process.exit(): on macOS/Linux a piped stdout is async, and exiting right after the write
  // cut every payload over 64 KB mid-JSON (exit 0, unparseable output). Let the write drain.
  process.exitCode = 0;
}

main();

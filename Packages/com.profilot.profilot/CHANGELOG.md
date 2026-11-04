# Changelog

All notable changes to this package are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.8.0] - 2026-11-04

Works through the 2026-11-03 team re-review of 0.7 (fixes 1-8). The headline: "is it fixed?" now
has a reliable answer, frames are matched to their trip strictly, and the CLI and window are
easier to use and harder to misuse.

### Changed
- Profilot runs in **Editor Play Mode only**. The tripwire no longer starts in development player
  builds, where nothing could turn a trip into an event.
- The diagnosis guide is short (it loads into every Claude session in the project). The
  field-by-field notes moved to `profilot-field-reference.md`, printed by `profilot reference`.
- Public docs use neutral example names; the README leads with a quickstart, requirements
  (Claude Code included) and troubleshooting.
- "Is it fixed?" has a real answer. Every Play now creates its run folder and `run.json` on entry,
  so a run that catches nothing exists as a clean run. The CLI computes recurrence **per event**
  (`recurrence.lastSeenRun`, `seenInNewestRun`, `runsSince`, `ageDays`); `stale` now means "not
  caught in the newest run", including for a record read from an older run. Before, a clean run
  left no folder, so a fixed problem still read as current, and a pinned older run read as fixed.
- `reviews.json` is authoritative: an event missing from it is open. Reopen used to fall back to
  the status baked into the record, so a reopened event stayed fixed / ignored for the CLI.
- Fixed measures a comeback against the problem's **peak** across every run (records keep
  `dedup.peakValue`), not the value of the card that was clicked.
- Records are `schemaVersion` 3 (`dedup.peakValue`; `stale` is no longer stored).
- The window is one scroll with the caught issues first (Capture, Settings and Setup below), so the
  list no longer falls off the bottom when Settings is open.
- Event cards read in plain language: "Frame hitch - high - seen 9 times", "in
  GarbageGenerator.Update" (full marker in the tooltip), "Allocated 1.2 MB in one frame" / "Frame
  took 48 ms (trips above 18 ms)".
- The run picker shows "Nov 4, 14:32:05 - 3 events" or "clean"; the header shows the state with the
  baseline and the package version instead of repeating the window title.
- "Reset to defaults" asks first; "Delete all runs" moved under Settings > Retention, away from the
  per-run delete; icon buttons fall back to short text, not their tooltip.
- The Deep capture text no longer promises "the exact file and line".
- The Console notification carries the same pinned command as the card (`--run`).
- The window sorts regressed first, then by severity, captions Fixed in the metric's unit ("fixed
  at 4.2 MB - alerts again at 12.6 MB"), and calls an empty run a clean run.
- The guide: mark Fixed after re-running, not right after the diagnosis; one meaning of `stale`.
- CLI arguments are strict: an unknown option, a positional argument (`diagnose evt_x`), a missing
  value or an unknown `--run` is an error with the valid choices, never a silent fall back to the
  latest event. `--flag=value` works like `--flag value`.
- `diagnose --last` (the default) returns the newest open or regressed event. Fixed and ignored
  events are skipped; when everything is muted it says so and lists them. `--id` still serves a
  muted event.
- `list` events carry `metric`, `value`, `count` and `topMarker`.
- The guide copy check ignores line endings and tells a locally **edited** copy (a note) from an
  **outdated** one (a warning). The shipped guide's first line names its release
  (`<!-- profilot-guide x.y.z -->`).
- `profilot init` keeps an edited guide as `.bak` (timestamped if one exists) before refreshing it.

### Added
- Window **Setup** panel: the package version, whether the `profilot` CLI is on the PATH (and its
  version), whether `.claude/` has a current guide and CLAUDE.md imports it, with a copyable setup
  command. When something is off and there are runs, a warning sits above the issues. The same
  checklist shows on first run.
- A fixed problem that comes back below the alert line stays muted but shows "seen again after
  Fixed" (how often, last run, largest value) on the card and as `review.seenAgainAfterFixed` /
  `seenAgainAfterFixed` in the CLI.
- "Did the diagnosis help?" (Yes / No) on fixed and regressed cards, stored in `reviews.json` and
  shown by the CLI as `review.diagnosisHelped` (SPEC metric M2).
- EditMode tests for the review logic and the store (`Assets/Tests/Editor`).
- `diagnose --worst`: the costliest open or regressed event of the newest run (or `--run`).
- `profilot --help` (and bare `profilot`) prints usage; `status`, `runs` and `list` take `--text`.
- `profilot status` warns when the store has runs but no `meta.json` (a package older than 0.7) and
  when the CLI and package versions differ, naming the older side.

### Fixed
- Only a capture whose frame matched the trip can turn a fixed problem `regressed` or count it as
  seen again; an unmatched one is named after a neighbouring frame. Its card and notification say
  "frame not matched" instead of blaming the method as fact.
- A regression always alerts, even when another problem was caught on the same frame.
- The run id survives "Recompile And Continue Playing" (kept in SessionState), so the rest of the
  run no longer lands in a fallback folder.
- The CLI accepts the ids the package writes for constructors (`Foo..ctor`) and non-ASCII class
  names; containment in the store is still enforced.
- A run still in Play does not count as a clean newest run (`recurrence.newestRunInProgress`).
- The Setup panel finds a CLI installed through nvm / fnm / Volta, and judges the guide against
  the CLI that ships it. The CLI reports a guide from a newer release as `newer`; a BOM no longer
  makes a copy look outdated.
- Store files are replaced in one step (`File.Replace`), so a read never finds `reviews.json`
  missing mid-write. Trips still in the channel when Play ends are written too.
- Frame matching is strict: a profiler frame is the trip's own frame (`frameMatchesTrigger`) only
  if it breaches the trip's threshold itself and is within 25% of the trip's value on the trip's
  metric. A mild hitch used to "match" a normal neighbouring frame, draw-call trips matched on
  frame time, and any gc_spike under 64 KB matched the first frame scanned.
- Draw-call trips are matched on the frame's `Draw Calls Count` counter; GC trips on the frame's
  `GC Allocated In Frame` counter, the same one the tripwire reads (the hierarchy root, with slack
  capped at half the trip, is only the fallback).
- One bad frame that trips several types (a load frame is a hitch and a GC spike) notifies once.
- Frames are scanned once through a cached `RawFrameDataView` shared by every pending trip; the
  hierarchy view is built only for the chosen frame.
- A trip still waiting for its frame when Play ends is written with the best frame seen, not
  dropped. `frameIndexDelta` is measured from when the trip was drained, not when it was written.
- A reader that closes the pipe early (`| head`) no longer crashes the CLI with an EPIPE trace and
  exit 1 (a regression from the 0.6 pipe fix).
- `--id` and `--run` are checked as names and must resolve inside the store (`--id ../../reviews`
  returned reviews.json).
- A run folder that is not a timestamp (an old "session" folder) no longer sorts as the newest run.

## [0.7.0] - 2026-11-03

Fixes the remaining blocking items B-5 to B-9 from the 2026-11-03 team review. The CLI and the
package now release together, with the same version number.

### Changed
- Review buttons are now **Fixed** and **Ignore** (were Reviewed / Not an issue, which both muted
  forever). Fixed remembers the value it was marked at; if the problem comes back 3x worse it turns
  `regressed`, alerts once, and shows a badge. Ignore is the permanent mute. Muted events sit in a
  collapsed "Muted (N)" group and each card says its state. Old decisions migrate on load
  (`reviewed` -> `fixed`, `not_a_real_issue` -> `ignored`).
- The card's main button is now "Copy prompt for Claude", a ready prompt for that event; "Command"
  copies just the command. Both always pin `--run`, so picking an older run no longer diagnoses
  the newest run's record.
- The window states what the tripwire is really doing: not playing, warming up (frame N/60),
  watching (with the baseline), or not running (with a reason). It used to say "monitoring" during
  the warm-up and whenever Play was on.
- The diagnosis guide that `profilot init` installs is in English and tells Claude to answer in
  the user's language (it was Hebrew and made every answer Hebrew).

### Added
- Records are `schemaVersion` 2 and carry `writerVersion`. The CLI adds a `compat` block to a
  record from another schema (the fields an older one lacks, or "update the CLI" for a newer one).
- The package writes `Library/Profilot/meta.json` on every Play. `profilot status` reports the CLI,
  package and schema versions and whether the project's guide copy is current, with `warnings`
  when they drift ("run `profilot init`").

## [0.6.0] - 2026-11-03

Fixes the blocking items B-1 to B-4 from the 2026-11-03 team review. Each was reproduced on a
fresh Unity 6000.3 project against v0.5.1 and verified fixed there.

### Fixed
- CLI: output piped to another process was cut at 64 KB on macOS/Linux (broken JSON, exit 0),
  because the CLI exited before stdout drained. This hit `--focus` and `--depth` the hardest.
- Capture picked the wrong frame. It took the largest frame in the search window instead of the
  one that tripped, so a 14 ms / 1.2 MB trip could come back with the tree of a neighbouring
  480 ms / 121 MB load frame - wrong marker, wrong event id, wrong dedup key. It now picks the
  frame whose counters are closest to the trip's.
- Capture also ran before the Profiler had delivered the tripping frame, so a single stall was
  captured as a neighbouring idle frame. A trip now waits up to 10 Editor updates for a
  matching frame, scanning only the frames that arrived since its last look.
- `draw_calls` never fired: the triggers were an else-if chain and, with the default GC budget
  of 0, `gc_spike` won almost every Editor frame. Each trigger is now checked on its own. A
  draw-call spike also has to be at least 50 draws above the baseline, so a near-empty scene
  does not trip on its first few draws.
- `stale` was always `false`. The CLI now computes it at read time: `true` for a record from a
  run that is not the newest.

### Added
- Event records carry `capturedFrameTimeMs` and `frameMatchesTrigger`, so the diagnosis can tell
  when the captured frame does not look like the trip. The diagnosis guide explains both.

## [0.5.1] - 2026-11-03

### Fixed
- Deep capture found no profiler frame in a project where the Profiler window's Record was off
  (any fresh project, and batchmode): every event came back `status: "error"`, `frameIndex: -1`,
  with no markers, so nothing mapped to code. Capture now arms `ProfilerDriver` recording
  alongside `Profiler.enabled` on entering Play, and turns Record back off on exit if it was off
  before. Found by an end-to-end install test on a fresh Unity 6000.3 project.

## [0.5.0] - 2026-11-03

### Added
- The CLI is published to npm as `profilot` (`npm install -g profilot`), so it no longer needs a
  clone of this repo. Its version now moves in lockstep with the package.
- `profilot init` - one-step install into a Unity project: adds the package to
  `Packages/manifest.json`, writes the diagnosis guide to `.claude/`, and imports it from
  `CLAUDE.md`. Idempotent; an existing package entry (for example a pinned version) is kept.
  `--path <dir>` targets another folder, `--no-package` skips the manifest.
- `profilot --version`.

## [0.4.0] - 2026-08-24

### Added
- `profilot diagnose --focus <markerName>` re-roots the marker tree at one marker and returns its
  whole subtree at full depth. The match is a case-insensitive substring, because real marker
  names are long and decorated (`Assembly-CSharp.dll!::InventoryPanel.Awake() [Invoke]`). When
  several markers match, the heaviest subtree wins and the rest are listed under
  `focus.otherMatches`; when none match, the error carries `focusCandidates` so the retry is one
  step rather than a guess.
- `profilot diagnose --depth <n>` prints n levels instead of the default. The default is
  unchanged, so payloads that already work keep working.
- Event records carry `markerTreeDepth` (how deep the capture actually went), and any node whose
  children were cut carries `"truncated": "depth"` or `"truncated": "budget"` - so a cut node is
  no longer indistinguishable from a genuine leaf.
- `diagnose` flags a record served from a run that is not the newest one: `runFallback: true`
  plus `resolvedRun`, `latestRun`, and a `warning`. `--id` resolves to the newest run that
  *contains* the event, so once a fix lands and the event stops being captured, the command kept
  quietly serving the old run's numbers - which reads as "no change" exactly when the result was
  an improvement. An explicitly pinned `--run` is not a fallback and is never flagged.

### Fixed
- The marker tree was cut at a fixed depth of 6, but Unity spends about seven levels of its own
  scaffolding before the first line of user code (`PlayerLoop > UpdateScene >
  Update.ScriptRunBehaviourUpdate > BehaviourUpdate > EventSystem.Update > Instantiate >
  Instantiate.Awake > YourClass.Awake`). The tree therefore ended exactly where the answer began:
  the responsible method came back childless, with a large self time nothing could break down.
  The capture now stores 16 levels (bounded by a 4000-node ceiling) and the CLI decides how much
  to print, so the default payload is unchanged and the extra depth is what `--focus` / `--depth`
  drill into.
- `topMarkers` was ranked over the trimmed tree, so a marker deeper than the tree's cap was
  invisible even when it owned the frame's largest self time - which is where hand-placed
  `Profiler.BeginSample` markers usually sit. It is now ranked over the whole frame at any depth.
  This costs column reads, not payload bytes: the list is still the top N.
- The editor window re-read and re-parsed every event file in the run on every repaint. It now
  reloads only when the run's files actually change, keyed on a cheap count + newest-write-time
  fingerprint.

## [0.3.0] - 2026-07-06

### Added
- Event records now carry `frameGcAllocBytes` (the captured frame's own GC alloc, from the
  marker-tree root) alongside the tripwire's `counters.gcAllocBytes`. When the two disagree the
  captured frame is not the trip frame - the diagnosis layer no longer reads the marker-tree
  total as the trip's allocation.
- Event records now carry `likelyCause`. A frame whose dominant marker is the garbage collector
  pausing the frame is tagged `"gc_pressure"` - a symptom of allocation churn, so the diagnosis
  points at the allocator (a gc_spike event) instead of trying to map the collector itself.

### Fixed
- The `markerTree`'s synthetic `<other>` node dropped the collapsed children's GC alloc, so a
  frame of diffuse allocation churn looked like it barely allocated (only a tiny fraction of the
  frame's bytes were attributed). `<other>` now carries `gcAllocBytes` too, and for an
  alloc-ranked event (gc_spike) the per-level top-N keeps children by GC alloc, so a low-self
  allocator is no longer pushed out of the tree.

### Changed
- Marker-tree normalization now reads each level's child columns once (into a small in-memory
  row) instead of re-reading self-time / alloc in both the sort comparator and the collapse loop
  - fewer native frame-view calls per captured frame. Behavior is unchanged (same tree output).

### Docs
- Corrected the diagnosis guide and SPEC: Profilot does not capture allocation `callstacks`, and
  the mapping-to-code path is via the marker name, not a backtrace. The guide no longer promises
  callstacks or tells the user to "capture a gc_spike to get a callstack" (it does not exist);
  gc_spike is still worth capturing because its tree is ranked by allocation.

## [0.2.3] - 2026-07-06

### Added
- Live baseline readout in the window. The relative thresholds are "x baseline", but the
  baseline itself was never shown - now, during Play, the window displays the rolling baseline
  and the concrete trip thresholds it produces: a compact "baseline: frame X ms" line under
  Runs, and a detailed "frame X ms -> hitch at Y ms, draw calls N -> trips at M" box under
  Thresholds. Before the baseline warms (or when not playing) it says so.

## [0.2.2] - 2026-07-06

### Fixed
- Settings parameter labels were clipped ("Frame hitch (x baseli...") - the label column is now
  widened so the full name and its units are visible.

## [0.2.1] - 2026-07-06

### Changed
- Window UI polish:
  - Colored section headers (Capture / Settings / Runs) for quick scanning.
  - Collapsible, persisted groups - Settings and its Thresholds / Retention / Notifications
    sub-sections shrink and expand and remember their state.
  - Tooltips on every parameter (meaning, units, and what raising/lowering it does) and on the
    actions.
  - Icon buttons (built-in, theme-aware editor icons) for Open folder and Delete-this-run, to
    save space; the destructive "Clear all" stays a labeled button on purpose.

## [0.2.0] - 2026-07-06

### Changed
- Per-run event store. Each Play run now writes into its own folder,
  `Library/Profilot/runs/<runId>/evt_*.json`, with a top-level `latest.json` and `reviews.json`.
  Runs are kept separately (no more cross-run dedup-overwrite), so results can be distinguished,
  navigated, and deleted by run. `runId` is the run's start time (`2026-07-06_14-32-05`).
- Window redesigned around runs: a run picker to navigate between runs, the selected run's
  caught-issue list, an "Open folder" button, and "Delete this run" / "Clear all runs".

### Added
- Settings. A `ProfilotConfig` (persisted at `ProjectSettings/ProfilotConfig.json`, so it
  survives a Library wipe and can be committed) exposes the detection thresholds (frame-hitch
  multiplier + floor, GC budget, draw-calls multiplier, cooldown, warm-up) and retention
  (auto-delete runs older than N days, default 30, toggleable). The runtime tripwire reads the
  thresholds at startup; a "Settings" foldout in the window edits everything, alongside the
  notification and deep-capture toggles.
- CLI is run-aware: `runs` (list runs), `--run <id>` filter on `list` and `diagnose --id`,
  `diagnose --id` resolves to the newest run containing the event, and review decisions from
  `reviews.json` are overlaid across runs. `list` items carry their `run`.

## [0.1.7] - 2026-07-06

### Changed
- The run id (`sessionId`) is now the run's start date and time (e.g. `2026-07-06 14:32:05`)
  instead of an opaque hash, so it reads meaningfully in the window and CLI.

### Added
- The window keeps the last run's results visible after Play stops (a "Stopped - N result(s)
  from the last run" view), so you can go handle them without being back in Play Mode.
- Window toolbar: "Open folder" (reveals `Library/Profilot/events`), "Clear earlier runs"
  (deletes stale results from prior runs, keeps the current run), and "Clear all" (with a
  confirm).
- Age-based retention: event results older than 30 days are dropped on play start.

## [0.1.6] - 2026-07-05

### Added
- Distinguish events by run. Each event already carries a `sessionId` (a fresh id per Play
  run); it is now surfaced so you can tell which run an event is from: the CLI `list` includes
  `sessionId`, and the window shows the current run id plus a per-event "this run" vs "earlier
  run" note (using the existing `stale` flag). This also serves before/after a fix - re-run and
  a problem that goes stale (did not recur) is likely fixed, while a still-fresh one persists.

## [0.1.5] - 2026-07-04

### Changed
- Deep capture (marker->code mapping) is ON by default again - it is the whole point of the
  tool. v0.1.4 turned it off on the theory that the always-on profiler was the Editor
  slowdown; a direct measurement disproved that: `Profiler.enabled` adds ~0.1ms/frame
  (negligible) in a marker-rich scene. The real costs were the per-trip capture work and
  event/log accumulation over time, which are fixed directly - so mapping stays on and the
  Editor stays smooth. The toggle remains for anyone who wants counter-only max speed.
- Per-trip capture cost cut: the frame-correlation scan builds a HierarchyFrameDataView per
  frame, so its window is reduced from 30 to 10 (still well above the observed frameIndexDelta)
  to remove the periodic capture hitch.

## [0.1.4] - 2026-07-04

### Changed
- Deep capture (keeping the Unity Profiler recording during Play) is now OFF by default. It
  was the real cause of the Editor slowing down over a session: recording the full marker
  hierarchy every frame is a heavy per-frame cost - an observer effect that itself produced
  the "24ms frame, ~1.5ms CPU" false hitches, which then mapped to ever-changing markers and
  proliferated event files and console logs, compounding over time. The cheap tripwire
  (ProfilerRecorder counters) never needed the profiler and keeps catching spikes at full
  Editor speed; arm deep capture (a toggle in the window) only when you want the marker->code
  mapping (SPEC.md M8/G5 - the two-layer design as intended).

### Added
- A "Deep capture" toggle in the window (persisted). Off: cheap counter-only events
  (`status: "counters_only"`, no marker tree). On: full marker tree + code mapping, slower.
- Event store cap (200 files): bounds accumulation from a long deep session whose distinct
  problems would otherwise grow the store and the window's per-repaint load without limit.

### Fixed
- The notification "flash the window" no longer calls GetWindow (which opened/focused the
  window mid-Play and stalled the frame by stealing focus from the Game View); it now only
  flashes an already-open Profilot window.

## [0.1.3] - 2026-07-04

### Fixed
- Review feedback is now reversible and durable (two dogfooding gaps):
  - A "Reviewed" / "Not an issue" mark was a dead end - both buttons disabled, no way back.
    There is now a "Reopen" button that returns a problem to open (and resumes notifications),
    and the two mark buttons stay enabled so you can switch freely.
  - "Not an issue" did not actually mute anything: the decision was cleared every Play session
    and the event reset to "open", so it notified again next run. Decisions now persist to
    `Library/Profilot/reviews.json` (survive sessions and editor restarts), and a problem the
    user marked reviewed or not-an-issue is muted - no further notifications until Reopen.

## [0.1.2] - 2026-07-04

### Added
- Proactive notifications (`ProfilotNotifier`): when a NEW problem is first caught in a Play
  session, Profilot alerts you instead of waiting for you to check the window. Fires once per
  distinct problem (the dedup folds repeats), never per frame, and never touches an LLM -
  diagnosis stays on-demand. Four independently toggleable channels, persisted per-user in
  EditorPrefs and configured from a "Notifications" foldout in the window: a Console warning
  carrying the ready `profilot diagnose --id` command (on), a Game View toast (on), flashing
  the Profilot window to the front (on), and a sound (opt-in, off by default).

## [0.1.1] - 2026-07-03

### Changed
- Off-CPU false-positive filter for frame_hitch (dogfooding finding): a hitch whose PlayerLoop
  CPU time (`cpuTimeMs`) explains less than half the frame was spent waiting off the CPU
  (VSync / GPU present / idle), not in fixable code, so it is dropped instead of surfaced
  (SPEC.md M4/M6, NG5). The off-thread wait markers never appear in the PlayerLoop tree, so
  the CPU-vs-frame ratio - not a marker match - is what catches the common VSync false hitch.

### Added
- `cpuTimeMs` on the event record: main-thread PlayerLoop CPU time for the frame. Compared
  against `counters.frameTimeMs` it separates a real CPU stall (ratio near 1) from an off-CPU
  wait (ratio low). Documented in the diagnosis guide, which also now clarifies that a
  frame_hitch `budget` is a relative threshold (rolling baseline x multiplier), not a target
  frame rate.

## [0.1.0] - 2026-07-03

### Added
- Phase 0.5 spike: `Tools/Profilot/Debug/Dump Last Frame To Console` menu item that
  fetches the last captured frame via `ProfilerDriver` + `HierarchyFrameDataView` and
  logs the top main-thread markers. Validates the editor frame-capture API (risk R1)
  in isolation before Phase 2 depends on it.
- Phase 1: `ProfilotTripwire` runtime component that samples frame time, in-frame GC
  allocations, and draw calls via `ProfilerRecorder` each frame and flags values that
  cross a (placeholder) budget, with a per-type cooldown and a startup warm-up. Boots
  only in the Editor and in development builds.
- Phase 2 - live capture pipeline:
  - `ProfilotTripChannel` / `TripSignal`: in-memory hand-off from the runtime tripwire to
    the editor (the internal trip channel, distinct from the file-based event store).
  - `ProfilotEventCapture`: drains the channel on the next `EditorApplication.update`,
    fetches the full problem frame, normalizes it, and writes an event record.
  - `MarkerTreeNormalizer`: drills into the `PlayerLoop` subtree (ignoring `EditorLoop`),
    trims the marker tree, and builds `topMarkers`.
  - `ProfilotEventStore`: atomic per-event JSON files under `Library/Profilot/events/`
    plus a `latest.json` pointer written last.
  - Node CLI (`cli/`): `diagnose --last`, `diagnose --id`, `list`, `status` - reads the
    store and prints JSON to stdout for Claude Code. Pure transport, no LLM, read-only.
- Editor window (`Tools/Profilot/Window`): live states (not playing / monitoring / issues
  caught), a one-line summary per caught problem, a button that copies the exact
  `profilot diagnose --id <id>` command, and Reviewed / Not-an-issue feedback that writes
  reviewStatus back to the event (the editor owns the store write; the CLI stays read-only).
- Events from a previous Play session are marked `stale` on the next play start (their frame
  indices no longer match the live profiler); the window flags them.
- Tests: a headless PlayMode integration test (`Profilot.PlayTests`) that allocates every
  frame and asserts a gc_spike event is captured, plus a Node test suite for the CLI.
- Phase 3 golden-scenario calibration harness (`GoldenScenarioTests`): scenarios with a
  KNOWN responsible method (a GC allocator and a synchronous main-thread stall) that assert
  both that the problem was caught (M5 recall) and that the captured event's dominant marker
  names the responsible method (M3 mapping). Runs live in the editor via the agent bridge.

### Changed
- Frame-hitch detection is now relative to the project's own rolling frame-time baseline
  (a frame above `baseline * FrameHitchMultiplier` and above an absolute `FrameHitchFloorMs`)
  instead of a fixed 16.6ms / 60fps budget. A hitch is a stutter relative to how the game
  normally runs, so detection transfers across projects and frame-rate targets without
  flooding an early-development or 30fps project with false hitches (SPEC.md M4). The
  baseline seeds only after warm-up (so the JIT/init storm never inflates it) and rejects
  spike frames (so a run of hitches can't drag it up). Minimum supported Unity is 6000.3.
- Capture correlates the trip to the right profiler frame (`PickBestFrame`) instead of
  blindly using `lastFrameIndex`, fixing the editor/player frame-index offset.
- Tripwire warm-up is frame-based (skips the first frames) rather than wall-clock, so the
  startup JIT storm no longer fires a false hitch.
- Phase 3 calibration: `topMarkers` is ranked by the trigger's own dimension (GC for a
  gc_spike), noise is filtered out (Profilot's own markers, JIT/GC.Alloc machinery,
  editor-only markers), and repeats fold into one rolling record per trigger + dominant
  marker (cross-store dedup) instead of one file per frame.
- Phase 3 calibration (from the golden harness): marker ranking now descends through - but
  never ranks - generic PlayerLoop structural phases (`UpdateScene`, `BehaviourUpdate`, ...),
  GPU/vsync/present waits, and the `GC.Collect` collector, so the dominant marker names the
  user code that actually allocated or stalled instead of a phase name, an idle GPU wait, or
  the collector. A frame_hitch whose only heavy markers were waits/structural (a GPU-bound or
  idle frame, no user CPU work to blame) is dropped rather than written as a false positive
  (M4/M6).

### Fixed
- Console logging is off by default - dogfooding showed `Debug.LogWarning` allocated in the
  hot path and polluted the captured frame. The event store is the surface; logging is opt-in.

## [0.0.1] - 2026-06-25

### Added
- Initial package scaffold (`com.profilot.profilot`), Runtime and Editor assemblies.

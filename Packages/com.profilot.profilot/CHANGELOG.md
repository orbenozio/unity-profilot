# Changelog

All notable changes to this package are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

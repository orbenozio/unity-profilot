# Changelog

All notable changes to this package are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

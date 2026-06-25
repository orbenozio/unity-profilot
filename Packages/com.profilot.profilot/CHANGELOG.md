# Changelog

All notable changes to this package are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

### Changed
- Capture correlates the trip to the right profiler frame (`PickBestFrame`) instead of
  blindly using `lastFrameIndex`, fixing the editor/player frame-index offset.
- Tripwire warm-up is frame-based (skips the first frames) rather than wall-clock, so the
  startup JIT storm no longer fires a false hitch.
- Phase 3 calibration: `topMarkers` is ranked by the trigger's own dimension (GC for a
  gc_spike), noise is filtered out (Profilot's own markers, JIT/GC.Alloc machinery,
  editor-only markers), and repeats fold into one rolling record per trigger + dominant
  marker (cross-store dedup) instead of one file per frame.

### Fixed
- Console logging is off by default - dogfooding showed `Debug.LogWarning` allocated in the
  hot path and polluted the captured frame. The event store is the surface; logging is opt-in.

## [0.0.1] - 2026-06-25

### Added
- Initial package scaffold (`com.profilot.profilot`), Runtime and Editor assemblies.

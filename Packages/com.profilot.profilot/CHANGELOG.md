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

## [0.0.1] - 2026-06-25

### Added
- Initial package scaffold (`com.profilot.profilot`), Runtime and Editor assemblies.

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
- Phase 1 skeleton: `ProfilotTripwire` runtime component that samples frame time,
  in-frame GC allocations, and draw calls via `ProfilerRecorder` each frame and logs a
  warning when a value crosses its (placeholder) budget. No LLM call, no frame capture
  yet. Boots only in the Editor and in development builds.

## [0.0.1] - 2026-06-25

### Added
- Initial package scaffold (`com.profilot.profilot`), Runtime and Editor assemblies.

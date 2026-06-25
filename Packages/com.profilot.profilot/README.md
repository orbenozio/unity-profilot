# Profilot

Copilot for the Unity Profiler.

Profilot watches your game in Play Mode, catches performance spikes on its own (frame
hitches, GC allocations, draw-call jumps), captures the full problem frame, and hands the
structured data to Claude Code, which diagnoses the cause, points at the responsible code,
and proposes a fix.

It is built in two layers so the LLM is never called per frame:

1. A cheap **tripwire** (`ProfilerRecorder`) samples counters every frame and flags
   anomalies against a budget. Runs for free in the background, no LLM.
2. On a trip, the **Editor layer** (`ProfilerDriver` + `HierarchyFrameDataView`) pulls the
   full frame and writes a structured event record that a small CLI exposes to Claude Code.

> Profiler markers are available only in the Editor and in development builds. In release
> builds they are stripped - this matches the use case (you develop in the Editor).

## Status

Working end to end and verified live in Unity 6000.3. See the roadmap in
[`SPEC.md`](../../SPEC.md) (section 17).

- Phase 0 (done): diagnosis guidance for Claude Code - see
  [`profilot-diagnosis-guide.md`](../../profilot-diagnosis-guide.md).
- Phase 0.5 (done): `ProfilerDriver` frame-dump spike that validated the editor capture API.
  Menu: `Tools/Profilot/Debug/Dump Last Frame To Console`.
- Phase 1 (done): the live tripwire (`ProfilotTripwire`).
- Phase 2 (done): full-frame capture on trip, the file-based event store, and the Node CLI.
- Phase 3 (in progress): calibration - marker-tree trimming, noise filtering, and cross-store
  dedup by trigger + dominant marker.
- Editor window (`Tools/Profilot/Window`): live states and the caught-issue list, with a
  "copy diagnose command" button and Reviewed / Not-an-issue feedback.

## Requirements

- Unity 2022.3 LTS or newer (developed and tested on Unity 6000.3).

## Layout

```
Runtime/  ProfilotTripwire.cs        - the live tripwire (ProfilerRecorder)
          ProfilotTripChannel.cs     - in-memory trip hand-off to the editor
Editor/   ProfilotEventCapture.cs    - on trip: fetch frame, normalize, write the event
          MarkerTreeNormalizer.cs    - drill into PlayerLoop, trim the tree, topMarkers
          ProfilotEventStore.cs      - atomic event files under Library/Profilot/events
          ProfilotWindow.cs          - the editor window
          FrameDumpSpike.cs          - the Phase 0.5 ProfilerDriver capture probe
          Json.cs                    - dependency-free JSON emitter
```

The Node CLI that reads the store lives at [`cli/`](../../cli) in the repo root.

## License

MIT. See [`LICENSE.md`](LICENSE.md).

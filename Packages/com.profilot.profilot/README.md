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

Early development. See the roadmap in [`SPEC.md`](../../SPEC.md) (section 17).

- Phase 0 (done): diagnosis guidance for Claude Code - see
  [`profilot-diagnosis-guide.md`](../../profilot-diagnosis-guide.md).
- Phase 0.5 (this build): `ProfilerDriver` frame-dump spike to validate the editor capture
  API in isolation. Menu: `Tools/Profilot/Debug/Dump Last Frame To Console`.
- Phase 1 (this build): the live tripwire skeleton (`ProfilotTripwire`).
- Phase 2 (next): full-frame capture, event store, and the CLI.

## Requirements

- Unity 2022.3 LTS or newer (developed and tested on Unity 6000.3).

## Layout

```
Runtime/  ProfilotTripwire.cs      - Phase 1 live tripwire (ProfilerRecorder)
Editor/   FrameDumpSpike.cs        - Phase 0.5 ProfilerDriver capture spike
```

## License

MIT. See [`LICENSE.md`](LICENSE.md).

# Profilot

Copilot for the Unity Profiler - a performance watchdog that sits in the background, catches
spikes on its own (frame hitches, GC allocations, draw calls) during Play Mode, and hands the
structured data of the problem frame to Claude Code, which diagnoses the cause, points at the
responsible code, and proposes a fix.

This repository is the Unity host project. The product itself is the embedded package at
[`Packages/com.profilot.profilot`](Packages/com.profilot.profilot).

## Why

Reading the Unity Profiler and Frame Debugger to understand a spike is a senior-developer skill.
Profilot turns that rare skill into something any Unity developer can use: it does the catching,
and Claude Code does the interpreting.

## How it works (two layers)

1. A cheap **tripwire** (`ProfilerRecorder`) samples counters every frame and flags anomalies
   against a budget. Runs for free, no LLM.
2. On a trip, the **Editor layer** (`ProfilerDriver` + `HierarchyFrameDataView`) captures the
   full frame and writes a structured event record. A small CLI exposes it to Claude Code, so
   the LLM is only paid for on real events.

> Profiler markers are available only in the Editor and in development builds. In release builds
> they are stripped - this matches the use case (developers develop in the Editor).

## Repository contents

| Path | What |
|---|---|
| [`SPEC.md`](SPEC.md) | Full specification (product, UX, architecture), review-gated. |
| [`SPEC-BRIEF.md`](SPEC-BRIEF.md) | The original brainstorming brief the spec grew from. |
| [`profilot-diagnosis-guide.md`](profilot-diagnosis-guide.md) | Phase 0 deliverable: the guidance that teaches Claude Code how to diagnose profiler data. |
| [`Packages/com.profilot.profilot`](Packages/com.profilot.profilot) | The embedded UPM package (the product). |

## Roadmap

See [`SPEC.md`](SPEC.md) section 17. Current state:

- Phase 0 - diagnosis guidance for Claude Code. Done.
- Phase 0.5 - `ProfilerDriver` frame-dump spike (validated the editor capture API). Done.
- Phase 1 - live tripwire (`ProfilerRecorder`). Done.
- Phase 2 - full-frame capture, event store, and the Node CLI. Done, verified live.
- Phase 3 - calibration (marker-tree trimming, noise filtering, cross-store dedup). In progress.
- Editor window (`Tools/Profilot/Window`) - live states, caught-issue list, copy-diagnose, and
  Reviewed / Not-an-issue feedback.

## Requirements

- Unity 6000.3 or newer (developed and verified on Unity 6000.3).

## License

MIT. See [`LICENSE`](LICENSE).

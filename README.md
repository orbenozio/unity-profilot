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

## Installation

Two pieces: the Unity package (captures the events) and the Node CLI (exposes them to Claude Code).

1. **Unity package** - add to the target project's `Packages/manifest.json`, under `dependencies`:

   ```json
   "com.profilot.profilot": "https://github.com/orbenozio/unity-profilot.git?path=/Packages/com.profilot.profilot#release"
   ```

   `#release` always resolves to the latest release; pin an exact version with `#v0.1.0` instead.
   Requires Unity 6000.3 or newer. No scene setup - the tripwire boots itself in Play Mode, and
   events are written to `Library/Profilot/events/` inside the target project.

2. **CLI** - from a clone of this repo:

   ```bash
   cd cli && npm link
   ```

   This makes the `profilot` command available. It finds the event store by walking up from the
   working directory to the Unity project root (the nearest folder containing
   `Library/Profilot/events`), or set `PROFILOT_PROJECT` to that root.

3. **Guidance for Claude Code** - copy or link
   [`profilot-diagnosis-guide.md`](profilot-diagnosis-guide.md) into the target project's
   `CLAUDE.md`, so the agent knows when to run the CLI and how to read its output.

## Usage

1. Enter Play Mode and reproduce the performance problem.
2. When the tripwire catches a spike, the editor window (`Tools/Profilot/Window`) shows it, with a
   button that copies the exact diagnose command.
3. Ask Claude Code to "diagnose the last event" - it runs the CLI, gets the structured frame, and
   returns a diagnosis plus a suggested fix.

CLI commands (read-only, print structured JSON to stdout, no LLM):

```
profilot diagnose --last          # the most recently captured event (full record)
profilot diagnose --id <eventId>  # a specific event
profilot list                     # summaries of the captured events
profilot status                   # is the store present, how many events, what is latest
```

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

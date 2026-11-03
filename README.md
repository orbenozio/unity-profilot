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

Requires Unity 6000.3 or newer and Node.js 16 or newer. Two commands, from the Unity project
folder:

```bash
npm install -g profilot
profilot init
```

`profilot init` wires the project up in three places, and is safe to re-run (after an upgrade it
just refreshes the guide):

- **Unity package** - adds `com.profilot.profilot` to `Packages/manifest.json`, pointing at
  `https://github.com/orbenozio/unity-profilot.git?path=/Packages/com.profilot.profilot#release`.
  `#release` always resolves to the latest release; to pin an exact version, change it to
  `#v0.7.0`. An existing entry is left as it is. Skip this step with `--no-package`.
- **Guidance for Claude Code** - writes
  [`profilot-diagnosis-guide.md`](profilot-diagnosis-guide.md) to `.claude/`, so the agent knows
  when to run the CLI and how to read its output.
- **CLAUDE.md** - adds an `@.claude/profilot-diagnosis-guide.md` import (creating the file if
  needed, appending to it otherwise).

Then open the project in Unity so Package Manager resolves the package. No scene setup - the
tripwire boots itself in Play Mode, and events are written to `Library/Profilot/runs/` inside the
project. The CLI finds that store by walking up from the working directory, or set
`PROFILOT_PROJECT` to the project root.

**Keep the two halves together.** The Unity package and the CLI ship with the same version
number, and records carry a `schemaVersion`. After upgrading either one, upgrade the other
(`npm install -g profilot`, and Package Manager > Profilot > Update) and re-run `profilot init` to
refresh the guide. `profilot status` prints all three versions and warns when they drift apart.

Working on Profilot itself? Use `cd cli && npm link` instead of the npm install, so the command
runs from your clone.

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

Unity spends about seven levels of its own scaffolding before the first line of your code, so the
marker that actually costs you often sits below the slice `diagnose` prints by default. Two flags
go deeper without inflating every payload:

```
profilot diagnose --id <eventId> --focus "TeamsScreen.Awake"   # re-root at that marker, full subtree
profilot diagnose --id <eventId> --depth 12                    # more levels of the whole tree
```

`--focus` matches on a substring of the marker name, since real names are long and decorated
(`Assembly-CSharp.dll!::TeamsScreen.Awake() [Invoke]`). See
[`cli/README.md`](cli/README.md) for the full behaviour.

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
- Editor window (`Tools/Profilot/Window`) - live states (not playing / warming up / watching /
  not running), caught-issue list, copy a prompt for Claude, and Fixed / Ignore feedback (a fixed
  problem that comes back 3x worse alerts again as regressed; muted ones sit in a "Muted" group).

## Requirements

- Unity 6000.3 or newer (developed and verified on Unity 6000.3).

## License

MIT. See [`LICENSE`](LICENSE).

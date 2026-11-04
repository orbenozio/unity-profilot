# Profilot

A performance watchdog for Unity Play Mode that hands each problem to Claude Code.

Profilot runs quietly while you play in the Editor. When a frame stutters, allocates garbage or
spikes in draw calls, it captures that frame's profiler data and writes it to a small local store.
You ask Claude Code to "diagnose the last Profilot event", and it reads the frame, finds the method
the profiler blames, and proposes a fix. You don't need to know how to read the Unity Profiler.

```
You:    diagnose the last Profilot event

Claude: frame_hitch - high - frame took 48 ms (this project normally runs at 9 ms)

        Diagnosis
        SyncHitch.Update busy-waits on the main thread every frame once the level starts...

        Location in code
        Assets/Game/SyncHitch.cs:21  (SyncHitch.Update)

        Suggested fix
        Move the work off the main thread, or spread it across frames: ...
```

## Requirements

- Unity 6000.3 or newer. Profilot works in **Editor Play Mode** only; it does not run in player
  builds.
- [Claude Code](https://claude.com/claude-code), which reads the events and writes the diagnosis.
- Node.js 16 or newer, for the `profilot` CLI.

## Quickstart

From the Unity project folder:

```bash
npm install -g profilot
profilot init
```

`profilot init` sets up three things, and is safe to re-run:

- **The Unity package.** It adds `com.profilot.profilot` to `Packages/manifest.json`, pointing at
  `https://github.com/orbenozio/unity-profilot.git?path=/Packages/com.profilot.profilot#release`.
  `#release` follows the latest release; pin one with `#v0.8.0`. An existing entry is left alone,
  and `--no-package` skips this step.
- **The guide for Claude Code.** It writes [`profilot-diagnosis-guide.md`](profilot-diagnosis-guide.md)
  to `.claude/`, which tells Claude Code when to run the CLI and how to read what comes back. If
  you edited your copy, the old one is kept as a `.bak`.
- **CLAUDE.md.** It adds an `@.claude/profilot-diagnosis-guide.md` import (and creates the file if
  there is none).

Then open the project in Unity so Package Manager resolves the package. There is no scene setup.

## Using it

1. Enter Play Mode and do what makes the game stutter. Profilot warms up for the first 60 frames,
   then watches against this project's own normal frame time.
2. When it catches something, you get a Console line and a Game View toast. The Profilot window
   (`Tools > Profilot > Window`) lists what this run caught.
3. Ask Claude Code to diagnose it. You can type "diagnose the last Profilot event", or click
   **Copy prompt for Claude** on an event card and paste it.
4. Apply the fix, enter Play again and repeat the same steps. If the problem is gone, mark it
   **Fixed** on its card. If it comes back three times worse than its peak, it alerts again as
   **regressed**. If it comes back smaller, the card says "seen again after Fixed". **Ignore** is
   for noise.

Each Play session is a **run**, named by its start time. A run that caught nothing shows as a clean
run, which is how you can tell a fix worked.

## The CLI

Claude Code runs these for you. They print JSON. `--text` prints readable lines instead, for
`status`, `runs` and `list`.

```
profilot diagnose                 # the newest open or regressed event (same as --last)
profilot diagnose --worst         # the costliest open or regressed event of the newest run
profilot diagnose --id <eventId>  # one event, even if you marked it Fixed or Ignore
profilot list [--run <run>]       # captured events
profilot runs                     # runs, newest first ("clean" when nothing was caught)
profilot status                   # store, versions, warnings
profilot reference                # how to read every field (Claude Code runs this)
profilot --help
```

Unity runs about seven levels of its own code before it reaches yours, so the method that costs you
is often below the part of the tree `diagnose` prints by default. `--focus <marker>` re-roots the
tree at that marker, and `--depth <n>` prints more levels. See [`cli/README.md`](cli/README.md).

## Troubleshooting

- **The window says "not running".** The tripwire is not in this Play session. Exit and re-enter
  Play; if it stays, reimport the Profilot package.
- **Events have no markers ("counters only").** Deep capture is off in the window's Capture section.
  Turn it on to map problems to code.
- **`profilot status` warns about versions.** The package and the CLI ship together with the same
  version number. Update the side it names: `npm install -g profilot`, or Package Manager > Profilot
  > Update. Then re-run `profilot init` to refresh the guide.
- **`profilot` says "No Profilot store found".** Run it from inside the Unity project, or set
  `PROFILOT_PROJECT` to the project root. The store only exists after the first Play with the
  package installed.
- **Too many events while the scene loads.** Raise "Warm-up (frames)" or the thresholds under
  Settings in the window.

## How it works

1. A **tripwire** (`ProfilerRecorder`) reads three counters every frame and compares them with the
   project's own rolling baseline. It costs almost nothing and calls no LLM.
2. When a counter trips, the **Editor layer** finds that frame in the Profiler
   (`ProfilerDriver`), captures its marker tree and writes an event record to
   `Library/Profilot/runs/<run>/`.
3. The **CLI** prints those records for Claude Code. The LLM only runs when you ask for a diagnosis.

Profilot does not capture allocation call stacks. Claude maps a profiler marker name (for example
`GarbageGenerator.Update`) to your code by searching the repo.

## Repository

| Path | What |
|---|---|
| [`Packages/com.profilot.profilot`](Packages/com.profilot.profilot) | The Unity package (the product). |
| [`cli/`](cli) | The `profilot` npm CLI. |
| [`profilot-diagnosis-guide.md`](profilot-diagnosis-guide.md) | The guide `profilot init` installs for Claude Code. |
| [`Assets/`](Assets) | A demo scene and the PlayMode / EditMode tests. |
| [`SPEC.md`](SPEC.md), [`SPEC-BRIEF.md`](SPEC-BRIEF.md) | The original specification (in Hebrew; some sections are historical). |

Working on Profilot itself? Use `cd cli && npm link` instead of the npm install, so the command runs
from your clone. Run the tests with `cd cli && npm test`, and in Unity with the Test Runner (EditMode
and PlayMode).

## Roadmap

- Calibration: fewer startup events, noise filters for engine markers.
- Draw-call events named after the rendering marker, not a CPU one.
- See [`TASKS.md`](TASKS.md) for the open items.

## License

MIT. See [`LICENSE`](LICENSE).

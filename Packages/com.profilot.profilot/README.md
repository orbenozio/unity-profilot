# Profilot

A performance watchdog for Unity Editor Play Mode that hands each problem to Claude Code.

Profilot watches your game while you play in the Editor, catches frame hitches, GC allocations and
draw-call spikes against your project's own baseline, captures the problem frame, and writes it to
`Library/Profilot/runs/`. The `profilot` CLI hands that frame to Claude Code, which maps the
profiler's marker to your code and suggests a fix.

It is built in two layers so the LLM is never called per frame:

1. A cheap **tripwire** (`ProfilerRecorder`) samples three counters every frame and flags
   anomalies. Runs for free, no LLM.
2. On a trip, the **Editor layer** (`ProfilerDriver`) finds the frame that tripped, captures its
   marker tree and writes an event record.

Install and usage: see the [repository README](../../README.md). The window is at
`Tools > Profilot > Window`.

## Requirements

- Unity 6000.3 or newer (developed and tested on Unity 6000.3).

## Layout

```
Runtime/  ProfilotTripwire.cs        - the live tripwire (ProfilerRecorder)
          ProfilotTripChannel.cs     - in-memory trip hand-off to the editor
          ProfilotConfig.cs          - user-tunable thresholds + retention (shared with the editor)
Editor/   ProfilotEventCapture.cs    - on trip: fetch frame, normalize, write the event
          MarkerTreeNormalizer.cs    - drill into PlayerLoop, trim the tree, topMarkers
          ProfilotEventStore.cs      - per-run event files under Library/Profilot/runs/<run>
          ProfilotWindow.cs          - the editor window (runs, settings, review actions, setup)
          ProfilotHealth.cs          - is the CLI / guide / CLAUDE.md wired up (Setup panel)
          ProfilotText.cs            - plain-language labels for cards and notifications
          ProfilotNotifier.cs        - proactive notifications on a new caught problem
          FrameDumpSpike.cs          - the Phase 0.5 ProfilerDriver capture probe
          Json.cs                    - dependency-free JSON emitter
```

The Node CLI that reads the store lives at [`cli/`](../../cli) in the repo root.

## License

MIT. See [`LICENSE.md`](LICENSE.md).

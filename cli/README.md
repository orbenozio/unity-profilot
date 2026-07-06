# profilot (CLI)

Pure-transport CLI for Profilot. It reads the per-run file store the Unity editor layer writes
(`Library/Profilot/runs/<run>/`) and prints structured JSON to stdout for Claude Code. It never
calls an LLM and never writes to the store.

## Commands

```
profilot diagnose --last                 # the most recently captured event (full record)
profilot diagnose --id <eventId>         # a specific event (newest run that has it)
profilot diagnose --id <eventId> --run <run>   # ...from a specific run
profilot list                            # event summaries across all runs (each tagged with its run)
profilot list --run <run>                # ...only that run
profilot runs                            # the runs, newest first, with event counts
profilot status                          # store present? run count, event count, latest
```

Each Play Mode session is a "run", identified by its start time (e.g. `2026-07-06_14-32-05`).
Results are grouped by run, so you can tell which run an event came from and diagnose a specific
one. Review decisions (`reviewed` / `not_a_real_issue`) are overlaid from `reviews.json` and
apply across runs.

Every command exits 0; failures are reported as a `status` field in the JSON
(`ok` / `no_data` / `error`), so an agent can branch on the payload, not the exit code.

## Project resolution

The CLI finds the store by walking up from the current directory to the Unity project root
(the nearest ancestor that contains `Library/Profilot`). To point it elsewhere, set
`PROFILOT_PROJECT` to the Unity project root.

## Requirements

- Node.js 16 or newer. No dependencies.

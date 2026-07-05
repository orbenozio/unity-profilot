# profilot (CLI)

Pure-transport CLI for Profilot. It reads the file-based event store the Unity editor layer
writes (`Library/Profilot/events/`) and prints structured JSON to stdout for Claude Code.
It never calls an LLM and never writes to the store.

## Commands

```
profilot diagnose --last        # the most recently captured event (full record)
profilot diagnose --id <eventId> # a specific event
profilot list                    # summaries of the events (incl. sessionId - the run each is from)
profilot status                  # is the store present, how many events, what is latest
```

Every command exits 0; failures are reported as a `status` field in the JSON
(`ok` / `no_data` / `error`), so an agent can branch on the payload, not the exit code.

## Project resolution

The CLI finds the store by walking up from the current directory to the Unity project root
(the nearest ancestor that contains `Library/Profilot/events`). To point it elsewhere, set
`PROFILOT_PROJECT` to the Unity project root.

## Requirements

- Node.js 16 or newer. No dependencies.

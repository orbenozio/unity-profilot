# profilot (CLI)

Pure-transport CLI for Profilot. It reads the per-run file store the Unity editor layer writes
(`Library/Profilot/runs/<run>/`) and prints structured JSON to stdout for Claude Code. It never
calls an LLM and never writes to the store.

## Commands

```
profilot diagnose --last                 # the most recently captured event (full record)
profilot diagnose --id <eventId>         # a specific event (newest run that has it)
profilot diagnose --id <eventId> --run <run>   # ...from a specific run
profilot diagnose --id <eventId> --focus <markerName>   # re-root the tree at a marker
profilot diagnose --id <eventId> --depth <n>            # print n levels instead of the default
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

## Reaching the marker that actually costs you: `--focus`

Unity spends roughly seven levels of its own scaffolding before the first line of your code:

```
PlayerLoop > UpdateScene > Update.ScriptRunBehaviourUpdate > BehaviourUpdate
  > EventSystem.Update > Instantiate > Instantiate.Awake > TeamsScreen.Awake
```

`diagnose` prints a shallow slice of the tree by default, which keeps the payload small but ends
right where the answer starts - the culprit comes back looking like a childless leaf with a big
unexplained self time. `--focus` re-roots the tree at one marker and prints its **whole** subtree:

```
profilot diagnose --id evt_frame_hitch_TeamsScreen.Awake --focus "TeamsScreen.Awake"
```

The match is a case-insensitive **substring**, because real marker names are long and decorated
(`Assembly-CSharp.dll!::TeamsScreen.Awake() [Invoke]`) and nobody types them exactly. If several
markers match, the heaviest subtree wins and the rest are listed under `focus.otherMatches`. If
nothing matches, the response is a `status: "error"` carrying `focusCandidates` - the names that
do exist - so the retry is one step, not a guess.

The output gains a `focus` block (`query`, `matched`, `path` from the root, `depth`, `matchCount`)
and `markerTree` becomes the focused subtree.

## `--depth <n>`

Prints `n` levels of the tree instead of the default 6. The default is unchanged, so payloads that
already work keep working; `--depth` is how you widen without re-rooting.

The record's `markerTreeDepth` says how deep the capture actually went. Asking for more than that
is not an error - the tree just ends where it was cut, and a `depthNote` says so. Any node whose
children were dropped carries `"truncated": "depth"` (out of levels) or `"truncated": "budget"`
(the frame was too wide to serialize fully), so a cut node never reads as a genuine leaf.

`--depth` composes with `--focus`, where it bounds the focused subtree rather than the whole tree.

## `topMarkers` is ranked over the frame, not over the tree

`topMarkers` is ranked across every marker in the frame at any depth, independent of how deep the
tree is stored or printed. A marker that sits below the tree's depth - which is where hand-placed
`Profiler.BeginSample` markers usually end up - still appears in `topMarkers` if it owns the
frame's largest self time or allocation.

## When the record is not from the newest run

`diagnose --id X` resolves to the newest run that **contains** X. Once a fix lands and X stops
being captured, that silently keeps serving the old run's numbers - which reads as "no change"
when the real result was an improvement. When the resolved run is not the newest one, the output
carries:

```json
"runFallback": true,
"resolvedRun": "2026-07-06_14-32-05",
"latestRun":   "2026-07-06_15-10-41",
"warning":     "This record is from run ... not the newest run ..."
```

Passing `--run` explicitly is not a fallback, so it is never flagged.

## Project resolution

The CLI finds the store by walking up from the current directory to the Unity project root
(the nearest ancestor that contains `Library/Profilot`). To point it elsewhere, set
`PROFILOT_PROJECT` to the Unity project root.

## Requirements

- Node.js 16 or newer. No dependencies.

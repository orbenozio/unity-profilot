# profilot (CLI)

Pure-transport CLI for Profilot. It reads the per-run file store the Unity package writes in Editor
Play Mode (`Library/Profilot/runs/<run>/`) and prints structured JSON to stdout for Claude Code. It
never calls an LLM and never writes to the store.

## Install

```bash
npm install -g profilot
profilot init          # from the Unity project folder - package, guide, CLAUDE.md import
```

`init` is the only command that writes, and only to project config (`Packages/manifest.json`,
`.claude/profilot-diagnosis-guide.md`, `CLAUDE.md`). It is idempotent. An edited guide is kept as
`.bak` before it is refreshed. `--path <dir>` targets another folder, `--no-package` leaves the
manifest alone.

## Commands

```
profilot diagnose [--last]               # the newest open or regressed event (the default)
profilot diagnose --worst                # the costliest open or regressed event of the newest run
profilot diagnose --id <eventId>         # one event (newest run that has it), even if muted
profilot diagnose ... --run <run>        # pin a run
profilot diagnose ... --focus <markerName>   # re-root the tree at a marker
profilot diagnose ... --depth <n>        # print n levels instead of the default
profilot list [--run <run>] [--text]     # event summaries, newest first
profilot runs [--text]                   # the runs, newest first ("clean" = nothing caught)
profilot status [--text]                 # store, counts, latest, versions, warnings
profilot reference                       # the field-by-field reading notes (Markdown)
profilot --help | --version
```

Options are strict: an unknown option, a stray argument (`diagnose evt_x`), a missing value or an
unknown run is an error that lists the valid choices. `--flag=value` works like `--flag value`.
`--id` and `--run` must be plain names inside the store.

Each Play Mode session is a "run", identified by its start time (e.g. `2026-07-06_14-32-05`).
Since 0.8 every Play creates its run, so a run that caught nothing exists as a clean run.

Review decisions (`open` / `fixed` / `regressed` / `ignored`) come from `reviews.json` and apply
across runs; when that file exists, an event missing from it is open. `--last` and `--worst` skip
fixed and ignored events; `--id` serves them.

`status` also reports `versions` (CLI, Unity package, schema, and whether the project's guide copy
is current, edited or outdated) plus `warnings` when they drift. A record written by an older
package comes back with a `compat` block listing the fields it lacks.

Every command exits 0; failures are reported as a `status` field in the JSON
(`ok` / `no_data` / `error`), so an agent can branch on the payload, not the exit code.

## Reaching the marker that actually costs you: `--focus`

Unity spends roughly seven levels of its own scaffolding before the first line of your code:

```
PlayerLoop > UpdateScene > Update.ScriptRunBehaviourUpdate > BehaviourUpdate
  > EventSystem.Update > Instantiate > Instantiate.Awake > InventoryPanel.Awake
```

`diagnose` prints a shallow slice of the tree by default, which keeps the payload small but ends
right where the answer starts - the culprit comes back looking like a childless leaf with a big
unexplained self time. `--focus` re-roots the tree at one marker and prints its **whole** subtree:

```
profilot diagnose --id evt_frame_hitch_InventoryPanel.Awake --focus "InventoryPanel.Awake"
```

The match is a case-insensitive **substring**, because real marker names are long and decorated
(`Assembly-CSharp.dll!::InventoryPanel.Awake() [Invoke]`) and nobody types them exactly. If several
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

## Is it still happening?

Every record carries `recurrence`, computed per event across runs:

```json
"recurrence": { "lastSeenRun": "2026-07-06_14-32-05", "newestRun": "2026-07-06_15-10-41",
                "seenInNewestRun": false, "runsSeen": 3, "runsSince": 1 },
"stale": true,
"ageDays": 0.4
```

`stale` means exactly "not caught in the newest run". A fixed problem that comes back below the
regression line shows `review.seenAgainAfterFixed` (count, last run, largest value).

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

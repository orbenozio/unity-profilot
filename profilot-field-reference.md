# Profilot - field reference

The full reading notes behind the diagnosis guide. `profilot reference` prints this file.

## The event record

Main fields: `trigger` (type, severity, metric, value, budget), `counters` (the tripwire's snapshot at trip time), `cpuTimeMs`, `frameGcAllocBytes`, `capturedFrameTimeMs`, `frameMatchesTrigger`, `likelyCause`, `markerTree`, `markerTreeDepth`, `topMarkers`, `dedup` (count, first/last frame, `peakValue`). The CLI adds at read time: `reviewStatus`, `review`, `recurrence`, `stale`, `ageDays`, and `compat` / `runFallback` when they apply.

**No callstacks.** There is no `callstacks` field, for any event type. A `gc_spike` differs from a `frame_hitch` only in that its `markerTree` / `topMarkers` are sorted by `gcAllocBytes`, so the allocating marker rises to the top. Never tell the user to "catch a gc_spike to get a callstack".

## The three problem axes

1. **Frame time / hitches** - a frame much slower than this project's normal frames.
2. **GC allocations** - memory allocated inside the frame (`gcAllocBytes > 0`), which leads to garbage collection.
3. **Rendering** - draw calls far above the baseline.

Out of scope: deep GPU or physics analysis. A physics hitch shows as a generic `frame_hitch`; you may say "the source is Physics.Simulate, outside the scope of this analysis".

## Depth: the tree you get is a slice

Unity runs about seven levels of its own scaffolding before user code (`PlayerLoop > UpdateScene > Update.ScriptRunBehaviourUpdate > BehaviourUpdate > EventSystem.Update > Instantiate > Instantiate.Awake > InventoryPanel.Awake`), so the default slice (6 levels) ends where the answer starts.

- `--focus <marker>` re-roots the tree at that marker and prints its whole subtree. Matching is a case-insensitive substring. Several matches: the heaviest wins, the rest are in `focus.otherMatches`. No match: an error with `focusCandidates`.
- `--depth <n>` prints n levels of the whole tree. Asking deeper than `markerTreeDepth` is not an error; a `depthNote` says where the capture stopped.
- A node with `"truncated": "depth"` ran out of levels; `"truncated": "budget"` means the frame was too wide. A node with no `children` and no `truncated` is a real leaf.
- `topMarkers` is ranked over the whole frame at any depth, so a deep hand-placed `Profiler.BeginSample` marker can be in `topMarkers` but not in the printed tree.

**When to dig:** a user-code marker with no children but a large `selfTimeMs`, or a `topMarkers` entry missing from `markerTree`. Run `--focus` on it before concluding the time cannot be broken down.

## Mapping a marker to code

1. The marker name - extract `Class.Method`; `Assembly-CSharp.dll!` and `[Invoke]` are decoration, the namespace comes before `::`.
2. `--focus` on a broad entry point (`Awake`, `Start`, a whole screen's `Update`): the child with the big `selfTimeMs` / `gcAllocBytes` is the target.
3. Your own reading of the code: check it explains the data (allocation in a loop, per-frame strings, and so on).

**The scaffolding chain.** In a gc_spike's `topMarkers` several markers often carry exactly the same `gcAllocBytes` (`UpdateScene` -> `Update.ScriptRunBehaviourUpdate` -> `BehaviourUpdate` -> `GarbageGenerator.Update`). That is one call chain with a cumulative value, not four problems. Map the user-code marker.

## Signals

- **`frameMatchesTrigger`** - `true`: the captured frame breached the trip's own threshold and is within 25% of its value, so the tree describes the problem. `false`: no frame like that was found in time; the tree may be a neighbour's. Compare `capturedFrameTimeMs` / `frameGcAllocBytes` with `counters.*` and say so. `frameIndexDelta` is only the Editor/Player frame offset.
- **`frameGcAllocBytes` vs `counters.gcAllocBytes`** - the first is the captured frame's tree total (excludes what the Editor allocates around the loop, about 15 KB a frame), the second the tripwire's exact counter. It is a float, rounded above ~16 MB: compare orders of magnitude. Many bytes under `<other>` means allocation spread over many markers (churn).
- **`likelyCause: "gc_pressure"`** - the dominant marker is the garbage collector pausing the frame: a symptom of allocation churn. Point at the allocator via a `gc_spike` event (`profilot list`).
- **A frame_hitch `budget` is relative** - the project's rolling baseline times a multiplier (baseline 3.46 ms -> budget 6.92 ms). It is not a 60/144 Hz budget; do not infer a target FPS or suggest 16.6 ms.
- **`cpuTimeMs` vs `counters.frameTimeMs`** - PlayerLoop CPU time vs the whole frame. Far lower CPU means the frame waited off-CPU (VSync, GPU present); not a code problem. Profilot drops the clear cases (CPU under half the frame), so a borderline one is GPU-bound / off-CPU, out of scope.
- **`dedup.count`** - how often it happened this run; high means nearly every frame. `dedup.peakValue` - the largest value this run.

## Is it still happening? Is it fixed?

- **`recurrence`** - `lastSeenRun` is the newest run that caught this problem, `seenInNewestRun` whether that is the newest run, `runsSince` how many runs came after it, `runsSeen` in how many runs it appeared. Since 0.8 every Play creates a run, so a clean run counts. `newestRunInProgress: true` means the newest run is still in Play (or ended abruptly); then "newest" is the newest finished run (`comparedToRun`), because not being caught yet proves nothing. A capture that matched no frame and sat next to a problem you already marked is written as `evt_<type>_unmatched`.
- **`stale: true`** means exactly "not caught in the newest run". After a fix and a new run, `stale: true` with `runsSince` >= 1 says it did not come back. `stale: false` says it is still happening, even if the record you read is from an older run.
- **`ageDays`** - how old this record is. Mention it when it is old: the code may have changed.
- **`runFallback: true`** - `diagnose --id X` without `--run` returned the newest run that contains X, not the newest run. The record is not the current state; `warning` spells it out. Do not report before/after from it; run `profilot list --run <latestRun>`.
- **`reviewStatus`** - `open` (not triaged), `fixed` (muted; alerts again at 3x its peak), `regressed` (marked fixed, came back worse - lead with it), `ignored` (noise). `--last` and `--worst` skip fixed and ignored events; diagnose one only when the user names it.
- **`review`** - `markedAt`, `fixedAtPeak`, `seenAgainAfterFixed` (how many captures since the mark, last run, largest value: the fix did not fully hold, though nothing alerted), `diagnosisHelped`. Only captures whose frame matched the trip count toward regressed or seen again.

## Versions

- **`compat`** - the record was written by another schema. `missingFields` are absent, not zero; a fresh capture with the current package gives the full record. If `compat.note` says to update the CLI, tell the user.
- **`profilot status`** - CLI, package and guide versions, `warnings` (drift, outdated guide - suggest `profilot init`) and `notes` (a locally edited guide).

## Where the data exists

Profilot runs in Play Mode in the Unity Editor only. If the user reports "no data", check they entered Play Mode with the package installed, and point at `profilot status`.

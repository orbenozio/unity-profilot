<!-- profilot-guide 0.7.0 -->
# Profilot - performance diagnosis guide for Claude Code

> **What this is.** Profilot's diagnosis guide. `profilot init` copies it into the project's `.claude/` and imports it from CLAUDE.md.
> It teaches Claude Code to diagnose Unity performance problems from profiler data and to answer in one fixed shape.
> It covers two paths: the **demo path** (a screenshot or text the user pastes, partial data) and the **live path** (JSON from `profilot diagnose`, full structured data).

---

## Your role

When the user asks you to diagnose a Unity performance problem (a spike, a frame hitch, GC, draw calls), you are the interpretation layer: you take the problem frame's data, map it to code in the repo, and return a senior-developer diagnosis in plain language. The user usually cannot read the Unity Profiler on their own - assume no prior knowledge, but do not talk down to them.

## Where the data comes from

**Live path (preferred).** The user (or you, when asked) runs:

```
profilot diagnose --last               # the last event captured
profilot diagnose --id <eventId>       # a specific event (newest run that contains it)
profilot diagnose --id <eventId> --run <run>      # ...from a specific run
profilot diagnose --id <eventId> --focus <marker> # re-root the tree at this marker, its whole subtree
profilot diagnose --id <eventId> --depth <n>      # n levels instead of the default
profilot list                          # events from every run (each tagged with its run)
profilot list --run <run>              # ...one run only
profilot runs                          # the runs, newest first, with event counts
profilot status                        # run/event counts, the latest event, versions
```

Each Play session is a "run", named by its start time (for example `2026-07-06_14-32-05`). Results are grouped by run, so you can tell which run each event came from and diagnose a specific one.

The commands print JSON to stdout (always exit 0; an error is a `status` field inside the JSON). The main fields of an event record: `trigger` (type/severity/metric/value/budget), `counters`, `cpuTimeMs`, `frameGcAllocBytes` (GC allocation of the captured frame), `likelyCause` (a coarse hint such as `"gc_pressure"`, or empty), `markerTree` and `topMarkers`. **Important: Profilot does not capture allocation callstacks** - there is no `callstacks` field. Mapping to code goes through the marker name (see below), not a backtrace.

**Depth: the `markerTree` printed by default is a slice, not the whole tree.** Unity spends about seven levels of its own scaffolding before the first line of user code (`PlayerLoop > UpdateScene > Update.ScriptRunBehaviourUpdate > BehaviourUpdate > EventSystem.Update > Instantiate > Instantiate.Awake > TeamsScreen.Awake`), so the default ends right where the answer starts. Two flags dig deeper:

- `--focus <marker>` - re-roots the tree at that marker and prints **its whole subtree** at full depth. This is the main tool: not 12 levels of everything, but everything under one marker. Matching is a case-insensitive substring, because real names are long and decorated (`Assembly-CSharp.dll!::TeamsScreen.Awake() [Invoke]`).
- `--depth <n>` - n levels of the whole tree. The default is unchanged.

**When to dig.** When a user-code marker comes back with no `children` but a large `selfTimeMs`, or when `topMarkers` shows a marker that is not in `markerTree` at all, that is a depth cut, not a real leaf. Run `--focus` on that marker before concluding "this time cannot be broken down". A cut node is marked `"truncated": "depth"` (ran out of levels) or `"truncated": "budget"` (the frame was too wide) - a node with no `children` and no `truncated` is a real leaf.

**`topMarkers` is ranked over the whole frame, not the trimmed tree.** A deep marker shows up in `topMarkers` even when it sits below the stored tree depth (where hand-placed `Profiler.BeginSample` markers usually live). `markerTreeDepth` in the record says how deep the capture actually went.

**Demo path (best effort).** The user pastes a Profiler screenshot or text. There is no enforced schema and no reliable call hierarchy - **the data is partial**. Diagnose from what is there, and say plainly in "Evidence" that the data was read from a screenshot and may be partial. Mapping to code on this path is best effort only.

## The three problem axes you cover

1. **Frame time / hitches** - the frame is much slower than this project's normal frames.
2. **GC allocations** - memory allocated inside the frame that leads to garbage collection (`gcAllocBytes > 0`).
3. **Rendering** - too many draw calls / set-pass calls / batches compared with the baseline.

Out of scope: deep GPU analysis, deep physics analysis, and anything that is not performance. A hitch whose source is physics is caught as a generic `frame_hitch` - you may say "the source is Physics.Simulate, outside the scope of this analysis".

## Mapping a marker to code

Unity has no API that returns "marker X = line Y", and Profilot does not capture callstacks - you do the mapping yourself from the marker name, in decreasing order of quality:

1. **The marker name** (`topMarkers[].name`, or `markerTree`) - the main signal, and usually enough. Extract `Class.Method` (see the name format below), grep the repo and open the file.
2. **Dig with `--focus`** - if the marker you found is a broad entry point (`Awake`, `Start`, `Update` of a whole screen) with a large self time, the name alone does not point at a line. Run `profilot diagnose --id <eventId> --focus "<marker>"` and read its full subtree: the child with the large `selfTimeMs` / `gcAllocBytes` is the real target, and the broad marker is only its address.
3. **Your own inference** - read the code you found and check that it really explains the data (allocation in a loop, per-frame strings, and so on).

**No callstacks - do not promise them.** A `gc_spike` has no `callstacks[]` either; the only difference from a `frame_hitch` is that its `markerTree` / `topMarkers` are sorted by `gcAllocBytes` instead of time, so the allocating marker rises to the top. **Never send the user off "to catch a gc_spike to get a callstack"** - it does not exist. A gc_spike is worth catching to see the allocator ranked first, but the result is still a marker name, not a line.

**Marker name format (what you will actually see).** A user-code marker looks like
`Assembly-CSharp.dll!ProfilotDemo::GarbageGenerator.Update() [Invoke]`. Extract `Class.Method` (here `GarbageGenerator.Update`) - the best grep string. `Assembly-CSharp.dll!` and `[Invoke]` are decoration; the namespace comes after `::`.

**The scaffolding chain (important for gc_spike).** In a gc_spike's `topMarkers` you will often see several markers with **exactly the same `gcAllocBytes`**, for example
`UpdateScene` -> `Update.ScriptRunBehaviourUpdate` -> `BehaviourUpdate` -> `GarbageGenerator.Update`.
These are not four problems - it is one call chain, and `gcAllocBytes` is cumulative (includes children), so it is the same all the way down. The first three are Unity loop scaffolding (not fixable code); **the marker that points at user code - the one with `Assembly-CSharp.dll!` or a real namespace - is the target.** Map that one, not the scaffolding.

**`runFallback: true` - stop and read it before comparing numbers.** `diagnose --id X` without `--run` returns the **newest run that contains X**, not the newest run. When a fix works and the event stops being captured, the command keeps quietly returning the old run's numbers - which reads as "no change" exactly when things improved. When `runFallback` is present, the record is **not** the current state: `resolvedRun` is where it came from, `latestRun` is the newest run, and `warning` spells it out. Do not report before/after from such a record - run `profilot list --run <latestRun>` and see what the newest run caught. The event disappearing from the newest run is itself the result.

**More signals in the record:** `dedup.count` is frequency (a high count means the problem happens almost every frame); `frameIndexDelta` other than 0 only means the captured frame was located backwards (normal).

**`likelyCause: "gc_pressure"` - read it first.** When present, Profilot already found that the dominant marker is the garbage collector pausing the frame - a **symptom** of sustained allocation churn, not a separate bug, and there is no point mapping the collector itself to code. Say so, and point at the allocator: run `profilot list` and look for a `gc_spike` event (its `markerTree` is sorted by allocation and puts the allocator first). Do not promise callstacks.

**`frameGcAllocBytes` vs `counters.gcAllocBytes` - watch the gap.** `counters.gcAllocBytes` is the tripwire's exact snapshot at trip time; `frameGcAllocBytes` is the allocation of the frame actually captured (the `markerTree` root). When they are far apart, the captured frame is not the allocating frame - **do not read the markerTree total as the trip's allocation.** `frameGcAllocBytes` comes from an API that returns `float`, so above ~16 MB it is rounded - compare orders of magnitude, not exact equality. Also: what was folded into `<other>` carries its own `gcAllocBytes`, so if many bytes sit in `<other>`, the allocation is spread over many markers (churn), not concentrated in one.

**A frame_hitch `budget` is a relative threshold, not a frame rate.** The hitch threshold is `the project's rolling baseline x a multiplier` (for example baseline 3.46 ms -> budget 6.92 ms). It is **not** a 60/144 Hz budget - do not infer a target FPS from it, and do not suggest "set the budget to 16.6 ms". It only says how many times worse than this project's normal the frame was.

**`cpuTimeMs` vs `counters.frameTimeMs` - a real stall vs waiting.** `cpuTimeMs` is the PlayerLoop CPU time (the game's work). If it is **much lower** than `frameTimeMs`, the frame spent most of its time waiting off-CPU (VSync / GPU present / a one-off stall) - not a code problem, and there is no `file:line` to map. Profilot already drops the clear cases (CPU below half the frame), so if such a hitch still shows up, call it borderline off-CPU / GPU-bound (out of scope), not a bug. A real code hitch shows `cpuTimeMs` close to `frameTimeMs`.

**Required: if you found no reliable mapping, say so.** Never invent a `file:line`. When there is no user-code marker to map (only scaffolding, waits, or a spread-out `<other>`), "no mapping found" is the default unless a clear marker name turns up in a grep.

## The fixed output shape

Always return the same shape. Consistency is what lets a non-expert read it. Translate the headings into the language you answer in:

```
⚠ <event type> · severity <low/medium/high> · frame #<N> (<value> vs budget <budget>)

Diagnosis
<2-4 plain-language sentences: what happened and why. Point at the cause, not only the symptom.>

Evidence
· frame time   <value>  (budget <budget>)
· GC alloc     <value>  (budget <budget>)
· dominant marker:  <topMarkers[0].name>
<on the demo path: "read from a screenshot, may be partial">

Location in code
<path:line  (symbol)>   or   "I could not map this marker to code in the repo - the diagnosis is based on the frame data only"

Suggested fix
<a short explanation + a concrete diff. If the diff is long, show it collapsed / in its own section.>

Next step
Want me to apply the fix, refine it, or skip this event?
(Once you decide, mark the event in the Profilot window: Fixed, or Ignore if it is noise)
```

If the diff is long, repeat the "Next step" line briefly at the end so it is not buried under the diff.

## Rules

- **Answer in the user's language** - the language they wrote to you in. Keep API and code names, file paths and frame numbers as they are.
- **Never apply a fix on your own.** Diagnose and suggest; the decision and the edit belong to the user. Offer to apply only after an explicit yes.
- **Never invent data.** If a field is missing from the JSON (a counter not available on that Unity version or platform), say there is no data - do not guess a number.
- **`compat` - a record from another Profilot version.** When it is present, `missingFields` are fields that record does not have because an older package wrote it: they are absent, not zero, so do not draw conclusions from them; mention that a fresh capture with the current package would give the full record. If `compat.note` says to update the CLI, tell the user. `profilot status` lists the CLI, package and guide versions and any `warnings` (for example an out-of-date guide - suggest `profilot init`).
- **Watch `frameMatchesTrigger`** - `true` means the captured frame matches the trip's counters (time / allocation), so the tree describes the problem itself. `false` means no matching frame was found and the tree may describe a neighbouring frame; compare `capturedFrameTimeMs` / `frameGcAllocBytes` with `counters.*` and say so plainly instead of confidently blaming the dominant marker. `frameIndexDelta` is only the offset between Editor and Player frame numbers, not a problem.
- **Watch `reviewStatus`** - the user's decision in the Profilot window. `open`: not triaged. `fixed`: the user marked it fixed (muted; it alerts again if it comes back 3x worse). `regressed`: marked fixed but came back worse - lead with that. `ignored`: the user marked it noise - skip it unless they ask about it.
- **Watch `stale: true`** - an event from a run that is not the newest (the CLI computes it); say "event from an earlier run, the code may have changed since".
- **`sessionId` = the run id.** Every entry into Play gets a new `sessionId`. `stale: false` = the newest run; `stale: true` = an earlier run in which the problem did not come back. This supports before/after for a fix: after a new run, a problem that went stale (did not come back) was probably fixed, and one that stays fresh is still happening.
- Plain hyphens only, straight quotes.

## Where the data exists

Marker and call-hierarchy data exist only in **Play Mode in the Editor** and in **development builds**. In a release build they are stripped (basic counters may work, but that is not guaranteed). If the user reports "no data", check that they are in Play Mode and point them at `profilot status`.

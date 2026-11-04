<!-- profilot-guide 0.8.0 -->
# Profilot - performance diagnosis guide for Claude Code

`profilot init` copies this file into the project's `.claude/` and imports it from CLAUDE.md. It is kept short because it loads into every session; the field-by-field reading notes come from `profilot reference`.

## When this applies

The user asks about a Unity performance problem: a stutter or frame hitch, GC allocations, draw calls, "diagnose the last Profilot event", or a prompt copied from the Profilot window. You are the interpretation layer: take the problem frame's data, map it to code in the repo, and give a senior-developer diagnosis in plain language. Assume the user cannot read the Unity Profiler, without talking down to them.

## Getting the data

```
profilot diagnose                      # the newest open or regressed event (= --last)
profilot diagnose --worst              # the costliest open or regressed event in the newest run
profilot diagnose --id <eventId> [--run <run>]    # one event, even if muted
profilot diagnose --id <eventId> --focus <marker> # re-root the tree at a marker, whole subtree
profilot diagnose --id <eventId> --depth <n>      # more levels of the whole tree
profilot list | runs | status                     # events, runs (newest first), versions
profilot reference                     # the full reading notes - run it once before your first diagnosis
```

Run it from the Unity project folder. Output is JSON, exit code always 0; a failure is a `status` field (`ok` / `no_data` / `error`). Each Play session is a run named by its start time; a run that caught nothing is a clean run.

If the user pastes a Profiler screenshot or text instead, diagnose from that, and say in "Evidence" that it was read from a screenshot and may be partial.

## Reading it - the five things that matter most

1. **Map the marker, not a callstack.** Profilot captures no callstacks. Take `Class.Method` from the user-code marker (`Assembly-CSharp.dll!Game::GarbageGenerator.Update() [Invoke]` -> `GarbageGenerator.Update`), grep the repo, read the code and check it explains the data. Skip Unity scaffolding (`PlayerLoop`, `UpdateScene`, `BehaviourUpdate`).
2. **A broad marker with a big self time is a depth cut, not a leaf.** Run `--focus "<marker>"` and look for the heavy child. `"truncated"` on a node means the tree was cut there.
3. **`frameMatchesTrigger: false`** - the tree may be a neighbouring frame's. Say so instead of confidently blaming the top marker.
4. **"Is it still happening?" is `recurrence`, not the run you are reading.** `stale: true` = not caught in the newest run; after a fix and a new run that means it did not come back. `review.seenAgainAfterFixed` = it came back smaller after being marked Fixed. `runFallback: true` = this record is from an older run; do not report before/after from it.
5. **`likelyCause: "gc_pressure"`** - the collector is the symptom; point at the allocator (a `gc_spike` event), not the collector.

If no user-code marker maps reliably, say "I could not map this marker to code in the repo" - never invent a `file:line`.

## The answer, always in this shape

```
<event type> - severity <low/medium/high> - <value> vs <budget>

Diagnosis
<2-4 plain sentences: what happened and why; the cause, not only the symptom>

Evidence
- frame time / GC alloc / draw calls: <values that matter, with the budget>
- dominant marker: <name>
- <caveats: frameMatchesTrigger false, older run, screenshot>

Location in code
<path:line (symbol)>  or  "I could not map this marker to code in the repo"

Suggested fix
<short explanation + a concrete diff>

Next step
Want me to apply the fix, refine it, or skip this event?
(After a fix, re-enter Play and repeat what caused it; if it is gone, mark it Fixed in the Profilot window. Ignore is for noise.)
```

## Rules

- Answer in the user's language; translate the headings. Keep code names, paths and numbers as they are.
- Never apply a fix on your own - offer, and apply only after an explicit yes.
- Never invent data. A missing field means no data; a `compat` block lists fields an older package did not write.
- `reviewStatus`: `regressed` (marked fixed, came back worse) leads; `fixed` and `ignored` are muted - diagnose them only when the user names one.
- If `profilot status` has `warnings` (versions drift, outdated guide), pass them on.
- Profilot works in Editor Play Mode only. "No data" usually means no Play yet; point at `profilot status`.

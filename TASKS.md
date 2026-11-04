# Profilot - TASKS

Decomposition of the remaining path to a finished, operational product, along the SPEC.md
section 17 roadmap. Phases 0, 0.5, 1, 2 are done and verified live on Unity 6000.3. What is
left is Phase 3 (calibration) and a Release phase. Kept as Todo / In progress / Done.

## Phase 3 - calibration and tuning (SPEC.md section 17)

Goal: hit the acceptance metrics on real usage - M4 (false positives < 15%), M5 (recall >= 80%
on golden scenarios), M3 (marker->code mapping accuracy), M6 (zero noise on a clean run),
M7 (LLM cost). The point of calibration is to validate the *relative / structural* detection,
not to find magic absolute numbers.

### In progress
- [ ] Make detection project-agnostic by construction
  - [x] Frame hitch: relative-to-baseline detection (replaces fixed 16.6ms budget)
  - [x] Verify the relative hitch change compiles + PlayTest passes (clean compile, gc_spike PlayTest green on 6000.3.7f1)
  - [ ] Confirm `Main Thread` counter vs `PlayerLoop` time for hitch source in Editor
        (SPEC.md cross-cutting note line ~753: Editor `Main Thread` includes `EditorLoop`)

### In progress
- [ ] Golden-scenario harness (feeds M3 / M4 / M5) - `Assets/Tests/GoldenScenarios.cs` + `GoldenScenarioTests.cs`
  - [x] Scenario: GC allocation in Update -> caught + dominant marker maps to `GcSpikeScenario.Update`
  - [x] Scenario: synchronous main-thread stall -> frame_hitch maps to `SyncHitchScenario.Update`
  - [x] Assert M5 (caught) + M3 (dominant marker names the responsible method) - both green
  - [ ] Scenario: draw-call spike from broken instancing / batching (needs live rendering + camera)
- [ ] Clean-run test: assert zero events / zero noise (M6) across a representative idle scene
- [ ] Measure M5 / M3 across a wider golden set; record the numbers

### Todo
- [ ] Dogfood on 2-3 varied real projects (incl. Or's new project) to tune the multipliers
      (`FrameHitchMultiplier`, `DrawCallsBaselineMultiplier`, EMA `alpha`) and confirm the
      relative approach generalizes. Collect the captured events as the data set.
- [ ] Tune retention / rate-limit / cooldown against real usage (UX-Q2)
- [ ] Measure M7 (LLM cost per dev-hour) and set the Q5 threshold (owner decision)
- [ ] Window accessibility polish (SPEC.md section 11)

### Calibration findings surfaced by dogfooding on a real (private) game project
- [x] Fixed: the marker tree's fixed depth of 6 cut it off before user code. Unity burns ~7
      levels of scaffolding first, so a 1.5 s boot `Awake()` and a 412 ms screen `Awake()`
      came back childless with unexplainable self time, and hand-placed
      `Profiler.BeginSample` markers at depth 8+ never appeared at all. Capture now stores 16
      levels; the CLI prints a depth-6 slice by default so payloads are unchanged.
- [x] Added: `diagnose --focus <marker>` re-roots the tree at one marker and returns its whole
      subtree - the actual need was not "12 levels of everything" but "everything under one
      marker", which also keeps the payload small.
- [x] Added: `diagnose --depth <n>`.
- [x] Fixed: `topMarkers` was ranked over the trimmed tree, so a deep marker was invisible even
      when it owned the frame's largest self time. Now ranked over the whole frame at any depth.
- [x] Fixed: `diagnose --id` silently fell back to an older run once a fix made the event stop
      reproducing, which read as "no change" on a 60% improvement. The output now carries
      `runFallback` / `resolvedRun` / `latestRun` / `warning`.
- [x] Fixed: the window re-parsed every event file on every repaint (now cached on a
      count + newest-write-time fingerprint), so the deeper records do not make it slower.

### Calibration findings surfaced by the golden harness (verified live)
- [x] Fixed: dominant marker was a generic PlayerLoop phase (`UpdateScene`) - ranking now
      descends through but skips structural phases, so the user method surfaces.
- [x] Fixed: GPU/vsync-wait frames were caught as false frame_hitches - waits are excluded
      from ranking and a pure-wait hitch (no user marker) is dropped, not written (M4/M6).
- [x] Fixed: `GC.Collect` collector was the dominant marker of a GC-pause hitch - now pruned,
      so the hitch maps to the allocator instead.
- [x] Fixed (from real dogfooding, NeonRunner): VSync/GPU-present/warmup frames surfaced as
      false frame_hitches - the off-thread wait markers are not in the PlayerLoop tree, so the
      old wait-marker filter missed them. Now dropped by CPU-vs-frame ratio (`cpuTimeMs` <
      half `frameTimeMs`); added `cpuTimeMs` to the record and clarified budget semantics in
      the diagnosis guide (a hitch budget is baseline x multiplier, not a target frame rate).
- [ ] Open: `WarmupFrames = 60` is too short for a real project - dogfooding showed hitch
      dedup spanning frames 61-5332 (shader compile / asset load / JIT warmup). The off-CPU
      filter drops the VSync warmup frames, but consider a longer/settling warmup for
      main-thread warmup spikes, or flag early-frame hitches as warmup.
- [ ] Open: an ISOLATED single-frame hitch can age out of the capture window
      (`FrameSearchWindow = 30` in `ProfilotEventCapture`) before the editor-side drain under
      very high frame rates, so a later unrelated frame gets captured. Fine at ~60fps live
      (drain tracks frames); revisit if dogfooding shows large `frameIndexDelta` on hitches.
- [ ] Note: a HEAVY per-frame allocator surfaces as the GC-pause `frame_hitch` (mapped to the
      allocator), not a `gc_spike`. Product-correct, but worth documenting for users.

## Team review 2026-11-03 (Noa / Maya / Ariel / Roni)

IDs refer to the consolidated review doc (B = blocking, S = should-fix, N = nit). The doc itself
is not in the repo (it quotes a private project's data).

### Done
- [x] B-1 CLI output cut at 64 KB through a pipe (exit before stdout drained) - v0.6.0
- [x] B-2 capture picked the largest frame, not the tripping one; plus it ran before the
      Profiler delivered that frame - closest-frame match + wait up to 10 updates - v0.6.0
- [x] B-3 `draw_calls` never fired (else-if chain) - independent checks + a 50-draw floor - v0.6.0
- [x] B-4 `stale` always false - computed by the CLI per run - v0.6.0
- [x] `CaptureIntegrationTests` now asserts a real frame, the allocator's marker and
      `frameMatchesTrigger`, not just "a file exists"
- Verified with an E2E harness on a fresh 6000.3 project (plain leak, small leak next to 4 MB
  frames, a 60 ms stall, a 300-cube draw-call spike): v0.5.1 fails 3 of 4, v0.6.0 passes all,
  each event mapped to its method with `frameMatchesTrigger: true`. Host suite 3/3.

- [x] B-5 schemaVersion 2 + writerVersion, CLI `compat` block, meta.json, versions and
      warnings in `profilot status`; CLI and package release together - v0.7.0
- [x] B-6 Fixed / Ignore (D-5: regression = comes back 3x worse), Muted (N) group, state on the
      card, old decisions migrated - v0.7.0
- [x] B-7 copy always pins `--run`; primary button copies a prompt for Claude - v0.7.0
- [x] B-8 honest states: not playing / warming up N/60 / watching / not running - v0.7.0
- [x] B-9 English guide, "answer in the user's language" (open question 3) - v0.7.0
- Verified: CLI 45/45, host suite 3/3 (asserts schema 2, writerVersion, meta.json), E2E with
  seeded reviews: a 60 ms stall marked fixed at 5 ms came back `regressed` and alerted once,
  BigLoader marked fixed at 40 MB stayed fixed at 4 MB, a legacy `not_a_real_issue` became
  `ignored`. The window itself was not looked at (batchmode only).

### Todo (open questions from the review)
- [ ] D-6 default meaning of `--last` (latest event vs latest open / worst)
- [ ] Open question 1 - who Profilot is for (sets S-34 to S-36, S-40, S-42)
- [ ] N-1 deprecate npm `0.0.0-stage` (needs Or)

### Todo (should-fix seen during the B-2/B-3 work)
- [ ] S-5 `PostLateUpdate.*` still ranks as a culprit (`evt_gc_spike_PostLateUpdate.BatchModeUpdate`)
- [ ] A draw-call event is named after the frame's CPU marker (e.g. `GarbageCollector...`),
      which says nothing about what drew - attribute draw calls to renderers, not markers
- [ ] S-15 capture cost: each scan still builds a hierarchy view per frame

## Release phase (packaging and distribution - the "operational" gap)

### Done
- [x] Cut the first release: v0.1.0 (tag + GitHub release), package bumped 0.0.1 -> 0.1.0.
- [x] UPM distribution via git URL: `...git?path=/Packages/com.profilot.profilot`.
      Always-latest through a moving `release` branch (fast-forward to each new release);
      `#release` for latest, `#v0.1.0` to pin.

- [x] Name locked: `profilot` (npm, CLI command, package display name). Domain still open.
- [x] CLI packaged for npm as `profilot` 0.5.0 (`files`, repository metadata, prepack copies the
      guide + LICENSE in), version in lockstep with the Unity package.
- [x] `profilot init`: one-step install - manifest dependency, guide into `.claude/`, `@import`
      in CLAUDE.md. Idempotent, covered by the CLI test suite.
- [x] Packed-tarball install test: `npm install -g` of the tarball into a clean prefix, then
      `profilot init` + `status` on a fresh project skeleton.

### Release workflow (repeat each version)
1. Bump `Packages/com.profilot.profilot/package.json`, `cli/package.json` and the guide's first
   line (`<!-- profilot-guide x.y.z -->`) to the same version - they always release together, and
   `profilot status` warns when they differ. Move CHANGELOG `[Unreleased]` -> `[x.y.z]`.
2. Commit on main, `git tag vX.Y.Z`, push main + tag.
3. Move the moving branch: `git branch -f release vX.Y.Z && git push -f origin release`.
4. `gh release create vX.Y.Z`. Consuming projects on `#release` just Refresh in Package Manager.
5. `cd cli && npm publish` (prepack copies the guide and the field reference in).

- [x] Released v0.5.0: GitHub release + first npm publish of `profilot`, verified by a
      registry install into a clean prefix + `profilot init` on a fresh project skeleton.
      npm publish needs 2FA (browser auth), so step 5 of the workflow is Or's.

- [x] End-to-end install test in real Unity (6000.3.7f1, headless): fresh project, npm
      `profilot init`, Package Manager resolved `#release` to v0.5.0, a PlayMode run caught a
      per-frame allocation. Surfaced a real bug - capture never armed `ProfilerDriver`
      recording, so in a project with the Profiler's Record off every event had no frame and
      no markers. Fixed in v0.5.1; re-run maps the event to `LeakyInventory.Update()`.

### Todo
- [ ] Have Claude Code diagnose a captured event in a fresh project (the last hop of the E2E,
      covered so far only by the CLI output)
- [x] Make `CaptureIntegrationTests` assert a mapped marker (not just "an event exists") - done in 0.6
- [x] English version of the diagnosis guide for public users - done in 0.7
- [x] Product-facing README / getting-started - done in 0.8

## Owner decisions still open (from SPEC.md)
- [ ] Q4 - future consented `apply` mode, or stay proposal-only forever
- [ ] Q5 - the "cheap enough to leave running" LLM cost threshold (feeds M7)
- [ ] Q6 - final name / domain / npm + VS Code publisher handle
- [ ] UX-Q1 - auto-pause Play on trip; UX-Q3 - global keyboard shortcut

## Done (Phases 0 - 2, verified live)
- [x] Phase 0 - diagnosis-guidance file for Claude Code
- [x] Phase 0.5 - `ProfilerDriver` frame-dump spike (R1 validated)
- [x] Phase 1 - live tripwire (`ProfilotTripwire`), event store, basic window
- [x] Phase 2 - full-frame capture, `MarkerTreeNormalizer`, event store, Node CLI, window
- [x] Stale-marking of prior-session events on play start
- [x] Headless PlayMode integration test + Node CLI test suite
- [x] Align all docs + manifest to Unity 6000.3 minimum

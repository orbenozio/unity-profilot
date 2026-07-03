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

### Calibration findings surfaced by the golden harness (verified live)
- [x] Fixed: dominant marker was a generic PlayerLoop phase (`UpdateScene`) - ranking now
      descends through but skips structural phases, so the user method surfaces.
- [x] Fixed: GPU/vsync-wait frames were caught as false frame_hitches - waits are excluded
      from ranking and a pure-wait hitch (no user marker) is dropped, not written (M4/M6).
- [x] Fixed: `GC.Collect` collector was the dominant marker of a GC-pause hitch - now pruned,
      so the hitch maps to the allocator instead.
- [ ] Open: an ISOLATED single-frame hitch can age out of the capture window
      (`FrameSearchWindow = 30` in `ProfilotEventCapture`) before the editor-side drain under
      very high frame rates, so a later unrelated frame gets captured. Fine at ~60fps live
      (drain tracks frames); revisit if dogfooding shows large `frameIndexDelta` on hitches.
- [ ] Note: a HEAVY per-frame allocator surfaces as the GC-pause `frame_hitch` (mapped to the
      allocator), not a `gc_spike`. Product-correct, but worth documenting for users.

## Release phase (packaging and distribution - the "operational" gap)

### Done
- [x] Cut the first release: v0.1.0 (tag + GitHub release), package bumped 0.0.1 -> 0.1.0.
- [x] UPM distribution via git URL: `...git?path=/Packages/com.profilot.profilot`.
      Always-latest through a moving `release` branch (fast-forward to each new release);
      `#release` for latest, `#v0.1.0` to pin.

### Release workflow (repeat each version)
1. Bump `Packages/com.profilot.profilot/package.json` version + move CHANGELOG `[Unreleased]` -> `[x.y.z]`.
2. Commit on main, `git tag vX.Y.Z`, push main + tag.
3. Move the moving branch: `git branch -f release vX.Y.Z && git push -f origin release`.
4. `gh release create vX.Y.Z`. Consuming projects on `#release` just Refresh in Package Manager.

### Todo
- [ ] Lock the name / npm handle / domain (Q6, owner decision) - blocks npm publish
- [ ] Publish the CLI to npm (`profilot`), bump from 0.0.1 (or document `npm link` from `cli/`)
- [ ] Ship the project-guidance file + one-step install instructions (copy/link into CLAUDE.md)
- [ ] End-to-end install test from a clean machine / fresh project (stranger can run it)
- [ ] Product-facing README / getting-started (technical-product-writer pass)

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

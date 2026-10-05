# Progress (engine branch)

Unattended job: reorganise into a bootable game (Phase 1), Vulkan backend (Phase 2), upscalers (Phase 3). Brief at
`C:\Temp\meitou-engine-vulkan-upscalers-prompt.md`. Decisions in [DECISIONS.md](DECISIONS.md). Work files (reference
viewer, screenshots, benchmark logs) in `R:\VlcekM\MeitouClient-engine-work` (outside the repo).

## Phase 1: reorganise into a bootable game — in progress

- [x] Worktree `R:\VlcekM\MeitouClient-engine`, branch `engine` from master `f127922`.
- [x] Parity tooling: `meitou-tools image-diff`, `tools/scripts/parity.sh`, `parity-compare.sh`.
- [ ] Baseline: reference screenshots twice (noise floor), reference benchmark.
- [ ] `src/Meitou.Engine` (fixed tick, game clock, input bindings, Kenshi + free camera, tests) — delegated to a subagent.
- [ ] `src/Meitou.Rendering`: world renderers moved out of the viewer; backend interface + OpenGL backend.
- [ ] `src/Meitou.Game` (`meitou`): boots at `--town` / a coordinate, Kenshi camera, maximized, vsync off + frame limiter, user config.
- [ ] Viewer on the same libraries, all options working.
- [ ] Gates: parity (mean < 0.05 in all eight views), benchmark within noise, build/test green.
- [ ] Docs: docs/engine.md, docs/viewer.md, ROADMAP.md.

## Phase 2: Vulkan backend — not started

## Phase 3: upscalers — not started

## Next step

Finish the baseline, then move the renderers into `src/Meitou.Rendering`.

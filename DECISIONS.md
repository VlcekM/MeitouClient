# Decisions (engine branch)

Choices made while working unattended on the `engine` branch, with the reason. Newest last.

1. **Parity reference.** The reference viewer is master at `f127922`, built Release into
   `R:\VlcekM\MeitouClient-engine-work\ref\viewer` (outside the repo). Screenshots and benchmark output live in
   `R:\VlcekM\MeitouClient-engine-work` too. After each merge of master into `engine` the reference is rebuilt from the
   merged master commit and the reference screenshots are taken again.
2. **Parity tooling.** `meitou-tools image-diff a.png b.png [diff.png]` (mean absolute RGB difference in 0..255, share of
   pixels whose largest channel difference is over 12, maximum) and `tools/scripts/parity.sh` /
   `parity-compare.sh` (the eight gate views: four places at 13:00 and 02:00, offscreen 1600x900). No Python on the machine,
   so the comparison is a C# tool. `PngWriter` moved from the viewer to `Meitou.Data.Textures` so the tool can write diff
   images.
3. **Phase 1 is a move, not a rewrite.** The gate is pixel identity (mean < 0.05), so the renderers move into
   `src/Meitou.Rendering` with their GL call sequences unchanged. The backend interface is defined in Phase 1 with Vulkan's
   shape in mind (pipelines = shaders + fixed state, render passes = targets + load/store, command lists, explicit queries)
   and implemented for OpenGL; the frame orchestration uses it where that does not change the pictures. Moving each
   renderer's own drawing onto the interface happens per renderer in Phase 2, when its Vulkan version is written.

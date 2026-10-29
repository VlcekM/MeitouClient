# Engine constraints

Current choices; implementation details belong in the linked references.

- **Compatibility:** exact formats, merge rules and save keys. Original assets and decompiled
  output never enter the repository. Use researched formulas; do not reproduce scheduling races.
  [AGENTS.md](AGENTS.md).
- **Simulation:** headless, deterministic, fixed 1/30 s game-time tick. Ordered effects and
  stateless seeded rolls; agreement across worker counts. [docs/simulation.md](docs/simulation.md).
- **Ownership:** `WorldSession` owns clocks, input, camera and world; GPU state belongs to Rendering.
  Navigation uses DotRecast and our cache; Havok is a reference. [docs/engine.md](docs/engine.md).
- **Rendering:** Vulkan only; native API/Core/compiler in `Meitou.Rendering/Gpu/`, windowing in
  `Meitou.Rendering.Display`. Retain `LegacyProgram` and `GlConventions` while needed. Images use
  GENERAL. Require bindless descriptor indexing and multi-draw indirect; no native-model fallback.
  [docs/renderer-native.md](docs/renderer-native.md).
- **Visual modes:** Faithful follows the original. Meitou improvements use independent switches
  and adjustable ranges. Deterministic screenshot inputs; FSR comparisons account for baseline
  noise. [docs/viewer.md](docs/viewer.md).
- **Latency:** tiered compilation/PGO off, the game's methods compiled ahead on a background thread; server GC with four heaps and a 16 MB gen0 budget cap, compacting collection after
  loading, SustainedLowLatency during play. Render jobs preserve execution order.
  [docs/engine.md](docs/engine.md), [docs/bench.md](docs/bench.md).
- **Upscaling:** single-sample jittered HDR scene; shadow maps/reused reflections unjittered.
  LOD bias `log2(scale)-1` for TAA, FSR and DLSS. [docs/render-post.md](docs/render-post.md).
- **Vendors:** Vulkan FidelityFX API library from SDK 1.1.4 (Windows: AMD's DLL; Linux: built from its source). DLSS on Windows: Streamline 2.14.16 manual
  hooking, initialized before Vulkan, applying requested features/extensions and proxying presentation. DLSS on
  Linux: NGX through NVIDIA's client library (the DLSS SDK's static library, linked into `libmeitou_ngx.so` at build time; no
  Streamline there), loaded only with the DLSS model present.
  Vendor binaries are not committed. [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md),
  [docs/engine.md](docs/engine.md#rendering-and-upscaling).
- **Shaders:** own GLSL, bundled shaderc, SPIR-V binding shifts and versioned cache keys. Kenshi
  HLSL supplies facts only. [docs/renderer-native.md](docs/renderer-native.md).
- **GI:** requested by default unless `--no-gi`; ray queries require supported hardware. Faithful keeps flat ambient. Quality may trade performance for
  lighting. Unsupported hardware uses the documented fallback. [docs/render-gi.md](docs/render-gi.md).

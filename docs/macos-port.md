# macOS (MoltenVK)

**Verified** on an Apple M1 Pro, macOS 15.1, MoltenVK 1.4.2 and Vulkan loader from Homebrew (external tester,
2026-10-19/20): the viewer streams The Hub, and the game boots unpatched into play (orders, combat, speed keys, audio
through OpenAL Soft, quicksave/load, original UI). All engine changes are `OperatingSystem.IsMacOS()`-guarded.

A native Metal path next to Vulkan: [metal-backend](metal-backend.md).

## Running

- `brew install molten-vk vulkan-loader`; `export DYLD_LIBRARY_PATH=/opt/homebrew/lib` (dlopen's default path
  lacks it). SIP-restricted launchers such as `nohup` strip that variable; GLFW then reports it "doesn't support
  Vulkan". `VulkanDevice.Create` checks for `libvulkan.1.dylib` first and names this cause.
- `MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS=1` is needed (lifts Metal's 16-sampler cap for regular descriptor sets).
- Screenshots and other `C:\Temp` defaults land in a folder literally named `C:\Temp` under the CWD.

## MoltenVK constraints and the fixes

| Problem | Fix |
|---|---|
| Loader hides portability drivers: `vkCreateInstance` → `ErrorIncompatibleDriver` | Instance enables `VK_KHR_portability_enumeration` + `EnumeratePortabilityBitKhr` |
| Push descriptor sets can't use argument buffers; MSL caps 16 samplers per function, so the water set fails to compile (MoltenVK advertises `maxPushDescriptors` 287) | `MaxPushDescriptors` clamped to 16, so large sets take the regular set path |
| Advertises 1024 update-after-bind sampled images (512 2D slots); The Hub keeps more resident | `BindlessTable` limit floored at 8192; MoltenVK accepts it on Tier 2 argument buffers. Exceeds the advertised limit: a driver update could reject it |
| Newer Metal headers declare `metal::coord`; SPIRV-Cross output of the terrain's `coord()` became ambiguous | Renamed to `GradCoord`/`gradCoord` (all platforms, no behaviour change) |
| stb_truetype can't bake `.ttc` without an index (Menlo) | Debug overlay tries `Supplemental/Courier New.ttf` and `SFNS.ttf` first |
| Edge scroll asked Win32 for the cursor | GLFW cursor + focus; overshoot up to 128 px (menu bar, Dock) clamps to the edge |

Unsupported on this stack: `drawIndirectCount` (GPU grass falls back to CPU-recorded draws), ray query (no GI),
DLSS. `SplashWindow` stays Windows-only.

## Performance (Observed, M1 Pro, 3024×1964)

GPU-bound and roughly linear in pixel count: render scale 0.83 → 21–25 fps (GPU 45–48 ms), 0.50 → 36–40 fps (GPU 27.5 ms,
CPU 6.3 ms). Render scale is the effective knob; VRAM 3.7–4.7 of 10.7 GB.

## Open

- `GoldenHashTests.Scripted_scenarios_hash_as_pinned`: 10 of 15 scenarios hash differently on arm64 (floating-point
  differences across architectures); the hashes are pinned for x64.
- Bindless floor: keep the static 8192 or make it configurable.

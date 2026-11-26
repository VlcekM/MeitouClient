# macOS (MoltenVK)

**Verified** on an Apple M1 Pro, macOS 15.1, MoltenVK 1.4.2 and Vulkan loader from Homebrew (external tester,
2026-10-19/20): the viewer streams The Hub, and the game boots unpatched into play (orders, combat, speed keys, audio
through OpenAL Soft, quicksave/load, original UI). All engine changes are `OperatingSystem.IsMacOS()`-guarded.

A native Metal path next to Vulkan: [metal-backend](metal-backend.md).

## Running

- Legacy Mac builds (`-p:MeitouLegacyMac=true`) use .NET 10 for the launcher, game and tools; standard builds use .NET 11.
  Legacy packages have a `-legacy` suffix and target macOS 13 Ventura and later,
  including Ventura-only Intel Macs (#555). **Observed** (launcher 1.10.0 Intel release, Mach-O `LC_BUILD_VERSION`): its .NET 11
  executable requires macOS 14.
- `package-release.ps1 -Rid osx-x64 -LegacyMac` (or `osx-arm64`) produces the legacy game zip on a Mac.
  `build-native-macos.sh` rebuilds OpenAL Soft 1.23.1 and GLFW 3.4 for Ventura, replacing their NuGet binaries in legacy publishes
  (including the launcher's embedded OpenAL). **Observed**: both NuGet libraries declare macOS 14 in `LC_BUILD_VERSION`;
  shaderc declares 11.7.1, SPIRV-Cross 13.0, and Avalonia/Skia/HarfBuzz at most 11.0.
- `brew install molten-vk vulkan-loader`; `export DYLD_LIBRARY_PATH=/opt/homebrew/lib` (dlopen's default path
  lacks it). SIP-restricted launchers such as `nohup` strip that variable; GLFW then reports it "doesn't support
  Vulkan". `VulkanDevice.Create` checks for `libvulkan.1.dylib` first and names this cause.
- `MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS=1` is needed (lifts Metal's 16-sampler cap for regular descriptor sets).
- The launcher's Play sets both for **Vulkan** and searches the existing `DYLD_LIBRARY_PATH` plus Homebrew for the loader;
  it then symlinks `libvulkan.1.dylib` and `libMoltenVK.dylib` into `~/Library/Application Support/Meitou/vulkan` and points
  the game's `DYLD_LIBRARY_PATH` at that narrow folder, not at all of `/opt/homebrew/lib` (an existing MVK value is kept):
  a Finder-started launcher has neither (#20). A `~/lib` symlink does not help
  (**Observed**, #20). The launcher's **Metal** choice starts the game with `--renderer metal` and removes
  `DYLD_LIBRARY_PATH`; it needs neither the loader nor MoltenVK. **Auto** uses Vulkan when the loader exists and Metal otherwise,
  so a new Apple-silicon user does not need Homebrew. Metal is offered on Apple silicon only in the launcher for now.
- Running a checkout: `tools/scripts/meitou-dev-macos.sh` builds the game (Release) and runs it with Metal; `--launcher` instead installs the game
  into the launcher's `versions/` as `99.0.0-dev` (with the settings schema a release ships, so Play, Graphics, Detect and the Benchmark page
  treat it as an installed version) and runs the Debug launcher from the checkout. Both run from app bundles beside their build folders
  (`bin/Release/MeitouClient.app`, `bin/Debug/meitou-launcher.app`, the build hard-linked in), so the game gets Game Mode and the launcher its
  name and icon ([App bundle](#app-bundle)). The launcher otherwise runs installed releases only ([launcher](launcher.md)); its `DebugGame`
  hook looks for the Windows `.exe`. Options and variables: `--help`.
- Build a checkout from its real path, not through a symlink such as `/tmp` (macOS: a link to `/private/tmp`): there NuGet's restore of
  `Meitou.Game` drops every project reference (its `obj/project.assets.json` names no Meitou project), so `Meitou.Data`'s NVorbis package is
  missing and `Audio/Voice.cs` fails with CS0246 (the compile still finds the projects). `dotnet restore --force` and
  `dotnet build-server shutdown` do not help; the same commit under `/private/tmp/...` builds (**Observed** 2026-11-26, worktrees of one commit).
- Screenshots and other `C:\Temp` defaults land in a folder literally named `C:\Temp` under the CWD.

## App bundle

Release zips hold the game in `MeitouClient.app` (`tools/scripts/macos-bundle.sh` writes its `Info.plist` and icon; `package-release.ps1` moves
the whole publish, viewer and tools included, into `Contents/MacOS`). The launcher starts `Contents/MacOS/MeitouClient` directly, and older
flat builds still run without Game Mode ([launcher](launcher.md)).

- Game Mode: **Observed** (macOS 27.2, `log stream` of `gamepolicyd`): a process started directly from a bundle's `Contents/MacOS`, without
  `open` and without a bundle signature, gets `Found game … labelReason=LSSupportsGameMode (Info.plist)`, then `Game mode enabled` in native
  full screen. A bare executable started as the child of a bundled app declaring a game stays `anon<MeitouClient>` to RunningBoard and is never
  labelled a game: the launcher's bundle cannot give the game Game Mode.
- `codesign` refuses the bundle as a whole: it treats the `.dll`s in `Contents/MacOS` as unsigned nested code. Given the path of the bundle's
  main executable, it still signs the whole bundle, so it fails too (**Observed**). `package-release.ps1` therefore signs each Mach-O file while
  the publish is flat and moves them in afterwards. The bundle has no seal, which is fine to run (the embedded signatures survive the zip).
- GLFW changes the working directory to `Contents/Resources` when the program runs from a bundle (`GLFW_COCOA_CHDIR_RESOURCES`); the game turns
  that off before its first window (`GlfwStartup`), keeping the launcher's data folder and its `meitou.user.json`. **Observed** with `lsof`.
- The game and the viewer write `log.txt` (and a typed-in `meitou.local.json`) beside the executable, in `Contents/MacOS`; the launcher reads
  them there.

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

## FSR

`FsrUpscaler` loads `libamd_fidelityfx_vk.dylib` and takes `vkGetDeviceProcAddr` from `libvulkan.1.dylib`. AMD ships no
macOS build; the FidelityFX SDK 1.1.4 builds for arm64 with patches (frame generation stubbed, upscaling untouched):
[fsr3-native-macos](https://github.com/SpaceCowboyServer/fsr3-native-macos). **Observed**: correct but slower than TAA
(28 vs 36–40 fps at render scale 0.5 on M1 Pro; ~12 ms of compute dispatch overhead under MoltenVK).

## Performance (Observed, M1 Pro, 3024×1964)

GPU-bound and roughly linear in pixel count: render scale 0.83 → 21–25 fps (GPU 45–48 ms), 0.50 → 36–40 fps (GPU 27.5 ms,
CPU 6.3 ms). Render scale is the effective knob; VRAM 3.7–4.7 of 10.7 GB.

## Open

- `GoldenHashTests.Scripted_scenarios_hash_as_pinned`: 10 of 15 scenarios hash differently on arm64 (floating-point
  differences across architectures); the hashes are pinned for x64.
- Bindless floor: keep the static 8192 or make it configurable.

# Launcher

`launcher/Meitou.Launcher` (Avalonia, `meitou-launcher`): installs game releases from GitHub, edits the mod list like Kenshi's own
launcher, keeps mod presets and starts the game. Styled after the Meitou UI kit ([meitou-ui](meitou-ui.md)) with its own title bar; Exo 2
is read from the Kenshi install, nothing is shipped. Mods come only from disk (`mods/` and downloaded workshop
items); the launcher never downloads or hosts mods.

Tooltips open beside the pointer, never under it (`ToolTipPlacement.cs`): below the cursor, else above it with a 4 px gap. **Observed**
(Avalonia 12.1.4 `ManagedPopupPositioner`, decompiled): a flip keeps the popup's offset, so the stock pointer placement (offset 20 px down)
flipped above the pointer near the screen's bottom covered the cursor and took the next click; the gaps are part of the anchor box instead.

## Releases

- One repository, two tag families: the game `v<version>` (`release.yml`, nightlies `nightly.yml`), the launcher `launcher-v<version>`
  (`launcher-release.yml`, changelog `launcher/CHANGELOG.md`). A full launcher release is marked Latest and game releases never
  are, so `/releases/latest` (README's link) is the newest launcher; README's
  setup links are `/releases/latest/download/<name>`, so the Setup/AppImage/pkg names must stay free of the version. Full releases of both pass
  `--notes-start-tag` with the previous tag of their own family, so the generated notes never span the other family's tag.
- Both manual release workflows accept `build_only`: packages go to workflow artifacts, with no tag, release or announcement;
  the supplied version needs no changelog section. Mac matrices include standard .NET 11 and separate .NET 10
  [legacy compatibility](macos-port.md#running) builds for both architectures.
- Both tag the commit the run started on with `GITHUB_TOKEN`. **Verified** (throwaway test workflow, 2026-10-22): GitHub refuses that
  token a tag or release (HTTP 403 "Resource not accessible by integration") on a commit whose `.github/workflows/` files differ from
  the current master, as it lacks the `workflows` permission. So pushing a workflow edit while a release runs fails its release step;
  re-run it from a new dispatch, or publish the run's artifacts by hand with `gh release create` (launcher 1.1.1 was).
- Nightlies: `nightly.yml` runs twice a day (05:00 UTC "nightly", 17:00 UTC "daily", or by hand) and publishes
  `v<X.Y.Z+1>-nightly.<yyyymmdd>`, a second build on the same UTC day `….<yyyymmdd>.2`
  (patch after the newest full release) as a pre-release without a changelog section; its notes list every
  commit since the newest game tag (GitHub's generated notes cover only merged pull requests, so nightlies don't use them). It is skipped when nothing
  outside `news/`, `site/`, `launcher/`, `docs/` and `.github/workflows/` changed since the newest game tag (nightly or full). Only the newest 14
  nightlies are kept, so the releases page and the launcher's Versions list stay short. Each nightly's commit list, and each full or launcher release's changelog section, is posted to Discord
  as the Tinfist bot (secret `DISCORD_BOT_TOKEN`, repository variable `DISCORD_NIGHTLY_CHANNEL`), else through the `DISCORD_WEBHOOK_URL`
  channel webhook, when set. The launcher shows them with the Nightly builds checkbox beside Play ticked ("Play latest nightly"; ticking it asks first, with Yes under the pointer, warning of crashes and corrupted saves) (GitHub pre-releases; `IncludePrereleases` in `launcher.json`). Installed versions stay listed either way; an update is offered only for a release newer than the newest installed. An installed version GitHub does not list (a build made from source and copied into `versions/`, e.g. `99.0.0-dev`; `Releases.LocalReleases`) is a row at the top of the Versions list, selectable, uninstallable and openable like the others, and is the arrow menu's "Latest" while it is the newest.
- Game assets: `Meitou-<version>-<rid>.zip`, `<rid>` as .NET's `RuntimeInformation.RuntimeIdentifier` (`win-x64`, `linux-x64`,
  `osx-x64`, `osx-arm64`). Legacy Mac assets append `-legacy` to the RID. The legacy launcher selects only those assets and uses
  matching Velopack channels (`osx-x64-legacy`, `osx-arm64-legacy`), so self-updates stay on .NET 10. Its pack ID and app title are
  `MeitouLauncherLegacy` / `Meitou Launcher Legacy`; installed games live in `versions-legacy/`, separately from standard builds.
  Settings are shared; moving the installed versions to a new install folder moves both build families. A release with no matching zip is listed but not installable.
- The list comes from the REST API without a token (60 requests an hour per IP). Every page is read (`per_page=100`, until a short page),
  cached together with page 1's ETag in `releases-cache.json`; an unchanged page 1 is a 304 that costs no quota and keeps the cache. Downloads use the asset URLs, which don't count
  against the quota.
- A download is checked against the asset's `digest` (`sha256:<hex>`) when GitHub lists one. The zip's single top folder becomes
  `versions/<version>/`. The game is `MeitouClient[.exe]` since 0.15 (`meitou[.exe]` before); on macOS the launcher looks for
  `MeitouClient.app/Contents/MacOS/MeitouClient` first ([macos-port](macos-port.md#app-bundle)), so flat older builds still run
  (`Releases.GameExecutables`). The version's own files (`settings-schema.json`, `log.txt`) are read beside the executable it finds. On Unix
  the executable gets its execute bits. Cancelling a download removes the partial zip and unpacked folder.

## One executable (Native AOT)

- `dotnet publish -r <rid>` gives a single Native AOT `meitou-launcher[.exe]` (`PublishAot`, ~45 MB on win-x64, **Verified**
  2026-10-20) and nothing else: no symbols, no native libraries beside it. `Meitou.Core` is `IsAotCompatible`, so code it or the
  launcher adds that AOT can't compile (reflection `JsonSerializer`, for one) fails the build; JSON goes through `LauncherJson`.
- Avalonia's native libraries (`libSkiaSharp`, `libHarfBuzzSharp`, on Windows ANGLE's `av_libglesv2`, on macOS
  `libAvaloniaNative`) can't be linked in, so the publish embeds them as `native/<file>` resources. `NativeLibraries.Load` (right
  after Velopack's hooks) writes them once to `native/<16 hex>/` in the data folder (a SHA-256 of the per-file hashes the build stores as the
  `NativeLibraries` assembly metadata, so startup hashes no library), loads them by full path and
  points SkiaSharp's, HarfBuzzSharp's and Avalonia.Native's P/Invokes at them; ANGLE is then found by name as an already loaded
  module (Windows only: macOS dyld doesn't match a bare name to a loaded library, issue #16). Other `native/*` folders are deleted when not in use.
  Debug builds embed nothing and load them from beside the executable.
- Statically linked Skia/ANGLE (third-party prebuilt packages, win-x64 only) would avoid the extraction; not used, so all four
  RIDs share one mechanism on the official binaries.
- Building it locally needs the platform's native toolchain: the MSVC C++ build tools on Windows (with `vswhere.exe` on `PATH`),
  `clang` and `zlib1g-dev` on Linux, Xcode on macOS.

## Self-update (Velopack)

- `launcher-release.yml` packs the launcher with `vpk pack` (Velopack, MIT; `vpk` version = the `Velopack` package version) on each
  runtime's own OS, one channel per RID (`--channel <rid>`, packId `MeitouLauncher`): Windows `-Setup.exe` + `-Portable.zip`,
  Linux an AppImage, macOS a bundle and installer (Mac-only tooling, unsigned). Every file name carries its channel
  (**Verified** for win-x64: `MeitouLauncher-<v>-win-x64-full.nupkg`, `releases.win-x64.json`, `assets.win-x64.json`,
  `RELEASES-win-x64`), so all four go on one `launcher-v<version>` release; the job fails on a name clash.
- `Program.Main` calls `VelopackApp.Build().Run()` first. `LauncherUpdater` uses `ExplicitChannel = <rid>` and a `GithubSource`
  whose `GetReleases` returns only `launcher-v*` releases from the launcher's own cached list. Velopack's stock listing reads just
  the newest 10 releases (`per_page=10`, page 1; **Verified** in its source 2026-10-19), which game releases would crowd out;
  its `GitBase` merges the feed of every release given and skips releases without one.
- Checked each time the release list loads. Installed by Setup or AppImage (`UpdateManager.IsInstalled`): at startup the launcher
  downloads and applies a newer version and restarts into it (Settings toggle, `AutoUpdateLauncher`, default on); when off or failed, the title bar and Settings offer "Update launcher to
  <v>" / "Update to <v>" to retry. Launchers up to 1.0.4 only offer the buttons. Run from anywhere else (a build, a copied folder):
  the same buttons open the newest `launcher-v*` release page.
- **Unknown**: the update path end to end (1.0.1 → 1.0.2 is the first real one).

## App menu (Linux)

- Run from the AppImage (`APPIMAGE` is set, by the AppImage runtime), the first-start dialog offers "Add to the app menu" (ticked) and
  Settings has an Add/Remove button. `AppMenuEntry` writes `wiki.kenshi.meitou.launcher.desktop` and a 128 px `.png` to
  `$XDG_DATA_HOME/applications` (`~/.local/share/applications`), with `Exec` and `TryExec` set to the AppImage, so the menu hides the entry
  once the AppImage is deleted. While the entry exists, each start rewrites it for the AppImage's current path.
- The window's X11 class is `MeitouLauncher` (`X11PlatformOptions.WmClass`; Avalonia's default is the executable name), Velopack's packId
  and the `StartupWMClass` of both this entry and the AppImage's embedded one. **Verified** with `xprop` in a headless gamescope:
  `WM_CLASS = "meitou-launcher", "MeitouLauncher"`.
- The AppImage's file icon is its 64 px `.DirIcon`; Dolphin shows it only through KDE's AppImage thumbnailer, which needs `libappimage`
  (an optional dependency of `kio-extras`). **Observed** on CachyOS: without it `ldd` reports `libappimage.so.1.0` not found for
  `appimagethumbnail.so`, and Dolphin shows the generic icon.

## Unsigned macOS packages

The macOS launcher and game are **not notarized**: there is no paid Apple Developer ID in this project. The app and the
published executable are ad-hoc signed where the build produces them, but Gatekeeper will still warn, and a `.pkg` downloaded
through a browser may be blocked outright.

- Tell users to right-click the downloaded `.pkg`/app and choose **Open**, then allow it in **System Settings → Privacy &
  Security** if macOS still asks. Do not tell them to disable Gatekeeper globally.
- Prefer a `.dmg`/zip with the `.app` when the `.pkg` is hard to open; test the exact path on a clean macOS account.
- Publish SHA-256 checksums beside the release; the source is public, so the warning is at least understandable.
- Test the launcher's self-update path while unsigned; if macOS blocks it, a manual update is the fallback.
- If the project ever gets a Developer ID, notarize the launcher and game binaries; no code change should be needed.

## Unsigned Windows binaries

No Windows exe or DLL we ship has an Authenticode signature (ours and NuGet ones like `DotRecast.*`, `BepuPhysics`).
**Observed** (issue #127): Smart App Control refused `DotRecast.Recast.dll` (`FileLoadException`, `0x800711C7`) on one Windows 11
machine while other unsigned files loaded; its verdict is per-file cloud reputation, so any of them can be the one refused. The game
then shows a message box (`GameConsole.ExplainBlocked`) saying to turn Smart App Control off, and closes. Code signing every Windows
binary in `package-release.ps1` is the real fix (not set up).

## Game zips per runtime

`release.yml` and `nightly.yml` run `tools/scripts/package-release.ps1 -Rid <rid>` on one runner per RID (win-x64 on Windows, linux-x64 on Ubuntu,
both osx RIDs on macOS) and attaches all four zips to the one `v<version>` release. Each zip has a single top folder
`Meitou-<version>-<rid>/` with the game (named by `Meitou.Game`'s `AssemblyName`), `meitou-viewer` and the self-contained .NET runtime; macOS zips hold
them in `MeitouClient.app/Contents/MacOS`, the readme and licences beside the bundle. Unix zips are made with
`zip(1)` (execute bits kept), macOS binaries (and Apple Silicon dylibs; Intel ones stay as shipped, as re-signing corrupts the unpadded shaderc and spirv-cross) are
ad-hoc signed, runtime debug symbols (`*.dbg`, ~250 MB) are dropped.

Native dependencies off Windows:
- Bundled from NuGet runtimes (**Verified** in the linux-x64 publish output): `libopenal.so`/`.dylib` (OpenAL Soft 1.23.1),
  `libglfw.so.3`/`.3.dylib` (GLFW 3.4), `libshaderc_shared`.
- Only in the win-x64 zip: DLSS/Streamline 2.14.16 (`sl.*.dll`, `nvngx_dlss.dll`, `nvngx_dlssg.dll`, `NvLowLatencyVk.dll`) and FSR (`amd_fidelityfx_vk.dll`),
  copied from the vendor SDK zips by `package-release.ps1`; both load via `NativeLibrary.TryLoad` and report a reason when missing. `FsrUpscaler` also loads the Vulkan loader by name (`vulkan-1.dll`, `libvulkan.1.dylib` on macOS).
- Only in the linux-x64 zip: the DLSS model `libnvidia-ngx-dlss.so.310.9.16` (NVIDIA DLSS SDK 310.9.16, `lib/Linux_x86_64/rel`) and NGX's
  client library `libmeitou_ngx.so` (the SDK's static `libnvsdk_ngx.a`, linked by `tools/scripts/build-ngx-shim.sh`), with NVIDIA's licence,
  added by `package-release.ps1`; NGX's core comes with the NVIDIA driver.
- Linux: the Vulkan loader (`libvulkan.so.1`) and driver come from the system, as do X11/Wayland and the OpenAL backend
  (PipeWire/PulseAudio/ALSA). **Verified** (KDE on Wayland and X11, RTX 3070 Laptop with the NVIDIA driver, GLFW 3.4): the game starts, loads a large save, renders and plays with sound; other distributions, AMD and Intel GPUs were not run. Case-sensitive file systems need `GameFiles.Resolve` for paths from game data.
- macOS: no Vulkan loader or MoltenVK is bundled (no `Silk.NET.MoltenVK.Native` reference); the user installs them
  (Homebrew or the LunarG SDK). Runs on Apple Silicon, see [macos-port](macos-port.md). Browser downloads get
  Gatekeeper's quarantine flag (no Apple signing or notarisation).
- Kenshi's Steam folder is found on Windows and Linux (native, `~/.steam/steam` and Flatpak Steam; `GameInstall`); on macOS it comes
  from `KENSHI_PATH` or the console prompt.

## Files

The launcher's data folder (its program, once installed, is Velopack's, e.g. `%LocalAppData%/MeitouLauncher`) is `%LocalAppData%/Meitou`, `~/.local/share/Meitou` or `~/Library/Application Support/Meitou`, the same
folder as the game's caches (shader cache, LODs, navmesh). Beside them it holds
`launcher.json` (Kenshi folder, install folder, channel, chosen version, launch arguments, renderer, Metal memory budget, presets, language, `ModDetailsShown`, the window's normal size and whether it was maximised or full screen, restored within the primary screen) and is the game's working directory.
The game runs from that folder, so its `meitou.user.json` (and anything else it writes to the working directory) is shared by all
versions. The game gets the Kenshi folder through `KENSHI_PATH`. On macOS, **Vulkan** gets `DYLD_LIBRARY_PATH` pointing at the
launcher's `vulkan/` folder (symlinks to `libvulkan.1.dylib` and `libMoltenVK.dylib`, with the sources found under Homebrew) plus the
MoltenVK setting ([macos-port](macos-port.md#running)); Play refuses with a `brew install` hint when no loader is found. Keeping the
loader in its own folder avoids putting all of Homebrew's `libjpeg`/`libpng` on the game's dyld path. **Metal** gets neither and is
started with `--renderer metal`.
Play refuses while the game it started still runs. A game that exits with a non-zero code is reported in the status line with its
`log.txt` path.

### Renderer on macOS

The first-start dialog and Settings offer **Auto**, **Metal (native)** and **Vulkan (MoltenVK)** on macOS. Auto uses Vulkan when
its loader is installed and falls back to Metal otherwise; on Apple silicon this means a new user does not need Homebrew just to
start. The value is stored in `launcher.json` and appended to the game's arguments after **Launch arguments**, so it wins over a
hand-written `--renderer`.

Vulkan is the reference path and stays selectable; its **Setup** button copies `brew install molten-vk vulkan-loader` and opens
brew.sh, and **Check** re-detects the loader. Metal starts without the Vulkan loader and without Homebrew on
`DYLD_LIBRARY_PATH`. The launcher reads the release's `settings-schema.json` `renderers` list, so a Vulkan-only release does not
silently claim Metal support. The launcher runs installed GitHub releases, not local checkouts.

On Apple silicon, Settings also has a **Metal memory budget**: Auto (default), Safe 5120 MB, Balanced 6144 MB and, on 16 GB-or-larger
Macs, High 8120 MB. Auto passes nothing and the game sizes the ceiling to the Mac's memory (a third of it, 5120 to 10240 MB: 5376 on 16 GB,
8192 on 24 GB). It is the game's soft `VramGuard` ceiling, not a hard VRAM reservation; Apple silicon uses unified memory
shared with macOS and other apps. The launcher passes a chosen size as `MEITOU_METAL_GPU_BUDGET_MB` only when it starts Metal. A
`launcher.json` that stored 5120 before Auto existed keeps it. The
GPU foliage cull is on by default in the game ([metal-gpu-cull-plan](metal-gpu-cull-plan.md)); `MEITOU_METAL_GPU_CULL=0` is a
diagnostic the launcher passes through.

### Kenshi data

Meitou ships no Kenshi assets. The first-start dialog explains that Kenshi is required and links to
[kenshi-on-macos](kenshi-on-macos.md), which covers copying a Windows install, CrossOver/Whisky, GOG/Wine and the advanced
DepotDownloader route. The launcher's **Find** searches macOS Steam, CrossOver and Whisky locations, and **Browse** accepts any
folder containing `data/gamedata.base`. DepotDownloader is not bundled or automated: it needs Steam credentials and ownership.

## Steam overlay and play time

- In a Debug launcher run, Play uses `src/Meitou.Game/bin/Debug/net11.0/MeitouClient.exe` when available, so source changes are tested instead of an older downloaded release; Release builds continue using the selected installed version. Debug runs redirect the child's stdout/stderr into the launcher's console and debugger output.
- Settings → **Launch as Kenshi on Steam** (Windows, `LaunchAsSteamKenshi`, on by default; also asked in the first-start dialog) gates this. Off, Play sets no app ID, removes inherited `SteamAppId`/`SteamGameId` and a leftover `steam_appid.txt` holding `233860`, so the game skips Steamworks (no overlay or play time).
- On, on Windows Kenshi installs that include `steam_api64.dll`, Play writes `steam_appid.txt` (`233860`) in the working directory and beside the Meitou executable and sets `SteamAppId`/`SteamGameId` for the game, then removes the files when Meitou exits (or on the next Play, if the launcher closed first). If either place already holds another ID, it writes nothing and sets no variables. The game (not for `--screenshot` or a start listing) loads the install's Steamworks DLL and calls `SteamAPI_InitFlat` when exported, else `SteamAPI_Init`, before creating its graphics device, and `SteamAPI_Shutdown` on exit; Meitou ships no Steamworks binaries. Other platforms and installs without the DLL skip it.
- **Unknown** (from the [Steamworks API](https://partner.steamgames.com/doc/sdk/api) and [overlay](https://partner.steamgames.com/doc/features/overlay) docs, not yet checked against a Steam run): a direct launch with `steam_appid.txt` counts as Kenshi play time, needs a Kenshi licence and a running Steam client, and gets the overlay on Vulkan, whose FPS counter is under Steam Settings → In Game.

The version folders and `downloads/` live in the install folder, by default the data folder. On first start (until `FoldersConfirmed`
is saved) an overlay asks for the install folder, the Kenshi folder and the renderer, prefilled with the current and the
detected ones. Changing the install folder there moves the installed versions (copied, then deleted, across drives). In
Settings, when the old folder holds versions, a question asks to move them or keep them; kept versions stay in the old folder
and the list shows only the new folder's versions as installed.

## Graphics

The Graphics page (`GraphicsPage.cs`, `GameSettings.cs`) shows the settings the version Play starts lists in its
`settings-schema.json` (`SettingsSchema`, Meitou.Core) and edits the data folder's `meitou.user.json` as a JSON tree, keeping every
key it does not show. The game writes the schema (`--settings-schema <file>`, `LauncherSchema.cs`; `package-release.ps1` runs it
for every zip) from the names it saves under: top-level `UserConfig` properties in camelCase (`fullScreen`, `vSync`, `fpsLimit`) and
every Tab panel slider and switch under `graphics` (a slider by label, `WorldFrame.*Slider`; a switch as `Meitou: <label>`, 1 on,
0 off: the Faithful / Meitou switches and `WorldFrame.*Switch`), with names and hints from the in-game menu. So a renamed or new
setting reaches the launcher with its release; the launcher knows none of them. A version without a schema (older), or with a newer
`format`, gets no page. Rows a computer lacks (tessellation, the Meitou shadows' filter) are listed anyway; the game ignores their keys.

- The schema is also the catalogue the game's own Display and Graphics pages are drawn from ([native-ui](native-ui.md)), so the launcher's
  rows and the game's are the same: names, ranges, value text, choices, impact (`impact`, beside the name) and order.
- Each schema section is a tab: Display (with `tickAhead`, the world tick alongside drawing), then the game's Graphics sub-pages (Lighting, Water & reflections, Terrain, Vegetation, Objects, Characters, Sky & weather, Colour, Post FX, Interface)
  as `SettingsMenu.Section` / `ToggleSection` place them, switches first. Interior lamps is not listed: it changes the stealth sight, so it
  is on the game's Advanced page only, as is Lamp warmth.
- Mutually exclusive techniques are one choice each (`WorldFrame.ChoiceSliders`): `Sun shadows` (Off / Faithful cascades / Meitou cascades /
  Traced / Traced near, cascades far), `Ambient occlusion type` (Off / SSAO / GTAO), `Global illumination` (Off / Probes / Probes + hybrid).
  The switches they stand for (`WorldFrame.ChoiceDrivenSwitches`) are not listed but stay saved; the game applies a saved choice after them at
  start, so the launcher's choice wins and an older file of switches alone keeps its look.
- A section's `groups` (null in older schemas: flat rows) are the menu's features (`FeatureGrouping.FeatureGroups`): one row headed by
  a setting (`header`) or a `title` and `description`, with an arrow folding its `members` under it (closed until opened; open ones stay
  open while the launcher runs), named without the feature's name they repeat, as in the game. The Display section has two: the upscaler (render scale and its custom value, sharpness, DLSS preset) and HDR output (white and peak brightness).
- The Look row is the game's look bar: Faithful | Meitou, the preset (`looks.presets`, then the player's files), the changes from it
  (`looks.keys`, the look tabs' settings: Revert; Save for the player's own), Save as (a name), Share (an `MT1:` code on the clipboard),
  Paste, Delete (two clicks). Files and codes are the game's (`LookPresetFile`, Meitou.Core); a saved preset holds what differs from
  `looks.meitou`, marked `looks.made`.
- `looks` (null in older schemas: no row) is the Look row: Faithful (`faithfulLook` true) or a Meitou look preset (`lookPreset`: one of
  `looks.presets`, the first saved as no key, or a player's JSON file in `looks.folder` beside `meitou.user.json`, its `name` and
  `values`). A look is a set of `graphics` values, as the game's menu applies it: `looks.faithful` (every Faithful / Meitou effect
  switch off but the performance ones, the look sliders at the game's value) or `looks.meitou` with the preset's own values over them (the quality tier's
  switches are left to the tier). Choosing one writes them, so the rows show it at once and the game counts no change from it; at Low
  the game still holds the tier's look switches off at start unless kept on (`QualityTiers.ApplySavedPreset`). No `faithfulLook`
  reads Faithful when every switch Faithful turns off and Meitou turns on is saved off, as the game does (unsaved, not written back). A look slider's Default writes the look's value.
- `presets` (null in older schemas: no row) is the Quality preset row over the top-level `graphicsPreset`. The game saves the preset its
  settings sit at (Custom when none) and at start applies a saved one they do not sit at (not under `--low-end`, `--benchmark-baseline`, `--meitou` or `--faithful`), so a preset chosen in
  the launcher takes effect on the next start; the rows show the old values until then. Changing a row the preset sets (`presets.keys`)
  saves Custom, so the game keeps that change. No saved preset shows High without saved graphics, else Custom.
- Detect (when `presets.detect` names the game's option, `--recommend-preset <file>`) runs the played version's game, which makes a
  headless Vulkan device (the GPU it would pick), reads the memory and the cores, and writes `{ preset, summary, gpu, integrated,
  videoMemoryMb, memoryMb, cores, problem }`; the launcher saves `preset` as above and shows `summary`. Without Vulkan (a Metal-only
  Mac, a driver that fails `vkCreateInstance`) Apple silicon takes the GPU's name from Metal (`Apple M6`) and other machines go by memory
  and cores alone; off macOS the summary names the failure (`no Vulkan device: <problem>`).
  Apple silicon goes by its GPU (`AppleGpuTiers`, an engine choice): the chip's Geekbench 7 Metal score (Geekbench Browser, M1 to M6: the
  base chips 28,496 / 43,540 / 48,180 / 54,230 / 72,805 / 89,357; Pro, Max and Ultra in the class) predicts its frame rate in proportion,
  against an M6 that ran the launcher benchmark's bases scenario at 164 fps Low, 136 Medium and 113 High (**Observed** 2026-11-25, two runs
  each, 1920x1080 without an upscaler; GPU time 93-103% of the frame time in every group, so GPU-bound). The tier is the highest predicted at
  60 fps or more: from a score of 39.4k Medium, from 47.6k High, never above High (Ultra and Extreme, which need hardware ray tracing, have
  no M6 measurement yet): M1 Low, M2 Medium, M3 and later and every Pro, Max and Ultra High. A chip not in the table: a later generation counts as the
  newest, a missing variant is that variant's newest score scaled by the base chips (M6 Pro: M5 Pro x M6 / M5). Under 12 GB of memory Medium at
  most, under 8 GB Low; a GPU name that is not an Apple chip's goes by memory (Medium from 16 GB, else Low). A GeForce goes by its model against the classes the tiers are meant for (`GeForceTier`: RTX 3060
  Medium, 3070 to 4070 High, 4070 Ti to 4080 Ultra, 4080 Super and 4090 Extreme, other series by their place among these, a laptop
  GPU a class lower); any other card by video memory (under 6 GB Medium, under 12 GB High, under 24 GB Ultra, else Extreme); under
  12 GB memory or 6 threads Medium at most. Observed: Intel Iris Xe (integrated) is Low, in 2.5 s.
- The launcher detects once by itself (`PresetDetected` in `launcher.json`): when the version Play starts can detect and the game has no
  graphics settings or preset saved yet, so the first start (or first install) of a fresh setup gets a preset; saved settings are kept.
  A failed detection is tried again at the next start.
- Each setting has one control: a switch a Faithful (0) / Meitou (1) drop-down, a slider with `labels` a drop-down of them, a slider with `modes`
  (named values; render scale's Ultra performance 0.33 to DLAA 1.00) a drop-down of them and Custom with the slider under it, locked to the
  mode's value but under Custom (the game's Display page alike; a value none of them reads Custom), any other slider a slider with a typed value; unsaved, a row shows the game's start value (`shown`). A setting with `shownWhen` (`key`, `value`)
  is shown only while that setting is at that value (the DLSS preset while anti-aliasing is DLSS; the game's Display page alike); with `not`
  at any other value, and with `and` another condition must hold too (older launchers ignore both). A setting with `renderer` (`vulkan`
  or `metal`) is shown only while Play would start the game with that renderer (`GraphicsRenderer`, as `SetUpRenderer` picks it; no
  `renderer`: always): the Vulkan anti-aliasing and upscaling rows are Vulkan's, and on macOS the schema adds Metal's Display rows
  after the world tick: the switch `metalUseUpscaling` and two trees under it. The switch and `metalRenderDivisor` show a Retina screen's
  defaults (off, screen / 2), every Apple-silicon Mac's own screen's; unsaved, the game takes its screen's ([metal-backend](metal-backend.md)). On: `metalUpscaler` (MetalFX temporal, MetalFX
  spatial, TAA) and `metalRenderScale` (modes). Off: `metalRetinaResolution` (while `fullScreen` is off), `metalRenderDivisor` (while it is on) and `metalUiAtScreenResolution` (always;
  [metal-backend](metal-backend.md)). A look setting that differs from the look's
  value has its name in brass and a Reset link that writes it, and so does its feature's header (for all of them, as in the game); quality and display settings are never marked (the Quality row reads Custom),
  as in the game's menu. Reset graphics, in a footer
  under the scrolling rows, removes `graphics`, `graphicsPreset`, `faithfulLook` and `lookPreset`. A top-level Default writes the schema's `default`.
  Whole values are written as integers.
- A slider without `labels` takes a typed value (Enter or leaving the box; Esc restores): the suffix may stay, a comma is a decimal
  point, the value is divided by `scale`, snapped to `step` and clamped; `offText` (Unlimited, Off) means the left end.
- A slider marked `reversed` (terrain detail's error in pixels) is drawn mirrored (`min + max - value`; logarithmic: `min x max / value`),
  so right is always the better end; the value written is unchanged. Texture quality, also reversed, is a drop-down, best first.
- Read-only when the file is unreadable, or its `version` differs from the schema's `settingsVersion` (another layout: the keys may
  mean something else). Warnings: the game is running (it writes the file on exit), `--low-end` or `--benchmark-baseline` (the game ignores `graphics`).

The Settings page's Language picks the game's and the launcher's language: English, Kenshi's nine languages, the install's `locale/` folders and the
languages Meitou has texts for, marked when Meitou's own texts are not translated. Choosing one writes only the `language` line of the Kenshi
folder's `settings.cfg` (encoding and line endings kept) and remembers the code in `launcher.json` (`Language`, used without a Kenshi folder); at
start the `settings.cfg` value wins. The launcher's labels come from the same `.po` catalogs as the game's Meitou texts
([localisation](formats/localisation.md)), without the mods' `meitou.po`; a string without a translation stays
English. Mod names, news and release notes are data, not translated.
Labels the layout holds are translated by one walk of the visual and logical trees (texts, button captions, placeholders, tooltips; a control
never shown has only logical children). Combo box items are data and not walked, except the label items of the Mac renderer and memory
boxes (`LabelCombos`); the closed box keeps the content its selected item had when chosen, so a selection-box template redraws it in the
current language rather than a re-selection, which would run the box's change handlers. Texts the code sets later (hints, statuses) go in as
inlines, which the walk leaves alone, or it would put back the first text it saw. A text the code
builds with a value in it is a catalog key with the game's `{1}`, `{2}` placeholders (`Tr("Downloading {1}...", title)`), never an
interpolated string, which no translation could match.

`meitou.user.json` layout (`UserConfig`; **Verified** by loading crafted files through the built game 2026-10-20):
- `version` (no key: 1) is raised when a key is renamed, retyped or reinterpreted; `UserConfig.Migrate` converts older files step by
  step, moving a renamed key only where the new one is missing. A slider whose numbering changes takes a new label instead
  (`UpscalerSliders`). A newer file keeps its `version` and its unknown top-level keys (`[JsonExtensionData]`) when an older release saves.
- A value of the wrong type is dropped by its JSON path and takes its default; the rest stay. Unparsable JSON gives all defaults.
  Either way the file is first copied to `meitou.user.<yyyyMMdd-HHmmss>.bak.json`.

## Mod list

`ModList` (Meitou.Core) holds the discovery and `mods.cfg` rules shared with `LoadOrder`
([overview](formats/overview.md#load-order)):

- Shown as the original launcher shows it: enabled mods in `mods.cfg` order first, then the rest after their header dependencies.
  Mods in a dependency cycle are kept at the end, where the original drops them. Mods not in `__mods.list` are new and start enabled.
- Ctrl+click and Shift+click select several mods; dragging a selected row's handle or Alt+Up / Alt+Down moves them all together,
  keeping their gaps, and stops at the list's ends.
- Search matches names and authors, ignoring case. **Hide incompatible RE_Kenshi plugins** hides DLL plugins without Meitou code or
  a built-in equivalent. An endorsed port is a separate mod. Hidden rows retain their enabled state and load order. Select all / Select
  none act on visible rows.
- The list can show load order, alphabetical order, date added (installed `.mod` file creation time) or last modified (its last write).
  Date sorts show newest first. Manual reordering is available in **Load order**.
- Every change to the list (a tick, a drag, a preset, Arrange, From save) and Play write `mods.cfg` (enabled `<name>.mod`, list
  order, CRLF) and `__mods.list` (every listed name, plus old names not on disk). These are the files the original game reads, so the mod list applies to it too.
- Discovery and the header reads run off the UI thread while the window opens; until they finish (or when they fail) Play and the
  preset buttons refuse, and nothing is saved, so an unloaded list never overwrites `mods.cfg`.
- The header counts the mods (and, while searching, those shown) and the enabled ones. One button ticks every shown mod, or unticks them
  all once every one is ticked (Select all / Select none). The list scrolls smoothly with the mouse wheel (`SmoothWheelScroll`).
- **Details** (the header, kept as `ModDetailsShown`, on by default) lets the side pane right of the list slide open while a mod is
  selected (shut with none, so not at start): the selected mod's name,
  source and author, its notes (red when the game would refuse it), its `.mod` header dependencies (each one not enabled above it in red),
  its description and its folder. The description's Steam BBCode is drawn (`BbCodeView`: headings, bold, italic, underline, strike,
  links, lists, quotes, code, rules, tables, spoilers, noparse); unknown or unmatched tags stay literal, only http(s) links open and
  `[img]` is a link, never fetched.
- **Save current preset** updates the selected preset. **Save as preset** saves under a typed name. Both capture the full load order
  and enabled flags, including hidden rows.
- A preset is a named, ordered list of names with enabled flags. Applying one reorders the list. Mods the preset doesn't name go
  to the end, disabled, and mods it names that aren't on disk are reported. Applying saves.
- From save lists the save folders (with `quick.save`) of Meitou's save root, the install's `save` and the per-user Kenshi save folder,
  newest first and labelled by root, and applies the chosen save's CAMERA `mods` list ([save](formats/save.md#camera-type-56-id-1051-the-world-record))
  as an all-enabled preset, without `base` and the core mods. `SaveModList` (Meitou.Core) reads it; **Verified** equal to
  `SaveCamera.Mods` on 22 of the owner's saves (0 to 180 mods).
- An enabled mod that blanks mods above it reads "Empties file paths in N records of A, B, C and K more: it likely belongs above them"
  (`ModBlanking`, `ModRecordScan`, Meitou.Core; [why](animation.md#animation-files)). It counts a record when it is stated whole (flag
  bit 0 clear), empties more of the filename values set so far than it gives itself, and the mod its string id names is enabled above it;
  REMOVED resets the record. Not counted: changes (bit 0 set), the base game's records, stubs for mods that are not enabled, records of a
  renamed mod (the id keeps the old name), and patches that overwrite with full copies instead of blanks. It also names a mod that
  restates another's record with most of its paths cleared, which may be meant (**Observed**: two armour records of one overhaul among
  363 workshop files). The data files are scanned once per list load, off the UI thread (363 files, 131 MB: 0.14 s from a warm file
  cache; a cold start with a long list is not measured), and the list is walked again on every change.
- A mod whose `RE_Kenshi.json` names DLLs (`Plugins`, `PreloadPlugins`) is marked as a plugin mod whose DLLs Meitou ignores
  ([RE_Kenshi](formats/rekenshi.md)), unless it also has `meitou.json`: then only the code-mod note shows. One with `FileRebinds` says
  they are applied to the map files, one with `SoundBanks` that they load before the game's ([data entries](formats/rekenshi-data.md)).
  The ignored plugin and code mod subtitles use distinct colours.
- A mod Meitou has built in (`CoveredMods`, Meitou.Core, the table the game uses: [RE_Kenshi](formats/rekenshi.md)) says so and names
  the setting it became and that the mod is not run, instead of the plugin or code-mod note.
- An RE_Kenshi plugin (no `meitou.json`) with an endorsed port on the Steam workshop (see [RE_Kenshi compatibility](formats/rekenshi.md),
  matched by workshop item or known `.mod` name) names the port and its maintainer. If a mod has multiple endorsed ports, the row shows
  the first installed one or offers the default. The list identifies installed ports by workshop item or known local `.mod` name. When
  none is installed, the row has a **Get the port** button. The button, and once per mod the first listing in which such a
  mod is enabled and its port is not installed (one question for all of them), shows a table of each RE_Kenshi mod beside its port and
  maintainer and asks whether to subscribe now: **Subscribe** ([Workshop subscribe](#workshop-subscribe); offered only where Steam can
  be asked), **Open its page** (the ports' workshop pages) or No thanks. Each mod asked about on a listing is kept in `launcher.json`
  (`OfferedPorts`) and not asked again. Not asked while another question or the first-start dialog is open.
- A covered workshop item can record the Steam update it was checked against. When the installed copy is newer (its `timeupdated` in
  `steamapps/workshop/appworkshop_233860.acf`, `WorkshopUpdates`), the row adds that it has updated since, with the date, and that some new
  features may be missing. A local copy has no date and gets no such note.
- Any other RE_Kenshi plugin (not built in, no endorsed port) keeps the ignored-DLLs note and has a Request button opening the public wishlist
  of RE_Kenshi mods (`CoveredMods.Wishlist`), where players ask for one to be covered.
- A mod folder with `meitou.json` is marked as a code mod ([modding](modding.md)); enabled, its code runs, with no question
  ([Trust](modding.md#trust)).
- A code mod's row lists what it requires, loads after and conflicts with, and each refusal the game would make without its code
  (preliminary: DLLs and initialization are checked only at start); **Arrange** proposes an
  order meeting the enabled mods' code and `.mod` header dependencies, applied only when accepted and saved only by Save or Play; a
  contradiction moves nothing and is shown ([dependencies](modding-dependencies.md#launcher)).

## Workshop sync (macOS)

Kenshi has no Mac build, so the macOS Steam client never installs it, but it still downloads the Kenshi Workshop items of an account that owns
the game. The mods page's **Sync workshop** button (`WorkshopSync`, `SteamUgc`, `MacSteam` in `Meitou.Core/Steam`) asks that client to download
or update the subscribed items and waits until they are installed; unsubscribing stays in Steam (client or web).

- The button is shown only on macOS and only when the client's own `libsteam_api.dylib` exists
  (`~/Library/Application Support/Steam/Steam.AppBundle/Steam/Contents/MacOS/Frameworks/Steam Helper.app/Contents/MacOS/`, hardcoded). Without it
  nothing is done. Meitou ships no Steam binary.
- Steam is not started: when its process (`steam_osx`) is absent, the status asks the user to start it and sign in first.
- The sync runs in a **child process**: the launcher's own executable started with `--macos-workshop-sync` (`WorkshopSyncChild`, no window), which
  prints `meitou-sync:`-prefixed `progress`, `result` or `problem` lines that `WorkshopSyncRunner` reads. Steam counts the account as playing Kenshi
  until the process that initialised Steamworks exits, not until `SteamAPI_Shutdown`; in the launcher's own process the client then waited on
  "Kenshi" when quitting (**Observed**: `gameprocess_log.txt` tracked the launcher's PID from the sync until Steam itself quit, while the spike's
  short-lived processes were untracked at exit).
- The child sets `SteamAppId`/`SteamGameId` (233860) in the real process environment (`setenv`; .NET's own copy is not seen by native code),
  calls `SteamAPI_InitFlat`, then every 0.5 s (`WorkshopSyncWatch`) runs the callbacks, reads `GetSubscribedItems` and each item's
  `GetItemState` (current = Installed without NeedsUpdate, Downloading or DownloadPending), calls `DownloadItem` for an item that is not current,
  and prints `installed of total` when either changes. It ends once every item is current and neither the subscriptions nor a state has
  changed for 5 s (an engine choice); then `SteamAPI_Shutdown` and it exits. The mod list is read again at the end. A process that has just
  started as Kenshi gets the client's cached answers: **Observed** (Steam restarted, 3 items subscribed on the Workshop since): the first
  `GetSubscribedItems` listed the 10 old items, the next poll 0.5 s later 13, the new ones `Subscribed|NeedsUpdate`, and the client began
  downloading them without a `DownloadItem` (installed after 3.5, 14.6 and 16.2 s). **Observed** (the owner, with updates pending): a sync
  that read the subscriptions and states once reported every item current, and a second one seconds later found the updates.
- An item unsubscribed keeps its folder while a process runs as Kenshi: **Observed** (one item unsubscribed on the Workshop): its folder was
  still there after a sync had run 60 s, and gone right after the next sync started. So the child also prints the item folders that are not
  subscribed (`unsubscribed <id>...`); the launcher keeps them in `launcher.json` (`WorkshopUnsubscribed`, replaced by each finished sync, an
  item dropped when the Subscribe button subscribes to it) and the Mods page leaves them out (`ModList.Discover`), as the original game, which
  asks Steam for the subscribed items, does.
- While it runs, Steam shows the account as playing Kenshi. The button reads **Stop syncing** meanwhile; stopping (or closing the launcher) kills
  the child, Steam keeps downloading by itself.
- Items land in the client's `steamapps/workshop/content/233860/<id>/` (one `.mod` each). `GameInstall.WorkshopDirectory` falls back to that
  folder on macOS when the install is not inside a Steam library, so the launcher and the game find them whether or not Steam is running.

**Verified** (macOS Steam client, the owner's account, 19 subscribed items, 7.3 GB, Kenshi not installed): `SteamAPI_InitFlat` succeeds as app
233860 with the bundled dylib; `GetSubscribedItems` lists all 19; after `DownloadItem` all read state 5 (Subscribed|Installed, before: 9) with their
folders; `SubscribeItem` alone downloads an item within a second (11 KB), `UnsubscribeItem` drops the count and Steam deletes the folder within about
20 s; with Steam closed `SteamAPI_InitFlat` fails ("Could not determine Steam client install directory") and does not start it.
**Observed**: Steam's `content_log.txt` shows the subscriptions' download starting when the first process that queried them ran as app 233860, and
`DownloadItem` only raising its priority ("Priority First"); `GetItemDownloadInfo` reported 0 bytes throughout, so progress is the installed count.

## Workshop subscribe

**Subscribe** in the endorsed-port question has the running Steam client subscribe the account to the ports and download them
(`WorkshopSubscribe`, `SteamUgc`, `SteamLibrary` in `Meitou.Core/Steam`), as the button on their workshop pages does.

- The Steam API library is one the user already has (`SteamLibrary.Locate`): the Kenshi install's `steam_api64.dll` on Windows, the Steam
  client's `libsteam_api.dylib` on macOS ([Workshop sync](#workshop-sync-macos)). Without one (a Kenshi without Steam, no Kenshi folder
  set, Linux: the install's library is a Windows DLL) the question has no Subscribe button, only the workshop pages.
- Kenshi's library is an older generation than the client's: no `SteamAPI_InitFlat` (so `SteamAPI_Init`, which gives no reason) and no
  versioned accessors, so `ISteamUGC` and `ISteamUtils` come from the `SteamClient` export's `ISteamClient` by version string,
  `STEAMUGC_INTERFACE_VERSION008` and `SteamUtils008`. **Verified** (Kenshi 1.0.68, Steam): those strings are in `kenshi_x64.exe`, and the
  DLL exports `SteamAPI_Init`, `SteamClient`, `SteamAPI_GetHSteamUser`/`Pipe`, `SteamAPI_ISteamClient_GetISteamUGC`/`Utils`,
  `SteamAPI_ISteamUGC_SubscribeItem`/`GetItemState`/`DownloadItem` and `SteamAPI_ISteamUtils_IsAPICallCompleted`/`GetAPICallResult`.
  A current library's accessors (`SteamAPI_SteamUGC_v0NN`, `SteamAPI_SteamUtils_v0NN`) are found by trying the version numbers.
- It runs in a child process, the launcher started with `--workshop-subscribe <library> <id>...` (`WorkshopSubscribeChild`,
  `WorkshopSubscribeRunner`), for the reason the sync does and so a library that misbehaves cannot take the launcher down; the launcher
  ends it after 4 minutes. Steam is not started.
- Per item: one already subscribed is left; else `SubscribeItem`, then up to 20 s for its call result
  (`RemoteStorageSubscribePublishedFileResult_t`, callback 1313: an `EResult`, then the id; 16 bytes on Windows, 12 elsewhere). The item
  counts as subscribed when the result is OK or its state has Subscribed. `DownloadItem` is then asked for, and the child waits up to
  90 s for the items to be installed before answering.
- Afterwards the status line names the ports subscribed to (and says when Steam is still downloading) and the list is read again; a new
  mod starts enabled. For each port Steam did not subscribe to, a second question gives the reason (Steam not running or not signed in,
  the item unknown, no answer in time, the child failing) and offers its workshop page instead.

**Verified** (Windows, Kenshi 1.0.68's `steam_api64.dll`, Steam not running): the child loads the library, `SteamAPI_Init` fails, it
answers "Steam could not be reached" and Steam is not started. **Verified** (same, Steam running, the owner's account, 2026-11-18): the
child subscribed to item 3841575072, which was not subscribed, and answered `item 3841575072 1` after 11 s with the item's folder in
`steamapps/workshop/content/233860/`. **Unknown:** whether that success came from the call result or from the item's state, and every
refusal reason (none was provoked).

## Continue

**Continue** is the middle part of the Play button, in its larger type,, shown while a save exists, with the newest save's name under it (rescanned
when the window is activated and when the game exits); Play beside it reads **Main menu** over the version it starts. Continue starts
the same version with `--load <folder>` (game 0.10.0 and later) for the
newest slot From save lists (newest `quick.save` over the three roots), only when its CAMERA `mods` list holds the enabled mods, order
ignored, names compared case-insensitively. Otherwise it starts nothing and the status says why: other mods (which are not enabled,
which are not in the save), no mod list, an unreadable save, or Launch arguments that already pick a start (`--load`, `--continue`, `--new-game`,
`--no-population`, `--duel`, `--sandbox`, `--battle`, `--terrain-sandbox`; the game refuses `--load` beside the others).

## News

The Home page shows the repository's `news/` folder (format in `news/README.md`): one folder per article,
`<yyyy-mm-dd>-<slug>/index.md` with an optional front matter block (`title`, `date`, `author`, `image`, `video`: the full video's page, a "Full video" button on a video cover) and its pictures beside it, newest
20 first. `news/` is listed through the contents API on `master` (one call, cached with its ETag in `news-cache/`); each
`index.md` downloads from raw.githubusercontent.com and is kept by its folder's tree hash, images by URL, so the page works
offline. Relative image and link paths resolve against the article's folder; a folder without `index.md` is skipped.
Debug builds run from a checkout read its own `news/` from disk instead (found above the executable next to `MeitouClient.slnx`),
to preview posts before they are pushed; the header then says "Debug: local news/". Markdown is parsed by Markdig and drawn
by `MarkdownView` (headings, paragraphs, emphasis, code, links, lists, quotes, code blocks, rules, images); raw HTML is dropped
and links open only for http(s).

News videos are hosted on the site, not in git: `tools/scripts/upload-media.sh` copies them into the `meitou-site` container's
`/usr/share/nginx/html/media` volume on Cloudovna (5 GB disk), served at `https://meitou.kenshi.wiki/media/<name>` with Range
support; the site is not behind Cloudflare. **Observed** 2026-10-25: GitHub plays no repository `.webm` in the browser (its file page
offers only the raw file, which raw.githubusercontent.com sends as an attachment), so it cannot host the full video either.

A `.webm` in place of an image is a video (`VideoView`, the post's full width): its first MB is fetched with a Range request (`NewsMediaCache.HeadAsync`, cached as `.head`) for the poster, the whole file (cached like an image) only on the first Play, the button dimmed meanwhile; `WebmFile` reads the
first video and audio track (Matroska elements, Block lacing), and the first decoded picture is the poster under a play button.
A click plays it; then a click or Space pauses and resumes, the bottom bar (shown while paused or hovered) skips 10 s back or forward (or Left/Right) and seeks by click or drag. Every play, resume or seek is a new session from that time: decode from the keyframe before it without drawing, show it, then sound from its first Vorbis packet at or after it (Vorbis can start at any packet). Sessions of all videos run one after another, so two never hold OpenAL's current context. One plays at a time; it pauses when the Home page is left, returns to the poster at the end or when the post is rebuilt.
Pictures: VP8/VP9 through libvpx (`VpxDecoder`, BSD), 8-bit only, converted BT.601 studio range to BGRA. Sound: Vorbis only
(`CodecPrivate` holds the three Xiph-laced headers, fed with the frames to NVorbis as packets), played by OpenAL Soft as 16-bit
stereo or mono; pictures follow the sound's clock, a stopwatch without sound. A late picture is decoded but not drawn. Anything
else (AV1, Opus sound, no libvpx) leaves the video out or silent.
libvpx comes from vcpkg (`VCPKG_COMMIT` in `launcher-release.yml`, libvpx 1.16.0): a publish links its static library into the
executable (`DirectPInvoke`) from the git-ignored `vpx-runtime/<triplet>/` (vcpkg's `installed/<triplet>`; CI builds and caches
it). vcpkg's Windows libvpx is static only, so a Debug build copies `vpx-runtime/x64-windows/bin/vpx.dll`, made from it with
`link /DLL /DEF:vpx.def` exporting the six `vpx_codec_*` calls `VpxDecoder` imports; Linux/macOS Debug builds load the system's libvpx.
**Verified** 2026-10-25: VP8 and VP9 + Vorbis clips from ffmpeg demux and decode in a console check (pictures match ffmpeg's
within rounding, sound length matches), and the win-x64 publish contains libvpx 1.16.0. OpenAL Soft is embedded like Skia (`native/`).

## Benchmark

The Benchmark page (`Benchmarks/`) runs the game's launcher benchmarks and submits them to the reports service
(`https://gear-pastry-chorus.cloudovna.app`, the game's `FeedbackSender.ServiceUrl`). Everything it writes is under `benchmark/` in the data
folder, never in the installed versions or Kenshi's save folder:
`builds/<version>-<rid>/` (the build, with its `benchmark-scenarios.json`), `downloads/` (the save zip, kept), `save/` (the unpacked save),
`save-copy/` (made fresh before each run), `proton-prefix/`, `sessions/<version>/<platform>/<scenario>/<tier>/run1|run2/` (the game's
`--benchmark-out`; `submitted.txt` holds the service's id once submitted).

- Build: every game release GitHub lists, marked target or not from `GET /bench/targets`; the newest target is chosen. Install (shown when
  not yet installed) or Start unpacks its zip for the platform (`win-x64` under Proton) into `builds/`, then `--benchmark-list <file>` names
  its scenarios. A version installed in `versions/` that GitHub does not list (a build made from source, such as a `99.0.0-dev` copied there)
  is offered first, never a target (so never submitted); Install or Start copies it anew into `builds/`, so its list is asked for again.
- Platform: the host's build (`native` on Linux, `windows`, `macos`), and on Linux each Proton with a `proton` script in
  `steamapps/common/Proton*` of every Steam library (`libraryfolders.vdf`) or in `compatibilitytools.d/` (Flatpak Steam's are not used).
  Proton runs `python3 <proton> run ./MeitouClient.exe` from the build's folder with `STEAM_COMPAT_DATA_PATH` = `proton-prefix/` (made
  first: Proton refuses a missing one) and `STEAM_COMPAT_CLIENT_INSTALL_PATH` = the Steam folder; every path the game gets (arguments,
  `KENSHI_PATH`) is `Z:` + the path with backslashes. On macOS (`BenchmarkPlatform.Renderer`) Apple silicon runs the benchmark on **Metal** when the
  build's list names it in `renderers` (`--renderer metal` on the probe and every run, no Vulkan loader needed), else on Vulkan with the
  loader's environment like Play, refusing without one. The listing starts the game with neither. Every inherited `MEITOU_METAL_*`
  variable is cleared from the game's environment.
- Choices: scenario check boxes from the build's list (`bases` ticked; before install only `bases`), tiers Low..Extreme with Medium and the
  tier the played version's `--recommend-preset` names ticked (nothing is saved to `meitou.user.json`) and Recommended / All quick links;
  Ultra and Extreme are greyed ("Needs ray tracing") once a probe said `rayQueries: false`. The estimate is two runs per ticked scenario
  and tier, each its `seconds` plus 90 s (8 min while the list is unknown).
- Start: before the first target benchmark (`AutoSubmit` unset in `launcher.json`), a dialog asks whether to submit results automatically
  after finishing (with an optional handle field); the choice is saved and can be toggled on the Run row. Start then ensures the build and
  list, downloads/unpacks the save only when a ticked scenario has `save` (checked against `ceb2fb18…`, redownloaded once on mismatch), runs
  `--benchmark-probe <file>` (kept as `Hardware` in `launcher.json`, seeding `MachineId` if empty), then per scenario and tier runs two
  launches of `--benchmark <tier> --benchmark-scenario <id> --benchmark-out <run dir>` plus `--load <save copy>` for a scenario with a save.
  The native game works from the data folder like Play; neither gets Steam's app id variables. Exit 5 (no ray queries) and 7 (unknown
  scenario) skip the session with a notice; 6 (save hash), any other non-zero code, no `result.json`, or 30 minutes stop the Start with the
  reason. Cancel and closing the window kill the game's process tree. Play refuses while a benchmark runs and the page refuses while the
  game Play started runs. When `AutoSubmit` is on and the build is a target, completed unsubmitted sessions are submitted automatically upon
  normal completion.
- Results: one framed table (`BenchmarkTable`) for the chosen build and platform with scenario and tier tabs above it (plus an `All tiers`
  overview when a scenario has multiple tiers, rows clickable to open that tier's groups). Each session shows its `groups` in `result.json`
  order (`all` / Whole run last) with each run's `avgFps`, `p99Ms`, `over33Pct`. A single Submit button submits all unsubmitted complete
  sessions for the build and platform (or resubmits all once every one is submitted); `POST /bench` sends one session per request with both
  runs agreeing on scenario, tier, version and fingerprint, a target build whose version matches `game.version`, and the auto-generated
  machine id (`BenchmarkMachineId`: GPU + OS family slug plus a 6-hex-character SHA-256 suffix of a per-PC OS identifier — Windows
  `MachineGuid`, Linux `/etc/machine-id`, macOS `gethostuuid`, falling back to `InstallId` — never editable and never exposing the computer
  name or raw OS id). The body is `{ installId, handle, machine, platform, target, runs, summary }`: the runs' `result.json` as written,
  run 2's `summary.md`, `machine` = `{id, name: "", notes}`, `cpu`, `gpu`, `ramGb`, `os`, `osFamily` from run 2's `hardware` and
  `resolution` from the primary screen.
- `launcher.json` `Benchmark`: `InstallId` (a GUID made at the first submit or as a machine-id fallback), `Handle`, `MachineId` (generated
  automatically from the probed hardware and per-PC hash, not editable), `MachineNotes`, `MachineLocked` (set by the first accepted submit,
  locking `MachineNotes`), `AutoSubmit` (`null` until asked on first target benchmark or toggled; defaults to `true` and remembered between
  launches), `Hardware`.
- **Verified** (2026-11-23, `BenchmarkSave` run from a scratch program on the published zip, Linux): unpacking, a re-unpack over a changed
  folder and two run copies all give the save hash `ceb2fb18…`. The zip holds the save as its single top folder and stores its folders
  without write permission (`dr-xr-xr-x`), so the launcher adds the owner's after unpacking.

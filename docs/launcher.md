# Launcher

`launcher/Meitou.Launcher` (Avalonia, `meitou-launcher`): installs game releases from GitHub, edits the mod list like Kenshi's own
launcher, keeps mod presets and starts the game. Styled after the Meitou UI kit ([meitou-ui](meitou-ui.md)) with its own title bar; Exo 2
is read from the Kenshi install, nothing is shipped. Mods come only from disk (`mods/` and downloaded workshop
items); the launcher never downloads or hosts mods.

## Releases

- One repository, two tag families: the game `v<version>` (`release.yml`, nightlies `nightly.yml`), the launcher `launcher-v<version>`
  (`launcher-release.yml`, changelog `launcher/CHANGELOG.md`). A full launcher release is marked Latest and game releases never
  are, so `/releases/latest` (README's link) is the newest launcher; README's
  setup links are `/releases/latest/download/<name>`, so the Setup/AppImage/pkg names must stay free of the version. Full releases of both pass
  `--notes-start-tag` with the previous tag of their own family, so the generated notes never span the other family's tag.
- Both tag the commit the run started on with `GITHUB_TOKEN`. **Verified** (throwaway test workflow, 2026-10-22): GitHub refuses that
  token a tag or release (HTTP 403 "Resource not accessible by integration") on a commit whose `.github/workflows/` files differ from
  the current master, as it lacks the `workflows` permission. So pushing a workflow edit while a release runs fails its release step;
  re-run it from a new dispatch, or publish the run's artifacts by hand with `gh release create` (launcher 1.1.1 was).
- Nightlies: `nightly.yml` runs twice a day (05:00 UTC "nightly", 16:00 UTC "daily", or by hand) and publishes
  `v<X.Y.Z+1>-nightly.<yyyymmdd>`, a second build on the same UTC day `….<yyyymmdd>.2`
  (patch after the newest full release) as a pre-release without a changelog section; its notes list every
  commit since the newest game tag (GitHub's generated notes cover only merged pull requests, so nightlies don't use them). It is skipped when nothing
  outside `news/`, `site/`, `launcher/`, `docs/` and `.github/workflows/` changed since the newest game tag (nightly or full). Only the newest 14
  nightlies are kept, so the releases page and the launcher's Versions list stay short. Each nightly's commit list is posted to Discord
  as the Tinfist bot (secret `DISCORD_BOT_TOKEN`, repository variable `DISCORD_NIGHTLY_CHANNEL`), else through the `DISCORD_WEBHOOK_URL`
  channel webhook, when set. The launcher shows them with the Nightly builds checkbox beside Play ticked ("Play latest nightly"; ticking it asks first, with Yes under the pointer, warning of crashes and corrupted saves) (GitHub pre-releases; `IncludePrereleases` in `launcher.json`). Installed versions stay listed either way; an update is offered only for a release newer than the newest installed.
- Game assets: `Meitou-<version>-<rid>.zip`, `<rid>` as .NET's `RuntimeInformation.RuntimeIdentifier` (`win-x64`, `linux-x64`,
  `osx-x64`, `osx-arm64`). A release with no zip for the machine's RID is listed but not installable.
- The list comes from the REST API without a token (60 requests an hour per IP). Every page is read (`per_page=100`, until a short page),
  cached together with page 1's ETag in `releases-cache.json`; an unchanged page 1 is a 304 that costs no quota and keeps the cache. Downloads use the asset URLs, which don't count
  against the quota.
- A download is checked against the asset's `digest` (`sha256:<hex>`) when GitHub lists one. The zip's single top folder becomes
  `versions/<version>/`. The game is `MeitouClient[.exe]` since 0.15 (`meitou[.exe]` before; the launcher takes either); on Unix
  it gets its execute bits. Cancelling a download removes the partial zip and unpacked folder.

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
`Meitou-<version>-<rid>/` with the game (named by `Meitou.Game`'s `AssemblyName`), `meitou-viewer` and the self-contained .NET runtime. Unix zips are made with
`zip(1)` (execute bits kept), macOS binaries and dylibs are ad-hoc signed, runtime debug symbols (`*.dbg`, ~250 MB) are dropped.

Native dependencies off Windows:
- Bundled from NuGet runtimes (**Verified** in the linux-x64 publish output): `libopenal.so`/`.dylib` (OpenAL Soft 1.23.1),
  `libglfw.so.3`/`.3.dylib` (GLFW 3.4), `libshaderc_shared`.
- Only in the win-x64 zip: DLSS/Streamline 2.14.16 (`sl.*.dll`, `nvngx_dlss.dll`, `NvLowLatencyVk.dll`) and FSR (`amd_fidelityfx_vk.dll`),
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
`launcher.json` (Kenshi folder, install folder, channel, chosen version, launch arguments, renderer, Metal memory budget, presets, language) and is the game's working directory.
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

On Apple silicon, Settings also has a **Metal memory budget**: Safe 5120 MB (default), Balanced 6144 MB and, on 16 GB-or-larger
Macs, High 8120 MB. It is the game's soft `VramGuard` ceiling, not a hard VRAM reservation; Apple silicon uses unified memory
shared with macOS and other apps. The launcher passes it as `MEITOU_METAL_GPU_BUDGET_MB` only when it starts Metal. The
experimental GPU foliage cull is deliberately not exposed here or in the game: it has caused GPU progress timeouts, so the
launcher clears any inherited `MEITOU_METAL_GPU_CULL` before Play.

### Kenshi data

Meitou ships no Kenshi assets. The first-start dialog explains that Kenshi is required and links to
[kenshi-on-macos](kenshi-on-macos.md), which covers copying a Windows install, CrossOver/Whisky, GOG/Wine and the advanced
DepotDownloader route. The launcher's **Find** searches macOS Steam, CrossOver and Whisky locations, and **Browse** accepts any
folder containing `data/gamedata.base`. DepotDownloader is not bundled or automated: it needs Steam credentials and ownership.

## Steam overlay and play time

- In a Debug launcher run, Play uses `src/Meitou.Game/bin/Debug/net10.0/MeitouClient.exe` when available, so source changes are tested instead of an older downloaded release; Release builds continue using the selected installed version. Debug runs redirect the child's stdout/stderr into the launcher's console and debugger output.
- Settings → **Launch as Kenshi on Steam** (Windows, `LaunchAsSteamKenshi`, on by default; also asked in the first-start dialog) gates this. Off, Play sets no app ID, removes inherited `SteamAppId`/`SteamGameId` and a leftover `steam_appid.txt` holding `233860`, so the game skips Steamworks (no overlay or play time).
- On, on Windows Kenshi installs that include `steam_api64.dll`, Play writes `steam_appid.txt` (`233860`) in the working directory and beside the Meitou executable and sets `SteamAppId`/`SteamGameId` for the game, then removes the files when Meitou exits (or on the next Play, if the launcher closed first). If either place already holds another ID, it writes nothing and sets no variables. The game (not for `--screenshot` or a start listing) loads the install's Steamworks DLL and calls `SteamAPI_InitFlat` when exported, else `SteamAPI_Init`, before creating its graphics device, and `SteamAPI_Shutdown` on exit; Meitou ships no Steamworks binaries. Other platforms and installs without the DLL skip it.
- **Unknown** (from the [Steamworks API](https://partner.steamgames.com/doc/sdk/api) and [overlay](https://partner.steamgames.com/doc/features/overlay) docs, not yet checked against a Steam run): a direct launch with `steam_appid.txt` counts as Kenshi play time, needs a Kenshi licence and a running Steam client, and gets the overlay on Vulkan, whose FPS counter is under Steam Settings → In Game.

`versions/` and `downloads/` live in the install folder, by default the data folder. On first start (until `FoldersConfirmed`
is saved) an overlay asks for the install folder, the Kenshi folder and the renderer, prefilled with the current and the
detected ones. Changing the install folder there or in Settings moves the installed versions (copied, then deleted,
across drives).

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
- Each schema section is a tab: Display (with `tickAhead`, the world tick alongside drawing), then the game's Graphics sub-pages (Lighting, Sky & weather, Colour, World, Characters)
  as `SettingsMenu.Section` / `ToggleSection` place them, switches first. Interior lamps is not listed: it changes the stealth sight, so it
  is on the game's Advanced page only.
- A section's `groups` (null in older schemas: flat rows) are the menu's features (`SettingsMenu.FeatureGroups`): one row headed by
  a setting (`header`) or a `title` and `description`, with an arrow folding its `members` under it (closed until opened; open ones stay
  open while the launcher runs), named without the feature's name they repeat, as in the game.
- The Look row is the game's look bar: Faithful | Meitou, the preset (`looks.presets`, then the player's files), the changes from it
  (`looks.keys`, the look tabs' settings: Revert; Save for the player's own), Save as (a name), Share (an `MT1:` code on the clipboard),
  Paste, Delete (two clicks). Files and codes are the game's (`LookPresetFile`, Meitou.Core); a saved preset holds what differs from
  `looks.meitou`, marked `looks.made`.
- `looks` (null in older schemas: no row) is the Look row: Faithful (`faithfulLook` true) or a Meitou look preset (`lookPreset`: one of
  `looks.presets`, the first saved as no key, or a player's JSON file in `looks.folder` beside `meitou.user.json`, its `name` and
  `values`). A look is a set of `graphics` values, as the game's menu applies it: `looks.faithful` (every Faithful / Meitou effect
  switch off but the performance ones, the look sliders at the game's value) or `looks.meitou` with the preset's own values over them (the quality tier's
  switches are left to the tier). Choosing one writes them, so the rows show it at once and the game counts no change from it; at Low
  the game still holds the tier's look switches off at start unless kept on (`SettingsMenu.ApplySavedPreset`). No `faithfulLook`
  reads Faithful when every switch Faithful turns off and Meitou turns on is saved off, as the game does (unsaved, not written back). A look slider's Default writes the look's value.
- `presets` (null in older schemas: no row) is the Quality preset row over the top-level `graphicsPreset`. The game saves the preset its
  settings sit at (Custom when none) and at start applies a saved one they do not sit at (not under `--low-end`, `--meitou` or `--faithful`), so a preset chosen in
  the launcher takes effect on the next start; the rows show the old values until then. Changing a row the preset sets (`presets.keys`)
  saves Custom, so the game keeps that change. No saved preset shows High without saved graphics, else Custom.
- Detect (when `presets.detect` names the game's option, `--recommend-preset <file>`) runs the played version's game, which makes a
  headless Vulkan device (the GPU it would pick), reads the memory and the cores, and writes `{ preset, summary, gpu, integrated,
  videoMemoryMb, memoryMb, cores, problem }`; the launcher saves `preset` as above and shows `summary`. Without Vulkan (a Metal-only Mac)
  the memory and cores decide alone. The thresholds (`HardwareProbe.Recommend`) are a judgement, **Unknown** whether they suit: integrated
  GPU (Apple's: under 16 GB memory), under 3 GB video memory, 8 GB memory or 4 threads is Low; under 6 GB, 12 GB or 6 threads Medium;
  under 10 GB video memory High; else Ultra. Observed: Intel Iris Xe (integrated) is Low, in 2.5 s.
- The launcher detects once by itself (`PresetDetected` in `launcher.json`): when the version Play starts can detect and the game has no
  graphics settings or preset saved yet, so the first start (or first install) of a fresh setup gets a preset; saved settings are kept.
  A failed detection is tried again at the next start.
- Each setting has one control: a switch a Faithful (0) / Meitou (1) drop-down, a slider with `labels` a drop-down of them, any other
  slider a slider with a typed value; unsaved, a row shows the game's start value (`shown`). A look setting that differs from the look's
  value has its name in brass and a Reset link that writes it, and so does its feature's header (for all of them, as in the game); quality and display settings are never marked (the Quality row reads Custom),
  as in the game's menu. Reset graphics, in a footer
  under the scrolling rows, removes `graphics`, `graphicsPreset`, `faithfulLook` and `lookPreset`. A top-level Default writes the schema's `default`.
  Whole values are written as integers.
- A slider without `labels` takes a typed value (Enter or leaving the box; Esc restores): the suffix may stay, a comma is a decimal
  point, the value is divided by `scale`, snapped to `step` and clamped; `offText` (Unlimited, Off) means the left end.
- A slider marked `reversed` (terrain detail's error in pixels) is drawn mirrored (`min + max - value`; logarithmic: `min x max / value`),
  so right is always the better end; the value written is unchanged. Texture quality, also reversed, is a drop-down, best first.
- Read-only when the file is unreadable, or its `version` differs from the schema's `settingsVersion` (another layout: the keys may
  mean something else). Warnings: the game is running (it writes the file on exit), `--low-end` (the game ignores `graphics`).

The Settings page's Language picks the game's and the launcher's language: English, Kenshi's nine languages, the install's `locale/` folders and the
languages Meitou has texts for, marked when Meitou's own texts are not translated. Choosing one writes only the `language` line of the Kenshi
folder's `settings.cfg` (encoding and line endings kept) and remembers the code in `launcher.json` (`Language`, used without a Kenshi folder); at
start the `settings.cfg` value wins. The launcher's labels come from the same `.po` catalogs as the game's Meitou texts
([localisation](formats/localisation.md)); a string without a translation stays English. Mod names, news and release notes are data, not translated.

`meitou.user.json` layout (`UserConfig`; **Verified** by loading crafted files through the built game 2026-10-20):
- `version` (no key: 1) is raised when a key is renamed, retyped or reinterpreted; `UserConfig.Migrate` converts older files step by
  step, moving a renamed key only where the new one is missing. A slider whose numbering changes takes a new label instead
  (`UpscalerSliders`). A newer file keeps its `version` and its unknown top-level keys (`[JsonExtensionData]`) when an older release saves.
- A value of the wrong type is dropped by its JSON path and takes its default; the rest stay. Unparsable JSON gives all defaults.
  Either way the file is first copied to `meitou.user.<yyyyMMdd-HHmmss>.bak.json`.

## Play reports

"Report to the roadmap" sends a play report to the reports service: [play-reports](play-reports.md).

## Mod list

`ModList` (Meitou.Core) holds the discovery and `mods.cfg` rules shared with `LoadOrder`
([overview](formats/overview.md#load-order)):

- Shown as the original launcher shows it: enabled mods in `mods.cfg` order first, then the rest after their header dependencies.
  Mods in a dependency cycle are kept at the end, where the original drops them. Mods not in `__mods.list` are new and start enabled.
- Saving (Save, Play) writes `mods.cfg` (enabled `<name>.mod`, list order, CRLF) and `__mods.list` (every listed name, plus old
  names not on disk). These are the files the original game reads, so the mod list applies to it too.
- Discovery and the header reads run off the UI thread while the window opens; until they finish (or when they fail) Save, Play
  and the preset buttons refuse, so an unloaded list never overwrites `mods.cfg`.
- A preset is a named, ordered list of names with enabled flags. Applying one reorders the list. Mods the preset doesn't name go
  to the end, disabled, and mods it names that aren't on disk are reported. Applying doesn't save.
- A mod folder with `RE_Kenshi.json` is marked as a plugin mod whose DLLs Meitou ignores ([RE_Kenshi](formats/rekenshi.md)).

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

A `.webm` in place of an image is a video (`VideoView`, the post's full width): its first MB is fetched with a Range request (`NewsFeed.HeadAsync`, cached as `.head`) for the poster, the whole file (cached like an image) only on the first Play, the button dimmed meanwhile; `WebmFile` reads the
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

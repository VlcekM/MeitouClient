<p align="center">
  <img src="assets/branding/header.png" alt="MeitouClient" width="100%">
</p>

[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/martinvlcek)

C#/.NET10 reimplementation of [Kenshi](https://lofigames.com/), aiming to run original content,
mods and saves unchanged. **No game assets are shipped; an installed legal copy is required.**
Early playable slice: streamed Vulkan world, characters/animation, movement/combat/body state,
NPC population/partial AI, inventory/trade, saves and native UI. 

Architecture/research: [docs index](docs/README.md)
Website: [meitou.kenshi.wiki](https://meitou.kenshi.wiki/).

## Download and requirements

The easiest way to get the game is the [Meitou Launcher](https://github.com/VlcekM/MeitouClient/releases/latest): it installs and
updates the game and manages mods. Download the newest one for

[Windows](https://github.com/VlcekM/MeitouClient/releases/latest/download/MeitouLauncher-win-x64-Setup.exe) (Setup.exe),

[Linux](https://github.com/VlcekM/MeitouClient/releases/latest/download/MeitouLauncher-linux-x64.AppImage) (AppImage),

[macOS Apple silicon](https://github.com/VlcekM/MeitouClient/releases/latest/download/MeitouLauncher-osx-arm64-Setup.pkg) or

[macOS Intel](https://github.com/VlcekM/MeitouClient/releases/latest/download/MeitouLauncher-osx-x64-Setup.pkg) (unsigned, Gatekeeper warns).

By hand: [releases](https://github.com/VlcekM/MeitouClient/releases) for Windows, Linux and macOS, unzip and run

the game or meitou-viewer (debug world viewer); prebuilt packages need no separate .NET install.
Needs a Vulkan1.3 GPU with required descriptor-indexing/indirect-draw features (Vulkan path) or an Apple-silicon Mac (Metal path), and Kenshi.
FSR runs on Windows and Linux (and on macOS from a self-built library); DLSS runs on Windows (Streamline) and on Linux with the NVIDIA driver (NGX through NVIDIA's client library, with the DLSS model beside the executable); optional ray-query GI falls back on unsupported GPUs. On Linux (checked on KDE, Wayland and X11, NVIDIA driver) a Steam install is found under ~/.local/share/Steam, ~/.steam/steam or the Flatpak folder; macOS runs through Homebrew MoltenVK or native Metal from the launcher on Apple silicon ([how to get Kenshi on macOS](docs/kenshi-on-macos.md)).
Building requires the [.NET10 SDK](https://dotnet.microsoft.com/download).

Steam installs, and GOG installs made by GOG Galaxy, Heroic, Lutris or Bottles, are located automatically; otherwise select the Kenshi folder when prompted,
set KENSHI_PATH, or create git-ignored meitou.local.json in the repo/working/executable directory (on Linux and macOS also `~/.config/meitou/config.json`, the place the prompt saves to when the executable's folder is read-only):

```json
{ "kenshiPath": "<your Kenshi install directory>" }
```

## Build and run

```text
dotnet build -c Release
dotnet test -c Release --filter "Category!=Slow&Category!=Bench"
dotnet run -c Release --project src/Meitou.Game -- --new-game
dotnet run -c Release --project tools/Meitou.ModelViewer -- --world --town "The Hub"
dotnet run -c Release --project tools/Meitou.Tools
```

For live UI iteration, run `dotnet watch --project src/Meitou.Game -c Debug -- --ui meitou --windowed`.
Saving supported C# edits updates the running game; Ctrl+R in the watch terminal restarts it.
Startup code, static initializers and already-baked UI textures need a restart to show changes.
DLSS also needs the vendor libraries (Windows: the Streamline DLLs; Linux: `libnvidia-ngx-dlss.so.*` from NVIDIA's
[DLSS SDK](https://github.com/NVIDIA/DLSS), `lib/Linux_x86_64/rel/`, and `libmeitou_ngx.so`, which `tools/scripts/build-ngx-shim.sh`
builds into `dlss-runtime/`; it needs curl and g++, on Fedora and RHEL also libstdc++-static) beside the Debug executable, or `MEITOU_DLSS_PATH` (formerly `MEITOU_STREAMLINE_PATH`, still read) pointing to their directory; see [third-party notices](THIRD_PARTY_NOTICES.md).
FSR on Linux needs `libamd_fidelityfx_vk.so` beside the executable or in `MEITOU_FFX_PATH`; `tools/scripts/build-ffx-linux.sh` builds it into the DLSS folder, which Debug builds copy from (its header lists the build tools).

Run the full suite before committing. Slow tests require assets/GPU/long runs; Bench tests assert
timing and need a quiet machine. Game/viewer share the rendering and data libraries. Use
--help / viewer --world --help for options. Tab opens settings, F10 lists current keys, F11/F12
show statistics/profiler. Game F5 saves/F9 loads; F2..F4 select1/2/5 speed, Space pauses,
F6 cinematic camera, F7 hides UI. [Viewer guide](docs/viewer.md), [simulation](docs/simulation.md),
[UI compatibility](docs/faithful-ui-parity.md).

Faithful follows original rendering/UI where implemented; independently selectable Meitou
features improve quality/ranges. FSR/DLSS vendor files are runtime/packaging dependencies,
never committed. [Third-party notices](THIRD_PARTY_NOTICES.md).
Both UI modes currently use the original in-game HUD and windows; Meitou retains its custom
title screen, transitions and settings. Known fidelity gaps: [UI compatibility](docs/faithful-ui-parity.md).

## Releases and license

GPL-3.0-or-later, with [license exception](LICENSE-EXCEPTION.md). Readers and game logic use
interoperability facts; proprietary SDKs/original content/decompiled code are not distributed.
Contribution rules: [AGENTS.md](AGENTS.md); current choices: [DECISIONS.md](DECISIONS.md).

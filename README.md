# MeitouClient

An open-source reimplementation of the [Kenshi](https://lofigames.com/) engine in C# / .NET 10, aiming to be a
playable drop-in replacement that runs the original content and mods unchanged, and runs it fast.

MeitouClient ships **no game assets**. You need a legal copy of Kenshi; MeitouClient reads everything from your
installation.

## Status

Early, and renderer-first. What works today:

- **World viewer**: the whole map streamed around a free-flying camera, on Vulkan 1.3: terrain, buildings and
  towns, foliage and grass, water with reflections, the game's sky, haze and weather effects, and cascaded
  shadows.
- **Faithful and Meitou modes**: each visual feature can look the way the game ships it (Faithful) or use
  MeitouClient's improvements (Meitou, the default): longer draw distances by object size, soft shadows that reach
  the horizon, SSAO, temporal anti-aliasing with DLSS or FSR upscaling.
- **Data**: `.mod` / `.base` files read and written byte-exactly, the load order merged like the game does, Ogre
  meshes, skeletons, materials and animations, DDS textures, the heightmap and zone files.
- **Characters**: races, heads and body shapes built from game data, with animations.

Not there yet: the game itself. AI, combat, the UI, saves and the rest of the simulation are still ahead; see
[ROADMAP.md](ROADMAP.md).

## Download

Pre-built Windows releases are on the [Releases](https://github.com/VlcekM/MeitouClient/releases) page: unzip and run
`meitou.exe` (the game) or `meitou-viewer.exe` (the world viewer). No .NET install is needed, and NVIDIA DLSS (Streamline) and AMD FSR are included. A Steam install of Kenshi is
found by itself; otherwise the game asks for the Kenshi folder once and keeps it in `meitou.local.json` next to the exe.
Releases are made by the manual `Release` workflow (`.github/workflows/release.yml`, which runs `tools/scripts/package-release.ps1`;
run that script with PowerShell 7 to build the same zip locally).

## Requirements

- Windows 10 or 11 (other platforms are untested).
- A GPU with Vulkan 1.3. Development and testing happen on NVIDIA; reports from AMD and Intel GPUs are welcome.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download).
- Kenshi, installed.

## Setup

A Steam install of Kenshi is found by itself (Steam's libraries). Otherwise point MeitouClient at your Kenshi install with an environment variable:

```
KENSHI_PATH=C:\Program Files (x86)\Steam\steamapps\common\Kenshi
```

or with a `meitou.local.json` (git-ignored) in the repository root, the working directory, or next to the executable:

```json
{ "kenshiPath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Kenshi" }
```

## Build and run

```
dotnet build -c Release
dotnet test -c Release
```

The tests that need the Kenshi install or a Vulkan device, or take long, are marked `[Slow]`. While iterating,
`dotnet test -c Release --filter "Category!=Slow"` runs the rest in seconds; run the whole suite before committing.

The world viewer, starting at The Hub:

```
dotnet run -c Release --project tools/Meitou.ModelViewer -- --world --town "The Hub"
```

Inside the viewer, `F10` lists the keys, `Tab` opens the settings sliders (time of day, draw distances, shadow
distance, upscaler), `F1`–`F6` switch features between Faithful and Meitou, and `F11` / `F12` show frame
statistics and a profiler. Everything is described in [docs/viewer.md](docs/viewer.md).

Other entry points:

```
dotnet run -c Release --project src/Meitou.Game                  # the game executable: boots into The Hub with the Kenshi camera
dotnet run -c Release --project tools/Meitou.ModelViewer -- --impostor-preview <FOLIAGE_MESH>   # bake one impostor and render preview pictures (docs/impostors.md)
dotnet run -c Release --project tools/Meitou.Tools                # data inspection commands
```

**Upscalers (optional).** DLSS and FSR need vendor libraries that are not part of this repository: NVIDIA
Streamline with the DLSS runtime (`MEITOU_STREAMLINE_PATH`) and AMD's FidelityFX Vulkan DLL (`MEITOU_FFX_PATH`).
Without them the viewer uses its own TAA. See [docs/engine.md](docs/engine.md) ("Upscaling").

## Layout

| Project | Purpose |
| --- | --- |
| `src/Meitou.Core` | Shared primitives, game install discovery |
| `src/Meitou.Data` | Kenshi data: `Fcs/` reads and writes `.mod` / `.base`; `GameDatabase` merges the load order; `Ogre/` reads meshes, skeletons and material scripts; `Textures/` decodes DDS and other images; `World/` reads the heightmap, zones, towns and foliage |
| `src/Meitou.Engine` | The simulation frame: fixed tick, game clock, input bindings, the Kenshi and free cameras |
| `src/Meitou.Rendering` | The world renderers (terrain, objects, foliage, water, sky, shadows, post-processing) and streaming; `Gpu/` is the native Vulkan rendering API they record through (device set-up and the shader compiler in `Gpu/Core` and `Gpu/Shaders`, the presenter), `Upscalers/` the FSR and DLSS integrations ([docs/renderer-native.md](docs/renderer-native.md)) |
| `src/Meitou.Rendering.Display` | The window and Vulkan device set up together, shared by the game and the viewer |
| `src/Meitou.Game` | `meitou`, the game executable |
| `tools/Meitou.ModelViewer` | `meitou-viewer`: the world viewer and the impostor preview |
| `tools/Meitou.Tools` | `meitou-tools`: inspect game data, render a top-down world map, compare screenshots and draw logs |
| `tests/Meitou.Tests` | Tests; the ones that need game files skip when no install is configured |

## Research notes

How Kenshi's files and systems work is written up in [docs/](docs/README.md), each claim marked **Verified**,
**Observed** or **Unknown**. These notes are the specification the code is written from.

**Clean-room approach.** Formats and behaviour are learned from the game's data, from observing the game, from the
open-source libraries it is built on (Ogre, MyGUI and others, used under their licenses), and from studying the
original binaries for interoperability. Only facts (layouts, constants, formulas, rules) go into the notes, in our own
words. Decompiled output never enters this repository and no code is copied or translated from it. The readers for
proprietary middleware formats (Havok, PhysX, Wwise) are written from observed file structure, without their SDKs.

## Contributing

Issues and pull requests are welcome. Please read [CLAUDE.md](CLAUDE.md) first: it holds the project's working
rules (no game files in the repository, findings go into `docs/` with their evidence, warnings are errors).
Contributions are accepted under the project's license.

## License

MeitouClient is free software under the [GNU General Public License v3.0 or later](LICENSE), with an
[additional permission](LICENSE-EXCEPTION.md) (GPLv3 section 7) to combine it with NVIDIA's proprietary DLSS runtime
libraries. Third-party notices: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Kenshi is a trademark of Lo-Fi Games. MeitouClient is an independent project, not affiliated with or endorsed by
Lo-Fi Games.

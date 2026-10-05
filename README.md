# MeitouClient

An open-source reimplementation of the [Kenshi](https://lofigames.com/) engine in C# / .NET 10,
aiming to be a fully playable drop-in replacement that runs original content and mods unchanged.

MeitouClient ships **no game assets**. You need a legal copy of Kenshi; MeitouClient reads its
data from your installation.

## Setup

Point MeitouClient at your Kenshi install, either with an environment variable:

```
KENSHI_PATH=C:\Program Files (x86)\Steam\steamapps\common\Kenshi
```

or a `meitou.local.json` (git-ignored) in the working directory or next to the executable:

```json
{ "kenshiPath": "C:\Program Files (x86)\Steam\steamapps\common\Kenshi" }
```

## Build

```
dotnet build
dotnet test
dotnet run --project tools/Meitou.Tools
dotnet run --project src/Meitou.Game            # the game: boots into The Hub with the Kenshi camera
```

## Layout

| Project | Purpose |
| --- | --- |
| `src/Meitou.Core` | Shared primitives, game install discovery |
| `src/Meitou.Data` | Kenshi data: `Fcs/` reads and writes `.mod` / `.base` byte-exactly; `GameDatabase` merges the load order; `Ogre/` reads `.mesh`, `.skeleton` and material scripts; `Textures/` decodes DDS and other images; `World/` reads the heightmap and zone files |
| `src/Meitou.Engine` | The simulation frame: fixed tick, game clock, input bindings, the Kenshi and free cameras ([docs/engine.md](docs/engine.md)) |
| `src/Meitou.Rendering` | The world renderers (terrain, objects, foliage, water, sky, shadows, post), and streaming (OpenGL 3.3) |
| `src/Meitou.Game` | `meitou`: the game executable |
| `tools/Meitou.ModelViewer` | `meitou-viewer`: renders a `.mesh` with textures and animations (OpenGL via Silk.NET); see [docs/viewer.md](docs/viewer.md) |
| `tools/Meitou.Tools` | `meitou-tools`: `formats`, `fcs <file>`, `load`, `meshes`, `skeletons`, `materials`, `fcs-types`, `fcs-records <type>`, `world`, `world-map <png>` to inspect game data; `image-diff` to compare screenshots |
| `tests/Meitou.Tests` | Tests; ones needing the real game skip when no install is configured |

See [ROADMAP.md](ROADMAP.md) for the plan and [docs/](docs/README.md) for research notes on the game formats.

## License

MeitouClient is free software under the [GNU General Public License v3.0 or later](LICENSE), with an
[additional permission](LICENSE-EXCEPTION.md) (GPLv3 section 7) to link with NVIDIA's proprietary DLSS runtime
libraries. Contributions are accepted under the same terms.

Third-party code notices: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Kenshi is a trademark of Lo-Fi Games. MeitouClient is not affiliated with or endorsed by Lo-Fi Games.

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
```

## Layout

| Project | Purpose |
| --- | --- |
| `src/Meitou.Core` | Shared primitives, game install discovery |
| `src/Meitou.Data` | Kenshi data formats: `Fcs/` reads and writes `.mod` / `.base` byte-exactly |
| `tools/Meitou.Tools` | `meitou-tools formats`, `meitou-tools fcs <file>`: inspect game data |
| `tests/Meitou.Tests` | Tests; ones needing the real game skip when no install is configured |

See [ROADMAP.md](ROADMAP.md) for the plan and [docs/](docs/README.md) for research notes on the game formats.

## License

MeitouClient is free software under the [GNU General Public License v3.0 or later](LICENSE).

Kenshi is a trademark of Lo-Fi Games. MeitouClient is not affiliated with or endorsed by Lo-Fi Games.

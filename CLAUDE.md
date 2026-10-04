# MeitouClient

C# / .NET 10 reimplementation of the Kenshi engine (namespace `Meitou`), GPL-3.0-or-later. Goal: a
drop-in replacement that runs original content and mods unchanged. Plan in [ROADMAP.md](ROADMAP.md).

## Research notes: read and keep them current

- Start with [docs/README.md](docs/README.md). Format knowledge lives in `docs/formats/`
  ([overview](docs/formats/overview.md), [FCS .mod/.base](docs/formats/fcs-mod.md)).
- Whenever you learn something about the original game's files or behaviour (a field's meaning, a
  layout, a merge rule, a disproved guess), write it into the matching doc in the same change.
  Create a new doc in `docs/formats/` for a new format and link it from `docs/README.md`.
- Mark each claim **Verified** (say against which files and how), **Observed**, or **Unknown**.
  Don't promote a guess to Verified without a test that checks it.

## Game assets

- Never commit original game files or content dumps. `.gitignore` blocks asset extensions.
- The install is found via `KENSHI_PATH` or a git-ignored `meitou.local.json`; never hardcode a path.
  Tests needing the game use `Assert.SkipWhen(GameInstall.Locate() is null, ...)`.
- Clean-room readers for Havok, PhysX and Wwise formats; don't link their SDKs or ship their DLLs.

## Build

`dotnet build`, `dotnet test`, `dotnet run --project tools/Meitou.Tools`. Warnings are errors.

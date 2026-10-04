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

## Reverse engineering

- We may decompile/disassemble the original binaries (for interoperability) to learn **facts**: formats,
  enum values, merge rules, formulas, constants. Write those facts into `docs/` in our own words,
  citing the source (e.g. "FCS `GameData.load`").
- Decompiled or disassembled output **never enters the repo**, and code is never copied or
  line-by-line translated from it. Keep it in the scratchpad / outside the working tree. Implement
  from the docs.
- Exception: identifiers that are part of a file format (enum names and their numbers, field names,
  magic values) are facts and may be transcribed, e.g. `FcsRecordType` from the editor's `itemType`.
  Logic may not.
- The mod editor is .NET: `ilspycmd -p -o <dir outside the repo> "<Kenshi>/forgotten construction set.exe"`
  (`dotnet tool install -g ilspycmd`). Its `GameData` class is the reference for the `.mod` format.

## Build

`dotnet build`, `dotnet test`, `dotnet run --project tools/Meitou.Tools`. Warnings are errors.

# MeitouClient

C# / .NET 11 reimplementation of the Kenshi engine (namespace `Meitou`), GPL-3.0-or-later. Goal: a
drop-in replacement that runs original content and mods unchanged.

## Research notes: read and keep them current

- Keep all `.md` files as small as possible: concise facts and instructions, no repetition or filler.
- Start with the compact [docs/README.md](docs/README.md) index, then read only the topic/sections needed.
  Format knowledge lives in `docs/formats/`
  ([overview](docs/formats/overview.md), [FCS .mod/.base](docs/formats/fcs-mod.md)).
- Whenever you learn something about the original game's files or behaviour (a field's meaning, a
  layout, a merge rule, a disproved guess), write it into the matching doc in the same change.
  Create a new doc in `docs/formats/` for a new format and link it from `docs/README.md`.
- Mark each claim **Verified** (say against which files and how), **Observed**, or **Unknown**.
  Don't promote a guess to Verified without a test that checks it.

- Document current contracts, facts, evidence and unresolved questions. Do not append development
  narration, progress diaries, completed plans, merge history or raw benchmark logs. Keep one
  authoritative description per fact and link to it; Git retains historical implementation details.

## Layout

- `src`: Core (shared with the launcher), Data (formats and pure rules), Simulation (the headless world: never references Game), Game
  (the executable `MeitouClient`). Tests, tools and the viewer reference Game. Metal code compiles where `MeitouMetal` is set (macOS).
- Folders are feature slices and the namespace follows the folder (`Simulation/Population` is `Meitou.Simulation.Population`); a
  project's kernel types sit at its root. `Game/Mods` keeps `Meitou.Modding` until the mod contract moves to Simulation.
- Never name a folder like a type: the namespace captures the name (`World`, `Orders`, `Inventory` are types).
- Don't add partial files to a class: a feature with state of its own is its own class. `partial` is for platform splits
  (`GameHost.Metal.cs`); the existing partial hosts are being broken up.

## Game assets

- Never commit original game files or content dumps. `.gitignore` blocks asset extensions.
- Paths that come from game data (records, scripts, meshes) go through `GameFiles.Resolve` (`Meitou.Content`) before they are tested or opened:
  Windows ignores letter case and Kenshi's data does not always agree with itself, so on Linux the exact path may not exist.
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

## Code style

- Keep methods short: one job each, roughly a screen at most. Split a longer method into named steps (its body reads as a list of
  calls), and split one you're editing when it has grown past that. Keep every comment that records a fact next to its code.
- Regions: a class with more than one responsibility gets one `#region` per responsibility, not nested. A region's fields and
  constants sit at its top, then its entry points, then their private steps; never fields between or after methods. A region whose
  state no other region touches becomes a class of its own.
- Doc comments: `/// <summary>`, the text, `/// </summary>` on separate lines, for every member including fields; the text
  wrapped at ~150. A summary sits directly on its member, never above a `//` comment or another member.
- Whitespace: a blank line between logical groups (setup, each decision, each group of side effects, the return); a comment that
  opens a group has a blank line before it. One statement per line.
- `if`: a short single-statement body stays on the `if` line (`if (x is null) return;`, the line ~120 at most); a longer one
  goes on the next line; a body of more statements, or spanning lines, goes in braces on their own lines. `try`/`catch`/`finally`
  bodies always in braces on their own lines. Guard clauses first, then the main path unindented.
- Lines: wrap past ~150, preferably by naming a local; a repeated long expression becomes a helper. A lambda longer than a line
  becomes a named method (method group) or a statement lambda laid out like a method. Chained `?:` and LINQ calls one per line.
- Merged code (a PR, another branch, a contributor's patch, a port) follows these rules too: bring it up to them while merging,
  without changing its behaviour.

## Code mods

- Contract: [docs/modding.md](docs/modding.md) and [docs/modding-dependencies.md](docs/modding-dependencies.md). A change to
  `CodeModManifest`, `ModHost` or `ModContext` updates them; bump `ModApi.Version` when an older game would misread the manifest.

## Build

- Get the user's explicit approval before adding tests, including new cases in existing tests.
  Explain briefly what they would catch and why existing coverage is insufficient; wait for approval.

`dotnet build`, `dotnet test`, `dotnet run --project tools/Meitou.Tools`. Warnings are errors. Naming follows `.editorconfig` and the build enforces it (IDE1006): private fields `_camelCase`, private static `s_camelCase`, static readonly and non-private fields PascalCase, locals, parameters and local constants camelCase; Metal-only code is checked by the `-p:MeitouMetal=true` build. Tests that need the game, a GPU or
long runs are marked `[Slow]`; tests that assert a timing are marked `[Bench]` (they fail on a loaded machine). The quick run while
iterating is `dotnet test --filter "Category!=Slow&Category!=Bench"`; the full run before committing.

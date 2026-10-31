# Contributing to MeitouClient

Thanks for wanting to help. Meitou started as a hobby project and I'd like
to keep it feeling like one, so there are a few rules on *what* gets
contributed and *how*. Code conventions, testing and commit rules are in
[AGENTS.md](AGENTS.md); current design choices in [DECISIONS.md](DECISIONS.md).

This is to keep the project fun for me, to make sure we reach the finish line and beyond 😉

Code is cheap today; sharing the vision, short/medium/long-term goals, and reviewing is unfortunately a bottleneck nowadays.

## Who decides what

- Final say on what lands in `master` is VlcekM's.
- **Core team:** VlcekM (`_sashkaa`), pupumer (Bazouz660) and DemitriX.
  The core team owns the core gameplay systems:
  simulation, AI, combat, economy, dialogue and the overall vision.

## What's welcome right now

The project is in a deliberately slower phase. I'm taking on **small
contributions** and not accepting **large features or new systems** for
the foreseeable future.

Always welcome, no need to ask first:

- Bug fixes, parity fixes against the original game, crash reports with repros
- Tests, tooling, docs, translations
- Platform specific bugs or features - stuff like making Vulkan-only features available on the Metal renderer

Ask first (open an issue or ping `_sashkaa` on Discord), and expect
a "not now" to be possible:

- Anything new in core gameplay or simulation
- Optional extras and mode toggles (postfx, bloom-style effects, UI skins).
  These are welcome if they're self-contained: own files, off by default,
  no changes to core systems.

Not accepted at the moment:

- Large features or systems. I am really sorry, but I don't have the bandwidth to
  review and fit them into the overall design right now 🙁. Keep them on
  your fork and we can revisit later.
- Larger refactors - Meitou is a huge project and Kenshi is one hell of a beast, keeping track of everything is already a daunting task.
- General performance work. Making things run fast is the most fun part of the puzzle for me, and having someone else do the fun parts would kill the motivation for me unfortunately.
  But we do accept contributions for stuff like Linux or macOS/Metal specific performance.

## Before you start

1. Check the [roadmap](https://meitou.kenshi.wiki/roadmap), open issues
   and PRs so you don't build what someone else is already building.
2. For anything beyond a small fix, open an issue describing the change
   and wait for a go-ahead before writing code. PRs for unapproved
   features will be closed with a pointer to this file, I am sorry, but again, code is cheap, mental bandwidth is in short supply 😓

## Sending a pull request

- Keep PRs small and focused; one change per PR.
- Build, run the full test suite and follow [AGENTS.md](AGENTS.md).
- Explain *why* in the description, not only what.
- Reviews happen in batches, usually at weekends. A slow reply is not a no.

## Credit

Everyone who lands code is in the contributors graphic in the README and
on the credits page. If I merge or adapt work from your fork or branch,
your commits are kept or you're added as co-author. You will always be attributed for your work, I promise 😊

## Licence

MeitouClient is GPL-3.0-or-later with a [licence exception](LICENSE-EXCEPTION.md).
By contributing you agree your work is licensed the same way. No original
game assets, proprietary SDKs or decompiled code may be contributed.

# Meitou benchmarks

Data and tools behind the [benchmarks page](https://meitou.kenshi.wiki/benchmarks) (`site/src/pages/benchmarks.astro`).

- `kit/`: the dev kit (Python 3.10+; Linux, Windows, macOS). Reproduces the fixed benchmark on your machine; see `kit/README.md`. Linux native is what the maintainer measures; Windows and macOS are supported but untested.
- `data/machines/<id>.json`: one machine (GPU, CPU, RAM, OS, resolution, GitHub handle).
- `data/results/<id>/`: its result CSVs. Contributors add their own files by pull request; see `CONTRIBUTING.md`.
- `targets.json`: benchmark targets, oldest first. The latest is the current standard; everyone benchmarks it.
- `series.json`: scenarios, procedure series and change-summary buckets.
- `tools/generate.py`: validates the data and builds the page's `bench.json`.

## The standard

Release targets (kind `release`, label = the upstream tag) are anchors every machine should run once. A commit that differs from a target only in `docs/`, `site/`, `bench/`, `tests/`, `.github/`, `tools/` or `*.md` is code-identical and counts for it. Any other commit on `master` is accepted but flagged off-target (faded on the page, left out of comparisons between machines).

## How the numbers are made

- Native Linux build of upstream `master` (contributors may add `proton`, `windows` or `macos` machines; a machine keeps one platform).
- Clean, unmodded game data (GOG copy), the frozen save `bench-bases-2026-10-27`, full graphics settings.
- Frame metrics come from the game's own bench frames (MangoHud is an optional extra log on Linux).
- Two scenarios: the play benchmark (upstream's `--play-bench`: idle, orbit, zoom, pan) and a base switch (four camera jumps between the two bases). Metrics: share of frames over 33 ms, p50 / p99 / max frame time, frames over 100 ms, slow-world tick p99 / max; optional F12 profiler stages and peak video memory (Vulkan builds whose `frames.csv` has `vram_mb`). Lower is better, except avg fps.
- Each benchmark runs twice back-to-back. Run 1 may be cache-cold; runs are never averaged.
- Series (`series.json`) mark changes of procedure; the charts draw a line where one starts.

## Data layout

```
bench/targets.json
bench/data/machines/<machine-id>.json
bench/data/results/<machine-id>/<date>-<commit>.csv            the kit's results.csv as written
bench/data/results/<machine-id>/<date>-<commit>.report.md      optional
bench/data/results/rtx3070-laptop-linux/daily-bench.csv        the maintainer's file (machine from the folder)
```

Commits are ordered by git history (`git rev-list --topo-order origin/master`), not by date; a commit not on `master` is rejected. "Changes between benchmarks" come from the commit subjects between consecutive benchmarked commits, grouped by the text before the first colon; they are not reviewed.

## Building

```
python3 bench/tools/generate.py --check                             # validate only (what the PR check runs)
python3 bench/tools/generate.py --out site/public/bench/bench.json  # what the site build runs
```

`--repo` (default: this repository) and `--ref` (default `origin/master`) choose the history that defines order. Invalid input prints every problem and exits non-zero. The site build (`.github/workflows/site-image.yml`) generates `bench.json` before building the image; it is not committed.

## Maintainer

After a benchmark run, copy the maintainer CSV to `data/results/rtx3070-laptop-linux/daily-bench.csv`.

`targets.json` is kept by CI (`.github/workflows/bench-target.yml`, `generate.py --add-target <tag>`): every release (`vX.Y.Z`) becomes a release target, and the day's first nightly (`v*-nightly.YYYYMMDD`) a daily target, unless that day has one or it is code-identical to the latest target. A release of the latest daily target's commit turns that target into the release.

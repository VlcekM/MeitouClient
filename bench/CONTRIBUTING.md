# Contributing benchmark results

Results from other machines are welcome. They are added by pull request to this repository and appear on the [benchmarks page](https://meitou.kenshi.wiki/benchmarks) next to the maintainer's, with your GitHub handle and your hardware.

## The standard: everyone benchmarks the same commits

`bench/targets.json` lists the benchmark targets, oldest first. **The latest entry is the current standard**: run that commit. Comparing machines only makes sense on the same code.

- **Run the latest target.** The dev kit builds it by default: `build.py` reads `targets.json` (from `bench/targets.json` on upstream `master`, or the copy next to the kit when offline) and builds that commit.
- **Release targets** (`kind: release`, for example `v0.16.0`) are anchor points: every machine should run each release at least once, so any two machines have a commit in common.
- **Daily targets** are added by the maintainer after each upstream update. You do not need to chase every one; the latest is enough.
- A commit that differs from a target only in `docs/`, `site/`, `bench/`, `tests/`, `.github/`, `tools/` or `*.md` is **code-identical** and counts for that target.
- Any other commit on upstream `master` is accepted but flagged **off-target**: it is drawn faded on the page and left out of comparisons between machines. Setting `REF=...` when building makes your result off-target (the kit prints a warning).

## 1. Run the kit

Follow `bench/kit/README.md` (Linux, Windows and macOS; Python 3.10+, nothing else to install): get the benchmark save, build the current standard, run `bench.py`.

```
cd bench/kit
export KENSHI_PATH=... MACHINE=<machine-id> WHO=<your GitHub handle>   # Windows PowerShell: $env:KENSHI_PATH="..."
python3 build.py
python3 bench.py
```

Pick the machine id once and keep it: lowercase letters, digits and hyphens, 3 to 48 characters, for example `ryzen5600-rx6800-linux`. It is your folder name and the `MACHINE` value. A machine's rows must all be one platform (`native` = Linux native, `proton` = Windows build under Proton on Linux, `windows`, `macos`): use a different id for a Proton setup than for a native one on the same computer, e.g. `<id>-proton`, and for a second operating system on the same hardware, e.g. `<id>-windows`. Windows and macOS runs are new and untested; if something does not work, say so in the PR or an issue.

The result is `kit/results/<date>-<commit>-<machine>/results.csv` (and `report.md`).

## 2. Submit by pull request

Fork [VlcekM/MeitouClient](https://github.com/VlcekM/MeitouClient) and add, in your fork, only files under `bench/data/`:

1. **First time only:** `bench/data/machines/<machine-id>.json`

   ```json
   {
     "id": "ryzen5600-rx6800-linux",
     "github": "your-github-handle",
     "cpu": "AMD Ryzen 5 5600",
     "gpu": "AMD Radeon RX 6800",
     "ram_gb": 32,
     "os": "Arch Linux",
     "resolution": "1920x1080",
     "notes": "Optional: anything useful, e.g. laptop on mains power",
     "added": "2026-10-28"
   }
   ```

   Optional `"os_family": "linux" | "windows" | "macos"`: the page derives it from the platform of your results; if you give it, it must agree. `os` is free text (distribution, Windows edition, macOS version).

   `id` must equal the file name without `.json`. `resolution` is the size the benchmark rendered at (the `resolution` column of your `results.csv`). Optional `"screen"` (e.g. `"2560x1440"`) is shown instead when the window is smaller than the screen; the render size stays in the tooltip.

2. `bench/data/results/<machine-id>/<date>-<commit>.csv`: your `results.csv`, unchanged. `<date>` is the day of the run (`YYYY-MM-DD`), `<commit>` the 8-digit commit from the CSV. The `machine` column, if present, must equal your machine id.

3. Optional: `bench/data/results/<machine-id>/<date>-<commit>.report.md`: your `report.md` (rename it like this so several runs can sit in one folder).

Open the pull request against `master`.

## What the automatic check verifies

On every pull request that touches `bench/data/`, the check (`bench/tools/generate.py --check`) fails if:

- a machine file is missing, misnamed or lacks a required field, or a results folder has no machine file;
- a CSV is missing columns (the 19 columns of the kit; the 6 machine columns are optional) or a value is not a number;
- `platform` is not `native`, `proton`, `windows` or `macos`;
- `segments` is not one of the two known scenarios, `save` is not `bench-bases-2026-10-27`, or `data` is not `gog` (clean, unmodded data);
- the `machine` column does not match your folder;
- the commit does not exist upstream or is not on upstream `master`;
- a row is duplicated, there are more than two runs of a scenario for one commit and machine, or one machine mixes platforms (native, proton, windows, macos).

Off-target commits do not fail the check; they are only flagged. Files changed outside `bench/data/machines/` and `bench/data/results/` are flagged for the reviewer too: keep a results PR to those two folders.

## Rules

- Your GitHub handle is shown on the site, linked to your profile, with your hardware and resolution. Do not submit if you do not want that.
- Do not edit other people's files. Corrections to your own data are fine (a new commit in your PR).
- Do not submit modded runs or runs with changed settings. The kit refuses a changed `meitou.user.json` and a save that differs from the checksums; if something was odd (a different window size, background load, a crash), say so in the `note` column or the report instead of changing the numbers.
- Only the `bench-bases-2026-10-27` save is accepted. Numbers from different save files or settings cannot be compared.
- Run 1 may be cache-cold; that is normal and both runs are kept.

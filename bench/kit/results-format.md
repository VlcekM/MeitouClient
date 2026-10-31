# results.csv format

`bench.py` writes one row per successful run to `results.csv` in its output folder: 2 runs of the play benchmark and 2 of the base-switch scenario give 4 rows. The first 19 columns are exactly those of the maintainers' `daily-bench.csv`, in the same order and with the same meaning, so rows can be appended to it; the machine columns come last.

| column | meaning |
|---|---|
| date, time | start of the run (local time) |
| build | build folder name, e.g. `Meitou-master-linux` |
| commit | 8-digit source commit of the build (from `BUILT_FROM.txt`) |
| platform | `native` (the Linux build on Linux), `proton` (the Windows build under Proton on Linux), `windows` (Windows build on Windows) or `macos` (macOS build on a Mac, Vulkan through MoltenVK). One machine keeps one platform |
| data | `gog` = clean unmodded Kenshi data (our name for it, whichever store you bought it in) |
| save | save folder name, `bench-bases-2026-10-27` |
| segments | `idle,orbit,zoom,pan` (play benchmark) or `bases:idle,return` (base switch) |
| run | 1 or 2; run 1 may be cache-cold (disk cache, shader cache) |
| frames_over_33ms_pct | share of frames slower than 33 ms, in % (all segments except `settle`) |
| p50_ms, p99_ms, max_ms | frame time percentiles and maximum, ms (percentile = sorted value at index round(p/100 * (n-1))) |
| frames_over_100ms | number of frames slower than 100 ms |
| slow_world_p99_ms, slow_world_max_ms | p99 and maximum of the slow-world simulation tick (column `sim_slow-world` of the game's frames.csv), ms |
| gpu | GPU name as the game's own `bench/summary.md` reports it |
| nvidia_driver | GPU driver version (name kept for merging; holds Mesa/AMD/Intel versions too) |
| note | free text; the script starts it with `series 2: 2 runs per scenario (run 1 may be cache-cold); dev kit` and appends `--note` |
| machine | short label of the machine (`MACHINE`) |
| cpu | CPU model |
| ram_gb | installed RAM, GB |
| os | distribution / OS name (`PRETTY_NAME`, Windows caption, macOS name and version) |
| kernel | kernel release (Linux, macOS: Darwin), Windows version number |
| resolution | resolution the benchmark rendered at (the game window), from `bench/summary.md`; depends on your screen size and affects every number |

After `resolution` come 94 optional F12-stage columns (the same names and order as the maintainers' `daily-bench.csv`): `cpu_<stage>_p50_ms` / `_p99_ms` for each stage (`-` written `_`, e.g. `cpu_upd_terrain_p50_ms`), `cpu_total_*`, `gpu_<stage>_*`, `gpu_other_*`, `gpu_total_*`, in ms with 3 decimals, computed from the same frames as above. Blank when the game's `frames.csv` has no stage columns.

The `settle` segment is always run first and left out of the numbers. Each folder under `play/` and `bases/` also keeps the game's per-frame `bench/frames.csv`, its `summary.md`, the MangoHud log and `game.log`.

To merge several machines into one file: keep one header and append the data rows (`tail -n +2`). Rows from `daily-bench.csv` have only 19 fields; pad them with six empty fields or leave them as they are when reading with a tool that tolerates short rows.

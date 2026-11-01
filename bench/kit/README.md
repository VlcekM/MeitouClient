# Meitou benchmark dev kit

Reproduce the Meitou daily benchmark on your own machine (Linux, Windows or macOS) and report in a common format, so numbers from different machines and commits can be compared.

## What it measures, and why it is comparable

Meitou is a C#/.NET 10 Vulkan reimplementation of Kenshi (upstream: github.com/VlcekM/MeitouClient). The maintainers run one fixed benchmark after every update of upstream master. This kit runs the same one:

- **Play benchmark** (the game's own `--play-bench`): after a `settle` step that is not counted, the segments `idle`, `orbit`, `zoom`, `pan` (about 44 s). Reports frame-time statistics and the slow-world simulation tick.
- **Base switch** (`bases` scenario): the segments `idle` and `return` (10 s each); 1 s and 6 s into each, the script double-taps `2` then `1`, which centres the camera on the character at base 2, then at base 1. Four switches; it measures streaming and drawing of the other base.

Everything that could differ is fixed: the frozen save `bench-bases-2026-10-27`, clean Kenshi data (no mods), the settings file in this kit, windowed mode with full settings (never `--low-end`), two runs of each scenario back-to-back. The numbers come from the game's own `bench/frames.csv`; nothing else (no MangoHud, no GPU tool) is needed for them. What stays machine dependent is the hardware, the operating system and the window size: the game opens windowed at the size of your desktop, and the resolution is recorded with every row. Compare like with like (same resolution, same platform).

Total game time is about 8 minutes (plus about 35 s of loading per run).

## Tested on

| platform (`platform` column) | status |
|---|---|
| Linux native (`native`) | Ran end to end once (2026-10-29, CachyOS, RTX 3070 Laptop, commit `0d95057f`): save download and check, `build.py`, `bench.py`, both scenarios twice; play numbers in line with the maintainer's daily rows. The metric and breakdown columns are also unit tested against the maintainer's saved runs. |
| Linux with Proton (`proton`) | Ran once (same day and machine): the play benchmark works; the base switch sent no keys then, because the Windows build's console output does not reach `game.log` under Proton. **Fixed:** the kit now also reads `log.txt` next to the executable, where the game always writes its output; a base-switch run on 2026-10-29 sent 4 of 4 keys. That run's numbers were much worse than other runs (first run in a fresh Proton prefix, machine under memory pressure), so no Proton base-switch numbers are established yet. |
| Windows (`windows`) | **Untested.** Command construction and the hardware probe are unit tested with simulated output only; building, starting the game and sending keys have never run on Windows. Contributors welcome: please report what works and what does not (an issue, or a note in your results). |
| macOS (`macos`) | **Untested**, and Meitou's macOS support is itself young (see "macOS" below). Contributors welcome. |

## Prerequisites (all platforms)

- **Python 3.10 or newer** (standard library only; nothing to `pip install`). The commands below say `python3`; on Windows it is usually `py -3` or `python`.
- **git** and the **.NET 10 SDK** (`dotnet --list-sdks` shows a `10.x`), to build the current benchmark target.
- A Kenshi install with **clean data: no mods** installed or active (a fresh GOG or Steam copy). Its folder holds `data/` and `save/`; this is `KENSHI_PATH` below. Kenshi's data are not included in Meitou; you need your own copy.
- About 6 GB free RAM and 1 GB disk (a copy of the save is made).
- The benchmark save (step 3).

### Linux

- A graphical session, the Vulkan loader and a Vulkan 1.3 driver (Meitou's own requirement).
- The base-switch keys need **ydotool** with its daemon `ydotoold` and write access to `/dev/uinput` (works on Wayland and X11), or **xdotool** (X11 / XWayland, fallback). Without either, only the play scenario runs and the report says so.
- Optional: MangoHud (`--mangohud`) for an extra frame-time log; `nvidia-smi`, `vulkaninfo`, `xrandr` for the hardware record (missing ones give "unknown"; override with env vars `CPU RAM_GB GPU_NAME GPU_DRIVER OS_NAME DISPLAY_RES`).
- Kenshi data: a Steam or GOG install of Kenshi (Windows version, e.g. through Proton or Heroic/Lutris), or a copy of its folder.

### Windows

- A Vulkan 1.3 GPU with a current driver.
- Keys are sent with `SendInput` (no extra software). The script brings the game window to the front by process id before each tap; do not touch the keyboard or mouse during the base-switch runs. If Windows refuses the focus change, the keys go to whatever window has focus (`keys sent: n of 4` in `run.txt` only counts keys the script sent, not whether the game received them; check that the camera jumped).
- The hardware record uses PowerShell (`Get-CimInstance`), which every Windows has.
- Kenshi data: your Kenshi install (Steam or GOG); the folder with `data\gamedata.base`.
- Run the commands in a normal terminal (cmd or PowerShell); set variables with `set KENSHI_PATH=C:\Games\Kenshi` (cmd) or `$env:KENSHI_PATH="C:\Games\Kenshi"` (PowerShell).

### macOS

- **Meitou's macOS state, from its docs (`docs/macos-port.md`, `docs/launcher.md`, `docs/metal-backend.md`):** it runs on Apple silicon through MoltenVK (Vulkan on Metal); there is also a native Metal renderer, but `--play-bench` is **Vulkan-only**, so the benchmark always uses the Vulkan path (the kit passes `--renderer vulkan`). You install the Vulkan loader and MoltenVK yourself: `brew install molten-vk vulkan-loader`. The kit finds `libvulkan.1.dylib` under Homebrew (`/opt/homebrew/lib`, `/usr/local/lib`), `$VULKAN_SDK/lib` or `MAC_VULKAN_DIR`, puts that folder on `DYLD_LIBRARY_PATH` for the game and sets `MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS=1` (both as upstream documents). Upstream's release README calls the macOS build experimental and untested; only Apple silicon is reported as verified (an M1 Pro).
- **Kenshi has no macOS version**, and Steam on macOS cannot install it. Meitou only needs the data folder (it must contain `data/gamedata.base`). Upstream's `docs/kenshi-on-macos.md` lists the ways: copy the `Kenshi` folder of a Windows install, or install it in a CrossOver/Whisky bottle, or the GOG offline installer in a Wine prefix. For this benchmark the data must be clean (no mods). Whether data obtained in each of these ways gives identical benchmark numbers has not been checked (**Unknown**).
- Keys are sent with CoreGraphics (`CGEventPost`). macOS only allows this if the app that runs the script (Terminal, iTerm, VS Code, ...) is switched on in **System Settings > Privacy & Security > Accessibility**. The script checks it and says so; without it only the play scenario runs. The game is brought to the front with `osascript` (also needs that permission).
- The build is ad-hoc signed by `build.py` (as upstream's release script does) because Apple silicon refuses unsigned code, and the quarantine flag is removed. Needs the Xcode command line tools (`xcode-select --install`) for `codesign`.
- Not known (**Unknown**): how the window size/Retina scaling shows in the `resolution` column, whether the first launch triggers Gatekeeper prompts for the unsigned .NET runtime files, and the benchmark's behaviour on Intel Macs.

## Steps

1. **Get the kit.** Copy this `kit` folder (or clone the repository) and enter it. (Linux/macOS: nothing to `chmod`; run the scripts with `python3`.)

```bash
cd kit
```

2. **Tell the scripts where your Kenshi is, and name your machine.** Pick the machine id once and keep it (see CONTRIBUTING.md).

```bash
export KENSHI_PATH="$HOME/path/to/Kenshi"      # the folder with data/ and save/
export MACHINE="mylaptop-rtx3060-linux"        # a short label for your report; also your machine id
export WHO="your name or handle"
```

   Windows PowerShell: `$env:KENSHI_PATH="C:\Games\Kenshi"; $env:MACHINE="desktop-rx6800-windows"; $env:WHO="handle"`.

3. **Get the benchmark save.** Download `bench-bases-2026-10-27.zip` from the release [bench-save-2026-10-27](https://github.com/Demitrix2k/meitou-bench/releases/download/bench-save-2026-10-27/bench-bases-2026-10-27.zip) (zip SHA-256 `c8f192aaab5364e2e2689a518bb49add746b8abbdc67e31878b8403b0c7da84b`) and unzip it into `<Kenshi>/save/` (so that `<Kenshi>/save/bench-bases-2026-10-27/` exists). The script verifies the folder against `save-checksums.sha256` (every file's SHA-256 and the file count) before use, and refuses on a mismatch. You can check by hand:

```bash
python3 verify_save.py "$KENSHI_PATH/save/bench-bases-2026-10-27"
```

   The checksum of `save-checksums.sha256` itself, which goes into your report: `ceb2fb186fcfce7d3841a947914eac89f37547b028fbf05fb760aa8613dac4ff`. The script never writes into the save: it copies it into the output folder and the game loads the copy.

4. **Build the current benchmark target** for your machine. Everyone benchmarks the same commits: `build.py` reads `targets.json` (the latest entry is the current standard; from the public repository, or `../targets.json` when offline) and builds that commit. `REF=origin/master` or any other commit still works but prints a warning: the result is then off-target (accepted, but flagged and left out of cross-machine comparisons). `bench.py` says in its output and report whether the commit is a target. The first run clones `https://github.com/VlcekM/MeitouClient.git` into `./MeitouClient`; later runs fetch. Your own working tree is never touched.

```bash
python3 build.py
```

   It runs upstream's own publish (`dotnet publish src/Meitou.Game/Meitou.Game.csproj -c Release -r <rid> --self-contained true -p:DebugType=embedded`) for the runtime of your machine (`linux-x64`, `win-x64`, `osx-arm64` or `osx-x64`; folder `builds/Meitou-master-linux`, `-win`, `-osx-arm64`, `-osx-x64`), signs it on macOS, and installs the kit's `meitou.user.json`. Other targets: `python3 build.py windows` (the win-x64 build, for `--platform proton` on Linux), `both`, or an explicit runtime. Options (env): `MEITOU_SRC` (an existing clone), `DOTNET_ROOT` (SDK not on PATH), `MEMORY_MAX=6G` (Linux), `REF=<commit>`, `FORCE=1`.

5. **The settings file.** `build.py` installs `meitou.user.json` from this kit into the build. If you built some other way, copy it yourself into the build folder. It holds the maintainers' benchmark settings (TAA, texture quality 1, frame limit 90, characters' flights off, ...). `bench.py` checks that the build's file still has all of its values (the game adds keys of its own on start) and refuses otherwise; do not edit it for a comparable run.

6. **Run the benchmark.** Check first without starting the game (optional), then run. Do not use the mouse or keyboard in other windows while it runs; the game window must keep focus.

```bash
python3 bench.py --dry
python3 bench.py
```

   Options: `--runs N` (default 2), `--scenarios play` (no key sending needed), `--platform auto|native|proton|windows|macos`, `--build DIR`, `--out DIR`, `--note "text"`, `--machine`, `--who`, `--mangohud` (Linux, extra log). Env: `MEMORY_MAX=10G` (Linux: systemd user scope with a memory cap, if available), `KEYS_BACKEND`, `GAME_ARGS`, `TIMEOUT`, and more; `python3 bench.py --help` lists all of them. If the machine is a laptop, plug it in and do not change power profiles between runs.

7. **Send back.** The output folder is `results/<date>-<commit>-<machine>/`. Fill the "Summary" and "Anything odd" parts of `report.md` and add the Kenshi store and version, then submit `results.csv` (and `report.md`) as described in "Submit your results" below. Besides the frame times, each row records the game's peak video memory (`vram_peak_mb`; builds from commit `21c41667` on, blank on macOS). If something looks wrong, also send the `run.txt` and `game.log` of that run. Formats: `REPORT-TEMPLATE.md`, `results-format.md`.

## How each OS starts the game and sends keys

| | game | base-switch keys | memory cap | hardware record |
|---|---|---|---|---|
| Linux native | `./MeitouClient --windowed --load <copy> --play-bench <dir>` with `WAYLAND_DISPLAY` unset (XWayland, as the maintainers do; `KEEP_WAYLAND=1` keeps it); optional `mangohud` in front | `ydotool key` (evdev codes), else `xdotool key` | `systemd-run --user --scope` if `MEMORY_MAX` is set | `/proc`, `nvidia-smi` / `vulkaninfo` / `glxinfo`, `/etc/os-release`, `xrandr` |
| Linux Proton | `python3 $PROTON_BIN run ./MeitouClient.exe ...` in the Windows build folder, paths as `Z:...`, own compat prefix | as Linux native | as Linux native | as Linux native |
| Windows | `MeitouClient.exe --windowed --load <copy> --play-bench <dir>` from the build folder | `SendInput` with scan codes, window brought to the front by PID | none | PowerShell `Get-CimInstance` (`Win32_Processor`, `Win32_VideoController`, `Win32_OperatingSystem`) |
| macOS | `./MeitouClient --windowed ... --renderer vulkan` with `DYLD_LIBRARY_PATH` (Vulkan loader) and `MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS=1` | `CGEventPost` (needs Accessibility permission), window to the front with `osascript` | none | `sysctl`, `sw_vers`, `system_profiler SPDisplaysDataType` |

On every OS the script reads the game's output to learn when the `idle` and `return` segments start, so the key presses land at 1 s and 6 s into them. It reads two files: `game.log` (the captured console, empty or partial for the Windows build under Proton) and `log.txt` next to the game executable (the game always writes its output there, appended across runs; the kit reads only what was added since the start of the run, and keeps that part as `log-txt-this-run.txt` in the run folder). Either one showing the `bench     idle (` line is enough, so Windows and macOS do not depend on console capture. If neither shows it, no keys are sent and `run.txt` says `keys sent: 0 of 4` with a warning; the play scenario is not affected. Needs a build with `log.txt` (upstream master of 2026-10); an old build without it works only where `game.log` gets the output.

## Submit your results

Results are published on the benchmark page by pull request to VlcekM/MeitouClient. In short: add `bench/data/machines/<machine-id>.json` (once) and copy your `results.csv` to `bench/data/results/<machine-id>/<date>-<commit>.csv`, with `report.md` beside it as `<date>-<commit>.report.md` if you like. Your GitHub handle is shown on the page with your hardware. The exact steps, naming rules and what the automatic check verifies are in [`../CONTRIBUTING.md`](../CONTRIBUTING.md). Use the same value for `MACHINE` (step 2) as your machine id. A machine id keeps one platform: a Linux machine that also runs the Windows build under Proton uses a separate id such as `<id>-proton`.

## Proton on Linux

`--platform proton` runs the Windows build with Proton, as the maintainers do: build it with `python3 build.py windows`, set `PROTON_BIN` to the `proton` script (e.g. Proton Experimental) and optionally `STEAM_COMPAT_DATA_PATH` (default `./proton-prefix`; a fresh prefix is created). Untested by anyone but the maintainers' machine (with the old bash script): a fresh prefix, a different Proton version, and whether `ydotool` keys reach the Proton window on other desktops. The Windows build normally also carries vendor DLLs (DLSS Streamline, FSR) that are not in the repository; the benchmark settings do not use them, but if the Windows build asks for them, copy them from a Meitou release into the build folder.

## Window size

The maintainers' window is maximised on a 2560x1440 screen (renders at 2560x1406). Other screens give other resolutions; the `resolution` column records it. The game parses a `--size WxH` argument (`GAME_ARGS="--size WxH"`), but it is not known whether it applies to the interactive window; untried.

## Files

| file | purpose |
|---|---|
| `build.py` | fetch upstream, publish the build for your OS (Linux, Windows, macOS), install the settings |
| `bench.py` | run the daily benchmark, write tables, `results.csv`, `report.md` |
| `kitlib.py`, `kitkeys.py`, `bench_table.py` | helpers: targets, save check, hardware probes, process checks; key sending per OS; tables, metrics and CSV rows |
| `verify_save.py` | check a save folder against the manifest |
| `meitou.user.json` | the settings file every benchmark build uses |
| `save-checksums.sha256` | SHA-256 of every file of the benchmark save |
| `REPORT-TEMPLATE.md`, `results-format.md` | the report and the CSV format |

The earlier bash scripts (`bench.sh`, `update-and-build.sh`, `verify-save.sh`, `lib.sh`) were removed: there is one implementation, the Python one. The command builders and parsers are pure functions, written to be testable for all three OSes from any OS.

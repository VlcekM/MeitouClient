#!/usr/bin/env python3
"""bench.py: the Meitou daily benchmark, for any machine (Linux, Windows, macOS). Python 3.10+, standard library only.
Runs N times the upstream play benchmark (--play-bench: idle, orbit, zoom, pan) and N times the base-switch scenario, back-to-back, on a frozen save
with clean (unmodded) Kenshi data, then prints the tables and writes results.csv and report.md. It is the same measurement as the maintainers'
`bench-compare.sh --daily`; see README.md and results-format.md in this folder. The metrics come from the game's own bench/frames.csv.

Usage:  KENSHI_PATH=<Kenshi install> python3 bench.py [options]      (Windows cmd: set KENSHI_PATH=...; PowerShell: $env:KENSHI_PATH="...")
  --platform auto|native|proton|windows|macos
                             auto (default): the host: native on Linux (the Linux build), windows, macos. proton: the Windows build run with Proton on Linux
                             (build it with `build.py windows`). The platform is written to the `platform` column; one machine id must keep one platform.
  --runs <n>                 runs of each scenario (default 2: run 1 may be cache-cold, so the first is not thrown away but both are reported)
  --scenarios play,bases     which scenarios to run (default both). "bases" sends keys to the focused game window (see README, per OS); where keys cannot
                             be sent, only "play" is run and the report says so.
  --build <dir>              the build folder (default <kit>/builds/Meitou-master-<linux|win|osx-arm64|osx-x64>; made by build.py)
  --out <dir>                output folder (default <kit>/results/<date>-<commit>-<machine>); must not hold runs already: nothing is overwritten
  --note <text>              extra text for the note column of results.csv
  --machine <id> --who <name>   the same as the env MACHINE and WHO
  --mangohud                 Linux only: also log with MangoHud (an extra log; the metrics never need it)
  --dry                      check everything and print the commands, start nothing (same as DRY=1)
Env (required first):
  KENSHI_PATH      the Kenshi install folder: it holds data/ (clean, no mods) and, normally, save/. The game is started with this as its data.
  SAVE_DIR         the benchmark save folder (default $KENSHI_PATH/save/bench-bases-2026-10-27). Verified against save-checksums.sha256 and then COPIED
                   into the output folder; the copy is what the game loads. The original is only read.
Env (optional):
  MACHINE, WHO     short label of the machine and your name or handle for the report/CSV (default: empty, fill in report.md)
  MEMORY_MAX       e.g. 10G: Linux only, run each game in a systemd user scope with this memory cap (if `systemd-run --user` works; otherwise no cap)
  MH_PCI_DEV, MH_BASE_CONF, MH_SECONDS   MangoHud (with --mangohud): GPU as a PCI address or "none"; a MangoHud config to start from; log_duration (default 60)
  TIMEOUT          longest wait per run, seconds (default 420)         MIN_RAM_MB   refuse to start below this much available RAM (default 6144)
  KEYS_BACKEND     Linux: ydotool or xdotool (default: ydotool, else xdotool); none = do not send keys (only the play scenario runs)
  YDOTOOL_SOCKET   socket of ydotoold (default: found in /tmp or $XDG_RUNTIME_DIR; ydotoold is started if it is not running)
  KEEP_WAYLAND=1   Linux: keep WAYLAND_DISPLAY set for the game (default: unset, as the maintainers do: the game opens through XWayland)
  MAC_VULKAN_DIR   macOS: the folder with libvulkan.1.dylib (default: found under Homebrew or $VULKAN_SDK)
  GAME_ARGS        extra arguments for the game, appended after the built-in ones (changes the numbers, say so in the note)
  ALLOW_SAVE_MISMATCH=1 / ALLOW_SETTINGS_DIFF=1   go on although the save or the settings differ from the kit's; the report says so
  CPU RAM_GB GPU_NAME GPU_DRIVER OS_NAME DISPLAY_RES   override the hardware probes
  Proton only: PROTON_BIN (the `proton` script, required), STEAM_COMPAT_DATA_PATH (default <kit>/proton-prefix), STEAM_COMPAT_CLIENT_INSTALL_PATH (default ~/.local/share/Steam)
What it does for every run: refuses if a game is running or RAM is short; starts the game windowed, full settings (no --low-end), waits for the benchmark to
  end by itself (the game quits), and for the base-switch scenario double-taps 2 then 1 at 1 s and 6 s into each of the segments idle and return (four
  switches). The game must keep focus: do not click elsewhere during a run.
Exit codes: 0 all runs fine, 1 a run failed, 2 refused or bad usage.
"""
import contextlib
import io
import os
import re
import shlex
import shutil
import signal
import subprocess
import sys
import threading
import time
from datetime import datetime

import bench_table as T
import kitkeys
import kitlib as K

PROG = "bench.py"
SCENARIO_SEGMENTS = {"play": ("idle,orbit,zoom,pan", "idle,orbit,zoom,pan", ""), "bases": ("idle,return", "bases:idle,return", "-bases")}


# ---------------------------------------------------------------- pure builders (tested for every OS by tests/test_kit.py)
def mh_pci_dev(env=None, listing=None):
    """GPU for MangoHud as a PCI address: MH_PCI_DEV, else the NVIDIA GPU when the machine has several display controllers (Linux)."""
    env = os.environ if env is None else env
    if env.get("MH_PCI_DEV"):
        return "" if env["MH_PCI_DEV"] == "none" else env["MH_PCI_DEV"]
    n, nv = 0, ""
    for name, cls, vendor in (listing if listing is not None else _pci_listing()):
        if cls >> 16 == 3:
            n += 1
            if not nv and vendor == "0x10de":
                nv = name
    return nv if n > 1 and nv else ""


def _pci_listing():
    out = []
    base = "/sys/bus/pci/devices"
    try:
        for d in sorted(os.listdir(base)):
            try:
                cls = int(open(os.path.join(base, d, "class")).read().strip(), 16)
                vendor = open(os.path.join(base, d, "vendor")).read().strip()
                out.append((d, cls, vendor))
            except (OSError, ValueError):
                pass
    except OSError:
        pass
    return out


def mangohud_config(log_dir, seconds, pci_dev="", base_conf_text=""):
    """A minimal MangoHud config: log only into log_dir. Never no_display (it turns logging off); log_duration shorter than the run."""
    lines = []
    if pci_dev:
        lines.append(f"pci_dev={pci_dev}")
    for l in base_conf_text.splitlines():
        if not re.match(r"^\s*(gpu_list|no_display|output_folder|log_duration|autostart_log|log_interval)\s*=", l):
            lines.append(l)
    lines += [f"output_folder={log_dir}", "autostart_log=1", "log_interval=100", f"log_duration={seconds}"]
    return "\n".join(lines) + "\n"


def find_mac_vulkan_dir(env, exists=os.path.exists):
    """The folder with libvulkan.1.dylib (docs/macos-port.md: DYLD_LIBRARY_PATH must hold it). MAC_VULKAN_DIR, DYLD_LIBRARY_PATH, $VULKAN_SDK/lib, Homebrew."""
    cands = []
    if env.get("MAC_VULKAN_DIR"):
        cands.append(env["MAC_VULKAN_DIR"])
    cands += [d for d in env.get("DYLD_LIBRARY_PATH", "").split(":") if d]
    if env.get("VULKAN_SDK"):
        cands.append(os.path.join(env["VULKAN_SDK"], "lib"))
    cands += ["/opt/homebrew/lib", "/usr/local/lib"]
    return next((d for d in cands if exists(os.path.join(d, "libvulkan.1.dylib"))), "")


def game_launch(cfg, run_dir, mdir):
    """What starting one run needs: dict(argv, env (variables to set), unset (variables to remove), cwd). Pure: nothing is executed or touched.
    cfg is a dict: osname, plat, kenshi, save_copy, build_dir, only (MEITOU_BENCH_ONLY), game_args, mangohud (bool), mh_seconds, mh_conf (path),
    scope (argv prefix), env (os.environ-like), proton_bin, compat_data, compat_client, keep_wayland, mac_vulkan_dir."""
    osname, plat = cfg["osname"], cfg["plat"]
    z = "Z:" if plat == "proton" else ""
    env = {"MEITOU_BENCH_ONLY": cfg["only"], "KENSHI_PATH": z + cfg["kenshi"]}
    unset = []
    if plat == "proton":
        runner = [cfg.get("python", sys.executable), cfg["proton_bin"], "run", "./MeitouClient.exe"]
        env["STEAM_COMPAT_DATA_PATH"] = cfg["compat_data"]
        env["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = cfg["compat_client"]
        if cfg["mangohud"]:
            env["MANGOHUD"] = "1"
    elif osname == "windows":
        runner = [os.path.join(cfg["build_dir"], "MeitouClient.exe")]
    else:
        runner = ["./MeitouClient"]
        if cfg["mangohud"]:
            runner = ["mangohud"] + runner
    if cfg["mangohud"]:
        env["MANGOHUD_CONFIGFILE"] = cfg["mh_conf"]
    if osname == "linux" and not cfg.get("keep_wayland"):
        unset.append("WAYLAND_DISPLAY")
    args = ["--windowed", "--load", z + cfg["save_copy"], "--play-bench", z + os.path.join(run_dir, "bench")]
    if osname == "macos":
        args += ["--renderer", "vulkan"]   # the play benchmark is Vulkan-only (docs/metal-backend.md); MoltenVK is the Vulkan driver there
        vk = cfg["mac_vulkan_dir"]
        old = [d for d in cfg["env"].get("DYLD_LIBRARY_PATH", "").split(":") if d]
        env["DYLD_LIBRARY_PATH"] = ":".join(old if vk in old or not vk else old + [vk])
        env["MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS"] = cfg["env"].get("MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS", "1")
    extra = shlex.split(cfg.get("game_args", ""), posix=(osname != "windows"))
    args += [a[1:-1] if osname == "windows" and len(a) > 1 and a[0] == a[-1] == '"' else a for a in extra]   # Windows: keep backslashes, drop the quotes
    return dict(argv=list(cfg.get("scope", [])) + runner + args, env=env, unset=unset, cwd=cfg["build_dir"])


def key_plan(scen):
    """For the base-switch scenario: [(segment, seconds after the segment starts, key)] = double-tap 2 at 1 s, then 1 at 6 s, in idle and in return."""
    return [(seg, t, key) for seg in ("idle", "return") for t, key in ((1, "2"), (6, "1"))] if scen == "bases" else []


def build_dir_default(plat, osname, kit=K.KIT, mac_arm=None):
    rid = K.platform_rid(plat, osname, mac_arm=mac_arm)
    return os.path.join(kit, "builds", f"Meitou-master-{K.RID_SUFFIX[rid]}")


def build_type_text(plat, rid):
    return {"native": "native Linux", "proton": "Windows build under Proton (Linux)", "windows": "native Windows",
            "macos": f"native macOS ({rid}), Vulkan through MoltenVK"}[plat]


# ---------------------------------------------------------------- running
def stop_process(proc, osname):
    if proc.poll() is not None:
        return
    try:
        if osname == "windows":
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True)
        else:
            os.killpg(proc.pid, signal.SIGTERM)
            try:
                proc.wait(15)
            except subprocess.TimeoutExpired:
                os.killpg(proc.pid, signal.SIGKILL)
    except (OSError, ProcessLookupError):
        pass
    try:
        proc.wait(20)
    except subprocess.TimeoutExpired:
        proc.kill()


def spawn(launch, log_path, osname):
    env = dict(os.environ)
    for k in launch["unset"]:
        env.pop(k, None)
    env.update(launch["env"])
    kw = {}
    if osname == "windows":
        kw["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        kw["start_new_session"] = True
    log = open(log_path, "wb")
    try:
        return subprocess.Popen(launch["argv"], cwd=launch["cwd"], env=env, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, **kw)
    finally:
        log.close()


def log_offset(path):
    """Size of a log file now (0 if it does not exist): where this run's own lines start in a file that is appended to across runs."""
    try:
        return os.path.getsize(path)
    except OSError:
        return 0


def read_run_output(sources):
    """The game's output of this run: the text of every (path, start offset) in sources joined. A source that is missing counts as empty; one that is
    now shorter than its offset (the game cuts the oldest lines of a big log.txt) is read from its start. Never raises."""
    parts = []
    for path, start in sources:
        try:
            with open(path, "rb") as f:
                f.seek(0, os.SEEK_END)
                f.seek(start if start <= f.tell() else 0)
                parts.append(f.read().decode("utf-8", errors="replace"))
        except OSError:
            pass
    return "\n".join(parts)


def segment_started(text, seg):
    """True when the play benchmark's line announcing segment `seg` ("bench     idle (10 s)") is in text. The game writes it to its console (game.log
    on Linux) and, always, to log.txt next to the executable; under Proton only log.txt gets it. Lines that tee into log.txt may be prefixed by nothing, but a
    leading timestamp or CR is tolerated."""
    return re.search(r"^[\r ]*bench\s+" + re.escape(seg) + r" \(", text, re.M) is not None


def key_driver(proc, sources, scen, keys, steps_path, osname, stop):
    """Follows the benchmark's segment lines in the game's output (see read_run_output); at the right moments sends the double taps. Runs in a thread."""
    for seg in ("idle", "return"):
        while True:
            if stop.is_set() or proc.poll() is not None:
                return
            if segment_started(read_run_output(sources), seg):
                break
            time.sleep(0.2)
        t0 = time.time()
        for s, at, key in key_plan(scen):
            if s != seg:
                continue
            while time.time() - t0 < at:
                if stop.is_set() or proc.poll() is not None:
                    return
                time.sleep(0.05)
            keys.focus(proc.pid)
            if keys.double_tap(key):
                with open(steps_path, "a", encoding="utf-8") as f:
                    f.write(f"{datetime.now():%H:%M:%S} {seg}: double-tap {key}\n")


def main(argv):
    K.check_python()
    osname = K.host_os()
    env = os.environ
    plat_arg, runs, scen_arg = "auto", 2, "play,bases"
    build_dir, out, note = env.get("BUILD_DIR", ""), env.get("OUT", ""), env.get("NOTE", "")
    dry = bool(env.get("DRY")); machine, who = env.get("MACHINE", ""), env.get("WHO", "")
    use_mh = False
    it = iter(argv)
    for a in it:
        nxt = lambda: next(it, None) or (_ for _ in ()).throw(K.KitError(f"{a} needs a value"))
        if a == "--platform": plat_arg = nxt()
        elif a == "--runs":
            v = nxt()
            if not re.fullmatch(r"[1-9][0-9]*", v): raise K.KitError("--runs must be a positive number")
            runs = int(v)
        elif a == "--scenarios": scen_arg = nxt()
        elif a == "--build": build_dir = nxt()
        elif a == "--out": out = nxt()
        elif a == "--note": note = nxt()
        elif a == "--machine": machine = nxt()
        elif a == "--who": who = nxt()
        elif a == "--mangohud": use_mh = True
        elif a == "--dry": dry = True
        elif a in ("-h", "--help"):
            print(__doc__); return 0
        else:
            raise K.KitError(f"unknown argument '{a}' (see bench.py --help)")
    plat = K.default_platform(osname) if plat_arg == "auto" else plat_arg
    K.check_platform(plat, osname)
    timeout, mh_seconds, min_ram = env.get("TIMEOUT", "420"), env.get("MH_SECONDS", "60"), env.get("MIN_RAM_MB", "6144")
    for n, v in (("TIMEOUT", timeout), ("MH_SECONDS", mh_seconds), ("MIN_RAM_MB", min_ram)):
        if not v.isdigit(): raise K.KitError(f"{n} must be a number")
    timeout, mh_seconds, min_ram = int(timeout), int(mh_seconds), int(min_ram)
    if use_mh and mh_seconds >= timeout: raise K.KitError(f"MH_SECONDS ({mh_seconds}) must be a number below TIMEOUT")
    scenarios = [s for s in scen_arg.split(",") if s]
    if not scenarios or any(s not in SCENARIO_SEGMENTS for s in scenarios): raise K.KitError("--scenarios takes play and/or bases")
    if use_mh and osname != "linux": raise K.KitError("--mangohud is Linux only (MangoHud); the metrics never need it")
    if use_mh and not shutil.which("mangohud"): raise K.KitError("'mangohud' not found on PATH. Install MangoHud, or run without --mangohud.")

    # ---- inputs
    if not env.get("KENSHI_PATH"): raise K.KitError("set KENSHI_PATH to your Kenshi install folder (the one with data/ and save/)")
    kenshi = os.path.realpath(env["KENSHI_PATH"])
    if not os.path.isdir(os.path.join(kenshi, "data")): raise K.KitError(f"no data/ folder in KENSHI_PATH={kenshi}")
    save_dir = os.path.realpath(env.get("SAVE_DIR") or os.path.join(kenshi, "save", K.SAVE_NAME))
    if not os.path.isdir(save_dir):
        raise K.KitError(f"benchmark save not found: {save_dir} (unzip bench-bases-2026-10-27.zip into {os.path.join(kenshi, 'save')} or set SAVE_DIR; see README step 3)")
    save_name = os.path.basename(save_dir)
    rid = K.platform_rid(plat, osname)
    exe = K.exe_name(rid)
    build_dir = os.path.realpath(build_dir or build_dir_default(plat, osname))
    if not os.path.isfile(os.path.join(build_dir, exe)):
        raise K.KitError(f"no {exe} in {build_dir} (build it: python3 build.py{' windows' if plat == 'proton' else ''}; or pass --build)")
    built = os.path.join(build_dir, "BUILT_FROM.txt")
    m = re.findall(r"[0-9a-f]{8}", open(built, encoding="utf-8").read()) if os.path.isfile(built) else []
    if not m: raise K.KitError(f"cannot read a commit from {built} (build with build.py)")
    commit = m[-1]
    warn = []
    tq = K.target_query(commit)
    if tq:
        target_note = f"commit {commit} is a benchmark target ({tq[1]})"
    else:
        lt = K.target_query("latest")
        target_note = (f"commit {commit} is NOT a benchmark target" + (f" (the current standard is {lt[0]})" if lt else "") +
                       ": accepted when submitted, but flagged off-target and left out of cross-machine comparisons (unless it is code-identical to a target)")
        warn.append(target_note)

    proton_bin = env.get("PROTON_BIN", "")
    if plat == "proton" and not (proton_bin and os.path.isfile(proton_bin)):
        raise K.KitError("set PROTON_BIN to the 'proton' script (e.g. .../steamapps/common/Proton - Experimental/proton)")
    mac_vk = ""
    if plat == "macos":
        mac_vk = find_mac_vulkan_dir(env)
        if not mac_vk:
            raise K.KitError("libvulkan.1.dylib not found (the Vulkan loader and MoltenVK are not bundled): brew install molten-vk vulkan-loader, or set MAC_VULKAN_DIR")

    # ---- keys (base-switch scenario)
    keys = None
    if "bases" in scenarios:
        keys, why = kitkeys.choose(osname, env.get("KEYS_BACKEND"))
        if keys is None:
            if scenarios == ["bases"]:
                raise K.KitError(f"the base-switch scenario needs key sending, which is not available: {why}")
            print(f"{PROG}: keys cannot be sent ({why}): running only the play scenario; the base-switch scenario is skipped", file=sys.stderr)
            warn.append(f"base-switch scenario skipped, keys unavailable: {why}")
            scenarios = [s for s in scenarios if s != "bases"]

    # ---- settings: the build's meitou.user.json must hold every value of the kit's (the game adds keys of its own when it starts)
    kit_settings, build_settings = os.path.join(K.KIT, "meitou.user.json"), os.path.join(build_dir, "meitou.user.json")
    if not os.path.isfile(build_settings): raise K.KitError(f"{build_settings} is missing: copy {kit_settings} there (build.py does)")
    settings_state = "unchanged (all values of the kit's meitou.user.json)"
    sd = T.diff_settings(kit_settings, build_settings)
    if sd:
        if not env.get("ALLOW_SETTINGS_DIFF"):
            raise K.KitError("the build's meitou.user.json differs from the kit's:\n" + "\n".join(sd) +
                             "\ncopy the kit's file over it, or ALLOW_SETTINGS_DIFF=1 to go on (the report will list the differences)")
        settings_state = "CHANGED: " + ";".join(sd)
        warn.append("settings differ from the kit's: " + "; ".join(sd))

    # ---- save: verify the original, never write into it
    sums = os.path.join(K.KIT, "save-checksums.sha256")
    save_state = "verified against save-checksums.sha256"
    if not os.path.isfile(sums):
        save_state = "NOT verified (no save-checksums.sha256 in the kit)"; warn.append(save_state)
    else:
        ok, problems = K.verify_save(save_dir, sums)
        if not ok:
            if not env.get("ALLOW_SAVE_MISMATCH"):
                raise K.KitError(f"{save_dir} does not match save-checksums.sha256 (other files or other contents; first differences: {'; '.join(problems[:3])}). "
                                 "Get a fresh bench-bases-2026-10-27.zip, or ALLOW_SAVE_MISMATCH=1 to go on (numbers are then not comparable).")
            save_state = "MISMATCH with save-checksums.sha256 (ALLOW_SAVE_MISMATCH)"; warn.append(save_state)
    save_sum = K.sha256_file(sums) if os.path.isfile(sums) else "none"

    # ---- machine, output folder
    hw = K.hw_info(osname)
    if not machine:
        machine = "unnamed"; warn.append("MACHINE not set: the machine column says 'unnamed'")
    out = os.path.abspath(out or os.path.join(K.KIT, "results", f"{datetime.now():%Y-%m-%d}-{commit}-{machine}"))
    if os.path.isdir(out):
        for _r, _d, files in os.walk(out):
            if "frames.csv" in files or "results.csv" in files:
                raise K.KitError(f"{out} already holds results; use --out <new folder>")

    # ---- refusals: one game at a time, enough RAM
    busy = K.game_process_lines(osname)
    if busy: raise K.KitError("a game is already running:\n" + "\n".join(busy) + "\nend it first")
    avail = hw["avail_mb"]
    if avail is None:
        warn.append("available RAM could not be read: the RAM check was skipped")
    elif avail < min_ram:
        raise K.KitError(f"only {avail} MB RAM available (need {min_ram}; MIN_RAM_MB to change)")

    est = runs * len(scenarios) * 110 // 60
    print(f"{PROG}: {target_note}")
    print(f"{PROG}: {plat} ({osname}), build {os.path.basename(build_dir)} ({commit}), save {save_name} ({save_state}), {runs} run(s) of: {','.join(scenarios)}; "
          f"about {est} min of game time; output {out}")
    if keys: print(f"{PROG}: base-switch keys through {keys.name}")
    save_copy = os.path.join(out, "save-copy", save_name)
    scope = K.memory_scope(osname, env.get("MEMORY_MAX")) if osname == "linux" else []
    if env.get("MEMORY_MAX") and osname != "linux":
        print(f"{PROG}: MEMORY_MAX is only used on Linux: running without a memory cap", file=sys.stderr)

    if not dry:
        os.makedirs(out, exist_ok=True)
        with open(os.path.join(out, "machine.txt"), "w", encoding="utf-8") as f:
            f.write("\n".join([f"machine: {machine}", f"who: {who}", f"cpu: {hw['cpu']}", f"ram_gb: {hw['ram_gb']}", f"gpu: {hw['gpu']}", f"gpu_driver: {hw['gpu_driver']}",
                               f"os: {hw['os']}", f"kernel: {hw['kernel']}", f"display: {hw['display']}", f"commit: {commit}", f"build: {build_dir}",
                               f"platform: {plat}", f"os_family: {osname}", f"rid: {rid}", f"kenshi_path: {kenshi}"]) + "\n")
        print(f"copying the save to {save_copy} ...")
        os.makedirs(os.path.dirname(save_copy), exist_ok=True)
        if os.path.isdir(save_copy):   # a rerun into the same output folder (same day, commit and machine): start from a fresh copy
            K.rmtree_force(save_copy)
        K.copytree_rw(save_dir, save_copy)
        if keys:
            try:
                keys.start()
            except RuntimeError as e:
                raise K.KitError(str(e))

    base_conf_text = ""
    if env.get("MH_BASE_CONF") and os.path.isfile(env["MH_BASE_CONF"]):
        base_conf_text = open(env["MH_BASE_CONF"], encoding="utf-8", errors="replace").read()
    pci = mh_pci_dev(env) if use_mh else ""
    fails = 0
    proc = None
    try:
        for scen in scenarios:
            segs, seg_label, mid = SCENARIO_SEGMENTS[scen]
            prefix = f"{plat}-gog{mid}"
            job = os.path.join(out, scen)
            if not dry:
                os.makedirs(job, exist_ok=True)
                open(os.path.join(job, "builds.txt"), "w", encoding="utf-8").write(f"master\t{os.path.basename(build_dir)}\t{commit}\n")
                open(os.path.join(job, "settings.txt"), "w", encoding="utf-8").write(f"platform={plat}\ndata=gog\nsave={save_name}\nsegments={seg_label}\n")
            for k in range(1, runs + 1):
                run_name = f"{prefix}-{k}-master"
                run_dir, mdir = os.path.join(job, run_name), os.path.join(job, run_name, "meitou")
                if os.path.exists(run_dir): raise K.KitError(f"{run_dir} exists; results are never overwritten")
                cfg = dict(osname=osname, plat=plat, kenshi=kenshi, save_copy=save_copy, build_dir=build_dir, only="settle," + segs, game_args=env.get("GAME_ARGS", ""),
                           mangohud=use_mh, mh_conf=os.path.join(mdir, "mangohud.conf"), scope=scope, env=env, proton_bin=proton_bin,
                           compat_data=env.get("STEAM_COMPAT_DATA_PATH") or os.path.join(K.KIT, "proton-prefix"),
                           compat_client=env.get("STEAM_COMPAT_CLIENT_INSTALL_PATH") or os.path.expanduser("~/.local/share/Steam"),
                           keep_wayland=bool(env.get("KEEP_WAYLAND")), mac_vulkan_dir=mac_vk)
                if plat == "proton" and not dry:
                    os.makedirs(cfg["compat_data"], exist_ok=True)   # proton refuses a compat data folder that does not exist yet
                launch = game_launch(cfg, run_dir, mdir)
                shown = " ".join(shlex.quote(a) for a in launch["argv"])
                envs = " ".join(f"{k2}={v}" for k2, v in launch["env"].items())
                if dry:
                    print(f"--- {scen} run {k}: {run_name}\ncd {build_dir}\nenv {envs}" + (f"; unset {' '.join(launch['unset'])}" if launch["unset"] else "") + f"\n  {shown}")
                    if scen == "bases": print("  keys: " + ", ".join(f"+{t}s into {s}: double-tap {key}" for s, t, key in key_plan(scen)) + f" (via {keys.name})")
                    if use_mh: print(f"  MangoHud config: pci_dev={pci or '(default)'}, output_folder={mdir}, log_duration={mh_seconds}")
                    continue
                os.makedirs(mdir, exist_ok=True)
                if use_mh:
                    open(launch["env"]["MANGOHUD_CONFIGFILE"], "w", encoding="utf-8").write(mangohud_config(mdir, mh_seconds, pci, base_conf_text))
                with open(os.path.join(mdir, "launch.txt"), "w", encoding="utf-8") as f:
                    f.write(f"started {datetime.now().astimezone().isoformat(timespec='seconds')} data=gog platform={plat}\ncd {build_dir}\nenv {envs}\n{shown}\n")
                print(f"== {scen} run {k}: {run_name}")
                log_path, t0 = os.path.join(mdir, "game.log"), time.time()
                # The Windows build always writes log.txt next to the exe (appended across runs); under Proton its console output never reaches game.log.
                game_txt = os.path.join(build_dir, "log.txt")
                sources = [(log_path, 0), (game_txt, log_offset(game_txt))]
                proc = spawn(launch, log_path, osname)
                stop, kthread = threading.Event(), None
                if scen == "bases":
                    kthread = threading.Thread(target=key_driver, args=(proc, sources, scen, keys, os.path.join(mdir, "steps.log"), osname, stop), daemon=True)
                    kthread.start()
                timed_out = False
                try:
                    rc = proc.wait(timeout)
                except subprocess.TimeoutExpired:
                    timed_out = True
                    stop_process(proc, osname)
                    rc = 124
                stop.set()
                if kthread: kthread.join(5)
                proc = None
                secs = int(time.time() - t0)
                if os.path.isfile(game_txt):   # this run's part of log.txt, kept with the run (the only copy of the game's output under Proton)
                    open(os.path.join(mdir, "log-txt-this-run.txt"), "w", encoding="utf-8").write(read_run_output([sources[1]]))
                lines = [f"exit={rc} seconds={secs}{' timedout' if timed_out else ''}"]
                if use_mh:
                    g = next((re.sub(r"^.*\] ", "", l) for l in open(log_path, encoding="utf-8", errors="replace") if re.search(r"MANGOHUD.*active GPU", l)), "")
                    lines.append(f"MangoHud: {g}" if g else "WARNING MangoHud GPU line not found in game.log")
                    if not [f for f in os.listdir(mdir) if f.endswith(".csv")]: lines.append("WARNING no MangoHud CSV written")
                if scen == "bases":
                    sl = os.path.join(mdir, "steps.log")
                    n = sum(1 for l in open(sl, encoding="utf-8")) if os.path.isfile(sl) else 0
                    lines.append(f"keys sent: {n} of 4")
                    if n == 0 and not segment_started(read_run_output(sources), "idle"):
                        lines.append("WARNING no 'bench     idle (' line in game.log or in log.txt next to the executable: the game's output was not seen")
                if not os.path.isfile(os.path.join(run_dir, "bench", "frames.csv")):
                    lines.append("FAILED no bench/frames.csv"); fails += 1
                open(os.path.join(run_dir, "run.txt"), "w", encoding="utf-8").write("\n".join(lines) + "\n")
                print("\n".join("   " + l for l in lines))
                time.sleep(3)
    except KeyboardInterrupt:
        if proc: stop_process(proc, osname)
        if keys: keys.stop()
        return 130
    except BaseException:
        if proc: stop_process(proc, osname)
        raise
    if keys: keys.stop()
    if dry:
        print(f"(DRY: nothing was started; about {est} min of game time)")
        return 0

    # ---- results
    if os.path.isfile(sums) and not K.verify_save(save_copy, sums)[0]:
        warn.append("the save copy changed while the game ran (autosave?): later runs started from a changed save")
    note_text = f"series 2: {runs} runs per scenario (run 1 may be cache-cold); dev kit" + (f"; {note}" if note else "")
    csvp = os.path.join(out, "results.csv")
    for s in scenarios:
        T.history(csvp, os.path.join(out, s), note_text, hw["gpu_driver"], machine, hw["cpu"], hw["ram_gb"], hw["os"], hw["kernel"])
    def tbl(s):
        if not os.path.isdir(os.path.join(out, s)): return f"(scenario {s} not run)"
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(io.StringIO()):
            T.main([os.path.join(out, s)])
        return buf.getvalue().rstrip("\n")
    r_play, r_bases = tbl("play"), tbl("bases")
    rw = []
    for root, _d, files in os.walk(out):
        if "run.txt" in files and os.path.basename(root) != "":
            for l in open(os.path.join(root, "run.txt"), encoding="utf-8"):
                if re.search(r"WARNING|FAILED|exit=[1-9]", l): rw.append(f"{os.path.relpath(os.path.join(root, 'run.txt'), out)}: {l.strip()}")
    if rw: warn.append("run warnings:\n" + "\n".join(sorted(rw)))
    print("\n## play benchmark\n" + r_play + "\n\n## base switch\n" + r_bases)

    fill = dict(WHO=who, DATE=f"{datetime.now():%Y-%m-%d}", MACHINE=machine, CPU=hw["cpu"], RAM=f"{hw['ram_gb']} GB", GPU=hw["gpu"], OS=hw["os"], KERNEL=hw["kernel"],
                DRIVER=hw["gpu_driver"], DISPLAY=f"{hw['display']} (desktop; the benchmark's render size is in the resolution column of results.csv)", COMMIT=commit,
                BUILD_TYPE=build_type_text(plat, rid), SETTINGS=settings_state, DATA=f"clean Kenshi data, no mods (path: {kenshi}); store and game version: fill in",
                SAVE=save_name, SAVE_SUM=f"{save_sum} (sha256 of save-checksums.sha256); {save_state}", RESULTS_PLAY=r_play, RESULTS_BASES=r_bases, RUNS=str(runs),
                NOTE=note, WARNINGS="\n".join("- " + w for w in warn) if warn else "- none from the script")
    tpl = open(os.path.join(K.KIT, "REPORT-TEMPLATE.md"), encoding="utf-8").read()
    with open(os.path.join(out, "report.md"), "w", encoding="utf-8") as f:
        f.write(re.sub(r"\{\{([A-Z_]+)\}\}", lambda mm: fill.get(mm.group(1), mm.group(0)), tpl))
    print(f"\nwritten: {os.path.join(out, 'results.csv')} (send this) and {os.path.join(out, 'report.md')} (fill in the summary, then send it too)")
    if fails:
        print(f"{fails} run(s) failed (see run.txt in their folders)", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except K.KitError as e:
        K.die(PROG, str(e))

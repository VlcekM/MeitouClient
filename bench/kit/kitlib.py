"""kitlib.py: helpers shared by build.py, bench.py and verify_save.py (Python 3.10+, standard library only).

Everything that differs between Windows, macOS and Linux is a function that takes the OS family ("linux", "windows", "macos") as an argument,
so it can be tested for any OS from any OS (see tests/test_kit.py): the functions build commands and parse text; only run_capture() and the
callers of subprocess execute anything.
"""
import hashlib
import json
import os
import platform as _platform
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request

KIT = os.path.dirname(os.path.abspath(__file__))
TARGETS_URL = os.environ.get("TARGETS_URL", "https://raw.githubusercontent.com/VlcekM/MeitouClient/master/bench/targets.json")
SAVE_NAME = "bench-bases-2026-10-27"
PLATFORMS = ("native", "proton", "windows", "macos")   # the values of the `platform` column; native = Linux native


class KitError(Exception):
    """A refusal or a bad usage: the script prints it and exits 2."""


def die(prog, msg):
    print(f"{prog}: {msg}", file=sys.stderr)
    sys.exit(2)


def check_python():
    if sys.version_info < (3, 10):
        raise KitError(f"Python 3.10 or newer is needed (this is {platform_str()})")


def platform_str():
    return ".".join(str(x) for x in sys.version_info[:3])


# ---------------------------------------------------------------- the host
def host_os():
    """'linux', 'windows' or 'macos' for the machine this runs on."""
    p = sys.platform
    if p.startswith("linux"):
        return "linux"
    if p in ("win32", "cygwin", "msys"):
        return "windows"
    if p == "darwin":
        return "macos"
    raise KitError(f"unsupported OS '{p}': the kit runs on Linux, Windows and macOS")


def run_capture(argv, timeout=20):
    """stdout of a command as text, or '' when it is missing or fails. Never raises."""
    try:
        r = subprocess.run(argv, capture_output=True, text=True, timeout=timeout, encoding="utf-8", errors="replace")
        return r.stdout if r.returncode == 0 else ""
    except (OSError, subprocess.SubprocessError):
        return ""


def mac_is_arm(run=run_capture):
    """True on Apple silicon, also when this Python runs under Rosetta (then platform.machine() says x86_64)."""
    if run(["sysctl", "-n", "hw.optional.arm64"]).strip() == "1":
        return True
    return _platform.machine().lower() in ("arm64", "aarch64")


def host_rid(osname=None, machine=None, mac_arm=None):
    """The .NET runtime identifier of this machine, among the four upstream ships (package-release.ps1)."""
    osname = osname or host_os()
    m = (machine or _platform.machine()).lower()
    if osname == "linux" and m in ("x86_64", "amd64"):
        return "linux-x64"
    if osname == "windows" and m in ("amd64", "x86_64"):
        return "win-x64"
    if osname == "macos":
        arm = mac_is_arm() if mac_arm is None else mac_arm
        return "osx-arm64" if arm else "osx-x64"
    raise KitError(f"no Meitou build for {osname} on '{m}' (upstream ships linux-x64, win-x64, osx-x64 and osx-arm64); "
                   "pass an explicit runtime to build.py to try anyway")


RID_SUFFIX = {"linux-x64": "linux", "win-x64": "win", "osx-arm64": "osx-arm64", "osx-x64": "osx-x64"}


def exe_name(rid):
    return "MeitouClient.exe" if rid.startswith("win-") else "MeitouClient"


def default_platform(osname):
    return {"linux": "native", "windows": "windows", "macos": "macos"}[osname]


def os_family(plat):
    """OS family of a `platform` value: native and proton are Linux machines."""
    return {"native": "linux", "proton": "linux", "windows": "windows", "macos": "macos"}[plat]


def platform_rid(plat, osname, mac_arm=None):
    """Which build a platform runs: proton runs the Windows build, the others the one of the host."""
    if plat == "proton":
        return "win-x64"
    return host_rid(osname, mac_arm=mac_arm)


def check_platform(plat, osname):
    if plat not in PLATFORMS:
        raise KitError(f"--platform must be one of {', '.join(PLATFORMS)}")
    if plat in ("native", "proton") and osname != "linux":
        raise KitError(f"platform '{plat}' needs Linux (this is {osname}); use --platform {default_platform(osname)}")
    if plat in ("windows", "macos") and os_family(plat) != osname:
        raise KitError(f"platform '{plat}' needs {os_family(plat)} (this is {osname}); use --platform {default_platform(osname)}")


# ---------------------------------------------------------------- benchmark targets
def targets_json():
    """The list in targets.json: the public copy first, then ../targets.json (offline, or before the public repository has it)."""
    try:
        with urllib.request.urlopen(TARGETS_URL, timeout=10) as r:
            t = json.loads(r.read().decode("utf-8"))
        if isinstance(t, list) and t:
            return t
    except (OSError, ValueError):
        pass
    try:
        with open(os.path.join(KIT, "..", "targets.json"), encoding="utf-8") as f:
            t = json.load(f)
        return t if isinstance(t, list) else []
    except (OSError, ValueError):
        return []


def target_query(want, targets=None):
    """('<commit8>', '<label>') of the latest target ('latest') or of the target equal to <want>; None if there is none."""
    targets = targets_json() if targets is None else targets
    for x in (reversed(targets) if want == "latest" else targets):
        if want == "latest" or x["commit"].startswith(want[:8]):
            return x["commit"], x.get("label", "")
    return None


# ---------------------------------------------------------------- the save
def read_manifest(path):
    """{relative path: sha256} from a 'sha256sum' style manifest ('<hash>  ./path' or '<hash> *path')."""
    out = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line.strip():
                continue
            m = re.match(r"^([0-9a-fA-F]{64}) [ *](.*)$", line)
            if not m:
                raise KitError(f"{path}: cannot parse line: {line[:60]}")
            p = m.group(2)
            if p.startswith("./"):
                p = p[2:]
            out[p] = m.group(1).lower()
    return out


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def verify_save(folder, manifest_path):
    """(ok, problems): the folder holds exactly the files of the manifest with exactly their hashes. Read-only."""
    want = read_manifest(manifest_path)
    have = []
    for root, _dirs, files in os.walk(folder):
        for fn in files:
            have.append(os.path.relpath(os.path.join(root, fn), folder).replace(os.sep, "/"))
    problems = []
    for p in sorted(set(want) - set(have)):
        problems.append(f"missing: {p}")
    for p in sorted(set(have) - set(want)):
        problems.append(f"not in the manifest: {p}")
    for p in sorted(set(want) & set(have)):
        if sha256_file(os.path.join(folder, *p.split("/"))) != want[p]:
            problems.append(f"differs: {p}")
    return not problems, problems


# ---------------------------------------------------------------- hardware probes
PS_PROBE = (
    "$ErrorActionPreference='SilentlyContinue';"
    "$c=Get-CimInstance Win32_Processor|Select-Object -First 1;"
    "$o=Get-CimInstance Win32_OperatingSystem;"
    "$s=Get-CimInstance Win32_ComputerSystem;"
    "$g=@(Get-CimInstance Win32_VideoController|ForEach-Object{@{name=$_.Name;driver=$_.DriverVersion;w=$_.CurrentHorizontalResolution;h=$_.CurrentVerticalResolution}});"
    "@{cpu=$c.Name;ram=$s.TotalPhysicalMemory;os=$o.Caption;ver=$o.Version;free_kb=$o.FreePhysicalMemory;gpus=$g}|ConvertTo-Json -Depth 4 -Compress"
)


def pick_gpu(gpus):
    """The GPU a game would use among several: NVIDIA first, then AMD/Radeon, then Intel Arc, else the first; virtual/basic adapters last."""
    def score(g):
        n = (g.get("name") or "").lower()
        if any(x in n for x in ("basic", "virtual", "remote", "parsec", "indirect")):
            return 9
        if "nvidia" in n or "geforce" in n or "quadro" in n:
            return 0
        if "radeon" in n and "graphics" not in n:
            return 1
        if "arc" in n:
            return 2
        return 3
    return sorted(gpus, key=score)[0] if gpus else {}


def _clean(s):
    return re.sub(r"\s+", " ", (s or "").strip())


def hw_windows(run=run_capture):
    """CPU, RAM, GPU, driver, OS, 'kernel' (Windows version), display, free MB: from one PowerShell call (Get-CimInstance)."""
    out = run(["powershell", "-NoProfile", "-NonInteractive", "-Command", PS_PROBE], timeout=60)
    try:
        d = json.loads(out)
    except ValueError:
        d = {}
    gpus = d.get("gpus") or []
    if isinstance(gpus, dict):
        gpus = [gpus]
    g = pick_gpu(gpus)
    res = ""
    if g.get("w") and g.get("h"):
        res = f"{g['w']}x{g['h']}"
    elif gpus and gpus[0].get("w"):
        res = f"{gpus[0]['w']}x{gpus[0]['h']}"
    ram = d.get("ram")
    return dict(cpu=_clean(d.get("cpu")), ram_gb=str(round(int(ram) / 1073741824)) if ram else "", gpu=_clean(g.get("name")), gpu_driver=_clean(g.get("driver")),
                os=_clean(d.get("os")), kernel=_clean(d.get("ver")), display=res, avail_mb=int(d["free_kb"]) // 1024 if d.get("free_kb") else None)


def parse_vm_stat(text):
    """Available MB from `vm_stat` (free + inactive + speculative pages)."""
    m = re.search(r"page size of (\d+) bytes", text)
    size = int(m.group(1)) if m else 4096
    pages = 0
    for key in ("Pages free", "Pages inactive", "Pages speculative"):
        mm = re.search(key + r":\s+(\d+)", text)
        pages += int(mm.group(1)) if mm else 0
    return pages * size // 1048576 if pages else None


def parse_system_profiler(text):
    """(gpu name, driver text, resolution) from `system_profiler SPDisplaysDataType -json`."""
    try:
        items = json.loads(text).get("SPDisplaysDataType") or []
    except ValueError:
        return "", "", ""
    if not items:
        return "", "", ""
    g = items[0]
    name = g.get("sppci_model") or g.get("_name") or ""
    drv = g.get("spdisplays_mtlgpufamilysupport") or g.get("spdisplays_metal") or ""
    drv = drv.replace("spdisplays_", "").replace("_", " ") if drv else ""
    res = ""
    for disp in g.get("spdisplays_ndrvs") or []:
        m = re.search(r"(\d{3,5})\s*x\s*(\d{3,5})", disp.get("_spdisplays_pixels") or disp.get("_spdisplays_resolution") or "")
        if m:
            res = f"{m.group(1)}x{m.group(2)}"
            break
    return _clean(name), _clean(drv), res


def hw_macos(run=run_capture):
    cpu = _clean(run(["sysctl", "-n", "machdep.cpu.brand_string"]))
    mem = run(["sysctl", "-n", "hw.memsize"]).strip()
    name, ver = _clean(run(["sw_vers", "-productName"])), _clean(run(["sw_vers", "-productVersion"]))
    gname, drv, res = parse_system_profiler(run(["system_profiler", "SPDisplaysDataType", "-json"], timeout=60))
    return dict(cpu=cpu, ram_gb=str(round(int(mem) / 1073741824)) if mem.isdigit() else "", gpu=gname, gpu_driver=drv,
                os=f"{name} {ver}".strip(), kernel=_clean(run(["uname", "-r"])), display=res, avail_mb=parse_vm_stat(run(["vm_stat"])))


def hw_linux(run=run_capture, read=None):
    read = read or (lambda p: open(p, encoding="utf-8", errors="replace").read())
    def rd(p):
        try:
            return read(p)
        except OSError:
            return ""
    cpu = ""
    for line in rd("/proc/cpuinfo").splitlines():
        if re.match(r"^(model name|Model)\s*:", line):
            cpu = _clean(line.split(":", 1)[1])
            break
    mi = rd("/proc/meminfo")
    mt = re.search(r"^MemTotal:\s+(\d+)", mi, re.M)
    ma = re.search(r"^MemAvailable:\s+(\d+)", mi, re.M)
    name = drv = ""
    smi = run(["nvidia-smi", "--query-gpu=name,driver_version", "--format=csv,noheader"]).splitlines()
    if smi:
        parts = [x.strip() for x in smi[0].split(",", 1)]
        name, drv = parts[0], (parts[1] if len(parts) > 1 else "")
    if not name:
        vi = run(["vulkaninfo", "--summary"])
        m = re.search(r"^\s*deviceName\s*=\s*(.*)$", vi, re.M)
        d = re.search(r"^\s*driverInfo\s*=\s*(.*)$", vi, re.M)
        name, drv = (m.group(1).strip() if m else ""), (d.group(1).strip() if d else "")
    if not name:
        gi = run(["glxinfo", "-B"])
        m = re.search(r"^OpenGL renderer string:\s*(.*)$", gi, re.M)
        d = re.search(r"^OpenGL version string:\s*(.*)$", gi, re.M)
        name, drv = (m.group(1).strip() if m else ""), (d.group(1).strip() if d else "")
    osr = rd("/etc/os-release")
    pm = re.search(r'^PRETTY_NAME="?([^"\n]*)"?', osr, re.M) or re.search(r'^NAME="?([^"\n]*)"?', osr, re.M)
    res = ""
    m = re.search(r"^\s*(\d+x\d+).*\*", run(["xrandr", "--current"]), re.M)
    if m:
        res = m.group(1)
    return dict(cpu=cpu, ram_gb=str(round(int(mt.group(1)) / 1048576)) if mt else "", gpu=name, gpu_driver=drv, os=pm.group(1).strip() if pm else "",
                kernel=_clean(run(["uname", "-r"])), display=res, avail_mb=int(ma.group(1)) // 1024 if ma else None)


def hw_info(osname, run=run_capture, env=None, read=None):
    """Machine description: dict cpu, ram_gb, gpu, gpu_driver, os, kernel, display (strings; 'unknown' when not found) and avail_mb (int or None).
    Env overrides: CPU RAM_GB GPU_NAME GPU_DRIVER OS_NAME DISPLAY_RES."""
    env = os.environ if env is None else env
    d = {"linux": lambda: hw_linux(run, read), "windows": lambda: hw_windows(run), "macos": lambda: hw_macos(run)}[osname]()
    for key, var in (("cpu", "CPU"), ("ram_gb", "RAM_GB"), ("gpu", "GPU_NAME"), ("gpu_driver", "GPU_DRIVER"), ("os", "OS_NAME"), ("display", "DISPLAY_RES")):
        if env.get(var):
            d[key] = env[var]
    for key in ("cpu", "ram_gb", "gpu", "gpu_driver", "os", "kernel", "display"):
        if not d.get(key):
            d[key] = "unknown"
    return d


def ram_avail_mb(osname, run=run_capture, env=None):
    """Available RAM in MB, or None when it cannot be read (the check is then skipped with a warning)."""
    return hw_info(osname, run=run, env={} if env is None else env)["avail_mb"]


# ---------------------------------------------------------------- game processes
def game_process_lines(osname, run=run_capture):
    """Lines describing a running Meitou or original Kenshi game (empty list when none)."""
    if osname == "windows":
        out = run(["tasklist", "/FO", "CSV", "/NH"])
        return [l for l in out.splitlines() if re.match(r'^"(MeitouClient|kenshi_x64|kenshi_GOG_x64)\.exe"', l, re.I)]
    out = run(["ps", "-eo", "pid,args"])
    pat = re.compile(r"(^|[ /\\])(MeitouClient(\.exe)?|kenshi_(gog_)?x64\.exe)( |$)", re.I)
    return [l for l in out.splitlines() if pat.search(l) and "grep" not in l and "bench.py" not in l]


# ---------------------------------------------------------------- misc
def copytree_rw(src, dst):
    shutil.copytree(src, dst)
    try:
        os.chmod(dst, os.stat(dst).st_mode | 0o200)   # the top folder too (the game writes its autosave into it; a read-only one cannot be removed)
    except OSError:
        pass
    for root, dirs, files in os.walk(dst):   # a copy of a read-only save must be writable (the game writes its autosave there)
        for n in dirs + files:
            p = os.path.join(root, n)
            try:
                os.chmod(p, os.stat(p).st_mode | 0o200)
            except OSError:
                pass


def rmtree_force(path):
    def onerr(func, p, _exc):
        try:
            os.chmod(p, 0o700)
            func(p)
        except OSError:
            pass
    if sys.version_info >= (3, 12):
        shutil.rmtree(path, onexc=lambda f, p, e: onerr(f, p, e))
    else:
        shutil.rmtree(path, onerror=lambda f, p, e: onerr(f, p, e))


def memory_scope(osname, memory_max, swap="3G", run=run_capture):
    """argv prefix that runs a command in a systemd user scope with a memory cap (Linux with systemd --user only), else []."""
    if not memory_max:
        return []
    if osname == "linux" and shutil.which("systemd-run") and (shutil.which("systemctl")):
        try:
            state = subprocess.run(["systemctl", "--user", "is-system-running"], capture_output=True, text=True, timeout=10).stdout.strip()
        except (OSError, subprocess.SubprocessError):
            state = ""
        if state in ("running", "degraded"):
            return ["systemd-run", "--user", "--scope", "--quiet", "-p", f"MemoryMax={memory_max}", "-p", f"MemorySwapMax={swap}", "--"]
    print(f"MEMORY_MAX is set but a memory cap is only available on Linux with `systemd-run --user`: running without a cap", file=sys.stderr)
    return []


def tmpdir(prefix):
    return tempfile.mkdtemp(prefix=prefix)

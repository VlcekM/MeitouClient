#!/usr/bin/env python3
"""build.py: fetch upstream Meitou, build the benchmark target the way the maintainers do (self-contained release publish), install the benchmark settings.
Works on Linux, Windows and macOS (Python 3.10+, standard library only; needs git and the .NET 10 SDK).

Usage:  python3 build.py [target]
  target   host (default)  the build for this machine: linux-x64, win-x64, osx-arm64 or osx-x64
           native          the same as host (kept from the old script)
           windows         the win-x64 build (for --platform proton on Linux; also possible to build on any OS)
           both            host and win-x64
           <rid>           an explicit runtime identifier (linux-x64, win-x64, osx-arm64, osx-x64)
Env (all optional):
  MEITOU_SRC    clone of MeitouClient to use (default <kit>/MeitouClient, cloned there if missing). Your working tree is never touched: the commit is
                built from a temporary git worktree.
  MEITOU_URL    what to clone (default https://github.com/VlcekM/MeitouClient.git)
  REMOTE, REF   what to build. Default REF = the latest benchmark target (read from targets.json); the build folder is then Meitou-master-<suffix>.
                Another REF still works but is OFF-TARGET (warned; accepted when submitted, flagged on the page); the folder is then named after it.
  BUILD_ROOT    where builds go (default <kit>/builds). Folders: Meitou-master-linux, -win, -osx-arm64, -osx-x64.
  DOTNET_ROOT   folder of a .NET SDK that is not on PATH (put first on PATH). Otherwise `dotnet` from PATH; the SDK must be 10.x.
  MEMORY_MAX    e.g. 6G: run the publish in a systemd user scope with this cap (Linux with `systemd-run --user` only; ignored elsewhere).
  FORCE=1       rebuild even if the build folder already holds this commit.
The publish command is upstream's own (tools/scripts/package-release.ps1):
  dotnet publish src/Meitou.Game/Meitou.Game.csproj -c Release -r <rid> --self-contained true -p:DebugType=embedded -o <folder>
On macOS the same script then ad-hoc signs the executable and the .dylib files (Apple silicon refuses unsigned code) and so does this one (codesign --sign -),
and the quarantine flag is removed. Native debug symbols (*.dbg, *.dwarf, *.dSYM, about 250 MB on Unix) are deleted, as upstream's release zip does.
Each build folder gets BUILT_FROM.txt ("<name>-<sha8> (<rid>)", read by bench.py for the commit) and meitou.user.json (the kit's settings file; a rebuild
recreates the folder, so the settings file is always installed again).
Not done: the Windows release also carries vendor DLLs (DLSS Streamline, FSR) that are not in the repository; the benchmark settings (TAA) do not need
them. macOS needs a Vulkan loader and MoltenVK at run time (brew install molten-vk vulkan-loader), see README.md.
"""
import glob
import os
import shutil
import subprocess
import sys

import kitlib as K

PROG = "build.py"
MEITOU_CSPROJ = "src/Meitou.Game/Meitou.Game.csproj"


def publish_command(rid, project, out, scope=()):
    """The dotnet publish command line for one runtime (the same on every OS; cross-building win-x64 on Linux works)."""
    return list(scope) + ["dotnet", "publish", project, "-c", "Release", "-r", rid, "--self-contained", "true", "-p:DebugType=embedded", "-o", out]


def post_publish_commands(rid, out):
    """Commands to run in the build folder after publishing (macOS: ad-hoc signing and un-quarantine). Each is an argv list."""
    if not rid.startswith("osx-"):
        return []
    cmds = []
    code = [os.path.join(out, "MeitouClient")] + sorted(glob.glob(os.path.join(out, "*.dylib")))
    for f in code:
        cmds.append(["codesign", "--force", "--sign", "-", f])
    cmds.append(["xattr", "-dr", "com.apple.quarantine", out])
    return cmds


def target_rids(target, osname):
    if target in ("host", "native"):
        return [K.host_rid(osname)]
    if target == "windows":
        return ["win-x64"]
    if target == "both":
        h = K.host_rid(osname)
        return [h] if h == "win-x64" else [h, "win-x64"]
    if target in K.RID_SUFFIX:
        return [target]
    raise K.KitError("usage: build.py [host|native|windows|both|linux-x64|win-x64|osx-arm64|osx-x64]")


def build_folder_name(name, rid):
    return f"{name}-{K.RID_SUFFIX.get(rid, rid)}"


def sdk_ok(run=K.run_capture):
    return any(l.startswith("10.") for l in run(["dotnet", "--list-sdks"]).splitlines())


def main(argv):
    K.check_python()
    if argv and argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if len(argv) > 1:
        raise K.KitError("usage: build.py [target] (see build.py --help)")
    osname = K.host_os()
    rids = target_rids(argv[0] if argv else "host", osname)
    here = K.KIT
    src = os.path.abspath(os.environ.get("MEITOU_SRC") or os.path.join(here, "MeitouClient"))
    url = os.environ.get("MEITOU_URL", "https://github.com/VlcekM/MeitouClient.git")
    remote = os.environ.get("REMOTE", "origin")
    root = os.path.abspath(os.environ.get("BUILD_ROOT") or os.path.join(here, "builds"))
    if os.environ.get("DOTNET_ROOT"):
        d = os.environ["DOTNET_ROOT"]
        if not (os.path.exists(os.path.join(d, "dotnet")) or os.path.exists(os.path.join(d, "dotnet.exe"))):
            raise K.KitError(f"DOTNET_ROOT={d} has no dotnet executable")
        os.environ["PATH"] = d + os.pathsep + os.environ["PATH"]
    if not shutil.which("git"):
        raise K.KitError("'git' not found on PATH. Install git.")
    if not shutil.which("dotnet"):
        raise K.KitError("'dotnet' not found on PATH. Install the .NET 10 SDK (https://dotnet.microsoft.com/download) or set DOTNET_ROOT to its folder.")
    if not sdk_ok():
        raise K.KitError(".NET SDK 10.x not found (dotnet --list-sdks: %s)" % " ".join(K.run_capture(["dotnet", "--list-sdks"]).split("\n")))
    if "osx-" in " ".join(rids) and osname != "macos":
        print("build.py: WARNING: osx builds are made on a Mac upstream (ad-hoc signing needs codesign); this one will not be signed.", file=sys.stderr)
    settings = os.path.join(here, "meitou.user.json")
    if not os.path.isfile(settings):
        raise K.KitError(f"{settings} (the benchmark settings) is missing from the kit")

    if not os.path.exists(os.path.join(src, ".git")):
        print(f"cloning {url} into {src}")
        subprocess.run(["git", "clone", url, src], check=True)
    git = lambda *a: subprocess.run(["git", "-C", src] + list(a), capture_output=True, text=True)
    if git("remote", "get-url", remote).returncode:
        raise K.KitError(f"{src} has no remote '{remote}' (set REMOTE=<name of the upstream remote>)")
    subprocess.run(["git", "-C", src, "fetch", remote], check=True)

    ref = os.environ.get("REF", "")
    default_target = False
    if not ref:
        t = K.target_query("latest")
        if not t:
            raise K.KitError("cannot read the current benchmark target (no network and no ../targets.json). Set REF=<commit> to build a specific commit.")
        print(f"current benchmark standard: {t[0]} ({t[1]})" if t[1] else f"current benchmark standard: {t[0]}")
        ref, default_target = t[0], True
    else:
        print(f"WARNING: REF={ref} is set, so this build is probably OFF-TARGET: results for commits that are not benchmark targets are accepted but flagged "
              "on the page and left out of cross-machine comparisons.", file=sys.stderr)
    r = git("rev-parse", "--short=8", ref + "^{commit}")
    if r.returncode:
        raise K.KitError(f"cannot resolve {ref} in {src}")
    sha = r.stdout.strip()
    if default_target or ref == f"{remote}/master":
        name, bname = "Meitou-master", f"origin-master-{sha}"
    else:
        safe = ref.replace("/", "-")
        name, bname = f"Meitou-{safe}-{sha}", f"{safe}-{sha}"
    print(f"building {ref} = {sha} ({git('log', '-1', '--format=%s', sha).stdout.strip()[:100]})")

    scope = K.memory_scope(osname, os.environ.get("MEMORY_MAX"), swap="0")
    tree = K.tmpdir("meitou-src-")
    try:
        subprocess.run(["git", "-C", src, "worktree", "add", "-f", "--detach", tree, sha, "-q"], check=True)
        os.makedirs(root, exist_ok=True)
        for rid in rids:
            out = os.path.join(root, build_folder_name(name, rid))
            exe = os.path.join(out, K.exe_name(rid))
            built = os.path.join(out, "BUILT_FROM.txt")
            if not os.environ.get("FORCE") and os.path.isfile(built) and sha in open(built).read() and os.path.exists(exe):
                print(f"{out} already holds {sha} (FORCE=1 to rebuild)")
                continue
            if os.path.exists(out):
                K.rmtree_force(out)
            os.makedirs(out)
            subprocess.run(publish_command(rid, os.path.join(tree, *MEITOU_CSPROJ.split("/")), out, scope), check=True)
            for pat in ("*.dbg", "*.dwarf"):
                for f in glob.glob(os.path.join(out, "**", pat), recursive=True):
                    os.remove(f)
            for d in glob.glob(os.path.join(out, "**", "*.dSYM"), recursive=True):
                K.rmtree_force(d)
            if not rid.startswith("win-"):
                os.chmod(exe, os.stat(exe).st_mode | 0o111)
            if osname == "macos" and rid.startswith("osx-"):
                for cmd in post_publish_commands(rid, out):
                    if subprocess.run(cmd, capture_output=True).returncode:
                        print(f"build.py: WARNING: '{' '.join(cmd[:4])}...' failed (is the Xcode command line tools package installed? xcode-select --install)", file=sys.stderr)
            with open(built, "w", encoding="utf-8") as f:
                f.write(f"{bname} ({rid})\n")
            shutil.copyfile(settings, os.path.join(out, "meitou.user.json"))
            if not os.path.exists(exe):
                raise K.KitError(f"publish finished but there is no {K.exe_name(rid)} in {out}")
            print(f"done: {out} ({sha})")
    finally:
        subprocess.run(["git", "-C", src, "worktree", "remove", "--force", tree], capture_output=True)
        if os.path.exists(tree):
            K.rmtree_force(tree)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except K.KitError as e:
        K.die(PROG, str(e))
    except subprocess.CalledProcessError as e:
        K.die(PROG, f"command failed ({e.returncode}): {' '.join(map(str, e.cmd))[:200]}")
    except KeyboardInterrupt:
        sys.exit(130)

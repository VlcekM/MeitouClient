"""kitkeys.py: sending the base-switch key presses (double-tap 2, then 1) to the game, per OS. Standard library only.

  Windows  ctypes SendInput with scan codes (layout independent); the game window is brought to the front by PID first (best effort).
  macOS    ctypes CoreGraphics CGEventPost; needs Accessibility permission for the app that runs the script (Terminal, iTerm, ...).
  Linux    ydotool (needs ydotoold and access to /dev/uinput; works on Wayland and X11), or xdotool on X11 / XWayland as a fallback.

Every backend has: problem() -> text or None (why keys cannot be sent), start()/stop(), focus(pid), double_tap(key), and a pure function that
builds what it would send, so it can be tested without sending anything (tests/test_kit.py).
"""
import ctypes
import os
import shutil
import subprocess
import time

# key -> (Linux evdev code, Windows scan code set 1, macOS virtual key code, xdotool name). Keys "1" and "2" are the same physical keys on any layout.
KEYCODES = {"1": (2, 0x02, 0x12, "1"), "2": (3, 0x03, 0x13, "2")}


# ---------------------------------------------------------------- pure builders
def ydotool_argv(key):
    """ydotool key <code>:1 <code>:0 twice = a double tap."""
    c = KEYCODES[key][0]
    return ["ydotool", "key", f"{c}:1", f"{c}:0", f"{c}:1", f"{c}:0"]


def xdotool_argv(key):
    k = KEYCODES[key][3]
    return ["xdotool", "key", "--clearmodifiers", k, k]


def win_events(key):
    """[(scan code, flags)] for SendInput: down, up, down, up (KEYEVENTF_SCANCODE = 0x8, KEYEVENTF_KEYUP = 0x2)."""
    s = KEYCODES[key][1]
    return [(s, 0x8), (s, 0x8 | 0x2), (s, 0x8), (s, 0x8 | 0x2)]


def mac_events(key):
    """[(virtual key code, key down?)] for CGEventCreateKeyboardEvent: down, up, down, up."""
    v = KEYCODES[key][2]
    return [(v, True), (v, False), (v, True), (v, False)]


def mac_focus_argv(pid):
    return ["osascript", "-e", f'tell application "System Events" to set frontmost of (first process whose unix id is {int(pid)}) to true']


# ---------------------------------------------------------------- backends
class Backend:
    name = "none"

    def problem(self):
        return None

    def start(self):
        pass

    def stop(self):
        pass

    def focus(self, pid):
        pass

    def double_tap(self, key):
        raise NotImplementedError


class YdotoolKeys(Backend):
    name = "ydotool"

    def __init__(self):
        self.socket = os.environ.get("YDOTOOL_SOCKET", "")
        self.daemon = None

    def _find_socket(self):
        if self.socket and os.path.exists(self.socket):
            return self.socket
        for s in ("/tmp/.ydotool_socket", os.path.join(os.environ.get("XDG_RUNTIME_DIR", "/nonexistent"), ".ydotool_socket")):
            if os.path.exists(s):
                return s
        return ""

    def problem(self):
        if not shutil.which("ydotool"):
            return "ydotool not found on PATH"
        if not self._find_socket() and not shutil.which("ydotoold"):
            return "no ydotool daemon socket and no ydotoold on PATH (start ydotoold, which needs access to /dev/uinput, or set YDOTOOL_SOCKET)"
        return None

    def start(self):
        s = self._find_socket()
        if not s:
            s = self.socket or "/tmp/.ydotool_socket"
            self.daemon = subprocess.Popen(["ydotoold", f"--socket-path={s}"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            time.sleep(1)
            if not os.path.exists(s) or self.daemon.poll() is not None:
                raise RuntimeError("could not start ydotoold (is /dev/uinput writable for you? try `sudo ydotoold`, then set YDOTOOL_SOCKET=<its socket>)")
        self.socket = s

    def stop(self):
        if self.daemon and self.daemon.poll() is None:
            self.daemon.terminate()

    def double_tap(self, key):
        env = dict(os.environ, YDOTOOL_SOCKET=self.socket)
        return subprocess.run(ydotool_argv(key), env=env, capture_output=True).returncode == 0


class XdotoolKeys(Backend):
    name = "xdotool"

    def problem(self):
        if not shutil.which("xdotool"):
            return "xdotool not found on PATH"
        if not os.environ.get("DISPLAY"):
            return "no X11 DISPLAY for xdotool (the game must run on X11 or XWayland)"
        return None

    def double_tap(self, key):
        return subprocess.run(xdotool_argv(key), capture_output=True).returncode == 0


class WindowsKeys(Backend):
    name = "SendInput"

    def problem(self):
        if os.name != "nt":
            return "SendInput needs Windows"
        return None

    def focus(self, pid):
        """Bring the visible top-level window of the process to the front (best effort; Windows may refuse to steal focus)."""
        from ctypes import wintypes
        u = ctypes.windll.user32
        found = []
        EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        def cb(hwnd, _l):
            p = wintypes.DWORD()
            u.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
            if p.value == pid and u.IsWindowVisible(hwnd):
                found.append(hwnd)
            return True
        u.EnumWindows(EnumProc(cb), 0)
        if found:
            u.keybd_event(0x12, 0, 0, 0)      # a tap of Alt lets SetForegroundWindow succeed from a background process
            u.keybd_event(0x12, 0, 0x2, 0)
            u.ShowWindow(found[0], 9)         # SW_RESTORE
            u.SetForegroundWindow(found[0])
            time.sleep(0.2)

    def double_tap(self, key):
        from ctypes import wintypes
        ULONG_PTR = ctypes.c_size_t

        class KEYBDINPUT(ctypes.Structure):
            _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD), ("dwExtraInfo", ULONG_PTR)]

        class MOUSEINPUT(ctypes.Structure):
            _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG), ("mouseData", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                        ("time", wintypes.DWORD), ("dwExtraInfo", ULONG_PTR)]

        class _U(ctypes.Union):
            _fields_ = [("ki", KEYBDINPUT), ("mi", MOUSEINPUT)]

        class INPUT(ctypes.Structure):
            _fields_ = [("type", wintypes.DWORD), ("u", _U)]

        ok = True
        for scan, flags in win_events(key):
            inp = INPUT(1, _U(ki=KEYBDINPUT(0, scan, flags, 0, 0)))   # type 1 = INPUT_KEYBOARD
            ok &= ctypes.windll.user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT)) == 1
            time.sleep(0.03)
        return bool(ok)


class MacKeys(Backend):
    name = "CGEventPost"

    def problem(self):
        try:
            ax = ctypes.CDLL("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")
            ax.AXIsProcessTrusted.restype = ctypes.c_bool
            if not ax.AXIsProcessTrusted():
                return ("this terminal app has no Accessibility permission, so macOS drops the key presses: System Settings > Privacy & Security > "
                        "Accessibility, switch on the app that runs this script (Terminal, iTerm, VS Code, ...), then start it again")
        except (OSError, AttributeError):
            return "the macOS CoreGraphics / ApplicationServices frameworks could not be loaded (run this on a Mac)"
        return None

    def focus(self, pid):
        subprocess.run(mac_focus_argv(pid), capture_output=True)   # also needs Accessibility; if it fails the game keeps whatever focus it has
        time.sleep(0.3)

    def double_tap(self, key):
        cg = ctypes.CDLL("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")
        cf = ctypes.CDLL("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")
        cg.CGEventCreateKeyboardEvent.restype = ctypes.c_void_p
        cg.CGEventCreateKeyboardEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint16, ctypes.c_bool]
        cg.CGEventPost.argtypes = [ctypes.c_uint32, ctypes.c_void_p]
        cf.CFRelease.argtypes = [ctypes.c_void_p]
        for vk, down in mac_events(key):
            ev = cg.CGEventCreateKeyboardEvent(None, vk, down)
            if not ev:
                return False
            cg.CGEventPost(0, ev)   # kCGHIDEventTap
            cf.CFRelease(ev)
            time.sleep(0.03)
        return True


def choose(osname, forced=None):
    """(backend or None, reason when None). `forced` (env KEYS_BACKEND) = ydotool, xdotool or none."""
    if forced == "none":
        return None, "key sending disabled (KEYS_BACKEND=none)"
    if osname == "windows":
        cands = [WindowsKeys()]
    elif osname == "macos":
        cands = [MacKeys()]
    else:
        cands = [YdotoolKeys(), XdotoolKeys()]
        if forced:
            cands = [c for c in cands if c.name == forced]
    why = []
    for c in cands:
        p = c.problem()
        if p is None:
            return c, ""
        why.append(f"{c.name}: {p}")
    return None, "; ".join(why) or "no key backend"

// The render window: its size and screenshots, through OgreMain's exports.
#include "harness.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void* (*getRoot)(void);
static void* (*getWindow)(void* root);
static unsigned (*getWidth)(const void* target);
static unsigned (*getHeight)(const void* target);
static void (*writeContents)(void* target, const GameString* file);

int screen_init(void)
{
    const wchar_t* ogre = L"OgreMain_x64.dll";

    getRoot = sym(ogre, "?getSingletonPtr@Root@Ogre@@SAPEAV12@XZ");
    getWindow = sym(ogre, "?getAutoCreatedWindow@Root@Ogre@@QEAAPEAVRenderWindow@2@XZ");
    getWidth = sym(ogre, "?getWidth@RenderTarget@Ogre@@UEBAIXZ");
    getHeight = sym(ogre, "?getHeight@RenderTarget@Ogre@@UEBAIXZ");
    writeContents = sym(ogre, "?writeContentsToFile@RenderTarget@Ogre@@QEAAXAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@Z");

    return getRoot && getWindow && getWidth && getHeight && writeContents;
}

static void* window(void)
{
    void* root = getRoot ? getRoot() : NULL;
    return root ? getWindow(root) : NULL;
}

int screen_size(int* width, int* height)
{
    void* target = window();
    if (!target) return 0;

    *width = (int)getWidth(target);
    *height = (int)getHeight(target);
    return 1;
}

// Ogre picks the codec by the extension and throws on one it does not know or a folder that is not there, which would
// unwind through the game's frame loop: both are checked here first.
static const char* refuse_path(const char* path)
{
    const char* dot = strrchr(path, '.');
    if (!dot || (_stricmp(dot, ".png") != 0 && _stricmp(dot, ".jpg") != 0 && _stricmp(dot, ".bmp") != 0))
        return "the file must be a .png, .jpg or .bmp";

    char folder[MAX_PATH];
    snprintf(folder, sizeof folder, "%s", path);

    char* slash = strrchr(folder, '\\');
    if (!slash) return "the file needs a full path";
    *slash = 0;

    DWORD attributes = GetFileAttributesA(folder);
    if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY)) return "the folder does not exist";

    return NULL;
}

// shot <file>: the frame as the player sees it, GUI included. Waits a frame first so the last input has been drawn.
int cmd_shot(Command* c)
{
    const char* path = arg(c, 0, NULL);
    if (!path) return fail(c, "usage: shot <file.png>");

    const char* refusal = refuse_path(path);
    if (refusal) return fail(c, "%s: %s", path, refusal);

    void* target = window();
    if (!target) return fail(c, "no render window");
    if (c->step < 2) return 0;

    GameString file = gs_view(path);
    writeContents(target, &file);

    text_add(&c->reply, "{\"file\":");
    text_json(&c->reply, path);
    text_add(&c->reply, ",\"width\":%u,\"height\":%u,\"frame\":%ld}", getWidth(target), getHeight(target), frame_number());
    return 1;
}

// ---- The window's place among the desktop's windows ---------------------------------------------------------------------

// Ogre's Direct3D 11 render window class: found by class rather than visibility, since `window hide` hides it.
#define RENDER_WINDOW_CLASS "OgreD3D11Wnd"

static BOOL CALLBACK find_own_window(HWND window, LPARAM found)
{
    char name[32] = "";
    DWORD process = 0;
    GetWindowThreadProcessId(window, &process);
    if (process != GetCurrentProcessId()) return TRUE;

    GetClassNameA(window, name, sizeof name);
    if (strcmp(name, RENDER_WINDOW_CLASS) != 0) return TRUE;

    *(HWND*)found = window;
    return FALSE;
}

// window back [handle]: gives the keyboard focus to that window (in hex), or the desktop. The game takes the focus when it
// starts; the harness needs it nowhere, so `kh start` hands it back to the window that had it. Windows lets only the
// process that has the focus pass it on, which is why this is done in here.
static void give_focus_away(Command* c)
{
    HWND other = (HWND)(uintptr_t)strtoull(arg(c, 1, "0"), NULL, 16);
    SetForegroundWindow(IsWindow(other) ? other : GetShellWindow());
}

// window back [handle] | front | hide | show: the focus, or whether the window is on the desktop at all. A hidden window
// still renders, so screenshots, the GUI, input and the state all work as before.
int cmd_window(Command* c)
{
    const char* what = arg(c, 0, "");
    HWND own = NULL;
    EnumWindows(find_own_window, (LPARAM)&own);
    if (!own) return fail(c, "no render window");

    if (_stricmp(what, "front") == 0) SetForegroundWindow(own);
    else if (_stricmp(what, "back") == 0) give_focus_away(c);
    else if (_stricmp(what, "hide") == 0) ShowWindow(own, SW_HIDE);
    else if (_stricmp(what, "show") == 0) ShowWindow(own, SW_SHOWNOACTIVATE);
    else return fail(c, "usage: window back [handle] | front | hide | show");

    const char* focused = GetForegroundWindow() == own ? "true" : "false";
    const char* visible = IsWindowVisible(own) ? "true" : "false";
    text_add(&c->reply, "{\"focused\":%s,\"visible\":%s}", focused, visible);
    return 1;
}

// Keys and the mouse, fed to the game the way OIS feeds them: the listeners the game's keyboard and mouse objects call are
// called with events built here. The game's listener (MainListener) passes an event to MyGUI and to the game's own key
// map, so a click lands on a button or on the world exactly as a player's would; a plugin that put its own listener in
// front (Re_Dev's KeyboardHook does, for its hotkeys) gets the event first, as it does from the real keyboard. Nothing
// goes through Windows, so the window needs no focus and the real cursor stays where it is; with the window unfocused OIS
// (DISCL_FOREGROUND) sends the game nothing of its own, so the two do not fight.
//
// Layouts, verified with `kh peek` on 1.0.65: an OIS Object is a vtable, the vendor string, type, buffered, id and creator
// (0x48 bytes); a Keyboard follows it with its modifiers and its listener (0x50), a Mouse with its MouseState (0x48) and
// then its listener (0x88; the reverse of the OIS 1.3 header). Kenshi's InputHandler (KenshiLib `key`) holds the keyboard
// at 0xA0, and MainListener the mouse at 0x78. MainListener is found from the keyboard's listener: that listener itself,
// or the one a plugin's hook in front of it keeps (looked for among the hook's first fields, by RTTI name).
#include "harness.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- The devices --------------------------------------------------------------------------------------------------------

typedef struct { int type, abs, rel, absOnly; } Axis;
typedef struct { int width, height; Axis x, y, z; int buttons; } MouseState;
typedef struct { void* vtable; void* device; MouseState* state; } MouseEvent;
typedef struct { void* vtable; void* device; int key; unsigned text; } KeyEvent;

#define HANDLER_KEYBOARD 0xA0
#define KEYBOARD_LISTENER 0x50
#define MOUSE_STATE 0x48
#define MOUSE_LISTENER 0x88
#define LISTENER_MOUSE 0x78

// The listeners' virtual slots, after the destructor.
enum { ON_MOUSE_MOVED = 1, ON_MOUSE_PRESSED = 2, ON_MOUSE_RELEASED = 3, ON_KEY_PRESSED = 1, ON_KEY_RELEASED = 2 };

static void*** handlerVariable;
static void* keyboard;
static void* mouse;
static MouseState* mouseState;
static char (*injectText)(void* inputManager, int key, unsigned text);
static void* (*getInputManager)(void);

int input_init(void)
{
    handlerVariable = sym(L"KenshiLib.dll", "?key@@3PEAVInputHandler@@EA");
    injectText = sym(L"MyGUIEngine_x64.dll", "?injectKeyPress@InputManager@MyGUI@@QEAA_NUKeyCode@2@I@Z");
    getInputManager = sym(L"MyGUIEngine_x64.dll", "?getInstancePtr@?$Singleton@VInputManager@MyGUI@@@MyGUI@@SAPEAVInputManager@2@XZ");

    return handlerVariable && injectText && getInputManager;
}

static void* pointer_at(const void* base, size_t offset)
{
    const void* at = (const char*)base + offset;
    return base && readable(at, sizeof(void*)) ? *(void**)at : NULL;
}

static int is_class(const void* object, const char* mangled)
{
    const char* name = object ? rtti_name(object) : NULL;
    return name && strcmp(name, mangled) == 0;
}

// The game's MainListener behind key listener `listener`: itself, or the one a hook in front of it holds.
static void* main_listener(void* listener)
{
    for (int hop = 0; hop < 4 && listener; hop++)
    {
        if (is_class(listener, ".?AVMainListener@@")) return rtti_whole(listener);

        void* next = NULL;
        for (size_t field = 1; field <= 8 && !next; field++)
        {
            void* candidate = pointer_at(listener, field * sizeof(void*));
            if (is_class(candidate, ".?AVMainListener@@")) next = candidate;
        }

        listener = next;
    }

    return NULL;
}

// Follows input handler -> keyboard -> listener -> mouse; null, or what is missing when the game is not that far yet.
// Looked up for every command: a plugin may put its hook in at any time.
static const char* find_devices(void)
{
    void* handler = handlerVariable ? pointer_at(handlerVariable, 0) : NULL;
    void* board = pointer_at(handler, HANDLER_KEYBOARD);
    void* listener = main_listener(pointer_at(board, KEYBOARD_LISTENER));
    if (!listener) return "the game's input listener was not found (not started yet, or another game version)";

    void* pointer = pointer_at(listener, LISTENER_MOUSE);
    if (!is_class(pointer, ".?AVWin32Mouse@OIS@@") || !pointer_at(pointer, MOUSE_LISTENER))
        return "the game's mouse was not found where expected (another game version?)";

    keyboard = board;
    mouse = pointer;
    mouseState = (MouseState*)((char*)pointer + MOUSE_STATE);
    return NULL;
}

// ---- Events -------------------------------------------------------------------------------------------------------------

static void move_to(int x, int y, int wheel)
{
    mouseState->x.rel = x - mouseState->x.abs;
    mouseState->y.rel = y - mouseState->y.abs;
    mouseState->z.rel = wheel;
    mouseState->x.abs = x;
    mouseState->y.abs = y;
    mouseState->z.abs += wheel;

    void* listener = pointer_at(mouse, MOUSE_LISTENER);
    MouseEvent event = {NULL, mouse, mouseState};
    char (*moved)(void*, const MouseEvent*) = VIRTUAL(listener, ON_MOUSE_MOVED);
    moved(listener, &event);

    mouseState->z.rel = 0;
}

static void press_button(int button, int down)
{
    mouseState->x.rel = 0;
    mouseState->y.rel = 0;
    mouseState->buttons = down ? mouseState->buttons | (1 << button) : mouseState->buttons & ~(1 << button);

    void* listener = pointer_at(mouse, MOUSE_LISTENER);
    MouseEvent event = {NULL, mouse, mouseState};
    char (*pressed)(void*, const MouseEvent*, int) = VIRTUAL(listener, down ? ON_MOUSE_PRESSED : ON_MOUSE_RELEASED);
    pressed(listener, &event, button);
}

static void press_key(int code, int down)
{
    void* listener = pointer_at(keyboard, KEYBOARD_LISTENER);
    KeyEvent event = {NULL, keyboard, code, 0};
    char (*pressed)(void*, const KeyEvent*) = VIRTUAL(listener, down ? ON_KEY_PRESSED : ON_KEY_RELEASED);
    pressed(listener, &event);
}

// ---- Key names: OIS key codes are DirectInput scan codes ----------------------------------------------------------------

typedef struct { const char* name; int code; } KeyName;

static const KeyName KEYS[] = {
    {"escape", 0x01}, {"esc", 0x01}, {"1", 0x02}, {"2", 0x03}, {"3", 0x04}, {"4", 0x05}, {"5", 0x06}, {"6", 0x07}, {"7", 0x08},
    {"8", 0x09}, {"9", 0x0A}, {"0", 0x0B}, {"minus", 0x0C}, {"equals", 0x0D}, {"backspace", 0x0E}, {"tab", 0x0F}, {"q", 0x10},
    {"w", 0x11}, {"e", 0x12}, {"r", 0x13}, {"t", 0x14}, {"y", 0x15}, {"u", 0x16}, {"i", 0x17}, {"o", 0x18}, {"p", 0x19},
    {"lbracket", 0x1A}, {"rbracket", 0x1B}, {"enter", 0x1C}, {"return", 0x1C}, {"ctrl", 0x1D}, {"lctrl", 0x1D}, {"a", 0x1E},
    {"s", 0x1F}, {"d", 0x20}, {"f", 0x21}, {"g", 0x22}, {"h", 0x23}, {"j", 0x24}, {"k", 0x25}, {"l", 0x26}, {"semicolon", 0x27},
    {"apostrophe", 0x28}, {"grave", 0x29}, {"shift", 0x2A}, {"lshift", 0x2A}, {"backslash", 0x2B}, {"z", 0x2C}, {"x", 0x2D},
    {"c", 0x2E}, {"v", 0x2F}, {"b", 0x30}, {"n", 0x31}, {"m", 0x32}, {"comma", 0x33}, {"period", 0x34}, {"slash", 0x35},
    {"rshift", 0x36}, {"multiply", 0x37}, {"alt", 0x38}, {"lalt", 0x38}, {"space", 0x39}, {"capslock", 0x3A}, {"f1", 0x3B},
    {"f2", 0x3C}, {"f3", 0x3D}, {"f4", 0x3E}, {"f5", 0x3F}, {"f6", 0x40}, {"f7", 0x41}, {"f8", 0x42}, {"f9", 0x43}, {"f10", 0x44},
    {"numpad7", 0x47}, {"numpad8", 0x48}, {"numpad9", 0x49}, {"subtract", 0x4A}, {"numpad4", 0x4B}, {"numpad5", 0x4C},
    {"numpad6", 0x4D}, {"add", 0x4E}, {"numpad1", 0x4F}, {"numpad2", 0x50}, {"numpad3", 0x51}, {"numpad0", 0x52},
    {"decimal", 0x53}, {"f11", 0x57}, {"f12", 0x58}, {"numpadenter", 0x9C}, {"rctrl", 0x9D}, {"divide", 0xB5}, {"ralt", 0xB8},
    {"pause", 0xC5}, {"home", 0xC7}, {"up", 0xC8}, {"pgup", 0xC9}, {"left", 0xCB}, {"right", 0xCD}, {"end", 0xCF},
    {"down", 0xD0}, {"pgdn", 0xD1}, {"insert", 0xD2}, {"delete", 0xD3},
};

static int key_code(const char* name, size_t length)
{
    for (size_t i = 0; i < sizeof KEYS / sizeof KEYS[0]; i++)
    {
        if (strlen(KEYS[i].name) == length && _strnicmp(KEYS[i].name, name, length) == 0) return KEYS[i].code;
    }

    return 0;
}

#define MAX_CHORD 4

// "ctrl+shift+s" as its key codes in order; 0 when a name is unknown.
static int parse_chord(const char* text, int codes[MAX_CHORD])
{
    int count = 0;

    while (*text && count < MAX_CHORD)
    {
        const char* plus = strchr(text, '+');
        size_t length = plus ? (size_t)(plus - text) : strlen(text);

        codes[count] = key_code(text, length);
        if (!codes[count++]) return 0;

        text += length + (plus ? 1 : 0);
    }

    return count;
}

static void press_chord(const int codes[MAX_CHORD], int count, int down)
{
    for (int i = 0; i < count; i++)
    {
        press_key(codes[down ? i : count - 1 - i], down);
    }
}

// The --shift, --ctrl and --alt options of the mouse commands, held around the action.
static void hold_modifiers(const Command* c, int down)
{
    if (option(c, "--shift")) press_key(0x2A, down);
    if (option(c, "--ctrl")) press_key(0x1D, down);
    if (option(c, "--alt")) press_key(0x38, down);
}

// ---- Arguments ----------------------------------------------------------------------------------------------------------

static int is_number(const char* text)
{
    if (!text || !*text) return 0;

    char* end;
    strtol(text, &end, 10);
    return *end == 0;
}

static int option_number(const Command* c, const char* prefix, int fallback)
{
    size_t length = strlen(prefix);

    for (int i = 1; i < c->argc; i++)
    {
        if (strncmp(c->argv[i], prefix, length) == 0) return atoi(c->argv[i] + length);
    }

    return fallback;
}

// A point given as "x y" or as a widget's caption or name; `next` is the argument after it.
static int parse_point(Command* c, int first, int* x, int* y, int* next)
{
    const char* a = arg(c, first, NULL);
    const char* b = arg(c, first + 1, NULL);

    if (is_number(a) && is_number(b))
    {
        *x = atoi(a);
        *y = atoi(b);
        *next = first + 2;
        return 1;
    }

    if (!a) return 0;

    Text why = {0};
    text_clear(&why);
    int found = ui_locate(a, option_number(c, "--nth=", -1), x, y, &why);
    if (!found) fail(c, "%s", why.text);

    free(why.text);
    *next = first + 1;
    return found;
}

static int parse_button(const char* name)
{
    if (!name || _stricmp(name, "left") == 0) return 0;
    if (_stricmp(name, "right") == 0) return 1;
    if (_stricmp(name, "middle") == 0) return 2;
    return -1;
}

static int reply_point(Command* c, int x, int y)
{
    text_add(&c->reply, "{\"x\":%d,\"y\":%d,\"frame\":%ld}", x, y, frame_number());
    return 1;
}

// ---- Commands -----------------------------------------------------------------------------------------------------------

// mouse <x> <y> | mouse <widget>: moves the game's cursor there. mouse down|up [button]: presses or releases a button
// where the cursor is, for sequences click and drag do not cover.
int cmd_mouse(Command* c)
{
    const char* missing = find_devices();
    if (missing) return fail(c, "%s", missing);

    const char* first = arg(c, 0, "");
    if (_stricmp(first, "down") == 0 || _stricmp(first, "up") == 0)
    {
        int button = parse_button(arg(c, 1, NULL));
        if (button < 0) return fail(c, "the button is left, right or middle");

        press_button(button, _stricmp(first, "down") == 0);
        return reply_point(c, mouseState->x.abs, mouseState->y.abs);
    }

    int x, y, next;
    if (!parse_point(c, 0, &x, &y, &next)) return c->failed ? 1 : fail(c, "usage: mouse <x> <y> | mouse <widget>");

    move_to(x, y, 0);
    return reply_point(c, x, y);
}

// click <x> <y> [left|right|middle] | click <widget> [button], with --double, --hold=<ms> (the button stays down that
// long: the game opens its context menu on a held right button), --shift, --ctrl, --alt, --nth=<n>.
// The move, each press and each release are two frames apart, as the game samples what is under the cursor once a frame.
int cmd_click(Command* c)
{
    if (c->step == 0)
    {
        const char* missing = find_devices();
        if (missing) return fail(c, "%s", missing);

        int x, y, next;
        if (!parse_point(c, 0, &x, &y, &next)) return c->failed ? 1 : fail(c, "usage: click <x> <y> [button] | click <widget> [button]");

        int button = parse_button(arg(c, next, NULL));
        if (button < 0) return fail(c, "the button is left, right or middle");

        c->memo[0] = x;
        c->memo[1] = y;
        c->memo[2] = button;
        c->memo[3] = option(c, "--double") ? 2 : 1;

        hold_modifiers(c, 1);
        move_to(x, y, 0);
        return 0;
    }

    if (c->step % 2) return 0;

    // Odd phases press, even ones release; a release waits out --hold.
    int phase = (int)c->memo[5] + 1;
    int down = phase % 2;
    ULONGLONG held = GetTickCount64() - (ULONGLONG)c->memo[4];

    if (phase > 2 * c->memo[3])
    {
        hold_modifiers(c, 0);
        return reply_point(c, (int)c->memo[0], (int)c->memo[1]);
    }

    if (!down && held < (ULONGLONG)option_number(c, "--hold=", 0)) return 0;

    press_button((int)c->memo[2], down);
    if (down) c->memo[4] = (intptr_t)GetTickCount64();

    c->memo[5] = phase;
    return 0;
}

#define DRAG_FRAMES 12

// drag <x1> <y1> <x2> <y2> [button]: press at the first point, move to the second over a dozen frames, release.
int cmd_drag(Command* c)
{
    if (c->step == 0)
    {
        const char* missing = find_devices();
        if (missing) return fail(c, "%s", missing);

        for (int i = 0; i < 4; i++)
        {
            if (!is_number(arg(c, i, NULL))) return fail(c, "usage: drag <x1> <y1> <x2> <y2> [button]");
            c->memo[i] = atoi(arg(c, i, NULL));
        }

        c->memo[4] = parse_button(arg(c, 4, NULL));
        if (c->memo[4] < 0) return fail(c, "the button is left, right or middle");

        hold_modifiers(c, 1);
        move_to((int)c->memo[0], (int)c->memo[1], 0);
        return 0;
    }

    int moved = c->step - 4;
    if (c->step == 2) press_button((int)c->memo[4], 1);

    if (moved >= 0 && moved <= DRAG_FRAMES)
    {
        int x = (int)(c->memo[0] + (c->memo[2] - c->memo[0]) * moved / DRAG_FRAMES);
        int y = (int)(c->memo[1] + (c->memo[3] - c->memo[1]) * moved / DRAG_FRAMES);
        move_to(x, y, 0);
    }

    if (moved < DRAG_FRAMES + 2) return 0;

    press_button((int)c->memo[4], 0);
    hold_modifiers(c, 0);
    return reply_point(c, (int)c->memo[2], (int)c->memo[3]);
}

// scroll <clicks> [<x> <y> | <widget>]: the wheel, up positive, at the cursor or at a point.
int cmd_scroll(Command* c)
{
    const char* missing = find_devices();
    if (missing) return fail(c, "%s", missing);
    if (!is_number(arg(c, 0, NULL))) return fail(c, "usage: scroll <clicks> [<x> <y> | <widget>]");

    int x = mouseState->x.abs;
    int y = mouseState->y.abs;
    int next;

    if (c->step == 0 && arg(c, 1, NULL))
    {
        if (!parse_point(c, 1, &x, &y, &next)) return c->failed ? 1 : fail(c, "bad point");

        move_to(x, y, 0);
        return 0;
    }

    if (c->step == 1) return 0;

    move_to(x, y, atoi(arg(c, 0, "0")) * 120);
    return reply_point(c, x, y);
}

// key <chord> [down|up]: "f3", "ctrl+s". Without down or up the chord is pressed and released two frames later.
int cmd_key(Command* c)
{
    const char* missing = find_devices();
    if (missing) return fail(c, "%s", missing);

    int codes[MAX_CHORD];
    int count = parse_chord(arg(c, 0, ""), codes);
    if (!count) return fail(c, "usage: key <name>[+<name>...] [down|up]; unknown key in '%s'", arg(c, 0, ""));

    const char* mode = arg(c, 1, "tap");
    int tap = _stricmp(mode, "tap") == 0;
    if (!tap && _stricmp(mode, "down") != 0 && _stricmp(mode, "up") != 0) return fail(c, "the mode is down or up");

    if (c->step == 0) press_chord(codes, count, tap || _stricmp(mode, "down") == 0);
    if (tap && c->step < 2) return 0;
    if (tap) press_chord(codes, count, 0);

    text_add(&c->reply, "{\"keys\":%d,\"frame\":%ld}", count, frame_number());
    return 1;
}

static unsigned next_code_point(const unsigned char** at)
{
    const unsigned char* s = *at;
    int extra = s[0] < 0x80 ? 0 : s[0] < 0xE0 ? 1 : s[0] < 0xF0 ? 2 : 3;
    unsigned code = extra == 0 ? s[0] : s[0] & (0x3F >> extra);

    for (int i = 1; i <= extra && s[i]; i++)
    {
        code = (code << 6) | (s[i] & 0x3F);
    }

    *at = s + 1 + extra;
    return code;
}

// type <text>: text into the edit box that has the key focus (click it first), a character a frame.
int cmd_type(Command* c)
{
    const char* text = arg(c, 0, NULL);
    void* manager = getInputManager ? getInputManager() : NULL;
    if (!text || !manager) return fail(c, text ? "the GUI is not up yet" : "usage: type <text>");

    const unsigned char* at = (const unsigned char*)text + c->memo[0];
    if (*at)
    {
        injectText(manager, 0, next_code_point(&at));
        c->memo[0] = (const char*)at - text;
        return 0;
    }

    text_add(&c->reply, "{\"typed\":%d}", (int)c->memo[0]);
    return 1;
}

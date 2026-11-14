// Tools for working on the harness itself: looking at the game's memory and at what the game's DLLs export.
#include "harness.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// A KenshiLib global by its short name: the object it points at.
static char* global_object(const char* name, size_t length)
{
    static const char* const GLOBALS[][2] = {
        {"ou", "?ou@@3PEAVGameWorld@@EA"}, {"key", "?key@@3PEAVInputHandler@@EA"}, {"gui", "?gui@@3PEAVForgottenGUI@@EA"},
        {"con", "?con@@3PEAVGlobalConstants@@EA"}, {"options", "?options@@3PEAVOptionsHolder@@EA"},
    };

    for (size_t i = 0; i < sizeof GLOBALS / sizeof GLOBALS[0]; i++)
    {
        if (strlen(GLOBALS[i][0]) != length || strncmp(GLOBALS[i][0], name, length) != 0) continue;

        char** variable = sym(L"KenshiLib.dll", GLOBALS[i][1]);
        return variable ? *variable : NULL;
    }

    return NULL;
}

// "ou+580*+2B0": a hex address or a global, then +offset (hex) and * (follow the pointer there) left to right.
static char* evaluate(const char* text, const char** error)
{
    size_t length = strcspn(text, "+*");
    char* at = global_object(text, length);
    if (!at) at = (char*)strtoull(text, NULL, 16);

    for (const char* op = text + length; *op && at; )
    {
        if (*op == '+')
        {
            char* end;
            at += strtoull(op + 1, &end, 16);
            op = end;
            continue;
        }

        if (!readable(at, sizeof(void*)))
        {
            *error = "a pointer on the way is not readable";
            return NULL;
        }

        at = *(char**)at;
        op++;
    }

    if (!at) *error = "the address is null";
    return at;
}

static void dump_line(Text* out, const unsigned char* at, size_t count)
{
    text_add(out, "%p ", at);

    for (size_t i = 0; i < 16; i++)
    {
        if (i < count) text_add(out, "%s%02x", i % 8 ? " " : "  ", at[i]);
        else text_add(out, "%s  ", i % 8 ? " " : "  ");
    }

    text_add(out, "  ");
    for (size_t i = 0; i < count; i++)
    {
        text_add(out, "%c", at[i] >= 0x20 && at[i] < 0x7F ? at[i] : '.');
    }

    text_add(out, "\n");
}

// peek <address> [bytes]: a hex dump. The address is hex or a KenshiLib global (ou, key, gui, con, options), then
// +offset and * steps: "peek ou+580*+2B0 24" is the player interface's squad list.
int cmd_peek(Command* c)
{
    const char* expression = arg(c, 0, NULL);
    if (!expression) return fail(c, "usage: peek <address>[+offset][*]... [bytes]");

    const char* error = NULL;
    const unsigned char* at = (const unsigned char*)evaluate(expression, &error);
    size_t size = (size_t)atoi(arg(c, 1, "64"));
    if (!at) return fail(c, "%s: %s", expression, error);
    if (size == 0 || size > 4096 || !readable(at, size)) return fail(c, "%p: %zu bytes are not readable", at, size);

    const char* type = rtti_name(at);
    if (type) text_add(&c->reply, "object of %s\n", type);

    for (size_t done = 0; done < size; done += 16)
    {
        dump_line(&c->reply, at + done, size - done < 16 ? size - done : 16);
    }

    return 1;
}

// fault: reads a bad pointer on purpose, to check that a fault ends the command and not the game.
int cmd_fault(Command* c)
{
    volatile int* nowhere = (volatile int*)(uintptr_t)(8 + c->step);

    text_add(&c->reply, "read %d\n", *nowhere);
    return 1;
}

// sym <module> <export>: where a DLL's export is in this process, to check a name before the harness uses it.
int cmd_sym(Command* c)
{
    const char* module = arg(c, 0, NULL);
    const char* name = arg(c, 1, NULL);
    if (!module || !name) return fail(c, "usage: sym <module.dll> <export name>");

    wchar_t wide[MAX_PATH];
    MultiByteToWideChar(CP_UTF8, 0, module, -1, wide, MAX_PATH);

    HMODULE handle = GetModuleHandleW(wide);
    void* found = handle ? (void*)GetProcAddress(handle, name) : NULL;
    if (!found) return fail(c, "%s has no export %s", handle ? module : "(module not loaded)", name);

    text_add(&c->reply, "%p (module at %p)\n", found, (void*)handle);
    return 1;
}

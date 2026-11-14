#include "abi.h"
#include "log.h"
#include <stdio.h>
#include <string.h>

// ---- std::string --------------------------------------------------------------------------------------------------------

const char* gs_text(const GameString* s) { return s->capacity > 15 ? s->at.heap : s->at.text; }

GameString gs_view(const char* text)
{
    GameString s;
    memset(&s, 0, sizeof s);
    s.size = strlen(text);

    if (s.size <= 15)
    {
        memcpy(s.at.text, text, s.size + 1);
        s.capacity = 15;
        return s;
    }

    s.at.heap = (char*)text;
    s.capacity = s.size;
    return s;
}

void gs_free(GameString* s)
{
    static void (*release)(void*);
    if (!release) release = (void (*)(void*))sym(L"MSVCR100.dll", "??3@YAXPEAX@Z");

    if (s->capacity > 15 && release) release(s->at.heap);
    s->capacity = 15;
    s->size = 0;
}

// ---- Memory -------------------------------------------------------------------------------------------------------------

int readable(const void* p, size_t size)
{
    MEMORY_BASIC_INFORMATION info;
    const char* at = (const char*)p;
    const char* end = at + size;

    while (at < end)
    {
        if (!VirtualQuery(at, &info, sizeof info)) return 0;
        if (info.State != MEM_COMMIT || (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) || !info.Protect) return 0;

        at = (const char*)info.BaseAddress + info.RegionSize;
    }

    return 1;
}

// ---- RTTI (VC++ x64): the slot before a vtable points at the class's locator, whose fields are offsets from the image ----

typedef struct { uint32_t signature, offset, constructorOffset, type, hierarchy, self; } Locator;
typedef struct { uint32_t signature, attributes, baseCount, bases; } Hierarchy;
typedef struct { uint32_t type, contained; int32_t memberOffset, vbaseOffset, vtableOffset; uint32_t attributes; } BaseClass;

// The name sits after the type descriptor's vtable pointer and spare pointer.
#define TYPE_NAME(image, rva) ((const char*)(image) + (rva) + 16)

static const Locator* locator_of(const void* object)
{
    if (!readable(object, sizeof(void*))) return NULL;

    void** vtable = *(void***)object;
    if (!readable(vtable - 1, sizeof(void*))) return NULL;

    const Locator* locator = (const Locator*)vtable[-1];
    if (!readable(locator, sizeof *locator) || locator->signature != 1) return NULL;

    return locator;
}

const char* rtti_name(const void* object)
{
    const Locator* locator = locator_of(object);
    if (!locator) return NULL;

    const char* image = (const char*)locator - locator->self;
    const char* name = TYPE_NAME(image, locator->type);
    return readable(name, 4) ? name : NULL;
}

void* rtti_whole(const void* part)
{
    const Locator* locator = locator_of(part);
    return locator ? (char*)part - locator->offset : NULL;
}

int rtti_base_offset(const void* object, const char* mangled)
{
    const Locator* locator = locator_of(object);
    if (!locator) return -1;

    const char* image = (const char*)locator - locator->self;
    const Hierarchy* hierarchy = (const Hierarchy*)(image + locator->hierarchy);
    const uint32_t* bases = (const uint32_t*)(image + hierarchy->bases);

    for (uint32_t i = 0; i < hierarchy->baseCount; i++)
    {
        const BaseClass* base = (const BaseClass*)(image + bases[i]);
        if (strcmp(TYPE_NAME(image, base->type), mangled) == 0) return base->memberOffset;
    }

    return -1;
}

void rtti_plain_name(const char* mangled, char* out, size_t size)
{
    // ".?AVButton@MyGUI@@" is class Button in namespace MyGUI: the innermost name first, the parts split by '@'.
    const char* start = strncmp(mangled, ".?A", 3) == 0 ? mangled + 4 : mangled;
    const char* end = strstr(start, "@@");
    if (!end) end = start + strlen(start);

    const char* outer = memchr(start, '@', (size_t)(end - start));
    size_t length = (size_t)((outer ? outer : end) - start);
    int ours = outer && strncmp(outer + 1, "MyGUI", 5) == 0;

    out[0] = 0;
    if (outer && !ours) snprintf(out, size, "%.*s::", (int)(end - outer - 1), outer + 1);
    snprintf(out + strlen(out), size - strlen(out), "%.*s", (int)length, start);
}

int vtable_slot(void** vtable, void* fn, int limit)
{
    for (int i = 0; i < limit; i++)
    {
        if (vtable[i] == fn) return i;
    }

    return -1;
}

// ---- Symbols ------------------------------------------------------------------------------------------------------------

void* sym(const wchar_t* module, const char* name)
{
    HMODULE handle = GetModuleHandleW(module);
    void* found = handle ? (void*)GetProcAddress(handle, name) : NULL;

    if (!found) log_line("missing %ls!%s", module, name);
    return found;
}

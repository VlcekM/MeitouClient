// The game's side of the C++ ABI, as plain C: Kenshi and its DLLs are built with Visual C++ 2010, so its std::string, its
// containers and its RTTI have fixed layouts this plugin reads and builds by hand.
#pragma once
#include <windows.h>
#include <stddef.h>
#include <stdint.h>

// ---- std::string (VC++ 2010, release): 16 bytes of text or a pointer, the length, the capacity, the allocator -----------
// Verified against the running game: InputHandler::Command (KenshiLib InputHandler.h) has its name at 0x18 and the next
// member at 0x40, and a string under 16 characters keeps its text in place.

typedef struct
{
    union { char text[16]; char* heap; } at;
    size_t size;
    size_t capacity;
    void* allocator;
} GameString;

// The string's text (not always zero terminated past `size`).
const char* gs_text(const GameString* s);

// A string to pass as `const std::string&`: it borrows `text`, which must outlive the call.
GameString gs_view(const char* text);

// Frees a string the game returned by value.
void gs_free(GameString* s);

// ---- lektor<T>, the game's own vector: a count, a capacity and the elements ---------------------------------------------

typedef struct
{
    void* unused;
    uint32_t count;
    uint32_t capacity;
    void** items;
} Lektor;

// ---- Ogre maths ---------------------------------------------------------------------------------------------------------

typedef struct { float x, y, z; } V3;
typedef struct { float w, x, y, z; } Q4;

// ---- Memory and RTTI ----------------------------------------------------------------------------------------------------

// Whether `size` bytes at `p` are committed, readable memory.
int readable(const void* p, size_t size);

// An object's class as its RTTI names it (".?AVButton@MyGUI@@"), or null when it has none.
const char* rtti_name(const void* object);

// The start of the whole object that `part` (a pointer to one of its base-class parts) belongs to, or null without RTTI.
void* rtti_whole(const void* part);

// The offset of base class `mangled` (".?AVTextBox@MyGUI@@") inside `object`, or -1 when the object does not derive from it.
int rtti_base_offset(const void* object, const char* mangled);

// Writes the class's plain name ("Button", or "Outer::Inner" outside MyGUI) into `out`.
void rtti_plain_name(const char* mangled, char* out, size_t size);

// A virtual function of `object` by slot.
#define VIRTUAL(object, slot) ((*(void***)(object))[slot])

// The slot of function `fn` in `vtable`, searching the first `limit` slots, or -1.
int vtable_slot(void** vtable, void* fn, int limit);

// ---- Symbols ------------------------------------------------------------------------------------------------------------

// The export `name` of loaded module `module`, or null (logged).
void* sym(const wchar_t* module, const char* name);

// The plugin's log (KenshiHarness.log beside the DLL) and the text buffer replies are written into.
#pragma once
#include <stddef.h>

// Sets the log file to `name` beside module `self` and empties it.
void log_open(void* self, const wchar_t* name);

// Appends a line to the log.
void log_line(const char* format, ...);

// ---- A growing text buffer ----------------------------------------------------------------------------------------------

typedef struct
{
    char* text;
    size_t length;
    size_t capacity;
} Text;

// Empties the buffer, keeping its memory.
void text_clear(Text* t);

// Appends formatted text.
void text_add(Text* t, const char* format, ...);

// Appends `value` as a JSON string, quotes included.
void text_json(Text* t, const char* value);

// Appends `"key":` for a JSON object member, with the separating comma when it is not the first.
void text_key(Text* t, const char* key);

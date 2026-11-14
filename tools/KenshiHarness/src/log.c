#include "log.h"
#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- Log ----------------------------------------------------------------------------------------------------------------

static wchar_t logFile[MAX_PATH];
static CRITICAL_SECTION logLock;

void log_open(void* self, const wchar_t* name)
{
    InitializeCriticalSection(&logLock);
    GetModuleFileNameW((HMODULE)self, logFile, MAX_PATH);

    wchar_t* slash = wcsrchr(logFile, L'\\');
    wcscpy(slash ? slash + 1 : logFile, name);

    FILE* f = _wfopen(logFile, L"w");
    if (f) fclose(f);
}

void log_line(const char* format, ...)
{
    if (!logFile[0]) return;

    char line[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(line, sizeof line, format, args);
    va_end(args);

    EnterCriticalSection(&logLock);
    FILE* f = _wfopen(logFile, L"a");
    if (f)
    {
        fprintf(f, "%8.3f %s\n", GetTickCount64() / 1000.0, line);
        fclose(f);
    }
    LeaveCriticalSection(&logLock);
}

// ---- Text ---------------------------------------------------------------------------------------------------------------

static void text_reserve(Text* t, size_t more)
{
    if (t->length + more + 1 <= t->capacity) return;

    t->capacity = (t->length + more + 1) * 2 + 256;
    t->text = realloc(t->text, t->capacity);
}

void text_clear(Text* t)
{
    text_reserve(t, 0);
    t->length = 0;
    t->text[0] = 0;
}

void text_add(Text* t, const char* format, ...)
{
    va_list args;
    va_start(args, format);
    int needed = vsnprintf(NULL, 0, format, args);
    va_end(args);
    if (needed < 0) return;

    text_reserve(t, (size_t)needed);

    va_start(args, format);
    vsnprintf(t->text + t->length, (size_t)needed + 1, format, args);
    va_end(args);
    t->length += (size_t)needed;
}

void text_json(Text* t, const char* value)
{
    text_add(t, "\"");

    for (const unsigned char* c = (const unsigned char*)(value ? value : ""); *c; c++)
    {
        if (*c == '"' || *c == '\\') text_add(t, "\\%c", *c);
        else if (*c == '\n') text_add(t, "\\n");
        else if (*c < 0x20) text_add(t, "\\u%04x", *c);
        else text_add(t, "%c", *c);
    }

    text_add(t, "\"");
}

void text_key(Text* t, const char* key)
{
    char last = t->length ? t->text[t->length - 1] : '{';
    if (last != '{' && last != '[') text_add(t, ",");

    text_add(t, "\"%s\":", key);
}

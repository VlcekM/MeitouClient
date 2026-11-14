// KenshiHarness - an RE_Kenshi plugin that lets a program outside the game see and drive a running Kenshi: screenshots,
// the GUI as text, the world's state as JSON, and keys and mouse fed through the game's own input listener (so the window
// needs no focus and the real cursor is left alone). tools/scripts/KenshiHarness/README.md has the commands.
//
// How: each request on the named pipe waits its turn for the game thread, which runs it from an
// Ogre frame listener (frameRenderingQueued: the frame is drawn and not yet shown, so a screenshot is the frame in full).
// A handler that needs several frames (a click is a move, a press and a release) returns 0 and is called again next frame.
//
// Wire format, both ways little-endian: request = uint32 length, uint32 stall in ms (how long the game may render no frame
// before the request fails), then the arguments each ended by a zero byte; reply = uint32 length, one status byte (0 ok,
// 1 failed), then the text.

#include "harness.h"
#include "game.h"
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- Commands -----------------------------------------------------------------------------------------------------------

typedef struct { const char* name; Handler run; } Entry;

static const Entry COMMANDS[] = {
    {"shot", cmd_shot}, {"window", cmd_window}, {"ui", cmd_ui}, {"state", cmd_state}, {"load", cmd_load}, {"speed", cmd_speed}, {"wait", cmd_wait},
    {"camera", cmd_camera}, {"select", cmd_select}, {"mouse", cmd_mouse}, {"click", cmd_click}, {"drag", cmd_drag},
    {"scroll", cmd_scroll}, {"key", cmd_key}, {"type", cmd_type}, {"peek", cmd_peek}, {"sym", cmd_sym},
    {"fault", cmd_fault},
};

int fail(Command* c, const char* format, ...)
{
    char message[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(message, sizeof message, format, args);
    va_end(args);

    text_clear(&c->reply);
    text_add(&c->reply, "%s", message);
    c->failed = 1;
    return 1;
}

const char* arg(const Command* c, int index, const char* fallback)
{
    for (int i = 1; i < c->argc; i++)
    {
        if (strncmp(c->argv[i], "--", 2) == 0) continue;
        if (index-- == 0) return c->argv[i];
    }

    return fallback;
}

int option(const Command* c, const char* name)
{
    for (int i = 1; i < c->argc; i++)
    {
        if (strcmp(c->argv[i], name) == 0) return 1;
    }

    return 0;
}

static Handler find_handler(const char* name)
{
    for (size_t i = 0; i < sizeof COMMANDS / sizeof COMMANDS[0]; i++)
    {
        if (strcmp(COMMANDS[i].name, name) == 0) return COMMANDS[i].run;
    }

    return NULL;
}

// ---- The game thread: one command at a time, a step a frame -------------------------------------------------------------

static volatile LONG frames;
static volatile ULONGLONG lastFrameAt;
static Command* volatile queued;
static Command* running;
static Handler runningHandler;
static HANDLE finished;

long frame_number(void) { return frames; }

static void* fault_address(EXCEPTION_POINTERS* info) { return info->ExceptionRecord->ExceptionAddress; }

// A handler that follows a bad pointer raises an access violation, in its own code or in the game's below it: the
// exception is handled here (Windows unwinds the frames between), so the command fails and the game renders on.
static int run_step(Command* c, Handler run)
{
    if (c->cancelled) return fail(c, "cancelled (kh cancel, or the game stopped rendering frames)");

    void* where = NULL;
    int done = 1;

    __try
    {
        done = run(c);
    }
    __except (where = fault_address(GetExceptionInformation()),
              GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH)
    {
        log_line("%s faulted at %p", c->argv[0], where);
        return fail(c, "%s faulted at %p (bad pointer); the game carries on", c->argv[0], where);
    }

    c->step++;
    return done;
}

static void tick(void)
{
    InterlockedIncrement(&frames);
    lastFrameAt = GetTickCount64();

    if (!running)
    {
        running = InterlockedExchangePointer((void* volatile*)&queued, NULL);
        if (running) runningHandler = find_handler(running->argv[0]);
    }

    if (!running) return;
    if (!run_step(running, runningHandler)) return;

    running = NULL;
    SetEvent(finished);
}

// ---- The frame listener: Ogre's FrameListener with one slot of its vtable replaced --------------------------------------

#define LISTENER_SLOTS 6

typedef struct { void** vtable; } FrameListener;

static void* listenerVtable[LISTENER_SLOTS];
static FrameListener listener = {listenerVtable};

// bool FrameListener::frameRenderingQueued(const FrameEvent&): false would stop the game's render loop.
static char on_frame_queued(void* self, const void* event)
{
    (void)self;
    (void)event;

    tick();
    return 1;
}

static int install_listener(void)
{
    void** ogreVtable = sym(L"OgreMain_x64.dll", "??_7FrameListener@Ogre@@6B@");
    void* queuedFn = sym(L"OgreMain_x64.dll", "?frameRenderingQueued@FrameListener@Ogre@@UEAA_NAEBUFrameEvent@2@@Z");
    void* (*getRoot)(void) = sym(L"OgreMain_x64.dll", "?getSingletonPtr@Root@Ogre@@SAPEAV12@XZ");
    void (*addListener)(void*, void*) = sym(L"OgreMain_x64.dll", "?addFrameListener@Root@Ogre@@QEAAXPEAVFrameListener@2@@Z");
    if (!ogreVtable || !queuedFn || !getRoot || !addListener) return 0;

    int slot = vtable_slot(ogreVtable, queuedFn, LISTENER_SLOTS);
    void* root = getRoot();
    if (slot < 0 || !root)
    {
        log_line("no frame listener: slot %d, root %p", slot, root);
        return 0;
    }

    memcpy(listenerVtable, ogreVtable, sizeof listenerVtable);
    listenerVtable[slot] = on_frame_queued;
    addListener(root, &listener);
    return 1;
}

// ---- The pipe -----------------------------------------------------------------------------------------------------------

#define MAX_REQUEST (1 << 20)

static int read_all(HANDLE pipe, void* buffer, DWORD size)
{
    DWORD total = 0;
    DWORD got = 0;

    while (total < size)
    {
        if (!ReadFile(pipe, (char*)buffer + total, size - total, &got, NULL) || got == 0) return 0;
        total += got;
    }

    return 1;
}

static void write_reply(HANDLE pipe, const Command* c)
{
    DWORD length = (DWORD)c->reply.length + 1;
    char status = (char)c->failed;
    DWORD written;

    WriteFile(pipe, &length, 4, &written, NULL);
    WriteFile(pipe, &status, 1, &written, NULL);
    WriteFile(pipe, c->reply.text, (DWORD)c->reply.length, &written, NULL);
    FlushFileBuffers(pipe);
}

static void split_arguments(Command* c, char* payload, DWORD length)
{
    char* end = payload + length;

    for (char* at = payload; at < end && c->argc < MAX_ARGS; at += strlen(at) + 1)
    {
        c->argv[c->argc++] = at;
    }
}

static void answer_ping(Command* c)
{
    ULONGLONG last = lastFrameAt;
    long long age = last ? (long long)(GetTickCount64() - last) : -1;

    text_add(&c->reply, "{\"harness\":\"%s\",\"pid\":%lu,\"frame\":%ld,\"frameAgeMs\":%lld}", HARNESS_VERSION,
             GetCurrentProcessId(), frame_number(), age);
}

// One command at a time goes to the game thread (`turn`); `waiting` is the one that has it, for cancel to find.
static CRITICAL_SECTION turn;
static CRITICAL_SECTION waitingLock;
static Command* waiting;

static void set_waiting(Command* c)
{
    EnterCriticalSection(&waitingLock);
    waiting = c;
    LeaveCriticalSection(&waitingLock);
}

// cancel: ends the command the game thread is on (a long wait), which then fails; answered without the game thread.
static void answer_cancel(Command* c)
{
    EnterCriticalSection(&waitingLock);
    int found = waiting != NULL;
    if (found) InterlockedExchange(&waiting->cancelled, 1);
    LeaveCriticalSection(&waitingLock);

    text_add(&c->reply, "{\"cancelled\":%s}", found ? "true" : "false");
}

// Hands the command to the game thread and waits for it for as long as the game keeps rendering frames; a game that
// renders none for `stall` ms (loading, or hung) fails the command.
static void run_on_game_thread(Command* c, DWORD stall)
{
    ResetEvent(finished);
    InterlockedExchangePointer((void* volatile*)&queued, c);

    long seen = frames;
    ULONGLONG progressAt = GetTickCount64();

    while (WaitForSingleObject(finished, 100) != WAIT_OBJECT_0)
    {
        if (frames != seen)
        {
            seen = frames;
            progressAt = GetTickCount64();
        }

        if (GetTickCount64() - progressAt < stall) continue;

        // Not started: take it back. Started: it ends, cancelled, on the next frame the game renders.
        if (InterlockedCompareExchangePointer((void* volatile*)&queued, NULL, c) == c)
        {
            fail(c, "the game rendered no frame in %lu ms (loading, or not responding)", stall);
            return;
        }

        InterlockedExchange(&c->cancelled, 1);
        WaitForSingleObject(finished, INFINITE);
        return;
    }
}

static void take_turn(Command* c, DWORD stall)
{
    EnterCriticalSection(&turn);
    set_waiting(c);
    run_on_game_thread(c, stall);
    set_waiting(NULL);
    LeaveCriticalSection(&turn);
}

static void serve(HANDLE pipe)
{
    DWORD header[2];
    if (!read_all(pipe, header, sizeof header) || header[0] == 0 || header[0] > MAX_REQUEST) return;

    char* payload = calloc(header[0] + 1, 1);
    Command* c = calloc(1, sizeof *c);
    text_clear(&c->reply);

    if (read_all(pipe, payload, header[0])) split_arguments(c, payload, header[0]);

    if (c->argc == 0) fail(c, "empty request");
    else if (strcmp(c->argv[0], "ping") == 0) answer_ping(c);
    else if (strcmp(c->argv[0], "cancel") == 0) answer_cancel(c);
    else if (!find_handler(c->argv[0])) fail(c, "unknown command '%s'", c->argv[0]);
    else take_turn(c, header[1] ? header[1] : 30000);

    write_reply(pipe, c);
    free(c->reply.text);
    free(c);
    free(payload);
}

static DWORD WINAPI client_thread(LPVOID pipe)
{
    serve(pipe);
    DisconnectNamedPipe(pipe);
    CloseHandle(pipe);
    return 0;
}

// Each client gets a pipe instance and a thread, so ping, cancel and `kh stop` get through while a long command runs.
static DWORD WINAPI pipe_thread(LPVOID unused)
{
    (void)unused;

    for (;;)
    {
        HANDLE pipe = CreateNamedPipeW(HARNESS_PIPE, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_WAIT, PIPE_UNLIMITED_INSTANCES,
                                       1 << 16, 1 << 16, 0, NULL);
        if (pipe == INVALID_HANDLE_VALUE)
        {
            log_line("CreateNamedPipe failed: %lu", GetLastError());
            return 1;
        }

        if (ConnectNamedPipe(pipe, NULL) || GetLastError() == ERROR_PIPE_CONNECTED)
            CloseHandle(CreateThread(NULL, 0, client_thread, pipe, 0, NULL));
        else
            CloseHandle(pipe);
    }
}

// ---- Entry --------------------------------------------------------------------------------------------------------------

static HMODULE self;

// RE_Kenshi calls this once the game's data and its own plugins are loaded.
void startPlugin(void)
{
    log_open(self, L"KenshiHarness.log");
    log_line("KenshiHarness %s, thread %lu", HARNESS_VERSION, GetCurrentThreadId());

    finished = CreateEventW(NULL, TRUE, FALSE, NULL);
    InitializeCriticalSection(&turn);
    InitializeCriticalSection(&waitingLock);

    // A part whose exports are missing is logged and its commands fail; the rest still work.
    log_line("screen %d, ui %d, input %d, game %d, control %d", screen_init(), ui_init(), input_init(), game_init(), control_init());

    if (!install_listener()) return;

    CloseHandle(CreateThread(NULL, 0, pipe_thread, NULL, 0, NULL));
    log_line("listening on %ls", HARNESS_PIPE);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH) self = instance;
    return TRUE;
}

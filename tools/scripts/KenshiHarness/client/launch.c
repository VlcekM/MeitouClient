// Starting the game unattended: Kenshi opens with a launcher dialog (its OK button is pressed here with a window message),
// restarts itself as RE_Kenshi's copy of the executable, and reaches the main menu, where a save can be loaded.
#include "launch.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#define PIPE L"\\\\.\\pipe\\kenshi-harness"
#define GAME_EXE L"kenshi_x64.exe"

// ---- The game process ---------------------------------------------------------------------------------------------------

// The game's process id as the pipe's server, or 0 when no game with the plugin is running.
static DWORD game_process(void)
{
    HANDLE pipe = CreateFileW(PIPE, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (pipe == INVALID_HANDLE_VALUE) return 0;

    ULONG id = 0;
    GetNamedPipeServerProcessId(pipe, &id);
    CloseHandle(pipe);
    return id;
}

int stop_game(void)
{
    DWORD id = game_process();
    HANDLE process = id ? OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, id) : NULL;
    if (!process)
    {
        fprintf(stderr, "no Kenshi with the harness is running\n");
        return 2;
    }

    TerminateProcess(process, 0);
    WaitForSingleObject(process, 10000);
    CloseHandle(process);

    printf("{\"stopped\":%lu}\n", id);
    return 0;
}

// The Kenshi folder: kh.exe is installed in <Kenshi>\mods\KenshiHarness.
static int game_folder(wchar_t folder[MAX_PATH])
{
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(NULL, folder, MAX_PATH);

    for (int i = 0; i < 3; i++)
    {
        wchar_t* slash = wcsrchr(folder, L'\\');
        if (!slash) return 0;
        *slash = 0;
    }

    swprintf(exe, MAX_PATH, L"%ls\\%ls", folder, GAME_EXE);
    return GetFileAttributesW(exe) != INVALID_FILE_ATTRIBUTES;
}

static int launch(const wchar_t* folder)
{
    wchar_t command[MAX_PATH + 8];
    STARTUPINFOW startup = {.cb = sizeof startup};
    PROCESS_INFORMATION process;
    swprintf(command, MAX_PATH + 8, L"\"%ls\\%ls\"", folder, GAME_EXE);

    if (!CreateProcessW(NULL, command, NULL, NULL, FALSE, 0, NULL, folder, &startup, &process)) return 0;

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 1;
}

// ---- The launcher dialog ------------------------------------------------------------------------------------------------

static int is_game_window(HWND window)
{
    wchar_t path[MAX_PATH] = L"";
    DWORD id = 0;
    DWORD size = MAX_PATH;
    GetWindowThreadProcessId(window, &id);

    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, id);
    if (!process) return 0;

    QueryFullProcessImageNameW(process, 0, path, &size);
    CloseHandle(process);

    const wchar_t* name = wcsrchr(path, L'\\');
    return name && _wcsicmp(name + 1, GAME_EXE) == 0;
}

static BOOL CALLBACK press_ok(HWND child, LPARAM pressed)
{
    wchar_t text[16] = L"";
    GetWindowTextW(child, text, 16);
    if (wcscmp(text, L"OK") != 0) return TRUE;

    PostMessageW(child, BM_CLICK, 0, 0);
    *(int*)pressed = 1;
    return FALSE;
}

static BOOL CALLBACK find_launcher(HWND window, LPARAM pressed)
{
    wchar_t class[16] = L"";
    GetClassNameW(window, class, 16);

    if (wcscmp(class, L"#32770") == 0 && IsWindowVisible(window) && is_game_window(window))
        EnumChildWindows(window, press_ok, pressed);

    return TRUE;
}

// ---- Waiting for the game -----------------------------------------------------------------------------------------------

// Sends a command and tells whether it succeeded with `expected` somewhere in its reply.
static int answers(const char* command, const char* argument, const char* expected)
{
    char* arguments[2] = {(char*)command, (char*)argument};
    char* reply;
    int ok = request(argument ? 2 : 1, arguments, 2000, &reply) == 0 && reply && strstr(reply, expected);

    free(reply);
    return ok;
}

// Presses the launcher's OK whenever the dialog is up, until the plugin answers.
static int wait_for_plugin(int seconds)
{
    ULONGLONG until = GetTickCount64() + (ULONGLONG)seconds * 1000;

    while (GetTickCount64() < until)
    {
        int pressed = 0;
        if (answers("ping", NULL, "harness")) return 1;

        EnumWindows(find_launcher, (LPARAM)&pressed);
        Sleep(500);
    }

    return 0;
}

static int wait_until(const char* command, const char* argument, const char* expected, int seconds)
{
    ULONGLONG until = GetTickCount64() + (ULONGLONG)seconds * 1000;

    while (GetTickCount64() < until)
    {
        if (answers(command, argument, expected)) return 1;
        Sleep(500);
    }

    return 0;
}

// The main menu, or a game already in play (start on a running game only loads the save).
static int wait_for_menu(int seconds)
{
    ULONGLONG until = GetTickCount64() + (ULONGLONG)seconds * 1000;

    while (GetTickCount64() < until)
    {
        if (answers("ui", "NewGameButton", "NewGameButton") || answers("state", "--brief", "\"squad\":[{")) return 1;
        Sleep(500);
    }

    return 0;
}

int load_save(const char* save)
{
    char* arguments[2] = {"load", (char*)save};
    char* reply;
    int status = request(2, arguments, 30000, &reply);
    if (status && reply) fprintf(stderr, "%s\n", reply);

    free(reply);
    if (status) return status;

    // The squad empties while the save loads (and the game renders no frame for part of it), then fills again.
    ULONGLONG emptyBy = GetTickCount64() + 8000;
    while (GetTickCount64() < emptyBy && answers("state", "--brief", "\"squad\":[{")) Sleep(200);

    if (!wait_until("state", "--brief", "\"squad\":[{", 600))
    {
        fprintf(stderr, "the save '%s' did not load (no player characters after 10 minutes)\n", save);
        return 1;
    }

    char* state[2] = {"state", "--brief"};
    status = request(2, state, 30000, &reply);
    if (reply) puts(reply);

    free(reply);
    return status;
}

// Starts the game unless one with the plugin runs already: 1 when this call started it, 0 when it ran, -1 (having said
// why) when it could not be started.
static int launch_if_needed(void)
{
    wchar_t folder[MAX_PATH];
    if (game_process()) return 0;

    if (!game_folder(folder))
    {
        fprintf(stderr, "run the kh.exe installed in <Kenshi>\\mods\\KenshiHarness: this one is not inside a Kenshi folder\n");
        return -1;
    }

    if (!launch(folder))
    {
        fprintf(stderr, "could not start %ls\\%ls (error %lu)\n", folder, GAME_EXE, GetLastError());
        return -1;
    }

    return 1;
}

// The game takes the keyboard focus when its window opens: the plugin gives it back to the window that had it.
static void return_focus(HWND before)
{
    char handle[32];
    char* arguments[3] = {"window", "back", handle};
    char* reply;
    snprintf(handle, sizeof handle, "%llx", (unsigned long long)(uintptr_t)before);

    request(3, arguments, 5000, &reply);
    free(reply);
}

int start_game(const char* save)
{
    HWND before = GetForegroundWindow();
    int launched = launch_if_needed();
    if (launched < 0) return 2;

    if (!wait_for_plugin(180))
    {
        fprintf(stderr, "Kenshi did not load the harness plugin within 3 minutes (is KenshiHarness.mod enabled?)\n");
        return 2;
    }

    if (!wait_for_menu(300))
    {
        fprintf(stderr, "Kenshi did not reach its main menu within 5 minutes\n");
        return 1;
    }

    if (launched) return_focus(before);
    if (save) return load_save(save);

    printf("{\"started\":%lu}\n", game_process());
    return 0;
}

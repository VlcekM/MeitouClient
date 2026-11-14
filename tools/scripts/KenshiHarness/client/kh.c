// kh - the command line of KenshiHarness: sends one command to the plugin inside a running Kenshi and prints the reply.
//
//   kh <command> [arguments]     README.md lists the commands; exit code 0 ok, 1 the command failed, 2 no game to talk to
//   kh start [save]              starts Kenshi (the install this kh.exe sits in: <Kenshi>\mods\KenshiHarness), waits for the
//                                main menu and loads the save
//   kh load <save>               loads a save and waits until it is in play
//   kh stop                      ends the game process
//   --stall=<seconds>            how long the game may render no frame before a command fails (default 30)
//
// The wire format is in src/harness.c.

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "launch.h"

#define PIPE L"\\\\.\\pipe\\kenshi-harness"

// ---- One request --------------------------------------------------------------------------------------------------------

static HANDLE open_pipe(DWORD waitMs)
{
    ULONGLONG until = GetTickCount64() + waitMs;

    for (;;)
    {
        HANDLE pipe = CreateFileW(PIPE, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (pipe != INVALID_HANDLE_VALUE) return pipe;

        // Busy: another request is being served. Anything else: no game, or it has not loaded the plugin yet.
        if (GetTickCount64() >= until) return INVALID_HANDLE_VALUE;
        if (GetLastError() == ERROR_PIPE_BUSY) WaitNamedPipeW(PIPE, 500);
        else Sleep(200);
    }
}

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

int request(int count, char** arguments, DWORD stallMs, char** reply)
{
    *reply = NULL;

    HANDLE pipe = open_pipe(3000);
    if (pipe == INVALID_HANDLE_VALUE) return 2;

    DWORD header[2] = {0, stallMs};
    for (int i = 0; i < count; i++) header[0] += (DWORD)strlen(arguments[i]) + 1;

    DWORD done;
    WriteFile(pipe, header, sizeof header, &done, NULL);
    for (int i = 0; i < count; i++) WriteFile(pipe, arguments[i], (DWORD)strlen(arguments[i]) + 1, &done, NULL);

    DWORD length = 0;
    char status = 1;
    int answered = read_all(pipe, &length, 4) && length >= 1 && read_all(pipe, &status, 1);

    if (answered)
    {
        *reply = calloc(length, 1);
        answered = read_all(pipe, *reply, length - 1);
    }

    CloseHandle(pipe);
    return answered ? status : 2;
}

// ---- The command line ---------------------------------------------------------------------------------------------------

static void print_reply(const char* reply, int status)
{
    if (!reply) return;

    FILE* to = status ? stderr : stdout;
    size_t length = strlen(reply);

    fputs(reply, to);
    if (length && reply[length - 1] != '\n') fputc('\n', to);
}

static int run_command(int count, char** arguments, DWORD stallMs)
{
    char* reply;
    int status = request(count, arguments, stallMs, &reply);

    if (status == 2) fprintf(stderr, "no Kenshi with the harness is running (kh start)\n");
    print_reply(reply, status);
    free(reply);
    return status;
}

// A screenshot's file is made a full path here: the game's working folder is not the caller's.
static void full_path(char** argument)
{
    static char path[MAX_PATH];

    if (GetFullPathNameA(*argument, MAX_PATH, path, NULL)) *argument = path;
}

int main(int argc, char** argv)
{
    char* arguments[64];
    int count = 0;
    DWORD stallMs = 30000;

    for (int i = 1; i < argc && count < 64; i++)
    {
        if (strncmp(argv[i], "--stall=", 8) == 0) stallMs = (DWORD)(atof(argv[i] + 8) * 1000);
        else arguments[count++] = argv[i];
    }

    if (count == 0)
    {
        fprintf(stderr, "usage: kh <command> [arguments]; kh start [save]; kh stop (see README.md)\n");
        return 2;
    }

    if (strcmp(arguments[0], "start") == 0) return start_game(count > 1 ? arguments[1] : NULL);
    if (strcmp(arguments[0], "stop") == 0) return stop_game();
    if (strcmp(arguments[0], "load") == 0 && count > 1) return load_save(arguments[1]);
    if (strcmp(arguments[0], "shot") == 0 && count > 1) full_path(&arguments[1]);

    return run_command(count, arguments, stallMs);
}

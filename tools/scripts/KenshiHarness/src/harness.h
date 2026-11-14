// What the plugin's parts share: a command as it travels from the pipe to the game thread, and each part's entry points.
#pragma once
#include "abi.h"
#include "log.h"

#define HARNESS_VERSION "1"
#define HARNESS_PIPE L"\\\\.\\pipe\\kenshi-harness"
#define MAX_ARGS 32

// One request: its arguments, the reply being written and what its handler remembers between frames.
typedef struct
{
    int argc;
    char* argv[MAX_ARGS];
    Text reply;
    int failed;
    int step;
    volatile LONG cancelled;
    intptr_t memo[6];
    double number;
} Command;

// A command's handler, run on the game thread once a frame until it returns 1.
typedef int (*Handler)(Command* c);

// Replaces the reply with an error message and ends the command: `return fail(c, ...)`.
int fail(Command* c, const char* format, ...);

// Argument `index` after the command's name (0 is the first), or `fallback` when it is absent. Options (--name) do not count.
const char* arg(const Command* c, int index, const char* fallback);

// Whether option `name` ("--all") was given.
int option(const Command* c, const char* name);

// The number of frames the game has rendered since the plugin loaded.
long frame_number(void);

// ---- screen.c: the render window ----------------------------------------------------------------------------------------

int screen_init(void);
int screen_size(int* width, int* height);
int cmd_shot(Command* c);
int cmd_window(Command* c);

// ---- ui.c: the game's MyGUI widgets -------------------------------------------------------------------------------------

int ui_init(void);
int cmd_ui(Command* c);

// The centre of the one visible widget whose caption or name matches `text`; 0 with the reason in `why` otherwise.
int ui_locate(const char* text, int nth, int* x, int* y, Text* why);

// ---- input.c: keys and the mouse, through the game's own input listener -------------------------------------------------

int input_init(void);
int cmd_mouse(Command* c);
int cmd_click(Command* c);
int cmd_drag(Command* c);
int cmd_scroll(Command* c);
int cmd_key(Command* c);
int cmd_type(Command* c);

// ---- state.c: the game's world as JSON ----------------------------------------------------------------------------------

int cmd_state(Command* c);

// ---- control.c: the save, the speed, the camera and the selection -------------------------------------------------------

int control_init(void);
char world_is_paused(void);
float world_speed(void);
void world_save_name(char* out, size_t size);
int cmd_load(Command* c);
int cmd_speed(Command* c);
int cmd_wait(Command* c);
int cmd_camera(Command* c);
int cmd_select(Command* c);

// ---- dev.c: tools for working on the harness itself ---------------------------------------------------------------------

int cmd_peek(Command* c);
int cmd_sym(Command* c);
int cmd_fault(Command* c);

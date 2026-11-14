// Starting and stopping the game for kh.
#pragma once
#include <windows.h>

// Sends a command to the plugin; returns 0 ok, 1 failed, 2 no game, with the reply's text in `reply` (to free) or null.
int request(int count, char** arguments, DWORD stallMs, char** reply);

// Starts Kenshi, waits for the main menu and, with a save, for the save to be loaded. Returns kh's exit code.
int start_game(const char* save);

// Loads a save and waits until its characters are in the world, then prints the state. Returns kh's exit code.
int load_save(const char* save);

// Ends the game process the plugin runs in. Returns kh's exit code.
int stop_game(void);

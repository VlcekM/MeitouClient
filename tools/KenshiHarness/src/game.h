// The game's objects as the harness reads them, through KenshiLib's exports: KenshiLib.dll exports the game's functions
// and globals by their C++ names for whichever game version is running, so nothing here is an address. Member offsets are
// KenshiLib's (Include/kenshi/*.h, game 1.0.65), each checked against the running game with `kh peek`.
#pragma once
#include "abi.h"

// Resolves the exports; 0 when one is missing (logged).
int game_init(void);

// The GameWorld (KenshiLib `ou`) and its PlayerInterface, or null before the game has made them.
void* game_world(void);
void* game_player(void);

// The player's characters.
const Lektor* game_squad(void);

// The characters within `radius` of `centre`, at most `limit`; the list is the harness's own and is reused by the next call.
const Lektor* game_characters_near(V3 centre, float radius, int limit);

// The character of that name (case ignored): the player's first, then anyone within `radius` of the camera's centre.
void* game_find_character(const char* name, float radius);

// The game clock in hours since the game began.
double game_hours(void);

// The camera rig (CameraClass), where it stands and what it looks at.
void* game_camera(void);
V3 game_camera_position(void);
V3 game_camera_centre(void);

// Where a world position is in the window; 0 when it is behind the camera or outside the window.
int game_project(V3 position, int* x, int* y);

// ---- A character --------------------------------------------------------------------------------------------------------

void character_name(void* character, char* out, size_t size);
void character_faction(void* character, char* out, size_t size);
V3 character_position(void* character);
int character_is_players(void* character);
int character_is_selected(void* character);

// The character's CharStats and MedicalSystem.
char* character_stats(void* character);
char* character_medical(void* character);

// A GameData record's name.
void record_name(const void* record, char* out, size_t size);

// The game's controls that are quicker called than clicked, so a test can set a situation up the same way every time:
// loading a save, the speed, the camera, the selection. Through KenshiLib's exports, as game.c.
#include "harness.h"
#include "game.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// How far from the camera's centre a character is looked for by name.
#define NAME_RADIUS 3000

static char (*isPaused)(const void* world);
static float (*getSpeed)(const void* world);
static void (*setSpeed)(void* world, float speed, char click);
static void (*userPause)(void* world, char paused);
static void* (*getSaves)(void);
static const GameString* (*getSavePath)(const void* saves);
static void (*loadSave)(void* saves, const GameString* name);
static const GameString* (*getCurrentSave)(void* saves);
static void (*teleportCamera)(void* rig, const V3* position);
static void (*selectObject)(void* player, void* object, char add);
static void (*unselectAll)(void* player);

int control_init(void)
{
    const wchar_t* lib = L"KenshiLib.dll";

    isPaused = sym(lib, "?isPaused@GameWorld@@QEBA_NXZ");
    getSpeed = sym(lib, "?getFrameSpeedMultiplier@GameWorld@@QEBAMXZ");
    setSpeed = sym(lib, "?setGameSpeed@GameWorld@@QEAAXM_N@Z");
    userPause = sym(lib, "?userPause@GameWorld@@QEAAX_N@Z");
    getSaves = sym(lib, "?getSingleton@SaveManager@@SAPEAV1@XZ");
    getSavePath = sym(lib, "?getSavePath@SaveManager@@QEBAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ");
    loadSave = sym(lib, "?load@SaveManager@@QEAAXAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@Z");
    getCurrentSave = sym(lib, "?getCurrentGame@SaveManager@@QEAAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ");
    teleportCamera = sym(lib, "?teleport@CameraClass@@QEAAXAEBVVector3@Ogre@@@Z");
    selectObject = sym(lib, "?selectObject@PlayerInterface@@QEAAXPEAVRootObject@@_N@Z");
    unselectAll = sym(lib, "?unselectAll@PlayerInterface@@QEAAXXZ");

    return isPaused && getSpeed && setSpeed && userPause && getSaves && getSavePath && loadSave && getCurrentSave && teleportCamera
        && selectObject && unselectAll;
}

// ---- What state.c reports -----------------------------------------------------------------------------------------------

char world_is_paused(void) { return isPaused(game_world()); }

float world_speed(void) { return getSpeed(game_world()); }

void world_save_name(char* out, size_t size)
{
    const GameString* name = getCurrentSave(getSaves());
    snprintf(out, size, "%.*s", (int)name->size, gs_text(name));
}

// ---- Commands -----------------------------------------------------------------------------------------------------------

// Whether the game's save path has a folder of that name; the path is left in `folder` for the error message.
static int save_exists(const char* name, char* folder, size_t size)
{
    const GameString* path = getSavePath(getSaves());
    char full[MAX_PATH];
    snprintf(folder, size, "%.*s", (int)path->size, gs_text(path));
    snprintf(full, sizeof full, "%s\\%s", folder, name);

    DWORD attributes = GetFileAttributesA(full);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY);
}

// load <save>: loads the save of that name (a folder under the game's save path), as the load window would.
int cmd_load(Command* c)
{
    const char* name = arg(c, 0, NULL);
    char folder[MAX_PATH];
    if (!name) return fail(c, "usage: load <save name>");
    if (!save_exists(name, folder, sizeof folder)) return fail(c, "no save named '%s' in %s", name, folder);

    GameString save = gs_view(name);
    loadSave(getSaves(), &save);

    text_add(&c->reply, "{\"loading\":");
    text_json(&c->reply, name);
    text_add(&c->reply, "}");
    return 1;
}

// speed <n>: 0 pauses; anything else unpauses and sets the game's speed multiplier (the game's buttons are 1, 2 and 5).
int cmd_speed(Command* c)
{
    void* world = game_world();
    const char* text = arg(c, 0, NULL);
    if (!world || !text) return fail(c, world ? "usage: speed <multiplier, 0 to pause>" : "the game world is not up yet");

    float speed = (float)atof(text);
    userPause(world, speed <= 0);
    if (speed > 0) setSpeed(world, speed, 0);

    text_add(&c->reply, "{\"paused\":%s,\"speed\":%.2f}", isPaused(world) ? "true" : "false", getSpeed(world));
    return 1;
}

static int reply_clock(Command* c)
{
    text_add(&c->reply, "{\"frame\":%ld,\"hours\":%.4f}", frame_number(), game_hours());
    return 1;
}

// wait <seconds> | wait frames <n> | wait hours <game hours>: returns after that long, so a script can let the game run
// between two looks. Game hours only pass while the game is unpaused, so that wait fails when it is paused.
int cmd_wait(Command* c)
{
    const char* first = arg(c, 0, NULL);
    if (!first) return fail(c, "usage: wait <seconds> | wait frames <n> | wait hours <game hours>");

    if (strcmp(first, "frames") == 0) return c->step < atoi(arg(c, 1, "1")) ? 0 : reply_clock(c);

    if (strcmp(first, "hours") == 0)
    {
        if (!game_world() || world_is_paused()) return fail(c, "the game is paused: no game hours pass (kh speed 1)");
        if (c->step == 0) c->number = game_hours() + atof(arg(c, 1, "0"));

        return game_hours() < c->number ? 0 : reply_clock(c);
    }

    if (c->step == 0) c->memo[0] = (intptr_t)GetTickCount64();
    return (double)(GetTickCount64() - (ULONGLONG)c->memo[0]) < atof(first) * 1000 ? 0 : reply_clock(c);
}

static int is_number(const char* text)
{
    char* end;
    if (!text || !*text) return 0;

    strtod(text, &end);
    return *end == 0;
}

// camera <x> <y> <z> | camera <character>: puts the camera's centre at a world position, or where a character stands,
// keeping its angle and zoom.
int cmd_camera(Command* c)
{
    void* rig = game_camera();
    const char* first = arg(c, 0, NULL);
    if (!rig || !first) return fail(c, rig ? "usage: camera <x> <y> <z> | camera <character>" : "the game world is not up yet");

    if (is_number(first) && is_number(arg(c, 1, NULL)) && is_number(arg(c, 2, NULL)))
    {
        V3 position = {(float)atof(first), (float)atof(arg(c, 1, "0")), (float)atof(arg(c, 2, "0"))};
        teleportCamera(rig, &position);
    }
    else
    {
        void* character = game_find_character(first, NAME_RADIUS);
        if (!character) return fail(c, "no character named '%s' in the squad or near the camera", first);

        V3 position = character_position(character);
        teleportCamera(rig, &position);
    }

    V3 centre = game_camera_centre();
    text_add(&c->reply, "{\"cameraCentre\":[%.2f,%.2f,%.2f]}", centre.x, centre.y, centre.z);
    return 1;
}

// select <character>... | select none: the selection, as clicking portraits would make it.
int cmd_select(Command* c)
{
    void* player = game_player();
    if (!player || !arg(c, 0, NULL)) return fail(c, player ? "usage: select <character>... | select none" : "the game world is not up yet");

    unselectAll(player);
    if (_stricmp(arg(c, 0, ""), "none") == 0) return reply_clock(c);

    for (int i = 0; arg(c, i, NULL); i++)
    {
        void* character = game_find_character(arg(c, i, ""), NAME_RADIUS);
        if (!character) return fail(c, "no character named '%s' in the squad or near the camera", arg(c, i, ""));

        selectObject(player, character, i > 0);
    }

    return reply_clock(c);
}

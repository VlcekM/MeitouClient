#include "game.h"
#include "harness.h"
#include <stdio.h>
#include <string.h>

// ---- Layout (KenshiLib headers, 1.0.65) ---------------------------------------------------------------------------------

#define WORLD_PLAYER 0x580
#define PLAYER_CAMERA 0x30
#define RIG_OGRE_CAMERA 0x68
#define CHARACTER_STATS 0x450
#define CHARACTER_MEDICAL 0x458
#define RECORD_NAME 0x28

// itemType CHARACTER.
#define TYPE_CHARACTER 1

// RootObjectBase's virtual slots.
enum { OBJECT_NAME = 1, OBJECT_POSITION = 8, OBJECT_FACTION = 11 };

// ---- Exports ------------------------------------------------------------------------------------------------------------

typedef struct { float m[4][4]; } Matrix4;
typedef struct { double hours; } TimeOfDay;

static void*** worldVariable;
static TimeOfDay* (*getHours)(void* world, TimeOfDay* out);
static V3* (*getCameraPos)(const void* world, V3* out);
static V3* (*getCameraCentre)(const void* world, V3* out);
static void (*getObjectsNear)(void* world, Lektor* results, const V3* centre, float radius, int type, int limit, void* skip);
static const Lektor* (*getSquad)(const void* player);
static char (*isSelected)(void* player, void* object);
static char (*isPlayers)(const void* character);
static const GameString* (*getFactionName)(void* faction);
static const Matrix4* (*getViewMatrix)(const void* camera, char ownFrustumOnly);
static const Matrix4* (*getProjectionMatrix)(const void* camera);
static void* (*getSceneManager)(const void* camera);
static V3* (*getRelativeOrigin)(const void* sceneManager, V3* out);

int game_init(void)
{
    const wchar_t* lib = L"KenshiLib.dll";
    const wchar_t* ogre = L"OgreMain_x64.dll";

    worldVariable = sym(lib, "?ou@@3PEAVGameWorld@@EA");
    getHours = sym(lib, "?getTimeStamp_inGameHours@GameWorld@@QEAA?AVTimeOfDay@@XZ");
    getCameraPos = sym(lib, "?getCameraPos@GameWorld@@QEBA?BVVector3@Ogre@@XZ");
    getCameraCentre = sym(lib, "?getCameraCenter@GameWorld@@QEBA?BVVector3@Ogre@@XZ");
    getObjectsNear = sym(lib, "?getObjectsWithinSphere@GameWorld@@QEAAXAEAV?$lektor@PEAVRootObject@@@@AEBVVector3@Ogre@@"
                              "MW4itemType@@HPEAVRootObject@@@Z");
    getSquad = sym(lib, "?getAllPlayerCharacters@PlayerInterface@@QEBAAEBV?$lektor@PEAVCharacter@@@@XZ");
    isSelected = sym(lib, "?isObjectSelected@PlayerInterface@@QEAA_NPEAVRootObject@@@Z");
    isPlayers = sym(lib, "?isPlayerCharacter@Character@@QEBA_NXZ");
    getFactionName = sym(lib, "?getName@Faction@@QEAAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ");
    getViewMatrix = sym(ogre, "?getViewMatrix@Camera@Ogre@@QEBAAEBVMatrix4@2@_N@Z");
    getProjectionMatrix = sym(ogre, "?getProjectionMatrix@Frustum@Ogre@@UEBAAEBVMatrix4@2@XZ");
    getSceneManager = sym(ogre, "?getSceneManager@Camera@Ogre@@QEBAPEAVSceneManager@2@XZ");
    getRelativeOrigin = sym(ogre, "?getRelativeOrigin@SceneManager@Ogre@@QEBA?AVVector3@2@XZ");

    return worldVariable && getHours && getCameraPos && getCameraCentre && getObjectsNear && getSquad && isSelected && isPlayers
        && getFactionName && getViewMatrix && getProjectionMatrix && getSceneManager && getRelativeOrigin;
}

// ---- The world ----------------------------------------------------------------------------------------------------------

void* game_world(void) { return worldVariable ? *worldVariable : NULL; }

void* game_player(void)
{
    void* world = game_world();
    return world ? *(void**)((char*)world + WORLD_PLAYER) : NULL;
}

const Lektor* game_squad(void)
{
    void* player = game_player();
    return player ? getSquad(player) : NULL;
}

// A lektor the game fills needs a lektor's vtable; the squad's list lends its own (the lists differ only in what the
// pointers point at). The game grows the list as it needs and the harness keeps it for the next call.
const Lektor* game_characters_near(V3 centre, float radius, int limit)
{
    static Lektor found;
    const Lektor* squad = game_squad();
    if (!squad) return NULL;

    found.unused = squad->unused;
    found.count = 0;
    getObjectsNear(game_world(), &found, &centre, radius, TYPE_CHARACTER, limit, NULL);
    return &found;
}

static void* find_in(const Lektor* list, const char* name)
{
    char text[128];

    for (uint32_t i = 0; list && i < list->count; i++)
    {
        character_name(list->items[i], text, sizeof text);
        if (_stricmp(text, name) == 0) return list->items[i];
    }

    return NULL;
}

void* game_find_character(const char* name, float radius)
{
    void* found = find_in(game_squad(), name);
    if (found || !game_world()) return found;

    return find_in(game_characters_near(game_camera_centre(), radius, 512), name);
}

double game_hours(void)
{
    TimeOfDay time = {0};
    void* world = game_world();
    return world ? getHours(world, &time)->hours : 0;
}

// ---- The camera ---------------------------------------------------------------------------------------------------------

void* game_camera(void)
{
    void* player = game_player();
    return player ? *(void**)((char*)player + PLAYER_CAMERA) : NULL;
}

V3 game_camera_position(void)
{
    V3 at = {0, 0, 0};
    return game_world() ? *getCameraPos(game_world(), &at) : at;
}

V3 game_camera_centre(void)
{
    V3 at = {0, 0, 0};
    return game_world() ? *getCameraCentre(game_world(), &at) : at;
}

static void transform(const Matrix4* m, const float in[4], float out[4])
{
    for (int row = 0; row < 4; row++)
    {
        out[row] = m->m[row][0] * in[0] + m->m[row][1] * in[1] + m->m[row][2] * in[2] + m->m[row][3] * in[3];
    }
}

// Kenshi's Ogre keeps the scene relative to a moving origin, so a world position goes to the view matrix less that origin.
int game_project(V3 position, int* x, int* y)
{
    void* rig = game_camera();
    void* camera = rig ? *(void**)((char*)rig + RIG_OGRE_CAMERA) : NULL;
    int width, height;
    if (!camera || !screen_size(&width, &height)) return 0;

    V3 origin;
    getRelativeOrigin(getSceneManager(camera), &origin);

    float world[4] = {position.x - origin.x, position.y - origin.y, position.z - origin.z, 1};
    float eye[4], clip[4];
    transform(getViewMatrix(camera, 1), world, eye);
    transform(getProjectionMatrix(camera), eye, clip);
    if (clip[3] <= 0) return 0;

    *x = (int)((clip[0] / clip[3] * 0.5f + 0.5f) * (float)width);
    *y = (int)((0.5f - clip[1] / clip[3] * 0.5f) * (float)height);
    return *x >= 0 && *y >= 0 && *x < width && *y < height;
}

// ---- A character --------------------------------------------------------------------------------------------------------

static void copy_string(const GameString* s, char* out, size_t size)
{
    snprintf(out, size, "%.*s", (int)s->size, gs_text(s));
}

void character_name(void* character, char* out, size_t size)
{
    GameString name;
    void (*getName)(void*, GameString*) = VIRTUAL(character, OBJECT_NAME);

    getName(character, &name);
    copy_string(&name, out, size);
    gs_free(&name);
}

void character_faction(void* character, char* out, size_t size)
{
    void* (*getFaction)(void*) = VIRTUAL(character, OBJECT_FACTION);
    void* faction = getFaction(character);

    out[0] = 0;
    if (faction) copy_string(getFactionName(faction), out, size);
}

V3 character_position(void* character)
{
    V3 position;
    void (*getPosition)(void*, V3*) = VIRTUAL(character, OBJECT_POSITION);

    getPosition(character, &position);
    return position;
}

int character_is_players(void* character) { return isPlayers(character); }

int character_is_selected(void* character) { return isSelected(game_player(), character); }

char* character_stats(void* character) { return *(char**)((char*)character + CHARACTER_STATS); }

char* character_medical(void* character) { return (char*)character + CHARACTER_MEDICAL; }

void record_name(const void* record, char* out, size_t size)
{
    out[0] = 0;
    if (record) copy_string((const GameString*)((const char*)record + RECORD_NAME), out, size);
}

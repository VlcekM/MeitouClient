// state: the game's world as JSON, for comparing a situation with the same one in MeitouClient.
#include "harness.h"
#include "game.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- Layout (KenshiLib MedicalSystem.h and CharStats.h, 1.0.65) ---------------------------------------------------------

#define MEDICAL_HUNGER 0x60
#define MEDICAL_BLOOD 0x70
#define MEDICAL_UNCONSCIOUS 0x161
#define MEDICAL_DEAD 0x164
#define MEDICAL_ANATOMY 0x190
#define PART_RECORD 0x00
#define PART_FLESH 0x40
#define PART_STUN 0x44
#define PART_MAX 0x54

// How far above a character's position (its feet) its `screen` point is taken: about the chest of a human, so a click
// there lands on the body.
#define BODY_HEIGHT 10

typedef struct { const char* name; int offset; } Stat;

static const Stat STATS[] = {
    {"strength", 0x80}, {"dexterity", 0x88}, {"perception", 0x8C}, {"toughness", 0x90}, {"athletics", 0x94}, {"medic", 0x98},
    {"stealth", 0xA4}, {"swimming", 0xA8}, {"thieving", 0xAC}, {"lockpicking", 0xB0}, {"assassin", 0xB8}, {"doctor", 0xC8},
    {"engineer", 0xCC}, {"weaponSmith", 0xD0}, {"armourSmith", 0xD4}, {"bowSmith", 0xD8}, {"robotics", 0xDC}, {"science", 0xE0},
    {"labouring", 0xE4}, {"farming", 0xE8}, {"cooking", 0xEC}, {"dodging", 0xF0}, {"friendlyFire", 0xF4}, {"katanas", 0xF8},
    {"sabres", 0xFC}, {"hackers", 0x100}, {"blunt", 0x104}, {"heavyWeapons", 0x108}, {"unarmed", 0x10C}, {"bows", 0x110},
    {"turrets", 0x114}, {"polearms", 0x118}, {"meleeAttack", 0x120}, {"meleeDefence", 0x124},
};

// ---- JSON pieces --------------------------------------------------------------------------------------------------------

static void add_text(Text* out, const char* key, const char* value)
{
    text_key(out, key);
    text_json(out, value);
}

static void add_flag(Text* out, const char* key, int value)
{
    text_key(out, key);
    text_add(out, value ? "true" : "false");
}

static void add_number(Text* out, const char* key, double value)
{
    text_key(out, key);
    text_add(out, "%.2f", value);
}

static void add_vector(Text* out, const char* key, V3 v)
{
    text_key(out, key);
    text_add(out, "[%.2f,%.2f,%.2f]", v.x, v.y, v.z);
}

// ---- A character --------------------------------------------------------------------------------------------------------

// "parts": each body part's health as [now, stun damage, full].
static void add_parts(Text* out, const char* medical)
{
    const Lektor* anatomy = (const Lektor*)(medical + MEDICAL_ANATOMY);
    char name[64];

    text_key(out, "parts");
    text_add(out, "{");

    for (uint32_t i = 0; i < anatomy->count && i < 32; i++)
    {
        const char* part = anatomy->items[i];
        record_name(*(void**)(part + PART_RECORD), name, sizeof name);

        text_key(out, name);
        text_add(out, "[%.2f,%.2f,%.2f]", *(float*)(part + PART_FLESH), *(float*)(part + PART_STUN), *(float*)(part + PART_MAX));
    }

    text_add(out, "}");
}

static void add_stats(Text* out, const char* stats)
{
    text_key(out, "stats");
    text_add(out, "{");

    for (size_t i = 0; i < sizeof STATS / sizeof STATS[0]; i++)
    {
        add_number(out, STATS[i].name, *(float*)(stats + STATS[i].offset));
    }

    text_add(out, "}");
}

static void add_character(Text* out, void* character, int detailed)
{
    char name[128], faction[128];
    int x, y;
    V3 position = character_position(character);
    const char* medical = character_medical(character);

    character_name(character, name, sizeof name);
    character_faction(character, faction, sizeof faction);

    text_add(out, "{");
    add_text(out, "name", name);
    add_text(out, "faction", faction);
    add_vector(out, "pos", position);

    V3 body = {position.x, position.y + BODY_HEIGHT, position.z};
    if (game_project(body, &x, &y))
    {
        text_key(out, "screen");
        text_add(out, "[%d,%d]", x, y);
    }

    add_flag(out, "player", character_is_players(character));
    add_flag(out, "selected", character_is_selected(character));
    add_flag(out, "dead", medical[MEDICAL_DEAD]);
    add_flag(out, "unconscious", medical[MEDICAL_UNCONSCIOUS]);
    add_number(out, "blood", *(float*)(medical + MEDICAL_BLOOD));
    add_number(out, "hunger", *(float*)(medical + MEDICAL_HUNGER));

    if (detailed)
    {
        add_parts(out, medical);
        if (character_stats(character)) add_stats(out, character_stats(character));
    }

    text_add(out, "}");
}

static void add_characters(Text* out, const char* key, const Lektor* list, int detailed, int playersToo)
{
    int written = 0;

    text_key(out, key);
    text_add(out, "[");

    for (uint32_t i = 0; list && i < list->count; i++)
    {
        if (!playersToo && character_is_players(list->items[i])) continue;
        if (written++) text_add(out, ",");

        add_character(out, list->items[i], detailed);
    }

    text_add(out, "]");
}

// ---- The command --------------------------------------------------------------------------------------------------------

static float option_radius(const Command* c)
{
    for (int i = 1; i < c->argc; i++)
    {
        if (strncmp(c->argv[i], "--near=", 7) == 0) return (float)atof(c->argv[i] + 7);
    }

    return 0;
}

static void add_clock(Text* out)
{
    double hours = game_hours();
    int minutes = (int)(hours * 60) % (24 * 60);

    text_add(out, "\"frame\":%ld,\"hours\":%.4f,\"day\":%d,\"clock\":\"%02d:%02d\"", frame_number(), hours, (int)(hours / 24),
             minutes / 60, minutes % 60);
}

// state [--near=<radius>] [--brief]: the clock, the speed, the camera and the player's characters with their health and
// stats; with --near also everyone else within that distance of the point the camera looks at. `screen` is where a
// character's body is in the window (a point to click), absent when it is out of view. --brief leaves out body parts and stats.
int cmd_state(Command* c)
{
    const Lektor* squad = game_squad();
    if (!squad) return fail(c, "the game world is not up yet");

    // No player characters: the main menu (whose world has no clock yet) or a save still loading.
    if (squad->count == 0)
    {
        text_add(&c->reply, "{\"frame\":%ld,\"inGame\":false,\"squad\":[]}", frame_number());
        return 1;
    }

    Text* out = &c->reply;
    char save[128];
    float radius = option_radius(c);
    int detailed = !option(c, "--brief");
    world_save_name(save, sizeof save);

    text_add(out, "{");
    add_clock(out);
    add_flag(out, "inGame", 1);
    add_flag(out, "paused", world_is_paused());
    add_number(out, "speed", world_speed());
    add_text(out, "save", save);
    add_vector(out, "camera", game_camera_position());
    add_vector(out, "cameraCentre", game_camera_centre());
    add_characters(out, "squad", squad, detailed, 1);
    if (radius > 0) add_characters(out, "near", game_characters_near(game_camera_centre(), radius, 256), detailed, 0);
    text_add(out, "}");
    return 1;
}

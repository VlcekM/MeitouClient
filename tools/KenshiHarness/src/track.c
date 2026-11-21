// track: what named characters' AI does, frame by frame, recorded while other commands drive the game. Each frame the
// tracker reads every tracked character and writes a line when its reading changes: the game's clock, the current goal, the
// body's task, the order queue's front and length, the default task and its target, the manoeuvre mode, whether a stumble
// clip plays, and how fast it moves. `track dump` hands the lines over.
#include "harness.h"
#include "game.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- Layout (KenshiLib AI.h, AITaskSystem.h, Tasker.h, 1.0.65) ----------------------------------------------------------

// AI::taskSystemAI, AI::ordersMovement (the manoeuvre mode), AI::centerOfMovement and AI::centerOfMovementTarget.
#define AI_TASK_SYSTEM 0x20
#define AI_MANOEUVRE 0x2BC
#define AI_CENTRE 0x2C0
#define AI_CENTRE_TARGET 0x2D0
// Character's SensoryData (its vfunc 0x68): the threats list (lektor<hand> of the characters whose intended target is the looker,
// hands 0x20 bytes each) and their aggression sum, both refilled by each AI update's sense pass (addToThreatsAndFlockingList).
#define CHARACTER_SENSES_SLOT (0x68 / 8)
#define SENSES_THREATS 0x50
#define SENSES_THREAT_SUM 0x88
#define HAND_SIZE 0x20
// Character's AI status, what others' sense passes read of it: the intended target (a hand), the running task's intended
// aggression and the current goal's (written by the AI's state broadcast).
#define CHARACTER_STATUS 0x1A0
#define STATUS_TARGET 0x68
#define STATUS_TASK_AGGRESSION 0xA0
#define STATUS_GOAL_AGGRESSION 0xAC
// AnimationData::dataName: the ANIMATION record's name of a clip.
#define ANIMATION_NAME 0x08
// OrdersReceiver::orders' std::deque (VC++ 2010: proxy, map, map size, offset, size; two pointers a block),
// OrdersReceiver::holdPositionOrders (the default task) and OrdersReceiver::currentGoal (a TaskMatch, its TaskData first).
#define ORDERS_DEQUE 0x40
#define ORDERS_DEFAULT 0x190
#define ORDERS_GOAL 0x1C0
// Tasker::subject and Tasker::taskData; TaskData::key.
#define TASKER_SUBJECT 0x10
#define TASKER_DATA 0x70
#define TASKDATA_KEY 0x44

#define MEDICAL_UNCONSCIOUS 0x161
#define MEDICAL_DEAD 0x164
#define MEDICAL_ANATOMY 0x190
#define PART_FLESH 0x40
#define PART_STUN 0x44
#define PART_MAX 0x54

#define MAX_TRACKED 8
#define MAX_LOG (4 << 20)

typedef struct
{
    void* character;
    char name[64];
    char last[512];
} Tracked;

static Tracked s_tracked[MAX_TRACKED];
static int s_count;
static Text s_log;

static void* (*getAI)(void* character);
static void* (*getBody)(void* character);
static void* (*getMovement)(void* character);
static void* (*getAnimation)(void* character);
static void* (*getCurrentAction)(void* body);
static void* (*getStumble)(const void* animation);
static float (*getSpeed)(const void* movement);
static void* (*handCharacter)(const void* hand);
static double (*getStamp)(void* world);
static GameString* (*getGoalString)(void* taskSystem, GameString* out);

int track_init(void)
{
    const wchar_t* lib = L"KenshiLib.dll";

    getAI = sym(lib, "?getAI@Character@@QEAAPEAVAI@@XZ");
    getBody = sym(lib, "?getBody@Character@@QEAAPEAVCharBody@@XZ");
    getMovement = sym(lib, "?getMovement@Character@@QEAAPEAVCharMovement@@XZ");
    getAnimation = sym(lib, "?getAnimationClass@Character@@QEAAPEAVAnimationClass@@XZ");
    getCurrentAction = sym(lib, "?getCurrentAction@CharBody@@QEAAPEAVTasker@@XZ");
    getStumble = sym(lib, "?getCurrentlyStumbleAnim@AnimationClass@@QEBAPEAVAnimationData@@XZ");
    getSpeed = sym(lib, "?getCurrentSpeed@AbstractMovementBase@@QEBAMXZ");
    handCharacter = sym(lib, "?getCharacter@hand@@QEBAPEAVCharacter@@XZ");
    getStamp = sym(lib, "?getTimeStamp@GameWorld@@QEAANXZ");
    getGoalString = sym(lib, "?getCurrentGoalString@AITaskSytem@@QEAA?AV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ");

    return getAI && getBody && getMovement && getAnimation && getCurrentAction && getStumble && getSpeed && handCharacter && getStamp
        && getGoalString;
}

// ---- Reading a character ------------------------------------------------------------------------------------------------

// A task's type (its TaskData's key), or -1 for none.
static int task_type(const char* tasker)
{
    if (!tasker) return -1;

    const char* data = *(const char* const*)(tasker + TASKER_DATA);
    return data ? *(const int*)(data + TASKDATA_KEY) : -1;
}

// The character a hand names, by name, or "-" for none.
static void hand_name(const void* hand, char* out, size_t size)
{
    void* character = handCharacter(hand);
    snprintf(out, size, "-");
    if (character) character_name(character, out, size);
}

// The damage the body parts have taken, flesh and stun together (MedicalSystem's anatomy as state.c reads it): it changes
// with every blow that lands.
static float damage_taken(const char* medical)
{
    const Lektor* anatomy = (const Lektor*)(medical + MEDICAL_ANATOMY);
    float damage = 0;
    for (uint32_t i = 0; i < anatomy->count && i < 32; i++)
    {
        const char* part = anatomy->items[i];
        damage += *(const float*)(part + PART_MAX) - *(const float*)(part + PART_FLESH) + *(const float*)(part + PART_STUN);
    }

    return damage;
}

// The order queue's front task and length.
static int queue_front(const char* taskSystem, int* length)
{
    const char* deque = taskSystem + ORDERS_DEQUE;
    char*** map = *(char****)(deque + 0x08);
    size_t mapSize = *(const size_t*)(deque + 0x10);
    size_t offset = *(const size_t*)(deque + 0x18);
    *length = (int)*(const size_t*)(deque + 0x20);
    if (*length == 0 || !map || mapSize == 0) return -1;

    const char* front = map[(offset / 2) % mapSize][offset % 2];
    return task_type(front);
}

static void read_character(void* character, char* out, size_t size)
{
    char* ai = getAI(character);
    char* taskSystem = ai ? *(char**)(ai + AI_TASK_SYSTEM) : NULL;
    if (!taskSystem)
    {
        snprintf(out, size, "no AI");
        return;
    }

    const char* goalData = *(const char* const*)(taskSystem + ORDERS_GOAL);
    int goal = goalData ? *(const int*)(goalData + TASKDATA_KEY) : -1;
    int length;
    int front = queue_front(taskSystem, &length);
    int action = task_type(getCurrentAction(getBody(character)));

    const char* standing = *(const char* const*)(taskSystem + ORDERS_DEFAULT);
    char standingTarget[64] = "-";
    if (standing) hand_name(standing + TASKER_SUBJECT, standingTarget, sizeof standingTarget);

    char centreTarget[64];
    hand_name(ai + AI_CENTRE_TARGET, centreTarget, sizeof centreTarget);

    GameString text;
    getGoalString(taskSystem, &text);
    char goalText[96];
    snprintf(goalText, sizeof goalText, "%.*s", (int)text.size, gs_text(&text));
    gs_free(&text);

    const char* medical = character_medical(character);
    const V3* centre = (const V3*)(ai + AI_CENTRE);
    float speed = getSpeed(getMovement(character));
    void* stumble = getStumble(getAnimation(character));
    char clip[64] = "-";
    const GameString* clipName = stumble ? (const GameString*)((char*)stumble + ANIMATION_NAME) : NULL;
    if (clipName) snprintf(clip, sizeof clip, "%.*s", (int)clipName->size, gs_text(clipName));

    typedef char* (*SensesGetter)(void* self);
    SensesGetter getSenses = (*(SensesGetter**)character)[CHARACTER_SENSES_SLOT];
    const char* senses = getSenses(character);
    const Lektor* threats = (const Lektor*)(senses + SENSES_THREATS);
    char threat[64] = "-";
    if (threats->count > 0 && threats->count < 64) hand_name((const char*)threats->items, threat, sizeof threat);
    const char* status = *(const char* const*)((const char*)character + CHARACTER_STATUS);
    char aim[64];
    hand_name(status + STATUS_TARGET, aim, sizeof aim);

    snprintf(out, size, "goal=%d \"%s\" act=%d front=%d/%d default=%d:%s mode=%d centre=%.0f,%.0f:%s stumble=%s moving=%d damage=%.0f%s%s"
             " aim=%s aggr=%.2f/%.2f threats=%u:%s/%.2f",
             goal, goalText, action, front, length, task_type(standing), standingTarget, *(const int*)(ai + AI_MANOEUVRE), centre->x,
             centre->z, centreTarget, clip, speed > 5, damage_taken(medical), medical[MEDICAL_UNCONSCIOUS] ? " KO" : "",
             medical[MEDICAL_DEAD] ? " DEAD" : "", aim, *(const float*)(status + STATUS_TASK_AGGRESSION),
             *(const float*)(status + STATUS_GOAL_AGGRESSION), threats->count, threat, *(const float*)(senses + SENSES_THREAT_SUM));
}

// ---- Each frame ---------------------------------------------------------------------------------------------------------

static void sample(Tracked* t, double stamp)
{
    char now[512];
    read_character(t->character, now, sizeof now);
    if (strcmp(now, t->last) == 0) return;

    strcpy(t->last, now);
    V3 at = character_position(t->character);
    float speed = getSpeed(getMovement(t->character));
    if (s_log.length < MAX_LOG)
        text_add(&s_log, "%.3f h%.6f f%ld %s %s speed=%.1f at %.0f,%.0f\n", stamp, game_hours(), frame_number(), t->name, now, speed, at.x,
                 at.z);
}

void track_reset(void) { s_count = 0; }

void track_frame(void)
{
    if (s_count == 0 || !game_world()) return;

    double stamp = getStamp(game_world());
    for (int i = 0; i < s_count; i++)
    {
        __try
        {
            sample(&s_tracked[i], stamp);
        }
        __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH)
        {
            text_add(&s_log, "%.3f f%ld %s gone (bad pointer); no longer tracked\n", stamp, frame_number(), s_tracked[i].name);
            s_tracked[i] = s_tracked[--s_count];
            i--;
        }
    }
}

// ---- The command --------------------------------------------------------------------------------------------------------

// track <character>... | track dump | track off: starts recording those characters (replacing the last set), hands over and
// clears what was recorded, or stops.
int cmd_track(Command* c)
{
    const char* first = arg(c, 0, NULL);
    if (!first) return fail(c, "usage: track <character>... | track dump | track off");

    if (strcmp(first, "dump") == 0)
    {
        if (s_log.length) text_add(&c->reply, "%.*s", (int)s_log.length, s_log.text);
        text_clear(&s_log);
        return 1;
    }

    s_count = 0;
    text_clear(&s_log);
    if (strcmp(first, "off") == 0) return 1;

    for (int i = 0; arg(c, i, NULL) && s_count < MAX_TRACKED; i++)
    {
        // "@1A2B3C": a character by its address (`state --addr`), for one of several with the same name.
        const char* which = arg(c, i, "");
        void* character = which[0] == '@' ? (void*)(uintptr_t)strtoull(which + 1, NULL, 16) : game_find_character(which, 3000);
        if (!character) return fail(c, "no character named '%s' in the squad or near the camera", which);

        Tracked* t = &s_tracked[s_count++];
        t->character = character;
        t->last[0] = 0;
        snprintf(t->name, sizeof t->name, "%s", arg(c, i, ""));
    }

    text_add(&c->reply, "{\"tracking\":%d}", s_count);
    return 1;
}

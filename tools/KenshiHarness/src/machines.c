// machines: the buildings an NPC's machine finders choose from (AI::getAllLocalMachines, the gatherer every building finder
// starts with), each with what those finders test: its function, completeness, users against capacity, the path-impossible
// map, and which one AI::_findOwnedCageThing (the corpse disposal and cage finder) returns.
#include "harness.h"
#include "game.h"
#include <stdlib.h>
#include <string.h>

// The building's function (BuildingFunction), its complete flag and its "a place free" test, as vtable slots (byte offsets).
#define SLOT_FUNCTION 0x2f0
#define SLOT_COMPLETE 0x228
#define SLOT_BLOCKED 0x4e8

// UseableStuff's users (the count of its user list) and its capacity (max operators), KenshiLib's UseableStuff layout.
#define USERS_COUNT 0x3e0
#define USERS_CAPACITY 0x3ac

// A building's own hand, the one the finders' claim and path tests take; the AI's pointer to its AITaskSytem.
#define BUILDING_HAND 0x58
#define AI_TASKS 0x20

static void* (*getAI)(void* character);
static void (*getAllLocalMachines)(void* ai, Lektor* results, unsigned int mask);
static void* (*findOwnedCageThing)(void* ai, char userTest, int function);
static char (*isPathImpossible)(void* tasks, const void* hand);

// Resolves the exports on first use; 0 when one is missing.
static int resolve(void)
{
    if (getAI && getAllLocalMachines && findOwnedCageThing && isPathImpossible) return 1;

    const wchar_t* lib = L"KenshiLib.dll";
    getAI = sym(lib, "?getAI@Character@@QEAAPEAVAI@@XZ");
    getAllLocalMachines = sym(lib, "?getAllLocalMachines@AI@@QEAAXAEAV?$lektor@PEAVBuilding@@@@I@Z");
    findOwnedCageThing = sym(lib, "?_findOwnedCageThing@AI@@QEAAPEAVUseableStuff@@_NW4BuildingFunction@@@Z");
    isPathImpossible = sym(lib, "?isPathImpossible@AITaskSytem@@QEAA_NAEBVhand@@@Z");
    return getAI && getAllLocalMachines && findOwnedCageThing && isPathImpossible;
}

// A virtual function of `object` at byte offset `slot`.
static void* slot(void* object, size_t offset)
{
    return (*(void***)object)[offset / sizeof(void*)];
}

// One candidate's line: address, name, place, function, complete, users / capacity, blocked, path impossible.
static void describe(Command* c, void* ai, void* building, void* chosen)
{
    char name[128];
    character_name(building, name, sizeof name);
    V3 at = character_position(building);
    int function = ((int (*)(void*))slot(building, SLOT_FUNCTION))(building);
    const char* complete = ((const char* (*)(void*))slot(building, SLOT_COMPLETE))(building);
    char blocked = ((char (*)(void*))slot(building, SLOT_BLOCKED))(building);
    uint64_t users = *(uint64_t*)((char*)building + USERS_COUNT);
    int capacity = *(int*)((char*)building + USERS_CAPACITY);
    char impossible = isPathImpossible(*(void**)((char*)ai + AI_TASKS), (char*)building + BUILDING_HAND);

    text_add(&c->reply, "@%p %s at %.0f, %.0f, %.0f function %d complete %d users %llu/%d blocked %d path-impossible %d%s\n", building, name,
        at.x, at.y, at.z, function, complete ? *complete : -1, (unsigned long long)users, capacity, blocked, impossible,
        building == chosen ? " CHOSEN" : "");
}

// machines <character> [function] [mask]: the character's machine candidates (all functions without `function`), and the
// building _findOwnedCageThing(true, function) picks for it when `function` is given.
int cmd_machines(Command* c)
{
    const char* who = arg(c, 0, NULL);
    if (!who) return fail(c, "usage: machines <character|@address> [function] [mask]");
    if (!resolve()) return fail(c, "KenshiLib lacks an export the command needs");

    void* character = who[0] == '@' ? (void*)(uintptr_t)strtoull(who + 1, NULL, 16) : game_find_character(who, 3000);
    void* ai = character ? getAI(character) : NULL;
    if (!ai) return fail(c, "no character %s with an AI", who);

    const char* only = arg(c, 1, NULL);
    int function = only ? atoi(only) : -1;
    unsigned int mask = (unsigned int)strtoul(arg(c, 2, "0"), NULL, 0);
    void* chosen = function >= 0 ? findOwnedCageThing(ai, 1, function) : NULL;

    const Lektor* squad = game_squad();
    if (!squad) return fail(c, "not in a game");

    // Room enough that the game never grows the list (it would free a buffer it did not allocate).
    static void* items[4096];
    Lektor found = {squad->unused, 0, 4096, items};
    getAllLocalMachines(ai, &found, mask);
    text_add(&c->reply, "%u candidate(s); chosen @%p\n", found.count, chosen);

    for (uint32_t i = 0; i < found.count; i++)
    {
        void* building = found.items[i];
        int its = ((int (*)(void*))slot(building, SLOT_FUNCTION))(building);
        if (function < 0 || its == function) describe(c, ai, building, chosen);
    }

    return 1;
}

// ---- finders: the corpse and disposal finders' answers for one character -------------------------------------------------

// A hand (the game's object handle) as the finders take and fill it: room for KenshiLib's layout.
typedef struct { _Alignas(16) unsigned char bytes[64]; } Hand;

static void (*handInit)(Hand* h);
static void* (*handCharacter)(const Hand* h);
static float (*findCorpseCages)(void* ai, const Hand* target, Hand* found, char flag);
static float (*findCorpseGround)(void* ai, const Hand* target, Hand* found, char a, char b, char c);
static float (*findDisposal)(void* ai, const Hand* target, Hand* found, char flag);

// Resolves the finders' exports on first use; 0 when one is missing.
static int resolve_finders(void)
{
    if (handInit && handCharacter && findCorpseCages && findCorpseGround && findDisposal) return 1;

    const wchar_t* lib = L"KenshiLib.dll";
    handInit = sym(lib, "??0hand@@QEAA@XZ");
    handCharacter = sym(lib, "?getCharacter@hand@@QEBAPEAVCharacter@@XZ");
    findCorpseCages = sym(lib, "?findCorpse_includingCages@AI@@QEAAMAEBVhand@@AEAV2@_N@Z");
    findCorpseGround = sym(lib, "?findCorpse_onGround_detail@AI@@QEAAMAEBVhand@@AEAV2@_N22@Z");
    findDisposal = sym(lib, "?findCorpseDisposal@AI@@QEAAMAEBVhand@@AEAV2@_N@Z");
    return handInit && handCharacter && findCorpseCages && findCorpseGround && findDisposal;
}

// One finder's answer: its factor and the character its hand names (none for a building or nothing).
static void report(Command* c, const char* what, float factor, const Hand* found)
{
    char name[128] = "-";
    void* character = handCharacter(found);
    if (character) character_name(character, name, sizeof name);

    text_add(&c->reply, "%s: %.3f, character %s @%p\n", what, factor, name, character);
}

// finders <character|@address>: findCorpse_includingCages (138's finder), findCorpse_onGround_detail as it calls it, and
// findCorpseDisposal (137's finder), each with an empty target as the planner gives them.
int cmd_finders(Command* c)
{
    const char* who = arg(c, 0, NULL);
    if (!who) return fail(c, "usage: finders <character|@address>");
    if (!resolve() || !resolve_finders()) return fail(c, "KenshiLib lacks an export the command needs");

    void* character = who[0] == '@' ? (void*)(uintptr_t)strtoull(who + 1, NULL, 16) : game_find_character(who, 3000);
    void* ai = character ? getAI(character) : NULL;
    if (!ai) return fail(c, "no character %s with an AI", who);

    Hand none, found;
    handInit(&none);
    handInit(&found);
    report(c, "corpse (cages, then ground)", findCorpseCages(ai, &none, &found, 0), &found);

    handInit(&found);
    report(c, "corpse on the ground", findCorpseGround(ai, &none, &found, 0, 1, 0), &found);

    handInit(&found);
    report(c, "disposal", findDisposal(ai, &none, &found, 0), &found);
    return 1;
}

// ---- goals: the goals a character's AI weighs ------------------------------------------------------------------------------

// The AI's OrdersReceiver, its five goal lists by priority (lektor<Tasker*> each, 0x18 apart), a Tasker's task data, weight and
// the task data's TaskType (OrdersReceiver::addGoal, FUN_140507950 in 1.0.68).
#define AI_ORDERS 0x20
#define GOAL_LISTS 0xa0
#define GOAL_LIST_SIZE 0x18
#define TASKER_DATA 0x70
#define TASKER_WEIGHT 0x30
#define TASK_TYPE 0x44

// The AITaskSytem's member type (SQUAD_1 0, SQUAD_2 1, SQUAD_LEADER 2, SIGNALS 3, SLAVE 4): which package role list it weighs.
#define MEMBER_TYPE 0x25c

// The squad Blackboard's role lists (Squad, Squad2, Leader; lektor<Tasker*> each), docs/game/ai.md "Goal loading".
static const int ROLE_LISTS[] = {0x1b0, 0x1c8, 0x198};
static const char* const ROLE_NAMES[] = {"squad", "squad2", "leader"};
static void* (*getBlackboard)(void* ai);


// goals <character|@address>: each registered goal's priority, TaskType and weight.
int cmd_goals(Command* c)
{
    const char* who = arg(c, 0, NULL);
    if (!who) return fail(c, "usage: goals <character|@address>");
    if (!resolve()) return fail(c, "KenshiLib lacks an export the command needs");

    void* character = who[0] == '@' ? (void*)(uintptr_t)strtoull(who + 1, NULL, 16) : game_find_character(who, 3000);
    char* ai = character ? getAI(character) : NULL;
    if (!ai) return fail(c, "no character %s with an AI", who);

    char* orders = *(char**)(ai + AI_ORDERS);
    text_add(&c->reply, "member type %d\n", *(int*)(orders + MEMBER_TYPE));
    if (!getBlackboard) getBlackboard = sym(L"KenshiLib.dll", "?getBlackboard@AI@@QEAAPEAVBlackboard@@XZ");
    char* board = getBlackboard ? getBlackboard(ai) : NULL;
    for (int role = 0; board && role < 3; role++)
    {
        const Lektor* list = (const Lektor*)(board + ROLE_LISTS[role]);
        text_add(&c->reply, "%s list @%p: count %u items @%p\n", ROLE_NAMES[role], (void*)list, list->count, (void*)list->items);
    }

    for (int priority = 0; priority < 5; priority++)
    {
        const Lektor* list = (const Lektor*)(orders + GOAL_LISTS + priority * GOAL_LIST_SIZE);
        for (uint32_t i = 0; i < list->count; i++)
        {
            char* tasker = list->items[i];
            char* data = *(char**)(tasker + TASKER_DATA);
            int type = data ? *(int*)(data + TASK_TYPE) : -1;
            text_add(&c->reply, "priority %d type %d weight %.3f\n", priority, type, *(float*)(tasker + TASKER_WEIGHT));
        }
    }

    return 1;
}

// ---- plans: the planner's value for each squad goal ------------------------------------------------------------------------

// The tier pass's role-list Taskers, one lektor<Tasker*> per priority (AITaskSytem +0x118 + priority x 0x18, FUN_140510d60).
#define TIER_LISTS 0x118

static float (*runGOAP)(void* tasks, void* tasker, char flagA, char flagB);
static float (*taskerScore)(void* tasker, void* ai);
static char (*resultsComplete)(void* data, void* ai, const void* target, const void* second, const void* position);
static char (*subTaskerComplete)(void* tasker, void* ai);

// A Tasker's target hand, second hand and position, as the scorer passes them to the drop tests (FUN_14050df10).
#define TASKER_TARGET 0x10
#define TASKER_SECOND 0x38
#define TASKER_POSITION 0x58
#define TASKER_FINISHED 0x20

// TaskData's crowd factor: the score is multiplied by it once per squad mate on the same goal and target.
#define TASK_CROWD 0x34

// plans <character|@address> [priority] [--plan] [--as=@address]: --as evaluates the tests for that other character (its AI and
// body) on the first character's Taskers. each role-list Tasker of that tier (default 2) with its Tasker::score (desire
// with its effect's factor), weight and crowd factor, and with --plan the value AITaskSytem::runGOAP plans it at (0: the plan
// fails). The lists hold what the last choice gathered.
int cmd_plans(Command* c)
{
    const char* who = arg(c, 0, NULL);
    if (!who) return fail(c, "usage: plans <character|@address> [priority]");
    if (!resolve()) return fail(c, "KenshiLib lacks an export the command needs");
    if (!runGOAP) runGOAP = sym(L"KenshiLib.dll", "?runGOAP@AITaskSytem@@QEAAMPEAVTasker@@_N1@Z");
    if (!taskerScore) taskerScore = sym(L"KenshiLib.dll", "?score@Tasker@@QEAAMPEAVAI@@@Z");
    if (!resultsComplete) resultsComplete = sym(L"KenshiLib.dll", "?isResultsComplete@TaskData@@QEAA_NPEAVAI@@AEBVhand@@1AEBVVector3@Ogre@@@Z");
    if (!subTaskerComplete) subTaskerComplete = sym(L"KenshiLib.dll", "?isSubTaskerComplete@Tasker@@QEAA_NPEAVAI@@@Z");
    if (!runGOAP || !taskerScore || !resultsComplete || !subTaskerComplete) return fail(c, "KenshiLib lacks an export plans needs");

    void* character = who[0] == '@' ? (void*)(uintptr_t)strtoull(who + 1, NULL, 16) : game_find_character(who, 3000);
    char* ai = character ? getAI(character) : NULL;
    if (!ai) return fail(c, "no character %s with an AI", who);

    int priority = atoi(arg(c, 1, "2"));
    char* tasks = *(char**)(ai + AI_ORDERS);
    for (int k = 0; k < c->argc; k++)
    {
        if (strncmp(c->argv[k], "--as=@", 6) != 0) continue;

        character = (void*)(uintptr_t)strtoull(c->argv[k] + 6, NULL, 16);
        ai = getAI(character);
        if (!ai) return fail(c, "no AI for %s", c->argv[k]);
    }

    const Lektor* list = (const Lektor*)(tasks + TIER_LISTS + priority * GOAL_LIST_SIZE);
    for (uint32_t i = 0; i < list->count; i++)
    {
        char* tasker = list->items[i];
        char* data = *(char**)(tasker + TASKER_DATA);
        int type = data ? *(int*)(data + TASK_TYPE) : -1;
        float crowd = data ? *(float*)(data + TASK_CROWD) : -1;
        float value = option(c, "--plan") ? runGOAP(tasks, tasker, 0, 0) : -1;
        char finished = ((char (*)(void*, void*))slot(tasker, TASKER_FINISHED))(tasker, character);
        char results = data ? resultsComplete(data, ai, tasker + TASKER_TARGET, tasker + TASKER_SECOND, tasker + TASKER_POSITION) : -1;
        char sub = subTaskerComplete(tasker, ai);
        text_add(&c->reply, "type %d score %.3f weight %.3f crowd %.3f plan %.3f finished %d results %d sub %d\n", type,
            taskerScore(tasker, ai), *(float*)(tasker + TASKER_WEIGHT), crowd, value, finished, results, sub);
    }

    return 1;
}

// ---- scores: the chooser's last candidate map ------------------------------------------------------------------------------

// The candidate map (std::map<float, Tasker*>, AITaskSytem +0x2c0, FUN_140510d60) as VC++ 2010 lays it out: the head node after
// the allocator, the size after it; a node's left, parent and right links, the key and the Tasker, then its colour and nil bytes.
#define CANDIDATES 0x2c0
#define MAP_HEAD 0x8
#define NODE_LEFT 0x0
#define NODE_RIGHT 0x10
#define NODE_NIL 0x29
#define NODE_KEY 0x18
#define NODE_VALUE 0x20

// The nodes under `node` in key order, at most `budget` lines and `depth` levels.
static void walk(Command* c, char* node, int* budget, int depth)
{
    if (*budget <= 0 || depth <= 0 || !readable(node, NODE_NIL + 1) || node[NODE_NIL]) return;

    walk(c, *(char**)(node + NODE_LEFT), budget, depth - 1);
    if (*budget <= 0) return;

    char* tasker = *(char**)(node + NODE_VALUE);
    char* data = tasker && readable(tasker, TASKER_DATA + 8) ? *(char**)(tasker + TASKER_DATA) : NULL;
    int type = data && readable(data, TASK_TYPE + 4) ? *(int*)(data + TASK_TYPE) : -1;
    text_add(&c->reply, "score %.4f type %d\n", *(float*)(node + NODE_KEY), type);
    (*budget)--;

    walk(c, *(char**)(node + NODE_RIGHT), budget, depth - 1);
}

// scores <character|@address>: the candidates of the chooser's last scored tier, key and TaskType, lowest first.
int cmd_scores(Command* c)
{
    const char* who = arg(c, 0, NULL);
    if (!who) return fail(c, "usage: scores <character|@address>");
    if (!resolve()) return fail(c, "KenshiLib lacks an export the command needs");

    void* character = who[0] == '@' ? (void*)(uintptr_t)strtoull(who + 1, NULL, 16) : game_find_character(who, 3000);
    char* ai = character ? getAI(character) : NULL;
    if (!ai) return fail(c, "no character %s with an AI", who);

    char* map = *(char**)(ai + AI_ORDERS) + CANDIDATES;
    char* head = *(char**)(map + MAP_HEAD);
    if (!readable(head, NODE_NIL + 1)) return fail(c, "the candidate map's head is not readable");

    text_add(&c->reply, "%llu candidate(s)\n", (unsigned long long)*(uint64_t*)(map + MAP_HEAD + 8));
    int budget = 64;
    walk(c, *(char**)(head + 8), &budget, 32);
    return 1;
}

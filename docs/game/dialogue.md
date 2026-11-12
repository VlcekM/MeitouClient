# Dialogue

How the original picks, says and acts on dialogue. Sources: the FCS editor's enums (decompile, outside the repo), the install's
`fcs.def` / `fcs_enums.def` / `data/gui/layout`, the kenshi_x64.exe decompile (functions cited), and a probe over the merged base
load order (`gamedata.base`, `Newwworld.mod`, `Dialogue.mod`, `rebirth.mod`; 2026-10-17). Claims are **Observed** (decompile reading)
unless marked. Engine: [Meitou design](#meitou-design).

## Data

**Verified** by probe: 293 DIALOGUE_PACKAGE, 1890 DIALOGUE, 22783 DIALOGUE_LINE, 21135 DIALOG_ACTION, 289 WORD_SWAPS records.

### Who has which dialogue

`FUN_14062b210` (character setup) and `FUN_1407ff8b0` (player characters):

| Character | Packages |
|---|---|
| any | CHARACTER `dialogue` (161 uses): each DIALOGUE goes straight to event 1 (fcs.def says the same, **Verified** text) |
| NPC squad leader (member 0) | SQUAD_TEMPLATE `dialog leader` (157), else CHARACTER `dialogue package` (793), else `dialog squad` (**Observed**, decompile) |
| other NPC members (and a leader with neither) | SQUAD_TEMPLATE `dialog squad` (216), else CHARACTER `dialogue package` |
| animal | SQUAD_TEMPLATE `dialog animal` (4) |
| player character, not an animal | CHARACTER `dialogue package player` (179), else the player faction's FACTION `dialog default` (25 records have one), else the package `5369-gamedata.base` ("player character default", **Verified** by probe) |

No NPC faction or race package is read. Other users of DIALOGUE (**Verified** counts): BIOME_GROUP `arrival dialog` 194,
FACTION_CAMPAIGN `dialog announcement` 121, AI_PACKAGE `contract end talk passive` 26, SINGLE_DIPLOMATIC_ASSAULT 13.

### DIALOGUE_PACKAGE

- `inheritsFrom` (457 refs) merges first, recursively (`FUN_14067fab0`, **Verified**: no visited check, so a loop would never end; Meitou keeps one).
- `dialogs`: each of the reference's val0, val1, val2 that is non-zero and not 100 is an `EventTriggerEnum` the dialogue answers
  (**Verified** by probe: 2586 refs, 127 with val1 and 30 with val2 set, all valid event numbers; 17 have val0 0).
- A package or DIALOGUE whose name contains `Unfinished` is skipped (`FUN_14067e220`; 9 packages, 24 dialogues). An Unfinished package is also not in the
  character's package set (what `has package` tests) and its parents are not followed (**Verified**, `FUN_14067fab0`: the name test comes before the insert and the recursion).

### DIALOGUE and DIALOGUE_LINE

One shared runtime object per DIALOGUE / DIALOGUE_LINE / WORD_SWAPS record (`FUN_14067ddd0`, reader `FUN_14067b070`, tree
`FUN_14067d260`), used by every character. A DIALOGUE is a root; `lines` are DIALOGUE_LINE children, recursively; the tree may
link back to earlier lines (loops; **Verified** in the data).

| Field | Meaning |
|---|---|
| `speaker` (`TalkerEnum`) | who says the node ([Speakers](#speakers)). Lines: T_ME 15365, T_TARGET 4976, T_TARGET_IF_PLAYER 329, interjectors 1471/485/145, whole squad 7, with race 5 (**Verified**) |
| `text0`..`textN` | alternatives (up to 43 in the data); the non-empty ones are kept and one is picked uniformly each time the line is said (`FUN_140675320`) |
| `repetition limit` (`DialogRepetitionEnum`) | cooldown hours: 0 none, 1 → 2, 2 → 6, 3 → 48, 4 → 168, 5 once only (5000), 6 → 0.33 |
| `chance permanent`, `chance temporary` | percent; below 99 the line rolls ([Line check](#line-check)) |
| `target is type` (`CharacterTypeEnum`) | the target's NPC class must match |
| `score bonus` | added to the [score](#score) |
| `locked`, `one at a time`, `monologue` | DIALOGUE only. `monologue` is set on 1888 of 1890 roots (**Verified**), including event-1 talk dialogues, so it does not mean "no player replies" |
| `interjection` | interjection node ([Speakers](#speakers)) |
| `conditions` | DIALOG_ACTION {`condition name` (DC), `compare by`, `who`}, value = the reference's val0 |
| `effects` | DIALOG_ACTION {`action name` (DA)}, value = val0; DA_NONE entries are skipped |
| `in town of`, `my faction`, `target faction`, `my race`, `my subrace`, `target race`, `is character`, `has package`, `target carrying character`, `target has item`, `target has item type`, `world state` | extra checks ([Line check](#line-check)) |
| `give item` (val0 = count), `change relations` (val0), `unlocks`, `unlock but keep me`, `locks`, `interrupt`, `crowd trigger`, `trigger campaign` (val0..val1 hours, val2 %), `change AI`, `AI contract` (val0 hours) | effects ([Effects](#effects)) |

Never read by the exe (strings absent): DIALOGUE `for enemies`, `no target race`, CHARACTER `announcement dialogue`.

### WORD_SWAPS

A record named after a token (`OUTLANDER`, `RACENAME`, ...; 289) whose `lines` are DIALOGUE_LINEs with texts and conditions. See
[Tokens](#tokens).

## Enums

**Verified**: the FCS editor's enums, value = position. `fcs_enums.def` has only `EventTriggerEnum`, as a commented subset in another
order (no EV_NONE, the two blank names, EV_ESCAPING_SLAVE_SPOTTED, EV_MAX), so the editor's numbering is authoritative.

- **EventTriggerEnum**: 0 NONE, 1 PLAYER_TALK_TO_ME, 2 ANNOUNCEMENT, 3 I_SEE_NEUTRAL_SQUAD, 4 I_SEE_RAGDOLL, 5 `EV_______`,
  6 SOUND_THE_ALARM, 7 `EV_I_________`, 8 THIEF_CAUGHT_STEALING_FROM_ME, 9 SHOO_FROM_MY_BUILDING, 10 MARKED_FOR_DEATH,
  11 SCREAMING_TORTURE, 12 BAR_TALK, 13 UNLOCK_MY_CAGE_OR_SHACKLES, 14 UNLOCK_MY_CAGE_ATTEMPT, 15 I_DEFEATED_SQUAD, 16 LAUNCH_ATTACK,
  17 INTRUDER_FOUND, 18 HEALING_OTHER_START, 19 BEING_HEALED_START, 20 HEALING_OTHER_FINISHED, 21 BEING_HEALED_FINISHED,
  22 FIRSTAID_KIT_EMPTY, 23 GET_UP_PEACE, 24 GET_UP_FIGHT, 25 GET_UP_UNNECCESSARY_FIGHT, 26 HARRASSMENT_SHOUTS, 27 I_SEE_ANIMAL_SQUAD,
  28 SPEECH_INTERRUPTED_ATTACKED_BY_TARGET, 29 SPEECH_INTERRUPTED_ATTACKED_BY_STRANGERS, 30 CONTRACT_JOB_ENDED, 31 BETRAYAL,
  32 LOOTING_WEAPON_ONLY, 33 LOOTING_EVERYTHING, 34 I_SEE_UNIFORM_IMPOSTER, 35 INTRODUCING_NEW_SLAVE, 36 ESCAPING_SLAVE_SPOTTED,
  37 RECAPTURED_A_SLAVE, 38 SHOUT_AT_SLAVE_WORKER, 39 SLAVE_DELIVERY, 40 ESCAPED_EX_SLAVE_SPOTTED, 41 WITNESS_GENERIC_ASSAULT,
  42 WITNESS_LOOTING_ALLY, 43 WITNESS_THIEF_OR_LOCKPICK, 44 BOUNTY_SPOTTED, 45 ESCAPED_PRISONER_SPOTTED, 46 PRISONER_FREE_TO_GO,
  47 ALMOST_WOKE_UP, 48 ENTER_BIOME, 49 ENTER_TOWN, 50 SQUAD_BROKEN, 51 BOUGHT_ME_FROM_SLAVERY, 52 EATING_SOMETHING_SOUNDS,
  53 WORSHIPING_SOMETHING, 54 SLAVE_ESCAPE_OPPORTUNITY_SAVIOR, 55 SLAVE_ESCAPE_OPPORTUNITY_ALONE, 56 ASSASSINATION_FAILED,
  57 EATING_MY_CROPS, 58 KIDNAPPING_MY_ALLY, 59 USING_MY_TRAINING_EQUIPMENT, 60 GIVE_UP_CHASE, 61 ACID_FEET, 62 ACID_RAIN,
  63 ACID_WATER, 64 WINDY, 65 POISON_GAS, 66 I_SEE_ENEMY_PLAYER, 67 I_SEE_ALLY_PLAYER, 68 I_SEE_ILLEGAL_PLAYER_BUILDING, 69 BURNING,
  70 LOST_LEG, 71 LOST_ARM, 72 I_SEE_PLAYER_NICE_BUILDING, 73 TAKEN_OVER_PLAYER_TOWN, 74 CROWD_TRIGGERED, 75 MAX (all `EV_`-prefixed).
- **DialogActionEnum**: 0 DA_NONE, 1 TRADE, 2 TALK_TO_LEADER, 3 JOIN_SQUAD_WITH_EDIT, 4 AFFECT_RELATIONS, 5 AFFECT_REPUTATION,
  6 ATTACK_CHASE_FOREVER, 7 GO_HOME, 8 TAKE_MONEY, 9 GIVE_MONEY, 10 PAY_BOUNTY, 11 CHARACTER_EDITOR, 12 FORCE_SPEECH_TIMER,
  13 DECLARE_WAR, 14 END_WAR, 15 CLEAR_AI, 16 FOLLOW_WHILE_TALKING, 17 THUG_HUNTER, 18 JOIN_SQUAD_FAST, 19 REMEMBER_CHARACTER,
  20 FLAG_TEMP_ALLY, 21 FLAG_TEMP_ENEMY, 22 MATES_KILL_ME, 23 MAKE_TARGET_RUN_FASTER, 24 GIVE_TARGET_MY_SLAVES, 25 TAG_ESCAPED_SLAVE,
  26 FREE_TARGET_SLAVE, 27 MERGE_WITH_SIMILAR_SQUADS, 28 SEPARATE_TO_MY_OWN_SQUAD, 29 ARREST_TARGET, 30 ARREST_TARGETS_CARRIED_PERSON,
  31 ATTACK_TOWN, 32 ASSIGN_BOUNTY, 33 CRIME_ALARM, 34 RUN_AWAY, 35 INCREASE_FACTION_RANK, 36 LOCK_THIS_DIALOG, 37 ASSAULT_PHASE,
  38 RETREAT_PHASE, 39 VICTORY_PHASE, 40 ENSLAVE_TARGETS_CARRIED_PERSON (all `DA_`-prefixed), 41 CHOOSE_SLAVES_SELLING,
  42 CHOOSE_SLAVES_BUYING, 43 CHOOSE_PRISONER_BAIL, 44 CHOOSE_CONSCRIPTION, 45 CHOOSE_RECRUITING, 46 CHOOSE_HIRING_CONTRACT,
  47 SURRENDER_NON_HUMANS, 48 CHOOSE_ANIMALS_BUYING, 49 DA_CLEAR_BOUNTY, 50 DA_PLAYER_SELL_PRISONERS,
  51 DA_PLAYER_SURRENDER_MEMBER_DIFFERENT_RACE, 52 DA_SUMMON_MY_SQUAD, 53 DA_REMOVE_SLAVE_STATUS, 54 DA_OPEN_NEAREST_GATE,
  55 DA_ATTACK_STAY_NEAR_HOME, 56 DA_MASSIVE_ALARM, 57 DA_ATTACK_IF_NO_COEXIST, 58 DA_KNOCKOUT, 59 DA_END.
- **DialogConditionEnum**: 0 DC_NONE, 1 RELATIONS, 2 PLAYERMONEY, 3 REPUTATION, 4 CARRYING_BOUNTY_ALIVE, 5 CARRYING_BOUNTY_DEAD,
  6 FACTION_VARIABLE, 7 IMPRISONED_BY_TARGET, 8 IMPRISONED_BY_OTHER, 9 IS_A_TRADER, 10 FACTION_RANK, 11 BUILDING_IS_CLOSED_AND_SECURED,
  12 PLAYER_TECH_LEVEL, 13 NUM_DIALOG_EVENT_REPEATS, 14 IS_IMPRISONED, 15 IMPRISONMENT_IS_DEATHROW, 16 TARGET_IN_TALKING_RANGE,
  17 IN_MY_BUILDING, 18 TARGET_LAST_SEEN_X_HOURS_AGO, 19 IS_LEADER, 20 MET_TARGET_BEFORE, 21 WEAKER_THAN_ME, 22 STRONGER_THAN_ME,
  23 HAS_TAG, 24 IS_ALLY, 25 IS_ENEMY, 26 PERSONALITY_TAG, 27 BROKEN_LEG, 28 BROKEN_ARM, 29 DAMAGED_HEAD, 30 NEARLY_KO,
  31 IN_A_NON_PLAYER_TOWN, 32 IS_RUNNING, 33 COPS_AROUND, 34 `NULL_NULL_____DC_TARGET_SQUAD_SIZE`, 35 SQUAD_SIZE, 36 IS_PLAYER,
  37 NUM_BACKPACKS, 38 SQUAD_ONLY_ANIMALS, 39 IS_OUTNUMBERED, 40 BOUNTY_AMOUNT_PERCEIVED, 41 IS_KO, 42 IS_NEARLY_KO, 43 SQUAD_IS_DOWN,
  44 IS_DEAD, 45 IS_FEMALE, 46 CARRYING_SOMEONE_TO_ENSLAVE, 47 BOUNTY_AMOUNT_ACTUAL, 48 IM_UNARMED, 49 TOWN_HAS_FORTIFICATIONS_WALLS,
  50 TARGET_IS_MY_MISSION_TARGET, 51 MY_MISSION_IS_FRIENDLY, 52 I_LOVE_THIS_GUY, 53 I_HATE_THIS_GUY, 54 I_SHOULD_SCREW_THIS_GUY_OVER,
  55 I_SHOULD_HELP_THIS_GUY, 56 IN_COMBAT, 57 WITHIN_TOWN_WALLS, 58 TOWN_WALLS_LOCKED_UP (**Verified**: the subject's current town, none reads false; then Town vfunc `0x300` `FUN_140929820` over the town's buildings: at least one gateway with a controller door and no such door in state 1 open; closed, opening and closing count as locked; Meitou `LockSystem.TownLockedUp`), 59 IS_SLAVE, 60 HAS_A_BASE_NEARBY,
  61 TARGET_IS_SLAVE_OF_MY_FACTION, 62 IS_ESCAPED_SLAVE, 63 IS_IN_LOCKED_CAGE, 64 WEARING_LOCKED_SHACKLES, 65 IS_SAME_RACE_AS_ME,
  66 CAN_AFFORD_BOUNTY, 67 IS_SNEAKING, 68 IS_INDOORS, 69 HAS_ILLEGAL_ITEM, 70 USING_MY_TRAINING_EQUIPMENT, 71 STARVING,
  72 MIXED_GENDER_GROUP, 73 TOWN_LEVEL_CURRENT_LOCATION, 74 PLAYERS_BEST_TOWN_LEVEL, 75 IN_A_PLAYER_TOWN, 76 TARGET_CHARACTER_EXISTS,
  77 IS_RECRUITABLE, 78 HAS_AI_CONTRACT, 79 HAS_ROBOT_LIMBS, 80 DC_END (all `DC_`-prefixed).
- **DialogRepetitionEnum**: 0 DR_NO_LIMIT, 1 DR_SHORT_2, 2 DR_MEDIUM_6, 3 DR_LONG_48, 4 DR_LONG_1WEEK, 5 DR_ONCE_ONLY, 6 DR_VSHORT_020.
- **TalkerEnum**: 0 T_ME, 1 T_TARGET, 2 T_TARGET_IF_PLAYER, 3 T_INTERJECTOR1, 4 T_INTERJECTOR2, 5 T_INTERJECTOR3, 6 T_WHOLE_SQUAD,
  7 T_TARGET_WITH_RACE.
- **CharacterTypeEnum**: 0 OT_NONE, 1 OT_LAW_ENFORCEMENT, 2 OT_MILITARY, 3 OT_TRADER, 4 OT_CIVILIAN, 5 OT_DIPLOMAT, 6 OT_SLAVE,
  7 OT_SLAVER, 8 OT_BANDIT, 9 OT_ADVENTURER, 10 OT_END.
- **PersonalityTags** (DC 26 `tag`): 0 PT_NONE, 1 HONORABLE, 2 TRAITOROUS, 3 `PT___`, 4 `PT__`, 5 SMART, 6 DUMB, 7 `PT____`,
  8 `PT_____`, 9 BRAVE, 10 FEARFUL, 11 WARM_KIND, 12 COLD_CRUEL, 13 NORMAL, 14 MANIACAL, 15 PT_END.
  **Personality roll** (**Verified**, `FUN_14062b210` at character setup, `FUN_140624b70`; weights read from the exe, `DAT_141685340` 5.0, `DAT_14168a254` 3.0,
  `DAT_1416885d8` 0.2, `DAT_14167c308` 1.0; saved per character, so only new rolls follow it): the PERSONALITY record is the first `personality` reference of the
  CHARACTER if it resolves, else the first of its FACTION (12 base FACTION records list one), else the `blank personality` record (all tags 0). Each key family
  `tags always<i>`, `common<i>`, `rare<i>`, `never<i>` runs from i = 0 to the first missing key (values of 0 are skipped but do not stop it). Any `always` tag: one is
  picked uniformly (a repeated tag counts each time) and nothing else is read. Else a weighted map: `common` tags weigh 3, `rare` 0.2, a tag already in the map
  is skipped (the first listing wins), `never` tags strike none of these; then each default tag 1 HONORABLE, 2 TRAITOROUS, 5 SMART, 6 DUMB, 9 BRAVE, 10 FEARFUL
  weighs 1 unless listed or in the `never` set (`never` applies only here); the pick is a float in [0, total) against the running sums (`FUN_14063cea0`). PT_NONE only
  when the map ends empty (the record struck all six and listed nothing), never for a blank record. Meitou: `Personality.Roll` with its own random draw.
- **CharacterPerceptionTags_LongTerm** (DC 23 `tag`): 0 LT_NONE, 1 MY_INTRUDER, 2 MY_LIFESAVER, 3 FREED_ME, 4 STOLE_FROM_ME,
  5 MY_CAPTOR, 6 FRIENDLY_AQUAINTANCE, 7 DEFEATED_MY_SQUAD_ONCE, 8 SQUAD_LOST_TO_ME_ONCE, 9 KILLED_MY_FRIEND, 10 I_SCREWED_THIS_GUY,
  11 LT_MAX.

**Use in the data** (**Verified** by probe, references counted; a recount on 2026-10-23 over the install's load order: DA 5 ×22, 13 ×7, 14 ×3,
15 ×2, 17 ×27, 19 ×3, 23 ×7, 52 ×26, 54 ×4, 56 ×5, 58 ×1, 27 and 39 none; DC 3 ×51, 7 ×6, 8 ×13, 11 ×40, 12 ×2, 21 ×21, 22 ×44, 37 ×25,
39 ×68, 49 none, 52 ×77, 53 ×84, 55 ×5, 57 ×27, 58 ×10, 68 ×32, 69 ×34, 70 ×33, 73 ×21, 74 ×7, 75 ×54, 78 ×2).
- Events (`dialogs` val0): 3 ×294, 1 ×220, 13 ×147, 15 ×87, 24 ×82, 10 ×79, 18 ×79, 8 ×69, 11 ×68, 21 ×63, 42 ×63, 19 ×62, 14 ×61,
  29 ×59, 66 ×59, 9 ×57, 28 ×56, 67 ×55, 22 ×53, 43 ×53; every other event fewer than 50.
- Effects: DA 6 ×1192, 33 ×417, 16 ×415, 12 ×389, 8 ×352, 36 ×244, 34 ×186, 29 ×176, 37 ×160, 4 ×154, 3 ×138, 7 ×128, 20 ×115,
  18 ×112, 0 ×102, 28 ×85, 1 ×80, 38 ×79, 2 ×72, 32 ×64, 21 ×63, 9 ×44; the rest fewer than 35.
- Conditions: DC 26 ×2125, 56 ×958, 14 ×892, 2 ×537, 35 ×417, 36 ×395, 17 ×367, 59 ×362, 16 ×357, 45 ×315, 65 ×310, 23 ×276, 32 ×243,
  31 ×237, 1 ×189, 27 ×188, 43 ×177, 38 ×167, 28 ×162, 41 ×158, 24 ×134, 77 ×133, 19 ×122, 18 ×117, 20 ×116, 79 ×103; the rest
  fewer than 100. `who`: 0 ×5311, 1 ×5550, 2 ×70, 3 ×7.

## Runtime

Each character has one `Dialogue` state (Character+0x280): owner (+0x150), target handle (+0x158), current event, root and current line, interjectors 1-3,
bubble and timers, choice list, per-event cooldowns and last target, per-event repetition counter, lock map, permanent-chance map,
package set.

### Firing an event

`FUN_140684990(dialogue, target, event)` refuses when the event's cooldown is above 0, the owner is dead, KO or asleep (unless the
event is in the mask `DAT_141eeaecc` = 0x03800800, events 11, 23, 24, 25; **Verified**, the exe's data), the owner is talking already (unless the event is one of 2, 6, 11, 13,
17, 26, 30, 31, 34, 36, 40, 45, 46, 48, 51, 54, 74: the **interrupting set**, `FUN_1406712f0`), or an owner-state test fails (skipped for 6, 11, 39). After a successful 3, 4,
27, 66 or 67 that event has a 1.0 cooldown (frame dt).

Event 1 starts a [player conversation](#player-conversation), using a stored `interrupt` DIALOGUE instead of the event-1 list when one
is pending. Every other event starts an [ambient conversation](#ambient-conversations).

Sources (call sites): 1 the player's Talk order (task 12, after reaching the NPC; also tasks 102/159/161 when the plan's event is
0); 3, 27, 34, 59, 66, 67 perception (`FUN_140859680`); 10 `FUN_1408d0d90`; 11 torture; 15 `CharacterAnimal::vfunc_62`; 18-21 first
aid (start: the medic 18 at the patient, the patient 19 back unless down; finish: 20 / 21 likewise; none when treating oneself);
22 empty kit (`Task_FirstAidRig`, the medic at the patient); 23-25 getting up (`Task_GetUp`, no target: 24, or 23 when the character's two
enemy counters (+0x94, +0x98) sum below 1; a variant `FUN_1408c6780` fires 25 on further counters, **Unknown** which); 26 harassment; 30 contract end (target = employer); 32/33 AI looting; 37 recapture; 47 almost
woke; 49 entering a town (no target); 50 squad broken (`FUN_1407a5be0`, on the platoon's intact to not-intact change, each member vs its attack target: [perception](perception.md#retreat-and-memory)); 51 slave bought; 52 eating a prisoner; 53 worship; 60 giving up a chase ([attack](combat.md#leaving-a-fight))
(move, follow, attack, talk tasks); 61-63 acid; 66 also from DA 56; 68 illegal player building; 73 town taken over; others from
lock and prison code: 13 from `Task_PickLock`, `Task_CutShackles` and `Task_BruteForceShackles` (the freed character at the
worker, when they differ); 31 `Contract_HiredAlly::vfunc_8` (betrayal); 36 the witness function and perception; 44 `FUN_140856f00`;
70 / 71 losing a leg (limb slots 2, 3) / an arm (`FUN_14064edc0`, no target).

**Witnesses** (`FUN_140854d10`, **Observed**): a witness reacting to a crime fires an event at the criminal by the crime code
(economy.md 11.2): Burglary 2 and Terrorism 7 → 43; Theft 3, Looting 10, Fencing 13 → 42, or 8 when the victim is in the witness's
platoon; Trespassing 11 → 17 (also fired by perception when it sets the crime, economy.md 11.2); Crop theft 14 → 57; Enslaving 1, Kidnapping 15 → 58; Uniform theft 16 → 34; the rest 41. Each case has
further tests that return without firing (partly read).
Event 17's guard dialogue `intruder law` (64870-rebirth.mod: monologue, no conditions, repetition limit 0; "Intruder!", "Infiltration!",
...) has one line whose effect is DA 6 ATTACK_CHASE_FOREVER; packages `Npc MILITARY Law 3` and `Empire Noble Bodyguard` (inherited) and the
Samurai Sergeant / Elite, Traders Guard and Fark Guard characters carry it (**Verified** by probe against the base data).

### Choosing a line

`FUN_14067a0c0` (**Verified**). Candidate lists are sorted by [score](#score), highest first, without duplicates. Each node's score here is its
static score plus 2 when the target's race is in its `target race` (`FUN_140671bb0`); the walk stops at the first entry whose score is below the best so
far (roots excepted; an interjection node always stops it). For each node the [speaker](#speakers) is resolved and the
[line check](#line-check) runs with that speaker as "me"; a passing root descends into its children, its total being its score plus the chosen leaf's
(also with the leaf's race bonus). An interjection node first finds and stores an interjector, then descends the same way. A passing leaf, or a root or
interjection total at least the best so far, is appended to the candidate list and becomes the best; **the list is never cleared**, so earlier lower
candidates stay in it and the final pick is **uniform over every appended candidate**. The checks and bonuses use the target the walk started with
([retargeting](#retargeting)). The root test is virtual (`FUN_1406a6050`): true for a DIALOGUE, for an interjection node and for a line with no text
(**Observed**; Meitou treats only a DIALOGUE as a root, and says a textless line itself, which 884 of the 22,783 base lines are; whether the original
descends into such a line's children instead is **Unknown**).

### Score

Computed at load: +1 speaker not T_ME, + `score bonus`, +2 `locked`, +2 for each non-empty `in town of` / `my faction` /
`target faction` / `target race`, +2 when the first `target has item type` reference resolves (**Verified**, `FUN_14067b070`: only that one is read), +2 each `target has item` and each `world state` entry, +1 each condition
(+1 more only for a required tag condition, DC 23 / 26 with val0 1; a forbidden one scores 1, **Verified**, `FUN_14067b070`), +1 `chance permanent` below 99, +1 `target is type` above 0, +2 interjection node, +1 speaker 3-6. At
choice time +2 when the target's race is in `target race` (`FUN_140671bb0`). A root's effective score is its own plus its best
child's (`FUN_1406ad9d0`).

### Line check

`FUN_140678f30`, all must pass, in order:
1. Tag masks (DC 23 / 26 are folded at load into required and forbidden bitmasks by the DIALOG_ACTION's `tag`, val0 1 = required).
   PERSONALITY_TAG tests the speaker's personality (status `+0xd8`); HAS_TAG (`FUN_140674c50`) tests the speaker squad's long-term
   [memory](#squad-memory) tags on the target: all required set, no forbidden; never noted: passes only when nothing is required; no
   target: not tested.
2. A squad test for flagged lines and a per-character line flag (purposes **Unknown**).
3. `unique` (no current field sets it). **Verified** meaning (`FUN_1400cd0b0` is the hand's `operator!=`): a node with `unique` set and a stored speaker hand (`+0x248`, type not none) fails the line when the current speaker's hand differs from the stored one; the hand is filled from the speaker after the chance-permanent roll succeeds, for unique nodes only. So a unique line sticks to the first speaker that used it. No base DIALOGUE_LINE sets it (the field exists only on CHARACTER and ANIMAL_CHARACTER records), so there is no vanilla effect; Meitou's `DialogueNode` has no such rule.
4. `chance temporary` below 99: a fresh 0-100 roll every check.
5. `in town of` (the speaker's current town's faction), `my faction`, `target faction`, `has package` (any of the speaker's
   packages), `is character` (the speaker's record), `target carrying character` (for each listed character in turn, the first
   member of the target's squad, the target or one the speaker perceives, that carries it becomes the target, [retargeting](#retargeting);
   any hit passes), `world state` (all equal their bool), `target has item type` / `target has item` (one ordered
   pass over the target's squad, see below; retargets), `target race` (**Verified**, `FUN_140678f30`: the target alone first, passing with no side effect; else per listed race in turn the first eligible squad member of that race becomes the target, [retargeting](#retargeting), so the last race with a hit decides; any hit passes; the one caller with the flag set, `FUN_14067a530` (the [token](#tokens) expansion), does not retarget), `my race`,
   `my subrace`. Each list is OR within itself. The race tests (`my race`, `target race`, the +2 below, T_TARGET_WITH_RACE, DA 47) match
   a race when the listed RACE is that race or any race of its RACE_GROUP (`FUN_14042ecd0`; the group's `races` set race +0x120, a race in
   two groups keeps the last loaded, `FUN_140430070`); `my subrace` is an exact RACE compare (**Verified**, decompile). The four squad tests count only the target and members the speaker perceives
   (`FUN_140855960`, **Observed** in `FUN_140678f30`); Meitou reads "perceives" as within the speaker's sight range.
   **Item tests** (**Verified**, `FUN_140678f30`): `target has item type` is one `item function` (0 = no test; the node reads only its first reference) and
   `target has item` a list; both are one pass over the eligible squad members in squad order. Flags: the list flag starts true when the list is empty, the
   function flag when no function is set. Per member the function is tested first (`FUN_14004b84e`: the member's own inventory list, any section, flat; the
   flag stays set), then the list (`FUN_140745f70`: the member's own list, else the worn backpack's own list; a container deeper is not searched). The first member
   that leaves the list flag set becomes the target ([retargeting](#retargeting)) and ends the pass. The line fails unless both flags are set, so a
   function-only line is decided by the first eligible member (which becomes the target even when it lacks the function), and with both the function holder
   must come at or before the first listed-item holder.
6. `repetition limit`: fails while now − last use is below the cooldown.
7. `target is type`: no target fails; otherwise the target's NPC class must equal the value. OT_SLAVE (6) instead accepts slave
   state 1/2 (slave, escaped) or class 6, and an accepted OT_SLAVE line returns at once, **skipping steps 8-9** (**Verified**:
   `FUN_140678f30`, slave state from `FUN_1405c8aa0`).
8. `chance permanent` below 99: a roll made once per character and line, then cached.
9. The DC list in order ([Conditions](#conditions)).

#### Retargeting

`FUN_140671930` (**Verified**): a matching squad member that is not the checked state's owner and not its target already is copied into the state's target
handle (`+0x158`) the moment its test matches, even if a later test of the line fails, and while probing candidate lines. It does not write the interjector slots
(`+0x1c8`, `+0x1e8`, `+0x208`; only interjection nodes do, `FUN_14067a0c0`). T_TARGET speakers, tokens, effects and later checks use the new target, but the
checking call keeps the target it was given (`FUN_140678f30` param 3) and `FUN_14067a0c0` passes the walk's own to every check and race bonus, so a retarget
shows from the next speaker resolution, walk or click (`FUN_140683c00` re-reads the handle). Callers: the `target carrying character`, `target has item (type)` and
`target race` tests above, and two conditions in `FUN_140676950` (IS_FEMALE's branch and HAS_ILLEGAL_ITEM's). Meitou: `SetTarget` writes `Conversation.Player` when the
check's `me` is the conversation's NPC (an interjector candidate's own state is not the conversation's); `Pick` tests the target it started with.

### Speakers

`FUN_140672bf0`: T_ME and T_WHOLE_SQUAD the owner (how WHOLE_SQUAD fans out is **Unknown**); T_TARGET the target; T_TARGET_IF_PLAYER
the target if it is a player-faction character, else the line is skipped; T_INTERJECTOR1-3 the stored interjectors; T_TARGET_WITH_RACE
for each listed `target race` in turn, the first member of the target's squad (squad order, no perception test) whose race matches it
(**Verified**, `FUN_140672bf0`). The token walk's flag (**Verified**, `FUN_140672bf0`, `FUN_14067a0c0` :55-60) only drops overrides through the
dialogue's `+0x2a0` handle: unflagged, T_ME and T_WHOLE_SQUAD resolve to it while the current event (`+0x188`) is NONE, and a T_TARGET speaker
that is the walk's target becomes it; flagged, T_TARGET becomes it only when the target is the owner (event NONE), else the owner / target as above.
`+0x2a0` is cleared at construction (`FUN_140673d30`) and conversation end (`FUN_140674830`), written when an event fires or a conversation starts
(`FUN_140684990`, `FUN_140683f90`), and is `FUN_1406717c0`'s fallback when the target is the owner; what it holds is **Unknown**. Meitou models no
`+0x2a0`, so its speakers are the flagged path in every walk.

Interjectors (`FUN_140679bc0`): members of the **owner's own squad**, awake, not the owner, not an animal, not talking, not already an
interjector, passing the node's check as speaker. A `dumb` character qualifies only when the node's `is character` lists it, an empty list failing (fcs.def
`dumb` "prevents them becoming interjectors or talking to NPCs", **Verified** text; `FUN_140679bc0`, **Verified**: the other members need no list test of their own, the line check applies a non-empty list to them). The line check itself (`FUN_140678f30`) has no dumb rule, so a dumb
NPC answers an event's lines like any other; the rule is also in [Arrival banter](#arrival-banter) and in the Talk To order, which a dumb player character refuses ([ui-input](ui-input.md)). Weight = clamp((d² − 202800) / (900 − 202800), 0,
1): 1 within 30 units, 0 beyond about 450; ×0.1 for a different race (inferred). The weighted pick rule is **Unknown**.

### Player conversation

`FUN_140684320`:
- Dropped unless the player character is within 9216 units of the camera node (inferred as the camera).
- Plays Wwise `Initiate_Conversation`. Chooses the root and its best child, says it.
- Opens the DialogueWindow when the said line has passing children; **the window pauses the game** (clock speed 0, the previous speed
  restored on close, `FUN_140787fb0`).
- The talking player character is the one given the Talk order; walking there is that task's precondition. AI-to-AI talk range is
  260 units (DC 16).
- **Talk distance** (**Observed**, task 12's `isAtLocation` → `AI::isWithinChoppingRange` `FUN_14059c6d0`, character branch; constant
  `DAT_141e3c5d0` = 7.0 read from the exe): X/Z distance < the target's radius (`Character::getRadius`: its movement's footprint
  radius +0x364 x a scale) + 7 x 3 (7 x 2 when the target is a ragdoll), that reach **doubled when the talker is not moving**, and
  |dy| < 10 (< 35 on the same floor); else within 7 x 1.6 = 11.2 on the same floor or |dy| < 35. Meitou (`DialogueSystem.InTalkRange`):
  the target's scaled radius (`CombatSteering.Radius`: footprint x body scale) + 21, doubled once stopped, |dy| < 35; also for an NPC walking up to talk (task 102, engine choice).
- **Walking to a moving NPC** (**Observed**: the player-reported vanilla behaviour of issue #136, the chase of a moving pick-up target; the task-12 walk's own re-aim code is not read): the talker follows the NPC's current position until in range. Meitou (`WalkUp`, shared by the Talk and Trade walks): every 0.5 s the Move order is renewed when the NPC is over 12 from the walk's goal or the walk has ended short of it; the NPC is kept still only when idle or wandering (`Hold`), so a walking NPC is chased; a talker found on any other task (another order, a fight) drops the approach (engine choice). Once the conversation opens, an NPC on its AI's walk stops and walks on after (`TaskStart.OnAiWalk`; engine choice, whether the original stops it is **Unknown**). The give-up after `ApproachSeconds` (60 s) without the talker moving on its walk (a long walk is not cut short, one stuck in place or re-aimed in vain is) is an engine choice.

**Saying a line** (`FUN_1406839a0`): in a player conversation a line with **no children ends the conversation at once** without being
shown (whether the original shows such closing lines elsewhere is **Unknown**). Otherwise: pick a text and mark the line and its parent chain
used, expand [tokens](#tokens), **run its effects at once**, log the text and put it in the NPC panel, then build the choices.

**Last use** (**Verified**, `FUN_140671770`; said line `FUN_140675320`, clicked reply `FUN_140683c00`): the stamp is each node's `repetition limit` clock.
A line's parent (`+0x230`) is set by the builder (`FUN_14067d260` via `FUN_1406ad9d0`) only when the line's owner is root-like (a DIALOGUE, an interjection
node or a textless line, see [Choosing a line](#choosing-a-line)), so a line under a text line has none, and a line shared by several roots keeps the last
one built. Meitou stamps the line and the conversation's root (`MarkUsed`): the same except for a shared line, or a deep line restamping its root, which
costs no game time while the window pauses the game. No parent link is kept, since the original's depends on load order.

**Choices** (`FUN_140680130`, `FUN_140683c00`): the said line's children are checked in list order (me = NPC, target = the player
character). If a passing child has speaker T_ME, a single `[...]` button continues to it and the scan stops; otherwise every passing
child is a button with a random text. Clicking one logs "<name>: <text>", plays `Dialogue_Choice` / `Continue_Conversation`, marks the
reply used, **runs its effects**, then chooses and says the reply's best child.

### Ambient conversations

`FUN_140683f90(dialogue, target, line, event, force)`: a monologue root outside event 1 shows a **speech bubble**, no window; a root
says its best child (as in [Choosing a line](#choosing-a-line)), any other line is said as given. Large font for events 2, 11, 17, 26,
31, 34, 36, 40, 45, 74 (`FUN_1406713a0`, **Verified**), medium for the others.

**Chain** (`FUN_140684720`, **Observed**): when a bubble ends, the owner's state picks the best child of the said line with full
speaker resolution (owner, target, interjector), copies its target to that speaker's state, and that speaker says it; a line said by
someone else hands back to the owner's state, so the tree stays rooted at the owner. This is how two NPCs talk: the target says the
T_TARGET lines. A non-monologue root in bubble mode ends after its first line (root flag `+0x1e1`, `FUN_140685090`). **Verified** (base
data probe): every root used for events 2, 3, 13, 18 and 66, every campaign announcement and every crowd trigger is a monologue.
**Effects of a chained line run on its speaker** (**Verified**): the chain calls `FUN_1406839a0(speaker's state, line)` → `FUN_140680560`
with that state, and the lock/unlock/interrupt writes go to the state passed in (`+0x30` lock map, `+0xc8` interrupt); the first line of
a bubble runs on the owner's (`FUN_140683f90` → `FUN_1406839a0(owner's state)`). Meitou: `Bubble.Chained`. **Observed**: the first line is
not given a speaker of its own in `FUN_140683f90`; Meitou says it as its resolved speaker (**Unknown** whether base data has a first line
that is not the owner's).

**Sight** (`FUN_140859680`, **Observed**, partly read): event 3 fires for a seen character outside the player's faction too (an NPC of
another squad); 66 and 67 need a player-faction target. **Looker gates** (**Verified**, `FUN_1408529c0`, `FUN_140859680` :52-72, 106-115): a slave (state 1) says nothing; 66 and the non-friend 3 also need slave state 0 and no escape; 67 stays open to state 2 and escaped lookers. For a non-ally target (**Verified**, the peaceful branch of `FUN_140859680`: neither side in combat mode, the target without a pending crime) event 59 USING_MY_TRAINING_EQUIPMENT replaces 66 / 3 when the target's Character `+0x158` equals the looker's faction and the looker senses no enemy: `+0x158` is the faction of the training dummy the target is using, written by the update of `Task_UseTrainingDummy` (`FUN_14035c1e0`, the dummy building's faction) and cleared by its end (`FUN_140345570`) and at the start of every character tick (`FUN_1405d00b0`); Meitou `DialogueSystem.SightEvent` (the Training fixture the target operates). When either side is in combat mode (or the target has a pending crime) the branch instead ends in `FUN_1406744a0(looker, target, 4)`, the squad-memory tag TEMP_ENEMY (not a dialogue event), when the target is outside every town and the looker or the target is a bandit (NPC class 8): Meitou `AggroSystem.BanditFightTag`. An awake animal the looker sees gets event 27 (I_SEE_ANIMAL_SQUAD) first (Meitou `DialogueSystem.SeeAnimals`; two flags of the original gate, target vfunc 0x240 and +0x1a9, are not decoded and left out). It runs only for a sensed-list entry with its seen or heard flag (`+0x38` / `+0x39`,
`FUN_140855710`), and only when Dialogue `+0x28` is set: `FUN_14067e220` sets it when a package with lines for 3, 4, 17, 27, 34, 36, 40, 66 or
67 is added (**Observed**). A fired event is refused while its timer in Dialogue `+0xd0` is above 0; firing sets it to 1.0 (`FUN_140684990`,
**Observed**). No start-of-game delay was found. **Observed** (user, vanilla): a new game in the Hub, the barman greets the player character
only after it walks into the bar. Meitou (`DialogueSystem.quiet`): pairs noticed at the world's first look (new game or load) get no sight line
until the player character has been out of notice.

**Camera range** (`FUN_140684990` routes event 1 to `FUN_140684320`, **Verified**): a player conversation starts only when the talking player character is within 9216 of the camera object's scene node (constant at 0x141700514); other events take the bubble path with no such test. Meitou: `DialogueSystem.TooFarFromCamera`, distance to the camera focus (`FocusCommand`), no test without one.
**Censorship** (settings.cfg `censorship`, bool, default off: `DAT_142133575`, written `FUN_1403eca90`; **Verified**, decompile and the exe's data): `FUN_14067e8b0` (run after `FUN_14067e660`, called from the line builders `FUN_14062b210`, `FUN_14067fd80`, `FUN_140680130`, `FUN_1406839a0`) replaces every occurrence, case-sensitively and in this order: shitting to crapping, Shitting to Crapping, shitty to crappy, Shitty to Crappy, shit/Shit/SHIT to crap/Crap/CRAP, fuck/FUCK/Fuck to bugger/BUGGER/Bugger, Bollocks/bollocks/BOLLOCKS to Damn/damn/DAMN, asshole/Asshole/ASSHOLE to idiot/Idiot/IDIOT, asswipe to idiot (no UPPER entries for shitting, shitty, asswipe). Meitou: `Censorship.Apply` on every dialogue line, choice and bubble text built by `DialogueSystem` (Interface setting; other text paths, journal and log lines, are not routed through it).
**Overlap** (`FUN_140671f50`, once a frame after the GUI update when 2 or more bubbles exist; **Verified**): for every pair with non-zero alpha whose absolute rectangles intersect (edges included) the axis of the smaller overlap is pushed (horizontal when the width overlap is smaller), each bubble by overlap / 2 + 1 integer pixels in opposite directions, the smaller coordinate toward smaller; passes repeat while any pair moved, at most 10; positions are recomputed from the heads each frame so nothing persists. Meitou: `PlayerInterface.SeparateBubbles`.
**Timing** (`FUN_140685090`, `FUN_14067fd80`): showing a bubble sets timer `+0x230` = 2.0 + 0.12 × text length (both constants **Verified**,
read from the exe); DA 12 sets the second timer `+0x234` (val0). Both lose the frame dt each update, only while the pause flag
`DAT_1421349c9` is clear, and the next line starts once both are below 0 (**Verified**; the dt unit is **Unknown**). Updates do nothing
while a state test on the owner (`FUN_1407d1440`) holds, unless the event is in the `DAT_141eeaecc` mask (11, 23, 24, 25; **Observed**; Meitou drops a
bubble whose speaker is not awake). No test of whether the speaker is drawn: a bubble shows over a character hidden inside a building (**Observed**, `FUN_140672320`). The `repetition limit` check passes when the cooldown is 0, the line was never used (stored time
−99.0, **Verified** constant) or now − last use ≥ the cooldown (`FUN_140678f30`).

**End** (`FUN_140674830`): bubble and window closed, the per-event counter +1 with its time (read by DC 13), the event's last target
stored, the squad blackboard notified for player conversations.

**AI tasks 102/159/161** (`Task_TalktoPlayer`, **Verified** decompile): fire `AITaskSytem+0x264` at the target (0 means 1), which the planner sets from the TaskData event of any
row on the chosen plan (188: 38, 194 / 218: 39, 158 / 283: 9); event 9 repeats every update (17 at an enemy, or with blackboard `+0xe8` at a non-ally); walks at the target;
after 25 game minutes, once the target is getting away (`FUN_140331ee0`: its task is 35 or 127, or a move order (29) to a non-zero spot farther
from the NPC than the target stands), resets its plan (`FUN_1405074a0`), ends the task and fires event 60; until then it walks on; done when the conversation has ended. 159 provides
state 109 (with a walk first) for 188 / 194 / 218, 161 provides 110 (no walk) for 158 / 283.
DA 2 gives the squad leader task 102 with event 1 (`+0x264`) for a player-faction target. **Verified** (probe, 124 AI_TASK records):
no base-data task uses the enums 102, 159 or 161. `isDialogDelivered` (109 / 110, evaluator `140792140`, **Verified**): true without a Dialogue object, else whether
the event was delivered in the speaker's current town (the per-event map the conversation end increments).

### Arrival banter

**Verified** (`PlayerInterface::triggerAreaArrivalDialogue` `FUN_1407ffe40`, called when the biome banner hides, [ui-screens](ui-screens.md)): the active squad needs 2 members;
candidates are members not unconscious, not carried, not in a bed or prison, not animals, not in combat mode, on screen and, for a BIOME_GROUP, not in a town. A member
sensing an enemy (`numEnemies` > 0) stops it, tested only for members that are up, free, not animals and not in melee combat mode (`FUN_1407ffe40` :100-117; the in-town and on-screen filters come after it, so an in-town member still aborts). With 2 or more candidates every candidate x every `arrival dialog` DIALOGUE is scored (`Dialogue::scoreCustomDialog`) and the best
runs (`runCustomDialog`). A `dumb` speaker (CHARACTER record flag, `FUN_1405e89c0`; Agnu and Ray in the base data) scores only when the DIALOGUE's `is character`
lists it, an empty list failing; any other speaker needs the list only when it is non-empty (**Verified**, `FUN_14067e060`, `FUN_140684d70`). Meitou: `DialogueSystem.ListsSpeaker` for interjections and the arrival banter; the line check tests only a non-empty list. Meitou (`AreaArrivalDialogue.Arrive`): the first dialogue, speaker
(with that rule, `MaySpeak`) and partner in squad order with a line win; "on screen" is the camera view test (`MedicalClock.OnScreen`), so a member
travelling alone far from its squad gets no banter (#430: without it the mate was out of sight and the speaker seemed to talk to itself).

**Retry** (**Verified**, `FUN_140801540` called every frame from `FUN_140787940`, `FUN_1408015d0`, `FUN_140800eb0`): hiding the banner stores the group and the real-time stamp (`FUN_14025b3b0` x 1e-6 seconds) and runs the first try; each frame the stored record is cleared when the group under the camera pivot (`FUN_1408f5770`) differs, else more than 10 s (`DAT_14167fad4`) after the stamp the stamp is renewed and the banter run again; the record is cleared only when the banter returns non-zero: a squad of under 2 members or a line started (zero: an enemy sensed, under 2 candidates or no pair with a line). Meitou: `AreaArrival` carries a serial, `AreaArrivalDialogue.Arrive` publishes (serial, forget) and the Biome UI (`PlayerInterface.RetryArrival`) keeps the group and the wall-clock stamp.

### Tokens

`/NAME/` pairs are expanded until none is left (`FUN_14067a530`). Built in: `COST` (val0 of the line's first DA 8 or 9, else of its
first DC 2), `MYNAME` (speaker), `YOURNAME` (target, else "you"), `TARGETFACTION` (target's faction, else "you"), `PLAYERFACTION`. Any
other name: the WORD_SWAPS record of that trimmed name, its lines chosen by the full line walk `FUN_14067a0c0` in token mode (**Verified**, `FUN_14067a530` :168-220: each line's speaker resolved, race bonus, interjections and roots as usual; walk target = the conversation target, or the passed one when that is the owner); run a second time when the first finds nothing; the picked line, its parents (the swap, a root it came through) are stamped used (`FUN_140675630`), so the repetition limit applies later; one text; no match → empty. **Observed** (1.0.68, Hive Trader): the walk retargets nobody, but a race hit in the squad still passes; a Shek squad member talking to the trader keeps the conversation with a Greenlander member beside her or outside the shop, and with him beside her RACENAME gave "Shek" 4 and "Human" 4 times in 8 tries. The flag (**Verified**, `FUN_14067a530` :212-213 passes 1 to `FUN_14067a0c0`, which hands it to every line check :61, nested walk :120 and [speaker](#speakers) :52) gates only `target race`'s retarget in the line check; Meitou passes it through `Pick`. Meitou: `DialogueSystem.Token`.
A token at the start or after ". ", "! ", "? " is capitalised. Every DA 8 effect of a line appends one " [c.N]" (N its own val0, `c.{1,num}`; DA 9 none), in effect order, to the text of a said line and of a choice button alike (`FUN_140675320`, called with flag 1 by `FUN_1406839a0` and flag 0 by `FUN_140680130`; the loop is not gated by the flag; the literals " [" and "]" **Verified** from `DAT_141686eec`, `DAT_14168d3f0`); **Observed**: the text is built before the tokens are expanded (Meitou appends after, the same result). Meitou: `MoneySuffix` on said lines (windows and bubbles) and choices. The `censorship` setting swaps a
fixed list of swear words. **Verified** by probe: 289 token names in the data, `HIM`, `OUTLANDER`, `FLATSKIN`, `BROTHER`, `YOURNAME`
the most used.

### Memory and saves

- `repetition limit` last-use time lives on the shared line, so **the cooldown is global across characters**; not saved.
- `chance permanent` rolls are per character; not written by `Character::serialise` (fcs.def's "saved forever" was not found).
- **Locks** are per character (default the DIALOGUE's `locked`) and **saved** in the character's save record as pairs `lok0`, `lok1`, ...:
  the string is the DIALOGUE string id, the bool of the same key "locked" (writer `FUN_140679900`, reader `FUN_14067f780`).
- `one at a time` (**Verified**, `FUN_140678f30`): fails while any member of the conversation owner's squad (owner included) has a live conversation, open window or bubble chain rooted at this node (`DialogueSystem.RootLive`).
- Per-event counters and last targets are memory only.
- The [squad memory](#squad-memory) is saved on the platoon (`FUN_140678cb0` reads `taglong`, `tagshort`, `tagtime` per index).

### Squad memory

**Observed** (`FUN_140673930`, `FUN_140674300`, `FUN_140673a90`, `FUN_140671840`, `FUN_1405e8410`, `FUN_140678830`; tag names from the
debug dump `FUN_140675680`). Each platoon (`+0x98`) maps a character key to an entry: a time (refreshed by every tag write; nothing
else, not sight, refreshes it), long-term tags (`CharacterPerceptionTags_LongTerm`: 1 intruder, 2 lifesaver, 3 freed me, 4 stole from
me, 5 captor, 6 friendly, 7 defeated my squad, 8 I beat his squad, 9 killed my friend, 10 I screwed him) and short-term tags
(`CharacterPerceptionTags_ShortTerm`: 2 aggressor, 3 TEMP ALLY, 4 TEMP ENEMY, 7 criminal). Bit = 1 << tag (**Verified**, `FUN_1405e20e0`).
- **Keys**: an NPC is its own key. All player-faction characters share **one** "player" entry for long-term tags, time and lookups
  (`FUN_140671840`), while short-term tags are written per character (`FUN_140674300`); the squad's TEMP ALLY on any player character
  is one bool (`memory +0x28`), which is also what a TEMP ALLY query about a player character returns.
- **Long-term write** (`FUN_140673930`, via `FUN_140673a20(self, other, tag)`): DA 19 and the end of an event-1 conversation (tag 0, so
  "met"; other events do not remember), DA 29 (5), the witness and relation deeds ([factions 3.5](factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile)),
  event 13 (3 freed me, with short TEMP ALLY, unless the speaker squad holds captor: `FUN_140684990`, run before a line is chosen, only for a speaker that is awake), a cage put-in (`Task_PutInSomething`
  `FUN_140358670`: the prisoner's squad tags the putter 5 captor), and the slave windows (selling: the sold one's squad tags the customer 5; buying: the freed one's squad tags the buyer 3,
  [economy 12](economy.md#12-slaves-prisoners-animals)). Meitou writes these (`DialogueSystem.Freed` / `Captured`, `FactionDeeds.CagedHook`, `CharacterTrade`); the blow tags 7, 8, 9 too (`FactionDeeds.Remember`); the witness tags of
  [factions 3.5](factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile) are not written yet.
- **Short-term write** (`FUN_140674300`): never TEMP ENEMY on the squad's own faction; a TEMP ALLY on a player character sets the bool;
  for a player character the squad's player entry takes the time. The character-level wrapper `FUN_1406744a0` also skips TEMP ENEMY on
  an allied faction or one the character regards as friendly. Cleared by `FUN_1406788d0` (contract code clears 4, 7, 2).
- **Attack effects 6, 55, 57** (`FUN_140680560`, **Verified**): forced sense of the target, the speaker's queue cleared, the squad TEMP ENEMY tag, then only for a speaker whose faction is not the player's (or whose AI's `+0x20` object `+0x60` is 0) `FUN_1405d20d0(speaker, 0, 0x65 | 0xf8, target)`: an Assault order, goal 101 (248 for 55: list B `!159`, a 0.3 + 2r h commitment). Its finder `FUN_140793eb0` takes the best known, in-area, standing, not imprisoned member of the target's squad. Meitou: `AiSystem.OrderAssault` (`AiMind.ForcedAssault`, re-picked by `SendAssaults` while free); a player-faction speaker gets no order; 248's extras are not modelled.
- **Attacks write TEMP ENEMY** (**Observed**, decompile; names KenshiLib 1ce4a16): the attacked character's squad tags its attacker 4
  through the wrapper in `AI::underMeleeAttack` (also reached from being targeted, [combat](combat.md#fight-lifecycle-and-orders)),
  `AI::underRangedAttack`, `Character::hitByMeleeAttack`, `CombatClass::_iHitYouAreYouHit` and `Character::iShotYou`. So an assault on one
  member makes the attacker an enemy of the whole squad (slot 125 below) at any relation, and its attack-on-sight members (goal 16) join.
  `Character::attackTarget(self, target)` (gives the AI the target, perceives it, tags it 4 on self's squad, replans) is called only by:
  a missed stealth KO (`Task_StealthKO`: both squads tag each other 4, then the victim goes for the attacker), a failed kidnap
  (`Task_Kidnapping`: the victim tags the kidnapper 4 and long-term 1, then goes for it), alarm investigators (`Task_InvestigateAlarms`:
  sensed characters of a faction hostile to theirs), DA 56 MASSIVE_ALARM, and the witness function `SensoryData::assessCrimes`: every
  reacting crime case ends by tagging the culprit short-term 7 criminal, setting its crime timer to 20 and, when the witness is the crime's
  victim (a trespass's victim is the witness that called it), `attackTarget` on the culprit. The player's attack orders do not call it.
  Short tags fade (**Verified**, `FUN_1406724e0` from the platoon slow step `FUN_1404ff060`, threshold 6.0 read from the exe): an entry
  whose game hours since its last write, truncated, exceed 6 (so 7 h) loses all its short tags; the entry stays unless the memory holds more than 40 entries and it is older than 48 h and its character is not a resolving player-faction one (then it is erased, `SquadMemory.Forget`, **Verified**); long tags, time and the player TEMP ALLY bool
  stay (Meitou: `SquadMemory.Age`). Besides that, the perception update (`SensoryData::add`) clears it once the tagged enemy is a slave of the looker's
  faction (or its state +0x2f8 is 2, **Unknown**). Meitou (`DialogueSystem.TagEnemy`): the alert in `RetaliationSystem`, the stealth-KO
  miss, the crime victim (`CrimeSystem.Witnesses`, an attack order unless it already fights the culprit, and the criminal tag 7), DA 56, the
  failed kidnap ([carry-loot](carry-loot.md#kidnapping)) and the alarm investigators ([economy](economy.md#town-alarms)).
- **Readers**: DC 18 (`FUN_140673290`: hours since the time, **9999.99** when never noted), DC 20 (an entry exists), DC 23, DC 52
  (long & 0xc), DC 53 (long & 0x2b0), "regards as friendly" (`FUN_1407923d0`, Character slot 126: same faction; not with TEMP ENEMY;
  yes with TEMP ALLY or love; else faction ally) and "regards as enemy" (`FUN_14079c950`, slot 125): TEMP ENEMY makes, and TEMP ALLY
  unmakes, an enemy across faction relations; `FUN_140599390` (a grudge: long & 0x7b2 or short & 0xb6).
- **"Regards as enemy" in order** (`FUN_14079c950(A, B)`, **Observed**): the same faction: no. Otherwise: A's squad holds TEMP ENEMY on B, or A's
  faction is the player's and B's squad holds TEMP ENEMY on A: yes; A's squad holds TEMP ALLY on B: no; else the faction relation test (hostile
  A -> B plus extra gates for disguise, coexistence and a flag at faction `+0x1d0`).
- **Save** (platoon, **Observed**: writer `FUN_1406789e0`, reader `FUN_140678cb0`): per entry `i` from 0 the entry's character handle under
  `tags<i>`, ints `taglong<i>` and `tagshort<i>`, float `tagtime<i>` (the double time cast to float); the reader stops at the first missing
  `taglong<i>`. The TEMP ALLY bool (`memory +0x28`) is not written, and where the shared player entry goes is **Unknown**. Meitou's own keys:
  [save](../formats/save.md#squad-memory).

## Conditions

`FUN_140676950`. `who` 0 = the speaker, **any other value = the target** (`FUN_140678f30`). `B` = value > 0. DC 13 and DC 76 are answered
first from what was passed. With no target (`who` ≠ 0) every other DC, 11 and 12 included, first takes **the player's selected object
nearest the speaker** (`FUN_1407f4ba0`: squared distance, no radius cap, the limit constant reads FLT_MAX; the first of equals; the list is
the one `FUN_1407f5520` reads "the first selected object" from, **Observed**), else the **first member of the active squad**
(`FUN_1407f5710`), as the subject for this and every later condition, and **stores it as the dialogue's target** (`+0x160`). Only when
the player has neither does the faction-level test `FUN_140671530` run, returned as is (no comparison with B): DC 1 = Me's faction's
relation to the player's faction, DC 2 = the player's money, DC 3 = the player faction's global trust (all Num2), every other DC
true (DC 10 included; DC 11 and 12 never get here). DC 1, 2, 3 reach the same function even with a subject (DC 1/3 then read the subject's
faction), and DC 2 and 12 never read it. **Verified** (decompile). Meitou: `Condition` / `SubjectFallback`; the target is stored unless the
Talk menu's dry run is picking.
- **Bool** conditions return computed == B (compare-by ignored; value 1 must hold, 0 must not).
- **Num3**: compare-by 1 `<`, 2 `>`, else `==`. **Num2**: compare-by 1 `<`, else `>=`.

| DC | Computes (S = subject, Me = speaker) | Style |
|---|---|---|
| 1 RELATIONS | Me's faction's relation to S's faction (−100..100) | Num2 |
| 2 PLAYERMONEY | the player faction's money, any `who` | Num2 |
| 3 REPUTATION | S's faction's global trust (FactionRelations `+0x18`), as an int (`FUN_140671530`: the faction object `+0x78`, then `+0x18`). **Verified** by reading both functions: DA 5 adds to the **speaker's** faction, DC 3 reads the **subject's**, so they meet only for `who` 0 | Num2 |
| 4 / 5 CARRYING_BOUNTY_ALIVE / DEAD | S carries a living / dead body with a bounty for Me's faction (or a diplomat of a hostile faction) | Bool |
| 6, 15, 34, 51 | not evaluated, always pass | - |
| 7 / 8 IMPRISONED_BY_TARGET / OTHER | S caged (in-something state 2) by Me's faction (7) or another (8); when S is Me, by the target's faction; no target: !B | Bool |
| 9 IS_A_TRADER | S's SQUAD_TEMPLATE has `is trader`, whatever the squad stocks (`FUN_1405cff70`, [who trades](economy.md#who-trades)) | Bool |
| 10 FACTION_RANK | the player's rank in Me's faction ([faction rank](factions-squads-towns.md#32-faction-rank)), any `who` | Num3 |
| 11 BUILDING_IS_CLOSED_AND_SECURED | Me's squad's "house locked" flag (`homelok`, platoon AI object `+0xe8` via `FUN_140791ba0`: Character `+0x658` -> `+0x78` -> `+0xf8`), the byte state 87 reads and Task_LockDoor sets ([locks](locks.md#doors); **Verified**: case 0xb of `FUN_140676950`, setter `FUN_1407933a0`), whatever `who` says; Meitou reads its home's flag, `LockSystem.IsHouseLocked` | Bool |
| 12 PLAYER_TECH_LEVEL | the research manager's tech level, `*(*(game + 0x38) + 8)` (**Observed**; the manager's level is the one [research](buildings-production.md#research) raises, 1 at the start), any `who` | Num3 |
| 13 NUM_DIALOG_EVENT_REPEATS | Me's counter for the current event | Num3 |
| 14 IS_IMPRISONED | S is caged | Bool |
| 16 TARGET_IN_TALKING_RANGE | same area as Me and within 260 | Bool |
| 17 IN_MY_BUILDING | S is inside Me's building | Bool |
| 18 TARGET_LAST_SEEN_X_HOURS_AGO | hours since Me's squad last noted S ([memory](#squad-memory)), 9999.99 never; `who` 0 asks about Me itself | Num2 |
| 19 IS_LEADER | S leads its squad | Bool |
| 20 MET_TARGET_BEFORE | Me's squad memory has an entry for S | Bool |
| 21 / 22 WEAKER / STRONGER_THAN_ME | status float `+0x90` (Character `+0x1a0`): 21 is Me's > S's × 1.5, 22 is S's > Me's × 1.5, strict (**Observed**, re-read in `FUN_140676950`). The float is written every update by `FUN_140852a30`: (strength x stat scale + melee attack rating `FUN_140884ee0` + defence rating `FUN_140886290`) / 3, x 0.1 for a non-animal with no weapon equipped (**Observed**; Meitou `CombatSystem.Rating`, computed when asked) | Bool |
| 24 IS_ALLY / 25 IS_ENEMY | relation ≥ 50 (or allied, or own) / ≤ −30 and not own | Bool |
| 27 BROKEN_LEG, 28 BROKEN_ARM, 29 DAMAGED_HEAD (< 0.2), 30/42 NEARLY_KO (< 0.1), 41 IS_KO, 44 IS_DEAD | S's body | Bool |
| 31 IN_A_NON_PLAYER_TOWN | S in a town not owned by the player | Bool |
| 32 IS_RUNNING | S runs (Me: always B) | Bool |
| 33 COPS_AROUND | Me senses law enforcement | Bool |
| 35 SQUAD_SIZE, 37 NUM_BACKPACKS | S's squad: members / members with a backpack in the backpack slot (`FUN_1405cb320`) | Num3 |
| 36 IS_PLAYER | S's faction is the player's | Bool |
| 38 SQUAD_ONLY_ANIMALS, 43 SQUAD_IS_DOWN, 72 MIXED_GENDER_GROUP | S's squad | Bool |
| 39 IS_OUTNUMBERED | S's counters (the object at S's `+0x650` + 0x28, Character slot 13 `FUN_1405c8700`): `+0x94` > `+0x4c` × 1.3, strict (**Observed**; the counts are `SensoryData` numEnemies and numConsciousAllies, [ragdolls](ragdolls.md); Meitou: `BodySystem.Senses`) | Bool |
| 40 / 47 BOUNTY_AMOUNT_PERCEIVED / ACTUAL | S's bounty with Me's faction (perceived: only while recognised) | Num2 |
| 45 IS_FEMALE | S's race is female | Bool |
| 46 CARRYING_SOMEONE_TO_ENSLAVE | S carries a living adventurer, bandit, slave or civilian. Carrying nobody, the result is `!(value > 0)`; carrying anyone, value 0 is always false (**Verified**, `FUN_140676950` case 0x2e; the slavers' "A new slave?" line needs the speaker at value 0, carrying nobody) | asymmetric |
| 48 IM_UNARMED | S has no weapon | Bool |
| 49 TOWN_HAS_FORTIFICATIONS_WALLS, 58 TOWN_WALLS_LOCKED_UP | S's current town (Character slot 14 `FUN_1405e1ed0` -> `+0x88`) has a building of the gate class near Me: town slot 97 `FUN_140929910` returns the nearest member of the town's building set with a non-null Building slot 123 (49 is true when any exists, wherever it is) / town slot 96 `FUN_140929820`: true when the town has at least one such building and none of them (gates without a first door, or failing `FUN_140547930`, are skipped) has a first door in state `+0x380` 1 (slot 123 is the gate-class test, state 1 is an open door `+0x380`, **Verified** `FUN_140929820`, `FUN_1409c70f0`; the extra door test `FUN_140547930` is **Unknown**). 49 has no use in the data; neither is built (**Observed**) | Bool |
| 57 WITHIN_TOWN_WALLS | S's gate code > 0 (Character slot 35 `FUN_140792200`, `amInsideTownWalls`, [gate codes](pathfinding.md#gate-codes)): refreshed from S's building (a gateway gives the code of the side S stands on) and cached at `+0xA0`, else the code the gate pass gave S (**Verified**, decompile `FUN_140676950`). Meitou reads the gate region at S's position; no gate pass yet counts as outside | Bool |
| 50 TARGET_IS_MY_MISSION_TARGET | S is Me's squad's mission target (`FUN_140792e00`) | Bool |
| 52 / 53 I_LOVE / I_HATE_THIS_GUY | Me's squad's long-term tags on S & 0xc / & 0x2b0 (`FUN_140599590`, `FUN_1405995d0`) | Bool |
| 54 I_SHOULD_SCREW_THIS_GUY_OVER | `FUN_14059a7b0`, each rule overriding the earlier: either is a bandit; no if both share an NPC class; yes if Me is traitorous (personality 2); no if Me regards S as friendly; no if Me is honourable (1); yes if S's faction owns Me as a slave (status `+0xe0`); no if Me loves S; yes if it hates S; yes if S is dead | Bool |
| 55 I_SHOULD_HELP_THIS_GUY | `FUN_14059a670`: the same NPC class; no if Me is traitorous or a bandit; yes if Me regards S as friendly; yes if both are honourable; yes if it loves S; no if it hates S | Bool |
| 56 IN_COMBAT, 67 IS_SNEAKING (not in combat), 71 STARVING (nutrition < 1.7), 79 HAS_ROBOT_LIMBS | S | Bool |
| 59 IS_SLAVE (state 1/2), 62 IS_ESCAPED_SLAVE (state > 1), 61 TARGET_IS_SLAVE_OF_MY_FACTION | S's slave state | Bool |
| 60 HAS_A_BASE_NEARBY | a town of S's faction within 23040 of Me | Bool |
| 63 IS_IN_LOCKED_CAGE, 64 WEARING_LOCKED_SHACKLES | S's lock | Bool |
| 65 IS_SAME_RACE_AS_ME | S's RACE_GROUP = Me's (race +0x120; two races in no group compare equal, **Verified** `FUN_140676950`) | Bool |
| 66 CAN_AFFORD_BOUNTY | S is a player character and the player's money ≥ its bounty | Bool |
| 68 IS_INDOORS | S's building handle is set (slot 59 `FUN_1405ce020`: its carrier's, its bed's or cage's building, else the one it stands in) | Bool |
| 69 HAS_ILLEGAL_ITEM | members of S's squad that are S or that Me perceives (`FUN_140855960`) carry an item of the set at `+0x100` of Me's town's faction (else Me's faction) (`FUN_140745f70`, any quantity); each carrier found (its first such item) posts "{1} has been discovered smuggling {2}" and becomes the dialogue's target (`FUN_140671930`, [retargeting](#retargeting); Meitou: not yet); the result is the 0/1 found. **Observed** (KenshiLib 1ce4a16: Faction +0x90 `tradeCulture`, its `illegalItems` set at +0x68, the tree head 8 further): the set is the faction culture's `illegal goods`; base data: Holy Nation lists Hashish and the forbidden books its gate guards' lines name (DIALOGUE 52700, event 1; Shek gate guard 9553, event 1; Empire gate guard 9521, event 3) | Num2 on 0/1 |
| 70 USING_MY_TRAINING_EQUIPMENT | S's `+0x158` (a faction handle) is Me's faction; who sets it is **Unknown**, not built | Bool |
| 73 TOWN_LEVEL_CURRENT_LOCATION / 74 PLAYERS_BEST_TOWN_LEVEL | Me's current building (Character slot 15 `+0x78`) has a town (Building slot 77) whose faction is the player's: its `+0x388` ([plevel](factions-squads-towns.md)); otherwise false whatever the value (73) / the highest `+0x388` over the player faction's towns (`FUN_140928cc0`), −1 with none (74) | Num3 |
| 75 IN_A_PLAYER_TOWN | S in a player-owned town | Bool |
| 76 TARGET_CHARACTER_EXISTS | there is a subject | Bool |
| 77 IS_RECRUITABLE | false at a player head cap (**Unknown**), for a quantity ≥ 25 (**Unknown**), for law, trader, diplomat; military → !B; others B | Bool |
| 78 HAS_AI_CONTRACT | Me's AI's contract slot (`FUN_140507070` `+0x210`) is set | Bool |

## Effects

`FUN_140680560(dialogue, line)`, run when a line is said or a reply clicked, in this order:
1. `unlocks` (listed unlocked, **and the current dialogue locked**), `unlock but keep me` (listed unlocked), `locks`: the speaker's own
   lock map (the state `FUN_140680560` runs on: the owner's, or for a chained bubble line its speaker's, [chain](#ambient-conversations)).
2. `interrupt` stored as the pending event-1 dialogue.
3. `change relations`: each listed faction's relation **toward the player faction** += val0 ("Relations with {1} improved by {2}" /
   "decreased by {2}").
4. `crowd trigger`: runs when the line has one and not (event 74 and the line's parent has no parent of its own: the line `+0x230` is
   its parent, **Observed** from `FUN_140683f90`'s walk to the root; so only a direct child of a root is skipped in event 74, Meitou
   skips every event-74 line). Every member of the squad of the state's owner (`FUN_140791b10`: the speaker, [chain](#ambient-conversations))
   is started with `FUN_140683f90(member's state, target, root, 74, force 1)` unless it is down (vfunc `0x30`, `FUN_1405c9e10`: dead,
   unconscious, playing dead, ragdolled or carried), an animal (vfunc `0x248`) or in a bed (`+0x2f8` 1); **Verified** in `FUN_140680560`.
   No test for combat, being held, or a cage (`+0x2f8` 2): those members are started too. `force` (`FUN_140683f90`) skips the refusal for
   a member whose dialogue state is busy (idle byte `+0x148` 0): the state is reset without `FUN_140674830` (no counter) and the new
   root's first line replaces what it was saying. Whether the owner is skipped is not visible in the decompile (the member list is the
   whole squad); Meitou leaves out the owner and the speaker, and a player character (engine choices, with the per-member val0 %, not
   read here: **Unknown** where). **Verified** (probe): 93 uses, all val0 100 but two (50, 60).
   Meitou (`StartBubbles(force)`): a crowd member in the open player conversation is still refused; a caged speaker's bubble goes on.
5. `give item` to the target (val0 2..49 = stack count; "Received" + the item's name). **Verified** (probe): 45 uses, food, kits, notes.
6. `trigger campaign` (if the speaker's squad is not in one) at the target's home town or the player town nearest the speaker
   ([campaigns](factions-squads-towns.md)).
7. The DA effects in data order (below).
8. Non-player speaker squads only (`FUN_140273330`): `change AI` (`FUN_140272430`: the squad's packages replaced by that AI_PACKAGE,
   every member's AI reset, the package built with no target), then `AI contract` (`FUN_140272740`: that package run as the contract,
   val0 hours, aimed at the dialogue target). **Verified** (probe): `change AI` 190 uses (168 "Escaping slave- run away solo"),
   `AI contract` 169 (Bodyguard 1-72 h, escaping slaves 1 h, "contract Mercenary outpost guard" 48-192 h). Bodyguard's goals
   "Bodyguard mission target" (enum 45) and "follow mission target" (44) target SQUAD_MISSION; its `contract end talk passive` is said
   on contract end (event 30).

A recruit into a full player squad ends the conversation with "It looks like your squad is a little full." and skips the rest.

### Recruiting

DA 3 / 18 with a player-faction target (**Observed**, `FUN_140680560` → `FUN_140692820` → `FUN_1407ff8b0`; a non-player target's
squad simply takes the speaker in, unless a target flag at +0x1d0 is set):
- **Cap**: refused (the "little full" line) when the player faction already has CONSTANTS `max faction size` characters (30 in the base
  data). The three CONSTANTS limits sit side by side: `max squad size` (20), `max squads` (10), `max faction size`; the HUD's
  "{1,num}/{2,num}" counters show the faction and squad counts against the first and last.
- **Which squad**: the character leaves its platoon and the player faction's member add (`Faction::vfunc_5` → `FUN_1406b9c60`) puts it
  into the player's active squad (`FUN_1407f5520`, **Verified**: the cached squad `+0x2a8`, else the platoon of the first selected object,
  else the faction's first platoon `+0x218` (**Observed**; Meitou: first by squad index, then id), or its second when the first is the squad of the dead, the handle at `+0x2c8` that
  `FUN_1407f4960` fills with `__DEAD_SQUAD__` (**Observed**: the handle compare is vfunc 8); none when there is only the dead one. The
  talker's own squad is never a fallback. Meitou: `DialogueSystem.ActiveSquad`, the dead squad is never in `SquadManagement.PlayerSquads`), or, when that squad
  already holds `max squad size` members, into a new platoon of the SQUAD_TEMPLATE "blank squad" (`FUN_1407f4420`: made, added to the
  faction's platoon list at +0x210/+0x218). No `max squads` check there.
- **Before the loop**, when the active squad's count plus the recruits exceeds `max squad size` and the faction already has `max squads`
  platoons, `FUN_1407f4420` also makes one platoon of the active squad's template (disassembly `1407ff928`-`1407ff978`); the recruits
  still go through the rule above, so that platoon stays empty (**Observed**, static). Meitou: `DialogueSystem.ExtraSquadAtTheCap` (also with the faction under its head cap, as `FUN_1407ff8b0` tests; **Verified** by the decompile).
- **Each recruit**: speed mode set to 2 (RUN; the same field DA 23 writes), NPC class (`+0x1a0 -> +0xdc`) set to 9 ADVENTURER (the
  value every player-faction character gets when made, `FUN_14062b210`); a character without its own dialogue package (+0x248) gets
  its record's `dialogue package player` (none: the player faction's `dialog default`) as its dialogue; every armour piece it carries
  loses its `uniform` faction (Armour+0x1e8, [economy](economy.md#5-runtime-unit-price-item-virtual-slots-81-and-82) prices uniforms at 0.3x).
- DA 3 then opens the character editor on the recruits (mode 1, [characters](../characters.md#character-editor)) and selects the first.
- **Verified** (base data scan, 2026-10-18): 332 DIALOG_ACTION records of DA 3 / 18, used by 250 lines in 125 dialogues (hire offers
  such as "hire me 2500", freed prisoners and escaped slaves asking to join, unique recruits such as Crumblejon), reached from mercenary
  and unique CHARACTER records and dialogue packages (drifters, beggars, bandits).

| DA | Effect |
|---|---|
| 1 TRADE | ends the conversation, opens trade with the speaker whatever its squad stocks (**Verified**, `FUN_140680560` case 1 → `FUN_1409524f0`: it tests only that the speaker, the target and the speaker's purse holder (its platoon, else its faction's, `FUN_140791b10`) exist; [who trades](economy.md#who-trades)) |
| 2 TALK_TO_LEADER | a player target: the speaker's squad leader gets task 102 on the target |
| 3 / 18 JOIN_SQUAD_WITH_EDIT / FAST | the speaker joins the target's squad (player: recruited, its package becomes `dialogue package player`; 3 opens the editor, [characters](../characters.md)) |
| 4 AFFECT_RELATIONS, 5 AFFECT_REPUTATION | speaker's faction toward the target's: relation += val0 (clamp ±100) / the speaker's faction's global trust += val0, no clamp, whoever the target is (slot 9, `FUN_1406b2530`) |
| 6 ATTACK_CHASE_FOREVER, 55 ATTACK_STAY_NEAR_HOME, 57 ATTACK_IF_NO_COEXIST | the speaker notices a character target (`FUN_140858f40`), is stopped with its task queue cleared (`FUN_1405c8330`), its squad tags the target TEMP ENEMY (`FUN_1406744a0`); task 101 (55: 248) goes to the speaker alone (`FUN_1405d20d0` without its squad fan-out): mates join only through the tag and their own enemy scan (**Verified**, `FUN_140680560`); 57 only without coexistence |
| 7 GO_HOME | its tasks cleared, task 19 with the target, the squad tags the target AGGRESSOR |
| 8 TAKE_MONEY | the target's faction pays val0, no balance check ("Paid c.{1}") |
| 9 GIVE_MONEY | the target's faction gets val0 |
| 10 PAY_BOUNTY, 30 ARREST_TARGETS_CARRIED_PERSON, 49 CLEAR_BOUNTY | [economy 11.4-11.7](economy.md#114-claiming); 30 only when the speaker has a town and the target is in it; 49 erases two ledger entries (the speaker's and the target's town's or jailer's booking faction) |
| 29 ARREST_TARGET | target in the speaker's town and not an animal: the speaker gets tasks 240 (capture) and 112 (find a cage), the target squad remembers the speaker (flag 5); else the speaker squad's memory flag 4 and task 4 (attack) on the target. One predicate picks both (the speaker's ownership town is set and equals the town he stands in, and the target's vfunc `0x248` is 0), so an animal in town is attacked, **Verified** (`FUN_140680560` case 0x1d). Meitou: `ArrestSystem.CapturesInTown` (`InOwnTown` and no animal; engine reading of the town test) is asked once by `DialogueSystem` and drives the memory and `ArrestFromDialogue` alike. **Verified** (probe): 176 uses ("You're under arrest!", "Thief!") |
| 11 CHARACTER_EDITOR | ends the conversation, opens the editor on the target |
| 12 FORCE_SPEECH_TIMER | the second speech timer (`+0x234`) = val0 ([Timing](#ambient-conversations)) |
| 13 DECLARE_WAR, 14 END_WAR | the speaker's faction's table, slots 8 / 7, about the target's ([factions 3.4](factions-squads-towns.md#34-changing-relations-observed-the-event-table-cross-checked-with-the-dialogue-action-numbers)) |
| 15 CLEAR_AI | the speaker platoon's AI object: `+0x130` = 1 and four lists emptied (what they hold **Unknown**) |
| 16 FOLLOW_WHILE_TALKING, 17 THUG_HUNTER | orders 164 ("Following and talking": `Task_FollowAndTalk` only changes the vtable of the shared follow task `FUN_140333400`, so it follows with the stand-off 20 to 40 on the follower's side and the follow speed profile, [pathfinding](pathfinding.md#following), **Verified**; Meitou starts the movement follow task, `TalkFollowers.StartFollowing`, and stands the speaker when the conversation ends), 165 ("Stalking", `Task_TownStalker`) given to the speaker on the target. 165 (**Verified**): in a town it waits 300 outside the town's first gate (gateway or wall with gate, outside gate code not > 0) on the line from the centre, facing the target, while the target is no farther from the centre; then (or outside any town) follows it and once sends event 26; done when both dialogues are free after that. Meitou: `TownStalkers.StepStalkers` |
| 34 RUN_AWAY | its tasks cleared, task 280 from the target, the squad tags the target AGGRESSOR |
| 52 SUMMON_MY_SQUAD | every other member of the speaker's squad gets task 67 |
| 19 REMEMBER_CHARACTER, 20 FLAG_TEMP_ALLY, 21 FLAG_TEMP_ENEMY | [squad memory](#squad-memory): long-term tag 0 / short-term 3 / 4 on the target |
| 23 MAKE_TARGET_RUN_FASTER | the target's speed mode = 2 |
| 25 TAG_ESCAPED_SLAVE | the target's slave state 2, its AI reset (9 uses) |
| 53 REMOVE_SLAVE_STATUS | a target in shackles or slave state: "Free!" floated in green over it and `Character_Unlock_Success`; slave state 0, AI reset, worn shackles unlocked (2 uses) |
| 24 GIVE_TARGET_MY_SLAVES | the characters on the speaker platoon's slave list are let out of their cages (if imprisoned) and join the target's platoon (`FUN_14032e430`) (5 uses) |
| 47 SURRENDER_NON_HUMANS | members of the target's faction within 100 of the speaker whose race does not match the speaker's (`FUN_14042ecd0`: the same race or one of the member's RACE_GROUP) become slaves (`FUN_14032e590`: slave state 1, the owner handle stored) and remembered by its squad; the tests are only partly read (8 uses, Holy Nation) |
| 40 ENSLAVE_TARGETS_CARRIED_PERSON | slavery ([carry-loot](carry-loot.md)) |
| 28 SEPARATE_TO_MY_OWN_SQUAD | the speaker (the target when the speaker is of the player's faction), unless alone, gets a new platoon of its faction (`FUN_1407f4420`, the old one's template field `+0x108`) where it stands, its tasks cleared; the new squad copies the old one's memory (`FUN_1405e85e0`) |
| 32 ASSIGN_BOUNTY | bounty += val0 for the speaker's faction; without an amount, a speaker whose CHARACTER has `assigns bounties` (59 base records) books the target's pending crime (`Character::canAssignBounties` `FUN_1405a4470`, **Verified**); others do nothing |
| 33 CRIME_ALARM | the speaker marks the target hostile; the target's crime timer 30 s and "recently wanted"; with a town (speaker not hostile to its faction): Trespassing on a target with no crime pending inside the speaker's non-public home, then the [town alarm](economy.md#town-alarms) at the target for its pending crime ("Alarm raised", at most every 30 s), [economy 11.2](economy.md#112-how-a-bounty-is-gained) |
| 35 INCREASE_FACTION_RANK | the speaker's faction's rank +1 while below val0 (0: no cap) (**Verified** by code) |
| 36 LOCK_THIS_DIALOG | locks the current dialogue |
| 37 / 38 / 39 ASSAULT / RETREAT / VICTORY_PHASE | the speaker's campaign phase: 1 (2 without a pt2 leader package), 2, 3 ([campaigns](factions-squads-towns.md#campaigns); 39 has no use in the data) |
| 41, 42, 43, 48 CHOOSE_* | the character trading window (slaves sold / bought, bail, animals; val0 price override), the speaker as the slaver; 41 / 42 do nothing when the target is the speaker ([economy 12](economy.md#12-slaves-prisoners-animals)). Base data: "trade slaves" (slaver bosses) and "Wandering slave trader" carry 41 and 42, Rebirth's boss 41, "Empire pro-slave" 42 with val0 1000 (**Verified** by probe) |
| 54 OPEN_NEAREST_GATE | the speaker's current town's gate nearest it (town slot 97) has its door opened (`FUN_140297040`) |
| 56 MASSIVE_ALARM | in a town: every member of every active squad of the speaker's faction perceives the target, tags it TEMP ENEMY (`FUN_1405cb0a0`), fires event 66 at it and gets task 29 to its position |
| 58 KNOCKOUT | speaker `+0x4f8` = val0 (meaning **Unknown**; 1 use) |
| 22 (empty), 26, 27, 31, 44, 45, 46, 50, 51, 59 | nothing in this executor |

## UI

**Verified** (layout files).
- `Kenshi_ConversationPanel.layout` (layer Middle, full-screen root, `position_real`): `NpcPanel` (Kenshi_FloatingPanelSkin, 0.25,
  0.6019, 0.4844 x 0.1389) with `NpcPortrait`, `NpcName` and the `NpcDialog` EditBox in a Kenshi_GenericTextBox frame;
  `PlayerPanel` (0.3542, 0.75, 0.2891 x 0.2519) with `PlayerPortrait`, `PlayerName`, the `PlayerDialog` ScrollView (choices),
  `MoneyLabelText` / `MoneyAmountText` ("c. {1,num}"); eight Kenshi_ConnectBarSkin bars.
- `Kenshi_SpeechPanel.layout` (layer Dialog, no input): Kenshi_SpeechPanelSkin (0.2344 x 0.0222) with `SpeechText` (Kenshi_WordWrapEmpty,
  centred, black shadow).

Code (**Observed**): the window (`FUN_140725740`, opened by `FUN_140727820`) names the speaker and the player character and renders
both portraits; choices use the MyGUI colours `DialogueTextOption` / `DialogueTextOptionHover`; the NPC frame grows to its text (Meitou keeps the layout's frame and scrolls a longer line under the EditBox's bar). The
bubble (`FUN_140725f30`) uses Kenshi_StandardFont_Large or _Medium, sits 4 units above the "Bip01 Head" bone, and fades from opaque at
1000 to gone at 2500 units from the camera, the distance ×0.5 for large-font events, ×0.75 for the rest of the
[interrupting set](#firing-an-event), ×1 for the others; an interrupting bubble is clamped inside the screen, left and top edges winning (`FUN_140672320`,
`FUN_1407221c0`, flags `+0x149` / `+0x14a` set by `FUN_140683f90`; constants **Verified** from the exe). Meitou draws it as that layout
(`PlayerInterface.AddSpeech`): the skin and `DialogueBubbleText` #e6e6e6 with a black 1 px shadow, fitted to the wrapped text (the original's sizing is **Unknown**).

## Unknowns

T_WHOLE_SQUAD fan-out; how a childless closing line is shown in a player conversation; `isDialogDelivered`; where `crowd trigger`'s %
is read; DA 44-46; the KO event mask; the interjector pick rule; DC 77's cap and quantity; what DC 21/22's rating and DC 39's
counters hold; DA 15's lists, 58's field; the speech timer's unit; the unnamed short-term tags 1 and 5 (in the grudge mask).

## Meitou design

As built: [simulation](../simulation.md#dialogue). Speech bubbles (`DialogueSystem.Ambient`, `SpeechBubbles`): ambient lines are said as bubbles over the speaker's head, 2 + 0.12 x length seconds apart, the chain passing between speakers as in the original (the target's lines said by the target, interjectors by interjectors; a non-monologue root stops after one line). Started by the campaign announcement (event 2), crowd triggers (event 74), a contract's end talk (event 30), `DialogueEvent` commands from other systems, and a sight stand-in (`DialogueSettings.AmbientEvents`, on in the game, which also gates the commands' event lines): every 30 ticks an awake, free NPC says event 66 (its faction hostile to the player's), 67 (ally) or 3 (else) to the nearest player character it sees by the [sight test](perception.md#sight) (`SightMap`; a sneaker with its stealth but no spot meter) or that a squad mate sees within 2 R ([shared sightings](perception.md#sight)), so a squad notices the player at sight range (1500 in daylight) and a leader holding the hold-up lines speaks when any member sees them; the nearest NPCs look first; or, when it said a line to it in the last half game hour or none passes now, event 3 to the nearest NPC of another squad within 260 (DC 16's talking range), only within 600 of a player character; at most 2 per look; a squad's half-hour cooldown per target starts only once a line is said, so a failed `chance temporary` roll is tried again next look, as the original's perception fires the event on every update (the Red Sabres' attack root is 20%) (engine choices: the original's perception tests are only partly read, and NPCs talk to each other only near a player character, where they can be heard). **Observed** (headless probe, base data, idle squads facing a standing player in daylight): Dust Bandits, Red Sabres, Swamp Ninjas and Starving Bandits start their hold-up or attack lines at 1000 and 1400 units.

Engine choices: a crowd trigger leaves out the owner, the speaker and player characters and rolls its val0 % per member; items that do not fit are dropped at the target's feet; contracts and `change AI` start on the next tick, and a contract's goals aimed at the squad mission follow the employer as squadmates follow a leader; DA 29 is an arrest without a fine in the speaker's town and an attack elsewhere (`ArrestSystem`); DA 53's "Free!" is a bark, not a green float. Events fired from other systems: 18-22 (first aid), 13 (shackles freed, a cage's lock picked), 41-43, 8, 17, 34, 57, 58 (the first witness of a pending crime, `CrimeSystem`), 70 / 71 (a severed limb), 23 / 24 (seen on the ambient cadence: a character down at the last look and up now; 24 when fighting or a hostile awake character is within sight), 60 (an NPC gives up a chase, at its target, [combat](combat.md#leaving-a-fight)), 46 (a jailer's release round), 44 / 45 (the police's spotting; the fine, the bail list and the surgeon's editor are the data's lines, [simulation](../simulation.md#arrest-and-jail)); DA 33 CRIME_ALARM raises the town alarm, which the investigators answer ([economy](economy.md#town-alarms)). DA 2: the leader walks up (`ApproachQueue`) and opens the event-1 conversation within talk range, quietly when it has nothing to say; after 25 game minutes it gives up with event 60. DA 47 compares RACE records only (sub-races not read). DA 24's slave list is the speaker squad's members in slave state; they keep it, owned by the target's faction. DA 41 / 42 end the conversation and open the slave window as it closes ([simulation](../simulation.md#arrest-and-jail), which also fires event 51); DA 48 opens the animal-buying window ([animals](animals.md#buying-animals)). Contracts and `change AI` are saved on the PLATOON (`AiSave`, [save](../formats/save.md#npc-platoons-meitou-mapping)); a wild platoon's are not. Squad memory (`SquadMemory`, hashed, saved on the platoon, kept by a platoon whose squad leaves the world; [Squad memory](#squad-memory)): written by event-1 ends, DA 19-21, 6/55/57, 7, 29, 34, 47
(each enslaved one's squad remembers the speaker, tag 0: the decompile hides the tag), read by DC 18, 20, 23, 52-55; `AggroSystem` reads TEMP ENEMY / ALLY as `FUN_14079c950` does (a squad with a TEMP ENEMY tag scans even when its faction has no enemy present, so a tagged character of a friendly or neutral faction is attacked, and a TEMP ALLY one is spared),
and the deeds and witnesses of factions 3.5 do not write tags yet. DA 34 is one run of 2000 + 400 x random units from the target at full speed, DA 7 a walk to the squad's
home building, else its town, else its home centre, DA 52 a run to the speaker, DA 16 a walk after the target re-aimed every 15 ticks beyond 40 units while the speaker's
conversation lasts; then the AI takes over (engine readings of tasks 280, 19, 67, 164). DA 28: a new NPC squad keeps the old one's template and home radius, no town, and gets a
fresh AI mind. DA 56: loaded squads only, an attack order instead of task 29. DA 54 opens every door of the nearest gate
building. DA 13 / 14 use `PlayerRelations.DeclareWar` / `EndWar`. "Regards as friendly" leaves out the uniform disguise. DA 5 / DC 3 use the faction global trust (`FactionRelations.GlobalTrust`, saved as `meitou global trust`); DC 12 reads `ResearchState.TechLevel`, DC 74 the highest `PopulationSystem.TownLevel` over the player's outposts, DC 73 that of the outpost within the player-town extent of the speaker (engine reading of "current town"). Not built: DA 15, 17,
39, 58; DC 21/22, 39, 49, 57, 58, 70 (inputs Meitou lacks: the strength float, perception counters, wall tests, training equipment; the decompile facts are in the table above);
event 25, 31 (betrayal), 36 from the witness function, the witness cases' extra tests.

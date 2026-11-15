# Dialogue

## Data

**Verified** by probe: 293 DIALOGUE_PACKAGE, 1890 DIALOGUE, 22783 DIALOGUE_LINE, 21135 DIALOG_ACTION, 289 WORD_SWAPS records.

### Who has which dialogue

`FUN_14062b210` (character setup) and `FUN_1407ff8b0` (player characters):

| Character | Packages |
|---|---|
| any | CHARACTER `dialogue` (161 uses): each DIALOGUE goes straight to event 1 (fcs.def says the same, **Verified** text) |
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
The asleep test shifts the mask by the event's low five bits, so 43, 55, 56 and 57 pass too (**Verified**, same function). Meitou
(`DialogueSystem.SleepsThrough`, asleep as `CarrySystem.Asleep`): a sleeper fires no other event, is offered no Talk and opens no trade, so a
shopkeeper in bed neither greets a customer nor trades (#582).

Event 1 starts a [player conversation](#player-conversation), using a stored `interrupt` DIALOGUE instead of the event-1 list when one
is pending. Every other event starts an [ambient conversation](#ambient-conversations).

### Score

Computed at load: +1 speaker not T_ME, + `score bonus`, +2 `locked`, +2 for each non-empty `in town of` / `my faction` /
`target faction` / `target race`, +2 when the first `target has item type` reference resolves (**Verified**, `FUN_14067b070`: only that one is read), +2 each `target has item` and each `world state` entry, +1 each condition
(+1 more only for a required tag condition, DC 23 / 26 with val0 1; a forbidden one scores 1, **Verified**, `FUN_14067b070`), +1 `chance permanent` below 99, +1 `target is type` above 0, +2 interjection node, +1 speaker 3-6. At
choice time +2 when the target's race is in `target race` (`FUN_140671bb0`). A root's effective score is its own plus its best
child's (`FUN_1406ad9d0`).

### Line check

`FUN_140678f30`, all must pass, in order:
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

### Player conversation

`FUN_140684320`:
- Dropped unless the player character is within 9216 units of the camera node (inferred as the camera).
- Plays Wwise `Initiate_Conversation`. Chooses the root and its best child, says it.
- Opens the DialogueWindow when the said line has passing children; **the window pauses the game** (clock speed 0, the previous speed
  restored on close, `FUN_140787fb0`).
- The talking player character is the one given the Talk order; walking there is that task's precondition. AI-to-AI talk range is
  260 units (DC 16).

**Last use** (**Verified**, `FUN_140671770`; said line `FUN_140675320`, clicked reply `FUN_140683c00`): the stamp is each node's `repetition limit` clock.
A line's parent (`+0x230`) is set by the builder (`FUN_14067d260` via `FUN_1406ad9d0`) only when the line's owner is root-like (a DIALOGUE, an interjection
node or a textless line, see Choosing a line), so a line under a text line has none, and a line shared by several roots keeps the last
one built. Meitou stamps the line and the conversation's root (`MarkUsed`): the same except for a shared line, or a deep line restamping its root, which
costs no game time while the window pauses the game. No parent link is kept, since the original's depends on load order.

**Choices** (`FUN_140680130`, `FUN_140683c00`): the said line's children are checked in list order (me = NPC, target = the player
character). If a passing child has speaker T_ME, a single `[...]` button continues to it and the scan stops; otherwise every passing
child is a button with a random text. Clicking one logs "<name>: <text>", plays `Dialogue_Choice` / `Continue_Conversation`, marks the
reply used, **runs its effects**, then chooses and says the reply's best child.

### Ambient conversations

`FUN_140683f90(dialogue, target, line, event, force)`: a monologue root outside event 1 shows a **speech bubble**, no window; a root
says its best child (as in Choosing a line), any other line is said as given. Large font for events 2, 11, 17, 26,
31, 34, 36, 40, 45, 74 (`FUN_1406713a0`, **Verified**), medium for the others.

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
- **Readers**: DC 18 (`FUN_140673290`: hours since the time, **9999.99** when never noted), DC 20 (an entry exists), DC 23, DC 52
  (long & 0xc), DC 53 (long & 0x2b0), "regards as friendly" (`FUN_1407923d0`, Character slot 126: same faction; not with TEMP ENEMY;
  yes with TEMP ALLY or love; else faction ally) and "regards as enemy" (`FUN_14079c950`, slot 125): TEMP ENEMY makes, and TEMP ALLY
  unmakes, an enemy across faction relations; `FUN_140599390` (a grudge: long & 0x7b2 or short & 0xb6).

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
| 13 NUM_DIALOG_EVENT_REPEATS | Me's counter for the current event | Num3 |
| 14 IS_IMPRISONED | S is caged | Bool |
| 16 TARGET_IN_TALKING_RANGE | same area as Me and within 260 | Bool |
| 17 IN_MY_BUILDING | S is inside Me's building | Bool |
| 18 TARGET_LAST_SEEN_X_HOURS_AGO | hours since Me's squad last noted S ([memory](#squad-memory)), 9999.99 never; `who` 0 asks about Me itself | Num2 |
| 19 IS_LEADER | S leads its squad | Bool |
| 20 MET_TARGET_BEFORE | Me's squad memory has an entry for S | Bool |
| 24 IS_ALLY / 25 IS_ENEMY | relation ≥ 50 (or allied, or own) / ≤ −30 and not own | Bool |
| 27 BROKEN_LEG, 28 BROKEN_ARM, 29 DAMAGED_HEAD (< 0.2), 30/42 NEARLY_KO (< 0.1), 41 IS_KO, 44 IS_DEAD | S's body | Bool |
| 31 IN_A_NON_PLAYER_TOWN | S in a town not owned by the player | Bool |
| 32 IS_RUNNING | S runs (Me: always B) | Bool |
| 33 COPS_AROUND | Me senses law enforcement | Bool |
| 35 SQUAD_SIZE, 37 NUM_BACKPACKS | S's squad: members / members with a backpack in the backpack slot (`FUN_1405cb320`) | Num3 |
| 36 IS_PLAYER | S's faction is the player's | Bool |
| 38 SQUAD_ONLY_ANIMALS, 43 SQUAD_IS_DOWN, 72 MIXED_GENDER_GROUP | S's squad | Bool |
| 40 / 47 BOUNTY_AMOUNT_PERCEIVED / ACTUAL | S's bounty with Me's faction (perceived: only while recognised) | Num2 |
| 45 IS_FEMALE | S's race is female | Bool |
| 46 CARRYING_SOMEONE_TO_ENSLAVE | S carries a living adventurer, bandit, slave or civilian. Carrying nobody, the result is `!(value > 0)`; carrying anyone, value 0 is always false (**Verified**, `FUN_140676950` case 0x2e; the slavers' "A new slave?" line needs the speaker at value 0, carrying nobody) | asymmetric |
| 48 IM_UNARMED | S has no weapon | Bool |
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
| 73 TOWN_LEVEL_CURRENT_LOCATION / 74 PLAYERS_BEST_TOWN_LEVEL | Me's current building (Character slot 15 `+0x78`) has a town (Building slot 77) whose faction is the player's: its `+0x388` ([plevel](factions-squads-towns.md)); otherwise false whatever the value (73) / the highest `+0x388` over the player faction's towns (`FUN_140928cc0`), −1 with none (74) | Num3 |
| 75 IN_A_PLAYER_TOWN | S in a player-owned town | Bool |
| 76 TARGET_CHARACTER_EXISTS | there is a subject | Bool |
| 78 HAS_AI_CONTRACT | Me's AI's contract slot (`FUN_140507070` `+0x210`) is set | Bool |

## Effects

`FUN_140680560(dialogue, line)`, run when a line is said or a reply clicked, in this order:
1. `unlocks` (listed unlocked, **and the current dialogue locked**), `unlock but keep me` (listed unlocked), `locks`: the speaker's own
   lock map (the state `FUN_140680560` runs on: the owner's, or for a chained bubble line its speaker's, [chain](#ambient-conversations)).
2. `interrupt` stored as the pending event-1 dialogue.
3. `change relations`: each listed faction's relation **toward the player faction** += val0 ("Relations with {1} improved by {2}" /
   "decreased by {2}").
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
| 33 CRIME_ALARM | the speaker notices the target (the forced sense `FUN_140858f40` with its alarm flag: seen, alert 5 s; **Verified** `FUN_140680560`: no attack order and no squad tag, so the Hub's Holy Citizen crying "SKELETONS!" (DA 33 + 34) only runs, #648; Meitou `CrimeSystem.Alarm` → `AggroSystem.Alarm`); the target's crime timer 30 s and "recently wanted"; with a town (speaker not hostile to its faction): Trespassing on a target with no crime pending inside the speaker's non-public home, then the [town alarm](economy.md#town-alarms) at the target for its pending crime ("Alarm raised", at most every 30 s), [economy 11.2](economy.md#112-how-a-bounty-is-gained) |
| 35 INCREASE_FACTION_RANK | the speaker's faction's rank +1 while below val0 (0: no cap) (**Verified** by code) |
| 36 LOCK_THIS_DIALOG | locks the current dialogue |
| 37 / 38 / 39 ASSAULT / RETREAT / VICTORY_PHASE | the speaker's campaign phase: 1 (2 without a pt2 leader package), 2, 3 ([campaigns](factions-squads-towns.md#campaigns); 39 has no use in the data) |
| 41, 42, 43, 48 CHOOSE_* | the character trading window (slaves sold / bought, bail, animals; val0 price override), the speaker as the slaver; 41 / 42 do nothing when the target is the speaker ([economy 12](economy.md#12-slaves-prisoners-animals)). Base data: "trade slaves" (slaver bosses) and "Wandering slave trader" carry 41 and 42, Rebirth's boss 41, "Empire pro-slave" 42 with val0 1000 (**Verified** by probe) |
| 54 OPEN_NEAREST_GATE | the speaker's current town's gate nearest it (town slot 97) has its door opened (`FUN_140297040`) |
| 56 MASSIVE_ALARM | in a town: every member of every active squad of the speaker's faction perceives the target, tags it TEMP ENEMY (`FUN_1405cb0a0`), fires event 66 at it and gets task 29 to its position |
| 22 (empty), 26, 27, 31, 44, 45, 46, 50, 51, 59 | nothing in this executor |

## UI

**Verified** (layout files).
- `Kenshi_ConversationPanel.layout` (layer Middle, full-screen root, `position_real`): `NpcPanel` (Kenshi_FloatingPanelSkin, 0.25,
  0.6019, 0.4844 x 0.1389) with `NpcPortrait`, `NpcName` and the `NpcDialog` EditBox in a Kenshi_GenericTextBox frame;
  `PlayerPanel` (0.3542, 0.75, 0.2891 x 0.2519) with `PlayerPortrait`, `PlayerName`, the `PlayerDialog` ScrollView (choices),
  `MoneyLabelText` / `MoneyAmountText` ("c. {1,num}"); eight Kenshi_ConnectBarSkin bars.
- `Kenshi_SpeechPanel.layout` (layer Dialog, no input): Kenshi_SpeechPanelSkin (0.2344 x 0.0222) with `SpeechText` (Kenshi_WordWrapEmpty,
  centred, black shadow).

## Unknowns

T_WHOLE_SQUAD fan-out; how a childless closing line is shown in a player conversation; `isDialogDelivered`; where `crowd trigger`'s %
is read; DA 44-46; the KO event mask; the interjector pick rule; DC 77's cap and quantity; what DC 21/22's rating and DC 39's
counters hold; DA 15's lists, 58's field; the speech timer's unit; the unnamed short-term tags 1 and 5 (in the grudge mask).

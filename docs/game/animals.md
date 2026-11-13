# Animals

Original ANIMAL_CHARACTER76/race/CharStatsAnimal/body/weapon/animation research; evidence scopes
and fields retained. Animal records substitute for CHARACTER in squad animals/animals2 and saves.
[Stats](character-stats.md), [combat](combat.md), [population](factions-squads-towns.md).

## Stats, body and techniques

Race controls anatomy/blood/hunger/speed/footprint; forelegs are ARM parts. Age/scale/HP-mult and
animal-strength use animal factory/race factors, not unmodified human stats/body. Creature
WEAPON category/natural technique flags differ from human fists; no block for surveyed weapons.
Weapon-level references manufacturer. Attack-type can disagree with weapon category and was
not found as an exe string (**Observed** editor-only inference, not all-mod guarantee).
Bonedog/Garru/Bull/Leviathan/spider/crab and other creatures have distinct clips/materials/limits.

Animal faction/hostility/run-away ratio applies ordinary relations; smell/egg behaviours were not found
([below](#breeding-and-eggs)). Race carriable gate governs lifting (Leviathan excluded in
survey), not an invented mass threshold. The Skimmers faction (`56098-rebirth.mod`, not real) lists no relations and default 0, so it is
neutral to every town; its solo hunters (package `animal patrol aggressive`) attack through ATTACK_ENEMIES_AND_NEUTRALS, and
guards answer only through their protect goals ([combat goals](ai-tasks.md#combat-goals); **Verified** by probe of the merged base data). Death items ([below](#death-items)), backpack and natural weapon are record inputs.
Appearance uses animal mesh/fur/body scale; race-specific animation names and bounds matter.
Pose/LOD distances can scale by animal size in engine, not original scheduler proof.

## 1. Records

- **ANIMAL_CHARACTER** (type 76) is the animal's record, used where a CHARACTER would be (squad template `animals`
  / `animals2`, [factions-squads-towns.md 5.1](factions-squads-towns.md); saves name it as the character's
  `target`, [save.md](../formats/save.md)). The install's load order has 43 of them (**Verified**, counted).
- Fields (**Verified**, the union over the 43): `combat stats`, `unarmed stats`, `stealth stats`, `ranged stats`,
  `strength`, `stats randomise` (as on CHARACTER), `animal strength` (int), `HP mult`, `scale min`, `scale max`,
  `lifespan`, `smell blood`, `smell eggs`, `turn rate`, `attack type`, `female chance`, `inventory w/h`, the
  bounty fields, `unique`, `faction importance`, `sounds`; references `race` (every record, value 100), `weapon`,
  `weapon level`, `stats` (only the Leviathan), `faction` (only the Landbat), `animations`, `strafe anim`,
  `turning anim`, `death items`, `backpack`, `AI Goals`, `bird attractor`.
- **Observed** (merged data of the owner's 662 mods, `meitou-tools characters`): 4 of 200 animal records with a race list give every entry chance 0 (mods'
  single-race animals: Great Beak Thing, Whistler, Pack Bull, Oddly Friendly Great Beak Thing; one has no chance above 0 and one RACE entry). Meitou took the first entry with a
  chance above 0, found none and built no look: the animal was invisible (a blue square far off). Now `CharacterAppearance.AnimalRaceId`: first entry with a chance above 0, else the
  first entry. **Verified** (decompile, `Faction::chooseARace` `FUN_1407ff1a0`, whose `param_2 + 0x50 == 0x4c` test is the ANIMAL_CHARACTER type 76): the weighted map is built by
  `FUN_1409b9400`, which adds only entries with a value above 0; with the map empty and the list non-empty, `FUN_1400b64b0(record, "race", 0, ...)` returns the list's entry 0
  (no draw), and only if that does not resolve does it fall back to the faction's races. No race at all: Greenlander by id (the person rule). A character with no look draws as a faction-coloured square (`GameHost.DrawMarkers`),
  so a failed look is logged once (`warning   no appearance for ...`); an animal without a race now takes the Greenlander RACE instead of a square.
- The `race` is an ordinary RACE record (Beak Thing, Bonedog, Garru, Bull, Goat, Leviathan, Spider, Crab...), with
  the anatomy, blood, speeds and footprint of [character-stats.md "Races"](character-stats.md#races); the
  animals' forelegs are ARM parts ([character-stats.md](character-stats.md)).
- `weapon` is a WEAPON record of a creature `skill category` (9 ELEPHANT .. 20 GIRAFFE, [combat.md
  "Enums"](combat.md#enums-from-the-editors-decompile-facts)): `_dog type weapon` (10), `_giraffe type` (20),
  `_bull type` (20), `_elephant type` (9), `_garru` (17), `_gorillo` (16), `_duck type weapon` (15), `_spider
  type` (13), `_robot spider type` (12), `_skimmer` (13). None can block. Lengths 18 (duck, small spider) to 120
  (elephant). Swamp Turtle and "Pack Beast Dead" name no weapon (**Verified**, dump).
- `weapon level` is a WEAPON_MANUFACTURER: "Ancient" on all but the Skimmer ("Unknown") (**Verified**).
- `attack type` (an int in the creature category range) does **not** always match the weapon's category: Wild
  Bull 11 with a category-20 weapon, Cage Beast 14 and Landbat 19 with the dog weapon (10). The string "attack
  type" does not occur in `kenshi_x64.exe` (**Observed**: absent from the binary's string table), so the game does
  not read it; it is an editor-only field.

### 3. Body

`CharacterAnimal` (**Observed**, vtable `1416f2208`):

| Character slot | Human | Animal | Used by |
| --- | --- | --- | --- |
| 0x238 (`vfunc_71`) | 1 | `HP mult` (`+0x6dc`; read by `14062f000`, 1 when 0 or below) | part body scale (+0x5c), blood capacity |
| 0x250 (`vfunc_74`) | the humanoid stats object | null | blood capacity uses s = 1; the hit path cannot sever limbs |
| 0x390 (`vfunc_114`) | 1 | lerp(`+0x700`, `scale min`, `scale max`) | part HP multiplier (+0x58, rewritten each tick), blood capacity, hunger rate |
| 0x398 (`vfunc_115`) | 1 | `+0x700`, the age ([Ageing](#ageing)) | the stats multiplier `+0x34`, the save's `age` |

### 7. Animations

| Races (records) | Skeleton (bones) | Movement | Idles | Down | Attacks (techniques) | Unused |
| --- | --- | --- | --- | --- | --- | --- |
| Bonedog, Boneyard Wolf | dog (42) | **walk** 1.1, **run** 0.67 | **idle2** 5.97, **idle3** 6.0, **idle noloop** 2.53 | **gotosleep** 0.83, sleep 2.0 | attack1 0.77, attack2 0.83 | eat (listed) 2.63, strafeleft |
| Beak Thing | mammal_beak (52) | **walk** 1.87, **run** 1.07 | **idle1** 8.0, **idle2** 10.2 | **gotosleep** 3.17, **sleep** 5.67 | attack1 5.0, attack2 5.0 | mouthclose, mouthclack, eat (listed), strafe/turn left/right |
| Goat | goat (52) | **walk** 1.33, **run** 0.67 | **idle1** 11.3, **idle2** 15.3, **idle3** 12.3 | gotosleep 1.1, sleep 1.33 | attack1 0.97 | strafeleft/right |
| Garru | pack_beast (44) | **walk** 1.07, **run** 0.53 | **idle1** 3.67, **idle2** 3.67 | gotosleep 1.17, **sleep** 5.23 | attack1 0.77, attack2 0.83, attack3 2.83 | strafeleft |
| Bull, Bull (domesticated) | bull, bull_nohorns (37) | **walk** 1.6, **run** 0.67 | **idle1** 15.9 | **gotosleep** 1.47, **sleep** 2.57 | attack1 2.23, attack2 1.47 | strafeleft/right |
| Leviathan | whale2 (44) | **walk** 2.17 | **idle1** 15.0, **idle2** 12.0 | gotosleep 2.03, sleep 2.17 | attack1 2.43, attack2 1.63 | strafeleft, turnleft |
| Big-Bones (Cleanser Unit) | Big-Bones (44) | **walk** 2.17 | **idle1** 15.0, **idle2** 12.0 | none | attack1 2.43, attack2 1.63 | strafeleft/right |
| Swamp Turtle | elephant_turtle (40) | **walk** 2.13 | **idle1** 15.0, **idle2** 12.1 | gotosleep 1.27, sleep 1.7 | attack1 2.37 | strafeleft |
| Gorillo, Black Gorillo | frog_gorilla (43) | **walk** 1.33, **run** 1.13 | **idle1** 1.33, **idle2** 3.0 | none | attack1 1.2, attack2 1.03, attack3 1.33 | eat (listed), strafeleft |
| Crimper | Crimper (43) | **walk** 1.33, **run** 0.68 | **idle1** 1.33, **idle2** 3.0 | none | attack1..3 as the gorillo | eat (listed), strafeleft |
| Swamp Raptor (Megaraptor), River Raptor | swamp_duck, swamp_duck_nospikes (59) | **walk** 1.0, **run** 0.67 | **idle1** 6.33, **idle2** 3.83 | gotosleep 1.33, sleep 0.67 | attack1 1.27 | eat (listed), strafeleft |
| Spider, Small Spider | flesh_insect (58) | **walk** 2.67 | **idle1** 1.33 | **gotosleep** 1.33, **sleep** 0.67 | attack1 1.83 | eat (listed), strafeleft/right |
| Crab (Barnabus) | Crab (36) | **walk** 2.67 | **idle1** 1.3 | none | attack1 1.83 | strafe |
| Robot Spider Worker / Guard | robot_worker (36) | **walk** 2.67 | **idle1** 0.03 | none | attack1 1.83 | strafeleft/right |
| Land Bat | land_bat (52) | **walk** 1.33, **run** 0.67 | **idle1** 11.3, **idle2** 15.3 | none | attack1 0.97 | idle3, eat (listed), strafeleft/right |
| Cage Beast | cage_beast (41) | **walk** 1.6 | **idle1** 15.9 | gotosleep 2.5, sleep 1.5 | attack1 1.4 | run 0.67, strafeleft |
| Skimmer | skimmer (32) | **walk** 2.67 | none: its `idle1` record names a clip the skeleton lacks (it has `idle01` 0.17) | **sleep** 0.17 | **attack1** 1.83 (its `eat (attack1)` record too) | strafe |

Clip filter (`FUN_14051d660`, **Verified** for the strafe list, Inferred for walk and run): a clip with `stealth mode` 1 is kept only while the animal sneaks; animals never sneak (the stealth flag is written only by the Character sneak setter), so such a clip is never picked. Meitou drops it in `AnimationLibrary.BuildAnimals`.

## Death items

The meat, skins, teeth (animals) and cores/CPUs (robots, Gurglers) a body yields. Not dropped at death: they are in the inventory
from the start.

- `death items` (ANIMAL_CHARACTER and CHARACTER) lists ITEMs, value 0 the quantity, the others 0 in the install (**Verified**,
  merged data: 43 animals, e.g. Bonedog 1 Animal Skin / 4 Animal Teeth / 3 Raw Meat; Pet Dog, Settler's Pup, Pack Beast Dead none).
- Added when a new character's inventory is set up, after the `inventory` list (`Character::vfunc_88` `FUN_140622e80`, called last
  by both setup functions, not for an inventory loaded from a save); entries below 1 skipped; each item gets the byte `+0x138`
  set (**Observed**, decompile). An emptied inventory loads empty (**Observed**, original: a player's Bonedog saved with an empty INVENTORY_STATE was
  loaded and saved again with nothing; Meitou `SavedCharacters`, an empty state is the whole kit). `FUN_140622e80` adds them only when its second argument is 0; the animal setup `FUN_14062a130` passes its own second argument
  through, and the squad factory's spawn (`FUN_140581770`) calls the setup slot `0x2c0` with a request field (**Observed**). What that argument is is
  **Unknown**: its `+0x20` / `+0x2c` hold a position and rotation copied onto the request, so it reads as a saved state the character is made from (**Inferred**).
  The player's own animals have them too (**Observed**: a new game's start squad, Bonedog, Pack Beast and Iron Spider, saved by the original holds them,
  flagged; out of sight in the window, below).
- `+0x138` is the saved item key `death` (read/written by `FUN_140761550` / `FUN_1407609e0`) (**Observed**). In saves these items
  take ordinary grid cells beside carried goods, each entry one stack of its whole quantity whatever the item's `stackable`
  (Electrical Components 4, Iron Plates 6, `stackable` 1), and a stack of the same record without the flag stays a separate stack
  (**Verified**, METARU and quicksave platoon files: 144 / 187 flagged items, none overlapping).
- Item lookups by type (`FUN_1407465d0`, used by `Task_grabOneFood` and `CharacterAnimal::vfunc_94`) skip flagged items while the
  owner is alive (`+0x5bc`) (**Observed**); a dead owner's count (**Verified**, `FUN_140748d70` and `FUN_1407465d0` read the owner's dead byte), so a squad mate eats from a dead mate's backpack (`FeedSystem.Best`).
- Taking one out (`FUN_14075e7e0`, from the inventory's remove functions) clears the flag; when the owner is alive and down
  (`isDown`, [carry-loot.md](carry-loot.md): unconscious, playing dead, carried or ragdolled, so an awake carried body too) the owner dies (the death function) with "{1} has been butchered"
  (**Verified**, `FUN_14075e7e0`: not dead, then `isDown` or posture 3+, which `isDown` already covers; the flag clears either way).
- No inventory-window filter on the flag was found (**Observed**, absence in the decompile). The player's own animal's inventory window
  shows no main grid at all, so they are out of sight there: it ends under the data panel, without the grid or "-INVENTORY-" (**Observed**,
  original: a bought Garru whose save holds its skins and meat, and a dog with none; Meitou `InventoryLayout.TrimAnimalWindow`; its loot
  window keeps the grid).
- Death items weigh nothing for a living owner (**Observed**, original: a Garru pup with a 6 kg backpack and 14 flagged 1 kg meat/skin items
  shows Total Weight 6). Meitou excludes them from Total Weight and encumbrance, including worn bag contents. A dead owner's are **Unknown**.
  Unknown: `FUN_1405a59a0` (reads record 3979's `death items`).

Meitou: `ItemFactory.Build` adds them (`ItemInstance.Death`) to every new character, stacks merge only with the same flag, the feed search skips them,
moving an item out of its inventory clears it, and a loot take (or an item moved out of the open loot) that removes flagged units from a live down body (`CharacterFacts.Down`) butchers it.

## Ageing

`CharacterAnimal` keeps `lifespan` at `+0x6f8`, `scale min` / `scale max` at `+0x6f0` / `+0x6f4`, `smell blood` / `smell eggs` at `+0x6e8` /
`+0x6ec`, `HP mult` at `+0x6dc` (all read by `FUN_14062f000`, slot 0x378), the age at `+0x700` and the age of the last size refresh at `+0x6fc`
(the constructor `FUN_1405c8150` sets both to its age argument) (**Observed**).
- Getters (**Observed**): slot 0x398 the age; 0x388 sets it (only the save loader `FUN_140626f50` calls it on an animal); 0x390 the size
  lerp(age, min, max); 0x3a8 `1 - (1 - scale min) x age` (consumers not traced); 0x430 / 0x438 the two smells (callers not traced).
- Growth (slot 0xe8, `FUN_1405cdf00`, run by the character update, **Observed**): while alive (`+0x5bc` clear) and age < 1.1, age +=
  `dt / 2618.18 / lifespan`, dt the update's elapsed time (`+0xcc`, the same value the base update passes on) and 2618.18 s the day length
  (`FUN_14066cd50`). So `lifespan` is the game days from age 0 to 1. When the age has moved more than 0.05 past `+0x6fc`, `+0x6fc` takes it and,
  below 0.95, the look's size is refreshed (appearance vfunc 0x30); slot 0x390 is read live by the body each tick ([Body](#3-body)).
  The update is lumpy in the original (8 characters a frame round robin for the logic tick, 6 for the full update with the accumulated time) and, while paused, one flagged character a frame still ages at 0.01 game-s a rendered frame (**Verified**, `FUN_140786a40`); Meitou steps every animal every tick and ages nothing while paused ([engine choices](../simulation.md#engine-choices-against-the-originals-frame-loop)).
- So the stats multiplier (= age) keeps growing to 1.1, the size to lerp(1.1). Spawn ages come from the squad template ([factions 5.1](factions-squads-towns.md));
  ages above 1.1 do not grow.
- Saves: GAMESTATE_CHARACTER float `age` = slot 0x398 (`FUN_1406280a0`), read back through slot 0x388 (**Verified**: 159 828 vanilla platoon
  records all have it, people 1, animals 0.00002 .. 2.07). Loading an animal below 0.99 rebuilds its look (`FUN_140627b30`).
- Age word (slot 0x3a0, `FUN_1408882b0`): below 0.4 "Pup", below 0.6 "Teen", up to 1.1 "Adult", above "Elder".

- `+0x6fc` is only the step gate of that look refresh (**Verified**: the decompile reads it only in `FUN_1405cdf00` and sets it in the constructor
  `FUN_1405c8150`). Gameplay reads the live age (slot 0x398, `FUN_1405e2010` returns `+0x700`): the mouth-food update `FUN_1405dc650`, the dog-play scores
  `FUN_140599720` / `FUN_140599800` and the limb finder `FUN_1409993a0`.

Meitou: `BodySystem.Age` grows `CharacterStats.Scale` (`CharacterCold.Age`, what gameplay reads) and resets the body's size each tick, `CharacterCold.ShownAge`
is `+0x6fc` (the drawn size only; it is not refreshed above 0.95, unlike the original), the save writes and reads `age`.

## Pack animals

- A new animal's inventory (`FUN_14062a130`, slot 0x2c0, **Observed**; the grid size **Verified**, :79-107): a 5 x 4 `backpack_attach` section, the main grid `inventory w` x
  `inventory h` (each below 1 becomes 4 and 8 on its own: w=6, h=0 gives 6 x 8; neither set 4 x 8), then every `backpack` list entry is made and added equipped, whatever its values (Pack Bull's
  entry is (0, 0, 0)); a saved inventory replaces this. Pack Beast and Pack Bull carry their CONTAINER this way.
- The worn backpack's combat terms apply to the animal as to a person (**Observed** for `combat skill bonus`: a Pack Beast wearing the Garru Backpack, -4,
  shows Attack 19 (-4) where its skill gives 23, and a Garru pup with attack 100 at age 0.18 shows 17 (-4); the other terms, `combat speed mult`, `stealth mult`
  and `athletics mult` are not separately verified, **Unknown**). Meitou applies the shared refresh through `CharacterAssembly.PackOf`.
- An animal with no backpack gets the naked bonus as a person with no armour and no backpack does (**Observed** for dexterity: at age 0.5 an Iron Spider with
  Dexterity 40 shows 22 (+2) and a Bonedog with 23.3 shows 12 (+1), 1.1 times the aged value, while a Pack Beast wearing its backpack shows the plain value; the
  dodge and stealth factors are not separately verified, **Unknown**). Meitou uses the shared refresh's 1.1 dodge and 1.2 stealth factors
  (`Fighter.Naked`).
- The main grid is shut while the animal lives (`CharacterAnimal` vtable slot 48 `FUN_1405cc630`, **Observed**): `main` is enabled only when the flag at +0x5bc (taken to be dead) is set,
  or the animal is not of the player's faction (`FUN_1407916d0`) and is down (vfunc 0x30); the animal window (`FUN_14014f570`) leaves a disabled `main` out. When slot 48 runs is
  **Unknown**. Meitou: `InventoryCommands.MainClosed`, drawn disabled; drops, right-click moves and pick-ups skip it; what is in it can still be taken out.
- The worn bag is drawn on the animal: Garru Backpack's `pack_beast_luggage.mesh` and rebirth.mod's Bull Backpack `bull_bags.mesh` are skinned to skeletons
  whose bone handles and names match `pack_beast.skeleton` and `bull.skeleton` (**Verified**, 44 and 37 bones compared), so they share the body's skeleton
  as a person's clothing does; the material is the CONTAINER's `material` (gar backpack). Meitou: `CharacterAppearance.BuildAnimal` with the bag in
  `backpack_attach`, one shared look per species and bag (`GeneratedAppearances.CreateAnimal`), swapped when the bag changes.
- Nothing else ties the bag to the squad: it is an ordinary inventory (feeding from squad mates reads it, [simulation](../simulation.md#items-and-eating)).

Meitou: `ItemFactory.AddAnimalBackpacks`.

## AI goals

The character's permanent goals (`FUN_140621b70`, called on creation and on every faction change `FUN_1405cbac0`, **Observed**):
IDLE (14) at 0.1, SELF_PRESERVATION (32, target itself) at 1.0, the FACTION's `AI Goals` (unless a platoon field, `+0x658 -> +0x78 -> +0xf8 + 0x210`,
is set; the player faction's lists "Protect allies"), then:
- not the player's faction (FACTION `+0x250` clear): the RACE's `AI Goals`;
- the player's faction: AQUIRE_FOOD_AT_HOMEBASE (243) at 0.5 unless present; an animal also gets its RACE's `AI Goals`, EAT_FOOD_ON_GROUND (259)
  at 0.5, and loses EAT_A_RANDOM_DEAD_BODY (230).

Animal races list their feeding goals (**Verified**, probe): goats, bulls, garru, turtles "eating food on the ground"; bonedogs and boneyard wolves
also "eating corpses" and the three limb games; beak things and spiders "eating any bodies"; raptors "eating crops". The ANIMAL_CHARACTER's own
`AI Goals` (Follow leader on Pet Dog, Settler's Pup, Cornelius) is not read: the exe's only references to the string are this function and the two
package loaders (**Observed**). 243's desire and what Meitou runs of these for the player's animals: [eating](character-stats.md#eating),
[simulation](../simulation.md#animals).

## Following

No goal makes an owned animal follow anyone (above). The player orders it like a person: the context menu offers "Follow" (verb 31,
STAY_CLOSE_TO_TARGET) on one of the player's own characters who is up, uncaged and not selected (`FUN_1407a6bc0`, beside "Inventory" 26 and
"Bodyguard" 45, **Observed**). Giving a 31 or 45 order or job runs the circle break `FUN_1405c8c10` (**Verified**; callers: `FUN_1405c8da0` after it adds the order or job, walking from the
ordered character, and `FUN_1405d1940` for a plain 31 order, walking from the target; never when a job later starts). It walks the followed chain: a character with a 31 order in its queue whose
front order targets the ordered character loses the whole queue, else the walk recurses into that front order's target; a character with any 31 job loses every 31 / 45 job targeting the ordered
character and the walk recurses into the targets of the other 31 / 45 jobs. Jobs 289 (an animal's companion) are never removed, and a character with only 45 / 289 jobs is not looked at. The follower moves as in [squads travelling together](pathfinding.md#squads-travelling-together) (a squad mate takes its formation slot, anyone else is kept 20..40 away).

Meitou: `FollowOrder` (`MovementSystem.Follow`) sets `CharacterCold.FollowOrder` and the follow task (the squad formation slot behind that character); a move or stop
order clears it; when the followed one is gone the follower stands (engine choice). `MovementSystem.BreakCircle` is the walk, run after a one-shot follow and after `AddOrderJob`
for 31 / 45 (a one-shot follow is `FollowOrder` unless a follow job is running; the walk is cut at 64 characters and never visits one twice, an engine guard).

## Buying animals

DA 48 `CHOOSE_ANIMALS_BUYING` (`FUN_140680560`, **Observed**; 12 DIALOG_ACTION records in the data use it, **Verified**), when the target is not the
speaker, opens the character trading window (`CharacterTrading_AnimalBuying` `FUN_1406a9440`, title "Buying Animals", confirm "Buy") on:
1. every animal (slot 0x248) within 3000 units of the speaker, of the speaker's faction, whose town (`FUN_1405ce5f0`) is the speaker's, not dead;
2. then the animals of the speaker's own platoon within 2304 units not dead and not yet listed.

Rows: price = (val0 if 1 or more, else 14000) x (age + 0.1), truncated. Panel 1 "Strength:" (stats vfunc 0x10) and "Melee Attack:"
(`FUN_140884ee0`; both read the age), "Gender:" Female / Male (appearance `sex female`, `FUN_140295a50`); panel 2 the name and "Age:" with
"<age word> (<age x 100>)".

Confirm (`FUN_1406aac20`, **Observed**):
- more ticked rows than free places (CONSTANTS `max faction size` minus the player's characters): message box "Character Limit Reached",
  "You cannot hire any more characters" / "You can only hire {1} more character(s)"; nothing is bought.
- each bought animal: its AI orders cleared, toughness, athletics, melee defence, melee attack, martial arts, dodge, stealth, mass combat and
  swimming x 0.25 (`FUN_1406aa730`), recruited as by DA 3/18 (`FUN_140692820`, [dialogue](dialogue.md#recruiting)).
- each item in the bought animals' backpacks goes into the backpack of the seller's platoon members (tried from the last), else is destroyed.
- the total is taken from the player's money (the backpack items already moved); then, only when at least one animal was bought, the character
  editor opens at once on exactly the bought animals, in mode 1 (in play; `FUN_1406e32f0(window, list, 1)` -> `FUN_1405f4dd0`), replacing any
  editor already open. **Verified** (decompile: the call follows the money write and is skipped when the bought list is empty; mode argument 1).
  The animal page ([characters.md](../characters.md#character-editor)) then lets the player name each one. The other mode-1 callers are
  DA 3 recruiting (`FUN_1407ff8b0`) and `FUN_1407f3670`.

Meitou: `DialogueSystem` `CharacterTradeKind.AnimalBuying`; the town test is `Squad.Town`; the confirm publishes a `DialogueRequestKind.AnimalEditor`
request listing the bought animals and the host opens the editor (`EditKind.Recruit`, the in-play kind) on them; the names are saved by the
name-only `EditCharacter` ([characters.md](../characters.md#character-editor)).

## Breeding and eggs

No breeding was found: "egg", "eggs", "pregnant", "mother" and "baby" are not strings of the exe (whole-string search), and the only `female chance` readers are the character
factories, which roll the look's gender (`FUN_140581770`, `FUN_1402e2b50`; **Observed**). `smell eggs` is stored and has a getter whose callers
were not traced. So the original has no egg laying, and Meitou builds none.

## Not found

The default level of the natural weapon (`14062aa60` makes it without a level argument), the consumers of `animal strength`, the smells and
slot 0x3a8; the movement rule of a Follow order.

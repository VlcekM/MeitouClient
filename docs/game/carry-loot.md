# Carrying, rescue and looting

Original Task_Lift/PutDown/PutIn, carrying/medical flags, inventory transfer and witness research.
Fields/source addresses and unresolved cases retained below. Runtime: [simulation](../simulation.md#carrying-rescue-and-looting).
Crime prices/ledger: [economy](economy.md); body/load: [stats](character-stats.md).

## Carry and release

**Verified** carriable race gate (53-race probe), left-arm usable medical+0x166, left shoulder/
Bip01 L UpperArm attachment. A collapsed, missing or crushed left arm prevents carrying ([arms](combat.md#arms)); right arm irrelevant.
**Observed** target must resolve character and differ from carrier. An already-carried target ends the lift (start `FUN_14034f990`, update `FUN_140354ff0`
when the carrier is someone else) unless the task's snatch byte `+0x79` is set: the task factory `FUN_14032ebd0` sets it for task types 213 Lifting/Snatching,
225 Lifting/Snatching Order and 240 Capture Target, not for 68 and 170; the link `FUN_14034fbe0` then unlinks the old carrier (`FUN_1405ce1e0`) before attaching
(**Verified** decompile). Meitou `CharacterCold.Snatching`: the player's pick-up order (not a kidnap) and a lift started with row 213 or 240 (`CarrySystem.BeginPickUp`; a dialogue capture, DA 29 in its own town, passes 240 and so takes a target another carries, `ArrestSystem` `Arrest.Capture`); the old carrier lets go as a drop (no put-down knock-out). AI down-state gate and player context-menu eligibility
are different: the menu offers Pick Up on a standing own character ([ui-input](ui-input.md#context-menu-verbs), **Observed**), never on a carried body (**Verified**, `FUN_1407a6bc0`): so a player snatch can only follow a body picked up by another after the order was given (**Observed**, inferred). Meitou's Ctrl + right click also snatches (engine choice). Race carry-bone affects attachment; secondary character type unknown.

Lift sequence/clip/approach, reciprocal partner handles, weapon/ragdoll/state changes and unlink
must stay ordered. **Verified** carry load is CONSTANTS30, not target's weight/race/inventory;
mode0 raises to1 without a separate speed-product carry factor. Dropping on arm loss/KO/death,
put-down and bed/cage placement are different state transitions. Bed/cage occupancy and release
must maintain both character and object links. Rescue target finders differ for squad/player/
NPC/leader and available safe beds; do not turn every pickup into kidnapping.

### Who carries

An animal never carries (**Verified**, decompile): `FUN_14060e840` picks the AI's task registry from its mode flags, whose start value is Character vslot `0x3f8`:
`CharacterAnimal` `FUN_1405e2020` returns 0x20 (the 'animal' registry), `CharacterHuman` `FUN_1405e59b0` 1 ('human'). Every carry row (105/148 rescue, 142 leader rescue, 132 kidnap,
254, 108 prison, 112/281 cage, 167/168 slaves, 182, 203/204 arrest and bounty, 238 foraging) is registered with flag 0x1 only (`FUN_140319a60`), so an animal cannot plan one
whatever its race `AI Goals` or package list (a mod's included); the context menu of an animals-only selection offers no Pick Up ([ui-input](ui-input.md)).
Meitou: every lift, put-down (69, 0x1) and loot (26, 177, 42: 0x45, no animal) tests its row against the repertoire (`TaskStart`, simulation); `CarrySystem.CanCarry`
is that test for every lift (the rescue and leader rescue passes, kidnap, town duty, capture and slave carry, foraging, errands and the player's order).

The lift rows (68, 213, 225, 240, 246, 170) carry condition `!26` on the carrier itself (**Verified**, `FUN_140599e90`: [state 26](ai-tasks.md)): a carrier staying low, playing dead or in a bed cannot start a lift (`CanCarry` tests `CharacterFacts.CrouchingOrLying`); a crawler (crippled) can.
### A carried body's position

**Verified** (`FUN_1405cf130`, the per-frame position sync, slot 28 `0xe0`, the same function for `Character`, `CharacterHuman` and `CharacterAnimal`; `FUN_1407d1440`, `FUN_1407d16f0`): for a carried character (`+0x3d4`) the position `+0x48` is not the carrier's but the ragdoll root body's position when the ragdoll controller is valid and either its whole-ragdoll flag (`+0x59`) is set, or the character is an animal (vfunc `0x248` `asAnimal` non-zero) and its carry flag (`+0x5a`) is set (a carried animal); otherwise the movement position. `FUN_1405d00b0` then pushes `+0x48` into the movement. Meitou (engine choice): a carried body stays at its carrier's place with the `carry me` pose; the body's own simulated root is not followed.

### A carried body's animation

**Verified** (`FUN_1405ced90`, `FUN_140520500`, `FUN_14051fc60`, `FUN_1405203b0`; writers of the animation category `+0xf0` found: 0, 4 swimming, 5 grounded, never 3): the body's side of the attach is made for every carried
body, **awake or not** (no consciousness test): it sets `+0x3d4`, clears the bed/restraint state, stops the layers (`FUN_14051c8d0`), and plays the record named `carry me` by name (`FUN_14051d510`, the character's
special action `AnimationClass +0x228`). The per-frame update `FUN_140520500` with that action set plays it alone (`FUN_1405203b0` by name each frame) and skips the movement, idle and combat picks, so
a conscious carried character holds the same `carry me` pose as a knocked-out one; category 3 (CARRIED) is only the record's own tag (the only record with `being carried`). Meitou: `AnimationSystem.Update` plays
`carry me` for any carried body (it did so only for a downed one before, so an awake carried mate fell through to the standing picks, none of which fits `being carried`, and showed the binding pose), and
a carried body plays no technique, stumble, lift or knock-out clip. Awake carrying exists for the player's standing squad mates (`CarrySystem.AwakeMate`).

### Lift and put-down timing

**Verified** (decompile; constants read from the binary). The lift task starts (`FUN_14034f990`) by playing the whole-body clip `shoulder lift <side>` on the carrier at
rate +1.0 (`FUN_1405203b0`; a clip plays at its record's `play speed`, which is **2** for `shoulder lift left/right`: the lift takes length / 2). Its update (`FUN_140354ff0`)
reads the clip position as a fraction (`FUN_1405b4460`, state `+0x54`): once past **0.4** and not yet linked it makes the link (`FUN_14034fbe0`: `FUN_1405cff90` links and calls the attach A carrier in deep water (water state 3, `FUN_1405c7fd0`) plays no clip and the update reads the position as 1.0, so the link is made and the task finishes on the first update (`FUN_14034f990` returns before the clip, `FUN_140354ff0`; `CarrySystem.Lift`).
`FUN_1405ced90`, the body plays `shoulder lift B` and the pick-up sound); the task is done at **0.95**. A link made at 0.4 persists whatever happens to the task. The update re-checks nothing about the carrier or the reach: it ends the task only when the target no longer resolves or another carrier holds it (and the task is not a snatch); arm, fight, down state and distance are the start's tests (`CarrySystem.Lift`).
The put-down starts (`FUN_14034a230`) by playing the same clip at rate **-1.0** and its update (`FUN_1403504f0`) releases the body when the position falls below **0.2**.
The put-down's **end handler** (`FUN_1403509f0`) always releases the body (`FUN_1405ce1e0`, ragdoll argument 1), stops both lift clips, and when the carrier does not regard the body as friendly
applies the put-down KO (`FUN_140644980`, 0.01 x the carrier's assassination clamped 0.1..0.4): so a put-down ended early (a hit outside a fight ends the current task, [combat](combat.md))
is finished at once, never restarted; a lift cut off before 0.4 plans again from its start. The update's own release (below 0.2, **Verified** `FUN_1403504f0`) applies that KO only when
the carrier does not regard the body as friendly **and** `FUN_14032e2a0` is false (the body is a chained slave, handle at `+0x330` set and byte `+0x320`, whose owner faction `+0x1a0 -> +0xe0`
is the carrier's), so a chained slave of the carrier's faction is set down awake; the end handler has no such test. Meitou: `CarrySystem.LiftPlaySpeed` (the record's play speed), `CarrySystem.Interrupted` (a hit) and the task handler's end (any other task replacing the put-down, or the Stop order) release the body the same way.

**Verified** placement (decompile `FUN_1405cda20`, `FUN_1405b5980`, `FUN_1405b1a30`, constants read): node position = carrier node position + carrier rotation x the attach bone's model-space position
(`FUN_1405b1ec0`) + the bone's world rotation x O; rotation = bone world rotation x 90 degrees about x x 14 degrees about z; O lerps by t = 2 x (Height - 80) x 0.025 from (-1.4, 0, 1) through (-1.9, -1, 2.6)
to (-3, -0.4, 5), not clamped (Meitou `CarryPose` matches). The body's node is its feet origin, so a carried body's head hangs down in front of the carrier and its hips ride on the shoulder.
Meitou draws the body on the carrier's attach bone as the renderer poses it that frame ([bone anchors](../character-renderer.md#bone-anchors-meitou-engine-choice): the simulation publishes the node in the bone's frame, `CarryAttach.OnBone`, for a person the offset O and the two fixed turns, for an animal what its pinned carry body gives). The simulation's own placement, on the carrier's **drawn** facing with the attach interpolated between ticks (`GameHost.CarrierYaw`, `MixCarry`), poses the ragdoll and draws the body while its carrier is not drawn. Measured (carry scene, a standing carrier): shoulder bone y 14.5, node y 17 to 20,
carried pelvis 8 to 16, head 14 to 16 above the carrier's feet; a crawling carrier's shoulder is at 3 to 5 and so is its body.

### A carrier in a fight

**Verified** in `kenshi_x64.exe`: no attack goal's conditions test carrying (goals 4, 5, 16, 61, 227 in the task table; only Assault 101, Attacking Squad 248 and the static guards test `!IS_CARRYING_SOMETHING`),
and the only combat-side read of the carrying flag (`+0x348`) is the steering update `FUN_140660dc0`, which lists the carried body with its carrier. The protect-allies desire
(`FUN_140599b90`, goals 15 / 288) is x0.5 while carrying and 0 in stealth. So a carrier retaliates and fights on its own with the body on its shoulder; weapon choice reads only the
medical arm bytes (not the carrying flag), and the carrying arm stays on the body because the carrying arm overlay plays every frame ([animation](../animation.md#a-carrier)). Other `+0x348` readers found in the
exe: squad-level "someone carries" tests (`FUN_140288630`, `FUN_14028fdf0`, a crowd-control count), the walk-to-run rule (`FUN_140342a30`: a carrier walking runs, Meitou `MovementSystem`), the lift and
put-down tasks, the FIND_BED desire and the Watching-slaves desire. **Verified** records: `carrying left arm only` (overlay, `override L arm`, every other bone deleted), `stand carrying left` (overlay, `override L arm`),
`walk/jog carrying left` (upper, `override L arm`), `sword ready (carry)` (upper idle, carrying left, one weapon in hand).

## Dropped item placement

**Verified** (decompile, `Item::createItemEntityCallback` `FUN_1407626e0`, run when an item's entity is made; an item put in the world with the pose-kept flag `+0x1d9` false, as every drop: the character's drop calls `FUN_1405ca4a0` / `FUN_1405ca740` pass the identity orientation and flag 0, whatever the caller's pose): the item's position and orientation are **set again there** by its record type (`+0x13c`), the dropper's orientation is not used. The position is raised 25, a ray goes down 99 (`FUN_1409b6a90`) and the hit height is the ground; no hit uses the terrain height (`FUN_1409b1f80`), else the unraised point. The up-to-ground-normal turn `Q` is `Vector3.UnitY.getRotationTo(normal)` (no yaw).

| Record type | Orientation | Height above the ground |
|---|---|---|
| WEAPON (2) | **planted**: the mesh turned about its own Z by `U(0.9, 1.1)` x pi (its +Y, the blade end, then points down, at most 18 degrees off vertical), then by `U(0, 2 pi)` about Y; the ground normal is not used | origin raised `U(0.5, 0.7)` x the mesh bounds' Y size |
| ARMOUR (3), CONTAINER (46) | a quarter turn about -X (mesh Z up), then `Q` | half the bounds' Z size |
| any other | `Q` | 0 |

## Theft and witnesses

**Observed** pending crime stores code, victim faction/handle, timer and booked state. Own/lawless
factions and police/prisoner exemptions gate creation. Loot permission for downed targets checks
slave/prisoner/bounty/bandit/police/diplomat/own-property branches, not just consciousness.
**Verified** transfer callsites disagree: right-click conscious→Theft3, unpermitted down→Looting10;
drag-drop unpermitted down-character→Theft3, other source→Looting10. Ordinary loot UI path unknown.
Ground pickup can raise Theft inside another faction's building, and taking from another faction's container does (Theft by right-click, Looting by drag).

Witnesses must be eligible and care about victim for theft/fencing/looting/kidnapping;
assault/murder have separate branches. Ordinary witness reacts; only police-like witness with
faction+0x40 books bounty. Kind7 is a squad-level suspect tag, not a faction relation event.
Friendly/hostile/slave-owner/lawless/bandit faction gates matter. Carrying past witnesses has
its own victim/captor ownership checks.

Meitou: the open loot body's limbs window uses the looter for drag and right-click transfers. Taking a fitted prosthetic leaves a stump and
keeps its damage, wear and stun on the item; a refused right-click move leaves it fitted.

## Stolen ownership

**Observed** item+0x140=from-owner, +0x160=current holder. Backpack uses its owning character's
faction. Same faction/lawless holder (faction `fundamental type` 8 or `not real`) is not stolen (**Verified**, `FUN_14079e550`; it is tested before the state checks, whatever the strictness); strict1 can use set/nonmatching original-owner
handle even if unresolved, strict0 also requires resolution. Dropping ownerless items can set
dropper as owner. The save's `ownedby` is item+0x140 (**Observed**, `Item` vfunc 75/76); preserve handles, not a boolean only. A mark naming only a faction (goods of a building
nobody lives in) or a robbed character the save does not hold has no handle; Meitou keeps the faction in `meitou stolen from` ([save.md](../formats/save.md#zone-files)).

A take from the inventory window (right click `RClickAutoTrade` `FUN_140713d20`, drag `FUN_140715560`) books its Theft/Looting, rolls
`ImStealingDoYouNotice` and marks an ownerless item (`Item::notifyTheftFrom`, vfunc 0x348: owner = the source, unless the source window is the
player's) only when `InventoryGUI::isStealing(source window, other window)` (`FUN_140711740`, **Verified**) holds. It is false when:

- the source character is an animal (`isAnimal`, vfunc 0x248) without a backpack on (`Character::hasABackpackOn` `FUN_1405cb320`), unless the
  window is the one open on a CONTAINER: a carcass (cleansers included) is never stolen from, a pack beast's load is;
- the source owner's faction is `not real` (+0x1d0), e.g. Wildlife;
- source and other window are the same;
- the source owner is a BUILDING whose `building category` is exactly `CAMPING` (Camp Bed, Campfire, tents);
- the two windows trade for money (`isTradingForMoney`).

The mark does not ask `isItOkForMeToLoot` (that only picks the crime), so a bandit's or bounty holder's gear is stolen from its faction too.
Right click and drag mark only an item with no owner yet; a give to a portrait (`FUN_140712490`) calls `notifyTheftFrom` whatever the owner (**Verified** call sites).

Meitou: `CrimeSystem.IsStealing` (character and container overloads) gates `Pickpocket`, `Steal` and the marks (`MarksTheft`, container takes);
the player's-windows and trade terms are the callers' own-faction tests and the separate trade window.

### Character fields used here

| Offset | Meaning | Evidence |
| --- | --- | --- |
| `+0x348` byte | carries someone (`isCarrying`) | set by the attach `FUN_1405cff90`, cleared by the unlink `FUN_1405ce1e0`, read by the carried-weight function `FUN_1405c89d0`: **Verified**, three functions agree |
| `+0x3d4` byte | is being carried | set by `FUN_1405ced90` (the body's side of the attach), cleared by `FUN_1405ce1e0` / `FUN_1405ccba0`; makes "ragdolled or carried" (`FUN_1407d1440`) true: **Verified** |
| `+0x380` handle (type `+0x388`, ids `+0x38c..+0x398`) | the carry partner: the body for the carrier, the carrier for the body | written on both sides by `FUN_1405cff90` / `FUN_1405ced90`, reset to the null handle (type 0xb) by the unlinks: **Verified** |
| `+0x378` byte | carry side, 0 right shoulder, 1 left | picks the bone `Bip01 R UpperArm` or `Bip01 L UpperArm` in `FUN_1405ced90` |
| `+0x350` string | side suffix of the lift clip name (`shoulder lift left` / `right`) | `Task_LiftSomething` slots 1 to 3 |
| `+0x2e0` | the RACE object; RACE `carriable` is its byte `+0x7a` | the RACE loader `FUN_14042efa0` stores it there and the lift task reads it: **Verified** |
| `+0x2e8` | the inventory | `FUN_1405c89d0` |
| `+0x2f8` int | special state: 0 normal, 1 in a bed (bed handle at `+0x300`, type `+0x308`), 2 imprisoned (cage or restraint) | `FUN_14032e2f0` sets and clears 1; the attach clears it and releases a restraint (`uses_restrains_movement_unique` `FUN_140330600`) when it was 2 |
| `+0x650` -> `+0x318` | the medical object; its byte `+0x165` = right arm usable, `+0x166` = left arm usable | section 1.2 |
| `+0x5bc` byte | dead | set by the death function `FUN_1407a6200` ("{1} is dead.", "{1} has died from blood loss.") |
| `+0x5b9` byte | unconscious (inferred) | no writer found; every reader pairs it with dead as "cannot act" (`FUN_1407944d0`, `FUN_140997c70`, `FUN_1408cdf50`, `isReadyForAction` `FUN_140998550`) |
| vtable slot 6 (`+0x30`), `CharacterAnimal::vfunc_6` `FUN_1405c9e10` | `isDown`: dead, `+0x5b9`, ragdolled or carried, or posture (slot 109, `+0xe0`, [ragdolls](ragdolls.md#forced-crawl): 3 playing dead, 4 KO) of 3 or more | Meitou: the stealth KO's `CarrySystem.CanBeSwungAt` (dead or unconscious, carried) |

### 3.2 Goals and their target finders

| Goal | Desire | Finder | Follow-up |
| --- | --- | --- | --- |
| `FIND_AND_RESCUE` 105 | `140599480` (exe bytes): 0 if CharStats `+0x12b` is set or the character carries, else 1 | `FUN_1408cd550` | - |
| `FIND_AND_RESCUE_IF_THERES_BEDS` 148 | `FUN_1405994c0`: 0 if CharStats `+0x12b` (Hold) or carrying, else the bed search `FUN_1408cfe00(..., 1)` (Meitou `CarrySystem.MayRescue`: all three, the bed one as `FindBed` for the rescuer, also for the one-shot Rescue order, which says "No free bed to take anyone to." over the rescuer when the search finds none, engine choice, the original is silent; a body already in a bed is not lifted again) | `FUN_1408cd550` | 148, then **106** `FIND_BED_AND_PUT_IN` |
| `FIND_AND_RESCUE_LEADER` 142 | - | `FUN_140598540`: the squad leader (`platoon +0xa0`) when it is not the character itself and not dead, at any distance (**Verified**; Meitou: goal 142 is data driven (`AiBehaviour.RescueLeader`, only the packages that list it: raid retreat, Go home everybody, ex slave, Retreat and refresh, campaign victory occupy town, addon wandering slaver base): the carry sweep `CarrySystem.RescueLeaders` lifts the leader as row 213 once the AI chose the goal, no range; an engine cap: only a knocked-out leader the lift can take, the original also counts a crippled or crawling one) | - |
| `FIND_BED_AND_PUT_IN` 106 | `FUN_1405a0d50`: 2 while carrying a body the carrier regards as friendly, whose slot `0x248` is null, that (for a police carrier, `FUN_140599600`) has no bounty with the carrier's faction, whose faction is not hostile to the carrier's (`FUN_1406b26d0`) and that the carrier's squad memory holds no grudge tag on (`FUN_140599390`: long-term tags 1, 4, 5, 7-10, short-term 1, 2, 4, 5, 7; **Verified**, Meitou `BedSystem.WantsToPutIn`); else 0 | none | - |
| `FIND_AND_KIDNAP` 132 | - | `FUN_1408cd5c0` (the character finder below, masks 0x4008 / 0x811, beds allowed) | - |
| `FIND_A_DEAD_GUY` 138 / 230 | - | `FUN_1408cfb00` / `FUN_1408ce170` (the nearest dead, ragdolled, uncarried body; `carriable` when asked) | - |
| `LOOT_THE_DEAD` 177 | AI slot 62: 1, 0 while character vfunc `0x368` (crawling or ragdolled) | `FUN_14099e680` (`FUN_14099ae10(ai, 0)`, [NPC looting](#33-npc-looting)) | - |
| `LOOT_STOLEN_GOODS` 212 | - | `FUN_1408cf6a0` (calls the stolen predicate) | - |
| `PICKUP_INTRUDERS` 223 / 273 | - | `FUN_14099c110` / `FUN_14099c250` | - |

### 3.3 NPC looting

- **shouldIScrewThisGuyOver** (= dialogue condition 54, [dialogue](dialogue.md#conditions); **Verified**), in order (later lines override): true when either is of NPC class 8
  (Bandit; status `+0xdc`); false when both have the same class; true when the looter's personality (status `+0xd8`, not the class) is 2 traitorous; false when looter vfunc
  `0x3f0`(target, 1), "regards as friendly", holds; false when the personality is 1 honourable; true when the looter's status
  `+0xe0` (a slave's owner faction) is the target's faction; false when the looter's squad has a love tag on it (long & 0xc), true with a hate tag (& 0x2b0); true when the target is dead.
- **Looting crime** (same start, every other looter; `BountyManager::setCrime` `FUN_140852c80`, **Observed**): code 10 Looting against the target's faction (victim the target)
  when the looter has any bounty (`getActualBounty(ledger, 0)` > 0) or is NPC class 8. Then with `notPlayerSide` or `foodOnly` the looter says event 33 LOOTING_EVERYTHING at the
  target and gets the same Looting unless `Character::isItOkForMeToLoot(looter, target, no item)` (vfunc `0x290`, `FUN_140793940`) allows it; else with `wantsWeapon` it says
  event 32 LOOTING_WEAPON_ONLY, and its Looting test sits behind `AI::hasWeapon(looter)`, which `wantsWeapon` rules out, so it never fires. The setter's own refusals apply
  (**Verified**, `FUN_140852c80`: a police-like culprit, one held in a cage (`+0x2f8` = 2), a victim of the culprit's own faction, for a culprit of status type 0 a victim of the faction it is seen as (`FUN_140852c20`: the uniform faction `+0x108` while the disguise level `+0x100` > 0.6 and the wanted timer `+0x110` is not above 0, else its own; the wrapper `FUN_140852e10` sets `+0x110` = 250 first, so its callers never meet this test), and a victim faction that is not real (`+0x1d0`) or of type 8; Meitou `CrimeSystem.SetPending`). **isItOkForMeToLoot** with no item: yes for a non-character, a slave or escaped slave (`isSlave` 1 / 2), the looter's
  own slave (`isMySlave`), a target with any bounty or of NPC class 8, a looter of NPC class 1, 2 or 5; otherwise no (the item branch, own-faction property, needs an item).
- **Update**: plays `medic` (start: `squat T`, `medic`); a timer adds the frame time. With neither `foodOnly` nor `notPlayerSide`, past 0.8 s: a `wantsWeapon` looter takes one
  weapon of the target's hip and back sections and is done. Otherwise the take comes past 8 s (an animal target: 20 s):
  1. Weapons (item type 2) of the target's inventory, when the looter has no weapon equipped (`Inventory::hasWeaponEquipped`) or the target is imprisoned; a weapon whose
     slot-type sections of the looter all hold something (none counts as all) is left, unless the looter is police or the target imprisoned; checked
     per weapon after each move (**Verified**, `FUN_140342de0` mode 2; Meitou: back and hip).
  3. With `notPlayerSide` and not `foodOnly`: every item (type 4) and armour (type 3), and every item of the backpack. An armour piece with the uniform field set (`+0x1e8`) is skipped outright; one taken and not worn replaces a worn non-uniform piece of its section when the worn level is lower (the worn piece's unknown `+0x2c8` term taken as false), the old one going into the inventory (**Verified** `FUN_140342de0`:128-157; Meitou `CarrySystem.WearIfBetter`).
  An item goes into the looter's inventory, else its worn backpack, else back. Then the looter's squad tags the target short-term 6 (`FUN_1406744a0`) and the task ends.
- **Confiscation** (the update with the confiscate flag, **Observed**): an animal target waits the 20 s and gets the take above (weapons, food); otherwise past 0.8 s
  `Inventory::getAllStolenItems(target's inventory, includeUnknown 0)` (`FUN_140748bc0`: the inventory's whole item list, then its equipped weapons and armour, each passing
  `InventoryItemBase::isStolen(item, 0)`; the target's own inventory, `Character::getInventory` slot 44 = `+0x2e8`, so not its backpack's contents; Meitou fix "Bags in the inventory", `World.BagSearchFix`: also the stolen items inside the worn backpack and the bags carried in the grid) and each in turn:
  `removeItemDontDestroy_returnsItem(item, 1, true)` (Inventory slot 5: one unit split off a stack, the whole item when it is the last), `addItem` into the looter's
  inventory; a unit that does not fit goes back and ends the loop. Taken items get `Item::notifyTheftFrom(target)` (slot 106, `FUN_140792800`: calls slot `0x2b0` only when the
  item's owner handle `+0x140` is of type 11 NULL_ITEM, read as "no owner"; a stolen item has one, so nothing changes). Nothing found: the short-term tag 6; something found: no tag. It acts out goal 212 Confiscating goods ([ai-tasks](ai-tasks.md#arrest-and-prison-rows)) and goal 177 for a
  law-enforcement looter. Base data (**Verified**, probe): 212 is listed only by "addon- law enforcement" (and its "- no freeing prisoners" variant), inherited by the police,
  guard-town and police-station packages. Illegal (culture-listed) goods are not part of it: no confiscation of them was found.
- **Bandits loot the player** is the new-game option at block `+0x20`, saved as CAMERA bool `bl` (**Verified**: the reader `FUN_1403eef20` reads key `bl` into `+0x20`, the writer
  `FUN_140912f80` copies `+0x20` to `142133598`; the reader's default is 1, and `bl` is true in the owner's three readable original saves).

### 3.4 Foraging take (row 241)

**Verified** (decompile of `Task_Loot_ResourcesWeHaveStorageFor`, vtable `1416be550`: start `FUN_14034e140`, update `FUN_14034e2a0`, pick `FUN_140998950` over `FUN_14059acd0`). The task
is row 241 "Looting resources", the effect 148 (`LOOTED_STORABLE_ITEMS`) of goal 238 Foraging animals ([ai-tasks](ai-tasks.md#perma-jobs)). It does not take everything:
- **Update**: plays `medic`; past **3.0 s** (`DAT_14168a254`) it takes once and ends. The pick (`FUN_140998950`): the first item of the target's inventory list, then of its backpack's, for which a
  storage of the looter's faction exists (`FUN_14059acd0`, the same test as the 238 finder). Then, one unit at a time (`FUN_14074ab30` splits a unit off the first stack of that item
  kind in the target's **own** inventory list, not its backpack's; a death item leaving butchers the body), each goes to the looter's worn backpack if that inventory takes the kind
  (checked once, before the loop), else the looter's inventory; it stops when that place has no room for the kind or none is left. No crime, theft mark or money is touched in the update.
  Nothing storable: nothing moves and the task ends.

## Kidnapping

**Observed** (decompile; names KenshiLib 1ce4a16; literals **Verified**). Verb 246 (the pick-up verb on another faction's character that is
not standing: down, asleep, imprisoned, [ui-input](ui-input.md#context-menu-verbs)) runs `Task_Kidnapping`, a `Task_LiftSomething` whose
arrival (vfunc 1, `FUN_14034ffa0`) first rolls:
- The victim's squad tags the kidnapper TEMP ENEMY and the kidnapper gets a pending Kidnapping (code 15) against the victim's faction.
- **Chance** (`Character::getKidnappingChance` `FUN_140885600`, also the verb's column): 100 for a down victim (`isDown`: dead, out, ragdolled or carried, posture 3+) that is not getting up (+0x278; Meitou `GettingUpUntil`); else
  the stealth KO's formula ([combat](combat.md#stealth-knock-out)) with the victim's effective strength in place of its toughness and no
  sense terms. Roll uniform 0..100; taken when roll <= chance.
- **Taken**: a victim neither dead nor unconscious is knocked out for lerp(2, 300, kidnapper assassination x 0.01) s (raw stat); assassination
  XP 100 is given only in that case (**Verified**, `FUN_14034ffa0`: the same `+0x5b9`/`+0x5bc` test guards both; a hit on a down victim gives none); then the lift.
- **Failed**: the victim perceives the kidnapper (the kidnapper's status `+0x110` = 250, the recently-wanted marker); its town's alarm is raised at the
  kidnapper (code 15, [town alarms](economy.md#town-alarms)); a sleeper (state 1) gets order 116 Getting out of bed; its squad tags the
  kidnapper TEMP ENEMY and long-term 1 (intruder) and says event 8 to it; assassination XP 300; the task ends; a sleeper wakes (state 0).

### The kidnap victim flag

Status `+0xe9`, saved as `kidn` ([save](../formats/save.md)). **Verified** by decompile (every `+0xe9` access in the status range):
- **Set** only by `Task_Kidnapping` vfunc 2 (`FUN_1403552d0`), on the victim, when the roll was not failed (task `+0x80` clear, set by the failed
  branch of vfunc 1) and the target resolves and is not the kidnapper: the kidnapper gets the pending Kidnapping (code 15) against the victim's
  faction, the victim the flag, then the lift (`Task_LiftSomething` vfunc 2). So every 246 lift flags its victim, whoever the kidnapper is, before any cage.
- **Cleared** by the per-update `FUN_140852a30`: not carried (`+0x3d4` clear) and in a bed (`+0x2f8` = 1) while its status town (`+0x88`) is its
  home town (`FUN_1407956a0` `+0x30`); or carried by someone it regards as friendly (vfunc `0x3f0`) or loves (`FUN_140599590`), else it tags its
  carrier TEMP ENEMY (4) each update. Also by the separated-squad merge ([locks](locks.md#cages-and-beds)). Caging, release and escape do not touch it.
- **Witnesses** (`FUN_1408549f0`, a looker seeing a carrier whose load passes slot `0x350`): nothing when the looker is the victim's enemy
  (vfunc `0x3e8`); a looker that does not regard the victim as friendly (`0x3f0`) acts only while the carrier's status town is set, the
  victim's faction is real and the victim has no bounty with the looker's faction. Then, for a carrier that is not down, no animal, and a real
  victim faction: the carrier's `+0x110` = 250 and a wrapper-set pending Kidnapping (timer 20, freed flag cleared).
- No sentence, bail or escape rule reads it.

Meitou: `CharacterCold.KidnapVictim`, set by `CarrySystem.Kidnap` on a roll that took, kept up by `ArrestSystem.UpdateKidnapVictims` (the bed
test reads "its home town" as `InOwnTown`) and `PopulationSystem.RejoinSeparated`, read by `CarrySystem.LetKidnapVictimGo`, the status line and
the witnesses (`CharacterFacts.Kidnapped`, `CrimeSystem.MindsKidnapping`; a witness friendly to the carrier's faction is still skipped, engine
choice) and the bounty panel (`TooltipRuntime.ShowsBountyPanel`). Meitou's own
`CharacterCold.Kidnapped` ("held by a kidnapper: no sentence, no bail, leaving is no escape"; set when `KidnapSystem` cages a victim and on
spawned cage prisoners, cleared with the hold) is a separate flag, saved as `meitou kidnapped`.

### Escaped slaves lie low

The status object's `+0x8` is a double of game hours (day x 24 + hour), saved as the float `ssct`. The slave state setter stamps it with the world clock on any change to a state above 1.

A per-character status update lets a state above 1 run down from that stamp. With `elapsed` = now - `+0x8` in game hours:
- **elapsed below 0:** the state becomes 0 at once, with the owner kept.
- **State 3 and elapsed / 24 above 4.0:** the state becomes 0 and the owner is cleared.
- **State 2 and elapsed above 12.0:** the state becomes 3 and `+0x8` is stamped again.

**Verified** by disassembly of a Kenshi 1.0.65 `kenshi_x64.exe`, sha256 `504b362c…cd3e`. In that build, the setter is `FUN_1405a3eb0`, the rule is `FUN_1405a44c0`, and its only caller is the vtable-reached `FUN_1405a4750`. Its constants are 24.0, 4.0 and 12.0. The other addresses in this file come from another build.

### A slave far from its owner escapes

The same update (1.0.68 `FUN_1405a4f50`, **Verified** by decompile) makes a slave (state 1) an escaped slave (2) through the setter when all of these hold:
- the status object's current town (`+0x88`, [below](#the-status-town)) is none, i.e. the slave is out of every town;
- its owner handle (character `+0x328`) names a character that resolves, and the squared distance between them is above 12 500 000 (`DAT_1416f016c`, about 3 536).

### The status town

The status object's current town (`+0x88`) is written by 1.0.68 `FUN_1405a51e0` just before it calls the status update above (**Verified** by decompile; the only
writer in that code range). Distances are on the ground plane from the town's centre; a town's reach is its radius (Town vfunc `0x2a0`, Meitou `TownData.Radius`)
times a factor, and a nest marker never holds anyone (`FUN_140926d50`). In order, the first that holds:
1. the nearest town of the player's faction, within 1.25 x radius (`DAT_14168ba14`);
2. the current town when there is one, within **1.5 x** radius (`DAT_14168ad18`), else the nearest town, within 1 x (`DAT_14167c308`): entering takes the radius,
   leaving takes half again;
5. the town of the building the character stands in (Character vfunc `0x1d8`).

- the save holds `ssct` 34.00063 and the clock day 1 10:00, which is 34.0 h;
- both escaped player characters were free within 120 calendar seconds of the load, in 5 runs out of 5, read with the KenshiOracle probe.

Meitou: `FixtureRegistry.LayLow` runs from the crime sweep (engine choice) and also clears the owner on the first path. The stamp is `CharacterCold.SlaveSince`, and `SlaveSave` reads and writes `ssct`.

## Unknowns

2. The **sensed-list category bits** (`FUN_140853960` masks 1, 0x4010, 0x20, 0x210, 0x4008, 0x811, 0x10): which bit is ally, enemy, dead, slave;
   and the size of the sensed list, which is the search range of every finder.
6. **Witness timing**: how often `FUN_1408580d0` runs per sensed character, with what range and line of sight, and how the pending timer `+0x90`
   counts down (`FUN_140851f40` sets it; the decrement was not found).
9. A **despawn timer** for ground items.

## Expression reference

- The attach: `+0x2f8 == 2`; `+0x2f8 = 0`
- Weight and speed: `platoon +0x78 -> +0xb0 != 4`
- Dropping: `RagdollPart { WHOLE=1, RIGHT_ARM=2, LEFT_ARM=4, ..., CARRY_MODE=2048 }`
- The unlink `FUN_1405ce1e0`: `+0x240 = -1`; `+0x3d4 = 0`
- `Task_PutInSomething`: `+0x2f8 = 2`; `+0x20 = 1`; `+0x2f8 = 1`; `+0x2f8 == 1`
- Crimes for taking items: `+0x34 == 8`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

# Economy: money, item prices, shops, bounties

Sources:

- **Binary**: `kenshi_x64.exe`, the Steam build (decompile dump with the naming pass applied, plus Ghidra disassembly of
  the item-value function, the shop fillers and the armour constructor to check the arithmetic order). Addresses like
  `FUN_14079e6d0` are functions of that dump; `Class::vfunc_n` are MSVC vtable slots from RTTI. Many dump names are
  misleading because the naming pass labelled by a string the function happens to touch: the item price function
  `FUN_140896e20` is called `read_food_crop_n11` in the dump, the CONSTANTS loader `FUN_14086b2b0` is
  `read_XP_rate_medic_1_n86`, the Platoon constructor `FUN_1407ee2d0` is `read_buy_mult_n6`. This doc uses the
  address as the key. The dump lists some virtual slots through a thunk: `MoneyItem::vfunc_81` is the thunk
  `FUN_14001352a`, whose body is `FUN_14075f660`; the same holds for the other `Class::vfunc_n` addresses quoted below
  where the quoted function is the thunk's target.
- **Arithmetic precision**: all of the price code works in 32-bit floats and truncating float -> int conversions
  (**Verified** in the disassembly of `FUN_140896e20` and the constructors). A model in doubles can be off by one on a
  product such as `500 * 1.4`, which is 700 in float but 699 in double; the worked examples in 4.6 use float arithmetic.
- **Data**: `gamedata.base` plus the core mods the probe's `BaseGame` load order brings in, read through
  `GameDatabase.Load` (probe `<RE workspace outside the repo>/probes\economy`); field descriptions from the install's
  `fcs.def` (the designers' text; sometimes stale, see "Data corrections"). The editor's `itemType` numbers are the ones
  in [fcs-mod.md](../formats/fcs-mod.md#record-types).
- **No game run.** Nothing here was checked against a running game; "Verified" below means game data, or two
  independent places in the binary (producer and consumer of a value, or loader and data), as the docs README defines.
  The probe's worked examples are our own re-implementation of the formulas, not output of the game.
- Counts are for the probe's merged database.

Related: [characters.md](../characters.md) (how an NPC's gear, quality and manufacturer are rolled, which feed the prices
here), [character-stats.md](character-stats.md) (the CONSTANTS table, the `CharStats` offsets used by the slave price),
[combat.md](combat.md) (the same armour class and material tables, columns for defence), [factions-squads-towns.md](factions-squads-towns.md)
(towns, squads, the trader fields in section 4, culture lists), [game-loop.md](game-loop.md) (the game clock in hours),
[save.md](../formats/save.md) (platoon `money`, character bounty keys), [terrain.md](../formats/terrain.md) (world size, for
the town price distances), [ui-screens.md](ui-screens.md#3-main-menu-and-new-game-flow) (the new-game start-offs with their cash), [buildings-production.md](buildings-production.md#research-and-tech)
(research and blueprint cost, shop buildings), [ai-tasks.md](ai-tasks.md) (`Task_Shopping`, arrest and bounty tasks) and [ai.md](ai.md) (how traders and squads are driven).

Labels: **Verified** = checked against game data or by two independent places in the binary (said how); **Observed** = one
reading of one function (or a pattern in data); **Unknown**.

## 1. Overview

| Quantity | Where it comes from | Section |
| --- | --- | --- |
| Money ("cats") | One signed 32-bit integer per `Platoon` (`+0x88`); physical cat items are folded into it on pickup | 2 |
| Base price of an item type | `ItemValue` (`FUN_140896e20`): FCS `value` fields, quality, manufacturer `price mod`, CONSTANTS multipliers | 3, 4 |
| Price of an item instance | Item virtual slot 81 (one unit) and 82 (a stack): `ItemValue` again or the stored value, sell fraction, charges, then the trader factor | 5 |
| Trader factor | Platoon `buy mult`, halved for stolen goods, times the culture price of the item (`ITEMS_CULTURE`) | 6 |
| Random per-town market factor | made once per town in `Zones_TownSetup` for the goods in the VENDOR_LIST `all trade goods` | 7 |
| Shop stock and shop money | `vendors`, `vendors fill total amount`, `vendor money`, `vendors refresh time` of the SQUAD_TEMPLATE | 9 |
| Bounties | per character, one entry per faction: amount, crime mask, claimed flag, timestamp | 11 |
| Slaves, prisoners, animals | separate price rules in the `CharacterTrading_*` windows | 12 |

There is **no weight term, no relation (faction standing) term and no trade-skill term** in the price chain
(**Observed** for the absence: the chain `FUN_140896e20` -> slot 81/82 -> `FUN_14079e6d0` -> `FUN_140715560` was read
and none of those functions reads weight, a relation value or a stat; the trade window `FUN_140715560` takes the prices and
compares money. The one function of the chain that was not decoded, the stolen predicate `FUN_14079e550`, and the callers
outside the chain were not searched exhaustively, so an absence is not proven). The CHARACTER `trade skills` stats of `fcs.def` (`armour smith`, `bow smith`, `labouring`, ...) are crafting
skills, not price inputs. The only stat the economy code reads is `thievery` (the odds of being caught fencing, section 8)
and the three stats of the slave price (section 12). Whether the *crafting* cost or the research cost involves other
terms: see section 10.

## 2. Money (cats)

- **Storage** (**Verified**: the Platoon constructor `FUN_1407ee2d0` zeroes it, the platoon save loader `FUN_140624640` stores
  the `money` key into it and the saver `FUN_140624120` writes it back to `money`; the shop fillers and the bounty claim
  also write the same offset): `Platoon+0x88` is the integer purse, saved as the platoon key `money` (see
  [save.md](../formats/save.md); the player's own is summarised as `player money` in the CAMERA record).
- **Cats the item**: ITEM `Cats` has `value` 1, `stackable` 100, `weight kg` 0.01 (`String of Cats` 10, `Money is
  Freedom` 100, `CPU of Cat-Lon` 100000, other ITEM records with `value` only; **Verified** by probe over ITEM). A
  `MoneyItem` (`MoneyItem::vfunc_81`, thunk `FUN_14001352a` -> `FUN_14075f660`) returns the record's `value` unchanged: no
  multiplier, no trader factor, no loot fraction (**Verified**: the whole function is one field read, and every ITEM
  record has a `charges` field defaulting to 1, so the charge ratio of the generic stack total stays 1).
- **Picking cats up** (`FUN_14075f700`, **Observed**): an owned item whose owner is not of the player's faction is refused;
  otherwise the stack total (slot 82 with `sell` = 1, i.e. `value` x count) is added to the player's purse
  (an object reached through `DAT_142134690`, the PlayerInterface of [ui-input.md](ui-input.md#7-mouse-and-selection), the same `+0x88` integer), the log line `"{1,num} cats added."` is shown, and the
  item is consumed. A total below 1 logs an "Item quantity error" and adds 1 cat.
- **Trades** move `price` from one party to the other by `FUN_140745e30(party, delta)` (**Observed**; the dump shows the
  function only forwarding to the virtual slot 54 (`+0x1b0`) of the object stored at `+0x80` of the party, so the
  purse mutation itself is in that slot and was not traced), after a funds check (section 8).
- **Start money**: NEW_GAME_STARTOFF `money` ("Starting cash" in `fcs.def`; the base game's start-off records use it).
  Which function reads it into the new player's purse was **not traced** (the string `money` is also touched by
  `read_difficulty_n4` `FUN_140916620` and the platoon save code; [ui-screens.md](ui-screens.md#3-main-menu-and-new-game-flow) names `FUN_140916620` as the reader of the NEW_GAME_STARTOFF records for the new-game window, which shows the cash), so the display side is known but the hand-over into the purse is **Unknown**.
- **Money items in loot**: VENDOR_LIST `money item prob` / `money item min` / `money item max` (section 13).

## 3. CONSTANTS price block

The loader `FUN_14086b2b0` reads the GLOBAL CONSTANTS record into the block at `142133dd0` (field address =
`142133dd0 + 4 * index`; the same loader as in [character-stats.md](character-stats.md#constants-used-by-this-subsystem)).
The consumer column is where the value is read. **Verified** for each row: the loader's read and a consumer found through the
data cross-references, and the base values from a probe over CONSTANTS; "[Editor]" is the `fcs.def` default.

| Index / address | FCS field | Base | [Editor] | Stored as | Consumer |
| --- | --- | --- | --- | --- | --- |
| 0x37 `142133eac` | `global price mult` | 1.0 | [1.0] | as is | only the seven rows marked "x global" below |
| 0x34 `142133ea0` | `armor price mult` | 0.6 | [1.0] | x global | `ItemValue` (armour with part coverage) |
| 0x38 `142133eb0` | `clothing price mult` | 1.0 | [1.0] | x global | `ItemValue` (armour without part coverage) |
| 0x35 `142133ea4` | `sword price mult` | 0.4 | [1.0] | x global | `ItemValue` (WEAPON) |
| 0x36 `142133ea8` | `trade price mult` | 0.6 | [1.0] | x global | `ItemValue` (plain items that are not `food crop`) |
| 0x28 `142133e70` | `food price mult` | 0.5 | [1.0] | x global | `ItemValue` (plain items with `food crop`) |
| 0x33 `142133e9c` | `robotics price mult` | 1.0 | [1.0] | x global | `ItemValue` (**CROSSBOW** records, see the swap below) |
| 0x32 `142133e98` | `crossbow price mult` | 1.0 | [1.0] | x global | `ItemValue` (**LIMB_REPLACEMENT** records) |
| 0x39 `142133eb4` | `trade profit margins` | 0.3 | [0.5] | as is | `Zones_TownSetup` (market factor spread, section 7) |
| 0x3a `142133eb8` | `loot price mult GEAR` | 0.25 | [0.15] | as is | sell fraction of weapons, armour, blueprints (slot 81) |
| 0x3b `142133ebc` | `loot price mult` | 0.5 | [0.4] | as is | sell fraction of maps, items, limbs (slot 81) |
| 0x3c `142133ec0` | `loot price mult player armour` | 0.25 | [1.0] | as is | sell fraction of armour the player crafted (`Armour::vfunc_81`) |
| 0x3d `142133ec4` | `loot price mult player weapons` | 0.25 | [0.5] | as is | same for weapons (`Weapon::vfunc_81`) |
| 0x3e `142133ec8` | `blueprint price mult` | 1.5 | [1.5] | as is | research cost (section 10) |
| 0x3f `142133ecc` | (none: the loader writes the constant 0.3 itself) | 0.3 | n/a | hard-coded | `Armour::vfunc_81` (see section 5) |

Notes (**Verified** unless said):

- **`global price mult` only pre-multiplies the seven category rows marked "x global"** (armour, clothing, sword,
  trade, food, robotics, crossbow). It is *not* applied again later, and the loot fractions, the profit margin and the blueprint multiplier do not include it.
  With the base value 1.0 it changes nothing; a mod that changes it rescales every category at once.
- **The crossbow and limb multipliers are swapped** in `ItemValue`: CROSSBOW (type 107) reads the `robotics price mult`
  slot and LIMB_REPLACEMENT (type 111) reads the `crossbow price mult` slot. Checked in the disassembly of the two
  branches (`14089775c` reads `142133e9c`, `14089763d` reads `142133e98`) and against the loader's index table. Both are
  1.0 in the base game, so it is invisible there.
- The editor defaults differ from the base data in 9 of the 14 rows; the game uses the **base data** values, the editor
  text is stale. The editor text for `loot price mult` ("regular items as loot (stolen, or non-trade items)") matches the
  code (section 5).
- **Correction to [character-stats.md](character-stats.md)**: its row for `food quality mult` says "x global price
  mult". The loader stores that field as is (`DAT_142133e6c`, used by the item constructor to scale a food item's
  `charges`); it is `food price mult` (index 0x28) that is multiplied by `global price mult`. That row has since been corrected in that doc.
- The sell-side constants are stored as read from CONSTANTS and are not scaled by `global price mult`.

## 4. Item base value: `ItemValue` (`FUN_140896e20`)

Signature (**Observed**, from the call sites): `ItemValue(record, quality, applyCategoryMult, ?, manufacturer)`. `quality`
is a float in 0..100 (the item's quality; the Armour and Weapon constructors pass the integer at `+0x224` converted to float);
`applyCategoryMult` is a byte: 0 gives the "raw" value, 1 multiplies in the CONSTANTS rows of section 3. The result is an
`int`; **every multiplication in the weapon, armour, crossbow and limb branches is truncated to an integer before the next
one** (float -> int conversion after each step, **Verified** in the disassembly of all four branches; the plain-item and
ingredient branches round with `floor(x + 0.5)` instead). The call site arguments were read in the disassembly of
the constructors (`14089898f`, `14089a3e4`: quality = the integer at `+0x224` as float, flag 1) and of the ingredient
recursion (`1408972c1`: same quality, flag 1, no manufacturer). Callers that pass 0 (raw value): the two small
helpers `FUN_1400b87d0` and `FUN_1400b8b60` (which cache the raw value in a record wrapper) and the blueprint reader
`FUN_1408324f0` (section 10); the weapon, armour and gear constructors and the generic-item slot 81 pass 1. Further callers
not analysed (**Unknown** flag): `FUN_1400b90a0` (a very large function, the dump shows quality 0), `FUN_140791570`
(a small helper that calls it with the item's quality) and the item tooltip `FUN_1407ae130`.

Let `lerp(t, a, b) = a + (b - a) * t` (`FUN_140015b63`; [character-stats.md](character-stats.md#effective-stats-injuries-and-hunger) writes it `lerp(a, b, t)`, a notation difference only; **Verified**: argument order is t first, then the value at t = 0,
then the value at t = 1). Quality fraction `s = quality / 100`.

The branch is chosen by the record's type number (`+0x50`), 2 / 3 / 0x2e / 0x6b / 0x6f / anything else.

### 4.1 WEAPON (type 2)

1. `v = value` (integer FCS field). If a WEAPON_MANUFACTURER is passed, `v = trunc(v * price mod)`.
2. Quality curve: the logistic `L(s) = 1 / (1 + e^(-(2s - 1) * 5 * 0.8))`, i.e. `1/(1+e^(-4(2s-1)))`. It is 0.018 at quality 0, exactly
   0.5 at quality 50, 0.982 at quality 100. The constants 0.01 (quality scale, `141682ad8`), 2.0 (`141682ad0`), 1.0,
   5.0 (`141685340`), 0.8 (`14168a178`) were read from the exe's data (**Verified**: values read out of the PE file).
3. `raw = trunc( lerp(L(s), 15, 160) * v )`, so a weapon is worth between 15x and 160x its `value` field; the 15
   (`14168a258`) and 160 (`141726c20`) are exe constants (**Verified** the same way, with the register order of the
   `lerp` call in the disassembly).
4. With `applyCategoryMult`: `result = trunc( raw * sword price mult * global price mult )` (the two are pre-multiplied in
   section 3, so one float).

### 4.2 ARMOUR and clothing (type 3)

1. Look at the `part coverage` list. The quality path is used only if `quality > 0` **and** the item has at least one `part
   coverage` entry (pieces without coverage, i.e. clothing, always use their own `value`).
2. Quality path: `t = s * s` (quadratic, not the logistic), `lo = classLo[class] * matLo[material type]`,
   `hi = classHi[class] * matHi[material type]`, and `v = trunc( lerp(t, lo, hi) )`. Otherwise `v = value`.
3. Category mult (if applied): `v = trunc( v * armor price mult )` if the item has `part coverage`, else `trunc( v * clothing price mult )`.
4. `v = trunc( v * relative price mult )` (ARMOUR field, 0.4 to 1.4 in the data).
5. Slot factor from the ARMOUR `slot` field: slot 3 (hat) x0.3, slot 6 (legs) x0.5, slot 8 (shirt) x1.5, slot 9 (boots) x0.2, and
   any other slot (5 body, 7 belt?, ...) x1; then truncated. The slot names are those of [combat.md](combat.md) (inferred
   there from the records); only the numbers 3, 6, 8, 9 are in the code (**Verified** in the disassembly at
   `140897cfb` onward, immediates 0.3 / 0.5 / 1.5 / 0.2 matched to the slots). The ARMOUR records use slots 3 (44), 5 (39),
   6 (30), 8 (20) and 9 (9) (**Verified** by probe), so 103 of the 142 records get a slot factor.

The class and material tables are the same two tables [combat.md](combat.md#armour) uses for defence, built by the
constructor `FUN_1408a8f90`; the price uses the **first value column** of each entry (offset +4), combat uses the later
columns. **Verified** (constructor stores and `ItemValue` loads read from the same four hash tables `142134ce0`,
`142134d20` (class, `ArmourClass`) and `142134d60`, `142134da0` (material, `ArmourType`)):

| `class` | name | `classLo` (quality 0) | `classHi` (quality 100) |
| --- | --- | --- | --- |
| 0 | GEAR_CLOTH | 100 | 5000 |
| 1 | LIGHT | 400 | 30000 |
| 2 | MEDIUM | 800 | 35000 |
| 3 | HEAVY | 400 | 40000 |

| `material type` | name | `matLo` | `matHi` |
| --- | --- | --- | --- |
| 0 | CLOTH | 0.2 | 0.8 |
| 1 | LEATHER | 0.5 | 1.0 |
| 2 | CHAIN | 1.0 | 1.5 |
| 3 | METAL_PLATE | 1.5 | 1.5 |

Because `lo` and `hi` are products, a top-quality piece is worth `classHi * matHi` cats before the multipliers (Heavy
metal plate: 60000), and quality 0 pieces use the FCS `value` instead of `lo` (the quality path needs `quality > 0`).

### 4.3 CROSSBOW (107) and LIMB_REPLACEMENT (111)

`raw = trunc( lerp(s*s, value, value 1) )` (`value 1` is "price at best quality", editor text). With the category mult:
`trunc(raw * robotics price mult)` for type 107 and `trunc(raw * crossbow price mult)` for type 111 (the swap of
section 3; `global price mult` is included in both rows).

### 4.4 CONTAINER (type 0x2e = 46)

Just the integer `value`; no quality, no multipliers.

### 4.5 Everything else (ITEM and the other types that fall through)

- **Items with `ingredients`**: the item's own `value` is ignored. For each entry of the `ingredients` list the game
  computes `ItemValue(ingredient, quality, 1)` and adds `floor( that * (v0 / 100) * (profitability + 1) + 0.5 )`; the sum is the price. (`v0` is the
  entry's first value, a percentage of one unit; `profitability`, an ITEM float (0.2 on `Cats`), is "the percentage added",
  the editor text says.) Recursion is allowed. **Verified** by decompile of the ingredient loop plus the disassembly of
  the same loop (`1408975a4`: add 0.5, `floorf`, add to the total; the recursive call passes the same quality and flag 1);
  the use of `v0` and not another value is **Observed** (the loop reads the first int of the reference, the same slot the
  culture loader reads as a percentage; in the data 55 ITEM records have `ingredients`, the first values are 25 .. 2000, mostly 100, so they look like percentages).
- **Plain items**: `v = value`. If the item is an `artifact` (ITEM bool) the multiplier is skipped. Without the multiplier the
  result is `value`. With it: `floor( v * m + 0.5 )` where `m` is `food price mult * global` for `food crop` items and
  `trade price mult * global` for **all other plain items** (not only `trade item` ones; `trade item` only matters for the
  sell fraction, section 5).
- The same plain branch also serves the other record types that reach it (blueprint items, maps, ...): they read their own `value`
  field the same way. **Unknown** for any type other than ITEM which ones do.

### 4.6 Worked examples (Observed: our re-implementation in `probes/economy`, re-run in float32 arithmetic in `probes/economy-verify`, not the game)

| Item | quality | raw | with category mult |
| --- | --- | --- | --- |
| Katana (WEAPON, `value` 500) | 0 / 25 / 50 / 75 / 100 | 8804 / 16142 / 43750 / 71357 / 78696 | 3521 / 6456 / 17500 / 28542 / 31478 |
| Katana, manufacturer Edgewalkers (`price mod` 1.4) | 50 | 61250 (value 700 first) | 24500 |
| Samurai Armour (class 3, material 3, `value` 100) | 0 / 50 / 100 | 100 / 15450 / 60000 | 60 / 9270 / 36000 |
| Chainmail (class 2, material 2, `value` 12000, slot 8 so x1.5) | 50 | 20587 | 12352 |

In the Edgewalkers row `500 * 1.4` is 700 in the game's float arithmetic (a double model would truncate 699.99999 to 699 and give 61162 / 24464). The 100-quality value can differ by one with the exact `expf` of the game's C runtime. Check by hand for the Katana at 50: `L = 0.5`, `lerp(0.5, 15, 160) = 87.5`, `500 * 87.5 = 43750`, `* 0.4 = 17500`.

### 4.7 WEAPON_MANUFACTURER `price mod` (data, Verified by probe)

Unknown 0.3, Ancient 0.8, Truth Two- / Homemade / Expired- / animal 1.0, Catun Scrapmaster and Skeleton Smiths 1.2,
Edgewalkers 1.4, Cross 2.0. Which manufacturer an NPC's or shop's weapon has is rolled as in
[characters.md](../characters.md#rolling-weapons).

## 5. Runtime unit price: Item virtual slots 81 and 82

Every inventory item class has slot 81 ("value of one unit, with a sell flag") and slot 82 ("value of the whole stack").
RTTI gives the implementations (`rtti.tsv`; the slots 81 and 82 are at `+0x288` and `+0x290` of the vtable); **Verified** by reading each (also the slot 82 `FUN_1407915b0`, which calls slot 81 through `+0x288`). The byte `sell` is **1 when the item is being
sold to a shop** and 0 when it is being bought from one (**Verified** in the trade window `FUN_140715560`: the error
code is `(sell != 0) + 3`, i.e. 3 "You can't afford that." for a purchase and 4 "The shopkeeper can't afford that." for a
sale, and the same flag selects which price is computed).

| Class | Slot 81 (`sell` = 0) | With `sell` = 1 |
| --- | --- | --- |
| `Weapon::vfunc_81` `FUN_1408a52f0` | `trunc( T * stored )` | `trunc( T * stored * f )`, `f` = `loot price mult player weapons` if `Weapon+0x208` is set, else `loot price mult GEAR` |
| `Armour::vfunc_81` `FUN_1408a9c20` | `stored`, then if `Armour+0x1e8` is set `* 0.3` (the hard-coded 0.3 of section 3), then `trunc( T * . )` | the `stored` first gets `loot price mult player armour` if `Armour+0x208` is set, else `loot price mult GEAR`, and then the same 0.3 and `T` steps |
| `BlueprintItem::vfunc_81` `FUN_1402d30f0` | `trunc( T * value )` (`value` = the research `money` stored at `+0x1e8`, section 10) | `trunc( T * value * loot price mult GEAR )` |
| `MapItem::vfunc_81` `FUN_14075d950` | `trunc( T * value )` (the int at `MapItem+0x1e8`; where it is set was not traced) | `* loot price mult` |
| `MoneyItem::vfunc_81` `FUN_14001352a` (thunk to `FUN_14075f660`) | the record `value`, nothing else | same |
| everything else (`Item`, `InventoryItemBase`, `Gear`, `NestItem`, `ContainerItem`, `SeveredLimbItem`, `RobotLimbItem`) `FUN_1407a8f90` | see below | see below |

`T` is the **trader factor** of section 6 (`FUN_14079e6d0`) and `stored` is the price made when the item object was
constructed: `Weapon+0x1f0` and `Armour+0x1f0` hold `ItemValue(record, quality, 1)` (section 4), computed once in the
constructors `FUN_140898830` (the weapon, `Weapon::vftable`) and `FUN_140898e90` (armour and gear). Because it is stored, a CONSTANTS change
applies only to items created after it.

`FUN_1407a8f90` (generic items, **Verified** by decompile and disassembly):

1. `u = ItemValue(record, item quality, 1)` (the item's quality is `vfunc 0x2b8`).
2. If a town can be found for the open trade (`FUN_14003efc7` -> `FUN_14070d170`, which asks `Zones_GetBuildingTown` and
   then the town's slot 77), `u = trunc( u * marketFactor )` where `marketFactor` is the per-town random factor of section 7
   for that record (1.0 when the town has none). **Observed**: which town is meant is inferred from the helper's name and
   result, not traced further.
3. If `sell`: `u = trunc( u * loot price mult )` **unless** the item is a `trade item` (`Item+0x128`) that is **not stolen**
   (`FUN_14079e550(item, 1)`, section 6). So trade items sell at full price, everything else at 0.5, and stolen trade
   items also at 0.5. This agrees with the editor text for `trade item` ("player can always sell this item for full
   price") and for `loot price mult` ("selling regular items as loot (stolen, or non-trade items)"): **Verified**
   (two independent places).
4. If the stack count (`+0x12c`) is exactly 1: `u = trunc( u * charges / max charges )` (`+0x118` over `+0x114`). There is
   no explicit "has charges" test; the item constructor `FUN_14075faf0` fills both from the `charges` field (scaled by
   `food quality mult`, section 3) and every ITEM record has it with editor default 1, so the ratio is 1 for ordinary items
   (**Verified** by probe: 309 of 309 ITEM records carry the field; the constructor stores `trade item` at `+0x128`).
5. Result `trunc( T * u )`.

**Stack total, slot 82** (`FUN_1407915b0`, shared): with unit price `p` and count `n`: if `n == 1` then `p`, else
`p * n - trunc( p - p * charges / max charges )`, i.e. every unit is full and the *last* unit is scaled by its charge
fraction.

**The trade window prices a stack slightly differently** (`FUN_140715560`, **Observed**): it calls slot 82 with `sell`
flipped for the first `k` units of the stack, `k` being a count returned by `FUN_14070e3b0` (probably units that came from the other
party's own stock; not decoded), and with the normal flag for the remaining `n - k` units, then applies the same
last-unit charge correction. The trader factor and the funds check are applied on top.

Base-game consequences (**Observed** from the values): gear sells for 25% (armour, weapons, blueprints of either), ordinary
non-trade items for 50%, trade items and `Cats` for 100%, all times the trader factor.

## 6. Trader factor `T` (`FUN_14079e6d0`) and the stolen test

Read in the function and its helpers (**Observed** as a whole; the parts cross-checked are marked **Verified**):

`T = buyMult * (0.5 if stolen-to-the-player-faction) * culturePrice(item)`

- **`buyMult`** (`FUN_1407116f0`): the `Platoon+0xe8` float of the trader's platoon (the trader object found by
  `FUN_14070e2d0` below, its platoon through `FUN_140791b10` and `+0x78`); 1.0 if there is no trade open. It is SQUAD_TEMPLATE
  `buy mult` (**Verified** by two places: the Platoon constructor `FUN_1407ee2d0` copies the field into `+0xe8`, and `FUN_1407116f0` reads `+0xe8` of the trader's platoon; the constructor also copies `buys stolen` to `+0xec` and `buys illegal` to `+0xed`). The editor text says it
  applies "when the npc buys and sells": it does, in both directions (slot 81 multiplies by `T` either way).
  Data (probe): 945 of 959 squad templates are 1.0, 9 are 0.5, 4 are 0.25, 1 is 1.5.
- **Stolen halving** (**Observed**): if `FUN_14079e550(item, 1)` says the item is stolen and one of the item's two owner
  handles (the one at `+0x140`) resolves to a faction with `Faction+0x250` set (the player's faction), `T` is halved. The
  predicate `FUN_14079e550` itself is only partly decoded: it needs the item's two owner handles and a few flags; see
  Unknowns.
- **`culturePrice`** (`FUN_14092bec0`): look the item *record* up in the **trader's town culture price map**; if the town has a
  culture object and the item is in it, use that value; otherwise look it up in the **faction's own culture**
  (`Faction+0x90`); default 1.0. The trader's town is found with `FUN_14070e2d0`, which scans the list `DAT_142132be8`
  for the first entry whose faction is not the player's (`+0x250 == 0`) and asks it for its town (`vfunc 0x78`, then slot 77 = vtable offset `+0x268`, which
  `Town::vfunc_77` answers with itself and `TownBase::vfunc_77` with null; **Verified** in the rtti table and the two bodies). The values are `ITEMS_CULTURE` `trade prices` (section 7).

So the price an NPC pays or asks for a given stack is `trunc( T * unit )`, then the stack arithmetic of section 5, then the
funds check of section 8.

**What `T` does not contain**: standing with the faction, the trader's or the player's skills, the item's weight, the
number of items already sold, the time of day. (**Observed** absence in `FUN_14079e6d0` and its helpers, see section 1.)

## 7. Towns: culture and the market factor (`Zones_TownSetup` `FUN_1409353c0`, `read_forbidden_items_n5` `FUN_1407fd9a0`)

### 7.1 Culture (ITEMS_CULTURE)

Towns and factions themselves (types, residents, the `trade culture` lists) are described in [factions-squads-towns.md](factions-squads-towns.md#7-towns).

TOWN and FACTION both have a `trade culture` list of ITEMS_CULTURE records. For each listed record `FUN_1407fd9a0` fills the
town's (or faction's) culture object:

- `illegal goods` (ITEM list) -> contraband set; `forbidden items` (ITEM list) -> items that never spawn there;
  `illegal buildings`, `happy buildings` (BUILDING lists).
- `trade prices` (ITEM list with the first reference value): the multiplier for the item is `v0 / 100`, with **0 meaning
  100** (the `0 -> 100` rule shows in the decompile: a zero float is replaced by 100 before the 0.01 scale). When several
  listed cultures price the same item the **later one overwrites** (map assignment). **Verified** against data: every
  `trade prices` entry in the data carries its percentage in the first value (`(125,0,0)`, `(75,0,0)`), and the editor's own
  default `(150)` for the field is the first value too. [factions-squads-towns.md](factions-squads-towns.md#7-towns) first said "ITEM v1 %" (the editor's wording) and has been corrected to v0: the first value is the percentage and the editor text "(val1)" is simply stale. Data (**Verified** by probe over ITEMS_CULTURE in the merged load order): the 39 entries are 400 x9, 50 x5, 125 x5, 75 x4, 150 x4, 40 x3, 1200 x3, and one each of 600,
  500, 350, 300, 100, 20, i.e. factors 0.2 to 12 (the extreme examples: shek culture prices item `1230-gamedata.base` at 350%, swamper
  culture prices `1913-gamedata.base` at 20%).
- The town's own culture list refines the faction's: the town object is consulted first (section 6).

### 7.2 Market factor (a second, random per-town price multiplier)

After the culture setup `Zones_TownSetup` reads the VENDOR_LIST named `all trade goods` (`1013-gamedata.base`; found by name
and type 49) and, for each item in its `items` list, makes one float and stores it in the town's own map (`Town+0x4b0`,
read back by `FUN_14092d590`, default 1.0):

1. `f = uniform in [1 - m, 1 + m)` with `m = trade profit margins` (0.3 in the base game, so [0.7, 1.3)); uniform
   comes from `Rand_IntInRangeB(a, b)` = `a + (b - a) * rand()/32768` (float; **Verified** in `FUN_1409b1c80`).
2. For **every real town in the global town list** (`DAT_142134100`; only objects whose slot 77 returns themselves count,
   i.e. `Town` objects, not `TownBase`/`TownNull`), let `p` be that town's factor for the item, **or 1.0 when it has none yet**
   (`FUN_14092d590` returns 1.0 for a missing item; the loop does not test for presence), and `d` the distance between the two town positions in the x-z plane (game units). The allowed
   spread is `w = d / 1,000,000 * m * 10` (so 3e-6 per unit at `m = 0.3`; a town 100,000 units away may differ by 0.3). `f` is first
   capped to `p + w` and then raised to at least `p - w`.

Because a town with no factor yet counts as 1.0, **every town set up before this one pulls `f` toward 1.0**, and nearby ones
do it hard: a neighbour 10,000 units away that has no factor yet (`p = 1.0`) clamps `f` to about [0.97, 1.03]. Towns that were
rolled earlier pull `f` toward their own value. Only a town far from every other (distance above about 100,000 units for
`m = 0.3`) keeps most of the 0.7 to 1.3 range. **Observed** (one reading; the clamp order and the position fields were
checked in the decompile). Whether the town being set up is itself in that list at this point is **Unknown** (see Unknowns). A cross-check with data: the sample save of [save.md](../formats/save.md) holds saved factors over the whole 0.70 to 1.30 range across 304 towns, so the squeeze toward 1.0 described here cannot be as complete as the worst case (most neighbours probably are not yet in the list when a town is set up, or the list holds few towns then); the true order is **Unknown**.

The market factor is **used only by the generic-item slot 81** (section 5, step 2) and by the dialog tooltip
(`FUN_1407ae130`, "Price mark-up" display, see Unknowns); weapons and armour do not get it, and `culturePrice` is a different
factor. **The factors are saved** (**Verified** by two places: the Town save writer `Town::vfunc_85` `FUN_140376b80` writes the factor map
of `Town+0x4b0` as the reference list `trade goods`, each item with `v0 = trunc(factor * 10000)`, and the Town state loader `FUN_1403715c0`
reads that list back and divides by 10000; the loader also accepts an older `goods <n>` key layout. The sample save of
[save.md](../formats/save.md) agrees: 75 ITEM ids per town, the same count as `all trade goods` (75 items, **Verified** by probe),
with `v0` between 7028 and 12978, i.e. factors 0.70 to 1.30 as `m = 0.3` gives). The roll itself uses the global C `rand()`
(`rand() / 32768`, **Verified** in `FUN_1409b1c80`), not a position seed, so it is random per new game. Whether
`Zones_TownSetup` also runs when a save is loaded (and whether the loader's values then win) was not traced.

## 8. Buying, selling, fencing

The trade window (`FUN_140715560`, "InventoryGUI::placeItemFromMouse" is the name the game logs) **Observed**:

1. When an item is dropped from one inventory onto the other party's, the game computes the stack total (slot 82 with
   `sell` set for the item going into the shop) and compares it with the payer's money (`vfunc 0x1b8` of the payer's side):
   if the payer has less, error 3 (buyer) or 4 (shopkeeper) is shown and the move is undone. If only some units are
   affordable the stack is split first (`vfunc 0x298` gives the number affordable for a money amount).
2. Otherwise `FUN_140745e30(buyer, +price)` / `(seller, -price)` moves the money and the item is placed.
3. Other refusal messages found in `FUN_14070f7e0` (**Verified** by the string table): "Out of trading range." (1), "No room
   for that item." (2), "Can't wear that item." (5), "Invalid container type." (6), "Item is locked in place." (7), "Got caught
   stealing." (8), "Got caught selling stolen item." (9), "That's not for sale." (0xc), "Target is conscious." (0xd), "Smugglers only
   buy illegal goods." (0xe), "Attempted smuggling!" (0xf), "What is that a uniform? I don't want that!" (0x10), "Container
   cannot be placed here unless it is empty." (0x11).
4. **Fencing prompt** (`FUN_140715290`, **Observed**): when a stolen item is dropped on a shop and the shop is **not** a
   knowing fence for it (`FUN_140792830` is false: it is false at once when the platoon's `+0xec` flag, SQUAD_TEMPLATE
   `buys stolen`, is off; with the flag on it is true unless the item's origin is not of type 0xb and its two origin integers (`+0xc`, `+0x10`) equal the
   platoon's `+100` and `+0x68`, i.e. a knowing fence buys everything except goods stolen from that very shop; **Observed**), the game asks "You
   have a {1}% chance of getting caught selling this stolen item. Fence it anyway?" where `{1} = 100 - round(100 * p)`
   and `p` = `FUN_140793490` (only asked when `p < 1`). `p` is 1.0 for the player's own faction as the buyer, with no
   seller, for items of origin type 0xb, or when the item's origin integer `+0xc` equals the integer at `+100` of the seller; 1.1 in one stranger case;
   otherwise `p = lerp(thievery / 100, 0.4, 1.25)` (thievery = stat 10 of the seller, `FUN_140884ac0`; constants 0.4 and 1.25 read from the immediates; **Verified** against the stat table of [character-stats.md](character-stats.md)), halved if the
   item's owner faction is the buyer's faction, halved again if the item's origin integer `+0xc` equals the shop's
   `+100` (a place identifier, not decoded). Being caught is the crime `Fencing` (section 11). The branches that give
   1.0 and 1.1 are only partly decoded (see Unknowns).
5. **Shop flags** (Platoon `+0xec` `buys stolen`, `+0xed` `buys illegal`): the message codes 0xe "Smugglers only buy
   illegal goods." and 0xf "Attempted smuggling!" exist in `FUN_14070f7e0`, and `CharacterAnimal::vfunc_85` reads `+0xed`
   together with a culture test (`FUN_14071da60`, a lookup in the town culture's illegal-goods set) to choose the outcome
   code 1 or 2; the whole decision tree is **not decoded**.
   Data (probe): of 959 squad templates 14 buy stolen only, 2 buy illegal only, 4 buy both.

## 9. Shops: stock, money and restock

### 9.1 SQUAD_TEMPLATE fields (data = **Verified** by probe over 959 templates)

The other template fields (leader, squads, animals, `world state`, building fields) and how a template becomes a squad are in [factions-squads-towns.md](factions-squads-towns.md#4-squad-templates).

| Field | Meaning | Data |
| --- | --- | --- |
| `is trader` | the squad is a trading squad | needs a vendor list |
| `vendors` | list of VENDOR_LIST (val0 = weight of the list) | |
| `vendors fill total amount` | how many items the vendor holds (default 15 in the editor; 0 or below means 100 in the code) | 0 x279, 15 x397, 10 x14, 100 x19, 20-40 common, 1000 x1 |
| `vendor money` | the building-shop vendor's purse (building fillers only, see 9.2); 0 = derive from the stock | 0 x954, 1000 x4, 250 x1 (the five non-zero templates all come from mods of the probe load order, none from `gamedata.base`) |
| `vendors refresh time` | game hours between stock refreshes (<= 0 means 24000) | 24 x874, 12 x15, 48 x13, 72 x13, 70 x10, 99 x10, some 99999 |
| `buy mult`, `buys stolen`, `buys illegal` | section 6 and 8 | |
| `special items`, `special map items` | extra guaranteed stock (`read_special_items_n3` `FUN_140958740`) | not decoded |

### 9.2 Stock size and money (`FUN_14095ab10`, squad shops; `FUN_14095b210`, building shops; **Observed**, decompile and
disassembly agree on the constants)

- `fill = vendors fill total amount`, replaced by 100 if it is below 1.
- `wealth = clamp(FUN_1402dd530(faction), 0.2, 1.0)`. In this build the function behind that thunk is a stub that returns the
  constant 1.0 (**Verified**: the whole decompile is `return 1.0f`), so `wealth` is always 1. (A hook for
  faction-dependent shop sizes that is not implemented.)
- `n = round( uniform(0.8, 1.2) * fill * wealth )` with `round` = half away from zero (`FUN_1409b32b0`, **Verified**: body is floor(x + 0.5) for x >= 0 and ceil(x - 0.5) below; the 0.8 and 1.2 are the two float arguments loaded at `14095ac26` / `14095b323`, values read from `14168a178` and `1416852f0`; the uniform is `FUN_1409b1c80`). For a building
  shop at least 1.
- The vendor lists are filled in proportion to the list weights: list `i` gets `round(n * w_i / sum w)` items, the last list
  takes the remainder when the rounding left any (`FUN_140959800`, **Observed**). A list with `items count` of -1 or less
  uses the VENDOR_LIST's own `items count` (40 when that is below 0): the editor says "total amount of items taken from the vendor list; the actual number
  depends on the space in the inventories" (`FUN_1409555d0`).
- Items are picked with a weighted roll (weights in `items`, `weapons`, `clothing`, ... reference value 0, "val0 is
  relative chance" in the editor), stacked up to the item's `stackable` size (`FUN_1409555d0` merges into an existing
  stack until `stackable`). In the squad filler `FUN_14095ab10` the new items are then sorted by their unit price (slot 81, `sell` = 0) and offered to the
  inventory sections; items that do not end up placed are destroyed and do not count toward the stock value (**Observed**; the sort key in the code is the price; whether size also orders the placement was not found).
- **Armour quality in shops**: the roll uses the faction's `armour vendor quality chance` (FACTION `armors 0..5`, editor
  defaults 50 / 100 / 50 / 2 / 0 / 0) to pick a grade 0..5, and the grade becomes a quality 5 / 20 / 40 / 60 / 80 / 95 for
  grade 0..5, any other value 20 (`FUN_140952320`, **Verified**: the same table is in [characters.md](../characters.md) for NPC gear, read
  from a different caller `@ 1406210f0`). So a shop's armour is priced at the corresponding point of the quadratic quality curve of section 4.2.
  How weapon quality is rolled for shop stock: **Unknown**.
- **Vendor money**: `stockValue` is the sum of the stack totals (slot 82, `sell`
  = 0, full price) of everything just stocked (and kept). **The squad filler `FUN_14095ab10` never reads `vendor money`**: its purse is always
  `trunc( clamp(0.25 * stockValue, 3000, 25000) * wealth )`. The field `vendor money` is read only by the building-shop filler `FUN_14095b210`
  (**Verified**: the string `vendor money` is referenced by that one function only), which does:
  - If `vendor money` is 0 or less: `money = trunc( clamp(0.25 * stockValue, 3000, 25000) * wealth )`.
  - Otherwise: `money = trunc( (vendor money + r) * wealth )` with `r` an integer uniform in `[-trunc(0.4 * vendor
    money), +trunc(0.4 * vendor money)]` (`Rand_IntInRange`, inclusive).
  The result is written to `Platoon+0x88`. The 0.25, 25000 and 0.4 are constants read from the exe
  (`141696c80`, `141718a04`, `1416c16a0` as a double; **Verified** by reading the PE), 3000 is an immediate. The second branch's `-trunc(0.4 * vendor money)` lower bound is
  what the dump shows; the upper bound argument is hidden by the decompiler (assumed symmetric, **Unknown**).

### 9.3 Restock (**Observed**)

`ActivePlatoon::vfunc_4` (`FUN_1405003c0`, the per-update method) refreshes a trader when all of these hold: the platoon is the
state with designation 1 and `+0x58 == 0`, its faction is not the player's, `uses_vendors` and `uses_is_trader` are true, and
`Platoon+0x1f8` (a game-hours timestamp, a double) is strictly earlier than the game clock (`DAT_1421303d0 + 0xa0`, total game
hours, see [game-loop.md](game-loop.md)). The refresh (`FUN_1404ff5e0`) removes the old stock, refills it as in 9.2 and sets the
next time to `now + vendors refresh time` (24000 hours if the field is 0 or below). A first fill happens at squad creation
(`FUN_1404fe970`). A building shop (platoon type 0, the shop building's own `vendors` through `DAT_142133fb0 + 0x1f0`) is refilled by the
same function with `FUN_14095b9d0`, which also refreshes the money. The next-refresh timestamp is saved as the float key `inventory refresh time`
for traders (`FUN_1407ed0f0`, see [save.md](../formats/save.md)); whether stock itself is saved (and so whether a
reload keeps a shop's stock): **Unknown**.

Platoons of the player's faction never restock through this path (the same `+0x250 == 0` faction test).

## 10. Blueprints, research cost, artifacts

- **Blueprint item price and research cost** (the research system that consumes the cost is in [buildings-production.md](buildings-production.md#research-and-tech); the writer, the constant 40 and the 1.5 multiplier are **Verified** in the disassembly; that the item reads the generated research record's `money` is **Observed**, the record held at `BlueprintItem+0xc0` was not traced to the generated RESEARCH record). The blueprint generator
  `FUN_1408324f0` (`read_blueprint_item_n6`) builds a RESEARCH_TEMPLATE record for a weapon, armour or gear blueprint and
  stores in its `money` field `trunc( ItemValue(item, quality 40, raw) * blueprint price mult )`, i.e. the raw (no
  category multiplier) value at the fixed quality 40 (the float 40.0 in the call in `FUN_1408324f0`; **Verified**), times 1.5 (editor text: "just affects armour blueprints"; the code applies it to every generated blueprint). The
  `BlueprintItem` constructor `FUN_1402b78a0` (`uses_money`) reads that same `money` field into `BlueprintItem+0x1e8`, which
  slot 81 uses as the unit price (section 5; **Verified**: the constructor stores the field `money` at `+0x1e8`, slot 81 reads `+0x1e8`). So the price of a blueprint item and the research cost are very probably the same number;
  RESEARCH records that are not generated carry their own authored `money`.
- **Artifacts**: the ITEM bool `artifact` stops the category multiplier (section 4.5). SQUAD_TEMPLATE
  `item artifacts base value` and `gear artifacts base value` (editor: "the base value of artifacts the squad can get, 0 =
  none") and TOWN `item/gear artifacts min value` / `max value` bound which artifacts a squad may carry; the readers are
  `FUN_1400b8e70`, `FUN_1400b8f10`, `FUN_1409512f0`, `FUN_1409513b0`, `FUN_140951470`, `FUN_140951530` (**Verified**: each is a single field
  read). The placement itself (`ArtifactTown`, which squad gets which item) is **Unknown**.
- **Not covered**: crafting material cost, building purchase price (FACTION `building cost mult`: "multiplier for the cost to buy
  buildings from this faction", editor text only), bed usage cost (`bed usage cost` 100 in the editor, "how much it costs to use a
  bed"), and the slave price override (section 12) have no decoded formula here.

## 11. Bounties and crimes

**Verified** where stated; the data and strings were checked, the logic is from the decompile (**Observed** unless marked).

### 11.1 Ledger

Each `Character` has a bounty ledger (a hash map keyed by faction, at `Character+0xf0`). An entry (read through
`FUN_1405e7ee0`) has: `+8` amount (int), `+0xc` crime bitmask (bit `1 << crimeCode`), `+0x10` byte "claimed", `+0x18` double game-hour timestamp of the last
raise. The player's own ledger is saved as `bountyfac<n>` / `bountyexp<n>` / `amount<n>` / `crimes<n>` / `claim<n>` (see
[save.md](../formats/save.md); the loader that fills the ledger from those keys is `FUN_140625e60` and the writer is `FUN_1406262b0`, **Verified** by which side assigns the record fields and which the ledger fields; the saved `bountyexp<n>` is the entry's timestamp (`+0x18`) truncated to an int, and `bountyfac<n>` is the faction's name; the sample-save details are in save.md). The `+8` / `+0xc` / `+0x18` / `+0x10` layout is **Verified** by the crime add, the claim marker `FUN_140854170` (sets `+0x10`) and these two functions agreeing.

### 11.2 How a bounty is gained

- **Crime**: `FUN_140853ec0(crimeEvent)`: if the crime code is nonzero and has not been processed yet (`+0xa4` flag), the
  character's state is not 8, 5 or 1, then the crime's amount is **added** to the ledger entry of the wronged faction
  (`FUN_140852010`), the crime bit is set, the timestamp is reset to now, and the "wanted" UI is refreshed. The very first crime
  code (1, Enslaving) sets the flag but adds nothing (**Observed**; the code returns before the add). The same function also sets a float (250.0) in a nearby object of the character at `+0x110` (purpose not traced).
- **Crime amounts** (`FUN_140851e40`; **Verified**: a switch with no other inputs; names from the game's own name table `FUN_140856530`; the editor's enum is not in `fcs_enums.def`, so a match with it was not checked):

| Code | Crime | Amount |
| --- | --- | --- |
| 1 | Enslaving | 100 (but see above: nothing is added) |
| 0xb (11) | Trespassing | 100 |
| 2 | Burglary | 1000 |
| 0xd (13) | Fencing | 1000 |
| 3 | Theft | 2500 |
| 0xc (12) | Escaping Prison | 2500 |
| 0x10 (16) | Uniform Theft | 2500 |
| 4 | Murder | 5000 |
| 7 | Terrorism | 5000 |
| 8 | Smuggling | 5000 |
| 9 | "Terrorism" (a second code with the same label) | 5000 |
| 0xf (15) | Kidnapping | 5000 |
| 6 | Treason | 50000 |
| 5, 10 (Looting), 0xe (14, Crop Theft), others | Assault, Looting, Crop Theft, default | 500 |

- **Link to relations** (**Observed** coincidence): the relation event kinds 3, 4, 5 and 12 of [factions-squads-towns.md](factions-squads-towns.md#34-changing-relations-observed-the-event-table-cross-checked-with-the-dialogue-action-numbers) have the same numbers as the crime codes Theft (3), Murder (4), Assault (5) and Escaping Prison (0xc) above; whether the event kind there is this crime code is still **Unknown**.
- **Spawn bounties** (`FUN_14062b210`, the squad-member roll, **Observed**): CHARACTER `bounty chance` (0..100) is
  compared with an inclusive integer roll in 0..100 (an inclusive integer roll of 0..100 compared as roll < chance, so chance 100 succeeds with 100 of 101 values; **Verified** in the roll function `FUN_1409b3b40`, which draws `min + floor(rand/32768 * (max - min + 1))`); on
  success the amount is `trunc( base + 0.5 * k )` with `k` an integer in `[0, 2 * fuzz]` where `base` = `bounty amount`
  and `fuzz` = `bounty amount fuzz`. **The fuzz is added, never subtracted** (mean `base + fuzz/2`) although the editor text says
  "+/- amount". It is added for each faction in `bounty factions` (the empire faction `defaultEmpireFactionSID` when the list is
  empty), through `FUN_140853e20` (same add as a crime, without a crime bit). Skipped when the caller's fifth argument is set (probably
  a save-load path; not checked). Data (**Verified** by probe over CHARACTER, 639 records, merged load order): 16 characters have chance 4 with amount 2000 + fuzz 2000; of the chance-100 ones, amount 20000 x11 (fuzz 0), 10000 x10,
  30000 x7, 40000 x5, 50000 x4, 100000 x3, 25000 x3, 3000 x3 (fuzz 500), and singles from 0 and 1000 up to 80000; other chances 1 to 50 carry 500 to 10000 with fuzz 0 to 4000.

### 11.3 Lifetime and notoriety

`FUN_140851f10(amount)`: lifetime in game hours = `0.04 * amount` for amounts below 10000, and 9,999,999 for 10000 or more
("Notorious: The bounty will never expire." in the tooltip `FUN_1405d1110`). So 500 cats lasts 20 hours, 5000 lasts 200 hours,
and 10000 or more never expires. The per-character tick `FUN_1405cbb80` goes through the ledger and, for an entry with amount
above 0 whose `now - timestamp` is negative or beyond the lifetime, removes it (`FUN_1408539f0`; one entry per tick, the loop stops after the first removal, and the tick is skipped when `Character+0x2f8` is 2); every new crime resets the
timestamp, so repeated crimes extend it (**Observed**). The tooltip shows "The bounty will expire in {n} hr(s)" with
`lifetime - elapsed` (**Verified**: strings).

### 11.4 Claiming

`FUN_140330d50(captor, ..., prisoner)` (**Observed**): when a character of the **player's faction** hands a captive to a
police chief (a character of designation 8, not of the player's faction, not state 7; further conditions in the code: the captive must not be of the player's faction either, and some flag bytes of the captor's and captive's data must be clear) and the captive's ledger has an amount for the
chief's faction and the entry is not yet claimed, the reward is the ledger amount, **halved when `captive+0x5bc` is set**
(truncated to an integer; the flag's meaning is **Unknown**), the entry is marked claimed (`FUN_140854170`), the amount is added to the
player's purse, the message "Received c.{1} reward." is shown and the `Bounty_Fulfilled` sound plays. A claimed entry cannot be
claimed again; this guards against selling the same prisoner twice.

### 11.5 Bail and sentence

- **Bail** (`CharacterTrading_PrisonerBailout`, `FUN_1406ab890`, window "Pay Bounty"; **Observed**): price = `2 * bounty` (read with `FUN_140854080` from the prisoner's ledger; the call passes a zero faction argument, so which faction's entry is meant was not resolved); if the prisoner is **not** of the player's faction the price is at least 2000 (`max(2000, 2*bounty)`).
- **Sentence**: CONSTANTS `prison time` (1.0, "multiplier for time you have to spend in prison for your crimes") is read by the
  loader. The sentence length function `FUN_140851d10` (a `lerp` between 350-600-based and 450/80 values scaled by a health fraction)
  is **not decoded**; see Unknowns.

## 12. Slaves, prisoners, animals

The three trading windows share `uses_buy_value` (`FUN_1406751f0`, **Observed**):

`buyValue = clamp( trunc( strength * k + attack + dexterity * k ) * RACE buy value, 1000, 20000 )`

where `strength` = `CharStatsAnimal::vfunc_2`'s value (stat at `+0x80` times `CharStats+0x34`; `k` = `CharStats+0x34`), `attack` =
the melee-attack stat (`+0x120`), `dexterity` = `+0x88`, with the stat offsets from [character-stats.md](character-stats.md).
RACE `buy value` is 100 for most races (27 of 53 records); other values in the data: 400 x8, 10 x4, 50 x3, 75 x2, 200 x2, 250 x2,
1000 x2, 0 x2, 300 x1 (**Verified** by probe).

| Window | Class | Price (**Observed**) |
| --- | --- | --- |
| Selling a slave to a slaver | `CharacterTrading_SlaveSelling` `FUN_1406a6f50` | an explicit override if the caller gave one, else `clamp(buyValue / 2, 200, 2500)` |
| Buying a slave | `CharacterTrading_SlaveBuying` `FUN_1406a7970` | override or `buyValue`; **doubled** when the character is of the player's faction |
| Buying an animal | `CharacterTrading_AnimalBuying` `FUN_1406a9440` | override or 14000, times `(growth + 0.1)` where `growth` = the animal's `vfunc 0x398` (age/maturity 0..1) |
| Paying bail | `CharacterTrading_PrisonerBailout` | section 11.5 |


## 13. Loot and money items

- **Loot pricing** is not a separate system: "loot" is the `sell` flag of section 5 plus `trade item` and `stolen` (section 5,
  step 3). The CONSTANTS rows `loot price mult` (0.5), `loot price mult GEAR` (0.25) and the two `player` rows (0.25) are the
  only inputs.
- **Money items in vendor lists** (`FUN_140956060`, `read_money_item_max_n3`): VENDOR_LIST `money item prob` is compared with a
  uniform in [0,1) (`FUN_1409b1c10` = `rand()/32768`); if the roll is not above the probability, an integer uniform in
  `[money item min, money item max]` (inclusive) is the number of cats added as a stack of the money item (the amounts of
  all the lists are summed and placed with `uses_stackable_material`, by the fillers `FUN_140959800` and `FUN_140959a90`).
  **Quirk** (**Verified** by data against the code): the editor documents the probability as [0-1], but the data stores
  percent-looking numbers (probe over VENDOR_LIST: 1 on 1 record, 25 on 1, 30 on 3, 40 on 3, 50 on 3, 60 on 4, 70 on 1, 100 on
  7; all others 0). The code only compares the raw float with a roll below 1, so **every list with a value of 1 or more
  always yields a money roll**, and a list with 0 yields none (except in the 1 in 32768 case the roll is exactly 0). Examples
  (probability, min..max): bank (1, 15000..30000), holy church (100, 1000..5000), safehouse supplies (100, 100..300), wealthy
  house (100, 0..2000), bandit good loot (100, 0..1000), trade goods (60, 0..600).
- **Nest and world loot** uses the same VENDOR_LIST type through `FUN_140959800` (`nest loot items`, `nest loot weapons low`,
  `ancient lab`, ...).

## 14. Data corrections and checks

- `fcs.def` (`E:...Kenshics.def` lines 734-747) says `loot price mult` 0.4, `loot price mult GEAR` 0.15 and the player rows 1.0 / 0.5: all differ from the base
  data (0.5 / 0.25 / 0.25 / 0.25). The data wins (**Verified**: the probe reads the CONSTANTS record).
- `fcs.def` says `bounty amount fuzz` is "+/-"; the code only adds (section 11.2).
- `fcs.def` for `trade prices` says "(val1)"; the text is stale: the percentage is the first value, `val0` (section 7.1; the "ITEM v1 %" wording of [factions-squads-towns.md](factions-squads-towns.md#7-towns) was corrected the same way).
- `fcs.def` for `trade item` ("always sell for full price"): correct, with the exception of stolen items (section 5).
- ITEM counts (probe, merged): 309 ITEM records, 160 `trade item`, 4 `artifact`, 5 `food crop`; 142 ARMOUR records with
  `relative price mult` from 0.4 to 1.4 (1.0 on 100, 0.7 on 13, 0.5 on 7, 0.6 on 6, 1.4 on 5, 0.8 and 1.2 on 3 each, 0.65 and 1.1 on 2 each, 0.4 on 1); 34 WEAPON records with
  a positive `value` (mean 330, max 600).

## Unknowns

- **The stolen predicate** `FUN_14079e550(item, 1)`: it reads two owner handles (`+0x140`, `+0x160`), a flag byte, and a type
  check against the type list `{2, 3, 4, 0x2e, 0x56, 0x66, 0x6b, 0x6f}`. What exactly makes an item "stolen" (the thief, the
  owning faction vs. the holder's, expiry) and the second halving condition in `FUN_140793490` are not decoded. The `0.5`
  stolen factor in `T` is therefore only roughly defined.
- **The meaning of `Armour+0x1e8`** (non-null multiplies the armour price by the hard-coded 0.3) and of `Weapon/Armour+0x208`
  (inferred as "crafted by the player" from the editor text of the two player rows; not shown by a writer).
- **Whether the town being set up is already in the global town list `DAT_142134100` when its own market factors are rolled.**
  If it were, its distance to itself is 0, the spread `w` is 0 and its own `p` is 1.0, which would force every factor to 1.0 and
  make the feature inert; so either the town is added after setup or something not yet seen intervenes. The order of the list
  decides which neighbours have factors already.
- **Whether `Zones_TownSetup` runs on a save load** and, if so, whether the saved `trade goods` factors (section 7.2, saved by `FUN_140376b80`,
  loaded by `FUN_1403715c0`) overwrite or are overwritten by a fresh roll. (That the factors are saved is settled.)
- **How the market factor reaches the display**: `FUN_1407ae130` ("Price mark-up", "Avg. Price") builds the tooltip text from the
  same functions; the exact strings and the average formula were not decoded.
- **What `FUN_140745e30` really does** (it forwards a money delta to virtual slot 54 of the object at `+0x80`; the purse mutation itself) and how
  the trade window decides the `k` units priced with the opposite flag (`FUN_14070e3b0`, section 5).
- **Where `MapItem+0x1e8` (the map price) comes from**, and which record `BlueprintItem+0xc0` points at (section 10).
- **How weapon quality is rolled for shop stock** and how VENDOR_LIST reference value 1 (and a value 0 in the first slot, as in
  `(0,100,0)`) is read for clothing, armour and weapon entries (`FUN_140956ba0` / `FUN_1409555d0` read them; "val0 is relative
  chance" per the editor, but 472 entries have (0, 100, 0)). It follows the NPC clothing rule in [characters.md](../characters.md)
  that quantity 0 never spawns, but this was not re-verified for shops.
- **The slave price override** (the fifth argument of the slave windows) and where it comes from.
- **The artifact placement** (`ArtifactTown`, which squad carries which artifact) beyond the six field readers.
- **The crime-witness faction selection** `FUN_140852010` (it chooses which faction's ledger entry a crime goes into; it reads
  `Character+0x2f8` which is 2 in some state) and `Character+0x2f8`'s meaning.
- **Whether CHARACTER `money min` / `money max`** exist in the data and are unused (the editor lists `money`; not found
  being read for NPC purses).
- **The "Terrorism" label for code 9** (two crime codes share the name; the second may be a different crime in the editor enum).
- **Sentence length** (`FUN_140851d10`) and `prison time`'s consumer.
- **Research cost** beyond the formula of section 10, crafting costs, building purchase price, bed cost.
- **The purpose of `FUN_1405d4260`** (the crime-in-progress tooltip function; the decompile timed out and was not analysed).

## Implementation outline

Order of work for the new engine (the data side is already in `Meitou.Data`: `GameDatabase`, `GameRecord` with `Ints`,
`Floats`, `Bools`, `GetReferences`; the quality roll for NPC gear is in the characters.md rolling code):

1. **Constants.** Read the CONSTANTS price block (section 3) into a record; derive the seven "x global" values and the hard-coded
   0.3 once. Keep the base-vs-editor note: use the data file's value.
2. **`ItemValue`** (section 4) as a pure function over a `GameRecord`, a quality float, a flag and an optional manufacturer
   record; truncate after every multiply; hold the armour class/material table (section 4.2) as constants. Port the probe's `Econ`
   class (`probes/economy/Program.cs`, but with `float` arithmetic, see the precision note in the sources) and the worked examples as unit tests (Katana 43750 / 17500, Katana with Edgewalkers 61250 / 24500, Samurai Armour 15450 /
   9270).
3. **Item instances.** All price arithmetic in 32-bit float with truncation. On creation store `ItemValue(record, quality, 1)` for weapons, armour and blueprints; compute the generic item price
   live (section 5, steps 1-5) with the `sell` flag, the `trade item` and stolen tests, charges and stack arithmetic.
4. **Trader factor.** `T = buyMult * stolen * culturePrice` (section 6); the culture object per town and per faction
   (section 7.1) with the 0 -> 100 rule and "last list wins".
5. **Town setup.** The `all trade goods` market factors with the neighbour clamp (section 7.2). First settle the open question of
   which towns are in the global list while a town is set up (and in which order): if the town itself were in it, `d = 0`
   would force every factor to 1.0. Persistence is settled: save the factors in the town state as the list `trade goods`, `v0 = trunc(factor * 10000)`.
6. **Vendor lists and shops.** `n = round(U(0.8, 1.2) * fill)`, weights across lists, per-list item counts, stack merging, armour grade -> quality
   (section 9.2), vendor money (derived from the stock for squads; `vendor money` only for building shops), restock timer in game hours (9.3).
7. **Platoon money.** `Platoon.Money` as `int` with save/load of `money` and `inventory refresh time`; the pickup of money items.
8. **Bounty ledger.** Per-character map faction -> (amount, crime mask, claimed, timestamp), the crime table, the lifetime rule and the
   claim (section 11); spawn bounties from `bounty chance` / `amount` / `fuzz`.
9. **Trade rules.** Funds check with split stacks, the two refusal codes, the fencing prompt and the smuggling messages; bail and
   slave/animal prices (section 12).

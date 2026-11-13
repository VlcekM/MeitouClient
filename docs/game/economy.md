# Economy

Kenshi1.0.68 research from ItemValue/trade/shops/town/crime binary functions, schema and merged
records. Tables retain constants, price classes and surveyed fields. `Meitou.Data.Gameplay.Economy`
and Simulation Trading/Crime implement researched rules; [carry-loot](carry-loot.md) owns theft.
No skill/reputation/time-of-day price factor was observed in the decoded trader-factor path.

## Item value

**Verified** arithmetic: gear truncates after each multiplication; plain/ingredient prices round
floor(x+0.5). Quality fraction s=quality/100. Engine lerp arguments are (t,a,b).
Weapon: truncate value*manufacturer-price-mod, then truncate lerp(logistic,15,160)*value,
logistic=1/(1+exp(-4*(2s-1))); apply combined sword/global factor with truncation.
Armour: quality>0 and coverage uses s² interpolation of classLo*matLo to classHi*matHi;
otherwise own value. Apply armour/clothing category, relative-price and slot factors separately:
hat3=0.3, legs6=0.5, shirt8=1.5, boots9=0.2, others1. Quality0 uses own value, not low endpoint.
Crossbow107/limb111: s² between value/value1, category multipliers are swapped (robotics for107,
crossbow for111). Container46 uses own integer value without multipliers.
The runtime price passes the item's level (vfunc 0x2b8, called first in `FUN_1407a8f90`) as the quality, never the saved `quality`
(**Verified**, vtables): Weapon, Armour, Crossbow and RobotLimbItem share `FUN_1400d31c0` (the Gear level int `+0x224`); Item, MapItem,
NestItem, MoneyItem, BlueprintItem, SeveredLimbItem and ContainerItem return 0. So armour at level 0 is worth its own `value`.

Ingredients recursively price same quality with category flag1; sum rounded
ingredientValue*(v0/100)*(1+profitability). **Observed** v0 interpretation; other steps verified
in disassembly. Plain artifact skips multiplier; food-crop uses food/global, other items use
trade/global, including non-trade items. Gear stores value at construction; generic value can
recompute. Runtime tables below retain class-specific sale/player-crafted/armour reductions.

## Units, stacks and traders

**Verified** sell1 means player selling to shop; sell0 buying. Generic unit: base value, town
market factor with truncation, loot fraction on sale unless unstolen trade item, charge fraction
when count1, finally trader factor. Stack total: p*n-trunc(p-p*charges/maxCharges), except count1
uses unit directly. The trade-window's split of exchanged-stock units remains **Observed/Unknown**.

**Observed** trader T=buyMult*culturePrice, halved for the decoded stolen-to-player-faction case.
**Verified** buyMult comes from trader squad template and applies both directions. **Observed** (`FUN_14079e6d0` -> `FUN_14092bec0`): the culture comes from the
trader's town (its `isTown`, vtable 0x268): the town's culture (`Town+0x4a8`) if it names the item, else the culture of the
**town's** faction (`getFaction`, vtable 0x58; `Faction+0x90`), never the trader's own faction; no town: 1. Duplicate culture entries
last-win. Trade-price percentage=v0/100, zero means100%. Culture also defines illegal/forbidden goods and buildings.
**Verified** (probe of the merged base data, 2026-10-24): no UC town has a culture, so UC towns price by FACTION United Cities'
`empire` (also the culture of Traders Guild, Empire Peasants), for every trader there, thieves included: Hashish 1200%, Bloodrum/Hemp 400%, Sake 150%, the first three illegal. Hashish elsewhere:
holy 400, slavers 600, shek 350, `empire SW override` towns 500, swamper 100.
**Observed** town market factor starts uniform[1-m,1+m), m0.3, then clamps against global-town
factors/default1 within distance/1e6*m*10. Actual setup-list membership/order remains unknown;
save factors span0.7..1.3. Only generic items use market factors; save v0=trunc(factor*10000),
load /10000, accepting legacy goods-key layout (**Verified**).

**Item construction** (**Verified**, `FUN_14075faf0`; container `FUN_140763810`; `ItemInfo.From`, `ItemFactory.Make`): max charges = record `charges`, lifted to 1 when below 1 (K = 1.0f,
`.rdata`). Current charges start at 1 and are set to the max for an ITEM only. Item function (saved, `FUN_1407609e0`): WEAPON 5, ARMOUR 6, CONTAINER 4 (set after the base
constructor), ITEM its record value, others 0; 7 reads as 0. An ITEM of function 3 or 15 with max charges above 1 has max and current multiplied by `food quality mult` (0.5; raw
meat included) and its `trade item` flag cleared (so it always sells at the loot fraction, no "Avg. Price"); function 3 with a single charge becomes 0 (no food), 15 stays.
Base data: 17 of the 18 food and raw-meat ITEMs lose their `trade item` flag (Human Teeth, 15 with one charge, keeps it). **Starting items** (**Verified**, `CharacterHuman::setupInventorySections` `FUN_14062c4e0`, `FUN_14075d570`): a
single unit (v0 = 1) of a CHARACTER `inventory` entry with current charges above 1 starts with a random whole number in [1, trunc(max)]; stacks, blueprints, loadout gear and death items stay
full, and an ANIMAL_CHARACTER's list (`FUN_14062a130`) is not rolled. Meitou: `ItemFactory.Build` with the spawn seed, key and entry index (`Rng` Spawn).

## Shops and transactions

Stock derives vendor/item lists and template count/money/restock fields, with culture-forbidden
items excluded. Squad and building shops have distinct setup paths. Keep stock/purse/refresh
state and inventories separate from displayed trade UI. Buy/sell checks availability, quantity,
charges, both parties' funds and inventory fit. Stolen goods require fence/catch logic; selling
success alone does not prove legal transfer. Restock, blueprints/research money, artifact and
money-item branches are scoped in the expressions/field tables and undecoded questions.

- **Stock grid** (**Observed**): the trader window shows its trader's `backpack_content` section ([ui-screens](ui-screens.md#6-inventory-trade-dialogue-build)).
  The base game's CONTAINER `Shopkeepers Backpack` (`910-gamedata.base`) has storage 20 x 20 (raw read of `gamedata.base`); which characters wear it is not traced.
  The window's grid is 18 x 18 for a squad trader and the building containers' merged size for a building shop (**Observed**, `FUN_140953850`; details in ui-screens).
  Meitou's squad shops hold one 18 x 18 stock (`Shop`). The 2026-10-17 reference screenshot's trader (Ronin Construction Trader) shows about 28 columns.
- **Stock stacks** (**Observed**, `ShopTraderInventory::vfunc_1` = `FUN_140955f50`, the stock section's constructor): stack bonus minimum 1, mult 50, so a stock cell holds
  50 x the item's `stackable` (Building Material, `stackable` 1: 50). Meitou: `Shop.StockStackMult` on both shop kinds.

### Trading range

(**Observed**, `FUN_14070d290`; its five callers are the one-unit move `FUN_140713d20`, the trade window `FUN_140715560` and `FUN_140712250`, `FUN_140712490`, `FUN_1407139a0`.)
An item may move between two inventories of different owners only within an X/Z distance of the owners' positions; a failure is result 1, "Out of trading range.",
with the sound `gui_notrade` (`FUN_14070f7e0`). The limit is 1000 unless a flag is set: the caller's flag (the one-unit move sets it when the target is a building's
inventory, `uses_building_category`), or an owner of type 1 whose +0x2f8 is 2, or of type 0x5b whose +0xc0 object has +0x2f8 = 2, or the source owner's +0x78 object passing
its slot +0x260 (meanings **Unknown**). Flagged: the owners must not be enemies (faction test) and must be on the same floor, and the limit is 60, or the source owner's bounds radius + 40
when the source is a building (class 0). The limits are stored squared (1e6, 3600). Same owner: always allowed. **Unknown**: which windows the three drop callers flag; whether take-all uses the check.

Opening a trade from dialogue (**Observed**, read): the Trade effect (`FUN_1409524f0`, the speaker's squad must have stock) only queues a pending open (kind 1, both
characters) on the player interface (`FUN_140791830`; kind 3 is loot/inspect, `FUN_140354dc0`); its per-frame update (`FUN_1406ea070`) opens both inventory windows
(`FUN_1407918a0`) with no distance test and no walk, so the trade opens where the conversation stands. Only item moves are range-checked (above).

Meitou (engine choices): window moves to and from a machine or storage need the character within `ProductionSettings.TransferReach` 40 of the building's footprint hull (for the
bounds radius + 40) or within 60 of its position (so a small storage is never stricter than the original; a General Storage Chest's hull is about 14 x 9, **Observed** with the probe, #505), to and from a container within `ContainerAccess.TradingRange` 60; both post "Out of trading range.". The loot window closes beyond 1.5 x the carry reach, or over 20 units apart in height (Meitou: the reach is on the body's floor, and the looter walks to that floor, #292; the height is the body's root or the floor under it, as a big ragdoll's root rests well off the ground: a downed Cleanser Unit 67 over the terrain, **Observed** on a player save). A held item given to a
character: within `HeldItems.GiveRange` 1000 of its window's owner, `StealRange` 60 out of a non-player window. No faction or floor test, no sound (`gui_notrade` is in no bank, [audio](../audio.md)).

### Who trades

- `IS_A_TRADER` (dialogue condition 9, `FUN_1405cff70`) and the stock are the squad's: they read the SQUAD_TEMPLATE `is trader` through the character's platoon (**Observed**, `uses_is_trader`).
  The CHARACTER field `is trader` (fcs.def) is read by no function through its string (**Observed**: string xrefs list only the three platoon readers).
- A trade is opened by a dialogue line with effect 1 (DA_TRADE). In the data only the shopkeeper's dialogue has one (**Observed**, walk of the dialogue references): Thieves Guild, Shinobi
  Trader 3 lines, Thief Boss, Thief, Ninja Guard and the squad's `dialog squad` (TOUGH 1234) none; Skeleton Barman, Smuggler Barman, Stone Camp Trader yes; Shop Guard, Traders Guard, Slaver,
  Samurai no. Of 125 trader templates with vendors in the probe load order, 89 mark a member CHARACTER `is trader`.
- DA_TRADE needs no stock: the window shows the trader's squad stock, for a squad with no home building its members' worn bags merged ([trader window](ui-screens.md#6-inventory-trade-dialogue-build)),
  so a squad that was never filled trades what those bags hold and buys with its purse. `Caravan` (1161, Traders Guild) and `Caravan NW` (43970) have `vendors` (`caravan (survival
  items)`) but no `is trader`, and their boss's Trade line (48150) has no condition: in the unmodded game they open an unfilled window (**Verified**: the factory and the restock reach the
  squad filler only through `FUN_1404ff5e0`, which refuses a homeless squad without `is trader`; the filler without that test, `FUN_1404fe970`, has no caller: no call, jump or pointer
  to it or its thunk in the exe). **Observed**: the owner's saves with ~100 mods (1.0.65, 1.0.68) hold Caravans stocked from that list with an `inventory refresh time`, which the saver
  writes only for `is trader` (`FUN_140372ef0`), so a mod sets it; which one is **Unknown**. Meitou (`TradeSystem.StockFor`): such a squad gets an empty shop, never filled, at its first trade; a
  housed squad without a shop trades from its home's stock containers, lent to the trade and handed back when it ends (none without containers).
- Meitou (the right-click Trade verb, engine): a member of a shop squad, or of a homeless squad whose template has `vendors` (a Caravan), whose record sets `is trader` or whose
  dialogue reaches a Trade line (`DialogueSet.Trades`, conditions not checked).
- A shop squad can have several traders: Flotsam Village's armoury (50348-rebirth.mod, 3 Flotsam Ninja + 3 Flotsam Refugee) has three same-named Flotsam Refugee traders, since the
  refugee's dialogue package reaches Trade under `IS_A_TRADER` and `IN_MY_BUILDING` only (**Verified**: the original's METARU save holds the same 6 members, and the dialogue data; #180).
- The context menu's pick-up verb tests "not a trader" with `FUN_1405cff70` itself (`FUN_140796f80`, **Verified**), so any member of a trader squad counts, guards included. Meitou: `World.VerbFactsOf` reads the squad template's `is trader`.
- Shop squads with no such member (8 in the probe load order: bank, armory UC, holy Armoury, holy General storage, farm shop UC, Crab Man Shop, Shop bar black desert, Cannibal
  hunter hq) cannot be bought from in the original either (**Observed**, owner's play); their stock is only in their home's containers.
- **Wages** (CHARACTER `wages`, 108 non-zero; `Character::giveBirth` / `Character::dailyUpdate`, **Verified**): a character not of the player's faction with wages >= 1 *sets* its squad's money to them
  when made and every day (the last waged member wins). "Made" is a character born without saved state: `giveBirth` (`FUN_14062b210`) skips the wages, like the bounty
  roll and `slave`, when it is given one (**Observed**, decompile), so a character coming back from a save or from out of the world keeps its squad's purse. Meitou
  (`SquadWages.Pay`, run by `TradeSystem`): on each game-day change, and once for a squad first seen after the system's first pass unless it is `Squad.Restored`.
- **Spawn money** (CHARACTER `money item prob` / `min` / `max`; `RootObjectFactory::process`, **Verified**): a newly made non-player character gets money items worth U[min, max]
  when a [0, 1) roll is at most prob (so 80-100 always pass); what does not fit is lost. Meitou: one stack of the `Cats` item (`ItemFactory.AddSpawnMoney`).

### NPC purchases

Goal 118/119 shoppers: the sale is `FUN_14074a630`, called by [`FUN_1409a14b0`](ai-tasks.md#town-goals). One unit per sale; the buyer is the shopper's character, the seller the inventory of a container in the shop building (kind 9 objects only, `FUN_14054add0`: a trader's carried stock is not a seller).
- **Price** (**Verified**): the item's slot 82 `FUN_1407915b0` (stack price) of the single unit, called with the sell flag = "buyer's faction has `+0x250`" (the player's faction), so an NPC pays the buying
  price. The trader factor `FUN_14079e6d0` takes its culture price and `buy mult` from an open trade window only (`FUN_14070e2d0` / `FUN_140710e10` read the trade-window set `DAT_142132be8` and answer
  null/1.0 with fewer than 2 entries), so with none open it is 1, and the market factor needs a town from `FUN_14070d170` (**Observed**: none outside a trade, the tooltip prices of ui-screens.md; the context
  that function reads, `DAT_142133f90` + type descriptor `141f396a0`, is **Unknown**). A window the player has open at that instant would price the NPC's unit by the player's trader (**Observed**, not modelled).
  The stolen x0.5 of `FUN_14079e6d0` (stolen from the player's faction) applies in any case; not modelled (**Unknown** whether such stock exists).
- **Money** (**Verified**): the buyer's purse is `Character` slot 55 `FUN_140791660` (player's faction: the player's purse; else the squad's `Ownerships` at platoon `+0x88`, no squad: 0); it must be at least
  the price ("I can't afford that.", nothing moves). Spending is slot 54 `FUN_1407965f0` -> `Ownerships` slot 0 `FUN_1407cbaa0`. The seller's is the building's (`FUN_14054e560`: the squad registered on the
  building, else the owner faction's); the price is added there. Meitou: `Squad.Money` of the buyer's squad, of the squad living in the building (none: lost).
- **The item** (**Verified** flow, **Observed** names): the stack of 2+ is split (a clone of quantity 1, the original -1), a single item is removed from its section. When the seller inventory's owner is not of
  the player's faction **and** `rand()/32768 > 0.2` (`DAT_1416885d8`), the add-to-buyer call is skipped: the unit is paid for and gone, the buyer gets nothing (80 %). Otherwise `Character` slot 45 `FUN_1405cb400`
  puts it into the buyer's inventory; it is never equipped or used here; without room: "No room for that item.", the unit goes back to the stock and nothing is paid.
- Not modelled: the speech text on failure (slot 56 `FUN_1405c9950`), and the gold floater and "Sell_Item" sound that only show with the GUI option `DAT_142133ae0`. Engine choices: unique items are never sold; a house's
  goods (`FillHome`) are sellers too.

### Building shops (**Observed**, decompile)

- **Which** (squad factory `FUN_140583a10`): a squad whose template has vendor lists (`FUN_140959420`, **Verified**: its `vendors`, else by its building designation the owner
  FACTION's 2/5 `item spawns armoury`, 3 `bar`, 6 `treasure`, 8 `HQ`, 9 `resident`, 11 `resident small` (`FUN_140958560`); `is trader` is not tested; Meitou `ShopFiller.VendorLinks`) with a home building is filled by
  `FUN_14095b210` into its home; one that also made prisoner or slave squads goes through `FUN_1404ff5e0(squad, 1)` instead, whose branch is chosen by the home handle's type (**Verified**): no home (0xb) fills a squad
  stock only for `is trader`; a home building (0) never makes a squad stock, it fills the home's containers (`FUN_14095b9d0`) and returns, retried next update, when the building or its interior is missing. So a housed
  trader with prisoners stocks its home like one without (Meitou `TradeSystem.Stock`). **Verified** (`FUN_14095b210`): it gathers the building's containers (`FUN_1409562f0`) and returns when there are none, so a home without stock containers gets nothing and Meitou makes no squad shop for it either (the base and mod data in the install: 268 shop templates with a level-data home, all with containers).
  A faction test (`+0x250 == 0`, meaning **Unknown**) gates both. `FUN_14095b210` also refuses a child object (door, mounted turret; [squad factory](factions-squads-towns.md)). So a resident without `is trader` (e.g. `flotsam refuge`, vendors `house contents`) gets its home's containers stocked. A trader template with no `vendors` but a designation list is filled the same way (`TradeSystem.HomeTemplateOf` accepts a trader whose vendors are empty; one with vendors goes through the shop path); in the merged base and mod data one template is affected (Ninja Gang of rebirth.mod: `is trader`, designation list, no `vendors`), none of the base game.
- **Restock** (`ActivePlatoon::vfunc_4` calls `FUN_1404ff5e0(platoon, 0)` once `inventory refresh time` passes): the home branch skips a building whose interior object's `+0x10` is set
  (meaning **Unknown**); the first fill passes 1 and never skips.
- Meitou, non-traders (`TradeSystem.FillHome`, engine choices): the stock rolls as a building shop's, then each unit goes into the first stock container with room (their own grids and
  stack bonus), what fits nowhere is lost, the containers holding something are locked, the empty ones unlocked, the purse is untouched; refilled every `vendors refresh time` only while the containers hold nothing but unique items ([artifacts](#artifacts)).
- **Containers** (`FUN_1409562f0`): the home's interior objects whose functionality is 2 BF_RESOURCE_STORAGE, 9 BF_SHOP or 13 BF_GENERAL_STORAGE and that have an inventory.
  The filler unlocks their locks, sorts them (key **Unknown**), optionally clears them, puts special items first, then the vendor items; what fits nowhere is destroyed; then
  `FUN_1409565e0` locks each that holds something.
- **Special items** (`FUN_140958740`, **Verified**): on a refresh with specials, the template's `special items` (ITEM, 34 templates) and then its `special map items` (MAP_ITEM, 10), both when both exist: v0 of each,
  in stacks of at most the record's raw `stackable` (a map item too; the stack limit of 1 for map items is not used here), flagged unique with their quantity, offered to the building inventories first; what fits nowhere is destroyed. Meitou: on a squad's first fill, into
  the stock before the vendor items; the unique flag is not modelled.
- **Count**: n = round(U(0.8, 1.2) x `vendors fill total amount` x wealth), at least 1. Items: the vendor lists share n by weight as for squads (`FUN_140959800`, **Verified**: each list's share is (v / sum) x n rounded half away from zero, and the last list takes max(what is left, its own share), so the total can exceed n; Meitou `ShopFiller.Fill`), but every list's
  money roll (`money item prob` against U(0, 1), then U[`money item min`, `money item max`]) is summed into one stack of the money item first.
- **Purse** (the platoon's money): `vendor money` < 1: trunc(clamp(0.25 x value of the placed items, 3000, 25000) x wealth); else trunc((`vendor money` + r) x wealth) with r an int from
  -0.4 x `vendor money` (the upper bound is not shown by the decompile).
- **Wealth** = clamp(faction wealth, 0.2, 1), but the faction wealth function (`FUN_1402dd530`) is a stub returning 1.0, so wealth is always 1
  (**Verified**, kenshi_x64.exe decompile: 9-byte function, constant return). It scales only shop stock count, purse and nest spawn weight; no price reads it.
- Meitou (`Shop.ForBuilding`, engine choices): the stock is the shop's one inventory in which each stock container's grid is a rectangle of its own (`Inventory.Regions`, packed by maximal rectangles, the lowest of four orders; an item never spans two), the trade window shows them all with the cells outside every container greyed (a drop there, or across two containers, goes to the nearest free container cell; "No room for that item." when none fits), a container's window only its own. The money stack goes to the purse, r is symmetric,
  stock is saved on the platoon as squad shops' ([save keys](../formats/save.md#npc-platoons-meitou-mapping); the original keeps it in the containers, zone file). Observed with real data (Squin, 2026-10-24): 3 to 13 rows more than the packed size (geometry: 300 random orders found none lower), e.g. the Bughouse armour shop 18 x 274 for 271.

## Artifacts

**Verified** by decompile (`FUN_1400b90a0`, `FUN_140931a90` and the functions named below); the base data by probe.

**The towns' lists** (`FUN_1400b90a0`, run once the towns exist in a new game and in an [import](../formats/save.md#importing-a-game-savemanagerimportgame-fun_140378a30)).
From the first ARTIFACTS record (base game: one, `44877-rebirth.mod`):
- `items` (ITEM): v0 units each, level 0. These are the item artifacts; the rest are gear.
- `armours` (ARMOUR): v0 units at level 80; `armours hq`: at level 95.
- `crossbows`, `robotics`: v0 units at level 95 and v1 units at level 80, each when above 0.
- `weapons` (MATERIAL_SPECS_WEAPON, a model): the WEAPON_MANUFACTURER other than `PLAYER_WEAPONS` whose `weapon models` lists it (the last one in data order); one artifact per
  WEAPON of its `weapon types`, each with the entry's v0 units, the model as material, the manufacturer as maker and the manufacturer's v0 for the model as level.
- In `items`, `armours`, `armours hq` and `weapons` the units are read by the entry's position among the entries whose record exists.

Base data: `items` Ancient Science Book 160, Engineering Research 30, AI Core 37; 29 + 24 armours, 6 crossbows, 16 limbs, 7 models (Catun No.1-3, Mk I-III, Edge Type 1) giving
223 weapons of 1 unit. An artifact's value is the [item value](#item-value) at its level without the sale multipliers (a weapon with its maker's `price mod`).

A town takes item artifacts when its TOWN `item artifacts max value` is not 0, those whose value lies in [`item artifacts min value`, max]; gear likewise by `gear artifacts min` /
`max value`. Types 0 (nest), 8 (nest marker) and 10 take none. Base data: 32 towns with an item range, 71 with a gear range (37 of the 106 ruins have one or both). Towns are ordered
by max, min, TOWN string id, then position x and z; artifacts by value, then the item, part 1 and part 2 string ids (one with the part
before one without; two without it compare equal), then level.

- **Gear** (a boost 1.60 `mt19937` seeded 11667085): first each gear town in order takes one unit of the first gear artifact in order that its range holds. Then every gear artifact,
  in order, gives each remaining unit to one of the towns whose range holds it, picked by `uniform_int` over them. One no town holds is dropped.
- **Items** (no randomness): an item artifact's units are spread over its towns in order by their max: with average = (sum of the maxima) / units and a balance starting at -1,
  before each unit a negative balance moves on to the next town (adding its max), and each unit takes the average off the balance. One no town holds is logged
  ("Can't find any towns for artifact: <name>") and dropped. A weighted pick of leftover units follows but is reached only by an entry of fewer than 0 units, which never ends (as does a gear entry of 0).

A town's list holds (item, part 1, part 2, level, units), its gear first, an artifact given to it again added to the same entry. It is saved in its GAMESTATE_TOWN (`FUN_1403709d0`,
read by `FUN_14036fc30`): int `artifacts count`, then per entry k the strings `artifact_<k>_itm`, `artifact_<k>_mat` (part 1), `artifact_<k>_man` (part 2) and the ints
`artifact_<k>_lvl`, `artifact_<k>_qty`; a load adds the entries whose item exists. An import keeps the states of the player's towns only (with Buildings), so the others start empty
and the player's keep their entries and get more.

**Distribution** (`FUN_140931a90`), the last step of the resident chooser (`FUN_1409370d0`). `FUN_140a0e760` runs that, once every newly active zone has loaded, for each
town not owned by the player that has a building in a zone made for the first time; a town with an empty list does nothing.
1. Its candidates (`FUN_1408cba20` over the town's loaded zones, `FUN_1408d10f0`): its buildings with an interior holding at least one object, not the player's, whose resident template
   has a non-zero SQUAD_TEMPLATE `item artifacts base value` (for items) or `gear artifacts base value` (gear), ordered by that value, highest first. With none at all the list
   is kept.
2. Each unit of each entry is made as an item at the entry's level (a weapon of the entry's maker and model) and flagged unique (`FUN_1400b8710` sets `+0x12a`, the
   tooltip's `[Unique]`: plain AI Cores and Catun No.1 weapons show it too, #344); the list is emptied. Items are ordered by value,
   highest first.
3. A new `mt19937(11667085)`: each item, items then gear, goes to a building picked by `uniform_int` among the first ones whose base value is at least the item's; with none it is
   destroyed (debug log "[Town] Artifact '<name>' has no building to be put in. Value: <v>").
4. In each building given items, its containers are its interior objects of function 2, 9 or 13 with an inventory: each one's first section is emptied (items destroyed, unique ones
   too), and they are ordered by their lock's level, highest first. The building's items, highest value first, go one by one into a container picked by `uniform_int` among the
   first 1, 2, 4, ... (doubling per item, at most all); one that does not fit tries the others in an order shuffled (MSVC 2010 `random_shuffle`) by a copy of the generator taken
   after step 3. Then each container holding something is locked. What is placed nowhere is destroyed.

Ties (equal base values, item values or lock levels) and the order in which the buildings draw follow memory addresses.

**Afterwards.** The resident chooser only records each building's squads (`FUN_14057f4c0` makes no characters); a squad is built when it first loads (`FUN_1404ffad0`), and only
then does a shop stock its home ([building shops](#building-shops-observed-decompile)), on top of the artifacts. That fill and every restock (`FUN_14095b9d0`) clear their containers with
`FUN_14074a5b0`, which spares unique items, so artifacts stay until taken; the distribution's clear (`FUN_140749c20`) spares nothing. The trade window lists the first section of every
interior object with an inventory of the trader's home (`FUN_140953850`, `FUN_1409528d0`), unique items included: a shop sells its artifacts. An item's flag is saved as the bool
`unique`, written only when set (`FUN_1407609e0` / `FUN_140761550`).

**Meitou** (`Artifacts`, `PopulationSystem.Artifacts.cs`): the lists are made for a new game and an import (`SaveImport.Filter` writes them into the town states), kept per town,
saved and read back with the keys above, and given out when a town makes its residents. Engine choices: the candidates are all the town's standing buildings with `interior` parts whose named interior layout (their residents', [layouts](factions-squads-towns.md#town-residents))
places an object, and whose template Meitou housed there ([town residents](factions-squads-towns.md#town-residents)); ties keep the buildings' placement order, the entries' order and the containers' layout order, and the buildings
draw in placement order; an entry of 0 or fewer units is skipped. A building with no interior objects is no candidate, as in the original; what goes to a building in another town's centre zone is placed, but a save made before
that zone is visited writes no file for it, so loses it. Items flagged unique (`ItemInstance.Unique`) do not stack with others; a shop
fill and `FillHome` keep them, and a building shop takes its containers' items into its stock when it is made and gives its unique items back to them when its squad is
dropped or made afresh; once nobody of its squad is alive the shop closes and its whole stock goes back into the containers, which a save keeps with the building. Not modelled: the shop fill's container choice; after a fill each
stock container holding something is locked (`FUN_1409565e0`: lock byte `+0x20` set when the inventory is not empty, **Observed**, #328). Not modelled either: an import of a save that has no state for a placed town (a mod's town added since) gives that town
no artifacts, as Meitou adds no state for a town a loaded save lacks. Tests: `ArtifactTests`, `TownArtifactTests`.

## Crimes, bounties and prisoners

Ledgers are per wanted faction; crimes pending at deed time and witness booking are distinct.
The per-character crime tick `FUN_1405cbb80` (bounty expiry, skipped while imprisoned; then the pending timer at ledger `+0x90`: at or below 0.0 clears the code and victim, else it is reduced by the tick delta) is jumped over by the character tick `FUN_1405d00b0` for a dead character (`+0x5bc`): **Verified**. A dead body's bounties never expire and its pending crime stays frozen.
CONSTANTS categories define theft/assault/murder/looting values. Expiry is0.04 hours/cat and
permanent from10000; capture, claiming, bail and prison sentences use separate paths.
Witness/innocence details remain partly unknown; a killing is no crime (11.6), only relation events.
Slavery/prisoner/animal transactions and loot generation are not equivalent to ordinary shop
trades. Consult [factions](factions-squads-towns.md), [carry-loot](carry-loot.md) and the retained
unknowns before implementing unsupported branches.

### 3. CONSTANTS price block

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
| 0x3e `142133ec8` | `blueprint price mult` | 1.5 | [1.5] | as is | money of a generated blueprint research ([blueprints](buildings-production.md#blueprints)) |
| 0x3f `142133ecc` | (none: the loader writes the constant 0.3 itself) | 0.3 | n/a | hard-coded | `Armour::vfunc_81` (see section 5) |

### 4.2 ARMOUR and clothing (type 3)

The class and material tables are the same two tables [combat.md](combat.md#armour-and-medical-hit) uses for defence, built by the
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

### 5. Runtime unit price: Item virtual slots 81 and 82

Every inventory item class has slot 81 ("value of one unit, with a sell flag") and slot 82 ("value of the whole stack").
RTTI gives the implementations (`rtti.tsv`; the slots 81 and 82 are at `+0x288` and `+0x290` of the vtable); **Verified** by reading each (also the slot 82 `FUN_1407915b0`, which calls slot 81 through `+0x288`). The byte `sell` is **1 when the item is being
sold to a shop** and 0 when it is being bought from one (**Verified** in the trade window `FUN_140715560`: the error
code is `(sell != 0) + 3`, i.e. 3 "You can't afford that." for a purchase and 4 "The shopkeeper can't afford that." for a
sale, and the same flag selects which price is computed).

| Class | Slot 81 (`sell` = 0) | With `sell` = 1 |
| --- | --- | --- |
| `Weapon::vfunc_81` `FUN_1408a52f0` | `trunc( T * stored )` | `trunc( T * stored * f )`, `f` = `loot price mult player weapons` if `Weapon+0x208` is set, else `loot price mult GEAR` |
| `Armour::vfunc_81` `FUN_1408a9c20` | `stored`, then if `Armour+0x1e8` is set `* 0.3` (the hard-coded 0.3 of section 3), then `trunc( T * . )` | the `stored` first gets `loot price mult player armour` if `Armour+0x208` is set, else `loot price mult GEAR`, and then the same 0.3 and `T` steps |
| `BlueprintItem::vfunc_81` `FUN_1402d30f0` | `trunc( T * value )` (`value` = the research `money` stored at `+0x1e8`, [blueprints](buildings-production.md#blueprints)) | `trunc( T * value * loot price mult GEAR )` |
| `MapItem::vfunc_81` `FUN_14075d950` | `trunc( T * value )` (the int at `MapItem+0x1e8`; where it is set was not traced) | `* loot price mult` |
| `MoneyItem::vfunc_81` `FUN_14001352a` (thunk to `FUN_14075f660`) | the record `value`, nothing else | same |
| everything else (`Item`, `InventoryItemBase`, `Gear`, `NestItem`, `ContainerItem`, `SeveredLimbItem`, `RobotLimbItem`) `FUN_1407a8f90` | see below | see below |

### 9.1 SQUAD_TEMPLATE fields (data = **Verified** by probe over 959 templates)

| Field | Meaning | Data |
| --- | --- | --- |
| `is trader` | the squad is a trading squad | needs a vendor list |
| `vendors` | list of VENDOR_LIST (val0 = weight of the list) | |
| `vendors fill total amount` | how many items the vendor holds (default 15 in the editor; 0 or below means 100 in the code) | 0 x279, 15 x397, 10 x14, 100 x19, 20-40 common, 1000 x1 |
| `vendor money` | the building-shop vendor's purse (building fillers only, see 9.2); 0 = derive from the stock | 0 x954, 1000 x4, 250 x1 (the five non-zero templates all come from mods of the probe load order, none from `gamedata.base`) |
| `vendors refresh time` | game hours between stock refreshes (<= 0 means 24000) | 24 x874, 12 x15, 48 x13, 72 x13, 70 x10, 99 x10, some 99999 |
| `buy mult`, `buys stolen`, `buys illegal` | section 6 and 8 | |
| `robotics levels`, `crossbow levels` | average grade g (ArmourRarity 0..5, editor default 1) of stocked limbs / crossbows. Limbs (`FUN_140955370`, **Verified**): weights g 100, g-2 22 (g > 1), g-1 44 (g > 0), g+1 44 (g < 5), g+2 22 (g < 4), an int roll against the running sums; the grade's 5/20/40/60/80/95 (`FUN_140952320`) is the limb's `level` (the factory's grade argument). Crossbows (`FUN_140955210`, **Verified**) likewise with g-2 20, g-1 33, g+1 33, g+2 5, only g when g is 5; a shop crossbow gets no manufacturer (the factory call passes none) | values not surveyed |
| `special items`, `special map items` | extra guaranteed stock (`read_special_items_n3` `FUN_140958740`) | not decoded |

### 11.1 Ledger

Per-character hash map keyed by faction (`Character+0xf0`). Entry (`FUN_1405e7ee0`): `+8` amount int, `+0xc` crime bitmask
(`1 << code`), `+0x10` claimed byte, `+0x18` double game-hour timestamp of the last raise (**Verified**: crime add, claim marker
`FUN_140854170`, save loader `FUN_140625e60` / writer `FUN_1406262b0` agree). Save keys: [save.md](../formats/save.md).

### 11.2 How a bounty is gained

- **Crime**: `FUN_140853ec0(crimeEvent, faction)`: if the crime code is nonzero and has not been processed yet (`+0xa4` flag), and the
  character's **`NPC class`** (`+0x1a0 -> +0xdc`, the same read as the police test, carry-loot.md 4.3) is not 8 (BANDIT),
  5 (DIPLOMAT) or 1 (LAW_ENFORCEMENT), then the crime's amount is **added** to a ledger entry chosen by `FUN_140852010` from the given faction: a caged character's prison
  owner's, else the given faction's **`legal system`** (`+0x38`, the first FACTION `legal system` reference; **Observed**, its only reader `FUN_1407f4f40`
  stores it there; e.g. Bounty Hunters and Medics Guild -> United Cities) when it has one, else the given faction itself when its `+0x40` flag is set (else a null key that
  nothing reads: the flag is still set and the culprit marked recently wanted, but no bounty is kept). `FUN_140851fb0(faction)` is the same choice without the cage rule ("booking
  faction" below). **Verified** (decompile of `FUN_140853ec0`, `FUN_140852010`, `FUN_140851fb0`, `FUN_140853e20`): every ledger read, claim and erase by a faction goes through the
  same key. Meitou: `CrimeSystem.Book` (`BountyBook.Book`; cage owner while imprisoned, `CrimeFactions.BookingOf`), every lookup through `BountyLedger.BookedFor` / `BookedAmount`, the release's erase of the releaser's and the jail's entries too (`FUN_1408539f0`).
  The given faction is the **booking police witness's** (or the police in the hit rule of 11.6), not the victim's (**Observed**: both callers pass their own faction); the crime bit is set, the timestamp is reset to now, and the "wanted" UI is refreshed. The very first crime
  code (1, Enslaving) sets the flag but adds nothing (**Observed**; the code returns before the add). The same function also sets the status float `Character+0x1a0 -> +0x110` to 250.0: the **"recently wanted" marker** that makes a bounty
  visible at once (11.8); setting or witnessing a pending crime (`FUN_140852e10`, `FUN_140851f40`) sets it too. Its decrement was not traced.
- **Crime amounts** (`FUN_140851e40`; **Verified**: a switch with no other inputs; names from the game's own name table `FUN_140856530`). The editor's
  `CrimeEnum` (FCS decompile) names the codes differently: 2 `CRIME_LOCKPICKING`, 6 `CRIME_ASSAULT_VIP`, 7 `CRIME_SLAVE_FREEING`, 9 `CRIME_TERRORISM`,
  14 `CRIME_FARM_EATING`; the rest match the game's labels:

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
| 7 | "Terrorism" label; editor `CRIME_SLAVE_FREEING` (given for lifting a prisoner out of a cage, [locks](locks.md)) | 5000 |
| 8 | Smuggling | 5000 |
| 9 | Terrorism | 5000 |
| 0xf (15) | Kidnapping | 5000 |
| 6 | Treason | 50000 |
| 5, 10 (Looting), 0xe (14, Crop Theft), others | Assault, Looting, Crop Theft, default | 500 |

- **Crime names are catalogue messages** (**Verified**: the zh_CN `main.po` source references cite the name table, `sensoryData.cpp` 145-177, for each name).
  Each name is a plain `msgid`, so the player's language shows it (zh_CN: Burglary 入室偷窃, Escaping Prison 越狱). **Observed**: `Assault`, `Looting` and `Kidnapping` share their
  `msgid` with the AI task names (zh_CN 攻击, 抢劫中, 绑架中), and the zh_CN catalogue renders `Fencing` as 剑术 (the sword skill; the translator note says "selling stolen goods"), so the
  original shows those as well. Meitou: `CrimeTable.Name` translates at the point a message or the HUD is built;
  the ledger and saves keep the code (the bit mask `crimes<n>`), the world hash never sees the text.

- **Trespassing** (perception `FUN_140859680` -> test `FUN_140852350`, **Observed**): an NPC seeing a player character inside a
  building sets a pending Trespassing (code 11) against **its own** faction (victim: itself), fires dialogue event 17 and gives its
  faction's memory of the player (`FUN_140673a20` -> `FUN_140673930`: one entry for the whole player faction, a `SubjectiveTags`
  stamped with the time) the long-term tag 1 MY_INTRUDER ([dialogue](dialogue.md#enums), DC 23); no base-game line reads that tag
  (**Verified** by probe). Not tested when the witness regards the target as an enemy (Character `vfunc 125`) or an ally
  (`vfunc 126`: same faction, else `FactionRelationsIsAlly`), when the target is in a code-0xb place, or is chained to the witness
  (`FUN_14032e1e0`: target `+0x320` "chained mode" and the chain's hand id = the witness's). Two cases, both needing the residents'
  **"house locked"** flag (the owner platoon's AI `+0xe8`, saved as `homelok`, [locks](locks.md#doors)) unless noted:
  the **resident** case (the target is in the witness's own home): flag set, and the home is not public (vfunc 82, `public day`;
  or `+0xc4 != 1`, **Unknown**) or the hour is outside 7..22 (game clock `DAT_1421303d0`+0, the hour int). Same-town targets of
  NPC class 1, 2, 5 are exempt. The **police** case (a police-like witness, someone else's building): the building's faction does
  not regard the target's as an ally, the building is not public, and the target has a slave state or the owner platoon's flag is
  set; never when that platoon's leader regards the target as an ally. No hour test. The witness function then books it as any
  crime (amount 100). `FUN_140852240` fires event 17 without a crime (a squad of the witness's town; NPC side). What follows is the
  event-17 lines' data (**Verified** by probe: "Intruder!" with DA 6, "Leave my abode" with DA 12 then DA 33/34, "/GUARDS/!" with DA 33).
- **Spawn bounties** (`FUN_14062b210`, **Observed**): CHARACTER `bounty chance` vs an inclusive roll 0..100 (`roll < chance`);
  amount `trunc(bounty amount + 0.5*k)`, `k` an integer in `[0, 2*bounty amount fuzz]` (the fuzz only adds), for each
  `bounty factions` entry (else the faction whose string id is the literal `defaultEmpireFactionSID`, looked up by `FUN_1402e7a20`; in gamedata.base that is United Cities, **Verified** by the install's bytes), added without a crime bit (`FUN_140853e20`), under that faction's booking key
  (`FUN_140852010`: its legal system, else itself when it books, else a null key nothing reads; argument passing read from registers, **Observed**). fcs.def: "Defaults to the
  empire"; 117 base CHARACTER records have a chance, 64 of them no `bounty factions` (**Verified** by probe). Whether the fuzz is rolled
  per faction is **Unknown**.
  An amount of 0 or less is booked too (`FUN_140853e20` adds and stamps the time unconditionally): the entry stays at 0, since the expiry sweep
  `FUN_1405cbb80` and "has a bounty" only consider amounts above 0 (**Verified**, decompile).

### 11.3 Lifetime

`FUN_140851f10`: `0.04 * amount` hours below 10000, else never ("Notorious"). The character tick `FUN_1405cbb80` removes at most
one expired entry per tick and is skipped while `Character+0x2f8 == 2` (imprisoned); each crime resets the timestamp (**Verified**, `FUN_1405cbb80`). Meitou: `BountyBook.Expire`, at most one per tick of its sweep period.

### 11.4 Claiming

Two paths pay the player a captive's bounty (**Observed**). Both read the captive's entry for a booking faction, pay only when it is
nonzero and unclaimed, halve it (truncated) when the captive is **dead** (`+0x5bc`, [carry-loot](carry-loot.md)), add it to the player's
money, mark the entry claimed (it never pays twice) and show "Received c.{1} reward.".

- **Caging** (`FUN_140330d50`, called by `Task_PutInSomething` after a successful put-in): the *cage* is the first argument (its
  building function, vtable slot 94 = `Building+0x158`, must be 8 `BF_CAGE`; the editor's `BuildingFunction`), the carrier the second,
  the captive the third. Pays when the carrier is of the player faction, the captive is not, the cage owner's faction is not the
  player's, not `not real` and not BANDIT (`fundamental type` 8, `CharacterTypeEnum`), neither carrier nor captive has the escaped flag
  (status `+0xe8`), and not (cage faction SLAVER 7 or `FarmBuilding::vfunc_15(cage) +0xd8 == 5`, read as a town of type 5 `TOWN_SLAVE_CAMP`: the function walks the
  cage's parent chain to its root, not verified as the town) unless the carrier's status type (`FUN_1405c8aa0`: status `+0`, 1 slave, 2 escaped) is 0.
  The amount is the captive's bounty for the cage owner's faction; plays `Bounty_Fulfilled`. The same put-in also sets the sentence (11.5). The "carrier of the player faction" test reads the carrier's FACTION object (`+0x250`), not whether
  the carrier is player-controlled; every cage put-in by the task also has the captive's squad remember the carrier as captor (`FUN_140673a20(captive, carrier, 5)`) and sends a kind-12 relation event. Meitou: `ArrestSystem.Claim` tests the carrier's faction;
  `TownDutySystem` raises `FactionDeeds.Caged` and calls `Claim` after its cage put-in.
- **Dialogue** `DA_PAY_BOUNTY` (10) (`FUN_140680560` case 10): pays for the body the talking player carries, with the speaker's
  booking faction; then the speaker takes the body (tasks 213 `LIFT_PERSON_SNATCHING_ALLOWED` and 112 `FIND_CAGE_AND_PUT_IN`, memory flag 5 on both),
  **whether or not a bounty was paid and in any town** (**Verified**, `FUN_140680560` case 10: the tail runs once the carried body resolves; Meitou hands the body over for DA 10 alone too).
  Base data uses it in `EV_PLAYER_TALK_TO_ME` dialogues gated by `DC_CARRYING_BOUNTY_ALIVE` / `_DEAD` (e.g. "shek bounty desk"),
  together with `DA_ARREST_TARGETS_CARRIED_PERSON` (30) and +relations (**Verified** by probe of the merged data). The two effects come in either
  order (shek desk: alive 10, 30, 4; dead 30, 10, 4; **Verified** same probe), so the body is still carried when 10 runs. Meitou: [simulation](../simulation.md#arrest-and-jail).

### 11.5 Bail and sentence

- **Sentence** (`FUN_140854950`, the only reader of CONSTANTS `prison time` 1.0; reader **Verified**, formula **Observed**): set by
  `Task_PutInSomething` (`PUT_IN_CAGE` 108) only when the captive has a bounty with the cage owner's faction. It stores the start
  (now, double game hours) at ledger `+0x98` and the length at ledger `+0xa0` (float): `trunc(bounty * 0.02 * prison time)` hours,
  999999 above 25000, 16 when the bounty lookup returns 0. Saved as character keys `sentence` (start, truncated int) and `psts` (length,
  float) by `FUN_1406262b0` / `FUN_140625e60`. Its one caller is the cage mode (0x6b) of `FUN_140358670`, so every carry-a-body-into-a-cage flow uses the constant (**Verified**; Meitou: arrest, bounty hunters, kidnap `KidnapSystem` and the town duty `TownDutySystem` pass `prison time`; a slave's own entry and the delivery handover are not that task). The squad factory's spawn-time caging (`FUN_140583a10` calling `FUN_140330600` / `FUN_14032e2f0` directly) never reaches `FUN_140854950` (it is the only function that does, **Verified** by a search of the whole decompile), so a spawned prisoner or slave gets no sentence; Meitou's two `PopulationSystem` cagings call `Imprison` with the default `prisonTime`, which only matters for a captive with a bounty, and a spawned one has none: the same result.
- **Served** when `start + length <= now` (`FUN_1405c9f00`, also false for a character with a nonzero status type, e.g. a slave);
  nothing counts down. A prisoner caged without a bounty gets no sentence: whether the ledger then reads start 0 / length 0 (served at
  once, so goal 211 frees them immediately) is **Unknown** (the ledger constructor was not checked); Meitou should treat it as served. The status panel (`FUN_1405d2da0`) shows "Imprisoned, sentence time remaining: ceil(length - elapsed) hrs",
  "FOREVER" for a length >= 2000, else "Waiting to be released" (**Observed**). Bounty expiry is paused meanwhile (11.3).
- **Release** (`Task_ReleasePrisoner` 110, `FUN_1403570a0`; **Observed**): the jailer goal is AI_TASK "police free prisoners" (enum 211,
  only in AI_PACKAGE "addon- law enforcement"; the "- no freeing prisoners" variant lacks it; **Verified** by probe). Its finder
  `FUN_1408cf3c0` picks an occupied `BF_CAGE` whose occupant is not down, served and not an enemy of the jailer (**Verified**: vfunc `0x3e8` = `isEnemy`, `FUN_14079c950`, called with the occupant and 1; Meitou `LockSystem.Served` reads its faction part, `CharacterFacts.RegardsEnemy`, and frees no enemy prisoner, not even by the booking-jail fallback), among the buildings of the jailer's own town (status `+0x88`) and faction in the 3 x 3 zone cells round the jailer (`FUN_1409fc450`, `FUN_1409fb880`, **Verified**; the faction argument is the jailer's AI object's vfunc 8, read as its faction, **Observed**), the first in list order: no radius, no nearest-jailer choice. For every releaser the
  cage is unlocked and status `+0xea = 1`; then, if the releaser's faction has `+0x40`, the prisoner's entries for the releaser's booking faction and for the
  jail's booking faction are **erased** (`FUN_1408539f0`), the releaser says `EV_PRISONER_FREE_TO_GO`; if the releaser is a player
  (non-`+0x40` player faction), the prisoner says `EV_UNLOCK_MY_CAGE_OR_SHACKLES`. In those two cases the prisoner gets 133
  `GET_OUT_OF_CAGE_LEGIT`. Meitou `LockSystem.FreeBy` does the mark and both events for the Set Free walk and the goal-211 rounds alike (the mark is set for every prisoner, a kidnapped one included, **Verified** `FUN_1403570a0`: byte `+0xea` set with no test; the status update clears it outside towns). Always: the restraint is freed; "{1} has been released from prison." for a player-faction prisoner of status type 0.
- **Bail** (dialogue action 43 `CHOOSE_PRISONER_BAIL` opens `CharacterTrading_PrisonerBailout` `FUN_1406ab890`, "Pay Bounty", **Observed**): the list
  is the living occupants of `BF_CAGE` furniture in the speaker's home town's buildings that are player-faction, or not hostile to
  the speaker under further tests (`FUN_1405995d0`, `FUN_140599590`, `FUN_14059a920 < 36`). Price `2 * bounty` with a zero faction,
  which through the cage rule of 11.2 is the jail's booking faction; at least 2000 when the prisoner is not of the player's faction.
  Paying (`FUN_1406ae170`) unlocks the cage, erases the jail's entry (`FUN_1408539f0(ledger, 0)`), sets `+0xea = 1` (freed: [perception](perception.md#sense-tags-who-is-an-enemy), Meitou `Freed`), the prisoner says
  `EV_BOUGHT_ME_FROM_SLAVERY` (event 51, `FUN_140684990` with the payer, the conversation's player character, as target; **Verified**) and gets 133 `GET_OUT_OF_CAGE_LEGIT` (**Verified**, `FUN_1405d20d0(prisoner, 0, 0x85, 0)` unconditionally; one whose event call returned 0, who could not speak, first gets 128 `RETREAT_HOMETOWN`; whether a later give replaces or queues is **Unknown**);
  its squad's memory also takes the payer as FREED_ME (long tag 3, `FUN_140673930`; **Verified**), for every freed prisoner whose character resolved. Meitou: `ArrestSystem.Bail`
  writes both, and `SendOut` walks a prisoner that spoke out of the cage to its payer (133, the walk of a bought slave) and one that did not to its squad's home (128 standing for the pair). Only two base dialogues carry DA 43 (**Verified** by probe, 2026-10-21): "Shek law
  bounty" (shek bounty desk, the reply "I'm here to bail out a friend.") and "Inquisitor heretic bounty" (Holy Nation Inquisitor); no United Cities law
  dialogue offers bail.
- **Escape**: leaving by 207/256 gives a pending Escaping Prison (0xc) and the escaped flag; being lifted out gives the flag only (the crime call is refused while still held, [locks](locks.md#cages-and-beds)) ([locks](locks.md#cages-and-beds));
  a pending crime becomes a bounty only when a booking witness sees it (11.2). Spotting the flag: 11.8.
- `FUN_140851d10` (a lerp of 350/600 and 450/80-based values by a health fraction) is a different, undecoded function.

### 11.6 Booking factions and police hits (**Observed**)

- **Faction `+0x40`** (FACTION loader `FUN_1407fe270`): `fundamental type` 1 (LAW_ENFORCEMENT), else `offers bounties`. Base data:
  Police, Mercenary Police, United Cities, The Holy Nation, Shek Kingdom, Republic of Tertius (**Verified** by `CrimeInstallTests`).
  The loader also stores `fundamental type` at `+0x34` and `not real` at `+0x1d0` (the setter's "lawless" tests). The setter `FUN_140852c80` also refuses a culprit that is law enforcement, in state `+0x2f8` 2, or whose faction is the victim's, and one whose apparent faction (`FUN_140852c20`: the disguise faction while its level is above 0.6 and the recently-wanted marker is zero, else the slave owner for status type 1, else its own) is the victim's unless its slave state is nonzero (**Verified**; Meitou keeps only the own-faction test, the disguise term is **not modelled**).
- **Hitting the police** (`FUN_140665fa0`): a landed blow between a police-like and a non-police character gives the non-police one a
  pending Assault (timer 30) against the police faction, booked with it at once, whichever struck.
- **Attack order** (`FUN_140334070`): a pending Assault against the victim's faction unless the victim has a bounty with the attacker's
  faction, its status flag `+0x114` is set, it is a BANDIT, it has a pending crime, or the victim is fighting (`Character::isInCombatMode` `FUN_140436610(victim, 1)`: CombatClass +0x130), tested in that order (**Verified**); the attacker's own +0x114 is then set (before the setter, whether or not it accepts). No faction-hostility test. **`+0x114` ("ordered")** lifecycle: set by the ordered attack task starts (`FUN_140334050`, `FUN_14033bf70`, `FUN_14033c1e0`, when the task's option byte `+8` is set: meaning **Unknown**), by this booking, and for every member of the active squad by each update of the cannibal assault package (`FUN_14028a4e0` from `FUN_14028fdf0`; Meitou `PopulationCampaigns.CannibalsLoaded` sets `CharacterCold.Ordered`); cleared when the character's task target is cleared (`FUN_1405c91c0`, at task end); read only here, on the victim (`FUN_140795b30` and `FUN_1404ffad0` read a platoon object's own `+0x114`). The caller is the attack task's start with its flag set, which only row 61 Attack Unprovoked has (and the +0x114 mark is then also set unconditionally, `FUN_14033c1e0`; [fight task](combat.md)). Meitou: `CrimeSystem.Assault` (run only for `AttackOrder.Unprovoked`) and `CharacterCold.Ordered` (set by the booking, cleared in `CombatSystem.EndFight` as the stand-in for the task end; the other two setters are not modelled: a cannibal victim is a lawless faction, so its booking is refused anyway); a hostile victim faction is skipped as well (engine choice).
- **Murder**: no caller of the setter `FUN_140852c80` (26 callers, plus the 7 of its wrapper `FUN_140852e10`) passes code 4, and no
  variable-code caller can yield 4 (task finders pass 2/7, 3/10; dialogue DA 49 passes 0 to clear). A killing therefore sets **no
  crime**: its consequences are the relation events of [factions 3.5](factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile)
  (kinds 5, 3 and 4) and an Assault pending from the attack order. Code 4 only names a ledger bit (**Observed**).
- **Assault to code 9**: the setter turns a code 5 into 9 when its victim passes `FUN_140796240(victim, 1)` (decompiles as void; **Unknown** test).
- **Witness timing** (`FUN_1408580d0`, the perception's per-seen-character update, **Observed**): the witness function `FUN_140854d10` runs for a
  seen character when the update says it is newly seen (either of two flag arguments), or while it has a pending crime whose timer
  (`+0x180`) is above 5; every run that reacts sets that timer back to 20 (`FUN_140851f40`), so a watched crime stays fresh. A witness
  that regards the culprit as friendly and sees it wrong the witness's own faction costs the culprit's faction 20 relation each time
  ([factions 3.5](factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile)). A reacting witness that is the crime's
  victim (for a trespass, the witness itself) goes for the culprit, which its whole squad then takes for an enemy
  ([squad memory](dialogue.md#squad-memory)).
- `FUN_140673a20` in the trespass path is the squad-memory tag MY_INTRUDER, not a witness function (factions 3.5).

### Town alarms

**Observed** (decompile; `Task_InvestigateAlarms` names KenshiLib 1ce4a16; literals **Verified**).
- **Raising** (`FUN_1409335e0(town, code, position, culprit, faction, raiser)`): nothing for code 0. Called only by DA 33 CRIME_ALARM (the
  target's pending crime code and victim faction, at the target) and a failed kidnap ([carry-loot](carry-loot.md#kidnapping): code 15, the
  victim's faction, at the kidnapper). The town keeps its alarms per code; one of the same code and faction is moved (position, building,
  floor), else a new one is made; only the new one sets `+0x24` = 8.0 (the merge path leaves it alone), a life in game seconds that the town update counts down (`FUN_14092bfe0`, `Town::periodicUpdate` dt), the alarm being
  deleted below 0 whether answered or not (**Verified**). So an alarm dies 8 game s after it was created, however often it is raised again; the next raise then makes a new one. "Alarm raised" when the culprit is the player's, at most every 30 s.
- **Investigators**: goals 209 INVESTIGATE_ALARMS and 210 (the task's flag: only alarms of its own faction), Urgent, weight 0.1, 0-24 h, in 51
  packages of the merged load order (town and gate guards, police, shops, residents, throne rooms; **Verified** by probe). Row 209: effect
  !130 ALARMS_IN_THE_VICINITY, needs 86 atATown. Finder `FUN_14092a810(town, 0, 375, me, faction or 0)`: the nearest alarm of the An alarm raised in a building whose every entrance is closed (`FUN_1405499a0` false: doors present and none open or passable) is skipped for an investigator in the same building and halves the reach a second time otherwise (`FUN_14092a810`:52-62, **Verified**; Meitou `CrimeSystem.AlarmFor`, `FixtureRegistry.HasOpenDoor`).
  investigator's squad's town within 375 (**Verified**: the reach is halved to 187.5 when the alarm's location handle `+0x48` is not type 0xb, i.e. it was raised in a building, whatever the crime code, which the finder never reads; the location is the culprit's own `vfunc 0x1d8`, set on create and overwritten on merge with the floor `+0x60`); halved again when that building resolves, has no open door (`FUN_1405499a0`) and the investigator is not at the same location (otherwise skipped); skipped across floors (alarm floor differs and the building byte `+0xc2` is set, or more than one floor apart).
- **Update** (`FUN_140354260`): no town or no alarm ends the task. Within 100 of it: faces it and counts down (15 s at the start, 10
  after any walk); at 0 the alarm is deleted and the task ends (the only remover found). Farther: runs (mode 2) to 30 short of it on its
  own side, the count set to 10, and every sensed character (neutral or enemy, not down: `SenseItr(6, 8)`) with a pending crime whose
  victim faction is an ally of the investigator's (`FactionRelations::isAlly`: same, or 50+) is tagged TEMP ENEMY and attacked
  (`Character::attackTarget`, which only stores the target). **Verified** (decompile; `FUN_140354260`): the whole sense list is walked with no early exit and no can-attack test (the goal system decides who is offered the task), on every update while it walks; each qualifying entry is tagged and the AI's attack hand is overwritten, so the last one scanned is the target (Meitou `CrimeSystem.Investigate`: all tagged, one order for the last; the sight scan stands in for the sense list).

Meitou (`CrimeSystem.TownAlarm` / `AlarmFor` / `Investigate`, `AiBehaviour.Investigate`): as above (reach by `TownAlarm.Place`, the culprit's `SightMap.PlaceOf`, and the more-than-one-floor skip by `Floor`); alarms are not saved; the closed-building
halving and the `+0xc2` floor skip (meaning **Unknown**) are not modelled; "sensed" is the witnesses' sight test.

### 11.7 Arrest

Arrests are started by **dialogue**, not by a crime test in code (**Verified** by probe of the merged data; enum numbers from the FCS
decompile's `DialogActionEnum` / `DialogConditionEnum` / `EventTriggerEnum`, compare-by 0 `==`, 1 `<`, 2 `>`).

- **Triggers**: law dialogue packages ("police", "Npc MILITARY Law 3", "... Holy Nation Law 3", "Npc TOUGH Vigilante Law 3", gate and city
  guards) answer events 8 `THIEF_CAUGHT_STEALING_FROM_ME`, 41 `WITNESS_GENERIC_ASSAULT`, 43 `WITNESS_THIEF_OR_LOCKPICK`, 44 `BOUNTY_SPOTTED`,
  45 `ESCAPED_PRISONER_SPOTTED`, 40 `ESCAPED_EX_SLAVE_SPOTTED`, 15 `I_DEFEATED_SQUAD` ("Law enforcement defeats squad (arrest them)",
  gated by `DC_CARRYING_BOUNTY_ALIVE` false) and 1 `PLAYER_TALK_TO_ME` with lines carrying `DA_ARREST_TARGET` (29). Bounty dialogues
  branch on `DC_BOUNTY_AMOUNT_ACTUAL`: "Small bounty" (0 < b < 1050) offers a fine; refusing or lacking money runs `DA_ARREST_TARGET`;
  "small fry" (1050 < b < 25000) and "super high" (> 25000) arrest at once. "Surrender" exists only as these dialogue choices; there is no
  surrender state.
- **How the fine is reached** (**Verified** by probe of the base load order, 2026-10-21): "Small bounty" is an event-44 monologue (bubbles)
  for a *leader* (`DC_IS_LEADER` on the speaker); its "I knew it, you're a convict!" line runs `DA_TALK_TO_LEADER` (2: the speaker squad's
  leader, here itself, gets task 102 and walks up to fire event 1, `FUN_140680560` case 2, **Observed**) and sets `interrupt` "Bounty
  extortion pay", so that talk opens the fine window. No law package answers event 1 with bounty lines. The other event-44 law roots
  ("police bounty spotted" b > 500 within talking range, "small fry", "super high", Shek "Shek law spots bounty") say a bubble whose lines
  run DA 29 (police add DA 33); event 45 roots ("police escaping prisoner", "Law enforcement spots prisoner escaping") likewise.
- **Fine** (DIALOGUE "Bounty extortion pay", `Npc MILITARY Law 3`; **Verified** by probe of the base load order): one line per bracket,
  `250 < b < 351` -> 400, then +100 per 100 up to `950 < b < 1051` -> 1100; the 300 line (`0 < b < 251`) tests the *speaker's* bounty
  (`who` 0, a data slip: it never shows for a guard without a bounty). Paying needs `DC_PLAYERMONEY > fine` (strict) and runs
  `DA_TAKE_MONEY` (8, the fine) + `DA_CLEAR_BOUNTY` (49); the 1100 line also runs `DA_PAY_BOUNTY` (10). 49 erases two entries, the speaker's booking faction's and the target's town's (or jailer's) ([dialogue](dialogue.md#effects));
  As built: [simulation](../simulation.md#arrest-and-jail).
- **`DA_ARREST_TARGET`** (`FUN_140680560` case 0x1d, **Observed**): when the speaker's ownership town (`Character+0x658 -> +0x30`) is set
  and equals the speaker's status `+0x88` by pointer (**Verified**: `+0x88` is the character's current town, `vfunc 0x78` = `FUN_1405c89b0`, written as "the town the status stands in"; "he is standing in his own town", no distance; event 45 in `FUN_140859680` makes the same compare, plus further tests not modelled), and the target is not an animal (slot `0x248` is `asAnimal`): the speaker gets tasks 240 `LIFT_PERSON_SNATCHING_ALLOWED_IN_TOWN_ONLY`
  ("Capture Target", needs `isTargetDown`) and 112 `FIND_CAGE_AND_PUT_IN` on the target, and the target's squad marks the speaker
  (flag 5). Otherwise the speaker's squad marks the target (flag 4) and the speaker attacks it (task 4 `MELEE_ATTACK`). Either way the
  guard fights until the target is down, then carries it. `DA_ARREST_TARGETS_CARRIED_PERSON` (30, case 0x1e) does the same for the body
  the target carries (it is dropped and taken over), but only when the speaker's ownership town is set and equals the town it stands in (cases 0x1d and 0x1e share the test; **Verified**), else nothing happens;
  `DA_ENSLAVE_TARGETS_CARRIED_PERSON` (40, case 0x28) takes the same tail with no town test (**Verified**). Meitou: the town test is `ArrestSystem.InOwnTown`. `DA_ASSIGN_BOUNTY` (32) adds its value as a bounty with the speaker's booking
  faction, or books the pending crime when the value is 0.
- **Arrest goal** (AI_TASK "Arrest bounties", enum 204 `HUNT_BOUNTIES`, in "addon- law enforcement" and the "Gate guard ... -arrests"
  packages; **Verified** by probe): desire 1 when the cage finder finds a free cage, else 0 (AI slot 33 `FUN_140598a90`); target finder
  `FUN_14099bfc0`: the first sensed character (mask 0x400e/0x221) not carried, not of a `not real` faction, not an animal (slot `0x248`), not
  imprisoned, that has a perceived bounty with the guard (`FUN_140853fd0`: the entry for the guard's booking faction, visible while
  `+0x110 > 0`, or when the target is down and an enemy), or that the guard's squad marks with flag 7, or regards as an enemy. Its effect
  129 leads the planner to 203 Arresting (list B: target down, unarmed, carried) and then the cage goals.
- **Caging** (goals 112 / 281 "Find and put in cage", subtask 108 `PUT_IN_CAGE`): desire `FUN_1405a1140` (0 unless carrying;
  0.1 / 0.5 / 1.4 / 2.0 by status, death and bounty tests in `FUN_1405a0f10`); cage finder `FUN_1408d0770(ai, wantFree 1, function 8)`
  over the candidates `FUN_1408cbea0` gathers (interiors included): the town in the character's status (`+0x88`), else its faction's nearest town (`FUN_140927f10`, nest markers left out) when within squared `DAT_14172dd88` = 1.5625e7, the buildings within that town's radius x `DAT_1416b1640` = 1.1 of its position (`FUN_140a00690`, each passing the usable test with no character, only complete ones), plus those of the home building's town when it differs; with no town at all every zone is searched, i.e. every building (**Verified**): usable (`FUN_140621230`, `Ownerships::canIUseThisBuilding`, full rule in [locks](locks.md#beds); **Verified** by decompile), not reserved, free; weight
  `clamp(1 - d^2 / 4608^2, 1e-5, 1)`, x100 if the cage is of the carrier's faction (any other must be usable or is skipped), x100 if in the
  carrier's home (the squad's Ownerships home), x1e-5 for a law-enforcement carrier when the cage stands inside a building that is not public (`isPublic`); the **highest** weight wins (**Verified**, `FUN_1408d0770`).
  Meitou: `FixtureRegistry.FindCage` with `CanUse` (all callers: arrests, bounty cagings, kidnapping, town duty; a player character's automatic pick skips `CanUse`, as it is the AI's Ownerships test: engine reading) and, for guard arrests, kidnapping and town duty, `CageCandidates` (the gatherer's town rule; the player's put-in-cage order keeps its own range); every caller passes the squad's home (`FixtureRegistry.HomeOf`) for the x100; a building's public flag is its state's `is public day`, public when absent.
  No free cage: the finder returns nothing; what the carrier then does is **Unknown**.

### 11.8 Bounty spotting and bounty hunters

- **Spotting** (`FUN_140856f00`, **Observed**): a non-player looker within 400 units (160000 squared) recognises a player-faction
  character whose bounty with the looker's faction is at least a class threshold (`FUN_1408520c0`: LAW_ENFORCEMENT 1, MILITARY 4000
  or 2000, ADVENTURER 1000, SLAVE never, others 10000), immediately while `+0x110 > 0`, else through a recognition meter
  (`FUN_140856cb0`, fires above 1.0; rate `5 + 40 * k`, `k` a 1..2 value from the target's stats scaled by status factors 0.5 / 0.6 /
  0.75 and x2, not decoded); not while the target is carried or imprisoned. Recognition fires `EV_BOUNTY_SPOTTED` (44). The same function
  handles escaped slaves (events 36 / 40). `EV_ESCAPED_PRISONER_SPOTTED` (45) fires in `FUN_140859680` for a target with the
  escaped flag, seen by a police-like looker (or one of a bandit town's faction) in his own town.
- **Bounty hunters** (**Verified** by probe): squads "Bounty hunters wandering between towns", "Bounty hunters low", "Bounty Hunter
  Solo Shek", rebirth "Bounty hunters" (faction Bounty Hunters, `legal system` United Cities) run "hang out in a bar" / "Wandering
  from town to town" / "Patrol map (short range) LOOTING", none of which has goal 204 or a cage goal. Their dialogue answers
  event 44 for bounties > 4000 (two dialogues, split at 35000) with `DA_ATTACK_CHASE_FOREVER` (6), never `DA_ARREST_TARGET`.
  "Bounty Hunter" is NPC class 9 (ADVENTURER), "Bounty Hunter Low" class 1 (police-like). Whether a hunter delivers a captive
  anywhere is **Unknown** (nothing in its data does). Bounty Hunters is the only base FACTION of `fundamental type` ADVENTURER with a
  `legal system` (**Verified** by probe; the others with one: Medics Guild, Traders Guild, Slave Traders, Free Traders, Tech Hunters,
  Empire Peasants, Machinists -> United Cities; the empires point at themselves).
- **Animals never spot** (**Verified**, decompile of `FUN_140856f00`): its first test is the looker's vfunc `0x248` (the animal test, also used by DA 29); an animal looker, one in combat mode, or a target that is carried or imprisoned makes it return at once, so an animal never recognises a bounty, fires event 44 or starts a hunt. The base "Bounty hunters" squads (wandering between towns, rebirth "Bounty hunters", "Empire Bounty hunters") carry a Bonedog in `animals`, in the squad's faction, so they pass the faction test of `CrimeFactions.Hunts`. They fight the target only as squad mates: DA 6 tags the target as the squad's temporary enemy and mates join through their enemy scan. Meitou: the spotting and disguise loops (`ArrestSystem.Spot`, `Disguise`) skip animal lookers. In-combat lookers are **not modelled** (unchanged).

### 12. Slaves, prisoners, animals

The slave value `buyValue` (`FUN_1406751f0`, **Observed**; [expression reference](#expression-reference)): `S` = `CharStatsAnimal::vfunc_2`'s value (the strength stat
`+0x80` times `k`), `attack` = the melee-attack stat (`+0x120`), `D` = the dexterity stat (`+0x88`), `k` = `CharStats+0x34` (1 for people, an animal's age), stat offsets
from [character-stats.md](character-stats.md). RACE `buy value` is 100 for most races (27 of 53 records); other values in the data: 400 x8, 10 x4, 50 x3, 75 x2, 200 x2,
250 x2, 1000 x2, 0 x2, 300 x1 (**Verified** by probe). fcs.def gives it default 100, "percentage of normal value when bought or sold as a slave" (**Verified**); whether
the exe scales it at load is **Unknown** (the formula is read as multiplying the stored value).

| Window | Class | Price (**Observed**) |
| --- | --- | --- |
| Selling a slave to a slaver | `CharacterTrading_SlaveSelling` `FUN_1406a6f50` | an explicit override if the caller gave one, else `clamp(buyValue / 2, 200, 2500)` |
| Buying a slave | `CharacterTrading_SlaveBuying` `FUN_1406a7970` | override or `buyValue`; **doubled** when the character is of the player's faction |
| Buying an animal | `CharacterTrading_AnimalBuying` `FUN_1406a9440` | override or 14000, times `(age + 0.1)`, the age the animal's `vfunc 0x398`; list and confirm in [animals](animals.md#buying-animals) |
| Paying bail | `CharacterTrading_PrisonerBailout` | section 11.5 |

The override is the dialogue effect's val0 when it is 1 or more. **Slave windows** (DA 41 `CHOOSE_SLAVES_SELLING` / 42 `CHOOSE_SLAVES_BUYING`, `FUN_140680560`,
**Observed**): the slaver is the dialogue's speaker, the customer its target (the player character); nothing happens when they are the same. Both open the
`CharacterTradingWindow` (`FUN_14072a880`, one at a time: opening one closes the previous; layout in [ui-screens 6](ui-screens.md#6-inventory-trade-dialogue-build)) with a handler
holding the slaver, the customer, the character list and val0.
- **Selling** (41, title "Selling Slaves", confirm "Sell"): the members of the customer's squad that are not dead and within 300 units (3D) of the slaver, the customer
  included; no slave or prisoner test. A row's first panel is ("Name:", name) and ("WARNING: Character will become a slave!", ""), the pair added 5 times in a loop (a quirk;
  Meitou shows it once); the second is empty. Confirm (`FUN_1406a7740`), each ticked character: made the slaver's slave (slave state 1, "{1} has been enslaved" for a free
  player-faction character, the owner set, AI order 187 obedient slave toward the owner, SHACKLES made and worn when it wears no locked armour); its faction and squad are
  kept (it stays in the player's squad, uncaged); its squad remembers the customer with long-term tag 5 MY_CAPTOR; relations toward the player's faction: the slaver's +4,
  its own -4. Then the total is **added** to the player faction's money; the slaver's money is not reduced.
- **Buying** (42, title "Buying Slaves", confirm "Buy"): every character in the slaver's owned set that is a slave (state 1) and not dead; no radius, cage or count limit
  (the set is per owner object, **Inferred**). Panels: Strength, Dexterity, Toughness, Melee Attack, Melee Defence | Labouring, Farming, Cooking, Engineering, Medic (stat
  values). Confirm (`FUN_1406add20`), each ticked character not dead: its price added to the total; no longer owned; split from a squad of more than one into a new squad of
  its own faction (it does not join the player); event 51 BOUGHT_ME_FROM_SLAVERY at it with the customer as target; its cage unlocked; slave state 0; it leaves its cage or
  restraint; AI reset; its locked shackles unlocked (still worn); its squad remembers the customer with tag 3 FREED_ME; task 133 GET_OUT_OF_CAGE_LEGIT; relations toward the
  player's faction: the slaver's +1, its own +4. Then the total is taken from the player faction's money (refused without deduction when it has less; the result is ignored);
  nothing is paid to the slaver.
- The window's gate (`FUN_1407281c0`) makes such a shortfall unreachable: confirm is enabled only with a row ticked and the total within the buyer's money (the slaver's when
  selling, the player's when buying).

Meitou's reading: [simulation, slave trading](../simulation.md#arrest-and-jail).

## Unknowns

- **The stolen predicate** `FUN_14079e550(item, 1)`: it reads two owner handles (`+0x140`, `+0x160`), a flag byte, and a type
  check against the type list `{2, 3, 4, 0x2e, 0x56, 0x66, 0x6b, 0x6f}`. What exactly makes an item "stolen" (the thief, the
  owning faction vs. the holder's, expiry) is not decoded. The `0.5`
  stolen factor in `T` is therefore only roughly defined (the fence chance's own halvings are in [Fencing, stealing and smuggling](#fencing-stealing-and-smuggling)). *Partly resolved 2026-10-13* ([carry-loot.md 4.4](carry-loot.md#stolen-ownership)):
  `+0x140` is the owner the item was taken from, `+0x160` its current holder (a backpack's owner for a backpack); stolen = the from-owner is set,
  differs from the holder and is of another faction, unless the holder's faction is lawless (**Verified**, `FUN_14079e550`: the holder's faction (an item in a CONTAINER, type 0x2e, takes the container owner's) has `fundamental type` 8 or `not real` set; Meitou `ItemInstance.IsStolenFor` takes `PopulationData.IsLawless`, false for no valid faction, as the original skips the test with no holder faction); the type list is the lootable item kinds. Expiry and
  the halving condition remain open.
- **Uniform and crafter** (`Armour::vfunc_81` `FUN_1408a9c20`, `Weapon::vfunc_81` `FUN_1408a52f0`, **Verified**): `+0x1e8` is the faction a uniform piece belongs to (the item state's
  `uniform` key, a FACTION id resolved through the faction manager; an unresolved id leaves it null), `+0x208` the length of the `crafter` string at `+0x1f8` (the item writer `FUN_140761c00` writes
  `crafter` only when it is set). A uniform costs and sells at 0.3x (both directions, after the sale factor); a crafted piece takes `loot price mult player armour` / `player weapons` instead of GEAR
  when sold. In the siwa-test save 3250 of 14448 item states (the commonest are armour) carry a `uniform` that resolves to a faction record (e.g. `defaultEmpireFactionSID`, the Hive, the Tech Hunters). Meitou: `ItemInstance.Uniform`
  (read and written with the save key, `Market.UnitPrice`); a recruit join clearing it is **Observed** only (earlier reading, not re-checked).
  Where it comes from: CHARACTER `wears uniform` (88 true; read by `CharacterHuman::setupInventorySections` `FUN_14062c4e0`, which then takes the faction for the clothing setup,
  **Verified** read, use **Observed**). Meitou (`CharacterAssembly.Attach`): the worn armour of a new character with it is its faction's uniform.
- **Whether the town being set up is already in the global town list `DAT_142134100` when its own market factors are rolled.**
  If it were, its distance to itself is 0, the spread `w` is 0 and its own `p` is 1.0, which would force every factor to 1.0 and
  make the feature inert; so either the town is added after setup or something not yet seen intervenes. The order of the list
  decides which neighbours have factors already.
- **Whether `Zones_TownSetup` runs on a save load** and, if so, whether the saved `trade goods` factors (section 7.2, saved by `FUN_140376b80`,
  loaded by `FUN_1403715c0`) overwrite or are overwritten by a fresh roll. (That the factors are saved is settled.)
- **What `FUN_140745e30` really does** (it forwards a money delta to virtual slot 54 of the object at `+0x80`; the purse mutation itself) and how
  the trade window decides the `k` units priced with the opposite flag (`FUN_14070e3b0`, section 5).
- **Where `MapItem+0x1e8` (the map price) comes from.** (`BlueprintItem+0xc0` is the item's manufacturer slot, holding the RESEARCH; `+0x1e8` its `money`,
  [blueprints](buildings-production.md#blueprints).)
- **How weapon quality is rolled for shop stock** and how VENDOR_LIST reference value 1 (and a value 0 in the first slot, as in
  `(0,100,0)`) is read for clothing, armour and weapon entries (`FUN_140956ba0` / `FUN_1409555d0` read them; "val0 is relative
  chance" per the editor, but 472 entries have (0, 100, 0)). It follows the NPC clothing rule in [characters.md](../characters.md)
  that quantity 0 never spawns, but this was not re-verified for shops.
- **How a pending crime's timer counts down** (`+0x90`; set by `FUN_140851f40` / `FUN_140852e10`, the decrement was not found), and so how long an unseen crime stays pending.
- **Whether CHARACTER `money min` / `money max`** exist in the data and are unused (the editor lists `money`; not found
  being read for NPC purses).
- **What `FUN_140851d10` computes** (`prison time`'s consumer is the cage sentence, 11.5).
- **Arrest and jail gaps** (11.5, 11.7, 11.8): what a guard carrying a captive does when no cage is free (`CAGES_ARE_ALL_FULL` 97 is
  registered but no TaskData row uses it); how `+0x110` decays;
  whether NPC bounty hunters ever cage or hand in a captive; what the speaker-in-own-town branch of `DA_ARREST_TARGET` tests exactly.
- **Research cost** beyond the bench queue cost, crafting costs, building purchase price, bed cost.
- **The purpose of `FUN_1405d4260`** (the crime-in-progress tooltip function; its behaviour remains untraced).
- **Which item records make a `MoneyItem`** (priced at their `value` alone, section 2): `Cats`, and probably `String of Cats` and the other value-only items, but the constructor's
  choice of class was not traced; Meitou tests the name `Cats` (found 2026-10-14 while building the trade).
- **The reading of a v0 of 0 in VENDOR_LIST lists**: Meitou counts it as 100 and keeps each entry by its target's record type (engine choices,
  [../simulation.md "Trade"](../simulation.md#trade)). The list names and accepted types are **Verified** (`FUN_140956ba0`): `items` ITEM, `weapons` WEAPON (with
  `weapon manufacturers`), `armours` / `clothing` ARMOUR, `containers` CONTAINER, `blueprints` RESEARCH (a blueprint item), `crossbows` CROSSBOW, `robotics`
  LIMB_REPLACEMENT, `maps` MAP_ITEM; `armour blueprints` / `crossbow blueprints` / `backpack blueprints` name gear and give its `<id>.TECH.1` research's blueprint
  (skipped when missing), never the gear. Merged base data: `armour blueprints` 318, `blueprints` 141, `crossbow blueprints` 20, `backpack blueprints` 0 entries.
  A `weapons` entry is stocked only when it is a `weapon types` entry of one of the list's `weapon manufacturers` (**Verified**, `FUN_140956ba0`: the types of every listed
  manufacturer form a set, a weapon outside it logs "No valid manufacturer found for weapon" and is dropped, an empty set skips the whole weapons branch); other lists are not gated.
- **The FACTION field names of the armour vendor quality chances** (`armors 0..5` in section 9.2): read as the ints `armors 0` .. `armors 5`, falling back to the editor defaults; not
  checked against the data.
- **Opening a trade**: Meitou walks the customer up as a Talk (`WalkUp`, [dialogue](dialogue.md#player-conversation)), opens it in talk range and passes it to the character whose window a buy or sell uses within 1000 ([simulation](../simulation.md#trade)); the original's direct Trade order's distance is not traced.

## Expression reference

- Item base value: `ItemValue` (`FUN_140896e20`): `lerp(t, a, b) = a + (b - a) * t`; `ItemValue` (`FUN_140896e20`): `s = quality / 100`
- WEAPON: `v = value`; `v = trunc(v * price mod)`; `L(s) = 1 / (1 + e^(-(2s - 1) * 5 * 0.8))`; `raw = trunc( lerp(L(s), 15, 160) * v )`; `result = trunc( raw * sword price mult * global price mult )`
- ARMOUR and clothing: `t = s * s`; `lo = classLo[class] * matLo[material type]`; `hi = classHi[class] * matHi[material type]`; `v = trunc( lerp(t, lo, hi) )`; `v = trunc( v * armor price mult )`; `v = trunc( v * relative price mult )`
- CROSSBOW and LIMB_REPLACEMENT: `raw = trunc( lerp(s*s, value, value 1) )`
- Runtime unit price: Item virtual slots 81 and 82: `(sell != 0) + 3`; `u = ItemValue(record, item quality, 1)`; `u = trunc( u * marketFactor )`; `u = trunc( u * loot price mult )`; `u = trunc( u * charges / max charges )`; `n == 1`
- Trader factor `T` and the stolen test: `T = buyMult * (0.5 if stolen-to-the-player-faction) * culturePrice(item)`; `+0x250 == 0`
- Market factor: `f = uniform in [1 - m, 1 + m)`; `m = trade profit margins`; `w = d / 1,000,000 * m * 10`; `m = 0.3`; `p = 1.0`; `v0 = trunc(factor * 10000)`
- Buying, selling, fencing: `{1} = 100 - round(100 * p)`; `p = lerp(thievery / 100, 0.4, 1.25)`
- Stock size and money (`FUN_14095ab10`, squad shops; `FUN_14095b210`, building shops; **Observed**, decompile and: `fill = vendors fill total amount`; `wealth = clamp(FUN_1402dd530(faction), 0.2, 1.0)`; `n = round( uniform(0.8, 1.2) * fill * wealth )`; `money = trunc( clamp(0.25 * stockValue, 3000, 25000) * wealth )`; `money = trunc( (vendor money + r) * wealth )`
- Restock: `+0x58 == 0`
- Pending crimes, witnesses and the cage sentence: `+0x34 == 8`
- Slaves, prisoners, animals: `buyValue = clamp( trunc( S + attack + D * k ) * RACE buy value, 1000, 20000 )`

## Fencing, stealing and smuggling

Character virtual slots (CharacterAnimal/CharacterHuman vtables, **Verified** by slot lookup): `0x190` fence attempt `FUN_140793770`, `0x290` loot permission `FUN_140793940`
([carry-loot](carry-loot.md#theft-and-witnesses)), `0x298` steal attempt `FUN_140794810`, `0x2a8` smuggling check `FUN_140794e80`. Constants read from the exe: 1.0, 1.1, 0.4, 1.25, 0.5, 0.3, 2.25, 0.75, 1.5, 2.0, 0.98, 20, 80, 200.
Roll convention: a `rand()` draw in [0, 1) against the chance p; the thief gets away when p >= the draw, so p >= 1 never fails.

- **Owner tests** (**Observed**, decompile and the handle layout): the item's owner handle (`ownedby`, item vfunc 0x2a8 = item+0x140) is compared by its `C` (+0xc) and `CS` (+0x10) with an
  object's own handle (+0x64, +0x68). A character's `C`/`CS` are its platoon's container ([save.md](../formats/save.md#object-handles-and-slot-lists)), so each test asks whether the owner is in
  that platoon. `FUN_140792830(squad, item)` ("does not fence it"): false when the squad does not buy stolen goods (`+0xec`), else false only when the owner is in that squad (`C` and `CS`).
- **Fence chance** (`FUN_140793490(trader, item, thief)`, **Observed**). 1.0 (never caught) when the trader's faction is the player's (faction +0x250), the item has no owner, the owner is in
  the thief's platoon (`C` only), or the owner is not a character (type 1 or 0x22) that still resolves. When the owner's faction is not the trader's: 1.0 when the trader's squad does not fence
  it (`FUN_140792830`) or the owner's faction `fundamental type` is 8 (Bandit) or 9 (Adventurer); 1.1 when the trader's faction is not allied to the owner's (`FUN_1406b2630`) and the owner's
  home town (vfunc 0x98, `Ownerships::_homeTown` +0x30) is not the town the trader is in (vfunc 0x78, `getCurrentTownLocation`). Otherwise p = lerp(thievery/100, 0.4, 1.25), x0.5 when the
  owner's faction is the trader's, x0.5 again when the owner is in the trader's platoon (`C` only).
  Meitou (`TradeSystem.FenceOdds`): the owner is `ItemInstance.Owner` with the squad it was in when robbed (`OwnerSquad`, standing for `C`/`CS`), set where taking from a character is a
  theft (`CarrySystem`, `HeldItems`). A theft of a building's goods (an item of its container, a loose item of its slot) names the building's owner too: the original's
  `ownedby` is the bar's barman, the first member of the squad living there (**Observed**, plain game, The Hub's bar: the barrel's item had a null `ownedby` before the theft and the barman's
  handle after it, "Stolen from: Barman"; the bar's loose items name him before any theft); whether that is the squad's leader or another member is **Unknown**. Meitou
  (`CrimeSystem.OwnerMark`) names the leader of the building's resident squad of the owning faction; with none the mark names only the faction, which no owner early-out applies to
  and the home-town test reads as another town. The trader's town is `AiSystem.TownAt`, the owner's home town its squad's `Town`. Saved as the robbed character's handle in
  `ownedby` (the null handle when that character is not saved) and the robbed faction's id in Meitou's `meitou stolen from` (any mark; the only trace of a faction-only one or of
  a robbed character the save does not hold). Loading marks an item whose `ownedby` names a character of another faction than its holder's as stolen from that faction, and from
  the character once it is in the world (`SavedPopulation.NoteOwners`); else one whose `meitou stolen from` names another faction, from that faction.
- **Prompt and roll** (`FUN_140715290`, `FUN_140793770`, **Observed**): both skip an item whose owner is in the seller's platoon, that the trader's squad does not fence, or whose owner does
  not resolve to a character; so a shop that buys stolen goods asks and rolls only for goods stolen from its own squad. Meitou: `TradeSystem.Fences`; the prompt and the hover box
  ([ui-screens](ui-screens.md)) show the same p the roll uses.
- **Fence attempt** (`FUN_140793770`, **Observed**): caught when p < the draw: dialogue event 8 on the trader and a pending Fencing (0xd) against the trader's faction with timer 30 (`FUN_140852e10`).
  Thievery XP whenever p < 0.98 (and the trader is not imprisoned): the generic entry (dt 1) with 5 x 2 when the fence works, 20 x 2 when caught (`CrimeSystem.FenceXp`). Meitou: `TradeSystem.Caught`.
- **Steal chance** (`FUN_1407944d0(thief, victim, item)`, **Observed**): 1.0 for a dead or knocked-out thief (`+0x5b9`, `+0x5bc`) or one in posture 3. A victim that is a character: p = 1.1 x thievery/100. A victim that is a
  building's container: the watcher is the nearest conscious member (not dead, `+0x5bc` clear, posture below 3, on the thief's floor first) of the building owner's platoon (`FUN_140792ce0`; none: 1.0);
  t = lerp(min(1, distance / 200), 0.5, 1) with the distance from the watcher to the building, x0.5 and an early 1.0 under a test of the watcher's and the container's building and of the
  building's doors (`FUN_1405499a0`: no doors, or any door open) whose direction is **Unknown**; p = lerp(thievery/100 x t, 0.3, 2.25); a watcher that is dead, down, carried or imprisoned gives 1.0. A character victim: x0.75 for
  an item whose byte `+0x129` is set and x0.75 again when its slot `0x2e0` answers true; a watcher other than the victim: x2 when in posture 3 (the finder never returns one), x1.5 on another floor.
- **Steal attempt** (`FUN_140794810`, **Observed**): caught when p < the draw, which stops the move ("Got caught stealing.", code 8 of `FUN_14070f7e0`; the item stays). A caught thief wakes a victim lying down (posture 1 or 3), the
  watcher fires event 8 (event 0x2f instead, and no crime, on a coin flip when the watcher's status is 1), a pending Theft (3) against the watcher's faction with timer 30 is set (`FUN_140852e10`) and the squad tags the thief
  (kind 4). Thievery XP: 20 x 2 for a success and 80 x 2 for a catch, only when p < 0.98 and the thief's own pending timer (`+0x180`) is below 10 (so a catch gives none). Meitou: `CrimeSystem.Steal` (waking, event 8 or the in-bed 0x2f, the Theft, the squad memory, the XP).
- **Where a Theft is set** (`FUN_140852c80`, before the roll): taking from a container of another faction's building against the container owner's faction, code 3 on a right-click take
  (`FUN_140713d20`, `FUN_140712490`; a Looting 10 when the owner is down and the loot permission fails) and code 10 on a drag out of the window (`FUN_140715560`, which sets 3 only for a downed character); and picking an item up from the ground (`FUN_14034f340`, **Observed**) while standing inside a building when the item's owner (a character, `ownedby`) is of another, real faction: against the faction of the
  building the picker stands in, no roll, thievery XP 20 (generic entry with amount 20) once the item is in the bag. Meitou: the steal roll (`CrimeSystem.Steal`) and `StolePickUp`.
- **Smuggling check** (`FUN_140794e80(trader, out, item)`, **Observed**; Meitou builds the refusal and event 8, `TradeSystem.Smuggling`, and the held item's prompt in `PlayerInterface.AddFencePrompt`, not the smuggler's answer): for a trader of a non-player faction with a town, the item is illegal when the culture of the **town's faction**
  (`Faction+0x90`, not the town's own culture) lists it. A trader that does not buy illegal goods (`+0xed` clear) refuses an illegal item: result 0, shown as "Attempted smuggling!" (code 0xf), with event 8 unless the item is the trader's own
  stolen property; a legal item passes (1). A smuggler (`+0xed` set) answers 2 for a legal item and 1 for an illegal one (the answer 2 is read by no caller seen; "Smugglers only buy illegal goods." is code 0xe).
  "What is that a uniform? I don't want that!" (code 0x10) is posted by a function not found (**Unknown**).

## Buyback during an open trade

**Observed** (reported by the PR #41 contributor from the original game; not checked by the owner): an item bought and returned before the trade closes refunds its
purchase price; one sold and bought back costs what the sale paid; closing the trade ends this. The original's item rows have a `Buyback` row right after the name
(**Observed**, decompile, [item rows](ui-screens.md#tooltips)); when it shows is **Unknown**, as is how partially charged or mixed-price stacks are allocated.
Meitou: [simulation](../simulation.md#trade).

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- 4. Item base value: `ItemValue` (`FUN_140896e20`)
- 4.4 CONTAINER (type 0x2e = 46)
- 4.7 WEAPON_MANUFACTURER `price mod` (data, Verified by probe)
- 6. Trader factor `T` (`FUN_14079e6d0`) and the stolen test
- 7. Towns: culture and the market factor (`Zones_TownSetup` `FUN_1409353c0`, `read_forbidden_items_n5` `FUN_1407fd9a0`)
- 9.1 SQUAD_TEMPLATE fields (data = **Verified** by probe over 959 templates)
- 9.2 Stock size and money (`FUN_14095ab10`, squad shops; `FUN_14095b210`, building shops; **Observed**, decompile and
- 9.3 Restock (**Observed**)
- 11.4-11.8 Claiming, sentence, release, bail, arrest, spotting: code **Observed** (decompile); dialogue, AI_PACKAGE and squad facts **Verified** by a probe over the merged base-game data (DIALOG_ACTION `action name`, DIALOGUE_PACKAGE `dialogs` event value, AI_TASK `enum`)

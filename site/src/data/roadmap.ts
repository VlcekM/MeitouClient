// Parity roadmap data: Kenshi's systems by category and how far each is in Meitou, against unmodded Kenshi 1.0.68.
// Ported from https://github.com/Demitrix2k/meitou-parity-roadmap. Statuses: works = seen working in play, or a reported
// bug whose fix is on master; untested = in the code, never seen; wip = in the code with known gaps; bug = does not work;
// missing = not in Meitou. A system that works but has known bugs stays "works" and lists the bug ids.

export type Status = 'works' | 'untested' | 'wip' | 'bug' | 'missing';
export type Os = 'win' | 'linux' | 'mac';
/** Evidence for a status: the build it was last confirmed on, when, and by whom ("playtest", an issue "#123", a tester). */
export interface Seen { build: string; date: string; by: string; }
/** id: a stable slug for the system, used in links (#id) and for play reports; keep it when the name is reworded. */
export interface Row { id: string; system: string; status: Status; note: string; bugs: string[]; platforms?: Partial<Record<Os, Status | 'na'>>; seen?: Seen; }
export interface Category { id: string; name: string; blurb: string; rows: Row[]; }
export interface Bug { id: string; seen: string; original: string; state: string; platform?: string; }
export interface Fix { id: string; text: string; closed?: string; }

export const asOf = { date: "2026-11-06", master: "99161952" };

export const statuses: Record<Status, { label: string; desc: string }> = {
  "works": {
    "label": "Works",
    "desc": "seen working, or fixed on master"
  },
  "untested": {
    "label": "Untested",
    "desc": "in the code, not seen yet"
  },
  "wip": {
    "label": "WIP",
    "desc": "in the code with known gaps"
  },
  "bug": {
    "label": "Bug",
    "desc": "does not work"
  },
  "missing": {
    "label": "Missing",
    "desc": "not in Meitou"
  }
};
export const order: Status[] = ["works","untested","wip","bug","missing"];

export const categories: Category[] = [
  { id: "startup", name: "Startup and saves", blurb: "New games, the main menu, loading and saving.", rows: [
    { id: "load-an-original-kenshi-save", system: "Load an original Kenshi save", status: "works", note: "A large modded save with several bases loads and plays. On master: autosave every 10 minutes of unpaused play into three rotating slots (on by default), and the Delete key deletes a save in the load window (#94).", bugs: [] },
    { id: "reaching-the-main-menu", system: "Reaching the main menu", status: "works", note: "Loads for most players; the crash near the end of loading on one player's PC is fixed on master (#55).", bugs: [] },
    { id: "quicksave-and-quickload", system: "Quicksave and quickload", status: "works", note: "Saving and loading work; the crash once seen after a quickload and a jump to another base is fixed on master (#78).", bugs: [] },
    { id: "new-game-starts-and-advanced-options", system: "New-game starts and advanced options", status: "works", note: "Tested in play, works mostly: all starts, advanced options with the original help text. Since #75 a start also sets the faction relations, start town, discovered towns, squad, research and clock as the original (not seen in play yet). The Hive Exile start that defaulted to a human character is fixed on master (#62). Play reports: the Holy Sword start has no starting bounty.", bugs: [] },
    { id: "import-game", system: "Import game", status: "works", note: "Tested in play: importing works. The squads afterwards are wrong: the first squad has no name, members show missing or black portraits, and one character is listed in two squads. On master, a save older than 0.90.0 unticks and disables Reset, Buildings, Research and Dead NPCs, as the original.", bugs: [] },
  ] },
  { id: "characters", name: "Characters", blurb: "Health, needs, stats, equipment and the character editor.", rows: [
    { id: "character-editor", system: "Character editor", status: "works", note: "Template rows, stats panel, camera and stance preview as the original. Races from mods: part maps hide body parts under worn hide parts, and editor limits and files resolve in Workshop folders, fixed on master (#93). A start's animals are listed in the editor too, as the original (#312, on master). After buying animals the editor opens on them to name them. The height of characters in some part maps (high heels, #126) and the missing character textures (#135) are fixed; both issues are closed.", bugs: [] },
    { id: "hunger-eating-starvation", system: "Hunger, eating, starvation", status: "works", note: "Tested in play: hunger, eating and starvation work.", bugs: [] },
    { id: "sleep-beds-bed-rent", system: "Sleep, beds, bed rent", status: "works", note: "Tested in play: characters sleep in beds. Bed rent and sleeping when injured not seen yet. On master: a hostile faction's bed, or one in a building that is not public, is refused as not yours.", bugs: [] },
    { id: "first-aid-splints-bleeding-knock-outs", system: "First aid, splints, bleeding, knock-outs", status: "works", note: "KO and blood rules recently matched to the original; the medic finder and quick right click follow the decoded rules. On master (#92): losing a leg knocks a standing character out first, bandaged parts no longer show as getting worse, splinting takes the medic's own kit, and a player medic treats a downed mate during a fight. A downed character that sometimes vanished until it got up (seen with a pack beast) is addressed on master: ragdolls no longer sink into floors (#296). A fresh stump also sets blood-loss trauma and lets a downed character wake from -25 blood (on master). Open: no arrow shows whether a wound is still bleeding (play report).", bugs: [] },
    { id: "limbs-stumps-prosthetics", system: "Limbs, stumps, prosthetics", status: "untested", note: "Fitting and removing prosthetics works in code. On master: a race's no hats / shirts / shoes and lost or replaced legs block their equipment slots (#87), a severed limb falls with physics, and cut limbs and stumps show blood. Prosthetics take their item's colour, else the wearer's scheme, like clothing (#279), and an animal that runs off with a limb holds it in its mouth (#313); both on master, not seen in play yet.", bugs: [] },
    { id: "carrying-characters", system: "Carrying characters", status: "works", note: "Tested in play. Lifting, the carry pose (downed or awake), the carrier's arm overlay, the carried body's arm and head physics and fighting while carrying follow the original; hurt characters hold the hurt part (hand to head, shoulder, chest). On master, not seen in play yet: a carried animal hangs by its carry bone on the shoulder, not over the head (#307). Play report: characters put down inside a building end up on the floor below or on the ground (#327).", bugs: [] },
    { id: "weather-on-characters", system: "Weather on characters", status: "untested", note: "Acid rain, acid water, fire, gas and dust affect characters; a roof shelters them.", bugs: [] },
    { id: "medical-beds-skeleton-repair-robot-repair", system: "Medical beds, skeleton repair, robot repair", status: "untested", note: "Bed healing rates and repair beds for skeletons.", bugs: [] },
    { id: "xp-stats-training-dummies", system: "XP, stats, training dummies", status: "untested", note: "Training dummy XP cap as the original; a Training bar shows over characters at a dummy. The stats window shows the Stats Affected numbers of each skill. Play report (with a 2x XP mod): skills level up at random points of the XP bar, not when it fills.", bugs: [] },
    { id: "encumbrance", system: "Encumbrance", status: "works", note: "Tested in play: carried weight slows characters and changes stats. Play report: backpacks give no weight reduction (#326).", bugs: [] },
    { id: "standing-up-and-crawling-on-purpose", system: "Standing up and crawling on purpose", status: "untested", note: "Waking while sneaking stays low and crawls; Stand Up, a speed order, sneak off or a melee attack stands.", bugs: [] },
  ] },
  { id: "movement", name: "Movement and control", blurb: "Selection, pathfinding, right-click orders, stairs and roofs.", rows: [
    { id: "switch-between-bases-and-squads", system: "Switch between bases and squads", status: "works", note: "Selecting a character in another base moves the camera there (Tab, double-tap 1).", bugs: [] },
    { id: "pathfinding-and-getting-stuck", system: "Pathfinding and getting stuck", status: "works", note: "Works mostly, and works for everyone on the latest build so far. Long routes no longer re-plan over and over (#50); the maintainer says NPCs still get stuck at times (#27). The northern stairs on the hub that one player could not walk down are fixed on master (#59). Newer upstream fixes for walkers stranded off the navmesh (a Hub polygon 40 above the ground, a flat ledge joined to a stair ramp) are checked in scratch simulations only, not yet in play. On master: walkers brake for corners and long routes splice the stretch ahead, and a blocked player move order is kept and re-asked every 5 seconds, as the original (the report #308 was closed as not planned).", bugs: [] },
    { id: "right-click-verbs-and-target-picking", system: "Right-click verbs and target picking", status: "wip", note: "Original labels and order; Throw out of home, Throw out of town (downed intruders inside the player's town walls) and Corpse Disposal are on master; Kidnap shows only for unwanted non-bandit targets in another faction's town, otherwise Pick Up; an own cage offers Lock / Unlock and another faction's occupied cage Use Tools / Use Strength (on master, not seen in play yet); cutting locks other than shackles is not checked. Pick up on squad members is fixed on master (#74). Play reports: right-clicking a squad member's portrait on the HUD gives no verbs; a character who enters a cage and picks its lock cannot walk out. Furniture inside a shown town house can be clicked from afar, also on a floor above the viewed one (#320, on master).", bugs: [] },
    { id: "clicks-on-roofs-and-upper-decks", system: "Clicks on roofs and upper decks", status: "works", note: "A click on a roof walk takes the character there by the stairs.", bugs: [] },
  ] },
  { id: "combat", name: "Combat", blurb: "Melee, ranged weapons, stealth and turrets.", rows: [
    { id: "melee-techniques-attack-slots-blocking", system: "Melee: techniques, attack slots, blocking", status: "works", note: "Tested in play: melee combat works. Attack choice, the five-fighter engaged list and circling follow the decoded rules. Fists now reach with the FISTS weapon's length and take its multipliers (martial artists no longer swing short), on master. On master, not seen in play yet: fighters walk to a target on another floor instead of swinging through the floor (#292), a swing at a downed target plays to its end first, and attack-range tests reach 100 / 500 units, so animals attack again from beyond touching range. Open: allies sometimes stand by instead of joining a fight to defend their allies.", bugs: [] },
    { id: "crossbows-and-turrets", system: "Crossbows and turrets", status: "wip", note: "Quick right click on a turret fixed on master; turrets show Height rows and a Loaded bar, and loaded harpoons are saved. On master: turret gunners score a downed target 0 and drop an ordered target that is down, bedded or caged; no crossbow while staying low, crawling or swimming.", bugs: [] },
    { id: "stealth-and-sneak-knock-outs", system: "Stealth and sneak knock-outs", status: "wip", note: "Hearing follows the original's interior, floor and door rules. On master: an awake listener hears at half range and a sleeper may drop what it heard, and a fighter standing still is heard at 350. Hearing checks now give the heard character stealth XP (2 per enemy, 2 for its home, x20 when heard) with an exposure factor that falls x0.7, and the hearing XP gate uses the posture 1 or 2 test (on master, not seen in play yet). A stealth knock-out walks to the target's floor and starts only there (#292, on master).", bugs: [] },
  ] },
  { id: "economy", name: "Economy", blurb: "Mining, trade, looting, theft, locks and bounties.", rows: [
    { id: "mining-rocks", system: "Mining rocks", status: "works", note: "Mine pointer, Mine and Add to jobs menu, ore appears in the ore container. A rock is reached within its bounds box, as the original, on master. Play reports: mining makes no sound; a rock can be mined from a great distance (#332).", bugs: ["#332"] },
    { id: "trade-window-prices-buyback", system: "Trade window, prices, buyback", status: "works", note: "Tested in play: buying, buying back and selling work. On master, not seen in play yet: a faction uniform buys and sells at 0.3x, as the original; caravans without a stock trade from an empty one (#316); shops restock again after a load (#310); prices and item grades are read at the level the original reads, so Masterwork limbs no longer sell at Prototype prices (#281).", bugs: [] },
    { id: "looting-stealing-stolen-marks", system: "Looting, stealing, stolen marks", status: "untested", note: "Witnessed theft, pickpocketing sleepers and prisoners, stolen ground items, shop theft. On master: a stolen item handed back to its owner's faction loses the stolen mark, NPC looters commit Looting, and guards confiscate stolen items. Bodies playing dead or being carried can be looted (on master). Play report: the fence chance is not shown when selling stolen goods; the trade hover box shows it on master, not seen in play yet. Play report: the fence chance should show in the item tooltip before clicking it (#330).", bugs: [] },
    { id: "locks-and-other-factions-containers", system: "Locks and other factions' containers", status: "wip", note: "Alt rules for locked containers fixed on master; player-built cages and safes get their own lock, saved. Play report: many containers are not treated as containers and none in the world is locked (#328). Taking from another faction's container sets the crime and rolls the original's steal check (on master). A lock on a stored shackle is not saved.", bugs: [] },
    { id: "bounties-and-slave-trading", system: "Bounties and slave trading", status: "wip", note: "Bounty desks, bounty expiry and slaver lists work in code. The inventory data panel shows Slave of and Looks like slave as the original; which equipment slots count for the slave look is not verified. Disguises (uniform level, lookers seeing through them) and chaining a sold slave are on master. Parity batch #283 (bounties, investigations, slavery, caging) is on master, not seen in play yet; a debug-menu row clears bounties. Slavers capture only characters on their sensed list (#314). Play report: picking shackles plays no lockpick animation.", bugs: [] },
  ] },
  { id: "squads", name: "Squads and jobs", blurb: "Recruiting, squads, orders and jobs.", rows: [
    { id: "recruiting", system: "Recruiting", status: "untested", note: "Hires, freed prisoners, unique recruits, up to the faction cap.", bugs: [] },
    { id: "squads-formations-orders-hotkeys", system: "Squads, formations, orders, hotkeys", status: "works", note: "Tested in play: squads work. A dead character staying in the squad is fixed on master (#70); a squad's first member leads, so a shop guard goes before its animals (#294); squads after an import are wrong.", bugs: [] },
    { id: "permanent-jobs-job-lists-ai-options", system: "Permanent jobs, job lists, AI options", status: "works", note: "Medic, robotics, splint, foraging and attack-enemies jobs; tested in play, works mostly. On master: a worker takes only a workplace it can run, one operator slot each (#289); a committed goal ends with its hours (#295), both checked in simulation runs of the original's Rebirth save, not played; slaver jobs walk straight to their target, and processed slaves are not processed again (#301, #300), each compared with the original over one in-game hour at Rebirth.", bugs: [] },
    { id: "hiring-mercenaries-ai-contracts", system: "Hiring mercenaries (AI contracts)", status: "untested", note: "Contracts start and end through dialogue. Play report: the mercenary plate cannot be moved while the game is unpaused (#329).", bugs: [] },
  ] },
  { id: "buildings", name: "Buildings", blurb: "Entering, building, research, production, storage and power.", rows: [
    { id: "entering-buildings", system: "Entering buildings", status: "works", note: "Buildings from collision mods and Workshop mods get their floors and collision. Some buildings still could not be entered; cause open. Hive buildings in the west that one player could not enter are fixed on master (#59). Doorless interiors such as hive huts, which one player could not enter or leave, are fixed on master (#71). Island Lab could not be entered (#117, closed as completed).", bugs: [] },
    { id: "build-menu-placement-construction", system: "Build menu, placement, construction", status: "works", note: "Placement ghost, queue, builders fetching materials, dismantle refunds; tested in play. Deselecting a building picked in the build menu is not implemented yet. Play report: engineers sometimes still deliver materials to and build a building whose construction is paused. On master: the max slope check fails only buildings that have one.", bugs: [] },
    { id: "research", system: "Research", status: "works", note: "Tested in play: research works (benches and the tech tree).", bugs: [] },
    { id: "prospecting", system: "Prospecting", status: "works", note: "Prospecting a spot for resources before building. Open: the prospecting window shows no map (play report).", bugs: [] },
    { id: "building-damage-and-repair", system: "Building damage and repair", status: "wip", note: "As in the original, only melee blows damage buildings: walls, gates and doors. A wall hit to 0 is ruined only when a builder or delivery first touches it, not by a world sweep (on master); walkers no longer pass through ruin walls (on master). Repair and Engineer jobs restore them. On master: an own broken door can be picked, shows the spanner pointer and offers Repair as a quick click and menu verb, and order 95 raises its progress at x3 with no materials . Open: broken doors offer no repair and engineers do not repair them (play report).", bugs: [] },
    { id: "naming-an-outpost-its-map-marker-buying-town-buildings", system: "Naming an outpost, its map marker, buying town buildings", status: "wip", note: "Buying a town house works and its owner is saved; outposts can be renamed from the town panel, and a bought ruined house can be restored through construction jobs (#63, not seen in play yet); player outposts show on the world map (on master). Furniture inside a bought town house can be placed on master (#298) and joins that town instead of founding an outpost that draws raids (#318); building in a ruin or a building of a not-real or no faction follows the original's ownership test; not seen in play yet.", bugs: [] },
    { id: "blueprints", system: "Blueprints", status: "untested", note: "Shops stock blueprints (research, armour, crossbows); right-clicking one in your own inventory learns its research. Not seen in play yet.", bugs: [] },
    { id: "walls-gates-turrets-upgrades", system: "Walls, gates, turrets, upgrades", status: "works", note: "Tested in play: wall lines, gates, turrets and building upgrades work. New on master, not seen in play yet: raiders whose target is behind a locked gate walk to it and bash it open.", bugs: [] },
    { id: "crafting-and-production", system: "Crafting and production", status: "wip", note: "Queue, repeat and research gating in code; the item furnace burns items into raw iron, and powered machines have Power and Battery toggles (on master); town buildings are machines that NPCs operate and feed from other machines' outputs (#286, on master, not seen in play yet). Play report: crafting stations make no sound while working.", bugs: [] },
    { id: "farms-and-wells", system: "Farms and wells", status: "wip", note: "Catch-up for time spent away only in Faithful mode (production far away switch, on master); crops have no quality. On master: harvest and clear progress scale with building health and power satisfaction, indoor farms use the biome water usage and ground fertility, and the catch-up water draw uses the per-second rate. Farm rain, harvest rolls and drought death follow the original (#281).", bugs: [] },
    { id: "power-generators-and-batteries", system: "Power: generators and batteries", status: "wip", note: "Generators, fuel, batteries, town power sharing and wind generators turned by the region's wind are in code; generator and battery output and capacity scale with health on master; one town grid is served per production pass and the surplus is shared among all batteries (#281); battery mode is not saved.", bugs: [] },
    { id: "hauling-and-storage", system: "Hauling and storage", status: "wip", note: "Works on an imported save. Crafted items are not hauled, and a hauling trip in progress is not saved. On master: Auto-Haul on a storage is a one-shot haul trip, and Hauling to needs Shift.", bugs: [] },
    { id: "storage-windows-and-grids", system: "Storage windows and grids", status: "works", note: "Grid sized from the stored item, Max Capacity line, red when full.", bugs: [] },
  ] },
  { id: "world", name: "World", blurb: "Towns, crime, dialogue, animals, raids, roads and the map.", rows: [
    { id: "town-life-residents-shops-bars", system: "Town life: residents, shops, bars", status: "wip", note: "Wandering, shopping (public shops only), bar seats, sleeping at home, shooing strangers from homes, town stalkers. Overly aggressive shopkeepers: fixed on master (#120). On master: shopkeepers reach their counters (#293, closed, fixed in the next nightly per the maintainer), walking NPCs stop to talk and trade, a static town guard holds a guard post of its town (#268), and a normal bar package needs a bar (#271); the last two were checked in simulation runs of the original's Rebirth save against the original, not played.", bugs: [] },
    { id: "crime-trespassing-arrest-jail", system: "Crime, trespassing, arrest, jail", status: "wip", note: "Guards, fines, cages, bail, bounty hunters, crime alarms (expiring 8 game seconds after the last raise); guards confiscate stolen goods on master. Guards with Protect allies defend allied defenders and hear fights in their own place, on master. Police jail raiders in upper-storey cages, and several guards arresting one target give one message (on master). Prisoners are held at the cage's facing every update; a report of prisoners facing 90 degrees away from the door is not confirmed fixed. Play report: guards do not attack intruders or wildlife until the player's character is downed.", bugs: [] },
    { id: "holy-nation-inspections-and-smuggling", system: "Holy Nation inspections and smuggling", status: "untested", note: "Gate guard inspections find the faction's illegal goods (Holy Nation: hashish and forbidden books) and post who was caught smuggling. Selling illegal goods to a trader is not checked yet.", bugs: [] },
    { id: "squads-away-from-the-camera", system: "Squads away from the camera", status: "works", note: "Tested in play: squads away from the camera work. Unloaded squads travel, fight and heal off screen. NPC squads used to load and unload in a box of 340 round the player's characters; on master (#285) a town unloads with its coverage zones and a platoon stays loaded inside its active zone, as the original (not seen in play yet). Corpses are saved and removed by the original's zone rule; a report of bodies vanishing beside the player is not reproduced.", bugs: [] },
    { id: "world-map-overview-and-town-discovery", system: "World map, overview and town discovery", status: "works", note: "Tested in play: the world map works. Faction overview, map items and discovering towns not seen one by one yet. On master the overview map is uploaded as stored BC1, 32 MB of video memory instead of 256 MB (#280, the contributor's figure, not measured here), and the map has no scroll bars. #280 says it was tested in game on a build of the branch by the low-end-PC user who reported it: the map opens as before and the problem is gone.", bugs: [] },
    { id: "kidnapping-cannibals-slavers", system: "Kidnapping, cannibals, slavers", status: "untested", note: "Carried to cages, made slaves, eaten. On master: slaves shipped to slave camps off screen, liberators free prisoners, escapees work off shackles, fogmen worship at cages. Knocked-out strangers are removed from a home building by its residents, and slavers walk straight to their target (#301).", bugs: [] },
    { id: "roaming-squads-roads-patrols", system: "Roaming squads, roads, patrols", status: "works", note: "Tested in play: roaming squads work. Road preference and sector patrols follow the decoded rules.", bugs: [] },
    { id: "dialogue-and-speech-bubbles", system: "Dialogue and speech bubbles", status: "wip", note: "Conditions that need state Meitou does not track yet do not pass. Said dialogue lines and replies are logged in the Dialog tab on master (#76). On master: condition 57 (within town walls) and conditions 21 and 22 (weaker or stronger than me, by combat rating) follow the original, and race tests match the race group. The crime names, [Leave] and the money label go through the translation catalogue (#303).", bugs: [] },
    { id: "chasing-and-running-away", system: "Chasing and running away", status: "untested", note: "Chasers give up as the original does; players reported pursuing squads vanishing during long chases, often in the fog islands, fixed on master (#57). Play report: outlaws and starving bandits chase far longer than in the original.", bugs: [] },
    { id: "raids-and-campaigns", system: "Raids and campaigns", status: "wip", note: "Campaigns run and can target player outposts; the war classes are not built. An attack on an unloaded town wins after 4 hours without changing the town, as the original. On master (#285, #286): campaigns follow the original's queue order and hold test, raiders plan entry by the open-door state and assault squads breach a wall when no gate leads in (not seen in play yet).", bugs: [] },
    { id: "animals-and-nests", system: "Animals and nests", status: "untested", note: "Own meshes and clips, ageing, pack beasts, butchering; on master the player's animals fetch food from outpost stores and the ground, and animals eat corpses and raid farms; nests lay eggs and loot when first loaded (#285, whose screenshot shows a Fogman nest in game).", bugs: [] },
    { id: "occupied-towns", system: "Occupied towns", status: "untested", note: "A squad that holds a town patrols it, throws out downed intruders and fetches food; occupiers stay saved and resume after a load. Static guards hold a guard post of their town (#268, on master).", bugs: [] },
  ] },
  { id: "factions", name: "Factions and events", blurb: "Unique characters, faction relations and changes to towns.", rows: [
    { id: "unique-npcs-their-capture-and-death", system: "Unique NPCs, their capture and death", status: "untested", note: "Made once; death and capture saved.", bugs: [] },
    { id: "world-event-states", system: "World event states", status: "untested", note: "Answered live from unique NPCs and relations.", bugs: [] },
    { id: "faction-relations-and-ranks", system: "Faction relations and ranks", status: "untested", note: "Relation changes from blows, deaths, caging and first aid; ranks saved. The faction tab lists only factions the player has met (on master).", bugs: [] },
    { id: "towns-change-after-a-leader-falls", system: "Towns change after a leader falls", status: "untested", note: "A town takes its override when its zone loads, by the original's rule, its zone reset (buildings, item slots, loose items) and new residents; saved.", bugs: [] },
  ] },
  { id: "audio", name: "Audio", blurb: "Sound and music.", rows: [
    { id: "sound-and-music", system: "Sound and music", status: "works", note: "Heard on the Windows build, native Linux and macOS, with the Steam copy and with the GOG copy of Kenshi. Sound that did not play is fixed on master (#113); on master the output reopens on the default device when it is lost or switched, instead of staying silent (#309).", bugs: [] },
  ] },
  { id: "interface", name: "Interface", blurb: "Windows, tooltips, pause, text and input.", rows: [
    { id: "game-speed-and-pause", system: "Game speed and pause", status: "works", note: "Pause and the 1x, 2x and 5x speeds; tested in play. Orders and interface actions given while paused work on master (#73).", bugs: [] },
    { id: "inventory-machine-and-loot-windows", system: "Inventory, machine and loot windows", status: "works", note: "Placement and opening rules as the original; both bags open when looting; a right click on an item in the open backpack moves it to the main grid (#315, on master). An animal's character window shows an inventory grid the original leaves out. A dead character the player still owns not being selectable to see its stats is fixed on master (#79).", bugs: [] },
    { id: "interface-drawing", system: "Interface drawing", status: "works", note: "Windows, bars and text draw in play. On master, the interface is drawn into its own transparent texture and composited onto the picture before the present (not seen in play yet). Play report (Windows, Radeon): the interface flickers whenever the camera is panned away from the squad; the original does not (#108, closed).", bugs: [] },
    { id: "item-tooltips", system: "Item tooltips", status: "works", note: "Food, tools, weapons and armour rows, with armour stats and effect lines by the original's rules; readable with the dark palette.", bugs: [] },
    { id: "right-click-item-transfer-between-windows", system: "Right-click item transfer between windows", status: "works", note: "Fixed on master: one unit per click, Shift for the stack. On master: a right click on money moves the whole stack.", bugs: [] },
    { id: "mod-settings-and-mods-page", system: "Mod settings and the Mods page", status: "works", note: "Tried in play (#269): the Settings window has a Mods page with per-mod issues, with Copy and Copy all problems, and --mods-cfg reads the mod list from another file; seen on Linux native with the Steam copy (91-mod list, and a 40-line --mods-cfg list), on the clean GOG copy and a two-mod copy, in the Faithful and the full Meitou UI, and Copy under Proton. Not checked in #269: native Windows and macOS, other resolutions, translations of the Mods strings (English fallback). Code mods can register a settings class shown on a Mod settings page (#297); #297 says clicking through that page in a running game is not tested yet.", bugs: [] },
    { id: "translations", system: "Translations", status: "works", note: "The game's interface and data use Kenshi's own translations for every language it ships (French seen in play). Meitou's own texts (launcher, title and settings screens) are translated into Brazilian Portuguese (#80) and Simplified Chinese (zh_CN, #302); other languages show them in English. On master the messages the code builds, the crime names and the money label go through the catalogue (#303, #305). Some game strings were reported missed (#51).", bugs: [] },
  ] },
  { id: "platform", name: "Platform and performance", blurb: "Platforms, graphics, install detection, tests and memory.", rows: [
    { id: "runs-on-the-platform", system: "Runs on the platform", status: "works", note: "Windows build (also played under Proton on Linux); native Linux on X11 and Wayland; macOS on Apple silicon, seen by an outside tester on an M1 Pro. Open reports on recent nightly builds: Vulkan ErrorDeviceLost a few seconds after the world loads on a Radeon RX 6800 XT, not seen on the 2026-10-28 and 2026-10-29 builds (#319); the client stops responding on the 2026-11-05 and 2026-11-06 nightlies (#331).", bugs: ["#319","#331"], platforms: {"win":"works","linux":"works","mac":"works"} },
    { id: "macos-on-intel", system: "macOS on Intel", status: "untested", note: "An installer is published; no test report.", bugs: [], platforms: {"win":"na","linux":"na","mac":"untested"} },
    { id: "borderless-full-screen", system: "Borderless full screen", status: "bug", note: "Works on Windows and on Linux with KDE (the old panel overlap was fixed). On macOS borderless shows no window (windowed works). On Linux, Alt+Enter leaving full screen and X11 opening on the primary monitor are fixed on master (#96). Play report (Windows): while the game has focus, windows on a second monitor run at a low frame rate.", bugs: [], platforms: {"win":"works","linux":"works","mac":"bug"} },
    { id: "dlss-and-fsr-upscaling", system: "DLSS and FSR upscaling", status: "wip", note: "Both on Windows (DLSS is the default); render scale is a choice of quality modes, Ultra performance to DLAA, with the slider only under Custom (on master); DLSS on native Linux through NVIDIA's NGX library is on master (#99), not seen yet; FSR on macOS, from a self-built library and slower than TAA; FSR 3 upscaling and frame generation on Linux through our own build of AMD's FidelityFX library (on master, not seen yet). Frame generation (FSR 3, off by default) makes extra frames and works with DLSS on Windows (on master, not seen in play yet). On master: dust no longer floods the upscalers' reactive mask, rain and fire stay full size (not seen in play yet).", bugs: [], platforms: {"win":"works","linux":"untested","mac":"wip"} },
    { id: "ray-traced-indirect-light", system: "Ray-traced indirect light", status: "wip", note: "Optional; MoltenVK has no ray queries, so macOS draws without it and grass falls back to the CPU. Not looked at on native Linux.", bugs: [], platforms: {"win":"works","linux":"untested","mac":"missing"} },
    { id: "finding-the-kenshi-install", system: "Finding the Kenshi install", status: "wip", note: "Steam installs on Windows and Linux; the Windows registry lookup and installs by Lutris, Bottles or Heroic are untested; macOS lookup untested.", bugs: [], platforms: {"win":"works","linux":"works","mac":"untested"} },
    { id: "scripted-test-runs-repeat-exactly", system: "Scripted test runs repeat exactly", status: "wip", note: "Pinned results pass on Windows and Linux; 10 of 15 differ on Apple silicon (floating point).", bugs: [], platforms: {"win":"works","linux":"works","mac":"wip"} },
    { id: "native-metal-renderer-for-macos", system: "Native Metal renderer for macOS", status: "wip", note: "Merged (#69) and offered by the launcher on Apple silicon; tessellated ground relief is not on Metal (flat and parallax terrain only).", bugs: [], platforms: {"win":"na","linux":"na","mac":"wip"} },
  ] },
];

export const bugs: Bug[] = [
  { id: "#332", seen: "Mining a rock from great distance.", original: "Not checked.", state: "Open issue" },
  { id: "#331", seen: "Client not responding on all current nightly builds (Windows).", original: "Not applicable.", state: "Open issue", platform: "Windows" },
  { id: "#319", seen: "Vulkan ErrorDeviceLost a few seconds after the world loads, also with no mods (RX 6800 XT, since 20261102).", original: "Not applicable.", state: "Open issue", platform: "Windows" },
];

// Fixed and seen working in play.
export const fixedSeen: Fix[] = [
  { id: "#61", text: "Armour tooltip and inventory weights match the original: stats by level, effect lines, weight and prices", closed: "2026-10-24" },
  { id: "#52", text: "Buildings from collision mods and Workshop mods have floors and collision", closed: "2026-10-24" },
  { id: "#53", text: "Clicks on open roof decks and roof walks go onto the roof" },
  { id: "#49", text: "Inventory, machine, storage and loot windows open and sit as in the original", closed: "2026-10-24" },
  { id: "#56", text: "Tooltips stay readable with the dark interface palette", closed: "2026-10-24" },
  { id: "#25", text: "Storage grids sized from the stored item, with a Max Capacity line", closed: "2026-10-21" },
  { id: "#24", text: "Pointer and click fixes for buildings, containers and mining", closed: "2026-10-21" },
  { id: "#14", text: "Armour tooltip with grade, class, resistances and coverage", closed: "2026-10-20" },
  { id: "#13", text: "Borderless full screen covers the KDE panel", closed: "2026-10-20" },
  { id: "#11", text: "Native Linux build: Wayland start-up, file names in any case", closed: "2026-10-20" },
  { id: "", text: "Food and tool tooltips show nutrition, ingredients and charges" },
  { id: "#9", text: "Fails to load foliage", closed: "2026-10-19" },
  { id: "#4", text: "Not able to load pass 'loading  - the view'", closed: "2026-10-19" },
  { id: "#162", text: "Cant walk out of slave cage in Rebirth", closed: "2026-11-03" },
  { id: "#144", text: "Navmesh series - Worlds End", closed: "2026-11-05" },
  { id: "#141", text: "Navmesh series - Heft", closed: "2026-11-01" },
];

// Fixed on master, not yet seen in play.
export const fixedUnseen: Fix[] = [
  { id: "#145", text: "Closed as completed: the Old Village (Deadcat) is in a ruined state", closed: "2026-11-03" },
  { id: "#135", text: "Characters have textures in the customisation screen (character textures load under VRAM pressure through a priority cache, #7)", closed: "2026-11-01" },
  { id: "#132", text: "Fogmen camps place their prisoner poles (nests place their town debris buildings and items, #8)", closed: "2026-11-03" },
  { id: "#130", text: "Stuck on a staircase: closed as completed with the navmesh work (#1, #10)", closed: "2026-11-01" },
  { id: "#129", text: "The UC faction HQ building can be entered (navmesh work, #1)", closed: "2026-11-01" },
  { id: "#127", text: "Smart App Control no longer blocks DotRecast.Recast.dll on Windows 11 (issue closed as completed)", closed: "2026-11-01" },
  { id: "#126", text: "Heeled boots and prosthetic legs lift the character as in the game (#186)", closed: "2026-11-01" },
  { id: "#118", text: "Textures on dropped items and bridges (material lookup for Windows-style mesh paths, #165)", closed: "2026-11-01" },
  { id: "#117", text: "Island Lab can be entered (navmesh work, #1)", closed: "2026-11-01" },
  { id: "#111", text: "Hub walls clipping into the Shinobi Thieves Tower: closed; the original has it too and an optional fix is planned in the settings", closed: "2026-11-01" },
  { id: "#108", text: "UI flickering: closed as completed on 2026-11-04 (last reporter said the test switches changed nothing)", closed: "2026-11-04" },
  { id: "#320", text: "Furniture inside a shown town house can be picked from afar, also on a floor above the viewed one", closed: "2026-11-06" },
  { id: "#318", text: "Furniture in a bought town house joins that town instead of founding a raided outpost", closed: "2026-11-06" },
  { id: "#316", text: "Goods trader caravans can be traded with", closed: "2026-11-06" },
  { id: "#315", text: "A right click on an item in the open backpack moves it to the main grid", closed: "2026-11-05" },
  { id: "#314", text: "Slave market traders open trade; slavers capture only characters on their sensed list", closed: "2026-11-05" },
  { id: "#313", text: "An animal that runs off with a limb holds it in its mouth", closed: "2026-11-05" },
  { id: "#312", text: "A start's animals are listed in the character editor", closed: "2026-11-05" },
  { id: "#310", text: "Shops restock after a load", closed: "2026-11-05" },
  { id: "#309", text: "Sound reopens on the default device when the output is lost", closed: "2026-11-06" },
  { id: "#307", text: "A carried animal hangs on the carrier's shoulder, not over the head", closed: "2026-11-05" },
  { id: "#298", text: "Building inside bought town properties and ruins", closed: "2026-11-05" },
  { id: "#296", text: "Ragdolls no longer sink into building floors", closed: "2026-11-04" },
  { id: "#294", text: "A shop guard leads its squad, so shop staff stand in the right place", closed: "2026-11-05" },
  { id: "#293", text: "Shopkeepers at slave markets reach their shops", closed: "2026-11-05" },
  { id: "#292", text: "Fighters, looters and knock-out attackers walk to a target's own floor instead of reaching through the floor", closed: "2026-11-05" },
  { id: "#279", text: "Prosthetics take their item's colour or the wearer's scheme", closed: "2026-11-05" },
  { id: "#120", text: "Shopkeepers are no longer overly aggressive", closed: "2026-10-30" },
  { id: "", text: "Dust no longer floods the upscalers' reactive mask; rain and fire stay full size" },
  { id: "#113", text: "Sound plays; the no-sound report is closed as completed", closed: "2026-10-28" },
  { id: "#94", text: "The Delete key deletes the selected save in the load window", closed: "2026-10-27" },
  { id: "#93", text: "Part maps and editor limits work for races from mods", closed: "2026-10-27" },
  { id: "#87", text: "Races' restricted equipment slots take nothing and draw blocked", closed: "2026-10-27" },
  { id: "#96", text: "Linux: Alt+Enter leaves full screen; X11 opens on the primary monitor", closed: "2026-10-26" },
  { id: "#55", text: "Crash just before finishing Main Menu loading on one player's PC", closed: "2026-10-25" },
  { id: "#79", text: "A dead character the player still owns can be selected to see its stats", closed: "2026-10-25" },
  { id: "#74", text: "Pick up is offered on squad members", closed: "2026-10-25" },
  { id: "#70", text: "A character who dies is removed from its squad", closed: "2026-10-25" },
  { id: "#57", text: "A squad chasing the player no longer vanishes during a long chase", closed: "2026-10-25" },
  { id: "#71", text: "Doorless interiors such as hive huts can be entered and left", closed: "2026-10-25" },
  { id: "#76", text: "Said dialogue lines and replies are logged in the Dialog tab", closed: "2026-10-25" },
  { id: "#78", text: "No more long Proton stutters from path searches; no crash placing character parts after a quickload", closed: "2026-10-25" },
  { id: "#77", text: "Full screen on Wayland shows the window", closed: "2026-10-25" },
  { id: "#73", text: "Orders and interface actions work while paused", closed: "2026-10-25" },
  { id: "#62", text: "The Hive Exile start defaults to a hive race, not a human", closed: "2026-10-24" },
  { id: "#59", text: "Northern hub stairs can be walked down; western hive buildings can be entered", closed: "2026-10-25" },
  { id: "#50", text: "Long routes no longer switch and re-plan over and over", closed: "2026-10-24" },
  { id: "#33", text: "Squads walk in formation behind their leader", closed: "2026-10-23" },
  { id: "#34", text: "Trade prices per unit, not per stack", closed: "2026-10-22" },
  { id: "#35", text: "The slaver shop opens", closed: "2026-10-22" },
  { id: "#36", text: "NPCs hold crossbows, not staffs, as ranged weapons", closed: "2026-10-22" },
  { id: "#32", text: "Animal inventories no longer fill like a person's", closed: "2026-10-22" },
  { id: "#19", text: "Characters inside player-built buildings from a Kenshi save can path out", closed: "2026-10-21" },
  { id: "#10", text: "Characters can enter swamp houses", closed: "2026-10-20" },
  { id: "#22", text: "No endless loading screen on Windows 11", closed: "2026-10-21" },
  { id: "#15", text: "Missing character creation options added", closed: "2026-10-20" },
  { id: "#18", text: "Text-only buttons show their hover colour", closed: "2026-10-21" },
  { id: "", text: "Right click moves one unit between windows, Shift the stack" },
  { id: "", text: "The right-click target is the nearest hit along the ray" },
  { id: "", text: "Other factions' locked containers: lockpick and steal hints, theft is a crime" },
  { id: "", text: "\"Out of trading range\" when dragging between distant windows" },
  { id: "", text: "Quick right click on a furnace opens it; on a turret, the nearest selected character mans it" },
  { id: "", text: "Armour tooltip shows the Stealth effect and Combat speed effect lines" },
  { id: "#260", text: "Blister Hill Nav Mesh Issues", closed: "2026-11-06" },
  { id: "#273", text: "Can't move throught this route", closed: "2026-11-06" },
  { id: "#317", text: "Navmesh series - Heft police bulding inaccessible due to rift in navmesh", closed: "2026-11-06" },
  { id: "#245", text: "Navmesh series - Slaver shop in Traders Edge", closed: "2026-11-06" },
  { id: "#184", text: "Pathfinding Issues on rooftops", closed: "2026-11-05" },
  { id: "#290", text: "All robotic limbs priced as lowest quality", closed: "2026-11-05" },
  { id: "#251", text: "can't get in building in mongrel, use mod Universal wateland expansion,...", closed: "2026-11-04" },
  { id: "#276", text: "Loading bar top right never finishes", closed: "2026-11-04" },
  { id: "#242", text: "heavy modlist but in character creation ..the characters keeping sitting..or...", closed: "2026-11-04" },
  { id: "#231", text: "Buying a town house leaves its furniture and its occupants behind, and...", closed: "2026-11-04" },
  { id: "#185", text: "Hub residents standing in shack", closed: "2026-11-04" },
  { id: "#221", text: "Slot Equipment BUG", closed: "2026-11-04" },
  { id: "#204", text: "Screen is shaking down and up if you pick up items or drop them in a...", closed: "2026-11-04" },
  { id: "#232", text: "Flickering tbagging", closed: "2026-11-04" },
  { id: "#224", text: "Character prosthetic issues", closed: "2026-11-04" },
  { id: "#176", text: "Picking up dog, but the body is still on the ground", closed: "2026-11-04" },
  { id: "#246", text: "Mining sounds", closed: "2026-11-04" },
  { id: "#193", text: "Behaviour of ranged combat AI en masse", closed: "2026-11-04" },
  { id: "#181", text: "Ancient Tech Lab building is completely empty", closed: "2026-11-04" },
  { id: "#223", text: "No furniture in Cat-Lon and thrall domes", closed: "2026-11-04" },
  { id: "#258", text: "Cannibal cages went underground", closed: "2026-11-04" },
  { id: "#163", text: "Rebirth guards dont bandage slaves", closed: "2026-11-03" },
  { id: "#261", text: "The Hub, trading with Barshopkeeper - crashes - unhandled...", closed: "2026-11-03" },
  { id: "#160", text: "Loot in animal nests", closed: "2026-11-03" },
  { id: "#238", text: "Right-click on portrait of another char gives no interaction menu.", closed: "2026-11-03" },
  { id: "#171", text: "Add MA dmg to limbs like vanilla", closed: "2026-11-03" },
  { id: "#233", text: "Loot from animals marked as stolen from their faction", closed: "2026-11-03" },
  { id: "#252", text: "Can't Move", closed: "2026-11-03" },
  { id: "#248", text: "Can't issue movement orders", closed: "2026-11-03" },
  { id: "#178", text: "Guy with a Dog - Start - Dog is getting hungry", closed: "2026-11-02" },
  { id: "#201", text: "Game-data issues are collected but never logged or shown - missing mod,...", closed: "2026-11-02" },
  { id: "#202", text: "Four _HI texture lookups warn on every start on a clean install - files do...", closed: "2026-11-02" },
  { id: "#159", text: "Toughness gaining", closed: "2026-11-01" },
  { id: "#200", text: "New game throws 'the game data has no player faction - Nameless' when a data...", closed: "2026-11-01" },
  { id: "#199", text: "A localised RACE record leaves 329 characters without a race - drawn as 6-px...", closed: "2026-11-01" },
  { id: "#212", text: "Switching character inventory while in a trade window. Item ar load into the...", closed: "2026-11-01" },
  { id: "#206", text: "Training dummy dont work", closed: "2026-11-01" },
  { id: "#213", text: "Right mouse-click transfer mission, when transfering item between characters...", closed: "2026-11-01" },
  { id: "#161", text: "Rebirth statue foundation and some site props are transparent for walking", closed: "2026-11-01" },
  { id: "#210", text: "Mod part maps are ignored - mods that replace data/ files by the same path...", closed: "2026-11-01" },
  { id: "#119", text: "I was stopped and held at sword point but they still hit me even after i paid", closed: "2026-11-01" },
  { id: "#168", text: "Long geometry spikes from wounded or downed characters", closed: "2026-11-01" },
  { id: "#124", text: "Lagging and command delay", closed: "2026-11-01" },
  { id: "#170", text: "When you are inside of a house and close the door in Floor0 Position, the...", closed: "2026-11-01" },
  { id: "#157", text: "Navmesh series - Workshop", closed: "2026-10-31" },
  { id: "#156", text: "Navmesh series - Blister Hill", closed: "2026-10-31" },
  { id: "#155", text: "Navmesh series - Shark", closed: "2026-10-31" },
  { id: "#154", text: "Navmesh series - Mourn", closed: "2026-10-31" },
  { id: "#153", text: "Navmesh series - Old Control Tower", closed: "2026-10-31" },
  { id: "#149", text: "Navmesh series - Expedition 5", closed: "2026-10-31" },
  { id: "#147", text: "Navmesh series - Black Scratch", closed: "2026-10-31" },
  { id: "#146", text: "Navmesh series - Scraphouse", closed: "2026-10-31" },
  { id: "#143", text: "Navmesh series - desert", closed: "2026-10-31" },
  { id: "#142", text: "Navmesh series - Sho-Battai", closed: "2026-10-31" },
  { id: "#140", text: "Navmesh series - Heft", closed: "2026-10-31" },
  { id: "#138", text: "Navmesh series - The Hub", closed: "2026-10-31" },
  { id: "#139", text: "Navmesh series - Squin", closed: "2026-10-31" },
  { id: "#152", text: "Blood on surfaces is not saved - make it persist like the terrain's blood", closed: "2026-10-30" },
  { id: "#137", text: "Add Camera Follow via Right-Click on Character Portrait", closed: "2026-10-30" },
  { id: "#136", text: "Player Moves to NPC's Old Location Instead of Their Current Position When...", closed: "2026-10-30" },
  { id: "#125", text: "Shops", closed: "2026-10-30" },
  { id: "#42", text: "Indirect light. still relevant in commit  #119addf", closed: "2026-10-23" },
  { id: "#30", text: "Organize Race Strengths and Weaknesses by Category", closed: "2026-10-22" },
  { id: "#16", text: "macOS launcher crashes on start - libAvaloniaNative.dylib not in the app bundle", closed: "2026-10-21" },
];

/** A row's status on one platform: its own entry, else the row status (the gameplay code is the same everywhere). */
export const platformStatus = (r: Row, os: Os): Status | 'na' => r.platforms?.[os] ?? r.status;
export const allRows = categories.flatMap((c) => c.rows);
export const countBy = (rows: Row[]) => Object.fromEntries(order.map((k) => [k, rows.filter((r) => r.status === k).length])) as Record<Status, number>;
/** Link for an id: #N is an issue or pull request on GitHub (GitHub redirects between the two). */
export const refUrl = (id: string) => (id.startsWith('#') ? `https://github.com/VlcekM/MeitouClient/issues/${id.slice(1)}` : undefined);

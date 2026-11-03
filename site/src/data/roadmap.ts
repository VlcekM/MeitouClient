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
export interface Fix { id: string; text: string; }

export const asOf = { date: "2026-10-31", master: "72bb0756" };

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
    { id: "character-editor", system: "Character editor", status: "untested", note: "Template rows, stats panel, camera and stance preview as the original. Races from mods: part maps hide body parts under worn hide parts, and editor limits and files resolve in Workshop folders, fixed on master (#93).", bugs: ["#126","#135"] },
    { id: "hunger-eating-starvation", system: "Hunger, eating, starvation", status: "works", note: "Tested in play: hunger, eating and starvation work.", bugs: [] },
    { id: "sleep-beds-bed-rent", system: "Sleep, beds, bed rent", status: "works", note: "Tested in play: characters sleep in beds. Bed rent and sleeping when injured not seen yet. On master: a hostile faction's bed, or one in a building that is not public, is refused as not yours.", bugs: [] },
    { id: "first-aid-splints-bleeding-knock-outs", system: "First aid, splints, bleeding, knock-outs", status: "untested", note: "KO and blood rules recently matched to the original; the medic finder and quick right click follow the decoded rules. On master, not seen in play yet (#92): losing a leg knocks a standing character out first, bandaged parts no longer show as getting worse, splinting takes the medic's own kit, and a player medic treats a downed mate during a fight. Open: a downed character, seen with a pack beast, sometimes vanishes until it gets up (not reproduced on flat ground, suspected ragdoll sinking under terrain). Play report: no arrow shows whether a wound is still bleeding.", bugs: [] },
    { id: "limbs-stumps-prosthetics", system: "Limbs, stumps, prosthetics", status: "untested", note: "Fitting and removing prosthetics works in code. On master: a race's no hats / shirts / shoes and lost or replaced legs block their equipment slots (#87), a severed limb falls with physics, and cut limbs and stumps show blood.", bugs: [] },
    { id: "carrying-characters", system: "Carrying characters", status: "works", note: "Tested in play. Lifting, the carry pose (downed or awake), the carrier's arm overlay, the carried body's arm and head physics and fighting while carrying follow the original; hurt characters hold the hurt part (hand to head, shoulder, chest).", bugs: [] },
    { id: "weather-on-characters", system: "Weather on characters", status: "untested", note: "Acid rain, acid water, fire, gas and dust affect characters; a roof shelters them.", bugs: [] },
    { id: "medical-beds-skeleton-repair-robot-repair", system: "Medical beds, skeleton repair, robot repair", status: "untested", note: "Bed healing rates and repair beds for skeletons.", bugs: [] },
    { id: "xp-stats-training-dummies", system: "XP, stats, training dummies", status: "untested", note: "Training dummy XP cap as the original; a Training bar shows over characters at a dummy. The stats window shows the Stats Affected numbers of each skill. Play report (with a 2x XP mod): skills level up at random points of the XP bar, not when it fills.", bugs: [] },
    { id: "encumbrance", system: "Encumbrance", status: "works", note: "Tested in play: carried weight slows characters and changes stats.", bugs: [] },
    { id: "standing-up-and-crawling-on-purpose", system: "Standing up and crawling on purpose", status: "untested", note: "Waking while sneaking stays low and crawls; Stand Up, a speed order, sneak off or a melee attack stands.", bugs: [] },
  ] },
  { id: "movement", name: "Movement and control", blurb: "Selection, pathfinding, right-click orders, stairs and roofs.", rows: [
    { id: "switch-between-bases-and-squads", system: "Switch between bases and squads", status: "works", note: "Selecting a character in another base moves the camera there (Tab, double-tap 1).", bugs: [] },
    { id: "pathfinding-and-getting-stuck", system: "Pathfinding and getting stuck", status: "works", note: "Works mostly, and works for everyone on the latest build so far. Long routes no longer re-plan over and over (#50); the maintainer says NPCs still get stuck at times (#27). The northern stairs on the hub that one player could not walk down are fixed on master (#59). Newer upstream fixes for walkers stranded off the navmesh are checked in scratch simulations only, not yet in play.", bugs: ["#130"] },
    { id: "right-click-verbs-and-target-picking", system: "Right-click verbs and target picking", status: "wip", note: "Original labels and order; Throw out of home, Throw out of town (downed intruders inside the player's town walls) and Corpse Disposal are on master; Kidnap shows only for unwanted non-bandit targets in another faction's town, otherwise Pick Up; locking a cage and cutting locks other than shackles are not built. Pick up on squad members is fixed on master (#74). Play reports: right-clicking a squad member's portrait on the HUD gives no verbs; a character who enters a cage and picks its lock cannot walk out.", bugs: [] },
    { id: "clicks-on-roofs-and-upper-decks", system: "Clicks on roofs and upper decks", status: "works", note: "A click on a roof walk takes the character there by the stairs.", bugs: [] },
  ] },
  { id: "combat", name: "Combat", blurb: "Melee, ranged weapons, stealth and turrets.", rows: [
    { id: "melee-techniques-attack-slots-blocking", system: "Melee: techniques, attack slots, blocking", status: "works", note: "Tested in play: melee combat works. Attack choice, the five-fighter engaged list and circling follow the decoded rules. Fists now reach with the FISTS weapon's length and take its multipliers (martial artists no longer swing short), on master. Open: allies sometimes stand by instead of joining a fight to defend their allies.", bugs: [] },
    { id: "crossbows-and-turrets", system: "Crossbows and turrets", status: "wip", note: "Quick right click on a turret fixed on master; turrets show Height rows and a Loaded bar, and loaded harpoons are saved. On master: turret gunners score a downed target 0 and drop an ordered target that is down, bedded or caged; no crossbow while staying low, crawling or swimming.", bugs: [] },
    { id: "stealth-and-sneak-knock-outs", system: "Stealth and sneak knock-outs", status: "wip", note: "Hearing follows the original's interior, floor and door rules. On master: an awake listener hears at half range and a sleeper may drop what it heard, and a fighter standing still is heard at 350. Hearing checks now give the heard character stealth XP (2 per enemy, 2 for its home, x20 when heard) with an exposure factor that falls x0.7, and the hearing XP gate uses the posture 1 or 2 test (on master, not seen in play yet).", bugs: [] },
  ] },
  { id: "economy", name: "Economy", blurb: "Mining, trade, looting, theft, locks and bounties.", rows: [
    { id: "mining-rocks", system: "Mining rocks", status: "works", note: "Mine pointer, Mine and Add to jobs menu, ore appears in the ore container. A rock is reached within its bounds box, as the original, on master. Play report: mining makes no sound.", bugs: [] },
    { id: "trade-window-prices-buyback", system: "Trade window, prices, buyback", status: "works", note: "Tested in play: buying, buying back and selling work. On master, not seen in play yet: a faction uniform buys and sells at 0.3x, as the original.", bugs: [] },
    { id: "looting-stealing-stolen-marks", system: "Looting, stealing, stolen marks", status: "untested", note: "Witnessed theft, pickpocketing sleepers and prisoners, stolen ground items, shop theft. On master: a stolen item handed back to its owner's faction loses the stolen mark, NPC looters commit Looting, and guards confiscate stolen items. Bodies playing dead or being carried can be looted (on master). Play report: the fence chance is not shown when selling stolen goods; the trade hover box shows it on master, not seen in play yet.", bugs: ["#145"] },
    { id: "locks-and-other-factions-containers", system: "Locks and other factions' containers", status: "wip", note: "Alt rules for locked containers fixed on master; player-built cages and safes get their own lock, saved. Taking from another faction's container sets the crime and rolls the original's steal check (on master). A lock on a stored shackle is not saved.", bugs: [] },
    { id: "bounties-and-slave-trading", system: "Bounties and slave trading", status: "wip", note: "Bounty desks, bounty expiry and slaver lists work in code. The inventory data panel shows Slave of and Looks like slave as the original; which equipment slots count for the slave look is not verified. Disguises (uniform level, lookers seeing through them) and chaining a sold slave are on master. Play report: picking shackles plays no lockpick animation.", bugs: [] },
  ] },
  { id: "squads", name: "Squads and jobs", blurb: "Recruiting, squads, orders and jobs.", rows: [
    { id: "recruiting", system: "Recruiting", status: "untested", note: "Hires, freed prisoners, unique recruits, up to the faction cap.", bugs: [] },
    { id: "squads-formations-orders-hotkeys", system: "Squads, formations, orders, hotkeys", status: "works", note: "Tested in play: squads work. A dead character staying in the squad is fixed on master (#70); squads after an import are wrong.", bugs: [] },
    { id: "permanent-jobs-job-lists-ai-options", system: "Permanent jobs, job lists, AI options", status: "works", note: "Medic, robotics, splint, foraging and attack-enemies jobs; tested in play, works mostly.", bugs: [] },
    { id: "hiring-mercenaries-ai-contracts", system: "Hiring mercenaries (AI contracts)", status: "untested", note: "Contracts start and end through dialogue.", bugs: [] },
  ] },
  { id: "buildings", name: "Buildings", blurb: "Entering, building, research, production, storage and power.", rows: [
    { id: "entering-buildings", system: "Entering buildings", status: "works", note: "Buildings from collision mods and Workshop mods get their floors and collision. Some buildings still could not be entered; cause open. Hive buildings in the west that one player could not enter are fixed on master (#59). Doorless interiors such as hive huts, which one player could not enter or leave, are fixed on master (#71). Island Lab could not be entered (#117, open).", bugs: ["#117","#129"] },
    { id: "build-menu-placement-construction", system: "Build menu, placement, construction", status: "works", note: "Placement ghost, queue, builders fetching materials, dismantle refunds; tested in play. Deselecting a building picked in the build menu is not implemented yet. Play report: engineers sometimes still deliver materials to and build a building whose construction is paused. On master: the max slope check fails only buildings that have one.", bugs: [] },
    { id: "research", system: "Research", status: "works", note: "Tested in play: research works (benches and the tech tree).", bugs: [] },
    { id: "prospecting", system: "Prospecting", status: "untested", note: "Prospecting a spot for resources before building. Play report: the prospecting window shows no map.", bugs: [] },
    { id: "building-damage-and-repair", system: "Building damage and repair", status: "untested", note: "As in the original, only melee blows damage buildings: walls, gates and doors. A wall hit to 0 is ruined only when a builder or delivery first touches it, not by a world sweep (on master); walkers no longer pass through ruin walls (on master). Repair and Engineer jobs restore them. On master: an own broken door can be picked, shows the spanner pointer and offers Repair as a quick click and menu verb, and order 95 raises its progress at x3 with no materials (not seen in play yet). Play report: broken doors offer no repair and engineers do not repair them.", bugs: [] },
    { id: "naming-an-outpost-its-map-marker-buying-town-buildings", system: "Naming an outpost, its map marker, buying town buildings", status: "wip", note: "Buying a town house works and its owner is saved; outposts can be renamed from the town panel, and a bought ruined house can be restored through construction jobs (#63, not seen in play yet); player outposts show on the world map (on master). Furniture cannot be placed inside a bought town house (also after repairing it, per a play report).", bugs: [] },
    { id: "blueprints", system: "Blueprints", status: "untested", note: "Shops stock blueprints (research, armour, crossbows); right-clicking one in your own inventory learns its research. Not seen in play yet.", bugs: [] },
    { id: "walls-gates-turrets-upgrades", system: "Walls, gates, turrets, upgrades", status: "works", note: "Tested in play: wall lines, gates, turrets and building upgrades work. New on master, not seen in play yet: raiders whose target is behind a locked gate walk to it and bash it open.", bugs: ["#111"] },
    { id: "crafting-and-production", system: "Crafting and production", status: "wip", note: "Queue, repeat and research gating in code; the item furnace burns items into raw iron, and powered machines have Power and Battery toggles (on master); machines in NPC towns do not run. Play report: crafting stations make no sound while working.", bugs: [] },
    { id: "farms-and-wells", system: "Farms and wells", status: "wip", note: "Catch-up for time spent away only in Faithful mode (production far away switch, on master); crops have no quality. On master: harvest and clear progress scale with building health and power satisfaction, indoor farms use the biome water usage and ground fertility, and the catch-up water draw uses the per-second rate.", bugs: [] },
    { id: "power-generators-and-batteries", system: "Power: generators and batteries", status: "wip", note: "Generators, fuel, batteries, town power sharing and wind generators turned by the region's wind are in code; generator and battery output and capacity scale with health on master; battery mode is not saved.", bugs: [] },
    { id: "hauling-and-storage", system: "Hauling and storage", status: "wip", note: "Works on an imported save. Crafted items are not hauled, and a hauling trip in progress is not saved. On master: Auto-Haul on a storage is a one-shot haul trip, and Hauling to needs Shift.", bugs: [] },
    { id: "storage-windows-and-grids", system: "Storage windows and grids", status: "works", note: "Grid sized from the stored item, Max Capacity line, red when full.", bugs: [] },
  ] },
  { id: "world", name: "World", blurb: "Towns, crime, dialogue, animals, raids, roads and the map.", rows: [
    { id: "town-life-residents-shops-bars", system: "Town life: residents, shops, bars", status: "untested", note: "Wandering, shopping (public shops only), bar seats, sleeping at home, shooing strangers from homes, town stalkers. Overly aggressive shopkeepers: fixed on master (#120).", bugs: [] },
    { id: "crime-trespassing-arrest-jail", system: "Crime, trespassing, arrest, jail", status: "wip", note: "Guards, fines, cages, bail, bounty hunters, crime alarms (expiring 8 game seconds after the last raise); guards confiscate stolen goods on master. Guards with Protect allies defend allied defenders and hear fights in their own place, on master. Prisoners are held at the cage's facing every update; a report of prisoners facing 90 degrees away from the door is not confirmed fixed. Play report: guards do not attack intruders or wildlife until the player's character is downed.", bugs: [] },
    { id: "holy-nation-inspections-and-smuggling", system: "Holy Nation inspections and smuggling", status: "untested", note: "Gate guard inspections find the faction's illegal goods (Holy Nation: hashish and forbidden books) and post who was caught smuggling. Selling illegal goods to a trader is not checked yet.", bugs: [] },
    { id: "squads-away-from-the-camera", system: "Squads away from the camera", status: "works", note: "Tested in play: squads away from the camera work. Unloaded squads travel, fight and heal off screen. Open: NPC squads load and unload in a box of 340 round the player's characters, where the original keeps a squad while its zone (3 x 3 zones round the camera) is loaded. Corpses are saved and removed by the original's zone rule; a report of bodies vanishing beside the player is not reproduced.", bugs: [] },
    { id: "world-map-overview-and-town-discovery", system: "World map, overview and town discovery", status: "works", note: "Tested in play: the world map works. Faction overview, map items and discovering towns not seen one by one yet.", bugs: [] },
    { id: "kidnapping-cannibals-slavers", system: "Kidnapping, cannibals, slavers", status: "untested", note: "Carried to cages, made slaves, eaten. On master: slaves shipped to slave camps off screen, liberators free prisoners, escapees work off shackles, fogmen worship at cages.", bugs: ["#132"] },
    { id: "roaming-squads-roads-patrols", system: "Roaming squads, roads, patrols", status: "works", note: "Tested in play: roaming squads work. Road preference and sector patrols follow the decoded rules.", bugs: [] },
    { id: "dialogue-and-speech-bubbles", system: "Dialogue and speech bubbles", status: "wip", note: "Conditions that need state Meitou does not track yet do not pass. Said dialogue lines and replies are logged in the Dialog tab on master (#76). On master: condition 57 (within town walls) and conditions 21 and 22 (weaker or stronger than me, by combat rating) follow the original, and race tests match the race group.", bugs: [] },
    { id: "chasing-and-running-away", system: "Chasing and running away", status: "untested", note: "Chasers give up as the original does; players reported pursuing squads vanishing during long chases, often in the fog islands, fixed on master (#57). Play report: outlaws and starving bandits chase far longer than in the original.", bugs: [] },
    { id: "raids-and-campaigns", system: "Raids and campaigns", status: "wip", note: "Campaigns run and can target player outposts; the war classes are not built. An attack on an unloaded town wins after 4 hours without changing the town, as the original.", bugs: [] },
    { id: "animals-and-nests", system: "Animals and nests", status: "untested", note: "Own meshes and clips, ageing, pack beasts, butchering; on master the player's animals fetch food from outpost stores and the ground, and animals eat corpses and raid farms.", bugs: [] },
    { id: "occupied-towns", system: "Occupied towns", status: "untested", note: "A squad that holds a town patrols it, throws out downed intruders and fetches food; occupiers stay saved and resume after a load.", bugs: [] },
  ] },
  { id: "factions", name: "Factions and events", blurb: "Unique characters, faction relations and changes to towns.", rows: [
    { id: "unique-npcs-their-capture-and-death", system: "Unique NPCs, their capture and death", status: "untested", note: "Made once; death and capture saved.", bugs: [] },
    { id: "world-event-states", system: "World event states", status: "untested", note: "Answered live from unique NPCs and relations.", bugs: [] },
    { id: "faction-relations-and-ranks", system: "Faction relations and ranks", status: "untested", note: "Relation changes from blows, deaths, caging and first aid; ranks saved. The faction tab lists only factions the player has met (on master).", bugs: [] },
    { id: "towns-change-after-a-leader-falls", system: "Towns change after a leader falls", status: "untested", note: "A town takes its override when its zone loads, by the original's rule, its zone reset (buildings, item slots, loose items) and new residents; saved.", bugs: [] },
  ] },
  { id: "audio", name: "Audio", blurb: "Sound and music.", rows: [
    { id: "sound-and-music", system: "Sound and music", status: "works", note: "Heard on the Windows build, native Linux and macOS, with the Steam copy and with the GOG copy of Kenshi. Sound that did not play is fixed on master (#113).", bugs: [] },
  ] },
  { id: "interface", name: "Interface", blurb: "Windows, tooltips, pause, text and input.", rows: [
    { id: "game-speed-and-pause", system: "Game speed and pause", status: "works", note: "Pause and the 1x, 2x and 5x speeds; tested in play. Orders and interface actions given while paused work on master (#73).", bugs: [] },
    { id: "inventory-machine-and-loot-windows", system: "Inventory, machine and loot windows", status: "works", note: "Placement and opening rules as the original; both bags open when looting. An animal's character window shows an inventory grid the original leaves out. A dead character the player still owns not being selectable to see its stats is fixed on master (#79).", bugs: [] },
    { id: "interface-drawing", system: "Interface drawing", status: "works", note: "Windows, bars and text draw in play. On master, the interface is drawn into its own transparent texture and composited onto the picture before the present (not seen in play yet). Play report (Windows, Radeon): the interface flickers whenever the camera is panned away from the squad; the original does not.", bugs: ["#108"] },
    { id: "item-tooltips", system: "Item tooltips", status: "works", note: "Food, tools, weapons and armour rows, with armour stats and effect lines by the original's rules; readable with the dark palette.", bugs: [] },
    { id: "right-click-item-transfer-between-windows", system: "Right-click item transfer between windows", status: "works", note: "Fixed on master: one unit per click, Shift for the stack. On master: a right click on money moves the whole stack.", bugs: [] },
    { id: "translations", system: "Translations", status: "works", note: "The game's interface and data use Kenshi's own translations for every language it ships (French seen in play). Meitou's own texts (launcher, title and settings screens) are translated only into Brazilian Portuguese so far (#80); other languages show them in English. Some game strings were reported missed (#51).", bugs: [] },
  ] },
  { id: "platform", name: "Platform and performance", blurb: "Platforms, graphics, install detection, tests and memory.", rows: [
    { id: "runs-on-the-platform", system: "Runs on the platform", status: "works", note: "Windows build (also played under Proton on Linux); native Linux on X11 and Wayland; macOS on Apple silicon, seen by an outside tester on an M1 Pro.", bugs: ["#127","#118"], platforms: {"win":"works","linux":"works","mac":"works"} },
    { id: "macos-on-intel", system: "macOS on Intel", status: "untested", note: "An installer is published; no test report.", bugs: [], platforms: {"win":"na","linux":"na","mac":"untested"} },
    { id: "borderless-full-screen", system: "Borderless full screen", status: "bug", note: "Works on Windows and on Linux with KDE (the old panel overlap was fixed). On macOS borderless shows no window (windowed works). On Linux, Alt+Enter leaving full screen and X11 opening on the primary monitor are fixed on master (#96). Play report (Windows): while the game has focus, windows on a second monitor run at a low frame rate.", bugs: ["#17"], platforms: {"win":"works","linux":"works","mac":"bug"} },
    { id: "dlss-and-fsr-upscaling", system: "DLSS and FSR upscaling", status: "wip", note: "Both on Windows (DLSS is the default); DLSS on native Linux through NVIDIA's NGX library is on master (#99), not seen yet; FSR on macOS, from a self-built library and slower than TAA; FSR 3 upscaling and frame generation on Linux through our own build of AMD's FidelityFX library (on master, not seen yet). Frame generation (FSR 3, off by default) makes extra frames and works with DLSS on Windows (on master, not seen in play yet). On master: dust no longer floods the upscalers' reactive mask, rain and fire stay full size (not seen in play yet).", bugs: [], platforms: {"win":"works","linux":"untested","mac":"wip"} },
    { id: "ray-traced-indirect-light", system: "Ray-traced indirect light", status: "wip", note: "Optional; MoltenVK has no ray queries, so macOS draws without it and grass falls back to the CPU. Not looked at on native Linux.", bugs: [], platforms: {"win":"works","linux":"untested","mac":"missing"} },
    { id: "finding-the-kenshi-install", system: "Finding the Kenshi install", status: "wip", note: "Steam installs on Windows and Linux; the Windows registry lookup and installs by Lutris, Bottles or Heroic are untested; macOS lookup untested.", bugs: [], platforms: {"win":"works","linux":"works","mac":"untested"} },
    { id: "scripted-test-runs-repeat-exactly", system: "Scripted test runs repeat exactly", status: "wip", note: "Pinned results pass on Windows and Linux; 10 of 15 differ on Apple silicon (floating point).", bugs: [], platforms: {"win":"works","linux":"works","mac":"wip"} },
    { id: "native-metal-renderer-for-macos", system: "Native Metal renderer for macOS", status: "wip", note: "Merged (#69) and offered by the launcher on Apple silicon; tessellated ground relief is not on Metal (flat and parallax terrain only).", bugs: [], platforms: {"win":"na","linux":"na","mac":"wip"} },
    { id: "memory-on-large-saves-with-several-bases", system: "Memory on large saves with several bases", status: "wip", note: "One run per build on one laptop with an RTX 3070, Windows build under Proton, 2026-10-24: peak RAM 60 s after loading a two-area save with 18 characters was 6.2 GB on master 450e3aa1 (with #58), against 7.6 GB on cf1c7bc6 and 7.3 GB on 5087ac3a; frame-time stutter was not measured. Upstream cut texture streaming churn; #58 adds a GC budget and JIT-ahead work for stutters; #66 adds GPU frame-time work (its timings are the contributor's, on an RTX 4090, and not checked here); how much path search still allocates is not checked.", bugs: [] },
  ] },
];

export const bugs: Bug[] = [
  { id: "#145", seen: "Items stolen from unconscious NPCs do not have stolen identifier on mouse hover.", original: "Not checked.", state: "Open issue" },
  { id: "#135", seen: "Characters have no texture in customization screen.", original: "Not checked.", state: "Open issue" },
  { id: "#132", seen: "Fogmen camps do not spawn prisoner poles.", original: "Not checked.", state: "Open issue" },
  { id: "#129", seen: "Can't enter UC faction HQ building.", original: "Not checked.", state: "Open issue" },
  { id: "#130", seen: "Stuck on staircase.", original: "Not checked.", state: "Open issue" },
  { id: "#127", seen: "Smart App Control blocks DotRecast.Recast.dll on Windows 11.", original: "Not checked.", state: "Open issue" },
  { id: "#126", seen: "Height calculation differs from the original game if certain partmaps are used (e.g. high heels).", original: "Not checked.", state: "Open issue" },
  { id: "#118", seen: "No textures on dropped items and bridges.", original: "Not checked.", state: "Open issue" },
  { id: "#117", seen: "Can't enter Island Lab.", original: "Not checked.", state: "Open issue" },
  { id: "#111", seen: "Hub defensive walls clip into the Shinobi Thieves Tower.", original: "Not checked.", state: "Open issue" },
  { id: "#108", seen: "UI flickering.", original: "Not checked.", state: "Open issue" },
  { id: "#17", seen: "Borderless full screen on macOS shows no window.", original: "Not applicable.", state: "Issue closed; last report says still broken", platform: "macOS" },
];

// Fixed and seen working in play.
export const fixedSeen: Fix[] = [
  { id: "#61", text: "Armour tooltip and inventory weights match the original: stats by level, effect lines, weight and prices" },
  { id: "#52", text: "Buildings from collision mods and Workshop mods have floors and collision" },
  { id: "#53", text: "Clicks on open roof decks and roof walks go onto the roof" },
  { id: "#49", text: "Inventory, machine, storage and loot windows open and sit as in the original" },
  { id: "#56", text: "Tooltips stay readable with the dark interface palette" },
  { id: "#25", text: "Storage grids sized from the stored item, with a Max Capacity line" },
  { id: "#24", text: "Pointer and click fixes for buildings, containers and mining" },
  { id: "#14", text: "Armour tooltip with grade, class, resistances and coverage" },
  { id: "#13", text: "Borderless full screen covers the KDE panel" },
  { id: "#11", text: "Native Linux build: Wayland start-up, file names in any case" },
  { id: "", text: "Food and tool tooltips show nutrition, ingredients and charges" },
];

// Fixed on master, not yet seen in play.
export const fixedUnseen: Fix[] = [
  { id: "#120", text: "Shopkeepers are no longer overly aggressive" },
  { id: "", text: "Dust no longer floods the upscalers' reactive mask; rain and fire stay full size" },
  { id: "#113", text: "Sound plays; the no-sound report is closed as completed" },
  { id: "#94", text: "The Delete key deletes the selected save in the load window" },
  { id: "#93", text: "Part maps and editor limits work for races from mods" },
  { id: "#87", text: "Races' restricted equipment slots take nothing and draw blocked" },
  { id: "#96", text: "Linux: Alt+Enter leaves full screen; X11 opens on the primary monitor" },
  { id: "#55", text: "Crash just before finishing Main Menu loading on one player's PC" },
  { id: "#79", text: "A dead character the player still owns can be selected to see its stats" },
  { id: "#74", text: "Pick up is offered on squad members" },
  { id: "#70", text: "A character who dies is removed from its squad" },
  { id: "#57", text: "A squad chasing the player no longer vanishes during a long chase" },
  { id: "#71", text: "Doorless interiors such as hive huts can be entered and left" },
  { id: "#76", text: "Said dialogue lines and replies are logged in the Dialog tab" },
  { id: "#78", text: "No more long Proton stutters from path searches; no crash placing character parts after a quickload" },
  { id: "#77", text: "Full screen on Wayland shows the window" },
  { id: "#73", text: "Orders and interface actions work while paused" },
  { id: "#62", text: "The Hive Exile start defaults to a hive race, not a human" },
  { id: "#59", text: "Northern hub stairs can be walked down; western hive buildings can be entered" },
  { id: "#50", text: "Long routes no longer switch and re-plan over and over" },
  { id: "#33", text: "Squads walk in formation behind their leader" },
  { id: "#34", text: "Trade prices per unit, not per stack" },
  { id: "#35", text: "The slaver shop opens" },
  { id: "#36", text: "NPCs hold crossbows, not staffs, as ranged weapons" },
  { id: "#32", text: "Animal inventories no longer fill like a person's" },
  { id: "#19", text: "Characters inside player-built buildings from a Kenshi save can path out" },
  { id: "#10", text: "Characters can enter swamp houses" },
  { id: "#22", text: "No endless loading screen on Windows 11" },
  { id: "#20", text: "macOS launcher's Play button finds the Vulkan loader" },
  { id: "#15", text: "Missing character creation options added" },
  { id: "#18", text: "Text-only buttons show their hover colour" },
  { id: "", text: "Right click moves one unit between windows, Shift the stack" },
  { id: "", text: "The right-click target is the nearest hit along the ray" },
  { id: "", text: "Other factions' locked containers: lockpick and steal hints, theft is a crime" },
  { id: "", text: "\"Out of trading range\" when dragging between distant windows" },
  { id: "", text: "Quick right click on a furnace opens it; on a turret, the nearest selected character mans it" },
  { id: "", text: "Armour tooltip shows the Stealth effect and Combat speed effect lines" },
];

/** A row's status on one platform: its own entry, else the row status (the gameplay code is the same everywhere). */
export const platformStatus = (r: Row, os: Os): Status | 'na' => r.platforms?.[os] ?? r.status;
export const allRows = categories.flatMap((c) => c.rows);
export const countBy = (rows: Row[]) => Object.fromEntries(order.map((k) => [k, rows.filter((r) => r.status === k).length])) as Record<Status, number>;
/** Link for an id: #N is an issue or pull request on GitHub (GitHub redirects between the two). */
export const refUrl = (id: string) => (id.startsWith('#') ? `https://github.com/VlcekM/MeitouClient/issues/${id.slice(1)}` : undefined);

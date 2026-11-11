# Changelog

What changed in each release, newest first. The release workflow copies the section of the version it builds into the GitHub release
notes and refuses to release a version without one: add a `## vX.Y.Z` section with one `- ` line per change before running it.

## v0.17.3

- Bandit camps, nests and homeless squads no longer spawn in water or inside town walls, as the original's spawn spot rules (#496)
- NPC squads whose leader died follow a living member instead of standing by the corpse (#529)
- Patrol leaders walk to the patrol point and move on, instead of milling round it (#528)
- Guards no longer restart an endless chase after an NPC trespasser (#519)
- Squads over 40 no longer stand idle beside their fight (#412)
- Fast walkers no longer stutter round corners (#429)
- Crossbows: reload progress on the State line (#525); unequipping one mid-fight no longer crashes (#526); turret Loaded bar drains during reloads
- Stats window shows whole skill levels, not rounded up at 50% (#514)
- Inventory: ARRANGE sorts only its own grid (#524); pinned windows keep their bag and OPEN BAG works on them (#523); no oversized backgrounds
- Rebuilt town ruins' doors open on screen (#522); characters walk out of a chest on an upper floor (#460)
- Shaved heads show; birds no longer sink into the ground (#409)
- Intel macOS: no longer crashes at launch (#461)
- Contributors panel redrawn

## v0.17.2

- Interface scale setting (50-200%) for fonts, skins, panels and windows; 4K radio buttons and save list columns scale up
- Main menu style is its own setting: Meitou or Faithful title, whatever the UI style
- Character draw distance slider (the game's NPC View Range) on Graphics > Characters (#413)
- Slavery: an unshackled caged slave is shackled before processing, then bandaged by its owners (#508, #509); the slave buy list offers only the slaver's own slaves (#425)
- Building: ramps snap to walls so turrets mount on them (#459, #431); furniture no longer cuts into walls (#399) or founds an outpost in a bought house
- Steal, pick-lock and loot follow the Highlight binding, not Alt (#453); controls.cfg bindings are read as defaults (#385)
- Storage in trading range from its position too (#505); general storage takes armour and weapons by hand again (#503, #493)
- Saves: buildings listed outside their zone no longer saved twice (#493); empty inventories stay empty; Staying close targets kept (#447)
- Sleep: a healed sleeper gets up (#500); a broken-legged sleeper lies in the bed (#391)
- Animals: death items, weight and capacity, stats, inventory rows and pack sleeping as the original (#471, #445)
- Medics: only reachable kits count (#437), carried characters treat no one (#417), skeletons need Robotics to repair themselves (#407), repair beds heal damage (#395)
- Dumb NPCs such as Agnu can be talked to again (#446); arrival banter only between squad members on screen (#430)
- Put in cage and lock picking walk to the cage's floor (#432); Set Free on a prisoner in another faction's open cage (#435)
- Craft and research queues show with older layouts such as Dark UI (#463); HLP opens the Tutorials (#443); windows may be dragged off screen (#444)
- Miners haul full output and reach their rock (#388); workmasters keep up with running slaves (#402); trestle bridges walkable underneath (#403)
- Camera no longer enters a ruin's base at close zoom (#483); long names widen their name tag; AMD device-loss fix (#319)

## v0.17.1

- Version tag fixed: the build reports v0.17.1, so the feedback button and code mods' game version ranges see the right release (#379)
- Faithful UI: scrollbars on the Jobs list and the squad portrait grid
- Tab bound to Inventory opens it while a campfire's window is open; the campfire's output line no longer takes typing (#383)
- The Hunger bar's tooltip shows the racial hunger rate and the other hunger rate factors, as in the original (#377)
- Shopkeepers open their doors at opening time, not only unlock them
- A guard on an open top floor bandages the prisoner caged beside it

## v0.17.0

- Code mods: mods can ship C# code (meitou.json) with dependencies, load order, a settings page and save data; mod-check in meitou-tools
- Mods' own leveldata loads, so towns and buildings a mod adds to the world appear
- AI decides as the original: player characters and NPCs pick goals and jobs through one chooser; NPCs join fights only by their combat goals
- Senses as the original: sight, hearing and squad sightings; every awake NPC spots sneakers, and being heard trains stealth
- Combat: defenders follow the swing coming at them, fighters walk to a target's floor instead of hitting through it, crowd fights 3x cheaper
- Ranged: bolts hit the original's hull boxes, crossbow reloads pause instead of restarting, bolt tracers
- Slavery: slavers cuff, strip and carry captives, workmasters work their slaves, escaped and ex-slaves, torture racks
- Prisons: release rounds and freed prisoners as the original, Lock / Unlock on own cages; lockpick and stealth KO pointers show their chance
- Towns: nests lay their eggs, loot and debris, ruined homes, artifacts, town machines and production chains, patrols that stroll
- Raids, campaigns, roaming squads, prosperity and town wipe-outs follow the original's rules
- Birds: flocks by biome that perch on buildings and features
- Building: repair broken doors, build in ruins, wall snapping to town walls, research paid from the benches round the camera
- Trade walks up like Talk and opens in talk range; any selected character in reach trades; fencing chance shown; bought animals are named
- Bought town houses are emptied of furniture, items and residents, and what you place in them joins the town
- Frame generation: DLSS (Streamline) or FSR 3, also on Linux; DLSS preset setting; render scale modes from Ultra performance to DLAA
- Graphics settings rebuilt: Faithful or Meitou looks with presets you can save and share, the same rows in the game and the launcher
- Meitou effects: blood on walls and floors (saved), decapitation, advanced ragdolls, sun-lit dust and smoke, KenshiNative's bloom
- Severed limbs and stumps bloody and skin-tinted; clothing colours from the character's or faction's scheme; belt-slot armour drawn
- Performance: navmesh with half the polygons and faster path searches, cheaper terrain, UI and characters, fewer GC stalls (#124)
- Memory: object meshes about half the size; under VRAM pressure textures load smaller instead of not at all, so 4 GB cards show the start town
- Loading: character LOD and colour map disk caches; the loading screen waits only for what is near the camera
- Navmesh: map features walkable (Mourn, Swamp Village), sealed interiors joined by their doors, half-unit steps (#357, #358)
- Controls: rebindable mouse buttons and squad number keys, Fast and Fastest speed sliders, enhanced hardware mouse, camera speed and invert
- In-game Feedback button: sends a screenshot, save and log as a GitHub issue
- Simplified Chinese; crime names and run-time messages translated; translation mods no longer break new games (#200)
- Launcher 1.5-1.9: Continue the last save, mods From save, the mod list saves itself, Arrange by dependencies, resizable window
- Audio: sound on GOG installs (#336), output reopens on a device change (#309), mining sounds for NPC workers
- Fix sunk cannibal cages (#258), FCS prosthetics (#224), high heels sinking (#126), Hub walls in the Shinobi tower (#111), AMD spikes (#197)
- Fix the indoor camera bobbing (#204), carried animals falling off (#176, #307), miners working from afar (#332), swimming on sunken floors (#345)

## v0.16.0

- Sky arrival: new games and loads fly in from the sky, the hour running forward to the destination's
- Autosave on by default: every 10 min of unpaused play, three rotating slots, with a toggle and slider on the Gameplay page
- Disguises: uniform level, lookers seeing through it, the uniform faction in aggro, panel rows; blown disguises are saved
- Stats window shows every skill's Stats Affected values as the original (work rates, aim, first aid, yield, smithing, dodge and more)
- Inventory: race and lost-limb slot limits drawn red and refusing drops; closed slots also refuse trade, loot and storage equips (#98)
- Stolen items lose their owner mark when handed to the owner's faction; slaves are chained when sold
- Fists reach and hit with the FISTS item's length and multipliers; Fixes setting for the original's weak punch reach (on by default)
- First aid: leg-loss knock-out, splint kits, medics in a fight; kidnappers keep and cage their captives
- Power: generator and battery output scale with health; rain collectors produce with the rain; broken machines read Broken
- Player animals chase, play and race to downed bodies; travelling platoons with slaves ship them to a camp
- Faction tab lists only the factions you have met; Diplomat status entry; faction rename refuses taken names
- Homeless patrols stop outside town gates; shoppers only pick public shops; followers path up to a leader on another storey
- Import: saves older than 0.90.0 disable Reset, Buildings, Research and Dead NPCs
- Launcher 1.4.0: Graphics tabs with every setting, quality presets with hardware Detect; Launch as Kenshi on Steam on first start (Windows)
- Linux: DLSS through NVIDIA's NGX client; camera rotation locks the pointer; Alt+Enter and primary-monitor fixes (#96)
- Graphics: ground parallax and displacement as Faithful/Meitou switches, default ground relief depth 30 cm
- Overview, research, tutorials, faction panel and machine windows laid out as the original
- Fix body parts showing through worn hide parts and mod race editor files in Workshop folders (#93)
- Fix walkers passing through Hub ruin walls, bolts and gibs losing their texture, and the loot window overwriting the player's tooltips
- Fix interior bed selection and the rotation marker showing in the free camera

## v0.15.1

- macOS: native Metal renderer on Apple silicon, chosen by the launcher (Auto, Metal or Vulkan via MoltenVK); no Homebrew needed for Metal
- Combat follows the original: scored target re-pick, Passive self-defence, Hold attacks on sight, NPCs fight Ranged, stagger interrupts orders and tasks
- Close-in fighters move by the original's force-based steering (band, repulsion, strafing), with attack zones, side steps that face the target and block slots freed as the original
- Mates join a fight only for a protect goal and an attacker within range; unreachable targets are dropped after 20 s
- NPCs whose target hides behind a door bash it in as the original's planner does; door blows depend on the door's construction progress
- Stealth KO XP as the original: x0.07 on a prisoner, none for a miss on a fighting, aware target
- Purchased ruined town houses are restored through construction jobs, and what you build in them saves correctly
- New game as the original: start faction relations, start town draw with fallbacks, discovered towns of the start biome, start squad characters, research and the day-1 10:00 clock
- Pause: movement, combat, first-aid and door/cage orders apply while paused; a second shortcut click closes its overview tab
- Pick Up works on standing squad mates: lifted awake they ride until put down (#74)
- Dead player characters leave the squad and the next member leads, saved and loaded as such (#70)
- Said dialogue lines and replies are logged in the Overview's DIALOGUE tab with the original's colours (#76)
- Meitou's own texts are translatable (.po catalogs), with Brazilian Portuguese as the first (#80)
- Graphics: Ground relief and Light shafts quality settings, scaled by the quality presets; Low turns ground relief off
- Fix towns unloading while one of their squads chases you, so chasers no longer vanish (#57)
- Fix Hive huts and other doorless interiors being unreachable on the navmesh (#71)
- Fix GC stalls under Proton and a crash placing character parts (#78)
- Fix full screen not starting on Wayland (#77)
- Fix neighbouring buildings flickering when town repairs start
- Fix the HUD's Toughness XP showing 1x for a whole character where the original shows 3x (#82)

## v0.15.0

- Linux and macOS builds (linux-x64, osx-x64, osx-arm64) alongside Windows
- Production: wells, farms with growing crops, mines, research benches and repeatable research, crafting benches with queues, quality and criticals, refiners
- Power: generators, fuel, batteries, wind generators driven by the region's wind, and town power sharing; fans, spinners and wind blades turn
- Job lists as the original: operate, haul, build, medic, rescue, bodyguard, training dummies, corpse disposal and more, with the AI tab and squad AI options (Heal, Rescue Allies, Share Food, Shoot first...)
- Automatic machines work unattended; operators haul outputs to storage and fetch inputs; building upgrades and downgrades
- Your outposts get the town panel (rename, Open to public, research, power and battery bars), and public outposts draw traders
- Squad management: squads tab to add, rename, delete, reorder and swap members; followers keep the original's formations
- Beds: Sleep and Put in Bed orders, bed rent, town house purchases, and NPCs sleep in their homes' beds
- Turrets fire: gunners aim and reload, town guards man turrets when enemies are near
- Containers and storage windows, loot window with the whole inventory, drag items onto the ground, ground item labels on Alt, and stealing
- Trade: buyback, slave trading, bag and backpack trading, stock kept per shop container; prices from the trader's town culture
- Full item tooltips: armour, weapons, crossbows, limbs and food, with grades, values, resistances and crafted-by
- Combat as the original: attack technique choice, attack slots, initiative, circling, chase give-up and pursuit; right click switches targets mid-fight
- Stealth: the original's 3D arrows over noticing NPCs, the light-level icon, and gear and crawling factors
- First aid: kits drain as the original, NPC medics treat prisoners, and knockouts follow the original's KO point and coma rules
- Captives: cannibals eat prisoners, slavers turn captives into slaves and work them in mines, occupiers hold towns, refugees flee home
- Dialogue: much more of the original's conditions and actions, squad memory, NPC-to-NPC conversations, faction ranks (join the Shinobi Thieves), police talk and bail
- Relations change from blows, deaths, cagings and first aid as in the original; trespassing and witnessed assaults
- Weather affects characters: dust, acid rain and ground, gas and worn protection
- Animals age and grow, pack beasts spawn with their bags, and animals can be bought
- Character editor matches the original, with .bod2 import and export and stat-driven bodies
- Language selection with the game's translations
- Import game: start a new world from an old save
- Graphics: subsurface scattering on skin, ground and rock relief (parallax and displacement), SMAA, blood decals on the terrain, campfire and torch effects, carried lanterns, quality presets and ambient light sliders
- Audio: doors, machines, footsteps on building floors, prospecting and research sounds; voice cap slider
- Faithful mode: the original's splash screens, loading screen, title windows and fonts; a Fixes page for vanilla bug fixes
- Performance: big battles, threaded pathfinding, the next world tick runs during the draw, faster blood and posing; default frame cap 150 fps
- luckyren Mode: fatal hits burst the character
- Saves keep the camera, container contents, blood decals, shop stock and more
- Navmesh: many stairs, doors, gates and roofs walkable, no more walkers under houses or stuck off the mesh
- Fix bounties losing an hour on every save and load
- Fix a rare crash at load in the ragdoll warm-up, and a crash at speed 5 with caged characters
- Fix character creation keeping a race the start doesn't offer
- log.txt keeps its lines after a hard reset

## v0.14.1

- Fix the main menu never appearing on slower GPUs (integrated graphics, GTX 1050 Ti class): it now opens after 5 s of ready content even when frames are not smooth
- Fix loading hanging on cards short of video memory: content held back by memory pressure is no longer waited for
- --low-end now takes effect in the game: the saved graphics settings no longer override it, and it does not overwrite them

## v0.14.0

- NPCs whose target hides behind another faction's locked doors bash the door in
- Slavery: put shackles on downed or caged characters; slavers enslave a carried captive; slaves can escape, be recaptured or freed, and their state is saved
- Town shops sell from the storage containers in their building, stocked and paid like the original
- Squads that keep prisoners start with them locked in their home's cages
- Town AI sits on chairs, stools and benches, facing the way the original does
- F10 debug menu: teleport, spawn NPCs, give items and money, heal, damage, max out skills
- Name tags over your characters as in vanilla: game font, stealth icon, brackets when selected
- Terrain, grass and rocks are cut away inside buildings you look into
- Pick up items from the ground with right click; ground items are saved
- Navmesh: Storm House and watchtower upper floors, the Longhouse ramp and the L-House and Y-House doors are walkable; walkers no longer drop through floor holes
- Clicks land on wall tops and ramps; characters inside closed buildings no longer catch the pointer
- Shift+F11 shows the navmesh debug view

## v0.13.0

- Recruiting: NPCs who offer to join (hires, freed prisoners, escaped slaves, unique recruits) join your active squad, a new squad when it is full, up to 30 characters
- Swimming: characters swim in deep water with their own speeds and animations, gain swimming XP, and walk along the seabed in shallow water
- Limbs: severed limbs leave stumps and fall to the ground; fit or remove prosthetics in the limbs window; stumps and prosthetics are saved
- Shackles: shackled characters can pick, cut or break them, and they are saved; bounty desks pay for a carried bounty target and the guard cages them
- Town AI wanders, shops, sits at bar seats and mans town turrets, chosen the way the original game does
- Portraits match vanilla: hurt flash, faded when down, combat, hunger, slave and prison states, and each character keeps its portrait in saves
- Click a building to see its name, type, town, doors and shop in the HUD
- Trader window matches vanilla: 18 x 18 stock and lighter item cells
- UI fonts resize like Kenshi's and render crisper; item fill bars match vanilla
- Navmesh: Shark's stilt-house stairs reach their decks, and the walkable area no longer changes with load order

## v0.12.2

- Meitou UI is the default again

## v0.12.1

- Fixed a crash on loading a save (door update on the empty world state)
- DLSS is the default anti-aliasing (TAA where DLSS is not available)

## v0.12.0

- Audio: ambience, biome music, the title theme, interior reverb and building emitters, footsteps, combat, voices, task and interface sounds, with an Audio settings page (volumes, music frequency) and pause ducking
- Dialogue: Talk verb and conversation panel with choices, conditions, effects and locks; speech bubbles for ambient lines and campaign announcements
- Campaigns: war managers, rolls, gathering, phases and assaults, columns with leader trails, negotiation talk, and saved war state
- Population: town and nest pools that grow and regenerate, stored platoons for unloaded towns, off-screen fights and healing, roads for roaming squads, and "Wild squads stay" (Faithful off, Meitou on)
- Unique NPCs are made once (replacement or nobody) and their death and capture states are saved
- Kidnapping: raiders and cannibals lift the downed and carry them to their cage; rescued or escaped victims are free
- Doors: leaves slide open and shut, walkers open closed doors and stop at locked ones, and an Advanced "Doors close behind" setting closes them 5 s after the last passer
- Camera follows the original's pan, zoom, turn and tilt rates, input smoothing and edge scroll; the character editor has its own scene with orbit
- Inventory charge bars on food and tools; trade: right click buys or sells one unit (Shift the stack) and buying equips into a free slot
- HUD: inspecting another faction's character keeps your selection; the build button works and B opens the build window; Tab changes squad
- Hover label removed; the outline is white unless the faction is hostile
- Advanced setting "luckyren Mode" (stored only, no effect yet)

## v0.11.0

- Building: build menu with placement ghost and queue, construction sites with builders and material delivery, dismantling refunds, wall lines with shorts, connectors and wedges, and player outposts; buildings and research are saved and loaded
- Crime and jail: guards fine or arrest wanted player characters and cage them, sentences, bail, bounty hunters and claiming bounties, lock picking, and locks on doors, cages and containers
- Ragdolls: full physics ragdolls for knockouts, deaths, carrying and getting up
- Stealth: sneak animations, NPC spot meters, stealth XP, sneak-up knockouts and indicators for who is noticing you
- First aid: Splint Injuries order and splint kits, and first aid on your own characters
- Combat: Ranged keeps the crossbow up, Taunt draws fights, initiative and abort rules, and a "Late dodges and blocks" balance toggle (Faithful off, Meitou on)
- Map as in the vanilla game: orientation, zoom, camps, nests, town discovery and explored-place tooltips
- Hover outline and tint on the character under the cursor, picking over a whole body, and a Hardware mouse setting
- Damage floaters (Off/Simple/Full) and green heal numbers
- The game writes its output to log.txt next to meitou.exe, capped at 10 MB
- Fix: a roaming platoon that builds no squad no longer crashes loading a save
- Project website, with its Docker image built and published to GitHub Container Registry

## v0.10.0

- Meitou UI mode has a live title scene: the real world at sunrise near Okran's Shield, the sun turned into the view, both moons, a lone wanderer and the brush-stroke Kenshi logo; Faithful keeps the original title screen
- New games, Continue and loading saves use a slower, skippable camera transition through the stars, with lighting animated independently of simulation time
- Startup and scene loading keep the window responsive, show progress and tips, and warm the new view before play
- Load original Kenshi saves from the menu or command line, choose new-game starts, and return to the main menu without restarting
- Faithful HUD improvements: character portraits, inventory and trader item icons, floor controls and visibility, and squad selection/follow shortcuts
- Right-click character actions include trade, first aid, carrying, rescue and looting
- Known issue: an existing save-schema test reports an extra `campaign` field when comparing against some original saves

## v0.9.0

- Cinematic camera in the game: numpad shots and the timeline editor, now on F6 (F2..F4 are the game speeds), with keys saved apart from the viewer's; recording steps the whole game at a fixed 60 fps
- F7 hides the game's interface for clean screenshots and video
- Game: the viewer's Tab panel rows (time of day, VSync, full screen), Alt+Enter, and F9/F10/F11 placed below the HUD
- Characters: drawn weapons, feet grounded to their body shape, no more jitter far from the camera, cheaper level of detail and shadows
- Movement: faster turning, holding right click steers, and a path is kept when you re-order
- Sharper shadows: a 4096 shadow map by default, with a Tab slider for 1024/2048/4096
- Tab panel: a weather list replaces "Reroll weather", so you can pick any of the region's weathers for the season
- Ctrl+F1 flips each Faithful / Meitou switch in turn, one a second, so you can compare them
- Moving the clock back no longer leaves the old weather's fog behind (e.g. a dust storm hanging over the Hub)
- Red rain is drawn at half its opacity
- Faster: shadows and water cost less, and water work is skipped when no water is near
- Fights: factions turn hostile by their relations, with a leash and self-defence for an idle player
- First aid and resting: treat, rest and medic orders, automatic aid from NPCs, and medic XP while on the move
- Carrying: carry downed characters, rescue them, and loot everyone with witnessed theft (Ctrl/Alt+right click, J, K)
- Crimes and bounties: witnesses, police arrests and a bounty ledger
- Senses and fleeing: sight cone, hearing, squads sharing what they see, and characters running away
- Crossbows: reload by skill, aim spread, bolt flight, ammo and friendly fire
- Animals: drawn and animated with their own meshes, fur, size and clips, with natural weapons; wild squads and nests fill each biome
- Inventories: stack limits and backpack bonuses, with stacks split and topped up
- F5 quicksave and F9 quickload into Meitou's own slot
- `--duel` trailer scene: two Samurai fight at sunset on a cliff top
- New native in-game interface: front-end menus, inventory and map panels

## v0.7.1

- Plant layouts stay cached across updates: an update no longer makes plants fill in slowly on the first start unless the layout itself changed

## v0.7.0

- Night sky: Kenshi's two planets in the sky, and a new star field that twinkles and fades towards the horizon (Faithful keeps the game's)
- Planetshine: at night the big planet lights the land, and shadows and water reflections follow its light
- Night grading: at night dim colours shift towards a moonlit blue-grey, while lamps keep their colour
- Night air: a faint glow and planet-lit haze near the horizon; the planets are seen through the mist instead of cutting dark holes in it
- Volumetric clouds with cloud shadows that drift over land and water
- Clouds lit by the sun and the planet through their depth, with smoother cloud textures
- Light shafts and the sun's glint on water fade under heavy cloud and rain
- Water: see-through distance and clarity sliders, glint only where the reflection shows sky, swamp fog in the reflections, swamp ponds no longer turn teal
- No more straight band across distant water at sunrise and sunset
- No more DLSS streaks beside the sun while the clock runs, and shadows no longer shimmer as the sun moves
- Weather dust keeps up with the camera and blows with the wind; particle sizes match the game's
- Faster: global illumination, ambient occlusion, fog, light shafts, foliage and water all cost less, and the sky is skipped when fog hides it
- Fewer stutters while travelling: weather changes, uploads and memory are handled in the background (worst frame of a test flight 229 ms to 30 ms)
- The viewer starts in borderless full screen (Alt+Enter toggles it, --windowed for a window)
- Cinematic camera: numpad shots, an F2 timeline editor with timed keys and path recording, smooth flights, letterbox and fixed-rate video recording
- F9 shows an fps counter; grass reaches further
- The Meitou DLLs ship beside the exes, for code mods
- Many unused launch options removed

## v0.6.1

- Tab panel: sliders and checkboxes spread over three columns, so the panel no longer runs off the bottom of the screen
- Viewer: the time speed slider goes up to 240 game hours per real minute

## v0.6.0

- Light shafts: hills, buildings and trees cast shadows into the haze and weather fog, and ridges cast dark wedges into the sky towards a low sun (on by default, --no-shafts turns it off)
- Fog volumes such as the swamp's fog banks are darker where the sun is shadowed
- Morning and evening mist: a thin layer of air near the ground that lights up towards the sun, strongest at sunrise and sunset and gone by midday (Tab sliders)
- New default look: a tone map between the game's clip and ACES, so bright skies and sunlit sand roll off instead of clipping, plus a slight saturation and contrast grade (Faithful keeps the game's)
- Viewer: --time-speed and a Tab slider make the clock run (game hours per real minute)
- Viewer: --camera-code puts the camera at a code copied with Ctrl+C
- Fixed blotches and bands in the Fog Islands' fog

- Global illumination: on GPUs with ray tracing, light bounces between surfaces, so corners, interiors and the ground under overhangs darken and sunlit ground lights what faces it (on by default, --no-gi turns it off)
- Lamps: the game's outdoor point and spot lights now light the world
- F1 in the viewer (Shift+F1 in the game) turns all Meitou improvements off and back on, replacing the per-feature F keys
- Tab panel: tone map (clamp, shoulder, ACES), grading and heat haze strength sliders
- Tab panel: anisotropic filtering, texture quality, weather particles and particle density sliders; anti-aliasing can be turned off
- --low-end preset for weak PCs; render scale also works without an upscaler
- Various optimizations: simpler models for distant objects and buildings, cheaper shading where fog hides the scene, cheaper terrain, foliage, water reflections and far shadows
- Fewer stutters: at most one far shadow cascade is redrawn per frame

## v0.4.0

- Meitou water: ocean waves, sets of breakers that roll in as whitewater and wear away into lace, refraction, depth tint, caustics and glitter
- Rivers flow along their channels, with no waves or surf on them
- Swamp fog: the game's fog volumes, including the twisters' dust balls, now appear in the world
- Dense fog hides what is behind it, so foggy weather costs less to draw
- Ambient occlusion fades in haze and fog
- No white band at the horizon in overcast weather, such as the Vain's red rain
- Faster: plants and trees get simpler models in the distance and are skipped when something blocks them, far shadows are cached, and weather particles are drawn at lower resolution
- Second and later starts load the swamp's plants in about 1 s instead of 25 s
- Tab panel: slider for how far the simpler plant models start
- F11 shows frame time (mean and max) and triangles per frame
- Fewer stutters when tree billboards are generated
- --log-spikes logs slow frames, --tiered-jit and --pgo test .NET JIT settings, --bench runs benchmark views

## v0.3.0

- Weather: the game's weather schedule per region and season drives the sky, clouds, fog and heat haze; Tab panel button rerolls the weather
- Weather effects: rain, ash and other weather particles, fog banks, wet ground, objects and plants in the rain, dust on buildings and rocks, rain ripples on water
- Cloud layer drawn like the game's, drifting with the wind
- Animations: smoother blending between moves, the game's movement and attack speeds, combat clips, stumbles and falls
- Landmarks cast their shadows however far away they are
- Trees turn into billboards at the normal distance but are drawn as far as before; rocks and ruins keep their full meshes to 12000
- Characters no longer look dirty from ambient occlusion
- Low-end GPUs: shadow filter setting (Tab, --shadow-filter) and the shadow distance slider goes to 0 to turn shadows off
- Faster: weather particles, and small plants no longer drawn into the far shadow cascades
- Sandbox mode (--sandbox) for testing movement and animations on a flat grid

## v0.0.2

- The world comes alive: the Hub's residents, bar patrons and roaming squads walk around with walk, jog, run and idle animations
- New game: pick a start, select your squad with a click or a box, right-click to move (Shift queues orders), HUD with health, skills and inventory
- Pathfinding on a navmesh, through building doors and interiors
- Characters drawn with bodies, clothing, hair, weapons and facial expressions; crowds use generated detail levels
- Bodies and medical state, stats and XP, encumbrance, eating; first melee combat (attacks, blocks, armour, knockouts)
- Game keys: F10 key list, F11 statistics, F12 profiler, F8 / Print Screen screenshot, Shift+F1.. the Faithful / Meitou switches
- Tab panel: Faithful / Meitou switches as checkboxes (saved), "Delete billboard cache" button
- Faster terrain and tree billboards (about 15% less GPU time per frame in forests)
- Small plants drawn to 3500 (was 800); large trees and rock stacks stay full meshes to 12000 before turning into billboards
- Viewer: fps in the window title; long F11 lines wrap instead of running off the screen

## v0.0.1

- First release: the game (meitou.exe) and the world viewer (meitou-viewer.exe)

using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Towns;
using Meitou.Simulation.Weather;

namespace Meitou.Simulation;

/// <summary>
/// The state every tick touches for one character, in a dense struct array (docs/simulation.md, "World model"). Plain data, no
/// references, so the table copies it by block and the state hash reads it by value. What the fields mean is up to the systems;
/// the movement system uses position, velocity, yaw, goal, task and flags.
/// </summary>
public struct CharacterHot
{
    /// <summary>The slot holds a character. A dead slot's other fields are stale.</summary>
    public bool Alive;
    /// <summary>
    /// Its zone is not loaded: the character is deactivated and takes no part in the update (<c>FUN_1405c9410</c>, docs/game/game-loop.md "Inactive characters"). Set on <see cref="CharacterTable.Previous"/> once a tick by
    /// <see cref="World"/>; derived (not hashed).
    /// </summary>
    public bool Dormant;
    /// <summary>The slot's generation: raised each time the slot is used again, so an old <see cref="CharacterId"/> never names the new character.</summary>
    public int Generation;
    /// <summary>World position: X/Z on the map, Y the height the character stands at.</summary>
    public Vector3 Position;
    public Vector3 Velocity;
    /// <summary>Heading in radians, 0 along +Z, turning towards +X (the renderer's convention: yaw of <c>atan2(x, z)</c>).</summary>
    public float Yaw;
    /// <summary>Movement mode (walk, run, ...); the meaning is the movement system's.</summary>
    public byte Mode;
    public ushort Flags;
    /// <summary>Where the character is going (X/Z).</summary>
    public Vector2 Goal;
    /// <summary>Cursor into the character's path (the path itself is cold state).</summary>
    public int PathCursor;
    /// <summary>Animation state and its time in seconds.</summary>
    public ushort Animation;
    public float AnimationTime;
    public float Health;
    /// <summary>What the character is doing (a <see cref="CharacterTask"/>) and the time in seconds that task keeps (a wait, a repath cooldown).</summary>
    public byte Task;
    public float TaskTime;
    /// <summary>The speed stat S in world units (decimetres) per second, and the race walk speed (the cap of speed mode 0).</summary>
    public float MaxSpeed;
    public float WalkSpeed;
}

/// <summary>The rest of a character: things few systems touch per tick (identity, squad, path, body, inventory, combat state; the AI blackboard is still to come).</summary>
public sealed class CharacterCold
{
    public string Name { get; set; } = "";
    public int Faction { get; set; }
    /// <summary>What the renderer needs to draw the character; a character without one is simulated but not published in snapshots.</summary>
    public CharacterAppearance? Appearance { get; set; }
    /// <summary>The tick the character appeared at.</summary>
    public long SpawnedTick { get; set; }
    /// <summary>The CHARACTER (or ANIMAL_CHARACTER) record it was made from.</summary>
    public string RecordId { get; set; } = "";
    /// <summary>The squad it belongs to (-1 for none) and its role in it (the editor's SquadMemberType).</summary>
    public int SquadId { get; set; } = -1;
    public int Role { get; set; }
    /// <summary>Seconds left that a follower blocked on its way straight to its slot takes paths (<see cref="MovementSystem.FollowPathSeconds"/>).</summary>
    public float FollowPathTime { get; set; }
    /// <summary>A formation follower's heading (unit X/Z; zero until it starts), eased toward its leader's (<see cref="MovementSystem.HeadingEase"/>). Not saved; hashed while following.</summary>
    public Vector2 FollowHeading { get; set; }
    /// <summary>The speed a follower wants this tick (<see cref="MovementSystem"/>, the original's desired speed). Not saved; hashed while following.</summary>
    public float FollowSpeed { get; set; }
    /// <summary>The follower steers straight at its aim (the original's <c>Task_Follow</c> flag +0xac, with its 50 to 100 hysteresis) and where it was at its last update (+0x94). Not saved; the flag is hashed while following.</summary>
    public bool FollowDirect { get; set; }
    public Vector3 FollowLast { get; set; }
    /// <summary>
    /// A campaign follower's pace near its trail point (task 215, <c>FUN_140349e70</c>; <see cref="PopulationSystem"/>'s campaign look sets it and clears it): its walk to the point goes
    /// no faster than this. Applies only to a <see cref="CharacterTask.GoTo"/> out of combat. Not saved (the next look sets it again); hashed while set.
    /// </summary>
    public float? PaceCap { get; set; }
    /// <summary>Whom a <see cref="FollowOrder"/> told it to follow while its task is <see cref="CharacterTask.Follow"/> (None: its squad's leader). Not saved; hashed only while set.</summary>
    public CharacterId FollowOrder { get; set; } = CharacterId.None;
    /// <summary>
    /// The <see cref="FollowOrder"/> runs row 45 Bodyguard (order or job; <see cref="Running"/>): the follower also defends its target (<see cref="RetaliationSystem"/>). Hashed with <see cref="FollowOrder"/>.
    /// </summary>
    public bool Guarding => Running is { Row: Data.Gameplay.Ai.TaskType.Bodyguard };

    /// <summary>
    /// A follower of row 31 or 289 left its follow for a fight that is a step of the row (<see cref="Running"/> kept through it, <see cref="TaskStart.Start"/>): once that fight leaves it idle
    /// it follows again, as the original's follow order is still queued; any other task first drops the row. Hashed only while set.
    /// </summary>
    public bool FollowResume => Running is { Task: CharacterTask.Attack, Row: not Data.Gameplay.Ai.TaskType.Bodyguard } run && Production.PermaJobs.Follows(run.Row);
    /// <summary>The target a manned-turret gunner was told to shoot at (Shoot At, 235; <see cref="Combat.ShootAtOrder"/>); None: the gunner chooses. Not saved; hashed only while set.</summary>
    public CharacterId TurretTarget { get; set; } = CharacterId.None;
    /// <summary>The caged prisoner this character walks to free (Set Free, 110; <see cref="Locks.SetFreeOrder"/>); None: none. Not saved; hashed only while set.</summary>
    public CharacterId Freeing { get; set; } = CharacterId.None;
    /// <summary>The free cage this character walks to get into (Get in, 107; <see cref="Locks.GetInCageOrder"/>); null: none. Not saved; hashed only while set.</summary>
    public string? EnteringCage { get; set; }
    /// <summary>The path being followed (world positions, the next one at <c>CharacterHot.PathCursor</c>); replaced whole in a serial phase.</summary>
    public Vector3[] Path { get; set; } = [];
    /// <summary>
    /// The index into <see cref="Path"/> where its provisional long-range part begins (<see cref="PathResult.Exact"/>; int.MaxValue: none). Reaching it asks for
    /// the path again, at most every <see cref="MovementSystem.ProvisionalRepathSeconds"/> (<see cref="ProvisionalRepathTick"/>). Set with the path, not hashed.
    /// </summary>
    public int PathExact { get; set; } = int.MaxValue;
    public long ProvisionalRepathTick { get; set; }
    /// <summary>Times the walker was stopped on the provisional part since a long route last gained a navmesh part (<see cref="MovementSystem.ProvisionalBlockedLimit"/>).</summary>
    public int ProvisionalBlocked { get; set; }
    /// <summary>Times the walker was stopped on the navmesh part of its path since it last reached a waypoint or was given an order (<see cref="MovementSystem.BlockedRepathLimit"/>).</summary>
    public int BlockedRepaths { get; set; }
    /// <summary>The tick a move-order walker held blocked past those limits asks for its path again (<see cref="MovementSystem.BlockedRetrySeconds"/>); 0: not held. Not hashed.</summary>
    public long BlockedRetryTick { get; set; }
    /// <summary>The path request in flight, so an answer to an older one is ignored.</summary>
    public long PathRequest { get; set; }
    /// <summary>
    /// The request whose path is being followed when it was not the one in flight (an order given again before its path came, holding the right button);
    /// set only with the asynchronous path service, so not hashed.
    /// </summary>
    public long PathApplied { get; set; }
    /// <summary>
    /// A splice asked for (<see cref="MovementSystem"/>, docs/game/pathfinding.md "Long routes"): the path it replaces the start of, up to and with its point
    /// <see cref="SpliceEnd"/>, while the goal is still <see cref="SpliceGoal"/>; null: the next request is for the whole way. Not saved, not hashed.
    /// </summary>
    public Vector3[]? SplicePath { get; set; }
    public int SpliceEnd { get; set; }
    public Vector2 SpliceGoal { get; set; }
    /// <summary>The splice request in flight (0: none), and the last one sent, so its answer is never taken for a whole path. Not hashed.</summary>
    public long SpliceRequest { get; set; }
    public long LastSplice { get; set; }
    /// <summary>Belongs to the player's faction: can be selected and ordered.</summary>
    public bool IsPlayer { get; set; }
    /// <summary>The order queue (<see cref="Meitou.Simulation.Player.OrderQueue"/>, <see cref="PlayerOrders"/>): the order in hand first, then the queued ones (Shift); changed only by the character's own partition or a serial phase.</summary>
    public OrderQueue OrderQueue { get; } = new();
    /// <summary>
    /// The height a move order was given at (the clicked ground), for the goal <see cref="GoalHeightAt"/>: it picks the floor where floors are stacked.
    /// Other goals use the character's own height. Derived from orders and not hashed.
    /// </summary>
    public float GoalHeight { get; set; } = float.NaN;
    public Vector2 GoalHeightAt { get; set; }
    /// <summary>
    /// How near (units) the walk's last waypoint counts as reached; 0: <see cref="MovementSystem.ArriveRadius"/>. The AI sets it for a walk to a node, post, seat
    /// or gun (<see cref="MovementSystem.SpotArrive"/>), which ends on the spot; <see cref="MovementSystem.GoTo"/> clears it. Derived from the AI and not hashed.
    /// </summary>
    public float ArriveWithin { get; set; }
    /// <summary>Animation layers in play (made by <see cref="AnimationSystem"/>; null before the first tick it runs).</summary>
    public CharacterAnimation? Animation { get; set; }
    /// <summary>The kind of weapon drawn in the right hand (None while sheathed): the stance animations are chosen for.</summary>
    public Data.Gameplay.WeaponKinds DrawnWeapon { get; set; }
    /// <summary>The bolts loaded in the crossbow (the combat slot's, mirrored for the renderer's loaded string and bolts; display only: not hashed, not saved).</summary>
    public int CrossbowShots { get; set; }
    /// <summary>The numbers of the HUD's extended lines, refreshed by <see cref="BodySystem"/> every tick (display only: not hashed, not saved).</summary>
    public ExtendedStats Extended { get; set; }
    /// <summary>
    /// The skill its job used at <see cref="SkillInUseTick"/> (the original's Character <c>+0x198</c> <c>currentSkillUsing</c>, docs/game/ui-screens.md section 4): what the HUD's
    /// "Current Skill" line shows. Set by the job systems with <see cref="UseSkill"/> (display only: not hashed, not saved).
    /// </summary>
    public Meitou.Data.Gameplay.Bodies.StatsEnumerated SkillInUse { get; private set; }
    public long SkillInUseTick { get; private set; } = long.MinValue;
    /// <summary>Marks <paramref name="stat"/> as the skill its job uses this tick.</summary>
    public void UseSkill(Meitou.Data.Gameplay.Bodies.StatsEnumerated stat, long tick) => (SkillInUse, SkillInUseTick) = (stat, tick);
    /// <summary>The race, stats and medical state (null for anything without a race record): see <see cref="BodySystem"/>.</summary>
    public Meitou.Data.Gameplay.Bodies.RaceData? Race { get; set; }
    /// <summary>
    /// An animal's ANIMAL_CHARACTER fields (null for people), set with its body. Its age (the original's <c>CharacterAnimal+0x700</c>) is <see cref="Bodies.CharacterStats.Scale"/>;
    /// <see cref="BodySystem"/> grows it (docs/game/animals.md "Ageing").
    /// </summary>
    public Meitou.Data.Gameplay.Bodies.AnimalData? Animal { get; set; }
    /// <summary>The age an animal's drawn size was last refreshed at (the original's <c>+0x6fc</c>, the step gate of the look refresh only: no gameplay reader): the size the renderer shows is lerp(this, scale min, scale max). Follows from the age: not hashed.</summary>
    public float ShownAge { get; set; } = 1;
    /// <summary>The live age gameplay reads (the original's <c>CharacterAnimal+0x700</c>, vtable slot 0x398): <see cref="Bodies.CharacterStats.Scale"/>, 1 without stats.</summary>
    public float Age => Stats?.Scale ?? 1;
    /// <summary>
    /// The bolts stuck in the character (docs/game/combat.md "Bolt visuals"): shown on their bones until first aid has nothing left to bandage. A visual: not hashed, not saved
    /// (the original keeps them in its appearance only).
    /// </summary>
    public List<StuckBolt>? StuckBolts { get; set; }
    public CharacterStats? Stats { get; set; }
    public MedicalState? Medical { get; set; }
    /// <summary>Sandbox only (the animation sandbox of the game host): replaces the movement mode of every order, and the speed the character walks at (0: the speed chain's). Not hashed; the normal game never sets them.</summary>
    public byte? ModeOverride { get; set; }
    public float SpeedOverride { get; set; }
    /// <summary>The path cost factor of water for this character (race <c>water avoidance</c>, halved for the player faction; 0: default), the floor of its <see cref="WaterCost"/>.</summary>
    public float WaterFactor { get; set; }
    /// <summary>Its faction's FACTION <c>road preference</c> (0: none), with which a long trip takes the roads (<see cref="RoadNetwork.Route"/>).</summary>
    public float RoadPreference { get; set; }
    /// <summary>The water state 0..3 (<see cref="Meitou.Simulation.Bodies.Speed.WaterState"/>) <see cref="MovementSystem"/> found at the character's position, and its swim speed (<see cref="Meitou.Simulation.Bodies.Speed.Swim"/>) <see cref="BodySystem"/> keeps up to date. Derived each tick: not hashed, not saved.</summary>
    public byte WaterState { get; set; }
    /// <summary>The ground height under a ragdolled body (the original's Character +0x698, set from the ragdoll's ground probe; 0: none yet). Derived: not hashed, not saved.</summary>
    public float StandHeight { get; set; }
    public float SwimSpeed { get; set; }
    /// <summary>
    /// The weather acting on the character (the original's animation holder <c>+0x198</c> / <c>+0x1a0</c>, docs/formats/weather.md "Effects on characters"): its kind, the
    /// region's strength s and the worn protection from it, set by <see cref="WeatherSystem"/> while it is exposed and not immune; None otherwise. Sight and hearing read them.
    /// Derived each tick: not hashed, not saved.
    /// </summary>
    public Meitou.Data.World.WeatherAffecting Weather { get; set; }
    public float WeatherStrength { get; set; }
    public float WeatherProtection { get; set; }
    /// <summary>
    /// The status notice a player character's weather update put up this tick (the original's short status string, Character <c>+0x548</c>, cleared at the start of every weather update and set when the
    /// effect hurt it, docs/formats/weather.md "Effects on characters") and the protection it shows. Derived each tick: not hashed, not saved.
    /// </summary>
    public WeatherNotice WeatherNotice { get; set; }
    public float WeatherNoticeProtection { get; set; }
    /// <summary>Where the last blood decal of its trail was left, and the drip build-up while it bleeds being carried (<see cref="BodySystem"/>, docs/game/blood-decals.md). Display only: not hashed, not saved.</summary>
    public Vector3 BloodMark { get; set; }
    public float BloodDrip { get; set; }
    /// <summary>Burst by a fatal hit in luckyren Mode (<see cref="World.Luckyren"/>): the blow's direction (X/Z); <see cref="BodySystem"/> removes it at the end of the tick. Not saved.</summary>
    public Vector2? Burst { get; set; }
    /// <summary>
    /// Meitou: the hit that put it down (<see cref="Ragdolls.RagdollBlow"/>; <see cref="Ragdolls.RagdollSystem"/> takes it and clears it). The original starts a ragdoll at rest. Not saved.
    /// </summary>
    public Ragdolls.RagdollBlow? Blow { get; set; }
    /// <summary>
    /// Meitou: a throw given to a downed character (the debug explosion, <c>CombatSystem.Blast</c>): world units/s. <see cref="Ragdolls.RagdollSystem"/> adds it to the ragdoll's bodies
    /// (waking a frozen one) or, without a ragdoll, turns it into <see cref="Slide"/>, and clears it. Not hashed, not saved.
    /// </summary>
    public Vector3? Launch { get; set; }
    /// <summary>The flat speed (world units/s) a downed character without a ragdoll is still sliding at after a <see cref="Launch"/>; it drags to a stop. Not hashed, not saved.</summary>
    public Vector3 Slide { get; set; }
    /// <summary>The last <see cref="Blow"/> it was given, kept for the drawing (a cut-off head is thrown with it, <see cref="CharacterSnapshot.Knockdown"/>). Not hashed, not saved.</summary>
    public Vector3 Knockdown { get; set; }
    /// <summary>What it carries (null for anything without a race; an animal's grid is its <c>inventory w</c> x <c>inventory h</c>, its luggage on the backpack slot):<see cref="Meitou.Simulation.Items.FeedSystem"/> eats from it, <see cref="BodySystem"/> weighs it.</summary>
    public Items.Inventory? Inventory { get; set; }
    /// <summary>The tick it died at (-1: alive); the corpse decays 12 game hours later (<see cref="BodySystem.SlowWorld"/>, which holds the clock back while it is carried or held).</summary>
    public long DiedTick { get; set; } = -1;
    /// <summary>The tick of the body system's last step for it (<see cref="BodySystem"/>, 2x and 5x step in turns); -1 before the first. Derived, not hashed or saved.</summary>
    public long BodyStepTick { get; set; } = -1;
    /// <summary>
    /// The medical time the character's update budget turns gave since its last body step (<see cref="Bodies.MedicalTimeBudget"/>, the original's second add to Character
    /// <c>+0xc8</c>); the body step adds it to its own time and clears it. Derived, not hashed or saved.
    /// </summary>
    public float MedicalBudgetSeconds { get; set; }
    /// <summary>
    /// The original's <c>CharBody::amIdle</c> (<see cref="BodySystem"/>): standing still out of combat with no task, refreshed only while the character is not down, so a knock-out
    /// keeps the value it had before. Derived, not hashed or saved.
    /// </summary>
    public bool AmIdle { get; set; }
    /// <summary>A corpse with flies (docs/game/character-stats.md "Corpses": 4 game hours after death while the weather leaves characters alone); it smells of blood (<see cref="SmellSystem"/>).</summary>
    public bool HasFlies { get; set; }
    /// <summary>Where the character came from in a loaded save, with the saved stats and medical state (null for a character the game made itself); see <c>Meitou.Simulation.Saving</c>.</summary>
    public Saving.SavedCharacterLink? Save { get; set; }
    /// <summary>The look was changed in the character editor (<see cref="Editing.EditCharacter"/>): a save writes its CHARACTER_APPEARANCE back.</summary>
    public bool LookEdited { get; set; }
    /// <summary>RACE <c>pathfind footprint radius</c> (0: not known, paths use the default human); what the navmesh keeps clear.</summary>
    public float FootprintRadius { get; set; }
    /// <summary>The character is in a fight (set by the combat system): the animation stance takes the combat clips.</summary>
    public bool InCombat { get; set; }
    /// <summary>
    /// The status byte <c>+0x114</c> ("ordered", docs/game/economy.md 11.6): set on an attacker by the assault booking of an ordered attack (<c>FUN_140334070</c>), cleared when its fight ends
    /// (<see cref="Combat.CombatSystem"/> <c>EndFight</c>, the stand-in for the attack task's end, <c>FUN_1405c91c0</c>); a victim with it set is not assaulted again. The original also sets it at
    /// ordered task starts and for the cannibal assault package's squad (not modelled). Not saved; hashed while set.
    /// </summary>
    public bool Ordered { get; set; }
    /// <summary>
    /// The crossbow controller's <c>combatMode</c> (<c>+0x2f0 → +0x36</c>, docs/game/ui-screens.md section 4 "Combat mode"): set by each shooting tick (the shooting task's update), cleared
    /// when the shooting ends (the fight's end, or the switch to melee). Only the HUD reads it; not saved, not hashed.
    /// </summary>
    public bool RangedCombatMode { get; set; }
    /// <summary>
    /// Character <c>+0x3d8 lastUsedWeaponCategory</c>: the <c>skill category</c> of the weapon in hand, written only by the HUD while it shows the character in a melee fight (0 at
    /// creation; no save key). At 7 the accuracy line takes Turrets. Display state: not saved, not hashed.
    /// </summary>
    public int LastWeaponCategory { get; set; }
    /// <summary>
    /// The key of the turret the character mans this tick (set by the combat system's serial step, docs/simulation.md "Combat", turrets; null: none). Derived every tick; not saved, not hashed.
    /// </summary>
    public string? Gunning { get; set; }
    /// <summary>A fighter running at its target out of the combat stance (set by the <see cref="PursuitSystem"/>, docs/game/combat.md "Combat stance").</summary>
    public bool CombatRunning { get; set; }
    /// <summary>
    /// The slot of the target a fighter in its footwork faces while a path moves it (set by the <see cref="PursuitSystem"/> every tick, -1: none; docs/animation.md "Side steps").
    /// Derived every tick; not saved, not hashed.
    /// </summary>
    public int FaceSlot { get; set; } = -1;
    /// <summary>The fight steering of a fighter in its footwork (<see cref="Combat.SteerState"/>; null until its first fight). Not saved; hashed while active or on a path chase.</summary>
    public SteerState? Steer { get; set; }
    /// <summary>
    /// The fight steering's speed (the original's steering +0x12c, <c>FUN_1402af1e0</c>): it grows by <see cref="MovementSystem.Acceleration"/> per second up to the top speed while
    /// the character fights and is never reset, so a fighter that has got going keeps its pace through stops, new paths and the footwork (whose 18 caps the speed used, not this).
    /// </summary>
    public float SteerSpeed { get; set; }
    /// <summary>
    /// The nav agent's own speed along its path, moved toward the wanted one at <see cref="MovementSystem.PathAcceleration"/>. A turn slows the step it takes, not this, so
    /// a walker taking a bend does not lose its pace bit by bit; 0 when it stands, lies or is held.
    /// </summary>
    public float AgentSpeed { get; set; }
    /// <summary>Seconds the fighter's target has been moving away out of a fight (the <see cref="PursuitSystem"/>'s fleeing check).</summary>
    public float TargetFleeingTime { get; set; }
    /// <summary>Its last path request came back with no way there (set by the <see cref="MovementSystem"/>, taken by the <see cref="PursuitSystem"/>).</summary>
    public bool PathFailed { get; set; }
    /// <summary>
    /// How its last walk (<see cref="CharacterTask.GoTo"/>) ended, set by the <see cref="MovementSystem"/>: <see cref="WalkEnd.Finished"/> when it followed its path to the
    /// last point, <see cref="WalkEnd.Failed"/> when it gave the path up (no way there, blocked, a locked door), <see cref="WalkEnd.None"/> while walking or when another
    /// system stopped it. What the AI's arrival reads (<c>isAtLocation</c>, docs/game/ai-tasks.md "Planner"). Not saved or hashed.
    /// </summary>
    public WalkEnd WalkEnd { get; set; }
    /// <summary>The target's place (height included) when the <see cref="PursuitSystem"/> last asked a path to <see cref="CharacterHot.Goal"/>: the path's end is measured against it, as the target may have moved since. Not saved.</summary>
    public Vector3 PursuitGoal { get; set; }
    /// <summary>
    /// The AI's ignore map (<c>AITaskSytem.pathFailures</c> +0x218, filled by <c>notifyPathImpossible</c> <c>FUN_140796ed0</c> with 20 s, read by <c>isPathImpossible</c> <c>FUN_1405b0290</c>): targets (a
    /// character, a site id, an <c>item:</c> id) it found no path to and the tick until which every finder leaves them out. Insertion order is kept (the lapse check reads it in order). Not saved.
    /// </summary>
    public List<(IgnoreKey Key, long Until)>? Ignored { get; set; }
    /// <summary>Ignores <paramref name="key"/> until the tick <paramref name="until"/> (a site id, <c>item:</c> id or machine's site id).</summary>
    public void NoteUnreachable(string key, long until) => Ignore(new IgnoreKey(key, default), until);
    /// <summary>Whether <paramref name="key"/> is still ignored at <paramref name="tick"/>.</summary>
    public bool IsUnreachable(string key, long tick) => IsIgnored(new IgnoreKey(key, default), tick);
    /// <summary>Ignores <paramref name="key"/> until <paramref name="until"/>, replacing an earlier entry for it.</summary>
    public void Ignore(IgnoreKey key, long until)
    {
        var list = Ignored ??= [];
        int at = list.FindIndex(e => e.Key == key);
        if (at >= 0) list[at] = (key, until); else list.Add((key, until));
    }
    /// <summary>Whether <paramref name="key"/> is ignored at <paramref name="tick"/>.</summary>
    public bool IsIgnored(IgnoreKey key, long tick)
    {
        if (Ignored is not { Count: > 0 } list) return false;
        foreach (var (k, until) in list) if (k == key && until > tick) return true;
        return false;
    }
    /// <summary>
    /// The number that turns its portrait camera (docs/game/ui-screens.md "Portrait cells"): the save's <c>portrait_serial</c>, else its handle's serial; a character the game makes
    /// gets random × 4e9 at <see cref="CharacterTable.Spawn"/>, as the original rolls one per character.
    /// </summary>
    public uint? PortraitSerial { get; set; }
    /// <summary>The build whose bone shape the character wears: made once, refreshed only while the character is of the player's faction (<c>FUN_140531820</c>, <c>FUN_1405338f0</c>). Not saved: made again at load, as the original.</summary>
    public BodyBuild? ShapedBuild { get; set; }
    /// <summary>The character is running away (set and cleared by the <see cref="FleeSystem"/>, serial phases only): it neither looks for fights nor answers attacks.</summary>
    public bool Fleeing { get; set; }
    /// <summary>
    /// The flight is Self preservation's (goal 32 through row 35, <see cref="Ai.SelfPreservation"/>), not a squad's: it ends when no threat attacks the character within reach, not when no
    /// enemy is in sight. Meaningful while <see cref="Fleeing"/>; set and cleared with it by the <see cref="FleeSystem"/>.
    /// </summary>
    public bool FleesAlone { get; set; }
    /// <summary>
    /// How exposed a sneaking character was at <see cref="StealthExposureTick"/> (set by the <see cref="AggroSystem"/> in its serial commit): what its stealth XP
    /// rate reads (<see cref="Bodies.XpService.Sneaking"/>).
    /// </summary>
    public StealthExposure StealthExposure { get; set; }
    public long StealthExposureTick { get; set; } = long.MinValue;
    /// <summary>
    /// The factor on the stealth XP of a hearing check (CharStats <c>+0x174</c>, <c>FUN_1408c7330</c>): 1, x0.7 after each check, back to 1 by the sneak XP step (<see cref="BodySystem"/>).
    /// Set by <see cref="AggroSystem"/>'s serial commit, which also adds the checks' XP to <see cref="HearingXp"/>; <see cref="BodySystem"/> pays that into Stealth. Hashed once it leaves its
    /// start, not saved (the original keeps it in the stats object and no loader writes it: <b>Observed</b>).
    /// </summary>
    public float StealthFactor { get; set; } = 1;
    public float HearingXp { get; set; }
    /// <summary>The hostile NPCs noticing or detecting this sneaker lately (<see cref="AggroSystem"/>, serial commit; display only: not hashed, not saved).</summary>
    public List<StealthWatcher>? Watchers { get; set; }
    /// <summary>Weapon and worn armour (track F, docs/simulation.md "Combat").</summary>
    public Fighter? Fighter { get; set; }
    /// <summary>The patient of the <see cref="CharacterTask.Treat"/> task (itself for self-treatment); meaningful only while the task is set (<see cref="FirstAid.FirstAidSystem"/>).</summary>
    public CharacterId TreatTarget { get; set; } = CharacterId.None;
    /// <summary>The treatment splints instead of bandaging (the original's <c>SPLINT_ORDER</c>, task "Splinting").</summary>
    public bool TreatSplint { get; set; }
    /// <summary>
    /// The kit a first aid treatment is using (the first aid task's stored kit, +0x80, <c>FUN_14033c7e0</c>): kept while it is of the wanted function with charges left and still held, else searched again.
    /// Runtime only (not saved, not hashed): derived, as the next treatment tick finds the same kit again by the fewest-charges rule.
    /// </summary>
    public (Items.ItemInstance Item, Items.Inventory Owner)? TreatKit { get; set; }
    /// <summary>
    /// The tick until which a medic treating this character asked it to stand still (the original's request of 62 Standing still on the patient's blackboard, renewed by every update of
    /// the first aid task, docs/game/ai-tasks.md "Medic jobs"; <see cref="FirstAid.FirstAidSystem"/>). <see cref="MovementSystem"/> holds an NPC still until then unless it fights or flees.
    /// Runtime only (not saved, not hashed): the running treatment renews it every tick.
    /// </summary>
    public long StandStillUntil { get; set; }
    /// <summary>
    /// The treating medic kneels at its patient (the <c>medic</c> clip): in reach of a patient keeping still, or treating itself (<see cref="FirstAid.FirstAidSystem"/>'s serial step, every tick of
    /// the treatment). Runtime only (not saved, not hashed): drawing only.
    /// </summary>
    public bool TreatKneels { get; set; }
    /// <summary>The medic job (58 <c>JOB_MEDIC</c>) is in <see cref="Jobs"/>: an idle character with a kit treats hurt squad mates by itself. NPCs with a kit do so without it (an engine rule until the AI).</summary>
    public bool MedicJob => Jobs.Exists(j => j.Type == Data.Gameplay.Ai.TaskType.JobMedic);
    /// <summary>The character this one carries on its left shoulder (none: not carrying); the original's <c>isCarrying</c> with the partner handle. Set and cleared by <see cref="Carrying.CarrySystem"/> in serial phases only, together with <see cref="CarriedBy"/> of the other.</summary>
    public CharacterId Carrying { get; set; } = CharacterId.None;
    /// <summary>The character carrying this one (none: not carried); the original's <c>isBeingCarried</c> with the partner handle.</summary>
    public CharacterId CarriedBy { get; set; } = CharacterId.None;
    /// <summary>While carried: lifted awake by a friendly carrier (the player's Pick Up on a standing squad mate), so it stays on the shoulder until put down rather than being set down as one that came round.</summary>
    public bool CarriedAwake { get; set; }
    /// <summary>While carried: where the body hangs on its carrier's shoulder, from the carrier's pose (<see cref="Ragdolls.RagdollSystem"/>, each tick); null without the carrier's skeleton.</summary>
    public Carrying.CarryAttach? CarryAttach { get; set; }
    /// <summary>Whom the <see cref="CharacterTask.PickUp"/>, <see cref="CharacterTask.Loot"/> or <see cref="CharacterTask.StealthKo"/> task is for; meaningful only while one of those tasks is set.</summary>
    public CharacterId CarryTarget { get; set; } = CharacterId.None;
    /// <summary>Seconds into the <c>shoulder lift left</c> clip of a pick-up or put-down, or the <c>knockout</c> swing of a stealth KO, in progress (-1: not yet; the animation system plays the clip at this time).</summary>
    public float LiftTime { get; set; } = -1;
    /// <summary>The stealth KO target heard the attacker on its last step before the swing (the sense entry's heard flag, <see cref="CharacterTask.StealthKo"/>).</summary>
    public bool KnockoutHeard { get; set; }
    /// <summary>The <see cref="CharacterTask.PickUp"/> is a kidnap (<see cref="Carrying.PickUpOrder.Kidnap"/>): the lift rolls <see cref="Carrying.CarrySystem.KidnapChance"/> first.</summary>
    public bool Kidnapping { get; set; }
    /// <summary>
    /// The <see cref="CharacterTask.PickUp"/> may snatch a body from another carrier (the original's Task_LiftSomething <c>+0x79</c>, set for task types 213, 225 and 240): the player's
    /// pick-up order. Runtime only; hashed with the task.
    /// </summary>
    public bool Snatching { get; set; }
    /// <summary>The <see cref="CharacterTask.Loot"/> is the foraging job's (Task_Loot_ResourcesWeHaveStorageFor, row 241): it takes only what the faction's storages accept, after a wait. Runtime only; hashed with the task.</summary>
    public bool Foraging { get; set; }
    /// <summary>
    /// The animation class's crouched byte (AnimationClass +0x21c, written with prone by <c>FUN_14051dda0</c>; both set clears crouched): set by Task_Crouch (27) once its <c>squat T</c> clip has passed a progress threshold
    /// (<c>FUN_140346440</c>), cleared by Task_UnCrouch (28) and the end of many tasks. The base data has no crouch task; nothing sets it yet (the task trigger is missing). Hashed only while set.
    /// </summary>
    public bool Crouched { get; set; }
    /// <summary>The NPC <see cref="CharacterTask.Loot"/>'s start flags (<see cref="Carrying.LootStart"/>), fixed when the task starts (<see cref="LiftTime"/> below 0); cleared with the task. Runtime only; hashed with the task.</summary>
    public Carrying.LootStart LootFlags { get; set; }
    /// <summary>The rescue job (148 <c>FIND_AND_RESCUE_IF_THERES_BEDS</c>, NUM8) is in <see cref="Jobs"/>: an idle character picks up a downed member of its faction by itself.</summary>
    public bool RescueJob => Jobs.Exists(j => j.Type == Data.Gameplay.Ai.TaskType.FindAndRescueIfTheresBeds);
    /// <summary>The orders panel's toggles (<see cref="Meitou.Simulation.Player.Orders"/>); the player's characters only.</summary>
    public CharacterOrders Orders { get; set; } = CharacterOrders.Jobs;
    /// <summary>
    /// The point the Hold order holds (CharStats +0x130): where Hold was turned on, then, while on Hold, the destination of the running ground move order (its own or a queued
    /// one; <see cref="OrderMove"/>); null: where it stands (not saved).
    /// </summary>
    public Vector3? HoldPoint { get; set; }
    /// <summary>The Stand Up order (28, <c>Task_UnCrouch</c>) is running: the crouch clip plays backwards (<see cref="AnimationSystem"/>) and <see cref="Bodies.MedicalState.StayingLow"/> clears when it is nearly back at its start. Not saved; hashed while set.</summary>
    public bool StandingUp { get; set; }
    /// <summary>The sleeper is getting out of a <c>gotobed</c> bed (<c>Task_GetOutOfBed</c>): the bed clip plays backwards (<see cref="AnimationSystem"/>) until <see cref="BedExitDone"/>. Not saved; hashed while set.</summary>
    public bool LeavingBed { get; set; }
    public bool BedExitDone { get; set; }
    /// <summary>The AI's manoeuvre mode (standing orders 8 and 9; the original's save key is unknown, so it is not saved, like <see cref="HoldPoint"/>).</summary>
    public ManoeuvreMode Manoeuvre { get; set; } = ManoeuvreMode.RoamFar;
    /// <summary>The AI's centre of movement (<c>AI::setCenterOfMovement</c>, +0x2c0): set by NEAR to where the character stands and by its ground move orders while not ROAM_FAR; null: none (not saved).</summary>
    public Vector3? CentreOfMovement { get; set; }
    /// <summary>
    /// The running <see cref="CharacterTask.GoTo"/> is the player's ground move order or a queued one (the original's Task_Move flag <c>+0x95</c>, set for tasks 29 and 40 only):
    /// cleared by any other <see cref="MovementSystem.GoTo"/>. Runtime only.
    /// </summary>
    public bool OrderMove { get; set; }
    /// <summary>The tick its stumble clip ends (combat's StunUntil, set at each interrupting hit): until then it neither walks nor works (docs/game/combat.md "Stagger outside a fight").</summary>
    public long StaggeredUntil { get; set; }
    /// <summary>
    /// Until when a stagger outside a fight reports the Stumbling task (147, <c>Character::_startStumble</c> queues it in front of the character's own task, which replans afterwards): set
    /// with the stumble clip's stun, display only. Not saved.
    /// </summary>
    public long StumbleTaskUntil { get; set; }
    /// <summary>The tick its get-up after a knockout ends (<see cref="Combat.CombatSystem.GetUpClipSeconds"/> from waking, after the clip; Character +0x278 isCurrentlyGettingUp).</summary>
    public long GettingUpUntil { get; set; }
    /// <summary>The speed mode the character's move orders use (the original's saved <c>speed mode</c>: walk, run or no cap); no cap by default.</summary>
    public byte RunSpeed { get; set; } = SpeedMode.Free;
    /// <summary>
    /// CHARACTER <c>NPC class</c> (the editor's CharacterTypeEnum; <c>fcs.def</c>'s default is MILITARY), set from the record at spawn; <c>None</c> for a character without a record
    /// (hand-made in tests). The crime rules read it (who is police, who may loot, who can be booked); not hashed, it follows from the record.
    /// </summary>
    public Data.Gameplay.FundamentalType NpcClass { get; set; }
    /// <summary>
    /// CHARACTER <c>faction importance</c> (the game's <c>Character+0x41c</c>, read by <c>FUN_14062b210</c>): how much a deed against it moves its faction's relations
    /// (<see cref="FactionDeeds.Weight"/>); 0 without a CHARACTER record. Set from the record at spawn, so not hashed.
    /// </summary>
    public float FactionImportance { get; set; }
    /// <summary>The character's bounties by faction (null: none ever); <see cref="Crime.CrimeSystem"/>, serial phases only. Hashed only while set.</summary>
    public Crime.BountyLedger? Bounties { get; set; }
    /// <summary>A crime nobody has booked yet (null: none); <see cref="Crime.CrimeSystem"/>, serial phases only. Hashed only while set.</summary>
    public Crime.PendingCrime? Pending { get; set; }
    /// <summary>What the AI decided for it (null: a character the AI does not run: the player's, or one whose squad has no AI packages); <see cref="Ai.AiSystem"/>.</summary>
    public AiMind? Ai { get; set; }
    /// <summary>Held still by an interaction (a trader in an open trade, <see cref="Trading.TradeSystem"/>): the AI leaves it alone. Serial phases only.</summary>
    public bool Engaged { get; set; }
    /// <summary>
    /// The personality tag dialogue tests (DC_PERSONALITY_TAG, docs/game/dialogue.md "Line check"): the save's <c>personality</c> when it has one, else rolled from the record on first use
    /// (<see cref="Dialogue.Personality"/>); 0 not yet known. Follows from the seed, the id and the record, so not hashed.
    /// </summary>
    public int Personality { get; set; }
    /// <summary>The dialogues this character has locked or unlocked (DIALOGUE string id → locked; saved as <c>lok*</c>); null: none changed. <see cref="Dialogue.DialogueSystem"/>; hashed only while set.</summary>
    public SortedDictionary<string, bool>? DialogueLocks { get; set; }
    /// <summary>
    /// The original's <c>Character+0x2f8</c> (save key <c>in something</c>): in a bed or imprisoned in a cage or restraint, with <see cref="InWhat"/> the
    /// <see cref="Locks.Fixture.Id"/> it is in (null: not known, e.g. a loaded prisoner whose cage the world has not mapped). Set by <see cref="Locks.FixtureRegistry"/>, serial phases only.
    /// </summary>
    public Locks.HeldState InSomething { get; set; }
    /// <summary>Game hours of the last update of a bed it lay in (status <c>+0xc0</c>, refreshed every update of Task_UseBed, <c>FUN_140347950</c>); 0 before any. Row 98's desire reads it ; derived, not hashed or saved.</summary>
    public double BedUsedHours { get; set; }
    public string? InWhat { get; set; }
    /// <summary>Imprisoned: it does not move, fight, carry or run AI (docs/simulation.md "Cages, beds and locks").</summary>
    public bool IsImprisoned => InSomething == Locks.HeldState.Imprisoned;
    /// <summary>
    /// The prison sentence as the original stores it (docs/game/economy.md 11.5): the start in <see cref="World.GameHours"/> and the length in game hours (save keys <c>sentence</c>,
    /// <c>psts</c>); served when start + length is not after now. Length 0: none (served).
    /// </summary>
    public double SentenceStart { get; set; }
    public float SentenceLength { get; set; }
    /// <summary>
    /// The original's "recently wanted" marker (status <c>+0x110</c>, set to 250 by a booking or a witnessed crime, docs/game/economy.md 11.2/11.8): while above 0 a looker recognises
    /// the bounty at once. Game seconds counting down by dt (<c>AI::updateStateBroadcast</c> <c>FUN_1405a4ae0</c>, <b>Observed</b>); the same field is the disguise's blown cooldown (saved as <c>disguiseblown</c>). <see cref="Crime.CrimeSystem"/>; hashed only while set.
    /// </summary>
    public float RecentlyWanted { get; set; }
    /// <summary>The bounty recognition meter (economy.md 11.8; <b>engine</b>: one per target, not per looker): fires at 1. <see cref="Crime.ArrestSystem"/>; hashed only while set.</summary>
    public float Recognition { get; set; }
    /// <summary>
    /// The clothing disguise (status <c>+0x100</c>, docs/game/perception.md "Disguise"): 0..1, recomputed for the player's characters by <see cref="Crime.ArrestSystem"/>; with
    /// <see cref="DisguiseFaction"/> the uniform's faction (status <c>+0x108</c>, -1 none). Hashed only while set.
    /// </summary>
    public float DisguiseLevel { get; set; }
    public int DisguiseFaction { get; set; } = -1;
    /// <summary>Seconds left on the disguise tooltip's three marks (same faction, slave owner, someone knows me; 14 s from the last looker that applied one). Runtime only, not hashed.</summary>
    public float[]? DisguiseMarks { get; set; }
    /// <summary>The escaped-prisoner flag (status <c>+0xe8</c>, docs/game/locks.md "Cages and beds"): left a cage by itself (the leave task's flag, written for a real cage owner only). Hashed only while set.</summary>
    public bool Escaped { get; set; }
    /// <summary>Held in a cage by a kidnapper (<see cref="Kidnap.KidnapSystem"/>), not sentenced: no sentence is ever served, no bail is offered, getting out is no crime. Saved as <c>meitou kidnapped</c>. Hashed only while set.</summary>
    public bool Kidnapped { get; set; }
    /// <summary>
    /// Let out of a cage by a releaser, a paid bail or a slave bought free (the original's status <c>+0xea</c>, set by <c>Task_ReleasePrisoner</c>, <c>FUN_1406ae170</c> and <c>FUN_1406add20</c>): no faction attacks it on
    /// sight for the relations alone unless it is not real or below -30 by default, and it attacks no one so (<see cref="AggroSystem"/>). Dropped when it is out of every town
    /// (<see cref="Crime.ArrestSystem"/>, <c>FUN_1405a4f50</c>) and when a pending crime is set through the original's wrapper (<see cref="Crime.CrimeSystem.SetPending"/>). Saved as <c>tn</c> (<see cref="Saving.CrimeSave"/>); hashed while set.
    /// </summary>
    public bool Freed { get; set; }
    /// <summary>
    /// The original's slave state (status <c>+0</c>, saved as <c>slavestate</c>; <c>FUN_1405a4940</c>): 0 free, 1 slave, 2 escaped slave, 3 ex-slave. A slave's sentence is never served.
    /// <see cref="Locks.FixtureRegistry.Enslave"/>; an escaped slave lies low to ex-slave, then free (<see cref="Locks.FixtureRegistry.LayLow"/>); hashed only while set.
    /// </summary>
    public byte SlaveState { get; set; }
    /// <summary>The game hours at which <see cref="SlaveState"/> last became 2 or 3 (status <c>+0x8</c>, saved as the float <c>ssct</c>; <see cref="Locks.FixtureRegistry.LayLow"/>). Hashed with a state above 1.</summary>
    public double SlaveSince { get; set; }
    /// <summary>The faction owning a slave or escaped slave (the original keeps a <c>slaver</c> handle; -1 none). Saved as <c>meitou slave owner</c>; hashed with <see cref="SlaveState"/>.</summary>
    public int SlaveOwner { get; set; } = -1;
    /// <summary>The squad a slave obeys (the original's owner handle naming a platoon; -1 none): job 187 Obedient Slave runs that squad's <c>Slave AI Goals</c> (<see cref="Ai.AiSystem"/>).
    /// Runtime only (not saved, not hashed): set where the slave is made or sold.</summary>
    public int SlaveMaster { get; set; } = -1;
    /// <summary>
    /// Its head is shaven (<c>CharacterHuman::isHeadShaven</c>, Character vslot 124 <c>0x3e0</c>: the appearance's <c>shaved</c>, <b>Verified</b>; false in every other class).
    /// Processing a slave shaves it and an ex-slave's hair grows back once it is free (<see cref="Kidnap.CaptiveSystem"/>, <see cref="Locks.FixtureRegistry.LayLow"/>); the watchers
    /// leave a shaven slave that looks like one alone. From the CHARACTER's or the saved GAMESTATE_CHARACTER's <c>shaved</c> (<see cref="Saving.SlaveSave"/>); hashed while set.
    /// </summary>
    public bool Shaved { get; set; }
    /// <summary>Stood up from playing dead on the player's order since it went down (<c>playerWantsMeToGetUp</c>): the get-up line may be event 25. Runtime only.</summary>
    public bool OrderedUp { get; set; }
    /// <summary>The player's speed group (<c>SpeedGroup</c>, made for the selection by Group speed: the first member's slot; -1 none). Runtime only.</summary>
    public int SpeedGroup { get; set; } = -1;
    /// <summary>The fixture this character is picking the lock of (<see cref="Locks.LockSystem"/>; null: none). Hashed only while set.</summary>
    public string? PickingLock { get; set; }
    /// <summary>The picker's work this update (Task_PickLock, <c>FUN_140359160</c>): the tick, the cage's <c>animation lockpick</c> clip (null: the medic clip) and the play speed 0.55 + 1.45 x 0.01 x skill (cosmetic). Not hashed: it follows from <see cref="PickingLock"/>.</summary>
    public (long Tick, string? Clip, float Rate) PickWork { get; set; } = (-1, null, 1);
    /// <summary>The door this character is bashing in (<see cref="Locks.LockSystem"/>; null: none). Hashed only while set.</summary>
    public string? Bashing { get; set; }
    /// <summary>Whether <see cref="Bashing"/> was ordered by the player (Smash, 226) rather than found by the pursuit: the basher then needs no attack target. Hashed with <see cref="Bashing"/>.</summary>
    public bool BashOrdered { get; set; }
    /// <summary>Whether <see cref="Bashing"/> names a wall site (a building of the world's registry) rather than a door: Smashing (272, planned through 270 when no gate leads to the target). Hashed with <see cref="Bashing"/>.</summary>
    public bool BashWall { get; set; }
    /// <summary>Whether <see cref="Bashing"/> is a door bashed for an errand (the ransackers' entry, row 81 planned for goal 216) rather than an attacker's: it needs no attack target and ends when the door opens. Hashed with <see cref="Bashing"/>.</summary>
    public bool BashErrand { get; set; }
    /// <summary>The fixture whose lock this character cuts or breaks by order (Use Tools 285, Use Strength 286; <see cref="Locks.LockSystem"/>; null: none). Hashed only while set.</summary>
    public string? ForcingLock { get; set; }
    /// <summary>How <see cref="ForcingLock"/> is forced.</summary>
    public Locks.LockForce Force { get; set; }
    /// <summary>The door this character locks or unlocks by order (Lock 77, Unlock 140; <see cref="Locks.LockSystem"/>; null: none). Hashed only while set.</summary>
    public Locks.DoorLockJob? DoorJob { get; set; }
    /// <summary>Game seconds since the last swing at <see cref="Bashing"/>' door (-1: walking there).</summary>
    public float BashTime { get; set; }
    /// <summary>The shackles this character is working on (<see cref="Locks.LockSystem"/>: picking, cutting; null: none). Hashed only while set.</summary>
    public Locks.ShackleJob? Shackles { get; set; }
    /// <summary>The ground item this character walks to and picks up (<see cref="Items.GroundItems"/> id, <see cref="PlayerSystem"/>; null: none). Hashed only while set.</summary>
    public int? PickingUpItem { get; set; }
    /// <summary>The limb an animal holds in its mouth (<see cref="Items.FeedSystem.HoldInMouth"/>; null: none), eaten later by <see cref="Items.FeedSystem"/>. Hashed only while set; not saved.</summary>
    public Items.ItemInstance? Mouth { get; set; }
    /// <summary>The world tick at which <see cref="Mouth"/> was taken.</summary>
    public long MouthSinceTick { get; set; }
    /// <summary>The skin tone of the character <see cref="Mouth"/> was cut from (<see cref="Items.FallSource.SkinTone"/>; 0: untinted), for its drawing only.</summary>
    public Vector3 MouthTone { get; set; }
    /// <summary>The building site the <see cref="CharacterTask.Build"/>, <see cref="CharacterTask.Dismantle"/> or <see cref="CharacterTask.DeliverMaterials"/> task works (<see cref="Construction.BuildSite.Id"/>; null: none). Serial phases only. Hashed only while set.</summary>
    public string? BuildSite { get; set; }
    /// <summary>The material delivery bar (0 to 1, filling at 0.2 per second, <see cref="CharacterTask.DeliverMaterials"/>). Hashed with <see cref="BuildSite"/>.</summary>
    public float BuildBar { get; set; }
    /// <summary>The player building (<see cref="Construction.BuildSite.Id"/>) a <see cref="CharacterTask.FetchMaterials"/> builder takes <see cref="FetchRecord"/> from (null: none, or a ground item). Hashed with <see cref="BuildSite"/>.</summary>
    public string? FetchSite { get; set; }
    /// <summary>The ground item (<see cref="Items.GroundItems"/> id) a <see cref="CharacterTask.FetchMaterials"/> builder picks up (null: none). Hashed with <see cref="BuildSite"/>.</summary>
    public int? FetchItem { get; set; }
    /// <summary>The material record a <see cref="CharacterTask.FetchMaterials"/> builder fetches (null: not fetching). Hashed with <see cref="BuildSite"/>.</summary>
    public string? FetchRecord { get; set; }
    /// <summary>The building the <see cref="CharacterTask.Operate"/> task works (<see cref="Construction.BuildSite.Id"/>; null: none). Serial phases only. Hashed only while set.</summary>
    public string? OperateSite { get; set; }
    /// <summary>The progress (0 to 1) of the <see cref="CharacterTask.Prospect"/> task (null: not prospecting). Serial phases only; not saved. Hashed only while set.</summary>
    public float? Prospecting { get; set; }
    /// <summary>The storage an operator walks to, to store its machine's output or fetch an input (<see cref="Production.ProductionSystem"/> "Hauling"; null: at its machine). Not saved. Hashed only while set.</summary>
    public string? HaulSite { get; set; }
    /// <summary>The ITEM a hauling operator fetches from <see cref="HaulSite"/>; null: it stores what it carries.</summary>
    public string? HaulFetch { get; set; }
    /// <summary>The job list (Perma-Jobs, <see cref="Production.PermaJobs"/>), first = highest priority. Serial phases only. Hashed only while not empty.</summary>
    public List<Production.PermaJob> Jobs { get; } = [];
    /// <summary>
    /// The provenance of the task (<see cref="TaskStart.Start"/>; null: none recorded): which job of <see cref="Jobs"/> or implicit job it runs (<see cref="Production.PermaJobs.ListJob"/>) and
    /// the task it goes back to. Serial phases only; not saved. Hashed while the task is a job's, with the resume task of a treatment, a carry, a site's work or a job list.
    /// </summary>
    public RunningTask? Running { get; set; }
    /// <summary>The storage a <see cref="CharacterTask.Haul"/> trip ends at (a "Hauling to" job's, or the one an implicit ditch picked). Hashed with <see cref="HaulSite"/>.</summary>
    public string? HaulTo { get; set; }
    /// <summary>The clip the operator plays while it works (the functionality's <c>animation</c>).</summary>
    public string? OperateClip { get; set; }
    /// <summary>The clips of the cage it is held in (<see cref="Locks.Fixture.Clips"/>), set when it is caged; read only while <see cref="InSomething"/> is imprisoned. Derived, not hashed.</summary>
    public Locks.CageClips CageClips { get; set; }
    /// <summary>The BUILDING_FUNCTIONALITY (string id) of the machine its operate task runs, whose <c>animation events</c> sound on <see cref="OperateClip"/>; derived, not hashed.</summary>
    public string? OperateFunctionality { get; set; }
    /// <summary>The ITEM the operator holds while its operate task runs (the functionality's <c>special tool</c>); derived, not hashed.</summary>
    public string? OperateTool { get; set; }
    /// <summary>The machine's <see cref="Meitou.Data.Gameplay.Construction.MachineData.HungerRate"/> while its operate task runs; derived, not hashed.</summary>
    public float OperateHungerRate { get; set; } = 1;
    /// <summary>The operator did work this tick (it plays <see cref="OperateClip"/>); derived, not hashed.</summary>
    public bool Operating { get; set; }
    /// <summary>The whole-body clip (an ANIMATION record name) a captor holds while it works a captive standing (<see cref="Kidnap.CaptiveSystem"/>: "chop down static", "eat", "medic"); null: none. Derived, not hashed.</summary>
    public string? ActionClip { get; set; }
    /// <summary>
    /// The machine an AI worker or obedient slave works standing (its functionality and the clip it set as <see cref="ActionClip"/>), whose <c>animation events</c> sound on that clip
    /// while <see cref="ActionClip"/> is still it (the original runs <c>Task_OperateMachine</c> for them too); null: none. Derived, not hashed.
    /// </summary>
    public WorkEvents? ActionWork { get; set; }
    /// <summary>
    /// The replan bit (<see cref="DecisionSystem.Request(CharacterCold)"/>): set by a replan trigger, consumed by the decision step. Player characters only; not saved (a load sets it); hashed while set.
    /// </summary>
    public bool Replan { get; set; }
    /// <summary>The queued orders a load read (<see cref="Saving.OrderSave"/>), until the decision step makes them the <see cref="OrderQueue"/> once the world they name is there; null: none. Not hashed.</summary>
    public IReadOnlyList<Saving.SavedOrder>? SavedOrders { get; set; }
    /// <summary>The last decision (<see cref="DecisionSystem.Tracing"/> on; null otherwise). Display only: not saved or hashed.</summary>
    public DecisionTrace? Decision { get; set; }
    /// <summary>Debug builds: the table tick (<see cref="CharacterTable.Ticks"/>) and the task the start path last set (<see cref="TaskStart.CheckStarts"/>); not hashed.</summary>
    internal long TaskSetTick = -1;
    internal byte TaskSet;
}

/// <summary>
/// The characters of a world: <see cref="CharacterHot"/> structs in two dense arrays, <see cref="Previous"/> (the last tick's
/// result, which every phase reads, other characters' included) and <see cref="Next"/> (the tick being computed, where a phase
/// writes only its own characters), plus a <see cref="CharacterCold"/> object per slot. A tick starts with <see cref="BeginTick"/>
/// (Next becomes a copy of Previous) and ends with <see cref="EndTick"/> (the two swap). Slots are reused, the most recently freed
/// first, with the generation raised; spawning and removing happen only in the serial phases, in the order the commit sorts them,
/// which is what keeps slot assignment independent of the thread count.
/// </summary>
public sealed class CharacterTable
{
    CharacterHot[] _previous;
    CharacterHot[] _next;
    CharacterCold?[] _cold;
    readonly Stack<int> _free = [];
    int _highWater;
    bool _inTick;
    TablePhase _phase = TablePhase.Outside;

    /// <summary>Limbs cut off in the tick (the character's slot, the limb slot 0 to 3): the population system rebuilds the look and drops the severed limb between ticks.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<(int Character, int Limb)> LimbsChanged { get; } = new();

    public CharacterTable(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _previous = new CharacterHot[capacity];
        _next = new CharacterHot[capacity];
        _cold = new CharacterCold?[capacity];
    }

    /// <summary>Slots ever used: every character is in 0..HighWater-1.</summary>
    public int HighWater => _highWater;
    /// <summary>Characters alive.</summary>
    public int Count => _highWater - _free.Count;
    /// <summary>The characters ever added: a pass waiting for a character to appear runs again only when this has changed.</summary>
    public long Spawns { get; private set; }

    /// <summary>The last tick's state of the slots 0..HighWater-1.</summary>
    public ReadOnlySpan<CharacterHot> Previous => _previous.AsSpan(0, _highWater);
    /// <summary>The state being computed, same slots. A phase writes only the slots of its own partition.</summary>
    public Span<CharacterHot> Next => _next.AsSpan(0, _highWater);

    public CharacterCold? Cold(int slot) => _cold[slot];

    /// <summary>The id of the character in <paramref name="slot"/> as of the last tick.</summary>
    public CharacterId IdOf(int slot) => new(slot, _previous[slot].Generation);

    /// <summary>Whether <paramref name="id"/> names a character alive as of the last tick.</summary>
    public bool IsAlive(CharacterId id) =>
        id.Slot >= 0 && id.Slot < _highWater && _previous[id.Slot].Alive && _previous[id.Slot].Generation == id.Generation;

    /// <summary>Finds the slot of <paramref name="id"/> in the state being computed (for the commit, which sees removals and spawns of this tick).</summary>
    public bool TryResolveNext(CharacterId id, out int slot)
    {
        slot = id.Slot;
        return slot >= 0 && slot < _highWater && _next[slot].Alive && _next[slot].Generation == id.Generation;
    }

    internal TablePhase Phase
    {
        get => _phase;
        set => _phase = value;
    }

    /// <summary>The ticks begun (a paused interface step counts): the start path's debug stamp (<see cref="CharacterCold.TaskSetTick"/>).</summary>
    internal long Ticks { get; private set; }

    /// <summary>Sets the activity flag of a slot on the previous tick's state (<see cref="CharacterHot.Dormant"/>; serial phases).</summary>
    internal void SetDormant(int slot, bool dormant) => _previous[slot].Dormant = dormant;

    internal void BeginTick()
    {
        Ticks++;
        Array.Copy(_previous, _next, _highWater);
        _inTick = true;
    }

    internal void EndTick()
    {
        (_previous, _next) = (_next, _previous);
        _inTick = false;
    }

    /// <summary>
    /// Adds a character; call only from the serial phases (inputs, commit, slow world) or before the first tick. The slot is the
    /// most recently freed one, else a new one. The generation and <c>Alive</c> in <paramref name="state"/> are set here.
    /// </summary>
    public CharacterId Spawn(CharacterHot state, CharacterCold cold, long tick)
    {
        ArgumentNullException.ThrowIfNull(cold);
        if (_phase == TablePhase.Parallel) throw new InvalidOperationException("characters are spawned in the serial phases only");
        int slot;
        int generation;
        if (_free.Count > 0)
        {
            slot = _free.Pop();
            generation = _next[slot].Generation + 1;
        }
        else
        {
            slot = _highWater;
            if (slot == _previous.Length) Grow();
            _highWater++;
            generation = 0;
        }
        state.Alive = true;
        state.Generation = generation;
        // No goal yet is its own spot, not the world's origin: a follower waiting out its first think steers at its goal (MovementSystem), and walked off towards (0, 0)
        // and asked for a path there, a long route across the world (issue #124).
        if (state.Goal == Vector2.Zero) state.Goal = new Vector2(state.Position.X, state.Position.Z);
        cold.SpawnedTick = tick;
        cold.PortraitSerial ??= (uint)(Rng.Float(Rng.Mix(Rng.Key(new CharacterId(slot, generation)) ^ (ulong)tick * 0x9E3779B97F4A7C15UL)) * 4e9f);
        _next[slot] = state;
        // Outside a tick there is no "last tick" to keep apart from the new one.
        if (!_inTick) _previous[slot] = state;
        this._cold[slot] = cold;
        Spawns++;
        return new CharacterId(slot, generation);
    }

    /// <summary>The id the next <see cref="Spawn"/> will give (serial phases), so a character's seeded body can be made before it is added.</summary>
    public CharacterId PeekNextId() => _free.Count > 0 ? new CharacterId(_free.Peek(), _next[_free.Peek()].Generation + 1) : new CharacterId(_highWater, 0);

    /// <summary>Removes a character in the state being computed; false when <paramref name="id"/> is not alive there. Serial phases only.</summary>
    public bool Remove(CharacterId id)
    {
        if (_phase is TablePhase.Parallel or TablePhase.Inputs or TablePhase.Schedule)
            throw new InvalidOperationException("characters are removed in the commit or the slow world only (a phase reads them as alive)");
        if (!TryResolveNext(id, out int slot)) return false;
        _next[slot].Alive = false;
        if (!_inTick) _previous[slot].Alive = false;
        _cold[slot] = null;
        _free.Push(slot);
        return true;
    }

    /// <summary>
    /// Puts a character at <paramref name="position"/> facing <paramref name="yaw"/>, standing still with no path (a teleport: into a cage), its task
    /// <paramref name="task"/>. Serial phases or setup only; outside a tick both buffers are written, as <see cref="Spawn"/> does.
    /// </summary>
    public void Place(int slot, Vector3 position, float yaw, CharacterTask task = CharacterTask.Idle)
    {
        if (_phase == TablePhase.Parallel) throw new InvalidOperationException("characters are placed in the serial phases only");
        ref var n = ref _next[slot];
        n.Position = position;
        n.Yaw = yaw;
        n.Velocity = Vector3.Zero;
        n.Goal = new Vector2(position.X, position.Z);
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
        n.Task = (byte)task;
        n.TaskTime = 0;
        if (!_inTick) _previous[slot] = n;
    }

    void Grow()
    {
        int size = _previous.Length * 2;
        Array.Resize(ref _previous, size);
        Array.Resize(ref _next, size);
        Array.Resize(ref _cold, size);
    }

    /// <summary>Adds the table's canonical state, in slot order, to <paramref name="hasher"/>: every slot's state, the free list in reuse order and the cold fields that matter.</summary>
    internal void Hash(ref StateHasher hasher)
    {
        hasher.Add(_highWater);
        for (int i = 0; i < _highWater; i++)
        {
            ref readonly var c = ref _previous[i];
            hasher.Add(c.Alive);
            hasher.Add(c.Generation);
            if (!c.Alive) continue;
            hasher.Add(c.Position);
            hasher.Add(c.Velocity);
            hasher.Add(c.Yaw);
            hasher.Add((int)c.Mode);
            hasher.Add((int)c.Flags);
            hasher.Add(c.Goal);
            hasher.Add(c.PathCursor);
            hasher.Add((int)c.Animation);
            hasher.Add(c.AnimationTime);
            hasher.Add(c.Health);
            hasher.Add((int)c.Task);
            hasher.Add(c.TaskTime);
            hasher.Add(c.MaxSpeed);
            hasher.Add(c.WalkSpeed);
            var k = _cold[i]!;
            hasher.Add(k.Faction);
            hasher.Add(k.SpawnedTick);
            hasher.Add(k.SquadId);
            hasher.Add(k.Role);
            hasher.Add(k.FollowPathTime);
            if (k.StandingUp) hasher.Add(0x5374556E);
            if (k.LeavingBed) { hasher.Add(0x4C766742); hasher.Add(k.BedExitDone); }
            if (c.Task == (byte)CharacterTask.Follow) { hasher.Add(k.FollowHeading); hasher.Add(k.FollowSpeed); hasher.Add(k.FollowDirect); }
            if (k.PaceCap is { } pace) hasher.Add(pace);
            hasher.Add(k.Path.Length);
            hasher.Add(k.PathRequest);
            hasher.Add(k.IsPlayer);
            k.OrderQueue.Hash(ref hasher);
            hasher.Add((int)k.DrawnWeapon);
            BodyHash.Add(ref hasher, k.Stats, k.Medical);
            if (k.Inventory is null) hasher.Add(-1); else k.Inventory.Hash(ref hasher);
            hasher.Add(k.DiedTick);
            if (k.HasFlies) hasher.Add(0x466C6965);
            hasher.Add(k.InCombat);
            if (k.CombatRunning || k.TargetFleeingTime != 0) { hasher.Add(0x43527573); hasher.Add(k.CombatRunning); hasher.Add(k.TargetFleeingTime); }   // only while set, so worlds without fights hash as before
            if (k.Steer is { Active: true } steer) steer.Hash(ref hasher);
            else if (k.Steer is { ChaseTarget: >= 0 } chase) { hasher.Add(0x43686173); hasher.Add(chase.ChaseTarget); hasher.Add(chase.PathUntil); }   // a path chase, only while on one
            if (k.Ignored is { Count: > 0 } failures && failures.Exists(e => e.Key.Name is null)) { hasher.Add(0x50464C53); foreach (var (key, until) in failures) if (key.Name is null) { hasher.Add(key.Id.Slot); hasher.Add(key.Id.Generation); hasher.Add(until); } }
            if (k.SteerSpeed != 0) { hasher.Add(0x53746572); hasher.Add(k.SteerSpeed); }
            if (k.AgentSpeed != 0) { hasher.Add(0x41676e74); hasher.Add(k.AgentSpeed); }
            if (k.Animation is { } anim) anim.Hash(ref hasher); else hasher.Add(-1);
            hasher.Add(k.Name.Length);
            // First aid state adds to the hash only while it is set, so worlds that never use it hash as before.
            var resume = k.Running is { } run && run.Runs((CharacterTask)c.Task) ? run.Resume : CharacterTask.Idle;
            if (c.Task == (byte)CharacterTask.Treat) { hasher.Add(k.TreatTarget); hasher.Add((int)resume); if (k.TreatSplint) hasher.Add(0x53706C74); }
            if (k.Fleeing) hasher.Add(k.FleesAlone ? 0x466C6566 : 0x466C6565);   // only while set, so worlds without fleeing hash as before
            if (k.StealthExposureTick != long.MinValue) { hasher.Add((int)k.StealthExposure); hasher.Add(k.StealthExposureTick); }   // likewise, once a sneaker was exposed
            if (k.StealthFactor != 1 || k.HearingXp != 0) { hasher.Add(0x48724558); hasher.Add(k.StealthFactor); hasher.Add(k.HearingXp); }   // likewise, once a listener's hearing check gave it stealth XP
            // Carrying, looting and the rescue job add to the hash only while set, so worlds without them hash as before.
            if (!k.Carrying.IsNone) { hasher.Add(0x43617272); hasher.Add(k.Carrying); }
            if (!k.CarriedBy.IsNone) { hasher.Add(0x42726E65); hasher.Add(k.CarriedBy); }
            if (k.CarriedAwake) hasher.Add(0x4177616B);
            if (c.Task is (byte)CharacterTask.PickUp or (byte)CharacterTask.PutDown or (byte)CharacterTask.Loot or (byte)CharacterTask.StealthKo) { hasher.Add(k.CarryTarget); hasher.Add(k.LiftTime); hasher.Add((int)resume); if (c.Task == (byte)CharacterTask.StealthKo) hasher.Add(k.KnockoutHeard); if (k.Kidnapping) hasher.Add(0x4B69646E); if (k.Snatching) hasher.Add(0x536E6174); if (k.Foraging) hasher.Add(0x466F7267); if (k.LootFlags != 0) hasher.Add(0x4C6F6F74 ^ (int)k.LootFlags); }
            // Orders add to the hash only while set, so worlds without them hash as before.
            if (k.Orders != CharacterOrders.None) { hasher.Add(0x4F726473); hasher.Add((int)k.Orders); }
            if (k.RunSpeed != SpeedMode.Free) { hasher.Add(0x52756E53); hasher.Add((int)k.RunSpeed); }
            // Bounties and pending crimes add to the hash only while set, so worlds without crimes hash as before.
            k.Bounties?.Hash(ref hasher);
            k.Pending?.Hash(ref hasher);
            // The AI's decision and the trade hold add to the hash only while set, so worlds without AI packages hash as before.
            if (k.Ai is { } mind) { hasher.Add(0x41694D64); mind.Hash(ref hasher); }
            if (k.Engaged) hasher.Add(0x456E6761);
            if (!k.FollowOrder.IsNone) { hasher.Add(0x466F6C77); hasher.Add(k.FollowOrder); if (k.Guarding) hasher.Add(0x47756172); }
            if (k.FollowResume) hasher.Add(0x46526573);
            if (k.Replan) hasher.Add(0x52706C6E);
            if (!k.TurretTarget.IsNone) { hasher.Add(0x54725467); hasher.Add(k.TurretTarget); }
            if (!k.Freeing.IsNone) { hasher.Add(0x46726565); hasher.Add(k.Freeing); }
            if (k.EnteringCage is { } entering) { hasher.Add(0x456E7472); hasher.Add(Rng.StableHash(entering)); }   // only while set, so worlds without follow orders hash as before
            if (k.DialogueLocks is { Count: > 0 } locks)
            {
                hasher.Add(0x4C6F6B73);
                foreach (var (dialogue, locked) in locks) { hasher.Add(Rng.StableHash(dialogue)); hasher.Add(locked); }
            }
            // Beds, cages and sentences add to the hash only while set, so worlds without them hash as before.
            if (k.InSomething != Locks.HeldState.None) { hasher.Add(0x496E536D); hasher.Add((int)k.InSomething); hasher.Add(k.InWhat is { } w ? Rng.StableHash(w) : 0UL); }
            if (k.SentenceLength > 0) { hasher.Add(k.SentenceStart); hasher.Add(k.SentenceLength); }
            // The arrest state adds to the hash only while set, so worlds without it hash as before.
            if (k.RecentlyWanted > 0) { hasher.Add(0x52576E74); hasher.Add(k.RecentlyWanted); }
            if (k.Recognition > 0) { hasher.Add(0x5265636F); hasher.Add(k.Recognition); }
            if (k.DisguiseLevel > 0) { hasher.Add(0x44697367); hasher.Add(k.DisguiseLevel); hasher.Add(k.DisguiseFaction); }
            if (k.Escaped) hasher.Add(0x45736361);
            if (k.Kidnapped) hasher.Add(0x4B64706E);
            if (k.Freed) hasher.Add(0x46726564);
            if (k.Ordered) hasher.Add(0x4F72646E);
            if (k.SlaveState != 0) { hasher.Add(0x536C7653); hasher.Add(k.SlaveState); hasher.Add(k.SlaveOwner); if (k.SlaveState > 1) hasher.Add(k.SlaveSince); }
            if (k.Shaved) hasher.Add(0x53686176);
            if (k.Crouched) hasher.Add(0x43726F63);
            if (k.PickingLock is { } pick) { hasher.Add(0x5069636B); hasher.Add(Rng.StableHash(pick)); }
            if (k.Bashing is { } bash) { hasher.Add(0x42617368); hasher.Add(Rng.StableHash(bash)); hasher.Add(k.BashTime); if (k.BashOrdered) hasher.Add(0x4F726473); if (k.BashWall) hasher.Add(0x57616C6C); if (k.BashErrand) hasher.Add(0x45727264); }
            if (k.ForcingLock is { } force) { hasher.Add(0x466F7263); hasher.Add(Rng.StableHash(force)); hasher.Add((int)k.Force); }
            if (k.DoorJob is { } doorJob) { hasher.Add(0x446F724A); hasher.Add(Rng.StableHash(doorJob.Door)); hasher.Add(doorJob.Lock); hasher.Add(doorJob.Work); }
            if (k.Shackles is { } job) { hasher.Add(0x5368636B); hasher.Add(Rng.Key(job.Wearer)); hasher.Add((int)job.Work); }
            if (k.PickingUpItem is { } taking) { hasher.Add(0x54616B65); hasher.Add(taking); }
            if (k.Mouth is { } mouth) { hasher.Add(0x4D6F7574); hasher.Add(Rng.StableHash(mouth.Record)); hasher.Add(k.MouthSinceTick); }
            // A builder's site, bar and the task it goes back to add to the hash only while it works one, so worlds without sites hash as before.
            if (k.BuildSite is { } site)
            {
                hasher.Add(0x4275696C); hasher.Add(Rng.StableHash(site)); hasher.Add(k.BuildBar); hasher.Add((int)resume);
                if (k.FetchRecord is { } fetch) { hasher.Add(0x46657463); hasher.Add(Rng.StableHash(fetch)); hasher.Add(Rng.StableHash(k.FetchSite ?? "")); hasher.Add(k.FetchItem ?? 0); }
            }
            if (k.OperateSite is { } machine) { hasher.Add(0x4F706572); hasher.Add(Rng.StableHash(machine)); }
            if (k.Prospecting is { } prospected) { hasher.Add(0x50726F73); hasher.Add(prospected); }
            if (k.HaulSite is { } haul) { hasher.Add(0x4861756C); hasher.Add(Rng.StableHash(haul)); hasher.Add(Rng.StableHash(k.HaulFetch ?? "")); }
            if (k.HaulTo is { } haulTo) hasher.Add(Rng.StableHash(haulTo));
            var listJob = Production.PermaJobs.ListJob(k, (CharacterTask)c.Task);
            if (listJob is { Implicit: true }) { hasher.Add(0x496D706C); hasher.Add((int)listJob.Row); hasher.Add((int)listJob.Resume); }
            if (k.Running is { Source: TaskSource.Job, Task: CharacterTask.Treat or CharacterTask.PickUp or CharacterTask.Loot } pass && pass.Runs((CharacterTask)c.Task)) hasher.Add(0x4A6F6254);   // the job passes' mark, only while it runs
            if (k.Jobs.Count > 0)
            {
                hasher.Add(0x4A6F6273);
                hasher.Add(listJob?.Job ?? -1);
                hasher.Add((int)resume);
                foreach (var perma in k.Jobs) { hasher.Add((int)perma.Type); hasher.Add(Rng.StableHash(perma.Site ?? "")); if (!perma.Character.IsNone) hasher.Add(perma.Character); }
            }
        }
        hasher.Add(_free.Count);
        foreach (int slot in _free) hasher.Add(slot);
    }
}

/// <summary>Where in a tick the table is, so a spawn or removal in the wrong place fails loudly instead of corrupting a tick.</summary>
public enum TablePhase
{
    /// <summary>Between ticks (setup): anything goes.</summary>
    Outside,
    /// <summary>Spawns allowed; removals not (the phases that follow read the character as alive).</summary>
    Inputs,
    Schedule,
    /// <summary>Think, move, act: no spawns or removals.</summary>
    Parallel,
    Commit,
    SlowWorld,
}
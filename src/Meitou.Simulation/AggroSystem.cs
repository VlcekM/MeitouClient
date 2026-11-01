using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

/// <summary>Settings of the <see cref="AggroSystem"/> (docs/simulation.md "Hostility and relations"; the original's figures in docs/game/perception.md).</summary>
public sealed record AggroSettings
{
    /// <summary>A character looks round once every this many ticks (staggered by slot: slot s on the ticks where tick % n == s % n). Engine choice: the original
    /// updates the senses of 8 characters per frame in turn (docs/game/perception.md "Update rate").</summary>
    public int ScanEveryTicks { get; init; } = 15;
    /// <summary>How far an NPC sees, in world units (dm), before the race's <c>vision range mult</c>: the original's outdoor range (1500, <c>FUN_140851bc0</c>, Verified),
    /// in full daylight; straight ahead only, the view cone shortens it to the side (<see cref="AggroSystem.Visibility"/>).</summary>
    public float SightRange { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>Keep a <see cref="AggroSystem.Log"/> of every fight an NPC's combat goal started (the host prints it; tests read it).</summary>
    public bool RecordLog { get; init; }
    /// <summary>
    /// Characters attack on sight, and NPCs give chases up and go back to their routine after a fight (false: the look keeps only the senses, for worlds that script their fights:
    /// the sandbox, the duel, the battle).
    /// </summary>
    public bool AttackOnSight { get; init; } = true;
}

/// <summary>A fight an NPC's combat goal started (<see cref="AggroSystem.GoalFight"/>): who attacks whom, the factions and the distance.</summary>
public readonly record struct AggroEntry(long Tick, CharacterId Attacker, CharacterId Target, int AttackerFaction, int TargetFaction, float Distance);

/// <summary>
/// The senses and the attack-on-sight finders (an <b>engine stand-in</b> for the original's <c>SensoryData</c> and the attack goals' finders, docs/game/perception.md "Senses"):
/// <list type="bullet">
/// <item><b>Look</b> (<c>Act</c>, parallel, own slot, reading the last tick's positions through the <see cref="SpatialGrid"/>): every living character on its turn (one in
/// <see cref="AggroSettings.ScanEveryTicks"/>, by slot) senses who it sees (<see cref="SightMap.Sees"/>: places, light and its range R = <see cref="AggroSettings.SightRange"/> x its race's
/// <c>vision range mult</c> times the view-cone factor of <see cref="Visibility"/>) or hears, and keeps the entries, tags, counts and threats. An NPC with a mind that senses a new enemy emits
/// one effect at it (a replan trigger); a player character with the attacking-enemies job or on Hold picks the best enemy by the target score (<see cref="Pick"/>).</item>
/// <item><b>Decide</b> (<c>Apply</c>, serial commit order): an NPC with a mind, and its squad mates within twice their own range R of the target (the original's squad-shared sightings,
/// <c>GroupSense</c>), are asked to think at the next tick (<see cref="Ai.AiMind.Replan"/>): their combat goals decide in the chooser (<see cref="FindFoe"/>). An NPC without a mind starts no
/// fight on sight. The player's characters look only with the attacking-enemies job (<see cref="HuntsEnemies"/>) or on Hold, and are offered the sighting: the decision step starts their fight
/// (<see cref="HoldAttack"/>, <see cref="AttackJob"/>); they have no leash, and after a job's fight go back to following or stand idle.</item>
/// <item><b>Chase give-up and stand down</b> (<c>SlowWorld</c>, serial, every tick): an NPC running after its target (<see cref="CharacterCold.CombatRunning"/>) senses that
/// target on its look cadence (<c>Act</c>) and gives it up by the original's <c>ChaseMode</c> rule (<see cref="GiveUpGateSeconds"/>, <see cref="GiveUpSenseSeconds"/>,
/// <see cref="GiveUpReach"/>). When its fight is over (target down, or stopped) it goes back to what it did before (wander or follow; a go-to becomes idle, which the roaming
/// squads order again).</item>
/// </list>
/// The live relation table is hashed here once a deed has changed it (<see cref="FactionRelations.Changes"/>).
/// </summary>
public sealed class AggroSystem(FactionRelations relations, AggroSettings? settings = null) : ITickSystem, IStateHashed, IDecisionOwner
{
    /// <summary>The <see cref="Effect.Kind"/> of a sighting: <c>Target</c> is the enemy, <c>Amount</c> the distance.</summary>
    public const int SightEffect = 1;
    /// <summary>The <see cref="Effect.Kind"/> of a hostile looker's exposure of a sneaker: <c>Target</c> is the sneaker, <c>Amount</c> its <see cref="StealthExposure"/>.</summary>
    public const int ExposureEffect = 2;
    /// <summary>The <see cref="Effect.Kind"/> of a sleeper that nearly woke at a sneaking player-side character's noise: <c>Target</c> is the sneaker (event <see cref="AlmostWokeUpEvent"/>).</summary>
    public const int AlmostWokeEffect = 3;
    /// <summary>The <see cref="Effect.Kind"/> of a package's sensing hook tagging a character in the looker's squad memory: <c>Target</c> is the character, <c>Amount</c> the short-term tag (<see cref="Dialogue.MemoryTag"/>).</summary>
    public const int TagEffect = 4;
    /// <summary>The <see cref="Effect.Kind"/> of a hearing check that reached the stealth XP step: <c>Source</c> the listener, <c>Target</c> the heard character, <c>Amount</c> 1 when it was heard (<c>FUN_1408c7330</c>).</summary>
    public const int HearingXpEffect = 5;
    /// <summary>The dialogue event a sleeper says when it nearly wakes at a sneaking player (ALMOST_WOKE_UP, docs/game/dialogue.md).</summary>
    public const int AlmostWokeUpEvent = 47;
    const byte NoTask = 0xFF;

    /// <summary>The original's vision range outdoors before the race multiplier (<c>FUN_140851bc0</c>: race <c>vision range mult</c> x 1500; 500 flat inside a building).</summary>
    public const float OutdoorSight = 1500;
    /// <summary>The edge of the original's view cone: the cosine of the angle off the facing at which the view factor reaches 0 (about 78.5 degrees; <c>FUN_140852f70</c>).</summary>
    public const float ConeEdge = 0.2f;
    /// <summary>The smallest view factor that counts as seen (<c>FUN_140857b10</c>: above 0.01).</summary>
    public const float MinVisibility = 0.01f;

    /// <summary>
    /// The original's sight test for an NPC looking at a character who is not sneaking or crawling, in full daylight with both lit alike (<c>FUN_140852f70</c>): the
    /// view factor v = (cos a - 0.2) / 0.8, a the angle between the looker's facing and the direction to the target (3D), and the target is seen when v is above 0.01
    /// and the distance is at most R x v (v x <paramref name="posture"/>: 0.5 for a target crawling or on the ground, <c>FUN_140852f70</c>). Returns v when seen, else 0. A target at the looker's own position counts as seen straight ahead (an engine choice).
    /// </summary>
    public static float Visibility(Vector3 eye, float yaw, Vector3 target, float range, float posture = 1)
    {
        var d = target - eye;
        float dist = d.Length();
        if (dist < 1e-3f) return 1;
        if (dist > range) return 0;
        float cos = (MathF.Sin(yaw) * d.X + MathF.Cos(yaw) * d.Z) / dist;
        float v = (cos - ConeEdge) / (1 - ConeEdge) * posture;
        return v > MinVisibility && dist <= range * v ? v : 0;
    }

    /// <summary>
    /// The sight test for a sneaking target: the view factor v of <see cref="Visibility"/>'s cone, scaled by <see cref="Orders.SneakView"/>, must reach the distance
    /// (d at most R x v'). Returns v' when it does, else 0; in view, the target still has to fill the looker's spot meter (<see cref="Orders.SpotSeconds"/>).
    /// </summary>
    public static float SneakVisibility(Vector3 eye, float yaw, Vector3 target, float range, float stealth, float posture = 1)
    {
        var d = target - eye;
        float dist = d.Length();
        if (dist < 1e-3f) return 1;
        if (dist > range) return 0;
        float v = Orders.SneakView(((MathF.Sin(yaw) * d.X + MathF.Cos(yaw) * d.Z) / dist - ConeEdge) / (1 - ConeEdge) * posture, stealth);
        return v > MinVisibility && dist <= range * v ? v : 0;
    }

    /// <summary>The weather's factor on the noise of the character in <paramref name="slot"/> (<see cref="WeatherSystem.NoiseFactor"/>; 1 without a cold row).</summary>
    static float Noise(CharacterTable table, int slot) => table.Cold(slot) is { } c ? WeatherSystem.NoiseFactor(c) : 1;

    /// <summary>The noise a moving character makes reaches lerp(speed / max speed, 350, 600) units (<c>FUN_140851d10</c>, Verified constants; clear weather: the caller applies <see cref="WeatherSystem.NoiseFactor"/>; not sneaking).</summary>
    public const float NoiseNear = 350, NoiseFar = 600;
    /// <summary>The chance per check that a character within the noise range is heard when it is not sneaking (<c>FUN_140857500</c>, Verified constant).</summary>
    public const float HearOdds = 0.75f;

    /// <summary>
    /// How far a character at <paramref name="speed"/> (its top speed <paramref name="maxSpeed"/>) is heard when it does not sneak: 350 at rest (a fighter standing still is still
    /// heard), 600 at full speed. Whether a character is heard at all (moving or in combat mode) is the caller's test.
    /// </summary>
    public static float NoiseRange(float speed, float maxSpeed)
    {
        float t = maxSpeed > 0 ? Math.Clamp(speed / maxSpeed, 0, 1) : 1;
        return NoiseNear + (NoiseFar - NoiseNear) * t;
    }

    /// <summary>The original's chance to hear a character at <paramref name="distance"/> with noise range <paramref name="noise"/>: 0.75 x (1 - d^2 / N^2), rolled each check.</summary>
    public static float HearChance(float distance, float noise) => noise > 0 && distance < noise ? HearOdds * (1 - distance * distance / (noise * noise)) : 0;

    /// <summary>Two characters both of the player's side hear each other within this distance, whatever else (<c>FUN_140857500</c> over <c>FUN_140856bc0</c>, Verified constant).</summary>
    public const float PlayerHearRange = 1000;

    /// <summary>Of the player's side (the original's <c>isWithThePlayer</c> <c>FUN_140856bc0</c>): the player's faction. Engine: its owner path (a slave's or pet's) is not modelled.</summary>
    static bool PlayerSide(World world, CharacterCold? c) => c is not null && (c.IsPlayer || world.Player.Faction >= 0 && c.Faction == world.Player.Faction);

    /// <summary>
    /// Whether <paramref name="listener"/> hears <paramref name="target"/> this look (<c>SensoryData::canHear</c> <c>FUN_140857500</c>, docs/game/perception.md "Hearing"), asked only
    /// when sight failed. Both of the player's side: heard within <see cref="PlayerHearRange"/>, nothing else. Else a target neither moving nor in combat mode is not heard; the
    /// building rules (<see cref="HearPlace"/>) scale the noise range, halved for an awake listener (the original's StateBroadcast <c>+0xb4</c> 0; a sleeper,
    /// <see cref="Carrying.CarrySystem.Asleep"/>, hears at full range); a sneaking target makes the sneaking noise, any other the ordinary one (350 at rest); in combat mode in the
    /// listener's place and floor any chance is a sure hearing, else <paramref name="roll"/> (the look's hash) is rolled against it, and a sleeper drops what it heard on a coin
    /// flip, then saying <see cref="AlmostWokeUpEvent"/> when the target is a sneaking player-side character (emitted as <see cref="AlmostWokeEffect"/>). After the building
    /// rules a pair checked less than <see cref="HearCooldownSeconds"/> ago gets the last answer (<paramref name="heard"/>, the listener's own list), and an alarmed pair doubles
    /// the chance. <paramref name="stealth"/>: the target's stealth when it sneaks, else below 0.
    /// </summary>
    static bool Hears(World world, CharacterTable table, int listener, int target, float stealth, ulong roll, EffectBuffer effects, ref List<HeardPair>? heard)
    {
        ref readonly var me = ref table.Previous[listener];
        ref readonly var other = ref table.Previous[target];
        var tc = table.Cold(target);
        float d2 = Vector3.DistanceSquared(me.Position, other.Position);
        if (PlayerSide(world, table.Cold(listener)) && PlayerSide(world, tc)) return d2 < PlayerHearRange * PlayerHearRange;
        float speed = new Vector2(other.Velocity.X, other.Velocity.Z).Length();
        bool combat = tc is { InCombat: true };
        if (!(speed > 0.01f) && !combat) return false;
        bool sure = false;
        float place = tc is null ? 1 : HearPlace(world, listener, target, me.Position, other.Position, combat, out sure);
        if (!(place > 0)) return false;
        int k = FindHeard(heard, target, other.Generation);
        var pair = k >= 0 ? heard![k] : new HeardPair { Target = target, Generation = other.Generation, Checked = -1 };
        if (pair.Checked >= 0 && world.Tick - pair.Checked < CooldownTicks(world)) return pair.Heard;
        pair.Checked = world.Tick;
        // The XP step of the check (FUN_1408c7330) is reached by a mover that is not in combat mode, or one staying low or crippled (posture 1 or 2, the heard-ability gate FUN_1406643d0).
        bool xpGate = tc is not null && (speed > 0.01f && !combat || tc.Medical is { Prone: true });
        pair.Heard = Roll(world, table, listener, target, stealth, roll, effects, d2, speed, place, sure, world.Tick < pair.AlarmUntil, xpGate);
        if (k >= 0) heard![k] = pair;
        else AddHeard(ref heard, pair);
        return pair.Heard;
    }

    /// <summary>
    /// The noise range and the roll of <see cref="Hears"/>, past its gates and cooldown. Every roll that gets to the end of the original's check (a sure hearing returns before) with
    /// <paramref name="xpGate"/> set emits a <see cref="HearingXpEffect"/> (<c>FUN_1408c7330</c>, <see cref="Apply"/>).
    /// </summary>
    static bool Roll(World world, CharacterTable table, int listener, int target, float stealth, ulong roll, EffectBuffer effects, float d2, float speed, float place, bool sure, bool alarmed, bool xpGate)
    {
        bool Rolled(bool heard)
        {
            if (xpGate) effects.Emit(listener, target, HearingXpEffect, heard ? 1 : 0);
            return heard;
        }
        ref readonly var other = ref table.Previous[target];
        bool asleep = Carrying.CarrySystem.Asleep(table, listener);
        if (!asleep) place *= AwakeHearing;
        float d = MathF.Sqrt(d2);
        float p = stealth >= 0
            ? Orders.SneakHearChance(d, Orders.SneakNoise(stealth, Noise(table, target)) * place, stealth)
            : HearChance(d, NoiseRange(speed, other.MaxSpeed) * Noise(table, target) * place);
        if (alarmed) p *= AlarmHearing;
        if (!(p > 0)) return Rolled(false);
        if (sure) return true;
        if (Rng.Float(roll) >= p) return Rolled(false);
        if (!asleep || Rng.Float(Rng.Mix(roll + 1)) >= 0.5f) return Rolled(true);
        if (stealth >= 0 && PlayerSide(world, table.Cold(target))) effects.Emit(listener, target, AlmostWokeEffect, 0);
        return Rolled(false);
    }

    /// <summary>A looker's faction attacks a freed target on sight for the relations only below this default relation (<c>FUN_14079c950</c>: strictly below -30, Verified constant).</summary>
    public const int FreedHostility = -30;

    /// <summary>The factor on the noise range an awake listener hears (<c>FUN_140857500</c>: x0.5 while StateBroadcast <c>+0xb4</c> is 0, Verified constant).</summary>
    public const float AwakeHearing = 0.5f;

    /// <summary>
    /// How long a listener keeps its last answer on a target before it checks again (<c>FUN_140857500</c>'s per-target time map: 2 s of 1x game time divided by the game speed).
    /// Engine: 2 s of simulated time, the speed not applied.
    /// </summary>
    public const float HearCooldownSeconds = 2;
    /// <summary>How long a pair stays alarmed (<c>FUN_140858f40</c> sets the sense entry's timer to 5 s, Verified) and the factor on the hearing chance meanwhile (x2, Verified).</summary>
    public const float AlarmSeconds = 5, AlarmHearing = 2;

    static long CooldownTicks(World world) => (long)MathF.Round(HearCooldownSeconds / world.TickSeconds);

    /// <summary>A listener's hearing memory of one target: the tick of its last full check (-1: none), its answer, and the tick its alarm runs out.</summary>
    struct HeardPair
    {
        public int Target, Generation;
        public long Checked, AlarmUntil;
        public bool Heard;
    }

    /// <summary>Drops the pairs whose target is gone or that neither hold an answer within the cooldown nor an alarm (null when none is left).</summary>
    static List<HeardPair>? PruneHeard(List<HeardPair> heard, World world)
    {
        long cooldown = CooldownTicks(world);
        var prev = world.Characters.Previous;
        for (int k = heard.Count - 1; k >= 0; k--)
        {
            var x = heard[k];
            if (!prev[x.Target].Alive || prev[x.Target].Generation != x.Generation || (x.Checked < 0 || world.Tick - x.Checked >= cooldown) && world.Tick >= x.AlarmUntil) heard.RemoveAt(k);
        }
        return heard.Count > 0 ? heard : null;
    }

    /// <summary>The pair of <paramref name="target"/> (its generation) in the listener's list, kept in target slot order; -1 when none.</summary>
    static int FindHeard(List<HeardPair>? heard, int target, int generation)
    {
        if (heard is null) return -1;
        int k = HeardBound(heard, target);
        return k < heard.Count && heard[k].Target == target && heard[k].Generation == generation ? k : -1;
    }

    static int HeardBound(List<HeardPair> heard, int target)
    {
        int lo = 0, hi = heard.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (heard[mid].Target < target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Puts a new pair in slot order, over a stale one of the same slot.</summary>
    static void AddHeard(ref List<HeardPair>? heard, in HeardPair pair)
    {
        heard ??= [];
        int k = HeardBound(heard, pair.Target);
        if (k < heard.Count && heard[k].Target == pair.Target) heard[k] = pair;
        else heard.Insert(k, pair);
    }

    /// <summary>
    /// Alarms <paramref name="listener"/> about <paramref name="target"/> for <see cref="AlarmSeconds"/> (<c>FUN_140858f40</c> with its alarm flag, which the perceive-and-tag step
    /// <c>FUN_1405cb0a0</c> of a massive alarm passes): it hears that target twice as easily meanwhile. Serial phases only.
    /// </summary>
    public void Alarm(World world, CharacterId listener, CharacterId target)
    {
        var prev = world.Characters.Previous;
        if (listener.Slot >= slots.Length || slots[listener.Slot].Generation != listener.Generation || target.Slot >= world.Characters.HighWater || prev[target.Slot].Generation != target.Generation) return;
        ref var heard = ref slots[listener.Slot].Heard;
        long until = world.Tick + (long)MathF.Round(AlarmSeconds / world.TickSeconds);
        int k = FindHeard(heard, target.Slot, target.Generation);
        if (k >= 0) heard![k] = heard[k] with { AlarmUntil = until };
        else AddHeard(ref heard, new HeardPair { Target = target.Slot, Generation = target.Generation, Checked = -1, AlarmUntil = until });
        Notice(world, listener, target);
    }

    /// <summary>
    /// The forced sense (<c>FUN_140858f40</c>, docs/game/perception.md "Spotting a sneaker"): <paramref name="looker"/> has been told of <paramref name="target"/> (a massive alarm, a
    /// blow or bolt that landed on it, a dialogue attack) and goes on its watcher list as seen, whether or not it sees it. Only a player-side target keeps the list (the arrows and the
    /// sneak XP read it). Serial phases only.
    /// </summary>
    public void Notice(World world, CharacterId looker, CharacterId target)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(target, out int t) || !table.TryResolveNext(looker, out int l) || table.Cold(t) is not { } sneaker || table.Cold(l) is not { } watcher) return;
        Force(world, l, t);
        if (watcher.Faction == sneaker.Faction || !sneaker.IsPlayer && !(world.Player.Faction >= 0 && sneaker.Faction == world.Player.Faction)) return;
        Watch(world, sneaker, looker, StealthExposure.Detected);
    }

    /// <summary>
    /// The building rules of the hearing check (<c>SensoryData::canHear</c> <c>FUN_140857500</c> steps 3, 4, 6 and 8, <b>Verified</b>): the factor on the target's noise range, 0 when it
    /// cannot be heard. Inside another building than the listener, or on another floor of the same one, it is heard only in combat mode; from one building into another never; across
    /// floors at most one apart. Another interior halves the range, and again when the building (the target's, else the listener's) has no open door. <paramref name="sure"/>: in combat
    /// mode in the listener's place and on its floor, heard whenever the chance is above 0. Engine: the floor test skips the original's cavernous-building exception.
    /// </summary>
    static float HearPlace(World world, int listener, int target, Vector3 listenerAt, Vector3 targetAt, bool combat, out bool sure)
    {
        sure = false;
        var sight = world.Sight;
        int a = sight.PlaceOf(listener), b = sight.PlaceOf(target);
        if (a != b)
        {
            if (!combat || a >= 0 && b >= 0) return 0;
            return sight.BuildingOf(b >= 0 ? target : listener) is { } building && !world.Fixtures.HasOpenDoor(building) ? 0.25f : 0.5f;
        }
        if (a >= 0 && sight.BuildingOf(listener) is { } same && sight.FloorAt(same, listenerAt) is var fa && sight.FloorAt(same, targetAt) is var fb && fa != fb)
            return combat && Math.Abs(fa - fb) <= 1 ? 1 : 0;
        sure = combat;
        return 1;
    }

    /// <summary>A character's daylight sight range R: <paramref name="sight"/> x its race's <c>vision range mult</c> x the weather's factor (<see cref="WeatherSystem.SightFactor"/>).</summary>
    public static float RangeOf(CharacterCold cold, float sight) => sight * (cold.Race?.VisionRangeMult ?? 1) * WeatherSystem.SightFactor(cold);

    struct Slot
    {
        public int Generation;
        public bool Tracked;
        public byte Task;
        /// <summary>The tick the running chase began (-1: none).</summary>
        public long ChaseStart;
        /// <summary>The character's senses (null until its first look or forced sense); written by its own partition only, or by <see cref="Force"/> in a serial phase.</summary>
        public SenseBlock? Senses;
        /// <summary>The looker's spot meters on sneaking characters, in target slot order (null: none); written by its own partition only.</summary>
        public List<Spot>? Spots;
        /// <summary>The listener's hearing cooldowns and alarms, in target slot order (null: none); written by its own partition only, or by <see cref="Alarm"/> in a serial phase.</summary>
        public List<HeardPair>? Heard;
    }

    /// <summary>
    /// One spot meter (the original's <c>SensoryData::SpottingPeopleMgr::Spot</c>): it fills by dt / <see cref="Orders.SpotSeconds"/> while the looker sees the sneaking target,
    /// empties by dt / <see cref="Orders.SpotForgetSeconds"/> while it does not, and is dropped at 0. Above 1 the target counts as seen.
    /// </summary>
    struct Spot
    {
        public int Target, Generation;
        public float Value;
    }

    /// <summary>A sneaking target the looker sees this scan, with its spot time.</summary>
    readonly record struct Sighting(int Target, int Generation, float Seconds);

    [ThreadStatic] static List<Sighting>? sightings;

    Slot[] slots = [];
    int size;
    readonly List<AggroEntry> log = [];
    readonly HashSet<int> taken = [];
    long takenTick = -1;
    /// <summary>
    /// This tick's targets of the player characters whose look (or a squad mate's) found an enemy, for row 16 Attacking enemies: Hold's goal (<see cref="HoldAttack"/>) and the job
    /// (<see cref="AttackJob"/>).
    /// </summary>
    readonly Dictionary<int, CharacterId> offers = [];
    long offersTick = -1;

    public void RegisterSources(DecisionSystem decisions)
    {
        decisions.Register(new HoldAttack(this));
        decisions.RegisterJob(Meitou.Data.Gameplay.Ai.TaskType.ATTACK_ENEMIES, new AttackJob(this));
    }

    /// <summary>The enemy this tick's look offered the player character for row 16 (<see cref="offers"/>), -1 for none.</summary>
    int Offered(World world, int slot) =>
        offersTick == world.Tick && offers.TryGetValue(slot, out var id) && world.Characters.TryResolveNext(id, out int t) && world.Characters.Cold(t) is { Medical.Incapacitated: false } ? t : -1;

    /// <summary>The senses of the character in <paramref name="slot"/> as of its last look (null: none yet). Read after this system's <c>Act</c>, or by a system whose <c>Act</c> runs before it.</summary>
    public SenseBlock? SensesOf(int slot) =>
        (uint)slot < (uint)slots.Length && slots[slot].Senses is { } b && slots[slot].Generation >= 0 ? b : null;

    /// <summary>
    /// The job 16 Attacking enemies (Shift with an attack; it runs with the jobs switch off, docs/game/ai-tasks.md "Perma-jobs") as the decision step's contributor (step 5): its finder is the
    /// original's attack on sight (<c>FUN_14059f3a0</c>, "Combat goals"), which this system's look stands in for: the enemy the look (or a squad mate's) offered this tick is attacked
    /// (row 16, the job's provenance), with the row's desire (0 while Passive, sneaking outside a fight or not standing, <see cref="WantsToAttack"/>). The fight's end asks a decision.
    /// </summary>
    sealed class AttackJob(AggroSystem owner) : IJobSource
    {
        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            if (cold.Running is { Source: TaskSource.Job, Row: Meitou.Data.Gameplay.Ai.TaskType.ATTACK_ENEMIES } run && run.Job == k && run.Runs((CharacterTask)table.Next[slot].Task)) return DecisionCheck.Runs;
            if (!WantsToAttack(cold)) return DecisionCheck.No("desire 0 (Passive, sneaking or not standing)");
            return owner.Offered(world, slot) >= 0 ? DecisionCheck.Run : DecisionCheck.No("no enemy sighted");
        }

        public void Start(World world, int slot, int k)
        {
            var table = world.Characters;
            int t = owner.Offered(world, slot);
            if (t < 0) return;
            var targetId = table.IdOf(t);
            world.Defer(new AttackOrder([table.IdOf(slot)], targetId) { Tick = world.Tick + 1, Source = TaskSource.Job, Job = k });
            owner.Started++;
            world.Sounds?.Enqueue(new SoundEvent(SoundKind.Alert, table.IdOf(slot), targetId, table.Next[t].Position));   // its alert voice
        }
    }

    /// <summary>
    /// Goal 16 Attacking enemies as Hold adds it to a player character (priority 3; its weight is <b>Unknown</b>, 1 here; desire 1, 0 as <see cref="HoldsGround"/> says): the enemy this tick's
    /// look found (<see cref="Apply"/>) is attacked. It runs while an attack on sight (row 16) of a goal is its fight.
    /// </summary>
    sealed class HoldAttack(AggroSystem owner) : IDecisionSource
    {
        public string Name => "16 Attack on sight (Hold)";
        public DecisionStep Step => DecisionStep.Goals;
        /// <summary>After the goals <c>setupAI</c> lists: Hold adds it when it is turned on.</summary>
        public int Key => 1000;

        public DecisionCheck CanRun(World world, int slot)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var task = (CharacterTask)table.Next[slot].Task;
            if (task == CharacterTask.Attack && cold.Running is { Row: Meitou.Data.Gameplay.Ai.TaskType.ATTACK_ENEMIES, Source: TaskSource.Goal } run && run.Runs(task)) return DecisionCheck.Runs;
            if (!Orders.Holding(cold)) return DecisionCheck.No("not on Hold");
            if (!HoldsGround(cold)) return DecisionCheck.No("desire 0 (Passive, sneaking or not standing)");
            return owner.Offered(world, slot) >= 0 ? DecisionCheck.Run : DecisionCheck.No("no enemy sighted");
        }

        public void Start(World world, int slot)
        {
            var table = world.Characters;
            int t = owner.Offered(world, slot);
            if (t < 0) return;
            var targetId = table.IdOf(t);
            world.Defer(new AttackOrder([table.IdOf(slot)], targetId) { Tick = world.Tick + 1 });
            owner.Started++;
            world.Sounds?.Enqueue(new SoundEvent(SoundKind.Alert, table.IdOf(slot), targetId, table.Next[t].Position));   // its alert voice
        }
    }

    public AggroSettings Settings { get; } = settings ?? new AggroSettings();
    public FactionRelations Relations { get; } = relations;
    /// <summary>The fights started since the last <see cref="TakeLog"/> (with <see cref="AggroSettings.RecordLog"/>).</summary>
    public IReadOnlyList<AggroEntry> Log => log;

    /// <summary>Takes the log (the host, between ticks).</summary>
    public AggroEntry[] TakeLog()
    {
        AggroEntry[] all = [.. log];
        log.Clear();
        return all;
    }

    /// <summary>The fights this system started so far.</summary>
    public long Started { get; private set; }

    /// <summary>The fights: the target score and the chase give-up (null: none; the look's goal finders then read the world's combat system).</summary>
    public CombatSystem? Combat { get; set; }

    /// <summary>The towns, for 144's home circle (<see cref="FoeFinder.Territory"/>; null: no circle, it attacks on sight).</summary>
    public PopulationData? Towns { get; set; }

    /// <summary>The nests' squads, for a nest's 144 circle (<see cref="FoeFinder.Territory"/>; null: none).</summary>
    public PopulationSystem? Population { get; set; }

    /// <summary>
    /// The territorial circle (<c>FUN_140926d50</c> from <c>FUN_14059fd70</c>, <b>Verified</b>): the squad's home town's point (a nest's: its centre, <see cref="Squad.HomeCentre"/>) and
    /// (1.3 x its radius)²; -1 for a nest marker or a zero-radius town, which contains nobody; 0 without a home town (no circle).
    /// </summary>
    (Vector2 Centre, float R2) Territory(CharacterCold cold)
    {
        if (cold.Ai?.Squad is not { } squad) return (default, 0);
        static float R2(TownData town) => TerritoryScale * town.Radius is var r && town.Type != TownType.NestMarker && r > 0 ? r * r : -1;
        if (squad.Ai is { Town: >= 0 } board && Towns is { } towns && board.Town < towns.Sites.Count)
        {
            var site = towns.Sites[board.Town];
            return (new Vector2(site.Position.X, site.Position.Z), R2(site.Town));
        }
        return Population?.NestOf(squad.Id) is { } nest ? (squad.HomeCentre, R2(nest)) : (default, 0);
    }

    const float TerritoryScale = 1.3f;

    /// <summary>
    /// The squads' memories (the dialogue system's): a squad's short-term TEMP_ENEMY tag on a character of another faction makes it an enemy of every member whatever the relations say, and
    /// TEMP_ALLY unmakes an enemy (<c>FUN_14079c950</c>, docs/game/dialogue.md "Squad memory"). Null: relations only.
    /// </summary>
    public Dialogue.SquadMemory? Memory { get; set; }

    public void Schedule(World world) => Sync(world.Characters);

    void Sync(CharacterTable table)
    {
        int n = table.HighWater;
        if (slots.Length < n)
        {
            int old = slots.Length;
            Array.Resize(ref slots, Math.Max(n, slots.Length * 2));
            for (int i = old; i < slots.Length; i++) slots[i] = new Slot { Generation = -1, Task = NoTask, ChaseStart = -1 };
        }
        size = Math.Max(size, n);
        var next = table.Next;
        if (present.Length != Relations.Count) { present = new bool[Relations.Count]; hasEnemy = new bool[Relations.Count]; }
        Array.Clear(present);
        sneakers.Clear();
        for (int i = 0; i < n; i++)
        {
            int generation = next[i].Alive ? next[i].Generation : -1;
            if (slots[i].Generation != generation) slots[i] = new Slot { Generation = generation, Task = NoTask, ChaseStart = -1 };
            if (generation >= 0 && table.Cold(i) is { Medical: not null } c && Relations.Contains(c.Faction))
            {
                present[c.Faction] = true;
                if ((c.Orders & CharacterOrders.Sneak) != 0 && !c.Medical.Incapacitated) sneakers.Add(next[i].Position);
            }
        }
        // Which factions have an enemy among the factions present: a peaceful crowd skips its looks. Worked out again only when the set changes.
        if (present.AsSpan().SequenceEqual(presentBefore) && Relations.Changes == changesBefore) return;
        presentBefore = (bool[])present.Clone();
        changesBefore = Relations.Changes;
        presentList.Clear();
        for (int f = 0; f < present.Length; f++) if (present[f]) presentList.Add(f);
        foreach (int f in presentList)
        {
            hasEnemy[f] = false;
            foreach (int g in presentList)
                if (AttacksOnSight(Relations, f, g)) { hasEnemy[f] = true; break; }
        }
    }

    /// <summary>Where the awake sneaking characters stand this tick (read only during the looks): a looker that picks no fights looks only when one is in its range.</summary>
    readonly List<Vector3> sneakers = [];

    bool SneakerWithin(Vector3 eye, float range)
    {
        foreach (var p in sneakers) if (Vector3.DistanceSquared(p, eye) <= range * range) return true;
        return false;
    }

    /// <summary>An NPC that keeps spot meters without picking fights: awake, not fighting, not held (carried, caged, in bed or asleep), not attacking or treating.</summary>
    static bool Watches(CharacterCold c, byte task) =>
        c is { IsPlayer: false, InCombat: false, IsImprisoned: false, Medical.Incapacitated: false } && c.CarriedBy.IsNone && c.InSomething != Locks.HeldState.Bed
        && c.Ai is not { Behaviour: Ai.AiBehaviour.Sleep } && task is not ((byte)CharacterTask.Attack or (byte)CharacterTask.Treat);

    bool[] present = [], presentBefore = [], hasEnemy = [];
    long changesBefore = -1;
    readonly List<int> presentList = [];

    // ------------------------------------------------------------------ look

    struct Scan
    {
        public CharacterTable Table;
        public FactionRelations Relations;
        public World World;
        public Dialogue.SquadMemory? Memory;
        public int Self, Faction, Squad;
        public Vector3 Eye;
        public CharacterCold? Cold;
        public float Range;
        /// <summary>The looker's outdoor sight (race multiplier applied) for the places and lights (<see cref="SightMap.Sees"/>); <see cref="Range"/> is the farthest it can see anyone.</summary>
        public float Outdoor;
        public SightMap Sight;
        public ulong Seed, Key, Tick;
        /// <summary>A campaign squad's assault targets (<see cref="Squad.War"/>; none for others).</summary>
        public WarTargets War;
        /// <summary>The towns, for the assault's town test (<see cref="AggroSystem.Towns"/>; null: nobody is in a town).</summary>
        public PopulationData? Towns;
        /// <summary>The faction a hired squad works for (<c>Contract_HiredAlly</c> 12, its employer's) or defends (<c>Contract_DefendPlayerBase</c> 15, the hirer's); -1: none. Its members are never enemies, nor, hired, its allies.</summary>
        public int Employer;
        public bool EmployerAllies;
        /// <summary>The territorial circle (R2 0: none, -1: contains nobody).</summary>
        public (Vector2 Centre, float R2) Home;
        public int Best;
        public float BestD2;
        /// <summary>The best target's score (<see cref="Combat.TargetScore"/>).</summary>
        public float BestScore;
        /// <summary>The combat state the score reads (null: hand-built worlds without one, every candidate counts as unfought).</summary>
        public Combat.CombatSystem? Combat;
        /// <summary>The fights the senses read (threats, who fights whom): <see cref="Combat"/>, else the world's combat system.</summary>
        public Combat.CombatSystem? Fights;
        /// <summary>The looker: one of the player's, on Hold (and its hold point), crawling, an animal.</summary>
        public bool Player, Hold, HoldPost, Prone, Animal;
        public Vector2 HoldPoint;
        /// <summary>The looker's spot meters (read only during the look) and the sneakers it sees this look.</summary>
        public List<Spot>? Spots;
        /// <summary>The looker's hearing memory (<see cref="Slot.Heard"/>), written during the look and stored back after it.</summary>
        public List<HeardPair>? Heard;
        public List<Sighting>? Sightings;
        /// <summary>The look's effects (null for a forced sense: no roll, meter or hook).</summary>
        public EffectBuffer? Effects;
        /// <summary>The looker keeps spot meters this look (it may pick fights, or it is an NPC watching for sneakers): sneakers in view fill them and are exposed.</summary>
        public bool Meters;
        /// <summary>The looker's senses, written by this look.</summary>
        public SenseBlock Block;
        /// <summary>The looker's own fight target (-1: none).</summary>
        public int FightTarget;
        /// <summary>New characters gathered this look (<see cref="GatherCap"/>, <see cref="GatherExtra"/>).</summary>
        public int Gathered;
        /// <summary>This look also gathers beyond <see cref="GatherNear"/> (<see cref="FarLooks"/>), and the gather is in that far pass.</summary>
        public bool Far, FarPass;
        /// <summary>This look works the entries' tags out in full (every <see cref="FarLooks"/>-th look and a forced sense; the fights and the KO and imprisoned bits are read at every look).</summary>
        public bool FullTags;
        /// <summary>Which attack goal's finder takes a candidate (<see cref="FindFoe"/>; the look's attack on sight is 16's).</summary>
        public FoeFinder Finder;
        /// <summary>Fights started this tick by target, counted as attackers in the score (<see cref="FindFoe"/> from the AI's serial step; null: none).</summary>
        public IReadOnlyDictionary<int, int>? Pending;
        /// <summary>An enemy-tagged character the look gathered as a new entry (-1: none), the replan trigger of a fighter the AI runs.</summary>
        public int NewEnemy;
    }

    /// <summary>The attack goals' finders <see cref="FindFoe"/> runs for the AI's chooser (docs/game/ai-tasks.md "Combat goals").</summary>
    public enum FoeFinder : byte
    {
        /// <summary>16 ATTACK_ENEMIES (<c>FUN_14059f3a0</c>): an enemy on sight (also the player's attack on sight, Hold's goal and the job).</summary>
        Enemies = 0,
        /// <summary>21 ATTACK_ENEMIES_AND_NEUTRALS (<c>FUN_14059f720</c>): an enemy or a neutral on sight.</summary>
        EnemiesAndNeutrals = 1,
        /// <summary>144 TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME (<c>FUN_14059fd70</c>): an enemy in the home circle or the looker's building.</summary>
        Territory = 2,
        /// <summary>34 ATTACK_TROUBLE_MAKERS (<c>FUN_14099baa0</c>): an enemy attacking an ally in the looker's town.</summary>
        TroubleMakers = 3,
    }

    /// <summary>
    /// The gather's cap (engine choice; the original's <c>FUN_14085a8d0</c> has none, <b>Verified</b>): up to <see cref="GatherCap"/> + <see cref="GatherExtra"/> new characters within
    /// <see cref="GatherNear"/>, then, while fewer than <see cref="GatherCap"/> were taken, more within the look's reach.
    /// </summary>
    public const int GatherCap = 40, GatherExtra = 20;
    public const float GatherNear = 250;
    /// <summary>
    /// A looker that neither may pick a fight nor fights gathers beyond <see cref="GatherNear"/> only every this many looks, and works its entries' tags out in full as often (engine
    /// choice, for the cost of the look in crowds: its entries are sensed at every look, only new far characters and relation changes wait).
    /// </summary>
    public const int FarLooks = 4;

    /// <summary>
    /// The look of every living character on its turn (one in <see cref="AggroSettings.ScanEveryTicks"/>, by slot): its senses (<see cref="Look"/>), then, for one that may pick a fight, the
    /// attack on sight (<see cref="Pick"/>), emitted as a <see cref="SightEffect"/>. Writes only the looker's own slot.
    /// </summary>
    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        int every = Math.Max(1, Settings.ScanEveryTicks);
        int phase = (int)(world.Tick % every);
        int first = part.Start + ((phase - part.Start % every) % every + every) % every;
        for (int i = first; i < part.End; i += every)
        {
            if (!prev[i].Alive || i >= slots.Length) continue;
            if (slots[i].Heard is { } heard) slots[i].Heard = PruneHeard(heard, world);
            if (table.Cold(i) is not { Medical: not null } cold) continue;
            byte task = next[i].Task;
            var war = cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { War.IsSet: true } assaulting ? assaulting.War : WarTargets.None;
            // One that could pick a fight now: free, able, of a faction with an enemy about (or at war, or with a TEMP_ENEMY tag of its squad).
            bool looks = Settings.AttackOnSight && !Busy(cold, task)
                && cold is { InCombat: false, Fleeing: false, IsImprisoned: false, SlaveState: not 1, Fighter.CanAttack: true, Medical.Incapacitated: false }
                && Relations.Contains(cold.Faction) && (hasEnemy[cold.Faction] || war.IsSet || (Memory?.AnyTempEnemy(cold.SquadId) ?? false));
            // The player's characters attack on sight with the job or Hold's goal (offered to the decision step); an NPC decides by its own goals in the chooser.
            bool targets = looks && cold.IsPlayer && (HuntsEnemies(cold) || HoldsGround(cold));
            var scan = NewScan(world, i, cold, war, MathF.Max(0, RangeOf(cold, Settings.SightRange)), effects);
            // Every awake NPC keeps spot meters on the sneakers it sees, not only those it would fight (the original's per-looker SpottingPeopleMgr): they feed the crime
            // witnesses, the stealth knock-out and the sneaker's arrows.
            bool picks = targets || looks && cold.Ai is not null;
            scan.Meters = sneakers.Count > 0 && (picks || Watches(cold, task) && SneakerWithin(prev[i].Position, scan.Range));
            scan.Sightings!.Clear();
            scan.FullTags = (world.Tick / every + i) % FarLooks == 0;
            scan.Far = picks || cold.InCombat || scan.FullTags;
            Look(ref scan);
            slots[i].Heard = scan.Heard;
            if (targets)
            {
                Pick(ref scan);
                if (scan.Best >= 0) effects.Emit(i, scan.Best, SightEffect, MathF.Sqrt(scan.BestD2));
            }
            // An NPC with a mind that senses a new enemy thinks again, and its squad mates with it (the sighting is shared): a replan trigger (engine stand-in), whatever its goals; a
            // fighter only in its own fight (in another, the AI weighs nothing but the run).
            else if (scan.NewEnemy >= 0 && cold.Ai is not null && (cold.InCombat ? Ai.AiSystem.Holds(next[i], cold) : looks))
                effects.Emit(i, scan.NewEnemy, SightEffect, Vector3.Distance(prev[i].Position, prev[scan.NewEnemy].Position));
            if (scan.Meters && (scan.Spots is not null || scan.Sightings!.Count > 0)) slots[i].Spots = AdvanceSpots(scan.Spots, scan.Sightings!, every * world.TickSeconds, table);
        }
    }

    Scan NewScan(World world, int i, CharacterCold cold, WarTargets war, float outdoor, EffectBuffer? effects, bool keepSenses = true, FoeFinder finder = FoeFinder.Enemies)
    {
        var prev = world.Characters.Previous;
        return new Scan
        {
            Table = world.Characters, Relations = Relations, World = world, Memory = Memory, Self = i, Faction = cold.Faction, Squad = cold.SquadId,
            Eye = prev[i].Position, Cold = cold, Range = world.Sight.MaxRange(i, outdoor), Outdoor = outdoor, Sight = world.Sight, Best = -1, BestD2 = float.MaxValue, NewEnemy = -1,
            BestScore = float.NegativeInfinity, Combat = Combat, Fights = Combat ?? world.SystemOf<Combat.CombatSystem>(), Player = cold.IsPlayer, Hold = Orders.Holding(cold) && cold.Fighter?.Crossbow is null,
            HoldPost = Orders.Holding(cold) && !Orders.RangedExempt(cold), Prone = cold.Medical?.Prone == true, Animal = cold.Fighter?.IsAnimal == true, HoldPoint = Orders.HoldPointOf(cold, prev[i].Position),
            Seed = world.Seed, Key = Rng.Key(new CharacterId(i, prev[i].Generation)), Tick = (ulong)world.Tick,
            Finder = finder, War = war, Towns = Towns,
            Employer = EmployerOf(world, cold, out bool allies), EmployerAllies = allies,
            Home = finder == FoeFinder.Territory ? Territory(cold) : default,
            Spots = slots[i].Spots, Heard = slots[i].Heard, Sightings = effects is null ? null : sightings ??= [], Effects = effects,
            Block = keepSenses ? slots[i].Senses ??= new SenseBlock() : slots[i].Senses ?? new SenseBlock(),
            FightTarget = cold.InCombat && (Combat ?? world.SystemOf<Combat.CombatSystem>()) is { } combat ? combat.StateOf(new CharacterId(i, prev[i].Generation)).TargetSlot : -1,
        };
    }

    /// <summary>
    /// The sense pass (<c>SensoryData</c> update <c>FUN_14085a070</c>, <b>Verified</b>; docs/game/perception.md "Senses"): the counts and the threats start again; every entry is sensed again
    /// (<see cref="SenseOne"/>) and refreshed (<see cref="Refresh"/>) or decayed (<see cref="Decay"/>), and counted (<see cref="Count"/>); then the characters gathered within the look's reach
    /// that have no entry are sensed and kept when sensed (<see cref="Visit"/>).
    /// </summary>
    static void Look(ref Scan s)
    {
        var b = s.Block;
        b.threats.Clear();
        b.Enemies = b.Neutrals = b.Allies = b.AlliesDown = 0;
        b.ThreatToMe = b.ThreatToAllies = 0;
        var table = s.Table;
        var prev = table.Previous;
        var list = b.entries;
        int w = 0;
        for (int k = 0; k < list.Count; k++)
        {
            var e = list[k];
            int t = e.Target;
            if ((uint)t >= (uint)table.HighWater || !prev[t].Alive || prev[t].Generation != e.Generation || table.Cold(t) is not { } c) continue;
            bool keep = true;
            if (SenseOne(ref s, t, c, out bool seen, out bool heard)) Refresh(ref s, ref e, t, c, seen, heard);
            else keep = Decay(ref s, ref e, t, c);
            Count(ref s, e, c);
            if (keep) list[w++] = e;
        }
        list.RemoveRange(w, list.Count - w);
        s.Gathered = 0;
        s.FarPass = false;
        s.World.Grid.Query(s.Eye.X, s.Eye.Z, GatherNear, ref s, Visit);
        if (!s.Far || s.Gathered >= GatherCap) return;
        s.FarPass = true;
        s.World.Grid.Query(s.Eye.X, s.Eye.Z, MathF.Max(s.Range, NoiseFar), ref s, Visit);
    }

    /// <summary>
    /// The forced sense (<c>FUN_140858f40</c>, docs/game/perception.md "Senses"): <paramref name="looker"/> is told of <paramref name="target"/> (<c>AI::underMeleeAttack</c>, a
    /// massive alarm): a new entry is made as seen (tags, threats and counts as at a look), an existing one is marked seen. An existing entry's threat is also taken at once when the target's
    /// fight targets the looker (<b>engine</b>: the original's AI update runs its sense pass right before the chooser, <c>FUN_1405112b0</c>, so the replan an attack asks for reads a current
    /// threats list; Meitou's look runs once in <see cref="AggroSettings.ScanEveryTicks"/>). Serial phases only.
    /// </summary>
    public void Force(World world, int looker, int target)
    {
        var table = world.Characters;
        var prev = table.Previous;
        if (looker == target || (uint)looker >= (uint)slots.Length || (uint)target >= (uint)table.HighWater || slots[looker].Generation < 0 || !prev[looker].Alive || !prev[target].Alive
            || table.Cold(looker) is not { Medical: not null } cold || table.Cold(target) is not { } tc) return;
        var b = slots[looker].Senses ??= new SenseBlock();
        int gen = prev[target].Generation, k = b.IndexOf(target, gen);
        if (k >= 0)
        {
            var e = b.entries[k];
            e.Seen = true;
            b.entries[k] = e;
            var id = new CharacterId(target, gen);
            if (!b.threats.Contains(id) && tc.Faction != cold.Faction && tc.InCombat && tc.Medical is { Incapacitated: false } && (Combat ?? world.SystemOf<Combat.CombatSystem>()) is { } fights
                && fights.TargetOf(target, gen) == (looker, prev[looker].Generation))
            {
                b.threats.Add(id);
                b.ThreatToMe += 1;
            }
            return;
        }
        var war = cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { War.IsSet: true } assaulting ? assaulting.War : WarTargets.None;
        var scan = NewScan(world, looker, cold, war, MathF.Max(0, RangeOf(cold, Settings.SightRange)), null);
        scan.FullTags = true;
        var entry = new SenseEntry { Target = target, Generation = gen };
        Refresh(ref scan, ref entry, target, tc, true, false);
        Count(ref scan, entry, tc);
        Insert(b, b.LowerBound(target), entry, world.Tick);
    }
    /// <summary>
    /// Moves a looker's spot meters on by one scan of <paramref name="dt"/> seconds: a seen sneaker's meter fills by dt / its spot time (a new one starts at 0), the others
    /// empty by dt / <see cref="Orders.SpotForgetSeconds"/>; empty meters and gone targets are dropped. Returns the list in target slot order (null when empty).
    /// </summary>
    static List<Spot>? AdvanceSpots(List<Spot>? spots, List<Sighting> seen, float dt, CharacterTable table)
    {
        spots ??= [];
        foreach (var x in seen)
        {
            int k = spots.FindIndex(p => p.Target == x.Target && p.Generation == x.Generation);
            if (k < 0) spots.Add(new Spot { Target = x.Target, Generation = x.Generation, Value = dt / x.Seconds });
            else spots[k] = spots[k] with { Value = spots[k].Value + dt / x.Seconds };
        }
        for (int k = spots.Count - 1; k >= 0; k--)
        {
            var p = spots[k];
            if (!table.Previous[p.Target].Alive || table.Previous[p.Target].Generation != p.Generation) { spots.RemoveAt(k); continue; }
            if (seen.Exists(x => x.Target == p.Target)) continue;
            p.Value -= dt / Orders.SpotForgetSeconds;
            if (p.Value <= 0) spots.RemoveAt(k);
            else spots[k] = p;
        }
        if (spots.Count == 0) return null;
        spots.Sort((a, b) => a.Target.CompareTo(b.Target));
        return spots;
    }

    /// <summary>Whether the NPC in slot <paramref name="looker"/> has spotted the sneaker <paramref name="target"/> (its spot meter is full; serial phases).</summary>
    public bool Spotted(int looker, CharacterId target) => looker < slots.Length && SpotOf(slots[looker].Spots, target.Slot, target.Generation) > 1;

    /// <summary>
    /// Whether the sneaker <paramref name="target"/> is unseen (Character +0xe8 = 1, docs/game/perception.md "Spotting a sneaker"): not fighting, and no looker's spot meter on it is
    /// filling or full (red or yellow); serial phases.
    /// </summary>
    public bool Unseen(CharacterTable table, CharacterId target)
    {
        if (table.Cold(target.Slot) is { InCombat: true }) return false;
        for (int k = 0; k < size && k < slots.Length; k++)
            if (SpotOf(slots[k].Spots, target.Slot, target.Generation) > 0) return false;
        return true;
    }

    /// <summary>The looker's spot meter on <paramref name="slot"/> before this scan (0: none).</summary>
    static float SpotOf(List<Spot>? spots, int slot, int generation)
    {
        if (spots is null) return 0;
        foreach (var p in spots) if (p.Target == slot && p.Generation == generation) return p.Value;
        return 0;
    }

    /// <summary>The faction <paramref name="cold"/>'s squad is contracted to (<see cref="Scan.Employer"/>): the contract target's (the hirer) for signals 12 and 15; -1 otherwise.</summary>
    static int EmployerOf(World world, CharacterCold cold, out bool allies)
    {
        allies = false;
        var table = world.Characters;
        if (cold.Ai?.Squad.Ai is not { Contract.Signal: 12 or 15 } board || board.MissionTarget is not { IsNone: false } id || id.Slot >= table.HighWater
            || table.Previous[id.Slot].Generation != id.Generation || table.Cold(id.Slot) is not { } h) return -1;
        allies = board.Contract.Signal == 12;
        return h.Faction;
    }

    /// <summary>
    /// The gather (<c>FUN_14085a8d0</c>): a character within the look's reach that has no entry yet (those with one were sensed or decayed before) is sensed, and kept as a new entry when
    /// seen or heard; the near pass and the far pass of <see cref="Look"/> take up to the engine cap (<see cref="GatherCap"/>; the original has none).
    /// </summary>
    static void Visit(int slot, ref Scan s)
    {
        if (slot == s.Self) return;
        ref readonly var other = ref s.Table.Previous[slot];
        float d2 = Vector3.DistanceSquared(other.Position, s.Eye);
        if (s.FarPass ? d2 <= GatherNear * GatherNear || s.Gathered >= GatherCap || d2 > s.Range * s.Range && d2 > NoiseFar * NoiseFar
            : d2 > GatherNear * GatherNear || s.Gathered >= GatherCap + GatherExtra) return;
        var list = s.Block.entries;
        int at = s.Block.LowerBound(slot);
        if (at < list.Count && list[at].Target == slot) return;
        s.Gathered++;
        if (s.Table.Cold(slot) is not { } c || !SenseOne(ref s, slot, c, out bool seen, out bool heard)) return;
        var e = new SenseEntry { Target = slot, Generation = other.Generation };
        Refresh(ref s, ref e, slot, c, seen, heard);
        Count(ref s, e, c);
        Insert(s.Block, at, e, (long)s.Tick);
        if (s.NewEnemy < 0 && (e.Tags & (SenseTag.Enemy | SenseTag.Ko)) == SenseTag.Enemy) s.NewEnemy = slot;
    }

    /// <summary>Puts a new entry at <paramref name="at"/>; with the block full the entry sensed longest ago (before <paramref name="tick"/>) makes room, else the new one is not kept.</summary>
    static void Insert(SenseBlock b, int at, in SenseEntry e, long tick)
    {
        var list = b.entries;
        if (list.Count >= SenseBlock.Cap)
        {
            int oldest = -1;
            for (int k = 0; k < list.Count; k++)
                if (list[k].Tick < tick && (oldest < 0 || list[k].Tick < list[oldest].Tick)) oldest = k;
            if (oldest < 0) return;
            list.RemoveAt(oldest);
            if (oldest < at) at--;
        }
        list.Insert(at, e);
    }

    /// <summary>
    /// Whether the looker senses <paramref name="slot"/> now (<c>FUN_1408593d0</c>: seen by <c>FUN_140857b10</c>, else heard by <c>FUN_140857500</c>). A sneaking target of another side fills
    /// the spot meters of a looker that keeps them (<see cref="Scan.Meters"/>, exposing the sneaker), and counts as seen in view only once its meter was full before this look, or when it
    /// is the looker's own fight target (engine choice: a chaser does not lose its quarry to the meter).
    /// </summary>
    static bool SenseOne(ref Scan s, int slot, CharacterCold c, out bool seen, out bool heard)
    {
        ref readonly var other = ref s.Table.Previous[slot];
        bool sneaking = (c.Orders & CharacterOrders.Sneak) != 0;
        float stealth = sneaking ? Orders.Stealth(c) : -1;
        heard = false;
        if (sneaking && c.Faction != s.Faction && !(s.Player && c.IsPlayer))
        {
            float v = s.Sight.Sees(s.Self, slot, s.Eye, other.Position, s.Outdoor, stealth);
            bool full = SpotOf(s.Spots, slot, other.Generation) > 1;
            if (s.Meters)
            {
                if (v > 0 && !Sighted(s.Sightings!, slot)) s.Sightings!.Add(new Sighting(slot, other.Generation, Orders.SpotSeconds(v, stealth)));
                if (Vector3.DistanceSquared(other.Position, s.Eye) <= s.Range * s.Range)
                    s.Effects!.Emit(s.Self, slot, ExposureEffect, (float)(full && v > 0 ? StealthExposure.Detected : v > 0 ? StealthExposure.Noticed : StealthExposure.Near));
            }
            seen = v > 0 && (full || slot == s.FightTarget);
        }
        else seen = s.Sight.Sees(s.Self, slot, s.Eye, other.Position, s.Outdoor, stealth) > 0;
        if (!seen && s.Effects is { } fx)
            heard = Hears(s.World, s.Table, s.Self, slot, stealth, Rng.Hash(s.Seed, s.Key, RngPurpose.Sense, (s.Tick << 20) ^ (ulong)(uint)slot), fx, ref s.Heard);
        return seen || heard;
    }

    static bool Sighted(List<Sighting> list, int slot)
    {
        foreach (var x in list) if (x.Target == slot) return true;
        return false;
    }

    /// <summary>
    /// An entry sensed now (<c>FUN_1408580d0</c>): its place and time, seen and heard, its tags (<see cref="TagOf"/>); the threats (<c>addToThreatsAndFlockingList</c> <c>FUN_140854370</c>:
    /// a standing character of another faction whose fight targets the looker joins the threats list and adds 1 to <see cref="SenseBlock.ThreatToMe"/>, one fighting another of the
    /// looker's faction adds 1 to <see cref="SenseBlock.ThreatToAllies"/>; the original adds its aggression, Meitou has none); an ally counts as standing or not; and the sensing hooks
    /// of the looker's squad package (war assault TEMP_ENEMY, hired ally TEMP_ALLY) tag it in the squad's memory.
    /// </summary>
    static void Refresh(ref Scan s, ref SenseEntry e, int slot, CharacterCold c, bool seen, bool heard)
    {
        var prev = s.Table.Previous;
        ref readonly var other = ref prev[slot];
        e.Seen = seen;
        e.Heard = heard;
        e.Tick = (long)s.Tick;
        e.Place = other.Position;
        e.Tags = TagOf(ref s, ref e, slot, c, other.Position);
        var b = s.Block;
        if (c.Faction != s.Faction && c.InCombat && c.Medical is { Incapacitated: false } && s.Fights is { } combat)
        {
            var (ft, fg) = combat.TargetOf(slot, other.Generation);
            if (ft == s.Self && fg == prev[s.Self].Generation)
            {
                b.threats.Add(new CharacterId(slot, other.Generation));
                b.ThreatToMe += 1;
            }
            else if (ft >= 0 && ft < s.Table.HighWater && prev[ft].Generation == fg && s.Table.Cold(ft) is { } fc && fc.Faction == s.Faction) b.ThreatToAllies += 1;
        }
        if ((e.Tags & SenseTag.Ally) != 0)
        {
            if (c.Medical is { Incapacitated: false, Prone: false, Dead: false }) b.Allies++;
            else b.AlliesDown++;
        }
        if (s.Effects is { } fx && s.Squad >= 0 && s.Memory is not null)
        {
            if (s.War.IsSet && s.War.Everyone && WarEnemy(ref s, c, other.Position)) fx.Emit(s.Self, slot, TagEffect, Dialogue.MemoryTag.TempEnemy);
            if (s.EmployerAllies && s.Employer >= 0 && c.Faction == s.Employer) fx.Emit(s.Self, slot, TagEffect, Dialogue.MemoryTag.TempAlly);
        }
    }

    /// <summary>
    /// The tags of a sensed character (<c>FUN_1408580d0</c>, docs/game/perception.md "Sense tags"): KO when down or dead (<see cref="CharacterFacts.Down"/>), imprisoned, and ENEMY when the
    /// looker regards it an enemy (<see cref="Regards"/>; a campaign force's member NEUTRAL instead), else ALLY for its own or an allied faction, else NEUTRAL.
    /// </summary>
    static SenseTag TagOf(ref Scan s, ref SenseEntry e, int slot, CharacterCold c, Vector3 at)
    {
        var tags = SenseTag.None;
        if (CharacterFacts.Down(c) || c.Medical is { Dead: true }) tags |= SenseTag.Ko;
        if (c.IsImprisoned) tags |= SenseTag.Imprisoned;
        if (Fighting(ref s, slot)) return tags | SenseTag.Enemy;
        // The rest (relations, disguise, squad memory, contracts, campaigns) is worked out again on the looker's full looks only (engine choice, for the look's cost; FullTags).
        if (s.FullTags || e.Base == SenseTag.None)
            e.Base = Regards(ref s, slot, c, at, out bool force) ? force ? SenseTag.Neutral : SenseTag.Enemy
                : c.Faction == s.Faction || s.Relations.Contains(c.Faction) && s.Relations.IsAlly(s.Faction, c.Faction) ? SenseTag.Ally : SenseTag.Neutral;
        return tags | e.Base;
    }

    /// <summary>The character in <paramref name="slot"/> fights the looker or is fought by it (last tick's fights).</summary>
    static bool Fighting(ref Scan s, int slot)
    {
        if (s.Fights is not { } combat) return false;
        var prev = s.Table.Previous;
        int gen = prev[slot].Generation, selfGen = prev[s.Self].Generation;
        return combat.TargetOf(slot, gen) == (s.Self, selfGen) || combat.TargetOf(s.Self, selfGen) == (slot, gen);
    }

    /// <summary>
    /// Slot 125, "the looker regards it an enemy" (<c>FUN_14079c950</c>, docs/game/perception.md "Sense tags"; engine reading): one fighting the looker or fought by it, of any faction
    /// (the TEMP_ENEMY <c>AI::underMeleeAttack</c> writes, read for any squad; Meitou's scripted fights may pit one faction against itself); else never its own faction; a slave only when the looker is hostile to its owner's faction and is not the owner's; else hostility from
    /// the looker's faction to the one it is seen as (a disguise) without coexistence, with the freed rule; the squad memory's TEMP_ENEMY makes, TEMP_ALLY unmakes one; a contract's
    /// employer never is; a campaign assault's extra target always is. <paramref name="force"/>: an enemy that is a campaign force's member, sensed NEUTRAL (<c>FUN_140796240</c>).
    /// </summary>
    static bool Regards(ref Scan s, int slot, CharacterCold c, Vector3 at, out bool force)
    {
        force = false;
        var prev = s.Table.Previous;
        int gen = prev[slot].Generation, selfGen = prev[s.Self].Generation;
        if (c.Faction == s.Faction) return false;
        if (s.War.IsSet && WarEnemy(ref s, c, at)) return true;
        bool enemy;
        if (c.SlaveState == 1) enemy = c.SlaveOwner != s.Faction && s.Relations.Contains(c.SlaveOwner) && s.Relations.IsHostile(s.Faction, c.SlaveOwner);
        else
        {
            int seenAs = Crime.ArrestSystem.SeenFaction(c);
            enemy = s.Relations.Contains(seenAs) && s.Relations.IsHostile(s.Faction, seenAs) && s.Cold is not { Freed: true }
                && (!c.Freed || s.Relations.Factions[s.Faction] is { } own && (own.NotReal || own.DefaultRelation < FreedHostility));
        }
        bool tagged = false;
        if (s.Memory is { } memory)
        {
            var target = new CharacterId(slot, gen);
            if (memory.AnyTempEnemy(s.Squad) && memory.HasShort(s.World, s.Squad, target, Dialogue.MemoryTag.TempEnemy)
                || s.World.Player.Faction == s.Faction && memory.AnyTempEnemy(c.SquadId) && memory.HasShort(s.World, c.SquadId, new CharacterId(s.Self, selfGen), Dialogue.MemoryTag.TempEnemy)) enemy = tagged = true;
            else if (enemy && s.Squad >= 0 && memory.HasShort(s.World, s.Squad, target, Dialogue.MemoryTag.TempAlly)) enemy = false;
        }
        if (enemy && s.Employer >= 0 && (c.Faction == s.Employer || s.EmployerAllies && s.Relations.Contains(c.Faction) && s.Relations.IsAlly(s.Employer, c.Faction))) enemy = false;
        if (enemy && !tagged && c.SquadId >= 0 && s.World.Squads.Find(c.SquadId) is { PlatoonId: >= 0 } squad && s.World.Platoons.Find(squad.PlatoonId)?.Campaign is not null) force = true;
        return enemy;
    }

    /// <summary>The counts of the pass (<c>FUN_1408593d0</c>): an entry tagged ENEMY whose character is not down, dead or carried adds to <c>numEnemies</c>, one tagged NEUTRAL to <c>numNeutrals</c>.</summary>
    static void Count(ref Scan s, in SenseEntry e, CharacterCold c)
    {
        if (CharacterFacts.Down(c) || c.Medical is { Dead: true }) return;
        if ((e.Tags & SenseTag.Enemy) != 0) s.Block.Enemies++;
        else if ((e.Tags & SenseTag.Neutral) != 0) s.Block.Neutrals++;
    }

    /// <summary>The sense-age unit in seconds (<c>FUN_140852150</c>: game hours x 3600 / 33 x 0.5; <see cref="GiveUpSenseSeconds"/> = 12 of them).</summary>
    public const float SenseUnitSeconds = GiveUpSenseSeconds / 12;
    /// <summary>How long an unsensed entry lasts, in sense units (<c>SensoryData::decay</c> <c>FUN_140859010</c>, <b>Verified</b> literals): 30, 60 for some targets, 120 for a player character in the home.</summary>
    public const int SenseLifeUnits = 30, SenseLifeLongUnits = 60, SenseLifeHomeUnits = 120;
    /// <summary>How long an unsensed entry follows its character's real place, in sense units (<c>FUN_140859010</c>, <b>Verified</b> literals): 8 inside a building, 25 out of one.</summary>
    public const int FollowInsideUnits = 8, FollowOutsideUnits = 25;
    /// <summary>The long-term squad memory tags that keep an entry 60 units: intruder, stole from me, captor, killed my friend (mask 0x232).</summary>
    const uint LongSenseTags = 0x232;

    /// <summary>
    /// An entry not sensed this look (<c>SensoryData::decay</c> <c>FUN_140859010</c>, <b>Verified</b>): seen and heard cleared, its place follows the character's for
    /// <see cref="FollowInsideUnits"/> / <see cref="FollowOutsideUnits"/> sense units, and it is dropped (false) past <see cref="SenseLife"/>.
    /// </summary>
    static bool Decay(ref Scan s, ref SenseEntry e, int slot, CharacterCold c)
    {
        e.Seen = e.Heard = false;
        float age = ((long)s.Tick - e.Tick) * s.World.TickSeconds / SenseUnitSeconds;
        if (age < (s.Sight.BuildingOf(slot) is null ? FollowOutsideUnits : FollowInsideUnits)) e.Place = s.Table.Previous[slot].Position;
        return age <= SenseLife(s.World, s.Relations, s.Memory, s.Cold!, slot, c);
    }

    /// <summary>
    /// The lifetime of a looker's entry of <paramref name="tc"/> in sense units (<c>FUN_140859010</c>, <b>Verified</b>): 120 when the looker's faction is real and <paramref name="tc"/> is a player
    /// character inside the looker's home building; 60 when it is fighting, the looker's squad remembers it as an intruder, thief, captor or friend killer, or it is the looker's faction's
    /// slave; else 30.
    /// </summary>
    static int SenseLife(World world, FactionRelations relations, Dialogue.SquadMemory? memory, CharacterCold cold, int t, CharacterCold tc)
    {
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        bool real = !relations.Contains(cold.Faction) || !relations.Factions[cold.Faction].NotReal;
        if (real && world.Player.Faction >= 0 && tc.Faction == world.Player.Faction && squad?.HomeBuilding is { } home && world.Sight.BuildingOf(t) == home.Key) return SenseLifeHomeUnits;
        if (tc.InCombat || tc.SlaveState == 1 && tc.SlaveOwner == cold.Faction) return SenseLifeLongUnits;
        if (squad is not null && memory?.Find(world, squad.Id, world.Characters.IdOf(t)) is { } m && (m.Long & LongSenseTags) != 0) return SenseLifeLongUnits;
        return SenseLifeUnits;
    }

    /// <summary>
    /// The attack on sight (goal 16's finder <c>FUN_14059f3a0</c> and the stances' finders, docs/game/ai-tasks.md "Combat goals"): of the entries seen or heard at this look, the best by the
    /// target score (<see cref="Candidate"/>), ties to the lower slot.
    /// </summary>
    static void Pick(ref Scan s)
    {
        var list = s.Block.entries;
        for (int k = 0; k < list.Count; k++)
            if (list[k].Now && s.Table.Cold(list[k].Target) is { } c) Candidate(ref s, list[k].Target, c);
    }

    /// <summary>
    /// The finder of an attack goal of an NPC the AI runs (16, 21, 144, 34; docs/game/ai-tasks.md "Combat goals"), for its chooser (<see cref="Ai.AiSystem"/>): of the characters its last look
    /// saw or heard, and those a squad mate's last look saw or heard (tagged an enemy, or a neutral for 21) within twice the looker's range R (the squad's shared sightings,
    /// <c>GroupSense</c>, docs/game/perception.md: no view cone or light test), the best by the target score that the goal's own test takes (<see cref="Candidate"/> with the goal's stance:
    /// relations, tags, the post and path tests; 144 its home circle, 34 an attacker of an ally in its town). <see cref="CharacterId.None"/> (the factor 0) when there is none, when the look
    /// picks no fights (<see cref="AggroSettings.AttackOnSight"/> off) or the looker cannot pick one (a slave, no relations, no fighter). A pure read of the last looks and fights: any thread
    /// in the think.
    /// </summary>
    public CharacterId FindFoe(World world, int slot, FoeFinder finder, IReadOnlyDictionary<int, int>? pending = null)
    {
        var table = world.Characters;
        if (!Settings.AttackOnSight || (uint)slot >= (uint)slots.Length || slots[slot].Senses is not { } own
            || table.Cold(slot) is not { Medical: not null, SlaveState: not 1, Fighter.CanAttack: true } cold || !Relations.Contains(cold.Faction)) return CharacterId.None;
        var war = cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { War.IsSet: true } assaulting ? assaulting.War : WarTargets.None;
        if (finder == FoeFinder.TroubleMakers) return FindTroubleMaker(world, slot, cold, own, war, pending);
        bool neutrals = finder == FoeFinder.EnemiesAndNeutrals;
        // As the look's own gate: no enemy faction about, no neutrals wanted, no war and no TEMP_ENEMY tag of its squad leave nothing to find.
        if (!(cold.Faction < hasEnemy.Length && hasEnemy[cold.Faction]) && !neutrals && !war.IsSet && !(Memory?.AnyTempEnemy(cold.SquadId) ?? false)) return CharacterId.None;
        float range = MathF.Max(0, RangeOf(cold, Settings.SightRange));
        var scan = NewScan(world, slot, cold, war, range, null, keepSenses: false, finder);
        scan.Pending = pending;
        Pick(ref scan);
        if (cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { Members.Count: > 1 } squad)
        {
            var prev = table.Previous;
            var eye = prev[slot].Position;
            float reach = 2 * range;
            var wanted = SenseTag.Enemy | (neutrals ? SenseTag.Neutral : SenseTag.None);
            foreach (var m in squad.Members)
            {
                if (m.Slot == slot || (uint)m.Slot >= (uint)slots.Length || slots[m.Slot].Generation != m.Generation || slots[m.Slot].Senses is not { } mate
                    || mate.Enemies + (neutrals ? mate.Neutrals : 0) == 0) continue;
                foreach (var e in mate.entries)
                {
                    int t = e.Target;
                    if (!e.Now || t == slot || (e.Tags & wanted) == 0 || (e.Tags & SenseTag.Ko) != 0 || (uint)t >= (uint)table.HighWater || !prev[t].Alive || prev[t].Generation != e.Generation
                        || own.Knows(new CharacterId(t, e.Generation), now: true) || Vector3.DistanceSquared(prev[t].Position, eye) > reach * reach || table.Cold(t) is not { } c) continue;
                    Candidate(ref scan, t, c);
                }
            }
        }
        return scan.Best >= 0 ? new CharacterId(scan.Best, table.Previous[scan.Best].Generation) : CharacterId.None;
    }

    /// <summary>
    /// 34 ATTACK_TROUBLE_MAKERS, "Law enforcer" (finder <c>FUN_14099baa0</c>, <b>Verified</b>; docs/game/ai-tasks.md "Combat goals"): nothing while the looker is in no town (its current town).
    /// Pass 1, the entries of its senses tagged ENEMY and not KO (seen or heard or not): one in the same town whose current goal intends aggression (status +0xac,
    /// <see cref="Ai.TaskAggression.OfGoal"/>) or that its squad tagged AGGRESSOR. Pass 2, the entries tagged NEUTRAL and not KO: not law enforcement, trader or diplomat; military only when
    /// its squad's town is not this one and its faction does not own it (the town's resident factions are <b>Unknown</b>: the owner stands in); in the same town; not a slaver; intending
    /// aggression, tagged AGGRESSOR, or a bandit or an animal whose running task intends it (+0xa0); and attacking someone in a fight whom the looker counts as an ally (its own or an
    /// allied faction; the original's "innocent" victim branch is <b>Unknown</b> in part and left out). The best by the target score, as the look's (the area test <c>FUN_140599f30</c> is not
    /// modelled). A pure read (any thread in the think).
    /// </summary>
    CharacterId FindTroubleMaker(World world, int slot, CharacterCold cold, SenseBlock own, WarTargets war, IReadOnlyDictionary<int, int>? pending)
    {
        if (Towns is not { } towns) return CharacterId.None;
        var table = world.Characters;
        var prev = table.Previous;
        var town = Ai.AiSystem.TownAt(towns, world, prev[slot].Position);
        if (town.IsNone) return CharacterId.None;
        int owner = town.Site >= 0 && town.Site < towns.SiteFactions.Count ? towns.SiteFactions[town.Site] : -1;
        var fights = Combat ?? world.SystemOf<Combat.CombatSystem>();
        var scan = NewScan(world, slot, cold, war, MathF.Max(0, RangeOf(cold, Settings.SightRange)), null, keepSenses: false, FoeFinder.TroubleMakers);
        scan.Pending = pending;
        foreach (var e in own.entries)
        {
            int t = e.Target;
            bool enemy = (e.Tags & SenseTag.Enemy) != 0;
            if ((e.Tags & SenseTag.Ko) != 0 || !enemy && (e.Tags & SenseTag.Neutral) == 0 || (uint)t >= (uint)table.HighWater || !prev[t].Alive || prev[t].Generation != e.Generation
                || table.Cold(t) is not { Medical.Incapacitated: false, IsImprisoned: false } c) continue;
            var id = new CharacterId(t, e.Generation);
            bool aggressor = Memory is { } memory && cold.SquadId >= 0 && memory.HasShort(world, cold.SquadId, id, Dialogue.MemoryTag.Aggressor);
            bool intends = Ai.TaskAggression.OfGoal(c) > 0;
            if (!enemy)
            {
                if (c.NpcClass is FundamentalType.LawEnforcement or FundamentalType.Trader or FundamentalType.Diplomat or FundamentalType.Slaver) continue;
                if (c.NpcClass == FundamentalType.Military && (c.Faction == owner || world.Squads.Find(c.SquadId)?.Ai?.Town == town.Site && town.Site >= 0)) continue;
                if (!(intends || aggressor || Ai.TaskAggression.OfTask(c) > 0 && (c.NpcClass == FundamentalType.Bandit || c.Fighter?.IsAnimal == true))) continue;
                if (fights is null || fights.TargetOf(t, e.Generation) is not { Slot: >= 0 } v || (uint)v.Slot >= (uint)table.HighWater || prev[v.Slot].Generation != v.Generation
                    || table.Cold(v.Slot) is not { InCombat: true } victim || !(victim.Faction == cold.Faction || Relations.Contains(victim.Faction) && Relations.IsAlly(cold.Faction, victim.Faction))) continue;
            }
            else if (!intends && !aggressor) continue;
            if (Ai.AiSystem.TownAt(towns, world, prev[t].Position) != town || PursuitSystem.PathImpossible(cold, id, world.Tick)) continue;
            float d2 = Vector3.DistanceSquared(prev[t].Position, scan.Eye);
            float score = Score(ref scan, t, c, d2) ?? float.NegativeInfinity;
            if (!(score > scan.BestScore)) continue;
            (scan.Best, scan.BestD2, scan.BestScore) = (t, d2, score);
        }
        return scan.Best >= 0 ? new CharacterId(scan.Best, prev[scan.Best].Generation) : CharacterId.None;
    }

    static void Candidate(ref Scan s, int slot, CharacterCold c)
    {
        ref readonly var other = ref s.Table.Previous[slot];
        // A caged prisoner is nobody's target, an owned slave nobody's but its owner's enemies' (engine choice: the original's sensed-list filter is not decoded; town cages
        // hold enemies of their guards, and slaves of a hostile faction, such as Heng's Outlaws, stand among them). The territorial finder (144, FUN_14059fd70, Verified)
        // skips a slave only when the looker's faction is allied with its owner.
        if (c is not { Medical.Incapacitated: false, IsImprisoned: false }) return;
        if (c.SlaveState == 1 && (s.Finder == FoeFinder.Territory ? s.Relations.Contains(c.SlaveOwner) && s.Relations.IsAlly(s.Faction, c.SlaveOwner) : !IsEnemy(s.Relations, s.Faction, c.SlaveOwner))) return;
        // The attack-on-sight finder (FUN_14059f3a0, Verified) skips a target whose posture is not 0 (staying low or crawling) while it stands inside a building. A chained slave of
        // the looker's own faction takes the slave test instead (dropped while enslaved, a target once escaped); Meitou has no chain flag, so state 1 stands for it.
        if (c.Medical?.Prone == true && s.Sight.BuildingOf(slot) is not null && !(c.SlaveState == 1 && c.SlaveOwner == s.Faction)) return;
        // Wanted characters are the arrest system's (Crime.ArrestSystem): the police spot, fine or arrest them there. A working disguise stands in for the target's faction
        // (slot 125, docs/game/perception.md "Sense tags"; Crime.ArrestSystem.SeenFaction).
        int seenAs = Crime.ArrestSystem.SeenFaction(c);
        // A freed prisoner (Freed, FUN_14079c950's last test, Observed) attacks no one for the relations alone, and is attacked so only by a looker of a faction that is not real
        // or below -30 by default.
        bool hostile = AttacksOnSight(s.Relations, s.Faction, seenAs) && s.Cold is not { Freed: true }
            && (!c.Freed || s.Relations.Factions[s.Faction] is { } mine && (mine.NotReal || mine.DefaultRelation < FreedHostility));
        bool warEnemy = s.War.IsSet && WarEnemy(ref s, c, other.Position);
        bool neutrals = s.Finder == FoeFinder.EnemiesAndNeutrals;
        bool enemy = hostile || neutrals && IsNeutral(s.Relations, s.Faction, seenAs) || warEnemy;
        // A campaign force's member is sensed as NEUTRAL, not ENEMY (FUN_1408580d0 over FUN_140796240, docs/game/perception.md "Sense tags"): a raid walks up to talk first.
        // A looker that attacks neutrals too still drops it unless it is hostile to its faction (FUN_1408569a0, Verified); its own campaign's targets stay, and a TEMP_ENEMY
        // tag (below) makes it an enemy again.
        if (enemy && (!neutrals || !IsEnemy(s.Relations, s.Faction, seenAs)) && !warEnemy && c.SquadId >= 0
            && s.World.Squads.Find(c.SquadId) is { PlatoonId: >= 0 } force && s.World.Platoons.Find(force.PlatoonId)?.Campaign is not null) enemy = false;
        // The squads' temporary tags (FUN_14079c950, other faction only): the looker's squad holds TEMP_ENEMY on the target, or, for a player-faction looker, the target's squad on it,
        // makes an enemy; the looker's squad holding TEMP_ALLY on the target unmakes one.
        if (s.Memory is { } memory && c.Faction != s.Faction)
        {
            var target = new CharacterId(slot, other.Generation);
            if (s.Squad >= 0 && memory.HasShort(s.World, s.Squad, target, Dialogue.MemoryTag.TempEnemy)
                || s.World.Player.Faction == s.Faction && c.SquadId >= 0 && memory.HasShort(s.World, c.SquadId, new CharacterId(s.Self, s.Table.Previous[s.Self].Generation), Dialogue.MemoryTag.TempEnemy)) enemy = true;
            else if (enemy && s.Squad >= 0 && memory.HasShort(s.World, s.Squad, target, Dialogue.MemoryTag.TempAlly)) enemy = false;
        }
        // A contract's employer (Contract_HiredAlly / Contract_DefendPlayerBase assessNeutral, and HiredAlly's assessEnemy for the employer's allies, Verified): TEMP_ALLY, not an enemy.
        if (enemy && s.Employer >= 0 && (c.Faction == s.Employer || s.EmployerAllies && s.Relations.Contains(c.Faction) && s.Relations.IsAlly(s.Employer, c.Faction))) enemy = false;
        if (!enemy) return;
        // One it found no path to lately is never picked (isPathImpossible in the attack-on-sight finder's score, docs/game/combat.md "Target score"): else a guard shut in with its
        // enemy (a cage, a locked room) takes it as target again every look and never turns on anyone it can reach.
        if (s.Cold is { } looker && PursuitSystem.PathImpossible(looker, new CharacterId(slot, other.Generation), (long)s.Tick)) return;
        // 144 takes only an enemy within its home (InTerritory).
        if (s.Finder == FoeFinder.Territory && !InTerritory(ref s, slot, other.Position)) return;
        // The target score (Combat.TargetScore; Taunt and Passive weigh in there), after the Hold post test for a holding looker. Strictly better only, so a tie keeps the lower slot.
        float d2 = Vector3.DistanceSquared(other.Position, s.Eye);
        float score = Score(ref s, slot, c, d2) ?? float.NegativeInfinity;
        if (!(score > s.BestScore)) return;
        s.Best = slot;
        s.BestD2 = d2;
        s.BestScore = score;
    }

    /// <summary>Territorial (144, <c>FUN_14059fd70</c>, <b>Verified</b>): without a home circle anyone; else one within the circle, or outside it in the looker's own building.</summary>
    static bool InTerritory(ref Scan s, int slot, Vector3 at) =>
        s.Home.R2 == 0 || Vector2.DistanceSquared(new Vector2(at.X, at.Z), s.Home.Centre) < s.Home.R2
        || s.World.Sight.BuildingOf(s.Self) is { } inside && s.World.Sight.BuildingOf(slot) == inside;

    /// <summary>The attack-on-sight score of <paramref name="c"/> in <paramref name="slot"/> (goal 16's finder picks by <c>FUN_14059e300</c>); null when dropped.</summary>
    static float? Score(ref Scan s, int slot, CharacterCold c, float d2)
    {
        ref readonly var other = ref s.Table.Previous[slot];
        var fight = s.Combat?.StateOf(new CharacterId(slot, other.Generation)) ?? Meitou.Simulation.Combat.CombatSlot.Fresh(other.Generation);
        if (s.HoldPost && !Orders.HoldPost(s.Eye, s.HoldPoint, other.Position, other.Velocity, other.Yaw, c.Medical!.Crawling || fight.AttackTech >= 0, c.InCombat)) return null;
        var candidate = new Meitou.Simulation.Combat.TargetCandidate
        {
            Staggered = fight.StumbleClip >= 0 && (int)s.Tick < fight.StunUntil,
            Attackers = (s.Combat?.AttackerCount(s.World, slot) ?? 0) + (s.Pending is { } pending ? pending.GetValueOrDefault(slot) : 0),
            Taunt = (c.Orders & CharacterOrders.Taunt) != 0,
            Passive = (c.Orders & CharacterOrders.Passive) != 0,
            Distance = MathF.Sqrt(d2),
            Fighting = c.InCombat,
            Gunner = c.Gunning is not null,
            Crippled = c.Medical!.Crawling || c.Fighter is not { CanAttack: true },
            Peaceful = !c.InCombat && !IsEnemy(s.Relations, c.Faction, s.Faction),
            GettingUp = CharacterFacts.GettingUp(c, (long)s.Tick),
        };
        return Meitou.Simulation.Combat.TargetScore.Score(candidate, s.Player, s.Hold, s.Prone, s.Animal);
    }

    /// <summary>
    /// Neither an ally (nor the same faction) nor hostile, and not a faction the looker's coexists with (one way, FUN_14059f720 over FUN_1406b23c0, Verified): whom
    /// ATTACK_ENEMIES_AND_NEUTRALS adds to the enemies.
    /// </summary>
    public static bool IsNeutral(FactionRelations relations, int mine, int theirs) =>
        relations.Contains(theirs) && !relations.IsAlly(mine, theirs) && !relations.IsHostile(mine, theirs) && !relations.Coexists(mine, theirs);

    /// <summary>A character the AI put to bed or sat down (<see cref="Ai.AiBehaviour.Sleep"/>, <see cref="Ai.AiBehaviour.Sit"/>): unlike one resting by order, it can be roused into a fight.</summary>
    static bool AiSleeper(CharacterCold? cold) => cold?.Ai is { Behaviour: Ai.AiBehaviour.Sleep or Ai.AiBehaviour.Sit };

    /// <summary>
    /// A campaign assault's extra enemy (<see cref="WarTargets"/>): of the target faction, or with <c>Everyone</c> the characters <c>Package_WarAssault</c>'s sensing hook takes
    /// (<c>FUN_14028a3f0</c>, <b>Verified</b>): not of the looker's faction, no animal, of a faction that is real and not a bandit one (the faction's <c>fundamental type</c> 8, not the
    /// character's class), and in no town or in the assault's target town (no distance test).
    /// </summary>
    static bool WarEnemy(ref Scan s, CharacterCold c, Vector3 at)
    {
        var war = s.War;
        if (!war.IsSet || c.Faction == s.Faction) return false;
        if (c.Faction == war.Faction) return true;
        if (!war.Everyone || c.Fighter?.IsAnimal == true || !s.Relations.Contains(c.Faction) || s.Relations.Factions[c.Faction] is { NotReal: true } or { FundamentalType: FundamentalType.Bandit }) return false;
        if (s.Towns is not { } towns) return true;
        var town = Ai.AiSystem.TownAt(towns, s.World, at);
        return town.IsNone || town == war.Town;
    }

    /// <summary>
    /// Whether a looker of faction <paramref name="mine"/> takes a sensed character of <paramref name="theirs"/> for an attack-on-sight target by the relations: hostile mine to
    /// theirs (-30 or lower) and <paramref name="mine"/>'s own <c>coexistence</c> does not list <paramref name="theirs"/> (<c>FUN_14059f3a0</c> over <c>FUN_1406b23c0</c>, which
    /// reads only the looker's table, <b>Verified</b>; the sense tag does not read coexistence, docs/game/perception.md). Same faction and allies are never hostile.
    /// </summary>
    public static bool AttacksOnSight(FactionRelations relations, int mine, int theirs) =>
        relations.Contains(theirs) && relations.IsHostile(mine, theirs) && !relations.Coexists(mine, theirs);

    /// <summary>
    /// Whether the factions are enemies in either direction's coexistence sense: hostile mine to theirs and neither lists the other in <c>coexistence</c>. Engine choice for the
    /// systems that have no looker (trade, crime, flight, fights far away, targets of a fight already on): the original's attack on sight reads the looker's side only
    /// (<see cref="AttacksOnSight"/>).
    /// </summary>
    public static bool IsEnemy(FactionRelations relations, int mine, int theirs) =>
        relations.Contains(theirs) && relations.IsHostile(mine, theirs) && !relations.Coexists(mine, theirs) && !relations.Coexists(theirs, mine);

    // ------------------------------------------------------------------ decide

    public void Apply(World world, in Effect e)
    {
        if (e.Kind == ExposureEffect)
        {
            // The sneaker keeps the highest exposure any looker gave it this tick.
            if (world.Characters.Cold(e.Target) is not { } sneaker) return;
            var level = (StealthExposure)(int)e.Amount;
            if (sneaker.StealthExposureTick != world.Tick || level > sneaker.StealthExposure) sneaker.StealthExposure = level;
            sneaker.StealthExposureTick = world.Tick;
            if (level >= StealthExposure.Noticed) Watch(world, sneaker, world.Characters.IdOf(e.Source), level);
            return;
        }
        if (e.Kind == HearingXpEffect)
        {
            HearingXp(world, e.Source, e.Target, e.Amount > 0);
            return;
        }
        if (e.Kind == AlmostWokeEffect)
        {
            world.Defer(new Dialogue.DialogueEvent(world.Characters.IdOf(e.Source), world.Characters.IdOf(e.Target), AlmostWokeUpEvent) { Tick = world.Tick + 1 });
            return;
        }
        if (e.Kind == TagEffect)
        {
            if (Memory is { } memory && world.Characters.Cold(e.Source) is { SquadId: >= 0 } looker && world.Characters.Next[e.Source].Alive && world.Characters.Next[e.Target].Alive)
                memory.Flag(world, looker.SquadId, world.Characters.IdOf(e.Target), (int)e.Amount);
            return;
        }
        if (e.Kind != SightEffect) return;
        if (takenTick != world.Tick) { taken.Clear(); takenTick = world.Tick; }
        var table = world.Characters;
        var next = table.Next;
        int i = e.Source, t = e.Target;
        if (!next[t].Alive || table.Cold(t) is not { Medical.Incapacitated: false } target) return;
        // An NPC the AI runs decides by its own goals in the chooser (docs/game/ai-tasks.md "Combat goals"): the sighting only asks it, and the squad mates that share it, to think at the
        // next tick (AiMind.Replan), where its attack goals' finders (FindFoe) and the rest of its goals are weighed.
        if (table.Cold(i)?.Ai is { } minded)
        {
            minded.Replan = true;
            JoinMates(world, table, i, t, target, null);
            return;
        }
        // A player character takes this fight through row 16 only, Hold's goal (priority 3) or its job: it and its free mates whose own look would take the target are offered it, and the
        // decision step weighs it against their orders, goals and jobs.
        if (table.Cold(i) is not { IsPlayer: true } || !Free(table, i, taken)) return;
        var fighters = new List<CharacterId> { table.IdOf(i) };
        taken.Add(i);
        JoinMates(world, table, i, t, target, fighters);
        var targetId = table.IdOf(t);
        if (offersTick != world.Tick) { offers.Clear(); offersTick = world.Tick; }
        foreach (var f in fighters)
        {
            offers.TryAdd(f.Slot, targetId);
            DecisionSystem.Request(table.Cold(f.Slot)!);
        }
    }

    /// <summary>
    /// One hearing check's stealth XP for the heard character (<c>FUN_1408c7330</c>, <b>Verified</b> amounts; docs/game/character-stats.md "Stealth (heard check)"): the listener is always
    /// taken as regarding the heard as an enemy (engine: any look hears, so neutrals and allies over-pay), so 2, 2 more when the heard is in the listener's home, x20 when heard, times the exposure factor, which then falls x0.7. Nothing
    /// for one down, carried or in a bed or cage (the factor still falls). The XP is added to <see cref="CharacterCold.HearingXp"/> for <see cref="BodySystem"/> to pay. Not gated to the player's
    /// faction (the original has no such test here; only the sneak update that resets the factor is).
    /// </summary>
    static void HearingXp(World world, int listener, int target, bool heard)
    {
        var table = world.Characters;
        if (table.Cold(target) is not { Stats: not null, Medical: { } medical } cold) return;
        float amount = Bodies.XpService.HearingAmount(true, InListenersHome(world, listener, target, cold) || cold.SlaveState == 1, heard);
        if (medical.Dead || medical.Incapacitated || !cold.CarriedBy.IsNone || cold.InSomething != Locks.HeldState.None) amount = 0;
        cold.HearingXp += amount * cold.StealthFactor;
        cold.StealthFactor *= Bodies.XpService.HearingFactorDecay;
    }

    /// <summary>
    /// The home rule of the stealth XP (<c>FUN_140857500</c> at its end, <b>Observed</b>): the heard character stands in the building that is the listener's squad's home and is not using
    /// anything (in a bed or cage), unless the building is public and the squad's byte <c>+0xe8</c> (meaning <b>Unknown</b>, read as clear) is clear; the original then falls back on the
    /// heard being a slave (status 1), which the caller adds.
    /// </summary>
    static bool InListenersHome(World world, int listener, int target, CharacterCold heard)
    {
        if (heard.InSomething != Locks.HeldState.None || world.Sight.BuildingOf(target) is not { } building || world.Characters.Cold(listener) is not { } lc || world.Squads.Find(lc.SquadId) is not { } squad) return false;
        return (squad.Ai?.OwnHome ?? squad.HomeBuilding)?.Key == building && world.Fixtures.PublicOf(building) == false;
    }

    /// <summary>How long a watcher stays on a sneaker's list after its last look (<c>FUN_1408586d0</c>: entries older than 4 s are dropped, Verified literal).</summary>
    public const float WatcherSeconds = 4;

    /// <summary>Puts <paramref name="looker"/> on the sneaker's watcher list at <paramref name="level"/> (or updates it) and drops lapsed entries.</summary>
    static void Watch(World world, CharacterCold sneaker, CharacterId looker, StealthExposure level)
    {
        var list = sneaker.Watchers ??= [];
        long lapse = world.Tick - (long)MathF.Ceiling(WatcherSeconds / world.TickSeconds);
        list.RemoveAll(w => w.Tick < lapse || w.Looker == looker);
        list.Add(new StealthWatcher(looker, level, world.Tick));
    }

    /// <summary>
    /// The looker's squad mates share its sighting, not its attack: the original shares a squad's sightings (<c>GroupSense</c>: a member counts a character a mate saw or heard as seen
    /// within 2 R, with no view cone or light test, <c>FUN_140857b10</c>, Observed), and each member attacks only by its own goals. A mate within twice its own range R of the target that
    /// has a mind is asked to think at the next tick (its attack goals read the mates' sightings, <see cref="FindFoe"/>); a player character that is free is offered the target with the
    /// looker (<paramref name="fighters"/>) when its own attack on sight takes it (<see cref="Takes"/>), in the squad's member order. Null <paramref name="fighters"/>: only the asking.
    /// </summary>
    void JoinMates(World world, CharacterTable table, int slot, int target, CharacterCold targetCold, List<CharacterId>? fighters)
    {
        var squad = table.Cold(slot)!.SquadId >= 0 ? world.Squads.Find(table.Cold(slot)!.SquadId) : null;
        if (squad is null) return;
        var at = table.Next[target].Position;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || s == slot) continue;
            var cold = table.Cold(s)!;
            float r = 2 * RangeOf(cold, Settings.SightRange);
            if (Vector3.DistanceSquared(table.Next[s].Position, at) > r * r) continue;
            if (cold.Ai is { } mate)
            {
                if (!cold.InCombat) mate.Replan = true;
                continue;
            }
            if (fighters is null || !cold.IsPlayer || !Free(table, s, taken) || !Takes(world, s, cold, target, targetCold)) continue;
            fighters.Add(m);
            taken.Add(s);
        }
    }

    /// <summary>
    /// A fight an attack or protect goal of the AI started (<see cref="Ai.AiSystem"/>'s serial step, docs/game/ai-tasks.md "Combat goals"): counted with this system's fights and logged
    /// (<see cref="AggroSettings.RecordLog"/>; nobody joined with it), and for an attack goal the fighter's alert voice. Serial phases only.
    /// </summary>
    internal void GoalFight(World world, int slot, int target, bool voice)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        var targetId = table.IdOf(target);
        Started++;
        if (voice) world.Sounds?.Enqueue(new SoundEvent(SoundKind.Alert, id, targetId, table.Next[target].Position));
        if (Settings.RecordLog)
            log.Add(new AggroEntry(world.Tick, id, targetId, table.Cold(slot)!.Faction, table.Cold(target)!.Faction, Vector3.Distance(table.Next[slot].Position, table.Next[target].Position)));
    }

    /// <summary>
    /// Whether the character in <paramref name="slot"/> would attack <paramref name="target"/> had it sensed it itself: its own look's gates (a slave and a faction without relations pick no
    /// fight) and its attack-on-sight finder (<see cref="Candidate"/>: the relations, a holder's post test). Serial phases only.
    /// </summary>
    bool Takes(World world, int slot, CharacterCold cold, int target, CharacterCold targetCold)
    {
        if (cold.SlaveState == 1 || !Relations.Contains(cold.Faction)) return false;
        var war = cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { War.IsSet: true } assaulting ? assaulting.War : WarTargets.None;
        var scan = NewScan(world, slot, cold, war, MathF.Max(0, RangeOf(cold, Settings.SightRange)), null, keepSenses: false);
        Candidate(ref scan, target, targetCold);
        return scan.Best == target;
    }

    static bool Free(CharacterTable table, int slot, HashSet<int> taken)
    {
        if (taken.Contains(slot) || !table.Next[slot].Alive) return false;
        byte task = table.Next[slot].Task;
        if (Busy(table.Cold(slot), task)) return false;
        return table.Cold(slot) is { InCombat: false, Fleeing: false, IsImprisoned: false, Fighter.CanAttack: true, Medical.Incapacitated: false, Gunning: null, Ai: null or { Behaviour: not Ai.AiBehaviour.Turret } } cold
            // a carrier fights like anyone (docs/game/carry-loot.md "A carrier in a fight"); a turret guard keeps to its turret (task 146: only a melee attacker takes it off)
            && (!cold.IsPlayer || HuntsEnemies(cold) || HoldsGround(cold));
    }

    /// <summary>
    /// Whether one of the player's characters has Hold's attack on sight: Hold adds goal 16 (standing order 12), whose desire (<c>AI::scoreAttackEnemies</c>) is 0 while sneaking outside a
    /// fight, Passive or not standing. It is a priority-3 goal: the decision step weighs it after the character's orders and before its jobs (<see cref="HoldAttack"/>, docs/game/ai-tasks.md
    /// "Precedence"). Its targets pass the Hold post test (<see cref="Orders.HoldPost"/>).
    /// </summary>
    static bool HoldsGround(CharacterCold? cold) => cold is not null && Orders.Holding(cold) && WantsToAttack(cold);

    /// <summary>Row 16's desire (<c>AI::scoreAttackEnemies</c>) above 0: not Passive, not sneaking outside a fight, standing.</summary>
    static bool WantsToAttack(CharacterCold cold) =>
        (cold.Orders & CharacterOrders.Passive) == 0 && ((cold.Orders & CharacterOrders.Sneak) == 0 || cold.InCombat) && cold.Medical is { Prone: false };

    /// <summary>
    /// Fighting; for a character the decision step does not decide also treating (a medic looks for no fight) or resting (not an AI's sleeper). A player character's look is offered to the step
    /// whatever its task (its orders, goals and jobs decide, docs/game/ai-tasks.md "Precedence").
    /// </summary>
    static bool Busy(CharacterCold? cold, byte task) =>
        task == (byte)CharacterTask.Attack || cold is null || !DecisionSystem.Decides(cold) && (task == (byte)CharacterTask.Treat || task == (byte)CharacterTask.Rest && !AiSleeper(cold));

    /// <summary>
    /// Whether one of the player's characters looks for enemies like an NPC: it has the job 16 "Attacking enemies" and the row's desire (<see cref="WantsToAttack"/>). Whether its
    /// sighting is attacked is the decision step's (<see cref="AttackJob"/>: its orders, goals and higher jobs first).
    /// </summary>
    static bool HuntsEnemies(CharacterCold cold) => WantsToAttack(cold) && Production.PermaJobs.IndexOf(cold, Meitou.Data.Gameplay.Ai.TaskType.ATTACK_ENEMIES) >= 0;

    // ------------------------------------------------------------------ chase give-up and stand down

    /// <summary>No give-up before this many game seconds of a chase: the original's 5 game minutes (<c>ChaseMode</c>, Verified literal; 1 game hour = 1200/11 s).</summary>
    public const float GiveUpGateSeconds = 5f / 60 * 1200 / 11;
    /// <summary>A target unsensed for longer than this is given up: the original's 12 sense-age units of hours x 3600 / 33 x 0.5 (<c>FUN_140852150</c>), i.e. 24 s.</summary>
    public const float GiveUpSenseSeconds = 24;
    /// <summary>A chaser within this of where it last sensed an unsensed target gives it up (the original's squared 3600, Verified literal).</summary>
    public const float GiveUpReach = 60;

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        Sync(table);   // characters spawned this tick
        if (!Settings.AttackOnSight) return;
        var next = table.Next;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive) continue;
            var cold = table.Cold(i)!;
            // The player's characters never give up a chase (the original's gate: the chaser's faction is not the player's); after a fight they are decided again (the decision step).
            if (DecisionSystem.Decides(cold)) continue;
            ref var s = ref slots[i];
            if (n.Task == (byte)CharacterTask.Attack)
            {
                s.Tracked = true;
                Chase(world, table, i, ref s, cold);
                continue;
            }
            s.ChaseStart = -1;
            if (!s.Tracked)
            {
                s.Task = n.Task;   // the routine to go back to after a fight
                continue;
            }
            s.Tracked = false;
            if (cold.Medical is { Incapacitated: true } || cold.InCombat || n.Task != (byte)CharacterTask.Idle) continue;   // down (it wakes into wandering) or busy with something else
            // A follow row whose fight was a step of it follows again under the row (as the decision step does for the characters it decides); else the routine is taken up again as
            // the fight's own end (a step: the provenance stays).
            if (MovementSystem.FightStepOver(table, i, cold) && MovementSystem.FollowAgain(world, i, cold)) continue;
            TaskStart.Stand(table, i);
            TaskStart.Step(table, i, (CharacterTask)(s.Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Follow or (byte)CharacterTask.Idle ? s.Task
                : s.Task == (byte)CharacterTask.GoTo ? (byte)CharacterTask.Idle : (byte)CharacterTask.Wander));
            n.TaskTime = 0;
        }
    }

    /// <summary>
    /// The original's chase give-up (<c>ChaseMode</c>, docs/game/combat.md "Leaving a fight"), read from the chaser's sense entry of its target: after <see cref="GiveUpGateSeconds"/> of a
    /// chase, a target with no entry is given up, and one its last look did not sense when the entry is older than <see cref="GiveUpSenseSeconds"/> or the chaser stands within
    /// <see cref="GiveUpReach"/> of the entry's place. The chaser stops and says so to the target (event 60 GIVE_UP_CHASE). Engine choice: the original tests this only in its path chase
    /// (fight state 11), Meitou in every running chase (<see cref="CharacterCold.CombatRunning"/>), since how the original lets go on open ground is Unknown.
    /// </summary>
    void Chase(World world, CharacterTable table, int i, ref Slot s, CharacterCold cold)
    {
        var next = table.Next;
        if (!cold.CombatRunning || Combat?.StateOf(table.IdOf(i)) is not { TargetSlot: >= 0 and var t } st || t >= table.HighWater || !next[t].Alive
            || next[t].Generation != st.TargetGeneration || table.Cold(t) is { Medical.Incapacitated: true })
        {
            s.ChaseStart = -1;
            return;
        }
        if (s.ChaseStart < 0) s.ChaseStart = world.Tick;
        if ((world.Tick - s.ChaseStart) * world.TickSeconds < GiveUpGateSeconds) return;
        SenseEntry entry = default;
        bool known = s.Senses is { } senses && senses.TryGet(new CharacterId(t, next[t].Generation), out entry);
        if (known && entry.Now) return;
        var here = new Vector2(next[i].Position.X, next[i].Position.Z);
        // Engine choice: a runner within GiveUpReach of the chaser itself has not got away, though a look missed it (a chaser running past it has it at its back); a sneaker has.
        if (Vector2.DistanceSquared(here, new Vector2(next[t].Position.X, next[t].Position.Z)) < GiveUpReach * GiveUpReach && (table.Cold(t)!.Orders & CharacterOrders.Sneak) == 0) return;
        if (known && (world.Tick - entry.Tick) * world.TickSeconds <= GiveUpSenseSeconds && Vector2.DistanceSquared(here, new Vector2(entry.Place.X, entry.Place.Z)) >= GiveUpReach * GiveUpReach) return;
        s.ChaseStart = -1;
        world.Defer(new StopCommand([table.IdOf(i)]) { Tick = world.Tick + 1 });
        world.Defer(new Dialogue.DialogueEvent(table.IdOf(i), table.IdOf(t), 60) { Tick = world.Tick + 1 });
    }

    public void Hash(ref StateHasher hasher)
    {
        hasher.Add(Relations.Changes);
        if (Relations.Changes > 0)
        {
            int n = Relations.Count;
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++) hasher.Add(Relations.Get(a, b));
            for (int a = 0; a < n; a++)
                if (Relations.Rank(a) is var rank and not 0) { hasher.Add(a); hasher.Add(rank); }
            for (int a = 0; a < n; a++)
            {
                if (Relations.GlobalTrust(a) is var trust and not 0) { hasher.Add(a); hasher.Add(trust); }
                for (int b = 0; b < n; b++)
                    if (Relations.AtWar(a, b)) { hasher.Add(a); hasher.Add(b); }
            }
        }
        hasher.Add(size);
        for (int i = 0; i < size; i++)
        {
            ref readonly var s = ref slots[i];
            hasher.Add(s.Generation);
            hasher.Add(s.Tracked);
            hasher.Add(s.Task);
            if (s.ChaseStart >= 0) hasher.Add(s.ChaseStart);   // only while a chase runs
            s.Senses?.Hash(ref hasher);
            if (s.Spots is { } spots)   // only while a looker has meters, so worlds without sneakers hash as before
                foreach (var p in spots)
                {
                    hasher.Add(p.Target);
                    hasher.Add(p.Generation);
                    hasher.Add(p.Value);
                }
            if (s.Heard is { } heard)   // only while a listener remembers a hearing check or an alarm
                foreach (var h in heard)
                {
                    hasher.Add(h.Target);
                    hasher.Add(h.Generation);
                    hasher.Add(h.Checked);
                    hasher.Add(h.AlarmUntil);
                    hasher.Add(h.Heard);
                }
        }
        hasher.Add(Started);
    }
}

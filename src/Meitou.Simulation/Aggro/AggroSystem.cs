using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Weather;

namespace Meitou.Simulation.Aggro;

/// <summary>
/// Settings of the <see cref="AggroSystem"/> (docs/simulation.md "Hostility and relations"; the original's figures in docs/game/perception.md).
/// </summary>
public sealed record AggroSettings
{
    /// <summary>
    /// A character looks round once every this many ticks (staggered by slot: slot s on the ticks where tick % n == s % n). Engine choice: the
    /// original updates the senses of 8 characters per frame in turn (docs/game/perception.md "Update rate").
    /// </summary>
    public int ScanEveryTicks { get; init; } = 15;
    /// <summary>
    /// How far an NPC sees, in world units (dm), before the race's <c>vision range mult</c>: the original's outdoor range (1500,
    /// <c>FUN_140851bc0</c>, Verified), in full daylight; straight ahead only, the view cone shortens it to the side
    /// (<see cref="AggroSystem.Visibility"/>).
    /// </summary>
    public float SightRange { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>
    /// Keep a <see cref="AggroSystem.Log"/> of every fight an NPC's combat goal started (the host prints it; tests read it).
    /// </summary>
    public bool RecordLog { get; init; }
    /// <summary>
    /// Characters attack on sight, and NPCs give chases up and go back to their routine after a fight (false: the look keeps only the senses, for
    /// worlds that script their fights: the sandbox, the duel, the battle).
    /// </summary>
    public bool AttackOnSight { get; init; } = true;
}

/// <summary>
/// A fight an NPC's combat goal started (<see cref="AggroSystem.GoalFight"/>): who attacks whom, the factions and the distance.
/// </summary>
public readonly record struct AggroEntry(long Tick, CharacterId Attacker, CharacterId Target, int AttackerFaction, int TargetFaction, float Distance);

/// <summary>
/// The senses and the attack-on-sight finders (an <b>engine stand-in</b> for the original's <c>SensoryData</c> and the attack goals' finders,
/// docs/game/perception.md "Senses"):
/// <list type="bullet">
/// <item><b>Look</b> (<c>Act</c>, parallel, own slot, reading the last tick's positions through the <see cref="SpatialGrid"/>): every living
/// character on its turn (one in <see cref="AggroSettings.ScanEveryTicks"/>, by slot) senses who it sees (<see cref="SightMap.Sees"/>: places, light
/// and its range R = <see cref="AggroSettings.SightRange"/> x its race's <c>vision range mult</c> times the view-cone factor of
/// <see cref="Visibility"/>) or hears, and keeps the entries, tags, counts and threats. An NPC with a mind that senses a new enemy emits one effect
/// at it (a replan trigger); a player character with the attacking-enemies job or on Hold picks the best enemy by the target score
/// (<see cref="Pick"/>).</item>
/// <item><b>Decide</b> (<c>Apply</c>, serial commit order): an NPC with a mind, and its squad mates within twice their own range R of the target (the
/// original's squad-shared sightings, <c>GroupSense</c>), are asked to think at the next tick (<see cref="Ai.AiMind.Replan"/>): their combat goals
/// decide in the chooser (<see cref="FindFoe"/>). An NPC without a mind starts no fight on sight. The player's characters look only with the
/// attacking-enemies job (<see cref="HuntsEnemies"/>) or on Hold, and are offered the sighting: the decision step starts their fight
/// (<see cref="HoldAttack"/>, <see cref="AttackJob"/>); they have no leash, and after a job's fight go back to following or stand idle.</item>
/// <item><b>Chase give-up and stand down</b> (<c>SlowWorld</c>, serial, every tick): an NPC running after its target
/// (<see cref="CharacterCold.CombatRunning"/>) senses that target on its look cadence (<c>Act</c>) and gives it up by the original's <c>ChaseMode</c>
/// rule (<see cref="GiveUpGateSeconds"/>, <see cref="GiveUpSenseSeconds"/>, <see cref="GiveUpReach"/>). When its fight is over (target down, or
/// stopped) it goes back to what it did before (wander or follow; a go-to becomes idle, which the roaming squads order again).</item>
/// </list>
/// The live relation table is hashed here once a deed has changed it (<see cref="FactionRelations.Changes"/>).
/// </summary>
public sealed class AggroSystem(FactionRelations relations, AggroSettings? settings = null) : ITickSystem, IStateHashed, IDecisionOwner
{
    #region Slots and wiring

    /// <summary>
    /// A character's state in this system, by slot: its generation, the chase and routine it goes back to, its senses, meters and hearing.
    /// </summary>
    struct Slot
    {
        /// <summary>
        /// The generation of the slot's character (-1: none).
        /// </summary>
        public int Generation;
        /// <summary>
        /// Its attack task was seen by the last <see cref="SlowWorld"/> (its fight is watched for its end).
        /// </summary>
        public bool Tracked;
        /// <summary>
        /// The routine to go back to after a fight (<see cref="NoTask"/>: none yet).
        /// </summary>
        public byte Task;
        /// <summary>
        /// The tick the running chase began (-1: none).
        /// </summary>
        public long ChaseStart;
        /// <summary>
        /// The character's senses (null until its first look or forced sense); written by its own partition only, or by <see cref="Force"/> in a
        /// serial phase.
        /// </summary>
        public SenseBlock? Senses;
        /// <summary>
        /// The looker's spot meters on sneaking characters, in target slot order (null: none); written by its own partition only.
        /// </summary>
        public List<Spot>? Spots;
        /// <summary>
        /// The listener's hearing cooldowns and alarms, in target slot order (null: none); written by its own partition only, or by
        /// <see cref="Alarm"/> in a serial phase.
        /// </summary>
        public List<HeardPair>? Heard;
    }

    /// <summary>
    /// No routine kept (<see cref="Slot.Task"/>).
    /// </summary>
    const byte NoTask = 0xFF;

    /// <summary>
    /// The slots, by character slot (grown to the table, <see cref="GrowSlots"/>).
    /// </summary>
    Slot[] _slots = [];

    /// <summary>
    /// The slots in use: one past the highest character slot seen, which the state hash covers.
    /// </summary>
    int _size;

    /// <summary>
    /// The tick's census for the looks (<see cref="Sync"/>): the player's characters, the fighters, the sneakers and the factions with an enemy about.
    /// </summary>
    readonly LookCensus _census = new();

    /// <summary>
    /// The settings (the defaults when none were given).
    /// </summary>
    public AggroSettings Settings { get; } = settings ?? new AggroSettings();

    /// <summary>
    /// The live relation table the looks and finders read.
    /// </summary>
    public FactionRelations Relations { get; } = relations;

    /// <summary>
    /// The fights this system started so far.
    /// </summary>
    public long Started { get; private set; }

    /// <summary>
    /// The fights: the target score and the chase give-up (null: none; the look's goal finders then read the world's combat system).
    /// </summary>
    public CombatSystem? Combat { get; set; }

    /// <summary>
    /// The towns, for 144's home circle (<see cref="FoeFinder.Territory"/>; null: no circle, it attacks on sight).
    /// </summary>
    public PopulationData? Towns { get; set; }

    /// <summary>
    /// The nests' squads, for a nest's 144 circle (<see cref="FoeFinder.Territory"/>; null: none).
    /// </summary>
    public PopulationSystem? Population { get; set; }

    /// <summary>
    /// The squads' memories (the dialogue system's): a squad's short-term TEMP_ENEMY tag on a character of another faction makes it an enemy of every
    /// member whatever the relations say, and TEMP_ALLY unmakes an enemy (<c>FUN_14079c950</c>, docs/game/dialogue.md "Squad memory"). Null:
    /// relations only.
    /// </summary>
    public Dialogue.SquadMemory? Memory { get; set; }

    /// <summary>
    /// Whether a character's dialogue has a sight line (the dialogue system's <see cref="Dialogue.DialogueSystem.HasSightLines"/>, the original's
    /// Dialogue <c>+0x28</c> flag): a looker without one sets no bandit fight tag (<see cref="BanditFightTag"/>). Null: every looker has one.
    /// </summary>
    public Func<World, int, bool>? SightLines { get; set; }

    /// <summary>
    /// Brings the slots and the census up to the table before the looks (<see cref="Sync"/>).
    /// </summary>
    public void Schedule(World world) => Sync(world.Characters);

    /// <summary>
    /// Brings the slots up to the table (a new or gone character's slot starts again) and takes the tick's census for the looks.
    /// </summary>
    void Sync(CharacterTable table)
    {
        int n = table.HighWater;
        GrowSlots(n);
        _size = Math.Max(_size, n);
        var next = table.Next;
        _census.Begin(Relations.Count);

        for (int i = 0; i < n; i++)
        {
            int generation = next[i].Alive ? next[i].Generation : -1;
            if (_slots[i].Generation != generation)
                _slots[i] = new Slot { Generation = generation, Task = NoTask, ChaseStart = -1 };

            if (generation >= 0) _census.Add(table, i, Relations);
        }

        // Which factions have an enemy among the factions present: a peaceful crowd skips its looks. Worked out again only when the set changes.
        _census.Settle(Relations);
    }

    /// <summary>
    /// Grows the slots to hold <paramref name="n"/> characters (doubling), the new ones empty.
    /// </summary>
    void GrowSlots(int n)
    {
        if (_slots.Length >= n) return;

        int old = _slots.Length;
        Array.Resize(ref _slots, Math.Max(n, _slots.Length * 2));
        for (int i = old; i < _slots.Length; i++) _slots[i] = new Slot { Generation = -1, Task = NoTask, ChaseStart = -1 };
    }

    /// <summary>
    /// The senses of the character in <paramref name="slot"/> as of its last look (null: none yet). Read after this system's <c>Act</c>, or by a
    /// system whose <c>Act</c> runs before it.
    /// </summary>
    public SenseBlock? SensesOf(int slot) =>
        (uint)slot < (uint)_slots.Length && _slots[slot].Senses is { } b && _slots[slot].Generation >= 0 ? b : null;

    #endregion

    #region Sight and hearing tests

    /// <summary>
    /// The original's vision range outdoors before the race multiplier (<c>FUN_140851bc0</c>: race <c>vision range mult</c> x 1500; 500 flat inside a
    /// building).
    /// </summary>
    public const float OutdoorSight = 1500;

    /// <summary>
    /// The edge of the original's view cone: the cosine of the angle off the facing at which the view factor reaches 0 (about 78.5 degrees;
    /// <c>FUN_140852f70</c>).
    /// </summary>
    public const float ConeEdge = 0.2f;

    /// <summary>
    /// The smallest view factor that counts as seen (<c>FUN_140857b10</c>: above 0.01).
    /// </summary>
    public const float MinVisibility = 0.01f;

    /// <summary>
    /// The noise a moving character makes reaches lerp(speed / max speed, 350, 600) units (<c>FUN_140851d10</c>, Verified constants; clear weather:
    /// the caller applies <see cref="WeatherSystem.NoiseFactor"/>; not sneaking).
    /// </summary>
    public const float NoiseNear = 350, NoiseFar = 600;

    /// <summary>
    /// The chance per check that a character within the noise range is heard when it is not sneaking (<c>FUN_140857500</c>, Verified constant).
    /// </summary>
    public const float HearOdds = 0.75f;

    /// <summary>
    /// Two characters both of the player's side hear each other within this distance, whatever else (<c>FUN_140857500</c> over <c>FUN_140856bc0</c>,
    /// Verified constant).
    /// </summary>
    public const float PlayerHearRange = 1000;

    /// <summary>
    /// The factor on the noise range an awake listener hears (<c>FUN_140857500</c>: x0.5 while StateBroadcast <c>+0xb4</c> is 0, Verified constant).
    /// </summary>
    public const float AwakeHearing = 0.5f;

    /// <summary>
    /// The original's sight test for an NPC looking at a character who is not sneaking or crawling, in full daylight with both lit alike
    /// (<c>FUN_140852f70</c>): the view factor v = (cos a - 0.2) / 0.8, a the angle between the looker's facing and the direction to the target (3D),
    /// and the target is seen when v is above 0.01 and the distance is at most R x v (v x <paramref name="posture"/>: 0.5 for a target crawling or on
    /// the ground, <c>FUN_140852f70</c>). Returns v when seen, else 0. A target at the looker's own position counts as seen straight ahead (an engine
    /// choice).
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
    /// The sight test for a sneaking target: the view factor v of <see cref="Visibility"/>'s cone, scaled by <see cref="Orders.SneakView"/>, must
    /// reach the distance (d at most R x v'). Returns v' when it does, else 0; in view, the target still has to fill the looker's spot meter
    /// (<see cref="Orders.SpotSeconds"/>).
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

    /// <summary>
    /// A character's daylight sight range R: <paramref name="sight"/> x its race's <c>vision range mult</c> x the weather's factor
    /// (<see cref="WeatherSystem.SightFactor"/>).
    /// </summary>
    public static float RangeOf(CharacterCold cold, float sight) => sight * (cold.Race?.VisionRangeMult ?? 1) * WeatherSystem.SightFactor(cold);

    /// <summary>
    /// The weather's factor on the noise of the character in <paramref name="slot"/> (<see cref="WeatherSystem.NoiseFactor"/>; 1 without a cold row).
    /// </summary>
    static float Noise(CharacterTable table, int slot) => table.Cold(slot) is { } c ? WeatherSystem.NoiseFactor(c) : 1;

    /// <summary>
    /// How far a character at <paramref name="speed"/> (its top speed <paramref name="maxSpeed"/>) is heard when it does not sneak: 350 at rest (a
    /// fighter standing still is still heard), 600 at full speed. Whether a character is heard at all (moving or in combat mode) is the caller's
    /// test.
    /// </summary>
    public static float NoiseRange(float speed, float maxSpeed)
    {
        float t = maxSpeed > 0 ? Math.Clamp(speed / maxSpeed, 0, 1) : 1;
        return NoiseNear + (NoiseFar - NoiseNear) * t;
    }

    /// <summary>
    /// The original's chance to hear a character at <paramref name="distance"/> with noise range <paramref name="noise"/>: 0.75 x (1 - d^2 / N^2),
    /// rolled each check.
    /// </summary>
    public static float HearChance(float distance, float noise) =>
        noise > 0 && distance < noise ? HearOdds * (1 - distance * distance / (noise * noise)) : 0;

    /// <summary>
    /// Of the player's side (the original's <c>isWithThePlayer</c> <c>FUN_140856bc0</c>): the player's faction. Engine: its owner path (a slave's or
    /// pet's) is not modelled.
    /// </summary>
    static bool PlayerSide(World world, CharacterCold? c) =>
        c is not null && (c.IsPlayer || world.Player.Faction >= 0 && c.Faction == world.Player.Faction);

    /// <summary>
    /// Whether <paramref name="listener"/> hears <paramref name="target"/> this look (<c>SensoryData::canHear</c> <c>FUN_140857500</c>,
    /// docs/game/perception.md "Hearing"), asked only when sight failed. Both of the player's side: heard within <see cref="PlayerHearRange"/>,
    /// nothing else. Else a target neither moving nor in combat mode is not heard; the building rules (<see cref="HearPlace"/>) scale the noise
    /// range, halved for an awake listener (the original's StateBroadcast <c>+0xb4</c> 0; a sleeper, <see cref="Carrying.CarrySystem.Asleep"/>, hears
    /// at full range); a sneaking target makes the sneaking noise, any other the ordinary one (350 at rest); in combat mode in the listener's place
    /// and floor any chance is a sure hearing, else <paramref name="roll"/> (the look's hash) is rolled against it, and a sleeper drops what it heard
    /// on a coin flip, then saying <see cref="AlmostWokeUpEvent"/> when the target is a sneaking player-side character (emitted as
    /// <see cref="AlmostWokeEffect"/>). After the building rules a pair checked less than <see cref="HearCooldownSeconds"/> ago gets the last answer
    /// (<paramref name="heard"/>, the listener's own list), and an alarmed pair doubles the chance. <paramref name="stealth"/>: the target's stealth
    /// when it sneaks, else below 0.
    /// </summary>
    static bool Hears(World world, CharacterTable table, int listener, int target, float stealth, ulong roll, EffectBuffer effects,
        ref List<HeardPair>? heard, out bool xpCheck)
    {
        xpCheck = false;
        ref readonly var me = ref table.Previous[listener];
        ref readonly var other = ref table.Previous[target];
        var tc = table.Cold(target);
        float d2 = Vector3.DistanceSquared(me.Position, other.Position);
        if (PlayerSide(world, table.Cold(listener)) && PlayerSide(world, tc))
            return d2 < PlayerHearRange * PlayerHearRange;

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

        // The XP step of the check (FUN_1408c7330) is reached by a mover that is not in combat mode, or one staying low or crippled (posture 1 or 2,
        // the heard-ability gate FUN_1406643d0).
        bool xpGate = tc is not null && (speed > 0.01f && !combat || tc.Medical is { Prone: true });
        pair.Heard = Roll(world, table, listener, target, stealth, roll, effects, d2, speed, place, sure, world.Tick < pair.AlarmUntil, xpGate,
            out xpCheck);
        if (k >= 0) heard![k] = pair;
        else AddHeard(ref heard, pair);

        return pair.Heard;
    }

    /// <summary>
    /// The noise range and the roll of <see cref="Hears"/>, past its gates and cooldown. Every roll that gets to the end of the original's check (a
    /// sure hearing returns before) with <paramref name="xpGate"/> set sets <paramref name="xpCheck"/>: the caller emits a
    /// <see cref="HearingXpEffect"/> (<c>FUN_1408c7330</c>, <see cref="Apply"/>).
    /// </summary>
    static bool Roll(World world, CharacterTable table, int listener, int target, float stealth, ulong roll, EffectBuffer effects, float d2,
        float speed, float place, bool sure, bool alarmed, bool xpGate,
        out bool xpCheck)
    {
        xpCheck = xpGate;
        ref readonly var other = ref table.Previous[target];
        bool asleep = Carrying.CarrySystem.Asleep(table, listener);
        if (!asleep) place *= AwakeHearing;

        float d = MathF.Sqrt(d2);
        float p = stealth >= 0
            ? Orders.SneakHearChance(d, Orders.SneakNoise(stealth, Noise(table, target)) * place, stealth, !asleep)
            : HearChance(d, NoiseRange(speed, other.MaxSpeed) * Noise(table, target) * place);
        if (alarmed) p *= AlarmHearing;
        if (!(p > 0)) return false;

        if (sure)
        {
            xpCheck = false;
            return true;
        }

        if (Rng.Float(roll) >= p) return false;
        if (!asleep || Rng.Float(Rng.Mix(roll + 1)) >= 0.5f) return true;
        if (stealth >= 0 && PlayerSide(world, table.Cold(target))) effects.Emit(listener, target, AlmostWokeEffect, 0);

        return false;
    }

    /// <summary>
    /// The building rules of the hearing check (<c>SensoryData::canHear</c> <c>FUN_140857500</c> steps 3, 4, 6 and 8, <b>Verified</b>): the factor on
    /// the target's noise range, 0 when it cannot be heard. Inside another building than the listener, or on another floor of the same one, it is
    /// heard only in combat mode; from one building into another never; across floors at most one apart. Another interior halves the range, and again
    /// when the building (the target's, else the listener's) has no open door. <paramref name="sure"/>: in combat mode in the listener's place and on
    /// its floor, heard whenever the chance is above 0. Engine: the floor test skips the original's cavernous-building exception.
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

        // Each character's floor is kept by the sight map (the same last-tick positions): asking the building for both at every pair in a crowd was
        // most of a tick.
        if (a >= 0 && sight.FloorOf(listener, listenerAt) is var fa && sight.FloorOf(target, targetAt) is var fb && fa != fb)
            return combat && Math.Abs(fa - fb) <= 1 ? 1 : 0;

        sure = combat;
        return 1;
    }

    #endregion

    #region Hearing memory

    /// <summary>
    /// How long a listener keeps its last answer on a target before it checks again (<c>FUN_140857500</c>'s per-target time map: 2 s of 1x game time
    /// divided by the game speed). Engine: 2 s of simulated time, the speed not applied.
    /// </summary>
    public const float HearCooldownSeconds = 2;

    /// <summary>
    /// The hearing cooldown in ticks (<see cref="HearCooldownSeconds"/>).
    /// </summary>
    static long CooldownTicks(World world) => (long)MathF.Round(HearCooldownSeconds / world.TickSeconds);

    /// <summary>
    /// How long a pair stays alarmed (<c>FUN_140858f40</c> sets the sense entry's timer to 5 s, Verified) and the factor on the hearing chance
    /// meanwhile (x2, Verified).
    /// </summary>
    public const float AlarmSeconds = 5, AlarmHearing = 2;

    /// <summary>
    /// A listener's hearing memory of one target: the tick of its last full check (-1: none), its answer, and the tick its alarm runs out.
    /// </summary>
    struct HeardPair
    {
        /// <summary>
        /// The heard character's slot and generation.
        /// </summary>
        public int Target, Generation;
        /// <summary>
        /// The tick of the last full check (-1: none) and the tick the alarm runs out.
        /// </summary>
        public long Checked, AlarmUntil;
        /// <summary>
        /// The last full check's answer.
        /// </summary>
        public bool Heard;
    }

    /// <summary>
    /// Drops the pairs whose target is gone or that neither hold an answer within the cooldown nor an alarm (null when none is left).
    /// </summary>
    static List<HeardPair>? PruneHeard(List<HeardPair> heard, World world)
    {
        long cooldown = CooldownTicks(world);
        var prev = world.Characters.Previous;

        for (int k = heard.Count - 1; k >= 0; k--)
        {
            var x = heard[k];

            if (!prev[x.Target].Alive || prev[x.Target].Generation != x.Generation
                || (x.Checked < 0 || world.Tick - x.Checked >= cooldown) && world.Tick >= x.AlarmUntil)
            {
                heard.RemoveAt(k);
            }
        }

        return heard.Count > 0 ? heard : null;
    }

    /// <summary>
    /// The pair of <paramref name="target"/> (its generation) in the listener's list, kept in target slot order; -1 when none.
    /// </summary>
    static int FindHeard(List<HeardPair>? heard, int target, int generation)
    {
        if (heard is null) return -1;

        int k = HeardBound(heard, target);
        return k < heard.Count && heard[k].Target == target && heard[k].Generation == generation ? k : -1;
    }

    /// <summary>
    /// The first pair at or after <paramref name="target"/> in the slot-ordered list (binary search).
    /// </summary>
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

    /// <summary>
    /// Puts a new pair in slot order, over a stale one of the same slot.
    /// </summary>
    static void AddHeard(ref List<HeardPair>? heard, in HeardPair pair)
    {
        heard ??= [];
        int k = HeardBound(heard, pair.Target);
        if (k < heard.Count && heard[k].Target == pair.Target) heard[k] = pair;
        else heard.Insert(k, pair);
    }

    /// <summary>
    /// Alarms <paramref name="listener"/> about <paramref name="target"/> for <see cref="AlarmSeconds"/> (<c>FUN_140858f40</c> with its alarm flag,
    /// which the perceive-and-tag step <c>FUN_1405cb0a0</c> of a massive alarm passes): it hears that target twice as easily meanwhile. Serial phases
    /// only.
    /// </summary>
    public void Alarm(World world, CharacterId listener, CharacterId target)
    {
        var prev = world.Characters.Previous;

        if (listener.Slot >= _slots.Length || _slots[listener.Slot].Generation != listener.Generation || target.Slot >= world.Characters.HighWater
            || prev[target.Slot].Generation != target.Generation)
        {
            return;
        }

        ref var heard = ref _slots[listener.Slot].Heard;
        long until = world.Tick + (long)MathF.Round(AlarmSeconds / world.TickSeconds);
        int k = FindHeard(heard, target.Slot, target.Generation);
        if (k >= 0) heard![k] = heard[k] with { AlarmUntil = until };
        else AddHeard(ref heard, new HeardPair { Target = target.Slot, Generation = target.Generation, Checked = -1, AlarmUntil = until });

        Notice(world, listener, target);
    }

    #endregion

    #region Spot meters

    /// <summary>
    /// One spot meter (the original's <c>SensoryData::SpottingPeopleMgr::Spot</c>): it fills by dt / <see cref="Orders.SpotSeconds"/> while the
    /// looker sees the sneaking target, empties by dt / <see cref="Orders.SpotForgetSeconds"/> while it does not, and is dropped at 0. Above 1 the
    /// target counts as seen.
    /// </summary>
    struct Spot
    {
        /// <summary>
        /// The sneaker's slot and generation.
        /// </summary>
        public int Target, Generation;
        /// <summary>
        /// The meter: above 1 the target counts as seen.
        /// </summary>
        public float Value;
    }

    /// <summary>
    /// A sneaking target the looker sees this scan, with its spot time.
    /// </summary>
    readonly record struct Sighting(int Target, int Generation, float Seconds);

    /// <summary>
    /// The worker thread's list of the sneaking targets a look sees (made on its first use).
    /// </summary>
    [ThreadStatic] static List<Sighting>? s_sightings;

    /// <summary>
    /// Moves a looker's spot meters on by one scan of <paramref name="dt"/> seconds: a seen sneaker's meter fills by dt / its spot time (a new one
    /// starts at 0), the others empty by dt / <see cref="Orders.SpotForgetSeconds"/>; empty meters and gone targets are dropped. Returns the list in
    /// target slot order (null when empty).
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

            if (!table.Previous[p.Target].Alive || table.Previous[p.Target].Generation != p.Generation)
            {
                spots.RemoveAt(k);
                continue;
            }

            if (seen.Exists(x => x.Target == p.Target)) continue;

            p.Value -= dt / Orders.SpotForgetSeconds;
            if (p.Value <= 0) spots.RemoveAt(k);
            else spots[k] = p;
        }

        if (spots.Count == 0) return null;

        spots.Sort((a, b) => a.Target.CompareTo(b.Target));
        return spots;
    }

    /// <summary>
    /// Whether the NPC in slot <paramref name="looker"/> has spotted the sneaker <paramref name="target"/> (its spot meter is full; serial phases).
    /// </summary>
    public bool Spotted(int looker, CharacterId target) => looker < _slots.Length && SpotOf(_slots[looker].Spots, target.Slot, target.Generation) > 1;

    /// <summary>
    /// Whether the sneaker <paramref name="target"/> is unseen (Character +0xe8 = 1, docs/game/perception.md "Spotting a sneaker"): not fighting, and
    /// no looker's spot meter on it is filling or full (red or yellow); serial phases.
    /// </summary>
    public bool Unseen(CharacterTable table, CharacterId target)
    {
        if (table.Cold(target.Slot) is { InCombat: true }) return false;
        for (int k = 0; k < _size && k < _slots.Length; k++)
            if (SpotOf(_slots[k].Spots, target.Slot, target.Generation) > 0) return false;

        return true;
    }

    /// <summary>
    /// The looker's spot meter on <paramref name="slot"/> before this scan (0: none).
    /// </summary>
    static float SpotOf(List<Spot>? spots, int slot, int generation)
    {
        if (spots is null) return 0;
        foreach (var p in spots) if (p.Target == slot && p.Generation == generation) return p.Value;

        return 0;
    }

    /// <summary>
    /// Whether this look already saw the sneaker in <paramref name="slot"/>.
    /// </summary>
    static bool Sighted(List<Sighting> list, int slot)
    {
        foreach (var x in list) if (x.Target == slot) return true;

        return false;
    }

    #endregion

    #region Look

    /// <summary>
    /// One look (or a finder's or a forced sense's read of the senses): the looker, what it reads, and the best target found so far.
    /// </summary>
    struct Scan
    {
        /// <summary>
        /// The characters (last tick's positions in <c>Previous</c>).
        /// </summary>
        public CharacterTable Table;
        /// <summary>
        /// The relation table.
        /// </summary>
        public FactionRelations Relations;
        /// <summary>
        /// The world looked at.
        /// </summary>
        public World World;
        /// <summary>
        /// The squads' memories (<see cref="AggroSystem.Memory"/>).
        /// </summary>
        public Dialogue.SquadMemory? Memory;
        /// <summary>
        /// Whether a character's dialogue has a sight line (<see cref="AggroSystem.SightLines"/>; null: every one has).
        /// </summary>
        public Func<World, int, bool>? SightLines;
        /// <summary>
        /// The looker's <see cref="SightLines"/> answer once asked: 0 not yet, 1 yes, -1 no.
        /// </summary>
        public sbyte LookerSees;
        /// <summary>
        /// The looker's slot, faction and squad.
        /// </summary>
        public int Self, Faction, Squad;
        /// <summary>
        /// Where the looker stands (last tick).
        /// </summary>
        public Vector3 Eye;
        /// <summary>
        /// The looker's cold row.
        /// </summary>
        public CharacterCold? Cold;
        /// <summary>
        /// The farthest the looker can see anyone (<see cref="SightMap.MaxRange"/>).
        /// </summary>
        public float Range;
        /// <summary>
        /// The looker's outdoor sight (race multiplier applied) for the places and lights (<see cref="SightMap.Sees"/>); <see cref="Range"/> is the
        /// farthest it can see anyone.
        /// </summary>
        public float Outdoor;
        /// <summary>
        /// The places, lights and floors of the sight test.
        /// </summary>
        public SightMap Sight;
        /// <summary>
        /// The world seed, the looker's RNG key and the tick, for the hearing rolls.
        /// </summary>
        public ulong Seed, Key, Tick;
        /// <summary>
        /// A campaign squad's assault targets (<see cref="Squad.War"/>; none for others).
        /// </summary>
        public WarTargets War;
        /// <summary>
        /// The towns, for the assault's town test (<see cref="AggroSystem.Towns"/>; null: nobody is in a town).
        /// </summary>
        public PopulationData? Towns;
        /// <summary>
        /// The faction a hired squad works for (<c>Contract_HiredAlly</c> 12, its employer's) or defends (<c>Contract_DefendPlayerBase</c> 15, the
        /// hirer's); -1: none. Its members are never enemies, nor, hired, its allies.
        /// </summary>
        public int Employer;
        /// <summary>
        /// The employer's allies are never enemies either (a hired ally).
        /// </summary>
        public bool EmployerAllies;
        /// <summary>
        /// Seeing the employer's faction tags the character TEMP ALLY in the squad's memory (the hired ally's and the defend-base contract's sighting
        /// hooks).
        /// </summary>
        public bool EmployerTags;
        /// <summary>
        /// The territorial circle (R2 0: none, -1: contains nobody).
        /// </summary>
        public (Vector2 Centre, float R2) Home;
        /// <summary>
        /// The best target so far (-1: none).
        /// </summary>
        public int Best;
        /// <summary>
        /// The best target's squared distance.
        /// </summary>
        public float BestD2;
        /// <summary>
        /// The best target's score (<see cref="Combat.TargetScore"/>).
        /// </summary>
        public float BestScore;
        /// <summary>
        /// The combat state the score reads (null: hand-built worlds without one, every candidate counts as unfought).
        /// </summary>
        public CombatSystem? Combat;
        /// <summary>
        /// The fights the senses read (threats, who fights whom): <see cref="Combat"/>, else the world's combat system.
        /// </summary>
        public CombatSystem? Fights;
        /// <summary>
        /// The looker: one of the player's, on Hold (and its hold point), crawling, an animal.
        /// </summary>
        public bool Player, Hold, HoldPost, Prone, Animal;
        /// <summary>
        /// The looker's hold point (<see cref="Orders.HoldPointOf"/>).
        /// </summary>
        public Vector3 HoldPoint;
        /// <summary>
        /// The looker's spot meters (read only during the look) and the sneakers it sees this look.
        /// </summary>
        public List<Spot>? Spots;
        /// <summary>
        /// The looker's hearing memory (<see cref="Slot.Heard"/>), written during the look and stored back after it.
        /// </summary>
        public List<HeardPair>? Heard;
        /// <summary>
        /// The sneakers seen this look (the thread's list; null for a forced sense).
        /// </summary>
        public List<Sighting>? Sightings;
        /// <summary>
        /// The look's effects (null for a forced sense: no roll, meter or hook).
        /// </summary>
        public EffectBuffer? Effects;
        /// <summary>
        /// The looker keeps spot meters this look (it may pick fights, or it is an NPC watching for sneakers): sneakers in view fill them and are
        /// exposed.
        /// </summary>
        public bool Meters;
        /// <summary>
        /// The looker's senses, written by this look.
        /// </summary>
        public SenseBlock Block;
        /// <summary>
        /// The looker's own fight target (-1: none).
        /// </summary>
        public int FightTarget;
        /// <summary>
        /// New characters gathered this look (<see cref="GatherCap"/>, <see cref="GatherExtra"/>).
        /// </summary>
        public int Gathered;
        /// <summary>
        /// The player's characters this tick (<see cref="VisitKin"/>); null for a forced sense.
        /// </summary>
        public List<int>? PlayerSlots;
        /// <summary>
        /// The characters in a fight this tick (<see cref="VisitFighters"/>); null for a forced sense.
        /// </summary>
        public List<int>? FighterSlots;
        /// <summary>
        /// This look also gathers beyond <see cref="GatherNear"/> (<see cref="FarLooks"/>), and the gather is in that far pass.
        /// </summary>
        public bool Far, FarPass;
        /// <summary>
        /// This look works the entries' tags out in full (every <see cref="FarLooks"/>-th look and a forced sense; the fights and the KO and
        /// imprisoned bits are read at every look).
        /// </summary>
        public bool FullTags;
        /// <summary>
        /// Which attack goal's finder takes a candidate (<see cref="FindFoe"/>; the look's attack on sight is 16's).
        /// </summary>
        public FoeFinder Finder;
        /// <summary>
        /// Fights started this tick by target, counted as attackers in the score (<see cref="FindFoe"/> from the AI's serial step; null: none).
        /// </summary>
        public IReadOnlyDictionary<int, int>? Pending;
        /// <summary>
        /// An enemy-tagged character the look gathered as a new entry (-1: none), the replan trigger of a fighter the AI runs.
        /// </summary>
        public int NewEnemy;
    }

    /// <summary>
    /// The gather's cap (engine choice; the original's <c>FUN_14085a8d0</c> has none, <b>Verified</b>): up to <see cref="GatherCap"/> +
    /// <see cref="GatherExtra"/> new characters within <see cref="GatherNear"/>, then, while fewer than <see cref="GatherCap"/> were taken, more
    /// within the look's reach.
    /// </summary>
    public const int GatherCap = 40, GatherExtra = 20;

    /// <summary>
    /// The near pass of the gather (<see cref="GatherCap"/>).
    /// </summary>
    public const float GatherNear = 250;

    /// <summary>
    /// A looker that neither may pick a fight nor fights gathers beyond <see cref="GatherNear"/> only every this many looks, and works its entries'
    /// tags out in full as often (engine choice, for the cost of the look in crowds: its entries are sensed at every look, only new far characters
    /// and relation changes wait).
    /// </summary>
    public const int FarLooks = 4;

    /// <summary>
    /// The factor on a home town's radius of the territorial circle (<see cref="Territory"/>).
    /// </summary>
    const float TerritoryScale = 1.3f;

    /// <summary>
    /// The look of every living character on its turn (one in <see cref="AggroSettings.ScanEveryTicks"/>, by slot): its senses (<see cref="Look"/>),
    /// then, for one that may pick a fight, the attack on sight (<see cref="Pick"/>), emitted as a <see cref="SightEffect"/>. Writes only the
    /// looker's own slot.
    /// </summary>
    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int every = world.Every(Settings.ScanEveryTicks);

        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || prev[i].Dormant || i >= _slots.Length || !world.Turn(i, every))
                continue;   // its turn (the far cadence, World.Turn)

            if (_slots[i].Heard is { } heard) _slots[i].Heard = PruneHeard(heard, world);
            if (table.Cold(i) is not { Medical: not null } cold) continue;

            LookOnTurn(world, i, cold, every, effects);
        }
    }

    /// <summary>
    /// One character's look on its turn: its senses (faded, or sensed again and gathered), then its attack on sight or replan trigger, and its spot
    /// meters.
    /// </summary>
    void LookOnTurn(World world, int i, CharacterCold cold, int every, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        byte task = next[i].Task;
        var war = cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { War.IsSet: true } assaulting ? assaulting.War : WarTargets.None;

        // One that could pick a fight now: free, able, of a faction with an enemy about (or at war, or with a TEMP_ENEMY tag of its squad).
        bool looks = Settings.AttackOnSight && !Busy(cold, task)
            && cold is { InCombat: false, Fleeing: false, IsImprisoned: false, SlaveState: not 1, Fighter.CanAttack: true,
                Medical.Incapacitated: false }
            && Relations.Contains(cold.Faction) && (_census.HasEnemy(cold.Faction) || war.IsSet || (Memory?.AnyTempEnemy(cold.SquadId) ?? false));

        // The player's characters attack on sight with the job or Hold's goal (offered to the decision step); an NPC decides by its own goals in
        // the chooser.
        bool targets = looks && cold.IsPlayer && (HuntsEnemies(cold) || HoldsGround(cold));
        var scan = NewScan(world, i, cold, war, MathF.Max(0, RangeOf(cold, Settings.SightRange)), effects);

        // A carried or knocked-out character only lets its senses fade (FUN_1405d00b0 runs FUN_14050c7c0 instead of the AI update when the get-up
        // test FUN_140598c20 fails): no gather, no sighting. One medically awake but lying (playing dead, or its knockout about to end) looks, so
        // the get-up test reads its enemies and allies (docs/game/ragdolls.md "Playing dead").
        if (!cold.CarriedBy.IsNone || cold.Medical!.Incapacitated && !AwakeLying(world, i, cold, every))
        {
            Fade(ref scan);
            return;
        }

        // Every awake NPC keeps spot meters on the sneakers it sees, not only those it would fight (the original's per-looker SpottingPeopleMgr):
        // they feed the crime witnesses, the stealth knock-out and the sneaker's arrows.
        bool picks = targets || looks && cold.Ai is not null;
        scan.Meters = _census.AnySneakers && (picks || Watches(cold, task) && _census.SneakerWithin(prev[i].Position, scan.Range));
        scan.Sightings!.Clear();
        scan.FullTags = (world.Tick / every + i) % FarLooks == 0;
        scan.Far = picks || cold.InCombat || scan.FullTags;
        Look(ref scan);
        _slots[i].Heard = scan.Heard;
        if (cold.Medical.Incapacitated) return;   // lying: senses only, no fight to pick

        if (targets)
        {
            Pick(ref scan);
            if (scan.Best >= 0) effects.Emit(i, scan.Best, SightEffect, MathF.Sqrt(scan.BestD2));
        }

        // An NPC with a mind that senses a new enemy thinks again, and its squad mates with it (the sighting is shared): a replan trigger (engine
        // stand-in), whatever its goals; a fighter only in its own fight (in another, the AI weighs nothing but the run).
        else if (scan.NewEnemy >= 0 && cold.Ai is not null && (cold.InCombat ? AiSystem.Holds(next[i], cold) : looks))
            effects.Emit(i, scan.NewEnemy, SightEffect, Vector3.Distance(prev[i].Position, prev[scan.NewEnemy].Position));

        if (scan.Meters && (scan.Spots is not null || scan.Sightings!.Count > 0))
            _slots[i].Spots = AdvanceSpots(scan.Spots, scan.Sightings!, every * world.TickSeconds, table);
    }

    /// <summary>
    /// An NPC that keeps spot meters without picking fights: awake, not fighting, not held (carried, caged, in bed or asleep), not attacking or
    /// treating.
    /// </summary>
    static bool Watches(CharacterCold c, byte task) =>
        c is { IsPlayer: false, InCombat: false, IsImprisoned: false, Medical.Incapacitated: false } && c.CarriedBy.IsNone
            && c.InSomething != Locks.HeldState.Bed
        && c.Ai is not { Behaviour: AiBehaviour.Sleep } && task is not ((byte)CharacterTask.Attack or (byte)CharacterTask.Treat);

    /// <summary>
    /// A downed character the original's sense gate lets look (<c>FUN_1405d00b0</c>: the AI update runs once the medical get-up test passes): one
    /// playing dead, or (engine choice, as <see cref="BodySystem.Schedule"/> decides on the waking tick from the last look) one whose knockout ends
    /// within two looks or is held down until it looks (<see cref="MedicalContext.GetUpUnasked"/>).
    /// </summary>
    static bool AwakeLying(World world, int slot, CharacterCold cold, int every)
    {
        var medical = cold.Medical!;
        if (medical.Dead || cold.Animal is not null) return false;

        return medical.PlayingDead || medical.WakesWithin((2 * every + world.StepTicks(slot)) * world.TickSeconds + cold.MedicalBudgetSeconds);
    }

    /// <summary>
    /// A scan of the looker in <paramref name="i"/> with outdoor sight <paramref name="outdoor"/>; <paramref name="keepSenses"/> false reads a
    /// block it does not keep.
    /// </summary>
    Scan NewScan(World world, int i, CharacterCold cold, WarTargets war, float outdoor, EffectBuffer? effects, bool keepSenses = true,
        FoeFinder finder = FoeFinder.Enemies)
    {
        var prev = world.Characters.Previous;
        return new Scan
        {
            Table = world.Characters,
            Relations = Relations,
            World = world,
            Memory = Memory,
            SightLines = SightLines,
            Self = i,
            Faction = cold.Faction,
            Squad = cold.SquadId,
            Eye = prev[i].Position,
            Cold = cold,
            Range = world.Sight.MaxRange(i, outdoor),
            Outdoor = outdoor,
            Sight = world.Sight,
            Best = -1,
            BestD2 = float.MaxValue,
            NewEnemy = -1,
            BestScore = float.NegativeInfinity,
            Combat = Combat,
            Fights = Combat ?? world.SystemOf<CombatSystem>(),
            Player = cold.IsPlayer,
            Hold = Orders.Holding(cold) && cold.Fighter?.Crossbow is null,
            HoldPost = Orders.Holding(cold) && !Orders.RangedExempt(cold),
            Prone = cold.Medical?.Prone == true,
            Animal = cold.Fighter?.IsAnimal == true,
            HoldPoint = Orders.HoldPointOf(cold, prev[i].Position),
            Seed = world.Seed,
            Key = Rng.Key(new CharacterId(i, prev[i].Generation)),
            Tick = (ulong)world.Tick,
            Finder = finder,
            War = war,
            Towns = Towns,
            PlayerSlots = _census.PlayerSlots,
            FighterSlots = _census.FighterSlots,
            Employer = EmployerOf(world, cold, out bool allies, out bool defending),
            EmployerAllies = allies,
            EmployerTags = allies || defending,
            Home = finder == FoeFinder.Territory ? Territory(cold) : default,
            Spots = _slots[i].Spots,
            Heard = _slots[i].Heard,
            Sightings = effects is null ? null : s_sightings ??= [],
            Effects = effects,
            Block = keepSenses ? _slots[i].Senses ??= new SenseBlock() : _slots[i].Senses ?? new SenseBlock(),
            FightTarget = cold.InCombat && (Combat ?? world.SystemOf<CombatSystem>()) is { } combat
                ? combat.StateOf(new CharacterId(i, prev[i].Generation)).TargetSlot : -1,
        };
    }

    /// <summary>
    /// The faction <paramref name="cold"/>'s squad is contracted to (<see cref="Scan.Employer"/>): the contract target's (the hirer) for signals 12
    /// and 15; -1 otherwise.
    /// </summary>
    static int EmployerOf(World world, CharacterCold cold, out bool allies, out bool defending)
    {
        allies = defending = false;
        var table = world.Characters;

        // Contract_DefendPlayerBase (15): the exempt faction is the owner of the player town nearest the platoon leader (FUN_14028a290 against the
        // town set at the start by FUN_14028a170), not a stored hirer; none without a player town.
        if (cold.Ai?.Squad.Ai is { Contract.Signal: 15 })
        {
            defending = true;
            var from = cold.Ai.Squad.Position;
            Construction.PlayerTown? nearest = null;

            foreach (var t in world.Buildings.Towns)
            {
                if (nearest is null || Vector3.DistanceSquared(t.Position, from) < Vector3.DistanceSquared(nearest.Position, from))
                    nearest = t;
            }

            return nearest?.Owner ?? -1;
        }

        if (cold.Ai?.Squad.Ai is not { Contract.Signal: 12 or 15 } board || board.MissionTarget is not { IsNone: false } id
            || id.Slot >= table.HighWater || table.Previous[id.Slot].Generation != id.Generation || table.Cold(id.Slot) is not { } h)
        {
            return -1;
        }

        allies = board.Contract.Signal == 12;
        return h.Faction;
    }

    /// <summary>
    /// The territorial circle (<c>FUN_140926d50</c> from <c>FUN_14059fd70</c>, <b>Verified</b>): the squad's home town's point (a nest's: its centre,
    /// <see cref="Squad.HomeCentre"/>) and (1.3 x its radius)²; -1 for a nest marker or a zero-radius town, which contains nobody; 0 without a home
    /// town (no circle).
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

    /// <summary>
    /// The sense pass (<c>SensoryData</c> update <c>FUN_14085a070</c>, <b>Verified</b>; docs/game/perception.md "Senses"): the counts and the threats
    /// start again; every entry is sensed again (<see cref="SenseOne"/>) and refreshed (<see cref="Refresh"/>) or decayed (<see cref="Decay"/>), and
    /// counted (<see cref="Count"/>); then the characters gathered within the look's reach that have no entry are sensed and kept when sensed
    /// (<see cref="Visit"/>).
    /// </summary>
    static void Look(ref Scan s)
    {
        var b = s.Block;
        b.ThreatList.Clear();
        b.Enemies = b.CagedEnemies = b.Neutrals = b.Allies = b.AlliesDown = 0;
        b.ThreatToMe = b.ThreatToAllies = 0;
        b.Faded = false;
        var table = s.Table;
        var prev = table.Previous;
        var list = b.EntryList;
        int w = 0;

        for (int k = 0; k < list.Count; k++)
        {
            var e = list[k];
            int t = e.Target;
            if ((uint)t >= (uint)table.HighWater || !prev[t].Alive || prev[t].Generation != e.Generation || table.Cold(t) is not { } c)
                continue;

            bool keep = true;
            if (SenseOne(ref s, t, c, out bool seen, out bool heard)) Refresh(ref s, ref e, t, c, seen, heard);
            else keep = Decay(ref s, ref e, t, c);

            Count(ref s, e, c);
            if (keep) list[w++] = e;
        }

        list.RemoveRange(w, list.Count - w);
        VisitKin(ref s);
        VisitFighters(ref s);
        s.Gathered = 0;
        s.FarPass = false;
        s.World.Grid.Query(s.Eye.X, s.Eye.Z, GatherNear, ref s, Visit);
        if (!s.Far || s.Gathered >= GatherCap) return;

        s.FarPass = true;
        s.World.Grid.Query(s.Eye.X, s.Eye.Z, MathF.Max(s.Range, NoiseFar), ref s, Visit);
    }

    /// <summary>
    /// The look of a carried or unconscious character (<c>SensoryData::fade</c> <c>FUN_140859220</c>, <b>Verified</b>): the counts and threats are
    /// cleared and every entry decays (<see cref="Decay"/>) as one not sensed; nobody new is gathered and nothing is sensed again.
    /// </summary>
    static void Fade(ref Scan s)
    {
        var b = s.Block;
        b.ThreatList.Clear();
        b.Enemies = b.CagedEnemies = b.Neutrals = b.Allies = b.AlliesDown = 0;
        b.ThreatToMe = b.ThreatToAllies = 0;
        b.Faded = true;
        var table = s.Table;
        var prev = table.Previous;
        var list = b.EntryList;
        int w = 0;

        for (int k = 0; k < list.Count; k++)
        {
            var e = list[k];
            int t = e.Target;
            if ((uint)t >= (uint)table.HighWater || !prev[t].Alive || prev[t].Generation != e.Generation || table.Cold(t) is not { } c)
                continue;

            if (Decay(ref s, ref e, t, c)) list[w++] = e;
        }

        list.RemoveRange(w, list.Count - w);
    }

    /// <summary>
    /// The forced sense (<c>FUN_140858f40</c>, docs/game/perception.md "Senses"): <paramref name="looker"/> is told of <paramref name="target"/>
    /// (<c>AI::underMeleeAttack</c>, a massive alarm): a new entry is made as seen (tags, threats and counts as at a look), an existing one is marked
    /// seen. An existing entry's threat is also taken at once when the target's fight targets the looker (<b>engine</b>: the original's AI update
    /// runs its sense pass right before the chooser, <c>FUN_1405112b0</c>, so the replan an attack asks for reads a current threats list; Meitou's
    /// look runs once in <see cref="AggroSettings.ScanEveryTicks"/>). Serial phases only.
    /// </summary>
    public void Force(World world, int looker, int target)
    {
        var table = world.Characters;
        var prev = table.Previous;

        if (looker == target || (uint)looker >= (uint)_slots.Length || (uint)target >= (uint)table.HighWater || _slots[looker].Generation < 0
            || !prev[looker].Alive || !prev[target].Alive || table.Cold(looker) is not { Medical: not null } cold
            || table.Cold(target) is not { } tc)
        {
            return;
        }

        var b = _slots[looker].Senses ??= new SenseBlock();
        int gen = prev[target].Generation, k = b.IndexOf(target, gen);

        if (k >= 0)
        {
            var e = b.EntryList[k];
            e.Seen = true;
            b.EntryList[k] = e;
            var id = new CharacterId(target, gen);

            if (!b.ThreatList.Contains(id) && tc.Faction != cold.Faction && tc.InCombat && tc.Medical is { Incapacitated: false }
                && (Combat ?? world.SystemOf<CombatSystem>()) is { } fights
                && fights.TargetOf(target, gen) == (looker, prev[looker].Generation))
            {
                b.ThreatList.Add(id);
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
    /// The forced sense (<c>FUN_140858f40</c>, docs/game/perception.md "Spotting a sneaker"): <paramref name="looker"/> has been told of
    /// <paramref name="target"/> (a massive alarm, a blow or bolt that landed on it, a dialogue attack) and goes on its watcher list as seen, whether
    /// or not it sees it. Only a player-side target keeps the list (the arrows and the sneak XP read it). Serial phases only.
    /// </summary>
    public void Notice(World world, CharacterId looker, CharacterId target)
    {
        var table = world.Characters;

        if (!table.TryResolveNext(target, out int t) || !table.TryResolveNext(looker, out int l) || table.Cold(t) is not { } sneaker
            || table.Cold(l) is not { } watcher)
        {
            return;
        }

        Force(world, l, t);
        if (watcher.Faction == sneaker.Faction || !sneaker.IsPlayer && !(world.Player.Faction >= 0 && sneaker.Faction == world.Player.Faction))
            return;

        Watch(world, sneaker, looker, StealthExposure.Detected);
    }

    /// <summary>
    /// The gather (<c>FUN_14085a8d0</c>): a character within the look's reach that has no entry yet (those with one were sensed or decayed before) is
    /// sensed, and kept as a new entry when seen or heard; the near pass and the far pass of <see cref="Look"/> take up to the engine cap
    /// (<see cref="GatherCap"/>; the original has none).
    /// </summary>
    static void Visit(int slot, ref Scan s)
    {
        if (slot == s.Self) return;

        ref readonly var other = ref s.Table.Previous[slot];

        // The range is on the ground plane (X/Z), as the original's gather over the grid (<c>FUN_1409ff210</c>, Verified).
        float d2 = Vector2.DistanceSquared(new Vector2(other.Position.X, other.Position.Z), new Vector2(s.Eye.X, s.Eye.Z));
        if (s.FarPass ? d2 <= GatherNear * GatherNear || s.Gathered >= GatherCap || d2 > s.Range * s.Range && d2 > NoiseFar * NoiseFar
            : d2 > GatherNear * GatherNear || s.Gathered >= GatherCap + GatherExtra) return;

        var list = s.Block.EntryList;
        int at = s.Block.LowerBound(slot);
        if (at < list.Count && list[at].Target == slot) return;

        s.Gathered++;
        if (s.Table.Cold(slot) is not { } c || !SenseOne(ref s, slot, c, out bool seen, out bool heard)) return;

        var e = new SenseEntry { Target = slot, Generation = other.Generation };
        Refresh(ref s, ref e, slot, c, seen, heard);
        Count(ref s, e, c);
        Insert(s.Block, at, e, (long)s.Tick);
        if (s.NewEnemy < 0 && IsEnemy(e)) s.NewEnemy = slot;
    }

    /// <summary>
    /// The kin of a look: the looker's squad mates, and every other character of the player for one of the player's, are sensed at each look outside
    /// the gather's quota and may take the place of a stranger's entry in a full block (<see cref="Insert"/>). The original's sensed map has no bound
    /// and no quota (<b>Verified</b>), and its protect goals (143, 15) read their mates in it; Meitou's engine caps would otherwise leave a mate
    /// unsensed among the first strangers the grid hands the gather in a town, and nobody would come to its fight.
    /// </summary>
    static void VisitKin(ref Scan s)
    {
        if (s.Squad >= 0 && s.World.Squads.Find(s.Squad) is { } squad)
            foreach (var m in squad.Members) VisitKin(ref s, m.Slot, m.Generation, out _);
        if (s.Player && s.PlayerSlots is { } players)
            foreach (int slot in players) VisitKin(ref s, slot, s.Table.Previous[slot].Generation, out _);
    }

    /// <summary>
    /// Senses a kin or fighter; true when the full block had no room for it (<see cref="Insert"/>), <paramref name="enemy"/> when it is tagged an
    /// enemy.
    /// </summary>
    static bool VisitKin(ref Scan s, int slot, int generation, out bool enemy)
    {
        enemy = false;
        var prev = s.Table.Previous;
        if (slot == s.Self || (uint)slot >= (uint)prev.Length || !prev[slot].Alive || prev[slot].Generation != generation)
            return false;

        var list = s.Block.EntryList;
        int at = s.Block.LowerBound(slot);
        if (at < list.Count && list[at].Target == slot) return false;
        if (s.Table.Cold(slot) is not { } c || !SenseOne(ref s, slot, c, out bool seen, out bool heard)) return false;

        var e = new SenseEntry { Target = slot, Generation = generation };
        Refresh(ref s, ref e, slot, c, seen, heard);
        Count(ref s, e, c);
        bool kept = Insert(s.Block, at, e, (long)s.Tick, kin: true);
        enemy = IsEnemy(e);
        if (s.NewEnemy < 0 && enemy) s.NewEnemy = slot;

        return !kept;
    }

    /// <summary>
    /// The fights of a look: every character in a fight within the look's reach (the sight range, or the noise reach) is sensed at each look outside
    /// the gather's quota and may take the place of a stranger's entry in a full block, an enemy also an ally's (<see cref="Insert"/>). The original's
    /// sensed map has no bound (<b>Verified</b>), and the goals that join other people's fights (34, 15, 288, 143's attacker test, the attack on sight
    /// of a fighter) read the fighter and its victim in it; with Meitou's caps both could be missed among the first strangers of a crowd. The looker's
    /// own squad mates were visited as kin already.
    /// </summary>
    static void VisitFighters(ref Scan s)
    {
        if (s.FighterSlots is not { Count: > 0 } fighters) return;

        var prev = s.Table.Previous;
        float reach = MathF.Max(s.Range, NoiseFar), r2 = reach * reach;
        bool onlyEnemies = false;

        foreach (int slot in fighters)
        {
            if (Vector3.DistanceSquared(prev[slot].Position, s.Eye) > r2 || s.Table.Cold(slot) is not { } c) continue;
            if (s.Squad >= 0 && c.SquadId == s.Squad || onlyEnemies && c.Faction == s.Faction) continue;
            if (!VisitKin(ref s, slot, prev[slot].Generation, out bool enemy)) continue;

            // Refused: an enemy finds the block full of enemies and knocked-out characters, so no one else has room; after anyone else only an
            // enemy may still get in, and the looker's own faction never is one.
            if (enemy) break;
            onlyEnemies = true;
        }
    }

    /// <summary>
    /// Puts a new entry at <paramref name="at"/>; with the block full the entry sensed longest ago (before <paramref name="tick"/>) makes room, else
    /// the new one is not kept. With every entry sensed at this look, a kin or enemy entry takes the place of a neutral stranger and an enemy that
    /// of an ally (the first one, in slot order; engine choice, docs/game/perception.md "Senses": a squad of more than <see cref="SenseBlock.Cap"/>
    /// would otherwise fill its mates' blocks with one another and never sense the enemy it fights).
    /// </summary>
    static bool Insert(SenseBlock b, int at, in SenseEntry e, long tick, bool kin = false)
    {
        var list = b.EntryList;

        if (list.Count >= SenseBlock.Cap)
        {
            int room = Oldest(list, tick);
            bool enemy = IsEnemy(e);
            if (room < 0 && (kin || enemy)) room = FirstTagged(list, SenseTag.Ally | SenseTag.Enemy | SenseTag.Ko, SenseTag.None);
            if (room < 0 && enemy) room = FirstTagged(list, SenseTag.Enemy, SenseTag.Ally);
            if (room < 0) return false;

            list.RemoveAt(room);
            if (room < at) at--;
        }

        list.Insert(at, e);
        return true;
    }

    /// <summary>
    /// Whether an entry is tagged ENEMY and not knocked out.
    /// </summary>
    static bool IsEnemy(in SenseEntry e) => (e.Tags & (SenseTag.Enemy | SenseTag.Ko)) == SenseTag.Enemy;

    /// <summary>
    /// The index of the entry sensed longest ago, before <paramref name="tick"/>; -1 when every entry was sensed at it.
    /// </summary>
    static int Oldest(List<SenseEntry> list, long tick)
    {
        int oldest = -1;
        for (int k = 0; k < list.Count; k++)
            if (list[k].Tick < tick && (oldest < 0 || list[k].Tick < list[oldest].Tick)) oldest = k;

        return oldest;
    }

    /// <summary>
    /// The index of the first entry with none of the tags <paramref name="none"/> and, unless <paramref name="any"/> is empty, one of
    /// <paramref name="any"/>; -1 when there is none.
    /// </summary>
    static int FirstTagged(List<SenseEntry> list, SenseTag none, SenseTag any)
    {
        for (int k = 0; k < list.Count; k++)
            if ((list[k].Tags & none) == 0 && (any == SenseTag.None || (list[k].Tags & any) != 0)) return k;

        return -1;
    }

    #endregion

    #region Sensing one character

    /// <summary>
    /// The sight ray of the spot meter (<c>FUN_1408533a0</c>, <b>Verified</b> literals): it starts this far over the looker's feet.
    /// </summary>
    const float SightRayFrom = 18;

    /// <summary>
    /// The sight ray ends this far over the target's feet (half of <see cref="SightRayFrom"/>).
    /// </summary>
    const float SightRayTo = 9;

    /// <summary>
    /// A hit of the sight ray nearer than this to the target does not block it.
    /// </summary>
    const float SightRaySlack = 2;

    /// <summary>
    /// Whether the looker senses <paramref name="slot"/> now (<c>FUN_1408593d0</c>: seen by <c>FUN_140857b10</c>, else heard by
    /// <c>FUN_140857500</c>). A sneaking target of another side fills the spot meters of a looker that keeps them (<see cref="Scan.Meters"/>,
    /// exposing the sneaker), and counts as seen in view only once its meter was full before this look, or when it is the looker's own fight target
    /// (engine choice: a chaser does not lose its quarry to the meter).
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
            if (v > 0 && !ClearSight(ref s, other.Position)) v = 0;

            bool full = SpotOf(s.Spots, slot, other.Generation) > 1;

            if (s.Meters)
            {
                if (v > 0 && !Sighted(s.Sightings!, slot))
                    s.Sightings!.Add(new Sighting(slot, other.Generation, Orders.SpotSeconds(v, stealth)));

                if (Vector3.DistanceSquared(other.Position, s.Eye) <= s.Range * s.Range)
                {
                    s.Effects!.Emit(s.Self, slot, ExposureEffect,
                        (float)(full && v > 0 ? StealthExposure.Detected : v > 0 ? StealthExposure.Noticed : StealthExposure.Near));
                }
            }

            seen = v > 0 && (full || slot == s.FightTarget);
        }
        else seen = s.Sight.Sees(s.Self, slot, s.Eye, other.Position, s.Outdoor, stealth) > 0;

        if (!seen && s.Effects is { } fx)
        {
            heard = Hears(s.World, s.Table, s.Self, slot, stealth, Rng.Hash(s.Seed, s.Key, RngPurpose.Sense, (s.Tick << 20) ^ (ulong)(uint)slot), fx,
                ref s.Heard, out bool xpCheck);

            // The check's stealth XP (FUN_1408c7330): its a is slot 125, the looker regarding the heard as an enemy (FUN_14079c950).
            if (xpCheck)
            {
                fx.Emit(s.Self, slot, HearingXpEffect,
                    (heard ? 1 : 0) | (Fighting(ref s, slot) || Regards(ref s, slot, c, other.Position, out _) ? 2 : 0));
            }
        }

        return seen || heard;
    }

    /// <summary>
    /// Whether nothing solid stands between the looker and a sneaker at <paramref name="target"/> (the spot meter's line of sight,
    /// <c>FUN_140856cb0</c> calling <c>FUN_1408533a0</c>): the terrain and the buildings' collision (walls, floors, stairs, furniture and roofs;
    /// not doors or passable parts, <see cref="SolidHits"/>) on the ray from <see cref="SightRayFrom"/> over the looker's feet to
    /// <see cref="SightRayTo"/> over the target's, a hit within <see cref="SightRaySlack"/> of the target not counting.
    /// </summary>
    static bool ClearSight(ref Scan s, Vector3 target)
    {
        var from = s.Eye + new Vector3(0, SightRayFrom, 0);
        var seg = target + new Vector3(0, SightRayTo, 0) - from;
        float hit = SolidHits.Hit(s.World.Walkability, s.Fights?.Buildings, from, seg, RangedTuning.TerrainStep, out _);

        return hit < 0 || hit * seg.Length() >= Vector3.Distance(s.Eye, target) - SightRaySlack;
    }

    /// <summary>
    /// An entry sensed now (<c>FUN_1408580d0</c>): its place and time, seen and heard, its tags (<see cref="TagOf"/>); the threats
    /// (<c>addToThreatsAndFlockingList</c> <c>FUN_140854370</c>: a standing character of another faction whose fight targets the looker joins the
    /// threats list and adds 1 to <see cref="SenseBlock.ThreatToMe"/>, one fighting another of the looker's faction adds 1 to
    /// <see cref="SenseBlock.ThreatToAllies"/>; the original adds its aggression, Meitou has none); an ally counts as standing or not; and the
    /// sensing hooks of the looker's squad package (war assault TEMP_ENEMY, hired ally TEMP_ALLY) tag it in the squad's memory.
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
        Threats(ref s, slot, c);

        if ((e.Tags & SenseTag.Ally) != 0)
        {
            if (c.Medical is { Incapacitated: false, Prone: false, Dead: false }) b.Allies++;
            else b.AlliesDown++;
        }

        if (s.Effects is { } fx && s.Squad >= 0 && s.Memory is not null && HookRuns(ref s, slot, other.Position))
            SenseHooks(ref s, fx, e, slot, c, other.Position);
    }

    /// <summary>
    /// The threats of a sensed entry (<c>addToThreatsAndFlockingList</c> <c>FUN_140854370</c>): a standing character of another faction whose
    /// fight targets the looker joins the threats list and adds 1 to <see cref="SenseBlock.ThreatToMe"/>; one fighting another of the looker's
    /// faction adds 1 to <see cref="SenseBlock.ThreatToAllies"/>.
    /// </summary>
    static void Threats(ref Scan s, int slot, CharacterCold c)
    {
        var prev = s.Table.Previous;
        ref readonly var other = ref prev[slot];
        var b = s.Block;
        if (c.Faction == s.Faction || !c.InCombat || c.Medical is not { Incapacitated: false } || s.Fights is not { } combat) return;

        var (ft, fg) = combat.TargetOf(slot, other.Generation);

        if (ft == s.Self && fg == prev[s.Self].Generation)
        {
            b.ThreatList.Add(new CharacterId(slot, other.Generation));
            b.ThreatToMe += 1;
        }
        else if (ft >= 0 && ft < s.Table.HighWater && prev[ft].Generation == fg && s.Table.Cold(ft) is { } fc && fc.Faction == s.Faction)
            b.ThreatToAllies += 1;
    }

    /// <summary>
    /// The sensing hooks of the looker's squad package on a sensed character (war assault TEMP_ENEMY and assault targets, hired ally TEMP_ALLY, the
    /// bandit fight tag), emitted as effects.
    /// </summary>
    static void SenseHooks(ref Scan s, EffectBuffer fx, in SenseEntry e, int slot, CharacterCold c, Vector3 at)
    {
        // The sighting hooks of the assault packages (passing-by assault FUN_1402903c0, occupation FUN_140286c40, the war assault FUN_14028a3f0)
        // tag each sensed enemy with squad memory 4; the tag has no expiry and the package's end does not clear it. Their "in view" gate
        // (Character +0x1a9) is treated as always true: the simulation does not depend on the camera (engine choice).
        if (s.War.IsSet && WarEnemy(ref s, c, at))
            fx.Emit(s.Self, slot, TagEffect, Dialogue.MemoryTag.TempEnemy);

        if (s.War.IsSet && (WarEnemy(ref s, c, at) || AssaultSensed(ref s, e, c, at)))
            fx.Emit(s.Self, slot, AssaultTargetEffect, 0);

        if (s.EmployerTags && s.Employer >= 0 && c.Faction == s.Employer)
            fx.Emit(s.Self, slot, TagEffect, Dialogue.MemoryTag.TempAlly);

        if (BanditFightTag(ref s, e, c, at))
            fx.Emit(s.Self, slot, TagEffect, Dialogue.MemoryTag.TempEnemy);
    }

    /// <summary>
    /// The sight pass's squad-memory tag (<c>FUN_140859680</c> ending in <c>FUN_1406744a0(looker, target, 4)</c>, <b>Verified</b>; "event 4" in the
    /// swarm notes is the memory tag TEMP_ENEMY, not a dialogue event): a sensed character that is no ally and not down or an animal, while the
    /// looker or it is in combat mode or it has a pending crime, with no status town (<see cref="CharacterCold.StatusTown"/>, the original's status
    /// <c>+0x88</c>: a town entered holds out to 1.5 x its radius) and outside every town's radius, with a bandit (NPC class 8) as looker or target,
    /// is tagged TEMP_ENEMY by the looker's squad (<c>FUN_1406744a0</c> skips an allied faction and one the looker regards as friendly: the ALLY
    /// tag). The looker must be free (slave state 0), not an escaped prisoner and without a pending crime of its own (<b>Verified</b>). Only a
    /// looker whose dialogue has a sight line tags (<see cref="SightLines"/>: the original's sight pass does nothing else while its Dialogue
    /// <c>+0x28</c> flag is clear). The view gate is <see cref="HookRuns"/>'s; the looker's sleeping is treated as open (engine choice).
    /// </summary>
    static bool BanditFightTag(ref Scan s, in SenseEntry e, CharacterCold c, Vector3 at)
    {
        if (s.Cold is not { } me || (e.Tags & (SenseTag.Ally | SenseTag.Ko)) != 0 || c.Fighter?.IsAnimal == true || me.SlaveState != 0
            || me.Escaped || me.Pending is not null || me.IsImprisoned)
        {
            return false;
        }

        if (!(me.InCombat || c.InCombat || c.Pending is not null)) return false;
        if (me.NpcClass != FundamentalType.Bandit && c.NpcClass != FundamentalType.Bandit) return false;
        if (s.SightLines is { } lines)
        {
            if (s.LookerSees == 0) s.LookerSees = lines(s.World, s.Self) ? (sbyte)1 : (sbyte)-1;
            if (s.LookerSees < 0) return false;
        }

        // Inside a town's radius the status town is always set; the radius test also covers a status town not yet written (the arrest system's
        // pass after a load or a spawn) and a world that keeps none (no Crime part).
        return c.StatusTown.IsNone && s.Towns is { } towns && AiSystem.TownAt(towns, s.World, at).IsNone;
    }

    /// <summary>
    /// The passing-by assault's update adds to its target set every character in its members' sensed lists with the enemy tag and not the KO one that
    /// is in a town, alive, awake and at posture 0 (<c>FUN_140294980</c>, <b>Verified</b>).
    /// </summary>
    static bool AssaultSensed(ref Scan s, in SenseEntry e, CharacterCold c, Vector3 at) =>
        (e.Tags & (SenseTag.Enemy | SenseTag.Ko)) == SenseTag.Enemy && c.Medical is { Dead: false, Incapacitated: false }
            && CharacterFacts.PostureZero(c)
        && s.Towns is { } towns && !AiSystem.TownAt(towns, s.World, at).IsNone;

    /// <summary>
    /// Whether the package sensing hooks run for a sensed character (<c>FUN_140859680</c>, <b>Verified</b>): when the listener or the target is on
    /// screen (<c>+0x1a9</c>, <see cref="MedicalClock.OnScreen"/>) or the target is of the player's side (<c>FUN_140856bc0</c>). A world that was
    /// never told a camera has nothing to test and runs them (engine choice).
    /// </summary>
    static bool HookRuns(ref Scan s, int target, Vector3 targetAt)
    {
        if (s.World.View is not { } view) return true;

        var prev = s.Table.Previous;
        return PlayerSide(s.World, s.Table.Cold(target)) || MedicalClock.OnScreen(view, prev[s.Self].Position) || MedicalClock.OnScreen(view, targetAt);
    }

    /// <summary>
    /// The tags of a sensed character (<c>FUN_1408580d0</c>, docs/game/perception.md "Sense tags"): KO when down or dead
    /// (<see cref="CharacterFacts.Down"/>), imprisoned, and ENEMY when the looker regards it an enemy (<see cref="Regards"/>; a campaign force's
    /// member NEUTRAL instead), else ALLY for its own or an allied faction, else NEUTRAL.
    /// </summary>
    static SenseTag TagOf(ref Scan s, ref SenseEntry e, int slot, CharacterCold c, Vector3 at)
    {
        var tags = SenseTag.None;
        if (CharacterFacts.Down(c) || c.Medical is { Dead: true }) tags |= SenseTag.Ko;
        if (c.IsImprisoned) tags |= SenseTag.Imprisoned;
        if (Fighting(ref s, slot)) return tags | SenseTag.Enemy;

        // The rest (relations, disguise, squad memory, contracts, campaigns) is worked out again on the looker's full looks only (engine choice, for
        // the look's cost; FullTags).
        if (s.FullTags || e.Base == SenseTag.None)
        {
            e.Base = Regards(ref s, slot, c, at, out bool force) ? force ? SenseTag.Neutral : SenseTag.Enemy
                : c.Faction == s.Faction || s.Relations.Contains(c.Faction) && s.Relations.IsAlly(s.Faction, c.Faction)
                    ? SenseTag.Ally
                    : SenseTag.Neutral;
        }

        return tags | e.Base;
    }

    /// <summary>
    /// The character in <paramref name="slot"/> fights the looker or is fought by it (last tick's fights).
    /// </summary>
    static bool Fighting(ref Scan s, int slot)
    {
        if (s.Fights is not { } combat) return false;

        var prev = s.Table.Previous;
        int gen = prev[slot].Generation, selfGen = prev[s.Self].Generation;
        return combat.TargetOf(slot, gen) == (s.Self, selfGen) || combat.TargetOf(s.Self, selfGen) == (slot, gen);
    }

    /// <summary>
    /// Slot 125, "the looker regards it an enemy" (<c>FUN_14079c950</c>, docs/game/perception.md "Sense tags"; engine reading): one fighting the
    /// looker or fought by it, of any faction (the TEMP_ENEMY <c>AI::underMeleeAttack</c> writes, read for any squad; Meitou's scripted fights may
    /// pit one faction against itself); else never its own faction; a slave only when the looker is hostile to its owner's faction and is not the
    /// owner's; else hostility from the looker's faction to the one it is seen as (a disguise) without coexistence, with the freed rule; the squad
    /// memory's TEMP_ENEMY makes, TEMP_ALLY unmakes one; a contract's employer never is; a campaign assault's extra target always is.
    /// <paramref name="force"/>: an enemy that is a campaign force's member, sensed NEUTRAL (<c>FUN_140796240</c>).
    /// </summary>
    static bool Regards(ref Scan s, int slot, CharacterCold c, Vector3 at, out bool force)
    {
        force = false;
        var prev = s.Table.Previous;
        int gen = prev[slot].Generation, selfGen = prev[s.Self].Generation;
        if (c.Faction == s.Faction) return false;
        if (s.War.IsSet && WarEnemy(ref s, c, at)) return true;

        bool enemy;

        if (c.SlaveState == 1)
            enemy = c.SlaveOwner != s.Faction && s.Relations.Contains(c.SlaveOwner) && s.Relations.IsHostile(s.Faction, c.SlaveOwner);
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
                || s.World.Player.Faction == s.Faction && memory.AnyTempEnemy(c.SquadId) && memory.HasShort(s.World, c.SquadId,
                    new CharacterId(s.Self, selfGen), Dialogue.MemoryTag.TempEnemy))
            {
                enemy = tagged = true;
            }
            else if (enemy && s.Squad >= 0 && memory.HasShort(s.World, s.Squad, target, Dialogue.MemoryTag.TempAlly))
                enemy = false;
        }

        if (enemy && s.Employer >= 0
            && (c.Faction == s.Employer || s.EmployerAllies && s.Relations.Contains(c.Faction) && s.Relations.IsAlly(s.Employer, c.Faction)))
        {
            enemy = false;
        }

        if (enemy && !tagged && c.SquadId >= 0 && s.World.Squads.Find(c.SquadId) is { PlatoonId: >= 0 } squad
            && s.World.Platoons.Find(squad.PlatoonId)?.Campaign is not null)
        {
            force = true;
        }

        return enemy;
    }

    /// <summary>
    /// A looker's faction attacks a freed target on sight for the relations only below this default relation (<c>FUN_14079c950</c>: strictly below
    /// -30, Verified constant).
    /// </summary>
    public const int FreedHostility = -30;

    /// <summary>
    /// The counts of the pass (<c>FUN_1408593d0</c>): an entry tagged ENEMY whose character is not down, dead or carried adds to <c>numEnemies</c>,
    /// one tagged NEUTRAL to <c>numNeutrals</c>. The enemies in a cage are counted apart as well (<see cref="SenseBlock.CagedEnemies"/>).
    /// </summary>
    static void Count(ref Scan s, in SenseEntry e, CharacterCold c)
    {
        if (CharacterFacts.Down(c) || c.Medical is { Dead: true }) return;

        if ((e.Tags & SenseTag.Enemy) != 0)
        {
            s.Block.Enemies++;
            if (c.IsImprisoned) s.Block.CagedEnemies++;
        }
        else if ((e.Tags & SenseTag.Neutral) != 0) s.Block.Neutrals++;
    }

    #endregion

    #region Sense decay

    /// <summary>
    /// The sense-age unit in seconds (<c>FUN_140852150</c>: game hours x 3600 / 33 x 0.5; <see cref="GiveUpSenseSeconds"/> = 12 of them).
    /// </summary>
    public const float SenseUnitSeconds = GiveUpSenseSeconds / 12;

    /// <summary>
    /// How long an unsensed entry lasts, in sense units (<c>SensoryData::decay</c> <c>FUN_140859010</c>, <b>Verified</b> literals): 30, 60 for some
    /// targets, 120 for a player character in the home.
    /// </summary>
    public const int SenseLifeUnits = 30, SenseLifeLongUnits = 60, SenseLifeHomeUnits = 120;

    /// <summary>
    /// How long an unsensed entry follows its character's real place, in sense units (<c>FUN_140859010</c>, <b>Verified</b> literals): 8 inside a
    /// building, 25 out of one.
    /// </summary>
    public const int FollowInsideUnits = 8, FollowOutsideUnits = 25;

    /// <summary>
    /// The long-term squad memory tags that keep an entry 60 units: intruder, stole from me, captor, killed my friend (mask 0x232).
    /// </summary>
    const uint LongSenseTags = 0x232;

    /// <summary>
    /// An entry not sensed this look (<c>SensoryData::decay</c> <c>FUN_140859010</c>, <b>Verified</b>): seen and heard cleared, its place follows the
    /// character's for <see cref="FollowInsideUnits"/> / <see cref="FollowOutsideUnits"/> sense units, and it is dropped (false) past
    /// <see cref="SenseLife"/>.
    /// </summary>
    static bool Decay(ref Scan s, ref SenseEntry e, int slot, CharacterCold c)
    {
        e.Seen = e.Heard = false;
        float age = ((long)s.Tick - e.Tick) * s.World.TickSeconds / SenseUnitSeconds;
        if (age < (s.Sight.BuildingOf(slot) is null ? FollowOutsideUnits : FollowInsideUnits))
            e.Place = s.Table.Previous[slot].Position;

        return age <= SenseLife(s.World, s.Relations, s.Memory, s.Cold!, slot, c);
    }

    /// <summary>
    /// The lifetime of a looker's entry of <paramref name="tc"/> in sense units (<c>FUN_140859010</c>, <b>Verified</b>): 120 when the looker's
    /// faction is real and <paramref name="tc"/> is a player character inside the looker's home building; 60 when it is fighting, the looker's squad
    /// remembers it as an intruder, thief, captor or friend killer, or it is the looker's faction's slave; else 30.
    /// </summary>
    static int SenseLife(World world, FactionRelations relations, Dialogue.SquadMemory? memory, CharacterCold cold, int t, CharacterCold tc)
    {
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        bool real = !relations.Contains(cold.Faction) || !relations.Factions[cold.Faction].NotReal;

        if (real && world.Player.Faction >= 0 && tc.Faction == world.Player.Faction && squad?.HomeBuilding is { } home
            && world.Sight.BuildingOf(t) == home.Key)
        {
            return SenseLifeHomeUnits;
        }

        if (tc.InCombat || tc.SlaveState == 1 && tc.SlaveOwner == cold.Faction) return SenseLifeLongUnits;
        if (squad is not null && memory?.Find(world, squad.Id, world.Characters.IdOf(t)) is { } m && (m.Long & LongSenseTags) != 0)
            return SenseLifeLongUnits;

        return SenseLifeUnits;
    }

    #endregion

    #region Foe finders

    /// <summary>
    /// The attack goals' finders <see cref="FindFoe"/> runs for the AI's chooser (docs/game/ai-tasks.md "Combat goals").
    /// </summary>
    public enum FoeFinder : byte
    {
        /// <summary>
        /// 16 ATTACK_ENEMIES (<c>FUN_14059f3a0</c>): an enemy on sight (also the player's attack on sight, Hold's goal and the job).
        /// </summary>
        Enemies = 0,
        /// <summary>
        /// 21 ATTACK_ENEMIES_AND_NEUTRALS (<c>FUN_14059f720</c>): an enemy or a neutral on sight.
        /// </summary>
        EnemiesAndNeutrals = 1,
        /// <summary>
        /// 144 TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME (<c>FUN_14059fd70</c>): an enemy in the home circle or the looker's building.
        /// </summary>
        Territory = 2,
        /// <summary>
        /// 34 ATTACK_TROUBLE_MAKERS (<c>FUN_14099baa0</c>): an enemy attacking an ally in the looker's town.
        /// </summary>
        TroubleMakers = 3,
        /// <summary>
        /// 41 ATTACK_ENEMIES_OF_MY_SLAVEMASTER (<c>FUN_14059ee50</c>): a sensed enemy of the slave's master (<see cref="FindMasterFoe"/>).
        /// </summary>
        MasterEnemies = 4,
    }

    /// <summary>
    /// The range within which a holding attacker not in combat mode still counts as fighting its target (<c>DAT_141685344</c>, 40).
    /// </summary>
    const float HoldDefenceRange = 40;

    /// <summary>
    /// The attack on sight (goal 16's finder <c>FUN_14059f3a0</c> and the stances' finders, docs/game/ai-tasks.md "Combat goals"): of the entries
    /// seen or heard at this look, the best by the target score (<see cref="Candidate"/>), ties to the lower slot.
    /// </summary>
    static void Pick(ref Scan s)
    {
        var list = s.Block.EntryList;
        for (int k = 0; k < list.Count; k++)
            if (list[k].Now && s.Table.Cold(list[k].Target) is { } c) Candidate(ref s, list[k].Target, c);
    }

    /// <summary>
    /// The finder of an attack goal of an NPC the AI runs (16, 21, 144, 34; docs/game/ai-tasks.md "Combat goals"), for its chooser
    /// (<see cref="Ai.AiSystem"/>): of the characters its last look saw or heard, and those a squad mate's last look saw or heard (tagged an enemy,
    /// or a neutral for 21) within twice the looker's range R (the squad's shared sightings, <c>GroupSense</c>, docs/game/perception.md: no view cone
    /// or light test), the best by the target score that the goal's own test takes (<see cref="Candidate"/> with the goal's stance: relations, tags,
    /// the post and path tests; 144 its home circle, 34 an attacker of an ally in its town). <see cref="CharacterId.None"/> (the factor 0) when there
    /// is none, when the look picks no fights (<see cref="AggroSettings.AttackOnSight"/> off) or the looker cannot pick one (a slave, no relations,
    /// no fighter). A pure read of the last looks and fights: any thread in the think.
    /// </summary>
    public CharacterId FindFoe(World world, int slot, FoeFinder finder, IReadOnlyDictionary<int, int>? pending = null)
    {
        var table = world.Characters;
        if (finder == FoeFinder.MasterEnemies) return FindMasterFoe(world, slot, pending);

        if (!Settings.AttackOnSight || (uint)slot >= (uint)_slots.Length || _slots[slot].Senses is not { } own
            || table.Cold(slot) is not { Medical: not null, SlaveState: not 1, Fighter.CanAttack: true } cold || !Relations.Contains(cold.Faction))
        {
            return CharacterId.None;
        }

        var war = cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { War.IsSet: true } assaulting ? assaulting.War : WarTargets.None;
        if (finder == FoeFinder.TroubleMakers) return FindTroubleMaker(world, slot, cold, own, war, pending);

        bool neutrals = finder == FoeFinder.EnemiesAndNeutrals;

        // As the look's own gate: no enemy faction about, no neutrals wanted, no war and no TEMP_ENEMY tag of its squad leave nothing to find.
        if (!(cold.Faction < _census.FactionCount && _census.HasEnemy(cold.Faction)) && !neutrals && !war.IsSet
            && !(Memory?.AnyTempEnemy(cold.SquadId) ?? false))
        {
            return CharacterId.None;
        }

        float range = MathF.Max(0, RangeOf(cold, Settings.SightRange));
        var scan = NewScan(world, slot, cold, war, range, null, keepSenses: false, finder);
        scan.Pending = pending;
        Pick(ref scan);

        if (cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { Members.Count: > 1 } squad)
            PickShared(world, slot, squad, own, 2 * range, neutrals, ref scan);

        return scan.Best >= 0 ? new CharacterId(scan.Best, table.Previous[scan.Best].Generation) : CharacterId.None;
    }

    /// <summary>
    /// The squad's shared sightings of <see cref="FindFoe"/> (<c>GroupSense</c>): what the mates' last looks sensed (tagged an enemy, or a neutral
    /// for 21), not down, unknown to the looker's own last look and within <paramref name="reach"/> of it, is weighed as a candidate.
    /// </summary>
    void PickShared(World world, int slot, Squad squad, SenseBlock own, float reach, bool neutrals, ref Scan scan)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var eye = prev[slot].Position;
        var wanted = SenseTag.Enemy | (neutrals ? SenseTag.Neutral : SenseTag.None);

        foreach (var m in squad.Members)
        {
            if (m.Slot == slot || (uint)m.Slot >= (uint)_slots.Length || _slots[m.Slot].Generation != m.Generation
                || _slots[m.Slot].Senses is not { } mate || mate.Enemies + (neutrals ? mate.Neutrals : 0) == 0)
            {
                continue;
            }

            foreach (var e in mate.EntryList)
            {
                int t = e.Target;

                if (!e.Now || t == slot || (e.Tags & wanted) == 0 || (e.Tags & SenseTag.Ko) != 0 || (uint)t >= (uint)table.HighWater
                    || !prev[t].Alive || prev[t].Generation != e.Generation
                    || own.Knows(new CharacterId(t, e.Generation), now: true) || Vector3.DistanceSquared(prev[t].Position, eye) > reach * reach
                    || table.Cold(t) is not { } c)
                {
                    continue;
                }

                Candidate(ref scan, t, c);
            }
        }
    }

    /// <summary>
    /// 41 ATTACK_ENEMIES_OF_MY_SLAVEMASTER (finder <c>FUN_14059ee50</c>, <b>Verified</b>; docs/game/perception.md "Sense tags"): for a slave (state
    /// 1) whose master resolves (its squad), the entries of its senses with the master-enemy tag (bit 12: the master counts the character as an
    /// enemy, slot 125 from the master's side; here the master squad's faction attacks it on sight, or its squad tag TEMP_ENEMY) and none of KO,
    /// imprisoned or crawling (bits 3, 9, 14); not of the slave's own squad, not a slave of the same owner; the best by the target score. The area
    /// test <c>FUN_140599f30</c> and the Hold post test <c>FUN_140598630</c> follow the look's (the area is not modelled). A pure read (any thread in
    /// the think).
    /// </summary>
    CharacterId FindMasterFoe(World world, int slot, IReadOnlyDictionary<int, int>? pending)
    {
        var table = world.Characters;
        var prev = table.Previous;

        if (slot >= _slots.Length || _slots[slot].Senses is not { } own
            || table.Cold(slot) is not { Medical: not null, SlaveState: 1, SlaveMaster: >= 0, Fighter.CanAttack: true } cold
            || world.Squads.Find(cold.SlaveMaster) is not { } master || !Relations.Contains(master.Faction))
        {
            return CharacterId.None;
        }

        var scan = NewScan(world, slot, cold, WarTargets.None, MathF.Max(0, RangeOf(cold, Settings.SightRange)), null, keepSenses: false,
            FoeFinder.MasterEnemies);
        scan.Pending = pending;

        foreach (var e in own.EntryList)
        {
            int t = e.Target;

            if ((e.Tags & (SenseTag.Ko | SenseTag.Imprisoned)) != 0 || (uint)t >= (uint)table.HighWater || t == slot || !prev[t].Alive
                || prev[t].Generation != e.Generation
                || table.Cold(t) is not { Medical.Incapacitated: false, IsImprisoned: false } c || c.Medical.Crawling && !c.InCombat
                || c.SquadId == cold.SquadId
                || c.SlaveState == 1 && c.SlaveOwner == cold.SlaveOwner || !EnemyOfMaster(world, master, c, t, e.Generation))
            {
                continue;
            }

            var id = new CharacterId(t, e.Generation);
            if (PursuitSystem.PathImpossible(cold, id, world.Tick)) continue;

            float d2 = Vector3.DistanceSquared(prev[t].Position, scan.Eye);
            float score = Score(ref scan, t, c, d2) ?? float.NegativeInfinity;
            if (!(score > scan.BestScore)) continue;

            (scan.Best, scan.BestD2, scan.BestScore) = (t, d2, score);
        }

        return scan.Best >= 0 ? new CharacterId(scan.Best, prev[scan.Best].Generation) : CharacterId.None;
    }

    /// <summary>
    /// Sense tag bit 12 (<c>FUN_1408580d0</c>): the slave's master calls <paramref name="c"/> an enemy (slot 125 of the master): its faction attacks
    /// the character's seen faction on sight, or its squad tags it TEMP_ENEMY.
    /// </summary>
    bool EnemyOfMaster(World world, Squad master, CharacterCold c, int slot, int generation) =>
        c.Faction != master.Faction && (AttacksOnSight(Relations, master.Faction, Crime.ArrestSystem.SeenFaction(c))
            || Memory is { } memory && memory.HasShort(world, master.Id, new CharacterId(slot, generation), Dialogue.MemoryTag.TempEnemy));

    /// <summary>
    /// 34 ATTACK_TROUBLE_MAKERS, "Law enforcer" (finder <c>FUN_14099baa0</c>, <b>Verified</b>; docs/game/ai-tasks.md "Combat goals"): nothing while
    /// the looker is in no town (its current town). Pass 1, the entries of its senses tagged ENEMY and not KO (seen or heard or not): one in the same
    /// town whose current goal intends aggression (status +0xac, <see cref="Ai.TaskAggression.OfGoal"/>) or that its squad tagged AGGRESSOR. Pass 2,
    /// the entries tagged NEUTRAL and not KO: not law enforcement, trader or diplomat; military only when its squad's town is not this one and its
    /// faction does not own it (the town's resident factions are <b>Unknown</b>: the owner stands in); in the same town; not a slaver; intending
    /// aggression, tagged AGGRESSOR, or a bandit or an animal whose running task intends it (+0xa0); and attacking an ally or an innocent
    /// (<see cref="AttacksProtected"/>). The best by the target score, as the look's (the area test <c>FUN_140599f30</c> is not modelled). A pure
    /// read (any thread in the think).
    /// </summary>
    CharacterId FindTroubleMaker(World world, int slot, CharacterCold cold, SenseBlock own, WarTargets war, IReadOnlyDictionary<int, int>? pending)
    {
        if (Towns is not { } towns) return CharacterId.None;

        var table = world.Characters;
        var prev = table.Previous;
        var town = AiSystem.TownAt(towns, world, prev[slot].Position);
        if (town.IsNone) return CharacterId.None;

        int owner = town.Site >= 0 && town.Site < towns.SiteFactions.Count ? towns.SiteFactions[town.Site] : -1;
        var fights = Combat ?? world.SystemOf<CombatSystem>();
        var scan = NewScan(world, slot, cold, war, MathF.Max(0, RangeOf(cold, Settings.SightRange)), null, keepSenses: false, FoeFinder.TroubleMakers);
        scan.Pending = pending;

        foreach (var e in own.EntryList)
        {
            int t = e.Target;
            bool enemy = (e.Tags & SenseTag.Enemy) != 0;
            if ((e.Tags & SenseTag.Ko) != 0 || !enemy && (e.Tags & SenseTag.Neutral) == 0 || (uint)t >= (uint)table.HighWater || !prev[t].Alive
                || prev[t].Generation != e.Generation || table.Cold(t) is not { Medical.Incapacitated: false, IsImprisoned: false } c)
            {
                continue;
            }

            var id = new CharacterId(t, e.Generation);
            bool aggressor = Memory is { } memory && cold.SquadId >= 0 && memory.HasShort(world, cold.SquadId, id, Dialogue.MemoryTag.Aggressor);
            bool intends = TaskAggression.OfGoal(c) > 0;

            if (!enemy)
            {
                if (c.NpcClass is FundamentalType.LawEnforcement or FundamentalType.Trader or FundamentalType.Diplomat or FundamentalType.Slaver)
                    continue;

                if (c.NpcClass == FundamentalType.Military
                    && (c.Faction == owner || world.Squads.Find(c.SquadId)?.Ai?.Town == town.Site && town.Site >= 0))
                {
                    continue;
                }

                if (!(intends || aggressor || TaskAggression.OfTask(c) > 0 && (c.NpcClass == FundamentalType.Bandit || c.Fighter?.IsAnimal == true)))
                    continue;

                if (!AttacksProtected(world, fights, cold, c, t, e.Generation)) continue;
            }
            else if (!intends && !aggressor) continue;

            if (AiSystem.TownAt(towns, world, prev[t].Position) != town || PursuitSystem.PathImpossible(cold, id, world.Tick))
                continue;

            float d2 = Vector3.DistanceSquared(prev[t].Position, scan.Eye);
            float score = Score(ref scan, t, c, d2) ?? float.NegativeInfinity;
            if (!(score > scan.BestScore)) continue;

            (scan.Best, scan.BestD2, scan.BestScore) = (t, d2, score);
        }

        return scan.Best >= 0 ? new CharacterId(scan.Best, prev[scan.Best].Generation) : CharacterId.None;
    }

    /// <summary>
    /// 34's pass-2 victim test (<c>FUN_14099b820</c>, <b>Verified</b>): the neutral <paramref name="attacker"/> in <paramref name="slot"/> has the
    /// victim as its target, is not knocked out or playing dead and is in combat mode, or holds position with the victim within
    /// <see cref="HoldDefenceRange"/> (<c>FUN_1405c92d0</c>: the attacker's own mode and target, not the victim's); and the victim is the looker's
    /// ally (its own faction or an allied one) or an <see cref="Innocent"/>.
    /// </summary>
    bool AttacksProtected(World world, CombatSystem? fights, CharacterCold looker, CharacterCold attacker, int slot, int generation)
    {
        var table = world.Characters;

        if (fights is null || attacker.Medical is { Incapacitated: true } or { PlayingDead: true }
            || fights.TargetOf(slot, generation) is not { Slot: >= 0 } v || (uint)v.Slot >= (uint)table.HighWater
            || table.Previous[v.Slot].Generation != v.Generation || table.Cold(v.Slot) is not { } victim)
        {
            return false;
        }

        // FUN_1405c92d0: the attacker is in combat mode, or holds position (CharStats +0x12b) with the victim within 40 of it.
        if (!attacker.InCombat
            && !((attacker.Orders & CharacterOrders.Hold) != 0
                && Vector3.Distance(table.Previous[slot].Position, table.Previous[v.Slot].Position) < HoldDefenceRange))
        {
            return false;
        }

        if (victim.Faction == looker.Faction || Relations.Contains(victim.Faction) && Relations.IsAlly(looker.Faction, victim.Faction))
            return true;

        return Innocent(looker, attacker, victim);
    }

    /// <summary>
    /// The innocent victim of 34's pass 2 (<c>FUN_14099b820</c>, <b>Verified</b>): not a slaver or bandit by NPC class, no bounty of the looker's
    /// faction, no crime of its own in progress (Character +0x148, <c>committingCrime</c>: <see cref="CharacterCold.Pending"/>); then the attacker's
    /// faction is a bandit one or not real, or else the victim does not regard the looker's faction as hostile (relations, the victim's side) and is
    /// no animal or slave. So the police defend a stranger from bandits.
    /// </summary>
    internal bool Innocent(CharacterCold looker, CharacterCold attacker, CharacterCold victim)
    {
        if (victim.NpcClass is FundamentalType.Slaver or FundamentalType.Bandit || victim.Bounties?.AmountFor(looker.Faction) >= 1
            || victim.Pending is not null)
        {
            return false;
        }

        if (Relations.Contains(attacker.Faction)
            && Relations.Factions[attacker.Faction] is { NotReal: true } or { FundamentalType: FundamentalType.Bandit })
        {
            return true;
        }

        bool hatesLooker = Relations.Contains(victim.Faction) && Relations.IsHostile(victim.Faction, looker.Faction);
        return !hatesLooker && victim.Fighter?.IsAnimal != true && victim.NpcClass != FundamentalType.Slave;
    }

    /// <summary>
    /// Takes <paramref name="c"/> in <paramref name="slot"/> as the scan's best target when the finder's tests pass (<see cref="Targetable"/>,
    /// <see cref="TakenAsEnemy"/>, the area, path and home tests) and its target score beats the best so far.
    /// </summary>
    static void Candidate(ref Scan s, int slot, CharacterCold c)
    {
        ref readonly var other = ref s.Table.Previous[slot];
        if (!Targetable(ref s, slot, c) || !TakenAsEnemy(ref s, slot, c)) return;

        // The attack-on-sight area test (FUN_140599f30) of the finders 16, 21 and 144.
        if (s.Finder != FoeFinder.TroubleMakers && !InAttackArea(ref s, slot, other.Position)) return;

        // One it found no path to lately is never picked (isPathImpossible in the attack-on-sight finder's score, docs/game/combat.md "Target
        // score"): else a guard shut in with its enemy (a cage, a locked room) takes it as target again every look and never turns on anyone it can
        // reach.
        if (s.Cold is { } looker && PursuitSystem.PathImpossible(looker, new CharacterId(slot, other.Generation), (long)s.Tick))
            return;

        // 144 takes only an enemy within its home (InTerritory).
        if (s.Finder == FoeFinder.Territory && !InTerritory(ref s, slot, other.Position)) return;

        // The target score (Combat.TargetScore; Taunt and Passive weigh in there), after the Hold post test for a holding looker. Strictly better
        // only, so a tie keeps the lower slot.
        float d2 = Vector3.DistanceSquared(other.Position, s.Eye);
        float score = Score(ref s, slot, c, d2) ?? float.NegativeInfinity;
        if (!(score > s.BestScore)) return;

        s.Best = slot;
        s.BestD2 = d2;
        s.BestScore = score;
    }

    /// <summary>
    /// The candidate's own gates: a standing character not caged, not an owned slave the looker may not fight, not staying low inside a building.
    /// </summary>
    static bool Targetable(ref Scan s, int slot, CharacterCold c)
    {
        // A caged prisoner is nobody's target, an owned slave nobody's but its owner's enemies' (engine choice: the original's sensed-list filter is
        // not decoded; town cages hold enemies of their guards, and slaves of a hostile faction, such as Heng's Outlaws, stand among them). The
        // territorial finder (144, FUN_14059fd70, Verified) skips a slave only when the looker's faction is allied with its owner.
        if (c is not { Medical.Incapacitated: false, IsImprisoned: false }) return false;

        if (c.SlaveState == 1
            && (s.Finder == FoeFinder.Territory
                ? s.Relations.Contains(c.SlaveOwner) && s.Relations.IsAlly(s.Faction, c.SlaveOwner)
                : !IsEnemy(s.Relations, s.Faction, c.SlaveOwner)))
        {
            return false;
        }

        // The attack-on-sight finder (FUN_14059f3a0, Verified) skips a target whose posture is not 0 (staying low or crawling) while it stands inside
        // a building. A chained slave of the looker's own faction takes the slave test instead (dropped while enslaved, a target once escaped);
        // Meitou has no chain flag, so state 1 stands for it.
        return !(c.Medical?.Prone == true && s.Sight.BuildingOf(slot) is not null && !(c.SlaveState == 1 && c.SlaveOwner == s.Faction));
    }

    /// <summary>
    /// Whether the finder takes the candidate for an enemy: the relations (a disguise, the freed rule, neutrals for 21), a campaign assault's
    /// target, a campaign force's member dropped, the squads' temporary tags and a contract's employer.
    /// </summary>
    static bool TakenAsEnemy(ref Scan s, int slot, CharacterCold c)
    {
        ref readonly var other = ref s.Table.Previous[slot];

        // Wanted characters are the arrest system's (Crime.ArrestSystem): the police spot, fine or arrest them there. A working disguise stands in
        // for the target's faction (slot 125, docs/game/perception.md "Sense tags"; Crime.ArrestSystem.SeenFaction).
        int seenAs = Crime.ArrestSystem.SeenFaction(c);

        // A freed prisoner (Freed, FUN_14079c950's last test, Observed) attacks no one for the relations alone, and is attacked so only by a looker
        // of a faction that is not real or below -30 by default.
        bool hostile = AttacksOnSight(s.Relations, s.Faction, seenAs) && s.Cold is not { Freed: true }
            && (!c.Freed || s.Relations.Factions[s.Faction] is { } mine && (mine.NotReal || mine.DefaultRelation < FreedHostility));
        bool warEnemy = s.War.IsSet && WarEnemy(ref s, c, other.Position);
        bool neutrals = s.Finder == FoeFinder.EnemiesAndNeutrals;
        bool enemy = hostile || neutrals && IsNeutral(s.Relations, s.Faction, seenAs) || warEnemy;

        // A campaign force's member is sensed as NEUTRAL, not ENEMY (FUN_1408580d0 over FUN_140796240, docs/game/perception.md "Sense tags"): a raid
        // walks up to talk first. A looker that attacks neutrals too still drops it unless it is hostile to its faction (FUN_1408569a0, Verified);
        // its own campaign's targets stay, and a TEMP_ENEMY tag (below) makes it an enemy again.
        if (enemy && (!neutrals || !IsEnemy(s.Relations, s.Faction, seenAs)) && !warEnemy && c.SquadId >= 0
            && s.World.Squads.Find(c.SquadId) is { PlatoonId: >= 0 } force && s.World.Platoons.Find(force.PlatoonId)?.Campaign is not null)
        {
            enemy = false;
        }

        // The squads' temporary tags (FUN_14079c950, other faction only): the looker's squad holds TEMP_ENEMY on the target, or, for a player-faction
        // looker, the target's squad on it, makes an enemy; the looker's squad holding TEMP_ALLY on the target unmakes one.
        if (s.Memory is { } memory && c.Faction != s.Faction)
        {
            var target = new CharacterId(slot, other.Generation);

            if (s.Squad >= 0 && memory.HasShort(s.World, s.Squad, target, Dialogue.MemoryTag.TempEnemy)
                || s.World.Player.Faction == s.Faction && c.SquadId >= 0 && memory.HasShort(s.World, c.SquadId,
                    new CharacterId(s.Self, s.Table.Previous[s.Self].Generation), Dialogue.MemoryTag.TempEnemy))
            {
                enemy = true;
            }
            else if (enemy && s.Squad >= 0 && memory.HasShort(s.World, s.Squad, target, Dialogue.MemoryTag.TempAlly))
                enemy = false;
        }

        // A contract's employer (Contract_HiredAlly / Contract_DefendPlayerBase assessNeutral, and HiredAlly's assessEnemy for the employer's allies,
        // Verified): TEMP_ALLY, not an enemy.
        if (enemy && s.Employer >= 0
            && (c.Faction == s.Employer || s.EmployerAllies && s.Relations.Contains(c.Faction) && s.Relations.IsAlly(s.Employer, c.Faction)))
        {
            enemy = false;
        }

        return enemy;
    }

    /// <summary>
    /// The area test of the attack-on-sight finders (<c>FUN_140599f30</c>, <b>Verified</b>): a target in the looker's own building passes; else the
    /// AI's area by manoeuvre mode (<see cref="Orders.InArea"/>); outside it a target still passes while it fights the looker.
    /// </summary>
    static bool InAttackArea(ref Scan s, int slot, Vector3 at)
    {
        if (s.World.Sight.BuildingOf(s.Self) is { } own && s.World.Sight.BuildingOf(slot) == own) return true;
        if (Orders.InArea(s.Cold!.Manoeuvre, s.Eye, s.Cold.CentreOfMovement, at)) return true;

        return s.Combat?.StateOf(new CharacterId(slot, s.Table.Previous[slot].Generation)) is { TargetSlot: var target } && target == s.Self;
    }

    /// <summary>
    /// Territorial (144, <c>FUN_14059fd70</c>, <b>Verified</b>): without a home circle anyone; else one within the circle, or outside it in the
    /// looker's own building.
    /// </summary>
    static bool InTerritory(ref Scan s, int slot, Vector3 at) =>
        s.Home.R2 == 0 || Vector2.DistanceSquared(new Vector2(at.X, at.Z), s.Home.Centre) < s.Home.R2
        || s.World.Sight.BuildingOf(s.Self) is { } inside && s.World.Sight.BuildingOf(slot) == inside;

    /// <summary>
    /// The attack-on-sight score of <paramref name="c"/> in <paramref name="slot"/> (goal 16's finder picks by <c>FUN_14059e300</c>); null when
    /// dropped.
    /// </summary>
    static float? Score(ref Scan s, int slot, CharacterCold c, float d2)
    {
        ref readonly var other = ref s.Table.Previous[slot];
        var fight = s.Combat?.StateOf(new CharacterId(slot, other.Generation)) ?? CombatSlot.Fresh(other.Generation);

        if (s.HoldPost && !Orders.HoldPost(s.Eye, s.HoldPoint, other.Position, other.Velocity, other.Yaw, c.Medical!.Crawling
            || fight.AttackTech >= 0, c.InCombat))
        {
            return null;
        }

        var candidate = new TargetCandidate
        {
            Staggered = fight.StumbleClip >= 0 && (int)s.Tick < fight.StunUntil,
            Attackers = (s.Combat?.AttackerCount(s.World, slot) ?? 0) + (s.Pending is { } pending ? pending.GetValueOrDefault(slot) : 0),
            Taunt = (c.Orders & CharacterOrders.Taunt) != 0,
            Passive = (c.Orders & CharacterOrders.Passive) != 0,
            Distance = MathF.Sqrt(d2),
            Fighting = c.InCombat,
            Gunner = c.Gunning is not null,
            Crippled = c.Medical!.Crawling || c.Medical.Maimed || c.Fighter is not { CanAttack: true },
            Peaceful = !c.InCombat && !IsEnemy(s.Relations, c.Faction, s.Faction),
            GettingUp = CharacterFacts.GettingUp(c, (long)s.Tick),
        };

        // No player range clamp: only the in-fight chooser sets the clamp flag of FUN_14059e300 (param_7), every goal finder and the look pass 0
        // (Verified), so the attack-on-sight look reaches beyond 400.
        return TargetScore.Score(candidate, false, s.Hold, s.Prone, s.Animal, s.Cold?.Manoeuvre ?? ManoeuvreMode.RoamFar);
    }

    /// <summary>
    /// A campaign assault's extra enemy (<see cref="WarTargets"/>): of the target faction, or with <c>Everyone</c> the characters
    /// <c>Package_WarAssault</c>'s sensing hook takes (<c>FUN_14028a3f0</c>, <b>Verified</b>): not of the looker's faction, no animal, of a faction
    /// that is real and not a bandit one (the faction's <c>fundamental type</c> 8, not the character's class), and in no town or in the assault's
    /// target town (no distance test).
    /// </summary>
    static bool WarEnemy(ref Scan s, CharacterCold c, Vector3 at)
    {
        var war = s.War;
        if (!war.IsSet || c.Faction == s.Faction) return false;
        if (c.Faction == war.Faction) return true;

        if (!war.Everyone || c.Fighter?.IsAnimal == true || !s.Relations.Contains(c.Faction)
            || s.Relations.Factions[c.Faction] is { NotReal: true } or { FundamentalType: FundamentalType.Bandit })
        {
            return false;
        }

        if (s.Towns is not { } towns) return true;

        var town = AiSystem.TownAt(towns, s.World, at);
        return town.IsNone || town == war.Town;
    }

    /// <summary>
    /// Neither an ally (nor the same faction) nor hostile, and not a faction the looker's coexists with (one way, FUN_14059f720 over FUN_1406b23c0,
    /// Verified): whom ATTACK_ENEMIES_AND_NEUTRALS adds to the enemies.
    /// </summary>
    public static bool IsNeutral(FactionRelations relations, int mine, int theirs) =>
        relations.Contains(theirs) && !relations.IsAlly(mine, theirs) && !relations.IsHostile(mine, theirs) && !relations.Coexists(mine, theirs);

    /// <summary>
    /// Whether a looker of faction <paramref name="mine"/> takes a sensed character of <paramref name="theirs"/> for an attack-on-sight target by the
    /// relations: hostile mine to theirs (-30 or lower) and <paramref name="mine"/>'s own <c>coexistence</c> does not list <paramref name="theirs"/>
    /// (<c>FUN_14059f3a0</c> over <c>FUN_1406b23c0</c>, which reads only the looker's table, <b>Verified</b>; the sense tag does not read
    /// coexistence, docs/game/perception.md). Same faction and allies are never hostile.
    /// </summary>
    public static bool AttacksOnSight(FactionRelations relations, int mine, int theirs) =>
        relations.Contains(theirs) && relations.IsHostile(mine, theirs) && !relations.Coexists(mine, theirs);

    /// <summary>
    /// Whether the factions are enemies in either direction's coexistence sense: hostile mine to theirs and neither lists the other in
    /// <c>coexistence</c>. Engine choice for the systems that have no looker (trade, crime, flight, fights far away, targets of a fight already on):
    /// the original's attack on sight reads the looker's side only (<see cref="AttacksOnSight"/>).
    /// </summary>
    public static bool IsEnemy(FactionRelations relations, int mine, int theirs) =>
        relations.Contains(theirs) && relations.IsHostile(mine, theirs) && !relations.Coexists(mine, theirs) && !relations.Coexists(theirs, mine);

    #endregion

    #region The player's attack on sight

    /// <summary>
    /// This tick's targets of the player characters whose look (or a squad mate's) found an enemy, for row 16 Attacking enemies: Hold's goal
    /// (<see cref="HoldAttack"/>) and the job (<see cref="AttackJob"/>).
    /// </summary>
    readonly SightOffers _offers = new();

    /// <summary>
    /// Registers Hold's goal and the job 16 with the decision step.
    /// </summary>
    public void RegisterSources(DecisionSystem decisions)
    {
        decisions.Register(new HoldAttack(this));
        decisions.RegisterJob(Data.Gameplay.Ai.TaskType.AttackEnemies, new AttackJob(this));
    }

    /// <summary>
    /// The job 16 Attacking enemies (Shift with an attack; it runs with the jobs switch off, docs/game/ai-tasks.md "Perma-jobs") as the decision
    /// step's contributor (step 5): its finder is the original's attack on sight (<c>FUN_14059f3a0</c>, "Combat goals"), which this system's look
    /// stands in for: the enemy the look (or a squad mate's) offered this tick is attacked (row 16, the job's provenance), with the row's desire (0
    /// while Passive, sneaking outside a fight or not standing, <see cref="WantsToAttack"/>). The fight's end asks a decision.
    /// </summary>
    sealed class AttackJob(AggroSystem owner) : IJobSource
    {
        /// <summary>
        /// Runs while its fight is the job's; else may run with the row's desire and an enemy offered.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;

            if (cold.Running is { Source: TaskSource.Job, Row: Data.Gameplay.Ai.TaskType.AttackEnemies } run && run.Job == k
                && run.Runs((CharacterTask)table.Next[slot].Task))
            {
                return DecisionCheck.Runs;
            }

            if (!WantsToAttack(cold)) return DecisionCheck.No("desire 0 (Passive, sneaking or not standing)");

            return owner._offers.Offered(world, slot) >= 0 ? DecisionCheck.Run : DecisionCheck.No("no enemy sighted");
        }

        /// <summary>
        /// Attacks the enemy offered this tick, as the job, with the fighter's alert voice.
        /// </summary>
        public void Start(World world, int slot, int k)
        {
            var table = world.Characters;
            int t = owner._offers.Offered(world, slot);
            if (t < 0) return;

            var targetId = table.IdOf(t);
            world.Defer(new AttackOrder([table.IdOf(slot)], targetId) { Tick = world.Tick + 1, Source = TaskSource.Job, Job = k });
            owner.Started++;
            world.Sounds?.Enqueue(new SoundEvent(SoundKind.Alert, table.IdOf(slot), targetId, table.Next[t].Position));   // its alert voice
        }
    }

    /// <summary>
    /// Goal 16 Attacking enemies as Hold adds it to a player character (priority 3; its weight is <b>Unknown</b>, 1 here; desire 1, 0 as
    /// <see cref="HoldsGround"/> says): the enemy this tick's look found (<see cref="Apply"/>) is attacked. It runs while an attack on sight (row 16)
    /// of a goal is its fight.
    /// </summary>
    sealed class HoldAttack(AggroSystem owner) : IDecisionSource
    {
        /// <summary>
        /// The source's name in the decision log.
        /// </summary>
        public string Name => "16 Attack on sight (Hold)";
        /// <summary>
        /// A goal (priority 3).
        /// </summary>
        public DecisionStep Step => DecisionStep.Goals;
        /// <summary>
        /// After the goals <c>setupAI</c> lists: Hold adds it when it is turned on.
        /// </summary>
        public int Key => 1000;

        /// <summary>
        /// Runs while its fight is the goal's; else may run on Hold with the desire and an enemy offered.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var task = (CharacterTask)table.Next[slot].Task;

            if (task == CharacterTask.Attack && cold.Running is { Row: Data.Gameplay.Ai.TaskType.AttackEnemies, Source: TaskSource.Goal } run
                && run.Runs(task))
            {
                return DecisionCheck.Runs;
            }

            if (!Orders.Holding(cold)) return DecisionCheck.No("not on Hold");
            if (!HoldsGround(cold)) return DecisionCheck.No("desire 0 (Passive, sneaking or not standing)");

            return owner._offers.Offered(world, slot) >= 0 ? DecisionCheck.Run : DecisionCheck.No("no enemy sighted");
        }

        /// <summary>
        /// Attacks the enemy offered this tick, with the fighter's alert voice.
        /// </summary>
        public void Start(World world, int slot)
        {
            var table = world.Characters;
            int t = owner._offers.Offered(world, slot);
            if (t < 0) return;

            var targetId = table.IdOf(t);
            world.Defer(new AttackOrder([table.IdOf(slot)], targetId) { Tick = world.Tick + 1 });
            owner.Started++;
            world.Sounds?.Enqueue(new SoundEvent(SoundKind.Alert, table.IdOf(slot), targetId, table.Next[t].Position));   // its alert voice
        }
    }

    /// <summary>
    /// Whether one of the player's characters has Hold's attack on sight: Hold adds goal 16 (standing order 12), whose desire
    /// (<c>AI::scoreAttackEnemies</c>) is 0 while sneaking outside a fight, Passive or not standing. It is a priority-3 goal: the decision step
    /// weighs it after the character's orders and before its jobs (<see cref="HoldAttack"/>, docs/game/ai-tasks.md "Precedence"). Its targets pass
    /// the Hold post test (<see cref="Orders.HoldPost"/>).
    /// </summary>
    static bool HoldsGround(CharacterCold? cold) => cold is not null && Orders.Holding(cold) && WantsToAttack(cold);

    /// <summary>
    /// Row 16's desire (<c>AI::scoreAttackEnemies</c>) above 0: not Passive, not sneaking outside a fight, standing.
    /// </summary>
    static bool WantsToAttack(CharacterCold cold) =>
        (cold.Orders & CharacterOrders.Passive) == 0 && ((cold.Orders & CharacterOrders.Sneak) == 0 || cold.InCombat)
            && cold.Medical is { Prone: false };

    /// <summary>
    /// Whether one of the player's characters looks for enemies like an NPC: it has the job 16 "Attacking enemies" and the row's desire
    /// (<see cref="WantsToAttack"/>). Whether its sighting is attacked is the decision step's (<see cref="AttackJob"/>: its orders, goals and higher
    /// jobs first).
    /// </summary>
    static bool HuntsEnemies(CharacterCold cold) =>
        WantsToAttack(cold) && Production.PermaJobs.IndexOf(cold, Data.Gameplay.Ai.TaskType.AttackEnemies) >= 0;

    #endregion

    #region Decide

    /// <summary>
    /// The <see cref="Effect.Kind"/> of a sighting: <c>Target</c> is the enemy, <c>Amount</c> the distance.
    /// </summary>
    public const int SightEffect = 1;

    /// <summary>
    /// The <see cref="Effect.Kind"/> of a hostile looker's exposure of a sneaker: <c>Target</c> is the sneaker, <c>Amount</c> its
    /// <see cref="StealthExposure"/>.
    /// </summary>
    public const int ExposureEffect = 2;

    /// <summary>
    /// The <see cref="Effect.Kind"/> of a sleeper that nearly woke at a sneaking player-side character's noise: <c>Target</c> is the sneaker (event
    /// <see cref="AlmostWokeUpEvent"/>).
    /// </summary>
    public const int AlmostWokeEffect = 3;

    /// <summary>
    /// The <see cref="Effect.Kind"/> of a package's sensing hook tagging a character in the looker's squad memory: <c>Target</c> is the character,
    /// <c>Amount</c> the short-term tag (<see cref="Dialogue.MemoryTag"/>).
    /// </summary>
    public const int TagEffect = 4;

    /// <summary>
    /// The <see cref="Effect.Kind"/> of a hearing check that reached the stealth XP step: <c>Source</c> the listener, <c>Target</c> the heard
    /// character, <c>Amount</c> 1 when it was heard (<c>FUN_1408c7330</c>).
    /// </summary>
    public const int HearingXpEffect = 5;

    /// <summary>
    /// The <see cref="Effect.Kind"/> of a sensed character the looker's passing-by assault adds to its target set (<c>Target</c>;
    /// <see cref="Ai.Blackboard.AssaultTargets"/>).
    /// </summary>
    public const int AssaultTargetEffect = 6;

    /// <summary>
    /// The dialogue event a sleeper says when it nearly wakes at a sneaking player (ALMOST_WOKE_UP, docs/game/dialogue.md).
    /// </summary>
    public const int AlmostWokeUpEvent = 47;

    /// <summary>
    /// How long a watcher stays on a sneaker's list after its last look (<c>FUN_1408586d0</c>: entries older than 4 s are dropped, Verified literal).
    /// </summary>
    public const float WatcherSeconds = 4;

    /// <summary>
    /// The player characters given a fight by this tick's sightings (<see cref="Free"/>).
    /// </summary>
    readonly HashSet<int> _taken = [];

    /// <summary>
    /// The tick <see cref="_taken"/> was last cleared on (-1: never).
    /// </summary>
    long _takenTick = -1;

    /// <summary>
    /// The fights started since the last <see cref="TakeLog"/> (<see cref="AggroSettings.RecordLog"/>).
    /// </summary>
    readonly List<AggroEntry> _log = [];

    /// <summary>
    /// The fights started since the last <see cref="TakeLog"/> (with <see cref="AggroSettings.RecordLog"/>).
    /// </summary>
    public IReadOnlyList<AggroEntry> Log => _log;

    /// <summary>
    /// Takes the log (the host, between ticks).
    /// </summary>
    public AggroEntry[] TakeLog()
    {
        AggroEntry[] all = [.. _log];
        _log.Clear();
        return all;
    }

    /// <summary>
    /// Commits one effect of the look, by its kind.
    /// </summary>
    public void Apply(World world, in Effect e)
    {
        switch (e.Kind)
        {
            case ExposureEffect:
                Expose(world, e);
                break;
            case HearingXpEffect:
                HearingXp(world, e.Source, e.Target, ((int)e.Amount & 2) != 0, ((int)e.Amount & 1) != 0);
                break;
            case AlmostWokeEffect:
                world.Defer(new Dialogue.DialogueEvent(world.Characters.IdOf(e.Source), world.Characters.IdOf(e.Target), AlmostWokeUpEvent)
                {
                    Tick = world.Tick + 1,
                });
                break;
            case AssaultTargetEffect:
                AddAssaultTarget(world, e);
                break;
            case TagEffect:
                Tag(world, e);
                break;
            case SightEffect:
                TakeSighting(world, e);
                break;
        }
    }

    /// <summary>
    /// An <see cref="ExposureEffect"/>: the sneaker keeps the highest exposure any looker gave it this tick, and a looker that noticed it goes on
    /// its watcher list.
    /// </summary>
    static void Expose(World world, in Effect e)
    {
        if (world.Characters.Cold(e.Target) is not { } sneaker) return;

        var level = (StealthExposure)(int)e.Amount;
        if (sneaker.StealthExposureTick != world.Tick || level > sneaker.StealthExposure)
            sneaker.StealthExposure = level;

        sneaker.StealthExposureTick = world.Tick;
        if (level >= StealthExposure.Noticed) Watch(world, sneaker, world.Characters.IdOf(e.Source), level);
    }

    /// <summary>
    /// An <see cref="AssaultTargetEffect"/>: the looker's passing-by assault adds the sensed character to its target set.
    /// </summary>
    static void AddAssaultTarget(World world, in Effect e)
    {
        if (world.Characters.Cold(e.Source) is { SquadId: >= 0 } member
            && world.Squads.Find(member.SquadId)?.Ai is { CurrentClass: PackageClass.PassingAssault } board)
        {
            board.AssaultTargets.Add(world.Characters.IdOf(e.Target));
        }
    }

    /// <summary>
    /// A <see cref="TagEffect"/>: the looker's squad memory tags the sensed character, both still alive.
    /// </summary>
    void Tag(World world, in Effect e)
    {
        if (Memory is { } memory && world.Characters.Cold(e.Source) is { SquadId: >= 0 } looker && world.Characters.Next[e.Source].Alive
            && world.Characters.Next[e.Target].Alive)
        {
            memory.Flag(world, looker.SquadId, world.Characters.IdOf(e.Target), (int)e.Amount);
        }
    }

    /// <summary>
    /// A <see cref="SightEffect"/>: an NPC with a mind, and its squad mates, are asked to think; a free player character and its free mates that
    /// would take the target are offered it for the decision step.
    /// </summary>
    void TakeSighting(World world, in Effect e)
    {
        if (_takenTick != world.Tick)
        {
            _taken.Clear();
            _takenTick = world.Tick;
        }

        var table = world.Characters;
        var next = table.Next;
        int i = e.Source, t = e.Target;
        if (!next[t].Alive || table.Cold(t) is not { Medical.Incapacitated: false } target) return;

        // An NPC the AI runs decides by its own goals in the chooser (docs/game/ai-tasks.md "Combat goals"): the sighting only asks it, and the squad
        // mates that share it, to think at the next tick (AiMind.Replan), where its attack goals' finders (FindFoe) and the rest of its goals are
        // weighed.
        if (table.Cold(i)?.Ai is { } minded)
        {
            minded.Replan = true;
            JoinMates(world, table, i, t, target, null);
            return;
        }

        // A player character takes this fight through row 16 only, Hold's goal (priority 3) or its job: it and its free mates whose own look would
        // take the target are offered it, and the decision step weighs it against their orders, goals and jobs.
        if (table.Cold(i) is not { IsPlayer: true } || !Free(table, i, _taken)) return;

        var fighters = new List<CharacterId> { table.IdOf(i) };
        _taken.Add(i);
        JoinMates(world, table, i, t, target, fighters);
        _offers.Offer(world, fighters, table.IdOf(t));
    }

    /// <summary>
    /// One hearing check's stealth XP for the heard character (<c>FUN_1408c7330</c>, <b>Verified</b> amounts; docs/game/character-stats.md "Stealth
    /// (heard check)"): 2 when the listener regards the heard as an enemy, 2 more when the heard is in the listener's home, x20 when heard, times the
    /// exposure factor, which then falls x0.7. Nothing for one down, carried or in a bed or cage (the factor still falls). The XP is added to
    /// <see cref="CharacterCold.HearingXp"/> for <see cref="BodySystem"/> to pay. Not gated to the player's faction (the original has no such test
    /// here; only the sneak update that resets the factor is).
    /// </summary>
    static void HearingXp(World world, int listener, int target, bool enemy, bool heard)
    {
        var table = world.Characters;
        if (table.Cold(target) is not { Stats: not null, Medical: { } medical } cold) return;

        float amount = XpService.HearingAmount(enemy, InListenersHome(world, listener, target, cold) || cold.SlaveState == 1, heard);
        if (medical.Dead || medical.Incapacitated || !cold.CarriedBy.IsNone || cold.InSomething != Locks.HeldState.None)
            amount = 0;

        cold.HearingXp += amount * cold.StealthFactor;
        cold.StealthFactor *= XpService.HearingFactorDecay;
    }

    /// <summary>
    /// The home rule of the stealth XP (<c>FUN_140857500</c> at its end, <b>Observed</b>): the heard character stands in the building that is the
    /// listener's squad's home and is not using anything (in a bed or cage), unless the building is public and the squad's byte <c>+0xe8</c> (meaning
    /// <b>Unknown</b>, read as clear) is clear; the original then falls back on the heard being a slave (status 1), which the caller adds.
    /// </summary>
    static bool InListenersHome(World world, int listener, int target, CharacterCold heard)
    {
        if (heard.InSomething != Locks.HeldState.None || world.Sight.BuildingOf(target) is not { } building
            || world.Characters.Cold(listener) is not { } lc || world.Squads.Find(lc.SquadId) is not { } squad)
        {
            return false;
        }

        return (squad.Ai?.OwnHome ?? squad.HomeBuilding)?.Key == building && world.Fixtures.PublicOf(building) == false;
    }

    /// <summary>
    /// Puts <paramref name="looker"/> on the sneaker's watcher list at <paramref name="level"/> (or updates it) and drops lapsed entries.
    /// </summary>
    static void Watch(World world, CharacterCold sneaker, CharacterId looker, StealthExposure level)
    {
        var list = sneaker.Watchers ??= [];
        long lapse = world.Tick - (long)MathF.Ceiling(WatcherSeconds / world.TickSeconds);
        list.RemoveAll(w => w.Tick < lapse || w.Looker == looker);
        list.Add(new StealthWatcher(looker, level, world.Tick));
    }

    /// <summary>
    /// The looker's squad mates share its sighting, not its attack: the original shares a squad's sightings (<c>GroupSense</c>: a member counts a
    /// character a mate saw or heard as seen within 2 R, with no view cone or light test, <c>FUN_140857b10</c>, Observed), and each member attacks
    /// only by its own goals. A mate within twice its own range R of the target that has a mind is asked to think at the next tick (its attack goals
    /// read the mates' sightings, <see cref="FindFoe"/>); a player character that is free is offered the target with the looker
    /// (<paramref name="fighters"/>) when its own attack on sight takes it (<see cref="Takes"/>), in the squad's member order. Null
    /// <paramref name="fighters"/>: only the asking.
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

            if (fighters is null || !cold.IsPlayer || !Free(table, s, _taken) || !Takes(world, s, cold, target, targetCold))
                continue;

            fighters.Add(m);
            _taken.Add(s);
        }
    }

    /// <summary>
    /// A fight an attack or protect goal of the AI started (<see cref="Ai.AiSystem"/>'s serial step, docs/game/ai-tasks.md "Combat goals"): counted
    /// with this system's fights and logged (<see cref="AggroSettings.RecordLog"/>; nobody joined with it), and for an attack goal the fighter's
    /// alert voice. Serial phases only.
    /// </summary>
    internal void GoalFight(World world, int slot, int target, bool voice)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        var targetId = table.IdOf(target);
        Started++;
        if (voice) world.Sounds?.Enqueue(new SoundEvent(SoundKind.Alert, id, targetId, table.Next[target].Position));

        if (Settings.RecordLog)
        {
            _log.Add(new AggroEntry(world.Tick, id, targetId, table.Cold(slot)!.Faction, table.Cold(target)!.Faction,
                Vector3.Distance(table.Next[slot].Position, table.Next[target].Position)));
        }
    }

    /// <summary>
    /// Whether the character in <paramref name="slot"/> would attack <paramref name="target"/> had it sensed it itself: its own look's gates (a slave
    /// and a faction without relations pick no fight) and its attack-on-sight finder (<see cref="Candidate"/>: the relations, a holder's post test).
    /// Serial phases only.
    /// </summary>
    bool Takes(World world, int slot, CharacterCold cold, int target, CharacterCold targetCold)
    {
        if (cold.SlaveState == 1 || !Relations.Contains(cold.Faction)) return false;

        var war = cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { War.IsSet: true } assaulting ? assaulting.War : WarTargets.None;
        var scan = NewScan(world, slot, cold, war, MathF.Max(0, RangeOf(cold, Settings.SightRange)), null, keepSenses: false);
        Candidate(ref scan, target, targetCold);
        return scan.Best == target;
    }

    /// <summary>
    /// Whether the character may take a sighting's fight: not taken this tick, not busy, able, not held by a turret or a gun.
    /// </summary>
    static bool Free(CharacterTable table, int slot, HashSet<int> taken)
    {
        if (taken.Contains(slot) || !table.Next[slot].Alive) return false;

        byte task = table.Next[slot].Task;
        if (Busy(table.Cold(slot), task)) return false;

        return table.Cold(slot) is { InCombat: false, Fleeing: false, IsImprisoned: false, Fighter.CanAttack: true, Medical.Incapacitated: false,
            Gunning: null, Ai: null or { Behaviour: not AiBehaviour.Turret } } cold
            // a carrier fights like anyone (docs/game/carry-loot.md "A carrier in a fight"); a turret guard keeps to its turret (task 146: only a
            // melee attacker takes it off)
            && (!cold.IsPlayer || HuntsEnemies(cold) || HoldsGround(cold));
    }

    /// <summary>
    /// Fighting; for a character the decision step does not decide also treating (a medic looks for no fight) or resting (not an AI's sleeper). A
    /// player character's look is offered to the step whatever its task (its orders, goals and jobs decide, docs/game/ai-tasks.md "Precedence").
    /// </summary>
    static bool Busy(CharacterCold? cold, byte task) =>
        task == (byte)CharacterTask.Attack || cold is null
        || !DecisionSystem.Decides(cold) && (task == (byte)CharacterTask.Treat || task == (byte)CharacterTask.Rest && !AiSleeper(cold));

    /// <summary>
    /// A character the AI put to bed or sat down (<see cref="Ai.AiBehaviour.Sleep"/>, <see cref="Ai.AiBehaviour.Sit"/>): unlike one resting by order,
    /// it can be roused into a fight.
    /// </summary>
    static bool AiSleeper(CharacterCold? cold) => cold?.Ai is { Behaviour: AiBehaviour.Sleep or AiBehaviour.Sit };

    #endregion

    #region Chase give-up and stand down

    /// <summary>
    /// No give-up before this many game seconds of a chase: the original's 5 game minutes (<c>ChaseMode</c>, Verified literal; 1 game hour = 1200/11
    /// s).
    /// </summary>
    public const float GiveUpGateSeconds = 5f / 60 * 1200 / 11;

    /// <summary>
    /// A target unsensed for longer than this is given up: the original's 12 sense-age units of hours x 3600 / 33 x 0.5 (<c>FUN_140852150</c>), i.e.
    /// 24 s.
    /// </summary>
    public const float GiveUpSenseSeconds = 24;

    /// <summary>
    /// A chaser within this of where it last sensed an unsensed target gives it up (the original's squared 3600, Verified literal).
    /// </summary>
    public const float GiveUpReach = 60;

    /// <summary>
    /// The chase age in sense-age units past which the plan is reset every tick (<c>FUN_14033a800</c>, Verified literal 60: 1.1 game hours).
    /// </summary>
    public const float ChaseResetSenseUnits = 60;

    /// <summary>
    /// Every tick, serial: an NPC's chase is given up by the original's rule (<see cref="Chase"/>) and, its fight over, it stands down.
    /// </summary>
    public void SlowWorld(World world)
    {
        var table = world.Characters;
        Sync(table);   // characters spawned this tick
        if (!Settings.AttackOnSight) return;

        var next = table.Next;

        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || !world.Steps(i)) continue;   // a far character is looked at on its step (the far cadence)

            var cold = table.Cold(i)!;

            // The player's characters never give up a chase (the original's gate: the chaser's faction is not the player's); after a fight they are
            // decided again (the decision step).
            if (DecisionSystem.Decides(cold)) continue;

            ref var s = ref _slots[i];

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
            StandDown(world, i, ref n, in s, cold);
        }
    }

    /// <summary>
    /// A tracked NPC whose fight is over (idle, standing, out of combat mode) goes back to its routine: a follow row whose fight was its step
    /// follows again, else the routine it had before the fight is taken up again.
    /// </summary>
    static void StandDown(World world, int i, ref CharacterHot n, in Slot s, CharacterCold cold)
    {
        var table = world.Characters;
        if (cold.Medical is { Incapacitated: true } || cold.InCombat || n.Task != (byte)CharacterTask.Idle)
            return;   // down (it wakes into wandering) or busy with something else

        // A follow row whose fight was a step of it follows again under the row (as the decision step does for the characters it decides); else
        // the routine is taken up again as the fight's own end (a step: the provenance stays).
        if (MovementSystem.FightStepOver(table, i, cold) && MovementSystem.FollowAgain(world, i, cold)) return;

        TaskStart.Stand(table, i);
        TaskStart.Step(table, i, (CharacterTask)(s.Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Follow or (byte)CharacterTask.Idle ? s.Task
            : s.Task == (byte)CharacterTask.GoTo ? (byte)CharacterTask.Idle : (byte)CharacterTask.Wander));
        n.TaskTime = 0;
    }

    /// <summary>
    /// The original's chase give-up (<c>ChaseMode</c>, docs/game/combat.md "Leaving a fight"), read from the chaser's sense entry of its target:
    /// after <see cref="GiveUpGateSeconds"/> of a chase, a target with no entry is given up, and one its last look did not sense when the entry is
    /// older than <see cref="GiveUpSenseSeconds"/> or the chaser stands within <see cref="GiveUpReach"/> of the entry's place. The chaser stops and
    /// says so to the target (event 60 GIVE_UP_CHASE). Engine choice: the original tests this only in its path chase (fight state 11), Meitou in
    /// every running chase (<see cref="CharacterCold.CombatRunning"/>), since how the original lets go on open ground is Unknown.
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

        // Past 60 sense-age units (1.1 game hours) of chase the plan is reset on every tick while it goes on (FUN_14033a800).
        if ((world.Tick - s.ChaseStart) * world.TickSeconds > ChaseResetSenseUnits * SenseUnitSeconds) ResetPlan(cold);

        SenseEntry entry = default;
        bool known = s.Senses is { } senses && senses.TryGet(new CharacterId(t, next[t].Generation), out entry);
        if (known && entry.Now) return;

        var here = new Vector2(next[i].Position.X, next[i].Position.Z);

        // Engine choice: a runner within GiveUpReach of the chaser itself has not got away, though a look missed it (a chaser running past it has it
        // at its back); a sneaker has.
        if (Vector2.DistanceSquared(here, new Vector2(next[t].Position.X, next[t].Position.Z)) < GiveUpReach * GiveUpReach
            && (table.Cold(t)!.Orders & CharacterOrders.Sneak) == 0)
        {
            return;
        }

        if (known && (world.Tick - entry.Tick) * world.TickSeconds <= GiveUpSenseSeconds
            && Vector2.DistanceSquared(here, new Vector2(entry.Place.X, entry.Place.Z)) >= GiveUpReach * GiveUpReach)
        {
            return;
        }

        s.ChaseStart = -1;
        ResetPlan(cold);   // the caller ends the task and resets the plan (FUN_14034ca40:36-48): the next think plans afresh
        world.Defer(new StopCommand([table.IdOf(i)]) { Tick = world.Tick + 1 });
        world.Defer(new Dialogue.DialogueEvent(table.IdOf(i), table.IdOf(t), 60) { Tick = world.Tick + 1 });
    }

    /// <summary>
    /// The plan reset (<c>FUN_1405074a0</c>): the pending steps and the committed goal are dropped, so the next think chooses again.
    /// </summary>
    static void ResetPlan(CharacterCold cold) => cold.Ai?.ResetPlan();

    #endregion

    #region Hash

    /// <summary>
    /// Hashes the live relation table (once a deed changed it), the slots and the fights started.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        hasher.Add(Relations.Changes);
        if (Relations.Changes > 0) HashRelations(ref hasher);

        hasher.Add(_size);
        for (int i = 0; i < _size; i++) HashSlot(ref hasher, _slots[i]);

        hasher.Add(Started);
    }

    /// <summary>
    /// Hashes the relation table: every pair, the ranks and trusts set, and the wars.
    /// </summary>
    void HashRelations(ref StateHasher hasher)
    {
        int n = Relations.Count;

        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++) hasher.Add(Relations.Get(a, b));

        for (int a = 0; a < n; a++)
        {
            if (Relations.Rank(a) is var rank and not 0)
            {
                hasher.Add(a);
                hasher.Add(rank);
            }
        }

        for (int a = 0; a < n; a++)
        {
            if (Relations.GlobalTrust(a) is var trust and not 0)
            {
                hasher.Add(a);
                hasher.Add(trust);
            }

            for (int b = 0; b < n; b++)
            {
                if (Relations.AtWar(a, b))
                {
                    hasher.Add(a);
                    hasher.Add(b);
                }
            }
        }
    }

    /// <summary>
    /// Hashes one slot: its chase, senses, spot meters and hearing memory (the meters and memory only while it has any).
    /// </summary>
    static void HashSlot(ref StateHasher hasher, in Slot s)
    {
        hasher.Add(s.Generation);
        hasher.Add(s.Tracked);
        hasher.Add(s.Task);
        if (s.ChaseStart >= 0) hasher.Add(s.ChaseStart);   // only while a chase runs

        s.Senses?.Hash(ref hasher);

        if (s.Spots is { } spots)   // only while a looker has meters, so worlds without sneakers hash as before
        {
            foreach (var p in spots)
            {
                hasher.Add(p.Target);
                hasher.Add(p.Generation);
                hasher.Add(p.Value);
            }
        }

        if (s.Heard is { } heard)   // only while a listener remembers a hearing check or an alarm
        {
            foreach (var h in heard)
            {
                hasher.Add(h.Target);
                hasher.Add(h.Generation);
                hasher.Add(h.Checked);
                hasher.Add(h.AlarmUntil);
                hasher.Add(h.Heard);
            }
        }
    }

    #endregion
}
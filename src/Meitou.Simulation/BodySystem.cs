using WorldLayout = Meitou.Data.World.WorldLayout;
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>
/// The bodies of the characters (stage 7, docs/game/character-stats.md): every tick it runs each living character's <see cref="MedicalState.Tick"/> (blood,
/// bleeding, part healing, hunger, the knock-out timer), sets the top speed from the speed chain (<see cref="Speed.Run"/>: athletics, legs, hunger,
/// encumbrance) and stops a character that has just been knocked out or has died. It runs in the Act phase, before the animation system, and writes only its own
/// slots. A corpse lying free is removed <see cref="CorpseHours"/> game hours after it died (<see cref="SlowWorld"/>), one luckyren Mode burst at the end of its tick (<see cref="Burst"/>).
/// </summary>
/// <remarks>
/// The medical tick takes the tick's seconds of character time (<see cref="World.TickSeconds"/>, character-stats.md "Time"); hunger converts them to game hours.
/// <see cref="BodyTimeScale"/> multiplies the time of the part and blood rates (not hunger or the knock-out timer), see <see cref="MedicalContext.BodyTimeScale"/>.
/// The bed a character lies in comes from <paramref name="db"/> (none without it).
/// </remarks>
public sealed class BodySystem(GameConstants constants, BodyOptions? options = null, float bodyTimeScale = 1, GameDatabase? db = null) : ITickSystem
{
    /// <summary>Game hours in one tick: 11/36000 h is 1/30 game s at the Verified 1200/11 s per game hour (<c>GameClock</c>).</summary>
    public const float HoursPerTick = 11f / 36000;
    /// <summary>How long a corpse lies before it decays, in game hours (<c>FUN_1405ccd90</c>, <b>Verified</b> literal: "corpse decayed" past 12).</summary>
    public const float CorpseHours = 12;
    public const long CorpseTicks = (long)(CorpseHours / HoursPerTick);
    /// <summary>While a corpse is carried or held in a bed or cage its clock is kept at most this many game hours old (<c>FUN_1405ccd90</c>, <b>Verified</b> literal).</summary>
    public const float HeldCorpseHours = 6;
    /// <summary>The ticks 12 game hours take at this world's tick length (<see cref="World.SecondsPerGameHour"/>).</summary>
    public static long CorpseTicksOf(World world) => (long)(CorpseHours * World.SecondsPerGameHour / world.TickSeconds);

    public GameConstants Constants { get; } = constants;
    public BodyOptions Options { get; } = options ?? BodyOptions.Default;
    /// <summary>The <see cref="MedicalContext.BodyTimeScale"/> every character gets (default 1: the documented rates).</summary>
    public float BodyTimeScale { get; } = bodyTimeScale;
    readonly XpService xp = new(constants, options ?? BodyOptions.Default);
    /// <summary>A bed record's kind and healing value without research (<see cref="BedOf"/>); filled from the parallel phase.</summary>
    readonly ConcurrentDictionary<string, (BedKind Kind, float Factor)> beds = new();

    /// <summary>How far from a bed's spot an AI sleeper counts as lying in it (it sleeps at the spot without being held in the bed).</summary>
    const float BedReach = 3;

    /// <summary>
    /// The bed a character lies in (character state 1; <c>FUN_140652000</c>, character-stats.md "Beds"): the one it is held in (the player's beds, <see cref="Locks.BedSystem"/>), else for an
    /// AI asleep at its home's bed spot the bed fixture there. Its kind is the functionality's <c>function</c> (6 BF_BED, 25 BF_SKELETON_BED) and its factor the
    /// Building's <c>+0x3c0</c>: <c>output rate</c> x <c>production mult</c> x the research production multiplier. None outside a bed.
    /// </summary>
    /// <summary>
    /// The health word tooltip's "Recovery rate: {1}x" (<c>FUN_14088a8f0</c>): in a bed (state 1) of class 6 whose rate is above 1, that rate; null otherwise. A repair bed has none.
    /// </summary>
    public float? BedRecoveryRate(World world, CharacterCold cold, in CharacterHot n) =>
        cold.InSomething == Locks.HeldState.Bed && BedOf(world, cold, in n) is (BedKind.Bed, var factor) && factor > 1 ? factor : null;

    (BedKind Kind, float Factor) BedOf(World world, CharacterCold cold, in CharacterHot n)
    {
        if (db is null) return (BedKind.None, 1);
        var fixture = cold.InSomething == Locks.HeldState.Bed && cold.InWhat is { } id ? world.Fixtures.Find(id)
            : n.Task == (byte)CharacterTask.Rest && cold.Ai?.Behaviour == Ai.AiBehaviour.Sleep ? world.Fixtures.BedAt(n.Position, BedReach) : null;
        if (fixture is null) return cold.InSomething == Locks.HeldState.Bed ? (BedKind.Bed, 1) : (BedKind.None, 1);
        var (kind, factor) = beds.GetOrAdd(fixture.RecordId, rid =>
        {
            if (db.Find(rid) is not { } bed) return (BedKind.Bed, 1);
            foreach (var f in bed.GetReferences("functionality").Select(r => db.Find(r.TargetStringId)))
                if (f?.GetInt("function") is (int)BedKind.Bed or (int)BedKind.RepairBed)
                    return ((BedKind)f.GetInt("function"), bed.GetFloat("output rate", 1) * f.GetFloat("production mult", 1));
            return (BedKind.Bed, bed.GetFloat("output rate", 1));
        });
        return (kind, factor * world.Research.Upgrade(world, fixture.RecordId, fixture.Owner).ProductionMult);
    }

    /// <summary>
    /// A living animal grows (<c>CharacterAnimal</c> slot 0xe8, <c>FUN_1405cdf00</c>, docs/game/animals.md "Ageing", <b>Observed</b>): while its age is below
    /// <see cref="AnimalData.GrowUntil"/> it gains <paramref name="dt"/> / (day length x <c>lifespan</c>) per update. The age is its stats multiplier; its size lerp(age, scale min,
    /// scale max) is the parts' HP multiplier, the blood capacity's and the hunger rate's k at once (the original reads slot 0x390 each time). The drawn size follows only in
    /// steps: once the age has moved more than 0.05 past <see cref="CharacterCold.ShownAge"/> it is noted, and below 0.95 the look is refreshed to it.
    /// </summary>
    static void Age(CharacterCold cold, AnimalData animal, CharacterStats stats, MedicalState medical, float dt)
    {
        if (stats.Scale >= AnimalData.GrowUntil || animal.AgePerSecond <= 0) return;
        stats.Scale += dt * animal.AgePerSecond;
        medical.SetAnimalBody(animal.Scale(stats.Scale), animal.HpMult);
        if (stats.Scale - cold.ShownAge > AnimalData.ResizeStep && stats.Scale < AnimalData.ResizeBelow) cold.ShownAge = stats.Scale;
    }

    /// <summary>Per slot: not safe to get up this tick (<see cref="MedicalContext.PlayDead"/>), from <see cref="Schedule"/>.</summary>
    bool[] playDead = [];

    /// <summary>How often a character playing dead looks again whether it is safe (engine choice, in ticks; the first test, as its knockout ends, is at once).</summary>
    const int PlayDeadRecheckTicks = 15;

    /// <summary>The vision range inside a building (<c>FUN_140851bc0</c>, Verified literal, no race multiplier).</summary>
    const float InsideSight = 500;

    /// <summary>
    /// Who may not get up this tick: a knocked-out person (not an animal) whose knockout ends, or who plays dead, when <see cref="SafeToGetUp"/> fails. Serial, from the last tick's state.
    /// </summary>
    public void Schedule(World world)
    {
        var table = world.Characters;
        if (playDead.Length < table.HighWater) Array.Resize(ref playDead, table.HighWater);
        var combat = world.SystemOf<Combat.CombatSystem>();
        for (int i = 0; i < table.HighWater; i++)
        {
            bool was = playDead[i];
            playDead[i] = false;
            if (!table.Previous[i].Alive || table.Cold(i) is not { Animal: null, Medical: { Unconscious: true, Dead: false, HasStatsObject: true } medical } cold) continue;
            if (medical.PlayingDead && was && (world.Tick + i) % PlayDeadRecheckTicks != 0) { playDead[i] = true; continue; }
            if (medical.PlayingDead || medical.WakesWithin(world.TickSeconds)) playDead[i] = !SafeToGetUp(world, combat, i, cold);
        }
    }

    /// <summary>
    /// <c>Character::isItSafeToGetUp</c> (docs/game/ragdolls.md "Playing dead", <b>Observed</b>): a slave, or no enemy sensed, or a conscious ally sensed.
    /// </summary>
    static bool SafeToGetUp(World world, Combat.CombatSystem? combat, int me, CharacterCold cold)
    {
        if (cold.SlaveState == 1 || combat is null) return true;
        var (enemies, _, allies) = Senses(world, combat, me, cold);
        return enemies == 0 || allies > 0;
    }

    /// <summary>
    /// The sense counts playing dead and the ordered get-up read (<c>SensoryData</c> <c>numEnemies</c>, <c>numNeutrals</c>, <c>numConsciousAllies</c>, tallied by <c>FUN_1408593d0</c> and
    /// <c>FUN_1408580d0</c>, <b>Verified</b>): the character's senses as of its last look (<see cref="CharacterFacts.Senses"/>); none before it.
    /// </summary>
    internal static (int Enemies, int Neutrals, int Allies) Senses(World world, Combat.CombatSystem combat, int me, CharacterCold cold) =>
        CharacterFacts.Senses(world, me) is { } b ? (b.Enemies, b.Neutrals, b.Allies) : (0, 0, 0);

    /// <summary>
    /// Getting up on the player's order (<c>Task_GetUp</c> start <c>FUN_14033ca10</c> with <c>playerWantsMeToGetUp</c>, then <c>FUN_1408c6780</c>, <b>Observed</b>; the Toughness tooltip's
    /// "Get up and fight instead of playing dead"): with an enemy or neutral sensed and no conscious ally, toughness gains
    /// <see cref="XpService.ToughnessFromGettingUp"/> for the enemies sensed.
    /// </summary>
    internal void GotUpByOrder(World world, int slot, CharacterCold cold)
    {
        cold.OrderedUp = true;
        if (world.SystemOf<Combat.CombatSystem>() is not { } combat || cold is not { Stats: { } stats, Race: { } race, Medical: { } medical }) return;
        var (enemies, neutrals, allies) = Senses(world, combat, slot, cold);
        if (enemies + neutrals >= 1 && allies == 0) xp.ToughnessFromGettingUp(stats, race, medical, enemies);
    }

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        List<MedicalEvent>? events = null;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            var cold = table.Cold(i)!;
            if (cold.Medical is not { } medical || cold.Race is not { } race || cold.Stats is not { } stats) continue;
            ref var n = ref next[i];
            if (medical.Dead && cold.DiedTick < 0) cold.DiedTick = world.Tick;   // also when something outside the tick killed it (a hit)
            // One lying in a bed keeps its task there (it would get up with any other, BedSystem); only its path goes.
            if (medical.Incapacitated && cold.InSomething == Locks.HeldState.Bed && n.Task == (byte)CharacterTask.Sleep) n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
            else if (medical.Incapacitated && (n.Task != (byte)CharacterTask.Idle || (n.Flags & (ushort)MoveFlags.AnyPath) != 0))
            {
                TaskStart.Stand(table, i);
                n.Velocity = Vector3.Zero;
            }
            if (medical.Dead) continue;
            if (cold.Animal is { } animal) Age(cold, animal, stats, medical, world.TickSeconds);
            // The light level the stat multiplier's light term reads (the original's Character +0xd8, kept by the senses).
            medical.Light = world.Sight.Light(i);
            // The load: the inventory weight, plus carry person weight while carrying someone, against the strength the injuries leave (character-stats.md "Encumbrance and carrying").
            float load = cold.Inventory?.ContentWeight() ?? 0;
            bool carrying = !cold.Carrying.IsNone;
            float encumbrance = load <= 0 && !carrying ? 1 : Encumbrance.Factor(Constants, load, carrying, stats.Strength, medical.StatMultiplier(Meitou.Data.Gameplay.Bodies.StatsEnumerated.Strength));
            var (bed, bedFactor) = BedOf(world, cold, in n);
            var ctx = new MedicalContext(Constants, Options, race)
            {
                EncumbranceFactor = encumbrance,
                Toughness = stats.Toughness,
                Strength = stats.Strength,
                Seed = world.Seed,
                CharacterKey = Rng.Key(table.IdOf(i)),
                BodyTimeScale = BodyTimeScale,
                // In a bed (state 1) a character rests: degeneration stops, the bed scales healing, blood and hunger take the resting rates; held (state 2) the organic rate doubles.
                Bed = bed,
                BedFactor = bedFactor,
                RepairRate = bed == BedKind.RepairBed ? bedFactor : 0,
                Resting = bed != BedKind.None,
                HungerFloor = HungerFloor(world, cold, medical),
                HealMult = cold.InSomething == Locks.HeldState.Imprisoned ? 2 : 1,
                PlayDead = i < playDead.Length && playDead[i],
                UseHungerRate = UseHungerRate(cold, in n),
            };
            // The combat system's own context (hits) reads the encumbrance from the fighter, so it matches the one used here.
            if (cold.Fighter is { } fighter) fighter.EncumbranceFactor = encumbrance;
            if (world.BloodMarks is { } marks && medical.Bleeding > 0 && cold.WaterState == 0) BloodTrail(world, marks, i, cold, medical.Bleeding, race, in n);
            events ??= [];
            events.Clear();
            medical.Tick(world.TickSeconds, ctx, events);
            n.MaxSpeed = Speed.Run(race, stats, medical, encumbrance, statsFactor: cold.Fighter?.AthleticsFactor ?? 1);
            cold.SwimSpeed = Speed.Swim(race, stats, medical, encumbrance);
            // Movement XP only for the player's faction and not while fight steering or swinging (FUN_140660dc0, Verified; Meitou: not on the Attack task).
            if ((cold.IsPlayer || world.Player.Faction >= 0 && cold.Faction == world.Player.Faction) && n.Task != (byte)CharacterTask.Attack)
                TrainOnTheMove(stats, race, medical, in n, cold.WaterState == 3, carrying, encumbrance, world.TickSeconds);
            // The stealth XP the listeners' hearing checks gave since the last tick (FUN_1408c7330, summed by AggroSystem.Apply; docs/game/character-stats.md "Stealth (heard check)").
            if (cold.HearingXp != 0)
            {
                xp.HearingCheck(stats, race, cold.HearingXp);
                cold.HearingXp = 0;
            }
            // Sneak XP (FUN_1408586d0 / FUN_1408c65a0, Verified): the player's faction only, not in a bed or cage or carried, while moving, and only with someone about; a watcher
            // of the last 4 s gives the noticed rate. The original also pays x0.1 to a sneaker standing still that a hostile targets (not modelled: none then).
            if ((cold.Orders & CharacterOrders.Sneak) != 0 && !medical.Incapacitated && (cold.IsPlayer || world.Player.Faction >= 0 && cold.Faction == world.Player.Faction)
                && cold.InSomething == Locks.HeldState.None && cold.CarriedBy.IsNone && n.Velocity.X * n.Velocity.X + n.Velocity.Z * n.Velocity.Z > SneakMoving * SneakMoving)
            {
                cold.StealthFactor = 1;   // the sneak XP step resets the hearing checks' exposure factor (FUN_1408c65a0)
                var exposure = PaidExposureOf(world, cold);
                if (exposure != StealthExposure.Unobserved || SomeoneAbout(world, i)) xp.Sneaking(stats, race, exposure, world.TickSeconds);
            }
            if (cold.IsPlayer || table.IdOf(i) == world.Player.Inspected) cold.Extended = ExtendedOf(cold, stats, race, medical, n.MaxSpeed, encumbrance, ctx.Resting, carrying, ctx.UseHungerRate);
            n.Health = Math.Clamp(medical.Blood / MathF.Max(MedicalState.BloodCapacity(race, stats.Strength), 1) * 100, 0, 100);
            foreach (var e in events)
            {
                if (e.Kind is MedicalEventKind.Died or MedicalEventKind.KnockedOut)
                {
                    if (cold.InSomething != Locks.HeldState.Bed) TaskStart.Stand(table, i);   // one in a bed stays in it
                    n.Velocity = Vector3.Zero;
                }
            }
        }
    }

    /// <summary>
    /// The blood a bleeding character out of the water leaves (<c>FUN_140652000</c>, docs/game/blood-decals.md): standing still (by its carrier's speed when carried) its pool
    /// grows; walking, a smear every 4 units, as opaque as the bleeding (at most 1); carried, drops of min(0.5, bleeding / 10) once a build-up of random × bleeding × dt × 10
    /// passes 1, where it has moved more than a unit. Display only (<see cref="World.BloodMarks"/>): the trail's state on the cold row is not hashed.
    /// </summary>
    static void BloodTrail(World world, System.Collections.Concurrent.ConcurrentQueue<BloodMark> marks, int slot, CharacterCold cold, float bleeding, RaceData race, in CharacterHot n)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        var velocity = table.IsAlive(cold.CarriedBy) ? table.Previous[cold.CarriedBy.Slot].Velocity : n.Velocity;
        var at = n.Position;
        float moved = Vector2.DistanceSquared(new Vector2(cold.BloodMark.X, cold.BloodMark.Z), new Vector2(at.X, at.Z));
        if (velocity.X == 0 && velocity.Z == 0)
        {
            marks.Enqueue(new BloodMark(BloodMarkKind.Pool, id, at, default, bleeding, race.BloodColour));
            cold.BloodMark = at;
        }
        else if (cold.CarriedBy.IsNone)
        {
            if (moved <= 16) return;
            cold.BloodMark = at;
            marks.Enqueue(new BloodMark(BloodMarkKind.Smear, id, at, new Vector2(velocity.X, velocity.Z), Math.Clamp(bleeding, 0, 1), race.BloodColour));
        }
        else if (bleeding > 0.01f)
        {
            float roll = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.BloodTrail, (ulong)world.Tick * 3));
            cold.BloodDrip += roll * bleeding * world.TickSeconds * 10;
            if (cold.BloodDrip <= 1) return;
            cold.BloodDrip = 0;
            if (moved <= 1) return;
            float x = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.BloodTrail, (ulong)world.Tick * 3 + 1)), z = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.BloodTrail, (ulong)world.Tick * 3 + 2));
            marks.Enqueue(new BloodMark(BloodMarkKind.Drip, id, at, new Vector2(x - 0.5f, z - 0.5f), MathF.Min(bleeding * 0.1f, 0.5f), race.BloodColour));
            cold.BloodMark = at;
        }
    }

    /// <summary>
    /// The numbers behind the information panel's extended lines (<c>FUN_140895350</c>, docs/game/ui-screens.md section 4). Toughness XP (<c>FUN_1408c5f40</c>, <b>Verified</b> in game, #82):
    /// 1, or 2 with the left arm crippled, plus 2 for the right arm, 2 while the blood-loss trauma flag is set, and 2 for the "Critical wounds" term, which tests the worst part's
    /// fraction (at most 1) against 15 and so is always on: a whole character reads 3x. Strength XP is the carrying rate here; the published HUD replaces it in combat mode
    /// (<see cref="WithCombatMode"/>), which needs the fight and turret state of other characters.
    /// </summary>
    /// <summary>The functionality <c>hunger rate</c> of the machine a character works or the chair it sits on, while it stands still out of combat (<see cref="MedicalContext.UseHungerRate"/>); null otherwise.</summary>
    static float? UseHungerRate(CharacterCold cold, in CharacterHot n)
    {
        if (cold.InCombat || n.Velocity.LengthSquared() > 1) return null;
        if (n.Task == (byte)CharacterTask.Operate && cold.Operating) return cold.OperateHungerRate;
        if (n.Task == (byte)CharacterTask.Rest && cold.Ai is { SitClip: not null } mind) return mind.SitHungerRate;
        // An NPC worker at its town building (AiBehaviour.Work, at its work clip), which the original works through Task_OperateMachine.
        if (cold.Ai is { WorkActive: true, WorkClip: { } clip } worker && cold.ActionClip == clip) return worker.WorkHungerRate;
        return null;
    }

    ExtendedStats ExtendedOf(CharacterCold cold, CharacterStats stats, RaceData race, MedicalState medical, float speed, float encumbrance, bool resting, bool carrying, float? useRate)
    {
        float runValue = Speed.RunValue(race, stats.Athletics);
        float toughness = XpService.ToughnessFactor(medical);
        bool rest = resting || medical.Unconscious || medical.Dead;
        float hunger = race.HungerRate * MedicalState.HungerSituationFactor(Constants, rest, false, encumbrance, useRate);
        return new ExtendedStats(XpService.AthleticsRate(encumbrance, speed, runValue), xp.StrengthCarryRate(encumbrance, carrying), toughness,
            MedicalState.KoPoint(Constants, stats.Toughness), speed, runValue, hunger, rest, WorkHungerRate: useRate);
    }

    /// <summary>
    /// The extended numbers as the HUD shows them (<c>FUN_140895350</c>): in combat mode (<c>Character::isInCombatMode(1, 1)</c>, <paramref name="combatMode"/>) Strength XP is CharStats
    /// <c>+0x184</c>, <see cref="XpService.WeaponWeightStrengthFactor"/> of the drawn weapon's weight (<see cref="Combat.CombatSystem.HandWeight"/>, a crossbow's too), or with nothing
    /// drawn (current weapon type 5) 1 - the encumbrance factor.
    /// </summary>
    internal ExtendedStats WithCombatMode(ExtendedStats x, CharacterCold cold, bool combatMode)
    {
        if (!combatMode || cold.Stats is not { } stats || cold.Medical is not { } medical) return x;
        bool fists = Combat.CombatSystem.HandCategory(cold) == (int)Meitou.Data.Gameplay.Combat.WeaponCategory.Unarmed;
        float rate = fists ? 1 - (cold.Fighter?.EncumbranceFactor ?? 1)
            : xp.WeaponWeightStrengthFactor(Combat.CombatSystem.HandWeight(cold), stats.Strength, medical.StatMultiplier(Meitou.Data.Gameplay.Bodies.StatsEnumerated.Strength));
        return x with { StrengthXp = rate, Fighting = true, Fists = fists };
    }

    /// <summary>
    /// XP outside combat (character-stats.md "Levelling"; the movement tick <c>FUN_1408c6660</c>): athletics while running, strength while carrying a load. A character counts as
    /// running when it moves faster than its race walk speed (+1); whether walking trains too is <b>Unknown</b> (the doc names running). In deep water (<paramref name="deepWater"/>)
    /// swimming trains instead of athletics (<b>Verified</b>, <c>FUN_1408c6660</c>). Strength rises only when the load
    /// encumbers it, and only while it moves. The speed the stat sees is the velocity the movement system left this tick.
    /// </summary>
    /// <summary>A sneaker's exposure for its stealth XP: an exposure older than a second (a looker's scan interval with room to spare) has lapsed.</summary>
    internal static StealthExposure ExposureOf(World world, CharacterCold cold) =>
        cold.StealthExposureTick >= world.Tick - (long)MathF.Ceiling(1 / world.TickSeconds) ? cold.StealthExposure : StealthExposure.Unobserved;

    /// <summary>
    /// The exposure a sneaker's stealth XP is paid at: <see cref="ExposureOf"/>, raised to noticed by a watcher of the last 4 s. The Stealth tooltip's "XP rate:" shows its rate
    /// (the original prints the rate its XP call stored, <c>FUN_14088f9a0</c>, <c>FUN_1408c65a0</c>), so both read this.
    /// </summary>
    internal static StealthExposure PaidExposureOf(World world, CharacterCold cold)
    {
        var exposure = ExposureOf(world, cold);
        return exposure < StealthExposure.Noticed && Watched(world, cold) ? StealthExposure.Noticed : exposure;
    }

    /// <summary>The speed above which a sneaker counts as moving for its XP (engine stand-in for the movement's moving flag).</summary>
    const float SneakMoving = 0.1f;

    /// <summary>A watcher of the last <see cref="AggroSystem.WatcherSeconds"/> that noticed or detected it and is alive and not carried (the original's watcher list scan).</summary>
    static bool Watched(World world, CharacterCold cold)
    {
        if (cold.Watchers is not { Count: > 0 } list) return false;
        long lapse = world.Tick - (long)MathF.Ceiling(AggroSystem.WatcherSeconds / world.TickSeconds);
        var table = world.Characters;
        foreach (var w in list)
            if (w.Tick >= lapse && w.Level >= StealthExposure.Noticed && table.TryResolveNext(w.Looker, out int s) && table.Cold(s) is { CarriedBy.IsNone: true }) return true;
        return false;
    }

    struct AboutScan
    {
        public CharacterTable Table;
        public int Self, PlayerFaction;
        public bool Found;
    }

    /// <summary>
    /// Someone the sneaker senses (the original's sensed enemies and neutrals, <b>engine stand-in</b>): a conscious, uncarried character not of the player's faction within
    /// <see cref="AggroSystem.OutdoorSight"/>.
    /// </summary>
    static bool SomeoneAbout(World world, int slot)
    {
        var at = world.Characters.Next[slot].Position;
        var scan = new AboutScan { Table = world.Characters, Self = slot, PlayerFaction = world.Player.Faction };
        world.Grid.Query(at.X, at.Z, AggroSystem.OutdoorSight, ref scan, static (int other, ref AboutScan st) =>
        {
            if (st.Found || other == st.Self || !st.Table.Previous[other].Alive || st.Table.Cold(other) is not { IsPlayer: false } c) return;
            if (c.Faction == st.PlayerFaction || c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone) return;
            st.Found = true;
        });
        return scan.Found;
    }

    void TrainOnTheMove(CharacterStats stats, RaceData race, MedicalState medical, in CharacterHot n, bool deepWater, bool carrying, float encumbrance, float dt)
    {
        if (medical.Incapacitated) return;
        float speed = MathF.Sqrt(n.Velocity.X * n.Velocity.X + n.Velocity.Z * n.Velocity.Z);
        // One gate, speed above 2 (FUN_140660dc0, Verified): walking trains too. In deep water only swimming (FUN_1408c6660).
        if (speed <= MoveXpSpeed) return;
        if (deepWater)
        {
            xp.SwimmingInWater(stats, race, carrying, dt);
            return;
        }
        xp.AthleticsFromRunning(stats, race, encumbrance, speed, Speed.RunValue(race, stats.Athletics), dt);
        if (encumbrance < 1) xp.StrengthFromCarrying(stats, race, encumbrance, dt, carrying);
    }

    /// <summary>The speed above which moving trains (<c>DAT_141682ad0</c>).</summary>
    const float MoveXpSpeed = 2;

    /// <summary>The <c>malnourished</c> squads' hunger floors (<see cref="HungerFloor"/>), by squad id; filled from the parallel phase.</summary>
    readonly ConcurrentDictionary<int, float> squadFloors = new();

    /// <summary>
    /// The hunger level a character is kept at for free (character slot <c>0x348</c>, <c>FUN_1405c9ca0</c>, <b>Verified</b> decompile; docs/game/character-stats.md "Free food"):
    /// an animal 3; a slave 1; held in a cage or restraint, none when the player's faction holds it, else 2 (1.15 for an escaped slave); the player's faction none (-10); anyone else
    /// its squad's level, 3, or 1 + 1.2 x a random number once per squad for a SQUAD_TEMPLATE with <c>malnourished</c> (the platoon constructor <c>FUN_1407ee2d0</c>). Without a
    /// database (tests) only animals, slaves and prisoners are fed.
    /// </summary>
    internal float HungerFloor(World world, CharacterCold cold, MedicalState medical)
    {
        const float None = MedicalContext.NoHungerFloor;
        if (!medical.HasStatsObject) return MedicalState.MaxHunger;
        if (cold.SlaveState == 1) return 1;
        if (cold.IsImprisoned)
        {
            var holder = cold.InWhat is { } id ? world.Fixtures.Find(id) : null;
            if (holder is not null && holder.Owner >= 0 && holder.Owner == world.Player.Faction) return None;
            return cold.SlaveState == 0 ? 2 : 1.15f;
        }
        if (cold.IsPlayer || world.Player.Faction >= 0 && cold.Faction == world.Player.Faction) return None;
        if (db is null) return None;   // a world without data (tests, tools): nobody is fed for free
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return MedicalState.MaxHunger;
        // A static factory with its argument: a capturing lambda would allocate a closure on every call (per character, per tick).
        return squadFloors.GetOrAdd(squad.Id, static (id, a) => a.Db.Find(a.Template)?.GetBool("malnourished") == true
            ? 1 + 1.2f * Rng.Float(Rng.Hash(a.Seed, (ulong)id, RngPurpose.Malnourished, 0)) : MedicalState.MaxHunger, (Db: db, Template: squad.TemplateId, world.Seed));
    }

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (table.Next[i].Alive && table.Cold(i) is { Medical.CollapseOnset: true } collapsed) Collapse(world, i, collapsed);
            if (table.Next[i].Alive && table.Cold(i) is { Burst: { } blow } burst) Burst(world, i, burst, blow);
            else if (table.Next[i].Alive && table.Cold(i) is { DiedTick: >= 0 } cold)
            {
                // A corpse that is carried or held in a bed or cage does not decay: its clock is kept at most 6 hours old, so it lies 6 more hours once let go.
                if (!cold.CarriedBy.IsNone || cold.InSomething != Locks.HeldState.None) cold.DiedTick = Math.Max(cold.DiedTick, world.Tick - (long)(HeldCorpseHours * World.SecondsPerGameHour / world.TickSeconds));
                // "corpse unloaded": a corpse in a zone of the map that is not active goes at once, carried or not (FUN_1405ccd90, after the decay test; Verified).
                bool unloaded = world.LoadedZones is { } active && WorldLayout.ZoneOf(table.Next[i].Position.X, table.Next[i].Position.Z) is { X: >= 0 and < WorldLayout.ZoneCount, Y: >= 0 and < WorldLayout.ZoneCount } zone
                    && !active.Contains(zone);
                if (!unloaded && world.Tick - cold.DiedTick <= CorpseTicksOf(world))
                {
                    Flies(world, i, cold);
                    continue;
                }
                var corpse = new CharacterId(i, table.Next[i].Generation);
                world.Uniques.Left(corpse);
                table.Remove(corpse);
            }
        }
    }

    /// <summary>
    /// The first tick a leg is down (<see cref="MedicalState.CollapseOnset"/>): <c>FUN_1405c8330</c> stops the character and clears its orders and tasks (<b>Observed</b>, docs/game/ragdolls.md
    /// "Forced crawl"), so the running task and fight end (<see cref="TaskStart.End"/>, which asks for a new decision; an NPC's AI picks again from idle), and the queued orders go. Not for one
    /// carried or in a bed or cage (the original tests both), nor while its current task is Stumbling (147), Getting up (65) or Standing (28; Meitou has no such task: Stand Up is at once),
    /// which the original's current-task test spares. Runs in the serial slow world, the tick the medical tick set it (the update itself is parallel).
    /// </summary>
    static void Collapse(World world, int slot, CharacterCold cold)
    {
        cold.Medical!.CollapseOnset = false;
        var table = world.Characters;
        long now = world.Tick;
        bool stumbling = cold.StaggeredUntil > now;
        if (cold.InSomething != Locks.HeldState.None || !cold.CarriedBy.IsNone || stumbling || CharacterFacts.GettingUp(cold, now)) return;
        cold.OrderQueue.Clear();
        TaskStart.End(world, slot, TaskEnd.Stopped);
    }

    /// <summary>Game hours after death before flies come (<c>FUN_1405ccd90</c>, <b>Verified</b> literal).</summary>
    public const float FliesHours = 4;

    /// <summary>
    /// Corpse flies (<c>FUN_1405ccd90</c>, docs/game/character-stats.md "Corpses"): a corpse of a race that is not a robot gets them <see cref="FliesHours"/> after its death while the weather
    /// there acts on no one (<see cref="Meitou.Data.World.WeatherAffecting.None"/>) and loses them while it does. A world without weather has none.
    /// </summary>
    static void Flies(World world, int slot, CharacterCold cold)
    {
        var at = world.Characters.Next[slot].Position;
        bool calm = cold.Race is { IsRobot: false } && world.Weather?.At(at.X, at.Z) is { Affect: Meitou.Data.World.WeatherAffecting.None };
        if (!calm) cold.HasFlies = false;
        else if (!cold.HasFlies && (world.Tick - cold.DiedTick) * world.TickSeconds > FliesHours * World.SecondsPerGameHour) cold.HasFlies = true;
    }

    /// <summary>How far a burst character's gear falls (an engine choice).</summary>
    public const float BurstSpread = 30;

    /// <summary>
    /// luckyren Mode (<see cref="World.Luckyren"/>): a character a fatal hit burst. Everything it had (worn, carried, fitted) falls within <see cref="BurstSpread"/> units
    /// around it, an <see cref="BloodMarkKind.Explosion"/> goes to the host and the body is gone at once.
    /// </summary>
    static void Burst(World world, int slot, CharacterCold cold, Vector2 blow)
    {
        var table = world.Characters;
        var id = new CharacterId(slot, table.Next[slot].Generation);
        var at = table.Next[slot].Position;
        if (cold.Inventory is { } inventory)
        {
            ulong n = 0;
            foreach (var item in inventory.Items)
            {
                float angle = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Luckyren, n++)) * MathF.Tau;
                float far = MathF.Sqrt(Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Luckyren, n++))) * BurstSpread;
                float yaw = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Luckyren, n++)) * MathF.Tau;
                item.Section = "main";
                var spot = at + new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * far;
                spot.Y = world.Walkability.GroundHeight(spot.X, spot.Z, at.Y);
                world.GroundItems.Drop(item, spot, yaw);
            }
            inventory.Items.Clear();
        }
        if (world.BloodMarks is { } marks && cold.Race is { } race)
            marks.Enqueue(new BloodMark(BloodMarkKind.Explosion, id, at, blow, race.BleedRate > 0 ? 1 : 0, race.BloodColour, race.StringId));
        world.Uniques.Left(id);
        table.Remove(id);
    }
}

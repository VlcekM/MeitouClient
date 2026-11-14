using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Bashing doors (Task_BashDoor, docs/game/locks.md "Bash door"; docs/simulation.md "Cages, beds and locks"). An NPC attacking a target inside a
/// building shut by locked doors (<see cref="FixtureRegistry.IsShut"/>), itself outside, bashes the building's main door when the original's planner
/// does (<see cref="DoorPlan"/>): it walks there and swings. Each swing (<c>FUN_14029d690</c>, <b>Observed</b>) takes 0.015 x clamp(2 x blunt + 0.5 x
/// cut, 20, 60) off the door's construction progress (1 when whole), an animal's damage at a quarter; at 0 the door breaks: broken, unlocked and
/// opening, so the building opens for paths and the attacker's pursuit carries on.
/// </summary>
public sealed partial class LockSystem
{
    /// <summary>
    /// Ticks between looks for attackers whose target is shut in (engine choice).
    /// </summary>
    public const int BashCheckTicks = 30;
    /// <summary>
    /// The cycle length used when the host gives none (worlds without the animation data).
    /// </summary>
    public const float DefaultBashClipSeconds = 1.5f;
    /// <summary>
    /// Where in the clip's cycle a blow lands (<c>Task_BashDoor</c> update <c>FUN_140348c10</c>, <b>Verified</b>: the blow fires when the
    /// <c>stonemine</c> progress crosses 0.6).
    /// </summary>
    public const float BashBlowProgress = 0.6f;
    /// <summary>
    /// Game seconds one <c>stonemine</c> cycle takes (the clip's length over its record's play speed; set by the host from the animation data). A
    /// human basher's blow lands at 0.6 of every cycle, the first at 0.6 into the first (the task starts with its previous progress at 1). An animal
    /// swings its own attack clip instead (<see cref="BashBlowLands"/>).
    /// </summary>
    public float BashClipSeconds { get; set; } = DefaultBashClipSeconds;

    /// <summary>
    /// How far into an animal's own attack clip it restarts (<c>Task_BashDoor</c>, <b>Verified</b> <c>FUN_140348c10</c>: it restarts the clip once
    /// its progress reads past 0.98, double <c>0x1416c0b90</c>); a human's looping <c>stonemine</c> has no restart.
    /// </summary>
    public const float AnimalRestartProgress = 0.98f;

    /// <summary>
    /// Advances a basher's clip phase (<see cref="CharacterCold.BashTime"/>, seconds into the cycle) by <paramref name="dt"/>; true on the tick the
    /// phase crosses <see cref="BashBlowProgress"/>. An animal swings its own attack technique's clip (<see cref="CombatSystem.AnimalBashSeconds"/>,
    /// <c>FUN_140887420</c>) and restarts it at <see cref="AnimalRestartProgress"/>.
    /// </summary>
    bool BashBlowLands(CharacterCold c, float dt)
    {
        float own = Combat?.AnimalBashSeconds(c) ?? 0;
        bool lands = own > 0 ? BlowLands(c.BashTime, dt, own, out float phase, AnimalRestartProgress * own)
                : BlowLands(c.BashTime, dt, BashClipSeconds, out phase);
        c.BashTime = phase;
        return lands;
    }

    /// <summary>
    /// One step of a swing cycle: the blow lands when the phase crosses 0.6 of the clip (<paramref name="cycle"/>); the phase wraps at
    /// <paramref name="period"/> (the clip's length unless given).
    /// </summary>
    internal static bool BlowLands(float phase, float dt, float cycle, out float next, float period = 0)
    {
        if (period <= 0) period = cycle;
        float blowAt = BashBlowProgress * cycle, t = phase + dt;
        next = t >= period ? t - period : t;
        return phase < blowAt && t >= blowAt;
    }

    /// <summary>
    /// How far out from the leaf the basher walks to (engine choice: a walk goal on the outside; arriving is <see cref="DoorPlan.AtDoor"/>).
    /// </summary>
    public const float BashStandOff = 12;
    /// <summary>
    /// How near the stand spot by a door a lock worker counts as there.
    /// </summary>
    public const float DoorWorkReach = 20;
    /// <summary>
    /// The gap (distance minus both radii) within which a character ahead of the basher stops it (<c>FUN_14033d690</c>).
    /// </summary>
    public const float BashBlockGap = 10;

    /// <summary>
    /// Whose attack targets the bashers follow (null: no bashing, as in worlds without combat).
    /// </summary>
    internal CombatSystem? Combat { get; set; }
    /// <summary>
    /// The blow formulas' constants (null: every swing does the minimum, 20).
    /// </summary>
    internal CombatConstants? CombatConstants { get; set; }

    /// <summary>
    /// Every basher: the doors it smashes are found every check; each one walks to its door, stops when the door no longer needs it, and swings.
    /// </summary>
    void Bashers(World world)
    {
        if (Combat is null) return;
        var table = world.Characters;
        var next = table.Next;
        if (world.Tick % BashCheckTicks == 0)
        {
            _gateGraph = null;
            FindBashers(world);
        }

        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { Bashing: { } doorId } c) continue;
            if (c.BashWall)
            {
                Smash(world, s, c, doorId);
                continue;
            }

            BashDoor(world, next, s, c, doorId);
        }
    }

    /// <summary>
    /// One basher at its door: it stops on a door broken, open or unlocked, walks there, faces it and swings; a breaking blow ends the task.
    /// </summary>
    void BashDoor(World world, Span<CharacterHot> next, int s, CharacterCold c, string doorId)
    {
        var table = world.Characters;
        var door = world.Fixtures.Find(doorId);

        // The task itself ends on a broken door or one open or opening and reads no lock (FUN_140348c10); a planner NPC's plan is dropped once
        // its door is unlocked and the planner would open it (72) instead (see PlanDropped).
        if (door is null || door.DoorBroken || door.Motion is DoorMotion.Open or DoorMotion.Opening || PlanDropped(world, door, c)
                || c.Medical is { Incapacitated: true } || (!c.BashOrdered && !c.BashErrand && next[s].Task != (byte)CharacterTask.Attack)
                        || world.Fixtures.DoorSpotOf(door) is not { } spot)
        {
            StopBashing(table, s, c);
            return;
        }

        var stand = door.IsGate ? GateStandSpot(spot, Flat(next[s].Position)) : StandSpot(door, spot);
        bool there = door.IsGate ? GatePlan.AtGate(next[s], spot) : DoorPlan.AtDoor(next[s], spot);
        if (!there && c.BashOrdered)
        {
            // An ordered smash walks by its order's own goal; a new order (another task) ends it.
            if (next[s].Task != (byte)CharacterTask.GoTo) StopBashing(table, s, c);
            return;
        }

        if (!there)
        {
            WalkToDoor(table, next, s, c, stand);
            return;
        }

        if ((next[s].Flags & (ushort)MoveFlags.AnyPath) != 0)
        {
            next[s].Flags = (ushort)(next[s].Flags & ~(ushort)MoveFlags.AnyPath);
            next[s].PathCursor = 0;
            c.PathRequest = 0;
        }

        var face = new Vector2(spot.Centre.X, spot.Centre.Z) - Flat(next[s].Position);
        if (face.LengthSquared() > 1e-4f) next[s].Yaw = MathF.Atan2(face.X, face.Y);

        if (UnlocksOwn(door, c))
        {
            UnlockOwn(world, s, c, door);
            return;
        }

        // Someone in the way: the task is impossible (the plan fails; an NPC looks again at the next check).
        if (InTheWay(world, s, face))
        {
            StopBashing(table, s, c);
            return;
        }

        if (c.BashOrdered && next[s].Task == (byte)CharacterTask.GoTo) AtWork(world, s);

        // Every update in reach books Burglary against the door's owner, before the blow, for any basher (Task_BashDoor, shared by rows
        // 81/123/226/272/287/290, locks.md).
        if (Crimes is not null && door.Owner >= 0 && door.Owner != c.Faction)
            Crimes.SetPending(world, s, Crime.CrimeCode.Burglary, door.Owner, CharacterId.None, Crime.CrimeSystem.MinTimer);

        if (c.BashTime < 0) c.BashTime = 0;
        if (!BashBlowLands(c, world.TickSeconds)) return;

        // A break's only sound is the door's Break (Task_BashDoor posts no unlock sound), played by the host's door sounds.
        if (Hit(world, door, c))
        {
            world.Fixtures.Touch(door);
            StopBashing(table, s, c);
        }
    }

    /// <summary>
    /// Whether <paramref name="c"/> unlocks <paramref name="door"/> instead of bashing it: a whole door of its own faction that is effectively
    /// locked. The planner's way through a locked door for its owner is row 78 Unlocking door (effect !54, list B 70 <c>isObjectMine</c> and 57 at
    /// the door), then 72 / 122 open it (docs/game/locks.md "Doors"); a locked door is shut for the owner's paths too until then.
    /// </summary>
    static bool UnlocksOwn(Fixture door, CharacterCold c) =>
        !c.BashOrdered && !door.DoorBroken && door.Owner >= 0 && door.Owner == c.Faction && door.Lock is { EffectivelyLocked: true };

    /// <summary>
    /// One tick of an owner at its locked door: the lock is worked for <see cref="LockWorkSeconds"/> (Task_UnlockDoor, 0.3 per game second), then
    /// cleared with the unlock sound and the door starts opening (row 122 / 72), and the pursuit carries on. Others standing at the leaf do not
    /// stop the work (the bash's ray test is Task_BashDoor's own).
    /// </summary>
    void UnlockOwn(World world, int s, CharacterCold c, Fixture door)
    {
        if (c.BashTime < 0) c.BashTime = 0;
        c.BashTime += world.TickSeconds;
        if (c.BashTime < LockWorkSeconds) return;

        world.Fixtures.SetLocked(door, false);
        FixtureRegistry.OpenDoor(door);
        world.EmitSound(SoundKind.Unlocked, s, world.Characters.Next[s].Position);
        StopBashing(world.Characters, s, c);
    }

    /// <summary>
    /// A basher not at its door walks there (the pursuit leaves a basher alone); a failed path ends it.
    /// </summary>
    static void WalkToDoor(CharacterTable table, Span<CharacterHot> next, int s, CharacterCold c, Vector2 stand)
    {
        // Walk there (the pursuit leaves a basher alone); a failed path ends it.
        if ((next[s].Flags & (ushort)MoveFlags.AnyPath) == 0)
        {
            if (c.BashTime < 0)
            {
                StopBashing(table, s, c);
                return;
            }

            next[s].Goal = stand;
            next[s].Mode = SpeedMode.Free;
            next[s].Flags |= (ushort)MoveFlags.NeedPath;
            c.BashTime = -1;   // asked once; arriving resets it
        }
    }

    /// <summary>
    /// A planner basher's door stands unlocked and closed: the planner replans, and only an aggressive character (desire over 72's 1, a bandit) still
    /// bashes it (<see cref="DoorPlan"/>).
    /// </summary>
    bool PlanDropped(World world, Fixture door, CharacterCold c) =>
        !c.BashOrdered && !c.BashErrand && door.Lock is not { EffectivelyLocked: true }
        && (door.IsGate || DoorPlan.AggressiveTowards(world, Combat, c, door.Building, door.Position) <= 1);

    /// <summary>
    /// The door construction progress (1 when whole) one point of <see cref="BlowDamage"/> takes off (<c>DoorStuff::vfunc_62</c>).
    /// </summary>
    public const float ProgressPerDamage = 0.015f;

    /// <summary>
    /// A blow's damage to a door (<c>FUN_14029d690</c>): clamp(2 x blunt + 0.5 x cut, 20, 60), an animal's packet at a quarter first
    /// (<c>FUN_140348c10</c>).
    /// </summary>
    public static float BlowDamage(float cut, float blunt, bool animal)
    {
        float scale = animal ? 0.25f : 1;
        return Math.Clamp(2 * blunt * scale + 0.5f * cut * scale, 20, 60);
    }

    /// <summary>
    /// One swing at a door (<c>FUN_14029d690</c>): true when it broke the door.
    /// </summary>
    bool Hit(World world, Fixture door, CharacterCold c)
            => Damage(world, door, Packet(c) is var (cut, blunt, animal) ? BlowDamage(cut, blunt, animal) : 20);

    /// <summary>
    /// The cut and blunt damage of a basher's blow (its weapon's or fists' packet) and whether it is an animal's; null without the combat constants
    /// (every swing then does the minimum).
    /// </summary>
    (float Cut, float Blunt, bool Animal)? Packet(CharacterCold c)
    {
        if (CombatConstants is not { } k || c.Fighter is not { } f || c.Stats is not { } stats || c.Medical is not { } med)
            return null;

        float Eff(StatsEnumerated stat) => stats[stat] * stats.Scale * med.StatMultiplier(stat);
        float str = Eff(StatsEnumerated.Strength);
        var packet = f.Unarmed
            ? DamageFormulas.UnarmedPacket(DamageFormulas.UnarmedRating(stats[StatsEnumerated.MartialArts] * stats.Scale, f.UnarmedBonus,
                    med.DustAttackPenalty, f.EncumbranceFactor), Eff(StatsEnumerated.Toughness), str, f.FistsItem, TargetKind.Human, null,
                            f.DamageOutput, k)
            : DamageFormulas.Packet(Eff(f.WeaponSkill), Eff(StatsEnumerated.Dexterity) * f.DexterityMult, str, f.Weapon!, TargetKind.Human, null,
                    f.DamageOutput, k);
        return (packet.Cut, packet.Blunt, f.IsAnimal);
    }

    /// <summary>
    /// Takes <paramref name="damage"/> (the clamped f) x <see cref="ProgressPerDamage"/> off a door's <see cref="Fixture.DoorProgress"/>; at 0 it
    /// breaks: unlocked and opened. True when it broke.
    /// </summary>
    static bool Damage(World world, Fixture door, float damage)
    {
        if (door.Kind != FixtureKind.Door || door.DoorBroken) return false;

        // the damage number at the door (FUN_14029d2b0); queued only while the host shows floaters
        world.Floaters?.Enqueue(Floater.BuildingHit(door.Position, (int)damage));
        door.DoorProgress -= damage * ProgressPerDamage;
        if (door.DoorProgress > 0) return false;
        door.DoorProgress = 0;
        if (door.Lock is { } l) l.Locked = false;
        door.DoorBroken = true;
        FixtureRegistry.OpenDoor(door);
        return true;
    }

    /// <summary>
    /// A fight blow whose attack zone takes in a door (<see cref="CombatSystem"/>): <paramref name="damage"/> is clamp(2 blunt + 0.5 cut, 20, 60); a
    /// break tells navigation. The blow posts no character sound (the door's own <c>Break</c> is the host's door sounds).
    /// </summary>
    internal static void Strike(World world, Fixture door, float damage)
    {
        if (Damage(world, door, damage)) world.Fixtures.Touch(door);
    }

    /// <summary>
    /// Gives the free attackers whose target is shut in another building, while they are outside it, its main door to bash when the planner says so
    /// (<see cref="DoorPlan"/>).
    /// </summary>
    void FindBashers(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        List<(string Building, Vector2 Origin, float Radius, Fixture Door, DoorSpot Spot)>? shut = null;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || next[s].Task != (byte)CharacterTask.Attack) continue;
            if (table.Cold(s) is not { IsPlayer: false, Bashing: null, IsImprisoned: false, Medical.Incapacitated: false, Carrying.IsNone: true,
                    Fighter: not null } c)
                continue;

            var st = Combat!.StateOf(table.IdOf(s));
            int t = st.TargetSlot;
            if (t < 0 || t >= table.HighWater || !next[t].Alive || next[t].Generation != st.TargetGeneration) continue;
            if (FindGateBasher(world, s, t, c)) continue;
            shut ??= ShutBuildings(world);
            if (shut.Count == 0) continue;
            var target = Flat(next[t].Position);
            var me = Flat(next[s].Position);
            foreach (var (building, origin, radius, door, spot) in shut)
            {
                if (Vector2.Distance(target, origin) > radius || Vector2.Distance(me, origin) <= radius) continue;
                if (_doorPlan.PlansBash(world, Combat, s, t, building, door, spot, this))
                {
                    c.Bashing = door.Id;
                    c.BashTime = 0;
                }

                break;
            }
        }
    }

    /// <summary>
    /// The planner's view of an attacker whose target is shut in a building, reused across plans.
    /// </summary>
    readonly DoorPlan _doorPlan = new();

    /// <summary>
    /// Sends <paramref name="c"/>, a ransacker whose entry plan bashes the door <paramref name="doorId"/> (row 81), to bash it: it walks there and
    /// swings as an attacker would, and the door breaking ends it.
    /// </summary>
    internal void BashForErrand(CharacterCold c, string doorId)
    {
        c.Bashing = doorId;
        c.BashErrand = true;
        c.BashTime = 0;
    }

    /// <summary>
    /// The shut buildings with their main door (<c>Building::getDoor</c>, one per building; engine reading: the one of lowest door index with a known
    /// leaf).
    /// </summary>
    List<(string, Vector2, float, Fixture, DoorSpot)> ShutBuildings(World world)
    {
        var list = new List<(string, Vector2, float, Fixture, DoorSpot)>();
        foreach (var b in world.Fixtures.Buildings)
        {
            if (world.Fixtures.HasOpenDoor(b)) continue;
            (Fixture Door, DoorSpot Spot)? main = null;
            Vector2 origin = default;
            float radius = 0;
            foreach (var f in world.Fixtures.OfBuilding(b))
            {
                if (f.Kind != FixtureKind.Door || world.Fixtures.DoorSpotOf(f) is not { } spot) continue;
                origin = Flat(f.Position);
                radius = MathF.Max(radius, Vector2.Distance(origin, new Vector2(spot.Centre.X, spot.Centre.Z)));
                if (main is null || f.DoorIndex < main.Value.Door.DoorIndex) main = (f, spot);
            }

            // Inside: nearer the origin than its farthest door (engine reading; the building's hull is not consulted).
            if (main is { } m) list.Add((b, origin, radius, m.Door, m.Spot));
        }

        return list;
    }

    /// <summary>
    /// The spot <see cref="BashStandOff"/> out from the leaf, on the side away from the building's origin.
    /// </summary>
    static Vector2 StandSpot(Fixture door, DoorSpot spot)
    {
        var n = new Vector2(spot.Normal.X, spot.Normal.Z);
        if (n.LengthSquared() < 1e-6f) n = Vector2.UnitY;
        n = Vector2.Normalize(n);
        var centre = new Vector2(spot.Centre.X, spot.Centre.Z);
        if (Vector2.Dot(centre - Flat(door.Position), n) < 0) n = -n;
        return centre + n * BashStandOff;
    }

    /// <summary>
    /// The state of one scan for a body in the basher's way.
    /// </summary>
    struct WayScan
    {
        /// <summary>
        /// The character table scanned.
        /// </summary>
        public CharacterTable Table;
        /// <summary>
        /// The basher's slot.
        /// </summary>
        public int Self;
        /// <summary>
        /// The basher's place and the unit way it faces.
        /// </summary>
        public Vector2 At, Face;
        /// <summary>
        /// The basher's radius, and the distance to the leaf (the line stops there).
        /// </summary>
        public float Radius, Leaf;
        /// <summary>
        /// Whether a body is in the way.
        /// </summary>
        public bool Found;
    }

    /// <summary>
    /// A character ahead of the basher within <see cref="BashBlockGap"/> whose body the line along its facing meets (<c>FUN_14033d690</c>,
    /// <b>Observed</b>: a ray from 12 up along the facing at each sensed character in front with that gap; any character, ally or not). Engine
    /// reading: the ray is the flat line against the body's radius, stopped by the door leaf; a body lying down (the ray starts 12 up) does not
    /// count.
    /// </summary>
    static bool InTheWay(World world, int s, Vector2 face)
    {
        float leaf = face.Length();
        if (leaf < 1e-3f) return false;
        var table = world.Characters;
        var scan = new WayScan { Table = table, Self = s, At = Flat(table.Previous[s].Position), Face = face / leaf, Leaf = leaf,
                Radius = CombatSteering.Radius(table.Cold(s)) };
        world.Grid.Query(scan.At.X, scan.At.Y, scan.Radius + BashBlockGap + CombatTuning.DefaultFootprint * 4, ref scan, static (int o, ref WayScan w)
                =>
        {
            if (w.Found || o == w.Self || !w.Table.Previous[o].Alive || w.Table.Cold(o) is { Medical.Incapacitated: true })
                return;

            var to = Flat(w.Table.Previous[o].Position) - w.At;
            float rc = CombatSteering.Radius(w.Table.Cold(o));
            float along = Vector2.Dot(to, w.Face);
            if (along <= 0 || along >= w.Leaf || to.Length() - w.Radius - rc >= BashBlockGap) return;
            if (MathF.Abs(w.Face.X * to.Y - w.Face.Y * to.X) < rc) w.Found = true;
        });
        return scan.Found;
    }

    /// <summary>
    /// Ends a character's bash: its bash state cleared and its task stopped.
    /// </summary>
    static void StopBashing(CharacterTable table, int s, CharacterCold c)
    {
        c.Bashing = null;
        c.BashWall = false;
        c.BashErrand = false;
        c.BashOrdered = false;
        c.BashTime = 0;
        ref var n = ref table.Next[s];
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
        n.PathCursor = 0;
        c.PathRequest = 0;
        n.TaskTime = 0;   // the pursuit asks for a path to the target at once
    }
}
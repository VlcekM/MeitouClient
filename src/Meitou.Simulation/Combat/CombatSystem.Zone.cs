using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>The most characters a blow's zone holds (FUN_1407862e0's limit of 20).</summary>
    const int ZoneCapacity = 20;

    [InlineArray(ZoneCapacity)]
    struct ZoneSlots
    {
        int first;
    }

    [InlineArray(ZoneCapacity)]
    struct ZoneKeys
    {
        float first;
    }

    struct ZoneScan
    {
        public CharacterTable Table;
        public CombatSlot[] Read;
        public FactionRelations? Relations;
        public int Self, Target, Faction, Squad, Count;
        public bool HitsSlaves;
        public Vector2 From, Centre, Forward;
        public float Y, Half, High, Deep;
        public ZoneSlots Slots;
        public ZoneKeys Keys;
    }

    /// <summary>
    /// The characters each fighter's attack zone held when it last looked (the original's list +0x1e0, <c>calculateTargetsInAttackZone</c> FUN_1406087a0: built at the attack start
    /// and at each blow's impact), <see cref="ZoneCapacity"/> slots per fighter and a count; double-buffered like the slots, so a defender reads the attacker's list of the last tick.
    /// </summary>
    int[] zoneRead = [], zoneWrite = [];
    byte[] zoneCountRead = [], zoneCountWrite = [];

    /// <summary>Whether <paramref name="defender"/> is in <paramref name="attacker"/>'s attack zone list (<c>FUN_140608460</c>), as of the last tick.</summary>
    bool InZone(int attacker, int defender)
    {
        int at = attacker * ZoneCapacity;
        for (int n = 0; n < zoneCountRead[attacker]; n++)
            if (zoneRead[at + n] == defender) return true;
        return false;
    }

    /// <summary>Builds fighter <paramref name="i"/>'s attack zone list for blow <paramref name="blow"/> (its own slot of the write buffer: partition-safe).</summary>
    void RecordZone(World world, int i, in CombatSlot s, CharacterCold cold, CombatTechnique tech, int blow) => Keep(i, Scan(world, i, s, cold, tech, blow));

    void Keep(int i, in ZoneScan scan)
    {
        int at = i * ZoneCapacity;
        for (int n = 0; n < scan.Count; n++) zoneWrite[at + n] = scan.Slots[n];
        zoneCountWrite[i] = (byte)scan.Count;
    }

    void ResizeZones(int size)
    {
        Array.Resize(ref zoneRead, size * ZoneCapacity);
        Array.Resize(ref zoneWrite, size * ZoneCapacity);
        Array.Resize(ref zoneCountRead, size);
        Array.Resize(ref zoneCountWrite, size);
    }

    void CopyZones(int highWater)
    {
        Array.Copy(zoneRead, zoneWrite, highWater * ZoneCapacity);
        Array.Copy(zoneCountRead, zoneCountWrite, highWater);
    }

    void SwapZones()
    {
        (zoneRead, zoneWrite) = (zoneWrite, zoneRead);
        (zoneCountRead, zoneCountWrite) = (zoneCountWrite, zoneCountRead);
    }

    void HashZone(ref StateHasher hasher, int i)
    {
        hasher.Add(zoneCountRead[i]);
        for (int n = 0; n < zoneCountRead[i]; n++) hasher.Add(zoneRead[i * ZoneCapacity + n]);
    }

    /// <summary>
    /// Emits blow <paramref name="blow"/> of fighter <paramref name="i"/>'s attack to everyone in its attack zone (<b>Verified</b>, <c>calculateTargetsInAttackZone</c> FUN_1406087a0
    /// and <c>attackImpactCheck</c> FUN_14060a2f0, docs/game/combat.md "Attack zone"), at most the technique's <c>max simultaneous hits</c>; nobody in it is a whiff and the swing
    /// keeps its 0.5 s pause. The zone is a box ahead along the heading, k the fighter's size, s = lerp(k, 0.45, 1), r its footprint, L its weapon's length: its centre
    /// (L / 4 + r + max(0, attack distance − the distance moved since the swing started)) s ahead, half-extents (r + 0.5, 25, L / 2) s, or (13, 8, L / 2) s for a left or
    /// right cut; then h = max(0, (offset − k r) / 2) moves the centre back by h and deepens it by h. In it: a body not knocked out that is the fighter's target or an enemy
    /// that is not an ally. Engine readings: a body counts when its footprint circle (size-scaled) meets the box, the height test is the box's half-height plus 9 from the
    /// fighter's feet, and the order is the target first, then by distance (the original's is the physics query's).
    /// </summary>
    void Strike(World world, int i, in CombatSlot s, CharacterCold cold, CombatTechnique tech, int blow, EffectBuffer effects)
    {
        var scan = Scan(world, i, s, cold, tech, blow);
        Keep(i, in scan);
        int hits = Math.Min(scan.Count, Math.Max(1, tech.MaxSimultaneousHits));
        for (int n = 0; n < hits; n++) effects.Emit(i, scan.Slots[n], BlowEffect, s.AttackSeq, new Vector3(s.AttackTech, 0, 0), blow);
        // The walls and doors in it take the hits left (looked up in the serial apply: the building lookups are not thread-safe).
        int left = Math.Max(1, tech.MaxSimultaneousHits) - hits;
        if (left > 0 && (Construction is not null || Locks is not null)) effects.Emit(i, i, ZoneBuildingsEffect, s.AttackSeq, new Vector3(left, s.AttackTech, 0), blow | (hits > 0 ? 0x100 : 0));
    }

    /// <summary>The characters in blow <paramref name="blow"/>'s attack zone, in the list's order (<see cref="Strike"/>).</summary>
    ZoneScan Scan(World world, int i, in CombatSlot s, CharacterCold cold, CombatTechnique tech, int blow)
    {
        var zone = ZoneOf(world, i, s, cold, tech, blow);
        var scan = new ZoneScan
        {
            Table = world.Characters, Read = read, Relations = Relations, Self = i, Target = s.TargetSlot, Faction = cold.Faction, Squad = cold.SquadId,
            From = zone.From, Centre = zone.Centre, Forward = zone.Forward, Y = zone.Y, Half = zone.Half, High = zone.High, Deep = zone.Deep,
            HitsSlaves = Relations is { } rel && rel.Contains(cold.Faction) && cold.Faction != rel.PlayerFaction && !rel.Factions[cold.Faction].AntiSlavery,
        };
        world.Grid.Query(scan.Centre.X, scan.Centre.Y, zone.Deep + scan.Half + RangedTuning.MaxBodyRadius, ref scan, static (int c, ref ZoneScan z) =>
        {
            if (c == z.Self || z.Count == ZoneCapacity) return;
            var state = z.Table.Previous;
            if (!state[c].Alive || z.Read[c].Down || z.Table.Cold(c) is not { Medical.Incapacitated: false } tc) return;
            if (c != z.Target)
            {
                if (tc.Faction == z.Faction || z.Squad >= 0 && tc.SquadId == z.Squad) return;
                // A slave (slave state 1) that is not its ally is in it for an attacker neither of the player's faction nor anti-slavery.
                if (z.Relations is { } relations && !AggroSystem.IsEnemy(relations, z.Faction, tc.Faction)
                    && !(z.HitsSlaves && tc.SlaveState == 1 && !relations.IsAlly(z.Faction, tc.Faction))) return;
            }
            var q = state[c].Position;
            if (MathF.Abs(q.Y - z.Y) > z.High) return;
            var d = new Vector2(q.X, q.Z) - z.Centre;
            float along = MathF.Abs(Vector2.Dot(d, z.Forward)), across = MathF.Abs(d.X * z.Forward.Y - d.Y * z.Forward.X);
            float ex = MathF.Max(0, across - z.Half), ez = MathF.Max(0, along - z.Deep);
            float radius = CombatTuning.Footprint(tc) * tc.Medical.ScaleK;
            if (ex * ex + ez * ez > radius * radius) return;
            // Kept sorted: the target first, then the nearest, then the lower slot.
            float key = c == z.Target ? -1 : Vector2.DistanceSquared(new Vector2(q.X, q.Z), z.From);
            int at = z.Count++;
            while (at > 0 && (z.Keys[at - 1] > key || z.Keys[at - 1] == key && z.Slots[at - 1] > c))
            {
                z.Keys[at] = z.Keys[at - 1];
                z.Slots[at] = z.Slots[at - 1];
                at--;
            }
            z.Keys[at] = key;
            z.Slots[at] = c;
        });
        return scan;
    }

    /// <summary>A blow's attack zone on the ground: the box's centre and heading (X/Z), half width, half depth, the attacker's feet height and the half height test.</summary>
    readonly record struct Zone(Vector2 From, Vector2 Centre, Vector2 Forward, float Half, float Deep, float Y, float High);

    /// <summary>The attack zone of blow <paramref name="blow"/> (<see cref="Strike"/>), from the last tick's positions, so the act and the apply see the same box.</summary>
    static Zone ZoneOf(World world, int i, in CombatSlot s, CharacterCold cold, CombatTechnique tech, int blow)
    {
        var prev = world.Characters.Previous;
        var p = prev[i].Position;
        var from = new Vector2(p.X, p.Z);
        float k = cold.Medical!.ScaleK, scale = WeaponStats.Lerp(k, 0.45f, 1), r = CombatTuning.Footprint(cold), length = cold.Fighter!.WeaponLength;
        var forward = new Vector2(MathF.Sin(prev[i].Yaw), MathF.Cos(prev[i].Yaw));
        float moved = Vector3.Distance(p, s.AttackFrom);
        float ahead = (length * 0.25f + r + MathF.Max(-1, tech.AttackDistance - moved)) * scale;
        bool sweep = tech.DirectionOf(blow) is 2 or 3;   // CUT_LEFT, CUT_RIGHT
        float deep = length * 0.5f * scale;
        float back = MathF.Max(0, (ahead - k * r) * 0.5f);
        ahead -= back;
        deep += back;
        return new Zone(from, from + forward * ahead, forward, (sweep ? 13 : r + 0.5f) * scale, deep, p.Y, (sweep ? 8 : 25) * scale + 9);
    }

    /// <summary>The <see cref="Effect.Kind"/> of the walls and doors a blow's zone may still hit: the target is the attacker, <c>Arg</c> the blow (+0x100 when it hit a character),
    /// <c>Amount</c> the attack number, <c>Position.X</c> the hits left, <c>Position.Y</c> the technique.</summary>
    public const int ZoneBuildingsEffect = 4;

    /// <summary>The construction system, for which sites are walls (<see cref="Construction.ConstructionSystem.Data"/>) and their footprints; null: blows hit no wall.</summary>
    public Construction.ConstructionSystem? Construction { get; set; }
    /// <summary>The lock system, whose door damage a blow on a door takes (<see cref="Locks.LockSystem.Strike"/>); null: blows hit no door.</summary>
    public Locks.LockSystem? Locks { get; set; }

    /// <summary>
    /// The walls and doors in a blow's zone (<c>attackImpactCheck</c>: <c>WallBuilding</c> and <c>DoorStuff</c> are the buildings whose <c>hitByMeleeAttack</c> does anything,
    /// docs/game/combat.md "Attack zone"), up to the hits the characters left, nearest first: the attacker's damage packet (times the blow's power) gives f = 2 blunt + 0.5 cut;
    /// a door takes clamp(f, 20, 60) (<see cref="Locks.LockSystem.Strike"/>), a wall clamp(f / 2, 10, 60) x 0.003 off its progress (its wall line's for an attached piece),
    /// destroyed at 0. A broken door takes nothing. Engine readings: a wall counts when its footprint hull is within the box's half width of its axis's three points (near
    /// end, centre, far end), a door when its leaf's centre is in the box widened by the leaf's half width; the walls and doors come after the characters.
    /// </summary>
    void ApplyZoneBuildings(World world, in Effect e)
    {
        int a = e.Target, blow = e.Arg & 0xff, left = (int)e.Position.X;
        ref var A = ref write[a];
        if (A.CancelTick == (int)world.Tick || world.Characters.Cold(a) is not { Fighter: { } fighter, Stats: { } stats, Medical: { } medical } cold) return;
        var tech = techniques[(int)e.Position.Y];
        var zone = ZoneOf(world, a, A, cold, tech, blow);
        float Eff(StatsEnumerated stat) => stats[stat] * stats.Scale * medical.StatMultiplier(stat);
        float str = Eff(StatsEnumerated.Strength);
        var packet = Powered(fighter.Unarmed
            ? DamageFormulas.UnarmedPacket(UnarmedRating(world, a, cold, fighter), Eff(StatsEnumerated.Toughness), str, fighter.FistsItem, TargetKind.None, null, fighter.DamageOutput, combat)
            : DamageFormulas.Packet(Eff(fighter.WeaponSkill), Eff(StatsEnumerated.Dexterity) * fighter.DexterityMult, str, fighter.Weapon!, TargetKind.None, null, fighter.DamageOutput, combat), tech, blow);
        float f = 2 * packet.Blunt + 0.5f * packet.Cut;
        var found = buildingScratch;
        found.Clear();
        if (Locks is not null)
            foreach (var pick in world.Fixtures.DoorPicks)
            {
                var d = new Vector2(pick.Position.X, pick.Position.Z) - zone.Centre;
                if (d.LengthSquared() > (zone.Deep + zone.Half + 30) * (zone.Deep + zone.Half + 30) || MathF.Abs(pick.Position.Y - zone.Y) > zone.High + 20) continue;
                if (world.Fixtures.Find(pick.Id) is not { DoorBroken: false } door || world.Fixtures.DoorSpotOf(door) is not { } spot) continue;
                float along = MathF.Abs(Vector2.Dot(d, zone.Forward)), across = MathF.Abs(d.X * zone.Forward.Y - d.Y * zone.Forward.X);
                if (MathF.Max(0, across - zone.Half) > spot.HalfWidth || MathF.Max(0, along - zone.Deep) > spot.HalfWidth) continue;
                found.Add((d.LengthSquared(), pick.Id, null));
            }
        if (Construction is { } construction)
            foreach (var site in world.Buildings.All)
            {
                if (site.Destroyed || site.Progress <= 0 || construction.Data(site.Record) is not { LinksTogether: true }) continue;
                float best = float.MaxValue;
                for (int n = -1; n <= 1; n++)
                {
                    var at = zone.Centre + zone.Forward * (n * zone.Deep);
                    float gap = construction.Settings.EdgeGap?.Invoke(site, at) ?? Vector2.Distance(at, new Vector2(site.Position.X, site.Position.Z));
                    best = MathF.Min(best, gap);
                }
                if (best > zone.Half) continue;
                found.Add((Vector2.DistanceSquared(new Vector2(site.Position.X, site.Position.Z), zone.From), null, site));
            }
        if (found.Count == 0) return;
        found.Sort(static (x, y) => x.Key.CompareTo(y.Key));
        for (int n = 0; n < found.Count && n < left; n++)
        {
            var (_, doorId, wall) = found[n];
            if (doorId is not null && world.Fixtures.Find(doorId) is { } door) global::Meitou.Simulation.Locks.LockSystem.Strike(world, door, Math.Clamp(f, 20, 60));
            else if (wall is not null)
            {
                StrikeWall(world, wall, f, a);
            }
        }
        if ((e.Arg & 0x100) == 0 && A.AttackSeq == (int)e.Amount && A.AttackTech >= 0) A.LastOutcome = BlowOutcome.Hit;
    }

    readonly List<(float Key, string? Door, Construction.BuildSite? Wall)> buildingScratch = [];

    /// <summary>
    /// A blow on a wall building (<c>WallBuilding</c> damage, docs/game/buildings-production.md): <paramref name="f"/> = 2 blunt + 0.5 cut of the attacker's packet; clamp(f / 2, 10, 60) x 0.003 off the
    /// progress of the wall line (an attached piece's parent), destroyed at 0 with the wall-broken sound at <paramref name="attacker"/>. Shared by the fight's blows and the bashers' swings (Smashing, 272).
    /// </summary>
    internal static bool StrikeWall(World world, Construction.BuildSite wall, float f, int attacker)
    {
        var line = wall.WallParent is { } parent && world.Buildings.Find(parent) is { } p ? p : wall;
        line.Progress -= Math.Clamp(f * 0.5f, 10, 60) * 0.003f;
        if (line.Progress <= 0)
        {
            line.Progress = 0;
            line.Destroyed = true;
            world.EmitSound(SoundKind.WallBroken, attacker, line.Position);
        }
        world.Buildings.Changed();
        return line.Destroyed;
    }
}

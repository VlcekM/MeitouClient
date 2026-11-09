using System.Numerics;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Population;

/// <summary>
/// Town squads kept as their characters (docs/simulation.md "Population"): when a town unloads, each squad it made becomes a pinned <see cref="Platoon"/> holding
/// a <see cref="StoredSquad"/>, and when the town loads again the platoon is made of those same characters (name, look, body, kit, bounties, place), never
/// rolled anew.
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>
    /// Takes the squad's characters out of the world into <paramref name="stored"/> (serial phase): each living member's cold state, cleared of what referred to
    /// the world, with its place; the dead are kept too, apart (<see cref="StoredSquad.Dead"/>), and their bodies leave the world with the others. Returns where
    /// the leader (else the first member) stood.
    /// </summary>
    static Vector2 Store(World world, Squad squad, StoredSquad stored)
    {
        var table = world.Characters;
        var at = new Vector2(squad.Position.X, squad.Position.Z);
        bool placed = false;
        stored.Members.Clear();
        stored.Dead.Clear();
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold) continue;

            var hot = table.Previous[slot];
            bool dead = cold.DiedTick >= 0 || cold.Medical is { Dead: true };
            if (!dead && hot.Alive)
            {
                Detach(cold);
                bool leads = id == squad.Leader;
                stored.Members.Add(new StoredMember(cold, hot.Position, hot.Yaw, leads));
                if (leads || !placed) at = new Vector2(hot.Position.X, hot.Position.Z);
                placed |= leads;
            }
            else if (hot.Alive)
            {
                // FUN_1404ff410 writes the whole list, the dead with it; FUN_1404feed0 then destroys every member.
                Detach(cold);
                stored.Dead.Add(new StoredMember(cold, hot.Position, hot.Yaw, false));
            }

            world.Uniques.Left(id);
            RemoveMember(world, id);
        }

        stored.Anchor = at;
        stored.StoredTick = world.Tick;
        stored.StoredHours = world.GameHours;
        return at;
    }

    /// <summary>
    /// A squad's member leaves the world with its squad, the dead too: the unload destroys every member of the active platoon with no dead test
    /// (<c>FUN_1404feed0</c>, docs/game/character-stats.md "Corpses"). A squad that stays keeps its corpses to the 12 hour rule (<see cref="BodySystem"/>).
    /// </summary>
    static void RemoveMember(World world, CharacterId id) => world.Characters.Remove(id);

    /// <summary>
    /// Clears what a character's cold state holds only while it is in the world: paths, AI, carrying, treatment, combat and stealth state, a pending crime, jobs.
    /// </summary>
    static void Detach(CharacterCold c)
    {
        c.Path = [];
        c.PathExact = int.MaxValue;
        c.PathRequest = 0;
        c.PathApplied = 0;
        c.OrderQueue.Clear();
        c.GoalHeight = float.NaN;
        c.Animation = null;
        c.DrawnWeapon = default;
        c.ModeOverride = null;
        c.SpeedOverride = 0;
        c.PaceCap = null;
        c.Ordered = false;
        c.InCombat = false;
        c.Fleeing = false;
        c.Engaged = false;
        c.Watchers = null;
        c.StealthExposure = default;
        c.StealthExposureTick = long.MinValue;
        c.TreatTarget = CharacterId.None;
        c.TreatSplint = false;
        c.TreatKit = null;
        c.Jobs.Clear();
        c.Running = null;
        c.Carrying = CharacterId.None;
        c.CarriedBy = CharacterId.None;
        c.CarryTarget = CharacterId.None;
        c.LiftTime = -1;
        c.Pending = null;
        c.Ai = null;
        c.PickingLock = null;
        c.BuildSite = null;
        c.BuildBar = 0;
        c.FetchSite = null;
        c.FetchItem = null;
        c.FetchRecord = null;
        c.SquadId = -1;
    }

    /// <summary>
    /// The stored characters into the world as the squad of <paramref name="p"/> (serial phase), each where it stood, the leader wandering round the town and the
    /// rest following it (a roaming one waits for its orders), moved along with a stand-in that travelled; the stored list is emptied (the characters are the
    /// world's again). Null when no one is left. One with no town (a prisoner split off a save's wild platoon keeps its <see cref="Platoon.Origin"/> -1) is
    /// homed round where it stands, as a homeless wild one is.
    /// </summary>
    Squad? SpawnStored(World world, Platoon p, StoredSquad stored)
    {
        if (stored.Members.Count == 0) return null;

        if (p.Origin < 0 || p.Origin >= _data.Sites.Count)
            return SpawnKept(world, stored, p.TemplateId, p.Faction, -1, p.Id, p.Position, p.Position, _settings.HomelessHomeRadius, p.Size);

        var site = _data.Sites[p.Origin];
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult;
        bool resume = ResidentSchedule && stored.Pinned && world.GameHours - stored.StoredHours >= ScheduleAfterHours;
        var squad = SpawnKept(
            world, stored, p.TemplateId, p.Faction, p.Origin, p.Id, p.Position, new Vector2(site.Position.X, site.Position.Z), Math.Max(radius * 0.8f, 40), p.Size);
        if (squad is not null) squad.Resume = resume;
        return squad;
    }

    /// <summary>
    /// A stored dead member back in the world as a corpse of <paramref name="squad"/> where it lay (moved along with the squad's stand-in, as the living are),
    /// still counted on the squad's list.
    /// </summary>
    void SpawnCorpse(World world, Squad squad, StoredMember m, Vector2 delta, bool moved, Vector2 position)
    {
        var walk = world.Walkability;
        var at = m.Position;
        if (moved)
        {
            float x = at.X + delta.X, z = at.Z + delta.Y;
            if (!walk.IsWalkable(x, z))
                (x, z) = WalkableSpots.Nearest(walk, new Vector2(x, z), out var near, 32) ? (near.X, near.Y) : (position.X, position.Y);

            at = new Vector3(x, walk.GroundHeight(x, z), z);
        }

        m.Cold.SquadId = squad.Id;
        var hot = new CharacterHot
        {
            Position = at,
            Yaw = m.Yaw,
            Health = 100,
            Task = (byte)CharacterTask.Idle,
            Mode = SpeedMode.Walk,
            MaxSpeed = CharacterAssembly.StandInMaxSpeed,
            WalkSpeed = m.Cold.Race?.WalkSpeed ?? 15,
        };
        squad.Members.Add(world.Characters.Spawn(hot, m.Cold, world.Tick));
    }

    /// <summary>
    /// Stored characters into the world as a squad of <paramref name="town"/> (-1: a wild one) homed at the given circle, moved along when
    /// <paramref name="position"/> is not where they were stored.
    /// </summary>
    Squad? SpawnKept(
        World world, StoredSquad stored, string templateId, int faction, int town, int platoonId, Vector2 position, Vector2 homeCentre, float homeRadius, int size)
    {
        if (stored.Members.Count == 0) return null;

        var table = world.Characters;
        var squad = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = templateId,
            Name = stored.Name,
            Faction = faction,
            Town = town,
            PlatoonId = platoonId,
            Pinned = stored.Pinned && town >= 0,
            Restored = true,
            Position = new Vector3(position.X, world.Walkability.GroundHeight(position.X, position.Y), position.Y),
            HomeCentre = homeCentre,
            HomeRadius = homeRadius,
            HomeBuilding = stored.HomeBuilding,
            Shop = stored.Shop,
            SavedAi = stored.Ai,
            LeftPackage = stored.Package,
            NextHomeFillTick = stored.NextHomeFillTick,
        };

        // A stand-in that travelled since takes its members along, each on walkable ground (else at the stand-in).
        var delta = position - stored.Anchor;
        bool moved = delta.LengthSquared() > 1;
        bool hasLeader = stored.Members.Any(m => m.Leader);

        // Without one the first member that is not an animal leads (SquadRegistry.Successor).
        var successor = hasLeader ? null : stored.Members.FirstOrDefault(m => !SquadRegistry.IsAnimal(m.Cold)) ?? stored.Members.FirstOrDefault();
        foreach (var m in stored.Members)
            SpawnKeptMember(world, squad, stored, m, successor, delta, moved, position);

        squad.SpawnSize = Math.Max(size, squad.Members.Count);
        foreach (var m in stored.Dead) SpawnCorpse(world, squad, m, delta, moved, position);

        stored.Members.Clear();
        stored.Dead.Clear();
        if (squad.Leader.IsNone) squad.Leader = SquadRegistry.Successor(squad.Members, table);
        world.Squads.Add(squad);
        Memory?.Restore(squad.Id, stored.Memory);
        stored.Memory = null;

        // A resident in its bed takes it again.
        world.Fixtures.Bind(world);
        return squad;
    }

    /// <summary>
    /// One stored member back in the world as a member of <paramref name="squad"/>: the leader (or the successor) wanders when its squad is pinned, the rest
    /// follow it.
    /// </summary>
    void SpawnKeptMember(
        World world, Squad squad, StoredSquad stored, StoredMember m, StoredMember? successor, Vector2 delta, bool moved, Vector2 position)
    {
        var walk = world.Walkability;
        var cold = m.Cold;
        bool leads = m.Leader || ReferenceEquals(m, successor);
        cold.SquadId = squad.Id;
        var at = m.Position;
        if (moved)
        {
            float x = at.X + delta.X, z = at.Z + delta.Y;
            if (!walk.IsWalkable(x, z))
                (x, z) = WalkableSpots.Nearest(walk, new Vector2(x, z), out var near, 32) ? (near.X, near.Y) : (position.X, position.Y);

            at = new Vector3(x, walk.GroundHeight(x, z), z);
        }

        var hot = new CharacterHot
        {
            Position = at,
            Yaw = m.Yaw,
            Health = 100,
            Task = (byte)(leads ? (stored.Pinned ? CharacterTask.Wander : CharacterTask.Idle) : CharacterTask.Follow),
            Mode = SpeedMode.Walk,
            MaxSpeed = cold.Race is { } race && cold.Stats is { } stats && cold.Medical is { } medical
                ? Speed.Run(race, stats, medical, 1)
                : CharacterAssembly.StandInMaxSpeed,
            WalkSpeed = cold.Race?.WalkSpeed ?? 15,
            TaskTime = 0.5f + Rng.Float(Rng.Hash(world.Seed, (ulong)squad.Id << 8 | (uint)squad.Members.Count, RngPurpose.Think)) * 3,
        };
        var id = world.Characters.Spawn(hot, cold, world.Tick);
        if (UniqueCharacters.IsUnique(_data.Db.Find(cold.RecordId))) world.Uniques.Bind(cold.RecordId, id);

        squad.Members.Add(id);
        if (leads && squad.Leader.IsNone) squad.Leader = id;
    }
}

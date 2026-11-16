using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Production;

/// <summary>
/// A corpse disposal an NPC may take a body to (BUILDING_FUNCTIONALITY <c>function</c> 11): a player's machine, a placed town building, or a piece of
/// a town building's interior. <paramref name="Building"/> is the building it is or stands in (the home and public tests), <paramref name="Site"/> its
/// town's site (-1: a player's).
/// </summary>
public sealed record CorpseMachine(string Id, string RecordId, Vector3 Position, int Owner, string Building, bool Inside, int Site);

/// <summary>
/// Corpse disposal (docs/game/ai-tasks.md "Perma-jobs", 137 / 138) for the NPCs' duty and the player's job alike: the disposal finder
/// <c>FUN_1408d0770</c>(1, 0xb), the corpse finder of 138 <c>FUN_1408cfb00</c>, and the feed of <c>Task_FeedCorpseIntoMachine</c> (136).
/// </summary>
public static class CorpseDisposal
{
    #region Machines

    /// <summary>
    /// The disposal weight's distance scale squared (<c>DAT_14172e248</c>, <b>Verified</b>: 4608^2).
    /// </summary>
    const float WeightScaleSquared = 21233664f;

    /// <summary>
    /// The weight factor of a machine of the carrier's own faction, and of one in its squad's home building (<b>Verified</b>, <c>FUN_1408d0770</c>).
    /// </summary>
    const float OwnWeight = 100;

    /// <summary>
    /// Every corpse disposal there is now: the complete player machines of function 11, and the placed buildings of function 11 and interior
    /// disposals (<see cref="TownBuilding.Disposals"/>) of the registered towns' standing buildings (none without population data).
    /// </summary>
    public static IEnumerable<CorpseMachine> Machines(World world, PopulationData? data)
    {
        foreach (var m in world.Machines.All)
        {
            if (m.Data.Function != TownBuildings.CorpseDisposalFunction || world.Buildings.Find(m.Site) is not { Complete: true } site) continue;

            yield return new CorpseMachine(site.Id, site.Record, site.Position, site.Owner, site.Parent ?? site.Id, site.Parent is not null, -1);
        }

        if (data is null) yield break;

        foreach (var town in data.Sites)
        {
            if (!world.Fixtures.IsRegistered(town.Index)) continue;

            int faction = town.Index < data.SiteFactions.Count ? data.SiteFactions[town.Index] : -1;
            foreach (var b in town.Buildings)
                foreach (var machine in OfBuilding(world, data, b, town.Index, faction))
                    yield return machine;
        }
    }

    /// <summary>
    /// A standing town building's disposals: itself when it is one, and its interior's; owned by its state's faction, else its town's.
    /// </summary>
    static IEnumerable<CorpseMachine> OfBuilding(World world, PopulationData data, TownBuilding b, int site, int townFaction)
    {
        if (b.Function != TownBuildings.CorpseDisposalFunction && b.Disposals.Count == 0 || world.Fixtures.IsDestroyed(b)) yield break;

        int owner = b.OwnerFactionId is { } id && data.FactionIndex(id) is >= 0 and var known ? known : townFaction;
        if (b.Function == TownBuildings.CorpseDisposalFunction)
        {
            bool nested = b.LevelState?.GetBool("is inside building") ?? false;
            yield return new CorpseMachine(b.Key, b.BuildingId, b.Position, owner, b.Key, nested, site);
        }

        foreach (var d in b.Disposals) yield return new CorpseMachine(d.Id, d.RecordId, d.Position, owner, b.Key, true, site);
    }

    /// <summary>
    /// The disposal of <paramref name="machines"/> (<see cref="Machines"/>) the carrier in <paramref name="slot"/> at <paramref name="at"/> takes a
    /// body to (<c>FUN_1408d0770</c>(1, 0xb), <b>Verified</b>): among the machines the building gatherer offers
    /// (<see cref="FixtureRegistry.TownCandidates"/>: its town's within 1.1 x the radius, else its faction's nearest town's, plus its squad's home
    /// town's) that <paramref name="skip"/> leaves (claimed, path failed), those of its faction or that pass <c>canIUseThisBuilding</c>, the greatest
    /// weight: clamp(1 - d^2 / 4608^2, 1e-5, 1) of the 3D distance, x100 for its faction's, x100 in its squad's home, x1e-5 for a law enforcer when
    /// the machine stands inside a building that is not public. The first listed wins a tie. Null for none. Without population data every machine
    /// is in reach.
    /// </summary>
    public static CorpseMachine? Find(World world, PopulationData? data, IReadOnlyList<CorpseMachine> machines, int slot, Vector3 at,
        Func<CorpseMachine, bool> skip)
    {
        if (machines.Count == 0) return null;

        var carrier = world.Characters.Cold(slot)!;
        var inReach = data is null ? static _ => true : world.Fixtures.TownCandidates(world, data, carrier, at);
        var home = FixtureRegistry.HomeOf(world, carrier);
        bool? police = null;
        CorpseMachine? best = null;
        float bestWeight = 0;
        foreach (var m in machines)
        {
            if (!inReach(m.Position) || skip(m) || !Usable(world, carrier, m)) continue;

            float w = Weight(world, carrier, at, m, home, ref police);
            if (w > bestWeight) (best, bestWeight) = (m, w);
        }

        return best;
    }

    /// <summary>
    /// Whether the carrier may use the machine: its faction's, else <c>canIUseThisBuilding</c> (<see cref="FixtureRegistry.CanUse(World,
    /// CharacterCold, int, FixtureRegistry.UsedBuilding?)"/>).
    /// </summary>
    static bool Usable(World world, CharacterCold carrier, CorpseMachine m)
    {
        if (m.Owner >= 0 && m.Owner == carrier.Faction) return true;

        var town = m.Site >= 0 ? CampaignTown.OfSite(m.Site) : CampaignTown.None;
        return world.Fixtures.CanUse(world, carrier, m.Owner, new FixtureRegistry.UsedBuilding(town, m.Site, m.Building, m.Inside, false));
    }

    /// <summary>
    /// A usable machine's weight for the carrier (<see cref="Find"/>).
    /// </summary>
    static float Weight(World world, CharacterCold carrier, Vector3 at, CorpseMachine m, string? home, ref bool? police)
    {
        float w = Math.Clamp(1 - Vector3.DistanceSquared(at, m.Position) / WeightScaleSquared, 1e-5f, 1);
        if (m.Owner >= 0 && m.Owner == carrier.Faction) w *= OwnWeight;
        if (home is not null && m.Building == home) w *= OwnWeight;
        if (!m.Inside || world.Fixtures.IsPublic(m.Building)) return w;

        police ??= world.SystemOf<CrimeSystem>()?.IsPoliceLike(world, carrier) == true;
        return police.Value ? w * FixtureRegistry.NotPublicPolice : w;
    }

    #endregion

    #region Corpses

    /// <summary>
    /// The corpse goal 138 lifts (<c>FUN_1408cfb00</c>, <b>Verified</b>): a dead, non-robot body in a cage of the carrier's faction that
    /// <paramref name="cageInReach"/> offers (the gatherer's cage mask 0x100), else (<c>FUN_1408ce170</c>) the nearest dead body within
    /// <paramref name="sight"/> (the sensed list's stand-in), robots included. Neither one carried nor one <paramref name="taken"/> leaves out (a squad
    /// mate's goal, a path that failed). None: <see cref="CharacterId.None"/>.
    /// </summary>
    public static CharacterId FindCorpse(World world, int slot, Func<Fixture, bool> cageInReach, float sight, Func<CharacterId, bool> taken)
    {
        var caged = CagedCorpse(world, slot, cageInReach, taken);
        return caged.IsNone ? NearestCorpse(world, slot, sight, taken) : caged;
    }

    /// <summary>
    /// The nearest dead, non-robot occupant of a cage of the carrier's faction in reach (<see cref="FindCorpse"/>'s first stage).
    /// </summary>
    static CharacterId CagedCorpse(World world, int slot, Func<Fixture, bool> cageInReach, Func<CharacterId, bool> taken)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var at = table.Next[slot].Position;
        var best = CharacterId.None;
        float bestD = float.MaxValue;
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || f.Owner != cold.Faction || f.Occupant.IsNone || !table.TryResolveNext(f.Occupant, out int s)) continue;
            if (table.Cold(s) is not { Medical.Dead: true, Race.IsRobot: false } body || taken(f.Occupant) || !cageInReach(f)) continue;
            if (!CarrySystem.CanBeLifted(world, body)) continue;

            float d = Vector3.DistanceSquared(at, f.Position);
            if (d < bestD) (best, bestD) = (f.Occupant, d);
        }

        return best;
    }

    /// <summary>
    /// The nearest dead body within <paramref name="sight"/> that is not carried, caged or taken (<see cref="FindCorpse"/>'s second stage).
    /// </summary>
    static CharacterId NearestCorpse(World world, int slot, float sight, Func<CharacterId, bool> taken)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        var best = CharacterId.None;
        float bestD = sight * sight;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !table.Next[s].Alive || table.Cold(s) is not { Medical.Dead: true } body || !CarrySystem.CanBeLifted(body)) continue;

            var id = table.IdOf(s);
            float d = Vector2.DistanceSquared(new(at.X, at.Z), new(table.Next[s].Position.X, table.Next[s].Position.Z));
            if (d <= bestD && !taken(id)) (best, bestD) = (id, d);
        }

        return best;
    }

    #endregion

    #region Feeding

    /// <summary>
    /// <c>Task_FeedCorpseIntoMachine</c> (136, slot 3 <c>FUN_1403567a0</c>, <b>Verified</b>): the carrier lets go of the body and it is removed from
    /// the world ("incinerated or cooked"), its items with it, with the sound and the machine's functionality <c>string</c> floating up from 5 units
    /// over it (<paramref name="label"/>; none when empty). False when the carrier held nothing.
    /// </summary>
    public static bool Feed(World world, CarrySystem carry, int carrier, Vector3 machine, string label)
    {
        var table = world.Characters;
        var body = table.Cold(carrier)!.Carrying;
        if (!carry.LetGo(world, carrier) || !table.IsAlive(body)) return false;

        if (label.Length > 0) world.Floaters?.Enqueue(Floater.Label(machine + new Vector3(0, 5, 0), label));
        if (table.TryResolveNext(body, out int b)) world.EmitSound(SoundKind.FedToMachine, b, table.Next[b].Position, armour: world.BodyArmourMaterial(b));

        world.Uniques.Left(body);
        table.Remove(body);
        return true;
    }

    #endregion
}

/// <summary>
/// The corpse disposals (<see cref="CorpseDisposal.Machines"/>) gathered at most once per <paramref name="everyTicks"/> ticks: the finders ask for
/// them on every look, and gathering walks every registered town's buildings. Derived state, not saved or hashed.
/// </summary>
public sealed class CorpseMachineCache(int everyTicks)
{
    /// <summary>
    /// The disposals as of <see cref="_tick"/>.
    /// </summary>
    readonly List<CorpseMachine> _machines = [];

    /// <summary>
    /// The tick <see cref="_machines"/> was gathered (-1: never).
    /// </summary>
    long _tick = -1;

    /// <summary>
    /// The disposals there are, gathered again when <paramref name="everyTicks"/> ticks have passed.
    /// </summary>
    public IReadOnlyList<CorpseMachine> Get(World world, PopulationData? data)
    {
        if (_tick >= 0 && world.Tick - _tick < everyTicks && world.Tick >= _tick) return _machines;

        _machines.Clear();
        _machines.AddRange(CorpseDisposal.Machines(world, data));
        _tick = world.Tick;
        return _machines;
    }
}

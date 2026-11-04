using Meitou.Simulation.Items;
using Meitou.Simulation.Bodies;
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;

namespace Meitou.Simulation;
public sealed partial class PopulationSystem
{
    // ---- building (pure given the seed; safe on another thread) ----

    internal sealed record BuiltMember(MemberPlan Plan, CharacterAppearance? Appearance);
    /// <param name="NearY">The height of the spot inside a building it stands on (the floor under it is used); null: on the ground.</param>
    /// <param name="Home">The building its town gave it as its home; null: none.</param>
    internal sealed record BuiltSquad(SquadPlan Plan, Vector2 Position, int Faction, List<BuiltMember> Members, float? NearY = null, TownBuilding? Home = null);
    /// <param name="Homes">The town's buildings with the template housed in each (those whose template makes no squad too), for the artifacts.</param>
    sealed record BuiltTown(int Site, long LoadId, List<BuiltSquad> Squads, List<(TownBuilding Building, SquadTemplate Template)> Homes);

    /// <summary>
    /// The town's residents and bar squads. <paramref name="held"/>: a loaded save holds squads of the town (its pinned saved platoons), which are then its whole
    /// population and nothing is made (docs/formats/save.md "NPC platoons"). <paramref name="playerOwned"/>: the keys of the town's buildings the player owns, which house nobody.
    /// </summary>
    BuiltTown BuildTown(TownSite site, long loadId, ulong seed, IWalkability walk, bool held = false, IReadOnlySet<string>? playerOwned = null)
    {
        if (held) return new BuiltTown(site.Index, loadId, [], []);
        var town = site.Town;
        int factionIndex = town.Faction is { } fid ? data.FactionIndex(fid) : -1;
        var faction = factionIndex >= 0 ? data.Factions[factionIndex] : null;
        var squads = new List<BuiltSquad>();
        var homes = new List<(TownBuilding, SquadTemplate)>();
        var entries = ResidentHomes.Entries(town, faction);
        if (site.Buildings.Any(b => b.CanHouse))
            HouseResidents(site, faction, factionIndex, seed, walk, squads, homes, playerOwned);
        else
        {
            // No building data (headless worlds): every entry's squads at spots round the centre (engine choice).
            for (int e = 0; e < entries.Count; e++)
            {
                if (data.Db.Find(entries[e].Id) is not { } record || !SquadFactory.IsTemplate(record)) continue;
                var template = SquadTemplate.From(record);
                for (int k = 0; k < entries[e].V0; k++)
                {
                    ulong key = Rng.Mix((ulong)site.Index << 40 ^ (ulong)e << 20 ^ (uint)k);
                    if (BuildOne(template, factionIndex, faction, key, seed, Place(site, seed, key, walk)) is { } one) squads.Add(one);
                }
            }
        }
        // Bar squads (Observed, FUN_1409fcd20): per entry floor(v0 x v1 / 200) when v0 > 2, plus one more for each roll of 0..100 that is at most v1, stopping at the
        // first that is not, at most v0 in all. The original puts each at a random node of the town's node map (the town's own position when none comes back), as Meitou.
        var barEntries = BarEntries(town, faction);
        List<Vector3>? guardNodes = null;
        for (int e = 0; e < barEntries.Count; e++)
        {
            if (data.Db.Find(barEntries[e].Id) is not { } record || !SquadFactory.IsTemplate(record)) continue;
            var template = SquadTemplate.From(record);
            int n = BarCount(barEntries[e].V0, barEntries[e].V1, seed, Rng.Mix(SeedSalts.BarSquad ^ (ulong)site.Index << 40 ^ (ulong)e << 20 ^ 0xFFFFF));
            for (int k = 0; k < n; k++)
            {
                ulong key = Rng.Mix(SeedSalts.BarSquad ^ (ulong)site.Index << 40 ^ (ulong)e << 20 ^ (uint)k);
                // A node of the town's node map, the guard nodes outside its doors, whether or not a bar has nodes (FUN_1409fcd20 via FUN_14038b000, FUN_14038b650); none: the town's own position with no random
                // offset, the height the ground there (FUN_1409fcd20:197-205, Town slot 8 FUN_1409272c0).
                if (GuardSpot(guardNodes ??= TownGuardNodes.Of(data.Db, site.Buildings, walk), seed, key) is { } node)
                {
                    if (BuildOne(template, factionIndex, faction, key, seed, new Vector2(node.X, node.Z)) is { } outside) squads.Add(outside with { NearY = node.Y + SpotLift });
                }
                else if (BuildOne(template, factionIndex, faction, key, seed, new Vector2(site.Position.X, site.Position.Z)) is { } one) squads.Add(one);
            }
        }
        return new BuiltTown(site.Index, loadId, squads, homes);
    }

    /// <summary>The original's per-cent constant, the double nearest the float 0.01 (<c>0x141748288</c>), so exact products like 100 x 0.5 x 4 fall just under their integer as there.</summary>
    const double BarShare = 0.009999999776482582;

    /// <summary>The number of squads a bar squad entry (v0 slots, v1 per cent) makes (see <see cref="BuildTown"/>).</summary>
    internal static int BarCount(int v0, int v1, ulong seed, ulong key)
    {
        int n = v0 > 2 ? (int)(v1 * 0.5 * BarShare * v0) : 0;
        for (int roll = 0; n < v0; roll++)
        {
            if (Rng.Int(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.BarChance + (ulong)roll), 101) > v1) break;
            n++;
        }
        return n;
    }

    /// <summary>The height above a node a squad is made at before it drops to the floor under it (the original adds 10 to the node's Y).</summary>
    const float SpotLift = 10;

    /// <summary>A random node of the town's node map (null when it has none): uniform, as <c>FUN_14038b000</c>.</summary>
    static Vector3? GuardSpot(List<Vector3> nodes, ulong seed, ulong key) =>
        nodes.Count == 0 ? null : nodes[Rng.Int(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.HomeSpot), nodes.Count)];

    /// <summary>
    /// The bar squad entries of a town (Observed): without <c>residents override</c> the faction's <c>bar squads</c> (for towns of type Town only), then the town's;
    /// with it the town's only. v0 = 0 entries are dropped. A town with no faction has none (its entry count is filled by the same builder, <c>FUN_140936e60</c>).
    /// </summary>
    public static List<RecordLink> BarEntries(TownData town, FactionData? faction)
    {
        var list = new List<RecordLink>();
        if (faction is null) return list;
        if (!town.ResidentsOverride && town.Type == TownType.Town) list.AddRange(faction.BarSquads);
        list.AddRange(town.BarSquads);
        list.RemoveAll(e => e.V0 <= 0);
        return list;
    }

    /// <summary>One squad: its plan from the factory and a rolled look for every human member. Pure given its arguments (any thread).</summary>
    BuiltSquad? BuildOne(SquadTemplate template, int townFactionIndex, FactionData? townFaction, ulong key, ulong seed, Vector2 position)
    {
        var plan = SquadFactory.Plan(data.Db, template, settings.SquadSizeMultiplier, seed, key, States);
        if (plan.TotalMembers == 0) return null;
        // The squad's own faction (its template's) wins over the town's (section 4).
        int squadFaction = template.Faction is { } tf && data.FactionIndex(tf) is var ti and >= 0 ? ti : townFactionIndex;
        var members = new List<BuiltMember>();
        int n = 0;
        foreach (var m in plan.Members)
        {
            CharacterAppearance? look = null;
            if (data.Appearances is { } source)   // an animal gets its species' shared look (GeneratedAppearances)
                look = source.Create(m.RecordId, townFaction?.Id, (int)(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.MemberLook + (ulong)n) & 0x7FFFFFFF), template.Id);
            members.Add(new BuiltMember(m, look));
            n++;
        }
        return new BuiltSquad(plan, position, squadFaction, members);
    }

    Vector2 Place(TownSite site, ulong seed, ulong key, IWalkability walk)
    {
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult * settings.PlacementRadiusShare;
        var centre = new Vector2(site.Position.X, site.Position.Z);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            ulong roll = Rng.Hash(seed, key, RngPurpose.Spawn, (ulong)attempt);
            var p = Rng.PointInDisc(roll, centre, radius);
            if (walk.IsWalkable(p.X, p.Y)) return p;
        }
        return centre;
    }
}

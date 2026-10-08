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
    /// <summary>The resident entries of a town (6.4): template id and number of squads, v0 = 0 dropped.</summary>
    public static List<RecordLink> ResidentEntries(TownData town, FactionData? faction)
    {
        var list = new List<RecordLink>();
        list.AddRange(town.Residents);
        if (!(town.ResidentsOverride && town.Residents.Count > 0) && faction is not null) list.AddRange(faction.Residents);
        list.RemoveAll(e => e.V0 <= 0);
        return list;
    }

    // ---- building (pure given the seed; safe on another thread) ----

    internal sealed record BuiltMember(MemberPlan Plan, CharacterAppearance? Appearance);
    internal sealed record BuiltSquad(SquadPlan Plan, Vector2 Position, int Faction, List<BuiltMember> Members);
    sealed record BuiltTown(int Site, long LoadId, List<BuiltSquad> Squads);

    BuiltTown BuildTown(TownSite site, long loadId, ulong seed, IWalkability walk)
    {
        var town = site.Town;
        int factionIndex = town.Faction is { } fid ? data.FactionIndex(fid) : -1;
        var faction = factionIndex >= 0 ? data.Factions[factionIndex] : null;
        var squads = new List<BuiltSquad>();
        var entries = ResidentEntries(town, faction);
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
        // Bar squads (6.4): the town's own list, plus the faction's for towns of type Town; v0 slots each, present with the chance v1 per cent (0 counts as 100).
        var bars = BarEntries(town, faction);
        for (int e = 0; e < bars.Count; e++)
        {
            if (data.Db.Find(bars[e].Id) is not { } record || !SquadFactory.IsTemplate(record)) continue;
            var template = SquadTemplate.From(record);
            for (int k = 0; k < bars[e].V0; k++)
            {
                ulong key = Rng.Mix(SeedSalts.BarSquad ^ (ulong)site.Index << 40 ^ (ulong)e << 20 ^ (uint)k);
                int chance = bars[e].V1 <= 0 ? 100 : bars[e].V1;
                if (Rng.Float(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.BarChance)) * 100 >= chance) continue;
                if (BuildOne(template, factionIndex, faction, key, seed, Place(site, seed, key, walk)) is { } one) squads.Add(one);
            }
        }
        return new BuiltTown(site.Index, loadId, squads);
    }

    /// <summary>The bar squad entries of a town (6.4, Observed): its own <c>bar squads</c>, plus the faction's default list for towns of type Town. v0 = 0 entries are dropped.</summary>
    public static List<RecordLink> BarEntries(TownData town, FactionData? faction)
    {
        var list = new List<RecordLink>(town.BarSquads);
        if (town.Type == TownType.Town && faction is not null) list.AddRange(faction.BarSquads);
        list.RemoveAll(e => e.V0 <= 0);
        return list;
    }

    /// <summary>One squad: its plan from the factory and a rolled look for every human member. Pure given its arguments (any thread).</summary>
    BuiltSquad? BuildOne(SquadTemplate template, int townFactionIndex, FactionData? townFaction, ulong key, ulong seed, Vector2 position)
    {
        var plan = SquadFactory.Plan(data.Db, template, settings.SquadSizeMultiplier, seed, key, data.States);
        if (plan.TotalMembers == 0) return null;
        // The squad's own faction (its template's) wins over the town's (section 4).
        int squadFaction = template.Faction is { } tf && data.FactionIndex(tf) is var ti and >= 0 ? ti : townFactionIndex;
        var members = new List<BuiltMember>();
        int n = 0;
        foreach (var m in plan.Members)
        {
            CharacterAppearance? look = null;
            if (!m.IsAnimal && data.Appearances is { } source)
                look = source.Create(m.RecordId, townFaction?.Id, (int)(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.MemberLook + (ulong)n) & 0x7FFFFFFF));
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

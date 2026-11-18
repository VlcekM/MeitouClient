using System.Numerics;
using System.Text.Json;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;

namespace ProvingGrounds.Town;

/// <summary>
/// Task 0.6 step 4, read only, serial (the system's Inputs): what Meitou put in Scratch. Logs JSON lines prefixed <c>PG-CENSUS </c>:
/// <c>site</c> (every TownSite named Scratch or with record 90, CAP 6), <c>building</c> (Scratch's buildings by BuildingId, and the listed ids
/// with key, position and yaw), <c>nearsite</c> (other towns with a building within <see cref="Radius"/> of the registry), <c>fixture</c>
/// (world.Fixtures.All within the radius), <c>spot</c> (bed / chair / throne seats of the buildings within the radius), <c>character</c> (every
/// character slot within the radius) and <c>summary</c>.
/// </summary>
static class Census
{
    internal const float Radius = 2500;
    const string M = "-Proving Grounds.mod";
    static readonly string[] Listed = ["183" + M, "195" + M, "163" + M, "192" + M, "261" + M, "272" + M];
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    static float[] P(Vector3 v) => [MathF.Round(v.X, 1), MathF.Round(v.Y, 1), MathF.Round(v.Z, 1)];
    static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    internal static string Run(World world, PopulationData data, Action<string> log)
    {
        void Line(string kind, object o) => log("PG-CENSUS " + JsonSerializer.Serialize(new { kind, data = o }, Json));
        string FactionName(int f) => (uint)f < (uint)data.Factions.Count ? $"{data.Factions[f].Id} {data.Factions[f].Name}" : $"#{f}";

        var scratch = data.Sites.Where(s => s.Town.Name == "Scratch" || s.Record.Name == "Scratch" || s.Record.Id == PgRecords.ScratchTown).ToList();
        foreach (var s in scratch)
            Line("site", new { s.Index, recordId = s.Record.Id, recordName = s.Record.Name, townId = s.Town.Id, townName = s.Town.Name,
                faction = s.Town.Faction, replacement = s.Replacement?.Id, s.InstanceId, position = P(s.Position), buildings = s.Buildings.Count });
        if (scratch.Count == 0) Line("site", new { none = true, sites = data.Sites.Count });

        // The registry: Scratch's own 195 first, else any town's.
        var all = data.Sites.SelectMany(s => s.Buildings.Select(b => (Site: s, B: b))).ToList();
        var registry = all.Where(x => x.B.BuildingId == PgRecords.TownRegistry).OrderBy(x => scratch.Contains(x.Site) ? 0 : 1).FirstOrDefault();
        Vector3? centre = registry.B?.Position;

        var cache = new Dictionary<string, GameRecord?>();
        int? Function(string id) => data.Db.Find(id) is { } r ? TownBuildings.FunctionOf(data.Db, r, cache)?.GetInt("function") : null;

        foreach (var s in scratch)
        {
            foreach (var g in s.Buildings.GroupBy(b => b.BuildingId).OrderBy(g => g.Key, StringComparer.Ordinal))
                Line("buildingtype", new { site = s.Index, id = g.Key, name = g.First().Name, count = g.Count(), function = Function(g.Key) });
            foreach (var b in s.Buildings.Where(b => Listed.Contains(b.BuildingId)))
                Line("building", new { site = s.Index, id = b.BuildingId, b.Name, b.Key, position = P(b.Position), yaw = MathF.Round(b.Yaw, 4),
                    beds = b.Beds, spots = b.Spots.GroupBy(x => x.Kind.ToString()).ToDictionary(x => x.Key, x => x.Count()),
                    furniture = b.Furniture.Count, fixtures = b.Fixtures.Count, b.OwnerFactionId,
                    distance = centre is { } c ? MathF.Round(Flat(b.Position, c)) : -1 });
        }
        if (centre is not { } at) { Line("summary", new { registry = false }); return "census: no building 195 in any town"; }

        // Other towns with buildings near the registry (UWE's Ogdam, DATA §3).
        foreach (var g in all.Where(x => !scratch.Contains(x.Site) && Flat(x.B.Position, at) <= Radius).GroupBy(x => x.Site.Index))
        {
            var s = g.First().Site;
            Line("nearsite", new { s.Index, townId = s.Town.Id, townName = s.Town.Name, faction = s.Town.Faction, position = P(s.Position),
                near = g.Count(), types = g.GroupBy(x => x.B.BuildingId).ToDictionary(x => x.Key, x => x.Count()) });
        }

        // Seats and beds of every building near the registry; corpse disposal (function 11) among them.
        bool furnace = false;
        foreach (var (site, b) in all.Where(x => Flat(x.B.Position, at) <= Radius))
        {
            if (Function(b.BuildingId) == TownBuildings.CorpseDisposalFunction) { furnace = true; Line("furnace", new { site = site.Index, b.BuildingId, b.Key, position = P(b.Position) }); }
            foreach (var sp in b.Spots.Concat(b.Furniture).Where(x => x.Kind is HomeSpotKind.Bed or HomeSpotKind.Chair or HomeSpotKind.ThroneSeat))
                Line("spot", new { site = site.Index, building = b.BuildingId, key = b.Key, kind = sp.Kind.ToString(), record = sp.Record,
                    position = P(sp.Position), yaw = MathF.Round(sp.Yaw, 4) });
            foreach (var f in b.Fixtures.Where(f => f.Function == TownBuildings.CorpseDisposalFunction))
            { furnace = true; Line("furnace", new { site = site.Index, building = b.BuildingId, fixture = f.RecordId, position = P(f.Position) }); }
        }

        var fixtures = world.Fixtures.All.Where(f => Flat(f.Position, at) <= Radius).ToList();
        foreach (var f in fixtures.Where(f => f.Kind is not Meitou.Simulation.Locks.FixtureKind.Door))
            Line("fixture", new { f.Id, kind = f.Kind.ToString(), f.RecordId, f.Building, f.HostRecordId, f.Site, f.Owner, position = P(f.Position),
                yaw = MathF.Round(f.Yaw, 4) });

        // Characters near the registry.
        var t = world.Characters;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int n = 0;
        for (int i = 0; i < t.Next.Length; i++)
        {
            if (!t.Next[i].Alive || t.Cold(i) is not { } c) continue;
            var pos = t.Next[i].Position;
            if (Flat(pos, at) > Radius) continue;
            n++;
            string state = c.Medical is { Dead: true } ? "dead" : c.Medical is { Unconscious: true } ? "ko" : "alive";
            var id = t.IdOf(i);
            var squad = world.Squads.Find(c.SquadId);
            counts[c.RecordId] = counts.GetValueOrDefault(c.RecordId) + 1;
            Line("character", new { id = $"c:{id.Slot}.{id.Generation}", record = c.RecordId, c.Name, faction = FactionName(c.Faction),
                squad = c.SquadId, squadTemplate = squad?.TemplateId, state, player = c.IsPlayer, animal = c.Animal is not null,
                position = P(pos), distance = MathF.Round(Flat(pos, at)) });
        }

        int town = scratch.FirstOrDefault()?.Town.Faction is { } tf ? data.Relations.IndexOf(tf) : -1, player = data.PlayerFaction;
        float? toPlayer = town >= 0 && player >= 0 ? data.Relations.Get(town, player) : null;
        float? fromPlayer = town >= 0 && player >= 0 ? data.Relations.Get(player, town) : null;
        Line("summary", new { registry = true, registrySite = registry.Site.Index, registryKey = registry.B!.Key, centre = P(at),
            characters = n, byRecord = counts.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
            fixtures = fixtures.Count, furnace, townFaction = town >= 0 ? FactionName(town) : null, relationTownToPlayer = toPlayer,
            relationPlayerToTown = fromPlayer, tick = world.Tick });
        return $"census: {n} characters, {fixtures.Count} fixtures within {Radius} of the registry at {P(at)[0]} {P(at)[2]}";
    }
}

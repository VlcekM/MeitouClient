using Meitou.Data.World;
using Meitou.Simulation.Population;

/// <summary>
/// The <c>owners</c> mode of <c>meitou-tools probe</c>: every town building's saved <c>owner faction ID</c> against the owner the housing rule
/// gives it (<see cref="PopulationData.LevelOwnerOf"/>) and the level data's own string, with each one that differs (#1026: a slave shop the
/// town's guards took for their own). On a save the original wrote, the saved owner is the original's.
/// </summary>
static class OwnerProbe
{
    /// <summary>
    /// The save's key of a building state's owner faction.
    /// </summary>
    const string OwnerKey = "owner faction ID";

    /// <summary>
    /// Prints each building whose saved owner is not the rule's, then the counts.
    /// </summary>
    public static void Run(ProbeWorld probe)
    {
        var data = probe.Data;
        var saved = probe.World.Fixtures.Saved;
        int agree = 0, differ = 0, levelOnly = 0, players = 0, unsaved = 0;
        foreach (var site in data.Sites)
        {
            int townFaction = site.Index < data.SiteFactions.Count ? data.SiteFactions[site.Index] : -1;
            foreach (var b in site.Buildings)
            {
                string? id = saved?.GetValueOrDefault(b.Key)?.Strings.GetValueOrDefault(OwnerKey);
                if (id is not { Length: > 0 })
                {
                    unsaved++;
                    continue;
                }

                int owner = data.FactionIndex(id);
                if (owner >= 0 && owner == data.PlayerFaction)
                {
                    players++;
                    continue;
                }

                int rule = data.LevelOwnerOf(b, townFaction);
                if (owner == rule)
                {
                    agree++;
                    continue;
                }

                // Saved as the level names it, where the rule says otherwise: what a save that copied the level string holds.
                bool level = id == b.OwnerFactionId;
                differ++;
                if (level) levelOnly++;
                Console.WriteLine($"{site.Town.Name} {b.Key} '{b.Name}' saved={Name(data, owner, id)} rule={Name(data, rule, "none")} "
                    + $"level={b.OwnerFactionId ?? "-"} housed={Housed(data, b)}{(level ? " (saved is the level's)" : "")}");
            }
        }

        Console.WriteLine($"agree {agree}, differ {differ} ({levelOnly} saved as the level names them), player's {players}, no saved owner {unsaved}");
    }

    /// <summary>
    /// A faction's name, or <paramref name="fallback"/> for one that is not loaded.
    /// </summary>
    static string Name(PopulationData data, int faction, string fallback) =>
        faction >= 0 && faction < data.Factions.Count ? data.Factions[faction].Name : fallback;

    /// <summary>
    /// The template the housing gave a building, by name; "-" for none.
    /// </summary>
    static string Housed(PopulationData data, TownBuilding b)
    {
        string template = b.Naming?.SquadTemplate ?? b.ResidentsTemplate;
        return template.Length == 0 ? "-" : data.Db.Find(template)?.Name ?? template;
    }
}

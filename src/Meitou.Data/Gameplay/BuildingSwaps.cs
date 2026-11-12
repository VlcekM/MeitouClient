namespace Meitou.Data.Gameplay;

/// <summary>
/// The factions' building swaps (FACTION <c>buildings replacements</c>, BUILDINGS_SWAP records: <c>to replace</c> BUILDINGs and <c>replace with</c>
/// BUILDINGs weighted by v0; docs/game/factions-squads-towns.md "Building swaps"). <c>Faction::getBuildingReplacement</c> (<c>FUN_1407f4770</c>,
/// <b>Verified</b>) walks the faction's sets in order and the FIRST set that holds the building answers: no <c>replace with</c> entry means the
/// building is not made, one entry means that building, several mean a weighted pick (a roll in [0, total weight] against the cumulative weights, the
/// first entry whose cumulative weight is above it). No set holding it leaves the building as it is.
/// </summary>
public sealed class BuildingSwaps
{
    /// <summary>
    /// One BUILDINGS_SWAP: the BUILDINGs it replaces and what replaces them with their weights (entries of weight 0 or less dropped).
    /// </summary>
    sealed record Swap(HashSet<string> Replace, (string Id, int Weight)[] With);

    /// <summary>
    /// Each faction's swap sets in its list order, by FACTION string id.
    /// </summary>
    readonly Dictionary<string, Swap[]> _byFaction;

    /// <summary>
    /// Swaps from the sets read by <see cref="From"/>.
    /// </summary>
    BuildingSwaps(Dictionary<string, Swap[]> byFaction) => this._byFaction = byFaction;

    /// <summary>
    /// Whether any faction has a swap.
    /// </summary>
    public bool Any => _byFaction.Count > 0;

    /// <summary>
    /// Reads the swaps of every faction of the database (a swap record the faction lists but the database lacks is skipped).
    /// </summary>
    public static BuildingSwaps From(GameDatabase db)
    {
        var result = new Dictionary<string, Swap[]>(StringComparer.Ordinal);
        foreach (var faction in db.OfType(Fcs.FcsRecordType.Faction))
        {
            var sets = new List<Swap>();
            foreach (var link in faction.GetReferences("buildings replacements"))
            {
                if (db.Find(link.TargetStringId) is not { } record) continue;

                var replace = new HashSet<string>(record.GetReferences("to replace").Select(r => r.TargetStringId), StringComparer.Ordinal);
                var with = record.GetReferences("replace with")
                    .Where(r => r.Values.Value0 > 0)
                    .Select(r => (r.TargetStringId, r.Values.Value0))
                    .ToArray();
                sets.Add(new Swap(replace, with));
            }

            if (sets.Count > 0) result[faction.StringId] = [.. sets];
        }

        return new BuildingSwaps(result);
    }

    /// <summary>
    /// The answer for a building of <paramref name="factionId"/>: null when no set holds it (it stays), an empty string when its set has no
    /// replacement (it is not made), else the building to make. <paramref name="roll"/>, in [0, 1), is the random draw of a weighted pick (the
    /// original's is unseeded).
    /// </summary>
    public string? Replacement(string factionId, string buildingId, float roll)
    {
        if (!_byFaction.TryGetValue(factionId, out var sets)) return null;

        foreach (var set in sets)
        {
            if (!set.Replace.Contains(buildingId)) continue;

            if (set.With.Length == 0) return "";
            if (set.With.Length == 1) return set.With[0].Id;
            return WeightedPick(set.With, roll);
        }

        return null;
    }

    /// <summary>
    /// The weighted pick of a set with several replacements: the first entry whose cumulative weight is above the roll's share of the total.
    /// </summary>
    static string WeightedPick((string Id, int Weight)[] with, float roll)
    {
        long total = with.Sum(w => (long)w.Weight);
        double at = roll * total;
        long cumulative = 0;
        foreach (var (id, weight) in with)
        {
            cumulative += weight;
            if (cumulative > at) return id;
        }

        return with[^1].Id;
    }
}

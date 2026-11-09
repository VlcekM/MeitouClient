using Meitou.Data;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Crime;

/// <summary>
/// The bounties a new NPC is made with (docs/game/economy.md 11.2 "Spawn bounties", the game's <c>FUN_14062b210</c>, <b>Observed</b>): CHARACTER
/// <c>bounty chance</c> against an inclusive roll 0..100 (a bounty when the roll is below it); the amount <c>trunc(bounty amount + 0.5 k)</c>, k an
/// integer in [0, 2 x <c>bounty amount fuzz</c>], added for every <c>bounty factions</c> entry without a crime bit, also when it is 0 or less
/// (<c>FUN_140853e20</c> books it regardless: a 0 entry is kept, the expiry sweep and "has a bounty" only look at amounts above 0, <b>Verified</b> in
/// <c>FUN_1405cbb80</c>). The rolls are Meitou's seeded stream keyed by the character's id (the original's sequence is not reproduced).
/// </summary>
public static class SpawnBounties
{
    /// <summary>
    /// The faction a record without <c>bounty factions</c> is wanted by: the game looks the literal string id <c>defaultEmpireFactionSID</c> up in its faction list
    /// (<c>FUN_14062b210</c> and <c>FUN_1402e7a20</c>, <b>Verified</b>: in gamedata.base that id belongs to the faction named United Cities).
    /// </summary>
    public const string DefaultEmpire = "defaultEmpireFactionSID";

    /// <summary>
    /// The ledger a character made from <paramref name="record"/> starts with, or null (no CHARACTER record, no chance, a failed roll, or no faction to be wanted by).
    /// </summary>
    public static BountyLedger? Roll(PopulationData data, GameRecord? record, ulong seed, CharacterId id, long tick)
    {
        if (record is not { Type: Data.Fcs.FcsRecordType.Character }) return null;

        int chance = IntOf(record, "bounty chance");
        if (chance <= 0) return null;

        ulong key = Rng.Key(id);
        if (Rng.Int(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.BountyChance), 101) >= chance) return null;

        int amount = IntOf(record, "bounty amount");
        int fuzz = Math.Max(0, IntOf(record, "bounty amount fuzz"));
        int k = fuzz > 0 ? Rng.Int(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.BountyFuzz), 2 * fuzz + 1) : 0;
        int value = (int)(amount + 0.5 * k);
        BountyLedger? ledger = null;
        var refs = record.GetReferences("bounty factions");
        if (refs.Count == 0)
        {
            Add(DefaultFaction(data));
        }
        else
        {
            foreach (var r in refs) Add(data.FactionIndex(r.TargetStringId));
        }

        return ledger;

        // Each listed faction books under its legal system, else itself when it books bounties, else nowhere (FUN_140853e20 keys by FUN_140852010;
        // the original's null key is never read).
        void Add(int listed)
        {
            if (listed < 0 || listed >= data.Factions.Count) return;

            var fd = data.Factions[listed];
            int faction = fd.LegalSystem is { } legal ? data.FactionIndex(legal) : -1;
            if (faction < 0) faction = fd.BooksBounties ? listed : -1;
            if (faction < 0) return;

            var entry = (ledger ??= new BountyLedger()).Get(faction, data.Factions[faction].Name);
            entry.Amount += value;
            entry.RaisedTick = tick;
        }
    }

    static int DefaultFaction(PopulationData data) => data.FactionIndex(DefaultEmpire);

    static int IntOf(GameRecord r, string key) =>
        r.Ints.TryGetValue(key, out int v) ? v : r.Floats.TryGetValue(key, out float f) ? (int)MathF.Round(f) : 0;
}

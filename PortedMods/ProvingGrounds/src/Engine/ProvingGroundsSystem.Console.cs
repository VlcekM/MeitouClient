using System.Globalization;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Engine;

/// <summary>The world-side handlers of the queued <c>pg</c> verbs (dispatched by <see cref="Apply"/>; the console half is PgConsole.cs).
/// Sim thread, serial Inputs. Split out of ProvingGroundsSystem.cs with no change in behaviour.</summary>
sealed partial class ProvingGroundsSystem
{
    /// <summary><c>pg probe &lt;id&gt;</c> (task 2.2): every engine-adapter value for one character, logged as <c>PG-PROBE {json}</c> and kept
    /// for the snapshot (a later <c>pg probe</c> with no id prints it).</summary>
    string Probe(World world, ConsoleLine pg)
    {
        if (pg.Args.Count < 1 || !CharacterIdText.TryParse(pg.Args[0], out var id)) return "usage pg probe c:<slot>.<generation>";
        identity.Refresh(world);
        string? token = Game.FighterIdentity.TokenOf(world, id);
        string identityFields = ",\"identity\":" + (token is null ? "null" : System.Text.Json.JsonSerializer.Serialize(token))
            + ",\"identityCreated\":" + (identity.Find(world, id, out _) ? "true" : "false")
            + ",\"identityBlocked\":" + (identity.IsBlocked(world, id) ? "true" : "false")
            + ArenaFields(world, id);
        lastProbe = PgEngine.ProbeJson(world, data, towns, id, identityFields);
        ProvingGroundsMod.Ctx?.Log("PG-PROBE " + lastProbe);
        return $"probe {CharacterIdText.Format(id)} logged";
    }

    /// <summary>The probe's arena fields (task 2.4; 5.1 adds the player-board match count and the reward tiers, RewardProgression's text):
    /// whether the arena is ready, the character's player-board and town-board ratings (floats, through <see cref="CMath.FormatG(double, int)"/>)
    /// and marks, or null when its identity has no row or the arena is blocked (C++ FindStanding
    /// / g_ledger.Find return nothing while not ready, LeaderboardStore.cpp:55-61, ArenaLedger.h:40-45). Reads with Find (no create).</summary>
    string ArenaFields(World world, CharacterId id)
    {
        string ready = ",\"arenaReady\":" + (store.HasActiveSave() ? "true" : "false");
        if (!store.HasActiveSave() || !identity.Find(world, id, out string token))
            return ready + ",\"rating\":null,\"townRating\":null,\"matches\":null,\"marks\":null,\"rewardTiers\":null";
        LeaderboardData.Standing? Row(List<LeaderboardData.Standing> rows) => rows.FirstOrDefault(r => r.Id == token);
        string Rating(LeaderboardData.Standing? r) => r is null ? "null" : TestJson.G(r.Mmr);
        LeaderboardData.Standing? player = Row(store.Data.PlayerStandings);
        LeaderboardData.Progression? progress = store.FindProgression(token);
        return ready + ",\"rating\":" + Rating(player) + ",\"townRating\":" + Rating(Row(store.Data.TownStandings))
            + ",\"matches\":" + (player is null ? "null" : player.Matches.ToString(CultureInfo.InvariantCulture))
            + ",\"marks\":" + (progress is null ? "null" : progress.Marks.ToString(CultureInfo.InvariantCulture))
            + ",\"rewardTiers\":" + (progress is null ? "null" : PGLog.Quote(RewardProgression.EncodeTiers(progress.RewardTiers)));
    }

    /// <summary>TEST ONLY <c>pg give-rating &lt;id&gt; &lt;n&gt;</c> (task 2.4): the C++ debug menu's rating setter (DebugMenu.cpp:398-430 ->
    /// LeaderboardStore::SetRating, :369-395; src/Game/LeaderboardStore.cs, the debug menu's own setter): refused while the arena is blocked, clamped to 0..9999, a float.
    /// Answers with the debug menu's texts (DebugMenu.cpp:407-409).</summary>
    string GiveRating(World world, ConsoleLine pg)
    {
        if (pg.Args.Count < 2 || !CharacterIdText.TryParse(pg.Args[0], out var id)
            || !float.TryParse(pg.Args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float rating) || !float.IsFinite(rating))
            return "usage pg give-rating c:<slot>.<generation> <rating>";
        return store.SetRating(world, id, rating) ? "Rating updated for the active world." : "Could not save rating (load a game first).";
    }

    /// <summary>TEST ONLY <c>pg give-marks &lt;id&gt; &lt;n&gt;</c> (task 2.4): the C++ debug menu's marks setter (DebugMenu.cpp:432-445 ->
    /// LeaderboardStore::SetMarks, :506-532; src/Game/LeaderboardStore.cs): refused while blocked, clamped to 0..9999, an int. Debug menu texts
    /// (DebugMenu.cpp:439-441).</summary>
    string GiveMarks(World world, ConsoleLine pg)
    {
        if (pg.Args.Count < 2 || !CharacterIdText.TryParse(pg.Args[0], out var id)
            || !int.TryParse(pg.Args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int marks))
            return "usage pg give-marks c:<slot>.<generation> <marks>";
        return store.SetMarks(world, id, marks) ? "Arena Marks updated for the active world." : "Could not save Arena Marks (load a game first).";
    }

    /// <summary>TEST ONLY <c>pg spar &lt;avb|1v1|ffa&gt; &lt;idA...&gt; [vs &lt;idB...&gt;]</c> (task 3.3; task 3.5 keeps it): SparSession::StartMatch
    /// as the original's Arena window calls it after the ingress (ArenaUI.cpp:2388-2430, ArenaIngress.cpp:942). Words -> modes as the window's
    /// three mode buttons: <c>avb</c> = AvB (TeamAvB), <c>1v1</c> = Teams 1v1 (one at a time from a bench), <c>ffa</c> = Last Standing; the team
    /// modes list team A then team B (TeamA / TeamB), Last Standing the pool with TeamNone. Answered as one PG-SPAR JSON line in log.txt.</summary>
    string Spar(World world, ConsoleLine pg)
    {
        if (!SparArgs.TryParse<CharacterId>(pg.Args, CharacterIdText.TryParse, out var mode, out var fighters, out var teams, out string error)) return error;
        return sparSession.StartForTest(world, mode, fighters, teams);
    }

    /// <summary>TEST ONLY: logs one <c>PG-IDENTITY {json}</c> line.</summary>
    static string IdentityLine(string json)
    {
        ProvingGroundsMod.Ctx?.Log("PG-IDENTITY " + json);
        return "identity logged";
    }

    /// <summary>TEST ONLY <c>pg identity create &lt;id&gt;</c>: <see cref="Game.FighterIdentity.GetOrCreate"/> for one character, the way the
    /// leaderboard will call it, so a live run exercises create, bind and block.</summary>
    string IdentityCreate(World world, ConsoleLine pg)
    {
        if (pg.Args.Count < 2 || pg.Args[0] != "create" || !CharacterIdText.TryParse(pg.Args[1], out var id)) return "usage pg identity create c:<slot>.<generation>";
        bool ok = identity.GetOrCreate(world, id, out string token);
        return IdentityLine("{\"verb\":\"identity-create\",\"id\":" + System.Text.Json.JsonSerializer.Serialize(CharacterIdText.Format(id))
            + ",\"ok\":" + (ok ? "true" : "false") + ",\"token\":" + System.Text.Json.JsonSerializer.Serialize(token)
            + ",\"blocked\":" + (identity.IsBlocked(world, id) ? "true" : "false") + "}");
    }

    /// <summary>TEST ONLY <c>pg items &lt;id&gt;</c> (task 5.2a): every item of the character and what its fighter holds, one
    /// <c>PG-ITEMS {json}</c> line (src/Engine/Items.cs ItemsJson).</summary>
    string ItemsList(World world, ConsoleLine pg)
    {
        if (pg.Args.Count < 1 || !CharacterIdText.TryParse(pg.Args[0], out var id)) return "usage pg items c:<slot>.<generation>";
        ProvingGroundsMod.Ctx?.Log("PG-ITEMS " + Items.ItemsJson(world, id));
        return $"items {CharacterIdText.Format(id)} logged";
    }

    /// <summary>TEST ONLY <c>pg reward-item &lt;id&gt; &lt;piece&gt; &lt;tier&gt; [upgrade] [wear]</c> or <c>pg reward-item &lt;id&gt; general
    /// &lt;item&gt;</c> (task 5.2a): one catalogue row (Core ArenaRewards; piece / tier / item = the enum names, any case) made and delivered with
    /// the engine calls RewardDelivery.cpp makes, WITHOUT its rules (no squad, licence, Marks, offer or accounting: task 5.2b): the record of the
    /// piece (the cloak always 89, Core RewardGrades.ItemRecord), CreateRewardItem / CreateBlueprintReward, the supplies' charges, the room check
    /// (:851, :1070), giveItem into the inventory, the fighter refreshed. <c>upgrade</c> = ClaimNext's upgrade path (:833-850, ItemDelivery):
    /// the previous tier's copy found by Core's finders, taken out and kept alive, the new one given, then the old one destroyed (given back on a
    /// failure). <c>wear</c> (port only) wears the delivered item (the C++ never does) so the live check sees the refresh reach Fighter.Weapon.
    /// Answers one <c>PG-REWARD {json}</c> line; status texts are the C++ ones where the C++ has one. The C++ RewardDiag lines go to the debug log.</summary>
    string RewardItemTest(World world, ConsoleLine pg)
    {
        const string usage = "usage pg reward-item c:<slot>.<generation> <piece> <tier> [upgrade] [wear] | c:<slot>.<generation> general <item>";
        if (pg.Args.Count < 3 || !CharacterIdText.TryParse(pg.Args[0], out var id)) return usage;
        var missing = Items.Missing().ToList();
        if (missing.Count > 0) return RewardLine(id, false, $"rewards off: {string.Join(", ", missing)} not found", null, null, false);
        var flags = pg.Args.Skip(3).Select(a => a.ToLowerInvariant()).ToHashSet();
        void Diag(string line) => PGLog.Debug(line);

        if (pg.Args[1].Equals("general", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse(pg.Args[2], true, out ArenaRewards.GeneralItem gi) || gi == ArenaRewards.GeneralItem.Count) return usage;
            var reward = ArenaRewards.GetGeneral(gi);
            // C++: RewardDelivery.cpp:1053-1093 (PurchaseGeneral's item half)
            if (!Items.RecordExists(data, reward.StringId)) return RewardLine(id, false, "The reward item record could not be found.", null, null, false);
            RewardItem? made = reward.BlueprintResearchStringId is { } research
                ? Items.CreateBlueprintReward(data, research, Diag)
                : Items.CreateRewardItem(data, reward.StringId, -1, diag: Diag);
            if (made is null) return RewardLine(id, false, "The reward item could not be created.", null, null, false);
            if (!Items.HasRoomFor(world, id, made))
                return RewardLine(id, false, "This fighter needs more inventory space for the reward.", made, null, false);
            if (reward.Charges > 0) Items.SetCharges(made, reward.Charges);
            if (!Items.Give(world, data, id, made, destroyOnFail: true))
                return RewardLine(id, false, "The reward would not fit; the purchase was rolled back.", made, null, false);
            return RewardLine(id, true, reward.Name + " purchased.", made, null, false, made.Section);
        }

        if (!Enum.TryParse(pg.Args[1], true, out ArenaRewards.Piece piece) || piece == ArenaRewards.Piece.Count
            || !Enum.TryParse(pg.Args[2], true, out ArenaRewards.Tier tier) || !ArenaRewards.IsValidTier(piece, tier))
            return usage;
        var row = ArenaRewards.Get(piece, tier);
        string record = RewardGrades.ItemRecord(piece);
        if (!Items.RecordExists(data, record)) return RewardLine(id, false, "The Warlord reward record could not be found.", null, null, false);

        // C++: RewardDelivery.cpp:833-855 (the previous grade for an upgrade, else the room check)
        RewardItem? previous = null;
        if (flags.Contains("upgrade"))
        {
            if (tier <= ArenaRewards.FirstTier(piece)) return usage + " (upgrade needs a tier above the first)";
            int want = ArenaRewards.Get(piece, (ArenaRewards.Tier)((int)tier - 1)).QualityLevel;
            var primary = Items.Copies(world, id, record);
            var alternate = RewardGrades.AlternateRecord(piece, record) is { } alt ? Items.Copies(world, id, alt) : [];
            var (inAlt, index) = RewardGrades.FindExactReward(piece, Items.Levels(primary), Items.Levels(alternate), want);
            if (index < 0) (inAlt, index) = RewardGrades.FindPreviousReward(piece, Items.Levels(primary), Items.Levels(alternate), want);
            if (index < 0) return RewardLine(id, false, "Place the previous grade in this fighter's inventory to upgrade it.", null, null, false);
            previous = (inAlt ? alternate : primary)[index];
        }

        var item = Items.CreateRewardItem(data, record, row.QualityLevel, row.ModelStringId, row.ManufacturerStringId, Diag);
        if (item is null) return RewardLine(id, false, "The reward item could not be created.", null, previous, false);
        if (previous is null && !Items.HasRoomFor(world, id, item))
            return RewardLine(id, false, "This fighter needs more inventory space for the reward.", item, null, false);

        // C++: RewardDelivery.cpp:465-503 (ItemDelivery) in RewardTransaction's order: remove the previous, deliver the new, destroy the previous
        RewardItem? retained = null;
        if (previous is not null)
        {
            retained = Items.TakeOut(world, data, id, previous);
            if (retained is null)
                return RewardLine(id, false, "The previous grade could not be removed; the purchase was cancelled.", item, previous, false);
        }
        if (!Items.Give(world, data, id, item, destroyOnFail: true))
        {
            if (retained is not null && !Items.Give(world, data, id, retained, destroyOnFail: false))
                return RewardLine(id, false, "The purchase failed and inventory restoration failed. Check the fighter's equipment before saving.", item, retained, false);
            return RewardLine(id, false, "The reward would not fit; the purchase was rolled back.", item, retained, false);
        }
        string delivered = item.Section;   // where giveItem put it, before any port-only wear
        if (retained is not null) _ = Items.Destroy(world, data, id, retained);   // loose after the take-out: always gone
        bool worn = flags.Contains("wear") && Items.Wear(world, data, id, item);
        return RewardLine(id, true, row.PieceName + " - " + row.TierName + " claimed.", item, retained, worn, delivered);
    }

    /// <summary>The PG-REWARD line of <see cref="RewardItemTest"/>: the delivered (or made) item's facts and the previous grade's.</summary>
    static string RewardLine(CharacterId id, bool ok, string status, RewardItem? item, RewardItem? previous, bool worn, string? delivered = null)
    {
        string Facts(RewardItem? i) => i is null ? "null"
            : "{\"record\":" + PGLog.Quote(i.Record) + ",\"type\":" + PGLog.Quote(i.Type) + ",\"section\":" + PGLog.Quote(i.Section)
              + ",\"equipped\":" + (i.Equipped ? "true" : "false") + ",\"level\":" + i.Level.ToString(CultureInfo.InvariantCulture)
              + ",\"quality\":" + TestJson.G(i.Quality) + ",\"companyId\":" + PGLog.Quote(i.CompanyId)
              + ",\"materialId\":" + PGLog.Quote(i.MaterialId) + ",\"charges\":" + TestJson.G(i.Charges)
              + ",\"loose\":" + (i.Loose ? "true" : "false") + ",\"destroyed\":" + (i.Destroyed ? "true" : "false") + "}";
        string line = "{\"event\":\"reward-item\",\"id\":" + PGLog.Quote(CharacterIdText.Format(id)) + ",\"ok\":" + (ok ? "true" : "false")
            + ",\"status\":" + PGLog.Quote(status) + ",\"item\":" + Facts(item) + ",\"previous\":" + Facts(previous)
            + ",\"worn\":" + (worn ? "true" : "false") + ",\"delivered\":" + (delivered is null ? "null" : PGLog.Quote(delivered)) + "}";
        ProvingGroundsMod.Ctx?.Log("PG-REWARD " + line);
        return status;
    }
}

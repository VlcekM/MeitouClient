using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Task 7.5 (challenge cards and booking, TW §2.5; the logic is Core/Glue/TownArenaLogic.Cards.cs). E1 (the engine calls the card half makes):
//   LeaderboardStore::GetActiveSaveKey (the first card's seed, TownArena.cpp:244-248) ... LeaderboardStore.GetActiveSaveGeneration (the snapshot's
//       generation: Meitou's save key is the fixed "meitou", docs/divergences.md "TownArena challenge cards")
//   LeaderboardStore::GetChallengeAccount (:349, :354, :1257) ... LeaderboardStore.GetChallengeAccount (ArenaWorldState's held account, saved as a
//       refund by ArenaSaveData: TownChallengeBuyInPolicy.SaveCredit)
//   TownChallengeWallet::Balance / Take / Credit ... src/Town/TownChallengeWallet.cs (world.Player.Money, CAP 10)
//   CombatBalanceLog::ChallengeAccepted / ChallengeSettled ... Core CombatBalanceLog over the system's CombatLogHost
//   the rest (characters, the sphere query, the clock, the card, the roster) ... the task 7.1-7.3 host members (TownArena.cs, TownArena.Bouts.cs)
// The window (TownArenaUI, task 7.6) reads and books through the methods below, from src/Town/TownArenaUISim.cs; the test-only pg town book too.

sealed partial class TownArena
{
    // ---------- the window's calls (TownArenaUI.cpp, task 7.6; the sim half is TownArenaUISim) ----------
    internal void UpdateMatchmakingPreview(World world, IReadOnlyList<CharacterId> players) => logic.UpdateMatchmakingPreview(H(world), players);   // C++ :746-923
    internal void RefreshMatchmakingRoster() => logic.RefreshMatchmakingRoster();                                                       // C++ :574-578
    internal bool BookChallenge(World world, IReadOnlyList<CharacterId> players, int slot) => logic.BookChallenge(H(world), players, slot);   // C++ :1183-1295
    internal bool BookPlayer(World world, CharacterId player) => logic.BookPlayer(H(world), player);                                  // C++ :1383-1404
    internal void CancelBooking(World world) => logic.CancelBooking(H(world));                                                          // C++ :1337-1351
    internal bool BuyChallengeRefresh(World world) => logic.BuyChallengeRefresh(H(world));                                             // C++ :491-564
    internal bool CanBuyChallengeRefresh(World world) => logic.CanBuyChallengeRefresh(H(world));                                       // C++ :483-490
    internal int GetChallengeRefreshCost(World world) => logic.GetChallengeRefreshCost(H(world));                                      // C++ :477-482
    internal string GetNextChallengeRefreshCaption(World world) => logic.GetNextChallengeRefreshCaption(H(world));                     // C++ :467-476
    internal bool SetDivision(World world, int division) => logic.SetDivision(H(world), division);                                    // C++ :565-570
    internal int GetDivision(World world) => logic.GetDivision(H(world));                                                               // C++ :466
    internal bool IsChallengeVisible(World world, int slot) => logic.IsChallengeVisible(H(world), slot);                              // C++ :571-573
    internal TownChallengeBuyInPolicy.Quote GetChallengeBuyIn(World world, int slot) => logic.GetChallengeBuyIn(H(world), slot);      // C++ :924-930
    internal bool GetPlayerReadiness(World world, CharacterId player, out string reason) => logic.GetPlayerReadiness(H(world), player, out reason);   // C++ :931-950
    internal TownArenaLogic<CharacterId>.Portraits GetChallengePortraits(World world) => logic.GetChallengePortraits(H(world));        // C++ :971-1031
    internal int GetChallengeRarity(World world, int slot) => logic.GetChallengeRarity(H(world), slot);                               // C++ :1032-1039
    internal bool IsUniqueLegendaryChallenge(World world, int slot) => logic.IsUniqueLegendaryChallenge(H(world), slot);              // C++ :1040-1048
    internal string GetChallengeUnavailableReason(World world, int slot) => logic.GetChallengeUnavailableReason(H(world), slot);      // C++ :1049-1055
    internal string GetDirectCaption(World world) => logic.GetDirectCaption(H(world));                                                  // C++ :1056-1061
    internal string GetProgressCaption(World world) => logic.GetProgressCaption(H(world));                                              // C++ :1062-1072
    internal string GetChallengeCaption(World world, int slot, bool includeLineup) => logic.GetChallengeCaption(H(world), slot, includeLineup);   // C++ :1150-1182
    internal bool IsChallengeSearching(int slot) => logic.IsChallengeSearching(slot);                                                   // C++ :1322-1324
    internal bool IsBestAvailableChallenge(int slot) => logic.IsBestAvailableChallenge(slot);                                           // C++ :1325-1328
    internal uint GetUniqueWins(World world) => logic.GetUniqueWins(H(world));                                                          // C++ :1329
    internal bool IsSkarnUnlocked(World world) => logic.IsSkarnUnlocked(H(world));                                                      // C++ :1330
    internal bool IsLineupLocked() => logic.IsLineupLocked();                                                                           // C++ :1321

    /// <summary>The window's sim half (TownArenaUISim) runs its logic over this TownArena's logic and host, bound to the world.</summary>
    internal void Ui(World world, Action<TownArenaLogic<CharacterId>, ITownHost<CharacterId>> run) => run(logic, H(world));

    sealed partial class Host
    {
        // ---------- task 7.5: the challenge card ----------
        public string CardSeedKey => owner.store().GetActiveSaveGeneration();
        public ref TownChallengeBuyInPolicy.Account ChallengeAccount() => ref owner.store().GetChallengeAccount();
        public int WalletBalance() => TownChallengeWallet.Balance(W);
        public bool WalletTake(int amount) => TownChallengeWallet.Take(W, amount);
        public bool WalletCredit(int amount) => TownChallengeWallet.Credit(W, amount);
        public void LogChallengeSettled(int outcome, int credit) => CombatBalanceLog.ChallengeSettled(owner.combatLog(), outcome, credit);
        public void LogChallengeAccepted(CombatBalanceLog.ChallengeCard card) => CombatBalanceLog.ChallengeAccepted(owner.combatLog(), card);
    }

    // ---------- TEST ONLY: pg town cards / book / cancel / money (task 7.5; the window's clicks come with task 7.6) ----------

    /// <summary>The fighters named after <paramref name="from"/> (c:&lt;slot&gt;.&lt;gen&gt;); null when one does not parse.</summary>
    static List<CharacterId>? Fighters(IReadOnlyList<string> args, int from)
    {
        var list = new List<CharacterId>();
        for (int i = from; i < args.Count; ++i)
        {
            if (!CharacterIdText.TryParse(args[i], out var c)) return null;
            list.Add(c);
        }
        return list;
    }

    /// <summary>TEST ONLY <c>pg town cards &lt;fighters...&gt;</c>: the window's refresh for that team (UpdateMatchmakingPreview, its 1 s throttle
    /// lifted so each call refreshes: g_previewTick = 0 first), then every card as the window would read it.</summary>
    string? CardsForTest(World world, IReadOnlyList<string> args)
    {
        var players = Fighters(args, 1);
        if (players is null) return "usage pg town cards <c:slot.gen ...>";
        logic.State.PreviewTick = 0;
        logic.UpdateMatchmakingPreview(H(world), players);
        return null;
    }

    /// <summary>TEST ONLY <c>pg town book &lt;slot 0-4 | direct&gt; &lt;fighters...&gt;</c>: BookChallenge / BookPlayer as the window's Book buttons
    /// call them (TownArenaUI.cpp:312-326), without the window; the answer's status is the C++'s.</summary>
    string? BookForTest(World world, IReadOnlyList<string> args)
    {
        if (args.Count < 3) return "usage pg town book <0-4|direct> <c:slot.gen ...>";
        var players = Fighters(args, 2);
        if (players is null || players.Count == 0) return "usage pg town book <0-4|direct> <c:slot.gen ...>";
        bool ok;
        if (args[1] == "direct") ok = players.Count == 1 && logic.BookPlayer(H(world), players[0]);
        else if (int.TryParse(args[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int slot)) ok = logic.BookChallenge(H(world), players, slot);
        else return "usage pg town book <0-4|direct> <c:slot.gen ...>";
        return ok ? null : "refused: " + logic.GetStatus();
    }

    /// <summary>TEST ONLY <c>pg town cancel</c>: the window's "Cancel booking" (CancelBooking, TownArena.cpp:1337-1351).</summary>
    string? CancelForTest(World world)
    {
        if (!logic.HasBooking()) return "no booking";
        logic.CancelBooking(H(world));
        return null;
    }

    /// <summary>TEST ONLY (task 7.5 review): while on, TownArena's host answers ArenaOperationBusy true, so the schedule begins no event (a booking
    /// stays idle, as between the booking and the next tick that would start it) and plans no bout; the save safety reads the real state. Per
    /// world: a load starts with it off.</summary>
    bool holdForTest;

    /// <summary>TEST ONLY <c>pg town hold on|off</c>: holds the next event's start, so a save can be taken while a booking is still idle.</summary>
    string? HoldForTest(IReadOnlyList<string> args)
    {
        if (args.Count != 2 || args[1] is not ("on" or "off")) return "usage pg town hold on|off";
        holdForTest = args[1] == "on";
        return null;
    }

    /// <summary>TEST ONLY <c>pg town heal &lt;c:slot.gen&gt;</c> (task 8.4c): heals one character, a town NPC included, as the dev console's <c>heal</c>
    /// does for the selected (Meitou's DebugHeal, next tick; limbs not restored). The console's <c>heal</c> reaches only the player's selection
    /// (PlayerSystem.Select takes player characters only), so the bed recovery's healed patient needs this verb.</summary>
    static string? HealForTest(World world, IReadOnlyList<string> args)
    {
        if (args.Count != 2 || !CharacterIdText.TryParse(args[1], out var id)) return "usage pg town heal c:<slot>.<generation>";
        return PgEngine.DebugHeal(world, id) ? null : "refused: no such character";
    }

    /// <summary>TEST ONLY <c>pg town limbs [id]</c> (task 8.5): the limb shop's state, and with an id that character's four limbs and limb-section items.</summary>
    static string? LimbsForTest(IReadOnlyList<string> args, out CharacterId? detail)
    {
        detail = null;
        if (args.Count == 1) return null;
        if (args.Count != 2 || !CharacterIdText.TryParse(args[1], out var id)) return "usage pg town limbs [c:<slot>.<generation>]";
        detail = id;
        return null;
    }

    /// <summary>TEST ONLY <c>pg town sever &lt;id&gt; &lt;0-3&gt;</c> (task 8.5): limb slot 0 left arm / 1 right arm / 2 left leg / 3 right leg cut off by
    /// Meitou's <c>MedicalState.Sever</c> and the game's after-sever step (Limbs.Sever: the console's <c>hurt</c> severs only by destroying the part and
    /// may kill). The answer carries that character's limbs.</summary>
    string? SeverForTest(World world, IReadOnlyList<string> args, out CharacterId? detail)
    {
        detail = null;
        if (args.Count != 3 || !CharacterIdText.TryParse(args[1], out var id) || !int.TryParse(args[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int slot) || slot > 3)
            return "usage pg town sever c:<slot>.<generation> <0-3>";
        detail = id;
        if (Members.Missing(Part.TestVerbs).Any(n => n.StartsWith("MedicalState.Sever", StringComparison.Ordinal) || n == "CharacterTable.LimbsChanged"))
            return "refused: MedicalState.Sever or CharacterTable.LimbsChanged not found";
        try { return Limbs.Sever(world, data, id, slot) ? null : "refused: no such character or that limb is not an original limb"; }
        catch (InvalidOperationException e) when (e.Message.StartsWith("severed,", StringComparison.Ordinal)) { return "partly done: " + e.Message; }
        catch (Exception e) { return "refused: " + e.GetType().Name + ": " + e.Message; }
    }

    /// <summary>TEST ONLY <c>pg town refresh</c>: the window's "Buy Refresh (N Cats)" (BuyChallengeRefresh, TownArena.cpp:491-564).</summary>
    string? RefreshForTest(World world) => logic.BuyChallengeRefresh(H(world)) ? null : "refused: " + logic.GetStatus();

    /// <summary>TEST ONLY <c>pg town money &lt;cats&gt;</c>: sets the player's money (world.Player.Money), the scenario's starting purse.</summary>
    static string? MoneyForTest(World world, IReadOnlyList<string> args)
    {
        if (args.Count != 2 || !long.TryParse(args[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long cats))
            return "usage pg town money <cats>";
        world.Player.Money = cats;
        return null;
    }

    /// <summary>TEST ONLY: the card part of the PG-TOWN line (money, account, ticket, booking, the five offers and previews, progress).</summary>
    string CardsJson(World world)
    {
        var st = logic.State;
        var h = H(world);
        var d = st.Display;
        static string N(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static string Q(string t) => PGLog.Quote(t);
        static string B(bool b) => b ? "true" : "false";
        var card = h.TownChallenges();
        ref var account = ref store().GetChallengeAccount();
        var sb = new System.Text.StringBuilder();
        sb.Append(",\"money\":").Append(N(world.Player.Money))
          .Append(",\"credit\":").Append(N(account.Credit)).Append(",\"escrow\":").Append(N(account.Escrow))
          .Append(",\"ticketPending\":").Append(B(d.BuyInTicket.Pending)).Append(",\"ticketBuyIn\":").Append(N(d.BuyInTicket.Quote.BuyIn))
          .Append(",\"ticketPayout\":").Append(N(d.BuyInTicket.Quote.Payout))
          .Append(",\"queued\":").Append(N(st.QueuedChallenge)).Append(",\"activeChallenge\":").Append(N(st.ActiveChallenge))
          .Append(",\"reservationMode\":").Append(Q(Core.TownArena.MatchModeFor(d.ChallengeReservation).ToString()))
          .Append(",\"playerMatch\":").Append(B(st.PlayerMatch)).Append(",\"playerCount\":").Append(N(st.PlayerCount))
          .Append(",\"bookedPlayer\":").Append(Q(CharacterIdText.Format(st.BookedPlayer)))
          .Append(",\"bookedTeam\":[").AppendJoin(",", st.BookedTeam.Select(c => Q(CharacterIdText.Format(c)))).Append(']')
          .Append(",\"marksMultiplier\":").Append(TestJson.G(st.ActiveMarksMultiplier)).Append(",\"marksBonus\":").Append(N(st.ActiveMarksBonus))
          .Append(",\"division\":").Append(N(card.Division)).Append(",\"challengeWins\":").Append(N(card.ChallengeWins))
          .Append(",\"uniqueWins\":").Append(N(card.UniqueWins)).Append(",\"paidRefreshCount\":").Append(N(card.PaidRefreshCount))
          .Append(",\"direct\":").Append(Q(logic.GetDirectCaption(h))).Append(",\"directResolved\":").Append(B(TownChallengePolicy.Resolved(d.DirectPreview)))
          .Append(",\"nextRefresh\":").Append(Q(logic.GetNextChallengeRefreshCaption(h)))
          .Append(",\"cards\":[");
        for (int slot = 0; slot < 5; ++slot)
        {
            var o = card.Offers[slot];
            var pv = d.Previews[slot];
            var quote = logic.GetChallengeBuyIn(h, slot);
            if (slot > 0) sb.Append(',');
            sb.Append("{\"slot\":").Append(N(slot)).Append(",\"visible\":").Append(B(TownChallengePolicy.Visible(card, slot)))
              .Append(",\"state\":").Append(Q(o.State.ToString())).Append(",\"epoch\":").Append(N(o.Epoch))
              .Append(",\"rarity\":").Append(N(TownChallengePolicy.OfferRarity(o, card.Division))).Append(",\"mode\":").Append(N(o.Mode))
              .Append(",\"resolved\":").Append(B(TownChallengePolicy.Resolved(pv)))
              .Append(",\"players\":").Append(N(TownChallengePolicy.Players(pv))).Append(",\"enemies\":").Append(N(TownChallengePolicy.Opponents(pv)))
              .Append(",\"opponents\":[").AppendJoin(",", Enumerable.Range(0, Math.Max(0, Math.Min(4, TownChallengePolicy.Opponents(pv)))).Select(i => Q(TownChallengePolicy.OpponentIdConst(pv, i)))).Append(']')
              .Append(",\"buyIn\":").Append(N(quote.BuyIn)).Append(",\"payout\":").Append(N(quote.Payout)).Append(",\"valid\":").Append(B(quote.Valid()))
              .Append(",\"searching\":").Append(B(logic.IsChallengeSearching(slot)))
              .Append(",\"reason\":").Append(Q(d.UnavailableReasons[slot]))
              .Append(",\"caption\":").Append(Q(Caption(logic, h, slot))).Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    static string Caption(TownArenaLogic<CharacterId> logic, ITownHost<CharacterId> h, int slot) => logic.GetChallengeCaption(h, slot, true);
}


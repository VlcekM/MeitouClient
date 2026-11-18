namespace ProvingGrounds.Core.Glue;

// Task 7.5 (challenge cards and booking, TW §2.5): TownArena.cpp:151-155 (MatchesOpponent), 177-295 (the reasons, MatchModeFor, Challenges),
// 347-366 (PayChallengeCredit, SettleChallenge), 466-564 (division, refresh caption / cost / paid refresh), 565-573, 691-745 (CancelUpcomingForPlayer,
// PopulatePreview, QuoteFor), 776-923 (the card half of UpdateMatchmakingPreview), 924-1072 (the window's reads), 1098-1149 (the player halves of
// the result and the abort), 1150-1295 (GetChallengeCaption, BookChallenge), 1322-1330, 1360-1404 (ReservePlayer, BookPlayer). The player half of
// BeginEvent (:1525-1606, :1658-1670, :1682-1683, :1725-1733) and of OnMatchStarted (:1471-1472) are in TownArenaLogic.Bouts.cs, where 7.3 left them.
// The pure parts already in Core are called, not re-ported: the captions (Core.TownArena.Captions.cs, over the state's DisplayState), the texts
// (Core.TownArena.Messages.cs), FoldSaveKey / ApplyPaidRefresh / CanBuyChallengeRefresh / SearchRequestSeed / DirectSearchSeed / ChallengeOutcome /
// DefeatedUniques / ActiveMarks (Core.TownArena.Rules.cs), TownChallengePolicy, TownChallengeBuyInPolicy, TownArenaPolicy. The three functions whose
// Core copies read a Core.TownArena.Actor (NamedOpponentUnavailableReason, PreviewUnavailableReason, QuoteFor) are written here over the host and the
// glue's TownRosterSnapshot, line for line, as task 7.2 did for Ready: the glue holds characters as TIds and reads each over the host.

public sealed partial class TownArenaLogic<TId>
{
    /// <summary>C++ <c>now - g_lastCardDiscovery &lt; 5000</c> (TownArena.cpp:254): the legacy opponents are bound at most every 5 s.</summary>
    public const uint CardDiscoveryMs = 5000;

    // C++: TownArena.cpp:151-155
    bool MatchesOpponent(ITownHost<TId> h, TId c, string key) =>
        !h.IsNull(c) && key.Length != 0 && (PersistentId(h, c) == key || LegacyOpponentId(h, c) == key ||
            TownChallengePolicy.OpponentKey("", h.Role(c), h.Name(c)) == key);

    // ---------- the reasons (TownArena.cpp:177-232) ----------

    // C++: TownArena.cpp:177-208
    internal string NamedOpponentUnavailableReason(ITownHost<TId> h, in TownChallengePolicy.Offer offer, TownRosterSnapshot<TId> snapshot)
    {
        string label = TownChallengePolicy.Title(offer.Encounter);
        string role = TownChallengePolicy.Role(offer.Encounter);
        string opponentId = TownChallengePolicy.OpponentIdConst(offer, 0);
        TId match = h.None;
        bool ambiguous = false;
        foreach (var it in snapshot.Characters)
        {
            TId candidate = it.Value.Unique ? it.Value.Character : h.None;   // an ambiguous key's value is the C++ NULL
            if (h.IsNull(candidate) || !h.Valid(candidate)) continue;
            if ((opponentId.Length != 0 && MatchesOpponent(h, candidate, opponentId)) ||
                (opponentId.Length == 0 && TownArenaRuntimePolicy.IdEquals(h.Role(candidate), role)))
            {
                if (!h.IsNull(match) && !Eq.Equals(match, candidate)) { ambiguous = true; break; }
                match = candidate;
            }
        }
        if (ambiguous) return label + ": identity is ambiguous";
        if (h.IsNull(match)) return label + ": not present in the registry town";
        if (h.AftercareManaged(match)) return label + ": still in aftercare";
        if (h.LimbShopManaged(match)) return label + ": waiting for replacement limbs";
        if (IsFighter(h, match)) return label + ": already assigned to an active bout";
        string reason;
        bool quiet = s.QuietDiscovery;
        s.QuietDiscovery = true;
        bool ready;
        try { ready = Ready(h, match, false, false, out reason); }
        finally { s.QuietDiscovery = quiet; }
        if (!ready) return label + ": " + (reason.Length == 0 ? "not ready to fight" : reason);
        if (!(TownMatchmakingRuntimeLogic.Ability(h, match) > 0)) return label + ": combat stats unavailable";
        return label + ": unavailable for this matchup";
    }

    // C++: TownArena.cpp:210-232
    internal string PreviewUnavailableReason(ITownHost<TId> h, in TownChallengePolicy.Offer offer, TownRosterSnapshot<TId> snapshot)
    {
        if (!snapshot.PlayersReady) return snapshot.PlayerBlocker.Length == 0 ?
            "Selected fighters are unavailable or recovering" : snapshot.PlayerBlocker;
        if (!TownChallengePolicy.Resolved(offer))
        {
            if (TownChallengePolicy.NamedForDivision(offer.Encounter, offer.Division))
                return NamedOpponentUnavailableReason(h, offer, snapshot);
            return "No suitable recovered opponents for this team";
        }
        if (snapshot.Players.Count != TownChallengePolicy.Players(offer))
            return "Selected lineup no longer matches this challenge";
        for (int i = 0; i < TownChallengePolicy.Opponents(offer); ++i)
            if (!EligibleOpponent(snapshot, TownChallengePolicy.OpponentIdConst(offer, i)))
            {
                if (i == 0 && TownChallengePolicy.NamedForDivision(offer.Encounter, offer.Division))
                    return NamedOpponentUnavailableReason(h, offer, snapshot);
                return TownChallengePolicy.OpponentNameConst(offer, i) + " is unavailable or recovering";
            }
        int participants = TownChallengePolicy.Players(offer) + TownChallengePolicy.Opponents(offer);
        if (snapshot.Medics.Count < TownAftercarePolicy.RequiredMedics(participants))
            return "Waiting for medical capacity";
        return "";
    }

    // ---------- the card (TownArena.cpp:239-295) ----------

    /// <summary>C++: TownArena.cpp:239-295 <c>Challenges()</c>: the held card; with a world and an active save, its first card seeded (port: from
    /// <see cref="ITownHost{TId}.CardSeedKey"/>), every stale slot regenerated for the current epoch except the queued or running one, and (at most
    /// every 5 s) a legacy offer's opponents bound to the unique matching character in the registry's town.</summary>
    public TownChallengePolicy.Card Challenges(ITownHost<TId> h)
    {
        TownChallengePolicy.Card card = h.TownChallenges();
        if (!h.HasWorld || !h.HasActiveSave) return card;
        TownArena.FoldSaveKey(card, h.CardSeedKey);   // :242-248 (only while the first offer's epoch is -2)
        int epoch = TownArenaPolicy.ChallengeCardEpoch(h.TotalHours, PGConfig.ChallengeValues().RefreshHours);
        TownChallengePolicy.RefreshGenerated(card, epoch, s.ActiveChallenge >= 0 ? s.ActiveChallenge : s.QueuedChallenge);
        PlacedBuilding? found = s.TownRegistry is { } r ? h.Resolve(r) : null;
        uint now = h.NowMs;
        if (found is not { } registry || (s.LastCardDiscovery != 0 && now - s.LastCardDiscovery < CardDiscoveryMs)) return card;
        s.LastCardDiscovery = now;
        IReadOnlyList<TId>? nearby = h.CharactersInSphere(registry.Building.Position, RegistrySearchRadius, RegistrySearchMax);
        for (int slot = 0; slot < 5; ++slot)
        {
            ref TownChallengePolicy.Offer offer = ref TownChallengePolicy.At(card, slot);
            if (offer.Generated) continue;
            for (int member = 0; member < TownChallengePolicy.Opponents(offer); ++member)
            {
                ref string boundId = ref TownChallengePolicy.OpponentId(ref offer, member);
                ref string boundName = ref TownChallengePolicy.OpponentName(ref offer, member);
                if (boundId.Length != 0) continue;
                int role = TownChallengePolicy.OpponentRole(offer, member);
                for (int i = 0; nearby is not null && i < nearby.Count; ++i)
                {
                    TId c = nearby[i];
                    if (h.IsNull(c) || !h.Valid(c)) continue;
                    if (h.IsDead(c) || h.CurrentTown(c) != registry.Town ||
                        !TownArenaRuntimePolicy.IdEquals(h.Role(c), TownChallengePolicy.Role(role))) continue;
                    string uid = PersistentId(h, c);
                    if (uid.Length == 0) continue;
                    bool ambiguous = false;
                    for (int j = 0; j < nearby.Count; ++j)
                    {
                        TId other = nearby[j];
                        if (h.IsNull(other) || Eq.Equals(other, c) || !h.Valid(other)) continue;
                        if (h.CurrentTown(other) == registry.Town && MatchesOpponent(h, other, uid))
                            ambiguous = true;
                    }
                    if (ambiguous) continue;
                    boundId = uid;
                    boundName = h.Name(c);
                    h.Debug(TownArena.OpponentResolvedLog(boundName, uid));
                    break;
                }
            }
        }
        return card;
    }

    // ---------- settlement (TownArena.cpp:347-366) ----------

    // C++: TownArena.cpp:347-351
    void PayChallengeCredit(ITownHost<TId> h)
    {
        if (!h.HasActiveSave) return;
        ref TownChallengeBuyInPolicy.Account account = ref h.ChallengeAccount();
        if (h.WalletCredit(account.Credit)) account.Credit = 0;
    }

    /// <summary>C++: TownArena.cpp:352-366 <c>SettleChallenge(outcome)</c>: 1 won (payout credited), 0 lost (buy-in consumed), -1 cancelled or drawn
    /// (buy-in refunded, and the paid-refresh count the booking reset put back); the credit is paid into the player's money at once when it can be.</summary>
    void SettleChallenge(ITownHost<TId> h, int outcome)
    {
        if (!s.Display.BuyInTicket.Pending) return;
        int credit = TownChallengeBuyInPolicy.Settle(ref h.ChallengeAccount(), ref s.Display.BuyInTicket, outcome);
        if (s.PaidRefreshResetPending)
        {
            if (outcome < 0 && h.HasActiveSave)
                h.TownChallenges().PaidRefreshCount = s.PaidRefreshCountBeforeBooking;
            s.PaidRefreshResetPending = false;
        }
        h.LogChallengeSettled(outcome, credit);
        PayChallengeCredit(h);
        Status(h, TownArena.SettleChallengeMessage(outcome, credit));
    }

    /// <summary>C++ <c>TownArenaPolicy::CancelBooking(TownChallengePolicy::At(LeaderboardStore::GetTownChallenges(), slot).state)</c>
    /// (TownArena.cpp:410, :1340).</summary>
    void CancelCardBooking(ITownHost<TId> h, int slot) =>
        TownArenaPolicy.CancelBooking(ref TownChallengePolicy.At(h.TownChallenges(), slot).State);

    /// <summary>C++ <c>g_challengeReservation = TownChallengePolicy::Offer()</c> (TownArena.cpp:426, :1349).</summary>
    void ResetChallengeReservation() => s.Display.ChallengeReservation = new TownChallengePolicy.Offer();

    // ---------- division and the paid refresh (TownArena.cpp:466-573) ----------

    // C++: TownArena.cpp:466
    public int GetDivision(ITownHost<TId> h) => h.TownChallenges().Division;

    // C++: TownArena.cpp:467-476
    public string GetNextChallengeRefreshCaption(ITownHost<TId> h) =>
        TownArena.NextChallengeRefreshCaption(h.HasWorld, h.HasWorld ? h.TotalHours : 0, PGConfig.ChallengeValues().RefreshHours);

    // C++: TownArena.cpp:477-482
    public int GetChallengeRefreshCost(ITownHost<TId> h)
    {
        PGConfig.Challenges settings = PGConfig.ChallengeValues();
        TownChallengePolicy.Card card = h.TownChallenges();
        return TownArenaPolicy.PaidRefreshCost(settings.RefreshBaseCostCats, settings.RefreshCostMultiplier, card.PaidRefreshCount);
    }

    // C++: TownArena.cpp:483-490
    public bool CanBuyChallengeRefresh(ITownHost<TId> h)
    {
        bool registryValid = s.TownRegistry is { } r && h.Resolve(r) is not null;
        if (!h.HasActiveSave || !h.HasWorld || !registryValid || s.BookingPending || s.QueuedChallenge >= 0 || s.ActiveChallenge >= 0 ||
            s.Phase == TownPhase.Preparing || s.Phase == TownPhase.Fighting) return false;
        int cost = GetChallengeRefreshCost(h);
        return cost > 0 && h.WalletBalance() >= cost;
    }

    // C++: TownArena.cpp:491-564
    public bool BuyChallengeRefresh(ITownHost<TId> h)
    {
        if (!h.HasActiveSave || !h.HasWorld)
        {
            Status(h, TownArena.Text.RefreshNoSave);
            return false;
        }
        if (s.TownRegistry is not { } held || h.Resolve(held) is null)
        {
            Status(h, TownArena.Text.RefreshNoRegistry);
            return false;
        }
        if (s.BookingPending || s.QueuedChallenge >= 0 || s.ActiveChallenge >= 0 ||
            s.Phase == TownPhase.Preparing || s.Phase == TownPhase.Fighting)
        {
            Status(h, TownArena.Text.RefreshBooked);
            return false;
        }
        TownChallengePolicy.Card card = Challenges(h);
        PGConfig.Challenges settings = PGConfig.ChallengeValues();
        int epoch = TownArenaPolicy.ChallengeCardEpoch(h.TotalHours, settings.RefreshHours);
        if (epoch < -1)
        {
            Status(h, TownArena.Text.RefreshBadClock);
            return false;
        }
        int cost = TownArenaPolicy.PaidRefreshCost(settings.RefreshBaseCostCats, settings.RefreshCostMultiplier, card.PaidRefreshCount);
        int balance = h.WalletBalance();
        if (cost <= 0 || balance < cost)
        {
            Status(h, TownArena.RefreshCostMessage(cost, balance));
            return false;
        }
        if (!h.WalletTake(cost))
        {
            Status(h, TownArena.Text.RefreshCollectFailed);
            return false;
        }
        TownArena.ApplyPaidRefresh(card, epoch);   // :527-536

        s.PreviewKey = "";
        s.RetryContext = "";
        s.PreviewTick = 0;
        s.PreviewSnapshotValid = false;
        s.PreviewSnapshotTick = 0;
        BeginSearchGeneration(h);
        for (int slot = 0; slot < 5; ++slot)
        {
            s.Display.Previews[slot] = new TownChallengePolicy.Offer();
            s.SearchQueued[slot] = false;
            s.SearchCompleted[slot] = false;
            s.Searching[slot] = false;
            s.BestAvailable[slot] = false;
            s.Display.UnavailableReasons[slot] = "";
            s.Display.BuyInQuotes[slot] = new TownChallengeBuyInPolicy.Quote();
        }
        s.Display.DirectPreview = new TownChallengePolicy.Offer();
        s.Display.BuyInQuotes[5] = new TownChallengeBuyInPolicy.Quote();
        int nextCost = TownArenaPolicy.PaidRefreshCost(settings.RefreshBaseCostCats, settings.RefreshCostMultiplier, card.PaidRefreshCount);
        Status(h, TownArena.RefreshedMessage(cost, nextCost));
        return true;
    }

    // C++: TownArena.cpp:565-570
    public bool SetDivision(ITownHost<TId> h, int division)
    {
        if (!TownChallengePolicy.SetDivision(h.TownChallenges(), division, IsLineupLocked())) return false;
        s.PreviewKey = ""; s.PreviewTick = 0;
        return true;
    }

    // C++: TownArena.cpp:571-573
    public bool IsChallengeVisible(ITownHost<TId> h, int slot) => TownChallengePolicy.Visible(h.TownChallenges(), slot);

    // ---------- the previews and quotes (TownArena.cpp:691-745) ----------

    // C++: TownArena.cpp:691-697
    void CancelUpcomingForPlayer(ITownHost<TId> h)
    {
        CancelPlannedNpcBout(h, TownArena.Text.PlayerTookSlot);
        if (s.Phase == TownPhase.Preparing && !s.PlayerMatch)
        {
            h.IngressCancel();
            Finish(h, TownArena.Text.UpcomingCancelledForPlayer);
        }
    }

    // C++: TownArena.cpp:698-711
    static void PopulatePreview(ITownHost<TId> h, ref TownChallengePolicy.Offer offer, TownRosterSnapshot<TId> snapshot, TownMatchmakingPolicy.Match match)
    {
        TownChallengePolicy.ClearPreview(ref offer);
        if (!match.Valid) return;
        offer.PlayerCount = match.A.Count;
        offer.EnemyCount = match.B.Count;
        offer.Context = snapshot.Context;
        for (int i = 0; i < offer.EnemyCount; ++i)
        {
            TId c = ResolveOrNull(h, snapshot, match.B[i]);
            TownChallengePolicy.OpponentId(ref offer, i) = match.B[i];
            TownChallengePolicy.OpponentName(ref offer, i) = TownMatchmakingRuntimeLogic.Label(h, c);
            offer.Roles[i] = TownMatchmakingRuntimeLogic.Role(h, c);
        }
    }

    /// <summary>C++: TownArena.cpp:715-745 <c>QuoteFor</c>: the buy-in and return of a resolved offer for the snapshot's players (team scores with the
    /// MMR influence of <see cref="TownMatchmakingPolicy.TeamScore(double[], double[], int)"/>, 1.0); <paramref name="ownScore"/> and
    /// <paramref name="enemyScore"/> are the two scores that priced it (for the combat log).</summary>
    internal TownChallengeBuyInPolicy.Quote QuoteFor(ITownHost<TId> h, in TownChallengePolicy.Offer offer, TownRosterSnapshot<TId> snapshot, int division,
        out double ownScore, out double enemyScore)
    {
        ownScore = 0; enemyScore = 0;   // the C++ leaves its out-pointers untouched on an early return; the callers start them at 0
        var unavailable = new TownChallengeBuyInPolicy.Quote();
        if (!snapshot.PlayersReady || !TownChallengePolicy.Resolved(offer) ||
            snapshot.Players.Count != TownChallengePolicy.Players(offer) ||
            (offer.Generated && PreviewUnavailableReason(h, offer, snapshot).Length != 0)) return unavailable;
        double[] own = new double[3], ownRatings = new double[3], enemy = new double[4], enemyRatings = new double[4];
        int count = TownChallengePolicy.Opponents(offer);
        if (count < 1 || count > 4 || snapshot.Players.Count > 3) return unavailable;
        for (int i = 0; i < snapshot.Players.Count; ++i)
        {
            own[i] = snapshot.Players[i].Ability;
            ownRatings[i] = snapshot.Players[i].Mmr;
        }
        for (int member = 0; member < count; ++member)
        {
            TId match = h.None;
            foreach (var it in snapshot.Characters)
            {
                TId c = it.Value.Unique ? it.Value.Character : h.None;
                if (!h.IsNull(c) && MatchesOpponent(h, c, TownChallengePolicy.OpponentIdConst(offer, member)))
                {
                    if (!h.IsNull(match) && !Eq.Equals(match, c)) return unavailable;
                    match = c;
                }
            }
            if (h.IsNull(match)) return unavailable;
            enemy[member] = TownMatchmakingRuntimeLogic.Ability(h, match);
            enemyRatings[member] = h.TownRating(match);
        }
        double own_ = TownMatchmakingPolicy.TeamScore(own, ownRatings, snapshot.Players.Count);
        double enemy_ = TownMatchmakingPolicy.TeamScore(enemy, enemyRatings, count);
        ownScore = own_;
        enemyScore = enemy_;
        return TownChallengeBuyInPolicy.Price(division, own_, enemy_);
    }

    // ---------- UpdateMatchmakingPreview (TownArena.cpp:746-923) ----------

    /// <summary>C++: TownArena.cpp:746-923 <c>UpdateMatchmakingPreview(players)</c>: the throttled roster (task 7.2's <see cref="UpdatePreviewRoster"/>),
    /// then each card slot: a finished background search's result taken (its preview, reason, best-available mark), a still-valid preview kept, a
    /// new search queued for a stale generated slot, a legacy slot's reason; the direct 1v1 preview (one selected fighter); each visible slot's quote.</summary>
    public void UpdateMatchmakingPreview(ITownHost<TId> h, IReadOnlyList<TId> players)
    {
        TownChallengePolicy.Card card = Challenges(h);
        if (!UpdatePreviewRoster(h, players, card, out bool sameCards, out string lineupKey)) return;
        TownRosterSnapshot<TId> snapshot = s.PreviewSnapshot;
        TownArena.DisplayState d = s.Display;
        for (int slot = 0; slot < 6; ++slot)
            d.BuyInQuotes[slot] = new TownChallengeBuyInPolicy.Quote();

        string searchContext = s.PreviewKey + ":" + snapshot.Context;
        RestartSearchIfContextChanged(h, searchContext);   // :779-793
        while (h.MatchmakingPoll(out TownMatchmakingSearch.Result? result) && result is not null)
        {
            if (result.Generation != s.SearchGeneration || result.Slot < 0 || result.Slot >= 5)
                continue;
            int slot = result.Slot;
            ref TownChallengePolicy.Offer offer = ref d.Previews[slot];
            s.SearchQueued[slot] = false;
            s.SearchCompleted[slot] = true;
            s.Searching[slot] = false;
            s.BestAvailable[slot] = false;
            PopulatePreview(h, ref offer, snapshot, result.Match);
            offer.Context = lineupKey;
            if (result.Match.Valid)
            {
                s.BestAvailable[slot] = result.Match.BestAvailable;
                d.UnavailableReasons[slot] = PreviewUnavailableReason(h, offer, snapshot);
                h.Debug(TownArena.SearchResolvedLog(slot, result.Match.BestAvailable));
            }
            else if (result.Match.Failure == TownMatchmakingPolicy.Match.FailureCode.PlayersTooWeak)
                d.UnavailableReasons[slot] = "Selected fighter is too weak for this challenge.";
            else
            {
                int rarity = TownChallengePolicy.OfferRarity(offer, card.Division);
                string anchor = rarity == 3 &&
                    TownChallengePolicy.NamedForDivision(offer.Encounter, card.Division) ?
                    TownChallengePolicy.Role(offer.Encounter) : "";
                bool anchorReady = anchor.Length == 0;
                for (int i = 0; i < snapshot.Npcs.Count; ++i)
                    if (snapshot.Npcs[i].Role == anchor) anchorReady = true;
                d.UnavailableReasons[slot] = anchorReady ?
                    "No suitable recovered opponents for this challenge." :
                    NamedOpponentUnavailableReason(h, offer, snapshot);
            }
        }

        List<List<string>> history = CopyHistory(card.PlayerHistory);
        for (int slot = 0; slot < 5; ++slot)
        {
            s.Searching[slot] = s.SearchQueued[slot];
            if (sameCards && card.Offers[slot].State == TownArenaPolicy.OfferState.Available &&
                TownChallengePolicy.Resolved(d.Previews[slot]))
            {
                bool opponentsReady = true;
                var opponentIds = new List<string>();
                for (int member = 0; member < TownChallengePolicy.Opponents(d.Previews[slot]); ++member)
                {
                    string id = TownChallengePolicy.OpponentIdConst(d.Previews[slot], member);
                    opponentIds.Add(id);
                    if (!EligibleOpponent(snapshot, id)) opponentsReady = false;
                }
                if (opponentsReady)
                {
                    d.UnavailableReasons[slot] = PreviewUnavailableReason(h, d.Previews[slot], snapshot);
                    TownMatchmakingPolicy.RecordHistory(history, opponentIds);
                    continue;
                }
            }

            if (!s.SearchQueued[slot] && !s.SearchCompleted[slot])
            {
                d.Previews[slot] = TownChallengePolicy.PreviewSource(card, slot);
                s.BestAvailable[slot] = false;
            }
            ref TownChallengePolicy.Offer offer = ref d.Previews[slot];
            if (offer.State != TownArenaPolicy.OfferState.Available) continue;
            if (!offer.Generated)
            {
                d.UnavailableReasons[slot] = PreviewUnavailableReason(h, offer, snapshot);
                continue;
            }
            if (s.SearchCompleted[slot]) continue;
            TownChallengePolicy.ClearPreview(ref offer);
            offer.Division = card.Division;
            d.UnavailableReasons[slot] = snapshot.PlayersReady ?
                "Searching eligible opponents..." : snapshot.PlayerBlocker;
            if (!snapshot.PlayersReady || s.SearchQueued[slot]) continue;

            int offerRarity = TownChallengePolicy.OfferRarity(offer, card.Division);
            var request = new TownMatchmakingSearch.Request();
            request.Generation = s.SearchGeneration;
            request.Slot = slot;
            request.Players = new List<TownMatchmakingPolicy.Fighter>(snapshot.Players);
            request.Npcs = new List<TownMatchmakingPolicy.Fighter>(snapshot.Npcs);
            request.Division = (TownMatchmakingPolicy.Division)card.Division;
            request.Seed = TownArena.SearchRequestSeed(offer, slot, card, out string requiredRole, out bool forceSoloNamed);   // :874, :889-895
            request.History = CopyHistory(history);
            request.MaxEnemies = 4;
            request.MaxTotal = snapshot.Medics.Count;
            request.Rarity = offerRarity;
            request.Teams1v1 = offer.Mode == (int)TownChallengePolicy.ChallengeMode.Teams1v1;
            request.ChallengeDifficulty = PGConfig.ChallengeValues().Difficulty;
            request.RequiredRole = requiredRole;
            request.ForceSoloNamed = forceSoloNamed;
            s.SearchQueued[slot] = h.MatchmakingEnqueue(request);
            s.Searching[slot] = s.SearchQueued[slot];
            if (!s.SearchQueued[slot])
            {
                s.SearchCompleted[slot] = true;
                d.UnavailableReasons[slot] = TownArena.Text.MatchmakingUnavailable;
            }
        }

        d.UnavailableReasons[5] = "";
        s.Searching[5] = false;
        d.DirectPreview = new TownChallengePolicy.Offer();
        d.DirectPreview.Generated = true;
        d.DirectPreview.Division = card.Division;
        if (snapshot.PlayersReady && snapshot.Players.Count == 1)
        {
            TownMatchmakingPolicy.Match direct = TownMatchmakingPolicy.SelectPlayer(
                snapshot.Players, snapshot.Npcs,
                (TownMatchmakingPolicy.Division)card.Division,
                TownArena.DirectSearchSeed(card), card.PlayerHistory, 1,
                snapshot.Medics.Count);
            PopulatePreview(h, ref d.DirectPreview, snapshot, direct);
            d.DirectPreview.Context = lineupKey;
        }
        for (int slot = 0; slot < 5; ++slot)
            if (TownChallengePolicy.Visible(card, slot))
                d.BuyInQuotes[slot] = QuoteFor(h, d.Previews[slot], snapshot, card.Division, out _, out _);
    }

    /// <summary>A <c>TownMatchmakingPolicy::History</c> copied by value (the C++ assignment).</summary>
    static List<List<string>> CopyHistory(List<List<string>> from)
    {
        var copy = new List<List<string>>(from.Count);
        foreach (var bout in from) copy.Add(new List<string>(bout));
        return copy;
    }

    // ---------- the window's reads (TownArena.cpp:924-1072, 1150-1182, 1322-1330) ----------

    // C++: TownArena.cpp:924-930
    public TownChallengeBuyInPolicy.Quote GetChallengeBuyIn(ITownHost<TId> h, int slot) =>
        TownArena.ChallengeBuyIn(h.TownChallenges(), s.Display, slot);   // IsChallengeVisible reads the same held card

    /// <summary>C++: TownArena.cpp:931-950 <c>GetPlayerReadiness(player, reason)</c> (quiet: no Ready log line).</summary>
    public bool GetPlayerReadiness(ITownHost<TId> h, TId player, out string reason)
    {
        bool previous = s.QuietDiscovery;
        s.QuietDiscovery = true;
        try
        {
            if (!Ready(h, player, false, true, out reason)) return false;
            PlacedBuilding? registry = s.TownRegistry is { } r ? h.Resolve(r) : null;
            if (registry is not { } reg || h.CurrentTown(player) != reg.Town)
            {
                reason = "not at this town registry";
                return false;
            }
            if (h.AftercareManaged(player)) { reason = "still in aftercare"; return false; }
            if (TownMatchmakingRuntimeLogic.SelectionIdentity(h, player).Length == 0) { reason = "live character handle unavailable"; return false; }
            if (!(TownMatchmakingRuntimeLogic.Ability(h, player) > 0)) { reason = "combat stats unavailable"; return false; }
            reason = "";
            return true;
        }
        finally { s.QuietDiscovery = previous; }
    }

    /// <summary>What <see cref="GetChallengePortraits"/> answers per slot (0-4 the cards, 5 Skarn): the opponents (the host's None where no unique
    /// character matched) and their names.</summary>
    public sealed class Portraits
    {
        public readonly List<TId>[] Opponents = [[], [], [], [], [], []];
        public readonly List<string>[] Names = [[], [], [], [], [], []];
    }

    // C++: TownArena.cpp:971-1031
    public Portraits GetChallengePortraits(ITownHost<TId> h)
    {
        var p = new Portraits();
        TownChallengePolicy.Card card = Challenges(h);
        var offers = new TownChallengePolicy.Offer[6];
        bool any = false;
        for (int slot = 0; slot < 6; ++slot)
        {
            offers[slot] = new TownChallengePolicy.Offer();
            p.Opponents[slot].Clear();
            p.Names[slot].Clear();
            if (slot == 5)
            {
                offers[slot] = new TownChallengePolicy.Offer();
                offers[slot].Encounter = (int)TownChallengePolicy.Encounter.Skarn;
                offers[slot].Division = 2;
                p.Opponents[slot].Add(h.None);
                p.Names[slot].Add(TownChallengePolicy.Title((int)TownChallengePolicy.Encounter.Skarn));
                any = true;
                continue;
            }
            if (!TownChallengePolicy.Visible(card, slot)) continue;
            offers[slot] = TownArena.DisplayedOffer(card, s.Display, slot);
            if (TownChallengePolicy.Resolved(offers[slot]))
            {
                int count = TownChallengePolicy.Opponents(offers[slot]);
                for (int member = 0; member < count; ++member)
                {
                    p.Opponents[slot].Add(h.None);
                    p.Names[slot].Add(TownArena.StoredPortraitName(offers[slot], member));
                }
            }
            else if (offers[slot].Generated &&
                TownChallengePolicy.OfferRarity(offers[slot], offers[slot].Division) == 3 &&
                TownChallengePolicy.NamedForDivision(offers[slot].Encounter, offers[slot].Division))
            {
                // A named Legendary anchor remains the intended opponent even
                // while recovering, so the card can show a faded live portrait.
                p.Opponents[slot].Add(h.None);
                p.Names[slot].Add(TownChallengePolicy.Title(offers[slot].Encounter));
            }
            else continue;
            any = true;
        }
        PlacedBuilding? found = s.TownRegistry is { } r ? h.Resolve(r) : null;
        if (!any || !h.HasWorld || found is not { } registry || registry.Town == TownKey.None) return p;
        IReadOnlyList<TId>? nearby = h.CharactersInSphere(registry.Building.Position, RegistrySearchRadius, RegistrySearchMax);
        var actors = new List<TId>();
        for (int i = 0; nearby is not null && i < nearby.Count; ++i)
        {
            TId c = nearby[i];
            if (h.IsNull(c) || !h.Valid(c) || h.CurrentTown(c) != registry.Town || actors.Contains(c)) continue;
            actors.Add(c);
        }
        // Match the booking identity, including legacy keys. An ambiguous key
        // gets no portrait, even when only one of its namesakes is fight-ready.
        for (int slot = 0; slot < 6; ++slot)
            for (int member = 0; member < p.Opponents[slot].Count; ++member)
            {
                TId match = h.None;
                string opponentId = TownChallengePolicy.OpponentIdConst(offers[slot], member);
                string intendedRole = opponentId.Length == 0 && member == 0 ? TownChallengePolicy.Role(offers[slot].Encounter) : "";
                for (int i = 0; i < actors.Count; ++i)
                    if ((opponentId.Length != 0 && MatchesOpponent(h, actors[i], opponentId)) ||
                        (opponentId.Length == 0 && intendedRole.Length != 0 && TownArenaRuntimePolicy.IdEquals(h.Role(actors[i]), intendedRole)))
                    {
                        if (!h.IsNull(match)) { match = h.None; break; }
                        match = actors[i];
                    }
                p.Opponents[slot][member] = match;
                if (!h.IsNull(match)) p.Names[slot][member] = h.Name(match);
            }
        return p;
    }

    // C++: TownArena.cpp:1032-1039
    public int GetChallengeRarity(ITownHost<TId> h, int slot)
    {
        if (slot < 0 || slot >= 5) return -1;
        return TownArena.ChallengeRarity(Challenges(h), s.Display, slot);
    }

    // C++: TownArena.cpp:1040-1048
    public bool IsUniqueLegendaryChallenge(ITownHost<TId> h, int slot)
    {
        if (slot < 0 || slot >= 5) return false;
        return TownArena.IsUniqueLegendaryChallenge(Challenges(h), s.Display, slot);
    }

    // C++: TownArena.cpp:1049-1055
    public string GetChallengeUnavailableReason(ITownHost<TId> h, int slot)
    {
        if (slot < 0 || slot >= 5) return "";
        return TownArena.ChallengeUnavailableReason(Challenges(h), s.Display, slot);
    }

    // C++: TownArena.cpp:1056-1061
    public string GetDirectCaption(ITownHost<TId> h) => TownArena.DirectCaption(s.Display, GetDivision(h));

    // C++: TownArena.cpp:1062-1072
    public string GetProgressCaption(ITownHost<TId> h) => TownArena.ProgressCaption(h.TownChallenges());

    // C++: TownArena.cpp:1150-1182
    public string GetChallengeCaption(ITownHost<TId> h, int slot, bool includeLineup = true)
    {
        if (slot < 0 || slot >= 5) return "Unavailable";
        return TownArena.ChallengeCaption(Challenges(h), s.Display, slot, includeLineup);
    }

    // C++: TownArena.cpp:1322-1324
    public bool IsChallengeSearching(int slot) => slot >= 0 && slot < 6 && !IsLineupLocked() && s.Searching[slot];

    // C++: TownArena.cpp:1325-1328
    public bool IsBestAvailableChallenge(int slot) =>
        slot >= 0 && slot < 5 && TownChallengePolicy.Resolved(s.Display.Previews[slot]) && s.BestAvailable[slot];

    // C++: TownArena.cpp:1329
    public uint GetUniqueWins(ITownHost<TId> h) => Challenges(h).UniqueWins;

    // C++: TownArena.cpp:1330
    public bool IsSkarnUnlocked(ITownHost<TId> h) => TownChallengePolicy.SkarnUnlocked(Challenges(h));

    // ---------- booking (TownArena.cpp:1183-1295, 1360-1404) ----------

    /// <summary>C++: TownArena.cpp:1183-1295 <c>BookChallenge(players, slot)</c> (TW §2.5 booking steps 1-5): the checks, the Skarn cooldown, the fresh
    /// quote equal to the shown one, the buy-in into escrow, the paid-refresh count reset, the reservation, the announced NPC bout cancelled.</summary>
    public bool BookChallenge(ITownHost<TId> h, IReadOnlyList<TId> players, int slot)
    {
        if (slot < 0 || slot >= 5 || !h.HasActiveSave)
        {
            Status(h, TownArena.Text.BookNoSave);
            return false;
        }
        if (s.ActiveChallenge >= 0 || s.BookingPending)
        {
            Status(h, TownArena.Text.BookBusy);
            return false;
        }
        TownChallengePolicy.Card card = Challenges(h);
        TownChallengePolicy.Offer offer = s.Display.Previews[slot];
        TownChallengePolicy.Card proposed = card.Clone();
        TownChallengePolicy.At(proposed, slot) = offer;
        if (!TownChallengePolicy.CanBookDivision(proposed, slot))
        {
            Status(h, TownArena.Text.BookWrongDivision); return false;
        }
        TownChallengePolicy.Offer stored = TownChallengePolicy.At(card, slot);   // read; written through At below
        if (stored.State != TownArenaPolicy.OfferState.Available || offer.Epoch != stored.Epoch || !TownChallengePolicy.Resolved(offer))
        {
            Status(h, TownArena.BookUnavailableMessage(slot, stored.State, offer, s.Display.UnavailableReasons[slot], out _));
            return false;
        }
        int required = TownChallengePolicy.Players(offer);
        double gameHours = h.HasWorld ? h.TotalHours : -1.0;
        if (TownChallengePolicy.ChallengeCooldownActive(card, offer, gameHours, PGConfig.ChallengeValues().CooldownHours))
        {
            Status(h, TownArena.SkarnCooldownMessage(PGConfig.ChallengeValues().CooldownHours, gameHours, card.SkarnLastEnd));
            return false;
        }
        if (players.Count != required)
        {
            Status(h, TownArena.SelectExactlyMessage(required));
            return false;
        }
        PlacedBuilding? registry = s.TownRegistry is { } r ? h.Resolve(r) : null;
        for (int i = 0; i < players.Count; ++i)
        {
            if (registry is not { } reg || !Ready(h, players[i], false, true, out _) || h.CurrentTown(players[i]) != reg.Town)
            {
                Status(h, TownArena.Text.BookFightersNotReady);
                return false;
            }
            for (int j = 0; j < i; ++j) if (Eq.Equals(players[i], players[j])) return false;
        }
        TownRosterSnapshot<TId> fresh = SnapshotFor(h, players);
        if (offer.Generated)
        {
            if (PlayerLineupKey(h, players) != offer.Context || offer.Division != card.Division)
            {
                s.PreviewKey = ""; s.PreviewTick = 0;
                UpdateMatchmakingPreview(h, players);
                Status(h, TownArena.Text.BookLineupChanged);
                return false;
            }
        }
        TownChallengeBuyInPolicy.Quote price = QuoteFor(h, offer, fresh, card.Division, out double ownScore, out double enemyScore);
        TownChallengeBuyInPolicy.Quote displayed = GetChallengeBuyIn(h, slot);
        if (!price.Valid() || price.BuyIn != displayed.BuyIn || price.Payout != displayed.Payout)
        {
            s.PreviewTick = 0; UpdateMatchmakingPreview(h, players);
            Status(h, TownArena.Text.BookReviewBuyIn); return false;
        }
        PayChallengeCredit(h);
        ref TownChallengeBuyInPolicy.Account account = ref h.ChallengeAccount();   // :1257, bound once as the C++'s reference
        int balance = h.WalletBalance();
        if (!TownChallengeBuyInPolicy.CanAccept(account, s.Display.BuyInTicket, price, balance))
        {
            Status(h, TownArena.ChallengeRequiresMessage(price.BuyIn, balance)); return false;
        }
        if (!ReservePlayer(h, players[0])) return false;
        s.BookedTeam.Clear();
        for (int i = 0; i < players.Count; ++i) s.BookedTeam.Add(players[i]);
        s.QueuedChallenge = slot;
        if (!TownChallengePolicy.ReserveOffer(ref TownChallengePolicy.At(card, slot), offer, ref s.Display.ChallengeReservation))
        {
            CancelBooking(h); return false;
        }
        if (!h.WalletTake(price.BuyIn))
        {
            CancelBooking(h); Status(h, TownArena.Text.BookCollectFailed); return false;
        }
        TownChallengeBuyInPolicy.Accept(ref account, ref s.Display.BuyInTicket, price, balance);
        s.PaidRefreshCountBeforeBooking = card.PaidRefreshCount;
        s.PaidRefreshResetPending = true;
        card.PaidRefreshCount = 0;
        var booked = new CombatBalanceLog.ChallengeCard();
        booked.Slot = slot; booked.Division = GetDivision(h);
        booked.Rarity = TownChallengePolicy.OfferRarity(offer, card.Division);
        booked.Encounter = offer.Encounter; booked.Format = offer.Format; booked.Mode = offer.Mode;
        booked.Players = players.Count;
        booked.Enemies = TownChallengePolicy.Opponents(offer);
        booked.OwnScore = ownScore; booked.EnemyScore = enemyScore;
        for (int i = 0; i < booked.Enemies && i < 4; ++i)
        {
            booked.Opponents[i] = TownChallengePolicy.OpponentIdConst(offer, i);
            booked.OpponentNames[i] = TownChallengePolicy.OpponentNameConst(offer, i);
            booked.OpponentRoles[i] = offer.Roles[i];
        }
        booked.Stake = price.BuyIn; booked.Payout = price.Payout;
        h.LogChallengeAccepted(booked);
        CancelUpcomingForPlayer(h);
        Status(h, TownArena.ChallengeReservedMessage(GetChallengeCaption(h, slot), price.BuyIn, price.Payout));
        return true;
    }

    // C++: TownArena.cpp:1360-1382
    public bool ReservePlayer(ITownHost<TId> h, TId player)
    {
        if (s.BookingPending)
        {
            Status(h, TownArena.Text.SlotTaken);
            return false;
        }
        PlacedBuilding? registry = s.TownRegistry is { } r ? h.Resolve(r) : null;
        if (registry is not { } reg || !ArenaIdentityLogic.IsRegistryFinished(reg.Building) ||
            !Ready(h, player, false, true, out _) || h.CurrentTown(player) != reg.Town)
        {
            Status(h, TownArena.Text.ReserveNeedsRecovered);
            return false;
        }
        s.BookedPlayer = player;
        s.BookedTeam.Clear();
        s.BookedTeam.Add(s.BookedPlayer);
        s.BookedRegistry = reg.Building.Ref;
        s.BookingPending = true;
        s.LastScheduleCheck = 0;
        Status(h, TownArena.SlotReservedMessage(h.Name(player)));
        return true;
    }

    /// <summary>C++: TownArena.cpp:1383-1404 <c>BookPlayer(player)</c>: the direct 1v1 (no buy-in, no card) against the displayed 1v1 lineup.</summary>
    public bool BookPlayer(ITownHost<TId> h, TId player)
    {
        if (!h.HasActiveSave) { Status(h, TownArena.Text.ProgressUnavailable); return false; }
        if (s.BookingPending || s.PlayerMatch) { Status(h, TownArena.Text.PlayerBookingBusy); return false; }
        TId[] players = [player];
        TownRosterSnapshot<TId> fresh = SnapshotFor(h, players);
        if (s.Display.DirectPreview.Division != GetDivision(h) || s.Display.DirectPreview.Context != PlayerLineupKey(h, players))
        {
            s.PreviewKey = ""; s.PreviewTick = 0;
            UpdateMatchmakingPreview(h, players);
            Status(h, TownArena.Text.DirectLineupChanged);
            return false;
        }
        if (!fresh.PlayersReady || !TownChallengePolicy.Resolved(s.Display.DirectPreview) ||
            PreviewUnavailableReason(h, s.Display.DirectPreview, fresh).Length != 0)
        {
            Status(h, TownArena.Text.DirectUnavailable);
            return false;
        }
        if (!ReservePlayer(h, player)) return false;
        s.DirectBooking = s.Display.DirectPreview;
        CancelUpcomingForPlayer(h);
        Status(h, TownArena.DirectReservedMessage(s.DirectBooking.Name));
        return true;
    }

    // ---------- the player match's result and abort (TownArena.cpp:1098-1149) ----------

    // C++: TownArena.cpp:1098-1137 (OnMatchResult after its NPC half)
    void PlayerMatchResult(ITownHost<TId> h, SparPodium.Snapshot result)
    {
        bool playerWon = result.Outcome == SparPodium.OutcomeKind.TeamAWins;
        SettleChallenge(h, TownArena.ChallengeOutcome(result.Outcome));
        double endedHours = h.HasWorld ? h.TotalHours : -1.0;
        if (s.ActiveChallenge >= 0)
            TownChallengePolicy.RecordSkarnChallengeEnd(h.TownChallenges(), s.Display.ChallengeReservation, endedHours);
        var history = new List<string>();
        for (int i = s.PlayerCount; i < s.FighterCount; ++i)
        {
            string id = PersistentId(h, s.Fighters[i]);
            if (id.Length != 0) history.Add(id);
        }
        TownMatchmakingPolicy.RecordHistory(h.TownChallenges().PlayerHistory, history);
        // Town challenges win with Team A. Stops, draws and Team B victories
        // never grant unique-fighter progress.
        if (!playerWon) return;
        var live = new List<string?>();
        for (int i = s.PlayerCount; i < s.FighterCount; ++i)
        {
            TId opponent = s.Fighters[i];
            live.Add(h.IsNull(opponent) || !h.Valid(opponent) ? null : h.Role(opponent));
        }
        // Kept C++ bug (docs/divergences.md "TownArena challenge cards"): Skarn is excluded from the defeated bits, so his win is never recorded.
        uint defeated = TownArena.DefeatedUniques(s.Display.ChallengeReservation, live);
        TownChallengePolicy.Card card = h.TownChallenges();
        TownChallengePolicy.RecordVictory(card, s.ActiveChallenge >= 0, true, defeated);
        h.Debug(TownArena.ProgressionLog(GetProgressCaption(h)));
    }

    // C++: TownArena.cpp:1145-1148 (OnMatchAborted after its NPC half)
    void PlayerMatchAborted(ITownHost<TId> h)
    {
        double endedHours = h.HasWorld ? h.TotalHours : -1.0;
        if (s.ActiveChallenge >= 0)
            TownChallengePolicy.RecordSkarnChallengeEnd(h.TownChallenges(), s.Display.ChallengeReservation, endedHours);
    }
}

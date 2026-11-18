using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Task 7.3 (NPC-vs-NPC bouts, TownArena.cpp:136-150, 433-465, 591-697, 1074-1097, 1138-1145, 1464-1779, 1890-2066; TW §2.2 steps 1-13): planning
// the next ambient bout, cancelling it, beginning it (BeginEvent's ambient path), the fight, the result, the aftercare (until M8 task 8.4 an
// immediate Finish: the aftercare / corpse-cleanup placeholders answer as a world with nothing to treat) and the saved bout's restore. The player
// halves of BeginEvent (here), OnMatchResult and OnMatchAborted (TownArenaLogic.Cards.cs) are task 7.5's.
// Every M8 subsystem (bookie, announcer, spectators, corpse cleanup, aftercare, limb shop) and the controlled diagnostic run is called where the C++
// calls it, through its placeholder on the host.

public sealed partial class TownArenaLogic<TId>
{
    /// <summary>C++ <c>getObjectsWithinSphere(..., arena-&gt;getPosition(), 2500.0f, CHARACTER, 512, NULL)</c> (TownArena.cpp:1523-1524, :1536).</summary>
    public const float BoutSearchRadius = 2500.0f;
    /// <summary>C++ <c>doctors.size() &lt; 10</c> (TownArena.cpp:1567): at most ten medics are gathered.</summary>
    public const int MaxDoctors = 10;
    /// <summary>C++ <c>hours - g_restoreObservedHours &gt;= 1.0</c> (TownArena.cpp:1935): how long a restored bout waits in Scratch.</summary>
    public const double RestoreWaitHours = 1.0;

    // ---------- identities (TownArena.cpp:136-150) ----------

    // C++: TownArena.cpp:136-140
    string LegacyOpponentId(ITownHost<TId> h, TId c) =>
        h.IsNull(c) ? "" : TownChallengePolicy.OpponentKey(h.CharacterUid(c), h.Role(c), h.Name(c));

    // C++: TownArena.cpp:141-145
    string PersistentId(ITownHost<TId> h, TId c)
    {
        string id = TownMatchmakingRuntimeLogic.Identity(h, c);
        return id.Length == 0 ? LegacyOpponentId(h, c) : id;
    }

    // ---------- the NPC preparation (TownArena.cpp:433-465) ----------

    // C++: TownArena.cpp:433-440
    public bool IsNpcReadyForPreparation(ITownHost<TId> h, TId character)
    {
        bool previousQuiet = s.QuietDiscovery;
        s.QuietDiscovery = true;
        try
        {
            return !h.IsNull(character) && h.Valid(character) && !h.IsCarried(character) &&
                !h.IsCarrying(character) && Ready(h, character, false, false, out _);
        }
        finally { s.QuietDiscovery = previousQuiet; }   // port only: a host throw never leaves discovery quiet
    }

    // C++: TownArena.cpp:441-446
    public void QuarantineIngressFighter(ITownHost<TId> h, TId character)
    {
        if (h.IsNull(character) || !h.Valid(character) || IngressQuarantined(character)) return;
        s.IngressQuarantine.Add(character);
        h.Debug(TownArena.QuarantineLog(h.Name(character), TownMatchmakingRuntimeLogic.Identity(h, character)));
    }

    // C++: TownArena.cpp:447-461 (struct NpcPreparation)
    struct NpcPreparation(TownArenaLogic<TId> logic, ITownHost<TId> h) : TownArenaRuntimePolicy.IPreparation
    {
        public readonly bool Ready() => h.BookiePreparationValid();
        public readonly void Cancel()
        {
            bool wagerPending = h.BookieHasPendingWager();
            string ingressStatus = h.IngressGetStatus();
            bool ingressPending = h.IngressPending();   // read before the cancel, as the C++'s formationFailure
            h.IngressCancel();
            h.DiagnosticRetryPending();
            logic.Finish(h, TownArena.PreparationCancelMessage(wagerPending, ingressStatus, ingressPending));
        }
    }

    // C++: TownArena.cpp:462-465
    public bool ValidatePreparation(ITownHost<TId> h)
    {
        var preparation = new NpcPreparation(this, h);
        return TownArenaRuntimePolicy.GuardPreparation(s.Phase == TownPhase.Preparing && !s.PlayerMatch, ref preparation);
    }

    // ---------- the planned bout (TownArena.cpp:591-697) ----------

    // C++: TownArena.cpp:591
    public bool HasPlannedNpcBout() => s.PlannedNpc.Valid;
    // C++: TownArena.cpp:592
    public double GetPlannedNpcStartHours() => s.PlannedNpc.Valid ? s.NextNpcHours : 0.0;

    /// <summary>C++: TownArena.cpp:593-607. <paramref name="output"/> is the save's snapshot field (the C++ <c>out</c>: assigned into).</summary>
    public bool CapturePlannedNpcBout(ITownHost<TId> h, ArenaPersistence.PlannedNpcBout output)
    {
        output.AssignFrom(new ArenaPersistence.PlannedNpcBout());
        if (!s.PlannedNpc.Valid) return !h.BookieHasPendingWager();
        if (s.RestoredNpcPending) { output.AssignFrom(s.RestoredNpc); return true; }
        PlacedBuilding? registry = s.PlannedRegistry is { } r && h.HasWorld ? h.Resolve(r) : null;
        string uid = registry is { } b ? h.BuildingUid(b.Building.Ref) : "";
        if (uid.Length == 0) return false;
        output.Active = true;
        output.RegistryId = uid;
        output.StartHours = s.NextNpcHours;
        output.ScoreA = s.PlannedNpc.ScoreA; output.ScoreB = s.PlannedNpc.ScoreB;
        output.TeamA.Clear(); output.TeamA.AddRange(s.PlannedNpc.A);
        output.TeamB.Clear(); output.TeamB.AddRange(s.PlannedNpc.B);
        h.BookieCapturePlannedNpcBout(output);
        return true;
    }

    // C++: TownArena.cpp:608-619
    public void RestorePlannedNpcBout(ITownHost<TId> h, ArenaPersistence.PlannedNpcBout saved)
    {
        if (!saved.Active) return;
        s.RestoredNpc = saved.Clone();
        s.RestoredNpcPending = true;
        s.RestoreObservedHours = 0.0;
        s.PlannedNpc = new TownMatchmakingPolicy.Match();
        s.PlannedNpc.Valid = true;
        s.PlannedNpc.A = new List<string>(saved.TeamA); s.PlannedNpc.B = new List<string>(saved.TeamB);
        s.PlannedNpc.ScoreA = saved.ScoreA; s.PlannedNpc.ScoreB = saved.ScoreB;
        s.NextNpcHours = saved.StartHours;
        Status(h, TownArena.Text.SavedLineupWaiting);
    }

    // CancelPlannedNpcBout (C++ TownArena.cpp:620-632)
    public void CancelPlannedNpcBout(ITownHost<TId> h, string reason)
    {
        if (!s.PlannedNpc.Valid) return;
        if (s.RestoredNpcPending)
        {
            h.BookieRefundUnrestoredWager(s.RestoredNpc);
            s.RestoredNpcPending = false;
            s.RestoredNpc = new ArenaPersistence.PlannedNpcBout();
            s.RestoreObservedHours = 0.0;
        }
        s.PlannedNpc = new TownMatchmakingPolicy.Match();
        s.PlannedRegistry = null; s.PlannedFighters.Clear();
        s.NextNpcHours = 0.0;
        s.LastScheduleCheck = h.NowMs;
        h.BookieCancelNpcBout(reason);
        h.DiagnosticCancelPending();
    }

    /// <summary>The fighter's town is not the registry's (the C++ <c>getCurrentTownLocation() != registry-&gt;getTown()</c>: a pointer compare,
    /// so two NULLs are equal).</summary>
    bool OutsideTown(ITownHost<TId> h, TId fighter, long registryTown) => h.CurrentTown(fighter) != registryTown;

    // C++: TownArena.cpp:635-646
    bool PlannedNpcReady(ITownHost<TId> h)
    {
        if (s.RestoredNpcPending) return false;
        PlacedBuilding? registry = s.PlannedRegistry is { } r ? h.Resolve(r) : null;
        if (registry is not { } b || !ArenaIdentityLogic.IsRegistryFinished(b.Building)) return false;
        for (int i = 0; i < s.PlannedFighters.Count; ++i)
        {
            TId fighter = s.PlannedFighters[i];
            if (h.IsNull(fighter) || !h.Valid(fighter) || OutsideTown(h, fighter, b.Town) ||
                !IsNpcReadyForPreparation(h, fighter) || h.AftercareManaged(fighter) ||
                h.LimbShopManaged(fighter)) return false;
        }
        return h.BookiePreparationValid();
    }

    /// <summary>C++: TownArena.cpp:647-689 <c>PlanNpcBout</c> (TW §2.2 steps 4-6). Port only (task 7.2, census): nothing is planned before
    /// <see cref="RosterSettled"/> (the town fills about 10 s after the player arrives; a roster read earlier would plan on a half-empty town).</summary>
    bool PlanNpcBout(ITownHost<TId> h)
    {
        if (s.PlannedNpc.Valid) return true;
        if (!h.HasWorld || !h.HasActiveSave || s.BookingPending || s.SchedulePaused ||
            (s.Phase != TownPhase.Idle && s.Phase != TownPhase.Aftercare)) return false;
        PlacedBuilding? registry = s.TownRegistry is { } tr ? h.Resolve(tr) : null;
        if (registry is not { } reg || !ArenaIdentityLogic.IsRegistryFinished(reg.Building)) return false;
        if (!RosterSettled(h)) return false;   // port only (task 7.2, census)
        TownRosterSnapshot<TId> snapshot = SnapshotFor(h, []);
        // Doctors caring for the previous bout are expected back by start time.
        // Readiness and actual capacity are checked again before staging.
        int capacity = TownArena.AmbientCapacity(snapshot.Medics.Count, s.Phase == TownPhase.Aftercare, s.FighterCount);
        TownChallengePolicy.Card card = h.TownChallenges();
        TownMatchmakingPolicy.DiagnosticRequest diagnostic = h.DiagnosticNextRequest();
        TownMatchmakingPolicy.Match match = diagnostic.Active ? TownMatchmakingPolicy.SelectAmbientDiagnostic(
            snapshot.Npcs, ref card.AmbientSeed, card.AmbientHistory, diagnostic, capacity) : TownMatchmakingPolicy.SelectAmbient(
            snapshot.Npcs, ref card.AmbientSeed, card.AmbientHistory, capacity);
        if (!match.Valid) return false;
        var fighters = new List<TId>();
        var teams = new List<MatchRules.MatchTeam>();
        for (int side = 0; side < 2; ++side)
        {
            List<string> ids = side == 0 ? match.A : match.B;
            for (int i = 0; i < ids.Count; ++i)
            {
                if (!TownMatchmakingRuntimeLogic.Resolve(h, snapshot, ids[i], out TId fighter)) return false;
                fighters.Add(fighter);
                teams.Add(side == 0 ? MatchRules.MatchTeam.TeamA : MatchRules.MatchTeam.TeamB);
            }
        }
        s.PlannedNpc = match;
        s.PlannedRegistry = reg.Building.Ref;
        s.PlannedFighters.Clear();
        for (int i = 0; i < fighters.Count; ++i) s.PlannedFighters.Add(fighters[i]);
        // Lead time comes from BalanceTuning; defaults reproduce the shipped
        // 1.0-2.0 h announcement window exactly.
        s.NextNpcHours = TownArena.NextNpcStartHours(card, h.TotalHours);
        if (match.Diagnostic) h.DiagnosticAssign(match, h.TotalHours);
        h.BookieBeginNpcBout([.. fighters], [.. teams], fighters.Count, match.ScoreA, match.ScoreB);
        Status(h, TownArena.Text.NpcLineupAnnounced);
        return true;
    }

    // ---------- the debug menu's trial (TownArena.cpp:1766-1779) ----------

    // C++: TownArena.cpp:1766-1779
    public bool BeginTrial(ITownHost<TId> h)
    {
        if (s.BookingPending)
        {
            Status(h, TownArena.Text.PlayerHoldsSlot);
            return false;
        }
        if (s.Phase == TownPhase.Preparing || s.Phase == TownPhase.Fighting)
        {
            Status(h, TownArena.Text.BoutUnderway); return false;
        }
        bool planned = PlanNpcBout(h);
        if (!planned) Status(h, TownArena.Text.WaitingToAnnounce);
        return planned;
    }

    // ---------- the restored bout (TownArena.cpp:1890-1939) ----------

    /// <summary>C++: TownArena.cpp:1890-1939, the wait for a restored bout's registry and fighters inside Tick. False where the C++ returns from
    /// Tick: paused, before the 5 s clock, or the bout still pending (:1939 <c>if (g_restoredNpcPending) return;</c>).</summary>
    bool TickRestoredNpc(ITownHost<TId> h, uint now)
    {
        if (h.IsPaused) return false;
        if (s.LastScheduleCheck != 0 && now - s.LastScheduleCheck < ScheduleIntervalMs) return false;
        s.LastScheduleCheck = now;
        PlacedBuilding? registry = s.TownRegistry is { } tr ? h.Resolve(tr) : null;
        string id = registry is { } r0 ? h.BuildingUid(r0.Building.Ref) : "";
        if (id.Length == 0 || id != s.RestoredNpc.RegistryId)
        {
            DiscoverTownRegistry(h);
            registry = s.TownRegistry is { } tr2 ? h.Resolve(tr2) : null;
            id = registry is { } r1 ? h.BuildingUid(r1.Building.Ref) : "";
        }
        if (id.Length != 0 && id == s.RestoredNpc.RegistryId && registry is { } reg &&
            ArenaIdentityLogic.IsRegistryFinished(reg.Building) && HasPlayerInTown(h, reg))
        {
            double hours = h.TotalHours;
            if (s.RestoreObservedHours == 0) s.RestoreObservedHours = hours;
            TownRosterSnapshot<TId> snapshot = SnapshotFor(h, []);
            var fighters = new List<TId>();
            var teams = new List<MatchRules.MatchTeam>();
            bool ready = true;
            for (int side = 0; side < 2; ++side)
            {
                List<string> ids = side != 0 ? s.RestoredNpc.TeamB : s.RestoredNpc.TeamA;
                for (int i = 0; i < ids.Count; ++i)
                {
                    TownMatchmakingRuntimeLogic.Resolve(h, snapshot, ids[i], out TId fighter);   // the host's None when unresolved
                    if (h.IsNull(fighter) || !IsNpcReadyForPreparation(h, fighter) ||
                        OutsideTown(h, fighter, reg.Town) ||
                        h.AftercareManaged(fighter) || h.LimbShopManaged(fighter)) ready = false;
                    fighters.Add(fighter);
                    teams.Add(side != 0 ? MatchRules.MatchTeam.TeamB : MatchRules.MatchTeam.TeamA);
                }
            }
            if (ready)
            {
                h.BookieBeginNpcBout([.. fighters], [.. teams], fighters.Count, s.RestoredNpc.ScoreA, s.RestoredNpc.ScoreB);
                if (h.BookieRestorePlannedNpcBout(s.RestoredNpc))
                {
                    s.PlannedRegistry = reg.Building.Ref;
                    s.PlannedFighters.Clear();
                    for (int i = 0; i < fighters.Count; ++i) s.PlannedFighters.Add(fighters[i]);
                    s.RestoredNpcPending = false;
                    s.RestoredNpc = new ArenaPersistence.PlannedNpcBout();
                    s.RestoreObservedHours = 0.0;
                    Status(h, TownArena.Text.SavedLineupRestored);
                }
                else CancelPlannedNpcBout(h, TownArena.Text.SavedLineupNoMarket);
            }
            else if (hours - s.RestoreObservedHours >= RestoreWaitHours)
                CancelPlannedNpcBout(h, TownArena.Text.SavedLineupExpired);
        }
        return !s.RestoredNpcPending;
    }

    // ---------- the next event (TownArena.cpp:1968-1999) ----------

    /// <summary>C++: TownArena.cpp:1968-1999, the rest of Tick's idle branch: <c>TownArenaPolicy::AmbientNext</c>, the player or ambient
    /// registry, the booked player's readiness, <c>BeginEvent</c> and the booking's hand-off.</summary>
    void BeginNextEvent(ITownHost<TId> h, bool registryReady, ArenaBuildingRef? ambientRegistry)
    {
        TownArenaPolicy.EventKind next = TownArenaPolicy.AmbientNext(
            s.SchedulePaused, registryReady, h.ArenaOperationBusy(), s.BookingPending,
            h.TotalHours, s.NextNpcHours);
        if (next == TownArenaPolicy.EventKind.None) return;
        ArenaBuildingRef? registry = next == TownArenaPolicy.EventKind.Player ? s.BookedRegistry : ambientRegistry;
        PlacedBuilding? resolved = registry is { } held ? h.Resolve(held) : null;
        // Never silently move a reservation to a different Registry/town.
        if (resolved is not { } placed)
        {
            if (next == TownArenaPolicy.EventKind.Player)
                Status(h, TownArena.Text.QueueReturnToRegistry);
            return;
        }
        TId player = next == TownArenaPolicy.EventKind.Player ? s.BookedPlayer : h.None;
        s.QuietDiscovery = next == TownArenaPolicy.EventKind.Npc;
        if (next == TownArenaPolicy.EventKind.Player &&
            (!Ready(h, player, false, true, out _) || OutsideTown(h, player, placed.Town)))
        {
            Status(h, TownArena.Text.BookingWaitingFighter);
            s.QuietDiscovery = false;
            return;
        }
        bool started;
        try { started = BeginEvent(h, player, placed); }
        finally { s.QuietDiscovery = false; }   // port only: a host throw never leaves discovery quiet
        if (started && next == TownArenaPolicy.EventKind.Player)
        {
            s.QueuedChallenge = -1;
            s.BookingPending = false;
            s.BookedPlayer = h.None;
            s.BookedTeam.Clear();
            s.BookedRegistry = null;
        }
    }

    // ---------- BeginEvent (TownArena.cpp:1485-1764) ----------

    /// <summary>C++: TownArena.cpp:1485-1764 <c>BeginEvent(player, requestedRegistry)</c>: the ambient (NPC v NPC) bout (task 7.3) or, with a
    /// booked <paramref name="player"/> (task 7.5), the player event: the booked team as Team A against the reserved card's (or the direct 1v1's)
    /// opponents as Team B, in the offer's mode, with the challenge's Marks multiplier and unique bonus. <paramref name="requested"/> = the
    /// requested registry resolved now (null = the C++ NULL).</summary>
    bool BeginEvent(ITownHost<TId> h, TId player, PlacedBuilding? requested)
    {
        if (!h.HasActiveSave) { Status(h, TownArena.Text.ProgressUnavailable); return false; }
        if (!h.HasWorld || h.ArenaOperationBusy())
        {
            Status(h, TownArena.Text.ArenaBusy);
            return false;
        }
        if (requested is not { } registry || !ArenaIdentityLogic.IsRegistryFinished(registry.Building) || registry.Town == TownKey.None)
        {
            Status(h, TownArena.Text.NeedRegistry);
            return false;
        }
        var buildings = h.BuildingsInSphere(registry.Building.Position, RegistrySearchRadius, RegistrySearchMax);
        PlacedBuilding? arenaFound = null;
        float nearest = 1.0e30f;
        for (int i = 0; buildings is not null && i < buildings.Count; ++i)
        {
            PlacedBuilding b = buildings[i];
            if (!TownArenaRuntimePolicy.IsNewArena(b.Building.RecordId)) continue;
            if (b.Town != registry.Town) continue;
            if (!b.Building.Finished) continue;
            float distance = Vector3.DistanceSquared(b.Building.Position, registry.Building.Position);
            if (distance < nearest) { arenaFound = b; nearest = distance; }
        }
        if (arenaFound is not { } arena)
        {
            Status(h, TownArena.Text.NoCrucible);
            return false;
        }

        IReadOnlyList<TId>? nearby = h.CharactersInSphere(arena.Building.Position, BoutSearchRadius, RegistrySearchMax);
        // Include the same search area that resolved the advertised card NPC.
        bool player_ = !h.IsNull(player);
        bool challenge = player_; // Direct duels also have a frozen opposing lineup.
        TownChallengePolicy.Offer? queuedOffer = !player_ ? null : s.QueuedChallenge >= 0 ?
            s.Display.ChallengeReservation : s.DirectBooking;
        TownChallengePolicy.Card reservedCard = h.TownChallenges().Clone();
        if (player_ && s.QueuedChallenge >= 0)
            TownChallengePolicy.At(reservedCard, s.QueuedChallenge) = queuedOffer!.Value;
        if (player_ && (!TownChallengePolicy.Resolved(queuedOffer!.Value) ||
            (s.QueuedChallenge >= 0 && !TownChallengePolicy.CanBookDivision(reservedCard, s.QueuedChallenge))))
        {
            Status(h, TownArena.Text.ReservationInvalid); return false;
        }
        IReadOnlyList<TId>? registryNearby = h.CharactersInSphere(registry.Building.Position, BoutSearchRadius, RegistrySearchMax);
        // :1537-1573: the fighters and doctors around the arena and the registry
        var fighters = new List<TId>();
        var challengeMatches = new List<TId>();
        string challengeBlocker = "";
        var doctors = new List<TId>();
        CollectNearby(h, nearby, arena.Town, fighters, doctors, challenge, queuedOffer, challengeMatches, ref challengeBlocker);
        CollectNearby(h, registryNearby, arena.Town, fighters, doctors, challenge, queuedOffer, challengeMatches, ref challengeBlocker);

        int npcTeamSize = 0;
        int neededEnemies = challenge ? TownChallengePolicy.Opponents(queuedOffer!.Value) : player_ ? 1 : 0;
        if (challenge)
        {
            fighters.Clear();
            TownChallengePolicy.Offer offer = queuedOffer!.Value;
            for (int enemy = 0; enemy < neededEnemies; ++enemy)
            {
                string id = TownChallengePolicy.OpponentIdConst(offer, enemy);
                string name = TownChallengePolicy.OpponentNameConst(offer, enemy);
                int matches = 0;
                TId candidate = h.None;
                for (int j = 0; j < challengeMatches.Count; ++j)
                    if (MatchesOpponent(h, challengeMatches[j], id)) { ++matches; candidate = challengeMatches[j]; }
                if (matches != 1)
                {
                    Status(h, TownArena.ChallengeWaitingMessage(name, matches));
                    return false;
                }
                if (!Ready(h, candidate, false, false, out challengeBlocker))
                {
                    Status(h, TownArena.ChallengeWaitingMessage(name, challengeBlocker));
                    return false;
                }
                if (h.AftercareManaged(candidate) || h.LimbShopManaged(candidate))
                {
                    Status(h, TownArena.Text.ReservedRecovering); return false;
                }
                fighters.Add(candidate);
            }
        }
        if (!challenge && !player_)
        {
            TownRosterSnapshot<TId> snapshot = SnapshotFor(h, []);
            // Never reroll or reprice an advertised lineup at staging time.
            TownMatchmakingPolicy.Match ambientMatch = s.PlannedNpc;
            if (!ambientMatch.Valid)
            {
                Status(h, TownArena.BoutWaitingMessage(snapshot.Availability, snapshot.Npcs.Count, snapshot.Medics.Count));
                return false;
            }
            if (s.PlannedRegistry != registry.Building.Ref || !PlannedNpcReady(h))
            {
                CancelPlannedNpcBout(h, TownArena.Text.AnnouncedUnavailable);
                return false;
            }
            for (int side = 0; side < 2; ++side)
            {
                List<string> ids = side == 0 ? ambientMatch.A : ambientMatch.B;
                for (int i = 0; i < ids.Count; ++i)
                {
                    if (!EligibleOpponent(snapshot, ids[i]))
                    {
                        CancelPlannedNpcBout(h, TownArena.Text.AnnouncedIneligible);
                        return false;
                    }
                }
            }
            doctors = new List<TId>(snapshot.Medics);
            fighters.Clear();
            npcTeamSize = ambientMatch.A.Count;
            for (int i = 0; i < ambientMatch.A.Count; ++i) fighters.Add(ResolveOrNull(h, snapshot, ambientMatch.A[i]));
            for (int i = 0; i < ambientMatch.B.Count; ++i) fighters.Add(ResolveOrNull(h, snapshot, ambientMatch.B[i]));
            neededEnemies = fighters.Count;
            h.Debug(TownArena.AmbientMatchupLog(ambientMatch.A.Count, ambientMatch.B.Count, ambientMatch.ScoreA, ambientMatch.ScoreB));
        }
        if (fighters.Count < neededEnemies)
        {
            Status(h, TownArena.Text.WaitingOpponents);
            return false;
        }
        if (doctors.Count == 0)
        {
            Status(h, TownArena.Text.NoMedic);
            return false;
        }
        var pair = new List<TId>();
        var teams = new List<MatchRules.MatchTeam>();
        if (player_)
        {
            for (int i = 0; i < s.BookedTeam.Count; ++i)
            {
                TId member = s.BookedTeam[i];
                if (!Ready(h, member, false, true, out _) || OutsideTown(h, member, registry.Town))
                {
                    Status(h, TownArena.Text.WaitingSquad);
                    return false;
                }
                pair.Add(member);
                teams.Add(MatchRules.MatchTeam.TeamA);
            }
            if (pair.Count == 0) return false;
        }
        int playerCount = pair.Count;
        for (int i = 0; i < neededEnemies; ++i)
        {
            if (h.IsNull(fighters[i]) || !h.Valid(fighters[i])) return false;
            pair.Add(fighters[i]);
            teams.Add(!player_ && i < npcTeamSize ? MatchRules.MatchTeam.TeamA : MatchRules.MatchTeam.TeamB);
        }
        MatchRules.MatchMode matchMode = player_ && s.QueuedChallenge >= 0 ?
            TownArena.MatchModeFor(s.Display.ChallengeReservation) : MatchRules.MatchMode.TeamAvB;
        if (pair.Count > TownArenaState<TId>.MaxFighters) return false;
        if (!h.AftercareControlAvailable())
        {
            Status(h, TownArena.Text.MedicControlUnavailable);
            return false;
        }
        int requiredMedics = TownAftercarePolicy.RequiredMedics(pair.Count);
        if (doctors.Count < requiredMedics)
        {
            Status(h, TownArena.MedicsWaitingMessage(doctors.Count, requiredMedics));
            return false;
        }
        doctors.RemoveRange(requiredMedics, doctors.Count - requiredMedics);
        s.FighterCount = pair.Count;
        s.PlayerCount = playerCount;
        for (int i = 0; i < s.FighterCount; ++i)
        {
            s.Fighters[i] = pair[i];
            s.FighterTeams[i] = teams[i];
        }
        Vector3 towardRegistry = registry.Building.Position - arena.Building.Position;
        TownArenaRuntimePolicy.MedicStandbyOffset(towardRegistry.X, towardRegistry.Z, out float standbyX, out float standbyZ);
        // Hand the ring guard the arena it is containing fighters in. The arena building is the ring's centre, resolved here at the moment the
        // bout is armed, so the guard never has to re-derive the site on a tick.
        h.RingGuardSetArena(arena.Building.Ref);

        TId[] pairArray = [.. pair];
        MatchRules.MatchTeam[] teamArray = [.. teams];
        h.AftercareBegin(doctors, pairArray, pairArray.Length, arena.Building.Ref, arena.Building.Position + new Vector3(standbyX, 0.0f, standbyZ));
        h.CorpseCleanupBegin(arena.Building.Ref, pairArray, pairArray.Length);
        s.PreviousProtocol = h.GetProtocol();
        h.SetProtocol(ArenaMedicalPolicy.Protocol.RingsideAid);
        s.Phase = TownPhase.Preparing;
        s.ResultRecorded = false;
        s.ActiveChallenge = player_ ? s.QueuedChallenge : -1;
        s.PlayerMatch = player_;
        // :1725-1733 (Core TownArena.ActiveMarks); GetChallengeRarity is read only for a card, as the C++'s conditional
        TownArena.ActiveMarks(player_, GetDivision(h), s.ActiveChallenge,
            s.ActiveChallenge >= 0 ? GetChallengeRarity(h, s.ActiveChallenge) : 0, s.Display.ChallengeReservation.Encounter,
            out s.ActiveMarksMultiplier, out s.ActiveMarksBonus);
        var opponentRoles = new string[TownArenaState<TId>.MaxFighters];
        int opponentCount = 0;
        for (int i = playerCount; i < s.FighterCount && opponentCount < TownArenaState<TId>.MaxFighters; ++i)
            opponentRoles[opponentCount++] = h.Role(pair[i]);
        h.AnnouncerBegin(arena.Building.Ref, s.PlayerMatch, s.ActiveChallenge, opponentRoles, opponentCount, pairArray, s.FighterCount);
        if (!player_)
        {
            s.PlannedNpc = new TownMatchmakingPolicy.Match();
            s.PlannedRegistry = null; s.PlannedFighters.Clear();
            s.NextNpcHours = 0.0;
            h.BookieNpcAssembling();
        }
        h.SpectatorsBegin(arena.Building.Ref);
        ArenaLocationMode previousMode = h.IngressGetLocationMode();
        h.IngressSetLocationMode(ArenaLocationMode.Arena);
        bool started;
        try { started = h.IngressBegin(matchMode, pairArray, teamArray, s.FighterCount, arena.Building.Ref); }
        finally { h.IngressSetLocationMode(previousMode); }   // port only: a throw never leaves the window's location mode changed
        if (!started)
        {
            Finish(h, TownArena.StagingFailedMessage(h.IngressGetStatus()));
            return false;
        }
        s.ActiveRegistry = registry.Building.Ref;
        Status(h, TownArena.AssemblingMessage(matchMode, player_, playerCount, npcTeamSize, neededEnemies));
        return true;
    }

    /// <summary><c>TownMatchmakingRuntime::Resolve(snapshot, id)</c>: the character, or the C++ NULL (<c>h.None</c>).</summary>
    static TId ResolveOrNull(ITownHost<TId> h, TownRosterSnapshot<TId> snapshot, string id)
    {
        TownMatchmakingRuntimeLogic.Resolve(h, snapshot, id, out TId fighter);   // false leaves the host's None
        return fighter;
    }

    /// <summary>C++: TownArena.cpp:1542-1573, one of the two character lists of BeginEvent's loop (the arena's sphere, then the registry's): in the
    /// arena's town, the regular fighters (an ambient bout) or the reserved offer's opponents (<paramref name="queuedOffer"/>, a player booking: each
    /// unique character kept in <paramref name="challengeMatches"/>, the last readiness reason in <paramref name="challengeBlocker"/>) and medics that
    /// are Ready, without duplicates; at most ten medics, none carrying or carried.</summary>
    void CollectNearby(ITownHost<TId> h, IReadOnlyList<TId>? characters, long arenaTown, List<TId> fighters, List<TId> doctors, bool challenge,
        TownChallengePolicy.Offer? queuedOffer, List<TId> challengeMatches, ref string challengeBlocker)
    {
        for (int i = 0; characters is not null && i < characters.Count; ++i)
        {
            TId c = characters[i];
            if (h.IsNull(c) || !h.Valid(c)) continue;
            if (OutsideTown(h, c, arenaTown)) continue;
            bool fighter = !challenge && TownArenaRuntimePolicy.IsRegularFighter(h.Role(c));
            if (queuedOffer is { } q)
                for (int enemy = 0; enemy < TownChallengePolicy.Opponents(q); ++enemy)
                    if (MatchesOpponent(h, c, TownChallengePolicy.OpponentIdConst(q, enemy))) fighter = true;
            if (challenge && fighter && !challengeMatches.Contains(c)) challengeMatches.Add(c);
            bool doctor = TownArenaRuntimePolicy.IsMedic(h.Role(c));
            if (!fighter && !doctor) continue;
            bool ready = Ready(h, c, doctor, false, out string reason);
            if (challenge && fighter) challengeBlocker = reason;   // the C++ passes &challengeBlocker only for a challenge opponent
            if (!ready) continue;
            if (fighter && !fighters.Contains(c)) fighters.Add(c);
            if (doctor && !h.IsCarrying(c) && !h.IsCarried(c) && !doctors.Contains(c) && doctors.Count < MaxDoctors) doctors.Add(c);
        }
    }

    // ---------- the fight (TownArena.cpp:1464-1483, 1074-1097, 1138-1145) ----------

    // C++: TownArena.cpp:1464-1483
    public void OnMatchStarted(ITownHost<TId> h)
    {
        if (s.Phase == TownPhase.Preparing && OwnsMatch(h))
        {
            h.TownRulesBoutStarted();   // port only (task 7.4): the town rules record the bout's TEMP_ENEMY state under the C++'s own test
            // The preview refresh replaces only unavailable opponents, keeping
            // unrelated advertised cards stable when an ambient bout starts.
            s.PreviewTick = 0;
            if (s.ActiveChallenge >= 0)
                TownArenaPolicy.BeginOffer(ref TownChallengePolicy.At(h.TownChallenges(), s.ActiveChallenge).State);
            s.Phase = TownPhase.Fighting;
            s.SequentialCare = h.SparMode() == MatchRules.MatchMode.Teams1v1;
            s.AftercareElapsed = 0.0f; s.AftercareTick = h.NowMs; s.LastAid = 0;
            bool wagerPending = h.BookieHasPendingWager();
            h.BookieCombatStarted();
            if (wagerPending && h.HasWorld)
                h.ShowPlayerMessage(TownArena.Text.WagerStarting);
            h.AftercareStandby();
            Status(h, TownArena.UnderwayMessage(s.PlayerMatch));
        }
    }

    // C++: TownArena.cpp:1074-1137 (the player half, :1098-1137, in TownArenaLogic.Cards.cs)
    public void OnMatchResult(ITownHost<TId> h, SparPodium.Snapshot result)
    {
        if (s.ResultRecorded || !OwnsMatch(h)) return;
        h.SpectatorsReactToResult(result);
        h.SpectatorsRelease();
        s.ResultRecorded = true;
        for (int i = 0; i < s.FighterCount; ++i)
        {
            if (s.SequentialCare && h.SparIsEliminated(s.Fighters[i])) continue;
            if ((result.Outcome == SparPodium.OutcomeKind.TeamAWins && s.FighterTeams[i] == MatchRules.MatchTeam.TeamA) ||
                (result.Outcome == SparPodium.OutcomeKind.TeamBWins && s.FighterTeams[i] == MatchRules.MatchTeam.TeamB))
                h.AftercareMarkWinner(s.Fighters[i]);
        }
        if (!s.PlayerMatch)
        {
            var history = new List<string>();
            for (int i = 0; i < s.FighterCount; ++i)
            {
                string id = PersistentId(h, s.Fighters[i]);
                if (id.Length != 0) history.Add(id);
            }
            TownMatchmakingPolicy.RecordHistory(h.TownChallenges().AmbientHistory, history);
            h.BookieSettle(result.Outcome == SparPodium.OutcomeKind.TeamAWins ? 0 :
                result.Outcome == SparPodium.OutcomeKind.TeamBWins ? 1 : -1);
            h.DiagnosticCompletePending(result, h.HasWorld ? h.TotalHours : 0);
            return;
        }
        PlayerMatchResult(h, result);   // :1098-1137 (TownArenaLogic.Cards.cs)
    }

    // C++: TownArena.cpp:1138-1149 (the player half, :1145-1148, in TownArenaLogic.Cards.cs)
    public void OnMatchAborted(ITownHost<TId> h)
    {
        if (!s.PlayerMatch)
        {
            h.DiagnosticAbortPending();
            return;
        }
        PlayerMatchAborted(h);   // :1145-1148 (TownArenaLogic.Cards.cs); the refund is Finish's SettleChallenge(-1)
    }

    // ---------- Tick while an event runs (TownArena.cpp:2002-2066) ----------

    /// <summary>C++: TownArena.cpp:2002-2066, Tick while an event runs (Preparing, Fighting, Aftercare). Since task 8.4b the aftercare treats and
    /// carries the patients (up to 300 s after the fight) and the bout planned during it (:2045-2050) is reachable; the corpse cleanup is still a
    /// placeholder with nothing pending (task 8.6).</summary>
    void TickEvent(ITownHost<TId> h, uint now)
    {
        if (s.Phase == TownPhase.Aftercare || (s.Phase == TownPhase.Fighting && s.SequentialCare))
        {
            float dt = s.AftercareTick != 0 ? unchecked(now - s.AftercareTick) / 1000.0f : 0.0f;
            s.AftercareElapsed = TownArenaRuntimePolicy.AdvanceAftercare(
                s.AftercareElapsed, dt, h.IsPaused);
            s.AftercareTick = now;
        }
        // Assemble the medical team while fighters are preparing, not after
        // combat has already begun. Standby also observes pauses for its clock.
        if (s.Phase == TownPhase.Preparing || (s.Phase == TownPhase.Fighting && !s.SequentialCare)) h.AftercareStandby();
        if (h.IsPaused) return;
        if (s.Phase == TownPhase.Preparing)
        {
            if (!ValidatePreparation(h)) return;
            if (OwnsMatch(h))
                OnMatchStarted(h);
            else if (!h.IngressPending())
                Finish(h, TownArena.StagingEndedMessage(h.IngressGetStatus()));
            return;
        }
        if (s.Phase == TownPhase.Fighting)
        {
            if (OwnsMatch(h))
            {
                if (s.SequentialCare && TownAftercarePolicy.MedicPollDue(s.LastAid, now))
                {
                    s.LastAid = now;
                    h.AftercareTreat(s.AftercareElapsed, true);
                }
                return;
            }
            h.BookieSettle(-1);
            h.SpectatorsRelease();
            h.CorpseCleanupSeal();
            // One event-driven limb check at match end. Queued visits cannot
            // take ownership until aftercare releases the fighters.
            var participants = new TId[TownArenaState<TId>.MaxFighters];
            for (int i = 0; i < s.FighterCount; ++i)
                participants[i] = s.Fighters[i];
            h.LimbShopBegin(s.ActiveRegistry, participants, s.FighterCount);
            s.Phase = TownPhase.Aftercare;
            if (!s.SequentialCare) s.AftercareElapsed = 0.0f;
            s.AftercareTick = now;
            s.LastAid = 0;
            Status(h, TownArena.Text.FightFinished);
        }
        if (s.Phase != TownPhase.Aftercare) return;
        if (!s.BookingPending && !s.SchedulePaused &&
            (s.LastScheduleCheck == 0 || now - s.LastScheduleCheck >= ScheduleIntervalMs))
        {
            s.LastScheduleCheck = now;
            PlanNpcBout(h);
        }
        if (!TownAftercarePolicy.MedicPollDue(s.LastAid, now)) return;
        s.LastAid = now;
        if (h.AftercareTreat(s.AftercareElapsed, false))
        {
            Status(h, h.AftercareGetStatus());
            return;
        }
        h.CorpseCleanupAdmitArenaDeaths();
        if (h.CorpseCleanupHasPending())
        {
            Status(h, h.CorpseCleanupGetStatus());
            return;
        }
        string medicalStatus = h.AftercareGetStatus();
        h.AftercareRelease();
        Finish(h, TownArena.AftercareDoneMessage(medicalStatus, h.LimbShopIsActive()));
    }
}

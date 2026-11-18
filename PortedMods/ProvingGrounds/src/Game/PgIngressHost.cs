using System.Numerics;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

/// <summary>ArenaIngress's host (task 4.3b): each <see cref="IIngressHost{TId}"/> member is one PgEngine adapter method or one call of another
/// subsystem of this world (the E1 table in src/Game/ArenaIngress.cs). Bound to the world of each call and reused; sim thread, serial steps
/// only. Owned by the ArenaIngress of one world.</summary>
sealed class PgIngressHost(ArenaIngress.Services services) : IIngressHost<CharacterId>
{
    readonly ArenaIngress.Services s = services;
    World? world;
    readonly List<CharacterId> heldNpcs = [];   // NPCs Park holds with Engaged (spike S10), re-asserted by every Park; released by ReleaseParked

    internal PgIngressHost Bind(World w) { world = w; return this; }

    World W => world ?? throw new InvalidOperationException("PgIngressHost used before Bind");

    static Vec3 V(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 N(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The NPCs the staging holds now (tests and the status answer).</summary>
    internal int HeldCount => heldNpcs.Count;

    // ---------- engine reads ----------
    public CharacterId None => CharacterId.None;
    public bool IsNone(CharacterId c) => c.IsNone;
    public bool Exists(CharacterId c) => PgEngine.Exists(W, c);
    public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
    public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
    public bool TryGetPosition(CharacterId c, out Vec3 position)
    {
        if (PgEngine.Position(W, c) is { } p) { position = V(p); return true; }
        position = default;
        return false;
    }
    public float MovementSpeed(CharacterId c) => PgEngine.MovementSpeed(W, c);
    public bool IsMoving(CharacterId c) => PgEngine.MoveDestination(W, c) is not null;
    public Vec3 Destination(CharacterId c) => PgEngine.MoveDestination(W, c) is { } d ? V(d) : new Vec3(0f, 0f, 0f);
    public uint NowMs => s.Clock.NowMs;
    /// <summary>A paused world runs no serial step (World.Interface.cs:16-40): the step never sees a pause.</summary>
    public bool Paused => false;
    /// <summary>C++ SquadUtil::CollectPlayerSquad's first character (src/Game/SquadUtil.cs; task 4.2 made it skip the dead and the stale, as the
    /// C++ does).</summary>
    public CharacterId FirstSquadMember()
    {
        var squad = SquadUtil.CollectPlayerSquad(W);
        return squad.Count > 0 ? squad[0] : CharacterId.None;
    }

    /// <summary>The selection the right press saw (the ArenaInteract command's, task 4.2), used for the one BeginApproachForUI it starts; null:
    /// the world's selection.</summary>
    internal CharacterId[]? SelectionOverride;

    /// <summary>C++ SquadUtil::CollectSelectedCharacters (src/Game/SquadUtil.cs): the right press's selection when it carried one (each still
    /// existing and alive), else the world's.</summary>
    public CharacterId[] CollectSelectedCharacters()
    {
        if (SelectionOverride is { } sel)
            return [.. sel.Where(id => PgEngine.Exists(W, id) && !PgEngine.IsDead(W, id))];
        return [.. SquadUtil.CollectSelectedCharacters(W)];
    }

    // ---------- engine writes ----------
    /// <summary>C++ IssueMove (ArenaIngress.cpp:428-453): Hold and Passive off (<see cref="PgEngine.SetMoveStance"/>), then one
    /// <c>MoveOrder([id], pos)</c> for the next tick (<see cref="PgEngine.MoveTo"/>; CAP 5: one id per order for exact spots). A town bout
    /// fighter (task 7.3): its town AI is taken off first (the C++ <c>clearAllAIGoals</c>, :437: TownArena's borrow, Engaged, spike S10) and it
    /// runs (<c>setDesiredSpeedOrders(RUN)</c> + <c>setDesiredSpeed(RUN)</c>, :451-455: <see cref="PgEngine.SetRunSpeed"/>; FightStarter's
    /// remembered orders put the speed mode back when the match ends).</summary>
    public void IssueMove(CharacterId c, Vec3 location, bool townFighter)
    {
        if (townFighter) s.Town.BorrowFighter(W, c);
        PgEngine.SetMoveStance(W, c);
        PgEngine.MoveTo(W, c, N(location));
        if (townFighter) PgEngine.SetRunSpeed(W, c);
    }

    public void RemoveMoveJob(CharacterId c) => PgEngine.EndWalk(W, c);

    /// <summary>C++ ParkCharacterInPlace (ArenaIngress.cpp:599-616), CAP 5: the walk ended (removeJob(MOVE_CUS_ORDERED), <see cref="PgEngine.EndWalk"/>);
    /// a player character stands still with the Hold stance (Passive + Hold where it stands, <see cref="PgEngine.SetBenchStance"/>; the AI never
    /// holds a player character, AiSystem.cs:366); an NPC (incl. the C++'s STAND_STILL goal for a match prisoner or a town fighter) is held with
    /// <c>Engaged</c> (<see cref="PgEngine.HoldStill"/> the first time, <see cref="PgEngine.ReassertHold"/> after: a save, load or other system
    /// may clear it, so every park re-asserts it).</summary>
    public void Park(CharacterId c, bool standStillGoal)
    {
        // standStillGoal (the C++ STAND_STILL goal of a match prisoner or a town fighter, :608-614) needs nothing more: every NPC is held with
        // Engaged. A town fighter TownArena already holds (task 7.3) is left to TownArena's borrow list (re-asserted there, released when the town
        // event ends), so ReleaseParked never clears another owner's hold.
        if (PgEngine.IsPlayer(W, c))
        {
            ParkPlayerInPlace(W, c);
            return;
        }
        PgEngine.EndWalk(W, c);
        if (s.Town.HoldsNpc(c)) { s.Town.BorrowFighter(W, c); return; }
        if (heldNpcs.Contains(c)) PgEngine.ReassertHold(W, c);
        else if (PgEngine.HoldStill(W, c)) heldNpcs.Add(c);
    }

    /// <summary>The park of a PLAYER character (<see cref="Park"/>'s player half): its walk ended (<see cref="PgEngine.EndWalk"/>) and Passive +
    /// Hold on where it stands (<see cref="PgEngine.SetBenchStance"/>); no Engaged (the AI never holds a player character, AiSystem.cs:366). Also
    /// the hold PrisonerUtil puts on a match's handlers (task 6.2, ApplyParkOrders PrisonerUtil.cpp:517-527): one hold mechanism. Serial step.</summary>
    internal static void ParkPlayerInPlace(World world, CharacterId c)
    {
        PgEngine.EndWalk(world, c);
        PgEngine.SetBenchStance(world, c, true);
    }

    public void ReleaseParked()
    {
        for (int i = 0; i < heldNpcs.Count; i++)
        {
            try { PgEngine.ReleaseHold(W, heldNpcs[i]); }
            catch (Exception e)
            {
                if (!releaseFailureLogged)   // once per world (PgSparHost's pattern)
                {
                    releaseFailureLogged = true;
                    s.OwnLog?.Invoke($"ArenaIngress release of {CharacterIdText.Format(heldNpcs[i])} failed (logged once per world): {e.GetType().Name}: {e.Message}");
                }
            }
        }
        heldNpcs.Clear();
    }

    bool releaseFailureLogged;

    public void Face(CharacterId c, Vec3 point) => PgEngine.Face(W, c, N(point));

    public void Say(CharacterId c, string text)
    {
        if (PgEngine.Bark(W, c, text)) s.Said?.Invoke(W, c, text);
    }

    // ---------- buildings ----------
    public ArenaBuilding? FindSiteForMode(ArenaLocationMode mode, Vector3 origin) => ArenaIdentity.FindSite(W, s.Data, mode, origin);
    public ArenaBuilding? Resolve(ArenaBuildingRef held) => ArenaIdentity.Resolve(W, s.Data, held);

    /// <summary>C++ <c>building->getPositionMarker(from)</c> (ArenaIngress.cpp:490): <see cref="PgEngine.PositionMarker"/> (the layout's marker of
    /// a building with fewer than two use nodes; <paramref name="from"/> picks among two or more nodes in Kenshi, which Meitou does not expose for a
    /// plain building: divergences.md "ContextMenuHooks").</summary>
    public Vec3? PositionMarker(ArenaBuilding building, Vec3 from) =>
        PgEngine.PositionMarker(W, ProvingGroundsMod.Ctx?.Data, building.Source == ArenaBuildingSource.Player ? PgEngine.BuildingSource.Player
            : PgEngine.BuildingSource.Town, building.Key, building.RecordId, building.Position, building.Yaw) is { } m ? V(m) : null;

    // ---------- ArenaUI / LeaderboardUI (task 4.2) ----------
    public void ArenaShowFromRegistry(ArenaBuilding registry, bool bindRegistry) => s.ArenaUi?.Invoke().ShowFromRegistry(W, registry, bindRegistry);
    public void LeaderboardShow(LeaderboardData.Kind kind, ArenaBuilding board) => s.Leaderboard?.Invoke().Show(W, kind, board);

    /// <summary>C++ <c>ArenaRingGuard::SetArena(site)</c> (ArenaIngress.cpp:1343): the site building as it is now (its origin = the ring's centre),
    /// a still-exists check through ArenaIdentity.Resolve each tick, the kind from ArenaIdentityLogic.RingKindOf (task 4.1).</summary>
    public void SetRingSite(ArenaBuildingRef site)
    {
        if (Resolve(site) is not { } now) { s.RingGuard.SetArena(null); return; }
        var data = s.Data;
        s.RingGuard.SetArena(new RingSite(ArenaIdentity.KeyText(site), now.Position, ArenaIdentityLogic.RingKindOf(now),
            w => ArenaIdentity.Resolve(w, data, site) is not null));
    }

    // ---------- FightStarter ----------
    public void RememberMatchFighters(CharacterId[] fighters, int count) => s.FightStarter.RememberMatchFighters(W, fighters, count);
    public void DisengageMatch(CharacterId[] fighters, int count) => s.FightStarter.DisengageMatch(W, fighters, count);
    public void LeaveStaleSweep(CharacterId c) => s.FightStarter.LeaveStaleSweep(W, c);

    // ---------- SparSession ----------
    public bool SparIsActive => s.Spar().IsActive(W);
    public bool SparStartMatch(MatchRules.MatchMode mode, CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count) =>
        s.Spar().StartMatch(W, mode, fighters, teams, count);
    public string SparGetStatus() => s.Spar().GetStatus();
    public bool SaveInFlight => s.SaveInFlight();

    // ---------- ResultsUI ----------
    public void ResultsCancel() => s.Results.Cancel(W);
    public void ResultsClose() => s.Results.Close(W);

    // ---------- TownArena ----------
    public bool TownIsAftercare() => s.Town.IsAftercare(W);
    public bool TownIsBusy() => s.Town.IsBusy(W);
    public bool TownIsPlayerMatch() => s.Town.IsPlayerMatch(W);
    public bool TownIsFighter(CharacterId c) => s.Town.IsFighter(W, c);
    public void TownCancelPlannedNpcBout(string reason) => s.Town.CancelPlannedNpcBout(W, reason);
    public bool TownValidatePreparation() => s.Town.ValidatePreparation(W);
    public void TownQuarantineIngressFighter(CharacterId c) => s.Town.QuarantineIngressFighter(W, c);

    // ---------- TownAnnouncer ----------
    public void AnnouncerRelease() => s.Announcer.Release(W);
    public void AnnouncerAbandonWorldState() => s.Announcer.AbandonWorldState(W);
    public void AnnouncerTickApproach() => s.Announcer.TickApproach(W);
    public bool AnnouncerWaitingForPosition() => s.Announcer.WaitingForPosition(W);
    public bool AnnouncerHasAnnouncement() => s.Announcer.HasAnnouncement(W);
    public void AnnouncerBeginAnnouncement() => s.Announcer.BeginAnnouncement(W);
    public void AnnouncerTickAnnouncement(float elapsed) => s.Announcer.TickAnnouncement(W, elapsed);
    public float AnnouncerAnnouncementDuration() => s.Announcer.AnnouncementDuration(W);
    public string AnnouncerGetStatus() => s.Announcer.GetStatus(W);

    // ---------- TownBookie ----------
    public bool BookieHoldForBets() => s.Bookie.HoldForBets(W);
    public bool BookieHasPendingWager() => s.Bookie.HasPendingWager(W);

    // ---------- PrisonerUtil ----------
    public bool IsMatchPrisoner(CharacterId c) => s.Prisoners.IsMatchPrisoner(W, c);
    public bool IsMatchPrisonerReleased(CharacterId c) => s.Prisoners.IsMatchPrisonerReleased(W, c);
    public bool AllMatchPrisonersReleased() => s.Prisoners.AllMatchPrisonersReleased(W);
    public bool MatchIncludesPrisoner() => s.Prisoners.MatchIncludesPrisoner(W);
    public void SendPairToArena(CharacterId prisoner, Vec3 target) => s.Prisoners.SendPairToArena(W, prisoner, N(target));
    public void BeginHandlerUnlocks() => s.Prisoners.BeginHandlerUnlocks(W);
    public void TickHandlerUnlocks() => s.Prisoners.TickHandlerUnlocks(W);
    public bool ConsumeNewlyReleased(CharacterId prisoner) => s.Prisoners.ConsumeNewlyReleased(W, prisoner);
    public void ParkMatchHandlers() => s.Prisoners.ParkMatchHandlers(W);
    public void ReinforceParkedHandlers() => s.Prisoners.ReinforceParkedHandlers(W);
    public void BeginReturnToCages(out string status) => s.Prisoners.BeginReturnToCages(W, out status);
}

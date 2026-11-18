using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/ArenaIngress.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.3b (PA §2.5; TW §2.2 step 9; CAP 5,
// 16). This file is the public surface (ArenaIngress.h); every C++ function, in order with its line range, is in Core/Glue/ArenaIngressLogic.cs,
// which runs over IIngressHost so its decisions are tested with a fake (E3, Tests/Glue/ArenaIngressLogicTests.cs). src/Game/PgIngressHost.cs is
// that host over PgEngine and this world's subsystems. The formation maths is Core/ArenaFormation.cs (task 4.3a); every site orientation is built
// as Quat.FromYaw(ArenaBuilding.Yaw) (Meitou's heading: 0 along +Z, turning towards +X, TownBuildings.YawOf, TownBuildings.cs:534-539, the same
// sense as Ogre's turn about +Y). Meitou 0.17.3 (tag v0.17.3, fa34a1b). Every method runs on the sim thread in a serial step.
//
// Engine calls of the C++ and what replaces them (E1; IIngressHost member -> adapter method in src/Engine/PgEngine.cs, bound in PgIngressHost):
//   c->isValid(), isDead(), getName(), getPosition() ... PgEngine.Exists / IsDead / Name / Position
//   c->getMovementSpeed() (:900, log lines :709, :1680) ... PgEngine.MovementSpeed (horizontal velocity, CharacterTable.cs:45-47)
//   movement->isCurrentlyMoving(), movement->destination (log lines :708-709, :1680-1683) ... PgEngine.MoveDestination
//   GetTickCount() (:840, :1307, :1430, :1505, :1571, :1745, :1790) ... PgClock.NowMs (uint, wraps; dt = (now - last) / 1000 as the C++)
//   ou->isPaused() (:1418) ... never true in a serial step (a paused world runs no SlowWorld, World.Interface.cs:16-40)
//   SquadUtil::CollectPlayerSquad (HasLocationSite :1110) ... PgEngine.PlayerSquads, the first member
//   IssueMove: TownArena::IsFighter + clearAllAIGoals (:434), setStandingOrder(HOLD/PASSIVE, false) (:436-437), movement->setDestination,
//       addOrder(MOVE_CUS_ORDERED, loc), setDestination (:439-446), RUN for a town fighter (:447-452) ... PgEngine.SetMoveStance + PgEngine.MoveTo
//       (one MoveOrder per character id for the next tick, CAP 5); the town fighter's AI clear and RUN are task 7.3's (no town fighter before M7)
//   removeJob(MOVE_CUS_ORDERED) (:603, :795, :801, :1810) ... PgEngine.EndWalk (a GoTo walk ended through TaskStart.End)
//   ParkCharacterInPlace: removeJob, clearAllAIGoals, setStandingOrder(PASSIVE/HOLD true, AGG false), addGoal(STAND_STILL) for a prisoner or
//       town fighter, reThinkCurrentAIAction (:599-616) ... player characters: PgEngine.EndWalk + SetBenchStance(id, true) (stand still with the
//       Hold stance; the stance before the staging comes back through FightStarter's restore, which RememberMatchFighters recorded at ingress
//       start); NPCs: PgEngine.EndWalk + HoldStill / ReassertHold (CharacterCold.Engaged, spike S10, re-asserted on every park, i.e. every
//       countdown tick), released where the staging ends (divergences.md "ArenaIngress")
//   lookatPosition(face, true) (FaceOpposingFormation :657) ... PgEngine.Face (CharacterHot.Yaw = atan2(dx, dz)); the point is
//       ArenaFormation.OpposingFacePoint
//   say(line) (ShowFighterCountdown :835) ... PgEngine.Bark (world.Bark, CAP 16), the strings "3", "2", "1", "FIGHT!"
//   hand g_boundRegistry / g_boundSite (isValid, getBuilding) ... ArenaBuildingRef re-resolved each tick through ArenaIdentity.Resolve (task 4.1)
//   building->getPosition() / getOrientation() ... ArenaBuilding.Position / Quat.FromYaw(ArenaBuilding.Yaw)
//   FindSiteForMode / FindNearestMatching (:175-233) ... ArenaIdentity.FindSite (task 4.1; writes the C++ "site search" line)
//   ArenaRingGuard::SetArena(site) (:1343) ... ArenaRingGuard.SetArena(new RingSite(key, centre, ArenaIdentityLogic.RingKindOf, still-exists))
//   FightStarter::RememberMatchFighters / DisengageMatch ... src/Game/FightStarter.cs; port only: FightStarter.LeaveStaleSweep for a walk-in
//   SparSession::IsActive / StartMatch / GetStatus ... src/Game/SparSession.cs
//   ResultsUI::Cancel / Close ... src/Ui/ResultsUI.cs (task 4.6)
//   TownArena::IsAftercare / IsBusy / IsPlayerMatch / IsFighter / CancelPlannedNpcBout / ValidatePreparation / QuarantineIngressFighter ...
//       src/Town/TownArena.cs (task 7.1: the state reads, CancelPlannedNpcBout; task 7.3: ValidatePreparation, QuarantineIngressFighter, the town fighter's borrow and RUN)
//   TownAnnouncer::* ... src/Town/TownAnnouncer.cs (task 8.3)
//   TownBookie::HoldForBets / HasPendingWager ... src/Town/TownBookie.cs (task 8.1a: the market's 20 s hold and the ticket)
//   PrisonerUtil::* (IsMatchPrisoner, ..., BeginReturnToCages) ... src/Game/PrisonerUtil*.cs (the match reads: task 6.1; release and park: task 6.2; the return: task 6.3)
//   PGLog::Debug(...) ... PGLog.Debug, the C++ texts byte for byte (%.1f / %.2f through CMath.FormatF)
//   SquadUtil::CollectSelectedCharacters (BeginApproachForUI :1198) ... src/Game/SquadUtil.cs (world.Player.Selection), or the selection the
//       right press carried (ContextMenuHooks' command)
//   building->getPositionMarker(from) (RegistryApproachPoint :490) ... PgEngine.PositionMarker (WorldObjectLayout.PositionMarker)
//   ArenaUI::ShowFromRegistry (:963, :990) ... src/Game/ArenaUISim.cs; LeaderboardUI::Show (:973, :1000) ... src/Game/LeaderboardUISim.cs
// Callers (C++ -> port): ArenaUI.cpp:3155 Tick = the frame loop's ArenaIngress.Tick slot; ArenaUI's Start (task 4.5; until then the TEST-ONLY
// `pg ingress`) -> Begin; SparSession.cpp walk-in calls (src/Game/PgSparHost.cs); WorldLifecycle.cpp:65, :118, :240-241, :266 (save safety, task
// 2.5) -> AbandonWorldState / Cancel / IsPending / IsWalkInPending; ContextMenuHooks (task 4.2) -> BeginApproachForUI / NotifyExternalOrder.

/// <summary>
/// The glue of the original ArenaIngress.cpp: the Registry bind, the staging of a bout at the chosen site (formation, gather hold, countdown)
/// and the Teams 1v1 walk-in. One object per world, owned by ProvingGroundsSystem. Public surface = ArenaIngress.h; each call binds the world
/// and runs the C++ function in <see cref="ArenaIngressLogic{TId}"/>.
/// </summary>
sealed class ArenaIngress(Action<string>? ownLog = null)
{
    /// <summary>The other subsystems and reads the staging calls (built by ProvingGroundsSystem). <see cref="Spar"/> is lazy: SparSession is
    /// built over this object; so are <see cref="ArenaUi"/> and <see cref="Leaderboard"/> (the approach's windows, task 4.2, built after this
    /// object). <see cref="Said"/>: port-only test output of each bark.</summary>
    internal sealed record Services(
        FightStarter FightStarter,
        Func<SparSession> Spar,
        ArenaRingGuard RingGuard,
        Ui.ResultsUI Results,
        PrisonerUtil Prisoners,
        Town.TownArena Town,
        Town.TownAnnouncer Announcer,
        Town.TownBookie Bookie,
        PgClock Clock,
        PopulationData Data,
        Func<bool> SaveInFlight,
        Action<World, CharacterId, string>? Said,
        Action<string>? OwnLog,
        Func<ArenaUISim>? ArenaUi = null,
        Func<LeaderboardUISim>? Leaderboard = null);

    readonly ArenaIngressLogic<CharacterId> logic = new(CharacterId.None, ownLog);   // ownLog: the port's lines (a staging step that threw)
    PgIngressHost? host;
    Services? services;
    string lastLogged = "";   // TEST ONLY: the last PG-INGRESS state line (see Tick)

    /// <summary>Gives the staging its subsystems (ProvingGroundsSystem's constructor, once every subsystem exists).</summary>
    internal void Wire(Services s)
    {
        services = s;
        host = new PgIngressHost(s);
    }

    PgIngressHost H(World world) => (host ?? throw new InvalidOperationException("ArenaIngress used before Wire")).Bind(world);

    /// <summary>The logic (tests, the status answer).</summary>
    internal ArenaIngressLogic<CharacterId> Logic => logic;

    // ---------- namespace ArenaIngress (ArenaIngress.h), C++ line ranges in Core/Glue/ArenaIngressLogic.cs ----------

    internal void BindRegistry(ArenaBuilding? registry) => logic.BindRegistry(registry);
    internal void ClearBind() => logic.ClearBind();
    internal bool HasValidRegistry(World world) => logic.HasValidRegistry(H(world));
    internal ArenaBuilding? GetBoundRegistry(World world) => logic.GetBoundRegistry(H(world));
    internal void SetLocationMode(ArenaLocationMode mode) => logic.SetLocationMode(mode);
    internal ArenaLocationMode GetLocationMode() => logic.GetLocationMode();
    internal bool HasLocationSite(World world) => logic.HasLocationSite(H(world));
    internal string GetMissingLocationMessage() => logic.GetMissingLocationMessage();
    internal bool IsPending(World world) => logic.IsPending();               // C++ ArenaIngress.h:30
    internal string GetStatus() => logic.GetStatus();
    /// <summary>C++ <c>ArenaRingGuard::SetArena(arena)</c> for TownArena's BeginEvent (TownArena.cpp:1715, task 7.3): the same ring registration
    /// the staging makes (<see cref="PgIngressHost.SetRingSite"/>), so the town and the ingress build one RingSite the same way.</summary>
    internal void SetRingSite(World world, ArenaBuildingRef site) => H(world).SetRingSite(site);

    // The Registry / Banner / Leaderboard approach (task 4.2): C++ ArenaIngress.h:34-39
    internal bool BeginApproachForUI(World world, ArenaBuilding? building) => BeginApproachForUI(world, building, null);

    /// <summary>BeginApproachForUI with the selection the right press saw (null: the world's selection; ContextMenuHooks' command, task 4.2).</summary>
    internal bool BeginApproachForUI(World world, ArenaBuilding? building, CharacterId[]? selection)
    {
        var h = H(world);
        h.SelectionOverride = selection;
        try { return logic.BeginApproachForUI(h, building); }
        finally { h.SelectionOverride = null; }
    }

    /// <summary>Port only (the PG-INTERACT line): the walk-up point the approach gives <paramref name="from"/> for <paramref name="building"/>.</summary>
    internal Vec3 ApproachPointFor(World world, ArenaBuilding building, CharacterId from) => logic.ApproachPointFor(H(world), building, from);

    /// <summary>Port only: whether a move order arriving now is one of this staging's own approach moves (the C++ g_issuingManagedMove, see
    /// ArenaIngressLogic.ConsumeOwnApproachMove); <paramref name="stale"/>: its approach is over.</summary>
    internal bool ConsumeOwnApproachMove(CharacterId fighter, System.Numerics.Vector3 point, out bool stale) =>
        logic.ConsumeOwnApproachMove(fighter, new Vec3(point.X, point.Y, point.Z), services?.Clock.NowMs ?? 0, out stale);
    internal void NotifyExternalOrder(World world, CharacterId fighter) => logic.NotifyExternalOrder(H(world), fighter);
    internal CharacterId GetUiOpener(World world) => logic.GetUiOpener(H(world));

    internal bool Begin(World world, MatchRules.MatchMode mode, CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count) =>
        logic.Begin(H(world), mode, fighters, teams, count);
    internal bool Begin(World world, MatchRules.MatchMode mode, CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count,
        CharacterId[]? escorts, int escortCount, ArenaBuildingRef? siteOverride = null) =>
        logic.Begin(H(world), mode, fighters, teams, count, escorts, escortCount, siteOverride);
    internal void Cancel(World world) => logic.Cancel(H(world));             // C++ ArenaIngress.h:50

    // C++: ArenaIngress.cpp:1413: called from ArenaUI.cpp ForgottenGUI_update_hook (:3155).
    internal void Tick(World world)
    {
        logic.Tick(H(world));
        if (Spikes.SpikePatches.Armed) LogState(world);   // TEST ONLY: a PG-INGRESS line per status change
    }

    internal bool BeginWalkIn(World world, CharacterId fighter, MatchRules.MatchTeam team) => logic.BeginWalkIn(H(world), fighter, team);   // C++ ArenaIngress.h:54
    internal bool IsWalkInPending(World world) => logic.IsWalkInPending();   // C++ ArenaIngress.h:55
    internal CharacterId GetWalkInFighter(World world) => logic.GetWalkInFighter();   // C++ ArenaIngress.h:56
    internal bool IsWalkInArrived(World world) => logic.IsWalkInArrived(H(world));   // C++ ArenaIngress.h:57
    internal void TickWalkIn(World world) => logic.TickWalkIn(H(world));     // C++ ArenaIngress.h:58
    internal void ClearWalkIn(World world)   // C++ ArenaIngress.h:59
    {
        if (Spikes.SpikePatches.Armed && logic.IsWalkInPending()) LogWalkInEnd(world);   // TEST ONLY
        logic.ClearWalkIn(H(world));
    }
    internal void AbandonWorldState(World world) => logic.AbandonWorldState(H(world));   // C++ ArenaIngress.h:62

    /// <summary>The F10 phase row.</summary>
    internal string Phase => (logic.IsWalkInPending()
        ? "walk-in"
        : logic.IsPending() ? logic.Kind.ToString().ToLowerInvariant() + ": " + logic.GetStatus() : "idle")
        + (logic.FailureCount > 0 ? ", " + logic.FailureCount.ToString(CultureInfo.InvariantCulture) + " staging steps failed" : "");

    // ---------- TEST ONLY: pg ingress (task 4.3b, until the Arena window, task 4.5) ----------

    static string Q(string? s) => PGLog.Quote(s ?? "");
    static string G(float v) => TestJson.G(v);   // task 8.8: NaN -> null

    static void Line(string json) => PgPerf.TestLine("PG-INGRESS " + json);

    static bool TryLocation(string word, out ArenaLocationMode mode)
    {
        switch (word)
        {
            case "arena": mode = ArenaLocationMode.Arena; return true;
            case "small": mode = ArenaLocationMode.SmallArena; return true;
            case "banner": mode = ArenaLocationMode.Banner; return true;
            default: mode = default; return false;
        }
    }

    const string Usage = "usage pg ingress <avb|1v1|ffa> <arena|small|banner> <idA...> [vs <idB...>] | pg ingress status | pg ingress cancel";

    /// <summary>TEST ONLY <c>pg ingress &lt;avb|1v1|ffa&gt; &lt;arena|small|banner&gt; &lt;idA...&gt; [vs &lt;idB...&gt;]</c>: binds the nearest finished
    /// Registry (if any, within the site search's 25,000 of the first fighter), sets the location as the Arena window's location buttons do
    /// (SetLocationMode) and calls <see cref="Begin(World, MatchRules.MatchMode, CharacterId[], MatchRules.MatchTeam[], int)"/> as the window's
    /// Start does after its checks. <c>pg ingress status</c>: the phase, status, each fighter's target and ground distance, the countdown time,
    /// the walk-in. <c>pg ingress cancel</c>: Cancel. Every answer is one PG-INGRESS JSON line.</summary>
    internal string Command(World world, PopulationData data, IReadOnlyList<string> args)
    {
        try
        {
            if (args.Count == 1 && args[0] == "status") return Answer(world, "status", true, null);
            if (args.Count == 1 && args[0] == "cancel") { Cancel(world); return Answer(world, "cancel", true, null); }
            if (args.Count < 3 || !TryLocation(args[1].ToLowerInvariant(), out var location)) return Answer(world, "begin", false, Usage);
            var rest = new List<string> { args[0] };
            rest.AddRange(args.Skip(2));
            if (!SparArgs.TryParse<CharacterId>(rest, CharacterIdText.TryParse, out var mode, out var fighters, out var teams, out string error))
                return Answer(world, "begin", false, error.Replace("pg spar", "pg ingress"));
            // the nearest finished Registry, as the window would be opened from one (ArenaUI::ShowFromRegistry binds it)
            if (PgEngine.Position(world, fighters[0]) is { } at)
            {
                var hits = ArenaIdentityLogic.SphereHits(ArenaIdentity.Near(world, data, at, ArenaIdentityLogic.SiteSearchRadius), at);
                if (ArenaIdentityLogic.FindNearestMatching(at, hits, ArenaIdentityLogic.IsRegistryFinished, out _) is { } reg)
                    BindRegistry(reg);
            }
            SetLocationMode(location);
            bool ok = Begin(world, mode, fighters, teams, fighters.Length);
            if (Spikes.SpikePatches.Armed) LogState(world);
            return Answer(world, "begin", ok, ok ? null : logic.GetStatus());
        }
        catch (Exception e) { return Answer(world, "ingress", false, $"{e.GetType().Name}: {e.Message}"); }
    }

    string Answer(World world, string verb, bool ok, string? error)
    {
        var sb = new StringBuilder();
        sb.Append("{\"event\":").Append(Q(verb)).Append(",\"ok\":").Append(ok ? "true" : "false");
        if (error is not null) sb.Append(",\"error\":").Append(Q(error));
        AppendState(world, sb);
        string json = sb.Append('}').ToString();
        Line(json);
        return json;
    }

    /// <summary>TEST ONLY: one PG-INGRESS "state" line whenever the phase or the status text changed (never per tick), so a scenario can time the
    /// gather hold and the countdown by sim ticks.</summary>
    void LogState(World world)
    {
        string key = logic.Kind + "|" + logic.GetStatus() + "|" + logic.IsWalkInPending();
        if (key == lastLogged) return;
        lastLogged = key;
        var sb = new StringBuilder("{\"event\":\"state\"");
        AppendState(world, sb);
        Line(sb.Append('}').ToString());
    }

    /// <summary>TEST ONLY: where the walk-in fighter stood when its walk-in ended (arrived, eliminated, cancelled), by sim tick.</summary>
    void LogWalkInEnd(World world)
    {
        var w = logic.GetWalkInFighter();
        var wt = logic.WalkInTarget;
        var sb = new StringBuilder("{\"event\":\"walkin-end\",\"tick\":").Append(world.Tick.ToString(CultureInfo.InvariantCulture))
            .Append(",\"id\":").Append(Q(CharacterIdText.Format(w))).Append(",\"tx\":").Append(G(wt.X)).Append(",\"tz\":").Append(G(wt.Z))
            .Append(",\"elapsed\":").Append(G(logic.WalkInElapsedSec));
        if (PgEngine.Position(world, w) is { } p)
            sb.Append(",\"x\":").Append(G(p.X)).Append(",\"z\":").Append(G(p.Z))
              .Append(",\"dist\":").Append(G(MathF.Sqrt(ArenaFormation.HorizontalDistSq(new Vec3(p.X, p.Y, p.Z), wt))));
        Line(sb.Append('}').ToString());
    }

    void AppendState(World world, StringBuilder sb)
    {
        sb.Append(",\"tick\":").Append(world.Tick.ToString(CultureInfo.InvariantCulture))
          .Append(",\"pending\":").Append(logic.IsPending() ? "true" : "false")
          .Append(",\"kind\":").Append(Q(logic.Kind.ToString()))
          .Append(",\"status\":").Append(Q(logic.GetStatus()))
          .Append(",\"location\":").Append(Q(logic.GetLocationMode().ToString()))
          .Append(",\"registry\":").Append(logic.BoundRegistryRef is { } r ? Q(ArenaIdentity.KeyText(r)) : "null")
          .Append(",\"site\":").Append(logic.BoundSite is { } st ? Q(ArenaIdentity.KeyText(st)) : "null")
          .Append(",\"elapsed\":").Append(G(logic.ElapsedSec))
          .Append(",\"hold\":").Append(G(logic.SettleHoldSec))
          .Append(",\"stage\":").Append(logic.CountdownStage.ToString(CultureInfo.InvariantCulture))
          .Append(",\"fighters\":[");
        for (int i = 0; i < logic.Fighters.Count; i++)
        {
            var id = logic.Fighters[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(Q(CharacterIdText.Format(id)));
            if (i < logic.Targets.Count)
            {
                var t = logic.Targets[i];
                sb.Append(",\"tx\":").Append(G(t.X)).Append(",\"ty\":").Append(G(t.Y)).Append(",\"tz\":").Append(G(t.Z));
                if (PgEngine.Position(world, id) is { } p)
                    sb.Append(",\"x\":").Append(G(p.X)).Append(",\"z\":").Append(G(p.Z)).Append(",\"dist\":").Append(G(MathF.Sqrt(
                        ArenaFormation.HorizontalDistSq(new Vec3(p.X, p.Y, p.Z), t)))).Append(",\"yaw\":").Append(G(PgEngine.Yaw(world, id)));
            }
            sb.Append('}');
        }
        sb.Append("],\"escorts\":[");   // task 6.1: the prisoners' handlers (ArenaUI.cpp:2384-2386 GetMatchHandlers)
        for (int i = 0; i < logic.Escorts.Count; i++) sb.Append(i > 0 ? "," : "").Append(Q(CharacterIdText.Format(logic.Escorts[i])));
        sb.Append("],\"walkIn\":");
        if (!logic.IsWalkInPending()) { sb.Append("null"); return; }
        var w = logic.GetWalkInFighter();
        var wt = logic.WalkInTarget;
        sb.Append("{\"id\":").Append(Q(CharacterIdText.Format(w))).Append(",\"tx\":").Append(G(wt.X)).Append(",\"tz\":").Append(G(wt.Z))
          .Append(",\"elapsed\":").Append(G(logic.WalkInElapsedSec));
        if (PgEngine.Position(world, w) is { } wp)
            sb.Append(",\"dist\":").Append(G(MathF.Sqrt(ArenaFormation.HorizontalDistSq(new Vec3(wp.X, wp.Y, wp.Z), wt))));
        sb.Append('}');
    }
}

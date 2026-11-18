using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Port of upstream/ProvingGrounds/plugin/src/TownAnnouncer.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.3 (TW §2.8, CAP 5, 16, 19). The C++
// decisions are Core/Glue/TownAnnouncerLogic.cs (fake-tested, Tests/Glue/TownAnnouncerLogicTests.cs) over ITownAnnouncerHost; this class binds them
// to a world and to CharacterIds. The state (C++ file-level globals, TownAnnouncer.cpp:53-65) is the logic's, one object per world: ProvingGroundsSystem
// builds this object (built again for every world), TownArena attaches its services to it and clears it through its AbandonWorldState chain
// (TownArena.cpp:1800), ArenaIngress through its own (ArenaIngress.cpp:1014). Not in the frame loop: the approach and the announcement are ticked by
// ArenaIngress (ArenaIngress.cpp:1420, 1447-1460, 1576, 1635-1638), the pending releases by TownArena.Tick (TownArena.cpp:1886), Begin by TownArena's
// BeginEvent (TownArena.cpp:1738), Release by ArenaIngress's ClearPendingState (:809) and TownArena's Finish (:399). Nothing is saved (as the C++).
//
// Engine calls of the C++ (E1) and what replaces them:
//   ou->player->getAllPlayerCharacters() (IsPlayer :67-76) ... PgEngine.IsPlayer
//   Character::isValid / isDead / isUnconcious / isInCombatMode(true,true) / isBeingCarried / isCarryingSomething / getOrdersReciever (CanAnnounce
//       :86-96, CanContinue :98-103) ... PgEngine Exists / IsDead / IsUnconscious / InCombatMode / IsCarried / IsCarrying (every character takes orders)
//   getGameData()->stringID (FindSpeaker :130) ... PgEngine.RecordId; port only: PgEngine.Faction == the town faction 91 (census)
//   getCurrentTownLocation / arena->getTown() ... Towns.CurrentTownOf / ArenaIdentity.Resolve's town
//   ou->getObjectsWithinSphere(arena, 2500, CHARACTER, 512) (FindSpeaker :119-120) ... PgEngine.CharactersNear (into one reused list)
//   ou->getObjectsWithinSphere(arena, 2500, BUILDING, 1024) + ArenaIdentity::GetStringId + getTown + getBuildState()->isComplete (FindSpot :144-173)
//       ... PgEngine.BuildingsNear filtered to the Announcer Spot record 261 (only once per bout) + ArenaIdentity.From
//   hand::getBuilding + isValid (g_arena / g_spot) ... ArenaIdentity.Resolve (cached: again only when the ref changed, a building was ruined or rebuilt,
//       or 5 s passed; it builds a PgBuilding record)
//   TownAftercare::ReserveExternalOrders / ReleaseExternalOrders ... src/Town/TownAftercare.cs (the shared four external slots + the hold, owner External)
//   getStandingOrder / setStandingOrder (HOLD, PASSIVE) ... PgEngine HoldPassive / SetStandingOrders
//   getMovementSpeedOrders / setDesiredSpeedOrders + restoreDesiredSpeed / setDesiredSpeed(RUN) ... PgEngine RunSpeedOf / SetRunSpeedMode / SetRunSpeed
//   addOrder(MOVE_CUS_ORDERED, position) + removeJob + clearAllAIGoals (PrepareNativeResume :262-265) ... PgEngine.ClearOrders
//   reThinkCurrentAIAction ... PgEngine.Replan (the hold's release also replans)
//   removeJob(MOVE_CUS_ORDERED) + clearAllAIGoals + addGoal(STAND_STILL) (HoldAtStand :230-234) ... PgEngine.EndWalk (a held NPC's AI chooses no goal)
//   lookatPosition(arena, true) (FaceArena :218-223) ... PgEngine.Face, written only once the character has stopped (CAP 5)
//   movement->setDestination + setDesiredSpeed(RUN) + addOrder(MOVE_CUS_ORDERED, target) + setDestination (IssueMove :238-258) ... PgEngine
//       SetStandingOrders(false, false) + SetRunSpeed + MoveTo (a MoveOrder: a player-order walk, TaskSource not Goal)
//   Character::say ... TownSpeech.Say (PgEngine.Bark + the test-only say line "announcer", CAP 16)
//   GetTickCount ... PgClock.NowMs; ou->getTimeStamp_inGameHours().getTotalHours() ... World.GameHours; ou->isPaused ... false;
//   PGLog::Debug ... PGLog.Debug (texts byte for byte)

/// <summary>The TownAnnouncer of one world: the logic and its state, bound to a world for each call (sim thread, serial steps only).</summary>
sealed class TownAnnouncer
{
    PopulationData? data;
    PgClock? clock;
    Towns? towns;
    TownAftercare? aftercare;
    Holds? holds;
    Func<World, CharacterId, bool>? isTownFaction;
    readonly Host host;
    readonly TownAnnouncerLogic<CharacterId> logic = new(CharacterId.None);
    /// <summary>Port only (the backstop): every NPC this announcer reserved in the external table, until it gave it back.</summary>
    readonly List<CharacterId> reserved = [];
    /// <summary>Port only (the backstop): every deferred speaker this announcer holds (owner AnnouncerPending), until it let go.</summary>
    readonly List<CharacterId> pendingHeld = [];

    internal TownAnnouncer() => host = new Host(this);

    /// <summary>TownArena gives the announcer its services when it is built (ProvingGroundsSystem builds this object first, for ArenaIngress).</summary>
    internal void Attach(PopulationData data, PgClock clock, Towns towns, TownAftercare aftercare, Holds holds, Func<World, CharacterId, bool> isTownFaction)
    {
        this.holds = holds;
        this.data = data;
        this.clock = clock;
        this.towns = towns;
        this.aftercare = aftercare;
        this.isTownFaction = isTownFaction;
    }

    bool Attached => data is not null;

    internal TownAnnouncerLogic<CharacterId> Logic => logic;

    ITownAnnouncerHost<CharacterId> H(World world) => host.Bind(world);

    // Port only: the steps the glue catches, each logged once per world and counted (F10's TownAnnouncer row, pg town announcer "failures"). After a
    // throw the cast is forgotten and every reservation the announcer took is given back (the speakers' AI resumes through the hold's release), so
    // HasAnnouncement answers false and the staging falls back to the fighters' countdown (as the C++'s FallBack).
    internal const int StepBegin = 0, StepApproach = 1, StepReleases = 2, StepAnnounce = 3, StepRelease = 4, StepOrphans = 5;
    static readonly string[] StepNames = ["begin", "approach", "releases", "announcement", "release", "orphan release"];
    readonly bool[] stepLogged = new bool[6];
    readonly int[] stepFailures = new int[6];
    bool orphanLogged;
    int orphanReleases, restoreFailures;

    void LogStep(int step, Exception e)
    {
        ++stepFailures[step];
        if (stepLogged[step]) return;
        stepLogged[step] = true;
        PGLog.Debug("Proving Grounds: announcer " + StepNames[step] + " failed error=" + e.GetType().Name + ": " + e.Message);
        ProvingGroundsMod.Ctx?.LogError($"announcer {StepNames[step]} failed: {e.GetType().Name}: {e.Message}");
    }

    void Fail(World world, int step, Exception e)
    {
        LogStep(step, e);
        // the speakers' saved Hold / Passive / speed back before the holds go back (spec review 8.3); a restore that throws is counted
        try { restoreFailures += logic.RestoreSpeakersAfterFailure(H(world)); }
        catch (Exception r) { ++restoreFailures; LogStep(StepOrphans, r); }
        logic.ForgetAll();
        ReleaseOrphanReservations(world, all: true);
    }

    // ---------- the calls (TownAnnouncer.h) ----------

    /// <summary>C++ :288-363 (TownArena.cpp:1738, BeginEvent).</summary>
    internal void Begin(World world, ArenaBuildingRef arena, bool playerMatch, int challenge, string[] roles, int roleCount,
        CharacterId[] fighters, int fighterCount)
    {
        if (!Attached) return;
        try { logic.Begin(H(world), arena, playerMatch, challenge, roles, roleCount, fighters, fighterCount); }
        catch (Exception e) { Fail(world, StepBegin, e); }
    }

    /// <summary>C++ :365-415 (ArenaIngress.cpp:1420, 1576).</summary>
    internal void TickApproach(World world)
    {
        if (!Attached || !logic.HasAnnouncement()) return;
        try { logic.TickApproach(H(world)); }
        catch (Exception e) { Fail(world, StepApproach, e); }
    }

    /// <summary>C++ :417-442 (TownArena.cpp:1886), then the port's backstop: a reservation that is no longer a speaker's is given back, and a cast
    /// that outlived its staging (<paramref name="ingressPending"/> false while an announcement is held) is released.</summary>
    internal void TickReleases(World world, bool ingressPending)
    {
        if (!Attached) return;
        if (logic.PendingReleaseCount > 0)
        {
            try { logic.TickReleases(H(world)); }
            catch (Exception e) { LogStep(StepReleases, e); }
        }
        if (logic.HasAnnouncement() && !ingressPending)
        {
            ++orphanReleases;
            if (!orphanLogged)
            {
                orphanLogged = true;
                PGLog.Debug("Proving Grounds: announcer released without a staging");
            }
            Release(world);
        }
        else if (reserved.Count > 0 || pendingHeld.Count > 0) ReleaseOrphanReservations(world, all: false);
    }

    /// <summary>C++ :444-452 (ArenaIngress.cpp:1635).</summary>
    internal bool WaitingForPosition(World world)
    {
        if (!Attached || !logic.HasAnnouncement()) return false;
        try { return logic.WaitingForPosition(H(world)); }
        catch (Exception e) { Fail(world, StepApproach, e); return false; }
    }

    internal bool HasAnnouncement(World world) => logic.HasAnnouncement();                       // C++ :454-457

    /// <summary>C++ :459-467 (ArenaIngress.cpp:857-858).</summary>
    internal void BeginAnnouncement(World world)
    {
        if (!Attached || !logic.HasAnnouncement()) return;
        try { logic.BeginAnnouncement(H(world)); }
        catch (Exception e) { Fail(world, StepAnnounce, e); }
    }

    /// <summary>C++ :469-500 (ArenaIngress.cpp:1449).</summary>
    internal void TickAnnouncement(World world, float elapsed)
    {
        if (!Attached || !logic.HasAnnouncement()) return;
        try { logic.TickAnnouncement(H(world), elapsed); }
        catch (Exception e) { Fail(world, StepAnnounce, e); }
    }

    internal float AnnouncementDuration(World world) => logic.AnnouncementDuration();             // C++ :502-505
    internal string GetStatus(World world) => logic.GetStatus();                                   // C++ :507-510

    /// <summary>C++ :512-545 (ArenaIngress.cpp:809, TownArena.cpp:399), then the backstop.</summary>
    internal void Release(World world)
    {
        if (!Attached) return;
        try { logic.Release(H(world)); }
        catch (Exception e)
        {
            LogStep(StepRelease, e);
            // the speakers Release had not reached get their saved Hold / Passive / speed back (or are deferred) first (quality review 8.3)
            try { restoreFailures += logic.RestoreSpeakersAfterFailure(H(world)); }
            catch (Exception r) { ++restoreFailures; LogStep(StepOrphans, r); }
            logic.ForgetAll();
        }
        ReleaseOrphanReservations(world, all: true);
    }

    /// <summary>C++ :547-564: no actor access (the reservations and holds are cleared by TownAftercare's and the hold registry's own resets).</summary>
    internal void AbandonWorldState(World world)
    {
        logic.AbandonWorldState();
        reserved.Clear();
        pendingHeld.Clear();   // the hold registry is emptied by its own world reset
        host.Forget();         // no reference to the old world is kept
    }

    /// <summary>Port only (the backstop): every external reservation this announcer took that is not a current speaker's is given back (a
    /// reservation a throw left behind). With <paramref name="all"/>, every one is given back.</summary>
    internal void ReleaseOrphanReservations(World world, bool all)
    {
        if (aftercare is null) return;
        for (int i = reserved.Count - 1; i >= 0; i--)
        {
            CharacterId c = reserved[i];
            if (!all && logic.Keeps(c)) continue;
            reserved.RemoveAt(i);
            try { aftercare.ReleaseExternalOrders(world, c); }
            catch (Exception e) { LogStep(StepOrphans, e); }
        }
        // a pending hold whose entry is gone (dropped after a throw) is given back
        for (int i = pendingHeld.Count - 1; i >= 0; i--)
        {
            CharacterId c = pendingHeld[i];
            if (logic.IsPendingRelease(c)) continue;
            pendingHeld.RemoveAt(i);
            try { holds?.Release(world, c, HoldOwner.AnnouncerPending); }
            catch (Exception e) { LogStep(StepOrphans, e); }
        }
    }

    /// <summary>The F10 row's phase (sim thread, at publish): the programme, the status and the failure counts.</summary>
    internal string PhaseText()
    {
        var l = logic;
        int total = orphanReleases + restoreFailures;
        for (int i = 0; i < stepFailures.Length; i++) total += stepFailures[i];
        string where = l.HasAnnouncement() ? l.Program.ToString() : "idle";
        return where + ": " + l.GetStatus() + (total == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
            $"; announcer failures: begin {stepFailures[0]}, approach {stepFailures[1]}, releases {stepFailures[2]}, announcement {stepFailures[3]}, release {stepFailures[4]}, orphan release {stepFailures[5]}, released without a staging {orphanReleases}, restores {restoreFailures}"));
    }

    /// <summary>ITownAnnouncerHost over one world, rebound for every call.</summary>
    sealed class Host(TownAnnouncer owner) : ITownAnnouncerHost<CharacterId>
    {
        World? world;
        readonly List<CharacterId> nearby = new(512);
        readonly List<PlacedBuilding> spots = new(2);
        static readonly HashSet<string> SpotRecords = new(StringComparer.Ordinal) { PgRecords.AnnouncerSpot };

        internal Host Bind(World w) { world = w; return this; }
        /// <summary>AbandonWorldState: the world and the cached buildings forgotten.</summary>
        internal void Forget()
        {
            world = null;
            for (int i = 0; i < 2; i++) { cacheRef[i] = null; cacheWorld[i] = null; cachePlaced[i] = null; }
        }

        World W => world ?? throw new InvalidOperationException("TownAnnouncer used before a world was bound");
        PopulationData D => owner.data!;

        // ---------- the world ----------
        public bool IsPaused => false;   // a paused world runs no serial step (World.Interface.cs:16-40)
        public uint NowMs => owner.clock!.NowMs;
        public double GameHours => W.GameHours;
        public void Debug(string line) => PGLog.Debug(line);

        // ---------- characters ----------
        public CharacterId None => CharacterId.None;
        public bool IsNull(CharacterId c) => c.IsNone;
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        public bool IsPlayer(CharacterId c) => PgEngine.IsPlayer(W, c);
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(W, c);
        public bool InCombatMode(CharacterId c) => PgEngine.InCombatMode(W, c);
        public bool IsCarried(CharacterId c) => PgEngine.IsCarried(W, c);
        public bool IsCarrying(CharacterId c) => PgEngine.IsCarrying(W, c);
        public string RecordId(CharacterId c) => PgEngine.RecordId(W, c) ?? "";
        public bool IsTownFaction(CharacterId c) => owner.isTownFaction!(W, c);
        public Vector3 Position(CharacterId c) => PgEngine.Position(W, c) ?? Vector3.Zero;
        public long CurrentTown(CharacterId c) => Towns.Key(owner.towns!.CurrentTownOf(W, c));
        public IReadOnlyList<CharacterId> CharactersInSphere(Vector3 origin, float radius, int max)
        {
            PgEngine.CharactersNear(W, origin, radius, max, nearby);
            return nearby;
        }

        // ---------- buildings ----------
        // the arena and the spot resolved again only when the ref changed, a building was ruined or rebuilt (RuinsRevision) or 5 s passed (as the
        // limb shop's: ArenaIdentity.Resolve builds a PgBuilding record; TickApproach and every facing read them)
        public PlacedBuilding? Resolve(ArenaBuildingRef held)
        {
            int slot = cacheRef[0] == held ? 0 : cacheRef[1] == held ? 1 : -1;
            long revision = Beds.RuinsRevision(W);
            uint now = owner.clock!.NowMs;
            if (slot >= 0 && cacheWorld[slot] == W && cacheRevision[slot] == revision && unchecked(now - cacheAt[slot]) < 5000) return cachePlaced[slot];
            if (slot < 0) { slot = nextSlot; nextSlot ^= 1; }
            cachePlaced[slot] = ArenaIdentity.Resolve(W, D, held, out var town) is { } b ? new PlacedBuilding(b, Towns.Key(town)) : null;
            cacheRef[slot] = held; cacheWorld[slot] = W; cacheRevision[slot] = revision; cacheAt[slot] = now;
            return cachePlaced[slot];
        }
        readonly ArenaBuildingRef?[] cacheRef = new ArenaBuildingRef?[2];
        readonly World?[] cacheWorld = new World?[2];
        readonly long[] cacheRevision = new long[2];
        readonly uint[] cacheAt = new uint[2];
        readonly PlacedBuilding?[] cachePlaced = new PlacedBuilding?[2];
        int nextSlot;

        public IReadOnlyList<PlacedBuilding> SpotsInSphere(Vector3 origin, float radius, int max)
        {
            spots.Clear();
            var production = PgEngine.Production(W);
            foreach (var b in PgEngine.BuildingsNear(W, D, origin, radius, SpotRecords, max))
                spots.Add(new PlacedBuilding(ArenaIdentity.From(b, production), Towns.Key(b.Town)));
            return spots;
        }

        // ---------- other subsystems ----------
        public bool ReserveExternal(CharacterId c)
        {
            if (!owner.aftercare!.ReserveExternalOrders(W, c)) return false;
            if (!owner.reserved.Contains(c)) owner.reserved.Add(c);
            return true;
        }
        public void ReleaseExternal(CharacterId c)
        {
            owner.aftercare!.ReleaseExternalOrders(W, c);
            owner.reserved.Remove(c);
        }
        public bool HoldPending(CharacterId c)
        {
            if (owner.holds is null || !owner.holds.Hold(W, c, HoldOwner.AnnouncerPending)) return false;
            if (!owner.pendingHeld.Contains(c)) owner.pendingHeld.Add(c);
            return true;
        }
        public void ReleasePendingHold(CharacterId c)
        {
            owner.pendingHeld.Remove(c);
            owner.holds?.Release(W, c, HoldOwner.AnnouncerPending);
        }

        // ---------- orders ----------
        public (bool Hold, bool Passive) StandingOrders(CharacterId c) => PgEngine.HoldPassive(W, c);
        public void SetStandingOrders(CharacterId c, bool hold, bool passive) => PgEngine.SetStandingOrders(W, c, hold, passive);
        public byte SpeedOf(CharacterId c) => PgEngine.RunSpeedOf(W, c);
        public void SetSpeed(CharacterId c, byte speed) => PgEngine.SetRunSpeedMode(W, c, speed);
        public void SetRun(CharacterId c) => PgEngine.SetRunSpeed(W, c);
        public void ClearOrders(CharacterId c) => PgEngine.ClearOrders(W, c);
        public void EndWalk(CharacterId c) => PgEngine.EndWalk(W, c);
        // a facing is written only once the character has stopped (CAP 5: movement overwrites a walker's yaw); MoveDestination is null at rest
        public void Face(CharacterId c, Vector3 target) { if (PgEngine.MoveDestination(W, c) is null) PgEngine.Face(W, c, target); }
        public void WalkTo(CharacterId c, Vector3 target) => PgEngine.MoveTo(W, c, target);
        public void Replan(CharacterId c) => PgEngine.Replan(W, c);
        public void Say(CharacterId c, string text) => TownSpeech.Say(W, c, "announcer", text);
    }

    /// <summary>pg town announcer's object (TEST ONLY): the programme, variant, status, cue state, duration, approach, arena and spot, the speakers
    /// (position, target, yaw, engaged, owned, task), the pending releases, the reservations, the counters and the failure counts.</summary>
    internal string AnnouncerJson(World world)
    {
        static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        static string B(bool b) => b ? "true" : "false";
        static string F(float f) => TestJson.G(f);   // task 8.8: NaN / infinity -> null
        static string V(Vector3 v) => "[" + F(v.X) + "," + F(v.Y) + "," + F(v.Z) + "]";
        static string Id(CharacterId c) => PGLog.Quote(CharacterIdText.Format(c));
        string Building(ArenaBuildingRef? r)
        {
            if (r is not { } x || !Attached) return "null";
            var p = H(world).Resolve(x);
            return "{\"key\":" + PGLog.Quote(ArenaIdentity.KeyText(x)) + ",\"record\":" + PGLog.Quote(x.RecordId)
                + ",\"pos\":" + (p is { } q ? V(q.Building.Position) : "null") + "}";
        }
        string Person(TownAnnouncerLogic<CharacterId>.ControlledSpeaker s)
        {
            CharacterId c = s.Actor;
            var pos = PgEngine.Position(world, c);
            return "{\"id\":" + Id(c) + ",\"exists\":" + B(PgEngine.Exists(world, c)) + ",\"name\":" + PGLog.Quote(PgEngine.Name(world, c) ?? "")
                + ",\"record\":" + PGLog.Quote(PgEngine.RecordId(world, c) ?? "") + ",\"pos\":" + (pos is { } p ? V(p) : "null")
                + ",\"target\":" + V(s.Target) + ",\"distance\":" + (pos is { } d ? F(Vector3.Distance(d, s.Target)) : "null")
                + ",\"yaw\":" + F(PgEngine.Yaw(world, c)) + ",\"moving\":" + B(PgEngine.MoveDestination(world, c) is not null)
                + ",\"task\":" + PGLog.Quote(PgEngine.TaskText(world, c)) + ",\"engaged\":" + B(PgEngine.IsEngaged(world, c))
                + ",\"holdPassive\":" + B(PgEngine.HoldPassive(world, c) is (true, true))
                + ",\"owned\":" + B(s.Owned) + ",\"directed\":" + B(s.Directed) + "}";
        }
        var l = logic;
        var sb = new StringBuilder(1024);
        sb.Append(",\"announcer\":{\"program\":").Append(PGLog.Quote(l.Program.ToString())).Append(",\"variant\":").Append(N(l.Variant))
          .Append(",\"status\":").Append(PGLog.Quote(l.GetStatus())).Append(",\"started\":").Append(B(l.AnnouncementStarted))
          .Append(",\"lastCue\":").Append(N(l.LastCue)).Append(",\"cues\":").Append(N(TownAnnouncerPolicy.CueCount(l.Program, l.Variant)))
          .Append(",\"duration\":").Append(F(l.AnnouncementDuration()))
          .Append(",\"approach\":{\"elapsed\":").Append(F(l.ApproachElapsed)).Append(",\"best\":").Append(F(l.Progress.BestDistance))
          .Append(",\"stalled\":").Append(F(l.Progress.StalledElapsed)).Append('}')
          .Append(",\"arena\":").Append(Building(l.Arena)).Append(",\"spot\":").Append(Building(l.Spot))
          .Append(",\"announced\":").Append(N(l.Announced)).Append(",\"fallbacks\":").Append(N(l.Fallbacks))
          .Append(",\"lastFallback\":").Append(PGLog.Quote(l.LastFallback)).Append(",\"lines\":").Append(N(l.LinesSpoken))
          .Append(",\"speakers\":[");
        for (int i = 0; i < l.SpeakerCount; i++) { if (i > 0) sb.Append(','); sb.Append(Person(l.SpeakerAt(i))); }
        sb.Append("],\"pending\":[");
        for (int i = 0; i < l.PendingReleaseCount; i++) { if (i > 0) sb.Append(','); sb.Append(Person(l.PendingReleaseAt(i))); }
        sb.Append("],\"reserved\":[");
        for (int i = 0; i < reserved.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Id(reserved[i])); }
        sb.Append("],\"pendingHeld\":[");
        for (int i = 0; i < pendingHeld.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Id(pendingHeld[i])); }
        sb.Append("],\"failures\":{");
        for (int i = 0; i < stepFailures.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(PGLog.Quote(StepNames[i])).Append(':').Append(N(stepFailures[i]));
        }
        sb.Append(",\"withoutStaging\":").Append(N(orphanReleases)).Append(",\"restores\":").Append(N(restoreFailures)).Append("}}");
        return sb.ToString();
    }
}

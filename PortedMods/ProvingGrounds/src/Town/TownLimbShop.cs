using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Port of upstream/ProvingGrounds/plugin/src/TownLimbShop.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.5 (TW §2.10, CAP 5, 13, 16). The C++
// decisions are Core/Glue/TownLimbShopLogic.cs (fake-tested, Tests/Glue/TownLimbShopLogicTests.cs) over ITownLimbShopHost; this class binds them to a
// world and to CharacterIds. The state (C++ file-level globals, TownLimbShop.cpp:83-95) is the logic's, one object per world: ProvingGroundsSystem
// builds this object (built again for every world), TownArena attaches its services to it and clears it through its AbandonWorldState chain
// (TownArena.cpp:1802 -> AbandonWorldState :613-628). Nothing of the queue is saved (as the C++): after a load the queue is empty.
//
// Engine calls of the C++ (E1) and what replaces them:
//   ou->player->getAllPlayerCharacters() (IsPlayer :97-104) ... PgEngine.IsPlayer
//   Character::isValid / isDead / isUnconcious / isInCombatMode(true,true) / isBeingCarried / isCarryingSomething / getOrdersReciever (CanVisit
//       :129-135) ... PgEngine Exists / IsDead / IsUnconscious / InCombatMode / IsCarried / IsCarrying (every character takes orders)
//   getGameData()->stringID (Role :106-110) ... PgEngine.RecordId; getName, hand::toString ... PgEngine.Name, CharacterIdText.Format
//   getMedical()->getLimbState(kLimbs[i]) == LIMB_STUMP / LIMB_REPLACED (MissingLimbs :112-120, :338) ... Limbs.StumpMask / Limbs.Replaced
//       (MedicalState.LimbPart(slot).Limb)
//   TownAftercare::IsManaged / ReserveExternalOrders / ReleaseExternalOrders ... src/Town/TownAftercare.cs (the shared four external slots + the hold,
//       owner External)
//   getStandingOrder / setStandingOrder (HOLD, PASSIVE) ... PgEngine HoldPassive / SetStandingOrders
//   getMovementSpeedOrders / setDesiredSpeedOrders + restoreDesiredSpeed / setDesiredSpeed(RUN) ... PgEngine RunSpeedOf / SetRunSpeedMode / SetRunSpeed
//   addOrder(MOVE_CUS_ORDERED) + removeJob + clearAllAIGoals (RestoreActor :142-145) ... PgEngine.ClearOrders; reThinkCurrentAIAction ... the hold's
//       release (Ai.Replan)
//   removeJob(MOVE_CUS_ORDERED) + clearAllAIGoals + addGoal(STAND_STILL) (HoldFacing :188-192) ... PgEngine.EndWalk (a held NPC's AI chooses no goal)
//   lookatPosition(target, true) ... PgEngine.Face, written only once the character has stopped (CAP 5; again at every Tick: the logic's ReFace)
//   movement->setDestination + setDesiredSpeed(RUN) + addOrder(MOVE_CUS_ORDERED, target) + setDestination (IssueMove :197-214) ... PgEngine
//       SetStandingOrders(false, false) + SetRunSpeed + MoveTo (a MoveOrder: a player-order walk, TaskSource not Goal)
//   ou->getObjectsWithinSphere(registry, 2500, CHARACTER, 512) (FindTrader :216-233) ... PgEngine.CharactersNear (into one reused list);
//       getCurrentTownLocation ... Towns.CurrentTownOf; registry->getTown / getPosition / isValid ... ArenaIdentity.Resolve
//   Character::say ... TownSpeech.Say (PgEngine.Bark + the test-only say line "limbshop", CAP 16)
//   TownMatchmakingRuntime::Identity ... FighterIdentity.Find (task 7.2's Identity); LeaderboardStore::GetMatchCount(c, Town) ... LeaderboardStore
//   ou->gamedata.getData + type LIMB_REPLACEMENT, theFactory->createItem(quality) + Gear level, setRobotLimbItem (InstallCurrent :286-347) ...
//       Limbs.CanFit / RecordReady / Create / Fit (ItemFactory.Limb / Create, Level, MedicalState.FitReplacement, the limb section) + Limbs.Refresh
//       once (CharacterAssembly.RefreshEquipment, gated only on its two members, CAP 13); ou->destroy(item) ... nothing: an item in no inventory is not in the world (Items.Destroy's rule)
//   GetTickCount ... PgClock.NowMs; ou->isPaused ... false; PGLog::Debug / Error ... PGLog.Debug / Error (texts byte for byte)

/// <summary>The TownLimbShop of one world: the logic and its state, bound to a world for each call (sim thread, serial steps only).</summary>
sealed class TownLimbShop
{
    PopulationData? data;
    PgClock? clock;
    Towns? towns;
    TownAftercare? aftercare;
    Func<FighterIdentity>? identity;
    Func<LeaderboardStore>? store;
    readonly Host host;
    readonly TownLimbShopLogic<CharacterId> logic = new(CharacterId.None);
    /// <summary>Port only (the backstop): every NPC this shop reserved in the external table, until it gave it back.</summary>
    readonly List<CharacterId> reserved = [];

    internal TownLimbShop() => host = new Host(this);

    /// <summary>TownArena gives the shop its services when it is built (ProvingGroundsSystem builds this object first, for the save path).</summary>
    internal void Attach(PopulationData data, PgClock clock, Towns towns, TownAftercare aftercare, Func<FighterIdentity> identity, Func<LeaderboardStore> store)
    {
        this.data = data;
        this.clock = clock;
        this.towns = towns;
        this.aftercare = aftercare;
        this.identity = identity;
        this.store = store;
    }

    bool Attached => data is not null;

    internal TownLimbShopLogic<CharacterId> Logic => logic;

    ITownLimbShopHost<CharacterId> H(World world) => host.Bind(world);

    // Port only: the steps the glue catches (Begin, Tick, SuspendVisit), each logged once per world and counted (F10's TownLimbShop row, pg town
    // limbs "failures"). An order's own step catches its own throw (the logic's OrderFailures). After a throw, every reservation the shop took that is
    // not its visit's is given back (the backstop below).
    internal const int StepBegin = 0, StepTick = 1, StepSuspend = 2, StepRelease = 3;
    static readonly string[] StepNames = ["begin", "tick", "suspend", "release"];
    readonly bool[] stepLogged = new bool[4];
    readonly int[] stepFailures = new int[4];
    bool orderFailureLogged, refreshFailureLogged;

    void LogStep(int step, Exception e)
    {
        ++stepFailures[step];
        if (stepLogged[step]) return;
        stepLogged[step] = true;
        PGLog.Debug("Proving Grounds: limb shop " + StepNames[step] + " failed error=" + e.GetType().Name + ": " + e.Message);
        ProvingGroundsMod.Ctx?.LogError($"limb shop {StepNames[step]} failed: {e.GetType().Name}: {e.Message}");
    }

    // ---------- the public calls (TownLimbShop.h) ----------

    /// <summary>C++ :402-453 (TownArena.cpp:2040, at the fight's end).</summary>
    internal void Begin(World world, ArenaBuildingRef? registry, CharacterId[] participants, int count)
    {
        if (!Attached) return;
        try { logic.Begin(H(world), registry, participants, count); }
        catch (Exception e) { LogStep(StepBegin, e); }
    }

    /// <summary>C++ :455-569 (TownArena.cpp:1883 ignores the result), then the port's backstop.</summary>
    internal bool Tick(World world)
    {
        if (!Attached) return false;
        bool active;
        try { active = logic.Tick(H(world)); }
        catch (Exception e)
        {
            LogStep(StepTick, e);
            logic.ForgetActors();
            ReleaseOrphanReservations(world, all: true);
            return logic.IsActive();
        }
        if (reserved.Count > 0) ReleaseOrphanReservations(world, all: false);
        return active;
    }

    internal bool IsActive(World world) => logic.IsActive();                                        // C++ :571
    internal bool IsManaged(World world, CharacterId fighter) => Attached && logic.IsManaged(H(world), fighter);   // C++ :573-579
    internal bool IsVisitActive(World world) => logic.IsVisitActive();                              // C++ :581-584
    internal bool PhaseActive(World world) => logic.PhaseActive();                                  // C++ g_phase != Idle (SuspendVisit acts, :588)

    /// <summary>C++ :586-597 (WorldLifecycle.cpp:97, the manual save's prepare).</summary>
    internal void SuspendVisit(World world)
    {
        if (!Attached) return;
        try { logic.SuspendVisit(H(world)); }
        catch (Exception e)
        {
            LogStep(StepSuspend, e);
            logic.ForgetActors();
            ReleaseOrphanReservations(world, all: true);
        }
    }

    /// <summary>C++ :599-611 (no caller in the original; ported).</summary>
    internal void Release(World world)
    {
        if (!Attached) return;
        try { logic.Release(H(world)); }
        catch (Exception e) { LogStep(StepRelease, e); logic.ForgetActors(); }
        ReleaseOrphanReservations(world, all: true);
    }

    /// <summary>C++ :613-628: no actor access (the reservations and holds are cleared by TownAftercare's and the hold registry's own resets).</summary>
    internal void AbandonWorldState(World world)
    {
        logic.AbandonWorldState();
        reserved.Clear();
    }

    internal string GetStatus() => logic.GetStatus();                                               // C++ :630

    /// <summary>Port only (the backstop): every external reservation this shop took that is not the visit's fighter or trader is given back (a
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
            catch (Exception e) { LogStep(StepTick, e); }
        }
    }

    /// <summary>The F10 row's phase (sim thread, at publish): the phase, the status and the failure counts.</summary>
    internal string PhaseText()
    {
        var l = logic;
        int total = l.OrderFailures + l.InstallFailures + l.RefreshFailures + stepFailures[0] + stepFailures[1] + stepFailures[2] + stepFailures[3];
        string where = l.Phase == TownLimbShopPhase.Idle ? "idle" : l.Phase.ToString().ToLowerInvariant();
        return where + ": " + l.GetStatus() + (total == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
            $"; limb shop failures: begin {stepFailures[0]}, tick {stepFailures[1]}, suspend {stepFailures[2]}, release {stepFailures[3]}, orders {l.OrderFailures}, installs {l.InstallFailures}, refreshes {l.RefreshFailures}"));
    }

    /// <summary>ITownLimbShopHost over one world, rebound for every call.</summary>
    sealed class Host(TownLimbShop owner) : ITownLimbShopHost<CharacterId>
    {
        World? world;
        readonly List<CharacterId> nearby = new(512);

        internal Host Bind(World w) { world = w; return this; }

        World W => world ?? throw new InvalidOperationException("TownLimbShop used before a world was bound");
        PopulationData D => owner.data!;

        // ---------- the world ----------
        public bool IsPaused => false;   // a paused world runs no SlowWorld (World.Interface.cs:16-40)
        public uint NowMs => owner.clock!.NowMs;
        public void Debug(string line) => PGLog.Debug(line);
        public void Error(string line) => PGLog.Error(line);

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
        public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
        // the C++ hand's text (Begin's identity fallback, :426): the character's identity token read without creating one (FighterIdentity.TokenOf:
        // PortraitSerial + record), else the id text (spec review 8.5)
        public string ActorText(CharacterId c) => FighterIdentity.TokenOf(W, c) ?? CharacterIdText.Format(c);
        public Vector3 Position(CharacterId c) => PgEngine.Position(W, c) ?? Vector3.Zero;
        public long CurrentTown(CharacterId c) => Towns.Key(owner.towns!.CurrentTownOf(W, c));
        public IReadOnlyList<CharacterId> CharactersInSphere(Vector3 origin, float radius, int max)
        {
            PgEngine.CharactersNear(W, origin, radius, max, nearby);
            return nearby;
        }
        public uint StumpMask(CharacterId c) => Limbs.StumpMask(W, c);
        public bool LimbReplaced(CharacterId c, int slot) => Limbs.Replaced(W, c, slot);

        // ---------- other subsystems ----------
        public bool AftercareManages(CharacterId c) => owner.aftercare!.IsManaged(W, c);
        public string Identity(CharacterId c) => Valid(c) && owner.identity!().Find(W, c, out string id) ? id : "";   // TownMatchmakingRuntime::Identity
        public int TownMatchCount(CharacterId c) => owner.store!().GetMatchCount(W, c, LeaderboardData.Kind.Town);
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

        // ---------- buildings ----------
        // the held registry resolved again only when its key changed, a building was ruined or rebuilt (RuinsRevision) or 5 s passed (as the
        // corpse cleanup's: ArenaIdentity.Resolve builds a PgBuilding record)
        public PlacedBuilding? Resolve(ArenaBuildingRef held)
        {
            long revision = Beds.RuinsRevision(W);
            uint now = owner.clock!.NowMs;
            if (cached && cachedWorld == W && cachedRef == held && cachedRevision == revision && unchecked(now - cachedAt) < 5000) return cachedPlaced;
            cachedPlaced = ArenaIdentity.Resolve(W, D, held, out var town) is { } b ? new PlacedBuilding(b, Towns.Key(town)) : null;
            cached = true; cachedWorld = W; cachedRef = held; cachedRevision = revision; cachedAt = now;
            return cachedPlaced;
        }
        bool cached;
        World? cachedWorld;
        ArenaBuildingRef cachedRef;
        long cachedRevision;
        uint cachedAt;
        PlacedBuilding? cachedPlaced;

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
        public void Say(CharacterId c, string text) => TownSpeech.Say(W, c, "limbshop", text);

        // ---------- the fit ----------
        public bool FitAvailable => Limbs.Available;
        public bool LimbRecordReady(int slot) => Limbs.RecordReady(D, TownLimbShopLogic<CharacterId>.LimbData[slot], slot);
        public object? CreateLimb(int slot, int quality) => Limbs.Create(D, TownLimbShopLogic<CharacterId>.LimbData[slot], quality);
        public void DestroyItem(object item) { }   // an item in no inventory is not in the world (Items.Destroy's rule): nothing to delete
        public bool FitLimb(CharacterId fighter, int slot, object item) => item is ItemInstance i && Limbs.Fit(W, D, fighter, slot, i);
        public bool CanFit(CharacterId fighter, int slot) => Limbs.CanFit(W, fighter, slot);
        public bool RefreshEquipment(CharacterId fighter)
        {
            if (Limbs.Refresh(W, D, fighter, out string? error)) return true;
            if (!owner.refreshFailureLogged)
            {
                owner.refreshFailureLogged = true;
                PGLog.Debug("Proving Grounds: limb shop equipment refresh failed actor=" + CharacterIdText.Format(fighter) + " error=" + error);
                ProvingGroundsMod.Ctx?.LogError($"limb shop equipment refresh failed: {error}; the limb stays fitted");
            }
            return false;
        }
        public void OrderFailed(Exception e)
        {
            if (owner.orderFailureLogged) return;
            owner.orderFailureLogged = true;
            PGLog.Debug("Proving Grounds: limb shop order failed error=" + e.GetType().Name + ": " + e.Message);
            ProvingGroundsMod.Ctx?.LogError($"limb shop order failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>pg town limbs' object (TEST ONLY): the phase, the status, the visit (fighter, trader, target, approach), the orders with their grade,
    /// quality and lines, the reservations, the counters, and with an id that character's four limbs and limb-section items.</summary>
    internal string LimbsJson(World world, CharacterId? detail)
    {
        static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        static string B(bool b) => b ? "true" : "false";
        static string F(float f) => TestJson.G(f);   // task 8.8: NaN / infinity -> null
        static string V(Vector3 v) => "[" + F(v.X) + "," + F(v.Y) + "," + F(v.Z) + "]";
        static string Id(CharacterId c) => PGLog.Quote(CharacterIdText.Format(c));
        string Person(CharacterId c)
        {
            if (c.IsNone) return "null";
            var pos = PgEngine.Position(world, c);
            return "{\"id\":" + Id(c) + ",\"exists\":" + B(PgEngine.Exists(world, c)) + ",\"name\":" + PGLog.Quote(PgEngine.Name(world, c) ?? "")
                + ",\"record\":" + PGLog.Quote(PgEngine.RecordId(world, c) ?? "") + ",\"pos\":" + (pos is { } p ? V(p) : "null")
                + ",\"yaw\":" + F(PgEngine.Yaw(world, c)) + ",\"task\":" + PGLog.Quote(PgEngine.TaskText(world, c))
                + ",\"engaged\":" + B(PgEngine.IsEngaged(world, c)) + ",\"stumps\":" + N(Limbs.StumpMask(world, c)) + "}";
        }
        var l = logic;
        var sb = new StringBuilder(2048);
        sb.Append(",\"limbs\":{\"phase\":").Append(PGLog.Quote(l.Phase.ToString())).Append(",\"status\":").Append(PGLog.Quote(l.GetStatus()))
          .Append(",\"active\":").Append(B(l.IsActive())).Append(",\"visitActive\":").Append(B(l.IsVisitActive())).Append(",\"visible\":").Append(B(l.Visible))
          .Append(",\"available\":").Append(B(Limbs.Available)).Append(",\"current\":").Append(N(l.Current))
          .Append(",\"phaseElapsed\":").Append(F(l.PhaseElapsed)).Append(",\"target\":").Append(V(l.Target))
          .Append(",\"approach\":{\"elapsed\":").Append(F(l.Progress.Elapsed)).Append(",\"best\":").Append(F(l.Progress.BestDistance))
          .Append(",\"stalled\":").Append(F(l.Progress.Stalled)).Append('}')
          .Append(",\"fighter\":").Append(Person(l.FighterActor)).Append(",\"fighterOwned\":").Append(B(l.FighterOwned))
          .Append(",\"trader\":").Append(Person(l.TraderActor)).Append(",\"traderOwned\":").Append(B(l.TraderOwned))
          .Append(",\"fighterFaceAt\":").Append(l.FighterFaceAt is { } ff ? V(ff) : "null").Append(",\"traderFaceAt\":").Append(l.TraderFaceAt is { } tf ? V(tf) : "null")
          .Append(",\"installed\":").Append(N(l.Installed)).Append(",\"silentFits\":").Append(N(l.SilentFits))
          .Append(",\"installFailures\":").Append(N(l.InstallFailures)).Append(",\"orderFailures\":").Append(N(l.OrderFailures))
          .Append(",\"reserveRefused\":").Append(N(l.ReserveRefused)).Append(",\"suspended\":").Append(N(l.Suspended)).Append(",\"refreshFailures\":").Append(N(l.RefreshFailures)).Append(",\"fitsRefused\":").Append(N(l.FitsRefused))
          .Append(",\"failures\":{\"begin\":").Append(N(stepFailures[0])).Append(",\"tick\":").Append(N(stepFailures[1]))
          .Append(",\"suspend\":").Append(N(stepFailures[2])).Append(",\"release\":").Append(N(stepFailures[3])).Append('}')
          .Append(",\"orders\":[");
        for (int i = 0; i < l.OrderCount; i++)
        {
            var o = l.OrderAt(i);
            if (i > 0) sb.Append(',');
            var kind = (int)TownLimbShopPolicy.KindFor(o.LimbMask);
            sb.Append("{\"fighter\":").Append(Person(o.Fighter)).Append(",\"identity\":").Append(PGLog.Quote(o.Identity))
              .Append(",\"mask\":").Append(N(o.LimbMask)).Append(",\"grade\":").Append(PGLog.Quote(TownLimbShopPolicy.GradeName(o.Grade)))
              .Append(",\"quality\":").Append(N(TownLimbShopPolicy.QualityLevel(o.Grade))).Append(",\"variant\":").Append(N(o.Variant))
              .Append(",\"traderLine\":").Append(PGLog.Quote(TownLimbShopPolicy.TraderLine(o.Grade, o.Variant, kind)))
              .Append(",\"fighterLine\":").Append(PGLog.Quote(TownLimbShopPolicy.FighterLine(o.Grade, o.Variant, kind)))
              .Append(",\"retry\":").Append(F(o.RetryRemaining)).Append(",\"failures\":").Append(N(o.Failures)).Append('}');
        }
        sb.Append("],\"reserved\":[");
        for (int i = 0; i < reserved.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Id(reserved[i])); }
        sb.Append(']');
        if (detail is { } d) sb.Append(",\"detail\":{\"person\":").Append(Person(d)).Append(",\"body\":").Append(Limbs.Json(world, d)).Append('}');
        sb.Append('}');
        return sb.ToString();
    }
}

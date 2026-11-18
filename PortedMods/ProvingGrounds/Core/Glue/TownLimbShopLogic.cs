using System.Globalization;
using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/TownLimbShop.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.5 (TW §2.10, CAP 5, 13, 16): the C++
// decisions over ITownLimbShopHost (the character, registry and trader reads, the visit's orders, the speech, the fit). The glue
// (src/Town/TownLimbShop.cs) binds the host to a world and to CharacterIds; Core never sees an engine type. The state (the C++ file-level globals,
// TownLimbShop.cpp:83-95) is this object's, one per world (ProvingGroundsSystem builds it, TownArena's AbandonWorldState chain clears it). The pure
// rules are Core TownLimbShopPolicy, used as they are. Kept bug (docs/divergences.md "Kept bugs"): no money changes hands
// (TownLimbShopPolicy.Price has no caller).
//
// Port choices (docs/divergences.md "TownLimbShop (task 8.5)"):
//   - The C++ order receiver is the character; the reservation is TownAftercare's external table (the four slots the announcer, the limb shop and
//     the corpse cleanup share) plus the hold registry (owner External), both through the host.
//   - The fit is the host's: the record check (getData + LIMB_REPLACEMENT), the item made at the grade's quality (createItem + the gear level), the
//     fit on the stump (setRobotLimbItem = MedicalState.FitReplacement + the item in its limb section, a stump with an item already in that section
//     is refused as QuickMoves refuses it) and one equipment refresh after every stump of the order (CAP 13). A refused fit is rolled back like a
//     failed creation (the item made is let go).
//   - Facing (port only): the C++ STAND_STILL goal keeps a character looking at its point; Meitou's movement overwrites a walker's yaw, so the
//     face is written again at every Tick while the character is held facing (the host writes it only once the character has stopped, CAP 5).
//   - A repeating failure of one order (a failed install retried every 5 s, the silent fallback that comes before it) writes its C++ lines the
//     first time only and is counted every time; a throw while one order is worked on drops that order (counted, logged once by the glue), so
//     one bad fighter never stops the others.
//   - A missing fit member (the host's FitAvailable) turns the part off: Begin queues nothing (the glue logs it once).

/// <summary>C++ <c>enum Phase</c> (TownLimbShop.cpp:59).</summary>
public enum TownLimbShopPhase { Idle, Acquire, Approach, TraderSpeech, FighterSpeech, Install }

/// <summary>What TownLimbShop.cpp reads of the world and does to it. The glue implements it over PgEngine, the world's hold registry, TownAftercare,
/// FighterIdentity, LeaderboardStore, TownSpeech and the limb adapter (src/Engine/Limbs.cs); the fake is in Tests/Glue/TownLimbShopLogicTests.cs.</summary>
public interface ITownLimbShopHost<TId>
{
    // ---------- the world ----------
    /// <summary><c>ou-&gt;isPaused()</c>: always false in the port's step (a paused world runs no SlowWorld).</summary>
    bool IsPaused { get; }
    /// <summary><c>GetTickCount()</c>: the sim clock's milliseconds (frozen while paused).</summary>
    uint NowMs { get; }
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);
    /// <summary><c>PGLog::Error</c>.</summary>
    void Error(string line);

    // ---------- characters ----------
    TId None { get; }
    bool IsNull(TId c);
    /// <summary><c>c &amp;&amp; c-&gt;isValid()</c> (also <c>getOrdersReciever()</c>: every Meitou character takes orders).</summary>
    bool Valid(TId c);
    /// <summary>The C++ <c>IsPlayer(c)</c> (:97-104).</summary>
    bool IsPlayer(TId c);
    bool IsDead(TId c);
    bool IsUnconscious(TId c);
    /// <summary><c>c-&gt;isInCombatMode(true, true)</c>.</summary>
    bool InCombatMode(TId c);
    /// <summary><c>c-&gt;isBeingCarried()</c>.</summary>
    bool IsCarried(TId c);
    /// <summary><c>c-&gt;isCarryingSomething</c>.</summary>
    bool IsCarrying(TId c);
    /// <summary><c>c-&gt;getGameData()-&gt;stringID</c> ("" for a stale id: the C++ <c>Role</c> NULL).</summary>
    string RecordId(TId c);
    /// <summary><c>c-&gt;getName()</c>.</summary>
    string Name(TId c);
    /// <summary><c>hand::toString()</c> (the identity fallback of Begin :426).</summary>
    string ActorText(TId c);
    Vector3 Position(TId c);
    /// <summary><c>c-&gt;getCurrentTownLocation()</c> as a Core TownKey.</summary>
    long CurrentTown(TId c);
    /// <summary><c>ou-&gt;getObjectsWithinSphere(origin, r, CHARACTER, max)</c> (the host may reuse the list: read at once, never kept).</summary>
    IReadOnlyList<TId> CharactersInSphere(Vector3 origin, float radius, int max);
    /// <summary>The limbs whose <c>MedicalSystem::getLimbState</c> is <c>LIMB_STUMP</c>, bit i = limb slot i (0 left arm, 1 right arm, 2 left leg,
    /// 3 right leg; the C++ <c>MissingLimbs</c> loop :112-120 over <c>kLimbs</c>); 0 for a stale id or no medical state.</summary>
    uint StumpMask(TId c);
    /// <summary><c>medical-&gt;getLimbState(kLimbs[slot]) == LIMB_REPLACED</c>.</summary>
    bool LimbReplaced(TId c, int slot);

    // ---------- other subsystems ----------
    /// <summary><c>TownAftercare::IsManaged(c)</c>.</summary>
    bool AftercareManages(TId c);
    /// <summary><c>TownMatchmakingRuntime::Identity(c)</c> ("" for none).</summary>
    string Identity(TId c);
    /// <summary><c>LeaderboardStore::GetMatchCount(c, LeaderboardData::Town)</c>.</summary>
    int TownMatchCount(TId c);
    /// <summary><c>TownAftercare::ReserveExternalOrders(receiver)</c> (and the port's hold, owner External).</summary>
    bool ReserveExternal(TId c);
    /// <summary><c>TownAftercare::ReleaseExternalOrders(receiver)</c> (and the hold given back).</summary>
    void ReleaseExternal(TId c);

    // ---------- buildings ----------
    /// <summary>The held registry resolved now (<c>registry.getBuilding()</c> + <c>isValid()</c>; null: gone).</summary>
    PlacedBuilding? Resolve(ArenaBuildingRef registry);

    // ---------- orders ----------
    /// <summary><c>getStandingOrder(M_SET_ORDER_HOLD)</c> / <c>(PASSIVE)</c>.</summary>
    (bool Hold, bool Passive) StandingOrders(TId c);
    /// <summary><c>setStandingOrder(HOLD, hold)</c> + <c>(PASSIVE, passive)</c>.</summary>
    void SetStandingOrders(TId c, bool hold, bool passive);
    /// <summary><c>getMovementSpeedOrders()</c>.</summary>
    byte SpeedOf(TId c);
    /// <summary><c>setDesiredSpeedOrders(speed)</c> + <c>restoreDesiredSpeed()</c>.</summary>
    void SetSpeed(TId c, byte speed);
    /// <summary><c>setDesiredSpeedOrders(RUN)</c> + <c>setDesiredSpeed(RUN)</c>.</summary>
    void SetRun(TId c);
    /// <summary><c>addOrder(NULL, MOVE_CUS_ORDERED, NULL, false, true, pos)</c> + <c>removeJob</c> + <c>clearAllAIGoals</c> (RestoreActor :142-145):
    /// the order queue emptied and the task ended.</summary>
    void ClearOrders(TId c);
    /// <summary><c>removeJob(MOVE_CUS_ORDERED)</c> + <c>clearAllAIGoals()</c> (HoldFacing :188-189): a walk ended.</summary>
    void EndWalk(TId c);
    /// <summary><c>lookatPosition(target, true)</c> (port: written only once the character has stopped).</summary>
    void Face(TId c, Vector3 target);
    /// <summary><c>movement-&gt;setDestination</c> + <c>addOrder(MOVE_CUS_ORDERED, target)</c> + <c>setDestination</c> (IssueMove :207-212).</summary>
    void WalkTo(TId c, Vector3 target);
    /// <summary><c>c-&gt;say(text)</c> (PgEngine.Bark + the test-only say line "limbshop").</summary>
    void Say(TId c, string text);

    // ---------- the fit (InstallCurrent :286-347) ----------
    /// <summary>Port only: the fit's members resolve (false: the part is off; the glue logged it once).</summary>
    bool FitAvailable { get; }
    /// <summary><c>ou-&gt;gamedata.getData(kLimbData[slot])</c> exists and is a <c>LIMB_REPLACEMENT</c> (:303-304).</summary>
    bool LimbRecordReady(int slot);
    /// <summary><c>ou-&gt;theFactory-&gt;createItem(data, hand(), NULL, NULL, quality, NULL)</c> of a <c>LIMB_REPLACEMENT</c> class, with
    /// <c>level_0_100 = quality</c> (:314-330); null when the creation failed.</summary>
    object? CreateLimb(int slot, int quality);
    /// <summary><c>ou-&gt;destroy(item, false, "Proving Grounds limb order rollback")</c> (:311, :322).</summary>
    void DestroyItem(object item);
    /// <summary><c>medical-&gt;setRobotLimbItem(kLimbs[slot], item, false)</c> (:334): fitted on the stump and put into the limb's section; false
    /// (nothing done) when the section already holds an item or the anatomy has no such limb.</summary>
    bool FitLimb(TId fighter, int slot, object item);
    /// <summary>Port only: the fit's pre-check before any item is made (a stump, the anatomy has the limb, its section holds no item); a slot it
    /// refuses would be refused by <see cref="FitLimb"/> too.</summary>
    bool CanFit(TId fighter, int slot);
    /// <summary>Port only (CAP 13): the character's equipment rebuilt once after the order's fits (CharacterAssembly.RefreshEquipment); false when
    /// it did not run (the host logs the first failure).</summary>
    bool RefreshEquipment(TId fighter);
    /// <summary>Port only: one order's step threw (the glue logs the first and counts every one).</summary>
    void OrderFailed(Exception e);
}

/// <summary>The C++ TownLimbShop namespace's state and calls, one object per world.</summary>
public sealed class TownLimbShopLogic<TId>
{
    /// <summary>C++ :36-43.</summary>
    public const float SearchRadius = 2500.0f, ServiceDistance = 12.0f, ArrivalRadius = 15.0f, MoveRetrySeconds = 0.5f,
        SpeechDelaySeconds = 2.5f, InstallDelaySeconds = 2.5f, InstallRetrySeconds = 5.0f;
    public const uint ReadinessPollMilliseconds = 5000;
    /// <summary>C++ :221 (the trader search's cap).</summary>
    public const int SearchMax = 512;

    /// <summary>C++ <c>kLimbData</c> (:45-50): left arm, right arm, left leg, right leg.</summary>
    public static readonly string[] LimbData = [PgRecords.SkeletonArmLeft, PgRecords.SkeletonArmRight, PgRecords.SkeletonLegLeft, PgRecords.SkeletonLegRight];

    /// <summary>C++ <c>struct Order</c> (:61-71). Port only: <see cref="FailureLogged"/> (a repeating failure logs once), <see cref="Failures"/>.</summary>
    public struct Order
    {
        public TId Fighter;
        public ArenaBuildingRef? Registry;
        public string Identity;
        public uint LimbMask;
        public TownLimbShopPolicy.Grade Grade;
        public uint Variant;
        public float RetryRemaining;
        public bool FailureLogged;
        public int Failures;
    }

    /// <summary>C++ <c>struct ControlledActor</c> (:73-81); the receiver is the character. Port only: the facing point re-written every Tick.</summary>
    struct ControlledActor
    {
        public TId Actor;
        public bool Hold, Passive, Owned, Directed;
        public byte Speed;
        public bool Facing;
        public Vector3 FaceAt;
    }

    readonly List<Order> orders = [];                       // :83
    int current;                                            // :84
    ControlledActor fighter, trader;                        // :85-86
    TownLimbShopPhase phase = TownLimbShopPhase.Idle;           // :87
    Vector3 target;                                         // :88
    TownLimbShopPolicy.ApproachProgress progress = new();   // :89
    uint lastTick, lastReadinessCheck;                      // :90-91
    float phaseElapsed, nextMove;                           // :92-93
    bool visible;                                           // :94
    string status = "Robotics visits idle";                 // :95
    readonly TId none;
    readonly EqualityComparer<TId> eq = EqualityComparer<TId>.Default;
    bool acquiring;   // port only: AcquireCurrent has not chosen its order yet (a throw then drops no order)

    public TownLimbShopLogic(TId none)
    {
        this.none = none;
        fighter = Empty();
        trader = Empty();
    }

    ControlledActor Empty() => new() { Actor = none, Speed = 0 };

    // ---------- port only: what the test verb, the F10 row and the glue's backstop read ----------
    public TownLimbShopPhase Phase => phase;
    public int Current => current;
    public bool Visible => visible;
    public Vector3 Target => target;
    public TownLimbShopPolicy.ApproachProgress Progress => progress;
    public float PhaseElapsed => phaseElapsed;
    public int OrderCount => orders.Count;
    public Order OrderAt(int i) => orders[i];
    public TId FighterActor => fighter.Actor;
    public TId TraderActor => trader.Actor;
    public bool FighterOwned => fighter.Owned;
    public bool TraderOwned => trader.Owned;
    /// <summary>The point the fighter / trader is held facing (null when it is not held facing).</summary>
    public Vector3? FighterFaceAt => fighter.Facing ? fighter.FaceAt : null;
    public Vector3? TraderFaceAt => trader.Facing ? trader.FaceAt : null;
    /// <summary>Orders fitted (every stump replaced).</summary>
    public int Installed { get; private set; }
    /// <summary>Visits that ended in the silent fallback.</summary>
    public int SilentFits { get; private set; }
    /// <summary>Install attempts that did not replace every stump (each counted; the C++ lines logged once per order).</summary>
    public int InstallFailures { get; private set; }
    /// <summary>Orders dropped because their step threw.</summary>
    public int OrderFailures { get; private set; }
    /// <summary>Reservations the shared external table refused (a silent fit or a wait, as the C++).</summary>
    public int ReserveRefused { get; private set; }
    /// <summary>Equipment refreshes that did not run after a fit.</summary>
    public int RefreshFailures { get; private set; }
    /// <summary>Stumps the pre-check refused (no item made for them; the order is retried, as the C++ retries an incomplete install).</summary>
    public int FitsRefused { get; private set; }
    /// <summary>SuspendVisit calls that acted (the phase was not Idle).</summary>
    public int Suspended { get; private set; }

    /// <summary>Port only (the glue's backstop): <paramref name="c"/> is the visit's fighter or trader and its reservation is still ours on purpose.</summary>
    public bool Keeps(TId c)
    {
        if (eq.Equals(c, none)) return false;
        return (fighter.Owned && eq.Equals(fighter.Actor, c)) || (trader.Owned && eq.Equals(trader.Actor, c));
    }

    // C++: TownLimbShop.cpp:112-120 (the host reads the four limb states)
    static uint MissingLimbs(ITownLimbShopHost<TId> h, TId c) => h.IsNull(c) || !h.Valid(c) ? 0u : h.StumpMask(c) & 15u;

    // C++: TownLimbShop.cpp:122-127
    static int LimbCount(uint mask)
    {
        int count = 0;
        for (int i = 0; i < 4; ++i) if ((mask & (1u << i)) != 0) ++count;
        return count;
    }

    // C++: TownLimbShop.cpp:129-135
    static bool CanVisit(ITownLimbShopHost<TId> h, TId c)
    {
        return !h.IsNull(c) && h.Valid(c) && !h.IsDead(c) &&
            !h.IsUnconscious(c) && !h.InCombatMode(c) &&
            !h.IsCarried(c) && !h.IsCarrying(c) &&
            !h.AftercareManages(c);
    }

    // C++: TownLimbShop.cpp:137-159
    void RestoreActor(ITownLimbShopHost<TId> h, ref ControlledActor controlled)
    {
        TId c = controlled.Actor;
        if (controlled.Directed && !h.IsNull(c) && h.Valid(c))
        {
            h.ClearOrders(c);   // :142-145 addOrder(MOVE_CUS_ORDERED) + removeJob + clearAllAIGoals
            h.SetStandingOrders(c, controlled.Hold, controlled.Passive);
            h.SetSpeed(c, controlled.Speed);
            // reThinkCurrentAIAction (:154): the hold given back below sets Ai.Replan
        }
        if (controlled.Owned)
            h.ReleaseExternal(c);
        controlled = Empty();
    }

    // C++: TownLimbShop.cpp:161-165
    void ReleaseControlled(ITownLimbShopHost<TId> h)
    {
        RestoreActor(h, ref fighter);
        RestoreActor(h, ref trader);
    }

    // C++: TownLimbShop.cpp:167-182
    bool Capture(ITownLimbShopHost<TId> h, TId c, ref ControlledActor controlled)
    {
        if (h.IsNull(c) || !h.Valid(c)) return false;
        controlled.Actor = c;
        (controlled.Hold, controlled.Passive) = h.StandingOrders(c);
        controlled.Speed = h.SpeedOf(c);
        if (!h.ReserveExternal(c))
        {
            ++ReserveRefused;
            controlled = Empty();
            return false;
        }
        controlled.Owned = true;
        return true;
    }

    // C++: TownLimbShop.cpp:184-195
    static void HoldFacing(ITownLimbShopHost<TId> h, ref ControlledActor controlled, Vector3 at)
    {
        TId c = controlled.Actor;
        if (h.IsNull(c) || !h.Valid(c)) return;
        h.EndWalk(c);
        h.SetStandingOrders(c, true, true);
        // addGoal(STAND_STILL): the held character's AI chooses no goal (spike S10)
        h.Face(c, at);
        controlled.Directed = true;
        controlled.Facing = true;   // port only: written again at every Tick
        controlled.FaceAt = at;
    }

    // C++: TownLimbShop.cpp:197-214
    void IssueMove(ITownLimbShopHost<TId> h)
    {
        TId c = fighter.Actor;
        if (h.IsNull(c) || !h.Valid(c)) return;
        h.SetStandingOrders(c, false, false);
        h.SetRun(c);
        h.WalkTo(c, target);
        fighter.Directed = true;
        fighter.Facing = false;
    }

    // C++: TownLimbShop.cpp:216-233
    TId FindTrader(ITownLimbShopHost<TId> h, ArenaBuildingRef? registry)
    {
        if (registry is not { } r || h.Resolve(r) is not { } placed || placed.Town == TownKey.None) return none;
        Vector3 origin = placed.Building.Position;
        var nearby = h.CharactersInSphere(origin, SearchRadius, SearchMax);
        TId result = none;
        float nearest = 1.0e30f;
        for (int i = 0; i < nearby.Count; ++i)
        {
            TId candidate = nearby[i];
            // the record first (a string read), then the town (the status-town read is dearer)
            if (h.IsNull(candidate) || !h.Valid(candidate) || h.RecordId(candidate) != PgRecords.RoboticsTrader ||
                h.CurrentTown(candidate) != placed.Town || !CanVisit(h, candidate)) continue;
            float distance = (h.Position(candidate) - origin).LengthSquared();
            if (distance < nearest) { nearest = distance; result = candidate; }
        }
        return result;
    }

    // C++: TownLimbShop.cpp:235-244
    void BeginSilent(ITownLimbShopHost<TId> h, string reason)
    {
        ReleaseControlled(h);
        visible = false;
        phase = TownLimbShopPhase.Install;
        phaseElapsed = 0.0f;
        status = "Scratch mechanic fitting replacement limbs";
        ++SilentFits;
        // port only: a retried order's repeat of the line is not written (it is counted)
        if (current < orders.Count && orders[current].FailureLogged) return;
        h.Debug("Proving Grounds: limb shop silent fallback - " + reason);
    }

    // C++: TownLimbShop.cpp:246-254
    void WaitForFighter(ITownLimbShopHost<TId> h)
    {
        ReleaseControlled(h);
        visible = false;
        phase = TownLimbShopPhase.Acquire;
        phaseElapsed = 0.0f;
        lastReadinessCheck = h.NowMs;
        status = "Robotics visit pending until the fighter is available after medical care";
    }

    // C++: TownLimbShop.cpp:256-269
    void CompleteQueue(ITownLimbShopHost<TId> h)
    {
        // Called only after the last pending order was removed. No scanning or
        // visit ticking resumes until the next match-end check queues an amputee.
        current = 0;
        phase = TownLimbShopPhase.Idle;
        lastTick = 0;
        lastReadinessCheck = 0;
        phaseElapsed = 0.0f;
        nextMove = 0.0f;
        visible = false;
        status = "Robotics visits complete";
        h.Debug("Proving Grounds: limb shop queue stopped; pending=0; awaiting next match-end limb check");
    }

    // C++: TownLimbShop.cpp:271-284
    void Advance(ITownLimbShopHost<TId> h)
    {
        ReleaseControlled(h);
        if (current < orders.Count)
            orders.RemoveAt(current);
        current = 0;
        phaseElapsed = 0.0f;
        nextMove = 0.0f;
        lastReadinessCheck = 0;
        visible = false;
        if (orders.Count == 0) { CompleteQueue(h); return; }
        phase = TownLimbShopPhase.Acquire;
        status = "Next fighter heading to Scratch Robotics";
    }

    // C++: TownLimbShop.cpp:286-347
    bool InstallCurrent(ITownLimbShopHost<TId> h)
    {
        if (current >= orders.Count) return false;
        Order order = orders[current];
        TId f = order.Fighter;
        if (h.IsNull(f) || !h.Valid(f) || h.IsDead(f)) return false;
        if (!h.FitAvailable) return false;   // :292-293 no medical system or no factory

        uint mask = MissingLimbs(h, f);
        order.LimbMask = mask;
        orders[current] = order;
        if (mask == 0) return true;
        bool log = !order.FailureLogged;   // port only: a retried order's lines are written once
        object?[] created = new object?[4];   // per install attempt (at most every 5 s), not per poll
        int quality = TownLimbShopPolicy.QualityLevel(order.Grade);
        for (int i = 0; i < 4; ++i)
        {
            if ((mask & (1u << i)) == 0) continue;
            if (!h.CanFit(f, i)) { ++FitsRefused; continue; }   // port only: the fit would be refused; no item made for this attempt
            if (!h.LimbRecordReady(i))
            {
                if (log) h.Error("Proving Grounds: limb shop missing replacement data limb=" + Int(i) + " id=" + LimbData[i]);
                Rollback(h, created);
                return false;
            }
            created[i] = h.CreateLimb(i, quality);
            if (created[i] is null)
            {
                if (log) h.Error("Proving Grounds: limb shop item creation failed limb=" + Int(i) + " id=" + LimbData[i]);
                Rollback(h, created);
                return false;
            }
        }

        bool any = false;
        try
        {
            for (int i = 0; i < 4; ++i)
            {
                if (created[i] is not { } item) continue;
                if (h.FitLimb(f, i, item)) any = true;
                else h.DestroyItem(item);   // port only: a refused fit leaves the made item in no inventory
            }
        }
        finally
        {
            // port only (CAP 13): the fighter rebuilt once, also when a later fit threw after one fit succeeded
            if (any && !h.RefreshEquipment(f)) ++RefreshFailures;
        }

        bool installed = true;
        for (int i = 0; i < 4; ++i)
            if ((mask & (1u << i)) != 0 && !h.LimbReplaced(f, i))
                installed = false;
        if (installed || log)
        {
            string line = "Proving Grounds: limb shop purchase fighter=" + h.Name(f) + " identity=" + order.Identity +
                " grade=" + TownLimbShopPolicy.GradeName(order.Grade) + " quality=" + Int(quality) + " limbs=" + mask.ToString(CultureInfo.InvariantCulture) +
                " count=" + Int(LimbCount(mask)) + " scene=" + (visible ? "visible" : "silent") + " installed=" + (installed ? "1" : "0");
            if (installed) h.Debug(line); else h.Error(line);
        }
        return installed;
    }

    static void Rollback(ITownLimbShopHost<TId> h, object?[] created)
    {
        for (int j = 0; j < 4; ++j)
            if (created[j] is { } item) h.DestroyItem(item);
    }

    static string Int(int n) => n.ToString(CultureInfo.InvariantCulture);

    // C++: TownLimbShop.cpp:349-397
    void AcquireCurrent(ITownLimbShopHost<TId> h)
    {
        acquiring = true;
        for (int i = 0; i < orders.Count;)
        {
            TId pending = orders[i].Fighter;
            bool drop;
            try { drop = h.IsNull(pending) || !h.Valid(pending) || h.IsDead(pending) || MissingLimbs(h, pending) == 0; }
            catch (Exception e) { DropFailed(h, e); drop = true; }   // port only: one bad fighter never stops the others
            if (drop) orders.RemoveAt(i);
            else
                ++i;
        }
        if (orders.Count == 0) { acquiring = false; CompleteQueue(h); return; }

        int ready = orders.Count;
        for (int i = 0; i < orders.Count; ++i)
        {
            TId pending = orders[i].Fighter;
            bool can;
            try { can = CanVisit(h, pending); }
            catch (Exception e) { DropFailed(h, e); orders.RemoveAt(i--); continue; }
            if (orders[i].RetryRemaining <= 0.0f && can) { ready = i; break; }
        }
        if (orders.Count == 0) { acquiring = false; CompleteQueue(h); return; }
        if (ready == orders.Count)
        {
            acquiring = false;
            status = "Robotics visits pending until queued fighters finish medical care and can visit";
            return;
        }

        current = ready;
        acquiring = false;
        Order order = orders[current];
        TId f = order.Fighter;
        order.LimbMask = MissingLimbs(h, f);
        orders[current] = order;
        TId t = FindTrader(h, order.Registry);
        if (h.IsNull(t)) { BeginSilent(h, "Scratch Robotics Trader unavailable"); return; }
        if (!Capture(h, f, ref fighter)) { BeginSilent(h, "fighter AI ownership unavailable"); return; }
        if (!Capture(h, t, ref trader)) { BeginSilent(h, "trader AI ownership unavailable"); return; }

        Vector3 fighterPos = h.Position(f), traderPos = h.Position(t);
        Vector3 direction = fighterPos - traderPos;
        direction.Y = 0.0f;
        if (direction.LengthSquared() <= 1.0f) direction = new Vector3(1.0f, 0.0f, 0.0f);
        else direction = Vector3.Normalize(direction);
        target = traderPos + direction * ServiceDistance;
        float distance = (fighterPos - target).Length();
        TownLimbShopPolicy.ResetApproach(ref progress, distance);
        HoldFacing(h, ref trader, fighterPos);
        IssueMove(h);
        visible = true;
        phase = TownLimbShopPhase.Approach;
        phaseElapsed = 0.0f;
        nextMove = MoveRetrySeconds;
        status = h.Name(f) + " is heading to Scratch Robotics";
    }

    void DropFailed(ITownLimbShopHost<TId> h, Exception e)
    {
        ++OrderFailures;
        h.OrderFailed(e);
    }

    // ---------- the public calls (TownLimbShop.h) ----------

    // C++: TownLimbShop.cpp:402-453
    public void Begin(ITownLimbShopHost<TId> h, ArenaBuildingRef? registry, TId[]? fighters, int count)
    {
        if (registry is not { } r || h.Resolve(r) is null || fighters is null || count <= 0) return;
        if (!h.FitAvailable) return;   // port only: the part is off (the glue logged it once)
        bool wasIdle = phase == TownLimbShopPhase.Idle;
        uint queued = 0;
        for (int i = 0; i < count && i < fighters.Length; ++i)
        {
            // port only: one fighter whose reads throw is skipped (counted, logged once), the others are still queued
            try { if (QueueFighter(h, r, fighters[i])) ++queued; }
            catch (Exception e) { DropFailed(h, e); }
        }
        h.Debug("Proving Grounds: limb shop match-end check participants=" + Int(count) + " newlyQueued=" + queued.ToString(CultureInfo.InvariantCulture) +
            " pending=" + Int(orders.Count));
        if (queued == 0) return;
        if (wasIdle)
        {
            current = 0;
            phase = TownLimbShopPhase.Acquire;
            lastTick = h.NowMs;
            lastReadinessCheck = 0;
            status = "Fighters queued for Scratch Robotics";
            h.Debug("Proving Grounds: limb shop queue started after match-end limb loss check");
        }
        h.Debug("Proving Grounds: limb shop added " + queued.ToString(CultureInfo.InvariantCulture) + " fighter visits; " + Int(orders.Count) + " pending");
    }

    // C++: TownLimbShop.cpp:409-433 (the loop body of Begin): true when an order was added
    bool QueueFighter(ITownLimbShopHost<TId> h, ArenaBuildingRef r, TId f)
    {
        if (h.IsNull(f) || !h.Valid(f) || h.IsDead(f) || h.IsPlayer(f)) return false;
        bool duplicate = false;
        for (int j = 0; j < orders.Count; ++j)
            if (eq.Equals(orders[j].Fighter, f)) duplicate = true;
        if (duplicate) return false;
        uint mask = MissingLimbs(h, f);
        if (mask == 0) return false;
        string role = h.RecordId(f);
        TownFighterCatalog.Entry? regular = role.Length > 0 ? TownFighterCatalog.Find(role) : null;
        int namedDivision = TownChallengePolicy.NamedRoleDivision(role);
        int division = regular != null ? regular.Tier : namedDivision >= 0 ? namedDivision : 0;
        bool legendary = regular == null && namedDivision == 2;
        var order = new Order { Fighter = f, Registry = r };
        order.Identity = h.Identity(f);
        if (order.Identity.Length == 0) order.Identity = h.ActorText(f);
        order.LimbMask = mask;
        uint seed = TownLimbShopPolicy.Seed(order.Identity, h.TownMatchCount(f), mask);
        order.Grade = TownLimbShopPolicy.GradeFor(division, legendary, seed);
        order.Variant = (seed >> 8) % 3u;
        orders.Add(order);
        return true;
    }

    // C++: TownLimbShop.cpp:455-569
    public bool Tick(ITownLimbShopHost<TId> h)
    {
        if (phase == TownLimbShopPhase.Idle) return false;
        uint now = h.NowMs;
        float dt = lastTick != 0 ? unchecked(now - lastTick) / 1000.0f : 0.0f;
        lastTick = now;
        if (h.IsPaused) return true;
        if (dt < 0.0f) dt = 0.0f;
        if (dt > 1.0f) dt = 1.0f;
        phaseElapsed += dt;
        for (int i = 0; i < orders.Count; ++i)
            if (orders[i].RetryRemaining > 0.0f)
            {
                Order o = orders[i];
                o.RetryRemaining -= dt;
                orders[i] = o;
            }
        try { TickPhase(h, now, dt); }
        catch (Exception e) { FailCurrent(h, e); }
        return phase != TownLimbShopPhase.Idle;
    }

    // C++: TownLimbShop.cpp:469-567 (the body of Tick after the clocks)
    void TickPhase(ITownLimbShopHost<TId> h, uint now, float dt)
    {
        if (phase == TownLimbShopPhase.Acquire)
        {
            if (lastReadinessCheck != 0 && unchecked(now - lastReadinessCheck) < ReadinessPollMilliseconds)
                return;
            lastReadinessCheck = now;
            AcquireCurrent(h);
            return;
        }

        TId f = current < orders.Count ? orders[current].Fighter : none;
        if (h.IsNull(f) || !h.Valid(f) || h.IsDead(f))
        {
            Advance(h);
            return;
        }

        if (visible && phase != TownLimbShopPhase.Install)
        {
            TId t = trader.Actor;
            if (!CanVisit(h, t))
            {
                BeginSilent(h, "trader interrupted during visit");
                return;
            }
            if (!CanVisit(h, f))
            {
                WaitForFighter(h);
                return;
            }
        }

        ReFace(h);   // port only

        if (phase == TownLimbShopPhase.Approach)
        {
            TId t = trader.Actor;
            float distance = (h.Position(f) - target).Length();
            TownLimbShopPolicy.UpdateApproach(ref progress, distance, dt);
            if (distance <= ArrivalRadius)
            {
                HoldFacing(h, ref fighter, h.Position(t));
                HoldFacing(h, ref trader, h.Position(f));
                Order order = orders[current];
                h.Say(t, TownLimbShopPolicy.TraderLine(order.Grade, order.Variant, (int)TownLimbShopPolicy.KindFor(order.LimbMask)));
                phase = TownLimbShopPhase.TraderSpeech;
                phaseElapsed = 0.0f;
                status = "Scratch Robotics is quoting a replacement order";
                return;
            }
            if (TownLimbShopPolicy.ApproachStalled(in progress))
            {
                BeginSilent(h, "shop approach stalled for 20 seconds");
                return;
            }
            if (phaseElapsed >= nextMove)
            {
                IssueMove(h);
                nextMove = phaseElapsed + MoveRetrySeconds;
            }
            return;
        }

        if (phase == TownLimbShopPhase.TraderSpeech && phaseElapsed >= SpeechDelaySeconds)
        {
            Order order = orders[current];
            h.Say(f, TownLimbShopPolicy.FighterLine(order.Grade, order.Variant, (int)TownLimbShopPolicy.KindFor(order.LimbMask)));
            phase = TownLimbShopPhase.FighterSpeech;
            phaseElapsed = 0.0f;
            status = h.Name(f) + " is buying replacement limbs";
            return;
        }

        if (phase == TownLimbShopPhase.FighterSpeech && phaseElapsed >= InstallDelaySeconds)
        {
            phase = TownLimbShopPhase.Install;
            phaseElapsed = 0.0f;
        }

        if (phase == TownLimbShopPhase.Install)
        {
            if (!CanVisit(h, f)) { WaitForFighter(h); return; }
            if (InstallCurrent(h)) { ++Installed; Advance(h); }
            else
            {
                ReleaseControlled(h);
                // Keep the failed order, but let other fighters and saves proceed.
                Order retry = orders[current];
                retry.RetryRemaining = InstallRetrySeconds;
                bool log = !retry.FailureLogged;
                retry.FailureLogged = true;   // port only
                ++retry.Failures;             // port only
                ++InstallFailures;
                orders.RemoveAt(current);
                orders.Add(retry);
                current = 0;
                phase = TownLimbShopPhase.Acquire;
                visible = false;
                phaseElapsed = 0.0f;
                lastReadinessCheck = 0;
                status = "Replacement limb installation failed; queued order will retry";
                if (log) h.Error("Proving Grounds: limb shop installation incomplete; order retained; retry in 5 unpaused seconds");
            }
        }
    }

    /// <summary>Port only: the C++ STAND_STILL goal keeps a held character looking at its point; Meitou's movement does not, so the face is
    /// written again (the host writes it only once the character has stopped).</summary>
    void ReFace(ITownLimbShopHost<TId> h)
    {
        if (fighter.Facing && !h.IsNull(fighter.Actor) && h.Valid(fighter.Actor)) h.Face(fighter.Actor, fighter.FaceAt);
        if (trader.Facing && !h.IsNull(trader.Actor) && h.Valid(trader.Actor)) h.Face(trader.Actor, trader.FaceAt);
    }

    /// <summary>Port only: a throw while one order was worked on. The visit's actors are given back (a throw there clears them; the glue's backstop
    /// gives back what is left), the order being worked on is dropped (none when AcquireCurrent had not chosen one yet), and the queue waits for
    /// its next 5 s poll.</summary>
    void FailCurrent(ITownLimbShopHost<TId> h, Exception e)
    {
        DropFailed(h, e);
        try { ReleaseControlled(h); }
        catch { fighter = Empty(); trader = Empty(); }
        if (!(phase == TownLimbShopPhase.Acquire && acquiring) && current < orders.Count) orders.RemoveAt(current);
        acquiring = false;
        current = 0;
        visible = false;
        phaseElapsed = 0.0f;
        nextMove = 0.0f;
        if (orders.Count == 0) { try { CompleteQueue(h); } catch { phase = TownLimbShopPhase.Idle; } return; }
        phase = TownLimbShopPhase.Acquire;
        lastReadinessCheck = h.NowMs;
        status = "Next fighter heading to Scratch Robotics";
    }

    // C++: TownLimbShop.cpp:571
    public bool IsActive() => phase != TownLimbShopPhase.Idle;

    // C++: TownLimbShop.cpp:573-579
    public bool IsManaged(ITownLimbShopHost<TId> h, TId c)
    {
        if (h.IsNull(c)) return false;
        for (int i = 0; i < orders.Count; ++i)
            if (eq.Equals(orders[i].Fighter, c)) return true;
        return false;
    }

    // C++: TownLimbShop.cpp:581-584
    public bool IsVisitActive() => visible || fighter.Owned || trader.Owned || phase == TownLimbShopPhase.Install;

    /// <summary>The C++ <c>g_phase != Idle</c> the save safety reads (SuspendVisit acts, :588).</summary>
    public bool PhaseActive() => phase != TownLimbShopPhase.Idle;

    // C++: TownLimbShop.cpp:586-597
    public void SuspendVisit(ITownLimbShopHost<TId> h)
    {
        if (phase == TownLimbShopPhase.Idle) return;
        ++Suspended;
        ReleaseControlled(h);
        visible = false;
        current = 0;
        phase = TownLimbShopPhase.Acquire;
        phaseElapsed = 0.0f;
        nextMove = 0.0f;
        lastReadinessCheck = 0;
        status = "Robotics visits resume after saving";
    }

    // C++: TownLimbShop.cpp:599-611
    public void Release(ITownLimbShopHost<TId> h)
    {
        ReleaseControlled(h);
        orders.Clear();
        current = 0;
        phase = TownLimbShopPhase.Idle;
        lastTick = 0;
        lastReadinessCheck = 0;
        phaseElapsed = 0.0f;
        nextMove = 0.0f;
        visible = false;
        status = "Robotics visits idle";
    }

    /// <summary>Port only: after a Release or a SuspendVisit that threw, the visit's actors are forgotten (the glue gives back every reservation).</summary>
    public void ForgetActors()
    {
        fighter = Empty();
        trader = Empty();
    }

    // C++: TownLimbShop.cpp:613-628
    public void AbandonWorldState()
    {
        // TownAftercare owns and clears the shared reservation table during
        // world teardown. Actor/receiver pointers may already be invalid here.
        fighter = Empty();
        trader = Empty();
        orders.Clear();
        current = 0;
        phase = TownLimbShopPhase.Idle;
        lastTick = 0;
        lastReadinessCheck = 0;
        phaseElapsed = 0.0f;
        nextMove = 0.0f;
        visible = false;
        status = "Robotics visits idle";
        acquiring = false;
    }

    // C++: TownLimbShop.cpp:630
    public string GetStatus() => status;
}

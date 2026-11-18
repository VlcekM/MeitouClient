using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/TownCorpseCleanup.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.6 (TW §2.11, CAP 12, 14, spike S9):
// the C++ decisions over ITownCorpseCleanupHost (the character, building and furnace reads, the handler's orders, the disposal). The glue
// (src/Town/TownCorpseCleanup.cs) binds the host to a world and to CharacterIds; Core never sees an engine type. The state (the C++ file-level globals,
// TownCorpseCleanup.cpp:21-47) is this object's, one per world (TownArena builds it, AbandonWorldState clears it). The pure rules are Core
// TownCorpseCleanupPolicy, used as they are.
//
// Port choices (docs/divergences.md "TownCorpseCleanup (task 8.6)"):
//   - The feed (spike S9: Meitou steps the corpse errand for player characters only, ProductionSystem.Errands.cs:146): the C++'s
//     FEED_CORPSE_INTO_MACHINE order is a RUN walk to the furnace re-ordered every 15 s (the C++'s order cadence), and once the handler is at the
//     furnace (the game's own AtSite rule, the glue) the host's Dispose runs the engine's Incinerate steps (LetGo, the label floater, Uniques.Left,
//     Characters.Remove) in the serial step that called Tick.
//   - A body someone else carries or that lies in a bed or a cage (SanitationCrew's Removal checks) and a body that is gone are resolved, never an
//     error (Meitou 0.20.2's own town corpse duties may take it first).
//   - The C++ order receiver is the character; the reservation is TownAftercare's external table plus the hold registry (owner External).
//   - A throw while one corpse is worked on resolves that corpse (counted, logged once by the glue), so one bad body never stops the others.

/// <summary>A corpse-disposal building (BUILDING function 11, <c>BF_CORPSE_DISPOSAL</c>) as the host reads it now: its building key, BUILDING record, position, town
/// (a Core TownKey), whether it is built (<c>getBuildState()-&gt;isComplete</c>), its functionality's label (the floater, e.g. "Incinerating") and
/// its use range (the arrival rule).</summary>
public readonly record struct CorpseFurnace(string Key, string Record, Vector3 Position, long Town, bool Complete, string Label, float UseRange);

/// <summary>What the host's disposal did with a body at the furnace.</summary>
public enum CorpseDisposalResult
{
    /// <summary>Let go, the floater shown, gone from the world (Incinerate).</summary>
    Removed,
    /// <summary>The body no longer exists.</summary>
    Gone,
    /// <summary>Someone else carries it, or it lies in a bed or a cage (nothing done).</summary>
    NotOurs,
    /// <summary>The handler does not carry it (nothing done).</summary>
    NotCarried,
    /// <summary>The removal's members are missing or a step refused (nothing removed).</summary>
    Failed,
}

/// <summary>What TownCorpseCleanup.cpp reads of the world and does to it. The glue implements it over PgEngine, the world's hold registry,
/// TownAftercare's external table, TownSpectators and the removal adapter (src/Engine/Corpses.cs); the fake is in
/// Tests/Glue/TownCorpseCleanupLogicTests.cs.</summary>
public interface ITownCorpseCleanupHost<TId>
{
    // ---------- the world ----------
    /// <summary><c>ou-&gt;isPaused()</c>: always false in the port's step (a paused world runs no SlowWorld).</summary>
    bool IsPaused { get; }
    /// <summary><c>GetTickCount()</c>: the sim clock's milliseconds (frozen while paused).</summary>
    uint NowMs { get; }
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);

    // ---------- characters ----------
    TId None { get; }
    bool IsNull(TId c);
    /// <summary><c>c &amp;&amp; c-&gt;isValid()</c> (also <c>getOrdersReciever()</c>: every Meitou character takes orders).</summary>
    bool Valid(TId c);
    /// <summary>The C++ <c>Player(c)</c> (:49-54).</summary>
    bool IsPlayer(TId c);
    bool IsDead(TId c);
    bool IsUnconscious(TId c);
    /// <summary><c>c-&gt;isInCombatMode(true, true)</c>.</summary>
    bool InCombatMode(TId c);
    /// <summary><c>c-&gt;isBeingCarried()</c>.</summary>
    bool IsCarried(TId c);
    /// <summary><c>c-&gt;isCarryingSomething</c>.</summary>
    bool IsCarrying(TId c);
    /// <summary><c>c-&gt;getCarryingObject().getCharacter()</c> (None when it carries nobody).</summary>
    TId CarryingOf(TId c);
    /// <summary>Port only (SanitationCrew's Removal check): the body lies in a bed or a cage.</summary>
    bool InBedOrCage(TId body);
    /// <summary><c>c-&gt;isBeingCarried()</c>'s carrier (None when nobody, or a stale carrier, carries it).</summary>
    TId CarrierOf(TId body);
    /// <summary><c>ArenaMedical::NeedsStabilization(c)</c>.</summary>
    bool NeedsStabilization(TId c);
    /// <summary><c>c-&gt;getGameData()-&gt;stringID</c> ("" for a stale id).</summary>
    string RecordId(TId c);
    /// <summary><c>c-&gt;getName()</c>.</summary>
    string Name(TId c);
    /// <summary><c>hand::toString()</c>.</summary>
    string ActorText(TId c);
    Vector3 Position(TId c);
    /// <summary><c>c-&gt;getCurrentTownLocation()</c> as a Core TownKey.</summary>
    long CurrentTown(TId c);
    /// <summary><c>ou-&gt;getObjectsWithinSphere(origin, r, CHARACTER, max)</c> (the host may reuse the list: read at once, never kept).</summary>
    IReadOnlyList<TId> CharactersInSphere(Vector3 origin, float radius, int max);

    // ---------- buildings ----------
    /// <summary>The held arena building resolved now (<c>activeArena.getBuilding()</c> + <c>isValid()</c>; null: gone).</summary>
    PlacedBuilding? Resolve(ArenaBuildingRef arena);
    /// <summary>The held furnace resolved now (null: gone).</summary>
    CorpseFurnace? ResolveFurnace(string key);
    /// <summary><c>ou-&gt;getObjectsWithinSphere(origin, r, BUILDING, max)</c> filtered to the corpse-disposal function (the host may reuse the list).</summary>
    IReadOnlyList<CorpseFurnace> FurnacesInSphere(Vector3 origin, float radius, int max);

    // ---------- other subsystems ----------
    /// <summary><c>TownAftercare::IsManaged(c)</c>.</summary>
    bool AftercareManages(TId c);
    /// <summary><c>TownSpectators::ReleaseCharacter(c)</c>.</summary>
    void SpectatorsReleaseCharacter(TId c);
    /// <summary><c>TownAftercare::ReserveExternalOrders(receiver)</c> (and the port's hold, owner External).</summary>
    bool ReserveExternal(TId c);
    /// <summary><c>TownAftercare::ReleaseExternalOrders(receiver)</c> (and the hold given back).</summary>
    void ReleaseExternal(TId c);

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
    /// <summary><c>addOrder(NULL, MOVE_CUS_ORDERED, NULL, false, true, pos)</c> + <c>removeJob</c> + <c>clearAllAIGoals</c> (RestoreNative :93-95): the
    /// order queue emptied and the task ended.</summary>
    void ClearOrders(TId c);
    /// <summary><c>addOrder(NULL, LIFT_PERSON_PLAYER_ORDER, corpse, ...)</c>: false when the engine refused the lift.</summary>
    bool BeginLift(TId c, TId corpse);
    /// <summary><c>dropCarriedObject(true, false)</c>: false when nothing is carried.</summary>
    bool LetGo(TId c);
    /// <summary>The walk half of <c>addOrder(furnace, FEED_CORPSE_INTO_MACHINE, ...)</c> (spike S9): a RUN walk to the furnace.</summary>
    void WalkTo(TId c, Vector3 position);
    /// <summary>The engine's arrival rule for the feed (ProductionSystem.AtSite / SiteReach): the handler is at the furnace.</summary>
    bool AtFurnace(TId c, CorpseFurnace furnace);
    /// <summary>Port only: the removal's members resolve (false: no body is ever fed; the batch ends at its 180 s limit).</summary>
    bool DisposalAvailable { get; }
    /// <summary>The feed half (spike S9, Incinerate's steps): LetGo, the label floater, Uniques.Left, Characters.Remove. Serial step (SlowWorld).
    /// The host writes the port's "fed" line for every result that ends the body's turn (Removed, Gone, NotOurs), and for a result that repeats
    /// (NotCarried, Failed) only when <paramref name="log"/> is true (the logic's first time for this body).</summary>
    CorpseDisposalResult Dispose(TId handler, TId body, CorpseFurnace furnace, bool log);
    /// <summary>Port only: one corpse's step threw (the glue logs the first and counts every one).</summary>
    void CorpseFailed(Exception e);
}

/// <summary>The C++ TownCorpseCleanup namespace's state and calls, one object per world.</summary>
public sealed class TownCorpseCleanupLogic<TId>
{
    /// <summary>C++ :159, :184 (the furnace and handler search radius).</summary>
    public const float SearchRadius = 2500.0f;
    /// <summary>C++ :239 (the final sweep's radius).</summary>
    public const float SweepRadius = 175.0f;
    /// <summary>C++ :301 (the poll), :345 / :352 (the order cadence), :319 (the batch limit).</summary>
    public const uint PollMs = 500, OrderMs = 15000;
    public const float BatchLimitSeconds = 180.0f;

    struct Corpse { public TId Actor; public bool Resolved; public bool DisposeFailed; }   // DisposeFailed: port only (a repeating failure is counted and logged once)
    struct DeferredHandler { public TId Actor; public bool Hold, Passive; public byte Speed; }

    readonly List<Corpse> corpses = [];                     // :35
    readonly List<DeferredHandler> deferredHandlers = [];   // :36
    ArenaBuildingRef? activeArena;                          // :37
    CorpseFurnace? furnace;                                 // :37
    TId handler;                                            // :37
    int current = -1;                                       // :38
    bool active, sealed_, directed;                         // :39
    bool batchStarted, batchExpired;                        // :40
    bool oldHold, oldPassive;                               // :41
    byte oldSpeed;                                          // :42 (the C++ WALK: the glue's first read replaces it)
    TId reservedReceiver;                                   // :43 (the receiver is the character)
    uint lastTick, lastPoll, lastOrder;                     // :44
    float batchElapsed;                                     // :45
    TownCorpseCleanupPolicy.Action lastAction = TownCorpseCleanupPolicy.Action.Wait;   // :46
    string status = "";                                     // :47
    readonly TId none;
    readonly EqualityComparer<TId> eq = EqualityComparer<TId>.Default;

    public TownCorpseCleanupLogic(TId none)
    {
        this.none = none;
        handler = none;
        reservedReceiver = none;
    }

    // ---------- port only: what the test verb and the F10 row read ----------
    /// <summary>Bodies the handler fed into the furnace (this world).</summary>
    public int Disposed { get; private set; }
    /// <summary>Lift orders the engine refused (the C++'s addOrder cannot fail; a refusal waits for the next 15 s order).</summary>
    public int LiftRefused { get; private set; }
    /// <summary>Corpses whose disposal removed nothing (a step refused), each counted once.</summary>
    public int DisposeFailures { get; private set; }
    /// <summary>Corpses whose step threw (each resolved; the glue logged the first).</summary>
    public int CorpseFailures { get; private set; }
    /// <summary>Corpses resolved because someone else carried them or they lay in a bed or a cage.</summary>
    public int TakenElsewhere { get; private set; }
    public bool Active => active;
    public bool Sealed => sealed_;
    public bool BatchStarted => batchStarted;
    public bool BatchExpired => batchExpired;
    public float BatchElapsed => batchElapsed;
    public TId Handler => handler;
    public bool Directed => directed;
    public int Current => current;
    public CorpseFurnace? Furnace => furnace;
    public int CorpseCount => corpses.Count;
    public TId CorpseAt(int i) => corpses[i].Actor;
    public bool CorpseResolved(int i) => corpses[i].Resolved;
    public int DeferredCount => deferredHandlers.Count;
    public TId DeferredAt(int i) => deferredHandlers[i].Actor;
    public TownCorpseCleanupPolicy.Action LastAction => lastAction;

    /// <summary>Port only (the Finish backstop): <paramref name="c"/> is this cleanup's handler or a deferred handler (its reservation is still ours
    /// on purpose).</summary>
    public bool Keeps(TId c)
    {
        if (eq.Equals(c, none)) return false;
        if (directed && eq.Equals(c, handler)) return true;
        for (int i = 0; i < deferredHandlers.Count; i++) if (eq.Equals(deferredHandlers[i].Actor, c)) return true;
        return false;
    }

    // C++: TownCorpseCleanup.cpp:56-61
    static bool IsCageHandler(ITownCorpseCleanupHost<TId> h, TId c)
    {
        if (h.IsNull(c) || !h.Valid(c)) return false;
        return TownSpectatorPolicy.RoleFor(h.RecordId(c)) == TownSpectatorPolicy.AudienceRole.CageHandler;
    }

    // C++: TownCorpseCleanup.cpp:63-72 (the building's function, construction and disabled flag are the host's furnace read: a CorpseFurnace is a
    // corpse-disposal building by construction; a Meitou town building has no disabled flag)
    // (port: the arena's town is resolved once per call of DiscoverFurnace and passed in, not once per candidate)
    static bool Disposal(CorpseFurnace? b, long arenaTown)
    {
        if (b is not { } f) return false;
        return TownCorpseCleanupPolicy.FurnaceReady(true, TownKey.Same(f.Town, arenaTown), f.Complete, false, true);
    }

    /// <summary>The arena's town resolved by the last DiscoverFurnace (TownKey.None: the arena is gone).</summary>
    long arenaTown = TownKey.None;

    // C++: TownCorpseCleanup.cpp:74-78
    void SetStatus(ITownCorpseCleanupHost<TId> h, string next)
    {
        if (status == next) return;
        status = next;
        h.Debug("Proving Grounds: corpse cleanup - " + status);
    }

    // C++: TownCorpseCleanup.cpp:80-86
    static TownCorpseCleanupPolicy.ReleaseDisposition ReleaseDispositionOf(ITownCorpseCleanupHost<TId> h, TId c)
    {
        if (h.IsNull(c) || !h.Valid(c)) return TownCorpseCleanupPolicy.ReleaseDisposition.ForgetRelease;
        if (h.IsPlayer(c)) return TownCorpseCleanupPolicy.ReleaseDisposition.ForgetRelease;
        return TownCorpseCleanupPolicy.ReleaseFor(true, false, h.IsDead(c),
            h.IsUnconscious(c), h.InCombatMode(c), h.IsCarried(c),
            h.NeedsStabilization(c), h.IsCarrying(c));
    }

    // C++: TownCorpseCleanup.cpp:88-104
    static void RestoreNative(ITownCorpseCleanupHost<TId> h, TId c, bool hold, bool passive, byte speed)
    {
        if (h.IsNull(c) || !h.Valid(c) || h.IsPlayer(c))
        {
            h.ReleaseExternal(c);
            return;
        }
        h.ClearOrders(c);   // :93-95 addOrder(MOVE_CUS_ORDERED) + removeJob + clearAllAIGoals
        h.SetStandingOrders(c, hold, passive);
        h.SetSpeed(c, speed);
        // The ownership hook must stop suppressing chooseGoal before native AI
        // is asked to select its next town job.
        h.ReleaseExternal(c);   // the hold given back: Engaged off + Ai.Replan (reThinkCurrentAIAction :103)
    }

    // C++: TownCorpseCleanup.cpp:106-124
    void TickDeferredHandlers(ITownCorpseCleanupHost<TId> h)
    {
        for (int i = 0; i < deferredHandlers.Count;)
        {
            DeferredHandler pending = deferredHandlers[i];
            TId c = pending.Actor;
            var disposition = ReleaseDispositionOf(h, c);
            if (disposition == TownCorpseCleanupPolicy.ReleaseDisposition.DeferRelease && !h.IsNull(c) &&
                !h.IsUnconscious(c) && !h.InCombatMode(c) && !h.IsCarried(c) &&
                !h.NeedsStabilization(c) && h.IsCarrying(c))
            {
                TId carried = h.CarryingOf(c);
                if (!h.IsNull(carried) && h.Valid(carried) && h.IsDead(carried)) h.LetGo(c);
                disposition = ReleaseDispositionOf(h, c);
            }
            if (disposition == TownCorpseCleanupPolicy.ReleaseDisposition.DeferRelease) { ++i; continue; }
            if (disposition == TownCorpseCleanupPolicy.ReleaseDisposition.ResumeNative)
                RestoreNative(h, c, pending.Hold, pending.Passive, pending.Speed);
            else h.ReleaseExternal(c);
            deferredHandlers.RemoveAt(i);
        }
    }

    // C++: TownCorpseCleanup.cpp:126-144
    void ResumeHandler(ITownCorpseCleanupHost<TId> h)
    {
        TId c = handler;
        if (directed)
        {
            var disposition = ReleaseDispositionOf(h, c);
            if (disposition == TownCorpseCleanupPolicy.ReleaseDisposition.ResumeNative)
                RestoreNative(h, reservedReceiver, oldHold, oldPassive, oldSpeed);
            else if (disposition == TownCorpseCleanupPolicy.ReleaseDisposition.DeferRelease)
                deferredHandlers.Add(new DeferredHandler { Actor = handler, Hold = oldHold, Passive = oldPassive, Speed = oldSpeed });
            else h.ReleaseExternal(reservedReceiver);
        }
        else if (!h.IsNull(reservedReceiver)) h.ReleaseExternal(reservedReceiver);   // the C++ releases NULL as a no-op
        reservedReceiver = none;
        directed = false;
        handler = none;
        lastOrder = 0;
        lastAction = TownCorpseCleanupPolicy.Action.Wait;
    }

    // C++: TownCorpseCleanup.cpp:146-151
    void ResolveCurrent(ITownCorpseCleanupHost<TId> h, string message)
    {
        if (current >= 0 && current < corpses.Count) { var c = corpses[current]; c.Resolved = true; corpses[current] = c; }
        ResumeHandler(h);
        current = -1;
        SetStatus(h, message);
    }

    // C++: TownCorpseCleanup.cpp:153-167
    void DiscoverFurnace(ITownCorpseCleanupHost<TId> h)
    {
        PlacedBuilding? arena = activeArena is { } a ? h.Resolve(a) : null;
        arenaTown = arena is { } ap ? ap.Town : TownKey.None;
        if (furnace is { } held)
        {
            CorpseFurnace? now = h.ResolveFurnace(held.Key);
            if (Disposal(now, arenaTown)) { furnace = now; return; }
        }
        furnace = null;
        if (arena is not { } placed) return;
        Vector3 origin = placed.Building.Position;
        var nearby = h.FurnacesInSphere(origin, SearchRadius, 1024);
        float best = SearchRadius * SearchRadius;
        for (int i = 0; i < nearby.Count; ++i)
        {
            CorpseFurnace candidate = nearby[i];
            if (!Disposal(candidate, arenaTown)) continue;
            float distance = Vector3.DistanceSquared(candidate.Position, origin);
            if (distance < best) { best = distance; furnace = candidate; }
        }
    }

    // C++: TownCorpseCleanup.cpp:169-175
    static bool HandlerOperational(ITownCorpseCleanupHost<TId> h, TId c, bool carryingTarget)
    {
        if (h.IsNull(c) || !h.Valid(c) || h.IsPlayer(c)) return false;
        return !h.IsCarried(c) &&
            !h.NeedsStabilization(c) && TownCorpseCleanupPolicy.HandlerReady(
                true, false, h.IsDead(c), h.IsUnconscious(c), h.InCombatMode(c),
                h.IsCarrying(c) && !carryingTarget);
    }

    // C++: TownCorpseCleanup.cpp:177-201
    void DiscoverHandler(ITownCorpseCleanupHost<TId> h)
    {
        TId existing = handler;
        if (HandlerOperational(h, existing, false) && IsCageHandler(h, existing)) return;
        ResumeHandler(h);
        PlacedBuilding? arena = activeArena is { } a ? h.Resolve(a) : null;
        if (arena is not { } placed) return;
        var nearby = h.CharactersInSphere(placed.Building.Position, SearchRadius, 512);
        for (int i = 0; i < nearby.Count; ++i)
        {
            TId candidate = nearby[i];
            if (!h.Valid(candidate)) continue;
            if (!IsCageHandler(h, candidate) || !TownKey.Same(h.CurrentTown(candidate), placed.Town) ||
                !HandlerOperational(h, candidate, false) || h.AftercareManages(candidate)) continue;
            h.SpectatorsReleaseCharacter(candidate);
            if (!h.ReserveExternal(candidate)) continue;
            handler = candidate;
            reservedReceiver = candidate;
            (oldHold, oldPassive) = h.StandingOrders(candidate);
            oldSpeed = h.SpeedOf(candidate);
            h.SetStandingOrders(candidate, false, false);
            directed = true;
            return;
        }
    }

    /// <summary>Port only (controller decision 8.6, SanitationCrew's Removal checks): the body lies in a bed or a cage, or a character carries it
    /// that is neither this cleanup's handler, nor a deferred handler (it drops a dead body once it can, TickDeferredHandlers :113-115, and the
    /// body stays pending for the next handler, as in the C++), nor one the aftercare manages (a medic: the C++ keeps such a body pending too).</summary>
    bool HeldElsewhere(ITownCorpseCleanupHost<TId> h, TId corpse)
    {
        if (h.InBedOrCage(corpse)) return true;
        TId carrier = h.CarrierOf(corpse);
        if (h.IsNull(carrier)) return false;
        if (directed && eq.Equals(carrier, handler)) return false;
        for (int i = 0; i < deferredHandlers.Count; i++) if (eq.Equals(deferredHandlers[i].Actor, carrier)) return false;
        return !h.AftercareManages(carrier);
    }

    // C++: TownCorpseCleanup.cpp:203-208
    bool CorpsePending(ITownCorpseCleanupHost<TId> h, int index)
    {
        if (index < 0 || index >= corpses.Count || corpses[index].Resolved) return false;
        TId c = corpses[index].Actor;
        if (h.IsNull(c) || !h.Valid(c)) return false;
        return TownCorpseCleanupPolicy.EligibleCorpse(true, true, h.IsDead(c), false);
    }

    // C++: TownCorpseCleanup.cpp:210-215
    int NextCorpse(ITownCorpseCleanupHost<TId> h)
    {
        if (CorpsePending(h, current)) return current;
        for (int i = 0; i < corpses.Count; ++i)
            if (CorpsePending(h, i)) return i;
        return -1;
    }

    // C++: TownCorpseCleanup.cpp:219-229
    public void Begin(ITownCorpseCleanupHost<TId> h, ArenaBuildingRef arena, IReadOnlyList<TId> fighters, int count)
    {
        Release(h);
        if (h.Resolve(arena) is null) return;
        activeArena = arena;
        for (int i = 0; i < count && i < fighters.Count; ++i)
            if (!h.IsNull(fighters[i])) corpses.Add(new Corpse { Actor = fighters[i] });
        active = corpses.Count > 0;
        DiscoverFurnace(h);
        SetStatus(h, furnace is null ? "waiting for a nearby corpse furnace" : "watching registered fighters");
    }

    // C++: TownCorpseCleanup.cpp:231
    public void Seal() => sealed_ = true;

    // C++: TownCorpseCleanup.cpp:233-283
    public int AdmitArenaDeaths(ITownCorpseCleanupHost<TId> h)
    {
        PlacedBuilding? arena = activeArena is { } a ? h.Resolve(a) : null;
        if (arena is not { } placed) return 0;

        // Match the medical final sweep: include bodies against the edge of
        // the 155-unit combat layout without reaching the medic standby line.
        var nearby = h.CharactersInSphere(placed.Building.Position, SweepRadius, 256);
        int admitted = 0;
        for (int i = 0; i < nearby.Count; ++i)
        {
            TId corpse = nearby[i];
            if (h.IsNull(corpse) || !h.Valid(corpse)) continue;
            if (!h.IsDead(corpse) || h.IsPlayer(corpse) || h.IsCarried(corpse)) continue;

            bool known = false;
            for (int existing = 0; existing < corpses.Count; ++existing)
                if (eq.Equals(corpses[existing].Actor, corpse)) { known = true; break; }
            if (known) continue;

            corpses.Add(new Corpse { Actor = corpse });
            ++admitted;
            Vector3 p = h.Position(corpse);
            h.Debug("Proving Grounds: final arena death sweep admitted name=" + h.Name(corpse) + " actor=" + h.ActorText(corpse) +
                " slot=" + ((uint)corpses.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " position=(" + CMath.FormatF(p.X, 1) + "," + CMath.FormatF(p.Y, 1) + "," + CMath.FormatF(p.Z, 1) + ")");
        }
        if (admitted > 0)
        {
            active = true;
            if (batchExpired)
            {
                batchExpired = false;
                batchStarted = false;
                batchElapsed = 0.0f;
                current = -1;
                lastOrder = 0;
                lastAction = TownCorpseCleanupPolicy.Action.Wait;
            }
            DiscoverFurnace(h);
            SetStatus(h, "final arena sweep found " + admitted.ToString(System.Globalization.CultureInfo.InvariantCulture) + " dead NPC" +
                (admitted == 1 ? "" : "s"));
        }
        return admitted;
    }

    // C++: TownCorpseCleanup.cpp:285-289
    public bool HasPending(ITownCorpseCleanupHost<TId> h)
    {
        if (!active || batchExpired) return false;
        for (int i = 0; i < corpses.Count; ++i) if (CorpsePending(h, i)) return true;
        return false;
    }

    // C++: TownCorpseCleanup.cpp:291-358
    public void Tick(ITownCorpseCleanupHost<TId> h)
    {
        TickDeferredHandlers(h);
        if (!active || batchExpired) return;
        uint now = h.NowMs;
        if (h.IsPaused) { lastTick = now; return; }
        float delta = lastTick != 0 ? unchecked(now - lastTick) / 1000.0f : 0.0f;
        lastTick = now;
        if (batchStarted)
            batchElapsed = TownCorpseCleanupPolicy.AdvanceElapsed(batchElapsed, delta, false);
        if (lastPoll != 0 && unchecked(now - lastPoll) < PollMs) return;
        lastPoll = now;
        int next = NextCorpse(h);
        if (next < 0)
        {
            if (current >= 0) ResolveCurrent(h, "corpse accepted by furnace");
            if (sealed_) SetStatus(h, "corpse cleanup complete");
            return;
        }
        try { Step(h, now, next); }
        catch (Exception e)
        {
            // port only: one bad corpse never stops the others (logged once and counted by the glue)
            ++CorpseFailures;
            h.CorpseFailed(e);
            if (current < 0) current = next;
            try { ResolveCurrent(h, "corpse cleanup step failed"); }
            catch (Exception) { handler = none; reservedReceiver = none; directed = false; current = -1; }
        }
    }

    // C++: TownCorpseCleanup.cpp:309-357 (Tick's per-corpse part)
    void Step(ITownCorpseCleanupHost<TId> h, uint now, int next)
    {
        if (current != next)
        {
            ResumeHandler(h);
            current = next;
            batchStarted = true;
            lastOrder = 0;
            TId named = corpses[current].Actor;
            SetStatus(h, "dispatching cage handler for " + (!h.IsNull(named) && h.Valid(named) ? h.Name(named) : "fighter"));
        }
        TId corpse = corpses[current].Actor;
        if (h.IsNull(corpse) || !h.Valid(corpse)) { ResolveCurrent(h, "corpse no longer present"); return; }
        if (TownCorpseCleanupPolicy.BatchTimedOut(batchStarted, batchElapsed, BatchLimitSeconds))
        {
            batchExpired = true;
            for (int i = 0; i < corpses.Count; ++i) { var entry = corpses[i]; entry.Resolved = true; corpses[i] = entry; }
            ResumeHandler(h);
            current = -1;
            SetStatus(h, "corpse cleanup timed out; remaining bodies released to town AI");
            return;
        }
        // port only (controller decision 8.6, SanitationCrew's Removal checks): a body another carrier took or that lies in a bed or a cage is resolved
        if (HeldElsewhere(h, corpse))
        {
            ++TakenElsewhere;
            ResolveCurrent(h, "corpse taken by someone else");
            return;
        }
        DiscoverFurnace(h);
        if (!Disposal(furnace, arenaTown)) { SetStatus(h, "dead fighter waiting: no usable corpse furnace nearby"); return; }
        TId c = handler;
        bool carryingTarget = !h.IsNull(c) && h.Valid(c) && h.IsCarrying(c) && eq.Equals(h.CarryingOf(c), corpse);
        if (!HandlerOperational(h, c, carryingTarget) || !IsCageHandler(h, c))
        {
            DiscoverHandler(h);
            c = handler;
            carryingTarget = !h.IsNull(c) && h.Valid(c) && h.IsCarrying(c) && eq.Equals(h.CarryingOf(c), corpse);
        }
        if (h.IsNull(c) || !directed) { SetStatus(h, "dead fighter waiting: no cage handler available"); return; }
        bool carryingOther = h.IsCarrying(c) && !carryingTarget;
        var action = TownCorpseCleanupPolicy.NextAction(
            HandlerOperational(h, c, carryingTarget), true, carryingTarget, carryingOther);
        if (action == TownCorpseCleanupPolicy.Action.Wait) return;
        if (action != lastAction) { lastAction = action; lastOrder = 0; }
        h.SetRun(c);
        if (action == TownCorpseCleanupPolicy.Action.PickUp)
        {
            if (lastOrder == 0 || unchecked(now - lastOrder) >= OrderMs)
            {
                lastOrder = now;
                if (!h.BeginLift(c, corpse)) ++LiftRefused;
                SetStatus(h, "cage handler collecting " + h.Name(corpse));
            }
            return;
        }
        CorpseFurnace target = furnace!.Value;
        // spike S9's fallback: the engine's feed step once the handler is at the furnace (Incinerate, ProductionSystem.Errands.cs:375-383)
        if (h.DisposalAvailable && h.AtFurnace(c, target))
        {
            Corpse entry = corpses[current];
            var result = h.Dispose(c, corpse, target, log: !entry.DisposeFailed);
            switch (result)
            {
                case CorpseDisposalResult.Removed: ++Disposed; return;   // gone: the next poll's NextCorpse resolves it ("corpse accepted by furnace")
                case CorpseDisposalResult.Gone: ResolveCurrent(h, "corpse no longer present"); return;
                case CorpseDisposalResult.NotOurs: ++TakenElsewhere; ResolveCurrent(h, "corpse taken by someone else"); return;
                default:
                    // NotCarried (the next poll lifts it again) or Failed (nothing removed; the walk order below is given again in 15 s): logged by
                    // the host and counted only the first time for this corpse
                    if (!entry.DisposeFailed)
                    {
                        if (result == CorpseDisposalResult.Failed) ++DisposeFailures;
                        entry.DisposeFailed = true;
                        corpses[current] = entry;
                    }
                    break;
            }
        }
        if (lastOrder == 0 || unchecked(now - lastOrder) >= OrderMs)
        {
            lastOrder = now;
            h.WalkTo(c, target.Position);
            SetStatus(h, "cage handler taking " + h.Name(corpse) + " to the corpse furnace");
        }
    }

    // C++: TownCorpseCleanup.cpp:360
    public string GetStatus() => status;

    // C++: TownCorpseCleanup.cpp:362-374
    public void Release(ITownCorpseCleanupHost<TId> h)
    {
        ResumeHandler(h);
        corpses.Clear();
        activeArena = null;
        furnace = null;
        arenaTown = TownKey.None;
        current = -1;
        active = sealed_ = batchStarted = batchExpired = false;
        reservedReceiver = none;
        lastTick = lastPoll = lastOrder = 0;
        batchElapsed = 0.0f;
        lastAction = TownCorpseCleanupPolicy.Action.Wait;
        status = "";
    }

    /// <summary>Port only: Release threw; forget the handler and the deferred list (the glue gives back every reservation this cleanup took).</summary>
    public void ResetAfterFailedRelease()
    {
        AbandonWorldState();
    }

    // C++: TownCorpseCleanup.cpp:376-386 (no actor access)
    public void AbandonWorldState()
    {
        corpses.Clear(); deferredHandlers.Clear();
        activeArena = null; furnace = null; handler = none; arenaTown = TownKey.None;
        current = -1;
        active = sealed_ = directed = batchStarted = batchExpired = false;
        reservedReceiver = none;
        lastTick = lastPoll = lastOrder = 0;
        batchElapsed = 0.0f;
        lastAction = TownCorpseCleanupPolicy.Action.Wait;
        status = "";
    }
}

using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/TownAnnouncer.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.3 (TW §2.8, CAP 5, 16, 19): the C++
// decisions over ITownAnnouncerHost (the character, arena and Announcer Spot reads, the speakers' orders, the lines). The glue
// (src/Town/TownAnnouncer.cs) binds the host to a world and to CharacterIds; Core never sees an engine type. The state (the C++ file-level globals,
// TownAnnouncer.cpp:53-65) is this object's, one per world (ProvingGroundsSystem builds it; ArenaIngress's and TownArena's AbandonWorldState clear
// it). The pure rules (the cast, its fallbacks, the variants, the cue times, the texts, the approach tracker, the release plans) are Core
// TownAnnouncerPolicy, used as they are. Kept bug K2 (docs/divergences.md "Kept bugs"): the Senn-and-Torka pair only for challengeSlot 5.
//
// Port choices (docs/divergences.md "TownAnnouncer (task 8.3)"):
//   - The C++ order receiver is the character; the reservation is TownAftercare's external table (the four slots the announcer, the limb shop and
//     the corpse cleanup share) plus the hold registry (owner External), both through the host.
//   - The facing (lookatPosition) is written by the host only once the speaker has stopped (CAP 5); HoldAtStand runs at every tick as in the C++,
//     so the yaw is written again once the walk has ended.
//   - Port only: a throw in a step is caught by the glue, which forgets the cast (ForgetAll) and gives back every reservation this announcer took;
//     the C++ cannot throw.

/// <summary>What TownAnnouncer.cpp reads of the world and does to it. The glue implements it over PgEngine, the world's hold registry,
/// TownAftercare's external table and TownSpeech; the fake is in Tests/Glue/TownAnnouncerLogicTests.cs.</summary>
public interface ITownAnnouncerHost<TId>
{
    // ---------- the world ----------
    /// <summary><c>ou-&gt;isPaused()</c>: always false in the port's step (a paused world runs no serial step).</summary>
    bool IsPaused { get; }
    /// <summary><c>GetTickCount()</c>: the sim clock's milliseconds (frozen while paused).</summary>
    uint NowMs { get; }
    /// <summary><c>ou-&gt;getTimeStamp_inGameHours().getTotalHours()</c>.</summary>
    double GameHours { get; }
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);

    // ---------- characters ----------
    TId None { get; }
    bool IsNull(TId c);
    /// <summary><c>c &amp;&amp; c-&gt;isValid()</c> (also <c>getOrdersReciever()</c>: every Meitou character takes orders).</summary>
    bool Valid(TId c);
    /// <summary>The C++ <c>IsPlayer(c)</c> (:67-76).</summary>
    bool IsPlayer(TId c);
    bool IsDead(TId c);
    bool IsUnconscious(TId c);
    /// <summary><c>c-&gt;isInCombatMode(true, true)</c>.</summary>
    bool InCombatMode(TId c);
    /// <summary><c>c-&gt;isBeingCarried()</c>.</summary>
    bool IsCarried(TId c);
    /// <summary><c>c-&gt;isCarryingSomething</c>.</summary>
    bool IsCarrying(TId c);
    /// <summary><c>c-&gt;getGameData()-&gt;stringID</c> ("" for a stale id).</summary>
    string RecordId(TId c);
    /// <summary>Port only (shared brief, census): the character belongs to the town faction 91 (Bloodstamp).</summary>
    bool IsTownFaction(TId c);
    Vector3 Position(TId c);
    /// <summary><c>c-&gt;getCurrentTownLocation()</c> as a Core TownKey.</summary>
    long CurrentTown(TId c);
    /// <summary><c>ou-&gt;getObjectsWithinSphere(origin, r, CHARACTER, max)</c> (the host may reuse the list: read at once, never kept).</summary>
    IReadOnlyList<TId> CharactersInSphere(Vector3 origin, float radius, int max);

    // ---------- buildings ----------
    /// <summary>A held building resolved now (<c>hand.getBuilding()</c> + <c>isValid()</c>; null: gone).</summary>
    PlacedBuilding? Resolve(ArenaBuildingRef building);
    /// <summary><c>ou-&gt;getObjectsWithinSphere(origin, r, BUILDING, max)</c> (FindSpot :148-150); the host may list only the Announcer Spot
    /// record (the only one the caller keeps) and reuse the list.</summary>
    IReadOnlyList<PlacedBuilding> SpotsInSphere(Vector3 origin, float radius, int max);

    // ---------- other subsystems ----------
    /// <summary><c>TownAftercare::ReserveExternalOrders(receiver)</c> (and the port's hold, owner External).</summary>
    bool ReserveExternal(TId c);
    /// <summary><c>TownAftercare::ReleaseExternalOrders(receiver)</c> (and the hold given back).</summary>
    void ReleaseExternal(TId c);
    /// <summary>Port only: the deferred release's own hold (owner AnnouncerPending), taken before the external hold goes back so the speaker is not
    /// replanned while its saved orders wait (the C++ does not rethink it until TickReleases). False when the hold was refused.</summary>
    bool HoldPending(TId c);
    /// <summary>Port only: the deferred release's hold given back (the AI decides again when no owner is left).</summary>
    void ReleasePendingHold(TId c);

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
    /// <summary><c>addOrder(NULL, MOVE_CUS_ORDERED, NULL, false, true, pos)</c> + <c>removeJob</c> + <c>clearAllAIGoals</c>
    /// (PrepareNativeResume :262-265): the order queue emptied and the task ended.</summary>
    void ClearOrders(TId c);
    /// <summary><c>removeJob(MOVE_CUS_ORDERED)</c> + <c>clearAllAIGoals()</c> (HoldAtStand :230-231): a walk ended.</summary>
    void EndWalk(TId c);
    /// <summary><c>lookatPosition(target, true)</c> (port: written only once the character has stopped).</summary>
    void Face(TId c, Vector3 target);
    /// <summary><c>movement-&gt;setDestination</c> + <c>addOrder(MOVE_CUS_ORDERED, target)</c> + <c>setDestination</c> (IssueMove :251-256).</summary>
    void WalkTo(TId c, Vector3 target);
    /// <summary><c>reThinkCurrentAIAction()</c>: the native AI decides again.</summary>
    void Replan(TId c);
    /// <summary><c>c-&gt;say(text)</c> (PgEngine.Bark + the test-only say line "announcer").</summary>
    void Say(TId c, string text);
}

/// <summary>The C++ TownAnnouncer namespace's state and calls, one object per world.</summary>
public sealed class TownAnnouncerLogic<TId>
{
    /// <summary>C++ :33-36.</summary>
    public const float ArrivalRadius = 15.0f, SpeakerSpacing = 16.0f, MoveRetrySec = 0.50f, SearchRadius = 2500.0f;
    /// <summary>C++ :120, :150 (the two sphere queries' caps).</summary>
    public const int CharacterSearchMax = 512, BuildingSearchMax = 1024;

    /// <summary>C++ <c>struct ControlledSpeaker</c> (:38-51); the receiver is the character.</summary>
    public struct ControlledSpeaker
    {
        public TId Actor;
        public bool Hold, Passive, Owned, Directed;
        public byte Speed;
        public Vector3 Target;
    }

    TownAnnouncerPolicy.Program program = TownAnnouncerPolicy.Program.LegacyCountdown;   // :53
    readonly List<ControlledSpeaker> speakers = new(2);                                    // :54
    readonly List<ControlledSpeaker> pendingReleases = new(4);                             // :55
    ArenaBuildingRef? arena;                                                              // :56
    ArenaBuildingRef? spot;                                                               // :57
    float approachElapsed;                                                                // :58
    TownAnnouncerPolicy.ApproachProgress approachProgress = new();                         // :59
    float nextMove;                                                                       // :60
    uint lastTick;                                                                        // :61
    int lastCue = -1;                                                                     // :62
    int variant;                                                                          // :63
    bool announcementStarted;                                                             // :64
    string status = "Legacy fighter countdown";                                           // :65
    readonly TId none;
    readonly EqualityComparer<TId> eq = EqualityComparer<TId>.Default;
    readonly List<TId> actors = new(2);   // Begin's local list (:299), kept to allocate nothing per bout

    public TownAnnouncerLogic(TId none) => this.none = none;

    // ---------- port only: what the test verb, the F10 row and the glue's backstop read ----------
    public TownAnnouncerPolicy.Program Program => program;
    public int Variant => variant;
    public int LastCue => lastCue;
    public bool AnnouncementStarted => announcementStarted;
    public float ApproachElapsed => approachElapsed;
    public TownAnnouncerPolicy.ApproachProgress Progress => approachProgress;
    public ArenaBuildingRef? Arena => arena;
    public ArenaBuildingRef? Spot => spot;
    public int SpeakerCount => speakers.Count;
    public ControlledSpeaker SpeakerAt(int i) => speakers[i];
    public int PendingReleaseCount => pendingReleases.Count;
    public ControlledSpeaker PendingReleaseAt(int i) => pendingReleases[i];
    /// <summary>Port only (the glue's backstop): <paramref name="c"/> waits in the pending releases (its pending hold is still wanted).</summary>
    public bool IsPendingRelease(TId c) => ReleasePending(c);
    /// <summary>Begins that chose an announcer (the cast walked).</summary>
    public int Announced { get; private set; }
    /// <summary>Fallbacks to the legacy countdown after a cast had been chosen or asked for (Begin's two lines and FallBack).</summary>
    public int Fallbacks { get; private set; }
    /// <summary>Lines spoken.</summary>
    public int LinesSpoken { get; private set; }
    /// <summary>The last fallback's reason ("" for none).</summary>
    public string LastFallback { get; private set; } = "";

    /// <summary>Port only (the glue's backstop): <paramref name="c"/> is a speaker of the current cast and its reservation is still ours on purpose.</summary>
    public bool Keeps(TId c)
    {
        if (eq.Equals(c, none)) return false;
        for (int i = 0; i < speakers.Count; ++i)
            if (speakers[i].Owned && eq.Equals(speakers[i].Actor, c)) return true;
        return false;
    }

    // Release's deferNative (:528-532), and the port's pending hold (taken before the external hold goes back)
    void Defer(ITownAnnouncerHost<TId> h, ControlledSpeaker speaker)
    {
        speaker.Owned = false;
        h.HoldPending(speaker.Actor);
        pendingReleases.Add(speaker);
    }

    /// <summary>Port only: after a throw, every speaker this cast directed (HoldAtStand / IssueMove changed its Hold, Passive and speed) gets its
    /// saved orders and speed back (PrepareNativeResume, each in its own try so one bad speaker does not stop the others) when it is safe to resume,
    /// and an unsafe one is deferred as Release defers it (owned by nobody, held by the pending hold) before the glue forgets
    /// the cast and gives the holds back. Answers how many restores threw.</summary>
    public int RestoreSpeakersAfterFailure(ITownAnnouncerHost<TId> h)
    {
        int failed = 0;
        for (int i = 0; i < speakers.Count; ++i)
        {
            ControlledSpeaker speaker = speakers[i];
            try
            {
                if (!speaker.Directed || !Alive(h, speaker.Actor) || ReleasePending(speaker.Actor))
                    continue;
                if (CanContinue(h, speaker.Actor))
                    PrepareNativeResume(h, speaker, speaker.Actor);
                else
                    Defer(h, speaker);   // unsafe (down, fighting, carried): as Release's deferNative
            }
            catch { ++failed; }
        }
        return failed;
    }

    /// <summary>Port only: after a throw the glue forgets the cast and gives back every reservation itself (the pending releases are kept: they own
    /// nothing).</summary>
    public void ForgetAll()
    {
        speakers.Clear();
        ResetState();
    }

    bool Alive(ITownAnnouncerHost<TId> h, TId c) => !h.IsNull(c) && h.Valid(c);

    // C++: TownAnnouncer.cpp:78-84
    bool IsFighter(TId character, TId[]? fighters, int fighterCount)
    {
        for (int i = 0; !eq.Equals(character, none) && fighters != null && i < fighterCount; ++i)
            if (eq.Equals(fighters[i], character))
                return true;
        return false;
    }

    // C++: TownAnnouncer.cpp:86-96 (arena town: arena->getTown(); the C++ compares the two Town* pointers, NULL == NULL included)
    bool CanAnnounce(ITownAnnouncerHost<TId> h, TId character, PlacedBuilding? arenaNow, TId[]? fighters, int fighterCount)
    {
        return Alive(h, character) && arenaNow is { } a &&
            h.CurrentTown(character) == a.Town &&
            !h.IsPlayer(character) && !IsFighter(character, fighters, fighterCount) &&
            !h.IsDead(character) && !h.IsUnconscious(character) &&
            !h.InCombatMode(character) &&
            !h.IsCarried(character) && !h.IsCarrying(character);
    }

    // C++: TownAnnouncer.cpp:98-103
    bool CanContinue(ITownAnnouncerHost<TId> h, TId character)
    {
        return Alive(h, character) && !h.IsDead(character) &&
            !h.IsUnconscious(character) && !h.InCombatMode(character) &&
            !h.IsCarried(character) && !h.IsCarrying(character);
    }

    // C++: TownAnnouncer.cpp:105-111
    bool ReleasePending(TId character)
    {
        for (int i = 0; !eq.Equals(character, none) && i < pendingReleases.Count; ++i)
            if (eq.Equals(pendingReleases[i].Actor, character))
                return true;
        return false;
    }

    // C++: TownAnnouncer.cpp:113-142 (port only: the town faction 91 as every M8 role, census)
    TId FindSpeaker(ITownAnnouncerHost<TId> h, PlacedBuilding? arenaNow, string? role, TId[]? fighters, int fighterCount)
    {
        if (arenaNow is not { } a || role == null)
            return none;
        Vector3 origin = a.Building.Position;
        var nearby = h.CharactersInSphere(origin, SearchRadius, CharacterSearchMax);
        TId nearest = none;
        float nearestDistance = 1.0e30f;
        for (int i = 0; i < nearby.Count; ++i)
        {
            TId character = nearby[i];
            if (!Alive(h, character))
                continue;
            if (!TownAnnouncerPolicy.Equals(h.RecordId(character), role) ||
                !CanAnnounce(h, character, arenaNow, fighters, fighterCount) ||
                ReleasePending(character) || !h.IsTownFaction(character))
                continue;
            float distance = (h.Position(character) - origin).LengthSquared();
            if (distance < nearestDistance)
            {
                nearest = character;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    // C++: TownAnnouncer.cpp:144-173
    static PlacedBuilding? FindSpot(ITownAnnouncerHost<TId> h, PlacedBuilding? arenaNow)
    {
        if (arenaNow is not { } a)
            return null;
        var nearby = h.SpotsInSphere(a.Building.Position, SearchRadius, BuildingSearchMax);
        PlacedBuilding? nearest = null;
        float nearestDistance = 1.0e30f;
        for (int i = 0; i < nearby.Count; ++i)
        {
            PlacedBuilding candidate = nearby[i];
            if (!TownAnnouncerPolicy.IsSpot(candidate.Building.RecordId))
                continue;
            if (candidate.Town != a.Town)
                continue;
            if (!candidate.Building.Finished)
                continue;
            float distance = (candidate.Building.Position - a.Building.Position).LengthSquared();
            if (distance < nearestDistance)
            {
                nearest = candidate;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    // C++: TownAnnouncer.cpp:175-191
    public static Vector3 SpeakerTarget(Vector3 arenaPos, Vector3 spotPos, int index, int count)
    {
        Vector3 center = spotPos;
        if (count <= 1)
            return center;
        Vector3 towardArena = arenaPos - center;
        towardArena.Y = 0.0f;
        float lengthSq = towardArena.LengthSquared();
        if (lengthSq <= 1.0f)
            towardArena = new Vector3(1.0f, 0.0f, 0.0f);
        else
            towardArena /= MathF.Sqrt(lengthSq);
        var sideways = new Vector3(-towardArena.Z, 0.0f, towardArena.X);
        float offset = ((float)index - ((float)(count - 1) * 0.5f)) * SpeakerSpacing;
        return center + sideways * offset;
    }

    // C++: TownAnnouncer.cpp:193-200
    bool Arrived(ITownAnnouncerHost<TId> h, in ControlledSpeaker speaker)
    {
        TId character = speaker.Actor;
        if (!Alive(h, character))
            return false;
        return (h.Position(character) - speaker.Target).LengthSquared() <=
            ArrivalRadius * ArrivalRadius;
    }

    // C++: TownAnnouncer.cpp:202-216
    float FarthestDistanceFromTarget(ITownAnnouncerHost<TId> h)
    {
        float farthest = 0.0f;
        for (int i = 0; i < speakers.Count; ++i)
        {
            TId character = speakers[i].Actor;
            if (!Alive(h, character))
                continue;
            float distance = (h.Position(character) - speakers[i].Target).Length();
            if (distance > farthest)
                farthest = distance;
        }
        return farthest;
    }

    // C++: TownAnnouncer.cpp:218-223
    void FaceArena(ITownAnnouncerHost<TId> h, TId character)
    {
        if (!Alive(h, character) || arena is not { } r || h.Resolve(r) is not { } a)
            return;
        h.Face(character, a.Building.Position);
    }

    // C++: TownAnnouncer.cpp:225-236
    void HoldAtStand(ITownAnnouncerHost<TId> h, in ControlledSpeaker speaker)
    {
        TId character = speaker.Actor;
        if (!Alive(h, character))
            return;
        h.EndWalk(character);                          // removeJob(MOVE_CUS_ORDERED) + clearAllAIGoals
        h.SetStandingOrders(character, true, true);    // HOLD + PASSIVE
        // addGoal(STAND_STILL): a held NPC's AI chooses no goal (spike S10)
        FaceArena(h, character);
    }

    // C++: TownAnnouncer.cpp:238-258
    void IssueMove(ITownAnnouncerHost<TId> h, ref ControlledSpeaker speaker)
    {
        TId character = speaker.Actor;
        if (!Alive(h, character))
            return;
        // "Kenshi ignores explicit movement while either standing order is set. Passive is restored after arrival when the speaker is parked."
        // (clearAllAIGoals has no call here: the walk is one MoveOrder, a player-order walk that replaces the queue, and a held NPC's AI chooses
        // no goal, spike S10)
        h.SetStandingOrders(character, false, false);
        h.SetRun(character);
        h.WalkTo(character, speaker.Target);
        speaker.Directed = true;
    }

    // C++: TownAnnouncer.cpp:260-274
    static void PrepareNativeResume(ITownAnnouncerHost<TId> h, in ControlledSpeaker speaker, TId character)
    {
        h.ClearOrders(character);   // addOrder(MOVE_CUS_ORDERED, position) + removeJob + clearAllAIGoals
        h.SetStandingOrders(character, speaker.Hold, speaker.Passive);
        h.SetSpeed(character, speaker.Speed);
    }

    // C++: TownAnnouncer.cpp:276-283
    void FallBack(ITownAnnouncerHost<TId> h, string? reason)
    {
        string message = "Proving Grounds: announcer fallback - ";
        message += reason ?? "unavailable";
        h.Debug(message);
        ++Fallbacks;
        LastFallback = reason ?? "unavailable";
        Release(h);
        status = "Legacy fighter countdown";
    }

    // C++: TownAnnouncer.cpp:288-363
    public void Begin(ITownAnnouncerHost<TId> h, ArenaBuildingRef? arenaRef, bool playerMatch, int challengeSlot,
        string?[]? opponentRoles, int opponentCount, TId[]? fighters, int fighterCount)
    {
        Release(h);
        TownAnnouncerPolicy.Program requested = TownAnnouncerPolicy.Select(
            playerMatch, challengeSlot, opponentRoles, opponentCount);
        if (requested == TownAnnouncerPolicy.Program.LegacyCountdown)
            return;

        PlacedBuilding? arenaNow = arenaRef is { } ar ? h.Resolve(ar) : null;
        PlacedBuilding? spotNow = FindSpot(h, arenaNow);
        actors.Clear();
        TownAnnouncerPolicy.Program candidate = requested;
        while (spotNow != null && candidate != TownAnnouncerPolicy.Program.LegacyCountdown)
        {
            actors.Clear();
            int candidateCount = TownAnnouncerPolicy.RequiredSpeakers(candidate);
            for (int i = 0; i < candidateCount; ++i)
            {
                TId actor = FindSpeaker(h, arenaNow,
                    TownAnnouncerPolicy.SpeakerRole(candidate, i), fighters, fighterCount);
                if (!eq.Equals(actor, none))
                    actors.Add(actor);
            }
            if (actors.Count == candidateCount)
                break;
            candidate = TownAnnouncerPolicy.NextFallback(candidate);
        }
        program = TownAnnouncerPolicy.Resolve(candidate, spotNow != null, actors.Count);
        if (program == TownAnnouncerPolicy.Program.LegacyCountdown)
        {
            h.Debug(spotNow == null
                ? "Proving Grounds: announcer fallback - spot 261 not found near arena"
                : "Proving Grounds: announcer fallback - required cast unavailable");
            ++Fallbacks;
            LastFallback = spotNow == null ? "spot 261 not found near arena" : "required cast unavailable";
            actors.Clear();
            return;
        }

        PlacedBuilding a = arenaNow!.Value, s = spotNow!.Value;
        arena = arenaRef;
        spot = s.Building.Ref;
        int required = TownAnnouncerPolicy.RequiredSpeakers(program);
        for (int i = 0; i < required; ++i)
        {
            TId c = actors[i];
            var speaker = new ControlledSpeaker { Actor = c };
            (speaker.Hold, speaker.Passive) = h.StandingOrders(c);
            speaker.Speed = h.SpeedOf(c);
            speaker.Target = SpeakerTarget(a.Building.Position, s.Building.Position, i, required);
            speakers.Add(speaker);
        }
        actors.Clear();
        for (int i = 0; i < speakers.Count; ++i)
        {
            if (!h.ReserveExternal(speakers[i].Actor))
            {
                FallBack(h, "speaker AI ownership could not be reserved");
                return;
            }
            var sp = speakers[i];
            sp.Owned = true;
            speakers[i] = sp;
        }
        uint timeSeed = unchecked(h.NowMs ^ (uint)(h.GameHours * 60.0));
        variant = TownAnnouncerPolicy.Variant(program, timeSeed);
        approachElapsed = 0.0f;
        TownAnnouncerPolicy.ResetApproachProgress(ref approachProgress,
            FarthestDistanceFromTarget(h));
        nextMove = 0.0f;
        lastTick = h.NowMs;
        lastCue = -1;
        announcementStarted = false;
        status = "Announcer walking to the viewing stand";
        for (int i = 0; i < speakers.Count; ++i)
        {
            var sp = speakers[i];
            IssueMove(h, ref sp);
            speakers[i] = sp;
        }
        ++Announced;
        h.Debug("Proving Grounds: announcer cast walking to spot 261");
    }

    // C++: TownAnnouncer.cpp:365-415
    public void TickApproach(ITownAnnouncerHost<TId> h)
    {
        if (program == TownAnnouncerPolicy.Program.LegacyCountdown || announcementStarted)
            return;
        if (arena is not { } ar || h.Resolve(ar) is null || spot is not { } sr || h.Resolve(sr) is null)
        {
            FallBack(h, "arena or spot became invalid");
            return;
        }
        uint now = h.NowMs;
        float dt = (float)unchecked(now - lastTick) / 1000.0f;
        lastTick = now;
        approachElapsed = TownAnnouncerPolicy.AdvanceApproach(
            approachElapsed, dt, h.IsPaused);
        if (h.IsPaused)
            return;
        bool allArrived = true;
        for (int i = 0; i < speakers.Count; ++i)
        {
            TId character = speakers[i].Actor;
            if (!CanContinue(h, character))
            {
                FallBack(h, "speaker became unavailable");
                return;
            }
            if (Arrived(h, speakers[i]))
                HoldAtStand(h, speakers[i]);
            else
                allArrived = false;
        }
        if (allArrived)
        {
            status = "Announcer ready at the viewing stand";
            return;
        }
        if (TownAnnouncerPolicy.ApproachStalled(ref approachProgress,
            FarthestDistanceFromTarget(h), dt, false))
        {
            FallBack(h, "viewing stand approach made no progress for 20 seconds");
            return;
        }
        if (approachElapsed >= nextMove)
        {
            for (int i = 0; i < speakers.Count; ++i)
            {
                if (Arrived(h, speakers[i]))
                    continue;
                var sp = speakers[i];
                IssueMove(h, ref sp);
                speakers[i] = sp;
            }
            nextMove = approachElapsed + MoveRetrySec;
        }
    }

    // C++: TownAnnouncer.cpp:417-442
    public void TickReleases(ITownAnnouncerHost<TId> h)
    {
        if (h.IsPaused)
            return;
        for (int i = 0; i < pendingReleases.Count;)
        {
            ControlledSpeaker speaker = pendingReleases[i];
            TId character = speaker.Actor;
            bool valid = Alive(h, character);
            bool dead = valid && h.IsDead(character);
            TownAnnouncerPolicy.PendingReleaseDisposition disposition =
                TownAnnouncerPolicy.PendingReleaseFor(valid, dead,
                    CanContinue(h, character));
            if (disposition == TownAnnouncerPolicy.PendingReleaseDisposition.WaitPendingRelease)
            {
                ++i;
                continue;
            }
            // port only: the entry leaves the list before its restore, so a restore that throws is dropped (the glue logs it once and counts it)
            // instead of throwing again at every tick; the pending hold is given back either way
            pendingReleases.RemoveAt(i);
            try
            {
                if (disposition == TownAnnouncerPolicy.PendingReleaseDisposition.ResumePendingRelease)
                {
                    PrepareNativeResume(h, speaker, character);
                    h.Replan(character);   // reThinkCurrentAIAction
                }
            }
            finally { h.ReleasePendingHold(character); }
        }
    }

    // C++: TownAnnouncer.cpp:444-452
    public bool WaitingForPosition(ITownAnnouncerHost<TId> h)
    {
        if (program == TownAnnouncerPolicy.Program.LegacyCountdown)
            return false;
        for (int i = 0; i < speakers.Count; ++i)
            if (!Arrived(h, speakers[i]))
                return true;
        return false;
    }

    // C++: TownAnnouncer.cpp:454-457
    public bool HasAnnouncement() => program != TownAnnouncerPolicy.Program.LegacyCountdown;

    // C++: TownAnnouncer.cpp:459-467
    public void BeginAnnouncement(ITownAnnouncerHost<TId> h)
    {
        if (!HasAnnouncement())
            return;
        announcementStarted = true;
        lastCue = -1;
        status = "The announcer takes the rail";
        TickAnnouncement(h, 0.0f);
    }

    // C++: TownAnnouncer.cpp:469-500
    public void TickAnnouncement(ITownAnnouncerHost<TId> h, float elapsed)
    {
        if (!HasAnnouncement() || !announcementStarted)
            return;
        for (int i = 0; i < speakers.Count; ++i)
        {
            TId character = speakers[i].Actor;
            if (!CanContinue(h, character))
            {
                FallBack(h, "speaker lost during announcement");
                return;
            }
            HoldAtStand(h, speakers[i]);
        }
        int due = TownAnnouncerPolicy.CuesDue(program, lastCue, elapsed, variant);
        for (int delivered = 0; delivered < due; ++delivered)
        {
            int cue = lastCue + 1;
            int speaker = TownAnnouncerPolicy.CueSpeaker(program, cue, variant);
            string? line = TownAnnouncerPolicy.CueText(program, variant, cue);
            if (speaker >= 0 && speaker < speakers.Count && line != null)
            {
                TId actor = speakers[speaker].Actor;
                FaceArena(h, actor);
                h.Say(actor, line);
                ++LinesSpoken;
                status = cue == TownAnnouncerPolicy.CueCount(program, variant) - 1
                    ? "Fight!"
                    : "The fight is being announced";
            }
            lastCue = cue;
        }
    }

    // C++: TownAnnouncer.cpp:502-505
    public float AnnouncementDuration() => TownAnnouncerPolicy.Duration(program, variant);

    // C++: TownAnnouncer.cpp:507-510
    public string GetStatus() => status;

    // C++: TownAnnouncer.cpp:512-545
    public void Release(ITownAnnouncerHost<TId> h)
    {
        for (int i = 0; i < speakers.Count; ++i)
        {
            ControlledSpeaker speaker = speakers[i];
            TId character = speaker.Actor;
            bool safeToResume = CanContinue(h, character);
            TownAnnouncerPolicy.ReleasePlan plan =
                TownAnnouncerPolicy.PlanRelease(speaker.Owned,
                    speaker.Directed, safeToResume);
            if (plan.ResumeNative)
                PrepareNativeResume(h, speaker, character);
            if (plan.DeferNative)
                h.HoldPending(character);   // port only: held until TickReleases resumes it (no rethink before, as the C++)
            if (plan.ReleaseOwnership)
                h.ReleaseExternal(character);
            if (plan.ResumeNative)
                h.Replan(character);   // reThinkCurrentAIAction
            else if (plan.DeferNative)
            {
                speaker.Owned = false;
                pendingReleases.Add(speaker);
            }
        }
        speakers.Clear();
        ResetState();
    }

    /// <summary>The C++'s <c>ReleaseExternalOrders</c> of every owned speaker (:549-551) is not called: the world is going away, and TownAftercare's
    /// external table and the hold registry are emptied by their own world resets (as the limb shop's and the corpse cleanup's AbandonWorldState).</summary>
    // C++: TownAnnouncer.cpp:547-564
    public void AbandonWorldState()
    {
        speakers.Clear();
        pendingReleases.Clear();
        ResetState();
    }

    // the field resets Release (:535-544) and AbandonWorldState (:554-563) share
    void ResetState()
    {
        arena = null;
        spot = null;
        program = TownAnnouncerPolicy.Program.LegacyCountdown;
        approachElapsed = 0.0f;
        TownAnnouncerPolicy.ResetApproachProgress(ref approachProgress, 0.0f);
        nextMove = 0.0f;
        lastTick = 0;
        lastCue = -1;
        announcementStarted = false;
        status = "Legacy fighter countdown";
    }
}

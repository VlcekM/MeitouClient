// The game-free decisions of upstream/ProvingGrounds/plugin/src/CombatHooks.cpp (Proving Grounds by Shutori, GPLv3), task 3.2b (PA §2.8,
// CAP 1-3, spikes S1 and S2): the "sparring pair" and "isolated pair" predicates over a published, immutable view of the match (read by the
// patches that run on Meitou's parallel AI threads), the classification of one blow from the attacker's combat counters, the damage numbers
// of a landed blow and the pairing of those numbers with the blow, the stats feed in the C++'s order, and the counter-difference fallback.
// Task 7.4 adds the town rules the view answers: SanctionedPair / CollateralPair (CombatHooks.cpp:32-40 over TownArena.cpp:1415-1438), IsFighter
// (:1409-1414) and BlockTownBystander (ArenaMedical.cpp:50-55) with the Scratch medic's own-carrier exemption (Tests/Glue/TownCombatRulesTests.cs).
// src/Game/CombatHooks.cs binds them to the engine; Tests/Glue/CombatHookLogicTests.cs runs them over fakes (E3).
namespace ProvingGrounds.Core.Glue;

/// <summary>
/// The running match as the combat hooks read it: published by the glue on the sim thread's serial steps whenever it changes (a new object
/// each time, never written after construction), read from any thread through one volatile reference. It holds exactly what SparSession's
/// <c>IsSparringOpponent</c> (SparSession.cpp:515-540) reads (active, mode, fighters, teams, who is out, the Teams 1v1 active pair, a pending
/// walk-in; the conscious state <c>MakeParticipant</c> also fills is not read by <see cref="MatchRules.IsOpponent"/>) plus the characters
/// CombatHooks.cpp:156-169 isolates (the town's bout fighters, <c>TownArena::IsFighter</c>, and the match prisoners,
/// <c>PrisonerUtil::IsMatchPrisoner</c>), and (task 7.4) what the town rules read: the town bout's fighter list with its teams, whether
/// <c>TownArena::IsIncidentPair</c> retains it (<c>retained</c>, TownArena.cpp:1426), whether <c>IsActiveCollateralIncidentPair</c> can answer
/// (<c>g_phase == Fighting &amp;&amp; OwnsMatch()</c>, :1430) and whether the town owns the running match (<c>OwnsMatch()</c>, ArenaMedical.cpp:53).
/// <typeparamref name="TId"/>: the key the readers compare (the glue: the character's table slot).
/// </summary>
public sealed class CombatMatchView<TId> where TId : IEquatable<TId>
{
    readonly TId none;
    readonly TId[] fighters, townFighters, matchPrisoners, prisonerHandlers, incidentFighters;
    readonly MatchRules.MatchTeam[] teams, incidentTeams;
    readonly bool[] eliminated;

    public bool Active { get; }
    public MatchRules.MatchMode Mode { get; }
    public TId ActiveA { get; }
    public TId ActiveB { get; }
    public bool WalkInPending { get; }
    public int FighterCount => fighters.Length;
    public int IsolatedCount => townFighters.Length + matchPrisoners.Length;
    /// <summary>Task 7.4: C++ TownArena.cpp:1426's <c>retained</c> (Fighting, Aftercare, or Preparing while the town owns the match).</summary>
    public bool IncidentRetained { get; }
    /// <summary>Task 7.4: C++ TownArena.cpp:1430's <c>g_phase == Fighting &amp;&amp; OwnsMatch()</c>.</summary>
    public bool CollateralActive { get; }
    /// <summary>Task 7.4: C++ <c>TownArena::OwnsMatch()</c> (TownArena.cpp:1457-1462), read by ArenaMedical.cpp:53.</summary>
    public bool TownOwnsMatch { get; }
    /// <summary>Task 7.4: the town bout's fighters the town rules read (empty when no sanctioned pair can exist).</summary>
    public int IncidentCount => incidentFighters.Length;

    /// <summary>No match and nobody isolated.</summary>
    public static CombatMatchView<TId> Idle(TId none) =>
        new(none, false, MatchRules.MatchMode.TeamAvB, [], [], [], none, none, false, [], []);

    /// <summary>Copies every span: the view never shares an array with its builder. <paramref name="prisonerHandlers"/> (task 6.3): the handler of
    /// each match prisoner, the same order as <paramref name="matchPrisoners"/> (<c>none</c> for one without), or empty for no handlers at all.
    /// The town rules' state (task 7.4, see the class summary): <paramref name="incidentFighters"/> and <paramref name="incidentTeams"/> aligned.</summary>
    public CombatMatchView(TId none, bool active, MatchRules.MatchMode mode, ReadOnlySpan<TId> fighters, ReadOnlySpan<MatchRules.MatchTeam> teams,
        ReadOnlySpan<bool> eliminated, TId activeA, TId activeB, bool walkInPending, ReadOnlySpan<TId> townFighters, ReadOnlySpan<TId> matchPrisoners,
        ReadOnlySpan<TId> prisonerHandlers = default, ReadOnlySpan<TId> incidentFighters = default,
        ReadOnlySpan<MatchRules.MatchTeam> incidentTeams = default, bool incidentRetained = false, bool collateralActive = false,
        bool townOwnsMatch = false)
    {
        if (teams.Length != fighters.Length || eliminated.Length != fighters.Length)
            throw new ArgumentException("fighters, teams and eliminated must have one entry each");
        if (prisonerHandlers.Length != 0 && prisonerHandlers.Length != matchPrisoners.Length)
            throw new ArgumentException("prisonerHandlers must be empty or have one entry per match prisoner");
        if (incidentTeams.Length != incidentFighters.Length)
            throw new ArgumentException("incidentFighters and incidentTeams must have one entry each");
        this.none = none;
        Active = active;
        Mode = mode;
        this.fighters = fighters.ToArray();
        this.teams = teams.ToArray();
        this.eliminated = eliminated.ToArray();
        ActiveA = activeA;
        ActiveB = activeB;
        WalkInPending = walkInPending;
        this.townFighters = townFighters.ToArray();
        this.matchPrisoners = matchPrisoners.ToArray();
        this.prisonerHandlers = prisonerHandlers.ToArray();
        this.incidentFighters = incidentFighters.ToArray();
        this.incidentTeams = incidentTeams.ToArray();
        IncidentRetained = incidentRetained;
        CollateralActive = collateralActive;
        TownOwnsMatch = townOwnsMatch;
    }

    /// <summary>Whether a view built from these values would equal this one (the glue republishes only on a change, so an unchanged tick
    /// allocates nothing).</summary>
    public bool Matches(bool active, MatchRules.MatchMode mode, ReadOnlySpan<TId> fighters, ReadOnlySpan<MatchRules.MatchTeam> teams,
        ReadOnlySpan<bool> eliminated, TId activeA, TId activeB, bool walkInPending, ReadOnlySpan<TId> townFighters, ReadOnlySpan<TId> matchPrisoners,
        ReadOnlySpan<TId> prisonerHandlers = default, ReadOnlySpan<TId> incidentFighters = default,
        ReadOnlySpan<MatchRules.MatchTeam> incidentTeams = default, bool incidentRetained = false, bool collateralActive = false,
        bool townOwnsMatch = false)
    {
        if (active != Active || mode != Mode || walkInPending != WalkInPending || !activeA.Equals(ActiveA) || !activeB.Equals(ActiveB)
            || incidentRetained != IncidentRetained || collateralActive != CollateralActive || townOwnsMatch != TownOwnsMatch)
            return false;
        return Same(this.fighters, fighters) && Same(this.townFighters, townFighters) && Same(this.matchPrisoners, matchPrisoners)
            && Same(this.prisonerHandlers, prisonerHandlers) && Same(this.incidentFighters, incidentFighters)
            && this.teams.AsSpan().SequenceEqual(teams) && this.eliminated.AsSpan().SequenceEqual(eliminated)
            && this.incidentTeams.AsSpan().SequenceEqual(incidentTeams);
    }

    static bool Same(TId[] a, ReadOnlySpan<TId> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    int IndexOf(TId c)
    {
        for (int i = 0; i < fighters.Length; i++)
            if (fighters[i].Equals(c)) return i;
        return -1;
    }

    static bool Contains(TId[] list, TId c)
    {
        for (int i = 0; i < list.Length; i++)
            if (list[i].Equals(c)) return true;
        return false;
    }

    bool IsNone(TId c) => c.Equals(none);

    /// <summary>C++: SparSession.cpp:547-550 <c>IsParticipant</c> over the view.</summary>
    public bool IsParticipant(TId c) => Active && !IsNone(c) && IndexOf(c) >= 0;

    /// <summary>C++: SparSession.cpp:464-469 <c>IsEliminated</c> over the view (task 4.7: ArenaMedical's treatment block reads it).</summary>
    public bool IsEliminated(TId c)
    {
        ParticipantState(c, out _, out bool e);
        return e;
    }

    /// <summary><see cref="IsParticipant"/> and <see cref="IsEliminated"/> with one scan of the fighters (task 4.7, the treatment block).</summary>
    public void ParticipantState(TId c, out bool participant, out bool isEliminated)
    {
        int i = Active && !IsNone(c) ? IndexOf(c) : -1;
        participant = i >= 0;
        isEliminated = i >= 0 && eliminated[i];
    }

    /// <summary>C++: SparSession.cpp:515-540 <c>IsSparringOpponent</c> over the view (the same tests in the same order; MakeParticipant's
    /// Conscious / Dead are not read by <see cref="MatchRules.IsOpponent"/>, MatchRules.cpp:5-12).</summary>
    public bool IsSparringOpponent(TId a, TId b)
    {
        if (!Active || IsNone(a) || IsNone(b))
            return false;

        int ia = IndexOf(a);
        int ib = IndexOf(b);
        if (ia < 0 || ib < 0)
            return false;
        if (eliminated[ia] || eliminated[ib])
            return false;

        if (Mode == MatchRules.MatchMode.Teams1v1)
        {
            if (WalkInPending)
                return false;
            bool aActive = a.Equals(ActiveA) || a.Equals(ActiveB);
            bool bActive = b.Equals(ActiveA) || b.Equals(ActiveB);
            if (!aActive || !bActive)
                return false;
        }

        var pa = new MatchRules.MatchParticipant { Id = ia, Team = teams[ia], Eliminated = eliminated[ia] };
        var pb = new MatchRules.MatchParticipant { Id = ib, Team = teams[ib], Eliminated = eliminated[ib] };
        return MatchRules.IsOpponent(Mode, pa, pb);
    }

    /// <summary>C++: CombatHooks.cpp:156-169 <c>ShouldIsolatePair</c>: not a sparring pair, and a town bout fighter is involved or either side
    /// is a match prisoner (PA §2.8 "isolated pair").</summary>
    public bool ShouldIsolatePair(TId a, TId b)
    {
        if (IsNone(a) || IsNone(b))
            return false;
        if (IsSparringOpponent(a, b))
            return false;
        if (TownArenaRuntimePolicy.IsolatePair(Contains(townFighters, a), Contains(townFighters, b), false))
            return true;
        return PrisonerMatchLogic.ShouldSuppressNaturalEnemy(false, Contains(matchPrisoners, a), Contains(matchPrisoners, b));
    }

    /// <summary>The BestAttackerOf / FindFoe postfixes (C++ attackTarget_hook :252-258, iShouldntAggravateThisTarget_hook :260-265): the foe
    /// <paramref name="foe"/> that <paramref name="looker"/> would pick is dropped when the two are an isolated pair. Nobody isolated: false at
    /// once (the same answer as <see cref="ShouldIsolatePair"/>, without the scans).</summary>
    public bool DropsFoe(TId looker, TId foe) => IsolatedCount > 0 && ShouldIsolatePair(looker, foe);

    /// <summary>The BeginPickUp prefix: <paramref name="carrier"/> may not lift <paramref name="body"/> when the two are an isolated pair (port
    /// only, CAP 2, task 3.2b: the outsiders the C++ keeps from a match prisoner or a town bout fighter), unless the carrier is one of the mod's
    /// own (<see cref="IsOwnCarrier"/>); nor (task 7.4, C++ pickupObject_hook ArenaMedical.cpp:64-71) when <see cref="BlocksTownBystander"/>.
    /// <paramref name="carrierIsTownMedic"/>: the carrier is a Scratch medic (record <c>100-Proving Grounds.mod</c>, C++ IsScratchMedicActor
    /// ArenaMedical.cpp:44-48); the glue reads it only when <see cref="IsTownFighter"/>(body), the one case it matters.</summary>
    public bool RefusesPickUp(TId carrier, TId body, bool carrierIsTownMedic = false) =>
        (IsolatedCount > 0 && !IsOwnCarrier(carrier, body, carrierIsTownMedic) && ShouldIsolatePair(carrier, body))
        || BlocksTownBystander(body, carrierIsTownMedic);

    /// <summary>The exemption from <see cref="RefusesPickUp"/>'s isolation refusal for the mod's own carriers (port only; the C++ never refused a
    /// pick-up by isolation): the handler of a match prisoner lifting that prisoner (task 6.3, the return's carry, PrisonerUtil.cpp:768-784).
    /// Published for the whole match, not only the return: a parked handler may now lift its own released prisoner mid-fight too, which the 3.2b
    /// refusal stopped before; the C++ never refused a pick-up, so this is closer to the original. And (task 7.4) a Scratch medic lifting a town
    /// bout fighter (<paramref name="carrierIsTownMedic"/>): the C++ lets only medics carry town fighters away (ArenaMedical.cpp:50-55, 64-71). The
    /// one place a later carrier of the mod (M8 aftercare) is added.</summary>
    public bool IsOwnCarrier(TId carrier, TId body, bool carrierIsTownMedic = false)
    {
        if (IsNone(carrier) || IsNone(body))
            return false;
        for (int i = 0; i < prisonerHandlers.Length; i++)
            if (matchPrisoners[i].Equals(body) && prisonerHandlers[i].Equals(carrier)) return true;
        return carrierIsTownMedic && Contains(townFighters, body);
    }

    // ---------- the town rules (task 7.4) ----------

    /// <summary>C++ <c>TownArena::IsFighter</c> (TownArena.cpp:1409-1414) over the view: a fighter of the town bout while it is being prepared or
    /// fights.</summary>
    public bool IsTownFighter(TId c) => !IsNone(c) && Contains(townFighters, c);

    /// <summary>C++: ArenaMedical.cpp:50-55 <c>BlockTownBystander(actor, patient)</c>: the patient is a town bout fighter, the town owns the running
    /// match and the actor is not a Scratch medic (<paramref name="actorIsTownMedic"/>, IsScratchMedicActor :44-48, read by the glue). Player-arena
    /// fights, prisoner handlers included, keep their care (the town fighter list is empty there).</summary>
    public bool BlocksTownBystander(TId patient, bool actorIsTownMedic) => IsTownFighter(patient) && TownOwnsMatch && !actorIsTownMedic;

    /// <summary>C++: TownArena.cpp:1415-1427 <c>IsIncidentPair</c> over the view's copy (TownIncidentPairs, the same loop the logic runs).</summary>
    public bool IsIncidentPair(TId a, TId b) =>
        !IsNone(a) && !IsNone(b) && TownIncidentPairs.IsIncidentPair(IncidentRetained, incidentFighters, incidentTeams, a, b);

    /// <summary>C++: TownArena.cpp:1428-1438 <c>IsActiveCollateralIncidentPair</c> over the view's copy.</summary>
    public bool IsCollateralPair(TId a, TId b) =>
        !IsNone(a) && !IsNone(b) && TownIncidentPairs.IsCollateralPair(CollateralActive, incidentFighters, a, b);

    /// <summary>C++: CombatHooks.cpp:37-40 <c>SanctionedPair</c>: an incident pair or a collateral pair. No town bout: false at once (no scan).
    /// Read by the crime prefix (C++ setCrime_hook :94-114, <see cref="SkipsCrime"/>) and the relation prefix (relationEvent_hook / setEnemy_hook
    /// :124-148).</summary>
    public bool IsSanctionedPair(TId a, TId b) => incidentFighters.Length > 0 && (IsIncidentPair(a, b) || IsCollateralPair(a, b));

    /// <summary>C++: CombatHooks.cpp:96-100 (setCrime_hook): no crime when it is a combat crime (assault, VIP assault or murder:
    /// <paramref name="combatCrime"/>, the glue's test of the crime code) and the culprit and the victim are a sanctioned pair.</summary>
    public bool SkipsCrime(bool combatCrime, TId culprit, TId victim) => combatCrime && IsSanctionedPair(culprit, victim);

    /// <summary>The FactionDeeds.Remember prefix (port only, CAP 1 "Long squad memory"): no long squad memory between two fighters of the running
    /// match (any two participants, eliminated or benched included; the original had no such memory).</summary>
    public bool SkipsSquadMemory(TId striker, TId victim) => IsParticipant(striker) && IsParticipant(victim);
}

/// <summary>The attacker's three counters a blow can move (Meitou <c>CombatSlot.Landed / Parried / Evaded</c>, CombatSlot.cs:99).</summary>
public readonly record struct BlowCounters(int Landed, int Parried, int Evaded);

/// <summary>What one resolved blow did. The C++ <c>HitMaterialType</c> results of <c>_iHitYouAreYouHit</c>: <see cref="Blocked"/> =
/// <c>HIT_SWORD</c>, <see cref="Dodged"/> = <c>HIT_MISSED</c> (a blow that reached the defender and was dodged: the C++'s "miss"), <see cref="Hit"/>
/// = anything else; <see cref="None"/> = the blow was dropped before it resolved (Meitou: a swing cancelled this tick, or a fighter without
/// stats or body), which never reaches the C++ hook either.</summary>
public enum BlowKind { None, Hit, Blocked, Dodged }

/// <summary>Where the stats feed sends one blow (the glue: SparStats and FightStarter's hit reaction).</summary>
public interface IBlowSink<TId>
{
    /// <summary>C++ <c>SparStats::OnHit(attacker, defender, applied, incoming, missed)</c>.</summary>
    void OnHit(TId attacker, TId defender, float appliedDamage, float incomingDamage, bool isMiss);
    /// <summary>C++ <c>SparStats::OnBlock(defender, attacker)</c>.</summary>
    void OnBlock(TId defender, TId attacker);
    /// <summary>C++ <c>FightStarter::ReactToIncomingAttack(defender, attacker)</c> (the glue records it for the next serial step).</summary>
    void React(TId defender, TId attacker);
}

/// <summary>Per-unit counts the counter fallback reports for one fighter.</summary>
public interface ICounterSink<TId>
{
    /// <summary>The fighter blocked <paramref name="blocks"/> blows, had <paramref name="misses"/> of its own blows dodged and dodged
    /// <paramref name="dodges"/>, since the last report.</summary>
    void AddCounts(TId fighter, int blocks, int misses, int dodges);
}

/// <summary>The pure steps of CombatHooks.cpp's hit hook.</summary>
public static class CombatHookLogic
{
    /// <summary>The blow's outcome from what moved in the attacker's counters across <c>CombatSystem.ApplyBlow</c> (spike S2: ApplyBlow raises
    /// exactly one of them for a resolved blow: Evaded at CombatSystem.Apply.cs:117, Parried at :140, Landed at :183; a dropped blow raises none). <paramref
    /// name="cancelled"/>: the swing was cancelled earlier in this tick (ApplyBlow returns at once, Apply.cs:81).</summary>
    public static BlowKind Classify(BlowCounters before, BlowCounters after, bool cancelled)
    {
        if (cancelled) return BlowKind.None;
        if (after.Landed > before.Landed) return BlowKind.Hit;
        if (after.Parried > before.Parried) return BlowKind.Blocked;
        if (after.Evaded > before.Evaded) return BlowKind.Dodged;
        return BlowKind.None;
    }

    /// <summary>C++: CombatHooks.cpp:96 <c>crime == CRIME_ASSAULT || crime == CRIME_ASSAULT_VIP || crime == CRIME_MURDER</c> over Meitou's crime
    /// codes (CrimeTypes.cs:31-41, the game's table): Murder 4, Assault 5, Treason 6 (the editor's CRIME_ASSAULT_VIP; nothing in Meitou 0.17.3 sets
    /// it).</summary>
    public static bool IsCombatCrime(int code) => code is 4 or 5 or 6;

    /// <summary>C++ <c>Damages::total()</c> of a blow's damage: cut + blunt + pierce (stun is not damage the stats count).</summary>
    public static float Total(float cut, float blunt, float pierce) => cut + blunt + pierce;

    /// <summary>C++: CombatHooks.cpp:202-241. The C++ reads <c>incomingDamage</c> = the blow's total before the defender's armour and
    /// <c>appliedDamage</c> = the total after it; the port gets them from Meitou's hit observer: applied = the delivered damage
    /// (<c>CombatHit.Damage</c>, after armour and toughness), incoming = applied + what the armour stopped (<c>CombatHit.Absorbed</c>).</summary>
    public static (float Applied, float Incoming) Damage(float cut, float blunt, float pierce, float absorbedCut, float absorbedBlunt,
        float absorbedPierce)
    {
        float applied = Total(cut, blunt, pierce);
        return (applied, applied + Total(absorbedCut, absorbedBlunt, absorbedPierce));
    }

    /// <summary>C++: CombatHooks.cpp:208-248, the part after the original call, for one resolved blow: only between sparring opponents; a
    /// block is the defender's block; anything else is a hit (with damage) or, for a dodge, the attacker's miss and the defender's dodge
    /// (applied 0); every result but a dodge makes the defender react to this attacker. The soft-miss net (:210-223) is not built (spike S1:
    /// blows between squad-mates land in Meitou; divergences.md). <paramref name="hasDamage"/> false (no observed damage for a hit) feeds 0 / 0.</summary>
    public static void Feed<TId, TSink>(TSink sink, bool sparringOpponents, BlowKind kind, TId attacker, TId defender, bool hasDamage,
        float appliedDamage, float incomingDamage) where TSink : IBlowSink<TId>
    {
        if (!sparringOpponents || kind == BlowKind.None)
            return;

        if (kind == BlowKind.Blocked)
        {
            sink.OnBlock(defender, attacker);
        }
        else
        {
            bool missed = kind == BlowKind.Dodged;
            float applied = missed || !hasDamage ? 0.0f : appliedDamage;
            float incoming = missed || !hasDamage ? 0.0f : incomingDamage;
            sink.OnHit(attacker, defender, applied, incoming, missed);
        }

        if (kind != BlowKind.Dodged)
            sink.React(defender, attacker);
    }
}

/// <summary>
/// Pairs the damage Meitou's hit observer reports (<c>ICombatHitObserver.HitResolved</c>, called from inside <c>ApplyBlow</c>'s
/// <c>LandBlow</c>, CombatSystem.Apply.cs:208-213) with the blow the ApplyBlow pre/postfix pair classifies: the prefix says which blow it
/// expects, the observer offers the numbers of the hit it sees, the postfix takes them. Each counted blow is counted once, in the postfix;
/// the observer only stores numbers. Keys: attacker and defender slots, the attack number and blow index (<c>Effect.Amount</c>, <c>Arg</c>;
/// <c>CombatHit.Sequence</c>, <c>Blow</c>) and the tick. Serial (the commit step) only.
/// </summary>
public struct HitPairing
{
    int attacker, defender, sequence, blow;
    long tick;
    bool expecting, has;
    float applied, incoming;

    /// <summary>The prefix: the next numbers offered for this blow belong to it; anything stored before is dropped.</summary>
    public void Expect(int attacker, int defender, int sequence, int blow, long tick)
    {
        this.attacker = attacker;
        this.defender = defender;
        this.sequence = sequence;
        this.blow = blow;
        this.tick = tick;
        expecting = true;
        has = false;
    }

    /// <summary>The observer: stores the numbers when they are the expected blow's (first offer wins); ignores any other hit.</summary>
    public bool Offer(int attacker, int defender, int sequence, int blow, long tick, float applied, float incoming)
    {
        if (!expecting || has || attacker != this.attacker || defender != this.defender || sequence != this.sequence || blow != this.blow
            || tick != this.tick)
            return false;
        this.applied = applied;
        this.incoming = incoming;
        has = true;
        return true;
    }

    /// <summary>Whether the prefix's expected blow is this one (and not taken yet).</summary>
    public readonly bool IsExpecting(int attacker, int defender, int sequence, int blow, long tick) =>
        expecting && attacker == this.attacker && defender == this.defender && sequence == this.sequence && blow == this.blow && tick == this.tick;

    /// <summary>The postfix, idempotent: false when this blow is not the expected one or was already taken (a second copy of the postfix, e.g.
    /// the patch applied twice, must not count the blow again); else true, with the stored numbers if the observer offered any
    /// (<paramref name="hasDamage"/>), and the pairing cleared.</summary>
    public bool TakeFor(int attacker, int defender, int sequence, int blow, long tick, out bool hasDamage, out float applied, out float incoming)
    {
        if (!IsExpecting(attacker, defender, sequence, blow, tick))
        {
            hasDamage = false; applied = 0.0f; incoming = 0.0f;
            return false;
        }
        hasDamage = Take(out applied, out incoming);
        return true;
    }

    /// <summary>The stored numbers, if any; clears everything.</summary>
    public bool Take(out float applied, out float incoming)
    {
        bool had = has;
        applied = had ? this.applied : 0.0f;
        incoming = had ? this.incoming : 0.0f;
        Clear();
        return had;
    }

    public void Clear()
    {
        expecting = false;
        has = false;
    }

    public readonly bool Expecting => expecting;
}

/// <summary>
/// The fallback when the ApplyBlow patch is not available (spike S2's offline method, docs/spikes.md S2): each serial step, a match fighter's
/// <c>CombatSystem.StateOf</c> counters are compared with the last ones seen, and the differences are its blocks (<c>Blocks</c>), misses (its
/// blows the other side dodged, <c>Evaded</c>) and dodges (<c>Dodges</c>). Never <c>Thrown - Landed - Parried - Evaded</c> (that counts swings
/// cut off before they reached anyone). The counters include blows against anyone, not only sparring opponents. A counter that went down
/// (the slot was reset) starts a new baseline. Sim thread only.
/// </summary>
public sealed class CounterFallback<TId> where TId : IEquatable<TId>
{
    struct Row { public TId Id; public int Blocks, Evaded, Dodges; }

    Row[] rows = new Row[8];
    int count;

    public int Count => count;

    /// <summary>Forget every baseline (a match start or end).</summary>
    public void Reset() => count = 0;

    /// <summary>One fighter's counters now: the first time a baseline only, afterwards the differences to <paramref name="sink"/> when any is
    /// positive.</summary>
    public void Observe<TSink>(TId fighter, int blocks, int evaded, int dodges, TSink sink) where TSink : ICounterSink<TId>
    {
        for (int i = 0; i < count; i++)
        {
            ref var r = ref rows[i];
            if (!r.Id.Equals(fighter)) continue;
            if (blocks < r.Blocks || evaded < r.Evaded || dodges < r.Dodges)
            {
                r.Blocks = blocks; r.Evaded = evaded; r.Dodges = dodges;
                return;
            }
            int db = blocks - r.Blocks, de = evaded - r.Evaded, dd = dodges - r.Dodges;
            r.Blocks = blocks; r.Evaded = evaded; r.Dodges = dodges;
            if (db > 0 || de > 0 || dd > 0) sink.AddCounts(fighter, db, de, dd);
            return;
        }
        if (count == rows.Length) Array.Resize(ref rows, rows.Length * 2);
        rows[count++] = new Row { Id = fighter, Blocks = blocks, Evaded = evaded, Dodges = dodges };
    }
}

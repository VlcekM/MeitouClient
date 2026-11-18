using System.Globalization;
using HarmonyLib;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/CombatHooks.h/.cpp (Proving Grounds by Shutori, GPLv3), task 3.2b (PA §2.8, CAP 1, 2, 3 and "The
// original's hooks and their Meitou counterparts", spikes S1 and S2). The decisions (the sparring / isolated pair predicates over the published
// match view, the blow classification, the damage numbers and their pairing, the stats feed, the counter fallback) are Core/Glue/CombatHookLogic.cs,
// tested with fakes in Tests/Glue/CombatHookLogicTests.cs (E3). Meitou 0.17.3 (tag v0.17.3, fa34a1b).
//
// The C++ hooks, in CombatHooks.cpp's order, and what covers each in the port:
//   CollateralPair / SanctionedPair / ArenaIncident / ArenaRelation / ArenaCombatOutcome (:32-79), meleeHit_hook (_NV_hitByMeleeAttack, :82-89),
//       setCrime_hook (:94-114), attackingYou_hook (:115-119), campaignAttack_hook (notifyTheCampaignOfAnAttack, :120-123), relationEvent_hook
//       (_NV_affectRelations, :124-143), setEnemy_hook (_NV_setEnemy, :144-148) ... task 7.4 (town bouts only, as in the original: they fire
//       only for TownArena's incident pairs, whose list is empty in the player arena, PA §2.8): CombatMatchView.IsSanctionedPair over the published
//       town state, read by prefixes on public CrimeSystem.SetPending (setCrime) and FactionDeeds.Blow (affectRelations,
//       setEnemy); no incident context, meleeHit or attackingYou counterpart and no campaign notice (src/Town/TownCombatRules.cs, its row list)
//   ShouldIsolatePair (:156-169) ... CombatMatchView.ShouldIsolatePair (Core/Glue/CombatHookLogic.cs)
//   isEnemy_hook (:171-178), isAlly_hook (:180-189) ... none needed: Meitou lets one faction fight itself (CAP 1, AggroSystem.cs:1592-1594;
//       fighting pairs are tagged Enemy to each other in the senses, AggroSystem.cs:1559-1590), spike S1 live: blows land between squad-mates;
//       the isolation half is the BestAttackerOf / FindFoe postfixes below (divergences.md "CombatHooks")
//   isFightingAnAllyOfMine_hook (:191-197) ... none needed for one faction (CAP 1 "Nobody else joins": every joiner picks its foe with
//       BestAttackerOf, which drops a same-faction attacker, CombatSystem.Targets.cs:222-232); its isolation half is the BestAttackerOf postfix
//   iHitYouAreYouHit_hook (:199-250) ... the prefix + postfix pair on private CombatSystem.ApplyBlow(World, in Effect) (CombatSystem.Apply.cs:70,
//       spike S2): the stats (block / dodge = miss / hit with damage) and the defender's reaction, only between sparring opponents. The damage
//       numbers come from Meitou's hit observer (ICombatHitObserver.HitResolved, called inside ApplyBlow's LandBlow, Apply.cs:208-213), paired
//       with the blow by HitPairing. The soft-miss net (:210-223) is not built (spike S1: same-faction blows land); its fallback would be a
//       prefix on public CombatSystem.Apply (CAP 3). If ApplyBlow (or _write) is missing: CombatSystem.StateOf counter differences
//       (CombatSystem.cs:250; CounterFallback) for blocks / misses / dodges, and the hit observer for hits and damage.
//   attackTarget_hook (:252-258) ... postfix on public CombatSystem.BestAttackerOf (CombatSystem.Targets.cs:202): no foe for an isolated pair
//   iShouldntAggravateThisTarget_hook (:260-265) ... postfix on public AggroSystem.FindFoe (AggroSystem.cs:1786): no foe for an isolated pair
//   (port only, CAP 2) ... prefix on public CarrySystem.BeginPickUp (CarrySystem.cs:545): an isolated pair's carrier does not lift the body
//       (not the mod's own carriers: a match prisoner's handler lifting it on the return, task 6.3, a Scratch medic lifting a town fighter,
//       task 7.4, CombatMatchView.IsOwnCarrier); task 7.4 adds the C++'s ArenaMedical pickupObject_hook (ArenaMedical.cpp:64-71): only a Scratch
//       medic lifts a town bout fighter while the town owns the match (CombatMatchView.BlocksTownBystander)
//   ArenaMedical's shouldIHelpThisGuy_hook (ArenaMedical.cpp:57-62, task 7.4) ... postfix on private FirstAidSystem.FindPatient (Finder.cs:381)
//   (port only, CAP 1 "Long squad memory") ... prefix on private FactionDeeds.Remember (Relations.cs:185): no long squad memory between two
//       fighters of the running match (the original had no such memory)
//   Install (:270-371) ... CombatHookPatches.Install (the C++'s PGLog texts for the hooks that have a counterpart)
//
// Threading (verified in the 0.17.3 source):
//   ApplyBlow: its only caller is the public CombatSystem.Apply (Apply.cs:55), and Apply is called only by World.Commit's loop over the sorted
//       effects (World.cs:710-727), step 6 "serial" (ITickSystem.Apply, World.cs:56-60; RunTick, World.cs:606-640). The hit observers run inside
//       it, serially on the tick thread (CombatHit.cs:48-53, World.CombatHits.cs:71-91). So the stats are counted directly in the postfix
//       (SparStats and SparSession are read only on the tick thread); no queue is needed.
//   BestAttackerOf: called on the parallel think (AiSystem.SelfDefencePick and ProtectPick "read only the last tick's state (any thread in the
//       think)", AiSystem.Fights.cs:155-172, :370-391) and in serial steps (Fighting.cs:718-722, :991).
//   FindFoe: "a pure read of the last looks and fights: any thread in the think" (AggroSystem.cs:1776-1785), from AiSystem.Fights.cs:115-119.
//   BeginPickUp: "serial phases" (CarrySystem.cs:538-544).
//   FactionDeeds.Remember: from FactionDeeds.Blow (Relations.cs:150-152), from LandBlow's consequences in the commit step (Apply.cs:327).
//   Task 7.4: CrimeSystem.SetPending runs from its callers' serial steps (an order's Assault CombatSystem.cs:465, :813; the commit step's Hit
//   Apply.cs:319 and the bolt hit Ranged.cs:815; the stealth knock-out, stealing, locks, cages: their serial steps); FactionDeeds.Blow in LandBlow's
//   consequences (Apply.cs:327, commit) and the bolt hit (Ranged.cs:825); FindPatient in the serial NPC looks, goal treatments and job checks
//   (FirstAidSystem.cs:807-955). They read the view, and the patient patches one record id.
//   So every patch except ApplyBlow's reads only the CombatMatchView this object publishes on its serial steps (the end of the system's Inputs
//   and SlowWorld) through one volatile reference: an immutable object keyed by table slot, never SparSession's lists, never the world table.
//   The slot key is the C++'s Character* compare; a fighter's slot can be reused only after the fighter left the table, which stops the match
//   (KOWatcher's invalid-participant scan) and republishes the view.
//
// Callers: ProvingGroundsSystem (HitResolved, AfterSerialStep at the end of Inputs and SlowWorld); the patches find this world's object through
// ProvingGroundsSystem.HooksFor(world).

/// <summary>
/// The per-world half of CombatHooks: publishes the match view the patches read, counts each blow between sparring opponents into SparStats and
/// records the defender's reaction for FightStarter, and runs the counter fallback when the ApplyBlow patch is not on. One object per world, owned
/// by ProvingGroundsSystem.
/// </summary>
sealed class CombatHooks
{
    internal sealed record Services(SparSession Session, SparStats Stats, FightStarter FightStarter, PrisonerUtil Prisoners, Town.TownArena Town);

    readonly Services s;
    volatile CombatMatchView<int> view = CombatMatchView<int>.Idle(-1);
    HitPairing pairing;                                    // commit step only
    readonly CounterFallback<CharacterId> fallback = new();
    bool fallbackRunning;
    long blowsCounted, fallbackCounts;

    // scratch for Publish (sim thread), reused
    int[] fighters = new int[8];
    MatchRules.MatchTeam[] teams = new MatchRules.MatchTeam[8];
    bool[] eliminated = new bool[8];
    int[] townSlots = new int[4], prisonerSlots = new int[4], handlerSlots = new int[4], incidentSlots = new int[4];
    MatchRules.MatchTeam[] incidentTeamSlots = new MatchRules.MatchTeam[4];
    readonly List<CharacterId> town = [], prisoners = [], handlers = [], incident = [];
    readonly List<MatchRules.MatchTeam> incidentTeams = [];

    CombatSystem? combat;
    World? combatWorld;

    internal CombatHooks(Services services) => s = services;

    /// <summary>The published match view (any thread).</summary>
    internal CombatMatchView<int> View => view;

    /// <summary>The F10 phase row (sim thread: built by ProvingGroundsSystem.Phases() when the snapshot is published).</summary>
    internal string Phase
    {
        get
        {
            var v = view;
            string feed = CombatHookPatches.BlowFeedOn ? "blow feed on" : "counter fallback";
            return string.Create(CultureInfo.InvariantCulture,
                $"{feed}, isolation {(CombatHookPatches.IsolationOn ? "on" : "off")}, town rules {(CombatHookPatches.TownRulesOn ? "on" : "off")}; {blowsCounted + fallbackCounts} blows counted; view: {(v.Active ? v.FighterCount + " fighters" : "no match")}, {v.IsolatedCount} isolated, {v.IncidentCount} in a town bout");
        }
    }

    // ---------- the serial steps ----------

    /// <summary>The end of each of the system's serial steps (Inputs, SlowWorld): republishes the view when the match changed, then the counter
    /// fallback when the ApplyBlow feed is not on.</summary>
    internal void AfterSerialStep(World world)
    {
        Publish(world);
        if (!CombatHookPatches.BlowFeedOn) Fallback(world);
    }

    /// <summary>Builds the match view from SparSession (its fighters, teams, who is out, the active pair and whether a walk-in is pending, which
    /// SparSession reads through ArenaIngress), TownArena (its bout fighters; task 7.4: the incident state and OwnsMatch) and PrisonerUtil into the
    /// reused scratch arrays and
    /// publishes a new immutable view only when it differs from the current one (no allocation on an unchanged step).</summary>
    void Publish(World world)
    {
        var session = s.Session;
        bool active = session.IsActive(world);
        int n = active ? session.GetParticipantCount() : 0;
        if (fighters.Length < n) { fighters = new int[n]; teams = new MatchRules.MatchTeam[n]; eliminated = new bool[n]; }
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            if (session.GetParticipant(i) is not { } id) continue;   // never a none slot (StartMatch refuses one); skipped like the C++'s NULL
            fighters[count] = id.Slot;
            teams[count] = session.GetTeam(id);
            eliminated[count] = session.IsEliminated(world, id);
            count++;
        }
        int activeA = active ? SlotOf(session.GetActiveA()) : -1, activeB = active ? SlotOf(session.GetActiveB()) : -1;
        bool walkIn = active && session.HasPendingWalkIn(world);
        town.Clear(); s.Town.CopyFighters(world, town);
        prisoners.Clear(); handlers.Clear(); s.Prisoners.CopyMatchPrisoners(world, prisoners, handlers);
        int tn = Slots(town, ref townSlots), pn = PrisonerSlots(prisoners, handlers, ref prisonerSlots, ref handlerSlots);
        // task 7.4: the town rules' state (TownArena.cpp:1415-1438 IsIncidentPair / IsActiveCollateralIncidentPair, ArenaMedical.cpp:53 OwnsMatch)
        incident.Clear(); incidentTeams.Clear();
        s.Town.CopyIncidentState(world, incident, incidentTeams, out bool retained, out bool collateral);
        bool townOwns = s.Town.OwnsMatch(world);
        int inc = IncidentSlots(incident, incidentTeams, ref incidentSlots, ref incidentTeamSlots);
        var mode = active ? session.GetMode() : MatchRules.MatchMode.TeamAvB;

        var cur = view;
        if (cur.Matches(active, mode, fighters.AsSpan(0, count), teams.AsSpan(0, count), eliminated.AsSpan(0, count), activeA, activeB, walkIn,
                townSlots.AsSpan(0, tn), prisonerSlots.AsSpan(0, pn), handlerSlots.AsSpan(0, pn), incidentSlots.AsSpan(0, inc),
                incidentTeamSlots.AsSpan(0, inc), retained, collateral, townOwns))
            return;
        view = new CombatMatchView<int>(-1, active, mode, fighters.AsSpan(0, count), teams.AsSpan(0, count), eliminated.AsSpan(0, count),
            activeA, activeB, walkIn, townSlots.AsSpan(0, tn), prisonerSlots.AsSpan(0, pn), handlerSlots.AsSpan(0, pn), incidentSlots.AsSpan(0, inc),
            incidentTeamSlots.AsSpan(0, inc), retained, collateral, townOwns);
    }

    /// <summary>Task 7.4: the town bout's fighters' slots with their teams, aligned; a none fighter is skipped with its team (the C++ compares
    /// <c>g_fighters[i] == a</c> with a non-NULL a, so a NULL entry never matches).</summary>
    static int IncidentSlots(List<CharacterId> ids, List<MatchRules.MatchTeam> idTeams, ref int[] into, ref MatchRules.MatchTeam[] teamsInto)
    {
        if (into.Length < ids.Count) { into = new int[ids.Count]; teamsInto = new MatchRules.MatchTeam[ids.Count]; }
        int k = 0;
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i].IsNone) continue;
            into[k] = ids[i].Slot;
            teamsInto[k] = idTeams[i];
            k++;
        }
        return k;
    }

    static int SlotOf(CharacterId id) => id.IsNone ? -1 : id.Slot;

    /// <summary>The match prisoners' slots and, aligned with them, their handlers' slots (-1: none), for the view's own-carrier exemption (task
    /// 6.3); a none prisoner is skipped with its handler.</summary>
    static int PrisonerSlots(List<CharacterId> prisoners, List<CharacterId> handlers, ref int[] into, ref int[] handlersInto)
    {
        if (into.Length < prisoners.Count) into = new int[prisoners.Count];
        if (handlersInto.Length < prisoners.Count) handlersInto = new int[prisoners.Count];
        int k = 0;
        for (int i = 0; i < prisoners.Count; i++)
        {
            if (prisoners[i].IsNone) continue;
            into[k] = prisoners[i].Slot;
            handlersInto[k] = i < handlers.Count ? SlotOf(handlers[i]) : -1;
            k++;
        }
        return k;
    }

    static int Slots(List<CharacterId> ids, ref int[] into)
    {
        if (into.Length < ids.Count) into = new int[ids.Count];
        int k = 0;
        foreach (var id in ids) if (!id.IsNone) into[k++] = id.Slot;
        return k;
    }

    // ---------- the blow (the commit step, CombatSystem.ApplyBlow) ----------

    /// <summary>What the ApplyBlow prefix hands its postfix (Harmony's __state).</summary>
    internal struct BlowState
    {
        public bool Ours, Cancelled;
        public BlowCounters Before;
    }

    /// <summary>C++: CombatHooks.cpp:202 (before the original call). Only for a blow between two fighters of the running match (the published
    /// view; everything else in the world returns at once): the attacker's counters, whether its swing was already cancelled this tick (ApplyBlow
    /// then returns at once, Apply.cs:81) and the pairing key for the damage the observer will report.</summary>
    internal void BlowPrefix(CombatSystem combat, World world, in Effect e, ref BlowState state)
    {
        var v = view;
        if (!v.Active || !v.IsParticipant(e.Source) || !v.IsParticipant(e.Target)) return;
        if (CombatHookPatches.WriteOf(combat) is not { } w || (uint)e.Source >= (uint)w.Length) return;
        ref var a = ref w[e.Source];
        state.Ours = true;
        state.Cancelled = a.CancelTick == (int)world.Tick;
        state.Before = new BlowCounters(a.Landed, a.Parried, a.Evaded);
        pairing.Expect(e.Source, e.Target, (int)e.Amount, e.Arg, world.Tick);
    }

    /// <summary>C++: CombatHooks.cpp:203-248 (after the original call): classify the blow by what moved, take its damage from the pairing, and
    /// for sparring opponents (SparSession.IsSparringOpponent, live: this is a serial step) feed SparStats and the defender's reaction.</summary>
    internal void BlowPostfix(CombatSystem combat, World world, in Effect e, in BlowState state)
    {
        if (!state.Ours) return;
        // idempotent: a second copy of this postfix (the patch applied twice, e.g. PgCheck hooks over a live game) finds the blow taken
        if (!pairing.TakeFor(e.Source, e.Target, (int)e.Amount, e.Arg, world.Tick, out bool hasDamage, out float applied, out float incoming)) return;
        if (CombatHookPatches.WriteOf(combat) is not { } w || (uint)e.Source >= (uint)w.Length) return;
        ref var a = ref w[e.Source];
        var kind = CombatHookLogic.Classify(state.Before, new BlowCounters(a.Landed, a.Parried, a.Evaded), state.Cancelled);
        if (kind == BlowKind.None) return;
        var table = world.Characters;
        CharacterId attacker = table.IdOf(e.Source), defender = table.IdOf(e.Target);   // the ids ApplyBlow's own observer call uses (Apply.cs:210)
        bool sparring = s.Session.IsSparringOpponent(world, attacker, defender);
        if (sparring) blowsCounted++;
        CombatHookLogic.Feed(new Sink(this, world), sparring, kind, attacker, defender, hasDamage, applied, incoming);
    }

    /// <summary>The feed's targets for one blow: SparStats (with the session as its roster) and FightStarter's reaction ring (answered in the
    /// next serial step, as task 3.1's hit path). A struct passed generically: no allocation.</summary>
    readonly struct Sink(CombatHooks h, World world) : IBlowSink<CharacterId>
    {
        public void OnHit(CharacterId attacker, CharacterId defender, float appliedDamage, float incomingDamage, bool isMiss) =>
            h.s.Stats.OnHit(world, h.s.Session, attacker, defender, appliedDamage, incomingDamage, isMiss);
        public void OnBlock(CharacterId defender, CharacterId attacker) => h.s.Stats.OnBlock(world, h.s.Session, defender, attacker);
        public void React(CharacterId defender, CharacterId attacker) => h.s.FightStarter.RecordIncomingAttack(defender, attacker);
    }

    // ---------- Meitou's hit observer (ProvingGroundsSystem.HitResolved) ----------

    /// <summary>From ProvingGroundsSystem's <c>ICombatHitObserver.HitResolved</c> (serial, inside ApplyBlow's LandBlow): with the ApplyBlow feed on,
    /// it only offers the hit's damage to the pairing (the postfix counts the blow); without it (fallback), while a match runs, it counts the hit
    /// and records the reaction itself (task 3.1's path). Never changes the world (CAP 3).</summary>
    internal void HitResolved(World world, in CombatHit hit)
    {
        if (hit.Source != CombatHitSource.Melee) return;
        var d = hit.Damage;
        var b = hit.Absorbed;
        if (CombatHookPatches.BlowFeedOn)
        {
            if (!pairing.Expecting) return;
            var (applied, incoming) = CombatHookLogic.Damage(d.Cut, d.Blunt, d.Pierce, b.Cut, b.Blunt, b.Pierce);
            pairing.Offer(hit.Attacker.Slot, hit.Victim.Slot, hit.Sequence, hit.Blow, hit.Tick, applied, incoming);
            return;
        }
        if (!s.Session.IsActive(world)) return;
        var (ap, inc) = CombatHookLogic.Damage(d.Cut, d.Blunt, d.Pierce, b.Cut, b.Blunt, b.Pierce);
        if (s.Session.IsSparringOpponent(world, hit.Attacker, hit.Victim)) fallbackCounts++;
        s.Stats.OnHit(world, s.Session, hit.Attacker, hit.Victim, ap, inc, false);
        s.FightStarter.RecordIncomingAttack(hit.Victim, hit.Attacker);
    }

    // ---------- the counter fallback (no ApplyBlow patch) ----------

    void Fallback(World world)
    {
        if (!s.Session.IsActive(world))
        {
            if (fallbackRunning) { fallback.Reset(); fallbackRunning = false; }
            return;
        }
        if (!fallbackRunning) { fallback.Reset(); fallbackRunning = true; }
        if (!ReferenceEquals(combatWorld, world) || combat is null)
        {
            combat = PgEngine.FindCombat(world);
            combatWorld = combat is null ? null : world;   // a failed lookup is tried again next step
        }
        if (combat is null) return;
        var sink = new CounterSink(this, world);
        int n = s.Session.GetParticipantCount();
        for (int i = 0; i < n; i++)
        {
            if (s.Session.GetParticipant(i) is not { } id) continue;
            var st = combat.StateOf(id);   // public, as of the last finished tick (CombatSystem.cs:250); our SlowWorld runs after combat's swap
            fallback.Observe(id, st.Blocks, st.Evaded, st.Dodges, sink);
        }
    }

    readonly struct CounterSink(CombatHooks h, World world) : ICounterSink<CharacterId>
    {
        public void AddCounts(CharacterId fighter, int blocks, int misses, int dodges)
        {
            h.fallbackCounts += blocks + misses + dodges;
            h.s.Stats.AddUnpairedCounts(world, h.s.Session, fighter, blocks, misses, dodges);
        }
    }
}

/// <summary>
/// The Harmony patches of CombatHooks (task 3.2b) and their installer (C++: CombatHooks.cpp:270-371 <c>Install</c>). Each patch is "a good
/// neighbour" (GuildQuests patches BeginPickUp too, CAP 2): a postfix or prefix that acts only on our pairs and never replaces the method for
/// anyone else; the isolation postfixes run last (<c>Priority.Last</c>) so they see what other mods' postfixes decided, and the pick-up prefix
/// does nothing when another prefix already skipped the original. A patch never throws into the game: a failure is logged at most three times
/// per patch, the game's own result stands, and a patch whose work fails <see cref="OffAfterFailures"/> times in a row turns that work off for
/// the process (one log line).
/// </summary>
static class CombatHookPatches
{
    // The patches, in Patches()' order; each index names the patch's failure counters and its install flag.
    internal const int BlowPatch = 0, AttackerPatch = 1, FoePatch = 2, PickUpPatch = 3, MemoryPatch = 4, SetPendingPatch = 5, DeedsPatch = 6,
        PatientPatch = 7, PatchCount = 8;

    /// <summary>Consecutive failures after which a patch's work is turned off for the process.</summary>
    internal const int OffAfterFailures = 20;

    /// <summary>One patch: its <see cref="PgPatch"/>, its index and the C++ hook it stands for (its PGLog install line; null: port only).</summary>
    sealed record Hook(int Index, PgPatch Patch, string? CppName);

    /// <summary>The ApplyBlow pre/postfix pair is on and <c>_write</c> is readable: the per-blow stats feed (else the counter fallback).</summary>
    internal static volatile bool BlowFeedOn;
    /// <summary>All three isolation patches are on (BestAttackerOf and FindFoe postfixes, BeginPickUp prefix).</summary>
    internal static volatile bool IsolationOn;
    /// <summary>Task 7.4: the town rules' crime and relation patches are both on (CrimeSystem.SetPending prefix, FactionDeeds.Blow prefix):
    /// the C++ install's "town arena incident exemptions" group.</summary>
    internal static volatile bool TownRulesOn;

    /// <summary>Fast access to CombatSystem's private <c>_write</c> (Members.CombatWrite), built once; null when the field is missing or not a
    /// CombatSlot[].</summary>
    static readonly AccessTools.FieldRef<CombatSystem, CombatSlot[]>? WriteRef = BuildWriteRef();
    static readonly int[] failures = new int[PatchCount];      // logged failures per patch (at most 3 lines each)
    static readonly int[] consecutive = new int[PatchCount];   // failures in a row per patch
    static readonly int[] off = new int[PatchCount];           // 1: the patch's work is off for the process

    static AccessTools.FieldRef<CombatSystem, CombatSlot[]>? BuildWriteRef()
    {
        try
        {
            return Members.CombatWrite is { } f && f.FieldType == typeof(CombatSlot[]) ? AccessTools.FieldRefAccess<CombatSystem, CombatSlot[]>(f) : null;
        }
        catch { return null; }
    }

    /// <summary>Whether the fast <c>_write</c> accessor was built (PgCheck hooks prints it).</summary>
    internal static bool WriteRefBuilt => WriteRef is not null;

    internal static CombatSlot[]? WriteOf(CombatSystem combat) => WriteRef is { } r ? r(combat) : null;

    static Hook H(int index, System.Reflection.MethodBase? target, string? pre, string? post, string what, string? cppName,
        int priority = Priority.Normal) =>
        new(index, new(Part.CombatHooks, what, target, pre is null ? null : PgPatches.Method(typeof(CombatHookPatches), pre),
            post is null ? null : PgPatches.Method(typeof(CombatHookPatches), post), priority), cppName);

    static readonly Hook[] hooks =
    [
        H(BlowPatch, Members.ApplyBlow, nameof(BeforeApplyBlow), nameof(AfterApplyBlow), "CombatSystem.ApplyBlow prefix + postfix",
            "CombatClass::_iHitYouAreYouHit"),
        H(AttackerPatch, Members.BestAttackerOf, null, nameof(AfterBestAttackerOf), "CombatSystem.BestAttackerOf postfix", "Character::attackTarget",
            Priority.Last),
        H(FoePatch, Members.FindFoe, null, nameof(AfterFindFoe), "AggroSystem.FindFoe postfix", "Character::iShouldntAggravateThisTarget",
            Priority.Last),
        H(PickUpPatch, Members.BeginPickUp, nameof(BeforeBeginPickUp), null, "CarrySystem.BeginPickUp prefix", null),
        H(MemoryPatch, Members.FactionRemember, nameof(BeforeRemember), null, "FactionDeeds.Remember prefix", null),
        // task 7.4: the town rules (their C++ install texts are written by Install as the C++ groups them)
        H(SetPendingPatch, Members.CrimeSetPending, nameof(BeforeSetPending), null, "CrimeSystem.SetPending prefix", null),
        H(DeedsPatch, Members.DeedsBlow, nameof(BeforeDeedsBlow), null, "FactionDeeds.Blow prefix", null),
        H(PatientPatch, Members.FindPatient, null, nameof(AfterFindPatient), "FirstAidSystem.FindPatient postfix", null, Priority.Last),
    ];

    /// <summary>The combat hooks' Harmony patches (applied by <see cref="Install"/> and by PgCheck hooks, through PgPatches.Apply).</summary>
    internal static IEnumerable<PgPatch> Patches() => hooks.Select(h => h.Patch);

    /// <summary>C++: CombatHooks.cpp:270-371. Each patch alone: one whose members are missing or that Harmony refuses is off with one line,
    /// the others stay on. The PGLog texts are the C++'s for the hooks with a counterpart (each hook's CppName); the Meitou log names the real
    /// patch.</summary>
    internal static void Install(ModContext c)
    {
        try
        {
            var missing = Members.MissingCombatHooks().ToList();
            if (missing.Count > 0) c.LogError($"combat hooks: {string.Join(", ", missing)} not found (those hooks off)");
            var on = new bool[PatchCount];
            foreach (var h in hooks)
            {
                var p = h.Patch;
                if (h.Index == BlowPatch && p.Target is not null && WriteRef is null)
                    c.LogError($"combat hook off: {p.What}: CombatSystem._write not readable (counter fallback)");
                else if (p.Target is not null)
                {
                    try { PgPatches.Apply(c.Harmony, p); on[h.Index] = true; c.Log($"combat hook on: {p.What}"); }
                    catch (Exception e) { c.LogError($"combat hook off: {p.What}: {e.GetType().Name}: {e.Message}"); }
                }
                if (h.CppName is { } n)
                {
                    if (on[h.Index]) PGLog.Debug("Proving Grounds: hooked " + n);
                    else PGLog.Error("Proving Grounds: failed to hook " + n);
                }
            }
            // C++ CombatHooks.cpp:281-290: the five crime / relation / campaign hooks install as one group with one line (Meitou: the crime
            // prefix and the relation prefix); ArenaMedical.cpp:272-286: the shouldIHelpThisGuy / pickupObject failure lines (Meitou: the
            // FindPatient postfix and the BeginPickUp prefix); the C++ melee-incident line (:273-279) has no counterpart (no incident context).
            if (on[SetPendingPatch] && on[DeedsPatch]) PGLog.Debug("Proving Grounds: installed town arena incident exemptions");
            else PGLog.Error("Proving Grounds: failed to install town arena incident exemptions");
            if (!on[PatientPatch]) PGLog.Error("Proving Grounds: failed to hook Character::shouldIHelpThisGuy");
            if (!on[PickUpPatch]) PGLog.Error("Proving Grounds: failed to hook Character::pickupObject");
            BlowFeedOn = on[BlowPatch];
            IsolationOn = on[AttackerPatch] && on[FoePatch] && on[PickUpPatch];
            TownRulesOn = on[SetPendingPatch] && on[DeedsPatch];
            if (!BlowFeedOn) c.LogError("combat hooks: per-blow stats from the combat counters (fallback; spike S2)");
        }
        catch (Exception e) { c.LogError($"combat hooks off: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>Whether a patch still does its work (not turned off after repeated failures). Any thread: one volatile read.</summary>
    static bool Works(int patch) => Volatile.Read(ref off[patch]) == 0;

    /// <summary>A patch's work succeeded: the run of failures ends. Writes only when a run was counting (no shared write on the hot path).</summary>
    static void Succeeded(int patch)
    {
        if (Volatile.Read(ref consecutive[patch]) != 0) Volatile.Write(ref consecutive[patch], 0);
    }

    /// <summary>A patch's work threw: logged at most three times per patch; after <see cref="OffAfterFailures"/> in a row its work is off for
    /// the process (one line). Never throws itself (a failing log is swallowed).</summary>
    static void Failed(int patch, string where, Exception e)
    {
        try
        {
            if (Interlocked.Increment(ref consecutive[patch]) >= OffAfterFailures && Interlocked.Exchange(ref off[patch], 1) == 0)
                ProvingGroundsMod.Ctx?.LogError($"combat hook {where}: {OffAfterFailures} failures in a row, turned off for this session (the game runs as without it)");
            if (Interlocked.Increment(ref failures[patch]) <= 3)
                ProvingGroundsMod.Ctx?.LogError($"combat hook {where} failed (the game's own result stands): {e.GetType().Name}: {e.Message}");
        }
        catch { }
    }

    // ---------- CombatSystem.ApplyBlow (C++ iHitYouAreYouHit_hook, :199-250) ----------

    /// <summary>Prefix on private CombatSystem.ApplyBlow(World world, in Effect e) (CombatSystem.Apply.cs:70); serial (the commit step).</summary>
    static void BeforeApplyBlow(CombatSystem __instance, World world, in Effect e, out CombatHooks.BlowState __state)
    {
        using var perfScope = PgPerf.Measure(PgPerf.ApplyBlowPrefix);   // task 9.2a: the cost instrument (off: one volatile read)
        __state = default;
        if (!Works(BlowPatch)) return;
        try { ProvingGroundsSystem.HooksFor(world)?.BlowPrefix(__instance, world, e, ref __state); Succeeded(BlowPatch); }
        catch (Exception ex) { __state = default; Failed(BlowPatch, "ApplyBlow prefix", ex); }
    }

    /// <summary>Postfix on CombatSystem.ApplyBlow. Idempotent (CombatHooks.BlowPostfix takes the blow from the pairing only once).</summary>
    static void AfterApplyBlow(CombatSystem __instance, World world, in Effect e, CombatHooks.BlowState __state)
    {
        using var perfScope = PgPerf.Measure(PgPerf.ApplyBlowPostfix);   // task 9.2a: the cost instrument (off: one volatile read)
        if (!__state.Ours || !Works(BlowPatch)) return;
        try { ProvingGroundsSystem.HooksFor(world)?.BlowPostfix(__instance, world, e, __state); Succeeded(BlowPatch); }
        catch (Exception ex) { Failed(BlowPatch, "ApplyBlow postfix", ex); }
    }

    // ---------- isolation (C++ attackTarget_hook :252-258, iShouldntAggravateThisTarget_hook :260-265); any thread: the view only ----------

    /// <summary>Postfix on public CombatSystem.BestAttackerOf(World world, int subject, int looker, ...) (CombatSystem.Targets.cs:202): the foe
    /// <paramref name="looker"/> would join against is dropped (-1) when the two are an isolated pair (<see cref="CombatMatchView{TId}.DropsFoe"/>).
    /// Parallel think or serial: reads only the published view; no allocation, no lock. Runs last among the method's postfixes.</summary>
    static void AfterBestAttackerOf(World world, int looker, ref int __result)
    {
        using var perfScope = PgPerf.Measure(PgPerf.BestAttackerOf);   // task 9.2a: the cost instrument (off: one volatile read)
        if (__result < 0 || !Works(AttackerPatch)) return;
        try
        {
            if (ProvingGroundsSystem.HooksFor(world)?.View is { } v && v.DropsFoe(looker, __result)) __result = -1;
            Succeeded(AttackerPatch);
        }
        catch (Exception ex) { Failed(AttackerPatch, "BestAttackerOf postfix", ex); }
    }

    /// <summary>Postfix on public AggroSystem.FindFoe(World world, int slot, FoeFinder finder, ...) (AggroSystem.cs:1786): no foe
    /// (<see cref="CharacterId.None"/>) when <paramref name="slot"/> and the foe are an isolated pair. Parallel think: the view only. Runs last.</summary>
    static void AfterFindFoe(World world, int slot, ref CharacterId __result)
    {
        using var perfScope = PgPerf.Measure(PgPerf.FindFoe);   // task 9.2a: the cost instrument (off: one volatile read)
        if (__result.IsNone || !Works(FoePatch)) return;
        try
        {
            if (ProvingGroundsSystem.HooksFor(world)?.View is { } v && v.DropsFoe(slot, __result.Slot)) __result = CharacterId.None;
            Succeeded(FoePatch);
        }
        catch (Exception ex) { Failed(FoePatch, "FindFoe postfix", ex); }
    }

    /// <summary>Prefix on public CarrySystem.BeginPickUp(World world, int carrier, CharacterId body, ...) (CarrySystem.cs:545): an isolated pair's
    /// carrier does not lift the body (<see cref="CombatMatchView{TId}.RefusesPickUp"/>; the original keeps outsiders from engaging a match
    /// prisoner, CombatHooks.cpp:252-265; Meitou's town duty, slavers and police lift knocked-out bodies, CAP 2), and (task 7.4, C++
    /// pickupObject_hook ArenaMedical.cpp:64-71) only a Scratch medic lifts a town bout fighter while the town owns the match (the record is read
    /// only when the body is a town fighter). Refuses only those cases (false, original skipped); otherwise, or when another mod's prefix already
    /// skipped the original, runs as without the mod.</summary>
    static bool BeforeBeginPickUp(World world, int carrier, CharacterId body, ref bool __result, bool __runOriginal)
    {
        using var perfScope = PgPerf.Measure(PgPerf.BeginPickUp);   // task 9.2a: the cost instrument (off: one volatile read)
        if (!__runOriginal || body.IsNone || !Works(PickUpPatch)) return true;
        try
        {
            // the IsTownFighter test (RefusesPickUp tests it again) gates the medic test's record read to town fighter bodies
            bool refuse = ProvingGroundsSystem.HooksFor(world)?.View is { } v
                && v.RefusesPickUp(carrier, body.Slot, v.IsTownFighter(body.Slot) && Town.TownCombatRules.IsScratchMedic(world, carrier));
            Succeeded(PickUpPatch);
            if (!refuse) return true;
            __result = false;
            return false;
        }
        catch (Exception ex) { Failed(PickUpPatch, "BeginPickUp prefix", ex); return true; }
    }

    /// <summary>Prefix on private FactionDeeds.Remember(World world, int striker, int victim, bool dead) (Relations.cs:185): skipped when both are
    /// fighters of the running match (<see cref="CombatMatchView{TId}.SkipsSquadMemory"/>; two player squads, or two town squads, would
    /// otherwise book DEFEATED_MY_SQUAD / SQUAD_LOST_TO_ME / KILLED_MY_FRIEND, CAP 1 "Long squad memory"; the original had no such memory).
    /// Serial (the commit step).</summary>
    static bool BeforeRemember(World world, int striker, int victim)
    {
        using var perfScope = PgPerf.Measure(PgPerf.Remember);   // task 9.2a: the cost instrument (off: one volatile read)
        if (!Works(MemoryPatch)) return true;
        try
        {
            bool skip = ProvingGroundsSystem.HooksFor(world)?.View is { } v && v.SkipsSquadMemory(striker, victim);
            Succeeded(MemoryPatch);
            return !skip;
        }
        catch (Exception ex) { Failed(MemoryPatch, "FactionDeeds.Remember prefix", ex); return true; }
    }

    // ---------- the town rules (task 7.4, src/Town/TownCombatRules.cs); serial steps; the view only (and a record id for the medic test) ----------

    /// <summary>C++ setCrime_hook (CombatHooks.cpp:94-114): prefix on public CrimeSystem.SetPending(World world, int culprit, CrimeCode code, int
    /// victimFaction, CharacterId victim, ...) (CrimeSystem.cs:117, the game's setter FUN_140852c80 = BountyManager::setCrime): an assault or murder
    /// (Meitou's codes 5 and 4; VIP assault = Treason (6), which nothing in Meitou 0.17.3 sets; CombatHookLogic.IsCombatCrime) between a sanctioned culprit and victim is not set (false, original skipped;
    /// <see cref="CombatMatchView{TId}.SkipsCrime"/>), whoever calls it; for a collateral pair the C++'s debug line (crime = the code). Everything else,
    /// or when another prefix already skipped the original, runs as without the mod. Serial (its callers' steps).</summary>
    static bool BeforeSetPending(World world, int culprit, Meitou.Simulation.Crime.CrimeCode code, CharacterId victim, ref bool __result,
        bool __runOriginal)
    {
        using var perfScope = PgPerf.Measure(PgPerf.SetPending);   // task 9.2a: the cost instrument (off: one volatile read)
        if (!__runOriginal || victim.IsNone || !Works(SetPendingPatch)) return true;
        try
        {
            bool combatCrime = CombatHookLogic.IsCombatCrime((int)code);
            var v = ProvingGroundsSystem.HooksFor(world)?.View;
            bool skip = v is not null && v.SkipsCrime(combatCrime, culprit, victim.Slot);
            Succeeded(SetPendingPatch);
            if (!skip) return true;
            if (v!.IsCollateralPair(culprit, victim.Slot))
            {
                var table = world.Characters;
                string culpritName = (uint)culprit < (uint)table.HighWater ? PgEngine.Name(world, table.IdOf(culprit)) ?? "<invalid>" : "<invalid>";
                PGLog.Debug("Proving Grounds: exempted town arena collateral crime=" + ((int)code).ToString(CultureInfo.InvariantCulture)
                    + " attacker=" + culpritName + " victim=" + (PgEngine.Name(world, victim) ?? "<invalid>"));
            }
            __result = false;
            return false;
        }
        catch (Exception ex) { Failed(SetPendingPatch, "CrimeSystem.SetPending prefix", ex); return true; }
    }

    /// <summary>C++ relationEvent_hook / setEnemy_hook (CombatHooks.cpp:124-148, inside the blow's incident context :82-89, 199-201): prefix on
    /// public FactionDeeds.Blow(World world, int striker, int victim, bool down, bool dead, bool bolt, bool aimed) (Relations.cs:150): skipped for a
    /// town bout's sanctioned pair (no faction event of the blow and no squad memory), with the C++'s debug line for a collateral pair. Serial
    /// (LandBlow, the bolt hit).</summary>
    static bool BeforeDeedsBlow(World world, int striker, int victim, bool __runOriginal)
    {
        using var perfScope = PgPerf.Measure(PgPerf.DeedsBlow);   // task 9.2a: the cost instrument (off: one volatile read)
        if (!__runOriginal || !Works(DeedsPatch)) return true;
        try
        {
            var v = ProvingGroundsSystem.HooksFor(world)?.View;
            bool skip = v is not null && v.IsSanctionedPair(striker, victim);
            Succeeded(DeedsPatch);
            if (!skip) return true;
            if (v!.IsCollateralPair(striker, victim)) PGLog.Debug("Proving Grounds: exempted town arena collateral faction event");
            return false;
        }
        catch (Exception ex) { Failed(DeedsPatch, "FactionDeeds.Blow prefix", ex); return true; }
    }

    /// <summary>C++ shouldIHelpThisGuy_hook (ArenaMedical.cpp:57-62): postfix on private FirstAidSystem.FindPatient(World world, int i, ...)
    /// (FirstAidSystem.Finder.cs:381): the patient the finder chose for medic <paramref name="i"/> is dropped (no patient this look) when
    /// <see cref="CombatMatchView{TId}.BlocksTownBystander"/>: a town bout fighter, the town owns the match, the medic is not a Scratch medic (its
    /// record is read only in that case). Serial (the NPC looks, the goal treatments, the medic jobs). Runs last.</summary>
    static void AfterFindPatient(World world, int i, ref ValueTuple<int, float> __result)
    {
        using var perfScope = PgPerf.Measure(PgPerf.FindPatient);   // task 9.2a: the cost instrument (off: one volatile read)
        if (__result.Item1 < 0 || !Works(PatientPatch)) return;
        try
        {
            // IsTownFighter first (BlocksTownBystander tests it again): it gates the record read of the medic test to town fighter patients
            if (ProvingGroundsSystem.HooksFor(world)?.View is { } v && v.IsTownFighter(__result.Item1)
                && v.BlocksTownBystander(__result.Item1, Town.TownCombatRules.IsScratchMedic(world, i)))
                __result = (-1, 0f);
            Succeeded(PatientPatch);
        }
        catch (Exception ex) { Failed(PatientPatch, "FindPatient postfix", ex); }
    }
}

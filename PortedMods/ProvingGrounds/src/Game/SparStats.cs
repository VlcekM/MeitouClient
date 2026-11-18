using System.Globalization;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/SparStats.h/.cpp (Proving Grounds by Shutori, GPLv3), task 3.2a (PA §2.8, §2.9). The counter table,
// the elimination order, the frozen snapshot and the podium fighters are Core/Glue/SparStatsState.cs (one function each, the C++ order, tested
// with fakes in Tests/Glue/SparStatsTests.cs); this file binds them to the world and to the match roster. Meitou 0.17.3 (tag v0.17.3, fa34a1b).
// Every method runs on the sim thread in a serial step.
//
// Engine calls of the C++ and what replaces them (E1; adapter methods in src/Engine/PgEngine.cs):
//   Character* identity (g_live[i].character == c, g_elimOrder, ParticipantId) ... CharacterId equality (a stale id never equals a live one)
//   c->getName() (Freeze :178) ... PgEngine.Name(world, id) (CharacterCold.Name), cut to 63 UTF-8 bytes (SparStatsState.CutName)
//   SparSession::GetParticipantCount / GetParticipant / GetTeam / IsSparringOpponent (:70-73, :103, :134, :145, :167-174, :226-230) ...
//       ISparSessionRoster, passed by the caller to each call that reads the match (src/Game/SparSession.cs passes itself, task 3.3)
//   returning a Character* from GetPodiumCharacter (:258-263) ... the CharacterId captured at freeze time, resolved against this tick's table
//       when asked (PgEngine.Exists): a stale id gives null (the C++ would hand out a dangling pointer)
// Callers (C++ -> port): CombatHooks.cpp:229, :235 OnBlock/OnHit (task 3.2b's patches); KOWatcher.cpp:131 OnEliminated (3.3); SparSession.cpp:661
// ResetForMatch, :710-717 Freeze / GetMutableSnapshot / GetSnapshot (3.3); ResultsUI.cpp:592, :609, :664-666 GetPodiumCharacter / HasSnapshot /
// GetSnapshot (M4); WorldLifecycle.cpp:130 AbandonWorldState in PrepareForManualSave (task 2.5's save path, src/Game/WorldLifecycle.cs).
// WorldLifecycle.cpp:264 (the world-teardown AbandonWorldState) is not needed: this object belongs to one world's system, a new world builds
// a new one (as task 2.5 dropped the other modules' teardown resets).

/// <summary>What SparStats reads of the running match (C++: the SparSession calls of SparStats.cpp). SparSession (task 3.3) implements it and is
/// passed to each SparStats call that reads the match (OnHit, OnBlock, OnDodge, Freeze); SparStats keeps no reference to it, so the two are
/// built independently (no construction cycle).</summary>
interface ISparSessionRoster
{
    int ParticipantCount(World world);
    CharacterId? Participant(World world, int index);
    MatchRules.MatchTeam Team(World world, CharacterId fighter);
    /// <summary>Serial steps only (it reads the world and binds the session to it): the commit step (where CombatSystem.ApplyBlow and the hit
    /// observers run, World.cs:710-727) is one; never the parallel Think / Move / Act (task 3.2b's off-thread readers use the published
    /// CombatMatchView instead).</summary>
    bool IsSparringOpponent(World world, CharacterId attacker, CharacterId defender);
}

/// <summary>
/// The glue of the original SparStats.cpp: the per-match counters (damage dealt / taken / mitigated, hits, blocks, misses, dodges), the order
/// fighters went down, and the snapshot frozen at the end of a match for the results podium, the ratings and the log. One object per world,
/// owned by ProvingGroundsSystem. Public surface = SparStats.h.
/// </summary>
sealed class SparStats
{
    readonly SparStatsState<CharacterId> state = new();

    /// <summary>The roster of ONE call: the caller's session and the world of the current serial step. A struct built per call and passed to
    /// the Core generically (no boxing, no allocation), so nothing outlives the call and there is no shared field to clear or misuse. Its
    /// members never throw (a stale id reads as "" / no team through the session and PgEngine).</summary>
    readonly struct RosterView(World world, ISparSessionRoster session) : ISparRoster<CharacterId>
    {
        public int ParticipantCount => session.ParticipantCount(world);
        public CharacterId? Participant(int index) => session.Participant(world, index);
        public MatchRules.MatchTeam Team(CharacterId fighter) => session.Team(world, fighter);
        public bool IsSparringOpponent(CharacterId attacker, CharacterId defender) => session.IsSparringOpponent(world, attacker, defender);
        public string? Name(CharacterId fighter) => PgEngine.Name(world, fighter);
    }

    /// <summary>The fighter table of one call (GetPodiumCharacter's stale check).</summary>
    readonly struct FighterTable(World world) : ISparFighters<CharacterId>
    {
        public bool Exists(CharacterId fighter) => PgEngine.Exists(world, fighter);
    }

    /// <summary>C++: SparStats.h:24-25 (WorldLifecycle.cpp:130: a manual save clears the counters and the snapshot).</summary>
    internal void AbandonWorldState(World world) => state.AbandonWorldState();

    /// <summary>C++: SparStats.h:9 (SparSession.cpp:661, a match start). Reads no roster (the C++ calls nothing of SparSession here).</summary>
    internal void ResetForMatch() => state.ResetForMatch();

    /// <summary>C++: SparStats.h:10-15. A miss is the attacker's side of a dodge only (CombatHooks.cpp:233-240 passes HIT_MISSED).</summary>
    internal void OnHit(World world, ISparSessionRoster session, CharacterId? attacker, CharacterId? defender, float appliedDamage,
        float incomingDamage, bool isMiss)
        => state.OnHit(new RosterView(world, session), attacker, defender, appliedDamage, incomingDamage, isMiss);

    /// <summary>C++: SparStats.h:16.</summary>
    internal void OnBlock(World world, ISparSessionRoster session, CharacterId? defender, CharacterId? attacker)
        => state.OnBlock(new RosterView(world, session), defender, attacker);

    /// <summary>C++: SparStats.h:17.</summary>
    internal void OnDodge(World world, ISparSessionRoster session, CharacterId? attacker, CharacterId? defender)
        => state.OnDodge(new RosterView(world, session), attacker, defender);

    /// <summary>Port only (task 3.2b): CombatHooks' counter fallback (no ApplyBlow patch): a participant's blocks, misses and dodges with no
    /// paired opponent (SparStatsState.AddUnpairedCounts).</summary>
    internal void AddUnpairedCounts(World world, ISparSessionRoster session, CharacterId fighter, int blocks, int misses, int dodges)
        => state.AddUnpairedCounts(new RosterView(world, session), fighter, blocks, misses, dodges);

    /// <summary>C++: SparStats.h:18 (KOWatcher.cpp:131). Reads no roster.</summary>
    internal void OnEliminated(CharacterId? who) => state.OnEliminated(who);

    /// <summary>C++: SparStats.h:19 (SparSession.cpp:710).</summary>
    internal void Freeze(World world, ISparSessionRoster session, SparPodium.OutcomeKind outcome, MatchRules.MatchMode mode,
        CharacterId? lastStandingOrNull)
        => state.Freeze(new RosterView(world, session), outcome, mode, lastStandingOrNull);

    /// <summary>C++: SparStats.h:20 (a const reference in the C++). The SAME object as GetMutableSnapshot: readers must not write to it; its rows
    /// are cleared in place by the next Freeze / ResetForMatch / ClearSnapshot (copy what you keep).</summary>
    internal SparPodium.Snapshot GetSnapshot() => state.GetSnapshot();

    /// <summary>C++: SparStats.h:21 (LeaderboardStore writes the rating and Marks fields). Same object as GetSnapshot.</summary>
    internal SparPodium.Snapshot GetMutableSnapshot() => state.GetMutableSnapshot();

    /// <summary>C++: SparStats.h:22.</summary>
    internal bool HasSnapshot() => state.HasSnapshot();

    /// <summary>C++: SparStats.h:23.</summary>
    internal void ClearSnapshot() => state.ClearSnapshot();

    /// <summary>C++: SparStats.h:27-28: the character of podium slot 0 = winner / 1st, 1 = 2nd, 2 = 3rd captured at freeze time; null when there is
    /// none or it no longer resolves in this tick's table.</summary>
    internal CharacterId? GetPodiumCharacter(World world, int podiumIndex)
        => state.GetPodiumCharacter(new FighterTable(world), podiumIndex);

    /// <summary>The F10 phase row (no C++ counterpart; sim thread, in Publish).</summary>
    internal string Phase => state.HasSnapshot()
        ? string.Create(CultureInfo.InvariantCulture, $"result frozen ({state.GetSnapshot().FighterCount} fighters)")
        : state.LiveCount > 0 ? string.Create(CultureInfo.InvariantCulture, $"{state.LiveCount} fighters counted") : "idle";
}

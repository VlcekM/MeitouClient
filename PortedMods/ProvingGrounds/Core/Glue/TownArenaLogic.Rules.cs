namespace ProvingGrounds.Core.Glue;

// Task 7.4 (town rules for fights, M7-town-core.md; CAP 2 option 2, 20; PA §2.8 "the four crime/relation rows"; TW §2.2): the C++ predicates that
// say which pairs of characters a town bout sanctions (TownArena.cpp:1415-1438 IsIncidentPair / IsActiveCollateralIncidentPair), over the logic's
// own state for the sim thread and, through TownIncidentPairs, over the copy the combat hooks publish for the patches (CombatMatchView: one copy of
// the C++ loop, read by both); and the port-only bookkeeping that clears, after a town bout, the TEMP_ENEMY tags a fight between the player's squad and
// the town wrote (TownBoutForgiveness, M7-town-core.md task 7.4 "after the bout"). The patches that use them are in src/Game/CombatHooks.cs (one patch
// class for player matches and town bouts, controller decision 3); the engine half of the forgiveness is src/Town/TownCombatRules.cs.
//
// Not ported: TownArena::IsIncidentFactionPair (TownArena.cpp:1439-1456), which CombatHooks.cpp:132-141 reads only to drop an ASYNCHRONOUS combat
// outcome event (DEFEATED_ONE_OF_US .. EXECUTED_ONE_OF_US raised outside the blow's own call). Meitou raises a faction's combat events only inside
// the blow (FactionDeeds.Blow, Relations.cs:150-163, from LandBlow CombatSystem.Apply.cs:327 and the bolt hit CombatSystem.Ranged.cs:825; no other
// caller of PlayerRelations.Event raises a combat event, Relations.cs:138), so the per-blow skip covers what the C++ needed it for
// (docs/divergences.md "Town rules for fights").

/// <summary>The C++ loops of TownArena.cpp:1415-1438 over a fighter list (ids compared with <see cref="EqualityComparer{T}.Default"/>, as the logic): the logic passes its state (<c>g_fighters</c>, <c>g_fighterTeams</c>,
/// <c>g_fighterCount</c>), the combat hooks' published view passes its copy. Callers test the C++ NULL first (the ids here are never "none").</summary>
public static class TownIncidentPairs
{
    /// <summary>C++: TownArena.cpp:1417-1426, after the NULL test: the side of each (Team A = 0, any other team = 1; -1 = not a fighter; the last
    /// match wins, as the C++ loop) and <see cref="TownArenaRuntimePolicy.RetainedIncidentPair"/>.</summary>
    public static bool IsIncidentPair<TId>(bool retained, ReadOnlySpan<TId> fighters, ReadOnlySpan<MatchRules.MatchTeam> teams, TId a, TId b)
    {
        var eq = EqualityComparer<TId>.Default;
        int sideA = -1, sideB = -1;
        for (int i = 0; i < fighters.Length; ++i)
        {
            int side = teams[i] == MatchRules.MatchTeam.TeamA ? 0 : 1;
            if (eq.Equals(fighters[i], a)) sideA = side;
            if (eq.Equals(fighters[i], b)) sideB = side;
        }
        return TownArenaRuntimePolicy.RetainedIncidentPair(retained, sideA, sideB);
    }

    /// <summary>C++: TownArena.cpp:1430-1437, after the NULL test: <paramref name="active"/> = the C++ <c>g_phase == Fighting &amp;&amp; OwnsMatch()</c>;
    /// exactly one of the two is a fighter of the bout.</summary>
    public static bool IsCollateralPair<TId>(bool active, ReadOnlySpan<TId> fighters, TId a, TId b)
    {
        if (!active) return false;
        var eq = EqualityComparer<TId>.Default;
        bool fighterA = false, fighterB = false;
        for (int i = 0; i < fighters.Length; ++i)
        {
            if (eq.Equals(fighters[i], a)) fighterA = true;
            if (eq.Equals(fighters[i], b)) fighterB = true;
        }
        return fighterA != fighterB;
    }
}

public sealed partial class TownArenaLogic<TId>
{
    /// <summary>The <c>retained</c> argument of C++ TownArena.cpp:1426: Fighting, Aftercare, or Preparing while the town owns the running match.</summary>
    bool IncidentRetained(ITownHost<TId> h) =>
        s.Phase == TownPhase.Fighting || s.Phase == TownPhase.Aftercare || (s.Phase == TownPhase.Preparing && OwnsMatch(h));

    /// <summary>The <c>g_phase == Fighting &amp;&amp; OwnsMatch()</c> of C++ TownArena.cpp:1430.</summary>
    bool CollateralActive(ITownHost<TId> h) => s.Phase == TownPhase.Fighting && OwnsMatch(h);

    // C++: TownArena.cpp:1415-1427
    /// <summary><c>IsIncidentPair</c>: the two are fighters of the town bout on opposite sides (Team A against any other team) while the bout
    /// fights, is in aftercare, or is being prepared for a match the town owns. The fighter list is read whatever the phase, as the C++.</summary>
    public bool IsIncidentPair(ITownHost<TId> h, TId a, TId b)
    {
        if (h.IsNull(a) || h.IsNull(b)) return false;
        return TownIncidentPairs.IsIncidentPair(IncidentRetained(h), s.Fighters.AsSpan(0, s.FighterCount), s.FighterTeams.AsSpan(0, s.FighterCount), a, b);
    }

    // C++: TownArena.cpp:1428-1438
    /// <summary><c>IsActiveCollateralIncidentPair</c>: while the town's owned match fights, exactly one of the two is a fighter of the bout.</summary>
    public bool IsActiveCollateralIncidentPair(ITownHost<TId> h, TId a, TId b)
    {
        if (h.IsNull(a) || h.IsNull(b) || !CollateralActive(h)) return false;
        return TownIncidentPairs.IsCollateralPair(true, s.Fighters.AsSpan(0, s.FighterCount), a, b);
    }

    /// <summary>The C++ <c>SparSession::AbortForSave()</c> of an owned match (TownArena.cpp:1794 CancelForSave, :1938-1941 the stop rule). The
    /// town rules' clearing (task 7.4) runs in the Finish that follows every such abort (<see cref="ITownHost{TId}.TownRulesEventEnded"/>,
    /// task 8.4b).</summary>
    void AbortOwnedMatch(ITownHost<TId> h)
    {
        h.SparAbortForSave();
    }

    /// <summary>What the combat hooks publish for the two predicates above (task 7.4; their view runs the same TownIncidentPairs loops): the
    /// fighters and teams (appended to the lists, aligned; empty when neither predicate can answer true), <paramref name="retained"/> and
    /// <paramref name="collateral"/> as the C++ computes them now.</summary>
    public void CopyIncidentState(ITownHost<TId> h, List<TId> fighters, List<MatchRules.MatchTeam> teams, out bool retained, out bool collateral)
    {
        retained = IncidentRetained(h);
        collateral = CollateralActive(h);
        if (!retained && !collateral) return;
        for (int i = 0; i < s.FighterCount; ++i)
        {
            fighters.Add(s.Fighters[i]);
            teams.Add(s.FighterTeams[i]);
        }
    }
}

/// <summary>The squad-memory reads and writes <see cref="TownBoutForgiveness{TId}"/> needs (the glue: Meitou's DialogueSystem.Memory).</summary>
public interface ITempEnemyHost<TId>
{
    /// <summary>One of the player's characters.</summary>
    bool IsPlayer(TId c);
    /// <summary>Appends the squads whose TEMP_ENEMY on someone <paramref name="observer"/>'s side writes: its own squad (if any), then every other
    /// squad of its faction (Meitou's DA_MASSIVE_ALARM tags in all of them, DialogueSystem.Acts.cs:317-338).</summary>
    void SquadsOf(TId observer, List<int> into);
    /// <summary>Squad <paramref name="squad"/> holds the short-term TEMP_ENEMY tag on <paramref name="target"/>.</summary>
    bool HoldsTempEnemy(int squad, TId target);
    /// <summary>Clears that one tag (Meitou's <c>SquadMemory.ClearShort(squad, target, MemoryTag.TempEnemy)</c>).</summary>
    void ClearTempEnemy(int squad, TId target);
}

/// <summary>
/// Port only (M7-town-core.md task 7.4, "after the bout"; CAP 2 option 2): the C++ kept a fight between the player's squad and the town from turning
/// the two into enemies at all (setEnemy_hook / relationEvent_hook in the blow's incident context, CombatHooks.cpp:124-148). Meitou has no faction
/// "enemy" switch in a blow; its counterpart is the squad memory's short-term TEMP_ENEMY tag (the alert writes it in the attacked character's squad
/// on its attacker, RetaliationSystem.Alert Fighting.cs:1021-1028 through DialogueSystem.TagEnemy / FlagFrom, Acts.cs:22-40; a massive alarm in every
/// squad of the speaker's faction, Acts.cs:317-338), lasting up to 6 game hours and making every mate take the other for an enemy whatever the
/// relations say. At the start of a town match this records, for every player character of the bout and every non-player fighter on the other side,
/// each squad of the other one's side (its own squad and every other squad of its faction) with whether that squad already held TEMP_ENEMY on the
/// character; at the end it clears exactly the TEMP_ENEMY tags the bout wrote (held now, not before), and nothing else: no other tag (criminal,
/// aggressor) and no tag held before the match (controller decision, task 7.4 spec review: not DialogueSystem.Forgive, which clears those too).
/// An NPC-only bout records nothing: a faction never tags itself TEMP_ENEMY (SquadMemory.Flag, SquadMemory.cs:139-141).
/// </summary>
public sealed class TownBoutForgiveness<TId>
{
    readonly record struct Entry(int Squad, TId Target, bool HeldBefore);

    readonly List<Entry> entries = [];
    readonly HashSet<(int, TId)> recordedKeys = [];
    readonly List<int> squads = [];

    /// <summary>The (squad, target) entries recorded at the start, for the tests and the log.</summary>
    public int Count => entries.Count;

    /// <summary>A record was taken at the start of a town match and not yet used or dropped (an NPC-only bout's empty record counts).</summary>
    public bool Recorded { get; private set; }

    /// <summary><see cref="Begin"/> that never throws into the match start (SparSession's StartMatch undoes the whole start on a throw): a host read
    /// that throws leaves no record (the after-bout clearing is lost for that match) and returns the exception; null when recorded.</summary>
    public Exception? TryBegin<THost>(THost host, ReadOnlySpan<TId> fighters, ReadOnlySpan<MatchRules.MatchTeam> teams) where THost : ITempEnemyHost<TId>
    {
        try
        {
            Begin(host, fighters, teams);
            return null;
        }
        catch (Exception e)
        {
            Clear();
            return e;
        }
    }

    /// <summary>The match started: records every (squad, target) entry, once each, between a player character and a non-player fighter on the
    /// other side, both ways, with whether the squad already held TEMP_ENEMY on the target. Anything left from an earlier match is dropped.</summary>
    public void Begin<THost>(THost host, ReadOnlySpan<TId> fighters, ReadOnlySpan<MatchRules.MatchTeam> teams) where THost : ITempEnemyHost<TId>
    {
        Clear();
        for (int i = 0; i < fighters.Length; i++)
        {
            if (!host.IsPlayer(fighters[i])) continue;
            for (int j = 0; j < fighters.Length; j++)
            {
                if (teams[j] == teams[i] || host.IsPlayer(fighters[j])) continue;
                Record(host, fighters[j], fighters[i]);   // the town side's squads on the player character
                Record(host, fighters[i], fighters[j]);   // the player side's squads on the town fighter
            }
        }
        Recorded = true;
    }

    void Record<THost>(THost host, TId observer, TId target) where THost : ITempEnemyHost<TId>
    {
        squads.Clear();
        host.SquadsOf(observer, squads);
        foreach (int squad in squads)
            if (recordedKeys.Add((squad, target))) entries.Add(new Entry(squad, target, host.HoldsTempEnemy(squad, target)));
    }

    /// <summary>A match ended: when it is the town's owned match (<paramref name="ownedMatchEnding"/>, the glue's OwnsMatch as it ends) and a
    /// record was taken, clears TEMP_ENEMY on each recorded entry whose squad holds it now and did not at the start, in the order recorded, then
    /// forgets the record. Any other match's end (a player-arena match) leaves the record alone. Returns how many tags were cleared.</summary>
    public int End<THost>(THost host, bool ownedMatchEnding) where THost : ITempEnemyHost<TId>
    {
        // One copy of the loop (task 7.3 review tidy): TryEnd's, with its exception rethrown (the record is dropped either way).
        if (TryEnd(host, ownedMatchEnding, out int cleared) is { } error)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return cleared;
    }

    /// <summary><see cref="End"/> that never throws into its caller (the town's abort must still abort the match and finish; a match end must still
    /// end): a host read or clear that throws drops the whole record, so a half-used record is never reused, and returns the exception (null when
    /// it ran); <paramref name="cleared"/> = the tags cleared before the throw, or all of them.</summary>
    public Exception? TryEnd<THost>(THost host, bool ownedMatchEnding, out int cleared) where THost : ITempEnemyHost<TId>
    {
        cleared = 0;
        if (!ownedMatchEnding || !Recorded) return null;
        int done = 0;
        try
        {
            foreach (var e in entries)
            {
                if (e.HeldBefore || !host.HoldsTempEnemy(e.Squad, e.Target)) continue;
                host.ClearTempEnemy(e.Squad, e.Target);
                done++;
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            cleared = done;
            Clear();
        }
    }

    /// <summary>Forgets the record without clearing anything (the world is gone: TownArena's AbandonWorldState; or a failed start).</summary>
    public void Clear()
    {
        entries.Clear();
        recordedKeys.Clear();
        Recorded = false;
    }
}

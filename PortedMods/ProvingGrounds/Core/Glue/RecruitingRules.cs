namespace ProvingGrounds.Core.Glue;

// Task 6.4a (review fix): the decisions of the recruit adapter (src/Engine/Recruiting.cs, Kenshi's PlayerInterface::recruit(prisoner, false),
// PrisonerRecruitment.cpp:50, and the prison mode it ends, :120) without the game, fake-tested in Tests/Glue/RecruitingRulesTests.cs. The binding
// makes one IRecruitingHost per call, bound to the world and the prisoner (and the cage it was found in). Every engine step that can throw is read
// back afterwards: a throw is never left as a half-done recruit (out of the cage but not a player, or a player still caged).

/// <summary>One recruit's engine reads and writes (src/Engine/Recruiting.cs binds it to a world and one prisoner).</summary>
public interface IRecruitingHost
{
    /// <summary>The part is on: Members.JoinPlayer / PlayerHeads resolved and bound.</summary>
    bool PartAvailable();
    /// <summary>The world's DialogueSystem (it owns JoinPlayer).</summary>
    bool HasDialogue();
    /// <summary>A living player character to stand as JoinPlayer's talker.</summary>
    bool HasTalker();
    /// <summary>The prisoner's id resolves (not stale).</summary>
    bool Resolves();
    /// <summary><c>DialogueSystem.PlayerHeads(world)</c>.</summary>
    int PlayerHeads();
    /// <summary>CONSTANTS <c>max faction size</c>.</summary>
    int MaxFactionSize();
    /// <summary>The prisoner is held in a cage now (<c>CharacterCold.IsImprisoned</c>).</summary>
    bool IsImprisoned();
    /// <summary>Remembers the cage it is held in (call before <see cref="Release"/>); false when none is registered.</summary>
    bool RememberCage();
    /// <summary><c>FixtureRegistry.Release(world, prisoner, -1)</c>.</summary>
    bool Release();
    /// <summary><c>DialogueSystem.JoinPlayer(world, slot, talker)</c>.</summary>
    bool JoinPlayer();
    /// <summary>The prisoner is a player character now (<c>CharacterCold.IsPlayer</c>): what a throw left behind.</summary>
    bool IsPlayer();
    /// <summary>Back in the remembered cage (Cages.PutInCage: Imprison locks it); false when refused.</summary>
    bool PutBack();
    /// <summary>One Meitou log line (an unusual outcome).</summary>
    void Log(string line);
}

/// <summary>What a recruit did. <see cref="Joined"/> and <see cref="JoinThrewJoined"/> are the two recruits that happened.</summary>
public enum RecruitOutcome
{
    Joined, PartOff, NoDialogue, NoTalker, StaleId, FactionFull, ReleaseRefused, ReleaseThrew, ReleaseThrewPutBackFailed,
    JoinRefused, JoinRefusedPutBackFailed, JoinThrewJoined, JoinThrew, JoinThrewPutBackFailed,
    /// <summary>The join threw and reading whether the character is a player character threw too: not known, so it is NOT put back in a cage
    /// (it may be a player character now); the glue reads it again in its recovery (IPrisonerRecruitmentHost.JoinedPlayer).</summary>
    JoinThrewUnknown,
}

/// <summary>The recruit's order: the faction cap (as Meitou's own recruits, DialogueSystem.Recruit.cs:30, :160), the prisoner let out of its cage
/// (GiveSlaves :157: JoinPlayer does not release), then JoinPlayer; a refusal or a throw that leaves a non-player puts it back in its cage.</summary>
public static class RecruitingRules
{
    /// <summary>True for the outcomes in which the prisoner joined the player's squad.</summary>
    public static bool IsJoined(RecruitOutcome o) => o is RecruitOutcome.Joined or RecruitOutcome.JoinThrewJoined;

    public static RecruitOutcome Recruit(IRecruitingHost h)
    {
        if (!h.PartAvailable()) return RecruitOutcome.PartOff;
        if (!h.HasDialogue()) return RecruitOutcome.NoDialogue;
        if (!h.HasTalker()) return RecruitOutcome.NoTalker;
        if (!h.Resolves()) return RecruitOutcome.StaleId;
        if (h.PlayerHeads() >= h.MaxFactionSize()) return RecruitOutcome.FactionFull;
        bool cage = false, released = false;   // released: it was let out of a cage, so a failure must put it back
        if (h.IsImprisoned())
        {
            cage = h.RememberCage();
            try { released = h.Release(); }
            catch (Exception e)
            {
                if (Read(h, h.IsImprisoned, true)) return Note(h, RecruitOutcome.ReleaseThrew, e);   // still caged: nothing changed
                return Note(h, cage && Put(h) ? RecruitOutcome.ReleaseThrew : RecruitOutcome.ReleaseThrewPutBackFailed, e);
            }
            if (!released) return RecruitOutcome.ReleaseRefused;
        }
        bool joined;
        try { joined = h.JoinPlayer(); }
        catch (Exception e)
        {
            bool? player;
            try { player = h.IsPlayer(); }
            catch (Exception) { player = null; }
            if (player is null) return Note(h, RecruitOutcome.JoinThrewUnknown, e);
            if (player.Value) return Note(h, RecruitOutcome.JoinThrewJoined, e);
            return Note(h, !released || cage && Put(h) ? RecruitOutcome.JoinThrew : RecruitOutcome.JoinThrewPutBackFailed, e);
        }
        if (joined) return RecruitOutcome.Joined;
        return !released || cage && Put(h) ? RecruitOutcome.JoinRefused : Note(h, RecruitOutcome.JoinRefusedPutBackFailed, null);
    }

    static bool Put(IRecruitingHost h)
    {
        try { return h.PutBack(); }
        catch (Exception) { return false; }
    }

    static bool Read(IRecruitingHost h, Func<bool> read, bool fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }

    static RecruitOutcome Note(IRecruitingHost h, RecruitOutcome o, Exception? e)
    {
        string what = o switch
        {
            RecruitOutcome.ReleaseThrew => "the release threw; the prisoner is caged",
            RecruitOutcome.ReleaseThrewPutBackFailed => "the release threw and the prisoner could not be put back in its cage",
            RecruitOutcome.JoinThrewJoined => "the join threw after the prisoner became a player character: read as joined",
            RecruitOutcome.JoinThrewUnknown => "the join threw and whether the prisoner joined could not be read; it is not put back in a cage",
            RecruitOutcome.JoinThrew => "the join threw; the prisoner is not a player character (back in its cage, if it was caged)",
            RecruitOutcome.JoinThrewPutBackFailed => "the join threw and the prisoner could not be put back in its cage",
            RecruitOutcome.JoinRefusedPutBackFailed => "the join refused and the prisoner could not be put back in its cage",
            _ => o.ToString(),
        };
        try { h.Log("prisoner recruitment: " + what + (e is null ? "" : $" ({e.GetType().Name}: {e.Message})")); }
        catch (Exception) { }
        return o;
    }
}

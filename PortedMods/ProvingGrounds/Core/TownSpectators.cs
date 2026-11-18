// Pure parts of upstream/ProvingGrounds/plugin/src/TownSpectators.cpp (Proving Grounds by Shutori, GPLv3).
using System.Globalization;

namespace ProvingGrounds.Core;

/// <summary>The crowd chatter of <c>TownSpectators.cpp</c> (TW §2.7): its random generator, the lines, the winners, and when and who
/// speaks. The rest of the file (seating, inviting, releasing guests, the Tick loop) reads and orders characters and stays in the glue.
/// <para>Plain inputs: a seated guest is its index in the glue's <c>seated</c> list (built in guest order, TownSpectators.cpp:255-259 /
/// :313-317); <c>lastChatterSpeaker</c> is that index, or -1 when the last speaker is not in the list; a fighter is its name.
/// <c>chatterSeed</c> is the module global (start 0), owned by the glue and passed by ref; <c>GetTickCount()</c> is <c>tickMs</c>.
/// Policy values are <see cref="ArenaPerformance.Policy"/>.</para></summary>
public static class TownSpectators
{
    /// <summary>The C++'s own xorshift: like <see cref="Hashing.Xorshift32"/> but its zero fix is <c>GetTickCount() ^ 0x9e3779b9u</c>
    /// (read only when the seed is 0, i.e. on the first draw after a reset).</summary>
    // C++: TownSpectators.cpp:140-146
    public static uint ChatterRandom(ref uint chatterSeed, uint tickMs)
    {
        unchecked
        {
            if (chatterSeed == 0) chatterSeed = tickMs ^ 0x9e3779b9u;
            chatterSeed ^= chatterSeed << 13;
            chatterSeed ^= chatterSeed >> 17;
            chatterSeed ^= chatterSeed << 5;
            return chatterSeed;
        }
    }

    /// <summary>The seed TickChatter starts each match's chatter with: <c>GetTickCount() ^ (unsigned)(guests.size() * 2654435761u)</c>
    /// (the size_t product cut to its low 32 bits).</summary>
    // C++: TownSpectators.cpp:293-294
    public static uint ChatterStartSeed(uint tickMs, int guestCount)
    {
        unchecked { return tickMs ^ ((uint)guestCount * 2654435761u); }
    }

    static readonly string[] Generic =
    {
        "Come on!", "Hit back!", "What a hit!", "Keep fighting!",
        "Stay on them!", "Don't let up!", "Now that's a fight!",
        "This is getting good!", "Get up!", "Finish it!"
    };
    static readonly int[] Stakes = { 100, 500, 1000 };

    /// <summary>A line during the fight. <paramref name="fighterNames"/> = <c>ChatterFighters()</c>'s fighters' names, in its order
    /// (TownSpectators.cpp:161-177: in a 1v1 the two active fighters that are valid, alive and conscious; otherwise every such
    /// participant not eliminated).</summary>
    // C++: TownSpectators.cpp:178-207
    public static string ChatterLine(IReadOnlyList<string> fighterNames, ref uint chatterSeed, uint tickMs)
    {
        uint type = ChatterRandom(ref chatterSeed, tickMs) % 10u;
        if (fighterNames.Count == 0 || type < 4u)
            return Generic[ChatterRandom(ref chatterSeed, tickMs) % (uint)Generic.Length];
        string name = fighterNames[(int)(ChatterRandom(ref chatterSeed, tickMs) % (uint)fighterNames.Count)];
        if (type < 7u)
        {
            switch (ChatterRandom(ref chatterSeed, tickMs) % 5u)
            {
                case 0: return "Come on, " + name + "!";
                case 1: return name + " has this!";
                case 2: return "Finish it, " + name + "!";
                case 3: return "Stay on them, " + name + "!";
                default: return "Get back in it, " + name + "!";
            }
        }
        string amount = Stakes[ChatterRandom(ref chatterSeed, tickMs) % 3u].ToString(CultureInfo.InvariantCulture);
        switch (ChatterRandom(ref chatterSeed, tickMs) % 4u)
        {
            case 0: return "I put " + amount + " Cats on " + name + "!";
            case 1: return amount + " Cats says " + name + " takes it!";
            case 2: return "My Cats are on " + name + "!";
            default: return "Don't fail me, " + name + " - " + amount + " Cats!";
        }
    }

    // C++: TownSpectators.cpp:208-220
    public static List<string> ResultWinners(SparPodium.Snapshot result)
    {
        var winners = new List<string>();
        for (int i = 0; i < result.FighterCount; ++i)
        {
            SparPodium.FighterRow fighter = result.Fighters[i];
            bool winningTeam =
                (result.Outcome == SparPodium.OutcomeKind.TeamAWins && fighter.Team == MatchRules.MatchTeam.TeamA) ||
                (result.Outcome == SparPodium.OutcomeKind.TeamBWins && fighter.Team == MatchRules.MatchTeam.TeamB);
            bool lastStanding = result.Outcome == SparPodium.OutcomeKind.LastStanding &&
                fighter.Id == result.LastStandingId;
            if ((winningTeam || lastStanding) && !string.IsNullOrEmpty(fighter.Name)) winners.Add(fighter.Name);
        }
        return winners;
    }

    static readonly string[] Enjoyment =
    {
        "What a fight!", "Now that was worth watching!", "Give us another one!",
        "What a finish!", "Best fight all day!", "I'd pay to see that again!"
    };
    static readonly string[] DrawLines =
    {
        "Run it back!", "Again! We need a winner!", "Nobody won my Cats!",
        "You can't end it there!"
    };

    // C++: TownSpectators.cpp:221-253
    public static string ResultChatterLine(SparPodium.Snapshot result, IReadOnlyList<string> winners, ref uint chatterSeed, uint tickMs)
    {
        if (result.Outcome == SparPodium.OutcomeKind.Draw || winners.Count == 0)
            return DrawLines[ChatterRandom(ref chatterSeed, tickMs) % (uint)DrawLines.Length];
        string winner = winners[(int)(ChatterRandom(ref chatterSeed, tickMs) % (uint)winners.Count)];
        uint type = ChatterRandom(ref chatterSeed, tickMs) % 10u;
        if (type < 3u)
            return Enjoyment[ChatterRandom(ref chatterSeed, tickMs) % (uint)Enjoyment.Length];
        if (type < 6u)
        {
            switch (ChatterRandom(ref chatterSeed, tickMs) % 4u)
            {
                case 0: return winner + " earned that one!";
                case 1: return "What a finish from " + winner + "!";
                case 2: return "That's how it's done, " + winner + "!";
                default: return "I knew " + winner + " would take it!";
            }
        }
        string amount = Stakes[ChatterRandom(ref chatterSeed, tickMs) % 3u].ToString(CultureInfo.InvariantCulture);
        switch (ChatterRandom(ref chatterSeed, tickMs) % 4u)
        {
            case 0: return winner + " just won me " + amount + " Cats!";
            case 1: return "There go my " + amount + " Cats...";
            case 2: return "Should've backed " + winner + "!";
            default: return "Never betting against " + winner + " again!";
        }
    }

    // How many seated guests speak (TownSpectators.cpp:267-272 / :319-324): one per 25 seated, capped by the policy's voices (0 when
    // it is not positive) and by the seated count.
    static int VoiceCount(int seatedCount, int maxChatterVoices)
    {
        uint seated = (uint)seatedCount;
        uint voices = (seated + 24u) / 25u;
        uint voiceLimit = maxChatterVoices > 0 ? (uint)maxChatterVoices : 0u;
        if (voices > voiceLimit) voices = voiceLimit;
        if (voices > seated) voices = seated;
        return (int)voices;
    }

    // The first speaker and the spacing between speakers (TownSpectators.cpp:273-276 / :326-329); draws one ChatterRandom.
    static void FirstAndStep(int seatedCount, int voices, int lastSpeakerIndex, ref uint chatterSeed, uint tickMs,
        out uint start, out uint step)
    {
        uint seated = (uint)seatedCount;
        start = ChatterRandom(ref chatterSeed, tickMs) % seated;
        if (seated > 1u && lastSpeakerIndex == (int)start)
            start = (start + 1u) % seated;
        step = seated / (uint)voices > 0u ? seated / (uint)voices : 1u;
    }

    /// <summary>One chatter cue: the seated index that speaks and when (game-elapsed seconds).</summary>
    public struct Cue
    {
        public int SeatedIndex;
        public float Due;
    }

    /// <summary>What <c>ScheduleChatterCycle</c> decides: the new <c>nextChatterCycle</c> and the cues it appends to <c>chatterCues</c>.</summary>
    public sealed class Cycle
    {
        public float Duration;
        public float NextChatterCycle;
        public List<Cue> Cues = new List<Cue>();
    }

    /// <summary>The next chatter cycle at <paramref name="elapsed"/> seconds: a duration in [min, max] whole milliseconds, then up to the
    /// voice count of speakers, evenly spread over it. The glue sets <c>nextChatterCycle</c> and appends the cues.</summary>
    // C++: TownSpectators.cpp:254-284
    public static Cycle ScheduleChatterCycle(in ArenaPerformance.Policy policy, int seatedCount, int lastSpeakerIndex, float elapsed,
        ref uint chatterSeed, uint tickMs)
    {
        var cycle = new Cycle();
        // static_cast<unsigned>(float): the shipped profiles are positive (a negative value is undefined in C++)
        uint minMs = (uint)(policy.ChatterMinSeconds * 1000.0f);
        uint maxMs = (uint)(policy.ChatterMaxSeconds * 1000.0f);
        uint rangeMs = maxMs >= minMs ? maxMs - minMs : 0u;
        float duration = (float)(unchecked(minMs +
            (rangeMs != 0 ? ChatterRandom(ref chatterSeed, tickMs) % (rangeMs + 1u) : 0u))) / 1000.0f;
        cycle.Duration = duration;
        cycle.NextChatterCycle = elapsed + duration;
        if (seatedCount == 0) return cycle;
        int voices = VoiceCount(seatedCount, policy.MaxChatterVoices);
        if (voices == 0) return cycle;
        FirstAndStep(seatedCount, voices, lastSpeakerIndex, ref chatterSeed, tickMs, out uint start, out uint step);
        float spacing = duration / (float)voices;
        for (uint i = 0; i < (uint)voices; ++i)
        {
            Cue cue;
            cue.SeatedIndex = (int)(unchecked(start + i * step) % (uint)seatedCount);
            cue.Due = elapsed + ((float)i + 0.5f) * spacing;
            cycle.Cues.Add(cue);
        }
        return cycle;
    }

    /// <summary>One result line: the seated index that says it and the line.</summary>
    public struct Saying
    {
        public int SeatedIndex;
        public string Line;
    }

    /// <summary>The crowd's reaction to a finished match. Each speaker says one line; the glue makes each say it and then sets
    /// <c>lastChatterSpeaker</c> to the last one (the C++ sets it after every line). <paramref name="guestCount"/> = <c>guests.size()</c>,
    /// <paramref name="seatedCount"/> = the seated list's size: two ints in a row, so call it with named arguments.</summary>
    // C++: TownSpectators.cpp:311-335
    public static List<Saying> ReactToResult(in ArenaPerformance.Policy policy, SparPodium.Snapshot result, int guestCount,
        int seatedCount, int lastSpeakerIndex, ref uint chatterSeed, uint tickMs)
    {
        var said = new List<Saying>();
        if (!policy.ChatterEnabled || result.Outcome == SparPodium.OutcomeKind.Stopped || guestCount == 0) return said;
        if (seatedCount == 0) return said;
        int voices = VoiceCount(seatedCount, policy.MaxChatterVoices);
        if (voices == 0) return said;
        List<string> winners = ResultWinners(result);
        FirstAndStep(seatedCount, voices, lastSpeakerIndex, ref chatterSeed, tickMs, out uint start, out uint step);
        for (uint i = 0; i < (uint)voices; ++i)
        {
            Saying s;
            s.SeatedIndex = (int)(unchecked(start + i * step) % (uint)seatedCount);
            s.Line = ResultChatterLine(result, winners, ref chatterSeed, tickMs);
            said.Add(s);
        }
        return said;
    }
}

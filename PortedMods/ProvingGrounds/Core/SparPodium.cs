namespace ProvingGrounds.Core;

/// <summary>Pure ranking (no Character): MVP score and the results podium. C++: SparPodium.h, SparPodium.cpp (PA §2.12).
/// <para>Name and header: the C++ holds <c>char name[64]</c> and <c>char header[96]</c>. Here they are strings. The C++ fills a name
/// with <c>strncpy(row.name, name.c_str(), 63)</c> (SparStats.cpp:180): 63 BYTES of the engine's narrow string, not 63 chars. No copy
/// site into a name exists in this file (rows are copied whole); the filler (SparStats glue) must cut to <see cref="NameMaxChars"/>
/// UTF-8 bytes without splitting a character (see docs/divergences.md). Headers go through <c>SetHeader</c>, which truncates to 95
/// chars (the header texts are ASCII, so chars = bytes).</para></summary>
public static class SparPodium
{
    /// <summary><c>char name[64]</c> holds 63 bytes plus the terminator.</summary>
    public const int NameMaxChars = 63;
    /// <summary><c>char header[96]</c> holds 95 chars plus the terminator.</summary>
    public const int HeaderMaxChars = 95;

    public enum OutcomeKind { TeamAWins = 0, TeamBWins = 1, LastStanding = 2, Stopped = 3, Draw = 4 }

    /// <summary>C++: SparPodium.h:16-37 (copied by value).</summary>
    public struct FighterRow
    {
        public int Id;
        public MatchRules.MatchTeam Team;
        public string? Name;             // char name[64]; null reads as "" (a zeroed C array)
        public float DamageDealt;
        public float DamageTaken;
        public float DamageMitigated;
        public int HitsLanded;
        public int Blocks;
        public int Misses;
        public int Dodges;
        public int EliminationIndex;     // -1 survived; otherwise first-KO order
        public float RatingBefore;
        public float RatingAfter;
        public float RatingDelta;
        public bool RatingUpdated;
        public int MarksBefore;
        public int MarksAfter;
        public int MarksEarned;
        public bool MarksUpdated;
    }

    /// <summary>C++: SparPodium.h:39-43.</summary>
    public struct PodiumEntry
    {
        public int Place;
        public FighterRow Fighter;
    }

    /// <summary>C++: SparPodium.h:45-57 (passed by reference and mutated, so a class).</summary>
    // SparStats.cpp clears its global snapshot in place with memset (:92, :161, :252) rather than making a new one: Clear() does that, so
    // holders of the object (Core/Glue/SparStatsState.cs, task 3.2a) see it.
    public sealed class Snapshot
    {
        /// <summary><c>std::memset(&amp;snap, 0, sizeof(snap))</c> (SparStats.cpp:92, :161, :252): every field zero, the array contents
        /// cleared in place (the same array objects stay), a name or header reads "" (a zeroed C array).</summary>
        public void Clear()
        {
            Outcome = default;
            Mode = default;
            Header = "";
            System.Array.Clear(Fighters);
            FighterCount = 0;
            EliminationCount = 0;
            System.Array.Clear(EliminationIds);
            LastStandingId = 0;
            System.Array.Clear(Podium);
            PodiumCount = 0;
        }

        public OutcomeKind Outcome;
        public MatchRules.MatchMode Mode;
        public string Header = "";       // char header[96]
        public FighterRow[] Fighters = new FighterRow[16];
        public int FighterCount;
        public int EliminationCount;
        public int[] EliminationIds = new int[16];
        public int LastStandingId;
        public PodiumEntry[] Podium = new PodiumEntry[3];
        public int PodiumCount;
    }

    /// <summary>Returns the index of the first fighter with <paramref name="id"/>, or -1 (the C++ returns a pointer or 0).</summary>
    // C++: SparPodium.cpp:10-18
    static int FindById(Snapshot snap, int id)
    {
        for (int i = 0; i < snap.FighterCount; ++i)
        {
            if (snap.Fighters[i].Id == id)
                return i;
        }
        return -1;
    }

    // C++: SparPodium.cpp:20-37
    static bool BetterMvp(in FighterRow a, in FighterRow b)
    {
        float aScore = ComputeMvpScore(a);
        float bScore = ComputeMvpScore(b);
        if (aScore != bScore)
            return aScore > bScore;
        if (a.DamageDealt != b.DamageDealt)
            return a.DamageDealt > b.DamageDealt;
        float aDefense = a.DamageMitigated + (float)(a.Blocks + a.Dodges);
        float bDefense = b.DamageMitigated + (float)(b.Blocks + b.Dodges);
        if (aDefense != bDefense)
            return aDefense > bDefense;
        if (a.HitsLanded != b.HitsLanded)
            return a.HitsLanded > b.HitsLanded;
        // std::strcmp compares unsigned bytes; string.CompareOrdinal compares UTF-16 code units. The two agree for ASCII names
        // (and for any name without chars above U+007F); a non-ASCII name could order differently than the UTF-8 / ANSI bytes did.
        return string.CompareOrdinal(a.Name ?? "", b.Name ?? "") < 0;
    }

    // C++: SparPodium.cpp:39-53
    static void SortByMvp(FighterRow[] rows, int count)
    {
        for (int i = 0; i < count; ++i)
        {
            for (int j = i + 1; j < count; ++j)
            {
                if (BetterMvp(rows[j], rows[i]))
                {
                    FighterRow tmp = rows[i];
                    rows[i] = rows[j];
                    rows[j] = tmp;
                }
            }
        }
    }

    // C++: SparPodium.cpp:55-73
    static void FillMvpPodium(Snapshot snap, MatchRules.MatchTeam filterTeam, bool filter)
    {
        var pool = new FighterRow[16];
        int n = 0;
        for (int i = 0; i < snap.FighterCount && n < 16; ++i)
        {
            if (filter && snap.Fighters[i].Team != filterTeam)
                continue;
            pool[n++] = snap.Fighters[i];
        }
        SortByMvp(pool, n);
        snap.PodiumCount = 0;
        for (int i = 0; i < n && snap.PodiumCount < 3; ++i)
        {
            snap.Podium[snap.PodiumCount].Place = snap.PodiumCount + 1;
            snap.Podium[snap.PodiumCount].Fighter = pool[i];
            ++snap.PodiumCount;
        }
    }

    // C++: SparPodium.cpp:75-79 (strncpy(header, text, 95); header[95] = '\0')
    static void SetHeader(Snapshot snap, string text)
    {
        snap.Header = text.Length > HeaderMaxChars ? text.Substring(0, HeaderMaxChars) : text;
    }

    // C++: SparPodium.cpp:82-99
    public static float ComputeMvpScore(in FighterRow fighter)
    {
        int attempts = fighter.HitsLanded + fighter.Misses;
        float accuracy = attempts > 0
            ? (float)fighter.HitsLanded / (float)attempts
            : 0.0f;
        float survivalBonus = fighter.EliminationIndex < 0 ? 10.0f : 0.0f;

        return fighter.DamageDealt +
            (fighter.DamageMitigated * 0.25f) -
            (fighter.DamageTaken * 0.10f) +
            ((float)fighter.HitsLanded * 2.0f) +
            ((float)fighter.Blocks * 4.0f) +
            ((float)fighter.Dodges * 4.0f) -
            ((float)fighter.Misses * 1.5f) +
            (accuracy * 15.0f) +
            survivalBonus;
    }

    /// <summary>Sets the header and fills the podium (at most 3). Entries past <see cref="Snapshot.PodiumCount"/> keep whatever they
    /// held before, like the C++ array. An outcome outside the five named values leaves an empty header and no podium.</summary>
    // C++: SparPodium.cpp:101-157
    public static void BuildPodium(Snapshot snap)
    {
        snap.PodiumCount = 0;
        snap.Header = "";

        switch (snap.Outcome)
        {
            case OutcomeKind.Draw:
                SetHeader(snap, "Draw");
                return;

            case OutcomeKind.TeamAWins:
                SetHeader(snap, "Team A wins");
                FillMvpPodium(snap, MatchRules.MatchTeam.TeamA, true);
                return;

            case OutcomeKind.TeamBWins:
                SetHeader(snap, "Team B wins");
                FillMvpPodium(snap, MatchRules.MatchTeam.TeamB, true);
                return;

            case OutcomeKind.Stopped:
                SetHeader(snap, "Stopped");
                FillMvpPodium(snap, MatchRules.MatchTeam.None, false);
                return;

            case OutcomeKind.LastStanding:
            {
                int winner = FindById(snap, snap.LastStandingId);
                // std::sprintf(header, "%s last standing", name): no truncation in the C++; a name of at most 63 chars plus 15
                // always fits the 96-byte header.
                if (winner >= 0)
                    snap.Header = (snap.Fighters[winner].Name ?? "") + " last standing";
                else
                    SetHeader(snap, "Last standing");

                if (winner >= 0)
                {
                    snap.Podium[0].Place = 1;
                    snap.Podium[0].Fighter = snap.Fighters[winner];
                    snap.PodiumCount = 1;
                }

                for (int i = snap.EliminationCount - 1; i >= 0 && snap.PodiumCount < 3; --i)
                {
                    int id = snap.EliminationIds[i];
                    if (id == snap.LastStandingId)
                        continue;
                    int row = FindById(snap, id);
                    if (row < 0)
                        continue;
                    snap.Podium[snap.PodiumCount].Place = snap.PodiumCount + 1;
                    snap.Podium[snap.PodiumCount].Fighter = snap.Fighters[row];
                    ++snap.PodiumCount;
                }
                return;
            }
        }
    }
}

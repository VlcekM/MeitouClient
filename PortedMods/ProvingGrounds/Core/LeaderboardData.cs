// Port of upstream/ProvingGrounds/plugin/src/LeaderboardData.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>The leaderboard tables: two standings tables (Player, Town), one shared progression table and the faction-wide
/// unlocks (PA §2.15). Header-only in the C++ (LeaderboardData.h:1-185).
/// <para><see cref="Standing"/> and <see cref="Progression"/> are classes: <c>EnsureStanding</c> / <c>EnsureProgression</c> return a
/// reference into the table that the caller writes through. A row is created fresh on insert, as the C++ pushes a copy of a
/// local; nothing in this file copies a table.</para>
/// <para>Elsewhere the C++ copies <c>Collections</c> BY VALUE inside the save snapshot (<c>snapshot = value</c> and
/// <c>out = snapshot</c>, ArenaLedger.h:35/38; <c>Snapshot snapshot=loaded.snapshot</c>, ArenaNativePersistence.cpp:351), which
/// deep-copies every row and tier list. Ports of those lines must call <see cref="Collections.DeepClone"/> (or
/// <see cref="Standing.Clone"/> / <see cref="Progression.Clone"/>), never copy the reference.</para></summary>
public static class LeaderboardData
{
    /// <summary>C++: LeaderboardData.h:10-14</summary>
    public enum Kind
    {
        Player,
        Town
    }

    // C++: LeaderboardData.h:16-20
    public const int CurrentVersion = 9;
    public const string PlayerBuildingId = PgRecords.Leaderboard;
    public const string PlayerFunctionId = PgRecords.LeaderboardFunc;
    public const string TownBuildingId = PgRecords.TownLeaderboard;
    public const string TownFunctionId = PgRecords.TownLeaderboardFunc;

    /// <summary>C string equality (a NUL ends a string, as in the C++ loop); a null pointer never equals anything.</summary>
    // C++: LeaderboardData.h:22-32
    public static bool IdEquals(string? a, string? b)
    {
        if (a == null || b == null)
            return false;
        int i = 0, j = 0;
        while (At(a, i) != '\0' && At(b, j) != '\0' && At(a, i) == At(b, j))
        {
            ++i;
            ++j;
        }
        return At(a, i) == '\0' && At(b, j) == '\0';
    }

    // the char a C string would hold at index i (its terminator past the end)
    static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    // C++: LeaderboardData.h:34-40
    public static bool IsLeaderboardId(string? id)
    {
        return IdEquals(id, PlayerBuildingId) ||
            IdEquals(id, PlayerFunctionId) ||
            IdEquals(id, TownBuildingId) ||
            IdEquals(id, TownFunctionId);
    }

    // C++: LeaderboardData.h:42-46
    public static Kind KindForId(string? id)
    {
        return IdEquals(id, TownBuildingId) ||
            IdEquals(id, TownFunctionId) ? Kind.Town : Kind.Player;
    }

    /// <summary>C++: LeaderboardData.h:48-59. id is a mod-owned FighterIdentity token; name is display metadata only.</summary>
    public sealed class Standing
    {
        public string Id = "";
        public string Name = "";
        public float Mmr;
        public int Wins;
        public int Losses;
        public int Matches;

        // C++: LeaderboardData.h:58
        public Standing() { Mmr = 100.0f; Wins = 0; Losses = 0; Matches = 0; }

        /// <summary>The C++ copy constructor (memberwise; strings are immutable).</summary>
        public Standing Clone() => new Standing { Id = Id, Name = Name, Mmr = Mmr, Wins = Wins, Losses = Losses, Matches = Matches };
    }

    /// <summary>C++: LeaderboardData.h:61-70. Shared by both standings tables, with no pointer or name balance cache.</summary>
    public sealed class Progression
    {
        public string Id = "";
        public string Name = "";
        public int Marks;
        public List<RewardProgression.TierEntry> RewardTiers = new List<RewardProgression.TierEntry>();

        // C++: LeaderboardData.h:69
        public Progression() { Marks = 0; }

        /// <summary>The C++ copy constructor: a new tier list (TierEntry is a value type, so copying the list copies the entries).</summary>
        public Progression Clone() => new Progression
        {
            Id = Id, Name = Name, Marks = Marks, RewardTiers = new List<RewardProgression.TierEntry>(RewardTiers),
        };
    }

    /// <summary>C++: LeaderboardData.h:72-78</summary>
    public sealed class Collections
    {
        public List<Standing> PlayerStandings = new List<Standing>();
        public List<Standing> TownStandings = new List<Standing>();
        public List<Progression> Progression = new List<Progression>();
        public List<string> FactionUnlocks = new List<string>();

        /// <summary>The C++ copy construction of <c>Collections</c>: new lists, every row cloned. Rule: a C++ copy CONSTRUCTION (<c>Snapshot s = loaded.snapshot;</c>, a by-value argument or return) is <c>Clone()</c>; a C++ copy ASSIGNMENT into an object others may hold (<c>member = value</c>, <c>out = x</c>) is <c>AssignFrom</c>.</summary>
        public Collections DeepClone() => new Collections
        {
            PlayerStandings = PlayerStandings.Select(r => r.Clone()).ToList(),
            TownStandings = TownStandings.Select(r => r.Clone()).ToList(),
            Progression = Progression.Select(r => r.Clone()).ToList(),
            FactionUnlocks = new List<string>(FactionUnlocks),
        };

        /// <summary>The C++ copy assignment <c>*this = other</c> (e.g. <c>snapshot = value</c>, ArenaLedger.h:35): the four vectors keep
        /// their identity and receive cloned rows, so a reference held to this object or its lists stays valid. Rule: a C++ copy CONSTRUCTION (<c>Snapshot s = loaded.snapshot;</c>, a by-value argument or return) is <c>Clone()</c>; a C++ copy ASSIGNMENT into an object others may hold (<c>member = value</c>, <c>out = x</c>) is <c>AssignFrom</c>.</summary>
        public void AssignFrom(Collections other)
        {
            if (ReferenceEquals(this, other)) return;
            Copy(other.PlayerStandings, PlayerStandings, r => r.Clone());
            Copy(other.TownStandings, TownStandings, r => r.Clone());
            Copy(other.Progression, Progression, r => r.Clone());
            Copy(other.FactionUnlocks, FactionUnlocks, s => s);
        }

        static void Copy<T>(List<T> from, List<T> to, Func<T, T> clone)
        {
            if (ReferenceEquals(from, to)) return;
            to.Clear();
            for (int i = 0; i < from.Count; ++i) to.Add(clone(from[i]));
        }
    }

    // C++: LeaderboardData.h:80-83
    public static Kind ForMatch(bool townOwned)
    {
        return townOwned ? Kind.Town : Kind.Player;
    }

    // C++: LeaderboardData.h:85-88
    public static bool ShouldResetFighters(int version)
    {
        return version < CurrentVersion;
    }

    // C++: LeaderboardData.h:90-100 (the const and non-const overloads are one method: the list is a reference either way)
    public static List<Standing> Standings(Collections data, Kind kind)
    {
        return kind == Kind.Town ? data.TownStandings : data.PlayerStandings;
    }

    // C++: LeaderboardData.h:102-123
    public static Standing EnsureStanding(
        Collections data,
        Kind kind,
        string id,
        string name)
    {
        List<Standing> rows = Standings(data, kind);
        for (int i = 0; i < rows.Count; ++i)
        {
            if (rows[i].Id != id)
                continue;
            if (name.Length != 0)
                rows[i].Name = name;
            return rows[i];
        }

        var row = new Standing();
        row.Id = id;
        row.Name = name;
        rows.Add(row);
        return rows[rows.Count - 1];
    }

    // C++: LeaderboardData.h:125-144
    public static Progression EnsureProgression(
        Collections data,
        string id,
        string name)
    {
        for (int i = 0; i < data.Progression.Count; ++i)
        {
            if (data.Progression[i].Id != id)
                continue;
            if (name.Length != 0)
                data.Progression[i].Name = name;
            return data.Progression[i];
        }

        var row = new Progression();
        row.Id = id;
        row.Name = name;
        data.Progression.Add(row);
        return data.Progression[data.Progression.Count - 1];
    }

    // C++: LeaderboardData.h:146-150
    public static int AwardedTotal(int value, int increase)
    {
        if (increase <= 0) return value;
        return value > int.MaxValue - increase ? int.MaxValue : value + increase;
    }

    // C++: LeaderboardData.h:152-166
    public static void ApplyCompetitiveResult(
        Collections data,
        Kind kind,
        string id,
        string name,
        float mmr,
        int winsDelta,
        int lossesDelta)
    {
        Standing row = EnsureStanding(data, kind, id, name);
        row.Mmr = mmr;
        row.Wins = AwardedTotal(row.Wins, winsDelta);
        row.Losses = AwardedTotal(row.Losses, lossesDelta);
        row.Matches = AwardedTotal(row.Matches, 1);
    }

    // C++: LeaderboardData.h:168-176
    public static void AddMarks(
        Collections data,
        string id,
        string name,
        int marks)
    {
        Progression row = EnsureProgression(data, id, name);
        row.Marks = AwardedTotal(row.Marks, marks);
    }

    // C++: LeaderboardData.h:178-184
    public static void ResetFighters(Collections data)
    {
        data.PlayerStandings.Clear();
        data.TownStandings.Clear();
        data.Progression.Clear();
        data.FactionUnlocks.Clear();
    }
}

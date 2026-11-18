// Port of upstream/ProvingGrounds/plugin/src/ArenaSnapshot.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++ namespace <c>ArenaPersistence</c>: the save snapshot (ArenaSnapshot.h), its JSON codec (ArenaSnapshotJson.cs) and the
/// repository over abstract storage (ArenaSnapshotRepository.cs). PA §2.20.
/// <para>The three types hold strings and vectors and the C++ copies them BY VALUE (<c>captured=snapshot</c>, <c>LoadResult rebound =
/// *generationMatch</c>, <c>result.snapshot=parsed</c>), so they are sealed classes with a deep <c>Clone()</c>: every by-value copy of
/// the C++ is a Clone here.</para></summary>
public static partial class ArenaPersistence
{
    /// <summary>C++: ArenaSnapshot.h:6-15</summary>
    public sealed class PlannedNpcBout
    {
        public bool Active;
        public string RegistryId;
        public double StartHours, ScoreA, ScoreB;
        public List<string> TeamA, TeamB;
        public bool Market, WagerPending;
        public int WagerSide, Stake, Payout;

        // C++: ArenaSnapshot.h:13-14
        public PlannedNpcBout()
        {
            Active = false; RegistryId = ""; StartHours = 0; ScoreA = 0; ScoreB = 0;
            TeamA = new List<string>(); TeamB = new List<string>();
            Market = false; WagerPending = false; WagerSide = -1; Stake = 0; Payout = 0;
        }

        /// <summary>The C++ implicit copy constructor (new team lists). Rule: a C++ copy CONSTRUCTION (<c>Snapshot s = loaded.snapshot;</c>, a by-value argument or return) is <c>Clone()</c>; a C++ copy ASSIGNMENT into an object others may hold (<c>member = value</c>, <c>out = x</c>) is <c>AssignFrom</c>.</summary>
        public PlannedNpcBout Clone() => new PlannedNpcBout
        {
            Active = Active, RegistryId = RegistryId, StartHours = StartHours, ScoreA = ScoreA, ScoreB = ScoreB,
            TeamA = new List<string>(TeamA), TeamB = new List<string>(TeamB),
            Market = Market, WagerPending = WagerPending, WagerSide = WagerSide, Stake = Stake, Payout = Payout,
        };

        /// <summary>The C++ implicit copy assignment <c>*this = other</c>: the team lists keep their identity. Rule: a C++ copy CONSTRUCTION (<c>Snapshot s = loaded.snapshot;</c>, a by-value argument or return) is <c>Clone()</c>; a C++ copy ASSIGNMENT into an object others may hold (<c>member = value</c>, <c>out = x</c>) is <c>AssignFrom</c>.</summary>
        public void AssignFrom(PlannedNpcBout other)
        {
            if (ReferenceEquals(this, other)) return;
            Active = other.Active; RegistryId = other.RegistryId; StartHours = other.StartHours; ScoreA = other.ScoreA; ScoreB = other.ScoreB;
            TeamA.Clear(); TeamA.AddRange(other.TeamA); TeamB.Clear(); TeamB.AddRange(other.TeamB);
            Market = other.Market; WagerPending = other.WagerPending; WagerSide = other.WagerSide; Stake = other.Stake; Payout = other.Payout;
        }
    }

    /// <summary>C++: ArenaSnapshot.h:16-17</summary>
    public enum LoadCode
    {
        Ready,
        Missing,
        LegacyReset,
        InvalidJson,
        InvalidData,
        UnsupportedVersion,
        WrongSnapshot,
        IoFailure,
    }

    /// <summary>C++: ArenaSnapshot.h:18-30</summary>
    public sealed class Snapshot
    {
        public int Version;
        public string SaveKey, Generation;
        public LeaderboardData.Collections Fighters;
        public TownChallengePolicy.Card Challenges;
        public int BookieCredit, ChallengeCredit;
        public TownDiagnosticData.State Diagnostic;
        public PlannedNpcBout PlannedNpc;
        // Runtime-only warning. It is deliberately not serialized; a later save
        // replaces an invalid diagnostic subsection with the safe default state.
        public string DiagnosticError;

        // C++: ArenaSnapshot.h:29 (the members default-constructed: empty strings and tables, a new Card, State and bout)
        public Snapshot()
        {
            Version = 16; SaveKey = ""; Generation = "";
            Fighters = new LeaderboardData.Collections();
            Challenges = new TownChallengePolicy.Card();
            BookieCredit = 0; ChallengeCredit = 0;
            Diagnostic = new TownDiagnosticData.State();
            PlannedNpc = new PlannedNpcBout();
            DiagnosticError = "";
        }

        /// <summary>The C++ implicit copy constructor: tables, card and bout deep-copied (State is a struct and copies by value). Rule: a C++ copy CONSTRUCTION (<c>Snapshot s = loaded.snapshot;</c>, a by-value argument or return) is <c>Clone()</c>; a C++ copy ASSIGNMENT into an object others may hold (<c>member = value</c>, <c>out = x</c>) is <c>AssignFrom</c>.</summary>
        public Snapshot Clone() => new Snapshot
        {
            Version = Version, SaveKey = SaveKey, Generation = Generation,
            Fighters = Fighters.DeepClone(),
            Challenges = Challenges.Clone(),
            BookieCredit = BookieCredit, ChallengeCredit = ChallengeCredit,
            Diagnostic = Diagnostic,
            PlannedNpc = PlannedNpc.Clone(),
            DiagnosticError = DiagnosticError,
        };

        /// <summary>The C++ implicit copy assignment <c>*this = other</c> (<c>snapshot = value</c>, <c>out = snapshot</c>, ArenaLedger.h:35-36/39):
        /// this object, <see cref="Fighters"/>, <see cref="Challenges"/> and <see cref="PlannedNpc"/> keep their identity (LeaderboardStore.cpp:30-34
        /// holds references to them across Activate and Block); their contents become deep copies of <paramref name="other"/>'s. Rule: a C++ copy CONSTRUCTION (<c>Snapshot s = loaded.snapshot;</c>, a by-value argument or return) is <c>Clone()</c>; a C++ copy ASSIGNMENT into an object others may hold (<c>member = value</c>, <c>out = x</c>) is <c>AssignFrom</c>.</summary>
        public void AssignFrom(Snapshot other)
        {
            if (ReferenceEquals(this, other)) return;
            Version = other.Version; SaveKey = other.SaveKey; Generation = other.Generation;
            Fighters.AssignFrom(other.Fighters);
            Challenges.AssignFrom(other.Challenges);
            BookieCredit = other.BookieCredit; ChallengeCredit = other.ChallengeCredit;
            Diagnostic = other.Diagnostic;
            PlannedNpc.AssignFrom(other.PlannedNpc);
            DiagnosticError = other.DiagnosticError;
        }
    }

    /// <summary>C++: ArenaSnapshot.h:31-36</summary>
    public sealed class LoadResult
    {
        public LoadCode Code;
        public Snapshot Snapshot;
        public string Field, Message;

        // C++: ArenaSnapshot.h:35
        public LoadResult() { Code = LoadCode.Missing; Snapshot = new Snapshot(); Field = ""; Message = ""; }

        /// <summary>The C++ implicit copy constructor.</summary>
        public LoadResult Clone() => new LoadResult { Code = Code, Snapshot = Snapshot.Clone(), Field = Field, Message = Message };
    }
}

// Port of upstream/ProvingGrounds/plugin/src/TownDiagnosticData.h (Proving Grounds by Shutori, GPLv3).
using System.Runtime.CompilerServices;

namespace ProvingGrounds.Core;

/// <summary>C++: TownDiagnosticData.h (header-only, namespace <c>TownDiagnosticData</c>): the controlled diagnostic run's saved state
/// (TW §2.17). Every struct is copied by value in the C++ (<c>s = State()</c>, <c>const Pending p = s.pending</c>,
/// <c>s.pending = p</c>), so they are C# structs and the fixed arrays inside are <c>[InlineArray]</c>s that copy with them.
/// Note (brief, task 1.8): .NET throws on the built-in Equals/GetHashCode of a struct holding an InlineArray; compare fields.
/// Fields the C++ declares <c>int</c> although they hold an enum (<c>status</c>, <c>model</c>, ...) stay <c>int</c>, so a saved
/// out-of-range value behaves the same.</summary>
public static class TownDiagnosticData
{
    /// <summary>C++: TownDiagnosticData.h:4</summary>
    public enum RunStatus { Off = 0, Active = 1, Paused = 2, Complete = 3, Ended = 4 }
    /// <summary>C++: TownDiagnosticData.h:5 (<c>ModelCount</c> is a member, as in the C++).</summary>
    public enum Model { StatsOnly = 0, ReducedMmr = 1, FullMmr = 2, ModelCount = 3 }
    /// <summary>C++: TownDiagnosticData.h:6</summary>
    public enum Coverage { Duel = 0, EqualGroup = 1, DifferenceOne = 2, DifferenceLarge = 3, CoverageCount = 4 }

    /// <summary>The enum counts as ints, for array lengths and loops (the C++ uses the enum members directly).</summary>
    public const int ModelCount = (int)Model.ModelCount;
    public const int CoverageCount = (int)Coverage.CoverageCount;

    /// <summary><c>bool[ModelCount]</c></summary>
    [InlineArray(ModelCount)] public struct ModelFlags { bool _e; }
    /// <summary><c>bool[CoverageCount]</c></summary>
    [InlineArray(CoverageCount)] public struct CoverageFlags { bool _e; }
    /// <summary><c>int[ModelCount]</c></summary>
    [InlineArray(ModelCount)] public struct ModelInts { int _e; }
    /// <summary><c>int[CoverageCount]</c></summary>
    [InlineArray(CoverageCount)] public struct CoverageInts { int _e; }
    /// <summary><c>double[ModelCount]</c></summary>
    [InlineArray(ModelCount)] public struct ModelDoubles { double _e; }

    /// <summary>C++: TownDiagnosticData.h:8-19</summary>
    public struct Config
    {
        public int Target;
        public ModelFlags Models;
        public CoverageFlags Coverage;
        public double ReducedInfluence;
        public int UnderdogPercent;
        public bool BalanceStyleTier;

        // C++: TownDiagnosticData.h:15-18
        public Config()
        {
            Target = 150; ReducedInfluence = .25; UnderdogPercent = 20; BalanceStyleTier = true;
            for (int i = 0; i < ModelCount; ++i) Models[i] = true;
            for (int i = 0; i < CoverageCount; ++i) Coverage[i] = true;
        }
    }

    /// <summary>C++: TownDiagnosticData.h:21-34</summary>
    public struct Counters
    {
        public int Opened, Completed, Cancelled, Aborted;
        public int TeamAWins, TeamBWins, LargerWins, SmallerWins, EqualWins, FavoriteWins;
        public ModelInts ModelCompleted;
        public CoverageInts CoverageCompleted;
        public double SchedulingHours, LastCompletedHours;
        public int SchedulingSamples;

        // C++: TownDiagnosticData.h:28-33 (the arrays are zeroed, as the InlineArrays start)
        public Counters()
        {
            Opened = 0; Completed = 0; Cancelled = 0; Aborted = 0;
            TeamAWins = 0; TeamBWins = 0; LargerWins = 0; SmallerWins = 0; EqualWins = 0;
            FavoriteWins = 0; SchedulingHours = 0; LastCompletedHours = 0; SchedulingSamples = 0;
            for (int i = 0; i < ModelCount; ++i) ModelCompleted[i] = 0;
            for (int i = 0; i < CoverageCount; ++i) CoverageCompleted[i] = 0;
        }
    }

    /// <summary>C++: TownDiagnosticData.h:36-47</summary>
    public struct Pending
    {
        public bool Active, Underdog;
        public uint Sequence;
        public int Model, RequestedCoverage, ActualCoverage, Relaxation;
        public int SizeA, SizeB;
        public ModelDoubles ScoreA, ScoreB, ProbabilityA;
        public double AnnouncedHours;

        // C++: TownDiagnosticData.h:43-46
        public Pending()
        {
            Active = false; Underdog = false; Sequence = 0; Model = (int)TownDiagnosticData.Model.StatsOnly;
            RequestedCoverage = (int)TownDiagnosticData.Coverage.Duel; ActualCoverage = (int)TownDiagnosticData.Coverage.Duel;
            Relaxation = 0; SizeA = 0; SizeB = 0; AnnouncedHours = 0;
            for (int i = 0; i < ModelCount; ++i) ScoreA[i] = ScoreB[i] = ProbabilityA[i] = 0;
        }
    }

    /// <summary>C++: TownDiagnosticData.h:49-56</summary>
    public struct State
    {
        public int Status;
        public uint RunId, NextSequence;
        public Config Config;
        public Counters Counters;
        public Pending Pending;

        // C++: TownDiagnosticData.h:55 (the members are default-constructed, as the C++ does)
        public State()
        {
            Status = (int)RunStatus.Off; RunId = 0; NextSequence = 1;
            Config = new Config(); Counters = new Counters(); Pending = new Pending();
        }
    }

    static readonly string[] StatusNames = { "off", "active", "paused", "complete", "ended" };
    static readonly string[] ModelNames = { "stats", "reduced_mmr", "full_mmr" };
    static readonly string[] CoverageNames = { "1v1", "equal_group", "difference_one", "difference_large" };

    // C++: TownDiagnosticData.h:58-61
    public static string StatusName(int value) =>
        value >= (int)RunStatus.Off && value <= (int)RunStatus.Ended ? StatusNames[value] : "invalid";

    // C++: TownDiagnosticData.h:62-65
    public static string ModelName(int value) => value >= 0 && value < ModelCount ? ModelNames[value] : "invalid";

    // C++: TownDiagnosticData.h:66-69
    public static string CoverageName(int value) => value >= 0 && value < CoverageCount ? CoverageNames[value] : "invalid";
}

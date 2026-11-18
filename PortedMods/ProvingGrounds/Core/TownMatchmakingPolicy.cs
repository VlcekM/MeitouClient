// Port of upstream/ProvingGrounds/plugin/src/TownMatchmakingPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++: TownMatchmakingPolicy.h (header-only, namespace <c>TownMatchmakingPolicy</c>): the power score, the player-challenge
/// search, the ambient (NPC vs NPC) search and the controlled diagnostic search (TW §2.3, §2.4, §2.17).
/// <para>Types: <see cref="Fighter"/> is a struct (the C++ copies fighters by value, <c>std::vector&lt;Fighter&gt;</c>; see its
/// summary for <c>default(Fighter)</c>). <see cref="Match"/> is a sealed class with a deep <see cref="Match.Clone"/> called at every C++ by-value copy that is
/// later mutated or kept (a struct would share its <c>List</c>s between copies). A history (<c>typedef
/// std::vector&lt;std::vector&lt;std::string&gt; &gt; History</c>) is a <c>List&lt;List&lt;string&gt;&gt;</c>.</para>
/// <para>Containers: <c>std::set&lt;std::string&gt;</c> that is iterated (the player ids) is a SortedSet with
/// <c>StringComparer.Ordinal</c>; sets used only for membership (<c>seen</c>, <c>excluded</c>, previous types) are HashSets with the
/// same comparer, which answer the same; <c>std::map&lt;std::string, std::string&gt;</c> is a SortedDictionary (Ordinal). Strings
/// compare as std::string does for ASCII (ordinal; see docs/divergences.md for non-ASCII ids). <c>std::sort</c> runs only over
/// strings (equal strings are identical) or with comparators that never tie two distinct fighters (ids are unique in a roster), so
/// an unstable sort gives the C++ order. Every double expression keeps the C++ operand order; <c>std::pow</c> is
/// <see cref="Math.Pow"/>, which may differ from glibc in the last bit (Tests/Golden/MatchmakingGoldenTests.cs states how often
/// that changes a lineup).</para></summary>
public static class TownMatchmakingPolicy
{
    /// <summary>C++: TownMatchmakingPolicy.h:15</summary>
    public enum Division { Easy = 0, Medium = 1, Hard = 2 }

    /// <summary>C++: TownMatchmakingPolicy.h:16-21. Id is a unique live persistent identity; role is the exact FCS character ID.
    /// Construct it with <c>new Fighter()</c> (or an initializer on it), which gives the C++ defaults ("", "", ability 0, mmr 100).
    /// <c>default(Fighter)</c> and the elements of a new <c>Fighter[]</c> skip that constructor: mmr 0 and null strings, which the
    /// C++ can never produce.</summary>
    public struct Fighter
    {
        public string Id, Role;
        public double Ability, Mmr;

        // C++: TownMatchmakingPolicy.h:20
        public Fighter() { Id = ""; Role = ""; Ability = 0; Mmr = 100; }
    }

    /// <summary>C++: TownMatchmakingPolicy.h:23-39. <c>enum Failure</c> is <see cref="FailureCode"/> here, because C# cannot name a
    /// nested type and the field <see cref="Failure"/> alike.</summary>
    public sealed class Match
    {
        public enum FailureCode { NoFailure = 0, PlayersTooWeak = 1 }
        public bool Valid;
        public List<string> A = new List<string>(), B = new List<string>();
        public double ScoreA, ScoreB;
        public bool Diagnostic, DiagnosticUnderdog, BestAvailable;
        public FailureCode Failure;
        public int DiagnosticModel, RequestedCoverage, ActualCoverage, Relaxation;
        public double[] ModelScoreA = new double[TownDiagnosticData.ModelCount];
        public double[] ModelScoreB = new double[TownDiagnosticData.ModelCount];
        public double[] ModelProbabilityA = new double[TownDiagnosticData.ModelCount];

        // C++: TownMatchmakingPolicy.h:34-38
        public Match()
        {
            Valid = false; ScoreA = 0; ScoreB = 0; Diagnostic = false; DiagnosticUnderdog = false; BestAvailable = false;
            Failure = FailureCode.NoFailure;
            DiagnosticModel = (int)TownDiagnosticData.Model.StatsOnly; RequestedCoverage = (int)TownDiagnosticData.Coverage.Duel;
            ActualCoverage = (int)TownDiagnosticData.Coverage.Duel; Relaxation = 0;
            for (int i = 0; i < TownDiagnosticData.ModelCount; ++i) ModelScoreA[i] = ModelScoreB[i] = ModelProbabilityA[i] = 0;
        }

        /// <summary>The C++ by-value copy (implicit copy constructor): every list and array copied.</summary>
        public Match Clone() => new Match
        {
            Valid = Valid, A = new List<string>(A), B = new List<string>(B), ScoreA = ScoreA, ScoreB = ScoreB,
            Diagnostic = Diagnostic, DiagnosticUnderdog = DiagnosticUnderdog, BestAvailable = BestAvailable, Failure = Failure,
            DiagnosticModel = DiagnosticModel, RequestedCoverage = RequestedCoverage, ActualCoverage = ActualCoverage, Relaxation = Relaxation,
            ModelScoreA = (double[])ModelScoreA.Clone(), ModelScoreB = (double[])ModelScoreB.Clone(),
            ModelProbabilityA = (double[])ModelProbabilityA.Clone(),
        };
    }

    /// <summary>C++: TownMatchmakingPolicy.h:40-51 (returned by value from TownDiagnosticRun::NextRequest).</summary>
    public struct DiagnosticRequest
    {
        public bool Active, Underdog, BalanceStyleTier;
        public TownDiagnosticData.CoverageFlags EnabledCoverage;
        public int Model, Coverage, DesiredType, DesiredTier;
        public double Influence, ReducedInfluence;
        public uint Seed;

        // C++: TownMatchmakingPolicy.h:46-50
        public DiagnosticRequest()
        {
            Active = false; Underdog = false; BalanceStyleTier = true;
            Model = (int)TownDiagnosticData.Model.StatsOnly; Coverage = (int)TownDiagnosticData.Coverage.Duel;
            DesiredType = 0; DesiredTier = 0; Influence = 0; ReducedInfluence = .25; Seed = 0;
            for (int i = 0; i < TownDiagnosticData.CoverageCount; ++i) EnabledCoverage[i] = true;
        }
    }

    // C++: TownMatchmakingPolicy.h:52-55
    public static bool Finite(double value)
    {
#pragma warning disable CS1718 // value == value is the C++'s NaN test
        return value == value && value <= double.MaxValue && value >= -double.MaxValue;
#pragma warning restore CS1718
    }

    // C++: TownMatchmakingPolicy.h:56-60
    public static double Ability(double attack, double defence, double strength, double toughness, double dexterity)
    {
        if (!Finite(attack) || !Finite(defence) || !Finite(strength) || !Finite(toughness) || !Finite(dexterity)) return -1;
        double score = 10 + .30 * attack + .30 * defence + .15 * strength + .15 * toughness + .10 * dexterity;
        return Finite(score) && score > 0 ? score : -1;
    }

    // C++: TownMatchmakingPolicy.h:61-66
    public static double RatingFactor(double mmr, double influence)
    {
        if (!Finite(mmr) || mmr < 0) return -1;
        if (!Finite(influence) || influence < 0 || influence > 1) return -1;
        double factor = Math.Pow(10.0, influence * (mmr - 100.0) / 400.0);
        return Finite(factor) && factor > 0 ? factor : -1;
    }

    // C++: TownMatchmakingPolicy.h:67
    public static double RatingFactor(double mmr) { return RatingFactor(mmr, 1.0); }

    // Live values from BalanceTuning, so a controlled diagnostic run can move them
    // without a rebuild. Shipped: exponent 2.5, full-model influence 1.0.
    // C++: TownMatchmakingPolicy.h:71
    public static double AbilityExponent() { return BalanceTuning.Get().AbilityExponent; }
    // C++: TownMatchmakingPolicy.h:72
    public static double RatingInfluence() { return BalanceTuning.Get().RatingInfluence; }

    // C++: TownMatchmakingPolicy.h:73-81
    public static double FighterPower(double ability, double mmr, double influence)
    {
        if (!Finite(ability) || ability <= 0) return -1;
        double rating = RatingFactor(mmr, influence);
        if (rating <= 0) return -1;
        // Stats, equipment bonuses, damage and survivability compound in Kenshi;
        // MMR remains a separate, modest correction around the neutral 100 baseline.
        double power = Math.Pow(ability, AbilityExponent()) * rating;
        return Finite(power) && power > 0 ? power : -1;
    }

    // C++: TownMatchmakingPolicy.h:82
    public static double FighterPower(double ability, double mmr) { return FighterPower(ability, mmr, RatingInfluence()); }

    /// <summary>A null array is the C++ null pointer. The arrays must hold at least <paramref name="count"/> values (the C++ reads past
    /// the end otherwise).</summary>
    // C++: TownMatchmakingPolicy.h:83-92
    public static double TeamScore(double[]? abilities, double[]? ratings, int count, double influence)
    {
        if (abilities == null || ratings == null || count <= 0) return -1;
        double total = 0;
        for (int i = 0; i < count; ++i)
        {
            double power = FighterPower(abilities[i], ratings[i], influence);
            if (power <= 0) return -1;
            total += power;
        }
        return Finite(total) && total > 0 ? total : -1;
    }

    // C++: TownMatchmakingPolicy.h:93-95
    public static double TeamScore(double[]? abilities, double[]? ratings, int count)
    {
        return TeamScore(abilities, ratings, count, RatingInfluence());
    }

    // C++: TownMatchmakingPolicy.h:96-100
    public static double TeamScore(double[]? abilities, int count)
    {
        if (abilities == null || count <= 0 || count > 4) return -1;
        double[] ratings = { 100, 100, 100, 100 };
        return TeamScore(abilities, ratings, count);
    }

    // C++: TownMatchmakingPolicy.h:101-108
    public static void RecordHistory(List<List<string>> history, List<string> ids)
    {
        if (ids.Count == 0) return;
        var sorted = new List<string>(ids);
        sorted.Sort(StringComparer.Ordinal);
        Detail.Unique(sorted);
        history.Add(sorted);
        if (history.Count > 5) history.RemoveRange(0, history.Count - 5);
    }

    /// <summary>C++: <c>namespace TownMatchmakingPolicy::Detail</c> (TownMatchmakingPolicy.h:110-241, 424-470, 542-632).</summary>
    public static class Detail
    {
        // C++: TownMatchmakingPolicy.h:111-115 (bit-identical to Hashing.Xorshift32, which documents this line)
        public static uint Next(ref uint seed) => Hashing.Xorshift32(ref seed);

        // C++: TownMatchmakingPolicy.h:116-120 (bit-identical to Hashing.Fnv1a32; strings as UTF-8, docs/divergences.md)
        public static uint Hash(uint seed, string value) => Hashing.Fnv1a32(seed, value);

        // C++: TownMatchmakingPolicy.h:121-125
        public static bool Named(string role)
        {
            return role == "616-Proving Grounds.mod" || role == "619-Proving Grounds.mod" ||
                role == "623-Proving Grounds.mod" || role == "169-Proving Grounds.mod" ||
                role == "168-Proving Grounds.mod" || role == "167-Proving Grounds.mod";
        }

        // C++: TownMatchmakingPolicy.h:126-132
        public static int NamedDivision(string role)
        {
            if (role == "616-Proving Grounds.mod") return (int)Division.Easy;
            if (role == "619-Proving Grounds.mod") return (int)Division.Medium;
            if (role == "623-Proving Grounds.mod" || role == "169-Proving Grounds.mod" ||
                role == "168-Proving Grounds.mod" || role == "167-Proving Grounds.mod") return (int)Division.Hard;
            return -1;
        }

        // C++: TownMatchmakingPolicy.h:133 (struct ById)
        sealed class ById : IComparer<Fighter>
        {
            public static readonly ById Instance = new ById();
            public int Compare(Fighter a, Fighter b) => string.CompareOrdinal(a.Id, b.Id);
        }

        // C++: TownMatchmakingPolicy.h:134-142 (struct ByPower: left == right ? a.id < b.id : left < right)
        sealed class ByPower : IComparer<Fighter>
        {
            readonly double _influence;
            public ByPower(double ratingInfluence) { _influence = ratingInfluence; }
            public int Compare(Fighter a, Fighter b)
            {
                double left = FighterPower(a.Ability, a.Mmr, _influence);
                double right = FighterPower(b.Ability, b.Mmr, _influence);
                if (left == right) return string.CompareOrdinal(a.Id, b.Id);
                return left < right ? -1 : 1;
            }
        }

        /// <summary>The C++ default argument <c>influence = RatingInfluence()</c> is the overload without it.</summary>
        // C++: TownMatchmakingPolicy.h:143-180
        public static List<Fighter> Roster(List<Fighter> input, uint seed, ISet<string> excluded, string anchor)
        {
            return Roster(input, seed, excluded, anchor, RatingInfluence());
        }

        // C++: TownMatchmakingPolicy.h:143-180
        public static List<Fighter> Roster(List<Fighter> input, uint seed, ISet<string> excluded, string anchor, double influence)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < input.Count; ++i) { counts.TryGetValue(input[i].Id, out int c); counts[input[i].Id] = c + 1; }
            var regular = new List<Fighter>();
            var named = new List<Fighter>();
            for (int i = 0; i < input.Count; ++i)
            {
                Fighter f = input[i];
                if (f.Id.Length == 0 || counts[f.Id] != 1 || excluded.Contains(f.Id) || !Finite(f.Ability) || f.Ability <= 0) continue;
                if (Named(f.Role))
                {
                    if (anchor.Length != 0 && f.Role == anchor) named.Add(f);
                }
                else if (TownFighterCatalog.Find(f.Role) != null) regular.Add(f);
            }
            regular.Sort(new ByPower(influence));
            named.Sort(ById.Instance);
            // Keep the bounded search representative across the entire strength range.
            // An identity-contiguous sample could omit every weak opponent and falsely
            // report PlayersTooWeak even when an eligible fighter existed outside it.
            if (regular.Count > 64)
            {
                uint rotation = Next(ref seed);
                var sample = new List<Fighter>();
                ulong size = (ulong)regular.Count;
                for (ulong i = 0; i < 64; ++i)
                {
                    ulong begin = i * size / 64;
                    ulong end = (i + 1) * size / 64;
                    ulong bucketSize = end - begin;
                    // size_t offset = (rotation + static_cast<unsigned>(i * 2654435761u)) % bucketSize;
                    ulong offset = unchecked(rotation + (uint)(i * 2654435761u)) % bucketSize;
                    if (i == 0) offset = 0;
                    if (i == 63) offset = bucketSize - 1;
                    sample.Add(regular[(int)(begin + offset)]);
                }
                regular = sample;
            }
            // A named encounter retains its anchor even with a full regular roster.
            if (named.Count != 0) regular.Add(named[(int)(Next(ref seed) % (ulong)named.Count)]);
            regular.Sort(ById.Instance);
            return regular;
        }

        // C++: TownMatchmakingPolicy.h:181-185
        public static string Key(List<string> ids)
        {
            var key = new System.Text.StringBuilder();
            for (int i = 0; i < ids.Count; ++i) { key.Append(ids[i]); key.Append('\0'); }
            return key.ToString();
        }

        // C++: TownMatchmakingPolicy.h:186-206
        public static double Novelty(List<string> ids, List<List<string>> history, SortedDictionary<string, string> types)
        {
            double penalty = 0;
            int first = history.Count > 5 ? history.Count - 5 : 0;
            for (int h = first; h < history.Count; ++h)
            {
                var old = new List<string>(history[h]); old.Sort(StringComparer.Ordinal);
                double weight = 1.0 + .1 * (h - first);
                if (SameIds(old, ids)) penalty += 100 * weight;
                var previousTypes = new HashSet<string>(StringComparer.Ordinal);
                for (int j = 0; j < old.Count; ++j)
                {
                    if (types.TryGetValue(old[j], out string? type)) previousTypes.Add(type);
                }
                for (int j = 0; j < ids.Count; ++j)
                {
                    if (old.BinarySearch(ids[j], StringComparer.Ordinal) >= 0) penalty += weight / ids.Count;
                    if (types.TryGetValue(ids[j], out string? type) && previousTypes.Contains(type)) penalty += .05 * weight / ids.Count;
                }
            }
            return penalty;
        }

        // C++: TownMatchmakingPolicy.h:207-214
        public static SortedDictionary<string, string> Types(List<Fighter> roster)
        {
            var types = new SortedDictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < roster.Count; ++i)
            {
                TownFighterCatalog.Entry? entry = TownFighterCatalog.Find(roster[i].Role);
                if (entry != null) types[roster[i].Id] = entry.Type;
            }
            return types;
        }

        /// <summary>C++: TownMatchmakingPolicy.h:215-229 (a class: the C++ copies it once, <c>byCount[i] = favorableByCount[i]</c>, ported
        /// as <see cref="Clone"/>).</summary>
        public sealed class Ranked
        {
            public Match Match = new Match();
            public int Fit;
            public double Novelty;
            public uint Tie;

            public Ranked Clone() => new Ranked { Match = Match.Clone(), Fit = Fit, Novelty = Novelty, Tie = Tie };

            // C++: TownMatchmakingPolicy.h:221-228
            public void Consider(Match candidate, double distance, double variety, uint tieBreak)
            {
                // Within a 2.5 percentage-point fit bucket, variety wins over tiny score differences.
                int bucket = (int)(distance / .025);
                if (!Match.Valid || bucket < Fit || (bucket == Fit &&
                    (variety < Novelty || (variety == Novelty && tieBreak < Tie))))
                {
                    Match = candidate.Clone(); Fit = bucket; Novelty = variety; Tie = tieBreak;
                }
            }
        }

        /// <summary>The C++ default argument <c>influence = RatingInfluence()</c> is the overload without it.</summary>
        // C++: TownMatchmakingPolicy.h:230-240
        public static double Score(List<Fighter> roster, List<int> indices, List<string> ids)
        {
            return Score(roster, indices, ids, RatingInfluence());
        }

        // C++: TownMatchmakingPolicy.h:230-240
        public static double Score(List<Fighter> roster, List<int> indices, List<string> ids, double influence)
        {
            double[] values = new double[4], ratings = new double[4];
            for (int i = 0; i < indices.Count; ++i)
            {
                values[i] = roster[indices[i]].Ability;
                ratings[i] = roster[indices[i]].Mmr;
                ids.Add(roster[indices[i]].Id);
            }
            ids.Sort(StringComparer.Ordinal);
            return TeamScore(values, ratings, indices.Count, influence);
        }

        // The ambient band boundaries used to live here as kCloseRatioCap (1.15) and
        // kParityRatioCap (1.30). They are now BalanceTuning ambientCloseCap / ambientRatioCap.
        // Ratio cap while the showcase band is enabled (TownMatchmakingPolicy.h:425-442 explain the pricing consequences).
        // C++: TownMatchmakingPolicy.h:443
        public const double ShowcaseRatioHigh = 1.80;

        // C++: TownMatchmakingPolicy.h:444-469
        internal static void AmbientCandidate(List<Fighter> roster, List<int> a,
            List<int> b, List<List<string>> history, SortedDictionary<string, string> types,
            double closeTarget, double underdogTarget, double showcaseTarget,
            double closeCap, double ratioCap,
            int attendance, uint seed, HashSet<string> seen, Ranked[] best)
        {
            var candidate = new Match();
            candidate.ScoreA = Score(roster, a, candidate.A); candidate.ScoreB = Score(roster, b, candidate.B);
            if (candidate.ScoreA <= 0 || candidate.ScoreB <= 0) return;
            double ratio = Max(candidate.ScoreA, candidate.ScoreB) / Min(candidate.ScoreA, candidate.ScoreB);
            if (ratio > ratioCap + 1e-12) return;
            if (LessIds(candidate.B, candidate.A)) { (candidate.A, candidate.B) = (candidate.B, candidate.A); (candidate.ScoreA, candidate.ScoreB) = (candidate.ScoreB, candidate.ScoreA); }
            string key = Key(candidate.A) + '\u0001' + Key(candidate.B);
            if (!seen.Add(key)) return;
            var members = new List<string>(candidate.A); members.AddRange(candidate.B);
            members.Sort(StringComparer.Ordinal);
            candidate.Valid = true;
            // 0 close (<= closeCap), 1 underdog ((closeCap, ratioCap]), 2 showcase
            // (> ratioCap, reachable only while showcasePercent > 0). closeCap and
            // ratioCap are the live BalanceTuning values, so both the boundary and the
            // target below come from the same band shape.
            // port note: ratioCap here is the cap the caller passed (1.80 while the showcase band is on), and every candidate above it
            // has already returned, so category 2 is never filled; the shipped C++ does the same (kept as is).
            int category = ratio <= closeCap + 1e-12 ? 0 :
                ratio <= ratioCap + 1e-12 ? 1 : 2;
            double variety = Novelty(members, history, types) + .01 * Math.Abs(members.Count - attendance);
            double target = category == 2 ? showcaseTarget : category == 1 ? underdogTarget : closeTarget;
            best[category].Consider(candidate, Math.Abs(ratio - target), variety, Hash(seed, key));
        }

        // C++: TownMatchmakingPolicy.h:543-547
        public static int CoverageFor(int a, int b)
        {
            if (a == 1 && b == 1) return (int)TownDiagnosticData.Coverage.Duel;
            if (a == b) return (int)TownDiagnosticData.Coverage.EqualGroup;
            return Math.Abs(a - b) == 1 ? (int)TownDiagnosticData.Coverage.DifferenceOne : (int)TownDiagnosticData.Coverage.DifferenceLarge;
        }

        // Catalog type names, in the order the diagnostic's round-robin steps through
        // them. ONE table behind both the index and the count, so the two can never
        // disagree. Adding a type to the catalog means adding it here: DesiredType()
        // walks (nextSequence-1) % TypeCount and HasTarget matches on TypeIndex, which
        // returns -1 for an unknown type, so a catalog type missing from this list can
        // never be targeted by a controlled run and its bouts read as relaxations.
        // C++: TownMatchmakingPolicy.h:554-558
        static readonly string[] TypeNamesTable = { "Polearm", "Sabre", "Katana", "Champion", "Mace", "Greatsword", "Martial", "Hacker" };
        public static IReadOnlyList<string> TypeNames(out int count) { count = TypeNamesTable.Length; return TypeNamesTable; }

        // C++: TownMatchmakingPolicy.h:559-563
        public static int TypeCount()
        {
            TypeNames(out int count);
            return count;
        }

        /// <summary>A null <paramref name="type"/> is the C++ NULL pointer (-1).</summary>
        // C++: TownMatchmakingPolicy.h:564-569
        public static int TypeIndex(string? type)
        {
            IReadOnlyList<string> names = TypeNames(out int count);
            for (int i = 0; i < count; ++i) if (type != null && string.Equals(type, names[i], StringComparison.Ordinal)) return i;
            return -1;
        }

        // C++: TownMatchmakingPolicy.h:570-579
        public static bool HasTarget(List<Fighter> roster, List<int> a, List<int> b, int type, int tier)
        {
            for (int side = 0; side < 2; ++side)
            {
                List<int> members = side != 0 ? b : a;
                for (int i = 0; i < members.Count; ++i)
                {
                    TownFighterCatalog.Entry? e = TownFighterCatalog.Find(roster[members[i]].Role);
                    if (e != null && TypeIndex(e.Type) == type && e.Tier == tier) return true;
                }
            }
            return false;
        }

        // C++: TownMatchmakingPolicy.h:580-587
        public static double ScoreIds(List<Fighter> roster, List<string> ids, double influence)
        {
            double total = 0; int found = 0;
            for (int i = 0; i < ids.Count; ++i) for (int j = 0; j < roster.Count; ++j) if (roster[j].Id == ids[i])
            {
                double power = FighterPower(roster[j].Ability, roster[j].Mmr, influence);
                if (power <= 0) return -1; total += power; ++found; break;
            }
            return found == ids.Count && total > 0 && Finite(total) ? total : -1;
        }

        // C++: TownMatchmakingPolicy.h:588-596
        public static void FillModels(Match match, List<Fighter> roster, double reduced)
        {
            double[] influences = { 0, reduced, 1 };
            for (int i = 0; i < TownDiagnosticData.ModelCount; ++i)
            {
                match.ModelScoreA[i] = ScoreIds(roster, match.A, influences[i]);
                match.ModelScoreB[i] = ScoreIds(roster, match.B, influences[i]);
                double aa = match.ModelScoreA[i] * match.ModelScoreA[i], bb = match.ModelScoreB[i] * match.ModelScoreB[i];
                match.ModelProbabilityA[i] = aa > 0 && bb > 0 ? aa / (aa + bb) : 0;
            }
        }

        /// <summary>C++: TownMatchmakingPolicy.h:597-611 (never copied in the C++).</summary>
        public sealed class DiagnosticRanked
        {
            public Match Match = new Match();
            public int CoveragePenalty = 9, StylePenalty = 9, Fit = 0;
            public double Novelty = 0;
            public uint Tie = 0;

            // C++: TownMatchmakingPolicy.h:603-610
            public void Consider(Match candidate, int cp, int sp, double distance, double variety, uint t)
            {
                int bucket = (int)(distance / .025);
                if (!Match.Valid || cp < CoveragePenalty || (cp == CoveragePenalty &&
                    (sp < StylePenalty || (sp == StylePenalty && (bucket < Fit || (bucket == Fit &&
                    (variety < Novelty || (variety == Novelty && t < Tie))))))))
                {
                    Match = candidate.Clone(); CoveragePenalty = cp; StylePenalty = sp; Fit = bucket; Novelty = variety; Tie = t;
                }
            }
        }

        // C++: TownMatchmakingPolicy.h:612-631
        internal static void DiagnosticCandidate(List<Fighter> roster, List<int> a, List<int> b,
            List<List<string>> history, SortedDictionary<string, string> types, in DiagnosticRequest request,
            int attendance, uint seed, HashSet<string> seen, DiagnosticRanked best)
        {
            var candidate = new Match(); candidate.ScoreA = Score(roster, a, candidate.A, request.Influence); candidate.ScoreB = Score(roster, b, candidate.B, request.Influence);
            if (candidate.ScoreA <= 0 || candidate.ScoreB <= 0) return;
            double ratio = Max(candidate.ScoreA, candidate.ScoreB) / Min(candidate.ScoreA, candidate.ScoreB);
            if (ratio > 1.30 + 1e-12 || (request.Underdog ? ratio <= 1.15 + 1e-12 : ratio > 1.15 + 1e-12)) return;
            if (LessIds(candidate.B, candidate.A)) { (candidate.A, candidate.B) = (candidate.B, candidate.A); (candidate.ScoreA, candidate.ScoreB) = (candidate.ScoreB, candidate.ScoreA); }
            string key = Key(candidate.A) + '\u0001' + Key(candidate.B); if (!seen.Add(key)) return;
            var members = new List<string>(candidate.A); members.AddRange(candidate.B); members.Sort(StringComparer.Ordinal);
            candidate.Valid = true; int coverage = CoverageFor(a.Count, b.Count);
            if (!request.EnabledCoverage[coverage]) return;
            int cp = coverage == request.Coverage ? 0 : 1;
            int sp = !request.BalanceStyleTier || HasTarget(roster, a, b, request.DesiredType, request.DesiredTier) ? 0 : 1;
            double target = request.Underdog ? 1.225 : 1.075;
            double variety = Novelty(members, history, types) + .01 * Math.Abs(members.Count - attendance);
            candidate.Diagnostic = true; candidate.DiagnosticUnderdog = request.Underdog; candidate.DiagnosticModel = request.Model;
            candidate.RequestedCoverage = request.Coverage; candidate.ActualCoverage = coverage; candidate.Relaxation = cp != 0 ? 2 : sp != 0 ? 1 : 0;
            best.Consider(candidate, cp, sp, Math.Abs(ratio - target), variety, Hash(seed, key));
        }

        // ---- helpers with no C++ function of their own -------------------------------------------------------------------

        // std::max / std::min of two doubles: (max)(a, b) = a < b ? b : a, (min)(a, b) = b < a ? b : a.
        internal static double Max(double a, double b) => a < b ? b : a;
        internal static double Min(double a, double b) => b < a ? b : a;

        // std::vector<std::string> operator== (same length, same strings in order).
        internal static bool SameIds(List<string> x, List<string> y)
        {
            if (x.Count != y.Count) return false;
            for (int i = 0; i < x.Count; ++i) if (!string.Equals(x[i], y[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // std::vector<std::string> operator< (lexicographical_compare).
        internal static bool LessIds(List<string> x, List<string> y)
        {
            int n = Math.Min(x.Count, y.Count);
            for (int i = 0; i < n; ++i)
            {
                int c = string.CompareOrdinal(x[i], y[i]);
                if (c != 0) return c < 0;
            }
            return x.Count < y.Count;
        }

        // sorted.erase(std::unique(sorted.begin(), sorted.end()), sorted.end()) on a sorted list.
        internal static void Unique(List<string> sorted)
        {
            int w = 0;
            for (int r = 0; r < sorted.Count; ++r)
                if (w == 0 || !string.Equals(sorted[w - 1], sorted[r], StringComparison.Ordinal)) sorted[w++] = sorted[r];
            sorted.RemoveRange(w, sorted.Count - w);
        }
    }

    // ---- SelectPlayer ---------------------------------------------------------------------------------------------------------

    static readonly double[] Low = { .55, .85, 1.15 }, High = { .80, 1.10, 1.45 }, Target = { .70, 1.00, 1.30 };
    // Challenge rarity describes actual relative team strength. Direct bookings
    // omit rarity and retain the original narrow division rules above.
    static readonly double[,] RarityLow = { { .25, .45, .70, 1.05 }, { .50, .75, 1.00, 1.35 }, { .85, 1.10, 1.40, 1.80 } };
    static readonly double[,] RarityHigh = { { .45, .70, 1.05, 1.45 }, { .75, 1.00, 1.35, 1.80 }, { 1.10, 1.40, 1.80, 2.30 } };
    static readonly int[] SizeWeights = { 50, 30, 15, 5 };

    /// <summary>The C++ default arguments are the optional parameters (maxEnemies 4, maxTotal 6, requiredRole "", rarity -1,
    /// forceSoloNamed false, teams1v1 false, challengeDifficulty Normal).</summary>
    // C++: TownMatchmakingPolicy.h:243-422
    public static Match SelectPlayer(List<Fighter> players, List<Fighter> npcs,
        Division division, uint seed, List<List<string>> history, int maxEnemies = 4,
        int maxTotal = 6, string requiredRole = "", int rarity = -1,
        bool forceSoloNamed = false, bool teams1v1 = false,
        PGConfig.ChallengeDifficulty challengeDifficulty = PGConfig.ChallengeDifficulty.Normal)
    {
        var empty = new Match();
        if (players.Count == 0 || players.Count > 3 || division < Division.Easy || division > Division.Hard ||
            rarity < -1 || rarity > 3 || maxEnemies < 1 || maxTotal < 2) return empty;
        if (requiredRole.Length != 0 && (!Detail.Named(requiredRole) || Detail.NamedDivision(requiredRole) != (int)division)) return empty;
        // Player challenges use combat ability alone; only NPC-vs-NPC searches use MMR.
        const double influence = 0.0;
        var playerIds = new SortedSet<string>(StringComparer.Ordinal);
        double[] values = new double[3], ratings = new double[3];
        double strongest = 0;
        for (int i = 0; i < players.Count; ++i)
        {
            if (players[i].Id.Length == 0 || !playerIds.Add(players[i].Id)) return empty;
            values[i] = players[i].Ability;
            ratings[i] = players[i].Mmr;
            strongest = Detail.Max(strongest, FighterPower(values[i], ratings[i], influence));
        }
        double playerScore = TeamScore(values, ratings, players.Count, influence);
        if (playerScore <= 0) return empty;
        int limit = Math.Min(4, Math.Min(maxEnemies, maxTotal - players.Count));
        if (limit < 1) return empty;
        List<Fighter> roster = Detail.Roster(npcs, seed, playerIds, requiredRole, influence);
        SortedDictionary<string, string> types = Detail.Types(roster);
        int div = (int)division;
        const double playerDifficulty = .80;
        double selectionScale = challengeDifficulty == PGConfig.ChallengeDifficulty.Easy ? .85 :
            challengeDifficulty == PGConfig.ChallengeDifficulty.Hard ? 1.15 : 1.0;
        double minimum = (rarity < 0 ? Low[div] : RarityLow[div, rarity]) * playerDifficulty * selectionScale;
        double maximum = (rarity < 0 ? High[div] : RarityHigh[div, rarity]) * playerDifficulty * selectionScale;
        // A solo named challenge is the advertised duel. Do not add supporting
        // fighters merely because the model undervalues the unique's gear or combat
        // advantages; only reject the duel when the unique exceeds the safety cap.
        if (forceSoloNamed && requiredRole.Length != 0 && players.Count == 1)
        {
            for (int i = 0; i < roster.Count; ++i)
            {
                if (roster[i].Role != requiredRole) continue;
                var anchorIndex = new List<int> { i };
                var duel = new Match();
                duel.ScoreA = playerScore;
                duel.ScoreB = Detail.Score(roster, anchorIndex, duel.B, influence);
                if (duel.ScoreB <= 0) return empty;
                duel.Valid = true;
                duel.A = new List<string>(playerIds);
                return duel;
            }
            return empty;
        }
        // If even the weakest eligible singleton is above the band, adding more
        // opponents can only worsen the mismatch. Stop before exploring teams.
        double weakestSingle = double.MaxValue;
        bool sawSingleton = false;
        for (int i = 0; i < roster.Count; ++i)
        {
            if (requiredRole.Length != 0 && roster[i].Role != requiredRole) continue;
            double score = FighterPower(roster[i].Ability, roster[i].Mmr, influence);
            if (score <= 0) continue;
            sawSingleton = true;
            weakestSingle = Detail.Min(weakestSingle, score / playerScore);
        }
        if (sawSingleton && weakestSingle > maximum + 1e-12)
        {
            empty.Failure = Match.FailureCode.PlayersTooWeak;
            return empty;
        }
        uint varietySeed = unchecked(seed ^ (0x9e3779b9u * (uint)(1 + div * 4 + rarity)));
        double desired = rarity < 0 ? Target[div] * playerDifficulty * selectionScale :
            minimum + (maximum - minimum) * (Detail.Next(ref varietySeed) % 10001) / 10000.0;
        var best = new Detail.Ranked();
        var byCount = new[] { new Detail.Ranked(), new Detail.Ranked(), new Detail.Ranked(), new Detail.Ranked() };
        var favorableByCount = new[] { new Detail.Ranked(), new Detail.Ranked() };
        var fallback = new Match();
        double fallbackRatio = 0.0;
        double fallbackNovelty = 0.0;
        uint fallbackTie = 0;
        int size = roster.Count;
        // Sentinel -1 visits singleton/pair/triple/quad subsets exactly once.
        for (int i = 0; i < size; ++i) for (int j = -1; j < (limit >= 2 ? size : 0); ++j)
        {
            if (j >= 0 && j <= i) continue;
            for (int k = -1; k < (limit >= 3 && j >= 0 ? size : 0); ++k)
            {
                if (k >= 0 && k <= j) continue;
                for (int l = -1; l < (limit >= 4 && k >= 0 ? size : 0); ++l)
                {
                    if (l >= 0 && l <= k) continue;
                    var indices = new List<int>(4) { i };
                    if (j >= 0) indices.Add(j); if (k >= 0) indices.Add(k); if (l >= 0) indices.Add(l);
                    if (requiredRole.Length == 0 && indices.Count == 1 && rarity >= 0)
                    {
                        TownFighterCatalog.Entry? entry = TownFighterCatalog.Find(roster[i].Role);
                        int tierCeiling = division == Division.Easy ? 0 :
                            division == Division.Medium ? (rarity >= 2 ? 1 : 0) :
                            rarity == 3 ? 2 : rarity >= 1 ? 1 : 0;
                        if (entry == null || entry.Tier > tierCeiling) continue;
                    }
                    bool anchor = requiredRole.Length == 0, overwhelming = false;
                    for (int n = 0; n < indices.Count; ++n)
                    {
                        Fighter f = roster[indices[n]];
                        anchor = anchor || f.Role == requiredRole;
                        overwhelming = overwhelming || FighterPower(f.Ability, f.Mmr, influence) > strongest;
                    }
                    // Challenge bands already account for both teams and headcount;
                    // one stronger NPC can still be easy for two or three players.
                    if (!anchor || (division == Division.Easy && rarity < 0 && overwhelming)) continue;
                    var candidate = new Match();
                    candidate.ScoreA = playerScore;
                    candidate.ScoreB = Detail.Score(roster, indices, candidate.B, influence);
                    if (candidate.ScoreB <= 0) continue;
                    double rawRatio = candidate.ScoreB / playerScore;
                    double ratio = rawRatio /
                        (teams1v1 && indices.Count > 1 ? 1.15 : 1.0);
                    if (players.Count == 1 && indices.Count <= 2 &&
                        ratio < minimum - 1e-12 && ratio >= minimum * .80 - 1e-12)
                    {
                        candidate.Valid = true;
                        candidate.BestAvailable = true;
                        candidate.A = new List<string>(playerIds);
                        favorableByCount[indices.Count - 1].Consider(candidate,
                            minimum - ratio, Detail.Novelty(candidate.B, history, types),
                            Detail.Hash(seed, Detail.Key(candidate.B)));
                    }
                    if (rarity >= 2 && ratio < minimum - 1e-12)
                    {
                        bool stronger = !fallback.Valid || ratio > fallbackRatio + 1e-12;
                        bool tied = fallback.Valid && Math.Abs(ratio - fallbackRatio) <= 1e-12;
                        if (stronger || tied)
                        {
                            double novelty = Detail.Novelty(candidate.B, history, types);
                            uint tie = Detail.Hash(seed, Detail.Key(candidate.B));
                            if (stronger || novelty < fallbackNovelty ||
                                (novelty == fallbackNovelty && tie < fallbackTie))
                            {
                                candidate.Valid = true;
                                candidate.BestAvailable = true;
                                candidate.A = new List<string>(playerIds);
                                fallback = candidate.Clone();
                                fallbackRatio = ratio;
                                fallbackNovelty = novelty;
                                fallbackTie = tie;
                            }
                        }
                    }
                    if (ratio < minimum - 1e-12 || ratio > maximum + 1e-12) continue;
                    candidate.Valid = true; candidate.A = new List<string>(playerIds);
                    // Named Legendary duels are definitive: once the unique alone
                    // fits the band, no supporting lineup can supersede it.
                    if (requiredRole.Length != 0 && players.Count == 1 && indices.Count == 1) return candidate;
                    Detail.Ranked ranked = rarity < 0 ? best : byCount[indices.Count - 1];
                    ranked.Consider(candidate, Math.Abs(ratio - desired), Detail.Novelty(candidate.B, history, types), Detail.Hash(seed, Detail.Key(candidate.B)));
                }
            }
        }
        if (rarity < 0) return best.Match;
        for (int i = 0; i < 2; ++i)
            if (!byCount[i].Match.Valid && favorableByCount[i].Match.Valid)
                byCount[i] = favorableByCount[i].Clone();
        // Solo challenges favor smaller opposing lineups while retaining every
        // feasible format. Slightly favorable 1v1/1v2 candidates may fill a gap.
        int[] available = new int[4];
        int count = 0;
        if (players.Count == 1)
        {
            int total = 0;
            for (int i = 0; i < 4; ++i) if (byCount[i].Match.Valid) total += SizeWeights[i];
            if (total != 0)
            {
                int roll = (int)(Detail.Next(ref varietySeed) % (uint)total);
                for (int i = 0; i < 4; ++i) if (byCount[i].Match.Valid)
                {
                    if (roll < SizeWeights[i]) return byCount[i].Match;
                    roll -= SizeWeights[i];
                }
            }
        }
        for (int i = 0; i < 4; ++i) if (byCount[i].Match.Valid) available[count++] = i;
        return count != 0 ? byCount[available[Detail.Next(ref varietySeed) % (uint)count]].Match :
            fallback.Valid ? fallback : empty;
    }

    // ---- SelectAmbient --------------------------------------------------------------------------------------------------------

    /// <summary><paramref name="seed"/> is the saved ambient stream (C++ <c>unsigned&amp;</c>), advanced exactly once.</summary>
    // C++: TownMatchmakingPolicy.h:471-540
    public static Match SelectAmbient(List<Fighter> npcs, ref uint seed,
        List<List<string>> history, int maxTotal = 8)
    {
        // Exactly one saved-stream step per attempt; search uses a local stream so
        // capacity and roster size cannot change the next attempt's random state.
        uint attemptSeed = Detail.Next(ref seed);
        uint random = attemptSeed;
        int capacity = Math.Min(8, maxTotal);
        var empty = new Match();
        if (capacity < 2) return empty;
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        List<Fighter> roster = Detail.Roster(npcs, random, excluded, "");
        int size = roster.Count;
        if (size < 2) return empty;
        // Exactly one draw here, as before. With the showcase band disabled the
        // classification is identical to the shipped "% 5 == 0 -> underdog" and the
        // saved stream is untouched, so shipped matchups are bit-for-bit unchanged.
        uint categoryRoll = Detail.Next(ref random);
        // One read of the live lever set: the band shape must not change between the
        // draws, the classification and the cap.
        BalanceTuning.Values tuning = BalanceTuning.Get();
        int showcasePercent = tuning.ShowcasePercent;
        double closeCap = tuning.AmbientCloseCap;
        int category;
        if (showcasePercent > 0)
        {
            uint bucket = categoryRoll % 100u;
            category = bucket < unchecked((uint)showcasePercent) ? 2 :
                bucket < unchecked((uint)(showcasePercent + 20)) ? 1 : 0;
        }
        else
        {
            category = (categoryRoll % 5u) == 0 ? 1 : 0;
        }
        // base + rand * SPAN, with the span an explicit literal rather than
        // (cap - base): deriving it would move the drawn target by one ULP on a
        // third of all draws and break the bit-identical shipped baseline.
        double closeTarget = 1 + (Detail.Next(ref random) % 10000) * tuning.AmbientCloseSpan / 10000;
        double underdogTarget = closeCap + (Detail.Next(ref random) % 10000) * tuning.AmbientUnderdogSpan / 10000;
        // Drawn only when the band is enabled, so the stream does not shift for the
        // shipped configuration. The showcase band starts at the LIVE ambient cap, so
        // narrowing the ambient band widens showcase rather than leaving a gap
        // between them.
        double showcaseTarget = 0.0;
        if (showcasePercent > 0)
            showcaseTarget = tuning.AmbientRatioCap + (Detail.Next(ref random) % 10000) *
                (Detail.ShowcaseRatioHigh - tuning.AmbientRatioCap) / 10000;
        double ratioCap = showcasePercent > 0 ? Detail.ShowcaseRatioHigh : tuning.AmbientRatioCap;
        int attendance = (int)(2 + Detail.Next(ref random) % (uint)(Math.Min(capacity, size) - 1));
        SortedDictionary<string, string> types = Detail.Types(roster);
        var best = new[] { new Detail.Ranked(), new Detail.Ranked(), new Detail.Ranked() };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < size; ++i) for (int j = i + 1; j < size; ++j)
        {
            var a = new List<int> { i };
            var b = new List<int> { j };
            Detail.AmbientCandidate(roster, a, b, history, types, closeTarget, underdogTarget, showcaseTarget, closeCap, ratioCap, attendance, attemptSeed, seen, best);
        }
        // A fixed work budget; invalid size draws cost an attempt too.
        for (int attempt = 0; attempt < 4096 && capacity > 2 && size > 2; ++attempt)
        {
            int countA = (int)(1 + Detail.Next(ref random) % 4), countB = (int)(1 + Detail.Next(ref random) % 4);
            if (countA + countB > capacity || countA + countB > size || countA + countB == 2) continue;
            var available = new List<int>(size);
            for (int i = 0; i < size; ++i) available.Add(i);
            var a = new List<int>();
            var b = new List<int>();
            for (int i = 0; i < countA + countB; ++i)
            {
                int chosen = (int)((uint)i + Detail.Next(ref random) % (uint)(size - i));
                (available[i], available[chosen]) = (available[chosen], available[i]);
                (i < countA ? a : b).Add(available[i]);
            }
            Detail.AmbientCandidate(roster, a, b, history, types, closeTarget, underdogTarget, showcaseTarget, closeCap, ratioCap, attendance, attemptSeed, seen, best);
        }
        Match match = (best[category].Match.Valid ? best[category].Match
            : best[category == 0 ? 1 : 0].Match).Clone();
        if (match.Valid && (Detail.Next(ref random) & 1u) != 0) { (match.A, match.B) = (match.B, match.A); (match.ScoreA, match.ScoreB) = (match.ScoreB, match.ScoreA); }
        return match;
    }

    // ---- SelectAmbientDiagnostic -----------------------------------------------------------------------------------------------

    // C++: TownMatchmakingPolicy.h:634-663
    public static Match SelectAmbientDiagnostic(List<Fighter> npcs, ref uint seed, List<List<string>> history,
        in DiagnosticRequest request, int maxTotal = 8)
    {
        if (!request.Active) return SelectAmbient(npcs, ref seed, history, maxTotal);
        uint attemptSeed = Detail.Next(ref seed); uint random = attemptSeed;
        int capacity = Math.Min(8, maxTotal); var empty = new Match(); if (capacity < 2) return empty;
        var excluded = new HashSet<string>(StringComparer.Ordinal); List<Fighter> roster = Detail.Roster(npcs, random, excluded, "");
        int size = roster.Count; if (size < 2) return empty;
        int attendance = (int)(2 + Detail.Next(ref random) % (uint)(Math.Min(capacity, size) - 1));
        SortedDictionary<string, string> types = Detail.Types(roster);
        var best = new Detail.DiagnosticRanked(); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < size; ++i) for (int j = i + 1; j < size; ++j)
        {
            var a = new List<int> { i };
            var b = new List<int> { j };
            Detail.DiagnosticCandidate(roster, a, b, history, types, request, attendance, attemptSeed, seen, best);
        }
        for (int attempt = 0; attempt < 4096 && capacity > 2 && size > 2; ++attempt)
        {
            int countA = (int)(1 + Detail.Next(ref random) % 4), countB = (int)(1 + Detail.Next(ref random) % 4);
            if (countA + countB > capacity || countA + countB > size || countA + countB == 2) continue;
            var available = new List<int>(size); for (int i = 0; i < size; ++i) available.Add(i);
            var a = new List<int>();
            var b = new List<int>();
            for (int i = 0; i < countA + countB; ++i)
            {
                int chosen = (int)((uint)i + Detail.Next(ref random) % (uint)(size - i));
                (available[i], available[chosen]) = (available[chosen], available[i]);
                (i < countA ? a : b).Add(available[i]);
            }
            Detail.DiagnosticCandidate(roster, a, b, history, types, request, attendance, attemptSeed, seen, best);
        }
        Match match = best.Match.Clone();
        if (!match.Valid)
        {
            uint fallbackSeed = attemptSeed; match = SelectAmbient(npcs, ref fallbackSeed, history, maxTotal);
            if (!match.Valid) return match; match.Diagnostic = true; match.DiagnosticModel = request.Model; match.RequestedCoverage = request.Coverage;
            match.ActualCoverage = Detail.CoverageFor(match.A.Count, match.B.Count); match.Relaxation = 3;
            match.DiagnosticUnderdog = Detail.Max(match.ScoreA, match.ScoreB) / Detail.Min(match.ScoreA, match.ScoreB) > 1.15;
        }
        if ((Detail.Next(ref random) & 1u) != 0) { (match.A, match.B) = (match.B, match.A); (match.ScoreA, match.ScoreB) = (match.ScoreB, match.ScoreA); }
        Detail.FillModels(match, npcs, request.ReducedInfluence);
        match.ScoreA = match.ModelScoreA[request.Model]; match.ScoreB = match.ModelScoreB[request.Model];
        return match;
    }
}

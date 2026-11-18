// Port of upstream/ProvingGrounds/plugin/src/PGConfig.h and PGConfig.cpp (Proving Grounds by Shutori, GPLv3).
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace ProvingGrounds.Core;

// PGConfig.h - one home for the tunable economy and plugin settings.
//
// The structs below carry the SHIPPED values; pg_config.json, beside
// ProvingGrounds.dll, may override them. A missing file, key, or unusable value
// leaves the shipped default in force. Gameplay settings load at startup and
// require restart; crowd performance and F8 enablement apply on save.
//
// Two resolution times, because the readers differ:
//
//   - The catalogue prices (gear tiers, supplies, licences) are STAMPED into
//     ArenaRewards' tables once at load. Every reader -- the rewards screen, the
//     affordability gates, the ledger reservation, the half-price replace --
//     reads that field, so stamping is what makes one edit cover all of them.
//   - The computed prices (prisoner recruitment, the bookie stake range, the
//     challenge buy-in) are read through this module at use time instead, so
//     there is no second place a value could be scaled twice.
//
// marksMultiplier scales MARKS only: the catalogue, and the prisoner recruitment
// cost. The bookie stake and the challenge buy-in are paid in Cats, which this
// mod never adjusts.
//
// ADDING A SECTION. A new setting needs four things, all in PGConfig: a shipped
// default (a struct here, or the caller's own default passed in), a place in
// kSections and kKnown in Parse, a reader that validates it, and where the value
// becomes visible -- either an accessor read at use time, or an apply hook in the
// owning module. Only the apply hooks are one-shot: a section whose value is
// stamped into another module's table must not be applied twice, because the
// second apply sees the already-stamped value. BalanceTuning is the other home
// for tunables and is not read from this file; if a balance lever belongs here
// later, its struct is the template for the reader and the shipped default.

/// <summary>Result of <see cref="IConfigFiles.Read"/>. The C++ (<c>ReadText</c>, PGConfig.cpp:434-464) tells three states apart, and
/// the messages differ: a file that is absent gets the shipped defaults WRITTEN; a file that exists but cannot be opened or read is
/// left alone (PGConfig.cpp:845-846).</summary>
public enum ConfigReadStatus
{
    /// <summary><c>FileMissing</c>: the open failed and the file does not exist (GetFileAttributesW fails with ERROR_FILE_NOT_FOUND
    /// or ERROR_PATH_NOT_FOUND).</summary>
    Missing,
    /// <summary><c>FileRead</c>: the whole file was read; <see cref="ConfigRead.Bytes"/> holds its raw bytes.</summary>
    Read,
    /// <summary><c>FileUnreadable</c>: it exists (or the error is anything else) but could not be opened, sized or read.</summary>
    Unreadable,
}

/// <summary>A read: status plus the file's raw bytes (only for <see cref="ConfigReadStatus.Read"/>). Raw bytes, not text: the C++
/// reads with <c>"rb"</c> into a <c>std::string</c> and handles the UTF-8 BOM itself, and the settings save splices the bytes.</summary>
public readonly record struct ConfigRead(ConfigReadStatus Status, byte[]? Bytes);

/// <summary>Result of <see cref="IConfigFiles.Write"/>: <c>_wfopen_s(.., L"wb")</c> failed / the fwrite or fclose failed / both
/// succeeded (PGConfig.cpp:614-619, 1062-1066).</summary>
public enum ConfigWriteStatus { NotOpened, Incomplete, Written }

/// <summary>The file operations PGConfig.cpp does, so Core stays file-free; the glue implements it over the real disk. Core
/// sequences them exactly as the C++ does (the default file is written in place; the settings save writes <c>path + ".tmp"</c>,
/// moves it over the file, and deletes the temporary file when anything after its creation failed).</summary>
public interface IConfigFiles
{
    /// <summary>Read the whole file as bytes. Missing vs Unreadable as in <see cref="ConfigReadStatus"/>. The 1 MiB guard of the C++
    /// (PGConfig.cpp:452-453) is applied by Core to the bytes returned; the glue may also refuse a larger file as Unreadable before
    /// reading it, which gives the same result.</summary>
    ConfigRead Read(string path);
    /// <summary>Create or truncate <paramref name="path"/> and write <paramref name="bytes"/> (<c>_wfopen_s "wb"</c>, fwrite, fclose).</summary>
    ConfigWriteStatus Write(string path, byte[] bytes);
    /// <summary><c>MoveFileExW(source, destination, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)</c>; true on success.</summary>
    bool MoveReplace(string source, string destination);
    /// <summary><c>DeleteFileW(path)</c>; the result is ignored, as in the C++.</summary>
    void Delete(string path);
}

/// <summary>The two PGLog channels PGConfig.cpp writes (<c>PGLog::Debug</c>, <c>PGLog::Error</c>), message text only (PGLog adds the
/// timestamp, session and level). Task 1.12 routes these to the ported PGLog sink.</summary>
public interface IConfigLog
{
    void Debug(string message);
    void Error(string message);
}

/// <summary>The original's pg_config.json values, reader, default-file writer and in-game settings overlay (PGConfig.h, PGConfig.cpp).</summary>
public static partial class PGConfig
{
    // Wagers move in whole tens of Cats and the bookie's buttons step by
    // 10/100/1000, so this stays compiled rather than configurable: the range
    // is the lever, the increment is the control.
    // C++: PGConfig.h:38-41
    public const int StakeStep = 10;

    // Prisoner recruitment cost model (PrisonerRecruitmentLogic::RequiredMarks).
    // minimum/maximum bound the stat-driven price and step rounds it up; the
    // marks multiplier is applied to that bounded result, so a multiplier of 2
    // doubles every prisoner's price, not just the cheap ones. Shipped
    // 25 / 250 / 5 / 2.5 -- the "soft bump vs armoury earn inflation: cheaper
    // than a full 6x, not free" balance.
    /// <summary>C++: PGConfig.h:43-55 (copied by value: <c>g_recruitment = candidate</c>, PGConfig.cpp:218).</summary>
    public struct Recruitment
    {
        public int Minimum;
        public int Maximum;
        public int Step;
        public double CombatScale;
    }

    // Bookie wager range, in Cats. Shipped 10 / 10000. Both are normalised to
    // whole kStakeStep multiples at load, so a configured ceiling the wager
    // validators would reject cannot be reached.
    /// <summary>C++: PGConfig.h:57-64 (copied by value).</summary>
    public struct Bookie
    {
        public int Minimum;
        public int Maximum;
    }

    /// <summary>C++: PGConfig.h:66 <c>enum ChallengeDifficulty { ChallengeEasy, ChallengeNormal, ChallengeHard };</c></summary>
    public enum ChallengeDifficulty { Easy = 0, Normal = 1, Hard = 2 }

    /// <summary>C++: PGConfig.h:67-77 (copied by value: <c>g_challenges = candidate</c>, PGConfig.cpp:282).</summary>
    public struct Challenges
    {
        public ChallengeDifficulty Difficulty;
        public int RefreshHours;
        public int RefreshBaseCostCats;
        public double RefreshCostMultiplier;
        public int CooldownHours;
        public double WinMarksMultiplier;
        public double UniqueBonusMultiplier;
        public double SkarnBonusMultiplier;
    }

    /// <summary>The C++ <c>int challengeBase[3]</c> inside <see cref="EditableSettings"/>: a fixed array copied with the struct (an
    /// <c>int[]</c> field would alias between copies).</summary>
    [InlineArray(3)]
    public struct ChallengeBases
    {
        int _element0;
    }

    // Settings edited by the in-game Mods page. Shop catalogue prices and the
    // Marks price multiplier deliberately remain in the hand-edited JSON only.
    /// <summary>C++: PGConfig.h:79-90 (returned and passed by value).</summary>
    public struct EditableSettings
    {
        public Recruitment Recruitment;
        public Bookie Bookie;
        public Challenges Challenges;
        /// <summary>Cats, indexed by division: 0 Easy, 1 Medium, 2 Hard.</summary>
        public ChallengeBases ChallengeBase;
        public ArenaPerformance.Profile PerformanceProfile;
        // true lets F8 toggle diagnostics; false makes F8 do nothing.
        public bool F8OpensDebugMenu;
    }

    // ---- PGConfig.cpp anonymous namespace: constants ----------------------------------------------------------------------------

    // C++: PGConfig.cpp:19-20 (kFileName, kFileNameW)
    const string FileName = "pg_config.json";
    // Resource guards, not balance limits: a price outside these ranges is a
    // stray digit rather than a deliberate setting.
    // C++: PGConfig.cpp:23-25
    const int MaxCost = 10000000;
    const int MaxEditableCost = 1000000;
    const double MaxScale = 100.0;
    // The shipped bookie-credit bound, and the odds ceiling the book is
    // clamped to in TownBettingPolicy::ReturnCats.
    // C++: PGConfig.cpp:28-29
    const int ShippedBookieCreditCeiling = 50000;
    const double BookOddsCeiling = 5.0;
    // Grade keys, indexed by ArenaRewards::Tier. The catalogue spells its
    // grades "Standard"/"High Grade"; these are the file's spelling.
    // C++: PGConfig.cpp:32-33
    static readonly string[] kTierNames = { "standard", "high", "specialist", "masterwork" };
    const string TierList = "standard, high, specialist or masterwork";

    // The shipped prices, each in one place. The globals below are built from
    // them, and ResetToShipped restores them.
    // C++: PGConfig.cpp:37-41
    static Recruitment ShippedRecruitment()
    {
        return new Recruitment { Minimum = 25, Maximum = 250, Step = 5, CombatScale = 2.5 };
    }

    // C++: PGConfig.cpp:42-46
    static Bookie ShippedBookie()
    {
        return new Bookie { Minimum = 10, Maximum = 10000 };
    }

    // C++: PGConfig.cpp:47-51
    static Challenges ShippedChallenges()
    {
        // Challenges shipped = { ChallengeNormal, 12, 500, 2.0, 24, 1.0, 1.0, 1.0 };
        return new Challenges
        {
            Difficulty = ChallengeDifficulty.Normal,
            RefreshHours = 12,
            RefreshBaseCostCats = 500,
            RefreshCostMultiplier = 2.0,
            CooldownHours = 24,
            WinMarksMultiplier = 1.0,
            UniqueBonusMultiplier = 1.0,
            SkarnBonusMultiplier = 1.0,
        };
    }

    // C++: PGConfig.cpp:52-55
    static int ShippedChallengeBase(int division)
    {
        return division == 0 ? 500 : division == 1 ? 1000 : 2500;
    }

    // ---- the globals ----------------------------------------------------------------------------------------------------------

    /// <summary>Every global of PGConfig.cpp (anonymous namespace, PGConfig.cpp:59-79), gathered in one object so a test can swap
    /// all of them at once. Load writes into <see cref="Current"/>. Namespace scope in the C++, "so nothing is constructed on first
    /// use: Load runs on the main thread before any hook is installed, and a worker thread may read a value afterwards"
    /// (PGConfig.cpp:59-61): a load (and a settings save) must stay on the main/sim thread like the original.</summary>
    internal sealed class State
    {
        // C++: PGConfig.cpp:57 `typedef std::map<std::string, int> Costs;` -> ordinal SortedDictionary (std::string operator<
        // compares bytes). Keys are "gear.<piece>.<tier>", "supplies.<id>", "licences.<id>"; empty when shipped (no pg_config.json).
        // C++: PGConfig.cpp:62-64
        public SortedDictionary<string, int> Gear = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public SortedDictionary<string, int> Supplies = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public SortedDictionary<string, int> Licences = new SortedDictionary<string, int>(StringComparer.Ordinal);
        // C++: PGConfig.cpp:65 `double g_multiplier = 1.0;` ("marks_multiplier")
        public double Multiplier = 1.0;
        // C++: PGConfig.cpp:66 `Recruitment g_recruitment = ShippedRecruitment();`
        public Recruitment Recruitment;
        // C++: PGConfig.cpp:67 `Bookie g_bookie = ShippedBookie();`
        public Bookie Bookie;
        // C++: PGConfig.cpp:68 `Challenges g_challenges = ShippedChallenges();`
        public Challenges Challenges;
        // C++: PGConfig.cpp:69 `ArenaPerformance::Profile g_performanceProfile = ArenaPerformance::Normal;`
        public ArenaPerformance.Profile PerformanceProfile = ArenaPerformance.Profile.Normal;
        // C++: PGConfig.cpp:70 `bool g_f8OpensDebugMenu = false;`
        public bool F8OpensDebugMenu;
        // C++: PGConfig.cpp:71-74 `int g_challengeBase[3]`
        public int[] ChallengeBase = { ShippedChallengeBase(0), ShippedChallengeBase(1), ShippedChallengeBase(2) };
        // C++: PGConfig.cpp:75-76
        public bool Loaded;
        public int Rejected;
        // C++: PGConfig.cpp:77-79
        public int AppliedGear;
        public int AppliedSupplies;
        public int AppliedLicences;
        // port (task 2.6): whether Load read the file's ui_settings (no C++ global; Load's locals `state` and Parse's result).
        // Missing until Load reads; an unresolved directory counts as Unreadable (no file was read).
        public ConfigReadStatus FileStatus = ConfigReadStatus.Missing;
        public bool FileParsed;

        /// <summary>A fresh State holding the shipped values (the original with no pg_config.json, before Load).</summary>
        public static State Shipped() => new State
        {
            Recruitment = ShippedRecruitment(),
            Bookie = ShippedBookie(),
            Challenges = ShippedChallenges(),
        };

        /// <summary>A deep copy (the maps and the challenge-base array are copied; every other field is a value type).</summary>
        public State Clone() => new State
        {
            Gear = new SortedDictionary<string, int>(Gear, StringComparer.Ordinal),
            Supplies = new SortedDictionary<string, int>(Supplies, StringComparer.Ordinal),
            Licences = new SortedDictionary<string, int>(Licences, StringComparer.Ordinal),
            Multiplier = Multiplier,
            Recruitment = Recruitment,
            Bookie = Bookie,
            Challenges = Challenges,
            PerformanceProfile = PerformanceProfile,
            F8OpensDebugMenu = F8OpensDebugMenu,
            ChallengeBase = (int[])ChallengeBase.Clone(),
            Loaded = Loaded,
            Rejected = Rejected,
            AppliedGear = AppliedGear,
            AppliedSupplies = AppliedSupplies,
            AppliedLicences = AppliedLicences,
            FileStatus = FileStatus,
            FileParsed = FileParsed,
        };
    }

    // The real globals (shipped values until Load runs).
    static readonly State g_state = State.Shipped();

    // Test seam (no C++ counterpart; mirrors golden/stubs/pgconfig_stub.cpp): an override visible only to the calling flow, so
    // parallel tests never see each other's values.
    static readonly AsyncLocal<State?> s_test = new AsyncLocal<State?>();

    /// <summary>The state every accessor reads: the test override in this flow, else the real globals.</summary>
    internal static State Current => s_test.Value ?? g_state;

    /// <summary>True while a test override is active in this flow (production never sets one).</summary>
    internal static bool HasTestOverride => s_test.Value != null;

    // ---- PGConfig.cpp anonymous namespace: the reader ---------------------------------------------------------------------------

    static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    // `std::string(x.GetString())` and `strcmp(x.GetString(), ..)`: rapidjson hands out a NUL-terminated const char*, so a string
    // holding \u0000 is cut there wherever the C++ goes through GetString() (HasMember/operator[] compare the full length instead).
    static string CStr(string value)
    {
        int nul = value.IndexOf('\0');
        return nul < 0 ? value : value.Substring(0, nul);
    }

    /// <summary>One parse's context: the globals it writes and the log it reports to (the C++ uses PGLog directly).</summary>
    sealed class Ctx
    {
        public readonly State G;
        readonly IConfigLog? _log;
        public Ctx(State g, IConfigLog? log) { G = g; _log = log; }
        public void Debug(string message) => _log?.Debug(message);
        public void Error(string message) => _log?.Error(message);
    }

    // C++: PGConfig.cpp:81-86
    static void Reject(Ctx c, string where, string reason)
    {
        ++c.G.Rejected;
        c.Error("Proving Grounds: " + FileName + " " + where +
            " ignored (" + reason + "); the shipped value stays in force");
    }

    // C++: PGConfig.cpp:88-94
    static int GradeIndex(string name)
    {
        for (int grade = 0; grade < 4; ++grade)
            if (name == kTierNames[grade])
                return grade;
        return -1;
    }

    // 1 when the value is usable, -1 when it is not (already logged).
    // C++: PGConfig.cpp:96-116
    static int ReadWhole(Ctx c, Rj value, string where, int minimum, int maximum, out int @out)
    {
        @out = 0;
        if (!value.IsInt)
        {
            Reject(c, where, value.IsNumber ?
                "expected a whole number" : "expected a number");
            return -1;
        }
        int number = value.GetInt();
        if (number < minimum || number > maximum)
        {
            Reject(c, where, RangeWhole(minimum, maximum));
            return -1;
        }
        @out = number;
        return 1;
    }

    // C++: PGConfig.cpp:109-110 (char range[80]; sprintf_s "expected %d to %d")
    static string RangeWhole(int minimum, int maximum) => Inv($"expected {minimum} to {maximum}");

    // C++: PGConfig.cpp:143-144 (char range[96]; sprintf_s "expected %g to %g")
    static string RangeNumber(double minimum, double maximum) =>
        "expected " + CMath.FormatG(minimum) + " to " + CMath.FormatG(maximum);

    // C++: PGConfig.cpp:118-126
    static void OptionalWhole(Ctx c, Rj @object, string key, string path, int minimum, int maximum, ref int target)
    {
        if (!@object.TryMember(key, out Rj member))
            return;
        if (ReadWhole(c, member, path + "." + key, minimum, maximum, out int value) == 1)
            target = value;
    }

    // C++: PGConfig.cpp:128-149
    static void OptionalNumber(Ctx c, Rj @object, string key, string path, double minimum, double maximum, ref double target)
    {
        if (!@object.TryMember(key, out Rj value))
            return;
        string where = path + "." + key;
        if (!value.IsNumber)
        {
            Reject(c, where, "expected a number");
            return;
        }
        double number = value.GetDouble();
        if (!double.IsFinite(number) || number < minimum || number > maximum)
        {
            Reject(c, where, RangeNumber(minimum, maximum));
            return;
        }
        target = number;
    }

    // C++: PGConfig.cpp:151-187
    static void ReadGear(Ctx c, Rj gear, string sectionName, string? expectedPrefix)
    {
        foreach (var (pieceName, pieceValue) in gear.Members())
        {
            string id = CStr(pieceName);
            string itemPath = sectionName + "." + id;
            if (expectedPrefix != null && !id.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                Reject(c, itemPath, "expected an item ID beginning with " + expectedPrefix);
                continue;
            }
            if (!pieceValue.IsObject)
            {
                Reject(c, itemPath, "expected an object of grade prices");
                continue;
            }
            foreach (var (gradeName, gradeValue) in pieceValue.Members())
            {
                string name = CStr(gradeName);
                string where = itemPath + "." + name;
                if (GradeIndex(name) < 0)
                {
                    Reject(c, where, "unknown grade; use " + TierList);
                    continue;
                }
                if (ReadWhole(c, gradeValue, where, 0, MaxCost, out int cost) != 1)
                    continue;
                string canonicalKey = "gear." + id + "." + name;
                if (!c.G.Gear.TryAdd(canonicalKey, cost))
                    Reject(c, where, "duplicate entry");
            }
        }
    }

    // Flat "id": cost sections (supplies, licences).
    // C++: PGConfig.cpp:189-203
    static void ReadFlat(Ctx c, Rj section, string prefix, SortedDictionary<string, int> target)
    {
        foreach (var (entryName, entryValue) in section.Members())
        {
            string where = prefix + "." + CStr(entryName);
            if (ReadWhole(c, entryValue, where, 0, MaxCost, out int cost) != 1)
                continue;
            if (!target.TryAdd(where, cost))
                Reject(c, where, "duplicate entry");
        }
    }

    // C++: PGConfig.cpp:205-219
    static void ReadRecruitment(Ctx c, Rj section)
    {
        Recruitment candidate = c.G.Recruitment;
        OptionalWhole(c, section, "minimum", "recruitment", 0, MaxEditableCost, ref candidate.Minimum);
        OptionalWhole(c, section, "maximum", "recruitment", 0, MaxEditableCost, ref candidate.Maximum);
        OptionalWhole(c, section, "step", "recruitment", 1, MaxEditableCost, ref candidate.Step);
        OptionalNumber(c, section, "combat_scale", "recruitment", 0.0, MaxScale,
            ref candidate.CombatScale);
        if (candidate.Minimum > candidate.Maximum)
        {
            Reject(c, "recruitment", "minimum exceeds maximum; the shipped range is kept");
            return;
        }
        c.G.Recruitment = candidate;
    }

    // C++: PGConfig.cpp:221-237
    static void ReadBookie(Ctx c, Rj section)
    {
        Bookie candidate = c.G.Bookie;
        OptionalWhole(c, section, "minimum", "bookie", 0, MaxEditableCost, ref candidate.Minimum);
        OptionalWhole(c, section, "maximum", "bookie", 0, MaxEditableCost, ref candidate.Maximum);
        // Wagers move in whole steps, so the reachable range is the step
        // multiples inside the configured one. Normalising here means the
        // stake validators never see a bound they would reject.
        candidate.Minimum = ((candidate.Minimum + StakeStep - 1) / StakeStep) * StakeStep;
        candidate.Maximum = (candidate.Maximum / StakeStep) * StakeStep;
        if (candidate.Minimum > candidate.Maximum)
        {
            Reject(c, "bookie", "minimum exceeds maximum; the shipped range is kept");
            return;
        }
        c.G.Bookie = candidate;
    }

    // C++: PGConfig.cpp:239-255
    static void ReadChallenge(Ctx c, Rj section)
    {
        string[] names = { "easy", "medium", "hard" };
        for (int division = 0; division < 3; ++division)
        {
            int @base = c.G.ChallengeBase[division];
            OptionalWhole(c, section, names[division], "challenge_buy_in", 0, MaxEditableCost, ref @base);
            c.G.ChallengeBase[division] = @base;
            // The quote scales its base by 0.5-2.0 and the wallet rejects a
            // buy-in outside 100-5000, so a base beyond this band prices
            // challenges nobody can accept.
            if (@base < 200 || @base > 2500)
                c.Error("Proving Grounds: " + FileName +
                    " challenge_buy_in." + names[division] + " is outside 200 to 2500;" +
                    " quotes built from it may be rejected as invalid");
        }
    }

    // C++: PGConfig.cpp:257-283
    static void ReadChallengeSettings(Ctx c, Rj section)
    {
        Challenges candidate = c.G.Challenges;
        if (section.TryMember("difficulty", out Rj value))
        {
            if (!value.IsString) Reject(c, "challenges.difficulty", "expected easy, normal or hard");
            else if (value.GetString() == "easy") candidate.Difficulty = ChallengeDifficulty.Easy;
            else if (value.GetString() == "normal") candidate.Difficulty = ChallengeDifficulty.Normal;
            else if (value.GetString() == "hard") candidate.Difficulty = ChallengeDifficulty.Hard;
            else Reject(c, "challenges.difficulty", "expected easy, normal or hard");
        }
        OptionalWhole(c, section, "refresh_hours", "challenges", 1, 168, ref candidate.RefreshHours);
        OptionalWhole(c, section, "refresh_base_cost_cats", "challenges", 1, 1000000, ref candidate.RefreshBaseCostCats);
        OptionalNumber(c, section, "refresh_cost_multiplier", "challenges", 1.0, 10.0, ref candidate.RefreshCostMultiplier);
        OptionalWhole(c, section, "cooldown_hours", "challenges", 0, 720, ref candidate.CooldownHours);
        bool hasMarks = section.TryMember("marks", out Rj marks);
        if (hasMarks && !marks.IsObject)
            Reject(c, "challenges.marks", "expected an object");
        else if (hasMarks)
        {
            OptionalNumber(c, marks, "win_multiplier", "challenges.marks", 0.0, MaxScale, ref candidate.WinMarksMultiplier);
            OptionalNumber(c, marks, "unique_bonus_multiplier", "challenges.marks", 0.0, MaxScale, ref candidate.UniqueBonusMultiplier);
            OptionalNumber(c, marks, "skarn_bonus_multiplier", "challenges.marks", 0.0, MaxScale, ref candidate.SkarnBonusMultiplier);
        }
        c.G.Challenges = candidate;
    }

    // C++: PGConfig.cpp:285-322
    static void ReadEditableOverlay(Ctx c, Rj section)
    {
        if (section.TryMember("recruitment", out Rj recruitment))
        {
            if (recruitment.IsObject) ReadRecruitment(c, recruitment);
            else Reject(c, "ui_settings.recruitment", "expected an object");
        }
        if (section.TryMember("bookie", out Rj bookie))
        {
            if (bookie.IsObject) ReadBookie(c, bookie);
            else Reject(c, "ui_settings.bookie", "expected an object");
        }
        if (section.TryMember("challenge_buy_in", out Rj buyIn))
        {
            if (buyIn.IsObject) ReadChallenge(c, buyIn);
            else Reject(c, "ui_settings.challenge_buy_in", "expected an object");
        }
        if (section.TryMember("challenges", out Rj challenges))
        {
            if (challenges.IsObject) ReadChallengeSettings(c, challenges);
            else Reject(c, "ui_settings.challenges", "expected an object");
        }
        if (section.TryMember("performance_profile", out Rj value))
        {
            ArenaPerformance.Profile profile = c.G.PerformanceProfile;
            if (!value.IsString) Reject(c, "ui_settings.performance_profile", "expected high, normal or potato");
            else if (!ArenaPerformance.Parse(value.GetString(), ref profile))
                Reject(c, "ui_settings.performance_profile", "expected high, normal or potato");
            else c.G.PerformanceProfile = profile;
        }
        if (section.TryMember("f8_opens_debug_menu", out Rj f8))
        {
            if (!f8.IsBool) Reject(c, "ui_settings.f8_opens_debug_menu", "expected true or false");
            else c.G.F8OpensDebugMenu = f8.GetBool();
        }
    }

    // C++: PGConfig.cpp:324-337
    static void ResetToShipped(State g)
    {
        g.Gear.Clear();
        g.Supplies.Clear();
        g.Licences.Clear();
        g.Multiplier = 1.0;
        g.Recruitment = ShippedRecruitment();
        g.Bookie = ShippedBookie();
        g.Challenges = ShippedChallenges();
        g.PerformanceProfile = ArenaPerformance.Profile.Normal;
        g.F8OpensDebugMenu = false;
        for (int division = 0; division < 3; ++division)
            g.ChallengeBase[division] = ShippedChallengeBase(division);
    }

    static readonly string[] kSections =
    {
        "armour", "weapons", "gear", "supplies", "licences", "recruitment", "bookie", "challenge_buy_in", "challenges", "ui_settings"
    };
    static readonly string[] kKnown =
    {
        "version", "marks_multiplier", "armour", "weapons", "gear", "supplies", "licences",
        "recruitment", "bookie", "challenge_buy_in", "challenges", "ui_settings"
    };

    // C++: PGConfig.cpp:339-432
    static bool Parse(Ctx c, byte[] text)
    {
        // A hand-edited file may carry annotations the JSON spec has no room
        // for; the generated file uses neither, so a file that does is still
        // read rather than rejected on punctuation.
        // A file saved from an editor may carry a UTF-8 BOM, which the reader
        // would otherwise treat as the start of the document.
        int start = HasBom(text) ? 3 : 0;
        using RjDocument? document = RjDocument.Parse(text, start, out RjParseError error);
        if (document == null)
        {
            c.Error(Inv($"Proving Grounds: {FileName} is not valid JSON at offset {error.Offset} ({error.Message}); it is left untouched and the shipped prices stay in force"));
            ResetToShipped(c.G);
            return false;
        }
        Rj root = document.Root;
        if (!root.IsObject)
        {
            Reject(c, "$", "expected an object");
            ResetToShipped(c.G);
            return false;
        }
        // A file stamped by another schema may mean something else by these
        // names, so nothing is taken from it.
        if (root.TryMember("version", out Rj version))
        {
            if (!version.IsInt || version.GetInt() != 1)
            {
                Reject(c, "version", "expected 1; no prices were loaded");
                ResetToShipped(c.G);
                return false;
            }
        }

        foreach (var (memberName, _) in root.Members())
        {
            string key = CStr(memberName);
            bool known = false;
            for (int i = 0; i < kKnown.Length; ++i)
                if (key == kKnown[i])
                    known = true;
            if (!known)
                Reject(c, key, "unknown section");
        }
        for (int i = 0; i < kSections.Length; ++i)
            if (root.TryMember(kSections[i], out Rj section) && !section.IsObject)
                Reject(c, kSections[i], "expected an object");

        OptionalNumber(c, root, "marks_multiplier", "$", 0.0, MaxScale, ref c.G.Multiplier);

        // The split sections take precedence when a config contains both
        // formats. Legacy mixed gear remains readable for existing installs.
        if (root.TryMember("armour", out Rj armour) && armour.IsObject)
            ReadGear(c, armour, "armour", "armour.");
        if (root.TryMember("weapons", out Rj weapons) && weapons.IsObject)
            ReadGear(c, weapons, "weapons", "weapon.");
        if (root.TryMember("gear", out Rj gear) && gear.IsObject)
            ReadGear(c, gear, "gear", null);
        if (root.TryMember("supplies", out Rj supplies) && supplies.IsObject)
            ReadFlat(c, supplies, "supplies", c.G.Supplies);
        if (root.TryMember("licences", out Rj licences) && licences.IsObject)
            ReadFlat(c, licences, "licences", c.G.Licences);
        if (root.TryMember("recruitment", out Rj recruitment) && recruitment.IsObject)
            ReadRecruitment(c, recruitment);
        if (root.TryMember("bookie", out Rj bookie) && bookie.IsObject)
            ReadBookie(c, bookie);
        if (root.TryMember("challenge_buy_in", out Rj buyIn) && buyIn.IsObject)
            ReadChallenge(c, buyIn);
        if (root.TryMember("challenges", out Rj challenges) && challenges.IsObject)
            ReadChallengeSettings(c, challenges);
        if (root.TryMember("ui_settings", out Rj ui) && ui.IsObject)
            ReadEditableOverlay(c, ui);
        return true;
    }

    // `text.size() >= 3 && text[0..2] == EF BB BF` (PGConfig.cpp:347-350, 746-749, 1030-1033)
    static bool HasBom(byte[] text) => text.Length >= 3 && text[0] == 0xEF && text[1] == 0xBB && text[2] == 0xBF;

    // C++: PGConfig.cpp:434-464 (FileState ReadText). The open/attribute/read steps are the glue's (IConfigFiles.Read); the
    // resource guard stays here.
    static ConfigReadStatus ReadText(IConfigFiles files, string path, out byte[] text)
    {
        text = Array.Empty<byte>();
        ConfigRead read = files.Read(path);
        if (read.Status != ConfigReadStatus.Read)
            return read.Status;
        byte[] bytes = read.Bytes ?? Array.Empty<byte>();
        // The arena snapshot reader's resource guard, for the same reason: a
        // hand-edited file must not be allocated whole before it is bounded.
        if ((ulong)bytes.LongLength > 1024u * 1024u)
            return ConfigReadStatus.Unreadable;
        text = bytes;
        return ConfigReadStatus.Read;
    }

    // C++: PGConfig.cpp:466-473 (sprintf_s "%g" into char[32], then strcat ".0")
    static string FormatNumber(double value)
    {
        string @out = CMath.FormatG(value);
        // A whole number would print as "1", which reads as an integer in a
        // file where the user is about to type fractional values.
        if (@out.IndexOf('.') < 0 && @out.IndexOf('e') < 0)
            @out += ".0";
        return @out;
    }

    // C++: PGLog.cpp:55-64 (PGLog::Quote, used by WriteDefaults; task 1.12 ports PGLog itself). Bytes >= 0x80 pass through; the
    // catalogue's stable ids are ASCII.
    static string Quote(string value)
    {
        var @out = new StringBuilder("\"");
        foreach (char c in value)
        {
            if (c == '"' || c == '\\') { @out.Append('\\'); @out.Append(c); }
            else if (c < 32) @out.Append(Inv($"\\u{(int)c:x4}"));
            else @out.Append(c);
        }
        return @out.Append('"').ToString();
    }

    // C++: PGConfig.cpp:475-512
    static void AppendGearSection(StringBuilder @out, string sectionName, bool weapons)
    {
        @out.Append("    \"");
        @out.Append(sectionName);
        @out.Append("\": {\n");
        int itemCount = 0;
        for (int index = 0; index < (int)ArenaRewards.Piece.Count; ++index)
        {
            var piece = (ArenaRewards.Piece)index;
            if (ArenaRewards.IsWeapon(piece) == weapons && ArenaRewards.TiersFor(piece) > 0)
                ++itemCount;
        }
        int emitted = 0;
        for (int index = 0; index < (int)ArenaRewards.Piece.Count; ++index)
        {
            var piece = (ArenaRewards.Piece)index;
            int tiers = ArenaRewards.TiersFor(piece);
            if (tiers <= 0 || ArenaRewards.IsWeapon(piece) != weapons)
                continue;
            ++emitted;
            int first = (int)ArenaRewards.FirstTier(piece);
            @out.Append("        ");
            @out.Append(Quote(ArenaRewards.StableId(piece)));
            @out.Append(": {\n");
            for (int offset = 0; offset < tiers; ++offset)
            {
                int tier = first + offset;
                @out.Append(Inv($"            \"{kTierNames[tier]}\": {ArenaRewards.Get(piece, (ArenaRewards.Tier)tier).Cost}{(offset + 1 < tiers ? "," : "")}\n"));
            }
            @out.Append(emitted < itemCount ? "        },\n" : "        }\n");
        }
        @out.Append("    },\n");
    }

    // The shipped settings, as the file a user edits. Written only when no
    // file exists, so an edited file survives a rebuild. The parser accepts
    // comments to keep the generated settings self-documenting.
    // C++: PGConfig.cpp:517-612 (the text; LF line endings, as the C++ writes "\n" in binary mode)
    static string DefaultsText(State g)
    {
        var @out = new StringBuilder();
        @out.Append("{\n    \"version\": 1,\n");
        @out.Append("    // Prices and Marks costs\n");
        @out.Append("    \"marks_multiplier\": " + FormatNumber(g.Multiplier) + ",\n");

        AppendGearSection(@out, "armour", false);
        AppendGearSection(@out, "weapons", true);

        @out.Append("    \"supplies\": {\n");
        for (int item = 0; item < (int)ArenaRewards.GeneralItem.Count; ++item)
        {
            ArenaRewards.GeneralReward reward = ArenaRewards.GetGeneral((ArenaRewards.GeneralItem)item);
            @out.Append(Inv($"        {Quote(reward.StableId)}: {reward.Cost}{(item + 1 < (int)ArenaRewards.GeneralItem.Count ? "," : "")}\n"));
        }
        @out.Append("    },\n");

        ArenaRewards.ArmourSet[] kSets = { ArenaRewards.ArmourSet.T1, ArenaRewards.ArmourSet.T2, ArenaRewards.ArmourSet.T3 };
        @out.Append("    \"licences\": {\n");
        for (int set = 0; set < 3; ++set)
        {
            ArenaRewards.Licence licence = ArenaRewards.GetLicence(kSets[set]);
            @out.Append(Inv($"        {Quote(licence.StableId)}: {licence.Cost},\n"));
        }
        ArenaRewards.Licence weapons = ArenaRewards.GetWeaponLicence();
        @out.Append(Inv($"        {Quote(weapons.StableId)}: {weapons.Cost}\n"));
        @out.Append("    },\n");

        @out.Append(Inv($"    \"recruitment\": {{\n        \"minimum\": {g.Recruitment.Minimum},\n        \"maximum\": {g.Recruitment.Maximum},\n        \"step\": {g.Recruitment.Step},\n        \"combat_scale\": {FormatNumber(g.Recruitment.CombatScale)}\n    }},\n"));
        @out.Append(Inv($"    \"bookie\": {{\n        \"minimum\": {g.Bookie.Minimum},\n        \"maximum\": {g.Bookie.Maximum}\n    }},\n"));
        @out.Append(Inv($"    // Cats paid to book a challenge\n    \"challenge_buy_in\": {{\n        \"easy\": {g.ChallengeBase[0]},\n        \"medium\": {g.ChallengeBase[1]},\n        \"hard\": {g.ChallengeBase[2]}\n    }},\n"));
        @out.Append("    // Matchmaking and rewards for player challenges\n" +
            "    \"challenges\": {\n" +
            "        // easy = weaker opponents, normal = shipped balance, hard = stronger opponents\n" +
            "        \"difficulty\": \"normal\",\n" +
            "        // Challenge offer refresh interval in in-game hours; 12 keeps the shipped 06:00 / 18:00 schedule\n" +
            "        \"refresh_hours\": 12,\n" +
            "        // First paid refresh costs this many Cats (1-1,000,000)\n" +
            "        \"refresh_base_cost_cats\": 500,\n" +
            "        // Price multiplier per paid refresh (1.0-10.0); whole-Cat prices round to nearest; 2.0 doubles\n" +
            "        \"refresh_cost_multiplier\": 2.0,\n" +
            "        // In-game hours after a Skarn challenge attempt before another Skarn challenge; 0 disables the cooldown\n" +
            "        \"cooldown_hours\": 24,\n" +
            "        \"marks\": {\n" +
            "            // Multipliers: 1.0 keeps the shipped reward\n" +
            "            \"win_multiplier\": 1.0,\n" +
            "            // Unique-fighter bonus: Ressa Vane 20, Bum-Per 40, Senn / Torka / Veyr 50 Marks\n" +
            "            \"unique_bonus_multiplier\": 1.0,\n" +
            "            // Skarn victory bonus: 100 Marks\n" +
            "            \"skarn_bonus_multiplier\": 1.0\n" +
            "        }\n" +
            "    },\n");
        @out.Append("    // Arena performance and plugin UI\n" +
            "    \"ui_settings\": {\n" +
            "        \"performance_profile\": \"normal\",\n" +
            "        // true lets F8 toggle the debug menu; false disables the F8 action\n" +
            "        \"f8_opens_debug_menu\": false\n" +
            "    }\n}\n");
        return @out.ToString();
    }

    // C++: PGConfig.cpp:517-620 (the file write is :614-619)
    static bool WriteDefaults(State g, IConfigFiles files, string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(DefaultsText(g));
        return files.Write(path, bytes) == ConfigWriteStatus.Written;
    }

    // Every key the catalogue did not claim names something this build does
    // not sell: a mistyped stable ID, or a grade the piece does not have.
    // C++: PGConfig.cpp:622-631
    static void ReportLeftovers(Ctx c)
    {
        SortedDictionary<string, int>[] sections = { c.G.Gear, c.G.Supplies, c.G.Licences };
        for (int i = 0; i < 3; ++i)
            foreach (var entry in sections[i])
                Reject(c, entry.Key, "does not name an entry in the shipped catalogue");
    }

    // PGConfig.cpp:633-648 Narrow(path): the wide path narrowed to the active code page for the log line only; the port's path
    // is already a .NET string and is logged as it is.

    // C++: PGConfig.cpp:650-660
    static bool SkipString(byte[] text, ref long pos)
    {
        if (pos >= text.Length || text[pos] != '"') return false;
        ++pos;
        while (pos < text.Length)
        {
            if (text[pos] == '\\') { pos += 2; continue; }
            if (text[pos++] == '"') return true;
        }
        return false;
    }

    // C++: PGConfig.cpp:662-684
    static long SkipTrivia(byte[] text, long pos)
    {
        while (pos < text.Length)
        {
            if (text[pos] == ' ' || text[pos] == '\t' || text[pos] == 13 || text[pos] == '\n')
            { ++pos; continue; }
            if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '/')
            {
                pos += 2;
                while (pos < text.Length && text[pos] != '\n' && text[pos] != 13) ++pos;
                continue;
            }
            if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '*')
            {
                pos += 2;
                while (pos + 1 < text.Length && !(text[pos] == '*' && text[pos + 1] == '/')) ++pos;
                if (pos + 1 < text.Length) pos += 2;
                continue;
            }
            break;
        }
        return pos;
    }

    // C++: PGConfig.cpp:686-741
    static bool ScanValue(byte[] text, long start, ref long end)
    {
        if (start >= text.Length) return false;
        if (text[start] == '"')
        {
            end = start;
            if (!SkipString(text, ref end)) return false;
            return true;
        }
        if (text[start] != '{' && text[start] != '[')
        {
            end = start;
            while (end < text.Length && text[end] != ',' && text[end] != '}' &&
                text[end] != ']' && text[end] != ' ' && text[end] != '\t' &&
                text[end] != 13 && text[end] != '\n') ++end;
            return end > start;
        }
        byte[] stack = new byte[128];
        int depth = 0;
        for (long pos = start; pos < text.Length; ++pos)
        {
            if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '/')
            {
                pos += 2;
                while (pos < text.Length && text[pos] != '\n' && text[pos] != 13) ++pos;
                if (pos >= text.Length) return false;
                continue;
            }
            if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '*')
            {
                pos += 2;
                while (pos + 1 < text.Length && !(text[pos] == '*' && text[pos + 1] == '/')) ++pos;
                if (pos + 1 >= text.Length) return false;
                ++pos;
                continue;
            }
            if (text[pos] == '"')
            {
                long quote = pos;
                if (!SkipString(text, ref quote)) return false;
                pos = quote - 1;
                continue;
            }
            if (text[pos] == '{' || text[pos] == '[')
            {
                if (depth >= stack.Length) return false;
                stack[depth++] = text[pos] == '{' ? (byte)'}' : (byte)']';
            }
            else if (text[pos] == '}' || text[pos] == ']')
            {
                if (depth == 0 || text[pos] != stack[depth - 1]) return false;
                if (--depth == 0) { end = pos + 1; return true; }
            }
        }
        return false;
    }

    // C++: PGConfig.cpp:743-789. valueStart/valueEnd stay -1 (the C++ static_cast<size_t>(-1)) when there is no ui_settings member.
    static bool FindUiOverlay(byte[] text, out long rootOpen, out long rootClose, out long valueStart, out long valueEnd,
        out long memberCount)
    {
        rootOpen = rootClose = valueStart = valueEnd = memberCount = 0;
        long pos = HasBom(text) ? 3 : 0;
        pos = SkipTrivia(text, pos);
        if (pos >= text.Length || text[pos] != '{') return false;
        rootOpen = pos++;
        memberCount = 0;
        valueStart = valueEnd = -1;
        while (pos < text.Length)
        {
            pos = SkipTrivia(text, pos);
            if (pos < text.Length && text[pos] == '}') { rootClose = pos; return true; }
            if (pos >= text.Length || text[pos] != '"') return false;
            long keyStart = pos;
            long keyEnd = keyStart;
            if (!SkipString(text, ref keyEnd)) return false;
            // `encodedKey = text.substr(keyStart, keyEnd - keyStart)`, then parsed as its own document
            byte[] encodedKey = new byte[keyEnd - keyStart];
            Array.Copy(text, keyStart, encodedKey, 0, encodedKey.Length);
            string key;
            using (RjDocument? keyDocument = RjDocument.Parse(encodedKey, 0, out _))
            {
                if (keyDocument == null || !keyDocument.Root.IsString) return false;
                key = keyDocument.Root.GetString();
            }
            pos = keyEnd;
            pos = SkipTrivia(text, pos);
            if (pos >= text.Length || text[pos++] != ':') return false;
            pos = SkipTrivia(text, pos);
            long start = pos;
            long end = start;
            if (!ScanValue(text, start, ref end)) return false;
            if (key == "ui_settings")
            {
                if (valueStart != -1) return false;
                valueStart = start;
                valueEnd = end;
            }
            ++memberCount;
            pos = SkipTrivia(text, end);
            if (pos < text.Length && text[pos] == ',') { ++pos; continue; }
            if (pos < text.Length && text[pos] == '}') { rootClose = pos; return true; }
            return false;
        }
        return false;
    }

    // C++: PGConfig.cpp:791-821
    static bool FormatEditableOverlay(in EditableSettings settings, out string @out)
    {
        string number = FormatNumber(settings.Recruitment.CombatScale);
        string difficulty = settings.Challenges.Difficulty == ChallengeDifficulty.Easy ? "easy" :
            settings.Challenges.Difficulty == ChallengeDifficulty.Hard ? "hard" : "normal";
        string performanceProfile = ArenaPerformance.PersistedName(settings.PerformanceProfile);
        string refreshScale = FormatNumber(settings.Challenges.RefreshCostMultiplier);
        string winMarks = FormatNumber(settings.Challenges.WinMarksMultiplier);
        string uniqueMarks = FormatNumber(settings.Challenges.UniqueBonusMultiplier);
        string skarnMarks = FormatNumber(settings.Challenges.SkarnBonusMultiplier);
        @out = Inv($"{{\n        \"recruitment\": {{\"minimum\": {settings.Recruitment.Minimum}, \"maximum\": {settings.Recruitment.Maximum}, \"step\": {settings.Recruitment.Step}, \"combat_scale\": {number}}},\n") +
            Inv($"        \"bookie\": {{\"minimum\": {settings.Bookie.Minimum}, \"maximum\": {settings.Bookie.Maximum}}},\n") +
            Inv($"        \"challenge_buy_in\": {{\"easy\": {settings.ChallengeBase[0]}, \"medium\": {settings.ChallengeBase[1]}, \"hard\": {settings.ChallengeBase[2]}}},\n") +
            Inv($"        \"challenges\": {{\"difficulty\": \"{difficulty}\", \"refresh_hours\": {settings.Challenges.RefreshHours}, \"refresh_base_cost_cats\": {settings.Challenges.RefreshBaseCostCats}, \"refresh_cost_multiplier\": {refreshScale}, \"cooldown_hours\": {settings.Challenges.CooldownHours}, \"marks\": {{\"win_multiplier\": {winMarks}, \"unique_bonus_multiplier\": {uniqueMarks}, \"skarn_bonus_multiplier\": {skarnMarks}}}}},\n") +
            "        \"performance_profile\": \"" + performanceProfile + "\",\n" +
            "        // true lets F8 toggle the debug menu; false disables the F8 action\n" +
            "        \"f8_opens_debug_menu\": " + (settings.F8OpensDebugMenu ? "true" : "false") + "\n" +
            "    }";
        return true;
    }

    // ---- the public functions -------------------------------------------------------------------------------------------------

    /// <summary>Reads pg_config.json beside ProvingGrounds.dll and stamps the results into the catalogue. Writes the shipped values
    /// to that path when no file exists; leaves a file it cannot parse untouched, because the user is probably mid-edit, and says so
    /// in pg_debug.log. Only the first call reads a file, and only the first call may apply, because an apply is one-shot.
    /// (PGConfig.h:92-97.) <paramref name="directory"/> is <c>PGLog::Directory()</c>: the plugin folder WITH its trailing
    /// separator (PGLog.cpp:21-23), or empty when it could not be resolved; the file is <c>directory + "pg_config.json"</c>.
    /// Runs on the main/sim thread before anything reads a price, like the original.</summary>
    // C++: PGConfig.cpp:824-889
    public static void Load(string? directory, IConfigFiles files, IConfigLog? log)
    {
        State g = Current;
        var c = new Ctx(g, log);
        if (g.Loaded)
            return;
        g.Loaded = true;

        if (string.IsNullOrEmpty(directory))
        {
            g.FileStatus = ConfigReadStatus.Unreadable;   // port (task 2.6)
            c.Error("Proving Grounds: the plugin directory could not be resolved; " + FileName + " cannot be read and the shipped prices stay in force");
            return;
        }

        string path = directory + FileName;
        ConfigReadStatus state = ReadText(files, path, out byte[] text);
        g.FileStatus = state;   // port (task 2.6)
        string origin = "could not be read, shipped prices used";
        if (state == ConfigReadStatus.Read)
        {
            origin = "read";
            g.FileParsed = Parse(c, text);   // port (task 2.6): the C++ ignores Parse's result here
        }
        else if (state == ConfigReadStatus.Unreadable)
            c.Error("Proving Grounds: " + FileName + " exists but could not be read; it is left untouched and the shipped prices stay in force");
        else if (WriteDefaults(g, files, path))
        {
            origin = "written from the shipped defaults";
            c.Debug("Proving Grounds: wrote " + FileName + " beside ProvingGrounds.dll from the shipped values; edit that file to change one");
        }
        else
        {
            origin = "absent and not writable, shipped prices used";
            c.Error("Proving Grounds: no " + FileName + " and it could not be written; the shipped prices stay in force and there is no file to edit");
        }

        // Counted as the catalogue consumes them, so the summary reports what
        // took effect rather than what the file offered.
        ArenaRewards.ApplyPriceOverrides();
        if (state == ConfigReadStatus.Read)
            ReportLeftovers(c);

        c.Debug(Summary(g, origin, path));
        if (g.Rejected != 0)
            c.Error("Proving Grounds: " + FileName + " was not applied in full; every rejected entry is named above");
    }

    // C++: PGConfig.cpp:864-885 (the summary line; Narrow(path) is the path itself, see Narrow above)
    static string Summary(State g, string origin, string path)
    {
        string difficultyName = g.Challenges.Difficulty == ChallengeDifficulty.Easy ? "easy" :
            g.Challenges.Difficulty == ChallengeDifficulty.Hard ? "hard" : "normal";
        // port note: the C++ formats into char summary[768] with sprintf_s, which aborts the process when the line (mostly the path)
        // would not fit; the port has no such limit.
        return "Proving Grounds: " + FileName + " " + origin + " (" + path + "); " +
            Inv($"applied catalogue={g.AppliedGear} supplies={g.AppliedSupplies} licences={g.AppliedLicences} marks_multiplier={CMath.FormatG(g.Multiplier)}; ") +
            Inv($"recruitment={g.Recruitment.Minimum}/{g.Recruitment.Maximum}/{g.Recruitment.Step} combat_scale={CMath.FormatG(g.Recruitment.CombatScale)}; bookie={g.Bookie.Minimum}/{g.Bookie.Maximum}; challenge_base={g.ChallengeBase[0]}/{g.ChallengeBase[1]}/{g.ChallengeBase[2]}; ") +
            Inv($"challenge_difficulty={difficultyName} refresh_hours={g.Challenges.RefreshHours} refresh_base_cats={g.Challenges.RefreshBaseCostCats} refresh_multiplier={CMath.FormatG(g.Challenges.RefreshCostMultiplier)} ") +
            Inv($"cooldown_hours={g.Challenges.CooldownHours} marks={CMath.FormatG(g.Challenges.WinMarksMultiplier)}/{CMath.FormatG(g.Challenges.UniqueBonusMultiplier)}/{CMath.FormatG(g.Challenges.SkarnBonusMultiplier)}; performance={ArenaPerformance.PersistedName(g.PerformanceProfile)} f8_debug={(g.F8OpensDebugMenu ? "enabled" : "disabled")}; rejected={g.Rejected}");
    }

    // Catalogue costs: the file's value for this key if it supplied a usable
    // one, otherwise defaultCost -- either way scaled by the marks multiplier.
    // tier is an ArenaRewards::Tier value. Called only by
    // ArenaRewards::ApplyPriceOverrides, which passes its own compiled default.
    // C++: PGConfig.cpp:891-903
    public static int GearCost(string? pieceStableId, int tier, int defaultCost)
    {
        State g = Current;
        if (pieceStableId == null || tier < 0 || tier > 3)
            return ScaleMarks(defaultCost);
        string key = "gear." + pieceStableId + "." + kTierNames[tier];
        if (!g.Gear.TryGetValue(key, out int cost))
            return ScaleMarks(defaultCost);
        // a used override is erased and counted (PGConfig.cpp:900-901); the load summary reports the count, ReportLeftovers the rest
        g.Gear.Remove(key);
        ++g.AppliedGear;
        return ScaleMarks(cost);
    }

    // C++: PGConfig.cpp:905-916
    public static int SupplyCost(string? stableId, int defaultCost)
    {
        State g = Current;
        if (stableId == null)
            return ScaleMarks(defaultCost);
        string key = "supplies." + stableId;
        if (!g.Supplies.TryGetValue(key, out int cost))
            return ScaleMarks(defaultCost);
        g.Supplies.Remove(key);
        ++g.AppliedSupplies;
        return ScaleMarks(cost);
    }

    // C++: PGConfig.cpp:918-929
    public static int LicenceCost(string? stableId, int defaultCost)
    {
        State g = Current;
        if (stableId == null)
            return ScaleMarks(defaultCost);
        string key = "licences." + stableId;
        if (!g.Licences.TryGetValue(key, out int cost))
            return ScaleMarks(defaultCost);
        g.Licences.Remove(key);
        ++g.AppliedLicences;
        return ScaleMarks(cost);
    }

    // A computed Marks price scaled by the multiplier, rounded half up. The one
    // place the multiplier meets a price that is not in the catalogue.
    // C++: PGConfig.cpp:931-943
    public static int ScaleMarks(int marks)
    {
        double multiplier = Current.Multiplier;
        // Exactly the shipped price at the shipped multiplier: the default build
        // never takes a path that could re-round a value.
        if (multiplier == 1.0)
            return marks;
        double scaled = (double)marks * multiplier;
        if (scaled >= (double)int.MaxValue)
            return int.MaxValue;
        if (!(scaled > 0.0))
            return 0;
        return CMath.TruncatePlusHalf(scaled);
    }

    // C++: PGConfig.cpp:945-948 (the C++ returns a const reference; a struct copy reads the same)
    public static Recruitment RecruitmentValues()
    {
        return Current.Recruitment;
    }

    // C++: PGConfig.cpp:950-953 (the C++ returns a const reference; a struct copy reads the same)
    public static Bookie BookieValues()
    {
        return Current.Bookie;
    }

    // C++: PGConfig.cpp:955-958 (the C++ returns a const reference; a struct copy reads the same)
    public static Challenges ChallengeValues()
    {
        return Current.Challenges;
    }

    // C++: PGConfig.cpp:960-970
    public static EditableSettings ShippedEditableSettings()
    {
        EditableSettings settings = default;
        settings.Recruitment = ShippedRecruitment();
        settings.Bookie = ShippedBookie();
        settings.Challenges = ShippedChallenges();
        for (int i = 0; i < 3; ++i) settings.ChallengeBase[i] = ShippedChallengeBase(i);
        settings.PerformanceProfile = ArenaPerformance.Profile.Normal;
        settings.F8OpensDebugMenu = false;
        return settings;
    }

    // C++: PGConfig.cpp:972-975
    public static ArenaPerformance.Profile PerformanceProfile()
    {
        return Current.PerformanceProfile;
    }

    // C++: PGConfig.cpp:977-987
    public static EditableSettings GetEditableSettings()
    {
        State g = Current;
        EditableSettings settings = default;
        settings.Recruitment = g.Recruitment;
        settings.Bookie = g.Bookie;
        settings.Challenges = g.Challenges;
        for (int i = 0; i < 3; ++i) settings.ChallengeBase[i] = g.ChallengeBase[i];
        settings.PerformanceProfile = g.PerformanceProfile;
        settings.F8OpensDebugMenu = g.F8OpensDebugMenu;
        return settings;
    }

    /// <summary>Saves a UI-owned overlay in pg_config.json, preserving the shop sections and comments/formatting outside that
    /// overlay. Gameplay values require a restart; crowd performance and F8 enablement update immediately (PGConfig.h:117-120).
    /// <paramref name="directory"/>: as for <see cref="Load"/>. <paramref name="error"/> is the player-facing message on false and
    /// empty on true.</summary>
    // C++: PGConfig.cpp:989-1079
    public static bool SaveEditableSettings(in EditableSettings settings, out string error, string? directory, IConfigFiles files,
        IConfigLog? log)
    {
        State g = Current;
        // port: the range checks moved unchanged into ValidateEditableSettings (PGConfig.SettingsPage.cs), which the settings page also uses.
        if (!ValidateEditableSettings(settings, out error))
            return false;

        if (string.IsNullOrEmpty(directory))
        { error = "The Proving Grounds mod folder could not be resolved."; return false; }
        string path = directory + FileName;
        if (ReadText(files, path, out byte[] text) != ConfigReadStatus.Read)
        { error = "pg_config.json could not be read; it was not changed."; return false; }
        int parseStart = HasBom(text) ? 3 : 0;
        using (RjDocument? document = RjDocument.Parse(text, parseStart, out _))
        {
            if (document == null || !document.Root.IsObject)
            { error = "pg_config.json is invalid; it was left untouched."; return false; }
        }

        if (!FindUiOverlay(text, out long rootOpen, out long rootClose, out long valueStart, out long valueEnd, out long memberCount))
        { error = "pg_config.json could not be safely updated; it was left untouched."; return false; }
        FormatEditableOverlay(settings, out string overlay);
        byte[] saved;
        if (valueStart != -1)
            saved = Splice(text, valueStart, valueEnd - valueStart, Encoding.UTF8.GetBytes(overlay));
        else
        {
            string insertion = "\n    \"ui_settings\": " + overlay;
            if (memberCount != 0) insertion += ",";
            insertion += "\n    ";
            saved = Splice(text, rootOpen + 1, 0, Encoding.UTF8.GetBytes(insertion));
        }
        _ = rootClose;
        using (RjDocument? savedDocument = RjDocument.Parse(saved, parseStart, out _))
        {
            if (savedDocument == null || !savedDocument.Root.IsObject ||
                !savedDocument.Root.TryMember("ui_settings", out Rj ui) || !ui.IsObject)
            { error = "The updated settings did not validate; pg_config.json was left untouched."; return false; }
        }

        string temporaryPath = path + ".tmp";
        ConfigWriteStatus write = files.Write(temporaryPath, saved);
        if (write == ConfigWriteStatus.NotOpened)
        { error = "Could not create a temporary config file."; return false; }
        if (write != ConfigWriteStatus.Written || !files.MoveReplace(temporaryPath, path))
        {
            files.Delete(temporaryPath);
            error = "Could not replace pg_config.json; the previous file was kept.";
            return false;
        }
        g.PerformanceProfile = settings.PerformanceProfile;
        g.F8OpensDebugMenu = settings.F8OpensDebugMenu;
        log?.Debug("Proving Grounds: saved in-game settings overlay to pg_config.json; crowd performance and F8 apply immediately");
        error = "";
        return true;
    }

    // std::string::replace / insert on the byte text
    static byte[] Splice(byte[] text, long at, long removed, byte[] inserted)
    {
        byte[] result = new byte[text.LongLength - removed + inserted.LongLength];
        Array.Copy(text, 0, result, 0, at);
        Array.Copy(inserted, 0, result, at, inserted.LongLength);
        Array.Copy(text, at + removed, result, at + inserted.LongLength, text.LongLength - at - removed);
        return result;
    }

    // C++: PGConfig.cpp:1081-1084
    public static bool F8OpensDebugMenu()
    {
        return Current.F8OpensDebugMenu;
    }

    // Challenge buy-in base for a division: 0 Easy, 1 Medium, 2 Hard. Cats.
    // C++: PGConfig.cpp:1086-1091
    public static int ChallengeBase(int division)
    {
        if (division < 0 || division > 2)
            division = 0;
        return Current.ChallengeBase[division];
    }

    // A wager of `stake` Cats as the bookie would hold it: bounded, and on a
    // whole step.
    // C++: PGConfig.cpp:1093-1100
    public static int ClampStake(int stake)
    {
        Bookie bookie = Current.Bookie;
        if (stake < bookie.Minimum)
            return bookie.Minimum;
        if (stake > bookie.Maximum)
            return bookie.Maximum;
        return stake;
    }

    // Ceiling the save sidecar accepts for a pending bookie credit. A raised
    // stake ceiling must not let the plugin write a credit the reader would then
    // reject -- that discards the save's whole arena progression. Never below the
    // shipped bound, so an existing save keeps loading.
    // C++: PGConfig.cpp:1102-1109
    public static int BookieCreditCeiling()
    {
        // Betting is blocked while a credit is pending, so the largest credit a
        // save can hold is one winning payout at the book's odds ceiling.
        double ceiling = (double)Current.Bookie.Maximum * BookOddsCeiling;
        return ceiling > ShippedBookieCreditCeiling ?
            CMath.TruncatePlusHalf(ceiling) : ShippedBookieCreditCeiling;
    }
}

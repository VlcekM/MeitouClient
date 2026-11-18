using System.Globalization;

namespace ProvingGrounds.Core.Glue;

/// <summary>The 19 values of the original's settings page (PGSettingsUI.cpp:41-46 <c>SettingId</c>, in its row order) as the port's Meitou
/// settings group holds them: numbers as numbers, the three lists as their option strings. Immutable: the page's Changed handler
/// publishes a new one, the simulation reads the published one (CAP 8 threading recipe).</summary>
public sealed record PgSettingsValues(
    int RecruitmentMinimum, int RecruitmentMaximum, int RecruitmentStep, double RecruitmentCombatScale,
    int BookieMinimum, int BookieMaximum,
    int ChallengeBuyInEasy, int ChallengeBuyInMedium, int ChallengeBuyInHard,
    string ChallengeDifficulty, int RefreshHours, int RefreshBaseCostCats, double RefreshCostMultiplier, int CooldownHours,
    double WinMarksMultiplier, double UniqueBonusMultiplier, double SkarnBonusMultiplier,
    string PerformanceProfile, string DebugMenuKey);

/// <summary>The two values that apply at once (PGConfig.cpp:1069-1070, PGSettingsUI.cpp:524 "Crowd performance applies next bout; F8
/// applies immediately"): the crowd preset and the debug menu key that replaces F8 (overview §1).</summary>
public sealed record PgLiveSettings(ArenaPerformance.Profile PerformanceProfile, string DebugMenuKey)
{
    /// <summary>The original's <c>f8OpensDebugMenu</c>: any key but Off.</summary>
    public bool F8OpensDebugMenu => DebugMenuKey != PgSettingsPage.DebugKeyOff;
}

/// <summary>The pure half of the port's settings page (task M2.6; the Meitou object is <c>src/Ui/PGSettingsUI.cs</c>): Meitou values to
/// and from the original's <see cref="PGConfig.EditableSettings"/>, which values apply at once, and the <c>pg probe-config</c> JSON.</summary>
public static class PgSettingsPage
{
    /// <summary>"Challenge opponent difficulty" choices as the original's row shows them (PGSettingsUI.cpp:227-228); stored in the file as
    /// easy / normal / hard.</summary>
    public static readonly string[] DifficultyOptions = { "Easy", "Normal", "Hard" };
    /// <summary>"Crowd performance" choices as the original's row shows them (ArenaPerformance::Name, ArenaPerformance.cpp:10-18;
    /// PGSettingsUI.cpp:233), in Profile order; stored in the file as high / normal / potato (PersistedName / Parse).</summary>
    public static readonly string[] PerformanceOptions = { "High", "Normal", "Potato" };
    public const string DebugKeyOff = "Off";
    /// <summary>"Debug menu key" choices (overview §1: F8 is Meitou's screenshot key).</summary>
    public static readonly string[] DebugKeyOptions = { DebugKeyOff, "O", "N", "V" };

    /// <summary>The key a file's <c>f8_opens_debug_menu</c> stands for: true = the first key, O (port rule: the file has no key).</summary>
    public static string DebugKeyFor(bool f8OpensDebugMenu) => f8OpensDebugMenu ? DebugKeyOptions[1] : DebugKeyOff;

    /// <summary>The page values for loaded settings; <paramref name="debugKey"/> is the key row (the original's settings have only a bool).</summary>
    public static PgSettingsValues From(in PGConfig.EditableSettings s, string debugKey) => new(
        s.Recruitment.Minimum, s.Recruitment.Maximum, s.Recruitment.Step, s.Recruitment.CombatScale,
        s.Bookie.Minimum, s.Bookie.Maximum,
        s.ChallengeBase[0], s.ChallengeBase[1], s.ChallengeBase[2],
        DifficultyOptions[(int)s.Challenges.Difficulty is >= 0 and <= 2 ? (int)s.Challenges.Difficulty : 1],
        s.Challenges.RefreshHours, s.Challenges.RefreshBaseCostCats, s.Challenges.RefreshCostMultiplier, s.Challenges.CooldownHours,
        s.Challenges.WinMarksMultiplier, s.Challenges.UniqueBonusMultiplier, s.Challenges.SkarnBonusMultiplier,
        ArenaPerformance.Name(s.PerformanceProfile), debugKey);

    /// <summary>The original's settings for page values. A list value that is not one of its options reads as the original's fallback
    /// (difficulty Normal, PGSettingsUI.cpp:227-228; performance Normal, the shipped profile, PGConfig.cpp:69; key Off).</summary>
    public static PGConfig.EditableSettings ToEditable(PgSettingsValues v)
    {
        PGConfig.EditableSettings s = default;
        s.Recruitment = new PGConfig.Recruitment
        { Minimum = v.RecruitmentMinimum, Maximum = v.RecruitmentMaximum, Step = v.RecruitmentStep, CombatScale = v.RecruitmentCombatScale };
        s.Bookie = new PGConfig.Bookie { Minimum = v.BookieMinimum, Maximum = v.BookieMaximum };
        s.ChallengeBase[0] = v.ChallengeBuyInEasy;
        s.ChallengeBase[1] = v.ChallengeBuyInMedium;
        s.ChallengeBase[2] = v.ChallengeBuyInHard;
        s.Challenges = new PGConfig.Challenges
        {
            Difficulty = v.ChallengeDifficulty == "Easy" ? PGConfig.ChallengeDifficulty.Easy
                : v.ChallengeDifficulty == "Hard" ? PGConfig.ChallengeDifficulty.Hard : PGConfig.ChallengeDifficulty.Normal,
            RefreshHours = v.RefreshHours,
            RefreshBaseCostCats = v.RefreshBaseCostCats,
            RefreshCostMultiplier = v.RefreshCostMultiplier,
            CooldownHours = v.CooldownHours,
            WinMarksMultiplier = v.WinMarksMultiplier,
            UniqueBonusMultiplier = v.UniqueBonusMultiplier,
            SkarnBonusMultiplier = v.SkarnBonusMultiplier,
        };
        s.PerformanceProfile = Live(v).PerformanceProfile;
        s.F8OpensDebugMenu = Live(v).F8OpensDebugMenu;
        return s;
    }

    /// <summary>The at-once values of a page.</summary>
    public static PgLiveSettings Live(PgSettingsValues v)
    {
        int index = Array.IndexOf(PerformanceOptions, v.PerformanceProfile);
        var profile = ArenaPerformance.IsValid(index) ? (ArenaPerformance.Profile)index : ArenaPerformance.Profile.Normal;
        return new PgLiveSettings(profile, Array.IndexOf(DebugKeyOptions, v.DebugMenuKey) >= 0 ? v.DebugMenuKey : DebugKeyOff);
    }

    /// <summary>True for the two settings that apply at once; every other one is read at start ("restart" in the original).</summary>
    public static bool AppliesAtOnce(string key) => key is nameof(PgSettingsValues.PerformanceProfile) or nameof(PgSettingsValues.DebugMenuKey);

    /// <summary>The ui_settings block for the page values the player changed (the values differing from the file's, which are the page's
    /// defaults); null when none.</summary>
    public static string? Overlay(PgSettingsValues file, PgSettingsValues chosen) =>
        PGConfig.SettingsPageOverlay(ToEditable(file), ToEditable(chosen));

    /// <summary>The original Save button's check of the whole page (PGConfig.cpp:991-1024) and its message.</summary>
    public static bool Validate(PgSettingsValues v, out string error) => PGConfig.ValidateEditableSettings(ToEditable(v), out error);

    /// <summary><c>pg probe-config</c>: every value in force (file + page overlay + at-once changes) as one JSON object in the file's key
    /// names, plus the key row, the marks multiplier and what Load counted. Doubles through <see cref="CMath.FormatG(double, int)"/>
    /// (%g, as the Load summary line prints them; the page's values have at most two decimals).</summary>
    public static string ProbeJson(in PGConfig.EditableSettings s, PGConfig.LoadReport report, string debugKey, string path)
    {
        static string N(double v) => double.IsFinite(v) ? CMath.FormatG(v) : "null";
        string difficulty = s.Challenges.Difficulty == PGConfig.ChallengeDifficulty.Easy ? "easy"
            : s.Challenges.Difficulty == PGConfig.ChallengeDifficulty.Hard ? "hard" : "normal";
        return string.Create(CultureInfo.InvariantCulture,
            $"{{\"verb\":\"probe-config\",\"path\":{System.Text.Json.JsonSerializer.Serialize(path)},\"loaded\":{(report.Loaded ? "true" : "false")},\"rejected\":{report.Rejected},")
            + string.Create(CultureInfo.InvariantCulture,
            $"\"marks_multiplier\":{N(report.MarksMultiplier)},\"applied\":{{\"catalogue\":{report.AppliedGear},\"supplies\":{report.AppliedSupplies},\"licences\":{report.AppliedLicences}}},")
            + string.Create(CultureInfo.InvariantCulture,
            $"\"recruitment\":{{\"minimum\":{s.Recruitment.Minimum},\"maximum\":{s.Recruitment.Maximum},\"step\":{s.Recruitment.Step},\"combat_scale\":{N(s.Recruitment.CombatScale)}}},")
            + string.Create(CultureInfo.InvariantCulture,
            $"\"bookie\":{{\"minimum\":{s.Bookie.Minimum},\"maximum\":{s.Bookie.Maximum}}},\"challenge_buy_in\":{{\"easy\":{s.ChallengeBase[0]},\"medium\":{s.ChallengeBase[1]},\"hard\":{s.ChallengeBase[2]}}},")
            + string.Create(CultureInfo.InvariantCulture,
            $"\"challenges\":{{\"difficulty\":\"{difficulty}\",\"refresh_hours\":{s.Challenges.RefreshHours},\"refresh_base_cost_cats\":{s.Challenges.RefreshBaseCostCats},\"refresh_cost_multiplier\":{N(s.Challenges.RefreshCostMultiplier)},\"cooldown_hours\":{s.Challenges.CooldownHours},")
            + string.Create(CultureInfo.InvariantCulture,
            $"\"marks\":{{\"win_multiplier\":{N(s.Challenges.WinMarksMultiplier)},\"unique_bonus_multiplier\":{N(s.Challenges.UniqueBonusMultiplier)},\"skarn_bonus_multiplier\":{N(s.Challenges.SkarnBonusMultiplier)}}}}},")
            + $"\"performance_profile\":\"{ArenaPerformance.PersistedName(s.PerformanceProfile)}\",\"f8_opens_debug_menu\":{(s.F8OpensDebugMenu ? "true" : "false")},"
            + $"\"debug_menu_key\":{System.Text.Json.JsonSerializer.Serialize(debugKey)}}}";
    }
}

/// <summary>The settings page's start-up order (task M2.6), shared by the mod's Initialize (<c>src/Ui/PGSettingsUI.cs</c> Start) and the
/// tests, so the tests run the real sequence: <see cref="LoadFile"/> once per process, then Meitou's AddSettings (sets the player's saved
/// values on the page), then <see cref="ApplyPage"/>.</summary>
public static class PgSettingsStartup
{
    /// <summary>PGConfig.Load (once per process: a second Load re-stamps the catalogue, ArenaRewards.cs:8-16) and the file's values, which
    /// are the page's defaults. <paramref name="directory"/> ends with a separator.</summary>
    public static PgSettingsValues LoadFile(string directory, IConfigFiles files, IConfigLog? log)
    {
        PGConfig.Load(directory, files, log);
        PGConfig.EditableSettings loaded = PGConfig.GetEditableSettings();
        return PgSettingsPage.From(loaded, PgSettingsPage.DebugKeyFor(loaded.F8OpensDebugMenu));
    }

    /// <summary>The values the page shows and that are in force: the player's, or the file's when this start's file was not read (the
    /// original then read no ui_settings, PGConfig.cpp:841-847; <see cref="PGConfig.SettingsPageOverlayAllowed"/>).</summary>
    public static PgSettingsValues Shown(PgSettingsValues file, PgSettingsValues chosen) =>
        PGConfig.SettingsPageOverlayAllowed() ? chosen : file;

    /// <summary>Applies the player's changes (<paramref name="chosen"/> against the file's values) through Core's ui_settings reader, which
    /// refuses them with a log line when the file was not read; then, whatever happened, puts the at-once values of <see cref="Shown"/> in
    /// force (so a failing overlay never leaves the crowd preset and the key out of step with the page). Returns <see cref="Shown"/>.</summary>
    public static PgSettingsValues ApplyPage(PgSettingsValues file, PgSettingsValues chosen, string directory, IConfigLog? log)
    {
        PgSettingsValues shown = Shown(file, chosen);
        try
        {
            PGConfig.ApplySettingsPageOverlay(PgSettingsPage.Overlay(file, chosen), directory, log);
        }
        finally
        {
            PgLiveSettings live = PgSettingsPage.Live(shown);
            PGConfig.ApplyLiveSettings(live.PerformanceProfile, live.F8OpensDebugMenu);
        }
        return shown;
    }
}

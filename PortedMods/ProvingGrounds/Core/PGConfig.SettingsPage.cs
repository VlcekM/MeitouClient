// Port of upstream/ProvingGrounds/plugin/src/PGConfig.cpp (Proving Grounds by Shutori, GPLv3): the in-game settings overlay as the port
// feeds it (task M2.6). No C++ function of its own: these pieces let the glue apply Meitou's settings page through the SAME readers the
// original's ui_settings block goes through (ReadEditableOverlay, PGConfig.cpp:285-322), validate a page with the SAME rules the original
// Save button used (SaveEditableSettings, PGConfig.cpp:989-1024), and apply the two "at once" values the way a save did (:1069-1070).

using System.Text;

namespace ProvingGrounds.Core;

public static partial class PGConfig
{
    /// <summary>What Load and the overlay left, for <c>pg probe-config</c> (fields of the C++ globals with no accessor of their own).</summary>
    public readonly record struct LoadReport(bool Loaded, int Rejected, double MarksMultiplier, int AppliedGear, int AppliedSupplies,
        int AppliedLicences, ConfigReadStatus FileStatus, bool FileParsed);

    /// <summary>The report of <see cref="Current"/> (port only: the C++ prints these only in its Load summary line, PGConfig.cpp:864-885).</summary>
    public static LoadReport Report()
    {
        State g = Current;
        return new LoadReport(g.Loaded, g.Rejected, g.Multiplier, g.AppliedGear, g.AppliedSupplies, g.AppliedLicences, g.FileStatus, g.FileParsed);
    }

    /// <summary>The range checks of the original's Save button, with its player-facing messages: true and "" when the settings may be
    /// saved. Extracted unchanged from <see cref="SaveEditableSettings"/>, which calls it first.</summary>
    // C++: PGConfig.cpp:991-1024
    public static bool ValidateEditableSettings(in EditableSettings settings, out string error)
    {
        if (settings.Recruitment.Minimum < 0 || settings.Recruitment.Maximum > MaxEditableCost ||
            settings.Recruitment.Minimum > settings.Recruitment.Maximum ||
            settings.Recruitment.Step < 1 || settings.Recruitment.Step > MaxEditableCost ||
            !double.IsFinite(settings.Recruitment.CombatScale) || settings.Recruitment.CombatScale < 0.0 ||
            settings.Recruitment.CombatScale > MaxScale)
        { error = "Recruitment settings are outside their supported ranges."; return false; }
        if (settings.Bookie.Minimum < 0 || settings.Bookie.Maximum > MaxEditableCost ||
            settings.Bookie.Minimum > settings.Bookie.Maximum ||
            settings.Bookie.Minimum % StakeStep != 0 || settings.Bookie.Maximum % StakeStep != 0)
        { error = "Bookie limits must be ordered multiples of 10 Cats."; return false; }
        if (settings.Challenges.Difficulty != ChallengeDifficulty.Easy &&
            settings.Challenges.Difficulty != ChallengeDifficulty.Normal &&
            settings.Challenges.Difficulty != ChallengeDifficulty.Hard)
        { error = "Challenge difficulty is invalid."; return false; }
        if (!ArenaPerformance.IsValid((int)settings.PerformanceProfile))
        { error = "Crowd performance profile is invalid."; return false; }
        for (int i = 0; i < 3; ++i)
            if (settings.ChallengeBase[i] < 0 || settings.ChallengeBase[i] > MaxEditableCost)
            { error = "Challenge buy-in is outside its supported range."; return false; }
        if (settings.Challenges.RefreshHours < 1 || settings.Challenges.RefreshHours > 168 ||
            settings.Challenges.RefreshBaseCostCats < 1 || settings.Challenges.RefreshBaseCostCats > 1000000 ||
            !double.IsFinite(settings.Challenges.RefreshCostMultiplier) || settings.Challenges.RefreshCostMultiplier < 1.0 ||
            settings.Challenges.RefreshCostMultiplier > 10.0 || settings.Challenges.CooldownHours < 0 ||
            settings.Challenges.CooldownHours > 720 ||
            !double.IsFinite(settings.Challenges.WinMarksMultiplier) || settings.Challenges.WinMarksMultiplier < 0.0 ||
            settings.Challenges.WinMarksMultiplier > MaxScale ||
            !double.IsFinite(settings.Challenges.UniqueBonusMultiplier) || settings.Challenges.UniqueBonusMultiplier < 0.0 ||
            settings.Challenges.UniqueBonusMultiplier > MaxScale ||
            !double.IsFinite(settings.Challenges.SkarnBonusMultiplier) || settings.Challenges.SkarnBonusMultiplier < 0.0 ||
            settings.Challenges.SkarnBonusMultiplier > MaxScale)
        { error = "Challenge settings are outside their supported ranges."; return false; }
        error = "";
        return true;
    }

    /// <summary>The original's ui_settings block for the values of <paramref name="chosen"/> that differ from <paramref name="file"/>,
    /// in the key names and number text of <c>FormatEditableOverlay</c> (PGConfig.cpp:791-821); null when nothing differs. The port
    /// keeps the settings page's values in Meitou's config, not in pg_config.json (divergences.md "settings stored by Meitou"): a
    /// value the player never changed on the page is left out, so the file's own value stays in force for it.</summary>
    public static string? SettingsPageOverlay(in EditableSettings file, in EditableSettings chosen)
    {
        var sections = new List<string>();
        var members = new List<string>();
        void Whole(string key, int a, int b) { if (a != b) members.Add(Inv($"\"{key}\": {b}")); }
        void Number(string key, double a, double b) { if (!a.Equals(b)) members.Add("\"" + key + "\": " + FormatNumber(b)); }
        void Section(string key)
        {
            if (members.Count > 0) sections.Add("        \"" + key + "\": {" + string.Join(", ", members) + "}");
            members.Clear();
        }

        Whole("minimum", file.Recruitment.Minimum, chosen.Recruitment.Minimum);
        Whole("maximum", file.Recruitment.Maximum, chosen.Recruitment.Maximum);
        Whole("step", file.Recruitment.Step, chosen.Recruitment.Step);
        Number("combat_scale", file.Recruitment.CombatScale, chosen.Recruitment.CombatScale);
        Section("recruitment");
        Whole("minimum", file.Bookie.Minimum, chosen.Bookie.Minimum);
        Whole("maximum", file.Bookie.Maximum, chosen.Bookie.Maximum);
        Section("bookie");
        Whole("easy", file.ChallengeBase[0], chosen.ChallengeBase[0]);
        Whole("medium", file.ChallengeBase[1], chosen.ChallengeBase[1]);
        Whole("hard", file.ChallengeBase[2], chosen.ChallengeBase[2]);
        Section("challenge_buy_in");
        if (file.Challenges.Difficulty != chosen.Challenges.Difficulty)
            members.Add("\"difficulty\": \"" + DifficultyKey(chosen.Challenges.Difficulty) + "\"");
        Whole("refresh_hours", file.Challenges.RefreshHours, chosen.Challenges.RefreshHours);
        Whole("refresh_base_cost_cats", file.Challenges.RefreshBaseCostCats, chosen.Challenges.RefreshBaseCostCats);
        Number("refresh_cost_multiplier", file.Challenges.RefreshCostMultiplier, chosen.Challenges.RefreshCostMultiplier);
        Whole("cooldown_hours", file.Challenges.CooldownHours, chosen.Challenges.CooldownHours);
        var challengeMembers = new List<string>(members);
        members.Clear();
        Number("win_multiplier", file.Challenges.WinMarksMultiplier, chosen.Challenges.WinMarksMultiplier);
        Number("unique_bonus_multiplier", file.Challenges.UniqueBonusMultiplier, chosen.Challenges.UniqueBonusMultiplier);
        Number("skarn_bonus_multiplier", file.Challenges.SkarnBonusMultiplier, chosen.Challenges.SkarnBonusMultiplier);
        if (members.Count > 0) challengeMembers.Add("\"marks\": {" + string.Join(", ", members) + "}");
        members.Clear();
        members.AddRange(challengeMembers);
        Section("challenges");
        if (file.PerformanceProfile != chosen.PerformanceProfile)
            sections.Add("        \"performance_profile\": \"" + ArenaPerformance.PersistedName(chosen.PerformanceProfile) + "\"");
        if (file.F8OpensDebugMenu != chosen.F8OpensDebugMenu)
            sections.Add("        \"f8_opens_debug_menu\": " + (chosen.F8OpensDebugMenu ? "true" : "false"));
        return sections.Count == 0 ? null : "{\n" + string.Join(",\n", sections) + "\n    }";
    }

    // The difficulty spelling of FormatEditableOverlay / ReadChallengeSettings (PGConfig.cpp:794-795, 263-266).
    static string DifficultyKey(ChallengeDifficulty difficulty) =>
        difficulty == ChallengeDifficulty.Easy ? "easy" : difficulty == ChallengeDifficulty.Hard ? "hard" : "normal";

    /// <summary>False when the original would have read no ui_settings from this start's file although one existed: the file could not
    /// be read (PGConfig.cpp:841-847, also an unresolved folder, :830-834), or it was read but Parse kept nothing (invalid JSON, a root
    /// that is not an object, a wrong version: the ResetToShipped returns at PGConfig.cpp:356-379). Then only the shipped values apply, so the page's values are not
    /// applied either. A missing file (the shipped default written) still takes the page's values: the port keeps them outside the file
    /// (divergences.md "settings stored by Meitou").</summary>
    public static bool SettingsPageOverlayAllowed()
    {
        State g = Current;
        return !(g.FileStatus == ConfigReadStatus.Unreadable || (g.FileStatus == ConfigReadStatus.Read && !g.FileParsed));
    }

    /// <summary>Applies a ui_settings block (from <see cref="SettingsPageOverlay"/>) over the loaded values through the original's own
    /// overlay reader, so each value is checked, normalised and rejected exactly as a ui_settings block in pg_config.json is
    /// (PGConfig.cpp:285-322: per-key ranges, "minimum exceeds maximum" keeps the earlier range, bookie bounds on whole tens). Runs once,
    /// right after <see cref="Load"/>, on the thread that loaded; null does nothing. Port only: the C++ reads ui_settings as the last
    /// section of the file inside Load, before the summary line, so the port logs the summary again when the page changed anything.</summary>
    public static void ApplySettingsPageOverlay(string? overlay, string? directory, IConfigLog? log)
    {
        if (overlay == null)
            return;
        State g = Current;
        var c = new Ctx(g, log);
        if (!SettingsPageOverlayAllowed())
        {
            c.Error("Proving Grounds: " + FileName + " was not read, so the settings page values are not applied either; the shipped values stay in force");
            return;
        }
        int rejectedBefore = g.Rejected;
        using (RjDocument? document = RjDocument.Parse(Encoding.UTF8.GetBytes(overlay), 0, out RjParseError error))
        {
            if (document == null || !document.Root.IsObject)
            {
                c.Error(Inv($"Proving Grounds: the settings page values could not be read ({error.Message}); the {FileName} values stay in force"));
                return;
            }
            ReadEditableOverlay(c, document.Root);
        }
        c.Debug(Summary(g, "with the settings page values applied", (directory ?? "") + FileName));
        if (g.Rejected != rejectedBefore)
            c.Error("Proving Grounds: the settings page values were not applied in full; every rejected entry is named above");
    }

    /// <summary>The two values a save applied at once (crowd performance, F8 enablement), as the end of SaveEditableSettings sets them.
    /// Call on the simulation thread (a worker may read them), like the C++, which saved on the main thread.</summary>
    // C++: PGConfig.cpp:1069-1070
    public static void ApplyLiveSettings(ArenaPerformance.Profile performanceProfile, bool f8OpensDebugMenu)
    {
        State g = Current;
        g.PerformanceProfile = performanceProfile;
        g.F8OpensDebugMenu = f8OpensDebugMenu;
    }
}

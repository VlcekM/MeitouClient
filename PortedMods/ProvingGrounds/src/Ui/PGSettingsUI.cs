using Meitou.Modding;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Ui;

/// <summary>
/// Port of PGSettingsUI.cpp (the "PG CONFIG" page, 813 lines) and the start of PGConfig (Load, task M2.6): the original's 19 rows as one Meitou
/// settings group (Esc &gt; Settings &gt; Mods, CAP 8, decision D6). Rows, order, labels and hints are kLabels / kDescriptions
/// (PGSettingsUI.cpp:67-98), except "F8 debug menu", which is "Debug menu key" (overview §1). The pure half (values to and from
/// PGConfig.EditableSettings, the overlay, the checks, the probe JSON) is Core/Glue/PgSettingsPage.cs, tested in Tests/Glue.
/// <para>Engine calls of the C++ and what replaces them:
/// <list type="bullet">
/// <item>OptionsWindow MODS tab, FindReSettingsButton, the injected "PG CONFIG" button, MyGUI window, rows (labels, edit boxes, sliders, -/+),
///   status line, Save / Reset / Close, restart popup (PGSettingsUI.cpp:103-813): <c>ModContext.AddSettings</c> on this object
///   (ModContext.cs:252-264); the page draws the rows (Settings/ModSettingsRows.cs:60-141).</item>
/// <item>PGConfig::GetEditableSettings at window open (:779): the page's values; the defaults are the loaded file's values.</item>
/// <item>PGConfig::SaveEditableSettings (:516-529, writes ui_settings into pg_config.json): Meitou keeps the page in meitou.user.json
///   (<c>modSettings</c>, a default not kept, ModSettingsRows.cs:131-140); the values that differ from the file's are applied over the file at
///   start through Core's ui_settings reader (<see cref="PGConfig.ApplySettingsPageOverlay"/>). divergences.md "settings stored by Meitou".</item>
/// <item>PGConfig::Load from Plugin.cpp startPlugin: <see cref="Start"/>, once per process, from the mod's Initialize before AddSystem.</item>
/// <item>The immediate g_performanceProfile / g_f8OpensDebugMenu update of a save (PGConfig.cpp:1069-1070): the group's Changed (frame thread)
///   publishes <see cref="PgLiveSettings"/>; <see cref="ApplyLive"/> applies it in the system's serial Inputs.</item>
/// <item>PGLog::Debug / PGLog::Error: <c>ModLog.Config</c> (pg_debug.log; errors also reach Meitou's log, task 2.7). PGLog.Install runs
///   first in the mod's Initialize, before this Load, as the C++ sink exists before startPlugin calls PGConfig::Load (Plugin.cpp:15-21).</item>
/// </list></para>
/// <para>Meitou rules (ModSettings.cs:12-43, 208-257): every property marked [Setting] is a row, in declaration order; the key in
/// meitou.user.json is the property name (renaming one loses the player's value). A number is clamped to Min..Max and snapped to Step.</para>
/// </summary>
public sealed class PGSettingsUI
{
    // C++: PGSettingsUI.cpp:444-452 (the restart popup, "Restart required"); shown on every row the original only applied at restart.
    const string Restart = " Gameplay settings were saved. Restart Kenshi for these changes to take effect.";
    // Port note (CAP 8): Meitou draws a number as a slider over its whole range; the original also had a text box. Kept the original's range.
    const string WideRange = " Port note: the slider spans the whole range; the arrow keys move it one step.";

    // ---- the 19 rows, in the original's order (PGSettingsUI.cpp:41-46 SettingId, :67-98 kLabels / kDescriptions) -------------------

    [Setting("Recruitment minimum (Marks)", Hint = "Lowest Marks fee after stat pricing; range 0-1,000,000 and cannot exceed the maximum." + Restart + WideRange,
        Min = 0, Max = 1000000, Step = 1)]
    public int RecruitmentMinimum { get; set; }

    [Setting("Recruitment maximum (Marks)", Hint = "Highest Marks fee before the Marks multiplier; range 0-1,000,000 and cannot be below the minimum." + Restart + WideRange,
        Min = 0, Max = 1000000, Step = 1)]
    public int RecruitmentMaximum { get; set; }

    [Setting("Recruitment rounding step (Marks)", Hint = "Round prisoner fees up to this many Marks; range 1-1,000,000." + Restart + WideRange,
        Min = 1, Max = 1000000, Step = 1)]
    public int RecruitmentStep { get; set; }

    [Setting("Recruitment combat scale", Hint = "Marks added per average combat-stat point; range 0-100." + Restart + WideRange,
        Min = 0, Max = 100, Step = 0.01)]
    public double RecruitmentCombatScale { get; set; }

    [Setting("Bookie minimum stake (Cats)", Hint = "Lowest bookie stake in Cats; range 0-1,000,000, in 10-Cat increments, at or below maximum." + Restart + WideRange,
        Min = 0, Max = 1000000, Step = 10)]
    public int BookieMinimum { get; set; }

    [Setting("Bookie maximum stake (Cats)", Hint = "Highest bookie stake in Cats; range 0-1,000,000, in 10-Cat increments, at or above minimum." + Restart + WideRange,
        Min = 0, Max = 1000000, Step = 10)]
    public int BookieMaximum { get; set; }

    [Setting("Easy challenge buy-in (Cats)", Hint = "Easy challenge base buy-in in Cats, before opponent-power adjustment; range 0-1,000,000." + Restart + WideRange,
        Min = 0, Max = 1000000, Step = 1)]
    public int ChallengeBuyInEasy { get; set; }

    [Setting("Medium challenge buy-in (Cats)", Hint = "Medium challenge base buy-in in Cats, before opponent-power adjustment; range 0-1,000,000." + Restart + WideRange,
        Min = 0, Max = 1000000, Step = 1)]
    public int ChallengeBuyInMedium { get; set; }

    [Setting("Hard challenge buy-in (Cats)", Hint = "Hard challenge base buy-in in Cats, before opponent-power adjustment; range 0-1,000,000." + Restart + WideRange,
        Min = 0, Max = 1000000, Step = 1)]
    public int ChallengeBuyInHard { get; set; }

    [Setting("Challenge opponent difficulty", Hint = "Biases generated challenge opponents weaker or stronger; does not change stats." + Restart,
        Options = new[] { "Easy", "Normal", "Hard" })]
    public string ChallengeDifficulty { get; set; } = "Normal";

    [Setting("Offer refresh interval (in-game hours)", Hint = "How often challenge offers refresh in in-game hours; range 1-168." + Restart,
        Min = 1, Max = 168, Step = 1)]
    public int RefreshHours { get; set; }

    [Setting("First paid refresh cost (Cats)", Hint = "Starting Cats cost for the first paid offer refresh; range 1-1,000,000." + Restart + WideRange,
        Min = 1, Max = 1000000, Step = 1)]
    public int RefreshBaseCostCats { get; set; }

    [Setting("Paid refresh cost multiplier", Hint = "Paid-refresh growth factor until a challenge is booked; range 1.00-10.00." + Restart,
        Min = 1, Max = 10, Step = 0.01)]
    public double RefreshCostMultiplier { get; set; }

    [Setting("Skarn cooldown (in-game hours)", Hint = "In-game hours after a Skarn challenge attempt before another Skarn challenge can be booked; other challenges are unaffected. Range 0-720." + Restart,
        Min = 0, Max = 720, Step = 1)]
    public int CooldownHours { get; set; }

    [Setting("Challenge win Marks multiplier", Hint = "Multiplier for Marks awarded for winning a challenge; range 0-100." + Restart + WideRange,
        Min = 0, Max = 100, Step = 0.01)]
    public double WinMarksMultiplier { get; set; }

    [Setting("Unique-fighter Marks bonus multiplier", Hint = "Multiplier for the unique-fighter Marks bonus in challenges; range 0-100." + Restart + WideRange,
        Min = 0, Max = 100, Step = 0.01)]
    public double UniqueBonusMultiplier { get; set; }

    [Setting("Skarn Marks bonus multiplier", Hint = "Multiplier for Marks awarded for defeating Skarn; range 0-100." + Restart + WideRange,
        Min = 0, Max = 100, Step = 0.01)]
    public double SkarnBonusMultiplier { get; set; }

    [Setting("Crowd performance", Hint = "Crowd upkeep preset. Takes effect on the next arena bout after saving.",
        Options = new[] { "High", "Normal", "Potato" })]
    public string PerformanceProfile { get; set; } = "Normal";

    // "F8 debug menu" in the original (kLabels[18]); the hint is the original's text (overview §1, divergences.md).
    [Setting("Debug menu key", Hint = "When enabled, F8 toggles the debug menu. When disabled, F8 does nothing.",
        Options = new[] { "Off", "O", "N", "V" })]
    public string DebugMenuKey { get; set; } = "Off";

    /// <summary>A page holding <paramref name="v"/>: the values it has when registered are Meitou's defaults (what Reset puts back).</summary>
    PGSettingsUI(PgSettingsValues v) => Show(v);

    /// <summary>Sets every property (no Changed: Meitou raises it only for its own Set).</summary>
    void Show(PgSettingsValues v)
    {
        RecruitmentMinimum = v.RecruitmentMinimum; RecruitmentMaximum = v.RecruitmentMaximum; RecruitmentStep = v.RecruitmentStep;
        RecruitmentCombatScale = v.RecruitmentCombatScale; BookieMinimum = v.BookieMinimum; BookieMaximum = v.BookieMaximum;
        ChallengeBuyInEasy = v.ChallengeBuyInEasy; ChallengeBuyInMedium = v.ChallengeBuyInMedium; ChallengeBuyInHard = v.ChallengeBuyInHard;
        ChallengeDifficulty = v.ChallengeDifficulty; RefreshHours = v.RefreshHours; RefreshBaseCostCats = v.RefreshBaseCostCats;
        RefreshCostMultiplier = v.RefreshCostMultiplier; CooldownHours = v.CooldownHours; WinMarksMultiplier = v.WinMarksMultiplier;
        UniqueBonusMultiplier = v.UniqueBonusMultiplier; SkarnBonusMultiplier = v.SkarnBonusMultiplier;
        PerformanceProfile = v.PerformanceProfile; DebugMenuKey = v.DebugMenuKey;
    }

    /// <summary>The page's values now (read on the thread that changes them: Initialize, then the page's frame thread).</summary>
    PgSettingsValues Values() => new(RecruitmentMinimum, RecruitmentMaximum, RecruitmentStep, RecruitmentCombatScale, BookieMinimum, BookieMaximum,
        ChallengeBuyInEasy, ChallengeBuyInMedium, ChallengeBuyInHard, ChallengeDifficulty, RefreshHours, RefreshBaseCostCats, RefreshCostMultiplier,
        CooldownHours, WinMarksMultiplier, UniqueBonusMultiplier, SkarnBonusMultiplier, PerformanceProfile, DebugMenuKey);

    // ---- process-wide state (the original's PGConfig globals are process-wide too) -------------------------------------------------

    static bool s_loaded;                     // Load ran (once per process: a second Load re-stamps the catalogue, ArenaRewards.cs:8-16)
    static string s_directory = "";           // the mod folder WITH its trailing separator (PGLog::Directory, PGLog.cpp:21-23)
    static PgSettingsValues? s_fileValues;    // the file's values = the page's defaults
    static volatile PgLiveSettings? s_published;   // written by the page's Changed (frame thread), read by ApplyLive (sim thread)
    static PgLiveSettings? s_applied;         // sim thread only (and Start, before any world)
    static volatile string s_probe = "{\"verb\":\"probe-config\",\"loaded\":false}";
    static volatile bool s_configLoaded;      // the last published report's Loaded (pg_config.json read), for the F10 status row
    static ModSettingsGroup? s_group;         // the registered page (TEST ONLY pg setting reaches its rows through it)

    /// <summary>The <c>pg probe-config</c> line: every value in force (any thread; immutable text).</summary>
    internal static string ProbeJson => s_probe;

    /// <summary>True once pg_config.json loaded (the probe's "loaded"; any thread).</summary>
    internal static bool Loaded => s_configLoaded;

    /// <summary>The debug menu key in force (sim thread; for ArenaUI.PollDebugMenuKey, task 4.x).</summary>
    internal static string DebugMenuKeyInForce => s_applied?.DebugMenuKey ?? PgSettingsPage.DebugKeyOff;

    // ---- task 4.6b ----
    /// <summary>The debug menu key the page published last (any thread: the volatile s_published): the key the debug menu's UiKey postfix
    /// (window thread, src/Ui/DebugMenu.cs) answers to; the sim half checks the key in force before it toggles.</summary>
    internal static string DebugMenuKeyPublished => s_published?.DebugMenuKey ?? PgSettingsPage.DebugKeyOff;
    // ---- end task 4.6b ----

    /// <summary>
    /// From the mod's Initialize, before AddSystem: loads pg_config.json once per process (writing the shipped default file when there is
    /// none, PGConfig.cpp:851-856), registers the page with the file's values as its defaults (Meitou then sets the player's saved values),
    /// and applies the values the player changed over the file through the ui_settings reader, once. A failure leaves what loaded in force
    /// and never reaches the game.
    /// </summary>
    internal static void Start(ModContext c)
    {
        IConfigLog log = Engine.ModLog.Config;   // PGLog::Debug / Error; errors also to Meitou's log (task 2.7)
        try
        {
            if (!s_loaded)
            {
                s_loaded = true;
                // C++ PGConfig.cpp:830-836 reads PGLog::Directory(); the mod folder itself when the log could not start
                s_directory = PGLog.Directory is { Length: > 0 } d ? d : PgLogGlue.Folder(c.Folder, Path.DirectorySeparatorChar);
                s_fileValues = PgSettingsStartup.LoadFile(s_directory, new PgConfigFiles(), log);
                var page = new PGSettingsUI(s_fileValues);
                ModSettingsGroup group = c.AddSettings(page);
                group.Changed += page.OnChanged;
                s_group = group;
                c.Log($"settings page: {group.Settings.Count} rows: {string.Join(" | ", group.Settings.Select(r => r.Label))}");
                // The player's values (Meitou set them in AddSettings) go to the ui_settings reader, which refuses them with a log line when
                // this start's file was not read (C++: PGConfig.cpp:841-847); the page then shows the values in force. The player's kept
                // values stay in meitou.user.json for a start whose file reads again.
                PgSettingsValues chosen = page.Values();
                PgSettingsValues shown = PgSettingsStartup.Shown(s_fileValues, chosen);
                try { PgSettingsStartup.ApplyPage(s_fileValues, chosen, s_directory, log); }
                finally
                {
                    if (!ReferenceEquals(shown, chosen)) page.Show(shown);
                    s_applied = s_published = PgSettingsPage.Live(shown);
                    Publish(s_applied.DebugMenuKey);
                }
            }
            else if (s_fileValues is not null)
            {
                // Initialize again in the same process (not done by Meitou 0.17.3, ModHost.cs:339 builds each context once): the page again,
                // nothing reloaded; the same "file not read" rule; changes apply as after a restart only where the original applied them at once.
                var page = new PGSettingsUI(s_fileValues);
                ModSettingsGroup group = c.AddSettings(page);
                group.Changed += page.OnChanged;
                s_group = group;
                PgSettingsValues chosen = page.Values();
                PgSettingsValues shown = PgSettingsStartup.Shown(s_fileValues, chosen);
                if (!ReferenceEquals(shown, chosen)) page.Show(shown);
                s_published = PgSettingsPage.Live(shown);
            }
        }
        catch (Exception e)
        {
            c.LogError($"settings page / pg_config.json failed: {e.GetType().Name}: {e.Message}; the loaded values stay in force");
        }
    }

    /// <summary>
    /// The group's Changed (Meitou: on the interface's thread, outside the tick, ModSettings.cs:309-316): the two at-once values are published
    /// for the simulation; a value read only at start is checked with the original Save button's rules, and a page the original would have
    /// refused shows that refusal as the page's notice (a throw from a handler is shown, ModSettingsRows.cs:117-126). Never touches the world.
    /// </summary>
    void OnChanged(ModSetting setting)
    {
        PgSettingsValues now = Values();
        if (PgSettingsPage.AppliesAtOnce(setting.Key))
        {
            s_published = PgSettingsPage.Live(now);
            return;
        }
        if (!PgSettingsPage.Validate(now, out string error))
            throw new InvalidOperationException(error);
    }

    /// <summary>Serial (the system's Inputs): applies a newly published crowd preset / debug key, as the original's save did at once.</summary>
    internal static void ApplyLive()
    {
        PgLiveSettings? live = s_published;
        if (live is null || ReferenceEquals(live, s_applied)) return;
        if (s_applied is not null && live.PerformanceProfile == s_applied.PerformanceProfile && live.DebugMenuKey == s_applied.DebugMenuKey)
        { s_applied = live; return; }
        PGConfig.ApplyLiveSettings(live.PerformanceProfile, live.F8OpensDebugMenu);
        Publish(live.DebugMenuKey);   // the key now in force (s_applied still holds the previous one)
        s_applied = live;   // only once applied and published: a throw here retries next tick
        ProvingGroundsMod.Ctx?.Log($"Proving Grounds: settings page: crowd performance {ArenaPerformance.PersistedName(live.PerformanceProfile)}, debug menu key {live.DebugMenuKey} in force");
    }

    // (the line above is the port's own text, no C++ counterpart: Meitou's log, task 2.7)
    static void Publish(string debugMenuKey)
    {
        PGConfig.LoadReport report = PGConfig.Report();
        s_probe = PgSettingsPage.ProbeJson(PGConfig.GetEditableSettings(), report, debugMenuKey, s_directory + "pg_config.json");
        s_configLoaded = report.Loaded;
    }

    /// <summary>
    /// TEST ONLY <c>pg setting &lt;key&gt; &lt;value&gt;</c> (frame thread): sets one row of the page through Meitou's own
    /// <see cref="ModSetting.Set"/>, which raises the group's Changed exactly as the page's row does (Mods/ModSettings.cs:162 -> Apply :172-176
    /// -> RaiseChanged :316; the page's row: Settings/ModSettingsRows.cs:109). Unlike the page it does not keep the value in meitou.user.json
    /// (KeepModSetting, ModSettingsRows.cs:133-141, the page's own bookkeeping). For the live run of the at-once rows, which the agent's ui-set cannot reach
    /// (it moves sliders only, AgentChannel.cs:1068-1072; the crowd preset and the debug key are lists). Returns null on success, else why not.
    /// </summary>
    internal static string? SetForTest(string key, string value)
    {
        if (s_group is not { } group) return "no settings page";
        ModSetting? row = group.Settings.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));
        if (row is null) return $"no setting '{key}'";
        try { row.Set(value); return null; }
        catch (Exception e) { return $"{e.GetType().Name}: {e.GetBaseException().Message}"; }
    }
}

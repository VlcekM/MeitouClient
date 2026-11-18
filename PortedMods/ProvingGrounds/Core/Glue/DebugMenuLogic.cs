using System.Globalization;

namespace ProvingGrounds.Core.Glue;

// Port of the rules half of upstream/ProvingGrounds/plugin/src/DebugMenu.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.6b (PA §2.24;
// CAP 7.2, 7.10). Every handler, CollectCharacters, Refresh's captions / visibility / enabled states, Toggle, Close, Tick and AbandonWorldState,
// in C++ order, over IDebugMenuHost<TId>. The widgets themselves (Create's createWidget calls, Layout's arithmetic) are the window half
// (src/Ui/DebugMenu.cs, Core/Glue/DebugMenuLayout.cs); the sim half that owns this object is src/Game/DebugMenuSim.cs.

/// <summary>The debug menu's widget names: the C++'s own (DebugMenu.cpp:546-620).</summary>
public static class DebugMenuNames
{
    public const string Window = "ProvingGroundsDebugWindow", FightersTab = "PG_DebugFightersTab", DiagnosticsTab = "PG_DebugDiagnosticsTab";
    public const string Heading = "PG_DebugHeading", Prev = "PG_DebugPrev", Next = "PG_DebugNext", Page = "PG_DebugPage", Scroll = "PG_DebugScroll";
    public const string Selected = "PG_DebugSelected", Rating = "PG_DebugRating", Marks = "PG_DebugMarks";
    public const string DiagnosticSummary = "PG_DebugDiagnosticSummary", Status = "PG_DebugStatus";

    /// <summary>C++ :579 <c>PG_DebugCharacter%d</c>.</summary>
    public static string Row(int i) => "PG_DebugCharacter" + i.ToString(CultureInfo.InvariantCulture);
    /// <summary>C++ :580 <c>PG_DebugCharacterText%d</c>.</summary>
    public static string RowText(int i) => "PG_DebugCharacterText" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>C++ :586-589 the fighter page's twelve action buttons.</summary>
    public static readonly IReadOnlyList<string> Actions = ["PG_DebugMinus100", "PG_DebugMinus10", "PG_DebugPlus10",
        "PG_DebugPlus100", "PG_DebugReset", "PG_DebugMarksMinus10", "PG_DebugMarksPlus10",
        "PG_DebugMarksPlus100", "PG_DebugMarksReset", "PG_DebugTownTrial",
        "PG_DebugStopTown", "PG_DebugTownSchedule"];

    /// <summary>The first <see cref="SelectionActions"/> of <see cref="Actions"/> act on the selected character (rating and Marks, C++ :413-460);
    /// the rest (9-11) are the town buttons (:462-476).</summary>
    public const int SelectionActions = 9;

    /// <summary>C++ :607-609 the diagnostics page's nineteen action buttons.</summary>
    public static readonly IReadOnlyList<string> Diagnostics = ["PG_DiagFresh150", "PG_DiagCurrent150", "PG_DiagConfiguredFresh", "PG_DiagConfiguredCurrent",
        "PG_DiagPause", "PG_DiagEnd", "PG_DiagTarget", "PG_DiagTargetMinus", "PG_DiagTargetPlus", "PG_DiagStats", "PG_DiagReduced", "PG_DiagFull",
        "PG_DiagInfluence", "PG_DiagDuel", "PG_DiagEqual", "PG_DiagDiffOne", "PG_DiagDiffLarge", "PG_DiagUnderdog", "PG_DiagBalance"];

    /// <summary>C++ :601 / :615 a button's painted label: its name + "Text".</summary>
    public static string LabelOf(string button) => button + "Text";
}

/// <summary>Every text of the debug menu, byte for byte from DebugMenu.cpp.</summary>
public static class DebugMenuTexts
{
    public const string Caption = "Proving Grounds - Debug Menu";                                   // :549
    public const string FightersTab = "Fighters", DiagnosticsTab = "Diagnostics";                  // :555-556
    public const string HeadingFighters = "Fighter Ratings & Marks";                                // :291, :558
    public const string HeadingDiagnostics = "Controlled Matchmaking Diagnostics";                  // :291
    public const string Prev = "< Prev", Next = "Next >";                                           // :560-561
    public const string PageOne = "Page 1 / 1";                                                     // :564
    public const string SelectCharacter = "Select a character";                                     // :368, :528, :570
    public const string RatingNone = "Rating: --";                                                  // :369, :530, :571
    public const string MarksNone = "Arena Marks: --";                                              // :370, :532, :572
    public const string DiagnosticOff = "Diagnostic mode is off.";                                  // :605
    public const string StatusStart = "F8 toggles this debug menu when enabled in Proving Grounds settings. When disabled, F8 does nothing.";   // :621
    public const string Prisoner = " [PRISONER]";                                                   // :351
    public const string ChangesSaved = "Changes are included in the next Kenshi save.";             // :393
    public const string SelectFirst = "Select a character first.";                                  // :402, :422
    public const string SelectOrPrisonerFirst = "Select a character or prisoner first.";            // :435, :453
    public const string RatingUpdated = "Rating updated for the active world.";                     // :408
    public const string RatingReset = "Rating reset for the active world.";                         // :426
    public const string RatingFailed = "Could not save rating (load a game first).";                // :409, :427
    public const string MarksUpdated = "Arena Marks updated for the active world.";                 // :440
    public const string MarksReset = "Arena Marks reset for the active world.";                     // :457
    public const string MarksFailed = "Could not save Arena Marks (load a game first).";            // :441, :458
    public const string DiagnosticUnavailable = "Diagnostic action is unavailable in the current run state.";   // :480
    public const string ConfirmFresh150 = "Click the red confirmation action again to reset all town ratings and records.";   // :484
    public const string ConfirmConfigured = "Click the confirmation action again to reset town records and use this configuration.";   // :489
    public const string Fresh150Started = "Fresh 150-fight diagnostic run started.";                // :485
    public const string Current150Started = "150-fight diagnostic run started with current ratings.";   // :487
    public const string ConfiguredFreshStarted = "Configured fresh diagnostic run started.";       // :490
    public const string ConfiguredCurrentStarted = "Configured diagnostic run started with current ratings.";   // :492
    public const string Resumed = "Diagnostic run resumed.";                                        // :493
    public const string Paused = "Diagnostic selection paused; ordinary matchmaking remains active.";   // :493
    public const string Ended = "Diagnostic run ended; ordinary matchmaking remains active.";      // :494
    public const string TargetChanged = "Diagnostic target changed.";                              // :495-497
    public const string StatsChanged = "Stats-only model setting changed.";                         // :498
    public const string ReducedChanged = "Reduced-MMR model setting changed.";                      // :499
    public const string FullChanged = "Full-MMR model setting changed.";                            // :500
    public const string InfluenceChanged = "Reduced-MMR influence changed.";                        // :501
    public const string DuelChanged = "1v1 coverage setting changed.";                              // :502
    public const string EqualChanged = "Equal-group coverage setting changed.";                     // :503
    public const string DiffOneChanged = "One-fighter-difference coverage setting changed.";       // :504
    public const string DiffLargeChanged = "Large-difference coverage setting changed.";           // :505
    public const string UnderdogChanged = "Underdog distribution changed.";                         // :506
    public const string BalanceChanged = "Style/tier balancing changed.";                           // :507
    public const string Created = "Proving Grounds: debug menu created";                            // :639

    /// <summary>C++ :590-592 the fighter page's action captions, in <see cref="DebugMenuNames.Actions"/> order.</summary>
    public static readonly IReadOnlyList<string> ActionCaptions = ["Rating -100", "Rating -10", "Rating +10", "Rating +100",
        "Reset rating to 0", "Marks -10", "Marks +10", "Marks +100", "Marks 0",
        "Town 1v1", "Stop town fight", "Pause/resume ambient bouts"];

    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    static string OnOff(bool v) => v ? "ON" : "OFF";

    /// <summary>C++ :346-351: the row text, name + " [PRISONER]" + <c>"    R:%d  M:%d"</c> (the rating as <c>static_cast&lt;int&gt;(rating + 0.5f)</c>).</summary>
    public static string RowText(string name, bool prisoner, float rating, int marks) =>
        name + (prisoner ? Prisoner : "") + "    R:" + I(RoundRating(rating)) + "  M:" + I(marks);

    /// <summary>C++ :347, :378 <c>static_cast&lt;int&gt;(GetRating(c) + 0.5f)</c> (a float sum, truncated toward zero).</summary>
    public static int RoundRating(float rating) => (int)(rating + 0.5f);

    /// <summary>C++ :361 <c>"Page %d / %d"</c>.</summary>
    public static string Page(int page, int pages) => "Page " + I(page) + " / " + I(pages);
    /// <summary>C++ :377 <c>"Rating: %d"</c>.</summary>
    public static string Rating(float rating) => "Rating: " + I(RoundRating(rating));
    /// <summary>C++ :381 <c>"Arena Marks: %d"</c>.</summary>
    public static string Marks(int marks) => "Arena Marks: " + I(marks);

    /// <summary>C++ :302-320 the nineteen diagnostic captions for a run state and the page's confirmation step (<c>g_confirmFresh</c>).</summary>
    public static string[] DiagnosticLabels(in TownDiagnosticData.State d, int confirmFresh)
    {
        var c = d.Config;
        return
        [
            confirmFresh == 1 ? "CONFIRM: reset town records + start 150" : "Start Fresh 150 (resets town records)",
            "Start Current 150",
            confirmFresh == 2 ? "CONFIRM: reset + start configured" : "Start Configured Fresh",
            "Start Configured / Current Ratings",
            d.Status == (int)TownDiagnosticData.RunStatus.Paused ? "Resume Diagnostic Run" : "Pause Diagnostic Selection",
            "End Run",
            "Target preset: " + I(c.Target),
            "Target -10", "Target +10",
            "Stats-only model: " + OnOff(c.Models[0]),
            "Reduced-MMR model: " + OnOff(c.Models[1]),
            "Full-MMR model: " + OnOff(c.Models[2]),
            "Reduced MMR influence: " + CMath.FormatF(c.ReducedInfluence * 100, 0) + "%",   // %.0f%%
            "1v1 coverage: " + OnOff(c.Coverage[0]),
            "Equal-group coverage: " + OnOff(c.Coverage[1]),
            "One-fighter difference: " + OnOff(c.Coverage[2]),
            "Large roster difference: " + OnOff(c.Coverage[3]),
            "Moderate-underdog share: " + I(c.UnderdogPercent) + "%",
            "Style/tier balancing: " + OnOff(c.BalanceStyleTier),
        ];
    }

    /// <summary>C++ :322-326 which diagnostic actions are enabled for a run state.</summary>
    public static bool[] DiagnosticEnabled(in TownDiagnosticData.State d)
    {
        bool active = d.Status == (int)TownDiagnosticData.RunStatus.Active, paused = d.Status == (int)TownDiagnosticData.RunStatus.Paused;
        bool live = active || paused;
        var e = new bool[DebugMenuNames.Diagnostics.Count];
        for (int i = 0; i < 4; ++i) e[i] = !live;
        e[4] = (active && !d.Pending.Active) || paused;
        e[5] = live && !d.Pending.Active;
        for (int i = 6; i < e.Length; ++i) e[i] = !live;
        return e;
    }
}

/// <summary>What the debug menu's rules read and write (DebugMenu.cpp's engine and store calls). Sim thread.</summary>
public interface IDebugMenuHost<TId>
{
    TId None { get; }
    bool IsNone(TId c);
    /// <summary><c>c->isValid()</c>.</summary>
    bool IsValid(TId c);
    /// <summary><c>c->isDead()</c>.</summary>
    bool IsDead(TId c);
    /// <summary><c>c->getName()</c>.</summary>
    string Name(TId c);
    /// <summary>Port only (task 8.7b): a character's identity as text, carried by the drawn rows and the drawn selection so the sim can tell
    /// that a click's row still holds the character the window drew (the glue's CharacterIdText.Format).</summary>
    string Key(TId c);

    /// <summary><c>SquadUtil::CollectPlayerSquad(out)</c> (SquadUtil.cpp:22-67): the living members of the player's active squads (appends).</summary>
    void CollectPlayerSquad(List<TId> into);
    /// <summary><c>ArenaIngress::GetBoundRegistry() != NULL</c>.</summary>
    bool HasBoundRegistry();
    /// <summary><c>PrisonerUtil::HasRuntimeActivity()</c>.</summary>
    bool PrisonersHaveRuntimeActivity();
    /// <summary><c>PrisonerUtil::CollectNearbyCagePrisoners(ArenaIngress::GetBoundRegistry(), out)</c> (appends).</summary>
    void CollectNearbyCagePrisoners(List<TId> into);
    /// <summary><c>PrisonerUtil::GetRosterPrisoners()</c>.</summary>
    IReadOnlyList<TId> RosterPrisoners();
    /// <summary><c>PrisonerUtil::IsRosterPrisoner(c)</c>.</summary>
    bool IsRosterPrisoner(TId c);

    /// <summary><c>LeaderboardStore::GetRating(c)</c> (player board).</summary>
    float GetRating(TId c);
    /// <summary><c>LeaderboardStore::GetMarks(c)</c>.</summary>
    int GetMarks(TId c);
    /// <summary><c>LeaderboardStore::SetRating(c, rating)</c>.</summary>
    bool SetRating(TId c, float rating);
    /// <summary><c>LeaderboardStore::SetMarks(c, marks)</c>.</summary>
    bool SetMarks(TId c, int marks);

    /// <summary><c>TownArena::BeginTrial()</c>.</summary>
    void TownBeginTrial();
    /// <summary><c>TownArena::IsScheduleEnabled()</c>.</summary>
    bool TownIsScheduleEnabled();
    /// <summary><c>TownArena::SetScheduleEnabled(enabled)</c>.</summary>
    void TownSetScheduleEnabled(bool enabled);
    /// <summary><c>TownArena::Cancel()</c>.</summary>
    void TownCancel();
    /// <summary><c>TownArena::GetStatus()</c>.</summary>
    string TownStatus();

    /// <summary><c>TownDiagnosticRun::Get()</c>.</summary>
    TownDiagnosticData.State Diagnostic();
    /// <summary><c>TownDiagnosticRun::StatusText()</c>.</summary>
    string DiagnosticStatusText();
    /// <summary><c>TownDiagnosticRun::ValidationError()</c>.</summary>
    string DiagnosticValidationError();
    bool DiagnosticStartPreset(bool resetTownRecords);
    bool DiagnosticStartConfigured(bool resetTownRecords);
    bool DiagnosticPause();
    bool DiagnosticResume();
    bool DiagnosticEnd();
    bool DiagnosticSetTarget(int target);
    bool DiagnosticToggleModel(int model);
    bool DiagnosticCycleReducedInfluence();
    bool DiagnosticToggleCoverage(int coverage);
    bool DiagnosticCycleUnderdogPercent();
    bool DiagnosticToggleStyleTierBalance();

    /// <summary><c>GetTickCount()</c>: PgClock.NowMs.</summary>
    uint NowMs { get; }
}

/// <summary>One character row as the window shows it (DebugMenu.cpp:332-355). <see cref="Key"/>: port only (task 8.7b), the identity of the
/// row's character (<see cref="IDebugMenuHost{TId}.Key"/>; the C++ kept the index in the button's user string "index"), never shown.</summary>
public readonly record struct DebugRowView(bool Visible, string Text, bool Selected, string Key = "");

/// <summary>What a press of the visible window did (task 8.7b): <see cref="Done"/> (the C++ handler ran), <see cref="Ignored"/> (nothing the C++
/// could have received: the window closed, the widget hidden or disabled), <see cref="Stale"/> (port only: the window drew another state for that
/// widget than the sim holds now, so the click is refused; nothing changes, no status text).</summary>
public enum DebugMenuPressResult { Done, Ignored, Stale }

/// <summary>
/// What the window drew for one widget at a click (task 8.7b, the window framework's drawn-model clicks; port only: the C++ handled each click in
/// the frame it was drawn). The window's Click builds it from the model it last DREW; the sim builds it from its current state and refuses the
/// click when they differ. Per widget: a tab, nothing; Prev / Next, the drawn page label and enabled state; a row, the key of its character; a
/// rating / Marks action, the key of the selected character; the town buttons, nothing but the tab; a diagnostic action, its label and enabled
/// state (so a second Pause / Resume, "Target preset" or Fresh click before the redraw carries the old label). Every non-tab widget also carries
/// the drawn tab. The whole view's Revision is NOT compared: the diagnostics page refreshes every second.
/// </summary>
public readonly record struct DebugMenuDrawn(bool Fighters, string Shown, bool Enabled)
{
    /// <summary>What view <paramref name="v"/> shows for <paramref name="widget"/>.</summary>
    public static DebugMenuDrawn Of(DebugMenuView v, string widget)
    {
        // a tab is never stale: it acts the same from either drawn tab (no v.Fighters here)
        if (widget == DebugMenuNames.FightersTab || widget == DebugMenuNames.DiagnosticsTab) return new(false, "", true);
        if (widget == DebugMenuNames.Prev) return new(v.Fighters, v.PageLabel, v.PrevEnabled);
        if (widget == DebugMenuNames.Next) return new(v.Fighters, v.PageLabel, v.NextEnabled);
        for (int row = 0; row < v.Rows.Count; ++row)
            if (widget == DebugMenuNames.Row(row))
                return v.Rows[row].Visible ? new(v.Fighters, v.Rows[row].Key, true) : new(v.Fighters, "", false);
        for (int i = 0; i < DebugMenuNames.Actions.Count; ++i)
            if (widget == DebugMenuNames.Actions[i]) return new(v.Fighters, i < DebugMenuNames.SelectionActions ? v.SelectedKey : "", true);
        for (int i = 0; i < DebugMenuNames.Diagnostics.Count; ++i)
            if (widget == DebugMenuNames.Diagnostics[i])
                return new(v.Fighters, i < v.DiagnosticLabels.Count ? v.DiagnosticLabels[i] : "", i < v.DiagnosticEnabled.Count && v.DiagnosticEnabled[i]);
        return new(v.Fighters, "", false);
    }
}

/// <summary>The published view of the debug menu. <see cref="Revision"/> counts the C++'s Layout passes after a change of what Layout reads
/// (the tab, the rows, the measured labels); a change of the status line or of an enabled state alone keeps it.</summary>
public sealed record DebugMenuView
{
    public int Revision { get; init; }
    public bool Fighters { get; init; } = true;
    public string Heading { get; init; } = DebugMenuTexts.HeadingFighters;
    public string PageLabel { get; init; } = DebugMenuTexts.PageOne;
    public bool PrevEnabled { get; init; } = true;
    public bool NextEnabled { get; init; } = true;
    public ValueList<DebugRowView> Rows { get; init; } = DebugMenuLogic<int>.HiddenRows;
    public string SelectedName { get; init; } = DebugMenuTexts.SelectCharacter;
    public string Rating { get; init; } = DebugMenuTexts.RatingNone;
    public string Marks { get; init; } = DebugMenuTexts.MarksNone;
    /// <summary>Port only (task 8.7b): the key of the selected character ("" for none), carried by the rating / Marks clicks.</summary>
    public string SelectedKey { get; init; } = "";
    public string DiagnosticSummary { get; init; } = DebugMenuTexts.DiagnosticOff;
    public ValueList<string> DiagnosticLabels { get; init; } = DebugMenuLogic<int>.EmptyDiagnosticLabels;
    public ValueList<bool> DiagnosticEnabled { get; init; } = DebugMenuLogic<int>.AllEnabled;
    public string Status { get; init; } = DebugMenuTexts.StatusStart;
    public int RequestSerial { get; init; }
    public bool RequestOpen { get; init; }
}

/// <summary>The rules half of DebugMenu.cpp (task 4.6b). Sim thread, one object per world.</summary>
public sealed class DebugMenuLogic<TId>
{
    /// <summary>C++ :39-40 kRowsPerPage, kDiagnosticActions.</summary>
    public const int RowsPerPage = 8, DiagnosticActions = 19;
    /// <summary>C++ :664 the diagnostics page's refresh, every 1000 ms.</summary>
    public const uint DiagnosticRefreshMs = 1000;

    internal static readonly ValueList<DebugRowView> HiddenRows = new(Enumerable.Repeat(new DebugRowView(false, "", false), RowsPerPage));
    internal static readonly ValueList<string> EmptyDiagnosticLabels = new(Enumerable.Repeat("", DiagnosticActions));
    internal static readonly ValueList<bool> AllEnabled = new(Enumerable.Repeat(true, DiagnosticActions));

    enum Tab { Fighters, Diagnostics }   // C++ :41

    readonly List<TId> characters = [];   // C++ g_characters
    readonly List<TId> prisoners = [];
    TId selected;                          // C++ g_selected (none = NULL)
    int page;                              // C++ g_page
    Tab tab = Tab.Fighters;                // C++ g_tab
    int confirmFresh;                      // C++ g_confirmFresh
    uint lastDiagnosticRefresh;            // C++ g_lastDiagnosticRefresh
    bool visible;                          // C++ g_window->getVisible()
    int requestSerial, revision;

    // the captions and states Refresh writes (the widgets keep them between refreshes, as MyGUI does)
    string heading = DebugMenuTexts.HeadingFighters, pageLabel = DebugMenuTexts.PageOne;
    bool prevEnabled = true, nextEnabled = true;
    readonly DebugRowView[] rows = [.. HiddenRows];
    readonly int[] rowIndex = Enumerable.Repeat(-1, RowsPerPage).ToArray();   // the row button's user string "index"
    string selectedName = DebugMenuTexts.SelectCharacter, rating = DebugMenuTexts.RatingNone, marks = DebugMenuTexts.MarksNone;
    string selectedKey = "";   // port only (task 8.7b): what the drawn rating / Marks clicks carry
    string summary = DebugMenuTexts.DiagnosticOff;
    string[] diagnosticLabels = [.. EmptyDiagnosticLabels];
    bool[] diagnosticEnabled = [.. AllEnabled];
    string status = DebugMenuTexts.StatusStart;
    object? layoutContent;   // what the last Layout pass read

    public DebugMenuLogic(TId none) => selected = none;

    /// <summary>C++: DebugMenu.cpp:661 IsVisible.</summary>
    public bool IsVisible => visible;
    public bool OnFightersTab => tab == Tab.Fighters;
    public TId SelectedCharacter => selected;
    public int Page => page;
    public IReadOnlyList<TId> Characters => characters;
    public int ConfirmFresh => confirmFresh;

    static int Pages(int count) => count == 0 ? 1 : (count + RowsPerPage - 1) / RowsPerPage;

    // C++: DebugMenu.cpp:242-282 CollectCharacters
    void CollectCharacters(IDebugMenuHost<TId> h)
    {
        characters.Clear();
        h.CollectPlayerSquad(characters);
        prisoners.Clear();
        if (h.HasBoundRegistry() && !h.PrisonersHaveRuntimeActivity())
            h.CollectNearbyCagePrisoners(prisoners);
        else
            prisoners.AddRange(h.RosterPrisoners());
        var cmp = EqualityComparer<TId>.Default;
        foreach (var prisoner in prisoners)
        {
            if (h.IsNone(prisoner) || !h.IsValid(prisoner) || h.IsDead(prisoner)) continue;
            bool duplicate = false;
            foreach (var c in characters)
                if (cmp.Equals(c, prisoner)) { duplicate = true; break; }
            if (!duplicate) characters.Add(prisoner);
        }
        if (!h.IsNone(selected) && !h.IsValid(selected)) selected = h.None;
        int pages = Pages(characters.Count);
        if (page >= pages) page = pages - 1;
        if (page < 0) page = 0;
    }

    // C++: DebugMenu.cpp:284-385 Refresh (the window always exists here: C++ :286-287 returns only before Create)
    void Refresh(IDebugMenuHost<TId> h)
    {
        bool fighters = tab == Tab.Fighters;
        heading = fighters ? DebugMenuTexts.HeadingFighters : DebugMenuTexts.HeadingDiagnostics;
        if (!fighters)
        {
            for (int i = 0; i < RowsPerPage; ++i) rows[i] = rows[i] with { Visible = false };
            var d = h.Diagnostic();
            summary = h.DiagnosticStatusText();
            diagnosticLabels = DebugMenuTexts.DiagnosticLabels(d, confirmFresh);
            diagnosticEnabled = DebugMenuTexts.DiagnosticEnabled(d);
            Layout();
            return;
        }

        CollectCharacters(h);
        int first = page * RowsPerPage;
        for (int row = 0; row < RowsPerPage; ++row)
        {
            int index = first + row;
            if (index >= characters.Count)
            {
                rows[row] = rows[row] with { Visible = false };
                continue;
            }
            var character = characters[index];
            bool prisoner = h.IsRosterPrisoner(character);
            // C++ :346-348: GetRating before GetMarks (each logs a missing identity once per character)
            float r = h.GetRating(character);
            int m = h.GetMarks(character);
            rows[row] = new DebugRowView(true, DebugMenuTexts.RowText(h.Name(character), prisoner, r, m),
                EqualityComparer<TId>.Default.Equals(character, selected) && !h.IsNone(selected), h.Key(character));
            rowIndex[row] = index;
        }

        int pages = Pages(characters.Count);
        pageLabel = DebugMenuTexts.Page(page + 1, pages);
        prevEnabled = page > 0;
        nextEnabled = page + 1 < pages;

        selectedKey = h.IsNone(selected) ? "" : h.Key(selected);
        if (h.IsNone(selected))
        {
            selectedName = DebugMenuTexts.SelectCharacter;
            rating = DebugMenuTexts.RatingNone;
            marks = DebugMenuTexts.MarksNone;
            Layout();
            return;
        }
        selectedName = h.Name(selected);
        rating = DebugMenuTexts.Rating(h.GetRating(selected));
        marks = DebugMenuTexts.Marks(h.GetMarks(selected));
        Layout();
    }

    /// <summary>C++ Layout() (:151-230), called at the end of every Refresh: a new revision when what it reads changed.</summary>
    void Layout()
    {
        // the rows without their Key (task 8.7b review): Layout never reads it, so a character swap with the same text lays out nothing new
        var content = (tab, new ValueList<DebugRowView>(rows.Select(r => r with { Key = "" })), selectedName, rating, marks, summary, new ValueList<string>(diagnosticLabels));
        if (Equals(content, layoutContent)) return;
        layoutContent = content;
        revision++;
    }

    // C++: DebugMenu.cpp:387-396 OnSelect (the row's user string "index" from the last Refresh)
    void OnSelect(IDebugMenuHost<TId> h, int row)
    {
        int index = row >= 0 && row < RowsPerPage ? rowIndex[row] : -1;
        if (index >= 0 && index < characters.Count)
        {
            selected = characters[index];
            status = DebugMenuTexts.ChangesSaved;
            Refresh(h);
        }
    }

    // C++: DebugMenu.cpp:398-411 ChangeRating
    void ChangeRating(IDebugMenuHost<TId> h, float delta)
    {
        if (h.IsNone(selected)) { status = DebugMenuTexts.SelectFirst; return; }
        float next = h.GetRating(selected) + delta;
        status = h.SetRating(selected, next) ? DebugMenuTexts.RatingUpdated : DebugMenuTexts.RatingFailed;
        Refresh(h);
    }

    // C++: DebugMenu.cpp:418-429 OnReset
    void OnReset(IDebugMenuHost<TId> h)
    {
        if (h.IsNone(selected)) { status = DebugMenuTexts.SelectFirst; return; }
        status = h.SetRating(selected, 0.0f) ? DebugMenuTexts.RatingReset : DebugMenuTexts.RatingFailed;
        Refresh(h);
    }

    // C++: DebugMenu.cpp:431-443 ChangeMarks
    void ChangeMarks(IDebugMenuHost<TId> h, int delta)
    {
        if (h.IsNone(selected)) { status = DebugMenuTexts.SelectOrPrisonerFirst; return; }
        int next = h.GetMarks(selected) + delta;
        status = h.SetMarks(selected, next) ? DebugMenuTexts.MarksUpdated : DebugMenuTexts.MarksFailed;
        Refresh(h);
    }

    // C++: DebugMenu.cpp:449-460 OnMarksReset
    void OnMarksReset(IDebugMenuHost<TId> h)
    {
        if (h.IsNone(selected)) { status = DebugMenuTexts.SelectOrPrisonerFirst; return; }
        status = h.SetMarks(selected, 0) ? DebugMenuTexts.MarksReset : DebugMenuTexts.MarksFailed;
        Refresh(h);
    }

    // C++: DebugMenu.cpp:479-482 DiagnosticFeedback
    void DiagnosticFeedback(IDebugMenuHost<TId> h, bool ok, string success)
    {
        string error = ok ? "" : h.DiagnosticValidationError();
        status = ok ? success : error.Length == 0 ? DebugMenuTexts.DiagnosticUnavailable : error;
        if (ok) confirmFresh = 0;
        Refresh(h);
    }

    void Confirm(IDebugMenuHost<TId> h, int step, string text)
    {
        confirmFresh = step;
        status = text;
        Refresh(h);
    }

    /// <summary>
    /// The tests' convenience (nothing in src calls it): a button press of the visible window as if the window drew the current state, i.e. the
    /// press with <see cref="DebugMenuDrawn.Of"/> of this object's own view. False when it was not <see cref="DebugMenuPressResult.Done"/>.
    /// </summary>
    public bool Press(IDebugMenuHost<TId> h, string widget) => Press(h, widget, DebugMenuDrawn.Of(BuildView(), widget)) == DebugMenuPressResult.Done;

    /// <summary>
    /// A button press of the visible window (its <c>eventMouseButtonClick</c> handler, DebugMenu.cpp:387-509; the row and action handlers in C++
    /// order) with what the window drew for that widget (<paramref name="drawn"/>, task 8.7b). <see cref="DebugMenuPressResult.Ignored"/> when the
    /// press reaches nothing the C++ could have received: the window closed, the widget hidden (the other tab's buttons, a row with no character)
    /// or disabled (MyGUI sends no click to a disabled widget); <see cref="DebugMenuPressResult.Stale"/> (port only) when the window drew another
    /// state for the widget than this object holds now: nothing changes, no status text.
    /// </summary>
    public DebugMenuPressResult Press(IDebugMenuHost<TId> h, string widget, DebugMenuDrawn drawn)
    {
        if (!Reachable(widget)) return DebugMenuPressResult.Ignored;
        if (DebugMenuDrawn.Of(BuildView(), widget) != drawn) return DebugMenuPressResult.Stale;
        Act(h, widget);
        return DebugMenuPressResult.Done;
    }

    /// <summary>Whether a press of <paramref name="widget"/> reaches a handler: the window visible, the widget shown on this tab and enabled.</summary>
    bool Reachable(string widget)
    {
        if (!visible) return false;
        bool fighters = tab == Tab.Fighters;
        if (widget == DebugMenuNames.FightersTab || widget == DebugMenuNames.DiagnosticsTab) return true;
        if (widget == DebugMenuNames.Prev) return fighters && prevEnabled;
        if (widget == DebugMenuNames.Next) return fighters && nextEnabled;
        for (int row = 0; row < RowsPerPage; ++row)
            if (widget == DebugMenuNames.Row(row)) return fighters && rows[row].Visible;
        if (IndexOf(DebugMenuNames.Actions, widget) >= 0) return fighters;
        int diagnostic = IndexOf(DebugMenuNames.Diagnostics, widget);
        return diagnostic >= 0 && !fighters && diagnosticEnabled[diagnostic];
    }

    /// <summary>The handler of a reachable widget (<see cref="Reachable"/>), in C++ order.</summary>
    void Act(IDebugMenuHost<TId> h, string widget)
    {
        switch (widget)
        {
            case DebugMenuNames.FightersTab:   // C++ :477
                tab = Tab.Fighters; confirmFresh = 0; Refresh(h); return;
            case DebugMenuNames.DiagnosticsTab:   // C++ :478
                tab = Tab.Diagnostics; confirmFresh = 0; Refresh(h); return;
            case DebugMenuNames.Prev:   // C++ :508
                if (page > 0) --page;
                Refresh(h);
                return;
            case DebugMenuNames.Next:   // C++ :509
                ++page;
                Refresh(h);
                return;
        }
        for (int row = 0; row < RowsPerPage; ++row)
            if (widget == DebugMenuNames.Row(row))
            {
                OnSelect(h, row);
                return;
            }
        int action = IndexOf(DebugMenuNames.Actions, widget);
        if (action >= 0)
        {
            switch (action)
            {
                case 0: ChangeRating(h, -100.0f); break;   // C++ :413
                case 1: ChangeRating(h, -10.0f); break;    // :414
                case 2: ChangeRating(h, 10.0f); break;     // :415
                case 3: ChangeRating(h, 100.0f); break;    // :416
                case 4: OnReset(h); break;                 // :418-429
                case 5: ChangeMarks(h, -10); break;        // :445
                case 6: ChangeMarks(h, 10); break;         // :446
                case 7: ChangeMarks(h, 100); break;        // :447
                case 8: OnMarksReset(h); break;            // :449-460
                case 9: h.TownBeginTrial(); status = h.TownStatus(); break;                                // :462-466
                case 10: h.TownCancel(); status = h.TownStatus(); break;                                   // :472-476
                default: h.TownSetScheduleEnabled(!h.TownIsScheduleEnabled()); status = h.TownStatus(); break;   // :467-471
            }
            return;
        }
        int diagnostic = IndexOf(DebugMenuNames.Diagnostics, widget);
        int target = h.Diagnostic().Config.Target;
        switch (diagnostic)
        {
            case 0:   // C++ :483-486
                if (confirmFresh != 1) { Confirm(h, 1, DebugMenuTexts.ConfirmFresh150); break; }
                DiagnosticFeedback(h, h.DiagnosticStartPreset(true), DebugMenuTexts.Fresh150Started); break;
            case 1: DiagnosticFeedback(h, h.DiagnosticStartPreset(false), DebugMenuTexts.Current150Started); break;   // :487
            case 2:   // C++ :488-491
                if (confirmFresh != 2) { Confirm(h, 2, DebugMenuTexts.ConfirmConfigured); break; }
                DiagnosticFeedback(h, h.DiagnosticStartConfigured(true), DebugMenuTexts.ConfiguredFreshStarted); break;
            case 3: DiagnosticFeedback(h, h.DiagnosticStartConfigured(false), DebugMenuTexts.ConfiguredCurrentStarted); break;   // :492
            case 4:   // C++ :493
                bool paused = h.Diagnostic().Status == (int)TownDiagnosticData.RunStatus.Paused;
                DiagnosticFeedback(h, paused ? h.DiagnosticResume() : h.DiagnosticPause(), paused ? DebugMenuTexts.Resumed : DebugMenuTexts.Paused); break;
            case 5: DiagnosticFeedback(h, h.DiagnosticEnd(), DebugMenuTexts.Ended); break;   // :494
            case 6:   // C++ :495
                DiagnosticFeedback(h, h.DiagnosticSetTarget(target < 6 ? 6 : target < 50 ? 50 : target < 150 ? 150 : target < 300 ? 300 : 6),
                    DebugMenuTexts.TargetChanged); break;
            case 7: DiagnosticFeedback(h, h.DiagnosticSetTarget(Math.Max(1, target - 10)), DebugMenuTexts.TargetChanged); break;   // :496
            case 8: DiagnosticFeedback(h, h.DiagnosticSetTarget(target + 10), DebugMenuTexts.TargetChanged); break;   // :497
            case 9: DiagnosticFeedback(h, h.DiagnosticToggleModel(0), DebugMenuTexts.StatsChanged); break;   // :498
            case 10: DiagnosticFeedback(h, h.DiagnosticToggleModel(1), DebugMenuTexts.ReducedChanged); break;   // :499
            case 11: DiagnosticFeedback(h, h.DiagnosticToggleModel(2), DebugMenuTexts.FullChanged); break;   // :500
            case 12: DiagnosticFeedback(h, h.DiagnosticCycleReducedInfluence(), DebugMenuTexts.InfluenceChanged); break;   // :501
            case 13: DiagnosticFeedback(h, h.DiagnosticToggleCoverage(0), DebugMenuTexts.DuelChanged); break;   // :502
            case 14: DiagnosticFeedback(h, h.DiagnosticToggleCoverage(1), DebugMenuTexts.EqualChanged); break;   // :503
            case 15: DiagnosticFeedback(h, h.DiagnosticToggleCoverage(2), DebugMenuTexts.DiffOneChanged); break;   // :504
            case 16: DiagnosticFeedback(h, h.DiagnosticToggleCoverage(3), DebugMenuTexts.DiffLargeChanged); break;   // :505
            case 17: DiagnosticFeedback(h, h.DiagnosticCycleUnderdogPercent(), DebugMenuTexts.UnderdogChanged); break;   // :506
            default: DiagnosticFeedback(h, h.DiagnosticToggleStyleTierBalance(), DebugMenuTexts.BalanceChanged); break;   // :507
        }
    }

    static int IndexOf(IReadOnlyList<string> names, string widget)
    {
        for (int i = 0; i < names.Count; ++i) if (names[i] == widget) return i;
        return -1;
    }

    /// <summary>C++: DebugMenu.cpp:642-658 Toggle (Create is the window side's: built once when the screens load).</summary>
    public void Toggle(IDebugMenuHost<TId> h)
    {
        bool show = !visible;
        SetVisible(show);
        if (show) Refresh(h);
    }

    /// <summary>C++: DebugMenu.cpp:660 Close.</summary>
    public void Close() => SetVisible(false);

    /// <summary>C++: DebugMenu.cpp:511-515 OnWindowButtonPressed ("close"), and Meitou's own Escape (ArenaUI.cpp:3004-3007 PollEscClose closes the
    /// debug menu): the window side reports the window closed under request <paramref name="serial"/>. False for another request or a closed window.</summary>
    public bool OnClosedFromWindow(int serial)
    {
        if (!visible || serial != requestSerial) return false;
        visible = false;
        return true;
    }

    /// <summary>C++: DebugMenu.cpp:662-666 Tick: the diagnostics page of the visible window refreshes every 1000 ms. True when it refreshed.</summary>
    public bool Tick(IDebugMenuHost<TId> h)
    {
        if (!visible || tab != Tab.Diagnostics) return false;
        uint now = h.NowMs;
        if (lastDiagnosticRefresh != 0 && unchecked(now - lastDiagnosticRefresh) < DiagnosticRefreshMs) return false;
        lastDiagnosticRefresh = now;
        Refresh(h);
        return true;
    }

    /// <summary>C++: DebugMenu.cpp:520-533 AbandonWorldState.</summary>
    public void AbandonWorldState(IDebugMenuHost<TId> h)
    {
        characters.Clear();
        selected = h.None;
        selectedKey = "";
        page = 0;
        SetVisible(false);
        selectedName = DebugMenuTexts.SelectCharacter;
        rating = DebugMenuTexts.RatingNone;
        marks = DebugMenuTexts.MarksNone;
    }

    void SetVisible(bool show)
    {
        if (visible == show) return;
        visible = show;
        requestSerial++;
    }

    public DebugMenuView BuildView() => new()
    {
        Revision = revision, Fighters = tab == Tab.Fighters, Heading = heading, PageLabel = pageLabel, PrevEnabled = prevEnabled,
        NextEnabled = nextEnabled, Rows = new(rows), SelectedName = selectedName, Rating = rating, Marks = marks, SelectedKey = selectedKey,
        DiagnosticSummary = summary,
        DiagnosticLabels = new(diagnosticLabels), DiagnosticEnabled = new(diagnosticEnabled), Status = status,
        RequestSerial = requestSerial, RequestOpen = visible,
    };
}

/// <summary>Whether the "Debug menu key" press is the debug menu's (task 4.6b; CAP 7.10, QuestLog's hotkey rule). The original polled F8 with
/// GetAsyncKeyState every frame (ArenaUI.cpp:2967-2976); Meitou's F8 is its screenshot key (overview §1), so the setting names a letter and the
/// key is taken only where the game left it unused.</summary>
public static class DebugMenuKeyRule
{
    /// <summary>The press of <paramref name="key"/> (a Meitou key name) toggles the menu when the setting names that key (not Off), Ctrl is not
    /// held, the game did not take the key (UiKey's result), the interface shows, the title does not, the host does not keep the key for itself
    /// and the player did not bind it to a game action, and no edit box has the keyboard.</summary>
    public static bool Takes(string setting, string key, bool ctrl, bool handled, bool uiVisible, bool titleOpen, bool reserved, bool editing) =>
        KeyName(setting) is { } name && key == name && !ctrl && !handled && uiVisible && !titleOpen && !reserved && !editing;

    /// <summary>The Meitou key name (<c>Meitou.Game.Host.Input.Key</c> member) a "Debug menu key" setting names: O, N or V; null for Off or
    /// anything else (PgSettingsPage.DebugKeyOptions).</summary>
    public static string? KeyName(string setting) => setting switch { "O" => "O", "N" => "N", "V" => "V", _ => null };
}

/// <summary>The debug menu key's presses waiting for the frame loop's PollDebugMenuKey slot (task 4.6b review). The original toggled in the frame
/// of each press (PollF8Toggle, one toggle per key-down edge, ArenaUI.cpp:2967-2976); Meitou applies the key's command in the next unpaused tick
/// (mod commands wait while the game is paused, CAP 7.0), so presses made meanwhile are counted and each one toggles then.</summary>
public sealed class DebugMenuKeyPresses
{
    int pending;

    public int Pending => pending;

    /// <summary>A press arrived (the key's command).</summary>
    public void Press() => pending++;

    /// <summary>The slot: how many toggles to make now (each waiting press when the setting is on, none when it is off); the count is cleared
    /// either way.</summary>
    public int Take(bool settingOn)
    {
        int n = pending;
        pending = 0;
        return settingOn ? n : 0;
    }
}

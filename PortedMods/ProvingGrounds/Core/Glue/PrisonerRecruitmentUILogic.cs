using System.Globalization;

namespace ProvingGrounds.Core.Glue;

// Port of the rules half of upstream/ProvingGrounds/plugin/src/PrisonerRecruitmentUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 6.4a (PA §2.19;
// CAP 7.0-7.4, 15). Everything PrisonerRecruitmentUI.cpp decides, holds as state or writes as text, without the game, so it is tested over a fake host
// (Tests/Glue/PrisonerRecruitmentUILogicTests.cs): which prisoners are listed (RefreshCandidates :212-233, Contains :124-130, SameCandidates
// :132-140), the selection (Select :153-160, OnRowClicked :162-172), the button and its explanation (PresentationState :235-251 over Core
// PrisonerRecruitmentPresentation), the details and the rows' Marks (UpdateDetails :253-297), the status line (g_message), the recruit click
// (OnRecruitClicked :465-473), Show / Close / IsVisible / Tick / AbandonWorldState (:591-647), the refresh (RefreshAll :561-586) and the window's
// recovery from a failure (the catch blocks -> AbandonWindow :446-463). The window itself (Passive :80-91, BodyHeight :93-96, WrappedHeight :98-107,
// BindPortrait :109-122, ClearRows / RebuildRows' widgets :142-148 / :174-210, LayoutRows :299-337, LayoutDetails :339-365, FitWindowToScreen
// :367-380, LayoutWindow :382-444, EnsureWindow :486-559) is task 6.4b's window half. The sim half (src/Game/PrisonerRecruitmentUISim.cs) owns this
// object and implements the host over the world.
//
// The recruit itself is PrisonerRecruitment::Recruit (Core/Glue/PrisonerRecruitmentGlue.cs through the host), run here in the sim on the window's
// RecruitPrisoner command: the C++ ran it in the click; a command arrives later, so one whose prisoner or price changed since the window showed it
// is not run (OnRecruitCommand).

/// <summary>What the rules half of PrisonerRecruitmentUI reads (the sim half implements it over the world: PrisonerUtil, ArenaIngress,
/// SparSession, LeaderboardStore, PrisonerRecruitment, ArenaUI).</summary>
public interface IPrisonerRecruitmentUIHost<TId>
{
    /// <summary><c>PrisonerUtil::GetRosterPrisoners()</c> in its order.</summary>
    IReadOnlyList<TId> RosterPrisoners();
    /// <summary><c>c &amp;&amp; c-&gt;isValid()</c>.</summary>
    bool Valid(TId c);
    /// <summary><c>c-&gt;isDead()</c>.</summary>
    bool IsDead(TId c);
    /// <summary><c>c-&gt;isUnconcious()</c>.</summary>
    bool IsUnconscious(TId c);
    /// <summary><c>PrisonerUtil::IsRosterPrisoner(c)</c>.</summary>
    bool IsRosterPrisoner(TId c);
    /// <summary><c>ArenaIngress::IsPending()</c>.</summary>
    bool IngressPending();
    /// <summary><c>SparSession::IsActive()</c>.</summary>
    bool SparActive();
    /// <summary><c>PrisonerUtil::HasRuntimeActivity()</c>.</summary>
    bool PrisonerRuntimeActivity();
    /// <summary><c>ou &amp;&amp; ou-&gt;player</c>.</summary>
    bool PlayerReady();
    /// <summary>Port only: the Recruitment part is on (the engine's recruit can be called).</summary>
    bool RecruitAvailable();
    /// <summary><c>LeaderboardStore::GetMarks(c)</c>.</summary>
    int GetMarks(TId c);
    /// <summary><c>PrisonerRecruitment::AverageCombatStat(c)</c>.</summary>
    float AverageCombatStat(TId c);
    /// <summary><c>PrisonerRecruitment::RequiredMarks(c)</c>.</summary>
    int RequiredMarks(TId c);
    /// <summary><c>c-&gt;getName()</c>.</summary>
    string Name(TId c);
    /// <summary><c>PrisonerRecruitment::Recruit(c, status)</c>.</summary>
    bool Recruit(TId c, out string status);
    /// <summary><c>registry &amp;&amp; registry-&gt;isValid()</c> for <c>ArenaIngress::GetBoundRegistry()</c>.</summary>
    bool BoundRegistryValid();
    /// <summary><c>ArenaUI::ShowFromRegistry(ArenaIngress::GetBoundRegistry(), false)</c>.</summary>
    void ShowArenaFromBoundRegistry();
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);
    /// <summary><c>PGLog::Error</c>.</summary>
    void Error(string line);
}

/// <summary>Texts of the recruitment window the C++ writes at creation or in its handlers (byte for byte).</summary>
public static class PrisonerRecruitmentUiTexts
{
    /// <summary>C++ :499 the window's caption.</summary>
    public const string Caption = "Proving Grounds - Prisoner Recruitment";
    /// <summary>C++ :508 the hidden metrics label (its font height is the body height).</summary>
    public const string Metrics = "Recruitment";
    /// <summary>C++ :513-514 the subtitle (two spaces either side of the bar).</summary>
    public const string Subtitle = "CONTRACTS OF FREEDOM  |  Prisoners spend their own Arena Marks";
    /// <summary>C++ :518 the roster heading.</summary>
    public const string RosterHeading = "PRISONERS";
    /// <summary>C++ :530 the selected name at creation, :279 with no selection.</summary>
    public const string NoPrisonerSelected = "No prisoner selected";
    /// <summary>C++ :280 the stats line with no selection.</summary>
    public const string NoContract = "No recruitment contract available.";
    /// <summary>C++ :537 the close button.</summary>
    public const string Close = "Back to Arena";
    /// <summary>C++ :541 the action button at creation.</summary>
    public const string ActionInitial = "No Prisoner Selected";
    /// <summary>C++ :295 the status line with no message.</summary>
    public const string StatusIdle = "Select a prisoner to review their freedom contract.";
    /// <summary>Port only (divergences.md "PrisonerRecruitment"): the button when the Recruitment part is off and the C++ rules would enable it.</summary>
    public const string UnavailableCaption = "Recruitment Unavailable";
    /// <summary>C++ :265-266 the stats line: <c>"Combat %.0f  |  %d / %d Arena Marks"</c> (the float through msvcr100 %.0f).</summary>
    public static string Stats(float combat, int marks, int required) =>
        "Combat " + CMath.FormatF(combat, 0) + "  |  " + I(marks) + " / " + I(required) + " Arena Marks";
    /// <summary>C++ :289-290 a row's Marks: <c>"%d Marks"</c>.</summary>
    public static string RowMarks(int marks) => I(marks) + " Marks";
    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One prisoner row (RebuildRows :174-210: the character and its name; UpdateDetails :285-293: the Marks caption and the selection).
/// <see cref="Index"/> = the row's place in the candidates (the C++ names the row's widgets <c>PG_RecruitmentRow_%d</c> with it; task 6.4b).</summary>
public readonly record struct PrisonerRecruitmentRowView<TId>(TId Character, int Index, string Name, string Marks, bool Selected);

/// <summary>The published view of the recruitment window (task 6.4a; the window half, 6.4b, binds it). Value equality: the sim half publishes a new
/// model only when it changed.</summary>
public sealed record PrisonerRecruitmentUiView<TId>
{
    /// <summary>Open / close requests (copied onto the published PgUiModel, as the other windows do).</summary>
    public int RequestSerial { get; init; }
    public bool RequestOpen { get; init; }
    /// <summary>Counts Shows: the window puts both scroll canvases at the top (Show :597-598).</summary>
    public int Shows { get; init; }
    /// <summary>Counts RebuildRows (the candidates changed, RefreshCandidates :230-231): the window rebuilds its rows and lays the window out
    /// again (RefreshAll :571-574).</summary>
    public int RowBuilds { get; init; }
    public string Caption { get; init; } = PrisonerRecruitmentUiTexts.Caption;
    public string Metrics { get; init; } = PrisonerRecruitmentUiTexts.Metrics;
    public string Subtitle { get; init; } = PrisonerRecruitmentUiTexts.Subtitle;
    public string RosterHeading { get; init; } = PrisonerRecruitmentUiTexts.RosterHeading;
    public string CloseCaption { get; init; } = PrisonerRecruitmentUiTexts.Close;
    public ValueList<PrisonerRecruitmentRowView<TId>> Rows { get; init; } = ValueList<PrisonerRecruitmentRowView<TId>>.Empty;
    /// <summary>C++ g_selected (none: false).</summary>
    public bool HasSelected { get; init; }
    public TId Selected { get; init; } = default!;
    /// <summary>The detail portrait (UpdateDetails :271-282: shown for a valid selection, hidden otherwise; the binding is the window half's).</summary>
    public bool PortraitVisible { get; init; }
    public string SelectedName { get; init; } = PrisonerRecruitmentUiTexts.NoPrisonerSelected;
    public string Stats { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string ActionCaption { get; init; } = PrisonerRecruitmentUiTexts.ActionInitial;
    public bool ActionEnabled { get; init; } = true;
    public string Status { get; init; } = "";
    /// <summary>The price the button shows for the selection (RequiredMarks; 0 with none): the window sends it with its RecruitPrisoner command,
    /// and the sim refuses a command whose price changed.</summary>
    public int ShownPrice { get; init; }
    /// <summary>The last RecruitPrisoner command applied (its sequence; 0 for none) and whether it was not run because its prisoner or price changed
    /// since it was shown (the status line is then left as it was).</summary>
    public long LastSeq { get; init; }
    public bool LastOfferChanged { get; init; }
}

/// <summary>The rules half of PrisonerRecruitmentUI.cpp. Sim thread (one object per world).</summary>
public sealed class PrisonerRecruitmentUILogic<TId>
{
    static readonly EqualityComparer<TId> Eq = EqualityComparer<TId>.Default;

    // ---- C++ globals (:66-75) that are not widgets ----
    readonly List<TId> candidates = [];   // g_candidates
    bool hasSelected;                     // g_selected != NULL
    TId selected = default!;
    string message = "";                  // g_message
    bool alive;                           // g_window != NULL (EnsureWindow made it; AbandonWindow destroys it)
    bool visible;                         // g_window->getVisible()

    // ---- the widget captions and flags UpdateDetails writes (their creation captions until then) ----
    readonly List<(string Name, string Marks, bool Selected)> rows = [];
    string selectedName = PrisonerRecruitmentUiTexts.NoPrisonerSelected, stats = "", explanation = "";
    string actionCaption = PrisonerRecruitmentUiTexts.ActionInitial, status = "";
    bool actionEnabled = true, portraitVisible;
    int shownPrice;

    int requestSerial, shows, rowBuilds;
    long lastSeq;
    bool lastOfferChanged;

    /// <summary>C++: PrisonerRecruitmentUI.cpp:617-620 IsVisible.</summary>
    public bool IsVisible => alive && visible;
    /// <summary>C++ g_selected (none: <see cref="HasSelected"/> false).</summary>
    public bool HasSelected => hasSelected;
    public TId Selected => selected;
    /// <summary>C++ g_message.</summary>
    public string Message => message;

    // C++: PrisonerRecruitmentUI.cpp:124-130
    static bool Contains(List<TId> values, bool has, TId character)
    {
        if (!has) return false;   // Contains(values, NULL): no entry is NULL (RefreshCandidates skips it)
        for (int i = 0; i < values.Count; ++i)
            if (Eq.Equals(values[i], character))
                return true;
        return false;
    }

    // C++: PrisonerRecruitmentUI.cpp:132-140
    bool SameCandidates(List<TId> values)
    {
        if (values.Count != candidates.Count)
            return false;
        for (int i = 0; i < values.Count; ++i)
            if (!Eq.Equals(values[i], candidates[i]))
                return false;
        return true;
    }

    // C++: PrisonerRecruitmentUI.cpp:153-160
    void Select(IPrisonerRecruitmentUIHost<TId> h, bool has, TId character)
    {
        if (!Contains(candidates, has, character))
            has = false;
        hasSelected = has;
        selected = has ? character : default!;
        message = "";
        RefreshAll(h, false);
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:162-172 OnRowClicked on the row that shows <paramref name="prisoner"/>. False: no row shows it
    /// (ignored, as a click on no row finds no row).</summary>
    public bool OnRowClicked(IPrisonerRecruitmentUIHost<TId> h, TId prisoner)
    {
        if (!alive) return false;
        for (int i = 0; i < candidates.Count; ++i)
        {
            if (Eq.Equals(candidates[i], prisoner))
            {
                Select(h, true, candidates[i]);
                return true;
            }
        }
        return false;
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:174-210 RebuildRows: one row per candidate with its name (the widgets are the window half's).</summary>
    void RebuildRows(IPrisonerRecruitmentUIHost<TId> h)
    {
        rows.Clear();   // ClearRows :142-148
        for (int i = 0; i < candidates.Count; ++i)
            rows.Add((h.Name(candidates[i]), "", false));
        rowBuilds++;
    }

    // C++: PrisonerRecruitmentUI.cpp:212-233
    bool RefreshCandidates(IPrisonerRecruitmentUIHost<TId> h)
    {
        var found = new List<TId>();
        IReadOnlyList<TId> roster = h.RosterPrisoners();
        for (int i = 0; i < roster.Count; ++i)
        {
            TId prisoner = roster[i];
            if (!h.Valid(prisoner) || h.IsDead(prisoner) ||
                Contains(found, true, prisoner))
                continue;
            found.Add(prisoner);
        }

        bool changed = !SameCandidates(found);
        candidates.Clear();
        candidates.AddRange(found);
        if (!Contains(candidates, hasSelected, selected))
        {
            hasSelected = candidates.Count != 0;
            selected = hasSelected ? candidates[0] : default!;
        }
        if (changed)
            RebuildRows(h);
        return changed;
    }

    bool SelectedValid(IPrisonerRecruitmentUIHost<TId> h) => hasSelected && h.Valid(selected);   // g_selected && g_selected->isValid()

    /// <summary>C++: PrisonerRecruitmentUI.cpp:235-251 PresentationState. Port only: when the Recruitment part is off and the C++ rules would
    /// enable the button, it is disabled with <see cref="PrisonerRecruitmentUiTexts.UnavailableCaption"/> and the recruit's own refusal text.</summary>
    PrisonerRecruitmentPresentation.State PresentationState(IPrisonerRecruitmentUIHost<TId> h)
    {
        var input = new PrisonerRecruitmentPresentation.Input();
        input.HasSelection = SelectedValid(h);
        input.LivingRosterPrisoner = input.HasSelection &&
            !h.IsDead(selected) && h.IsRosterPrisoner(selected);
        input.Busy = h.IngressPending() || h.SparActive() ||
            h.PrisonerRuntimeActivity();
        input.Unconscious = input.HasSelection && h.IsUnconscious(selected);
        input.PlayerReady = h.PlayerReady();
        if (input.HasSelection)
        {
            input.CurrentMarks = h.GetMarks(selected);
            input.RequiredMarks = h.RequiredMarks(selected);
        }
        var state = PrisonerRecruitmentPresentation.Evaluate(input);
        if (state.Enabled && !h.RecruitAvailable())
        {
            state.Enabled = false;
            state.ActionCaption = PrisonerRecruitmentUiTexts.UnavailableCaption;
            state.Explanation = PrisonerRecruitmentGlue<TId>.UnavailableText;
        }
        return state;
    }

    // C++: PrisonerRecruitmentUI.cpp:253-297
    void UpdateDetails(IPrisonerRecruitmentUIHost<TId> h)
    {
        PrisonerRecruitmentPresentation.State state =
            PresentationState(h);
        actionCaption = state.ActionCaption ?? "";
        actionEnabled = state.Enabled;
        explanation = state.Explanation ?? "";

        if (SelectedValid(h))
        {
            selectedName = h.Name(selected);
            int required = h.RequiredMarks(selected);
            stats = PrisonerRecruitmentUiTexts.Stats(
                h.AverageCombatStat(selected),
                h.GetMarks(selected),
                required);
            shownPrice = required;
            portraitVisible = true;   // BindPortrait(g_portrait, g_selected) when it changed: the window half's
        }
        else
        {
            selectedName = PrisonerRecruitmentUiTexts.NoPrisonerSelected;
            stats = PrisonerRecruitmentUiTexts.NoContract;
            portraitVisible = false;
            shownPrice = 0;
        }

        for (int i = 0; i < rows.Count; ++i)
        {
            TId character = candidates[i];
            rows[i] = (rows[i].Name, PrisonerRecruitmentUiTexts.RowMarks(h.GetMarks(character)), hasSelected && Eq.Equals(character, selected));
        }
        status = message.Length == 0
            ? PrisonerRecruitmentUiTexts.StatusIdle
            : message;
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:446-463 AbandonWindow (the window destroyed: a close request; the next Show creates it again).</summary>
    void AbandonWindow()
    {
        if (alive && visible) requestSerial++;
        alive = false;
        visible = false;
        rows.Clear();
        candidates.Clear();
        hasSelected = false;
        selected = default!;
        message = "";
        selectedName = PrisonerRecruitmentUiTexts.NoPrisonerSelected;
        stats = explanation = status = "";
        actionCaption = PrisonerRecruitmentUiTexts.ActionInitial;
        actionEnabled = true;
        portraitVisible = false;
        shownPrice = 0;
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:465-473 OnRecruitClicked (run in the C++ click). Use <see cref="OnRecruitCommand"/> for the window's
    /// command.</summary>
    void OnRecruitClicked(IPrisonerRecruitmentUIHost<TId> h)
    {
        if (!SelectedValid(h))
            return;
        h.Recruit(selected, out string result);
        message = result;
        RefreshAll(h, true);
    }

    /// <summary>The window's "Grant Freedom" button as one command (port, task 6.4a): <paramref name="prisoner"/> = the selection the window
    /// showed and <paramref name="price"/> its shown price (<see cref="PrisonerRecruitmentUiView{TId}.ShownPrice"/>). The C++ recruited
    /// <c>g_selected</c> in the click; a command is applied later, so one whose prisoner is no longer the selection or whose price changed (a second
    /// click before the window saw the first one's result, a roster change) is NOT run: no status, a debug line, <c>LastOfferChanged</c>. A command
    /// that arrives while the window is closed is ignored (the C++ handler is reachable only through the visible window). Every command with a
    /// sequence above <c>LastSeq</c> is recorded as <c>LastSeq</c>, so the window's wait for its result always ends; one at or below it is dropped
    /// (the window numbers its requests above the model's LastSeq). True when the recruit ran (whatever it answered).</summary>
    public bool OnRecruitCommand(IPrisonerRecruitmentUIHost<TId> h, TId prisoner, int price, long seq)
    {
        if (seq <= lastSeq) return false;   // review fix: an old or repeated request (the model already answered a later one): dropped, not recorded
        lastSeq = seq;
        lastOfferChanged = false;
        if (!IsVisible) return false;
        if (!SelectedValid(h)) return false;   // C++ :467-468
        int now = h.RequiredMarks(selected);
        if (!Eq.Equals(selected, prisoner) || now != price)
        {
            lastOfferChanged = true;
            h.Debug("Proving Grounds: prisoner recruitment skipped: the contract changed since it was shown (price "
                + price.ToString(CultureInfo.InvariantCulture) + " -> " + now.ToString(CultureInfo.InvariantCulture)
                + (Eq.Equals(selected, prisoner) ? "" : ", another prisoner selected") + ")");
            return false;
        }
        OnRecruitClicked(h);
        return true;
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:475-478 OnCloseClicked and :480-484 OnWindowButtonPressed ("close"), Escape (ArenaUI.cpp:2994-2997):
    /// all three are <c>PrisonerRecruitmentUI::Close()</c>.</summary>
    public void OnCloseClicked(IPrisonerRecruitmentUIHost<TId> h) => Close(h);

    /// <summary>The window side reports the window closed (the title bar's close, Escape) under request <paramref name="serial"/>: Close as the C++
    /// (OnWindowButtonPressed :480-484, ArenaUI.cpp:2994-2997), without a second close request. False for another request or a hidden window.</summary>
    public bool OnClosedFromWindow(IPrisonerRecruitmentUIHost<TId> h, int serial)
    {
        if (!IsVisible || serial != requestSerial) return false;
        visible = false;   // the window side closed it itself
        ReopenArena(h);
        return true;
    }

    // C++: PrisonerRecruitmentUI.cpp:561-586
    void RefreshAll(IPrisonerRecruitmentUIHost<TId> h, bool preserveMessage)
    {
        if (!alive)
            return;
        if (!preserveMessage)
            message = "";
        try
        {
            RefreshCandidates(h);   // candidatesChanged -> LayoutWindow, else LayoutRows + LayoutDetails: the window half's (RowBuilds)
            UpdateDetails(h);
        }
        catch (Exception)
        {
            h.Error("Proving Grounds: prisoner recruitment UI refresh failed");
            AbandonWindow();
        }
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:591-606 Show. EnsureWindow's own failure (a widget it cannot make) is the window half's.</summary>
    public void Show(IPrisonerRecruitmentUIHost<TId> h)
    {
        alive = true;   // EnsureWindow
        message = "";
        shows++;        // g_rosterScroll / g_detailScroll->setViewOffset(0, 0)
        RefreshAll(h, false);
        if (!alive)
            return;
        // FitWindowToScreen(); LayoutWindow(): the window half's
        if (!visible) requestSerial++;
        visible = true;
        h.Debug("Proving Grounds: prisoner recruitment UI shown");
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:608-615 Close.</summary>
    public void Close(IPrisonerRecruitmentUIHost<TId> h)
    {
        if (alive && visible)
        {
            visible = false;
            requestSerial++;
        }
        ReopenArena(h);
    }

    // C++: PrisonerRecruitmentUI.cpp:612-614
    void ReopenArena(IPrisonerRecruitmentUIHost<TId> h)
    {
        if (h.BoundRegistryValid())
            h.ShowArenaFromBoundRegistry();
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:622-636 Tick (every frame while visible: FitWindowToScreen is the window half's, then RefreshAll).</summary>
    public void Tick(IPrisonerRecruitmentUIHost<TId> h)
    {
        if (!IsVisible)
            return;
        try
        {
            RefreshAll(h, true);
        }
        catch (Exception)
        {
            h.Error("Proving Grounds: prisoner recruitment UI tick failed");
            AbandonWindow();
        }
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:638-647 AbandonWorldState (the window hidden, kept: only its rows, candidates, selection and message
    /// go).</summary>
    public void AbandonWorldState()
    {
        if (alive && visible) requestSerial++;
        visible = false;
        rows.Clear();   // ClearRows
        candidates.Clear();
        hasSelected = false;
        selected = default!;
        message = "";
    }

    /// <summary>The view now.</summary>
    public PrisonerRecruitmentUiView<TId> BuildView() => new()
    {
        RequestSerial = requestSerial, RequestOpen = IsVisible, Shows = shows, RowBuilds = rowBuilds,
        Rows = new(rows.Select((r, i) => new PrisonerRecruitmentRowView<TId>(candidates[i], i, r.Name, r.Marks, r.Selected))),
        HasSelected = hasSelected, Selected = selected, PortraitVisible = portraitVisible,
        SelectedName = selectedName, Stats = stats, Explanation = explanation,
        ActionCaption = actionCaption, ActionEnabled = actionEnabled, Status = status, ShownPrice = shownPrice,
        LastSeq = lastSeq, LastOfferChanged = lastOfferChanged,
    };
}

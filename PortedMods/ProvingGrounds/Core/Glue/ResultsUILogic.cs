namespace ProvingGrounds.Core.Glue;

// Port of the rules half of upstream/ProvingGrounds/plugin/src/ResultsUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.6a (PA §2.12):
// ScheduleShow / Cancel / Close / IsVisible / Tick's delay (:605-683), ShowFromSnapshot (:563-602) and FillSlot (:501-561) with the caption
// helpers (:460-499). Every text the window shows is built here once per show, numbers through CMath (docs/divergences.md "Number formatting for
// glue"). The window itself (EnsureWindow :366-458, LayoutWindow :274-365, FitWindowToContent :225-273, the per-frame resize in Tick :638-651)
// is the window half: Core/Glue/ResultsUILayout.cs + src/Ui/ResultsUI.cs. The sim half (src/Game/ResultsUISim.cs) owns this object.

/// <summary>What the rules half of ResultsUI reads (the sim half implements it over SparStats and the clock).</summary>
public interface IResultsHost<TId>
{
    /// <summary><c>GetTickCount()</c>: the sim clock's milliseconds (PgClock.NowMs).</summary>
    uint NowMs { get; }
    /// <summary><c>SparStats::HasSnapshot()</c>.</summary>
    bool HasSnapshot();
    /// <summary><c>SparStats::GetSnapshot()</c>.</summary>
    SparPodium.Snapshot GetSnapshot();
    /// <summary><c>SparStats::GetPodiumCharacter(index)</c> that is valid (BindPortrait's <c>character &amp;&amp; character-&gt;isValid()</c>, :100).</summary>
    bool TryPodiumCharacter(int index, out TId id);
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);
}

/// <summary>One podium card as FillSlot (:501-561) fills it.</summary>
public readonly record struct ResultsSlotView<TId>(int Place, string RankCaption, string NameLine, string Initial, string Stats, TId Character,
    bool HasCharacter);

/// <summary>The texts the window shows that the C++ writes at creation (EnsureWindow :384-454).</summary>
public static class ResultsUiTexts
{
    /// <summary>C++ :377.</summary>
    public const string Caption = "Proving Grounds - Results";
    /// <summary>C++ :384 g_measure's creation caption (its caption is the header from the first show on).</summary>
    public const string Measure = "Match complete";
    /// <summary>C++ :391-392.</summary>
    public const string Empty = "No victor\nThe match ended without a podium.";
    /// <summary>C++ :444.</summary>
    public const string Close = "Close results";
    /// <summary>C++ :418 the portrait placeholder's creation caption.</summary>
    public const string Placeholder = "?";
}

/// <summary>The published view of the Results window: the texts of the last show and the open / close request. <see cref="Revision"/> counts
/// shows (each one lays the window out again in the C++, FitWindowToContent + LayoutWindow :598-599).</summary>
public sealed record ResultsUiView<TId>
{
    public int Revision { get; init; }
    public string Header { get; init; } = "";
    public string Context { get; init; } = "";
    public ValueList<ResultsSlotView<TId>> Slots { get; init; } = ValueList<ResultsSlotView<TId>>.Empty;
    public bool EmptyVisible { get; init; }
    public int RequestSerial { get; init; }
    public bool RequestOpen { get; init; }
}

/// <summary>The rules half of ResultsUI.cpp: the delayed show and the texts of the podium. Sim thread (one object per world).</summary>
public sealed class ResultsUILogic<TId>
{
    bool pending;            // C++ :30 g_pending
    float delaySec = 1.0f;   // C++ :31 g_delaySec
    float elapsedSec;        // C++ :32 g_elapsedSec
    uint lastTick;           // C++ :33 g_lastTick
    bool visible;            // C++ g_window->getVisible()
    int requestSerial, revision;
    string header = "", context = "";
    ValueList<ResultsSlotView<TId>> slots = ValueList<ResultsSlotView<TId>>.Empty;
    bool emptyVisible;

    public bool Pending => pending;

    /// <summary>C++: ResultsUI.cpp:631-634 IsVisible.</summary>
    public bool IsVisible => visible;

    // C++: ResultsUI.cpp:607-616
    public void ScheduleShow<THost>(THost h, float delaySec) where THost : IResultsHost<TId>
    {
        if (!h.HasSnapshot())
            return;
        pending = true;
        this.delaySec = delaySec > 0.0f ? delaySec : 1.0f;
        elapsedSec = 0.0f;
        lastTick = h.NowMs;
        h.Debug("Proving Grounds: results UI scheduled");
    }

    // C++: ResultsUI.cpp:618-622
    public void Cancel()
    {
        pending = false;
        elapsedSec = 0.0f;
    }

    // C++: ResultsUI.cpp:624-629
    public void Close()
    {
        Cancel();
        SetVisible(false);
    }

    /// <summary>C++: ResultsUI.cpp:636-683, the pending half (the resize half :638-651 is the window side's). True when the window was shown
    /// (the view changed). Generic over the host so a struct host is not boxed (called every sim tick).</summary>
    public bool Tick<THost>(THost h) where THost : IResultsHost<TId>
    {
        if (!pending)
            return false;

        uint now = h.NowMs;
        float deltaSec = (float)(now - lastTick) / 1000.0f;
        lastTick = now;
        elapsedSec += deltaSec;

        if (elapsedSec < delaySec)
            return false;

        pending = false;
        if (!h.HasSnapshot())
            return false;
        ShowFromSnapshot(h, h.GetSnapshot());
        return true;
    }

    /// <summary>C++: ResultsUI.cpp:66 OnCloseClicked (the "Close results" button): Close. A press that reaches the sim half while the window is
    /// not visible (sent just before it closed, a tick later) is ignored: the C++ handler is reachable only through the visible window, and a stale
    /// press must not cancel a new pending show. False when ignored.</summary>
    public bool OnCloseClicked()
    {
        if (!visible)
            return false;
        Close();
        return true;
    }

    /// <summary>The window side reports the window closed (Escape, its title bar's close button) under request <paramref name="serial"/>: as
    /// Close without a new request (the window is already closed). False for another request.</summary>
    public bool OnClosedFromWindow(int serial)
    {
        if (!visible || serial != requestSerial)
            return false;
        Cancel();
        visible = false;
        return true;
    }

    void SetVisible(bool show)
    {
        if (visible == show) return;
        visible = show;
        requestSerial++;
    }

    // C++: ResultsUI.cpp:460-481
    public static string PlaceCaption(MatchRules.MatchMode mode, int place)
    {
        if (mode == MatchRules.MatchMode.TeamAvB ||
            mode == MatchRules.MatchMode.Teams1v1)
        {
            if (place == 1)
                return "MVP";
            if (place == 2)
                return "MVP #2";
            if (place == 3)
                return "MVP #3";
            return "";
        }

        if (place == 1)
            return "VICTOR";
        if (place == 2)
            return "SECOND PLACE";
        if (place == 3)
            return "THIRD PLACE";
        return "";
    }

    // C++: ResultsUI.cpp:483-490
    public static string ModeCaption(MatchRules.MatchMode mode)
    {
        if (mode == MatchRules.MatchMode.TeamAvB)
            return "Team A vs Team B";
        if (mode == MatchRules.MatchMode.Teams1v1)
            return "Teams 1v1";
        return "Last man standing";
    }

    // C++: ResultsUI.cpp:492-499
    public static string TeamCaption(MatchRules.MatchTeam team)
    {
        if (team == MatchRules.MatchTeam.TeamA)
            return "Team A";
        if (team == MatchRules.MatchTeam.TeamB)
            return "Team B";
        return "";
    }

    static string I(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary><c>%+.1f</c> (docs/divergences.md "Number formatting for glue").</summary>
    static string Signed1(float v) => (double.IsNegative(v) ? "" : "+") + CMath.FormatF(v, 1);

    /// <summary>C++: ResultsUI.cpp:524-558 the stats line (sprintf_s into char[256]; the bullet is U+2022, written as its UTF-8 bytes there).</summary>
    public static string StatsLine(in SparPodium.FighterRow f)
    {
        string text = "DEALT " + CMath.FormatF(f.DamageDealt, 0) + "  •  TAKEN " + CMath.FormatF(f.DamageTaken, 0) + "  •  MITIGATED "
            + CMath.FormatF(f.DamageMitigated, 0)
            + "\nHITS " + I(f.HitsLanded) + "  •  BLOCKS " + I(f.Blocks) + "  •  MISSES " + I(f.Misses) + "  •  DODGES " + I(f.Dodges);
        if (f.RatingUpdated)
            text += "\nRATING " + CMath.FormatF(f.RatingAfter, 1) + " (" + Signed1(f.RatingDelta) + ")  •  MARKS " + I(f.MarksAfter)
                + " (+" + I(f.MarksEarned) + ")";
        return text;
    }

    // C++: ResultsUI.cpp:501-561 FillSlot (the portrait bind is the window side's, from the character captured here)
    static ResultsSlotView<TId> FillSlot<THost>(THost h, int slotIndex, in SparPodium.PodiumEntry entry, MatchRules.MatchMode mode)
        where THost : IResultsHost<TId>
    {
        string name = entry.Fighter.Name ?? "";
        string team = TeamCaption(entry.Fighter.Team);
        string nameLine = team.Length > 0 ? name + "  -  " + team : name;
        bool has = h.TryPodiumCharacter(slotIndex, out TId id);
        return new ResultsSlotView<TId>(entry.Place, PlaceCaption(mode, entry.Place), nameLine, ResultsLayout.PortraitInitialUtf8(name),
            StatsLine(entry.Fighter), id, has);
    }

    // C++: ResultsUI.cpp:563-602 ShowFromSnapshot
    void ShowFromSnapshot<THost>(THost h, SparPodium.Snapshot snapshot) where THost : IResultsHost<TId>
    {
        header = snapshot.Header.Length > 0 ? snapshot.Header : "Match ended";
        context = ModeCaption(snapshot.Mode) + "  -  " + I(snapshot.FighterCount) + " fighter" + (snapshot.FighterCount == 1 ? "" : "s");
        var list = new List<ResultsSlotView<TId>>(3);
        for (int i = 0; i < snapshot.PodiumCount && i < 3; ++i)
            list.Add(FillSlot(h, i, snapshot.Podium[i], snapshot.Mode));
        slots = new ValueList<ResultsSlotView<TId>>(list);
        emptyVisible = snapshot.PodiumCount == 0;
        revision++;
        SetVisible(true);
        h.Debug("Proving Grounds: results podium shown");
    }

    /// <summary>The view to publish.</summary>
    public ResultsUiView<TId> BuildView() => new()
    {
        Revision = revision, Header = header, Context = context, Slots = slots, EmptyVisible = emptyVisible,
        RequestSerial = requestSerial, RequestOpen = visible,
    };
}

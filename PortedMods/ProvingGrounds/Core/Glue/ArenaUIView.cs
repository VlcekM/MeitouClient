using System.Collections;

namespace ProvingGrounds.Core.Glue;

/// <summary>C++ ArenaUI.cpp:79 <c>UiMode</c>: the window's match mode (Teams = AvB, Teams 1v1, Last Man Standing).</summary>
public enum ArenaUiMode { AvB = 0, Teams1v1 = 1, LastStanding = 2 }

/// <summary>C++ ArenaUI.cpp:80 <c>AssignDest</c>: where a team-mode assignment goes.</summary>
public enum ArenaDest { TeamA = 0, TeamB = 1 }

/// <summary>C++ ArenaUI.cpp:81 <c>ListKind</c>: the four portrait lists of the window.</summary>
public enum ArenaList { Roster = 0, TeamA = 1, TeamB = 2, Pool = 3 }

/// <summary>Which list a row was released over (C++ OnPortraitRowRelease, ArenaUI.cpp:1277-1294 PointInside of g_teamAScroll / g_teamBScroll /
/// g_poolScroll / g_rosterScroll: the window side hit-tests the release point against the VISIBLE lists, 4.5b). A prisoner row under the point
/// is sent separately (ArenaRowRelease.Prisoner, :1267): it lies inside one of these lists, so both are sent.</summary>
public enum ArenaDropTarget { None = 0, TeamA = 1, TeamB = 2, Pool = 3, Roster = 4 }

/// <summary>A read-only list with value equality (so a published view compares by content: the window side redraws only what changed).</summary>
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    readonly T[] items;

    public static readonly ValueList<T> Empty = new([]);

    public ValueList(IEnumerable<T> source) => items = [.. source];

    public T this[int index] => items[index];
    public int Count => items.Length;
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();

    public bool Equals(ValueList<T>? other) =>
        other is not null && (ReferenceEquals(this, other) || items.AsSpan().SequenceEqual(other.items.AsSpan(), EqualityComparer<T>.Default));

    public override bool Equals(object? obj) => obj is ValueList<T> v && Equals(v);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(items.Length);
        foreach (var x in items) h.Add(x);
        return h.ToHashCode();
    }
}

/// <summary>Texts the window side (4.5b) shows that the C++ writes at build or drag time, and the C++ constants the rules use.</summary>
public static class ArenaUiTexts
{
    /// <summary>C++ ArenaUI.cpp:745 RebuildList's empty-list label of the roster.</summary>
    public const string EmptyRoster = "No fighters match this filter.";
    /// <summary>C++ :745 RebuildList's empty-list label of a team or the pool.</summary>
    public const string EmptyAssign = "Drop fighters here";
    /// <summary>C++ :2848, :1731 the search box's placeholder.</summary>
    public const string SearchPlaceholder = "Search fighters...";
    /// <summary>C++ :1183 ApplyDragHighlights' status text over an assigned prisoner row (a handler drop).</summary>
    public const string DragHandlerOk = "Release to assign handler.", DragHandlerNo = "Cannot assign this handler.";
    /// <summary>C++ :1190 ApplyDragHighlights' suffix added to the heading of the list under the pointer.</summary>
    public const string HeadingDropOk = "\nRelease to assign", HeadingDropNo = "\nCannot assign";
    /// <summary>C++ :236 kMaxFighterDistanceFromRegistry.</summary>
    public const float MaxFighterDistanceFromRegistry = 2500.0f;
}

/// <summary>One portrait row as the window shows it (C++ ArenaUI.cpp:84-96 <c>PortraitRow</c> and MakeRow / RefreshRowLabel, :632-686,
/// :1736-1760): the fighter, its list, its name, the label caption (the name and, when the C++ adds one, a second line), whether it is the
/// selected fighter (the button's selected state, ApplyRowState :1004-1007), the health bar (roster rows only: RefreshStatusBar :270-281; the
/// window side sizes the fill as <c>width * ratio + .5</c> and colours it by the band, StatusColour :263-268) and the handler line of a team or
/// pool prisoner row ("Handler: name", :671-683). The drag flags the window side's highlights need (ApplyDragHighlights :1172-1191, 4.5b):
/// <see cref="DragValid"/> (the row's fighter can be dropped now, :1174-1175), <see cref="HandlerCandidate"/> (IsHandlerCandidate :843-852) and
/// <see cref="Prisoner"/> (a roster prisoner: on a team or pool row that is the C++ "assigned prisoner row", FindAssignedPrisonerRowAtPoint
/// :988-996).</summary>
public readonly record struct ArenaRowView<TId>(
    TId Id,
    ArenaList List,
    string Name,
    string Caption,
    bool Selected,
    bool HealthVisible,
    float HealthRatio,
    RosterStatusPolicy.Band HealthBand,
    bool HasHandler,
    TId Handler,
    string HandlerLabel,
    bool DragValid,
    bool HandlerCandidate,
    bool Prisoner);

/// <summary>One button as the window shows it: visible, enabled, selected (the toggle look) and its caption.</summary>
public readonly record struct ArenaButtonView(bool Visible, bool Enabled, bool Selected, string Caption);

/// <summary>
/// Everything the Arena window shows (task 4.5a; the window half, 4.5b, binds it and sends commands back). Built by
/// <see cref="ArenaUILogic{TId}.BuildView"/> on the sim thread with every text already formatted (CMath.FormatF); value equality, so the sim half
/// publishes a new model only when something changed. <see cref="ShapeKey"/> is what changes the layout (MyGuiScreenHost.Replace): the row
/// count of each list, the team mode and the balance menu (4.5b adds compact mode).
/// </summary>
public sealed record ArenaUiView<TId>
{
    /// <summary>The window's open / close request (task 4.4's ScreenRequests): the sim half bumps the serial on each show / hide.</summary>
    public int RequestSerial { get; init; }
    public bool RequestOpen { get; init; }

    public ArenaUiMode Mode { get; init; }
    /// <summary>C++ IsTeamUiMode (:197-200): Teams or Teams 1v1. Decides which lists, labels and buttons are visible (UpdateModeVisibility,
    /// :1316-1353): team A / B lists and labels, Assign to Team B, Swap Teams in team mode; the pool list and label otherwise.</summary>
    public bool TeamMode { get; init; }
    public bool BalanceMenuOpen { get; init; }

    public ValueList<ArenaRowView<TId>> Roster { get; init; } = ValueList<ArenaRowView<TId>>.Empty;
    public ValueList<ArenaRowView<TId>> TeamA { get; init; } = ValueList<ArenaRowView<TId>>.Empty;
    public ValueList<ArenaRowView<TId>> TeamB { get; init; } = ValueList<ArenaRowView<TId>>.Empty;
    public ValueList<ArenaRowView<TId>> Pool { get; init; } = ValueList<ArenaRowView<TId>>.Empty;

    /// <summary>PG_RosterCount ("N fighters" / "N of M fighters", RefreshRosterList :1614-1626).</summary>
    public string RosterCount { get; init; } = "0 fighters";
    /// <summary>The search text the roster is filtered by ("" = the placeholder is shown: the window side keeps "Search fighters...").</summary>
    public string RosterQuery { get; init; } = "";
    /// <summary>PG_LblDest (UpdateDestHighlight :1557-1558).</summary>
    public string DestLabel { get; init; } = "Double-click assigns to Team A";
    public string TeamALabel { get; init; } = "Team A";
    public string TeamBLabel { get; init; } = "Team B";
    public string PoolLabel { get; init; } = "Fighter pool";

    public ArenaButtonView ModeAvB { get; init; }
    public ArenaButtonView ModeTeams1v1 { get; init; }
    public ArenaButtonView ModeLast { get; init; }
    public ArenaButtonView DestA { get; init; }
    public ArenaButtonView DestB { get; init; }
    public ArenaButtonView Remove { get; init; }
    public ArenaButtonView Balance { get; init; }
    public ArenaButtonView BalanceSkill { get; init; }
    public ArenaButtonView BalanceRating { get; init; }
    public ArenaButtonView Swap { get; init; }
    public ArenaButtonView Clear { get; init; }
    public ValueList<ArenaButtonView> Presets { get; init; } = ValueList<ArenaButtonView>.Empty;
    public ArenaButtonView LocArena { get; init; }
    public ArenaButtonView LocSmall { get; init; }
    public ArenaButtonView LocBanner { get; init; }
    public ArenaButtonView Medical { get; init; }
    /// <summary>The medical button's tooltip (GetMedicalProtocolTip :1393-1403, the button's "tip" user string).</summary>
    public string MedicalTip { get; init; } = "";
    public ArenaButtonView Recruit { get; init; }
    public ArenaButtonView Start { get; init; }
    public ArenaButtonView Stop { get; init; }

    /// <summary>The selected fighter card (SetPreview :537-594): the portrait's character (when <see cref="PreviewImageVisible"/>), name, meta and
    /// stats texts.</summary>
    public bool HasPreview { get; init; }
    public TId Preview { get; init; } = default!;
    public bool PreviewImageVisible { get; init; }
    public string PreviewName { get; init; } = "No fighter selected";
    public string PreviewMeta { get; init; } = "Select a roster or team row";
    public string PreviewStats { get; init; } = "Choose a fighter to review readiness.";

    /// <summary>PG_Summary (RefreshSummary :1784-1823).</summary>
    public string Summary { get; init; } = "Team A vs Team B";
    /// <summary>PG_Status (the status line next to Start).</summary>
    public string Status { get; init; } = "";

    /// <summary>The layout's shape as the sim sees it, built once with the view (<see cref="ShapeOf"/>): the row count of each list (an empty
    /// list shows its empty label instead), the team mode and the balance menu (the C++ lays the panel out again on every refresh, LayoutColumn
    /// :2612-2703, and the positions depend on which lists and button rows are visible). Compact mode (RefreshResponsiveMode :2512-2530) is decided
    /// on the window thread from the client width: 4.5b appends it to this key before handing the key to the screen host.</summary>
    public string ShapeKey { get; init; } = "";

    /// <summary>The <see cref="ShapeKey"/> of a view with these counts and flags.</summary>
    public static string ShapeOf(int roster, int teamA, int teamB, int pool, bool teamMode, bool balanceMenuOpen) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"arena:{roster}/{teamA}/{teamB}/{pool}/t{(teamMode ? 1 : 0)}/b{(balanceMenuOpen ? 1 : 0)}");
}

using System.Numerics;
using System.Text;

namespace ProvingGrounds.Core.Glue;

// The rules half of upstream/ProvingGrounds/plugin/src/ArenaUI.cpp (Proving Grounds by Shutori, GPLv3), task 4.5a (PA §2.3, §2.4): every
// function or part of one that decides something, holds state or computes text, over one host interface so it runs without the game (E3,
// Tests/Glue/ArenaUILogicTests.cs). src/Game/ArenaUISim.cs is the sim half that owns it (the frame-loop refresh, the commands, the published
// model) and src/Game/PgArenaUIHost.cs the host over PgEngine and this world's subsystems (the E1 table is in ArenaUISim.cs's header). The window
// half (layout, binding, clicks, compact drawers, drag highlights, scrolling, the settings tooltip's placement) is task 4.5b, src/Ui/ArenaUI.cs.
// Each function below is the C++ function of the same name, in the C++'s order, with its line range; a C++ function that also moves widgets
// keeps here only its decision, and its comment says what 4.5b does with the rest.
//
// Widgets -> state: the C++ writes captions, enabled / visible / selected flags straight into MyGUI widgets at the moments its functions run;
// the port keeps each written value in a field at the same moment (so a value the C++ leaves stale stays stale here too) and BuildView publishes
// them. Rows hold a character handle the C++ holds as a pointer; the port resolves the handle on every read (a removed character reads as not
// valid, "Invalid", and keeps the name it was last seen with: the C++ would read freed memory; docs/divergences.md "Arena window").

/// <summary>The combat card numbers of a character with stats (C++ <c>CharStats</c>: getOverallSkillLevel_0_100, toughness, strengthActual,
/// dexterityActual).</summary>
public readonly record struct ArenaCardStats(float Combat, float Toughness, float Strength, float Dexterity);

/// <summary>What the Arena window's rules read of and do to the game and the other modules (one member per C++ call; the E1 table in
/// src/Game/ArenaUISim.cs says what replaces each in Meitou). <typeparamref name="TId"/>: a character handle (the C++ <c>Character*</c>).</summary>
public interface IArenaUIHost<TId>
{
    // ---------- characters ----------
    /// <summary>The "no character" id (a C++ null pointer).</summary>
    TId None { get; }
    bool IsNone(TId c);
    /// <summary><c>c->isValid()</c>.</summary>
    bool IsValid(TId c);
    bool IsDead(TId c);
    bool IsUnconscious(TId c);
    /// <summary><c>c->getName()</c> of a valid character (null otherwise).</summary>
    string? Name(TId c);
    /// <summary><c>c->getPosition()</c>; null for a stale id.</summary>
    Vector3? Position(TId c);
    /// <summary><c>c->getStats()</c> non-null: its card numbers.</summary>
    bool Stats(TId c, out ArenaCardStats stats);
    /// <summary><c>RosterStatus::Read(c, status)</c> (RosterStatus.cpp:9-48): false = no bar.</summary>
    bool Health(TId c, out RosterStatus.Snapshot status);
    /// <summary><c>LeaderboardStore::GetRating(c)</c> (player board, LeaderboardStore.cpp:425-439).</summary>
    float Rating(TId c);
    /// <summary><c>SquadUtil::CollectPlayerSquad(out)</c> (SquadUtil.cpp:22-67): the living members of the player's active squads.</summary>
    void CollectPlayerSquad(List<TId> into);

    // ---------- prisoners (PrisonerUtil, task 6.1) ----------
    bool IsRosterPrisoner(TId c);
    /// <summary><c>PrisonerUtil::FindCageForOccupant(c) != NULL</c>.</summary>
    bool HasCage(TId c);
    bool MatchIncludesPrisoner();
    /// <summary><c>PrisonerUtil::CollectNearbyCagePrisoners(registry, out)</c>.</summary>
    void CollectNearbyCagePrisoners(ArenaBuilding registry, List<TId> into);
    IReadOnlyList<TId> MatchPrisoners();
    IReadOnlyList<TId> MatchHandlers();
    /// <summary><c>PrisonerUtil::IsReturning()</c>.</summary>
    bool PrisonersReturning();
    bool PrepareMatch(IReadOnlyList<TId> prisoners, IReadOnlyList<TId> handlers, out string status);
    /// <summary><c>PrisonerUtil::ReturnAllToCages(statusOut)</c>: statusOut.</summary>
    string ReturnAllToCages();
    void ClearMatchState();

    // ---------- ArenaIngress ----------
    ArenaBuilding? BoundRegistry();
    void BindRegistry(ArenaBuilding? registry);
    bool IngressPending();
    string IngressStatus();
    bool HasLocationSite();
    string MissingLocationMessage();
    ArenaLocationMode LocationMode();
    void SetLocationMode(ArenaLocationMode mode);
    bool IngressBegin(MatchRules.MatchMode mode, TId[] fighters, MatchRules.MatchTeam[] teams, TId[] escorts);
    void IngressCancel();
    TId UiOpener();

    // ---------- SparSession ----------
    bool SparActive();
    string SparStatus();
    /// <summary><c>SparSession::Stop(SparSession::StopManual)</c>.</summary>
    void SparStopManual();

    // ---------- TownArena ----------
    bool TownBusy();
    bool TownHasBooking();
    void TownCancel();

    // ---------- ArenaMedical (task 4.7) ----------
    ArenaMedicalPolicy.Protocol GetProtocol();
    void SetProtocol(ArenaMedicalPolicy.Protocol protocol);

    // ---------- other windows ----------
    /// <summary><c>PrisonerRecruitmentUI::Show()</c> (task 6.4a).</summary>
    void ShowRecruitment();
    /// <summary><c>TownBookie::Close()</c> (task 8.1a).</summary>
    void TownBookieClose();
    /// <summary><c>PrisonerRecruitmentUI::AbandonWorldState()</c> (task 6.4a).</summary>
    void RecruitmentAbandon();
    /// <summary><c>TownArenaUI::Show(registry, bindRegistry)</c> (task 7.5: the sim half; the window is task 7.6).</summary>
    void TownArenaShow(ArenaBuilding registry, bool bindRegistry);
    /// <summary><c>TownArenaUI::Close()</c> (task 7.5).</summary>
    void TownArenaClose();
    /// <summary>The other windows' AbandonWorldState that ArenaUI::AbandonWorldState calls first (MarksHUD, TownArenaUI, RewardsUI,
    /// PrisonerRecruitmentUI, :3199-3202).</summary>
    void OtherWindowsAbandon();
}

/// <summary>
/// The Arena window's rules and state (C++ ArenaUI.cpp's anonymous-namespace globals and the functions over them). One per world, owned by the
/// sim half (src/Game/ArenaUISim.cs). Sim thread only.
/// </summary>
public sealed class ArenaUILogic<TId> where TId : IEquatable<TId>
{
    sealed class Row
    {
        public TId Id = default!;
        public ArenaList List;
        public string Caption = "";
        public bool HealthVisible;
        public float HealthRatio;
        public RosterStatusPolicy.Band HealthBand;
        public TId Handler = default!;
        public bool HasHandler;
        public string HandlerLabel = "";
    }

    struct HandlerAssignment
    {
        public TId Prisoner, Handler;
    }

    sealed class SetupPreset   // C++ :104-114
    {
        public bool Valid;
        public ArenaUiMode Mode;
        public ArenaDest Destination;
        public ArenaLocationMode Location;
        public ArenaMedicalPolicy.Protocol MedicalProtocol;
        public List<TId> TeamA = [], TeamB = [], Pool = [];
    }

    readonly IEqualityComparer<TId> eq = EqualityComparer<TId>.Default;

    // C++ :180-215 module state
    readonly List<TId> squadCache = [], rosterCache = [], filteredRoster = [], teamA = [], teamB = [], pool = [];
    readonly List<Row> rosterRows = [], teamARows = [], teamBRows = [], poolRows = [];
    readonly List<HandlerAssignment> handlerAssignments = [];
    readonly SetupPreset[] setupPresets = [new(), new(), new()];
    ArenaUiMode uiMode = ArenaUiMode.AvB;
    ArenaMedicalPolicy.Protocol medicalProtocol = ArenaMedicalPolicy.Protocol.RingsideAid;
    bool balanceMenuOpen;
    // UpdateBalanceMenuVisibility's writes (:1302-1314): the Balance caption and the visibility of By Skill, By Rating, Swap Teams, Clear Teams,
    // written only where the C++ calls it (AbandonWorldState closes the menu without calling it: the reopened window keeps the old look)
    string balanceCaption = "Balance";
    bool balanceSkillVisible, balanceRatingVisible, swapVisible = true, clearVisible = true;
    ArenaDest dest = ArenaDest.TeamA;
    TId previewChar;
    string rosterQuery = "";
    readonly UiRefreshCadence refresh = new();   // C++ :213-230 g_uiRefreshLastTick / g_uiRefreshElapsedSec
    readonly Dictionary<TId, string> lastNames = [];   // port: the name a removed character was last seen with

    // window state the C++ keeps in its widgets
    bool created;          // C++ g_window != NULL (CreatePanel ran)
    bool visible;          // C++ g_window->getVisible() (as the sim half asked, or as the window side reported)
    int requestSerial;
    string status = "";
    string previewName = "No fighter selected", previewMeta = "Select a roster or team row", previewStats = "Choose a fighter to review readiness.";
    bool previewImageVisible;
    string summary = "Team A vs Team B";
    string rosterCount = "0 fighters";
    string lblTeamA = "Team A", lblTeamB = "Team B", lblPool = "Fighter pool";
    string lblDest = "Double-click assigns to Team A";
    bool destASelected, destBSelected;
    ArenaLocationMode highlightedLocation = ArenaLocationMode.Arena;   // UpdateLocationHighlight's last write
    bool hasHighlightedLocation;
    ButtonsState buttons;

    struct ButtonsState   // RefreshStatusAndButtons :1958-2007 writes
    {
        public bool StartVisible, StartEnabled, StopVisible, StopEnabled, ModesEnabled, LocsEnabled, DestAEnabled, DestBEnabled, RemoveEnabled,
            BalanceEnabled, ClearEnabled, SwapEnabled, PresetsEnabled;
        public string StopCaption;
    }

    public ArenaUILogic(TId none)
    {
        previewChar = none;
        buttons.StopCaption = "Stop Fight";
        buttons.StartVisible = buttons.StopVisible = true;
    }

    // ---------- reads for the sim half and the tests ----------
    public bool Visible => visible;
    public int RequestSerial => requestSerial;
    public ArenaUiMode Mode => uiMode;
    public ArenaDest Dest => dest;
    public string Status => status;
    public IReadOnlyList<TId> TeamAList => teamA;
    public IReadOnlyList<TId> TeamBList => teamB;
    public IReadOnlyList<TId> PoolList => pool;
    public IReadOnlyList<TId> RosterCacheList => rosterCache;
    public IReadOnlyList<TId> FilteredRoster => filteredRoster;
    public TId PreviewChar => previewChar;
    public ArenaMedicalPolicy.Protocol MedicalProtocol => medicalProtocol;
    public bool BalanceMenuOpen => balanceMenuOpen;
    public string RosterQuery => rosterQuery;
    public bool PresetValid(int i) => i >= 0 && i < 3 && setupPresets[i].Valid;

    /// <summary>The handler assigned to <paramref name="prisoner"/> (none when none).</summary>
    public TId HandlerOf(IArenaUIHost<TId> h, TId prisoner) => FindHandlerAssignment(h, prisoner);

    bool Same(TId a, TId b) => eq.Equals(a, b);

    // C++ :197-200
    bool IsTeamUiMode() => uiMode == ArenaUiMode.AvB || uiMode == ArenaUiMode.Teams1v1;

    // C++: ArenaUI.cpp:217-230 AdvanceUiRefreshClock / ResetUiRefreshClock: Core/Glue/UiRefreshCadence.cs.
    void ResetUiRefreshClock() => refresh.Reset();

    // C++: ArenaUI.cpp:263-281 StatusColour / RefreshStatusBar: the decision (visible, ratio, band) here; 4.5b sizes and colours the fill.
    static void RefreshStatusBar(IArenaUIHost<TId> h, Row row)
    {
        bool shown = h.Health(row.Id, out var s);
        row.HealthVisible = shown;
        row.HealthRatio = shown ? s.Recovery : 0f;
        row.HealthBand = shown ? s.Band : RosterStatusPolicy.Band.Red;
    }

    bool Valid(IArenaUIHost<TId> h, TId c) => !h.IsNone(c) && h.IsValid(c);

    /// <summary><c>c->getName()</c>: a valid character's name (remembered); a removed one keeps its last name (port: never freed memory).</summary>
    string NameOf(IArenaUIHost<TId> h, TId c)
    {
        if (h.IsNone(c)) return "";
        if (h.IsValid(c) && h.Name(c) is { } n)
        {
            lastNames[c] = n;
            return n;
        }
        return lastNames.GetValueOrDefault(c, "");
    }

    // C++: ArenaUI.cpp:283-299
    bool IsTooFarFromRegistry(IArenaUIHost<TId> h, TId c)
    {
        if (!Valid(h, c))
            return false;

        if (h.BoundRegistry() is not { } registry)
            return false;

        if (h.Position(c) is not { } fighterPos)
            return false;
        Vector3 registryPos = registry.Position;
        float dx = fighterPos.X - registryPos.X;
        float dz = fighterPos.Z - registryPos.Z;
        float maxDistSq = ArenaUiTexts.MaxFighterDistanceFromRegistry * ArenaUiTexts.MaxFighterDistanceFromRegistry;
        return (dx * dx) + (dz * dz) > maxDistSq;
    }

    // C++: ArenaUI.cpp:301-306
    static void AppendUnavailableReason(StringBuilder reasons, string reason)
    {
        if (reasons.Length > 0)
            reasons.Append(", ");
        reasons.Append(reason);
    }

    // C++: ArenaUI.cpp:308-316
    bool VectorContains(List<TId> list, TId c)
    {
        for (int i = 0; i < list.Count; ++i)
            if (Same(list[i], c))
                return true;
        return false;
    }

    // C++: ArenaUI.cpp:318-323
    bool IsInMatchRoster(TId c) => VectorContains(teamA, c) || VectorContains(teamB, c) || VectorContains(pool, c);

    // C++: ArenaUI.cpp:325-345
    public string FighterUnavailableReason(IArenaUIHost<TId> h, TId c)
    {
        if (!Valid(h, c))
            return "Invalid";

        var reasons = new StringBuilder();
        if (h.IsDead(c))
            AppendUnavailableReason(reasons, "Dead");
        else if (h.IsUnconscious(c))
            AppendUnavailableReason(reasons, "Unconscious");
        if (IsTooFarFromRegistry(h, c))
            AppendUnavailableReason(reasons, "Too far away");
        if (h.IsRosterPrisoner(c) && !h.HasCage(c))
        {
            bool releasedForMatch = h.MatchIncludesPrisoner() && IsInMatchRoster(c);
            if (!releasedForMatch)
                AppendUnavailableReason(reasons, "No cage");
        }
        return reasons.ToString();
    }

    // C++: ArenaUI.cpp:347-350
    public bool IsAssignable(IArenaUIHost<TId> h, TId c) => FighterUnavailableReason(h, c).Length == 0;

    // C++: ArenaUI.cpp:352-389
    void RemoveFromAll(IArenaUIHost<TId> h, TId c)
    {
        if (h.IsNone(c))
            return;
        teamA.RemoveAll(x => Same(x, c));
        teamB.RemoveAll(x => Same(x, c));
        pool.RemoveAll(x => Same(x, c));
        handlerAssignments.RemoveAll(a => Same(a.Prisoner, c) || Same(a.Handler, c));
    }

    // C++: ArenaUI.cpp:391-394
    public bool IsAssigned(TId c) => VectorContains(teamA, c) || VectorContains(teamB, c) || VectorContains(pool, c);

    // C++: ArenaUI.cpp:396-407 RosterLabel has no caller in the C++ (MakeRow / RefreshRowLabel build their own captions): not ported.

    // C++: ArenaUI.cpp:409-418
    string FighterAssignment(IArenaUIHost<TId> h, TId c)
    {
        if (VectorContains(teamA, c))
            return "Team A";
        if (VectorContains(teamB, c))
            return "Team B";
        if (VectorContains(pool, c))
            return "Fighter pool";
        return IsAssignable(h, c) ? "Unassigned" : "Unavailable";
    }

    // C++: ArenaUI.cpp:420-449
    public string FighterDetailLabel(IArenaUIHost<TId> h, TId c)
    {
        string reason = FighterUnavailableReason(h, c);
        string assignment = FighterAssignment(h, c);
        if (reason.Length == 0)
        {
            if (h.IsRosterPrisoner(c))
                return assignment + " · Prisoner";
            return assignment;
        }

        string detail;
        if (IsAssigned(c))
        {
            detail = assignment;
            if (h.IsRosterPrisoner(c))
                detail += " · Prisoner";
            detail += " - ";
        }
        else
        {
            detail = "Unavailable - ";
        }
        detail += reason;
        return detail;
    }

    // C++: ArenaUI.cpp:451-481
    void RefreshRosterCaches(IArenaUIHost<TId> h)
    {
        squadCache.Clear();
        h.CollectPlayerSquad(squadCache);
        rosterCache.Clear();
        rosterCache.AddRange(squadCache);
        if (h.BoundRegistry() is { } registry)
        {
            var prisoners = new List<TId>();
            h.CollectNearbyCagePrisoners(registry, prisoners);
            for (int i = 0; i < prisoners.Count; ++i)
            {
                TId p = prisoners[i];
                if (h.IsNone(p) || VectorContains(rosterCache, p))
                    continue;
                rosterCache.Add(p);
            }
        }

        if (h.IngressPending() || h.MatchIncludesPrisoner())
        {
            var matchPrisoners = h.MatchPrisoners();
            for (int i = 0; i < matchPrisoners.Count; ++i)
            {
                TId p = matchPrisoners[i];
                if (h.IsNone(p) || VectorContains(rosterCache, p))
                    continue;
                rosterCache.Add(p);
            }
        }
    }

    // C++: ArenaUI.cpp:483-495
    List<TId> CollectMatchFighters()
    {
        var output = new List<TId>();
        if (IsTeamUiMode())
        {
            output.AddRange(teamA);
            output.AddRange(teamB);
        }
        else
        {
            output.AddRange(pool);
        }
        return output;
    }

    // C++: ArenaUI.cpp:497-508
    int CountPrisonersInMatch(IArenaUIHost<TId> h)
    {
        var match = CollectMatchFighters();
        int n = 0;
        for (int i = 0; i < match.Count; ++i)
            if (h.IsRosterPrisoner(match[i]))
                ++n;
        return n;
    }

    // C++: ArenaUI.cpp:510-525
    int CountAvailableHandlers(IArenaUIHost<TId> h)
    {
        var match = CollectMatchFighters();
        int n = 0;
        for (int i = 0; i < squadCache.Count; ++i)
        {
            TId c = squadCache[i];
            if (!IsAssignable(h, c))
                continue;
            if (VectorContains(match, c))
                continue;
            ++n;
        }
        return n;
    }

    /// <summary>C++: ArenaUI.cpp:527-533 <c>LowerText</c>: <c>tolower</c> per byte in the "C" locale, i.e. A-Z only.</summary>
    public static string LowerText(string value)
    {
        var chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; ++i)
            if (chars[i] >= 'A' && chars[i] <= 'Z')
                chars[i] = (char)(chars[i] + 32);
        return new string(chars);
    }

    // C++: ArenaUI.cpp:537-594. The portrait (setImageWidget) is bound by 4.5b from the published preview character; RefreshRowStates (the
    // rows' selected look, :1009-1020) is derived from the preview when the view is built.
    void SetPreview(IArenaUIHost<TId> h, TId c)
    {
        previewChar = c;
        bool valid = Valid(h, c);
        previewName = valid ? NameOf(h, c) : "No fighter selected";
        previewMeta = valid ? FighterDetailLabel(h, c) : "Select a roster or team row";

        if (valid && h.Stats(c, out var stats))
        {
            // sprintf_s(statBuf, "Combat %.0f   Toughness %.0f\nStrength %.0f   Dexterity %.0f", ...)
            previewStats = "Combat " + CMath.FormatF(stats.Combat, 0) + "   Toughness " + CMath.FormatF(stats.Toughness, 0)
                + "\nStrength " + CMath.FormatF(stats.Strength, 0) + "   Dexterity " + CMath.FormatF(stats.Dexterity, 0);
        }
        else
        {
            previewStats = "Choose a fighter to review readiness,\nassignment, and combat stats.";
        }

        previewImageVisible = valid;
    }

    // C++: ArenaUI.cpp:632-686 MakeRow: the label caption, the roster status bar and the handler line; 4.5b builds the row widgets.
    Row MakeRow(IArenaUIHost<TId> h, TId c, ArenaList kind)
    {
        var row = new Row { Id = c, List = kind, Handler = h.None };
        row.Caption = RowCaption(h, c, kind);
        if (kind == ArenaList.Roster)
            RefreshStatusBar(h, row);
        TId handler = kind != ArenaList.Roster && h.IsRosterPrisoner(c) && !h.IsUnconscious(c) ? FindHandlerAssignment(h, c) : h.None;
        if (!h.IsNone(handler))
        {
            row.HasHandler = true;
            row.Handler = handler;
            row.HandlerLabel = "Handler: " + NameOf(h, handler);
        }
        return row;
    }

    /// <summary>C++ :654-658 (MakeRow) = :1741-1755 (RefreshRowLabel): the name, and the detail (roster) or the reason (team / pool) line.</summary>
    string RowCaption(IArenaUIHost<TId> h, TId c, ArenaList kind)
    {
        string caption = NameOf(h, c);
        string reason = FighterUnavailableReason(h, c);
        if (kind == ArenaList.Roster)
        {
            if (IsAssigned(c) || reason.Length != 0)
                caption += "\n" + FighterDetailLabel(h, c);
        }
        else if (reason.Length != 0)
        {
            caption += "\nUnavailable - " + reason;
        }
        return caption;
    }

    // C++: ArenaUI.cpp:733-755 RebuildList: the rows (4.5b lays them out, LayoutRows :688-731, and keeps the scroll offset).
    void RebuildList(IArenaUIHost<TId> h, List<Row> rows, List<TId> chars, ArenaList kind)
    {
        rows.Clear();
        for (int i = 0; i < chars.Count; ++i)
            if (!h.IsNone(chars[i])) rows.Add(MakeRow(h, chars[i], kind));
    }

    List<Row> RowsOf(ArenaList kind) => kind switch
    {
        ArenaList.Roster => rosterRows,
        ArenaList.TeamA => teamARows,
        ArenaList.TeamB => teamBRows,
        _ => poolRows,
    };

    /// <summary>C++: ArenaUI.cpp:757-772 FindRowCharacter, by the character the window saw instead of a row index: true when a row of
    /// <paramref name="kind"/> shows <paramref name="c"/> (port: the roster may have changed before the click landed, docs/divergences.md).</summary>
    public bool RowShows(ArenaList kind, TId c)
    {
        foreach (var r in RowsOf(kind))
            if (Same(r.Id, c)) return true;
        return false;
    }

    // C++: ArenaUI.cpp:819-829
    TId FindHandlerAssignment(IArenaUIHost<TId> h, TId prisoner)
    {
        if (h.IsNone(prisoner))
            return h.None;
        for (int i = 0; i < handlerAssignments.Count; ++i)
            if (Same(handlerAssignments[i].Prisoner, prisoner))
                return handlerAssignments[i].Handler;
        return h.None;
    }

    // C++: ArenaUI.cpp:831-841
    bool HandlerClaimed(IArenaUIHost<TId> h, TId handler)
    {
        if (h.IsNone(handler))
            return false;
        for (int i = 0; i < handlerAssignments.Count; ++i)
            if (Same(handlerAssignments[i].Handler, handler))
                return true;
        return false;
    }

    // C++: ArenaUI.cpp:843-852
    public bool IsHandlerCandidate(IArenaUIHost<TId> h, TId c)
    {
        if (!IsAssignable(h, c))
            return false;
        if (h.IsRosterPrisoner(c))
            return false;
        if (IsAssigned(c))
            return false;
        return true;
    }

    // C++: ArenaUI.cpp:854-874
    void SetHandlerAssignment(IArenaUIHost<TId> h, TId prisoner, TId handler)
    {
        if (h.IsNone(prisoner) || h.IsNone(handler))
            return;
        handlerAssignments.RemoveAll(a => Same(a.Handler, handler) || Same(a.Prisoner, prisoner));
        handlerAssignments.Add(new HandlerAssignment { Prisoner = prisoner, Handler = handler });
    }

    // C++: ArenaUI.cpp:876-896
    void PruneHandlerAssignments(IArenaUIHost<TId> h)
    {
        handlerAssignments.RemoveAll(a =>
        {
            TId prisoner = a.Prisoner, handler = a.Handler;
            bool keep =
                !h.IsNone(prisoner) &&
                h.IsRosterPrisoner(prisoner) &&
                IsAssigned(prisoner) &&
                !h.IsNone(handler) &&
                h.IsValid(handler) &&
                !h.IsDead(handler) &&
                !IsAssigned(handler) &&
                !h.IsRosterPrisoner(handler);
            return !keep;
        });
    }

    // C++: ArenaUI.cpp:898-924
    void EnsureHandlerAssignments(IArenaUIHost<TId> h)
    {
        PruneHandlerAssignments(h);

        var match = CollectMatchFighters();
        for (int i = 0; i < match.Count; ++i)
        {
            TId prisoner = match[i];
            if (!h.IsRosterPrisoner(prisoner))
                continue;
            if (!h.IsNone(FindHandlerAssignment(h, prisoner)))
                continue;

            for (int s = 0; s < squadCache.Count; ++s)
            {
                TId candidate = squadCache[s];
                if (!IsHandlerCandidate(h, candidate))
                    continue;
                if (HandlerClaimed(h, candidate))
                    continue;
                SetHandlerAssignment(h, prisoner, candidate);
                break;
            }
        }
    }

    // C++: ArenaUI.cpp:926-961
    List<TId> BuildOrderedHandlers(IArenaUIHost<TId> h, List<TId> prisonerFighters)
    {
        EnsureHandlerAssignments(h);
        var outHandlers = new List<TId>();

        for (int i = 0; i < prisonerFighters.Count; ++i)
        {
            TId assigned = FindHandlerAssignment(h, prisonerFighters[i]);
            if (h.IsNone(assigned))
            {
                for (int s = 0; s < squadCache.Count; ++s)
                {
                    TId candidate = squadCache[s];
                    if (!IsHandlerCandidate(h, candidate))
                        continue;
                    bool used = VectorContains(outHandlers, candidate);
                    if (used || HandlerClaimed(h, candidate))
                        continue;
                    SetHandlerAssignment(h, prisonerFighters[i], candidate);
                    assigned = candidate;
                    break;
                }
            }
            outHandlers.Add(assigned);
        }
        return outHandlers;
    }

    // C++: ArenaUI.cpp:963-1002 FindPortraitRowAtPoint / FindAssignedPrisonerRowAtPoint / FindAssignedPrisonerAtPoint: the hit test is 4.5b's;
    // the sim half receives the row under the release point (ArenaRowRelease.Prisoner) and checks it the C++ way here (a team or pool row of a
    // roster prisoner).
    bool IsAssignedPrisonerRow(IArenaUIHost<TId> h, TId prisoner) =>
        (RowShows(ArenaList.TeamA, prisoner) || RowShows(ArenaList.TeamB, prisoner) || RowShows(ArenaList.Pool, prisoner)) && h.IsRosterPrisoner(prisoner);

    // C++: ArenaUI.cpp:1030-1059
    void AssignCharacter(IArenaUIHost<TId> h, TId c)
    {
        if (!IsAssignable(h, c) || h.SparActive() || h.IngressPending())
            return;

        if (IsTeamUiMode())
        {
            RemoveFromAll(h, c);
            if (dest == ArenaDest.TeamA)
                teamA.Add(c);
            else
                teamB.Add(c);
        }
        else
        {
            if (VectorContains(pool, c))
            {
                SetPreview(h, c);
                return;
            }
            RemoveFromAll(h, c);
            pool.Add(c);
        }

        SetPreview(h, c);
        RefreshRosterList(h);
        RefreshAssignmentLists(h);
        RefreshSummary(h);
        RefreshStatusAndButtons(h);
    }

    // C++: ArenaUI.cpp:1061-1071
    void RemoveCharacter(IArenaUIHost<TId> h, TId c)
    {
        if (h.IsNone(c) || h.SparActive() || h.IngressPending())
            return;
        RemoveFromAll(h, c);
        SetPreview(h, c);
        RefreshRosterList(h);
        RefreshAssignmentLists(h);
        RefreshSummary(h);
        RefreshStatusAndButtons(h);
    }

    /// <summary>C++: ArenaUI.cpp:1073-1089 OnPortraitRowClick: select (preview) the row's fighter. Ignored when no row of that list shows it.</summary>
    public void OnPortraitRowClick(IArenaUIHost<TId> h, ArenaList kind, TId c)
    {
        if (!RowShows(kind, c))
            return;

        SetPreview(h, c);
        RefreshStatusAndButtons(h);
        // Single click selects; double click or explicit actions assign.
    }

    /// <summary>C++: ArenaUI.cpp:1091-1103 OnPortraitRowDoubleClick: a roster row's assignable fighter is assigned.</summary>
    public void OnPortraitRowDoubleClick(IArenaUIHost<TId> h, ArenaList kind, TId c)
    {
        if (h.SparActive() || h.IngressPending())
            return;
        if (kind != ArenaList.Roster || !RowShows(kind, c))
            return;
        if (IsAssignable(h, c))
            AssignCharacter(h, c);
    }

    // C++: ArenaUI.cpp:1105-1158 (ScrollForListKind, ListKindForScroll, ScrollListBy, UpdateScrollButtonsForKind, OnPortraitRowWheel),
    // :1160-1232 (ResetDropZoneVisuals, ApplyDragHighlights, RefreshPointerHighlights, OnPortraitRowDrag): window-side scrolling and drag
    // highlights, task 4.5b. ApplyDragHighlights's validity test is published per row (ArenaRowView.DragValid, built in ViewOf).

    /// <summary>C++ :1174-1175 (ApplyDragHighlights): whether a drag of <paramref name="c"/> can be dropped (4.5b's highlight text).</summary>
    bool DragValid(IArenaUIHost<TId> h, TId c) => IsAssignable(h, c) && !h.SparActive() && !h.IngressPending() && !h.PrisonersReturning();

    /// <summary>C++: ArenaUI.cpp:1234-1295 OnPortraitRowRelease, the release of a row of <paramref name="kind"/> showing <paramref name="c"/>
    /// (the window side's hit test of the release point: <paramref name="target"/> = the visible list under it, <paramref name="prisoner"/> = the
    /// character of the team / pool row under it, or none), or, with <paramref name="right"/>, the right button's release on the row (remove).
    /// The C++ order: a handler candidate dropped on an assigned prisoner row becomes its handler; anything else falls through to the list tests,
    /// which the prisoner row's own list still matches (:1267-1294).</summary>
    public void OnPortraitRowRelease(IArenaUIHost<TId> h, ArenaList kind, TId c, bool right, ArenaDropTarget target, TId prisoner)
    {
        // ResetDropZoneVisuals / drag flags / RefreshRowStates: window side (4.5b)
        if (h.SparActive() || h.IngressPending() || h.PrisonersReturning())
            return;
        if (!RowShows(kind, c))
            return;

        if (right)
        {
            if (IsAssigned(c))
                RemoveCharacter(h, c);
            return;
        }

        if (!IsAssignable(h, c))
            return;

        // Drag available squad member onto an assigned prisoner to set handler.
        TId dropPrisoner = !h.IsNone(prisoner) && IsAssignedPrisonerRow(h, prisoner) ? prisoner : h.None;
        if (!h.IsNone(dropPrisoner) && IsHandlerCandidate(h, c))
        {
            SetHandlerAssignment(h, dropPrisoner, c);
            SetPreview(h, dropPrisoner);
            RefreshAssignmentLists(h);
            RefreshStatusAndButtons(h);
            return;
        }

        if (IsTeamUiMode() && target == ArenaDropTarget.TeamA)
        {
            dest = ArenaDest.TeamA;
            AssignCharacter(h, c);
        }
        else if (IsTeamUiMode() && target == ArenaDropTarget.TeamB)
        {
            dest = ArenaDest.TeamB;
            AssignCharacter(h, c);
        }
        else if (!IsTeamUiMode() && target == ArenaDropTarget.Pool)
        {
            AssignCharacter(h, c);
        }
        else if (kind != ArenaList.Roster && target == ArenaDropTarget.Roster)
        {
            RemoveCharacter(h, c);
        }
    }

    // C++: ArenaUI.cpp:1302-1314
    void UpdateBalanceMenuVisibility()
    {
        balanceCaption = balanceMenuOpen ? "Back" : "Balance";
        balanceSkillVisible = balanceMenuOpen;
        balanceRatingVisible = balanceMenuOpen;
        swapVisible = !balanceMenuOpen && IsTeamUiMode();
        clearVisible = !balanceMenuOpen;
    }

    // C++: ArenaUI.cpp:1316-1353 UpdateModeVisibility: the lists, labels and Dest buttons follow the mode at BuildView (every mode change calls
    // this); the balance row through UpdateBalanceMenuVisibility; the location and destination highlights in their own functions.
    void UpdateModeVisibility(IArenaUIHost<TId> h)
    {
        UpdateBalanceMenuVisibility();
        UpdateLocationHighlight(h);
        UpdateDestHighlight();
    }

    // C++: ArenaUI.cpp:1355-1361
    void UpdateLocationHighlight(IArenaUIHost<TId> h)
    {
        highlightedLocation = h.LocationMode();
        hasHighlightedLocation = true;
    }

    // C++: ArenaUI.cpp:1363-1391 OnLocationArena / OnLocationBanner / OnLocationSmallArena
    public void OnLocation(IArenaUIHost<TId> h, ArenaLocationMode mode)
    {
        if (h.IngressPending() || h.SparActive())
            return;
        h.SetLocationMode(mode);
        UpdateLocationHighlight(h);
        RefreshSummary(h);
        RefreshStatusAndButtons(h);
    }

    /// <summary>C++: ArenaUI.cpp:1393-1403</summary>
    public static string GetMedicalProtocolTip(ArenaMedicalPolicy.Protocol protocol) => protocol switch
    {
        ArenaMedicalPolicy.Protocol.Bloodsport => "No treatment until the match ends. No auto prisoner aid.",
        _ => "No aid for active fighters; OK once eliminated. Prisoners get stabilized.",
    };

    // C++: ArenaUI.cpp:1405-1516 the settings tooltip (HideSettingsTip, PlaceSettingsTip, ShowSettingsTip, OnSettingsToolTip,
    // BindSettingsToolTip, RefreshSettingsTipIfShowing): window side (4.5b) over the published MedicalTip.

    /// <summary>C++: ArenaUI.cpp:1518-1529 UpdateMedicalProtocolButton's caption.</summary>
    public static string MedicalCaption(ArenaMedicalPolicy.Protocol protocol) => "Medical: " + ArenaMedicalPolicy.GetProtocolName(protocol);

    // C++: ArenaUI.cpp:1531-1541
    public void OnMedicalProtocolToggle(IArenaUIHost<TId> h)
    {
        if (h.IngressPending() || h.SparActive())
            return;

        medicalProtocol = ArenaMedicalPolicy.NextProtocol(medicalProtocol);
        h.SetProtocol(medicalProtocol);
        RefreshSummary(h);
        RefreshStatusAndButtons(h);
    }

    // C++: ArenaUI.cpp:1543-1551
    public void OnRecruitPrisoners(IArenaUIHost<TId> h)
    {
        if (created)
            SetVisible(false);   // HideSettingsTip + setVisible(false) (no clock reset, as the C++)
        h.ShowRecruitment();
    }

    // C++: ArenaUI.cpp:1553-1559
    void UpdateDestHighlight()
    {
        destASelected = !IsTeamUiMode() || dest == ArenaDest.TeamA;
        destBSelected = IsTeamUiMode() && dest == ArenaDest.TeamB;
        lblDest = !IsTeamUiMode() ? "Double-click adds to the pool" :
            dest == ArenaDest.TeamA ? "Double-click assigns to Team A" : "Double-click assigns to Team B";
    }

    // C++: ArenaUI.cpp:1561-1595
    void SwitchMode(IArenaUIHost<TId> h, ArenaUiMode newMode)
    {
        if (newMode == uiMode)
            return;
        if (h.SparActive() || h.IngressPending())
            return;

        bool wasTeam = IsTeamUiMode();
        bool nowTeam = newMode == ArenaUiMode.AvB || newMode == ArenaUiMode.Teams1v1;
        bool nowLast = newMode == ArenaUiMode.LastStanding;
        bool wasLast = uiMode == ArenaUiMode.LastStanding;

        if (wasTeam && nowLast)
        {
            for (int i = 0; i < teamA.Count; ++i)
                if (!VectorContains(pool, teamA[i]))
                    pool.Add(teamA[i]);
            for (int i = 0; i < teamB.Count; ++i)
                if (!VectorContains(pool, teamB[i]))
                    pool.Add(teamB[i]);
            teamA.Clear();
            teamB.Clear();
        }
        else if (wasLast && nowTeam)
        {
            pool.Clear();
        }

        uiMode = newMode;
        UpdateModeVisibility(h);
    }

    // C++: ArenaUI.cpp:1597-1628 (UpdateScrollButtonsForKind: 4.5b)
    void RefreshRosterList(IArenaUIHost<TId> h)
    {
        filteredRoster.Clear();
        string query = LowerText(rosterQuery);
        for (int i = 0; i < rosterCache.Count; ++i)
        {
            TId c = rosterCache[i];
            if (h.IsNone(c))
                continue;
            bool filterMatch = true;

            if (filterMatch &&
                (query.Length == 0 || LowerText(NameOf(h, c)).Contains(query, StringComparison.Ordinal)))
                filteredRoster.Add(c);
        }
        RebuildList(h, rosterRows, filteredRoster, ArenaList.Roster);

        rosterCount = filteredRoster.Count == rosterCache.Count
            ? Int(rosterCache.Count) + " fighters"
            : Int(filteredRoster.Count) + " of " + Int(rosterCache.Count) + " fighters";
    }

    static string Int(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // C++: ArenaUI.cpp:1630-1645
    float AverageCombatSkill(IArenaUIHost<TId> h, List<TId> fighters)
    {
        float totalSkill = 0.0f;
        int validFighters = 0;
        for (int i = 0; i < fighters.Count; ++i)
        {
            TId fighter = fighters[i];
            if (!Valid(h, fighter) || !h.Stats(fighter, out var stats))
                continue;

            totalSkill += stats.Combat;
            ++validFighters;
        }

        return validFighters > 0 ? totalSkill / (float)validFighters : 0.0f;
    }

    // C++: ArenaUI.cpp:1647-1683 (UpdateScrollButtonsForKind: 4.5b)
    void RefreshAssignmentLists(IArenaUIHost<TId> h)
    {
        EnsureHandlerAssignments(h);
        RebuildList(h, teamARows, teamA, ArenaList.TeamA);
        RebuildList(h, teamBRows, teamB, ArenaList.TeamB);
        RebuildList(h, poolRows, pool, ArenaList.Pool);

        // sprintf_s(buf, "Team A | %d fighter%s | ~%.0f combat", ...)
        lblTeamA = "Team A | " + Int(teamA.Count) + " fighter" + (teamA.Count == 1 ? "" : "s") + " | ~" + CMath.FormatF(AverageCombatSkill(h, teamA), 0) + " combat";
        lblTeamB = "Team B | " + Int(teamB.Count) + " fighter" + (teamB.Count == 1 ? "" : "s") + " | ~" + CMath.FormatF(AverageCombatSkill(h, teamB), 0) + " combat";
        lblPool = "Fighter pool  |  " + Int(pool.Count) + " fighters";
    }

    /// <summary>C++: ArenaUI.cpp:1685-1692 OnRosterSearchChanged (and :1719-1734 OnRosterSearchBlur back to the placeholder, which clears the
    /// query): the roster filtered by <paramref name="query"/> (the window side sends "" while its placeholder shows and resets the scroll).</summary>
    public void OnRosterSearchChanged(IArenaUIHost<TId> h, string query)
    {
        rosterQuery = query;
        RefreshRosterList(h);
    }

    // C++: ArenaUI.cpp:1694-1717 search box focus / mouse / accept handlers: window side (4.5b).

    // C++: ArenaUI.cpp:1736-1760
    void RefreshRowLabel(IArenaUIHost<TId> h, Row row)
    {
        if (h.IsNone(row.Id))
            return;
        row.Caption = RowCaption(h, row.Id, row.List);
        if (row.List == ArenaList.Roster)
            RefreshStatusBar(h, row);   // only roster rows have a bar (statusBack NULL elsewhere: RefreshStatusBar returns)
    }

    // C++: ArenaUI.cpp:1762-1775
    void RefreshAvailabilityLabels(IArenaUIHost<TId> h)
    {
        foreach (var r in rosterRows) RefreshRowLabel(h, r);
        foreach (var r in teamARows) RefreshRowLabel(h, r);
        foreach (var r in teamBRows) RefreshRowLabel(h, r);
        foreach (var r in poolRows) RefreshRowLabel(h, r);

        if (Valid(h, previewChar))
            previewMeta = FighterDetailLabel(h, previewChar);
    }

    // C++: ArenaUI.cpp:1777-1782
    void RefreshRosterStatusBars(IArenaUIHost<TId> h)
    {
        foreach (var r in rosterRows) RefreshStatusBar(h, r);
    }

    // C++: ArenaUI.cpp:1784-1823
    void RefreshSummary(IArenaUIHost<TId> h)
    {
        string location = "Banner";
        if (h.LocationMode() == ArenaLocationMode.Arena)
            location = "Arena";
        else if (h.LocationMode() == ArenaLocationMode.SmallArena)
            location = "Small Arena";
        if (uiMode == ArenaUiMode.AvB)
            summary = "Teams   ·   A " + Int(teamA.Count) + " / B " + Int(teamB.Count) + "\n" + location + "   ·   Team knockout";
        else if (uiMode == ArenaUiMode.Teams1v1)
            summary = "Teams 1v1   ·   A " + Int(teamA.Count) + " / B " + Int(teamB.Count) + "\n" + location + "   ·   Sequential · Winner stays";
        else
            summary = "Last man standing   ·   " + Int(pool.Count) + " fighters\n" + location + "   ·   Last conscious fighter wins";
    }

    // C++: ArenaUI.cpp:1825-1869
    public bool CanStart(IArenaUIHost<TId> h)
    {
        if (h.TownBusy() || h.TownHasBooking())
            return false;
        if (h.SparActive())
            return false;
        if (h.IngressPending())
            return false;
        if (h.PrisonersReturning())
            return false;
        if (!h.HasLocationSite())
            return false;

        if (IsTeamUiMode())
        {
            if (teamA.Count == 0 || teamB.Count == 0)
                return false;
            for (int i = 0; i < teamA.Count; ++i)
                if (!IsAssignable(h, teamA[i]))
                    return false;
            for (int i = 0; i < teamB.Count; ++i)
                if (!IsAssignable(h, teamB[i]))
                    return false;
            if (!PrisonerMatchLogic.HasEnoughHandlers(CountPrisonersInMatch(h), CountAvailableHandlers(h)))
                return false;
            return true;
        }

        if (pool.Count < 2)
            return false;
        for (int i = 0; i < pool.Count; ++i)
            if (!IsAssignable(h, pool[i]))
                return false;
        if (!PrisonerMatchLogic.HasEnoughHandlers(CountPrisonersInMatch(h), CountAvailableHandlers(h)))
            return false;
        return true;
    }

    // C++: ArenaUI.cpp:1871-1888
    string FirstUnavailableAssignmentMessage(IArenaUIHost<TId> h, List<TId> fighters)
    {
        for (int i = 0; i < fighters.Count; ++i)
        {
            string reason = FighterUnavailableReason(h, fighters[i]);
            if (reason.Length == 0)
                continue;

            string message = Valid(h, fighters[i]) ? NameOf(h, fighters[i]) : "Fighter";
            message += " unavailable: ";
            message += reason;
            return message;
        }
        return "";
    }

    // C++: ArenaUI.cpp:1890-1920
    public string ValidationMessage(IArenaUIHost<TId> h)
    {
        if (h.IngressPending())
            return "[...] Fighters are moving to the match location.";
        if (h.SparActive())
            return "[LIVE] A spar is currently running.";
        if (h.PrisonersReturning())
            return "Returning prisoners to their cages...";
        if (!h.HasLocationSite())
            return h.MissingLocationMessage();
        if (IsTeamUiMode() && (teamA.Count == 0 || teamB.Count == 0))
            return "Assign at least one fighter to each team.";
        if (!IsTeamUiMode() && pool.Count < 2)
            return "Assign at least two fighters.";

        string unavailable = FirstUnavailableAssignmentMessage(h, teamA);
        if (unavailable.Length == 0)
            unavailable = FirstUnavailableAssignmentMessage(h, teamB);
        if (unavailable.Length == 0)
            unavailable = FirstUnavailableAssignmentMessage(h, pool);
        if (unavailable.Length != 0)
            return unavailable;

        int prisonersInMatch = CountPrisonersInMatch(h);
        int availableHandlers = CountAvailableHandlers(h);
        if (!PrisonerMatchLogic.HasEnoughHandlers(prisonersInMatch, availableHandlers))
            return PrisonerMatchLogic.FormatHandlerShortage(prisonersInMatch, availableHandlers);

        return "Ready";
    }

    // C++: ArenaUI.cpp:1922-2010 (RefreshCompactDrawers at its end: 4.5b)
    void RefreshStatusAndButtons(IArenaUIHost<TId> h)
    {
        bool active = h.SparActive();
        bool pending = h.IngressPending();
        bool edit = !active && !pending;
        bool canStart = edit && CanStart(h);

        if (active)
            RefreshRosterStatusBars(h);
        else
            RefreshAvailabilityLabels(h);

        if (pending)
            status = h.IngressStatus();
        else if (active)
            status = h.SparStatus();
        else if (!h.HasLocationSite())
            status = h.MissingLocationMessage();
        else if (!h.IsNone(previewChar) && !IsAssignable(h, previewChar))
            status = "Selected fighter is KO/dead — cannot assign";
        else if (IsTeamUiMode())
            status = dest == ArenaDest.TeamA
                ? "Click a face to send to Team A"
                : "Click a face to send to Team B";
        else
            status = "Click a face to add to pool";

        // Keep validation adjacent to the primary action; selection detail lives above.
        if (!active && !pending)
            status = ValidationMessage(h);
        if (h.TownBusy() || h.TownHasBooking())
            status = "Arena reserved by a town event.";

        bool townBusy = h.TownBusy();
        buttons.StartVisible = !active && !pending && !townBusy;
        buttons.StartEnabled = canStart;
        buttons.StopVisible = active || pending || townBusy;
        buttons.StopEnabled = active || pending || townBusy;
        buttons.StopCaption = pending ? "Cancel Match Start" : "Stop Fight";

        buttons.ModesEnabled = edit;
        buttons.LocsEnabled = edit;
        buttons.DestAEnabled = edit && !h.IsNone(previewChar) && IsAssignable(h, previewChar);
        buttons.DestBEnabled = edit && IsTeamUiMode() && !h.IsNone(previewChar) && IsAssignable(h, previewChar);
        buttons.RemoveEnabled = edit && !h.IsNone(previewChar) && IsAssigned(previewChar);
        buttons.BalanceEnabled = edit;
        buttons.ClearEnabled = edit && (teamA.Count != 0 || teamB.Count != 0 || pool.Count != 0);
        buttons.SwapEnabled = edit && IsTeamUiMode() && (teamA.Count != 0 || teamB.Count != 0);
        buttons.PresetsEnabled = edit;
    }

    // C++: ArenaUI.cpp:2012-2048
    void RefreshAll(IArenaUIHost<TId> h)
    {
        if (!created) return;
        RefreshRosterCaches(h);

        // Drop stale assignment pointers.
        teamA.RemoveAll(c => !VectorContains(rosterCache, c));
        teamB.RemoveAll(c => !VectorContains(rosterCache, c));
        pool.RemoveAll(c => !VectorContains(rosterCache, c));

        PruneLastNames();
        RefreshRosterList(h);
        RefreshAssignmentLists(h);
        RefreshSummary(h);
        if (!h.IsNone(previewChar) && !VectorContains(rosterCache, previewChar))
            SetPreview(h, h.None);
        else
            SetPreview(h, previewChar);
        RefreshStatusAndButtons(h);
    }

    /// <summary>Port only: forget the names of characters no list, row, preset, handler pair or the preview holds any more (RefreshAll's prune
    /// point), so the table never grows past what the window can show.</summary>
    void PruneLastNames()
    {
        var keep = new HashSet<TId>(rosterCache, eq);
        foreach (var rows in new[] { rosterRows, teamARows, teamBRows, poolRows })
            foreach (var r in rows) { keep.Add(r.Id); if (r.HasHandler) keep.Add(r.Handler); }
        foreach (var p in setupPresets) { keep.UnionWith(p.TeamA); keep.UnionWith(p.TeamB); keep.UnionWith(p.Pool); }
        foreach (var a in handlerAssignments) { keep.Add(a.Prisoner); keep.Add(a.Handler); }
        keep.Add(previewChar);
        foreach (var k in lastNames.Keys.Where(k => !keep.Contains(k)).ToList()) lastNames.Remove(k);
    }

    // C++: ArenaUI.cpp:2050-2075 OnModeAvB / OnModeTeams1v1 / OnModeLast
    public void OnMode(IArenaUIHost<TId> h, ArenaUiMode mode)
    {
        SwitchMode(h, mode);
        RefreshAssignmentLists(h);
        RefreshRosterList(h);
        RefreshSummary(h);
        RefreshStatusAndButtons(h);
    }

    // C++: ArenaUI.cpp:2077-2099 OnDestA / OnDestB
    public void OnDest(IArenaUIHost<TId> h, ArenaDest d)
    {
        if (h.SparActive() || h.IngressPending())
            return;
        dest = d;
        UpdateDestHighlight();
        if (!h.IsNone(previewChar) && IsAssignable(h, previewChar))
            AssignCharacter(h, previewChar);
        else
            RefreshStatusAndButtons(h);
    }

    // C++: ArenaUI.cpp:2101-2111
    public void OnRemove(IArenaUIHost<TId> h)
    {
        if (h.IngressPending())
        {
            status = "Walk-in in progress — cannot change roster";
            return;
        }
        if (!h.IsNone(previewChar) && IsAssigned(previewChar))
            RemoveCharacter(h, previewChar);
    }

    /// <summary>C++: ArenaUI.cpp:2124-2139 RefreshPresetButtons' caption of preset <paramref name="i"/> (0-2).</summary>
    public string PresetCaption(int i) => "Preset " + Int(i + 1) + (setupPresets[i].Valid ? " *" : "");

    // C++: ArenaUI.cpp:2141-2155
    void SaveSetupPreset(IArenaUIHost<TId> h, int index)
    {
        if (index < 0 || index >= 3)
            return;
        var preset = setupPresets[index];
        preset.Valid = true;
        preset.Mode = uiMode;
        preset.Destination = dest;
        preset.Location = h.LocationMode();
        preset.MedicalProtocol = medicalProtocol;
        preset.TeamA = [.. teamA];
        preset.TeamB = [.. teamB];
        preset.Pool = [.. pool];
    }

    // C++: ArenaUI.cpp:2157-2166
    void RemoveMissingPresetFighters(List<TId> fighters) => fighters.RemoveAll(c => !VectorContains(rosterCache, c));

    // C++: ArenaUI.cpp:2168-2194
    void LoadSetupPreset(IArenaUIHost<TId> h, int index)
    {
        if (index < 0 || index >= 3 || !setupPresets[index].Valid)
            return;

        var preset = setupPresets[index];
        uiMode = preset.Mode;
        dest = preset.Destination;
        medicalProtocol = preset.MedicalProtocol;
        teamA.Clear(); teamA.AddRange(preset.TeamA);
        teamB.Clear(); teamB.AddRange(preset.TeamB);
        pool.Clear(); pool.AddRange(preset.Pool);
        RemoveMissingPresetFighters(teamA);
        RemoveMissingPresetFighters(teamB);
        RemoveMissingPresetFighters(pool);
        h.SetLocationMode(preset.Location);
        h.SetProtocol(medicalProtocol);
        medicalProtocol = h.GetProtocol();
        SetPreview(h, h.None);
        UpdateModeVisibility(h);
        UpdateLocationHighlight(h);
        RefreshRosterList(h);
        RefreshAssignmentLists(h);
        RefreshSummary(h);
        RefreshStatusAndButtons(h);
    }

    /// <summary>C++: ArenaUI.cpp:2196-2217 OnPresetMousePressed: <paramref name="save"/> = the right button (a real right-click,
    /// task 4.5b: docs/divergences.md "Window framework") saves, else loads.</summary>
    public void OnPreset(IArenaUIHost<TId> h, int index, bool save)
    {
        if (h.SparActive() || h.IngressPending())
            return;
        if (index < 0 || index >= 3)
            return;

        if (save)
            SaveSetupPreset(h, index);
        else
            LoadSetupPreset(h, index);
    }

    // C++: ArenaUI.cpp:2219-2225
    public void OnBalanceMenu(IArenaUIHost<TId> h)
    {
        if (h.SparActive() || h.IngressPending())
            return;
        balanceMenuOpen = !balanceMenuOpen;
        UpdateBalanceMenuVisibility();
    }

    /// <summary>C++: ArenaUI.cpp:122-129 <c>BetterBalanceCandidate</c>: higher score first, then the name (std::string's operator&lt;: bytes).</summary>
    public static int CompareBalanceCandidates(float scoreA, string nameA, float scoreB, string nameB)
    {
        if (scoreA != scoreB)
            return scoreA > scoreB ? -1 : 1;
        return CompareNames(nameA, nameB);
    }

    /// <summary>std::string's <c>operator&lt;</c> over the UTF-8 bytes (unsigned, then length).</summary>
    public static int CompareNames(string a, string b)
    {
        int c = Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
        return c < 0 ? -1 : c > 0 ? 1 : 0;
    }

    // C++: ArenaUI.cpp:2227-2313 BalanceTeams (OnBalanceBySkill / OnBalanceByRating)
    public void BalanceTeams(IArenaUIHost<TId> h, bool byRating)
    {
        if (h.SparActive() || h.IngressPending())
            return;

        List<TId> selected;
        if (IsTeamUiMode())
        {
            selected = [.. teamA, .. teamB];
        }
        else
        {
            selected = [.. pool];
        }

        // With no selection, either balance method doubles as a quick "select all
        // available" action. Otherwise it only operates on the chosen fighters.
        List<TId> source = selected.Count == 0 ? [.. rosterCache] : selected;

        teamA.Clear();
        teamB.Clear();
        pool.Clear();
        var candidates = new List<(TId Fighter, float Score, string Name)>();
        for (int i = 0; i < source.Count; ++i)
        {
            TId c = source[i];
            if (!IsAssignable(h, c))
                continue;
            if (IsTeamUiMode())
            {
                float score = byRating
                    ? h.Rating(c)
                    : (h.Stats(c, out var stats) ? stats.Combat : 0.0f);
                candidates.Add((c, score, NameOf(h, c)));
            }
            else
            {
                pool.Add(c);
            }
        }

        if (IsTeamUiMode())
        {
            // std::sort with BetterBalanceCandidate; a stable sort keeps the roster order of two candidates with the same score and name
            // (std::sort leaves it unspecified; docs/divergences.md "Arena window")
            var sorted = candidates.Select((x, i) => (x, i))
                .OrderBy(p => p, Comparer<((TId Fighter, float Score, string Name) x, int i)>.Create((p, q) =>
                {
                    int c = CompareBalanceCandidates(p.x.Score, p.x.Name, q.x.Score, q.x.Name);
                    return c != 0 ? c : p.i.CompareTo(q.i);
                }))
                .Select(p => p.x).ToList();
            int targetA = (sorted.Count + 1) / 2;
            int targetB = sorted.Count / 2;
            float scoreA = 0.0f;
            float scoreB = 0.0f;
            for (int i = 0; i < sorted.Count; ++i)
            {
                bool toA = teamA.Count < targetA &&
                    (teamB.Count >= targetB || scoreA <= scoreB);
                if (toA)
                {
                    teamA.Add(sorted[i].Fighter);
                    scoreA += sorted[i].Score;
                }
                else
                {
                    teamB.Add(sorted[i].Fighter);
                    scoreB += sorted[i].Score;
                }
            }
        }
        balanceMenuOpen = false;
        UpdateBalanceMenuVisibility();
        RefreshRosterList(h);
        RefreshAssignmentLists(h);
        RefreshSummary(h);
        RefreshStatusAndButtons(h);
    }

    // C++: ArenaUI.cpp:2315-2327
    public void OnClearTeams(IArenaUIHost<TId> h)
    {
        if (h.SparActive() || h.IngressPending())
            return;
        teamA.Clear();
        teamB.Clear();
        pool.Clear();
        SetPreview(h, h.None);
        RefreshRosterList(h);
        RefreshAssignmentLists(h);
        RefreshSummary(h);
        RefreshStatusAndButtons(h);
    }

    // C++: ArenaUI.cpp:2329-2339
    public void OnSwapTeams(IArenaUIHost<TId> h)
    {
        if (!IsTeamUiMode() || h.SparActive() || h.IngressPending())
            return;
        var a = new List<TId>(teamA);
        teamA.Clear(); teamA.AddRange(teamB);
        teamB.Clear(); teamB.AddRange(a);
        RefreshRosterList(h);
        RefreshAssignmentLists(h);
        SetPreview(h, previewChar);
        RefreshSummary(h);
        RefreshStatusAndButtons(h);
    }

    // C++: ArenaUI.cpp:2341-2464
    public void OnStart(IArenaUIHost<TId> h)
    {
        if (!CanStart(h))
            return;

        RefreshRosterCaches(h);

        var match = CollectMatchFighters();

        var prisonerFighters = new List<TId>();
        for (int i = 0; i < match.Count; ++i)
            if (h.IsRosterPrisoner(match[i]))
                prisonerFighters.Add(match[i]);

        var handlers = BuildOrderedHandlers(h, prisonerFighters);

        if (prisonerFighters.Count != 0)
        {
            if (!h.PrepareMatch(prisonerFighters, handlers, out string prepStatus))
            {
                if (h.MatchIncludesPrisoner())
                {
                    string returnStatus = h.ReturnAllToCages();
                    if (returnStatus.Length != 0)
                        prepStatus = returnStatus;
                }
                status = prepStatus;
                RefreshStatusAndButtons(h);
                return;
            }
        }
        else
        {
            h.ClearMatchState();
        }

        TId[] escorts = [.. h.MatchHandlers()];

        var fighters = new List<TId>();
        var teams = new List<MatchRules.MatchTeam>();
        MatchRules.MatchMode mode;
        bool ok;
        h.SetProtocol(medicalProtocol);

        if (IsTeamUiMode())
        {
            for (int i = 0; i < teamA.Count; ++i)
            {
                fighters.Add(teamA[i]);
                teams.Add(MatchRules.MatchTeam.TeamA);
            }
            for (int i = 0; i < teamB.Count; ++i)
            {
                fighters.Add(teamB[i]);
                teams.Add(MatchRules.MatchTeam.TeamB);
            }
            mode = uiMode == ArenaUiMode.Teams1v1
                ? MatchRules.MatchMode.Teams1v1
                : MatchRules.MatchMode.TeamAvB;
            ok = h.IngressBegin(mode, [.. fighters], [.. teams], escorts);
        }
        else
        {
            mode = MatchRules.MatchMode.LastStanding;
            for (int i = 0; i < pool.Count; ++i)
            {
                fighters.Add(pool[i]);
                teams.Add(MatchRules.MatchTeam.None);
            }
            ok = h.IngressBegin(mode, [.. fighters], [.. teams], escorts);
        }

        if (!ok)
        {
            if (h.MatchIncludesPrisoner())
            {
                string returnStatus = h.ReturnAllToCages();
                string ingressStatus = h.IngressStatus();
                status = returnStatus.Length == 0 ? ingressStatus : returnStatus;
            }
            else
            {
                status = h.IngressStatus();
            }
        }

        if (ok)
        {
            if (created)
                SetVisible(false);   // HideSettingsTip + setVisible(false)
        }
        else
            RefreshStatusAndButtons(h);
    }

    // C++: ArenaUI.cpp:2466-2478
    public void OnStop(IArenaUIHost<TId> h)
    {
        if (h.TownBusy())
        {
            h.TownCancel();
            RefreshStatusAndButtons(h);
            return;
        }
        if (h.IngressPending())
            h.IngressCancel();
        h.SparStopManual();
        RefreshStatusAndButtons(h);
    }

    // C++: ArenaUI.cpp:2480-2756 RefreshCompactDrawers, OnToggleRosterDrawer, OnToggleSettingsDrawer, RefreshResponsiveMode, PanelLabel,
    // PanelButton, ButtonText, SyncButtonText, PlaceLabel, Heading, PlaceButtons, LayoutColumn, LayoutPanel, DestroyPanelWidgets: window side
    // (4.5b).

    /// <summary>C++: ArenaUI.cpp:2814-2924 CreatePanel's state part (its widgets are 4.5b's layout): the highlights, the preset captions and
    /// RefreshAll (:2911-2916), once.</summary>
    void CreatePanel(IArenaUIHost<TId> h)
    {
        if (created) return;
        created = true;
        UpdateModeVisibility(h);
        UpdateLocationHighlight(h);
        RefreshAll(h);
    }

    // C++: ArenaUI.cpp:2926-2936 (RefreshResponsiveMode: 4.5b)
    void ShowWindow(IArenaUIHost<TId> h)
    {
        if (!created)
            CreatePanel(h);
        SetVisible(true);
        ResetUiRefreshClock();
    }

    /// <summary>C++: ArenaUI.cpp:2938-2945 HidePanel (the close button / Escape, :2960-2965, :3024-3028, through the window side).</summary>
    public void HidePanel()
    {
        if (!created || !visible)
            return;
        SetVisible(false);
        ResetUiRefreshClock();
    }

    // C++: ArenaUI.cpp:2947-2958 TogglePanel has no caller in the C++: not ported.

    /// <summary>The window side reports the window closed (Escape, its close button, CloseAll; task 4.4's Closed click) under request
    /// <paramref name="serial"/>: as HidePanel, without a new request (the window is already closed). False when it is for another request.</summary>
    public bool OnClosedFromWindow(int serial)
    {
        if (!visible || serial != requestSerial)
            return false;
        visible = false;
        ResetUiRefreshClock();
        return true;
    }

    /// <summary>A new open / close request for the window side (task 4.4: ScreenRequests acts once per serial), only when the visibility
    /// changes: a repeat request is not a no-op there (a new serial makes it call Open / Close again, ScreenHostLogic.cs ScreenFrame.Run), and a
    /// C++ setVisible to the same value changes nothing.</summary>
    void SetVisible(bool show)
    {
        if (visible == show) return;
        visible = show;
        requestSerial++;
    }

    /// <summary>Whether a window command may run now (port: the C++ handlers are reachable only through a visible window; the sim half drops
    /// any command but the close while the window is closed, e.g. a click sent just before Start hid it).</summary>
    public bool AcceptsCommand(bool isClose) => isClose || (created && visible);

    /// <summary>C++: ArenaUI.cpp:3172-3185, the idle refresh while the window is visible: every 0.20 s (dt = the sim clock's tick, capped at 1 s)
    /// RefreshStatusAndButtons (RefreshResponsiveMode and RefreshPointerHighlights are window-side, 4.5b). True when it refreshed.</summary>
    public bool Tick(IArenaUIHost<TId> h, float dtSec)
    {
        if (!created || !visible)
            return false;
        if (!refresh.Advance(dtSec))
            return false;
        RefreshStatusAndButtons(h);
        return true;
    }

    // C++: ArenaUI.cpp:2967-3037 PollF8Toggle, PollEscClose (Escape closes the topmost of the mod's windows: MyGuiScreenHost.Escape, task 4.4),
    // :3039-3186 the TitleScreen / ForgottenGUI hooks (the frame loop is ProvingGroundsSystem's; the wheel fallbacks are window side, 4.5b).

    /// <summary>C++: ArenaUI.cpp:3197-3249 AbandonWorldState (the save safety's cleanup, WorldLifecycle.cpp:69, :263). The scroll offsets and the
    /// search placeholder are reset by the window side (4.5b) on a close.</summary>
    public void AbandonWorldState(IArenaUIHost<TId> h)
    {
        h.OtherWindowsAbandon();
        squadCache.Clear();
        rosterCache.Clear();
        filteredRoster.Clear();
        teamA.Clear();
        teamB.Clear();
        pool.Clear();
        rosterRows.Clear();
        teamARows.Clear();
        teamBRows.Clear();
        poolRows.Clear();
        handlerAssignments.Clear();
        lastNames.Clear();

        foreach (var p in setupPresets)
        {
            p.Valid = false;
            p.TeamA.Clear();
            p.TeamB.Clear();
            p.Pool.Clear();
        }

        previewChar = h.None;
        balanceMenuOpen = false;
        rosterQuery = "";
        ResetUiRefreshClock();

        // UI objects belong to MyGUI rather than the game world. Keep the panel
        // allocated, but hide it until rows can be rebuilt from the new world.
        if (created)
        {
            if (visible) SetVisible(false);
            previewImageVisible = false;
            status = "Idle";
        }
    }

    /// <summary>C++: ArenaUI.cpp:3251-3284 ShowFromRegistry: opens the window from a Registry or Banner (4.2's approach; until then the
    /// TEST-ONLY <c>pg arena open</c>), or the town window for a town Registry.</summary>
    public void ShowFromRegistry(IArenaUIHost<TId> h, ArenaBuilding? registry, bool bindRegistry = true)
    {
        h.TownBookieClose();
        h.RecruitmentAbandon();
        if (ArenaIdentityLogic.IsTownRegistry(registry))
        {
            HidePanel();
            h.TownArenaShow(registry!.Value, bindRegistry);
            return;
        }
        h.TownArenaClose();
        if (h.IngressPending())
        {
            ShowWindow(h);
            RefreshAll(h);
            status = h.IngressStatus();
            return;
        }

        if (bindRegistry)
            h.BindRegistry(registry);
        ShowWindow(h);
        RefreshAll(h);
        TId opener = h.UiOpener();
        if (Valid(h, opener) && VectorContains(rosterCache, opener))
            SetPreview(h, opener);
        status = h.SparActive() ? h.SparStatus() : h.IngressStatus();
    }

    // C++: ArenaUI.cpp:3286-3291
    public TId GetPreviewCharacter(IArenaUIHost<TId> h) => Valid(h, previewChar) ? previewChar : h.None;

    // ---------- the published view ----------

    ArenaRowView<TId> ViewOf(IArenaUIHost<TId> h, Row r) =>
        new(r.Id, r.List, NameOf(h, r.Id), r.Caption, !h.IsNone(r.Id) && Same(r.Id, previewChar), r.HealthVisible, r.HealthRatio, r.HealthBand,
            r.HasHandler, r.Handler, r.HandlerLabel, DragValid(h, r.Id), IsHandlerCandidate(h, r.Id), h.IsRosterPrisoner(r.Id));

    ValueList<ArenaRowView<TId>> Rows(IArenaUIHost<TId> h, List<Row> rows) => new(rows.Select(r => ViewOf(h, r)));

    /// <summary>What the window shows now (sim thread): the values the C++'s functions last wrote into its widgets, plus the visibilities and
    /// captions that follow the mode and the balance menu (UpdateModeVisibility :1316-1353, UpdateBalanceMenuVisibility :1302-1314).</summary>
    public ArenaUiView<TId> BuildView(IArenaUIHost<TId> h)
    {
        bool team = IsTeamUiMode();
        var b = buttons;
        var loc = hasHighlightedLocation ? highlightedLocation : h.LocationMode();
        var presets = new ArenaButtonView[3];
        var roster = Rows(h, rosterRows);
        var teamARowsView = Rows(h, teamARows);
        var teamBRowsView = Rows(h, teamBRows);
        var poolRowsView = Rows(h, poolRows);
        for (int i = 0; i < 3; i++) presets[i] = new ArenaButtonView(true, b.PresetsEnabled, false, PresetCaption(i));
        return new ArenaUiView<TId>
        {
            RequestSerial = requestSerial,
            RequestOpen = visible,
            Mode = uiMode,
            TeamMode = team,
            BalanceMenuOpen = balanceMenuOpen,
            Roster = roster,
            TeamA = teamARowsView,
            TeamB = teamBRowsView,
            Pool = poolRowsView,
            ShapeKey = ArenaUiView<TId>.ShapeOf(roster.Count, teamARowsView.Count, teamBRowsView.Count, poolRowsView.Count, team, balanceMenuOpen),
            RosterCount = rosterCount,
            RosterQuery = rosterQuery,
            DestLabel = lblDest,
            TeamALabel = lblTeamA,
            TeamBLabel = lblTeamB,
            PoolLabel = lblPool,
            ModeAvB = new(true, b.ModesEnabled, uiMode == ArenaUiMode.AvB, "Teams"),
            ModeTeams1v1 = new(true, b.ModesEnabled, uiMode == ArenaUiMode.Teams1v1, "Teams 1v1"),
            ModeLast = new(true, b.ModesEnabled, uiMode == ArenaUiMode.LastStanding, "Last Man Standing"),
            DestA = new(true, b.DestAEnabled, destASelected, team ? "Assign to Team A" : "Add Selected Fighter"),
            DestB = new(team, b.DestBEnabled, destBSelected, "Assign to Team B"),
            Remove = new(true, b.RemoveEnabled, false, "Remove Fighter"),
            Balance = new(true, b.BalanceEnabled, false, balanceCaption),
            BalanceSkill = new(balanceSkillVisible, b.BalanceEnabled, false, "By Skill"),
            BalanceRating = new(balanceRatingVisible, b.BalanceEnabled, false, "By Rating"),
            Swap = new(swapVisible, b.SwapEnabled, false, "Swap Teams"),
            Clear = new(clearVisible, b.ClearEnabled, false, "Clear Teams"),
            Presets = new(presets),
            LocArena = new(true, b.LocsEnabled, loc == ArenaLocationMode.Arena, "Arena"),
            LocSmall = new(true, b.LocsEnabled, loc == ArenaLocationMode.SmallArena, "Small"),
            LocBanner = new(true, b.LocsEnabled, loc == ArenaLocationMode.Banner, "Banner"),
            Medical = new(true, true, false, MedicalCaption(medicalProtocol)),
            MedicalTip = GetMedicalProtocolTip(medicalProtocol),
            Recruit = new(true, true, false, "Recruit Prisoners"),
            Start = new(b.StartVisible, b.StartEnabled, false, "Start Fight"),
            Stop = new(b.StopVisible, b.StopEnabled, false, b.StopCaption),
            HasPreview = !h.IsNone(previewChar),
            Preview = previewChar,
            PreviewImageVisible = previewImageVisible,
            PreviewName = previewName,
            PreviewMeta = previewMeta,
            PreviewStats = previewStats,
            Summary = summary,
            Status = status,
        };
    }
}

/// <summary>
/// Port only: the Arena window's site check (<c>ArenaIngress::HasLocationSite</c>, whose <c>FindSiteForMode</c> writes the C++ "site search"
/// debug line on every call) kept for <see cref="RefreshMs"/> per (location, bound Registry). The C++ calls it up to three times per 0.20 s refresh
/// while the window is open (RefreshStatusAndButtons's status chain, CanStart, ValidationMessage, :1835, :1898, :1940); the port asks again
/// when the location or the Registry changes, after <see cref="RefreshMs"/>, and on <see cref="Forget"/> (the window opens, Start). Same answer
/// at those moments; fewer log lines (docs/divergences.md "Arena window").
/// </summary>
public sealed class SiteCheckCache
{
    /// <summary>How long one answer is kept.</summary>
    public const uint RefreshMs = 1000;

    bool has, value;
    ArenaLocationMode mode;
    ArenaBuildingRef? registry;
    uint at;

    /// <summary>The kept answer for (<paramref name="location"/>, <paramref name="boundRegistry"/>), or <paramref name="compute"/>'s new one.</summary>
    public bool Get(ArenaLocationMode location, ArenaBuildingRef? boundRegistry, uint nowMs, Func<bool> compute)
    {
        if (has && mode == location && Nullable.Equals(registry, boundRegistry) && unchecked(nowMs - at) < RefreshMs)
            return value;
        value = compute();
        has = true;
        mode = location;
        registry = boundRegistry;
        at = nowMs;
        return value;
    }

    /// <summary>The next <see cref="Get"/> asks again.</summary>
    public void Forget() => has = false;
}

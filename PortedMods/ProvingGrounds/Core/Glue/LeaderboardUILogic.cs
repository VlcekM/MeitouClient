using System.Globalization;
using System.Text;

namespace ProvingGrounds.Core.Glue;

// Port of the rules half of upstream/ProvingGrounds/plugin/src/LeaderboardUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.6a (PA §2.15):
// Show / Close / IsVisible (:705-719), RefreshContent's texts (:654-702), RebuildRows' row texts (:585-652), OnResetClicked (:70-91), and
// LeaderboardStore::GetStandings (LeaderboardStore.cpp:397-423, the rows with at least one match, live names, rating desc then name) which the
// window reads. The window itself (EnsureWindow :514-583, LayoutWindow :471-512, LayoutRows :291-457, FitWindowToScreen :459-469, the
// resize in Tick :721-736) is the window half: Core/Glue/LeaderboardUILayout.cs + src/Ui/LeaderboardUI.cs. The sim half
// (src/Game/LeaderboardUISim.cs) owns this object.

/// <summary>What the rules half of LeaderboardUI reads (the sim half implements it over the world's ledger, task 2.4).</summary>
public interface ILeaderboardHost<TId>
{
    /// <summary><c>LeaderboardStore::HasActiveSave()</c> (= the ledger is Ready).</summary>
    bool HasActiveSave { get; }
    /// <summary><c>LeaderboardStore::GetActiveSaveKey()</c>.</summary>
    string ActiveSaveKey { get; }
    /// <summary><c>ArenaNativePersistence::CanResetUnmatchedSidecar()</c>.</summary>
    bool CanResetUnmatchedSidecar();
    /// <summary><c>ArenaNativePersistence::ResetUnmatchedSidecar()</c>.</summary>
    bool ResetUnmatchedSidecar();
    /// <summary>The standings table of <paramref name="kind"/> as the ledger holds it (<c>LeaderboardData::Standings(g_data, kind)</c>).</summary>
    IReadOnlyList<LeaderboardData.Standing> RawStandings(LeaderboardData.Kind kind);
    /// <summary><c>LeaderboardStore::FindRatedCharacter(kind, id, source)</c> found a valid character (GetStandings passes no source, RebuildRows the
    /// building); its name for GetStandings' live name.</summary>
    bool TryRatedCharacter(LeaderboardData.Kind kind, string id, bool withSource, out TId character, out string name);
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);
}

/// <summary>One standings row as RebuildRows (:593-651) fills it.</summary>
public readonly record struct LeaderboardRowView<TId>(string Rank, string Name, string Rating, string Record, string Matches, string Details,
    string Initial, int RankColour, TId Character, bool HasCharacter);

/// <summary>Texts of the Leaderboard window the C++ writes at creation or in its handlers.</summary>
public static class LeaderboardUiTexts
{
    /// <summary>C++ :525 the window's creation caption.</summary>
    public const string Caption = "Proving Grounds - Leaderboard";
    public const string CaptionTown = "Proving Grounds - Town Leaderboard", CaptionPlayer = "Proving Grounds - Player Leaderboard";   // :662-664
    /// <summary>C++ :534, :537 the creation captions of the subtitle and the empty label.</summary>
    public const string Subtitle = "Arena rating", Empty = "No rated fighters yet.\nWin arena matches to climb the board.";
    /// <summary>C++ :544.</summary>
    public const string Champions = "Champions of the Pit";
    /// <summary>C++ :553 the column headings.</summary>
    public static readonly IReadOnlyList<string> Headings = ["Rank", "Fighter", "Rating", "Record", "Matches"];
    /// <summary>C++ :564, :566, :76.</summary>
    public const string Close = "Close", Reset = "Reset arena progress", ResetConfirm = "Confirm reset to zero";
    public const string EmptyTown = "No town bouts recorded yet.\nComplete a town arena bout to open the standings.";   // :674
    public const string EmptyPlayer = "No rated player fighters yet.\nComplete a player-v-player arena match to open the standings.";   // :675
    public const string ResetSubtitle = "Arena progress blocked: no sidecar matches this native save.";   // :681
    public const string ResetEmpty = "A matching sidecar and backups could not be recovered. Reset starts arena ratings, rewards and challenges from zero for this slot. Existing files are not deleted; a valid old sidecar will be backed up on the next save. Click Reset twice to confirm, then save immediately.";   // :682
    public const string ResetFailed = "Arena reset could not be completed. Preserve this save and reload before retrying.";   // :83
    public const string ResetDoneSubtitle = "Fresh arena progress - save this slot now, then reload it.";   // :88
    public const string ResetDoneEmpty = "Arena progress reset to zero. Save now to publish the new sidecar; the old sidecar is backed up, if present.";   // :89
}

/// <summary>The published view of the Leaderboard window. <see cref="Revision"/> counts the C++'s layout passes that follow a content change
/// (RefreshContent :687-688, a completed reset :90); a caption written without one (the reset's first click, a failed reset, Close) changes
/// only the texts.</summary>
public sealed record LeaderboardUiView<TId>
{
    public int Revision { get; init; }
    /// <summary>Counts Shows (RebuildRows puts the canvas back at the top, :591; a completed reset's layout pass keeps the offset).</summary>
    public int Shows { get; init; }
    public string Caption { get; init; } = LeaderboardUiTexts.Caption;
    public string Subtitle { get; init; } = LeaderboardUiTexts.Subtitle;
    public string Empty { get; init; } = LeaderboardUiTexts.Empty;
    public bool ResetVisible { get; init; }
    public string ResetCaption { get; init; } = LeaderboardUiTexts.Reset;
    public ValueList<LeaderboardRowView<TId>> Rows { get; init; } = ValueList<LeaderboardRowView<TId>>.Empty;
    public int RequestSerial { get; init; }
    public bool RequestOpen { get; init; }
}

/// <summary>The rules half of LeaderboardUI.cpp. Sim thread (one object per world).</summary>
public sealed class LeaderboardUILogic<TId>
{
    bool visible, resetConfirm;   // C++ g_window->getVisible(), :37 g_resetConfirm
    int requestSerial, revision, shows;
    string caption = LeaderboardUiTexts.Caption, subtitle = LeaderboardUiTexts.Subtitle, empty = LeaderboardUiTexts.Empty;
    string resetCaption = LeaderboardUiTexts.Reset;
    bool resetVisible;
    ValueList<LeaderboardRowView<TId>> rows = ValueList<LeaderboardRowView<TId>>.Empty;

    /// <summary>C++: LeaderboardUI.cpp:719 IsVisible.</summary>
    public bool IsVisible => visible;
    public bool ResetConfirm => resetConfirm;

    /// <summary>C++ <c>StandingsLess</c> (LeaderboardStore.cpp:83-86): <see cref="LeaderboardStoreRules.CompareStandings"/> (task 5.1: the store owns it).</summary>
    public static int CompareStandings(LeaderboardData.Standing a, LeaderboardData.Standing b) => LeaderboardStoreRules.CompareStandings(a, b);

    /// <summary>C++: LeaderboardStore.cpp:397-423 GetStandings over the window's host: <see cref="LeaderboardStoreRules.SortedStandings"/> (the
    /// one copy of the rule, task 5.1) with the live name of FindRatedCharacter without a building.</summary>
    public static List<LeaderboardData.Standing> GetStandings(ILeaderboardHost<TId> h, LeaderboardData.Kind kind) =>
        LeaderboardStoreRules.SortedStandings(h.RawStandings(kind),
            id => h.TryRatedCharacter(kind, id, false, out _, out string liveName) ? liveName : "");

    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>C++ :612-613 the placeholder: "?" for an empty name, else <c>name.substr(0, 1)</c>, the first BYTE of the UTF-8 name: the first
    /// character for an ASCII one; for a non-ASCII name the C++ hands MyGUI a lone lead byte, here U+FFFD (docs/divergences.md).</summary>
    public static string Placeholder(string name)
    {
        if (name.Length == 0) return "?";
        return name[0] < 0x80 ? name[..1] : "�";
    }

    /// <summary>C++: LeaderboardUI.cpp:593-651 the texts of row <paramref name="i"/> (sprintf_s into char[32] / [64] / [160]).</summary>
    public static LeaderboardRowView<TId> RowTexts(int i, LeaderboardData.Standing record, bool hasCharacter, TId character)
    {
        string rank = ((uint)(i + 1)).ToString(CultureInfo.InvariantCulture);
        string rating = I((int)(record.Mmr + .5f));
        string result = I(record.Wins) + " - " + I(record.Losses);
        string matches = I(record.Matches);
        string details = "Rating " + rating + "  •  " + result + "  •  " + matches + " " + (record.Matches == 1 ? "bout" : "bouts");
        return new LeaderboardRowView<TId>(rank, record.Name, rating, result, matches, details, Placeholder(record.Name), i < 3 ? i + 1 : 0,
            character, hasCharacter);
    }

    /// <summary>C++: LeaderboardUI.cpp:705-716 Show: RefreshContent (:654-702), then visible.</summary>
    public void Show(ILeaderboardHost<TId> h, LeaderboardData.Kind kind)
    {
        List<LeaderboardData.Standing> standings = GetStandings(h, kind);
        bool town = kind == LeaderboardData.Kind.Town;
        caption = town ? LeaderboardUiTexts.CaptionTown : LeaderboardUiTexts.CaptionPlayer;
        subtitle = h.HasActiveSave
            ? (town ? "Scratch circuit standings - " : "Player arena standings - ") + h.ActiveSaveKey
            : town ? "Scratch circuit standings - no active save" : "Player arena standings - no active save";
        empty = town ? LeaderboardUiTexts.EmptyTown : LeaderboardUiTexts.EmptyPlayer;
        resetConfirm = false;
        resetCaption = LeaderboardUiTexts.Reset;
        resetVisible = h.CanResetUnmatchedSidecar();
        if (resetVisible)
        {
            subtitle = LeaderboardUiTexts.ResetSubtitle;
            empty = LeaderboardUiTexts.ResetEmpty;
        }
        var list = new List<LeaderboardRowView<TId>>(standings.Count);
        for (int i = 0; i < standings.Count; ++i)
        {
            bool has = h.TryRatedCharacter(kind, standings[i].Id, true, out TId c, out _);
            list.Add(RowTexts(i, standings[i], has, c));
        }
        rows = new ValueList<LeaderboardRowView<TId>>(list);
        revision++;
        shows++;
        SetVisible(true);
        h.Debug("Proving Grounds: native leaderboard UI shown");
    }

    /// <summary>C++: LeaderboardUI.cpp:70-91 OnResetClicked. True when the view changed.</summary>
    public bool OnResetClicked(ILeaderboardHost<TId> h)
    {
        if (!h.CanResetUnmatchedSidecar()) return false;
        if (!resetConfirm)
        {
            resetConfirm = true;
            resetCaption = LeaderboardUiTexts.ResetConfirm;
            return true;
        }
        if (!h.ResetUnmatchedSidecar())
        {
            resetConfirm = false;
            resetCaption = LeaderboardUiTexts.Reset;
            empty = LeaderboardUiTexts.ResetFailed;
            return true;
        }
        resetConfirm = false;
        resetVisible = false;
        subtitle = LeaderboardUiTexts.ResetDoneSubtitle;
        empty = LeaderboardUiTexts.ResetDoneEmpty;
        revision++;   // LayoutWindow (:90)
        return true;
    }

    /// <summary>C++: LeaderboardUI.cpp:718 Close.</summary>
    public void Close()
    {
        SetVisible(false);
        resetConfirm = false;
        resetCaption = LeaderboardUiTexts.Reset;
    }

    /// <summary>The window side reports the window closed (Escape, its title bar's close button) under request <paramref name="serial"/>: as
    /// Close, without a new request. False for another request.</summary>
    public bool OnClosedFromWindow(int serial)
    {
        if (!visible || serial != requestSerial)
            return false;
        visible = false;
        resetConfirm = false;
        resetCaption = LeaderboardUiTexts.Reset;
        return true;
    }

    void SetVisible(bool show)
    {
        if (visible == show) return;
        visible = show;
        requestSerial++;
    }

    public LeaderboardUiView<TId> BuildView() => new()
    {
        Revision = revision, Shows = shows, Caption = caption, Subtitle = subtitle, Empty = empty, ResetVisible = resetVisible, ResetCaption = resetCaption,
        Rows = rows, RequestSerial = requestSerial, RequestOpen = visible,
    };
}

/// <summary>
/// C++ <c>LeaderboardStore::FindRatedCharacter</c> (LeaderboardStore.cpp:615-667) for many ids at once (task 4.6a review): the C++ walks the
/// player's squad, the roster prisoners, the match prisoners and then (town board with a building) the living characters within 2500 of the building,
/// once per row; here each candidate's key is read once, into a dictionary in that order with the first match kept (TryAdd), so every lookup
/// answers what the C++ walk answers. The town sphere is read only when a lookup needs it. One index per Show / click (the world may change between).
/// </summary>
public sealed class RatedCharacterIndex<TId>
{
    readonly Func<IEnumerable<(TId Id, string Key)>> primary;
    readonly Func<IEnumerable<(TId Id, string Key)>>? town;
    Dictionary<string, TId>? first, near;

    /// <param name="primary">The squad, then the roster prisoners, then the match prisoners, each valid, with its key ("" without one).</param>
    /// <param name="town">The living characters of the town search (null: no town search, the player board or no building).</param>
    public RatedCharacterIndex(Func<IEnumerable<(TId Id, string Key)>> primary, Func<IEnumerable<(TId Id, string Key)>>? town)
    {
        this.primary = primary;
        this.town = town;
    }

    static Dictionary<string, TId> Build(IEnumerable<(TId Id, string Key)> candidates)
    {
        var d = new Dictionary<string, TId>(StringComparer.Ordinal);
        foreach (var (id, key) in candidates)
            if (key.Length > 0) d.TryAdd(key, id);
        return d;
    }

    /// <summary>The first character in the C++ order whose key is <paramref name="id"/>; the town search only when <paramref name="withTown"/>.
    /// False for "" (the C++ returns NULL for an empty id).</summary>
    public bool TryFind(string id, bool withTown, out TId character)
    {
        character = default!;
        if (id.Length == 0)
            return false;
        first ??= Build(primary());
        if (first.TryGetValue(id, out character!))
            return true;
        if (!withTown || town is null)
            return false;
        near ??= Build(town());
        return near.TryGetValue(id, out character!);
    }
}

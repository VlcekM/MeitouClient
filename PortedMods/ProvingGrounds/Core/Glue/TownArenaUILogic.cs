using System.Text;

namespace ProvingGrounds.Core.Glue;

// Port of the sim half of upstream/ProvingGrounds/plugin/src/TownArenaUI.cpp (Proving Grounds by Shutori, GPLv3), task 7.5 (TW §2.5; CAP 7): the
// team the window holds (lineup, TownArenaUI.cpp:41, the selection identities), Resolve / Selected (:235-250), RowReleased's toggle (:280-297), Book
// (:312-320), Challenge (:321-326), BuyRefresh (:327-330), Division (:331-333), Close / AbandonWorldState (:904-908), Tick's refresh (:909-956:
// RefreshRoster :417-452, RefreshCard :494-561, RefreshSelectionPrompt :562-577, RefreshSkarn's texts :342-368) and Show (:957-965). Every text is
// the C++'s byte for byte; numbers through CMath.FormatF. The window itself (layout, widgets, portraits, the tips, LayoutTownPanel) is task 7.6: it
// draws the published view and sends PgCommands; the C++ handlers ran inside the widget events, here they run when the command arrives in the sim,
// re-checked against what the window showed (a request number per booking / refresh: one at or below LastSeq is dropped, as task 6.4a's
// recruitment; the shown quote, caption or cost must still be the shown one, else nothing runs).

/// <summary>What the window's sim half reads besides TownArena (the squad, the combat profile of a portrait).</summary>
public interface ITownArenaUIHost<TId>
{
    /// <summary><c>SquadUtil::CollectPlayerSquad(current)</c>.</summary>
    IReadOnlyList<TId> PlayerSquad();
    /// <summary><c>ArenaCombatProfile::Read(c)</c>.</summary>
    ArenaCombatProfile.Profile Profile(TId c);
    /// <summary><c>RosterStatus::Read(c, status)</c>'s recovery fraction and whether it could be read (the row's status bar).</summary>
    bool Recovery(TId c, out float recovery, out RosterStatusPolicy.Band band);
    /// <summary><c>PGLog::Error</c>.</summary>
    void Error(string line);
}

/// <summary>One fighter row of the window (RefreshRoster, :417-452).</summary>
public sealed record TownArenaRowView<TId>(string Id, TId Character, string Caption, bool Selected, bool StatusVisible, float Recovery,
    RosterStatusPolicy.Band Band);

/// <summary>One opponent of a card (RefreshCard's per-member widgets, :520-547).</summary>
public sealed record TownArenaPortraitView<TId>(TId Character, string Key, bool PortraitVisible, bool PlaceholderVisible, string Placeholder,
    float Alpha, bool NameVisible, string Name, bool StatsVisible, string Stats, string Tip);

/// <summary>One card (RefreshCard :494-561 / RefreshSelectionPrompt :562-577).</summary>
public sealed record TownArenaCardView<TId>(int Slot, bool Visible, string Heading, string Text, bool TextVisible, bool TermsVisible, string Terms,
    bool BookVisible, bool BookEnabled, int Rarity, bool UniqueLegendary, int PortraitCount, IReadOnlyList<TownArenaPortraitView<TId>> Portraits,
    TownChallengeBuyInPolicy.Quote Quote, string OfferKey = "");

/// <summary>The window's whole published state (task 7.5). <see cref="Key"/> holds every field as text, so two views with the same content compare
/// equal (the model is replaced only when it changed).</summary>
public sealed class TownArenaUiView<TId>
{
    public bool Open; public long RequestSerial; public long LastSeq;
    public string Status = "", Feedback = "", Squad = "", Direct = "", NextRefresh = "", RefreshButton = "", BookingButton = "", Progress = "";
    public bool EmptyRoster, RefreshEnabled, BookingEnabled, DivisionsEnabled, SelectionPrompt, SkarnUnlocked;
    public int Division;
    /// <summary>Task 7.6: the cost the refresh button's caption shows (GetChallengeRefreshCost, Tick :919), sent back with its click.</summary>
    public int RefreshCost;
    public string SkarnState = "", SkarnPlaceholder = "";
    public TId Skarn = default!; public string SkarnKey = "";
    public string[] SkarnTip = new string[7];
    public bool[] SkarnTipGood = new bool[7];
    public List<TownArenaRowView<TId>> Rows = [];
    public List<TownArenaCardView<TId>> Cards = [];
    public string Key = "";
}

/// <summary>The window's sim half over TownArena (one per world, sim thread).</summary>
public sealed class TownArenaUILogic<TId>
{
    /// <summary>C++ <c>now - lastRefresh &lt; 250</c> (TownArenaUI.cpp:912).</summary>
    public const uint RefreshMs = 250;

    readonly List<string> lineup = [];   // C++ lineup (the selection identities, :41)
    bool visible;                        // window->getVisible()
    uint lastRefresh;                    // C++ lastRefresh (0 = refresh now: RefreshNow, :252)
    string lastStatus = "";              // C++ lastStatus
    string feedback = "";                // the status label's caption (Feedback, :253)
    long requestSerial, lastSeq;
    TownArenaUiView<TId> view = new();

    public bool IsVisible => visible;
    public IReadOnlyList<string> Lineup => lineup;
    public TownArenaUiView<TId> View => view;

    // C++: TownArenaUI.cpp:235-242
    static TId Resolve(ITownHost<TId> h, ITownArenaUIHost<TId> u, string id, out bool found)
    {
        found = false;
        if (id.Length == 0) return h.None;
        IReadOnlyList<TId> current = u.PlayerSquad();
        for (int i = 0; i < current.Count; ++i)
            if (TownMatchmakingRuntimeLogic.SelectionIdentity(h, current[i]) == id) { found = true; return current[i]; }
        return h.None;
    }

    // C++: TownArenaUI.cpp:243-250
    public List<TId> Selected(ITownHost<TId> h, ITownArenaUIHost<TId> u)
    {
        var result = new List<TId>();
        for (int i = 0; i < lineup.Count; ++i)
        {
            TId c = Resolve(h, u, lineup[i], out bool found);
            if (found) result.Add(c);
        }
        return result;
    }

    bool Assigned(string id) => lineup.Contains(id);

    /// <summary>C++: TownArenaUI.cpp:957-965 <c>Show(registry, bindRegistry)</c> (the registry is checked and bound by the caller, as ArenaUI's
    /// ShowFromRegistry passes it on).</summary>
    public void Show(TownArenaLogic<TId> town, ITownHost<TId> h, ITownArenaUIHost<TId> u, ArenaBuilding registry, bool bindRegistry)
    {
        if (!ArenaIdentityLogic.IsTownRegistry(registry) || !ArenaIdentityLogic.IsRegistryFinished(registry)) return;
        if (bindRegistry) town.BindRegistry(registry);
        town.RefreshMatchmakingRoster();
        lastStatus = town.GetStatus(); feedback = lastStatus;
        visible = true; ++requestSerial; lastRefresh = 0;
        Tick(town, h, u);
    }

    // C++: TownArenaUI.cpp:904
    public void Close() { visible = false; ++requestSerial; Rebuild(view.Cards, view.Rows); }

    // C++: TownArenaUI.cpp:905-908 (the window is the window half's)
    public void AbandonWorldState() { lineup.Clear(); lastStatus = ""; visible = false; feedback = ""; view = new TownArenaUiView<TId>(); }

    /// <summary>Port only: the window was closed by the player (Escape, the title bar: the framework's Closed report), as Close.</summary>
    public void ClosedFromWindow() => Close();

    // ---------- the handlers (run when their command arrives) ----------

    /// <summary>C++: TownArenaUI.cpp:280-297 <c>RowReleased</c> for the row of <paramref name="id"/> (a selection identity, never a row index).
    /// <paramref name="seq"/>: the window's request number (one at or below LastSeq is dropped, so a repeated or late release never toggles twice).</summary>
    public bool OnRowReleased(TownArenaLogic<TId> town, ITownHost<TId> h, ITownArenaUIHost<TId> u, long seq, string id)
    {
        if (!Accept(seq)) return false;
        TId c = Resolve(h, u, id, out _);
        bool ready = town.GetPlayerReadiness(h, c, out string reason);
        if (TownRosterSelection.Toggle(lineup, id, ready, town.IsLineupLocked()))
        {
            feedback = town.GetStatus();
            lastRefresh = 0;
        }
        else if (town.IsLineupLocked()) feedback = "Cancel the booking before editing the team.";
        else if (!ready) feedback = reason;
        else feedback = "Town teams can contain up to three fighters.";
        Rebuild(view.Cards, view.Rows);
        return true;
    }

    /// <summary>C++: TownArenaUI.cpp:312-320 <c>Book</c> (the "Book direct 1v1" / "Cancel booking" button). <paramref name="shownCaption"/> = the
    /// button's caption as drawn: the command runs only while it is still the caption (a booking made or cancelled in between changes it).</summary>
    public bool OnBook(TownArenaLogic<TId> town, ITownHost<TId> h, ITownArenaUIHost<TId> u, long seq, string shownCaption)
    {
        if (!Accept(seq) || shownCaption != (town.HasBooking() ? "Cancel booking" : "Book direct 1v1")) return false;
        if (!(!town.IsPlayerMatch() && (town.HasBooking() || lineup.Count == 1))) return false;   // the button's enabled rule (Tick :925)
        if (town.HasBooking()) town.CancelBooking(h);
        else
        {
            List<TId> selected = Selected(h, u);
            if (selected.Count != 1 || lineup.Count != 1) { feedback = "Assign exactly one fighter for a direct 1v1."; Rebuild(view.Cards, view.Rows); return true; }
            town.BookPlayer(h, selected[0]);
        }
        lastStatus = town.GetStatus(); feedback = lastStatus; lastRefresh = 0;
        Rebuild(view.Cards, view.Rows);
        return true;
    }

    /// <summary>The offer a card shows, as the window draws it and a Book click names it: the slot's stored epoch and the displayed offer's
    /// opponents (TownArena.cpp:951-958 DisplayedOffer).</summary>
    public static string OfferKey(TownArenaLogic<TId> town, ITownHost<TId> h, int slot)
    {
        if (slot < 0 || slot >= 5) return "";
        TownChallengePolicy.Card card = h.TownChallenges();
        TownChallengePolicy.Offer o = TownArena.DisplayedOffer(card, town.State.Display, slot);
        var k = new StringBuilder();
        k.Append(slot).Append(':').Append(card.Offers[slot].Epoch).Append(':').Append(o.Epoch).Append(':').Append(o.Encounter).Append(':').Append(o.Draw);
        for (int i = 0; i < Math.Max(0, Math.Min(4, TownChallengePolicy.Opponents(o))); ++i) k.Append(':').Append(TownChallengePolicy.OpponentIdConst(o, i));
        return k.ToString();
    }

    /// <summary>C++: TownArenaUI.cpp:321-326 <c>Challenge</c> (a card's Book button). Runs only while the card still shows the offer
    /// (<paramref name="shownOffer"/>, <see cref="OfferKey"/>) and the quote the window drew, and while the button is still enabled by the C++'s
    /// rule (Tick :948: <c>!Locked() &amp;&amp; !IsPlayerMatch() &amp;&amp; GetChallengeBuyIn(i).Valid()</c>).</summary>
    public bool OnChallenge(TownArenaLogic<TId> town, ITownHost<TId> h, ITownArenaUIHost<TId> u, long seq, int slot, string shownOffer,
        TownChallengeBuyInPolicy.Quote shown)
    {
        if (!Accept(seq)) return false;
        TownChallengeBuyInPolicy.Quote now = town.GetChallengeBuyIn(h, slot);
        if (town.IsLineupLocked() || town.IsPlayerMatch() || !now.Valid()) return false;   // the button's enabled rule (:948)
        if (OfferKey(town, h, slot) != shownOffer) return false;
        if (now.BuyIn != shown.BuyIn || now.Payout != shown.Payout) return false;
        List<TId> selected = Selected(h, u);
        if (selected.Count != lineup.Count || selected.Count == 0) { feedback = "Assign 1-3 available squad fighters before booking."; Rebuild(view.Cards, view.Rows); return true; }
        town.BookChallenge(h, selected, slot);
        lastStatus = town.GetStatus(); feedback = lastStatus; lastRefresh = 0;
        Rebuild(view.Cards, view.Rows);
        return true;
    }

    /// <summary>C++: TownArenaUI.cpp:327-330 <c>BuyRefresh</c>. Runs only while the button still shows <paramref name="shownCost"/>.</summary>
    public bool OnBuyRefresh(TownArenaLogic<TId> town, ITownHost<TId> h, long seq, int shownCost)
    {
        if (!Accept(seq) || town.GetChallengeRefreshCost(h) != shownCost) return false;
        town.BuyChallengeRefresh(h);
        lastStatus = town.GetStatus(); feedback = lastStatus; lastRefresh = 0;
        Rebuild(view.Cards, view.Rows);
        return true;
    }

    /// <summary>C++: TownArenaUI.cpp:331-333 <c>Division</c>.</summary>
    public void OnDivision(TownArenaLogic<TId> town, ITownHost<TId> h, int division)
    {
        town.SetDivision(h, division); lastRefresh = 0;
    }

    /// <summary>Port only (task 6.4a's rule): a request is recorded (LastSeq) when it arrives; one at or below LastSeq is an old one, dropped.</summary>
    bool Accept(long seq)
    {
        if (seq <= lastSeq) return false;
        lastSeq = seq;
        Rebuild(view.Cards, view.Rows);   // a new view with the new LastSeq: a published view is never changed
        return visible;
    }

    // ---------- Tick (TownArenaUI.cpp:909-956) ----------

    /// <summary>C++: TownArenaUI.cpp:909-956 <c>Tick</c> while visible, at most every 250 ms: the roster, the preview, the header captions, the cards.</summary>
    public void Tick(TownArenaLogic<TId> town, ITownHost<TId> h, ITownArenaUIHost<TId> u)
    {
        if (!visible) return;
        uint now = h.NowMs;
        if (lastRefresh != 0 && now - lastRefresh < RefreshMs) return;
        lastRefresh = now == 0 ? 1u : now;
        try { Refresh(town, h, u); }
        catch (Exception e)
        {
            // :955-956 catch (const std::exception& error) / catch (...): the error line (a .NET exception always has a message: the first form), AbandonWindow
            u.Error("Proving Grounds: town registry refresh failed: " + e.Message);
            AbandonWindow();
        }
    }

    /// <summary>C++: TownArenaUI.cpp:784-805 <c>AbandonWindow</c> (the window gone; the next Show builds it again): not visible, the refresh clock
    /// reset, the drawn view emptied. The team (lineup) stays, as the C++'s.</summary>
    void AbandonWindow()
    {
        if (visible) ++requestSerial;
        visible = false;
        lastRefresh = 0;
        Rebuild([], []);
    }

    // C++: TownArenaUI.cpp:915-954 (the try block of Tick)
    void Refresh(TownArenaLogic<TId> town, ITownHost<TId> h, ITownArenaUIHost<TId> u)
    {
        var rows = RefreshRoster(town, h, u);
        town.UpdateMatchmakingPreview(h, Selected(h, u));
        var v = new TownArenaUiView<TId>();
        v.Direct = town.GetDirectCaption(h);
        v.NextRefresh = town.GetNextChallengeRefreshCaption(h);
        v.RefreshCost = town.GetChallengeRefreshCost(h);
        v.RefreshButton = "Buy Refresh (" + v.RefreshCost.ToString(System.Globalization.CultureInfo.InvariantCulture) + " Cats)";
        v.RefreshEnabled = town.CanBuyChallengeRefresh(h);
        v.BookingButton = town.HasBooking() ? "Cancel booking" : "Book direct 1v1";
        v.BookingEnabled = !town.IsPlayerMatch() && (town.HasBooking() || lineup.Count == 1);
        v.Division = town.GetDivision(h);
        v.DivisionsEnabled = !town.IsLineupLocked();
        v.Progress = town.GetProgressCaption(h);
        TownArenaLogic<TId>.Portraits portraits = town.GetChallengePortraits(h);
        RefreshSkarn(town, h, v, portraits.Opponents[5].Count == 0 ? h.None : portraits.Opponents[5][0]);
        v.SelectionPrompt = lineup.Count == 0;
        var cards = new List<TownArenaCardView<TId>>();
        if (v.SelectionPrompt)
        {
            cards.Add(SelectionPrompt());
            for (int i = 1; i < 5; ++i) cards.Add(Hidden(i));
        }
        else
            for (int i = 0; i < 5; ++i)
            {
                bool cardVisible = town.IsChallengeVisible(h, i);
                cards.Add(RefreshCard(town, h, u, i, cardVisible, portraits.Opponents[i], portraits.Names[i]));
            }
        if (lastStatus != town.GetStatus()) { lastStatus = town.GetStatus(); feedback = lastStatus; }
        v.Squad = "Selected team: " + lineup.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " / 3" +
            (town.IsLineupLocked() ? " - locked" : "");
        v.Status = town.GetStatus();
        view = Finish(v, cards, rows);
    }

    // C++: TownArenaUI.cpp:417-452 (the captions; the widgets are the window half's)
    List<TownArenaRowView<TId>> RefreshRoster(TownArenaLogic<TId> town, ITownHost<TId> h, ITownArenaUIHost<TId> u)
    {
        IReadOnlyList<TId> current = u.PlayerSquad();
        var allIds = new List<string>();
        for (int i = 0; i < current.Count; ++i) allIds.Add(TownMatchmakingRuntimeLogic.SelectionIdentity(h, current[i]));
        // A reservation keeps its original identity list even if a member disappears;
        // booking/ingress own cancellation. UI resolution never substitutes someone.
        if (!town.IsLineupLocked()) TownRosterSelection.Prune(lineup, allIds);
        var rows = new List<TownArenaRowView<TId>>();
        for (int i = 0; i < current.Count; ++i)
        {
            string id = allIds[i];
            if (id.Length == 0) continue;
            bool ready = town.GetPlayerReadiness(h, current[i], out string reason);
            bool assigned = Assigned(id);
            string caption = h.Name(current[i]);
            string inlineStatus = RosterStatusPolicy.InlineStatus(ready, reason);
            if (inlineStatus.Length != 0) caption += " - " + inlineStatus;
            if (assigned) caption += "\nSelected";
            else if (!ready && inlineStatus.Length == 0) caption += "\n" + reason;
            if (assigned && !ready && inlineStatus.Length == 0) caption += " - " + reason;
            bool statusVisible = u.Recovery(current[i], out float recovery, out RosterStatusPolicy.Band band);
            rows.Add(new TownArenaRowView<TId>(id, current[i], caption, assigned, statusVisible, recovery, band));
        }
        return rows;
    }

    // C++: TownArenaUI.cpp:342-368 (the texts and colours: true = the C++'s green, false its red)
    static void RefreshSkarn(TownArenaLogic<TId> town, ITownHost<TId> h, TownArenaUiView<TId> v, TId skarn)
    {
        bool unlocked = town.IsSkarnUnlocked(h);
        v.Skarn = skarn;
        v.SkarnKey = TownMatchmakingRuntimeLogic.Identity(h, skarn);
        v.SkarnUnlocked = unlocked;
        v.SkarnPlaceholder = unlocked ? "SKARN\nPORTRAIT UNAVAILABLE" : "SKARN\nLOCKED";
        v.SkarnState = unlocked ? "UNLOCKED" : "LOCKED";
        uint wins = town.GetUniqueWins(h);
        uint[] bits = [(uint)TownChallengePolicy.UniqueVictory.RessaVane, (uint)TownChallengePolicy.UniqueVictory.BumPer,
            (uint)TownChallengePolicy.UniqueVictory.Senn, (uint)TownChallengePolicy.UniqueVictory.Torka, (uint)TownChallengePolicy.UniqueVictory.Veyr];
        string[] names = ["Ressa Vane", "Bum-Per", "Senn", "Torka", "Veyr the Gilded"];
        v.SkarnTip[0] = unlocked ? "Skarn unlocked" : "Skarn locked"; v.SkarnTipGood[0] = unlocked;
        for (int i = 0; i < 5; ++i)
        {
            bool won = (wins & bits[i]) != 0;
            v.SkarnTip[i + 1] = (won ? "Completed - " : "Incomplete - ") + names[i];
            v.SkarnTipGood[i + 1] = won;
        }
        v.SkarnTip[6] = unlocked ? "Skarn may now join the fighter pool." : "Defeat every unique fighter to unlock Skarn.";
        v.SkarnTipGood[6] = unlocked;
    }

    // C++: TownArenaUI.cpp:562-577
    static TownArenaCardView<TId> SelectionPrompt() =>
        new(0, true, "Select a recovered fighter", "Choose 1-3 recovered squad fighters to reveal the current challenge cards.", true,
            false, "", false, false, -1, false, 0, [], new TownChallengeBuyInPolicy.Quote());

    static TownArenaCardView<TId> Hidden(int slot) =>
        new(slot, false, "", "", false, false, "", false, false, -1, false, 0, [], new TownChallengeBuyInPolicy.Quote());

    // C++: TownArenaUI.cpp:494-561
    TownArenaCardView<TId> RefreshCard(TownArenaLogic<TId> town, ITownHost<TId> h, ITownArenaUIHost<TId> u, int slot, bool cardVisible,
        List<TId> opponents, List<string> names)
    {
        // Split only the existing display caption; offer identity and opponent
        // resolution come from the booking owner, never from display names.
        string caption = town.GetChallengeCaption(h, slot, false);
        int newline = caption.IndexOf('\n');
        string heading = newline < 0 ? caption : caption[..newline];
        string detail = newline < 0 ? "" : caption[(newline + 1)..];
        TownChallengeBuyInPolicy.Quote quote = town.GetChallengeBuyIn(h, slot);
        string unavailableReason = town.GetChallengeUnavailableReason(h, slot);
        bool unavailable = unavailableReason.Length != 0;
        bool searching = town.IsChallengeSearching(slot);
        string explanation = unavailableReason;
        if (searching) explanation = "Looking for a new fighter...\n" + explanation;
        else if (town.IsBestAvailableChallenge(slot))
            explanation = "Best available challenge: no eligible lineup reaches the normal rarity band.";
        if (town.IsPlayerMatch() && town.IsAftercare())
            explanation += (explanation.Length == 0 ? "" : "\n") + "Aftercare underway. You can change your team; booking resumes when care finishes.";
        if (explanation.Length == 0 && !quote.Valid()) explanation = detail;
        int portraitCount = Math.Max(1, Math.Min(4, opponents.Count));
        var portraits = new List<TownArenaPortraitView<TId>>();
        for (int member = 0; member < 4; ++member)
        {
            TId c = member < opponents.Count ? opponents[member] : h.None;
            string key = TownMatchmakingRuntimeLogic.Identity(h, c);
            ArenaCombatProfile.Profile profile = h.IsNull(c) ? new ArenaCombatProfile.Profile() : u.Profile(c);
            string tip = !h.IsNull(c) && h.Valid(c) ? h.Name(c) + "\n" + (profile.Style ?? "") +
                "\n\n" + ArenaCombatProfile.StatText(profile) : "";
            bool portraitVisible = member < portraitCount && key.Length != 0;
            string stats;
            if (profile.Valid && profile.Martial)
                stats = "ATK " + F0(profile.Attack) + " | DOD " + F0(profile.Defence) + " | STR " + F0(profile.Strength) +
                    "\nTOU " + F0(profile.Toughness) + " | DEX " + F0(profile.Dexterity);
            else if (profile.Valid)
                stats = "ATK " + F0(profile.Attack) + " | DEF " + F0(profile.Defence) + " | STR " + F0(profile.Strength) +
                    "\nTOU " + F0(profile.Toughness) + " | DEX " + F0(profile.Dexterity) + " | WPN " + F0(profile.WeaponSkill);
            else stats = "Combat stats unavailable";
            portraits.Add(new TownArenaPortraitView<TId>(c, key, portraitVisible, member < portraitCount && !portraitVisible,
                searching ? "Searching..." : "Portrait unavailable", unavailable ? .32f : 1f,
                member < opponents.Count, member < names.Count ? names[member] : "Unavailable", member < opponents.Count, stats, tip));
        }
        string terms = quote.Valid()
            ? "Buy-in: " + I(quote.BuyIn) + " Cats\nOdds: " + CMath.FormatF(quote.Payout / (double)quote.BuyIn, 2) + "x\nWin returns: " + I(quote.Payout) + " Cats"
            : "Buy-in / odds unavailable";
        int division = town.GetDivision(h);
        int rarity = town.GetChallengeRarity(h, slot);
        double config = PGConfig.ChallengeValues().WinMarksMultiplier;
        // "\nMarks: %.2fx (division %.2fx / rarity %.2fx / config %.2fx)" (the C++ promotes the float products to double for %f)
        string marks = "\nMarks: " + CMath.FormatF(ArenaMarks.ChallengeMultiplier(division, rarity) * config, 2) +
            "x (division " + CMath.FormatF(ArenaMarks.DivisionMultiplier(division), 2) + "x / rarity " + CMath.FormatF(ArenaMarks.RarityMultiplier(rarity), 2) +
            "x / config " + CMath.FormatF(config, 2) + "x)";
        bool bookEnabled = !town.IsLineupLocked() && !town.IsPlayerMatch() && quote.Valid();
        return new TownArenaCardView<TId>(slot, cardVisible, heading, explanation, explanation.Length != 0, true, terms + marks, true, bookEnabled,
            rarity, town.IsUniqueLegendaryChallenge(h, slot), portraitCount, portraits, quote, OfferKey(town, h, slot));
    }

    static string I(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    static string F0(double v) => CMath.FormatF(v, 0);

    TownArenaUiView<TId> Finish(TownArenaUiView<TId> v, List<TownArenaCardView<TId>> cards, List<TownArenaRowView<TId>> rows)
    {
        v.Rows = rows; v.Cards = cards; v.EmptyRoster = rows.Count == 0;
        return Seal(v);
    }

    /// <summary>The handlers change the feedback, the visibility and the lineup between refreshes: the view is sealed again over its rows and cards.</summary>
    void Rebuild(List<TownArenaCardView<TId>> cards, List<TownArenaRowView<TId>> rows)
    {
        var v = view;
        var copy = new TownArenaUiView<TId>
        {
            Status = v.Status, Squad = v.Squad, Direct = v.Direct, NextRefresh = v.NextRefresh, RefreshButton = v.RefreshButton, BookingButton = v.BookingButton,
            Progress = v.Progress, EmptyRoster = v.EmptyRoster, RefreshEnabled = v.RefreshEnabled, BookingEnabled = v.BookingEnabled,
            DivisionsEnabled = v.DivisionsEnabled, SelectionPrompt = v.SelectionPrompt, SkarnUnlocked = v.SkarnUnlocked, Division = v.Division,
            RefreshCost = v.RefreshCost,
            SkarnState = v.SkarnState, SkarnPlaceholder = v.SkarnPlaceholder, Skarn = v.Skarn, SkarnKey = v.SkarnKey,
            SkarnTip = v.SkarnTip, SkarnTipGood = v.SkarnTipGood, Rows = rows, Cards = cards,
        };
        view = Seal(copy);
    }

    TownArenaUiView<TId> Seal(TownArenaUiView<TId> v)
    {
        v.Open = visible; v.RequestSerial = requestSerial; v.LastSeq = lastSeq; v.Feedback = feedback;
        var k = new StringBuilder();
        k.Append(v.Open).Append('|').Append(v.RequestSerial).Append('|').Append(v.LastSeq).Append('|').Append(v.Status).Append('|').Append(v.Feedback)
         .Append('|').Append(v.Squad).Append('|').Append(v.Direct).Append('|').Append(v.NextRefresh).Append('|').Append(v.RefreshButton).Append('|')
         .Append(v.BookingButton).Append('|').Append(v.Progress).Append('|').Append(v.EmptyRoster).Append(v.RefreshEnabled).Append(v.BookingEnabled)
         .Append(v.DivisionsEnabled).Append(v.SelectionPrompt).Append(v.SkarnUnlocked).Append(v.Division).Append('|').Append(v.SkarnKey).Append('|')
         .Append(v.Skarn).Append('|').AppendJoin("/", v.SkarnTip).Append('|');
        foreach (var r in v.Rows) k.Append(r).Append('\u0001');
        foreach (var c in v.Cards)
        {
            k.Append(c with { Portraits = [] }).Append('\u0002');
            foreach (var p in c.Portraits) k.Append(p).Append('\u0003');
        }
        v.Key = k.ToString();
        return v;
    }
}

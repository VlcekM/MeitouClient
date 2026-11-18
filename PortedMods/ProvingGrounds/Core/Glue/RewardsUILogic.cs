using System.Globalization;

namespace ProvingGrounds.Core.Glue;

// Port of the rules half of upstream/ProvingGrounds/plugin/src/RewardsUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 5.3a (PA §2.17;
// CAP 7.0-7.4, 10). Everything RewardsUI.cpp decides, holds as state or writes as text, without the game, so it is tested over a fake host
// (Tests/Glue/RewardsUILogicTests.cs): which fighters are listed and in what order (RebuildRows :1230-1311), the preferred / selected fighter
// (Show :1592-1617, RebuildRows :1303-1310, OnFighterClicked :463-487), IsLiveSquadSelection (:449-459), the catalogue tab (OnCatalogTabClicked
// :507-521), what each card shows (UpdateCatalogVisibility :1313-1387, UpdateDetails :1389-1556), the status line (Show :1605-1614; the purchase
// handlers :489-561 write the purchase's status, which the window takes from PgSnapshot.LastPurchaseOf(sender): RewardsPurchaseGate below), Close /
// IsVisible / AbandonWorldState (:1619-1641), the window's recovery from a failure (RefreshDetails :1558-1567, RefreshAll :1569-1588 catch blocks
// -> AbandonWindow :569-588), the accent colours (LayoutRewardAccent :264-299) and the scroll offsets (RewardsScrollModel below). The window
// itself (EnsureWindow :1094-1228, LayoutWindow :986-1065, LayoutFighters :590-623, LayoutRewardCard :625-681, LayoutGeneralCard :683-737,
// LayoutDetails :739-904, LayoutProfile :906-943, FitWindowToScreen :945-956, the icons :301-438, the resize in Tick :1620-1633) is task 5.3b's
// window half. The sim half (src/Game/RewardsUISim.cs) owns this object and implements the host over the world.
//
// The purchases themselves are not here: a purchase button sends a RewardPurchase command (task 5.2b) that src/Game/RewardDelivery.cs runs; the
// sim half then refreshes this object (AfterPurchase = the C++ RefreshDetails after each purchase). The C++ handlers' guard
// "if (!g_selected || !g_selected->isValid()) return;" is the window's: it sends a purchase only for a valid selection (the buttons of an invalid
// selection are disabled, UpdateDetails :1391-1416).

/// <summary>What the rules half of RewardsUI reads (the sim half implements it over the world: squad, Marks, unlocks, RewardDelivery).</summary>
public interface IRewardsUIHost<TId>
{
    /// <summary><c>SquadUtil::CollectPlayerSquad(squad)</c> in its order (RebuildRows :1235).</summary>
    IReadOnlyList<TId> PlayerSquad();
    /// <summary><c>c &amp;&amp; c-&gt;isValid()</c>.</summary>
    bool Valid(TId c);
    /// <summary><c>c-&gt;isDead()</c>.</summary>
    bool IsDead(TId c);
    /// <summary><c>c-&gt;getName()</c>.</summary>
    string Name(TId c);
    /// <summary><c>LeaderboardStore::GetMarks(c)</c>.</summary>
    int GetMarks(TId c);
    /// <summary><c>LeaderboardStore::HasFactionUnlock(id)</c>.</summary>
    bool HasFactionUnlock(string id);
    /// <summary><c>RewardDelivery::ResolveOffer(c, piece)</c>.</summary>
    ArenaRewards.Offer ResolveOffer(TId c, ArenaRewards.Piece piece);
    /// <summary><c>PGLog::Error</c>.</summary>
    void Error(string line);
    /// <summary>BindRewardIcon :366-374: <c>RewardDelivery::ResolveRewardItemData(piece)</c>'s record (the cloak's is record 89, spike S13), or
    /// null when there is no item data (the icon is hidden).</summary>
    string? RewardIconRecord(ArenaRewards.Piece piece);
    /// <summary>BindGeneralIcon :410-416: <c>ou->gamedata.getData(stringId)</c> is not null.</summary>
    bool RecordExists(string recordId);
}

/// <summary>What a card's icon shows (BindRewardIcon :361-403 / BindGeneralIcon :405-438): the preview item the window half makes for its texture
/// (CreateRewardItem(record, quality, model, maker), task 5.3b) and never gives. <see cref="Record"/> null = no item data: the icon is hidden. The
/// texture itself (InventoryIcon::createIconImage) is the window half's; an empty texture also hides it there.</summary>
public readonly record struct RewardsIconView(string? Record, int Quality, string? Model, string? Maker);

/// <summary>C++ RewardsUI.cpp:83-88 <c>CatalogTab</c>.</summary>
public enum RewardsCatalogTab { General = 0, Armour = 1, Weapons = 2 }

/// <summary>One fighter row (RebuildRows :1244-1301; its Marks caption and selection from UpdateDetails :1543-1554).</summary>
public readonly record struct RewardsFighterRowView<TId>(TId Character, string Name, string Marks, bool Selected, bool RailVisible, bool PortraitVisible)
{
    /// <summary>The fighter's index in <c>CollectPlayerSquad</c>'s list, dead and invalid members counted (RebuildRows :1244-1290 names the row's
    /// widgets <c>PG_RewardFighter_%d</c> with it; task 5.3b).</summary>
    public int SquadIndex { get; init; }
}

/// <summary>One armour / weapon card (C++ <c>RewardCard</c> :113-129). <see cref="Offer"/> is the offer the card shows (the window sends it with a
/// claim, task 5.2b review: the sim refuses a claim whose offer changed); <see cref="HasOffer"/> false = no offer shown (no fighter, weapons locked,
/// complete).</summary>
public readonly record struct RewardsPieceCardView(ArenaRewards.Piece Piece, bool Visible, string Name, string Progress, string ClaimCaption,
    bool ClaimEnabled, int Rarity, bool HasOffer, ArenaRewards.Offer Offer, RewardsIconView Icon);

/// <summary>One General card (C++ <c>GeneralCard</c> :91-102).</summary>
public readonly record struct RewardsGeneralCardView(ArenaRewards.GeneralItem Item, bool Visible, string Name, string Details, string BuyCaption,
    bool BuyEnabled, RewardsIconView Icon);

/// <summary>One licence card (C++ <c>LicenceCard</c> :104-111): the four armour rows (<see cref="Row"/>) or the weapons licence
/// (<see cref="Row"/> = <see cref="ArenaRewards.ArmourRow.Count"/>, <see cref="Set"/> = None). <see cref="Manifest"/>: the armour pieces whose icons a
/// locked row shows under its button (LayoutDetails :816-829; each piece's icon is its card's <see cref="RewardsPieceCardView.Icon"/>, shown when it
/// has a record); empty for the weapons licence.</summary>
public readonly record struct RewardsLicenceView(ArenaRewards.ArmourRow Row, ArenaRewards.ArmourSet Set, bool Visible, bool Unlocked, string Heading,
    string Details, string BuyCaption, bool BuyVisible, bool BuyEnabled, ValueList<ArenaRewards.Piece> Manifest);

/// <summary>Texts of the Rewards window the C++ writes at creation or in its handlers (byte for byte).</summary>
public static class RewardsUiTexts
{
    /// <summary>C++ :1105 the window's caption (the reference shots show it upper-cased by the skin).</summary>
    public const string Caption = "Proving Grounds - Arena Rewards";
    /// <summary>C++ :1115 the hidden title, :1117 the fighters heading.</summary>
    public const string Title = "ARENA REWARDS", FightersHeading = "FIGHTERS";
    /// <summary>C++ :1122, :1395 the subtitle (two spaces either side of the bar).</summary>
    public const string Subtitle = "NULL'S CRUCIBLE CATALOGUE  |  Victory buys privilege";
    /// <summary>C++ :1125 the status line's creation caption (Show always overwrites it).</summary>
    public const string StatusInitial = "Earn Marks in rated arena matches.";
    /// <summary>C++ :1129 the career line's creation caption, :1397 with no eligible fighter.</summary>
    public const string CareerInitial = "No rewards available", CareerNone = "NO ELIGIBLE FIGHTER  |  0 ARENA MARKS";
    /// <summary>C++ :1134-1136 the tab captions.</summary>
    public const string TabGeneral = "GENERAL", TabArmour = "ARMOUR", TabWeapons = "WEAPONS";
    /// <summary>C++ :1170, :1210-1213 the button captions at creation.</summary>
    public const string Claim = "CLAIM", Buy = "BUY", UnlockLicence = "UNLOCK LICENCE";
    /// <summary>C++ :1217 the close button.</summary>
    public const string Close = "Close catalogue";
    /// <summary>C++ :1189-1194 the General tab's two headings.</summary>
    public const string BlueprintsHeading = "|  ARENA BLUEPRINTS", SuppliesHeading = "|  FIGHT SUPPLIES";
    /// <summary>C++ :1204 the weapons licence heading.</summary>
    public const string WeaponLicenceHeading = "|  WEAPONS LICENCE";
    /// <summary>C++ :1400 a card's progress with no eligible fighter.</summary>
    public const string SelectFighter = "Select a fighter to review requirements.";
    /// <summary>C++ :1612 the status line with no eligible fighter.</summary>
    public const string NoFighters = "No eligible fighters available.";
    /// <summary>C++ :1424, :1452, :1458, :1461, :1500, :1508, :1514, :1527 the captions UpdateDetails writes.</summary>
    public const string BuyAgain = "BUY AGAIN", PrerequisiteRequired = "PREREQUISITE REQUIRED", WeaponsLicenceRequired = "WEAPONS LICENCE REQUIRED",
        Locked = "LOCKED", Complete = "COMPLETE", Replace = "REPLACE", Upgrade = "UPGRADE";
    /// <summary>C++ :1442-1444 / :1471 the "  |  INSUFFICIENT MARKS" suffix.</summary>
    public const string Insufficient = "  |  INSUFFICIENT MARKS";

    /// <summary>C++: RewardsUI.cpp:221-231 ArmourRowName.</summary>
    public static string ArmourRowName(ArenaRewards.ArmourRow row) => row switch
    {
        ArenaRewards.ArmourRow.Pitfighter => "PITFIGHTER",
        ArenaRewards.ArmourRow.Retainer => "RETAINER LEATHER",
        ArenaRewards.ArmourRow.Enforcer => "PLATE ENFORCER",
        ArenaRewards.ArmourRow.Warlord => "WARLORD",
        _ => "ARMOUR",
    };

    /// <summary>C++: RewardsUI.cpp:233-248 ArmourRowManifest.</summary>
    public static string ArmourRowManifest(ArenaRewards.ArmourRow row) => row switch
    {
        ArenaRewards.ArmourRow.Pitfighter => "Footwraps / Pants / Harness / Jaw Guard",
        ArenaRewards.ArmourRow.Retainer => "Boots / Pants / Jacket / Helmet",
        ArenaRewards.ArmourRow.Enforcer => "Plated Boots / Legplates / Chest / Helmet",
        ArenaRewards.ArmourRow.Warlord => "Plate Boots / Plateskirt / Armour / Cloak / Helmet",
        _ => "",
    };
}

/// <summary>The colours and alphas the C++ paints (MyGUI::Colour r, g, b in 0..1). Grade = the card's rarity index (RewardsLayout.RarityIndex).</summary>
public static class RewardsUiColours
{
    /// <summary>C++ :277-282 the accent colour per grade: blue, green, purple, orange.</summary>
    public static readonly IReadOnlyList<(float R, float G, float B)> Grades =
        [(.22f, .52f, .95f), (.22f, .76f, .36f), (.65f, .34f, .92f), (1f, .49f, .12f)];

    /// <summary>C++ :283 <c>colours[Min(3, Max(0, card.rarity))]</c>.</summary>
    public static (float R, float G, float B) Grade(int rarity) => Grades[RewardsLayout.Min(3, RewardsLayout.Max(0, rarity))];

    /// <summary>C++ :1289 the fighter row's rail (the selected row's marker).</summary>
    public static readonly (float R, float G, float B) Rail = (0.58f, 0.25f, 0.12f);

    /// <summary>C++ :240 <c>.12f * (16 - i) / 16.f</c>: the alpha of fade strip <paramref name="i"/> (0..15), float arithmetic as the C++.</summary>
    public static float FadeAlpha(int i) => .12f * (16 - i) / 16f;

    /// <summary>C++ :250 the border alphas: the left rail .9, the other three .35.</summary>
    public static float BorderAlpha(int i) => i == 0 ? .9f : .35f;
}

/// <summary>The published view of the Rewards window (task 5.3a; the window half, 5.3b, binds it). Value equality: the sim half publishes a new
/// model only when it changed.</summary>
public sealed record RewardsUiView<TId>
{
    // RequestSerial / RequestOpen are also on the published PgUiModel (the window framework reads them there); the view keeps its own copy so
    // value equality sees an open / close request as a change (the sim half copies them into the model, as the other windows do).
    public int RequestSerial { get; init; }
    public bool RequestOpen { get; init; }
    /// <summary>Counts Shows: the window puts the fighter and profile canvases at the top and the detail canvas at its tab's offset (:1600-1603).</summary>
    public int Shows { get; init; }
    /// <summary>Counts the fighter clicks that changed the selection: the detail canvas goes to the top (:478-481).</summary>
    public int DetailResets { get; init; }
    public string Caption { get; init; } = RewardsUiTexts.Caption;
    public string Title { get; init; } = RewardsUiTexts.Title;
    public string Subtitle { get; init; } = RewardsUiTexts.Subtitle;
    public string FightersHeading { get; init; } = RewardsUiTexts.FightersHeading;
    public string Career { get; init; } = RewardsUiTexts.CareerInitial;
    /// <summary>The status line as the sim half set it (Show); the window shows a purchase's status over it (RewardsPurchaseGate).</summary>
    public string Status { get; init; } = RewardsUiTexts.StatusInitial;
    public string CloseCaption { get; init; } = RewardsUiTexts.Close;
    public RewardsCatalogTab Tab { get; init; }
    public bool TabsVisible { get; init; }
    public bool HeadingsVisible { get; init; }
    public bool HasSelected { get; init; }
    public TId Selected { get; init; } = default!;
    public ValueList<RewardsFighterRowView<TId>> Rows { get; init; } = ValueList<RewardsFighterRowView<TId>>.Empty;
    public ValueList<RewardsPieceCardView> Cards { get; init; } = ValueList<RewardsPieceCardView>.Empty;
    public ValueList<RewardsGeneralCardView> General { get; init; } = ValueList<RewardsGeneralCardView>.Empty;
    public ValueList<RewardsLicenceView> Licences { get; init; } = ValueList<RewardsLicenceView>.Empty;
    public RewardsLicenceView WeaponLicence { get; init; }
}

/// <summary>The rules half of RewardsUI.cpp. Sim thread (one object per world). Widget captions the C++ writes only in some branches keep their
/// last value, as the widgets do (the no-fighter branch of UpdateDetails leaves the General / licence texts as they were).</summary>
public sealed class RewardsUILogic<TId>
{
    static readonly EqualityComparer<TId> Eq = EqualityComparer<TId>.Default;

    /// <summary>C++ :149-157 kSupplyDisplayOrder (UI order only).</summary>
    public static readonly IReadOnlyList<ArenaRewards.GeneralItem> SupplyDisplayOrder =
    [
        ArenaRewards.GeneralItem.AdvancedFirstAid, ArenaRewards.GeneralItem.PitSurgeonRoll, ArenaRewards.GeneralItem.SkeletonRepair,
        ArenaRewards.GeneralItem.IronmenderCase, ArenaRewards.GeneralItem.SplintKit, ArenaRewards.GeneralItem.BoneSetterBraces,
    ];

    /// <summary>C++ :159-179 kArmourDisplayOrder (UI order only: Piece values are persisted progression keys).</summary>
    public static readonly IReadOnlyList<ArenaRewards.Piece> ArmourDisplayOrder =
    [
        ArenaRewards.Piece.PitfighterFootwraps, ArenaRewards.Piece.PitfighterPants, ArenaRewards.Piece.PitfighterHarness, ArenaRewards.Piece.PitfighterJawGuard,
        ArenaRewards.Piece.RetainerBoots, ArenaRewards.Piece.RetainerPants, ArenaRewards.Piece.RetainerJacket, ArenaRewards.Piece.RetainerHelmet,
        ArenaRewards.Piece.EnforcerBoots, ArenaRewards.Piece.EnforcerLegplates, ArenaRewards.Piece.EnforcerChest, ArenaRewards.Piece.EnforcerHelmet,
        ArenaRewards.Piece.PlateBoots, ArenaRewards.Piece.PlateSkirt, ArenaRewards.Piece.PlateArmour, ArenaRewards.Piece.Cloak, ArenaRewards.Piece.Helmet,
    ];

    /// <summary>C++ LayoutDetails :816-829: the pieces whose icons an armour row's locked licence shows (its manifest), in display order.</summary>
    public static IEnumerable<ArenaRewards.Piece> ManifestPieces(ArenaRewards.ArmourRow row) =>
        ArmourDisplayOrder.Where(p => !ArenaRewards.IsWeapon(p) && ArenaRewards.RowFor(p) == row);

    // ---- widget state (the C++ captions and flags of the created window) ----
    sealed class Card
    {
        public ArenaRewards.Piece Piece;
        public bool Visible, ClaimEnabled = true, HasOffer;
        public string Name = "", Progress = "", ClaimCaption = RewardsUiTexts.Claim;
        public int Rarity;
        public ArenaRewards.Offer Offer;
        public RewardsIconView Icon;
        public bool IconLoaded;   // C++ card.iconLoaded
    }
    sealed class General
    {
        public ArenaRewards.GeneralItem Item;
        public bool Visible, BuyEnabled = true;
        public string Name = "", Details = "", BuyCaption = RewardsUiTexts.Buy;
        public RewardsIconView Icon;
        public bool IconLoaded;
    }
    sealed class Licence
    {
        public ArenaRewards.ArmourRow Row;
        public ArenaRewards.ArmourSet Set;
        public bool Visible, Unlocked, BuyVisible = true, BuyEnabled = true;
        public string Heading = "", Details = "", BuyCaption = RewardsUiTexts.UnlockLicence;
    }
    sealed class Row
    {
        public TId Character = default!;
        public string Name = "", Marks = "";
        public bool Selected, Rail, Portrait;
        public int SquadIndex;
    }

    readonly Card[] cards = new Card[(int)ArenaRewards.Piece.Count];
    readonly General[] general = new General[(int)ArenaRewards.GeneralItem.Count];
    readonly Licence[] licences = new Licence[(int)ArenaRewards.ArmourRow.Count];
    Licence weaponLicence = null!;
    readonly List<Row> rows = [];

    bool visible, tabsVisible, headingsVisible;
    bool alive;   // C++ g_window != NULL: made by Show (EnsureWindow), gone after AbandonWindow
    int requestSerial, shows, detailResets;
    string title = RewardsUiTexts.Title, subtitle = RewardsUiTexts.Subtitle, fightersHeading = RewardsUiTexts.FightersHeading;
    string career = RewardsUiTexts.CareerInitial, status = RewardsUiTexts.StatusInitial;
    RewardsCatalogTab tab;
    bool hasSelected, hasPreferred;   // C++ g_selected / g_preferred != NULL
    TId selected = default!, preferred = default!;

    /// <param name="tab">The catalogue tab to start from (C++ g_catalogTab :89 is process-wide: the sim half carries it from world to world).</param>
    public RewardsUILogic(RewardsCatalogTab tab = RewardsCatalogTab.General)
    {
        this.tab = tab;
        CreateWidgets();
    }

    /// <summary>C++: RewardsUI.cpp:1619 IsVisible.</summary>
    public bool IsVisible => visible;
    /// <summary>C++ g_catalogTab.</summary>
    public RewardsCatalogTab Tab => tab;
    /// <summary>C++ g_selected (none: <see cref="HasSelected"/> false).</summary>
    public TId Selected => selected;
    public bool HasSelected => hasSelected;

    /// <summary>C++ EnsureWindow :1094-1228: the widgets' creation captions (and AbandonWindow :569-588's reset to them: the next Show creates the
    /// window again).</summary>
    void CreateWidgets()
    {
        for (int i = 0; i < cards.Length; ++i)
        {
            var piece = (ArenaRewards.Piece)i;
            var b = ArenaRewards.Get(piece, ArenaRewards.FirstTier(piece));
            cards[i] = new Card
            {
                Piece = piece, Name = b.PieceName, Progress = b.TierName,
                Rarity = RewardsLayout.RarityIndex((int)ArenaRewards.FirstTier(piece), false),
            };
        }
        for (int i = 0; i < general.Length; ++i)
        {
            var item = (ArenaRewards.GeneralItem)i;
            general[i] = new General { Item = item, Name = ArenaRewards.GetGeneral(item).Name };
        }
        for (int i = 0; i < licences.Length; ++i)
        {
            var row = (ArenaRewards.ArmourRow)i;
            licences[i] = new Licence { Row = row, Set = ArenaRewards.SetForRow(row), Heading = "|  " + RewardsUiTexts.ArmourRowName(row) };
        }
        weaponLicence = new Licence { Row = ArenaRewards.ArmourRow.Count, Set = ArenaRewards.ArmourSet.None, Heading = RewardsUiTexts.WeaponLicenceHeading };
        title = RewardsUiTexts.Title;
        subtitle = RewardsUiTexts.Subtitle;
        fightersHeading = RewardsUiTexts.FightersHeading;
        career = RewardsUiTexts.CareerInitial;
        status = RewardsUiTexts.StatusInitial;
        tabsVisible = headingsVisible = false;
    }

    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    bool Selected_(IRewardsUIHost<TId> h) => hasSelected && h.Valid(selected);   // C++ g_selected && g_selected->isValid()

    // C++: RewardsUI.cpp:449-459
    bool IsLiveSquadSelection(IRewardsUIHost<TId> h, bool has, TId character)
    {
        if (!has || !h.Valid(character))
            return false;
        foreach (var r in rows)
            if (Eq.Equals(r.Character, character))
                return true;
        return false;
    }

    /// <summary>C++: RewardsUI.cpp:1230-1311 RebuildRows (the rows' widgets are the window half's).</summary>
    void RebuildRows(IRewardsUIHost<TId> h)
    {
        rows.Clear();   // ClearRows :440-447
        var squad = h.PlayerSquad();
        for (int i = 0; i < squad.Count; ++i)
        {
            var character = squad[i];
            if (!h.Valid(character) || h.IsDead(character))
                continue;
            rows.Add(new Row
            {
                Character = character, Name = h.Name(character), Marks = I(h.GetMarks(character)) + " Marks", SquadIndex = i,
                Portrait = true,   // BindPortrait :347-359: valid here; the window half sets the image
            });
        }
        if (!IsLiveSquadSelection(h, hasSelected, selected))
        {
            hasSelected = false;
            selected = default!;
            if (hasPreferred && IsLiveSquadSelection(h, hasPreferred, preferred))
            {
                hasSelected = true;
                selected = preferred;
            }
            else if (rows.Count > 0)
            {
                hasSelected = true;
                selected = rows[0].Character;
            }
        }
    }

    // C++: RewardsUI.cpp:1313-1387
    void UpdateCatalogVisibility(IRewardsUIHost<TId> h, bool show)
    {
        tabsVisible = show;   // setStateSelected(g_catalogTab == i): the view's Tab
        for (int i = 0; i < cards.Length; ++i)
        {
            var c = cards[i];
            bool weapon = ArenaRewards.IsWeapon(c.Piece);
            bool inTab = weapon ? tab == RewardsCatalogTab.Weapons
                : tab == RewardsCatalogTab.Armour && h.HasFactionUnlock(ArenaRewards.GetLicence(ArenaRewards.SetFor(c.Piece)).StableId);
            c.Visible = show && inTab;
        }
        bool arenaOne = h.HasFactionUnlock("blueprint.arena1");
        headingsVisible = show && tab == RewardsCatalogTab.General;
        foreach (var g in general)
            g.Visible = show && tab == RewardsCatalogTab.General && ArenaRewards.IsGeneralVisible(g.Item, arenaOne);
        foreach (var l in licences)
        {
            bool s = show && tab == RewardsCatalogTab.Armour;
            l.Unlocked = h.HasFactionUnlock(ArenaRewards.GetLicence(l.Set).StableId);
            l.Visible = s;
            l.BuyVisible = s && !l.Unlocked;
        }
        bool showWeapon = show && tab == RewardsCatalogTab.Weapons;
        weaponLicence.Unlocked = h.HasFactionUnlock(ArenaRewards.GetWeaponLicence().StableId);
        weaponLicence.Visible = showWeapon;
        weaponLicence.BuyVisible = showWeapon && !weaponLicence.Unlocked;
    }

    // C++: RewardsUI.cpp:1389-1556
    void UpdateDetails(IRewardsUIHost<TId> h)
    {
        if (!Selected_(h))
        {
            title = RewardsUiTexts.Title;
            subtitle = RewardsUiTexts.Subtitle;
            fightersHeading = RewardsUiTexts.FightersHeading;
            career = RewardsUiTexts.CareerNone;
            UpdateCatalogVisibility(h, true);
            foreach (var c in cards)
            {
                c.ClaimEnabled = false;
                c.ClaimCaption = RewardsUiTexts.Claim;
                c.Progress = RewardsUiTexts.SelectFighter;
                c.Rarity = RewardsLayout.RarityIndex((int)ArenaRewards.FirstTier(c.Piece), false);
                c.HasOffer = false;
                c.Offer = default;
            }
            foreach (var r in rows)
            {
                r.Selected = false;
                r.Rail = false;
            }
            foreach (var g in general) g.BuyEnabled = false;
            foreach (var l in licences) l.BuyEnabled = false;
            weaponLicence.BuyEnabled = false;
            return;
        }

        int marks = h.GetMarks(selected);
        title = RewardsUiTexts.Title;
        fightersHeading = RewardsUiTexts.FightersHeading;
        career = I(marks) + " ARENA MARKS";

        UpdateCatalogVisibility(h, true);

        foreach (var g in general)
        {
            var reward = ArenaRewards.GetGeneral(g.Item);
            g.Details = (g.Item == ArenaRewards.GeneralItem.ArenaBuildingsI || g.Item == ArenaRewards.GeneralItem.ArenaBuildingsII
                    ? "PHYSICAL BLUEPRINT BOOK" : "REPEATABLE FIGHT SUPPLY")
                + "  |  " + I(reward.Cost) + " Marks" + (marks < reward.Cost ? RewardsUiTexts.Insufficient : "");
            g.BuyCaption = reward.GrantsUnlockId is { } grants && h.HasFactionUnlock(grants) ? RewardsUiTexts.BuyAgain : RewardsUiTexts.Buy;
            g.BuyEnabled = g.Visible && marks >= reward.Cost;
        }

        foreach (var l in licences)
        {
            var licence = ArenaRewards.GetLicence(l.Set);
            bool prerequisite = licence.PrerequisiteId is null || h.HasFactionUnlock(licence.PrerequisiteId);
            string manifest = RewardsUiTexts.ArmourRowManifest(l.Row);
            if (l.Unlocked)
                l.Details = "LICENCE ACTIVE  |  " + manifest;
            else if (!prerequisite)
                l.Details = "LOCKED  |  Previous armour licence required  |  " + manifest;
            else
                l.Details = "FACTION LICENCE  |  " + I(licence.Cost) + " Marks" + (marks < licence.Cost ? RewardsUiTexts.Insufficient : "") + "  |  " + manifest;
            l.BuyEnabled = ArenaRewards.CanPurchaseLicence(l.Set, l.Unlocked, prerequisite, marks);
            l.BuyCaption = prerequisite ? RewardsUiTexts.UnlockLicence : RewardsUiTexts.PrerequisiteRequired;
        }

        var weapon = ArenaRewards.GetWeaponLicence();
        weaponLicence.Details = weaponLicence.Unlocked
            ? "LICENCE ACTIVE  |  All weapon rewards available"
            : "FACTION LICENCE  |  " + I(weapon.Cost) + " Marks" + (marks < weapon.Cost ? RewardsUiTexts.Insufficient : "") + "  |  Unlocks all weapon rewards";
        weaponLicence.BuyEnabled = ArenaRewards.CanPurchaseWeaponLicence(weaponLicence.Unlocked, marks);
        weaponLicence.BuyCaption = RewardsUiTexts.UnlockLicence;

        foreach (var c in cards)
        {
            if (ArenaRewards.IsWeapon(c.Piece) && !weaponLicence.Unlocked)
            {
                c.Progress = RewardsUiTexts.WeaponsLicenceRequired;
                c.ClaimCaption = RewardsUiTexts.Locked;
                c.ClaimEnabled = false;
                c.Rarity = RewardsLayout.RarityIndex((int)ArenaRewards.FirstTier(c.Piece), false);
                c.HasOffer = false;
                c.Offer = default;
                continue;
            }
            var offer = h.ResolveOffer(selected, c.Piece);
            if (offer.Kind == ArenaRewards.OfferKind.Complete)
            {
                int lastTier = (int)ArenaRewards.FirstTier(c.Piece) + ArenaRewards.TiersFor(c.Piece) - 1;
                var top = ArenaRewards.Get(c.Piece, (ArenaRewards.Tier)lastTier);
                c.Progress = top.TierName + " CLAIMED";
                c.ClaimCaption = RewardsUiTexts.Complete;
                c.ClaimEnabled = false;
                c.Rarity = RewardsLayout.RarityIndex(lastTier, true);
                c.HasOffer = false;
                c.Offer = offer;
                continue;
            }
            var reward = ArenaRewards.Get(c.Piece, offer.Tier);
            string tail = I(offer.Cost) + " Marks" + (marks < offer.Cost ? RewardsUiTexts.Insufficient : "");
            if (offer.Kind == ArenaRewards.OfferKind.Replace)
            {
                c.Progress = "REPLACE: " + reward.TierName + "  |  " + tail;
                c.ClaimCaption = RewardsUiTexts.Replace;
            }
            else
            {
                c.Progress = "NEXT: " + reward.TierName + "  |  " + tail;
                c.ClaimCaption = offer.Tier == ArenaRewards.FirstTier(c.Piece) ? RewardsUiTexts.Claim : RewardsUiTexts.Upgrade;
            }
            c.Rarity = RewardsLayout.RarityIndex((int)offer.Tier, false);
            c.ClaimEnabled = marks >= offer.Cost;
            c.HasOffer = true;
            c.Offer = offer;
        }
        foreach (var r in rows)
        {
            r.Marks = I(h.GetMarks(r.Character)) + " Marks";
            r.Selected = Eq.Equals(r.Character, selected);
            r.Rail = r.Selected;
        }
    }

    /// <summary>C++ AbandonWindow :569-588 after a failure: the window is gone (not visible), its widgets are made again by the next Show.</summary>
    void AbandonWindow()
    {
        if (visible) requestSerial++;   // the window side closes it
        visible = false;
        alive = false;
        rows.Clear();
        CreateWidgets();
        hasSelected = hasPreferred = false;
        selected = preferred = default!;
    }

    /// <summary>C++: RewardsUI.cpp:1558-1567 RefreshDetails (its layout pass is the window half's).</summary>
    void RefreshDetails(IRewardsUIHost<TId> h)
    {
        try { UpdateDetails(h); }
        catch (Exception)
        {
            h.Error("Proving Grounds: native rewards content/layout failed");
            AbandonWindow();
        }
    }

    /// <summary>C++: RewardsUI.cpp:361-403 BindRewardIcon's record (once: a loaded icon is kept, iconLoaded :364-365); the texture is 5.3b's.</summary>
    void BindRewardIcon(IRewardsUIHost<TId> h, Card c)
    {
        if (c.IconLoaded) return;
        var reward = ArenaRewards.Get(c.Piece, ArenaRewards.FirstTier(c.Piece));
        string? record = h.RewardIconRecord(c.Piece);
        if (record is null) { c.Icon = default; return; }
        c.Icon = new RewardsIconView(record, reward.QualityLevel, reward.ModelStringId, reward.ManufacturerStringId);
        c.IconLoaded = true;
    }

    /// <summary>C++: RewardsUI.cpp:405-438 BindGeneralIcon's record (<c>CreateRewardItem(itemData, -1)</c>: no quality).</summary>
    void BindGeneralIcon(IRewardsUIHost<TId> h, General g)
    {
        if (g.IconLoaded) return;
        var reward = ArenaRewards.GetGeneral(g.Item);
        if (!h.RecordExists(reward.StringId)) { g.Icon = default; return; }
        g.Icon = new RewardsIconView(reward.StringId, -1, null, null);
        g.IconLoaded = true;
    }

    /// <summary>C++: RewardsUI.cpp:1569-1588 RefreshAll (the icons' records here; their textures are the window half's).</summary>
    bool RefreshAll(IRewardsUIHost<TId> h)
    {
        try
        {
            RebuildRows(h);
            foreach (var c in cards) BindRewardIcon(h, c);
            foreach (var g in general) BindGeneralIcon(h, g);
        }
        catch (Exception)
        {
            h.Error("Proving Grounds: native rewards content failed (Kenshi_Button1/Kenshi_PaintedWordWrapEmpty/Kenshi_TextboxPaintedText/ImageBox)");
            AbandonWindow();
            return false;
        }
        RefreshDetails(h);
        return true;
    }

    /// <summary>C++: RewardsUI.cpp:1592-1617 Show(preferred) (<paramref name="hasPreferred"/> false = NULL).</summary>
    public void Show(IRewardsUIHost<TId> h, bool hasPreferred, TId preferredFighter)
    {
        bool valid = hasPreferred && h.Valid(preferredFighter);
        this.hasPreferred = valid;
        preferred = valid ? preferredFighter : default!;
        hasSelected = valid;
        selected = preferred;
        alive = true;   // EnsureWindow
        shows++;
        if (!RefreshAll(h)) return;
        if (!alive) return;
        status = Selected_(h) ? "Purchases go to " + h.Name(selected) + "." : RewardsUiTexts.NoFighters;
        RefreshDetails(h);
        if (!alive) return;
        if (!visible) requestSerial++;
        visible = true;
    }

    /// <summary>C++: RewardsUI.cpp:1618 Close.</summary>
    public void Close()
    {
        if (!visible) return;
        visible = false;
        requestSerial++;
    }

    /// <summary>The window side reports the window closed (its close button's own close, Escape: ArenaUI.cpp:3014-3016, the title bar's
    /// OnWindowButtonPressed :563-567) under request <paramref name="serial"/>: as Close, without a new request. False for another request.</summary>
    public bool OnClosedFromWindow(int serial)
    {
        if (!visible || serial != requestSerial) return false;
        visible = false;
        return true;
    }

    /// <summary>C++: RewardsUI.cpp:1635-1641 AbandonWorldState.</summary>
    public void AbandonWorldState()
    {
        Close();
        rows.Clear();   // ClearRows
        hasSelected = hasPreferred = false;
        selected = preferred = default!;
    }

    /// <summary>C++: RewardsUI.cpp:463-487 OnFighterClicked on the row that shows <paramref name="fighter"/>. False: no row shows it (ignored).</summary>
    public bool OnFighterClicked(IRewardsUIHost<TId> h, TId fighter)
    {
        foreach (var r in rows)
        {
            if (!Eq.Equals(r.Character, fighter)) continue;
            if (!hasSelected || !Eq.Equals(selected, r.Character))
                detailResets++;   // g_detailScroll->setViewOffset(0, 0)
            hasSelected = true;
            selected = r.Character;
            RefreshDetails(h);
            return true;
        }
        return false;
    }

    /// <summary>C++: RewardsUI.cpp:507-521 OnCatalogTabClicked. False when <paramref name="next"/> is the tab already shown.</summary>
    public bool OnCatalogTabClicked(IRewardsUIHost<TId> h, RewardsCatalogTab next)
    {
        if (tab == next) return false;
        tab = next;   // the offsets are the window's: RewardsScrollModel.OnTabChanged
        RefreshDetails(h);
        return true;
    }

    /// <summary>The C++ <c>RefreshDetails()</c> each purchase handler runs after RewardDelivery answered (:502, :537, :550, :559), here after the
    /// RewardPurchase command ran. Only while the window is shown (the C++ handlers are reachable only through it).</summary>
    public void AfterPurchase(IRewardsUIHost<TId> h)
    {
        if (visible && alive) RefreshDetails(h);
    }

    /// <summary>The view now.</summary>
    public RewardsUiView<TId> BuildView() => new()
    {
        RequestSerial = requestSerial, RequestOpen = visible, Shows = shows, DetailResets = detailResets,
        Title = title, Subtitle = subtitle, FightersHeading = fightersHeading, Career = career, Status = status,
        Tab = tab, TabsVisible = tabsVisible, HeadingsVisible = headingsVisible, HasSelected = hasSelected, Selected = selected,
        Rows = new(rows.Select(r => new RewardsFighterRowView<TId>(r.Character, r.Name, r.Marks, r.Selected, r.Rail, r.Portrait) { SquadIndex = r.SquadIndex })),
        Cards = new(cards.Select(c => new RewardsPieceCardView(c.Piece, c.Visible, c.Name, c.Progress, c.ClaimCaption, c.ClaimEnabled, c.Rarity, c.HasOffer, c.Offer, c.Icon))),
        General = new(general.Select(g => new RewardsGeneralCardView(g.Item, g.Visible, g.Name, g.Details, g.BuyCaption, g.BuyEnabled, g.Icon))),
        Licences = new(licences.Select(L)),
        WeaponLicence = L(weaponLicence),
    };

    static readonly ValueList<ArenaRewards.Piece>[] Manifests = [.. Enumerable.Range(0, (int)ArenaRewards.ArmourRow.Count + 1)
        .Select(r => new ValueList<ArenaRewards.Piece>(r < (int)ArenaRewards.ArmourRow.Count ? ManifestPieces((ArenaRewards.ArmourRow)r) : []))];

    static RewardsLicenceView L(Licence l) => new(l.Row, l.Set, l.Visible, l.Unlocked, l.Heading, l.Details, l.BuyCaption, l.BuyVisible, l.BuyEnabled,
        Manifests[(int)l.Row]);
}

/// <summary>
/// One sender's purchase sequence (task 5.2b review note, task 5.3a brief note 0 and quality review): each purchase button sends a RewardPurchase
/// with the next number of this sender (its own <see cref="RewardPurchaseSender"/>, counting from 1); while the last one sent has no published
/// result of the same sender (PgSnapshot.LastPurchaseOf(sender).Seq) no other purchase is sent; the status line shows the purchase's status (C++
/// <c>g_status->setCaption(status)</c>, RewardsUI.cpp:500-501, :534, :549, :558) over the model's (Show's) until the next Show. A claim refused
/// because its offer changed (an empty status, OfferChanged) leaves the line as it was (port only: the C++ has no such refusal). A new world
/// (<see cref="FollowWorld"/>) starts over: its results start empty, so a purchase pending in the old world would wait forever. Window thread
/// (5.3b); the TEST-ONLY verb uses its own (sender TestGate) on the sim thread.
/// </summary>
public sealed class RewardsPurchaseGate
{
    long next = 1;          // this sender counts from 1
    long pending;           // the last number sent whose result is not seen yet (0: none)
    int pendingShows;       // the Show it was sent under
    int shows = -1;         // the model's Shows the status belongs to
    string? purchaseStatus; // the last result's status under that Show, or null: the model's line
    object? world;          // the world (system) the numbers belong to

    /// <summary>True while the purchase sent waits for its result (<paramref name="publishedSeq"/> = this sender's last published Seq, 0 for none).</summary>
    public bool Waiting(long publishedSeq) => pending != 0 && publishedSeq != pending;

    /// <summary>The number of a new purchase sent under the model's <paramref name="modelShows"/>, or 0 when one still waits (no purchase is sent).
    /// Reads only the wait: the status line is <see cref="Observe"/>'s, so a result not read yet is never shown as blank.</summary>
    public long Send(long publishedSeq, int modelShows)
    {
        if (Waiting(publishedSeq)) return 0;
        pending = next++;
        pendingShows = modelShows;
        return pending;
    }

    /// <summary>Reads this sender's published result (call before <see cref="Status"/>): the result of the purchase sent takes over the status line
    /// when it belongs to the current Show.</summary>
    public void Observe(long publishedSeq, bool hasResult, string resultStatus, bool offerChanged, int modelShows)
    {
        if (modelShows != shows)
        {
            shows = modelShows;
            purchaseStatus = null;   // Show wrote its own line
        }
        if (pending == 0 || !hasResult || publishedSeq != pending) return;
        bool current = pendingShows == modelShows;
        pending = 0;
        if (current && !offerChanged) purchaseStatus = resultStatus;
    }

    /// <summary>The status line to show over the model's <paramref name="modelStatus"/>.</summary>
    public string Status(string modelStatus) => purchaseStatus ?? modelStatus;

    /// <summary>Call with the current world (the system object) before each read: a different one resets the gate.</summary>
    public void FollowWorld(object? current)
    {
        if (ReferenceEquals(current, world)) return;
        world = current;
        Reset();
    }

    /// <summary>Forgets everything: no purchase waits, the next number is 1, the model's line shows.</summary>
    public void Reset()
    {
        next = 1;
        pending = 0;
        pendingShows = 0;
        shows = -1;
        purchaseStatus = null;
    }
}

/// <summary>
/// The window half's scroll offsets of the three canvases (task 5.3b binds them; pure so the C++ rules are tested here): C++ g_tabOffsets :90,
/// Show :1600-1603, OnFighterClicked :478-481, OnCatalogTabClicked :515-519, LayoutDetails' clamp :895-903, LayoutFighters / LayoutProfile's
/// clamp :621-622 / :941-942, OnWheel :188-203 and AbandonWindow :586. Offsets are positive distances from the top.
/// </summary>
public sealed class RewardsScrollModel
{
    readonly int[] tabOffsets = new int[3];
    public int Fighter { get; private set; }
    public int Profile { get; private set; }
    public int Detail { get; private set; }

    /// <summary>C++ g_tabOffsets[tab].</summary>
    public int TabOffset(RewardsCatalogTab tab) => tabOffsets[(int)tab];

    /// <summary>C++ Show :1600-1603.</summary>
    public void OnShow(RewardsCatalogTab tab)
    {
        Fighter = 0;
        Profile = 0;
        Detail = tabOffsets[(int)tab];
    }

    /// <summary>C++ OnFighterClicked :478-481 (the selection changed).</summary>
    public void OnSelectionChanged() => Detail = 0;

    /// <summary>C++ OnCatalogTabClicked :515-519: the old tab keeps where it was, the new one goes back to where it was.</summary>
    public void OnTabChanged(RewardsCatalogTab from, RewardsCatalogTab to)
    {
        if (from == to) return;
        tabOffsets[(int)from] = RewardsLayout.Max(0, Detail);
        Detail = tabOffsets[(int)to];
    }

    /// <summary>C++ the end of LayoutDetails :895-903: the offset clamped to the content, and kept as the tab's.</summary>
    public void AfterDetailLayout(RewardsCatalogTab tab, int contentHeight, int viewHeight)
    {
        Detail = Clamp(Detail, contentHeight, viewHeight);
        tabOffsets[(int)tab] = Detail;
    }

    /// <summary>C++ the end of LayoutFighters :621-622.</summary>
    public void AfterFighterLayout(int contentHeight, int viewHeight) => Fighter = Clamp(Fighter, contentHeight, viewHeight);

    /// <summary>C++ the end of LayoutProfile :941-942.</summary>
    public void AfterProfileLayout(int contentHeight, int viewHeight) => Profile = Clamp(Profile, contentHeight, viewHeight);

    /// <summary>C++ <c>Min(Max(0, content - view), Max(0, offset))</c>.</summary>
    public static int Clamp(int offset, int contentHeight, int viewHeight) =>
        RewardsLayout.Min(RewardsLayout.Max(0, contentHeight - viewHeight), RewardsLayout.Max(0, offset));

    /// <summary>C++ OnWheel :188-203: three body lines per notch, clamped; 0 = no change.</summary>
    public static int Wheel(int offset, int relative, int bodyHeight, int contentHeight, int viewHeight)
    {
        if (relative == 0) return offset;
        return Clamp(offset + (relative < 0 ? 3 : -3) * bodyHeight, contentHeight, viewHeight);
    }

    public enum Canvas { Fighter, Profile, Detail }

    /// <summary>The canvas's offset as the window shows it now (task 5.3b: the screen host also scrolls by its own wheel over an empty canvas area
    /// and by the scroll bar, MyGUI ScrollView's own handling): read back before a rule above uses it.</summary>
    public void Observe(Canvas canvas, int offset)
    {
        offset = RewardsLayout.Max(0, offset);
        switch (canvas)
        {
            case Canvas.Fighter: Fighter = offset; break;
            case Canvas.Profile: Profile = offset; break;
            default: Detail = offset; break;
        }
    }

    /// <summary>A wheel notch over one canvas.</summary>
    public void OnWheel(Canvas canvas, int relative, int bodyHeight, int contentHeight, int viewHeight)
    {
        switch (canvas)
        {
            case Canvas.Fighter: Fighter = Wheel(Fighter, relative, bodyHeight, contentHeight, viewHeight); break;
            case Canvas.Profile: Profile = Wheel(Profile, relative, bodyHeight, contentHeight, viewHeight); break;
            default: Detail = Wheel(Detail, relative, bodyHeight, contentHeight, viewHeight); break;
        }
    }

    /// <summary>C++ AbandonWindow :586 (and the window's first creation).</summary>
    public void Reset()
    {
        Array.Clear(tabOffsets);
        Fighter = Profile = Detail = 0;
    }
}

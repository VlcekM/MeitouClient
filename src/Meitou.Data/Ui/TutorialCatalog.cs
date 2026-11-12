namespace Meitou.Data.Ui;

/// <summary>
/// How a tutorial page moves on by itself (docs/game/ui-screens.md "Tutorials", advance tests); <see cref="None"/> waits for the player's button.
/// </summary>
public enum TutorialAdvance
{
    /// <summary>
    /// No test: the page waits for the player's button.
    /// </summary>
    None,

    /// <summary>
    /// Moves on once the camera has both panned and zoomed, after the page's wait.
    /// </summary>
    CameraControls,

    /// <summary>
    /// Moves on when the selection's lead is a player character.
    /// </summary>
    CharSelection,

    /// <summary>
    /// Moves on when the lead character has moved.
    /// </summary>
    CharMovement,

    /// <summary>
    /// Moves on when the camera follows a character.
    /// </summary>
    CenterChar,

    /// <summary>
    /// Moves on when the selection's lead is a player character, as <see cref="CharSelection"/>.
    /// </summary>
    SelectCharacter,

    /// <summary>
    /// Moves on once the overview screen's map tab (tab 0) was open, after the page's wait.
    /// </summary>
    Map,

    /// <summary>
    /// Moves on once Alt (the signs key) was held, after the page's wait.
    /// </summary>
    Signs,

    /// <summary>
    /// Moves on once a player character treats someone, after the page's wait.
    /// </summary>
    Medkiting,

    /// <summary>
    /// Moves on when the tutorial's recruit is the selection's lead (at once without a recruit).
    /// </summary>
    RecruitSelect,

    /// <summary>
    /// Moves on when the stats screen is open.
    /// </summary>
    RecruitStats,

    /// <summary>
    /// Moves on when the overview screen shows its main tab 4.
    /// </summary>
    SquadArrange,

    /// <summary>
    /// Moves on when the lead is a player character lying unconscious (not playing dead).
    /// </summary>
    SelectUnconscious,

    /// <summary>
    /// Moves on once a player character picks a lock, after the page's wait; the imprisoned player characters at that moment are remembered.
    /// </summary>
    Lockpicking,

    /// <summary>
    /// Moves on when one of the prisoners <see cref="Lockpicking"/> remembered is in a cage whose lock is open.
    /// </summary>
    LockpickingSuccessful,

    /// <summary>
    /// Moves on when the overview screen shows its main tab 2 or 3.
    /// </summary>
    CraftButton,

    /// <summary>
    /// Moves on when the overview screen shows its main tab 3, crafting.
    /// </summary>
    CraftTab,

    /// <summary>
    /// Moves on when the focused building is a complete production building with an output.
    /// </summary>
    ProductionSelection,

    /// <summary>
    /// Moves on when the focused building is complete.
    /// </summary>
    ConstructionMaterials,
}

/// <summary>
/// What a page highlights (<c>TutorialGUI::HighlightItem</c>, KenshiLib 1ce4a16; the numbers are the exe's).
/// </summary>
public enum TutorialHighlight
{
    /// <summary>
    /// Highlights nothing.
    /// </summary>
    None,

    /// <summary>
    /// The squad's portraits.
    /// </summary>
    Portraits,

    /// <summary>
    /// The selected character's name panel.
    /// </summary>
    PanelStats,

    /// <summary>
    /// The stats panel.
    /// </summary>
    PanelStatsBottom,

    /// <summary>
    /// The stats panel's Power line.
    /// </summary>
    PanelStatsBottomPowerButton,

    /// <summary>
    /// The stats panel's Workers line.
    /// </summary>
    PanelStatsBottomWorkers,

    /// <summary>
    /// The medical panel.
    /// </summary>
    PanelMedical,

    /// <summary>
    /// The medical panel's status part.
    /// </summary>
    PanelMedicalStatus,

    /// <summary>
    /// The medical panel's second bar.
    /// </summary>
    PanelMedicalBarBody,

    /// <summary>
    /// The medical panel's fifth bar.
    /// </summary>
    PanelMedicalBarLimbs,

    /// <summary>
    /// The medical panel's first bar.
    /// </summary>
    PanelMedicalBarBlood,

    /// <summary>
    /// The medical panel's second bar.
    /// </summary>
    PanelMedicalBarPower,

    /// <summary>
    /// The medical panel's second bar: an unfinished building's materials.
    /// </summary>
    PanelMedicalBarMaterials,

    /// <summary>
    /// The medical panel's third bar: the materials of an unfinished building with a door.
    /// </summary>
    PanelMedicalBarMaterialsBuilding,

    /// <summary>
    /// The medical panel's first bar: an unfinished building's condition.
    /// </summary>
    PanelMedicalBarCondition,

    /// <summary>
    /// The medical panel's second bar: the condition of an unfinished building with a door.
    /// </summary>
    PanelMedicalBarConditionBuilding,

    /// <summary>
    /// The game speed buttons.
    /// </summary>
    PanelGameSpeed,

    /// <summary>
    /// The time and money panel.
    /// </summary>
    PanelTimeMoney,

    /// <summary>
    /// The build button's panel.
    /// </summary>
    ButtonBuild,

    /// <summary>
    /// The bounty panel.
    /// </summary>
    PanelBounty,

    /// <summary>
    /// The menu bar of shortcut buttons.
    /// </summary>
    PanelMenuBar,

    /// <summary>
    /// The menu bar's squad button.
    /// </summary>
    PanelMenuBarSquad,

    /// <summary>
    /// The menu bar's stats button.
    /// </summary>
    PanelMenuBarStats,

    /// <summary>
    /// The menu bar's tech button.
    /// </summary>
    PanelMenuBarTech,

    /// <summary>
    /// The orders panel.
    /// </summary>
    PanelOrders,

    /// <summary>
    /// The order buttons together: block, hold, passive, chase, taunt, ranged and sneak.
    /// </summary>
    PanelOrdersCommands,

    /// <summary>
    /// The jobs list.
    /// </summary>
    PanelOrdersJobs,

    /// <summary>
    /// The button that removes a job.
    /// </summary>
    PanelOrdersJobRemove,

    /// <summary>
    /// The row of the first Obedient Slave job; nothing when there is none.
    /// </summary>
    PanelOrdersJobObedience,

    /// <summary>
    /// The orders panel's medic button.
    /// </summary>
    PanelOrdersMedicButton,

    /// <summary>
    /// The orders panel's rescue button.
    /// </summary>
    PanelOrdersRescueButton,

    /// <summary>
    /// The overview screen's crafting tab button (main tab 3).
    /// </summary>
    MapscreenCraftTab,

    /// <summary>
    /// The contents of the overview screen's crafting tab.
    /// </summary>
    MapscreenCraftTabItems,

    /// <summary>
    /// The input column of a building's inventory.
    /// </summary>
    BuildingInventoryInput,

    /// <summary>
    /// The build mode's list of buildings.
    /// </summary>
    BuildModePanelBuildings,
}

/// <summary>
/// A tutorial page: its text's source line in <c>kenshi/TutorialsData.cpp</c>, whether its Next reads CONTINUE (else SKIP), its own advance, and the
/// HUD part its start highlights (<paramref name="Run"/>: its run sets the highlight each frame while its condition holds, docs/game/ui-screens.md
/// "Tutorials").
/// </summary>
public readonly record struct TutorialPage(int Line, bool Continue, TutorialAdvance Advance = TutorialAdvance.None,
    TutorialHighlight Highlight = TutorialHighlight.None, bool Run = false);

/// <summary>
/// The 34 tutorials' titles and pages (<b>Verified</b>, the item and page constructors; docs/game/ui-screens.md "Tutorials"). The texts are the
/// installed English template's msgids at those source lines, through the catalog; no game text is embedded.
/// </summary>
public sealed class TutorialCatalog
{
    /// <summary>
    /// The source file the tutorials' msgids are referenced from in the template.
    /// </summary>
    const string Source = "kenshi/TutorialsData.cpp";

    /// <summary>
    /// A page that only goes on with CONTINUE.
    /// </summary>
    static TutorialPage G(int line) => new(line, true);
    /// <summary>
    /// A page that moves on by its own <paramref name="advance"/>; its button reads SKIP.
    /// </summary>
    static TutorialPage S(int line, TutorialAdvance advance, TutorialHighlight highlight = TutorialHighlight.None, bool run = false) =>
        new(line, false, advance, highlight, run);
    /// <summary>
    /// A page whose button reads CONTINUE, with an optional advance and highlight.
    /// </summary>
    static TutorialPage C(int line, TutorialAdvance advance = TutorialAdvance.None, TutorialHighlight highlight = TutorialHighlight.None,
        bool run = false) => new(line, true, advance, highlight, run);
    /// <summary>
    /// A CONTINUE page that highlights <paramref name="highlight"/>.
    /// </summary>
    static TutorialPage H(int line, TutorialHighlight highlight, bool run = false) => new(line, true, TutorialAdvance.None, highlight, run);

    /// <summary>
    /// Per id: the title's line and the pages in order. The run highlights hold while the focus is a complete production building (23), build mode
    /// places (30), or the focused building is unfinished (31: materials, then condition; their door variant for a building with a door).
    /// </summary>
    public static readonly (int Title, TutorialPage[] Pages)[] Items =
    [
        (220, [S(108, TutorialAdvance.CameraControls), S(156, TutorialAdvance.CharSelection, TutorialHighlight.Portraits),
            S(182, TutorialAdvance.CharMovement), S(203, TutorialAdvance.CenterChar), G(228)]),
        (406, [C(247, TutorialAdvance.SelectCharacter), H(262, TutorialHighlight.PanelStats), H(289, TutorialHighlight.PanelMedical),
            H(309, TutorialHighlight.PanelGameSpeed), H(329, TutorialHighlight.PanelTimeMoney), H(349, TutorialHighlight.ButtonBuild),
            H(369, TutorialHighlight.PanelMenuBar), H(389, TutorialHighlight.PanelOrders)]),
        (468, [G(473), S(440, TutorialAdvance.Map), G(475), G(476), G(477)]),
        (538, [G(542), S(511, TutorialAdvance.Signs), G(544)]),
        (598, [H(581, TutorialHighlight.PanelMedical), G(602)]),
        (785, [S(636, TutorialAdvance.Medkiting), H(681, TutorialHighlight.PanelOrdersMedicButton), G(790),
            H(704, TutorialHighlight.PanelMedicalBarBody), H(733, TutorialHighlight.PanelMedicalBarLimbs),
            H(761, TutorialHighlight.PanelMedicalBarBlood), G(794)]),
        (853, [G(856), G(857), G(858), H(832, TutorialHighlight.PanelOrdersCommands)]),
        (972, [S(895, TutorialAdvance.RecruitSelect), S(920, TutorialAdvance.RecruitStats, TutorialHighlight.PanelMenuBarStats), G(977),
            S(946, TutorialAdvance.SquadArrange, TutorialHighlight.PanelMenuBarSquad)]),
        (1014, [G(1018), G(1019), G(1020), G(1021)]),
        (1096, [G(1100), S(1058, TutorialAdvance.SelectUnconscious), G(1102), G(1103), G(1104)]),
        (1142, [G(1146), G(1147), H(1130, TutorialHighlight.PanelOrdersRescueButton)]),
        (1290, [G(1294), S(1188, TutorialAdvance.Lockpicking), S(1261, TutorialAdvance.LockpickingSuccessful), G(1297)]),
        (1325, [G(1328), S(1188, TutorialAdvance.Lockpicking), S(1261, TutorialAdvance.LockpickingSuccessful), G(1331)]),
        (1354, [G(1358), G(1359), G(1360), G(1361), G(1362)]),
        (1387, [G(1391), G(1392), G(1393), G(1394)]),
        (1430, [H(1413, TutorialHighlight.PanelBounty), G(1435), G(1436), G(1437)]),
        (1469, [G(1473), G(1474)]),
        (1520, [H(1503, TutorialHighlight.PanelMedicalStatus), G(1525)]),
        (1592, [H(1555, TutorialHighlight.PanelOrdersJobs), G(1597), G(1598), H(1575, TutorialHighlight.PanelOrdersJobRemove)]),
        (1653, [G(1657), H(1636, TutorialHighlight.PanelOrdersJobObedience), G(1659), G(1660)]),
        (1707, [G(1711), H(1690, TutorialHighlight.PanelMedicalStatus), G(1713)]),
        (1787, [H(1770, TutorialHighlight.PanelMedicalStatus), G(1792)]),
        (2278, [H(2266, TutorialHighlight.PanelStatsBottom), G(2282)]),
        (2166, [G(2170), S(2018, TutorialAdvance.ProductionSelection), H(2033, TutorialHighlight.PanelStatsBottom),
            H(2053, TutorialHighlight.PanelMedicalBarPower, true), H(2082, TutorialHighlight.PanelStatsBottomPowerButton, true),
            H(2111, TutorialHighlight.PanelStatsBottomWorkers, true), G(2176), H(2140, TutorialHighlight.BuildingInventoryInput, true)]),
        (1828, [G(1832), G(1833), G(1834), G(1835)]),
        (1924, [S(1854, TutorialAdvance.CraftButton, TutorialHighlight.PanelMenuBarTech),
            S(1881, TutorialAdvance.CraftTab, TutorialHighlight.MapscreenCraftTab), G(1930), H(1907, TutorialHighlight.MapscreenCraftTabItems),
            G(1932), G(1933), G(1934)]),
        (1981, [H(1964, TutorialHighlight.ButtonBuild), G(1986), G(1987)]),
        (2231, [H(2214, TutorialHighlight.ButtonBuild), G(2236)]),
        (2319, [G(2323), G(2324), H(2302, TutorialHighlight.PanelMenuBarTech), G(2326)]),
        (2353, [G(2357), G(2358), G(2359)]),
        (2431, [G(2435), H(2405, TutorialHighlight.BuildModePanelBuildings, true), G(2437), G(2438), G(2439), G(2440)]),
        (2554, [G(2558), S(2459, TutorialAdvance.ConstructionMaterials, TutorialHighlight.PanelMedicalBarMaterials, true),
            H(2507, TutorialHighlight.PanelMedicalBarCondition, true)]),
        (2585, [G(2587), G(2588)]),
        (2601, [G(2602), G(2603)]),
    ];

    /// <summary>
    /// The English msgids by their source line in <see cref="Source"/>.
    /// </summary>
    readonly Dictionary<int, string> _texts = [];

    /// <summary>
    /// The English msgid at a source line ("" when the template lacks it).
    /// </summary>
    public string EnglishAt(int line) => _texts.GetValueOrDefault(line, "");
    /// <summary>
    /// The text at a source line in the active language.
    /// </summary>
    public string TextAt(int line) => Loc.Tr(EnglishAt(line));
    /// <summary>
    /// The title of tutorial <paramref name="id"/> in the active language.
    /// </summary>
    public string Title(int id) => TextAt(Items[id].Title);
    /// <summary>
    /// The template had the tutorials' texts.
    /// </summary>
    public bool HasTexts => _texts.Count > 0;

    /// <summary>
    /// The catalog of the template's <paramref name="entries"/>: each msgid referenced from <see cref="Source"/>, by its line.
    /// </summary>
    public static TutorialCatalog From(IEnumerable<PotEntry> entries)
    {
        var result = new TutorialCatalog();
        foreach (var entry in entries)
            foreach (var (file, line) in entry.References)
                if (file == Source) result._texts[line] = entry.Id;
        return result;
    }
}
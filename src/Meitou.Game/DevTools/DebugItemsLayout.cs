using Meitou.Data.Ui;

namespace Meitou.Game.DevTools;

/// <summary>
/// The Items window's layout: a search box across the top, the categories and the items of the picked one as two lists (the build window's pair of
/// <c>ListBox</c>es) and the fake trader's stock grid beside them, which takes the whole width while a search shows its results
/// (<see cref="DebugItemsWindow"/>). The layout never changes: the lists are bound, and the stock grid is placed and sized by the window.
/// </summary>
static class DebugItemsLayout
{
    #region Names

    /// <summary>
    /// The screen's id.
    /// </summary>
    internal const string Screen = "debug-items";

    /// <summary>
    /// The window's root widget (its caption).
    /// </summary>
    internal const string Root = "Root";

    /// <summary>
    /// The search label, its box and the button that empties it.
    /// </summary>
    internal const string SearchLabel = "SearchLabel", SearchBox = "SearchBox", ClearButton = "ClearButton";

    /// <summary>
    /// The action the clear button raises.
    /// </summary>
    internal const string ClearAction = "clear-search";

    /// <summary>
    /// The quality filter's label and its dropdown, at the right end of the search row.
    /// </summary>
    internal const string QualityLabel = "QualityLabel", QualityCombo = "QualityCombo";

    /// <summary>
    /// The line under the stock that says what a drag does.
    /// </summary>
    internal const string HelpText = "HelpText";

    /// <summary>
    /// The headings over the two lists.
    /// </summary>
    internal const string CategoriesHead = "CategoriesHead", ItemsHead = "ItemsHead";

    /// <summary>
    /// The categories list and the list of the picked category's items.
    /// </summary>
    internal const string CategoriesList = "CategoriesList", ItemsList = "ItemsList";

    /// <summary>
    /// The texts shown over the lists and the stock while they have nothing to show.
    /// </summary>
    internal const string CategoriesEmpty = "CategoriesEmpty", ItemsEmpty = "ItemsEmpty", StockEmpty = "StockEmpty";

    /// <summary>
    /// The stock's slot grid (drawn as the scroll view's canvas) and the scroll view that holds it.
    /// </summary>
    internal const string StockGrid = "StockGrid", StockView = "StockView";

    /// <summary>
    /// The control property an item cell of the stock carries: the cell's number.
    /// </summary>
    internal const string CellProperty = "DebugStockItem";

    #endregion

    #region Sizes

    /// <summary>
    /// The window's size in layout pixels (at a 1080 line screen); it shrinks to the room above the squad bar.
    /// </summary>
    internal const float WindowWidth = 1280, WindowHeight = 640;

    /// <summary>
    /// The stock's place in the window as fractions of it, with the lists beside it and without them (a search's results).
    /// </summary>
    internal static readonly UiRect StockBeside = new(0.49f, 0.14f, 0.498f, 0.762f), StockWide = new(0.012f, 0.14f, 0.976f, 0.762f);

    /// <summary>
    /// The light ink of text on the window's dark surface (the original's name boxes use it).
    /// </summary>
    const string LightInk = "0.894737 0.860072 0.811019";

    #endregion

    #region Layout

    /// <summary>
    /// The window's layout.
    /// </summary>
    internal static MyGuiLayout Build() => MyGuiReader.ParseLayout($"""
        <MyGUI type="Layout">
          <Widget type="Window" skin="Kenshi_WindowCX" position_real="0 0 0.62 0.6" layer="Window" name="{Root}">
            <Property key="Caption" value="ITEMS"/>
            {Label(SearchLabel, "0.012 0.012 0.113 0.062", "Right VCenter")}
            <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.128 0.012 0.367 0.062" name="{SearchBox}">
              <Property key="MaxTextLength" value="40"/><Property key="TextAlign" value="Left VCenter"/>
            </Widget>
            <Widget type="Button" skin="Kenshi_Button1" position_real="0.5 0.012 0.075 0.062" name="{ClearButton}"/>
            {Label(QualityLabel, "0.68 0.012 0.18 0.062", "Right VCenter")}
            <Widget type="ComboBox" skin="Kenshi_ComboBox" position_real="0.868 0.018 0.12 0.05" name="{QualityCombo}"/>
            {Heading(CategoriesHead, "0.012 0.085 0.19 0.05")}
            {Heading(ItemsHead, "0.215 0.085 0.26 0.05")}
            <Widget type="ListBox" skin="Kenshi_ListBoxEmpty" position_real="0.012 0.14 0.19 0.762" name="{CategoriesList}"/>
            <Widget type="ListBox" skin="Kenshi_ListBoxEmpty" position_real="0.215 0.14 0.26 0.762" name="{ItemsList}"/>
            {Label(CategoriesEmpty, "0.02 0.4 0.174 0.1", "Center")}
            {Label(ItemsEmpty, "0.225 0.4 0.24 0.1", "Center")}
            <Widget type="Widget" skin="Kenshi_InventorySlotSkin" position_real="0.49 0.14 0.498 0.762" name="{StockGrid}"/>
            <Widget type="ScrollView" skin="Kenshi_ScrollView" position_real="0.49 0.14 0.498 0.762" name="{StockView}">
              <Property key="CanvasSize" value="450 600"/><Property key="CanvasAlign" value="Default"/><Property key="VisibleHScroll" value="false"/>
            </Widget>
            {Label(StockEmpty, "0.49 0.4 0.498 0.1", "Center")}
            {Label(HelpText, "0.012 0.912 0.976 0.078", "Left VCenter")}
          </Widget>
        </MyGUI>
        """);

    /// <summary>
    /// A line of text in the window's light ink, wrapped, aligned as <paramref name="align"/>.
    /// </summary>
    static string Label(string name, string position, string align) => $"""
        <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="{position}" name="{name}">
          <Property key="TextAlign" value="{align}"/><Property key="TextColour" value="{LightInk}"/><Property key="WordWrap" value="true"/>
        </Widget>
        """;

    /// <summary>
    /// A list's heading: its title in large light ink, centred.
    /// </summary>
    static string Heading(string name, string position) => $"""
        <Widget type="TextBox" skin="Kenshi_TextboxStandardText_Large" position_real="{position}" name="{name}">
          <Property key="TextAlign" value="Center"/><Property key="TextColour" value="{LightInk}"/>
        </Widget>
        """;

    #endregion
}

using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Game;
using Meitou.Game.Ui;
using Meitou.Lua;

namespace Meitou.Modding;

/// <summary>
/// Transparent MyGUI overlay screen (<see cref="ScreenId"/>) that renders <see cref="LuaHudState"/>
/// (ImageBox atlases, TextBox labels, and clickable Buttons) directly on <see cref="MyGuiScreenHost"/>.
/// Opens automatically when a Lua mod places widgets and closes when empty.
/// </summary>
public sealed class LuaHudOverlay
{
    /// <summary>
    /// Screen id registered on <see cref="MyGuiScreenHost"/>.
    /// </summary>
    public const string ScreenId = "meitou.lua.hud";

#pragma warning disable MEI0002 // MyGUI layer name, not user-facing text
    /// <summary>
    /// The MyGUI layer the overlay's root widget sits on.
    /// </summary>
    const string OverlayLayer = "Back";
#pragma warning restore MEI0002

    /// <summary>
    /// The HUD state this overlay renders.
    /// </summary>
    readonly LuaHudState _state;

    /// <summary>
    /// Called with the owning mod and the widget id when a Lua HUD button is clicked.
    /// </summary>
    readonly Action<string, string> _onButtonClick;

    /// <summary>
    /// The buttons bound at the last <see cref="Bind"/>, in slot order, so a click maps back to its mod and widget.
    /// </summary>
    IReadOnlyList<LuaHudItem> _boundButtons = [];

    /// <summary>
    /// Whether the layout is registered on the screen host.
    /// </summary>
    bool _registered;

    /// <summary>
    /// Creates a new Lua HUD overlay bound to <paramref name="state"/>.
    /// </summary>
    public LuaHudOverlay(LuaHudState state, Action<string, string> onButtonClick)
    {
        _state = state;
        _onButtonClick = onButtonClick;
    }

    /// <summary>
    /// Builds the transparent overlay layout with bounded image, text, and button slots.
    /// </summary>
    public static MyGuiLayout BuildLayout()
    {
        Dictionary<string, string> empty = [];
        Dictionary<string, string> imgProps = new() { ["NeedMouse"] = "false", ["Alpha"] = "0" };
        Dictionary<string, string> txtProps = new() { ["TextAlign"] = "Center VCenter", ["NeedMouse"] = "false" };
        Dictionary<string, string> rootProps = new() { ["NeedMouse"] = "false" };

        List<MyGuiWidget> children = new(LuaHudState.MaxTotalImages + LuaHudState.MaxTotalTexts + LuaHudState.MaxTotalButtons);
        AddImageSlots(children, imgProps, empty);
        AddTextSlots(children, txtProps, empty);
        AddButtonSlots(children, empty);

        MyGuiWidget root = new("Widget", "LuaHudRoot", "PanelEmpty", OverlayLayer, "", new UiRect(0, 0, 1f, 1f), true, rootProps, empty, children);

        return new MyGuiLayout("3.2.0", [root]);
    }

    /// <summary>
    /// Registers this overlay on <paramref name="screens"/> once.
    /// </summary>
    public void EnsureRegistered(MyGuiScreenHost screens)
    {
        if (_registered) return;

        _registered = true;
        screens.Register(ScreenId, BuildLayout());
    }

    /// <summary>
    /// Opens the overlay when any Lua HUD widget exists and closes it when empty.
    /// </summary>
    public void SyncOpen(MyGuiScreenHost screens)
    {
        EnsureRegistered(screens);

        bool hasAny = _state.HasAny;
        bool open = screens.IsOpen(ScreenId);
        if (hasAny && !open) screens.Open(ScreenId);
        else if (!hasAny && open) screens.Close(ScreenId);
    }

    /// <summary>
    /// Binds all active Lua images, texts, and buttons onto <paramref name="b"/>.
    /// </summary>
    public void Bind(UiBindings b, float width, float height)
    {
        float vw = width > 1f ? width : 1920f;
        float vh = height > 1f ? height : 1080f;

        IReadOnlyList<LuaHudImage> imgs = _state.ImageSnapshot();
        IReadOnlyList<LuaHudItem> texts = _state.TextSnapshot();
        IReadOnlyList<LuaHudItem> btns = _state.ButtonSnapshot();
        _boundButtons = btns;

        BindImages(b, imgs, vw, vh);
        BindTexts(b, texts, vw, vh);
        BindButtons(b, btns, vw, vh);
    }

    /// <summary>
    /// Forwards clicks on Lua HUD buttons to <see cref="KenshiLuaHost.DispatchHudClick"/>.
    /// </summary>
    public void HandleEvent(MyGuiEvent e)
    {
        if (e.Widget is null || !e.Widget.StartsWith("luaBtn", StringComparison.Ordinal)) return;
        if (!int.TryParse(e.Widget[6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) return;

        IReadOnlyList<LuaHudItem> btns = _boundButtons;
        if (idx >= 0 && idx < btns.Count)
        {
            _onButtonClick(btns[idx].OwnerMod, btns[idx].Id);
        }
    }

    /// <summary>
    /// The widget name of image slot <paramref name="i"/>.
    /// </summary>
    static string ImgName(int i) => "luaImg" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The widget name of text slot <paramref name="i"/>.
    /// </summary>
    static string SlotName(int i) => "luaTxt" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The widget name of button slot <paramref name="i"/>.
    /// </summary>
    static string BtnName(int i) => "luaBtn" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Adds the bounded image slots to the layout's children.
    /// </summary>
    static void AddImageSlots(List<MyGuiWidget> children, Dictionary<string, string> imgProps, Dictionary<string, string> empty)
    {
        for (int i = 0; i < LuaHudState.MaxTotalImages; i++)
        {
            UiRect rect = new(0, 0, 0.001f, 0.001f);
            children.Add(new MyGuiWidget("ImageBox", ImgName(i), "ImageBox", "", "", rect, true, imgProps, empty, []));
        }
    }

    /// <summary>
    /// Adds the bounded text slots to the layout's children.
    /// </summary>
    static void AddTextSlots(List<MyGuiWidget> children, Dictionary<string, string> txtProps, Dictionary<string, string> empty)
    {
        for (int i = 0; i < LuaHudState.MaxTotalTexts; i++)
        {
            UiRect rect = new(0, 0, 0.01f, 0.01f);
            children.Add(new MyGuiWidget("TextBox", SlotName(i), "Kenshi_TextboxStandardText", "", "", rect, true, txtProps, empty, []));
        }
    }

    /// <summary>
    /// Adds the bounded button slots to the layout's children.
    /// </summary>
    static void AddButtonSlots(List<MyGuiWidget> children, Dictionary<string, string> empty)
    {
        for (int i = 0; i < LuaHudState.MaxTotalButtons; i++)
        {
            UiRect rect = new(0, 0, 0.01f, 0.01f);
            children.Add(new MyGuiWidget("Button", BtnName(i), "Kenshi_Button1", "", "", rect, true, empty, empty, []));
        }
    }

    /// <summary>
    /// Binds the image slots: the first ones to the active images, the rest hidden.
    /// </summary>
    static void BindImages(UiBindings b, IReadOnlyList<LuaHudImage> imgs, float vw, float vh)
    {
        for (int i = 0; i < LuaHudState.MaxTotalImages; i++)
        {
            string name = ImgName(i);
            if (i >= imgs.Count)
            {
                Hide(b, name);
                continue;
            }

            LuaHudImage it = imgs[i];
            Dictionary<string, string> props = new(4)
            {
                ["ImageTexture"] = it.Texture,
                ["Colour"] = it.Color,
                ["Alpha"] = AlphaText(it.Alpha),
            };
            if (it.Rect.Length > 0) props["ImageRect"] = it.Rect;

            b.Properties[name] = props;
            b.Positions[name] = Scaled(it.X, it.Y, it.W, it.H, vw, vh);
        }
    }

    /// <summary>
    /// Binds the text slots: the first ones to the active texts, the rest hidden.
    /// </summary>
    static void BindTexts(UiBindings b, IReadOnlyList<LuaHudItem> texts, float vw, float vh)
    {
        for (int i = 0; i < LuaHudState.MaxTotalTexts; i++)
        {
            string name = SlotName(i);
            if (i >= texts.Count)
            {
                b.Text[name] = " ";
                Hide(b, name);
                continue;
            }

            LuaHudItem it = texts[i];
            b.Text[name] = it.Text.Length == 0 ? " " : it.Text;
            b.Properties[name] = new Dictionary<string, string>
            {
                ["TextColour"] = it.Color,
                ["Alpha"] = AlphaText(it.Alpha),
                ["TextAlign"] = it.Align,
            };
            b.Positions[name] = Scaled(it.X, it.Y, it.W, it.H, vw, vh);
        }
    }

    /// <summary>
    /// Binds the button slots: the first ones to the active buttons, the rest hidden.
    /// </summary>
    static void BindButtons(UiBindings b, IReadOnlyList<LuaHudItem> btns, float vw, float vh)
    {
        for (int i = 0; i < LuaHudState.MaxTotalButtons; i++)
        {
            string name = BtnName(i);
            if (i >= btns.Count)
            {
                b.Text[name] = " ";
                Hide(b, name);
                continue;
            }

            LuaHudItem it = btns[i];
            b.Disabled.Remove(name);
            b.Text[name] = it.Text.Length == 0 ? " " : it.Text;
            b.Properties[name] = new Dictionary<string, string>
            {
                ["TextColour"] = it.Color,
                ["Alpha"] = "1",
            };
            b.Positions[name] = Scaled(it.X, it.Y, it.W, it.H, vw, vh);
        }
    }

    /// <summary>
    /// Hides an unused slot: makes <paramref name="name"/> transparent and moves it to a one-pixel rectangle at the origin.
    /// </summary>
    static void Hide(UiBindings b, string name)
    {
        b.Properties[name] = new Dictionary<string, string> { ["Alpha"] = "0" };
        b.Positions[name] = new UiRect(0, 0, 1, 1);
    }

    /// <summary>
    /// The widget rectangle for fractions of the view, at least one pixel wide and high.
    /// </summary>
    static UiRect Scaled(double x, double y, double w, double h, float vw, float vh) =>
        new((float)(x * vw), (float)(y * vh), Math.Max(1f, (float)(w * vw)), Math.Max(1f, (float)(h * vh)));

    /// <summary>
    /// The alpha as the text MyGUI reads, culture-invariant.
    /// </summary>
    static string AlphaText(double alpha) => alpha.ToString("0.###", CultureInfo.InvariantCulture);
}

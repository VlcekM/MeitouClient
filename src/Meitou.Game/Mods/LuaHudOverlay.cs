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
    /// <summary>Screen id registered on <see cref="MyGuiScreenHost"/>.</summary>
    public const string ScreenId = "meitou.lua.hud";
#pragma warning disable MEI0002 // MyGUI layer name, not user-facing text
    const string OverlayLayer = "Back";
#pragma warning restore MEI0002

    readonly LuaHudState _state;
    readonly Action<string, string> _onButtonClick;
    IReadOnlyList<LuaHudItem> _boundButtons = [];
    bool _registered;

    /// <summary>Creates a new Lua HUD overlay bound to <paramref name="state"/>.</summary>
    public LuaHudOverlay(LuaHudState state, Action<string, string> onButtonClick)
    {
        _state = state;
        _onButtonClick = onButtonClick;
    }

    static string ImgName(int i) => "luaImg" + i.ToString(CultureInfo.InvariantCulture);
    static string SlotName(int i) => "luaTxt" + i.ToString(CultureInfo.InvariantCulture);
    static string BtnName(int i) => "luaBtn" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>Builds the transparent overlay layout with bounded image, text, and button slots.</summary>
    public static MyGuiLayout BuildLayout()
    {
        Dictionary<string, string> empty = [];
        Dictionary<string, string> imgProps = new() { ["NeedMouse"] = "false", ["Alpha"] = "0" };
        Dictionary<string, string> txtProps = new() { ["TextAlign"] = "Center VCenter", ["NeedMouse"] = "false" };
        Dictionary<string, string> rootProps = new() { ["NeedMouse"] = "false" };

        List<MyGuiWidget> children = new(LuaHudState.MaxTotalImages + LuaHudState.MaxTotalTexts + LuaHudState.MaxTotalButtons);
        for (int i = 0; i < LuaHudState.MaxTotalImages; i++)
        {
            children.Add(new MyGuiWidget("ImageBox", ImgName(i), "ImageBox", "", "", new UiRect(0, 0, 0.001f, 0.001f), true, imgProps, empty, []));
        }
        for (int i = 0; i < LuaHudState.MaxTotalTexts; i++)
        {
            children.Add(new MyGuiWidget("TextBox", SlotName(i), "Kenshi_TextboxStandardText", "", "", new UiRect(0, 0, 0.01f, 0.01f), true, txtProps, empty, []));
        }
        for (int i = 0; i < LuaHudState.MaxTotalButtons; i++)
        {
            children.Add(new MyGuiWidget("Button", BtnName(i), "Kenshi_Button1", "", "", new UiRect(0, 0, 0.01f, 0.01f), true, empty, empty, []));
        }
        MyGuiWidget root = new("Widget", "LuaHudRoot", "PanelEmpty", OverlayLayer, "", new UiRect(0, 0, 1f, 1f), true, rootProps, empty, children);
        return new MyGuiLayout("3.2.0", [root]);
    }

    /// <summary>Registers this overlay on <paramref name="screens"/> once.</summary>
    public void EnsureRegistered(MyGuiScreenHost screens)
    {
        if (_registered) return;
        _registered = true;
        screens.Register(ScreenId, BuildLayout());
    }

    /// <summary>Opens the overlay when any Lua HUD widget exists and closes it when empty.</summary>
    public void SyncOpen(MyGuiScreenHost screens)
    {
        EnsureRegistered(screens);
        bool hasAny = _state.HasAny;
        bool open = screens.IsOpen(ScreenId);
        if (hasAny && !open) screens.Open(ScreenId);
        else if (!hasAny && open) screens.Close(ScreenId);
    }

    /// <summary>Binds all active Lua images, texts, and buttons onto <paramref name="b"/>.</summary>
    public void Bind(UiBindings b, float width, float height)
    {
        float vw = width > 1f ? width : 1920f;
        float vh = height > 1f ? height : 1080f;

        IReadOnlyList<LuaHudImage> imgs = _state.ImageSnapshot();
        IReadOnlyList<LuaHudItem> texts = _state.TextSnapshot();
        IReadOnlyList<LuaHudItem> btns = _state.ButtonSnapshot();
        _boundButtons = btns;

        for (int i = 0; i < LuaHudState.MaxTotalImages; i++)
        {
            string name = ImgName(i);
            if (i < imgs.Count)
            {
                LuaHudImage it = imgs[i];
                string alphaStr = it.Alpha.ToString("0.###", CultureInfo.InvariantCulture);
                Dictionary<string, string> props = new(4)
                {
                    ["ImageTexture"] = it.Texture,
                    ["Colour"] = it.Color,
                    ["Alpha"] = alphaStr,
                };
                if (it.Rect.Length > 0) props["ImageRect"] = it.Rect;
                b.Properties[name] = props;
                b.Positions[name] = new UiRect((float)(it.X * vw), (float)(it.Y * vh), Math.Max(1f, (float)(it.W * vw)), Math.Max(1f, (float)(it.H * vh)));
            }
            else
            {
                b.Properties[name] = new Dictionary<string, string> { ["Alpha"] = "0" };
                b.Positions[name] = new UiRect(0, 0, 1, 1);
            }
        }

        for (int i = 0; i < LuaHudState.MaxTotalTexts; i++)
        {
            string name = SlotName(i);
            if (i < texts.Count)
            {
                LuaHudItem it = texts[i];
                b.Text[name] = it.Text.Length == 0 ? " " : it.Text;
                b.Properties[name] = new Dictionary<string, string>
                {
                    ["TextColour"] = it.Color,
                    ["Alpha"] = it.Alpha.ToString("0.###", CultureInfo.InvariantCulture),
                    ["TextAlign"] = it.Align,
                };
                b.Positions[name] = new UiRect((float)(it.X * vw), (float)(it.Y * vh), Math.Max(1f, (float)(it.W * vw)), Math.Max(1f, (float)(it.H * vh)));
            }
            else
            {
                b.Text[name] = " ";
                b.Properties[name] = new Dictionary<string, string> { ["Alpha"] = "0" };
                b.Positions[name] = new UiRect(0, 0, 1, 1);
            }
        }

        for (int i = 0; i < LuaHudState.MaxTotalButtons; i++)
        {
            string name = BtnName(i);
            if (i < btns.Count)
            {
                LuaHudItem it = btns[i];
                b.Disabled.Remove(name);
                b.Text[name] = it.Text.Length == 0 ? " " : it.Text;
                b.Properties[name] = new Dictionary<string, string>
                {
                    ["TextColour"] = it.Color,
                    ["Alpha"] = "1",
                };
                b.Positions[name] = new UiRect((float)(it.X * vw), (float)(it.Y * vh), Math.Max(1f, (float)(it.W * vw)), Math.Max(1f, (float)(it.H * vh)));
            }
            else
            {
                b.Text[name] = " ";
                b.Properties[name] = new Dictionary<string, string> { ["Alpha"] = "0" };
                b.Positions[name] = new UiRect(0, 0, 1, 1);
            }
        }
    }

    /// <summary>Forwards clicks on Lua HUD buttons to <see cref="KenshiLuaHost.DispatchHudClick"/>.</summary>
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
}

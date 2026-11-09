using System.Globalization;
using System.Numerics;
using Meitou.Data.Save;
using Meitou.Data.Textures;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Modding;

namespace Meitou.Game.Title;

/// <summary>
/// The Meitou title's main menu, after the Meitou UI kit (docs/meitou-ui.md, "Layout and states"): a stack 320 wide, 56 from the left and 36
/// from the bottom of a 1600×900 frame, scaled with the height. On top, the save card: worn paper with its dark edge and a drop shadow, the
/// CONTINUE with an ENTER key badge, and the save's name, day, place, squad and money. Below it, plate buttons (the game's
/// Button1Skin at 70% brightness, the pushed skin at 85% under the mouse) with the order-sprite icons. The surfaces are baked once per size
/// from crops of the game's Kenshi_UI.png (rounded corners, edges and shadow included) and drawn as images; the original title's buttons
/// stay in place, invisible, as the hit targets, so input is the faithful screen's.
/// </summary>
sealed class TitleMenu(PlayerInterface player)
{
    /// <summary>
    /// An upper-case button or title (<c>NEW GAME</c>, <c>YES</c>) in the active language: the catalog's message of the same text (<c>CONTINUE</c> is
    /// one), else of its capitalised form (<c>New game</c>) in upper case. Text that is not a message is unchanged.
    /// </summary>
    internal static string UpperTr(string upper)
    {
        if (!Loc.Translating || upper.Length == 0) return upper;
        string exact = Loc.Tr(upper);
        if (exact != upper) return exact;
        string plain = char.ToUpperInvariant(upper[0]) + upper[1..].ToLowerInvariant();
        string translated = Loc.Tr(plain);
        return translated == plain ? upper : translated.ToUpperInvariant();
    }

    const string MenuCard = "meitou:menu:card", MenuCardHover = "meitou:menu:card-hover", MenuPlate = "meitou:menu:plate",
        MenuPlateHover = "meitou:menu:plate-hover:v2", MenuKey = "meitou:menu:key", MenuVignette = "meitou:menu:vignette";

    /// <summary>
    /// The kit's numbers at 1600×900.
    /// </summary>
    const float MenuLeft = 56, MenuBottom = 36, MenuWidth = 320, MenuButton = 38, MenuGap = 6, MenuExitGap = 4, MenuCardGap = 8, MenuCardHeight = 78,
        MenuShadow = 22;

    /// <summary>
    /// The title's buttons and the catalog messages their captions are (FUN_140918ef0, upper-cased).
    /// </summary>
    internal static readonly (string Widget, string Caption)[] TitleCaptions =
    [
        ("ContinueButton", "Continue"), ("NewGameButton", "New game"), ("LoadGameButton", "Load game"), ("ImportGameButton", "Import game"),
        ("OptionsButton", "Options"), ("CreditsButton", "Credits"), ("ExitButton", "Exit"),
    ];

    static readonly (string Widget, string Caption, UiRect Icon)[] MenuButtons =
    [
        ("NewGameButton", "NEW GAME", new(108, 88, 33, 37)), ("LoadGameButton", "LOAD GAME", new(222, 90, 32, 23)),
        ("ImportGameButton", "IMPORT GAME", new(73, 88, 32, 32)),   // pic_PointerPickupItem
        ("OptionsButton", "OPTIONS", new(146, 88, 34, 37)), ("CreditsButton", "CREDITS", new(49, 0, 44, 36)),
        ("ExitButton", "EXIT", new(202, 126, 30, 36)),
    ];

    static Vector4 Hex(int r, int g, int b, float a = 1) => new(r / 255f, g / 255f, b / 255f, a);
    static readonly Vector4 InkMain = Hex(175, 166, 139), InkLight = Hex(217, 206, 184), InkPaper = Hex(20, 8, 6), InkDanger = Hex(191, 72, 52);

    float _menuArtScale;
    Dictionary<string, (float Progress, long At)>? _menuHover;
    readonly Dictionary<UiRect, UiRect> _menuIconBounds = [];
    (string Folder, DateTime Modified, SaveSlotInfo? Info) _continueInfo;

    /// <summary>
    /// The load's game data and level file problems (<see cref="GameDatabase.Issues"/>, <see cref="WorldLevelData.Issues"/>); listed in log.txt.
    /// </summary>
    public int DataProblems { get; set; }

    /// <summary>
    /// The loaded mods and their problems, for the Settings MODS page; null without game data.
    /// </summary>
    public ModReport? Mods { get; set; }

    internal const string TitleLogoTexture = "meitou:title-logo";

    /// <summary>
    /// Makes the Meitou title's wordmark (<see cref="TitleLogo"/>); asked once, null keeps the plain lettering.
    /// </summary>
    public Func<RgbaImage?>? TitleLogoSource { get; set; }

    Lazy<RgbaImage?>? _titleLogo;

    /// <summary>
    /// The kit's scale for this frame: 1 at 900 pixels high.
    /// </summary>
    float MenuScale => player.Height / 900f;

    float TitleHover(string name, bool hovered)
    {
        var states = _menuHover ??= [];
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        states.TryGetValue(name, out var previous);
        float dt = previous.At == 0 ? 0 : Math.Clamp((float)System.Diagnostics.Stopwatch.GetElapsedTime(previous.At, now).TotalSeconds, 0, 0.05f);
        float progress = Math.Clamp(previous.Progress + dt / (hovered ? 0.16f : -0.16f), 0, 1);
        states[name] = (progress, now);
        return progress * progress * (3 - 2 * progress);
    }

    /// <summary>
    /// Where each title button goes (the card first when there is a save), for the bindings and the drawing alike.
    /// </summary>
    internal Dictionary<string, UiRect> MenuLayout(bool card)
    {
        float s = MenuScale, x = MenuLeft * s, w = MenuWidth * s, y = player.Height - MenuBottom * s - MenuButton * s;
        var at = new Dictionary<string, UiRect>(StringComparer.Ordinal) { ["ExitButton"] = new(x, y, w, MenuButton * s) };
        y -= MenuExitGap * s;
        for (int i = MenuButtons.Length - 2; i >= 0; i--)
        {
            y -= (MenuGap + MenuButton) * s;
            at[MenuButtons[i].Widget] = new(x, y, w, MenuButton * s);
        }

        if (card) at["ContinueButton"] = new(x, y - (MenuCardGap + MenuCardHeight) * s, w, MenuCardHeight * s);
        return at;
    }

    /// <summary>
    /// Replaces the title buttons' looks with the kit's (their bounds and actions stay, invisible) and makes Continue the screen's default, so Enter
    /// continues. Falls back to the plain buttons until the art is baked.
    /// </summary>
    internal void MeitouTitleMenu(List<UiControl> controls)
    {
        if (!MenuArtReady()) return;
        float s = MenuScale;
        var art = new List<UiControl>();
        UiControl Image(string name, UiRect at, string texture, string? rect = null, string? colour = null)
        {
            var p = new Dictionary<string, string> { ["ImageTexture"] = texture };
            if (rect is not null) p["ImageRect"] = rect;
            if (colour is not null) p["Colour"] = colour;
            return new(name, "ImageBox", at, "", "", "", true, p, Vector4.Zero, Pickable: false);
        }
        UiControl Text(string name, UiRect at, string text, float pixels, Vector4 colour, string align = "Left VCenter") =>
            new(name, "TextBox", at, text, "", "", true, new Dictionary<string, string> { ["TextAlign"] = align }, Vector4.Zero, colour,
                Pickable: false, TextScale: pixels * s / 16);

        // The vignette behind the stack (kit: an ellipse 620×470 at 4% across, on the bottom edge).
        var vignette = new UiRect(0, player.Height - 470 * 0.8f * s, player.Width * 0.04f + 620 * 0.8f * s, 470 * 0.8f * s);
        art.Add(Image("MeitouMenuVignette", vignette, MenuVignette));
        for (int i = 0; i < controls.Count; i++)
        {
            var c = controls[i];
            if (c.Type != "Button") continue;
            var b = c.Bounds;
            bool hover = c.Enabled && b.Contains(player.Mouse.X, player.Mouse.Y);
            float hoverAmount = TitleHover(c.Name, hover);
            if (c.Name == "ContinueButton")
            {
                float m = MenuShadow * s;
                var cardBounds = new UiRect(b.X - m, b.Y - m, b.Width + 2 * m, b.Height + 2 * m);
                art.Add(Image("MeitouMenuCard", cardBounds, MenuCard));
                if (hoverAmount > 0) art.Add(Image("MeitouMenuCardHover", cardBounds, MenuCardHover,
                    colour: FormattableString.Invariant($"1 1 1 {hoverAmount}")));
                var (line, meta) = ContinueText();
                art.Add(Text("MeitouMenuContinue", new(b.X + 16 * s, b.Y + 11 * s, b.Width - 80 * s, 26 * s), UpperTr("CONTINUE"), 22, InkPaper));
                art.Add(Image("MeitouMenuKey", new(b.X + b.Width - 60 * s, b.Y + 15 * s, 44 * s, 16 * s), MenuKey));
                art.Add(Text("MeitouMenuKeyText", new(b.X + b.Width - 60 * s, b.Y + 15 * s, 44 * s, 16 * s), "ENTER", 10, InkLight, "Center"));
                art.Add(Text("MeitouMenuSaveLine", new(b.X + 16 * s, b.Y + 37 * s, b.Width - 32 * s, 16 * s), line, 12, InkPaper));
                art.Add(Text("MeitouMenuSaveMeta", new(b.X + 16 * s, b.Y + 52 * s, b.Width - 32 * s, 16 * s), meta, 12, InkPaper with { W = 0.75f }));
                controls[i] = Hollow(c) with { Properties = new Dictionary<string, string>(c.Properties) { ["DefaultAction"] = "true" } };
                continue;
            }
            if (Array.FindIndex(MenuButtons, m => m.Widget == c.Name) is not (>= 0 and var index)) continue;
            var (_, caption, icon) = MenuButtons[index];
            art.Add(Image("MeitouMenuPlate" + index, b, MenuPlate, colour: c.Enabled ? null : "0.7 0.7 0.7 1"));
            if (hoverAmount > 0) art.Add(Image("MeitouMenuPlateHover" + index, b, MenuPlateHover,
                colour: FormattableString.Invariant($"1 1 1 {hoverAmount}")));
            // Use the sprite's tight visible box (stray pixels from neighbouring sprites dropped), fit it into the shared 24×20 slot
            // without upscaling, and centre it on the slot.
            var tight = TightIconBounds(icon);
            float fit = Math.Min(1f, Math.Min(24 / tight.Width, 20 / tight.Height)) * s;
            float iconX = b.X + 24 * s - tight.Width * fit / 2;
            float iconY = b.Y + b.Height / 2 - tight.Height * fit / 2 + 1.0f * s;
            string crop = FormattableString.Invariant($"{tight.X} {tight.Y} {tight.Width} {tight.Height}");
            string iconColour = FormattableString.Invariant($"1 1 1 {0.8f + 0.2f * hoverAmount}");
            art.Add(Image("MeitouMenuIcon" + index, new(iconX, iconY, tight.Width * fit, tight.Height * fit), "Kenshi_UI.png", crop, iconColour));
            var ink = !c.Enabled ? Hex(119, 119, 119) : Vector4.Lerp(InkMain, c.Name == "ExitButton" ? InkDanger : InkLight, hoverAmount);
            var labelBounds = new UiRect(b.X + 48 * s, b.Y, b.Width - 60 * s, b.Height);
            art.Add(Text("MeitouMenuLabel" + index, labelBounds, UpperTr(caption), 14, ink));
            controls[i] = Hollow(c);
        }
        // Drawn first: the buttons themselves are only hit targets now.
        controls.InsertRange(0, art);
    }

    /// <summary>
    /// The title's notice of <see cref="DataProblems"/>, bottom right (a missing mod or a data conflict would otherwise look like a clean install).
    /// </summary>
    internal void AddDataProblems(List<UiControl> controls)
    {
        if (DataProblems == 0) return;

        float s = MenuScale;
        string text = DataProblems == 1 ? "1 game data problem, see log.txt" : $"{DataProblems} game data problems, see log.txt";
        var bounds = new UiRect(player.Width - 420 * s, player.Height - MenuBottom * s - 20 * s, 400 * s, 20 * s);
        controls.Add(new("MeitouDataProblems", "TextBox", bounds, text, "", "", true,
            new Dictionary<string, string> { ["TextAlign"] = "Right VCenter" }, Vector4.Zero, InkDanger, Pickable: false, TextScale: 13 * s / 16));
    }

    /// <summary>
    /// A title button with nothing drawn: no skin, caption or background, the same bounds and action.
    /// </summary>
    static UiControl Hollow(UiControl c) => c with { Skin = "", Caption = "", Background = Vector4.Zero };

    /// <summary>
    /// The tight box of a button's icon in the atlas, cached per crop.
    /// </summary>
    UiRect TightIconBounds(UiRect icon)
    {
        if (_menuIconBounds.TryGetValue(icon, out var cached)) return cached;
        if (player.UiAtlas() is not { } atlas) return icon;

        return _menuIconBounds[icon] = VisibleBounds(atlas, icon);
    }

    /// <summary>
    /// The tight box around an atlas crop's visible sprite: pixels with alpha ≥ 40 grouped into 8-connected blobs, blobs smaller than a tenth of the
    /// biggest dropped (stray pixels from neighbouring sprites), the rest unioned. Falls back to the crop itself.
    /// </summary>
    static UiRect VisibleBounds(RgbaImage atlas, UiRect crop)
    {
        int left = Math.Max(0, (int)MathF.Floor(crop.X)), top = Math.Max(0, (int)MathF.Floor(crop.Y));
        int right = Math.Min(atlas.Width, (int)MathF.Ceiling(crop.X + crop.Width));
        int bottom = Math.Min(atlas.Height, (int)MathF.Ceiling(crop.Y + crop.Height));
        int w = right - left, h = bottom - top;
        if (w <= 0 || h <= 0) return crop;
        bool Solid(int x, int y) => atlas.Pixels[((top + y) * atlas.Width + left + x) * 4 + 3] >= 40;

        var seen = new bool[w * h];
        var blobs = new List<(int Count, int X0, int Y0, int X1, int Y1)>();
        var stack = new Stack<(int X, int Y)>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (seen[y * w + x] || !Solid(x, y)) continue;
                int count = 0, x0 = x, y0 = y, x1 = x, y1 = y;
                seen[y * w + x] = true;
                stack.Push((x, y));
                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    count++;
                    x0 = Math.Min(x0, cx);
                    y0 = Math.Min(y0, cy);
                    x1 = Math.Max(x1, cx);
                    y1 = Math.Max(y1, cy);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h || seen[ny * w + nx] || !Solid(nx, ny)) continue;
                            seen[ny * w + nx] = true;
                            stack.Push((nx, ny));
                        }
                }
                blobs.Add((count, x0, y0, x1, y1));
            }
        if (blobs.Count == 0) return crop;
        int biggest = blobs.Max(b => b.Count);
        var kept = blobs.Where(b => b.Count * 10 >= biggest).ToList();
        int minX = kept.Min(b => b.X0), minY = kept.Min(b => b.Y0), maxX = kept.Max(b => b.X1), maxY = kept.Max(b => b.Y1);
        return new(left + minX, top + minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// The save card's lines, from the newest save: "Save · Day N · Place" and "Squad · c.N · M members" (the squad when it is not the save's name).
    /// </summary>
    (string Line, string Meta) ContinueText()
    {
        if (player.Frontend.Slots.FirstOrDefault() is not { } slot) return ("", "");
        if (_continueInfo.Folder != slot.Folder || _continueInfo.Modified != slot.ModifiedUtc)
        {
            SaveSlotInfo? info = null;
            try
            {
                info = SaveCatalog.ReadInfo(slot);
            }
            catch (Exception e) when (e is IOException or FormatException or ArgumentException)
            {
            }

            _continueInfo = (slot.Folder, slot.ModifiedUtc, info);
        }
        if (_continueInfo.Info is not { } i) return (slot.Name, "");
        var line = string.Join(" · ", new[] { slot.Name, $"Day {i.Day}", i.Area }.Where(p => !string.IsNullOrWhiteSpace(p)));
        string squad = string.Equals(i.Faction, slot.Name, StringComparison.OrdinalIgnoreCase) ? "" : i.Faction;
        string money = $"c.{i.Funds.ToString("N0", CultureInfo.InvariantCulture)} · {i.Members} {(i.Members == 1 ? "member" : "members")}";
        string meta = string.IsNullOrWhiteSpace(squad) ? money : $"{squad} · {money}";
        return (line, meta);
    }

    /// <summary>
    /// Whether the menu's surfaces are on the GPU at this frame's size; bakes and uploads them when not (the atlas is read once).
    /// </summary>
    bool MenuArtReady()
    {
        if (player.UiPainter is not { } painter) return false;

        float s = MenuScale;
        if (_menuArtScale == s && painter.HasTexture(MenuPlateHover)) return true;
        if (player.UiAtlas() is not { } uiAtlas) return false;

        int W(float v) => Math.Max(1, (int)MathF.Round(v * s));

        // GenericTextBoxSkin inside its soft alpha rim and worn corners (kit "paper-inner", cropped further)
        var paper = new UiRect(352, 457, 164, 81);
        painter.SetImage(MenuCard, MenuArt.Card(uiAtlas, paper, W(MenuWidth), W(MenuCardHeight), s, MenuShadow * s, 1), srgb: false);
        painter.SetImage(MenuCardHover, MenuArt.Card(uiAtlas, paper, W(MenuWidth), W(MenuCardHeight), s, MenuShadow * s, 1.1f), srgb: false);

        // Button1Skin
        var plateSkin = new UiRect(612, 766, 118, 45);
        painter.SetImage(MenuPlate, MenuArt.Plate(uiAtlas, plateSkin, W(MenuWidth), W(MenuButton), s, 0.7f), srgb: false);
        painter.SetImage(MenuPlateHover, MenuArt.Plate(uiAtlas, plateSkin, W(MenuWidth), W(MenuButton), s, 0.85f), srgb: false);
        painter.SetImage(MenuKey, MenuArt.Chip(W(44), W(16), 4 * s, Hex(20, 8, 6, 0.6f), null), srgb: false);
        painter.SetImage(MenuVignette, MenuArt.Vignette(256, 160), srgb: false);
        _menuArtScale = s;
        return true;
    }

    #region Title logo

    /// <summary>
    /// Whether the wordmark can be drawn; the first ask makes it and hands it to the painter.
    /// </summary>
    internal bool TitleLogoReady()
    {
        if (player.UiPainter is not { } painter || TitleLogoSource is not { } source) return false;
        if (painter.HasTexture(TitleLogoTexture)) return true;
        _titleLogo ??= new(source);
        if (_titleLogo.Value is not { } image) return false;
        painter.SetImage(TitleLogoTexture, image);
        return true;
    }

    /// <summary>
    /// The wordmark's texture goes while the title is closed (its image stays, so it is uploaded again when the title opens).
    /// </summary>
    internal void ReleaseTitleLogo()
    {
        if (player.UiPainter is { } painter && painter.HasTexture(TitleLogoTexture)) painter.Forget(TitleLogoTexture);
    }

    #endregion
}
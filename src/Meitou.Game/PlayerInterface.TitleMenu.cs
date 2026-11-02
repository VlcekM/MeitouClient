using System.Globalization;
using System.Numerics;
using Meitou.Data.Save;
using Meitou.Data.Textures;
using Meitou.Data.Ui;

namespace Meitou.Game;

/// <summary>
/// The Meitou title's main menu, after the Meitou UI kit (docs/meitou-ui.md, "Layout and states"): a stack 320 wide, 56 from the left and 36
/// from the bottom of a 1600×900 frame, scaled with the height. On top, the save card: worn paper with its dark edge and a drop shadow, the
/// CONTINUE with an ENTER key badge, and the save's name, day, place, squad and money. Below it, plate buttons (the game's
/// Button1Skin at 70% brightness, the pushed skin at 85% under the mouse) with the order-sprite icons. The surfaces are baked once per size
/// from crops of the game's Kenshi_UI.png (rounded corners, edges and shadow included) and drawn as images; the original title's buttons
/// stay in place, invisible, as the hit targets, so input is the faithful screen's.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>
    /// An upper-case button or title (<c>NEW GAME</c>, <c>YES</c>) in the active language: the catalog's message of the same text (<c>CONTINUE</c> is one), else of its
    /// capitalised form (<c>New game</c>) in upper case. Text that is not a message is unchanged.
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

    const string MenuCard = "meitou:menu:card", MenuCardHover = "meitou:menu:card-hover", MenuPlate = "meitou:menu:plate", MenuPlateHover = "meitou:menu:plate-hover:v2",
        MenuKey = "meitou:menu:key", MenuVignette = "meitou:menu:vignette";
    /// <summary>The kit's numbers at 1600×900.</summary>
    const float MenuLeft = 56, MenuBottom = 36, MenuWidth = 320, MenuButton = 38, MenuGap = 6, MenuExitGap = 4, MenuCardGap = 8, MenuCardHeight = 78, MenuShadow = 22;
    /// <summary>The title's buttons and the catalog messages their captions are (FUN_140918ef0, upper-cased).</summary>
    static readonly (string Widget, string Caption)[] TitleCaptions =
    [
        ("ContinueButton", "Continue"), ("NewGameButton", "New game"), ("LoadGameButton", "Load game"), ("ImportGameButton", "Import game"),
        ("OptionsButton", "Options"), ("CreditsButton", "Credits"), ("ExitButton", "Exit"),
    ];
    static readonly (string Widget, string Caption, UiRect Icon)[] MenuButtons =
    [
        ("NewGameButton", "NEW GAME", new(108, 88, 33, 37)), ("LoadGameButton", "LOAD GAME", new(222, 90, 32, 23)),
        ("ImportGameButton", "IMPORT GAME", new(73, 88, 32, 32)),   // pic_PointerPickupItem
        ("OptionsButton", "OPTIONS", new(146, 88, 34, 37)), ("CreditsButton", "CREDITS", new(49, 0, 44, 36)), ("ExitButton", "EXIT", new(202, 126, 30, 36)),
    ];
    static Vector4 Hex(int r, int g, int b, float a = 1) => new(r / 255f, g / 255f, b / 255f, a);
    static readonly Vector4 InkMain = Hex(175, 166, 139), InkLight = Hex(217, 206, 184), InkPaper = Hex(20, 8, 6), InkDanger = Hex(191, 72, 52);

    Func<string, string?>? uiResolve;
    RgbaImage? uiAtlas;
    float menuArtScale;

    /// <summary>The game's Kenshi_UI.png, decoded once and shared by the title menu, tooltips and settings; null when missing or unreadable.</summary>
    internal RgbaImage? UiAtlas()
    {
        if (uiAtlas is not null || uiResolve?.Invoke("Kenshi_UI.png") is not { } path) return uiAtlas;
        try { return uiAtlas = TextureLoader.LoadFile(path, allMips: false).Levels[0]; }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException) { return null; }
    }
    Dictionary<string, (float Progress, long At)>? menuHover;
    readonly Dictionary<UiRect, UiRect> menuIconBounds = [];
    (string Folder, DateTime Modified, SaveSlotInfo? Info) continueInfo;

    /// <summary>The kit's scale for this frame: 1 at 900 pixels high.</summary>
    float MenuScale => Height / 900f;

    float TitleHover(string name, bool hovered)
    {
        var states = menuHover ??= [];
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        states.TryGetValue(name, out var previous);
        float dt = previous.At == 0 ? 0 : Math.Clamp((float)System.Diagnostics.Stopwatch.GetElapsedTime(previous.At, now).TotalSeconds, 0, 0.05f);
        float progress = Math.Clamp(previous.Progress + dt / (hovered ? 0.16f : -0.16f), 0, 1);
        states[name] = (progress, now);
        return progress * progress * (3 - 2 * progress);
    }

    /// <summary>Where each title button goes (the card first when there is a save), for the bindings and the drawing alike.</summary>
    Dictionary<string, UiRect> MenuLayout(bool card)
    {
        float s = MenuScale, x = MenuLeft * s, w = MenuWidth * s, y = Height - MenuBottom * s - MenuButton * s;
        var at = new Dictionary<string, UiRect>(StringComparer.Ordinal) { ["ExitButton"] = new(x, y, w, MenuButton * s) };
        y -= MenuExitGap * s;
        for (int i = MenuButtons.Length - 2; i >= 0; i--) { y -= (MenuGap + MenuButton) * s; at[MenuButtons[i].Widget] = new(x, y, w, MenuButton * s); }
        if (card) at["ContinueButton"] = new(x, y - (MenuCardGap + MenuCardHeight) * s, w, MenuCardHeight * s);
        return at;
    }

    /// <summary>
    /// Replaces the title buttons' looks with the kit's (their bounds and actions stay, invisible) and makes Continue the screen's default, so Enter
    /// continues. Falls back to the plain buttons until the art is baked.
    /// </summary>
    void MeitouTitleMenu(List<UiControl> controls)
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
            new(name, "TextBox", at, text, "", "", true, new Dictionary<string, string> { ["TextAlign"] = align }, Vector4.Zero, colour, Pickable: false, TextScale: pixels * s / 16);
        // The vignette behind the stack (kit: an ellipse 620×470 at 4% across, on the bottom edge).
        art.Add(Image("MeitouMenuVignette", new(0, Height - 470 * 0.8f * s, Width * 0.04f + 620 * 0.8f * s, 470 * 0.8f * s), MenuVignette));
        for (int i = 0; i < controls.Count; i++)
        {
            var c = controls[i];
            if (c.Type != "Button") continue;
            var b = c.Bounds;
            bool hover = c.Enabled && b.Contains(mouse.X, mouse.Y);
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
            var tight = menuIconBounds.TryGetValue(icon, out var cached) ? cached : UiAtlas() is { } atlas ? menuIconBounds[icon] = VisibleBounds(atlas, icon) : icon;
            float fit = Math.Min(1f, Math.Min(24 / tight.Width, 20 / tight.Height)) * s;
            float iconX = b.X + 24 * s - tight.Width * fit / 2;
            float iconY = b.Y + b.Height / 2 - tight.Height * fit / 2 + 1.0f * s;
            art.Add(Image("MeitouMenuIcon" + index, new(iconX, iconY, tight.Width * fit, tight.Height * fit), "Kenshi_UI.png",
                FormattableString.Invariant($"{tight.X} {tight.Y} {tight.Width} {tight.Height}"), FormattableString.Invariant($"1 1 1 {0.8f + 0.2f * hoverAmount}")));
            var ink = !c.Enabled ? Hex(119, 119, 119) : Vector4.Lerp(InkMain, c.Name == "ExitButton" ? InkDanger : InkLight, hoverAmount);
            art.Add(Text("MeitouMenuLabel" + index, new(b.X + 48 * s, b.Y, b.Width - 60 * s, b.Height), UpperTr(caption), 14, ink));
            controls[i] = Hollow(c);
        }
        // Drawn first: the buttons themselves are only hit targets now.
        controls.InsertRange(0, art);
    }

    /// <summary>The load's game data and level file problems (<see cref="GameDatabase.Issues"/>, <see cref="WorldLevelData.Issues"/>); listed in log.txt.</summary>
    public int DataProblems { get; set; }

    /// <summary>The title's notice of <see cref="DataProblems"/>, bottom right (a missing mod or a data conflict would otherwise look like a clean install).</summary>
    void AddDataProblems(List<UiControl> controls)
    {
        if (DataProblems == 0) return;
        float s = MenuScale;
        string text = DataProblems == 1 ? "1 game data problem, see log.txt" : $"{DataProblems} game data problems, see log.txt";
        controls.Add(new("MeitouDataProblems", "TextBox", new(Width - 420 * s, Height - MenuBottom * s - 20 * s, 400 * s, 20 * s), text, "", "", true,
            new Dictionary<string, string> { ["TextAlign"] = "Right VCenter" }, Vector4.Zero, InkDanger, Pickable: false, TextScale: 13 * s / 16));
    }

    /// <summary>A title button with nothing drawn: no skin, caption or background, the same bounds and action.</summary>
    static UiControl Hollow(UiControl c) => c with { Skin = "", Caption = "", Background = Vector4.Zero };

    /// <summary>
    /// The tight box around an atlas crop's visible sprite: pixels with alpha ≥ 40 grouped into 8-connected blobs, blobs smaller than a tenth of the
    /// biggest dropped (stray pixels from neighbouring sprites), the rest unioned. Falls back to the crop itself.
    /// </summary>
    static UiRect VisibleBounds(RgbaImage atlas, UiRect crop)
    {
        int left = Math.Max(0, (int)MathF.Floor(crop.X)), top = Math.Max(0, (int)MathF.Floor(crop.Y));
        int right = Math.Min(atlas.Width, (int)MathF.Ceiling(crop.X + crop.Width)), bottom = Math.Min(atlas.Height, (int)MathF.Ceiling(crop.Y + crop.Height));
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
                    x0 = Math.Min(x0, cx); y0 = Math.Min(y0, cy); x1 = Math.Max(x1, cx); y1 = Math.Max(y1, cy);
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

    /// <summary>The save card's lines, from the newest save: "Save · Day N · Place" and "Squad · c.N · M members" (the squad when it is not the save's name).</summary>
    (string Line, string Meta) ContinueText()
    {
        if (slots.FirstOrDefault() is not { } slot) return ("", "");
        if (continueInfo.Folder != slot.Folder || continueInfo.Modified != slot.ModifiedUtc)
        {
            SaveSlotInfo? info = null;
            try { info = SaveCatalog.ReadInfo(slot); }
            catch (Exception e) when (e is IOException or FormatException or ArgumentException) { }
            continueInfo = (slot.Folder, slot.ModifiedUtc, info);
        }
        if (continueInfo.Info is not { } i) return (slot.Name, "");
        var line = string.Join(" · ", new[] { slot.Name, $"Day {i.Day}", i.Area }.Where(p => !string.IsNullOrWhiteSpace(p)));
        string squad = string.Equals(i.Faction, slot.Name, StringComparison.OrdinalIgnoreCase) ? "" : i.Faction;
        string money = $"c.{i.Funds.ToString("N0", CultureInfo.InvariantCulture)} · {i.Members} {(i.Members == 1 ? "member" : "members")}";
        string meta = string.IsNullOrWhiteSpace(squad) ? money : $"{squad} · {money}";
        return (line, meta);
    }

    /// <summary>Whether the menu's surfaces are on the GPU at this frame's size; bakes and uploads them when not (the atlas is read once).</summary>
    bool MenuArtReady()
    {
        if (uiPainter is not { } painter) return false;
        float s = MenuScale;
        if (menuArtScale == s && painter.HasTexture(MenuPlateHover)) return true;
        if (UiAtlas() is not { } uiAtlas) return false;
        int W(float v) => Math.Max(1, (int)MathF.Round(v * s));
        var paper = new UiRect(352, 457, 164, 81);   // GenericTextBoxSkin inside its soft alpha rim and worn corners (kit "paper-inner", cropped further)
        painter.SetImage(MenuCard, MenuArt.Card(uiAtlas, paper, W(MenuWidth), W(MenuCardHeight), s, MenuShadow * s, 1), srgb: false);
        painter.SetImage(MenuCardHover, MenuArt.Card(uiAtlas, paper, W(MenuWidth), W(MenuCardHeight), s, MenuShadow * s, 1.1f), srgb: false);
        painter.SetImage(MenuPlate, MenuArt.Plate(uiAtlas, new(612, 766, 118, 45), W(MenuWidth), W(MenuButton), s, 0.7f), srgb: false);      // Button1Skin
        painter.SetImage(MenuPlateHover, MenuArt.Plate(uiAtlas, new(612, 766, 118, 45), W(MenuWidth), W(MenuButton), s, 0.85f), srgb: false);
        painter.SetImage(MenuKey, MenuArt.Chip(W(44), W(16), 4 * s, Hex(20, 8, 6, 0.6f), null), srgb: false);
        painter.SetImage(MenuVignette, MenuArt.Vignette(256, 160), srgb: false);
        menuArtScale = s;
        return true;
    }
}

/// <summary>The Meitou menu's surfaces, baked on the CPU as sRGB images with straight alpha (docs/meitou-ui.md, "Surfaces").</summary>
static partial class MenuArt
{
    /// <summary>
    /// The paper card (kit .mt-save): the paper crop stretched whole, brightened by <paramref name="bright"/>, with its edge drawn inside (2px dark line,
    /// 1px faint line, 10px wear), corners rounded to 10, over a drop shadow (6 down, ~14 soft, 55% black) in a margin of <paramref name="margin"/> pixels.
    /// </summary>
    public static RgbaImage Card(RgbaImage atlas, UiRect paper, int width, int height, float s, float margin, float bright)
    {
        int m = (int)MathF.Ceiling(margin), w = width + 2 * m, h = height + 2 * m;
        var image = new RgbaImage(w, h);
        float radius = 10 * s;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float px = x + 0.5f - m, py = y + 0.5f - m;
                float d = RoundedDistance(px, py, width, height, radius);
                // The shadow: the card's shape 6 lower, faded over ~14.
                float shadow = 0.55f * Smooth(1 - Math.Clamp((RoundedDistance(px, py - 6 * s, width, height, radius) + 3 * s) / (14 * s), 0, 1));
                var colour = Vector4.Zero;
                float cover = Math.Clamp(0.5f - d, 0, 1);
                if (cover > 0)
                {
                    var c = Sample(atlas, paper, px / width, py / height) * bright;
                    float inside = -d;
                    c = Mix(c, new(60, 35, 20), 0.35f * Math.Clamp(1 - inside / (10 * s), 0, 1));
                    if (inside < 3 * s) c = Mix(c, new(20, 8, 6), 0.25f);
                    if (inside < 2 * s) c = Mix(c, new(20, 8, 6), 0.9f);
                    colour = new(c, cover);
                }
                Put(image, x, y, Over(colour, new(0, 0, 0, shadow)));
            }
        return image;
    }

    /// <summary>A plate button (kit .mt-btn): the 9-slice skin (6-pixel slices) stretched to size at <paramref name="bright"/> brightness, corners rounded to 7.</summary>
    public static RgbaImage Plate(RgbaImage atlas, UiRect skin, int width, int height, float s, float bright)
    {
        var image = new RgbaImage(width, height);
        float slice = 6 * s, radius = 7 * s;
        static float Map(float p, float size, float slice, float source) =>
            p < slice ? p / slice * 6 : p > size - slice ? source - 6 + (p - (size - slice)) / slice * 6 : 6 + (p - slice) / Math.Max(1, size - 2 * slice) * (source - 12);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float cover = Math.Clamp(0.5f - RoundedDistance(px, py, width, height, radius), 0, 1);
                var sample = SampleAlpha(atlas, skin.X + Map(px, width, slice, skin.Width), skin.Y + Map(py, height, slice, skin.Height));
                Put(image, x, y, new(new Vector3(sample.X, sample.Y, sample.Z) * bright, sample.W * cover));
            }
        return image;
    }

    /// <summary>A flat chip with rounded corners (the save card's key badge), with an optional 1px rim.</summary>
    public static RgbaImage Chip(int width, int height, float radius, Vector4 fill, Vector4? rim)
    {
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float d = RoundedDistance(x + 0.5f, y + 0.5f, width, height, radius);
                var c = rim is { } r && d > -1 ? Over(r, fill) : fill;
                Put(image, x, y, c with { W = c.W * Math.Clamp(0.5f - d, 0, 1) });
            }
        return image;
    }

    /// <summary>
    /// The vignette behind the stack (kit: radial-gradient(ellipse 620px 470px at 4% 100%, .93 at 25%, .6 at 55%, clear at 80%) in #080605), baked for
    /// the area it covers: the ellipse's 80% extent from its centre at the image's bottom left, scaled to the frame by the caller.
    /// </summary>
    public static RgbaImage Vignette(int width, int height)
    {
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                // The image spans 0..0.8 of the ellipse's radii (its left 4% of the frame is folded into the first column).
                float t = MathF.Sqrt(MathF.Pow((x + 0.5f) / width * 0.8f, 2) + MathF.Pow((height - y - 0.5f) / height * 0.8f, 2));
                float a = t < 0.25f ? 0.93f : t < 0.55f ? 0.93f + (0.6f - 0.93f) * (t - 0.25f) / 0.3f : t < 0.8f ? 0.6f * (1 - (t - 0.55f) / 0.25f) : 0;
                Put(image, x, y, new(8 / 255f, 6 / 255f, 5 / 255f, a));
            }
        return image;
    }

    /// <summary>Signed distance from the edge of a <paramref name="w"/>×<paramref name="h"/> rectangle at the origin with corners rounded to <paramref name="r"/> (negative inside).</summary>
    static float RoundedDistance(float x, float y, float w, float h, float r)
    {
        float qx = MathF.Abs(x - w / 2) - (w / 2 - r), qy = MathF.Abs(y - h / 2) - (h / 2 - r);
        return MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0)) + MathF.Min(MathF.Max(qx, qy), 0) - r;
    }

    static float Smooth(float t) => t * t * (3 - 2 * t);
    static Vector3 Mix(Vector3 a, Vector3 b, float t) => a + (b / 255f - a) * t;

    /// <summary>Paper colour (0..1 sRGB) at <paramref name="u"/>, <paramref name="v"/> across a crop, bilinear.</summary>
    static Vector3 Sample(RgbaImage atlas, UiRect crop, float u, float v)
    {
        var c = SampleAlpha(atlas, crop.X + Math.Clamp(u, 0, 1) * crop.Width, crop.Y + Math.Clamp(v, 0, 1) * crop.Height);
        return new(c.X, c.Y, c.Z);
    }

    /// <summary>The atlas at pixel position <paramref name="x"/>, <paramref name="y"/> (0..1 sRGB with alpha), bilinear.</summary>
    static Vector4 SampleAlpha(RgbaImage atlas, float x, float y)
    {
        x -= 0.5f; y -= 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Vector4 At(int px, int py)
        {
            px = Math.Clamp(px, 0, atlas.Width - 1); py = Math.Clamp(py, 0, atlas.Height - 1);
            int i = (py * atlas.Width + px) * 4;
            return new Vector4(atlas.Pixels[i], atlas.Pixels[i + 1], atlas.Pixels[i + 2], atlas.Pixels[i + 3]) / 255f;
        }
        return Vector4.Lerp(Vector4.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Vector4.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
    }

    /// <summary><paramref name="top"/> over <paramref name="under"/>, straight alpha.</summary>
    static Vector4 Over(Vector4 top, Vector4 under)
    {
        float a = top.W + under.W * (1 - top.W);
        if (a <= 0) return Vector4.Zero;
        var rgb = (new Vector3(top.X, top.Y, top.Z) * top.W + new Vector3(under.X, under.Y, under.Z) * under.W * (1 - top.W)) / a;
        return new(rgb, a);
    }

    static void Put(RgbaImage image, int x, int y, Vector4 c)
    {
        int i = (y * image.Width + x) * 4;
        image.Pixels[i] = (byte)Math.Clamp(c.X * 255 + 0.5f, 0, 255);
        image.Pixels[i + 1] = (byte)Math.Clamp(c.Y * 255 + 0.5f, 0, 255);
        image.Pixels[i + 2] = (byte)Math.Clamp(c.Z * 255 + 0.5f, 0, 255);
        image.Pixels[i + 3] = (byte)Math.Clamp(c.W * 255 + 0.5f, 0, 255);
    }
}

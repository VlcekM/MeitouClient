namespace Meitou.Lua;

/// <summary>
/// Text or button HUD item owned by a Lua mod.
/// </summary>
public sealed record LuaHudItem(
    string OwnerMod,
    string Id,
    double X,
    double Y,
    double W,
    double H,
    string Text,
    string Color,
    bool IsButton = false,
    double Alpha = 1.0,
    string Align = "Center VCenter");

/// <summary>
/// Atlas or texture image HUD item owned by a Lua mod.
/// </summary>
public sealed record LuaHudImage(
    string OwnerMod,
    string Id,
    double X,
    double Y,
    double W,
    double H,
    string Texture,
    string Rect,
    string Color,
    double Alpha,
    int Order);

/// <summary>
/// Thread-safe, bounded HUD overlay state owned by Lua mods.
/// Cleaned up automatically whenever a mod stops or faults.
/// </summary>
public sealed class LuaHudState
{
    /// <summary>
    /// Maximum text/button items per mod.
    /// </summary>
    public const int MaxPerMod = 48;

    /// <summary>
    /// Maximum total text items across all mods.
    /// </summary>
    public const int MaxTotalTexts = 64;

    /// <summary>
    /// Maximum total clickable buttons across all mods.
    /// </summary>
    public const int MaxTotalButtons = 8;

    /// <summary>
    /// Maximum atlas/image widgets per mod.
    /// </summary>
    public const int MaxImagesPerMod = 192;

    /// <summary>
    /// Maximum total atlas/image widgets across all mods.
    /// </summary>
    public const int MaxTotalImages = 256;

    /// <summary>
    /// The longest widget id accepted.
    /// </summary>
    const int MaxIdLength = 64;

    /// <summary>
    /// The longest text kept; longer text is cut.
    /// </summary>
    const int MaxTextLength = 160;

    /// <summary>
    /// Guards every collection below.
    /// </summary>
    readonly object _gate = new();

    /// <summary>
    /// The text and button items by owner and id.
    /// </summary>
    readonly Dictionary<string, LuaHudItem> _items = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The image widgets by owner and id.
    /// </summary>
    readonly Dictionary<string, LuaHudImage> _images = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The last draw order given to a new image.
    /// </summary>
    int _imageSeq;

    /// <summary>
    /// Whether any HUD text, button, or image is currently active.
    /// </summary>
    public bool HasAny
    {
        get { lock (_gate) return _items.Count > 0 || _images.Count > 0; }
    }

    /// <summary>
    /// Creates or updates a text overlay slot.
    /// </summary>
    public bool SetText(
        string mod, string id, double x, double y, double w, double h, string text, string color, double alpha = 1.0, string align = "Center VCenter")
        => SetItem(mod, id, x, y, w, h, text, color, isButton: false, alpha, align);

    /// <summary>
    /// Creates or updates a clickable button overlay slot.
    /// </summary>
    public bool SetButton(string mod, string id, double x, double y, double w, double h, string label, string color)
        => SetItem(mod, id, x, y, w, h, label, color, isButton: true, 1.0, "Center VCenter");

    /// <summary>
    /// Creates or updates an atlas/texture image widget.
    /// </summary>
    public bool SetImage(string mod, string id, double x, double y, double w, double h, string texture, string rect, string color, double alpha)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength || string.IsNullOrWhiteSpace(texture)) return false;
        if (!AllFinite(x, y, w, h)) return false;

        x = ClampPosition(x);
        y = ClampPosition(y);
        w = ClampSize(w, 0.001);
        h = ClampSize(h, 0.001);
        alpha = ClampAlpha(alpha);
        color = ColorOrDefault(color, "#FFFFFF");

        string k = Key(mod, id);
        lock (_gate)
        {
            bool replacing = _images.TryGetValue(k, out LuaHudImage? existing);
            if (!replacing && !HasImageRoom(mod)) return false;

            int order = replacing ? existing!.Order : ++_imageSeq;
            _images[k] = new LuaHudImage(mod, id, x, y, w, h, texture, rect ?? "", color, alpha, order);

            return true;
        }
    }

    /// <summary>
    /// Removes a widget by id.
    /// </summary>
    public bool Remove(string mod, string id)
    {
        string k = Key(mod, id);
        lock (_gate)
        {
            bool a = _items.Remove(k);
            bool b = _images.Remove(k);

            return a || b;
        }
    }

    /// <summary>
    /// Removes every widget owned by <paramref name="mod"/>.
    /// </summary>
    public void ClearMod(string mod)
    {
        lock (_gate)
        {
            RemoveOwned(_items, mod);
            RemoveOwned(_images, mod);
        }
    }

    /// <summary>
    /// Returns ordered text items.
    /// </summary>
    public IReadOnlyList<LuaHudItem> TextSnapshot() => ItemSnapshot(isButton: false);

    /// <summary>
    /// Returns ordered clickable button items.
    /// </summary>
    public IReadOnlyList<LuaHudItem> ButtonSnapshot() => ItemSnapshot(isButton: true);

    /// <summary>
    /// Returns ordered atlas/image widgets.
    /// </summary>
    public IReadOnlyList<LuaHudImage> ImageSnapshot()
    {
        lock (_gate) return _images.Values.OrderBy(it => it.Order).ToList();
    }

    /// <summary>
    /// The dictionary key of a widget: its owner and id, lower-cased.
    /// </summary>
    static string Key(string mod, string id) => $"{mod.ToLowerInvariant()}:{id.ToLowerInvariant()}";

    /// <summary>
    /// Removes the entries of <paramref name="widgets"/> owned by <paramref name="mod"/>.
    /// </summary>
    static void RemoveOwned<T>(Dictionary<string, T> widgets, string mod)
    {
        List<string> keys = widgets.Where(kv => OwnerOf(kv.Value).Equals(mod, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
        foreach (string k in keys) widgets.Remove(k);
    }

    /// <summary>
    /// The owning mod of a text, button or image widget.
    /// </summary>
    static string OwnerOf<T>(T widget) => widget switch
    {
        LuaHudItem item => item.OwnerMod,
        LuaHudImage image => image.OwnerMod,
        _ => "",
    };

    /// <summary>
    /// Validates, clamps and stores a text or button item; false when it is invalid or a limit is reached.
    /// </summary>
    bool SetItem(string mod, string id, double x, double y, double w, double h, string text, string color, bool isButton, double alpha, string align)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength) return false;
        if (!AllFinite(x, y, w, h)) return false;

        x = ClampPosition(x);
        y = ClampPosition(y);
        w = ClampSize(w, 0.002);
        h = ClampSize(h, 0.002);
        alpha = ClampAlpha(alpha);
        text = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        color = ColorOrDefault(color, "#F0E6D2");
        if (string.IsNullOrWhiteSpace(align)) align = "Center VCenter";

        string k = Key(mod, id);
        lock (_gate)
        {
            _items.TryGetValue(k, out LuaHudItem? existing);
            if (!HasItemRoom(mod, isButton, existing)) return false;

            _items[k] = new LuaHudItem(mod, id, x, y, w, h, text, color, isButton, alpha, align);

            return true;
        }
    }

    /// <summary>
    /// Whether a text or button item fits the per-mod limit and its kind's total limit; <paramref name="existing"/> is the item it replaces.
    /// </summary>
    bool HasItemRoom(string mod, bool isButton, LuaHudItem? existing)
    {
        bool replacing = existing is not null;
        if (!replacing && _items.Values.Count(it => it.OwnerMod.Equals(mod, StringComparison.OrdinalIgnoreCase)) >= MaxPerMod) return false;

        if (isButton)
        {
            int btnCount = _items.Values.Count(it => it.IsButton) - (replacing && existing!.IsButton ? 1 : 0);
            return btnCount < MaxTotalButtons;
        }

        int txtCount = _items.Values.Count(it => !it.IsButton) - (replacing && !existing!.IsButton ? 1 : 0);

        return txtCount < MaxTotalTexts;
    }

    /// <summary>
    /// Whether a new image fits the total and the per-mod limit.
    /// </summary>
    bool HasImageRoom(string mod)
    {
        if (_images.Count >= MaxTotalImages) return false;

        return _images.Values.Count(it => it.OwnerMod.Equals(mod, StringComparison.OrdinalIgnoreCase)) < MaxImagesPerMod;
    }

    /// <summary>
    /// The text items or the button items, ordered by owner then id.
    /// </summary>
    List<LuaHudItem> ItemSnapshot(bool isButton)
    {
        lock (_gate)
        {
            return _items.Values.Where(it => it.IsButton == isButton)
                .OrderBy(it => it.OwnerMod, StringComparer.OrdinalIgnoreCase)
                .ThenBy(it => it.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Whether all four coordinates are finite numbers.
    /// </summary>
    static bool AllFinite(double x, double y, double w, double h)
        => double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(w) && double.IsFinite(h);

    /// <summary>
    /// Clamps a position to the screen with a quarter of a screen of margin.
    /// </summary>
    static double ClampPosition(double v) => Math.Clamp(v, -0.25, 1.0);

    /// <summary>
    /// Clamps a size to [<paramref name="min"/>, 1].
    /// </summary>
    static double ClampSize(double v, double min) => Math.Clamp(v, min, 1.0);

    /// <summary>
    /// Clamps an alpha to [0, 1]; a non-finite one becomes 1.
    /// </summary>
    static double ClampAlpha(double alpha) => double.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 1.0;

    /// <summary>
    /// The colour when it is <c>#RRGGBB</c>, else <paramref name="fallback"/>.
    /// </summary>
    static string ColorOrDefault(string color, string fallback)
        => string.IsNullOrWhiteSpace(color) || !color.StartsWith('#') || color.Length != 7 ? fallback : color;
}

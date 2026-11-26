namespace Meitou.Lua;

/// <summary>Text or button HUD item owned by a Lua mod.</summary>
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

/// <summary>Atlas or texture image HUD item owned by a Lua mod.</summary>
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
    /// <summary>Maximum text/button items per mod.</summary>
    public const int MaxPerMod = 48;

    /// <summary>Maximum total text items across all mods.</summary>
    public const int MaxTotalTexts = 64;

    /// <summary>Maximum total clickable buttons across all mods.</summary>
    public const int MaxTotalButtons = 8;

    /// <summary>Maximum atlas/image widgets per mod.</summary>
    public const int MaxImagesPerMod = 192;

    /// <summary>Maximum total atlas/image widgets across all mods.</summary>
    public const int MaxTotalImages = 256;

    readonly object _gate = new();
    readonly Dictionary<string, LuaHudItem> _items = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, LuaHudImage> _images = new(StringComparer.OrdinalIgnoreCase);
    int _imageSeq;

    static string Key(string mod, string id) => $"{mod.ToLowerInvariant()}:{id.ToLowerInvariant()}";

    /// <summary>Whether any HUD text, button, or image is currently active.</summary>
    public bool HasAny
    {
        get { lock (_gate) return _items.Count > 0 || _images.Count > 0; }
    }

    /// <summary>Creates or updates a text overlay slot.</summary>
    public bool SetText(string mod, string id, double x, double y, double w, double h, string text, string color, double alpha = 1.0, string align = "Center VCenter") =>
        SetItem(mod, id, x, y, w, h, text, color, isButton: false, alpha, align);

    /// <summary>Creates or updates a clickable button overlay slot.</summary>
    public bool SetButton(string mod, string id, double x, double y, double w, double h, string label, string color) =>
        SetItem(mod, id, x, y, w, h, label, color, isButton: true, 1.0, "Center VCenter");

    bool SetItem(string mod, string id, double x, double y, double w, double h, string text, string color, bool isButton, double alpha, string align)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h)) return false;
        x = Math.Clamp(x, -0.25, 1.0);
        y = Math.Clamp(y, -0.25, 1.0);
        w = Math.Clamp(w, 0.002, 1.0);
        h = Math.Clamp(h, 0.002, 1.0);
        alpha = double.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 1.0;
        text = text.Length > 160 ? text[..160] : text;
        if (string.IsNullOrWhiteSpace(color) || !color.StartsWith('#') || color.Length != 7) color = "#F0E6D2";
        if (string.IsNullOrWhiteSpace(align)) align = "Center VCenter";

        string k = Key(mod, id);
        lock (_gate)
        {
            bool replacing = _items.TryGetValue(k, out LuaHudItem? existing);
            if (!replacing && _items.Values.Count(it => it.OwnerMod.Equals(mod, StringComparison.OrdinalIgnoreCase)) >= MaxPerMod)
            {
                return false;
            }
            if (isButton)
            {
                int btnCount = _items.Values.Count(it => it.IsButton) - (replacing && existing!.IsButton ? 1 : 0);
                if (btnCount >= MaxTotalButtons) return false;
            }
            else
            {
                int txtCount = _items.Values.Count(it => !it.IsButton) - (replacing && !existing!.IsButton ? 1 : 0);
                if (txtCount >= MaxTotalTexts) return false;
            }
            _items[k] = new LuaHudItem(mod, id, x, y, w, h, text, color, isButton, alpha, align);
            return true;
        }
    }

    /// <summary>Creates or updates an atlas/texture image widget.</summary>
    public bool SetImage(string mod, string id, double x, double y, double w, double h, string texture, string rect, string color, double alpha)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || string.IsNullOrWhiteSpace(texture)) return false;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h)) return false;
        x = Math.Clamp(x, -0.25, 1.0);
        y = Math.Clamp(y, -0.25, 1.0);
        w = Math.Clamp(w, 0.001, 1.0);
        h = Math.Clamp(h, 0.001, 1.0);
        alpha = double.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 1.0;
        if (string.IsNullOrWhiteSpace(color) || !color.StartsWith('#') || color.Length != 7) color = "#FFFFFF";

        string k = Key(mod, id);
        lock (_gate)
        {
            bool replacing = _images.TryGetValue(k, out LuaHudImage? existing);
            if (!replacing)
            {
                if (_images.Count >= MaxTotalImages) return false;
                if (_images.Values.Count(it => it.OwnerMod.Equals(mod, StringComparison.OrdinalIgnoreCase)) >= MaxImagesPerMod) return false;
            }
            int order = replacing ? existing!.Order : ++_imageSeq;
            _images[k] = new LuaHudImage(mod, id, x, y, w, h, texture, rect ?? "", color, alpha, order);
            return true;
        }
    }

    /// <summary>Removes a widget by id.</summary>
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

    /// <summary>Removes every widget owned by <paramref name="mod"/>.</summary>
    public void ClearMod(string mod)
    {
        lock (_gate)
        {
            List<string> itemKeys = _items.Where(kv => kv.Value.OwnerMod.Equals(mod, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            foreach (string k in itemKeys) _items.Remove(k);
            List<string> imgKeys = _images.Where(kv => kv.Value.OwnerMod.Equals(mod, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            foreach (string k in imgKeys) _images.Remove(k);
        }
    }

    /// <summary>Returns ordered text items.</summary>
    public IReadOnlyList<LuaHudItem> TextSnapshot()
    {
        lock (_gate)
        {
            return _items.Values.Where(it => !it.IsButton)
                .OrderBy(it => it.OwnerMod, StringComparer.OrdinalIgnoreCase)
                .ThenBy(it => it.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Returns ordered clickable button items.</summary>
    public IReadOnlyList<LuaHudItem> ButtonSnapshot()
    {
        lock (_gate)
        {
            return _items.Values.Where(it => it.IsButton)
                .OrderBy(it => it.OwnerMod, StringComparer.OrdinalIgnoreCase)
                .ThenBy(it => it.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Returns ordered atlas/image widgets.</summary>
    public IReadOnlyList<LuaHudImage> ImageSnapshot()
    {
        lock (_gate) return _images.Values.OrderBy(it => it.Order).ToList();
    }
}

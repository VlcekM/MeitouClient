using System.Globalization;
using System.Numerics;

namespace Meitou.Data.Ui;

public static class MyGuiColours
{
    static IReadOnlyDictionary<string, string> s_palette = new Dictionary<string, string>();

    /// <summary>
    /// The named colours of <c>gui/colours/kenshi_colours.xml</c> (Main, Secondary, Title, Bad, Good, Alert, ...) as "r g b" strings. A mod's file (Dark UI) replaces the game's,
    /// so a text colour the game takes from here must not be a constant. Set once when the UI loads; a name the file lacks gets the game's own value.
    /// </summary>
    public static void LoadPalette(string? path)
    {
        var read = new Dictionary<string, string>();
        if (path is not null && File.Exists(path))
            foreach (var property in MyGuiReader.ReadFile(path).Elements("Property"))
                if (property.Attribute("key")?.Value is { } key && property.Attribute("value")?.Value is { } value && Hex(value) is { } rgb) read[key] = rgb;
        s_palette = read;
    }

    /// <summary>
    /// The colour named <paramref name="key"/> in the palette, else <paramref name="gameHex"/> (the game's own value, as "rrggbb").
    /// </summary>
    public static string Named(string key, string gameHex) => s_palette.GetValueOrDefault(key) ?? Hex(gameHex)!;

    static string? Hex(string value)
    {
        value = value.TrimStart('#');
        if (value.Length != 6) return null;
        var n = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(value.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b)) return null;
            n[i] = b / 255f;
        }
        return string.Join(' ', n.Select(f => f.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The palette's Secondary: the text default (dark ink on the game's parchment, grey in Dark UI).
    /// </summary>
    public static string InkText => Named("Secondary", "140806");
    public static Vector4 Ink => Parse(InkText, default);
    public static Vector4 Parse(string? value, Vector4 fallback)
    {
        if (value is null) return fallback;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4)) return fallback;
        var n = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++) if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || !float.IsFinite(n[i])) return fallback;
        return new(n[0], n[1], n[2], parts.Length == 4 ? n[3] : 1);
    }
    /// <summary>
    /// The text colour a widget made from layout resource <paramref name="template"/> gets, as "r g b": the template's own <c>TextColour</c> property, else the normal-state colour of its skin's text part,
    /// else that of its <c>Client</c> child's template or skin (an EditBox template holds its text in one); a mod such as a Palette UI recolours these together with the window's background.
    /// <paramref name="template"/> may also name a skin. Null when the data names none.
    /// </summary>
    public static string? TemplateText(MyGuiResources resources, string template, int depth = 0)
    {
        if (!resources.Templates.TryGetValue(template, out var resource) || resource.Element("Widget") is not { } root) return SkinText(resources, template);
        var own = root.Elements("Property").FirstOrDefault(p => (string?)p.Attribute("key") == "TextColour")?.Attribute("value")?.Value;
        if (own is not null && Parse(own, default) != default) return own;
        if (SkinText(resources, (string?)root.Attribute("skin") ?? "") is { } fromSkin) return fromSkin;
        if (depth < 3 && root.Elements("Widget").FirstOrDefault(w => (string?)w.Attribute("name") == "Client") is { } client && (string?)client.Attribute("skin") is { Length: > 0 } clientSkin)
            return TemplateText(resources, clientSkin, depth + 1);
        return null;
    }

    static string? SkinText(MyGuiResources resources, string skinName)
    {
        if (!resources.Skins.TryGetValue(skinName, out var skin)) return null;
        string? colour = null;
        foreach (var part in skin.Parts.Where(p => p.Type is "SimpleText" or "EditText"))
            colour = part.StateProperties?.GetValueOrDefault("normal")?.GetValueOrDefault("colour") ?? colour;
        return colour;
    }

    static MyGuiResources? s_inkOwner;
    static readonly Dictionary<string, string?> InkCache = new(StringComparer.Ordinal);
    static readonly Vector4 GameSecondary = Parse(Hex("140806"), default);
    /// <summary>
    /// The ink of the text that sits on <paramref name="template"/> (a template or skin name): <see cref="TemplateText"/> when a skin mod recoloured it (it differs from the game's
    /// Secondary), else the palette's <see cref="InkText"/>, so a mod that changes only the palette (Dark UI) still recolours the text. The lookup is cached per name
    /// (these run every frame), and the cache is dropped when the resources change.
    /// </summary>
    public static string TemplateInk(MyGuiResources resources, string template)
    {
        string? found;
        lock (InkCache)
        {
            if (!ReferenceEquals(s_inkOwner, resources)) { InkCache.Clear(); s_inkOwner = resources; }
            if (!InkCache.TryGetValue(template, out found))
            {
                found = TemplateText(resources, template);
                if (found is not null && Vector3.DistanceSquared(Parse(found, default).AsVector3(), GameSecondary.AsVector3()) < 1e-5f) found = null;
                InkCache[template] = found;
            }
        }
        return found ?? InkText;
    }

    public static Vector4 Text(MyGuiResources resources, UiControl control, string state, Vector4 fallback)
    {
        // A text-only button (no background to tint) shows its hover colour on the text: dialogue choices, research and crafting lists.
        if (state == "highlighted" && control.HoverColour is { } hover && control.Background.W <= 0) return hover;
        var colour = control.TextColour ?? fallback;
        if (control.TextColour is null && resources.Skins.TryGetValue(control.Skin, out var skin))
            foreach (var part in skin.Parts.Where(p => p.Type is "SimpleText" or "EditText"))
            {
                var properties = part.StateProperties?.GetValueOrDefault(state) ?? part.StateProperties?.GetValueOrDefault("normal");
                colour = Parse(properties?.GetValueOrDefault("colour"), colour);
            }
        return Parse(control.Properties.GetValueOrDefault("TextColour"), colour);
    }
}
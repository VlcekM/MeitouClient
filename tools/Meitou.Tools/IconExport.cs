using System.Text.Json;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;

/// <summary>
/// Writes the inventory icon of every item to a folder (docs/game/ui-screens.md "Item icons"): an item the game draws from its mesh is drawn headless
/// by the character renderer and saved as the game saves its pictures, <c>&lt;item&gt;.&lt;material&gt;[.&lt;colour&gt;].png</c>; any other item's
/// <c>icon</c> file is written as <c>&lt;item&gt;.png</c>. <c>icons.json</c> lists each item with its file.
/// </summary>
static class IconExport
{
    /// <summary>
    /// The record types an inventory holds.
    /// </summary>
    static readonly FcsRecordType[] ItemTypes =
    [
        FcsRecordType.Weapon, FcsRecordType.Armour, FcsRecordType.Item, FcsRecordType.Container, FcsRecordType.NestItem,
        FcsRecordType.MapItem, FcsRecordType.Crossbow, FcsRecordType.LimbReplacement,
    ];

    /// <summary>
    /// Frames without a new icon after which the run stops waiting (the renderer gives up on missing textures after 300).
    /// </summary>
    const int StallFrames = 400;

    /// <summary>
    /// An item's icon file (null: none) and, for a weapon, one per manufacturer's model (<see cref="Variant"/>).
    /// </summary>
    sealed record Entry(string StringId, string Name, string Type, string? File, bool Complete, IReadOnlyList<Variant>? Variants = null);

    /// <summary>
    /// A weapon drawn in one manufacturer's model (a MATERIAL_SPECS_WEAPON), as the game names the picture.
    /// </summary>
    sealed record Variant(string Manufacturer, string Model, string ModelName, string File, bool Complete);

    /// <summary>
    /// An icon to draw, and the manufacturer whose model it is drawn in ("" for an item that is not a weapon).
    /// </summary>
    sealed record Wanted(IconKey Key, string Manufacturer);

    /// <summary>
    /// <c>meitou-tools icons &lt;folder&gt; [--enhanced] [--mods]</c>: writes every item's icon and <c>icons.json</c> into the folder (the base
    /// game's data, or the load order with <c>--mods</c>). Returns the exit code.
    /// </summary>
    public static int Run(GameInstall install, string[] args)
    {
        if (args is not [var output, ..] || output.StartsWith("--", StringComparison.Ordinal)) return Usage();
        bool enhanced = args.Contains("--enhanced");
        bool mods = args.Contains("--mods");
        Directory.CreateDirectory(output);

        var db = GameDatabase.Load(mods ? LoadOrder.FromInstall(install) : LoadOrder.BaseGame(install));
        var assets = new AssetLocator(install);
        var items = ItemTypes.SelectMany(db.OfType).OrderBy(r => r.StringId, StringComparer.Ordinal).ToList();
        var drawn = items.Where(r => ItemIcons.Renders(r) && ItemIconStyle.MeshOf(r).Length > 0).ToDictionary(r => r.StringId, r => WantedOf(db, r));

        var entries = new List<Entry>();
        foreach (var item in items.Where(r => !drawn.ContainsKey(r.StringId)))
            entries.Add(CopyStaticIcon(item, assets, output));
        var complete = Draw(install, db, assets, [.. drawn.Values.SelectMany(w => w).Select(w => w.Key)], enhanced, output);
        foreach (var (id, wanted) in drawn) entries.Add(DrawnEntry(db.Find(id)!, wanted, complete, db, output));

        WriteManifest(output, entries);
        Report(entries);
        return 0;
    }

    /// <summary>
    /// Prints the command line and returns 2.
    /// </summary>
    static int Usage()
    {
        Console.Error.WriteLine("meitou-tools icons <folder> [--enhanced] [--mods]");
        return 2;
    }

    /// <summary>
    /// The icons to draw of <paramref name="item"/>: a weapon in each model of each manufacturer that makes it (the game names its pictures by the
    /// model), any other item in its likeliest material and its own colour, as a new item has.
    /// </summary>
    static List<Wanted> WantedOf(GameDatabase db, GameRecord item)
    {
        if (item.Type == FcsRecordType.Weapon)
        {
            var models = WeaponModels(db, item);
            if (models.Count > 0) return models;
        }

        var material = ItemIcons.MaterialOf(db, item, "");
        string colour = ItemIcons.ColourOf(db, item, material)?.StringId ?? "";
        return [new(new IconKey(item.StringId, material?.StringId ?? "", colour), "")];
    }

    /// <summary>
    /// Each model of each WEAPON_MANUFACTURER whose <c>weapon types</c> list <paramref name="weapon"/>, once.
    /// </summary>
    static List<Wanted> WeaponModels(GameDatabase db, GameRecord weapon)
    {
        var wanted = new List<Wanted>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var maker in db.OfType(FcsRecordType.WeaponManufacturer).OrderBy(r => r.StringId, StringComparer.Ordinal))
        {
            if (!maker.GetReferences("weapon types").Any(r => r.TargetStringId == weapon.StringId)) continue;
            foreach (var model in maker.GetReferences("weapon models"))
            {
                if (db.Find(model.TargetStringId) is { Type: FcsRecordType.MaterialSpecsWeapon } && seen.Add(model.TargetStringId))
                    wanted.Add(new(new IconKey(weapon.StringId, model.TargetStringId), maker.Name));
            }
        }
        return wanted;
    }

    /// <summary>
    /// The manifest entry of a drawn item: its first picture, and for a weapon every model's.
    /// </summary>
    static Entry DrawnEntry(GameRecord item, List<Wanted> wanted, HashSet<IconKey> complete, GameDatabase db, string output)
    {
        var files = wanted.Where(w => File.Exists(Path.Combine(output, FileName(w.Key)))).ToList();
        var first = files.FirstOrDefault();
        var variants = item.Type == FcsRecordType.Weapon && files.Count > 0
            ? files.Select(ToVariant).ToList()
            : null;
        bool firstComplete = first is not null && complete.Contains(first.Key);
        return new(item.StringId, item.Name, item.Type.ToString(), first is null ? null : FileName(first.Key), firstComplete, variants);

        Variant ToVariant(Wanted w) =>
            new(w.Manufacturer, w.Key.Material, db.Find(w.Key.Material)?.Name ?? "", FileName(w.Key), complete.Contains(w.Key));
    }

    /// <summary>
    /// The game's name for a saved picture of <paramref name="key"/>.
    /// </summary>
    static string FileName(IconKey key) =>
        string.Join('.', new[] { key.Record, key.Material, key.Colour }.Where(part => part.Length > 0)) + ".png";

    /// <summary>
    /// Writes the PNG an item's <c>icon</c> field names (found in the <c>Icons</c> resource group, as the game looks it up) as
    /// <c>&lt;item&gt;.png</c>.
    /// </summary>
    static Entry CopyStaticIcon(GameRecord item, AssetLocator assets, string output)
    {
        string icon = ItemIcons.StaticIconOf(item);
        string? path = icon.Length > 0 ? assets.FindInGroup(icon, "Icons") : null;
        string? file = null;
        if (path is not null)
        {
            var image = TextureLoader.LoadImage(File.ReadAllBytes(path));
            file = item.StringId + ".png";
            PngWriter.Write(Path.Combine(output, file), image.Width, image.Height, image.Pixels);
        }
        return new(item.StringId, item.Name, item.Type.ToString(), file, path is not null);
    }

    /// <summary>
    /// Draws the icons of <paramref name="keys"/> headless and writes each as soon as it is complete; once no icon has completed for
    /// <see cref="StallFrames"/> frames, writes the ones shown after giving up on their textures. The keys drawn with all their textures resident.
    /// </summary>
    static HashSet<IconKey> Draw(GameInstall install, GameDatabase db, AssetLocator assets, IReadOnlyList<IconKey> keys, bool enhanced, string output)
    {
        using var device = VulkanDevice.Create(new VulkanDeviceOptions { Validation = false });
        using var ctx = new GpuContext(device);
        using var characters = new CharacterRenderer(ctx, install, db, assets) { LoadBudget = 0, EnhancedIcons = enhanced };
        var written = new HashSet<IconKey>();
        for (int stalled = 0; written.Count < keys.Count && stalled < StallFrames;)
        {
            ctx.BeginFrame();
            foreach (var key in keys) characters.TryGetIcon(key, out _);
            characters.Update(default);
            characters.DrawIcons();
            ctx.Finish();

            int before = written.Count;
            Write(ctx, characters, keys.Where(characters.IconComplete), written, output);
            stalled = written.Count > before ? 0 : stalled + 1;
            Console.Error.Write($"{(char)13}{written.Count}/{keys.Count} icons");
        }
        Write(ctx, characters, keys, written, output);
        Console.Error.WriteLine($"{(char)13}{written.Count}/{keys.Count} icons");
        return [.. keys.Where(characters.IconComplete)];
    }

    /// <summary>
    /// Writes each of <paramref name="keys"/> that has a picture and is not written yet.
    /// </summary>
    static void Write(GpuContext ctx, CharacterRenderer characters, IEnumerable<IconKey> keys, HashSet<IconKey> written, string output)
    {
        foreach (var key in keys)
        {
            if (written.Contains(key) || !characters.TryGetIcon(key, out var icon)) continue;
            var (w, h) = (icon.Texture.Desc.Width, icon.Texture.Desc.Height);
            PngWriter.Write(Path.Combine(output, FileName(key)), w, h, FlipRows(ctx.ReadBack(icon.Texture, 4), w, h));
            written.Add(key);
        }
    }

    /// <summary>
    /// The renderer's targets hold rows bottom first; a PNG top first.
    /// </summary>
    static byte[] FlipRows(byte[] pixels, int width, int height)
    {
        var flipped = new byte[pixels.Length];
        int row = width * 4;
        for (int y = 0; y < height; y++) pixels.AsSpan((height - 1 - y) * row, row).CopyTo(flipped.AsSpan(y * row));
        return flipped;
    }

    /// <summary>
    /// Writes <c>icons.json</c>: every entry, indented, camel-case names.
    /// </summary>
    static void WriteManifest(string output, List<Entry> entries)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(output, "icons.json"),
                JsonSerializer.Serialize(entries.OrderBy(e => e.StringId, StringComparer.Ordinal), options));
    }

    /// <summary>
    /// Counts, and the items without an icon or drawn without all their textures.
    /// </summary>
    static void Report(List<Entry> entries)
    {
        Console.WriteLine($"{entries.Count(e => e.File is not null)} of {entries.Count} items have an icon.");
        foreach (var e in entries.Where(e => e.File is null))
            Console.WriteLine($"  no icon:   {e.StringId} {e.Name} ({e.Type})");
        foreach (var e in entries.Where(e => e.File is not null && !e.Complete))
            Console.WriteLine($"  partial:   {e.StringId} {e.Name} ({e.Type}): drawn without all its textures");
    }
}

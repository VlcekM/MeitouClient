using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// What an inventory cell shows of an item (<see cref="ItemIcons.Choose"/>): a PNG file, a picture to draw from the item's mesh, or nothing.
/// </summary>
public enum ItemIconSource
{
    /// <summary>
    /// No icon.
    /// </summary>
    None,
    /// <summary>
    /// A PNG file (<see cref="ItemIconChoice.File"/>).
    /// </summary>
    File,
    /// <summary>
    /// A picture drawn from the item's mesh (<see cref="ItemIconStyle"/>).
    /// </summary>
    Render
}

/// <summary>
/// <see cref="Colour"/>: the COLOR_DATA string id a drawn icon is dyed with ("" undyed).
/// </summary>
public readonly record struct ItemIconChoice(ItemIconSource Source, string? File = null, string Colour = "");

/// <summary>
/// Where an inventory item's icon comes from (docs/game/ui-screens.md, "Item icons"). The game draws the icon from the item's mesh for a WEAPON or a
/// record with <c>auto icon image</c> (<see cref="Renders"/>), saving it under <c>data/icons/</c> (<see cref="ItemIconCache"/>); any other record
/// shows the PNG its <c>icon</c> field names (<see cref="StaticIconOf"/>), else <c>default_icon.png</c>. <see cref="Choose"/> follows that; the
/// picture to draw is described by <see cref="ItemIconStyle"/>. <paramref name="resolve"/> finds a file by its bare name in the <c>Icons</c> resource
/// group alone, as the game asks (a General <c>water.png</c> is not Water's icon).
/// </summary>
public sealed class ItemIcons(GameDatabase database, Func<string, string?> resolve, ItemIconCache cache)
{
    /// <summary>
    /// The database the item records are found in.
    /// </summary>
    public GameDatabase Database => database;

    /// <summary>
    /// The icon file a record names (an install-relative path such as <c>.\data\items\icons\Fuel.png</c>), or "".
    /// </summary>
    public static string StaticIconOf(GameRecord record) => record.GetPath("icon");

    /// <summary>
    /// Whether the game draws this record's icon from its mesh (FUN_140847bf0, <b>Verified</b>): a WEAPON always, else the record's <c>auto icon
    /// image</c> (fcs.def default: true for CROSSBOW, ITEM, NEST_ITEM and WEAPON, false for ARMOUR, CONTAINER, LIMB_REPLACEMENT and MAP_ITEM; the
    /// base game's records all store it).
    /// </summary>
    public static bool Renders(GameRecord record) =>
        record.Type == FcsRecordType.Weapon
            || record.GetBool("auto icon image", record.Type is FcsRecordType.Item or FcsRecordType.Crossbow or FcsRecordType.NestItem);

    /// <summary>
    /// The icon of an item of record <paramref name="record"/> made of <paramref name="material"/> (the instance's <c>material sid</c>, "" for none)
    /// dyed with <paramref name="colour"/> (its <c>color sid</c>; "" for none, or no instance: the record's own). A drawn kind takes the game's saved
    /// picture when there is one, else <see cref="ItemIconSource.Render"/> (when the record has a mesh to draw); the others their <c>icon</c> file,
    /// else <c>default_icon.png</c>.
    /// </summary>
    public ItemIconChoice Choose(string record, string material, string colour = "")
    {
        if (database.Find(record) is not { } r) return default;
        if (Renders(r))
        {
            var spec = MaterialOf(database, r, material);
            string dye = ColourOf(database, r, spec, colour)?.StringId ?? "";
            // An item with no material of its own is drawn with its first; Meitou's saved pictures are named by that one.
            if (cache.Find(record, material.Length > 0 ? material : spec?.StringId ?? "", dye) is { } saved)
                return new(ItemIconSource.File, saved);
            return ItemIconStyle.MeshOf(r).Length > 0 ? new(ItemIconSource.Render, Colour: dye) : default;
        }
        if (StaticIconOf(r) is { Length: > 0 } icon && resolve(icon) is { } file) return new(ItemIconSource.File, file);
        return resolve("default_icon.png") is { } fallback ? new(ItemIconSource.File, fallback) : default;
    }

    /// <summary>
    /// The material record an icon is drawn with: <paramref name="materialId"/> (the instance's), else the item's first (likeliest)
    /// <c>material</c> reference.
    /// </summary>
    public static GameRecord? MaterialOf(GameDatabase db, GameRecord item, string materialId)
    {
        static bool IsSpec(GameRecord? r) =>
            r is { Type: FcsRecordType.MaterialSpecsClothing or FcsRecordType.MaterialSpec or FcsRecordType.MaterialSpecsWeapon };
        if (materialId.Length > 0 && db.Find(materialId) is { } chosen && IsSpec(chosen)) return chosen;
        return item.GetReferences("material").OrderByDescending(r => r.Values.Value0).Select(r => db.Find(r.TargetStringId)).FirstOrDefault(IsSpec);
    }

    /// <summary>
    /// The COLOR_DATA an icon is dyed with: the instance's colour <paramref name="colourId"/> (empty: the record's own <c>color</c>, what a new item
    /// has), when the item's <c>dont colorise</c> is false and <paramref name="material"/> has a <c>color map</c> (the game's rule).
    /// </summary>
    public static GameRecord? ColourOf(GameDatabase db, GameRecord item, GameRecord? material, string colourId = "") =>
        material?.GetPath("color map") is { Length: > 0 } && !item.GetBool("dont colorise")
            ? colourId.Length > 0 ? db.Find(colourId) is { Type: FcsRecordType.ColorData } own ? own : null : ItemColours.Of(db, item)
            : null;
}

/// <summary>
/// How the game draws an item's icon (FUN_1408473d0 and FUN_140846060, <b>Verified</b> against the decompiled code, not yet against the picture): a
/// perspective camera on the +Z axis looking at the origin, the item's node moved by <see cref="OffsetH"/> / <see cref="OffsetV"/>, then turned by
/// pitch, yaw - 90 and roll about its own axes, in that order.
/// </summary>
public sealed record ItemIconStyle(
    string Mesh, float Zoom, float OffsetH, float OffsetV, float Pitch, float Yaw, float Roll, Vector3 Scale, int Width, int Height)
{
    /// <summary>
    /// Pixels of the picture per inventory cell along each side (the game asks for 2 x 25).
    /// </summary>
    public const int PixelsPerCell = 50;

    /// <summary>
    /// The vertical field of view of the icon camera, in degrees.
    /// </summary>
    public const float FieldOfView = 45;

    /// <summary>
    /// The camera's distance from the origin per unit of <c>icon zoom</c>.
    /// </summary>
    public const float DistancePerZoom = 5;

    /// <summary>
    /// The camera's distance from the origin.
    /// </summary>
    public float Distance => Zoom * DistancePerZoom;

    /// <summary>
    /// The width over the height the camera is set to (the footprint's).
    /// </summary>
    public float Aspect => (float)Width / Height;

    /// <summary>
    /// The size the image is made at: each side rounded up to a power of two (the original's textures; the saved files are 256 x 256 for a 5 x 4 bag,
    /// 512 x 64 for 7 x 1).
    /// </summary>
    public (int Width, int Height) TextureSize => (PowerOfTwo(Width), PowerOfTwo(Height));

    /// <summary>
    /// <paramref name="n"/> rounded up to a power of two (at least 1).
    /// </summary>
    static int PowerOfTwo(int n) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(n, 1));

    /// <summary>
    /// The mesh field the icon is drawn from: a weapon's <c>bare sword</c> (its sheath is left out), else <c>mesh</c>; "" when the record names none.
    /// </summary>
    public static string MeshOf(GameRecord item) => item.GetPath(item.Type == FcsRecordType.Weapon ? "bare sword" : "mesh");

    /// <summary>
    /// The style of <paramref name="item"/>, with <paramref name="material"/> (its material record, for a weapon's scale) when known. A WEAPON is
    /// turned by the fixed -90, -90, 90 and scaled by its <c>scale thickness</c>, <c>scale length</c>, <c>scale width</c> and <c>overall scale</c>
    /// times the material's; the others by their <c>icon offset pitch / yaw / roll</c>. Fields a record leaves out take fcs.def's defaults (zoom 2.7
    /// and vertical offset -8, 1.0 and 0 for a weapon or crossbow; yaw 90; horizontal offset -4 for a weapon).
    /// </summary>
    public static ItemIconStyle Of(GameRecord item, GameRecord? material = null)
    {
        bool weapon = item.Type == FcsRecordType.Weapon, crossbow = item.Type == FcsRecordType.Crossbow;
        float zoom = item.GetFloat("icon zoom", weapon || crossbow ? 1 : 2.7f);
        float v = item.GetFloat("icon offset V", weapon || crossbow ? 0 : -8);
        float h = item.GetFloat("icon offset H", weapon ? -4 : 0);
        var scale = Vector3.One;
        if (weapon)
        {
            scale = ScaleOf(item);
            if (material is not null) scale *= ScaleOf(material);
        }
        var (pitch, yaw, roll) = weapon
            ? (-90f, -90f, 90f)
            : (item.GetFloat("icon offset pitch"), item.GetFloat("icon offset yaw", 90), item.GetFloat("icon offset roll"));
        int width = Math.Max(item.GetInt("inventory footprint width", 1), 1), height = Math.Max(item.GetInt("inventory footprint height", 1), 1);
        return new(MeshOf(item), zoom, h, v, pitch, yaw, roll, scale, width * PixelsPerCell, height * PixelsPerCell);
    }

    /// <summary>
    /// (thickness, length, width) times the overall scale, x y z of the node's scale.
    /// </summary>
    static Vector3 ScaleOf(GameRecord r) =>
        new Vector3(r.GetFloat("scale thickness", 1), r.GetFloat("scale length", 1), r.GetFloat("scale width", 1)) * r.GetFloat("overall scale", 1);

    /// <summary>
    /// The node's transform and the camera's view and projection for the picture, as the original sets them: the node at (H, V, 0) with the turns in
    /// its own axes, pitch first (<c>M = scale * Rz(roll) * Ry(yaw - 90) * Rx(pitch) * translation</c> in row-vector form), the camera at (0, 0,
    /// distance) looking down -Z.
    /// </summary>
    public (Matrix4x4 Model, Matrix4x4 View, Matrix4x4 Projection) Matrices()
    {
        float rad = MathF.PI / 180;
        var model = Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateRotationZ(Roll * rad) * Matrix4x4.CreateRotationY((Yaw - 90) * rad)
            * Matrix4x4.CreateRotationX(Pitch * rad) * Matrix4x4.CreateTranslation(OffsetH, OffsetV, 0);
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, Distance), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView * rad, Aspect, 0.5f, 300);
        return (model, view, projection);
    }
}

/// <summary>
/// The icons the game rendered and saved under <c>data/icons/</c> (the cache is made on the player's machine, so it holds only items they have seen).
/// A file name is <c>&lt;item string id&gt;.&lt;material string id&gt;[.&lt;colour string id&gt;].png</c>, a string id being
/// <c>&lt;number&gt;-&lt;file&gt;</c>.
/// </summary>
public sealed class ItemIconCache
{
    /// <summary>
    /// The cached PNGs by item string id: each file's material, colour and path, in directory order.
    /// </summary>
    readonly Dictionary<string, List<(string Material, string Colour, string Path)>> _byItem = new(StringComparer.Ordinal);

    /// <summary>
    /// Indexes the PNGs of <paramref name="directories"/> (one that does not exist adds none); for the same ids the first directory's file is found.
    /// </summary>
    public ItemIconCache(params string[] directories)
    {
        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.EnumerateFiles(directory, "*.png"))
            {
                if (Parse(Path.GetFileNameWithoutExtension(file)) is not var (item, material, colour)) continue;
                if (!_byItem.TryGetValue(item, out var list)) _byItem[item] = list = [];
                list.Add((material, colour, file));
            }
        }
    }

    /// <summary>
    /// Splits a cache file name (without <c>.png</c>) into item, material and colour string ids; null when it is not of that form (the colour is ""
    /// when absent).
    /// </summary>
    public static (string Item, string Material, string Colour)? Parse(string name)
    {
        var ids = new List<string>(3);
        int start = 0;
        // A string id is "<number>-<file>" and the file may hold dots ("gamedata.base"), so the ids are cut where a number starts after a dot.
        for (int i = 1; i < name.Length; i++)
        {
            if (name[i - 1] != '.' || !char.IsAsciiDigit(name[i])) continue;
            int dash = i;
            while (dash < name.Length && char.IsAsciiDigit(name[dash])) dash++;
            if (dash >= name.Length || name[dash] != '-') continue;
            ids.Add(name[start..(i - 1)]);
            start = i;
        }
        ids.Add(name[start..]);
        return ids.Count is 2 or 3 && ids.All(IsStringId) ? (ids[0], ids[1], ids.Count == 3 ? ids[2] : "") : null;
    }

    /// <summary>
    /// Whether <paramref name="id"/> has the <c>&lt;number&gt;-&lt;file&gt;</c> form: digits, a dash, then at least one character.
    /// </summary>
    static bool IsStringId(string id)
    {
        int dash = id.IndexOf('-');
        return dash > 0 && dash < id.Length - 1 && id[..dash].All(char.IsAsciiDigit);
    }

    /// <summary>
    /// The cached icon of <paramref name="item"/> in <paramref name="material"/> dyed <paramref name="colour"/> ("" undyed): the file of exactly
    /// those ids; null when none. A picture of another material or colour would show another texture, and the renderer draws the exact one.
    /// </summary>
    public string? Find(string item, string material, string colour = "") =>
        _byItem.TryGetValue(item, out var list) ? list.FirstOrDefault(f => f.Material == material && f.Colour == colour).Path : null;
}
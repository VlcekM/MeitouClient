namespace Meitou.Data.Textures;

/// <summary>
/// The game's <c>texture resolution gimping</c> setting (docs/formats/settings.md, "Graphics: textures"): how many top mip
/// levels of block-compressed DDS textures are dropped as they are loaded, and the texture memory budget that goes with it.
/// The level is process-wide and read when a texture is loaded, so it is set once before the world loads (the game needs a
/// restart for it too).
/// </summary>
public static class TextureQuality
{
    /// <summary>The value when the key is missing (High).</summary>
    public const int Default = 1;
    public const int Maximum = 4;

    /// <summary>The level (0 Maximum, 1 High, 2 Medium, 3 Low, 4 Fugly). The tools and tests that do not set it keep 0: nothing dropped.</summary>
    public static int Level { get; set; }

    /// <summary>The options' labels, indexed by level.</summary>
    public static readonly string[] Labels = ["Maximum", "High", "Medium", "Low", "Fugly"];

    /// <summary>
    /// Mip levels dropped from a texture called <paramref name="name"/> in resource group <paramref name="group"/> at <paramref name="level"/>
    /// (the game's resource-loading listener, FUN_14082ddc0): none for names without <c>dds</c> or in the groups <c>Overlaymaps</c>,
    /// <c>GUI</c>, <c>PLSM2</c> or an empty group; otherwise the level, one fewer for <c>_LO.</c>, and, while still above 0, one fewer for
    /// a name without <c>_HI.</c> outside the group <c>Landscape</c>. Never below 0 (the exe's handling of a negative count is not traced:
    /// at Maximum nothing is touched).
    /// </summary>
    public static int LevelsToDrop(string name, string group, int level)
    {
        if (level <= 0) return 0;
        if (group.Length == 0 || group is "Overlaymaps" or "GUI" or "PLSM2") return 0;
        if (!name.Contains("dds", StringComparison.OrdinalIgnoreCase)) return 0;
        int n = level;
        if (name.Contains("_LO.", StringComparison.Ordinal)) n--;
        if (n > 0 && !name.Contains("_HI.", StringComparison.Ordinal) && group != "Landscape") n--;
        return Math.Max(n, 0);
    }

    /// <summary><see cref="LevelsToDrop(string, string, int)"/> at the current <see cref="Level"/>.</summary>
    public static int LevelsToDrop(string name, string group) => LevelsToDrop(name, group, Level);

    /// <summary>
    /// The TextureManager's memory budget in MiB (FUN_140816d90): 2 GiB at Maximum, 1.5 GiB at High, 1 GiB below. (The game sets it on
    /// the one manager; the viewer's texture caches each apply it to themselves.)
    /// </summary>
    public static int MemoryBudgetMb(int level) => level switch { 0 => 2048, 1 => 1536, _ => 1024 };

    /// <summary>
    /// The file after the game's in-memory DDS rewrite (FUN_14082d840): <paramref name="levels"/> top mip levels removed from a DXT
    /// texture (FourCC <c>DXT*</c>; <c>DX10</c> headers, ATI1/ATI2, uncompressed, cube maps and volumes are left alone). It stops early
    /// when, before a drop, fewer than 2 mips remain or a side is already below 5. The surfaces are re-sliced, not copied.
    /// </summary>
    public static DdsFile DropTopMips(DdsFile dds, int levels)
    {
        if (levels <= 0 || dds.FourCC is not { } fourCC || !fourCC.StartsWith("DXT", StringComparison.Ordinal) || dds.DxgiFormat is not null
            || dds.IsCubemap || dds.IsVolume || dds.ImageCount != 1 || dds.Surfaces.Count < dds.MipCount) return dds;
        int drop = 0, w = dds.Width, h = dds.Height, mips = dds.MipCount;
        while (drop < levels && mips >= 2 && w >= 5 && h >= 5)
        {
            drop++;
            mips--;
            w = Math.Max(w / 2, 1);
            h = Math.Max(h / 2, 1);
        }
        if (drop == 0) return dds;
        var surfaces = new List<DdsSurface>(mips);
        for (int i = drop; i < dds.MipCount; i++)
            surfaces.Add(dds.Surfaces[i] with { Level = i - drop });
        return new DdsFile
        {
            Data = dds.Data, Width = w, Height = h, Depth = 1, MipCount = mips, ImageCount = 1, IsCubemap = false, IsVolume = false,
            Format = dds.Format, FourCC = dds.FourCC, DxgiFormat = null, Masks = dds.Masks, HeaderFlags = dds.HeaderFlags,
            HeaderSize = dds.HeaderSize, Surfaces = surfaces,
        };
    }
}

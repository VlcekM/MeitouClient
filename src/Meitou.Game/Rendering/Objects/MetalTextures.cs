namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The <c>objmetal</c> switch (Meitou, optional): the building textures that are plainly sheet metal, named by hand. The game's data marks almost
/// none of its buildings as metal (docs/characters.md "metal_map"), and a rule on colour alone would take grey concrete and stone for it.
/// </summary>
static class MetalTextures
{
    /// <summary>
    /// The building textures named sheet metal (file names).
    /// </summary>
    static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "dirtplates-rusty_dif.dds", "redpaintrust.dds", "metal0059.dds", "diamondplate2.dds", "metalstep.dds", "storagetank01_diffuse.dds",
        "storagetank01_diffuse_hi.dds", "metalfloorsrusted0045_l.dds", "metal14.dds", "metal20.dds", "metal_wall-house_dif_hi.dds",
        "bench machinery_diff_hi.dds",
    };

    /// <summary>
    /// Whether the switch is on: the named textures are drawn as metal.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// Whether <paramref name="diffuse"/> (a path or a file name) is one of them.
    /// </summary>
    public static bool Holds(string? diffuse) => diffuse is not null && Names.Contains(Path.GetFileName(diffuse.Replace('\\', '/')));
}

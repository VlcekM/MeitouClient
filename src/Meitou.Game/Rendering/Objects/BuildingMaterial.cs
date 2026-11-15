using System.Numerics;
using Meitou.Data;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The look of a building part from the MATERIAL_SPEC the game gives it (docs/formats/runtime-materials.md: StaticObject
/// template, <c>material type</c> BuildingShader DEFAULT / ALPHA / FOLIAGE / DUAL / EMISSIVE, threshold = <c>alpha threshold</c> / 255).
/// </summary>
static class BuildingMaterial
{
    /// <summary>
    /// The material of a building part's MATERIAL_SPEC record (the class summary's rules).
    /// </summary>
    public static SurfaceMaterial FromSpec(GameRecord spec)
    {
        int type = spec.GetInt("material type");
        bool alpha = type is 1 or 2;
        return new SurfaceMaterial
        {
            Description = $"{spec.Type} '{spec.Name}' (building material, " +
                $"type {type switch { 0 => "DEFAULT", 1 => "ALPHA", 2 => "FOLIAGE", 3 => "DUAL", 4 => "EMISSIVE", _ => type.ToString() }})",
            Diffuse = Tex(spec, "texture map") ?? "white.dds",
            Normal = Tex(spec, "normal map"),
            Diffuse2 = type == 3 ? Tex(spec, "texture map 2") : null,
            Normal2 = type == 3 ? Tex(spec, "normal map 2") : null,
            Metal = Tex(spec, "metalness map"),
            Alpha = alpha ? AlphaSource.NormalAlpha : AlphaSource.None,
            AlphaThreshold = alpha ? spec.GetInt("alpha threshold", 128) / 255f : 0,
            DoubleSided = type == 2,
            Emissive = type == 4,
            Tile = new Vector2(spec.GetFloat("tile X", 1), spec.GetFloat("tile Y", 1)) is var t && t.X != 0 && t.Y != 0 ? t : Vector2.One,
            SpecularMult = spec.GetFloat("specular mult", 1),
            VertexColours = true,
        };
    }

    /// <summary>
    /// The material of a farm's plants (docs/game/buildings-production.md "Farm plants": the <c>FarmPlants</c> template, flags 0, fragment program
    /// with TRANSPARENCY and COLOURING): the spec's three maps, cut out at <c>alpha threshold</c> / 255 by the normal map's alpha and drawn from both
    /// sides, whatever its <c>material type</c>; no second set, tiling, dust or emission. COLOURING takes the plant's tint, not the mesh's vertex
    /// colours. The dust is left out by <c>WorldObjectRenderer.MakeMaterials</c>.
    /// </summary>
    public static SurfaceMaterial FromFarmSpec(GameRecord spec) => new()
    {
        Description = $"{spec.Type} '{spec.Name}' (farm plants)",
        Diffuse = Tex(spec, "texture map") ?? "white.dds",
        Normal = Tex(spec, "normal map"),
        Metal = Tex(spec, "metalness map"),
        Alpha = AlphaSource.NormalAlpha,
        AlphaThreshold = spec.GetInt("alpha threshold", 128) / 255f,
        DoubleSided = true,
        SpecularMult = spec.GetFloat("specular mult", 1),
    };

    /// <summary>
    /// A record's path field, or null when empty.
    /// </summary>
    static string? Tex(GameRecord record, string field) => record.GetPath(field) is { Length: > 0 } p ? p : null;
}
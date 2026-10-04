using System.Numerics;
using Meitou.Data;

namespace Meitou.ModelViewer;

/// <summary>
/// The look of a building part from the MATERIAL_SPEC the game gives it (docs/formats/runtime-materials.md: StaticObject
/// template, <c>material type</c> BuildingShader DEFAULT / ALPHA / FOLIAGE / DUAL / EMISSIVE, threshold = <c>alpha threshold</c> / 255).
/// </summary>
static class BuildingMaterial
{
    public static SurfaceMaterial FromSpec(GameRecord spec)
    {
        int type = spec.GetInt("material type");
        bool alpha = type is 1 or 2;
        return new SurfaceMaterial
        {
            Description = $"{spec.Type} '{spec.Name}' (building material, type {type switch { 0 => "DEFAULT", 1 => "ALPHA", 2 => "FOLIAGE", 3 => "DUAL", 4 => "EMISSIVE", _ => type.ToString() }})",
            Diffuse = Tex(spec, "texture map") ?? "white.dds",
            Normal = Tex(spec, "normal map"),
            Diffuse2 = type == 3 ? Tex(spec, "texture map 2") : null,
            Normal2 = type == 3 ? Tex(spec, "normal map 2") : null,
            Alpha = alpha ? AlphaSource.NormalAlpha : AlphaSource.None,
            AlphaThreshold = alpha ? spec.GetInt("alpha threshold", 128) / 255f : 0,
            DoubleSided = type == 2,
            Emissive = type == 4,
            Tile = new Vector2(spec.GetFloat("tile X", 1), spec.GetFloat("tile Y", 1)) is var t && t.X != 0 && t.Y != 0 ? t : Vector2.One,
            SpecularMult = spec.GetFloat("specular mult", 1),
            VertexColours = true,
        };
    }

    static string? Tex(GameRecord record, string field) => record.GetPath(field) is { Length: > 0 } p ? p : null;
}

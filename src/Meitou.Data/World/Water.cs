using System.Numerics;
using Meitou.Content;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// How the game places and parameterises water (docs/formats/terrain.md, "Water"): one level for the whole world,
/// a 4608-unit plane per loaded zone near the camera and a 500000-unit distant plane, both shaded with the
/// parameters of the biomes under them.
/// </summary>
public static class WorldWater
{
    /// <summary>World height of the water surface everywhere (Verified, kenshi_x64.exe: set once when water is created).</summary>
    public const float Height = 100f;

    /// <summary>Edge length of the per-zone water planes (one zone) and of the distant water plane.</summary>
    public const float ZonePlaneSize = WorldLayout.ZoneSize;
    public const float DistantPlaneSize = 500000f;

    /// <summary>Within this distance the near (per-zone, alpha-blended) water is drawn; beyond it the opaque distant water.</summary>
    public const float NearDistance = 4000f;

    /// <summary>World maps under <c>data/newland/land/overlaymaps/</c> that the water material samples over the whole world.</summary>
    public const string ColourMap = "newland/land/overlaymaps/watercolourmap.png";
    public const string FlowMap = "newland/land/overlaymaps/flowmap.png";
}

/// <summary>
/// A BIOMES record's water parameters, converted the way the game hands them to the water shader (Verified,
/// kenshi_x64.exe water material builder; defaults from fcs.def).
/// </summary>
public sealed record BiomeWater
{
    public required string StringId { get; init; }
    public required string Name { get; init; }
    /// <summary>Biome map colour (<c>index</c>), RGB.</summary>
    public required uint Index { get; init; }
    /// <summary><c>water color</c> (RGB 0..1). The game does not hand this to the shader: it is baked into <c>watercolourmap.png</c>.</summary>
    public required Vector3 Colour { get; init; }
    /// <summary>Shader <c>scale</c>: (<c>water scale X</c>, <c>Y</c>, <c>scum scale X</c>, <c>Y</c>) / 5000, texture repeats per world unit.</summary>
    public required Vector4 Scale { get; init; }
    /// <summary>Shader <c>invStrength</c>: 1 / max(<c>water strength</c>, 0.0001); divides the normal map's up component.</summary>
    public required float InvStrength { get; init; }
    /// <summary>Shader <c>invOpacity</c>: 1 / max(<c>water visibility</c> × 10, 1).</summary>
    public required float InvOpacity { get; init; }
    public required float Gloss { get; init; }
    public required float Glow { get; init; }
    /// <summary>Shader <c>distortion</c>: (<c>water distortion</c>, <c>scum distortion</c> / mean scum scale).</summary>
    public required Vector2 Distortion { get; init; }
    /// <summary>Texture paths as stored (null when empty): normal map, scum diffuse and normal, turbulence map.</summary>
    public string? NormalMap { get; init; }
    public string? ScumMap { get; init; }
    public string? ScumNormal { get; init; }
    public string? TurbulenceMap { get; init; }

    /// <summary>The game's divisor for the texture scales (the same 5000 units as the terrain's tiling).</summary>
    public const float ScaleUnit = 5000f;

    public static BiomeWater FromRecord(GameRecord r)
    {
        static string? Tex(GameRecord r, string field) => r.GetPath(field) is { Length: > 0 } p ? p : null;
        uint c = (uint)r.GetInt("water color", 0x002040) & 0xFFFFFF;
        float scumX = r.GetFloat("scum scale X", 20), scumY = r.GetFloat("scum scale Y", 20);
        float meanScum = (scumX + scumY) * 0.5f;
        return new BiomeWater
        {
            StringId = r.StringId,
            Name = r.Name,
            Index = (uint)r.GetInt("index") & 0xFFFFFF,
            Colour = new Vector3((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF) / 255f,
            Scale = new Vector4(r.GetFloat("water scale X", 20), r.GetFloat("water scale Y", 20), scumX, scumY) / ScaleUnit,
            InvStrength = 1f / Math.Max(r.GetFloat("water strength", 1), 1e-4f),
            InvOpacity = 1f / Math.Max(r.GetFloat("water visibility", 10) * 10f, 1f),
            Gloss = r.GetFloat("water gloss", 0.9f),
            Glow = r.GetFloat("water glow", 0),
            Distortion = new Vector2(r.GetFloat("water distortion", 120), meanScum > 0 ? r.GetFloat("scum distortion", 1) / meanScum : 0),
            NormalMap = Tex(r, "water normal"),
            ScumMap = Tex(r, "texture scum"),
            ScumNormal = Tex(r, "texture scum normal"),
            TurbulenceMap = Tex(r, "turbulence map"),
        };
    }

    /// <summary>All BIOMES records' water by <see cref="Index"/> colour (the first record by id wins if two share a colour).</summary>
    public static Dictionary<uint, BiomeWater> ByIndex(GameDatabase db)
    {
        var result = new Dictionary<uint, BiomeWater>();
        foreach (var r in db.OfType(FcsRecordType.BIOMES).OrderBy(r => r.StringId, StringComparer.Ordinal))
            result.TryAdd((uint)r.GetInt("index") & 0xFFFFFF, FromRecord(r));
        return result;
    }

    public static string ColourMapPath(GameInstall install) => Path.Combine(install.DataDirectory, WorldWater.ColourMap);
}

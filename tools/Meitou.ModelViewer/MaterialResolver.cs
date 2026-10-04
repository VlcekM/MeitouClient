using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;

namespace Meitou.ModelViewer;

/// <summary>Where a surface's alpha comes from. Values follow the fcs.def descriptions (docs/viewer.md).</summary>
public enum AlphaSource { None, DiffuseAlpha, NormalAlpha, DiffuseChannel }

/// <summary>How the viewer shades one submesh: texture files plus the few material switches Kenshi's records carry.</summary>
public sealed record SurfaceMaterial
{
    /// <summary>Where this came from, e.g. <c>MATERIAL_SPEC 'wallbasic01' via BUILDING_PART 'basic wall gate A'</c>.</summary>
    public required string Description { get; init; }
    public string? Diffuse { get; init; }
    public string? Normal { get; init; }
    /// <summary>Second texture set, blended in by vertex alpha (BuildingShader.DUAL, MapFeatureMode.DUAL_*).</summary>
    public string? Diffuse2 { get; init; }
    public string? Normal2 { get; init; }
    public AlphaSource Alpha { get; init; }
    /// <summary>Alpha test threshold 0–1 (0 = no test).</summary>
    public float AlphaThreshold { get; init; }
    /// <summary>For <see cref="AlphaSource.DiffuseChannel"/> and channel-packed hair: channel indices 0–3 (R, G, B, A).</summary>
    public int AlphaChannel { get; init; } = 3;
    public int GreyChannel { get; init; } = -1;
    public Vector3 Tint { get; init; } = Vector3.One;
    public bool DoubleSided { get; init; }
    public bool Emissive { get; init; }
    /// <summary>World-space planar projection instead of UVs (MapFeatureMode.TRIPLANAR / DUAL_TRIPLANAR).</summary>
    public bool Triplanar { get; init; }
    public Vector2 Tile { get; init; } = Vector2.One;
    public float SpecularMult { get; init; } = 1;
    /// <summary>Multiply the diffuse by the vertex colour when the mesh has one (the COLOURING define; docs/formats/runtime-materials.md).</summary>
    public bool VertexColours { get; init; }
    /// <summary>Character head textures, sampled at uv + (0, 1): the body mesh keeps head UVs in v -1..0 (Kenshi character.hlsl).</summary>
    public string? HeadDiffuse { get; init; }
    public string? HeadNormal { get; init; }
    /// <summary>Clamp to a transparent black border instead of repeating (Kenshi's Character material units).</summary>
    public bool BorderAddressing { get; init; }
}

/// <summary>
/// Finds textures for a mesh's submeshes (best effort; the rules and evidence are in docs/viewer.md):
/// FCS records that name the mesh and carry textures (directly, or via MATERIAL_SPEC* records), then the
/// submesh's script material when it names real texture files.
/// </summary>
public sealed class MaterialResolver
{
    readonly GameDatabase? db;
    readonly OgreMaterialLibrary? materials;
    readonly AssetLocator assets;
    readonly Dictionary<string, List<(GameRecord Record, string Field)>> meshUsers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<GameRecord>> referencedBy = new(StringComparer.Ordinal);

    /// <summary>Placeholder textures the script materials name for units Kenshi fills at run time.</summary>
    public static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "black.dds", "flat.dds", "white.dds", "grey.dds", "gray.dds", "blank.dds", "blask.dds",
    };

    public MaterialResolver(GameDatabase? db, OgreMaterialLibrary? materials, AssetLocator assets)
    {
        this.db = db;
        this.materials = materials;
        this.assets = assets;
        if (db is null) return;
        foreach (var record in db.Records.Values)
        {
            foreach (var (key, value) in record.Filenames)
                if (value.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase))
                    Users(Path.GetFileName(value.Replace('\\', '/'))).Add((record, key));
            foreach (var list in record.ReferenceLists)
                foreach (var r in record.GetReferences(list))
                {
                    if (!referencedBy.TryGetValue(r.TargetStringId, out var by)) referencedBy[r.TargetStringId] = by = [];
                    by.Add(record);
                }
        }
    }

    List<(GameRecord, string)> Users(string mesh)
    {
        if (!meshUsers.TryGetValue(mesh, out var list)) meshUsers[mesh] = list = [];
        return list;
    }

    /// <summary>FCS records whose filename fields name <paramref name="meshFile"/> (bare name, case-insensitive).</summary>
    public IReadOnlyList<(GameRecord Record, string Field)> RecordsUsing(string meshFile) =>
        meshUsers.TryGetValue(Path.GetFileName(meshFile), out var list) ? list : [];

    /// <summary>
    /// Candidate materials for a submesh, best first. FCS-derived candidates apply to the whole mesh (a record gives one
    /// texture set; several material references are alternatives Kenshi picks from at random).
    /// </summary>
    public List<SurfaceMaterial> Candidates(string meshFile, OgreSubMesh subMesh)
    {
        var result = new List<SurfaceMaterial>();
        foreach (var (record, field) in RecordsUsing(meshFile))
            result.AddRange(FromRecord(record, field));
        if (FromScript(subMesh.MaterialName) is { } script) result.Add(script);

        // Drop candidates whose diffuse texture doesn't exist, and repeats.
        return result.Where(m => m.Diffuse is null || assets.Find(m.Diffuse) is not null)
            .DistinctBy(m => (m.Diffuse?.ToLowerInvariant(), m.Normal?.ToLowerInvariant(), m.Diffuse2?.ToLowerInvariant(), m.Alpha))
            .ToList();
    }

    IEnumerable<SurfaceMaterial> FromRecord(GameRecord record, string field)
    {
        string via = $"{record.Type} '{record.Name}' .{field}";
        bool female = field.EndsWith(" female", StringComparison.Ordinal) || field.StartsWith("female ", StringComparison.Ordinal);
        switch (record.Type)
        {
            case FcsRecordType.FOLIAGE_MESH when field == "leaves mesh":
                yield return new SurfaceMaterial
                {
                    Description = via + " (leaves texture)", Diffuse = Tex(record, "leaves texture"), Normal = Tex(record, "leaves normal"),
                    Alpha = AlphaSource.NormalAlpha, AlphaThreshold = record.GetInt("leaves alpha threshold", 80) / 255f, DoubleSided = true,
                };
                yield break;
            case FcsRecordType.FOLIAGE_MESH:
                yield return MapFeature(record, via, record.GetInt("material type"), record.GetInt("alpha threshold", 128));
                yield break;
            case FcsRecordType.MAP_FEATURES:
                yield return MapFeature(record, via, record.GetInt("texture mode"), record.GetInt("alpha threshold"));
                yield break;
            case FcsRecordType.WILDLIFE_BIRDS:
                yield return new SurfaceMaterial { Description = via + " (texture)", Diffuse = Tex(record, "texture") };
                yield break;
            case FcsRecordType.ATTACHMENT:
                // Hair: grey diffuse and alpha are packed into channels of one texture (fcs.def "hair ... channel").
                yield return new SurfaceMaterial
                {
                    Description = via + " (texture map, channel-packed hair)", Diffuse = Tex(record, female ? "texture map female" : "texture map"),
                    Alpha = AlphaSource.DiffuseChannel, AlphaChannel = record.GetInt("hair alpha channel"), GreyChannel = record.GetInt("hair diffuse channel"),
                    AlphaThreshold = record.GetInt("alpha rejection", 128) / 255f, Tint = new Vector3(0.35f, 0.25f, 0.17f), DoubleSided = true,
                };
                yield break;
            case FcsRecordType.RACE:
            {
                // The first head of the race (heads are picked per character; the first is as good as any).
                var head = record.GetReferences(female ? "heads female" : "heads male").Select(r => db!.Find(r.TargetStringId)).FirstOrDefault(h => h is not null);
                yield return new SurfaceMaterial
                {
                    Description = via + " (body texture" + (head is null ? ")" : $", head '{head.Name}')"),
                    Diffuse = Tex(record, female ? "body texture female" : "body texture male"),
                    Normal = Tex(record, female ? "nm female" : "nm male"),
                    HeadDiffuse = head is null ? null : Tex(head, "texture map"), HeadNormal = head is null ? null : Tex(head, "normal map"),
                    BorderAddressing = true,
                };
                yield break;
            }
            case FcsRecordType.WEAPON:
                foreach (var m in WeaponModels(record)) yield return m with { Description = $"{m.Description} via {via}" };
                yield break;
        }

        if (Tex(record, "texture map") is { } own)
            yield return new SurfaceMaterial { Description = via + " (texture map)", Diffuse = own, Normal = Tex(record, "normal map") };

        var materialRefs = record.GetReferences("material");
        if (materialRefs.Count == 0 && record.Type == FcsRecordType.BUILDING_PART)
        {
            // Parts without their own material: use the material of the buildings (or parts) listing them.
            foreach (var parent in referencedBy.GetValueOrDefault(record.StringId) ?? [])
                if (parent.GetReferences("parts").Any(r => r.TargetStringId == record.StringId))
                    foreach (var m in Expand(parent.GetReferences("material"), 0))
                        yield return m with { Description = $"{m.Description} via {parent.Type} '{parent.Name}' (parent of {via})" };
            yield break;
        }
        foreach (var m in Expand(materialRefs, 0))
            yield return m with { Description = $"{m.Description} via {via}" };
    }

    SurfaceMaterial MapFeature(GameRecord record, string via, int mode, int alphaThreshold)
    {
        // MapFeatureMode { UV_MAPPED, TRIPLANAR, TERRAIN, DUAL_TEXTURE, FOLIAGE, DUAL_TRIPLANAR, EMISSIVE }
        bool dual = mode is 3 or 5;
        return new SurfaceMaterial
        {
            Description = $"{via} (texture map, mode {MapFeatureModeName(mode)})",
            Diffuse = Tex(record, "texture map"), Normal = Tex(record, "normal map"),
            Diffuse2 = dual ? Tex(record, "texture map 2") : null, Normal2 = dual ? Tex(record, "normal map 2") : null,
            Triplanar = mode is 1 or 5,
            Alpha = mode == 4 && alphaThreshold > 0 ? AlphaSource.NormalAlpha : AlphaSource.None, // the cut-out mask is the normal map's alpha
            AlphaThreshold = mode == 4 ? alphaThreshold / 255f : 0,
            DoubleSided = mode == 4,
            Emissive = mode == 6,
            VertexColours = true,
            Tile = new Vector2(record.GetFloat("tile X", 1), record.GetFloat("tile Y", 1)),
        };
    }

    public static string MapFeatureModeName(int mode) => mode switch
    {
        0 => "UV_MAPPED", 1 => "TRIPLANAR", 2 => "TERRAIN", 3 => "DUAL_TEXTURE", 4 => "FOLIAGE", 5 => "DUAL_TRIPLANAR", 6 => "EMISSIVE", _ => mode.ToString(),
    };

    IEnumerable<SurfaceMaterial> WeaponModels(GameRecord weapon)
    {
        // WEAPON_MANUFACTURER lists "weapon types" (WEAPON) and "weapon models" (MATERIAL_SPECS_WEAPON); a weapon's look is
        // one of its manufacturers' models. Highest chance first.
        var models = new List<(GameReference Ref, GameRecord Maker)>();
        foreach (var maker in referencedBy.GetValueOrDefault(weapon.StringId) ?? [])
            if (maker.Type == FcsRecordType.WEAPON_MANUFACTURER && maker.GetReferences("weapon types").Any(r => r.TargetStringId == weapon.StringId))
                models.AddRange(maker.GetReferences("weapon models").Select(r => (r, maker)));
        foreach (var (r, maker) in models.OrderByDescending(m => m.Ref.Values.Value1))
            if (db!.Find(r.TargetStringId) is { } spec)
                foreach (var m in Leaf(spec))
                    yield return m with { Description = $"{m.Description} (model of {maker.Name})" };
    }

    IEnumerable<SurfaceMaterial> Expand(IReadOnlyList<GameReference> refs, int depth)
    {
        if (db is null || depth > 4) yield break;
        // Collections pick at random with val1 as probability (fcs.def); list the likeliest first.
        foreach (var r in refs.OrderByDescending(r => r.Values.Value1 == 0 ? 100 : r.Values.Value1))
            if (db.Find(r.TargetStringId) is { } target)
            {
                var nested = target.GetReferences("material");
                if (target.Type is FcsRecordType.MATERIAL_SPECS_COLLECTION || target.Type == FcsRecordType.MATERIAL_SPEC && nested.Count > 0)
                {
                    foreach (var m in Expand(nested, depth + 1)) yield return m with { Description = $"{m.Description} in collection '{target.Name}'" };
                }
                else
                    foreach (var m in Leaf(target)) yield return m;
            }
    }

    IEnumerable<SurfaceMaterial> Leaf(GameRecord spec)
    {
        string name = $"{spec.Type} '{spec.Name}'";
        switch (spec.Type)
        {
            case FcsRecordType.MATERIAL_SPEC:
            {
                // BuildingShader { DEFAULT, ALPHA, FOLIAGE, DUAL, EMISSIVE }
                int type = spec.GetInt("material type");
                yield return new SurfaceMaterial
                {
                    Description = $"{name} (type {type switch { 0 => "DEFAULT", 1 => "ALPHA", 2 => "FOLIAGE", 3 => "DUAL", 4 => "EMISSIVE", _ => type.ToString() }})",
                    Diffuse = Tex(spec, "texture map"), Normal = Tex(spec, "normal map"),
                    Diffuse2 = type == 3 ? Tex(spec, "texture map 2") : null, Normal2 = type == 3 ? Tex(spec, "normal map 2") : null,
                    Alpha = type is 1 or 2 ? AlphaSource.NormalAlpha : AlphaSource.None,
                    AlphaThreshold = type is 1 or 2 ? 0.5f : 0, DoubleSided = type == 2, Emissive = type == 4,
                    Tile = new Vector2(spec.GetFloat("tile X", 1), spec.GetFloat("tile Y", 1)),
                    SpecularMult = spec.GetFloat("specular mult", 1),
                    VertexColours = true,
                };
                break;
            }
            case FcsRecordType.MATERIAL_SPECS_CLOTHING:
            {
                // ItemShader { DEFAULT, ALPHA, DOUBLE_SIDED }; which channel ALPHA uses is not documented (docs/viewer.md).
                int type = spec.GetInt("material type");
                yield return new SurfaceMaterial
                {
                    Description = $"{name} (type {type switch { 0 => "DEFAULT", 1 => "ALPHA", 2 => "DOUBLE_SIDED", _ => type.ToString() }})",
                    Diffuse = Tex(spec, "texture map"), Normal = Tex(spec, "normal map"),
                    Alpha = type == 1 ? AlphaSource.NormalAlpha : AlphaSource.None, AlphaThreshold = type == 1 ? 0.5f : 0,
                    DoubleSided = type is 1 or 2, SpecularMult = spec.GetFloat("specular mult", 1),
                };
                break;
            }
            case FcsRecordType.MATERIAL_SPECS_WEAPON:
                yield return new SurfaceMaterial
                {
                    Description = name, Diffuse = Tex(spec, "texture map"), Normal = Tex(spec, "normal map"),
                    SpecularMult = spec.GetFloat("specular mult", 1),
                };
                break;
        }
    }

    /// <summary>A script material's first pass, if it names a real texture (not a placeholder Kenshi replaces).</summary>
    SurfaceMaterial? FromScript(string materialName)
    {
        if (materials?.Find(materialName) is not { } material) return null;
        var pass = material.Techniques.FirstOrDefault(t => t.Scheme == "Default")?.Passes.FirstOrDefault()
            ?? material.Techniques.FirstOrDefault()?.Passes.FirstOrDefault();
        if (pass is null) return null;
        var units = pass.TextureUnits.Where(u => u.Texture is { } t && !Placeholders.Contains(t) && assets.Find(t) is not null).ToList();
        if (units.Count == 0) return null;
        var diffuse = units.FirstOrDefault(u => u.Alias.Contains("diffuse", StringComparison.OrdinalIgnoreCase)) ?? units[0];
        var normal = units.FirstOrDefault(u => u != diffuse && u.Alias.Contains("normal", StringComparison.OrdinalIgnoreCase));
        var blend = pass.SceneBlend;
        return new SurfaceMaterial
        {
            Description = $"script material '{material.Name}' ({material.File})",
            Diffuse = diffuse.Texture, Normal = normal?.Texture,
            Alpha = pass.AlphaRejection.Function != OgreCompareFunction.AlwaysPass || !blend.IsOpaque ? AlphaSource.DiffuseAlpha : AlphaSource.None,
            AlphaThreshold = pass.AlphaRejection.Function != OgreCompareFunction.AlwaysPass ? Math.Max(pass.AlphaRejection.Value, (byte)1) / 255f : !blend.IsOpaque ? 0.5f : 0,
            DoubleSided = pass.CullHardware == OgreCullHardware.None,
        };
    }

    static string? Tex(GameRecord record, string field) => record.GetPath(field) is { Length: > 0 } p ? p : null;
}

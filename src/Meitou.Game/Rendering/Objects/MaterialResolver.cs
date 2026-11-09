using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// Where a surface's alpha comes from. Values follow the fcs.def descriptions (docs/viewer.md).
/// </summary>
public enum AlphaSource
{
    /// <summary>
    /// Opaque: no alpha test.
    /// </summary>
    None,

    /// <summary>
    /// The diffuse map's alpha (script materials with alpha rejection or blending).
    /// </summary>
    DiffuseAlpha,

    /// <summary>
    /// The normal map's alpha (ALPHA and FOLIAGE materials, foliage leaves).
    /// </summary>
    NormalAlpha,

    /// <summary>
    /// A channel of the diffuse map (<see cref="SurfaceMaterial.AlphaChannel"/>; channel-packed hair).
    /// </summary>
    DiffuseChannel
}

/// <summary>
/// How the viewer shades one submesh: texture files plus the few material switches Kenshi's records carry.
/// </summary>
public sealed record SurfaceMaterial
{
    /// <summary>
    /// Where this came from, e.g. <c>MATERIAL_SPEC 'wallbasic01' via BUILDING_PART 'basic wall gate A'</c>.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// The diffuse texture file (null: none).
    /// </summary>
    public string? Diffuse { get; init; }

    /// <summary>
    /// The normal map file (null: none).
    /// </summary>
    public string? Normal { get; init; }

    /// <summary>
    /// Second texture set, blended in by vertex alpha (BuildingShader.DUAL, MapFeatureMode.DUAL_*).
    /// </summary>
    public string? Diffuse2 { get; init; }

    /// <summary>
    /// The second set's normal map file.
    /// </summary>
    public string? Normal2 { get; init; }

    /// <summary>
    /// Where the surface's alpha comes from (<see cref="AlphaSource.None"/>: opaque).
    /// </summary>
    public AlphaSource Alpha { get; init; }

    /// <summary>
    /// Alpha test threshold 0–1 (0 = no test).
    /// </summary>
    public float AlphaThreshold { get; init; }

    /// <summary>
    /// For <see cref="AlphaSource.DiffuseChannel"/> and channel-packed hair: channel indices 0–3 (R, G, B, A).
    /// </summary>
    public int AlphaChannel { get; init; } = 3;

    /// <summary>
    /// The texture channel holding the grey diffuse (channel-packed hair, 0-3); -1: the texture's own colour.
    /// </summary>
    public int GreyChannel { get; init; } = -1;

    /// <summary>
    /// A colour the diffuse is multiplied by (hair's brown; white for the rest).
    /// </summary>
    public Vector3 Tint { get; init; } = Vector3.One;

    /// <summary>
    /// Drawn without back-face culling (foliage, alpha clothing, cull-none script materials).
    /// </summary>
    public bool DoubleSided { get; init; }

    /// <summary>
    /// The EMISSIVE type (map feature mode 6, MATERIAL_SPEC type 4).
    /// </summary>
    public bool Emissive { get; init; }

    /// <summary>
    /// World-space planar projection instead of UVs (MapFeatureMode.TRIPLANAR / DUAL_TRIPLANAR).
    /// </summary>
    public bool Triplanar { get; init; }

    /// <summary>
    /// The DUST define: the weather's dust covers the material (building parts; triplanar map features, docs/formats/weather.md "Dust").
    /// </summary>
    public bool Dust { get; init; }

    /// <summary>
    /// The foliage shader (FOLIAGE map features, leaves): wetness with a fixed absorbance.
    /// </summary>
    public bool Foliage { get; init; }

    /// <summary>
    /// The texture coordinates' tiling (the record's <c>tile X</c>, <c>tile Y</c>).
    /// </summary>
    public Vector2 Tile { get; init; } = Vector2.One;

    /// <summary>
    /// The record's <c>specular mult</c> (1 when absent).
    /// </summary>
    public float SpecularMult { get; init; } = 1;

    /// <summary>
    /// Multiply the diffuse by the vertex colour when the mesh has one (the COLOURING define; docs/formats/runtime-materials.md).
    /// </summary>
    public bool VertexColours { get; init; }

    /// <summary>
    /// Character head textures, sampled at uv + (0, 1): the body mesh keeps head UVs in v -1..0 (Kenshi character.hlsl).
    /// </summary>
    public string? HeadDiffuse { get; init; }

    /// <summary>
    /// The character head's normal map (see <see cref="HeadDiffuse"/>).
    /// </summary>
    public string? HeadNormal { get; init; }

    /// <summary>
    /// Clamp to a transparent black border instead of repeating (Kenshi's Character material units).
    /// </summary>
    public bool BorderAddressing { get; init; }
}

/// <summary>
/// Finds textures for a mesh's submeshes (best effort; the rules and evidence are in docs/viewer.md):
/// FCS records that name the mesh and carry textures (directly, or via MATERIAL_SPEC* records), then the
/// submesh's script material when it names real texture files.
/// </summary>
public sealed class MaterialResolver
{
    /// <summary>
    /// The records the candidates come from (null: script materials only).
    /// </summary>
    readonly GameDatabase? _db;

    /// <summary>
    /// The Ogre script materials (null: none).
    /// </summary>
    readonly OgreMaterialLibrary? _materials;

    /// <summary>
    /// Finds texture files (a candidate whose diffuse map is not found is dropped).
    /// </summary>
    readonly AssetLocator _assets;

    /// <summary>
    /// The records whose filename fields name each mesh (bare name, case-insensitive), with the field; an item with mesh variants is listed under
    /// each variant.
    /// </summary>
    readonly Dictionary<string, List<(GameRecord Record, string Field)>> _meshUsers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The records that reference each record (by string id), for parts that take a parent's material and weapons that take a manufacturer's model.
    /// </summary>
    readonly Dictionary<string, List<GameRecord>> _referencedBy = new(StringComparer.Ordinal);

    /// <summary>
    /// Placeholder textures the script materials name for units Kenshi fills at run time.
    /// </summary>
    public static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "black.dds", "flat.dds", "white.dds", "grey.dds", "gray.dds", "blank.dds", "blask.dds",
    };

    /// <summary>
    /// Indexes the database's records by the meshes they name and by the records they reference.
    /// </summary>
    public MaterialResolver(GameDatabase? db, OgreMaterialLibrary? materials, AssetLocator assets)
    {
        this._db = db;
        this._materials = materials;
        this._assets = assets;
        if (db is null) return;
        foreach (var record in db.Records.Values)
        {
            // An item with mesh variants (a nest's bones: bone1..bone14.mesh) uses every variant its named mesh stands for (#342).
            int variants = record.GetInt("number of meshes");
            foreach (var (key, value) in record.Filenames)
                if (value.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase))
                {
                    Users(Path.GetFileName(value.Replace('\\', '/'))).Add((record, key));
                    for (int n = 2; n <= variants; n++) Users(Data.World.WorldObjectLayout.MeshVariant(value, n)).Add((record, key));
                }
            foreach (var list in record.ReferenceLists)
                foreach (var r in record.GetReferences(list))
                {
                    if (!_referencedBy.TryGetValue(r.TargetStringId, out var by)) _referencedBy[r.TargetStringId] = by = [];
                    by.Add(record);
                }
        }
    }

    /// <summary>
    /// The users list of a mesh, made on first use.
    /// </summary>
    List<(GameRecord, string)> Users(string mesh)
    {
        if (!_meshUsers.TryGetValue(mesh, out var list)) _meshUsers[mesh] = list = [];
        return list;
    }

    /// <summary>
    /// FCS records whose filename fields name <paramref name="meshFile"/> (bare name, case-insensitive).
    /// </summary>
    public IReadOnlyList<(GameRecord Record, string Field)> RecordsUsing(string meshFile) =>
        _meshUsers.TryGetValue(Path.GetFileName(meshFile.Replace('\\', '/')), out var list) ? list : [];

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
        return result.Where(m => m.Diffuse is null || _assets.Find(m.Diffuse) is not null)
            .DistinctBy(m => (m.Diffuse?.ToLowerInvariant(), m.Normal?.ToLowerInvariant(), m.Diffuse2?.ToLowerInvariant(), m.Alpha))
            .ToList();
    }

    /// <summary>
    /// The materials a record gives for the mesh named in its <paramref name="field"/>: by record type (foliage, map features, birds, hair, races,
    /// weapons), else its own texture map, then its material references (a building part without any takes its parents').
    /// </summary>
    IEnumerable<SurfaceMaterial> FromRecord(GameRecord record, string field)
    {
        string via = $"{record.Type} '{record.Name}' .{field}";
        bool female = field.EndsWith(" female", StringComparison.Ordinal) || field.StartsWith("female ", StringComparison.Ordinal);
        switch (record.Type)
        {
            case FcsRecordType.FoliageMesh when field == "leaves mesh":
                yield return new SurfaceMaterial
                {
                    Description = via + " (leaves texture)",
                    Diffuse = Tex(record, "leaves texture"),
                    Normal = Tex(record, "leaves normal"),
                    Alpha = AlphaSource.NormalAlpha,
                    AlphaThreshold = record.GetInt("leaves alpha threshold", 80) / 255f,
                    DoubleSided = true,
                    Foliage = true,
                };
                yield break;
            case FcsRecordType.FoliageMesh:
                yield return MapFeature(record, via, record.GetInt("material type"), record.GetInt("alpha threshold", 128));
                yield break;
            case FcsRecordType.MapFeatures:
                yield return MapFeature(record, via, record.GetInt("texture mode"), record.GetInt("alpha threshold"));
                yield break;
            case FcsRecordType.WildlifeBirds:
                yield return new SurfaceMaterial { Description = via + " (texture)", Diffuse = Tex(record, "texture") };
                yield break;
            case FcsRecordType.Attachment:
                // Hair: grey diffuse and alpha are packed into channels of one texture (fcs.def "hair ... channel").
                yield return new SurfaceMaterial
                {
                    Description = via + " (texture map, channel-packed hair)",
                    Diffuse = Tex(record, female ? "texture map female" : "texture map"),
                    Alpha = AlphaSource.DiffuseChannel,
                    AlphaChannel = record.GetInt("hair alpha channel"),
                    GreyChannel = record.GetInt("hair diffuse channel"),
                    AlphaThreshold = record.GetInt("alpha rejection", 128) / 255f,
                    Tint = new Vector3(0.35f, 0.25f, 0.17f),
                    DoubleSided = true,
                };
                yield break;
            case FcsRecordType.Race:
                {
                    // The first head of the race (heads are picked per character; the first is as good as any).
                    var head = record.GetReferences(female ? "heads female" : "heads male")
                        .Select(r => _db!.Find(r.TargetStringId))
                        .FirstOrDefault(h => h is not null);
                    yield return new SurfaceMaterial
                    {
                        Description = via + " (body texture" + (head is null ? ")" : $", head '{head.Name}')"),
                        Diffuse = Tex(record, female ? "body texture female" : "body texture male"),
                        Normal = Tex(record, female ? "nm female" : "nm male"),
                        HeadDiffuse = head is null ? null : Tex(head, "texture map"),
                        HeadNormal = head is null ? null : Tex(head, "normal map"),
                        BorderAddressing = true,
                    };
                    yield break;
                }
            case FcsRecordType.Weapon:
                foreach (var m in WeaponModels(record)) yield return m with { Description = $"{m.Description} via {via}" };
                yield break;
        }

        if (Tex(record, "texture map") is { } own)
            yield return new SurfaceMaterial { Description = via + " (texture map)", Diffuse = own, Normal = Tex(record, "normal map") };

        var materialRefs = record.GetReferences("material");
        if (materialRefs.Count == 0 && record.Type == FcsRecordType.BuildingPart)
        {
            // Parts without their own material: use the material of the buildings (or parts) listing them.
            foreach (var parent in _referencedBy.GetValueOrDefault(record.StringId) ?? [])
                if (parent.GetReferences("parts").Any(r => r.TargetStringId == record.StringId))
                    foreach (var m in Expand(parent.GetReferences("material"), 0))
                        yield return m with { Description = $"{m.Description} via {parent.Type} '{parent.Name}' (parent of {via})" };
            yield break;
        }
        foreach (var m in Expand(materialRefs, 0))
            yield return m with { Description = $"{m.Description} via {via}" };
    }

    /// <summary>
    /// A map feature's (or foliage mesh's) material by its MapFeatureMode <paramref name="mode"/>: triplanar and dual modes, FOLIAGE's cut-out from
    /// the normal map's alpha, EMISSIVE; vertex colouring always.
    /// </summary>
    SurfaceMaterial MapFeature(GameRecord record, string via, int mode, int alphaThreshold)
    {
        // MapFeatureMode { UV_MAPPED, TRIPLANAR, TERRAIN, DUAL_TEXTURE, FOLIAGE, DUAL_TRIPLANAR, EMISSIVE }
        bool dual = mode is 3 or 5;
        return new SurfaceMaterial
        {
            Description = $"{via} (texture map, mode {MapFeatureModeName(mode)})",
            Diffuse = Tex(record, "texture map"),
            Normal = Tex(record, "normal map"),
            Diffuse2 = dual ? Tex(record, "texture map 2") : null,
            Normal2 = dual ? Tex(record, "normal map 2") : null,
            Triplanar = mode is 1 or 5,
            Dust = mode is 1 or 5,
            Foliage = mode == 4,
            Alpha = mode == 4 && alphaThreshold > 0 ? AlphaSource.NormalAlpha : AlphaSource.None, // the cut-out mask is the normal map's alpha
            AlphaThreshold = mode == 4 ? alphaThreshold / 255f : 0,
            DoubleSided = mode == 4,
            Emissive = mode == 6,
            VertexColours = true,
            Tile = new Vector2(record.GetFloat("tile X", 1), record.GetFloat("tile Y", 1)),
        };
    }

    /// <summary>
    /// The MapFeatureMode name of a mode number (the number itself when unknown).
    /// </summary>
    public static string MapFeatureModeName(int mode) => mode switch
    {
        0 => "UV_MAPPED",
        1 => "TRIPLANAR",
        2 => "TERRAIN",
        3 => "DUAL_TEXTURE",
        4 => "FOLIAGE",
        5 => "DUAL_TRIPLANAR",
        6 => "EMISSIVE",
        _ => mode.ToString(),
    };

    /// <summary>
    /// A weapon's looks: the models of the WEAPON_MANUFACTURERs that list it, highest chance first.
    /// </summary>
    IEnumerable<SurfaceMaterial> WeaponModels(GameRecord weapon)
    {
        // WEAPON_MANUFACTURER lists "weapon types" (WEAPON) and "weapon models" (MATERIAL_SPECS_WEAPON); a weapon's look is
        // one of its manufacturers' models. Highest chance first.
        var models = new List<(GameReference Ref, GameRecord Maker)>();
        foreach (var maker in _referencedBy.GetValueOrDefault(weapon.StringId) ?? [])
            if (maker.Type == FcsRecordType.WeaponManufacturer && maker.GetReferences("weapon types").Any(r => r.TargetStringId == weapon.StringId))
                models.AddRange(maker.GetReferences("weapon models").Select(r => (r, maker)));
        foreach (var (r, maker) in models.OrderByDescending(m => m.Ref.Values.Value1))
            if (_db!.Find(r.TargetStringId) is { } spec)
                foreach (var m in Leaf(spec))
                    yield return m with { Description = $"{m.Description} (model of {maker.Name})" };
    }

    /// <summary>
    /// The materials of a list of MATERIAL_SPEC references, likeliest first, collections expanded (at most 4 deep).
    /// </summary>
    IEnumerable<SurfaceMaterial> Expand(IReadOnlyList<GameReference> refs, int depth)
    {
        if (_db is null || depth > 4) yield break;

        // Collections pick at random with val1 as probability (fcs.def); list the likeliest first.
        foreach (var r in refs.OrderByDescending(r => r.Values.Value1 == 0 ? 100 : r.Values.Value1))
            if (_db.Find(r.TargetStringId) is { } target)
            {
                var nested = target.GetReferences("material");
                if (target.Type is FcsRecordType.MaterialSpecsCollection || target.Type == FcsRecordType.MaterialSpec && nested.Count > 0)
                {
                    foreach (var m in Expand(nested, depth + 1)) yield return m with { Description = $"{m.Description} in collection '{target.Name}'" };
                }
                else
                    foreach (var m in Leaf(target)) yield return m;
            }
    }

    /// <summary>
    /// The material of one MATERIAL_SPEC, MATERIAL_SPECS_CLOTHING or MATERIAL_SPECS_WEAPON record (none for another type).
    /// </summary>
    IEnumerable<SurfaceMaterial> Leaf(GameRecord spec)
    {
        string name = $"{spec.Type} '{spec.Name}'";
        switch (spec.Type)
        {
            case FcsRecordType.MaterialSpec:
                {
                    // BuildingShader { DEFAULT, ALPHA, FOLIAGE, DUAL, EMISSIVE }
                    int type = spec.GetInt("material type");
                    yield return new SurfaceMaterial
                    {
                        Description = $"{name} (type " +
                            $"{type switch { 0 => "DEFAULT", 1 => "ALPHA", 2 => "FOLIAGE", 3 => "DUAL", 4 => "EMISSIVE", _ => type.ToString() }})",
                        Diffuse = Tex(spec, "texture map"),
                        Normal = Tex(spec, "normal map"),
                        Diffuse2 = type == 3 ? Tex(spec, "texture map 2") : null,
                        Normal2 = type == 3 ? Tex(spec, "normal map 2") : null,
                        Alpha = type is 1 or 2 ? AlphaSource.NormalAlpha : AlphaSource.None,
                        AlphaThreshold = type is 1 or 2 ? 0.5f : 0,
                        DoubleSided = type == 2,
                        Emissive = type == 4,
                        Tile = new Vector2(spec.GetFloat("tile X", 1), spec.GetFloat("tile Y", 1)),
                        SpecularMult = spec.GetFloat("specular mult", 1),
                        VertexColours = true,
                    };
                    break;
                }
            case FcsRecordType.MaterialSpecsClothing:
                {
                    // ItemShader { DEFAULT, ALPHA, DOUBLE_SIDED }; which channel ALPHA uses is not documented (docs/viewer.md).
                    int type = spec.GetInt("material type");
                    yield return new SurfaceMaterial
                    {
                        Description = $"{name} (type {type switch { 0 => "DEFAULT", 1 => "ALPHA", 2 => "DOUBLE_SIDED", _ => type.ToString() }})",
                        Diffuse = Tex(spec, "texture map"),
                        Normal = Tex(spec, "normal map"),
                        Alpha = type == 1 ? AlphaSource.NormalAlpha : AlphaSource.None,
                        AlphaThreshold = type == 1 ? 0.5f : 0,
                        DoubleSided = type is 1 or 2,
                        SpecularMult = spec.GetFloat("specular mult", 1),
                        // A ground item takes COLOURING when its mesh has vertex colours (docs/formats/runtime-materials.md "Callers"); worn items
                        // never read this.
                        VertexColours = true,
                    };
                    break;
                }
            case FcsRecordType.MaterialSpecsWeapon:
                yield return new SurfaceMaterial
                {
                    Description = name,
                    Diffuse = Tex(spec, "texture map"),
                    Normal = Tex(spec, "normal map"),
                    SpecularMult = spec.GetFloat("specular mult", 1),
                };
                break;
        }
    }

    /// <summary>
    /// A script material's first pass, if it names a real texture (not a placeholder Kenshi replaces).
    /// </summary>
    SurfaceMaterial? FromScript(string materialName)
    {
        if (_materials?.Find(materialName) is not { } material) return null;
        var pass = material.Techniques.FirstOrDefault(t => t.Scheme == "Default")?.Passes.FirstOrDefault()
            ?? material.Techniques.FirstOrDefault()?.Passes.FirstOrDefault();
        if (pass is null) return null;
        var units = pass.TextureUnits.Where(u => u.Texture is { } t && !Placeholders.Contains(t) && _assets.Find(t) is not null).ToList();
        if (units.Count == 0) return null;
        var diffuse = units.FirstOrDefault(u => u.Alias.Contains("diffuse", StringComparison.OrdinalIgnoreCase)) ?? units[0];
        var normal = units.FirstOrDefault(u => u != diffuse && u.Alias.Contains("normal", StringComparison.OrdinalIgnoreCase));
        var blend = pass.SceneBlend;
        return new SurfaceMaterial
        {
            Description = $"script material '{material.Name}' ({material.File})",
            Diffuse = diffuse.Texture,
            Normal = normal?.Texture,
            Alpha = pass.AlphaRejection.Function != OgreCompareFunction.AlwaysPass || !blend.IsOpaque ? AlphaSource.DiffuseAlpha : AlphaSource.None,
            AlphaThreshold = pass.AlphaRejection.Function != OgreCompareFunction.AlwaysPass ? Math.Max(pass.AlphaRejection.Value,
                    (byte)1) / 255f : !blend.IsOpaque ? 0.5f : 0,
            DoubleSided = pass.CullHardware == OgreCullHardware.None,
        };
    }

    /// <summary>
    /// A record's path field, or null when empty.
    /// </summary>
    static string? Tex(GameRecord record, string field) => record.GetPath(field) is { Length: > 0 } p ? p : null;
}
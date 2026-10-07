using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Characters;

/// <summary>A mesh part of one character: which GPU mesh, how it is posed and with which material.</summary>
internal sealed class AssetPart
{
    public required string Label;
    public required GpuObjectMesh Mesh;
    /// <summary>Skinned by the body's bone palette (clothing, hair, beards); else placed by <see cref="Bone"/> and <see cref="Offset"/>.</summary>
    public required bool Shared;
    public int Bone = -1;
    public Matrix4x4 Offset = Matrix4x4.Identity;
    /// <summary>Index among the character's bone-attached parts (the matrices <see cref="CharacterRenderer"/> keeps per frame); -1 for shared parts.</summary>
    public int AttachIndex = -1;
    public required PartMaterial Material;
    /// <summary>The mesh file's bounds (the LOD value's centre and radius), in the part's own space.</summary>
    public required Vector3 BoundsCentre;
    public required float BoundsRadius;
    // This frame's slot in the material table (CharacterRenderer.Update).
    public long MaterialFrame = -1;
    public int MaterialSlot;
}

/// <summary>
/// One part's look as the shader takes it (<see cref="CharacterMaterialRecord"/>): the shading mode, the textures (resolved to bindless
/// indices each frame, as they come and go with the cache) and the colours. Made once per appearance part.
/// </summary>
internal sealed class PartMaterial
{
    public uint Shading;
    public string Description = "";
    public readonly WorldTexture?[] Tex = new WorldTexture?[CharacterShaders.Slots];
    public bool Swizzled;
    public bool ClipOnNormalAlpha, Dyed, DoubleSided;
    public int VestCount;
    public bool[] VestColoured = new bool[3];
    public Vector3 ShirtColour, SkinTone, HairColour;
    public bool HasShirtColour;
    public Vector4 HairAlpha, HairMult, BeardAlpha, DiffuseChannel = new(1, 0, 0, 0), AlphaChannel = new(1, 0, 0, 0), Colour1, Colour2;
    public float AlphaThreshold;
}

/// <summary>Everything the renderer keeps for one appearance: the skeleton and its shape, the parts, the posture layers that always apply.</summary>
internal sealed class CharacterAsset
{
    public required CharacterAppearance Appearance;
    public required SkeletonRig Rig;
    public BodyShapeData? Shape;
    public readonly List<AssetPart> Parts = [];
    /// <summary>The posture libraries (<c>postures</c>, <c>neck set</c>, <c>shoulder set</c>) held at the body file's sliders, weight 1.</summary>
    public readonly List<(LayerDef Def, float Time)> Fixed = [];
    public int AttachCount;
    /// <summary>The default idle: the body file's <c>idle stance</c>.</summary>
    public string Idle = "idle_stand_relax";
    /// <summary>A sphere round the standing character in its own space (centre height, radius) for culling.</summary>
    public float Height = 20, Radius = 16;
}

/// <summary>
/// Turns appearances into GPU content (the port of the removed viewer's <c>CharacterScene.Load</c>, docs/character-viewer.md): meshes by file
/// (shared between appearances; hair, armour and weapons share naturally), skeleton rigs by file, textures through one cache.
/// Render thread only; uploads need an open frame (<see cref="GpuContext.EnsureFrame"/>).
/// </summary>
internal sealed class CharacterContent
{
    const string AllocationName = "character meshes";
    readonly GpuContext gpu;
    readonly AssetLocator assets;
    readonly GameDatabase db;
    readonly string installRoot;
    readonly MaterialResolver resolver;
    public WorldTextureCache Textures { get; }
    readonly Dictionary<string, SkeletonRig> rigs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string Path, int Bones), LoadedMesh?> meshes = [];
    public List<string> Messages { get; } = [];
    public long MeshBytes { get; private set; }
    public int MeshCount => meshes.Count;

    sealed record LoadedMesh(GpuObjectMesh Gpu, string? SkeletonName, Vector3 Centre, float Radius);

    public CharacterContent(GpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assets)
    {
        this.gpu = gpu;
        this.assets = assets;
        this.db = db;
        installRoot = install.Root;
        resolver = new MaterialResolver(db, OgreMaterialLibrary.LoadConfigured(install, out _), assets);
        Textures = new WorldTextureCache(gpu, assets, "character textures");
    }

    public GameDatabase Database => db;

    /// <summary>Meshes are loaded by bare name (Kenshi reduces the path first), so resources.cfg's last location wins.</summary>
    string? FindMesh(string fcsPath)
    {
        var bare = Path.GetFileName(fcsPath.Replace('\\', '/'));
        return assets.Configured.Find(bare) ?? assets.Find(fcsPath);
    }

    SkeletonRig? Rig(string name)
    {
        var path = assets.Find(name);
        if (path is null) return null;
        if (rigs.TryGetValue(path, out var rig)) return rig;
        return rigs[path] = new SkeletonRig(OgreSkeletonReader.ReadFile(path));
    }

    /// <summary>Reads and uploads a mesh file, with its skinning weights for a skeleton of <paramref name="boneCount"/> bones (null: unskinned).</summary>
    LoadedMesh? Mesh(string path, int? boneCount)
    {
        var key = (path.ToLowerInvariant(), boneCount ?? -1);
        if (meshes.TryGetValue(key, out var cached)) return cached;
        LoadedMesh? result = null;
        try
        {
            var mesh = OgreMeshReader.ReadFile(path);
            var model = Model.Build(mesh, boneCount);
            foreach (var w in model.Warnings) Messages.Add($"{Path.GetFileName(path)}: {w}");
            var levels = CharacterLod.Levels(mesh);
            var centre = mesh.Bounds is { } b ? (b.Min + b.Max) / 2 : model.Center;
            float radius = mesh.Bounds is { } bb ? bb.Radius : model.Radius;
            var decoded = new DecodedObjectMesh { Model = model, Levels = levels, Manual = [], Centre = centre, Radius = radius };
            var gpuMesh = new GpuObjectMesh { Distances = [.. levels.Select(l => l.Distance)], Manual = new GpuObjectMesh?[levels.Count], Centre = centre, Radius = radius };
            gpu.EnsureFrame();
            foreach (var part in model.Parts)
            {
                var prepared = ObjectMeshCache.PreparePart(decoded, part, keepLevels: false);
                int vertexBytes = part.Vertices.Length * Vertex.Size, indexBytes = prepared.All.Length * 4;
                var vertices = DeviceBuffer.Create(gpu, (ulong)vertexBytes, BufferUse.Vertex, AllocationName);
                var indices = DeviceBuffer.Create(gpu, (ulong)indexBytes, BufferUse.Index, AllocationName);
                gpu.Uploads.Write(vertices, 0, MemoryMarshal.AsBytes(part.Vertices.AsSpan()));
                gpu.Uploads.Write(indices, 0, MemoryMarshal.AsBytes(prepared.All.AsSpan()));
                var gp = new GpuObjectPart
                {
                    SubMeshIndex = part.SubMeshIndex, MaterialName = part.MaterialName, HasTangents = part.HasTangents, HasColours = part.HasColours,
                    Vertices = vertices, Indices = indices, Offset = prepared.Offset, Count = prepared.Count, UvScale = prepared.UvScale,
                    Attributes = ObjectMeshCache.VertexAttributes(vertices),
                };
                gpuMesh.Parts.Add(gp);
                gpuMesh.Bytes += vertexBytes + indexBytes;
            }
            MeshBytes += gpuMesh.Bytes;
            result = new LoadedMesh(gpuMesh, mesh.SkeletonName, centre, radius);
        }
        catch (Exception e) when (e is OgreFormatException or IOException or EndOfStreamException or InvalidDataException)
        {
            Messages.Add($"{Path.GetFileName(path)}: {e.Message}");
        }
        return meshes[key] = result;
    }

    /// <summary>Assembles the asset for <paramref name="c"/>; null when its body mesh or skeleton cannot be found.</summary>
    public CharacterAsset? Build(CharacterAppearance c)
    {
        var bodyPath = FindMesh(c.BodyMesh);
        if (bodyPath is null) { Messages.Add($"body mesh not found: {c.BodyMesh}"); return null; }
        // The body's own skeleton link (Kenshi shares that instance with worn items); the rig's bone count decides the weights kept.
        string? skeletonName;
        try { skeletonName = OgreMeshReader.ReadFile(bodyPath).SkeletonName; }
        catch (Exception e) when (e is OgreFormatException or IOException or EndOfStreamException or InvalidDataException) { Messages.Add($"{c.BodyMesh}: {e.Message}"); return null; }
        var rig = skeletonName is null ? null : Rig(skeletonName);
        if (rig is null) { Messages.Add($"{c.BodyMesh} has no skeleton ({skeletonName})"); return null; }
        var body = Mesh(bodyPath, rig.BoneCount);
        if (body is null) return null;

        var asset = new CharacterAsset { Appearance = c, Rig = rig };
        asset.Shape = Shape(c, rig);
        asset.Parts.Add(new AssetPart
        {
            Label = $"body {Path.GetFileName(bodyPath)}", Mesh = body.Gpu, Shared = true, Material = BodyMaterial(c),
            BoundsCentre = body.Centre, BoundsRadius = body.Radius,
        });
        foreach (var part in c.Parts)
        {
            var path = FindMesh(part.Mesh);
            if (path is null) continue;
            bool shared = part.Mode == AttachMode.SharedSkeleton;
            var loaded = Mesh(path, shared ? rig.BoneCount : null);
            if (loaded is null || loaded.Gpu.Parts.Count == 0) continue;
            var ap = new AssetPart
            {
                Label = $"{part.Record.Type} '{part.Record.Name}' .{part.Field}", Mesh = loaded.Gpu, Shared = shared, Material = ItemMaterial(c, part, path, loaded),
                BoundsCentre = loaded.Centre, BoundsRadius = loaded.Radius,
            };
            if (!shared)
            {
                // Mode 0: attachObjectToBone at the attachment point; a bone the skeleton lacks falls back to the root.
                var point = part.Point is { } pn && c.AttachmentPoints.TryGetValue(pn, out var a) ? a : null;
                int bone = point is null ? -1 : rig.Handle(point.Bone) ?? -1;
                if (bone < 0) bone = rig.Skeleton.Bones.FirstOrDefault(b => b.Parent is null)?.Handle ?? 0;
                ap.Bone = bone;
                if (point is not null) ap.Offset = Matrix4x4.CreateFromQuaternion(point.Rotation) * Matrix4x4.CreateTranslation(point.Position);
                ap.AttachIndex = asset.AttachCount++;
            }
            asset.Parts.Add(ap);
        }

        // The posture libraries, held at the body file's sliders (docs/animation.md, "Posture sliders").
        if (c.Body is { } bodyFile)
        {
            asset.Idle = bodyFile.IdleStance ?? asset.Idle;
            foreach (var (animation, slider) in new[] { ("postures", "Posture"), ("neck set", "Neck position"), ("shoulder set", "Shoulder set") })
                if (bodyFile.Floats.ContainsKey(slider) && rig.Layer(db, animation) is { } def)
                    asset.Fixed.Add((def, def.Clip.Length * bodyFile.Get(slider) / 100));
        }
        return asset;
    }

    /// <summary>Body-shape sliders as bone sizes (docs/animation.md, "Body shape sliders"); only skeletons with exactly 30 bones, as in Kenshi.</summary>
    BodyShapeData? Shape(CharacterAppearance c, SkeletonRig rig)
    {
        if (rig.BoneCount != CharacterShape.HumanBoneCount) return null;
        var input = CharacterShape.InputFor(db, c);
        var shape = CharacterShape.Compute(input, name => rig.Handle(name) is not null);
        return rig.Shape(shape.BoneSize, shape.PositionalSize, shape.MovementScale);
    }

    // ---- materials (CharacterScene.BodyMaterial / PartMaterial) ----

    static Vector4 Channel(int channel) => channel switch { 1 => new(0, 1, 0, 0), 2 => new(0, 0, 1, 0), 3 => new(0, 0, 0, 1), _ => new(1, 0, 0, 0) };

    static string? Tex(GameRecord? r, string field) => r?.GetPath(field) is { Length: > 0 } p ? p : null;

    WorldTexture? Texture(string? name, bool border) => name is null ? null : Textures.Get(name, border);

    PartMaterial BodyMaterial(CharacterAppearance c)
    {
        string f = c.Female ? " female" : "";
        // Shirt colour for vest colour masks: the CHARACTER's colour record (dyes of the faction are not followed).
        var colour = c.Character?.GetReferences("color").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(x => x is not null);
        var m = new PartMaterial
        {
            Shading = CharacterShaders.ShadeBody, Description = $"RACE '{c.Race.Name}' body",
            SkinTone = c.SkinToneParameter, HairColour = c.HairColour,
            HairAlpha = Channel(c.Hair?.GetInt("head alpha channel" + f, 1) ?? 1), HairMult = Channel(c.Hair?.GetInt("head channel" + f) ?? 0),
            BeardAlpha = Channel(c.Beard?.GetInt("head alpha channel" + f, 1) ?? 1),
            ShirtColour = colour is null ? Vector3.One : CharacterAppearance.Rgb(colour.GetInt("color 1")), HasShirtColour = colour is not null,
        };
        m.Tex[CharacterShaders.SlotDiffuse] = Texture(c.BodyTexture, true);
        m.Tex[CharacterShaders.SlotNormal] = Texture(c.BodyNormal, true);
        m.Tex[CharacterShaders.SlotBodyMask] = Texture(c.BodyMask, true);
        m.Tex[CharacterShaders.SlotHeadDiffuse] = Texture(Tex(c.Head, "texture map"), true);
        m.Tex[CharacterShaders.SlotHeadNormal] = Texture(Tex(c.Head, "normal map"), true);
        m.Tex[CharacterShaders.SlotHeadMask] = Texture(Tex(c.Head, "mask map"), true);
        m.Tex[CharacterShaders.SlotHairOverlay] = Texture(Tex(c.Hair, "head texture" + f), true);
        m.Tex[CharacterShaders.SlotBeardOverlay] = Texture(Tex(c.Beard, "head texture" + f), true);
        for (int i = 0; i < c.BodyLayers.Count && i < 3; i++)
        {
            var layer = c.BodyLayers[i];
            m.Tex[CharacterShaders.SlotVest + 3 * i] = Texture(layer.Diffuse, true);
            m.Tex[CharacterShaders.SlotVest + 3 * i + 1] = Texture(layer.Normal, true);
            m.Tex[CharacterShaders.SlotVest + 3 * i + 2] = Texture(layer.ColourMap, true);
            m.VestCount = i + 1;
            m.VestColoured[i] = layer.ColourMap is not null && m.HasShirtColour;
        }
        m.Swizzled = true;
        return m;
    }

    PartMaterial ItemMaterial(CharacterAppearance c, CharacterPart part, string path, LoadedMesh loaded)
    {
        var item = part.Record;
        string f = c.Female ? " female" : "";
        var m = new PartMaterial();
        if (item.Type == FcsRecordType.ATTACHMENT)
        {
            // Hair and beards: brightness and alpha are channels of the diffuse texture.
            m.Shading = CharacterShaders.ShadeHair;
            string? diffuse = Tex(item, "texture map" + f) ?? Tex(item, "texture map");
            m.Description = $"hair texture {Path.GetFileName(diffuse ?? "-")}";
            m.Tex[CharacterShaders.SlotDiffuse] = Texture(diffuse, false);
            m.DiffuseChannel = Channel(item.GetInt("hair diffuse channel"));
            m.AlphaChannel = Channel(item.GetInt("hair alpha channel"));
            m.AlphaThreshold = item.GetInt("alpha rejection", 128) / 255f;
            m.HairColour = c.HairColour;
            m.DoubleSided = true;
            return m;
        }

        m.Shading = CharacterShaders.ShadeItem;
        bool worn = part.Mode == AttachMode.SharedSkeleton;
        // Clothing: MATERIAL_SPECS_CLOTHING from "material" (female: "material female" if set), likeliest first; dye from
        // the item's "color" COLOR_DATA (else the CHARACTER's, unless "dont colorise").
        var refs = item.GetReferences(c.Female && item.GetReferences("material female").Count > 0 ? "material female" : "material");
        var spec = refs.OrderByDescending(r => r.Values.Value0).Select(r => db.Find(r.TargetStringId))
            .FirstOrDefault(s => s is { Type: FcsRecordType.MATERIAL_SPECS_CLOTHING or FcsRecordType.MATERIAL_SPEC or FcsRecordType.MATERIAL_SPECS_WEAPON });
        // A generated loadout chose the clothing material (from "material"; "material female" still wins for women).
        if (part.Material is { Type: FcsRecordType.MATERIAL_SPECS_CLOTHING } chosen && !(c.Female && item.GetReferences("material female").Count > 0))
            spec = chosen;
        if (spec is not null)
        {
            var dye = item.GetReferences("color").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(x => x is not null);
            if (dye is null && !item.GetBool("dont colorise", true))
                dye = c.Character?.GetReferences("color").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(x => x is not null);
            bool dyed = dye is not null && Tex(spec, "color map") is not null;
            m.Description = $"{spec.Type} '{spec.Name}'" + (dyed ? $", dye '{dye!.Name}'" : "");
            m.Tex[CharacterShaders.SlotDiffuse] = Texture(Tex(spec, "texture map"), false);
            m.Tex[CharacterShaders.SlotNormal] = Texture(Tex(spec, "normal map"), false);
            if (dyed)
            {
                m.Tex[CharacterShaders.SlotColourMap] = Texture(Tex(spec, "color map"), false);
                m.Dyed = true;
                m.Colour1 = new Vector4(CharacterAppearance.Rgb(dye!.GetInt("color 1")), spec.GetFloat("paint factor 1"));
                m.Colour2 = new Vector4(CharacterAppearance.Rgb(dye.GetInt("color 2")), spec.GetFloat("paint factor 2"));
            }
            m.ClipOnNormalAlpha = worn && Tex(spec, "normal map") is not null;
            m.DoubleSided = spec.GetInt("material type") == 2;
            return m;
        }
        // A weapon of a generated loadout: its manufacturer's model.
        if (part.Material is { Type: FcsRecordType.MATERIAL_SPECS_WEAPON } model && Tex(model, "texture map") is { } modelTexture)
        {
            m.Description = $"MATERIAL_SPECS_WEAPON '{model.Name}' (loadout model)";
            m.Tex[CharacterShaders.SlotDiffuse] = Texture(modelTexture, false);
            m.Tex[CharacterShaders.SlotNormal] = Texture(Tex(model, "normal map"), false);
            return m;
        }
        // Weapons and anything else: the resolver (weapon models of the manufacturers, own textures, script material).
        OgreSubMesh? first = null;
        try { first = OgreMeshReader.ReadFile(path).SubMeshes.FirstOrDefault(); }
        catch (Exception e) when (e is OgreFormatException or IOException or EndOfStreamException or InvalidDataException) { }
        var candidate = first is null ? null : resolver.Candidates(path, first).FirstOrDefault();
        m.Description = candidate?.Description ?? "untextured";
        m.Tex[CharacterShaders.SlotDiffuse] = Texture(candidate?.Diffuse, false);
        m.Tex[CharacterShaders.SlotNormal] = Texture(candidate?.Normal, false);
        m.DoubleSided = candidate?.DoubleSided ?? false;
        return m;
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
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
    /// <summary>Only while the weapon or crossbow is stowed or in hand (<see cref="CharacterPart.Shown"/>).</summary>
    public WeaponShown Shown;
    /// <summary>When it shows by the bolts loaded in its crossbow (<see cref="CharacterPart.ShownShots"/>).</summary>
    public int ShownShots;
    /// <summary>The first loaded bolt of the crossbow in the hands: its origin is the muzzle (<see cref="CharacterRenderer.Muzzle"/>).</summary>
    public bool Muzzle;
    /// <summary>
    /// Whether the part shows with a weapon drawn (<paramref name="weaponDrawn"/>), that weapon being the crossbow (<paramref name="ranged"/>) or the back melee weapon
    /// (<paramref name="back"/>, else the hip one), with <paramref name="shots"/> bolts loaded in the crossbow.
    /// </summary>
    public bool ShowsWith(bool weaponDrawn, bool ranged, int shots = 1, bool back = false) => (ShownShots == 0 || (ShownShots < 0 ? shots <= 0 : shots >= ShownShots)) && Shown switch
    {
        WeaponShown.Drawn => weaponDrawn && !ranged && !back,
        WeaponShown.Sheathed => !(weaponDrawn && !ranged && !back),
        WeaponShown.BackDrawn => weaponDrawn && !ranged && back,
        WeaponShown.BackSheathed => !(weaponDrawn && !ranged && back),
        WeaponShown.RangedDrawn => weaponDrawn && ranged,
        WeaponShown.RangedSheathed => !(weaponDrawn && ranged),
        _ => true,
    };
    public required PartMaterial Material;
    /// <summary>The mesh file's bounds (the LOD value's centre and radius), in the part's own space.</summary>
    public required Vector3 BoundsCentre;
    public required float BoundsRadius;
    /// <summary>Boxes round the mesh for picking (<see cref="PickBox"/>): one per bone of a skinned part, one for an unskinned one.</summary>
    public PickBox[] PickBoxes = [];
    // The frame (CharacterRenderer.Update) it was last listed in, so the material update takes each part once.
    public long MaterialFrame = -1;
    /// <summary>The body mesh, whose vertices take the asset's morph.</summary>
    public bool Morphed;
    /// <summary>The body mesh, whose limb vertices the appearance's hidden limbs hide (<see cref="CharacterAppearance.HiddenLimbs"/>).</summary>
    public bool IsBody;
    /// <summary>Boots' collapsed half (<see cref="CharacterPart.HideSide"/>).</summary>
    public int HideSide;
    /// <summary>The mesh's shadow-only levels (null: the shadow passes pick among the picture's levels): their distances, level 0 first (the original), and the mesh level the first of them is.</summary>
    public float[]? ShadowDistances;
    public int ShadowFirst;
    /// <summary>
    /// The mesh file's own levels (the original's, docs/characters.md "LOD of character meshes"), for the Faithful character LOD: their distances, level 0 first, the mesh
    /// level the first reduced one is, and the file's stored bounding radius (the original's LOD value uses it unclamped). Null distances: the file has no levels.
    /// </summary>
    public float[]? FileDistances;
    public int FileFirst;
    public float FileRadius;
    /// <summary>Nearest distance of a user this frame, for the textures' mip streaming.</summary>
    public float Near;
    /// <summary>The draw batches of its mesh's parts by part, level and sidedness (<see cref="CharacterRenderer"/>'s gather), so the views do not look them up per instance.</summary>
    public int[]? Batches;
    float uvScale;
    /// <summary>World length of a texture-coordinate unit on the part (the largest of the mesh's parts; infinity: unknown, every mip kept).</summary>
    public float UvScale
    {
        get
        {
            if (uvScale == 0)
            {
                uvScale = float.PositiveInfinity;
                foreach (var p in Mesh.Parts) if (float.IsFinite(p.UvScale)) uvScale = float.IsFinite(uvScale) ? Math.Max(uvScale, p.UvScale) : p.UvScale;
            }
            return uvScale;
        }
    }
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
    /// <summary>Worn on the body's skeleton (the original's Skinned template): matte (its glossMult is 0).</summary>
    public bool Worn;
    /// <summary>A person's body (not an animal's): its bare skin may scatter light under the <c>skin</c> switch (<see cref="CharacterShaders.Skin"/>), by its race's choice
    /// (<see cref="CharacterRenderer.SkinRaces"/>, else <see cref="SkinDefault"/>).</summary>
    public bool Skin, SkinDefault;
    /// <summary>The RACE record's string id of a body (<see cref="Skin"/>).</summary>
    public string? Race;
    public int VestCount;
    public bool[] VestColoured = new bool[3];
    public Vector3 ShirtColour, SkinTone, HairColour;
    public bool HasShirtColour;
    public Vector4 HairAlpha, HairMult, BeardAlpha, DiffuseChannel = new(1, 0, 0, 0), AlphaChannel = new(1, 0, 0, 0), Colour1, Colour2;
    public float AlphaThreshold;
    /// <summary>An item's gloss multiplier (an icon's material <c>specular mult</c>, rtticons.hlsl's <c>glossMult</c>); 1 for the others.</summary>
    public float GlossMult = 1;
    /// <summary>An icon whose mesh has vertex colours: they multiply the albedo (the game's <c>RTTIcons_Coloured</c>).</summary>
    public bool VertexColoured;
    /// <summary>Takes blood (<see cref="CharacterShaders.Bloody"/>; <see cref="BloodFull"/>: the whole mesh at the full amount, a stump): <see cref="Colour1"/> holds the blood colour, <see cref="Colour2"/> x and y the race's <c>blood horizontal</c> and <c>blood vertical</c>.</summary>
    public bool Bloody, BloodFull;
    /// <summary>A headless body's neck cap (<see cref="CharacterShaders.Wound"/>): <see cref="Colour1"/> the race's blood colour, the diffuse slot the splatter map; <see cref="Mechanical"/> a robot race's.</summary>
    public bool Wound, Mechanical;
    /// <summary>The body's normal blend weight towards <see cref="CharacterShaders.SlotBlendNormal"/> (the original's <c>muscleBlend</c>).</summary>
    public float MuscleBlend;
    /// <summary>The record last written for it and the renderer's material epoch it holds for (<see cref="CharacterRenderer"/>); 0 after a field above changed: written again.</summary>
    public CharacterMaterialRecord Record;
    public int RecordEpoch;
    /// <summary>Its slot in <see cref="CharacterMaterialTable"/> (-1: none) and the update it was last drawn in.</summary>
    public int Slot = -1;
    public long SlotUsed;
}

/// <summary>Everything the renderer keeps for one appearance: the skeleton and its shape, the parts, the posture layers that always apply.</summary>
internal sealed class CharacterAsset
{
    public required CharacterAppearance Appearance;
    public required SkeletonRig Rig;
    public BodyShapeData? Shape;
    /// <summary>The stat-driven body last applied to the shape and the body's normal maps (<see cref="CharacterRenderer"/>), null before the first.</summary>
    public BodyBuild? AppliedBuild;
    /// <summary>The body normal maps and weight of <see cref="AppliedBuild"/> while a map is still loading; the parts keep the last ones until both are resident.</summary>
    public (WorldTexture? Normal, WorldTexture? Blend, float Weight)? PendingNormals;
    public readonly List<AssetPart> Parts = [];
    /// <summary>The carried lights (<see cref="CharacterAppearance.Lights"/>): a bone, the offset from it and an attach slot, posed like a bone-attached part.</summary>
    public readonly List<(int Bone, Matrix4x4 Offset, int AttachIndex, Meitou.Data.World.WorldLight Light)> Lamps = [];
    /// <summary>The posture libraries (<c>postures</c>, <c>neck set</c>, <c>shoulder set</c>) held at the body file's sliders, weight 1.</summary>
    public readonly List<(LayerDef Def, float Time)> Fixed = [];
    public int AttachCount;
    /// <summary>Where the face's morph offsets start in <see cref="MorphArena"/> (0: none).</summary>
    public uint MorphBase;
    /// <summary>The default idle: the body file's <c>idle stance</c>.</summary>
    public string Idle = "idle_stand_relax";
    /// <summary>A sphere round the standing character in its own space (centre height, radius) for culling.</summary>
    public float Height = 20, Radius = 16;
    /// <summary>An animal (<see cref="CharacterAppearance.Animal"/>): its layers are its own skeleton's clips (<see cref="SkeletonRig.Layer"/>), its sphere from the body mesh's bounds.</summary>
    public bool Animal;
    /// <summary>How far the body shape lifts the feet in the bind pose (negative: sinks them); the renderer lowers the character by it.</summary>
    public float GroundOffset;
    /// <summary>The skeleton's <c>Bip01 Head</c> (-1: none), which the portrait camera looks at.</summary>
    public int HeadBone = -1;
    /// <summary>An item's inventory icon (<see cref="CharacterContent.BuildIcon"/>): one unskinned part, no body (<see cref="Appearance"/> and <see cref="Rig"/> are null) and nothing to pose.</summary>
    public bool IsIcon;
    /// <summary>The body mesh's poses and the weights this appearance sets them to, for <see cref="CharacterRenderer"/>'s blend between two looks (null: the body has no poses).</summary>
    public MorphSource? Morph;
    /// <summary>What a blend between looks changes, as it was built (null until the asset is first blended); <see cref="Blending"/> while the fields hold a mix.</summary>
    public AssetRest? Rest;
    public bool Blending;
}

/// <summary>An appearance's face and body morph: the body mesh's poses, the weights set on them and whether the horns are cut.</summary>
internal sealed record MorphSource(MeshMorphs Poses, IReadOnlyDictionary<string, float> Weights, bool CutHorns);

/// <summary>An asset's blendable values as built: the body shape, the lift of the feet, the posture times, the colours of every part and the morph base.</summary>
internal sealed class AssetRest
{
    public BodyShapeData? Shape;
    public float GroundOffset;
    public (LayerDef Def, float Time)[] Fixed = [];
    public (Vector3 Skin, Vector3 Hair)[] Colours = [];
    public uint MorphBase;
}

/// <summary>
/// Turns appearances into GPU content (the port of the removed viewer's <c>CharacterScene.Load</c>, docs/character-viewer.md): meshes by file
/// (shared between appearances; hair, armour and weapons share naturally), skeleton rigs by file, textures through one cache.
/// Render thread only; uploads need an open frame (<see cref="IGpuContext.EnsureFrame"/>).
/// </summary>
internal sealed class CharacterContent : IDisposable
{
    const string AllocationName = "character meshes";
    readonly IGpuContext gpu;
    readonly AssetLocator assets;
    readonly GameDatabase db;
    readonly MaterialResolver resolver;
    public WorldTextureCache Textures { get; }
    readonly Dictionary<(string Path, int Bones, bool Close, string? PartMap), LoadedMesh?> meshes = [];
    public List<string> Messages { get; } = [];
    public long MeshBytes { get; private set; }
    public int MeshCount => meshes.Count;
    /// <summary>The render's pixels per radian (<see cref="CharacterRenderer.SetView"/>) when a mesh loads: its generated levels are placed by it and keep their distances after a resize.
    /// Until the first view: 1080 rows at 50 degrees.</summary>
    public float LodPixelsPerRadian { get; set; } = Meitou.Data.World.TerrainLod.ProjectionScale(1080, 50 * MathF.PI / 180);

    /// <param name="FirstMaterialName">The first submesh's material (script) name, which is all the material resolver takes of a submesh; null: no submesh.</param>
    sealed record LoadedMesh(GpuObjectMesh Gpu, string? FirstMaterialName, Vector3 Centre, float Radius, MeshMorphs? Morphs = null, float[]? ShadowDistances = null, int ShadowFirst = 0, PickBox[]? PickBoxes = null,
        float[]? FileDistances = null, int FileFirst = 0, float FileRadius = 0, PreparedMesh? NeckCap = null, PreparedMesh? HeadCap = null);

    public CharacterContent(IGpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assets, CharacterRigs? rigs = null)
    {
        this.gpu = gpu;
        this.assets = assets;
        this.db = db;
        resolver = new MaterialResolver(db, OgreMaterialLibrary.LoadConfigured(install, out _), assets);
        Textures = new WorldTextureCache(gpu, assets, "character textures");
        Morphs = new MorphArena(gpu);
        Materials = new CharacterMaterialTable(gpu);
        Rigs = rigs ?? CreateRigs(db, assets);
    }

    public GameDatabase Database => db;

    public MorphArena Morphs { get; }

    /// <summary>The parts' material records (binding 7), kept between frames.</summary>
    public CharacterMaterialTable Materials { get; }

    /// <summary>Frees every mesh's buffers, the textures, the morph arena and the material table. The meshes' parts must not be drawn afterwards.</summary>
    public void Dispose()
    {
        foreach (var mesh in meshes.Values)
            if (mesh is not null)
                foreach (var part in mesh.Gpu.Parts)
                {
                    part.Vertices.Dispose();
                    part.Indices.Dispose();
                }
        meshes.Clear();
        prepared.Clear();
        MeshBytes = 0;
        Textures.Dispose();
        Morphs.Dispose();
        Materials.Dispose();
    }

    /// <summary>Meshes are loaded by bare name (Kenshi reduces the path first), so resources.cfg's last location wins.</summary>
    string? FindMesh(string fcsPath) => FindMesh(assets, fcsPath);

    static string? FindMesh(AssetLocator assets, string fcsPath) => assets.Configured.Find(Path.GetFileName(fcsPath.Replace('\\', '/'))) ?? assets.Find(fcsPath);

    /// <summary>Rigs that find files as the characters' content does; the host makes one to share between the simulation and the renderer.</summary>
    public static CharacterRigs CreateRigs(GameDatabase db, AssetLocator assets) => new(db, path => FindMesh(assets, path), name => assets.Find(name));

    /// <summary>Skeleton rigs and pose setups, shared with the simulation's ragdolls (<see cref="CharacterRenderer.Rigs"/>).</summary>
    public CharacterRigs Rigs { get; }

    SkeletonRig? Rig(string name) => Rigs.Rig(name);

    string? BodySkeletonName(string path) => Rigs.BodySkeletonName(path);

    /// <summary>What a worker makes of a mesh file (<see cref="PrepareMesh"/>): everything but the GPU buffers.</summary>
    sealed record PreparedMesh(PreparedPart[] Parts, float[] Distances, string? FirstMaterialName, Vector3 Centre, float Radius, MeshMorphs? Morphs, float[]? ShadowDistances, int ShadowFirst, PickBox[] PickBoxes,
        float[]? FileDistances, int FileFirst, float FileRadius, PreparedMesh? NeckCap = null, PreparedMesh? HeadCap = null);
    /// <summary>A prepared mesh (null: it could not be read) and the messages its reading left.</summary>
    sealed record Prepared(PreparedMesh? Mesh, List<string> Messages);
    static readonly Prepared Uploaded = new(null, []);

    /// <summary>The meshes prepared or being prepared, by the key of <see cref="meshes"/>; an uploaded one is left as <see cref="Uploaded"/> so no worker prepares it again.</summary>
    readonly ConcurrentDictionary<(string Path, int Bones, bool Close, string? PartMap), Lazy<Prepared>> prepared = new();

    /// <summary>The preparation of a mesh, made once (any thread; the first caller's level settings stand).</summary>
    Lazy<Prepared> Prepare(string path, int? boneCount, float lodScale, bool shadowLevels, float pixelsPerRadian, LimbHide? limbs = null, bool close = false, bool icon = false, string? partMap = null) =>
        prepared.GetOrAdd((path.ToLowerInvariant(), icon ? IconBones : boneCount ?? -1, close, partMap?.ToLowerInvariant()), _ => new Lazy<Prepared>(() => PrepareMesh(path, boneCount, lodScale, shadowLevels, pixelsPerRadian, limbs, close, icon, partMap)));

    /// <summary>The bone count an icon's mesh is kept under (<see cref="prepared"/>, <see cref="meshes"/>): its tangents are built the icon shader's way (<see cref="Model.Build"/>).</summary>
    const int IconBones = -2;

    /// <summary>Reads a mesh file and makes its levels and index buffers (no GPU work, any thread): with its skinning weights for a skeleton of <paramref name="boneCount"/>
    /// bones (null: unskinned); its generated levels start <paramref name="lodScale"/> times farther than the rule gives. A <paramref name="close"/> mesh (the character editor's
    /// close-up) keeps only its full level: generating the others is nearly all of the time a mesh takes (a hair 1 to 2 s), and the editor shows one character up close.</summary>
    static Prepared PrepareMesh(string path, int? boneCount, float lodScale, bool shadowLevels, float pixelsPerRadian, LimbHide? limbs, bool close, bool icon = false, string? partMap = null)
    {
        var messages = new List<string>();
        try
        {
            var mesh = OgreMeshReader.ReadFile(path);
            if (MeshWeld.Enabled) OgreMeshWeld.Apply(mesh);
            var model = Model.Build(mesh, boneCount, gameBinormals: !icon, tangentSign: icon);
            var morphs = MeshMorphs.From(mesh, model, messages, Path.GetFileName(path));
            if (limbs is not null || partMap is not null) MarkBody(model, limbs, partMap is null ? null : PartMapImage(partMap, messages));
            // Not an icon's: its colour is the mesh's own vertex colour (CharacterShaders.VertexColoured), and icons take no skin.
            if (!icon) MarkCurvature(model);
            foreach (var w in model.Warnings) messages.Add($"{Path.GetFileName(path)}: {w}");
            var centre = mesh.Bounds is { } b ? (b.Min + b.Max) / 2 : model.Center;
            float radius = mesh.Bounds is { } bb ? bb.Radius : model.Radius, fileRadius = radius;
            // The LOD value is distance minus this radius. A few character meshes carry a stored radius many times their size (human_female:
            // 116 for a 20 unit tall body), which would keep them at full detail for another hundred units: the model's own extent bounds it.
            radius = Math.Min(radius, Math.Max(model.Radius, 1));
            // Hair and beards (HairLodScale) are alpha-tested cards: a collapse merges cards and strands go while the surface hardly moves, so the deviation underrates it.
            var levels = close ? [FullLevel(mesh)] : GeneratedLevels(path, mesh, model, radius, pixelsPerRadian * lodScale, LodSettings);
            // Shadow-only levels (the body, which has no cut-outs): built without the texture guard, whose refusals stop the body at about 1300 triangles, so a crowd's
            // shadows can be coarser than its picture; never picked by the picture (distance infinity), the depth pass chooses among them by ShadowDistances.
            float[]? shadowDistances = null;
            int shadowFirst = levels.Count;
            if (shadowLevels && !close && GeneratedLevels(path, mesh, model, radius, pixelsPerRadian, ShadowSettings) is { Count: > 1 } shadow)
            {
                shadowDistances = [.. shadow.Select(l => l.Distance)];
                levels.AddRange(shadow.Skip(1).Select(l => l with { Distance = float.PositiveInfinity }));
            }
            // The file's own reduced levels (Faithful character LOD, CharacterRenderer.GeneratedLod off): appended like the shadow-only ones, never picked by the
            // generated distances; manual levels (another mesh file) are left out (no character mesh has one, docs/characters.md).
            float[]? fileDistances = null;
            int fileFirst = levels.Count;
            if (!close && MeshLod.Levels(mesh) is { Count: > 1 } own && own.Skip(1).All(l => l.ManualMesh is null))
            {
                fileDistances = [.. own.Select(l => l.Distance)];
                levels.AddRange(own.Skip(1).Select(l => l with { Distance = float.PositiveInfinity }));
            }
            var decoded = new DecodedObjectMesh { Model = model, Levels = levels, Manual = [], Centre = centre, Radius = radius };
            var parts = model.Parts.Select(part => ObjectMeshCache.PreparePart(decoded, part, keepLevels: false)).ToArray();
            return new(new PreparedMesh(parts, [.. levels.Select(l => l.Distance)], mesh.SubMeshes.FirstOrDefault()?.MaterialName, centre, radius, morphs, shadowDistances, shadowFirst,
                PickBox.From(model), fileDistances, fileFirst, fileRadius,
                limbs is not null && !close && !icon ? CapMesh(model, head: false) : null, limbs is not null && !close && !icon ? CapMesh(model, head: true) : null), messages);
        }
        catch (Exception e) when (e is OgreFormatException or IOException or EndOfStreamException or InvalidDataException)
        {
            messages.Add($"{Path.GetFileName(path)}: {e.Message}");
            return new(null, messages);
        }
    }

    /// <summary>A mesh by file, uploaded the first time: prepared here unless a worker has done it (<see cref="Fetched"/>), or after waiting for the worker preparing it.</summary>
    LoadedMesh? Mesh(string path, int? boneCount, float lodScale = 1, bool shadowLevels = false, LimbHide? limbs = null, bool close = false, bool icon = false, string? partMap = null)
    {
        var key = (path.ToLowerInvariant(), icon ? IconBones : boneCount ?? -1, close, partMap?.ToLowerInvariant());
        if (meshes.TryGetValue(key, out var cached)) return cached;
        var (ready, messages) = Prepare(path, boneCount, lodScale, shadowLevels, LodPixelsPerRadian, limbs, close, icon, partMap).Value;
        prepared[key] = new Lazy<Prepared>(Uploaded);
        Messages.AddRange(messages);
        return meshes[key] = ready is null ? null : Upload(ready);
    }

    LoadedMesh Upload(PreparedMesh ready)
    {
        var gpuMesh = new GpuObjectMesh { Distances = ready.Distances, Manual = new GpuObjectMesh?[ready.Distances.Length], Centre = ready.Centre, Radius = ready.Radius };
        gpu.EnsureFrame();
        using var uploads = gpu.Uploads.Begin();
        foreach (var made in ready.Parts)
        {
            var part = made.Part;
            var packed = GpuVertex.PackCharacter(part.Vertices);
            int vertexBytes = packed.Length, indexBytes = made.All.Length * 4;
            var vertices = DeviceBuffer.Create(gpu, (ulong)vertexBytes, BufferUse.Vertex, AllocationName);
            var indices = DeviceBuffer.Create(gpu, (ulong)indexBytes, BufferUse.Index, AllocationName);
            uploads.Write(vertices, 0, packed);
            uploads.Write(indices, 0, MemoryMarshal.AsBytes(made.All.AsSpan()));
            var gp = new GpuObjectPart
            {
                SubMeshIndex = part.SubMeshIndex, MaterialName = part.MaterialName, HasTangents = part.HasTangents, HasColours = part.HasColours,
                Vertices = vertices, Indices = indices, Offset = made.Offset, Count = made.Count, UvScale = made.UvScale,
                Attributes = GpuVertex.CharacterAttributes(vertices), Stride = GpuVertex.CharacterSize,
            };
            gpuMesh.Parts.Add(gp);
            gpuMesh.Bytes += vertexBytes + indexBytes;
        }
        MeshBytes += gpuMesh.Bytes;
        return new LoadedMesh(gpuMesh, ready.FirstMaterialName, ready.Centre, ready.Radius, ready.Morphs, ready.ShadowDistances, ready.ShadowFirst, ready.PickBoxes,
            ready.FileDistances, ready.FileFirst, ready.FileRadius, ready.NeckCap, ready.HeadCap);
    }

    /// <summary>
    /// Whether the mesh <see cref="BuildIcon"/> draws <paramref name="item"/> with is read (so building it only uploads): the first call starts reading it on a worker, and the
    /// answer is false until that is done. True too when there is no mesh (<see cref="BuildIcon"/> reports it). Render thread.
    /// </summary>
    public bool IconMeshReady(GameRecord item)
    {
        // Asked again every frame while it waits: the first answer's reading is kept by item.
        if (iconReads.TryGetValue(item, out var started)) return started is null || started.IsValueCreated;
        string field = ItemIconStyle.MeshOf(item);
        if (field.Length == 0 || FindMesh(field) is not { } path || meshes.ContainsKey((path.ToLowerInvariant(), IconBones, true, null))) { iconReads[item] = null; return true; }
        var lazy = Prepare(path, null, 1, false, LodPixelsPerRadian, close: true, icon: true);
        iconReads[item] = lazy;
        if (lazy.IsValueCreated) return true;
        if (iconFetches.Add(path.ToLowerInvariant()))
            BackgroundWork.RunUrgent(() =>
            {
                try { _ = lazy.Value; }
                catch (Exception) { }   // a file that cannot be read: BuildIcon meets it again on the render thread and reports it
                return true;
            });
        return false;
    }

    /// <summary>Icon meshes whose reading has been started on a worker (<see cref="IconMeshReady"/>).</summary>
    readonly HashSet<string> iconFetches = [];
    /// <summary>The reading <see cref="IconMeshReady"/> waits on, by item (null: nothing to wait for).</summary>
    readonly Dictionary<GameRecord, Lazy<Prepared>?> iconReads = new(ReferenceEqualityComparer.Instance);

    readonly Dictionary<CharacterAppearance, Task> fetching = [];
    int fetchesRunning;
    /// <summary>How many appearances are prepared on workers at once (the workers are shared with the objects and the foliage).</summary>
    const int FetchLimit = 6;

    /// <summary>
    /// Whether <see cref="Build"/> will find the appearance's skeleton read and its meshes prepared, so that it only uploads. The first call starts that on a worker
    /// (at most <see cref="FetchLimit"/> ordinary requests at once) and the answer is false until it is done.
    /// Essential scene actors can bypass that cap and use the urgent queue. Render thread.
    /// </summary>
    public bool Fetched(CharacterAppearance c, bool urgent = false)
    {
        if (fetching.TryGetValue(c, out var task)) return task.IsCompleted;
        if (!urgent && Volatile.Read(ref fetchesRunning) >= FetchLimit) return false;
        // Every mesh prepared already (a look that only lost a limb or its head, or changed what it carries): built at once, so the character is not left undrawn a frame.
        if (AllPrepared(c, closeUp.Contains(c))) return true;
        if (Start(c, urgent, closeUp.Contains(c)) is not { } started) return true;   // no body mesh: Build reports it
        fetching[c] = started;
        return false;
    }

    /// <summary>
    /// Prepares <paramref name="c"/>'s skeleton and meshes on a worker without keeping the request (a look that may never be built: the character editor's next ones); nothing
    /// when they are all prepared already. Render thread.
    /// </summary>
    public void Warm(CharacterAppearance c, bool urgent, bool close)
    {
        if (!AllPrepared(c, close)) Start(c, urgent, close);
    }

    /// <summary>Whether <paramref name="c"/>'s body and part meshes are all prepared (or uploaded) already.</summary>
    bool AllPrepared(CharacterAppearance c, bool close) =>
        FindMesh(c.BodyMesh) is { } body && IsPrepared(body, close) && c.Parts.All(p => FindMesh(p.Mesh) is not { } path || IsPrepared(path, close));

    /// <summary>Looks to build as close-ups (<see cref="PrepareMesh"/>: their meshes without generated levels), from <see cref="Prefetch"/> until <see cref="Build"/>.</summary>
    readonly HashSet<CharacterAppearance> closeUp = [];

    /// <summary>Starts preparing <paramref name="c"/> urgently, as a close-up when <paramref name="close"/> (the character editor's look). Render thread.</summary>
    public void Prefetch(CharacterAppearance c, bool close)
    {
        if (close) closeUp.Add(c);
        Fetched(c, urgent: true);
    }

    /// <summary>
    /// The character editor has closed: the close-up preparations never uploaded (looks warmed ahead and never shown, every head, hair and beard of the races visited) are
    /// dropped with their CPU copies, and so are the close-up requests. Uploaded close-ups keep their marker. Render thread.
    /// </summary>
    public void ReleaseCloseUps()
    {
        closeUp.Clear();
        foreach (var (key, lazy) in prepared)
            if (key.Close && key.Bones != IconBones && !(lazy.IsValueCreated && ReferenceEquals(lazy.Value, Uploaded))) prepared.TryRemove(key, out _);
    }

    /// <summary>Whether a mesh file has been prepared (for any skeleton), as a close-up or with its levels.</summary>
    bool IsPrepared(string path, bool close)
    {
        string key = path.ToLowerInvariant();
        foreach (var (k, lazy) in prepared) if (k.Path == key && k.Close == close && lazy.IsValueCreated) return true;
        return false;
    }

    /// <summary>Starts preparing <paramref name="c"/>'s skeleton and meshes on a worker; null when it has no body mesh.</summary>
    Task? Start(CharacterAppearance c, bool urgent, bool close)
    {
        var bodyPath = FindMesh(c.BodyMesh);
        if (bodyPath is null) return null;
        var parts = c.Parts.Select(p => (Path: FindMesh(p.Mesh), Shared: p.Mode == AttachMode.SharedSkeleton, Scale: PartLodScale(c, p))).Where(p => p.Path is not null).ToList();
        var partMap = PartMapPath(c);
        float pixels = LodPixelsPerRadian;
        Interlocked.Increment(ref fetchesRunning);
        bool Fetch()
        {
            try
            {
                if (BodySkeletonName(bodyPath) is not { } name || Rig(name) is not { } rig) return true;
                _ = Prepare(bodyPath, rig.BoneCount, 1, true, pixels, LimbHideOf(c, rig), close, partMap: partMap).Value;
                foreach (var (path, shared, scale) in parts) _ = Prepare(path!, shared ? rig.BoneCount : null, scale, false, pixels, close: close).Value;
            }
            catch (Exception) { }   // a file that cannot be read: Build meets it again on the render thread and reports it
            finally { Interlocked.Decrement(ref fetchesRunning); }
            return true;
        }
        return urgent ? BackgroundWork.RunUrgent(Fetch) : BackgroundWork.Run(Fetch);
    }

    /// <summary>The head's entry in <see cref="LimbHide.LimbOfBone"/> (the limb slots are 0 to 3) and its body-vertex hide bit (between the part-map bits 4 to 13 and the part index from 16).</summary>
    internal const int HeadLimb = 4, HeadBit = 1 << 14;

    /// <summary>Which bone handle belongs to which limb slot (-1: none; <see cref="HeadLimb"/> the head) and the bind-pose heights below which a limb's vertices are its hideable part.</summary>
    internal sealed record LimbHide(int[] LimbOfBone, float ArmBelow, float LegBelow, int[] PartOfBone);

    /// <summary>
    /// The limb map of a person's body (<c>read_part_map</c> <c>FUN_140075600</c>, docs/characters.md "Hidden body parts"): each LOCATIONAL_DAMAGE of the race's <c>combat anatomy</c>
    /// claims its <c>bone name</c>, <c>bone name 2</c>, <c>bone name 3</c>; a bone no part names takes its nearest named ancestor's; an arm part is limb 0 or 1 and a leg 2 or 3 by
    /// whether its <c>collapse part</c> has a bit of 0x12 (the right side). Arms hide below 13.83 (male) or 14.1 (female), legs below 8.6. Null for an animal.
    /// </summary>
    LimbHide? LimbHideOf(CharacterAppearance c, SkeletonRig rig)
    {
        if (c.Animal is not null) return null;
        var claimed = new Dictionary<string, int>(StringComparer.Ordinal);
        var claimedPart = new Dictionary<string, int>(StringComparer.Ordinal);   // the same bones to the part's index in the health parts' order (MedicalState.Create: BodyPartTemplate.MedicalOrder, stable)
        var anatomy = c.Race.GetReferences("combat anatomy").Select(r => db.Find(r.TargetStringId)).Where(p => p is { Type: FcsRecordType.LOCATIONAL_DAMAGE }).Select(p => p!).ToList();
        var rank = anatomy.Select((p, i) => (p, i)).OrderBy(x => Meitou.Data.Gameplay.Bodies.BodyPartTemplate.From(x.p).MedicalOrder).Select((x, k) => (x.i, k)).ToDictionary(x => x.i, x => x.k);
        for (int partIndex = 0; partIndex < anatomy.Count; partIndex++)
        {
            var part = anatomy[partIndex];
            bool right = (part.GetInt("collapse part") & 0x12) != 0;
            int limb = part.GetInt("body part type") switch { 2 => right ? 1 : 0, 1 => right ? 3 : 2, 3 => HeadLimb, _ => -1 };
            foreach (var key in new[] { "bone name", "bone name 2", "bone name 3" })
                if (part.GetString(key) is { Length: > 0 } bone) { claimed.TryAdd(bone, limb); claimedPart.TryAdd(bone, rank[partIndex]); }
        }
        var bones = rig.Skeleton.Bones;
        var byHandle = bones.ToDictionary(b => (int)b.Handle);
        var map = new int[bones.Count == 0 ? 0 : bones.Max(b => b.Handle) + 1];
        Array.Fill(map, -1);
        var partMap = new int[map.Length];
        Array.Fill(partMap, -1);
        foreach (var b in bones)
            for (var x = b; x is not null; x = x.Parent is { } p && byHandle.TryGetValue(p, out var parent) ? parent : null)
                if (claimed.TryGetValue(x.Name, out int limb)) { (map[b.Handle], partMap[b.Handle]) = (limb, claimedPart[x.Name]); break; }
        return new LimbHide(map, c.Female ? 14.1f : 13.83f, 8.6f, partMap);
    }

    /// <summary>The file of the race's part map (<see cref="CharacterAppearance.PartMap"/>), found as Ogre finds it (by name through the resource and mod folders); null without one.</summary>
    string? PartMapPath(CharacterAppearance c) => c.PartMap is { } name ? assets.Find(name) : null;

    /// <summary>
    /// The part map's pixels (<c>FUN_140075600</c>, docs/characters.md "Hidden body parts"): null when it cannot be read, and for a DXT1 to DXT5 file, which the original
    /// refuses ("is a dxt format. Convert this to png instead.").
    /// </summary>
    static RgbaImage? PartMapImage(string path, List<string> messages)
    {
        try
        {
            var texture = TextureLoader.LoadFile(path, allMips: false);
            if (texture.Source?.Format is DdsFormat.Bc1 or DdsFormat.Bc2 or DdsFormat.Bc3) { messages.Add($"{Path.GetFileName(path)}: a part map in DXT format is not read (the original's rule)"); return null; }
            return texture.Levels[0];
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            messages.Add($"{Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes each body vertex's hide bits into its colour's Y (<c>FUN_140071f90</c>): bits 0 to 3 the limb of its heaviest bone, when the vertex lies below that limb's height
    /// (bit 14 for a head bone's vertex, Meitou's: <see cref="HeadBit"/>);
    /// from bit 4 the part-map bit of the texel under its UV (texel floor(size × uv), clamped; an exact colour of the table, alpha above 0; any other colour marks nothing).
    /// </summary>
    static void MarkBody(Model model, LimbHide? hide, RgbaImage? partMap)
    {
        foreach (var part in model.Parts)
            for (int i = 0; i < part.Vertices.Length; i++)
            {
                ref var v = ref part.Vertices[i];
                int bits = 0;
                if (hide is not null)
                {
                    int bone = -1;
                    float best = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = v.Weights[k];
                        if (w > best) { best = w; bone = (int)((v.Bones >> (8 * k)) & 0xFF); }
                    }
                    int limb = (uint)bone < (uint)hide.LimbOfBone.Length ? hide.LimbOfBone[bone] : -1;
                    // The head (Meitou decapitation): every vertex of its bones, bit 14; the neck is the chest's.
                    if (limb == HeadLimb) bits = HeadBit;
                    else if (limb >= 0 && v.Position.Y < (limb < 2 ? hide.ArmBelow : hide.LegBelow)) bits = 1 << limb;
                    // The part of the heaviest bone (the blood amounts are the first 8 parts', FUN_140530b30): bits 16 to 19, its index + 1.
                    int owner = (uint)bone < (uint)hide.PartOfBone.Length ? hide.PartOfBone[bone] : -1;
                    if ((uint)owner < 8) bits |= (owner + 1) << 16;
                }
                if (partMap is not null)
                {
                    int x = Math.Clamp((int)(partMap.Width * v.Uv.X), 0, partMap.Width - 1), y = Math.Clamp((int)(partMap.Height * v.Uv.Y), 0, partMap.Height - 1);
                    var (r, g, b, a) = partMap[x, y];
                    if (a > 0) bits |= PartBit(r << 16 | g << 8 | b) << 4;
                }
                v.Colour.Y = bits;
            }
    }

    /// <summary>The part-map bit of a colour (docs/characters.md "Hidden body parts"); 0 for black and for a colour not in the table (the original logs it as invalid).</summary>
    static int PartBit(int rgb) => rgb switch
    {
        0xFFFFFF => 0x1, 0xFF0000 => 0x2, 0x00FF00 => 0x4, 0x0000FF => 0x8, 0xFFFF00 => 0x10,
        0xFF00FF => 0x20, 0x00FFFF => 0x40, 0xFF8000 => 0x80, 0x8000FF => 0x100, 0x008080 => 0x200, _ => 0,
    };

    /// <summary>
    /// Writes each vertex's curvature (1 / radius, world units) into its colour's Z, for the Meitou skin (docs/character-renderer.md "Skin"): over the edges at the vertex,
    /// the mean of how far the normal turns per unit of length, then averaged over the vertices that share its position (a UV seam must not show). Baked per vertex and
    /// interpolated, it is smooth across triangles; a screen-space estimate is constant per triangle and drew the low-poly faceting in red.
    /// </summary>
    internal static void MarkCurvature(Model model)
    {
        foreach (var part in model.Parts)
        {
            var v = part.Vertices;
            var sum = new float[v.Length];
            var count = new int[v.Length];
            var idx = part.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    uint a = idx[t + e], b = idx[t + (e + 1) % 3];
                    float length = Vector3.Distance(v[a].Position, v[b].Position);
                    if (length < 1e-5f) continue;
                    float k = Vector3.Distance(Vector3.Normalize(v[a].Normal), Vector3.Normalize(v[b].Normal)) / length;
                    sum[a] += k; count[a]++;
                    sum[b] += k; count[b]++;
                }
            static Vector3 Key(Vector3 p) => new(MathF.Round(p.X, 3), MathF.Round(p.Y, 3), MathF.Round(p.Z, 3));
            var byPosition = new Dictionary<Vector3, (float Sum, int Count)>();
            for (int i = 0; i < v.Length; i++)
                byPosition[Key(v[i].Position)] = byPosition.TryGetValue(Key(v[i].Position), out var s) ? (s.Sum + sum[i], s.Count + count[i]) : (sum[i], count[i]);
            for (int i = 0; i < v.Length; i++)
            {
                var s = byPosition[Key(v[i].Position)];
                v[i].Colour.Z = s.Count > 0 ? s.Sum / s.Count : 0;
            }
        }
    }

    /// <summary>How much farther a part's generated levels start (<see cref="HairLodScale"/> for a person's hair and beard).</summary>
    static float PartLodScale(CharacterAppearance c, CharacterPart part) => part.Record.Type == FcsRecordType.ATTACHMENT && c.Animal is null ? HairLodScale : 1;

    /// <summary>The steps (share of a part's triangles), floor per part, limits and guards the character levels are built with: the objects' limits, and collapses keep the
    /// texture coordinates (without that a face's eyes and mouth smeared across their UV islands from the second level on).</summary>
    static readonly FoliageLodBuilder.Settings LodSettings = new([0.5f, 0.25f, 0.12f, 0.05f], 40, 0.25f, 3.2f, 0.85f, true);

    /// <summary>The shadow-only levels' settings: as <see cref="LodSettings"/> without the texture guard (a shadow map shows no texture), down to 5 %.</summary>
    static readonly FoliageLodBuilder.Settings ShadowSettings = LodSettings with { UvAware = false };

    /// <summary>How much farther the levels of hair and beards start: reduced at the rule's distance their dark strands thinned visibly at 200 units; never reduced they were
    /// about 650k of a 500-character crowd's 2.0M triangles.</summary>
    internal const float HairLodScale = 4;

    /// <summary>
    /// Mesh levels generated by edge collapse from level 0 for every mesh, because the files' own are few and far (human_female has none, the male body's only level starts at
    /// 200 units). Each level starts where its deviation from the original is under the objects' tolerance in rendered pixels (<see cref="ObjectLodGen.Distances"/>, measured in
    /// the bind pose at <paramref name="pixelsPerRadian"/>), so a face or a weapon keeps its detail as long as it would show.
    /// </summary>
    /// <summary>A mesh's own triangles, its full level.</summary>
    static MeshLodLevel FullLevel(OgreMesh mesh) => new(0, mesh.SubMeshes.Select(s => (uint[]?)s.Indices.Indices).ToList());

    static List<MeshLodLevel> GeneratedLevels(string path, OgreMesh mesh, Model model, float radius, float pixelsPerRadian, FoliageLodBuilder.Settings settings)
    {
        var levels = new List<MeshLodLevel> { FullLevel(mesh) };
        var parts = model.Parts.Where(p => p.Indices.Length > 0).ToList();
        if (parts.Count == 0 || BuildLevels(path, model, parts, radius, settings) is not { } set) return levels;
        var distances = ObjectLodGen.Distances(set, ObjectLodGen.TriangleExtent(parts), radius, pixelsPerRadian);
        for (int k = 1; k < set.Levels; k++)
        {
            var subs = new uint[]?[mesh.SubMeshes.Count];
            for (int p = 0; p < parts.Count; p++) subs[parts[p].SubMeshIndex] = set.Indices[p][k - 1];
            levels.Add(new MeshLodLevel(distances[k], subs));
        }
        return levels;
    }

    /// <summary>
    /// The disk cache of the generated levels, <c>%LOCALAPPDATA%\Meitou\charlods</c> (<c>MEITOU_CHARACTER_LOD_CACHE</c>; neither read nor written with <c>--no-load-cache</c>), as
    /// the objects' (<see cref="ObjectLodGen.Root"/>): building the levels is nearly all of a mesh's preparation, and a town's crowd took about 40 s of worker time at every load.
    /// </summary>
    public static string LodRoot { get; set; } = Environment.GetEnvironmentVariable("MEITOU_CHARACTER_LOD_CACHE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "charlods");

    /// <summary>Bumped when what the character levels are built from changes (with <see cref="FoliageLodBuilder.Version"/>, the cache's key).</summary>
    const int LodVersion = 1;

    /// <summary>
    /// <see cref="FoliageLodBuilder.Build(Model, float, FoliageLodBuilder.Settings)"/> through <see cref="LodRoot"/>, keyed by all the build reads: the parts' vertices and
    /// indices, the radius and the settings (a mesh read for another bone count or limb marking has an entry of its own).
    /// </summary>
    static FoliageLodSet? BuildLevels(string path, Model model, List<ModelPart> parts, float radius, FoliageLodBuilder.Settings settings)
    {
        if (Meitou.Data.World.LoadCaches.Disabled) return FoliageLodBuilder.Build(model, radius, settings);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            hash.AppendData(MemoryMarshal.AsBytes(part.Vertices.AsSpan()));
            hash.AppendData(MemoryMarshal.AsBytes(part.Indices.AsSpan()));
        }
        string Number(float f) => f.ToString("R", CultureInfo.InvariantCulture);
        hash.AppendData(Encoding.UTF8.GetBytes($"{Number(radius)} {string.Join(',', settings.Fractions.Select(Number))} {settings.Floor} {Number(settings.MaxRelativeError)} " +
            $"{Number(settings.MaxNormalAngle)} {Number(settings.MinGain)} {settings.UvAware} {settings.TwoSided}"));
        int version = FoliageLodBuilder.Version * 100 + LodVersion;
        // The file is named by the mesh and a hash of that key (PathFor hashes the bytes it is given).
        string file = FoliageLodCache.PathFor(path, hash.GetHashAndReset(), LodRoot, version);
        var (hit, cached) = FoliageLodCache.TryLoad(file, parts.Count, version);
        if (hit) return cached;
        var built = FoliageLodBuilder.Build(model, radius, settings);
        FoliageLodCache.Save(file, built, parts.Count, version);
        return built;
    }

    /// <summary>Assembles the asset for <paramref name="c"/>; null when its body mesh or skeleton cannot be found.</summary>
    public CharacterAsset? Build(CharacterAppearance c)
    {
        fetching.Remove(c);
        bool close = closeUp.Remove(c);
        var bodyPath = FindMesh(c.BodyMesh);
        if (bodyPath is null) { Messages.Add($"body mesh not found: {c.BodyMesh}"); return null; }
        // The body's own skeleton link (Kenshi shares that instance with worn items); the rig's bone count decides the weights kept.
        string? skeletonName;
        try { skeletonName = BodySkeletonName(bodyPath); }
        catch (Exception e) when (e is OgreFormatException or IOException or EndOfStreamException or InvalidDataException) { Messages.Add($"{c.BodyMesh}: {e.Message}"); return null; }
        var rig = skeletonName is null ? null : Rigs.Rig(skeletonName, c);   // with the animation files it links (docs/animation.md)
        if (rig is null) { Messages.Add($"{c.BodyMesh} has no skeleton ({skeletonName})"); return null; }
        var body = Mesh(bodyPath, rig.BoneCount, shadowLevels: true, limbs: LimbHideOf(c, rig), close: close, partMap: PartMapPath(c));
        if (body is null) return null;

        var asset = new CharacterAsset { Appearance = c, Rig = rig, Animal = c.Animal is not null };
        if (asset.Animal)
        {
            // An animal's sphere from its body's bounds (a leviathan is 190 units tall): centred above the feet, grown by the bounds' offset across the ground so any heading fits.
            asset.Height = Math.Max(2 * body.Centre.Y, 1);
            asset.Radius = body.Radius + new Vector2(body.Centre.X, body.Centre.Z).Length();
        }
        var setup = Rigs.For(c);
        asset.Shape = setup?.Shape;
        asset.GroundOffset = setup?.GroundOffset ?? 0;
        asset.HeadBone = rig.Handle("Bip01 Head") ?? -1;
        if (body.Morphs is { } bodyMorphs)
        {
            asset.Morph = new MorphSource(bodyMorphs, c.PoseWeights, c.Character?.GetBool("shaved") == true && bodyMorphs.HasCuttableHorns);
            if (bodyMorphs.Deltas(c.PoseWeights, asset.Morph.CutHorns) is { } deltas)
            {
                var key = string.Join(';', c.PoseWeights.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => FormattableString.Invariant($"{p.Key}={p.Value}"))) + "@" + bodyPath + (c.Character?.GetBool("shaved") == true);
                asset.MorphBase = Morphs.Add(key, deltas);
            }
        }
        asset.Parts.Add(new AssetPart
        {
            Label = $"body {Path.GetFileName(bodyPath)}", Mesh = body.Gpu, Shared = true, Material = BodyMaterial(c),
            BoundsCentre = body.Centre, BoundsRadius = body.Radius, PickBoxes = body.PickBoxes ?? [],
            Morphed = asset.MorphBase != 0, ShadowDistances = body.ShadowDistances, ShadowFirst = body.ShadowFirst, IsBody = true,
            FileDistances = body.FileDistances, FileFirst = body.FileFirst, FileRadius = body.FileRadius,
        });
        // A headless body's neck, and a cut-off head's, is closed by a cap made from the body (NeckCap), in the race's wound look.
        if ((c.Headless || c.HeadOnly) && NeckCapMesh(bodyPath, body, rig.BoneCount, c.HeadOnly) is { } cap)
            asset.Parts.Add(new AssetPart
            {
                Label = $"neck cap {Path.GetFileName(bodyPath)}", Mesh = cap.Gpu, Shared = true, Material = WoundMaterial(c),
                BoundsCentre = cap.Centre, BoundsRadius = cap.Radius, Morphed = asset.MorphBase != 0,
            });
        foreach (var part in c.Parts)
        {
            var path = FindMesh(part.Mesh);
            if (path is null) continue;
            bool shared = part.Mode == AttachMode.SharedSkeleton;
            // An animal's fur is a shell over the whole body (5.5k triangles on the bonedog, more than its body), not a few hair cards: it takes the rule's own distances.
            var loaded = Mesh(path, shared ? rig.BoneCount : null, PartLodScale(c, part), close: close);
            if (loaded is null || loaded.Gpu.Parts.Count == 0) continue;
            var ap = new AssetPart
            {
                Label = $"{part.Record.Type} '{part.Record.Name}' .{part.Field}", Mesh = loaded.Gpu, Shared = shared, Material = ItemMaterial(c, part, path, loaded),
                BoundsCentre = loaded.Centre, BoundsRadius = loaded.Radius, Shown = part.Shown, ShownShots = part.ShownShots, Muzzle = part is { Field: "live ammo", ShownShots: 1, Shown: WeaponShown.RangedDrawn }, HideSide = part.HideSide, PickBoxes = loaded.PickBoxes ?? [],
                FileDistances = loaded.FileDistances, FileFirst = loaded.FileFirst, FileRadius = loaded.FileRadius,
            };
            if (!shared)
            {
                // Mode 0: attachObjectToBone at the attachment point; a bone the skeleton lacks falls back to the root.
                var point = part.Point is { } pn && c.AttachmentPoints.TryGetValue(pn, out var a) ? a : null;
                int bone = point is null ? -1 : rig.Handle(point.Bone) ?? -1;
                if (bone < 0) bone = rig.Skeleton.Bones.FirstOrDefault(b => b.Parent is null)?.Handle ?? 0;
                ap.Bone = bone;
                if (point is not null) ap.Offset = Matrix4x4.CreateFromQuaternion(point.Rotation) * Matrix4x4.CreateTranslation(point.Position + part.PointOffset);
                if (part.Local is { } local) ap.Offset = local * ap.Offset;
                ap.AttachIndex = asset.AttachCount++;
            }
            asset.Parts.Add(ap);
        }
        // A carried light hangs at its binding-pose position, held relative to its bone (a bone the skeleton lacks: the root).
        foreach (var carried in c.Lights)
        {
            int bone = rig.Handle(carried.Bone) ?? rig.Skeleton.Bones.FirstOrDefault(b => b.Parent is null)?.Handle ?? 0;
            var local = Vector3.Transform(carried.Light.Position - rig.BindPosition[bone], rig.BindRotationInverse[bone]) / rig.BindScale[bone];
            asset.Lamps.Add((bone, Matrix4x4.CreateTranslation(local), asset.AttachCount++, carried.Light));
        }

        if (setup is not null)
        {
            asset.Idle = setup.Idle;
            asset.Fixed.AddRange(setup.Fixed);
        }
        return asset;
    }

    // ---- materials (CharacterScene.BodyMaterial / PartMaterial) ----

    static Vector4 Channel(int channel) => channel switch { 1 => new(0, 1, 0, 0), 2 => new(0, 0, 1, 0), 3 => new(0, 0, 0, 1), _ => new(1, 0, 0, 0) };

    static string? Tex(GameRecord? r, string field) => r?.GetPath(field) is { Length: > 0 } p ? p : null;

    WorldTexture? Texture(string? name, bool border) => name is null ? null : Textures.Get(name, border, deferred: true);

    /// <summary>A body texture by file (bordered, as the body's own), streamed in like the rest.</summary>
    public WorldTexture? BodyTexture(string? name) => Texture(name, true);

    /// <summary>The body blood layer (docs/game/blood-decals.md "On the body and on severed limbs"): the race's blood colour and scales and the splatter map; none for an animal or a bloodless race.</summary>
    void BodyBlood(PartMaterial m, CharacterAppearance c)
    {
        if (c.Animal is not null || c.Race.GetFloat("bleed rate", 1) <= 0) return;
        m.Bloody = true;
        m.Colour1 = new Vector4(CharacterAppearance.Rgb(c.Race.GetInt("blood colour", 0x800000) & 0xFFFFFF), 1);
        m.Colour2 = new Vector4(c.Race.GetFloat("blood horizontal", 1), c.Race.GetFloat("blood vertical", 0.15f), 0, 0);
        m.Tex[CharacterShaders.SlotBlood] = Texture("bloody.png", false);
    }

    /// <summary>
    /// The neck cap's look (Meitou decapitation): a wound in the race's <c>blood colour</c> splattered by <c>bloody.png</c> over its planar coordinates, flesh with a bone at
    /// the centre, or for a race with <c>is robot</c> (skeletons; a mod's mechanical race the same way) a metal socket with the fluid over it (<see cref="CharacterShaders.Wound"/>).
    /// </summary>
    PartMaterial WoundMaterial(CharacterAppearance c)
    {
        var m = new PartMaterial
        {
            Shading = CharacterShaders.ShadeItem, Description = $"RACE '{c.Race.Name}' neck wound", Wound = true, Mechanical = c.Race.GetBool("is robot"),
            Colour1 = new Vector4(CharacterAppearance.Rgb(c.Race.GetInt("blood colour", 0x800000) & 0xFFFFFF), 1),
        };
        m.Tex[CharacterShaders.SlotDiffuse] = Texture("bloody.png", false);
        return m;
    }

    /// <summary>
    /// The neck cap of a body mesh (<see cref="NeckCap"/>; <paramref name="head"/>: the cut-off head's), prepared on the worker with the body (<see cref="PrepareMesh"/>) and uploaded
    /// the first time a look needs it; null when the body has no head vertices.
    /// </summary>
    LoadedMesh? NeckCapMesh(string bodyPath, LoadedMesh body, int bones, bool head)
    {
        var key = (bodyPath.ToLowerInvariant() + (head ? "#head cap" : "#neck cap"), bones, false, (string?)null);
        if (meshes.TryGetValue(key, out var cached)) return cached;
        return meshes[key] = (head ? body.HeadCap : body.NeckCap) is { } ready ? Upload(ready) : null;
    }

    /// <summary>A body's cap (<see cref="NeckCap"/>) ready for upload, from the body's model as <see cref="PrepareMesh"/> marked it (its face-morph slots and head bits); null: none.</summary>
    static PreparedMesh? CapMesh(Model body, bool head)
    {
        if (NeckCap.Build(body, head) is not { } part) return null;
        var min = part.Vertices.Aggregate(new Vector3(float.MaxValue), (a, v) => Vector3.Min(a, v.Position));
        var max = part.Vertices.Aggregate(new Vector3(float.MinValue), (a, v) => Vector3.Max(a, v.Position));
        var model = new Model();
        model.Parts.Add(part);
        var decoded = new DecodedObjectMesh { Model = model, Levels = [new MeshLodLevel(0, [part.Indices])], Manual = [], Centre = (min + max) / 2, Radius = Math.Max((max - min).Length() / 2, 0.5f) };
        return new PreparedMesh([ObjectMeshCache.PreparePart(decoded, part, keepLevels: false)], [0], "neck cap", decoded.Centre, decoded.Radius, null, null, 0, [], null, 0, decoded.Radius);
    }

    PartMaterial BodyMaterial(CharacterAppearance c)
    {
        string f = c.Female ? " female" : "";
        // Shirt colour for vest colour masks: the first dyed vest's colour, as its meshes would be (the shader has one; the original's vest colour source is Unknown).
        var colour = c.BodyLayers.Take(3).Select(l => l.Colour).FirstOrDefault(x => x is not null);
        var m = new PartMaterial
        {
            Shading = CharacterShaders.ShadeBody, Description = $"RACE '{c.Race.Name}' body",
            SkinTone = c.SkinToneParameter, HairColour = c.HairColour,
            HairAlpha = Channel(c.Hair?.GetInt("head alpha channel" + f, 1) ?? 1), HairMult = Channel(c.Hair?.GetInt("head channel" + f) ?? 0),
            BeardAlpha = Channel(c.Beard?.GetInt("head alpha channel" + f, 1) ?? 1),
            ShirtColour = colour is null ? Vector3.One : CharacterAppearance.Rgb(colour.GetInt("color 1")), HasShirtColour = colour is not null,
            // Animals: fur and hide; a robot race (RACE `is robot`, skeletons) is off by default (docs/character-renderer.md "Skin").
            Skin = c.Animal is null, SkinDefault = CharacterRenderer.SkinByDefault(c.Race), Race = c.Race.StringId,
        };
        // Animal meshes put the mirrored half of the body at u 1..2 (goat, bull, beak thing: Observed, the meshes' texture coordinates), so their textures repeat; a
        // person's body texture has the transparent border (the head sits beside it at v -1..0). That Kenshi repeats them is an engine reading (the animal material was not read).
        m.Tex[CharacterShaders.SlotDiffuse] = Texture(c.BodyTexture, c.Animal is null);
        m.Tex[CharacterShaders.SlotNormal] = Texture(c.BodyNormal, c.Animal is null);
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
            m.VestColoured[i] = layer.ColourMap is not null && layer.Colour is not null;
        }
        m.Swizzled = true;
        BodyBlood(m, c);
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
        m.Worn = worn;
        // Clothing: MATERIAL_SPECS_CLOTHING from "material" (female: "material female" if set), likeliest first; dye the part's colour
        // (CharacterPart.Colour: the instance's, else the wearer's scheme, none for "dont colorise").
        var refs = item.GetReferences(c.Female && item.GetReferences("material female").Count > 0 ? "material female" : "material");
        var spec = refs.OrderByDescending(r => r.Values.Value0).Select(r => db.Find(r.TargetStringId))
            .FirstOrDefault(s => s is { Type: FcsRecordType.MATERIAL_SPECS_CLOTHING or FcsRecordType.MATERIAL_SPEC or FcsRecordType.MATERIAL_SPECS_WEAPON });
        // A generated loadout chose the clothing material (from "material"; "material female" still wins for women).
        if (part.Material is { Type: FcsRecordType.MATERIAL_SPECS_CLOTHING } chosen && !(c.Female && item.GetReferences("material female").Count > 0))
            spec = chosen;
        if (spec is not null)
        {
            var dye = part.Colour;
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
        // A limb replacement without a material (the races' stumps, mesh material "default") wears the body's own material: its textures and skin tone
        // (AppearanceBase::setupItemMaterial, FUN_140536210: no material record and type 111 sets the appearance's body material). Prosthetics have one.
        if (item.Type == FcsRecordType.LIMB_REPLACEMENT)
        {
            var body = BodyMaterial(c);
            body.Description = $"RACE '{c.Race.Name}' body (stump '{item.Name}')";
            // A severed part is far below its health, so the original's per-part blood amount (FUN_140530b30) is 1: the stump is covered in blood (docs/game/blood-decals.md "On the body").
            body.BloodFull = body.Bloody;
            return body;
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
        var candidate = loaded.FirstMaterialName is null ? null : resolver.Candidates(path, new OgreSubMesh { MaterialName = loaded.FirstMaterialName }).FirstOrDefault();
        m.Description = candidate?.Description ?? "untextured";
        m.Tex[CharacterShaders.SlotDiffuse] = Texture(candidate?.Diffuse, false);
        m.Tex[CharacterShaders.SlotNormal] = Texture(candidate?.Normal, false);
        m.DoubleSided = candidate?.DoubleSided ?? false;
        return m;
    }

    // ---- item icons ----

    /// <summary>The material record an icon is drawn with (<see cref="ItemIcons.MaterialOf"/>).</summary>
    public GameRecord? IconMaterialOf(GameRecord item, string materialId) => ItemIcons.MaterialOf(db, item, materialId);

    /// <summary>
    /// The asset of <paramref name="item"/>'s inventory icon (docs/game/ui-screens.md "Item icons"): its mesh (a weapon's bare blade) unskinned, with the textures of <paramref name="spec"/>,
    /// dyed with <paramref name="dye"/> (a COLOR_DATA; when the spec has a <c>color map</c>) as worn clothing is. Null when the mesh is missing or unreadable.
    /// </summary>
    public CharacterAsset? BuildIcon(GameRecord item, GameRecord? spec, GameRecord? dye = null)
    {
        string field = ItemIconStyle.MeshOf(item);
        if (field.Length == 0 || FindMesh(field) is not { } path) { Messages.Add($"icon mesh not found: {field}"); return null; }
        // Only the full level: an icon is drawn once, up close (generating the others is nearly all of a mesh's preparation).
        var loaded = Mesh(path, null, close: true, icon: true);
        if (loaded is null || loaded.Gpu.Parts.Count == 0) return null;
        var m = new PartMaterial { Shading = CharacterShaders.ShadeItem, VertexColoured = loaded.Gpu.Parts[0].HasColours };
        if (spec is not null)
        {
            m.Description = $"icon {spec.Type} '{spec.Name}'";
            m.GlossMult = spec.GetFloat("specular mult", 1);
            m.Tex[CharacterShaders.SlotDiffuse] = Texture(Tex(spec, "texture map"), false);
            m.Tex[CharacterShaders.SlotNormal] = Texture(Tex(spec, "normal map"), false);
            m.ClipOnNormalAlpha = Tex(spec, "normal map") is not null;
            m.DoubleSided = spec.GetInt("material type") == 2;
            if (dye is { Type: FcsRecordType.COLOR_DATA } && Tex(spec, "color map") is not null)
            {
                m.Description += $", dye '{dye.Name}'";
                m.Tex[CharacterShaders.SlotColourMap] = Texture(Tex(spec, "color map"), false);
                m.Dyed = true;
                // The icon's dye has no paint factor: the game's colour alpha is the COLOR_DATA's top byte, 0 in every record.
                m.Colour1 = new Vector4(CharacterAppearance.Rgb(dye.GetInt("color 1")), 0);
                m.Colour2 = new Vector4(CharacterAppearance.Rgb(dye.GetInt("color 2")), 0);
            }
        }
        else
        {
            var candidate = loaded.FirstMaterialName is null ? null : resolver.Candidates(path, new OgreSubMesh { MaterialName = loaded.FirstMaterialName }).FirstOrDefault();
            m.Description = candidate?.Description ?? "untextured";
            m.Tex[CharacterShaders.SlotDiffuse] = Texture(candidate?.Diffuse, false);
            m.Tex[CharacterShaders.SlotNormal] = Texture(candidate?.Normal, false);
            m.ClipOnNormalAlpha = candidate?.Normal is not null;
            m.DoubleSided = candidate?.DoubleSided ?? false;
        }
        var asset = new CharacterAsset { Appearance = null!, Rig = null!, IsIcon = true, AttachCount = 1 };
        asset.Parts.Add(new AssetPart
        {
            Label = $"icon {item.Type} '{item.Name}'", Mesh = loaded.Gpu, Shared = false, Material = m, AttachIndex = 0,
            BoundsCentre = loaded.Centre, BoundsRadius = loaded.Radius,
        });
        return asset;
    }
}

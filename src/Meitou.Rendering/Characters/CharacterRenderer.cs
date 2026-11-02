using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;
using Meitou.Rendering.Gpu;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

/// <summary>
/// Draws the characters of a <see cref="CharacterDrawList"/> (docs/character-renderer.md): skinned meshes with their clothing, hair,
/// head and bone-attached weapons, posed from the animation layers the host gives, lit like the world's buildings.
/// <list type="bullet">
/// <item><see cref="Update"/> once per frame, before the shadow cascades: asks the host's <see cref="Source"/> for the list, builds assets for
/// appearances not seen yet, poses every character in range in parallel into the frame's bone-palette buffer, and writes the materials of the
/// parts in view.</item>
/// <item><see cref="Draw"/> / <see cref="DrawDepth"/> for each view (the scene's near slice, a shadow cascade): cull, pick each part's mesh LOD, group
/// the instances by (mesh part, level) and record one instanced draw each.</item>
/// </list>
/// </summary>
internal sealed unsafe partial class CharacterRenderer : IDisposable
{
    IGpuContext Gpu { get; }

    /// <summary>
    /// Zeroed host-visible memory of this frame (the bone palettes, the material table and the instances are written into it), cleared in place: a managed
    /// array of this size (megabytes with a crowd) would land on the large object heap every frame and force gen2 collections.
    /// </summary>
    Transient AllocateConstants(ulong bytes, ulong alignment)
    {
        var slice = Gpu.Frame.AllocateConstants(Math.Max(bytes, 1), alignment);
        slice.Bytes.Clear();
        return slice;
    }

    readonly NativeFrame nativeFrame;
    readonly ReflectedProgram colourProg, depthProg, motionProg;
    readonly CharacterContent content;
    readonly PassTimer colourTimer, depthTimer;
    readonly Dictionary<CharacterAppearance, CharacterAsset?> assets = new(ReferenceEqualityComparer.Instance);

    /// <summary>The characters of this frame: filled by <see cref="Source"/> inside <see cref="Update"/>, or by the host before it.</summary>
    public CharacterDrawList Draws { get; } = new();
    /// <summary>Called at the start of <see cref="Update"/> with the (cleared) list to fill; null: the host fills <see cref="Draws"/> itself.</summary>
    public Action<CharacterDrawList>? Source { get; set; }

    /// <summary>Characters farther than this from the eye are neither posed nor drawn.</summary>
    public float DrawDistance { get; set; } = 6000;
    /// <summary>While the eye is above the game's camera heights (<see cref="EyeLift"/>): characters farther than this are neither built, posed nor drawn
    /// (engine choice: a few pixels tall there).</summary>
    public float LiftedReach { get; set; } = 3000;
    /// <summary>The ground's height under the eye (set each frame; unknown: none), for <see cref="LiftedReach"/>.</summary>
    public float GroundUnderEye { get; set; } = float.NegativeInfinity;
    /// <summary>A character flight's arrival eye and the player's kept points: characters within <see cref="KeptReach"/> of them are built ahead (their
    /// meshes and textures), the eye's own being only those it can draw.</summary>
    public Vector3? Ahead { get; set; }
    public IReadOnlyList<Vector3> Kept { get; set; } = [];
    public float KeptReach { get; set; } = 3000;
    /// <summary>The camera's distance to its target: a kept character's texture needs count from at least this far (the view a jump there would have).</summary>
    public float KeptViewDistance { get; set; }
    /// <summary>Places kept loaded (<see cref="WorldTextureCache.Areas"/>): the character textures' mark (and so <see cref="CharacterTextureBudget"/>) scales with it.</summary>
    public int Areas { set => content.Textures.Areas = value; }
    /// <summary>Built characters out of the eye's reach but within <see cref="KeptReach"/> of a kept point, with that distance: their textures are offered
    /// (and so marked used) as if seen from there, or the cache's idle limit unloaded them before a jump there.</summary>
    readonly List<(CharacterAsset Asset, float Distance)> keptLooks = [];
    float NearKept(Vector3 p)
    {
        float d = float.PositiveInfinity;
        var kept = Kept;
        for (int k = 0; k < kept.Count; k++) d = MathF.Min(d, Vector2.Distance(new Vector2(kept[k].X, kept[k].Z), new Vector2(p.X, p.Z)));
        return d;
    }
    /// <summary>Added to the reach for building, so a character is ready a little before it is drawn.</summary>
    const float BuildMargin = 300;

    /// <summary>Whether <paramref name="c"/>'s look is built: within <paramref name="reach"/> (and the margin) of the eye, or within <see cref="KeptReach"/> of
    /// the arrival or a kept point. Every loaded character is in the draw list; built all, they loaded the textures of the whole loaded world.</summary>
    bool WantsAsset(in CharacterInstance c, Vector3 eye, float reach)
    {
        if (Vector3.Distance(eye, c.Position) <= reach + BuildMargin) return true;
        var at = new Vector2(c.Position.X, c.Position.Z);
        if (Ahead is { } a && Vector2.Distance(new Vector2(a.X, a.Z), at) <= KeptReach) return true;
        var kept = Kept;
        for (int k = 0; k < kept.Count; k++) if (Vector2.Distance(new Vector2(kept[k].X, kept[k].Z), at) <= KeptReach) return true;
        return false;
    }
    /// <summary>A presentation fill light (linear colour, 0 in play): added ambient, three times as strong at the silhouette, so a backlit figure reads.</summary>
    public Vector3 Fill { get; set; }
    /// <summary>Assets built per <see cref="Update"/> (0: all of them, for stills), and no new one once <see cref="BuildBudgetMs"/> went on them.</summary>
    public int LoadBudget { get; set; } = 4;
    /// <summary>
    /// A frame's building time after which it starts no other (the first is always built). A new appearance takes 20-40 ms on the render thread (its item materials and
    /// race rig), and four of them in one frame of a town filling up held it 150 ms (docs/bench.md "Play benchmark").
    /// </summary>
    public const double BuildBudgetMs = 4;
    /// <summary>
    /// The view is under the loading screen (the host sets it each frame): its frames need not be smooth, so textures upload and appearances build for up to
    /// <see cref="CoveredBudgetMs"/> a frame instead of the play budgets, whose trickle of texture uploads held a town's loading screen up 10 s.
    /// </summary>
    public bool Covered { get; set; }
    const double CoveredBudgetMs = 16;
    /// <summary>Ogre's camera LOD bias as a distance factor (above 1: coarser levels sooner).</summary>
    public float LodBias { get; set; } = 1;
    /// <summary>Further factor on the LOD value in the shadow cascades (a shadow hides detail: coarser levels sooner).</summary>
    public float ShadowLodBias { get; set; } = 2;
    /// <summary>
    /// Meitou's generated levels (four reductions, the shadows' own bias and floors; the <c>charlod</c> switch's Meitou side); off: the mesh files' own levels as the
    /// original picks them (Faithful).
    /// </summary>
    public bool GeneratedLod { get; set; } = true;

    /// <summary>
    /// Pre-integrated skin scattering on people's bare skin (the <c>skin</c> switch's Meitou side, docs/character-renderer.md "Skin"); off: the plain N.L of the game.
    /// A change redraws the portraits.
    /// </summary>
    public bool SkinShading
    {
        get => skinShading;
        set
        {
            if (skinShading == value) return;
            skinShading = value;
            portraitShown.Clear();
            materialEpoch++;
        }
    }
    bool skinShading = true;

    /// <summary>The skin scattering table's bindless index while the Skin switch is on, else 0 (a severed limb's skin: <see cref="DebrisRenderer"/>).</summary>
    public uint SkinTable => SkinShading ? skinScattering.Index : 0;

    /// <summary>Whether the skin shading applies to the race <paramref name="race"/> (RACE string id): the Skin page's choice, else <paramref name="byDefault"/>.</summary>
    public bool SkinOn(string? race, bool byDefault) => race is not null && skinRaces is not null && skinRaces.TryGetValue(race, out bool chosen) ? chosen : byDefault;
    readonly SkinScattering skinScattering;

    /// <summary>
    /// The skin per race, chosen against its default (RACE string id to on or off; the settings' Skin page): a race not in it takes <see cref="SkinByDefault"/>.
    /// Read as the materials are written each frame, so a change shows at once; setting it (also the same, changed, dictionary) redraws the portraits.
    /// </summary>
    public IReadOnlyDictionary<string, bool>? SkinRaces
    {
        get => skinRaces;
        set { skinRaces = value; portraitShown.Clear(); materialEpoch++; }
    }
    IReadOnlyDictionary<string, bool>? skinRaces;

    /// <summary>Whether a race's bodies scatter light unless chosen otherwise: every person race but a robot one (RACE <c>is robot</c>: skeletons, metal).</summary>
    public static bool SkinByDefault(GameRecord race) => !race.GetBool("is robot");

    bool SkinFor(PartMaterial m) => m.Race is not null && skinRaces is not null && skinRaces.TryGetValue(m.Race, out bool chosen) ? chosen : m.SkinDefault;

    /// <summary>
    /// The person races of the loaded data (base game and mods) for the Skin page: every RACE record no ANIMAL_CHARACTER uses, by name, with its default.
    /// </summary>
    public static IReadOnlyList<(string Id, string Name, bool Default)> PersonRaces(GameDatabase db)
    {
        var animal = db.Records.Values.Where(r => r.Type == Meitou.Data.Fcs.FcsRecordType.ANIMAL_CHARACTER)
            .SelectMany(r => r.GetReferences("race").Select(x => x.TargetStringId)).ToHashSet();
        return [.. db.Records.Values.Where(r => r.Type == Meitou.Data.Fcs.FcsRecordType.RACE && !animal.Contains(r.StringId))
            .OrderBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(r => (r.StringId, r.Name.Trim(), SkinByDefault(r)))];
    }

    // Statistics of the last Update / Draw.
    public int Posed { get; private set; }
    public int DrawnCharacters { get; private set; }
    public int DrawnParts { get; private set; }
    public long DrawnTriangles { get; private set; }
    public int DrawCalls { get; private set; }
    /// <summary>Part instances drawn by mesh LOD level in the last view, and their triangles.</summary>
    public readonly int[] LevelParts = new int[8];
    public readonly long[] LevelTriangles = new long[8];
    public double LastUpdateMs { get; private set; }
    public double LastPoseMs { get; private set; }
    public double LastDrawCpuMs { get; private set; }
    public int Assets => assets.Count(a => a.Value is not null);
    /// <summary>Loads waiting: the looks drawn this frame whose meshes are not built yet and the character textures still to stream in.</summary>
    public int PendingLoads
    {
        get
        {
            // A reused set: the background-loading bar asks every frame.
            pendingLooks.Clear();
            foreach (var c in Draws.Items)
                if (!assets.ContainsKey(c.Appearance)) pendingLooks.Add(c.Appearance);
            return pendingLooks.Count + content.Textures.AwaitedCount + content.Textures.UnresidentUsedCount;
        }
    }
    readonly HashSet<CharacterAppearance> pendingLooks = new(ReferenceEqualityComparer.Instance);
    /// <summary>Current appearances still being prepared/uploaded, or textures still being loaded. Failed assets count as finished.</summary>
    public bool ContentPending => Draws.Items.Any(c => !Deferred(c) && !assets.ContainsKey(c.Appearance)) || content.Textures.AwaitedCount > 0 || content.Textures.UnresidentUsedCount > 0;

    /// <summary>
    /// While set (a game's loading screen): only the characters within <see cref="LoadingReach"/> of this point (x, z) are prepared and counted by <see cref="ContentPending"/>; the
    /// rest wait until it is cleared. The original's loaded zones (the camera's 3 x 3 block) can hold hundreds of NPCs, and preparing every one of their meshes (their
    /// generated levels: up to seconds each) kept the screen up two minutes for a view that shows a few dozen.
    /// </summary>
    public Vector2? LoadingFocus { get; set; }
    /// <summary>How far from <see cref="LoadingFocus"/> the loading screen's characters reach (world units).</summary>
    public float LoadingReach { get; set; } = 3000;

    /// <summary>Whether <paramref name="c"/> waits for the loading screen to lift (<see cref="LoadingFocus"/>).</summary>
    bool Deferred(in CharacterInstance c) =>
        LoadingFocus is { } f && Vector2.DistanceSquared(new Vector2(c.Position.X, c.Position.Z), f) > LoadingReach * LoadingReach;
    /// <summary>The appearance was uploaded or failed with a diagnostic; either completes its loading request.</summary>
    public bool IsPrepared(CharacterAppearance appearance) => assets.ContainsKey(appearance);
    /// <summary>Start preparing a scene's essential characters ahead of the world streaming queue (render thread).</summary>
    public void Prefetch(CharacterAppearance appearance) => content.Fetched(appearance, urgent: true);
    /// <summary>Prepares a look's meshes without keeping the request (the character editor's next looks), on the urgent queue or the ordinary one; a close-up's without generated levels. Render thread.</summary>
    public void Warm(CharacterAppearance appearance, bool urgent, bool close = false) => content.Warm(appearance, urgent, close);
    /// <summary>Starts preparing a look urgently as a close-up (the character editor's): its meshes without generated levels, which take nearly all of a mesh's preparation. Render thread.</summary>
    public void PrefetchCloseUp(CharacterAppearance appearance) => content.Prefetch(appearance, close: true);
    /// <summary>The character editor has closed: drops the close-up preparations it warmed and never showed. Render thread.</summary>
    public void ReleaseCloseUps()
    {
        content.ReleaseCloseUps();
        ReleaseStage();
    }
    public List<string> Messages => content.Messages;

    /// <summary>One character in range this frame.</summary>
    struct Live
    {
        public CharacterAsset Asset;
        public Matrix4x4 World;
        public Vector3 Centre;
        public float Radius;
        public int BoneBase, AttachBase, LayerStart, LayerCount;
        /// <summary>Its parts' first entry in <see cref="partFrames"/> (one per asset part).</summary>
        public int PartBase;
        public CharState State;
        public bool Repose, WeaponDrawn, RangedDrawn, BackDrawn;
        /// <summary>The blood of its first 8 body parts, a byte each (<see cref="CharacterInstance.Blood"/>).</summary>
        public ulong Blood;
        /// <summary>Posed this frame whatever the thinning (the player's, a ragdoll, a new asset or a first pose).</summary>
        public bool MustPose;
        /// <summary>How tall it is on screen, in pixels (its diameter over its distance by the view's pixels per radian): the thinning's priority.</summary>
        public float Pixels;
        public int Shots;
        public IReadOnlyList<StuckBoltMesh> Bolts;
        /// <summary>The ragdoll's bones over the animation (null: none).</summary>
        public BonePose? Bones;
        public long Key;
        /// <summary>Beyond <see cref="DrawDistance"/> (or <see cref="CharacterInstance.Hidden"/>) but posed for its portrait (<see cref="PortraitRequests"/>): not drawn in the world.</summary>
        public bool PortraitOnly;
        /// <summary>In a loaded building's interior (<see cref="WorldObjectRenderer.InteriorAt"/>), found once a frame for every view.</summary>
        public bool Interior;
        /// <summary>Holds bones taken from another character (<see cref="CharacterInstance.FreezeFrom"/>): never posed.</summary>
        public bool Frozen;
    }

    /// <summary>What the renderer keeps of a character between frames (by <see cref="CharacterInstance.Key"/>): its last posed palette and the one before.</summary>
    sealed class CharState
    {
        public Matrix4x4[] Bones = [], Previous = [], Attach = [], PreviousAttach = [];
        public Matrix4x4 World, PreviousWorld;
        public bool HasWorld;
        public long Posed = -1, Seen;
        /// <summary>The pose-rate period the character was last seen in (<see cref="PosePixelSteps"/>).</summary>
        public long PoseBucket = long.MinValue;
        /// <summary>What the last update drew it with, for picking (<see cref="RayHit"/>): its asset, weapon, bounding sphere, and whether it is drawn in the world.</summary>
        public CharacterAsset? Asset;
        public bool WeaponDrawn, RangedDrawn, BackDrawn, InWorld;
        /// <summary>Each stuck bolt's transform in its bone's frame, fixed the first time it was drawn (by bolt id).</summary>
        public readonly Dictionary<int, Matrix4x4> BoltLocal = [];
        public Vector3 Centre;
        public float Radius;
        /// <summary>Its last indoors answer (<see cref="Live.Interior"/>), asked again once it has moved.</summary>
        public WorldObjectRenderer.InteriorMemo Interior;
        /// <summary>Its bones were taken from another character (<see cref="Freeze"/>) and are never posed.</summary>
        public bool Frozen;
        /// <summary>The ragdoll bones it was last posed with: the same object again (a frozen ragdoll) needs no pose every frame.</summary>
        public BonePose? PosedRagdoll;
    }

    /// <summary>
    /// Gives the character <paramref name="key"/> the bones of <paramref name="from"/> as last posed, held from now on and never posed (a cut-off head keeps the pose of the
    /// moment it was cut, taken before its own look is ready). False when <paramref name="from"/> has no posed bones. Render thread, between updates.
    /// </summary>
    public bool HoldPose(long key, long from)
    {
        if (!states.TryGetValue(from, out var source) || source.Posed < 0 || source.Bones.Length == 0) return false;
        states[key] = new CharState { Bones = [.. source.Bones], Previous = [.. source.Bones], Posed = updateStamp, Seen = updateStamp, Frozen = true };
        return true;
    }

    /// <summary>
    /// <see cref="CharacterInstance.FreezeFrom"/>: the held bones (<see cref="HoldPose"/>, taken now if they were not), sized for <paramref name="asset"/>; false while there are none
    /// for its skeleton, and the character is not drawn.
    /// </summary>
    bool Freeze(long key, long from, CharacterAsset asset)
    {
        if ((!states.TryGetValue(key, out var own) || !own.Frozen) && (!HoldPose(key, from) || !states.TryGetValue(key, out own))) return false;
        if (own.Bones.Length != asset.Rig.BoneCount) return false;
        if (own.Attach.Length != asset.AttachCount) (own.Attach, own.PreviousAttach) = (new Matrix4x4[asset.AttachCount], new Matrix4x4[asset.AttachCount]);
        return true;
    }

    readonly Dictionary<long, CharState> states = [];
    /// <summary>
    /// The thinning by size on screen (engine choice, a performance measure: the original poses every animated character every frame): a character at least as many pixels
    /// tall as the first step is posed every frame, smaller ones at most 30, 15 and 7.5 times a second past each step (a skipped frame holds the last palette), each on a
    /// phase of its own so the poses spread over the frames. At most <see cref="FullRateBudget"/> characters, the largest on screen first, take the full rate; the next
    /// ones at least the second rate. <see cref="CharacterInstance.FullRateAnimation"/> (the player's first characters and the selected one) always keeps the full rate.
    /// </summary>
    public float[] PosePixelSteps { get; set; } = [100, 50, 25];
    /// <summary>The most characters posed at the full rate by their size alone (the nearest, largest first).</summary>
    public int FullRateBudget { get; set; } = 48;
    /// <summary>Pixels per radian of the view (<see cref="SetView"/>), for <see cref="Live.Pixels"/>.</summary>
    float viewPixelsPerRadian = Meitou.Data.World.TerrainLod.ProjectionScale(1080, 50 * MathF.PI / 180);
    /// <summary>With fewer characters than this drawn in the world, none is thinned: every one is posed every frame.</summary>
    const int ThinFromCharacters = 100;
    /// <summary>The pose rate inside the first step's distance, halved past each step (in poses per second, a frame rate above it poses every frame).</summary>
    const double PoseRateHz = 60;
    /// <summary>The seconds the pose rates count in (the game's animation clock, which a recorded flight steps by its frame time, so a take poses the same however slowly
    /// it is drawn); null: the real time.</summary>
    public Func<double>? PoseClock { get; set; }

    double poseSeconds;

    int PoseInterval(float pixels)
    {
        int interval = 1;
        foreach (float step in PosePixelSteps)
            if (pixels < step) interval *= 2;
        return interval;
    }

    Live[] live = new Live[256];
    int[] thinOrder = [];
    int liveCount;
    ActiveLayer[] layers = new ActiveLayer[1024];
    int layerCount;
    Matrix4x4[] attach = new Matrix4x4[256];
    int attachCount;
    Transient bones, previousBones;
    readonly List<AssetPart> slotParts = [];
    long updateStamp;
    readonly CharacterTextureBudget budget = new();

    /// <summary>Tells the character textures' mip streaming the camera: the render size in pixels and the vertical field of view (the upscaler's LOD bias is the context's).</summary>
    public void SetView(int width, int height, float fieldOfView)
    {
        content.Textures.Mips.SetView(width, height, fieldOfView, Gpu.LodBias);
        content.LodPixelsPerRadian = Meitou.Data.World.TerrainLod.ProjectionScale(height, fieldOfView);   // the generated levels are placed by their deviation in rendered pixels
        viewPixelsPerRadian = content.LodPixelsPerRadian;
        viewKnown = true;
    }

    bool viewKnown;
    /// <summary>Wait, at the next <see cref="Update"/> after <see cref="SetView"/>, for every asset and texture (stills).</summary>
    public bool SettleFirst { get; set; }
    float textureBias;
    uint textureStandIn;
    // What every material record depends on besides its material: the texture cache's version, the bias, the stand-in and the samplers. A change (or a
    // skin setting's) starts a new epoch, and each record is written again when next used.
    (int, float, uint, int) materialInputs;
    int materialEpoch = 1;

    public CharacterRenderer(IGpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assetLocator, CharacterRigs? rigs = null)
    {
        Gpu = gpu;
        nativeFrame = new NativeFrame(gpu, extraStorage: 4);
        colourProg = new ReflectedProgram(gpu, nativeFrame, CharacterShaders.Vertex(), Gi.GiResolveShaders.WithAlbedo(CharacterShaders.Fragment(), inShader: true), "characters", CharacterShaders.InstanceLocation);
        depthProg = new ReflectedProgram(gpu, nativeFrame, CharacterShaders.DepthVertex(), CharacterShaders.DepthFragment(), "characters depth", CharacterShaders.InstanceLocation);
        motionProg = new ReflectedProgram(gpu, nativeFrame, CharacterShaders.MotionVertex(), CharacterShaders.MotionFragment(), "characters motion", CharacterShaders.InstanceLocation);
        content = new CharacterContent(gpu, install, db, assetLocator, rigs);
        skinScattering = new SkinScattering(gpu);
        colourTimer = new PassTimer(gpu);
        depthTimer = new PassTimer(gpu);
    }

    public VramGuard? Guard { get => content.Textures.Guard; set => content.Textures.Guard = value; }
    /// <summary>The characters' texture cache (the VRAM dump lists it).</summary>
    public WorldTextureCache Textures => content.Textures;

    /// <summary>The skeleton rigs and pose setups the characters are drawn with; the simulation's ragdolls pose from the same ones.</summary>
    public CharacterRigs Rigs => content.Rigs;

    /// <summary>Rigs that find files as the renderer does, to share with the simulation (<see cref="CharacterRenderer(IGpuContext, GameInstall, GameDatabase, AssetLocator, CharacterRigs?)"/>).</summary>
    public static CharacterRigs CreateRigs(GameDatabase db, AssetLocator assets) => CharacterContent.CreateRigs(db, assets);

    // ------------------------------------------------------------------ per frame

    /// <summary>How far back along its flight a stuck bolt sits from its bone's origin (engine choice: the original takes 0.6 times a random vertex of the body near the bone).</summary>
    const float BoltDepth = 2;

    /// <summary>
    /// The bolts stuck in this update's posed characters (docs/game/combat.md "Bolt visuals"), as loose meshes into <paramref name="into"/> (cleared first), after <see cref="Update"/>:
    /// each fixed to its bone the first time it is seen, its +z along the flight and pulled back from the bone's origin by <see cref="BoltDepth"/>, then carried by the bone
    /// (a ragdoll's included), as the original attaches it to the bone keeping its world orientation (<c>FUN_14052bfa0</c>).
    /// </summary>
    public void CollectBolts(List<DebrisPiece> into)
    {
        into.Clear();
        for (int i = 0; i < liveCount; i++)
        {
            ref var l = ref live[i];
            var state = l.State;
            if (l.Bolts is not { Count: > 0 } bolts || l.PortraitOnly || l.Asset.IsIcon)
            {
                if (l.Bolts is not { Count: > 0 }) state?.BoltLocal.Clear();
                continue;
            }
            var rig = l.Asset.Rig;
            if (state.Bones.Length != rig.BoneCount) continue;
            if (state.BoltLocal.Count > bolts.Count)
                foreach (int id in state.BoltLocal.Keys.ToArray())
                    if (!bolts.Any(b => b.Id == id)) state.BoltLocal.Remove(id);
            int root = rig.Skeleton.Bones.FirstOrDefault(b => b.Parent is null)?.Handle ?? 0;
            foreach (var bolt in bolts)
            {
                int h = rig.Handle(bolt.Bone) ?? root;
                if ((uint)h >= (uint)state.Bones.Length) continue;
                // The bone's posed frame: its binding transform times its skin matrix (which is relative to the binding), placed in the world.
                var bind = Matrix4x4.CreateScale(rig.BindScale[h]) * Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(rig.BindRotationInverse[h]))
                    * Matrix4x4.CreateTranslation(rig.BindPosition[h]);
                var bone = bind * state.Bones[h] * l.World;
                if (!state.BoltLocal.TryGetValue(bolt.Id, out var local))
                {
                    var flight = Vector3.TransformNormal(bolt.Direction, l.World);
                    if (flight.LengthSquared() < 1e-8f || !Matrix4x4.Invert(bone, out var inverse)) continue;
                    flight = Vector3.Normalize(flight);
                    var placed = Matrix4x4.CreateFromQuaternion(TurnZ(flight)) * Matrix4x4.CreateTranslation(bone.Translation - flight * BoltDepth);
                    state.BoltLocal[bolt.Id] = local = placed * inverse;
                }
                if (!Matrix4x4.Decompose(local * bone, out _, out var rotation, out var position)) continue;
                into.Add(new DebrisPiece(bolt.Mesh, 1, rotation, position));
            }
        }
    }

    /// <summary>The rotation that turns +z onto <paramref name="to"/> (unit length).</summary>
    static Quaternion TurnZ(Vector3 to)
    {
        var axis = Vector3.Cross(Vector3.UnitZ, to);
        float s = axis.Length();
        if (s < 1e-5f) return to.Z > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        return Quaternion.CreateFromAxisAngle(axis / s, MathF.Atan2(s, to.Z));
    }

    /// <summary>Adds the carried lights of this update's posed characters to <paramref name="into"/>, at their world positions (after <see cref="Update"/>).</summary>
    public void CollectLamps(List<Meitou.Data.World.WorldLight> into)
    {
        for (int i = 0; i < liveCount; i++)
        {
            ref var l = ref live[i];
            if (l.PortraitOnly || l.Asset.IsIcon || l.Asset.Lamps.Count == 0) continue;
            foreach (var lamp in l.Asset.Lamps)
                if (lamp.AttachIndex < l.State.Attach.Length)
                    into.Add(lamp.Light with { Position = (l.State.Attach[lamp.AttachIndex] * l.World).Translation });
        }
    }

    /// <summary>
    /// The frame's characters: the list from <see cref="Source"/>, assets built for new appearances (at most <see cref="LoadBudget"/>), every
    /// character within <see cref="DrawDistance"/> of <paramref name="eye"/> posed in parallel into the frame's bone palettes, the textures'
    /// bindless indices and the parts' materials written. Render thread; before the frame's first <see cref="Draw"/> or <see cref="DrawDepth"/>.
    /// </summary>
    public void Update(Vector3 eye)
    {
        if (SettleFirst && viewKnown)
        {
            SettleFirst = false;
            var settleWatch = Stopwatch.StartNew();
            Settle(eye);
            Console.WriteLine($"characters built and loaded: {Describe()} ({settleWatch.ElapsedMilliseconds} ms)");
            foreach (var m in Messages.Distinct().Take(20)) Console.WriteLine($"warning   {m}");
            Messages.Clear();
        }
        PollTimers();
        var watch = Stopwatch.StartNew();
        Draws.Clear();
        Source?.Invoke(Draws);
        StageClock.Part(StageClock.PartFill);
        GatherPortraitKeys();
        content.Textures.Pump(wait: LoadBudget == 0, max: Covered ? 64 : 8, budgetMs: Covered ? CoveredBudgetMs : 1.0);
        StageClock.Part(StageClock.PartTextures);
        textureBias = Gpu.LodBias;
        textureStandIn = Gpu.StandIn2D;
        updateStamp++;
        poseSeconds = PoseClock?.Invoke() ?? Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        // 1. Assets and the characters in range, with their layers resolved (the rig's caches are filled here, on this thread).
        liveCount = layerCount = attachCount = 0;
        int built = 0, boneTotal = 0, partTotal = 0;
        double buildMs = 0;
        float reach = EyeLift.Of(eye, GroundUnderEye) > 0 ? Math.Min(DrawDistance, LiftedReach) : DrawDistance;
        var items = Draws.Items;
        keptLooks.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            var inst = items[i];
            if (!assets.TryGetValue(inst.Appearance, out var asset))
            {
                if (Deferred(inst)) continue;
                if (!portraitKeys.Contains(inst.Key) && !WantsAsset(inst, eye, reach)) continue;
                // Interactive: the files are read and the meshes prepared on a worker first (CharacterContent.Fetched), then a few a frame are uploaded.
                if (LoadBudget > 0 && (!content.Fetched(inst.Appearance) || !Covered && built >= LoadBudget || buildMs >= (Covered ? CoveredBudgetMs : BuildBudgetMs))) continue;
                long buildStart = Stopwatch.GetTimestamp();
                asset = content.Build(inst.Appearance);
                buildMs += Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds;
                assets[inst.Appearance] = asset;
                built++;
            }
            if (asset is null) continue;
            if (inst.FreezeFrom is { } freezeFrom && !Freeze(inst.Key, freezeFrom, asset)) continue;
            if (inst.Build is { } build) ApplyBuild(asset, build);
            if (inst.BlendFrom is { } blendFrom && inst.Blend < 1 && assets.TryGetValue(blendFrom, out var fromAsset) && fromAsset is not null) ApplyBlend(asset, fromAsset, inst.Blend);
            else RestoreBlend(asset);
            float scale = inst.Scale > 0 ? inst.Scale : 1;
            var centre = inst.Position + new Vector3(0, asset.Height * 0.5f * scale, 0);
            bool far = inst.Hidden || Vector3.Distance(eye, centre) - asset.Radius * scale > reach;
            if (far && !portraitKeys.Contains(inst.Key))
            {
                if (!inst.Hidden && Kept.Count > 0 && NearKept(centre) is var dk && dk <= KeptReach) keptLooks.Add((asset, dk));
                continue;
            }
            if (liveCount == live.Length) Array.Resize(ref live, liveCount * 2);
            ref var l = ref live[liveCount++];
            l.Asset = asset;
            (l.Key, l.PortraitOnly) = (inst.Key, far);
            // Lowered by the body shape's lift, so a short body's feet stay on the ground (CharacterAsset.GroundOffset); an animal scaled by its size (its node's scale).
            // A carried body's whole rotation lowers it in its own frame (CharacterRigs.Placement, as the simulation poses its ragdoll).
            l.World = inst.World ?? (inst.Rotation is { } rotation
                ? Matrix4x4.CreateTranslation(0, -asset.GroundOffset, 0) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(inst.Position)
                : Matrix4x4.CreateRotationY(inst.Yaw) * Matrix4x4.CreateTranslation(inst.Position - new Vector3(0, asset.GroundOffset, 0)));
            if (scale != 1 && inst.World is null) l.World = Matrix4x4.CreateScale(scale) * l.World;
            (l.WeaponDrawn, l.RangedDrawn, l.BackDrawn, l.Shots, l.Bolts) = (inst.WeaponDrawn, inst.RangedDrawn, inst.BackDrawn, inst.Shots, inst.Bolts);
            l.Centre = centre;
            l.Radius = asset.Radius * scale;
            l.BoneBase = boneTotal;
            boneTotal += asset.Rig.BoneCount;
            l.AttachBase = attachCount;
            attachCount += asset.AttachCount;
            l.PartBase = partTotal;
            partTotal += asset.Parts.Count;
            if (!states.TryGetValue(inst.Key, out var state)) states[inst.Key] = state = new CharState();
            state.Seen = updateStamp;
            l.State = state;
            l.Interior = !far && InteriorObjects?.InteriorAt(centre, ref state.Interior) is not null;
            bool newAsset = !ReferenceEquals(state.Asset, asset);
            (state.Asset, state.WeaponDrawn, state.RangedDrawn, state.BackDrawn, state.InWorld, state.Centre, state.Radius) = (asset, inst.WeaponDrawn, inst.RangedDrawn, inst.BackDrawn, !far, centre, l.Radius);
            state.PreviousWorld = state.HasWorld ? state.World : l.World;
            (state.World, state.HasWorld) = (l.World, true);
            l.Bones = inst.Bones;
            l.Blood = inst.Blood;
            // A moving ragdoll is posed every frame: its bones move by physics, not by a clip the thinning could thin out (a frozen one hands over the same bones). A new asset (other gear, another rig) is posed at
            // once: its parts' attach matrices and its arrays' sizes come from it. The rest are thinned by their size on screen after the gather (below).
            l.Frozen = state.Frozen;
            l.MustPose = !l.Frozen && (inst.FullRateAnimation || inst.Bones is not null && !ReferenceEquals(inst.Bones, state.PosedRagdoll) || state.Posed < 0 || newAsset);
            l.Pixels = 2 * l.Radius * viewPixelsPerRadian / MathF.Max(1, Vector3.Distance(eye, centre));
            l.LayerStart = layerCount;
            var rig = asset.Rig;
            if (inst.Poses is { } poses)
                for (int p = 0; p < poses.Count; p++)
                    if (rig.Layer(content.Database, poses[p].Animation, asset.Animal) is { } def) AddLayer(new ActiveLayer(def, poses[p].Time, poses[p].Weight));
            foreach (var (def, time) in asset.Fixed) AddLayer(new ActiveLayer(def, time, 1));
            l.LayerCount = layerCount - l.LayerStart;
        }
        int inWorld = 0;
        for (int i = 0; i < liveCount; i++) if (!live[i].PortraitOnly) inWorld++;
        if (inWorld < ThinFromCharacters)
            for (int i = 0; i < liveCount; i++) live[i].Repose = !live[i].Frozen;
        else
        {
            // The largest on screen first: they keep the full rate up to the budget, the rest at least the second rate.
            if (thinOrder.Length < liveCount) thinOrder = new int[Math.Max(liveCount, thinOrder.Length * 2)];
            for (int i = 0; i < liveCount; i++) thinOrder[i] = i;
            var order = thinOrder.AsSpan(0, liveCount);
            var lives = live;
            order.Sort((a, b) => lives[b].Pixels.CompareTo(lives[a].Pixels));
            int full = 0;
            foreach (int i in order)
            {
                ref var l = ref live[i];
                int interval = PoseInterval(l.Pixels);
                if (interval == 1 && full++ >= FullRateBudget) interval = 2;
                // In time, not frames (a slow frame rate must not thin the poses out further): a new pose each time the character's own clock enters a new period.
                long bucket = interval == 1 ? updateStamp : (long)Math.Floor(poseSeconds * PoseRateHz / interval + unchecked((ulong)l.Key * 0x9E3779B97F4A7C15UL) / 18446744073709551616.0);
                l.Repose = !l.Frozen && (l.MustPose || bucket != l.State.PoseBucket);
                l.State.PoseBucket = bucket;
            }
        }
        Posed = 0;
        for (int i = 0; i < liveCount; i++) if (live[i].Repose) Posed++;
        if (updateStamp % 120 == 0) EvictStates();
        GatherIcons(eye, ref attachCount);
        if (attach.Length < attachCount) attach = new Matrix4x4[Math.Max(attachCount, attach.Length * 2)];

        // 2. The bone palettes, posed in parallel straight into the frame's memory.
        StageClock.Part(StageClock.PartGather);
        double gatherMs = watch.Elapsed.TotalMilliseconds;
        var poseWatch = Stopwatch.StartNew();
        bones = AllocateConstants((ulong)Math.Max(boneTotal, 1) * 64, 256);
        nint bonePointer = (nint)bones.Pointer;
        previousBones = Motion ? AllocateConstants((ulong)Math.Max(boneTotal, 1) * 64, 256) : bones;
        nint previousPointer = (nint)previousBones.Pointer;
        bool motion = Motion;
        if (partFrames.Length < partTotal) partFrames = new PartFrame[Math.Max(partTotal, partFrames.Length * 2)];
        var liveArray = live;
        var layerArray = layers;
        var attachArray = attach;
        long stamp = updateStamp;
        if (liveCount > 0)
            Parallel.For(0, liveCount, i =>
            {
                ref var l = ref liveArray[i];
                var asset = l.Asset;
                if (asset.IsIcon) return;
                var rig = asset.Rig;
                var state = l.State;
                if (l.Repose)
                {
                    if (state.Bones.Length != rig.BoneCount || state.Attach.Length != asset.AttachCount) { state.Bones = new Matrix4x4[rig.BoneCount]; state.Previous = new Matrix4x4[rig.BoneCount]; state.Attach = new Matrix4x4[asset.AttachCount]; state.PreviousAttach = new Matrix4x4[asset.AttachCount]; state.Posed = -1; }
                    (state.Bones, state.Previous) = (state.Previous, state.Bones);
                    state.Attach.CopyTo(state.PreviousAttach, 0);
                    var scratch = rig.Rent();
                    scratch.Pose(asset.Shape, layerArray.AsSpan(l.LayerStart, l.LayerCount), state.Bones, l.Bones);
                    foreach (var part in asset.Parts)
                        if (part.AttachIndex >= 0) state.Attach[part.AttachIndex] = scratch.TagTransform(part.Bone, part.Offset);
                    foreach (var lamp in asset.Lamps) state.Attach[lamp.AttachIndex] = lamp.Offset * scratch.BoneTransform(lamp.Bone);
                    rig.Return(scratch);
                    if (state.Posed < 0) { state.Bones.CopyTo(state.Previous, 0); state.Attach.CopyTo(state.PreviousAttach, 0); }
                    state.Posed = stamp;
                    state.PosedRagdoll = l.Bones;
                }
                state.Bones.CopyTo(new Span<Matrix4x4>((byte*)bonePointer + (long)l.BoneBase * 64, rig.BoneCount));
                state.Attach.CopyTo(attachArray, l.AttachBase);
                if (motion)
                {
                    // Last frame's palette: what was posed before this repose, or the current one when this frame did not repose (no bone motion).
                    (l.Repose ? state.Previous : state.Bones).CopyTo(new Span<Matrix4x4>((byte*)previousPointer + (long)l.BoneBase * 64, rig.BoneCount));
                    if (!l.Repose) state.Attach.CopyTo(state.PreviousAttach, 0);
                }
                if (!l.PortraitOnly) PlaceParts(in l, eye, motion);
            });
        (partStamp, partOrigin, partMotion) = (updateStamp, eye, motion);
        LastPoseMs = poseWatch.Elapsed.TotalMilliseconds;
        PoseIcons();
        StageClock.Part(StageClock.PartPose);

        // 3. The materials of the parts in range.
        slotParts.Clear();
        for (int i = 0; i < liveCount; i++)
        {
            float distance = Vector3.Distance(eye, live[i].Centre);
            foreach (var part in live[i].Asset.Parts)
            {
                if (!part.ShowsWith(live[i].WeaponDrawn, live[i].RangedDrawn, live[i].Shots, live[i].BackDrawn)) continue;
                if (part.MaterialFrame != updateStamp)
                {
                    part.MaterialFrame = updateStamp;
                    part.Near = float.PositiveInfinity;
                    slotParts.Add(part);
                }
                part.Near = Math.Min(part.Near, Math.Max(distance - live[i].Radius * 0.5f, 0.5f));
            }
        }
        // Mip streaming: each texture's nearest user over how large a texel of its part is there; textures load at the size that need allows. Offered on
        // the frame the budget commits them (every 500 ms), which also marks the textures used (reading the key), long before the cache's idle limits.
        if (budget.Due)
            foreach (var part in slotParts)
            {
                float need = part.Near / part.UvScale * budget.NeedScale;
                foreach (var t in part.Material.Tex)
                    if (t is not null) { t.OfferNow(need); _ = t.Key; }
            }
        if (budget.Due)
            foreach (var (asset, distance) in keptLooks)
                foreach (var part in asset.Parts)
                {
                    float need = Math.Max(Math.Max(distance, KeptViewDistance) - asset.Radius * 0.5f, 0.5f) / part.UvScale * budget.NeedScale;
                    foreach (var t in part.Material.Tex)
                        if (t is not null) { t.OfferNow(need); _ = t.Key; }
                }
        budget.Update(content.Textures);
        // A record is written again only when something it reads changed (materialEpoch, or the material's own fields), and uploaded into the material table
        // (CharacterMaterialTable) only when it came out different or the material has no slot.
        var inputs = (content.Textures.Version, textureBias, textureStandIn, Gpu.Samplers.Generation);
        if (inputs != materialInputs) (materialInputs, materialEpoch) = (inputs, materialEpoch + 1);
        var table = content.Materials;
        table.BeginFrame();
        if (updateStamp % 120 == 0) table.Sweep(updateStamp - CharacterMaterialTable.IdleUpdates);
        CharacterMaterialRecord record = default;
        for (int s = 0; s < slotParts.Count; s++)
        {
            var m = slotParts[s].Material;
            m.SlotUsed = updateStamp;
            if (m.RecordEpoch != materialEpoch)
            {
                Write(m, ref record);
                m.RecordEpoch = materialEpoch;
                if (m.Slot >= 0 && SameRecord(in record, in m.Record)) continue;
                m.Record = record;
                table.Place(m);
            }
            else if (m.Slot < 0) table.Place(m);
        }
        table.Flush();
        StageClock.Part(StageClock.PartMaterials);
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
        updateCount++;
        updateCpu.Add(LastUpdateMs);
        poseCpu.Add(LastPoseMs);
        gatherCpu.Add(gatherMs);
    }

    static bool SameRecord(in CharacterMaterialRecord a, in CharacterMaterialRecord b) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<CharacterMaterialRecord>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<CharacterMaterialRecord>(in b)));

    readonly List<long> staleKeys = [];

    /// <summary>Forgets the characters not seen for 60 updates.</summary>
    void EvictStates()
    {
        staleKeys.Clear();
        foreach (var (key, state) in states)
            if (updateStamp - state.Seen > (state.Frozen ? 600 : 60)) staleKeys.Add(key);   // a held pose waits longer for its look to be ready
        foreach (var key in staleKeys) states.Remove(key);
    }

    void AddLayer(in ActiveLayer layer)
    {
        if (layerCount == layers.Length) Array.Resize(ref layers, layerCount * 2);
        layers[layerCount++] = layer;
    }

    uint Index(WorldTexture? t, out bool present)
    {
        uint key = t?.Key ?? 0;
        present = key != 0;
        return present ? content.Textures.Index(key, textureBias, textureStandIn) : textureStandIn;
    }

    /// <summary>
    /// A person's stat-driven body (docs/animation.md "Stat-driven body"), applied when it has changed by more than 0.01: the bone shape and ground offset with its
    /// muscle and starvation, and the body-shaded parts' normal maps (<see cref="CharacterShape.NormalBlend"/>: base, blend and weight), as <c>FUN_140531820</c> sets them.
    /// The maps change once both are resident, so a body is not drawn without one while it loads.
    /// </summary>
    void ApplyBuild(CharacterAsset asset, BodyBuild build)
    {
        if (asset.Animal) return;
        if (asset.AppliedBuild is not { } applied || !applied.Near(build))
        {
            asset.AppliedBuild = build;
            var c = asset.Appearance;
            if (content.Rigs.Reshape(c, build) is { } reshaped)
            {
                // A glide mixes the rest values (CharacterRenderer.Blend), so the build goes there too, or the end of a glide would bring back an older one.
                if (asset.Rest is { } rest) (rest.Shape, rest.GroundOffset) = reshaped;
                if (!asset.Blending) (asset.Shape, asset.GroundOffset) = reshaped;
            }
            var (baseField, blendField, weight) = CharacterShape.NormalBlend(c.Female, build.Definition, build.Starvation);
            string? File(string field) => field.EndsWith(" strong", StringComparison.Ordinal) ? c.BodyNormalStrong : field.EndsWith(" skinny", StringComparison.Ordinal) ? c.BodyNormalSkinny : c.BodyNormal;
            // A race without the strong or skinny map keeps its own as the base.
            asset.PendingNormals = (content.BodyTexture(File(baseField) ?? c.BodyNormal), content.BodyTexture(File(blendField)), weight);
        }
        if (asset.PendingNormals is not { } p || !Ready(p.Normal) || !Ready(p.Blend)) return;
        asset.PendingNormals = null;
        foreach (var part in asset.Parts)
            if (part.Material.Shading == CharacterShaders.ShadeBody)
            {
                part.Material.Tex[CharacterShaders.SlotNormal] = p.Normal;
                part.Material.Tex[CharacterShaders.SlotBlendNormal] = p.Blend;
                part.Material.MuscleBlend = p.Weight;
                part.Material.RecordEpoch = 0;
            }

        // Reading the key asks for the texture; a missing file never becomes resident.
        static bool Ready(WorldTexture? t) => t is null || t.Key != 0 || t.State == WorldTexture.Residency.Missing;
    }

    void Write(PartMaterial m, ref CharacterMaterialRecord r)
    {
        r = default;
        Span<bool> has = stackalloc bool[CharacterShaders.Slots];
        for (int i = 0; i < CharacterShaders.Slots; i++) r.Tex[i] = Index(m.Tex[i], out has[i]);
        for (int i = CharacterShaders.Slots; i < CharacterShaders.TextureSlotCount; i++) r.Tex[i] = textureStandIn;
        uint flags = 0;
        if (has[CharacterShaders.SlotDiffuse]) flags |= CharacterShaders.HasDiffuse;
        if (has[CharacterShaders.SlotNormal]) flags |= CharacterShaders.HasNormal;
        if (m.Swizzled || m.Tex[CharacterShaders.SlotNormal] is { Swizzled: true }) flags |= CharacterShaders.NormalSwizzled;
        if (has[CharacterShaders.SlotHeadDiffuse]) flags |= CharacterShaders.HasHead;
        if (has[CharacterShaders.SlotHeadNormal]) flags |= CharacterShaders.HasHeadNormal;
        if (has[CharacterShaders.SlotHeadMask]) flags |= CharacterShaders.HasHeadMask;
        if (has[CharacterShaders.SlotBodyMask]) flags |= CharacterShaders.HasBodyMask;
        if (has[CharacterShaders.SlotHairOverlay]) flags |= CharacterShaders.HasHair;
        if (has[CharacterShaders.SlotBeardOverlay]) flags |= CharacterShaders.HasBeard;
        if (m.ClipOnNormalAlpha && has[CharacterShaders.SlotNormal]) flags |= CharacterShaders.ClipNormalAlpha;
        if (m.Dyed && has[CharacterShaders.SlotColourMap]) flags |= CharacterShaders.Dyed;
        if (m.Worn) flags |= CharacterShaders.Worn;
        if (m.Skin && SkinFor(m)) flags |= CharacterShaders.Skin;
        if (m.VertexColoured) flags |= CharacterShaders.VertexColoured;
        if (m.Bloody && has[CharacterShaders.SlotBlood]) flags |= CharacterShaders.Bloody | (m.BloodFull ? CharacterShaders.BloodFull : 0);
        if (m.Wound) flags |= CharacterShaders.Wound | (m.Mechanical ? CharacterShaders.Mechanical : 0);
        // Textures assigned but not resident yet: a hair's diffuse (its cut-out), a clipped item's normal map, the head's maps.
        if (m.Shading == CharacterShaders.ShadeHair && m.Tex[CharacterShaders.SlotDiffuse] is not null && !has[CharacterShaders.SlotDiffuse]
            || m.ClipOnNormalAlpha && m.Tex[CharacterShaders.SlotNormal] is not null && !has[CharacterShaders.SlotNormal])
            flags |= CharacterShaders.AlphaPending;
        if (m.Tex[CharacterShaders.SlotHeadDiffuse] is not null && !has[CharacterShaders.SlotHeadDiffuse]) flags |= CharacterShaders.HeadPending;
        if (m.Tex[CharacterShaders.SlotHeadNormal] is not null && !has[CharacterShaders.SlotHeadNormal]) flags |= CharacterShaders.HeadNormalPending;
        uint vests = 0;
        for (int i = 0; i < 3; i++)
        {
            int s = CharacterShaders.SlotVest + 3 * i;
            if (has[s] && has[s + 1]) vests |= 1u << i;
            if (m.VestColoured[i] && has[s + 2]) flags |= CharacterShaders.Vest0Coloured << i;
        }
        r.Shading = m.Shading;
        r.Flags = flags;
        r.VestCount = vests;
        r.AlphaThreshold = m.AlphaThreshold;
        r.ShirtColour = new Vector4(m.ShirtColour, m.GlossMult);
        r.SkinTone = new Vector4(m.SkinTone, has[CharacterShaders.SlotBlendNormal] ? m.MuscleBlend : 0);
        r.HairColour = new Vector4(m.HairColour, 1);
        r.HairAlpha = m.HairAlpha; r.HairMult = m.HairMult; r.BeardAlpha = m.BeardAlpha;
        r.DiffuseChannel = m.DiffuseChannel; r.AlphaChannel = m.AlphaChannel;
        r.Colour1 = m.Colour1; r.Colour2 = m.Colour2;
    }

    /// <summary>Waits for everything wanted to be built and loaded (stills): every appearance of the list, every texture.</summary>
    public void Settle(Vector3 eye, int timeoutMs = 120000)
    {
        int old = LoadBudget;
        LoadBudget = 0;
        try
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                Update(eye);
                content.Textures.Pump(wait: true);
                if (content.Textures.PendingCount == 0) break;
                Thread.Sleep(1);
            }
            Update(eye);   // the indices of what has just landed
        }
        finally { LoadBudget = old; }
    }

    public string Describe() =>
        $"{Assets} appearances, {content.MeshCount} meshes ({content.MeshBytes / 1048576.0:0.0} MB), {content.Textures.Describe()}, texture need x{budget.NeedScale:0.0} (mark {content.Textures.MarkMb:0} MB)";

    public void Dispose()
    {
        if (updateCount > 0) Console.WriteLine($"characters {Statistics()}");
        colourProg.Dispose();
        depthProg.Dispose();
        motionProg.Dispose();
        nativeFrame.Dispose();
        content.Dispose();
        skinScattering.Dispose();
        DisposePortraits();
        DisposeStage();
        DisposeHover();
        DisposeIcons();
    }
}

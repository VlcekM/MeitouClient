using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Profiling;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// Draws the characters of a <see cref="CharacterDrawList"/> (docs/character-renderer.md): skinned meshes with their clothing, hair,
/// head and bone-attached weapons, posed from the animation layers the host gives, lit like the world's buildings.
/// <list type="bullet">
/// <item><see cref="Update"/> once per frame, before the shadow cascades: asks the host's <see cref="Source"/> for the list, builds assets for
/// appearances not seen yet, poses every character in range in parallel into the frame's bone-palette buffer, and writes the materials of the
/// parts in view.</item>
/// <item><see cref="Draw"/> / <see cref="DrawDepth"/> for each view (the scene, a shadow cascade): cull, pick each part's mesh LOD, group
/// the instances by (mesh part, level) and record one instanced draw each.</item>
/// </list>
/// </summary>
internal sealed unsafe partial class CharacterRenderer : IDisposable
{
    /// <summary>
    /// The device everything is drawn on.
    /// </summary>
    IGpuContext Gpu { get; }

    /// <summary>
    /// Zeroed host-visible memory of this frame (the bone palettes, the material table and the instances are written into it), cleared in place: a
    /// managed array of this size (megabytes with a crowd) would land on the large object heap every frame and force gen2 collections.
    /// </summary>
    Transient AllocateConstants(ulong bytes, ulong alignment)
    {
        var slice = Gpu.Frame.AllocateConstants(Math.Max(bytes, 1), alignment);
        slice.Bytes.Clear();
        return slice;
    }

    /// <summary>
    /// The native model's sets the three programs are made with and bound through (with four extra storage buffers).
    /// </summary>
    readonly NativeFrame _nativeFrame;
    /// <summary>
    /// The programs of the colour pass, the depth-only passes (shadows, prepass) and the motion-vector pass.
    /// </summary>
    readonly ReflectedProgram _colourProg, _depthProg, _motionProg;
    /// <summary>
    /// The meshes, textures, rigs, morphs and material table the assets are built from.
    /// </summary>
    readonly CharacterContent _content;
    /// <summary>
    /// GPU timers of the colour and the depth passes, for the statistics.
    /// </summary>
    readonly PassTimer _colourTimer, _depthTimer;
    /// <summary>
    /// The built asset of each appearance by reference (null: it could not be built); kept between frames.
    /// </summary>
    readonly Dictionary<CharacterAppearance, CharacterAsset?> _assets = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The characters of this frame: filled by <see cref="Source"/> inside <see cref="Update"/>, or by the host before it.
    /// </summary>
    public CharacterDrawList Draws { get; } = new();
    /// <summary>
    /// Called at the start of <see cref="Update"/> with the (cleared) list to fill; null: the host fills <see cref="Draws"/> itself.
    /// </summary>
    public Action<CharacterDrawList>? Source { get; set; }

    /// <summary>
    /// Characters farther than this from the eye are neither posed nor drawn.
    /// </summary>
    public float DrawDistance { get; set; } = 6000;
    /// <summary>
    /// The range of the game's NPC View Range slider (docs/formats/settings.md), which <see cref="DrawDistance"/> is set from.
    /// </summary>
    public const float MinDrawDistance = 1000, MaxDrawDistance = 8000;
    /// <summary>
    /// While the eye is above the game's camera heights (<see cref="EyeLift"/>): characters farther than this are neither built, posed nor drawn
    /// (engine choice: a few pixels tall there).
    /// </summary>
    public float LiftedReach { get; set; } = 3000;
    /// <summary>
    /// The ground's height under the eye (set each frame; unknown: none), for <see cref="LiftedReach"/>.
    /// </summary>
    public float GroundUnderEye { get; set; } = float.NegativeInfinity;
    /// <summary>
    /// A character flight's arrival eye and the player's kept points: characters within <see cref="KeptReach"/> of them are built ahead (their meshes
    /// and textures), the eye's own being only those it can draw.
    /// </summary>
    public Vector3? Ahead { get; set; }
    /// <summary>
    /// The player's kept points (see <see cref="Ahead"/>), in world units.
    /// </summary>
    public IReadOnlyList<Vector3> Kept { get; set; } = [];
    /// <summary>
    /// The ground distance from <see cref="Ahead"/> or a kept point within which characters are built ahead, in world units.
    /// </summary>
    public float KeptReach { get; set; } = 3000;
    /// <summary>
    /// The camera's distance to its target: a kept character's texture needs count from at least this far (the view a jump there would have).
    /// </summary>
    public float KeptViewDistance { get; set; }
    /// <summary>
    /// Places kept loaded (<see cref="WorldTextureCache.Areas"/>): the character textures' mark (and so <see cref="CharacterTextureBudget"/>) scales
    /// with it.
    /// </summary>
    public int Areas { set => _content.Textures.Areas = value; }
    /// <summary>
    /// Built characters out of the eye's reach but within <see cref="KeptReach"/> of a kept point, with that distance: their textures are offered
    /// (and so marked used) as if seen from there, or the cache's idle limit unloaded them before a jump there.
    /// </summary>
    readonly List<(CharacterAsset Asset, float Distance)> _keptLooks = [];
    /// <summary>
    /// The ground (X/Z) distance from <paramref name="p"/> to the nearest kept point; infinity with none.
    /// </summary>
    float NearKept(Vector3 p)
    {
        float d = float.PositiveInfinity;
        var kept = Kept;
        for (int k = 0; k < kept.Count; k++) d = MathF.Min(d, Vector2.Distance(new Vector2(kept[k].X, kept[k].Z), new Vector2(p.X, p.Z)));
        return d;
    }
    /// <summary>
    /// Added to the reach for building, so a character is ready a little before it is drawn.
    /// </summary>
    const float BuildMargin = 300;

    /// <summary>
    /// Whether <paramref name="c"/>'s look is built: within <paramref name="reach"/> (and the margin) of the eye, or within <see cref="KeptReach"/>
    /// of the arrival or a kept point. Every loaded character is in the draw list; built all, they loaded the textures of the whole loaded world.
    /// </summary>
    bool WantsAsset(in CharacterInstance c, Vector3 eye, float reach)
    {
        if (Vector3.Distance(eye, c.Position) <= reach + BuildMargin) return true;
        var at = new Vector2(c.Position.X, c.Position.Z);
        if (Ahead is { } a && Vector2.Distance(new Vector2(a.X, a.Z), at) <= KeptReach) return true;
        var kept = Kept;
        for (int k = 0; k < kept.Count; k++) if (Vector2.Distance(new Vector2(kept[k].X, kept[k].Z), at) <= KeptReach) return true;
        return false;
    }
    /// <summary>
    /// A presentation fill light (linear colour, 0 in play): added ambient, three times as strong at the silhouette, so a backlit figure reads.
    /// </summary>
    public Vector3 Fill { get; set; }
    /// <summary>
    /// Assets built per <see cref="Update"/> (0: all of them, for stills), and no new one once <see cref="BuildBudgetMs"/> went on them.
    /// </summary>
    public int LoadBudget { get; set; } = 4;
    /// <summary>
    /// A frame's building time after which it starts no other (the first is always built). A new appearance takes 20-40 ms on the render thread (its
    /// item materials and race rig), and four of them in one frame of a town filling up held it 150 ms (docs/bench.md "Play benchmark").
    /// </summary>
    public const double BuildBudgetMs = 4;
    /// <summary>
    /// The view is under the loading screen (the host sets it each frame): its frames need not be smooth, so textures upload and appearances build
    /// for up to <see cref="CoveredBudgetMs"/> a frame instead of the play budgets, whose trickle of texture uploads held a town's loading screen up
    /// 10 s.
    /// </summary>
    public bool Covered { get; set; }
    /// <summary>
    /// The per-frame budget for texture uploads and builds while <see cref="Covered"/>, in milliseconds.
    /// </summary>
    const double CoveredBudgetMs = 16;
    /// <summary>
    /// Ogre's camera LOD bias as a distance factor (above 1: coarser levels sooner).
    /// </summary>
    public float LodBias { get; set; } = 1;
    /// <summary>
    /// Further factor on the LOD value in the shadow cascades (a shadow hides detail: coarser levels sooner).
    /// </summary>
    public float ShadowLodBias { get; set; } = 2;
    /// <summary>
    /// Meitou's generated levels (four reductions, the shadows' own bias and floors; the <c>charlod</c> switch's Meitou side); off: the mesh files'
    /// own levels as the original picks them (Faithful).
    /// </summary>
    public bool GeneratedLod { get; set; } = true;

    /// <summary>
    /// Pre-integrated skin scattering on people's bare skin (the <c>skin</c> switch's Meitou side, docs/character-renderer.md "Skin"); off: the plain
    /// N.L of the game. A change redraws the portraits.
    /// </summary>
    public bool SkinShading
    {
        get => _skinShading;
        set
        {
            if (_skinShading == value) return;
            _skinShading = value;
            _portraitShown.Clear();
            _materialEpoch++;
        }
    }
    /// <summary>
    /// Backing field of <see cref="SkinShading"/>.
    /// </summary>
    bool _skinShading = true;

    /// <summary>
    /// The skin scattering table's bindless index while the Skin switch is on, else 0 (a severed limb's skin: <see cref="DebrisRenderer"/>).
    /// </summary>
    public uint SkinTable => SkinShading ? _skinScattering.Index : 0;

    /// <summary>
    /// Whether the skin shading applies to the race <paramref name="race"/> (RACE string id): the Skin page's choice, else
    /// <paramref name="byDefault"/>.
    /// </summary>
    public bool SkinOn(string? race, bool byDefault) => race is not null && _skinRaces is not null && _skinRaces.TryGetValue(race,
        out bool chosen) ? chosen : byDefault;
    /// <summary>
    /// The skin scattering table (<see cref="SkinTable"/>).
    /// </summary>
    readonly SkinScattering _skinScattering;

    /// <summary>
    /// The skin per race, chosen against its default (RACE string id to on or off; the settings' Skin page): a race not in it takes
    /// <see cref="SkinByDefault"/>. Read as the materials are written each frame, so a change shows at once; setting it (also the same, changed,
    /// dictionary) redraws the portraits.
    /// </summary>
    public IReadOnlyDictionary<string, bool>? SkinRaces
    {
        get => _skinRaces;
        set { _skinRaces = value; _portraitShown.Clear(); _materialEpoch++; }
    }
    /// <summary>
    /// Backing field of <see cref="SkinRaces"/>.
    /// </summary>
    IReadOnlyDictionary<string, bool>? _skinRaces;

    /// <summary>
    /// Whether a race's bodies scatter light unless chosen otherwise: every person race but a robot one (RACE <c>is robot</c>: skeletons, metal).
    /// </summary>
    public static bool SkinByDefault(GameRecord race) => !race.GetBool("is robot");

    /// <summary>
    /// Whether the skin shading applies to a body material's race: the Skin page's choice, else the material's default.
    /// </summary>
    bool SkinFor(PartMaterial m) => m.Race is not null && _skinRaces is not null && _skinRaces.TryGetValue(m.Race,
        out bool chosen) ? chosen : m.SkinDefault;

    /// <summary>
    /// The person races of the loaded data (base game and mods) for the Skin page: every RACE record no ANIMAL_CHARACTER uses, by name, with its
    /// default.
    /// </summary>
    public static IReadOnlyList<(string Id, string Name, bool Default)> PersonRaces(GameDatabase db)
    {
        var animal = db.Records.Values.Where(r => r.Type == Data.Fcs.FcsRecordType.AnimalCharacter)
            .SelectMany(r => r.GetReferences("race").Select(x => x.TargetStringId)).ToHashSet();
        return [.. db.Records.Values.Where(r => r.Type == Data.Fcs.FcsRecordType.Race && !animal.Contains(r.StringId))
            .OrderBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(r => (r.StringId, r.Name.Trim(), SkinByDefault(r)))];
    }

    // Statistics of the last Update / Draw.
    /// <summary>
    /// Characters posed in the last update.
    /// </summary>
    public int Posed { get; private set; }
    /// <summary>
    /// Characters drawn in the last view.
    /// </summary>
    public int DrawnCharacters { get; private set; }
    /// <summary>
    /// Part instances drawn in the last view.
    /// </summary>
    public int DrawnParts { get; private set; }
    /// <summary>
    /// Triangles drawn in the last view.
    /// </summary>
    public long DrawnTriangles { get; private set; }
    /// <summary>
    /// Draw calls recorded in the last view.
    /// </summary>
    public int DrawCalls { get; private set; }
    /// <summary>
    /// Part instances drawn by mesh LOD level in the last view, and their triangles.
    /// </summary>
    public readonly int[] LevelParts = new int[8];
    /// <summary>
    /// Triangles drawn by mesh LOD level in the last view (see <see cref="LevelParts"/>).
    /// </summary>
    public readonly long[] LevelTriangles = new long[8];
    /// <summary>
    /// CPU time of the last <see cref="Update"/>, in milliseconds.
    /// </summary>
    public double LastUpdateMs { get; private set; }
    /// <summary>
    /// CPU time the last update spent posing, in milliseconds.
    /// </summary>
    public double LastPoseMs { get; private set; }
    /// <summary>
    /// CPU time of the last view's draw recording, in milliseconds.
    /// </summary>
    public double LastDrawCpuMs { get; private set; }
    /// <summary>
    /// How many appearances have a built asset (counted on each read).
    /// </summary>
    public int Assets => _assets.Count(a => a.Value is not null);
    /// <summary>
    /// Loads waiting: the looks drawn this frame that the last <see cref="Update"/> is going to build but has not yet, and the character textures
    /// still to stream in. A look beyond the build reach (a far town's, listed for its portrait or a later approach) is not built until the eye comes
    /// near, so it is not counted: it would keep the background-loading bar up for good.
    /// </summary>
    public int PendingLoads
    {
        get
        {
            // A reused set: the background-loading bar asks every frame.
            _pendingLooks.Clear();
            foreach (var c in Draws.Items)
            {
                if (!_assets.ContainsKey(c.Appearance) && BuildsLook(c, _buildEye, _buildReach))
                    _pendingLooks.Add(c.Appearance);
            }

            foreach (var c in Draws.Distant)
            {
                if (!_assets.ContainsKey(c.Appearance) && BuildsLook(c, _buildEye, _buildReach))
                    _pendingLooks.Add(c.Appearance);
            }

            return _pendingLooks.Count + _content.Textures.AwaitedCount + _content.Textures.UnresidentUsedCount;
        }
    }
    /// <summary>
    /// The set <see cref="PendingLoads"/> counts the waiting looks in, reused between reads.
    /// </summary>
    readonly HashSet<CharacterAppearance> _pendingLooks = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The eye and build reach of the last <see cref="GatherLive"/>, for <see cref="PendingLoads"/> (before the first update nothing is near).
    /// </summary>
    Vector3 _buildEye;
    /// <summary>
    /// The build reach of the last <see cref="GatherLive"/> (see <see cref="_buildEye"/>).
    /// </summary>
    float _buildReach;

    /// <summary>
    /// Whether an update seeing <paramref name="eye"/> and <paramref name="reach"/> builds <paramref name="c"/>'s look when it is missing: not
    /// deferred (<see cref="LoadingFocus"/>), and a portrait's, wanted (<see cref="WantsAsset"/>) or one the loading screen waits for (any not
    /// deferred while a focus is set: the eye's reach is not the focus's, and one waited for but never built held the screen up for good). The one
    /// rule of <see cref="AssetFor"/>, <see cref="UpdateDistant"/> and <see cref="PendingLoads"/>.
    /// </summary>
    bool BuildsLook(in CharacterInstance c, Vector3 eye, float reach) =>
        !Deferred(c) && (LoadingFocus is not null || _portraitKeys.Contains(c.Key) || WantsAsset(c, eye, reach));

    /// <summary>
    /// Current appearances still being prepared/uploaded, or textures still being loaded. Failed assets count as finished. Only looks an update
    /// builds (<see cref="BuildsLook"/>) count: one out of the eye's reach on the title, never built, held its screen up for good.
    /// </summary>
    public bool ContentPending => Draws.Items.Any(c => !_assets.ContainsKey(c.Appearance) && BuildsLook(c, _buildEye, _buildReach))
        || Draws.Distant.Any(c => !_assets.ContainsKey(c.Appearance) && BuildsLook(c, _buildEye, _buildReach))
        || _content.Textures.AwaitedCount > 0 || _content.Textures.UnresidentUsedCount > 0;

    /// <summary>
    /// While set (a game's loading screen): only the characters within <see cref="LoadingReach"/> of this point (x, z) are prepared and counted by
    /// <see cref="ContentPending"/>; the rest wait until it is cleared. The original's loaded zones (the camera's 3 x 3 block) can hold hundreds of
    /// NPCs, and preparing every one of their meshes (their generated levels: up to seconds each) kept the screen up two minutes for a view that
    /// shows a few dozen.
    /// </summary>
    public Vector2? LoadingFocus { get; set; }
    /// <summary>
    /// How far from <see cref="LoadingFocus"/> the loading screen's characters reach (world units).
    /// </summary>
    public float LoadingReach { get; set; } = 3000;

    /// <summary>
    /// Whether <paramref name="c"/> waits for the loading screen to lift (<see cref="LoadingFocus"/>).
    /// </summary>
    bool Deferred(in CharacterInstance c) =>
        LoadingFocus is { } f && Vector2.DistanceSquared(new Vector2(c.Position.X, c.Position.Z), f) > LoadingReach * LoadingReach;
    /// <summary>
    /// The appearance was uploaded or failed with a diagnostic; either completes its loading request.
    /// </summary>
    public bool IsPrepared(CharacterAppearance appearance) => _assets.ContainsKey(appearance);
    /// <summary>
    /// Start preparing a scene's essential characters ahead of the world streaming queue (render thread).
    /// </summary>
    public void Prefetch(CharacterAppearance appearance) => _content.Fetched(appearance, urgent: true);
    /// <summary>
    /// Prepares a look's meshes without keeping the request (the character editor's next looks), on the urgent queue or the ordinary one; a
    /// close-up's without generated levels. Render thread.
    /// </summary>
    public void Warm(CharacterAppearance appearance, bool urgent, bool close = false) => _content.Warm(appearance, urgent, close);
    /// <summary>
    /// Starts preparing a look urgently as a close-up (the character editor's): its meshes without generated levels, which take nearly all of a
    /// mesh's preparation. Render thread.
    /// </summary>
    public void PrefetchCloseUp(CharacterAppearance appearance) => _content.Prefetch(appearance, close: true);
    /// <summary>
    /// The character editor has closed: drops the close-up preparations it warmed and never showed. Render thread.
    /// </summary>
    public void ReleaseCloseUps()
    {
        _content.ReleaseCloseUps();
        ReleaseStage();
    }
    /// <summary>
    /// Problems met while loading the characters' content (<see cref="CharacterContent.Messages"/>).
    /// </summary>
    public List<string> Messages => _content.Messages;

    /// <summary>
    /// One character in range this frame.
    /// </summary>
    struct Live
    {
        /// <summary>
        /// The asset it is drawn with.
        /// </summary>
        public CharacterAsset Asset;
        /// <summary>
        /// Its model-to-world transform this frame.
        /// </summary>
        public Matrix4x4 World;
        /// <summary>
        /// Its bounding sphere's centre in the world.
        /// </summary>
        public Vector3 Centre;
        /// <summary>
        /// Its bounding sphere's radius in the world.
        /// </summary>
        public float Radius;
        /// <summary>
        /// Where its bones start in this frame's palette, where its attach matrices start in <see cref="CharacterRenderer._attach"/>, and the range
        /// of its animation layers in <see cref="CharacterRenderer._layers"/>.
        /// </summary>
        public int BoneBase, AttachBase, LayerStart, LayerCount;
        /// <summary>
        /// Its parts' first entry in <see cref="CharacterRenderer._partFrames"/> (one per asset part).
        /// </summary>
        public int PartBase;
        /// <summary>
        /// What the renderer keeps of it between frames.
        /// </summary>
        public CharState State;
        /// <summary>
        /// <c>Repose</c>: posed this frame (else its last palette is held); the others: the weapon drawn, that weapon the crossbow, the back melee
        /// weapon (as <see cref="AssetPart.ShowsWith"/> takes them).
        /// </summary>
        public bool Repose, WeaponDrawn, RangedDrawn, BackDrawn;
        /// <summary>
        /// The blood of its first 8 body parts, a byte each (<see cref="CharacterInstance.Blood"/>).
        /// </summary>
        public ulong Blood;
        /// <summary>
        /// Posed this frame whatever the thinning (the player's, a ragdoll, a new asset or a first pose).
        /// </summary>
        public bool MustPose;
        /// <summary>
        /// How tall it is on screen, in pixels (its diameter over its distance by the view's pixels per radian): the thinning's priority.
        /// </summary>
        public float Pixels;
        /// <summary>
        /// The bolts loaded in its crossbow (<see cref="AssetPart.ShownShots"/>).
        /// </summary>
        public int Shots;
        /// <summary>
        /// The bolts stuck in it, drawn on their bones (<see cref="CharState.BoltLocal"/>).
        /// </summary>
        public IReadOnlyList<StuckBoltMesh> Bolts;
        /// <summary>
        /// The limb held at its <c>mouth</c> point (<see cref="CharacterInstance.Mouth"/>; null: none).
        /// </summary>
        public MouthMesh? Mouth;
        /// <summary>
        /// The ragdoll's bones over the animation (null: none).
        /// </summary>
        public BonePose? Bones;
        /// <summary>
        /// Its <see cref="CharacterInstance.Key"/>.
        /// </summary>
        public long Key;
        /// <summary>
        /// Beyond <see cref="DrawDistance"/> (or <see cref="CharacterInstance.Hidden"/>) but posed for its portrait (<see cref="PortraitRequests"/>):
        /// not drawn in the world.
        /// </summary>
        public bool PortraitOnly;
        /// <summary>
        /// In a loaded building's interior (<see cref="WorldObjectRenderer.InteriorAt"/>), found once a frame for every view.
        /// </summary>
        public bool Interior;
        /// <summary>
        /// Holds bones taken from another character (<see cref="CharacterInstance.FreezeFrom"/>): never posed.
        /// </summary>
        public bool Frozen;
        /// <summary>
        /// Neither it, its shadow nor its mirror image can be in view (<see cref="Shown"/>), and it is no portrait: not posed, its last palette held.
        /// </summary>
        public bool OffScreen;
        /// <summary>
        /// Posed this frame with no motion from its last palette: it has none yet, or it comes back on screen after poses were skipped.
        /// </summary>
        public bool Snap;
    }

    /// <summary>
    /// What the renderer keeps of a character between frames (by <see cref="CharacterInstance.Key"/>): its last posed palette and the one before.
    /// </summary>
    sealed class CharState
    {
        /// <summary>
        /// The last posed bone palette and the one before it (for motion vectors), and the same for the attach matrices.
        /// </summary>
        public Matrix4x4[] Bones = [], Previous = [], Attach = [], PreviousAttach = [];
        /// <summary>
        /// The last drawn model-to-world transform and the one before it.
        /// </summary>
        public Matrix4x4 World, PreviousWorld;
        /// <summary>
        /// Whether <see cref="World"/> has been set (the first frame's previous transform is the current one).
        /// </summary>
        public bool HasWorld;
        /// <summary>
        /// The update stamp it was last posed in (-1: never) and the one it was last seen in (<see cref="EvictStates"/> forgets it after 60).
        /// </summary>
        public long Posed = -1, Seen;
        /// <summary>
        /// The pose-rate period the character was last seen in (<see cref="PosePixelSteps"/>).
        /// </summary>
        public long PoseBucket = long.MinValue;
        /// <summary>
        /// What the last update drew it with, for picking (<see cref="RayHit"/>): its asset, weapon, bounding sphere, and whether it is drawn in the
        /// world.
        /// </summary>
        public CharacterAsset? Asset;
        /// <summary>
        /// The weapon state of the last update (see <see cref="Live.Repose"/>), and whether it is drawn in the world.
        /// </summary>
        public bool WeaponDrawn, RangedDrawn, BackDrawn, InWorld;
        /// <summary>
        /// Each stuck bolt's transform in its bone's frame, fixed the first time it was drawn (by bolt id).
        /// </summary>
        public readonly Dictionary<int, Matrix4x4> BoltLocal = [];
        /// <summary>
        /// The bounding sphere's centre in the world at the last update (<see cref="RayHit"/>).
        /// </summary>
        public Vector3 Centre;
        /// <summary>
        /// The bounding sphere's radius at the last update.
        /// </summary>
        public float Radius;
        /// <summary>
        /// Its last indoors answer (<see cref="Live.Interior"/>), asked again once it has moved.
        /// </summary>
        public WorldObjectRenderer.InteriorMemo Interior;
        /// <summary>
        /// Its bones were taken from another character (<see cref="Freeze"/>) and are never posed.
        /// </summary>
        public bool Frozen;
        /// <summary>
        /// The ragdoll bones it was last posed with: the same object again (a frozen ragdoll) needs no pose every frame.
        /// </summary>
        public BonePose? PosedRagdoll;
        /// <summary>
        /// Off screen at the last update (<see cref="Live.OffScreen"/>).
        /// </summary>
        public bool OffScreen;
    }

    /// <summary>
    /// Gives the character <paramref name="key"/> the bones of <paramref name="from"/> as last posed, held from now on and never posed (a cut-off
    /// head keeps the pose of the moment it was cut, taken before its own look is ready). False when <paramref name="from"/> has no posed bones.
    /// Render thread, between updates.
    /// </summary>
    public bool HoldPose(long key, long from)
    {
        if (!_states.TryGetValue(from, out var source) || source.Posed < 0 || source.Bones.Length == 0) return false;
        _states[key] = new CharState { Bones = [.. source.Bones], Previous = [.. source.Bones], Posed = _updateStamp, Seen = _updateStamp,
            Frozen = true };
        return true;
    }

    /// <summary>
    /// <see cref="CharacterInstance.FreezeFrom"/>: the held bones (<see cref="HoldPose"/>, taken now if they were not), sized for
    /// <paramref name="asset"/>; false while there are none for its skeleton, and the character is not drawn.
    /// </summary>
    bool Freeze(long key, long from, CharacterAsset asset)
    {
        if ((!_states.TryGetValue(key, out var own) || !own.Frozen) && (!HoldPose(key, from) || !_states.TryGetValue(key, out own)))
            return false;
        if (own.Bones.Length != asset.Rig.BoneCount) return false;
        if (own.Attach.Length != asset.AttachCount)
            (own.Attach, own.PreviousAttach) = (new Matrix4x4[asset.AttachCount], new Matrix4x4[asset.AttachCount]);
        return true;
    }

    /// <summary>
    /// What is kept of each character between frames, by <see cref="CharacterInstance.Key"/>; evicted once unseen (<see cref="EvictStates"/>).
    /// </summary>
    readonly Dictionary<long, CharState> _states = [];
    /// <summary>
    /// The thinning by size on screen (engine choice, a performance measure: the original poses every animated character every frame): a character at
    /// least as many pixels tall as the first step is posed every frame, smaller ones at most 30, 15 and 7.5 times a second past each step (a skipped
    /// frame holds the last palette), each on a phase of its own so the poses spread over the frames. At most <see cref="FullRateBudget"/>
    /// characters, the largest on screen first, take the full rate; the next ones at least the second rate.
    /// <see cref="CharacterInstance.FullRateAnimation"/> (the player's first characters and the selected one) always keeps the full rate.
    /// </summary>
    public float[] PosePixelSteps { get; set; } = [100, 50, 25];
    /// <summary>
    /// The most characters posed at the full rate by their size alone (the nearest, largest first).
    /// </summary>
    public int FullRateBudget { get; set; } = 48;
    /// <summary>
    /// Pixels per radian of the view (<see cref="SetView"/>), for <see cref="Live.Pixels"/>.
    /// </summary>
    float _viewPixelsPerRadian = Data.World.TerrainLod.ProjectionScale(1080, 50 * MathF.PI / 180);
    /// <summary>
    /// With fewer characters than this drawn in the world, none is thinned: every one is posed every frame.
    /// </summary>
    const int ThinFromCharacters = 100;
    /// <summary>
    /// The pose rate inside the first step's distance, halved past each step (in poses per second, a frame rate above it poses every frame).
    /// </summary>
    const double PoseRateHz = 60;
    /// <summary>
    /// The seconds the pose rates count in (the game's animation clock, which a recorded flight steps by its frame time, so a take poses the same
    /// however slowly it is drawn); null: the real time.
    /// </summary>
    public Func<double>? PoseClock { get; set; }

    /// <summary>
    /// The pose clock's seconds at the start of this update (<see cref="PoseClock"/>, else the real time), for the thinning's buckets.
    /// </summary>
    double _poseSeconds;

    /// <summary>
    /// Whether a character placed at <paramref name="position"/> can come within <see cref="DrawDistance"/> of <paramref name="eye"/> this frame,
    /// with a margin for the largest bodies' size: the host leaves out the poses and the ragdoll of the rest, which <see cref="Update"/> never reads
    /// (only their look and place, for loading).
    /// </summary>
    public bool InReach(Vector3 position, Vector3 eye, float distanceScale = 1) => Vector3.DistanceSquared(position,
        eye) <= (DrawDistance * distanceScale + ReachMargin) * (DrawDistance * distanceScale + ReachMargin);
    /// <summary>
    /// The margin added to <see cref="DrawDistance"/> for the largest bodies' size, in world units.
    /// </summary>
    const float ReachMargin = 2000;
    /// <summary>
    /// The distance within which a character may be drawn this frame (<see cref="DrawDistance"/> plus the margin): the simulation's near radius for
    /// its far cadence.
    /// </summary>
    public float NearReach => DrawDistance + ReachMargin;

    /// <summary>
    /// The planes of the view the characters are shown in this frame (the host sets them before <see cref="Update"/>), for the off-screen rule
    /// (<see cref="Shown"/>); null: every character in range counts as shown.
    /// </summary>
    public Vector4[]? ShownView { get; set; }
    /// <summary>
    /// The water's height while the water reflection shows characters (else null): a character whose mirror image is in view counts as shown.
    /// </summary>
    public float? ShownMirror { get; set; }
    /// <summary>
    /// The light the last colour view was drawn with (towards the sun or moon): its shadows are cast away from it.
    /// </summary>
    Vector3 _shownLight = Vector3.UnitY;

    /// <summary>
    /// Whether a character's sphere can show in this frame's view (engine choice, a performance measure): the sphere itself, its shadow (the sphere
    /// swept away from the light as far as a shadow of its size can fall, at a light at least 3° high) or its mirror image in the water. A character
    /// not shown is not posed: its held palette is still drawn (a far shadow cascade is kept for up to 32 frames, and would miss it when it comes
    /// into view), and it is posed again, with no motion from that palette, the frame it comes back.
    /// </summary>
    bool Shown(Vector3 centre, float radius)
    {
        if (ShownView is not { } planes) return true;
        var r = new Vector3(radius);
        if (WorldCamera.Intersects(planes, centre - r, centre + r)) return true;
        var light = _shownLight;
        float up = MathF.Max(MathF.Abs(light.Y), 0.05f);
        var tip = centre - new Vector3(light.X, up, light.Z) * (2 * radius / up);
        if (WorldCamera.Intersects(planes, Vector3.Min(centre, tip) - r, Vector3.Max(centre, tip) + r)) return true;
        if (ShownMirror is not { } water) return false;
        var mirrored = new Vector3(centre.X, 2 * water - centre.Y, centre.Z);
        return WorldCamera.Intersects(planes, mirrored - r, mirrored + r);
    }

    /// <summary>
    /// The pose interval, in steps of the full rate, for a character <paramref name="pixels"/> tall: doubled past each of
    /// <see cref="PosePixelSteps"/>.
    /// </summary>
    int PoseInterval(float pixels)
    {
        int interval = 1;
        foreach (float step in PosePixelSteps)
            if (pixels < step) interval *= 2;
        return interval;
    }

    /// <summary>
    /// This frame's characters in range (the first <see cref="_liveCount"/>), grown as needed and reused.
    /// </summary>
    Live[] _live = new Live[256];
    /// <summary>
    /// Scratch: the live indices sorted by size for the thinning, reused.
    /// </summary>
    int[] _thinOrder = [];
    /// <summary>
    /// How many entries of <see cref="_live"/> are this frame's.
    /// </summary>
    int _liveCount;
    /// <summary>
    /// This frame's animation layers of every live character (the first <see cref="_layerCount"/>), grown as needed.
    /// </summary>
    ActiveLayer[] _layers = new ActiveLayer[1024];
    /// <summary>
    /// How many entries of <see cref="_layers"/> are this frame's.
    /// </summary>
    int _layerCount;
    /// <summary>
    /// This frame's attach matrices of every live character (the first <see cref="_attachCount"/>), grown as needed.
    /// </summary>
    Matrix4x4[] _attach = new Matrix4x4[256];
    /// <summary>
    /// How many entries of <see cref="_attach"/> are this frame's.
    /// </summary>
    int _attachCount;
    /// <summary>
    /// This frame's bone palettes and the previous ones, in host-visible frame memory (the same slice when motion vectors are off).
    /// </summary>
    Transient _bones, _previousBones;
    /// <summary>
    /// The parts shown on the live characters this frame, each once, whose materials the update writes.
    /// </summary>
    readonly List<AssetPart> _slotParts = [];
    /// <summary>
    /// The update's count, the stamp the states, materials and parts are marked with.
    /// </summary>
    long _updateStamp;
    /// <summary>
    /// The character textures' memory governor.
    /// </summary>
    readonly CharacterTextureBudget _budget = new();

    /// <summary>
    /// Tells the character textures' mip streaming the camera: the render size in pixels and the vertical field of view (the upscaler's LOD bias is
    /// the context's).
    /// </summary>
    public void SetView(int width, int height, float fieldOfView)
    {
        _content.Textures.Mips.SetView(width, height, fieldOfView, Gpu.LodBias);
        _content.LodPixelsPerRadian = Data.World.TerrainLod.ProjectionScale(height,
            fieldOfView);   // the generated levels are placed by their deviation in rendered pixels
        _viewPixelsPerRadian = _content.LodPixelsPerRadian;
        _viewKnown = true;
    }

    /// <summary>
    /// Whether <see cref="SetView"/> has been called: until then nothing is thinned or culled as tiny.
    /// </summary>
    bool _viewKnown;
    /// <summary>
    /// Wait, at the next <see cref="Update"/> after <see cref="SetView"/>, for every asset and texture (stills).
    /// </summary>
    public bool SettleFirst { get; set; }
    /// <summary>
    /// The device's texture LOD bias at the start of this update, which the bindless texture indices are made for.
    /// </summary>
    float _textureBias;
    /// <summary>
    /// The device's stand-in texture for slots whose texture is missing or not resident.
    /// </summary>
    uint _textureStandIn;

    /// <summary>
    /// What every material record depends on besides its material: the texture cache's version, the bias, the stand-in and the samplers. A change (or
    /// a skin setting's) starts a new epoch, and each record is written again when next used.
    /// </summary>
    (int, float, uint, int) _materialInputs;
    /// <summary>
    /// The material records' epoch (see <see cref="_materialInputs"/>); a record written in an older one is written again.
    /// </summary>
    int _materialEpoch = 1;

    /// <summary>
    /// Compiles the colour, depth and motion programs and makes the content, the skin table and the pass timers.
    /// </summary>
    public CharacterRenderer(IGpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assetLocator, CharacterRigs? rigs = null)
    {
        Gpu = gpu;
        _nativeFrame = new NativeFrame(gpu, extraStorage: 4);
        var colourFragment = CharacterShaders.Fragment(gpu.Shaders.DiscardNeedsLateDepth);
        _colourProg = new ReflectedProgram(gpu, _nativeFrame, CharacterShaders.Vertex(),
            Gi.GiResolveShaders.WithAlbedo(colourFragment, inShader: true), "characters", CharacterShaders.InstanceLocation);
        _depthProg = new ReflectedProgram(gpu, _nativeFrame, CharacterShaders.DepthVertex(), CharacterShaders.DepthFragment(), "characters depth",
            CharacterShaders.InstanceLocation);
        _motionProg = new ReflectedProgram(gpu, _nativeFrame, CharacterShaders.MotionVertex(), CharacterShaders.MotionFragment(), "characters motion",
            CharacterShaders.InstanceLocation);
        _content = new CharacterContent(gpu, install, db, assetLocator, rigs);
        _skinScattering = new SkinScattering(gpu);
        _colourTimer = new PassTimer(gpu);
        _depthTimer = new PassTimer(gpu);
    }

    /// <summary>
    /// The VRAM guard the character texture cache answers to (null: none).
    /// </summary>
    public VramGuard? Guard { get => _content.Textures.Guard; set => _content.Textures.Guard = value; }
    /// <summary>
    /// The characters' texture cache (the VRAM dump lists it).
    /// </summary>
    public WorldTextureCache Textures => _content.Textures;

    /// <summary>
    /// The skeleton rigs and pose setups the characters are drawn with; the simulation's ragdolls pose from the same ones.
    /// </summary>
    public CharacterRigs Rigs => _content.Rigs;

    /// <summary>
    /// Rigs that find files as the renderer does, to share with the simulation
    /// (<see cref="CharacterRenderer(IGpuContext, GameInstall, GameDatabase, AssetLocator, CharacterRigs?)"/>).
    /// </summary>
    public static CharacterRigs CreateRigs(GameDatabase db, AssetLocator assets) => CharacterContent.CreateRigs(db, assets);

    // ------------------------------------------------------------------ per frame

    /// <summary>
    /// How far back along its flight a stuck bolt sits from its bone's origin (engine choice: the original takes 0.6 times a random vertex of the
    /// body near the bone).
    /// </summary>
    const float BoltDepth = 2;

    /// <summary>
    /// The bolts stuck in this update's posed characters (docs/game/combat.md "Bolt visuals"), as loose meshes into <paramref name="into"/> (cleared
    /// first), after <see cref="Update"/>: each fixed to its bone the first time it is seen, its +z along the flight and pulled back from the bone's
    /// origin by <see cref="BoltDepth"/>, then carried by the bone (a ragdoll's included), as the original attaches it to the bone keeping its world
    /// orientation (<c>FUN_14052bfa0</c>). An animal's held limb comes with them (<see cref="AddMouth"/>).
    /// </summary>
    public void CollectBolts(List<DebrisPiece> into)
    {
        into.Clear();
        for (int i = 0; i < _liveCount; i++)
        {
            ref var l = ref _live[i];
            AddMouth(ref l, into);
            var state = l.State;
            if (l.Bolts is not { Count: > 0 } bolts || l.PortraitOnly || l.Asset.IsIcon)
            {
                if (l.Bolts is not { Count: > 0 }) state?.BoltLocal.Clear();
                continue;
            }
            var rig = l.Asset.Rig;
            if (state.Bones.Length != rig.BoneCount) continue;
            if (state.BoltLocal.Count > bolts.Count)
            {
                foreach (int id in state.BoltLocal.Keys.ToArray())
                    if (!bolts.Any(b => b.Id == id)) state.BoltLocal.Remove(id);
            }

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

    /// <summary>
    /// The limb an animal holds (<see cref="CharacterInstance.Mouth"/>) as a loose mesh at its posed <c>mouth</c> point
    /// (<see cref="CharacterAsset.Mouth"/>), sized with the animal; nothing when its race has no such point (docs/game/character-stats.md "Mouth").
    /// </summary>
    void AddMouth(ref Live l, List<DebrisPiece> into)
    {
        if (l.Mouth is not { } held || l.PortraitOnly || l.Asset.IsIcon || l.Asset.Mouth is not { } mouth || l.State is not { Posed: >= 0 } state)
            return;
        if (mouth.AttachIndex >= state.Attach.Length || !Matrix4x4.Decompose(state.Attach[mouth.AttachIndex] * l.World, out var scale,
                out var rotation, out var position))
            return;
        into.Add(new DebrisPiece(held.Mesh, scale.X, rotation, position, 1, held.Look));
    }

    /// <summary>
    /// The rotation that turns +z onto <paramref name="to"/> (unit length).
    /// </summary>
    static Quaternion TurnZ(Vector3 to)
    {
        var axis = Vector3.Cross(Vector3.UnitZ, to);
        float s = axis.Length();
        if (s < 1e-5f) return to.Z > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        return Quaternion.CreateFromAxisAngle(axis / s, MathF.Atan2(s, to.Z));
    }

    /// <summary>
    /// Adds the carried lights of this update's posed characters to <paramref name="into"/>, at their world positions (after <see cref="Update"/>).
    /// </summary>
    public void CollectLamps(List<Data.World.WorldLight> into)
    {
        for (int i = 0; i < _liveCount; i++)
        {
            ref var l = ref _live[i];
            if (l.PortraitOnly || l.Asset.IsIcon || l.Asset.Lamps.Count == 0) continue;
            foreach (var lamp in l.Asset.Lamps)
            {
                if (lamp.AttachIndex < l.State.Attach.Length)
                    into.Add(lamp.Light with { Position = (l.State.Attach[lamp.AttachIndex] * l.World).Translation });
            }
        }
    }

    /// <summary>
    /// The frame's characters: the list from <see cref="Source"/>, assets built for new appearances (at most <see cref="LoadBudget"/>), every
    /// character within <see cref="DrawDistance"/> of <paramref name="eye"/> posed in parallel into the frame's bone palettes, the textures'
    /// bindless indices and the parts' materials written. Render thread; before the frame's first <see cref="Draw"/> or <see cref="DrawDepth"/>.
    /// </summary>
    public void Update(Vector3 eye)
    {
        if (SettleFirst && _viewKnown) SettleNow(eye);
        PollTimers();
        var watch = Stopwatch.StartNew();
        Draws.Clear();
        Source?.Invoke(Draws);
        StageClock.Part(StageClock.PartFill);

        GatherPortraitKeys();
        _content.Textures.Pump(wait: LoadBudget == 0, max: Covered ? 64 : 8, budgetMs: Covered ? CoveredBudgetMs : 1.0);
        StageClock.Part(StageClock.PartTextures);

        _textureBias = Gpu.LodBias;
        _textureStandIn = Gpu.StandIn2D;
        _updateStamp++;
        _poseSeconds = PoseClock?.Invoke() ?? Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        // 1. Assets and the characters in range, with their layers resolved (the rig's caches are filled here, on this thread), and which to pose.
        var (boneTotal, partTotal) = GatherLive(eye);
        ChooseReposes();
        Posed = 0;
        for (int i = 0; i < _liveCount; i++) if (_live[i].Repose) Posed++;
        if (_updateStamp % 120 == 0) EvictStates();
        GatherIcons(eye, ref _attachCount);
        if (_attach.Length < _attachCount) _attach = new Matrix4x4[Math.Max(_attachCount, _attach.Length * 2)];
        StageClock.Part(StageClock.PartGather);
        double gatherMs = watch.Elapsed.TotalMilliseconds;

        // 2. The bone palettes, posed in parallel straight into the frame's memory.
        PoseLive(eye, boneTotal, partTotal);
        PoseIcons();
        StageClock.Part(StageClock.PartPose);

        // 3. The materials of the parts in range.
        WriteMaterials(eye);
        StageClock.Part(StageClock.PartMaterials);

        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
        _updateCount++;
        _updateCpu.Add(LastUpdateMs);
        _poseCpu.Add(LastPoseMs);
        _gatherCpu.Add(gatherMs);
    }

    /// <summary>
    /// The first update after <see cref="SettleFirst"/> and the view: every asset and texture waited for (stills).
    /// </summary>
    void SettleNow(Vector3 eye)
    {
        SettleFirst = false;
        var watch = Stopwatch.StartNew();
        Settle(eye);
        Console.WriteLine($"characters built and loaded: {Describe()} ({watch.ElapsedMilliseconds} ms)");
        foreach (var m in Messages.Distinct().Take(20)) Console.WriteLine($"warning   {m}");
        Messages.Clear();
    }

    /// <summary>
    /// The live list: each drawn character within reach (or with a portrait) whose asset is built, with its layers; those beyond reach near a kept
    /// point go to <see cref="_keptLooks"/>. Builds new assets under the budget, then the distant list's share (<see cref="UpdateDistant"/>). Returns
    /// the bone and part totals.
    /// </summary>
    (int Bones, int Parts) GatherLive(Vector3 eye)
    {
        _liveCount = _layerCount = _attachCount = 0;
        (_tinyBefore, _tinyNow) = (_tinyNow, _tinyBefore);
        _tinyNow.Clear();
        int built = 0, boneTotal = 0, partTotal = 0;
        double buildMs = 0;
        float reach = EyeLift.Of(eye, GroundUnderEye) > 0 ? Math.Min(DrawDistance, LiftedReach) : DrawDistance;
        var items = Draws.Items;
        _keptLooks.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            var inst = items[i];

            // Too small to show (Meitou's choice, docs/character-renderer.md "Tiny characters"): no build, pose or draw; the simulation and the
            // host's lists still hold it.
            _assets.TryGetValue(inst.Appearance, out var known);
            if (CullTiny(in inst, eye, known)) continue;
            if (AssetFor(in inst, eye, reach * inst.DrawDistanceScale, ref built, ref buildMs) is not { } asset)
                continue;
            if (inst.FreezeFrom is { } freezeFrom && !Freeze(inst.Key, freezeFrom, asset)) continue;
            if (inst.Build is { } build) ApplyBuild(asset, build);
            if (inst.BlendFrom is { } blendFrom && inst.Blend < 1 && _assets.TryGetValue(blendFrom, out var fromAsset) && fromAsset is not null)
                ApplyBlend(asset, fromAsset, inst.Blend);
            else RestoreBlend(asset);

            float scale = inst.Scale > 0 ? inst.Scale : 1;
            var centre = inst.Position + new Vector3(0, asset.Height * 0.5f * scale, 0);

            // FUN_1405c9f60: the position's distance, skipped for a gigantic race.
            bool far = inst.Hidden || Vector3.Distance(eye, centre) - asset.Radius * scale > reach * inst.DrawDistanceScale
                || inst.DrawDistanceScale <= 1 && Vector3.DistanceSquared(eye, inst.Position) > reach * reach;
            if (far && !_portraitKeys.Contains(inst.Key))
            {
                if (!inst.Hidden && Kept.Count > 0 && NearKept(centre) is var dk && dk <= KeptReach)
                    _keptLooks.Add((asset, dk));
                continue;
            }

            ref var l = ref AddLive(in inst, asset, centre, scale, far, eye);
            l.BoneBase = boneTotal;
            boneTotal += asset.Rig.BoneCount;
            l.PartBase = partTotal;
            partTotal += asset.Parts.Count;
            if (l.OffScreen && !l.MustPose) continue;   // not posed this frame: its clips need not be looked up
            AddLayers(ref l, in inst);
        }

        (_buildEye, _buildReach) = (eye, reach);
        UpdateDistant(eye, reach, ref built, ref buildMs);
        return (boneTotal, partTotal);
    }

    /// <summary>
    /// The character's asset, built now when it is missing and wanted (at most <see cref="LoadBudget"/> a frame, within the build budget); null when
    /// it is not built (deferred, not wanted, over budget, or a failed build).
    /// </summary>
    CharacterAsset? AssetFor(in CharacterInstance inst, Vector3 eye, float reach, ref int built, ref double buildMs)
    {
        if (_assets.TryGetValue(inst.Appearance, out var asset)) return asset;
        if (!BuildsLook(inst, eye, reach)) return null;

        // Interactive: the files are read and the meshes prepared on a worker first (CharacterContent.Fetched), then a few a frame are uploaded.
        bool overBudget = !Covered && built >= LoadBudget || buildMs >= (Covered ? CoveredBudgetMs : BuildBudgetMs);
        if (LoadBudget > 0 && (!_content.Fetched(inst.Appearance) || overBudget)) return null;

        long buildStart = Stopwatch.GetTimestamp();
        asset = _content.Build(inst.Appearance);
        buildMs += Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds;
        _assets[inst.Appearance] = asset;
        if (asset is not null && inst.Appearance?.BodyMesh is { Length: > 0 } body)
            _bodyHeights[body] = Math.Max(_bodyHeights.GetValueOrDefault(body), asset.Height);
        built++;
        return asset;
    }

    /// <summary>
    /// Projected height, in render pixels, under which a character is not built, posed or drawn; it comes back above <see cref="TinyOff"/>
    /// (hysteresis, so one at the edge does not flicker).
    /// </summary>
    const float TinyOn = 1.5f, TinyOff = 2.0f;
    /// <summary>
    /// The tallest asset built per body mesh, so a look not built yet can be judged before its build.
    /// </summary>
    readonly Dictionary<string, float> _bodyHeights = [];
    /// <summary>
    /// The characters culled as tiny in the last update and in this one (swapped each update), for the hysteresis of <see cref="CullTiny"/>.
    /// </summary>
    HashSet<long> _tinyBefore = [], _tinyNow = [];

    /// <summary>
    /// Whether the character's projected height is under <see cref="TinyOn"/> render pixels (<see cref="TinyOff"/> once it was): a speck the original
    /// draws and Meitou does not. Never the player's, a portrait's, a hidden or an editor character, not one near a kept point (its look is built
    /// ahead), and none under a game's loading screen (it waits for their looks, <see cref="LoadingFocus"/>). Without a built asset the body mesh's
    /// known height is used.
    /// </summary>
    bool CullTiny(in CharacterInstance inst, Vector3 eye, CharacterAsset? asset)
    {
        if (!_viewKnown || LoadingFocus is not null || inst.FullRateAnimation || inst.Hidden || _portraitKeys.Contains(inst.Key))
            return false;
        float height = asset?.Height ?? (inst.Appearance.BodyMesh is { Length: > 0 } body && _bodyHeights.TryGetValue(body, out var h) ? h : 0);
        if (height <= 0) return false;
        float scale = inst.Scale > 0 ? inst.Scale : 1;
        float distance = MathF.Max(1, Vector3.Distance(eye, inst.Position));
        float pixels = height * scale * _viewPixelsPerRadian / distance;
        if (pixels >= (_tinyBefore.Contains(inst.Key) ? TinyOff : TinyOn)) return false;
        if (asset is null && (Ahead is not null || Kept.Count > 0)) return false;
        _tinyNow.Add(inst.Key);
        return true;
    }

    /// <summary>
    /// A new live entry for the character (<paramref name="far"/>: drawn for its portrait only), its state brought up to date.
    /// </summary>
    ref Live AddLive(in CharacterInstance inst, CharacterAsset asset, Vector3 centre, float scale, bool far, Vector3 eye)
    {
        if (_liveCount == _live.Length) Array.Resize(ref _live, _liveCount * 2);
        ref var l = ref _live[_liveCount++];
        l.Asset = asset;
        (l.Key, l.PortraitOnly) = (inst.Key, far);
        l.World = WorldOf(in inst, asset, scale);
        (l.WeaponDrawn, l.RangedDrawn, l.BackDrawn, l.Shots, l.Bolts) = (inst.WeaponDrawn, inst.RangedDrawn, inst.BackDrawn, inst.Shots, inst.Bolts);
        l.Mouth = inst.Mouth;
        l.Centre = centre;
        l.Radius = asset.Radius * scale;
        l.AttachBase = _attachCount;
        _attachCount += asset.AttachCount;
        l.Bones = inst.Bones;
        l.Blood = inst.Blood;
        l.Pixels = 2 * l.Radius * _viewPixelsPerRadian / MathF.Max(1, Vector3.Distance(eye, centre));
        l.LayerStart = _layerCount;
        l.LayerCount = 0;
        Track(ref l, in inst, asset, far);
        return ref l;
    }

    /// <summary>
    /// The character's world matrix: lowered by the body shape's lift, so a short body's feet stay on the ground (CharacterAsset.GroundOffset); an
    /// animal scaled by its size (its node's scale). A carried body's whole rotation lowers it in its own frame (CharacterRigs.Placement, as the
    /// simulation poses its ragdoll).
    /// </summary>
    static Matrix4x4 WorldOf(in CharacterInstance inst, CharacterAsset asset, float scale)
    {
        if (inst.World is { } world) return world;
        var placed = inst.Rotation is { } rotation
            ? Matrix4x4.CreateTranslation(0, -asset.GroundOffset, 0) * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(inst.Position)
            : Matrix4x4.CreateRotationY(inst.Yaw) * Matrix4x4.CreateTranslation(inst.Position - new Vector3(0, asset.GroundOffset, 0));
        return scale != 1 ? Matrix4x4.CreateScale(scale) * placed : placed;
    }

    /// <summary>
    /// The character's kept state brought up to date, and whether it must be posed this frame and whether it can show.
    /// </summary>
    void Track(ref Live l, in CharacterInstance inst, CharacterAsset asset, bool far)
    {
        if (!_states.TryGetValue(inst.Key, out var state)) _states[inst.Key] = state = new CharState();
        state.Seen = _updateStamp;
        l.State = state;
        l.Interior = !far && InteriorObjects?.InteriorAt(l.Centre, ref state.Interior) is not null;

        bool newAsset = !ReferenceEquals(state.Asset, asset);
        (state.Asset, state.WeaponDrawn, state.RangedDrawn, state.BackDrawn) = (asset, inst.WeaponDrawn, inst.RangedDrawn, inst.BackDrawn);
        (state.InWorld, state.Centre, state.Radius) = (!far, l.Centre, l.Radius);
        state.PreviousWorld = state.HasWorld ? state.World : l.World;
        (state.World, state.HasWorld) = (l.World, true);

        // A moving ragdoll is posed every frame: its bones move by physics, not by a clip the thinning could thin out (a frozen one hands over the
        // same bones). A new asset (other gear, another rig) is posed at once: its parts' attach matrices and its arrays' sizes come from it. The
        // rest are thinned by their size on screen after the gather (ChooseReposes).
        l.Frozen = state.Frozen;
        bool newRagdollPose = inst.Bones is not null && !ReferenceEquals(inst.Bones, state.PosedRagdoll);
        l.MustPose = !l.Frozen && (inst.FullRateAnimation || newRagdollPose || state.Posed < 0 || newAsset);

        // Off screen: no pose but the first one of an asset (its palette's size); back on screen: posed now, without motion from the held palette.
        l.OffScreen = !l.Frozen && !_portraitKeys.Contains(inst.Key) && !Shown(l.Centre, l.Radius);
        l.Snap = !l.OffScreen && state.OffScreen;
        if (l.OffScreen) l.MustPose = state.Posed < 0 || newAsset;
        else if (l.Snap) l.MustPose = !l.Frozen;
        state.OffScreen = l.OffScreen;
    }

    /// <summary>
    /// The animation layers the character plays (its poses' clips on its rig), then its asset's fixed ones.
    /// </summary>
    void AddLayers(ref Live l, in CharacterInstance inst)
    {
        var asset = l.Asset;
        var rig = asset.Rig;
        if (inst.Poses is { } poses)
        {
            for (int p = 0; p < poses.Count; p++)
            {
                if (rig.Layer(_content.Database, poses[p].Animation, asset.Animal) is { } def)
                    AddLayer(new ActiveLayer(def, poses[p].Time, poses[p].Weight));
            }
        }

        foreach (var (def, time) in asset.Fixed) AddLayer(new ActiveLayer(def, time, 1));
        l.LayerCount = _layerCount - l.LayerStart;
    }

    /// <summary>
    /// Which characters are posed this frame: all that can show while fewer than <see cref="ThinFromCharacters"/> are in the world; else thinned by
    /// their size on screen, the largest first (they keep the full rate up to the budget, the rest at least the second rate).
    /// </summary>
    void ChooseReposes()
    {
        int inWorld = 0;
        for (int i = 0; i < _liveCount; i++) if (!_live[i].PortraitOnly) inWorld++;
        if (inWorld < ThinFromCharacters)
        {
            for (int i = 0; i < _liveCount; i++) _live[i].Repose = !_live[i].Frozen && (!_live[i].OffScreen || _live[i].MustPose);
            return;
        }

        if (_thinOrder.Length < _liveCount) _thinOrder = new int[Math.Max(_liveCount, _thinOrder.Length * 2)];
        for (int i = 0; i < _liveCount; i++) _thinOrder[i] = i;
        var order = _thinOrder.AsSpan(0, _liveCount);
        var lives = _live;
        order.Sort((a, b) => lives[b].Pixels.CompareTo(lives[a].Pixels));

        int full = 0;
        foreach (int i in order)
        {
            ref var l = ref _live[i];
            if (l.OffScreen)
            {
                l.Repose = l.MustPose;
                continue;
            }
            int interval = PoseInterval(l.Pixels);
            if (interval == 1 && full++ >= FullRateBudget) interval = 2;

            // In time, not frames (a slow frame rate must not thin the poses out further): a new pose each time the character's own clock enters a
            // new period.
            double phase = unchecked((ulong)l.Key * 0x9E3779B97F4A7C15UL) / 18446744073709551616.0;
            long bucket = interval == 1 ? _updateStamp : (long)Math.Floor(_poseSeconds * PoseRateHz / interval + phase);
            l.Repose = !l.Frozen && (l.MustPose || bucket != l.State.PoseBucket);
            l.State.PoseBucket = bucket;
        }
    }

    /// <summary>
    /// The frame's bone palettes (and, with motion, last frame's), each character posed or its held palette copied in parallel, then its parts
    /// placed.
    /// </summary>
    void PoseLive(Vector3 eye, int boneTotal, int partTotal)
    {
        var watch = Stopwatch.StartNew();

        // Not cleared: every live character's range is written below (an icon has none), so zeroing megabytes first was wasted.
        _bones = Gpu.Frame.AllocateConstants((ulong)Math.Max(boneTotal, 1) * 64, 256);
        nint bonePointer = (nint)_bones.Pointer;
        _previousBones = Motion ? Gpu.Frame.AllocateConstants((ulong)Math.Max(boneTotal, 1) * 64, 256) : _bones;
        nint previousPointer = (nint)_previousBones.Pointer;
        bool motion = Motion;
        if (_partFrames.Length < partTotal) _partFrames = new PartFrame[Math.Max(partTotal, _partFrames.Length * 2)];
        if (_liveCount > 0)
            Parallel.For(0, _liveCount, i => PoseOne(ref _live[i], bonePointer, previousPointer, motion, eye));
        (_partStamp, _partOrigin, _partMotion) = (_updateStamp, eye, motion);
        LastPoseMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// One character's palette: posed again when it reposes (its attach points and lamps too), then copied into the frame's palettes.
    /// </summary>
    void PoseOne(ref Live l, nint bonePointer, nint previousPointer, bool motion, Vector3 eye)
    {
        var asset = l.Asset;
        if (asset.IsIcon) return;   // no rig, so no range in the palettes (GatherIcons gives it no bones)
        var rig = asset.Rig;

        var state = l.State;
        if (l.Repose) Repose(ref l, state);

        state.Bones.CopyTo(new Span<Matrix4x4>((byte*)bonePointer + (long)l.BoneBase * 64, rig.BoneCount));
        state.Attach.CopyTo(_attach, l.AttachBase);
        if (motion)
        {
            // Last frame's palette: what was posed before this repose, or the current one when this frame did not repose (no bone motion).
            (l.Repose ? state.Previous : state.Bones).CopyTo(new Span<Matrix4x4>((byte*)previousPointer + (long)l.BoneBase * 64, rig.BoneCount));
            if (!l.Repose) state.Attach.CopyTo(state.PreviousAttach, 0);
        }
        if (!l.PortraitOnly) PlaceParts(in l, eye, motion);
    }

    /// <summary>
    /// Poses the character's bones, attach points and lamps; the last pose becomes the previous one, or the new one does on a first pose or a snap.
    /// </summary>
    void Repose(ref Live l, CharState state)
    {
        var asset = l.Asset;
        var rig = asset.Rig;
        if (state.Bones.Length != rig.BoneCount || state.Attach.Length != asset.AttachCount)
        {
            state.Bones = new Matrix4x4[rig.BoneCount];
            state.Previous = new Matrix4x4[rig.BoneCount];
            state.Attach = new Matrix4x4[asset.AttachCount];
            state.PreviousAttach = new Matrix4x4[asset.AttachCount];
            state.Posed = -1;
        }
        (state.Bones, state.Previous) = (state.Previous, state.Bones);
        state.Attach.CopyTo(state.PreviousAttach, 0);

        var scratch = rig.Rent();
        scratch.Pose(asset.Shape, _layers.AsSpan(l.LayerStart, l.LayerCount), state.Bones, l.Bones);
        foreach (var part in asset.Parts)
            if (part.AttachIndex >= 0) state.Attach[part.AttachIndex] = scratch.TagTransform(part.Bone, part.Offset);
        foreach (var lamp in asset.Lamps) state.Attach[lamp.AttachIndex] = lamp.Offset * scratch.BoneTransform(lamp.Bone);
        if (asset.Mouth is { } mouth) state.Attach[mouth.AttachIndex] = scratch.TagTransform(mouth.Bone, mouth.Offset);
        rig.Return(scratch);

        if (state.Posed < 0 || l.Snap)
        {
            state.Bones.CopyTo(state.Previous, 0);
            state.Attach.CopyTo(state.PreviousAttach, 0);
        }
        state.Posed = _updateStamp;
        state.PosedRagdoll = l.Bones;
    }

    /// <summary>
    /// The materials of the parts shown in range: their texture needs offered (<see cref="OfferTextureNeeds"/>), then each record written again only
    /// when something it reads changed (materialEpoch, or the material's own fields), and uploaded into the material table (CharacterMaterialTable)
    /// only when it came out different or the material has no slot.
    /// </summary>
    void WriteMaterials(Vector3 eye)
    {
        CollectSlotParts(eye);
        if (_budget.Due) OfferTextureNeeds();
        _budget.Update(_content.Textures);

        var inputs = (_content.Textures.Version, textureBias: _textureBias, textureStandIn: _textureStandIn, Gpu.Samplers.Generation);
        if (inputs != _materialInputs) (_materialInputs, _materialEpoch) = (inputs, _materialEpoch + 1);
        var table = _content.Materials;
        table.BeginFrame();
        if (_updateStamp % 120 == 0) table.Sweep(_updateStamp - CharacterMaterialTable.IdleUpdates);

        CharacterMaterialRecord record = default;
        for (int s = 0; s < _slotParts.Count; s++)
        {
            var m = _slotParts[s].Material;
            m.SlotUsed = _updateStamp;
            if (m.RecordEpoch != _materialEpoch)
            {
                Write(m, ref record);
                m.RecordEpoch = _materialEpoch;
                if (m.Slot >= 0 && SameRecord(in record, in m.Record)) continue;
                m.Record = record;
                table.Place(m);
            }
            else if (m.Slot < 0) table.Place(m);
        }
        table.Flush();
    }

    /// <summary>
    /// The parts shown on the live characters (<see cref="_slotParts"/>, each once), with their nearest distance from <paramref name="eye"/>.
    /// </summary>
    void CollectSlotParts(Vector3 eye)
    {
        _slotParts.Clear();
        for (int i = 0; i < _liveCount; i++)
        {
            ref var l = ref _live[i];
            float distance = Vector3.Distance(eye, l.Centre);
            foreach (var part in l.Asset.Parts)
            {
                if (!part.ShowsWith(l.WeaponDrawn, l.RangedDrawn, l.Shots, l.BackDrawn)) continue;
                if (part.MaterialFrame != _updateStamp)
                {
                    part.MaterialFrame = _updateStamp;
                    part.Near = float.PositiveInfinity;
                    _slotParts.Add(part);
                }
                part.Near = Math.Min(part.Near, Math.Max(distance - l.Radius * 0.5f, 0.5f));
            }
        }
    }

    /// <summary>
    /// Mip streaming: each texture's nearest user over how large a texel of its part is there; textures load at the size that need allows. Offered on
    /// the frame the budget commits them (every 500 ms), which also marks the textures used (reading the key), long before the cache's idle limits;
    /// the kept points' looks too.
    /// </summary>
    void OfferTextureNeeds()
    {
        foreach (var part in _slotParts)
            Offer(part, part.Near / part.UvScale * _budget.NeedScale);
        foreach (var (asset, distance) in _keptLooks)
        {
            foreach (var part in asset.Parts)
                Offer(part, Math.Max(Math.Max(distance, KeptViewDistance) - asset.Radius * 0.5f, 0.5f) / part.UvScale * _budget.NeedScale);
        }

        static void Offer(AssetPart part, float need)
        {
            // Negative attachment bias needs finer resident mips as well as biased sampling.
            need *= MathF.Pow(2, part.Material.MipmapBias);
            foreach (var t in part.Material.Tex)
            {
                if (t is not null)
                {
                    t.OfferNow(need);
                    _ = t.Key;
                }
            }
        }
    }

    /// <summary>
    /// Whether two material records are equal byte for byte (an unchanged record is not written again).
    /// </summary>
    static bool SameRecord(in CharacterMaterialRecord a, in CharacterMaterialRecord b) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<CharacterMaterialRecord>(in a))
            .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<CharacterMaterialRecord>(in b)));

    /// <summary>
    /// Scratch for <see cref="EvictStates"/>: the keys to forget.
    /// </summary>
    readonly List<long> _staleKeys = [];

    // The distant list's share of an update (CharacterDrawList.Distant): what it asks for, found when it or what it is judged by changes.
    /// <summary>
    /// The distant looks to build ahead.
    /// </summary>
    readonly List<CharacterAppearance> _distantBuilds = [];
    /// <summary>
    /// The built distant characters near a kept point, with their distance from it.
    /// </summary>
    readonly List<(CharacterAsset Asset, float Distance)> _distantKept = [];
    /// <summary>
    /// The draw list's distant version and the hash of the rules the last scan was made with ((-1, 0): never scanned).
    /// </summary>
    (int Version, int Inputs) _distantScanned = (-1, 0);

    /// <summary>
    /// The characters beyond reach (<see cref="CharacterDrawList.Distant"/>): scanned when the host refills them or the rules for them change (the
    /// loading focus, the arrival, the kept points), for the looks to build ahead (<see cref="WantsAsset"/>) and the built ones near a kept point
    /// (<see cref="_keptLooks"/>); every update then builds what is left of the first under the same budget as the drawn ones and adds the second.
    /// Touches no character state: such a character may also be drawn.
    /// </summary>
    void UpdateDistant(Vector3 eye, float reach, ref int built, ref double buildMs)
    {
        var distant = Draws.Distant;
        var inputs = new HashCode();
        inputs.Add(LoadingFocus);
        inputs.Add(Ahead is { } a ? Quantized(a) : default);
        foreach (var k in Kept) inputs.Add(Quantized(k));
        var key = (Draws.DistantVersion, inputs.ToHashCode());
        if (key != _distantScanned)
        {
            _distantScanned = key;
            _distantBuilds.Clear();
            _distantKept.Clear();
            foreach (ref readonly var d in CollectionsMarshal.AsSpan(distant))
            {
                if (!_assets.TryGetValue(d.Appearance, out var asset))
                {
                    if (BuildsLook(d, eye, reach)) _distantBuilds.Add(d.Appearance);
                    continue;
                }
                if (asset is null) continue;
                if (d.Build is { } build) ApplyBuild(asset, build);
                float scale = d.Scale > 0 ? d.Scale : 1;
                if (!d.Hidden && Kept.Count > 0 && NearKept(d.Position + new Vector3(0, asset.Height * 0.5f * scale, 0)) is var dk && dk <= KeptReach)
                    _distantKept.Add((asset, dk));
            }
        }
        for (int i = 0; i < _distantBuilds.Count; i++)
        {
            var look = _distantBuilds[i];
            if (_assets.ContainsKey(look))
            {
                _distantBuilds.RemoveAt(i--);
                continue;
            }

            if (LoadBudget > 0
                && (!_content.Fetched(look) || !Covered && built >= LoadBudget || buildMs >= (Covered ? CoveredBudgetMs : BuildBudgetMs)))
            {
                continue;
            }
            long buildStart = Stopwatch.GetTimestamp();
            _assets[look] = _content.Build(look);
            buildMs += Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds;
            built++;
        }
        _keptLooks.AddRange(_distantKept);

        // A kept point or the arrival moving less than this keeps the scan: KeptReach is 3000.
        static (int, int) Quantized(Vector3 p) => ((int)MathF.Floor(p.X / 250), (int)MathF.Floor(p.Z / 250));
    }

    /// <summary>
    /// Forgets the characters not seen for 60 updates.
    /// </summary>
    void EvictStates()
    {
        _staleKeys.Clear();
        foreach (var (key, state) in _states)
            if (_updateStamp - state.Seen > (state.Frozen ? 600 : 60)) _staleKeys.Add(key);   // a held pose waits longer for its look to be ready
        foreach (var key in _staleKeys) _states.Remove(key);
    }

    /// <summary>
    /// Appends an animation layer to this frame's layers, growing the array as needed.
    /// </summary>
    void AddLayer(in ActiveLayer layer)
    {
        if (_layerCount == _layers.Length) Array.Resize(ref _layers, _layerCount * 2);
        _layers[_layerCount++] = layer;
    }

    /// <summary>
    /// The bindless index of a texture for this frame's bias; the stand-in when it is null or has no key (<paramref name="present"/> false).
    /// </summary>
    uint Index(WorldTexture? t, out bool present)
    {
        uint key = t?.Key ?? 0;
        present = key != 0;
        return present ? _content.Textures.Index(key, _textureBias, _textureStandIn) : _textureStandIn;
    }

    /// <summary>
    /// A person's stat-driven body (docs/animation.md "Stat-driven body"), applied when it has changed by more than 0.01: the bone shape and ground
    /// offset with its muscle and starvation, and the body-shaded parts' normal maps (<see cref="CharacterShape.NormalBlend"/>: base, blend and
    /// weight), as <c>FUN_140531820</c> sets them. The maps change once both are resident, so a body is not drawn without one while it loads.
    /// </summary>
    void ApplyBuild(CharacterAsset asset, BodyBuild build)
    {
        if (asset.Animal) return;
        if (asset.AppliedBuild is not { } applied || !applied.Near(build))
        {
            asset.AppliedBuild = build;
            var c = asset.Appearance;
            if (_content.Rigs.Reshape(c, build) is { } reshaped)
            {
                // A glide mixes the rest values (CharacterRenderer.Blend), so the build goes there too, or the end of a glide would bring back an
                // older one.
                if (asset.Rest is { } rest) (rest.Shape, rest.GroundOffset) = reshaped;
                if (!asset.Blending) (asset.Shape, asset.GroundOffset) = reshaped;
            }
            var (baseField, blendField, weight) = CharacterShape.NormalBlend(c.Female, build.Definition, build.Starvation);
            string? File(string field) => field.EndsWith(" strong", StringComparison.Ordinal) ? c.BodyNormalStrong : field.EndsWith(" skinny",
                StringComparison.Ordinal) ? c.BodyNormalSkinny : c.BodyNormal;

            // A race without the strong or skinny map keeps its own as the base.
            asset.PendingNormals = (_content.BodyTexture(File(baseField) ?? c.BodyNormal), _content.BodyTexture(File(blendField)), weight);
        }
        if (asset.PendingNormals is not { } p || !Ready(p.Normal) || !Ready(p.Blend)) return;
        asset.PendingNormals = null;
        foreach (var part in asset.Parts)
        {
            if (part.Material.Shading == CharacterShaders.ShadeBody)
            {
                part.Material.Tex[CharacterShaders.SlotNormal] = p.Normal;
                part.Material.Tex[CharacterShaders.SlotBlendNormal] = p.Blend;
                part.Material.MuscleBlend = p.Weight;
                part.Material.RecordEpoch = 0;
            }
        }

        // Reading the key asks for the texture; a missing file never becomes resident.
        static bool Ready(WorldTexture? t) => t is null || t.Key != 0 || t.State == WorldTexture.Residency.Missing;
    }

    /// <summary>
    /// Writes a material's record: its textures' bindless indices, the flags of what is present, pending, worn, dyed, skinned, bloody or wounded, the
    /// vest mask and the colours.
    /// </summary>
    void Write(PartMaterial m, ref CharacterMaterialRecord r)
    {
        r = default;
        Span<bool> has = stackalloc bool[CharacterShaders.Slots];
        for (int i = 0; i < CharacterShaders.Slots; i++) r.Tex[i] = Index(m.Tex[i], out has[i]);
        for (int i = CharacterShaders.Slots; i < CharacterShaders.TextureSlotCount; i++) r.Tex[i] = _textureStandIn;
        uint flags = 0;
        if (has[CharacterShaders.SlotDiffuse]) flags |= CharacterShaders.HasDiffuse;
        if (has[CharacterShaders.SlotNormal]) flags |= CharacterShaders.HasNormal;
        if (m.Swizzled || m.Tex[CharacterShaders.SlotNormal] is { Swizzled: true })
            flags |= CharacterShaders.NormalSwizzled;
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
        if (m.Bloody && has[CharacterShaders.SlotBlood])
            flags |= CharacterShaders.Bloody | (m.BloodFull ? CharacterShaders.BloodFull : 0);
        if (m.Wound) flags |= CharacterShaders.Wound | (m.Mechanical ? CharacterShaders.Mechanical : 0);

        // Textures assigned but not resident yet: a hair's diffuse (its cut-out), a clipped item's normal map, the head's maps.
        if (m.Shading == CharacterShaders.ShadeHair && m.Tex[CharacterShaders.SlotDiffuse] is not null && !has[CharacterShaders.SlotDiffuse]
            || m.ClipOnNormalAlpha && m.Tex[CharacterShaders.SlotNormal] is not null && !has[CharacterShaders.SlotNormal])
            flags |= CharacterShaders.AlphaPending;
        if (m.Tex[CharacterShaders.SlotHeadDiffuse] is not null && !has[CharacterShaders.SlotHeadDiffuse])
            flags |= CharacterShaders.HeadPending;
        if (m.Tex[CharacterShaders.SlotHeadNormal] is not null && !has[CharacterShaders.SlotHeadNormal])
            flags |= CharacterShaders.HeadNormalPending;
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
        r.HairColour = new Vector4(m.HairColour, m.MipmapBias);
        r.HairAlpha = m.HairAlpha;
        r.HairMult = m.HairMult;
        r.BeardAlpha = m.BeardAlpha;
        r.DiffuseChannel = m.DiffuseChannel;
        r.AlphaChannel = m.AlphaChannel;
        r.Colour1 = m.Colour1;
        r.Colour2 = m.Colour2;
    }

    /// <summary>
    /// Waits for everything wanted to be built and loaded (stills): every appearance of the list, every texture.
    /// </summary>
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
                _content.Textures.Pump(wait: true);
                if (_content.Textures.PendingCount == 0) break;
                Thread.Sleep(1);
            }
            Update(eye);   // the indices of what has just landed
        }
        finally
        {
            LoadBudget = old;
        }
    }

    /// <summary>
    /// A line about the content for the statistics: appearances, meshes and their memory, the texture cache and the governor's need factor.
    /// </summary>
    public string Describe() =>
        $"{Assets} appearances, {_content.MeshCount} meshes ({_content.MeshBytes / 1048576.0:0.0} MB), {_content.Textures.Describe()}, " +
        $"texture need x{_budget.NeedScale:0.0} (mark {_content.Textures.MarkMb:0} MB)";

    /// <summary>
    /// Prints the statistics when anything was drawn, then frees the programs, the frame sets, the content, the skin table, the portraits, the stage,
    /// the hover masks and the icons.
    /// </summary>
    public void Dispose()
    {
        if (_updateCount > 0) Console.WriteLine($"characters {Statistics()}");
        _colourProg.Dispose();
        _depthProg.Dispose();
        _motionProg.Dispose();
        _nativeFrame.Dispose();
        _content.Dispose();
        _skinScattering.Dispose();
        DisposePortraits();
        DisposeStage();
        DisposeHover();
        DisposeIcons();
    }
}
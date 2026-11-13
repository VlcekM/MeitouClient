using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The build window's pictures of buildings the game would draw (docs/game/ui-screens.md "Building icons"), made here when its saved picture is not
/// at hand: the building's parts (as the placement ghost lays them out) turned and framed as <see cref="BuildingIconStyle"/> describes, drawn through
/// the objects' program into a texture of their own. The parts stand at the eye of the frame they were asked in (so the streaming loads their meshes
/// and fine textures), are drawn each frame until every texture is resident, then dropped; the picture is kept.
/// </summary>
public sealed unsafe partial class WorldObjectRenderer
{
    /// <summary>
    /// Pictures drawn at once; the others wait their turn.
    /// </summary>
    const int BuildingIconsPerFrame = 2;

    /// <summary>
    /// Frames a picture is drawn waiting for its meshes and textures before it is shown with what it has.
    /// </summary>
    const int BuildingIconGiveUpFrames = 300;

    /// <summary>
    /// One building picture: its parts, their loose instances while it is made, its camera and its textures.
    /// </summary>
    sealed class BuildingIconEntry
    {
        /// <summary>
        /// The BUILDING record's string id the picture is of.
        /// </summary>
        public required string Record;

        /// <summary>
        /// The parts as asked for, laid out at the origin and unrotated.
        /// </summary>
        public required IReadOnlyList<PlacedMesh> Parts;

        /// <summary>
        /// The parts drawn, laid out at the origin, and their instances (in the same order).
        /// </summary>
        public PlacedMesh[] Base = [];

        /// <summary>
        /// The loose instances of <see cref="Base"/> while the picture is made (null until laid out; emptied once drawn).
        /// </summary>
        public List<ObjectStreamer.Instance>? Instances;

        /// <summary>
        /// The camera and the node's turns are set (<see cref="FrameIcon"/>).
        /// </summary>
        public bool Framed;

        /// <summary>
        /// The node's turn, and the picture's camera (<see cref="BuildingIconStyle.Matrices"/>).
        /// </summary>
        public Matrix4x4 Model, View, Projection;

        /// <summary>
        /// Where the parts stand: the eye when they were laid out, so the streaming loads their meshes and fine textures.
        /// </summary>
        public Vector3 Anchor;

        /// <summary>
        /// The picture (RGBA8, <see cref="BuildingIconStyle.Size"/> square) and its depth target (freed once the picture is final).
        /// </summary>
        public GpuTexture? Colour, Depth;

        /// <summary>
        /// The picture with its linear, edge-clamped sampler, for the UI.
        /// </summary>
        public SampledTexture Sampled;

        /// <summary>
        /// Nothing to draw (no parts, no bounds): the window shows none.
        /// </summary>
        public bool Failed;

        /// <summary>
        /// The image holds a picture (published to the UI thread).
        /// </summary>
        public volatile bool Drawn;

        /// <summary>
        /// Frames drawn so far (past <see cref="BuildingIconGiveUpFrames"/> the picture is shown with what it has).
        /// </summary>
        public int Frames;
    }

    /// <summary>
    /// Every picture asked for, by record id.
    /// </summary>
    readonly ConcurrentDictionary<string, BuildingIconEntry> _buildingIcons = new(StringComparer.Ordinal);

    /// <summary>
    /// Pictures asked for (from any thread), taken in by <see cref="DrawBuildingIcons"/>.
    /// </summary>
    readonly ConcurrentQueue<BuildingIconEntry> _buildingIconRequests = new();

    /// <summary>
    /// The pictures being made: drawn each frame until final.
    /// </summary>
    readonly List<BuildingIconEntry> _buildingIconsUnsettled = [];

    /// <summary>
    /// The drawn picture of the BUILDING <paramref name="record"/> (upside down, as the renderer's targets are: show it flipped) once it exists;
    /// asking starts it being made from
    /// <paramref name="parts"/> (laid out at the origin, unrotated). Any thread.
    /// </summary>
    public bool TryGetBuildingIcon(string record, IReadOnlyList<PlacedMesh> parts, out (GpuTexture Texture, SampledTexture Sampled) icon)
    {
        if (!_buildingIcons.TryGetValue(record, out var entry))
        {
            var made = new BuildingIconEntry { Record = record, Parts = parts };
            if (_buildingIcons.TryAdd(record, made)) _buildingIconRequests.Enqueue(made);
            entry = _buildingIcons[record];
        }
        icon = entry.Drawn ? (entry.Colour!, entry.Sampled) : default;
        return entry.Drawn;
    }

    /// <summary>
    /// Building pictures asked for and not yet final.
    /// </summary>
    public int PendingBuildingIcons => _buildingIconRequests.Count + _buildingIconsUnsettled.Count;

    /// <summary>
    /// Draws the pictures being made, each into its own texture (after <see cref="Update"/>, outside any pass, before the frame's
    /// <see cref="Draw"/>).
    /// </summary>
    public void DrawBuildingIcons()
    {
        while (_buildingIconRequests.TryDequeue(out var requested)) _buildingIconsUnsettled.Add(requested);
        int drawn = 0;
        for (int i = 0; i < _buildingIconsUnsettled.Count && drawn < BuildingIconsPerFrame; i++)
        {
            var entry = _buildingIconsUnsettled[i];
            if (entry.Instances is null) LayOutIcon(entry);
            bool meshes = entry.Instances!.TrueForAll(inst => inst.Gpu is not null || inst.Mesh.Status == ObjectMesh.State.Failed);
            bool textures = meshes && entry.Instances.TrueForAll(inst => inst.Materials is not { } set || set.Parts.All(p => p.TexturesSettled));
            bool late = ++entry.Frames > BuildingIconGiveUpFrames;
            if (!meshes && !late) continue;
            if (!entry.Framed) FrameIcon(entry);
            if (entry.Failed)
            {
                foreach (var inst in entry.Instances) _streamer.DropLoose(inst);
                entry.Instances.Clear();
                _buildingIconsUnsettled.RemoveAt(i--);
                continue;
            }
            DrawBuildingIcon(entry);
            drawn++;
            entry.Drawn = textures || late;
            if (entry.Drawn)
            {
                foreach (var inst in entry.Instances) _streamer.DropLoose(inst);
                entry.Instances.Clear();
                entry.Depth?.Dispose();
                entry.Depth = null;
                _buildingIconsUnsettled.RemoveAt(i--);
            }
        }
    }

    /// <summary>
    /// The parts as loose instances at the eye, not yet turned (the ghost's parts: interior ones left out), so the streaming loads their meshes.
    /// </summary>
    void LayOutIcon(BuildingIconEntry entry)
    {
        entry.Base = [.. entry.Parts.Where(p => !p.Interior)];
        entry.Anchor = _eyeNow;
        var at = Matrix4x4.CreateTranslation(entry.Anchor);
        entry.Instances = [.. entry.Base.Select(p => _streamer.MakeLoose(p with { Transform = p.Transform * at }))];
    }

    /// <summary>
    /// The camera from the loaded meshes' bounds (many files have no bounds chunk), and the parts turned as the node is.
    /// </summary>
    void FrameIcon(BuildingIconEntry entry)
    {
        entry.Framed = true;
        var boxes = new List<(Vector3, Vector3, Matrix4x4)>(entry.Base.Length);
        for (int i = 0; i < entry.Base.Length; i++)
            if (entry.Instances![i].Gpu is { } gpu && gpu.Max.X >= gpu.Min.X) boxes.Add((gpu.Min, gpu.Max, entry.Base[i].Transform));
        if (BuildingIconStyle.Matrices(boxes) is not { } camera)
        {
            entry.Failed = true;
            return;
        }
        (entry.Model, entry.View, entry.Projection) = camera;
        var place = entry.Model * Matrix4x4.CreateTranslation(entry.Anchor);
        for (int i = 0; i < entry.Base.Length; i++)
        {
            var inst = entry.Instances![i];
            var transform = entry.Base[i].Transform * place;
            inst.Placed = entry.Base[i] with { Transform = transform };
            inst.Transform = transform;
            if (inst.Gpu is { } gpu) inst.Centre = Vector3.Transform(gpu.Centre, transform);
        }
    }

    /// <summary>
    /// Draws one picture: its textures made on first use, its instances into batches of their own at their finest held level, recorded with the
    /// objects' colour program at the picture's camera, without fog or weather.
    /// </summary>
    void DrawBuildingIcon(BuildingIconEntry entry)
    {
        const int size = BuildingIconStyle.Size;
        if (entry.Colour is null)
        {
            entry.Colour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, size, size,
                Use: TextureUse.Sampled | TextureUse.ColourTarget | TextureUse.TransferSrc, Name: $"building icon {entry.Record}"));
            entry.Depth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, size, size, Use: TextureUse.DepthTarget,
                Name: $"building icon depth {entry.Record}"));
            entry.Sampled = new SampledTexture(Gpu.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Linear, TextureMagFilter.Linear)),
                entry.Colour.View());
        }

        // The batches of this picture alone (the frame's Draw starts its own from nothing).
        foreach (var b in _batchMap.Values) b.Count = 0;
        _active.Clear();
        long now = Environment.TickCount64;
        foreach (var inst in entry.Instances!)
        {
            if (inst.Gpu is not { } gpu || inst.Materials is null) continue;
            inst.Mesh.LastUsed = now;
            Add(gpu, inst.Materials, gpu.MinLevel, inst.Transform, 0, 2, 0);
        }
        UploadInstances();
        var cmd = Gpu.BeginNative("building icon");
        var targets = PassTargets.Of(entry.Colour, entry.Depth);
        cmd.BeginRendering(new RenderingDesc(targets.Colour, targets.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, targets.Width,
            targets.Height));
        Gpu.BeginHostPass(cmd, targets, DrawState.Scene(targets.Formats));
        var rect = new PixelRect(0, 0, (uint)size, (uint)size);
        cmd.Clear(true, default, true, 1, rect);
        Gpu.SetPassViewport(new PixelViewport(0, 0, size, size, 0, 1), rect);
        if (_active.Count > 0)
        {
            // The camera relative to where the parts stand; no fog and the objects' plain lighting (a positive fog distance would select the sky's).
            var view = Matrix4x4.CreateTranslation(-entry.Anchor) * entry.View;
            Matrix4x4.Invert(view, out var toWorld);
            var eye = toWorld.Translation;
            var constants = new ViewConstants
            {
                ViewProjection = Matrix4x4.CreateTranslation(eye) * view * entry.Projection,
                Eye = eye,
                LightDir = Vector3.UnitZ,
                FogDistance = 0,
            };
            PrepareDraws(IconOptions, wire: false);

            // The picture shows no weather (the game's has no dust), as an impostor bake.
            for (int d = 0; d < _drawCount; d++) _draws[d].Push.Spare = MeshSurface.NoWeather;
            RecordDraws(_colourProg, wire: false, in constants, default);
        }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
        foreach (var b in _active) b.Count = 0;
        _active.Clear();
    }

    /// <summary>
    /// The options the pictures are drawn with (the defaults).
    /// </summary>
    static readonly WorldRenderOptions IconOptions = new();

    /// <summary>
    /// Frees every picture's textures.
    /// </summary>
    void DisposeBuildingIcons()
    {
        foreach (var entry in _buildingIcons.Values)
        {
            entry.Colour?.Dispose();
            entry.Depth?.Dispose();
        }
        _buildingIcons.Clear();
    }
}
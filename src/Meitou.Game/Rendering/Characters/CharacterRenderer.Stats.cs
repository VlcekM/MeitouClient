using Meitou.Data.Characters;
using System.Globalization;
using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The characters' statistics: the drawn characters' screen boxes, the CPU and GPU cost samples and the statistics line.
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>
    /// The last <see cref="Capacity"/> samples of a cost in milliseconds and how many there were in all (a ring: a session records one per frame).
    /// </summary>
    sealed class SampleRing
    {
        /// <summary>
        /// How many samples the ring keeps.
        /// </summary>
        public const int Capacity = 64;
        /// <summary>
        /// The samples, written round the ring.
        /// </summary>
        readonly double[] _values = new double[Capacity];

        /// <summary>
        /// How many samples were added in all.
        /// </summary>
        public int Count { get; private set; }

        /// <summary>
        /// Adds a sample, overwriting the oldest once the ring is full.
        /// </summary>
        public void Add(double value) => _values[Count++ % Capacity] = value;

        /// <summary>
        /// The mean of the last <paramref name="count"/> samples (fewer when there are fewer; 0 when none).
        /// </summary>
        public double Tail(int count)
        {
            int n = Math.Min(Math.Min(count, Count), Capacity);
            if (n == 0) return 0;
            double sum = 0;
            for (int i = n; i >= 1; i--) sum += _values[(Count - i) % Capacity];
            return sum / n;
        }
    }

    /// <summary>
    /// The last colour view's frustum, which <see cref="DrawnCharacters"/> counts by (<see cref="CountedBoxes"/>).
    /// </summary>
    Vector4[]? _countedFrustum;
    /// <summary>
    /// The last colour view's fog cull (see <see cref="_countedFrustum"/>).
    /// </summary>
    FogVolumes? _countedFog;

    /// <summary>
    /// The screen rectangles (x0, y0, x1, y1 in pixels of a <paramref name="width"/> × <paramref name="height"/> picture seen through
    /// <paramref name="viewProjection"/>) of the characters the last colour view counted in <see cref="DrawnCharacters"/>: in its frustum and not
    /// hidden by fog (the F11 boxes). Those partly behind the eye are left out.
    /// </summary>
    public void CountedBoxes(Matrix4x4 viewProjection, int width, int height, List<Vector4> into)
    {
        into.Clear();
        if (_countedFrustum is not { } frustum) return;
        for (int i = 0; i < _liveCount; i++)
        {
            ref var l = ref _live[i];
            if (l.PortraitOnly || !FrustumTests.SphereVisible(frustum, l.Centre, l.Radius)) continue;
            if (_countedFog?.Hidden(l.Centre, l.Radius, FogVolumes.CullKind.Characters) == true) continue;
            if (ScreenBox(l.Centre, l.Radius, viewProjection, width, height) is { } box) into.Add(box);
        }
    }

    /// <summary>
    /// The pixel rectangle of the box round a sphere; null when a corner is behind the eye.
    /// </summary>
    static Vector4? ScreenBox(Vector3 centre, float radius, Matrix4x4 viewProjection, int width, int height)
    {
        var box = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var p = centre + new Vector3((corner & 1) == 0 ? -radius : radius, (corner & 2) == 0 ? -radius : radius,
                (corner & 4) == 0 ? -radius : radius);
            var clip = Vector4.Transform(new Vector4(p, 1), viewProjection);
            if (clip.W <= 0.01f) return null;
            float x = (clip.X / clip.W * 0.5f + 0.5f) * width, y = (0.5f - clip.Y / clip.W * 0.5f) * height;
            box = new Vector4(MathF.Min(box.X, x), MathF.Min(box.Y, y), MathF.Max(box.Z, x), MathF.Max(box.W, y));
        }
        return box;
    }

    /// <summary>
    /// The cost samples: the colour and shadow passes' GPU times, the update's, posing's, the draw recording's and the gathering's CPU times, in
    /// milliseconds.
    /// </summary>
    readonly SampleRing _colourGpu = new(), _depthGpu = new(), _updateCpu = new(), _poseCpu = new(), _drawCpu = new(), _gatherCpu = new();
    /// <summary>
    /// Scratch: the GPU times a timer's completed frames returned.
    /// </summary>
    readonly List<double> _polled = [];
    /// <summary>
    /// How many updates ran (the statistics are printed at <see cref="Dispose"/> when any did).
    /// </summary>
    int _updateCount;

    /// <summary>
    /// Takes in the GPU times of the passes whose frames have completed.
    /// </summary>
    void PollTimers()
    {
        _polled.Clear();
        _colourTimer.Poll(_polled);
        foreach (var ms in _polled) _colourGpu.Add(ms);
        _polled.Clear();
        _depthTimer.Poll(_polled);
        foreach (var ms in _polled) _depthGpu.Add(ms);
    }

    /// <summary>
    /// The last frames' costs (the stills' timed frames): the render thread's milliseconds and the GPU's per pass.
    /// </summary>
    public string Statistics()
    {
        PollTimers();
        string levels = LevelSummary();
        return string.Create(CultureInfo.InvariantCulture,
            $"{Posed} posed, {DrawnCharacters} drawn ({DrawnParts} parts) in {DrawCalls} calls, {DrawnTriangles:N0} triangles in the last view; "
            + $"by level {levels}; last frames: cpu update {_updateCpu.Tail(10):0.00} ms (gather {_gatherCpu.Tail(10):0.00}, "
            + $"pose {_poseCpu.Tail(10):0.00}), draw {_drawCpu.Tail(10):0.00} ms; gpu colour {_colourGpu.Tail(10):0.00} ms, "
            + $"shadow cascades {_depthGpu.Tail(40):0.00} ms per call ({_depthGpu.Count} calls), {_motionCalls} motion passes; {Describe()}");
    }

    /// <summary>
    /// The parts and triangles per level of the last view, for the statistics line.
    /// </summary>
    string LevelSummary() =>
        string.Join(", ", Enumerable.Range(0, 5).Where(l => LevelParts[l] > 0).Select(l => $"L{l} {LevelParts[l]} parts {LevelTriangles[l] / 1000}k"));

    /// <summary>
    /// Debug (control command <c>draw-state</c>): what the last update did with the character: not listed, culled tiny, out of reach or not built, or
    /// live with its culling tests, palette and shown parts.
    /// </summary>
    public string DrawReport(long key)
    {
        if (_tinyNow.Contains(key)) return "tiny-culled";

        int found = -1;
        for (int i = 0; i < _liveCount; i++)
        {
            if (_live[i].Key == key)
            {
                found = i;
                break;
            }
        }

        if (found < 0)
            return _states.ContainsKey(key) ? "not live (far, hidden or asset not built)" : "never seen by the renderer";

        ref var l = ref _live[found];
        int nan = 0;
        int shown = 0;
        int partCount = l.Asset.Parts.Count;
        var bones = l.State.Bones;
        for (int b = 0; b < bones.Length; b++)
        {
            var m = bones[b];
            float sum = m.M11 + m.M12 + m.M13 + m.M21 + m.M22 + m.M23 + m.M31 + m.M32 + m.M33 + m.M41 + m.M42 + m.M43;
            if (!float.IsFinite(sum) || MathF.Abs(m.M41) > 1e5f || MathF.Abs(m.M42) > 1e5f || MathF.Abs(m.M43) > 1e5f) nan++;
        }

        for (int p = 0; p < partCount; p++)
        {
            if (_partFrames[l.PartBase + p].Shows) shown++;
        }

        bool frustum = _countedFrustum is { } f && FrustumTests.SphereVisible(f, l.Centre, l.Radius);
        bool fog = _countedFog?.Hidden(l.Centre, l.Radius, FogVolumes.CullKind.Characters) == true;
        return $"live portraitOnly={l.PortraitOnly} offScreen={l.OffScreen} frozen={l.Frozen} repose={l.Repose} interior={l.Interior}"
            + $" centre=({l.Centre.X:F1},{l.Centre.Y:F1},{l.Centre.Z:F1}) radius={l.Radius:F1} inFrustum={frustum} fogHidden={fog}"
            + $" badBones={nan}/{bones.Length} partsShown={shown}/{partCount} ragdoll={l.Bones is not null}"
            + $" world.finite={float.IsFinite(l.World.M41 + l.World.M42 + l.World.M43)}";
    }

    /// <summary>
    /// Debug (<c>draw-state</c>): whether the look's asset is built, failed to build (cached as null: the character is never drawn, silently) or not
    /// built yet.
    /// </summary>
    public string AssetState(CharacterAppearance look) =>
        !_assets.TryGetValue(look, out var a) ? "unbuilt"
        : a is null ? "FAILED"
        : "built";
}

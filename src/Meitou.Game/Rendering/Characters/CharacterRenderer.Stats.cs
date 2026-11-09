using System.Globalization;
using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>
    /// The last <see cref="Capacity"/> samples of a cost in milliseconds and how many there were in all (a ring: a session records one per frame).
    /// </summary>
    sealed class SampleRing
    {
        public const int Capacity = 64;
        readonly double[] _values = new double[Capacity];

        public int Count { get; private set; }

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

    // The last colour view's culling, which DrawnCharacters counts by (CountedBoxes).
    Vector4[]? _countedFrustum;
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

    readonly SampleRing _colourGpu = new(), _depthGpu = new(), _updateCpu = new(), _poseCpu = new(), _drawCpu = new(), _gatherCpu = new();
    readonly List<double> _polled = [];
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
}
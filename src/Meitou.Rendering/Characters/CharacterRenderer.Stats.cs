using System.Globalization;

namespace Meitou.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>The last <see cref="Capacity"/> samples of a cost in milliseconds and how many there were in all (a ring: a session records one per frame).</summary>
    sealed class SampleRing
    {
        public const int Capacity = 64;
        readonly double[] values = new double[Capacity];

        public int Count { get; private set; }

        public void Add(double value) => values[Count++ % Capacity] = value;

        /// <summary>The mean of the last <paramref name="count"/> samples (fewer when there are fewer; 0 when none).</summary>
        public double Tail(int count)
        {
            int n = Math.Min(Math.Min(count, Count), Capacity);
            if (n == 0) return 0;
            double sum = 0;
            for (int i = n; i >= 1; i--) sum += values[(Count - i) % Capacity];
            return sum / n;
        }
    }

    readonly SampleRing colourGpu = new(), depthGpu = new(), updateCpu = new(), poseCpu = new(), drawCpu = new(), gatherCpu = new();
    readonly List<double> polled = [];
    int updateCount;

    /// <summary>Takes in the GPU times of the passes whose frames have completed.</summary>
    void PollTimers()
    {
        polled.Clear();
        colourTimer.Poll(polled);
        foreach (var ms in polled) colourGpu.Add(ms);
        polled.Clear();
        depthTimer.Poll(polled);
        foreach (var ms in polled) depthGpu.Add(ms);
    }

    /// <summary>The last frames' costs (the stills' timed frames): the render thread's milliseconds and the GPU's per pass.</summary>
    public string Statistics()
    {
        PollTimers();
        return string.Create(CultureInfo.InvariantCulture,
            $"{Posed} posed, {DrawnCharacters} drawn ({DrawnParts} parts) in {DrawCalls} calls, {DrawnTriangles:N0} triangles in the last view; by level {string.Join(", ", Enumerable.Range(0, 5).Where(l => LevelParts[l] > 0).Select(l => $"L{l} {LevelParts[l]} parts {LevelTriangles[l] / 1000}k"))}; last frames: cpu update {updateCpu.Tail(10):0.00} ms (gather {gatherCpu.Tail(10):0.00}, pose {poseCpu.Tail(10):0.00}), " +
            $"draw {drawCpu.Tail(10):0.00} ms; gpu colour {colourGpu.Tail(10):0.00} ms, shadow cascades {depthGpu.Tail(40):0.00} ms per call ({depthGpu.Count} calls), {motionCalls} motion passes; {Describe()}");
    }
}

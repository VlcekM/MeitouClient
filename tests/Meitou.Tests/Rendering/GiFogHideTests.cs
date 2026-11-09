using Meitou.Game.Rendering.Gi;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The probes the fog hides (docs/render-gi.md "Probes in the fog"): the reach box of a probe column, the layers of it that are hidden, and the
/// frame's probe list.
/// </summary>
public class GiFogHideTests
{
    const float S = 512, Sy = 384;

    [Fact]
    public void The_reach_box_holds_every_point_that_can_read_the_probes_of_the_column()
    {
        // giCascade reads a point q = p + offset (offset at most 0.3 of the smaller spacing) from the probes of the cells around q: a probe at column
        // x is read from p within one spacing across. Layer l is read from base + (l - 0.5) sy to base + (l + 1.5) sy.
        const float x = 10240, z = -20480, baseY = 100;
        for (int layers = 1; layers <= 7; layers++)
        {
            var (min, max) = GiFogHide.ReachBox(x, z, baseY, S, Sy, layers, -1000, 0);
            float offset = 0.3f * MathF.Min(S, Sy);

            Assert.True(min.X <= x - S - offset && max.X >= x + S + offset);
            Assert.True(min.Z <= z - S - offset && max.Z >= z + S + offset);
            Assert.True(max.Y >= baseY + (layers - 1 + 1.5f) * Sy + offset, $"top for {layers} layers");
            Assert.True(min.Y <= -1000);   // the lowest layer is read from below, as far as the block goes
        }

        // Wider with the slack.
        var (a, _) = GiFogHide.ReachBox(x, z, baseY, S, Sy, 1, -1000, 0);
        var (b, _) = GiFogHide.ReachBox(x, z, baseY, S, Sy, 1, -1000, 64);

        Assert.Equal(a.X - 64, b.X);
    }

    [Fact]
    public void A_hidden_probe_is_listed_once_in_each_period_of_its_rounds_and_the_others_every_time()
    {
        const int phases = 4, phase = 1, period = 16;
        const int offX0 = 5, offZ0 = 9, offX1 = 30, offZ1 = 2;
        var hidden = new byte[GiProbes.Cascades * GiProbes.Columns * GiProbes.Columns];

        // Cascade 1's columns in the window's far half (wx >= 16) hide 5 layers; cascade 0 hides nothing.
        for (int wz = 0; wz < GiProbes.Columns; wz++)
            for (int wx = 16; wx < GiProbes.Columns; wx++)
                hidden[(1 * GiProbes.Columns + (offZ1 + wz) % GiProbes.Columns) * GiProbes.Columns + (offX1 + wx) % GiProbes.Columns] = 5;
        var into = new uint[GiProbes.Probes];
        var seen = new Dictionary<uint, int>();
        int plain = 0;
        for (uint round = 0; round < period; round++)
        {
            int n = GiFogHide.List(into, phases, phase, round, period, hidden, offX0, offZ0, offX1, offZ1);
            var set = new HashSet<uint>();
            for (int i = 0; i < n; i++)
            {
                uint probe = into[i] & ~GiFogHide.SlowBit;

                Assert.True(set.Add(probe));   // never twice in one list

                int layer = (int)(probe % GiProbes.Layers), rest = (int)(probe / GiProbes.Layers), wx = rest % GiProbes.Columns,
                        c = rest / (GiProbes.Columns * GiProbes.Columns);
                bool isHidden = c == 1 && wx >= 16 && layer < 5;

                Assert.Equal(isHidden, (into[i] & GiFogHide.SlowBit) != 0);

                if (isHidden) seen[probe] = seen.GetValueOrDefault(probe) + 1; else plain++;
            }
        }

        Assert.Equal(GiProbes.Probes / phases - seen.Count, plain / period);   // every visible probe in every round
        Assert.NotEmpty(seen);
        Assert.All(seen.Values, count => Assert.Equal(1, count));   // each hidden probe exactly once in the 16 rounds
    }
}

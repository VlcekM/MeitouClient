using System.Text;
using Meitou.Data.Textures;

namespace Meitou.Tests.Textures;

/// <summary>
/// The game's texture quality rules (docs/formats/settings.md, "Mip skipping").
/// </summary>
public class TextureQualityTests
{
    // level, name, group, expected drop
    [Theory]
    [InlineData(0, "a.dds", "General", 0)]        // Maximum touches nothing
    [InlineData(1, "a.dds", "General", 0)]        // High: ordinary textures keep full size
    [InlineData(1, "a_HI.dds", "General", 1)]     // ... only _HI. and Landscape lose one level
    [InlineData(1, "a.dds", "Landscape", 1)]
    [InlineData(2, "a.dds", "Landscape", 2)]      // Medium
    [InlineData(2, "a_LO.dds", "General", 0)]     // _LO. drops one fewer, then the no-_HI. rule takes one more (never below 0)
    [InlineData(3, "a_LO.dds", "General", 1)]
    [InlineData(4, "a.dds", "General", 3)]
    [InlineData(4, "a_LO_HI.dds", "General", 4)]  // "_LO_" is not "_LO.", and the name has _HI.
    [InlineData(4, "a.dds", "Overlaymaps", 0)]    // excluded groups and names without dds
    [InlineData(4, "a.png", "General", 0)]
    public void LevelsToDropFollowsTheListener(int level, string name, string group, int expected) =>
        Assert.Equal(expected, TextureQuality.LevelsToDrop(name, group, level));

    /// <summary>
    /// A DXT file of <paramref name="width"/>×<paramref name="height"/> with <paramref name="mips"/> levels; every block byte is its level's number
    /// plus 1.
    /// </summary>
    static DdsFile Dxt(int width, int height, int mips, string fourCc = "DXT1", uint caps2 = 0)
    {
        int blockBytes = fourCc == "DXT1" ? 8 : 16;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("DDS "));
        w.Write(124);
        w.Write(0x1u | 0x2 | 0x4 | 0x1000 | DdsReader.FlagMipMapCount);
        w.Write(height);
        w.Write(width);
        w.Write(0);
        w.Write(0);
        w.Write(mips);
        for (int i = 0; i < 11; i++) w.Write(0);
        w.Write(32);
        w.Write(0x4u);
        w.Write(Encoding.ASCII.GetBytes(fourCc));
        for (int i = 0; i < 5; i++) w.Write(0);
        w.Write(0x1000u);
        w.Write(caps2);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        for (int level = 0; level < mips; level++)
        {
            int blocks = Math.Max((width >> level) / 4, 1) * Math.Max((height >> level) / 4, 1);
            w.Write(Enumerable.Repeat((byte)(level + 1), blocks * blockBytes).ToArray());
        }
        return DdsReader.Read(ms.ToArray());
    }

    /// <summary>
    /// A DXT file's bytes (as <see cref="Dxt"/> makes them) on disk.
    /// </summary>
    static string DxtFile(int width, int height, int mips, string fourCc = "DXT1")
    {
        var dds = Dxt(width, height, mips, fourCc);
        var path = Path.Combine(Path.GetTempPath(), "meitou-dds-" + Guid.NewGuid().ToString("N") + ".dds");
        File.WriteAllBytes(path, dds.Data);
        return path;
    }

    [Fact]
    public void ReadKeptReadsOnlyTheMipsThatAreKept()
    {
        var path = DxtFile(64, 64, 7, "DXT5");
        try
        {
            var expected = TextureQuality.DropTopMips(Dxt(64, 64, 7, "DXT5"), 2);
            using var file = File.OpenHandle(path);
            var kept = DdsReader.ReadKept(file, w => TextureQuality.DropTopMips(w, 2), out var whole);
            Assert.Equal((64, 64, 7), (whole.Width, whole.Height, whole.MipCount));   // the headers as the whole file lays them out
            Assert.Equal((expected.Width, expected.Height, expected.MipCount, expected.Format),
                    (kept.Width, kept.Height, kept.MipCount, kept.Format));
            Assert.Equal(expected.Surfaces.Count, kept.Surfaces.Count);
            Assert.True(kept.Data.Length < new FileInfo(path).Length / 8, "the top two levels (15/16 of the file) are not read");
            Assert.Equal(0, kept.Surfaces[0].Offset);   // rebased
            for (int i = 0; i < expected.Surfaces.Count; i++)
            {
                var (a, b) = (expected.Surfaces[i], kept.Surfaces[i]);
                Assert.Equal((a.Level, a.Width, a.Height, a.Length), (b.Level, b.Width, b.Height, b.Length));
                Assert.True(expected.SurfaceData(a).SequenceEqual(kept.SurfaceData(b)), $"level {i} differs");
            }
            Assert.Equal(0, kept.TrailingBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

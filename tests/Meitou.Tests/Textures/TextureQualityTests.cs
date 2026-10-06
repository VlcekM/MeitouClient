using System.Text;
using Meitou.Data.Textures;

namespace Meitou.Tests.Textures;

/// <summary>The game's texture quality rules (docs/formats/settings.md, "Mip skipping").</summary>
public class TextureQualityTests
{
    // level, name, group, expected drop
    [Theory]
    [InlineData(0, "a.dds", "General", 0)]        // Maximum touches nothing
    [InlineData(0, "a_HI.dds", "Landscape", 0)]
    [InlineData(1, "a.dds", "General", 0)]        // High: ordinary textures keep full size
    [InlineData(1, "a_HI.dds", "General", 1)]     // ... only _HI. and Landscape lose one level
    [InlineData(1, "a.dds", "Landscape", 1)]
    [InlineData(1, "a_LO.dds", "Landscape", 0)]
    [InlineData(2, "a.dds", "General", 1)]        // Medium
    [InlineData(2, "a_HI.dds", "General", 2)]
    [InlineData(2, "a.dds", "Landscape", 2)]
    [InlineData(2, "a_LO.dds", "General", 0)]     // _LO. drops one fewer, then the no-_HI. rule takes one more (never below 0)
    [InlineData(3, "a_LO.dds", "General", 1)]
    [InlineData(4, "a.dds", "General", 3)]
    [InlineData(4, "a_HI.dds", "foliage", 4)]
    [InlineData(4, "a_LO_HI.dds", "General", 4)]  // "_LO_" is not "_LO.", and the name has _HI.
    [InlineData(4, "a_LO.dds", "Landscape", 3)]
    [InlineData(4, "a.dds", "Overlaymaps", 0)]    // excluded groups and names without dds
    [InlineData(4, "a.dds", "GUI", 0)]
    [InlineData(4, "a.dds", "PLSM2", 0)]
    [InlineData(4, "a.dds", "", 0)]
    [InlineData(4, "a.png", "General", 0)]
    public void LevelsToDropFollowsTheListener(int level, string name, string group, int expected) =>
        Assert.Equal(expected, TextureQuality.LevelsToDrop(name, group, level));

    [Theory]
    [InlineData(0, 2048)]
    [InlineData(1, 1536)]
    [InlineData(2, 1024)]
    [InlineData(4, 1024)]
    public void MemoryBudget(int level, int mb) => Assert.Equal(mb, TextureQuality.MemoryBudgetMb(level));

    /// <summary>A DXT file of <paramref name="width"/>×<paramref name="height"/> with <paramref name="mips"/> levels; every block byte is its level's number plus 1.</summary>
    static DdsFile Dxt(int width, int height, int mips, string fourCC = "DXT1", uint caps2 = 0)
    {
        int blockBytes = fourCC == "DXT1" ? 8 : 16;
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
        w.Write(Encoding.ASCII.GetBytes(fourCC));
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

    [Fact]
    public void DropTopMipsReslicesTheSurfaces()
    {
        var dds = Dxt(16, 16, 5);
        var dropped = TextureQuality.DropTopMips(dds, 1);
        Assert.Equal((8, 8, 4), (dropped.Width, dropped.Height, dropped.MipCount));
        Assert.Equal(4, dropped.Surfaces.Count);
        Assert.Equal(Enumerable.Range(0, 4), dropped.Surfaces.Select(s => s.Level));
        Assert.Equal(dds.Surfaces[1].Offset, dropped.Surfaces[0].Offset);
        Assert.Equal((8, 8), (dropped.Surfaces[0].Width, dropped.Surfaces[0].Height));
        Assert.Equal(2, dropped.Data[dropped.Surfaces[0].Offset]);   // the second level's bytes
        Assert.Same(dds.Data, dropped.Data);                            // no copy
    }

    [Fact]
    public void DropTopMipsStopsWhenAMipOrSizeIsLacking()
    {
        // 16x16 with 3 mips: two drops leave one level; a third would start with fewer than 2 mips.
        var three = TextureQuality.DropTopMips(Dxt(16, 16, 3), 5);
        Assert.Equal((4, 4, 1), (three.Width, three.Height, three.MipCount));
        // 8x8 with a full chain: 8 -> 4 is allowed (side 8 >= 5), 4 -> 2 is not (side 4 < 5).
        var small = TextureQuality.DropTopMips(Dxt(8, 8, 4), 5);
        Assert.Equal((4, 4, 3), (small.Width, small.Height, small.MipCount));
    }

    [Fact]
    public void DropTopMipsLeavesOtherFilesAlone()
    {
        var dds = Dxt(16, 16, 5);
        Assert.Same(dds, TextureQuality.DropTopMips(dds, 0));
        var dxt5 = TextureQuality.DropTopMips(Dxt(16, 16, 5, "DXT5"), 2);
        Assert.Equal((4, 4, 3, DdsFormat.Bc3), (dxt5.Width, dxt5.Height, dxt5.MipCount, dxt5.Format));
    }
}

using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The F11 statistics panel's line wrapping (<see cref="DebugOverlay.Wrap"/>): long lines stay inside the window.</summary>
public class OverlayWrapTests
{
    [Fact]
    public void Short_lines_are_left_alone()
    {
        string[] lines = ["fps 60", "  foliage   12 MB"];
        Assert.Same(lines, DebugOverlay.Wrap(lines, 40));
    }

    [Fact]
    public void Long_lines_break_at_spaces_and_continuations_are_indented()
    {
        var wrapped = DebugOverlay.Wrap(["  foliage   122 MB in 310 meshes (156 unloaded, 33 reloaded), 202 textures 828 MB"], 30);
        Assert.All(wrapped, l => Assert.True(l.Length <= 30, l));
        Assert.Equal("  foliage   122 MB in 310", wrapped[0]);
        Assert.All(wrapped.Skip(1), l => Assert.StartsWith("      ", l));
        Assert.Equal("foliage 122 MB in 310 meshes (156 unloaded, 33 reloaded), 202 textures 828 MB",
            string.Join(' ', wrapped.Select(l => l.Trim())).Replace("   ", " "));
    }

    [Fact]
    public void A_word_longer_than_the_width_is_cut()
    {
        var wrapped = DebugOverlay.Wrap([new string('x', 50)], 20);
        Assert.Equal(["xxxxxxxxxxxxxxxxxxxx", "    xxxxxxxxxxxxxxxx", "    xxxxxxxxxxxxxx"], wrapped);
    }
}

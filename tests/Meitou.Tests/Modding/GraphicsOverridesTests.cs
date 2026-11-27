using Meitou.Game.Rendering.Post;

namespace Meitou.Tests.Modding;

/// <summary>
/// The code mods' overrides of the look settings (docs/modding.md "Graphics overrides"): laid over the options for the time of a draw, with the
/// player's values back after it.
/// </summary>
public class GraphicsOverridesTests
{
    const string Vignette = "Meitou: Vignette", Strength = "Vignette strength";

    [Fact]
    public void Overrides_are_in_the_options_only_between_apply_and_restore_and_the_players_values_stay()
    {
        var options = new PostOptions { Vignette = false, VignetteStrength = 0.35f };
        var overrides = new GraphicsOverrides();
        Assert.Contains(Vignette, GraphicsOverrides.Keys);
        Assert.Contains(Strength, GraphicsOverrides.Keys);
        Assert.Contains("Meitou: Letterbox", GraphicsOverrides.Keys);

        // Before the game's options are known nothing is read; an override is kept for when they are.
        Assert.Null(overrides.Player(Strength));
        Assert.True(overrides.Set("first", Vignette, 1));
        Assert.True(overrides.Set("first", Strength, 5));   // past the slider's range: clamped when laid on
        Assert.False(overrides.Set("first", "No such setting", 1));
        Assert.False(overrides.Set("first", Strength, float.NaN));

        overrides.Bind(options);
        Assert.Equal(0.35f, overrides.Player(Strength));

        overrides.Apply();
        Assert.True(options.Vignette);
        Assert.Equal(1f, options.VignetteStrength);
        Assert.Equal(0.35f, overrides.Player(Strength));   // the player's value, also while the override is in the options
        Assert.Equal(0f, overrides.Player(Vignette));
        overrides.Restore();
        Assert.False(options.Vignette);
        Assert.Equal(0.35f, options.VignetteStrength);

        // A change the player makes between frames is the value put back from then on.
        options.VignetteStrength = 0.6f;
        overrides.Apply();
        overrides.Restore();
        Assert.Equal(0.6f, options.VignetteStrength);
    }

    [Fact]
    public void The_override_set_last_wins_and_a_released_or_dropped_one_is_gone()
    {
        var options = new PostOptions { VignetteStrength = 0.35f };
        var overrides = new GraphicsOverrides();
        overrides.Bind(options);
        overrides.Set("first", Strength, 0.2f);
        overrides.Set("second", Strength, 0.8f);

        overrides.Apply();
        Assert.Equal(0.8f, options.VignetteStrength);
        overrides.Restore();

        // The first mod sets its override again: the value changes, its place in the order does not.
        overrides.Set("first", Strength, 0.1f);
        overrides.Apply();
        Assert.Equal(0.8f, options.VignetteStrength);
        overrides.Restore();

        Assert.True(overrides.Release("second", Strength));
        Assert.False(overrides.Release("second", Strength));
        overrides.Apply();
        Assert.Equal(0.1f, options.VignetteStrength);
        overrides.Restore();

        overrides.Drop("first");
        overrides.Apply();
        Assert.Equal(0.35f, options.VignetteStrength);
        overrides.Restore();
        Assert.Equal(0.35f, options.VignetteStrength);
    }
}

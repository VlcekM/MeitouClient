using Meitou.Modding;

namespace Meitou.Compat.CharactersEmotions;

/// <summary>
/// The settings of the plugin's <c>CharactersEmotions_settings.ini</c> that apply to a port, on the Settings screen's Mod settings page instead of the plugin's F6 menu.
/// The defaults are the plugin's. Times are in milliseconds of game time (the simulation clock: they stand still while paused and follow the game speed).
/// </summary>
public sealed class CharactersEmotionsSettings
{
    [Setting("Only selected characters", Hint = "Express only the selected characters instead of every character of the player's squads.")]
    public bool OnlySelectedCharacter { get; set; }

    [Setting("Other characters", Hint = "Also give faces to characters that are not the player's (nearby townspeople, enemies) when their mesh supports it.")]
    public bool IncludeOthers { get; set; }

    [Setting("Number of characters", Hint = "How many characters are expressive at once (the plugin's NumberOfCharactersEmotions).", Min = 1, Max = 100)]
    public int NumberOfCharacters { get; set; } = 30;

    [Setting("Animation expressions", Hint = "Expressions driven by the character's animation (mining, sneaking...), from CharactersEmotions_animemotions.ini.")]
    public bool AllowAnimEmotions { get; set; } = true;

    [Setting("Status check interval (ms)", Hint = "How often a character's situation (combat, hurt, hungry, bed...) is looked at.", Min = 20, Max = 1000, Step = 10)]
    public int CheckFrequency { get; set; } = 100;

    [Setting("Status check randomizer (ms)", Hint = "Random extra time added to each status check, so characters do not change in step.", Min = 0, Max = 500, Step = 10)]
    public int CheckRandomize { get; set; } = 20;

    [Setting("Injury boundary", Hint = "The pain expression shows while the worst body part is below this fraction of its health (1.0 = always when scratched).", Min = 0, Max = 1, Step = 0.05)]
    public float HurtBoundary { get; set; } = 0.7f;

    [Setting("Blinking interval (ms)", Min = 200, Max = 10000, Step = 100)]
    public int BlinkingFrequency { get; set; } = 2000;

    [Setting("Blinking randomizer (ms)", Min = 0, Max = 10000, Step = 100)]
    public int BlinkingRandomizer { get; set; } = 1500;

    [Setting("Blinking time (ms)", Hint = "One blink, closing and opening.", Min = 50, Max = 1000, Step = 10)]
    public int BlinkingTime { get; set; } = 200;

    [Setting("Normal expression interval (ms)", Min = 200, Max = 20000, Step = 100)]
    public int NormalFrequency { get; set; } = 3500;

    [Setting("Normal expression randomizer (ms)", Min = 0, Max = 20000, Step = 100)]
    public int NormalRandomizer { get; set; } = 3000;

    [Setting("Normal expression keep time (ms)", Min = 200, Max = 20000, Step = 100)]
    public int NormalKeepTime { get; set; } = 5000;

    [Setting("Normal expression morph time (ms)", Hint = "How long a normal expression takes to form and to fade.", Min = 50, Max = 5000, Step = 50)]
    public int NormalMorphTime { get; set; } = 800;

    [Setting("Attacked keep time (ms)", Min = 0, Max = 5000, Step = 50)]
    public int AttackedKeepTime { get; set; } = 500;

    [Setting("Attacked fade time (ms)", Min = 50, Max = 5000, Step = 50)]
    public int AttackedOffTime { get; set; } = 1000;
}

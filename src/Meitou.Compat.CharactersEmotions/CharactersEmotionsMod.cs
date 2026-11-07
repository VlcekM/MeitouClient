using Meitou.Modding;

namespace Meitou.Compat.CharactersEmotions;

/// <summary>
/// Entry of the native port of the Workshop plugin mod "Characters Emotions - Realtime expression" (docs/compat-characters-emotions.md). The game loads it from its
/// <c>compat</c> folder when CharactersEmotions.mod is enabled; the plugin's own DLL is never loaded.
/// Behaviour and configuration formats follow the plugin's readmes and default INI text, MIT licensed, Copyright (c) 2026 호옹이호옹.
/// </summary>
public sealed class CharactersEmotionsMod : IMod
{
    public void Initialize(ModContext c)
    {
        var settings = new CharactersEmotionsSettings();
        c.AddSettings(settings);
        // The INI files are the plugin's own, next to its DLL; MEITOU_EMOTIONS_INI points at another folder (tests, tuning).
        string? plugins = Environment.GetEnvironmentVariable("MEITOU_EMOTIONS_INI") is { Length: > 0 } over ? over
            : c.PortedFolder is { } folder ? Path.Combine(folder, "Plugins") : null;
        var config = EmotionConfig.Load(plugins, c.Log);
        var driver = new FaceDriver(config, settings, c.Log);
        c.AddFaceController(driver);
        c.AddCombatHitFeed(driver.NoteHits);
        c.Log($"{config.Required.Length} shape keys required, {config.ByAnimation.Count} animation expressions");
    }
}

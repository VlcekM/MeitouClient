using Meitou.Game.Rendering.Post;

namespace Meitou.Modding;

/// <summary>
/// A mod's hold on the picture's look settings (docs/modding.md "Graphics overrides"), from <see cref="ModContext.Graphics"/>: it reads the
/// player's values and lays its own over them for as long as it likes (a kill-cam's vignette, a spyglass's depth of field) without changing what
/// the player set: the settings menu, the saved file and the look presets keep the player's values, and <see cref="Release"/> shows them again.
/// Call it on the host's thread (a frame callback, a key action).
/// </summary>
public sealed class ModGraphics
{
    /// <summary>
    /// Every mod's overrides.
    /// </summary>
    readonly GraphicsOverrides _store;

    /// <summary>
    /// The mod this handle sets for.
    /// </summary>
    readonly string _modId;

    /// <summary>
    /// A mod's handle on the overrides.
    /// </summary>
    internal ModGraphics(GraphicsOverrides store, string modId) => (_store, _modId) = (store, modId);

    /// <summary>
    /// The settings a mod may override, by the keys <c>meitou.user.json</c>'s <c>graphics</c> and the look presets use: a slider's label
    /// (<c>Vignette strength</c>), <c>Meitou: </c> and a switch's label (<c>Meitou: Vignette</c>).
    /// </summary>
    public IReadOnlyList<string> Settings => GraphicsOverrides.Keys;

    /// <summary>
    /// The player's value of a setting (a switch: 1 on, 0 off), whatever a mod lays over it; null for a setting that is not one of
    /// <see cref="Settings"/>, or before the game has its options.
    /// </summary>
    public float? Get(string setting) => _store.Player(setting);

    /// <summary>
    /// Draws the world with <paramref name="value"/> for the setting (clamped to its range; a switch is on from 0.5) until
    /// <see cref="Release"/>, over the player's value and over an earlier mod's override. False, with nothing changed, for a setting that is
    /// not one of <see cref="Settings"/> or a value that is not finite.
    /// </summary>
    public bool Override(string setting, float value) => _store.Set(_modId, setting, value);

    /// <summary>
    /// Ends this mod's override of the setting; false when it held none.
    /// </summary>
    public bool Release(string setting) => _store.Release(_modId, setting);

    /// <summary>
    /// Ends every override this mod holds.
    /// </summary>
    public void ReleaseAll() => _store.Drop(_modId);
}

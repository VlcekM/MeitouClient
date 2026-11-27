using Meitou.Game.Rendering.Sky;

namespace Meitou.Modding;

/// <summary>
/// A mod's hook in the game's lighting shaders (docs/modding.md "Lighting hooks"), declared by <c>lighting</c> in its <c>meitou.json</c> and
/// handed out by <see cref="ModContext.Lighting"/>: its switch and its parameters, which change what is drawn from the next frame with no
/// recompile. Set them from any thread.
/// </summary>
public sealed class LightingHook
{
    /// <summary>
    /// The installed hook this handle sets.
    /// </summary>
    readonly LightingHooks.Hook _hook;

    /// <summary>
    /// A handle on an installed hook.
    /// </summary>
    internal LightingHook(LightingHooks.Hook hook) => _hook = hook;

    /// <summary>
    /// The parameters' names, in the manifest's order.
    /// </summary>
    public IReadOnlyList<string> Params => [.. _hook.Params.Select(p => p.Name)];

    /// <summary>
    /// Whether the hook's functions run. On from the mod's <see cref="IMod.Initialize"/>; off, the lighting is the game's own.
    /// </summary>
    public bool Enabled
    {
        get => LightingHooks.Read(_hook.First) > 0.5f;
        set => LightingHooks.Write(_hook.First, value ? 1 : 0);
    }

    /// <summary>
    /// Sets the parameter the shader reads as <c>PARAM_name</c>; false, with nothing set, for a name the manifest does not declare or a value
    /// that is not finite.
    /// </summary>
    public bool Set(string param, float value)
    {
        ArgumentNullException.ThrowIfNull(param);
        int index = IndexOf(param);
        if (index < 0 || !float.IsFinite(value)) return false;

        LightingHooks.Write(_hook.First + 1 + index, value);
        return true;
    }

    /// <summary>
    /// A parameter's value now; null for a name the manifest does not declare.
    /// </summary>
    public float? Get(string param)
    {
        ArgumentNullException.ThrowIfNull(param);
        int index = IndexOf(param);

        return index < 0 ? null : LightingHooks.Read(_hook.First + 1 + index);
    }

    /// <summary>
    /// A parameter's place in the manifest's order, -1 when it declares none of that name.
    /// </summary>
    int IndexOf(string param)
    {
        for (int i = 0; i < _hook.Params.Count; i++)
        {
            if (_hook.Params[i].Name == param) return i;
        }

        return -1;
    }
}

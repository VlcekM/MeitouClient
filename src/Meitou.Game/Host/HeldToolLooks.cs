using Meitou.Game.Rendering.Characters;
using Meitou.Simulation;

namespace Meitou.Game.Host;

/// <summary>
/// The looks of operators with the tool they hold (<see cref="CharacterSnapshot.HeldTool"/>), made once per look and tool and drawn once prepared.
/// </summary>
sealed class HeldToolLooks(GameHost host)
{
    /// <summary>
    /// Each look's looks with a tool held, by the tool's record id.
    /// </summary>
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Data.Characters.CharacterAppearance,
        Dictionary<string, Data.Characters.CharacterAppearance>> _heldLooks = new();

    /// <summary>
    /// The look with the tool an operator holds (<see cref="CharacterSnapshot.HeldTool"/>), made once per look and tool. Until the renderer has
    /// prepared it the plain look is drawn and the tool's look is <paramref name="pending"/>, to be listed hidden so it gets prepared.
    /// </summary>
    internal Data.Characters.CharacterAppearance HeldLook(Data.Characters.CharacterAppearance look, string? tool,
        out Data.Characters.CharacterAppearance? pending)
    {
        pending = null;
        if (tool is null || host.Scene.Database is not { } db) return look;
        var byTool = _heldLooks.GetOrCreateValue(look);
        if (!byTool.TryGetValue(tool, out var held)) byTool[tool] = held = db.Find(tool) is { } item ? look.WithHeld(item) : look;
        if (host.Gpu.Characters is not { } characters || characters.IsPrepared(held)) return held;
        pending = held;
        return look;
    }

    /// <summary>
    /// Whether the look with the operator's tool (<see cref="HeldLook"/>) is made and prepared: then it is the one drawn.
    /// </summary>
    internal bool HeldPrepared(Data.Characters.CharacterAppearance look, string? tool, CharacterRenderer characters) =>
        tool is not null && _heldLooks.TryGetValue(look, out var byTool) && byTool.TryGetValue(tool, out var held) && characters.IsPrepared(held);
}

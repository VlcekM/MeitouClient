using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Simulation;

/// <summary>One animation playing on a character: the skeleton animation's name, its time in seconds and its blend weight.</summary>
public readonly record struct AnimationLayer(string Name, float Time, float Weight);

/// <summary>A character as the renderer needs it after one tick; <c>Appearance</c> is null for one that is simulated but has nothing to draw (an animal, a failed build): the host shows a marker (docs/simulation.md, "Threading", step 8).</summary>
public sealed record CharacterSnapshot(CharacterId Id, CharacterAppearance? Appearance, Vector3 Position, float Yaw, IReadOnlyList<AnimationLayer> Animations)
{
    /// <summary>The owner faction (a position in the world's faction list; -1 none), for debug markers.</summary>
    public int Faction { get; init; } = -1;
    public string Name { get; init; } = "";
    public int SquadId { get; init; } = -1;
    /// <summary>One of the player's characters, and whether it is selected.</summary>
    public bool IsPlayer { get; init; }
    public bool Selected { get; init; }
}

/// <summary>
/// What a tick publishes for drawing. Immutable once published: the host keeps the last two and interpolates between them by the
/// frame's alpha, as it does for the camera; a character only in the newer one appears without interpolation.
/// </summary>
public sealed record WorldSnapshot(long Tick, IReadOnlyList<CharacterSnapshot> Characters)
{
    public static readonly WorldSnapshot Empty = new(-1, []);
}

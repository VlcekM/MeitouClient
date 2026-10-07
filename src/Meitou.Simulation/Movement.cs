using System.Numerics;

namespace Meitou.Simulation;

/// <summary>What a character is doing (<see cref="CharacterHot.Task"/>). The first tasks of the movement stage; the AI packages replace them later.</summary>
public enum CharacterTask : byte
{
    Idle = 0,
    /// <summary>Walks to random places within the squad's home radius, waiting between them.</summary>
    Wander = 1,
    /// <summary>Walks to <see cref="CharacterHot.Goal"/> (a move order) and then idles.</summary>
    GoTo = 2,
    /// <summary>Keeps its formation place beside the squad leader.</summary>
    Follow = 3,
}

/// <summary>Bits of <see cref="CharacterHot.Flags"/> the movement system uses.</summary>
[Flags]
public enum MoveFlags : ushort
{
    None = 0,
    /// <summary>A path to <see cref="CharacterHot.Goal"/> is wanted; the next Schedule step asks the path service.</summary>
    NeedPath = 1,
    /// <summary>A path request is in flight.</summary>
    Pending = 2,
    /// <summary>The character is following <see cref="CharacterCold.Path"/>.</summary>
    HasPath = 4,
}

/// <summary>Speed modes (docs/game/pathfinding.md "Speed"): 0 caps at the race walk speed, 1 at 55, 2 and 3 do not cap.</summary>
public static class SpeedMode
{
    public const byte Walk = 0, Run = 1, Free = 2;
    public const float RunCap = 55;

    public static float Cap(byte mode, float walkSpeed) => mode switch { Walk => walkSpeed, Run => RunCap, _ => 999 };
}

/// <summary>The animation layers a character plays for its movement state (<see cref="CharacterHot.Animation"/>): the names are the skeleton animations of docs/animation.md.</summary>
public static class AnimationLayers
{
    public const string Idle = "idle_stand_relax";
    public const string WalkLower = "walk lower", WalkUpper = "walk upper";

    public static IReadOnlyList<AnimationLayer> For(ushort animation, float time) => animation == 0
        ? [new AnimationLayer(Idle, time, 1)]
        : [new AnimationLayer(WalkLower, time, 1), new AnimationLayer(WalkUpper, time, 1)];
}

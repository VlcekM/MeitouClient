using Meitou.Data.Gameplay;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;

namespace Meitou.Simulation;

/// <summary>The systems of the game's wiring, for <see cref="StandardSystemOptions.Parts"/>. Listed in the order they run.</summary>
[Flags]
public enum StandardParts
{
    None = 0,
    Population = 1,
    Player = 2,
    Pursuit = 4,
    Movement = 8,
    Body = 16,
    Combat = 32,
    Animation = 64,
    Feed = 128,
    Retaliation = 256,
    /// <summary>What the game runs.</summary>
    All = Population | Player | Pursuit | Movement | Body | Combat | Animation | Feed | Retaliation,
}

/// <summary>What <see cref="StandardSystems.Build"/> builds and with which settings; the defaults are the game's.</summary>
public sealed class StandardSystemOptions
{
    /// <summary>The systems to build; a test that needs a few of them asks for those and gets them in the game's relative order.</summary>
    public StandardParts Parts { get; init; } = StandardParts.All;
    public PopulationSettings? Population { get; init; }
    /// <summary>True answers path requests inside the tick (tests); false on the path service's own thread (the interactive game).</summary>
    public bool SynchronousPaths { get; init; }
    /// <summary>Multiplies the time of the body-part and blood rates (<see cref="BodySystem"/>).</summary>
    public float BodyTimeScale { get; init; } = 1;
    /// <summary>The game's: the path service walks attackers to their targets and the body system ticks the medical state.</summary>
    public CombatOptions Combat { get; init; } = new() { SelfApproach = false, TickMedical = false };
    /// <summary>Null: the clip lengths of an empty set (every attack takes its default time).</summary>
    public AnimationLengths? AnimationLengths { get; init; }
    /// <summary>Null: built from the database of the data.</summary>
    public AnimationLibrary? AnimationLibrary { get; init; }
    /// <summary>Null: the animation system's default; the game passes the CONSTANTS value.</summary>
    public float? AnimationBlendRate { get; init; }
    public FeedSettings? Feed { get; init; }
}

/// <summary>The built systems in tick order, with the ones the host talks to.</summary>
public sealed class StandardSystemSet
{
    public required IReadOnlyList<ITickSystem> Systems { get; init; }
    public PopulationSystem? Population { get; init; }
    public MovementSystem? Movement { get; init; }
    /// <summary>Built when combat, pursuit or retaliation is asked for.</summary>
    public CombatSystem? Combat { get; init; }
}

/// <summary>
/// The system list the game runs, in the order it needs, built in one place so the game and the tests that mean "the game's wiring" cannot
/// drift apart. Systems of the same phase run in list order (<c>Inputs</c>, <c>Schedule</c>, <c>Apply</c> and <c>SlowWorld</c> serially, the
/// parallel phases one system after the other, and an effect's order of commit by the system's index), and the order is part of the
/// simulation's result (the golden hashes pin it). The rules:
/// <list type="number">
/// <item><b>Population first</b>: it takes the focus command in <c>Inputs</c> and loads, spawns and unloads in <c>SlowWorld</c>, so every system after it sees the characters it made.
/// <b>Player before movement</b>: the player system applies <c>SelectCommand</c>/<c>StopCommand</c> in <c>Inputs</c> and the movement system's <c>MoveOrder</c> in the same step reads the selection that results.</item>
/// <item><b>Pursuit before movement</b>: pursuit raises <c>NeedPath</c> in its <c>Schedule</c> and the movement system answers the same tick.</item>
/// <item><b>Body before combat</b>: the body system ticks the medical state (<c>TickMedical</c> is off in the combat system) and the combat
/// system reads its result in the same tick; with it on, combat does that itself.</item>
/// <item><b>Combat before animation and retaliation</b>: the combat system swaps its read and write arrays at the end of its <c>SlowWorld</c>,
/// so a system placed after it sees the new tick's combat state and one placed before it the old one. Retaliation reads the combat state of the tick that just ended
/// (animation is placed after combat in the game's order, and the golden hashes pin it). Moving the swap to a separate end-of-tick hook would change what retaliation reads.</item>
/// <item><b>Feed after body</b>: the game's order; eating reads hunger the body system advanced in the same tick.</item>
/// <item><b>Retaliation last</b>: it queues <c>AttackOrder</c>s for the next tick from <c>SlowWorld</c>, after combat has swapped its arrays.</item>
/// </list>
/// An extra system a test wants (a probe, a poke) goes after these.
/// </summary>
public static class StandardSystems
{
    public static StandardSystemSet Build(PopulationData data, IWalkability walkability, StandardSystemOptions? options = null)
    {
        var o = options ?? new StandardSystemOptions();
        bool Has(StandardParts p) => (o.Parts & p) != 0;
        var systems = new List<ITickSystem>();
        PopulationSystem? population = null;
        MovementSystem? movement = null;
        CombatSystem? combat = null;
        if (Has(StandardParts.Population)) systems.Add(population = new PopulationSystem(data, o.Population));
        if (Has(StandardParts.Player)) systems.Add(new PlayerSystem());
        if (Has(StandardParts.Pursuit | StandardParts.Combat | StandardParts.Retaliation))
            combat = new CombatSystem(data.Combat.Techniques, data.Combat.Constants, data.Bodies.Constants, data.BodyOptions, o.AnimationLengths ?? new AnimationLengths(), o.Combat);
        if (Has(StandardParts.Pursuit)) systems.Add(new PursuitSystem(combat!));
        if (Has(StandardParts.Movement)) systems.Add(movement = new MovementSystem(new PathService(walkability, o.SynchronousPaths)));
        if (Has(StandardParts.Body)) systems.Add(new BodySystem(data.Bodies.Constants, data.BodyOptions, o.BodyTimeScale));
        if (Has(StandardParts.Combat)) systems.Add(combat!);
        if (Has(StandardParts.Animation))
        {
            var library = o.AnimationLibrary ?? AnimationLibrary.FromDatabase(data.Db);
            var lengths = o.AnimationLengths ?? new AnimationLengths();
            systems.Add(o.AnimationBlendRate is { } rate ? new AnimationSystem(library, lengths, rate) : new AnimationSystem(library, lengths));
        }
        if (Has(StandardParts.Feed)) systems.Add(new FeedSystem(data.Items, o.Feed));
        if (Has(StandardParts.Retaliation)) systems.Add(new RetaliationSystem(combat!));
        return new StandardSystemSet { Systems = systems, Population = population, Movement = movement, Combat = combat };
    }
}

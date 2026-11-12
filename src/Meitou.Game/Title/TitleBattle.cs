using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Frame;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Game.Title;

/// <summary>
/// A presentation-only road skirmish. Its world has no population streaming, needs, crimes or save connection.
/// </summary>
sealed class TitleBattle : IDisposable
{
    /// <summary>
    /// How much larger than life the fighters are drawn, and their spacing round each centre.
    /// </summary>
    const float DisplayScale = 2.5f;

    /// <summary>
    /// The fighters' draw-distance multiple: the shortest NPC View Range times this is the longest, so the staged fight shows (and the title's
    /// loading, which waits for its looks, ends) whatever the range.
    /// </summary>
    const float DrawReach = CharacterRenderer.MaxDrawDistance / CharacterRenderer.MinDrawDistance;

    /// <summary>
    /// The fight's own world (combat and animation systems only).
    /// </summary>
    readonly World _world;

    /// <summary>
    /// The fight centres on the road, one per pair.
    /// </summary>
    readonly IReadOnlyList<Vector3> _centres;

    /// <summary>
    /// Seconds left over after the last whole tick, for the drawing's interpolation.
    /// </summary>
    double _remainder;

    /// <summary>
    /// The fight: its world with damage off and medical ticks off, a paladin and a bandit at each centre, run 90 ticks in.
    /// </summary>
    TitleBattle(PopulationData data, Func<float, float, float> ground, IReadOnlyList<Vector3> centres,
        AnimationLengths lengths, AnimationLibrary library, string paladin, string bandit, int holyNation, int bandits)
    {
        this._centres = centres;
        var combat = new CombatSystem(data.Combat.Techniques, data.Combat.Constants, data.Bodies.Constants,
            data.BodyOptions with { GlobalDamageMultiplier = 0 }, lengths, new CombatOptions { TickMedical = false });
        var animation = new AnimationSystem(library, lengths, data.Bodies.Constants.AnimationBlendRate, combat);
        _world = new(new WorldSettings { Seed = 173 }, new OpenGroundWalkability(ground), [combat, animation]);
        using var population = new PopulationSystem(data, new PopulationSettings { Background = false });
        try
        {
            SpawnPairs(population, paladin, bandit, holyNation, bandits);

            // Open on a fight already in progress, rather than six people drawing their weapons together.
            for (int i = 0; i < 90; i++) _world.RunTick();
        }
        catch
        {
            _world.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A fight of Paladins against Dust Bandits at <paramref name="centres"/>; null (said on stderr) when a character or faction record is missing.
    /// </summary>
    public static TitleBattle? Create(PopulationData data, Func<float, float, float> ground, IReadOnlyList<Vector3> centres,
        AnimationLengths lengths, AnimationLibrary library)
    {
        string? Record(string name) => data.Db.OfType(FcsRecordType.Character).FirstOrDefault(r => r.BaseName == name)?.StringId;
        int Faction(string name) => data.Factions
            .Select((f, i) => (f, i))
            .FirstOrDefault(p => p.f.Name == name, (null!, -1))
            .i;

        if (Record("Paladin") is not { } paladin || Record("Dust Bandit") is not { } bandit
            || Faction("The Holy Nation") is not (>= 0 and var holyNation) || Faction("Dust Bandits") is not (>= 0 and var bandits)
            || data.PlayerFaction < 0)
        {
            Console.Error.WriteLine("title     road fight unavailable: missing Paladin, Dust Bandit or faction records");
            return null;
        }

        return new(data, ground, centres, lengths, library, paladin, bandit, holyNation, bandits);
    }

    /// <summary>
    /// Real time, independent of the paused gameplay clock; bounded catch-up after a stalled frame.
    /// </summary>
    public void Advance(double seconds)
    {
        _remainder += Math.Clamp(seconds, 0, 0.25);
        while (_remainder >= _world.TickSeconds)
        {
            _world.RunTick();
            _remainder -= _world.TickSeconds;
        }
    }

    /// <summary>
    /// Adds the fighters to <paramref name="list"/>: interpolated between the last two ticks, each pair kept on its centre and drawn at the display
    /// scale.
    /// </summary>
    public void Fill(CharacterDrawList list)
    {
        var current = _world.Snapshot.Characters;
        var previous = _world.PreviousSnapshot.Characters;
        float alpha = (float)(_remainder / _world.TickSeconds);
        for (int i = 0; i < current.Count; i++)
        {
            var c = current[i];
            if (c.Appearance is null) continue;

            var before = previous.Count > i ? previous[i] : c;
            var position = Vector3.Lerp(before.Position, c.Position, alpha);

            // Keep each pair centred on the road; enlarge its spacing with its bodies so their swings still line up.
            int pair = i / 2 * 2;
            var middle = (current[pair].Position + current[pair + 1].Position) / 2;
            var oldMiddle = (previous[pair].Position + previous[pair + 1].Position) / 2;
            position = _centres[i / 2] + (position - Vector3.Lerp(oldMiddle, middle, alpha)) * DisplayScale;
            position.Y = _world.Walkability.GroundHeight(position.X, position.Z);
            var poses = new CharacterPose[GameHost.DrawnLayerCount(before, c)];
            GameHost.BlendLayers(before, c, alpha, poses);
            list.Add(new CharacterInstance(long.MinValue + 2 + i, c.Appearance, position,
                Simulation.Session.Interp.LerpAngle(before.Yaw, c.Yaw, alpha), poses)
            {
                WeaponDrawn = c.WeaponDrawn,
                RangedDrawn = c.RangedDrawn,
                Shots = c.CrossbowShots,
                Scale = c.Scale * DisplayScale,
                DrawDistanceScale = DrawReach,
                FullRateAnimation = true,
            });
        }
    }

    /// <summary>
    /// Disposes the fight's world.
    /// </summary>
    public void Dispose() => _world.Dispose();

    /// <summary>
    /// Spawns a paladin and a bandit facing each other at every centre, and queues their attacks on each other.
    /// </summary>
    void SpawnPairs(PopulationSystem population, string paladin, string bandit, int holyNation, int bandits)
    {
        for (int i = 0; i < _centres.Count; i++)
        {
            var at = new Vector2(_centres[i].X, _centres[i].Z);
            var a = population.SpawnSandbox(_world, paladin, at - new Vector2(0, 16), false, 0, i + 1, holyNation);
            var b = population.SpawnSandbox(_world, bandit, at + new Vector2(0, 16), false, MathF.PI, i + 1, bandits);
            _world.Commands.Enqueue(new AttackOrder([a], b) { Tick = i * 7 });
            _world.Commands.Enqueue(new AttackOrder([b], a) { Tick = i * 7 });
        }
    }
}

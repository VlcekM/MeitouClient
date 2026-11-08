using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Rendering;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Game;

/// <summary>A presentation-only road skirmish. Its world has no population streaming, needs, crimes or save connection.</summary>
sealed class TitleBattle : IDisposable
{
    const float DisplayScale = 2.5f;
    readonly World world;
    readonly IReadOnlyList<Vector3> centres;
    double remainder;

    TitleBattle(PopulationData data, Func<float, float, float> ground, IReadOnlyList<Vector3> centres,
        AnimationLengths lengths, AnimationLibrary library, string paladin, string bandit, int holyNation, int bandits)
    {
        this.centres = centres;
        var combat = new CombatSystem(data.Combat.Techniques, data.Combat.Constants, data.Bodies.Constants,
            data.BodyOptions with { GlobalDamageMultiplier = 0 }, lengths, new CombatOptions { TickMedical = false });
        var animation = new AnimationSystem(library, lengths, data.Bodies.Constants.AnimationBlendRate, combat);
        world = new(new WorldSettings { Seed = 173 }, new OpenGroundWalkability(ground), [combat, animation]);
        using var population = new PopulationSystem(data, new PopulationSettings { Background = false });
        try
        {
            for (int i = 0; i < centres.Count; i++)
            {
                var at = new Vector2(centres[i].X, centres[i].Z);
                var a = population.SpawnSandbox(world, paladin, at - new Vector2(0, 16), false, 0, i + 1, holyNation);
                var b = population.SpawnSandbox(world, bandit, at + new Vector2(0, 16), false, MathF.PI, i + 1, bandits);
                world.Commands.Enqueue(new AttackOrder([a], b) { Tick = i * 7 });
                world.Commands.Enqueue(new AttackOrder([b], a) { Tick = i * 7 });
            }
            // Open on a fight already in progress, rather than six people drawing their weapons together.
            for (int i = 0; i < 90; i++) world.RunTick();
        }
        catch { world.Dispose(); throw; }
    }

    public static TitleBattle? Create(PopulationData data, Func<float, float, float> ground, IReadOnlyList<Vector3> centres,
        AnimationLengths lengths, AnimationLibrary library)
    {
        string? Record(string name) => data.Db.OfType(FcsRecordType.CHARACTER).FirstOrDefault(r => r.BaseName == name)?.StringId;
        int Faction(string name) => data.Factions.Select((f, i) => (f, i)).FirstOrDefault(p => p.f.Name == name, (null!, -1)).i;
        if (Record("Paladin") is not { } paladin || Record("Dust Bandit") is not { } bandit ||
            Faction("The Holy Nation") is not (>= 0 and var holyNation) || Faction("Dust Bandits") is not (>= 0 and var bandits) || data.PlayerFaction < 0)
        {
            Console.Error.WriteLine("title     road fight unavailable: missing Paladin, Dust Bandit or faction records");
            return null;
        }
        return new(data, ground, centres, lengths, library, paladin, bandit, holyNation, bandits);
    }

    /// <summary>Real time, independent of the paused gameplay clock; bounded catch-up after a stalled frame.</summary>
    public void Advance(double seconds)
    {
        remainder += Math.Clamp(seconds, 0, 0.25);
        while (remainder >= world.TickSeconds)
        {
            world.RunTick();
            remainder -= world.TickSeconds;
        }
    }

    public void Fill(CharacterDrawList list)
    {
        var current = world.Snapshot.Characters;
        var previous = world.PreviousSnapshot.Characters;
        float alpha = (float)(remainder / world.TickSeconds);
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
            position = centres[i / 2] + (position - Vector3.Lerp(oldMiddle, middle, alpha)) * DisplayScale;
            position.Y = world.Walkability.GroundHeight(position.X, position.Z);
            var poses = c.Animations.Select(layer =>
            {
                var old = before.Animations.FirstOrDefault(p => p.Name == layer.Name);
                return new CharacterPose(layer.Name, old.Name is null ? layer.Time : GameHost.LayerTime(old, layer, alpha),
                    old.Weight + (layer.Weight - old.Weight) * alpha);
            }).ToArray();
            list.Add(new CharacterInstance(long.MinValue + 2 + i, c.Appearance, position,
                Meitou.Engine.Time.Interp.LerpAngle(before.Yaw, c.Yaw, alpha), poses)
                { WeaponDrawn = c.WeaponDrawn, RangedDrawn = c.RangedDrawn, Shots = c.CrossbowShots, Scale = c.Scale * DisplayScale, FullRateAnimation = true });
        }
    }

    public void Dispose() => world.Dispose();
}

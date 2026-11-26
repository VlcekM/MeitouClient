using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Frame;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;
using Meitou.Simulation.Ragdolls;
using Meitou.Data.Characters;
using Meitou.Simulation.Bodies;

namespace Meitou.Game.Title;

/// <summary>
/// A presentation-only road skirmish. Its world has no population streaming, needs, crimes or save connection.
/// </summary>
sealed class TitleBattle : IDisposable
{
    /// <summary>
    /// How much larger than life the fighters are drawn, and their spacing round each centre.
    /// </summary>
    internal const float DisplayScale = 2.5f;

    /// <summary>
    /// The fighters' draw-distance multiple: the shortest NPC View Range times this is the longest, so the staged fight shows (and the title's
    /// loading, which waits for its looks, ends) whatever the range.
    /// </summary>
    const float DrawReach = CharacterRenderer.MaxDrawDistance / CharacterRenderer.MinDrawDistance;

    /// <summary>
    /// The fight's own world (combat, animation and the pairs' spacing only).
    /// </summary>
    readonly World _world;

    /// <summary>
    /// The fight's own world (the tests read it).
    /// </summary>
    internal World FightWorld => _world;

    /// <summary>
    /// The fight centres on the road, one per pair.
    /// </summary>
    readonly IReadOnlyList<Vector3> _centres;

    /// <summary>
    /// Seconds left over after the last whole tick, for the drawing's interpolation.
    /// </summary>
    double _remainder;

    /// <summary>
    /// The blows landed since the last <see cref="Advance"/> (the world's display-only hit feed), for the easter egg.
    /// </summary>
    readonly ConcurrentQueue<CombatHit> _hits = new();

    /// <summary>
    /// The easter egg: a rare blow throws its victim up in a twister.
    /// </summary>
    public TitleTornado Tornado { get; } = new();

    /// <summary>
    /// The other easter egg, a Beak Thing's ambush; null for a title without it. With it the fight is drawn to one scale round
    /// <see cref="TitleStage.Origin"/> rather than each pair round its centre, so the Beak Thing, the thrown and the fallen line up with the pairs.
    /// </summary>
    public TitleAmbush? Ambush { get; }

    /// <summary>
    /// The fight: its world with damage off and medical ticks off and each pair kept apart (<see cref="TitleSpacing"/>), a paladin and a bandit at each
    /// centre, run 90 ticks in.
    /// </summary>
    TitleBattle(PopulationData data, Func<float, float, float> ground, IReadOnlyList<Vector3> centres,
        AnimationLengths lengths, AnimationLibrary library, string paladin, string bandit, int holyNation, int bandits, AmbushCast? ambush)
    {
        this._centres = centres;
        var options = data.BodyOptions with { GlobalDamageMultiplier = 0 };
        var combat = new CombatSystem(data.Combat.Techniques, data.Combat.Constants, data.Bodies.Constants,
            options, lengths, new CombatOptions { TickMedical = false });
        var animation = new AnimationSystem(library, lengths, data.Bodies.Constants.AnimationBlendRate, combat) { Rigs = ambush?.Rigs };
        Ambush = ambush is null ? null : new TitleAmbush(ambush.Stage, combat);
        _world = new(new WorldSettings { Seed = 173 }, new OpenGroundWalkability(ground), Systems(combat, animation, ambush, options));
        using var population = new PopulationSystem(data, new PopulationSettings { Background = false });
        try
        {
            var fighters = SpawnPairs(population, paladin, bandit, holyNation, bandits);
            if (Ambush is not null) Ambush.Cast(fighters, SpawnBeak(population, ambush!, bandits));

            // Open on a fight already in progress, rather than six people drawing their weapons together.
            for (int i = 0; i < 90; i++) _world.RunTick();
            _world.CombatHitFeed = _hits;
            Ambush?.Begin();
        }
        catch
        {
            _world.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The fight's systems: combat, animation and the pairs' spacing; with the ambush also the ambush first and the ragdolls after the animation.
    /// </summary>
    IEnumerable<ITickSystem> Systems(CombatSystem combat, AnimationSystem animation, AmbushCast? ambush, BodyOptions options)
    {
        if (Ambush is not null) yield return Ambush;
        yield return combat;
        yield return animation;
        if (ambush is not null)
        {
            var ragdolls = new RagdollSystem(ambush.Rigs, null, options);
            animation.Ragdolls = ragdolls;
            yield return ragdolls;
        }

        yield return new TitleSpacing(_centres.Count);
    }

    /// <summary>
    /// What the ambush needs of the title: its places, the rigs its fallen ragdoll with, and the Beak Thing's record.
    /// </summary>
    internal sealed record AmbushCast(TitleStage Stage, CharacterRigs Rigs, string Beak);

    /// <summary>
    /// A fight of Paladins against Dust Bandits at <paramref name="centres"/>; null (said on stderr) when a character or faction record is missing.
    /// With <paramref name="ambush"/> (and <paramref name="rigs"/> and a Beak Thing record) a Beak Thing ambushes it (<see cref="TitleAmbush"/>).
    /// </summary>
    public static TitleBattle? Create(PopulationData data, Func<float, float, float> ground, IReadOnlyList<Vector3> centres,
        AnimationLengths lengths, AnimationLibrary library, TitleStage? ambush = null, CharacterRigs? rigs = null)
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

        var cast = ambush is { } stage && rigs is not null && BeakRecord(data) is { } beak ? new AmbushCast(stage, rigs, beak) : null;
        return new(data, ground, centres, lengths, library, paladin, bandit, holyNation, bandits, cast);
    }

    /// <summary>
    /// The base game's Beak Thing (an ANIMAL_CHARACTER record); null without one.
    /// </summary>
    static string? BeakRecord(PopulationData data) => data.Db.OfType(FcsRecordType.AnimalCharacter)
        .Where(r => r.Name == "Beak Thing" || r.BaseName == "Beak Thing")
        .Select(r => r.StringId)
        .FirstOrDefault();

    /// <summary>
    /// Real time, independent of the paused gameplay clock; bounded catch-up after a stalled frame. The blows landed meanwhile may throw their
    /// victims (<see cref="Tornado"/>).
    /// </summary>
    public void Advance(double seconds)
    {
        double step = Math.Clamp(seconds, 0, 0.25);
        _remainder += step;
        while (_remainder >= _world.TickSeconds)
        {
            _world.RunTick();
            _remainder -= _world.TickSeconds;
        }

        Tornado.Advance((float)step);
        while (_hits.TryDequeue(out var hit))
            if (hit.Source == CombatHitSource.Melee && Ambush is null) Tornado.Hit(hit.Victim.Slot);
    }

    /// <summary>
    /// Adds the fighters to <paramref name="list"/>: interpolated between the last two ticks, each pair kept on its centre and drawn at the display
    /// scale, a fighter the twister threw lifted and spun (<see cref="Tornado"/>).
    /// </summary>
    public void Fill(CharacterDrawList list)
    {
        var current = _world.Snapshot.Characters;
        var previous = _world.PreviousSnapshot.Characters;
        float alpha = (float)(_remainder / _world.TickSeconds);
        for (int i = 0; i < current.Count; i++)
        {
            var c = current[i];
            if (c.Appearance is null || Ambush is { Now: TitleAmbush.Step.Gone } gone && c.Id == gone.Beak) continue;

            var before = previous.Count > i ? previous[i] : c;

            // A body thrown into its ragdoll, or getting up from it, is drawn where it is this tick (its place jumps to its root body and back).
            if ((before.Ragdoll is null) != (c.Ragdoll is null)) before = c;
            var position = Ambush is null ? DrawnPosition(i, current, previous, alpha) : StagedPosition(c, before, alpha);
            var (lift, spin) = Tornado.Place(i, position);
            position.Y += lift;
            float yaw = Simulation.Session.Interp.LerpAngle(before.Yaw, c.Yaw, alpha) + spin;
            var poses = new CharacterPose[GameHost.DrawnLayerCount(before, c)];
            GameHost.BlendLayers(before, c, alpha, poses);
            list.Add(new CharacterInstance(long.MinValue + 2 + i, c.Appearance, position, yaw, poses)
            {
                WeaponDrawn = c.WeaponDrawn,
                RangedDrawn = c.RangedDrawn,
                Shots = c.CrossbowShots,
                Scale = c.Scale * DisplayScale * (Ambush is { } giant && c.Id == giant.Beak ? BeakSize : 1),
                DrawDistanceScale = DrawReach,
                FullRateAnimation = true,
                Bones = c.Ragdoll,
                Blood = Ambush?.Bloodied(c.Id.Slot) == true ? BloodSoaked : 0,
            });
        }
    }

    /// <summary>
    /// How much larger again the ambush's Beak Thing is drawn than the fighters: a giant even beside them.
    /// </summary>
    const float BeakSize = 1.6f;

    /// <summary>
    /// Every body part of a fighter the Beak Thing struck down shows the most blood.
    /// </summary>
    const ulong BloodSoaked = ulong.MaxValue;

    /// <summary>
    /// Where character <paramref name="c"/> stands drawn in an ambushed fight: its place between the ticks moved to the one scale round the stage's
    /// origin (<see cref="TitleStage"/>), its height over the ground grown with it; the Beak Thing on its run climbs to the wanderer's plateau.
    /// </summary>
    Vector3 StagedPosition(CharacterSnapshot c, CharacterSnapshot before, float alpha)
    {
        var stage = Ambush!.Stage;
        var world = Vector3.Lerp(before.Position, c.Position, alpha);
        var across = stage.Drawn(world);
        float ground = _world.Walkability.GroundHeight(across.X, across.Y);
        float over = world.Y - _world.Walkability.GroundHeight(world.X, world.Z);
        float y = ground + MathF.Max(over, 0) * DisplayScale;
        if (c.Id == Ambush.Beak && Ambush.ChaseSeconds is not null)
        {
            // Up the hill to the plateau the wanderer stood on, as the run goes on.
            var from = stage.Drawn(new Vector3(Ambush.RunFrom.X, 0, Ambush.RunFrom.Y));
            float start = _world.Walkability.GroundHeight(from.X, from.Y);
            float share = Ambush.HillShare(world);
            share = share * share * (3 - 2 * share);
            y = MathF.Max(y, start + (stage.Wanderer.Y - start) * share);
        }

        return new Vector3(across.X, y, across.Y);
    }

    /// <summary>
    /// Disposes the fight's world.
    /// </summary>
    public void Dispose() => _world.Dispose();

    /// <summary>
    /// Where fighter <paramref name="i"/> stands drawn: its pair kept centred on the road, its spacing enlarged with its bodies so their swings
    /// still line up, on the ground.
    /// </summary>
    Vector3 DrawnPosition(int i, IReadOnlyList<CharacterSnapshot> current, IReadOnlyList<CharacterSnapshot> previous, float alpha)
    {
        var before = previous.Count > i ? previous[i] : current[i];
        var position = Vector3.Lerp(before.Position, current[i].Position, alpha);
        int pair = i / 2 * 2;
        var middle = (current[pair].Position + current[pair + 1].Position) / 2;
        var oldMiddle = (previous[pair].Position + previous[pair + 1].Position) / 2;
        position = _centres[i / 2] + (position - Vector3.Lerp(oldMiddle, middle, alpha)) * DisplayScale;
        position.Y = _world.Walkability.GroundHeight(position.X, position.Z);
        return position;
    }

    /// <summary>
    /// Spawns a paladin and a bandit facing each other at every centre (in an ambushed fight, at the world point under it, <see cref="TitleStage"/>),
    /// and queues their attacks on each other. Returns them pair by pair.
    /// </summary>
    List<CharacterId> SpawnPairs(PopulationSystem population, string paladin, string bandit, int holyNation, int bandits)
    {
        var fighters = new List<CharacterId>();
        for (int i = 0; i < _centres.Count; i++)
        {
            var at = Ambush?.Stage.World(_centres[i]) ?? new Vector2(_centres[i].X, _centres[i].Z);
            var a = population.SpawnSandbox(_world, paladin, at - new Vector2(0, 16), false, 0, i + 1, holyNation);
            var b = population.SpawnSandbox(_world, bandit, at + new Vector2(0, 16), false, MathF.PI, i + 1, bandits);
            _world.Commands.Enqueue(new AttackOrder([a], b) { Tick = i * 7 });
            _world.Commands.Enqueue(new AttackOrder([b], a) { Tick = i * 7 });
            fighters.Add(a);
            fighters.Add(b);
        }

        return fighters;
    }

    /// <summary>
    /// Spawns the Beak Thing waiting off the right of the frame, facing the fight.
    /// </summary>
    CharacterId SpawnBeak(PopulationSystem population, AmbushCast ambush, int bandits)
    {
        var at = ambush.Stage.World(ambush.Stage.Entry);
        var toward = ambush.Stage.World(_centres[0]) - at;
        return population.SpawnSandbox(_world, ambush.Beak, at, false, MathF.Atan2(toward.X, toward.Y), _centres.Count + 1, bandits);
    }
}

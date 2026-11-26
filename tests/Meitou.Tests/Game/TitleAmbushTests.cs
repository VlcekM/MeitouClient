using System.Numerics;
using Meitou.Content;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Data.Ogre;
using Meitou.Game.Title;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;
using Xunit;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Game;

/// <summary>
/// The title fight's Beak Thing easter egg (<see cref="TitleAmbush"/>): when it rolls, the one scale it draws the fight at, the far pair's throw, and
/// the whole ambush played out in the fight's own world on the base game's records.
/// </summary>
public class TitleAmbushTests(ITestOutputHelper output)
{
    /// <summary>
    /// A stage on flat ground: the fights round the origin, the Beak Thing waiting to their right, the landing far to their left, the wanderer
    /// and the camera in front.
    /// </summary>
    static readonly TitleStage Flat = new(Vector3.Zero, 2.5f, new(1500, 0, 0), new(-1500, 0, -200), new(-600, 0, 1500), new(-500, 350, 2200));

    /// <summary>
    /// The flat stage's ground height: dry land, above the world's water.
    /// </summary>
    const float Ground = 2000;

    /// <summary>
    /// The three fight centres on the flat stage, the first the furthest.
    /// </summary>
    static readonly Vector3[] Centres = [new(-150, 0, -250), new(0, 0, 0), new(150, 0, 250)];

    /// <summary>
    /// The variable forces it either way, a screenshot never rolls it, and otherwise one roll in ten does.
    /// </summary>
    [Theory]
    [InlineData("1", true, 5, true)]
    [InlineData(" 1 ", false, 5, true)]
    [InlineData("0", false, 0, false)]
    [InlineData(null, true, 0, false)]
    [InlineData(null, false, 0, true)]
    [InlineData(null, false, 3, false)]
    [InlineData("yes", false, 9, false)]
    public void Rolls_as_the_variable_and_the_roll_say(string? forced, bool screenshot, int roll, bool ambushed) =>
        Assert.Equal(ambushed, TitleAmbush.Rolls(forced, screenshot, roll));

    /// <summary>
    /// The stage's world and drawn points are one scale apart round its origin, both ways.
    /// </summary>
    [Fact]
    public void Stage_scales_round_its_origin()
    {
        var stage = Flat with { Origin = new Vector3(100, 40, -200) };
        var world = stage.World(new Vector3(350, 0, -450));
        Assert.Equal(new Vector2(200, -300), world);
        Assert.Equal(new Vector2(350, -450), stage.Drawn(new Vector3(world.X, 0, world.Y)));
    }

    /// <summary>
    /// The far pair's first fighter is launched to land on the landing (as the ballistic range has it), the second a little short of it.
    /// </summary>
    [Fact]
    public void Throw_lands_on_the_left()
    {
        var ambush = new TitleAmbush(Flat);
        var from = new Vector3(-60, 0, -100);
        var target = Flat.World(Flat.Landing) - new Vector2(from.X, from.Z);
        var first = ambush.ThrowToward(from, 0);
        var second = ambush.ThrowToward(from, 1);
        float range = 2 * first.Y * new Vector2(first.X, first.Z).Length() / 98.1f;
        Assert.Equal(target.Length(), range, 1);
        Assert.True(Vector2.Dot(Vector2.Normalize(new Vector2(first.X, first.Z)), Vector2.Normalize(target)) > 0.999f);
        Assert.True(new Vector2(second.X, second.Z).Length() < new Vector2(first.X, first.Z).Length());
    }

    /// <summary>
    /// The whole ambush on the base game's Paladins, Dust Bandits and Beak Thing: the charge, the far pair thrown towards the landing, the other four
    /// struck down one at a time, each by a swing, the run past the camera, and the two risers standing and fighting each other at the end while
    /// the rest stay down.
    /// </summary>
    [Fact]
    [Slow]
    public void Ambush_plays_out()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null || InstallData.BaseGame is null, "Kenshi install not found");

        var db = InstallData.BaseGame!;
        var assets = new AssetLocator(install!);
        var data = PopulationData.Create(db, [], new GeneratedAppearances(db, install!.Root));
        var lengths = AnimationLengths.Load(install.Root, db, assets.Find);
        using var battle = TitleBattle.Create(data, (_, _) => Ground, Centres, lengths, AnimationLibrary.FromDatabase(db), Flat,
            CharacterRigs.For(db, assets));
        Assert.NotNull(battle);

        var ambush = battle.Ambush!;
        var world = battle.FightWorld;
        var start = world.Snapshot.Characters.Take(6).Select(c => c.Position).ToArray();
        var seen = Play(battle, ambush);

        AssertStepsInOrder(seen);
        AssertFarPairThrown(world, start);

        // Each of the four fell to one of the Beak Thing's swings.
        Assert.Equal(0, ambush.Stalls);
        AssertRisersFight(ambush, world);
    }

    /// <summary>
    /// Runs the fight 120 seconds in 0.05 s frames, writing each change of step or of the number down to the output; returns the steps seen.
    /// </summary>
    List<(float Seconds, TitleAmbush.Step Step)> Play(TitleBattle battle, TitleAmbush ambush)
    {
        var world = battle.FightWorld;
        var seen = new List<(float Seconds, TitleAmbush.Step Step)>();
        int downs = 0;
        for (int frame = 0; frame < 20 * 120; frame++)
        {
            battle.Advance(0.05);
            int down = world.Snapshot.Characters.Take(6).Count(c => c.Down);
            if (seen.Count == 0 || seen[^1].Step != ambush.Now || down != downs)
            {
                seen.Add((frame * 0.05f, ambush.Now));
                output.WriteLine($"{frame * 0.05f,6:0.00} s {ambush.Now,-8} down {down} beak {world.Snapshot.Characters[6].Position}");
            }

            downs = down;
        }

        return seen;
    }

    /// <summary>
    /// In order: the charge after the wait, the melee, the run, then gone.
    /// </summary>
    static void AssertStepsInOrder(List<(float Seconds, TitleAmbush.Step Step)> seen) =>
        Assert.Equal([TitleAmbush.Step.Charge, TitleAmbush.Step.Melee, TitleAmbush.Step.Chase, TitleAmbush.Step.Gone],
            seen.Select(s => s.Step).Where(s => s != TitleAmbush.Step.Waiting).Distinct());

    /// <summary>
    /// The far pair flew towards the landing, at least half the way from where each <paramref name="start"/>ed.
    /// </summary>
    static void AssertFarPairThrown(SimWorld world, Vector3[] start)
    {
        var landing = Flat.World(Flat.Landing);
        for (int f = 0; f < 2; f++)
        {
            var now = world.Snapshot.Characters[f].Position;
            float before = Vector2.Distance(landing, new Vector2(start[f].X, start[f].Z));
            Assert.True(Vector2.Distance(landing, new Vector2(now.X, now.Z)) < before * 0.5f, $"fighter {f} at {now}");
        }
    }

    /// <summary>
    /// The risers stand, bloodied, and fight each other; the other four stay down.
    /// </summary>
    static void AssertRisersFight(TitleAmbush ambush, SimWorld world)
    {
        var end = world.Snapshot.Characters;
        Assert.All(new[] { 0, 1, 4, 5 }, f => Assert.True(end[f].Down, $"fighter {f} is up"));
        Assert.All(new[] { 2, 3 }, f => Assert.False(end[f].Down, $"riser {f} is down"));
        Assert.All(new[] { 2, 3 }, f => Assert.True(ambush.Bloodied(end[f].Id.Slot)));
        Assert.All(new[] { 2, 3 }, f => Assert.Equal(CharacterTask.Attack, end[f].Task));
    }
}

using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Faction campaigns (docs/game/factions-squads-towns.md "Campaigns", docs/simulation.md "Off-screen tier").
/// </summary>
public class CampaignTests(ITestOutputHelper output)
{
    [Fact]
    public void A_rolls_chance_is_a_quarter_of_the_daily_chance_falling_off_linearly_from_range_near_to_range_far()
    {
        var c = new CampaignData { Id = "c", Name = "c", DisplayName = "c", ChancePerDay = 0.4f, RangeNear = 25000, RangeFar = 90000 };

        Assert.Equal(1, CampaignData.Falloff(10000, 25000, 90000));
        Assert.Equal(1, CampaignData.Falloff(25000, 25000, 90000));
        Assert.Equal(0.5f, CampaignData.Falloff(57500, 25000, 90000), 5);
        Assert.Equal(0, CampaignData.Falloff(90001, 25000, 90000));
        Assert.Equal(1, CampaignData.Falloff(1e6f, 25000, 0));   // no range far: no falloff
        Assert.Equal(0.1f, c.RollChance(10000, 1), 5);
        Assert.Equal(0.05f * 3, c.RollChance(57500, 3), 5);   // times raidFrequencyMult
        Assert.Equal(0.05f, (c with { IgnoresChanceMults = true }).RollChance(57500, 3), 5);
    }

    sealed record Run(SimWorld World, PopulationSystem Population) : IDisposable
    {
        public void Dispose()
        {
            World.Dispose();
            foreach (var s in World.Systems) (s as IDisposable)?.Dispose();
        }
    }

    static Run Start(PopulationData data, PopulationSettings settings)
    {
        var walk = new OpenGroundWalkability((_, _) => 300);
        var systems = StandardSystems.Build(data, walk,
                new StandardSystemOptions { Parts = StandardParts.Population | StandardParts.Player, Population = settings });
        return new Run(new SimWorld(new WorldSettings { Seed = 11, Threads = 2, PublishSnapshots = false }, walk, systems.Systems),
                systems.Population!);
    }

    [Fact, Slow]
    public void A_raid_on_a_player_outpost_gathers_travels_wins_and_is_removed_and_a_save_in_the_middle_keeps_it()
    {
        const float distance = 12000;

        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        var levels = InstallData.Levels!;
        var data = PopulationData.Create(InstallData.FullLoadOrder!, levels.Towns());
        var settings = new PopulationSettings { RaidFrequencyMult = 1000 };   // every roll passes
        using var run = Start(data, settings);
        var (world, population) = (run.World, run.Population);
        world.Player.Faction = data.PlayerFaction;

        // A hostile town assault from a faction with a town, needing no leader or population and passing its world states as a new game answers them,
        // whose squads
        // plan someone then; the outpost Distance from that town, far enough that the player's watcher there does not load the raiders' town.
        bool Makes(CampaignData c) => c.SquadsToUse.All(e => data.Db.Find(e.Id) is { } r && SquadFactory.IsTemplate(r) && SquadFactory.Plan(data.Db,
                SquadTemplate.From(r), 1, 1, 1, population.States).TotalMembers > 0);
        (int Faction, CampaignData C, TownSite Town)? pick = null;
        for (int f = 0; f < data.Factions.Count && pick is null; f++)
        {
            var town = data.Sites.FirstOrDefault(s => data.SiteFactions[s.Index] == f && s.Town.Type is not (TownType.NestMarker or TownType.Null));
            if (town is null) continue;
            foreach (var link in data.Factions[f].Campaigns)
                if (population.CampaignOf(link.Id) is { TerritorialTriggers: true, IsHostile: true, Key: CampaignKey.AssaultTown, HasPt2: true,
                        TargetPopulationMin: 0, TargetTechLevel: 0,
                        IgnoresChanceMults: false, SpecialLeaders.Count: 0,
                                SquadsToUse.Count: > 0 } c && c.WorldStates.All(s => IWorldStates.Passes(population.States, data.Db, s.Key,
                                s.Value ? 1 : 0)) && Makes(c))
                { pick = (f, c, town); break; }
        }

        Assert.NotNull(pick);

        var (faction, campaign, home) = pick.Value;
        var outpost = world.Buildings.AddTown(home.Position + new Vector3(distance, 0, 0), data.PlayerFaction);

        // A roll aims only at a player town one of the player's characters is in (Town slot 70); one stands there until the raid sets out.
        string human = data.Db.OfType(Data.Fcs.FcsRecordType.Character)
            .First(r => r.GetReferences("race").Any(x => x.Values.Value0 > 0 && data.Db.Find(x.TargetStringId)?.BaseName == "Greenlander")
                    && r.GetReferences("stats").Count > 0).StringId;
        var watcher = population.SpawnSandbox(world, human, new Vector2(outpost.Position.X, outpost.Position.Z), player: true);
        output.WriteLine($"{data.Factions[faction].Name}: {campaign.Name} ({campaign.NumForces} men) against an outpost by {home.Town.Name}");

        world.RunTicks(15);
        var war = population.Wars[faction];
        foreach (var m in population.Wars) m.UpdateTime = m == war ? 0 : 1e9;   // only this faction rolls, at once
        bool RunUntil(Func<bool> done, int ticks)
        {
            for (int t = 0; t < ticks && !done(); t += 150) world.RunTicks(150);
            return done();
        }

        // The roll starts it: squads gathered beyond the men needed, the warning posted.
        // one war manager runs per tick at a wrapping cursor (FUN_1402e74f0), so the roll waits up to one lap
        Assert.True(RunUntil(() => war.Actives.Count > 0, 300 + population.Wars.Count));

        // The watcher leaves and the camera looks far off, so the raid travels and fights unloaded as before.
        int watchers = world.Characters.Cold(watcher.Slot)!.SquadId;
        world.Characters.Remove(watcher);
        world.Squads.Remove(watchers);
        world.Commands.Enqueue(new FocusCommand(outpost.Position + new Vector3(0, 0, 60000)) { Tick = world.Tick + 1 });
        var inst = war.Actives[0];

        Assert.Equal(CampaignTown.OfPlayer(outpost.Number), inst.Target);
        Assert.True(inst.Men > inst.Required && inst.Platoons.Count > 0);
        Assert.All(inst.Platoons, id => Assert.Same(inst, world.Platoons.Find(id)!.Campaign));

        var messages = world.TakeMessages();

        Assert.Contains(messages, m => m.StartsWith("Warning: ") && m.EndsWith(" moving towards " + outpost.Name));
        Assert.NotEqual(0, war.Known.Single(k => k.Id == inst.CampaignId).LastStart);

        // Travel: the leader reaches the outpost, the others follow it.
        Assert.True(RunUntil(() => inst.Phase == 1, 60_000));

        output.WriteLine($"arrived after {world.GameHours - 37:0.00} h with {inst.Platoons.Count} squad(s)");
        var leaderAt = world.Platoons.Find(inst.Leader)!.Position;

        Assert.True(Vector2.Distance(leaderAt, new Vector2(outpost.Position.X, outpost.Position.Z)) <= 0.8f * outpost.Radius);

        // A save at the target and a load: the same campaign, phase and squads (the phase timer starts again).
        var game = SaveCapture.Capture(world, data, new SaveClock(1, 13, 0),
                options: new SaveCaptureOptions { Placements = [.. levels.Towns()], Seed = 3 });
        var state = game.Factions.Single(f => f.Id == data.Factions[faction].Id).War!;

        Assert.Equal(1, state.Ints["num actives"]);
        Assert.Equal(inst.CampaignId, state.Strings["dat c0"]);
        Assert.Equal(inst.Platoons.Count, state.Ints["num plats"]);

        using (var back = Start(data, settings))
        {
            SaveLoader.Load(back.World, game, data, new SaveLoadOptions { ApplyRelations = false });
            back.World.RunTicks(15);
            var again = back.Population.Wars[faction].Actives.Single();

            Assert.Equal((inst.CampaignId, inst.Id, 1, inst.Reached), (again.CampaignId, again.Id, again.Phase, again.Reached));
            Assert.Equal(inst.Platoons.Select(id => world.Platoons.Find(id)!.TemplateId).Order(),
                    again.Platoons.Select(id => back.World.Platoons.Find(id)!.TemplateId).Order());
            Assert.Equal(CampaignTown.OfPlayer(1), again.Target);
            Assert.Equal(inst.Home, again.Home);
        }

        // Unloaded, nobody fights: the assault wins after 4 h, then is removed 3 h later and its squads go home.
        Assert.True(RunUntil(() => inst.Phase == 2, 4 * 3300 + 2 * population.Wars.Count));
        Assert.Contains(world.TakeMessages(), m => m.EndsWith(" against " + outpost.Name + " has ended"));

        var squads = inst.Platoons.ToList();

        Assert.True(RunUntil(() => war.Actives.Count == 0, 3 * 3300 + 2 * population.Wars.Count));
        Assert.All(squads, id => Assert.Null(world.Platoons.Find(id)?.Campaign));

        output.WriteLine($"ended after {world.GameHours - 37:0.00} h; follow-ups queued: {string.Join(", ", population.Wars.SelectMany(m => m.Requests).Select(r => r.CampaignId))}");
        if (campaign.VictoryTriggers.Sum(t => t.V2) >= 100)

            Assert.NotEmpty(population.Wars.SelectMany(m => m.Requests));
    }
}

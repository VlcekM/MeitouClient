using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Town duty rules pinned to the original's behaviour (docs/game/ai-tasks.md, docs/simulation.md).
/// </summary>
public class TownDutyRulesTests
{
    static readonly Vector3 Centre = new(2000, 150, 2000);

    /// <summary>
    /// FUN_1405a0f10: with a mission target set that is not the town the carrier stands in, the cage desire is 0; in that town it is 2 for a living
    /// body.
    /// </summary>
    [Fact]
    public void Cage_desire_is_zero_while_the_mission_town_is_another_than_the_one_stood_in()
    {
        var data = ArrestTests.Town();
        var carry = new CarrySystem(BaseGame, BodyOptions.Default, 1) { Relations = data.Relations };
        using var population = new PopulationSystem(data);
        var duty = new TownDutySystem(data, population, carry);
        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 7, PublishSnapshots = false }, walk, [new PlayerSystem(), carry, duty]);
        var board = new Blackboard([]);
        var squad = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = data.FactionIndex("50-a"), Ai = board,
                HomeCentre = new Vector2(Centre.X, Centre.Z), HomeRadius = 100 };
        world.Squads.Add(squad);
        CharacterId Add(int faction)
        {
            var cold = new Combatant { Name = "c" }.MakeCold();
            cold.Faction = faction;
            cold.SquadId = squad.Id;
            return world.Characters.Spawn(new CharacterHot { Position = Centre, Health = 100 }, cold, 0);
        }
        var carrier = Add(squad.Faction);
        var body = Add(data.Relations.PlayerFaction);
        var here = AiSystem.TownAt(data, world, Centre);
        Assert.False(here.IsNone);

        Assert.Equal(TownDutySystem.AliveDesire, duty.CageDesire(world, carrier.Slot, body.Slot));
        board.Destination = here;
        Assert.Equal(TownDutySystem.AliveDesire, duty.CageDesire(world, carrier.Slot, body.Slot));
        board.Destination = CampaignTown.OfPlayer(99);
        Assert.Equal(0, duty.CageDesire(world, carrier.Slot, body.Slot));
    }
}

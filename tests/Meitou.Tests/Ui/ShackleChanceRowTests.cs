using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Game;
using Meitou.Game.Host;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Player;

namespace Meitou.Tests.Ui;

/// <summary>
/// The Unlock Shackles row carries the shackles' pick chance in its chance column, as the original's menu does for verb 201 (#1503).
/// </summary>
public class ShackleChanceRowTests
{
    /// <summary>
    /// A selected picker of lockpicking 10 and an inspected stranger in level 20 shackles: the row reads 45 (0.9 / 2^((20 - 10) / 10)).
    /// </summary>
    [Fact]
    public void Unlock_shackles_shows_the_pick_chance_of_the_worn_shackles()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: [new PlayerSystem()]);
        var world = session.World;
        world.Player.Faction = 0;
        var stats = new CharacterStats();
        stats[StatsEnumerated.Lockpicking] = 10;
        var picker = world.Characters.Spawn(new CharacterHot { Position = new(5, 0, 0), Health = 100 },
            new CharacterCold { Name = "Picker", IsPlayer = true, Faction = 0, Stats = stats, Inventory = new Inventory() }, 0);
        var wearer = world.Characters.Spawn(new CharacterHot { Position = Vector3.Zero, Health = 100 },
            new CharacterCold { Name = "Prisoner", Faction = 3, Inventory = new Inventory() }, 0);
        var shackles = new ItemInstance { Record = "shackles", Section = "boots", IsLocked = true, Lock = new LockState { Level = 20, Locked = true } };
        world.Characters.Cold(wearer.Slot)!.Inventory!.Items.Add(shackles);
        world.Player.Selection.Add(picker);
        world.Commands.Enqueue(new InspectCommand(wearer) { Tick = world.Tick });
        world.RunTick();

        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position) { Width = 1600, Height = 900 };
        var target = session.CurrentSnapshot.Characters.First(c => c.Id == wearer);
        var row = ui.ContextMenuWindow.Verbs(target).Select(v => v.Caption).Single(c => c.StartsWith("Unlock Shackles", StringComparison.Ordinal));

        Assert.Equal("Unlock Shackles\t45", row);
    }
}

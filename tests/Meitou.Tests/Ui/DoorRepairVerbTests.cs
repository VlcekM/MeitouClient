using System.Numerics;
using Meitou.Game;
using Meitou.Game.Host;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Player;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Ui;

/// <summary>
/// The held menu on the player's damaged door offers Repair (95) before the door's own rows, locked or not, as the original's building branch does
/// (95 when damaged, then the door's verbs); a locked gate damaged short of breaking had no Repair (#1571).
/// </summary>
public class DoorRepairVerbTests
{
    /// <summary>
    /// The town house's first door, owned by the player, one of the player's characters selected: damaged and locked it reads Repair, Unlock,
    /// Use Tools, Use Strength, Smash; damaged and unlocked Repair, Lock, Open. Catches a Repair offered only on a broken door.
    /// </summary>
    [Theory]
    [InlineData(true, new[] { "Repair", "Unlock", "Use Tools", "Use Strength", "Smash" })]
    [InlineData(false, new[] { "Repair", "Lock", "Open" })]
    public void A_damaged_own_door_offers_repair_before_its_lock_verbs(bool locked, string[] expected)
    {
        using var setup = Setup(locked, progress: 0.3f, owner: 0);

        Assert.Equal(expected, setup.Verbs());
    }

    /// <summary>
    /// A whole door has no Repair, nor has another faction's damaged one; a broken own door has Repair alone (as before).
    /// </summary>
    [Fact]
    public void Repair_needs_an_own_door_below_its_whole_progress()
    {
        using (var whole = Setup(locked: true, progress: 1, owner: 0))
            Assert.DoesNotContain("Repair", whole.Verbs());

        using (var theirs = Setup(locked: true, progress: 0.3f, owner: 3))
            Assert.DoesNotContain("Repair", theirs.Verbs());

        using var broken = Setup(locked: false, progress: 0, owner: 0, broken: true);
        Assert.Equal(["Repair"], broken.Verbs());
    }

    /// <summary>
    /// Choosing Repair on the damaged locked door orders the build work on that door (<see cref="BuildOrder"/> on its fixture id), which the
    /// construction system takes for a door below its whole progress.
    /// </summary>
    [Fact]
    public void Repair_on_a_damaged_locked_door_orders_build_work_on_the_door()
    {
        using var setup = Setup(locked: true, progress: 0.3f, owner: 0);
        var repair = setup.Ui.ContextMenuWindow.DoorVerbs((setup.Pick, true)).Single(v => v.Caption == "Repair");

        repair.Give();

        var order = Assert.Single(setup.Session.World.Commands.TakeDue(long.MaxValue).OfType<BuildOrder>());
        Assert.Equal(setup.Door.Id, order.Site);
    }

    /// <summary>
    /// The synthetic town's house door set up as asked, a player character selected and the snapshot published.
    /// </summary>
    static DoorSetup Setup(bool locked, float progress, int owner, bool broken = false)
    {
        var data = ArrestTests.Town();
        var session = new WorldSession(Vector3.Zero, (-5000, -5000, 5000, 5000), (_, _) => 150,
            systems: [new PlayerSystem(), new LockSystem(BaseGame, BodyOptions.Default)]);
        var world = session.World;
        world.Player.Faction = 0;
        world.Fixtures.RegisterSite(world, data, 0);

        var door = world.Fixtures.Find("0.0:house#0")!;
        door.Owner = owner;
        door.DoorProgress = progress;
        door.DoorBroken = broken;
        door.Lock!.Locked = locked;

        var hero = world.Characters.Spawn(new CharacterHot { Position = door.Position + new Vector3(30, 0, 0), Health = 100 },
            new CharacterCold { Name = "Engineer", IsPlayer = true, Faction = 0, Inventory = new Inventory() }, 0);
        world.Player.Selection.Add(hero);
        world.RunTick();

        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position) { Width = 1600, Height = 900 };
        var pick = new DoorPick(door.Id, door.Building, door.DoorIndex, door.Owner, door.Position);
        return new DoorSetup(session, ui, door, pick);
    }

    /// <summary>
    /// A set-up door: its session, the interface over it, the fixture and the pick the menu takes.
    /// </summary>
    sealed record DoorSetup(WorldSession Session, PlayerInterface Ui, Fixture Door, DoorPick Pick) : IDisposable
    {
        /// <summary>
        /// The door menu's captions, its lock state as the snapshot shows it.
        /// </summary>
        public string[] Verbs()
        {
            bool locked = Session.CurrentSnapshot.Doors.GetValueOrDefault((Door.Building, Door.DoorIndex)).Locked;
            return [.. Ui.ContextMenuWindow.DoorVerbs((Pick, locked)).Select(v => v.Caption)];
        }

        /// <summary>
        /// Ends the session.
        /// </summary>
        public void Dispose() => Session.Dispose();
    }
}

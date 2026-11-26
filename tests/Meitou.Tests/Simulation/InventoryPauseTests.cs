using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Production;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The pause of a character holding an inventory window it opened (<see cref="InventoryPause"/>, docs/simulation.md "Inventory pause"), on the task
/// start rig with the decision step: the focus, the hold against jobs, combat both ways, the next order and the wait after the window closes.
/// </summary>
public class InventoryPauseTests
{
    /// <summary>
    /// The rig with the decision step; <paramref name="fighting"/> keeps the fighter on its opening attack of the far enemy (in combat), else it is
    /// stopped and stands idle. Not sneaking either way.
    /// </summary>
    static TaskStartTests.Rig Make(bool fighting)
    {
        var rig = new TaskStartTests.Rig(carrying: false, decide: true, ai: true);
        rig.World.RunTicks(3);
        if (!fighting)
        {
            rig.Say(new StopCommand([rig.Fighter]) { Tick = rig.World.Tick });
            rig.World.RunTick();
        }

        rig.Cold(rig.Fighter).Orders &= ~CharacterOrders.Sneak;
        return rig;
    }

    static void RunUntil(TaskStartTests.Rig rig, Func<bool> done, int max, string what)
    {
        for (int t = 0; t < max && !done(); t++) rig.World.RunTick();

        Assert.True(done(), $"{what} within {max} ticks");
    }

    /// <summary>
    /// The fighter opens the downed NPC's inventory and the window is open.
    /// </summary>
    static void OpenLoot(TaskStartTests.Rig rig)
    {
        rig.Say(new OpenLootOrder([rig.Fighter], rig.Downed) { Tick = rig.World.Tick });
        RunUntil(rig, () => LootOpen(rig), 600, "the loot window opening");
    }

    static bool LootOpen(TaskStartTests.Rig rig) => rig.World.Player.OpenLoot == rig.Downed && rig.World.Player.LootOpener == rig.Fighter;

    /// <summary>
    /// An NPC of the enemy's faction beside the mate attacks it, and the mate fights back.
    /// </summary>
    static void AttackMate(TaskStartTests.Rig rig)
    {
        var at = rig.World.Characters.Next[rig.Mate.Slot].Position;
        var foe = rig.Add(at.X + 12, at.Z, player: false);
        rig.Say(new AttackOrder([foe], rig.Mate) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Cold(rig.Mate).InCombat || rig.Task(rig.Mate) == CharacterTask.Attack, 300, "the mate fighting");
    }

    /// <summary>
    /// An unowned town chest (a 4 x 4 general storage) at <paramref name="at"/>, registered as a loaded town's; its fixture id.
    /// </summary>
    static string Chest(TaskStartTests.Rig rig, Vector3 at)
    {
        var db = rig.Data.Db;
        var file = new FcsFile();
        file.Records.AddRange([new FcsRecord { StringId = "town-t", Flags = 0x10, Name = "Town", RecordType = FcsRecordType.Town },
            new FcsRecord { StringId = "chest-t", Flags = 0x10, Name = "Chest", RecordType = FcsRecordType.Building }]);
        db.Apply(file, "chest.mod");
        var house = new TownBuilding("house", "chest-t", "Chest", at, 0, false, false, [])
        {
            Fixtures = [new TownFixture("chest", "chest-t", false, at, 0)
                    { Function = TownBuildings.GeneralStorageFunction, StorageWidth = 4, StorageHeight = 4 }],
        };
        var data = PopulationData.Create(db, [new TownPlacement("i-town", "town-t", at, null)],
                buildings: new Dictionary<string, List<TownBuilding>> { ["i-town"] = [house] });
        rig.World.Fixtures.RegisterSite(rig.World, data, 0);

        return rig.World.Fixtures.OfBuilding(house.Key).Single(f => f.Contents is not null).Id;
    }

    /// <summary>
    /// The window opening selects its opener alone and shows its squad, though another character of another squad was selected. Catches a window
    /// that leaves the old selection (and the inventory of the wrong character) on screen.
    /// </summary>
    [Fact]
    public void Opening_a_window_selects_the_opener_and_its_squad()
    {
        using var rig = Make(fighting: false);
        rig.Join(rig.Fighter, null);
        rig.Say(new SelectCommand([rig.Mate]) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.NotEqual(rig.Cold(rig.Fighter).SquadId, rig.World.Player.Squad);

        OpenLoot(rig);

        Assert.Equal([rig.Fighter], rig.World.Player.Selection);
        Assert.Equal(rig.Cold(rig.Fighter).SquadId, rig.World.Player.Squad);
    }

    /// <summary>
    /// Neither a workstation job nor the medic job (a hurt mate beside it) takes the looter off while its window is open. Catches a job that walks
    /// the looter away from the window.
    /// </summary>
    [Fact]
    public void Jobs_do_not_break_the_pause()
    {
        using var rig = Make(fighting: false);
        var f = rig.Fighter;
        rig.Say(new AddJob([f], TaskType.OperateMachinery, rig.WellSite) { Tick = rig.World.Tick });
        rig.Say(new MedicJobCommand([f]) { Tick = rig.World.Tick + 1 });
        RunUntil(rig, () => rig.Cold(f).Jobs.Count == 2 && rig.Cold(f).Running is { Source: TaskSource.Job }, 600, "a job running");

        OpenLoot(rig);
        rig.World.RunTicks(4 * ProductionSystem.JobCheckTicks);

        Assert.True(LootOpen(rig));
        Assert.Equal(CharacterTask.Loot, rig.Task(f));
        Assert.Equal(TaskSource.Order, rig.Cold(f).Running!.Source);
    }

    /// <summary>
    /// A looter out of combat when told: an ally near it entering combat ends its pause, and the window with it. Catches a looter that stands at
    /// the window while its squad fights.
    /// </summary>
    [Fact]
    public void An_ally_entering_combat_ends_the_pause_of_a_looter_told_out_of_combat()
    {
        using var rig = Make(fighting: false);
        OpenLoot(rig);

        Assert.False(rig.Cold(rig.Fighter).PauseIgnoresCombat);

        AttackMate(rig);
        RunUntil(rig, () => !LootOpen(rig) && rig.Task(rig.Fighter) != CharacterTask.Loot, 30, "the pause ending");
    }

    /// <summary>
    /// A looter told while in combat ignores combat: its window stays open, and it at it, while an ally beside it fights. Catches the original's
    /// melee-threat end, or an ally's fight, taking the looter off.
    /// </summary>
    [Fact]
    public void A_looter_told_in_combat_ignores_combat()
    {
        using var rig = Make(fighting: true);
        Assert.Equal(CharacterTask.Attack, rig.Task(rig.Fighter));

        OpenLoot(rig);

        Assert.True(rig.Cold(rig.Fighter).PauseIgnoresCombat);

        AttackMate(rig);
        for (int t = 0; t < 150; t++)
        {
            rig.World.RunTick();
            Assert.True(LootOpen(rig), $"the window open {t} ticks into the mate's fight");
        }

        Assert.Equal(CharacterTask.Loot, rig.Task(rig.Fighter));
    }

    /// <summary>
    /// A looter told while in combat ignores an attacker at its side too: the window stays open and it at it. Catches the original's end of the
    /// held loot at a melee threat within 40.
    /// </summary>
    [Fact]
    public void A_looter_told_in_combat_ignores_its_own_attacker()
    {
        using var rig = Make(fighting: true);
        OpenLoot(rig);
        var at = rig.World.Characters.Next[rig.Fighter.Slot].Position;
        var foe = rig.Add(at.X + 12, at.Z, player: false);
        rig.Say(new AttackOrder([foe], rig.Fighter) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Combat.ImmediateThreat(rig.World, rig.Fighter.Slot), 300, "the attacker threatening the looter");
        for (int t = 0; t < 150; t++)
        {
            rig.World.RunTick();
            Assert.True(LootOpen(rig), $"the window open {t} ticks into the attack");
        }
    }

    /// <summary>
    /// The player's next order ends the pause: a move walks the looter away and closes the loot window. Catches a pause that holds the character
    /// against the player.
    /// </summary>
    [Fact]
    public void A_new_order_ends_the_pause()
    {
        using var rig = Make(fighting: false);
        OpenLoot(rig);
        rig.Say(new MoveOrder([rig.Fighter], new Vector3(900, 0, 1900)) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Equal(CharacterTask.GoTo, rig.Task(rig.Fighter));
        Assert.False(LootOpen(rig));
    }

    /// <summary>
    /// The window closing ends the pause and the looter's jobs wait half a second (15 ticks) before they run again: no job starts within the wait,
    /// and the well job runs after it. Catches jobs taking the character the tick its window closes, or never again.
    /// </summary>
    [Fact]
    public void Jobs_wait_half_a_second_after_the_window_closes()
    {
        using var rig = Make(fighting: false);
        var f = rig.Fighter;
        rig.Say(new AddJob([f], TaskType.OperateMachinery, rig.WellSite) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Cold(f).Running is { Source: TaskSource.Job }, 600, "the well job running");
        OpenLoot(rig);

        rig.Say(new CloseContainer { Tick = rig.World.Tick });
        long closed = rig.World.Tick;
        int wait = (int)MathF.Ceiling(InventoryPause.ResumeSeconds / rig.World.TickSeconds);

        Assert.Equal(15, wait);

        while (rig.World.Tick < closed + wait)
        {
            rig.World.RunTick();
            Assert.False(rig.Cold(f).Running is { Source: TaskSource.Job }, $"a job running {rig.World.Tick - closed} ticks after the close");
        }

        Assert.NotEqual(CharacterTask.Loot, rig.Task(f));
        RunUntil(rig, () => rig.Cold(f).Running is { Source: TaskSource.Job }, 3 * ProductionSystem.JobCheckTicks, "the well job again");
    }

    /// <summary>
    /// A container's window holds its opener the same way: on its order, with its job waiting, until the window closes. Catches a container
    /// opener that goes back to its job as soon as the window opens.
    /// </summary>
    [Fact]
    public void A_container_window_holds_its_opener_until_it_closes()
    {
        using var rig = Make(fighting: false);
        var f = rig.Fighter;
        string store = Chest(rig, new Vector3(1100, 300, 2000));
        rig.Say(new AddJob([f], TaskType.OperateMachinery, rig.WellSite) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Cold(f).Running is { Source: TaskSource.Job }, 600, "the well job running");

        rig.Say(new OpenContainerOrder([f], store) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.World.Player.OpenContainer == store, 600, "the container window opening");
        rig.World.RunTicks(4 * ProductionSystem.JobCheckTicks);

        Assert.Equal(store, rig.World.Player.OpenContainer);
        Assert.Equal((CharacterTask.Idle, TaskSource.Order), (rig.Task(f), rig.Cold(f).Running!.Source));
        Assert.Equal([f], rig.World.Player.Selection);

        rig.Say(new CloseContainer { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Cold(f).Running is { Source: TaskSource.Job }, 3 * ProductionSystem.JobCheckTicks + 15, "the well job again");
    }
}

using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Game;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;

namespace Meitou.Tests.Ui;

/// <summary>
/// The original farm panel's crop values and bar formatting, without an installed game or GPU.
/// </summary>
public class FarmPanelTests
{
    /// <summary>
    /// A 25-plant crop with an eight-hour growth and the original's 48-hour harvest and eight-hour death periods.
    /// </summary>
    static readonly FarmData Crop = new()
    {
        Id = "crop", Plants = 25, GrowthTime = 8, HarvestTime = 48, DeathTime = 8, DeathThreshold = 0.4f,
        DroughtMultiplier = 0.5f, DroughtDeathTime = 48,
    };

    /// <summary>
    /// A half-grown, partially harvested crop in an environment giving 40% yield before farmer skill.
    /// </summary>
    static FarmView Growing => new(0.4f, 0.1f, 3, 25, FarmStatus.Growing, 0.8f, 0.5f, 0.4f)
    {
        Data = Crop, Start = -0.2f, FarmerSkill = 0,
    };

    /// <summary>
    /// A powered farm with one unskilled worker, at half health and half power.
    /// </summary>
    static MachineSnapshot Machine(FarmView farm) => new("field", "farm", [], null, null, farm, (byte)farm.Status, 1, 2)
    {
        Panel = MachinePanel.Farm,
        Efficiency = new(1, 2, 0.5f, 0.5f, true, 1, farm.Fertility),
    };

    /// <summary>
    /// The building's stats path shows yield, growth rate and the original Workers line; biome match affects yield, while drought and power slow growth.
    /// </summary>
    [Fact]
    public void Stats_distinguish_harvest_yield_from_growth_rate_and_keep_workers()
    {
        using var session = new WorldSession(Vector3.Zero, (-10, -10, 10, 10), (_, _) => 0);
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position);
        var stats = new List<PlayerInterface.HudLine>();
        var extended = new List<PlayerInterface.HudLine>();
        string status = "";
        bool bad = false;
        ui.Production.AddMachineLines(Machine(Growing with { Status = FarmStatus.NoWater }), stats, extended, ref status, ref bad);

        Assert.Equal(new[] { "Crop Yield", "Growth Rate", "Workers:" }, stats.Select(r => r.Label));
        Assert.Equal(new[] { "20%", "10%", "-" }, stats.Select(r => r.Value));
        Assert.Equal("No water", status);
        Assert.True(bad);
        Assert.Equal(PlayerInterface.BadColour, stats[0].ValueColour);
        Assert.Contains(("Environment:", "40%"), stats[0].TooltipRows!);
        Assert.Contains(("Farmers Skill:", "50%"), stats[0].TooltipRows!);
        Assert.Contains(("Actual Growth Time:", "80 hrs"), stats[1].TooltipRows!);
        Assert.Equal("No workers required until the crop is ready to harvest.", stats[2].Tooltip);

        stats.Clear();
        ui.Production.AddMachineLines(Machine(Growing with { Growth = 1.5f, Status = FarmStatus.Harvest }),
            stats, extended, ref status, ref bad);
        Assert.Equal("1/2", stats.Single(r => r.Label == "Workers:").Value);
    }

    /// <summary>
    /// Empty staffing assumes full harvest skill; zero drought growth displays a dash for its undefined growth time.
    /// </summary>
    [Fact]
    public void Unstaffed_dry_hydroponics_show_environment_yield_and_no_growth_time()
    {
        var farm = Growing with { Status = FarmStatus.NoWater, FarmerSkill = 1, Data = Crop with { DroughtMultiplier = 0 } };
        var machine = Machine(farm) with { Operators = 0, Hydroponic = true };
        var rows = new List<PlayerInterface.HudLine>();
        FarmPanel.AddStats(machine, rows);

        Assert.Equal("40%", rows[0].Value);
        Assert.Contains(("Farmers Skill:", "--"), rows[0].TooltipRows!);
        Assert.Equal("0%", rows[1].Value);
        Assert.Contains(("Actual Growth Time:", "-"), rows[1].TooltipRows!);
        Assert.DoesNotContain(rows[1].TooltipRows!, r => r.Key == "Environment Effect:");
    }

    /// <summary>
    /// Growing is relative to the planting start, plant health is relative to the clearing threshold, and harvested plants disappear from the count.
    /// </summary>
    [Fact]
    public void Crop_bars_show_relative_growth_health_and_remaining_plants()
    {
        var rows = new List<PlayerInterface.MedicalRow>();
        FarmPanel.AddRows(Growing, rows);

        Assert.Equal(new[] { "Growing", "Plant Condition", "Number of Plants" }, rows.Select(r => r.Name));
        Assert.Equal(new[] { "50%", "75%", "22" }, rows.Select(r => r.Value));
        Assert.Equal(0.5f, rows[0].Yellow, 5);
        Assert.Equal(0.75f, rows[1].Yellow, 5);
        Assert.Equal(0.88f, rows[2].Yellow, 5);
    }

    /// <summary>
    /// Harvest time includes the remaining ripe period and the healthy part of decay; water controls its bar independently of the text.
    /// </summary>
    [Theory]
    [InlineData(1.5f, 0.1f, true, "27 hrs")]
    [InlineData(2.2f, 0.1f, true, "2 hrs")]
    [InlineData(2.2f, 0.1f, false, "2 hrs")]
    [InlineData(2.2f, 0.4f, true, "")]
    [InlineData(1, 0, true, "")]
    public void Harvest_deadline_matches_the_ripe_and_decay_periods(float growth, float death, bool watered, string expected)
    {
        var rows = new List<PlayerInterface.MedicalRow>();
        FarmPanel.AddRows(Growing with { Growth = growth, Death = death, Watered = watered }, rows);
        if (expected.Length == 0)
        {
            Assert.DoesNotContain(rows, r => r.Name == "Time left to harvest");
            return;
        }

        Assert.Equal(expected, rows[3].Value);
        Assert.Equal(watered, rows[3].Yellow > 0);
    }

    /// <summary>
    /// Snapshot publication carries planting, crop data, average farmer skill and actual water level, rather than guessing from the harvest status.
    /// </summary>
    [Fact]
    public void Snapshot_publishes_the_farm_panel_inputs()
    {
        var machine = new Machine
        {
            Site = "field", Data = new MachineData { Id = "farm", Name = "Farm", Farm = Crop },
            Farm = new FarmState(25) { Start = -0.2f, Growth = 1.5f, Status = FarmStatus.Harvest },
            Inputs = [new InputSlot(new RecipeInput("water", 5, 1))], Operators = 2, FarmerSkillTotal = 0.6f,
        };
        var items = new ItemFactory(new GameDatabase(), GameConstants.Default);
        var farm = MachineSnapshot.Of(machine, items).Farm!.Value;
        Assert.Same(Crop, farm.Data);
        Assert.Equal(-0.2f, farm.Start);
        Assert.Equal(0.3f, farm.FarmerSkill, 5);
        Assert.False(farm.Watered);

        machine.Operators = 0;
        machine.Inputs[0].Level = 0.5f;
        farm = MachineSnapshot.Of(machine, items).Farm!.Value;
        Assert.Equal(1, farm.FarmerSkill);
        Assert.True(farm.Watered);
    }
}

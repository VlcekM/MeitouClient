using System.Globalization;
using Meitou.Simulation.Production;

namespace Meitou.Game.Ui;

/// <summary>
/// The farm's stats and crop bars (docs/game/ui-screens.md, Farm panel): display values derived from its published crop and operators.
/// </summary>
internal static class FarmPanel
{
    /// <summary>
    /// Crop Yield and Growth Rate, in place of a mine's richness line; Workers and hydroponic Efficiency follow in ProductionWindows.
    /// </summary>
    internal static void AddStats(MachineSnapshot machine, List<PlayerInterface.HudLine> rows)
    {
        if (machine.Farm is not { Data: { } data } farm) return;

        AddYield(machine, farm, rows);
        AddGrowth(machine, farm, data.GrowthTime, data.DroughtMultiplier, rows);
    }

    /// <summary>
    /// The average farmer's clamped harvest skill factor times the biome match and fertility; no farmer assumes a full skill factor.
    /// </summary>
    static void AddYield(MachineSnapshot machine, FarmView farm, List<PlayerInterface.HudLine> rows)
    {
        float environment = farm.Fertility * farm.CropMatch;
        float skill = Math.Clamp(0.5f + 1.5f * farm.FarmerSkill, 0, 1);
        float yield = skill * environment;
        var yieldRows = new List<(string, string)>
        {
            (Loc.Tr("Environment:"), Percent(environment)),
            (Loc.Tr("Farmers Skill:"), machine.Operators == 0 ? "--" : Percent(skill)),
        };
        string yieldTip = Loc.Tr("This is the percentage yield of the farm. A lower number means that you get less crops from a harvest. "
            + "Skillful farmers are important.");
        if (yield < 0.15f)
        {
            yieldTip += "\n" + Loc.Tr("WARNING! Crop yield is too low to be productive, probably the crop is not suitable for this area.");
        }

        yieldRows.Add((yieldTip, ""));
        rows.Add(new("Crop Yield", Percent(yield), PlayerInterface.MainColour,
            yield < 0.3f ? PlayerInterface.BadColour : null, TooltipRows: yieldRows));
    }

    /// <summary>
    /// Growth uses fertility, the powered building's health and power factor, and its drought multiplier; crop match does not slow growth.
    /// </summary>
    static void AddGrowth(MachineSnapshot machine, FarmView farm, float growthTime, float droughtMultiplier, List<PlayerInterface.HudLine> rows)
    {
        float power = machine.Efficiency is { Consumer: true } e ? e.Health * e.Power : 1;
        float drought = farm.Status == FarmStatus.NoWater ? droughtMultiplier : 1;
        float rate = farm.Fertility * power * drought;
        var growthRows = new List<(string, string)> { (Loc.Tr("Normal Growth Time:"), Hours((int)growthTime)) };
        if (!machine.Hydroponic)
            growthRows.Add((Loc.Tr("Environment Effect:"), (farm.Fertility < 0.3f ? ProductionWindows.BadTag : "") + Percent(farm.Fertility)));
        if (machine.Efficiency is { Consumer: true })
            growthRows.Add((Loc.Tr("Power:"), power >= 1 ? ProductionWindows.GoodTag + Loc.Tr("OK")
                : ProductionWindows.BadTag + "-" + Percent(1 - power)));
        if (farm.Status == FarmStatus.NoWater)
            growthRows.Add((Loc.Tr("No Water:"), ProductionWindows.BadTag + Percent(drought - 1)));

        growthRows.Add((Loc.Tr("Actual Growth Time:"), rate == 0 ? "-" : Hours((int)(growthTime / rate))));
        rows.Add(new("Growth Rate", Percent(rate), PlayerInterface.MainColour,
            rate < 0.3f ? PlayerInterface.BadColour : null, TooltipRows: growthRows));
    }

    /// <summary>
    /// Section 4 after building Condition: Growing, Plant Condition, Number of Plants and Time left to harvest.
    /// </summary>
    internal static void AddRows(FarmView farm, List<PlayerInterface.MedicalRow> rows)
    {
        if (farm.Data is not { } data) return;

        float growing = Math.Clamp((farm.Growth - farm.Start) / (1 - farm.Start), -0.5f, 1);
        string progress = farm.Fertility <= 0.01f ? "--" : PercentRounded(growing);
        rows.Add(Bar("Growing", growing, progress,
            "Once the plants have grown 100% you can begin to harvest them. Until then all you have to do is make sure the farm has water."));

        float condition = data.DeathThreshold > 0 ? Math.Clamp(1 - farm.Death / data.DeathThreshold, 0, 1) : 0;
        rows.Add(Bar("Plant Condition", condition, Percent(condition),
            "The health of the plants, it will start to degenerate once they are fully grown, or if they run out of water. "
            + "If it reaches 0 then the dead plants will have to be cleared and re-planted."));

        int remaining = farm.Plants - farm.Harvested;
        rows.Add(Bar("Number of Plants", farm.Plants > 0 ? (float)remaining / farm.Plants : 0,
            remaining.ToString(CultureInfo.InvariantCulture),
            "The number of plants remaining. This number will decrease when you harvest them, or if some pest starts eating them."));

        AddHarvestRow(farm, rows);
    }

    /// <summary>
    /// A harvest deadline exists strictly after growth 1 and before the clearing threshold; a dry crop's time remains visible with an empty bar.
    /// </summary>
    static void AddHarvestRow(FarmView farm, List<PlayerInterface.MedicalRow> rows)
    {
        var data = farm.Data!;
        if (farm.Growth <= 1 || farm.Death >= data.DeathThreshold) return;

        float hours = data.HarvestTime * Math.Max(2 - farm.Growth, 0)
            + data.DeathTime * (data.DeathThreshold - (farm.Growth >= 2 ? farm.Death : 0));
        float window = data.HarvestTime + data.DeathTime * data.DeathThreshold;
        bool watered = data.DroughtDeathTime <= 0 || farm.Watered;
        rows.Add(Bar("Time left to harvest", watered && window > 0 ? hours / window : 0, Hours((int)MathF.Floor(hours)),
            "Time left to harvest all plants before they go bad."));
    }

    /// <summary>
    /// A crop row uses the medical panel's yellow bar and translated caption and tooltip.
    /// </summary>
    static PlayerInterface.MedicalRow Bar(string name, float fill, string value, string tooltip) =>
        new(Loc.Tr(name), 0, fill, 0, value, "", false, Loc.Tr(tooltip));

    /// <summary>
    /// The original's whole percentages truncate toward zero; only Growing rounds to the nearest integer.
    /// </summary>
    static string Percent(float fraction) => ((int)(fraction * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Growing rounds with floor(x + 0.5), including negative saved growth.
    /// </summary>
    static string PercentRounded(float fraction) => MathF.Floor(fraction * 100 + 0.5f).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// A whole number of game hours with the original's singular/plural suffix.
    /// </summary>
    static string Hours(int hours) => Loc.Plural("{1,num} hr", "{1,num} hrs", hours, hours);
}

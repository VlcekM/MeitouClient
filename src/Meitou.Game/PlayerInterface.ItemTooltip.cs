using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Combat;
using Meitou.Game.Ui;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    /// <summary>The ARMOUR record of an item, null for anything else (the host sets it).</summary>
    public Func<string, ArmourData?>? ArmourOf { get; set; }
    /// <summary>An item's Value and Sell value with no trader, and the Avg. Price of a trade item (null for others); the host sets it. Null: no prices for this item.</summary>
    public Func<TooltipRuntime.ItemTip, (int Value, int Sell, int? Average)?>? PricesOf { get; set; }
    /// <summary>The player faction's name (what the dialogue token PLAYERFACTION expands to); the host sets it.</summary>
    public Func<string>? PlayerFactionName { get; set; }
    /// <summary>The name of a record by string id (a body part of an armour's coverage); the host sets it.</summary>
    public Func<string, string?>? RecordName { get; set; }

    /// <summary>The tone of a grade's name (<c>ItemGrade0</c> to <c>ItemGrade4</c> of kenshi_colours.xml; Specialist and Masterwork share the last).</summary>
    internal static TooltipTone GradeTone(int grade) => grade switch { 0 => TooltipTone.Grade0, 1 => TooltipTone.Grade1, 2 => TooltipTone.Grade2, 3 => TooltipTone.Grade3, _ => TooltipTone.Grade4 };
    /// <summary>A weapon's grade colour by its level (<c>FUN_1401625c0</c>, <b>Observed</b>): below 16, 36, 51, 66, up to 80, above 80.</summary>
    internal static int WeaponGrade(int level) => level < 16 ? 0 : level < 36 ? 1 : level < 51 ? 2 : level < 66 ? 3 : level <= 80 ? 4 : 5;

    /// <summary>An armour line (<see cref="ArmourTooltip"/>, docs/game/ui-screens.md "Item tooltip") as a tooltip row: a statistic's or effect's label in the title colour, a grade in its grade's colour, an effect's value in the bad colour below neutral and the good colour above.</summary>
    internal static TooltipLine ArmourRow(ArmourTooltip.Line line) => line.Kind switch
    {
        ArmourTooltip.LineKind.Grade => new(line.Label, "", GradeTone(line.Grade)),
        ArmourTooltip.LineKind.Stat => new(line.Label, line.Value, TooltipTone.Title, TooltipTone.Ink),
        ArmourTooltip.LineKind.Effect => new(line.Label, line.Value, TooltipTone.Title, line.Grade < 0 ? TooltipTone.Bad : TooltipTone.Good),
        _ => new(line.Label, line.Value, TooltipTone.Main, TooltipTone.Main),
    };
}

using System.Globalization;
using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The armour lines of an item tooltip (docs/game/ui-screens.md "Item tooltip"): the grade, the class and material, the resistances, the coverage and
/// the skill effects. The numbers are the ones the hit code uses (<see cref="ArmourPiece.Create"/>), so what the tooltip says is what the armour
/// does.
/// </summary>
public static class ArmourTooltip
{
    /// <summary>
    /// The armour grade names by grade 0 to 5 (the six crafting tiers of <c>CraftBench.ArmourQualities</c>, 5 to 95 per cent).
    /// </summary>
    public static readonly string[] GradeNames = ["Prototype", "Shoddy", "Standard", "High", "Specialist", "Masterwork"];

    /// <summary>
    /// The quality of each grade 0 to 5 (per cent): where <see cref="GradeOf"/> starts each grade.
    /// </summary>
    static readonly int[] GradeQualities = [5, 20, 40, 60, 80, 95];

    /// <summary>
    /// The order of parts of equal coverage as the original prints them; the list runs from the highest coverage down (<b>Observed</b> on two
    /// armours: the Samurai Armour prints Chest 100, then Stomach, Right Arm, Left Arm at 85; the Samurai Legplates Right Leg 100, Left Leg 100,
    /// Stomach 50, whose records list them in other orders). Parts not named here follow in record order.
    /// </summary>
    static readonly string[] PartOrder = ["Head", "Chest", "Stomach", "Right Arm", "Left Arm", "Right Leg", "Left Leg"];

    /// <summary>
    /// The grade of a quality (0 to 100): the highest grade whose quality it reaches; below the first grade's quality it is grade 0. The cut-offs
    /// between grades are <b>Unknown</b> (items made or found have exactly the six qualities).
    /// </summary>
    public static int GradeOf(float quality)
    {
        int grade = 0;
        for (int i = 1; i < GradeQualities.Length; i++) if (quality >= GradeQualities[i]) grade = i;
        return grade;
    }

    /// <summary>
    /// How a line is shown: a heading as is, a statistic with its label in the accent colour, a grade in the colour of its grade, a skill effect with
    /// its label in the accent colour and its value in the bad colour below neutral and the good colour above.
    /// </summary>
    public enum LineKind
    {
        /// <summary>
        /// A heading, shown as is.
        /// </summary>
        Plain,
        /// <summary>
        /// A statistic: its label in the accent colour.
        /// </summary>
        Stat,
        /// <summary>
        /// A grade: in the colour of its grade.
        /// </summary>
        Grade,
        /// <summary>
        /// A skill effect: its label in the accent colour, its value in the bad colour below neutral and the good colour above.
        /// </summary>
        Effect
    }

    /// <summary>
    /// One tooltip line: a label and a value (empty for a heading); <paramref name="Grade"/> is the grade of a <see cref="LineKind.Grade"/> line, and
    /// -1 or 1 (a penalty or a bonus) for an <see cref="LineKind.Effect"/> line.
    /// </summary>
    public readonly record struct Line(string Label, string Value, LineKind Kind = LineKind.Plain, int Grade = 0);

    /// <summary>
    /// <paramref name="v"/> in the invariant culture with <paramref name="format"/> (up to two decimals by default).
    /// </summary>
    static string Number(float v, string format = "0.##") => v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// A resistance as the original prints it: whole numbers, truncated (<b>Observed</b>: 61.75 % reads 61 %, 73.56 % reads 73 %, 85.5 pts reads 85
    /// pts).
    /// </summary>
    static string Percent(float fraction) => ((int)(fraction * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The lines above the weight: grade, class and material, resistances. <paramref name="quality"/> is the item's <c>level</c> (0 to 100), the
    /// grade's quality (95 for Masterwork): a saved item has <c>quality</c> 100 whatever its grade, and the original's numbers follow the level
    /// (<b>Observed</b>: the Samurai Legplates, level 95, resistances of 0.95).
    /// </summary>
    public static IReadOnlyList<Line> Head(ArmourData a, float quality)
    {
        // level x 0.01 in single precision, as the original (80 x 0.01 is just below 0.8: 71 pts, not 72)
        var piece = ArmourPiece.Create(a, ArmourPiece.QualityOf(a, quality), ArmourPiece.Graded(a, quality));
        int grade = GradeOf(quality);
        string material = a.Material == ArmourType.MetalPlate ? "Metal Plate" : a.Material.ToString();
        var lines = new List<Line>
        {
            new("[" + Loc.Tr("{1} Grade", Loc.Tr(GradeNames[grade])) + "]", "", LineKind.Grade, grade),
            new($"[{a.Class} Armour Class]", $"[{material}]"),
            new("-Blunt resistance", Percent(piece.BluntDefence), LineKind.Stat),
            new("-Cut resistance", Percent(piece.CutDefence), LineKind.Stat),
            new("-Harpoon resistance", ((int)piece.PierceDefence).ToString(CultureInfo.InvariantCulture) + " pts", LineKind.Stat),
        };

        // The efficiency line only appears below 100 per cent (<b>Observed</b>: the wiki's vanilla Legplates print 90 %, the same item with cut into
        // stun 0 prints none).
        if (piece.CutIntoStun > 0) lines.Insert(4, new("-Cut resistance efficiency", Percent(1 - piece.CutIntoStun), LineKind.Stat));
        return lines;
    }

    /// <summary>
    /// The lines below the price: the coverage by part and the skill effects that are not neutral (<b>Observed</b>, docs/game/ui-screens.md "Item
    /// tooltip"; checked against the six grades of the Samurai Armour and Legplates). Stealth effect is the piece's stealth factor
    /// (<see cref="ArmourPiece.StealthOf"/>); Athletics effect is the record's <c>athletics mult</c> x the class table's lerp(t, lo, hi) (medium 0.85
    /// to 1, heavy 0.8 to 0.95, forced to 1 for slots 3, 5 and 8) and Combat speed effect likewise (heavy 0.9 to 1, forced to 1 for a hat). The
    /// dexterity, defence, perception and ranged bonus lines are not shown: their formulas and labels are <b>Unknown</b>.
    /// </summary>
    public static IReadOnlyList<Line> Tail(ArmourData a, Func<string, string?> partName, float quality)
    {
        float t = ArmourPiece.QualityOf(a, quality) + a.LevelBonus * 0.01f;   // level 0: full strength, t = 1 (Observed)
        var lines = new List<Line>();
        var parts = a.Coverage.Select(c => (Name: partName(c.PartStringId) ?? c.PartStringId, c.Percent)).ToList();
        if (parts.Count > 0)
        {
            lines.Add(new("[Coverage]", ""));
            foreach (var (name, percent) in parts.OrderByDescending(p => p.Percent)
                         .ThenBy(p => Array.IndexOf(PartOrder, p.Name) is var i and >= 0 ? i : PartOrder.Length))
                lines.Add(new("-" + name, percent + "%", LineKind.Stat));
        }
        var skills = new List<Line>();

        // A row is skipped at exactly 1 (the Athletics row also between 0.97 and 1); the colour follows the side of neutral (the original's red for a
        // penalty).
        void Multiplier(string label, float mult, bool hideNearOne = false)
        {
            if (mult == 1 || hideNearOne && mult is >= 0.97f and <= 1f) return;
            skills.Add(new("-" + label, Number(mult, "0.00") + "x", LineKind.Effect, mult < 1 ? -1 : 1));
        }
        void Bonus(string label, int bonus)
        {
            if (bonus != 0) skills.Add(new("-" + label, bonus.ToString(CultureInfo.InvariantCulture), LineKind.Effect, bonus < 0 ? -1 : 1));
        }
        Multiplier("Crossbows effect", a.RangedSkillMult);
        Multiplier("Precision shooting effect", a.RangedSkillMult);
        Multiplier("Stealth effect", ArmourPiece.StealthOf(a, t, ArmourPiece.Graded(a, quality)));
        Multiplier("Dodge skill effect", a.DodgeMult);
        Multiplier("Athletics effect", ArmourPiece.AthleticsOf(a, t), hideNearOne: true);

        // The class table's combat speed column (heavy 0.9 to 1, else 1); a hat is forced to 1.
        Multiplier("Combat speed effect", ArmourPiece.CombatSpeedOf(a, t));
        Multiplier("Attack damage effect", a.DamageOutputMult);
        Multiplier("Assassination skill", a.AssassinationMult);
        Bonus("Melee attack bonus", a.CombatAttackBonus);
        Bonus("Martial arts bonus", a.UnarmedBonus);
        if (skills.Count > 0)
        {
            lines.Add(new("[Skills]", ""));
            lines.AddRange(skills);
        }
        return lines;
    }
}
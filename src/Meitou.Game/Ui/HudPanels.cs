using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Editor;
using Meitou.Game.Host;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Weather;

namespace Meitou.Game.Ui;

/// <summary>
/// The HUD's text panels as the original fills them (docs/game/ui-screens.md section 4): the name bar, the state lines, the stats lines, the medical
/// header and rows. The original builds a list of lines inside each empty text widget, a label from 3 % of its width and a value right-aligned to 96
/// %, one row per line.
/// </summary>
sealed class HudPanels(PlayerInterface player)
{
    /// <summary>
    /// The goal line for a character the AI is not running (the player's orders, a fight, a job): what its movement task is, else Aimless.
    /// </summary>
    static string TaskText(CharacterTask task) => task switch
    {
        CharacterTask.GoTo => Loc.Tr("Moving"),
        CharacterTask.Follow => Loc.Tr("Following"),
        CharacterTask.Attack => Loc.Tr("Attacking"),
        CharacterTask.Treat => Loc.Tr("Giving first aid"),
        CharacterTask.Rest => Loc.Tr("Resting"),
        CharacterTask.PickUp => Loc.Tr("Picking up"),
        CharacterTask.PutDown => Loc.Tr("Putting down"),
        CharacterTask.Loot => Loc.Tr("Looting"),
        CharacterTask.StealthKo => Loc.Tr("Sneak attack"),
        CharacterTask.Build => Loc.Tr("Building"),
        CharacterTask.Dismantle => Loc.Tr("Dismantling"),
        CharacterTask.DeliverMaterials => Loc.Tr("Delivering materials"),
        CharacterTask.FetchMaterials => Loc.Tr("Collecting resource"),
        CharacterTask.Operate => Loc.Tr("Operating machine"),
        CharacterTask.Haul => Loc.Tr("Hauling"),
        CharacterTask.Prospect => Loc.Tr("Prospecting"),
        CharacterTask.Sleep => Loc.Tr("Sleeping"),
        _ => Loc.Tr("Aimless"),
    };

    /// <summary>
    /// The weather notice of a hurt player character (the medical object's short and long status strings, <c>FUN_1406472c0</c> and its siblings,
    /// docs/formats/weather.md): the short word for the State line and the long sentence for its tooltip. A protection <paramref name="p"/> below
    /// 0.02 is none, from 0.99 total; the percentage is rounded (<b>engine choice</b>).
    /// </summary>
    internal static (string Short, string Long) WeatherText(WeatherNotice notice, float p)
    {
        string percent = Loc.Tr("{1,num}%", MathF.Round(Math.Clamp(p, 0, 1) * 100));

        switch (notice)
        {
            case WeatherNotice.AcidWater:
                return (Loc.Tr("Acid!"), Loc.Tr("You are currently submersed in acid, get out of there as soon as you can."));
            case WeatherNotice.AcidFeet:
                return (Loc.Tr("Feet Burning!"), Loc.Tr("The ground here is acidic and burns your feet, find some footwear!"));
        }

        string head = notice switch
        {
            WeatherNotice.AcidRain => Loc.Tr("Acid Rain ({1} protection)", percent),
            WeatherNotice.Dust => Loc.Tr("Dust Storm ({1} protection)", percent),
            WeatherNotice.Burning => Loc.Tr("Burning ({1} protection)", percent),
            _ => Loc.Tr("Poison Gas ({1} protection)", percent),
        };
        string text = notice switch
        {
            WeatherNotice.AcidRain => p < 0.02f
                ? Loc.Tr("It is currently raining acid, and you aren't wearing anything to protect your skin.  Best to find shelter soon.")
                : p < 0.99f ? Loc.Tr("It is currently raining acid, your gear is providing you with {1} protection.", percent)
                : Loc.Tr("It is currently raining acid, but your gear is providing you with total protection."),
            WeatherNotice.Dust => p < 0.02f ? Loc.Tr("You are in a dust storm, it affects your accuracy in combat.")
                : p < 0.99f ? Loc.Tr("You are in a dust storm, your gear is providing you with {1} protection.", percent)
                : Loc.Tr("Your gear is protecting you from the storm."),
            WeatherNotice.Burning => p < 0.02f ? Loc.Tr("You are currently on fire.")
                : p < 0.99f ? Loc.Tr("You are currently on fire, your gear is providing you with {1} protection.", percent)
                : Loc.Tr("Your gear is protecting you from being horribly burnt."),
            _ => p < 0.02f ? Loc.Tr("You are currently breathing in poison gas.")
                : p < 0.99f ? Loc.Tr("You are currently breathing in poison gas, your gear is providing you with {1} protection.", percent)
                : Loc.Tr("Your gear is protecting you from breathing in any poisons."),
        };
        return (head, text);
    }

    /// <summary>
    /// The state lines (State, Goal, Encumbrance and, alive, the Current Skill line) of the selected character. The State line is the original's
    /// first match (<c>FUN_1405d4260</c>, docs/game/ui-screens.md section 4): Dead; caged, the cage lock's "Locked (lvl: N)" or "(Unlocked...)" (no
    /// State line in a cage without a lock); Unconscious / Rebooting; Sleeping in a bed; Defensive; Normal; each with its tooltip; the ranged
    /// controller's line comes between Sleeping and Defensive. The weather notice replaces it while it lasts.
    /// </summary>
    static PlayerInterface.HudLine[] StateLines(CharacterSnapshot lead, SpeedUnit unit)
    {
        var body = lead.Body;
        string? state, colour = null, tooltip = null;

        if (body is { Dead: true })
            (state, colour, tooltip) = (Loc.Tr("Dead"), PlayerInterface.BadColour, Loc.Tr("This guy is dead and gone."));
        else if (lead.Prison is { } prison)
        {
            (state, colour, tooltip) = prison.LockLevel < 0 ? (null, null, null)
                : prison.Locked ? (Loc.Tr("Locked (lvl: {1,num})", prison.LockLevel), PlayerInterface.BadColour,
                    Loc.Tr("You are locked in a cage.\nThe lock has a difficulty level of {1,num}.", prison.LockLevel))
                : (Loc.Tr("(Unlocked...)"), PlayerInterface.GoodColour,
                    Loc.Tr("The cage you are in is UNLOCKED.\nNobody knows this but you, you can escape any time you want."));
        }
        else if (body is { Unconscious: true })
        {
            (state, colour, tooltip) = (Loc.Tr(lead.Hud?.Robot == true ? "Rebooting" : "Unconscious"), PlayerInterface.BadColour,
                Loc.Tr("This guy is unconscious and probably needs medical attention and rest."));
        }
        else if (lead.InBed) (state, tooltip) = (Loc.Tr("Sleeping"), Loc.Tr("Sleeping..."));
        else if (lead.RangedLine is { } ranged)
        {
            // FUN_140438640: "Aiming..." / "Reloading..." and " [loaded/shots]"; an unpowered gun's "No Power!" (Bad) replaces the word.
            string loaded = ranged.Loaded.ToString(CultureInfo.InvariantCulture), shots = ranged.Shots.ToString(CultureInfo.InvariantCulture);
            (state, colour) = (Loc.Tr(ranged.NoPower ? "No Power!" : ranged.Reloading ? "Reloading..." : "Aiming...") + $" [{loaded}/{shots}]",
                ranged.NoPower ? PlayerInterface.BadColour : null);
        }
        else if ((lead.Orders & CharacterOrders.Block) != 0)
        {
            (state, tooltip) = (Loc.Tr("Defensive"),
                Loc.Tr("In defensive combat mode the character will not even try to attack, focusing entirely on defense for a +20 skill bonus."));
        }
        else state = Loc.Tr("Normal");

        if (body is not { Dead: true } && lead.Prison is null && lead.WeatherNotice != WeatherNotice.None)
        {
            (state, tooltip) = WeatherText(lead.WeatherNotice, lead.WeatherNoticeProtection);
            colour = lead.WeatherNoticeProtection > 0.9f && lead.WeatherNotice is not (WeatherNotice.AcidWater or WeatherNotice.AcidFeet)
                ? PlayerInterface.GoodColour
                : PlayerInterface.BadColour;
        }

        PlayerInterface.HudLine[] rest =
            [new(Loc.Tr("Goal:"), lead.Activity ?? TaskText(lead.Task), Tooltip: Loc.Tr("Current goal of this character.")), EncumbranceLine(lead)];
        PlayerInterface.HudLine[] lines = state is null ? rest : [new(Loc.Tr("State:"), state, ValueColour: colour, Tooltip: tooltip), .. rest];
        return body is { Dead: true } || lead.Hud?.Skill is not { } skill ? lines : [.. lines, SkillLine(skill, unit)];
    }

    /// <summary>
    /// The Encumbrance line: (1 - f) x 100 % of the encumbrance factor f, Bad from 50 %, with the original's rows (<c>FUN_1405d4260</c> panel 3,
    /// docs/game/ui-screens.md): the percent with the burden word, Athletics XP, Strength XP (when the extended stats are known), Run Speed Penalty
    /// and Stealth Penalty (both the same percent).
    /// </summary>
    static PlayerInterface.HudLine EncumbranceLine(CharacterSnapshot lead)
    {
        float factor = Math.Clamp(lead.Hud?.Encumbrance ?? 1, 0, 1);
        int load = (int)((1 - factor) * 100);
        string percent = load.ToString(CultureInfo.InvariantCulture) + "%";
        string word = lead.Body is { Dead: true } ? Loc.Tr("Dead") : InventoryLayout.BurdenText(factor).Text[1..];
        var rows = new List<(string, string)> { (TipTitle + Loc.Tr("Encumbrance:"), (load >= 50 ? TipBad : TipTitle) + percent + " (" + word + ")") };

        if (lead.Hud?.Extended is { } e)
        {
            rows.Add((Loc.Tr("Athletics XP:"), ((int)(e.AthleticsXp * 100)).ToString(CultureInfo.InvariantCulture) + "%"));
            rows.Add((Loc.Tr("Strength XP:"), ((int)(e.StrengthXp * 100)).ToString(CultureInfo.InvariantCulture) + "%"));
        }

        string penalty = (load >= 50 ? TipBad : "") + percent;
        rows.Add((Loc.Tr("Run Speed Penalty:"), penalty));
        rows.Add((Loc.Tr("Stealth Penalty:"), penalty));
        return new(Loc.Tr("Encumbrance:"), percent, ValueColour: load >= 50 ? PlayerInterface.BadColour : null, TooltipRows: rows);
    }

    /// <summary>
    /// The state panel's "Current Skill" line: the job's skill by the editor's stat names (<c>FUN_140888440</c>) and its level
    /// (<c>{1,num=fix,p=0}</c>), Bad when penalised; a fallback keeps its own label; Swimming shows the swim speed like the run speed.
    /// </summary>
    static PlayerInterface.HudLine SkillLine(CurrentSkillView skill, SpeedUnit unit)
    {
        string label = skill.Label is { } own ? Loc.Tr(own) : EditorRaceStats.EditorStatName(skill.Stat);
        string? colour = skill.Penalised ? PlayerInterface.BadColour : null;
        string value = skill.Speed ? SpeedText(skill.Value, unit) : skill.Accuracy is not null ? Degrees(skill.Value) : Num(skill.Value);

        if (skill.Base is { } trained && (int)skill.Value != (int)trained)
        {
            // FUN_140889710: the value and its change from the trained level, the whole text Bad when lower and Good when higher.
            int change = (int)skill.Value - (int)trained;
            value = change < 0 ? $"{(int)skill.Value} ({change})" : $"{(int)skill.Value} (+{change})";
            colour = change < 0 ? PlayerInterface.BadColour : PlayerInterface.GoodColour;
        }

        (string, string)[]? tip = skill.Terms is { } terms ? SkillTip(label, terms) : skill.Accuracy is { } a ? AccuracyTip(a)
            : skill.Damage is { } damage ? [(Loc.Tr("Damage:"), Num(damage))] : skill.Stealth is { } s ? StealthTip(s, unit)
            : skill.Athletics is { } at ? AthleticsTip(at, unit) : skill.Swim is { } sw ? SwimTip(sw, unit) : null;
        return new(label, value, ValueColour: colour, TooltipRows: tip);
    }

    /// <summary>
    /// The exe's colour tags the stat tooltips carry: Bad, Good, Main and Title.
    /// </summary>
    internal const string TipBad = "#59231a", TipGood = "#a8b774", TipMain = "#afa68b", TipTitle = "#492620";

    /// <summary>
    /// <paramref name="v"/> truncated to a whole number.
    /// </summary>
    static string Num(float v) => ((int)v).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="v"/> in degrees with two decimals.
    /// </summary>
    static string Degrees(float v) => v.ToString("0.00", CultureInfo.InvariantCulture) + Loc.Tr(" degrees");

    /// <summary>
    /// A speed in km/h or mph (truncated).
    /// </summary>
    static string SpeedText(float unitsPerSecond, SpeedUnit unit) =>
        $"{(int)(unitsPerSecond * (unit == SpeedUnit.Kmh ? 0.36f : 0.22369362f))} {(unit == SpeedUnit.Kmh ? "km/h" : "mph")}";

    /// <summary>
    /// A penalty of <paramref name="percent"/> above 0 as Bad <c>-n%</c>.
    /// </summary>
    static string Minus(int percent) => TipBad + "-" + percent.ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// A signed change (<c>FUN_140889320</c>): <c>+n%</c> Good above 0, <c>-n%</c> Bad below.
    /// </summary>
    static string Signed(int percent) => SignedNumber(percent) + "%";

    /// <summary>
    /// <c>FUN_140889320</c> alone: <c>+n</c> Good above 0, <c>n</c> Bad otherwise, no unit.
    /// </summary>
    static string SignedNumber(int n) => (n > 0 ? TipGood + "+" : TipBad) + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The "Accuracy Error:" tooltip: the crossbow's own deviation, the skill and the resulting deviation (the last in Main).
    /// </summary>
    static (string, string)[] AccuracyTip(AccuracyTerms a) =>
        [(Loc.Tr("Weapon Accuracy:"), "0-" + Degrees(a.Weapon)), (Loc.Tr("Skill:"), MathF.Round(a.Skill).ToString(CultureInfo.InvariantCulture)),
            (TipMain + Loc.Tr("Resulting Accuracy Deviation:"), TipMain + Degrees(a.Deviation))];

    /// <summary>
    /// The Stealth tooltip (<c>FUN_14088f9a0</c>); crawling (+100%) skips the penalties.
    /// </summary>
    static (string, string)[] StealthTip(StealthTerms s, SpeedUnit unit)
    {
        var rows = new List<(string, string)> { (Loc.Tr("Stealth skill:"), Num(s.Skill)), (Loc.Tr("Max speed:"), SpeedText(s.MaxSpeed, unit)),
            (Loc.Tr("XP rate:"), Num(s.XpRate * 100) + "%") };
        if (s.Carrying) rows.Add((Loc.Tr("Carrying something:"), TipBad + "-25%"));

        if (s.Crawling) rows.Add((Loc.Tr("Crawling:"), TipGood + "+100%"));
        else
        {
            if (s.Encumbrance > 0) rows.Add((Loc.Tr("Encumbrance:"), Minus(s.Encumbrance)));
            if (s.Injuries < 0) rows.Add((Loc.Tr("Injuries:"), Signed(s.Injuries)));
            if (s.Gear != 0) rows.Add((Loc.Tr("Armour/equipment:"), Signed(s.Gear)));
            if (s.Robotics != 0) rows.Add((Loc.Tr("Robotics:"), Signed(s.Robotics)));
        }

        rows.Add((Loc.Tr("Total:"), Num(s.Total)));
        return [.. rows];
    }

    /// <summary>
    /// The Athletics tooltip (<c>FUN_14088e780</c>).
    /// </summary>
    static (string, string)[] AthleticsTip(AthleticsTerms a, SpeedUnit unit)
    {
        var rows = new List<(string, string)>
        {
            (Loc.Tr("Athletics Skill:"), Num(a.Skill)), (Loc.Tr("Max possible speed:"), SpeedText(a.MaxSpeed, unit)),
        };
        if (a.LegInjury > 0) rows.Add((Loc.Tr("Leg injury:"), Minus(a.LegInjury)));
        if (a.Encumbrance > 0) rows.Add((Loc.Tr("Encumbrance:"), Minus(a.Encumbrance)));

        // CharStats +0x18 on either side of 1, the sign from the factor (so +0% and -0% can show).
        if (a.Gear > 1) rows.Add((Loc.Tr("Armour/equipment:"), TipGood + "+" + Num((a.Gear - 1) * 100) + "%"));
        else if (a.Gear < 1) rows.Add((Loc.Tr("Armour/equipment:"), TipBad + "-" + Num((1 - a.Gear) * 100) + "%"));
        if (a.Robotics != 0) rows.Add((Loc.Tr("Robotics:"), Signed(a.Robotics)));

        rows.Add((TipMain + Loc.Tr("Total speed:"), SpeedText(a.TotalSpeed, unit)));
        return [.. rows];
    }

    /// <summary>
    /// The Swimming tooltip (the HUD update): penalties above 1 % in Bad, label and value.
    /// </summary>
    static (string, string)[] SwimTip(SwimTerms s, SpeedUnit unit)
    {
        var rows = new List<(string, string)> { (Loc.Tr("Skill:"), Num(s.Skill)), (Loc.Tr("Max Swim Speed:"), SpeedText(s.MaxSpeed, unit)) };

        void Penalty(string label, int percent)
        {
            if (percent > 1)
                rows.Add((TipBad + Loc.Tr(label), TipBad + percent.ToString(CultureInfo.InvariantCulture) + "%"));
        }

        Penalty("Injuries", s.Injuries);
        Penalty("Hunger", s.Hunger);
        Penalty("Encumbrance", s.Encumbrance);
        if (s.Robotics != 0) rows.Add((TipBad + Loc.Tr("Robotics"), Signed(-s.Robotics)));
        if (s.BrokenArms) rows.Add((TipBad + Loc.Tr("2 Broken Arms!"), TipBad + "-99%"));

        rows.Add((Loc.Tr("Current Swim Speed:"), SpeedText(s.Speed, unit)));
        return [.. rows];
    }

    /// <summary>
    /// A skill's tooltip (<c>FUN_140890b00</c>): "{skill} skill:" and the trained level; each penalty above 0 as Bad <c>-n%</c> (Injuries, Hunger,
    /// Weather, Working in darkness, Robotics; a Robotics bonus Good <c>+n%</c>, labelled "Limbs disabled:" without fitted robotics); the gear as a
    /// signed percentage; for strength the missing arm penalty; the total.
    /// </summary>
    static (string, string)[] SkillTip(string skill, SkillTerms t)
    {
        var rows = new List<(string, string)> { (Loc.Tr("{1} skill:", skill), ((int)t.Base).ToString(CultureInfo.InvariantCulture)) };

        void Penalty(string label, int percent)
        {
            if (percent > 0) rows.Add((Loc.Tr(label), Minus(percent)));
        }

        Penalty("Injuries:", t.Injuries);
        Penalty("Hunger:", t.Hunger);
        Penalty("Weather:", t.Weather);
        Penalty("Working in darkness:", t.Darkness);
        Penalty("Robotics:", t.Robotics);
        if (t.Robotics < 0) rows.Add((Loc.Tr(t.HasRobotics ? "Robotics:" : "Limbs disabled:"), Signed(-t.Robotics)));
        if (t.Gear != 0) rows.Add((Loc.Tr("Armour/equipment:"), Signed(-t.Gear)));
        if (t.ArmourBonus != 0) rows.Add((Loc.Tr("Armour/equipment:"), SignedNumber(t.ArmourBonus)));

        Penalty("Missing arm penalty:", t.MissingArm);
        rows.Add((Loc.Tr("Total:"), ((int)t.Total).ToString(CultureInfo.InvariantCulture)));
        return [.. rows];
    }

    /// <summary>
    /// The stats lines: the value the character fights with, then its change from the trained value in brackets, the whole value Bad when it is lower
    /// and Good when higher. The labels show upper case and in Main on the panel's dark side (<b>Observed</b> in the original; where the case comes
    /// from is <b>Unknown</b>).
    /// </summary>
    internal static PlayerInterface.HudLine[] StatsLines(CharacterSnapshot lead) => lead.Hud is not { } hud ? [] : [.. hud.Stats.Select(s =>
    {
        if (s.Text is { } word)
            return new PlayerInterface.HudLine(Loc.Tr(s.Label).ToUpperInvariant(), word, PlayerInterface.StatLabelColour, TooltipRows: s.Tip);

        int value = (int)s.Value, change = value - (int)s.Base;
        string text = change == 0 ? value.ToString(CultureInfo.InvariantCulture) : change < 0 ? $"{value} ({change})" : $"{value} (+{change})";
        return new PlayerInterface.HudLine(Loc.Tr(s.Label).ToUpperInvariant(), text, PlayerInterface.StatLabelColour,
            change < 0 ? PlayerInterface.BadColour : change > 0 ? PlayerInterface.GoodColour : null, TooltipRows: s.Tip);
    })];

    /// <summary>
    /// The extended lines (shown when the information panel is expanded; <c>FUN_140895350</c>, docs/game/ui-screens.md section 4): the XP rates, the
    /// KO point, the run speed in mph (units per second x 0.22369362; km/h x 0.36) with its change from the unhurt run value in brackets (Bad either
    /// way, <c>FUN_140888fb0</c>), and the hunger rate.
    /// </summary>
    internal static PlayerInterface.HudLine[] ExtendedLines(CharacterSnapshot lead, SpeedUnit unit = SpeedUnit.Mph)
    {
        if (lead.Hud?.Extended is not { } e) return [];

        static string Percent(float f) => ((int)(f * 100)).ToString(CultureInfo.InvariantCulture) + "%";

        float factor = unit == SpeedUnit.Kmh ? 0.36f : 0.22369362f;
        string suffix = unit == SpeedUnit.Kmh ? "km/h" : "mph";
        int speed = (int)MathF.Round(e.RunSpeed * factor), change = (int)MathF.Round((e.RunSpeed - e.RunValue) * factor);
        string run = change == 0 ? $"{speed} {suffix}" : change < 0 ? $"{speed} ({change}) {suffix}" : $"{speed} (+{change}) {suffix}";
        var tips = lead.Hud.ExtendedTips;
        return [new(Loc.Tr("Athletics XP:"), Percent(e.AthleticsXp), TooltipRows: tips?.Athletics),
            new(Loc.Tr("Strength XP:"), Percent(e.StrengthXp), TooltipRows: tips?.Strength),
            new(Loc.Tr("Toughness XP:"), ((int)e.ToughnessXp).ToString(CultureInfo.InvariantCulture) + "x", TooltipRows: tips?.Toughness),
            new(Loc.Tr("KO point:"), ((int)e.KoPoint).ToString(CultureInfo.InvariantCulture), Tooltip: TooltipRuntime.KoPointTip(e.KoPoint)),
            new(Loc.Tr("Run speed:"), run, ValueColour: change != 0 ? PlayerInterface.BadColour : null,
                TooltipRows: tips is null
                    ? null
                    : RunTip(e, tips, lead.Hud.Encumbrance, change != 0 ? ProductionWindows.BadTag + run : run, factor, suffix)),
            new(Loc.Tr("Hunger Rate:"), Percent(e.HungerRate), TooltipRows: tips?.Hunger)];
    }


    /// <summary>
    /// The run speed's tooltip (<c>FUN_14088e780</c>, then <c>FUN_140895350</c>'s rows): the stored athletics, the run value, each loss (legs,
    /// encumbrance), the gear's run factor, the fitted legs, the top speed, while sneaking its cap, and "Total:" with the line's own text. Speeds in
    /// the panel's unit (the original's mph); the speeds are truncated, the line's rounded.
    /// </summary>
    static List<(string Key, string Value)> RunTip(ExtendedStats e, ExtendedTips tips, float encumbrance, string total, float factor, string suffix)
    {
        string InUnit(float unitsPerSecond) => $"{Loc.Tr("{1,num}", (int)(unitsPerSecond * factor))} {suffix}";

        var rows = new List<(string, string)>
        {
            (Loc.Tr("Athletics Skill:"), Loc.Tr("{1,num}", (int)tips.StoredAthletics)), (Loc.Tr("Max possible speed:"), InUnit(e.RunValue)),
        };
        int legs = (int)((1 - tips.LegFactor) * 100), load = (int)((1 - Math.Clamp(encumbrance, 0, 1)) * 100);
        int robotics = (int)((tips.RoboticLegs - 1) * 100);
        if (legs > 0) rows.Add((Loc.Tr("Leg injury:"), ProductionWindows.BadTag + "-" + Loc.Tr("{1,num}%", legs)));
        if (load > 0) rows.Add((Loc.Tr("Encumbrance:"), ProductionWindows.BadTag + "-" + Loc.Tr("{1,num}%", load)));

        // CharStats +0x18 on either side of 1, as the Athletics tooltip (so +0% and -0% can show).
        if (tips.Gear > 1)
            rows.Add((Loc.Tr("Armour/equipment:"), ProductionWindows.GoodTag + "+" + Loc.Tr("{1,num}%", (int)((tips.Gear - 1) * 100))));
        else if (tips.Gear < 1)
            rows.Add((Loc.Tr("Armour/equipment:"), ProductionWindows.BadTag + "-" + Loc.Tr("{1,num}%", (int)((1 - tips.Gear) * 100))));
        if (robotics != 0)
        {
            string sign = robotics < 0 ? ProductionWindows.BadTag : ProductionWindows.GoodTag;
            rows.Add((Loc.Tr("Robotics:"), sign + Loc.Tr(robotics > 0 ? "+{1,num}" : "{1,num}", robotics) + "%"));
        }

        rows.Add(("#afa68b" + Loc.Tr("Total speed:"), InUnit(e.RunSpeed)));
        if (tips.SneakCap is { } cap)
            rows.Add((Loc.Tr("Stealth mode:"), ProductionWindows.BadTag + Loc.Tr("max:") + " " + Loc.Tr("{1,num}", (int)(cap * factor))));

        rows.Add((Loc.Tr("Total:"), total));
        return rows;
    }

    /// <summary>
    /// The parts' severity the health word reads (<c>FUN_14088a8f0</c>'s part loop, docs/game/ui-screens.md "Health word choice"; h, d, s, m and the
    /// KO point as in <see cref="TooltipRuntime.PartTip"/>): 3 a vital part under 0 and below the KO point, 2 any part under 0 (h + d) that is not
    /// destroyed, 1 a destroyed non-vital part, else 0; the highest over the parts.
    /// </summary>
    static int WoundSeverity(IReadOnlyList<HealthPartView> parts, float? koPoint)
    {
        int worst = 0;

        foreach (var part in parts)
        {
            float h = part.Flesh, d = part.Bandage, s = part.Stun, m = part.Maximum;

            if (h + d - s <= -m)
            {
                if (!part.Vital && worst == 0) worst = 1;
            }
            else if (h + d < 0) worst = Math.Max(worst, part.Vital && !(koPoint is { } ko && h + d >= ko) ? 3 : 2);
        }

        return worst;
    }

    /// <summary>
    /// The medical header's word, its colour and its tooltip, the original's first match (<c>FUN_14088a8f0</c>, docs/game/ui-screens.md "Health word
    /// choice"), and alive in a bed that heals faster, "\nRecovery rate: {1}x" after the tooltip. Not built: the <c>+0xf0</c> status texts and the
    /// disguise words (Meitou models neither).
    /// </summary>
    internal static (string Text, string? Colour, string Tip) HealthWord(CharacterSnapshot lead)
    {
        var (text, colour, tip) = HealthWord(lead.Body!.Value, lead.Hud, lead.HealthParts, lead.Disguise, lead.Slave?.State ?? 0);
        return lead.BedRecoveryRate is { } rate && !lead.Body.Value.Dead
            ? (text, colour, tip + "\n" + Loc.Tr("Recovery rate: {1}x", rate))
            : (text, colour, tip);
    }

    /// <summary>
    /// <see cref="HealthWord(CharacterSnapshot)"/> from the body, the HUD view (bleeding, stomach, robot race, KO point) and the part rows.
    /// </summary>
    internal static (string Text, string? Colour, string Tip) HealthWord(BodyStatus body, HudView? hud = null,
        IReadOnlyList<HealthPartView>? parts = null, DisguiseView? disguise = null, int slaveState = 0)
    {
        bool robot = hud?.Robot ?? false;
        int w = WoundSeverity(parts ?? [], hud?.KoPoint ?? hud?.Extended?.KoPoint);
        float bleeding = hud?.Bleeding ?? 0;

        return DownWord(body, w, robot, bleeding)
            ?? InjuryWord(body, w, bleeding)
            ?? DisguiseWord(disguise, slaveState)
            ?? WellWord(body, hud);
    }

    /// <summary>
    /// A health word: <paramref name="word"/> and <paramref name="tip"/> translated.
    /// </summary>
    static (string, string?, string) Word(string word, string? colour, string tip) => (Loc.Tr(word), colour, Loc.Tr(tip));

    /// <summary>
    /// <see cref="HealthWord(BodyStatus, HudView?, IReadOnlyList{HealthPartView}?, DisguiseView?, int)"/>'s first matches: dead, being eaten,
    /// dying, knocked out, coma, playing dead, blood loss; null when none holds. <paramref name="w"/> is <see cref="WoundSeverity"/>.
    /// </summary>
    static (string Text, string? Colour, string Tip)? DownWord(BodyStatus body, int w, bool robot, float bleeding)
    {
        if (body.Dead) return Word("Dead", PlayerInterface.BadColour, "Gone forever. RIP.");
        if (body.BeingEaten)
            return Word("BEING EATEN ALIVE!!", PlayerInterface.BadColour, "Something is eating you alive! AAAAAAHHHHHHHHHH!!");
        if (body.Blood <= 0 && w == 1)
        {
            return Word("Dying", PlayerInterface.BadColour,
                "Severe limb damage has now led to critical blood loss. You will die without medical attention.");
        }

        if (w == 3)
            return Word("Dying", PlayerInterface.BadColour, "If you don't get medical attention, you will die.");
        if (body.KoTimer > 0)
            return ($"{Loc.Tr(robot ? "Rebooting" : "Unconscious")} ({(int)body.KoTimer})", PlayerInterface.BadColour, Loc.Tr("Knocked out cold."));
        if (body.Unconscious && body.Coma && w == 0)
        {
            return Word("Recovery coma", null,
                "You've suffered too much damage. You won't be getting up again until all critical body parts have recovered above 0.");
        }

        if (body.PlayingDead)
        {
            return Word("Playing dead...", PlayerInterface.MainColour,
                "This person has regained consciousness but is staying down, waiting for all the enemies to leave the area.");
        }

        if (body.Blood <= 0 && body.Unconscious)
        {
            return bleeding > 0
                ? Word("Bleeding out", PlayerInterface.BadColour,
                    "Unconscious from blood loss and bleeding to death. Hope that the bleeding stops before it's too late.")
                : Word("Blood loss KO", PlayerInterface.BadColour, "Unconscious from blood loss. It will recover over time, bed rest will help.");
        }

        return null;
    }

    /// <summary>
    /// The next matches: crippled legs or sword arm, critical injuries, limb trauma, staying low, starving, bleeding; null when none holds.
    /// </summary>
    static (string Text, string? Colour, string Tip)? InjuryWord(BodyStatus body, int w, float bleeding)
    {
        if (body.Crawling)
            return Word("Crippled", PlayerInterface.BadColour, "Your legs are incapable of supporting you.");
        if (body.RightArmDisabled && !body.Unconscious)
            return Word("Crippled arm", PlayerInterface.BadColour, "Your sword arm is crippled and you are unable to fight.");
        if (w == 2)
            return Word("Critical", PlayerInterface.BadColour, "Critical injuries will continue to degenerate without medical attention.");
        if (w == 1)
            return Word("Limb trauma", null, "One or more limbs have been seriously crippled by critical injury.");
        if (body.StayingLow)
            return Word("Staying low", PlayerInterface.MainColour, "You are staying down, hoping to avoid drawing attention.");
        if (body.Hunger < 1)
            return Word("Starving", PlayerInterface.BadColour, "You are starving to death. Get some food fast.");
        if (bleeding > 0) return Word("Bleeding", null, "Losing blood, will collapse if blood goes below 0.");
        return null;
    }

    /// <summary>
    /// The slave and disguise record: blown (or an escaped slave discovered) while the cooldown runs, else a working disguise; null otherwise.
    /// </summary>
    static (string Text, string? Colour, string Tip)? DisguiseWord(DisguiseView? disguise, int slaveState)
    {
        if (disguise is { } d)
        {
            if ((slaveState > 1 || d.Level > 0) && d.Blown > 0)
            {
                int secs = (int)d.Blown;
                string cooldown = "\n" + TooltipRuntime.MainTag + Loc.Tr(secs == 1 ? "Cooldown {1,num} second" : "Cooldown {1,num} seconds", secs);
                return slaveState < 2
                    ? (Loc.Tr("Disguise Blown!"), PlayerInterface.BadColour,
                        Loc.Tr("You've been discovered, somebody has seen through your disguise.") + cooldown)
                    : (Loc.Tr("Discovered!"), PlayerInterface.BadColour,
                        Loc.Tr("You've been discovered, somebody has noticed that you are an escaped slave.") + cooldown);
            }

            if (d.Works)
                return (Loc.Tr("In Disguise"), PlayerInterface.MainColour, Loc.Tr("You are currently disguised as {1}", d.Faction));
        }

        return null;
    }

    /// <summary>
    /// The last matches, which always give a word: wounded, hungry or malnourished, slightly injured, normal.
    /// </summary>
    static (string Text, string? Colour, string Tip) WellWord(BodyStatus body, HudView? hud)
    {
        if (body.WorstPart < 0.7f) return Word("Wounded", null, "You are kind of healthy for now.");

        if (body.Hunger < 2)
        {
            return (hud?.Fed ?? 0) <= 0
                ? Word("Hungry", PlayerInterface.BadColour, "You are hungry and suffering from malnutrition. Your stats and strength will be affected, "
                    + "you need to get some food as soon as possible.")
                : Word("Malnourished", PlayerInterface.BadColour, "You are hungry and suffering from malnutrition, but you are fed and slowly "
                    + "recovering. Your stats and strength will be affected until you recover fully.");
        }

        return body.WorstPart < 0.9f
            ? Word("Slightly Injured", null, "You are kind of healthy for now.")
            : Word("Normal", null, "You are kind of healthy for now.");
    }

    /// <summary>
    /// The HUD panels' text: the focused building's or the selected character's lines.
    /// </summary>
    internal void AddHudText(List<UiControl> controls)
    {
        float row = 20 * player.UiScale, sidePixels = player.StandardPixels;

        if (player.BuildingSelection.BuildingFocus() is { } focus)
        {
            var (name, state, stats, _, _, extended) = player.BuildingSelection.BuildingLines(focus);
            player.AddLines(controls, "NameText", [name], row, sidePixels);
            player.AddLines(controls, "StateText", state, row, sidePixels);
            player.AddLines(controls, "StatsText", stats, row, sidePixels);

            // Section 7 ("Stats 2"): only a turret fills it (TurretBuilding slot 40: Workers and Efficiency).
            if (player.InfoExpanded) player.AddLines(controls, "ExtendedStatsText", extended, row, sidePixels);

            AddMedicalText(controls, sidePixels);
            return;
        }

        if (player.Lead() is not { } lead) return;

        string faction = player.FactionName?.Invoke(lead.Faction) ?? "";
        player.AddLines(controls, "NameText", [new(lead.Name, faction, PlayerInterface.TitleColour, PlayerInterface.TitleColour)], row, sidePixels);
        player.AddLines(controls, "StateText", StateLines(lead, player.SpeedUnit), row, sidePixels);
        if (lead.Bounties.Count > 0 || lead.Slave is not null || lead.Status is not null)
            player.AddLines(controls, "BountyText", [TooltipRuntime.StatusLine(lead) ?? TooltipRuntime.BountyLine(lead.Bounties)], row, sidePixels);

        // The base game draws the stats labels in the painted face (Observed: glyph shapes and widths match Sentencia at its resized 22 pt); the
        // values take the standard face, like the medical column's numbers.
        player.AddLines(controls, "StatsText", StatsLines(lead), row, font: "Kenshi_PaintedTextFont_Medium", valueFont: "Kenshi_StandardFont_Medium",
            valuePixels: sidePixels);

        if (player.InfoExpanded)
        {
            player.AddLines(controls, "ExtendedStatsText", ExtendedLines(lead, player.SpeedUnit).Select(line => line with
            { Label = line.Label.ToUpperInvariant(), LabelColour = PlayerInterface.MainColour }).ToArray(), row, sidePixels);
        }

        AddMedicalText(controls, sidePixels);
    }

    /// <summary>
    /// The medical rows' names (centred on the bar), arrows and numbers.
    /// </summary>
    void AddMedicalText(List<UiControl> controls, float sidePixels)
    {
        foreach (var (bar, name, arrows) in _medicalRows)
        {
            // The names made once per row: inside the predicates they were a new string for every control compared, every frame.
            string panelName = bar + "Datapanel", valueName = bar + "Value";
            if (controls.FirstOrDefault(c => c.Name == panelName) is not { } host) continue;

            var fullBar = controls.FirstOrDefault(c => c.Name == bar)?.Bounds ?? host.Bounds;
            var textBounds = new UiRect(host.Bounds.X, fullBar.Y, host.Bounds.Width, fullBar.Height);
            string pixels = Math.Max(8, (int)MathF.Round(sidePixels)).ToString(CultureInfo.InvariantCulture);
            controls.Add(new($"{bar}-name", "TextBox", textBounds, Loc.Tr(name), "", "", true,
                new Dictionary<string, string> { ["FontPixels"] = pixels, ["TextAlign"] = "Center", ["TextColour"] = MyGuiColours.InkText },
                Vector4.Zero, Pickable: false, Clip: host.Clip));

            if (arrows.Length > 0)
            {
                controls.Add(new($"{bar}-arrows", "TextBox", textBounds, arrows, "", "", true,
                    new Dictionary<string, string>
                    {
                        ["FontPixels"] = pixels,
                        ["TextAlign"] = "Right VCenter",
                        ["TextColour"] = PlayerInterface.AlertColour,
                    },
                    Vector4.Zero, Pickable: false, Clip: host.Clip));
            }

            int valueIndex = controls.FindIndex(c => c.Name == valueName);

            if (valueIndex >= 0)
            {
                var value = controls[valueIndex];
                float height = Math.Max(value.Bounds.Height, 20 * player.UiScale);
                controls[valueIndex] = value with
                {
                    Bounds = value.Bounds with { Y = value.Bounds.Y + (value.Bounds.Height - height) / 2, Height = height },
                    Properties = new Dictionary<string, string>(value.Properties) { ["FontPixels"] = pixels }
                };
            }
        }
    }

    /// <summary>
    /// The medical rows bound this frame: the bar widget, the row's name and its arrows (<see cref="BindMedicalBars"/>).
    /// </summary>
    readonly List<(string Bar, string Name, string Arrows)> _medicalRows = [];

    /// <summary>
    /// A bar's fraction in steps of 1/1024, about half a pixel of a bar on a 4K panel: hunger and blood creep every tick, and an unrounded fraction
    /// made the HUD's bindings new each tick, so its cached layout (<see cref="PlayerInterface.ArrangeHud"/>) was made again for a change no one can
    /// see.
    /// </summary>
    static float BarStep(float fraction) => MathF.Round(fraction * 1024) / 1024;

    /// <summary>
    /// The medical panel (docs/game/ui-screens.md section 4): Blood first, the body parts, Hunger last, each a name centred on its bar, arrows at its
    /// right end and its number in the value column. A part's bar is green to its flesh less stun, yellow to the bandage on top of that, red behind
    /// it to its maximum less stun (the lost part shows red) and white past the green by the splint; blood is green to its fraction of capacity;
    /// hunger (0..3) green to h - 2, yellow to h - 1, red to h.
    /// </summary>
    internal void BindMedicalBars(UiBindings b, CharacterSnapshot? lead, IReadOnlyList<PlayerInterface.MedicalRow>? buildingRows = null)
    {
        _medicalRows.Clear();
        var rows = new List<PlayerInterface.MedicalRow>(buildingRows ?? []);

        if (buildingRows is null && lead?.Body is { } body) AddCharacterRows(rows, lead, body);

        for (int i = 1; i <= 9; i++) BindMedicalBar(b, i, rows);

        if (buildingRows is null && lead?.Body is not null)
        {
            var (word, colour, _) = HealthWord(lead);
            b.Text["HealthText"] = word;
            b.Properties["HealthText"] = new Dictionary<string, string>
            {
                ["TextAlign"] = "Center",
                ["TextColour"] = colour ?? MyGuiColours.TemplateInk(player.UiResources, "Kenshi_TextboxStandardText"),
            };
        }
    }

    /// <summary>
    /// A character's medical rows: Blood (Oil for a robot race), each body part, then Hunger (none for a robot race).
    /// </summary>
    void AddCharacterRows(List<PlayerInterface.MedicalRow> rows, CharacterSnapshot lead, BodyStatus body)
    {
        var hud = lead.Hud;
        float? koPoint = hud?.KoPoint ?? hud?.Extended?.KoPoint;
        float capacity = Math.Max(hud?.BloodCapacity ?? 1, 1e-3f), blood = hud?.Blood ?? body.Blood * capacity;

        // A run of '<', one per 0.05 of bleeding (at most 100).
        int bleed = Math.Min(100, (int)MathF.Ceiling(Math.Abs(hud?.Bleeding ?? 0) / 0.05f));

        // A robot race's row reads "Oil" and shows the Robot layer over its green, as a robotic part does (FUN_14088a8f0 passes RACE is robot;
        // docs/game/ui-screens.md).
        bool robot = hud is { Robot: true };
        rows.Add(new(Loc.Tr(robot ? "Oil" : "Blood"), blood / capacity, 0, 0, ((int)MathF.Floor(blood + 0.1f)).ToString(CultureInfo.InvariantCulture),
            new string('<', bleed), robot, TooltipRuntime.BloodTip(hud?.BleedRate ?? 1)));

        foreach (var part in lead.HealthParts)
        {
            // The original's row (FUN_14088a8f0): the green and the number are the flesh less the stun, the yellow adds the bandage, the white is
            // the green plus the splint (rig x 0.01), none on a stump; a stump or a crushed limb shows the Crushed layer.
            float max = Math.Max(part.Maximum, 1e-3f), health = part.Flesh - part.Stun, green = health / max;
            bool stump = part.Limb == Simulation.Bodies.LimbState.Stump;
            rows.Add(new(part.Name, green, (health + part.Bandage) / max, (max - part.Stun) / max,
                ((int)MathF.Floor(health + 0.1f)).ToString(CultureInfo.InvariantCulture), TooltipRuntime.PartArrows(part, koPoint), part.Robotic,
                TooltipRuntime.PartTip(part, koPoint, body.Unconscious))
            {
                White = stump ? 0 : Math.Clamp(green + part.Rig * 0.01f, 0f, 1f),
                Crushed = stump || part.Limb == Simulation.Bodies.LimbState.Crushed,
            });
        }

        float hunger = body.Hunger;

        // '>' digesting, '<!' below 2, none in a bed, else '<'. Robot races have no Hunger row.
        string hungerArrows = hud?.Fed > 0 ? ">   " : hunger < 2 ? "<!   " : lead.InBed ? "" : "<   ";

        if (hud is not { Robot: true })
        {
            string tip = TooltipRuntime.HungerTip(hunger, body.Unconscious, player.ActiveSquadMembers().Count);

            // The original's Hunger tooltip (FUN_14088a8f0) is the Hunger Rate rows (FUN_14088a2b0) with the sentence under them (#377).
            rows.Add(new(Loc.Tr("Hunger"), hunger - 2, hunger - 1, hunger, ((int)(hunger * 100)).ToString(CultureInfo.InvariantCulture),
                hungerArrows, false, tip)
            { TooltipRows = hud?.ExtendedTips is { } tips ? [.. tips.Hunger, (tip, "")] : null });
        }
    }

    /// <summary>
    /// The panel's bar <paramref name="i"/> (1-based): hidden past the rows, else its layers, tooltip, value and name.
    /// </summary>
    void BindMedicalBar(UiBindings b, int i, List<PlayerInterface.MedicalRow> rows)
    {
        string name = $"LifeBar{i}";

        if (i > rows.Count)
        {
            b.Hidden.Add(name + "Crushed");
            b.Hidden.Add(name + "White");
            b.Hidden.Add(name);
            b.Hidden.Add(name + "Value");
            b.Hidden.Add(name + "Tooltip");
            return;
        }

        var r = rows[i - 1];
        if (r.Crushed) b.Progress[name + "Crushed"] = 1;
        else b.Hidden.Add(name + "Crushed");

        // The splint layer is drawn only where it reaches past the green (with no splint it would sit under the green).
        if (r.White > Math.Max(r.Green, 0)) b.Progress[name + "White"] = BarStep(r.White);
        else b.Hidden.Add(name + "White");

        // The row's tooltip sits on its LifeBarNTooltip widget (FUN_1407228a0 sets a type 0 text there).
        if (r.TooltipRows is { Count: > 0 } tipRows)
            b.Properties[name + "Tooltip"] = UiTooltips.RowProperties(tipRows, "fixed");
        else if (r.Tooltip is { Length: > 0 } tip)
            b.Properties[name + "Tooltip"] = UiTooltips.TextProperties(tip, "fixed");
        else b.Hidden.Add(name + "Tooltip");

        b.Progress[name + "Green"] = BarStep(Math.Max(r.Green, 0));
        b.Progress[name + "Yellow"] = BarStep(Math.Max(r.Yellow, 0));
        b.Progress[name + "Red"] = BarStep(Math.Max(r.Red, 0));
        if (r.Robot) b.Progress[name + "Robot"] = BarStep(Math.Max(r.Green, 0));
        else b.Hidden.Add(name + "Robot");

        b.Text[name + "Datapanel"] = "";
        b.Text[name + "Value"] = r.Value;
        b.Properties[name + "Value"] = new Dictionary<string, string>
        {
            ["TextAlign"] = "Center",
            ["TextColour"] = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_TextboxStandardText"),
        };
        _medicalRows.Add((name, r.Name, r.Arrows));
    }
}
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The HUD stats panel's lines with their tooltips (<c>FUN_140892120</c>) and the extended lines' tooltips (<c>FUN_140895350</c>), docs/game/ui-screens.md section 4 "Stat tooltips".
/// Rows are (key, value) with the exe's inline colour tags: Bad <c>#59231a</c>, Good <c>#a8b774</c>, Main <c>#afa68b</c>, and the text default <c>#140806</c> for a signed 0.
/// "-" stands where the original shows "-": a row that does not apply (outdoors, no dust).
/// </summary>
static class HudTips
{
    const string Bad = "#59231a", Good = "#a8b774", Main = "#afa68b", Ink = "#140806";
    const string None = "-";

    /// <summary>A signed whole number in its sign's colour (<c>FUN_140889320</c>): <c>+N</c> Good, <c>-N</c> Bad, <c>0</c> in the text default.</summary>
    static string Signed(int n) => (n < 0 ? Bad : n > 0 ? Good : Ink) + Loc.Tr(n > 0 ? "+{1,num}" : "{1,num}", n);

    static string Penalty(int percent) => Bad + "-" + Loc.Tr("{1,num}%", percent);

    static string Int(float v) => Loc.Tr("{1,num}", (int)v);

    static int Round(float v) => (int)MathF.Floor(v + 0.5f);

    /// <summary>A multiplier <c>{x.xx}x</c> (<c>FUN_1408894f0</c>): above 1 Bad, below 1 Good, 1 plain.</summary>
    static string Mult(float x) => (x > 1 ? Bad : x < 1 ? Good : "") + Loc.Tr("{1,num=fix,p=2}x", x);

    /// <summary>
    /// The stats panel's lines (docs/game/ui-screens.md section 4), each with its tooltip. <paramref name="inBuilding"/>: in a building at all, on top included (the original's location
    /// type is not 0xb); <paramref name="inside"/>: in its indoor volume (<see cref="SightMap.Inside"/>), which the indoors modifiers of the values need. <paramref name="displayMode"/>:
    /// <c>getGUIStatsDisplayMode</c> (<see cref="CombatSystem.StatsDisplayMode"/>), 1 Martial Arts, 2 the crossbow lines, 0 Attack and Defense.
    /// </summary>
    public static List<HudStatLine> Lines(CharacterCold cold, MedicalState med, CharacterStats stats, HudCombat? hc, GameConstants? constants, bool inBuilding, bool inside, int displayMode)
    {
        var fighter = cold.Fighter;
        var factors = (fighter?.Factors ?? StatFactors.None) with { Stun = med.DustStatFactor };
        float e = fighter?.EncumbranceFactor ?? 1;
        float Stored(StatsEnumerated stat) => stats[stat] * stats.Scale;
        float Eff(StatsEnumerated stat, StatUse use = StatUse.All) => Stored(stat) * med.StatMultiplier(stat, use, factors);
        int dust = (int)(med.DustInEffect * 10);
        bool defensive = (cold.Orders & CharacterOrders.Block) != 0;
        HudStatLine Line(string label, StatsEnumerated stat, float factor = 1) =>
            new(label, (stats[stat] + CombatSystem.GearBonus(cold, stat)) * med.StatMultiplier(stat) * factor, stats[stat]);
        // An animal's age (slot 0x398) scales its fighting numbers (the stats multiplier, already in Stored); a person's is 1.
        var animal = cold.Animal;
        float age = animal is null ? 1 : stats.Scale;
        var weapon = fighter?.Weapon?.Stats;
        int indoorsMod = weapon?.IndoorsMod ?? 0;

        var lines = new List<HudStatLine>(7);
        if (animal is not null)
            lines.Add(new HudStatLine("Age:", age, age) { Text = Loc.Tr(AnimalData.AgeWord(age)), Tip = [(Loc.Tr("Stat Multiplier:"), Loc.Tr("{1,num=fix,p=2}x", age))] });
        bool ranged = displayMode == 2 && fighter?.Crossbow is not null;
        if (ranged)
        {
            // The gear's ranged factor as the breakdown's Total has it.
            lines.Add(Line("Crossbows:", StatsEnumerated.Crossbows, factors.Ranged) with { Tip = Breakdown(med, stats, factors, StatsEnumerated.Crossbows, "Crossbows") });
            lines.Add(Line("Precision:", StatsEnumerated.FriendlyFire, factors.Ranged) with { Tip = Breakdown(med, stats, factors, StatsEnumerated.FriendlyFire, "Careful Shooting") });
            lines.Add(Line("Perception:", StatsEnumerated.Perception) with
            {
                Tip = Breakdown(med, stats, factors, StatsEnumerated.Perception, "Perception",
                CombatSystem.GearBonus(cold, StatsEnumerated.Perception))
            });
        }
        else if (displayMode == 1 && animal is null)
        {
            // FUN_140884e00 and FUN_140884680: the value lerp(e, 0.2, 1) x (bonus + skill), the change the bonus less 0.8 (1 - e) x skill. The bonus (FUN_140884580) takes the
            // weapon's indoors field like Attack's (the fists item's: 8), while the row shows a flat +8 in any building.
            float ma = Eff(StatsEnumerated.MartialArts);
            int gear = (int)(fighter?.UnarmedBonus ?? 0), bonus = Indoors(gear, fighter?.FistsItem?.Stats.IndoorsMod ?? 8) - dust, encumbrance = (int)-(0.8f * (1 - e) * ma);
            int value = (int)((0.2f + 0.8f * e) * (bonus + ma)), change = bonus + encumbrance;
            // The original labels the encumbrance row of this tooltip "Strength XP:".
            var tip = new List<(string, string)> { (Loc.Tr("Total equipment bonuses:"), Signed(gear)), (Loc.Tr("Indoors bonus:"), inBuilding ? Signed(8) : None),
                (Loc.Tr("Weather penalty:"), dust != 0 ? Signed(-dust) : None), (Loc.Tr("Encumbrance penalty:"), Signed(encumbrance)),
                (Loc.Tr("Strength XP:"), Loc.Tr("{1,num}%", (int)((1 - e) * 100))) };
            if (hc is { } fists) tip.Add((Loc.Tr("Damage:"), Int(fists.UnarmedDamage)));
            lines.Add(new HudStatLine("Martial Arts:", value, value - change) { Tip = tip });
        }
        else
        {
            // FUN_1408844c0 / FUN_140884e80: the equipment bonus (indoors with the weapon's indoors modifier) less the dust penalty, then the effective skill, times the age;
            // the change is the bonus. An animal always shows Attack, and no Defense.
            int gear = (int)((fighter?.AttackBonus ?? 0) + (weapon?.AttackMod ?? 0)), change = Indoors(gear, indoorsMod) - dust, value = (int)(change * age + Eff(StatsEnumerated.MeleeAttack));
            lines.Add(new HudStatLine("Attack:", value, value - change)
            {
                Tip = [(Loc.Tr("Total equipment bonuses:"), Signed(gear)), (Loc.Tr("Weapon indoors penalty:"), inside ? Signed(indoorsMod) : None),
                    (Loc.Tr("Weather penalty:"), dust != 0 ? Signed(-dust) : None), (Loc.Tr("Base hit chance:"), Loc.Tr("{1,num}%", value))],
            });
            if (animal is null)
            {
                // FUN_140884710 / FUN_140884f50: the defence bonus (+ the indoors modifier inside), +20 in defensive mode, then the effective skill on top.
                int def = (int)((fighter?.DefenceBonus ?? 0) + (weapon?.DefenceMod ?? 0)), defChange = def + (inside ? indoorsMod : 0) + (defensive ? 20 : 0);
                int defValue = (int)(defChange + Eff(StatsEnumerated.MeleeDefence));
                var defTip = new List<(string, string)> { (Loc.Tr("Total equipment bonuses:"), Signed(def)) };
                if (defensive) defTip.Add((Loc.Tr("Defensive combat mode:"), Good + "+20"));
                defTip.Add((Loc.Tr("Weapon indoors penalty:"), inBuilding ? Signed(indoorsMod) : None));
                defTip.Add((Loc.Tr("Your defense skill is your chance of blocking enemy attacks."), ""));
                lines.Add(new HudStatLine("Defense:", defValue, defValue - defChange) { Tip = defTip });
            }
        }

        // The equipment bonus indoors: a positive bonus is dropped under an indoors penalty, then the modifier is added.
        int Indoors(int bonus, int mod) => !inside ? bonus : (bonus > 0 && mod < 0 ? 0 : bonus) + mod;

        var strengthTip = new List<(string, string)>();
        if (cold.Extended is { } x) strengthTip.Add((Loc.Tr("Strength XP rate:"), Loc.Tr("{1,num}%", (int)(x.StrengthXp * 100))));
        // FUN_140892120 writes the carry weight with a '%' (the inventory's own line says kg).
        if (constants is not null)
            strengthTip.Add((Loc.Tr("Max carry weight:"), Loc.Tr("{1,num}%", (int)(Eff(StatsEnumerated.Strength, StatUse.Hunger | StatUse.Limbs | StatUse.DamageState | StatUse.LimbItems) + constants.EncumbranceBase))));
        if (hc is { } c) strengthTip.Add((Loc.Tr("Blunt weapon damage:"), Int(c.Blunt)));
        strengthTip.AddRange(Breakdown(med, stats, factors, StatsEnumerated.Strength, "Strength"));
        lines.Add(Line("Strength:", StatsEnumerated.Strength) with { Tip = strengthTip });

        var dexterityTip = new List<(string, string)>();
        if (hc is { } d)
            dexterityTip.AddRange([(Loc.Tr("Attack speed:"), Loc.Tr("{1,num=fix,p=2}x", d.AttackRate)), (Loc.Tr("Block speed:"), Loc.Tr("{1,num=fix,p=2}x", d.BlockRate)),
                (Loc.Tr("Weapon cutting damage:"), Loc.Tr("{1,num=fix,p=2}", d.Cut))]);
        dexterityTip.AddRange(Breakdown(med, stats, factors, StatsEnumerated.Dexterity, "Dexterity"));
        lines.Add(Line("Dexterity:", StatsEnumerated.Dexterity, fighter?.DexterityMult ?? 1) with { Tip = dexterityTip });

        var toughnessTip = new List<(string, string)> { (Loc.Tr("Toughness XP rate:"), Loc.Tr("{1,num}x", (int)XpService.ToughnessFactor(med))) };
        float tough = stats.ScaledToughness;
        if (constants is not null)
            toughnessTip.AddRange([(Loc.Tr("Health KO point:"), Int(MedicalState.KoPoint(constants, stats.ScaledToughness))),
                (Loc.Tr("Wound degeneration speed:"), Loc.Tr("{1,num=fix,p=2}x", MedicalState.DegenerationMult(constants, tough)))]);
        if (hc is { } r) toughnessTip.Add((Loc.Tr("Damage resistance:"), Loc.Tr("{1,num}%", (int)(r.Resistance * 100))));
        lines.Add(new HudStatLine("Toughness:", stats.Toughness, stats.Toughness) { Tip = toughnessTip });

        if (!ranged)
        {
            // FUN_140885190: the dodge skill less the encumbrance and leg terms plus the gear's, +20 in defensive mode with fists (CharStats +0x1f0, the current weapon type, is 5).
            float dodge = Stored(StatsEnumerated.Dodge);
            float encumbrance = -(1 - Math.Clamp(e, 0.4f, 1)) * dodge, legs = -(1 - Math.Clamp(Speed.LegFactor(med), 0.5f, 1)) * dodge;
            float gear = dodge * (fighter?.DodgeMult ?? 1) - dodge, total = dodge + encumbrance + legs + gear + (defensive && (fighter is null || fighter.Unarmed) ? 20 : 0);
            int value = (int)total, change = Round(total - dodge);
            var dodgeTip = new List<(string, string)> { (Loc.Tr("Dodge skill:"), Signed(Round(dodge))), (Loc.Tr("Encumbrance penalty:"), Signed(Round(encumbrance))),
                (Loc.Tr("Leg injury penalty:"), Signed(Round(legs))), (Loc.Tr("Equipment penalty:"), Signed(Round(gear))) };
            if (defensive) dodgeTip.Add((Loc.Tr("Defensive mode:"), Signed(20)));
            dodgeTip.Add((Loc.Tr("-End total:"), Signed(Round(total))));
            lines.Add(new HudStatLine("Dodge:", value, value - change) { Tip = dodgeTip });
        }
        return lines;
    }

    /// <summary>
    /// The numbers of the stats window's "Stats Affected" rows (<c>FUN_1408b1250</c>, docs/game/ui-screens.md "Stats window"), for the three attributes that have them. Strength: the
    /// carry weight (int)(effective strength + <c>encumbrance base</c>) kg, the walking strength XP rate (<c>FUN_1408c60c0</c>) and the fists' damage; Toughness: damage resistance,
    /// the fists' damage, the toughness XP rate, the knockout time and wound deterioration lerps over t = toughness x scale / 100 (unclamped), the KO point and the stagger damage;
    /// Dexterity: the attack and block play rates; Dodge: the encumbrance factor as the XP rate; Athletics: the run value, the current speed and the Athletics XP rate; Swimming: the
    /// base and current swim speeds. Rows without a number in the original have none here.
    /// </summary>
    public static Dictionary<StatsEnumerated, IReadOnlyList<StatReadout>> StatsWindow(CharacterCold cold, MedicalState med, RaceData race, CharacterStats stats, HudCombat? hc,
        GameConstants? constants, Data.GameDatabase? db = null)
    {
        float e = cold.Fighter?.EncumbranceFactor ?? 1;
        var factors = (cold.Fighter?.Factors ?? StatFactors.None) with { Stun = med.DustStatFactor };
        float t = stats.ScaledToughness * 0.01f;
        static float Lerp(float t, float a, float b) => (b - a) * t + a;
        var strength = new List<StatReadout>();
        if (constants is not null)
            strength.Add(new("Max carry weight:", Loc.Tr("{1,num} kg", (int)(stats[StatsEnumerated.Strength] * stats.Scale
                * med.StatMultiplier(StatsEnumerated.Strength, StatUse.Hunger | StatUse.Limbs | StatUse.DamageState | StatUse.LimbItems, factors) + constants.EncumbranceBase))));
        if (cold.Extended is { } x) strength.Add(new("Strength XP rate (walking):", Loc.Tr("{1,num}%", (int)(x.StrengthXp * 100))));
        var toughness = new List<StatReadout>();
        if (hc is { } c)
        {
            strength.Add(new("Martial arts damage", Int(c.UnarmedDamage)));
            toughness.Add(new("Damage resistance:", Loc.Tr("{1,num}%", (int)(c.Resistance * 100))));
            toughness.Add(new("Martial arts damage", Int(c.UnarmedDamage)));
        }
        toughness.Add(new("Toughness current XP rate:", Loc.Tr("{1,num}", XpService.ToughnessFactor(med))));
        if (constants is not null)
        {
            toughness.Add(new("Knockout time:", Loc.Tr("{1,num=fix,p=2}", Lerp(t, constants.KnockoutMult1, constants.KnockoutMult99)) + "x"));
            toughness.Add(new("Knockout point:", Int(MedicalState.KoPoint(constants, stats.ScaledToughness)), Main: true));
            toughness.Add(new("Wound deterioration speed:", Loc.Tr("{1,num=fix,p=2}", Lerp(t, constants.DegenerationMult1, constants.DegenerationMult99)) + "x"));
        }
        if (cold.Extended is { } r)
        {
            // FUN_1408c5ec0 with the weapon's unit weight (item slot 0x248): the encumbrance factor, x1.5 when that weight is above 0.8 x the run value.
            float weight = CombatWeapon(cold)?.UnitWeight ?? 0;
            strength.Add(new("Strength XP rate (combat):", Loc.Tr("{1,num}%", (int)(Math.Max(e, 0) * (weight > 0.8f * r.RunValue ? 1.5f : 1) * 100))));
        }
        if (hc is { } s) toughness.Add(new("Amount of damage it takes to stagger you:", Int(s.Stagger)));
        var result = new Dictionary<StatsEnumerated, IReadOnlyList<StatReadout>> { [StatsEnumerated.Strength] = strength, [StatsEnumerated.Toughness] = toughness };
        if (hc is { } d)
            result[StatsEnumerated.Dexterity] = [new("Attack speed:", Loc.Tr("{1,num=fix,p=2}", d.AttackRate)), new("Block speed:", Loc.Tr("{1,num=fix,p=2}", d.BlockRate))];
        // CharStats +0x190 (the encumbrance factor) x 100 as a float number.
        result[StatsEnumerated.Dodge] = [new("Dodge XP rate:", Loc.Tr("{1,num}%", e * 100))];
        if (cold.Extended is { } run)
            result[StatsEnumerated.Athletics] = [new("Max run speed:", Int(run.RunValue)), new("Current run speed:", Int(run.RunSpeed)),
                new("Athletics XP bonus:", Loc.Tr("{1,num}%", (int)(run.AthleticsXp * 100)))];
        result[StatsEnumerated.Swimming] = [new("Max swim speed:", Int(Speed.SwimBase(race, stats))), new("Current swim speed:", Int(Speed.Swim(race, stats, med, e)))];
        // The skills, from the effective level L: work rates lerp(L/100, 0.5, 1.4) (FUN_1408852a0), aim (L + perception) x 0.5% (FUN_140435160).
        float Eff(StatsEnumerated s) => stats[s] * stats.Scale * med.StatMultiplier(s, factors: factors);
        string Rate(StatsEnumerated s) => Loc.Tr("{1,num,p=2}x", Lerp(Eff(s) * 0.01f, 0.5f, 1.4f));
        string Aim(StatsEnumerated s) => Loc.Tr("{1,num}%", (int)((Eff(s) + Eff(StatsEnumerated.Perception)) * 0.005f * 100));
        string Grade(StatsEnumerated s) => "[" + Loc.Tr(ArmourTooltip.GradeNames[CraftTier((int)Eff(s))]) + "]";
        if (hc is { } m) result[StatsEnumerated.MartialArts] = [new("Affects damage", Int(m.UnarmedDamage))];
        if (constants is not null) result[StatsEnumerated.Medic] = [new("First aid skills:", Loc.Tr("{1,num,p=2}x", Lerp(Eff(StatsEnumerated.Medic) * 0.01f, 1, 15) * constants.MedicSpeedMult))];
        result[StatsEnumerated.Turrets] = [new("Accuracy:", Aim(StatsEnumerated.Turrets)), new("Rate of fire:", Aim(StatsEnumerated.Turrets))];
        result[StatsEnumerated.Crossbows] = [new("Accuracy:", Aim(StatsEnumerated.Crossbows)), new("Rate of fire:", Aim(StatsEnumerated.Crossbows))];
        result[StatsEnumerated.FriendlyFire] = [new("Friendly fire avoidance:", Loc.Tr("{1,num}%", (int)(Math.Min(Eff(StatsEnumerated.FriendlyFire) * 0.015f, 1) * 100)))];
        result[StatsEnumerated.Assassination] = [new("Stealthy knock-outs:", Rate(StatsEnumerated.Assassination))];
        result[StatsEnumerated.Engineering] = [new("Building things:", Rate(StatsEnumerated.Engineering)), new("Repairing things:", Rate(StatsEnumerated.Engineering))];
        result[StatsEnumerated.Science] = [new("Research rate:", Rate(StatsEnumerated.Science))];
        result[StatsEnumerated.Labouring] = [new("Mining:", Rate(StatsEnumerated.Labouring)), new("Using machinery:", Rate(StatsEnumerated.Labouring))];
        result[StatsEnumerated.Farming] = [new("Harvesting speed:", Rate(StatsEnumerated.Farming)),
            new("Harvesting yield:", Loc.Tr("{1,num}%", (int)(Math.Clamp(Lerp(Eff(StatsEnumerated.Farming) * 0.01f, 0.5f, 2), 0, 1) * 100)))];
        result[StatsEnumerated.Cooking] = [new("Cooking:", Rate(StatsEnumerated.Cooking))];
        var weapon = new List<StatReadout> { new("Crafting weapons (speed):", Rate(StatsEnumerated.SmithingWeapon)) };
        if (db is not null && WeaponModel(db, Eff(StatsEnumerated.SmithingWeapon)) is { } model) weapon.Add(new("Weapon quality:", "[" + model + "]"));
        result[StatsEnumerated.SmithingWeapon] = weapon;
        result[StatsEnumerated.SmithingArmour] = [new("Crafting armour (speed):", Rate(StatsEnumerated.SmithingArmour)), new("Armour quality:", Grade(StatsEnumerated.SmithingArmour))];
        result[StatsEnumerated.SmithingBow] = [new("Crafting crossbows (speed):", Rate(StatsEnumerated.SmithingBow)),
            new("Crafting crossbow bolts (speed):", Rate(StatsEnumerated.SmithingBow)), new("Crossbow quality:", Grade(StatsEnumerated.SmithingBow))];
        return result;
    }

    /// <summary>The weapon Strength's combat XP rate weighs: the one in hand, else the first of <c>hip</c> and <c>back</c>.</summary>
    static Items.ItemInstance? CombatWeapon(CharacterCold cold)
    {
        if (cold.Inventory?.Items is not { } items) return null;
        Items.ItemInstance? In(string section) => items.FirstOrDefault(i => i.Section == section && i.Type == Data.Fcs.FcsRecordType.Weapon);
        return cold.Fighter is { Weapon: not null } f ? In(f.WeaponIsBack ? "back" : "hip") : In("hip") ?? In("back");
    }

    /// <summary>The crafted-quality tier of an int skill (<c>FUN_14082e6b0</c> without a critical): one step each above 19, 39, 59 and 79.</summary>
    static int CraftTier(int skill) => (skill > 19 ? 1 : 0) + (skill > 39 ? 1 : 0) + (skill > 59 ? 1 : 0) + (skill > 79 ? 1 : 0);

    /// <summary>
    /// The weapon model a weaponsmith of <paramref name="level"/> makes (<c>FUN_1408b0f90</c>): of <c>PLAYER_WEAPONS</c>' <c>weapon models</c>, the highest v0 above 0 and at or below
    /// the level, else the record <c>913-gamedata.base</c>'s name.
    /// </summary>
    static string? WeaponModel(Data.GameDatabase db, float level)
    {
        var best = (db.Find(Data.Gameplay.Construction.CraftBench.PlayerWeapons)?.GetReferences("weapon models") ?? [])
            .Select(r => (Record: db.Find(r.TargetStringId), Level: r.Values.Value0)).Where(m => m.Record is not null && m.Level > 0 && m.Level <= level)
            .OrderByDescending(m => m.Level).FirstOrDefault();
        return (best.Record ?? db.Find("913-gamedata.base"))?.Name;
    }

    /// <summary>
    /// A stat's breakdown (<c>FUN_140890b00</c>): the stored skill, a row per term of the stat multiplier that costs anything (injuries, hunger, weather, darkness, the fitted limbs, the
    /// gear), for strength the missing-arm penalty instead of the gear, for perception the armour's <paramref name="bonus"/> (added before the multiplier), then the effective value.
    /// </summary>
    static List<(string, string)> Breakdown(MedicalState med, CharacterStats stats, StatFactors factors, StatsEnumerated stat, string name, int bonus = 0)
    {
        float stored = stats[stat] * stats.Scale;
        int Lost(StatUse use) => (int)((1 - med.StatMultiplier(stat, use, factors)) * 100);
        var rows = new List<(string, string)> { (Loc.Tr("{1} skill:", Loc.Tr(name)), Int(stored)) };
        foreach (var (use, label) in new[] { (StatUse.Limbs, "Injuries:"), (StatUse.Hunger, "Hunger:"), (StatUse.Stun, "Weather:"), (StatUse.Light, "Working in darkness:") })
            if (Lost(use) is > 0 and var lost) rows.Add((Loc.Tr(label), Penalty(lost)));
        int limbs = Lost(StatUse.DamageState);
        if (limbs > 0) rows.Add((Loc.Tr("Robotics:"), Penalty(limbs)));
        else if (limbs < 0) rows.Add((Loc.Tr(med.HasProsthetics ? "Robotics:" : "Limbs disabled:"), Good + "+" + Loc.Tr("{1,num}%", -limbs)));
        if (stat == StatsEnumerated.Strength)
        {
            if (med.Aggregates().EitherArmDisabled) rows.Add((Loc.Tr("Missing arm penalty:"), Penalty((int)((1 - 0.66f) * 100))));
        }
        else if (stat == StatsEnumerated.Perception)
        {
            // FUN_140889320(bonus): +n Good or n Bad, no unit.
            if (bonus != 0) rows.Add((Loc.Tr("Armour/equipment:"), Signed(bonus)));
        }
        else if (Lost(StatUse.LimbItems) is not 0 and var gear)
            rows.Add((Loc.Tr("Armour/equipment:"), Signed(-gear) + "%"));
        rows.Add((Loc.Tr("Total:"), Int((stored + bonus) * med.StatMultiplier(stat, StatUse.All, factors))));
        return rows;
    }

    /// <summary>The extended lines' tooltips (<c>FUN_140895350</c>); the run speed's rows are the host's (<see cref="ExtendedTips"/>).</summary>
    public static ExtendedTips Extended(CharacterCold cold, MedicalState med, RaceData race, CharacterStats stats, ExtendedStats x, GameConstants? constants)
    {
        float e = cold.Fighter?.EncumbranceFactor ?? 1;
        bool carrying = !cold.Carrying.IsNone;
        var athletics = new List<(string, string)> { (Loc.Tr("Encumbrance penalty:"), Penalty((int)((1 - e) * 100))) };
        if (x.RunSpeed > x.RunValue * 0.8f) athletics.Add((Loc.Tr("Full speed bonus:"), Good + "+50%"));
        athletics.Add((Loc.Tr("This is the rate at which your athletics skill is increasing while running."), ""));

        // In combat mode the rate is the drawn weapon's weight factor, or 1 - e with nothing drawn (current weapon type 5); else the carrying rate, whose bonus row is halved while carrying.
        int rate = (int)(x.StrengthXp * 100);
        var strength = new List<(string, string)>();
        if (x.Fighting) strength.Add((Loc.Tr(x.Fists ? "Encumbrance:" : "Weapon weight:"), Loc.Tr("{1,num}%", rate)));
        else
        {
            strength.Add((Loc.Tr("Encumbrance bonus:"), Loc.Tr("{1,num}%", carrying ? (int)(rate * 0.5f) : rate)));
            if (carrying) strength.Add((Loc.Tr("Carrying somebody:"), Good + "200%"));
        }
        strength.Add((Loc.Tr("This is the rate at which your strength is increasing."), ""));

        var arms = med.Aggregates();
        var toughness = new List<(string, string)>();
        if (arms.LeftArmDisabled) toughness.Add((Loc.Tr("Crippled left arm:"), Good + "+1x"));
        if (arms.RightArmDisabled) toughness.Add((Loc.Tr("Crippled right arm:"), Good + "+2x"));
        if (med.BloodKo) toughness.Add((Loc.Tr("Blood loss trauma:"), Good + "+2x"));
        if (WorstFraction(med) < 0) toughness.Add((Loc.Tr("Critical wounds:"), Good + "+2x"));
        toughness.Add((Loc.Tr("This is the rate at which your toughness increases when you get hurt."), ""));

        var hunger = new List<(string, string)> { (Loc.Tr("Racial hunger rate:"), Mult(race.HungerRate)) };
        // An animal's age row (FUN_14088a2b0): when its age (slot 0x398) is not 1, the size factor (slot 0x390, lerp(age, scale min, scale max)).
        if (cold.Animal is { } animal && stats.Scale != 1) hunger.Add((Loc.Tr("Age:"), Mult(animal.Scale(stats.Scale))));
        if (constants is not null)
        {
            hunger.Add((Loc.Tr("Encumbrance effect:"), Mult((1 - e) * constants.EncumbranceHungerRate + 1)));
            if (x.Resting) hunger.Add((Loc.Tr("Resting/KO state:"), Mult(constants.BedHungerRate)));
        }
        // FUN_14088a2b0: the idle flag (CharBody::amIdle) gives the Idle row, else the used functionality's hunger rate (CharStats +0x178) when it is not 1. A Meitou worker at its
        // work clip may have no task, so its rate goes first, as in the hunger factor.
        if (x.WorkHungerRate is { } work)
        {
            if (work != 1) hunger.Add((Loc.Tr("Current work:"), Mult(work)));
        }
        else if (x.Idle) hunger.Add((Loc.Tr("Idle:"), Mult(MedicalState.IdleHungerRate)));

        // FUN_140895350's sneak row: t = max((s - 40) / 60, 0) of the stealth term s, which is in 0..1, then lerp(12, S, t), so the cap reads 12.
        float? sneak = (cold.Orders & CharacterOrders.Sneak) != 0 ? 12 + (x.RunSpeed - 12) * MathF.Max((Orders.Stealth(cold) - 40) / 60, 0) : null;
        return new ExtendedTips(athletics, strength, toughness, hunger, stats.Athletics, Speed.LegFactor(med), med.LegItemFactor(StatsEnumerated.Athletics), sneak,
            cold.Fighter?.AthleticsFactor ?? 1);
    }

    /// <summary>Medical <c>+0xa4 worstDamage</c>: the lowest part fraction, 1 without parts.</summary>
    static float WorstFraction(MedicalState med)
    {
        float worst = 1;
        foreach (var p in med.PartSpan) worst = MathF.Min(worst, p.Fraction);
        return worst;
    }
}
namespace Meitou.Data.Gameplay.Bodies;

/// <summary>One entry of a race's <c>combat anatomy</c>: a part, how likely it is to be struck, and its base HP.</summary>
/// <param name="Part">The LOCATIONAL_DAMAGE record.</param>
/// <param name="HitWeight">The reference's value 0: a relative chance to be struck (<b>Observed</b>; the assignment of the two integers is inferred, character-stats.md).</param>
/// <param name="BaseHp">The reference's value 1: the part's base HP (100 for humans); 100 when the data says less than 1.</param>
public readonly record struct AnatomyEntry(BodyPartTemplate Part, int HitWeight, float BaseHp);

/// <summary>A reference with a slot number: RACE <c>severed limbs</c> (the item a lost limb drops) and <c>limb replacement</c> (the default prosthetic) per limb slot.</summary>
public readonly record struct SlotReference(int Slot, string TargetStringId);

/// <summary>
/// A RACE as the body code uses it (docs/game/character-stats.md "Races", <b>Verified</b> field by field). The properties can be set directly, so tests
/// and tools can build a race without a database; <see cref="From"/> reads one from a record.
/// </summary>
public sealed class RaceData
{
    public string StringId { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary><c>heal rate</c>: multiplier of part healing and blood recovery.</summary>
    public float HealRate { get; init; } = 1;
    /// <summary><c>bleed rate</c>: multiplier of blood loss (0 = bloodless).</summary>
    public float BleedRate { get; init; } = 1;
    /// <summary><c>heal stat</c>: the skill used to treat this race (Medic 9, Hive medic 14, Vet 15, Robotics 6).</summary>
    public StatsEnumerated HealStat { get; init; } = StatsEnumerated.Medic;
    /// <summary><c>hunger rate</c>: multiplier of starvation speed; 0 never starves.</summary>
    public float HungerRate { get; init; } = 1;
    public float MinBlood { get; init; } = 75;
    public float MaxBlood { get; init; } = 150;
    /// <summary><c>speed min skill</c>: run speed at athletics 0.</summary>
    public float SpeedMinSkill { get; init; } = 70;
    /// <summary><c>speed max skill</c>: run speed at athletics 100.</summary>
    public float SpeedMaxSkill { get; init; } = 120;
    public float WalkSpeed { get; init; } = 15;
    public float SwimSpeedMult { get; init; } = 1;
    public float SwimOffset { get; init; } = 1;
    /// <summary>The data's <c>water avoidance</c> (-10 to 10).</summary>
    public float WaterAvoidanceRaw { get; init; }
    /// <summary>The pathfinder's value: a becomes a + 1 if a >= 0, else 1 / (1 - a) (<b>Observed</b>, character-stats.md).</summary>
    public float WaterAvoidance => WaterAvoidanceRaw >= 0 ? WaterAvoidanceRaw + 1 : 1 / (1 - WaterAvoidanceRaw);
    public float VisionRangeMult { get; init; } = 1;
    public float CombatMoveSpeedMult { get; init; } = 1;
    /// <summary><c>pathfind footprint radius</c>: the clearance a path query needs (4 humans, 7 Garru, 40 Leviathan).</summary>
    public float PathfindFootprintRadius { get; init; } = 4;
    public float PathfindAcceleration { get; init; } = 20;
    public int ExtraAttackSlots { get; init; }

    public bool IsRobot { get; init; }
    public bool Swims { get; init; }
    public bool Carriable { get; init; }
    public bool SingleGender { get; init; }
    public bool Gigantic { get; init; }
    public bool Vampiric { get; init; }
    public bool NoHats { get; init; }
    public bool NoShirts { get; init; }
    public bool NoShoes { get; init; }
    /// <summary>The inverse of the data's <c>cant enter buildings</c>.</summary>
    public bool CanEnterBuildings { get; init; } = true;
    /// <summary><c>self healing</c>: untreated damage turns slowly into treated damage (animals and a few robots).</summary>
    public bool SelfHealing { get; init; }

    /// <summary>
    /// The stat multipliers by stat number (index 0 to 38, length <see cref="StatsInfo.Count"/>): 1, 1.2 for the <c>stats good</c> numbers, 0.8 for
    /// <c>stats bad</c>, then RACE <c>strength</c> and <c>dexterity</c> for stats 1 and 18. They scale the starting stats and every XP gain.
    /// </summary>
    public float[] StatMultipliers { get; init; } = NewStatMap();

    /// <summary>The parts, in the order of the race's <c>combat anatomy</c> list (which differs from the saves' usual order).</summary>
    public IReadOnlyList<AnatomyEntry> Anatomy { get; init; } = [];

    public IReadOnlyList<SlotReference> SeveredLimbs { get; init; } = [];
    public IReadOnlyList<SlotReference> LimbReplacements { get; init; } = [];

    public float StatMultiplier(StatsEnumerated stat) => (uint)stat < StatsInfo.Count ? StatMultipliers[(int)stat] : 1;

    /// <summary>A map with every entry 1.</summary>
    public static float[] NewStatMap()
    {
        var map = new float[StatsInfo.Count];
        Array.Fill(map, 1f);
        return map;
    }

    /// <summary>
    /// The race's stat map (<c>FUN_14042efa0</c>, <b>Verified</b>): goods first (1.2), then bads (0.8), then RACE <c>strength</c> and <c>dexterity</c> over
    /// entries 1 and 18. A stat in both lists ends at 0.8; 0 (STAT_NONE) entries and numbers out of range are skipped.
    /// </summary>
    public static float[] BuildStatMap(IEnumerable<int> goods, IEnumerable<int> bads, float strength = 1, float dexterity = 1)
    {
        var map = NewStatMap();
        foreach (int n in goods) if (n > 0 && n < StatsInfo.Count) map[n] = 1.2f;
        foreach (int n in bads) if (n > 0 && n < StatsInfo.Count) map[n] = 0.8f;
        map[(int)StatsEnumerated.Strength] = strength;
        map[(int)StatsEnumerated.Dexterity] = dexterity;
        return map;
    }

    /// <summary>Reads a RACE record; the anatomy references resolve through <paramref name="db"/> (a missing LOCATIONAL_DAMAGE record is left out).</summary>
    public static RaceData From(GameRecord r, GameDatabase db)
    {
        // "stats good0", "stats good1"...: the scan stops at the first key that is missing, but a stored 0 only skips.
        List<int> Numbered(string prefix)
        {
            var list = new List<int>();
            for (int i = 0; r.Ints.TryGetValue(prefix + i, out int n); i++) list.Add(n);
            return list;
        }
        var anatomy = new List<AnatomyEntry>();
        foreach (var reference in r.GetReferences("combat anatomy"))
        {
            if (db.Find(reference.TargetStringId) is not { Type: Fcs.FcsRecordType.LOCATIONAL_DAMAGE } part) continue;
            float hp = reference.Values.Value1;
            anatomy.Add(new AnatomyEntry(BodyPartTemplate.From(part), reference.Values.Value0, hp < 1 ? 100 : hp));
        }
        float waterAvoidance = RecordReading.Float(r, "water avoidance", 0);
        return new RaceData
        {
            StringId = r.StringId,
            Name = r.Name,
            HealRate = RecordReading.Float(r, "heal rate", 1),
            BleedRate = RecordReading.Float(r, "bleed rate", 1),
            HealStat = (StatsEnumerated)RecordReading.Int(r, "heal stat", (int)StatsEnumerated.Medic),
            HungerRate = RecordReading.Float(r, "hunger rate", 1),
            MinBlood = RecordReading.Float(r, "min blood", 75),
            MaxBlood = RecordReading.Float(r, "max blood", 150),
            SpeedMinSkill = RecordReading.Float(r, "speed min skill", 70),
            SpeedMaxSkill = RecordReading.Float(r, "speed max skill", 120),
            WalkSpeed = RecordReading.Float(r, "walk speed", 15),
            SwimSpeedMult = RecordReading.Float(r, "swim speed mult", 1),
            SwimOffset = RecordReading.Float(r, "swim offset", 1),
            WaterAvoidanceRaw = waterAvoidance,
            VisionRangeMult = RecordReading.Float(r, "vision range mult", 1),
            CombatMoveSpeedMult = RecordReading.Float(r, "combat move speed mult", 1),
            PathfindFootprintRadius = RecordReading.Float(r, "pathfind footprint radius", 4),
            PathfindAcceleration = RecordReading.Float(r, "pathfind acceleration", 20),
            ExtraAttackSlots = RecordReading.Int(r, "extra attack slots", 0),
            IsRobot = RecordReading.Bool(r, "is robot", false),
            Swims = RecordReading.Bool(r, "swims", false),
            Carriable = RecordReading.Bool(r, "carriable", false),
            SingleGender = RecordReading.Bool(r, "single gender", false),
            Gigantic = RecordReading.Bool(r, "gigantic", false),
            Vampiric = RecordReading.Bool(r, "vampiric", false),
            NoHats = RecordReading.Bool(r, "no hats", false),
            NoShirts = RecordReading.Bool(r, "no shirts", false),
            NoShoes = RecordReading.Bool(r, "no shoes", false),
            CanEnterBuildings = !RecordReading.Bool(r, "cant enter buildings", false),
            SelfHealing = RecordReading.Bool(r, "self healing", false),
            StatMultipliers = BuildStatMap(Numbered("stats good"), Numbered("stats bad"),
                RecordReading.Float(r, "strength", 1), RecordReading.Float(r, "dexterity", 1)),
            Anatomy = anatomy,
            SeveredLimbs = [.. r.GetReferences("severed limbs").Select(x => new SlotReference(x.Values.Value0, x.TargetStringId))],
            LimbReplacements = [.. r.GetReferences("limb replacement").Select(x => new SlotReference(x.Values.Value0, x.TargetStringId))],
        };
    }
}

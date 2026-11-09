using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Data.Gameplay.Combat;

/// <summary>
/// A COMBAT_TECHNIQUE record: one attack, block or dodge animation with its timings and which weapons it suits (docs/game/combat.md "Technique
/// choice, block and dodge"). Field names <b>Verified</b> against the 44 base records. <b>Observed</b> in the data: <c>anim blocked frame</c>,
/// <c>anim stop frame</c> and <c>acceptable end time</c> are fractions of the animation when <c>num frames</c> is 1 (every sword, block, dodge and
/// creature row) and frame numbers when it is larger (the martial-arts rows: blocked 18 and 31 of 57 frames); the second value of a one-blow
/// technique is a leftover default and is not used.
/// </summary>
public sealed record CombatTechnique
{
    /// <summary>
    /// The COMBAT_TECHNIQUE's string id.
    /// </summary>
    public required string StringId { get; init; }

    /// <summary>
    /// The COMBAT_TECHNIQUE's name in the active language.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// <c>anim name</c>: the animation clip the technique plays.
    /// </summary>
    public string AnimName { get; init; } = "";

    /// <summary>
    /// <c>is block</c>: a block rather than an attack.
    /// </summary>
    public bool IsBlock { get; init; }

    /// <summary>
    /// <c>is dodge</c>.
    /// </summary>
    public bool IsDodge { get; init; }

    /// <summary>
    /// <c>is stumble dodge</c>.
    /// </summary>
    public bool IsStumbleDodge { get; init; }

    /// <summary>
    /// <c>is prone</c>.
    /// </summary>
    public bool IsProne { get; init; }

    /// <summary>
    /// <c>low strike</c>.
    /// </summary>
    public bool LowStrike { get; init; }

    /// <summary>
    /// <c>gains ground</c>.
    /// </summary>
    public bool GainsGround { get; init; }

    /// <summary>
    /// <c>disabled</c>.
    /// </summary>
    public bool Disabled { get; init; }

    /// <summary>
    /// <c>use L arm</c>.
    /// </summary>
    public bool UseLeftArm { get; init; }

    /// <summary>
    /// <c>use R arm</c>.
    /// </summary>
    public bool UseRightArm { get; init; }

    /// <summary>
    /// Weapon kinds the technique is valid for (the record's 1 handed, blunt, hackers, heavy weapons, katanas, polearm, sabre, unarmed flags).
    /// </summary>
    public WeaponKinds Kinds { get; init; }

    /// <summary>
    /// <c>animal</c>: 0 for a person's technique, else the creature's weapon category (9 to 20).
    /// </summary>
    public int Animal { get; init; }

    /// <summary>
    /// <c>num techniques</c>: the number of blows, any count (the reader <c>FUN_1406684c0</c> loops index 1, 2, ... while it is above the index,
    /// <b>Verified</b>); 0 when the record says 0 (no blow is stored, so the limb factor is 0 and the technique is never chosen), 1 when the field is
    /// absent (the editor's default; whether the loaded record carries it is <b>Unknown</b>). Blows 1 and 2 are the paired fields below, blows 3 and
    /// up are <see cref="MoreBlows"/>.
    /// </summary>
    public int Blows { get; init; } = 1;

    /// <summary>
    /// Blows 3, 4, ... (index 0 is blow 3); a blow the list does not reach has the default fields.
    /// </summary>
    public IReadOnlyList<CombatBlow> MoreBlows { get; init; } = [];

    /// <summary>
    /// <c>attack direction 1</c>: blow 1's CutDirection (default 0).
    /// </summary>
    public int AttackDirection1 { get; init; }

    /// <summary>
    /// <c>attack direction 2</c>: blow 2's CutDirection (default 0).
    /// </summary>
    public int AttackDirection2 { get; init; }

    /// <summary>
    /// <c>power 1</c>: blow 1's power (default 100).
    /// </summary>
    public int Power1 { get; init; } = 100;

    /// <summary>
    /// <c>power 2</c>: blow 2's power (default 100).
    /// </summary>
    public int Power2 { get; init; } = 100;

    /// <summary>
    /// <c>limb 1</c>: blow 1's limb (0 left arm, 1 right arm, 2 and 3 legs, 4 the weapon; the reader's default 4).
    /// </summary>
    public int Limb1 { get; init; }

    /// <summary>
    /// <c>limb 2</c>: blow 2's limb (the reader's default 4).
    /// </summary>
    public int Limb2 { get; init; }

    /// <summary>
    /// <c>max simultaneous hits</c> (default 1).
    /// </summary>
    public int MaxSimultaneousHits { get; init; } = 1;

    /// <summary>
    /// <c>chance</c> (default 1).
    /// </summary>
    public float Chance { get; init; } = 1;

    /// <summary>
    /// <c>min skill</c> (default -100).
    /// </summary>
    public float MinSkill { get; init; } = -100;

    /// <summary>
    /// <c>max skill</c> (default 999).
    /// </summary>
    public float MaxSkill { get; init; } = 999;

    /// <summary>
    /// <c>max encumbrance</c> (default 999).
    /// </summary>
    public float MaxEncumbrance { get; init; } = 999;

    /// <summary>
    /// <c>attack distance</c> (default 0).
    /// </summary>
    public float AttackDistance { get; init; }

    /// <summary>
    /// <c>attack distance min vs static</c> (default 0).
    /// </summary>
    public float AttackDistanceMinVsStatic { get; init; }

    /// <summary>
    /// <c>anim hesitate point</c> (default 0).
    /// </summary>
    public float HesitatePoint { get; init; }

    /// <summary>
    /// <c>anim speed mult</c> (default 1).
    /// </summary>
    public float AnimSpeedMult { get; init; } = 1;

    /// <summary>
    /// <c>num frames</c> (default 1): 1 makes the frame fields fractions of the clip, a larger value frame numbers.
    /// </summary>
    public float NumFrames { get; init; } = 1;

    /// <summary>
    /// <c>anim blocked frame 1</c>: where blow 1 arrives (default 1).
    /// </summary>
    public float BlockedFrame1 { get; init; } = 1;

    /// <summary>
    /// <c>anim blocked frame 2</c>: where blow 2 arrives (default 1).
    /// </summary>
    public float BlockedFrame2 { get; init; } = 1;

    /// <summary>
    /// <c>anim stop frame 1</c>: where the clip stops when blow 1 is stopped (default 1).
    /// </summary>
    public float StopFrame1 { get; init; } = 1;

    /// <summary>
    /// <c>anim stop frame 2</c>: where the clip stops when blow 2 is stopped (default 1).
    /// </summary>
    public float StopFrame2 { get; init; } = 1;

    /// <summary>
    /// <c>acceptable end time</c> (default 0.95), as a frame or fraction like the other frame fields (<see cref="EndProgress"/>).
    /// </summary>
    public float AcceptableEndTime { get; init; } = 0.95f;

    /// <summary>
    /// The attack direction (CutDirection) of blow <paramref name="blow"/> (1 or 2).
    /// </summary>
    public int DirectionOf(int blow) => BlowOf(blow).Direction;

    /// <summary>
    /// The power of blow <paramref name="blow"/>.
    /// </summary>
    public int PowerOf(int blow) => BlowOf(blow).Power;

    /// <summary>
    /// The limb (0 left arm, 1 right arm, 2 and 3 legs, 4 the weapon) of blow <paramref name="blow"/>.
    /// </summary>
    public int LimbOf(int blow) => BlowOf(blow).Limb;

    /// <summary>
    /// The stored fields of blow <paramref name="blow"/> (1-based; below 1 reads blow 1).
    /// </summary>
    public CombatBlow BlowOf(int blow) => blow switch
    {
        <= 1 => new CombatBlow(AttackDirection1, Power1, Limb1, BlockedFrame1, StopFrame1),
        2 => new CombatBlow(AttackDirection2, Power2, Limb2, BlockedFrame2, StopFrame2),
        _ => blow - 3 < MoreBlows.Count ? MoreBlows[blow - 3] : CombatBlow.Default,
    };

    /// <summary>
    /// The animation progress (0 to 1) at which blow <paramref name="blow"/> arrives (its <c>anim blocked frame</c>).
    /// </summary>
    public float StrikeProgress(int blow) => ToProgress(BlowOf(blow).BlockedFrame);

    /// <summary>
    /// The progress at which the animation stops when blow <paramref name="blow"/> is stopped (<c>anim stop frame</c>).
    /// </summary>
    public float StopProgress(int blow) => ToProgress(BlowOf(blow).StopFrame);

    /// <summary>
    /// The clip progress under which a clip counts as busy (<c>acceptable end time</c>; 0 never is): the post-swing wait starts at the later of this
    /// and <c>CombatTuning.SwingFinished</c> (docs/game/combat.md "Pacing").
    /// </summary>
    public float EndProgress => ToProgress(AcceptableEndTime);

    /// <summary>
    /// A frame field as clip progress: divided by <see cref="NumFrames"/> when that is above 1, clamped to 0..1.
    /// </summary>
    float ToProgress(float frame) => Math.Clamp(NumFrames > 1 ? frame / NumFrames : frame, 0, 1);

    /// <summary>
    /// Reads a COMBAT_TECHNIQUE record: the weapon-kind flags, every blow (<c>num techniques</c>, default 1) and the defaults named on each property.
    /// </summary>
    public static CombatTechnique From(GameRecord r)
    {
        WeaponKinds kinds = WeaponKinds.None;
        void Flag(string field, WeaponKinds k)
        {
            if (r.GetBool(field)) kinds |= k;
        }

        Flag("1 handed", WeaponKinds.OneHanded);
        Flag("blunt", WeaponKinds.Blunt);
        Flag("hackers", WeaponKinds.Hacker);
        Flag("heavy weapons", WeaponKinds.Heavy);
        Flag("katanas", WeaponKinds.Katana);
        Flag("polearm", WeaponKinds.Polearm);
        Flag("sabre", WeaponKinds.Sabre);
        Flag("unarmed", WeaponKinds.Unarmed);

        int blowCount = RecordReading.Int(r, "num techniques", 1);
        return new CombatTechnique
        {
            StringId = r.StringId,
            Name = r.Name,
            AnimName = r.GetString("anim name"),
            IsBlock = r.GetBool("is block"),
            IsDodge = r.GetBool("is dodge"),
            IsStumbleDodge = r.GetBool("is stumble dodge"),
            IsProne = r.GetBool("is prone"),
            LowStrike = r.GetBool("low strike"),
            GainsGround = r.GetBool("gains ground"),
            Disabled = r.GetBool("disabled"),
            UseLeftArm = r.GetBool("use L arm"),
            UseRightArm = r.GetBool("use R arm"),
            Kinds = kinds,
            Animal = RecordReading.Int(r, "animal", 0),
            Blows = Math.Max(blowCount, 0),
            MoreBlows = [.. Enumerable.Range(3, Math.Max(blowCount - 2, 0)).Select(n => ReadBlow(r, n))],
            AttackDirection1 = RecordReading.Int(r, "attack direction 1", 0),
            AttackDirection2 = RecordReading.Int(r, "attack direction 2", 0),
            Power1 = RecordReading.Int(r, "power 1", 100),
            Power2 = RecordReading.Int(r, "power 2", 100),
            Limb1 = RecordReading.Int(r, "limb 1", 4),
            Limb2 = RecordReading.Int(r, "limb 2", 4),
            MaxSimultaneousHits = RecordReading.Int(r, "max simultaneous hits", 1),
            Chance = RecordReading.Float(r, "chance", 1),
            MinSkill = RecordReading.Float(r, "min skill", -100),
            MaxSkill = RecordReading.Float(r, "max skill", 999),
            MaxEncumbrance = RecordReading.Float(r, "max encumbrance", 999),
            AttackDistance = RecordReading.Float(r, "attack distance", 0),
            AttackDistanceMinVsStatic = RecordReading.Float(r, "attack distance min vs static", 0),
            HesitatePoint = RecordReading.Float(r, "anim hesitate point", 0),
            AnimSpeedMult = RecordReading.Float(r, "anim speed mult", 1),
            NumFrames = RecordReading.Float(r, "num frames", 1),
            BlockedFrame1 = RecordReading.Float(r, "anim blocked frame 1", 1),
            BlockedFrame2 = RecordReading.Float(r, "anim blocked frame 2", 1),
            StopFrame1 = RecordReading.Float(r, "anim stop frame 1", 1),
            StopFrame2 = RecordReading.Float(r, "anim stop frame 2", 1),
            AcceptableEndTime = RecordReading.Float(r, "acceptable end time", 0.95f),
        };
    }

    /// <summary>
    /// Reads blow <paramref name="n"/> (3 and up) from its numbered fields, with the editor's defaults.
    /// </summary>
    static CombatBlow ReadBlow(GameRecord r, int n) => new(
        RecordReading.Int(r, $"attack direction {n}", 0),
        RecordReading.Int(r, $"power {n}", 100),
        RecordReading.Int(r, $"limb {n}", 4),
        RecordReading.Float(r, $"anim blocked frame {n}", 1),
        RecordReading.Float(r, $"anim stop frame {n}", 1));
}

/// <summary>
/// One blow of a technique: the stored record of the reader (<c>FUN_1406684c0</c>), its defaults those of the editor (power 100, limb 4 the weapon,
/// frames 1).
/// </summary>
public readonly record struct CombatBlow(int Direction, int Power, int Limb, float BlockedFrame, float StopFrame)
{
    /// <summary>
    /// A blow with every field at the editor's default (direction 0, power 100, limb 4, frames 1).
    /// </summary>
    public static readonly CombatBlow Default = new(0, 100, 4, 1, 1);
}

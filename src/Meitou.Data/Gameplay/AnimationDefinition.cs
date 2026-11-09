using Meitou.Data.Characters;

namespace Meitou.Data.Gameplay;

/// <summary>
/// One ANIMATION record as the simulation reads it (field meanings from fcs.def; docs/animation.md "Animation definitions"). <b>Verified</b>: the
/// field names and defaults against fcs.def; <b>Observed</b>: the matching rules in <see cref="AnimationLibrary"/>.
/// </summary>
public sealed record AnimationDefinition
{
    /// <summary>
    /// The idle weight factor when the held weapon type is unarmed (<see cref="IdleWeight"/>): the loaded constant <c>DAT_14167fad0</c>.
    /// </summary>
    public const float UnarmedHeldYesFactor = 0.1f;

    /// <summary>
    /// The record's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The record's name: what the renderer resolves to this record's track masks (<see cref="AnimationMask.Find"/>).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>anim name</c>: the skeleton animation it plays.
    /// </summary>
    public required string Clip { get; init; }

    /// <summary>
    /// <c>layer</c> (default upper): the body part the clip plays on.
    /// </summary>
    public AnimationArea Area { get; init; }

    /// <summary>
    /// <c>idle</c>.
    /// </summary>
    public bool Idle { get; init; }

    /// <summary>
    /// <c>is action</c>.
    /// </summary>
    public bool IsAction { get; init; }

    /// <summary>
    /// <c>disables movement</c>: while this is the character's action clip it cannot move along waypoints
    /// (<c>AnimationData::restrictsMovementOrders</c>, <b>Verified</b>).
    /// </summary>
    public bool DisablesMovement { get; init; }

    /// <summary>
    /// <c>loop</c>.
    /// </summary>
    public bool Loop { get; init; }

    /// <summary>
    /// <c>synchs</c>.
    /// </summary>
    public bool Synchs { get; init; }

    /// <summary>
    /// <c>synch offset</c>.
    /// </summary>
    public float SynchOffset { get; init; }

    /// <summary>
    /// The speed this movement clip travels at (<c>move speed</c>, units per second); 0 for an idle or an action.
    /// </summary>
    public float MoveSpeed { get; init; }

    /// <summary>
    /// <c>play speed</c>: a clip's rate multiplier; for movement clips it is multiplied by the movement speed.
    /// </summary>
    public float PlaySpeed { get; init; }

    /// <summary>
    /// <c>idle chance</c> (relative) and the seconds an idle plays before another is chosen.
    /// </summary>
    public float IdleChance { get; init; }

    /// <summary>
    /// <c>idle time min</c> (seconds).
    /// </summary>
    public float IdleTimeMin { get; init; }

    /// <summary>
    /// <c>idle time max</c> (seconds).
    /// </summary>
    public float IdleTimeMax { get; init; }

    /// <summary>
    /// <c>chance</c>.
    /// </summary>
    public int Chance { get; init; }

    /// <summary>
    /// <c>has weapon L/R</c>: 0 NO, 1 YES, 2 EITHER (the Either enum).
    /// </summary>
    public int WeaponLeft { get; init; }

    /// <summary>
    /// <c>has weapon R</c>: 0 NO, 1 YES, 2 EITHER.
    /// </summary>
    public int WeaponRight { get; init; }

    /// <summary>
    /// <c>is combat mode</c>: 0 only out of combat mode, 1 only in it, 2 either.
    /// </summary>
    public int CombatMode { get; init; }

    /// <summary>
    /// <c>stealth mode</c>: 0 only out of stealth mode, 1 only in it, 2 either.
    /// </summary>
    public int StealthMode { get; init; }

    /// <summary>
    /// <c>crouched</c>: the posture must match.
    /// </summary>
    public bool Crouched { get; init; }

    /// <summary>
    /// <c>prone</c>: the posture must match.
    /// </summary>
    public bool Prone { get; init; }

    /// <summary>
    /// <c>carrying left</c>: must match on the upper and overlay picks.
    /// </summary>
    public bool CarryingLeft { get; init; }

    /// <summary>
    /// <c>carrying right</c>: must match on the upper and overlay picks.
    /// </summary>
    public bool CarryingRight { get; init; }

    /// <summary>
    /// <c>being carried</c>: must match on the upper and overlay picks.
    /// </summary>
    public bool BeingCarried { get; init; }

    /// <summary>
    /// <c>uses left arm</c> / <c>uses right arm</c> (record bytes <c>+0x9f</c> / <c>+0x9e</c>): the layer holds that arm, which the injury hold
    /// (<c>FUN_14051fe60</c>) and the carrying arm check.
    /// </summary>
    public bool UsesLeftArm { get; init; }

    /// <summary>
    /// <c>uses right arm</c> (record byte <c>+0x9e</c>): the layer holds the right arm.
    /// </summary>
    public bool UsesRightArm { get; init; }

    /// <summary>
    /// The weapon kinds the clip suits (the <c>katanas</c>, <c>sabre</c>, <c>blunt</c>, <c>heavy weapons</c>, <c>hackers</c>, <c>polearm</c>,
    /// <c>unarmed</c>, <c>1 handed</c> flags).
    /// </summary>
    public WeaponKinds Kinds { get; init; }

    /// <summary>
    /// <c>L leg damage min</c>: the lowest left leg health the clip fits (<see cref="LegFloor"/>; 1000 with the max: not used).
    /// </summary>
    public float LeftLegMin { get; init; }

    /// <summary>
    /// <c>L leg damage max</c>: the highest left leg health the clip fits (<see cref="LegCeil"/>).
    /// </summary>
    public float LeftLegMax { get; init; }

    /// <summary>
    /// <c>R leg damage min</c>: the lowest right leg health the clip fits (<see cref="LegFloor"/>; 1000 with the max: not used).
    /// </summary>
    public float RightLegMin { get; init; }

    /// <summary>
    /// <c>R leg damage max</c>: the highest right leg health the clip fits (<see cref="LegCeil"/>).
    /// </summary>
    public float RightLegMax { get; init; }

    /// <summary>
    /// <c>min speed</c> / <c>max speed</c>: where the speed weight of a movement clip starts rising and has fallen back to 0 (docs/animation.md
    /// "Choosing candidates").
    /// </summary>
    public float MinSpeed { get; init; }

    /// <summary>
    /// <c>max speed</c> (see <see cref="MinSpeed"/>).
    /// </summary>
    public float MaxSpeed { get; init; }

    /// <summary>
    /// <c>strafe speed max</c>: on a lower clip with no <c>move speed</c>, the side-step clip of a fighter (<c>strafe lower</c>, docs/animation.md
    /// "Side steps"); 0 on the walking clips.
    /// </summary>
    public float StrafeSpeedMax { get; init; }

    /// <summary>
    /// <c>L/R leg damage ideal</c>: the leg health the leg weight peaks at.
    /// </summary>
    public float LeftLegIdeal { get; init; } = 100;

    /// <summary>
    /// <c>R leg damage ideal</c>: the right leg health the leg weight peaks at (default 100).
    /// </summary>
    public float RightLegIdeal { get; init; } = 100;

    /// <summary>
    /// Hit reactions: <c>stumbles</c> (the LOCATIONAL_DAMAGE string ids of the body parts whose hit plays it), <c>big stumble</c> and <c>stumble
    /// from</c> (0 front, 1 rear).
    /// </summary>
    public IReadOnlyList<string> Stumbles { get; init; } = [];

    /// <summary>
    /// <c>big stumble</c> (default true).
    /// </summary>
    public bool BigStumble { get; init; }

    /// <summary>
    /// <c>stumble from</c>: 0 front, 1 rear.
    /// </summary>
    public int StumbleFrom { get; init; }

    /// <summary>
    /// <c>category</c> (CharacterAnimCategory: 0 ANIM_NORMAL ... 8 RANGED). Only category 0 is chosen as movement, idle or stumble; the RANGED clips
    /// (crossbow aim and reload) are played by the combat system's timing like techniques.
    /// </summary>
    public int Category { get; init; }

    /// <summary>
    /// The category a weather-type record (<see cref="AnimationLibrary.WeatherCategory"/>, taken by no selector but the stumble pick) really has;
    /// null for every other record.
    /// </summary>
    public int? StumbleCategory { get; init; }

    /// <summary>
    /// A COMBAT_TECHNIQUE (attack, block or dodge): played by the combat system's timing, never chosen as movement or idle.
    /// </summary>
    public bool Technique { get; init; }

    /// <summary>
    /// The clip moves the character (<see cref="Characters.AnimationMask.MovesCharacter"/>): the renderer keeps only the height of its <c>Bip01</c>
    /// track and the simulation moves the character by the rest (<see cref="AnimationLengths.Root"/>).
    /// </summary>
    public bool Relocates { get; init; }

    /// <summary>
    /// An ANIMAL_ANIMATION as a definition (docs/game/animals.md "Animations"): read like an ANIMATION, but on the whole body (every animal record
    /// says <c>lower</c>; an animal has no body halves), published under its clip (<c>anim name</c>: the records share names, two
    /// <c>sleeponfloor</c>s play <c>sleep</c> and <c>gotosleep</c>, and the renderer finds the clip in the animal's own skeleton), and in
    /// <see cref="AnimationLibrary.AnimalCategory"/> so no humanoid selector takes it.
    /// </summary>
    public static AnimationDefinition FromAnimal(GameRecord r) => From(r) with
    {
        Name = r.GetString("anim name", "") is { Length: > 0 } clip ? clip : r.Name,
        Area = AnimationArea.All,
        Category = AnimationLibrary.AnimalCategory,
        Relocates = false,
        WeaponLeft = 2,
        WeaponRight = 2,
        CombatMode = 2,
        Kinds = WeaponKinds.All,
        LeftLegMin = 1000,
        LeftLegMax = 1000,
        RightLegMin = 1000,
        RightLegMax = 1000,
    };

    /// <summary>
    /// A COMBAT_TECHNIQUE as a definition: its <c>anim name</c>, blocks on the upper body (their lower-body tracks are deleted), the rest on the
    /// whole body.
    /// </summary>
    public static AnimationDefinition FromTechnique(GameRecord r) => new()
    {
        Id = r.StringId,
        Name = r.Name,
        Clip = r.GetString("anim name", ""),
        Area = r.GetBool("is block") && !r.GetBool("is dodge") ? AnimationArea.Upper : AnimationArea.All,
        IsAction = true,
        Technique = true,
        Relocates = AnimationMask.MovesCharacter(r),
        PlaySpeed = 1,
        WeaponLeft = 2,
        WeaponRight = 2,
        CombatMode = 2,
        StealthMode = 2,
        Kinds = WeaponKinds.All,
        LeftLegMin = 1000,
        LeftLegMax = 1000,
        RightLegMin = 1000,
        RightLegMax = 1000,
    };

    /// <summary>
    /// Reads an ANIMATION record (unset numbers 0, <c>layer</c> upper, <c>big stumble</c> true, leg ideals 100).
    /// </summary>
    public static AnimationDefinition From(GameRecord r)
    {
        WeaponKinds kinds = WeaponKinds.None;
        void Flag(string field, WeaponKinds k)
        {
            if (r.GetBool(field)) kinds |= k;
        }
        Flag("katanas", WeaponKinds.Katana);
        Flag("sabre", WeaponKinds.Sabre);
        Flag("blunt", WeaponKinds.Blunt);
        Flag("heavy weapons", WeaponKinds.Heavy);
        Flag("hackers", WeaponKinds.Hacker);
        Flag("polearm", WeaponKinds.Polearm);
        Flag("unarmed", WeaponKinds.Unarmed);
        Flag("1 handed", WeaponKinds.OneHanded);
        return new AnimationDefinition
        {
            Id = r.StringId,
            Name = r.Name,
            Clip = r.GetString("anim name", ""),
            Area = r.GetString("layer", "upper") switch
            {
                "all" => AnimationArea.All,
                "lower" => AnimationArea.Lower,
                "upper" => AnimationArea.Upper,
                "overlay" => AnimationArea.Overlay,
                _ => AnimationArea.Other,
            },
            Idle = r.GetBool("idle"),
            IsAction = r.GetBool("is action"),
            DisablesMovement = r.GetBool("disables movement"),
            Loop = r.GetBool("loop"),
            Synchs = r.GetBool("synchs"),
            SynchOffset = r.GetFloat("synch offset"),
            MoveSpeed = r.GetFloat("move speed"),
            PlaySpeed = r.GetFloat("play speed"),
            IdleChance = r.GetInt("idle chance"),
            IdleTimeMin = r.GetInt("idle time min"),
            IdleTimeMax = r.GetInt("idle time max"),
            Chance = r.GetInt("chance"),
            WeaponLeft = r.GetInt("has weapon L"),
            WeaponRight = r.GetInt("has weapon R"),
            CombatMode = r.GetInt("is combat mode"),
            StealthMode = r.GetInt("stealth mode"),
            Crouched = r.GetBool("crouched"),
            Prone = r.GetBool("prone"),
            CarryingLeft = r.GetBool("carrying left"),
            CarryingRight = r.GetBool("carrying right"),
            BeingCarried = r.GetBool("being carried"),
            UsesLeftArm = r.GetBool("uses left arm"),
            UsesRightArm = r.GetBool("uses right arm"),
            Kinds = kinds,
            LeftLegMin = r.GetFloat("L leg damage min"),
            LeftLegMax = r.GetFloat("L leg damage max"),
            RightLegMin = r.GetFloat("R leg damage min"),
            RightLegMax = r.GetFloat("R leg damage max"),
            Relocates = AnimationMask.MovesCharacter(r),
            MinSpeed = r.GetFloat("min speed"),
            MaxSpeed = r.GetFloat("max speed"),
            StrafeSpeedMax = r.GetFloat("strafe speed max"),
            LeftLegIdeal = r.GetFloat("L leg damage ideal", 100),
            RightLegIdeal = r.GetFloat("R leg damage ideal", 100),
            Stumbles = [.. r.GetReferences("stumbles").Select(x => x.TargetStringId)],
            Category = r.GetInt("category"),
            BigStumble = r.GetBool("big stumble", true),
            StumbleFrom = r.GetInt("stumble from"),
        };
    }

    /// <summary>
    /// The speed weight of a movement clip at speed <paramref name="v"/> (<b>Verified</b>, docs/animation.md "Choosing candidates"): a ramp rising
    /// from <c>min speed</c> to <c>move speed</c> (1 throughout when <c>min speed</c> is under 1), falling to 0 at <c>max speed</c> (when <c>max
    /// speed</c> is under 0.5 the line goes on past it: 1 for a max of 0, 0 at forward speeds for a backing-off clip), 0 outside, sharpened by 1 / (1
    /// + e^(-10 (2r - 1))); 1 when both the speed and <c>min speed</c> are under -40.
    /// </summary>
    public float SpeedWeight(float v)
    {
        if (v < -40 && MinSpeed < -40) return 1;
        float r = Ramp(v, MinSpeed, MoveSpeed, MaxSpeed, MinSpeed < 1, MaxSpeed < 0.5f);
        return r > 0 ? 1 / (1 + MathF.Exp(-10 * (2 * r - 1))) : 0;
    }

    /// <summary>
    /// The leg weight (<b>Verified</b>): the same ramp, without the sigmoid, over the health of the more hurt leg against that side's damage min /
    /// ideal / max, a min under 1 read as -101 and a max over 99 as 101 (the loader's rule).
    /// </summary>
    public float LegWeight(float left, float right)
    {
        bool useLeft = left <= right;
        float health = useLeft ? left : right;
        float min = LegFloor(useLeft ? LeftLegMin : RightLegMin);
        float ideal = useLeft ? LeftLegIdeal : RightLegIdeal;
        float max = LegCeil(useLeft ? LeftLegMax : RightLegMax);
        if (health < -40 && min < -40) return 1;   // the ramp's own shortcut (FUN_14051cd10): a value and a minimum both under -40
        return Ramp(health, min, ideal, max, min < 1, false);
    }

    /// <summary>
    /// The ramp of <paramref name="v"/>: 0 below <paramref name="min"/>, rising to 1 at <paramref name="peak"/> (1 throughout when
    /// <paramref name="flatBelow"/>), falling to 0 at <paramref name="max"/>; with <paramref name="lineAbove"/> the fall goes on along the line past
    /// max, clamped to 0..1.
    /// </summary>
    static float Ramp(float v, float min, float peak, float max, bool flatBelow, bool lineAbove)
    {
        if (v < min) return 0;
        if (v <= peak) return flatBelow || peak <= min ? 1 : (v - min) / (peak - min);
        // Past the peak the original (FUN_14051cd10) falls along the line through (peak, 1) and (max, 0), also beyond max when max is under 0.5: with
        // a max left at 0 that line rises (the fastest clip stays at 1), with a negative max (a backing-off clip) it falls on (0 at forward speeds).
        if (lineAbove) return max == peak ? 0 : Math.Clamp(1 - (v - peak) / (max - peak), 0, 1);
        if (v >= max) return 0;
        return max <= peak ? 1 : (max - v) / (max - peak);
    }

    /// <summary>
    /// The loader's reading of a record's leg range (<b>Verified</b>, docs/animation.md "Choosing candidates"): a min under 1 is -101 and a max over
    /// 99 is 101, so a leg already below 0 (limping, not yet collapsed) still fits the records that say 0 .. 100.
    /// </summary>
    public static float LegFloor(float min) => min < 1 ? -101 : min;

    /// <summary>
    /// The loader's reading of a record's leg max: over 99 is 101.
    /// </summary>
    public static float LegCeil(float max) => max > 99 ? 101 : max;

    /// <summary>
    /// A leg range of 1000 .. 1000 is the records' "not used" (the limp clips constrain one leg only; <b>Observed</b>); else the range as the loader
    /// reads it.
    /// </summary>
    static bool Leg(float health, float min, float max) => min == 1000 && max == 1000 || health >= LegFloor(min) && health <= LegCeil(max);

    /// <summary>
    /// Whether the record can play for <paramref name="s"/>: the weapon in each hand (NO / YES / EITHER, and the held weapon's kind among the
    /// record's kinds), combat and stealth modes, posture, carrying and the leg damage ranges.
    /// </summary>
    public bool Fits(in AnimationStance s) =>
        FitsExceptLegs(s) && Leg(s.LeftLeg, LeftLegMin, LeftLegMax) && Leg(s.RightLeg, RightLegMin, RightLegMax);

    /// <summary>
    /// <see cref="Fits"/> without the leg ranges (movement clips weigh the legs with <see cref="LegWeight"/> instead).
    /// </summary>
    public bool FitsExceptLegs(in AnimationStance s)
    {
        return (Area == AnimationArea.Lower || ArmsFit(s) && KindFits(s))
            && Mode(CombatMode, s.Combat) && Mode(StealthMode, s.Stealth)
            && Crouched == s.Crouched && Prone == s.Prone
            // The carrying booleans are tested on upper and overlay picks only (docs/animation.md): a carrier's legs walk with the ordinary lower
            // clips.
            && (Area == AnimationArea.Lower || CarryingLeft == s.CarryingLeft && CarryingRight == s.CarryingRight && BeingCarried == s.BeingCarried);
    }

    /// <summary>
    /// The arm check of the original (<c>FUN_14051d240</c>, <b>Verified</b>: it tests the layer index, 1 for upper <i>and</i> whole-body records, 2
    /// overlay, and lets only the lower one pass): <c>has weapon L</c> and <c>R</c> both YES mean exactly one hand holds a weapon, else each hand's
    /// YES / NO must match what it holds (EITHER passes). The lower layer is exempt.
    /// </summary>
    public bool ArmsFit(in AnimationStance s)
    {
        if (Area == AnimationArea.Lower) return true;
        bool left = s.Left.Holds, right = s.Right.Holds;
        if (WeaponLeft == 1 && WeaponRight == 1) return left != right;
        return (WeaponLeft == 2 || WeaponLeft == (left ? 1 : 0)) && (WeaponRight == 2 || WeaponRight == (right ? 1 : 0));
    }

    /// <summary>
    /// The weapon type held must be one of the record's weapon kinds, for the upper, whole-body and overlay layers (<b>Verified</b>, the idle draw
    /// <c>@ 14051ec80</c>); fists (type 5) skip the test.
    /// </summary>
    public bool KindFits(in AnimationStance s) => HeldKind(s) is var held && (held == WeaponKinds.Unarmed || (Kinds & held) != 0);

    /// <summary>
    /// The kind of the weapon held: the right hand's, else the left's, else unarmed.
    /// </summary>
    static WeaponKinds HeldKind(in AnimationStance s) => s.Right.Holds ? s.Right.Weapon : s.Left.Holds ? s.Left.Weapon : WeaponKinds.Unarmed;

    /// <summary>
    /// The weight an idle pick gives the record (<c>FUN_14051ec80</c> and <c>FUN_14051d660</c>, <b>Verified</b>): <c>idle chance</c> (negative counts
    /// 0), times 0.1 on the upper, whole-body and overlay layers when the held weapon type is unarmed (5) and <c>has weapon L</c> or <c>R</c> is
    /// exactly YES (a hand then holds an item whose animation kind is 5; the arm check already drops those when both hands are empty).
    /// </summary>
    public float IdleWeight(in AnimationStance s)
    {
        bool unarmedHeld = Area != AnimationArea.Lower && HeldKind(s) == WeaponKinds.Unarmed && (WeaponLeft == 1 || WeaponRight == 1);
        return Math.Max(IdleChance, 0) * (unarmedHeld ? UnarmedHeldYesFactor : 1f);
    }

    /// <summary>
    /// Whether the record can play as a character's own <c>idle stance</c> (the original's idle pick <c>FUN_14051f230</c>, <b>Verified</b> by
    /// decompilation, docs/animation.md "Idle stance"): normal category, the combat and stealth modes fit, and the arm check, so a character with a
    /// weapon in hand does not stand in the stance of the empty-handed (it draws from the idle list: <c>stand 1 sword</c>). No other test (legs,
    /// kinds, carrying, posture).
    /// </summary>
    public bool FitsAsStance(in AnimationStance s) =>
        Category == 0 && !s.BeingCarried && Mode(CombatMode, s.Combat) && Mode(StealthMode, s.Stealth) && ArmsFit(s);

    /// <summary>
    /// Whether a mode field (0 off, 1 on, 2 either) passes for the character's mode <paramref name="on"/>.
    /// </summary>
    static bool Mode(int wants, bool on) => wants switch { 0 => !on, 1 => on, _ => true };
}

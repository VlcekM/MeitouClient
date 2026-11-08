using Meitou.Content;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;

namespace Meitou.Data.Gameplay;

/// <summary>The body area an ANIMATION plays on (<c>layer</c>: all, lower, upper, overlay).</summary>
public enum AnimationArea : byte { All, Lower, Upper, Overlay, Other }

/// <summary>The weapon kinds an ANIMATION's validity flags name (katanas, sabre, blunt, heavy weapons, hackers, polearm, unarmed, 1 handed).</summary>
[Flags]
public enum WeaponKinds : ushort
{
    None = 0,
    Katana = 1, Sabre = 2, Blunt = 4, Heavy = 8, Hacker = 16, Polearm = 32, Unarmed = 64, OneHanded = 128,
    All = 255,
}

/// <summary>What is in a hand: nothing, or a weapon of some kind (the animation records' <c>has weapon L/R</c> asks for this).</summary>
public readonly record struct HandHold(WeaponKinds Weapon)
{
    public static readonly HandHold Empty = new(WeaponKinds.None);
    public bool Holds => Weapon != WeaponKinds.None;

    /// <summary>The kind of a WEAPON's <c>skill category</c> (WeaponCategory: 0 katanas, 1 sabres, 2 blunt, 3 heavy, 4 hackers, 8 polearms; docs/game/combat.md).</summary>
    public static HandHold OfCategory(int skillCategory) => new(Combat.WeaponCategories.AnimationKind(skillCategory));
}

/// <summary>
/// The state an animation is chosen for besides the speed: what the hands hold, combat and stealth, posture, carrying and the legs.
/// The default is a healthy character walking about with its weapons sheathed.
/// </summary>
public readonly record struct AnimationStance
{
    public HandHold Left { get; init; }
    public HandHold Right { get; init; }
    public bool Combat { get; init; }
    public bool Stealth { get; init; }
    public bool Crouched { get; init; }
    public bool Prone { get; init; }
    public bool CarryingLeft { get; init; }
    public bool CarryingRight { get; init; }
    public bool BeingCarried { get; init; }
    /// <summary>Health of the legs in per cent (100 healthy, negative past the point of function); compared with the records' leg damage ranges.</summary>
    public float LeftLeg { get; init; }
    public float RightLeg { get; init; }
    /// <summary>The animation class's current category (<c>AC+0xf0</c>, <b>Verified</b> FUN_140520500): 4 in deep water for a race that swims, 5 held down or crawling, else 0. Only the hit-reaction pick tests it.</summary>
    public int Category { get; init; }

    public AnimationStance() { LeftLeg = 100; RightLeg = 100; }
}

/// <summary>
/// One ANIMATION record as the simulation reads it (field meanings from fcs.def; docs/animation.md "Animation definitions").
/// <b>Verified</b>: the field names and defaults against fcs.def; <b>Observed</b>: the matching rules in <see cref="AnimationLibrary"/>.
/// </summary>
public sealed record AnimationDefinition
{
    public required string Id { get; init; }
    /// <summary>The record's name: what the renderer resolves to this record's track masks (<see cref="AnimationMask.Find"/>).</summary>
    public required string Name { get; init; }
    /// <summary><c>anim name</c>: the skeleton animation it plays.</summary>
    public required string Clip { get; init; }
    public AnimationArea Area { get; init; }
    public bool Idle { get; init; }
    public bool IsAction { get; init; }
    /// <summary><c>disables movement</c>: while this is the character's action clip it cannot move along waypoints (<c>AnimationData::restrictsMovementOrders</c>, <b>Verified</b>).</summary>
    public bool DisablesMovement { get; init; }
    public bool Loop { get; init; }
    public bool Synchs { get; init; }
    public float SynchOffset { get; init; }
    /// <summary>The speed this movement clip travels at (<c>move speed</c>, units per second); 0 for an idle or an action.</summary>
    public float MoveSpeed { get; init; }
    /// <summary><c>play speed</c>: a clip's rate multiplier; for movement clips it is multiplied by the movement speed.</summary>
    public float PlaySpeed { get; init; }
    /// <summary><c>idle chance</c> (relative) and the seconds an idle plays before another is chosen.</summary>
    public float IdleChance { get; init; }
    public float IdleTimeMin { get; init; }
    public float IdleTimeMax { get; init; }
    public int Chance { get; init; }
    /// <summary><c>has weapon L/R</c>: 0 NO, 1 YES, 2 EITHER (the Either enum).</summary>
    public int WeaponLeft { get; init; }
    public int WeaponRight { get; init; }
    public int CombatMode { get; init; }
    public int StealthMode { get; init; }
    public bool Crouched { get; init; }
    public bool Prone { get; init; }
    public bool CarryingLeft { get; init; }
    public bool CarryingRight { get; init; }
    public bool BeingCarried { get; init; }
    /// <summary><c>uses left arm</c> / <c>uses right arm</c> (record bytes <c>+0x9f</c> / <c>+0x9e</c>): the layer holds that arm, which the injury hold (<c>FUN_14051fe60</c>) and the carrying arm check.</summary>
    public bool UsesLeftArm { get; init; }
    public bool UsesRightArm { get; init; }
    public WeaponKinds Kinds { get; init; }
    public float LeftLegMin { get; init; }
    public float LeftLegMax { get; init; }
    public float RightLegMin { get; init; }
    public float RightLegMax { get; init; }
    /// <summary><c>min speed</c> / <c>max speed</c>: where the speed weight of a movement clip starts rising and has fallen back to 0 (docs/animation.md "Choosing candidates").</summary>
    public float MinSpeed { get; init; }
    public float MaxSpeed { get; init; }
    /// <summary><c>strafe speed max</c>: on a lower clip with no <c>move speed</c>, the side-step clip of a fighter (<c>strafe lower</c>, docs/animation.md "Side steps"); 0 on the walking clips.</summary>
    public float StrafeSpeedMax { get; init; }
    /// <summary><c>L/R leg damage ideal</c>: the leg health the leg weight peaks at.</summary>
    public float LeftLegIdeal { get; init; } = 100;
    public float RightLegIdeal { get; init; } = 100;
    /// <summary>Hit reactions: <c>stumbles</c> (the LOCATIONAL_DAMAGE string ids of the body parts whose hit plays it), <c>big stumble</c> and <c>stumble from</c> (0 front, 1 rear).</summary>
    public IReadOnlyList<string> Stumbles { get; init; } = [];
    public bool BigStumble { get; init; }
    public int StumbleFrom { get; init; }
    /// <summary><c>category</c> (CharacterAnimCategory: 0 ANIM_NORMAL ... 8 RANGED). Only category 0 is chosen as movement, idle or stumble; the RANGED clips (crossbow aim and reload) are played by the combat system's timing like techniques.</summary>
    public int Category { get; init; }
    /// <summary>The category a weather-type record (<see cref="AnimationLibrary.WeatherCategory"/>, taken by no selector but the stumble pick) really has; null for every other record.</summary>
    public int? StumbleCategory { get; init; }
    /// <summary>A COMBAT_TECHNIQUE (attack, block or dodge): played by the combat system's timing, never chosen as movement or idle.</summary>
    public bool Technique { get; init; }
    /// <summary>
    /// The clip moves the character (<see cref="Characters.AnimationMask.MovesCharacter"/>): the renderer keeps only the height of its <c>Bip01</c> track and the
    /// simulation moves the character by the rest (<see cref="AnimationLengths.Root"/>).
    /// </summary>
    public bool Relocates { get; init; }

    /// <summary>
    /// An ANIMAL_ANIMATION as a definition (docs/game/animals.md "Animations"): read like an ANIMATION, but on the whole body (every animal record says <c>lower</c>; an
    /// animal has no body halves), published under its clip (<c>anim name</c>: the records share names, two <c>sleeponfloor</c>s play <c>sleep</c> and <c>gotosleep</c>,
    /// and the renderer finds the clip in the animal's own skeleton), and in <see cref="AnimationLibrary.AnimalCategory"/> so no humanoid selector takes it.
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

    /// <summary>A COMBAT_TECHNIQUE as a definition: its <c>anim name</c>, blocks on the upper body (their lower-body tracks are deleted), the rest on the whole body.</summary>
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

    public static AnimationDefinition From(GameRecord r)
    {
        WeaponKinds kinds = WeaponKinds.None;
        void Flag(string field, WeaponKinds k) { if (r.GetBool(field)) kinds |= k; }
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
            Area = r.GetString("layer", "upper") switch { "all" => AnimationArea.All, "lower" => AnimationArea.Lower, "upper" => AnimationArea.Upper, "overlay" => AnimationArea.Overlay, _ => AnimationArea.Other },
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
    /// The speed weight of a movement clip at speed <paramref name="v"/> (<b>Verified</b>, docs/animation.md "Choosing candidates"): a ramp rising from
    /// <c>min speed</c> to <c>move speed</c> (1 throughout when <c>min speed</c> is under 1), falling to 0 at <c>max speed</c> (when <c>max speed</c> is under 0.5
    /// the line goes on past it: 1 for a max of 0, 0 at forward speeds for a backing-off clip), 0 outside, sharpened by 1 / (1 + e^(-10 (2r - 1))); 1 when both the speed and <c>min speed</c> are under -40.
    /// </summary>
    public float SpeedWeight(float v)
    {
        if (v < -40 && MinSpeed < -40) return 1;
        float r = Ramp(v, MinSpeed, MoveSpeed, MaxSpeed, MinSpeed < 1, MaxSpeed < 0.5f);
        return r > 0 ? 1 / (1 + MathF.Exp(-10 * (2 * r - 1))) : 0;
    }

    /// <summary>
    /// The leg weight (<b>Verified</b>): the same ramp, without the sigmoid, over the health of the more hurt leg against that side's damage min / ideal / max,
    /// a min under 1 read as -101 and a max over 99 as 101 (the loader's rule).
    /// </summary>
    public float LegWeight(float left, float right)
    {
        bool useLeft = left <= right;
        float health = useLeft ? left : right;
        float min = LegFloor(useLeft ? LeftLegMin : RightLegMin), ideal = useLeft ? LeftLegIdeal : RightLegIdeal, max = LegCeil(useLeft ? LeftLegMax : RightLegMax);
        if (health < -40 && min < -40) return 1;   // the ramp's own shortcut (FUN_14051cd10): a value and a minimum both under -40
        return Ramp(health, min, ideal, max, min < 1, false);
    }

    static float Ramp(float v, float min, float peak, float max, bool flatBelow, bool lineAbove)
    {
        if (v < min) return 0;
        if (v <= peak) return flatBelow || peak <= min ? 1 : (v - min) / (peak - min);
        // Past the peak the original (FUN_14051cd10) falls along the line through (peak, 1) and (max, 0), also beyond max when max is under 0.5: with a max
        // left at 0 that line rises (the fastest clip stays at 1), with a negative max (a backing-off clip) it falls on (0 at forward speeds).
        if (lineAbove) return max == peak ? 0 : Math.Clamp(1 - (v - peak) / (max - peak), 0, 1);
        if (v >= max) return 0;
        return max <= peak ? 1 : (max - v) / (max - peak);
    }

    /// <summary>The loader's reading of a record's leg range (<b>Verified</b>, docs/animation.md "Choosing candidates"): a min under 1 is -101 and a max over 99 is 101, so a leg already below 0 (limping, not yet collapsed) still fits the records that say 0 .. 100.</summary>
    public static float LegFloor(float min) => min < 1 ? -101 : min;
    public static float LegCeil(float max) => max > 99 ? 101 : max;

    /// <summary>A leg range of 1000 .. 1000 is the records' "not used" (the limp clips constrain one leg only; <b>Observed</b>); else the range as the loader reads it.</summary>
    static bool Leg(float health, float min, float max) => min == 1000 && max == 1000 || health >= LegFloor(min) && health <= LegCeil(max);

    /// <summary>
    /// Whether the record can play for <paramref name="s"/>: the weapon in each hand (NO / YES / EITHER, and the held weapon's kind
    /// among the record's kinds), combat and stealth modes, posture, carrying and the leg damage ranges.
    /// </summary>
    public bool Fits(in AnimationStance s) => FitsExceptLegs(s) && Leg(s.LeftLeg, LeftLegMin, LeftLegMax) && Leg(s.RightLeg, RightLegMin, RightLegMax);

    /// <summary><see cref="Fits"/> without the leg ranges (movement clips weigh the legs with <see cref="LegWeight"/> instead).</summary>
    public bool FitsExceptLegs(in AnimationStance s)
    {
        return (Area == AnimationArea.Lower || ArmsFit(s) && KindFits(s))
            && Mode(CombatMode, s.Combat) && Mode(StealthMode, s.Stealth)
            && Crouched == s.Crouched && Prone == s.Prone
            // The carrying booleans are tested on upper and overlay picks only (docs/animation.md): a carrier's legs walk with the ordinary lower clips.
            && (Area == AnimationArea.Lower || CarryingLeft == s.CarryingLeft && CarryingRight == s.CarryingRight && BeingCarried == s.BeingCarried);
    }

    /// <summary>
    /// The arm check of the original (<c>FUN_14051d240</c>, <b>Verified</b>: it tests the layer index, 1 for upper <i>and</i> whole-body records, 2 overlay, and lets only the lower one
    /// pass): <c>has weapon L</c> and <c>R</c> both YES mean exactly one hand holds a weapon, else each hand's YES / NO must match what it holds (EITHER passes). The lower layer is exempt.
    /// </summary>
    public bool ArmsFit(in AnimationStance s)
    {
        if (Area == AnimationArea.Lower) return true;
        bool left = s.Left.Holds, right = s.Right.Holds;
        if (WeaponLeft == 1 && WeaponRight == 1) return left != right;
        return (WeaponLeft == 2 || WeaponLeft == (left ? 1 : 0)) && (WeaponRight == 2 || WeaponRight == (right ? 1 : 0));
    }

    /// <summary>The weapon type held must be one of the record's weapon kinds, for the upper, whole-body and overlay layers (<b>Verified</b>, the idle draw <c>@ 14051ec80</c>); fists (type 5) skip the test.</summary>
    public bool KindFits(in AnimationStance s) => HeldKind(s) is var held && (held == WeaponKinds.Unarmed || (Kinds & held) != 0);

    static WeaponKinds HeldKind(in AnimationStance s) => s.Right.Holds ? s.Right.Weapon : s.Left.Holds ? s.Left.Weapon : WeaponKinds.Unarmed;

    /// <summary>
    /// The weight an idle pick gives the record (<c>FUN_14051ec80</c> and <c>FUN_14051d660</c>, <b>Verified</b>): <c>idle chance</c> (negative counts 0), times 0.1 on the upper, whole-body and overlay
    /// layers when the held weapon type is unarmed (5) and <c>has weapon L</c> or <c>R</c> is exactly YES (a hand then holds an item whose animation kind is 5; the arm check already drops those when both hands are empty).
    /// </summary>
    public float IdleWeight(in AnimationStance s) =>
        Math.Max(IdleChance, 0) * (Area != AnimationArea.Lower && HeldKind(s) == WeaponKinds.Unarmed && (WeaponLeft == 1 || WeaponRight == 1) ? UnarmedHeldYesFactor : 1f);

    /// <summary>The loaded constant <c>DAT_14167fad0</c>.</summary>
    public const float UnarmedHeldYesFactor = 0.1f;

    /// <summary>
    /// Whether the record can play as a character's own <c>idle stance</c> (the original's idle pick <c>FUN_14051f230</c>, <b>Verified</b> by decompilation,
    /// docs/animation.md "Idle stance"): normal category, the combat and stealth modes fit, and the arm check, so a character with a weapon in hand does not stand in the
    /// stance of the empty-handed (it draws from the idle list: <c>stand 1 sword</c>). No other test (legs, kinds, carrying, posture).
    /// </summary>
    public bool FitsAsStance(in AnimationStance s) =>
        Category == 0 && !s.BeingCarried && Mode(CombatMode, s.Combat) && Mode(StealthMode, s.Stealth) && ArmsFit(s);

    static bool Mode(int wants, bool on) => wants switch { 0 => !on, 1 => on, _ => true };
}

/// <summary>
/// The usable ANIMATION records of a game database (normal category, not disabled, no weather overlays; docs/animation.md
/// "Animation definitions"), and the choices a character makes from them. <b>Observed</b>, not traced in the original (docs/simulation.md
/// "Animation"): movement clips of a layer form a chain by <c>move speed</c> and a character blends the two around its
/// speed; a standing character plays one of the valid idles, another after <c>idle time</c> seconds.
/// </summary>
public sealed class AnimationLibrary
{
    readonly List<AnimationDefinition> _all;

    // The leg health values the records' ranges are cut at (sorted, distinct), per leg. A stance is cached by the interval of these its leg values fall
    // in, not by the values: they change every tick of an injury that heals or rots, and a key per value would grow the caches without bound.
    readonly float[] _leftBounds, _rightBounds;
    readonly int[] _swimming, _grounded;

    public AnimationLibrary(IEnumerable<AnimationDefinition> definitions)
    {
        _all = [.. definitions];
        for (int i = 0; i < _all.Count; i++) if (_all[i].Technique) _techniques.TryAdd(_all[i].Id, i);
        _swimming = [.. Enumerable.Range(0, _all.Count).Where(i => _all[i].Category == SwimmingCategory && !_all[i].Technique)];
        _grounded = [.. Enumerable.Range(0, _all.Count).Where(i => _all[i].Category == GroundedCategory && !_all[i].Technique)];
        _leftBounds = Bounds(_all.SelectMany(d => new[] { d.LeftLegMin, d.LeftLegMax, AnimationDefinition.LegFloor(d.LeftLegMin), AnimationDefinition.LegCeil(d.LeftLegMax) }));
        _rightBounds = Bounds(_all.SelectMany(d => new[] { d.RightLegMin, d.RightLegMax, AnimationDefinition.LegFloor(d.RightLegMin), AnimationDefinition.LegCeil(d.RightLegMax) }));
    }

    static float[] Bounds(IEnumerable<float> values) => [.. values.Where(v => !float.IsNaN(v)).Distinct().Order()];

    /// <summary>
    /// A leg health standing for every value that compares the same with each bound (below, equal to, above): the bound itself when it is one, else the
    /// nearest float inside the interval. <see cref="AnimationDefinition.Fits"/> only compares, so the clips chosen for the stand-in are the clips for the value.
    /// </summary>
    static float Representative(float health, float[] bounds)
    {
        if (bounds.Length == 0) return 100;
        int at = Array.BinarySearch(bounds, health);
        if (at >= 0) return bounds[at];
        int above = ~at;   // the first bound over the value (a NaN sorts first, and fits nothing, like a value under every bound)
        if (above == 0) return MathF.BitDecrement(bounds[0]);
        return MathF.BitIncrement(bounds[above - 1]);
    }

    AnimationStance Canonical(in AnimationStance s) => s with { LeftLeg = Representative(s.LeftLeg, _leftBounds), RightLeg = Representative(s.RightLeg, _rightBounds) };

    /// <summary>Entries in the movement and idle caches (it stays small however the legs' health varies; for tests).</summary>
    public int CachedChoices => _candidates.Count + _idles.Count;

    public IReadOnlyList<AnimationDefinition> Definitions => _all;

    readonly Dictionary<string, int> _techniques = new(StringComparer.Ordinal);

    /// <summary>The index of the definition (ANIMATION or COMBAT_TECHNIQUE) whose record is called <paramref name="recordName"/>, or -1.</summary>
    public int IndexOfAny(string recordName) => _all.FindIndex(d => d.Name == recordName);

    /// <summary>The definition's index (an ANIMATION record by name), or -1.</summary>
    public int IndexOf(string recordName) => _all.FindIndex(d => d.Name == recordName && !d.Technique);

    /// <summary>The index of a COMBAT_TECHNIQUE's definition by its string id, or -1.</summary>
    public int IndexOfTechnique(string stringId) => _techniques.TryGetValue(stringId, out int i) ? i : -1;

    /// <summary>The index of an ANIMATION record's definition by its string id, or -1.</summary>
    public int IndexOfRecord(string stringId) => _all.FindIndex(d => d.Id == stringId && !d.Technique);

    /// <summary>The RANGED ANIMATION record of the crossbow's aim (<c>aimH</c>, clip <c>aimshoot</c>) and the default reload (<c>reload 1 phase</c>), by record name.</summary>
    public const string RangedAimRecord = "aimH", RangedReloadRecord = "reload 1 phase";

    /// <summary>
    /// The usable ANIMATION records (normal category, not disabled, no weather overlay), then the COMBAT_TECHNIQUE records of humanoids (<c>animal</c> 8 or
    /// less, the set Kenshi's startup preprocessing gathers; docs/animation.md) as <see cref="AnimationDefinition.FromTechnique"/>, then the RANGED (category 8)
    /// records, which only the combat system plays (appended last so the indices of the others do not move).
    /// </summary>
    public static AnimationLibrary FromDatabase(GameDatabase db)
    {
        var library = new AnimationLibrary(Humanoid(db)
            // The animals' after everything (no earlier index moves): every ANIMAL_ANIMATION, then the creature techniques (animal 9 and up), which only animals play.
            .Concat(db.OfType(FcsRecordType.AnimalAnimation)
                .OrderBy(r => r.StringId, StringComparer.Ordinal)
                .Select(AnimationDefinition.FromAnimal))
            .Concat(db.OfType(FcsRecordType.CombatTechnique)
                .Where(r => r.GetInt("animal") > 8)
                .OrderBy(r => r.StringId, StringComparer.Ordinal)
                .Select(r => AnimationDefinition.FromTechnique(r) with { Category = AnimalCategory }))
            // The swimming records (category 4: swim, swim carry, swim idle) last of all, again so no earlier index moves; only <see cref="SwimMovement"/> and <see cref="SwimIdle"/> choose them.
            .Concat(db.OfType(FcsRecordType.Animation)
                .Where(r => !r.GetBool("disabled") && r.GetInt("category") == SwimmingCategory && r.GetInt("weather type") == 0)
                .OrderBy(r => r.StringId, StringComparer.Ordinal)
                .Select(AnimationDefinition.From))
            // The grounded records (category 5: crawling move, crawl idle up / down, crawl use) after those; only the crawl selectors choose them.
            .Concat(db.OfType(FcsRecordType.Animation)
                .Where(r => !r.GetBool("disabled") && r.GetInt("category") == GroundedCategory && r.GetInt("weather type") == 0)
                .OrderBy(r => r.StringId, StringComparer.Ordinal)
                .Select(AnimationDefinition.From))
            // The weather-type records that list `stumbles` (FUN_1405bbd40 files them in the by-name and stumble maps like any record), last of all; only the stumble pick takes them.
            .Concat(db.OfType(FcsRecordType.Animation)
                .Where(r => !r.GetBool("disabled") && r.GetInt("weather type") != 0 && r.GetReferences("stumbles").Any())
                .OrderBy(r => r.StringId, StringComparer.Ordinal)
                .Select(r => AnimationDefinition.From(r) with { StumbleCategory = r.GetInt("category"), Category = WeatherCategory })));
        library.BuildAnimals(db);
        // The order the original lists ANIMATION records in: its per-type map of records by string id (GameDataContainer +0x90), a boost hash map filled in load order.
        int rank = 0;
        foreach (var id in BoostHashOrder.Of(db.OfType(FcsRecordType.Animation).Select(r => r.StringId), BoostHashOrder.StringHash)) library._dataOrder[id] = rank++;
        return library;
    }

    /// <summary>The humanoid part of <see cref="FromDatabase"/> (its order is what the hashed library indices of people are).</summary>
    static IEnumerable<AnimationDefinition> Humanoid(GameDatabase db) => db.OfType(FcsRecordType.Animation)
        .Where(r => !r.GetBool("disabled") && r.GetInt("category") == 0 && r.GetInt("weather type") == 0)
        .OrderBy(r => r.StringId, StringComparer.Ordinal)
        .Select(AnimationDefinition.From)
        .Concat(db.OfType(FcsRecordType.CombatTechnique)
            .Where(r => r.GetInt("animal") <= 8)
            .OrderBy(r => r.StringId, StringComparer.Ordinal)
            .Select(AnimationDefinition.FromTechnique))
        .Concat(db.OfType(FcsRecordType.Animation)
            .Where(r => !r.GetBool("disabled") && r.GetInt("category") == RangedCategory && r.GetInt("weather type") == 0)
            .OrderBy(r => r.StringId, StringComparer.Ordinal)
            .Select(AnimationDefinition.From))
        // Category 3 after those (so no earlier index moves): the carried body's held pose "carry me". Played by name only; every selector takes category 0.
        .Concat(db.OfType(FcsRecordType.Animation)
            .Where(r => !r.GetBool("disabled") && r.GetInt("category") == CarriedCategory && r.GetInt("weather type") == 0)
            .OrderBy(r => r.StringId, StringComparer.Ordinal)
            .Select(AnimationDefinition.From));

    /// <summary>The category of the animal definitions (ANIMAL_ANIMATION records and creature techniques): no ANIMATION record has it, so no humanoid selector takes them.</summary>
    public const int AnimalCategory = -1;

    /// <summary>The category of weather-type records with stumbles: no selector takes them, only <see cref="Stumbles"/> by their <see cref="AnimationDefinition.StumbleCategory"/> (docs/animation.md).</summary>
    public const int WeatherCategory = -2;

    /// <summary>The ANIMAL_ANIMATION record name of the lying clips: one record plays <c>gotosleep</c> once (the fall), another <c>sleep</c> in a loop.</summary>
    public const string AnimalLyingRecord = "sleeponfloor";

    readonly Dictionary<string, AnimalClips> _animals = new(StringComparer.Ordinal);

    /// <summary>The clip set of an ANIMAL_CHARACTER (by string id); null for anything else (people).</summary>
    public AnimalClips? Animal(string recordId) => _animals.GetValueOrDefault(recordId);

    /// <summary>
    /// Every ANIMAL_CHARACTER's clip set from its <c>animations</c> list (docs/game/animals.md "Animations"): the movement clips (non-zero <c>move speed</c>), the idles
    /// (<c>idle</c>, by <c>idle chance</c>) and its <c>sleeponfloor</c> records (the one that does not loop is the fall, the looping one the lying clip), each followed by
    /// every other <c>sleeponfloor</c> record as a fallback (engine choice: most lists name one or none; the system takes the first whose clip the skeleton has).
    /// A clip whose <c>stealth mode</c> is 1 (sneaking only) is dropped: an animal is never sneaking, so its clip filter (<c>FUN_14051d660</c>) never keeps it (Inferred for movement picks).
    /// </summary>
    void BuildAnimals(GameDatabase db)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < _all.Count; i++) if (_all[i].Category == AnimalCategory && !_all[i].Technique) index.TryAdd(_all[i].Id, i);
        var lying = db.OfType(FcsRecordType.AnimalAnimation).Where(r => r.Name == AnimalLyingRecord && index.ContainsKey(r.StringId))
            .OrderBy(r => r.StringId, StringComparer.Ordinal).ToList();
        int[] Pick(IEnumerable<GameRecord> own, bool loop) =>
            [.. own.Concat(lying).Where(r => r.Name == AnimalLyingRecord && r.GetBool("loop") == loop).Select(r => index[r.StringId]).Distinct()];
        foreach (var a in db.OfType(FcsRecordType.AnimalCharacter))
        {
            var listed = a.GetReferences("animations").Select(r => db.Find(r.TargetStringId))
                .OfType<GameRecord>().Where(r => index.ContainsKey(r.StringId) && _all[index[r.StringId]].StealthMode != 1).DistinctBy(r => r.StringId).ToList();
            var defs = listed.Select(r => index[r.StringId]).ToList();
            string? race = a.GetReferences("race").Where(r => r.Values.Value0 > 0).Select(r => r.TargetStringId).FirstOrDefault();
            _animals[a.StringId] = new AnimalClips(
                a.StringId, race,
                [.. defs.Where(i => _all[i].MoveSpeed != 0).OrderBy(i => _all[i].Id, StringComparer.Ordinal)],
                [.. defs.Where(i => _all[i].Idle && _all[i].MoveSpeed == 0).Select(i => (i, (float)Math.Max(_all[i].IdleChance, 0)))],
                Pick(listed, loop: false), Pick(listed, loop: true));
        }
    }

    /// <summary>
    /// The movement clips among <paramref name="candidates"/> to play at speed <paramref name="v"/>, as (index, weight): each weighted by its speed weight, normalised to sum 1
    /// (the humanoid rule without the stance and the legs, which the animal records do not have). Nothing is added when none weighs anything.
    /// </summary>
    public void Movement(IReadOnlyList<int> candidates, float v, List<(int Index, float Weight)> into)
    {
        int first = into.Count;
        float total = 0;
        foreach (int i in candidates)
        {
            float w = _all[i].SpeedWeight(v);
            if (!(w > 0)) continue;
            into.Add((i, w));
            total += w;
        }
        for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Weight / total);
    }

    /// <summary>CharacterAnimCategory 8, RANGED.</summary>
    public const int RangedCategory = 8;
    /// <summary>The ANIMATION category of <c>carry me</c> (3 in the data; the enum name is not known here).</summary>
    public const int CarriedCategory = 3;
    /// <summary>CharacterAnimCategory 4, SWIMMING (<b>Verified</b>, docs/animation.md): what a character in deep water plays instead of the normal clips.</summary>
    public const int SwimmingCategory = 4;

    /// <summary>
    /// Adds to <paramref name="into"/> the swimming movement clips (category 4, whole body, <c>move speed</c> set) that fit <paramref name="stance"/>, to play at speed <paramref name="v"/>,
    /// weighed and normalised like <see cref="Movement"/> (the carrying booleans pick <c>swim carry</c> and <c>swim carry left</c>). Nothing is added when none fits (a sword in the left hand).
    /// </summary>
    public void SwimMovement(float v, in AnimationStance stance, List<(int Index, float Weight)> into)
    {
        int first = into.Count;
        float total = 0;
        foreach (int i in _swimming)
        {
            var d = _all[i];
            if (d.Idle || d.MoveSpeed == 0 || d.Area != AnimationArea.All || !d.FitsExceptLegs(stance)) continue;
            float w = d.SpeedWeight(v) * d.LegWeight(stance.LeftLeg, stance.RightLeg);
            if (!(w > 0)) continue;
            into.Add((i, w));
            total += w;
        }
        for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Weight / total);
    }

    /// <summary>The swimming idle (<c>swim idle</c>: the first whole-body category 4 idle that fits the stance, a carrier's carrying booleans ignored: there is none for carrying); -1 when none fits.</summary>
    public int SwimIdle(in AnimationStance stance)
    {
        var s = stance with { CarryingLeft = false, CarryingRight = false };
        foreach (int i in _swimming)
        {
            var d = _all[i];
            if (d.Idle && d.Area == AnimationArea.All && d.Fits(s)) return i;
        }
        return -1;
    }

    /// <summary>CharacterAnimCategory 5, GROUNDED: what a crawling character plays (<c>AnimHuman_LowerCombat</c> picks it for posture 1 and 2, <b>Observed</b>).</summary>
    public const int GroundedCategory = 5;

    /// <summary>The leg health the crawl clips are weighed at: no lower than <c>crawling move</c>'s -100, so a leg past it still crawls (engine choice: the data has one crawl clip).</summary>
    const float CrawlLegFloor = -100;

    /// <summary>
    /// Adds to <paramref name="into"/> the crawling movement clips (category 5, <c>move speed</c> set; <c>crawling move</c>) that fit <paramref name="stance"/> (prone), to play at
    /// speed <paramref name="v"/>, weighed and normalised like <see cref="SwimMovement"/>, the legs read no lower than <see cref="CrawlLegFloor"/>. Nothing is added when none fits.
    /// </summary>
    public void CrawlMovement(float v, in AnimationStance stance, List<(int Index, float Weight)> into)
    {
        int first = into.Count;
        float total = 0;
        float left = MathF.Max(stance.LeftLeg, CrawlLegFloor), right = MathF.Max(stance.RightLeg, CrawlLegFloor);
        foreach (int i in _grounded)
        {
            var d = _all[i];
            if (d.Idle || d.MoveSpeed == 0 || d.IsAction || !d.FitsExceptLegs(stance)) continue;
            float w = d.SpeedWeight(v) * d.LegWeight(left, right);
            if (!(w > 0)) continue;
            into.Add((i, w));
            total += w;
        }
        for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Weight / total);
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<AnimationStance, (int Index, float Weight)[]> _crawlIdles = new();

    /// <summary>The stance with the legs neutral: the idle caches hold the clips that fit apart from the legs, which weigh in per call (<see cref="LegWeighted"/>).</summary>
    static AnimationStance NoLegs(in AnimationStance s) => s with { LeftLeg = 0, RightLeg = 0 };

    /// <summary>
    /// An idle draw's weights (<c>FUN_14051ec80</c>, <b>Verified</b>): <c>idle chance</c> x the leg ramp (<see cref="AnimationDefinition.LegWeight"/>, the same as a movement clip's) of the more hurt leg,
    /// only candidates above 0 kept. With <paramref name="firstInDataOrder"/> a draw that keeps none takes the first in data order (the draw never comes back empty; the crawl idles, whose -999 minimum reads as -101).
    /// </summary>
    IReadOnlyList<(int Index, float Weight)> LegWeighted((int Index, float Weight)[] fits, in AnimationStance s, bool firstInDataOrder = false)
    {
        var list = new List<(int, float)>(fits.Length);
        foreach (var (i, chance) in fits)
            if (_all[i].LegWeight(s.LeftLeg, s.RightLeg) is var leg and > 0) list.Add((i, chance * leg));
        if (list.Count == 0 && firstInDataOrder && fits.Length > 0)
        {
            var first = fits.OrderBy(f => DataOrder(f.Index)).First();
            list.Add(first);
        }
        return list;
    }

    /// <summary>The crawling idles that fit <paramref name="stance"/> (<c>crawl idle up</c>, and out of combat <c>crawl idle down</c>), by <c>idle chance</c>; empty when none fits.</summary>
    public IReadOnlyList<(int Index, float Weight)> CrawlIdles(in AnimationStance stance) => LegWeighted(_crawlIdles.GetOrAdd(NoLegs(stance), static (s, self) =>
    {
        var list = new List<(int, float)>();
        foreach (int i in self._grounded)
        {
            var d = self._all[i];
            if (d.Idle && !d.IsAction && d.MoveSpeed == 0 && d.FitsExceptLegs(s)) list.Add((i, d.IdleWeight(s)));
        }
        return [.. list];
    }, this), stance, firstInDataOrder: true);

    readonly System.Collections.Concurrent.ConcurrentDictionary<(AnimationArea, AnimationStance), int[]> _candidates = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<AnimationStance, (int Index, float Weight)[]> _idles = new();

    /// <summary>
    /// The movement clips of a body half (<b>Verified</b>, docs/animation.md "Movement"): the lower list is every clip with a non-zero <c>move speed</c> whose
    /// layer is not <c>upper</c> or <c>overlay</c>, the upper list the <c>upper</c> ones; idles and actions are not movement. The stance filters them, except
    /// for the legs (weighed per call).
    /// </summary>
    int[] CandidatesFor(AnimationArea area, in AnimationStance stance) => _candidates.GetOrAdd((area, stance with { LeftLeg = 100, RightLeg = 100 }), static (key, self) =>
    {
        var list = new List<int>();
        for (int i = 0; i < self._all.Count; i++)
        {
            var d = self._all[i];
            if (d.Category != 0 || d.MoveSpeed == 0 || d.Idle || d.IsAction || d.Area == AnimationArea.Overlay) continue;
            if ((key.Item1 == AnimationArea.Upper) != (d.Area == AnimationArea.Upper)) continue;
            if (d.FitsExceptLegs(key.Item2)) list.Add(i);
        }
        return [.. list.OrderBy(i => self._all[i].Id, StringComparer.Ordinal)];
    }, this);

    /// <summary>
    /// Adds to <paramref name="into"/> the movement clips of <paramref name="area"/> (lower, or upper) to play at speed <paramref name="v"/> (units per second;
    /// negative backs off) as (index, weight): every candidate plays, weighted by its speed weight times its leg weight, the weights normalised to sum 1
    /// (<b>Verified</b>, docs/animation.md "Choosing candidates"). Nothing is added when no candidate weighs anything.
    /// </summary>
    public void Movement(AnimationArea area, float v, in AnimationStance stance, List<(int Index, float Weight)> into)
    {
        int first = into.Count;
        float total = 0;
        foreach (int i in CandidatesFor(area, stance))
        {
            var d = _all[i];
            float w = d.SpeedWeight(v) * d.LegWeight(stance.LeftLeg, stance.RightLeg);
            if (!(w > 0)) continue;
            into.Add((i, w));
            total += w;
        }
        for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Weight / total);
    }

    /// <summary>
    /// The hit reactions (stumbles) a hit to body part <paramref name="part"/> (a LOCATIONAL_DAMAGE string id) may play, with their weights (<b>Verified</b>,
    /// docs/animation.md "Hit reactions"): the clips of the stance's <see cref="AnimationStance.Category"/> listing the part in <c>stumbles</c> that fit the stance, whose <c>big stumble</c> equals
    /// <paramref name="big"/> and whose <c>stumble from</c> is the side (0 front, 1 rear), weighing <c>chance</c> / 100; the one already playing
    /// (<paramref name="playing"/>) weighs a quarter of that. Every base-game stumble has <c>chance</c> 0, so when all weigh 0 they are equally likely
    /// (<b>engine choice</b>).
    /// </summary>
    public void Stumbles(string part, bool big, int side, in AnimationStance stance, int playing, List<(int Index, float Weight)> into)
    {
        int first = into.Count;
        float total = 0;
        for (int i = 0; i < _all.Count; i++)
        {
            var d = _all[i];
            if ((d.StumbleCategory ?? d.Category) != stance.Category || d.Stumbles.Count == 0 || d.BigStumble != big || d.StumbleFrom != side || !d.Stumbles.Contains(part) || !d.FitsExceptLegs(stance)) continue;
            float w = d.Chance / 100f * (i == playing ? 0.25f : 1);
            into.Add((i, w));
            total += w;
        }
        if (total <= 0)
            for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Index == playing ? 0.25f : 1f);
    }

    /// <summary>
    /// The guard a fighter's upper body holds (<b>Verified</b>, the combat pick <c>AnimationClassHuman</c> slot 28 through <c>FUN_14051d660</c>): of <see cref="CombatIdles"/>, the one with the
    /// highest candidate weight (speed weight x leg weight) at speed <paramref name="v"/>, the first in the original's list order on a tie (<see cref="DataOrder"/>), chosen again
    /// every tick; -1 when there is none.
    /// </summary>
    public int CombatGuard(in AnimationStance stance, float v)
    {
        int best = -1;
        float bestWeight = float.NegativeInfinity;
        foreach (var (i, _) in CombatIdles(stance))
        {
            var d = _all[i];
            float w = d.SpeedWeight(v) * d.LegWeight(stance.LeftLeg, stance.RightLeg);
            if (w > bestWeight || best >= 0 && w == bestWeight && DataOrder(i) < DataOrder(best)) (best, bestWeight) = (i, w);
        }
        return best;
    }

    readonly Dictionary<string, int> _dataOrder = new(StringComparer.Ordinal);

    /// <summary>A definition's place in the original's list of ANIMATION records (<see cref="BoostHashOrder"/>), for its "first in list order" ties; its index without one.</summary>
    int DataOrder(int index) => _dataOrder.TryGetValue(_all[index].Id, out int rank) ? rank : int.MaxValue / 2 + index;

    readonly System.Collections.Concurrent.ConcurrentDictionary<AnimationStance, (int Index, float Weight)[]> _combatIdles = new();

    /// <summary>
    /// The idles of a character standing in combat: the upper-body <c>idle</c> clips (the guards, <c>MA idle1</c>) that fit the stance: combat mode, arm check and weapon kinds
    /// (<see cref="AnimationDefinition.ArmsFit"/>, <see cref="AnimationDefinition.KindFits"/>: the guards want exactly one hand armed), by <c>idle chance</c>. <b>Observed</b> (docs/animation.md "Combat upper body"): the original picks from the
    /// idle list by weapon type and carrying.
    /// Without any, the whole-body idles that fit apart from the combat mode.
    /// </summary>
    public IReadOnlyList<(int Index, float Weight)> CombatIdles(in AnimationStance stance) => LegWeighted(_combatIdles.GetOrAdd(NoLegs(stance), static (s, self) =>
    {
        var list = new List<(int, float)>();
        for (int i = 0; i < self._all.Count; i++)
        {
            var d = self._all[i];
            if (d.Category == 0 && d.Idle && !d.IsAction && d.Area == AnimationArea.Upper && d.CombatMode != 0 && d.FitsExceptLegs(s)) list.Add((i, d.IdleWeight(s)));
        }
        if (list.Count == 0)
            for (int i = 0; i < self._all.Count; i++)
            {
                var d = self._all[i];
                if (d.Category == 0 && d.Idle && !d.IsAction && d.Area == AnimationArea.All && d.FitsExceptLegs(s with { Combat = d.CombatMode == 1 })) list.Add((i, d.IdleWeight(s)));
            }
        return list.ToArray();
    }, this), stance);

    /// <summary>The standing animations valid for the stance: <c>idle</c> clips of the whole body (not actions, not carry overlays) with their <c>idle chance</c>.</summary>
    public IReadOnlyList<(int Index, float Weight)> Idles(in AnimationStance stance) => LegWeighted(_idles.GetOrAdd(NoLegs(stance), static (s, self) =>
    {
        var list = new List<(int, float)>();
        for (int i = 0; i < self._all.Count; i++)
        {
            var d = self._all[i];
            if (d.Category == 0 && d.Idle && !d.IsAction && d.Area == AnimationArea.All && d.FitsExceptLegs(s)) list.Add((i, d.IdleWeight(s)));
        }
        return list.ToArray();
    }, this), stance);

    /// <summary>
    /// The side-step clip of a human fighter (<c>FUN_14051fc60</c>, <b>Verified</b>): the record NAMED <c>strafe lower</c> from the race's by-name map (the first lookup cached for every later
    /// character), with no stance fit test; -1 when the data has none. The animals' side step is their own <c>strafe anim</c> list.
    /// </summary>
    public int Strafe(in AnimationStance stance) => _strafeByName ??= IndexOf(StrafeRecord);

    /// <summary>The record name of a human's side-step clip.</summary>
    public const string StrafeRecord = "strafe lower";
    int? _strafeByName;

    readonly System.Collections.Concurrent.ConcurrentDictionary<AnimationStance, int> _carryIdles = new();

    /// <summary>
    /// The held arm pose of a carrier standing (or of a carried body): the first idle overlay or upper clip, in record order, whose carrying booleans are set and fit the stance
    /// (<c>stand carrying left</c> in the base game); -1 when none. Played over a whole-body idle of the same stance without the carrying (engine choice).
    /// </summary>
    public int CarryIdle(in AnimationStance stance) => _carryIdles.GetOrAdd(Canonical(stance), static (s, self) =>
    {
        for (int i = 0; i < self._all.Count; i++)
        {
            var d = self._all[i];
            if (d.Category == 0 && d.Idle && !d.IsAction && d.Area is AnimationArea.Overlay or AnimationArea.Upper && (d.CarryingLeft || d.CarryingRight || d.BeingCarried) && d.Fits(s)) return i;
        }
        return -1;
    }, this);
}

/// <summary>
/// The clips an ANIMAL_CHARACTER plays, as indices into its <see cref="AnimationLibrary"/> (<see cref="AnimationLibrary.Animal"/>): from its own <c>animations</c> list, the
/// movement clips, the idles with their weights, and the fall and lying candidates in order (its own first). <paramref name="RaceId"/> picks the skeleton's clip lengths
/// (<see cref="AnimationLengths.For"/>). Its techniques are the creature techniques of its weapon's category, which the combat system chooses.
/// </summary>
public sealed record AnimalClips(string RecordId, string? RaceId, int[] Movement, (int Index, float Weight)[] Idles, int[] Fall, int[] Lying);

/// <summary>
/// The length in seconds of every clip of the humanoid skeletons (what a synched movement clip needs to turn a phase into a time), and, per animal race, the clips of that
/// race's own skeleton (<see cref="For"/>: an animal's clips are in the skeleton its body mesh links, and their names repeat across species with other lengths).
/// </summary>
public sealed class AnimationLengths
{
    readonly Dictionary<string, float> _lengths = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (float[] Times, System.Numerics.Vector2[] Offsets)> _roots = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AnimationLengths> _species = new(StringComparer.Ordinal);

    public static readonly AnimationLengths Empty = new();

    /// <summary>
    /// The clips of every <c>*_skeleton.skeleton</c> under <c>data/character/meshes</c> (the first file to name a clip wins); with <paramref name="db"/> also the clips of
    /// the skeleton each ANIMAL_CHARACTER's race's body mesh links (<see cref="For"/>), read in parallel, one table per skeleton file. With <paramref name="find"/> (a bare
    /// file name to its full path, as the game's resource groups find it) the clips of the ANIMATION_FILE skeletons follow, in link order (<see cref="AnimationFiles"/>).
    /// </summary>
    public static AnimationLengths Load(string installRoot, GameDatabase? db = null, Func<string, string?>? find = null)
    {
        var result = new AnimationLengths();
        var dir = Path.Combine(installRoot, "data", "character", "meshes");
        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir, "*_skeleton.skeleton", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                result.AddSkeleton(file);
        if (db is not null && find is not null) result.AddLinked(AnimationFiles.All(db), find);
        if (db is not null) result.LoadAnimals(installRoot, db, find);
        return result;
    }

    /// <summary>Adds the clips of linked skeleton files (FCS paths, in link order), read in parallel; a file not found is left out, as the game links only the ones it finds.</summary>
    void AddLinked(IReadOnlyList<string> files, Func<string, string?> find)
    {
        var paths = files.Select(f => find(AnimationFiles.BareName(f))).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var read = new OgreSkeleton?[paths.Count];
        Parallel.For(0, paths.Count, i => read[i] = TryRead(paths[i]));
        foreach (var skeleton in read) if (skeleton is not null) AddSkeleton(skeleton);
    }

    static OgreSkeleton? TryRead(string file)
    {
        try { return OgreSkeletonReader.ReadFile(file); }
        catch (Exception e) when (e is IOException or InvalidDataException or FcsFormatException or OgreFormatException or EndOfStreamException) { return null; }
    }

    /// <summary>Adds the clips of a skeleton file (a clip already known keeps its first length).</summary>
    void AddSkeleton(string file)
    {
        if (TryRead(file) is { } skeleton) AddSkeleton(skeleton);
    }

    void AddSkeleton(OgreSkeleton skeleton)
    {
        var root = skeleton.Bones.FirstOrDefault(b => b.Name == "Bip01");
        foreach (var a in skeleton.Animations)
        {
            if (!_lengths.TryAdd(a.Name, a.Length) || root is null) continue;
            var track = a.Tracks.FirstOrDefault(t => t.Bone == root.Handle);
            if (track is null || track.KeyFrames.Count == 0) continue;
            _roots[a.Name] = ([.. track.KeyFrames.Select(k => k.Time)], [.. track.KeyFrames.Select(k => new System.Numerics.Vector2(k.Translation.X, k.Translation.Z))]);
        }
    }

    /// <summary>
    /// Per race of an ANIMAL_CHARACTER: the race's <c>male mesh</c> (animal races name the same mesh for both), the skeleton that mesh links (read from the mesh file; looked
    /// for beside it, then anywhere under <c>data</c>), and that skeleton's clips, then with <paramref name="find"/> those of the race's animation files (an animal is male:
    /// <c>male animation</c>). Races sharing a skeleton and animation files share their table.
    /// </summary>
    void LoadAnimals(string installRoot, GameDatabase db, Func<string, string?>? find)
    {
        var races = db.OfType(FcsRecordType.AnimalCharacter)
            .SelectMany(a => a.GetReferences("race").Where(r => r.Values.Value0 > 0).Take(1).Select(r => db.Find(r.TargetStringId)))
            .OfType<GameRecord>().Where(r => r.Type == FcsRecordType.Race).DistinctBy(r => r.StringId).ToList();
        var keyOf = new System.Collections.Concurrent.ConcurrentDictionary<string, (string File, string[] Linked)?>(StringComparer.Ordinal);
        Parallel.ForEach(races, race =>
        {
            var file = SkeletonFile(installRoot, race.GetPath("male mesh"), find);
            string[] linked = file is null || find is null ? []
                : [.. AnimationFiles.Of(db, race, null, female: false).Select(f => find(AnimationFiles.BareName(f))).OfType<string>()];
            keyOf[race.StringId] = file is null ? null : (file, linked);
        });
        static string Key((string File, string[] Linked) k) => string.Join("|", [k.File, .. k.Linked]);
        var tables = new System.Collections.Concurrent.ConcurrentDictionary<string, AnimationLengths>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(keyOf.Values.OfType<(string File, string[] Linked)>().DistinctBy(Key, StringComparer.OrdinalIgnoreCase), k =>
        {
            var table = new AnimationLengths();
            table.AddSkeleton(k.File);
            table.AddLinked(k.Linked, f => f);
            tables[Key(k)] = table;
        });
        foreach (var race in races)
            if (keyOf[race.StringId] is { } k && tables.TryGetValue(Key(k), out var table)) _species[race.StringId] = table;
    }

    /// <summary>The skeleton file a mesh (an FCS path) links: beside the mesh, else the first file of that name under <c>data</c>; null when either is missing.</summary>
    static string? SkeletonFile(string installRoot, string fcsMesh, Func<string, string?>? find)
    {
        if (fcsMesh.Length == 0) return null;
        var mesh = GameFiles.Resolve(Path.Combine(installRoot, fcsMesh.Replace('\\', Path.DirectorySeparatorChar).TrimStart('.', Path.DirectorySeparatorChar)));
        try
        {
            if (!File.Exists(mesh) && find?.Invoke(AnimationFiles.BareName(fcsMesh)) is { } found) mesh = found;   // a mod's mesh away from its FCS path (a workshop folder)
            if (!File.Exists(mesh)) return null;
            if (OgreMeshReader.ReadFile(mesh).SkeletonName is not { Length: > 0 } name) return null;
            var beside = GameFiles.Resolve(Path.Combine(Path.GetDirectoryName(mesh)!, name));
            if (File.Exists(beside)) return beside;
            return Directory.EnumerateFiles(Path.Combine(installRoot, "data"), Path.GetFileName(name), SearchOption.AllDirectories).Order(StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OgreFormatException or EndOfStreamException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The clips of an animal race's own skeleton (<paramref name="raceId"/> a RACE string id); this table for anything else (people, a race not loaded).</summary>
    public AnimationLengths For(string? raceId) => raceId is not null && _species.TryGetValue(raceId, out var table) ? table : this;

    /// <summary>Whether <see cref="For"/> has a table of its own for the race (an animal race whose skeleton was read).</summary>
    public bool HasSpecies(string raceId) => _species.ContainsKey(raceId);

    /// <summary>Whether the table knows the clip (its skeleton has an animation of that name).</summary>
    public bool Has(string clip) => _lengths.ContainsKey(clip);

    /// <summary>Sets an animal race's table (tests).</summary>
    public AnimationLengths AddSpecies(string raceId, AnimationLengths table)
    {
        _species[raceId] = table;
        return this;
    }

    public AnimationLengths Add(string clip, float seconds)
    {
        _lengths[clip] = seconds;
        return this;
    }

    /// <summary>Sets a clip's <c>Bip01</c> track as (time, X, Z) keys (tests).</summary>
    public AnimationLengths AddRoot(string clip, float[] times, System.Numerics.Vector2[] offsets)
    {
        _roots[clip] = (times, offsets);
        return this;
    }

    /// <summary>
    /// The clip's <c>Bip01</c> translation across the ground (model X and Z: Z forward, X to the character's left) at a time, relative to the binding pose,
    /// linear between keys and held past the ends; zero for a clip without one.
    /// </summary>
    public System.Numerics.Vector2 Root(string clip, float time)
    {
        if (!_roots.TryGetValue(clip, out var r)) return default;
        var (times, offsets) = r;
        if (time <= times[0]) return offsets[0];
        if (time >= times[^1]) return offsets[^1];
        int hi = Array.BinarySearch(times, time);
        if (hi >= 0) return offsets[hi];
        hi = ~hi;
        float f = (time - times[hi - 1]) / MathF.Max(times[hi] - times[hi - 1], 1e-6f);
        return System.Numerics.Vector2.Lerp(offsets[hi - 1], offsets[hi], f);
    }

    /// <summary>The clip's length; 1 s for a clip not known.</summary>
    public float Of(string clip) => _lengths.TryGetValue(clip, out float l) && l > 0 ? l : 1;
}
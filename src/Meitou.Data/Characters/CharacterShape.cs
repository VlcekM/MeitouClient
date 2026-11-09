using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Data.Characters;

/// <summary>
/// Kenshi's limb order for missing limbs and robotic limbs (fcs.def "severed limbs": 0 left arm ... 3 right leg).
/// </summary>
public enum Limb
{
    /// <summary>
    /// 0: the left arm.
    /// </summary>
    LeftArm,
    /// <summary>
    /// 1: the right arm.
    /// </summary>
    RightArm,
    /// <summary>
    /// 2: the left leg.
    /// </summary>
    LeftLeg,
    /// <summary>
    /// 3: the right leg.
    /// </summary>
    RightLeg
}

/// <summary>
/// ARMOUR <c>hide stump</c> (fcs_enums.def HideStump): which upper arms go to zero when that arm is missing.
/// </summary>
[Flags]
public enum HideStump
{
    /// <summary>
    /// Neither upper arm.
    /// </summary>
    None = 0,
    /// <summary>
    /// The left upper arm.
    /// </summary>
    LeftArm = 1,
    /// <summary>
    /// The right upper arm.
    /// </summary>
    RightArm = 2,
    /// <summary>
    /// Both upper arms.
    /// </summary>
    BothArms = 3
}

/// <summary>
/// Everything the body-shape sliders depend on (docs/animation.md, "Body shape sliders").
/// </summary>
public sealed record CharacterShapeInput
{
    /// <summary>
    /// Appearance values in Kenshi units (100 = neutral), keyed by slider name (<c>Height</c>, <c>Arm bulk</c>...).
    /// </summary>
    public IReadOnlyDictionary<string, float> Sliders { get; init; } = new Dictionary<string, float>();

    /// <summary>
    /// Whether the body is female.
    /// </summary>
    public bool Female { get; init; }

    /// <summary>
    /// The character's muscle value (stat driven, see <see cref="CharacterShape.MuscleFromStats"/>); 0 = no change.
    /// </summary>
    public float Muscle { get; init; }

    /// <summary>
    /// The character's starvation value, 0 (fed) to 1; see <see cref="CharacterShape.StarvationFromNutrition"/>.
    /// </summary>
    public float Starvation { get; init; }

    /// <summary>
    /// Limbs the character has lost (forearm, hand / calf, foot and toe scale to zero).
    /// </summary>
    public IReadOnlySet<Limb> Missing { get; init; } = new HashSet<Limb>();

    /// <summary>
    /// <c>hide stump</c> of the worn body armour.
    /// </summary>
    public HideStump HideStump { get; init; }

    /// <summary>
    /// Calf length ratio from boot heights and robotic-leg offsets (positive: left calf Y, negative: right calf Y); 0 = none.
    /// </summary>
    public float LegRatio { get; init; }

    /// <summary>
    /// A state of the character's left leg that narrows the thigh to 0.8 (source not identified).
    /// </summary>
    public bool NarrowLeftThigh { get; init; }

    /// <summary>
    /// The same state of the right leg (see <see cref="NarrowLeftThigh"/>).
    /// </summary>
    public bool NarrowRightThigh { get; init; }
}

/// <summary>
/// A character's stat-driven body (the original's <c>AppearanceHuman</c> values at +0x1a0..+0x1a8, <c>FUN_14052bc70</c>; docs/animation.md
/// "Stat-driven body"): <see cref="Muscle"/> the bulk of the bone shape, <see cref="Definition"/> the body normal map's blend towards the strong one,
/// <see cref="Starvation"/> 0 (fed) to 1.
/// </summary>
public readonly record struct BodyBuild(float Muscle, float Definition, float Starvation, float ShapeMuscle, float ShapeStarvation)
{
    /// <summary>
    /// The build whose bone shape is <paramref name="shape"/>'s (muscle and starvation) while the normal maps follow this one's values: the original
    /// rebuilds the bones only for the player's faction.
    /// </summary>
    public BodyBuild WithShapeOf(BodyBuild shape) => this with { ShapeMuscle = shape.ShapeMuscle, ShapeStarvation = shape.ShapeStarvation };

    /// <summary>
    /// Whether <paramref name="other"/> looks the same: every value within <paramref name="tolerance"/>.
    /// </summary>
    public bool Near(BodyBuild other, float tolerance = 0.01f) =>
        MathF.Abs(Definition - other.Definition) <= tolerance && MathF.Abs(Starvation - other.Starvation) <= tolerance
        && MathF.Abs(ShapeMuscle - other.ShapeMuscle) <= tolerance && MathF.Abs(ShapeStarvation - other.ShapeStarvation) <= tolerance;
}

/// <summary>
/// Per-bone vectors for Kenshi's modified Ogre (docs/formats/ogre-skeleton.md, "Bone maths").
/// </summary>
public sealed class CharacterShapeResult
{
    /// <summary>
    /// Bone size: multiplies the bone's own derived scale.
    /// </summary>
    public Dictionary<string, Vector3> BoneSize { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Positional size: multiplies the bone's local position before the parent transform.
    /// </summary>
    public Dictionary<string, Vector3> PositionalSize { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The skeleton's movement scale (track translations are multiplied by it).
    /// </summary>
    public float MovementScale { get; set; } = 1;
}

/// <summary>
/// Kenshi's body-shape sliders: appearance values, muscle and starvation turned into bone sizes and positional sizes
/// of the 30-bone human skeletons. The formulas are written out in docs/animation.md ("Body shape sliders"); this is an
/// implementation of that description.
/// </summary>
public static class CharacterShape
{
    /// <summary>
    /// Every slider the body shape reads (the last two only for women).
    /// </summary>
    public static readonly IReadOnlyList<string> Sliders =
    [
        "Height", "Leg length", "Frame", "Legs bulk", "Legs shape", "Hips", "Chest", "Waist", "Stomach", "Breast size",
        "Breast height", "Breast spacing", "Arm bulk", "Shoulders", "Hands", "Feet", "Head size", "Head shape", "Neck",
        "Neck width", "Neck length", "Jaw",
    ];

    /// <summary>
    /// Only skeletons with exactly this many bones get a body shape.
    /// </summary>
    public const int HumanBoneCount = 30;

    #region Bone shape

    /// <summary>
    /// 1 moved towards <paramref name="to"/> by <paramref name="t"/>.
    /// </summary>
    static float Lerp1(float to, float t) => 1 + (to - 1) * t;

    /// <summary>
    /// Computes bone sizes. Sliders missing from the input count as 100 (a viewer choice, see the docs).
    /// </summary>
    /// <param name="hasBone">Whether the skeleton has a bone (for <c>L Boob</c> / <c>R Boob</c> and <c>Bip01 Jaw</c>).</param>
    public static CharacterShapeResult Compute(CharacterShapeInput input, Func<string, bool> hasBone)
    {
        float h = SliderValue(input, "Height"), legLength = SliderValue(input, "Leg length"), frame = SliderValue(input, "Frame");
        float legScale = h + legLength - 1;
        var r = new CharacterShapeResult { MovementScale = legScale };
        AddLegs(r, input, frame, h, legScale);
        AddTorso(r, input, hasBone, frame, h);
        AddArms(r, input, frame, h);
        AddFeet(r, input, h, legLength);
        AddNeckAndHead(r, input, hasBone, frame);
        return r;
    }

    /// <summary>
    /// A slider as a factor: its value, or 100 when the input does not have it, times 0.01.
    /// </summary>
    static float SliderValue(CharacterShapeInput input, string name) => (input.Sliders.TryGetValue(name, out var v) ? v : 100) * 0.01f;

    /// <summary>
    /// The thighs and calves, with the thighs' positional offsets; the legs are <paramref name="legScale"/> tall.
    /// </summary>
    static void AddLegs(CharacterShapeResult r, CharacterShapeInput input, float frame, float h, float legScale)
    {
        void Size(string bone, Vector3 v) => r.BoneSize[bone] = v;
        void Offset(string bone, Vector3 v) => r.PositionalSize[bone] = v;

        float thin07 = Lerp1(0.7f, input.Starvation), bulk = Lerp1(input.Female ? 1.24f : 1.27f, input.Muscle);
        float limb2 = input.Missing.Contains(Limb.LeftLeg) ? 0 : 1, limb3 = input.Missing.Contains(Limb.RightLeg) ? 0 : 1;

        // Legs.
        float legsBulk = SliderValue(input, "Legs bulk") * thin07 * bulk, legsShape = SliderValue(input, "Legs shape");
        float hips = SliderValue(input, "Hips");
        float thigh = (legsShape * legsBulk + (hips - 1) / 3) * frame;
        float tr = input.NarrowRightThigh ? 0.8f : 1, tl = input.NarrowLeftThigh ? 0.8f : 1;
        Size("Bip01 R Thigh", new Vector3(thigh * tr, legScale * 0.95f, thigh * tr));
        Size("Bip01 L Thigh", new Vector3(thigh * tl, legScale * 0.95f, thigh * tl));
        var thighOffset = new Vector3(1, frame * (2 - h) * hips, 1);
        Offset("Bip01 R Thigh", thighOffset);
        Offset("Bip01 L Thigh", thighOffset);
        float calf = (2 - legsShape) * legsBulk * frame;
        var calfBase = new Vector3(calf, legScale, calf);
        Size("Bip01 L Calf", calfBase * limb2 * new Vector3(1, input.LegRatio > 0 ? input.LegRatio : 1, 1));
        Size("Bip01 R Calf", calfBase * limb3 * new Vector3(1, input.LegRatio < 0 ? -input.LegRatio : 1, 1));
    }

    /// <summary>
    /// The pelvis, spine, chest and (for a skeleton with the bone) the breasts. <c>"Mid-section"</c> is read by the game but not used.
    /// </summary>
    static void AddTorso(CharacterShapeResult r, CharacterShapeInput input, Func<string, bool> hasBone, float frame, float h)
    {
        void Size(string bone, Vector3 v) => r.BoneSize[bone] = v;
        void Offset(string bone, Vector3 v) => r.PositionalSize[bone] = v;

        float thin07 = Lerp1(0.7f, input.Starvation), thin086 = Lerp1(0.86f, input.Starvation), thin06 = Lerp1(0.6f, input.Starvation);
        float thin04 = Lerp1(0.4f, input.Starvation), broad = Lerp1(input.Female ? 1.12f : 1.13f, input.Muscle);

        // Torso. ("Mid-section" is read by the game but not used.)
        float hips = SliderValue(input, "Hips");
        float chest = SliderValue(input, "Chest") * broad * thin086, waist = SliderValue(input, "Waist") * thin07;
        float stomach = SliderValue(input, "Stomach") * thin06;
        Size("Bip01 Pelvis", new Vector3(hips * frame, h, hips * frame));
        float hips06 = (hips - 1) * 0.6f + 1;
        Size("Bip01 Spine", new Vector3(hips06 * frame, h, hips06 * stomach * frame));
        Size("Bip01 Spine1", new Vector3(waist * frame, h, stomach * frame));
        float chest045 = (chest - 1) * 0.45f + 1, chest09 = (chest - 1) * 0.9f + 1;
        Size("Bip01 Spine2", new Vector3(chest045 * frame, h, chest09 * frame));

        if (hasBone("L Boob"))
        {
            float breast = SliderValue(input, "Breast size") * thin04;
            float bx = breast, by = breast;
            if (breast < 1)
            {
                bx = (breast - 1) * 0.75f + 1;
                by = (breast - 1) * 0.5f + 1;
            }

            float height = input.Female ? SliderValue(input, "Breast height") : 1;
            float spacing = input.Female ? SliderValue(input, "Breast spacing") : 1;
            var size = new Vector3(bx * frame, by * h, breast * frame);
            var offset = new Vector3(height, spacing * chest09 * frame * ((1 - h) * 0.5f + 1), (2 - h) * chest09 * frame);
            foreach (var b in (ReadOnlySpan<string>)["L Boob", "R Boob"])
            {
                Size(b, size);
                Offset(b, offset);
            }
        }
    }

    /// <summary>
    /// The arms and hands; a missing or hidden stump is not drawn.
    /// </summary>
    static void AddArms(CharacterShapeResult r, CharacterShapeInput input, float frame, float h)
    {
        void Size(string bone, Vector3 v) => r.BoneSize[bone] = v;
        void Offset(string bone, Vector3 v) => r.PositionalSize[bone] = v;

        float thin07 = Lerp1(0.7f, input.Starvation), thin086 = Lerp1(0.86f, input.Starvation);
        float broad = Lerp1(input.Female ? 1.12f : 1.13f, input.Muscle), bulk = Lerp1(input.Female ? 1.24f : 1.27f, input.Muscle);
        float limb0 = input.Missing.Contains(Limb.LeftArm) ? 0 : 1, limb1 = input.Missing.Contains(Limb.RightArm) ? 0 : 1;
        float stumpL = input.HideStump.HasFlag(HideStump.LeftArm) ? limb0 : 1, stumpR = input.HideStump.HasFlag(HideStump.RightArm) ? limb1 : 1;
        float chest = SliderValue(input, "Chest") * broad * thin086, chest045 = (chest - 1) * 0.45f + 1;

        // Arms.
        float arm = SliderValue(input, "Arm bulk") * thin07 * bulk, shoulders = SliderValue(input, "Shoulders") * broad;
        var upperArm = new Vector3(arm * frame, h, ((arm - 1) * 1.5f + 1) * frame);
        Size("Bip01 L UpperArm", upperArm * stumpL);
        Size("Bip01 R UpperArm", upperArm * stumpR);
        var forearm = new Vector3(arm * frame, h, arm * frame);
        Size("Bip01 L Forearm", forearm * limb0);
        Size("Bip01 R Forearm", forearm * limb1);
        var clavicle = new Vector3(shoulders * frame, ((shoulders - 1) * 0.3f + 1) * frame, shoulders * frame);
        Size("Bip01 L Clavicle", clavicle);
        Size("Bip01 R Clavicle", clavicle);
        var upperArmOffset = new Vector3(shoulders * chest045, 1, 1);
        Offset("Bip01 L UpperArm", upperArmOffset);
        Offset("Bip01 R UpperArm", upperArmOffset);
        var hand = forearm * SliderValue(input, "Hands");
        Size("Bip01 L Hand", hand * limb0);
        Size("Bip01 R Hand", hand * limb1);
    }

    /// <summary>
    /// The feet and toes; a missing leg's are not drawn.
    /// </summary>
    static void AddFeet(CharacterShapeResult r, CharacterShapeInput input, float h, float legLength)
    {
        void Size(string bone, Vector3 v) => r.BoneSize[bone] = v;
        void Offset(string bone, Vector3 v) => r.PositionalSize[bone] = v;

        float limb2 = input.Missing.Contains(Limb.LeftLeg) ? 0 : 1, limb3 = input.Missing.Contains(Limb.RightLeg) ? 0 : 1;

        // Feet.
        float feet = SliderValue(input, "Feet") * h;
        Size("Bip01 L Foot", new Vector3(feet, legLength, feet) * limb2);
        Size("Bip01 R Foot", new Vector3(feet, legLength, feet) * limb3);
        Size("Bip01 L Toe0", new Vector3(feet) * limb2);
        Size("Bip01 R Toe0", new Vector3(feet) * limb3);
        Offset("Bip01 L Toe0", new Vector3(1, 1, feet) * limb2);
        Offset("Bip01 R Toe0", new Vector3(1, 1, feet) * limb3);
    }

    /// <summary>
    /// The neck and the head, with its jaw when the skeleton has the bone.
    /// </summary>
    static void AddNeckAndHead(CharacterShapeResult r, CharacterShapeInput input, Func<string, bool> hasBone, float frame)
    {
        void Size(string bone, Vector3 v) => r.BoneSize[bone] = v;

        float thin07 = Lerp1(0.7f, input.Starvation), thin06 = Lerp1(0.6f, input.Starvation);
        float broad = Lerp1(input.Female ? 1.12f : 1.13f, input.Muscle);

        // Neck and head.
        float neck = SliderValue(input, "Neck") * thin07, neckWidth = SliderValue(input, "Neck width") * thin06 * broad;
        Size("Bip01 Neck", new Vector3(neckWidth * frame, SliderValue(input, "Neck length"), neck * frame));
        float headSize = SliderValue(input, "Head size"), headFrame = (frame - 1) * 0.25f + 1;
        var head = new Vector3(headFrame * headSize * SliderValue(input, "Head shape"), headSize, headFrame * headSize);
        Size("Bip01 Head", head);
        if (hasBone("Bip01 Jaw")) Size("Bip01 Jaw", new Vector3(SliderValue(input, "Jaw"), 1, 1) * head);
    }

    #endregion

    #region Stat-driven body

    /// <summary>
    /// Muscle from stats: the largest of strength, weapon smithing and armour smithing (0–100), × 0.01, mapped so 0.2 → 0
    /// and 0.99 → 1 (not clamped).
    /// </summary>
    public static float MuscleFromStats(float strength, float weaponSmith, float armourSmith) =>
        (Math.Max(strength, Math.Max(weaponSmith, armourSmith)) * 0.01f - 0.2f) / (0.99f - 0.2f);

    /// <summary>
    /// Muscle definition (the body shader's <c>muscleBlend</c> toward the strong normal map): (swimming + athletics +
    /// 3 × max(dexterity, unarmed) − cooking − science) × 0.01 / 5, mapped so 0.2 → 0 and 0.99 (women) / 0.9 (men) → 1,
    /// clamped to 0–1. The dexterity multiplier of the game (an unidentified stat field) is taken as 1.
    /// </summary>
    public static float MuscleDefinitionFromStats(
        bool female, float swimming, float athletics, float dexterity, float unarmed, float cooking, float science)
    {
        float x = (swimming + athletics + 3 * Math.Max(dexterity, unarmed) - (cooking + science)) * 0.01f / 5;
        return Math.Clamp((x - 0.2f) / ((female ? 0.99f : 0.9f) - 0.2f), 0, 1);
    }

    /// <summary>
    /// The body a character's current stats and hunger give (<c>FUN_14052bc70</c>): bulk from strength and the two smithing skills, definition from
    /// swimming, athletics, dexterity, martial arts, cooking and science, starvation from the hunger (0 to 3).
    /// </summary>
    public static BodyBuild BuildFromStats<T>(bool female, T stats, Func<T, StatsEnumerated, float> stat, float hunger)
    {
        float muscle = MuscleFromStats(
            stat(stats, StatsEnumerated.Strength), stat(stats, StatsEnumerated.SmithingWeapon), stat(stats, StatsEnumerated.SmithingArmour));
        float definition = MuscleDefinitionFromStats(female,
            stat(stats, StatsEnumerated.Swimming), stat(stats, StatsEnumerated.Athletics), stat(stats, StatsEnumerated.Dexterity),
            stat(stats, StatsEnumerated.MartialArts), stat(stats, StatsEnumerated.Cooking), stat(stats, StatsEnumerated.Science));
        float starvation = StarvationFromNutrition(hunger);
        return new(muscle, definition, starvation, muscle, starvation);
    }

    /// <summary>
    /// Starvation from the character's hunger (the original's <c>+0x4b8</c>, 0 to 3): 1 at 4/3 or less, falling to 0 at 2.
    /// </summary>
    public static float StarvationFromNutrition(float nutrition) => Math.Clamp((1 - Math.Clamp(nutrition - 1, 0, 1)) * 1.5f, 0, 1);

    #endregion

    #region Normal maps and poses

    /// <summary>
    /// Which normal maps the body blends and by how much: base <c>nm</c> toward <c>nm strong</c> by the muscle definition,
    /// unless starvation is above 0.25 or above the definition: then toward <c>nm skinny</c> by the starvation, with
    /// <c>nm strong</c> as the base when the definition is above 0.33.
    /// </summary>
    public static (string BaseField, string BlendField, float Weight) NormalBlend(bool female, float muscleDefinition, float starvation)
    {
        string g = female ? "female" : "male";
        if (starvation > 0.25f || starvation > muscleDefinition)
            return (muscleDefinition <= 0.33f ? $"nm {g}" : $"nm {g} strong", $"nm {g} skinny", starvation);
        return ($"nm {g}", $"nm {g} strong", muscleDefinition);
    }

    /// <summary>
    /// The weight a stored pose value is baked with: the value itself, except for slaves' cut horns (CHARACTER
    /// <c>shaved</c> on a mesh with <c>bone_horns_top_short</c>): <c>bone_horns_top_short</c> and
    /// <c>bone_horns_bottom_short</c> at 1, <c>bone_horns_curved</c> left out. Zero values are never baked. Null = skip.
    /// </summary>
    public static float? BakeWeight(string pose, float stored, bool cutHorns)
    {
        if (stored == 0) return null;
        if (!cutHorns) return stored;
        return pose switch
        {
            "bone_horns_top_short" or "bone_horns_bottom_short" => 1,
            "bone_horns_curved" => null,
            _ => stored,
        };
    }

    #endregion

    #region Inputs

    /// <summary>
    /// The skeleton's movement scale H = <c>Height</c> + <c>Leg length</c> - 1 (sliders / 100, a missing one 100) of a character's body file
    /// (docs/animation.md "Body shape sliders"); 1 without one. Animation playback divides by about this (docs/animation.md "Movement").
    /// </summary>
    public static float MovementScaleOf(CharacterAppearance c)
    {
        if (c.Body is not { } body) return 1;
        float h = body.Floats.TryGetValue("Height", out var height) ? height : 100;
        float l = body.Floats.TryGetValue("Leg length", out var leg) ? leg : 100;
        return h * 0.01f + l * 0.01f - 1;
    }

    /// <summary>
    /// The shape inputs of an assembled character: the body file's sliders (or none), its gender, and muscle from the
    /// CHARACTER's first <c>stats</c> STATS record (an approximation: the game uses the live stats; without a record the
    /// muscle is 0).
    /// </summary>
    public static CharacterShapeInput InputFor(GameDatabase db, CharacterAppearance c)
    {
        var sliders = new Dictionary<string, float>(StringComparer.Ordinal);
        if (c.Body is { } body)
            foreach (var name in Sliders)
                if (body.Floats.TryGetValue(name, out var v)) sliders[name] = v;
        float muscle = 0;
        if (c.Character?.GetReferences("stats").Select(r => db.Find(r.TargetStringId))
                .FirstOrDefault(s => s?.Type == FcsRecordType.Stats) is { } stats)
            muscle = MuscleFromStats(stats.GetFloat("strength"), stats.GetFloat("weapon smith"), stats.GetFloat("armour smith"));
        // hide stump: Kenshi reads it from the item worn under the name "armour"; the viewer takes the body-slot ARMOUR.
        var bodyArmour = c.Parts.FirstOrDefault(p => p.Record.Type == FcsRecordType.Armour && p.Slot == AttachSlot.Body)?.Record;
        int State(int k) => c.Loadout?.Limbs[k]?.State ?? 0;
        return new CharacterShapeInput
        {
            Sliders = sliders,
            Female = c.Female,
            Muscle = muscle,
            // Limb factor 0 only for a stump (state 1, uses_hide_stump); a prosthetic keeps the bone sizes so it skins to them.
            Missing = Enumerable.Range(0, 4).Where(k => State(k) == 1).Select(k => (Limb)k).ToHashSet(),
            HideStump = (HideStump)((bodyArmour?.GetInt("hide stump") ?? 0) & 3),
            LegRatio = LegRatioOf(c.Parts),
            // The thigh narrows to 0.8 when its leg is a stump (uses_hide_stump: the leg part's limb slot, FUN_140643ba0, in state 1).
            NarrowLeftThigh = State(2) == 1,
            NarrowRightThigh = State(3) == 1,
        };
    }

    /// <summary>
    /// How far the worn items lift the body off the ground (docs/animation.md, "Feet lift"): with prosthetics in both leg slots (52, 53), the lower
    /// of their <c>offset</c>s, else the worn boots' <c>boot height</c> (0 without). Game: <c>FUN_140538630</c> stores it at the character's
    /// +0x2cc, and <c>FUN_1405f16a0</c> places the body mesh's node that much higher.
    /// </summary>
    public static float FeetLiftOf(IEnumerable<CharacterPart> parts)
    {
        var (boot, left, right, flags) = WornLegs(parts);
        return flags == 3 ? Math.Min(left, right) : boot;
    }

    /// <summary>
    /// The calf length ratio (<c>read_boot_height_n7</c>, docs/animation.md): b the worn boots' <c>boot height</c>, L / R the <c>offset</c> of the
    /// LIMB_REPLACEMENT worn in slot 52 / 53 (a stump's mesh counts, offset 0). Left only: (b + 4) / (L + 4); right only: x = R against y = b; both:
    /// x = R, y = L. Then x &lt; y gives (x + 4) / (y + 4) (the left calf), y &lt; x gives -(y + 4) / (x + 4) (the right calf), equal nothing.
    /// Without a leg item it is not set (0).
    /// </summary>
    public static float LegRatioOf(IEnumerable<CharacterPart> parts)
    {
        var (boot, left, right, flags) = WornLegs(parts);
        if (flags == 0) return 0;
        if (flags == 1) return (boot + 4) / (left + 4);
        float x = right, y = flags == 2 ? boot : left;
        if (x < y) return (x + 4) / (y + 4);
        if (y < x || flags == 2) return -((y + 4) / (x + 4));
        return 0;
    }

    /// <summary>
    /// The worn boots' <c>boot height</c> (the last boots worn; 0 without), the <c>offset</c> of the LIMB_REPLACEMENT in slot 52 and in 53, and
    /// which of the two slots are worn (bit 0: 52, bit 1: 53).
    /// </summary>
    static (float Boot, float Left, float Right, int Flags) WornLegs(IEnumerable<CharacterPart> parts)
    {
        float boot = 0, left = 0, right = 0;
        int flags = 0;
        foreach (var p in parts)
        {
            if (p.Record.Type == FcsRecordType.Armour && p.Slot == AttachSlot.Boots)
                boot = p.Record.GetFloat("boot height");
            else if (p.Record.Type == FcsRecordType.LimbReplacement)
                switch (p.Record.GetInt("slot"))
                {
                    case 52: flags |= 1; left = p.Record.GetFloat("offset"); break;
                    case 53: flags |= 2; right = p.Record.GetFloat("offset"); break;
                }
        }
        return (boot, left, right, flags);
    }

    #endregion

    #region Overrides

    /// <summary>
    /// Applies <c>name=value</c> overrides (as given on the command line). Slider names are matched case-insensitively;
    /// values above 3 are Kenshi units (100 = neutral), 3 or less are fractions (0.8 = 80). Extra keys: <c>muscle</c>,
    /// <c>starve</c>, <c>legratio</c>, <c>missing</c> (comma list of larm, rarm, lleg, rleg), <c>hidestump</c> (0–3).
    /// </summary>
    public static CharacterShapeInput Override(CharacterShapeInput input, IEnumerable<string> overrides, out List<string> applied)
    {
        applied = [];
        var sliders = new Dictionary<string, float>(input.Sliders, StringComparer.Ordinal);
        var missing = new HashSet<Limb>(input.Missing);
        var result = input;
        foreach (var spec in overrides.SelectMany(s => s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            int eq = spec.IndexOf('=');
            if (eq <= 0) throw new FormatException($"--shape '{spec}': expected name=value");
            string key = spec[..eq].Trim(), text = spec[(eq + 1)..].Trim();
            if (OverrideExtra(key, text, ref result, missing, applied)) continue;
            OverrideSlider(key, text, sliders, applied);
        }
        return result with { Sliders = sliders, Missing = missing };
    }

    /// <summary>
    /// Applies one of the extra keys (<c>muscle</c>, <c>starve</c>, <c>legratio</c>, <c>hidestump</c>, <c>missing</c>); false for any other key.
    /// </summary>
    static bool OverrideExtra(string key, string text, ref CharacterShapeInput result, HashSet<Limb> missing, List<string> applied)
    {
        switch (key.ToLowerInvariant())
        {
            case "muscle": result = result with { Muscle = Number(text) }; applied.Add($"muscle={result.Muscle:0.###}"); return true;
            case "starve" or "starvation":
                result = result with { Starvation = Number(text) };
                applied.Add($"starvation={result.Starvation:0.###}");
                return true;
            case "legratio": result = result with { LegRatio = Number(text) }; applied.Add($"legratio={result.LegRatio:0.###}"); return true;
            case "hidestump":
                result = result with { HideStump = (HideStump)(int)Number(text) };
                applied.Add($"hide stump={result.HideStump}");
                return true;
            case "missing":
                foreach (var limb in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    missing.Add(limb.ToLowerInvariant() switch
                    {
                        "larm" => Limb.LeftArm,
                        "rarm" => Limb.RightArm,
                        "lleg" => Limb.LeftLeg,
                        "rleg" => Limb.RightLeg,
                        _ => throw new FormatException($"--shape missing: unknown limb '{limb}' (larm, rarm, lleg, rleg)"),
                    });
                applied.Add($"missing={string.Join(",", missing)}");
                return true;
        }
        return false;
    }

    /// <summary>
    /// Applies a slider value: the slider matched by name ignoring case, then ignoring spaces, hyphens and underscores; values of 3 or less
    /// are fractions.
    /// </summary>
    static void OverrideSlider(string key, string text, Dictionary<string, float> sliders, List<string> applied)
    {
        string bareKey = key.Replace(" ", "").Replace("-", "").Replace("_", "");
        string? slider = Sliders.FirstOrDefault(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase))
            ?? Sliders.FirstOrDefault(s => string.Equals(s.Replace(" ", "").Replace("-", ""), bareKey, StringComparison.OrdinalIgnoreCase));
        if (slider is null)
            throw new FormatException(
                $"--shape: unknown slider '{key}' (known: {string.Join(", ", Sliders)}, muscle, starve, legratio, missing, hidestump)");
        float value = Number(text);
        if (Math.Abs(value) <= 3) value *= 100;
        sliders[slider] = value;
        applied.Add($"{slider}={value:0.##}");
    }

    /// <summary>
    /// An override's number (invariant culture).
    /// </summary>
    static float Number(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    #endregion
}
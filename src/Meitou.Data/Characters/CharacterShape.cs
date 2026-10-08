using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>Kenshi's limb order for missing limbs and robotic limbs (fcs.def "severed limbs": 0 left arm ... 3 right leg).</summary>
public enum Limb { LeftArm, RightArm, LeftLeg, RightLeg }

/// <summary>ARMOUR <c>hide stump</c> (fcs_enums.def HideStump): which upper arms go to zero when that arm is missing.</summary>
[Flags]
public enum HideStump { None = 0, LeftArm = 1, RightArm = 2, BothArms = 3 }

/// <summary>Everything the body-shape sliders depend on (docs/animation.md, "Body shape sliders").</summary>
public sealed record CharacterShapeInput
{
    /// <summary>Appearance values in Kenshi units (100 = neutral), keyed by slider name (<c>Height</c>, <c>Arm bulk</c>...).</summary>
    public IReadOnlyDictionary<string, float> Sliders { get; init; } = new Dictionary<string, float>();
    public bool Female { get; init; }
    /// <summary>The character's muscle value (stat driven, see <see cref="CharacterShape.MuscleFromStats"/>); 0 = no change.</summary>
    public float Muscle { get; init; }
    /// <summary>The character's starvation value, 0 (fed) to 1; see <see cref="CharacterShape.StarvationFromNutrition"/>.</summary>
    public float Starvation { get; init; }
    /// <summary>Limbs the character has lost (forearm, hand / calf, foot and toe scale to zero).</summary>
    public IReadOnlySet<Limb> Missing { get; init; } = new HashSet<Limb>();
    /// <summary><c>hide stump</c> of the worn body armour.</summary>
    public HideStump HideStump { get; init; }
    /// <summary>Calf length ratio from boot heights and robotic-leg offsets (positive: left calf Y, negative: right calf Y); 0 = none.</summary>
    public float LegRatio { get; init; }
    /// <summary>Per-leg state of the character that narrows the thigh to 0.8 (source not identified).</summary>
    public bool NarrowLeftThigh { get; init; }
    public bool NarrowRightThigh { get; init; }
}

/// <summary>Per-bone vectors for Kenshi's modified Ogre (docs/formats/ogre-skeleton.md, "Bone maths").</summary>
public sealed class CharacterShapeResult
{
    /// <summary>Bone size: multiplies the bone's own derived scale.</summary>
    public Dictionary<string, Vector3> BoneSize { get; } = new(StringComparer.Ordinal);
    /// <summary>Positional size: multiplies the bone's local position before the parent transform.</summary>
    public Dictionary<string, Vector3> PositionalSize { get; } = new(StringComparer.Ordinal);
    /// <summary>The skeleton's movement scale (track translations are multiplied by it).</summary>
    public float MovementScale { get; set; } = 1;
}

/// <summary>
/// Kenshi's body-shape sliders: appearance values, muscle and starvation turned into bone sizes and positional sizes
/// of the 30-bone human skeletons. The formulas are written out in docs/animation.md ("Body shape sliders"); this is an
/// implementation of that description.
/// </summary>
public static class CharacterShape
{
    /// <summary>Every slider the body shape reads (the last two only for women).</summary>
    public static readonly IReadOnlyList<string> Sliders =
    [
        "Height", "Leg length", "Frame", "Legs bulk", "Legs shape", "Hips", "Chest", "Waist", "Stomach", "Breast size",
        "Breast height", "Breast spacing", "Arm bulk", "Shoulders", "Hands", "Feet", "Head size", "Head shape", "Neck",
        "Neck width", "Neck length", "Jaw",
    ];

    /// <summary>Only skeletons with exactly this many bones get a body shape.</summary>
    public const int HumanBoneCount = 30;

    static float Lerp1(float to, float t) => 1 + (to - 1) * t;

    /// <summary>Computes bone sizes. Sliders missing from the input count as 100 (a viewer choice, see the docs).</summary>
    /// <param name="hasBone">Whether the skeleton has a bone (for <c>L Boob</c> / <c>R Boob</c> and <c>Bip01 Jaw</c>).</param>
    public static CharacterShapeResult Compute(CharacterShapeInput input, Func<string, bool> hasBone)
    {
        float S(string name) => (input.Sliders.TryGetValue(name, out var v) ? v : 100) * 0.01f;
        float m = input.Muscle, starve = input.Starvation;

        // Starvation and muscle factors.
        float thin07 = Lerp1(0.7f, starve), thin086 = Lerp1(0.86f, starve), thin06 = Lerp1(0.6f, starve), thin04 = Lerp1(0.4f, starve);
        float bulk = Lerp1(input.Female ? 1.24f : 1.27f, m), broad = Lerp1(input.Female ? 1.12f : 1.13f, m);

        float limb0 = input.Missing.Contains(Limb.LeftArm) ? 0 : 1, limb1 = input.Missing.Contains(Limb.RightArm) ? 0 : 1;
        float limb2 = input.Missing.Contains(Limb.LeftLeg) ? 0 : 1, limb3 = input.Missing.Contains(Limb.RightLeg) ? 0 : 1;
        float stumpL = input.HideStump.HasFlag(HideStump.LeftArm) ? limb0 : 1, stumpR = input.HideStump.HasFlag(HideStump.RightArm) ? limb1 : 1;

        float h = S("Height"), legLength = S("Leg length"), frame = S("Frame");
        float legScale = h + legLength - 1;
        var r = new CharacterShapeResult { MovementScale = legScale };
        void Size(string bone, Vector3 v) => r.BoneSize[bone] = v;
        void Offset(string bone, Vector3 v) => r.PositionalSize[bone] = v;

        // Legs.
        float legsBulk = S("Legs bulk") * thin07 * bulk, legsShape = S("Legs shape"), hips = S("Hips");
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

        // Torso. ("Mid-section" is read by the game but not used.)
        float chest = S("Chest") * broad * thin086, waist = S("Waist") * thin07, stomach = S("Stomach") * thin06;
        Size("Bip01 Pelvis", new Vector3(hips * frame, h, hips * frame));
        float hips06 = (hips - 1) * 0.6f + 1;
        Size("Bip01 Spine", new Vector3(hips06 * frame, h, hips06 * stomach * frame));
        Size("Bip01 Spine1", new Vector3(waist * frame, h, stomach * frame));
        float chest045 = (chest - 1) * 0.45f + 1, chest09 = (chest - 1) * 0.9f + 1;
        Size("Bip01 Spine2", new Vector3(chest045 * frame, h, chest09 * frame));

        if (hasBone("L Boob"))
        {
            float breast = S("Breast size") * thin04;
            float bx = breast, by = breast;
            if (breast < 1) { bx = (breast - 1) * 0.75f + 1; by = (breast - 1) * 0.5f + 1; }
            float height = input.Female ? S("Breast height") : 1, spacing = input.Female ? S("Breast spacing") : 1;
            var size = new Vector3(bx * frame, by * h, breast * frame);
            var offset = new Vector3(height, spacing * chest09 * frame * ((1 - h) * 0.5f + 1), (2 - h) * chest09 * frame);
            foreach (var b in (ReadOnlySpan<string>)["L Boob", "R Boob"]) { Size(b, size); Offset(b, offset); }
        }

        // Arms.
        float arm = S("Arm bulk") * thin07 * bulk, shoulders = S("Shoulders") * broad;
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
        var hand = forearm * S("Hands");
        Size("Bip01 L Hand", hand * limb0);
        Size("Bip01 R Hand", hand * limb1);

        // Feet.
        float feet = S("Feet") * h;
        Size("Bip01 L Foot", new Vector3(feet, legLength, feet) * limb2);
        Size("Bip01 R Foot", new Vector3(feet, legLength, feet) * limb3);
        Size("Bip01 L Toe0", new Vector3(feet) * limb2);
        Size("Bip01 R Toe0", new Vector3(feet) * limb3);
        Offset("Bip01 L Toe0", new Vector3(1, 1, feet) * limb2);
        Offset("Bip01 R Toe0", new Vector3(1, 1, feet) * limb3);

        // Neck and head.
        float neck = S("Neck") * thin07, neckWidth = S("Neck width") * thin06 * broad;
        Size("Bip01 Neck", new Vector3(neckWidth * frame, S("Neck length"), neck * frame));
        float headSize = S("Head size"), headFrame = (frame - 1) * 0.25f + 1;
        var head = new Vector3(headFrame * headSize * S("Head shape"), headSize, headFrame * headSize);
        Size("Bip01 Head", head);
        if (hasBone("Bip01 Jaw")) Size("Bip01 Jaw", new Vector3(S("Jaw"), 1, 1) * head);
        return r;
    }

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
    public static float MuscleDefinitionFromStats(bool female, float swimming, float athletics, float dexterity, float unarmed, float cooking, float science)
    {
        float x = (swimming + athletics + 3 * Math.Max(dexterity, unarmed) - (cooking + science)) * 0.01f / 5;
        return Math.Clamp((x - 0.2f) / ((female ? 0.99f : 0.9f) - 0.2f), 0, 1);
    }

    /// <summary>Starvation from the character's nutrition value: 1 at 4/3 or less, falling to 0 at 2.</summary>
    public static float StarvationFromNutrition(float nutrition) => Math.Clamp((1 - Math.Clamp(nutrition - 1, 0, 1)) * 1.5f, 0, 1);

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
        if (c.Character?.GetReferences("stats").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(s => s?.Type == FcsRecordType.STATS) is { } stats)
            muscle = MuscleFromStats(stats.GetFloat("strength"), stats.GetFloat("weapon smith"), stats.GetFloat("armour smith"));
        // hide stump: Kenshi reads it from the item worn under the name "armour"; the viewer takes the body-slot ARMOUR.
        var bodyArmour = c.Parts.FirstOrDefault(p => p.Record.Type == FcsRecordType.ARMOUR && p.Slot == AttachSlot.Body)?.Record;
        return new CharacterShapeInput
        {
            Sliders = sliders, Female = c.Female, Muscle = muscle,
            HideStump = (HideStump)((bodyArmour?.GetInt("hide stump") ?? 0) & 3),
        };
    }

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
            switch (key.ToLowerInvariant())
            {
                case "muscle": result = result with { Muscle = Number(text) }; applied.Add($"muscle={result.Muscle:0.###}"); continue;
                case "starve" or "starvation": result = result with { Starvation = Number(text) }; applied.Add($"starvation={result.Starvation:0.###}"); continue;
                case "legratio": result = result with { LegRatio = Number(text) }; applied.Add($"legratio={result.LegRatio:0.###}"); continue;
                case "hidestump": result = result with { HideStump = (HideStump)(int)Number(text) }; applied.Add($"hide stump={result.HideStump}"); continue;
                case "missing":
                    foreach (var limb in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        missing.Add(limb.ToLowerInvariant() switch
                        {
                            "larm" => Limb.LeftArm, "rarm" => Limb.RightArm, "lleg" => Limb.LeftLeg, "rleg" => Limb.RightLeg,
                            _ => throw new FormatException($"--shape missing: unknown limb '{limb}' (larm, rarm, lleg, rleg)"),
                        });
                    applied.Add($"missing={string.Join(",", missing)}");
                    continue;
            }
            string? slider = Sliders.FirstOrDefault(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase))
                ?? Sliders.FirstOrDefault(s => string.Equals(s.Replace(" ", "").Replace("-", ""), key.Replace(" ", "").Replace("-", "").Replace("_", ""), StringComparison.OrdinalIgnoreCase));
            if (slider is null) throw new FormatException($"--shape: unknown slider '{key}' (known: {string.Join(", ", Sliders)}, muscle, starve, legratio, missing, hidestump)");
            float value = Number(text);
            if (Math.Abs(value) <= 3) value *= 100;
            sliders[slider] = value;
            applied.Add($"{slider}={value:0.##}");
        }
        return result with { Sliders = sliders, Missing = missing };
    }

    static float Number(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}

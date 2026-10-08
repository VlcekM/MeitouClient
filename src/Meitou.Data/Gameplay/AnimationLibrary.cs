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
    public WeaponKinds Kinds { get; init; }
    public float LeftLegMin { get; init; }
    public float LeftLegMax { get; init; }
    public float RightLegMin { get; init; }
    public float RightLegMax { get; init; }
    /// <summary><c>min speed</c> / <c>max speed</c>: where the speed weight of a movement clip starts rising and has fallen back to 0 (docs/animation.md "Choosing candidates").</summary>
    public float MinSpeed { get; init; }
    public float MaxSpeed { get; init; }
    /// <summary><c>L/R leg damage ideal</c>: the leg health the leg weight peaks at.</summary>
    public float LeftLegIdeal { get; init; } = 100;
    public float RightLegIdeal { get; init; } = 100;
    /// <summary>Hit reactions: <c>stumbles</c> (the LOCATIONAL_DAMAGE string ids of the body parts whose hit plays it), <c>big stumble</c> and <c>stumble from</c> (0 front, 1 rear).</summary>
    public IReadOnlyList<string> Stumbles { get; init; } = [];
    public bool BigStumble { get; init; }
    public int StumbleFrom { get; init; }
    /// <summary>A COMBAT_TECHNIQUE (attack, block or dodge): played by the combat system's timing, never chosen as movement or idle.</summary>
    public bool Technique { get; init; }
    /// <summary>
    /// The clip moves the character (<see cref="Characters.AnimationMask.MovesCharacter"/>): the renderer keeps only the height of its <c>Bip01</c> track and the
    /// simulation moves the character by the rest (<see cref="AnimationLengths.Root"/>).
    /// </summary>
    public bool Relocates { get; init; }

    /// <summary>A COMBAT_TECHNIQUE as a definition: its <c>anim name</c>, blocks on the upper body (their lower-body tracks are deleted), the rest on the whole body.</summary>
    public static AnimationDefinition FromTechnique(GameRecord r) => new()
    {
        Id = r.StringId,
        Name = r.Name,
        Clip = r.GetString("anim name", ""),
        Area = r.GetBool("is block") && !r.GetBool("is dodge") ? AnimationArea.Upper : AnimationArea.All,
        IsAction = true,
        Technique = true,
        Relocates = Characters.AnimationMask.MovesCharacter(r),
        PlaySpeed = 1,
        WeaponLeft = 2,
        WeaponRight = 2,
        CombatMode = 2,
        StealthMode = 2,
        Kinds = WeaponKinds.All,
        LeftLegMin = 1000, LeftLegMax = 1000, RightLegMin = 1000, RightLegMax = 1000,
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
            Kinds = kinds,
            LeftLegMin = r.GetFloat("L leg damage min"),
            LeftLegMax = r.GetFloat("L leg damage max"),
            RightLegMin = r.GetFloat("R leg damage min"),
            RightLegMax = r.GetFloat("R leg damage max"),
            Relocates = Characters.AnimationMask.MovesCharacter(r),
            MinSpeed = r.GetFloat("min speed"),
            MaxSpeed = r.GetFloat("max speed"),
            LeftLegIdeal = r.GetFloat("L leg damage ideal", 100),
            RightLegIdeal = r.GetFloat("R leg damage ideal", 100),
            Stumbles = [.. r.GetReferences("stumbles").Select(x => x.TargetStringId)],
            BigStumble = r.GetBool("big stumble", true),
            StumbleFrom = r.GetInt("stumble from"),
        };
    }

    /// <summary>
    /// The speed weight of a movement clip at speed <paramref name="v"/> (<b>Verified</b>, docs/animation.md "Choosing candidates"): a ramp rising from
    /// <c>min speed</c> to <c>move speed</c> (1 throughout when <c>min speed</c> is under 1), falling to 0 at <c>max speed</c> (staying 1 when <c>max speed</c>
    /// is under 0.5), 0 outside, sharpened by 1 / (1 + e^(-10 (2r - 1))); 1 when both the speed and <c>min speed</c> are under -40.
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
        float min = useLeft ? LeftLegMin : RightLegMin, ideal = useLeft ? LeftLegIdeal : RightLegIdeal, max = useLeft ? LeftLegMax : RightLegMax;
        if (min < 1) min = -101;
        if (max > 99) max = 101;
        return Ramp(health, min, ideal, max, false, false);
    }

    static float Ramp(float v, float min, float peak, float max, bool flatBelow, bool flatAbove)
    {
        if (v < min) return 0;
        if (v <= peak) return flatBelow || peak <= min ? 1 : (v - min) / (peak - min);
        if (flatAbove) return 1;
        if (v >= max) return 0;
        return max <= peak ? 1 : (max - v) / (max - peak);
    }

    /// <summary>A leg range of 1000 .. 1000 is the records' "not used" (the limp clips constrain one leg only; <b>Observed</b>).</summary>
    static bool Leg(float health, float min, float max) => min == 1000 && max == 1000 || health >= min && health <= max;

    /// <summary>
    /// Whether the record can play for <paramref name="s"/>: the weapon in each hand (NO / YES / EITHER, and the held weapon's kind
    /// among the record's kinds), combat and stealth modes, posture, carrying and the leg damage ranges.
    /// </summary>
    public bool Fits(in AnimationStance s) => FitsExceptLegs(s) && Leg(s.LeftLeg, LeftLegMin, LeftLegMax) && Leg(s.RightLeg, RightLegMin, RightLegMax);

    /// <summary><see cref="Fits"/> without the leg ranges (movement clips weigh the legs with <see cref="LegWeight"/> instead).</summary>
    public bool FitsExceptLegs(in AnimationStance s)
    {
        static bool Hand(int wants, HandHold held, WeaponKinds kinds) => wants switch
        {
            0 => !held.Holds,
            1 => held.Holds && (kinds & held.Weapon) != 0,
            _ => true,
        };
        static bool Mode(int wants, bool on) => wants switch { 0 => !on, 1 => on, _ => true };
        return Hand(WeaponLeft, s.Left, Kinds) && Hand(WeaponRight, s.Right, Kinds)
            && Mode(CombatMode, s.Combat) && Mode(StealthMode, s.Stealth)
            && Crouched == s.Crouched && Prone == s.Prone
            && CarryingLeft == s.CarryingLeft && CarryingRight == s.CarryingRight && BeingCarried == s.BeingCarried;
    }
}

/// <summary>
/// The usable ANIMATION records of a game database (normal category, not disabled, no weather overlays; docs/animation.md
/// "Animation definitions"), and the choices a character makes from them. <b>Observed</b>, not traced in the original (docs/simulation.md
/// "Animation"): movement clips of a layer form a chain by <c>move speed</c> and a character blends the two around its
/// speed; a standing character plays one of the valid idles, another after <c>idle time</c> seconds.
/// </summary>
public sealed class AnimationLibrary
{
    readonly List<AnimationDefinition> all;

    // The leg health values the records' ranges are cut at (sorted, distinct), per leg. A stance is cached by the interval of these its leg values fall
    // in, not by the values: they change every tick of an injury that heals or rots, and a key per value would grow the caches without bound.
    readonly float[] leftBounds, rightBounds;

    public AnimationLibrary(IEnumerable<AnimationDefinition> definitions)
    {
        all = [.. definitions];
        for (int i = 0; i < all.Count; i++) if (all[i].Technique) techniques.TryAdd(all[i].Id, i);
        leftBounds = Bounds(all.SelectMany(d => new[] { d.LeftLegMin, d.LeftLegMax }));
        rightBounds = Bounds(all.SelectMany(d => new[] { d.RightLegMin, d.RightLegMax }));
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

    AnimationStance Canonical(in AnimationStance s) => s with { LeftLeg = Representative(s.LeftLeg, leftBounds), RightLeg = Representative(s.RightLeg, rightBounds) };

    /// <summary>Entries in the movement and idle caches (it stays small however the legs' health varies; for tests).</summary>
    public int CachedChoices => candidates.Count + idles.Count;

    public IReadOnlyList<AnimationDefinition> Definitions => all;

    readonly Dictionary<string, int> techniques = new(StringComparer.Ordinal);

    /// <summary>The index of the definition (ANIMATION or COMBAT_TECHNIQUE) whose record is called <paramref name="recordName"/>, or -1.</summary>
    public int IndexOfAny(string recordName) => all.FindIndex(d => d.Name == recordName);

    /// <summary>The definition's index (an ANIMATION record by name), or -1.</summary>
    public int IndexOf(string recordName) => all.FindIndex(d => d.Name == recordName && !d.Technique);

    /// <summary>The index of a COMBAT_TECHNIQUE's definition by its string id, or -1.</summary>
    public int IndexOfTechnique(string stringId) => techniques.TryGetValue(stringId, out int i) ? i : -1;

    /// <summary>
    /// The usable ANIMATION records (normal category, not disabled, no weather overlay), then the COMBAT_TECHNIQUE records of humanoids (<c>animal</c> 8 or
    /// less, the set Kenshi's startup preprocessing gathers; docs/animation.md) as <see cref="AnimationDefinition.FromTechnique"/>.
    /// </summary>
    public static AnimationLibrary FromDatabase(GameDatabase db) => new(db.OfType(FcsRecordType.ANIMATION)
        .Where(r => !r.GetBool("disabled") && r.GetInt("category") == 0 && r.GetInt("weather type") == 0)
        .OrderBy(r => r.StringId, StringComparer.Ordinal)
        .Select(AnimationDefinition.From)
        .Concat(db.OfType(FcsRecordType.COMBAT_TECHNIQUE)
            .Where(r => r.GetInt("animal") <= 8)
            .OrderBy(r => r.StringId, StringComparer.Ordinal)
            .Select(AnimationDefinition.FromTechnique)));

    readonly System.Collections.Concurrent.ConcurrentDictionary<(AnimationArea, AnimationStance), int[]> candidates = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<AnimationStance, (int Index, float Weight)[]> idles = new();

    /// <summary>
    /// The movement clips of a body half (<b>Verified</b>, docs/animation.md "Movement"): the lower list is every clip with a non-zero <c>move speed</c> whose
    /// layer is not <c>upper</c> or <c>overlay</c>, the upper list the <c>upper</c> ones; idles and actions are not movement. The stance filters them, except
    /// for the legs (weighed per call).
    /// </summary>
    int[] CandidatesFor(AnimationArea area, in AnimationStance stance) => candidates.GetOrAdd((area, stance with { LeftLeg = 100, RightLeg = 100 }), static (key, self) =>
    {
        var list = new List<int>();
        for (int i = 0; i < self.all.Count; i++)
        {
            var d = self.all[i];
            if (d.MoveSpeed == 0 || d.Idle || d.IsAction || d.Area == AnimationArea.Overlay) continue;
            if ((key.Item1 == AnimationArea.Upper) != (d.Area == AnimationArea.Upper)) continue;
            if (d.FitsExceptLegs(key.Item2)) list.Add(i);
        }
        return [.. list.OrderBy(i => self.all[i].Id, StringComparer.Ordinal)];
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
            var d = all[i];
            float w = d.SpeedWeight(v) * d.LegWeight(stance.LeftLeg, stance.RightLeg);
            if (!(w > 0)) continue;
            into.Add((i, w));
            total += w;
        }
        for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Weight / total);
    }

    /// <summary>
    /// The hit reactions (stumbles) a hit to body part <paramref name="part"/> (a LOCATIONAL_DAMAGE string id) may play, with their weights (<b>Verified</b>,
    /// docs/animation.md "Hit reactions"): the clips listing the part in <c>stumbles</c> that fit the stance, whose <c>big stumble</c> equals
    /// <paramref name="big"/> and whose <c>stumble from</c> is the side (0 front, 1 rear), weighing <c>chance</c> / 100; the one already playing
    /// (<paramref name="playing"/>) weighs a quarter of that. Every base-game stumble has <c>chance</c> 0, so when all weigh 0 they are equally likely
    /// (<b>engine choice</b>).
    /// </summary>
    public void Stumbles(string part, bool big, int side, in AnimationStance stance, int playing, List<(int Index, float Weight)> into)
    {
        int first = into.Count;
        float total = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var d = all[i];
            if (d.Stumbles.Count == 0 || d.BigStumble != big || d.StumbleFrom != side || !d.Stumbles.Contains(part) || !d.FitsExceptLegs(stance)) continue;
            float w = d.Chance / 100f * (i == playing ? 0.25f : 1);
            into.Add((i, w));
            total += w;
        }
        if (total <= 0)
            for (int k = first; k < into.Count; k++) into[k] = (into[k].Index, into[k].Index == playing ? 0.25f : 1f);
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<AnimationStance, (int Index, float Weight)[]> combatIdles = new();

    /// <summary>
    /// The idles of a character standing in combat: the upper-body <c>idle</c> clips (the guards, <c>MA idle1</c>) whose combat mode fits and whose weapon kinds
    /// include the one in hand (fists: <c>unarmed</c>), by <c>idle chance</c>. <b>Observed</b> (docs/animation.md "Combat footwork"): the original picks from the
    /// idle list by weapon type and carrying; its arm check was not decoded, so the hands' YES / NO fields are not tested (the guards want both hands).
    /// Without any, the whole-body idles that fit apart from the combat mode.
    /// </summary>
    public IReadOnlyList<(int Index, float Weight)> CombatIdles(in AnimationStance stance) => combatIdles.GetOrAdd(Canonical(stance), static (s, self) =>
    {
        var held = s.Right.Holds ? s.Right.Weapon : s.Left.Holds ? s.Left.Weapon : WeaponKinds.Unarmed;
        var list = new List<(int, float)>();
        for (int i = 0; i < self.all.Count; i++)
        {
            var d = self.all[i];
            if (d.Idle && !d.IsAction && d.Area == AnimationArea.Upper && d.CombatMode != 0 && (d.Kinds & held) != 0) list.Add((i, Math.Max(d.IdleChance, 0)));
        }
        if (list.Count == 0)
            for (int i = 0; i < self.all.Count; i++)
            {
                var d = self.all[i];
                if (d.Idle && !d.IsAction && d.Area == AnimationArea.All && d.Fits(s with { Combat = d.CombatMode == 1 })) list.Add((i, Math.Max(d.IdleChance, 0)));
            }
        return list.ToArray();
    }, this);

    /// <summary>The standing animations valid for the stance: <c>idle</c> clips of the whole body (not actions, not carry overlays) with their <c>idle chance</c>.</summary>
    public IReadOnlyList<(int Index, float Weight)> Idles(in AnimationStance stance) => idles.GetOrAdd(Canonical(stance), static (s, self) =>
    {
        var list = new List<(int, float)>();
        for (int i = 0; i < self.all.Count; i++)
        {
            var d = self.all[i];
            if (d.Idle && !d.IsAction && d.Area == AnimationArea.All && d.Fits(s)) list.Add((i, Math.Max(d.IdleChance, 0)));
        }
        return list.ToArray();
    }, this);
}

/// <summary>The length in seconds of every clip of the humanoid skeletons (what a synched movement clip needs to turn a phase into a time).</summary>
public sealed class AnimationLengths
{
    readonly Dictionary<string, float> lengths = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (float[] Times, System.Numerics.Vector2[] Offsets)> roots = new(StringComparer.OrdinalIgnoreCase);

    public static readonly AnimationLengths Empty = new();

    /// <summary>The clips of every <c>*_skeleton.skeleton</c> under <c>data/character/meshes</c> (the first file to name a clip wins).</summary>
    public static AnimationLengths Load(string installRoot)
    {
        var result = new AnimationLengths();
        var dir = Path.Combine(installRoot, "data", "character", "meshes");
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*_skeleton.skeleton", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            try
            {
                var skeleton = OgreSkeletonReader.ReadFile(file);
                var root = skeleton.Bones.FirstOrDefault(b => b.Name == "Bip01");
                foreach (var a in skeleton.Animations)
                {
                    if (!result.lengths.TryAdd(a.Name, a.Length) || root is null) continue;
                    var track = a.Tracks.FirstOrDefault(t => t.Bone == root.Handle);
                    if (track is null || track.KeyFrames.Count == 0) continue;
                    result.roots[a.Name] = ([.. track.KeyFrames.Select(k => k.Time)], [.. track.KeyFrames.Select(k => new System.Numerics.Vector2(k.Translation.X, k.Translation.Z))]);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or FcsFormatException) { }
        }
        return result;
    }

    public AnimationLengths Add(string clip, float seconds)
    {
        lengths[clip] = seconds;
        return this;
    }

    /// <summary>Sets a clip's <c>Bip01</c> track as (time, X, Z) keys (tests).</summary>
    public AnimationLengths AddRoot(string clip, float[] times, System.Numerics.Vector2[] offsets)
    {
        roots[clip] = (times, offsets);
        return this;
    }

    /// <summary>
    /// The clip's <c>Bip01</c> translation across the ground (model X and Z: Z forward, X to the character's left) at a time, relative to the binding pose,
    /// linear between keys and held past the ends; zero for a clip without one.
    /// </summary>
    public System.Numerics.Vector2 Root(string clip, float time)
    {
        if (!roots.TryGetValue(clip, out var r)) return default;
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
    public float Of(string clip) => lengths.TryGetValue(clip, out float l) && l > 0 ? l : 1;
}

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
    public static HandHold OfCategory(int skillCategory) => new(skillCategory switch
    {
        0 => WeaponKinds.Katana,
        1 => WeaponKinds.Sabre,
        2 => WeaponKinds.Blunt,
        3 => WeaponKinds.Heavy,
        4 => WeaponKinds.Hacker,
        8 => WeaponKinds.Polearm,
        5 => WeaponKinds.Unarmed,
        _ => WeaponKinds.OneHanded,
    });
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
        };
    }

    /// <summary>A leg range of 1000 .. 1000 is the records' "not used" (the limp clips constrain one leg only; <b>Observed</b>).</summary>
    static bool Leg(float health, float min, float max) => min == 1000 && max == 1000 || health >= min && health <= max;

    /// <summary>
    /// Whether the record can play for <paramref name="s"/>: the weapon in each hand (NO / YES / EITHER, and the held weapon's kind
    /// among the record's kinds), combat and stealth modes, posture, carrying and the leg damage ranges.
    /// </summary>
    public bool Fits(in AnimationStance s)
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
            && CarryingLeft == s.CarryingLeft && CarryingRight == s.CarryingRight && BeingCarried == s.BeingCarried
            && Leg(s.LeftLeg, LeftLegMin, LeftLegMax) && Leg(s.RightLeg, RightLegMin, RightLegMax);
    }
}

/// <summary>
/// The usable ANIMATION records of a game database (normal category, not disabled, no weather overlays; docs/animation.md
/// "Animation definitions"), and the choices a character makes from them. <b>Observed</b>, not traced in the original (docs/simulation.md
/// "Animation as built"): movement clips of a layer form a chain by <c>move speed</c> and a character blends the two around its
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
    public int CachedChoices => chains.Count + idles.Count;

    public IReadOnlyList<AnimationDefinition> Definitions => all;

    /// <summary>The definition's index, or -1.</summary>
    public int IndexOf(string recordName) => all.FindIndex(d => d.Name == recordName);

    public static AnimationLibrary FromDatabase(GameDatabase db) => new(db.OfType(FcsRecordType.ANIMATION)
        .Where(r => !r.GetBool("disabled") && r.GetInt("category") == 0 && r.GetInt("weather type") == 0)
        .OrderBy(r => r.StringId, StringComparer.Ordinal)
        .Select(AnimationDefinition.From));

    readonly System.Collections.Concurrent.ConcurrentDictionary<(AnimationArea, AnimationStance), Chain> chains = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<AnimationStance, (int Index, float Weight)[]> idles = new();

    /// <summary>The valid movement clips of one area and stance, sorted by <c>move speed</c>; clips of one speed are variants.</summary>
    sealed class Chain
    {
        public required float[] Speeds;
        public required (int Index, float Weight)[][] Groups;   // per distinct speed: the variants with their share (by chance)
    }

    Chain ChainFor(AnimationArea area, in AnimationStance stance) => chains.GetOrAdd((area, Canonical(stance)), static (key, self) =>
    {
        var valid = new List<int>();
        for (int i = 0; i < self.all.Count; i++)
        {
            var d = self.all[i];
            if (d.Area == key.Item1 && d.MoveSpeed > 0 && !d.Idle && !d.IsAction && d.Fits(key.Item2)) valid.Add(i);
        }
        var speeds = valid.Select(i => self.all[i].MoveSpeed).Distinct().Order().ToArray();
        var groups = speeds.Select(s =>
        {
            var g = valid.Where(i => self.all[i].MoveSpeed == s).OrderBy(i => self.all[i].Id, StringComparer.Ordinal).ToList();
            float total = g.Sum(i => Math.Max(self.all[i].Chance, 1));
            return g.Select(i => (i, Math.Max(self.all[i].Chance, 1) / total)).ToArray();
        }).ToArray();
        return new Chain { Speeds = speeds, Groups = groups };
    }, this);

    /// <summary>
    /// Adds to <paramref name="into"/> the movement clips of <paramref name="area"/> to play at <paramref name="speed"/> (units per
    /// second) as (index, weight): the valid clips sorted by <c>move speed</c>, the speed between two of them blending those two,
    /// beyond the ends the last one alone; variants of one speed split the weight by <c>chance</c>. Weights sum to 1; nothing is
    /// added when the library has no movement clip for the stance.
    /// </summary>
    public void Movement(AnimationArea area, float speed, in AnimationStance stance, List<(int Index, float Weight)> into)
    {
        var chain = ChainFor(area, stance);
        var speeds = chain.Speeds;
        if (speeds.Length == 0) return;
        if (speed <= speeds[0]) AddGroup(chain, 0, 1, into);
        else if (speed >= speeds[^1]) AddGroup(chain, speeds.Length - 1, 1, into);
        else
        {
            int k = speeds.Length - 1;
            while (speeds[k] > speed) k--;   // the last speed at or under the speed (the first is under it, so k >= 0)
            float t = (speed - speeds[k]) / (speeds[k + 1] - speeds[k]);
            AddGroup(chain, k, 1 - t, into);
            AddGroup(chain, k + 1, t, into);
        }
    }

    static void AddGroup(Chain chain, int group, float weight, List<(int Index, float Weight)> into)
    {
        foreach (var (index, share) in chain.Groups[group]) into.Add((index, weight * share));
    }

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
                foreach (var a in OgreSkeletonReader.ReadFile(file).Animations) result.lengths.TryAdd(a.Name, a.Length);
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

    /// <summary>The clip's length; 1 s for a clip not known.</summary>
    public float Of(string clip) => lengths.TryGetValue(clip, out float l) && l > 0 ? l : 1;
}

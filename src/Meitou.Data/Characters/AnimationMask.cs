using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>
/// What Kenshi's startup preprocessing does to one animation for an ANIMATION record (docs/animation.md, "Startup
/// preprocessing"): tracks it deletes and bones it marks as override bones. Bone names; bones a skeleton lacks are
/// ignored by the caller.
/// </summary>
public sealed record AnimationMask(string AnimationName, string? RecordName, string Layer, IReadOnlySet<string> DeletedBones,
        IReadOnlySet<string> OverrideBones)
{
    /// <summary>
    /// The left arm's bones (<c>override L arm</c>, <c>delete L arm</c>).
    /// </summary>
    static readonly string[] LeftArm = ["Bip01 L Clavicle", "Bip01 L UpperArm", "Bip01 L Forearm", "Bip01 L Hand"];

    /// <summary>
    /// The right arm's bones (<c>override R arm</c>, <c>delete R arm</c>).
    /// </summary>
    static readonly string[] RightArm = ["Bip01 R Clavicle", "Bip01 R UpperArm", "Bip01 R Forearm", "Bip01 R Hand"];

    /// <summary>
    /// The root, pelvis and leg bones <c>delete below waist</c> removes.
    /// </summary>
    static readonly string[] BelowWaist =
    [
        "Bip01", "Bip01 Pelvis", "Bip01 R Thigh", "Bip01 L Thigh", "Bip01 R Calf", "Bip01 L Calf",
        "Bip01 R Foot", "Bip01 L Foot", "Bip01 R Toe0", "Bip01 L Toe0",
    ];

    /// <summary>
    /// The spine, neck and head bones <c>delete body head</c> removes.
    /// </summary>
    static readonly string[] BodyHead = ["Bip01 Spine", "Bip01 Spine1", "Bip01 Spine2", "Bip01 Neck", "Bip01 Head"];

    /// <summary>
    /// The tail bones <c>delete tail</c> (default true) removes.
    /// </summary>
    static readonly string[] Tail = ["Bip01 Tail", "Bip01 Tail1", "Bip01 Tail2", "Bip01 Tail3", "Bip01 Tail4"];

    /// <summary>
    /// The hand and prop bones deleted from the <c>postures</c>, <c>shoulder set</c> and <c>neck set</c> animations.
    /// </summary>
    static readonly string[] HandsAndProps = ["Bip01 L Hand", "Bip01 R Hand", "Bip01 Prop1", "Bip01 Prop2"];

    /// <summary>
    /// The clip moves the character instead of its root bone (<see cref="MovesCharacter"/>): only the height of the <c>Bip01</c> track is posed.
    /// </summary>
    public bool Relocates { get; init; }

    /// <summary>
    /// Whether a record's clip moves the character (<b>Verified</b>, docs/animation.md "Root motion"): an ANIMATION with <c>relocates</c>; a
    /// COMBAT_TECHNIQUE unless it is a block that is not a dodge (the combat code plays attacks and dodges with root motion whatever <c>gains
    /// ground</c> says).
    /// </summary>
    public static bool MovesCharacter(GameRecord record) => record.Type == FcsRecordType.CombatTechnique
        ? !(record.GetBool("is block") && !record.GetBool("is dodge"))
        : record.GetBool("relocates");

    /// <summary>
    /// No record: nothing deleted, except the hand and prop tracks of the three posture libraries.
    /// </summary>
    public static AnimationMask Plain(string animation)
    {
        var deleted = animation is "postures" or "shoulder set" or "neck set"
            ? new HashSet<string>(HandsAndProps, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return new(animation, null, "all", deleted, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// The mask an ANIMATION (or ANIMAL_ANIMATION, which is not preprocessed: no deletions) record gives its animation.
    /// </summary>
    public static AnimationMask FromRecord(GameRecord record)
    {
        string animation = record.GetString("anim name", record.Name);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var overrides = new HashSet<string>(StringComparer.Ordinal);
        if (record.Type == FcsRecordType.Animation)
        {
            if (record.GetBool("override L arm")) overrides.UnionWith([.. LeftArm, "Bip01 Prop1"]);
            if (record.GetBool("override R arm")) overrides.UnionWith([.. RightArm, "Bip01 Prop2"]);
            if (record.GetBool("override head")) overrides.UnionWith(["Bip01 Neck", "Bip01 Head"]);
            if (record.GetBool("delete root")) deleted.Add("Bip01");
            if (record.GetBool("delete L arm")) deleted.UnionWith(LeftArm);
            if (record.GetBool("delete R arm")) deleted.UnionWith(RightArm);
            if (record.GetBool("delete below waist")) deleted.UnionWith(BelowWaist);
            if (record.GetBool("delete spine0")) deleted.Add("Bip01 Spine");
            if (record.GetBool("delete body head")) deleted.UnionWith(BodyHead);
            if (record.GetBool("delete weapons")) deleted.UnionWith(["Bip01 Prop2", "Bip01 Prop1"]);
            if (record.GetBool("delete tail", true)) deleted.UnionWith(Tail);
        }
        if (animation is "postures" or "shoulder set" or "neck set") deleted.UnionWith(HandsAndProps);
        return new AnimationMask(animation, record.Name, record.GetString("layer", "upper"), deleted,
                overrides) { Relocates = MovesCharacter(record) };
    }

    /// <summary>
    /// The mask of a COMBAT_TECHNIQUE (docs/animation.md "Startup preprocessing", <b>Verified</b>): a block that is not a dodge loses its lower-body
    /// tracks (the <c>delete below waist</c> set) and plays on the upper body; attacks and dodges keep every track. Kenshi also adds a clone of each
    /// block with override bones on the right arm, which neither the defending state nor the stumble break-out asks for (docs/game/combat.md "Hit
    /// reactions"): this is the one they play.
    /// </summary>
    public static AnimationMask FromTechnique(GameRecord record)
    {
        bool block = record.GetBool("is block") && !record.GetBool("is dodge");
        var deleted = new HashSet<string>(block ? BelowWaist : [], StringComparer.Ordinal);
        return new AnimationMask(record.GetString("anim name", record.Name), record.Name, block ? "upper" : "all", deleted,
                new HashSet<string>(StringComparer.Ordinal))
        {
            Relocates = MovesCharacter(record),
        };
    }

    /// <summary>
    /// The mask for <paramref name="name"/>: an ANIMATION record of that name, else a COMBAT_TECHNIQUE of that name, else the first ANIMATION record
    /// whose <c>anim name</c> is it (the one that keeps the name when duplicates are renamed), else <see cref="Plain"/>.
    /// </summary>
    public static AnimationMask Find(GameDatabase? db, string name)
    {
        if (db is null) return Plain(name);
        var index = Index.Of(db);
        if (index.ByName.TryGetValue(name, out var byName)) return FromRecord(byName);
        if (index.Techniques.TryGetValue(name, out var technique)) return FromTechnique(technique);
        return index.ByAnimation.TryGetValue(name, out var byAnimation) ? FromRecord(byAnimation) : Plain(name);
    }

    /// <summary>
    /// <see cref="Find"/>'s lookups over a database, the first record in its order for each name (case-insensitive), made once per
    /// <see cref="GameDatabase.Version"/>: scanning every record for each layer of each new rig took two seconds of a town's loading with a heavy
    /// mod list.
    /// </summary>
    sealed class Index
    {
        /// <summary>
        /// The index of each database, replaced when its version changes.
        /// </summary>
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameDatabase, Index> Made = new();

        /// <summary>
        /// The <see cref="GameDatabase.Version"/> the index was made at.
        /// </summary>
        int _version;

        /// <summary>
        /// The first ANIMATION of each name, the first COMBAT_TECHNIQUE of each name, and the first ANIMATION of each <c>anim name</c> (all
        /// ignoring case).
        /// </summary>
        public readonly Dictionary<string, GameRecord> ByName = new(StringComparer.OrdinalIgnoreCase),
            Techniques = new(StringComparer.OrdinalIgnoreCase),
            ByAnimation = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The index of <paramref name="db"/>: the one made for its current version, else a new one.
        /// </summary>
        public static Index Of(GameDatabase db)
        {
            if (Made.TryGetValue(db, out var index) && index._version == db.Version) return index;
            index = new Index { _version = db.Version };
            foreach (var r in db.Records.Values)
            {
                if (r.Type == FcsRecordType.Animation)
                {
                    index.ByName.TryAdd(r.Name, r);
                    index.ByAnimation.TryAdd(r.GetString("anim name"), r);
                }
                else if (r.Type == FcsRecordType.CombatTechnique) index.Techniques.TryAdd(r.Name, r);
            }
            Made.AddOrUpdate(db, index);
            return index;
        }
    }
}
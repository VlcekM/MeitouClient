using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>
/// What Kenshi's startup preprocessing does to one animation for an ANIMATION record (docs/animation.md, "Startup
/// preprocessing"): tracks it deletes and bones it marks as override bones. Bone names; bones a skeleton lacks are
/// ignored by the caller.
/// </summary>
public sealed record AnimationMask(string AnimationName, string? RecordName, string Layer, IReadOnlySet<string> DeletedBones, IReadOnlySet<string> OverrideBones)
{
    static readonly string[] LeftArm = ["Bip01 L Clavicle", "Bip01 L UpperArm", "Bip01 L Forearm", "Bip01 L Hand"];
    static readonly string[] RightArm = ["Bip01 R Clavicle", "Bip01 R UpperArm", "Bip01 R Forearm", "Bip01 R Hand"];
    static readonly string[] BelowWaist =
    [
        "Bip01", "Bip01 Pelvis", "Bip01 R Thigh", "Bip01 L Thigh", "Bip01 R Calf", "Bip01 L Calf",
        "Bip01 R Foot", "Bip01 L Foot", "Bip01 R Toe0", "Bip01 L Toe0",
    ];
    static readonly string[] BodyHead = ["Bip01 Spine", "Bip01 Spine1", "Bip01 Spine2", "Bip01 Neck", "Bip01 Head"];
    static readonly string[] Tail = ["Bip01 Tail", "Bip01 Tail1", "Bip01 Tail2", "Bip01 Tail3", "Bip01 Tail4"];
    static readonly string[] HandsAndProps = ["Bip01 L Hand", "Bip01 R Hand", "Bip01 Prop1", "Bip01 Prop2"];

    /// <summary>No record: nothing deleted, except the hand and prop tracks of the three posture libraries.</summary>
    public static AnimationMask Plain(string animation) => new(animation, null, "all",
        animation is "postures" or "shoulder set" or "neck set" ? new HashSet<string>(HandsAndProps, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    /// <summary>The mask an ANIMATION (or ANIMAL_ANIMATION, which is not preprocessed: no deletions) record gives its animation.</summary>
    public static AnimationMask FromRecord(GameRecord record)
    {
        string animation = record.GetString("anim name", record.Name);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var overrides = new HashSet<string>(StringComparer.Ordinal);
        if (record.Type == FcsRecordType.ANIMATION)
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
        return new AnimationMask(animation, record.Name, record.GetString("layer", "upper"), deleted, overrides);
    }

    /// <summary>
    /// The mask for <paramref name="name"/>: an ANIMATION record of that name, else the first ANIMATION record whose
    /// <c>anim name</c> is it (the one that keeps the name when duplicates are renamed), else <see cref="Plain"/>.
    /// </summary>
    public static AnimationMask Find(GameDatabase? db, string name)
    {
        if (db is null) return Plain(name);
        var records = db.OfType(FcsRecordType.ANIMATION).ToList();
        var byName = records.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return FromRecord(byName);
        var byAnimation = records.FirstOrDefault(r => string.Equals(r.GetString("anim name"), name, StringComparison.OrdinalIgnoreCase));
        return byAnimation is not null ? FromRecord(byAnimation) : Plain(name);
    }
}

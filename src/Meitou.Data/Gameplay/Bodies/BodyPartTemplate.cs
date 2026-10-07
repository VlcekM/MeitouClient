namespace Meitou.Data.Gameplay.Bodies;

/// <summary><c>BodyPartType</c> of a LOCATIONAL_DAMAGE record (docs/game/character-stats.md, <b>Verified</b>).</summary>
public enum BodyPartType
{
    Torso = 0,
    Leg = 1,
    Arm = 2,
    Head = 3,
}

/// <summary>Which side of the body a part is on, from its <c>collapse part</c> bits (4 and 0x20 left, 2 and 0x10 right).</summary>
public enum BodySide
{
    None = 0,
    Left = 1,
    Right = 2,
}

/// <summary>
/// A LOCATIONAL_DAMAGE record: what a body part is (docs/game/character-stats.md "What a body part is"; <b>Verified</b>, all nine base records). The
/// part's hit weight and base HP come from the race's <c>combat anatomy</c> reference, not from here.
/// </summary>
public sealed record BodyPartTemplate
{
    public required string StringId { get; init; }
    public string Name { get; init; } = "";
    public BodyPartType Type { get; init; }
    /// <summary><c>collapses</c>: a part that is down makes its limb go limp.</summary>
    public bool Collapses { get; init; }
    /// <summary><c>death</c>: a vital part. Down means knocked out, destroyed means dead.</summary>
    public bool Vital { get; init; }
    /// <summary><c>severance</c>: the part can be cut off.</summary>
    public bool Severance { get; init; }
    /// <summary><c>collapse part</c> bit mask: 1 whole body, 2 right arm, 4 left arm, 0x10 right leg, 0x20 left leg.</summary>
    public int CollapsePart { get; init; }
    /// <summary><c>affects move speed</c> (consumer <b>Unknown</b>).</summary>
    public float AffectsMoveSpeed { get; init; }
    /// <summary><c>affects skills</c> (consumer <b>Unknown</b>).</summary>
    public float AffectsSkills { get; init; }
    /// <summary><c>KO mult</c>: weight of this part's damage in the knock-out time.</summary>
    public float KoMult { get; init; } = 1;

    public BodySide Side => CollapsePart is 4 or 0x20 ? BodySide.Left : CollapsePart is 2 or 0x10 ? BodySide.Right : BodySide.None;

    /// <summary>
    /// The limb slot of limb replacement and RACE <c>severed limbs</c>: 0 left arm, 1 right arm, 2 left leg, 3 right leg (<b>Verified</b>); -1 for
    /// the head, the torso and any part without a side. The animals' forelegs (type ARM) take the arm slots.
    /// </summary>
    public int LimbSlot => (Type, Side) switch
    {
        (BodyPartType.Arm, BodySide.Left) => 0,
        (BodyPartType.Arm, BodySide.Right) => 1,
        (BodyPartType.Leg, BodySide.Left) => 2,
        (BodyPartType.Leg, BodySide.Right) => 3,
        _ => -1,
    };

    public static BodyPartTemplate From(GameRecord r) => new()
    {
        StringId = r.StringId,
        Name = r.Name,
        Type = (BodyPartType)RecordReading.Int(r, "body part type", 0),
        Collapses = RecordReading.Bool(r, "collapses", false),
        Vital = RecordReading.Bool(r, "death", false),
        Severance = RecordReading.Bool(r, "severance", false),
        CollapsePart = RecordReading.Int(r, "collapse part", 0),
        AffectsMoveSpeed = RecordReading.Float(r, "affects move speed", 0),
        AffectsSkills = RecordReading.Float(r, "affects skills", 0),
        KoMult = RecordReading.Float(r, "KO mult", 1),
    };
}

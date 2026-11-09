namespace Meitou.Data.Gameplay.Bodies;

/// <summary>
/// <c>BodyPartType</c> of a LOCATIONAL_DAMAGE record (docs/game/character-stats.md, <b>Verified</b>).
/// </summary>
public enum BodyPartType
{
    /// <summary>
    /// 0: <c>PART_TORSO</c> (fcs_enums.def).
    /// </summary>
    Torso = 0,
    /// <summary>
    /// 1: <c>PART_LEG</c>.
    /// </summary>
    Leg = 1,
    /// <summary>
    /// 2: <c>PART_ARM</c> (also an animal's foreleg).
    /// </summary>
    Arm = 2,
    /// <summary>
    /// 3: <c>PART_HEAD</c>.
    /// </summary>
    Head = 3,
}

/// <summary>
/// Which side of the body a part is on, from its <c>collapse part</c> bits (4 and 0x20 left, 2 and 0x10 right).
/// </summary>
public enum BodySide
{
    /// <summary>
    /// Neither side: the head, the torso, any part whose <c>collapse part</c> names no side.
    /// </summary>
    None = 0,
    /// <summary>
    /// <c>collapse part</c> 4 (left arm) or 0x20 (left leg).
    /// </summary>
    Left = 1,
    /// <summary>
    /// <c>collapse part</c> 2 (right arm) or 0x10 (right leg).
    /// </summary>
    Right = 2,
}

/// <summary>
/// A LOCATIONAL_DAMAGE record: what a body part is (docs/game/character-stats.md "What a body part is"; <b>Verified</b>, all nine base records). The
/// part's hit weight and base HP come from the race's <c>combat anatomy</c> reference, not from here.
/// </summary>
public sealed record BodyPartTemplate
{
    /// <summary>
    /// The LOCATIONAL_DAMAGE's string id.
    /// </summary>
    public required string StringId { get; init; }

    /// <summary>
    /// The LOCATIONAL_DAMAGE's name in the active language.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// <c>body part type</c> (default 0, torso).
    /// </summary>
    public BodyPartType Type { get; init; }

    /// <summary>
    /// <c>collapses</c>: a part that is down makes its limb go limp.
    /// </summary>
    public bool Collapses { get; init; }

    /// <summary>
    /// <c>death</c>: a vital part. Down means knocked out, destroyed means dead.
    /// </summary>
    public bool Vital { get; init; }

    /// <summary>
    /// <c>severance</c>: the part can be cut off.
    /// </summary>
    public bool Severance { get; init; }

    /// <summary>
    /// <c>collapse part</c> bit mask: 1 whole body, 2 right arm, 4 left arm, 0x10 right leg, 0x20 left leg.
    /// </summary>
    public int CollapsePart { get; init; }

    /// <summary>
    /// <c>affects move speed</c> (consumer <b>Unknown</b>).
    /// </summary>
    public float AffectsMoveSpeed { get; init; }

    /// <summary>
    /// <c>affects skills</c> (consumer <b>Unknown</b>).
    /// </summary>
    public float AffectsSkills { get; init; }

    /// <summary>
    /// <c>KO mult</c>: weight of this part's damage in the knock-out time.
    /// </summary>
    public float KoMult { get; init; } = 1;

    /// <summary>
    /// <c>bone name</c> and <c>bone name 2</c>: the skeleton bones of the part (a bolt sticks in one, docs/game/combat.md "Bolt visuals").
    /// </summary>
    public string BoneName { get; init; } = "";

    /// <summary>
    /// <c>bone name 2</c>: the part's second bone; empty when the part has only one.
    /// </summary>
    public string BoneName2 { get; init; } = "";

    /// <summary>
    /// <c>pain anim</c>: the ANIMATION record (string id) a hurt character holds this part with, and the reference's first value, the health per cent
    /// under which it does (<c>FUN_14064a100</c>, docs/animation.md "Injury holds"); null when the record has none.
    /// </summary>
    public string? PainAnim { get; init; }

    /// <summary>
    /// The first <c>pain anim</c> reference's value 0: the health per cent under which the hold plays; 0 when the record has none.
    /// </summary>
    public int PainThreshold { get; init; }

    /// <summary>
    /// The side of the part from its <c>collapse part</c> bits.
    /// </summary>
    public BodySide Side => CollapsePart is 4 or 0x20 ? BodySide.Left : CollapsePart is 2 or 0x10 ? BodySide.Right : BodySide.None;

    /// <summary>
    /// The part's place in the original's medical part list (<c>FUN_14064e410</c>, <b>Verified</b> by decompilation; docs/game/character-stats.md
    /// "Part order"): a table by type (head 0, torso 10, arm 20, leg 30) plus 1 for a right part (<c>collapse part</c> 2 or 0x10), sorted ascending.
    /// Other types (none in the data) go last (engine choice: the original reads past its table).
    /// </summary>
    public int MedicalOrder => Type switch
    {
        BodyPartType.Head => 0,
        BodyPartType.Torso => 10,
        BodyPartType.Arm => 20,
        BodyPartType.Leg => 30,
        _ => 255
    }
        + (CollapsePart is 2 or 0x10 ? 1 : 0);

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

    /// <summary>
    /// Reads a LOCATIONAL_DAMAGE record.
    /// </summary>
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
        BoneName = r.GetString("bone name"),
        BoneName2 = r.GetString("bone name 2"),
        PainAnim = r.GetReferences("pain anim").Select(x => x.TargetStringId).FirstOrDefault(),
        PainThreshold = r.GetReferences("pain anim").Select(x => x.Values.Value0).FirstOrDefault(),
    };
}

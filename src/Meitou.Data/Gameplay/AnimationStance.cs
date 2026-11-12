namespace Meitou.Data.Gameplay;

/// <summary>
/// What is in a hand: nothing, or a weapon of some kind (the animation records' <c>has weapon L/R</c> asks for this).
/// </summary>
public readonly record struct HandHold(WeaponKinds Weapon)
{
    /// <summary>
    /// Nothing in the hand.
    /// </summary>
    public static readonly HandHold Empty = new(WeaponKinds.None);

    /// <summary>
    /// True when the hand holds a weapon of some kind.
    /// </summary>
    public bool Holds => Weapon != WeaponKinds.None;

    /// <summary>
    /// The kind of a WEAPON's <c>skill category</c> (WeaponCategory: 0 katanas, 1 sabres, 2 blunt, 3 heavy, 4 hackers, 8
    /// polearms; docs/game/combat.md).
    /// </summary>
    public static HandHold OfCategory(int skillCategory) => new(Combat.WeaponCategories.AnimationKind(skillCategory));
}

/// <summary>
/// The state an animation is chosen for besides the speed: what the hands hold, combat and stealth, posture, carrying and the legs. The default is a
/// healthy character walking about with its weapons sheathed.
/// </summary>
public readonly record struct AnimationStance
{
    /// <summary>
    /// What the left hand holds.
    /// </summary>
    public HandHold Left { get; init; }

    /// <summary>
    /// What the right hand holds.
    /// </summary>
    public HandHold Right { get; init; }

    /// <summary>
    /// The character is in combat mode.
    /// </summary>
    public bool Combat { get; init; }

    /// <summary>
    /// The character is in stealth mode.
    /// </summary>
    public bool Stealth { get; init; }

    /// <summary>
    /// The character is crouched.
    /// </summary>
    public bool Crouched { get; init; }

    /// <summary>
    /// The character is prone.
    /// </summary>
    public bool Prone { get; init; }

    /// <summary>
    /// The character carries something on the left.
    /// </summary>
    public bool CarryingLeft { get; init; }

    /// <summary>
    /// The character carries something on the right.
    /// </summary>
    public bool CarryingRight { get; init; }

    /// <summary>
    /// The character is being carried.
    /// </summary>
    public bool BeingCarried { get; init; }

    /// <summary>
    /// Health of the legs in per cent (100 healthy, negative past the point of function); compared with the records' leg damage ranges.
    /// </summary>
    public float LeftLeg { get; init; }

    /// <summary>
    /// The right leg's health in per cent (see <see cref="LeftLeg"/>).
    /// </summary>
    public float RightLeg { get; init; }

    /// <summary>
    /// The animation class's current category (<c>AC+0xf0</c>, <b>Verified</b> FUN_140520500): 4 in deep water for a race that swims, 5 held down or
    /// crawling, else 0. Only the hit-reaction pick tests it.
    /// </summary>
    public int Category { get; init; }

    /// <summary>
    /// The default stance: both legs at 100.
    /// </summary>
    public AnimationStance()
    {
        LeftLeg = 100;
        RightLeg = 100;
    }
}

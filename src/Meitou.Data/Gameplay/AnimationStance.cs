namespace Meitou.Data.Gameplay;

/// <summary>
/// What is in a hand: nothing, or a weapon of some kind (the animation records' <c>has weapon L/R</c> asks for this).
/// </summary>
public readonly record struct HandHold(WeaponKinds Weapon)
{
    public static readonly HandHold Empty = new(WeaponKinds.None);
    public bool Holds => Weapon != WeaponKinds.None;

    /// <summary>
    /// The kind of a WEAPON's <c>skill category</c> (WeaponCategory: 0 katanas, 1 sabres, 2 blunt, 3 heavy, 4 hackers, 8 polearms;
    /// docs/game/combat.md).
    /// </summary>
    public static HandHold OfCategory(int skillCategory) => new(Combat.WeaponCategories.AnimationKind(skillCategory));
}

/// <summary>
/// The state an animation is chosen for besides the speed: what the hands hold, combat and stealth, posture, carrying and the legs. The default is a
/// healthy character walking about with its weapons sheathed.
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
    /// <summary>
    /// Health of the legs in per cent (100 healthy, negative past the point of function); compared with the records' leg damage ranges.
    /// </summary>
    public float LeftLeg { get; init; }
    public float RightLeg { get; init; }
    /// <summary>
    /// The animation class's current category (<c>AC+0xf0</c>, <b>Verified</b> FUN_140520500): 4 in deep water for a race that swims, 5 held down or
    /// crawling, else 0. Only the hit-reaction pick tests it.
    /// </summary>
    public int Category { get; init; }

    public AnimationStance()
    {
        LeftLeg = 100;
        RightLeg = 100;
    }
}

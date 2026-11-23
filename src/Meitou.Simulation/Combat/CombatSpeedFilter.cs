namespace Meitou.Simulation.Combat;

/// <summary>
/// Which play rate or speed a combat speed filter is asked about.
/// </summary>
public enum CombatSpeedKind : byte
{
    /// <summary>
    /// The attack techniques' play rate (CharStats <c>+0x188</c>), which the chase lead reads too.
    /// </summary>
    Attack,

    /// <summary>
    /// The block techniques' play rate (CharStats <c>+0x18c</c>); dodges play at 1 and are not asked.
    /// </summary>
    Block,

    /// <summary>
    /// A crossbow reload's speed, asked as 1 when a reload starts: the reload takes the game's reload time divided by the answer.
    /// </summary>
    Reload,

    /// <summary>
    /// A crossbow bolt's flight speed in world units a second (the crossbow's <c>shoot speed</c>, the loaded ammunition's multiplier and the
    /// shot's height factor), asked as the shot is fired, and the speed the aim leads its target by.
    /// </summary>
    Projectile,
}

/// <summary>
/// A fighter's attack or block play rate, crossbow reload speed or bolt speed as the game computed it (docs/modding.md "Combat speed").
/// </summary>
public readonly record struct CombatSpeedAsk(long Tick, CharacterId Character, CombatSpeedKind Kind, float Rate);

/// <summary>
/// A system asked about every attack and block play rate the game computes (docs/modding.md "Combat speed"): when a fighter starts a swing or a
/// block, for the chase lead, and for the HUD's figures; and about every crossbow reload and bolt speed (<see cref="CombatSpeedKind"/>). It answers
/// the rate to use (1.2 is the game's attack cap, 4 its block cap; an answer may pass them). It runs on any thread, concurrently, so it must be
/// quick, thread-safe and a pure read of the ask and of world state the tick has settled. A throw or an answer that is not a finite number above 0
/// leaves the rate as it came; the filter is still asked for the rest of that tick, not from the next, and the fault enters the state hash. Engine
/// API, not in the original.
/// </summary>
public interface ICombatSpeedFilter : ITickSystem
{
    /// <summary>
    /// The play rate to use.
    /// </summary>
    float Rate(World world, in CombatSpeedAsk ask);
}

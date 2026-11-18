namespace Meitou.Simulation.Crime;

/// <summary>
/// A system told of what the crime rules do (docs/modding.md "Crimes"): a crime set pending on a culprit (an assault the engine notices: an
/// unprovoked attack order, an NPC's landed blow or aimed bolt, a stealth knock-out swing, a hit between the police and another; and every other
/// crime the game sets), a witness seeing it, its booking as a bounty, and a bounty's expiry, each once (a crime already pending with the same
/// code is only refreshed, and not told again). Called serially on the tick thread, in system order, once the crime system has made the change;
/// it may read the world and change its own state, and must not change characters, crimes or other world state in the call (gameplay changes go
/// through commands). One that throws is disabled for that world, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface ICrimeObserver : ITickSystem
{
    /// <summary>
    /// The crime rules did <paramref name="crime"/>'s <see cref="CrimeEvent.Kind"/>.
    /// </summary>
    void CrimeNoted(World world, in CrimeEvent crime);
}

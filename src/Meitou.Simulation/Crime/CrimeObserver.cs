namespace Meitou.Simulation.Crime;

/// <summary>
/// A system told of each change the crime rules make to a character's pending crime or bounties (docs/modding.md "Crimes"): a crime set pending
/// on a culprit (an assault the engine notices: an unprovoked attack order, an NPC's landed blow or aimed bolt, a stealth knock-out swing, a hit
/// between the police and another; and every other crime the game sets), a witness seeing it, a bounty raised (a booking or a dialogue line), a
/// pending crime ended, a bounty expired or cleared, each once (a crime already pending with the same code is only refreshed, and not told
/// again). Called serially, in system order, once the crime system has made the change: during a tick on the thread running it, and for the
/// paused interface's commands between ticks (the dialogue window, which pauses the game: its bounties, recruiting, buying free; character
/// trades, squad commands such as a dismissal, a theft, a fence's sale) on the host's thread, with the coming tick's number. It may read the
/// world and change its own hashed state, and must not change characters, crimes or other world state in the call (gameplay changes go through
/// commands). One that throws is disabled for that world, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface ICrimeObserver : ITickSystem
{
    /// <summary>
    /// The crime rules did <paramref name="crime"/>'s <see cref="CrimeEvent.Kind"/>.
    /// </summary>
    void CrimeNoted(World world, in CrimeEvent crime);
}

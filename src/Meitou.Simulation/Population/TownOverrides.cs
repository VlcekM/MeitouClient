namespace Meitou.Simulation.Population;

/// <summary>
/// The population's state for towns that change version away from their zone's load (docs/simulation.md "Town overrides"): the towns code mods
/// hold, the versions each town held since the world states it reads last changed (so a town is not switched back and forth at every look), the
/// platoons left homeless by a change, and how many towns changed off screen.
/// </summary>
sealed class TownOverrides
{
    #region Holds

    /// <summary>
    /// The towns a code mod drives itself, by site index (<see cref="PopulationSystem.HoldTown"/>).
    /// </summary>
    readonly SortedSet<int> _held = [];

    /// <summary>
    /// Whether a code mod holds <paramref name="site"/>.
    /// </summary>
    public bool IsHeld(int site) => _held.Contains(site);

    /// <summary>
    /// Holds or releases <paramref name="site"/>.
    /// </summary>
    public void Hold(int site, bool held)
    {
        if (held) _held.Add(site);
        else _held.Remove(site);
    }

    #endregion

    #region Versions held

    /// <summary>
    /// The fingerprint of the world states' inputs the versions below were held under (<see cref="Inputs"/>).
    /// </summary>
    long _inputs = long.MinValue;
    /// <summary>
    /// For each town changed off screen, the versions it held since <see cref="_inputs"/> (<see cref="PopulationSystem.VersionOf"/>).
    /// </summary>
    readonly Dictionary<int, HashSet<string>> _visited = [];

    /// <summary>
    /// Takes the fingerprint of what the world states read now; a change forgets every version held, so each town may move again.
    /// </summary>
    public void Inputs(long fingerprint)
    {
        if (fingerprint == _inputs) return;

        _inputs = fingerprint;
        _visited.Clear();
    }

    /// <summary>
    /// Whether <paramref name="site"/> already held <paramref name="version"/> since the inputs last changed: it would be switched back.
    /// </summary>
    public bool Revisits(int site, string version) => _visited.TryGetValue(site, out var held) && held.Contains(version);

    /// <summary>
    /// Records that <paramref name="site"/> went from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    public void Visited(int site, string from, string to)
    {
        if (!_visited.TryGetValue(site, out var held)) _visited[site] = held = new HashSet<string>(StringComparer.Ordinal);
        held.Add(from);
        held.Add(to);
    }

    #endregion

    #region Displaced platoons

    /// <summary>
    /// The platoons a town's change left homeless (<see cref="Factions.Platoon.Displaced"/>), by id, dropped at the next look.
    /// </summary>
    readonly SortedSet<int> _displaced = [];

    /// <summary>
    /// Adds <paramref name="platoon"/> to those dropped at the next look.
    /// </summary>
    public void Displace(int platoon) => _displaced.Add(platoon);

    /// <summary>
    /// Takes <paramref name="platoon"/> off that list (a mod moved it to another town).
    /// </summary>
    public void Claim(int platoon) => _displaced.Remove(platoon);

    /// <summary>
    /// The platoons to drop now, in id order; the list is emptied.
    /// </summary>
    public List<int> TakeDisplaced()
    {
        var list = new List<int>(_displaced);
        _displaced.Clear();
        return list;
    }

    #endregion

    #region Counter and state

    /// <summary>
    /// How many towns changed version off screen in this world.
    /// </summary>
    public int OffscreenSwitches { get; private set; }

    /// <summary>
    /// Counts a town changed off screen.
    /// </summary>
    public void CountOffscreen() => OffscreenSwitches++;

    /// <summary>
    /// Whether there is anything to hash (so worlds without town changes or holds hash as before).
    /// </summary>
    public bool Any => _held.Count > 0 || _displaced.Count > 0 || OffscreenSwitches > 0;

    /// <summary>
    /// Adds the holds, the platoons waiting to go and the count to the world's state hash.
    /// </summary>
    public void Hash(ref StateHasher h)
    {
        h.Add(0x54534F56);
        h.Add(OffscreenSwitches);
        h.Add(_held.Count);
        foreach (int site in _held) h.Add(site);
        h.Add(_displaced.Count);
        foreach (int platoon in _displaced) h.Add(platoon);
    }

    #endregion
}

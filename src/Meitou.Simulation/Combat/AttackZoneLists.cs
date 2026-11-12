namespace Meitou.Simulation.Combat;

/// <summary>
/// The characters each fighter's attack zone held when it last looked (the original's list +0x1e0, <c>calculateTargetsInAttackZone</c>
/// FUN_1406087a0: built at the attack start and at each blow's impact), <see cref="CombatSystem.ZoneCapacity"/> slots per fighter and a count;
/// double-buffered like the combat slots, so a defender reads the attacker's list of the last tick. The <see cref="CombatSystem"/> owns it.
/// </summary>
sealed class AttackZoneLists
{
    /// <summary>
    /// The lists as of the last tick and this tick's, <see cref="CombatSystem.ZoneCapacity"/> slots per fighter.
    /// </summary>
    int[] _zoneRead = [], _zoneWrite = [];

    /// <summary>
    /// How many slots of each fighter's list are used, as of the last tick and this tick.
    /// </summary>
    byte[] _zoneCountRead = [], _zoneCountWrite = [];

    /// <summary>
    /// Whether <paramref name="defender"/> is in <paramref name="attacker"/>'s attack zone list (<c>FUN_140608460</c>), as of the last tick.
    /// </summary>
    internal bool Contains(int attacker, int defender)
    {
        int at = attacker * CombatSystem.ZoneCapacity;
        for (int n = 0; n < _zoneCountRead[attacker]; n++)
            if (_zoneRead[at + n] == defender) return true;

        return false;
    }

    /// <summary>
    /// Keeps fighter <paramref name="i"/>'s zone scan as its list (its own slot of the write buffer: partition-safe).
    /// </summary>
    internal void Keep(int i, in CombatSystem.ZoneSlots slots, int count)
    {
        int at = i * CombatSystem.ZoneCapacity;
        for (int n = 0; n < count; n++) _zoneWrite[at + n] = slots[n];

        _zoneCountWrite[i] = (byte)count;
    }

    /// <summary>
    /// Empties fighter <paramref name="i"/>'s list as of the last tick (a new character in its slot).
    /// </summary>
    internal void Clear(int i) => _zoneCountRead[i] = 0;

    /// <summary>
    /// Grows (or shrinks) the buffers to <paramref name="size"/> fighters.
    /// </summary>
    internal void Resize(int size)
    {
        Array.Resize(ref _zoneRead, size * CombatSystem.ZoneCapacity);
        Array.Resize(ref _zoneWrite, size * CombatSystem.ZoneCapacity);
        Array.Resize(ref _zoneCountRead, size);
        Array.Resize(ref _zoneCountWrite, size);
    }

    /// <summary>
    /// Starts this tick's lists as the last tick's, for the first <paramref name="highWater"/> fighters.
    /// </summary>
    internal void Copy(int highWater)
    {
        Array.Copy(_zoneRead, _zoneWrite, highWater * CombatSystem.ZoneCapacity);
        Array.Copy(_zoneCountRead, _zoneCountWrite, highWater);
    }

    /// <summary>
    /// This tick's lists become the last tick's.
    /// </summary>
    internal void Swap()
    {
        (_zoneRead, _zoneWrite) = (_zoneWrite, _zoneRead);
        (_zoneCountRead, _zoneCountWrite) = (_zoneCountWrite, _zoneCountRead);
    }

    /// <summary>
    /// Adds fighter <paramref name="i"/>'s list as of the last tick: its count and slots.
    /// </summary>
    internal void Hash(ref StateHasher hasher, int i)
    {
        hasher.Add(_zoneCountRead[i]);
        for (int n = 0; n < _zoneCountRead[i]; n++) hasher.Add(_zoneRead[i * CombatSystem.ZoneCapacity + n]);
    }
}

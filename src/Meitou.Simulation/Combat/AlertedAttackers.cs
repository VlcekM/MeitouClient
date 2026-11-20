namespace Meitou.Simulation.Combat;

/// <summary>
/// The entries the alerts put on a character's engaged list (CombatClass <c>+0x1f8</c>, <c>FUN_140666e20</c>, <b>Verified</b>; docs/game/combat.md
/// "Attack slots and circling"): the melee alert <c>AI::underMeleeAttack</c> (<c>FUN_140999f00</c>) puts the attacker there whether or not it
/// fights the character (a failed stealth knock-out's attacker does not). The askers that join the list in the fight are Meitou's
/// <see cref="CombatSlot.Engaged"/> flags; this list holds the rest. An entry is stamped with its owner's <see cref="CombatSlot.ListEpoch"/>, so the
/// end of the owner's fight task (<c>FUN_1406659b0</c> mode 2 zeroes the list) leaves it out; a blow the owner lands on it, or one it blocks, takes
/// it off (<c>FUN_14060a2f0</c> → <c>FUN_1406657d0</c>). Written in serial phases only; read anywhere after the schedule.
/// </summary>
internal sealed class AlertedAttackers
{
    /// <summary>
    /// The most entries an engaged list holds: <c>FUN_140666e20</c> refuses a push once it holds more than 4.
    /// </summary>
    public const int Cap = 5;

    /// <summary>
    /// The owner's generation the entries of a slot belong to, by slot.
    /// </summary>
    int[] _generation = [];
    /// <summary>
    /// The entries by owner slot, in push order (null: none yet).
    /// </summary>
    List<(CharacterId Attacker, int Epoch)>?[] _lists = [];

    /// <summary>
    /// Puts <paramref name="attacker"/> on the list of <paramref name="owner"/> (its generation <paramref name="generation"/>, its list epoch now
    /// <paramref name="epoch"/>): nothing when it is there already or when the list, with the <paramref name="askers"/> the fight put there,
    /// holds <see cref="Cap"/>. Serial.
    /// </summary>
    public void Push(int owner, int generation, int epoch, CharacterId attacker, int askers)
    {
        var list = ListOf(owner, generation);
        list.RemoveAll(e => e.Epoch != epoch);
        if (list.Exists(e => e.Attacker == attacker) || list.Count + askers >= Cap) return;

        list.Add((attacker, epoch));
    }

    /// <summary>
    /// Takes <paramref name="attacker"/> off the list of <paramref name="owner"/>. Serial.
    /// </summary>
    public void Remove(int owner, CharacterId attacker)
    {
        if ((uint)owner < (uint)_lists.Length) _lists[owner]?.RemoveAll(e => e.Attacker == attacker);
    }

    /// <summary>
    /// The number of entries kept for <paramref name="owner"/>, current or not (<see cref="At"/>).
    /// </summary>
    public int Count(int owner) => (uint)owner < (uint)_lists.Length && _lists[owner] is { } list ? list.Count : 0;

    /// <summary>
    /// Entry <paramref name="k"/> of <paramref name="owner"/>'s list when it is on it now (its generation <paramref name="generation"/>, its list
    /// epoch <paramref name="epoch"/>); <see cref="CharacterId.None"/> otherwise.
    /// </summary>
    public CharacterId At(int owner, int generation, int epoch, int k)
    {
        if (_generation[owner] != generation) return CharacterId.None;

        var (attacker, stamp) = _lists[owner]![k];
        return stamp == epoch ? attacker : CharacterId.None;
    }

    /// <summary>
    /// Adds the lists that hold entries to <paramref name="h"/>.
    /// </summary>
    public void Hash(ref StateHasher h)
    {
        for (int s = 0; s < _lists.Length; s++)
        {
            if (_lists[s] is not { Count: > 0 } list) continue;

            h.Add(s);
            h.Add(_generation[s]);
            h.Add(list.Count);
            foreach (var (attacker, epoch) in list)
            {
                h.Add(attacker);
                h.Add(epoch);
            }
        }
    }

    /// <summary>
    /// The list of <paramref name="owner"/>, emptied when it last belonged to another character in that slot.
    /// </summary>
    List<(CharacterId Attacker, int Epoch)> ListOf(int owner, int generation)
    {
        if (owner >= _lists.Length)
        {
            int size = Math.Max(owner + 1, _lists.Length * 2);
            Array.Resize(ref _lists, size);
            Array.Resize(ref _generation, size);
        }

        var list = _lists[owner] ??= [];
        if (_generation[owner] != generation) list.Clear();
        _generation[owner] = generation;
        return list;
    }
}

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// Who started each character's last conversation (the original's Dialogue <c>+0x2a0</c>, docs/game/dialogue.md "Speakers", <b>Verified</b>):
/// a start (<c>FUN_140683f90</c>, the player conversation's <c>FUN_140684320</c>) writes the owner into its own and its target's state, the owner's
/// end (<c>FUN_140674830</c>) clears its own, and an event fired at a character that runs no conversation of its own is refused while its starter
/// still talks (<c>FUN_140684990</c>). Kept only for characters whose starter is another character (one that is its own starter, or has none,
/// passes the same). Hashed.
/// </summary>
sealed class ConversationStarters
{
    /// <summary>
    /// Each character's starter, kept only when it is another character.
    /// </summary>
    readonly SortedDictionary<CharacterId, CharacterId> _starter = new(Comparer<CharacterId>.Create(DialogueSystem.CompareIds));

    /// <summary>
    /// The number of characters with another's starter.
    /// </summary>
    public int Count => _starter.Count;

    /// <summary>
    /// A conversation of <paramref name="owner"/> with <paramref name="target"/> started: the owner is the starter of both.
    /// </summary>
    public void Started(CharacterId owner, CharacterId target)
    {
        _starter.Remove(owner);
        if (!target.IsNone && target != owner) _starter[target] = owner;
    }

    /// <summary>
    /// The conversation of <paramref name="owner"/> ended: its starter is cleared.
    /// </summary>
    public void Ended(CharacterId owner) => _starter.Remove(owner);

    /// <summary>
    /// The starter test for a speaker that runs no conversation of its own: refused when the event is not <paramref name="exempt"/> and its
    /// starter is not <paramref name="free"/>; otherwise the speaker becomes its own starter.
    /// </summary>
    public bool Refuses(CharacterId speaker, bool exempt, Func<CharacterId, bool> free)
    {
        if (!_starter.TryGetValue(speaker, out var starter)) return false;
        if (!exempt && !free(starter)) return true;

        _starter.Remove(speaker);
        return false;
    }

    /// <summary>
    /// Drops the entries of characters that are gone.
    /// </summary>
    public void DropGone(World world)
    {
        if (_starter.Count == 0) return;

        var gone = _starter.Keys.Where(id => !world.Characters.TryResolveNext(id, out _)).ToArray();
        foreach (var id in gone) _starter.Remove(id);
    }

    /// <summary>
    /// Adds the starters to the world hash, only while there are any.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (_starter.Count == 0) return;

        hasher.Add(0x53747274);
        foreach (var (who, starter) in _starter)
        {
            hasher.Add(who);
            hasher.Add(starter);
        }
    }
}

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// A player choice picked in an open conversation (<see cref="IDialogueChoiceObserver"/>, <see cref="IDialogueChoiceFilter"/>): the tick, the
/// player speaker, the NPC partner, the chosen reply node's string id (empty for <c>[Leave]</c> or <c>[...]</c>), the displayed choice text, the
/// zero-based choice index, and whether it was the leave option.
/// </summary>
public readonly record struct DialogueChoiceEvent(long Tick, CharacterId Player, CharacterId Npc, string LineId, string Text, int Index,
    bool Leave);

/// <summary>
/// A system told whenever the player picks a choice in a dialogue conversation (docs/modding.md "Dialogue choices"). Called serially on the
/// simulation tick when the choice is answered, after any <see cref="IDialogueChoiceFilter"/> runs. It may read the world and update its own hashed
/// state, and must not mutate characters or other world state (changes go through commands). One that throws is disabled for that world, and the
/// fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IDialogueChoiceObserver : ITickSystem
{
    /// <summary>
    /// A dialogue choice was picked.
    /// </summary>
    void ChoicePicked(World world, in DialogueChoiceEvent choice);
}

/// <summary>
/// A system asked before the dialogue system runs a player's chosen reply (docs/modding.md "Dialogue choices"): returning true claims the choice
/// (the conversation ends without running the reply's vanilla effects or advancing to the next line), false lets vanilla dialogue proceed. Called
/// serially in system order until one returns true. One that throws counts as false and is disabled for that world, and the fault enters the state
/// hash. Engine API, not in the original.
/// </summary>
public interface IDialogueChoiceFilter : ITickSystem
{
    /// <summary>
    /// Whether this mod handles <paramref name="choice"/> instead of vanilla dialogue.
    /// </summary>
    bool HandleChoice(World world, in DialogueChoiceEvent choice);
}

/// <summary>
/// The world's dialogue choice filters and observers (<see cref="IDialogueChoiceFilter"/>, <see cref="IDialogueChoiceObserver"/>).
/// </summary>
sealed class DialogueChoiceObservers
{
    /// <summary>
    /// Registered dialogue choice filters.
    /// </summary>
    readonly SerialHooks<IDialogueChoiceFilter> _filters = new(0x444C4746, "dialogue choice filter");

    /// <summary>
    /// Registered dialogue choice observers.
    /// </summary>
    readonly SerialHooks<IDialogueChoiceObserver> _observers = new(0x444C474F, "dialogue choice observer");

    /// <summary>
    /// Whether any filter or observer is active.
    /// </summary>
    internal bool Active => _filters.Active || _observers.Active;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter or observer.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        _filters.Register(system, systemIndex);
        _observers.Register(system, systemIndex);
    }

    /// <summary>
    /// Adds any disabled filters or observers to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        _filters.HashFaults(ref hash);
        _observers.HashFaults(ref hash);
    }

    /// <summary>
    /// Runs filters and observers for a picked choice; returns true when a filter claimed it so vanilla effects and advance are skipped.
    /// </summary>
    internal bool Picked(World world, CharacterId player, CharacterId npc, string lineId, string text, int index, bool leave)
    {
        if (!Active) return false;

        var choice = new DialogueChoiceEvent(world.Tick, player, npc, lineId, text, index, leave);
        bool handled = RunFilters(world, choice);
        RunObservers(world, choice);
        return handled;
    }

    /// <summary>
    /// Asks each enabled filter in system order; stops at the first that claims the choice.
    /// </summary>
    bool RunFilters(World world, in DialogueChoiceEvent choice)
    {
        for (int i = 0; i < _filters.Count; i++)
        {
            if (!_filters.Enabled(i)) continue;

            try
            {
                if (_filters[i].HandleChoice(world, choice)) return true;
            }
            catch (Exception e)
            {
                _filters.Threw(i, e);
            }
        }

        return false;
    }

    /// <summary>
    /// Notifies every enabled observer.
    /// </summary>
    void RunObservers(World world, in DialogueChoiceEvent choice)
    {
        for (int i = 0; i < _observers.Count; i++)
        {
            if (!_observers.Enabled(i)) continue;

            try
            {
                _observers[i].ChoicePicked(world, choice);
            }
            catch (Exception e)
            {
                _observers.Threw(i, e);
            }
        }
    }
}

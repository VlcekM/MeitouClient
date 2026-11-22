using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Production;

/// <summary>
/// Who a code mod's job offer is for (<see cref="ModJobOffer.Takers"/>).
/// </summary>
[Flags]
public enum ModJobTakers
{
    /// <summary>
    /// Nobody.
    /// </summary>
    None = 0,

    /// <summary>
    /// The player's people (not animals).
    /// </summary>
    People = 1,

    /// <summary>
    /// The player's animals.
    /// </summary>
    Animals = 2,
}

/// <summary>
/// A code mod's offer of one of its job types (docs/modding.md "Mod jobs"): the right-click menu of a finished building of the player's faction whose
/// record is one of <paramref name="Buildings"/> shows <paramref name="Caption"/> to a selection holding a character of <paramref name="Takers"/>,
/// and choosing it gives each such character the job <paramref name="Type"/> on that building (<see cref="AddJob"/>,
/// <see cref="PermaJobs.GiveOffered"/>).
/// </summary>
public sealed record ModJobOffer(TaskType Type, IReadOnlyList<string> Buildings, string Caption, ModJobTakers Takers)
{
    /// <summary>
    /// The longest caption, in characters.
    /// </summary>
    public const int MaxCaption = 32;

    /// <summary>
    /// Whether the offer is for an animal (<paramref name="animal"/>) or for a person.
    /// </summary>
    public bool Takes(bool animal) => (Takers & (animal ? ModJobTakers.Animals : ModJobTakers.People)) != 0;

    /// <summary>
    /// Whether a building of record <paramref name="building"/> offers it.
    /// </summary>
    public bool On(string building) => Buildings.Contains(building);

    /// <summary>
    /// Why the offer cannot be registered, or null: no building or a blank one; a caption that is blank, over <see cref="MaxCaption"/> characters
    /// or holds a control character (a tab starts the menu's chance column); takers that are none or more than people and animals.
    /// </summary>
    public string? Refusal()
    {
        if (Buildings is not { Count: > 0 } || Buildings.Any(string.IsNullOrWhiteSpace))
            return "A job offer names its building records.";
        if (string.IsNullOrWhiteSpace(Caption) || Caption.Length > MaxCaption || Caption.Any(char.IsControl))
            return $"A job offer's caption is 1 to {MaxCaption} characters, none a control character.";
        if (Takers == ModJobTakers.None || (Takers & ~(ModJobTakers.People | ModJobTakers.Animals)) != 0)
            return "A job offer is for people, animals or both.";

        return null;
    }
}

namespace Meitou.Simulation.Carrying;

/// <summary>
/// The stealth knock-out swings rolled since the host last took them (the host shows them as "*Thunk*" or "Failed!").
/// </summary>
sealed class KnockoutLog
{
    /// <summary>
    /// The swings rolled since the last <see cref="Take"/>.
    /// </summary>
    readonly List<StealthKoEntry> _knockouts = [];

    /// <summary>
    /// The swings rolled since the last <see cref="Take"/>.
    /// </summary>
    public IReadOnlyList<StealthKoEntry> Entries => _knockouts;

    /// <summary>
    /// Notes one rolled swing.
    /// </summary>
    public void Add(StealthKoEntry entry) => _knockouts.Add(entry);

    /// <summary>
    /// The swings rolled since the last take, which empties the log.
    /// </summary>
    public StealthKoEntry[] Take()
    {
        StealthKoEntry[] all = [.. _knockouts];
        _knockouts.Clear();
        return all;
    }
}

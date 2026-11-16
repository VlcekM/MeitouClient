namespace Meitou.Simulation.Production;

/// <summary>
/// A consumer of a town's power frame (docs/game/buildings-production.md "Power"): a player machine or a pseudo consumer.
/// </summary>
interface IPowerDraw
{
    /// <summary>
    /// What it wants this tick.
    /// </summary>
    float Demand { get; }

    /// <summary>
    /// What the frame gave it this tick.
    /// </summary>
    float Granted { get; set; }

    /// <summary>
    /// Whether it takes battery power when the generators leave it short (<c>gets battery</c>, +0x3b4).
    /// </summary>
    bool GetsBattery { get; }
}

/// <summary>
/// A building the simulation has no machine for that draws on a frame a player machine is on: an NPC town's level building or layout object, or a
/// town-less level building. <paramref name="Key"/> is its level key or layout object id.
/// </summary>
sealed record PseudoDraw(string Key, float Demand, bool GetsBattery) : IPowerDraw
{
    /// <summary>
    /// What the frame gave it this tick.
    /// </summary>
    public float Granted { get; set; }
}

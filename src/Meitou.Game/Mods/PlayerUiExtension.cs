using Meitou.Game.Ui;

namespace Meitou.Modding;

/// <summary>
/// Shared actions for the original in-game layouts and UI extensions.
/// </summary>
sealed class PlayerUiExtension(Action<string, string> command) : IUiExtension
{
    /// <summary>
    /// The extension id of Meitou's own shared actions.
    /// </summary>
    public string Id => "meitou";

    /// <summary>
    /// Registers the shared actions (the speed buttons, the panels and orders) that run the host's commands.
    /// </summary>
    public void Register(UiRegistration r)
    {
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            r.RegisterAction($"speed.{i}", _ => command($"speed:{index}", ""));
        }

        string[] names = new[]
        {
            "inventory", "stats", "squad", "stop", "medic", "rescue", "member", "context", "extendedstats", "floor-up", "floor-down", "build",
            "job-remove", "prospect", "building-buy", "building-dismantle", "building-pause", "building-doors", "building-locks", "building-public",
            "building-upgrade", "building-downgrade", "machine-power", "machine-battery", "tutorial", "help",
        };
        foreach (string name in names)
            r.RegisterAction(name, c => command(name, c.Argument));

        IEnumerable<string> orders = Enum.GetNames<Simulation.Player.CharacterOrders>().Where(n => n != "None").Select(n => "order." + n);
        foreach (string name in orders.Concat(["runspeed.-1", "runspeed.1"]))
            r.RegisterAction(name, c => command(name, c.Argument));
    }
}
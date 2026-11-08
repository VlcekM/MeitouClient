using Meitou.Ui;

namespace Meitou.Game;

/// <summary>Shared actions for the original in-game layouts and UI extensions.</summary>
sealed class PlayerUiExtension(Action<string, string> command) : IUiExtension
{
    public string Id => "meitou";

    public void Register(UiRegistration r)
    {
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            r.RegisterAction($"speed.{i}", _ => command($"speed:{index}", ""));
        }
        foreach (string name in new[] { "inventory", "stats", "squad", "stop", "medic", "rescue", "member", "context", "extendedstats", "floor-up", "floor-down", "build", "job-remove", "prospect", "building-buy", "building-dismantle", "building-pause", "building-doors", "building-locks", "building-public", "building-upgrade", "building-downgrade", "machine-power", "machine-battery", "tutorial" })
            r.RegisterAction(name, c => command(name, c.Argument));
        foreach (string name in Enum.GetNames<Meitou.Simulation.CharacterOrders>().Where(n => n != "None").Select(n => "order." + n).Concat(["runspeed.-1", "runspeed.1"]))
            r.RegisterAction(name, c => command(name, c.Argument));
    }
}

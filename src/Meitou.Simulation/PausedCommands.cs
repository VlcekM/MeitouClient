namespace Meitou.Simulation;

/// <summary>
/// Marks a command type that the paused interface step applies at once, as the game's own interface commands are (docs/modding.md "Commands
/// while paused", <see cref="World.ApplyPausedInterfaceCommands"/>): while the clock is paused it reaches the <see cref="IPausedCommandHandler"/>
/// systems between ticks instead of waiting for the next tick. Running, it is applied at its tick like any command.
/// </summary>
public interface IPausedCommand
{
}

/// <summary>
/// A system whose <see cref="ITickSystem.Inputs"/> also runs in the paused interface step (<see cref="World.ApplyPausedInterfaceCommands"/>), in
/// system order, with that step's due commands: the game's interface commands and every <see cref="IPausedCommand"/>. Time does not advance there
/// and no other phase runs, so it must only apply its commands (serially, as in a tick's inputs). A mod system that throws there is disabled and
/// its fault enters the state hash, as in a tick.
/// </summary>
public interface IPausedCommandHandler : ITickSystem
{
}

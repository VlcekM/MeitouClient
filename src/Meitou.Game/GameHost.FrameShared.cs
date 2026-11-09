using System.Diagnostics;
using System.Numerics;
using Meitou.Game.Host;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Ui;

using EngineButton = Meitou.Game.Host.Input.MouseButton;

namespace Meitou.Game;

/// <summary>
/// What the Vulkan loop (<see cref="Host.GameLoop"/>) and the Metal loop share: the input helpers, the frame limit, the run-only settings and the
/// world's news.
/// </summary>
sealed partial class GameHost
{
    /// <summary>
    /// Panel rows that are the session's state, not graphics settings: kept out of <c>graphics</c> in meitou.user.json (vsync and full screen have
    /// their own keys).
    /// </summary>
    static readonly HashSet<string> SessionSliders = ["Time of day", "VSync", "Full screen"];

    /// <summary>
    /// The <c>graphics</c> keys this run neither reads from nor writes to meitou.user.json: <see cref="SessionSliders"/>, and the bloom's switch and
    /// strength when <c>--bloom</c> sets them for one run.
    /// </summary>
    internal static HashSet<string> RunOnlyKeys()
    {
        var keys = new HashSet<string>(SessionSliders);
        if (Environment.GetCommandLineArgs().Contains("--bloom"))
                { keys.Add(WorldFrame.BloomStrengthSlider); keys.Add(SettingsMenu.SwitchKeyPrefix + WorldFrame.BloomSwitch); }
        return keys;
    }


    /// <summary>
    /// The player moved, turned, zoomed or switched the camera this tick (what hands a camera the cinema left back to the rig).
    /// </summary>
    internal static bool CameraInput(ActionState a) =>
        a.Held(InputAction.MoveForward) || a.Held(InputAction.MoveBack) || a.Held(InputAction.MoveLeft) || a.Held(InputAction.MoveRight) ||
        a.Held(InputAction.MoveUp) || a.Held(InputAction.MoveDown) || a.Held(InputAction.RotateLeft) || a.Held(InputAction.RotateRight) ||
        a.Held(InputAction.PitchUp) || a.Held(InputAction.PitchDown) || a.Held(InputAction.ZoomIn) || a.Held(InputAction.ZoomOut) ||
        a.Held(InputAction.Orbit) && a.MouseDelta != Vector2.Zero || a.Wheel != 0 || a.Pressed(InputAction.ToggleFreeCamera);

    /// <summary>
    /// A key or button bound to Orbit (middle button or Ctrl by default) is down.
    /// </summary>
    internal bool OrbitHeld() => Session.Bindings.Get(InputAction.Orbit).Any(Session.Input.IsHeld);

    /// <summary>
    /// The button the interface sees for a physical one: the Select button is its left, the Command button its right (each falls back to its own
    /// default while unbound, so the interface stays reachable). Any other of the three, such as the Orbit button, is its middle (which the map pans
    /// with and which stops no click reaching the world under the interface); the extra buttons stay as they are.
    /// </summary>
    internal EngineButton PlayerButton(EngineButton physical)
    {
        var select = Session.Bindings.Get(InputAction.Select);
        var command = Session.Bindings.Get(InputAction.Command);
        if (select.Count == 0 ? physical == EngineButton.Left : select.Contains(Binding.Of(physical))) return EngineButton.Left;
        if (command.Count == 0 ? physical == EngineButton.Right : command.Contains(Binding.Of(physical))) return EngineButton.Right;
        return physical is EngineButton.Left or EngineButton.Right ? EngineButton.Middle : physical;
    }

    /// <summary>
    /// While the camera orbits the pointer stays where it was, as in the original: each movement turns the camera and the pointer is put back.
    /// </summary>
    internal void HoldPointer(Silk.NET.Input.IMouse mouse, Vector2 anchor)
    {
        mouse.Position = anchor;
        Session.Input.SetMousePosition(anchor.X, anchor.Y);
    }


    /// <summary>
    /// Waits until <paramref name="period"/> has passed since <paramref name="start"/>: sleeps most of it, spins the last 1.5 ms.
    /// </summary>
    internal static void Limit(Stopwatch clock, double start, double period)
    {
        double end = start + period;
        double remaining = end - clock.Elapsed.TotalSeconds;
        if (remaining > 0.002) Thread.Sleep(TimeSpan.FromSeconds(remaining - 0.0015));
        while (clock.Elapsed.TotalSeconds < end) Thread.SpinWait(64);
    }

    /// <summary>
    /// --log-spikes notes the simulation's systems of a frame whose ticks took longer than this.
    /// </summary>
    internal const double SpikeSimMs = 20;


    /// <summary>
    /// --log-spikes: a tick run ahead over <see cref="SpikeSimMs"/>, by system and part, in the frame whose draw waited for it (its profile holds
    /// that tick alone then: <see cref="GameLoop.SimProfile"/> cleared it before the draw).
    /// </summary>
    void NoteAheadTick()
    {
        if (!SpikeLog.Enabled || Session.AheadMs <= SpikeSimMs) return;
        var world = Session.World;
        var profile = world.Profile;
        SpikeLog.Note("sim ahead tick", ms: Session.AheadMs);
        for (int k = 0; k < profile.SystemMs.Length && k < world.Systems.Count; k++)
            if (profile.SystemMs[k] >= 2) SpikeLog.Note($"sim ahead {world.Systems[k].GetType().Name}", ms: profile.SystemMs[k]);
        foreach (var (name, ms) in profile.Parts)
            if (ms >= 2) SpikeLog.Note($"sim ahead {name}", ms: ms);
    }

    /// <summary>
    /// Between ticks: the world's messages for the player go to the HUD and the log, the fights hostility started to the log.
    /// </summary>
    internal void WorldNews()
    {
        var world = Session.World;
        foreach (string message in world.TakeMessages())
        {
            Console.WriteLine($"relations {message}");
            Player.Notice(message);
            if (DevConsoleHost.ExplodeAsker is { } asker && message.StartsWith("Explode at ", StringComparison.Ordinal))
                    { asker.Print(message); DevConsoleHost.ExplodeAsker = null; }
        }
        foreach (var line in world.TakeSaid()) Player.MessageRoller.LogMessage(line.Owner, line.Text, line.Player ? MessageTone.Player
                : MessageTone.Normal);
        // Stealth knock-outs: the original's floating "*Thunk*" (a hit) or "Failed!" over the attacker, here as a notice.
        if (world.Systems.OfType<Simulation.Carrying.CarrySystem>().FirstOrDefault() is { Knockouts.Count: > 0 } carry)
            foreach (var k in carry.TakeKnockouts())
                if (world.Characters.IsAlive(k.Attacker) && world.Characters.Cold(k.Attacker.Slot) is { IsPlayer: true } attacker)
                    Player.Notice($"{attacker.Name}: {Loc.Tr(k.Success ? "*Thunk*" : "Failed!")}{(k.NoXp ? Loc.Tr(" (No XP)") : "")} ({Math.Clamp(k.Chance, 0, 100):0}%)", !k.Success);
        if (Population is not null)
            foreach (var line in Population.TakeTownLog()) Console.WriteLine($"town      {line}");
        if (Population is not null)
            foreach (var w in Population.TakeWildLog())
                Console.WriteLine($"wild      tick {w.Tick}: {w.Squad} ({w.Members}) from the {(w.Kind == Simulation.Population.WildKind.Nest ? "nest" : "sector of")} {w.Place}");
    }
}
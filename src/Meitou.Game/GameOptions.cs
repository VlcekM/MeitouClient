using System.Globalization;

namespace Meitou.Game;

/// <summary>Game-only options; everything else is the world options shared with the viewer (<see cref="WorldOptions"/>).</summary>
sealed class GameOptions
{
    public int? FpsLimit, TickRate, SimThreads;
    public ulong Seed;
    public bool? VSync;
    /// <summary>With <c>--screenshot</c>: control ticks (and their real time of simulation, at speed 1) run before the picture, with no input.</summary>
    public int Ticks;
    public bool FreeCamera, NoPopulation, NoNavmesh, NewGame, ListStarts, SelectPlayer;
    /// <summary>Multiplies the time of the body-part and blood rates (<c>MedicalContext.BodyTimeScale</c>, default 1: the documented rates).</summary>
    public float BodyTimeScale = 1;
    /// <summary>The start to play (null = the default); with <c>--select-player</c>/<c>--move-to</c> the picture shows a selected squad walking.</summary>
    public string? NewGameName;
    public (float X, float Z)? MoveTo;
    /// <summary>With <c>--new-game</c> and <c>--screenshot</c>: the squad attacks the nearest other character as soon as one is loaded (for a picture of a fight).</summary>
    public bool AttackNearest;
    /// <summary>Interactive run that closes itself after this many seconds and prints the frame rate (an unattended smoke test).</summary>
    public double? QuitAfter;

    public const string Usage = """
        meitou [where] [options]     boots into the world with the Kenshi camera (default --town "The Hub")
          --fps-limit <n>            frame limit when vsync is off (default 240 from meitou.user.json; 0 = unlimited)
          --vsync / --no-vsync       vsync (default off)
          --tick-rate <hz>           control ticks per second: input actions and camera, in real time (default 30; the world ticks 30 per game second)
          --free-camera              start in the free camera (; toggles)
          --sim-threads <n>          worker threads of the simulation (default: half the cores, 1 to 8; the result never depends on it)
          --seed <n>                 the world seed (default 0)
          --no-population            no town residents or movement (an empty world)
          --no-navmesh               paths on open ground (no buildings), as in the tests; default: the navmesh of the active zones (built on first use, cached)
          --body-time-scale <x>      multiplies the time of body-part and blood rates (default 1, the documented rates in game hours; see docs/simulation.md "Bodies")
          --new-game [start]         a new game as the NEW_GAME_STARTOFF start (default Wanderer): the player squad at its town, camera on it
          --list-starts              print the available starts and exit
          --attack-nearest           with --new-game and --screenshot: the squad attacks the nearest other character once loaded
          --select-player, --move-to <x> <z>   with --new-game: select the squad / order it to walk (for screenshots)
          --ticks <n>                with --screenshot: run n control ticks (and the same real time of the simulation, at speed 1) before the picture
          --quit-after <s>           close after s seconds and print the frame rate (smoke test)
          --yaw/--pitch/--distance   start view: heading, pitch above the horizon and boom (Kenshi: 30 degrees, boom 150; clamped to 10..2000)
          world options as meitou-viewer --world: --at, --zone, --town, --radius, --time, --screenshot, --size, --no-foliage, ...
        Keys: W/A/S/D move, Q/E or Left/Right rotate, Up/Down pitch, wheel or PageUp/PageDown zoom, middle drag orbit, left click or drag selects, right click moves (shift queues), 1..9 / ` select, R stops,
          ; free camera (R/F up/down), Space pause, F2/F3/F4 speed 1x/2x/5x (. / , step), Tab settings, F8 or PrintScreen screenshot,
          F10 keys, F11 statistics, F12 profiler (gpu, cpu, off), Esc quit. Shift+F1.. the Faithful / Meitou switches (the viewer's F1..).
        Settings (frame limit, vsync, tick rate, the Tab sliders, key bindings) are kept in meitou.user.json.
        """;

    /// <summary>Takes the game's own options out of <paramref name="args"/> and returns the rest.</summary>
    public static (GameOptions, string[]) Split(string[] args)
    {
        var g = new GameOptions();
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--fps-limit": g.FpsLimit = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--tick-rate": g.TickRate = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--sim-threads": g.SimThreads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": g.Seed = ulong.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--vsync": g.VSync = true; break;
                case "--no-vsync": g.VSync = false; break;
                case "--ticks": g.Ticks = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--free-camera": g.FreeCamera = true; break;
                case "--no-population": g.NoPopulation = true; break;
                case "--no-navmesh": g.NoNavmesh = true; break;
                case "--body-time-scale": g.BodyTimeScale = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--new-game":
                    g.NewGame = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) g.NewGameName = args[++i];
                    break;
                case "--list-starts": g.ListStarts = true; break;
                case "--select-player": g.SelectPlayer = true; break;
                case "--attack-nearest": g.AttackNearest = true; g.SelectPlayer = true; break;
                case "--move-to": g.MoveTo = (float.Parse(Next(), CultureInfo.InvariantCulture), float.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--quit-after": g.QuitAfter = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                default: rest.Add(a); break;
            }
        }
        return (g, rest.ToArray());
    }
}

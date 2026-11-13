using Meitou.Game.Host.WorldSync;
using Meitou.Game.DevTools;
using System.Numerics;
using System.Text.Json;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Impostors;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Ui;
using Meitou.Game.Rendering.Upscalers;
using Meitou.Game.Rendering.Water;

namespace Meitou.Game;

/// <summary>
/// The agent control channel's interior view layer (docs/agent-playtest.md): <c>interior-view</c> forces the interior view of the building at the
/// camera target at a floor (no character needed, so a capture cannot lose the view), and <c>interior-debug</c> switches <see cref="InteriorDebug"/>.
/// The view is listed by <c>state</c>.
/// </summary>
sealed partial class GameHost
{
    /// <summary>
    /// The floor <c>interior-view</c> holds the building at the camera target at, or null.
    /// </summary>
    int? _forcedViewFloor;

    /// <summary>
    /// Called by <see cref="UpdateInteriorView"/>: puts the building over the camera target in the view at the forced floor.
    /// </summary>
    internal void ApplyForcedInteriorView(Meitou.Game.Rendering.Objects.WorldObjectRenderer objects)
    {
        if (_forcedViewFloor is not { } floor) return;

        Interiors.ViewedFloor = floor;
        for (float dy = 0; dy < 600; dy += 20)
        {
            if (objects.InteriorAt(Camera.Target + new Vector3(0, dy, 0), InteriorView.InsideMargin) is { } room)
            {
                Interiors.AddViewed(room);
                return;
            }
        }
    }

    /// <summary>
    /// Writes the <c>interior</c> object of <c>state</c>: the viewed floor, whether <c>interior-view</c> forces it, and each viewed building's floor.
    /// </summary>
    internal void WriteInteriorState(Utf8JsonWriter w)
    {
        w.WriteStartObject("interior");
        w.WriteNumber("floor", Interiors.ViewedFloorValue);
        w.WriteBoolean("forced", _forcedViewFloor is not null);
        w.WriteStartArray("views");
        foreach (var (id, floor) in Interiors.InteriorFloors)
        {
            w.WriteStartObject();
            w.WriteString("building", id);
            w.WriteNumber("floor", floor);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>
    /// Registers the interior view, lamp and render debug commands of the control channel.
    /// </summary>
    internal void RegisterControlInteriorCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add)
    {
        add("interior-view", "interior-view <floor 0..3 | off>", "hold the interior view of the building over the camera target at a floor, with no "
            + "character inside (off: back to the characters' own view); state lists the view", ControlInteriorView);
        add("interior-hide", "interior-hide <mesh text | off>", "debug: building parts whose mesh path contains the text count as hidden by the floor "
            + "view (colour and shadow passes), to find which part draws a surface", ControlInteriorHide);
        add("vram", "vram", "debug: device-local memory in use and the budget (MB), as the memory guard samples it", ControlVram);
        add("object-sort", "object-sort <on|off>", "debug: colour batches of the objects are drawn nearest first (on, the default with the reach "
            + "switch) or in the order met (off)", ControlObjectSort);
        add("gpu-times", "gpu-times [frames]", "debug: the GPU's mean ms per frame stage over the newest frames (default 60, at most 300), largest "
            + "first", ControlGpuTimes);
        add("lamps", "lamps list [radius] | lamps off <index...> | lamps on", "debug: the lamps reaching the camera's target (index, position "
            + "relative to it, colour, power, radius, type, cone, falloff, rule, owner); off leaves those indices out, on brings them all back",
            ControlLamps);
        add("interior-debug", "interior-debug <cover|shadow> <on|off>", "debug switches of the interior lighting: cover = the room-grid focus ignores "
            + "surfaces above the viewed floor; shadow = shadow passes leave out what the view hides", ControlInteriorDebug);
    }

    /// <summary>
    /// The <c>interior-view</c> command (usage and help at its registration in <see cref="RegisterControlInteriorCommands"/>).
    /// </summary>
    void ControlInteriorView(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count != 1)
        {
            con.Error("usage: interior-view <floor 0..3 | off>");
            return;
        }

        if (args[0] == "off")
        {
            _forcedViewFloor = null;
            con.Print("interior view released");
            return;
        }

        if (!int.TryParse(args[0], out int f) || f < 0 || f > 3)
        {
            con.Error("usage: interior-view <floor 0..3 | off>");
            return;
        }

        _forcedViewFloor = f;
        con.Print($"interior view held at floor {f}");
    }

    /// <summary>
    /// The <c>interior-hide</c> command (usage and help at its registration in <see cref="RegisterControlInteriorCommands"/>).
    /// </summary>
    static void ControlInteriorHide(IReadOnlyList<string> args, DevConsole con)
    {
        InteriorDebug.HideMesh = args.Count == 1 && args[0] != "off" ? args[0] : null;
        con.Print(InteriorDebug.HideMesh is null ? "off" : "hiding " + InteriorDebug.HideMesh);
    }

    /// <summary>
    /// The <c>vram</c> command (usage and help at its registration in <see cref="RegisterControlInteriorCommands"/>).
    /// </summary>
    void ControlVram(IReadOnlyList<string> args, DevConsole con)
    {
        if (Gpu.Guard is not { } g)
        {
            con.Error("no memory guard");
            return;
        }

        con.Print(FormattableString.Invariant($"vram {g.Fraction * g.BudgetBytes / 1048576:F0} / {g.BudgetBytes / 1048576} MB ({g.Fraction:P1})"));
    }

    /// <summary>
    /// The <c>object-sort</c> command (usage and help at its registration in <see cref="RegisterControlInteriorCommands"/>).
    /// </summary>
    static void ControlObjectSort(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count != 1 || args[0] is not ("on" or "off"))
        {
            con.Error("usage: object-sort <on|off>");
            return;
        }

        InteriorDebug.NoObjectSort = args[0] == "off";
        con.Print("object sort " + args[0]);
    }

    /// <summary>
    /// The <c>gpu-times</c> command (usage and help at its registration in <see cref="RegisterControlInteriorCommands"/>).
    /// </summary>
    void ControlGpuTimes(IReadOnlyList<string> args, DevConsole con)
    {
        if (DebugMenu.Profiler is null)
        {
            con.Error("no DebugMenu.Profiler in this run");
            return;
        }

        con.Print(DebugMenu.Profiler.GpuMeans(args.Count > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 60));
    }

    /// <summary>
    /// The <c>lamps</c> command (usage and help at its registration in <see cref="RegisterControlInteriorCommands"/>); also <c>carry</c>,
    /// <c>refview</c> and <c>tiles</c>.
    /// </summary>
    void ControlLamps(IReadOnlyList<string> args, DevConsole con)
    {
        if (ActiveLamps is not { } lamps || args.Count == 0)
        {
            con.Error("usage: lamps list [radius] | lamps off <index...> | lamps on");
            return;
        }

        if (args[0] == "on")
        {
            lamps.DebugOff.Clear();
            con.Print("all lamps on");
            return;
        }

        if (args[0] == "carry")
        {
            LampsCarry(lamps, args, con);
            return;
        }

        if (args[0] == "refview")
        {
            lamps.DebugReference = args.Count < 3 ? null : (int.Parse(args[1]), RefViewMode(args[2]));
            con.Print(lamps.DebugReference is null ? "refview off" : $"refview lamp {args[1]} {args[2]}");
            return;
        }

        if (args[0] == "tiles")
        {
            if (args.Count > 1) lamps.TilesEnabled = args[1] == "on";
            con.Print($"lamp tiles {(lamps.TilesEnabled ? "on" : "off")}: {lamps.TilesDescription}");
            return;
        }

        if (args[0] == "off")
        {
            foreach (var a in args.Skip(1)) lamps.DebugOff.Add(int.Parse(a));
            con.Print($"{lamps.DebugOff.Count} lamps off");
            return;
        }

        LampsList(lamps, args, con);
    }

    /// <summary>
    /// <c>lamps carry x z lift [reach]</c>: a debug point lamp at that place, lift over the terrain (reach default 300); fewer words take it away.
    /// </summary>
    void LampsCarry(WorldLamps lamps, IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count < 4)
        {
            lamps.DebugCarried = null;
            con.Print("carried lamp off");
            return;
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        float cx = float.Parse(args[1], inv);
        float cz = float.Parse(args[2], inv);
        float lift = float.Parse(args[3], inv);
        float reach = args.Count > 4 ? float.Parse(args[4], inv) : 300;
        var place = new System.Numerics.Vector3(cx, Gpu.Terrain.HeightAt(cx, cz) + lift, cz);
        lamps.DebugCarried = new Meitou.Data.World.WorldLight(place, new System.Numerics.Vector3(1, 0.9f, 0.6f), 0.8f, reach,
            Meitou.Data.World.WorldLightType.Point, -System.Numerics.Vector3.UnitY, 0, 0, 1, default, default, false, 0, "debug", "debug",
            "debug-carried");
        con.Print($"carried lamp at {cx} {cz}");
    }

    /// <summary>
    /// The reference view a <c>lamps refview</c> mode names: objects 3, ref60 2, ref 1, anything else 0.
    /// </summary>
    static int RefViewMode(string mode) =>
        mode == "objects"
            ? 3
            : mode == "ref60"
                ? 2
                : mode == "ref"
                    ? 1
                    : 0;

    /// <summary>
    /// <c>lamps list [radius]</c>: prints each lamp reaching the camera's target with its place relative to it and its settings.
    /// </summary>
    void LampsList(WorldLamps lamps, IReadOnlyList<string> args, DevConsole con)
    {
        float radius = args.Count > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var at = Camera.Target;
        var listed = new System.Text.StringBuilder();
        foreach (var (i, l) in lamps.Around(at, radius))
        {
            var d = l.Position - at;
            listed.Append($" ## #{i} at {d.X:0} {d.Y:0} {d.Z:0} colour {l.Colour.X:0.00} {l.Colour.Y:0.00} {l.Colour.Z:0.00} power {l.Intensity:0.00} "
                + $"radius {l.Radius:0} {l.Type} dir {l.Direction.X:0.00} {l.Direction.Y:0.00} {l.Direction.Z:0.00} "
                + $"cone {l.InnerAngle * 57.3f:0}/{l.OuterAngle * 57.3f:0} falloff {l.Falloff:0.00} {l.Rule} interior={l.Interior} {l.LightId} "
                + $"of {l.BuildingId}");
        }

        con.Print(listed.ToString());
    }

    /// <summary>
    /// The <c>interior-debug</c> command (usage and help at its registration in <see cref="RegisterControlInteriorCommands"/>).
    /// </summary>
    static void ControlInteriorDebug(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count != 2 || args[1] is not ("on" or "off"))
        {
            con.Error("usage: interior-debug <cover|shadow> <on|off>");
            return;
        }

        bool on = args[1] == "on";
        switch (args[0])
        {
            case "cover":
                InteriorDebug.CoverIgnoresHiddenFloors = on;
                break;
            case "shadow":
                InteriorDebug.ShadowsLeaveOutHidden = on;
                break;
            default:
                con.Error("usage: interior-debug <cover|shadow> <on|off>");
                return;
        }

        con.Print($"{args[0]} {args[1]}");
    }
}

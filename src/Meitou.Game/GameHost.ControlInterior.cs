using System.Numerics;
using System.Text.Json;
using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// The agent control channel's interior view layer (docs/agent-playtest.md): <c>interior-view</c> forces the interior view of the building at the camera target at a
/// floor (no character needed, so a capture cannot lose the view), and <c>interior-debug</c> switches <see cref="InteriorDebug"/>. The view is listed by <c>state</c>.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The floor <c>interior-view</c> holds the building at the camera target at, or null.</summary>
    int? forcedViewFloor;

    /// <summary>Called by <see cref="UpdateInteriorView"/>: puts the building over the camera target in the view at the forced floor.</summary>
    void ApplyForcedInteriorView(Meitou.Rendering.WorldObjectRenderer objects)
    {
        if (forcedViewFloor is not { } floor) return;
        ViewedFloor = floor;
        for (float dy = 0; dy < 600; dy += 20)
            if (objects.InteriorAt(camera.Target + new Vector3(0, dy, 0), InsideMargin) is { } room)
            {
                if (interiorView.TryAdd(room.PlacementId, viewedFloor)) viewed.Add(room);
                return;
            }
    }

    void WriteInteriorState(Utf8JsonWriter w)
    {
        w.WriteStartObject("interior");
        w.WriteNumber("floor", viewedFloor);
        w.WriteBoolean("forced", forcedViewFloor is not null);
        w.WriteStartArray("views");
        foreach (var (id, floor) in interiorView) { w.WriteStartObject(); w.WriteString("building", id); w.WriteNumber("floor", floor); w.WriteEndObject(); }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    void RegisterControlInteriorCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add)
    {
        add("interior-view", "interior-view <floor 0..3 | off>", "hold the interior view of the building over the camera target at a floor, with no character inside (off: back to the characters' own view); state lists the view", (args, con) =>
        {
            if (args.Count != 1) { con.Error("usage: interior-view <floor 0..3 | off>"); return; }
            if (args[0] == "off") { forcedViewFloor = null; con.Print("interior view released"); return; }
            if (!int.TryParse(args[0], out int f) || f < 0 || f > 3) { con.Error("usage: interior-view <floor 0..3 | off>"); return; }
            forcedViewFloor = f;
            con.Print($"interior view held at floor {f}");
        });
        add("interior-hide", "interior-hide <mesh text | off>", "debug: building parts whose mesh path contains the text count as hidden by the floor view (colour and shadow passes), to find which part draws a surface", (args, con) =>
        {
            InteriorDebug.HideMesh = args.Count == 1 && args[0] != "off" ? args[0] : null;
            con.Print(InteriorDebug.HideMesh is null ? "off" : "hiding " + InteriorDebug.HideMesh);
        });
        add("vram", "vram", "debug: device-local memory in use and the budget (MB), as the memory guard samples it", (args, con) =>
        {
            if (gpu.Guard is not { } g) { con.Error("no memory guard"); return; }
            con.Print(FormattableString.Invariant($"vram {g.Fraction * g.BudgetBytes / 1048576:F0} / {g.BudgetBytes / 1048576} MB ({g.Fraction:P1})"));
        });
        add("object-sort", "object-sort <on|off>", "debug: colour batches of the objects are drawn nearest first (on, the default with the reach switch) or in the order met (off)", (args, con) =>
        {
            if (args.Count != 1 || args[0] is not ("on" or "off")) { con.Error("usage: object-sort <on|off>"); return; }
            InteriorDebug.NoObjectSort = args[0] == "off";
            con.Print("object sort " + args[0]);
        });
        add("gpu-times", "gpu-times [frames]", "debug: the GPU's mean ms per frame stage over the newest frames (default 60, at most 300), largest first", (args, con) =>
        {
            if (profiler is null) { con.Error("no profiler in this run"); return; }
            con.Print(profiler.GpuMeans(args.Count > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 60));
        });
        add("lamps", "lamps list [radius] | lamps off <index...> | lamps on", "debug: the lamps reaching the camera's target (index, position relative to it, colour, power, radius, type, cone, falloff, rule, owner); off leaves those indices out, on brings them all back", (args, con) =>
        {
            if (ActiveLamps is not { } lamps || args.Count == 0) { con.Error("usage: lamps list [radius] | lamps off <index...> | lamps on"); return; }
            if (args[0] == "on") { lamps.DebugOff.Clear(); con.Print("all lamps on"); return; }
            if (args[0] == "refview")
            {
                lamps.DebugReference = args.Count < 3 ? null : (int.Parse(args[1]), args[2] == "objects" ? 3 : args[2] == "ref60" ? 2 : args[2] == "ref" ? 1 : 0);
                con.Print(lamps.DebugReference is null ? "refview off" : $"refview lamp {args[1]} {args[2]}");
                return;
            }
            if (args[0] == "tiles")
            {
                if (args.Count > 1) lamps.TilesEnabled = args[1] == "on";
                con.Print($"lamp tiles {(lamps.TilesEnabled ? "on" : "off")}: {lamps.TilesDescription}");
                return;
            }
            if (args[0] == "off") { foreach (var a in args.Skip(1)) lamps.DebugOff.Add(int.Parse(a)); con.Print($"{lamps.DebugOff.Count} lamps off"); return; }
            float radius = args.Count > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            var at = camera.Target;
            var listed = new System.Text.StringBuilder();
            foreach (var (i, l) in lamps.Around(at, radius))
            {
                var d = l.Position - at;
                listed.Append($" ## #{i} at {d.X:0} {d.Y:0} {d.Z:0} colour {l.Colour.X:0.00} {l.Colour.Y:0.00} {l.Colour.Z:0.00} power {l.Intensity:0.00} radius {l.Radius:0} {l.Type} dir {l.Direction.X:0.00} {l.Direction.Y:0.00} {l.Direction.Z:0.00} cone {l.InnerAngle * 57.3f:0}/{l.OuterAngle * 57.3f:0} falloff {l.Falloff:0.00} {l.Rule} interior={l.Interior} {l.LightId} of {l.BuildingId}");
            }
            con.Print(listed.ToString());
        });
        add("interior-debug", "interior-debug <cover|shadow> <on|off>", "debug switches of the interior lighting: cover = the room-grid focus ignores surfaces above the viewed floor; shadow = shadow passes leave out what the view hides", (args, con) =>
        {
            if (args.Count != 2 || args[1] is not ("on" or "off")) { con.Error("usage: interior-debug <cover|shadow> <on|off>"); return; }
            bool on = args[1] == "on";
            switch (args[0])
            {
                case "cover": InteriorDebug.CoverIgnoresHiddenFloors = on; break;
                case "shadow": InteriorDebug.ShadowsLeaveOutHidden = on; break;
                default: con.Error("usage: interior-debug <cover|shadow> <on|off>"); return;
            }
            con.Print($"{args[0]} {args[1]}");
        });
    }
}

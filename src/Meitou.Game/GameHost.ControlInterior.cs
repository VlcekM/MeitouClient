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

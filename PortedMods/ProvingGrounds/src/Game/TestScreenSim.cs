using System.Globalization;
using Meitou.Simulation;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

/// <summary>The window model of the TEST-ONLY screen <c>pg.test</c> (task 4.4): rows, a status line, the click count and the portrait character.</summary>
sealed record TestScreenModel(int Rows, string Status, string Clicks, CharacterId Portrait) : PgUiModel;

/// <summary>
/// The sim half of the TEST-ONLY screen <c>pg.test</c> (task 4.4; the window half is src/Ui/TestScreen.cs). It exercises the window framework end
/// to end as the real windows will use it (4.5 runs it live once before building the Arena window): <c>pg window test open|close</c> asks the window
/// side to open / close it (a request in the published model), <c>rows &lt;n&gt;</c> changes its shape (the window side Replaces the layout),
/// <c>portrait &lt;id&gt;|off</c> shows a character's portrait, and a click on its button (the left release over it, task 4.4b) comes back as a <see cref="UiClick"/> (with Ctrl: a
/// "ctrl-click", a flag; a right-click is "right-click", task 4.5b) logged as a PG-UI JSON line. Sim thread, serial Inputs only; every verb answers in
/// log.txt through the system's <c>pg window at tick</c> line.
/// </summary>
sealed class TestScreenSim
{
    internal const string ScreenId = "pg.test";
    internal const string ButtonWidget = "PgTestButton";
    internal const int MaxRows = 12;

    int rows = 3, clicks, serial;
    bool open;
    string last = "none";
    CharacterId portrait = CharacterId.None;
    TestScreenModel model;

    internal TestScreenSim() => model = Build();

    /// <summary>The published model (a new object after each change).</summary>
    internal TestScreenModel Model => model;

    TestScreenModel Build() => new(rows, string.Create(CultureInfo.InvariantCulture, $"Test window: {rows} rows, portrait {(portrait.IsNone ? "none" : CharacterIdText.Format(portrait))}"),
        string.Create(CultureInfo.InvariantCulture, $"Clicks: {clicks} (last: {last})"), portrait) { RequestSerial = serial, RequestOpen = open };

    static string Json(string verb, string text) => "{\"verb\":\"window\",\"screen\":\"" + ScreenId + "\",\"step\":\"" + verb + "\",\"text\":"
        + System.Text.Json.JsonSerializer.Serialize(text) + "}";

    /// <summary>TEST ONLY <c>pg window test &lt;open|close|rows N|portrait &lt;id&gt;|portrait off|status&gt;</c> (the console verb queues the words after
    /// <c>test</c>).</summary>
    internal string Command(World world, IReadOnlyList<string> args)
    {
        string usage = "usage pg window test <open|close|rows <1-12>|portrait <c:slot.gen>|portrait off|status>";
        if (args.Count < 2 || args[0] != "test") return usage;
        switch (args[1])
        {
            case "open" or "close":
                open = args[1] == "open";
                serial++;
                break;
            case "rows" when args.Count > 2 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= MaxRows:
                rows = n;
                break;
            case "portrait" when args.Count > 2 && args[2] == "off":
                portrait = CharacterId.None;
                break;
            case "portrait" when args.Count > 2 && CharacterIdText.TryParse(args[2], out var id):
                if (!PgEngine.Exists(world, id)) return Json("portrait", "no such character " + args[2]);
                portrait = id;
                break;
            case "status":
                return Json("status", model.Status + "; " + model.Clicks);
            default:
                return usage;
        }

        model = Build();
        return Json(args[1], model.Status);
    }

    /// <summary>A widget event of the screen (sim thread): a click (task 4.4b: the left release over the pressed button) or a right press on its button counts (Ctrl: "ctrl-click"); a window-side close sets the model
    /// closed; each logs one PG-UI JSON line.</summary>
    internal string Click(UiClickData c)
    {
        if (c.Kind == UiEventKind.Closed)
        {
            // closed from the window side (Escape, its close button; ScreenFrame reports it): the model stops saying open, the request serial
            // stays (nothing to act on), so the next "open" works with one request
            if (!open || c.Value != serial) return "closed (already, or for an older request)";   // Value = the request serial it closed under
            open = false;
            model = Build();
            ProvingGroundsMod.Ctx?.Log("PG-UI {\"screen\":\"" + ScreenId + "\",\"kind\":\"Closed\"}");
            return "closed from the window";
        }
        if (c.Widget != ButtonWidget || c.Kind is not (UiEventKind.Click or UiEventKind.RightPress)) return $"ignored {c.Kind} on {c.Widget}";
        clicks++;
        last = c.Kind == UiEventKind.RightPress ? "right-click" : c.Ctrl ? "ctrl-click" : "click";
        model = Build();
        string line = "{\"screen\":\"" + ScreenId + "\",\"widget\":\"" + c.Widget + "\",\"kind\":\"" + c.Kind + "\",\"ctrl\":" + (c.Ctrl ? "true" : "false")
            + ",\"clicks\":" + clicks.ToString(CultureInfo.InvariantCulture) + "}";
        ProvingGroundsMod.Ctx?.Log("PG-UI " + line);
        return line;
    }
}

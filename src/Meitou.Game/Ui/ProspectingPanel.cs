using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Production;

namespace Meitou.Game.Ui;

/// <summary>
/// The Prospect button and the Prospecting Results window (docs/game/ui-screens.md "Prospecting"): the button gives the selected character nearest the camera the prospecting task, the window
/// shows what <see cref="World.Prospected"/> found, with its map.
/// </summary>
sealed class ProspectingPanel(PlayerInterface player)
{
    object? prospectWorld;
    long prospectSerial;
    ProspectReport? prospectReport;
    int prospectRow;

    /// <summary>
    /// The HUD's Prospect action (<c>FUN_140721800</c>): with the window open it closes it; else the selected character nearest the camera pivot starts prospecting, unless it already does,
    /// and the click sounds. The sound is not part of an order's Right_Click: the exe posts it here.
    /// </summary>
    internal void ProspectAction()
    {
        if (player.faithfulScreens.IsOpen("prospect")) { player.faithfulScreens.Close("prospect"); return; }
        var pivot = player.session.Camera.Current.Target;
        CharacterSnapshot? nearest = null;
        float best = float.MaxValue;
        foreach (var c in player.Selected())
        {
            float d = Vector3.DistanceSquared(c.Position, pivot);
            if (d < best) (nearest, best) = (c, d);
        }
        if (nearest is null || nearest.Task == CharacterTask.Prospect) return;
        player.Say(new ProspectOrder(nearest.Id) { Tick = player.session.World.Tick });
        player.interfaceSounds.Sound("Prospect_Click");
    }

    /// <summary>Once a frame: a new finished prospecting opens the results window (the exe shows it the moment the task hands it the spot) and sounds <c>Prospect_Results</c>. A new world only takes the baseline.</summary>
    internal void PollProspect()
    {
        var world = player.session.World;
        if (!ReferenceEquals(prospectWorld, world))
        {
            prospectWorld = world;
            prospectSerial = world.Prospected?.Serial ?? 0;
            prospectReport = null;
            prospectMapDrawn = -1;
            player.faithfulScreens.Close("prospect");
        }
        if (world.Prospected is not { } report || report.Serial == prospectSerial) return;
        prospectSerial = report.Serial;
        prospectReport = report;
        prospectRow = 0;
        player.faithfulScreens.Open("prospect");
        player.interfaceSounds.Sound("Prospect_Results");
    }

    internal void BindProspectScreen(UiBindings b)
    {
        b.Text["Root"] = "PROSPECTING RESULTS";
        b.Hidden.Add("CameraMarker");
    }

    /// <summary>
    /// The two data panels and the resource rows (<c>FUN_14049d850</c>): panel 1 the zone, altitude, the prospector's skill and the area size; panel 2 the wind and the environment; each resource a
    /// radio row with its value in the original's colours (above 0.75 good, below 0.5 bad).
    /// </summary>
    internal void AddProspectContent(List<UiControl> controls)
    {
        if (prospectReport is not { } r) return;
        float row = 20 * player.Height / 1080f, pixels = player.StandardPixels;
        string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        // The ink of the window's text comes from its templates (the overview lines are Kenshi_TextboxStandardText, AddLines' default, the resource values
        // Kenshi_GenericTextBoxFlatSmall), which a UI mod recolours with the window's skin; the fixed palette ink is only the fallback.
        string labelInk = MyGuiColours.TemplateInk(player.uiResources, "Kenshi_TextboxStandardText");
        string valueInk = MyGuiColours.TemplateInk(player.uiResources, "Kenshi_GenericTextBoxFlatSmall");
        player.AddLines(controls, "OverviewDatapanel1",
        [
            new("Zone:", r.Zone, PlayerInterface.TitleColour, PlayerInterface.TitleColour),
            new("Altitude:", Loc.Tr("{1,num} m", r.Altitude)),
            new("Prospector Skill:", N(r.Skill)),
            new("Area Size:", Loc.Tr("{1,num} m", r.AreaMetres)),
        ], row, pixels);
        // The wind of the spot's region when it finished (140785f50) in mph; the environment lists its coverages, "Dead" when there are none, the first on the label's line.
        double wind = Math.Round(r.WindSpeed * 0.22369362f, 1);
        var panel = new List<PlayerInterface.HudLine> { new("Wind Speed:", Loc.Tr("{1,num} mph", wind)) };
        var environment = r.Environment.Count == 0 ? [Loc.Tr("Dead")] : r.Environment.Select(l => $"{Loc.Tr(l.Name)} {N(l.Percent)}%").ToArray();
        for (int i = 0; i < environment.Length; i++) panel.Add(new(i == 0 ? "Environment:" : "", environment[i], labelInk, PlayerInterface.MainColour));
        player.AddLines(controls, "OverviewDatapanel2", panel, row, pixels);

        AddProspectMap(controls, r);
        if (controls.FirstOrDefault(c => c.Name == "ResourcesList") is not { } list) return;
        float height = player.Height * 0.0277778f;
        for (int i = 0; i < r.Resources.Count; i++)
        {
            var line = r.Resources[i];
            var area = new UiRect(list.Bounds.X, list.Bounds.Y + i * height, list.Bounds.Width, height);
            bool checkedRow = i == prospectRow;
            controls.Add(new($"prospect-row-{i}", "Button", new(area.X, area.Y, area.Width * 0.662835f, area.Height), line.Name, "prospect-row:" + N(i), "Kenshi_TickButton1Skin", true,
                new Dictionary<string, string> { ["Checked"] = checkedRow ? "true" : "false", ["StateSelected"] = checkedRow ? "true" : "false", ["TextAlign"] = "Center" }, Vector4.Zero, Pickable: true, Clip: list.Bounds));
            string colour = line.Value > 0.75f ? PlayerInterface.GoodColour : line.Value < 0.5f ? PlayerInterface.BadColour : valueInk;
            controls.Add(new($"prospect-value-{i}", "TextBox", new(area.X + area.Width * 0.724138f, area.Y, area.Width * 0.275862f, area.Height), N(line.Percent), "", "Kenshi_GenericTextBoxFlatSmall", true,
                new Dictionary<string, string> { ["TextAlign"] = "Center", ["TextColour"] = colour }, Vector4.Zero, Pickable: false, Clip: list.Bounds));
        }
    }

    /// <summary>
    /// The <c>Map</c> box shows the checked resource over the ground round the spot, and the layout's camera marker (hidden for this copy) sits every frame on the camera's pivot (the original's
    /// <c>CameraClass</c> centre node), turned by the level direction from the eye to the pivot (<c>FUN_14048c970</c>), a tenth of the box (the layout's size), cut to the box.
    /// </summary>
    void AddProspectMap(List<UiControl> controls, ProspectReport r)
    {
        int index = controls.FindIndex(c => c.Name == "Map");
        if (index < 0 || r.Map is not { } map || player.uiPainter is not { } painter) return;
        long drawn = r.Serial * 8 + prospectRow;
        if (drawn != prospectMapDrawn || !painter.HasTexture(ProspectMapTexture))
        {
            painter.SetImage(ProspectMapTexture, DrawProspectMap(map, prospectRow), srgb: false);
            prospectMapDrawn = drawn;
        }
        var box = controls[index];
        controls[index] = box with { Properties = new Dictionary<string, string>(box.Properties) { ["ImageTexture"] = ProspectMapTexture } };

        var camera = player.session.Camera.Current;
        var look = new Vector2(camera.Target.X - camera.Eye.X, camera.Target.Z - camera.Eye.Z);
        float angle = look.LengthSquared() > 1e-8f ? MathF.Acos(Math.Clamp(-Vector2.Normalize(look).Y, -1, 1)) * (look.X < 0 ? -1 : 1) : 0;
        var b = box.Bounds;
        float w = b.Width * 0.106667f, h = b.Height * 0.106667f;
        float x = b.X + (camera.Target.X - map.X) / map.Extent * b.Width, y = b.Y + (camera.Target.Z - map.Z) / map.Extent * b.Height;
        var props = new Dictionary<string, string> { ["ImageResource"] = "Kenshi_MapMarkers", ["ImageGroup"] = "Camera", ["ImageName"] = "Camera" };
        if (angle != 0) props["ImageAngle"] = angle.ToString(CultureInfo.InvariantCulture);
        controls.Insert(index + 1, new("prospect-camera", "ImageBox", new(x - w / 2, y - h / 2, w, h), "", "", "", true, props, Vector4.Zero, Pickable: false, Clip: box.Clip is { } clip ? b.Intersect(clip) : b));
    }

    const string ProspectMapTexture = "meitou:prospect:map";
    long prospectMapDrawn = -1;

    /// <summary>
    /// The map's picture (<b>Verified</b>, <c>FUN_14049cd60</c>): the ground grey, black up to 500 and brightening to white at 10000; a field resource above 0.01 at a cell adds v² of white to green and
    /// (1 - v) v to red; a rock paints its cell (2 x 2 cells on a map over 40 a side) red (1 - v) v, green v², blue the ground's grey.
    /// </summary>
    static Meitou.Data.Textures.RgbaImage DrawProspectMap(ProspectMap map, int row)
    {
        int n = map.Size;
        var image = new Meitou.Data.Textures.RgbaImage(n, n);
        var px = image.Pixels;
        var shade = new byte[n * n];
        for (int i = 0; i < shade.Length; i++)
        {
            float h = map.Heights[i];
            shade[i] = h > 500 ? (byte)Math.Clamp((h - 500) / (10000 - 500) * 255, 0, 255) : (byte)0;
        }
        void Put(int i, byte red, byte green, byte blue) { px[i * 4] = red; px[i * 4 + 1] = green; px[i * 4 + 2] = blue; px[i * 4 + 3] = 255; }
        for (int i = 0; i < shade.Length; i++) Put(i, shade[i], shade[i], shade[i]);
        if (row >= 0 && row < map.Fields.Count && map.Fields[row] is { } field)
        {
            for (int i = 0; i < field.Length; i++)
            {
                float v = field[i];
                if (map.Heights[i] < 0 || v <= 0.01f) continue;
                v = MathF.Min(v, 1);
                float a = (1 - v) * v, g = shade[i];
                Put(i, (byte)((1 - a) * g + a * 255), (byte)((1 - v * v) * g + v * v * 255), shade[i]);
            }
        }
        else if (row >= 0 && row < map.Rocks.Count)
        {
            float cell = map.Extent / n;
            foreach (var rock in map.Rocks[row])
            {
                int col = (int)((rock.X - map.X) / cell), line = (int)((rock.Z - map.Z) / cell);
                if (col < 0 || line < 0 || col >= n || line >= n) continue;
                float v = Math.Clamp(rock.Value, 0, 1);
                byte red = (byte)((1 - v) * v * 255), green = (byte)(v * v * 255), blue = shade[line * n + col];
                Put(line * n + col, red, green, blue);
                if (n <= 40) continue;
                if (line + 1 < n) Put((line + 1) * n + col, red, green, blue);
                if (line + 1 < n && col + 1 < n) Put((line + 1) * n + col + 1, red, green, blue);
                if (col + 1 < n) Put(line * n + col + 1, red, green, blue);
            }
        }
        return image;
    }

    internal void ProspectScreenAction(MyGuiEvent e)
    {
        if (e.Action.StartsWith("prospect-row:", StringComparison.Ordinal) && int.TryParse(e.Action.AsSpan(13), CultureInfo.InvariantCulture, out int i)) prospectRow = i;
    }
}

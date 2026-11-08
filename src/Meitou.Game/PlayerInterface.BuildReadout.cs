using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace Meitou.Game;

/// <summary>
/// The resource popup over a mine's, well's or farm's ghost (docs/game/buildings-production.md "Placement", <c>FUN_1404d9ea0</c>): the resource with its value, and for a farm the crop
/// yield and the environment, each value red to green. Drawn as a floating tooltip with its bottom centred over the top of the ghost's collision box (the original's popup sits above the
/// box's projected top; its exact x is an engine choice).
/// </summary>
sealed partial class PlayerInterface
{
    readonly Dictionary<string, (Vector3 Min, Vector3 Max)?> ghostBoxes = new(StringComparer.Ordinal);

    void AddBuildReadout(List<UiControl> controls)
    {
        if (!buildMode || buildHost is not { } host || ghost is not { } g || uiPainter is not { } painter) return;
        if (host.Placement.Readout(g.Building.Id, g.Position) is not { } readout) return;

        // The original's label is the resource's name and a colon (FUN_140a0c300's names), the value int(v x 100) and "%", coloured (1 - v, v, 0).
        string name = readout.Resource switch
        {
            ResourceField.IronResource => "Iron", ResourceField.StoneResource => "Stone", ResourceField.CopperResource => "Copper", ResourceField.CarbonResource => "Carbon",
            MachineData.WaterResource => "Water", _ => "Fertility",
        };
        static string Percent(float v) => ((int)(v * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        static string Colour(float v)
        {
            float c = Math.Clamp(v, 0, 1);
            return string.Create(CultureInfo.InvariantCulture, $"{1 - c:0.###} {c:0.###} 0");
        }
        var lines = new List<TooltipLine> { new(Loc.Tr(name) + ":", Percent(readout.Value)) };
        var colours = new List<string?> { Colour(readout.Value) };
        if (readout.CropYield is { } crop)
        {
            lines.Add(new("Crop yield:", Percent(crop)));
            colours.Add(Colour(crop));
            // Indoors the environment is "-" in the default colour; else its names in the crop yield's colour.
            bool indoor = readout.Environment is not { Count: > 0 };
            lines.Add(new("Environment:", indoor ? "-" : string.Join(", ", readout.Environment!.Select(Loc.Tr))));
            colours.Add(indoor ? null : Colour(crop));
        }

        if (GhostTop(host, g.Building.Id, g.Position, g.Rotation) is not { } top) return;
        float minimum = TitleOpen ? 0 : StandardPixels;
        var width = UiTooltips.Arrange(lines, TooltipKind.Floating, Width, Height, top, null,
            (text, wrap) => painter.MeasureText(TooltipFont, text, Height, wrap, minimum) is var s ? new(s.Width, s.Height) : default).Panel.Width;
        PlaceTooltip(controls, painter, lines, TooltipKind.Floating, null, "build-readout", top with { X = top.X + width / 2 }, colours);
    }

    /// <summary>The pixel at the middle top of the ghost's collision box (its corners projected: their mean x, their highest y); null without a box or behind the camera.</summary>
    Vector2? GhostTop(Building.BuildServices host, string record, Vector3 position, Quaternion rotation)
    {
        if (!ghostBoxes.TryGetValue(record, out var box))
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var (vertices, _) in host.Placement.CollisionMesh(record))
                foreach (var v in vertices) (min, max) = (Vector3.Min(min, v), Vector3.Max(max, v));
            ghostBoxes[record] = box = min.X <= max.X ? (min, max) : null;
        }
        var (lo, hi) = box ?? (new Vector3(-4, 0, -4), new Vector3(4, 8, 4));
        float left = float.MaxValue, right = float.MinValue, top = float.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
            if (Project(position + Vector3.Transform(corner, rotation)) is not { } p) return null;
            (left, right, top) = (MathF.Min(left, p.X), MathF.Max(right, p.X), MathF.Min(top, p.Y));
        }
        return new((left + right) / 2, top);
    }
}

using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// The medical panel's rows past the layout's nine (docs/modding.md "Medical meters"): <c>Kenshi_MainPanel.layout</c> has <c>LifeBar1</c> to
/// <c>LifeBar9</c> only (docs/game/ui-screens.md), which a human's Blood, seven parts and Hunger fill. More bars are copies of the ninth row's three
/// widgets (the bar in <c>MedicalPanel_Back</c>, its <c>Value</c> and <c>Tooltip</c> in <c>MedicalPanel_Front</c>), and more than nine rows share the
/// nine rows' height.
/// </summary>
static class MedicalPanelRows
{
    /// <summary>
    /// The rows the layout has.
    /// </summary>
    internal const int LayoutBars = 9;

    /// <summary>
    /// The name ending of the row's value and tooltip widgets, which sit in the front panel.
    /// </summary>
    static readonly string[] FrontParts = ["Value", "Tooltip"];

    /// <summary>
    /// The number of <c>LifeBarN</c> rows in <paramref name="hud"/>.
    /// </summary>
    internal static int Bars(MyGuiLayout hud)
    {
        int bars = 0;
        while (hud.Find($"LifeBar{bars + 1}") is not null) bars++;
        return bars;
    }

    /// <summary>
    /// <paramref name="hud"/> with rows up to <paramref name="bars"/>, each new one a copy of the ninth renamed; <paramref name="hud"/> itself when
    /// it has them or has no ninth row. Only the medical panels and their ancestors are new widgets.
    /// </summary>
    internal static MyGuiLayout WithBars(MyGuiLayout hud, int bars)
    {
        int have = Bars(hud);
        if (have >= bars || have < LayoutBars) return hud;

        var roots = hud.Roots.Select(r => AddRows(r, have, bars)).ToArray();
        return hud with { Roots = roots };
    }

    /// <summary>
    /// Places rows 1 to <paramref name="rows"/> evenly over the nine rows' height when there are more than nine; nothing otherwise, so the layout's
    /// own places stand.
    /// </summary>
    internal static void Spread(MyGuiLayout hud, UiBindings b, int rows)
    {
        if (rows <= LayoutBars) return;

        foreach (string part in (string[])["", .. FrontParts])
        {
            if (hud.Find($"LifeBar1{part}") is not { } first) continue;

            float pitch = (hud.Find($"LifeBar{LayoutBars}{part}")?.Position.Y - first.Position.Y ?? 0) / (LayoutBars - 1);
            float squeeze = (float)LayoutBars / rows;
            for (int i = 1; i <= rows; i++)
            {
                if (hud.Find($"LifeBar{i}{part}") is not { } w) continue;

                float y = first.Position.Y + (i - 1) * pitch * squeeze;
                b.RelativePositions[w] = w.Position with { Y = y, Height = w.Position.Height * squeeze };
            }
        }
    }

    /// <summary>
    /// <paramref name="w"/> with the rows <paramref name="have"/> + 1 to <paramref name="bars"/> added to the medical panels below it; the same
    /// widget when nothing below it changed.
    /// </summary>
    static MyGuiWidget AddRows(MyGuiWidget w, int have, int bars)
    {
        string? template = w.Name switch
        {
            "MedicalPanel_Back" => $"LifeBar{LayoutBars}",
            "MedicalPanel_Front" => null,
            _ => "",
        };

        if (template == "")
        {
            var children = w.Children.Select(c => AddRows(c, have, bars)).ToArray();
            return children.SequenceEqual(w.Children, ReferenceEqualityComparer.Instance) ? w : w with { Children = children };
        }

        var copies = new List<MyGuiWidget>();
        var sources = template is null ? FrontParts.Select(p => $"LifeBar{LayoutBars}{p}") : [template];
        foreach (string source in sources)
        {
            if (w.Children.FirstOrDefault(c => c.Name == source) is not { } row) continue;
            for (int n = have + 1; n <= bars; n++) copies.Add(Renamed(row, $"LifeBar{LayoutBars}", $"LifeBar{n}"));
        }

        return w with { Children = [.. w.Children, .. copies] };
    }

    /// <summary>
    /// A copy of <paramref name="w"/> and its children with the name prefix <paramref name="from"/> replaced by <paramref name="to"/>.
    /// </summary>
    static MyGuiWidget Renamed(MyGuiWidget w, string from, string to)
    {
        string name = w.Name.StartsWith(from, StringComparison.Ordinal) ? to + w.Name[from.Length..] : w.Name;
        return w with { Name = name, Children = w.Children.Select(c => Renamed(c, from, to)).ToArray() };
    }
}

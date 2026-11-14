using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// Inventory layout geometry resolved once in pixels, then resized with MyGUI alignment instead of rescaling every real-position child.
/// </summary>
sealed class InventoryGeometry
{
    /// <summary>
    /// One layout widget's local rectangle and its children, including the skin's client and frame pieces.
    /// </summary>
    internal sealed class Node(MyGuiWidget widget, UiRect bounds)
    {
        /// <summary>
        /// The layout widget whose runtime position is bound.
        /// </summary>
        internal readonly MyGuiWidget Widget = widget;

        /// <summary>
        /// The rectangle relative to the parent, in pixels.
        /// </summary>
        internal UiRect Bounds = bounds;

        /// <summary>
        /// The widget's immediate children.
        /// </summary>
        internal readonly List<Node> Children = [];

        /// <summary>
        /// Changes size while preserving each child's alignment and fixed margins.
        /// </summary>
        internal void Resize(float width, float height)
        {
            foreach (var child in Children)
            {
                var aligned = child.Bounds.AlignTo(new(0, 0, width, height), Bounds.Width, Bounds.Height, child.Widget.Align);
                child.Resize(aligned.Width, aligned.Height);
                child.Bounds = aligned;
            }

            Bounds = Bounds with { Width = width, Height = height };
        }

        /// <summary>
        /// Places the widget without changing its dimensions.
        /// </summary>
        internal void Move(float x, float y) => Bounds = Bounds with { X = x, Y = y };
    }

    /// <summary>
    /// The named widgets in the resolved layout.
    /// </summary>
    readonly Dictionary<string, Node> _named = new(StringComparer.Ordinal);

    /// <summary>
    /// The window whose frame encloses the contents.
    /// </summary>
    internal Node Root { get; }

    /// <summary>
    /// Resolves a template-expanded inventory layout at the viewport and interface scale before any inventory dimensions are applied.
    /// </summary>
    internal InventoryGeometry(MyGuiLayout layout, int width, int height, float scale, float templateScale)
    {
        var root = layout.Roots.First(w => w.Type == "Window");
        var initial = root.Position.RelativeTo(new(0, 0, width, height), root.RealPosition);
        Root = Resolve(root, new(0, 0, initial.Width * scale, initial.Height * scale), templateScale);
    }

    /// <summary>
    /// A widget by its layout name.
    /// </summary>
    internal Node this[string name] => _named[name];

    /// <summary>
    /// Builds the local geometry, converting real positions to pixels at the initial parent size just as MyGUI loads a layout.
    /// </summary>
    Node Resolve(MyGuiWidget widget, UiRect bounds, float scale)
    {
        var node = new Node(widget, bounds);
        if (widget.Name.Length > 0) _named.TryAdd(widget.Name, node);

        foreach (var child in widget.Children)
        {
            var parent = new UiRect(0, 0, bounds.Width, bounds.Height);
            var rect = child.Position.RelativeTo(parent, child.RealPosition);
            if (child.Properties.TryGetValue("TemplateReferenceWidth", out var rw)
                && child.Properties.TryGetValue("TemplateReferenceHeight", out var rh))
            {
                rect = new UiRect(child.Position.X * scale, child.Position.Y * scale, child.Position.Width * scale, child.Position.Height * scale)
                    .AlignTo(parent, float.Parse(rw, CultureInfo.InvariantCulture) * scale,
                        float.Parse(rh, CultureInfo.InvariantCulture) * scale, child.Align);
            }

            node.Children.Add(Resolve(child, rect, scale));
        }

        return node;
    }

    /// <summary>
    /// Binds local geometry as parent-relative rectangles so the host can place and drag the entire window without stale absolute coordinates.
    /// </summary>
    internal void Bind(UiBindings bindings)
    {
        bindings.Positions[Root.Widget.Name] = Root.Bounds;
        BindChildren(Root, bindings);
    }

    /// <summary>
    /// Writes each child position, including anonymous panels and template pieces, relative to its final parent.
    /// </summary>
    static void BindChildren(Node parent, UiBindings bindings)
    {
        foreach (var child in parent.Children)
        {
            var r = child.Bounds;
            float width = parent.Bounds.Width, height = parent.Bounds.Height;
            bindings.RelativePositions[child.Widget] = new(width > 0 ? r.X / width : 0, height > 0 ? r.Y / height : 0,
                width > 0 ? r.Width / width : 0, height > 0 ? r.Height / height : 0);
            BindChildren(child, bindings);
        }
    }

    /// <summary>
    /// Fits production and crafting panels to their inventory sections, retaining the layout's caption, text, progress bars and margins.
    /// Verified original contract: docs/game/ui-screens.md, inventory geometry.
    /// </summary>
    internal void FitBuilding(IReadOnlyList<Vector2> inputs, Vector2? output, bool crafting, bool furnace)
    {
        var titles = this["TitlesPanel"];
        var content = this["InputOutputPanel"];
        if (!crafting)
        {
            var craft = this["CraftingPanel"].Bounds;
            titles.Move(titles.Bounds.X, craft.Y);
            content.Move(content.Bounds.X, craft.Y + titles.Bounds.Height);
            Root.Resize(Root.Bounds.Width, Root.Bounds.Height - craft.Height);
        }

        var before = content.Bounds;
        var input = this["InputColumn"];
        var result = this["OutputColumn"];
        float inputWidth = FitInputs(inputs);
        float inputHeight = inputs.Count == 0 ? 0 : input.Bounds.Height;
        if (output is { } size)
        {
            FitPanel("OutputPanel", "out", size);
            var panel = this["OutputPanel"].Bounds;
            result.Move(inputs.Count > 0 ? input.Bounds.X + inputWidth : input.Bounds.X, result.Bounds.Y);
            result.Resize(panel.Width, Math.Max(panel.Height, inputHeight));
            if (furnace)
            {
                var data = this["OutputDataPanel"];
                data.Move(0, panel.Y + panel.Height);
                data.Resize(panel.Width, Math.Max(0, result.Bounds.Height - panel.Height));
            }
        }

        float outputWidth = output.HasValue ? result.Bounds.Width : 0;
        float height = Math.Max(inputHeight, output.HasValue ? result.Bounds.Height : 0);
        content.Resize(inputWidth + outputWidth, height);
        float growWidth = content.Bounds.Width - before.Width;
        if (crafting || furnace) growWidth = Math.Max(0, growWidth);

        Root.Resize(Root.Bounds.Width + growWidth, Root.Bounds.Height + content.Bounds.Height - before.Height);
        SetTitle("InputTitleText", 0, inputWidth);
        SetTitle("OutputTitleText", inputWidth, outputWidth);
        CentreSection("out", "OutputProgressBar");
    }

    /// <summary>
    /// Fits the input panels independently, stacks the second below the first and uses their maximum width for the column.
    /// </summary>
    float FitInputs(IReadOnlyList<Vector2> inputs)
    {
        float width = 0, height = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            FitPanel($"Input{n}Panel", $"in{n}", inputs[i]);
            var panel = this[$"Input{n}Panel"];
            panel.Move(panel.Bounds.X, height);
            width = Math.Max(width, panel.Bounds.Width);
            height += panel.Bounds.Height;
        }

        this["InputColumn"].Resize(width, height);
        for (int i = 0; i < inputs.Count; i++)
        {
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            CentreSection($"in{n}", $"Input{n}ProgressBar");
        }

        return width;
    }

    /// <summary>
    /// Enlarges the enclosing panel by the grid's positive size delta; smaller grids retain the layout's minimum panel dimensions.
    /// </summary>
    void FitPanel(string panelName, string sectionName, Vector2 size)
    {
        var section = this[sectionName];
        var panel = this[panelName];
        float dx = Math.Max(0, size.X - section.Bounds.Width), dy = Math.Max(0, size.Y - section.Bounds.Height);
        section.Resize(size.X, size.Y);
        panel.Resize(panel.Bounds.Width + dx, panel.Bounds.Height + dy);
    }

    /// <summary>
    /// Centres a grid and its progress bar independently in their shared content panel.
    /// </summary>
    void CentreSection(string name, string bar)
    {
        if (!_named.TryGetValue(name, out var section) || !_named.TryGetValue(bar, out var progress)) return;
        var parent = _named.Values.FirstOrDefault(n => n.Children.Contains(section));
        if (parent is null) return;

        section.Move((parent.Bounds.Width - section.Bounds.Width) / 2, section.Bounds.Y);
        progress.Move((parent.Bounds.Width - progress.Bounds.Width) / 2, progress.Bounds.Y);
    }

    /// <summary>
    /// Aligns a section header with its final column, retaining its layout height.
    /// </summary>
    void SetTitle(string name, float x, float width)
    {
        var title = this[name];
        title.Resize(width, title.Bounds.Height);
        title.Move(x, 0);
    }
}

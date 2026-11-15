using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// The conversation panel's choices laid out as the original lays them out (docs/game/dialogue.md "UI"): each choice word-wrapped to the player
/// box, its row as high as its text, and the player panel grown upward, the NPC panel above it, when the choices need more room than the box.
/// </summary>
sealed class DialogueChoiceLayout
{
    /// <summary>
    /// The choices' font (the <c>Kenshi_WordWrapEmpty</c> template's client, <c>Kenshi_TextboxStandardText</c>).
    /// </summary>
    const string ChoiceFont = "Kenshi_StandardFont_Medium";

    /// <summary>
    /// A choice's side margin as a share of the canvas width, and the gap after it as a share of the canvas height.
    /// </summary>
    const float Margin = 0.05f;

    /// <summary>
    /// The choices and the viewport the rows were laid out for; a new set or size lays them out again.
    /// </summary>
    IReadOnlyList<string> _choices = [];

    /// <summary>
    /// The viewport size the rows were laid out for.
    /// </summary>
    (int Width, int Height) _viewport;

    /// <summary>
    /// The choice box's canvas height left by the last layout, which sets the next layout's gaps; 0 before the first.
    /// </summary>
    float _canvasHeight;

    /// <summary>
    /// How much taller than the layout the choice box is.
    /// </summary>
    float _grow;

    /// <summary>
    /// Each choice's rectangle relative to the choice box's corner.
    /// </summary>
    readonly List<UiRect> _rows = [];

    /// <summary>
    /// Lays out the choices when they changed, then binds the grown panels.
    /// </summary>
    internal void Bind(UiBindings b, MyGuiLayout layout, IReadOnlyList<string> choices, UiPainter? painter, int width, int height, float minimumPixels)
    {
        if (layout.Find("PlayerPanel") is not { } panel || layout.Find("NpcPanel") is not { } npc || painter is null) return;

        var screen = new UiRect(0, 0, width, height);
        var panelRect = panel.Position.RelativeTo(screen, panel.RealPosition);
        if (!choices.SequenceEqual(_choices) || _viewport != (width, height))
            LayOut(choices, DialogBox(panel, panelRect), painter, height, minimumPixels);
        _choices = choices;
        _viewport = (width, height);
        if (_grow <= 0) return;

        // The player panel keeps its bottom and grows upward, its children kept to their align; the NPC panel keeps its gap above it.
        b.Positions[panel.Name] = panelRect with { Y = panelRect.Y - _grow, Height = panelRect.Height + _grow };
        BindChildren(b, panel, panelRect.Width, panelRect.Height, panelRect.Height + _grow);
        var npcRect = npc.Position.RelativeTo(screen, npc.RealPosition);
        b.Positions[npc.Name] = npcRect with { Y = npcRect.Y - _grow };
    }

    /// <summary>
    /// The choice buttons in the choice box <paramref name="area"/>.
    /// </summary>
    internal void AddChoices(List<UiControl> controls, UiControl area, IReadOnlyList<string> choices, Vector4 colour, Vector4 hover)
    {
        for (int i = 0; i < choices.Count && i < _rows.Count; i++)
        {
            var row = _rows[i];
            var bounds = row with { X = area.Bounds.X + row.X, Y = area.Bounds.Y + row.Y };
            controls.Add(Choice(i, choices[i], bounds) with { TextColour = colour, HoverColour = hover, Clip = area.Clip ?? area.Bounds });
        }
    }

    /// <summary>
    /// A choice's button: "N. text", wrapped, picked by a click.
    /// </summary>
    static UiControl Choice(int index, string text, UiRect bounds) =>
        new($"dialogue-choice-{index}", "Button", bounds, $"{index + 1}. {text}", $"dialogue-choice:{index}", "", true,
            new Dictionary<string, string> { ["FontName"] = ChoiceFont, ["TextAlign"] = "Left Top", ["WordWrap"] = "true" }, Vector4.Zero,
            Pickable: true);

    /// <summary>
    /// The choice box's layout size inside the player panel <paramref name="panelRect"/>.
    /// </summary>
    static UiRect DialogBox(MyGuiWidget panel, UiRect panelRect)
    {
        foreach (var child in panel.Children)
        {
            var rect = child.Position.RelativeTo(panelRect, child.RealPosition);
            if (child.Name == "PlayerDialog") return rect;
            if (child.Children.Count > 0 && DialogBox(child, rect) is { Width: > 0 } found) return found;
        }

        return default;
    }

    /// <summary>
    /// The rows: each choice's wrapped height, a gap of the margin share of the last canvas height after it; the box grows to hold them.
    /// </summary>
    void LayOut(IReadOnlyList<string> choices, UiRect box, UiPainter painter, int viewportHeight, float minimumPixels)
    {
        float canvasHeight = _canvasHeight > 0 ? _canvasHeight : box.Height;
        float x = box.Width * Margin, width = box.Width - 2 * x, gap = canvasHeight * Margin, y = 0;

        _rows.Clear();
        for (int i = 0; i < choices.Count; i++)
        {
            float rowHeight = painter.TextHeight(Choice(i, choices[i], new(0, 0, width, box.Height)), viewportHeight, minimumPixels);
            _rows.Add(new(x, y, width, rowHeight));
            y += rowHeight + gap;
        }

        _grow = Math.Max(0, y - box.Height);
        _canvasHeight = box.Height + _grow;
    }

    /// <summary>
    /// Places <paramref name="parent"/>'s children for its new height by their MyGUI align, as fractions of it, down the tree.
    /// </summary>
    static void BindChildren(UiBindings b, MyGuiWidget parent, float width, float oldHeight, float newHeight)
    {
        var before = new UiRect(0, 0, width, oldHeight);
        // A template's pieces already align to their owner's bounds (UiDocument.WidgetBounds).
        foreach (var child in parent.Children.Where(c => !c.Properties.ContainsKey("TemplateReferenceWidth")))
        {
            var local = child.Position.RelativeTo(before, child.RealPosition);
            var placed = local.AlignTo(new(0, 0, width, newHeight), width, oldHeight, child.Align);
            b.RelativePositions[child] = new(placed.X / width, placed.Y / newHeight, placed.Width / width, placed.Height / newHeight);
            if (placed.Height != local.Height) BindChildren(b, child, local.Width, local.Height, placed.Height);
        }
    }
}

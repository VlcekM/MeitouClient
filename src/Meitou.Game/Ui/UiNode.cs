using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// Pixels plus a fraction of the available parent dimension. No browser/CSS dependency.
/// </summary>
public readonly record struct UiLength(float Pixels, float Fraction = 0)
{
    /// <summary>
    /// <paramref name="percent"/> per cent of the available dimension.
    /// </summary>
    public static UiLength Percent(float percent) => new(0, percent / 100);

    /// <summary>
    /// All of the available dimension.
    /// </summary>
    public static UiLength Fill => new(0, 1);

    /// <summary>
    /// A length of <paramref name="pixels"/> only.
    /// </summary>
    public static implicit operator UiLength(float pixels) => new(pixels);

    /// <summary>
    /// The length in pixels for <paramref name="available"/> pixels of the parent.
    /// </summary>
    public float Resolve(float available) => Pixels + Fraction * available;
}

/// <summary>
/// A node's size and its offset from the parent's left and top (or from its right and bottom when given).
/// </summary>
public sealed record UiPlacement(UiLength Width, UiLength Height, UiLength Left = default, UiLength Top = default,
    UiLength? Right = null, UiLength? Bottom = null)
{
    /// <summary>
    /// The whole parent.
    /// </summary>
    public static UiPlacement Fill { get; } = new(UiLength.Fill, UiLength.Fill);

    /// <summary>
    /// The node's rectangle in <paramref name="parent"/>, its size at least 0.
    /// </summary>
    public UiRect Resolve(UiRect parent)
    {
        float w = Math.Max(0, Width.Resolve(parent.Width)), h = Math.Max(0, Height.Resolve(parent.Height));
        return new(parent.X + (Right is { } right ? parent.Width - w - right.Resolve(parent.Width) : Left.Resolve(parent.Width)),
            parent.Y + (Bottom is { } bottom ? parent.Height - h - bottom.Resolve(parent.Height) : Top.Resolve(parent.Height)), w, h);
    }
}

/// <summary>
/// How a node places its children.
/// </summary>
public enum UiFlow
{
    /// <summary>
    /// The children's own placements.
    /// </summary>
    Absolute,

    /// <summary>
    /// Side by side, each after the last plus the gap.
    /// </summary>
    Row,

    /// <summary>
    /// One under the other, each after the last plus the gap.
    /// </summary>
    Column,
}

/// <summary>
/// What a node is drawn as.
/// </summary>
public enum UiNodeKind
{
    /// <summary>
    /// A widget.
    /// </summary>
    Panel,

    /// <summary>
    /// A text box (not pickable by default).
    /// </summary>
    Label,

    /// <summary>
    /// A button.
    /// </summary>
    Button,
}

/// <summary>
/// Reusable visual tokens; state colours are evaluated by the native painter.
/// </summary>
public sealed record UiStyle
{
    /// <summary>
    /// The fill colour.
    /// </summary>
    public Vector4 Background { get; init; }

    /// <summary>
    /// The text colour.
    /// </summary>
    public Vector4 Text { get; init; } = new(0.92f, 0.92f, 0.88f, 1);

    /// <summary>
    /// The colour under the pointer; null: unchanged.
    /// </summary>
    public Vector4? Hover { get; init; }

    /// <summary>
    /// The colour while pressed; null: unchanged.
    /// </summary>
    public Vector4? Active { get; init; }

    /// <summary>
    /// The colour while disabled; null: unchanged.
    /// </summary>
    public Vector4? Disabled { get; init; }

    /// <summary>
    /// The text's alignment.
    /// </summary>
    public string TextAlign { get; init; } = "Left VCenter";

    /// <summary>
    /// The MyGUI skin; empty for none.
    /// </summary>
    public string Skin { get; init; } = "";

    /// <summary>
    /// The font.
    /// </summary>
    public string FontName { get; init; } = "Kenshi_StandardFont_Medium";

    /// <summary>
    /// The text's scale.
    /// </summary>
    public float TextScale { get; init; } = 1;

    /// <summary>
    /// The text's padding.
    /// </summary>
    public UiInsets TextPadding { get; init; }
}

/// <summary>
/// A composable native control tree, built in code by the game or an extension once per UI frame.
/// </summary>
public sealed class UiNode(string id, UiNodeKind kind = UiNodeKind.Panel)
{
    /// <summary>
    /// The node's id (the control's name).
    /// </summary>
    public string Id { get; } = id;

    /// <summary>
    /// What the node is drawn as.
    /// </summary>
    public UiNodeKind Kind { get; } = kind;

    /// <summary>
    /// Where the node sits in its parent (when its parent's flow is absolute; otherwise its size).
    /// </summary>
    public UiPlacement Placement { get; init; } = UiPlacement.Fill;

    /// <summary>
    /// The style's id; empty for the default.
    /// </summary>
    public string Style { get; init; } = "";

    /// <summary>
    /// The node's text.
    /// </summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// The action a click dispatches; empty for none.
    /// </summary>
    public string Action { get; init; } = "";

    /// <summary>
    /// The argument the action gets.
    /// </summary>
    public string Argument { get; init; } = "";

    /// <summary>
    /// The node takes clicks (its children too).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The node and its children are laid out.
    /// </summary>
    public bool Visible { get; init; } = true;

    /// <summary>
    /// The node takes the pointer (all but labels by default).
    /// </summary>
    public bool Pickable { get; init; } = kind != UiNodeKind.Label;

    /// <summary>
    /// How the children are placed.
    /// </summary>
    public UiFlow Flow { get; init; }

    /// <summary>
    /// The inset of the children's area, pixels.
    /// </summary>
    public float Padding { get; init; }

    /// <summary>
    /// The space between flowed children, pixels.
    /// </summary>
    public float Gap { get; init; }

    /// <summary>
    /// The child nodes.
    /// </summary>
    public List<UiNode> Children { get; } = [];
}

/// <summary>
/// The HUD slots extensions contribute to.
/// </summary>
public static class UiSlots
{
    /// <summary>
    /// The slot ids: the toolbar, the selection, the squad and the overlay.
    /// </summary>
    public const string Toolbar = "hud.toolbar", Selection = "hud.selection", Squad = "hud.squad", Overlay = "hud.overlay";
}

/// <summary>
/// The public entry point for a future managed code-mod loader. Registration is owned and disposable.
/// </summary>
public interface IUiExtension
{
    /// <summary>
    /// The extension's id (its registrations' prefix).
    /// </summary>
    string Id { get; }

    /// <summary>
    /// The UI API version the extension needs.
    /// </summary>
    int ApiVersion => 1;

    /// <summary>
    /// Registers the extension's styles, controls, actions and contributions into <paramref name="registration"/>.
    /// </summary>
    void Register(UiRegistration registration);
}
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

    /// <summary>
    /// A picture, <see cref="UiNode.Image"/>, stretched over the node (not pickable by default).
    /// </summary>
    Image,

    /// <summary>
    /// A one-line edit box (MyGUI <c>EditBox</c>, skin <c>Kenshi_EditBox</c> unless the style names one): a click gives it the keys, and each change
    /// of its text runs <see cref="UiNode.Action"/> with the text in <see cref="UiActionContext.Text"/>. Its text is <see cref="UiNode.Text"/>.
    /// </summary>
    TextBox,

    /// <summary>
    /// A scroll view: a panel whose children are laid out by its flow, shifted by its scroll offset and cut off at its edges. The wheel over it (or
    /// a child) and the <c>Kenshi_ScrollBarV</c>-skinned bar at its right (shown while the children overflow, <see cref="UiNode.ScrollBar"/>)
    /// scroll it; the host keeps the offset across frames by the extension and node id.
    /// </summary>
    ScrollView,
}

/// <summary>
/// The pointer events a node's <see cref="UiNode.Action"/> gets besides the left press it always gets (flags; room to grow).
/// </summary>
[Flags]
public enum UiPointerEvents
{
    /// <summary>
    /// The press only.
    /// </summary>
    None = 0,

    /// <summary>
    /// The left button's release after a press on the node or a child (wherever the pointer is then).
    /// </summary>
    Release = 1,

    /// <summary>
    /// Each pointer move while the left button pressed on the node or a child is held.
    /// </summary>
    Drag = 2,

    /// <summary>
    /// The wheel over the node or a child that does not take it itself.
    /// </summary>
    Wheel = 4,

    /// <summary>
    /// The pointer coming onto the node or a child, and leaving it.
    /// </summary>
    Hover = 8,

    /// <summary>
    /// A press of another button than the left (right, middle, the extra ones) on the node or a child: <see cref="UiEventKind.Press"/> with
    /// <see cref="UiActionContext.Button"/>. That press is then the node's, not the game's.
    /// </summary>
    Buttons = 16,
}

/// <summary>
/// What ran an action (<see cref="UiActionContext.Kind"/>).
/// </summary>
public enum UiEventKind
{
    /// <summary>
    /// The left button pressed on the node: the click every action gets.
    /// </summary>
    Press,

    /// <summary>
    /// A text box's text changed (<see cref="UiActionContext.Text"/>).
    /// </summary>
    Text,

    /// <summary>
    /// The left button released (<see cref="UiPointerEvents.Release"/>).
    /// </summary>
    Release,

    /// <summary>
    /// The pointer moved with the button held (<see cref="UiPointerEvents.Drag"/>).
    /// </summary>
    Drag,

    /// <summary>
    /// A wheel step (<see cref="UiPointerEvents.Wheel"/>).
    /// </summary>
    Wheel,

    /// <summary>
    /// The pointer came onto the node (<see cref="UiPointerEvents.Hover"/>).
    /// </summary>
    Enter,

    /// <summary>
    /// The pointer left the node (<see cref="UiPointerEvents.Hover"/>).
    /// </summary>
    Leave,

    /// <summary>
    /// Enter in a text box that has the keys (<see cref="UiActionContext.Text"/> its text); the box lets go of the keys.
    /// </summary>
    Submit,
}

/// <summary>
/// The part of an image node's picture it shows: <paramref name="Rect"/> in the picture's pixels, or with <paramref name="Normalized"/> in
/// fractions of its size (UV, 0 to 1).
/// </summary>
public readonly record struct UiImageCrop(UiRect Rect, bool Normalized = false)
{
    /// <summary>
    /// The picture's pixels from <paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/> by <paramref name="height"/> (an atlas cell).
    /// </summary>
    public static UiImageCrop Pixels(float x, float y, float width, float height) => new(new(x, y, width, height));

    /// <summary>
    /// The picture from UV <paramref name="u0"/>, <paramref name="v0"/> to <paramref name="u1"/>, <paramref name="v1"/>.
    /// </summary>
    public static UiImageCrop Uv(float u0, float v0, float u1, float v1) => new(new(u0, v0, u1 - u0, v1 - v0), true);
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
    /// The node's text; a text box's text, which the box takes again whenever it differs (so the node echoes the text its action gets, or sets
    /// another, such as empty to clear it).
    /// </summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// A text box's most characters; 0 for MyGUI's default (2048).
    /// </summary>
    public int MaxLength { get; init; }

    /// <summary>
    /// An image node's picture: the full path of a PNG, TGA, JPEG, BMP or DDS file (a mod's own: under <c>ModContext.Folder</c>). It is read off
    /// the UI thread and shown once loaded; a file that cannot be read shows nothing.
    /// </summary>
    public string Image { get; init; } = "";

    /// <summary>
    /// An image node's picture instead of <see cref="Image"/>: the string id of a CHARACTER (or ANIMAL_CHARACTER) record, whose portrait is
    /// drawn as the squad bar's are, in the record's own gear; square, and transparent round the character. It appears once the look has loaded;
    /// an unknown record shows nothing. At most <see cref="RecordPortraits.Cells"/> different records show at once.
    /// </summary>
    public string Portrait { get; init; } = "";

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
    /// The node takes the pointer (all but labels and images by default).
    /// </summary>
    public bool Pickable { get; init; } = kind is not (UiNodeKind.Label or UiNodeKind.Image);

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
    /// The pointer events <see cref="Action"/> gets besides the press (<see cref="UiActionContext.Kind"/> tells them apart); none by default. A press
    /// on a child goes to the nearest node, the child or one of its parents, asking for Release or Drag; the wheel and hover to the nearest asking
    /// for them.
    /// </summary>
    public UiPointerEvents Pointer { get; init; }

    /// <summary>
    /// The text's scale instead of the style's; null: the style's. Finite, above 0 and at most <see cref="UiHost.MaxTextScale"/>.
    /// </summary>
    public float? TextScale { get; init; }

    /// <summary>
    /// The text colour (RGBA, 0 to 1) instead of the style's; null: the style's. Inline <c>#RRGGBB</c> tags in <see cref="Text"/> colour the text
    /// after them (<c>##</c> is a #), as in the game's own texts.
    /// </summary>
    public Vector4? TextColour { get; init; }

    /// <summary>
    /// The node's opacity, 0 to 1, multiplied into its fill, picture, skin and text and into its children's; 1 by default. A transparent node still
    /// takes the pointer as it would opaque.
    /// </summary>
    public float Opacity { get; init; } = 1;

    /// <summary>
    /// The part of an image node's <see cref="Image"/> it shows (an atlas cell); null: the whole picture. Not for a portrait.
    /// </summary>
    public UiImageCrop? ImageCrop { get; init; }

    /// <summary>
    /// On a contribution's root in <see cref="UiSlots.Overlay"/>: it is drawn also while the interface is hidden (F7), without input and
    /// portraits, laid out with no bindings (<see cref="UiFrameContext.Text"/> gives its fallback).
    /// </summary>
    public bool ShowWithHudHidden { get; init; }

    /// <summary>
    /// The node changes on real time (a fade): while one is shown the overlay is laid out every frame instead of at least 20 times a second.
    /// </summary>
    public bool Animated { get; init; }

    /// <summary>
    /// A scroll view shows its bar while its children overflow; true by default.
    /// </summary>
    public bool ScrollBar { get; init; } = true;

    /// <summary>
    /// A scroll view's step for a wheel step and an arrow of its bar, pixels (a page is its height); finite and above 0.
    /// </summary>
    public float ScrollStep { get; init; } = 24;

    /// <summary>
    /// A text box takes the typing focus (the keys) when it shows with this set and did not the frame before, so a node set once, or kept set,
    /// focuses once; a disabled box does not take it.
    /// </summary>
    public bool Focus { get; init; }

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
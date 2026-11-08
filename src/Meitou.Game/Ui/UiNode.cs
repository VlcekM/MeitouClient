using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Ui;

/// <summary>Pixels plus a fraction of the available parent dimension. No browser/CSS dependency.</summary>
public readonly record struct UiLength(float Pixels, float Fraction = 0)
{
    public static UiLength Percent(float percent) => new(0, percent / 100);
    public static UiLength Fill => new(0, 1);
    public static implicit operator UiLength(float pixels) => new(pixels);
    public float Resolve(float available) => Pixels + Fraction * available;
}

public sealed record UiPlacement(UiLength Width, UiLength Height, UiLength Left = default, UiLength Top = default,
    UiLength? Right = null, UiLength? Bottom = null)
{
    public static UiPlacement Fill { get; } = new(UiLength.Fill, UiLength.Fill);
    public UiRect Resolve(UiRect parent)
    {
        float w = Math.Max(0, Width.Resolve(parent.Width)), h = Math.Max(0, Height.Resolve(parent.Height));
        return new(parent.X + (Right is { } right ? parent.Width - w - right.Resolve(parent.Width) : Left.Resolve(parent.Width)),
            parent.Y + (Bottom is { } bottom ? parent.Height - h - bottom.Resolve(parent.Height) : Top.Resolve(parent.Height)), w, h);
    }
}

public enum UiFlow { Absolute, Row, Column }
public enum UiNodeKind { Panel, Label, Button }

/// <summary>Reusable visual tokens; state colours are evaluated by the native painter.</summary>
public sealed record UiStyle
{
    public Vector4 Background { get; init; }
    public Vector4 Text { get; init; } = new(0.92f, 0.92f, 0.88f, 1);
    public Vector4? Hover { get; init; }
    public Vector4? Active { get; init; }
    public Vector4? Disabled { get; init; }
    public string TextAlign { get; init; } = "Left VCenter";
    public string Skin { get; init; } = "";
    public string FontName { get; init; } = "Kenshi_StandardFont_Medium";
    public float TextScale { get; init; } = 1;
    public UiInsets TextPadding { get; init; }
}

/// <summary>A composable native control tree, built in code by the game or an extension once per UI frame.</summary>
public sealed class UiNode(string id, UiNodeKind kind = UiNodeKind.Panel)
{
    public string Id { get; } = id;
    public UiNodeKind Kind { get; } = kind;
    public UiPlacement Placement { get; init; } = UiPlacement.Fill;
    public string Style { get; init; } = "";
    public string Text { get; init; } = "";
    public string Action { get; init; } = "";
    public string Argument { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool Visible { get; init; } = true;
    public bool Pickable { get; init; } = kind != UiNodeKind.Label;
    public UiFlow Flow { get; init; }
    public float Padding { get; init; }
    public float Gap { get; init; }
    public List<UiNode> Children { get; } = [];
}

public static class UiSlots
{
    public const string Toolbar = "hud.toolbar", Selection = "hud.selection", Squad = "hud.squad", Overlay = "hud.overlay";
}

/// <summary>The public entry point for a future managed code-mod loader. Registration is owned and disposable.</summary>
public interface IUiExtension
{
    string Id { get; }
    int ApiVersion => 1;
    void Register(UiRegistration registration);
}

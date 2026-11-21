using System.Numerics;

namespace Meitou.Game.Ui;

/// <summary>
/// The Squad XP multiplier ("XP x10") at the top left of the screen during play (Advanced settings "Show Squad XP"), drawn with the game's interface
/// so it shows only with the HUD. <paramref name="text"/> is the label, null while it is off.
/// </summary>
sealed class SquadXpLabel(Func<string?> text) : IUiExtension
{
    /// <summary>
    /// The extension's id (namespaces its control and style).
    /// </summary>
    public string Id => "meitou-xp";

    /// <summary>
    /// Registers the label and puts it in the HUD's overlay.
    /// </summary>
    public void Register(UiRegistration registration)
    {
        string style = registration.RegisterStyle("label", new UiStyle
        {
            Text = new Vector4(0.95f, 0.85f, 0.45f, 1),
            FontName = "Kenshi_StandardFont_Medium",
            TextAlign = "Left Top",
        });
        string key = registration.Key("label");
        string control = registration.RegisterControl("label", _ => Node(key, style));
        registration.Contribute(UiSlots.Overlay, frame => frame.CreateControl(control));
    }

    /// <summary>
    /// The label's node this frame: hidden while <see cref="text"/> gives none.
    /// </summary>
    UiNode Node(string key, string style) =>
        text() is { } label
            ? new UiNode(key, UiNodeKind.Label) { Placement = new UiPlacement(160, 24, Left: 12, Top: 10), Style = style, Text = label }
            : new UiNode(key, UiNodeKind.Label) { Visible = false };
}

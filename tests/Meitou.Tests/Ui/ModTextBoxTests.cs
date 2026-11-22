using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace Meitou.Tests.Ui;

/// <summary>
/// A mod overlay's text box (<see cref="UiNodeKind.TextBox"/>) through the screens' edit focus, wired as the interface wires it.
/// </summary>
public class ModTextBoxTests
{
    sealed class Extension(Action<UiRegistration> register) : IUiExtension
    {
        public string Id => "mod";
        public void Register(UiRegistration r) => register(r);
    }

    static readonly Dictionary<string, UiRect> Slots = new() { [UiSlots.Overlay] = new(0, 0, 800, 600) };

    /// <summary>
    /// Lays out the overlay and lets the screen host decorate its text boxes, as <c>PlayerInterface.ArrangeBase</c> does.
    /// </summary>
    static List<UiControl> Arrange(UiHost host, MyGuiScreenHost screens)
    {
        var controls = host.Arrange(UiMode.Meitou, 800, 600, new UiBindings(), Slots).ToList();
        screens.ArrangeOverlayEdits(controls);
        return controls;
    }

    [Fact]
    public void ATextBoxTakesTheKeysAndItsActionGetsTheTextWhichTheNodeCanReplace()
    {
        var host = new UiHost();
        var screens = new MyGuiScreenHost(new());
        screens.Activated += e => host.DispatchText(e);
        string text = "ab";
        using var module = host.Register(new Extension(r =>
        {
            string action = r.RegisterAction("typed", c => text = c.Text);
            r.Contribute(UiSlots.Overlay, _ => new UiNode("name", UiNodeKind.TextBox)
            {
                Placement = new(200, 30), Text = text, Action = action, MaxLength = 3,
            });
        }));

        var box = Assert.Single(Arrange(host, screens));
        Assert.Equal(("EditBox", "Kenshi_EditBox", "3"), (box.Type, box.Skin, box.Properties["MaxTextLength"]));
        Assert.True(screens.PressOverlayEdit(box, new Vector2(100, 15)));
        Assert.True(screens.HasKeyboardFocus);

        screens.TextInput('c');
        screens.TextInput('d');
        Assert.Equal("abc", text);
        screens.Backspace();
        Assert.Equal("ab", text);
        Assert.Equal("2", Assert.Single(Arrange(host, screens)).Properties["CaretIndex"]);

        text = "";
        Arrange(host, screens);
        screens.TextInput('x');
        Assert.Equal("x", text);
        Assert.True(screens.EditEnter(false));
        Assert.False(screens.HasKeyboardFocus);
    }

    [Fact]
    public void OtherNodeKindsAreNotEditBoxesAndAClickGetsNoText()
    {
        var host = new UiHost();
        var screens = new MyGuiScreenHost(new());
        string? clicked = null;
        using var module = host.Register(new Extension(r =>
        {
            string action = r.RegisterAction("click", c => clicked = c.Text);
            r.Contribute(UiSlots.Overlay, _ => new UiNode("panel")
            {
                Flow = UiFlow.Column,
                Children =
                {
                    new UiNode("label", UiNodeKind.Label) { Placement = new(100, 20) },
                    new UiNode("button", UiNodeKind.Button) { Placement = new(100, 20), Action = action },
                    new UiNode("image", UiNodeKind.Image) { Placement = new(100, 20) },
                },
            });
        }));

        var controls = Arrange(host, screens);
        Assert.Equal(new[] { "Widget", "TextBox", "Button", "ImageBox" }, controls.Select(c => c.Type));
        Assert.All(controls, c => Assert.False(c.Properties.ContainsKey(MyGuiScreenHost.OverlayEditProperty)));
        Assert.All(controls, c => Assert.False(screens.PressOverlayEdit(c, Vector2.Zero)));
        Assert.True(host.Dispatch(controls[2]));
        Assert.Equal("", clicked);
        Assert.False(screens.HasKeyboardFocus);
    }
}

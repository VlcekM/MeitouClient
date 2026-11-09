using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace Meitou.Tests.Ui;

public class NativeUiTests
{
    sealed class Extension(string id, Action<UiRegistration> register) : IUiExtension
    {
        public string Id => id;
        public void Register(UiRegistration r) => register(r);
    }

    static Dictionary<string, UiRect> Slots(int width = 800, int height = 600) => new() { [UiSlots.Overlay] = new(0, 0, width, height) };

    [Fact]
    public void ACodeExtensionBuildsAReusableControlAndDispatchesAnActionWithReadOnlyData()
    {
        var host = new UiHost();
        string? observed = null;
        using var module = host.Register(new Extension("sample", r =>
        {
            string style = r.RegisterStyle("button", new() { Background = Vector4.One, TextPadding = new(8, 4, 8, 4) });
            string action = r.RegisterAction("click", c => observed = c.Frame.Text("name") + ":" + c.Argument);
            string control = r.RegisterControl("status", c => new UiNode(r.Key("status"), UiNodeKind.Button)
            {
                Placement = new(100, 30, Right: 10, Bottom: 20), Style = style, Text = c.Text("name"), Action = action, Argument = "selected",
            });
            r.Contribute(UiSlots.Overlay, c => c.CreateControl(control));
        }));
        var bindings = new UiBindings();
        bindings.Text["name"] = "Wanderer";
        var frame = host.Arrange(UiMode.Meitou, 800, 600, bindings, Slots());
        bindings.Text["name"] = "changed after frame";
        var button = Assert.Single(frame);
        Assert.Equal(new UiRect(690, 550, 100, 30), button.Bounds);
        Assert.Equal("sample:click", button.Action);
        Assert.Equal("Wanderer", button.Caption);
        Assert.Equal("8 4 8 4", button.Properties["TextPadding"]);
        Assert.True(host.Dispatch(button));
        Assert.Equal("Wanderer:selected", observed);
    }

    [Fact]
    public void ModulesAreNamespacedAndDisposalRemovesControlsStylesActionsAndContributions()
    {
        var host = new UiHost();
        int clicks = 0;

        Extension Make(string id) => new(id, r =>
        {
            string style = r.RegisterStyle("button", new() { Background = Vector4.One });
            string action = r.RegisterAction("click", _ => clicks++);
            string factory = r.RegisterControl("control", _ => new UiNode(r.Key("control"), UiNodeKind.Button) { Style = style, Action = action });
            r.Contribute(UiSlots.Overlay, c => c.CreateControl(factory));
        });

        var first = host.Register(Make("first"));
        using var second = host.Register(Make("second"));
        var before = host.Arrange(UiMode.Meitou, 800, 600, new(), Slots());
        Assert.Equal(2, before.Count);
        first.Dispose();
        first.Dispose();
        Assert.False(host.Dispatch(before[0]));
        var after = Assert.Single(host.Arrange(UiMode.Meitou, 800, 600, new(), Slots()));
        Assert.Equal("second:control", after.Name);
        Assert.Throws<KeyNotFoundException>(() => host.Style("first:button"));
        Assert.True(host.Dispatch(after));
        Assert.Equal(1, clicks);
        using var again = host.Register(Make("first"));
    }

    [Fact]
    public void AModCanExtendABuiltInControlAndUnloadingRestoresTheOriginalControlAndStyle()
    {
        var host = new UiHost();
        using var game = host.Register(new Extension("game", r =>
        {
            string control = r.RegisterControl("panel", _ => new("original"));
            r.Contribute(UiSlots.Overlay, c => c.CreateControl(control));
        }));
        var mod = host.Register(new Extension("mod", r => r.OverrideControl("game:panel", c =>
        {
            var original = c.CreateBaseControl("game:panel");
            original.Children.Add(new("extra", UiNodeKind.Label) { Text = "Mod content" });
            return original;
        })));
        Assert.Equal(2, host.Arrange(UiMode.Meitou, 800, 600, new(), Slots()).Count);
        mod.Dispose();
        Assert.Equal("original", Assert.Single(host.Arrange(UiMode.Meitou, 800, 600, new(), Slots())).Name);

        // A style override restores the previous style when unloaded.
        var styled = new UiHost();
        using var baseStyles = styled.Register(new Extension("game", r => r.RegisterStyle("button", new() { Background = Vector4.One })));
        var skin = styled.Register(new Extension("skin", r => r.OverrideStyle("game:button", new() { Background = Vector4.Zero })));
        Assert.Equal(Vector4.Zero, styled.Style("game:button").Background);
        skin.Dispose();
        Assert.Equal(Vector4.One, styled.Style("game:button").Background);
    }

    sealed class IncompatibleExtension : IUiExtension
    {
        public string Id => "incompatible";
        public int ApiVersion => UiHost.ApiVersion + 1;
        public void Register(UiRegistration r) => throw new InvalidOperationException("must not register");
    }

    [Fact]
    public void IncompatibleApiAndDisposedScopesAreRejected()
    {
        var host = new UiHost();
        Assert.Throws<NotSupportedException>(() => host.Register(new IncompatibleExtension()));
        UiRegistration? registration = null;
        var module = host.Register(new Extension("test", r => registration = r));
        module.Dispose();
        Assert.Throws<ObjectDisposedException>(() => registration!.RegisterStyle("late", new()));

        // A registration that fails rolls back and can be retried.
        var failing = new UiHost();
        Assert.Throws<InvalidOperationException>(() => failing.Register(new Extension("sample", r =>
        {
            r.RegisterStyle("temporary", new());
            r.RegisterAction("temporary", _ => { });
            throw new InvalidOperationException("load failed");
        })));
        Assert.Throws<KeyNotFoundException>(() => failing.Style("sample:temporary"));
        using var retry = failing.Register(new Extension("sample", r => r.RegisterStyle("temporary", new())));
        Assert.Throws<InvalidOperationException>(() => failing.Register(new Extension("sample", _ => { })));
    }

    [Fact]
    public void ContributionsCanTargetEitherModeAndHaveStableLayerOrder()
    {
        var host = new UiHost();
        using var mod = host.Register(new Extension("sample", r =>
        {
            r.Contribute(UiSlots.Overlay, _ => new("last"), order: 10);
            r.Contribute(UiSlots.Overlay, _ => new("native"), order: -1, mode: UiMode.Meitou);
            r.Contribute(UiSlots.Overlay, _ => new("shared"));
        }));
        Assert.Equal(new[] { "native", "shared", "last" }, host.Arrange(UiMode.Meitou, 800, 600, new(), Slots()).Select(c => c.Name));
        Assert.Equal(new[] { "shared", "last" }, host.Arrange(UiMode.Faithful, 800, 600, new(), Slots()).Select(c => c.Name));
        Assert.Empty(host.Arrange(UiMode.Meitou, 0, 0, new(), Slots(0, 0)));
    }

    [Fact]
    public void BrokenControlsAndActionsReportDiagnosticsWithoutBreakingOtherModules()
    {
        var host = new UiHost();
        var diagnostics = new List<UiDiagnostic>();
        host.Diagnostic += diagnostics.Add;
        using var broken = host.Register(new Extension("broken", r =>
        {
            r.Contribute(UiSlots.Overlay, _ => throw new InvalidOperationException("layout failed"));
        }));
        using var healthy = host.Register(new Extension("healthy", r =>
        {
            string action = r.RegisterAction("click", _ => throw new InvalidOperationException("action failed"));
            r.Contribute(UiSlots.Overlay, _ => new("button", UiNodeKind.Button) { Action = action });
        }));
        var button = Assert.Single(host.Arrange(UiMode.Meitou, 800, 600, new(), Slots()));
        Assert.Equal("broken", Assert.Single(diagnostics).Extension);
        Assert.True(host.Dispatch(button));
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("healthy", diagnostics[1].Extension);
    }

    [Fact]
    public void DisabledParentsPreventChildActionsAndLabelsDoNotStealClicks()
    {
        var host = new UiHost();
        int clicks = 0;
        using var module = host.Register(new Extension("sample", r =>
        {
            string action = r.RegisterAction("click", _ => clicks++);
            r.Contribute(UiSlots.Overlay, _ =>
            {
                var root = new UiNode("panel") { Enabled = false };
                var button = new UiNode("button", UiNodeKind.Button) { Action = action, Placement = new(100, 30) };
                button.Children.Add(new("label", UiNodeKind.Label) { Text = "Click" });
                root.Children.Add(button);
                return root;
            });
        }));
        var frame = host.Arrange(UiMode.Meitou, 800, 600, new(), Slots());
        var picked = MyGuiPresentation.HitTest(frame, 10, 10)!;
        Assert.Equal("button", picked.Name);
        Assert.False(picked.Enabled);
        Assert.True(host.Dispatch(picked));
        Assert.Equal(0, clicks);
    }

    [Fact]
    public void FlowSkipsHiddenChildrenAndClipsDrawingAndPickingTogether()
    {
        var host = new UiHost();
        using var module = host.Register(new Extension("sample", r => r.Contribute(UiSlots.Overlay, _ =>
        {
            var root = new UiNode("row") { Flow = UiFlow.Row, Gap = 2, Placement = new(16, 16) };
            root.Children.Add(new("first") { Placement = new(8, 8) });
            root.Children.Add(new("hidden") { Visible = false, Placement = new(100, 100) });
            root.Children.Add(new("second") { Placement = new(12, 12) });
            return root;
        })));
        var frame = host.Arrange(UiMode.Meitou, 800, 600, new(), Slots());
        var second = Assert.Single(frame, c => c.Name == "second");
        Assert.Equal(10, second.Bounds.X);
        Assert.Equal(0, second.Bounds.Y);
        Assert.Equal(6, second.Bounds.Width);
        Assert.Equal(12, second.Bounds.Height);
        Assert.Null(MyGuiPresentation.HitTest(frame, 17, 17));
    }
}

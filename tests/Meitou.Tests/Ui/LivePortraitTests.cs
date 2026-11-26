using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Simulation;

namespace Meitou.Tests.Ui;

/// <summary>
/// Live portraits on any widget (docs/modding.md "Screen overrides" and Interface <c>PortraitOf</c>): the cells of the portrait atlas the
/// frame's live portraits share (<see cref="RecordPortraits.AssignLive"/>), and a screen override that turns a faithful screen's own image into one.
/// </summary>
public class LivePortraitTests
{
    sealed class Extension(string id, Action<UiRegistration> register) : IUiExtension
    {
        public string Id => id;
        public void Register(UiRegistration r) => register(r);
    }

    static readonly Dictionary<string, int> Drawn = new(StringComparer.Ordinal) { ["a"] = 0 };

    static List<(string Key, int Cell, bool Own)> Assign(params string[] asked) =>
        RecordPortraits.AssignLive(["a", "b", "c", "d"], asked.ToHashSet(StringComparer.Ordinal), Drawn, next: 3, end: 5);

    [Fact]
    public void Live_portraits_take_a_cell_each_and_free_it_when_no_one_asks()
    {
        Assert.Equal([("a", 0, false), ("b", 3, true), ("c", 4, true)], Assign("b", "c", "a"));
        Assert.Equal([("b", 3, true), ("c", 4, true)], Assign("b", "c", "d"));
        Assert.Equal([("d", 3, true)], Assign("d"));
        Assert.Empty(Assign("gone"));
    }

    [Fact]
    public void A_screen_override_shows_a_live_portrait_in_the_screens_own_image()
    {
        var host = new UiHost();
        var character = new CharacterId(7, 2);
        using var module = host.Register(new Extension("a", r =>
            r.AddScreenOverrides("atlas", "Face*", _ => new UiWidgetOverride { PortraitOf = character })));
        List<UiControl> controls = [Image("FaceLeft"), Image("Logo")];

        var view = new ScreenView("atlas", "atlas", null, null, null, WorldSnapshot.Empty, () => 0, _ => { });
        host.ArrangeScreenControls(view, controls, UiMode.Meitou, 800, 600);

        var face = controls.Single(c => c.Name == "FaceLeft");
        Assert.Equal(character.ToString(), face.Properties[RecordPortraits.LiveProperty]);
        Assert.False(face.Properties.ContainsKey("ImageResource"));
        Assert.Equal(Image("Logo"), controls.Single(c => c.Name == "Logo"), (x, y) => x.Properties.SequenceEqual(y.Properties));
    }

    static UiControl Image(string name) => new(name, "ImageBox", new UiRect(10, 10, 64, 64), "", "", "", true,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["ImageResource"] = "atlas", ["ImageGroup"] = "faces", ["ImageName"] = name },
        Vector4.Zero);
}

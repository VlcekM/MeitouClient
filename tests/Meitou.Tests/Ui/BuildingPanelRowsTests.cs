using Meitou.Game.Ui;
using Meitou.Simulation;

namespace Meitou.Tests.Ui;

/// <summary>
/// The bars code mods add under a selected building's Condition (<see cref="UiRegistration.AddBuildingRows"/>, docs/modding.md "Building panel
/// bars"), asked through <see cref="UiHost.BuildingRows"/> as the building panel asks them.
/// </summary>
public class BuildingPanelRowsTests
{
    sealed class Extension(string id, Action<UiRegistration> register) : IUiExtension
    {
        public string Id => id;
        public void Register(UiRegistration r) => register(r);
    }

    static UiBuildingContext Context() =>
        new(new UiSelectedBuilding("site-1", "rec", "site-1", "Pen", Owned: true), new WorldSnapshot(0, []), () => 0, _ => { });

    [Fact]
    public void BarsComeInRegistrationOrderWithTheirTextsKept()
    {
        var host = new UiHost();
        using var first = host.Register(new Extension("a", r => r.AddBuildingRows(_ => [new UiBuildingRow("Hunger", 0.5f, "5/10", "Fed")])));
        using var second = host.Register(new Extension("b", r => r.AddBuildingRows(_ => [new UiBuildingRow("Water", 1)])));

        Assert.True(host.HasBuildingRows);
        Assert.Equal(
            [new UiBuildingRow("Hunger", 0.5f, "5/10", "Fed"), new UiBuildingRow("Water", 1)],
            host.BuildingRows(Context()));
    }

    [Fact]
    public void FractionsAreClampedAndTextsPutOnOneLineAndCut()
    {
        var host = new UiHost();
        string longName = new('x', BuildingPanelRows.MaxText + 20);
        using var module = host.Register(new Extension("a", r => r.AddBuildingRows(_ =>
        [
            new UiBuildingRow("Over", 2),
            new UiBuildingRow("Under", -1),
            new UiBuildingRow("NaN", float.NaN),
            new UiBuildingRow("Two\nlines", 0.5f, "", "  "),
            new UiBuildingRow(longName, 0.5f),
        ])));

        var rows = host.BuildingRows(Context());

        Assert.Equal([1f, 0f, 0f, 0.5f, 0.5f], rows.Select(r => r.Fraction));
        Assert.Equal("Two lines", rows[3].Name);
        Assert.Null(rows[3].Tooltip);
        Assert.Equal(BuildingPanelRows.MaxText, rows[4].Name.Length);
    }

    [Fact]
    public void AllSourcesTogetherAddAtMostSixBars()
    {
        var host = new UiHost();
        using var first = host.Register(new Extension("a", r => r.AddBuildingRows(_ => Enumerable.Range(0, 4).Select(i => new UiBuildingRow($"a{i}", 0)))));
        using var second = host.Register(new Extension("b", r => r.AddBuildingRows(_ => Enumerable.Range(0, 4).Select(i => new UiBuildingRow($"b{i}", 0)))));

        Assert.Equal(["a0", "a1", "a2", "a3", "b0", "b1"], host.BuildingRows(Context()).Select(r => r.Name));
    }

    [Fact]
    public void ASourceThatThrowsOrNamesNoBarIsReportedOnceAndDropped()
    {
        var host = new UiHost();
        var reports = new List<UiDiagnostic>();
        host.Diagnostic += reports.Add;
        int asked = 0;
        using var broken = host.Register(new Extension("broken", r => r.AddBuildingRows(_ =>
        {
            asked++;
            throw new InvalidOperationException("boom");
        })));
        using var nameless = host.Register(new Extension("nameless", r => r.AddBuildingRows(_ => [new UiBuildingRow(" ", 1)])));
        using var good = host.Register(new Extension("good", r => r.AddBuildingRows(_ => [new UiBuildingRow("Fine", 1)])));

        Assert.Equal(["Fine"], host.BuildingRows(Context()).Select(r => r.Name));
        Assert.Equal(["Fine"], host.BuildingRows(Context()).Select(r => r.Name));

        Assert.Equal(1, asked);
        Assert.Equal(["broken", "nameless"], reports.Select(r => r.Extension));
    }

    [Fact]
    public void AModsBarsGoWhenItIsRemoved()
    {
        var host = new UiHost();
        var module = host.Register(new Extension("a", r => r.AddBuildingRows(_ => [new UiBuildingRow("Hunger", 0.5f)])));

        module.Dispose();

        Assert.False(host.HasBuildingRows);
        Assert.Empty(host.BuildingRows(Context()));
    }
}

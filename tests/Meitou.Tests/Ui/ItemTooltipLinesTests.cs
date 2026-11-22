using Meitou.Game.Ui;

namespace Meitou.Tests.Ui;

public class ItemTooltipLinesTests
{
    sealed class Extension(string id, Action<UiRegistration> register) : IUiExtension
    {
        public string Id => id;
        public void Register(UiRegistration r) => register(r);
    }

    static readonly UiItemTooltipContext Sword = new("katana", "Katana", "dxseba;durability=0.5", 1, 40, 0.4f);

    static IEnumerable<UiItemTooltipLine> Condition(UiItemTooltipContext item) => [new("Condition", item.ModTag.Contains("0.5") ? "50%" : "")];

    static IEnumerable<UiItemTooltipLine> LevelAndBlank(UiItemTooltipContext item) => [new("Level", item.Level.ToString()), new("Note", " ")];

    [Fact]
    public void Sources_answer_from_the_item_in_registration_order_and_disposal_removes_them()
    {
        var host = new UiHost();
        var first = host.Register(new Extension("first", r => r.AddItemTooltipLines(Condition)));
        using var second = host.Register(new Extension("second", r => r.AddItemTooltipLines(LevelAndBlank)));

        var lines = host.ItemTooltipLines(Sword);

        Assert.Equal([new("Condition", "50%"), new("Level", "40"), new UiItemTooltipLine("Note")], lines);
        first.Dispose();
        Assert.Equal(["Level", "Note"], host.ItemTooltipLines(Sword).Select(l => l.Label));
    }

    [Fact]
    public void A_throwing_or_unlabelled_source_is_reported_once_and_dropped_and_lines_are_capped_and_cut()
    {
        var host = new UiHost();
        var reports = new List<UiDiagnostic>();
        host.Diagnostic += reports.Add;
        using var broken = host.Register(new Extension("broken", r => r.AddItemTooltipLines(_ => throw new InvalidOperationException("fault"))));
        using var blank = host.Register(new Extension("blank", r => r.AddItemTooltipLines(_ => [new(" ")])));
        using var many = host.Register(new Extension("many", r => r.AddItemTooltipLines(_ => Enumerable.Range(0, 20)
            .Select(i => new UiItemTooltipLine(new string('x', 200) + i, "line\nbreak")))));

        var lines = host.ItemTooltipLines(Sword);
        host.ItemTooltipLines(Sword);

        Assert.Equal(["broken", "blank"], reports.Select(r => r.Extension));
        Assert.Equal(ItemTooltipLines.MaxLines, lines.Count);
        Assert.All(lines, l => Assert.Equal(ItemTooltipLines.MaxText, l.Label.Length));
        Assert.All(lines, l => Assert.Equal("line break", l.Value));
    }
}

using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.Ui;
using Meitou.Game;
using Meitou.Game.Host;
using Meitou.Game.Host.Input;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Player;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Ui;

public class CharacterPanelLinesTests
{
    sealed class Extension(string id, Action<UiRegistration> register) : IUiExtension
    {
        public string Id => id;
        public void Register(UiRegistration r) => register(r);
    }

    static UiCharacterContext Context(string name)
    {
        using var session = new WorldSession(Vector3.Zero, (-10, -10, 10, 10), (_, _) => 0);
        session.World.Characters.Spawn(new() { Position = Vector3.Zero }, new() { Name = name, IsPlayer = true }, 0);
        session.World.RunTick();
        var snapshot = session.CurrentSnapshot;
        return new UiCharacterContext(snapshot.Characters[0], snapshot, () => 7, _ => { });
    }

    [Fact]
    public void Sources_answer_from_the_character_in_registration_order_and_disposal_removes_them()
    {
        var host = new UiHost();
        int pressed = 0;
        var first = host.Register(new Extension("first", r =>
        {
            r.AddCharacterPanelLines(c => [new("XP", c.Name, Bar: 1.5f), new("Reset", Press: () => pressed++)]);
            r.AddJobPanelButtons(c => [new("All", null, () => pressed += 10)]);
        }));
        using var second = host.Register(new Extension("second", r => r.AddCharacterPanelLines(c => [new("Cap", " ")])));
        var context = Context("Beep");

        var lines = host.CharacterPanelLines(context);
        Assert.Equal(["XP", "Reset", "Cap"], lines.Select(l => l.Label));
        Assert.Equal(("Beep", 1f), (lines[0].Value, lines[0].Bar!.Value));
        Assert.Null(lines[2].Value);
        var press = lines[1].Press!;
        var button = Assert.Single(host.JobPanelButtons(context));
        button.Press();
        press();
        Assert.Equal(11, pressed);

        first.Dispose();
        press();
        Assert.Equal(11, pressed);
        Assert.Equal(["Cap"], host.CharacterPanelLines(context).Select(l => l.Label));
        Assert.False(host.HasJobPanelButtons);
    }

    [Fact]
    public void A_faulty_source_is_reported_once_and_dropped_and_lines_are_capped_and_cut()
    {
        var host = new UiHost();
        var reports = new List<UiDiagnostic>();
        host.Diagnostic += reports.Add;
        using var broken = host.Register(new Extension("broken", r => r.AddCharacterPanelLines(_ => throw new InvalidOperationException("fault"))));
        using var blank = host.Register(new Extension("blank", r => r.AddJobPanelButtons(_ => [new(" ", null, () => { })])));
        using var nan = host.Register(new Extension("nan", r => r.AddCharacterPanelLines(_ => [new("Bar", "x", float.NaN)])));
        using var many = host.Register(new Extension("many", r =>
        {
            r.AddCharacterPanelLines(_ => Enumerable.Range(0, 9).Select(i => new UiCharacterLine(new string('x', 200) + i, "line\nbreak")));
            r.AddJobPanelButtons(_ => Enumerable.Range(0, 9).Select(i => new UiJobButton("Tab" + i, new string('t', 2000), () => { })));
        }));
        var context = Context("Beep");

        var lines = host.CharacterPanelLines(context);
        var buttons = host.JobPanelButtons(context);
        host.CharacterPanelLines(context);
        host.JobPanelButtons(context);

        Assert.Equal(["broken", "nan", "many", "blank", "many"], reports.Select(r => r.Extension));
        Assert.Equal(CharacterPanelHooks.MaxLines, lines.Count);
        Assert.All(lines, l => Assert.Equal((UiHost.MaxTownLineText, "line break"), (l.Label.Length, l.Value)));
        Assert.Equal(CharacterPanelHooks.MaxButtons, buttons.Count);
        Assert.All(buttons, b => Assert.Equal(UiHost.MaxTownLineTooltip, b.Tooltip!.Length));
    }

    [Fact, Slow]
    public void The_hud_places_the_lines_under_the_stats_and_the_buttons_above_the_jobs_list_and_a_press_runs_for_that_character()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "No Kenshi install configured");
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: [new PlayerSystem()]);
        var own = session.World.Characters.Spawn(new() { Position = Vector3.Zero }, new() { Name = "Player", IsPlayer = true }, 0);
        session.World.Player.Faction = 0;
        session.World.Player.Selection.Add(own);
        session.World.RunTick();
        var resources = OgreScriptResources.ForInstall(install, LoadOrder.FromInstall(install));
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position) { Width = 1600, Height = 900 };
        ui.LoadUi(UiMode.Faithful, name => resources.Find(name));
        ui.Frontend.ConfigureFrontend(SyntheticTown.Database(), () => [], "");
        try
        {
            var plainList = ui.UiFrame.Last(c => c.Name == "JobsPanel").Bounds;
            var pressedFor = new List<CharacterId>();
            using var mod = ui.RegisterUiExtension(new Extension("mod", r =>
            {
                r.AddCharacterPanelLines(c => [new("XP", "50%", Bar: 0.5f), new("Raise cap", Press: () => pressedFor.Add(c.Id))]);
                r.AddJobPanelButtons(c => [new("All", null, () => pressedFor.Add(c.Id)), new("Work", "Work jobs", () => { })]);
            }));

            var frame = Fresh(ui);
            var stats = frame.Last(c => c.Name == "StatsText").Bounds;
            var label = frame.Last(c => c.Name == "StatsText-mod-0-label").Bounds;
            var list = frame.Last(c => c.Name == "JobsPanel").Bounds;
            var tab = frame.Last(c => c.Name == "JobsPanel-mod-0").Bounds;
            Assert.True(label.Y >= stats.Y && label.Y + label.Height <= stats.Y + stats.Height);
            Assert.Contains(frame, c => c.Name.StartsWith("StatsText-mod-0-bar-", StringComparison.Ordinal));
            Assert.True(list.Y > plainList.Y && list.Y + list.Height <= plainList.Y + plainList.Height + 1);
            Assert.True(tab.Y >= plainList.Y - 1 && tab.Y + tab.Height <= list.Y + 1);

            Click(ui, "JobsPanel-mod-0");
            Click(ui, "StatsText-mod-1-button");
            Assert.Equal([own, own], pressedFor);

            mod.Dispose();
            frame = Fresh(ui);
            Assert.Equal(plainList, frame.Last(c => c.Name == "JobsPanel").Bounds);
            Assert.DoesNotContain(frame, c => c.Name.StartsWith("StatsText-mod-", StringComparison.Ordinal));
        }
        finally
        {
            ui.DisposeUi();
        }
    }

    /// <summary>
    /// The frame arranged again (the interface reuses its base for a moment while nothing it keys on changes, the pointer included).
    /// </summary>
    static IReadOnlyList<UiControl> Fresh(PlayerInterface ui)
    {
        ui.MouseMove(new(ui.Mouse.X + 1, 1));
        return ui.UiFrame;
    }

    static void Click(PlayerInterface ui, string name)
    {
        var b = ui.UiFrame.Last(c => c.Name == name).Bounds;
        ui.MouseDown(MouseButton.Left, new(b.X + b.Width / 2, b.Y + b.Height / 2), false);
    }
}

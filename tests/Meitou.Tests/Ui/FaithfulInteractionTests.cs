using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Ogre;
using Meitou.Data.Ui;
using Meitou.Engine;
using Meitou.Engine.Input;
using Meitou.Game;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Tests.Simulation;
using Meitou.Ui;

namespace Meitou.Tests.Ui;

public class FaithfulInteractionTests(ITestOutputHelper output)
{
    static PlayerInterface Ui(WorldSession session)
    {
        var install = GameInstall.Locate(); Assert.SkipWhen(install is null, "No Kenshi install configured");
        var resources = OgreScriptResources.ForInstall(install, LoadOrder.FromInstall(install));
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position) { Width = 1600, Height = 900 };
        ui.LoadUi(UiMode.Faithful, name => resources.Find(name));
        ui.ConfigureFrontend(SyntheticTown.Database(), () => [], ""); return ui;
    }
    static Vector2 Centre(UiControl c) => new(c.Bounds.X + c.Bounds.Width / 2, c.Bounds.Y + c.Bounds.Height / 2);
    static void Click(PlayerInterface ui, string name) => ui.MouseDown(MouseButton.Left, Centre(ui.UiFrame.Last(c => c.Name == name)), false);

    [Fact, Slow]
    public void AdvancedOptionsShareTheNewGameScreenAndRemainOperable()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0);
        var ui = Ui(session);
        try
        {
            int height = 720; ui.Width = 1280; ui.Height = height; ui.ShowTitle();
            Click(ui, "NewGameButton");
            float closedY = ui.UiFrame.Last(c => c.Name == "BeginButton").Bounds.Y;
            Click(ui, "AdvancedButton");
            var controls = ui.UiFrame;
            var begin = controls.Last(c => c.Name == "BeginButton");
            var lower = controls.Last(c => c.Name == "advanced-Root");
            Assert.Equal(closedY, begin.Bounds.Y);   // the start window stays where it is, the options panel opens under it (FUN_140918ef0)
            Assert.True(lower.Bounds.Y >= begin.Bounds.Y + begin.Bounds.Height);
            Assert.True(lower.Bounds.Y + lower.Bounds.Height <= height);
            Assert.DoesNotContain(controls, c => c.Name is "advanced-reset" or "advanced-close");
            var help = NewGameHelpCatalog.Read(Path.Combine(GameInstall.Locate()!.Root, "locale/en_GB/LC_MESSAGES/main.pot"));
            for (int i = 0; i < 7; i++)
            {
                foreach (string part in new[] { "label", "track", "less", "more", "value" })
                {
                    ui.MouseMove(Centre(ui.UiFrame.Last(c => c.Name == $"advanced-{part}-{i}")));
                    Assert.NotEmpty(help.EnglishAt(NewGameSettings.Options[i].HelpLine));
                    Assert.Equal(help.TextAt(NewGameSettings.Options[i].HelpLine), ui.UiFrame.Last(c => c.Name == "advanced-help").Caption);
                }
                Click(ui, $"advanced-more-{i}");
                Assert.Equal(1.05f, ui.NewGameOptions[i]);
                var track = ui.UiFrame.Last(c => c.Name == $"advanced-track-{i}");
                ui.MouseDown(MouseButton.Left, Centre(track), false);
                ui.MouseUp(MouseButton.Left, Centre(track), false);
                Assert.InRange(ui.NewGameOptions[i], NewGameSettings.Options[i].Min, NewGameSettings.Options[i].Max);
            }
            ui.MouseMove(Vector2.Zero);   // the last option's help stays, so the pointer can reach its scroll bar
            Assert.Equal(help.TextAt(NewGameSettings.Options[^1].HelpLine), ui.UiFrame.Last(c => c.Name == "advanced-help").Caption);
            float selectedHunger = ui.NewGameOptions[0];
            Click(ui, "AdvancedButton"); // The lower panel must not intercept the parent toggle.
            Assert.DoesNotContain(ui.UiFrame, c => c.Name == "advanced-track-0");
            Click(ui, "AdvancedButton");
            Assert.Equal(selectedHunger, ui.NewGameOptions[0]);
            ui.UiKey(Key.Escape);
            Assert.Contains(ui.UiFrame, c => c.Name == "BeginButton");
            Assert.DoesNotContain(ui.UiFrame, c => c.Name == "advanced-track-0");
            Click(ui, "AdvancedButton");
            MyGuiEvent? request = null; ui.ScreenAction += e => request = e;
            Click(ui, "BeginButton"); Assert.Equal("new-game", request?.Action);
            Assert.Equal(selectedHunger, ui.NewGameOptions[0]);
            Click(ui, "CancelButton");
            Assert.DoesNotContain(ui.UiFrame, c => c.Name == "advanced-track-0");
            Click(ui, "NewGameButton"); Assert.Equal(1, ui.NewGameOptions[0]);
        }
        finally { ui.DisposeUi(); }
    }

    [Fact, Slow]
    public void OriginalTitleNewGameAndExitConfirmationProduceHostRequestsAndPauseTheWorld()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0);
        var ui = Ui(session); MyGuiEvent? request = null; ui.ScreenAction += e => request = e;
        try
        {
            // The Meitou title uses the world and keeps the new-game dialogs and mode switching functional.
            ui.SetUiMode(UiMode.Meitou);
            ui.ShowTitle();
            Assert.True(session.Simulation.RequestedPause);
            Assert.Contains(ui.UiFrame, c => c.Name == "MeitouTitle");
            Assert.DoesNotContain(ui.UiFrame, c => c.Properties.GetValueOrDefault("ImageResource") == "Kenshi_TitleScreen");
            Click(ui, "NewGameButton");
            Assert.Contains(ui.UiFrame, c => c.Name == "BeginButton" && c.Enabled);
            Assert.True(ui.UiKey(Key.Escape));
            Assert.DoesNotContain(ui.UiFrame, c => c.Name == "BeginButton");
            ui.SetUiMode(UiMode.Faithful);
            Assert.Contains(ui.UiFrame, c => c.Properties.GetValueOrDefault("ImageResource") == "Kenshi_TitleScreen");
            ui.CloseFrontend();
            Assert.False(session.Simulation.RequestedPause);
            ui.ShowTitle(); Assert.True(session.Simulation.RequestedPause);
            Click(ui, "NewGameButton"); Assert.Contains(ui.UiFrame, c => c.Name == "BeginButton" && c.Enabled);
            Click(ui, "BeginButton"); Assert.Equal("new-game", request!.Action); Assert.False(session.World.Player.Exists);
            ui.UiKey(Key.Escape); Click(ui, "ExitButton");
            Assert.Contains(ui.UiFrame, c => c.Name == "ButtonA");
            Click(ui, "ButtonB"); Assert.Equal("new-game", request.Action);
            Click(ui, "ExitButton"); Click(ui, "ButtonA"); Assert.Equal("exit", request.Action);
            ui.CloseFrontend(); Assert.False(session.Simulation.RequestedPause);
            // In play the Esc menu's Exit offers the main menu, quit and cancel.
            ui.UiKey(Key.Escape); Click(ui, "ExitGameButton"); Assert.Contains(ui.UiFrame, c => c.Name == "ButtonC" && c.Enabled);
            Click(ui, "ButtonA"); Assert.Equal("main-menu", request.Action);
        }
        finally { ui.DisposeUi(); }
    }

    [Fact, Slow]
    public void PocketDragRunsThroughTheOriginalWindowAndOnlyMovesAtASimulationTick()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: [new PlayerSystem()]);
        var inventory = new Inventory(8, 6);
        var item = new ItemInstance { Record = "item", Name = "Test item", Width = 1, Height = 1 };
        inventory.Items.Add(item);
        var character = session.World.Characters.Spawn(new(), new() { Name = "Player", IsPlayer = true, Inventory = inventory }, 0);
        session.World.Player.Faction = 0; session.World.Player.Selection.Add(character); session.World.RunTick();
        var ui = Ui(session);
        try
        {
            ui.SetUiMode(UiMode.Faithful);
            ui.ShowUiPanel("inventory");
            var source = ui.UiFrame.Single(c => c.Name == "inventory-item-0");
            var slot = ui.UiFrame.Single(c => c.Name == "main").Bounds;
            Assert.Equal(slot.Width / 8, slot.Height / 6, 3);
            Assert.Contains(ui.UiFrame, c => c.Properties.ContainsKey("CaptionOwner") && c.Caption == "PLAYER");
            var target = new Vector2(slot.X + slot.Width * 2.5f / 8, slot.Y + slot.Height * 1.5f / 6);
            ui.MouseDown(MouseButton.Left, Centre(source), false); Assert.True(ui.UiCapturesPointer);
            ui.MouseMove(target); ui.MouseUp(MouseButton.Left, target, false);
            Assert.Equal((0, 0), (item.X, item.Y));
            session.World.RunTick(); Assert.Equal((2, 1), (item.X, item.Y)); Assert.False(ui.UiCapturesPointer);
        }
        finally { ui.DisposeUi(); }
    }

    [Theory, Slow]
    [InlineData(UiMode.Faithful, 1280, 720)]
    [InlineData(UiMode.Meitou, 1600, 900)]
    public void BackpackFitsItsContentsAndEquipmentKeepsItsFootprint(UiMode mode, int width, int height)
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: [new PlayerSystem()]);
        var inventory = new Inventory();
        inventory.Items.Add(new() { Record = "bag", Name = "Thieves Backpack", Section = "backpack_attach", Width = 4, Height = 4, Contents = new Inventory(10, 14) });
        inventory.Items.Add(new() { Record = "sword", Name = "Sword", Section = "back", Width = 7, Height = 1 });
        var character = session.World.Characters.Spawn(new(), new() { Name = "Wolf", IsPlayer = true, Inventory = inventory }, 0);
        session.World.Player.Faction = 0; session.World.Player.Selection.Add(character); session.World.RunTick();
        var ui = Ui(session);
        try
        {
            ui.SetUiMode(mode); ui.Width = width; ui.Height = height;
            ui.ShowUiPanel("inventory");
            var main = ui.UiFrame.Single(c => c.Name == "main").Bounds;
            Assert.Equal(8, inventory.Width); Assert.Equal(10, inventory.Height);
            Assert.Equal(main.Width / 8, main.Height / 10, 3);
            var sword = ui.UiFrame.Single(c => c.Name == "inventory-item-1").Bounds;
            Assert.Equal(7, sword.Width / sword.Height, 3);
            Assert.Equal("normal", ui.UiFrame.Single(c => c.Name == "shirt").Properties["State"]);
            Click(ui, "OpenBagButton");
            var bag = ui.UiFrame.Single(c => c.Name == "backpack_content");
            Assert.Equal(bag.Bounds.Width / 10, bag.Bounds.Height / 14, 3);
            Assert.True(bag.Bounds.X + bag.Bounds.Width <= ui.UiFrame.Single(c => c.Name == "backpack_attach").Bounds.X);
            Assert.Contains(ui.UiFrame, c => c.Properties.ContainsKey("CaptionOwner") && c.Caption == "THIEVES BACKPACK");
            Assert.DoesNotContain(ui.UiFrame, c => c.Name is "CapacityText" or "TypeText");
            Assert.DoesNotContain(ui.UiFrame, c => c.Name == "inventory-scroll-thumb");
            Click(ui, "OpenBagButton");
            Assert.DoesNotContain(ui.UiFrame, c => c.Name == "backpack_content");
            Assert.Contains(ui.UiFrame, c => c.Name == "main");
        }
        finally { ui.DisposeUi(); }
    }

    [Fact, Slow]
    public void HoldingTheCommandButtonOnACharacterOpensTheContextMenuAndItsVerbGivesTheOrder()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: [new PlayerSystem()]);
        var own = session.World.Characters.Spawn(new() { Position = new(-3, 0, 0) }, new() { Name = "Player", IsPlayer = true }, 0);
        var other = session.World.Characters.Spawn(new() { Position = new(3, 0, 0) }, new() { Name = "Stranger" }, 0);
        session.World.Player.Faction = 0; session.World.Player.Selection.Add(own); session.World.RunTick();
        var ui = Ui(session);
        try
        {
            var eye = new Vector3(0, 6, -14);
            ui.Eye = eye;
            ui.ViewProjection = Matrix4x4.CreateLookAt(eye, new(0, 1, 0), Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(1, 1600 / 900f, 0.1f, 1000);
            var at = ui.Project(new Vector3(3, 1, 0))!.Value;
            ui.MouseDown(MouseButton.Right, at, false);
            ui.Steer(); Assert.False(ui.ContextMenuOpen);   // not held long enough yet
            Thread.Sleep(TimeSpan.FromSeconds(PlayerInterface.ContextHoldSeconds + 0.05));
            ui.Steer(); Assert.True(ui.ContextMenuOpen);
            Assert.Contains(ui.UiFrame, c => c.Name == "context-title" && c.Caption == "Stranger");
            Assert.Equal(0, session.World.Commands.Count);   // the hold gave no quick order
            // The rows act on the release (the original's eventMouseButtonReleased): the held button let go over the first verb gives it and closes the menu.
            var verb = Centre(ui.UiFrame.Last(c => c.Name == "context-verb-0"));
            ui.MouseMove(verb);
            ui.MouseUp(MouseButton.Right, verb, false);
            Assert.False(ui.ContextMenuOpen);
            Assert.Contains(session.World.Commands.TakeDue(long.MaxValue), c => c is Meitou.Simulation.Combat.AttackOrder a && a.Target == other);
        }
        finally { ui.DisposeUi(); }
    }

    [Fact, Slow]
    public void TheSquadBarShowsEachMembersPortraitCellAndAClickOnItSelectsThem()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: [new PlayerSystem()]);
        var first = session.World.Characters.Spawn(new() { Position = new(-3, 0, 0) }, new() { Name = "First", IsPlayer = true }, 0);
        var second = session.World.Characters.Spawn(new() { Position = new(3, 0, 0) }, new() { Name = "Second", IsPlayer = true }, 0);
        session.World.Player.Faction = 0; session.World.Player.Selection.Add(first); session.World.RunTick();
        var ui = Ui(session);
        try
        {
            Assert.Equal([first, second], ui.SquadBarMembers().Select(c => c.Id));
            var frame = ui.UiFrame;
            // The renderer draws member i into atlas cell i (CharacterRenderer.PortraitRequests): the second cell starts 256 px in, the original's 128 px portrait in its corner.
            var portrait = Assert.Single(frame, c => c.Name == "portrait-1");
            Assert.Equal(PlayerInterface.PortraitTexture, portrait.Properties["ImageTexture"]);
            Assert.Equal("256 0 128 128", portrait.Properties["ImageRect"]);
            Assert.Contains(frame, c => c.Name == "portrait-name-1" && c.Caption == "Second");
            Click(ui, "member-1");
            session.World.RunTick();
            Assert.Equal([second], session.World.Player.Selection);
        }
        finally { ui.DisposeUi(); }
    }

    /// <summary>The HUD's medical word thresholds, body part rows and stats line format (docs/game/ui-screens.md section 4).</summary>
    [Fact]
    public void HudTextFollowsTheOriginalsRules()
    {
        Assert.Equal("Normal", PlayerInterface.HealthWord(new BodyStatus(1, 0.9f, 3, false, false)).Text);
        Assert.Equal("Slightly Injured", PlayerInterface.HealthWord(new BodyStatus(1, 0.77f, 2.2f, false, false)).Text);
        Assert.Equal("Hungry", PlayerInterface.HealthWord(new BodyStatus(1, 1, 1.5f, false, false)).Text);
        Assert.Equal("Wounded", PlayerInterface.HealthWord(new BodyStatus(1, 0.5f, 3, false, false)).Text);
        Assert.Equal("Starving", PlayerInterface.HealthWord(new BodyStatus(1, 1, 0.5f, false, false)).Text);
        // The race lists Right Leg, Left Arm, Left Leg, Head, Stomach, Right Arm, Chest; the panel shows head, torso, arms, legs, left before right.
        (string Name, BodyPartType Type, int Collapse)[] race = [("Right Leg", BodyPartType.Leg, 0x10), ("Left Arm", BodyPartType.Arm, 4), ("Left Leg", BodyPartType.Leg, 0x20),
            ("Head", BodyPartType.Head, 1), ("Stomach", BodyPartType.Torso, 1), ("Right Arm", BodyPartType.Arm, 2), ("Chest", BodyPartType.Torso, 1)];
        var parts = race.Select(p => new BodyPartTemplate { StringId = p.Name, Name = p.Name, Type = p.Type, CollapsePart = p.Collapse });
        Assert.Equal(["Head", "Stomach", "Chest", "Left Arm", "Right Arm", "Left Leg", "Right Leg"], parts.OrderBy(p => p.MedicalOrder).Select(p => p.Name));
        var lead = new CharacterSnapshot(new CharacterId(0, 0), null, Vector3.Zero, 0, [])
            { Hud = new HudView(150, 150, 0, 1, [new("Strength:", 99.4f, 100), new("Toughness:", 100, 100), new("Dodge:", 105, 100)]) };
        Assert.Equal(["STRENGTH: 99 (-1)", "TOUGHNESS: 100", "DODGE: 105 (+5)"], PlayerInterface.StatsLines(lead).Select(l => $"{l.Label} {l.Value}"));
        var extended = lead;
        extended.Hud = lead.Hud! with { Extended = new ExtendedStats(1.5f, 0.25f, 3, -37.6f, 30, 40, 1.35f) };
        Assert.Equal(["Athletics XP: 150%", "Strength XP: 25%", "Toughness XP: 3x", "KO point: -37", "Run speed: 7 (-2) mph", "Hunger Rate: 135%"],
            PlayerInterface.ExtendedLines(extended).Select(l => $"{l.Label} {l.Value}"));
    }

    /// <summary>A click on another faction's character shows it in the HUD without selecting it (<see cref="InspectCommand"/>).</summary>
    [Fact]
    public void InspectingACharacterPublishesItsHudDetailsWithoutSelectingIt()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: [new PlayerSystem()]);
        var stranger = session.World.Characters.Spawn(new(), new() { Name = "Stranger", Faction = 3 }, 0);
        session.World.Player.Faction = 0;
        session.World.Commands.Enqueue(new InspectCommand(stranger) { Tick = session.World.Tick });
        session.World.RunTick();
        var shown = Assert.Single(session.CurrentSnapshot.Characters);
        Assert.True(shown.Inspected);
        Assert.False(shown.Selected);
        session.World.Commands.Enqueue(new InspectCommand(CharacterId.None) { Tick = session.World.Tick });
        session.World.RunTick();
        Assert.False(Assert.Single(session.CurrentSnapshot.Characters).Inspected);
    }

    /// <summary>Names the HUD bindings carry for the Meitou mode's native HUD only; the faithful layout has no widget for them.</summary>
    static readonly HashSet<string> NativeOnly = ["clock", "hunger", "condition", "blood", "body", "bloodFraction", "bodyFraction", "hungerFraction", "squad.count", "members"];

    [Fact, Slow]
    public void EveryWidgetTheFaithfulModeFillsExistsInTheOriginalLayout()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: [new PlayerSystem()]);
        var character = session.World.Characters.Spawn(new(), new() { Name = "Player", IsPlayer = true, Inventory = new Inventory(8, 6) }, 0);
        session.World.Player.Faction = 0; session.World.Player.Selection.Add(character); session.World.RunTick();
        var ui = Ui(session);
        var missing = new List<string>();
        try
        {
            foreach (string id in PlayerInterface.FaithfulScreenFiles.Select(f => f.Id).Prepend("hud"))
            {
                var (layout, b) = ui.FaithfulBindings(id);
                var names = layout.Widgets.Select(w => w.Name).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
                bool Native(string n) => id == "hud" && (NativeOnly.Contains(n) || n.StartsWith("squad.", StringComparison.Ordinal)) || n.IndexOf("@tab", StringComparison.Ordinal) is > 0 and int at && names.Contains(n[..at]);
                // What the screen shows or answers to must land on a real widget; hiding or styling an absent one is only reported.
                foreach (string n in b.Text.Keys.Concat(b.Actions.Keys).Concat(b.Lists.Keys).Concat(b.Progress.Keys).Distinct())
                    if (!names.Contains(n) && !Native(n)) missing.Add($"{id}: {n}");
                foreach (string n in b.Hidden.Concat(b.Disabled).Concat(b.Properties.Keys).Distinct().Where(n => !names.Contains(n) && !Native(n)))
                    output.WriteLine($"{id}: hides or styles absent widget {n}");
                // Named widgets with an editor caption that the code neither fills nor hides keep showing the layout's sample text.
                foreach (var w in layout.Widgets.Where(w => w.Name.Length > 0 && w.Properties.TryGetValue("Caption", out string? c) && c.Length > 0))
                    if (!b.Text.ContainsKey(w.Name) && !b.Hidden.Contains(w.Name))
                        output.WriteLine($"{id}: {w.Type} {w.Name} keeps its caption \"{w.Properties["Caption"]}\"");
            }
        }
        finally { ui.DisposeUi(); }
        Assert.True(missing.Count == 0, "bound to widgets the original layouts lack: " + string.Join(", ", missing));
    }
}

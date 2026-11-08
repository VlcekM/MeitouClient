using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace Meitou.Game.Title;

/// <summary>The front-end windows (docs/game/ui-screens.md): title, new game, load, save, import, credits and the message box.</summary>
sealed class FrontendScreens(PlayerInterface player)
{
    IReadOnlyList<NewGameStart> starts = [];
    int startIndex;
    Func<IReadOnlyList<SaveSlot>> saveSlots = () => [];
    internal IReadOnlyList<SaveSlot> slots = [];
    internal SaveSlot? selectedSlot;
    string slotDetails = "", credits = "";
    internal bool showCredits;
    /// <summary>When the credits were opened (Stopwatch): they scroll by the wall clock.</summary>
    long creditsStart;
    bool slotReadable;
    /// <summary>The Faithful load window's Load button is live only with a readable save selected (Fixes settings); off, it is always live, as in the game.</summary>
    public bool FixLoadButton { get; set; } = true;
    Meitou.Data.Save.SaveSlotInfo? slotInfo;
    /// <summary>The load and import windows' ticks (ImportGameMenu flags 0x1, 0x4, 0x8, 0x10, 0x20): reset squad positions, buildings, research, dead NPCs, relations.</summary>
    readonly bool[] slotTicks = new bool[5];
    static readonly string[] TickBoxes = ["ResetTickBox", "BuildingsTickBox", "ResearchTickBox", "NPCTickBox", "RelationsTickBox"];
    /// <summary>Meitou's load window tick that leaves the base game's saves out of the list (<see cref="WithSaveFilter"/>).</summary>
    const string HideKenshiTick = "HideKenshiTickBox";
    bool hideKenshiSaves;
    /// <summary>The saves the window lists: the save window only Meitou's, the load window without Kenshi's when its tick is set.</summary>
    SaveSlot[] VisibleSlots(string id) => [.. slots.Where(s => s.Writable || id != "save" && !(id == "load" && hideKenshiSaves && player.UiMode == UiMode.Meitou))];
    /// <summary>The import window's ticks as the flag bits of <c>importGame</c> (ticks in the order of <see cref="TickBoxes"/>: 0x1, 0x4, 0x8, 0x10, 0x20).</summary>
    public Meitou.Simulation.Saving.ImportOptions ImportFlags =>
        (slotTicks[0] ? Meitou.Simulation.Saving.ImportOptions.ResetPositions : 0) | (slotTicks[1] ? Meitou.Simulation.Saving.ImportOptions.Buildings : 0)
        | (slotTicks[2] ? Meitou.Simulation.Saving.ImportOptions.Research : 0) | (slotTicks[3] ? Meitou.Simulation.Saving.ImportOptions.DeadNpcs : 0)
        | (slotTicks[4] ? Meitou.Simulation.Saving.ImportOptions.Relations : 0);
    /// <summary>The open message box: ButtonA confirms (captioned <c>Yes</c>), an optional middle choice (ButtonB), and the last button cancels (Esc presses it).</summary>
    internal sealed record Confirmation(string Title, string Text, Action Confirm, string Yes = "YES", (string Caption, Action Act)? Middle = null, string Cancel = "CANCEL")
    {
        /// <summary>Only ButtonA, captioned Ok (the original's message box with button mask 1, <c>FUN_140742130</c>).</summary>
        public bool OkOnly { get; init; }
    }
    internal Confirmation? confirmation;
    /// <summary>The game data (<see cref="ConfigureFrontend"/>; null without it).</summary>
    internal GameDatabase? gameData;
    public void ConfigureFrontend(GameDatabase? database, Func<IReadOnlyList<SaveSlot>> enumerate, string creditsText)
    {
        gameData = database;
        starts =database is null ? [] : NewGameStart.LoadAll(database);
        startIndex = Math.Max(0, starts.ToList().FindIndex(s => s.Name == NewGameStart.DefaultName));
        saveSlots = enumerate;
        // As the title reads data/credits.txt (FUN_14013d680, docs/game/ui-screens.md "Credits"): empty lines dropped, a "-" line a blank one, each line ended.
        credits = string.Concat(creditsText.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Select(l => (l == "-" ? "" : l) + "\n"));
    }
    public void ShowTitle() { player.faithfulScreens.Open("title"); RefreshSlots(); player.UpdateMenuPause(); }
    /// <summary>After leaving a game: title controls over the original image or the live world, according to UI mode.</summary>
    public void ShowMainMenu() => ShowTitle();
    public void CloseFrontend()
    {
        foreach (string id in new[] { "title", "menu", "new-game", "save", "load", "import", "message", "editor", "file" }) player.faithfulScreens.Close(id);
        player.characterEditor.editor = null; player.characterEditor.previews.Clear();
        confirmation = null; player.UpdateMenuPause();
    }
    /// <summary>The cancel button of the open message box: ButtonC when it has a middle choice, else ButtonB.</summary>
    internal string CancelButton => confirmation?.Middle is null ? "ButtonB" : "ButtonC";
    void RefreshSlots()
    {
        try { slots = saveSlots(); selectedSlot = slots.FirstOrDefault(); ReadSlotDetails(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { slots = []; selectedSlot = null; slotDetails = e.Message; }
    }
    /// <summary>
    /// The selected save is older than 0.90.0 (<see cref="Meitou.Simulation.Saving.SaveImport.AllowedFor"/>): the import window's row selection (<c>FUN_14047cee0</c>) unticks and disables Reset squad
    /// positions, Buildings, Research and Dead NPCs, leaving Relations.
    /// </summary>
    bool OldImportSave => slotInfo is { } info && Meitou.Simulation.Saving.SaveImport.AllowedFor(Meitou.Simulation.Saving.ImportOptions.ResetPositions, info.Version) == 0;

    void ReadSlotDetails()
    {
        slotReadable = false;
        slotInfo = null;
        if (selectedSlot is not { } slot) { slotDetails = "No saves found."; return; }
        try
        {
            var info = SaveCatalog.ReadInfo(slot);
            slotReadable = true;
            slotInfo = info;
            // The five labels of the original info panel (FUN_14047e390 / FUN_14047e780, docs/game/ui-screens.md section 3), in their order.
            slotDetails = $"File version: {info.Version}\nFaction name: {info.Faction}\nFunds: c.{info.Funds.ToString("N0", CultureInfo.InvariantCulture)}\nMembers size: {info.Members}\nLocation: {info.Area}";
        }
        catch (Exception e) when (e is IOException or FormatException or ArgumentException) { slotDetails = "Unable to read save:\n" + e.Message; }
    }
    /// <summary>The advanced options of the import window take the selected save's difficulty (<c>FUN_140912f80</c>, when the window is opened or a row selected while it is open); the import runs with what the sliders hold.</summary>
    void ReadSlotDifficulty()
    {
        if (selectedSlot is not { } slot) return;
        try { player.advancedOptions.NewGameOptions = NewGameSettings.Read(new SaveGame(SaveFile.Read(File.ReadAllBytes(Path.Combine(slot.Folder, SaveFolder.QuickSaveName)))).Camera); }
        catch (Exception e) when (e is IOException or FormatException or ArgumentException or Meitou.Data.Fcs.FcsFormatException) { /* an unreadable save keeps the sliders as they are */ }
    }
    /// <summary>Whether a save folder is the one the game in play came from or was last saved to (the host's).</summary>
    public Func<string, bool>? SaveInUse { get; set; }
    /// <summary>Deletes a save folder (the host's); returns an error to show, or null.</summary>
    public Func<string, string?>? DeleteSave { get; set; }

    /// <summary>The load window's Delete / Backspace (<b>Verified</b>, <c>LoadSaveWindow::keyPressed</c>): the save in use cannot be deleted; any other asks first.</summary>
    internal void AskDeleteSave(SaveSlot slot)
    {
        if (SaveInUse?.Invoke(slot.Folder) == true) { player.Confirm("Delete Save", "This save file is currently in use and cannot be deleted", () => { }, "OK"); return; }
        player.Confirm("Delete Save", "Are you sure you want to delete this save game?", () =>
        {
            if (DeleteSave?.Invoke(slot.Folder) is { } error) player.Notice(error, true);
            RefreshSlots();
            selectedSlot = null; slotInfo = null;
        });
    }

    internal void OpenSlots(string id)
    {
        RefreshSlots();
        if (id == "save" || id == "load" && player.UiMode == UiMode.Meitou) { selectedSlot = VisibleSlots(id).FirstOrDefault(); ReadSlotDetails(); }
        // The original's windows open with nothing selected and the info box empty (LoadSaveWindow, FUN_140480100), the ticks as their layout has them.
        else if (player.UiMode == UiMode.Faithful) { selectedSlot = null; slotInfo = null; }
        Array.Copy(new[] { false, true, true, true, true }, slotTicks, slotTicks.Length);
        if (id == "import" && OldImportSave) Array.Fill(slotTicks, false, 0, 4);
        player.faithfulScreens.Open(id); if (id == "save") player.faithfulScreens.SetText(id, "SaveEditBox", selectedSlot?.Name ?? "");   // the selected save's name, ready to overwrite
    }
    internal bool HandleFrontendAction(MyGuiEvent e)
    {
        if (e.Screen is "title" or "menu")
        {
            // The title's buttons first close its other windows and the credits (FUN_140914a90); Load and Import close their own window when it is open,
            // New game leaves its window as it is, Credits starts them again (docs/game/ui-screens.md "Title screen").
            bool title = e.Screen == "title";
            bool wasOpen = player.faithfulScreens.IsOpen(e.Widget switch { "LoadGameButton" => "load", "ImportGameButton" => "import", "NewGameButton" => "new-game", _ => "" });
            if (title && !(e.Widget == "NewGameButton" && wasOpen)) CloseTitleWindows();
            switch (e.Widget)
            {
                case "ResumeButton": player.faithfulScreens.Close("menu"); break;
                case "NewGameButton": if (!wasOpen) { player.advancedOptions.advancedOpen = false; player.faithfulScreens.Open("new-game"); } break;
                case "SaveGameButton": OpenSlots("save"); break;
                case "LoadGameButton": if (!(title && wasOpen)) OpenSlots("load"); break;
                case "ImportGameButton": if (!wasOpen) { player.advancedOptions.advancedOpen = false; OpenSlots("import"); } break;
                case "OptionsButton": player.RaiseScreenAction(e); break;
                case "CreditsButton": showCredits = true; creditsStart = System.Diagnostics.Stopwatch.GetTimestamp(); break;
                case "ContinueButton": if (slots.FirstOrDefault() is { } last) player.RaiseScreenAction(new("load", "LoadButton", "load", last.Folder)); break;
                case "ExitButton": player.Confirm("EXIT", "Are you sure you want to exit?", () => player.RaiseScreenAction(new("menu", "ExitGameButton", "exit"))); break;
                // In play the Esc menu's Exit offers the main menu as well (engine choice: the original's in-game Exit dialog is not traced).
                case "ExitGameButton":
                    player.Confirm("EXIT", player.session.World.Player.Exists ? "Return to the main menu or quit the game? Unsaved progress will be lost." : "Return to the main menu or quit the game?",
                        () => player.RaiseScreenAction(new("menu", "MainMenuButton", "main-menu")), "MAIN MENU", ("QUIT", () => player.RaiseScreenAction(new("menu", "ExitGameButton", "exit"))));
                    break;
            }
            return true;
        }
        if (e.Screen == "message")
        {
            var confirm = confirmation; confirmation = null; player.faithfulScreens.Close("message");
            if (e.Widget == "ButtonA") confirm?.Confirm();
            else if (e.Widget == "ButtonB" && confirm?.Middle is { } middle) middle.Act();
            return true;
        }
        if (e.Screen == "new-game")
        {
            if (player.advancedOptions.AdvancedAction(e)) return true;
            if (e.Widget == "CancelButton") player.faithfulScreens.Close(e.Screen);
            else if (e.Widget == "LeftButton" && starts.Count > 0) startIndex = (startIndex + starts.Count - 1) % starts.Count;
            else if (e.Widget == "RightButton" && starts.Count > 0) startIndex = (startIndex + 1) % starts.Count;
            else if (e.Widget == "AdvancedButton") { player.advancedOptions.advancedOpen = !player.advancedOptions.advancedOpen; player.advancedOptions.advancedHelp = -1; }
            else if (e.Widget == "BeginButton" && starts.Count > 0)
            {
                void Start() => player.RaiseScreenAction(new("new-game", "BeginButton", "new-game", starts[startIndex].Id));
                if (player.session.World.Player.Exists) player.Confirm("NEW GAME", "Start a new game? Unsaved progress will be lost.", Start); else Start();
            }
            return true;
        }
        if (e.Screen is "save" or "load" or "import")
        {
            if (e.Screen == "import" && player.advancedOptions.AdvancedAction(e)) return true;
            if (e.Widget == "CancelButton") { player.faithfulScreens.Close(e.Screen); player.advancedOptions.advancedOpen = false; }
            else if (Array.IndexOf(TickBoxes, e.Widget) is var tick and >= 0) { if (!(e.Screen == "import" && tick < 4 && OldImportSave)) slotTicks[tick] = !slotTicks[tick]; }
            else if (e.Widget == HideKenshiTick)
            {
                hideKenshiSaves = !hideKenshiSaves;
                if (selectedSlot is not null && !VisibleSlots(e.Screen).Contains(selectedSlot)) { selectedSlot = null; ReadSlotDetails(); }
            }
            else if (e.Widget == "AdvancedButton") { player.advancedOptions.advancedOpen = !player.advancedOptions.advancedOpen; player.advancedOptions.advancedHelp = -1; if (player.advancedOptions.advancedOpen && e.Screen == "import") ReadSlotDifficulty(); }
            else if (e.Widget == "ImportButton" && selectedSlot is { } importSlot)
            {
                void Import() => player.RaiseScreenAction(new("import", "ImportButton", "import", importSlot.Folder));
                if (!slotReadable) player.Notice("This save file is invalid", true);   // the original's message box for a save whose row reads ERROR
                else if (player.session.World.Player.Exists) player.Confirm("IMPORT GAME", "Import this save into a new game? Unsaved progress will be lost.", Import); else Import();
            }
            else if (e.Widget == "GamesList" && e.Action == "select")
            {
                selectedSlot = slots.FirstOrDefault(s => s.Folder == e.Text); ReadSlotDetails();
                if (e.Screen == "import" && player.advancedOptions.advancedOpen) ReadSlotDifficulty();
                if (e.Screen == "import" && OldImportSave) Array.Fill(slotTicks, false, 0, 4);
                if (e.Screen == "save" && selectedSlot is { } slot) player.faithfulScreens.SetText("save", "SaveEditBox", slot.Name);
            }
            else if (e.Widget == "LoadButton" && selectedSlot is { } loadSlot)
            {
                void Load() => player.RaiseScreenAction(new("load", "LoadButton", "load", loadSlot.Folder));
                if (player.session.World.Player.Exists) player.Confirm("LOAD GAME", "Load this save? Unsaved progress will be lost.", Load); else Load();
            }
            else if (e.Widget == "SaveButton")
            {
                string name = player.faithfulScreens.GetText("save", "SaveEditBox");
                try { SaveCatalog.ValidateName(name); }
                catch (ArgumentException ex) { player.Notice(ex.Message, true); return true; }
                void Save() { player.RaiseScreenAction(new("save", "SaveButton", "save", name)); player.faithfulScreens.Close("save"); }
                if (slots.Any(s => s.Writable && string.Equals(s.Name, name, StringComparison.Ordinal))) player.Confirm("OVERWRITE SAVE", Loc.Tr("Overwrite save '{1}'?", name), Save); else Save();
            }
            return true;
        }
        return false;
    }
    /// <summary>The title's other windows and the credits closed (the original's FUN_140914a90, run by every title button).</summary>
    void CloseTitleWindows()
    {
        foreach (string id in new[] { "new-game", "load", "import" }) player.faithfulScreens.Close(id);
        player.advancedOptions.advancedOpen = false;
        showCredits = false;
    }

    /// <summary>A caption the original sets from the catalog in upper case (boost::locale's to_upper of the translation).</summary>
    internal static string UpperCaption(string id) => Loc.Tr(id).ToUpperInvariant();

    /// <summary>The save list's date: the save folder's last write, in the user's short date and time with seconds (boost::locale <c>{1,datetime}</c>, Faithful).</summary>
    string SlotDate(SaveSlot s) => player.UiMode == UiMode.Faithful ? s.ModifiedUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)
        : s.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    internal bool BindFrontend(string id, UiBindings b)
    {
        void Enable(params string[] names) { foreach (string name in names) { b.Disabled.Remove(name); b.Actions[name] = name; } }
        void Paragraph(string name, string text) { b.Text[name] = text; b.Properties[name] = new Dictionary<string, string> { ["WordWrap"] = "true", ["TextAlign"] = "Left Top", ["TextColour"] = MyGuiColours.TemplateInk(player.uiResources, "Kenshi_GenericTextBox"), ["TextPadding"] = name == "InfoPanel" ? "8 8 8 8" : "12 10 12 10" }; }
        if (id is "title" or "menu")
        {
            Enable("NewGameButton", "LoadGameButton", "OptionsButton");
            if (starts.Count == 0) b.Disabled.Add("NewGameButton");
            if (id == "menu")
            {
                Enable("ExitGameButton", "ResumeButton");
                if (player.session.World.Player.Exists) Enable("SaveGameButton");
                return true;
            }
            Enable("ExitButton", "CreditsButton", "ImportGameButton");
            if (player.UiMode == UiMode.Faithful)
                foreach (var (widget, caption) in TitleMenu.TitleCaptions) b.Text[widget] = UpperCaption(caption);
            if (player.UiMode == UiMode.Meitou)
            {
                foreach (var (widget, at) in player.titleMenu.MenuLayout(card: slots.Count > 0 && slotReadable)) b.Positions[widget] = at;
            }
            // Continue is shown only when a save exists (docs/game/ui-screens.md section 3), not greyed out.
            if (slots.Count > 0 && slotReadable) Enable("ContinueButton"); else b.Hidden.Add("ContinueButton");
            b.Properties["CreditsPanel"] = new Dictionary<string, string> { ["Visible"] = showCredits ? "true" : "false" };
            if (showCredits)
            {
                // From 0.85 of the panel's height, 25 pixels up a second of wall-clock time (FUN_140914230, FUN_140913880); the text goes on out of view.
                var layout = player.faithfulScreens.Layout("title");
                var panel = player.characterEditor.RealBounds(layout, "CreditsPanel");
                var view = player.characterEditor.RealBounds(layout, "CreditsPanelContent");
                double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(creditsStart).TotalSeconds;
                float scroll = MathF.Round((float)(panel.Height * 0.85 - seconds * 25), MidpointRounding.AwayFromZero);
                b.Positions["CreditsPanelContent"] = view with { Y = view.Y + scroll, Height = Math.Max(view.Height, credits.Count(c => c == '\n') * 40 * player.Height / 1080f + 100) };
                b.Text["CreditsPanelContent"] = credits;
            }
            b.Text["VersionText"] = player.UiMode == UiMode.Meitou ? "Meitou" : "Meitou - Faithful UI";
        }
        else if (id == "new-game")
        {
            player.advancedOptions.BindNewGamePanels(b);
            Enable("CancelButton", "AdvancedButton");
            b.Text["lbChooseBeginning"] = UpperCaption("Choose your beginning");
            b.Text["AdvancedButton"] = UpperCaption("Advanced options");
            b.Text["BeginButton"] = UpperCaption("Begin");
            b.Text["CancelButton"] = UpperCaption("Cancel");
            b.Text["DescriptionPanel"] = "";
            if (starts.Count > 0)
            {
                Enable("LeftButton", "RightButton", "BeginButton"); var start = starts[startIndex];
                b.Text["TitleTextBox"] = start.Name;
            }
        }
        else if (id is "save" or "load" or "import")
        {
            Enable("CancelButton");
            // The original scales its templates' pixel sizes with the screen (Observed: the list's 21-pixel HeightLine is 28 pixels apart at 1440 lines).
            b.TemplateScale = player.Height / 1080f;
            PlaceSlotWindow(id, b);
            b.Text["lbTitle"] = UpperCaption(id switch { "save" => "Save game", "import" => "Import game", _ => "Load game" });
            b.Text["CancelButton"] = UpperCaption("Cancel");
            b.Text[id == "save" ? "SaveButton" : id == "import" ? "ImportButton" : "LoadButton"] = UpperCaption(id switch { "save" => "Save", "import" => "Import", _ => "Load" });
            if (id != "save") b.Text["lbResetSquadPosition"] = Loc.Tr("Reset squad positions");
            if (id == "import")
            {
                b.Text["AdvancedButton"] = UpperCaption("Advanced options");
                foreach (var (label, caption) in new[] { ("lbImportBuildings", "Import Buildings"), ("lbImportResearch", "Import Research"), ("lbImportNPC", "Import Dead NPCs"), ("lbImportRelations", "Import Relations") })
                    b.Text[label] = Loc.Tr(caption);
                Enable("AdvancedButton");
                if (selectedSlot is not null) Enable("ImportButton"); else b.Disabled.Add("ImportButton");
            }
            // The ticks each window has: load the reset one, import all five, save none.
            for (int t = 0; t < (id == "import" ? TickBoxes.Length : id == "load" ? 1 : 0); t++)
            {
                if (id == "import" && t < 4 && OldImportSave) b.Disabled.Add(TickBoxes[t]); else Enable(TickBoxes[t]);
                b.Properties[TickBoxes[t]] = new Dictionary<string, string> { ["StateSelected"] = slotTicks[t] ? "true" : "false", ["Checked"] = slotTicks[t] ? "true" : "false" };
            }
            if (id == "load" && player.UiMode == UiMode.Meitou)
            {
                Enable(HideKenshiTick);
                b.Properties[HideKenshiTick] = new Dictionary<string, string> { ["StateSelected"] = hideKenshiSaves ? "true" : "false", ["Checked"] = hideKenshiSaves ? "true" : "false" };
            }
            else if (id == "load") { b.Hidden.Add(HideKenshiTick); b.Hidden.Add("lbHideKenshiSaves"); }
            var visibleSlots = VisibleSlots(id);
            // The original's save list (LoadSaveWindow, decompile of FUN_140480100, Observed): two columns, the first filling the width, the second fixed at 180; the captions are localised strings
            // (the pot has "Name" and "Date", Unknown which the window uses). Initially sorted by the second, newest first. Meitou adds the save's origin to the name.
            // addColumn takes screen pixels, not the template's (the date is cut off at 1440 lines in the base game).
            b.Columns["GamesList"] = [new("Name", 170 / b.TemplateScale, Fill: true), new("Date", 180 / b.TemplateScale)];
            b.Sorts["GamesList"] = (1, false);
            // Faithful: the folder name as the original shows it; Meitou adds where the save comes from.
            bool faithful = player.UiMode == UiMode.Faithful;
            b.Lists["GamesList"] = visibleSlots.Select(s => new UiListItem(s.Folder, $"{s.Name}   {SlotDate(s)}",
                [faithful ? s.Name : $"{s.Name}   [{(s.Writable ? "Meitou" : "Kenshi")}]", SlotDate(s)],
                // The original's comparator (FUN_14047cdb0): names without regard to case, dates by their time.
                [s.Name.ToLowerInvariant(), s.ModifiedUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)])).ToArray();
            b.SelectedItems["GamesList"] = Array.FindIndex(visibleSlots, s => s == selectedSlot);
            if (faithful) b.Text["InfoPanel"] = ""; else Paragraph("InfoPanel", slotDetails);
            // The original's Load button is always live (a click without a selection does nothing); the fix waits for a readable save.
            if (id == "save") Enable("SaveButton"); else if (id == "load" && (faithful && !FixLoadButton || selectedSlot is not null && slotReadable)) Enable("LoadButton");
            b.Properties[id == "save" ? "SaveButton" : "LoadButton"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
        }
        else if (id == "message")
        {
            b.Text["Root"] = confirmation?.Title ?? ""; b.Text["MessageText"] = confirmation?.Text ?? "";
            b.Text["ButtonA"] = confirmation?.Yes ?? "YES";
            if (confirmation is { OkOnly: true }) { Enable("ButtonA"); b.Hidden.Add("ButtonB"); b.Hidden.Add("ButtonC"); }
            else if (confirmation?.Middle is { } middle) { Enable("ButtonA", "ButtonB", "ButtonC"); b.Text["ButtonB"] = middle.Caption; b.Text["ButtonC"] = confirmation?.Cancel ?? "CANCEL"; }
            else { Enable("ButtonA", "ButtonB"); b.Hidden.Add("ButtonC"); b.Text["ButtonB"] = confirmation?.Cancel ?? "CANCEL"; }
            b.Properties["MessageText"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true" };
            b.Properties["ButtonA"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
        }
        else return false;
        return true;
    }

    // The Faithful frontend windows' placement and their line panels (docs/game/ui-screens.md "Title screen", "Load and save windows", "Start window").
    /// <summary>
    /// The load and save windows at the layout's size, centred on the screen in play and with their left edge at the screen's centre on the title (the end of
    /// FUN_140480100); the import window at 0.5 and 0.05 of the screen with its advanced options under it (FUN_140481130).
    /// </summary>
    void PlaceSlotWindow(string id, UiBindings b)
    {
        if (id == "import") { player.advancedOptions.BindAdvancedPanels(id, b); if (!player.advancedOptions.advancedOpen) b.Hidden.Add("advanced-Root"); return; }
        var root = player.faithfulScreens.Layout(id).Roots[0];
        var size = root.Position.RelativeTo(new(0, 0, player.Width, player.Height), root.RealPosition);
        float w = Math.Min(size.Width, player.Width), h = Math.Min(size.Height, player.Height);
        float x = player.TitleOpen && player.UiMode == UiMode.Faithful ? Math.Min(player.Width / 2f, player.Width - w) : (player.Width - w) / 2;
        b.Positions[root.Name] = new(x, (player.Height - h) / 2, w, h);
        // Meitou: the save list ends above the buttons' row, where the Kenshi saves tick goes (WithSaveFilter).
        if (id == "load" && player.UiMode == UiMode.Meitou) b.Positions["GamesList"] = new(x + 0.0102941f * w, (player.Height - h) / 2 + 0.135385f * h, 0.644118f * w, 0.69f * h);
    }

    /// <summary>The load window with a Meitou row under its list: a tick that hides the base game's saves, styled as the reset tick (hidden in Faithful).</summary>
    internal static MyGuiLayout WithSaveFilter(MyGuiLayout load)
    {
        var root = load.Roots[0];
        MyGuiWidget Row(string type, string name, string skin, UiRect at, Dictionary<string, string> properties) =>
            new(type, name, skin, "", "Left Bottom", at, true, properties, new Dictionary<string, string>(), []);
        MyGuiWidget[] row =
        [
            Row("TextBox", "lbHideKenshiSaves", "Kenshi_GenericTextBoxFlat", new(0.0102941f, 0.836923f, 0.58f, 0.123077f), new() { ["Caption"] = "Hide Kenshi saves", ["TextAlign"] = "Left VCenter" }),
            Row("Button", HideKenshiTick, "Kenshi_TickBoxSkin", new(0.6029412f, 0.844615f, 0.0514706f, 0.107692f), []),
        ];
        return load with { Roots = [root with { Children = [.. root.Children, .. row] }, .. load.Roots.Skip(1)] };
    }

    /// <summary>
    /// The lines of a DatapanelGUI over the arranged widget <paramref name="panel"/> (FUN_1406f62a0, FUN_1406f9ab0, FUN_1406f7cb0): rows of 0.94 / max(1, (int)(<paramref name="rows"/> ×
    /// panel height / screen height)) of its height from 0.03 of it (rows 55 by default, FUN_1406fce20; the start window sets 40), the label from 3 % of the width and 96 % wide, a value right-aligned in 93 % from the same place. The last label of a
    /// <paramref name="lastWraps"/> panel is a wrapped paragraph down to the panel's bottom (the start's description).
    /// </summary>
    internal void AddDatapanel(List<UiControl> controls, string panel, IReadOnlyList<(string Label, string Value)> lines, bool lastWraps, float rows = 55)
    {
        if (controls.FirstOrDefault(c => c.Name == panel) is not { } host) return;
        var area = host.Bounds;
        float view = area.Height, width = area.Width;
        float pitch = 0.94f / Math.Max(1, (int)(rows * view / Math.Max(1, player.Height))) * view;
        var clip = area.Intersect(host.Clip ?? area);
        for (int i = 0; i < lines.Count; i++)
        {
            var (label, value) = lines[i];
            float top = area.Y + i * pitch + 0.03f * view;
            bool wrap = lastWraps && i == lines.Count - 1;
            var labelBounds = new UiRect(area.X + 0.03f * width, top, (wrap ? 0.96f : value.Length > 0 ? 0.93f : 0.96f) * width, wrap ? area.Y + area.Height - top : pitch);
            controls.Add(new($"{panel}-label-{i}", "TextBox", labelBounds, label, "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string> { ["TextAlign"] = "Left Top", ["TextColour"] = MyGuiColours.TemplateInk(player.uiResources, "Kenshi_TextboxStandardText"), ["WordWrap"] = wrap ? "true" : "false" }, Vector4.Zero, Pickable: false, Clip: clip));
            if (value.Length == 0) continue;
            controls.Add(new($"{panel}-value-{i}", "TextBox", new(area.X + 0.03f * width, top, 0.93f * width, pitch), value, "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string> { ["TextAlign"] = "Right Top", ["TextColour"] = MyGuiColours.TemplateInk(player.uiResources, "Kenshi_TextboxStandardText") }, Vector4.Zero, Pickable: false, Clip: clip));
        }
    }

    /// <summary>
    /// The message box with its buttons centred as a group and its text in the palette's light Main colour, in both modes (Meitou's choice for readability: the base
    /// game leaves them where the layout puts them, left of centre, and the text in dark ink on the dark window).
    /// </summary>
    internal void StyleMessageBox(List<UiControl> controls)
    {
        if (controls.FirstOrDefault(c => c.Name == "Root") is not { } root) return;
        string[] buttons = [.. new[] { "ButtonA", "ButtonB", "ButtonC" }.Where(n => controls.Any(c => c.Name == n))];
        if (buttons.Length > 0)
        {
            var bounds = buttons.Select(n => controls.Last(c => c.Name == n).Bounds).ToArray();
            float left = bounds.Min(r => r.X), right = bounds.Max(r => r.X + r.Width);
            float dx = root.Bounds.X + (root.Bounds.Width - (right - left)) / 2 - left;
            for (int i = 0; i < controls.Count; i++)
                if (buttons.Any(n => controls[i].Name == n || controls[i].Name.StartsWith(n + "@", StringComparison.Ordinal)))
                    controls[i] = controls[i] with { Bounds = controls[i].Bounds with { X = controls[i].Bounds.X + dx } };
        }
        for (int i = 0; i < controls.Count; i++)
            if (controls[i].Name == "MessageText" || controls[i].Properties.GetValueOrDefault("CaptionOwner") == "MessageText")
                controls[i] = controls[i] with { Properties = new Dictionary<string, string>(controls[i].Properties) { ["TextColour"] = PlayerInterface.MainColour } };
    }

    /// <summary>The start's lines (FUN_140916620): difficulty, cash, play style as the record has them (not translated), then its description.</summary>
    internal void AddStartLines(List<UiControl> controls)
    {
        if (starts.Count == 0) return;
        var start = starts[startIndex];
        AddDatapanel(controls, "DescriptionPanel",
        [
            (Loc.Tr("Difficulty:"), start.Difficulty), (Loc.Tr("Cash:"), "c." + start.Money.ToString(CultureInfo.InvariantCulture)), (Loc.Tr("Play Style:"), start.Style),
            (start.Description, ""),
        ], lastWraps: true, rows: 40);
    }

    /// <summary>
    /// The selected save's lines in the info box (FUN_14047e780): its date, the CAMERA's version ("Ancient" when it has none), faction name, money, members, area and day;
    /// empty while nothing is selected.
    /// </summary>
    internal void AddSlotInfo(List<UiControl> controls)
    {
        if (selectedSlot is not { } slot || slotInfo is not { } info) return;
        AddDatapanel(controls, "InfoPanel",
        [
            (Loc.Tr("Date"), SlotDate(slot)), (Loc.Tr("File version"), info.Version.Length > 0 ? info.Version : "Ancient"), (Loc.Tr("Faction name"), info.Faction),
            (Loc.Tr("Funds"), "c." + info.Funds.ToString(CultureInfo.InvariantCulture)), (Loc.Tr("Members size"), info.Members.ToString(CultureInfo.InvariantCulture)),
            (Loc.Tr("Location"), info.Area), (Loc.Tr("Day"), info.Day.ToString(CultureInfo.InvariantCulture)),
        ], lastWraps: false);
    }
}

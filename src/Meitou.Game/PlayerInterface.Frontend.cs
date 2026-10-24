using Meitou.Data.Fcs;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Data.Ui;
using Meitou.Ui;
using System.Globalization;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    IReadOnlyList<NewGameStart> starts = [];
    int startIndex;
    Func<IReadOnlyList<SaveSlot>> saveSlots = () => [];
    IReadOnlyList<SaveSlot> slots = [];
    SaveSlot? selectedSlot;
    string slotDetails = "", credits = "";
    bool showCredits;
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
    SaveSlot[] VisibleSlots(string id) => [.. slots.Where(s => s.Writable || id != "save" && !(id == "load" && hideKenshiSaves && UiMode == UiMode.Meitou))];
    /// <summary>The import window's ticks as the flag bits of <c>importGame</c> (ticks in the order of <see cref="TickBoxes"/>: 0x1, 0x4, 0x8, 0x10, 0x20).</summary>
    public Meitou.Simulation.Saving.ImportOptions ImportFlags =>
        (slotTicks[0] ? Meitou.Simulation.Saving.ImportOptions.ResetPositions : 0) | (slotTicks[1] ? Meitou.Simulation.Saving.ImportOptions.Buildings : 0)
        | (slotTicks[2] ? Meitou.Simulation.Saving.ImportOptions.Research : 0) | (slotTicks[3] ? Meitou.Simulation.Saving.ImportOptions.DeadNpcs : 0)
        | (slotTicks[4] ? Meitou.Simulation.Saving.ImportOptions.Relations : 0);
    /// <summary>The open message box: ButtonA confirms (captioned <c>Yes</c>), an optional middle choice (ButtonB), and the last button cancels (Esc presses it).</summary>
    sealed record Confirmation(string Title, string Text, Action Confirm, string Yes = "YES", (string Caption, Action Act)? Middle = null, string Cancel = "CANCEL");
    Confirmation? confirmation;
    /// <summary>The game data (<see cref="ConfigureFrontend"/>; null without it).</summary>
    GameDatabase? gameData;
    public void ConfigureFrontend(GameDatabase? database, Func<IReadOnlyList<SaveSlot>> enumerate, string creditsText)
    {
        gameData = database;
        starts =database is null ? [] : NewGameStart.LoadAll(database);
        startIndex = Math.Max(0, starts.ToList().FindIndex(s => s.Name == NewGameStart.DefaultName));
        saveSlots = enumerate;
        // As the title reads data/credits.txt (FUN_14013d680, docs/game/ui-screens.md "Credits"): empty lines dropped, a "-" line a blank one, each line ended.
        credits = string.Concat(creditsText.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Select(l => (l == "-" ? "" : l) + "\n"));
    }
    public void ShowTitle() { faithfulScreens.Open("title"); RefreshSlots(); UpdateMenuPause(); }
    public bool TitleOpen => faithfulScreens.IsOpen("title");
    /// <summary>After leaving a game: title controls over the original image or the live world, according to UI mode.</summary>
    public void ShowMainMenu() => ShowTitle();
    public void CloseFrontend()
    {
        foreach (string id in new[] { "title", "menu", "new-game", "save", "load", "import", "message", "editor", "file" }) faithfulScreens.Close(id);
        editor = null; previews.Clear();
        confirmation = null; UpdateMenuPause();
    }
    void Confirm(string title, string text, Action action, string yes = "YES", (string Caption, Action Act)? middle = null, string cancel = "CANCEL")
    {
        confirmation = new(UpperTr(title), Loc.Tr(text), action, UpperTr(yes), middle is { } m ? (UpperTr(m.Caption), m.Act) : null, UpperTr(cancel)); faithfulScreens.Open("message");
    }
    /// <summary>The cancel button of the open message box: ButtonC when it has a middle choice, else ButtonB.</summary>
    string CancelButton => confirmation?.Middle is null ? "ButtonB" : "ButtonC";
    void RefreshSlots()
    {
        try { slots = saveSlots(); selectedSlot = slots.FirstOrDefault(); ReadSlotDetails(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { slots = []; selectedSlot = null; slotDetails = e.Message; }
    }
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
        try { NewGameOptions = NewGameSettings.Read(new SaveGame(SaveFile.Read(File.ReadAllBytes(Path.Combine(slot.Folder, SaveFolder.QuickSaveName)))).Camera); }
        catch (Exception e) when (e is IOException or FormatException or ArgumentException or Meitou.Data.Fcs.FcsFormatException) { /* an unreadable save keeps the sliders as they are */ }
    }
    void OpenSlots(string id)
    {
        RefreshSlots();
        if (id == "save" || id == "load" && UiMode == UiMode.Meitou) { selectedSlot = VisibleSlots(id).FirstOrDefault(); ReadSlotDetails(); }
        // The original's windows open with nothing selected and the info box empty (LoadSaveWindow, FUN_140480100), the ticks as their layout has them.
        else if (UiMode == UiMode.Faithful) { selectedSlot = null; slotInfo = null; }
        Array.Copy(new[] { false, true, true, true, true }, slotTicks, slotTicks.Length);
        faithfulScreens.Open(id); if (id == "save") faithfulScreens.SetText(id, "SaveEditBox", selectedSlot?.Name ?? "");   // the selected save's name, ready to overwrite
    }
    bool HandleFrontendAction(MyGuiEvent e)
    {
        if (e.Screen is "title" or "menu")
        {
            // The title's buttons first close its other windows and the credits (FUN_140914a90); Load and Import close their own window when it is open,
            // New game leaves its window as it is, Credits starts them again (docs/game/ui-screens.md "Title screen").
            bool title = e.Screen == "title";
            bool wasOpen = faithfulScreens.IsOpen(e.Widget switch { "LoadGameButton" => "load", "ImportGameButton" => "import", "NewGameButton" => "new-game", _ => "" });
            if (title && !(e.Widget == "NewGameButton" && wasOpen)) CloseTitleWindows();
            switch (e.Widget)
            {
                case "ResumeButton": faithfulScreens.Close("menu"); break;
                case "NewGameButton": if (!wasOpen) { NewGameOptions = new(); advancedOpen = false; faithfulScreens.Open("new-game"); } break;
                case "SaveGameButton": OpenSlots("save"); break;
                case "LoadGameButton": if (!(title && wasOpen)) OpenSlots("load"); break;
                case "ImportGameButton": if (!wasOpen) { advancedOpen = false; OpenSlots("import"); } break;
                case "OptionsButton": ScreenAction?.Invoke(e); break;
                case "CreditsButton": showCredits = true; creditsStart = System.Diagnostics.Stopwatch.GetTimestamp(); break;
                case "ContinueButton": if (slots.FirstOrDefault() is { } last) ScreenAction?.Invoke(new("load", "LoadButton", "load", last.Folder)); break;
                case "ExitButton": Confirm("EXIT", "Are you sure you want to exit?", () => ScreenAction?.Invoke(new("menu", "ExitGameButton", "exit"))); break;
                // In play the Esc menu's Exit offers the main menu as well (engine choice: the original's in-game Exit dialog is not traced).
                case "ExitGameButton":
                    Confirm("EXIT", session.World.Player.Exists ? "Return to the main menu or quit the game? Unsaved progress will be lost." : "Return to the main menu or quit the game?",
                        () => ScreenAction?.Invoke(new("menu", "MainMenuButton", "main-menu")), "MAIN MENU", ("QUIT", () => ScreenAction?.Invoke(new("menu", "ExitGameButton", "exit"))));
                    break;
            }
            return true;
        }
        if (e.Screen == "message")
        {
            var confirm = confirmation; confirmation = null; faithfulScreens.Close("message");
            if (e.Widget == "ButtonA") confirm?.Confirm();
            else if (e.Widget == "ButtonB" && confirm?.Middle is { } middle) middle.Act();
            return true;
        }
        if (e.Screen == "new-game")
        {
            if (AdvancedAction(e)) return true;
            if (e.Widget == "CancelButton") faithfulScreens.Close(e.Screen);
            else if (e.Widget == "LeftButton" && starts.Count > 0) startIndex = (startIndex + starts.Count - 1) % starts.Count;
            else if (e.Widget == "RightButton" && starts.Count > 0) startIndex = (startIndex + 1) % starts.Count;
            else if (e.Widget == "AdvancedButton") { advancedOpen = !advancedOpen; advancedHelp = -1; }
            else if (e.Widget == "BeginButton" && starts.Count > 0)
            {
                void Start() => ScreenAction?.Invoke(new("new-game", "BeginButton", "new-game", starts[startIndex].Id));
                if (session.World.Player.Exists) Confirm("NEW GAME", "Start a new game? Unsaved progress will be lost.", Start); else Start();
            }
            return true;
        }
        if (e.Screen is "save" or "load" or "import")
        {
            if (e.Screen == "import" && AdvancedAction(e)) return true;
            if (e.Widget == "CancelButton") { faithfulScreens.Close(e.Screen); advancedOpen = false; }
            else if (Array.IndexOf(TickBoxes, e.Widget) is var tick and >= 0) slotTicks[tick] = !slotTicks[tick];
            else if (e.Widget == HideKenshiTick)
            {
                hideKenshiSaves = !hideKenshiSaves;
                if (selectedSlot is not null && !VisibleSlots(e.Screen).Contains(selectedSlot)) { selectedSlot = null; ReadSlotDetails(); }
            }
            else if (e.Widget == "AdvancedButton") { advancedOpen = !advancedOpen; advancedHelp = -1; if (advancedOpen && e.Screen == "import") ReadSlotDifficulty(); }
            else if (e.Widget == "ImportButton" && selectedSlot is { } importSlot)
            {
                void Import() => ScreenAction?.Invoke(new("import", "ImportButton", "import", importSlot.Folder));
                if (!slotReadable) Notice("This save file is invalid", true);   // the original's message box for a save whose row reads ERROR
                else if (session.World.Player.Exists) Confirm("IMPORT GAME", "Import this save into a new game? Unsaved progress will be lost.", Import); else Import();
            }
            else if (e.Widget == "GamesList" && e.Action == "select")
            {
                selectedSlot = slots.FirstOrDefault(s => s.Folder == e.Text); ReadSlotDetails();
                if (e.Screen == "import" && advancedOpen) ReadSlotDifficulty();
                if (e.Screen == "save" && selectedSlot is { } slot) faithfulScreens.SetText("save", "SaveEditBox", slot.Name);
            }
            else if (e.Widget == "LoadButton" && selectedSlot is { } loadSlot)
            {
                void Load() => ScreenAction?.Invoke(new("load", "LoadButton", "load", loadSlot.Folder));
                if (session.World.Player.Exists) Confirm("LOAD GAME", "Load this save? Unsaved progress will be lost.", Load); else Load();
            }
            else if (e.Widget == "SaveButton")
            {
                string name = faithfulScreens.GetText("save", "SaveEditBox");
                try { SaveCatalog.ValidateName(name); }
                catch (ArgumentException ex) { Notice(ex.Message, true); return true; }
                void Save() { ScreenAction?.Invoke(new("save", "SaveButton", "save", name)); faithfulScreens.Close("save"); }
                if (slots.Any(s => s.Writable && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) Confirm("OVERWRITE SAVE", Loc.Tr("Overwrite save '{1}'?", name), Save); else Save();
            }
            return true;
        }
        return false;
    }
    /// <summary>The title's other windows and the credits closed (the original's FUN_140914a90, run by every title button).</summary>
    void CloseTitleWindows()
    {
        foreach (string id in new[] { "new-game", "load", "import" }) faithfulScreens.Close(id);
        advancedOpen = false;
        showCredits = false;
    }

    /// <summary>A caption the original sets from the catalog in upper case (boost::locale's to_upper of the translation).</summary>
    static string UpperCaption(string id) => Loc.Tr(id).ToUpperInvariant();

    /// <summary>The save list's date: the save folder's last write, in the user's short date and time with seconds (boost::locale <c>{1,datetime}</c>, Faithful).</summary>
    string SlotDate(SaveSlot s) => UiMode == UiMode.Faithful ? s.ModifiedUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)
        : s.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    bool BindFrontend(string id, UiBindings b)
    {
        void Enable(params string[] names) { foreach (string name in names) { b.Disabled.Remove(name); b.Actions[name] = name; } }
        void Paragraph(string name, string text) { b.Text[name] = text; b.Properties[name] = new Dictionary<string, string> { ["WordWrap"] = "true", ["TextAlign"] = "Left Top", ["TextColour"] = MyGuiColours.InkText, ["TextPadding"] = name == "InfoPanel" ? "8 8 8 8" : "12 10 12 10" }; }
        if (id is "title" or "menu")
        {
            Enable("NewGameButton", "LoadGameButton", "OptionsButton");
            if (starts.Count == 0) b.Disabled.Add("NewGameButton");
            if (id == "menu")
            {
                Enable("ExitGameButton", "ResumeButton");
                if (session.World.Player.Exists) Enable("SaveGameButton");
                return true;
            }
            Enable("ExitButton", "CreditsButton");
            if (UiMode == UiMode.Faithful)
            {
                Enable("ImportGameButton");
                foreach (var (widget, caption) in TitleCaptions) b.Text[widget] = UpperCaption(caption);
            }
            if (UiMode == UiMode.Meitou)
            {
                b.Hidden.Add("ImportGameButton");
                foreach (var (widget, at) in MenuLayout(card: slots.Count > 0 && slotReadable)) b.Positions[widget] = at;
            }
            // Continue is shown only when a save exists (docs/game/ui-screens.md section 3), not greyed out.
            if (slots.Count > 0 && slotReadable) Enable("ContinueButton"); else b.Hidden.Add("ContinueButton");
            b.Properties["CreditsPanel"] = new Dictionary<string, string> { ["Visible"] = showCredits ? "true" : "false" };
            if (showCredits)
            {
                // From 0.85 of the panel's height, 25 pixels up a second of wall-clock time (FUN_140914230, FUN_140913880); the text goes on out of view.
                var layout = faithfulScreens.Layout("title");
                var panel = RealBounds(layout, "CreditsPanel");
                var view = RealBounds(layout, "CreditsPanelContent");
                double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(creditsStart).TotalSeconds;
                float scroll = MathF.Round((float)(panel.Height * 0.85 - seconds * 25), MidpointRounding.AwayFromZero);
                b.Positions["CreditsPanelContent"] = view with { Y = view.Y + scroll, Height = Math.Max(view.Height, credits.Count(c => c == '\n') * 40 * Height / 1080f + 100) };
                b.Text["CreditsPanelContent"] = credits;
            }
            b.Text["VersionText"] = UiMode == UiMode.Meitou ? "Meitou" : "Meitou - Faithful UI";
        }
        else if (id == "new-game")
        {
            BindNewGamePanels(b);
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
            b.TemplateScale = Height / 1080f;
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
                Enable(TickBoxes[t]);
                b.Properties[TickBoxes[t]] = new Dictionary<string, string> { ["StateSelected"] = slotTicks[t] ? "true" : "false", ["Checked"] = slotTicks[t] ? "true" : "false" };
            }
            if (id == "load" && UiMode == UiMode.Meitou)
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
            bool faithful = UiMode == UiMode.Faithful;
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
            if (confirmation?.Middle is { } middle) { Enable("ButtonA", "ButtonB", "ButtonC"); b.Text["ButtonB"] = middle.Caption; b.Text["ButtonC"] = confirmation?.Cancel ?? "CANCEL"; }
            else { Enable("ButtonA", "ButtonB"); b.Hidden.Add("ButtonC"); b.Text["ButtonB"] = confirmation?.Cancel ?? "CANCEL"; }
            b.Properties["MessageText"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true" };
            b.Properties["ButtonA"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
        }
        else return false;
        return true;
    }
}

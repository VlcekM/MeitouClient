using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Meitou.Launcher;

// Play reports for the roadmap: "Works for me" / "Broken" on one roadmap system, with the version, platform and whether mods are on, and
// optionally the end of that version's log.txt. Sent to the reports service (services/Meitou.Reports); docs/play-reports.md.
public sealed partial class MainWindow
{
    /// <summary>The reports service; empty hides the Report button.</summary>
    const string ReportsUrl = "https://gear-pastry-chorus.cloudovna.app";
    const string RoadmapUrl = "https://meitou.kenshi.wiki/roadmap.json";
    const int LogTail = 512 * 1024;

    /// <summary>A roadmap row: the id sent in the report and the system name shown (its text in the picker's list).</summary>
    sealed record RoadmapRow(string Id, string Name) { public override string ToString() => Name; }

    void ShowReportButton() => ReportButton.IsVisible = ReportsUrl.Length > 0;

    async void Report_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        List<RoadmapRow> rows;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var file = JsonNode.Parse(await Http.GetStringAsync(RoadmapUrl, cts.Token));
            rows = (file?["rows"] as JsonArray ?? []).OfType<JsonObject>().Select(r => new RoadmapRow((string)r["id"]!, (string)r["system"]!)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Status(LauncherText.Translate("Could not read the roadmap: {1}", ex.Message));
            return;
        }
        if (rows.Count == 0) { Status("The roadmap lists no systems."); return; }

        var secondary = (IBrush)this.FindResource("Secondary")!;
        var main = (IBrush)this.FindResource("Main")!;
        // The system picker: a plate button that drops a searchable list (a ComboBox cannot hold a search box), the pick committed by a
        // click or Enter; arrows move the highlight, Esc closes.
        RoadmapRow? chosen = null;
        var systemLabel = new TextBlock { Text = "Roadmap system", Foreground = (IBrush)this.FindResource("Quiet")!, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var chevron = new Avalonia.Controls.Shapes.Path { Data = StreamGeometry.Parse("M0,0 H12 L6,7 Z"), Fill = main, VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 0, 0) };
        DockPanel.SetDock(chevron, Dock.Right);
        var system = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(12, 6), Content = new DockPanel { Children = { chevron, systemLabel } } };
        var search = new TextBox { PlaceholderText = LauncherText.Translate("Search"), Margin = new(8, 8, 8, 4) };
        var list = new ListBox { MaxHeight = 280 };
        var noMatch = new TextBlock { Classes = { "desc" }, Text = LauncherText.Translate("No match"), Margin = new(16, 6, 16, 10), IsVisible = false };
        var picker = new StackPanel { Children = { search, list, noMatch } };
        // Anchored on a border around the button, not the button: keys typed in the popup bubble to its anchor, and a Button treats Space
        // as a press.
        var anchor = new Border { Child = system };
        var flyout = new Flyout { Content = picker, Placement = PlacementMode.BottomEdgeAlignedLeft };
        system.Click += (_, _) => flyout.ShowAt(anchor);
        void Filter()
        {
            string query = search.Text?.Trim() ?? "";
            var shown = rows.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            list.ItemsSource = shown;
            list.SelectedIndex = query.Length == 0 && chosen is not null ? Math.Max(0, shown.IndexOf(chosen)) : shown.Count > 0 ? 0 : -1;
            noMatch.IsVisible = shown.Count == 0;
        }
        void Pick()
        {
            if (list.SelectedItem is not RoadmapRow row) return;
            chosen = row;
            systemLabel.Text = row.Name;
            systemLabel.Foreground = main;
            flyout.Hide();
        }
        flyout.Opening += (_, _) =>
        {
            picker.Width = Math.Max(0, anchor.Bounds.Width - 2);
            search.Text = "";
            Filter();
        };
        flyout.Opened += (_, _) => search.Focus();
        flyout.Closed += (_, _) => system.Focus();
        search.TextChanged += (_, _) => Filter();
        search.AddHandler(KeyDownEvent, (_, k) =>
        {
            if (k.Key == Key.Enter) { Pick(); k.Handled = true; }
            else if (k.Key == Key.Down && list.ContainerFromIndex(Math.Max(0, list.SelectedIndex)) is { } row) { row.Focus(); k.Handled = true; }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(KeyDownEvent, (_, k) => { if (k.Key == Key.Enter) { Pick(); k.Handled = true; } }, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(TappedEvent, (_, t) => { if ((t.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().Any() == true) Pick(); }, RoutingStrategies.Bubble, handledEventsToo: true);
        picker.AddHandler(KeyDownEvent, (_, k) => { if (k.Key == Key.Escape) { flyout.Hide(); k.Handled = true; } }, RoutingStrategies.Bubble, handledEventsToo: true);
        var works = new RadioButton { Content = "Works for me", GroupName = "verdict", IsChecked = true, Margin = new(0, 0, 16, 0) };
        var broken = new RadioButton { Content = "Broken", GroupName = "verdict" };
        var version = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var v in installedVersions) version.Items.Add(new ComboBoxItem { Content = v, Tag = v });
        version.SelectedItem = version.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == PlayVersion) ?? version.Items.OfType<ComboBoxItem>().FirstOrDefault();
        var note = new TextBox { PlaceholderText = "What you did or saw (needed for Broken)", MaxLength = 300 };
        var attach = new CheckBox { Content = "Attach the end of this version's log.txt (it can show folder names, such as your user name)" };
        var problem = new TextBlock { Classes = { "desc" }, TextWrapping = TextWrapping.Wrap };
        var send = new Button { Classes = { "paper" }, Content = new TextBlock { Text = "Send report", Foreground = secondary } };
        var cancel = new Button { Content = "Cancel", Margin = new(0, 0, 8, 0) };

        static Control Labelled(string label, Control control) =>
            new StackPanel { Spacing = 4, Children = { new TextBlock { Classes = { "desc" }, Text = label }, control } };
        var plate = new Border
        {
            Classes = { "plate" }, Width = 560, Padding = new(20, 16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new Border { Classes = { "header" }, Child = new TextBlock { Text = "Report to the roadmap" } },
                    new TextBlock { Classes = { "desc" }, TextWrapping = TextWrapping.Wrap, Text = "Tell the roadmap how one of Kenshi's systems plays in Meitou. Bugs with steps are best as a GitHub issue." },
                    Labelled("System", anchor),
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { works, broken } },
                    Labelled("Meitou version", version),
                    note,
                    attach,
                    problem,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, send } },
                },
            },
        };
        void Close()
        {
            SetupHost.IsVisible = false;
            SetupHost.Child = null;
        }
        cancel.Click += (_, _) => Close();
        plate.KeyDown += (_, k) => { if (k.Key == Key.Escape) Close(); };
        send.Click += async (_, _) =>
        {
            if (chosen is not { } row) { problem.Text = LauncherText.Translate("Pick a system."); return; }
            bool isBroken = broken.IsChecked == true;
            if (isBroken && string.IsNullOrWhiteSpace(note.Text)) { problem.Text = LauncherText.Translate("Say what happened."); return; }
            string? build = (version.SelectedItem as ComboBoxItem)?.Tag as string;
            var body = new JsonObject
            {
                ["row"] = row.Id, ["verdict"] = isBroken ? "broken" : "works", ["build"] = build ?? "unknown", ["platform"] = Platform(),
                ["modded"] = mods.Any(m => m.Enabled), ["note"] = note.Text?.Trim(), ["source"] = "launcher",
            };
            if (attach.IsChecked == true && build is not null && ReadLogTail(build) is { } log) body["log"] = log;
            send.IsEnabled = false;
            problem.Text = LauncherText.Translate("Sending…");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var response = await Http.PostAsync($"{ReportsUrl}/reports", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    string? error = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token))?["error"];
                    throw new HttpRequestException((int)response.StatusCode == 429 ? "too many reports from here, try later" : error ?? $"HTTP {(int)response.StatusCode}");
                }
                Close();
                Status("Thanks: your report is on its way to the roadmap.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                problem.Text = LauncherText.Translate("Not sent: {1}", ex.Message);
                send.IsEnabled = true;
            }
        };
        LauncherText.TranslateTree(plate, originalUiText);
        SetupHost.Child = plate;
        SetupHost.IsVisible = true;
        Dispatcher.UIThread.Post(() => system.Focus());
    }

    /// <summary>win, linux or mac; a Windows build under Wine or Proton (its registry has a Software\Wine key) is proton.</summary>
    static string Platform()
    {
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "mac";
        if (OperatingSystem.IsWindows())
        {
            using var wine = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Wine");
            if (wine is not null) return "proton";
        }
        return "win";
    }

    /// <summary>The last <see cref="LogTail"/> bytes of <paramref name="version"/>'s log.txt, from a whole line; null without one.</summary>
    static string? ReadLogTail(string version)
    {
        try
        {
            var path = Path.Combine(Releases.VersionDirectory(version), "log.txt");
            if (!File.Exists(path)) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, file.Length - LogTail);
            file.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[file.Length - start];
            file.ReadExactly(bytes);
            int from = start > 0 ? Array.IndexOf(bytes, (byte)'\n') + 1 : 0;
            return Encoding.UTF8.GetString(bytes, from, bytes.Length - from);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

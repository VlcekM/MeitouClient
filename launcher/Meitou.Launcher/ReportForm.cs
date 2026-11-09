using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Meitou.Launcher;

/// <summary>
/// The "Report to the roadmap" form: "Works for me" / "Broken" on one roadmap system, with the version, platform and whether mods are on, and
/// optionally the end of that version's log.txt. Sent to the reports service (services/Meitou.Reports); docs/play-reports.md.
/// </summary>
sealed class ReportForm
{
    /// <summary>
    /// The reports service; empty hides the Report button.
    /// </summary>
    public const string ReportsUrl = "https://gear-pastry-chorus.cloudovna.app";

    /// <summary>
    /// The most of a log.txt a report carries, in bytes (its end).
    /// </summary>
    const int LogTail = 512 * 1024;

    /// <summary>
    /// The client the report is posted with.
    /// </summary>
    readonly HttpClient _http;

    /// <summary>
    /// Whether any mod is enabled, asked when the report is sent.
    /// </summary>
    readonly Func<bool> _modded;

    /// <summary>
    /// Shows a line in the launcher's status bar.
    /// </summary>
    readonly Action<string> _status;

    /// <summary>
    /// Takes the form off the window.
    /// </summary>
    readonly Action _close;

    /// <summary>
    /// The system picker.
    /// </summary>
    readonly RoadmapPicker _picker;

    /// <summary>
    /// The "Broken" verdict (else "Works for me").
    /// </summary>
    readonly RadioButton _broken;

    /// <summary>
    /// The installed version the report is about.
    /// </summary>
    readonly ComboBox _version;

    /// <summary>
    /// What the player did or saw (needed for Broken).
    /// </summary>
    readonly TextBox _note;

    /// <summary>
    /// Whether the end of the version's log.txt goes with the report.
    /// </summary>
    readonly CheckBox _attach;

    /// <summary>
    /// What is missing, or how the sending went.
    /// </summary>
    readonly TextBlock _problem;

    /// <summary>
    /// The Send button (off while a report is on its way).
    /// </summary>
    readonly Button _send;

    /// <summary>
    /// The form, to place over the window.
    /// </summary>
    public Border Plate { get; }

    /// <summary>
    /// A form over the roadmap's <paramref name="rows"/>, offering the <paramref name="versions"/> installed with <paramref name="playVersion"/>
    /// chosen; the brushes come from <paramref name="resources"/>.
    /// </summary>
    public ReportForm(List<RoadmapRow> rows, IResourceHost resources, IEnumerable<string> versions, string? playVersion, Func<bool> modded,
            HttpClient http, Action<string> status, Action close)
    {
        _http = http;
        _modded = modded;
        _status = status;
        _close = close;
        var secondary = (IBrush)resources.FindResource("Secondary")!;
        var main = (IBrush)resources.FindResource("Main")!;

        _picker = new RoadmapPicker(rows, main, (IBrush)resources.FindResource("Quiet")!);
        var works = new RadioButton { Content = "Works for me", GroupName = "verdict", IsChecked = true, Margin = new(0, 0, 16, 0) };
        _broken = new RadioButton { Content = "Broken", GroupName = "verdict" };
        _version = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var v in versions) _version.Items.Add(new ComboBoxItem { Content = v, Tag = v });
        _version.SelectedItem = _version.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == playVersion)
            ?? _version.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _note = new TextBox { PlaceholderText = "What you did or saw (needed for Broken)", MaxLength = 300 };
        _attach = new CheckBox { Content = "Attach the end of this version's log.txt (it can show folder names, such as your user name)" };
        _problem = new TextBlock { Classes = { "desc" }, TextWrapping = TextWrapping.Wrap };
        _send = new Button { Classes = { "paper" }, Content = new TextBlock { Text = "Send report", Foreground = secondary } };
        var cancel = new Button { Content = "Cancel", Margin = new(0, 0, 8, 0) };

        Plate = CreatePlate(works, cancel);
        cancel.Click += (_, _) => _close();
        Plate.KeyDown += (_, k) => { if (k.Key == Key.Escape) _close(); };
        _send.Click += Send_Click;
    }

    /// <summary>
    /// Focuses the system picker.
    /// </summary>
    public void Focus() => _picker.Focus();

    /// <summary>
    /// The plate: the header and the hint, the system, the verdict, the version, the note, the log box, the problem line and the buttons.
    /// </summary>
    Border CreatePlate(RadioButton works, Button cancel)
    {
        static Control Labelled(string label, Control control) =>
            new StackPanel { Spacing = 4, Children = { new TextBlock { Classes = { "desc" }, Text = label }, control } };
        return new Border
        {
            Classes = { "plate" }, Width = 560, Padding = new(20, 16), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new Border { Classes = { "header" }, Child = new TextBlock { Text = "Report to the roadmap" } },
                    new TextBlock
                    {
                        Classes = { "desc" }, TextWrapping = TextWrapping.Wrap,
                        Text = "Tell the roadmap how one of Kenshi's systems plays in Meitou. Bugs with steps are best as a GitHub issue.",
                    },
                    Labelled("System", _picker.Anchor),
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { works, _broken } },
                    Labelled("Meitou version", _version),
                    _note,
                    _attach,
                    _problem,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _send },
                    },
                },
            },
        };
    }

    /// <summary>
    /// Send: a system must be picked, and Broken needs a note; then the report is posted.
    /// </summary>
    async void Send_Click(object? sender, RoutedEventArgs e)
    {
        if (_picker.Chosen is not { } row)
        {
            _problem.Text = LauncherText.Translate("Pick a system.");
            return;
        }
        bool isBroken = _broken.IsChecked == true;
        if (isBroken && string.IsNullOrWhiteSpace(_note.Text))
        {
            _problem.Text = LauncherText.Translate("Say what happened.");
            return;
        }
        var body = ReportBody(row, isBroken);
        _send.IsEnabled = false;
        _problem.Text = LauncherText.Translate("Sending…");
        await PostAsync(body);
    }

    /// <summary>
    /// The report: the row, the verdict, the build, the platform, whether mods are on, the note, and the log's end when asked for.
    /// </summary>
    JsonObject ReportBody(RoadmapRow row, bool isBroken)
    {
        string? build = (_version.SelectedItem as ComboBoxItem)?.Tag as string;
        var body = new JsonObject
        {
            ["row"] = row.Id, ["verdict"] = isBroken ? "broken" : "works", ["build"] = build ?? "unknown", ["platform"] = Platform(),
            ["modded"] = _modded(), ["note"] = _note.Text?.Trim(), ["source"] = "launcher",
        };
        if (_attach.IsChecked == true && build is not null && ReadLogTail(build) is { } log) body["log"] = log;
        return body;
    }

    /// <summary>
    /// Posts the report; on success the form closes with a thank-you, else the problem line says why and Send is on again.
    /// </summary>
    async Task PostAsync(JsonObject body)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{ReportsUrl}/reports", content, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string? error = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token))?["error"];
                throw new HttpRequestException(
                    (int)response.StatusCode == 429 ? "too many reports from here, try later" : error ?? $"HTTP {(int)response.StatusCode}");
            }
            _close();
            _status("Thanks: your report is on its way to the roadmap.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _problem.Text = LauncherText.Translate("Not sent: {1}", ex.Message);
            _send.IsEnabled = true;
        }
    }

    /// <summary>
    /// win, linux or mac; a Windows build under Wine or Proton (its registry has a Software\Wine key) is proton.
    /// </summary>
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

    /// <summary>
    /// The last <see cref="LogTail"/> bytes of <paramref name="version"/>'s log.txt, from a whole line; null without one.
    /// </summary>
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

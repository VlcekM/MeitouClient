using System.Text.Json.Nodes;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Meitou.Launcher;

// Play reports for the roadmap (the form is ReportForm): the roadmap's rows read, then the form shown over the window.
public sealed partial class MainWindow
{
    /// <summary>
    /// The roadmap's rows (docs/play-reports.md).
    /// </summary>
    const string RoadmapUrl = "https://meitou.kenshi.wiki/roadmap.json";

    /// <summary>
    /// The Report button shows while there is a reports service.
    /// </summary>
    void ShowReportButton() => ReportButton.IsVisible = ReportForm.ReportsUrl.Length > 0;

    /// <summary>
    /// Report: the roadmap's rows read, then the report form over the window.
    /// </summary>
    async void Report_Click(object? sender, RoutedEventArgs e)
    {
        if (await ReadRoadmapAsync() is not { } rows) return;
        var form = new ReportForm(rows, this, _installedVersions, PlayVersion, () => _mods.Any(m => m.Enabled), Http, Status, CloseReport);
        LauncherText.TranslateTree(form.Plate, _originalUiText);
        SetupHost.Child = form.Plate;
        SetupHost.IsVisible = true;
        Dispatcher.UIThread.Post(form.Focus);
    }

    /// <summary>
    /// The roadmap's rows; null, with the reason in the status bar, when it cannot be read or lists none.
    /// </summary>
    async Task<List<RoadmapRow>?> ReadRoadmapAsync()
    {
        List<RoadmapRow> rows;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var file = JsonNode.Parse(await Http.GetStringAsync(RoadmapUrl, cts.Token));
            rows = (file?["rows"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(r => new RoadmapRow((string)r["id"]!, (string)r["system"]!))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Status(LauncherText.Translate("Could not read the roadmap: {1}", ex.Message));
            return null;
        }
        if (rows.Count == 0)
        {
            Status("The roadmap lists no systems.");
            return null;
        }
        return rows;
    }

    /// <summary>
    /// Takes the report form off the window.
    /// </summary>
    void CloseReport()
    {
        SetupHost.IsVisible = false;
        SetupHost.Child = null;
    }
}

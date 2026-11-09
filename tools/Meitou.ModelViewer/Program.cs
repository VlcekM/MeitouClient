using System.Diagnostics;
using System.Globalization;
using Meitou.ModelViewer;
using Silk.NET.Windowing;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
args = SmokeTest.Strip(args);
// Started without options (a double-click on the release's exe): the world at The Hub.
if (args.Length == 0) args = ["--world", "--town", "The Hub"];
if (args.Contains("--world") || args.Contains("--view") || args.Contains("--bench-compare")) return WorldApp.Run(args);
if (args.Contains("--impostor-preview") || args.Contains("--impostor-bake-all")) return ImpostorApp.Run(args);
// The mesh and character viewers were removed in phase 8 (DECISIONS 23; docs/character-viewer.md, the tag model-viewer-last has them).
Console.WriteLine("meitou-viewer --world [options]   (meitou-viewer --world --help lists the options)");
Console.WriteLine(ImpostorApp.Usage);
Console.WriteLine("The mesh and character viewers were removed (DECISIONS 23, docs/character-viewer.md).");
return 2;

/// <summary>
/// <c>--quit-after &lt;s&gt;</c> (any interactive mode): the window closes itself after that many seconds and the frames drawn are printed (an
/// unattended smoke test).
/// </summary>
static class SmokeTest
{
    /// <summary>
    /// The seconds after the first frame the window closes at; null without <c>--quit-after</c> (or once it has).
    /// </summary>
    static double? s_seconds;

    /// <summary>
    /// The frames drawn.
    /// </summary>
    static long s_frames;

    /// <summary>
    /// Runs from the first frame.
    /// </summary>
    static readonly Stopwatch Clock = new();

    /// <summary>
    /// Takes <c>--quit-after</c> out of <paramref name="args"/>.
    /// </summary>
    public static string[] Strip(string[] args)
    {
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--quit-after" && i + 1 < args.Length)
            {
                s_seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else
            {
                rest.Add(args[i]);
            }
        }
        return rest.ToArray();
    }

    /// <summary>
    /// Counts a drawn frame.
    /// </summary>
    public static void Frame()
    {
        if (s_frames++ == 0) Clock.Start();   // the time starts at the first frame, after loading
    }

    /// <summary>
    /// Closes <paramref name="window"/> once the time is up (call from the update).
    /// </summary>
    public static void Check(IWindow window)
    {
        if (s_seconds is not { } s || s_frames == 0 || Clock.Elapsed.TotalSeconds < s) return;
        double t = Clock.Elapsed.TotalSeconds;
        Console.WriteLine($"smoke     {s_frames} frames in {t:0.0} s: {s_frames / t:0} fps on average");
        s_seconds = null;
        window.Close();
    }
}

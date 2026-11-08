using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Metal;

/// <summary>
/// A one-shot programmatic Metal frame capture (<see cref="MTLCaptureManager"/>): commands recorded while it is active
/// are written to a <c>.gputrace</c> that Xcode's Metal debugger can open. The caller brackets exactly one frame:
/// start before the frame's command buffer is made, stop after the present commits.
/// </summary>
public sealed class MetalGpuCapture
{
    readonly string path;
    bool active;

    MetalGpuCapture(string path)
    {
        this.path = path;
        active = true;
    }

    /// <summary>The trace file the capture is writing.</summary>
    public string OutputPath => path;

    /// <summary>
    /// Starts a capture of the given command queue into a <c>.gputrace</c>. Returns null and a readable reason when the
    /// destination is unavailable (normally because the process was not launched with <c>MTL_CAPTURE_ENABLED=1</c>).
    /// </summary>
    public static MetalGpuCapture? TryStart(MTLCommandQueue queue, out string? error)
    {
        error = null;
        string path = DefaultPath();
        if (!path.EndsWith(".gputrace", StringComparison.OrdinalIgnoreCase)) path += ".gputrace";
        path = Path.GetFullPath(path);
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
        catch (Exception e)
        {
            error = $"cannot create '{Path.GetDirectoryName(path)}': {e.Message}";
            return null;
        }
        var manager = MTLCaptureManager.SharedCaptureManager;
        if (manager.IsCapturing)
        {
            error = "a Metal capture is already running";
            return null;
        }
        if (!manager.SupportsDestination(MTLCaptureDestination.GPUTraceDocument))
        {
            error = "the GPU trace document destination is unavailable (launch with MTL_CAPTURE_ENABLED=1)";
            return null;
        }
        // The descriptor and URL retain what they need once StartCapture returns; dispose our wrappers afterwards.
        var descriptor = new MTLCaptureDescriptor();
        var url = NSURL.FileURLWithPath((NSString)path);
        var captureObject = new NSObject((nint)queue);
        NSError nsError = default;
        bool started = false;
        try
        {
            descriptor.CaptureObject = captureObject;
            descriptor.Destination = MTLCaptureDestination.GPUTraceDocument;
            descriptor.OutputURL = url;
            started = manager.StartCapture(descriptor, ref nsError);
            if (!started)
            {
                string reason = nsError.NativePtr != 0 ? nsError.LocalizedDescription.ToString() ?? "unknown error" : "unknown error";
                error = $"start failed: {reason} (launch with MTL_CAPTURE_ENABLED=1)";
            }
        }
        catch (Exception e)
        {
            error = $"start failed: {e.Message} (launch with MTL_CAPTURE_ENABLED=1)";
        }
        finally
        {
            descriptor.Dispose();
            url.Dispose();
            if (nsError.NativePtr != 0) nsError.Dispose();
        }
        return started ? new MetalGpuCapture(path) : null;
    }

    /// <summary>Stops the capture and leaves the trace for Xcode.</summary>
    public void Stop()
    {
        if (!active) return;
        active = false;
        MTLCaptureManager.SharedCaptureManager.StopCapture();
    }

    /// <summary>Stops the capture and removes the partially written trace (the frame was skipped).</summary>
    public void Abort()
    {
        if (!active) return;
        Stop();
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The default trace folder: ~/Documents/Meitou Captures, with a timestamped name.</summary>
    public static string DefaultPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(root)) root = Directory.GetCurrentDirectory();
        return Path.Combine(root, "Meitou Captures", $"meitou-{DateTime.Now:yyyyMMdd-HHmmss-fff}.gputrace");
    }
}

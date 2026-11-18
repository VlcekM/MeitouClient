using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Meitou.Steam;

namespace Meitou.Launcher;

/// <summary>
/// Subscribes to Workshop items in a child process (<see cref="WorkshopSubscribeChild"/>) and reads its answer. Every way it can go wrong (no
/// child, a crash, no answer in time) ends as a result with a reason, so the caller can offer the Workshop pages instead.
/// </summary>
static class WorkshopSubscribeRunner
{
    /// <summary>
    /// How long the child gets: Steam's answers and the wait for the downloads (<see cref="WorkshopSubscribe"/>) fit well inside it.
    /// </summary>
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Subscribes to <paramref name="items"/> through the Steam API library at <paramref name="libraryPath"/>.
    /// </summary>
    public static async Task<WorkshopSubscribeResult> RunAsync(string libraryPath, IReadOnlyList<ulong> items)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? "")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add(WorkshopSubscribeChild.Argument);
        start.ArgumentList.Add(libraryPath);
        foreach (ulong id in items) start.ArgumentList.Add(id.ToString(CultureInfo.InvariantCulture));

        try
        {
            using var child = Process.Start(start);
            if (child is null) return Failed("Subscribing could not be started.");

            return await Follow(child);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return Failed($"Subscribing could not be started: {e.Message}");
        }
    }

    /// <summary>
    /// Reads the child's lines until it ends, or ends it after <see cref="Timeout"/>, and turns them into the result.
    /// </summary>
    static async Task<WorkshopSubscribeResult> Follow(Process child)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        string? problem = null;
        List<WorkshopSubscription> subscriptions = [];
        try
        {
            while (await child.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith(WorkshopSyncChild.Prefix, StringComparison.Ordinal)) continue;

                string[] parts = line[WorkshopSyncChild.Prefix.Length..].Split(' ', 4);
                if (parts is ["problem", ..]) problem = string.Join(' ', parts[1..]);
                else if (Subscription(parts) is { } subscription) subscriptions.Add(subscription);
            }

            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(child);
            return Failed("Steam did not answer in time.");
        }

        if (problem is not null) return Failed(problem);

        return subscriptions.Count > 0 ? new(null, subscriptions) : Failed($"Subscribing ended unexpectedly (exit code {child.ExitCode}).");
    }

    /// <summary>
    /// The child's <c>item &lt;id&gt; &lt;installed&gt; [problem]</c> line; null for another line.
    /// </summary>
    static WorkshopSubscription? Subscription(string[] parts)
    {
        if (parts is not ["item", var id, var installed, ..] || !ulong.TryParse(id, CultureInfo.InvariantCulture, out ulong item)) return null;

        return new(item, parts.Length > 3 ? parts[3] : null, installed == "1");
    }

    /// <summary>
    /// Ends a child that ran out of time.
    /// </summary>
    static void Kill(Process child)
    {
        try
        {
            child.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // It ended by itself meanwhile.
        }
    }

    /// <summary>
    /// A result for subscribing that could not run.
    /// </summary>
    static WorkshopSubscribeResult Failed(string problem) => new(problem, []);
}

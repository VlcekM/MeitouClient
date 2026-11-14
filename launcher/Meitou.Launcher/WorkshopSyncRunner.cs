using System.Diagnostics;
using Meitou.Steam;

namespace Meitou.Launcher;

/// <summary>
/// Runs a Workshop sync in a child process (<see cref="WorkshopSyncChild"/>), one at a time, and lets the window stop it. Its own process, so Steam
/// stops counting the account as playing Kenshi when the sync ends.
/// </summary>
sealed class WorkshopSyncRunner
{
    /// <summary>
    /// The child that is syncing; null when none is.
    /// </summary>
    Process? _child;

    /// <summary>
    /// True while a sync runs.
    /// </summary>
    public bool Running => _child is not null;

    /// <summary>
    /// Starts the child and follows it; <paramref name="report"/> gets the installed and total item counts as the child prints them. A stop ends
    /// with the last counts seen and <see cref="WorkshopSyncResult.Stopped"/> set.
    /// </summary>
    public async Task<WorkshopSyncResult> RunAsync(Action<int, int> report)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? "") { RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add(WorkshopSyncChild.Argument);
        try
        {
            using var child = Process.Start(start);
            if (child is null) return Failed("The Workshop sync could not be started.");

            _child = child;
            return await Follow(child, report);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Failed($"The Workshop sync could not be started: {e.Message}");
        }
        finally
        {
            _child = null;
        }
    }

    /// <summary>
    /// Ends the running sync, if any, by ending its process; Steam then goes on downloading by itself.
    /// </summary>
    public void Stop()
    {
        try
        {
            _child?.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It ended by itself meanwhile.
        }
    }

    /// <summary>
    /// Reads the child's lines until it ends and turns them into the result.
    /// </summary>
    static async Task<WorkshopSyncResult> Follow(Process child, Action<int, int> report)
    {
        (int Installed, int Total) last = (0, 0);
        WorkshopSyncResult? final = null;
        while (await child.StandardOutput.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith(WorkshopSyncChild.Prefix, StringComparison.Ordinal)) continue;

            string[] parts = line[WorkshopSyncChild.Prefix.Length..].Split(' ', 4);
            if (parts is ["progress", var installed, var total] && int.TryParse(installed, out int i) && int.TryParse(total, out int t))
            {
                last = (i, t);
                report(i, t);
            }
            else final = Final(parts);
        }
        await child.WaitForExitAsync();

        if (final is not null) return final;

        // No result line: the window stopped it, or it died.
        return child.ExitCode is 0 or -1 or 137 ? new WorkshopSyncResult(null, last.Total, last.Installed, 0, true)
            : Failed($"The Workshop sync ended unexpectedly (exit code {child.ExitCode}).");
    }

    /// <summary>
    /// The child's closing <c>result</c> or <c>problem</c> line; null for one that is neither.
    /// </summary>
    static WorkshopSyncResult? Final(string[] parts)
    {
        if (parts is ["problem", ..] && parts.Length > 1) return Failed(string.Join(' ', parts[1..]));
        if (parts is ["result", var total, var installed, var refused] && int.TryParse(total, out int t) && int.TryParse(installed, out int i)
            && int.TryParse(refused, out int r))
            return new WorkshopSyncResult(null, t, i, r, false);
        return null;
    }

    /// <summary>
    /// A sync that could not run.
    /// </summary>
    static WorkshopSyncResult Failed(string problem) => new(problem, 0, 0, 0, false);
}

using System.Globalization;
using System.Numerics;
using Meitou.Game.DevTools;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary><c>order move &lt;x&gt; &lt;z&gt; --wait &lt;s&gt; [--stuck &lt;s&gt;]</c>: one reply when the first ordered character arrives, makes no progress, or the time is up (docs/procedures/playtest/agent-play-api.md).</summary>
sealed partial class GameHost
{
    /// <summary>Within this many metres of the target counts as arrived.</summary>
    const float MoveWaitArriveRadius = 4f;
    /// <summary>Default game seconds without progress that make a waited move "stuck" (override with <c>--stuck</c>).</summary>
    const float MoveWaitStuckSeconds = 10f;
    /// <summary>Progress means the distance to the target fell by at least this many metres since the last progress.</summary>
    const float MoveWaitProgressStep = 1f;
    const int MoveWaitTicksPerSecond = 30;
    /// <summary>Most ticks a paused waited move steps in one frame (the same cap as <c>step</c>).</summary>
    const int MoveWaitPausedTickCap = 3000;

    sealed class ControlMoveWait(string id, CharacterId who, float x, float z, long startTick, int timeoutTicks, int stuckTicks, float dist0)
    {
        public string Id = id; public CharacterId Who = who; public float X = x, Z = z; public long StartTick = startTick; public int TimeoutTicks = timeoutTicks, StuckTicks = stuckTicks;
        public float BestDist = dist0; public long ProgressTick = startTick;
        public long RealDeadline;
    }

    readonly List<ControlMoveWait> controlMoveWaits = [];

    /// <summary>Starts a waited move after the order was given; true when the reply is deferred or already written into <paramref name="con"/>.</summary>
    void ControlMoveWaitStart(DevConsole con, float x, float z, float waitSeconds, float stuckSeconds)
    {
        var who = player.ControlOrderers();
        if (who.Count == 0 || player.ControlPosition($"c:{who[0].Slot}.{who[0].Generation}") is not { } p0) { con.Error("order: no position for the ordered character"); return; }
        var w = new ControlMoveWait(currentControlId ?? "0", who[0], x, z, session.World.Tick,
            (int)MathF.Ceiling(waitSeconds * MoveWaitTicksPerSecond), (int)MathF.Ceiling(stuckSeconds * MoveWaitTicksPerSecond), Vector2.Distance(new Vector2(p0.X, p0.Z), new Vector2(x, z)));
        if (session.Simulation.IsPaused)
        {
            // A paused game takes no tick of its own: run them here, checking every tick, but never more than a frame can bear (like `step`).
            int steps = 0, cap = Math.Min(w.TimeoutTicks + 2, MoveWaitPausedTickCap);
            while (steps < cap && !ControlMoveWaitProgress(w, out _)) { session.Step(1); steps++; }
            if (steps >= cap && w.TimeoutTicks + 2 > cap && !ControlMoveWaitProgress(w, out _))
            {
                con.Error($"order: paused wait stopped after {cap} ticks without a result (cap); the move goes on - `step` and `observe`, or unpause and use wait-for");
                return;
            }
            controlData = ControlMoveWaitLast;
            con.Print("move wait done (paused game stepped)");
            return;
        }
        w.RealDeadline = System.Diagnostics.Stopwatch.GetTimestamp() + (long)((waitSeconds * 4 + 30) * System.Diagnostics.Stopwatch.Frequency);   // safety net: a stalled world never ticks
        controlMoveWaits.Add(w);
        controlReplyDeferred = true;
        con.Print("move wait pending");
    }

    string ControlMoveWaitLast = "";

    /// <summary>One check; true (reply JSON in <see cref="ControlMoveWaitLast"/>) once arrived, stuck or timed out.</summary>
    bool ControlMoveWaitProgress(ControlMoveWait w, out string json)
    {
        json = "";
        var p = player.ControlPosition($"c:{w.Who.Slot}.{w.Who.Generation}");
        long ticks = session.World.Tick - w.StartTick;
        string result;
        float dist;
        if (p is not { } pos) { dist = -1; result = "lost"; }
        else
        {
            dist = Vector2.Distance(new Vector2(pos.X, pos.Z), new Vector2(w.X, w.Z));
            if (dist <= MoveWaitArriveRadius) result = "arrived";
            else
            {
                if (dist <= w.BestDist - MoveWaitProgressStep) { w.BestDist = dist; w.ProgressTick = session.World.Tick; }
                if (session.World.Tick - w.ProgressTick >= w.StuckTicks) result = "stuck";
                else if (ticks >= w.TimeoutTicks) result = "timeout";
                else return false;
            }
        }
        var c = CultureInfo.InvariantCulture;
        json = p is { } q
            ? $"{{\"result\":\"{result}\",\"pos\":[{q.X.ToString("0.#", c)},{q.Z.ToString("0.#", c)}],\"dist_to_target\":{dist.ToString("0.#", c)},\"ticks\":{ticks},\"id\":\"c:{w.Who.Slot}.{w.Who.Generation}\"}}"
            : $"{{\"result\":\"lost\",\"pos\":null,\"dist_to_target\":null,\"ticks\":{ticks},\"id\":\"c:{w.Who.Slot}.{w.Who.Generation}\"}}";
        ControlMoveWaitLast = json;
        return true;
    }

    /// <summary>Every poll: finish the pending waited moves.</summary>
    void ControlMoveWaitPoll()
    {
        for (int i = controlMoveWaits.Count - 1; i >= 0; i--)
        {
            var w = controlMoveWaits[i];
            if (ControlMoveWaitProgress(w, out string json)) { controlMoveWaits.RemoveAt(i); ControlWrite(w.Id, true, "", json); }
            else if (System.Diagnostics.Stopwatch.GetTimestamp() >= w.RealDeadline) { controlMoveWaits.RemoveAt(i); ControlWrite(w.Id, false, "order: the world did not tick (wait gave up)"); }
        }
    }
}

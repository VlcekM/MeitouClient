using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Profiling;
using Meitou.Simulation;
using Meitou.Simulation.Factions;

namespace Meitou.Game.DevTools;

/// <summary>
/// <c>--play-bench &lt;dir&gt;</c> (docs/bench.md, "Play benchmark"): the interactive game driven by a script instead of a player, every frame measured. Once the
/// world is up the script runs its segments (idle, camera orbit, zoom, pan, a long travel, the return, time x2 and x5, talking, trading, a container, doors, a pick-up,
/// path requests, the squad walking, a fight, looting, the interface panels cold and warm, a horizon view, the overview) and records per frame the interval, the update and the world ticks (their steps and systems, <see cref="TickProfile"/>),
/// the render thread's stages and the GPU's (<see cref="FrameProfiler"/>), the garbage collections, allocation and memory; then it writes frames.csv
/// and summary.md and quits. The frame limit and vsync are off for it.
/// </summary>
sealed class PlayBench(GameHost host)
{
    sealed record BenchSegment(string Name, double Seconds, Action<double, double>? Frame = null, Action? Begin = null);

    sealed class BenchFrame
    {
        public int Segment;
        public double T, FrameMs, UpdateMs, Busy;
        public int Ticks, Characters, Far;
        public double[] Steps = new double[TickProfile.PhaseNames.Length];
        public double[] Cpu = new double[StageClock.Names.Length];
        public double[]? Gpu;
        public double GpuTotal;
        public int Gc0, Gc1, Gc2;
        public double AllocMb, PauseMs, WorkingSetMb;
        /// <summary>This process's video memory (<c>VulkanDevice.VideoMemory</c>), MB: read every 30 frames (a driver call), carried between; NaN on Metal.</summary>
        public double VramMb = double.NaN;
        public double[] Sections = new double[BenchSections.Length];
        public double CameraMs, HandlersMs, StartMs;
        /// <summary>The frame's slowest system phase ("System.phase") and its time.</summary>
        public string TopSystem = "";
        /// <summary>Pipelines the frame created while drawing (nothing prepared them at load).</summary>
        public int LatePipelines;
        public double TopSystemMs;
    }

    BenchGcListener? _benchGc;
    double _benchGcStart;

    /// <summary>The game loop's sections a frame's wall time splits into (<see cref="BenchLap"/>): the last runs from the end of the drawing to the next frame.</summary>
    static readonly string[] BenchSections = ["events", "update", "host", "camera", "audio", "view", "begin-frame", "render", "tail"];
    internal const int BenchTail = 8;
    long _benchLapAt;
    readonly double[] _benchLaps = new double[BenchSections.Length];

    /// <summary>Ends loop section <paramref name="section"/> (<see cref="BenchSections"/>) for the benchmark; the tail goes to the frame recorded last.</summary>
    internal void BenchLap(int section)
    {
        if (host.GameOptions.PlayBench is null) return;
        long now = Stopwatch.GetTimestamp();
        double ms = _benchLapAt == 0 ? 0 : (now - _benchLapAt) * 1000.0 / Stopwatch.Frequency;
        _benchLapAt = now;
        if (section == BenchTail)
        {
            if (_benchFrames.Count > 0) _benchFrames[^1].Sections[BenchTail] = ms;
            Array.Clear(_benchLaps);
            return;
        }
        _benchLaps[section] += ms;
    }

    BenchSegment[]? _benchScript;
    readonly List<BenchFrame> _benchFrames = [];
    readonly Dictionary<(int Segment, string System), double> _benchSystems = [];
    int _benchSegment = -1;
    double _benchClock, _benchSegmentStart, _benchReadyFor;
    long _benchTickAt, _benchAllocAt;
    int _benchLateAt;
    int[] _benchGcAt = new int[3];
    TimeSpan _benchPauseAt;
    double _benchVramMb = double.NaN;
    BenchFrame? _benchCurrent;
    Vector2 _benchOrigin, _benchDirection;
    CharacterId _benchLeader;

    /// <summary>The frame's script step (before the world's update): false once the script is done. Waits for the world (no loading screen, a player character) first.</summary>
    internal bool PlayBenchFrame(double dt, FrameProfiler? profiler)
    {
        if (host.GameOptions.PlayBench is null) return true;
        if (_benchScript is null)
        {
            var players = host.Session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).ToList();
            if (host.Covered || host.TitleScreen.TitleVisible || players.Count == 0) { _benchReadyFor = 0; return true; }
            if ((_benchReadyFor += dt) < 1) return true;
            _benchLeader = (host.PlayerSquad is { Leader.IsNone: false } squad ? squad.Leader : players[0].Id);
            _benchOrigin = host.Session.Camera.Strategy.PivotXz;
            _benchDirection = _benchOrigin.LengthSquared() > 1 ? Vector2.Normalize(-_benchOrigin) : Vector2.UnitX;
            _benchScript = BenchScript();
            // MEITOU_BENCH_ONLY=settle,zoom: only those segments (iterating on one hitch).
            if (Environment.GetEnvironmentVariable("MEITOU_BENCH_ONLY") is { Length: > 0 } only)
            {
                var keep = only.Split(',');
                _benchScript = [.. _benchScript.Where(s => keep.Contains(s.Name))];
                if (_benchScript.Length == 0) { Console.WriteLine($"bench     no segment named {only}: nothing to run"); return false; }
            }
            _benchGc = new BenchGcListener();
            _benchGcStart = _benchGc.NowMs;
            if (profiler is not null) profiler.OnGpuFrame = (tag, stages, total) =>
            {
                if (tag >= 0 && tag < _benchFrames.Count) (_benchFrames[(int)tag].Gpu, _benchFrames[(int)tag].GpuTotal) = ([.. stages], total);
            };
            Console.WriteLine($"bench     the script starts: {_benchScript.Length} segments, {_benchScript.Sum(s => s.Seconds):0} s, leader {_benchLeader}, from {_benchOrigin.X:0}, {_benchOrigin.Y:0}");
            StartBenchSegment(0);
        }
        _benchClock += dt;
        if (_benchClock - _benchSegmentStart >= _benchScript[_benchSegment].Seconds)
        {
            if (_benchSegment + 1 >= _benchScript.Length) return false;
            StartBenchSegment(_benchSegment + 1);
        }
        double t = _benchClock - _benchSegmentStart;
        // A modal screen (a message box) pauses the game until a player answers: the script says which and closes it.
        if (host.Player.ModalOpen && !_benchScript[_benchSegment].Name.StartsWith("panels", StringComparison.Ordinal))
        {
            Console.WriteLine($"bench     a modal screen paused the game: {string.Join(", ", host.Player.OpenScreens)}; closed");
            host.Player.ShowUiPanel(null);
        }
        _benchScript[_benchSegment].Frame?.Invoke(t, dt);
        _benchCurrent = new BenchFrame { Segment = _benchSegment, T = _benchClock, FrameMs = dt * 1000, Characters = host.Session.CurrentSnapshot.Characters.Count, Far = host.Session.World.FarSkipped, StartMs = _benchGc?.NowMs ?? 0 };
        if (profiler is not null) profiler.Tag = _benchFrames.Count;
        host.Session.World.Profile.Clear();
        (host.Session.CameraMs, host.Session.HandlersMs) = (0, 0);
        _benchTickAt = host.Session.World.Tick;
        return true;
    }

    void StartBenchSegment(int index)
    {
        _benchSegment = index;
        _benchSegmentStart = _benchClock;
        Console.WriteLine($"bench     {_benchScript![index].Name} ({_benchScript[index].Seconds:0} s)");
        _benchScript[index].Begin?.Invoke();
    }

    /// <summary>After the world's update: its wall time and the ticks it ran.</summary>
    internal void PlayBenchUpdated(double ms)
    {
        if (_benchCurrent is not { } f) return;
        f.UpdateMs = ms;
        (f.CameraMs, f.HandlersMs) = (host.Session.CameraMs, host.Session.HandlersMs);
        f.Ticks = (int)(host.Session.World.Tick - _benchTickAt);
        var profile = host.Session.World.Profile;
        Array.Copy(profile.PhaseMs, f.Steps, f.Steps.Length);
        var systems = host.Session.World.Systems;
        for (int k = 0; k < profile.SystemMs.Length && k < systems.Count; k++)
        {
            string system = systems[k].GetType().Name;
            double value = profile.SystemMs[k];
            _benchSystems[(f.Segment, system)] = _benchSystems.GetValueOrDefault((f.Segment, system)) + value;
            if (value > f.TopSystemMs) (f.TopSystem, f.TopSystemMs) = (system, value);
        }
    }

    /// <summary>After the frame is presented: the render thread's stages (StageClock, read before the next frame starts it), the collections and memory.</summary>
    internal void PlayBenchRendered(double busyMs, int latePipelines)
    {
        if (_benchCurrent is not { } f) return;
        (f.LatePipelines, _benchLateAt) = (_benchFrames.Count > 0 ? latePipelines - _benchLateAt : 0, latePipelines);
        Array.Copy(StageClock.Ms, f.Cpu, f.Cpu.Length);
        Array.Copy(_benchLaps, f.Sections, BenchTail);
        f.Busy = busyMs;
        int[] gc = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        long alloc = GC.GetTotalAllocatedBytes(false);
        var pause = GC.GetTotalPauseDuration();
        if (_benchFrames.Count > 0)
        {
            (f.Gc0, f.Gc1, f.Gc2) = (gc[0] - _benchGcAt[0], gc[1] - _benchGcAt[1], gc[2] - _benchGcAt[2]);
            f.AllocMb = (alloc - _benchAllocAt) / 1048576.0;
            f.PauseMs = (pause - _benchPauseAt).TotalMilliseconds;
        }
        (_benchGcAt, _benchAllocAt, _benchPauseAt) = (gc, alloc, pause);
        f.WorkingSetMb = Environment.WorkingSet / 1048576.0;
        if (_benchFrames.Count % 30 == 0 && host.Gpu.Sky.Gpu is Rendering.Gpu.GpuContext vk) _benchVramMb = vk.Device.VideoMemory().Used / 1048576.0;
        f.VramMb = _benchVramMb;
        _benchFrames.Add(f);
        _benchCurrent = null;
    }

    /// <summary>The script: each segment's length and what it does each frame (t: seconds into it, dt: the frame's).</summary>
    BenchSegment[] BenchScript()
    {
        var strategy = host.Session.Camera.Strategy;
        float yaw0 = strategy.Yaw, pitch0 = strategy.Pitch, distance0 = strategy.Distance;
        Vector3 LeaderAt() => host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == _benchLeader)?.Position ?? new Vector3(_benchOrigin.X, 0, _benchOrigin.Y);
        void Look(Vector2 at, float yaw, float pitch, float distance) => strategy.Place(at, yaw, pitch, distance);
        void OnLeader() { var p = LeaderAt(); Look(new Vector2(p.X, p.Z), strategy.Yaw, pitch0, distance0); }
        List<CharacterId> Squad() => [.. host.Session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).Select(c => c.Id)];
        void Order(SimCommand command) => host.Session.World.Commands.Enqueue(command with { Tick = host.Session.World.Tick });
        // The nearest of what the script interacts with, said on the console (or that there is none within reach).
        T? Nearest<T>(string what, IEnumerable<T> items, Func<T, Vector3> at) where T : class
        {
            var leader = LeaderAt();
            var found = items.OrderBy(i => Vector3.DistanceSquared(at(i), leader)).FirstOrDefault();
            float d = found is null ? float.NaN : Vector3.Distance(at(found), leader);
            Console.WriteLine(found is null || d > 1500 ? $"bench     no {what} within reach" : string.Create(CultureInfo.InvariantCulture, $"bench     {what} {d:0} away"));
            return d <= 1500 ? found : null;
        }
        bool Alive(CharacterSnapshot c) => c.Body is not { Dead: true } and not { Unconscious: true };
        // An interaction asked again now and then (a walk that outlasts the approach gives up), until it opens.
        Action<double, double> Retry(Func<bool> open, Action again)
        {
            double next = 3;
            return (t, dt) => { OnLeader(); if (t >= next && !open()) { again(); next += 3; } };
        }
        Vector2 pan = Vector2.Zero;
        var random = new Random(1);
        CharacterSnapshot? talkee = null, trader = null, body = null;
        Simulation.Locks.ContainerSpot? box = null;
        return
        [
            new("settle", 8),
            new("idle", 10, Begin: () => { host.Player.Follow = null; Look(_benchOrigin, yaw0, pitch0, distance0); }),
            new("orbit", 12, (t, dt) => strategy.Rotate((float)(dt * 2 * Math.PI / 12))),
            new("zoom", 10, (t, dt) =>
            {
                float k = (float)(t < 5 ? t / 5 : 1 - (t - 5) / 5);
                Look(_benchOrigin, strategy.Yaw, pitch0, distance0 + (Data.World.KenshiCamera.MaxDistance - distance0) * k);
            }),
            new("pan", 12, (t, dt) => { pan += _benchDirection * (float)(400 * dt); Look(_benchOrigin + pan, strategy.Yaw, pitch0, distance0); },
                Begin: () => pan = Vector2.Zero),
            new("travel", 30, (t, dt) => { pan += _benchDirection * (float)(3000 * dt); Look(_benchOrigin + pan, strategy.Yaw, pitch0, distance0); }),
            new("return", 10, Begin: () => Look(_benchOrigin, yaw0, pitch0, distance0)),
            new("speed-x2", 10, (t, dt) => OnLeader(), Begin: () => host.Session.Simulation.SetSpeed(2)),
            new("speed-x5", 15, (t, dt) => OnLeader(), Begin: () => host.Session.Simulation.SetSpeed(5)),
            new("talk", 10, Retry(() => host.Session.CurrentSnapshot.Conversation is not null, () => { if (talkee is not null) Order(new Simulation.Dialogue.TalkOrder(_benchLeader, talkee.Id) { Tick = 0 }); }), Begin: () =>
            {
                host.Session.Simulation.SetSpeed(1);
                talkee = Nearest("talker", host.Session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && Alive(c) && host.Session.World.CanTalk(c.Id, _benchLeader)).ToList(), c => c.Position);
                if (talkee is not null) Order(new Simulation.Dialogue.TalkOrder(_benchLeader, talkee.Id) { Tick = 0 });
            }),
            new("trade", 12, Retry(() => host.Session.CurrentSnapshot.Trade is not null, () => { if (trader is not null) Order(new Simulation.Trading.OpenTrade(_benchLeader, trader.Id) { Tick = 0 }); }), Begin: () =>
            {
                Order(new Simulation.Dialogue.EndConversation { Tick = 0 });
                trader = Nearest("trader", host.Session.CurrentSnapshot.Characters.Where(c => c.IsTrader && Alive(c)).ToList(), c => c.Position);
                if (trader is not null) Order(new Simulation.Trading.OpenTrade(_benchLeader, trader.Id) { Tick = 0 });
            }),
            new("container", 10, (t, dt) => OnLeader(), Begin: () =>
            {
                Order(new Simulation.Trading.CloseTrade { Tick = 0 });
                box = Nearest("container", host.Session.CurrentSnapshot.Containers.Where(c => !c.Locked).ToList(), c => c.Position);
                if (box is not null) Order(new Simulation.Items.OpenContainerOrder([_benchLeader], box.Id) { Tick = 0 });
            }),
            new("doors", 8, (t, dt) => OnLeader(), Begin: () =>
            {
                Order(new Simulation.Items.CloseContainer { Tick = 0 });
                var leader = LeaderAt();
                var doors = host.Session.CurrentSnapshot.DoorPicks.OrderBy(d => Vector3.DistanceSquared(d.Position, leader)).Take(3).ToList();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"bench     doors: {doors.Count}, the nearest {(doors.Count > 0 ? Vector3.Distance(doors[0].Position, leader) : float.NaN):0} away"));
                foreach (var door in doors) Order(new Simulation.Locks.DoorOrder([_benchLeader], door.Id) { Tick = 0 });
            }),
            new("pick-up", 8, (t, dt) => OnLeader(), Begin: () =>
            {
                var leader = LeaderAt();
                var items = host.Session.CurrentSnapshot.GroundItems;
                if (items.Count == 0) { Console.WriteLine("bench     no ground item"); return; }
                var item = items.MinBy(i => Vector3.DistanceSquared(i.Position, leader));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"bench     ground item {item.Name} {Vector3.Distance(item.Position, leader):0} away"));
                Order(new Simulation.Items.PickUpItemOrder([_benchLeader], item.Id) { Tick = 0 });
            }),
            // Path requests: every half second the squad, or each member apart, is sent 20 to 200 m away in a random direction (some places it cannot reach).
            new("paths", 20, (t, dt) =>
            {
                OnLeader();
                if ((int)((t - dt) * 2) == (int)(t * 2)) return;
                var at = LeaderAt();
                Vector3 Somewhere() { double a = random.NextDouble() * 2 * Math.PI, r = 200 + random.NextDouble() * 1800; return at + new Vector3((float)(Math.Cos(a) * r), 0, (float)(Math.Sin(a) * r)); }
                var squad = Squad();
                if ((int)(t * 2) % 3 == 0) Order(new MoveOrder(squad, Somewhere()) { Tick = 0 });
                else foreach (var id in squad) Order(new MoveOrder([id], Somewhere()) { Tick = 0 });
            }),
            new("walk", 15, (t, dt) => OnLeader(), Begin: () =>
            {
                host.Session.Simulation.SetSpeed(1);
                var at = LeaderAt();
                host.Session.World.Commands.Enqueue(new MoveOrder(Squad(), at + new Vector3(_benchDirection.X * 600, 0, _benchDirection.Y * 600)) { Tick = host.Session.World.Tick });
            }),
            new("fight", 25, (t, dt) => OnLeader(), Begin: () =>
            {
                var world = host.Session.World;
                var at = LeaderAt();
                if (host.DebugMenu.DebugRecord(host.GameOptions.SceneNpc, character: true) is not { } npc) { Console.WriteLine($"bench     no character record matches '{host.GameOptions.SceneNpc}': no fight"); return; }
                for (int k = 0; k < 6; k++)
                    world.Commands.Enqueue(new DebugSpawn(npc.Id, new Vector2(at.X + 50 + 15 * (k % 3), at.Z - 20 + 20 * (k / 3))) { Tick = world.Tick });
                Console.WriteLine($"bench     fight: six {npc.Name} spawned beside the squad");
            }),
            new("fight-on", 0.1, Begin: () =>
            {
                var at = LeaderAt();
                if (host.Session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && Vector3.Distance(c.Position, at) < 150).OrderBy(c => Vector3.DistanceSquared(c.Position, at)).FirstOrDefault() is { } enemy)
                    host.Session.World.Commands.Enqueue(new Simulation.Combat.AttackOrder(Squad(), enemy.Id) { Tick = host.Session.World.Tick });
            }),
            new("loot", 8, Retry(() => !host.Session.CurrentSnapshot.OpenLoot.IsNone, () => { if (body is not null) Order(new Simulation.Carrying.OpenLootOrder([_benchLeader], body.Id) { Tick = 0 }); }), Begin: () =>
            {
                body = Nearest("downed body", host.Session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && !Alive(c)).ToList(), c => c.Position);
                if (body is not null) Order(new Simulation.Carrying.OpenLootOrder([_benchLeader], body.Id) { Tick = 0 });
            }),
            new("panels", 12, (t, dt) =>
            {
                string? panel = t < 3 ? "inventory" : t < 6 ? "stats" : t < 9 ? "overview" : null;
                if (panel != _benchPanel)
                {
                    _benchPanel = panel;
                    try { host.Player.ShowUiPanel(panel); }
                    catch (InvalidOperationException e) { Console.WriteLine($"bench     panel {panel}: {e.Message}"); }
                }
            }, Begin: () => _benchPanel = "-"),
            // The same panels again (what the first opening built is warm), with the limbs and the build menu.
            new("panels-warm", 15, (t, dt) =>
            {
                string? panel = t < 2.5 ? "inventory" : t < 5 ? "stats" : t < 7.5 ? "overview" : t < 10 ? "limbs" : t < 12.5 ? "build" : null;
                if (panel != _benchPanel)
                {
                    _benchPanel = panel;
                    try { host.Player.ShowUiPanel(panel); }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Console.WriteLine($"bench     panel {panel}: {e.Message}"); }
                }
            }, Begin: () => _benchPanel = "-"),
            // The far grazing view: the most terrain, foliage and objects in sight.
            new("horizon", 12, (t, dt) => Look(_benchOrigin, yaw0 + (float)(t * 2 * Math.PI / 12), 0.12f, 900), Begin: () => host.Player.ShowUiPanel(null)),
            new("overview", 8, Begin: () =>
            {
                host.Player.ShowUiPanel(null);
                Look(_benchOrigin, yaw0, 1.2f, Data.World.KenshiCamera.MaxDistance);
            }),
            // Every player character in turn, twice, 6 s each, as a double click on its portrait does (a character flight with "Character flights" on, else a cut).
            new("flights", 6 * 2 * Math.Max(1, host.Session.CurrentSnapshot.PlayerSquads.Sum(s => s.Members.Count)), (t, dt) =>
            {
                var members = host.Session.CurrentSnapshot.PlayerSquads.SelectMany(s => s.Members).ToList();
                int hop = (int)(t / 6);
                if (members.Count == 0 || hop == _flightHop) return;
                _flightHop = hop;
                var id = members[hop % members.Count];
                if (host.Player.FlyTo?.Invoke(id) != true) host.Player.Follow = id;
                Console.WriteLine($"bench     flight {hop + 1} to {id}");
            }, Begin: () => { host.Player.ShowUiPanel(null); _flightHop = -1; }),
        ];
    }

    string? _benchPanel;
    int _flightHop = -1;

    /// <summary>Writes frames.csv and summary.md into the bench directory (after the loop).</summary>
    internal void WritePlayBench(Silk.NET.Maths.Vector2D<int> size)
    {
        if (host.GameOptions.PlayBench is not { } dir || _benchScript is null) return;
        Directory.CreateDirectory(dir);
        var ci = CultureInfo.InvariantCulture;
        var stages = StageClock.Names;
        var csv = new StringBuilder();
        csv.Append("frame,segment,t,frame_ms,update_ms,busy_ms,ticks,characters,far,gpu_total_ms,gc0,gc1,gc2,alloc_mb,gc_pause_ms,working_set_mb,vram_mb");
        foreach (var s in BenchSections) csv.Append(",loop_").Append(s);
        csv.Append(",control_camera_ms,control_handlers_ms,top_system,top_system_ms,late_pipelines");
        foreach (var s in TickProfile.PhaseNames) csv.Append(",sim_").Append(s.Replace(' ', '-'));
        foreach (var s in stages) csv.Append(",cpu_").Append(s);
        foreach (var s in stages) csv.Append(",gpu_").Append(s);
        csv.AppendLine();
        for (int i = 0; i < _benchFrames.Count; i++)
        {
            var f = _benchFrames[i];
            csv.Append(ci, $"{i},{_benchScript[f.Segment].Name},{f.T:0.000},{f.FrameMs:0.00},{f.UpdateMs:0.00},{f.Busy:0.00},{f.Ticks},{f.Characters},{f.Far},{f.GpuTotal:0.00},{f.Gc0},{f.Gc1},{f.Gc2},{f.AllocMb:0.00},{f.PauseMs:0.00},{f.WorkingSetMb:0},{(double.IsNaN(f.VramMb) ? "" : f.VramMb.ToString("0", ci))}");
            foreach (var v in f.Sections) csv.Append(ci, $",{v:0.000}");
            csv.Append(ci, $",{f.CameraMs:0.000},{f.HandlersMs:0.000},{f.TopSystem},{f.TopSystemMs:0.000},{f.LatePipelines}");
            foreach (var v in f.Steps) csv.Append(ci, $",{v:0.000}");
            foreach (var v in f.Cpu) csv.Append(ci, $",{v:0.000}");
            for (int s = 0; s < stages.Length; s++) csv.Append(ci, $",{(f.Gpu is { } g2 && s < g2.Length ? g2[s] : double.NaN):0.000}");
            csv.AppendLine();
        }
        File.WriteAllText(Path.Combine(dir, "frames.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(dir, "summary.md"), BenchSummary(size) + GcSummary());
        _benchGc?.Dispose();
        Console.WriteLine($"bench     {_benchFrames.Count} frames: {Path.GetFullPath(Path.Combine(dir, "summary.md"))}");
    }

    /// <summary>The collections (generation, reason, pause) and the allocation samples by type, from <see cref="BenchGcListener"/>.</summary>
    string GcSummary()
    {
        if (_benchGc is null) return "";
        var ci = CultureInfo.InvariantCulture;
        var (allocated, collections) = _benchGc.Take();
        var sb = new StringBuilder("\n## Garbage collector\n\n");
        double seconds = (_benchGc.NowMs - _benchGcStart) / 1000;
        foreach (var g2 in collections.GroupBy(c => c.Generation).OrderBy(g2 => g2.Key))
        {
            var p = g2.Select(c => c.PauseMs).Order().ToList();
            sb.Append(ci, $"- gen{g2.Key}: {p.Count} collections, pause median {p[p.Count / 2]:0.0} ms, p95 {p[Math.Min(p.Count - 1, (int)(0.95 * p.Count))]:0.0}, max {p[^1]:0.0}, total {p.Sum():0} ms; reasons {string.Join(", ", g2.GroupBy(c => c.Reason).Select(r => $"{r.Key}x{r.Count()}"))}\n");
        }
        // The longer pauses with the frame (and segment) they fell in: the last frame started before the pause.
        var starts = _benchFrames.Select(f => f.StartMs).ToArray();
        var longer = collections.Where(c => c.PauseMs >= 5).OrderByDescending(c => c.PauseMs).Take(30).ToList();
        if (longer.Count > 0)
        {
            sb.Append("\nPauses of 5 ms or more, longest first (frame: the one running when it began):\n\n| frame | segment | gen | reason | pause ms |\n|---|---|---|---|---|\n");
            foreach (var c in longer)
            {
                int i = Array.BinarySearch(starts, c.AtMs);
                if (i < 0) i = ~i - 1;
                string segment = i >= 0 && i < _benchFrames.Count ? _benchScript![_benchFrames[i].Segment].Name : "-";
                sb.Append(ci, $"| {i} | {segment} | {c.Generation} | {c.Reason} | {c.PauseMs:0.0} |\n");
            }
        }
        long total = allocated.Values.Sum();
        sb.Append(ci, $"\nAllocation samples: about {total / 1048576.0:0} MB over {seconds:0} s ({total / 1048576.0 / Math.Max(seconds, 1):0} MB/s). The 40 largest types:\n\n| type | MB | share |\n|---|---|---|\n");
        foreach (var (type, bytes) in allocated.OrderByDescending(kv => kv.Value).Take(40))
            sb.Append(ci, $"| `{type}` | {bytes / 1048576.0:0} | {100.0 * bytes / Math.Max(total, 1):0.0}% |\n");
        sb.Append("\nGC reasons: 0 small allocation, 1 induced, 2 low memory, 3 empty, 4 large allocation, 5 out of space (small), 6 out of space (large), 7 induced not forced.\n");
        return sb.ToString();
    }

    string BenchSummary(Silk.NET.Maths.Vector2D<int> sz)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        static double P(List<double> v, double q) { if (v.Count == 0) return 0; var s = v.Order().ToList(); return s[Math.Min(s.Count - 1, (int)(q * s.Count))]; }
        var stages = StageClock.Names;
        string device = host.Gpu.Sky.Gpu is Rendering.Gpu.GpuContext vk ? vk.Device.DeviceName : "Metal";
        sb.Append(ci, $"# Play benchmark\n\n{_benchFrames.Count} frames, {sz.X}x{sz.Y}, {device}, upscaler {host.WorldOptions.Post.Upscale.Kind} at {host.WorldOptions.Post.Upscale.EffectiveScale:0.00}; frame limit and vsync off.\n\n");
        if (host.Gpu.Shadow is { Meitou: true } shadowPass) sb.Append(ci, $"Shadows: {shadowPass.DescribeMeitou()}.\n\n");
        sb.Append("| segment | frames | fps | frame ms p50 | p95 | p99 | max | update ms | ticks/frame | sim ms/tick | gpu ms | render cpu ms | alloc MB/s | gen2 |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        for (int s = 0; s < _benchScript!.Length; s++)
        {
            var fs = _benchFrames.Where(f => f.Segment == s).ToList();
            if (fs.Count < 2) continue;
            var ms = fs.Select(f => f.FrameMs).ToList();
            double seconds = ms.Sum() / 1000, ticks = fs.Sum(f => f.Ticks), sim = fs.Sum(f => f.Steps.Sum());
            var gpuMs = fs.Where(f => f.Gpu is not null).Select(f => f.GpuTotal).ToList();
            sb.Append(ci, $"| {_benchScript[s].Name} | {fs.Count} | {fs.Count / seconds:0} | {P(ms, 0.5):0.0} | {P(ms, 0.95):0.0} | {P(ms, 0.99):0.0} | {ms.Max():0.0} | {fs.Average(f => f.UpdateMs):0.00} | {ticks / fs.Count:0.0} | {(ticks > 0 ? sim / ticks : 0):0.00} | {(gpuMs.Count > 0 ? gpuMs.Average() : 0):0.00} | {fs.Average(f => f.Cpu.Sum()):0.00} | {fs.Sum(f => f.AllocMb) / seconds:0} | {fs.Sum(f => f.Gc2)} |\n");
        }
        // Per segment: the stages that cost most on each side, the world tick's steps, the systems.
        for (int s = 0; s < _benchScript.Length; s++)
        {
            var fs = _benchFrames.Where(f => f.Segment == s).ToList();
            if (fs.Count < 2) continue;
            sb.Append(ci, $"\n## {_benchScript[s].Name}\n\n");
            string Top(Func<BenchFrame, double[]?> pick, string[] names, int n) => string.Join(", ", Enumerable.Range(0, names.Length)
                .Select(i => (Name: names[i], Ms: fs.Where(f => pick(f) is { } a && i < a.Length && double.IsFinite(a[i])).Select(f => pick(f)![i]).DefaultIfEmpty(0).Average()))
                .OrderByDescending(x => x.Ms).Take(n).Where(x => x.Ms >= 0.01).Select(x => string.Create(ci, $"{x.Name} {x.Ms:0.00}")));
            sb.Append("- game loop ms/frame: ").Append(Top(f => f.Sections, BenchSections, 9)).Append('\n');
            sb.Append("- render thread ms/frame: ").Append(Top(f => f.Cpu, stages, 8)).Append('\n');
            sb.Append("- GPU ms/frame: ").Append(Top(f => f.Gpu, stages, 8)).Append('\n');
            sb.Append("- world tick ms/frame: ").Append(Top(f => f.Steps, TickProfile.PhaseNames, 6)).Append('\n');
            double frames = fs.Count;
            var systems = _benchSystems.Where(kv => kv.Key.Segment == s).OrderByDescending(kv => kv.Value).Take(8)
                .Select(kv => string.Create(ci, $"{kv.Key.System} {kv.Value / frames:0.00}"));
            sb.Append("- systems ms/frame: ").Append(string.Join(", ", systems)).Append('\n');
        }
        // Stutters: frames over twice their segment's median and 20 ms, the worst first, with what was largest in them.
        sb.Append("\n## Stutters\n\nFrames over twice their segment's median frame time and over 20 ms, worst first: what the frame held most (render-thread stage, world tick, collections).\n\n");
        var medians = Enumerable.Range(0, _benchScript.Length).Select(s => P(_benchFrames.Where(f => f.Segment == s).Select(f => f.FrameMs).ToList(), 0.5)).ToArray();
        var hitches = _benchFrames.Select((f, i) => (f, i)).Where(x => x.f.FrameMs > Math.Max(20, 2 * medians[x.f.Segment])).OrderByDescending(x => x.f.FrameMs).ToList();
        sb.Append(ci, $"{hitches.Count} frames; by segment: {string.Join(", ", hitches.GroupBy(h => _benchScript[h.f.Segment].Name).Select(g2 => $"{g2.Key} {g2.Count()}"))}.\n\n");
        sb.Append("| frame | segment | t s | frame ms | largest loop sections | ticks | largest render stages | largest tick steps | gc |\n|---|---|---|---|---|---|---|---|---|\n");
        foreach (var (f, i) in hitches.Take(40))
        {
            string cpu = string.Join(", ", f.Cpu.Select((v, k) => (v, k)).OrderByDescending(x => x.v).Take(3).Select(x => string.Create(ci, $"{stages[x.k]} {x.v:0.0}")));
            string sim = string.Join(", ", f.Steps.Select((v, k) => (v, k)).OrderByDescending(x => x.v).Take(2).Where(x => x.v > 0.1).Select(x => string.Create(ci, $"{TickProfile.PhaseNames[x.k]} {x.v:0.0}")));
            if (f.TopSystemMs > 0.1) sim += string.Create(ci, $" ({f.TopSystem} {f.TopSystemMs:0.0})");
            string gc = f.Gc0 + f.Gc1 + f.Gc2 == 0 ? "" : string.Create(ci, $"gen0 {f.Gc0}, gen1 {f.Gc1}, gen2 {f.Gc2}, pause {f.PauseMs:0.0} ms");
            // The frame's interval holds the previous frame's tail and this one's sections.
            var tail = i > 0 ? _benchFrames[i - 1].Sections[BenchTail] : 0;
            string loop = string.Join(", ", f.Sections.Take(BenchTail).Select((v, k) => (v, k)).Append((tail, BenchTail)).OrderByDescending(x => x.Item1).Take(3)
                .Select(x => string.Create(ci, $"{BenchSections[x.Item2]} {x.Item1:0.0}")));
            sb.Append(ci, $"| {i} | {_benchScript[f.Segment].Name} | {f.T:0.0} | {f.FrameMs:0.0} | {loop} | {f.Ticks} | {cpu} | {sim} | {gc} |\n");
        }
        var last = _benchFrames.Count > 0 ? _benchFrames[^1] : null;
        sb.Append(ci, $"\nCollections over the run: gen0 {_benchFrames.Sum(f => f.Gc0)}, gen1 {_benchFrames.Sum(f => f.Gc1)}, gen2 {_benchFrames.Sum(f => f.Gc2)}, pauses {_benchFrames.Sum(f => f.PauseMs):0} ms; allocated {_benchFrames.Sum(f => f.AllocMb):0} MB; working set at the end {last?.WorkingSetMb:0} MB.\n");
        var vram = _benchFrames.Where(f => !double.IsNaN(f.VramMb)).ToList();
        if (vram.Count > 0) sb.Append(ci, $"Video memory: peak {vram.Max(f => f.VramMb):0} MB, at the end {vram[^1].VramMb:0} MB.\n");
        return sb.ToString();
    }
}
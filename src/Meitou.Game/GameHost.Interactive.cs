using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Engine;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Simulation;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Display;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineKey = Meitou.Engine.Input.Key;
using EngineButton = Meitou.Engine.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;

namespace Meitou.Game;

sealed partial class GameHost
{
    int Interactive()
    {
        bool vsync = g.VSync ?? config.VSync;
        int fpsLimit = g.FpsLimit ?? config.FpsLimit;
        using var display = new VulkanDisplay(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            Title = "Meitou",
            WindowState = WindowState.Maximized,
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
        }, vsync, streamline: WantsDlss());
        streamline = display.Streamline;
        var window = display.Window!;
        Boot(display, interactive: true);
        var overlay = DebugOverlay.TryCreate(display.Context);
        // The viewer's debug overlays (docs/engine.md): F10 the key list, F11 the frame statistics, F12 the profiler chart.
        var profiler = overlay is null ? null : new FrameProfiler(display.Context, () => display.Context.GpuFrameMs);
        // Shift+F1 upwards: the viewer's Faithful / Meitou switches (its F1 upwards; plain F2..F4 and F8 are the game's speed and screenshot keys).
        var switches = WorldFrame.LiveSwitches(o, () => gpu, () => camera, () => render);
        var panel = overlay is null ? null : WorldFrame.CreateSettingsPanel(overlay, gpu, render, switches: switches);
        if (panel is not null)
            foreach (var slider in panel.Sliders)
                if (config.Graphics.TryGetValue(slider.Label, out float v) && !(o.Post.Upscale.Explicit && WorldFrame.UpscalerSliders.Contains(slider.Label)))
                    slider.Set(Math.Clamp(v, slider.Min, slider.Max));
        // The switches as saved, unless the command line chose them (--meitou / --faithful).
        if (panel is not null && !Environment.GetCommandLineArgs().Any(a => a is "--meitou" or "--faithful"))
            foreach (var toggle in panel.Toggles)
                if (config.Graphics.TryGetValue(SwitchKey(toggle.Label), out float v) && (v >= 0.5f) != toggle.Get()) toggle.Set(v >= 0.5f);
        var keyItems = DebugOverlay.KeyItems(GameOptions.Usage);
        keyItems.RemoveAll(i => i.StartsWith("Shift+F1..", StringComparison.Ordinal));   // listed one by one instead
        keyItems.AddRange(switches.Select((e, i) => $"Shift+F{i + 1} {e.Name.ToLowerInvariant()}"));
        var stats = new List<string>();
        bool statsVisible = false;

        var silkInput = Silk.NET.Input.InputWindowExtensions.CreateInput(window);
        Vector2? lastMouse = null;
        bool panelDrag = false, shiftDown = false;
        Vector2 Pixels(Vector2 p) => p * new Vector2(window.FramebufferSize.X / (float)Math.Max(window.Size.X, 1), window.FramebufferSize.Y / (float)Math.Max(window.Size.Y, 1));
        foreach (var kb in silkInput.Keyboards)
        {
            kb.KeyDown += (_, k, _) =>
            {
                if (k is SilkKey.ShiftLeft or SilkKey.ShiftRight) shiftDown = true;
                if (KeyMap.Map(k) is { } key)
                {
                    if (shiftDown && key - EngineKey.F1 is >= 0 and < 9 && key - EngineKey.F1 < switches.Count)
                    {
                        var e = switches[key - EngineKey.F1];
                        e.IsMeitou = !e.IsMeitou;
                        Console.WriteLine($"{e.Name,-18}{e.State}: {e.Note}");
                        return;
                    }
                    session.Input.SetKey(key, true);
                    player.Key(key, shiftDown);
                    SandboxKey(key);
                }
            };
            kb.KeyUp += (_, k, _) =>
            {
                if (k is SilkKey.ShiftLeft or SilkKey.ShiftRight) shiftDown = false;
                if (KeyMap.Map(k) is { } key) session.Input.SetKey(key, false);
            };
        }
        foreach (var mouse in silkInput.Mice)
        {
            mouse.MouseDown += (m, b) =>
            {
                if (b == SilkButton.Left && panel?.MouseDown(Pixels(m.Position)) == true) { panelDrag = true; return; }
                if (panel?.Contains(Pixels(m.Position)) == true) return;
                if (KeyMap.Map(b) is { } button)
                {
                    if (player.MouseDown(button, Pixels(m.Position), shiftDown)) return;
                    session.Input.SetMouseButton(button, true);
                }
            };
            mouse.MouseUp += (m, b) =>
            {
                if (KeyMap.Map(b) is { } released) player.MouseUp(released, Pixels(m.Position), shiftDown);
                panel?.MouseUp();
                panelDrag = false;
                if (KeyMap.Map(b) is { } button) session.Input.SetMouseButton(button, false);
            };
            mouse.MouseMove += (_, p) =>
            {
                session.Input.SetMousePosition(p.X, p.Y);
                player.MouseMove(Pixels(p));
                if (panel?.MouseMove(Pixels(p)) == true || panelDrag) { lastMouse = p; return; }
                if (lastMouse is { } last) session.Input.AddMouseDelta(p.X - last.X, p.Y - last.Y);
                lastMouse = p;
            };
            mouse.Scroll += (m, wheel) => { if (panel?.Contains(Pixels(m.Position)) != true) session.Input.AddWheel(wheel.Y); };
        }
        Console.WriteLine(GameOptions.Usage[GameOptions.Usage.IndexOf("Keys:", StringComparison.Ordinal)..]);
        Console.WriteLine($"display   vsync {(vsync ? "on" : "off")}, frame limit {(vsync || fpsLimit <= 0 ? "none" : fpsLimit + " fps")}, control tick {session.Ticks.TickRate:0} Hz, world tick {SimulationClock.TickRate:0} per game second; settings in {config.Path}");

        var frame = Stopwatch.StartNew();
        double last = 0, titleTimer = 0, cpuSum = 0;
        int frames = 0, totalFrames = 0;
        bool quit = false;
        session.Ticked += actions =>
        {
            if (actions.Pressed(InputAction.Quit)) quit = true;
            if (actions.Pressed(InputAction.ToggleSettings) && panel is not null) panel.Visible = !panel.Visible;
            if (actions.Pressed(InputAction.Screenshot)) screenshotRequested = true;
            if (actions.Pressed(InputAction.ToggleKeys) && overlay is not null) overlay.Visible = !overlay.Visible;
            if (actions.Pressed(InputAction.ToggleStats)) statsVisible = !statsVisible;
            if (actions.Pressed(InputAction.CycleProfiler) && profiler is not null) profiler.Showing = (FrameProfiler.Mode)(((int)profiler.Showing + 1) % 3);
        };
        long windowRan = 0, windowDropped = 0;
        double drawTotal = 0, gpuSum = 0, statsTimer = 0;
        int gpuSamples = 0, statsFrames = 0;
        long statsTicks = 0;
        while (!window.IsClosing && !quit)
        {
            window.DoEvents();
            if (window.IsClosing) break;
            double now = frame.Elapsed.TotalSeconds, dt = now - last;
            last = now;
            long a0 = Stopwatch.GetTimestamp();
            session.Advance(dt);
            advanceTotal += Stopwatch.GetElapsedTime(a0).TotalMilliseconds;
            UpdateNav();
            SendFocus();
            ApplyCamera(session.CameraAt());
            SandboxFollowCamera();
            var size = window.FramebufferSize;
            UpdateView(size.X, size.Y);
            if (display.BeginFrame(size.X, size.Y))
            {
                long t0 = Stopwatch.GetTimestamp();
                // The chain and the panel draw into the window's backbuffer (made again when the size changes).
                var backbuffer = display.Backbuffer!;
                gpu.Post!.Target = backbuffer;
                if (overlay is not null) overlay.Target = backbuffer;
                profiler?.BeginFrame();
                DrawWorld(size.X, size.Y);
                cpuSum += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                drawTotal += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                bool shot = screenshotRequested;
                screenshotRequested = false;
                if (overlay is not null && !g.NoPopulation) DrawMarkers(overlay, size.X, size.Y);
                if (overlay is not null && IsSandbox) DrawSandbox(overlay, size.X, size.Y);
                if (overlay is not null && !g.NoPopulation) player.Draw(overlay, size.X, size.Y);
                // The debug overlays are left out of saved pictures: the statistics at the top left, the key list below them, the profiler.
                if (overlay is not null && !shot)
                {
                    float panelsBottom = statsVisible && stats.Count > 0 ? overlay.Panel(size.X, size.Y, "Meitou   (F11 hides this)", stats) : 0;
                    if (overlay.Visible)
                    {
                        int width = keyItems.Max(i => i.Length) + 2;
                        var lines = keyItems.Select(item => KeyState(item.Split(' ')[0]) is { } state ? item.PadRight(width) + state : item).ToList();
                        overlay.Draw(size.X, size.Y, "Keys   (F10 hides this)", lines, panelsBottom);
                    }
                    profiler?.Draw(overlay, size.X, size.Y);
                }
                panel?.Draw(size.X, size.Y);
                display.Present();
                profiler?.EndFrame();
                if (display.Context.GpuFrameMs > 0) { gpuSum += display.Context.GpuFrameMs; gpuSamples++; }
                statsFrames++;
                if (shot) SaveScreenshot(display.Context, backbuffer, size.X, size.Y);
                frames++;
            }
            titleTimer += dt;
            statsTimer += dt;
            if (statsTimer >= 1)
            {
                // Once a second: the frame, the simulation, then the renderer's lines.
                stats.Clear();
                stats.Add($"{statsFrames / statsTimer:0} fps ({(vsync ? "vsync" : fpsLimit > 0 ? $"limit {fpsLimit}" : "uncapped")}), gpu {(gpuSamples > 0 ? $"{gpuSum / gpuSamples:0.00}" : "-")} ms");
                long ticks = session.Simulation.TotalTicks - statsTicks;
                statsTicks = session.Simulation.TotalTicks;
                stats.Add($"simulation  {ticks / statsTimer:0} ticks/s, {session.CurrentSnapshot.Characters.Count} characters, dropped {session.Simulation.DroppedTicks} ticks in all");
                WorldStats.Add(stats, display.Context, gpu, render, camera.Eye);
                statsTimer = gpuSum = 0;
                statsFrames = gpuSamples = 0;
            }
            if (titleTimer >= 0.5)
            {
                var s = session.Camera.Current;
                // The achieved speed over this window: the asked speed scaled by the share of the owed ticks that ran.
                long ran = session.Simulation.TotalTicks - windowRan, dropped = session.Simulation.DroppedTicks - windowDropped;
                windowRan = session.Simulation.TotalTicks;
                windowDropped = session.Simulation.DroppedTicks;
                double achieved = SimulationClock.AchievedSpeedOver(session.TimeScale, ran, dropped);
                string speedText = session.Simulation.IsPaused ? "paused" + (session.Simulation.RequestedPause ? "" : $" (x{session.Simulation.LastNonZeroSpeed:0})") : $"x{session.TimeScale:0}" + (achieved < session.TimeScale - 0.05 ? $" (running x{achieved:0.0})" : "");
                window.Title = $"Meitou | {frames / titleTimer:0} fps, cpu {cpuSum / Math.Max(frames, 1):0.00} ms | {(session.Camera.IsFree ? "free camera" : $"boom {s.Distance:0}")} | " +
                    $"{session.Clock.TimeText} {session.Clock.DayText} {speedText} | {s.Target.X:0}, {s.Target.Z:0}{(gpu.Post is { Temporal: true } p ? $" | {p.ActiveUpscaler}" : "")}";
                titleTimer = cpuSum = 0;
                frames = 0;
            }
            totalFrames++;
            if (g.QuitAfter is { } quitAfter && now >= quitAfter) { Console.WriteLine($"profile   {session.CurrentSnapshot.Characters.Count} characters in the snapshot, {session.CurrentSnapshot.Characters.Count(c => c.Appearance is not null)} drawable; per frame: simulation advance {advanceTotal / Math.Max(totalFrames, 1):0.000} ms ({session.Simulation.TotalTicks} ticks, {advanceTotal / Math.Max(session.Simulation.TotalTicks, 1):0.000} ms per tick), host draw-list fill {fillTotal / Math.Max(fillCalls, 1):0.000} ms, world draw cpu {drawTotal / Math.Max(totalFrames, 1):0.00} ms, {(gpu.Characters is { } chars ? chars.Statistics() : "no characters")}");
                Console.WriteLine($"smoke     {totalFrames} frames in {now:0.0} s: {totalFrames / now:0} fps on average, {session.Ticks.TotalTicks} control ticks, {session.Simulation.TotalTicks} simulation ticks, game time {session.Clock.TimeText}"); quit = true; }
            if (!vsync && fpsLimit > 0) Limit(frame, now, 1.0 / fpsLimit);
        }
        if (panel is not null)
            foreach (var slider in panel.Sliders) config.Graphics[slider.Label] = slider.Get();
        if (panel is not null)
            foreach (var toggle in panel.Toggles) config.Graphics[SwitchKey(toggle.Label)] = toggle.Get() ? 1 : 0;
        profiler?.Dispose();
        config.Bindings = session.Bindings.ToDictionary();
        config.Save();
        overlay?.Dispose();
        gpu.Dispose();
        silkInput.Dispose();
        return 0;

        static string SwitchKey(string label) => "Meitou: " + label;

        // The state shown beside a key in the F10 list, by the item's first word.
        string? KeyState(string key)
        {
            static string OnOff(bool on) => on ? "on" : "off";
            return key switch
            {
                ";" => OnOff(session.Camera.IsFree),
                "Space" => session.Simulation.IsPaused ? "paused" : "running",
                "F2/F3/F4" => $"x{session.TimeScale:0}",
                "Tab" => OnOff(panel?.Visible == true),
                "F12" => profiler?.Showing.ToString().ToLowerInvariant(),
                ['S', 'h', 'i', 'f', 't', '+', 'F', >= '1' and <= '9'] when key[^1] - '1' < switches.Count => switches[key[^1] - '1'].State,
                _ => null,
            };
        }
    }

    static void SaveScreenshot(GpuContext context, Texture backbuffer, int width, int height)
    {
        var file = Path.Combine(Directory.CreateDirectory(@"C:\Temp").FullName, $"meitou-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        FramebufferCapture.SavePng(context, backbuffer, file, width, height);
        Console.WriteLine($"saved     {file}");
    }

    /// <summary>Waits until <paramref name="period"/> has passed since <paramref name="start"/>: sleeps most of it, spins the last 1.5 ms.</summary>
    static void Limit(Stopwatch clock, double start, double period)
    {
        double end = start + period;
        double remaining = end - clock.Elapsed.TotalSeconds;
        if (remaining > 0.002) Thread.Sleep(TimeSpan.FromSeconds(remaining - 0.0015));
        while (clock.Elapsed.TotalSeconds < end) Thread.SpinWait(64);
    }
}

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
        var panel = overlay is null ? null : WorldFrame.CreateSettingsPanel(overlay, gpu, render);
        if (panel is not null)
            foreach (var slider in panel.Sliders)
                if (config.Graphics.TryGetValue(slider.Label, out float v) && !(o.Post.Upscale.Explicit && WorldFrame.UpscalerSliders.Contains(slider.Label)))
                    slider.Set(Math.Clamp(v, slider.Min, slider.Max));

        var silkInput = Silk.NET.Input.InputWindowExtensions.CreateInput(window);
        Vector2? lastMouse = null;
        bool panelDrag = false, shiftDown = false;
        Vector2 Pixels(Vector2 p) => p * new Vector2(window.FramebufferSize.X / (float)Math.Max(window.Size.X, 1), window.FramebufferSize.Y / (float)Math.Max(window.Size.Y, 1));
        foreach (var kb in silkInput.Keyboards)
        {
            kb.KeyDown += (_, k, _) =>
            {
                if (k is SilkKey.ShiftLeft or SilkKey.ShiftRight) shiftDown = true;
                if (KeyMap.Map(k) is { } key) { session.Input.SetKey(key, true); player.Key(key, shiftDown); }
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
        };
        long windowRan = 0, windowDropped = 0;
        double drawTotal = 0;
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
            var size = window.FramebufferSize;
            UpdateView(size.X, size.Y);
            if (display.BeginFrame(size.X, size.Y))
            {
                long t0 = Stopwatch.GetTimestamp();
                // The chain and the panel draw into the window's backbuffer (made again when the size changes).
                var backbuffer = display.Backbuffer!;
                gpu.Post!.Target = backbuffer;
                if (overlay is not null) overlay.Target = backbuffer;
                DrawWorld(size.X, size.Y);
                cpuSum += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                drawTotal += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                bool shot = screenshotRequested;
                screenshotRequested = false;
                if (overlay is not null && !g.NoPopulation) DrawMarkers(overlay, size.X, size.Y);
                if (overlay is not null && !g.NoPopulation) player.Draw(overlay, size.X, size.Y);
                panel?.Draw(size.X, size.Y);
                display.Present();
                if (shot) SaveScreenshot(display.Context, backbuffer, size.X, size.Y);
                frames++;
            }
            titleTimer += dt;
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
        config.Bindings = session.Bindings.ToDictionary();
        config.Save();
        overlay?.Dispose();
        gpu.Dispose();
        silkInput.Dispose();
        return 0;
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

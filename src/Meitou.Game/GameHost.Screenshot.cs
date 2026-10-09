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
    unsafe int Screenshot()
    {
        VulkanDisplay.RayTracing = o.WantsRayTracing;
        using var display = new VulkanDisplay(null, vsync: false, streamline: WantsDlss());
        streamline = display.Streamline;
        Boot(display, interactive: false);
        if (playerSquad is not null && (g.SelectPlayer || g.MoveTo is not null))
        {
            session.World.Commands.Enqueue(new Meitou.Simulation.SelectCommand(playerSquad.Members.ToList()) { Tick = session.World.Tick });
            if (g.MoveTo is { } to) session.World.Commands.Enqueue(new Meitou.Simulation.MoveOrder([], new Vector3(to.X, 0, to.Z)) { Tick = session.World.Tick });
        }
        bool attacked = false;
        for (int i = 0; i < g.Ticks; i++)
        {
            if (g.AttackNearest && !attacked && playerSquad is not null && session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == playerSquad.Leader) is { } lead)
            {
                var victim = session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && c.Body is { Dead: false }).OrderBy(c => Vector3.DistanceSquared(c.Position, lead.Position)).FirstOrDefault();
                if (victim is not null)
                {
                    session.World.Commands.Enqueue(new Meitou.Simulation.Combat.AttackOrder(playerSquad.Members.ToList(), victim.Id) { Tick = session.World.Tick });
                    Console.WriteLine($"attack    squad ordered to attack {victim.Name} {Vector3.Distance(victim.Position, lead.Position):0} units away");
                    attacked = true;
                }
            }
            session.Tick();
            session.AdvanceSimulation(session.Ticks.TickSeconds);   // one control tick of real time, at speed 1
            UpdateNav(wait: true);
        }
        SandboxFollowCamera();
        ApplyCamera(session.Camera.Current);
        gpu.Streamer?.Settle(gpu.Anchor ?? camera.Eye);
        gpu.Objects?.Settle(gpu.Anchor ?? camera.Eye);
        gpu.Foliage?.Settle(gpu.Anchor ?? camera.Eye);
        gpu.Characters?.Settle(gpu.Anchor ?? camera.Eye);
        WorldFrame.FinishLoading(display.Context);
        var context = display.Context;
        int w = o.Width, h = o.Height;
        using var target = Meitou.Rendering.Gpu.Texture.Create(context, new TextureDesc(Silk.NET.Vulkan.Format.R8G8B8A8Unorm, w, h,
            Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "offscreen picture"));
        gpu.Post!.Target = target;
        gpu.Post.InstantAdaptation = true;
        for (int i = 0; i < gpu.Post.WarmupFrames; i++) { DrawWorld(w, h); display.EndFrame(); }   // a temporal upscaler converges first
        DrawWorld(w, h);
        display.EndFrame();
        DrawWorld(w, h);
        if (playerSquad is not null && DebugOverlay.TryCreate(context) is { } overlay)
        {
            using (overlay)
            {
                overlay.Target = target;
                UpdateView(w, h);
                if (IsSandbox) DrawSandbox(overlay, w, h);
                player.Draw(overlay, w, h);
                if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer) is { } me && player.Project(DrawnPosition(me) + new Vector3(0, 1, 0)) is { } px)
                {
                    var picked = player.Pick(px);
                    var ray = player.Ray(px + new Vector2(0, 60));
                    var hit = ray is { } r ? player.GroundHit(r.Origin, r.Direction) : null;
                    Console.WriteLine($"pickcheck pixel {px.X:0}, {px.Y:0} picks {(picked is { } id && id == me.Id ? "the leader" : "NOTHING")}; ground 60 px lower: {(hit is { } gh ? $"{gh.X:0}, {gh.Z:0} (leader at {me.Position.X:0}, {me.Position.Z:0})" : "no hit")}");
                }
            }
        }
        display.EndFrame();
        context.Finish();
        var s = session.Camera.Current;
        Console.WriteLine($"camera    {(session.Camera.IsFree ? "free" : "strategy")}: pivot {s.Target.X:0}, {s.Target.Y:0}, {s.Target.Z:0}, eye {s.Eye.X:0}, {s.Eye.Y:0}, {s.Eye.Z:0}, " +
            $"yaw {s.Yaw * 180 / MathF.PI:0.#}, pitch {s.Pitch * 180 / MathF.PI:0.#}, boom {s.Distance:0.#}; {session.Ticks.TotalTicks} control ticks, {session.Simulation.TotalTicks} simulation ticks, game time {session.Clock.TimeText} ({session.Clock.DayText})");
        FramebufferCapture.SavePng(context, target, o.Screenshot!, w, h);
        Console.WriteLine($"saved     {Path.GetFullPath(o.Screenshot!)}");
        gpu.Dispose();
        return 0;
    }
}

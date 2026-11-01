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
    /// <summary>The showcase's trade: the leader opens a trade with the nearest trader and walks up to it (false: no trader, or it never opened).</summary>
    bool ShowcaseTrade()
    {
        player.ShowUiPanel(null);
        var characters = session.CurrentSnapshot.Characters;
        if (playerSquad is null || characters.FirstOrDefault(c => c.Id == playerSquad.Leader) is not { } leader) return false;
        var trader = characters.Where(c => c.IsTrader && c.Body is not { Dead: true } and not { Unconscious: true })
            .OrderBy(c => Vector3.DistanceSquared(c.Position, leader.Position)).FirstOrDefault();
        if (trader is null) { Console.WriteLine("showcase  no trader in the world"); return false; }
        session.World.Commands.Enqueue(new Meitou.Simulation.Trading.OpenTrade(leader.Id, trader.Id) { Tick = session.World.Tick });
        for (int i = 0; i < 4000 && session.CurrentSnapshot.Trade is null; i++)
        {
            // A long walk outlasts the approach time: ask again now and then.
            if (i > 0 && i % 400 == 0) session.World.Commands.Enqueue(new Meitou.Simulation.Trading.OpenTrade(leader.Id, trader.Id) { Tick = session.World.Tick });
            session.Tick();
            session.AdvanceSimulation(session.Ticks.TickSeconds);
            UpdateNav(wait: true);
        }
        Console.WriteLine(session.CurrentSnapshot.Trade is { } t ? $"showcase  trading with {t.TraderName}: {t.Stock.Count} stock items" : "showcase  the trade did not open");
        if (session.CurrentSnapshot.Trade is null) return false;
        if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == leader.Id) is { } walker)
        {
            session.Camera.MovePivot(new Vector2(walker.Position.X, walker.Position.Z));
            ApplyCamera(session.Camera.Current);
        }
        return true;
    }

    /// <summary>The showcase's conversation: the trade closes, the leader talks to the nearest shopkeeper (else the nearest NPC) that answers a Talk and walks up to it (false: none, or it never opened).</summary>
    bool ShowcaseTalk()
    {
        session.World.Commands.Enqueue(new Meitou.Simulation.Trading.CloseTrade { Tick = session.World.Tick });
        session.AdvanceSimulation(session.Ticks.TickSeconds);
        var characters = session.CurrentSnapshot.Characters;
        if (playerSquad is null || characters.FirstOrDefault(c => c.Id == playerSquad.Leader) is not { } leader) return false;
        // Most NPCs answer a Talk only in a special state (a guard while caged): try the nearest ones, shopkeepers first, until one talks.
        var candidates = characters.Where(c => !c.IsPlayer && c.Body is not { Dead: true } and not { Unconscious: true } && session.World.CanTalk(c.Id))
            .OrderBy(c => c.IsTrader ? 0 : 1).ThenBy(c => Vector3.DistanceSquared(c.Position, leader.Position)).Take(12).ToList();
        if (candidates.Count == 0) { Console.WriteLine("showcase  nobody to talk to"); return false; }
        CharacterSnapshot? npc = null;
        foreach (var candidate in candidates)
        {
            bool silent = false;
            for (int i = 0; i < 3000 && session.CurrentSnapshot.Conversation is null && !silent; i++)
            {
                if (i % 400 == 0) session.World.Commands.Enqueue(new Meitou.Simulation.Dialogue.TalkOrder(leader.Id, candidate.Id) { Tick = session.World.Tick });
                session.Tick();
                session.AdvanceSimulation(session.Ticks.TickSeconds);
                UpdateNav(wait: true);
                foreach (string message in session.World.TakeMessages())
                {
                    Console.WriteLine($"message   {message}");
                    silent |= message.EndsWith("has nothing to say.", StringComparison.Ordinal);
                }
            }
            if (session.CurrentSnapshot.Conversation is not null) { npc = candidate; break; }
        }
        if (npc is null || session.CurrentSnapshot.Conversation is not { } talk) { Console.WriteLine("showcase  no conversation opened"); return false; }
        Console.WriteLine($"showcase  talking to {npc.Name}: {talk.SpeakerName}: \"{talk.Text}\" choices: {string.Join(" | ", talk.Choices)}");
        if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == leader.Id) is { } walker)
        {
            session.Camera.MovePivot(new Vector2(walker.Position.X, walker.Position.Z));
            ApplyCamera(session.Camera.Current);
        }
        return true;
    }

    /// <summary>The panels a still captures: the one named by <c>--ui-panel</c>, the showcase's set, or only the HUD.</summary>
    string?[] StillPanels() => g.UiPanel is { Length: > 0 } one ? [one == "hud" ? null : one]
        : g.UiShowcase
            ? [null, "hud-expanded", "inventory", "stats", "overview", "menu", "new-game", "save", "load", "title", "build", "editor", "editor-face", "editor-hair", "edited", "trade", "talk"]
            : [null];

    /// <summary>The file a still panel is written to: the requested picture, or one per panel for the showcase.</summary>
    string StillPicturePath(string? panelName) => g.UiShowcase
        ? Path.Combine(Path.GetDirectoryName(o.Screenshot!) ?? "", $"{Path.GetFileNameWithoutExtension(o.Screenshot!)}-{panelName ?? "hud"}.png")
        : o.Screenshot!;

    /// <summary>
    /// The deterministic state before a still: the showcase's selection, a <c>--move-to</c> order, the requested control ticks
    /// and their simulation time, the duel/title per-tick work, the camera on where the leader got to, and the streamers settled.
    /// Vulkan and Metal both call this after their world and interface are up.
    /// </summary>
    void PrepareStill(IGpuContext context, bool finish = true)
    {
        if (o.Screenshot is null) return;
        if (g.TitlePreview && player is not null) ApplyTitleCamera();
        if (g.UiShowcase && player is not null && playerSquad is not null)
            player.Select([playerSquad.Leader], additive: false);
        if (playerSquad is not null && g.MoveTo is { } to)
        {
            session.World.Commands.Enqueue(new Meitou.Simulation.SelectCommand(playerSquad.Members.ToList()) { Tick = session.World.Tick });
            session.World.Commands.Enqueue(new Meitou.Simulation.MoveOrder([], new Vector3(to.X, 0, to.Z)) { Tick = session.World.Tick });
        }
        for (int i = 0; i < Math.Max(g.Ticks, g.UiShowcase ? 1 : 0); i++)
        {
            session.Tick();
            session.AdvanceSimulation(session.Ticks.TickSeconds);   // one control tick of real time, at speed 1
            UpdateNav(wait: true);
            DuelHoldClock();
            if (player is not null) DuelFrame(false);   // the duel's laps run on in a long still too
            BattleFrame();
            if (g.TitlePreview) titleBattle?.Advance(session.Ticks.TickSeconds);
        }
        PrintSavedLog();
        if (population is not null)
            foreach (var line in population.TakeTownLog()) Console.WriteLine($"town      {line}");
        SandboxFollowCamera();
        ApplyCamera(session.Camera.Current);
        if (g.TitlePreview && player is not null) ApplyTitleCamera();
        // After a walk (--move-to) the picture is of where the leader got to.
        if (g.MoveTo is not null && session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer) is { } walker)
        {
            session.Camera.MovePivot(new Vector2(walker.Position.X, walker.Position.Z));
            camera.Target = DrawnPosition(walker);
        }
        UpdateBuildings();   // the sites of a loaded save or a started game, before the objects settle
        gpu.Streamer?.Settle(camera.Eye);
        gpu.Objects?.Settle(camera.Eye);
        gpu.Foliage?.Settle(camera.Eye);
        gpu.Characters?.Settle(camera.Eye);
        if (finish) WorldFrame.FinishLoading(context);
    }

    unsafe int Screenshot()
    {
        VulkanDisplay.RayTracing = o.WantsRayTracing;
        using var display = new VulkanDisplay(null, vsync: false, dlss: true);
        streamline = display.Streamline;
        ngx = display.Ngx;
        Boot(display, interactive: false);
        PrepareStill(display.Context);
        var context = display.Context;
        int w = o.Width, h = o.Height;
        using var target = context.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h,
            Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "offscreen picture"));
        gpu.Post!.Target = target;
        gpu.Post.InstantAdaptation = true;
        for (int i = 0; i < gpu.Post.WarmupFrames; i++) { DrawWorld(w, h); display.EndFrame(); }   // a temporal upscaler converges first
        DrawWorld(w, h);
        display.EndFrame();
        using var overlay = DebugOverlay.TryCreate(context, assets.Find);
        if (overlay is not null) { player.CreateUiPainter(overlay, assets.Find); ConnectItemIcons(); }
        if (gpu.Characters is { } portraits) { var (atlas, sampled) = portraits.PortraitAtlas; player.SetUiTexture(PlayerInterface.PortraitTexture, atlas, sampled); }
        if (g.Scene is { } sceneName)
        {
            SceneShots(sceneName, display, target, overlay, w, h);
            player.DisposeUi();
            DisposeTitleScene();
            gpu.Dispose();
            return 0;
        }
        string?[] panels = g.UiShowcase
            ? [null, "hud-expanded", "inventory", "limbs", "stats", "overview", "overview-faction", "building", "menu", "new-game", "save", "load", "title", "title-new-game", "title-load", "title-import", "title-credits", "title-exit", "build", "editor", "editor-face", "editor-hair", "edited", "trade", "talk"] : [null];
        foreach (string? panelName in panels)
        {
            if (panelName == "trade" && !ShowcaseTrade()) continue;
            if (panelName == "talk" && !ShowcaseTalk()) continue;
            // The trade picture shows a stock item's tooltip: the pointer rests on the first item.
            if (panelName == "trade" && player.UiFrame.FirstOrDefault(c => c.Name.StartsWith("trade-item-", StringComparison.Ordinal) && !c.Name.EndsWith("-background", StringComparison.Ordinal) && !c.Name.EndsWith("-icon", StringComparison.Ordinal)) is { } item)
                player.MouseMove(new Vector2(item.Bounds.X + item.Bounds.Width / 2, item.Bounds.Y + item.Bounds.Height / 2));
            bool showPanel = panelName is not ("trade" or "talk");   // the trade and the conversation open their own windows; closing them would end them
            if (panelName?.StartsWith("editor", StringComparison.Ordinal) == true) { player.ShowUiPanel(panelName); EditorCamera(); }
            if (panelName == "edited") { player.ShowcaseEdit(); session.AdvanceSimulation(session.Ticks.TickSeconds); }
            if (panelName == "building") { UpdateView(w, h); player.ShowUiPanel("building"); session.AdvanceSimulation(session.Ticks.TickSeconds); }
            // Item icons are drawn from the frame after a cell asks for them, and their textures stream in: show the panel until they are done.
            for (int frames = 0; panelName is not null && overlay is not null && frames < 400 && (frames < 3 || gpu.Characters is { PendingIcons: > 0 } || gpu.Objects is { StagePropPending: true } || editorBar is not null && !editorBarReady || gpu.Objects is { PendingBuildingIcons: > 0 } || player.PendingIconFiles > 0); frames++)
            {
                DrawWorld(w, h);
                overlay.Target = target;
                UpdateView(w, h);
                if (g.TitlePreview) player.ShowTitle(); else if (showPanel) player.ShowUiPanel(panelName is "hud-expanded" or "edited" ? null : panelName, panelName == "hud-expanded");
                player.Draw(overlay, w, h);
                display.EndFrame();
                Thread.Sleep(5);
            }
            DrawWorld(w, h);
            if (overlay is not null)
            {
                overlay.Target = target;
                UpdateView(w, h);
                if (g.TitlePreview) player.ShowTitle(); else if (showPanel) player.ShowUiPanel(panelName is "hud-expanded" or "edited" ? null : panelName, panelName == "hud-expanded");
                player.Draw(overlay, w, h);
                if (IsSandbox && !g.UiShowcase) DrawSandbox(overlay, w, h);
                if (IsBattle && !g.UiShowcase) DrawBattleCounter(overlay, w, h);
                if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer) is { } me && player.Project(DrawnPosition(me) + new Vector3(0, 1, 0)) is { } px)
                {
                    var picked = player.Pick(px);
                    var ray = player.Ray(px + new Vector2(0, 60));
                    var hit = ray is { } r ? player.GroundHit(r.Origin, r.Direction) : null;
                    Console.WriteLine($"pickcheck pixel {px.X:0}, {px.Y:0} picks {(picked is { } id && id == me.Id ? "the leader" : "NOTHING")}; ground 60 px lower: {(hit is { } gh ? $"{gh.X:0}, {gh.Z:0} (leader at {me.Position.X:0}, {me.Position.Z:0})" : "no hit")}");
                }
            }
            display.EndFrame();
            context.Finish();
            string output = StillPicturePath(panelName);
            FramebufferCapture.SavePng(context, target, output, w, h);
            Console.WriteLine($"saved     {Path.GetFullPath(output)}");
        }
        if (g.SettingsShowcase && overlay is not null) SettingsShowcase(overlay, display, target, w, h);
        player.DisposeUi();
        var s = session.Camera.Current;
        Console.WriteLine($"camera    {(session.Camera.IsFree ? "free" : "strategy")}: pivot {s.Target.X:0}, {s.Target.Y:0}, {s.Target.Z:0}, eye {s.Eye.X:0}, {s.Eye.Y:0}, {s.Eye.Z:0}, " +
            $"yaw {s.Yaw * 180 / MathF.PI:0.#}, pitch {s.Pitch * 180 / MathF.PI:0.#}, boom {s.Distance:0.#}; {session.Ticks.TotalTicks} control ticks, {session.Simulation.TotalTicks} simulation ticks, game time {session.Clock.TimeText} ({session.Clock.DayText})");
        DisposeTitleScene();
        gpu.Dispose();
        return 0;
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Game.Host;
using Meitou.Game.Host.Input;
using Meitou.Game.Host.WorldSync;
using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;
using Meitou.Simulation.Weather;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineButton = Meitou.Game.Host.Input.MouseButton;
using EngineKey = Meitou.Game.Host.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;

namespace Meitou.Game.DevTools;

/// <summary>The <c>--screenshot</c> runs: the still pictures and showcases, and the staged scenes (<c>--scene</c>) saved frame by frame.</summary>
sealed class ScreenshotScenes(GameHost host)
{
    /// <summary>The showcase's trade: the leader opens a trade with the nearest trader and walks up to it (false: no trader, or it never opened).</summary>
    internal bool ShowcaseTrade()
    {
        host.player.ShowUiPanel(null);
        var characters = host.session.CurrentSnapshot.Characters;
        if (host.playerSquad is null || characters.FirstOrDefault(c => c.Id == host.playerSquad.Leader) is not { } leader) return false;
        var trader = characters.Where(c => c.IsTrader && c.Body is not { Dead: true } and not { Unconscious: true })
            .OrderBy(c => Vector3.DistanceSquared(c.Position, leader.Position)).FirstOrDefault();
        if (trader is null) { Console.WriteLine("showcase  no trader in the world"); return false; }
        host.session.World.Commands.Enqueue(new Meitou.Simulation.Trading.OpenTrade(leader.Id, trader.Id) { Tick = host.session.World.Tick });
        for (int i = 0; i < 4000 && host.session.CurrentSnapshot.Trade is null; i++)
        {
            // A long walk outlasts the approach time: ask again now and then.
            if (i > 0 && i % 400 == 0) host.session.World.Commands.Enqueue(new Meitou.Simulation.Trading.OpenTrade(leader.Id, trader.Id) { Tick = host.session.World.Tick });
            host.session.Tick();
            host.session.AdvanceSimulation(host.session.Ticks.TickSeconds);
            host.UpdateNav(wait: true);
        }
        Console.WriteLine(host.session.CurrentSnapshot.Trade is { } t ? $"showcase  trading with {t.TraderName}: {t.Stock.Count} stock items" : "showcase  the trade did not open");
        if (host.session.CurrentSnapshot.Trade is null) return false;
        if (host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == leader.Id) is { } walker)
        {
            host.session.Camera.MovePivot(new Vector2(walker.Position.X, walker.Position.Z));
            host.ApplyCamera(host.session.Camera.Current);
        }
        return true;
    }

    /// <summary>The showcase's conversation: the trade closes, the leader talks to the nearest shopkeeper (else the nearest NPC) that answers a Talk and walks up to it (false: none, or it never opened).</summary>
    internal bool ShowcaseTalk()
    {
        host.session.World.Commands.Enqueue(new Meitou.Simulation.Trading.CloseTrade { Tick = host.session.World.Tick });
        host.session.AdvanceSimulation(host.session.Ticks.TickSeconds);
        var characters = host.session.CurrentSnapshot.Characters;
        if (host.playerSquad is null || characters.FirstOrDefault(c => c.Id == host.playerSquad.Leader) is not { } leader) return false;
        // Most NPCs answer a Talk only in a special state (a guard while caged): try the nearest ones, shopkeepers first, until one talks.
        var candidates = characters.Where(c => !c.IsPlayer && c.Body is not { Dead: true } and not { Unconscious: true } && host.session.World.CanTalk(c.Id))
            .OrderBy(c => c.IsTrader ? 0 : 1).ThenBy(c => Vector3.DistanceSquared(c.Position, leader.Position)).Take(12).ToList();
        if (candidates.Count == 0) { Console.WriteLine("showcase  nobody to talk to"); return false; }
        CharacterSnapshot? npc = null;
        foreach (var candidate in candidates)
        {
            bool silent = false;
            for (int i = 0; i < 3000 && host.session.CurrentSnapshot.Conversation is null && !silent; i++)
            {
                if (i % 400 == 0) host.session.World.Commands.Enqueue(new Meitou.Simulation.Dialogue.TalkOrder(leader.Id, candidate.Id) { Tick = host.session.World.Tick });
                host.session.Tick();
                host.session.AdvanceSimulation(host.session.Ticks.TickSeconds);
                host.UpdateNav(wait: true);
                foreach (string message in host.session.World.TakeMessages())
                {
                    Console.WriteLine($"message   {message}");
                    silent |= message.EndsWith("has nothing to say.", StringComparison.Ordinal);
                }
            }
            if (host.session.CurrentSnapshot.Conversation is not null) { npc = candidate; break; }
        }
        if (npc is null || host.session.CurrentSnapshot.Conversation is not { } talk) { Console.WriteLine("showcase  no conversation opened"); return false; }
        Console.WriteLine($"showcase  talking to {npc.Name}: {talk.SpeakerName}: \"{talk.Text}\" choices: {string.Join(" | ", talk.Choices)}");
        if (host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == leader.Id) is { } walker)
        {
            host.session.Camera.MovePivot(new Vector2(walker.Position.X, walker.Position.Z));
            host.ApplyCamera(host.session.Camera.Current);
        }
        return true;
    }

    /// <summary>The panels a still captures: the one named by <c>--ui-panel</c>, the showcase's set, or only the HUD.</summary>
    internal string?[] StillPanels() => host.g.UiPanel is { Length: > 0 } one ? [one == "hud" ? null : one]
        : host.g.UiShowcase
            ? [null, "hud-expanded", "inventory", "stats", "overview", "menu", "new-game", "save", "load", "title", "build", "editor", "editor-face", "editor-hair", "edited", "trade", "talk"]
            : [null];

    /// <summary>The file a still panel is written to: the requested picture, or one per panel for the showcase.</summary>
    internal string StillPicturePath(string? panelName) => host.g.UiShowcase
        ? Path.Combine(Path.GetDirectoryName(host.o.Screenshot!) ?? "", $"{Path.GetFileNameWithoutExtension(host.o.Screenshot!)}-{panelName ?? "hud"}.png")
        : host.o.Screenshot!;

    /// <summary>
    /// The deterministic state before a still: the showcase's selection, a <c>--move-to</c> order, the requested control ticks
    /// and their simulation time, the duel/title per-tick work, the camera on where the leader got to, and the streamers settled.
    /// Vulkan and Metal both call this after their world and interface are up.
    /// </summary>
    internal void PrepareStill(IGpuContext context, bool finish = true)
    {
        if (host.o.Screenshot is null) return;
        if (host.g.TitlePreview && host.player is not null) host.titleScreen.ApplyTitleCamera();
        if (host.g.UiShowcase && host.player is not null && host.playerSquad is not null)
            host.player.Select([host.playerSquad.Leader], additive: false);
        if (host.playerSquad is not null && host.g.MoveTo is { } to)
        {
            host.session.World.Commands.Enqueue(new Meitou.Simulation.SelectCommand(host.playerSquad.Members.ToList()) { Tick = host.session.World.Tick });
            host.session.World.Commands.Enqueue(new Meitou.Simulation.MoveOrder([], new Vector3(to.X, 0, to.Z)) { Tick = host.session.World.Tick });
        }
        for (int i = 0; i < Math.Max(host.g.Ticks, host.g.UiShowcase ? 1 : 0); i++)
        {
            host.session.Tick();
            host.session.AdvanceSimulation(host.session.Ticks.TickSeconds);   // one control tick of real time, at speed 1
            host.UpdateNav(wait: true);
            host.duel.DuelHoldClock();
            if (host.player is not null) host.duel.DuelFrame(false);   // the duel's laps run on in a long still too
            host.battle.BattleFrame();
            if (host.g.TitlePreview) host.titleScreen.titleBattle?.Advance(host.session.Ticks.TickSeconds);
        }
        host.saves.PrintSavedLog();
        if (host.population is not null)
            foreach (var line in host.population.TakeTownLog()) Console.WriteLine($"town      {line}");
        host.sandbox.SandboxFollowCamera();
        host.ApplyCamera(host.session.Camera.Current);
        if (host.g.TitlePreview && host.player is not null) host.titleScreen.ApplyTitleCamera();
        // After a walk (--move-to) the picture is of where the leader got to.
        if (host.g.MoveTo is not null && host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer) is { } walker)
        {
            host.session.Camera.MovePivot(new Vector2(walker.Position.X, walker.Position.Z));
            host.camera.Target = host.DrawnPosition(walker);
        }
        host.buildingSync.UpdateBuildings();   // the sites of a loaded save or a started game, before the objects settle
        host.gpu.Streamer?.Settle(host.camera.Eye);
        host.gpu.Objects?.Settle(host.camera.Eye);
        host.gpu.Foliage?.Settle(host.camera.Eye);
        host.gpu.Characters?.Settle(host.camera.Eye);
        if (finish) WorldFrame.FinishLoading(context);
    }

    internal unsafe int Screenshot()
    {
        VulkanDisplay.RayTracing = host.o.WantsRayTracing;
        using var display = new VulkanDisplay(null, vsync: false, dlss: true);
        host.streamline = display.Streamline;
        host.ngx = display.Ngx;
        host.Boot(display, interactive: false);
        PrepareStill(display.Context);
        var context = display.Context;
        int w = host.o.Width, h = host.o.Height;
        using var target = context.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h,
            Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "offscreen picture"));
        host.gpu.Post!.Target = target;
        host.gpu.Post.InstantAdaptation = true;
        for (int i = 0; i < host.gpu.Post.WarmupFrames; i++) { host.DrawWorld(w, h); display.EndFrame(); }   // a temporal upscaler converges first
        host.DrawWorld(w, h);
        display.EndFrame();
        using var overlay = DebugOverlay.TryCreate(context, host.assets.Find);
        if (overlay is not null) { host.player.CreateUiPainter(overlay, host.assets.Find); host.itemIconFiles.ConnectItemIcons(); }
        if (host.gpu.Characters is { } portraits) { var (atlas, sampled) = portraits.PortraitAtlas; host.player.SetUiTexture(PlayerInterface.PortraitTexture, atlas, sampled); }
        if (host.g.Scene is { } sceneName)
        {
            SceneShots(sceneName, display, target, overlay, w, h);
            host.player.DisposeUi();
            host.titleScreen.DisposeTitleScene();
            host.gpu.Dispose();
            return 0;
        }
        string?[] panels = host.g.UiPanel is { Length: > 0 } onePanel ? [onePanel == "hud" ? null : onePanel] : host.g.UiShowcase
            ? [null, "hud-expanded", "inventory", "limbs", "stats", "overview", "overview-faction", "building", "menu", "new-game", "save", "load", "title", "title-new-game", "title-load", "title-import", "title-credits", "title-exit", "build", "editor", "editor-face", "editor-hair", "edited", "trade", "talk"] : [null];
        foreach (string? panelName in panels)
        {
            if (panelName == "trade" && !ShowcaseTrade()) continue;
            if (panelName == "talk" && !ShowcaseTalk()) continue;
            // The trade picture shows a stock item's tooltip: the pointer rests on the first item.
            if (panelName == "trade" && host.player.UiFrame.FirstOrDefault(c => c.Name.StartsWith("trade-item-", StringComparison.Ordinal) && !c.Name.EndsWith("-background", StringComparison.Ordinal) && !c.Name.EndsWith("-icon", StringComparison.Ordinal)) is { } item)
                host.player.MouseMove(new Vector2(item.Bounds.X + item.Bounds.Width / 2, item.Bounds.Y + item.Bounds.Height / 2));
            bool showPanel = panelName is not ("trade" or "talk");   // the trade and the conversation open their own windows; closing them would end them
            if (panelName?.StartsWith("editor", StringComparison.Ordinal) == true) { host.player.ShowUiPanel(panelName); host.characterEditor.EditorCamera(); }
            if (panelName == "edited") { host.player.ShowcaseEdit(); host.session.AdvanceSimulation(host.session.Ticks.TickSeconds); }
            if (panelName == "building") { host.UpdateView(w, h); host.player.ShowUiPanel("building"); host.session.AdvanceSimulation(host.session.Ticks.TickSeconds); }
            // Item icons are drawn from the frame after a cell asks for them, and their textures stream in: show the panel until they are done.
            for (int frames = 0; panelName is not null && overlay is not null && frames < 400 && (frames < 3 || host.gpu.Characters is { PendingIcons: > 0 } || host.gpu.Objects is { StagePropPending: true } || host.characterEditor.editorBar is not null && !host.characterEditor.editorBarReady || host.gpu.Objects is { PendingBuildingIcons: > 0 } || host.player.PendingIconFiles > 0); frames++)
            {
                host.DrawWorld(w, h);
                overlay.Target = target;
                host.UpdateView(w, h);
                if (host.g.TitlePreview) host.player.ShowTitle(); else if (showPanel) host.player.ShowUiPanel(panelName is "hud-expanded" or "edited" ? null : panelName, panelName == "hud-expanded");
                host.player.Draw(overlay, w, h);
                display.EndFrame();
                Thread.Sleep(5);
            }
            host.DrawWorld(w, h);
            if (overlay is not null)
            {
                overlay.Target = target;
                host.UpdateView(w, h);
                if (host.g.TitlePreview) host.player.ShowTitle(); else if (showPanel) host.player.ShowUiPanel(panelName is "hud-expanded" or "edited" ? null : panelName, panelName == "hud-expanded");
                host.player.Draw(overlay, w, h);
                if (host.sandbox.IsSandbox && !host.g.UiShowcase) host.sandbox.DrawSandbox(overlay, w, h);
                if (host.battle.IsBattle && !host.g.UiShowcase) host.battle.DrawBattleCounter(overlay, w, h);
                if (host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer) is { } me && host.player.Project(host.DrawnPosition(me) + new Vector3(0, 1, 0)) is { } px)
                {
                    var picked = host.player.Pick(px);
                    var ray = host.player.Ray(px + new Vector2(0, 60));
                    var hit = ray is { } r ? host.player.GroundHit(r.Origin, r.Direction) : null;
                    Console.WriteLine($"pickcheck pixel {px.X:0}, {px.Y:0} picks {(picked is { } id && id == me.Id ? "the leader" : "NOTHING")}; ground 60 px lower: {(hit is { } gh ? $"{gh.X:0}, {gh.Z:0} (leader at {me.Position.X:0}, {me.Position.Z:0})" : "no hit")}");
                }
            }
            display.EndFrame();
            context.Finish();
            string output = StillPicturePath(panelName);
            FramebufferCapture.SavePng(context, target, output, w, h);
            Console.WriteLine($"saved     {Path.GetFullPath(output)}");
        }
        if (host.g.SettingsShowcase && overlay is not null) SettingsShowcase(overlay, display, target, w, h);
        host.player.DisposeUi();
        var s = host.session.Camera.Current;
        Console.WriteLine($"camera    {(host.session.Camera.IsFree ? "free" : "strategy")}: pivot {s.Target.X:0}, {s.Target.Y:0}, {s.Target.Z:0}, eye {s.Eye.X:0}, {s.Eye.Y:0}, {s.Eye.Z:0}, " +
            $"yaw {s.Yaw * 180 / MathF.PI:0.#}, pitch {s.Pitch * 180 / MathF.PI:0.#}, boom {s.Distance:0.#}; {host.session.Ticks.TotalTicks} control ticks, {host.session.Simulation.TotalTicks} simulation ticks, game time {host.session.Clock.TimeText} ({host.session.Clock.DayText})");
        host.titleScreen.DisposeTitleScene();
        host.gpu.Dispose();
        return 0;
    }

    // Staged scenes to check behaviour offscreen (<c>--scene</c> with <c>--screenshot</c>, usually with <c>--load</c>): the scene gives its orders, then every <c>--scene-every</c>
    // control ticks a frame with the interface is saved as <c>&lt;screenshot&gt;-scene-NN.png</c>, <c>--scene-frames</c> of them, and the log prints the leader's state. The camera
    // follows the scene's focus. A test harness: nothing here is a game rule.
    /// <summary>The scenes <see cref="StageScene"/> knows.</summary>
    static readonly string[] SceneNames = ["fight", "turret", "bed", "acid", "menu"];

    /// <summary>The menu scene's targets, one frame each.</summary>
    static readonly string[] SceneMenus = ["character", "door", "container", "cage"];

    /// <summary>
    /// Gives the scene's orders and returns the point the camera watches (null: the leader). fight: two enemies of <c>--scene-npc</c> spawned 40 units from the leader, which the
    /// squad attacks; turret: the nearest town turret within 5000, three enemies spawned 250 units before it; bed: the leader sleeps in the nearest bed; acid: acid rain on every
    /// point; menu: nothing (the menus open per frame).
    /// </summary>
    Vector3? StageScene(string scene)
    {
        var world = host.session.World;
        long tick = world.Tick;
        if (host.playerSquad is null || host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == host.playerSquad.Leader) is not { } leader)
        {
            Console.WriteLine("scene     no player squad");
            return null;
        }
        var members = host.playerSquad.Members.ToList();
        world.Commands.Enqueue(new SelectCommand(members) { Tick = tick });
        var at = leader.Position;
        switch (scene)
        {
            case "fight":
            {
                if (host.debugMenu.DebugRecord(host.g.SceneNpc, character: true) is not { } npc) { Console.WriteLine($"scene     no character record matches '{host.g.SceneNpc}'"); return null; }
                world.Commands.Enqueue(new Meitou.Simulation.DebugSpawn(npc.Id, new Vector2(at.X + 40, at.Z)) { Tick = tick });
                world.Commands.Enqueue(new Meitou.Simulation.DebugSpawn(npc.Id, new Vector2(at.X + 40, at.Z + 25)) { Tick = tick });
                Console.WriteLine($"scene     fight: two {npc.Name} spawned beside {leader.Name}");
                AdvanceScene(2);
                if (host.session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && Vector3.Distance(c.Position, at) < 120).OrderBy(c => Vector3.DistanceSquared(c.Position, at)).FirstOrDefault() is { } enemy)
                    world.Commands.Enqueue(new Meitou.Simulation.Combat.AttackOrder(members, enemy.Id) { Tick = world.Tick });
                return null;
            }
            case "turret":
            {
                if (host.g.SceneTown is { } townName && host.popData is { } towns)
                {
                    var match = towns.Sites.Where(s => s.Town.Name.Contains(townName, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Town.Name.Length).FirstOrDefault();
                    if (match is null) { Console.WriteLine($"scene     no town matches '{townName}'"); return null; }
                    world.Commands.Enqueue(new Meitou.Simulation.DebugTeleport(members, match.Position) { Tick = tick });
                    Console.WriteLine($"scene     turret: the squad goes to {match.Town.Name} at {match.Position.X:0}, {match.Position.Z:0}");
                    host.session.Camera.MovePivot(new Vector2(match.Position.X, match.Position.Z));
                    // The town's residents spawn once its zones are loaded: wait until someone is there (at most 1200 ticks).
                    for (int k = 0; k < 40 && !host.session.CurrentSnapshot.Characters.Any(c => !c.IsPlayer && Vector3.Distance(c.Position, match.Position) < 1500); k++) AdvanceScene(30);
                    AdvanceScene(60);
                    if (host.population is not null) foreach (var line in host.population.TakeTownLog()) Console.WriteLine($"town      {line}");
                    at = match.Position;
                    tick = world.Tick;
                }
                Meitou.Data.World.TownBuilding? best = null;
                int bestSite = -1;
                float bestD = 5000 * 5000;
                foreach (var site in host.popData?.Sites ?? [])
                    foreach (var b in site.Buildings)
                        if (b.Turret is not null && Vector3.DistanceSquared(b.Position, at) is var d && d < bestD) (best, bestSite, bestD) = (b, site.Index, d);
                if (best?.Turret is not { } post) { Console.WriteLine("scene     no town turret within 5000"); return null; }
                var forward = new Vector2(MathF.Sin(post.Yaw), MathF.Cos(post.Yaw));
                var spot = new Vector2(post.Position.X, post.Position.Z) + forward * 250;
                if (host.debugMenu.DebugRecord(host.g.SceneNpc, character: true) is { } npc)
                    for (int k = 0; k < 3; k++)
                        world.Commands.Enqueue(new Meitou.Simulation.DebugSpawn(npc.Id, spot + new Vector2(k * 20, 0)) { Tick = tick });
                world.Commands.Enqueue(new Meitou.Simulation.DebugTeleport(members, new Vector3(post.Position.X - forward.X * 120, post.Position.Y, post.Position.Z - forward.Y * 120)) { Tick = tick });
                Console.WriteLine($"scene     turret: {best.Name} ({best.InstanceId}) at {post.Position.X:0}, {post.Position.Z:0}, gun {post.GunId}; enemies at {spot.X:0}, {spot.Y:0}");
                // The spawned enemies' faction (the first one hostile to the player) need not be the town's enemy: the scene sets them at war, so its guards see enemies.
                AdvanceScene(2);
                int town = host.popData is { } pd && bestSite >= 0 && bestSite < pd.SiteFactions.Count ? pd.SiteFactions[bestSite] : -1;
                var spawned = host.session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && Vector2.Distance(new Vector2(c.Position.X, c.Position.Z), spot) < 80).ToList();
                if (town >= 0 && spawned.Count > 0 && host.popData is { } rel)
                {
                    var war = rel.Relations.DeclareWar(spawned[0].Faction, town);
                    Console.WriteLine($"scene     turret: faction {spawned[0].Faction} at war with the town's faction {town} (relation {war.Before:0} -> {war.After:0})");
                }
                return post.Position;
            }
            case "bed":
            {
                // The player's own beds first (another faction's private bed answers "This isn't mine"), else the nearest.
                int own = world.Player.Faction;
                if (host.session.CurrentSnapshot.Beds.OrderBy(b => b.Owner == own ? 0 : 1).ThenBy(b => Vector3.DistanceSquared(b.Position, at)).FirstOrDefault() is not { } bed) { Console.WriteLine("scene     no bed"); return null; }
                world.Commands.Enqueue(new Meitou.Simulation.Locks.UseBedOrder([leader.Id], bed.Id) { Tick = tick });
                Console.WriteLine($"scene     bed: {leader.Name} to {bed.Id} ({bed.Record}, {(bed.Owner == own ? "the player's" : $"faction {bed.Owner}'s")}) at {bed.Position.X:0}, {bed.Position.Z:0}, {Vector3.Distance(bed.Position, at):0} away");
                return null;
            }
            case "acid":
                host.weatherFeed.hostWeather ??= new WeatherFeed.HostWeather(() => host.gpu.Weather);
                host.weatherFeed.hostWeather.Forced = new WeatherHere(Meitou.Data.World.WeatherAffecting.Acid, 1, 0, 0);
                Console.WriteLine("scene     acid: acid rain of strength 1 forced on every point");
                return null;
            case "menu":
                return null;
            default:
                Console.WriteLine($"scene     unknown scene '{scene}' (known: {string.Join(", ", SceneNames)})");
                return null;
        }
    }

    /// <summary>Runs <paramref name="ticks"/> control ticks as the screenshot loop does (each with its real time of simulation at speed 1).</summary>
    void AdvanceScene(int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            host.session.Tick();
            host.session.AdvanceSimulation(host.session.Ticks.TickSeconds);
            host.UpdateNav(wait: true);
        }
    }

    /// <summary>The leader's state for the log: task, activity, weapon, turret, bed, weather notice, blood and the parts' flesh.</summary>
    string SceneState()
    {
        if (host.playerSquad is null || host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == host.playerSquad.Leader) is not { } me) return "no leader";
        string blood = me.Hud is { } hud ? $"blood {hud.Blood:0.0}/{hud.BloodCapacity:0}, bleeding {hud.Bleeding:0.000}" : "";
        string parts = me.HealthParts.Count > 0 ? "parts flesh/stun " + string.Join(" ", me.HealthParts.Select(p => $"{p.Flesh.ToString("0", CultureInfo.InvariantCulture)}/{p.Stun.ToString("0.0", CultureInfo.InvariantCulture)}")) : "";
        var enemies = host.session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && c.FightsPlayer).ToList();
        string turrets = string.Join(", ", host.session.CurrentSnapshot.Characters.Where(c => c.Turret is not null).Select(c => $"{c.Name} on {c.Turret!.BuildingId}"));
        var near = host.session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && Vector3.Distance(c.Position, me.Position) < 200)
            .Select(c => $"{c.Name} [{c.Task}{(host.session.World.Characters.TryResolveNext(c.Id, out int cs) && host.session.World.Characters.Cold(cs) is { } cc ? $" faction {cc.Faction}{(cc.Fleeing ? ", fleeing" : "")}{(cc.Fighter is { CanAttack: false } ? ", no attack" : "")}" : "")}{(c.InCombat ? ", fighting" : "")}{(c.FightsPlayer ? ", vs player" : "")}{(c.Body is { Unconscious: true } ? ", down" : c.Body is { Dead: true } ? ", dead" : "")}]");
        return $"{me.Name} at {me.Position.X:0}, {me.Position.Z:0}: task {me.Task}, {me.Activity}, weapon {(me.WeaponDrawn ? me.BackDrawn ? "back" : "hip" : "none")}, " +
            $"in bed {me.InBed}, weather {me.WeatherNotice}, {blood}, {parts}; enemies fighting {enemies.Count}{(turrets.Length > 0 ? $"; gunners: {turrets}" : "")}; near: {string.Join(", ", near)}";
    }

    /// <summary>
    /// The scene's frames: <see cref="StageScene"/>, then per frame the ticks, the camera on the focus (else the leader), the world and the interface drawn, the picture saved;
    /// the menu scene opens one menu per frame instead of running ticks.
    /// </summary>
    void SceneShots(string scene, VulkanDisplay display, GpuTexture target, DebugOverlay? overlay, int w, int h)
    {
        var focus = StageScene(scene);
        int frames = scene == "menu" ? SceneMenus.Length : host.g.SceneFrames;
        for (int f = 0; f < frames; f++)
        {
            if (scene != "menu") AdvanceScene(host.g.SceneEvery);
            var lead = host.playerSquad is null ? null : host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == host.playerSquad.Leader);
            var watch = focus ?? lead?.Position;
            if (watch is { } p)
            {
                host.session.Camera.MovePivot(new Vector2(p.X, p.Z));
                host.camera.Target = p;
            }
            host.ApplyCamera(host.session.Camera.Current);
            host.buildingSync.UpdateBuildings();
            host.gpu.Characters?.Settle(host.camera.Eye);
            host.gpu.Objects?.Settle(host.camera.Eye);
            host.DrawWorld(w, h);
            if (overlay is not null)
            {
                overlay.Target = target;
                host.UpdateView(w, h);
                if (scene == "menu" && lead is not null && !host.player.OpenSceneMenu(SceneMenus[f], lead.Position)) Console.WriteLine($"scene     menu {SceneMenus[f]}: none on screen");
                host.player.Draw(overlay, w, h);
            }
            display.EndFrame();
            display.Context.Finish();
            string output = Path.Combine(Path.GetDirectoryName(host.o.Screenshot!) ?? "", $"{Path.GetFileNameWithoutExtension(host.o.Screenshot!)}-{scene}-{f:00}.png");
            FramebufferCapture.SavePng(display.Context, target, output, w, h);
            Console.WriteLine($"scene     frame {f} tick {host.session.World.Tick}: {SceneState()}");
            Console.WriteLine($"saved     {Path.GetFullPath(output)}");
        }
    }
    /// <summary>
    /// <c>--settings-showcase</c> with <c>--screenshot</c>: the Graphics page of the settings menu (Meitou UI) over the world, saved as
    /// <c>&lt;screenshot&gt;-settings-&lt;n&gt;-&lt;state&gt;.png</c>. The menu reads and writes no preset file and changes no saved setting.
    /// </summary>
    void SettingsShowcase(DebugOverlay overlay, VulkanDisplay display, GpuTexture target, int w, int h)
    {
        var values = WorldFrame.CreateSettingsPanel(overlay, host.gpu, host.render, switches: WorldFrame.LiveSwitches(host.o, () => host.gpu, () => host.camera, () => host.render));
        var menu = new SettingsMenu(host.player, values, host.session.Bindings, () => { }, new Slider("Frame limit", 0, 360, () => 150, _ => { }, "0"),
            new UserConfig()) { NoDiskWrites = true };
        host.player.SetUiMode(Meitou.Data.Ui.UiMode.Meitou);
        int n = 0;
        void Shot(string name, int tab, Action? setup = null)
        {
            menu.ShowGraphicsTab(tab);
            setup?.Invoke();
            for (int f = 0; f < 40; f++)   // the menu's open and tab animations settle
            {
                host.DrawWorld(w, h);
                overlay.Target = target;
                host.UpdateView(w, h);
                host.player.Draw(overlay, w, h);
                menu.Draw(w, h);
                display.EndFrame();
                Thread.Sleep(10);
            }
            display.Context.Finish();
            string output = Path.Combine(Path.GetDirectoryName(host.o.Screenshot!) ?? "", $"{Path.GetFileNameWithoutExtension(host.o.Screenshot!)}-settings-{++n}-{name}.png");
            FramebufferCapture.SavePng(display.Context, target, output, w, h);
            Console.WriteLine($"saved     {Path.GetFullPath(output)}");
        }
        string[] tabs = ["lighting", "sky", "colour", "world", "characters"];
        for (int i = 0; i < tabs.Length; i++) Shot(tabs[i], i);
        menu.ShowcaseOpen("Shadows", "Light shafts", "Ambient light", "Tone map and grade", "Draw distances", "Terrain relief", "Water");
        Shot("lighting-open", 0);
        Shot("colour-open", 2);
        Shot("world-open", 3);
        Shot("sky-changed", 1, () => { menu.ShowcaseOpen("Heat haze", "Clouds"); menu.ShowcaseSet("Heat haze strength (1 = game)", 0.8f); });
        menu.ShowcaseSet("Heat haze strength (1 = game)", 0.5f);
        Shot("preset-changed", 2, () => { menu.ShowcaseLook(1); menu.ShowcaseSet("Night grading strength (0 = game)", 0.8f); });
        Shot("preset-list", 2, menu.ShowcaseOpenLooks);
        Shot("save-as", 2, menu.ShowcaseNaming);
        Shot("low-quality", 0, () => menu.ShowcaseTier(0));
        Shot("faithful", 0, () => { menu.ShowcaseTier(2); menu.ShowcaseFaithful(true); });
        Shot("faithful-world", 3);
        Shot("faithful-changed", 0, () => menu.ShowcaseSwitch("Ambient occlusion", true));
        menu.ShowcaseFaithful(false);
        // The world in every look, without the menu (the temporal upscaler's history settles first).
        for (int i = 0; i < menu.ShowcaseLookCount; i++)
        {
            string name = menu.ShowcaseLook(i);
            for (int f = 0; f < Math.Max(host.gpu.Post!.WarmupFrames, 8); f++) { host.DrawWorld(w, h); display.EndFrame(); }
            host.DrawWorld(w, h);
            display.EndFrame();
            display.Context.Finish();
            string output = Path.Combine(Path.GetDirectoryName(host.o.Screenshot!) ?? "", $"{Path.GetFileNameWithoutExtension(host.o.Screenshot!)}-look-{i}-{name.Replace(' ', '-').ToLowerInvariant()}.png");
            FramebufferCapture.SavePng(display.Context, target, output, w, h);
            Console.WriteLine($"saved     {Path.GetFullPath(output)}");
        }
    }
}

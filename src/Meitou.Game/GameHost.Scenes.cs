using System.Globalization;
using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;
using Meitou.Simulation.Weather;

namespace Meitou.Game;

/// <summary>
/// Staged scenes to check behaviour offscreen (<c>--scene</c> with <c>--screenshot</c>, usually with <c>--load</c>): the scene gives its orders, then every <c>--scene-every</c>
/// control ticks a frame with the interface is saved as <c>&lt;screenshot&gt;-scene-NN.png</c>, <c>--scene-frames</c> of them, and the log prints the leader's state. The camera
/// follows the scene's focus. A test harness: nothing here is a game rule.
/// </summary>
sealed partial class GameHost
{
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
        var world = session.World;
        long tick = world.Tick;
        if (playerSquad is null || session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == playerSquad.Leader) is not { } leader)
        {
            Console.WriteLine("scene     no player squad");
            return null;
        }
        var members = playerSquad.Members.ToList();
        world.Commands.Enqueue(new SelectCommand(members) { Tick = tick });
        var at = leader.Position;
        switch (scene)
        {
            case "fight":
            {
                if (DebugRecord(g.SceneNpc, character: true) is not { } npc) { Console.WriteLine($"scene     no character record matches '{g.SceneNpc}'"); return null; }
                world.Commands.Enqueue(new Meitou.Simulation.DebugSpawn(npc.Id, new Vector2(at.X + 40, at.Z)) { Tick = tick });
                world.Commands.Enqueue(new Meitou.Simulation.DebugSpawn(npc.Id, new Vector2(at.X + 40, at.Z + 25)) { Tick = tick });
                Console.WriteLine($"scene     fight: two {npc.Name} spawned beside {leader.Name}");
                AdvanceScene(2);
                if (session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && Vector3.Distance(c.Position, at) < 120).OrderBy(c => Vector3.DistanceSquared(c.Position, at)).FirstOrDefault() is { } enemy)
                    world.Commands.Enqueue(new Meitou.Simulation.Combat.AttackOrder(members, enemy.Id) { Tick = world.Tick });
                return null;
            }
            case "turret":
            {
                if (g.SceneTown is { } townName && popData is { } towns)
                {
                    var match = towns.Sites.Where(s => s.Town.Name.Contains(townName, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Town.Name.Length).FirstOrDefault();
                    if (match is null) { Console.WriteLine($"scene     no town matches '{townName}'"); return null; }
                    world.Commands.Enqueue(new Meitou.Simulation.DebugTeleport(members, match.Position) { Tick = tick });
                    Console.WriteLine($"scene     turret: the squad goes to {match.Town.Name} at {match.Position.X:0}, {match.Position.Z:0}");
                    session.Camera.MovePivot(new Vector2(match.Position.X, match.Position.Z));
                    // The town's residents spawn once its zones are loaded: wait until someone is there (at most 1200 ticks).
                    for (int k = 0; k < 40 && !session.CurrentSnapshot.Characters.Any(c => !c.IsPlayer && Vector3.Distance(c.Position, match.Position) < 1500); k++) AdvanceScene(30);
                    AdvanceScene(60);
                    if (population is not null) foreach (var line in population.TakeTownLog()) Console.WriteLine($"town      {line}");
                    at = match.Position;
                    tick = world.Tick;
                }
                Meitou.Data.World.TownBuilding? best = null;
                int bestSite = -1;
                float bestD = 5000 * 5000;
                foreach (var site in popData?.Sites ?? [])
                    foreach (var b in site.Buildings)
                        if (b.Turret is not null && Vector3.DistanceSquared(b.Position, at) is var d && d < bestD) (best, bestSite, bestD) = (b, site.Index, d);
                if (best?.Turret is not { } post) { Console.WriteLine("scene     no town turret within 5000"); return null; }
                var forward = new Vector2(MathF.Sin(post.Yaw), MathF.Cos(post.Yaw));
                var spot = new Vector2(post.Position.X, post.Position.Z) + forward * 250;
                if (DebugRecord(g.SceneNpc, character: true) is { } npc)
                    for (int k = 0; k < 3; k++)
                        world.Commands.Enqueue(new Meitou.Simulation.DebugSpawn(npc.Id, spot + new Vector2(k * 20, 0)) { Tick = tick });
                world.Commands.Enqueue(new Meitou.Simulation.DebugTeleport(members, new Vector3(post.Position.X - forward.X * 120, post.Position.Y, post.Position.Z - forward.Y * 120)) { Tick = tick });
                Console.WriteLine($"scene     turret: {best.Name} ({best.InstanceId}) at {post.Position.X:0}, {post.Position.Z:0}, gun {post.GunId}; enemies at {spot.X:0}, {spot.Y:0}");
                // The spawned enemies' faction (the first one hostile to the player) need not be the town's enemy: the scene sets them at war, so its guards see enemies.
                AdvanceScene(2);
                int town = popData is { } pd && bestSite >= 0 && bestSite < pd.SiteFactions.Count ? pd.SiteFactions[bestSite] : -1;
                var spawned = session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && Vector2.Distance(new Vector2(c.Position.X, c.Position.Z), spot) < 80).ToList();
                if (town >= 0 && spawned.Count > 0 && popData is { } rel)
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
                if (session.CurrentSnapshot.Beds.OrderBy(b => b.Owner == own ? 0 : 1).ThenBy(b => Vector3.DistanceSquared(b.Position, at)).FirstOrDefault() is not { } bed) { Console.WriteLine("scene     no bed"); return null; }
                world.Commands.Enqueue(new Meitou.Simulation.Locks.UseBedOrder([leader.Id], bed.Id) { Tick = tick });
                Console.WriteLine($"scene     bed: {leader.Name} to {bed.Id} ({bed.Record}, {(bed.Owner == own ? "the player's" : $"faction {bed.Owner}'s")}) at {bed.Position.X:0}, {bed.Position.Z:0}, {Vector3.Distance(bed.Position, at):0} away");
                return null;
            }
            case "acid":
                hostWeather ??= new HostWeather(() => gpu.Weather);
                hostWeather.Forced = new WeatherHere(Meitou.Data.World.WeatherAffecting.Acid, 1, 0, 0);
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
            session.Tick();
            session.AdvanceSimulation(session.Ticks.TickSeconds);
            UpdateNav(wait: true);
        }
    }

    /// <summary>The leader's state for the log: task, activity, weapon, turret, bed, weather notice, blood and the parts' flesh.</summary>
    string SceneState()
    {
        if (playerSquad is null || session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == playerSquad.Leader) is not { } me) return "no leader";
        string blood = me.Hud is { } hud ? $"blood {hud.Blood:0.0}/{hud.BloodCapacity:0}, bleeding {hud.Bleeding:0.000}" : "";
        string parts = me.HealthParts.Count > 0 ? "parts flesh/stun " + string.Join(" ", me.HealthParts.Select(p => $"{p.Flesh.ToString("0", CultureInfo.InvariantCulture)}/{p.Stun.ToString("0.0", CultureInfo.InvariantCulture)}")) : "";
        var enemies = session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && c.FightsPlayer).ToList();
        string turrets = string.Join(", ", session.CurrentSnapshot.Characters.Where(c => c.Turret is not null).Select(c => $"{c.Name} on {c.Turret!.BuildingId}"));
        var near = session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && Vector3.Distance(c.Position, me.Position) < 200)
            .Select(c => $"{c.Name} [{c.Task}{(session.World.Characters.TryResolveNext(c.Id, out int cs) && session.World.Characters.Cold(cs) is { } cc ? $" faction {cc.Faction}{(cc.Fleeing ? ", fleeing" : "")}{(cc.Fighter is { CanAttack: false } ? ", no attack" : "")}" : "")}{(c.InCombat ? ", fighting" : "")}{(c.FightsPlayer ? ", vs player" : "")}{(c.Body is { Unconscious: true } ? ", down" : c.Body is { Dead: true } ? ", dead" : "")}]");
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
        int frames = scene == "menu" ? SceneMenus.Length : g.SceneFrames;
        for (int f = 0; f < frames; f++)
        {
            if (scene != "menu") AdvanceScene(g.SceneEvery);
            var lead = playerSquad is null ? null : session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == playerSquad.Leader);
            var watch = focus ?? lead?.Position;
            if (watch is { } p)
            {
                session.Camera.MovePivot(new Vector2(p.X, p.Z));
                camera.Target = p;
            }
            ApplyCamera(session.Camera.Current);
            UpdateBuildings();
            gpu.Characters?.Settle(camera.Eye);
            gpu.Objects?.Settle(camera.Eye);
            DrawWorld(w, h);
            if (overlay is not null)
            {
                overlay.Target = target;
                UpdateView(w, h);
                if (scene == "menu" && lead is not null && !player.OpenSceneMenu(SceneMenus[f], lead.Position)) Console.WriteLine($"scene     menu {SceneMenus[f]}: none on screen");
                player.Draw(overlay, w, h);
            }
            display.EndFrame();
            display.Context.Finish();
            string output = Path.Combine(Path.GetDirectoryName(o.Screenshot!) ?? "", $"{Path.GetFileNameWithoutExtension(o.Screenshot!)}-{scene}-{f:00}.png");
            FramebufferCapture.SavePng(display.Context, target, output, w, h);
            Console.WriteLine($"scene     frame {f} tick {session.World.Tick}: {SceneState()}");
            Console.WriteLine($"saved     {Path.GetFullPath(output)}");
        }
    }
}

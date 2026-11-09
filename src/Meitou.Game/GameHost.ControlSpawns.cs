using Meitou.Game.DevTools;
using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>Debug aids of the control channel for the spawn rules (docs/agent-playtest.md): <c>spawn-trace</c> and <c>birds</c>.</summary>
sealed partial class GameHost
{
    internal void RegisterControlSpawnCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add, Func<DevConsole, bool> loaded)
    {
        add("spawn-trace", "spawn-trace on|off|clear|summary|dump <tsv path>", "debug: record every squad the population makes (not the player's): tick, source (sector:<group> / nest:<town> / town / roam), template, leader record, members, position, terrain height and water depth there (100 minus the height), the walkability's answer and the distance to the nearest player character at the time; summary lists the counts per leader record with how many stand in water deeper than 5; dump writes the rows as TSV", (args, con) =>
        {
            if (!loaded(con) || Population is null) return;
            string verb = args.Count > 0 ? args[0].ToLowerInvariant() : "summary";
            switch (verb)
            {
                case "on": Population.SpawnTrace ??= []; con.Print("{\"trace\":\"on\"}"); return;
                case "off": Population.SpawnTrace = null; con.Print("{\"trace\":\"off\"}"); return;
                case "clear": Population.SpawnTrace?.Clear(); con.Print("{\"cleared\":true}"); return;
            }
            var rows = Population.SpawnTrace;
            if (rows is null) { con.Error("spawn-trace: not on"); return; }
            var walk = Session.World.Walkability;
            var lines = rows.Select(r =>
            {
                float h = walk.TerrainHeight(r.X, r.Z);
                return (Row: r, Height: h, Depth: Meitou.Data.World.WorldWater.Height - h, Walk: walk.IsWalkable(r.X, r.Z));
            }).ToList();
            if (verb == "dump" && args.Count == 2)
            {
                var sb = new StringBuilder("tick\tsource\ttemplate\tleader\tmembers\tx\tz\theight\tdepth\twalkable\tplayerDist\n");
                foreach (var (r, h, d, w) in lines)
                    sb.Append(CultureInfo.InvariantCulture, $"{r.Tick}\t{r.Source}\t{r.TemplateId}\t{r.LeaderRecord}\t{r.Members}\t{r.X:0}\t{r.Z:0}\t{h:0.0}\t{d:0.0}\t{w}\t{r.PlayerDistance:0}\n");
                File.WriteAllText(args[1], sb.ToString());
                con.Print($"{{\"rows\":{lines.Count}}}");
                return;
            }
            var by = lines.GroupBy(l => l.Row.LeaderRecord).OrderByDescending(g => g.Count())
                .Select(g => $"{{\"leader\":\"{g.Key}\",\"n\":{g.Count()},\"inWater\":{g.Count(l => l.Depth > 5)},\"avgPlayerDist\":{g.Average(l => l.Row.PlayerDistance):0}}}");
            con.Print($"{{\"rows\":{lines.Count},\"inWater\":{lines.Count(l => l.Depth > 5)},\"byLeader\":[{string.Join(",", by)}]}}");
        });

        add("birds", "birds [near <m>]", "debug: the bird kinds now: alive, within <m> (default 5000) of the camera, the cell sum the spawn rule compares with (birds alive + spawn count <= sum), the cells listing the kind in range, the kind's distance and spawn count", (args, con) =>
        {
            if (Birds?.Flocks is not { } flocks) { con.Error("birds: not made yet"); return; }
            float near = args.Count == 2 && DevTools.Agents.AgentWorld.Num(args[1], out float n) ? n : 5000;
            var rows = flocks.Stats(Camera.Eye, near).Select(k =>
                $"{{\"kind\":\"{k.Kind}\",\"alive\":{k.Alive},\"near\":{k.Near},\"sum\":{k.Total},\"cells\":{k.Cells},\"distance\":{k.Distance.ToString(CultureInfo.InvariantCulture)},\"spawnCount\":{k.SpawnCount}}}");
            con.Print($"{{\"total\":{flocks.BirdCount},\"eye\":[{Camera.Eye.X:0},{Camera.Eye.Z:0}],\"kinds\":[{string.Join(",", rows)}]}}");
        });
    }
}

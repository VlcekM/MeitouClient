using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

/// <summary>
/// TEST ONLY <c>pg</c> verbs of task 4.1 (refused until <c>pg spikes on</c>; divergences.md "Test-only commands"), promoted from spike S15
/// (src/Spikes/Spikes.cs, M0) for the scripted live runs: <c>pg place</c>, <c>pg findspot</c>, <c>pg finish-build</c> place and finish a
/// PLAYER-OWNED building by the player's own construction path (the dev console's <c>place-building</c> makes a level-editor building that is not
/// the player's); <c>pg sites</c> and <c>pg findsite</c> show what ArenaIdentity sees. Each answers ONE JSON line in log.txt
/// (<c>PG-PLACE</c>, <c>PG-FINDSPOT</c>, <c>PG-FINISH-BUILD</c>, <c>PG-SITES</c>, <c>PG-FINDSITE</c>, <c>PG-RESOLVE</c>), a failure (an exception
/// included) as an ok:false line. Sim thread, serial Inputs.
/// </summary>
static class ArenaIdentityTestVerbs
{
    internal static readonly string[] Verbs = ["place", "findspot", "finish-build", "sites", "findsite", "resolve"];

    /// <summary>The friendly names of <c>pg place</c> / <c>pg findspot</c> (any other word is a BUILDING record id). The cage is the only cage
    /// unlocked by agent <c>research</c> (spike S15: Prisoner Cage).</summary>
    internal static string RecordOf(string word) => word switch
    {
        "registry" => PgRecords.Registry,
        "arena" => PgRecords.Arena,
        "small-arena" => PgRecords.SmallArena,
        "banner" => PgRecords.Banner,
        "leaderboard" => PgRecords.Leaderboard,
        "cage" => "1783-gamedata.base",
        _ => word,
    };

    static bool TryLocation(string word, out ArenaLocationMode mode)
    {
        switch (word)
        {
            case "arena": mode = ArenaLocationMode.Arena; return true;
            case "small-arena" or "small": mode = ArenaLocationMode.SmallArena; return true;
            case "banner": mode = ArenaLocationMode.Banner; return true;
            default: mode = ArenaLocationMode.Arena; return false;
        }
    }

    static string G(double v) => TestJson.G(v);   // task 8.8: NaN -> null
    static string Q(string? s) => PGLog.Quote(s);
    static bool TryF(string s, out float f) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) && float.IsFinite(f);

    static string Line(string tag, StringBuilder json)
    {
        string text = json.ToString();
        ProvingGroundsMod.Ctx?.Log(tag + " " + text);
        return text;
    }

    static StringBuilder Start(World world, string verb, bool ok, string? error)
    {
        var sb = new StringBuilder();
        sb.Append("{\"verb\":").Append(Q(verb)).Append(",\"ok\":").Append(ok ? "true" : "false")
          .Append(",\"tick\":").Append(world.Tick.ToString(CultureInfo.InvariantCulture));
        if (error is not null) sb.Append(",\"error\":").Append(Q(error));
        return sb;
    }

    static string Fail(World world, string tag, string verb, string error) => Line(tag, Start(world, verb, false, error).Append('}'));

    static ConstructionSystem? Construction(World world) => world.Systems.OfType<ConstructionSystem>().FirstOrDefault();

    internal static string Run(World world, PopulationData data, ConsoleLine pg)
    {
        try
        {
            return pg.Verb switch
            {
                "place" => Place(world, pg.Args),
                "findspot" => FindSpot(world, pg.Args),
                "finish-build" => FinishBuild(world, pg.Args),
                "sites" => Sites(world, data, pg.Args),
                "findsite" => FindSite(world, data, pg.Args),
                "resolve" => Resolve(world, data, pg.Args),
                _ => Fail(world, "PG-" + pg.Verb.ToUpperInvariant(), pg.Verb, "unknown verb " + pg.Verb),
            };
        }
        catch (Exception e)   // the one-JSON-line contract holds on a throw too
        {
            return Fail(world, "PG-" + pg.Verb.ToUpperInvariant(), pg.Verb, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary><c>pg place &lt;what&gt; &lt;x&gt; &lt;z&gt; [y]</c>: the build-mode Confirm command <c>PlaceBuildings</c> (public, src/Meitou.Simulation/
    /// Construction/ConstructionCommands.cs:16) with a player-owned <c>PlannedBuilding</c>, without the UI's placement rules (the simulation's own
    /// town rule still refuses a town-making building near a town, ConstructionSystem.Place :617). y defaults to the first selected character's height.</summary>
    static string Place(World world, IReadOnlyList<string> args)
    {
        const string tag = "PG-PLACE";
        if (args.Count < 3 || !TryF(args[1], out float x) || !TryF(args[2], out float z))
            return Fail(world, tag, "place", "usage pg place <registry|arena|small-arena|banner|leaderboard|cage|record> <x> <z> [y]");
        string record = RecordOf(args[0]);
        float y = args.Count > 3 && TryF(args[3], out float yy) ? yy
            : world.Player.Selection.Count > 0 && PgEngine.Position(world, world.Player.Selection[0]) is { } p ? p.Y : 0;
        world.Commands.Enqueue(new PlaceBuildings([new PlannedBuilding(record, new Vector3(x, y, z), Quaternion.Identity)]) { Tick = world.Tick + 1 });
        return Line(tag, Start(world, "place", true, null).Append(",\"record\":").Append(Q(record))
            .Append(",\"x\":").Append(G(x)).Append(",\"y\":").Append(G(y)).Append(",\"z\":").Append(G(z))
            .Append(",\"queued\":").Append((world.Tick + 1).ToString(CultureInfo.InvariantCulture)).Append('}'));
    }

    /// <summary><c>pg findspot &lt;what&gt; &lt;x&gt; &lt;z&gt;</c>: the nearest point (rings of 500 units, up to 60,000) the simulation's own town rule
    /// accepts for the record (public <c>PlacementRules.TownBlock</c> over ConstructionSystem's private <c>_towns</c> / <c>_playerFactionId</c>,
    /// ConstructionSystem.cs:134, :139, :617).</summary>
    static string FindSpot(World world, IReadOnlyList<string> args)
    {
        const string tag = "PG-FINDSPOT";
        if (args.Count < 3 || !TryF(args[1], out float x) || !TryF(args[2], out float z))
            return Fail(world, tag, "findspot", "usage pg findspot <what|record> <x> <z>");
        var cons = Construction(world);
        if (cons is null || Members.ConstructionTowns?.GetValue(cons) is not IReadOnlyList<PlacementTown> towns
            || Members.ConstructionPlayerFactionId?.GetValue(cons) is not string pf)
            return Fail(world, tag, "findspot", "ConstructionSystem members missing: " + string.Join(", ", Members.Missing(Part.TestVerbs)));
        string record = RecordOf(args[0]);
        if (cons.Data(record) is not { } data) return Fail(world, tag, "findspot", "no building data " + record);
        for (int r = 0; r <= 60000; r += 500)
        {
            int n = Math.Max(1, r / 250);
            for (int k = 0; k < n; k++)
            {
                double a = 2 * Math.PI * k / n;
                var at = new Vector2(x + (float)(r * Math.Cos(a)), z + (float)(r * Math.Sin(a)));
                if (PlacementRules.TownBlock(data, at, towns, pf) != PlacementFailure.None) continue;
                return Line(tag, Start(world, "findspot", true, null).Append(",\"record\":").Append(Q(record))
                    .Append(",\"x\":").Append(G(MathF.Round(at.X))).Append(",\"z\":").Append(G(MathF.Round(at.Y)))
                    .Append(",\"distance\":").Append(r.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"createsPlayerTown\":").Append(data.CreatesPlayerTown ? "true" : "false").Append('}'));
            }
        }
        return Fail(world, tag, "findspot", "none within 60000");
    }

    /// <summary><c>pg finish-build &lt;siteId|all&gt;</c>: every material of the player's unfinished site(s) delivered, then the engine's own
    /// private <c>ConstructionSystem.Finish(World, BuildSite)</c> (ConstructionSystem.cs:1386-1396: complete, "construction complete" notice).</summary>
    static string FinishBuild(World world, IReadOnlyList<string> args)
    {
        const string tag = "PG-FINISH-BUILD";
        if (args.Count < 1) return Fail(world, tag, "finish-build", "usage pg finish-build <siteId|all>");
        var cons = Construction(world);
        if (cons is null || Members.ConstructionFinish is null) return Fail(world, tag, "finish-build", "no ConstructionSystem.Finish");
        if (args[0].StartsWith("town:", StringComparison.Ordinal))
            return Fail(world, tag, "finish-build", "a town building has no construction site to finish (player sites only)");
        string id = args[0].StartsWith("b:", StringComparison.Ordinal) ? args[0][2..]
            : args[0].StartsWith("player:", StringComparison.Ordinal) ? args[0][7..] : args[0];
        int pf = world.Player.Faction;
        static bool Due(BuildSite s) => !s.Complete && !s.Destroyed;
        if (args[0] != "all" && world.Buildings.Find(id) is { LevelPlacement: not null })
            return Fail(world, tag, "finish-build", "a town house under repair (its site has a level placement): not a player building to finish");
        List<BuildSite> sites = args[0] == "all" ? [.. world.Buildings.All.Where(s => s.Owner == pf && Due(s))]
            : world.Buildings.Find(id) is { } one && Due(one) ? [one] : [];
        var sb = Start(world, "finish-build", true, null).Append(",\"finished\":[");
        bool first = true;
        foreach (var s in sites)
        {
            foreach (var m in s.Materials) m.Delivered = m.Required;
            Members.ConstructionFinish.Invoke(cons, [world, s]);
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":").Append(Q(s.Id)).Append(",\"record\":").Append(Q(s.Record)).Append(",\"complete\":").Append(s.Complete ? "true" : "false").Append('}');
        }
        return Line(tag, sb.Append("]}"));
    }

    /// <summary>The selected character, else the first member of the player's first squad (none: null).</summary>
    static CharacterId? Lead(World world, bool selectionFirst)
    {
        if (selectionFirst && world.Player.Selection.Count > 0 && PgEngine.Exists(world, world.Player.Selection[0])) return world.Player.Selection[0];
        foreach (var squad in PgEngine.PlayerSquads(world))
            foreach (var m in squad.Members)
                if (PgEngine.Exists(world, m)) return m;
        return null;
    }

    static void AppendBuilding(StringBuilder sb, ArenaBuilding b, Vector3? from)
    {
        sb.Append("{\"kind\":").Append(Q(ArenaIdentityLogic.KindOf(b).ToString())).Append(",\"record\":").Append(Q(b.RecordId))
          .Append(",\"finished\":").Append(b.Finished ? "true" : "false")
          .Append(",\"x\":").Append(G(b.Position.X)).Append(",\"y\":").Append(G(b.Position.Y)).Append(",\"z\":").Append(G(b.Position.Z))
          .Append(",\"yaw\":").Append(G(b.Yaw)).Append(",\"source\":").Append(Q(b.Source.ToString())).Append(",\"key\":").Append(Q(ArenaIdentity.KeyText(b.Ref)))
          .Append(",\"func\":").Append(b.FunctionRecordId is null ? "null" : Q(b.FunctionRecordId));
        if (from is { } f)
        {
            float dx = b.Position.X - f.X, dz = b.Position.Z - f.Z;
            sb.Append(",\"distXZ\":").Append(G(MathF.Sqrt(dx * dx + dz * dz)));
        }
        sb.Append('}');
    }

    /// <summary><c>pg sites [radius]</c>: every building ArenaIdentity recognises within the radius (default 25,000, the site search's) of the
    /// selected character (else the squad's first member): kind, record, finished, position, yaw, source, key.</summary>
    static string Sites(World world, PopulationData data, IReadOnlyList<string> args)
    {
        const string tag = "PG-SITES";
        float radius = ArenaIdentityLogic.SiteSearchRadius;
        if (args.Count > 0 && (!TryF(args[0], out radius) || radius < 0)) return Fail(world, tag, "sites", "usage pg sites [radius]");
        if (Lead(world, true) is not { } lead || PgEngine.Position(world, lead) is not { } at) return Fail(world, tag, "sites", "no selected or squad character");
        var list = ArenaIdentity.RecognisedNear(world, data, at, radius);
        var sb = Start(world, "sites", true, null).Append(",\"from\":").Append(Q(CharacterIdText.Format(lead)))
            .Append(",\"x\":").Append(G(at.X)).Append(",\"z\":").Append(G(at.Z)).Append(",\"radius\":").Append(G(radius)).Append(",\"sites\":[");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendBuilding(sb, list[i], at);
        }
        return Line(tag, sb.Append("]}"));
    }

    /// <summary><c>pg findsite &lt;arena|small-arena|banner&gt;</c>: ArenaIngress's <c>FindSiteForMode(mode, ResolveSearchOrigin(fighters))</c> with no
    /// bound Registry and the squad's first member as the first fighter (ArenaIngress.cpp:213-233); the C++ "site search" debug line is logged too.</summary>
    static string FindSite(World world, PopulationData data, IReadOnlyList<string> args)
    {
        const string tag = "PG-FINDSITE";
        if (args.Count < 1 || !TryLocation(args[0], out var mode)) return Fail(world, tag, "findsite", "usage pg findsite <arena|small-arena|banner>");
        var lead = Lead(world, false);
        var origin = ArenaIdentity.ResolveSearchOrigin(world, data, null, lead is { } l ? [l] : []);
        var site = ArenaIdentity.FindSite(world, data, mode, origin);
        var sb = Start(world, "findsite", true, null).Append(",\"mode\":").Append(Q(mode.ToString()))
            .Append(",\"from\":").Append(lead is { } f ? Q(CharacterIdText.Format(f)) : "null")
            .Append(",\"x\":").Append(G(origin.X)).Append(",\"z\":").Append(G(origin.Z)).Append(",\"site\":");
        if (site is { } s) AppendBuilding(sb, s, origin); else sb.Append("null");
        return Line(tag, sb.Append('}'));
    }

    /// <summary><c>pg resolve &lt;player:id|town:key&gt; &lt;record|registry|arena|...&gt;</c>: ArenaIdentity.Resolve of that ref (source, key, record)
    /// as it is now: the building (kind, finished, position, yaw, key) or null (gone, or the key names another record).</summary>
    static string Resolve(World world, PopulationData data, IReadOnlyList<string> args)
    {
        const string tag = "PG-RESOLVE";
        if (args.Count < 2 || !ArenaIdentity.TryParseKey(args[0], out var source, out string key))
            return Fail(world, tag, "resolve", "usage pg resolve <player:id|town:key> <record|registry|arena|small-arena|banner|leaderboard|cage>");
        var held = new ArenaBuildingRef(source, key, RecordOf(args[1]));
        var now = ArenaIdentity.Resolve(world, data, held);
        var sb = Start(world, "resolve", true, null).Append(",\"ref\":{\"key\":").Append(Q(ArenaIdentity.KeyText(held)))
            .Append(",\"record\":").Append(Q(held.RecordId)).Append("},\"building\":");
        if (now is { } b) AppendBuilding(sb, b, null); else sb.Append("null");
        return Line(tag, sb.Append('}'));
    }
}

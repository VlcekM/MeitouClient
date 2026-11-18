using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Locks;
using ProvingGrounds.Core;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

/// <summary>
/// TEST ONLY <c>pg</c> verbs of task 6.1 (refused until <c>pg spikes on</c>; divergences.md "Test-only commands"), answered as ONE JSON line in
/// log.txt each (a failure, an exception included, as an ok:false line). Sim thread, serial Inputs.
/// <list type="bullet">
/// <item><c>pg prisoners</c> (<c>PG-PRISONERS</c>): the discovery the Arena window runs (PrisonerUtil.CollectNearbyCagePrisoners around the
/// Registry the window is bound to), then the cages it found (id, record, building, occupant, lock), the roster and match prisoners, the
/// handlers, and every player-owned cage anywhere (with its distance to that Registry).</item>
/// <item><c>pg imprison &lt;id&gt; &lt;cage site or fixture&gt;</c> (<c>PG-IMPRISON</c>): spike S11's recipe (LetGo if carried, Release if
/// imprisoned, then Imprison, which locks the cage), promoted from src/Spikes/Spikes.cs: the spike verb <c>pg cage</c> now runs this one
/// copy.</item>
/// </list>
/// <c>pg ko</c> needs no world: PgConsole enqueues Meitou's DebugKnockOut itself.
/// </summary>
static class PrisonerTestVerbs
{
    internal static readonly string[] Verbs = ["prisoners", "imprison"];

    static string Q(string? s) => PGLog.Quote(s);
    static string G(double v) => TestJson.G(v);   // task 8.8: NaN -> null
    static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    static string Id(CharacterId c) => c.IsNone ? "null" : Q(CharacterIdText.Format(c));

    static string Line(string tag, StringBuilder json)
    {
        string text = json.ToString();
        ProvingGroundsMod.Ctx?.Log(tag + " " + text);
        return text;
    }

    static StringBuilder Start(World world, string verb, bool ok, string? error)
    {
        var sb = new StringBuilder();
        sb.Append("{\"verb\":").Append(Q(verb)).Append(",\"ok\":").Append(ok ? "true" : "false").Append(",\"tick\":").Append(N(world.Tick));
        if (error is not null) sb.Append(",\"error\":").Append(Q(error));
        return sb;
    }

    static string Fail(World world, string verb, string error) => Line("PG-" + verb.ToUpperInvariant(), Start(world, verb, false, error).Append('}'));

    internal static string Run(World world, PrisonerUtil prisoners, ArenaIngress ingress, ConsoleLine pg)
    {
        try
        {
            return pg.Verb switch
            {
                "prisoners" => Prisoners(world, prisoners, ingress),
                "imprison" => Imprison(world, pg.Args),
                _ => Fail(world, pg.Verb, "unknown verb " + pg.Verb),
            };
        }
        catch (Exception e) { return Fail(world, pg.Verb, $"{e.GetType().Name}: {e.Message}"); }
    }

    static void Ids(StringBuilder sb, string key, IReadOnlyList<CharacterId> ids)
    {
        sb.Append(",\"").Append(key).Append("\":[");
        for (int i = 0; i < ids.Count; i++) sb.Append(i == 0 ? "" : ",").Append(Id(ids[i]));
        sb.Append(']');
    }

    static void Cage(StringBuilder sb, World world, Fixture f, Vector3? registry)
    {
        var occ = Cages.Occupant(world, f);
        sb.Append("{\"id\":").Append(Q(f.Id)).Append(",\"kind\":").Append(Q(f.Kind.ToString())).Append(",\"record\":").Append(Q(f.RecordId))
          .Append(",\"building\":").Append(Q(f.Building)).Append(",\"owner\":").Append(N(f.Owner))
          .Append(",\"player\":").Append(f.Owner >= 0 && f.Owner == world.Player.Faction ? "true" : "false")
          .Append(",\"occupant\":").Append(Id(occ)).Append(",\"occupantName\":").Append(occ.IsNone ? "null" : Q(PgEngine.Name(world, occ)))
          .Append(",\"locked\":").Append(f.Lock?.Locked == true ? "true" : "false")
          .Append(",\"effective\":").Append(f.Lock?.EffectivelyLocked == true ? "true" : "false")
          .Append(",\"level\":").Append(N(f.Lock?.Level ?? 0))
          .Append(",\"x\":").Append(G(f.Position.X)).Append(",\"z\":").Append(G(f.Position.Z));
        if (registry is { } r)
        {
            float dx = f.Position.X - r.X, dz = f.Position.Z - r.Z;
            sb.Append(",\"distXZ\":").Append(G(MathF.Sqrt(dx * dx + dz * dz)));
        }
        sb.Append('}');
    }

    /// <summary><c>pg prisoners</c>: the window's discovery around the bound Registry (none bound: the C++'s "skipped" line and an empty roster).
    /// It RE-RUNS the discovery, so it replaces the roster and <c>NearbyCages</c>, the list a match entry's <c>OriginalCageIndex</c> refers to (as
    /// the Arena window's own refresh does): a scenario must not call it between Start and the return expecting the indices PrepareMatch saw.</summary>
    static string Prisoners(World world, PrisonerUtil prisoners, ArenaIngress ingress)
    {
        var registry = ingress.GetBoundRegistry(world);
        var roster = new List<CharacterId>();
        prisoners.CollectNearbyCagePrisoners(world, registry, roster);
        var sb = Start(world, "prisoners", true, null);
        sb.Append(",\"registry\":").Append(registry is { } r ? Q(ArenaIdentity.KeyText(r.Ref)) : "null");
        sb.Append(",\"cages\":[");
        var near = prisoners.State.NearbyCages;
        for (int i = 0; i < near.Count; i++)
        {
            if (i > 0) sb.Append(',');
            if (Cages.Cage(world, near[i].Useable) is { } f) Cage(sb, world, f, registry?.Position);
            else sb.Append("{\"id\":").Append(Q(near[i].Useable)).Append(",\"kind\":null}");
        }
        sb.Append(']');
        Ids(sb, "roster", prisoners.GetRosterPrisoners(world));
        Ids(sb, "match", [.. prisoners.GetMatchPrisoners(world)]);
        Ids(sb, "handlers", prisoners.GetMatchHandlers(world));
        var chained = roster.Where(c => PgEngine.IsChained(world, c)).ToList();
        Ids(sb, "chained", chained);
        sb.Append(",\"matchIncludesPrisoner\":").Append(prisoners.MatchIncludesPrisoner(world) ? "true" : "false")
          .Append(",\"runtimeActivity\":").Append(prisoners.HasRuntimeActivity(world) ? "true" : "false")
          .Append(",\"returning\":").Append(prisoners.IsReturning(world) ? "true" : "false")
          .Append(",\"pendingLocks\":").Append(prisoners.HasPendingCageLocks(world) ? "true" : "false")
          .Append(",\"cageReturnFailed\":").Append(prisoners.HasCageReturnFailure(world) ? "true" : "false")
          .Append(",\"cageReturnStatus\":").Append(Q(prisoners.GetCageReturnStatus(world)));
        Entries(sb, prisoners);
        sb.Append(",\"aid\":{\"treatsAsked\":").Append(N(prisoners.TreatsAsked)).Append(",\"treatsStarted\":").Append(N(prisoners.TreatsStarted))
          .Append(",\"liftsAsked\":").Append(N(prisoners.LiftsAsked)).Append(",\"liftsStarted\":").Append(N(prisoners.LiftsStarted)).Append('}')
          .Append(",\"protection\":{\"passes\":").Append(N(prisoners.ProtectionPasses)).Append(",\"outsiderBreakOffs\":")
          .Append(N(prisoners.OutsiderBreakOffs)).Append(",\"prisonerBreakOffs\":").Append(N(prisoners.PrisonerBreakOffs)).Append(",\"outsiders\":[");
        for (int i = 0; i < prisoners.OutsidersBrokenOff.Count; i++)
        {
            var (oid, count) = prisoners.OutsidersBrokenOff[i];
            sb.Append(i == 0 ? "" : ",").Append("{\"id\":").Append(Id(oid)).Append(",\"name\":").Append(Q(PgEngine.Name(world, oid)))
              .Append(",\"player\":").Append(PgEngine.IsPlayer(world, oid) ? "true" : "false").Append(",\"count\":").Append(N(count)).Append('}');
        }
        sb.Append("]}");
        var owned = new List<string>();
        PrisonerUtilInternal.CollectPlayerOwnedCageBuildings(world, owned);
        sb.Append(",\"ownedCages\":[");
        for (int i = 0; i < owned.Count; i++)
        {
            if (i > 0) sb.Append(',');
            if (Cages.Cage(world, owned[i]) is { } f) Cage(sb, world, f, registry?.Position);
        }
        sb.Append("]}");
        return Line("PG-PRISONERS", sb);
    }

    /// <summary>Task 6.3: each match entry's release / return / lock state (the C++ MatchEntry fields the return advances).</summary>
    static void Entries(StringBuilder sb, PrisonerUtil prisoners)
    {
        static string B(bool v) => v ? "true" : "false";
        sb.Append(",\"entries\":[");
        var entries = prisoners.State.MatchEntries;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            sb.Append(i == 0 ? "" : ",").Append("{\"prisoner\":").Append(Id(e.Prisoner)).Append(",\"handler\":").Append(Id(e.Handler))
              .Append(",\"cage\":").Append(Q(e.CageHand)).Append(",\"returnCage\":").Append(Q(e.ReturnCageHand))
              .Append(",\"released\":").Append(B(e.Released)).Append(",\"returnPending\":").Append(B(e.ReturnPending))
              .Append(",\"escorting\":").Append(B(e.ReturnEscorting)).Append(",\"carrying\":").Append(B(e.ReturnCarrying))
              .Append(",\"pickedUp\":").Append(B(e.ReturnPickedUp)).Append(",\"treating\":").Append(B(e.ReturnTreating))
              .Append(",\"returnElapsed\":").Append(G(e.ReturnElapsed)).Append(",\"lockPending\":").Append(B(e.LockPending))
              .Append(",\"ringsideTreating\":").Append(B(e.RingsideTreating)).Append(",\"ringsideElapsed\":").Append(G(e.RingsideTreatElapsed))
              .Append('}');
        }
        sb.Append(']');
    }

    /// <summary>The cage fixture a site or fixture id names (a fixture id first, else the first cage of that building), as spike S11's CageOf.</summary>
    internal static Fixture? CageOf(World world, string site)
    {
        if (site.StartsWith("b:", StringComparison.Ordinal)) site = site[2..];
        return Cages.Cage(world, site) ?? world.Fixtures.OfBuilding(site).FirstOrDefault(f => f.Kind == FixtureKind.Cage);
    }

    /// <summary><c>pg imprison &lt;id&gt; &lt;cage site or fixture&gt;</c> (spike S11's recipe, Cages.Imprison).</summary>
    static string Imprison(World world, IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !CharacterIdText.TryParse(args[0], out var who)) return Fail(world, "imprison", "usage pg imprison <c:slot.gen> <cage site or fixture id>");
        if (CageOf(world, args[1]) is not { } cage) return Fail(world, "imprison", "no cage fixture for " + args[1]);
        var (found, released, imprisoned) = Cages.Imprison(world, who, cage);
        if (!found) return Fail(world, "imprison", "prisoner not found");
        var cold = world.Characters.TryResolveNext(who, out int s) ? world.Characters.Cold(s) : null;
        var sb = Start(world, "imprison", imprisoned, imprisoned ? null : "Imprison refused (cage taken, or the character held, carried or carrying)");
        sb.Append(",\"id\":").Append(Id(who)).Append(",\"cage\":").Append(Q(cage.Id))
          .Append(",\"released\":").Append(released ? "true" : "false").Append(",\"imprisoned\":").Append(imprisoned ? "true" : "false")
          .Append(",\"held\":").Append(Q(cold?.InSomething.ToString())).Append(",\"inWhat\":").Append(Q(cold?.InWhat))
          .Append(",\"locked\":").Append(cage.Lock?.Locked == true ? "true" : "false")
          .Append(",\"effective\":").Append(cage.Lock?.EffectivelyLocked == true ? "true" : "false").Append('}');
        return Line("PG-IMPRISON", sb);
    }
}

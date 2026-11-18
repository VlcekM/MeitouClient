using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Engine;

/// <summary>Task 8.6 (TW §2.11, CAP 14, spike S9): the corpse furnace and the removal of a fed body. Meitou 0.17.3 (fa34a1b).
/// <para>The furnace: a placed town building whose <c>TownBuilding.Function</c> is <c>TownBuildings.CorpseDisposalFunction</c> (11,
/// TownBuildings.cs:350; the C++ <c>getSpecialFunction() == BF_CORPSE_DISPOSAL</c>), listed by PgEngine's rule (not a ruin), its completion as
/// PgEngine.FromTown reads it, its label and use range from <c>ProductionSystem.Data(record)</c> (ProductionSystem.cs:181, MachineData.cs:86, 118,
/// 365, 371: never <c>TownWork</c>, which registers a machine). Player buildings are not searched: a player site's town is a player town, never the
/// arena's (the C++'s sameTown test would refuse it).</para>
/// <para>The removal (spike S9's fallback): the steps of the engine's private <c>Incinerate</c> (ProductionSystem.Errands.cs:375-383), after
/// SanitationCrew's Removal checks (not in a bed or a cage, carried by our handler only). <c>CharacterTable.Remove</c> throws outside the serial
/// commit / SlowWorld phases (CharacterTable.cs:1507-1510): call it only from TownArena.Tick (SlowWorld).</para></summary>
static class Corpses
{
    /// <summary>The removal's members (Members part <c>Corpses</c>) resolve; read once per process, a missing one logged once.</summary>
    internal static bool Available => available ??= Resolve();
    static bool? available;

    static bool Resolve()
    {
        var missing = Members.Missing(Part.Corpses).ToList();
        if (missing.Count == 0) return true;
        ProvingGroundsMod.Ctx?.Log($"corpse cleanup off: {string.Join(", ", missing)} not found; no corpse furnace is found and the dead are left to the town after 180 s");
        return false;
    }

    /// <summary>A town building as a <see cref="CorpseFurnace"/> (null when it is not a corpse-disposal building or not listed).</summary>
    /// The town and completion are PgEngine.FromTown's rule computed here without building its PgBuilding record (the poll runs every 500 ms;
    /// spec review 8.6): the site's town, and a town house under repair complete only once its repair site is (and not destroyed).
    static CorpseFurnace? FromTown(World world, TownSite site, Meitou.Data.World.TownBuilding b)
    {
        if (b.Function != Meitou.Data.World.TownBuildings.CorpseDisposalFunction || !PgEngine.IsListed(world, b)) return null;
        bool complete = world.Buildings.Find(b.Key) is { LevelPlacement: not null } repair ? repair.Complete && !repair.Destroyed : true;
        var machine = PgEngine.Production(world)?.Data(b.BuildingId);   // a cached record after the first read (ProductionSystem.cs:181-187)
        return new CorpseFurnace(b.Key, b.BuildingId, b.Position, Towns.Key(CampaignTown.OfSite(site.Index)), complete, machine?.Label ?? "",
            machine?.UseRange ?? 20f);
    }

    /// <summary>C++ <c>ou-&gt;getObjectsWithinSphere(origin, r, BUILDING, max)</c> + <c>Disposal</c>'s function test (TownCorpseCleanup.cpp:158-166):
    /// every listed corpse-disposal town building within <paramref name="radius"/> (3D) of <paramref name="origin"/>, into <paramref name="into"/>
    /// (cleared first), at most <paramref name="max"/>.</summary>
    internal static void FurnacesNear(World world, PopulationData data, Vector3 origin, float radius, int max, List<CorpseFurnace> into)
    {
        into.Clear();
        if (!Available) return;   // TownBuilding.Function and ProductionSystem.Data belong to part Corpses: no furnace without them
        float r2 = radius * radius;
        var sites = data.Sites;
        for (int si = 0; si < sites.Count; si++)
        {
            var site = sites[si];
            var buildings = site.Buildings;   // copy-on-write (PopulationData.cs:197-205): take it once
            for (int i = 0; i < buildings.Count && into.Count < max; i++)
            {
                var b = buildings[i];
                if (b.Function != Meitou.Data.World.TownBuildings.CorpseDisposalFunction) continue;
                if (Vector3.DistanceSquared(b.Position, origin) > r2) continue;
                if (FromTown(world, site, b) is { } f) into.Add(f);
            }
        }
    }

    /// <summary>The held furnace resolved again by its key (the C++ <c>furnace.getBuilding()</c> + <c>isValid()</c>): the hint is the site and
    /// list positions where it was found last (a site list is copy-on-write, so the key is checked there), else one scan of the placed towns.
    /// Null when it is gone, a ruin, or no longer a corpse-disposal building.</summary>
    internal static CorpseFurnace? Find(World world, PopulationData data, string key, ref (int Site, int Index) hint)
    {
        if (!Available) return null;
        var sites = data.Sites;
        if (hint.Site >= 0 && hint.Site < sites.Count)
        {
            var hs = sites[hint.Site];
            var hb = hs.Buildings;
            if (hint.Index >= 0 && hint.Index < hb.Count && hb[hint.Index].Key == key) return FromTown(world, hs, hb[hint.Index]);
        }
        for (int si = 0; si < sites.Count; si++)
        {
            var buildings = sites[si].Buildings;
            for (int i = 0; i < buildings.Count; i++)
            {
                if (buildings[i].Key != key) continue;
                hint = (si, i);
                return FromTown(world, sites[si], buildings[i]);
            }
        }
        hint = (-1, -1);
        return null;
    }

    /// <summary>The game's arrival rule for a machine (ProductionSystem.AtSite, ProductionSystem.cs:606-627, and public SiteReach, Construction/
    /// SiteReach.cs:32-47): the flat gap to the furnace's position under its use range, or under twice it once the walker has no path (stopped),
    /// and the height difference under <c>SiteReach.HeightLimit(false, false)</c> (45: a building standing outside any building; a town
    /// building has no door list here). The gap is to the building's position, not its footprint's edge (a town building has no BuildSite edge).
    /// False for a stale id.</summary>
    internal static bool AtFurnace(World world, CharacterId c, CorpseFurnace furnace)
    {
        if (!Available || !world.Characters.TryResolveNext(c, out int slot)) return false;   // the arrival rule's members belong to part Corpses
        return Arrived(world, slot, furnace);
    }

    /// <summary>The arrival rule itself, in its own method so a member a newer game moved fails here and not in the caller.</summary>
    static bool Arrived(World world, int slot, CorpseFurnace furnace)
    {
        ref var n = ref world.Characters.Next[slot];
        float gap = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), new Vector2(furnace.Position.X, furnace.Position.Z));
        if (!Meitou.Simulation.Construction.SiteReach.Within(n, gap, furnace.UseRange)) return false;
        return MathF.Abs(n.Position.Y - furnace.Position.Y) < Meitou.Simulation.Construction.SiteReach.HeightLimit(false, false);
    }

    /// <summary>Port only (SanitationCrew's Removal checks, CAP 14): the body lies in a bed or a cage (<c>InSomething</c> set). False for a stale id.</summary>
    internal static bool InBedOrCage(World world, CharacterId body) =>
        world.Characters.TryResolveNext(body, out int s) && world.Characters.Cold(s) is { } c && c.InSomething != Meitou.Simulation.Locks.HeldState.None;

    /// <summary>The body's carrier (<c>CharacterCold.CarriedBy</c>), None when nobody or a stale character carries it.</summary>
    internal static CharacterId CarrierOf(World world, CharacterId body)
    {
        var t = world.Characters;
        if (!t.TryResolveNext(body, out int s) || t.Cold(s) is not { } c || c.CarriedBy.IsNone) return CharacterId.None;
        return t.TryResolveNext(c.CarriedBy, out _) ? c.CarriedBy : CharacterId.None;
    }

    /// <summary>The feed (spike S9's fallback, Incinerate's steps, ProductionSystem.Errands.cs:375-383): the handler lets go of the body
    /// (<c>CarrySystem.LetGo</c>, CarrySystem.cs:608), the furnace's label floats up 5 units over it (<c>world.Floaters?.Enqueue(Floater.Label)</c>,
    /// Floaters.cs:103, 121; the queue is null without a host), <c>world.Uniques.Left(body)</c> (UniqueCharacters.cs:159) and
    /// <c>world.Characters.Remove(body)</c> (its inventory goes with it, CharacterTable.cs:1507-1514). SanitationCrew's checks first: not in a bed or a
    /// cage, carried by <paramref name="handler"/> only. SlowWorld only.</summary>
    internal static CorpseDisposalResult Dispose(World world, CharacterId handler, CharacterId body, CorpseFurnace furnace)
    {
        var t = world.Characters;
        if (!t.TryResolveNext(body, out int s) || t.Cold(s) is not { } c) return CorpseDisposalResult.Gone;
        if (c.InSomething != Meitou.Simulation.Locks.HeldState.None) return CorpseDisposalResult.NotOurs;
        if (c.CarriedBy.IsNone) return CorpseDisposalResult.NotCarried;
        if (c.CarriedBy != handler) return CorpseDisposalResult.NotOurs;
        if (!Available || !t.TryResolveNext(handler, out int carrier)) return CorpseDisposalResult.Failed;
        return Feed(world, carrier, new CharacterId(s, t.Next[s].Generation), furnace);
    }

    /// <summary>Incinerate's steps, in their own method so a member a newer game moved fails here (MissingMethodException) and not in the caller.</summary>
    static CorpseDisposalResult Feed(World world, int carrier, CharacterId body, CorpseFurnace furnace)
    {
        if (PgEngine.FindCarry(world) is not { } carry || !carry.LetGo(world, carrier)) return CorpseDisposalResult.Failed;
        if (!world.Characters.IsAlive(body)) return CorpseDisposalResult.Gone;
        if (furnace.Label.Length > 0) world.Floaters?.Enqueue(Floater.Label(furnace.Position + new Vector3(0, 5, 0), furnace.Label));
        world.Uniques.Left(body);
        return world.Characters.Remove(body) ? CorpseDisposalResult.Removed : CorpseDisposalResult.Gone;
    }
}

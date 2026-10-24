using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Meitou.Data;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;

namespace Meitou.Simulation.Construction;

/// <summary>A material entry of a site: the ITEM record, the amount needed and the amount handed over so far (whole units).</summary>
public sealed class MaterialSlot(string item, float required)
{
    public string Item { get; } = item;
    public float Required { get; set; } = required;
    public float Delivered { get; set; }
}

/// <summary>
/// A player-built building, owned town-house repair or construction site (docs/simulation.md "Buildings"; the state is the original's Building+0x160 block, docs/game/buildings-production.md
/// "Construction state"). Serial phases only. <see cref="Progress"/> and <see cref="Target"/> are in material units.
/// </summary>
public sealed class BuildSite
{
    public required string Id { get; init; }
    /// <summary>Existing town placement, replaced in place during repairs; never a new save instance.</summary>
    public BuildingPlacement? LevelPlacement { get; init; }
    public string? LevelTownName { get; init; }
    /// <summary>The BUILDING record; an upgrade or downgrade swaps it in place (<see cref="ConstructionSystem"/> "Upgrades").</summary>
    public required string Record { get; set; }
    /// <summary>The BUILDING's name (messages only; not hashed).</summary>
    public string Name { get; set; } = "";
    public Vector3 Position { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public int Floor { get; init; }
    /// <summary>The instance id of the building this one stands in (null outdoors).</summary>
    public string? Parent { get; set; }
    /// <summary>
    /// The id of the wall this short piece, connector or wedge is attached to (the original's WallBuilding+0x368): work, deliveries and orders go to that wall and its progress sets this
    /// one's (<see cref="ConstructionSystem.AttachWalls"/>). Rebuilt when placed or loaded; not saved, not hashed.
    /// </summary>
    public string? WallParent { get; set; }
    /// <summary>The <see cref="PlayerTown.Number"/> of the player town it belongs to (the save's <c>town</c> handle); 0 for none.</summary>
    public int Town { get; set; }
    /// <summary>The owning faction, a position in the world's faction list.</summary>
    public int Owner { get; init; }
    public required MaterialSlot[] Materials { get; set; }
    public float Progress { get; set; }
    public float Target { get; set; }
    /// <summary>Progress per game second of one unit of build work: <c>build speed mult</c> / (1200/11).</summary>
    public float Rate { get; set; }
    public bool Complete { get; set; }
    public bool Paused { get; set; }
    /// <summary>Open to the public (the original's Building+0xc8, saved as <c>is public day</c>; the stats panel's toggle).</summary>
    public bool Public { get; set; } = true;
    /// <summary>Being built up to a new record by an upgrade or downgrade: its doors stay meanwhile (the original keeps the door objects). Engine state, not saved; hashed while set.</summary>
    public bool Upgrading { get; set; }
    /// <summary>The save's <c>destroyed</c> flag; the first tick on a player-built site, or explicit Repair on a town house, turns it back into a half-built site (<see cref="ConstructionSystem"/>).</summary>
    public bool Destroyed { get; set; }
    /// <summary>The destroyed flag has been acted on (the building is a ruin being rebuilt).</summary>
    public bool Ruined { get; set; }
    /// <summary>Builders that have applied work to this site this tick (the 0.75 falloff counter, reset each tick); not saved.</summary>
    public int BuilderCounter { get; set; }
    /// <summary>A builder has taken it apart this tick (derived; the snapshot's flag).</summary>
    public bool Dismantling { get; set; }
    /// <summary>The zone the site stands in (its id and save file).</summary>
    public ZoneCoordinate Zone => WorldLayout.ZoneOf(Position.X, Position.Z);

    /// <summary>The materials are all delivered and built: <c>sum(min(delivered, required))</c>, what progress may reach (<b>Verified</b>, <c>FUN_14054a3f0</c>).</summary>
    public float Deliverable
    {
        get
        {
            float sum = 0;
            foreach (var m in Materials) sum += MathF.Min(m.Delivered, m.Required);
            return sum;
        }
    }

    /// <summary>A site that is not complete and has built all it was given: the builder waits for more materials (<c>sum(min(delivered, required)) &lt;= progress</c>).</summary>
    public bool Stalled => !Complete && Deliverable <= Progress;
    /// <summary>Unfinished, or complete with progress below the target (damaged: Building slot 101 <c>FUN_1400f6b70</c>, <b>Verified</b>); building work (orders 2 and 95) applies.</summary>
    public bool NeedsWork => Destroyed || !Complete || Progress < Target;

    internal void Hash(ref StateHasher h)
    {
        h.Add(Rng.StableHash(Id));
        if (LevelPlacement is not null) h.Add(0x4c657665);
        h.Add(Rng.StableHash(Record));
        h.Add(Position);
        h.Add(Rotation.X); h.Add(Rotation.Y); h.Add(Rotation.Z); h.Add(Rotation.W);
        h.Add(Floor);
        h.Add(Parent is null ? 0UL : Rng.StableHash(Parent));
        h.Add(Owner);
        h.Add(Progress);
        h.Add(Target);
        h.Add(Rate);
        h.Add(Complete);
        h.Add(Paused);
        if (Destroyed || Ruined) { h.Add(0x44657374); h.Add(Destroyed); h.Add(Ruined); }
        if (Town != 0) { h.Add(0x546F776E); h.Add(Town); }
        if (!Public) h.Add(0x50726976);
        if (Upgrading) h.Add(0x55706772);
        h.Add(Materials.Length);
        foreach (var m in Materials)
        {
            h.Add(Rng.StableHash(m.Item));
            h.Add(m.Required);
            h.Add(m.Delivered);
        }
    }
}

/// <summary>
/// A town the player's buildings made: the original's TOWN <c>player_town_template</c> ("Your Outpost"), owned by the player faction (docs/game/buildings-production.md "Placement",
/// docs/simulation.md "Buildings"). Serial phases only.
/// </summary>
public sealed class PlayerTown
{
    /// <summary>The TOWN record every outpost is made from (<b>Verified</b>, <c>FUN_140934e10</c>).</summary>
    public const string Template = "player_town_template";
    /// <summary>The name a new outpost gets (<b>Verified</b>).</summary>
    public const string DefaultName = "Your Outpost";

    /// <summary>1, 2, ... in creation (or load) order: what <see cref="BuildSite.Town"/> holds.</summary>
    public required int Number { get; init; }
    public string Name { get; set; } = DefaultName;
    public Vector3 Position { get; init; }
    /// <summary>The owning faction, a position in the world's faction list.</summary>
    public int Owner { get; init; }
    /// <summary>The save's instance id of its town-list entry (null until a save has named it); bookkeeping for <c>Saving.BuildingSave</c>, not hashed.</summary>
    public string? SaveInstance { get; set; }
    /// <summary>The save's town handle <c>hand*</c> (null until a save has named it); not hashed.</summary>
    public Meitou.Data.Save.Hand? SaveHandle { get; set; }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Number);
        h.Add(Rng.StableHash(Name));
        h.Add(Position);
        h.Add(Owner);
    }
}

/// <summary>
/// The player-built sites of the world in creation order (docs/simulation.md "Buildings"). Ids are the original's <c>&lt;n&gt;-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c>, n from a counter per zone that
/// starts above every such id in the loaded save (<see cref="Reserve"/>). Every change that the host or a save must see bumps <see cref="Version"/>; adding, completing, destroying and
/// removing a site also bump <see cref="Revision"/>. Hashed by <see cref="World.StateHash"/> only while it holds a site.
/// </summary>
public sealed partial class BuildingRegistry
{
    readonly List<BuildSite> sites = [];
    readonly List<PlayerTown> towns = [];
    readonly Dictionary<string, BuildSite> byId = new(StringComparer.Ordinal);
    readonly Dictionary<ZoneCoordinate, int> counters = [];
    IReadOnlyList<BuildingSnapshot> published = [];
    long publishedVersion = -1;

    public int Count => sites.Count;
    public IReadOnlyList<BuildSite> All => sites;
    public BuildSite? Find(string id) => byId.GetValueOrDefault(id);

    /// <summary>The player towns (outposts), in creation order; never removed.</summary>
    public IReadOnlyList<PlayerTown> Towns => towns;
    public PlayerTown? FindTown(int number) => number > 0 && number <= towns.Count ? towns[number - 1] : null;

    /// <summary>Makes a player town with the next number.</summary>
    public PlayerTown AddTown(Vector3 position, int owner, string name = PlayerTown.DefaultName)
    {
        var town = new PlayerTown { Number = towns.Count + 1, Name = name, Position = position, Owner = owner };
        towns.Add(town);
        Revision++;
        Version++;
        return town;
    }

    /// <summary>Bumped when a site is added, completed (or turned back into a site), destroyed or removed: the host diffs the snapshot's buildings by id when it changes.</summary>
    public long Revision { get; private set; }
    /// <summary>Bumped by every change to a site (progress included); the published snapshots are rebuilt only then.</summary>
    public long Version { get; private set; }

    /// <summary>Sites that are not complete (the auto-job looks only while there are some).</summary>
    public bool AnyIncomplete
    {
        get
        {
            foreach (var s in sites) if (!s.Complete) return true;
            return false;
        }
    }

    /// <summary>Some site is unfinished or a complete building is damaged (progress below its target, docs/game/buildings-production.md "Repair").</summary>
    public bool AnyNeedsWork
    {
        get
        {
            foreach (var s in sites) if (s.NeedsWork) return true;
            return false;
        }
    }

    [GeneratedRegex(@"^(\d+)-zone(\d+)\.(\d+)-INGAME$")]
    private static partial Regex IdPattern();

    /// <summary>The zone and number of an id of the pattern <c>&lt;n&gt;-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c>; false for any other id.</summary>
    public static bool TryParseId(string id, out ZoneCoordinate zone, out int n)
    {
        zone = default;
        n = 0;
        var m = IdPattern().Match(id);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return false;
        if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int x) || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int y)) return false;
        zone = new ZoneCoordinate(x, y);
        return true;
    }

    /// <summary>Keeps new ids of the id's zone above its number (an id of another pattern is ignored).</summary>
    public void Reserve(string id)
    {
        if (TryParseId(id, out var zone, out int n)) counters[zone] = Math.Max(counters.GetValueOrDefault(zone), n);
    }

    /// <summary>The id the next site in <paramref name="zone"/> gets (skipping any id in use).</summary>
    public string NewId(ZoneCoordinate zone)
    {
        while (true)
        {
            int n = counters.GetValueOrDefault(zone) + 1;
            counters[zone] = n;
            string id = IdOf(n, zone);
            if (!byId.ContainsKey(id)) return id;
        }
    }

    public static string IdOf(int n, ZoneCoordinate zone) =>
        string.Create(CultureInfo.InvariantCulture, $"{n}-zone{zone.X}.{zone.Y}-INGAME");

    public void Add(BuildSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (!byId.TryAdd(site.Id, site)) throw new InvalidOperationException($"site {site.Id} exists");
        sites.Add(site);
        Reserve(site.Id);
        Revision++;
        Version++;
    }

    public bool Remove(BuildSite site)
    {
        if (!byId.Remove(site.Id)) return false;
        sites.Remove(site);
        Revision++;
        Version++;
        return true;
    }

    /// <summary>Notes a change to a site's fields (progress, delivery, pause); the next snapshot is rebuilt.</summary>
    public void Touch() => Version++;

    /// <summary>Notes a change the host must act on (completed, turned back into a site, destroyed).</summary>
    public void Changed()
    {
        Revision++;
        Version++;
    }

    /// <summary>The snapshots of every site, rebuilt only when something changed since the last call.</summary>
    internal IReadOnlyList<BuildingSnapshot> Published()
    {
        if (publishedVersion == Version) return published;
        var list = new List<BuildingSnapshot>(sites.Count);
        foreach (var s in sites)
        {
            var materials = new MaterialView[s.Materials.Length];
            for (int i = 0; i < materials.Length; i++) materials[i] = new MaterialView(s.Materials[i].Item, s.Materials[i].Required, s.Materials[i].Delivered);
            list.Add(new BuildingSnapshot(s.Id, s.Record, s.Position, s.Rotation, s.Floor, s.Parent, s.Owner, s.Progress, s.Target, materials, s.Complete, s.Paused) { Dismantling = s.Dismantling, WallParent = s.WallParent, Public = s.Public, TownName = s.LevelTownName ?? FindTown(s.Town)?.Name, LevelPlacement = s.LevelPlacement, Destroyed = s.Destroyed, Name = s.Name });
        }
        publishedVersion = Version;
        return published = list;
    }

    internal void Hash(ref StateHasher h)
    {
        h.Add(0x426C6473);
        var zones = counters.Keys.ToList();
        zones.Sort(static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        h.Add(zones.Count);
        foreach (var z in zones)
        {
            h.Add(z.X);
            h.Add(z.Y);
            h.Add(counters[z]);
        }
        h.Add(sites.Count);
        foreach (var s in sites) s.Hash(ref h);
        if (towns.Count > 0)
        {
            h.Add(towns.Count);
            foreach (var t in towns) t.Hash(ref h);
        }
    }
}


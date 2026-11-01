using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>A town a campaign aims at or comes from: a placed town (<see cref="Site"/>, a position in the population's sites) or a player town (<see cref="PlayerTown"/>, its number).</summary>
public readonly record struct CampaignTown(int Site, int PlayerTown)
{
    public static readonly CampaignTown None = new(-1, 0);
    public bool IsNone => Site < 0 && PlayerTown <= 0;
    public static CampaignTown OfSite(int site) => new(site, 0);
    public static CampaignTown OfPlayer(int number) => new(-1, number);
}

/// <summary>A running campaign (the original's <c>CampaignInstance</c>, docs/game/factions-squads-towns.md "Campaigns"). Changed in serial phases only.</summary>
public sealed class CampaignInstance
{
    /// <summary>The faction whose war manager runs it (a position in the factions).</summary>
    public required int Faction { get; init; }
    /// <summary><c>id c</c>: unique within its faction.</summary>
    public required int Id { get; init; }
    /// <summary><c>dat c</c>: the FACTION_CAMPAIGN.</summary>
    public required string CampaignId { get; init; }
    public CampaignTown Target { get; set; }
    /// <summary><c>enemy c</c>: the target's faction (-1: none).</summary>
    public int Enemy { get; set; } = -1;
    public CampaignTown Home { get; set; }
    /// <summary>0 travel, 1 at the target, 2 ended.</summary>
    public int Phase { get; set; }
    /// <summary>The game hour the phase began (not saved: the phase starts again at load, as the original).</summary>
    public double PhaseStart { get; set; }
    /// <summary><c>rd c</c>: the leader has been in the target town.</summary>
    public bool Reached { get; set; }
    /// <summary><c>trig c</c>: a follow-up of this manager is queued.</summary>
    public bool Trig { get; set; }
    /// <summary>The leader platoon (-1: none).</summary>
    public int Leader { get; set; } = -1;
    /// <summary>Its platoons, in the order they joined.</summary>
    public List<int> Platoons { get; } = [];
    /// <summary>Its platoons gone from the world (killed off screen or despawned): not intact for the 20 % rule (not saved: a load skips them, as the original's unresolved handles).</summary>
    public int Lost { get; set; }
    /// <summary>The characters it needs (int(<c>num forces</c> × <c>raidSizeMult</c>)) and those it gathered.</summary>
    public int Required { get; set; }
    public int Men { get; set; }
    /// <summary>The game hour its first loaded fight began (-1: none yet; restarts at load).</summary>
    public double FightStart { get; set; } = -1;
    /// <summary>Its leader platoon fought the player's characters (engine test, <c>PopulationCampaigns.cs</c>).</summary>
    public bool FoughtPlayer { get; set; }
    /// <summary>The leader's recorded trail, oldest first (the original's <c>GatherAndMove</c>): its followers' places. Not saved: laid again after a load.</summary>
    public List<Vector2> Trail { get; } = [];
    /// <summary>Its leader said the <c>dialog announcement</c> (the character, and the game hour; -1: not yet) and has reached the target. Not saved.</summary>
    public CharacterId Announcer { get; set; } = CharacterId.None;
    public double AnnouncedAt { get; set; } = -1;
    public double ArrivedAt { get; set; } = -1;
    /// <summary>The redirect timer (the original's +0xf0, started the first time the target is found gone or inactive; -1: not started). Not saved or hashed.</summary>
    public double RedirectStart { get; set; } = -1;
    /// <summary>
    /// Per platoon of a cannibal assault (Package_WarAssault_Cannibal, each platoon's own package instance): the game hour one of its members was first seen carrying someone; absent: nobody
    /// carries. Saved as <c>meitou carry start p&lt;i&gt;</c> (the original keeps the stamp in the package and saves none).
    /// </summary>
    public Dictionary<int, double> CarryStarts { get; } = [];
}

/// <summary>A queued campaign (<c>req*</c> of the save): due at a game hour against a target.</summary>
public sealed class CampaignRequest
{
    public required string CampaignId { get; init; }
    public int Enemy { get; set; } = -1;
    public double Due { get; set; }
    public CampaignTown Target { get; set; }
    public CampaignTown Home { get; set; }
    /// <summary>The one counter (+0x58, <b>Verified</b> <c>FUN_1409cb310</c>; not saved) of the delays for a gone target and the starts tried: the request goes above 10 on a delay, above 100 on a start.</summary>
    public int Tries { get; set; }
}

/// <summary>A campaign a faction knows (<c>poss*</c>): its last start, the <c>repeat limit</c> clock (0: never started).</summary>
public sealed class KnownCampaign(string id)
{
    public string Id { get; } = id;
    public double LastStart { get; set; }
}

/// <summary>A faction's war manager (the original's <c>FactionWarMgr</c>): its next roll, next campaign id, running and queued campaigns and the campaigns it knows.</summary>
public sealed class WarManager
{
    public required int Faction { get; init; }
    /// <summary><c>updatetime</c>: the game hour of the next roll.</summary>
    public double UpdateTime { get; set; }
    /// <summary><c>fwc id</c>.</summary>
    public int NextId { get; set; }
    public List<CampaignInstance> Actives { get; } = [];
    public List<CampaignRequest> Requests { get; } = [];
    public List<KnownCampaign> Known { get; } = [];
    /// <summary>Requests queued since the world began (keys their delay rolls; not saved).</summary>
    internal int Queued { get; set; }

    public KnownCampaign Know(string id)
    {
        foreach (var k in Known) if (k.Id == id) return k;
        var added = new KnownCampaign(id);
        Known.Add(added);
        return added;
    }
}

/// <summary>
/// Faction campaigns (docs/game/factions-squads-towns.md "Campaigns", docs/simulation.md "Off-screen tier"): one war manager per faction, run serially in faction order at every
/// population look (every rule reads the game clock, so the cadence does not matter): the running campaigns, then the due requests, then the roll when the clock passes
/// <c>updatetime</c>. Campaign squads are platoons kept while their campaign runs; off screen they are moved here (the leader to the target, the others to the leader), loaded
/// their leader is sent to the same goal. Rolls are keyed by the world seed, the faction and the roll's hour, so a run repeats at any thread count.
/// </summary>
public sealed partial class PopulationSystem : IStateHashed
{
    WarManager[]? wars;
    CampaignDatabase? campaignDb;

    /// <summary>Hours between rolls (<b>Verified</b>: +6 in the loader and the update).</summary>
    public const double RollEveryHours = 6;
    /// <summary>Spawn radius of a campaign squad round its town or leader (<b>Verified</b>, 700).</summary>
    public const float CampaignSpawnRadius = 700;
    /// <summary>Distance at which an unloaded follower stops behind its leader (engine choice).</summary>
    public const float CampaignFollowDistance = 100;
    /// <summary>Inside this distance of its trail point a follower goes at <see cref="CampaignPaceFactor"/> x the leader's speed (<b>Verified</b>, 70 and 0.9, <c>FUN_140349e70</c>).</summary>
    public const float CampaignPaceDistance = 70, CampaignPaceFactor = 0.9f;

    /// <summary>The war managers, one per faction in faction order (empty before the first look).</summary>
    public IReadOnlyList<WarManager> Wars => wars ?? [];

    /// <summary>A FACTION_CAMPAIGN with its ancestors applied by the live inheritance switch (null: not one).</summary>
    public CampaignData? CampaignOf(string id) => (campaignDb ??= new CampaignDatabase(data.Db)).Find(id, !CampaignAncestorsOnce);

    int PlayerFactionOf(World world) => world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;

    /// <summary>The managers, made once: each faction's <c>campaigns</c> list known, the first roll 6 h on; then a loaded save's war states over them.</summary>
    internal void EnsureWars(World world)
    {
        if (wars is not null) return;
        wars = new WarManager[data.Factions.Count];
        for (int f = 0; f < wars.Length; f++)
        {
            var m = wars[f] = new WarManager { Faction = f, UpdateTime = world.GameHours + RollEveryHours };
            foreach (var link in data.Factions[f].Campaigns)
                if (CampaignOf(link.Id) is not null) m.Know(link.Id);
        }
        if (world.Saved is { Wars.Count: > 0 } saved) Saving.WarSave.Read(world, this, saved);
    }

    internal WarManager? WarOf(int faction) => wars is not null && faction >= 0 && faction < wars.Length ? wars[faction] : null;

    // ---- prosperity ----

    /// <summary>Per second of game time, a faction's prosperity grows while below its cap (<b>Verified</b>, <c>FUN_1402dd680</c>: 0.1).</summary>
    const double ProsperityPerSecond = 0.1;
    /// <summary>What a unique's death and a damaged platoon's destroy take off its faction's prosperity (<b>Verified</b>, <c>FUN_1407a6200</c>, <c>FUN_1406bab60</c>: 100 and 10).</summary>
    const double UniqueDeathProsperity = 100, DamagedDespawnProsperity = 10;
    /// <summary>Each faction's prosperity (the FactionUniqueSquadManager's +8, saved as the faction's <c>prosperity</c>), by faction; null before the first need.</summary>
    double[]? prosperity;
    long prosperityTick;

    /// <summary>
    /// The factions' prosperity, made on first use: the loaded save's value for a faction it names, else the faction's <c>max prosperity</c> (<b>Verified</b>, <c>FUN_1402ddc90</c> /
    /// <c>FUN_1402e0ab0</c> start the value at the cap).
    /// </summary>
    double[] EnsureProsperity(World world)
    {
        if (prosperity is not null) return prosperity;
        prosperityTick = world.Tick;
        var values = new double[data.Factions.Count];
        for (int f = 0; f < values.Length; f++)
            values[f] = world.Saved?.Prosperity.TryGetValue(data.Factions[f].Id, out float saved) == true ? saved : data.Factions[f].MaxProsperity;
        return prosperity = values;
    }

    /// <summary>A faction's prosperity (0 for no faction).</summary>
    internal double ProsperityOf(World world, int faction) => faction >= 0 && faction < data.Factions.Count ? EnsureProsperity(world)[faction] : 0;

    /// <summary>
    /// Lowers a faction's prosperity, floored at 0 (<c>FUN_1407a6200</c>, <c>FUN_1406bab60</c>); the one place the drops are made.
    /// </summary>
    void LowerProsperity(World world, int faction, double by)
    {
        if (faction < 0 || faction >= data.Factions.Count) return;
        var values = EnsureProsperity(world);
        values[faction] = Math.Max(0, values[faction] - by);
    }

    /// <summary>
    /// At each look: the uniques that died since the last (<see cref="UniqueCharacters.Died"/>) cost their faction 100, then every faction below its cap gains 0.1 per second of game
    /// time since the last look (<c>FUN_1402dd680</c>; no clamp to the cap in the add). The original's period is the faction's accumulated frame time, which nothing else reads (<b>Unknown</b>).
    /// </summary>
    void StepProsperity(World world)
    {
        var values = EnsureProsperity(world);
        foreach (int faction in world.Uniques.TakeFallen()) LowerProsperity(world, faction, UniqueDeathProsperity);
        double seconds = (world.Tick - prosperityTick) * (double)world.TickSeconds;
        prosperityTick = world.Tick;
        for (int f = 0; f < values.Length; f++)
            if (values[f] < data.Factions[f].MaxProsperity) values[f] += seconds * ProsperityPerSecond;
    }

    void StepCampaigns(World world)
    {
        StepProsperity(world);
        EnsureWars(world);
        double now = world.GameHours;
        foreach (var m in wars!)
        {
            for (int i = 0; i < m.Actives.Count; i++)
                if (UpdateCampaign(world, m, m.Actives[i], now)) m.Actives.RemoveAt(i--);
            UpdateRequests(world, m, now);
            if (now > m.UpdateTime || m.UpdateTime == 0)
            {
                double at = m.UpdateTime;
                m.UpdateTime = now + RollEveryHours;
                if (m.Actives.Count == 0 && m.Requests.Count == 0) Roll(world, m, at, now);
            }
        }
        MoveCampaigns(world, settings.CheckEveryTicks * world.TickSeconds);
    }

    // ---- towns ----

    Vector2 CentreOf(World world, CampaignTown t)
    {
        if (t.Site >= 0) return SiteCentre(t.Site);
        var town = world.Buildings.FindTown(t.PlayerTown);
        return town is null ? default : new Vector2(town.Position.X, town.Position.Z);
    }

    /// <summary>A town's radius (<c>Town::getRadius</c>, <b>Verified</b> <c>FUN_140926eb0</c>): a placed town's <see cref="TownData.Radius"/> (<c>town radius mult</c> × its type base); a player town's own (<see cref="Construction.PlayerTown.Radius"/>, 2500 when it is gone).</summary>
    float RadiusOf(World world, CampaignTown t)
    {
        if (t.Site >= 0) return data.Sites[t.Site].Town.Radius;
        return world.Buildings.FindTown(t.PlayerTown)?.Radius ?? 2500;
    }

    int FactionOfTown(World world, CampaignTown t) =>
        t.Site >= 0 ? data.SiteFactions[t.Site] : world.Buildings.FindTown(t.PlayerTown)?.Owner ?? -1;

    string NameOf(World world, CampaignTown t) =>
        t.Site >= 0 ? data.Sites[t.Site].Town.Name : world.Buildings.FindTown(t.PlayerTown)?.Name ?? (PlayerFactionOf(world) is >= 0 and var pf ? data.Factions[pf].Name : "");

    bool Exists(World world, CampaignTown t) => t.Site >= 0 ? t.Site < data.Sites.Count : world.Buildings.FindTown(t.PlayerTown) is not null;

    /// <summary>The town a position is in (within its radius, XZ): placed towns in site order, then the player's; <see cref="CampaignTown.None"/> outside every town.</summary>
    CampaignTown TownAt(World world, Vector2 at)
    {
        foreach (var site in data.Sites)
        {
            if (site.Town.Type is TownType.NestMarker or TownType.Null) continue;
            float r = site.Town.Radius;
            if (r > 0 &&Vector2.DistanceSquared(at, SiteCentre(site.Index)) <= r * r) return CampaignTown.OfSite(site.Index);
        }
        foreach (var town in world.Buildings.Towns)
        {
            float r = RadiusOf(world, CampaignTown.OfPlayer(town.Number));
            if (Vector2.DistanceSquared(at, new Vector2(town.Position.X, town.Position.Z)) <= r * r) return CampaignTown.OfPlayer(town.Number);
        }
        return CampaignTown.None;
    }

    /// <summary>The faction's own town nearest a point (XZ; every placed town of the faction), none when it has no town.</summary>
    CampaignTown NearestOwnTown(int faction, Vector2 at)
    {
        int best = -1;
        float bestD = float.MaxValue;
        foreach (var site in data.Sites)
        {
            if (data.SiteFactions[site.Index] != faction) continue;
            float d = Vector2.DistanceSquared(at, SiteCentre(site.Index));
            if (d < bestD) (best, bestD) = (site.Index, d);
        }
        return best < 0 ? CampaignTown.None : CampaignTown.OfSite(best);
    }

    /// <summary>The BIOME_GROUP id at a point (null without a biome map).</summary>
    string? BiomeAt(Vector2 at) => data.BiomeGroupAt(at);

    // ---- the roll ----

    /// <summary>
    /// The roll (<c>FUN_1409cb310</c>): each <c>territorial triggers</c> campaign the faction knows with a target passes with its chance; one passer is drawn by chance and
    /// started. <paramref name="at"/> is the roll's hour (the stored <c>updatetime</c>), which keys the rolls.
    /// </summary>
    void Roll(World world, WarManager m, double at, double now)
    {
        int player = PlayerFactionOf(world);
        if (player < 0) return;
        int living = -1;
        ulong time = BitConverter.DoubleToUInt64Bits(at);
        var passers = new List<(CampaignData C, CampaignTown Target, CampaignTown Home, float Chance)>();
        for (int i = 0; i < m.Known.Count; i++)
        {
            var known = m.Known[i];
            if (CampaignOf(known.Id) is not { TerritorialTriggers: true } c) continue;
            if (!FindTarget(world, m.Faction, c, player, out var target, out var home)) continue;
            if (living < 0) living = PlayerLiving(world);
            float chance = Chance(world, m, c, known, target, now, living);
            if (chance <= 0) continue;
            ulong roll = Rng.Hash(world.Seed, (ulong)m.Faction << 32 | (uint)i, RngPurpose.Campaign, time ^ SeedSalts.CampaignPass);
            if (chance > Rng.Float(roll)) passers.Add((c, target, home, chance));
        }
        if (passers.Count == 0) return;
        float total = passers.Sum(p => p.Chance);
        float pick = Rng.Float(Rng.Hash(world.Seed, (ulong)m.Faction << 32, RngPurpose.Campaign, time ^ SeedSalts.CampaignDraw)) * total;
        var chosen = passers[^1];
        foreach (var p in passers)
            if ((pick -= p.Chance) < 0) { chosen = p; break; }
        Start(world, m, chosen.C, chosen.Target, chosen.Home, now);
    }

    /// <summary>The player's living characters (the population gate counts them).</summary>
    static int PlayerLiving(World world)
    {
        int n = 0;
        var prev = world.Characters.Previous;
        for (int i = 0; i < prev.Length; i++)
            if (prev[i].Alive && world.Characters.Cold(i) is { IsPlayer: true, DiedTick: < 0 }) n++;
        return n;
    }

    /// <summary>
    /// A roll's chance (<c>CampaignData::vfunc_1</c>), 0 on any failure: against a player town, the player's living characters ≥ <c>target population min</c> and the town's
    /// level (<see cref="TownLevel"/>, Town +0x388) ≥ <c>target tech level &gt;=</c>; the <c>repeat limit</c> passed; world states and leader; then the distance falloff from the
    /// faction's nearest town.
    /// </summary>
    float Chance(World world, WarManager m, CampaignData c, KnownCampaign known, CampaignTown target, double now, int living)
    {
        if (living < c.TargetPopulationMin) return 0;
        if (target.PlayerTown > 0 && TownLevel(world, target.PlayerTown) < c.TargetTechLevel) return 0;
        if (known.LastStart > 0 && now <= known.LastStart + c.RepeatLimitHours) return 0;
        if (!WorldStatesPass(world, c)) return 0;
        var at = CentreOf(world, target);
        var nearest = NearestOwnTown(m.Faction, at);
        if (nearest.IsNone) return 0;
        return c.RollChance(Vector2.Distance(at, CentreOf(world, nearest)), settings.RaidFrequencyMult);
    }

    /// <summary>The world-state gate (<c>vfunc_2</c>): every <c>world state</c> has its value, and there is no <c>special leader</c> or one of them is alive (an untracked one counts as alive).</summary>
    bool WorldStatesPass(World world, CampaignData c)
    {
        foreach (var (state, value) in c.WorldStates)
            if (!IWorldStates.Passes(States, data.Db, state, value ? 1 : 0)) return false;
        if (c.SpecialLeaders.Count == 0) return true;
        foreach (var leader in c.SpecialLeaders)
            if (world.Uniques.StateOf(leader) == UniqueState.Alive) return true;
        return false;
    }

    /// <summary>
    /// A target and home (<c>CampaignData::vfunc_0</c>): the towns of <paramref name="enemy"/> (the player's for rolls) in the faction's <c>biomes</c>, else all of them, less those
    /// not active (<see cref="TownActive"/>, Town slot 70), those with hostile occupiers when the campaign is not hostile (Town +0x128, <see cref="Ai.AiSystem.Occupied"/>), and
    /// those in its <c>no-go zones</c> unless the campaign ignores them; then the first source town in site order with one of its 5 nearest candidates within <c>range far</c> is
    /// the home, that candidate the target.
    /// </summary>
    bool FindTarget(World world, int faction, CampaignData c, int enemy, out CampaignTown target, out CampaignTown home)
    {
        target = home = CampaignTown.None;
        var fd = data.Factions[faction];
        var all = new List<CampaignTown>();
        if (enemy == PlayerFactionOf(world))
        {
            foreach (var town in world.Buildings.Towns)
                if (town.Owner == enemy) all.Add(CampaignTown.OfPlayer(town.Number));
        }
        else
            foreach (var site in data.Sites)
                if (data.SiteFactions[site.Index] == enemy && site.Town.Type is not (TownType.NestMarker or TownType.Null)) all.Add(CampaignTown.OfSite(site.Index));
        if (all.Count == 0) return false;
        var candidates = all.Where(t => BiomeAt(CentreOf(world, t)) is { } b && fd.Biomes.Any(x => x.Id == b)).ToList();
        if (candidates.Count == 0) candidates = all;
        candidates.RemoveAll(t => !TownActive(world, t) || !c.IsHostile && Ai.AiSystem.Occupied(world, t));
        if (!c.IgnoresNogoZones && fd.NoGoZones.Count > 0)
            candidates.RemoveAll(t => BiomeAt(CentreOf(world, t)) is { } b && fd.NoGoZones.Any(x => x.Id == b));
        if (candidates.Count == 0) return false;
        bool strict = fd.BooksBounties;
        foreach (var site in data.Sites)
        {
            if (data.SiteFactions[site.Index] != faction || site.Town.Type is TownType.NestMarker or TownType.Null) continue;
            if (strict && site.Town.Type is TownType.Village or TownType.PointOfInterest or TownType.Prison or TownType.SlaveCamp) continue;
            var from = SiteCentre(site.Index);
            foreach (var t in candidates.OrderBy(t => Vector2.DistanceSquared(from, CentreOf(world, t))).Take(5))
            {
                if (c.RangeFar > 0 && Vector2.Distance(from, CentreOf(world, t)) > c.RangeFar) continue;
                target = t;
                home = CampaignTown.OfSite(site.Index);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a town is active (Town vtable slot 70, <c>FUN_140929eb0</c>, <b>Verified</b>): a zone of it is loaded, and for a town of the player's faction a member of the faction's
    /// platoons has it as its current town (<c>Character</c> slot 15, the AI's rule <see cref="Ai.AiSystem.TownAt"/>: the nearest town whose radius holds it, so a nearer town
    /// takes precedence). So raids and redirects aim only at a player town the player is at. The loaded-zone test of a player town is not made: the member standing in it keeps
    /// its own zone loaded (the town's zone list is not modelled).
    /// </summary>
    bool TownActive(World world, CampaignTown t)
    {
        if (t.Site >= 0)
        {
            if (!active.Contains(data.Sites[t.Site].Zone)) return false;
            if (PlayerFactionOf(world) is not (>= 0 and var player) || data.SiteFactions[t.Site] != player) return true;
        }
        if (presentTick != world.Tick)
        {
            // The towns the player's characters are in, worked out once a tick (serial).
            presentTick = world.Tick;
            presentTowns.Clear();
            var prev = world.Characters.Previous;
            for (int i = 0; i < prev.Length; i++)
                if (prev[i].Alive && world.Characters.Cold(i) is { IsPlayer: true } && Ai.AiSystem.TownAt(data, world, prev[i].Position) is { IsNone: false } here) presentTowns.Add(here);
        }
        return presentTowns.Contains(t);
    }

    long presentTick = -1;
    readonly HashSet<CampaignTown> presentTowns = [];

    /// <summary>
    /// A player town's level (Town +0x388, the save's <c>plevel</c>; <c>FUN_14048c200</c>, Verified by code, recomputed by the original when towns merge and at other
    /// calls): over its finished buildings, H = those with an interior that are neither a part nor a gate, T = turrets (function 12), G = gates (21), R = the highest research
    /// <c>tech level</c> (function 3). 1 when R &gt; 1, H &gt; 1, G &gt; 0 or T &gt; 3; with a gate, 2 when R &gt; 3, H &gt; 4 or T &gt; 4, and 3 when R &gt; 3, H &gt; 4 and T &gt; 6.
    /// Engine readings: "an interior" is the BUILDING's <c>interior</c> list (the original's Building +0x1f0), a part is a site with a parent.
    /// </summary>
    internal int TownLevel(World world, int number)
    {
        int houses = 0, turrets = 0, gates = 0, research = 0;
        foreach (var b in world.Buildings.All)
        {
            if (b.Town != number || !b.Complete || b.Destroyed || b.Ruined || data.Db.Find(b.Record) is not { } r) continue;
            var f = r.GetReferences("functionality") is [var link, ..] ? data.Db.Find(link.TargetStringId) : null;
            int function = f?.GetInt("function") ?? 0;
            if (function == 12) turrets++;
            else if (function == 21) gates++;
            else if (function == 3) research = Math.Max(research, f!.GetInt("tech level"));
            if (function != 21 && b.Parent is null && r.GetReferences("interior").Count > 0) houses++;
        }
        int level = research > 1 || houses > 1 || gates > 0 || turrets > 3 ? 1 : 0;
        if (gates > 0 && (research > 3 || houses > 4 || turrets > 4)) level = 2;
        if (gates > 0 && research > 3 && houses > 4 && turrets > 6) level = 3;
        return level;
    }

    // ---- start and squads ----

    enum StartResult { Started, Weak, NoHome }

    /// <summary>
    /// Starts a campaign (<c>FUN_1409c9830</c>): a home (the given one, else the faction's town nearest the target), the next id, its squads gathered; too few men cancels it,
    /// else the messages, the last start and the <c>trigger player ally</c> requests.
    /// </summary>
    StartResult Start(World world, WarManager m, CampaignData c, CampaignTown target, CampaignTown home, double now)
    {
        if (home.IsNone || !Exists(world, home)) home = NearestOwnTown(m.Faction, CentreOf(world, target));
        if (home.IsNone || !Exists(world, target)) return StartResult.NoHome;
        var inst = new CampaignInstance
        {
            Faction = m.Faction, Id = m.NextId++, CampaignId = c.Id, Target = target, Home = home, Enemy = FactionOfTown(world, target), PhaseStart = now,
            Required = (int)(c.NumForces * settings.RaidSizeMult),
        };
        int player = PlayerFactionOf(world);
        var made = new List<Platoon>();
        Gather(world, m, c, inst, made);
        if (inst.Men <= inst.Required)
        {
            if (inst.Enemy == player && player >= 0) world.Post(Loc.Tr("Assault to {1} canceled due to faction weakness", c.DisplayName));
            Disband(world, inst, made);
            Log($"{data.Factions[m.Faction].Name}: {c.Name} against {NameOf(world, target)} canceled ({inst.Men} of {inst.Required} men)");
            return StartResult.Weak;
        }
        m.Actives.Add(inst);
        if (inst.Enemy == player && player >= 0)
            world.Post(c.IsHostile ? Loc.Tr("Warning: {1} moving towards {2}", c.DisplayName, NameOf(world, target)) : Loc.Tr("{1} on the way to {2}", c.DisplayName, NameOf(world, target)));
        m.Know(c.Id).LastStart = now;
        Log($"{data.Factions[m.Faction].Name}: {c.Name} against {NameOf(world, target)} from {NameOf(world, home)}, {inst.Platoons.Count} squad(s), {inst.Men} men");
        foreach (var allyId in c.TriggerPlayerAlly)
        {
            if (CampaignOf(allyId) is not { FactionOverride: { } fo } ally || data.FactionIndex(fo) is not (>= 0 and var af) || player < 0) continue;
            if (data.Relations.IsAlly(af, player)) Queue(world, m, ally, target, CampaignTown.None, inst.Enemy, 0, 0, now, callerFlag: false);
        }
        return StartResult.Started;
    }

    void Log(string line)
    {
        if (townLog.Count >= 256) townLog.RemoveAt(0);
        townLog.Add(line);
    }

    /// <summary>
    /// The squads (<c>uses_leader</c>), until the men exceed the need: the <c>special leader</c>'s platoon (or the template naming it leader, made near the faction's town nearest the
    /// target); then for each of the 4 own towns nearest the target its free roaming platoons, then new squads from <c>squads to use</c> by weight within 700 of the town (of the
    /// leader once there is one), up to 100 per town. Engine choices: homeless platoons and a town's own (residents, bar squads) are not drafted.
    /// </summary>
    void Gather(World world, WarManager m, CampaignData c, CampaignInstance inst, List<Platoon> made)
    {
        var targetAt = CentreOf(world, inst.Target);
        bool Full() => inst.Men > inst.Required;
        foreach (var leader in c.SpecialLeaders)
        {
            if (FindUniquePlatoon(world, leader) is { } p)
            {
                if (!Free(p, m.Faction, null)) continue;
                Join(world, inst, p, leads: true);
                break;
            }
            if (world.Uniques.IsClaimed(leader) || LeaderTemplate(leader) is not { } template) continue;
            var town = NearestOwnTown(m.Faction, targetAt);
            if (town.IsNone || NewSquad(world, inst, template, town.Site, CentreOf(world, town), made) is not { } spawned) continue;
            Join(world, inst, spawned, leads: true);
            break;
        }
        if (Full()) return;
        var towns = data.Sites.Where(s => data.SiteFactions[s.Index] == m.Faction)
            .OrderBy(s => Vector2.DistanceSquared(targetAt, SiteCentre(s.Index))).ThenBy(s => s.Index).Take(4).ToList();
        var weights = new List<(string Id, float Weight)>();
        foreach (var e in c.SquadsToUse)
            if (e.V0 > 0 && SquadFactory.IsTemplate(data.Db.Find(e.Id))) weights.Add((e.Id, e.V0));
        float total = weights.Sum(w => w.Weight);
        foreach (var town in towns)
        {
            foreach (var p in world.Platoons.All.ToList())
            {
                if (p.Origin != town.Index || !Free(p, m.Faction, c)) continue;
                Join(world, inst, p, leads: false);
                if (Full()) return;
            }
            if (total <= 0) continue;
            for (int n = 0; n < 100 && !Full(); n++)
            {
                int next = world.Platoons.NextId();
                float pick = Rng.Float(Rng.Hash(world.Seed, (ulong)m.Faction << 32 | (uint)next, RngPurpose.Campaign, SeedSalts.CampaignTemplate)) * total;
                string chosen = weights[^1].Id;
                foreach (var (id, w) in weights)
                    if ((pick -= w) < 0) { chosen = id; break; }
                var around = inst.Leader >= 0 && world.Platoons.Find(inst.Leader) is { } lead ? lead.Position : SiteCentre(town.Index);
                if (NewSquad(world, inst, SquadTemplate.From(data.Db.Find(chosen)!), town.Index, around, made, next) is { } p) Join(world, inst, p, leads: false);
            }
            if (Full()) return;
        }
    }

    /// <summary>A platoon a campaign may take: not in one, of the faction, a roaming one (not a town's own nor wild), someone alive, of a <c>squads to use</c> template when the list is set.</summary>
    bool Free(Platoon p, int faction, CampaignData? c) =>
        p.Campaign is null && !p.Pinned && !p.Wild && p.Origin >= 0 && p.Faction == faction && p.State != PlatoonState.Activating && !p.Regenerate
        && (c is null || c.SquadsToUse.Count == 0 || c.SquadsToUse.Any(e => e.Id == p.TemplateId));

    /// <summary>The platoon holding a unique character: in the world (its handle), else stored or saved in a platoon; null when none holds it.</summary>
    Platoon? FindUniquePlatoon(World world, string recordId)
    {
        if (world.Uniques.Find(recordId) is { Handle.IsNone: false } entry && world.Characters.TryResolveNext(entry.Handle, out int slot) && world.Characters.Cold(slot) is { } cold
            && world.Squads.Find(cold.SquadId) is { PlatoonId: >= 0 } squad)
            return world.Platoons.Find(squad.PlatoonId);
        foreach (var p in world.Platoons.All)
        {
            if (p.Stored is { } stored && stored.Members.Any(m => m.Cold.RecordId == recordId && m.Cold.Medical is not { Dead: true })) return p;
            if (p.Saved is { } saved && saved.Members.Any(m => !m.Dead && !m.Gone && m.Source.RecordId == recordId)) return p;
        }
        return null;
    }

    /// <summary>The squad template whose <c>leader</c> is the character (the first in the data's order; null: none).</summary>
    SquadTemplate? LeaderTemplate(string recordId)
    {
        foreach (var r in data.Db.OfType(FcsRecordType.SQUAD_TEMPLATE).Concat(data.Db.OfType(FcsRecordType.UNIQUE_SQUAD_TEMPLATE)))
            if (r.GetReferences("leader") is { Count: > 0 } l && l[0].TargetStringId == recordId) return SquadTemplate.From(r);
        return null;
    }

    /// <summary>A new campaign squad: an unloaded platoon of the template at a walkable point within 700 of <paramref name="around"/>, homed at the town (null when it plans no one).</summary>
    Platoon? NewSquad(World world, CampaignInstance inst, SquadTemplate template, int town, Vector2 around, List<Platoon> made, int id = -1)
    {
        if (id < 0) id = world.Platoons.NextId();
        ulong key = Rng.Mix(SeedSalts.CampaignSquad ^ (ulong)id << 8 ^ (ulong)inst.Faction << 40);
        var plan = SquadFactory.Plan(data.Db, template, settings.SquadSizeMultiplier, world.Seed, key, States);
        if (plan.TotalMembers == 0) return null;
        var at = around;
        for (int k = 0; k < 8; k++)
        {
            var spot = Rng.PointInDisc(Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignPlace + (ulong)k), around, CampaignSpawnRadius);
            if (world.Walkability.IsWalkable(spot.X, spot.Y)) { at = spot; break; }
        }
        var (package, job) = Ai.PlatoonJobs.For(data.Ai, template);
        var p = new Platoon
        {
            Id = id, Origin = town, TemplateId = template.Id, Faction = inst.Faction, Key = key, Size = plan.TotalMembers, Position = at, Package = package?.Id, Job = job, Target = town,
        };
        world.Platoons.Add(p);
        made.Add(p);
        return p;
    }

    void Join(World world, CampaignInstance inst, Platoon p, bool leads)
    {
        p.Campaign = inst;
        p.WaitLeft = 0;
        p.Point = null;
        inst.Platoons.Add(p.Id);
        inst.Men += LivingOf(world, p);
        if (leads || inst.Leader < 0) inst.Leader = p.Id;
    }

    /// <summary>A platoon's living members: in the world, stored, saved, or (never made) its size.</summary>
    internal int LivingOf(World world, Platoon p)
    {
        if (p.State == PlatoonState.Loaded && world.Squads.Find(p.SquadId) is { } squad) return squad.Members.Count(m => Living(world, m));
        if (p.Stored is { Members.Count: > 0 } stored) return stored.Members.Count(m => m.Cold.Medical is not { Dead: true });
        // A save's never-made platoon: its `char count`, as the original's pool counts a platoon that is not active (FUN_1408f6a40).
        if (p.Unmade) return p.Size;
        if (p.Saved is { } saved) return saved.Members.Count(m => !m.Dead && !m.Gone);
        return p.Size;
    }

    /// <summary>A cancelled start: its new squads go, the drafted ones are free again.</summary>
    void Disband(World world, CampaignInstance inst, List<Platoon> made)
    {
        foreach (var p in made) if (world.Platoons.Find(p.Id) is not null) Despawn(world, p);
        foreach (int id in inst.Platoons)
            if (world.Platoons.Find(id) is { } p) Release(world, p);
    }

    /// <summary>A platoon leaves its campaign: a roaming squad again, on its way home (Faithful: the normal despawn rules apply from now).</summary>
    void Release(World world, Platoon p)
    {
        p.Campaign = null;
        p.CampaignGoal = null;
        if (world.Squads.Find(p.SquadId) is { } assaulting)
        {
            assaulting.War = WarTargets.None;
            foreach (var m in assaulting.Members)
                if (world.Characters.TryResolveNext(m, out int ms)) world.Characters.Cold(ms)!.PaceCap = null;
        }
        p.Target = p.Origin;
        p.GoingHome = true;
        p.WaitLeft = 0;
        // No campaign holds it, so its job is its package's again (FUN_1407ee790).
        if (p.Package is { } package) p.Job = Ai.PlatoonJobs.JobOf(data.Ai.Package(package)?.Unloaded ?? 0);
        if (p.State == PlatoonState.Loaded && world.Squads.Find(p.SquadId) is { } squad) OrderLeader(world, p, squad);
    }

    // ---- requests ----

    /// <summary>
    /// Queues a campaign (<c>FUN_1409c9410</c>): handed to its <c>faction override</c> (home cleared); home missing → the receiving faction's town nearest the target, none → dropped;
    /// dropped when the same campaign and target is queued, or (without <paramref name="callerFlag"/>) running; queued by the faction itself, dropped while one of its campaigns has
    /// <c>trig</c>, else <c>trig</c> set on all of them. Due after uniform[<paramref name="minHours"/>, <paramref name="maxHours"/>) hours. Returns whether it was queued.
    /// </summary>
    bool Queue(World world, WarManager from, CampaignData c, CampaignTown target, CampaignTown home, int enemy, float minHours, float maxHours, double now, bool callerFlag)
    {
        var to = from;
        if (c.FactionOverride is { } fo && data.FactionIndex(fo) is >= 0 and var fi && fi != from.Faction && WarOf(fi) is { } other)
        {
            to = other;
            home = CampaignTown.None;
        }
        if (home.IsNone || !Exists(world, home)) home = NearestOwnTown(to.Faction, CentreOf(world, target));
        if (home.IsNone) return false;
        if (to.Requests.Any(r => r.CampaignId == c.Id && r.Target == target)) return false;
        if (!callerFlag && to.Actives.Any(a => a.CampaignId == c.Id && a.Target == target)) return false;
        if (to == from)
        {
            if (from.Actives.Any(a => a.Trig)) return false;
            foreach (var a in from.Actives) a.Trig = true;
        }
        ulong roll = Rng.Hash(world.Seed, (ulong)to.Faction << 32 | (uint)to.Queued++, RngPurpose.Campaign, Rng.StableHash(c.Id) ^ SeedSalts.CampaignDelay);
        to.Requests.Add(new CampaignRequest { CampaignId = c.Id, Enemy = enemy, Due = now + minHours + Rng.Float(roll) * Math.Max(0, maxHours - minHours), Target = target, Home = home });
        to.Know(c.Id);
        return true;
    }

    /// <summary>
    /// Dialogue's DA 37 ASSAULT_PHASE / DA 38 RETREAT_PHASE (<c>FUN_140680560</c>, <b>Verified</b>): the campaign the speaker's squad marches in goes to phase 1 (2 when it has no
    /// <c>pt2</c> leader package) or 2, its phase clock restarting. DA 39 (phase 3, which no phase handles; the data never uses it) is not built. Returns whether a campaign changed.
    /// </summary>
    public bool SetCampaignPhase(World world, CharacterId speaker, bool assault)
    {
        if (!world.Characters.TryResolveNext(speaker, out int s) || world.Characters.Cold(s) is not { SquadId: >= 0 } sc) return false;
        if (world.Platoons.All.FirstOrDefault(p => p.SquadId == sc.SquadId && p.State == PlatoonState.Loaded && p.Campaign is not null)?.Campaign is not { Phase: not 2 } inst
            || CampaignOf(inst.CampaignId) is not { } c) return false;
        int phase = assault && c.HasPt2 ? 1 : 2;
        if (inst.Phase == phase) return false;
        inst.Phase = phase;
        inst.PhaseStart = world.GameHours;
        return true;
    }

    /// <summary>
    /// A DIALOGUE_LINE's <c>trigger campaign</c> list (<c>FUN_140680560</c>, docs/game/dialogue.md "Effects"), said by <paramref name="speaker"/> to <paramref name="target"/>: nothing
    /// when the speaker's squad is in a campaign; the target town is the player-faction town nearest the speaker when the target is of the player's faction (none: nothing, so a
    /// homeless player gets no dialogue campaign), else the town of the target's platoon (none: nothing), and the speaker's own current town when the line has no resolvable
    /// target (<c>FUN_140680560</c>, <b>Verified</b>). One weighted draw queues an entry for the speaker's faction, due after uniform[V0, V1) hours.
    /// Returns whether one was queued.
    /// </summary>
    public bool TriggerFromDialogue(World world, CharacterId speaker, CharacterId target, IReadOnlyList<CampaignLink> list)
    {
        var table = world.Characters;
        if (list.Count == 0 || !table.TryResolveNext(speaker, out int s) || table.Cold(s) is not { } sc) return false;
        if (sc.SquadId >= 0 && world.Platoons.All.Any(p => p.SquadId == sc.SquadId && p.State == PlatoonState.Loaded && p.Campaign is not null)) return false;
        EnsureWars(world);
        if (WarOf(sc.Faction) is not { } m) return false;
        var at = new Vector2(table.Next[s].Position.X, table.Next[s].Position.Z);
        var tc = table.TryResolveNext(target, out int t) ? table.Cold(t) : null;
        // Without a (resolvable) target the town is the speaker's current one; a resolved target replaces it, also with none (a platoon without an origin).
        var town = tc is null ? TownAt(world, at) : CampaignTown.None;
        if (tc is not null && (tc.IsPlayer || tc.Faction == world.Player.Faction))
        {
            float best = float.MaxValue;
            foreach (var pt in world.Buildings.Towns)
            {
                if (pt.Owner != world.Player.Faction) continue;
                float d = Vector2.DistanceSquared(at, new Vector2(pt.Position.X, pt.Position.Z));
                if (d < best) (best, town) = (d, CampaignTown.OfPlayer(pt.Number));
            }
        }
        else if (tc is { SquadId: >= 0 } && world.Platoons.All.FirstOrDefault(p => p.SquadId == tc.SquadId && p.State == PlatoonState.Loaded) is { Origin: >= 0 } tp)
            town = CampaignTown.OfSite(tp.Origin);
        if (town.IsNone || !Exists(world, town)) return false;

        ulong key = (ulong)speaker.Slot << 32 | (uint)world.Tick;
        float r = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignDialogue)) * 100;
        foreach (var e in list)
        {
            if ((r -= e.V2) >= 0) continue;
            if (CampaignOf(e.Id) is not { } c) return false;
            float delay = e.V0 + Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignDialogue + 1)) * Math.Max(0, e.V1 - e.V0);
            return Queue(world, m, c, town, CampaignTown.None, FactionOfTown(world, town), delay, delay, world.GameHours, callerFlag: true);
        }
        return false;
    }

    /// <summary>
    /// The due requests (<c>FUN_1409cb310</c>'s request pass): a gone target (missing, or a player town occupied for a non-hostile campaign or not active) is redirected, the start
    /// waiting for the next run, else delayed 5-10 h; a failed world-state or leader gate drops it; else a start, tried each run (no chance, cooldown, range or population gate).
    /// Delays and starts share one counter (<see cref="CampaignRequest.Tries"/>): dropped when a delay takes it above 10, or a failed start above 100.
    /// </summary>
    void UpdateRequests(World world, WarManager m, double now)
    {
        for (int i = 0; i < m.Requests.Count; i++)
        {
            var r = m.Requests[i];
            if (r.Due > now) continue;
            if (CampaignOf(r.CampaignId) is not { } c)
            {
                m.Requests.RemoveAt(i--);
                continue;
            }
            // Gone: no such town, or a player town that is occupied (for a non-hostile campaign) or not active. A redirected request starts at the next run.
            if (!Exists(world, r.Target) || r.Target.PlayerTown > 0 && (!c.IsHostile && Ai.AiSystem.Occupied(world, r.Target) || !TownActive(world, r.Target)))
            {
                if (FindTarget(world, m.Faction, c, r.Enemy, out var retarget, out _))
                {
                    r.Target = retarget;
                    continue;
                }
                ulong roll = Rng.Hash(world.Seed, (ulong)m.Faction << 32 | (uint)i, RngPurpose.Campaign, BitConverter.DoubleToUInt64Bits(r.Due) ^ SeedSalts.CampaignRetry);
                r.Due += 5 + Rng.Float(roll) * 5;
                if (++r.Tries > 10) m.Requests.RemoveAt(i--);
                continue;
            }
            if (!WorldStatesPass(world, c))
            {
                m.Requests.RemoveAt(i--);
                continue;
            }
            // A start only appends requests (the ally help), so i still points at r.
            var result = Start(world, m, c, r.Target, r.Home, now);
            if (result != StartResult.Weak || ++r.Tries > 100) m.Requests.RemoveAt(i--);
        }
    }

    // ---- running campaigns ----

    /// <summary>The redirect timer's period in game hours (<b>Verified</b>, 0.2 in <c>FUN_1409d62f0</c>).</summary>
    const double RedirectHours = 0.2;

    /// <summary>
    /// The redirect (<c>FUN_1409d62f0</c>, docs/game/factions-squads-towns.md "Campaigns"): a running campaign whose target town is gone, or is a player town that is not active
    /// (<see cref="TownActive"/>, Town slot 70: the player has left it), looks for another every 0.2 game hours (the timer starts when it is first found so, and restarts after each look,
    /// the original's +0xf0); a different target found replaces it with "{1} has redirected to {2}". The home stays. None found keeps the old target.
    /// </summary>
    void Redirect(World world, CampaignData c, CampaignInstance inst, double now)
    {
        bool lost = inst.Target.IsNone || !Exists(world, inst.Target) || inst.Target.PlayerTown > 0 && !TownActive(world, inst.Target);
        if (!lost || inst.Enemy < 0) return;   // engine choice: with no target faction there is nothing to look in
        if (inst.RedirectStart < 0) inst.RedirectStart = now;
        if (now - inst.RedirectStart < RedirectHours) return;
        if (FindTarget(world, inst.Faction, c, inst.Enemy, out var target, out _) && target != inst.Target)
        {
            inst.Target = target;
            world.Post(Loc.Tr("{1} has redirected to {2}", c.DisplayName, NameOf(world, target)));
        }
        inst.RedirectStart = now;
    }

    /// <summary>
    /// One campaign's update (<c>CampaignInstance::vfunc_10</c>); true when it is to be removed. Phase 0: the leader within 0.8 × the town's radius arrives ("{1} has arrived at {2}"),
    /// then phase 1, or 2 without victory or defeat when there is no <c>pt2</c>. Phase 1: the leader back in its home town wins; else by <c>key</c> (assault 4 h or 0.2 h after its
    /// first loaded fight; defend 12 h with no hostile campaign against the player; trader visit 1 h); then fewer than 20 % intact platoons lose. Any phase but 2: the leader
    /// platoon loaded with everyone down loses. Phase 2: removed after 3 h.
    /// </summary>
    bool UpdateCampaign(World world, WarManager m, CampaignInstance inst, double now)
    {
        if (CampaignOf(inst.CampaignId) is not { } c)
        {
            foreach (int id in inst.Platoons) if (world.Platoons.Find(id) is { } gone) Release(world, gone);
            return true;
        }
        inst.Lost += inst.Platoons.Count(id => world.Platoons.Find(id) is null);
        inst.Platoons.RemoveAll(id => world.Platoons.Find(id) is not { } p || p.Campaign != inst);
        if (inst.Leader < 0 || !inst.Platoons.Contains(inst.Leader)) inst.Leader = inst.Platoons.Count > 0 ? inst.Platoons[0] : -1;
        double hours = now - inst.PhaseStart;
        if (inst.Phase == 2)
        {
            if (hours <= 3) return false;
            foreach (int id in inst.Platoons) if (world.Platoons.Find(id) is { } p) Release(world, p);
            inst.Platoons.Clear();
            return true;
        }
        Redirect(world, c, inst, now);
        var leader = inst.Leader >= 0 ? world.Platoons.Find(inst.Leader) : null;
        // Engine choice: a campaign with no platoon left is defeated (the original keeps it waiting for a leader that cannot come).
        if (leader is null)
        {
            Defeat(world, m, c, inst, now);
            return false;
        }
        var leaderTown = TownAt(world, leader.Position);
        if (!inst.Reached && leaderTown == inst.Target) inst.Reached = true;
        TrackFights(world, inst, now);
        if (leader.State == PlatoonState.Loaded && AllDown(world, leader))
        {
            Defeat(world, m, c, inst, now);
            return false;
        }
        switch (inst.Phase)
        {
            case 0:
                // A loaded campaign that is neither hostile nor a defence is defeated at once within 1.8 × the radius of a town with hostile occupiers (Town +0x128).
                if (leader.State == PlatoonState.Loaded && !c.IsHostile && c.Key != CampaignKey.DefendTown && OccupiedNear(world, leader.Position))
                {
                    Defeat(world, m, c, inst, now);
                    return false;
                }
                // Strictly inside 0.8 x the radius (FUN_140926d50); a nest marker, whose radius is 0, is never inside.
                float arrival = 0.8f * RadiusOf(world, inst.Target);
                if (Vector2.DistanceSquared(leader.Position, CentreOf(world, inst.Target)) >= arrival * arrival) return false;
                if (inst.ArrivedAt < 0)
                {
                    world.Post(Loc.Tr("{1} has arrived at {2}", c.DisplayName, NameOf(world, inst.Target)));
                    inst.ArrivedAt = now;
                }
                // With an announcement, a loaded leader waits for it, then while its negotiation is pending (engine choice for the Unknown event-2 state: up to
                // AnnouncementPatience hours from arriving); off screen nobody can hear it, so it goes on.
                if (c.DialogAnnouncement is not null && leader.State == PlatoonState.Loaded && Announcer is not null && now - inst.ArrivedAt < AnnouncementPatience
                    && (inst.AnnouncedAt < 0 || PendingTalk?.Invoke(inst.Announcer) == true)) return false;
                inst.Phase = c.HasPt2 ? 1 : 2;
                inst.PhaseStart = now;
                return false;
            case 1:
                if (leaderTown == inst.Home)
                {
                    Victory(world, m, c, inst, now);
                    return false;
                }
                // The cannibals' own rule (package class 32, Observed): the retreat starts when three of them carry someone or 15 game minutes after the first did.
                if (c.LeaderPackages[1] is { } pt2 && data.Ai.Package(pt2) is { Signal: 32 } && CannibalsLoaded(world, inst, now))
                {
                    inst.Phase = 2;
                    inst.PhaseStart = now;
                    return false;
                }
                bool won = false;
                switch (c.Key)
                {
                    case CampaignKey.AssaultTown:
                        if (hours > 4) won = true;
                        else if (PlayersHold(world, inst.Target)) return false;
                        else if (inst.FightStart >= 0 && now - inst.FightStart > 0.2 && (!AnyFighting(world, inst) || !PlayersStanding(world, inst.Target))
                            && DoorsOpen(world, inst.Target) && !WallsLockedUp(world, inst.Target)) won = true;
                        break;
                    case CampaignKey.DefendTown:
                        won = hours > 12 && !HostileAgainstPlayer(world);
                        break;
                    case CampaignKey.TraderVisit:
                        won = hours > 1;   // trading holds it in the original (an open trade window); not modelled, engine choice
                        break;
                }
                if (won)
                {
                    Victory(world, m, c, inst, now);
                    return false;
                }
                int intact = inst.Platoons.Count(id => world.Platoons.Find(id) is { } p && Intact(world, p));
                if (inst.Platoons.Count == 0 || intact < 0.2f * (inst.Platoons.Count + inst.Lost)) Defeat(world, m, c, inst, now);
                return false;
        }
        return false;
    }

    /// <summary>Whether a point is within 1.8 × the radius of a town with hostile occupiers (only player towns get them, <see cref="Ai.AiSystem.Occupied"/>).</summary>
    bool OccupiedNear(World world, Vector2 at)
    {
        foreach (var town in world.Buildings.Towns)
        {
            var t = CampaignTown.OfPlayer(town.Number);
            float r = 1.8f * RadiusOf(world, t);
            if (Vector2.DistanceSquared(at, CentreOf(world, t)) <= r * r && Ai.AiSystem.Occupied(world, t)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a loaded platoon of a cannibal assault carries enough to retreat (<c>FUN_14028fdf0</c>, <b>Verified</b>): each platoon counts its own carriers and keeps its own stamp of the
    /// first one, cleared while nobody (or no loaded squad) carries; any platoon with three carriers, or more than <see cref="CannibalCarryHours"/> since its first, ends the campaign's assault.
    /// </summary>
    bool CannibalsLoaded(World world, CampaignInstance inst, double now)
    {
        bool retreat = false;
        var table = world.Characters;
        foreach (int id in inst.Platoons)
        {
            int carriers = 0;
            if (world.Platoons.Find(id) is { State: PlatoonState.Loaded } p && world.Squads.Find(p.SquadId) is { } squad)
                foreach (var member in squad.Members)
                    if (table.TryResolveNext(member, out int slot) && !table.Cold(slot)!.Carrying.IsNone) carriers++;
            if (carriers == 0)
            {
                inst.CarryStarts.Remove(id);
                continue;
            }
            if (!inst.CarryStarts.TryGetValue(id, out double start)) inst.CarryStarts[id] = start = now;
            if (carriers >= 3 || now - start > CannibalCarryHours) retreat = true;
        }
        if (inst.CarryStarts.Count > 0)
            foreach (int id in inst.CarryStarts.Keys.Where(k => !inst.Platoons.Contains(k)).ToList()) inst.CarryStarts.Remove(id);
        return retreat;
    }

    /// <summary>Game hours after a platoon's first cannibal carrier that end the assault (more than 15 game minutes: the timer in hours x 60 above 15, <b>Verified</b>).</summary>
    public const double CannibalCarryHours = 0.25;

    /// <summary>
    /// Whether the members of a squad run the "find, kidnap and put in a cage" goal now (task 254, <see cref="Meitou.Data.Gameplay.Ai.TaskType.FINAL_KIDNAPPER_CAGE_JOB"/>): the goals of
    /// <paramref name="role"/> in the package their campaign is in the phase of (a loaded campaign squad is driven by the campaign, not the AI), or in the squad's running AI package, in the hour's window.
    /// </summary>
    public bool KidnapDuty(World world, Squad squad, Meitou.Data.Gameplay.Ai.AiRole role) =>
        GoalDuty(world, squad, role, Meitou.Data.Gameplay.Ai.TaskType.FINAL_KIDNAPPER_CAGE_JOB);

    /// <summary>As <see cref="KidnapDuty"/> for any goal type: whether <paramref name="role"/>'s goals in the package the squad runs now hold <paramref name="type"/> in the hour's window.</summary>
    public bool GoalDuty(World world, Squad squad, Meitou.Data.Gameplay.Ai.AiRole role, Meitou.Data.Gameplay.Ai.TaskType type)
    {
        IReadOnlyList<Meitou.Data.Gameplay.Ai.AiGoal>? goals = null;
        if (squad.PlatoonId >= 0 && world.Platoons.Find(squad.PlatoonId) is { Campaign: { } inst } p)
        {
            if (inst.Phase > 2 || CampaignOf(inst.CampaignId)?.PackageOf(inst.Phase, inst.Leader == p.Id) is not { } id) return false;
            goals = data.Ai.Package(id)?.Goals(role);
        }
        else if (squad.Ai is { Roles: { } roles }) goals = roles[(int)role];
        if (goals is null) return false;
        int hour = world.Hour;
        for (int k = 0; k < goals.Count; k++)
            if (goals[k].Task.Type == type && goals[k].InWindow(hour)) return true;
        return false;
    }

    void Victory(World world, WarManager m, CampaignData c, CampaignInstance inst, double now)
    {
        inst.Phase = 2;
        inst.PhaseStart = now;
        if (c.IsHostile) world.Post(Loc.Tr("{1} against {2} has ended", c.DisplayName, NameOf(world, inst.Target)));
        Log($"{data.Factions[m.Faction].Name}: {c.Name} against {NameOf(world, inst.Target)} won");
        FollowUp(world, m, c.VictoryTriggers, inst, now, 1);
    }

    void Defeat(World world, WarManager m, CampaignData c, CampaignInstance inst, double now)
    {
        inst.Phase = 2;
        inst.PhaseStart = now;
        string target = NameOf(world, inst.Target);
        world.Post(!c.IsHostile ? Loc.Tr("{1} was defeated on their way to {2}", c.DisplayName, target) : inst.FoughtPlayer ? Loc.Tr("{1} against {2} has been defeated", c.DisplayName, target) : Loc.Tr("{1} to {2} has failed", c.DisplayName, target));
        Log($"{data.Factions[m.Faction].Name}: {c.Name} against {target} lost");
        if (c.IsHostile && inst.Reached || inst.FoughtPlayer) FollowUp(world, m, c.LossTriggers, inst, now, 2);
        else if (c.IsHostile)
        {
            ulong roll = Rng.Hash(world.Seed, (ulong)m.Faction << 32 | (uint)inst.Id, RngPurpose.Campaign, SeedSalts.CampaignRetry);
            float delay = 40 + Rng.Float(roll) * 56;
            Queue(world, m, c, inst.Target, CampaignTown.None, inst.Enemy, delay, delay, now, callerFlag: true);
        }
    }

    /// <summary>One follow-up draw (<c>FUN_1409d27a0</c>): r = uniform[0, 100); below the sum of the V2 percents the entry whose share holds r is queued, due after uniform[V0, V1) hours.</summary>
    void FollowUp(World world, WarManager m, IReadOnlyList<CampaignLink> list, CampaignInstance inst, double now, ulong which)
    {
        if (list.Count == 0) return;
        ulong key = (ulong)m.Faction << 32 | (uint)inst.Id;
        float r = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignFollowUp + which)) * 100;
        foreach (var e in list)
        {
            if ((r -= e.V2) >= 0) continue;
            if (CampaignOf(e.Id) is not { } next) return;
            float delay = e.V0 + Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignDelay + which)) * Math.Max(0, e.V1 - e.V0);
            Queue(world, m, next, inst.Target, CampaignTown.None, inst.Enemy, delay, delay, now, callerFlag: true);
            return;
        }
    }

    /// <summary>Loaded fights (the original's are loaded only): the first one starts the assault's fight timer; a leader-platoon member fighting within 300 of a player character marks a fight with the player (engine test for the <b>Unknown</b> one).</summary>
    void TrackFights(World world, CampaignInstance inst, double now)
    {
        var table = world.Characters;
        foreach (int id in inst.Platoons)
        {
            if (world.Platoons.Find(id) is not { State: PlatoonState.Loaded } p || world.Squads.Find(p.SquadId) is not { } squad) continue;
            foreach (var member in squad.Members)
            {
                if (!table.TryResolveNext(member, out int slot) || table.Cold(slot) is not { InCombat: true }) continue;
                if (inst.FightStart < 0) inst.FightStart = now;
                if (id == inst.Leader && !inst.FoughtPlayer && PlayerNear(world, table.Previous[slot].Position, 300)) inst.FoughtPlayer = true;
            }
        }
    }

    static bool PlayerNear(World world, Vector3 at, float range)
    {
        var prev = world.Characters.Previous;
        for (int i = 0; i < prev.Length; i++)
            if (prev[i].Alive && world.Characters.Cold(i) is { IsPlayer: true } && Vector3.DistanceSquared(prev[i].Position, at) <= range * range) return true;
        return false;
    }

    bool AnyFighting(World world, CampaignInstance inst)
    {
        foreach (int id in inst.Platoons)
            if (world.Platoons.Find(id) is { State: PlatoonState.Loaded } p && world.Squads.Find(p.SquadId) is { } squad
                && squad.Members.Any(m => world.Characters.TryResolveNext(m, out int s) && world.Characters.Cold(s) is { InCombat: true }))
                return true;
        return false;
    }

    /// <summary>Up: alive and conscious.</summary>
    static bool Up(World world, CharacterId m) =>
        Living(world, m) && world.Characters.Cold(m.Slot) is not { Medical.Unconscious: true };

    /// <summary>A loaded platoon with every member down (dead or out).</summary>
    static bool AllDown(World world, Platoon p) => world.Squads.Find(p.SquadId) is not { } squad || !squad.Members.Any(m => Up(world, m));

    /// <summary>
    /// The platoon's intact flag (<b>Verified</b>, <c>FUN_1407ac7b0</c>): while loaded the squad-intact rule (<c>FUN_1407a5be0</c>, docs/game/perception.md: a member held keeps it
    /// intact; under 4 members the leader is up; else at least 20 % up, 40 % with the leader down), stored; unloaded, the value it had at unload.
    /// </summary>
    bool Intact(World world, Platoon p)
    {
        if (p.Dead) return false;   // declared dead (+0x1f0): never intact
        if (p.State == PlatoonState.Loaded && world.Squads.Find(p.SquadId) is { } squad) p.IntactFlag = SquadIntact(world, squad);
        return p.IntactFlag;
    }

    /// <summary>The squad-intact rule (<see cref="Ai.SquadIntact"/>, <c>FUN_14079acf0</c>): a campaign platoon is not a persistent one with a home.</summary>
    bool SquadIntact(World world, Squad squad)
    {
        var table = world.Characters;
        if (Ai.SquadIntact.Held(table, squad, data.Relations)) return true;
        int up = 0;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && table.Next[s].Alive && table.Cold(s)?.Medical is not { Incapacitated: true }) up++;
        return Ai.SquadIntact.Decide(up, squad.Members.Count, !squad.Leader.IsNone, Ai.SquadIntact.Down(table, squad.Leader), persistentHome: false);
    }

    /// <summary>The player's characters in a town (XZ, its radius): (in all, up).</summary>
    (int All, int Up) PlayersIn(World world, CampaignTown t)
    {
        var at = CentreOf(world, t);
        float r = RadiusOf(world, t);
        int all = 0, up = 0;
        var prev = world.Characters.Previous;
        for (int i = 0; i < prev.Length; i++)
        {
            if (!prev[i].Alive || world.Characters.Cold(i) is not { IsPlayer: true } cold) continue;
            if (Vector2.DistanceSquared(new Vector2(prev[i].Position.X, prev[i].Position.Z), at) > r * r) continue;
            all++;
            if (cold.DiedTick < 0 && cold.Medical is not { Unconscious: true } and not { Dead: true }) up++;
        }
        return (all, up);
    }

    /// <summary>More than 2 player characters in the town, more than half of them up: an assault keeps going.</summary>
    bool PlayersHold(World world, CampaignTown t)
    {
        var (all, up) = PlayersIn(world, t);
        return all > 2 && up * 2 > all;
    }

    bool PlayersStanding(World world, CampaignTown t) => PlayersIn(world, t).Up > 0;

    /// <summary>The first door fixture of each of a town's buildings (a placed town's by site, a player town's by its buildings): the slots 92 and 96 read only the building's first door (<c>Building</c> slot 124).</summary>
    static IEnumerable<Locks.Fixture> TownDoors(World world, CampaignTown t) => world.Fixtures.All.Where(f => f.Kind == Locks.FixtureKind.Door && f.DoorIndex == 0
        && (t.Site >= 0 ? f.Site == t.Site : t.PlayerTown > 0 && world.Buildings.Find(f.Building) is { } b && b.Town == t.PlayerTown));

    /// <summary>Town slot 92 (<c>FUN_1408cc930</c>): no house door of the town is closed (a broken one counts as open); true without any.</summary>
    static bool DoorsOpen(World world, CampaignTown t) => TownDoors(world, t).All(f => f.IsGate || f.DoorBroken || f.Motion != Locks.DoorMotion.Closed);

    /// <summary>Town slot 96 (<c>FUN_140929820</c>): the town has a gate and none of its gates is open (a broken one counts as open).</summary>
    static bool WallsLockedUp(World world, CampaignTown t)
    {
        bool any = false;
        foreach (var gate in TownDoors(world, t).Where(f => f.IsGate))
        {
            if (gate.DoorBroken || gate.Motion == Locks.DoorMotion.Open) return false;
            any = true;
        }
        return any;
    }

    /// <summary>Some faction runs a hostile campaign against the player (a defence ends only when none does).</summary>
    bool HostileAgainstPlayer(World world)
    {
        int player = PlayerFactionOf(world);
        foreach (var m in wars!)
            foreach (var a in m.Actives)
                if (a.Phase != 2 && a.Enemy == player && CampaignOf(a.CampaignId) is { IsHostile: true }) return true;
        return false;
    }

    // ---- moving ----

    /// <summary>
    /// Sets every campaign platoon's goal (the leader's: the target, or home once ended; the others': the leader) and moves the unloaded ones in straight lines at the
    /// <c>travel speed unloaded</c> speeds (leader TRAVEL_TARGET, others TRAVEL_TARGET_FAST: WALK 25, JOG 55, RUN 90; GROUPED and NO_SPEED_CHANGE leader 25, others 60 or 90 beyond
    /// 25000; all capped at 70). Serial: followers read their leader's place.
    /// </summary>
    void MoveCampaigns(World world, float dt)
    {
        foreach (var m in wars!)
            foreach (var inst in m.Actives)
            {
                if (CampaignOf(inst.CampaignId) is not { } c || world.Platoons.Find(inst.Leader) is not { } leader) continue;
                var target = inst.Phase == 2 ? CentreOf(world, inst.Home) : CentreOf(world, inst.Target);
                int count = inst.Platoons.Count, slots = count > TrailSlots ? count + 1 : TrailSlots, follower = 0;
                LayTrail(world, inst, leader.Position, slots, count);
                foreach (int id in inst.Platoons)
                {
                    if (world.Platoons.Find(id) is not { } p) continue;
                    bool leads = p == leader;
                    // Follower i keeps i × slots × 150 / N back along the leader's trail (engine choice for the Unknown: the trail point, not the leader platoon).
                    var goal = leads ? target : TrailPoint(inst, ++follower * slots * TrailStep / count);
                    p.CampaignGoal = goal;
                    if (p.State != PlatoonState.Unloaded) continue;
                    var to = goal - p.Position;
                    float d = to.Length() - (leads ? 0 : CampaignFollowDistance);
                    if (d <= 0) continue;
                    float speed = MathF.Min(CampaignSpeed(c.TravelSpeedUnloaded, leads, d), StandInSpeedCap);
                    // The leader takes the roads by the original's rule; followers go straight for it (engine choice: their goal moves every look).
                    if (leads) Travel(p, goal, speed * dt);
                    else p.Position += to / to.Length() * MathF.Min(speed * dt, d);
                }
            }
    }

    /// <summary>
    /// Whom a loaded campaign platoon fights besides its faction's enemies (docs/game/factions-squads-towns.md "Campaigns", enemies): in phase 1, everyone in the target town or in none
    /// (<c>AggroSystem.WarEnemy</c>) when its <c>pt2</c> package is a Package_WarAssault (signal 20 or 32), else the target faction when the campaign is hostile; nobody in the other phases.
    /// </summary>
    WarTargets AssaultTargets(World world, CampaignInstance inst, bool leads)
    {
        if (inst.Phase != 1 || CampaignOf(inst.CampaignId) is not { } c) return WarTargets.None;
        if (c.PackageOf(1, leads) is { } id && data.Ai.Package(id) is { Signal: 20 or 32 })
            return new WarTargets(-1, true, inst.Target);
        return c.IsHostile && inst.Enemy >= 0 ? new WarTargets(inst.Enemy, false, CampaignTown.None) : WarTargets.None;
    }

    /// <summary>The dialogue's announcement and pending-talk queries (<see cref="Dialogue.DialogueSystem.Announce"/>, <see cref="Dialogue.DialogueSystem.HasInterrupt"/>), set by the host; null: none.</summary>
    public Func<World, CharacterId, string, bool>? Announcer { get; set; }
    public Func<CharacterId, bool>? PendingTalk { get; set; }

    /// <summary>The squads' memories (the dialogue system's): a squad that leaves the world hands its memory to its platoon (<see cref="StoredSquad.Memory"/>), and gets it back when it returns; null: none kept.</summary>
    public Dialogue.SquadMemory? Memory { get; set; }

    /// <summary>Game hours an arrived campaign waits for its announcement and negotiation before it goes on (engine choice).</summary>
    public const double AnnouncementPatience = 1;
    /// <summary>The announcement needs the leader within this of the camera and a target-faction member within this of the leader (<b>Verified</b>, task 129).</summary>
    const float AnnounceRange = 2500;

    /// <summary>
    /// A loaded leader in phase 0 that has arrived at the target (task 129 needs state 131 <c>atPackageTarget</c>; Meitou: <see cref="CampaignInstance.ArrivedAt"/>) and is not
    /// down (!101) says its campaign's <c>dialog announcement</c> once, with the camera and a target-faction member within 2500 of it.
    /// </summary>
    void TryAnnounce(World world, CampaignInstance inst, CharacterId leader, Vector3 at)
    {
        if (inst.Phase != 0 || inst.ArrivedAt < 0 || inst.AnnouncedAt >= 0 || Announcer is null || inst.Enemy < 0 || CampaignOf(inst.CampaignId)?.DialogAnnouncement is not { } announcement) return;
        if (!world.Characters.TryResolveNext(leader, out int ls) || !world.Characters.Next[ls].Alive || world.Characters.Cold(ls)?.Medical?.Incapacitated == true) return;
        if (focus is not { } camera || Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(camera.X, camera.Z)) > AnnounceRange * AnnounceRange) return;
        var table = world.Characters;
        bool heard = false;
        for (int i = 0; i < table.Previous.Length && !heard; i++)
            heard = table.Previous[i].Alive && table.Cold(i) is { } c && c.Faction == inst.Enemy && Vector3.DistanceSquared(table.Previous[i].Position, at) <= AnnounceRange * AnnounceRange;
        if (!heard || !Announcer(world, leader, announcement)) return;
        inst.Announcer = leader;
        inst.AnnouncedAt = world.GameHours;
    }

    /// <summary>The trail's step and its first slot count (<b>Verified</b>, <c>FUN_1409d6070</c>).</summary>
    const float TrailStep = 150;
    const int TrailSlots = 5;

    /// <summary>
    /// The leader's trail, each war-manager run (<b>Verified</b>): an empty one is seeded with <paramref name="slots"/> + 1 points within ±100 of the leader; the leader's
    /// place is added once it is over 150 from the newest; one oldest point is dropped while there are more points than platoons.
    /// </summary>
    void LayTrail(World world, CampaignInstance inst, Vector2 leader, int slots, int platoons)
    {
        var trail = inst.Trail;
        if (trail.Count == 0)
        {
            ulong key = (ulong)inst.Faction << 32 | (uint)inst.Id;
            for (int k = 0; k <= slots; k++)
            {
                float x = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignTrail + (ulong)(2 * k))) * 200 - 100;
                float z = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignTrail + (ulong)(2 * k + 1))) * 200 - 100;
                trail.Add(leader + new Vector2(x, z));
            }
        }
        else if (Vector2.Distance(trail[^1], leader) > TrailStep) trail.Add(leader);
        if (trail.Count > Math.Max(platoons, 1)) trail.RemoveAt(0);
    }

    /// <summary>The point <paramref name="back"/> units back along the trail from its newest point (its oldest when the trail is shorter).</summary>
    static Vector2 TrailPoint(CampaignInstance inst, float back)
    {
        var trail = inst.Trail;
        for (int k = trail.Count - 1; k > 0; k--)
        {
            float d = Vector2.Distance(trail[k], trail[k - 1]);
            if (back <= d) return Vector2.Lerp(trail[k], trail[k - 1], d > 0 ? back / d : 0);
            back -= d;
        }
        return trail[0];
    }

    /// <summary>A <c>travel speed loaded</c> as a movement mode (<b>Verified</b>: WALK the default walk, JOG 55, RUN and GROUPED uncapped, NO_SPEED_CHANGE as it was).</summary>
    static byte LoadedMode(MoveSpeed speed, byte current) => speed switch
    {
        MoveSpeed.Walk => SpeedMode.Walk,
        MoveSpeed.Jog => SpeedMode.Run,
        MoveSpeed.Run or MoveSpeed.Grouped => SpeedMode.Free,
        _ => current,
    };

    /// <summary>The leader keeps its loaded speed while every intact platoon is within this of it (<b>Verified</b>), else it stops within <see cref="WaitNearTarget"/> of its target, else walks.</summary>
    const float ColumnRange = 6000, WaitNearTarget = 8294.4f;

    static float CampaignSpeed(MoveSpeed speed, bool leader, float distance) => speed switch
    {
        MoveSpeed.Walk => 25,
        MoveSpeed.Jog => 55,
        MoveSpeed.Run => 90,
        _ => leader ? 25 : distance > 25000 ? 90 : 60,
    };

    /// <summary>A loaded campaign platoon (serial, from the roaming look): its leader is sent to the goal (walking for WALK, else running), and mills about once there.</summary>
    void StepCampaignLoaded(World world, Platoon p, Squad squad, float dt)
    {
        var table = world.Characters;
        squad.War = AssaultTargets(world, p.Campaign!, p.Campaign!.Leader == p.Id);
        if (p.CampaignGoal is not { } goal || !table.TryResolveNext(squad.Leader, out int slot)) return;
        var cold = table.Cold(slot)!;
        cold.PaceCap = null;   // set again below while it trails at the leader's pace
        if (p.Campaign!.Leader == p.Id) TryAnnounce(world, p.Campaign, squad.Leader, table.Next[slot].Position);
        // Retreating, members the column does not move (a carrier, whom the AI leaves alone) mill round their own town again, not round the raid target the squad last
        // milled at, so a kidnapper takes its captive home (engine choice; the arrival below still mills them round the goal).
        if (p.Campaign.Phase == 2 && squad.Town >= 0 && Vector2.Distance(p.Position, goal) > (p.Campaign.Leader == p.Id ? settings.RoamArrivalRadius : CampaignFollowDistance))
        {
            squad.HomeCentre = SiteCentre(squad.Town);
            squad.HomeRadius = 60;
        }
        if (table.Next[slot].Task is (byte)CharacterTask.Attack or (byte)CharacterTask.PickUp or (byte)CharacterTask.PutDown or (byte)CharacterTask.Loot) return;   // a fight, a lift (a kidnapper) or a loot is left to itself
        bool leads = p.Campaign!.Leader == p.Id;
        float arrive = leads ? settings.RoamArrivalRadius : CampaignFollowDistance;
        ref var n = ref table.Next[slot];
        float d = Vector2.Distance(p.Position, goal);
        // A travelling follower (task 215) never mills: inside 100 of its trail point it keeps walking straight at it at the leader's pace (FUN_140349e70); only the
        // arrival at the target (phase 1) mills.
        if (d <= arrive && (leads || p.Campaign.Phase == 1))
        {
            if (n.Task == (byte)CharacterTask.GoTo)
            {
                TaskStart.Become(world, slot, CharacterTask.Wander, Meitou.Data.Gameplay.Ai.TaskType.WANDERER, TaskSource.Goal);
                n.Mode = SpeedMode.Walk;
                n.TaskTime = 0.5f;
            }
            squad.HomeCentre = goal;
            // The mill at the target is an engine choice, sized as a roaming squad's at a town (PopulationRoaming.cs): the size radius of a placed town, a player town's own.
            float mill = p.Campaign.Target.Site >= 0 ? data.Sites[p.Campaign.Target.Site].Town.SizeRadius * data.Sites[p.Campaign.Target.Site].Town.TownRadiusMult : RadiusOf(world, p.Campaign.Target);
            squad.HomeRadius = leads && p.Campaign.Phase == 1 ? Math.Max(mill * 0.5f, 40) : 60;
            return;
        }
        // The column (task 215, Verified): followers jog to their trail points; the leader marching out keeps its loaded speed while every intact platoon is within
        // 6000, else it waits near the target or walks.
        var inst = p.Campaign;
        byte mode = SpeedMode.Run;
        // Inside 100 a follower takes the campaign leader's current speed, x 0.9 inside 70 (FUN_140349e70): its leader's mode and, as a cap, the leader's speed now.
        if (!leads && d < CampaignFollowDistance && world.Platoons.Find(inst.Leader) is { } head && world.Squads.Find(head.SquadId) is { } headSquad
            && table.TryResolveNext(headSquad.Leader, out int hs))
        {
            mode = table.Next[hs].Mode;
            var v = table.Next[hs].Velocity;
            cold.PaceCap = new Vector2(v.X, v.Z).Length() * (d < CampaignPaceDistance ? CampaignPaceFactor : 1);
        }
        if (leads)
        {
            mode = LoadedMode(CampaignOf(inst.CampaignId)?.TravelSpeedLoaded ?? MoveSpeed.Run, n.Mode);
            if (inst.Phase == 0 && !inst.Platoons.All(id => id == p.Id || world.Platoons.Find(id) is not { } q || !Intact(world, q) || Vector2.Distance(q.Position, p.Position) <= ColumnRange))
            {
                if (Vector2.Distance(p.Position, goal) <= WaitNearTarget)
                {
                    if (n.Task == (byte)CharacterTask.GoTo) TaskStart.Stand(table, slot);
                    return;
                }
                mode = SpeedMode.Walk;
            }
        }
        bool busy = n.Task == (byte)CharacterTask.GoTo && (n.Flags & (ushort)MoveFlags.AnyPath) != 0 && n.Mode == mode;
        p.OrderCooldown -= dt;
        if (busy || p.OrderCooldown > 0) return;
        TaskStart.Walk(world, slot, goal, mode, Meitou.Data.Gameplay.Ai.TaskType.TRAVEL_TO_TARGET_PACKAGE, TaskSource.Goal);
        p.OrderCooldown = 4;
    }

    // ---- state hash ----

    /// <summary>The war managers once any faction has started or queued a campaign, the kept wild squads while there are any, the towns' artifacts while any are left and the prosperity of the factions below their cap (so worlds without them hash as before).</summary>
    public void Hash(ref StateHasher h)
    {
        HashWars(ref h);
        HashArtifacts(ref h);
        if (prosperity is not null)
            for (int f = 0; f < prosperity.Length; f++)
                if (prosperity[f] < data.Factions[f].MaxProsperity)
                {
                    h.Add(f);
                    h.Add(prosperity[f]);
                }
        foreach (var (site, kept) in keptWild)
        {
            h.Add(site);
            foreach (var k in kept)
            {
                h.Add(Rng.StableHash(k.TemplateId));
                h.Add(k.Stored.Members.Count);
                foreach (var m in k.Stored.Members) { h.Add(m.Position.X); h.Add(m.Position.Z); }
            }
        }
    }

    void HashWars(ref StateHasher h)
    {
        if (wars is null || !wars.Any(m => m.NextId > 0 || m.Requests.Count > 0)) return;
        foreach (var m in wars)
        {
            h.Add(m.NextId);
            h.Add(m.UpdateTime);
            h.Add(m.Actives.Count);
            foreach (var a in m.Actives)
            {
                h.Add(a.Id);
                h.Add(a.Phase);
                h.Add(a.Reached);
                h.Add(a.Trig);
                h.Add(a.Leader);
                h.Add(a.Men);
                h.Add(a.Lost);
                h.Add(a.Announcer);
                h.Add(a.AnnouncedAt);
                h.Add(a.ArrivedAt);
                h.Add(a.Trail.Count);
                foreach (var t in a.Trail) h.Add(t);
                h.Add(a.Platoons.Count);
                foreach (int id in a.Platoons) h.Add(id);
                if (a.CarryStarts.Count > 0)   // only while a cannibal platoon's members carry, so other worlds hash as before
                    foreach (int id in a.Platoons)
                        if (a.CarryStarts.TryGetValue(id, out double carry)) { h.Add(id); h.Add(carry); }
            }
            h.Add(m.Requests.Count);
            foreach (var r in m.Requests)
            {
                h.Add(Rng.StableHash(r.CampaignId));
                h.Add(r.Due);
                h.Add(r.Tries);
            }
        }
    }
}

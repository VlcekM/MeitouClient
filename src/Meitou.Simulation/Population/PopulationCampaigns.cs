using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Population;

/// <summary>
/// A town a campaign aims at or comes from: a placed town (<see cref="Site"/>, a position in the population's sites) or a player town
/// (<see cref="PlayerTown"/>, its number).
/// </summary>
public readonly record struct CampaignTown(int Site, int PlayerTown)
{
    /// <summary>
    /// No town.
    /// </summary>
    public static readonly CampaignTown None = new(-1, 0);
    /// <summary>
    /// Whether this is no town.
    /// </summary>
    public bool IsNone => Site < 0 && PlayerTown <= 0;
    /// <summary>
    /// The placed town at site <paramref name="site"/>.
    /// </summary>
    public static CampaignTown OfSite(int site) => new(site, 0);
    /// <summary>
    /// The player town numbered <paramref name="number"/>.
    /// </summary>
    public static CampaignTown OfPlayer(int number) => new(-1, number);
}

/// <summary>
/// A running campaign (the original's <c>CampaignInstance</c>, docs/game/factions-squads-towns.md "Campaigns"). Changed in serial phases only.
/// </summary>
public sealed class CampaignInstance
{
    /// <summary>
    /// The faction whose war manager runs it (a position in the factions).
    /// </summary>
    public required int Faction { get; init; }
    /// <summary>
    /// <c>id c</c>: unique within its faction.
    /// </summary>
    public required int Id { get; init; }
    /// <summary>
    /// <c>dat c</c>: the FACTION_CAMPAIGN.
    /// </summary>
    public required string CampaignId { get; init; }
    /// <summary>
    /// <c>target c</c>: the town it aims at.
    /// </summary>
    public CampaignTown Target { get; set; }
    /// <summary>
    /// <c>enemy c</c>: the target's faction (-1: none).
    /// </summary>
    public int Enemy { get; set; } = -1;
    /// <summary>
    /// <c>home c</c>: the town it comes from.
    /// </summary>
    public CampaignTown Home { get; set; }
    /// <summary>
    /// 0 travel, 1 at the target, 2 ended.
    /// </summary>
    public int Phase { get; set; }
    /// <summary>
    /// The game hour the phase began (not saved: the phase starts again at load, as the original).
    /// </summary>
    public double PhaseStart { get; set; }
    /// <summary>
    /// <c>rd c</c>: the leader has been in the target town.
    /// </summary>
    public bool Reached { get; set; }
    /// <summary>
    /// <c>trig c</c>: a follow-up of this manager is queued.
    /// </summary>
    public bool Trig { get; set; }
    /// <summary>
    /// The leader platoon (-1: none).
    /// </summary>
    public int Leader { get; set; } = -1;
    /// <summary>
    /// Its platoons, in the order they joined.
    /// </summary>
    public List<int> Platoons { get; } = [];
    /// <summary>
    /// The characters it needs (int(<c>num forces</c> × <c>raidSizeMult</c>)) and those it gathered.
    /// </summary>
    public int Required { get; set; }
    /// <summary>
    /// The characters it gathered (see <see cref="Required"/>).
    /// </summary>
    public int Men { get; set; }
    /// <summary>
    /// The game hour its first loaded fight began in the first 4 hours of an assault's phase 1 (<c>FUN_1409c70f0</c>, <b>Verified</b>); -1: none yet,
    /// and at every phase change.
    /// </summary>
    public double FightStart { get; set; } = -1;
    /// <summary>
    /// Its leader platoon fought the player's characters (engine test, <c>PopulationCampaigns.cs</c>).
    /// </summary>
    public bool FoughtPlayer { get; set; }
    /// <summary>
    /// The leader's recorded trail, oldest first (the original's <c>GatherAndMove</c>): its followers' places. Not saved: laid again after a load.
    /// </summary>
    public List<Vector2> Trail { get; } = [];
    /// <summary>
    /// Its leader said the <c>dialog announcement</c> (the character, and the game hour; -1: not yet) and has reached the target. Not saved.
    /// </summary>
    public CharacterId Announcer { get; set; } = CharacterId.None;
    /// <summary>
    /// The game hour its leader said the announcement (-1: not yet; see <see cref="Announcer"/>).
    /// </summary>
    public double AnnouncedAt { get; set; } = -1;
    /// <summary>
    /// The game hour its leader reached the target (-1: not yet). Not saved.
    /// </summary>
    public double ArrivedAt { get; set; } = -1;
    /// <summary>
    /// The redirect timer (the original's +0xf0, started the first time the target is found gone or inactive; -1: not started). Not saved or hashed.
    /// </summary>
    public double RedirectStart { get; set; } = -1;
    /// <summary>
    /// Per platoon of a cannibal assault (Package_WarAssault_Cannibal, each platoon's own package instance): the game hour one of its members was
    /// first seen carrying someone; absent: nobody carries. Saved as <c>meitou carry start p&lt;i&gt;</c>
    /// (the original keeps the stamp in the package and saves none).
    /// </summary>
    public Dictionary<int, double> CarryStarts { get; } = [];
}

/// <summary>
/// A queued campaign (<c>req*</c> of the save): due at a game hour against a target.
/// </summary>
public sealed class CampaignRequest
{
    /// <summary>
    /// The FACTION_CAMPAIGN it starts.
    /// </summary>
    public required string CampaignId { get; init; }
    /// <summary>
    /// The target's faction (-1: none).
    /// </summary>
    public int Enemy { get; set; } = -1;
    /// <summary>
    /// The game hour it is due at.
    /// </summary>
    public double Due { get; set; }
    /// <summary>
    /// The town it aims at.
    /// </summary>
    public CampaignTown Target { get; set; }
    /// <summary>
    /// The town it comes from (none: the faction's town nearest the target).
    /// </summary>
    public CampaignTown Home { get; set; }
    /// <summary>
    /// The one counter (+0x58, <b>Verified</b> <c>FUN_1409cb310</c>; not saved) of the delays for a gone target and the starts tried:
    /// the request goes above 10 on a delay, above 100 on a start.
    /// </summary>
    public int Tries { get; set; }
}

/// <summary>
/// A campaign a faction knows (<c>poss*</c>): its last start, the <c>repeat limit</c> clock (0: never started).
/// </summary>
public sealed class KnownCampaign(string id)
{
    /// <summary>
    /// The FACTION_CAMPAIGN's id.
    /// </summary>
    public string Id { get; } = id;
    /// <summary>
    /// The game hour of its last start (0: never started).
    /// </summary>
    public double LastStart { get; set; }
}

/// <summary>
/// A faction's war manager (the original's <c>FactionWarMgr</c>): its next roll, next campaign id, running and queued campaigns
/// and the campaigns it knows.
/// </summary>
public sealed class WarManager
{
    /// <summary>
    /// The faction it runs for (a position in the factions).
    /// </summary>
    public required int Faction { get; init; }
    /// <summary>
    /// <c>updatetime</c>: the game hour of the next roll.
    /// </summary>
    public double UpdateTime { get; set; }
    /// <summary>
    /// <c>fwc id</c>.
    /// </summary>
    public int NextId { get; set; }
    /// <summary>
    /// The running campaigns.
    /// </summary>
    public List<CampaignInstance> Actives { get; } = [];
    /// <summary>
    /// The queued campaigns.
    /// </summary>
    public List<CampaignRequest> Requests { get; } = [];
    /// <summary>
    /// The campaigns the faction knows.
    /// </summary>
    public List<KnownCampaign> Known { get; } = [];
    /// <summary>
    /// Requests queued since the world began (keys their delay rolls; not saved).
    /// </summary>
    internal int Queued { get; set; }

    /// <summary>
    /// The known campaign <paramref name="id"/>, added when it is not known yet.
    /// </summary>
    public KnownCampaign Know(string id)
    {
        foreach (var k in Known)
            if (k.Id == id) return k;

        var added = new KnownCampaign(id);
        Known.Add(added);
        return added;
    }
}

/// <summary>
/// Faction campaigns (docs/game/factions-squads-towns.md "Campaigns", docs/simulation.md "Off-screen tier"): one war manager per faction,
/// one runs per tick at a wrapping cursor (<see cref="StepWarManager"/>; every rule reads the game clock): the running campaigns, then the
/// due requests, then the roll when the clock passes <c>updatetime</c>. Campaign squads are platoons kept while their campaign runs; off
/// screen they are moved here (the leader to the target, the others to the leader), loaded their leader is sent to the same goal. Rolls are
/// keyed by the world seed, the faction and the roll's hour, so a run repeats at any thread count.
/// </summary>
public sealed partial class PopulationSystem : IStateHashed
{
    #region Managers

    /// <summary>
    /// Each faction's war manager, by faction; null until <see cref="EnsureWars"/> makes them.
    /// </summary>
    WarManager[]? _wars;
    /// <summary>
    /// The campaign data, read on first use.
    /// </summary>
    CampaignDatabase? _campaignDb;

    /// <summary>
    /// Hours between rolls (<b>Verified</b>: +6 in the loader and the update).
    /// </summary>
    public const double RollEveryHours = 6;
    /// <summary>
    /// Spawn radius of a campaign squad round its town or leader (<b>Verified</b>, 700).
    /// </summary>
    public const float CampaignSpawnRadius = 700;
    /// <summary>
    /// Distance at which an unloaded follower stops behind its leader (engine choice).
    /// </summary>
    public const float CampaignFollowDistance = 100;
    /// <summary>
    /// Inside this distance of its trail point a follower goes at <see cref="CampaignPaceFactor"/> x the leader's speed (<b>Verified</b>, 70 and 0.9,
    /// <c>FUN_140349e70</c>).
    /// </summary>
    public const float CampaignPaceDistance = 70, CampaignPaceFactor = 0.9f;

    /// <summary>
    /// The war managers, one per faction in faction order (empty before the first look).
    /// </summary>
    public IReadOnlyList<WarManager> Wars => _wars ?? [];

    /// <summary>
    /// A FACTION_CAMPAIGN with its ancestors applied by the live inheritance switch (null: not one).
    /// </summary>
    public CampaignData? CampaignOf(string id) => (_campaignDb ??= new CampaignDatabase(_data.Db)).Find(id, !CampaignAncestorsOnce);

    /// <summary>
    /// The player's faction: the world's, else the data's.
    /// </summary>
    int PlayerFactionOf(World world) => world.Player.Faction >= 0 ? world.Player.Faction : _data.PlayerFaction;

    /// <summary>
    /// The managers, made once: each faction's <c>campaigns</c> list known, the first roll 6 h on; then a loaded save's war states over them.
    /// </summary>
    internal void EnsureWars(World world)
    {
        if (_wars is not null) return;

        _wars = new WarManager[_data.Factions.Count];
        for (int f = 0; f < _wars.Length; f++)
        {
            var m = _wars[f] = new WarManager { Faction = f, UpdateTime = world.GameHours + RollEveryHours };
            foreach (var link in _data.Factions[f].Campaigns)
                if (CampaignOf(link.Id) is not null) m.Know(link.Id);
        }

        if (world.Saved is { Wars.Count: > 0 } saved) Saving.WarSave.Read(world, this, saved);
    }

    /// <summary>
    /// The war manager of <paramref name="faction"/>; null before the managers are made or for no faction.
    /// </summary>
    internal WarManager? WarOf(int faction) => _wars is not null && faction >= 0 && faction < _wars.Length ? _wars[faction] : null;

    #endregion

    #region Prosperity

    /// <summary>
    /// Per second of game time, a faction's prosperity grows while below its cap (<b>Verified</b>, <c>FUN_1402dd680</c>: 0.1).
    /// </summary>
    const double ProsperityPerSecond = 0.1;
    /// <summary>
    /// What a unique's death and a damaged platoon's destroy take off its faction's prosperity (<b>Verified</b>, <c>FUN_1407a6200</c>,
    /// <c>FUN_1406bab60</c>: 100 and 10).
    /// </summary>
    const double UniqueDeathProsperity = 100, DamagedDespawnProsperity = 10;
    /// <summary>
    /// Each faction's prosperity (the FactionUniqueSquadManager's +8, saved as the faction's <c>prosperity</c>), by faction;
    /// null before the first need.
    /// </summary>
    double[]? _prosperity;
    /// <summary>
    /// The tick the prosperity last grew to.
    /// </summary>
    long _prosperityTick;

    /// <summary>
    /// The factions' prosperity, made on first use: the loaded save's value for a faction it names, else the faction's <c>max prosperity</c>
    /// (<b>Verified</b>, <c>FUN_1402ddc90</c> / <c>FUN_1402e0ab0</c> start the value at the cap).
    /// </summary>
    double[] EnsureProsperity(World world)
    {
        if (_prosperity is not null) return _prosperity;

        _prosperityTick = world.Tick;
        var values = new double[_data.Factions.Count];
        for (int f = 0; f < values.Length; f++)
            values[f] = world.Saved?.Prosperity.TryGetValue(_data.Factions[f].Id, out float saved) == true ? saved : _data.Factions[f].MaxProsperity;

        return _prosperity = values;
    }

    /// <summary>
    /// A faction's prosperity (0 for no faction).
    /// </summary>
    internal double ProsperityOf(World world, int faction) =>
        faction >= 0 && faction < _data.Factions.Count ? EnsureProsperity(world)[faction] : 0;

    /// <summary>
    /// Lowers a faction's prosperity, floored at 0 (<c>FUN_1407a6200</c>, <c>FUN_1406bab60</c>); the one place the drops are made.
    /// </summary>
    void LowerProsperity(World world, int faction, double by)
    {
        if (faction < 0 || faction >= _data.Factions.Count) return;

        var values = EnsureProsperity(world);
        values[faction] = Math.Max(0, values[faction] - by);
    }

    /// <summary>
    /// At each look: the uniques that died since the last (<see cref="UniqueCharacters.Died"/>) cost their faction 100, then every faction
    /// below its cap gains 0.1 per second of game time since the last look (<c>FUN_1402dd680</c>; no clamp to the cap in the add). The original's
    /// period is the faction's accumulated frame time, which nothing else reads (<b>Unknown</b>).
    /// </summary>
    void StepProsperity(World world)
    {
        var values = EnsureProsperity(world);
        foreach (int faction in world.Uniques.TakeFallen()) LowerProsperity(world, faction, UniqueDeathProsperity);

        double seconds = (world.Tick - _prosperityTick) * (double)world.TickSeconds;
        _prosperityTick = world.Tick;
        for (int f = 0; f < values.Length; f++)
            if (values[f] < _data.Factions[f].MaxProsperity) values[f] += seconds * ProsperityPerSecond;
    }

    #endregion

    #region Campaign steps

    /// <summary>
    /// The faction whose war manager runs next (the original's global cursor <c>DAT_14212e314</c>); not saved, hashed.
    /// </summary>
    int _warCursor;

    /// <summary>
    /// The campaign step: the factions' prosperity grows, the managers are made if needed and the campaigns move on by the check's game seconds.
    /// </summary>
    void StepCampaigns(World world)
    {
        StepProsperity(world);
        EnsureWars(world);
        MoveCampaigns(world, _settings.CheckEveryTicks * world.TickSeconds);
    }

    /// <summary>
    /// One war manager per tick, at a cursor that wraps over the factions (<c>FUN_1402e74f0</c>, <b>Verified</b>: every call adds its time to
    /// all factions, then runs the faction clock and the war manager <c>FUN_1409cb310</c> of the faction at the cursor only, and advances the
    /// cursor): the running campaigns, then the due requests, then the roll when the clock passes <c>updatetime</c>.
    /// </summary>
    void StepWarManager(World world)
    {
        EnsureWars(world);
        if (_wars!.Length == 0) return;

        if (_warCursor >= _wars.Length) _warCursor = 0;
        var m = _wars[_warCursor];
        _warCursor = (_warCursor + 1) % _wars.Length;

        double now = world.GameHours;
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

    #endregion

    #region Towns

    /// <summary>
    /// The centre of a campaign town (XZ): a site's centre or a player town's position; the origin when the player town is gone.
    /// </summary>
    Vector2 CentreOf(World world, CampaignTown t)
    {
        if (t.Site >= 0) return SiteCentre(t.Site);

        var town = world.Buildings.FindTown(t.PlayerTown);
        return town is null ? default : new Vector2(town.Position.X, town.Position.Z);
    }

    /// <summary>
    /// A town's radius (<c>Town::getRadius</c>, <b>Verified</b> <c>FUN_140926eb0</c>): a placed town's <see cref="TownData.Radius"/>
    /// (<c>town radius mult</c> × its type base); a player town's own (<see cref="Construction.PlayerTown.Radius"/>, 2500 when it is gone).
    /// </summary>
    float RadiusOf(World world, CampaignTown t)
    {
        if (t.Site >= 0) return _data.Sites[t.Site].Town.Radius;

        return world.Buildings.FindTown(t.PlayerTown)?.Radius ?? 2500;
    }

    /// <summary>
    /// The faction owning a campaign town (-1: none, or a player town that is gone).
    /// </summary>
    int FactionOfTown(World world, CampaignTown t) =>
        t.Site >= 0 ? _data.SiteFactions[t.Site] : world.Buildings.FindTown(t.PlayerTown)?.Owner ?? -1;

    /// <summary>
    /// A campaign town's name: the site's town name, the player town's, else the player faction's name.
    /// </summary>
    string NameOf(World world, CampaignTown t)
    {
        if (t.Site >= 0) return _data.Sites[t.Site].Town.Name;

        string? name = world.Buildings.FindTown(t.PlayerTown)?.Name;
        if (name is not null) return name;

        return PlayerFactionOf(world) is >= 0 and var pf ? _data.Factions[pf].Name : "";
    }

    /// <summary>
    /// Whether a campaign town is still there: a site in range, or a player town that stands.
    /// </summary>
    bool Exists(World world, CampaignTown t) => t.Site >= 0 ? t.Site < _data.Sites.Count : world.Buildings.FindTown(t.PlayerTown) is not null;

    /// <summary>
    /// The town a position is in (within its radius, XZ): placed towns in site order, then the player's;
    /// <see cref="CampaignTown.None"/> outside every town.
    /// </summary>
    CampaignTown TownAt(World world, Vector2 at)
    {
        foreach (var site in _data.Sites)
        {
            if (site.Town.Type is TownType.NestMarker or TownType.Null) continue;

            float r = site.Town.Radius;
            if (r > 0 && Vector2.DistanceSquared(at, SiteCentre(site.Index)) <= r * r)
                return CampaignTown.OfSite(site.Index);
        }

        foreach (var town in world.Buildings.Towns)
        {
            float r = RadiusOf(world, CampaignTown.OfPlayer(town.Number));
            if (Vector2.DistanceSquared(at, new Vector2(town.Position.X, town.Position.Z)) <= r * r)
                return CampaignTown.OfPlayer(town.Number);
        }

        return CampaignTown.None;
    }

    /// <summary>
    /// The faction's own town nearest a point (XZ; every placed town of the faction), none when it has no town.
    /// </summary>
    CampaignTown NearestOwnTown(int faction, Vector2 at)
    {
        int best = -1;
        float bestD = float.MaxValue;
        foreach (var site in _data.Sites)
        {
            if (_data.SiteFactions[site.Index] != faction) continue;

            float d = Vector2.DistanceSquared(at, SiteCentre(site.Index));
            if (d < bestD) (best, bestD) = (site.Index, d);
        }

        return best < 0 ? CampaignTown.None : CampaignTown.OfSite(best);
    }

    /// <summary>
    /// The BIOME_GROUP id at a point (null without a biome map).
    /// </summary>
    string? BiomeAt(Vector2 at) => _data.BiomeGroupAt(at);

    #endregion

    #region The roll

    /// <summary>
    /// The tick <see cref="_presentTowns"/> was gathered on (-1: never).
    /// </summary>
    long _presentTick = -1;
    /// <summary>
    /// The towns a player character is in, gathered once a tick for <see cref="TownActive"/>.
    /// </summary>
    readonly HashSet<CampaignTown> _presentTowns = [];

    /// <summary>
    /// The roll (<c>FUN_1409cb310</c>): each <c>territorial triggers</c> campaign the faction knows with a target passes with its chance;
    /// one passer is drawn by chance and started. <paramref name="at"/> is the roll's hour (the stored <c>updatetime</c>), which keys the rolls.
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
        {
            if ((pick -= p.Chance) < 0)
            {
                chosen = p;
                break;
            }
        }

        Start(world, m, chosen.C, chosen.Target, chosen.Home, now);
    }

    /// <summary>
    /// The player's living characters (the population gate counts them).
    /// </summary>
    static int PlayerLiving(World world)
    {
        int n = 0;
        var prev = world.Characters.Previous;
        for (int i = 0; i < prev.Length; i++)
            if (prev[i].Alive && world.Characters.Cold(i) is { IsPlayer: true, DiedTick: < 0 }) n++;

        return n;
    }

    /// <summary>
    /// A roll's chance (<c>CampaignData::vfunc_1</c>), 0 on any failure: against a player town, the player's living characters ≥
    /// <c>target population min</c> and the town's level (<see cref="TownLevel"/>, Town +0x388) ≥ <c>target tech level &gt;=</c>;
    /// the <c>repeat limit</c> passed; world states and leader; then the distance falloff from the faction's nearest town.
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

        return c.RollChance(Vector2.Distance(at, CentreOf(world, nearest)), _settings.RaidFrequencyMult);
    }

    /// <summary>
    /// The world-state gate (<c>vfunc_2</c>): every <c>world state</c> has its value, and there is no <c>special leader</c> or one of them is alive
    /// (an untracked one counts as alive).
    /// </summary>
    bool WorldStatesPass(World world, CampaignData c)
    {
        foreach (var (state, value) in c.WorldStates)
            if (!IWorldStates.Passes(States, _data.Db, state, value ? 1 : 0)) return false;

        if (c.SpecialLeaders.Count == 0) return true;

        foreach (var leader in c.SpecialLeaders)
            if (world.Uniques.StateOf(leader) == UniqueState.Alive) return true;

        return false;
    }

    /// <summary>
    /// A target and home (<c>CampaignData::vfunc_0</c>): the towns of <paramref name="enemy"/> (the player's for rolls) in the faction's
    /// <c>biomes</c>, else all of them, less those not active (<see cref="TownActive"/>, Town slot 70), those with hostile occupiers when the
    /// campaign is not hostile (Town +0x128, <see cref="Ai.AiSystem.Occupied"/>), and those in its <c>no-go zones</c> unless the campaign ignores
    /// them; then the first source town in site order with one of its 5 nearest candidates within <c>range far</c> is the home, that candidate the
    /// target.
    /// </summary>
    bool FindTarget(World world, int faction, CampaignData c, int enemy, out CampaignTown target, out CampaignTown home)
    {
        target = home = CampaignTown.None;
        var candidates = ActiveCandidates(world, faction, c, EnemyTowns(world, enemy));
        if (candidates.Count == 0) return false;

        bool strict = _data.Factions[faction].BooksBounties;
        foreach (var site in _data.Sites)
        {
            if (_data.SiteFactions[site.Index] != faction) continue;
            if (site.Town.Type is TownType.NestMarker or TownType.Null) continue;
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
    /// The towns of <paramref name="enemy"/>: the player's own towns when it is the player's faction, else the faction's placed towns (nest markers
    /// and nulls left out).
    /// </summary>
    List<CampaignTown> EnemyTowns(World world, int enemy)
    {
        var all = new List<CampaignTown>();
        if (enemy == PlayerFactionOf(world))
        {
            foreach (var town in world.Buildings.Towns)
                if (town.Owner == enemy) all.Add(CampaignTown.OfPlayer(town.Number));
        }
        else
        {
            foreach (var site in _data.Sites)
            {
                if (_data.SiteFactions[site.Index] != enemy) continue;
                if (site.Town.Type is TownType.NestMarker or TownType.Null) continue;

                all.Add(CampaignTown.OfSite(site.Index));
            }
        }

        return all;
    }

    /// <summary>
    /// The candidates among <paramref name="all"/>: those in the faction's biomes (all of them when none is), less the inactive ones, the occupied
    /// ones when the campaign is not hostile, and the no-go zones unless the campaign ignores them.
    /// </summary>
    List<CampaignTown> ActiveCandidates(World world, int faction, CampaignData c, List<CampaignTown> all)
    {
        if (all.Count == 0) return all;

        var fd = _data.Factions[faction];
        var candidates = all.Where(t => BiomeAt(CentreOf(world, t)) is { } b && fd.Biomes.Any(x => x.Id == b)).ToList();
        if (candidates.Count == 0) candidates = all;

        candidates.RemoveAll(t => !TownActive(world, t) || !c.IsHostile && AiSystem.Occupied(world, t));
        if (!c.IgnoresNogoZones && fd.NoGoZones.Count > 0)
            candidates.RemoveAll(t => BiomeAt(CentreOf(world, t)) is { } b && fd.NoGoZones.Any(x => x.Id == b));

        return candidates;
    }

    /// <summary>
    /// Whether a town is active (Town vtable slot 70, <c>FUN_140929eb0</c>, <b>Verified</b>): a zone of it is loaded, and for a town of the player's
    /// faction a member of the faction's platoons has it as its current town (<c>Character</c> slot 15, the AI's rule
    /// <see cref="Ai.AiSystem.TownAt"/>: the nearest town whose radius holds it, so a nearer town takes precedence). So raids and redirects aim only
    /// at a player town the player is at. The loaded-zone test of a player town is not made: the member standing in it keeps its own zone loaded
    /// (the town's zone list is not modelled).
    /// </summary>
    bool TownActive(World world, CampaignTown t)
    {
        if (t.Site >= 0)
        {
            if (!_active.Contains(_data.Sites[t.Site].Zone)) return false;
            if (PlayerFactionOf(world) is not (>= 0 and var player) || _data.SiteFactions[t.Site] != player)
                return true;
        }

        if (_presentTick != world.Tick)
        {
            // The towns the player's characters are in, worked out once a tick (serial).
            _presentTick = world.Tick;
            _presentTowns.Clear();
            var prev = world.Characters.Previous;
            for (int i = 0; i < prev.Length; i++)
            {
                if (!prev[i].Alive || world.Characters.Cold(i) is not { IsPlayer: true }) continue;
                if (AiSystem.TownAt(_data, world, prev[i].Position) is { IsNone: false } here) _presentTowns.Add(here);
            }
        }

        return _presentTowns.Contains(t);
    }

    /// <summary>
    /// A player town's level (Town +0x388, the save's <c>plevel</c>; <c>FUN_14048c200</c>, Verified by code, recomputed by the original when towns
    /// merge and at other calls): over its finished buildings, H = those with an interior that are neither a part nor a gate, T = turrets (function
    /// 12), G = gates (21), R = the highest research <c>tech level</c> (function 3). 1 when R &gt; 1, H &gt; 1, G &gt; 0 or T &gt; 3; with a gate, 2
    /// when R &gt; 3, H &gt; 4 or T &gt; 4, and 3 when R &gt; 3, H &gt; 4 and T &gt; 6. Engine readings: "an interior" is the BUILDING's
    /// <c>interior</c> list (the original's Building +0x1f0), a part is a site with a parent.
    /// </summary>
    internal int TownLevel(World world, int number)
    {
        int houses = 0, turrets = 0, gates = 0, research = 0;
        foreach (var b in world.Buildings.All)
        {
            if (b.Town != number || !b.Complete || b.Destroyed || b.Ruined) continue;
            if (_data.Db.Find(b.Record) is not { } r) continue;

            var f = r.GetReferences("functionality") is [var link, ..] ? _data.Db.Find(link.TargetStringId) : null;
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

    #endregion

    #region Start and squads

    /// <summary>
    /// What <see cref="Start"/> did with a campaign.
    /// </summary>
    enum StartResult
    {
        /// <summary>
        /// The campaign started.
        /// </summary>
        Started,
        /// <summary>
        /// It gathered too few men and was cancelled.
        /// </summary>
        Weak,
        /// <summary>
        /// It has no home town, or its target is gone.
        /// </summary>
        NoHome
    }

    /// <summary>
    /// Starts a campaign (<c>FUN_1409c9830</c>): a home (the given one, else the faction's town nearest the target), the next id, its squads
    /// gathered; too few men cancels it, else the messages, the last start and the <c>trigger player ally</c> requests.
    /// </summary>
    StartResult Start(World world, WarManager m, CampaignData c, CampaignTown target, CampaignTown home, double now)
    {
        if (home.IsNone || !Exists(world, home)) home = NearestOwnTown(m.Faction, CentreOf(world, target));
        if (home.IsNone || !Exists(world, target)) return StartResult.NoHome;

        var inst = new CampaignInstance
        {
            Faction = m.Faction,
            Id = m.NextId++,
            CampaignId = c.Id,
            Target = target,
            Home = home,
            Enemy = FactionOfTown(world, target),
            PhaseStart = now,
            Required = (int)(c.NumForces * _settings.RaidSizeMult),
        };
        int player = PlayerFactionOf(world);
        var made = new List<Platoon>();
        Gather(world, m, c, inst, made);
        if (inst.Men <= inst.Required)
        {
            if (inst.Enemy == player && player >= 0) world.Post(Loc.Tr("Assault to {1} canceled due to faction weakness", c.DisplayName));
            Disband(world, inst, made);
            Log($"{_data.Factions[m.Faction].Name}: {c.Name} against {NameOf(world, target)} canceled ({inst.Men} of {inst.Required} men)");
            return StartResult.Weak;
        }

        m.Actives.Add(inst);
        if (inst.Enemy == player && player >= 0)
        {
            string targetName = NameOf(world, target);
            world.Post(c.IsHostile
                ? Loc.Tr("Warning: {1} moving towards {2}", c.DisplayName, targetName)
                : Loc.Tr("{1} on the way to {2}", c.DisplayName, targetName));
        }

        m.Know(c.Id).LastStart = now;
        string factionName = _data.Factions[m.Faction].Name;
        string homeName = NameOf(world, home);
        Log($"{factionName}: {c.Name} against {NameOf(world, target)} from {homeName}, {inst.Platoons.Count} squad(s), {inst.Men} men");
        foreach (var allyId in c.TriggerPlayerAlly)
        {
            if (CampaignOf(allyId) is not { FactionOverride: { } fo } ally || _data.FactionIndex(fo) is not (>= 0 and var af) || player < 0) continue;
            if (_data.Relations.IsAlly(af, player)) Queue(world, m, ally, target, CampaignTown.None, inst.Enemy, 0, 0, now, callerFlag: false);
        }

        return StartResult.Started;
    }

    /// <summary>
    /// Adds a line to the town log, keeping the last 256.
    /// </summary>
    void Log(string line)
    {
        if (_townLog.Count >= 256) _townLog.RemoveAt(0);
        _townLog.Add(line);
    }

    /// <summary>
    /// The squads (<c>uses_leader</c>), until the men exceed the need: the <c>special leader</c>'s platoon (joined whatever it is doing; or the
    /// template naming it leader, made near the faction's town nearest the target); then for each of the 4 own towns nearest the target its free
    /// roaming platoons, then new squads from <c>squads to use</c> by weight within 700 of the town (of the leader once there is one), up to 100 per
    /// town. Engine choices: homeless platoons and a town's own (residents, bar squads) are not drafted.
    /// </summary>
    void Gather(World world, WarManager m, CampaignData c, CampaignInstance inst, List<Platoon> made)
    {
        var targetAt = CentreOf(world, inst.Target);
        bool Full() => inst.Men > inst.Required;
        foreach (var leader in c.SpecialLeaders)
        {
            if (FindUniquePlatoon(world, leader) is { } p)
            {
                // No eligibility test (FUN_1409c4720 only returns when it is already in this campaign): the old campaign drops it at its next update.
                if (p.Campaign != inst) Join(world, inst, p, leads: true);
                break;
            }

            if (world.Uniques.IsClaimed(leader) || LeaderTemplate(leader) is not { } template) continue;

            var town = NearestOwnTown(m.Faction, targetAt);
            if (town.IsNone || NewSquad(world, inst, template, town.Site, CentreOf(world, town), made) is not { } spawned) continue;

            Join(world, inst, spawned, leads: true);
            break;
        }

        if (Full()) return;

        var towns = _data.Sites.Where(s => _data.SiteFactions[s.Index] == m.Faction)
            .OrderBy(s => Vector2.DistanceSquared(targetAt, SiteCentre(s.Index))).ThenBy(s => s.Index).Take(4).ToList();
        var weights = new List<(string Id, float Weight)>();
        foreach (var e in c.SquadsToUse)
            if (e.V0 > 0 && SquadFactory.IsTemplate(_data.Db.Find(e.Id))) weights.Add((e.Id, e.V0));

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
                DraftNewSquad(world, m, c, inst, town.Index, weights, total, made);

            if (Full()) return;
        }
    }

    /// <summary>
    /// One new squad of a town's draft: a template drawn by weight from <paramref name="weights"/> (the draw keyed by the platoon id it would take),
    /// spawned round the leader once there is one, else round the town, then joined as a follower.
    /// </summary>
    void DraftNewSquad(World world, WarManager m, CampaignData c, CampaignInstance inst, int town, List<(string Id, float Weight)> weights,
        float total, List<Platoon> made)
    {
        int next = world.Platoons.NextId();
        float pick = Rng.Float(Rng.Hash(world.Seed, (ulong)m.Faction << 32 | (uint)next, RngPurpose.Campaign, SeedSalts.CampaignTemplate)) * total;
        string chosen = weights[^1].Id;
        foreach (var (id, w) in weights)
        {
            if ((pick -= w) < 0)
            {
                chosen = id;
                break;
            }
        }

        var around = inst.Leader >= 0 && world.Platoons.Find(inst.Leader) is { } lead ? lead.Position : SiteCentre(town);
        var squadTemplate = SquadTemplate.From(_data.Db.Find(chosen)!);
        if (NewSquad(world, inst, squadTemplate, town, around, made, next) is { } p) Join(world, inst, p, leads: false);
    }

    /// <summary>
    /// A platoon a campaign may take: not in one, of the faction, a roaming one (not a town's own nor wild), someone alive, of a <c>squads to use</c>
    /// template when the list is set.
    /// </summary>
    bool Free(Platoon p, int faction, CampaignData? c) =>
        p.Campaign is null && !p.Pinned && !p.Wild && p.Origin >= 0 && p.Faction == faction
        && p.State != PlatoonState.Activating && !p.Regenerate
        && (c is null || c.SquadsToUse.Count == 0 || c.SquadsToUse.Any(e => e.Id == p.TemplateId));

    /// <summary>
    /// The platoon holding a unique character: in the world (its handle), else stored or saved in a platoon; null when none holds it.
    /// </summary>
    Platoon? FindUniquePlatoon(World world, string recordId)
    {
        if (world.Uniques.Find(recordId) is { Handle.IsNone: false } entry
            && world.Characters.TryResolveNext(entry.Handle, out int slot)
            && world.Characters.Cold(slot) is { } cold
            && world.Squads.Find(cold.SquadId) is { PlatoonId: >= 0 } squad)
            return world.Platoons.Find(squad.PlatoonId);

        foreach (var p in world.Platoons.All)
        {
            if (p.Stored is { } stored && stored.Members.Any(m => m.Cold.RecordId == recordId && m.Cold.Medical is not { Dead: true }))
                return p;

            if (p.Saved is { } saved && saved.Members.Any(m => !m.Dead && !m.Gone && m.Source.RecordId == recordId))
                return p;
        }

        return null;
    }

    /// <summary>
    /// The squad template whose <c>leader</c> is the character (the first in the data's order; null: none).
    /// </summary>
    SquadTemplate? LeaderTemplate(string recordId)
    {
        foreach (var r in _data.Db.OfType(FcsRecordType.SquadTemplate).Concat(_data.Db.OfType(FcsRecordType.UniqueSquadTemplate)))
        {
            if (r.GetReferences("leader") is { Count: > 0 } l && l[0].TargetStringId == recordId)
                return SquadTemplate.From(r);
        }

        return null;
    }

    /// <summary>
    /// A new campaign squad: an unloaded platoon of the template at a walkable point within 700 of <paramref name="around"/>, homed at the town
    /// (null when it plans no one).
    /// </summary>
    Platoon? NewSquad(World world, CampaignInstance inst, SquadTemplate template, int town, Vector2 around, List<Platoon> made, int id = -1)
    {
        if (id < 0) id = world.Platoons.NextId();
        ulong key = Rng.Mix(SeedSalts.CampaignSquad ^ (ulong)id << 8 ^ (ulong)inst.Faction << 40);
        var plan = SquadFactory.Plan(_data.Db, template, _settings.SquadSizeMultiplier, world.Seed, key, States);
        if (plan.TotalMembers == 0) return null;

        var at = around;
        for (int k = 0; k < 8; k++)
        {
            ulong placeKey = Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignPlace + (ulong)k);
            var spot = Rng.PointInDisc(placeKey, around, CampaignSpawnRadius);
            if (world.Walkability.IsWalkable(spot.X, spot.Y))
            {
                at = spot;
                break;
            }
        }

        var (package, job) = PlatoonJobs.For(_data.Ai, template);
        var p = new Platoon
        {
            Id = id,
            Origin = town,
            TemplateId = template.Id,
            Faction = inst.Faction,
            Key = key,
            Size = plan.TotalMembers,
            Position = at,
            Package = package?.Id,
            Job = job,
            Target = town,
        };
        world.Platoons.Add(p);
        made.Add(p);
        return p;
    }

    /// <summary>
    /// Adds a platoon to a campaign: it drops its own wait and point, its living members count to the campaign's men, and it leads when asked to or
    /// when the campaign has no leader.
    /// </summary>
    void Join(World world, CampaignInstance inst, Platoon p, bool leads)
    {
        p.Campaign = inst;
        p.WaitLeft = 0;
        p.Point = null;
        inst.Platoons.Add(p.Id);
        inst.Men += LivingOf(world, p);
        if (leads || inst.Leader < 0) inst.Leader = p.Id;
    }

    /// <summary>
    /// A platoon's living members: in the world, stored, saved, or (never made) its size.
    /// </summary>
    internal int LivingOf(World world, Platoon p)
    {
        if (p.State == PlatoonState.Loaded && world.Squads.Find(p.SquadId) is { } squad) return squad.Members.Count(m => Living(world, m));
        if (p.Stored is { Members.Count: > 0 } stored) return stored.Members.Count(m => m.Cold.Medical is not { Dead: true });

        // A save's never-made platoon: its `char count`, as the original's pool counts a platoon that is not active (FUN_1408f6a40).
        if (p.Unmade) return p.Size;
        if (p.Saved is { } saved) return saved.Members.Count(m => !m.Dead && !m.Gone);
        return p.Size;
    }

    /// <summary>
    /// A cancelled start: its new squads go, the drafted ones are free again.
    /// </summary>
    void Disband(World world, CampaignInstance inst, List<Platoon> made)
    {
        foreach (var p in made)
            if (world.Platoons.Find(p.Id) is not null) Despawn(world, p);

        foreach (int id in inst.Platoons)
            if (world.Platoons.Find(id) is { } p) Release(world, p);
    }

    /// <summary>
    /// A platoon leaves its campaign: a roaming squad again, on its way home (Faithful: the normal despawn rules apply from now).
    /// </summary>
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
        if (p.Package is { } package) p.Job = PlatoonJobs.JobOf(_data.Ai.Package(package)?.Unloaded ?? 0);
        if (p.State == PlatoonState.Loaded && world.Squads.Find(p.SquadId) is { } squad) OrderLeader(world, p, squad);
    }

    #endregion

    #region Requests

    /// <summary>
    /// Queues a campaign (<c>FUN_1409c9410</c>): handed to its <c>faction override</c> (home cleared); home missing → the receiving faction's town
    /// nearest the target, none → dropped; dropped when the same campaign and target is queued, or (without <paramref name="callerFlag"/>, which a
    /// moved request loses) running; queued by the faction itself, its campaigns are walked in order: dropped at the first with <c>trig</c>, each
    /// before it having got <c>trig</c> set, else all have it. Due after uniform[<paramref name="minHours"/>, <paramref name="maxHours"/>) hours.
    /// Returns whether it was queued.
    /// </summary>
    bool Queue(World world, WarManager from, CampaignData c, CampaignTown target, CampaignTown home, int enemy, float minHours, float maxHours,
        double now, bool callerFlag)
    {
        var to = from;
        if (c.FactionOverride is { } fo && _data.FactionIndex(fo) is >= 0 and var fi && fi != from.Faction && WarOf(fi) is { } other)
        {
            to = other;
            home = CampaignTown.None;
            callerFlag = false;   // a moved request is always tested against the target faction's running campaigns
        }

        if (home.IsNone || !Exists(world, home)) home = NearestOwnTown(to.Faction, CentreOf(world, target));
        if (home.IsNone) return false;
        if (to.Requests.Any(r => r.CampaignId == c.Id && r.Target == target)) return false;
        if (!callerFlag && to.Actives.Any(a => a.CampaignId == c.Id && a.Target == target)) return false;
        if (to == from)
        {
            // In list order: the request is dropped at the first campaign already marked, the ones before it keep the mark set here.
            foreach (var a in from.Actives)
            {
                if (a.Trig) return false;
                a.Trig = true;
            }
        }

        ulong idKey = Rng.StableHash(c.Id) ^ SeedSalts.CampaignDelay;
        ulong roll = Rng.Hash(world.Seed, (ulong)to.Faction << 32 | (uint)to.Queued++, RngPurpose.Campaign, idKey);
        double due = now + minHours + Rng.Float(roll) * Math.Max(0, maxHours - minHours);
        to.Requests.Add(new CampaignRequest { CampaignId = c.Id, Enemy = enemy, Due = due, Target = target, Home = home });
        to.Know(c.Id);
        return true;
    }

    /// <summary>
    /// Dialogue's DA 37 ASSAULT_PHASE / DA 38 RETREAT_PHASE (<c>FUN_140680560</c>, <b>Verified</b>): the campaign the speaker's squad marches in goes
    /// to phase 1 (2 when it has no <c>pt2</c> leader package) or 2, its phase clock restarting. DA 39 (phase 3, which no phase handles; the data
    /// never uses it) is not built. Returns whether a campaign changed.
    /// </summary>
    public bool SetCampaignPhase(World world, CharacterId speaker, bool assault)
    {
        if (!world.Characters.TryResolveNext(speaker, out int s) || world.Characters.Cold(s) is not { SquadId: >= 0 } sc) return false;

        var loaded = world.Platoons.All.FirstOrDefault(p => p.SquadId == sc.SquadId && p.State == PlatoonState.Loaded && p.Campaign is not null);
        if (loaded?.Campaign is not { Phase: not 2 } inst || CampaignOf(inst.CampaignId) is not { } c) return false;

        int phase = assault && c.HasPt2 ? 1 : 2;
        if (inst.Phase == phase) return false;

        inst.Phase = phase;
        inst.PhaseStart = world.GameHours;
        inst.FightStart = -1;
        return true;
    }

    /// <summary>
    /// A DIALOGUE_LINE's <c>trigger campaign</c> list (<c>FUN_140680560</c>, docs/game/dialogue.md "Effects"), said by <paramref name="speaker"/>
    /// to <paramref name="target"/>: nothing when the speaker's squad is in a campaign; the target town is the player-faction town nearest the
    /// speaker when the target is of the player's faction (none: nothing, so a homeless player gets no dialogue campaign), else the town of the
    /// target's platoon (none: nothing), and the speaker's own current town when the line has no resolvable target (<c>FUN_140680560</c>,
    /// <b>Verified</b>). One weighted draw queues an entry for the speaker's faction, due after uniform[V0, V1) hours.
    /// Returns whether one was queued.
    /// </summary>
    public bool TriggerFromDialogue(World world, CharacterId speaker, CharacterId target, IReadOnlyList<CampaignLink> list)
    {
        var table = world.Characters;
        if (list.Count == 0 || !table.TryResolveNext(speaker, out int s) || table.Cold(s) is not { } sc) return false;

        bool inCampaign = sc.SquadId >= 0
            && world.Platoons.All.Any(p => p.SquadId == sc.SquadId && p.State == PlatoonState.Loaded && p.Campaign is not null);
        if (inCampaign) return false;

        EnsureWars(world);
        if (WarOf(sc.Faction) is not { } m) return false;

        var at = new Vector2(table.Next[s].Position.X, table.Next[s].Position.Z);
        var tc = table.TryResolveNext(target, out int t) ? table.Cold(t) : null;

        // Without a (resolvable) target the town is the speaker's current one; a resolved target replaces it, also with none
        // (a platoon without an origin).
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
        else if (tc is { SquadId: >= 0 }
            && world.Platoons.All.FirstOrDefault(p => p.SquadId == tc.SquadId && p.State == PlatoonState.Loaded) is { Origin: >= 0 } tp)
        {
            town = CampaignTown.OfSite(tp.Origin);
        }

        if (town.IsNone || !Exists(world, town)) return false;

        ulong key = (ulong)speaker.Slot << 32 | (uint)world.Tick;
        float r = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignDialogue)) * 100;
        foreach (var e in list)
        {
            if ((r -= e.V2) >= 0) continue;
            if (CampaignOf(e.Id) is not { } c) return false;

            ulong delayRoll = Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignDialogue + 1);
            float delay = e.V0 + Rng.Float(delayRoll) * Math.Max(0, e.V1 - e.V0);
            return Queue(world, m, c, town, CampaignTown.None, FactionOfTown(world, town), delay, delay, world.GameHours, callerFlag: true);
        }

        return false;
    }

    /// <summary>
    /// The due requests (<c>FUN_1409cb310</c>'s request pass): a gone target (missing, or a player town occupied for a non-hostile campaign or not
    /// active) is redirected, the start waiting for the next run, else delayed 5-10 h; a failed world-state or leader gate drops it; else a start,
    /// tried each run (no chance, cooldown, range or population gate). Delays and starts share one counter
    /// (<see cref="CampaignRequest.Tries"/>): dropped when a delay takes it above 10, or a failed start above 100. A removal ends the run
    /// (**Verified**: the original breaks out of its loop after each delete).
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

            // Gone: no such town, or a player town that is occupied (for a non-hostile campaign) or not active.
            // A redirected request starts at the next run.
            if (!Exists(world, r.Target)
                || r.Target.PlayerTown > 0 && (!c.IsHostile && AiSystem.Occupied(world, r.Target) || !TownActive(world, r.Target)))
            {
                if (FindTarget(world, m.Faction, c, r.Enemy, out var retarget, out _))
                {
                    r.Target = retarget;
                    continue;
                }

                ulong dueKey = BitConverter.DoubleToUInt64Bits(r.Due) ^ SeedSalts.CampaignRetry;
                ulong roll = Rng.Hash(world.Seed, (ulong)m.Faction << 32 | (uint)i, RngPurpose.Campaign, dueKey);
                r.Due += 5 + Rng.Float(roll) * 5;
                if (++r.Tries > 10)
                {
                    m.Requests.RemoveAt(i);   // a removal ends the run (the original's loop breaks after deleting)
                    break;
                }

                continue;
            }

            if (!WorldStatesPass(world, c))
            {
                m.Requests.RemoveAt(i);
                break;
            }

            // A start only appends requests (the ally help), so i still points at r.
            var result = Start(world, m, c, r.Target, r.Home, now);
            if (result != StartResult.Weak || ++r.Tries > 100)
            {
                m.Requests.RemoveAt(i);
                break;
            }
        }
    }

    #endregion

    #region Running campaigns

    /// <summary>
    /// The redirect timer's period in game hours (<b>Verified</b>, 0.2 in <c>FUN_1409d62f0</c>).
    /// </summary>
    const double RedirectHours = 0.2;

    /// <summary>
    /// The redirect (<c>FUN_1409d62f0</c>, docs/game/factions-squads-towns.md "Campaigns"): a running campaign whose target town is gone, or is a
    /// player town that is not active (<see cref="TownActive"/>, Town slot 70: the player has left it), looks for another every 0.2 game hours (the
    /// timer starts when it is first found so, and restarts after each look, the original's +0xf0); a different target found replaces it with "{1}
    /// has redirected to {2}". The home stays. None found keeps the old target.
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
    /// One campaign's update (<c>CampaignInstance::vfunc_10</c>); true when it is to be removed. Phase 0: the leader within 0.8 × the town's radius
    /// arrives ("{1} has arrived at {2}"), then phase 1, or 2 without victory or defeat when there is no <c>pt2</c>. Phase 1: the leader back in its
    /// home town wins; else by <c>key</c> (assault 4 h or 0.2 h after its first loaded fight; defend 12 h with no hostile campaign against the
    /// player; trader visit 1 h); then fewer than 20 % intact platoons lose. Any phase but 2: the leader platoon loaded with everyone down loses.
    /// Phase 2: removed after 3 h.
    /// </summary>
    bool UpdateCampaign(World world, WarManager m, CampaignInstance inst, double now)
    {
        if (CampaignOf(inst.CampaignId) is not { } c)
        {
            foreach (int id in inst.Platoons)
                if (world.Platoons.Find(id) is { } gone) Release(world, gone);

            return true;
        }

        inst.Platoons.RemoveAll(id => world.Platoons.Find(id) is not { } p || p.Campaign != inst);
        if (inst.Leader < 0 || !inst.Platoons.Contains(inst.Leader)) inst.Leader = inst.Platoons.Count > 0 ? inst.Platoons[0] : -1;

        double hours = now - inst.PhaseStart;
        if (inst.Phase == 2) return RemoveEnded(world, inst, hours);

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
        TrackFights(world, inst);
        if (leader.State == PlatoonState.Loaded && AllDown(world, leader))
        {
            Defeat(world, m, c, inst, now);
            return false;
        }

        if (inst.Phase == 0) return UpdateArrival(world, m, c, inst, leader, now);
        if (inst.Phase == 1) return UpdateAssault(world, m, c, inst, leaderTown, hours, now);
        return false;
    }

    /// <summary>
    /// A campaign that has ended (phase 2) is removed once 3 hours have passed, its platoons released; true when it is removed.
    /// </summary>
    bool RemoveEnded(World world, CampaignInstance inst, double hours)
    {
        if (hours <= 3) return false;

        foreach (int id in inst.Platoons)
            if (world.Platoons.Find(id) is { } p) Release(world, p);

        inst.Platoons.Clear();
        return true;
    }

    /// <summary>
    /// Phase 0 of <see cref="UpdateCampaign"/>: a loaded campaign at its target is defeated when the town has hostile occupiers, else the leader's
    /// arrival is noted, its announcement waited for, and the campaign moves on to phase 1 (or 2 without a <c>pt2</c>).
    /// </summary>
    bool UpdateArrival(World world, WarManager m, CampaignData c, CampaignInstance inst, Platoon leader, double now)
    {
        // A loaded campaign that is neither hostile nor a defence is defeated at once within 1.8 × the radius of a town with hostile occupiers
        // (Town +0x128).
        if (leader.State == PlatoonState.Loaded && !c.IsHostile && c.Key != CampaignKey.DefendTown
            && OccupiedNear(world, inst.Target, leader.Position))
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

        // With an announcement, a loaded leader waits for it, then while its negotiation is pending (engine choice for the Unknown event-2 state: up
        // to AnnouncementPatience hours from arriving); off screen nobody can hear it, so it goes on.
        if (c.DialogAnnouncement is not null && leader.State == PlatoonState.Loaded && Announcer is not null
            && now - inst.ArrivedAt < AnnouncementPatience
            && (inst.AnnouncedAt < 0 || PendingTalk?.Invoke(inst.Announcer) == true || AnnouncementHeard(world, inst, leader.Position)))
            return false;

        inst.Phase = c.HasPt2 ? 1 : 2;
        inst.PhaseStart = now;
        inst.FightStart = -1;
        return false;
    }

    /// <summary>
    /// Phase 1 of <see cref="UpdateCampaign"/>: the leader back in the home town wins, a cannibal retreat ends the phase, else the campaign's key
    /// decides the win, and too few intact platoons lose it.
    /// </summary>
    bool UpdateAssault(World world, WarManager m, CampaignData c, CampaignInstance inst, CampaignTown leaderTown, double hours, double now)
    {
        if (leaderTown == inst.Home)
        {
            Victory(world, m, c, inst, now);
            return false;
        }

        // The cannibals' own rule (package class 32, Observed): the retreat starts when three of them carry someone or 15 game minutes after the
        // first did.
        if (c.LeaderPackages[1] is { } pt2 && _data.Ai.Package(pt2) is { Signal: 32 } && CannibalsLoaded(world, inst, now))
        {
            inst.Phase = 2;
            inst.PhaseStart = now;
            inst.FightStart = -1;
            return false;
        }

        bool won = false;
        switch (c.Key)
        {
            case CampaignKey.AssaultTown:
                if (hours > 4) won = true;
                else if (PlayersHold(world, inst.Target)) return false;
                else if (inst.FightStart < 0)
                {
                    if (AnyFighting(world, inst)) inst.FightStart = now;   // armed only past the players' hold test, FUN_1409c70f0
                }
                else if (now - inst.FightStart > 0.2 && (!AnyFighting(world, inst) || !PlayersStanding(world, inst.Target))
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
        if (inst.Platoons.Count == 0 || intact < 0.2f * inst.Platoons.Count) Defeat(world, m, c, inst, now);
        return false;
    }

    /// <summary>
    /// Whether a point is within 1.8 × the radius of the campaign's own target when it has hostile occupiers (<c>FUN_1409c70f0</c> phase 0,
    /// <b>Verified</b>: no other town is looked at; <see cref="Ai.AiSystem.Occupied"/>).
    /// </summary>
    bool OccupiedNear(World world, CampaignTown target, Vector2 at)
    {
        float r = 1.8f * RadiusOf(world, target);
        return Vector2.DistanceSquared(at, CentreOf(world, target)) <= r * r && AiSystem.Occupied(world, target);
    }

    /// <summary>
    /// Whether a loaded platoon of a cannibal assault carries enough to retreat (<c>FUN_14028fdf0</c>, <b>Verified</b>; it first marks every member
    /// of the loaded squad "ordered", <c>FUN_14028a4e0</c>): each platoon counts its own carriers and keeps its own stamp of the first one, cleared
    /// while nobody (or no loaded squad) carries; any platoon with three carriers, or more than <see cref="CannibalCarryHours"/> since its first,
    /// ends the campaign's assault.
    /// </summary>
    bool CannibalsLoaded(World world, CampaignInstance inst, double now)
    {
        bool retreat = false;
        var table = world.Characters;
        foreach (int id in inst.Platoons)
        {
            int carriers = 0;
            if (world.Platoons.Find(id) is { State: PlatoonState.Loaded } p && world.Squads.Find(p.SquadId) is { } squad)
            {
                foreach (var member in squad.Members)
                {
                    if (table.TryResolveNext(member, out int slot))
                    {
                        var cold = table.Cold(slot)!;

                        // FUN_14028a4e0, first thing the package's update does: the victim-side mark that stops an Assault booking
                        cold.Ordered = true;
                        if (!cold.Carrying.IsNone) carriers++;
                    }
                }
            }

            if (carriers == 0)
            {
                inst.CarryStarts.Remove(id);
                continue;
            }

            if (!inst.CarryStarts.TryGetValue(id, out double start)) inst.CarryStarts[id] = start = now;
            if (carriers >= 3 || now - start > CannibalCarryHours) retreat = true;
        }

        if (inst.CarryStarts.Count > 0)
        {
            foreach (int id in inst.CarryStarts.Keys.Where(k => !inst.Platoons.Contains(k)).ToList())
                inst.CarryStarts.Remove(id);
        }

        return retreat;
    }

    /// <summary>
    /// Game hours after a platoon's first cannibal carrier that end the assault (more than 15 game minutes: the timer in hours x 60 above 15,
    /// <b>Verified</b>).
    /// </summary>
    public const double CannibalCarryHours = 0.25;

    /// <summary>
    /// Whether the members of a squad run the "find, kidnap and put in a cage" goal now (task 254, <see cref="TaskType.FinalKidnapperCageJob"/>): the
    /// goals of <paramref name="role"/> in the package their campaign is in the phase of (a loaded campaign squad is driven by the campaign, not the
    /// AI), or in the squad's running AI package, in the hour's window.
    /// </summary>
    public bool KidnapDuty(World world, Squad squad, Meitou.Data.Gameplay.Ai.AiRole role) =>
        GoalDuty(world, squad, role, Meitou.Data.Gameplay.Ai.TaskType.FinalKidnapperCageJob);

    /// <summary>
    /// As <see cref="KidnapDuty"/> for any goal type: whether <paramref name="role"/>'s goals in the package the squad runs now hold
    /// <paramref name="type"/> in the hour's window.
    /// </summary>
    public bool GoalDuty(World world, Squad squad, Meitou.Data.Gameplay.Ai.AiRole role, Meitou.Data.Gameplay.Ai.TaskType type)
    {
        IReadOnlyList<Meitou.Data.Gameplay.Ai.AiGoal>? goals = null;
        if (squad.PlatoonId >= 0 && world.Platoons.Find(squad.PlatoonId) is { Campaign: { } inst } p)
        {
            if (inst.Phase > 2) return false;
            if (CampaignOf(inst.CampaignId)?.PackageOf(inst.Phase, inst.Leader == p.Id) is not { } id) return false;

            goals = _data.Ai.Package(id)?.Goals(role);
        }
        else if (squad.Ai is { Roles: { } roles }) goals = roles[(int)role];

        if (goals is null) return false;

        int hour = world.Hour;
        for (int k = 0; k < goals.Count; k++)
            if (goals[k].Task.Type == type && goals[k].InWindow(hour)) return true;

        return false;
    }

    /// <summary>
    /// A campaign won: it ends (phase 2), a hostile one posts its end, and the victory triggers follow up.
    /// </summary>
    void Victory(World world, WarManager m, CampaignData c, CampaignInstance inst, double now)
    {
        inst.Phase = 2;
        inst.PhaseStart = now;
        inst.FightStart = -1;
        if (c.IsHostile) world.Post(Loc.Tr("{1} against {2} has ended", c.DisplayName, NameOf(world, inst.Target)));

        Log($"{_data.Factions[m.Faction].Name}: {c.Name} against {NameOf(world, inst.Target)} won");
        FollowUp(world, m, c.VictoryTriggers, inst, now, 1);
    }

    /// <summary>
    /// A campaign lost: it ends (phase 2) with the message its kind and fight call for, and the loss triggers follow up when it reached its target or
    /// fought the player.
    /// </summary>
    void Defeat(World world, WarManager m, CampaignData c, CampaignInstance inst, double now)
    {
        inst.Phase = 2;
        inst.PhaseStart = now;
        inst.FightStart = -1;
        string target = NameOf(world, inst.Target);
        string message = !c.IsHostile ? Loc.Tr("{1} was defeated on their way to {2}", c.DisplayName, target)
            : inst.FoughtPlayer ? Loc.Tr("{1} against {2} has been defeated", c.DisplayName, target)
            : Loc.Tr("{1} to {2} has failed", c.DisplayName, target);
        world.Post(message);
        Log($"{_data.Factions[m.Faction].Name}: {c.Name} against {target} lost");
        if (c.IsHostile && inst.Reached || inst.FoughtPlayer) FollowUp(world, m, c.LossTriggers, inst, now, 2);
        else if (c.IsHostile)
        {
            ulong roll = Rng.Hash(world.Seed, (ulong)m.Faction << 32 | (uint)inst.Id, RngPurpose.Campaign, SeedSalts.CampaignRetry);
            float delay = 40 + Rng.Float(roll) * 56;
            Queue(world, m, c, inst.Target, CampaignTown.None, inst.Enemy, delay, delay, now, callerFlag: true);
        }
    }

    /// <summary>
    /// One follow-up draw (<c>FUN_1409d27a0</c>): r = uniform[0, 100); below the sum of the V2 percents the entry whose share holds r is queued, due
    /// after uniform[V0, V1) hours.
    /// </summary>
    void FollowUp(World world, WarManager m, IReadOnlyList<CampaignLink> list, CampaignInstance inst, double now, ulong which)
    {
        if (list.Count == 0) return;

        ulong key = (ulong)m.Faction << 32 | (uint)inst.Id;
        float r = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignFollowUp + which)) * 100;
        foreach (var e in list)
        {
            if ((r -= e.V2) >= 0) continue;
            if (CampaignOf(e.Id) is not { } next) return;

            ulong delayRoll = Rng.Hash(world.Seed, key, RngPurpose.Campaign, SeedSalts.CampaignDelay + which);
            float delay = e.V0 + Rng.Float(delayRoll) * Math.Max(0, e.V1 - e.V0);
            Queue(world, m, next, inst.Target, CampaignTown.None, inst.Enemy, delay, delay, now, callerFlag: true);
            return;
        }
    }

    #endregion

    #region Players and fights

    /// <summary>
    /// Loaded fights (the original's are loaded only; the assault's fight timer is armed in <c>Update</c>'s phase 1): a leader-platoon member
    /// fighting within 300 of a player character marks a fight with the player (engine test for the <b>Unknown</b> one).
    /// </summary>
    void TrackFights(World world, CampaignInstance inst)
    {
        var table = world.Characters;
        foreach (int id in inst.Platoons)
        {
            if (world.Platoons.Find(id) is not { State: PlatoonState.Loaded } p || world.Squads.Find(p.SquadId) is not { } squad)
                continue;

            foreach (var member in squad.Members)
            {
                if (!table.TryResolveNext(member, out int slot) || table.Cold(slot) is not { InCombat: true }) continue;

                if (id == inst.Leader && !inst.FoughtPlayer && PlayerNear(world, table.Previous[slot].Position, 300))
                    inst.FoughtPlayer = true;
            }
        }
    }

    /// <summary>
    /// Whether a living player character is within <paramref name="range"/> of <paramref name="at"/>.
    /// </summary>
    static bool PlayerNear(World world, Vector3 at, float range)
    {
        var prev = world.Characters.Previous;
        for (int i = 0; i < prev.Length; i++)
        {
            if (prev[i].Alive && world.Characters.Cold(i) is { IsPlayer: true }
                && Vector3.DistanceSquared(prev[i].Position, at) <= range * range)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a loaded platoon of the campaign has a member in combat.
    /// </summary>
    bool AnyFighting(World world, CampaignInstance inst)
    {
        foreach (int id in inst.Platoons)
        {
            if (world.Platoons.Find(id) is { State: PlatoonState.Loaded } p && world.Squads.Find(p.SquadId) is { } squad
                && squad.Members.Any(m => world.Characters.TryResolveNext(m, out int s) && world.Characters.Cold(s) is { InCombat: true }))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Up: alive and conscious.
    /// </summary>
    static bool Up(World world, CharacterId m) =>
        Living(world, m) && world.Characters.Cold(m.Slot) is not { Medical.Unconscious: true };

    /// <summary>
    /// A loaded platoon with every member down (dead or out).
    /// </summary>
    static bool AllDown(World world, Platoon p) => world.Squads.Find(p.SquadId) is not { } squad || !squad.Members.Any(m => Up(world, m));

    /// <summary>
    /// The platoon's intact flag (<b>Verified</b>, <c>FUN_1407ac7b0</c>): while loaded the squad-intact rule (<c>FUN_1407a5be0</c>,
    /// docs/game/perception.md: a member held keeps it intact; under 4 members the leader is up; else at least 20 % up, 40 % with the leader down),
    /// stored; unloaded, the value it had at unload.
    /// </summary>
    bool Intact(World world, Platoon p)
    {
        if (p.Dead) return false;   // declared dead (+0x1f0): never intact
        if (p.State == PlatoonState.Loaded && world.Squads.Find(p.SquadId) is { } squad) p.IntactFlag = SquadIntact(world, squad);
        return p.IntactFlag;
    }

    /// <summary>
    /// The squad-intact rule (<see cref="Ai.SquadIntact"/>, <c>FUN_14079acf0</c>): a campaign platoon is not a persistent one with a home.
    /// </summary>
    bool SquadIntact(World world, Squad squad)
    {
        var table = world.Characters;
        if (Ai.SquadIntact.Held(table, squad, _data.Relations)) return true;

        int up = 0;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || !table.Next[s].Alive) continue;
            if (table.Cold(s) is { } c && Ai.SquadIntact.Able(world, c, false)) up++;
        }

        return Ai.SquadIntact.Decide(up, squad.Members.Count, !squad.Leader.IsNone, Ai.SquadIntact.Down(table, squad.Leader), persistentHome: false);
    }

    /// <summary>
    /// The player's living characters in a town (XZ, its radius).
    /// </summary>
    List<CharacterCold> PlayersAt(World world, CampaignTown t)
    {
        var at = CentreOf(world, t);
        float r = RadiusOf(world, t);
        var found = new List<CharacterCold>();
        var prev = world.Characters.Previous;
        for (int i = 0; i < prev.Length; i++)
        {
            if (!prev[i].Alive || world.Characters.Cold(i) is not { IsPlayer: true } cold) continue;

            if (Vector2.DistanceSquared(new Vector2(prev[i].Position.X, prev[i].Position.Z), at) <= r * r)
                found.Add(cold);
        }

        return found;
    }

    /// <summary>
    /// The player's characters in a town: (in all, up).
    /// </summary>
    (int All, int Up) PlayersIn(World world, CampaignTown t)
    {
        int all = 0, up = 0;
        foreach (var cold in PlayersAt(world, t))
        {
            all++;
            if (cold.DiedTick < 0 && cold.Medical is not { Unconscious: true } and not { Dead: true }) up++;
        }

        return (all, up);
    }

    /// <summary>
    /// More than 2 player characters in the town that are not sneaking out of a fight (the original's A, <c>FUN_1405c9750</c>), and more than half of
    /// A is B: the characters of the town in posture 0 (not staying low, crawling, playing dead or knocked out; vfunc <c>0x368</c>) or in a bed
    /// (<c>+0x2f8</c> 1), stealthed ones included (<b>Verified</b>, <c>FUN_1409c70f0</c>): an assault keeps going.
    /// </summary>
    bool PlayersHold(World world, CampaignTown t)
    {
        int seen = 0, standing = 0;
        foreach (var cold in PlayersAt(world, t))
        {
            if ((cold.Orders & CharacterOrders.Sneak) == 0 || cold.InCombat) seen++;
            if (cold.InSomething == Locks.HeldState.Bed || cold.DiedTick < 0 && CharacterFacts.PostureZero(cold))
                standing++;
        }

        return seen > 2 && standing * 2 > seen;
    }

    /// <summary>
    /// Whether a player character stands up in the town.
    /// </summary>
    bool PlayersStanding(World world, CampaignTown t) => PlayersIn(world, t).Up > 0;

    /// <summary>
    /// The first door fixture of each of a town's buildings (a placed town's by site, a player town's by its buildings): the slots 92 and 96 read
    /// only the building's first door (<c>Building</c> slot 124).
    /// </summary>
    static IEnumerable<Locks.Fixture> TownDoors(World world, CampaignTown t) =>
        world.Fixtures.All.Where(f => f.Kind == Locks.FixtureKind.Door && f.DoorIndex == 0
            && (t.Site >= 0 ? f.Site == t.Site : t.PlayerTown > 0 && world.Buildings.Find(f.Building) is { } b && b.Town == t.PlayerTown));

    /// <summary>
    /// Town slot 92 (<c>FUN_1408cc930</c>): no house door of the town is closed (a broken one counts as open); true without any.
    /// </summary>
    static bool DoorsOpen(World world, CampaignTown t) =>
        TownDoors(world, t).All(f => f.IsGate || f.DoorBroken || f.Motion != Locks.DoorMotion.Closed);

    /// <summary>
    /// Town slot 96 (<c>FUN_140929820</c>): the town has a gate and none of its gates is open (a broken one counts as open).
    /// </summary>
    static bool WallsLockedUp(World world, CampaignTown t) => Locks.LockSystem.GatesLockedUp(TownDoors(world, t));

    /// <summary>
    /// Some faction runs a hostile campaign against the player (a defence ends only when none does).
    /// </summary>
    bool HostileAgainstPlayer(World world)
    {
        int player = PlayerFactionOf(world);
        foreach (var m in _wars!)
        {
            foreach (var a in m.Actives)
                if (a.Phase != 2 && a.Enemy == player && CampaignOf(a.CampaignId) is { IsHostile: true }) return true;
        }

        return false;
    }

    #endregion

    #region Moving

    /// <summary>
    /// Sets every campaign platoon's goal (the leader's: the target, or home once ended; the others': the leader) and moves the unloaded ones in
    /// straight lines at the <c>travel speed unloaded</c> speeds (leader TRAVEL_TARGET, others TRAVEL_TARGET_FAST: WALK 25, JOG 55, RUN 90; GROUPED
    /// and NO_SPEED_CHANGE leader 25, others 60 or 90 beyond 25000; all capped at 70). Serial: followers read their leader's place.
    /// </summary>
    void MoveCampaigns(World world, float dt)
    {
        foreach (var m in _wars!)
        {
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

                    // Follower i keeps i × slots × 150 / N back along the leader's trail
                    // (engine choice for the Unknown: the trail point, not the leader platoon).
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
    }

    /// <summary>
    /// Whom a loaded campaign platoon fights besides its faction's enemies (docs/game/factions-squads-towns.md "Campaigns", enemies): in phase 1,
    /// everyone in the target town or in none (<c>AggroSystem.WarEnemy</c>) when its <c>pt2</c> package is a Package_WarAssault (signal 20 or 32),
    /// else the target faction when the campaign is hostile; nobody in the other phases.
    /// </summary>
    WarTargets AssaultTargets(World world, CampaignInstance inst, bool leads)
    {
        if (inst.Phase != 1 || CampaignOf(inst.CampaignId) is not { } c) return WarTargets.None;

        if (c.PackageOf(1, leads) is { } id && _data.Ai.Package(id) is { Signal: 20 or 32 })
            return new WarTargets(-1, true, inst.Target);

        return c.IsHostile && inst.Enemy >= 0 ? new WarTargets(inst.Enemy, false, CampaignTown.None) : WarTargets.None;
    }

    #endregion

    #region Announcements

    /// <summary>
    /// The dialogue's announcement and pending-talk queries (<see cref="Dialogue.DialogueSystem.Announce"/>,
    /// <see cref="Dialogue.DialogueSystem.HasInterrupt"/>), set by the host; null: none.
    /// </summary>
    public Func<World, CharacterId, string, bool>? Announcer { get; set; }
    /// <summary>
    /// The dialogue's pending-talk query (see <see cref="Announcer"/>); null: none.
    /// </summary>
    public Func<CharacterId, bool>? PendingTalk { get; set; }

    /// <summary>
    /// The squads' memories (the dialogue system's): a squad that leaves the world hands its memory to its platoon
    /// (<see cref="StoredSquad.Memory"/>), and gets it back when it returns; null: none kept.
    /// </summary>
    public Dialogue.SquadMemory? Memory { get; set; }

    /// <summary>
    /// Game hours an arrived campaign waits for its announcement and negotiation before it goes on (engine choice).
    /// </summary>
    public const double AnnouncementPatience = 1;
    /// <summary>
    /// The announcement needs the leader within this of the camera and a target-faction member within this of the leader (<b>Verified</b>, task 129).
    /// </summary>
    const float AnnounceRange = 2500;

    /// <summary>
    /// A loaded leader in phase 0 that has arrived at the target (task 129 needs state 131 <c>atPackageTarget</c>; Meitou:
    /// <see cref="CampaignInstance.ArrivedAt"/>) and is not down (!101) says its campaign's <c>dialog announcement</c>, again each time it has ended,
    /// while the camera and a target-faction member are within 2500 of it.
    /// </summary>
    void TryAnnounce(World world, CampaignInstance inst, CharacterId leader, Vector3 at)
    {
        // After the wait ends (its dialogue finished, nothing pending) the task falls through to the announcement again while the conditions hold
        // (FUN_140355770, a2-047).
        if (inst.Phase != 0 || inst.ArrivedAt < 0 || inst.AnnouncedAt >= 0 && PendingTalk?.Invoke(inst.Announcer) == true || Announcer is null
            || inst.Enemy < 0 || CampaignOf(inst.CampaignId)?.DialogAnnouncement is not { } announcement)
            return;

        if (!world.Characters.TryResolveNext(leader, out int ls) || !world.Characters.Next[ls].Alive
            || world.Characters.Cold(ls)?.Medical?.Incapacitated == true)
            return;

        if (!AnnouncementHeard(world, inst, new Vector2(at.X, at.Z)) || !Announcer(world, leader, announcement)) return;

        inst.Announcer = leader;
        inst.AnnouncedAt = world.GameHours;
    }

    /// <summary>
    /// The announcement's conditions (task 129, <c>FUN_140354990</c>): the leader within 2500 of the camera and a member of the target faction
    /// within 2500 of the leader.
    /// </summary>
    bool AnnouncementHeard(World world, CampaignInstance inst, Vector2 at)
    {
        if (inst.Enemy < 0 || _focus is not { } camera) return false;
        if (Vector2.DistanceSquared(at, new Vector2(camera.X, camera.Z)) > AnnounceRange * AnnounceRange) return false;

        var table = world.Characters;
        for (int i = 0; i < table.Previous.Length; i++)
        {
            if (!table.Previous[i].Alive || table.Cold(i) is not { } c || c.Faction != inst.Enemy) continue;

            var memberAt = new Vector2(table.Previous[i].Position.X, table.Previous[i].Position.Z);
            if (Vector2.DistanceSquared(memberAt, at) <= AnnounceRange * AnnounceRange) return true;
        }

        return false;
    }

    #endregion

    #region Trail

    /// <summary>
    /// The trail's step and its first slot count (<b>Verified</b>, <c>FUN_1409d6070</c>).
    /// </summary>
    const float TrailStep = 150;
    /// <summary>
    /// The trail's first slot count (see <see cref="TrailStep"/>).
    /// </summary>
    const int TrailSlots = 5;

    /// <summary>
    /// The leader's trail, each war-manager run (<b>Verified</b>): an empty one is seeded with <paramref name="slots"/> + 1 points within ±100 of the
    /// leader; the leader's place is added once it is over 150 from the newest; one oldest point is dropped while there are more points than
    /// platoons.
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

    /// <summary>
    /// The point <paramref name="back"/> units back along the trail from its newest point (its oldest when the trail is shorter).
    /// </summary>
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

    #endregion

    #region Column

    /// <summary>
    /// A <c>travel speed loaded</c> as a movement mode (<b>Verified</b>: WALK the default walk, JOG 55, RUN and GROUPED uncapped,
    /// NO_SPEED_CHANGE as it was).
    /// </summary>
    static byte LoadedMode(MoveSpeed speed, byte current) => speed switch
    {
        MoveSpeed.Walk => SpeedMode.Walk,
        MoveSpeed.Jog => SpeedMode.Run,
        MoveSpeed.Run or MoveSpeed.Grouped => SpeedMode.Free,
        _ => current,
    };

    /// <summary>
    /// The leader keeps its loaded speed while every intact platoon is within this of it (<b>Verified</b>), else it stops within
    /// <see cref="WaitNearTarget"/> of its target, else walks.
    /// </summary>
    const float ColumnRange = 6000, WaitNearTarget = 8294.4f;

    /// <summary>
    /// The off-screen speed of a campaign platoon, in units per second: by its <c>move speed</c>, else the leader 25 and the others 60, or 90 more
    /// than 25000 away.
    /// </summary>
    static float CampaignSpeed(MoveSpeed speed, bool leader, float distance) => speed switch
    {
        MoveSpeed.Walk => 25,
        MoveSpeed.Jog => 55,
        MoveSpeed.Run => 90,
        _ => leader ? 25 : distance > 25000 ? 90 : 60,
    };

    /// <summary>
    /// A loaded campaign platoon (serial, from the roaming look): its leader is sent to the goal (walking for WALK, else running),
    /// and mills about once there.
    /// </summary>
    void StepCampaignLoaded(World world, Platoon p, Squad squad, float dt)
    {
        var table = world.Characters;
        squad.War = AssaultTargets(world, p.Campaign!, p.Campaign!.Leader == p.Id);
        if (p.CampaignGoal is not { } goal || !table.TryResolveNext(squad.Leader, out int slot)) return;

        var cold = table.Cold(slot)!;
        cold.PaceCap = null;   // set again below while it trails at the leader's pace
        if (p.Campaign!.Leader == p.Id) TryAnnounce(world, p.Campaign, squad.Leader, table.Next[slot].Position);

        RetreatHome(p, squad, goal);

        // a fight, a lift (a kidnapper) or a loot is left to itself
        byte task = table.Next[slot].Task;
        if (task is (byte)CharacterTask.Attack or (byte)CharacterTask.PickUp or (byte)CharacterTask.PutDown or (byte)CharacterTask.Loot) return;

        if (MillAtGoal(world, p, squad, table, slot, goal)) return;

        // The column (task 215, Verified): followers jog to their trail points; the leader marching out keeps its loaded speed while every intact
        // platoon is within 6000, else it waits near the target or walks.
        var inst = p.Campaign!;
        bool leads = inst.Leader == p.Id;
        ref var n = ref table.Next[slot];
        float d = Vector2.Distance(p.Position, goal);
        byte mode = FollowerPace(world, inst, leads, table, cold, d);

        if (leads)
        {
            mode = LoadedMode(CampaignOf(inst.CampaignId)?.TravelSpeedLoaded ?? MoveSpeed.Run, n.Mode);
            if (inst.Phase == 0 && !inst.Platoons.All(id => id == p.Id || world.Platoons.Find(id) is not { } q || !Intact(world, q)
                || Vector2.Distance(q.Position, p.Position) <= ColumnRange))
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

        TaskStart.Walk(world, slot, goal, mode, Meitou.Data.Gameplay.Ai.TaskType.TravelToTargetPackage, TaskSource.Goal);
        p.OrderCooldown = 4;
    }

    /// <summary>
    /// Retreating, members the column does not move (a carrier, whom the AI leaves alone) mill round their own town again, not round the raid target
    /// the squad last milled at, so a kidnapper takes its captive home (engine choice; the arrival below still mills them round the goal).
    /// </summary>
    void RetreatHome(Platoon p, Squad squad, Vector2 goal)
    {
        var inst = p.Campaign!;
        float homeLimit = inst.Leader == p.Id ? _settings.RoamArrivalRadius : CampaignFollowDistance;
        if (inst.Phase == 2 && squad.Town >= 0 && Vector2.Distance(p.Position, goal) > homeLimit)
        {
            squad.HomeCentre = SiteCentre(squad.Town);
            squad.HomeRadius = 60;
        }
    }

    /// <summary>
    /// The arrival of a campaign platoon at its goal (task 215): a leader, or a follower in phase 1, within its arrival radius mills round the goal
    /// (its home centre and radius are set); true when it has arrived, which ends the step.
    /// </summary>
    bool MillAtGoal(World world, Platoon p, Squad squad, CharacterTable table, int slot, Vector2 goal)
    {
        var inst = p.Campaign!;
        bool leads = inst.Leader == p.Id;
        float arrive = leads ? _settings.RoamArrivalRadius : CampaignFollowDistance;

        // A travelling follower (task 215) never mills: inside 100 of its trail point it keeps walking straight at it at the leader's pace
        // (FUN_140349e70); only the arrival at the target (phase 1) mills.
        bool arrived = Vector2.Distance(p.Position, goal) <= arrive && (leads || inst.Phase == 1);
        if (!arrived) return false;

        if (table.Next[slot].Task == (byte)CharacterTask.GoTo)
        {
            TaskStart.Become(world, slot, CharacterTask.Wander, Meitou.Data.Gameplay.Ai.TaskType.Wanderer, TaskSource.Goal);
            table.Next[slot].Mode = SpeedMode.Walk;
            table.Next[slot].TaskTime = 0.5f;
        }

        squad.HomeCentre = goal;

        // The mill at the target is an engine choice, sized as a roaming squad's at a town (PopulationRoaming.cs): the size radius of a placed town,
        // a player town's own.
        float mill = inst.Target.Site >= 0
            ? _data.Sites[inst.Target.Site].Town.SizeRadius * _data.Sites[inst.Target.Site].Town.TownRadiusMult
            : RadiusOf(world, inst.Target);
        squad.HomeRadius = leads && inst.Phase == 1 ? Math.Max(mill * 0.5f, 40) : 60;
        return true;
    }

    /// <summary>
    /// The movement mode of a follower inside its trail point's reach: inside 100 it takes its leader's mode and, as a cap, the leader's speed now (x
    /// 0.9 inside 70, FUN_140349e70); otherwise the run mode.
    /// </summary>
    byte FollowerPace(World world, CampaignInstance inst, bool leads, CharacterTable table, CharacterCold cold, float d)
    {
        byte mode = SpeedMode.Run;

        // Inside 100 a follower takes the campaign leader's current speed, x 0.9 inside 70 (FUN_140349e70): its leader's mode and, as a cap, the
        // leader's speed now.
        if (!leads && d < CampaignFollowDistance && world.Platoons.Find(inst.Leader) is { } head && world.Squads.Find(head.SquadId) is { } headSquad
            && table.TryResolveNext(headSquad.Leader, out int hs))
        {
            mode = table.Next[hs].Mode;
            var v = table.Next[hs].Velocity;
            cold.PaceCap = new Vector2(v.X, v.Z).Length() * (d < CampaignPaceDistance ? CampaignPaceFactor : 1);
        }

        return mode;
    }

    #endregion

    #region State hash

    /// <summary>
    /// The war managers once any faction has started or queued a campaign, the kept wild squads while there are any, the towns' artifacts while any
    /// are left and the prosperity of the factions below their cap (so worlds without them hash as before).
    /// </summary>
    public void Hash(ref StateHasher h)
    {
        HashWars(ref h);
        HashArtifacts(ref h);
        if (_prosperity is not null)
        {
            for (int f = 0; f < _prosperity.Length; f++)
            {
                if (_prosperity[f] < _data.Factions[f].MaxProsperity)
                {
                    h.Add(f);
                    h.Add(_prosperity[f]);
                }
            }
        }

        foreach (var (site, kept) in _keptWild)
        {
            h.Add(site);
            foreach (var k in kept)
            {
                h.Add(Rng.StableHash(k.TemplateId));
                h.Add(k.Stored.Members.Count);
                foreach (var m in k.Stored.Members)
                {
                    h.Add(m.Position.X);
                    h.Add(m.Position.Z);
                }
            }
        }
    }

    /// <summary>
    /// Adds the war managers' state to the world's state hash, once any manager started or queued a campaign.
    /// </summary>
    void HashWars(ref StateHasher h)
    {
        if (_wars is null || !_wars.Any(m => m.NextId > 0 || m.Requests.Count > 0)) return;

        h.Add(_warCursor);
        foreach (var m in _wars)
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
                h.Add(a.Announcer);
                h.Add(a.AnnouncedAt);
                h.Add(a.ArrivedAt);
                h.Add(a.Trail.Count);
                foreach (var t in a.Trail)
                    h.Add(t);

                h.Add(a.Platoons.Count);
                foreach (int id in a.Platoons)
                    h.Add(id);

                // only while a cannibal platoon's members carry, so other worlds hash as before
                if (a.CarryStarts.Count > 0)
                {
                    foreach (int id in a.Platoons)
                    {
                        if (!a.CarryStarts.TryGetValue(id, out double carry)) continue;

                        h.Add(id);
                        h.Add(carry);
                    }
                }
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

    #endregion
}
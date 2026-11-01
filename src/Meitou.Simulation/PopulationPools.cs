using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Simulation;

/// <summary>
/// A population pool of a town or nest (docs/game/factions-squads-towns.md "Off-screen world"): its desired max (save key <c>popd</c> / <c>popd2</c>), its current max
/// (<c>pop</c> / <c>pop2</c>) and the game hour it last grew at (not saved: the original stamps it at load too).
/// </summary>
public sealed class PoolState
{
    public int Desired { get; set; }
    public float Current { get; set; }
    public double Stamp { get; set; }

    /// <summary>
    /// The original's growth (<c>FUN_1408f4c20</c>): the current max rises 3600 / 33 x 0.5 x 0.004 = 0.21818 per game hour since the last growth, up to the desired
    /// max, whenever it is looked at.
    /// </summary>
    public void Grow(double nowHours)
    {
        double h = nowHours - Stamp;
        Stamp = nowHours;
        if (h < 0 || Current >= Desired) return;
        Current = MathF.Min(Desired, Current + (float)(h * GrowthPerHour));
    }

    public const double GrowthPerHour = 3600.0 / 33 * 0.5 * 0.004;
}

/// <summary>A town's or nest's two pools (1 residents, 2 roaming) and its flags: made once (<c>started</c>), wiped out (<c>ded</c>) and when (<c>tod</c>, game hours).</summary>
public sealed class SitePools
{
    public PoolState Residents { get; } = new();
    public PoolState Roaming { get; } = new();
    public bool Started { get; set; }
    public bool Ded { get; set; }
    public float DiedAt { get; set; }

    /// <summary>
    /// Both pools as the original grows them (<c>FUN_1408f6760</c>): residents, then roaming once the residents are within 1 of full. A roaming pool not grown keeps its stamp,
    /// so the time it waited is credited when it grows again (<b>Verified</b>, <c>FUN_1408f4c20</c>).
    /// </summary>
    public void Grow(double nowHours)
    {
        Residents.Grow(nowHours);
        if (Residents.Current >= Residents.Desired - 1) Roaming.Grow(nowHours);
    }

    /// <summary>Reads a town state's pool keys (<c>pop</c>, <c>popd</c>, <c>pop2</c>, <c>popd2</c>, <c>started</c>, <c>ded</c>, <c>tod</c>), stamped at <paramref name="nowHours"/>.</summary>
    public static SitePools Read(FcsRecord state, double nowHours)
    {
        var p = new SitePools
        {
            Started = state.Bools.GetValueOrDefault("started"),
            Ded = state.Bools.GetValueOrDefault("ded"),
            DiedAt = state.Floats.GetValueOrDefault("tod"),
        };
        p.Residents.Current = state.Floats.GetValueOrDefault("pop");
        p.Residents.Desired = (int)state.Floats.GetValueOrDefault("popd");
        p.Roaming.Current = state.Floats.GetValueOrDefault("pop2");
        p.Roaming.Desired = (int)state.Floats.GetValueOrDefault("popd2");
        p.Residents.Stamp = p.Roaming.Stamp = nowHours;
        return p;
    }

    /// <summary>Writes the pool keys into a town state (as floats, the save's type), and <c>ded</c>; <c>started</c> is the caller's.</summary>
    public void Write(FcsRecord state)
    {
        state.Floats["pop"] = Residents.Current;
        state.Floats["popd"] = Residents.Desired;
        state.Floats["pop2"] = Roaming.Current;
        state.Floats["popd2"] = Roaming.Desired;
        state.Floats["tod"] = DiedAt;
        state.Bools["ded"] = Ded;
    }
}

/// <summary>
/// The pools of the towns and nests (docs/simulation.md "Off-screen tier"): grown on the game clock for every town and nest, loaded or not; a town made once makes no
/// residents again; on a town's load its squads that lost at least 60 % are made afresh within its residents' budget.
/// </summary>
public sealed partial class PopulationSystem
{
    SitePools?[] townPools = [];
    readonly Dictionary<int, SitePools> nestPools = [];

    /// <summary>The game hour of the world clock (ticks since the world began).</summary>
    static double Hours(World world) => world.Tick * (double)BodySystem.HoursPerTick;

    /// <summary>A town's pools (null: never made, and the save holds none).</summary>
    public SitePools? PoolsOf(int site)
    {
        if (townPools.Length != data.Sites.Count) return null;
        return townPools[site];
    }

    /// <summary>A nest site's pools (null: not set up yet).</summary>
    public SitePools? NestPoolsOf(int wildSite) => nestPools.GetValueOrDefault(wildSite);

    /// <summary>The towns' pools from a loaded save's town states, once (towns the save made: <c>started</c> or a pool of their own).</summary>
    void EnsurePools(World world)
    {
        if (townPools.Length == data.Sites.Count) return;
        townPools = new SitePools?[data.Sites.Count];
        if (world.Saved is not { } saved) return;
        double now = Hours(world);
        foreach (var site in data.Sites)
            if (site.InstanceId.Length > 0 && saved.TownStates.TryGetValue(site.InstanceId, out var state) &&
                (state.Bools.GetValueOrDefault("started") || state.Floats.GetValueOrDefault("popd") > 0 || state.Floats.GetValueOrDefault("popd2") > 0))
                townPools[site.Index] = SitePools.Read(state, now);
    }

    /// <summary>Every pool grows (the slow phase, every <see cref="PopulationSettings.RoamCheckTicks"/>).</summary>
    void GrowPools(World world)
    {
        double now = Hours(world);
        foreach (var p in townPools) p?.Grow(now);
        foreach (var p in nestPools.Values) p.Grow(now);
    }

    /// <summary>A town made: <c>started</c>, and each resident squad's size (one with a home; bar squads are not counted) into the residents' desired and current max (as the original's resident maker, <c>FUN_14057f820</c>).</summary>
    void MadeTown(World world, int site, IEnumerable<int> sizes)
    {
        var pools = townPools[site] ??= new SitePools { Residents = { Stamp = Hours(world) }, Roaming = { Stamp = Hours(world) } };
        pools.Started = true;
        foreach (int n in sizes)
        {
            pools.Residents.Desired += n;
            pools.Residents.Current += n;
        }
    }

    /// <summary>The town wipe-out gates (<c>TownPopulationManager::vfunc_4</c>, <b>Verified</b>): a roaming pool over this many keeps the town, and so do more living roamers than <see cref="WipeRoamingLiving"/>.</summary>
    public const float WipeRoamingPool = 3;
    public const int WipeRoamingLiving = 25;

    /// <summary>
    /// Town wipe-out (<c>TownPopulationManager::vfunc_4</c> <c>FUN_1408f7a80</c>, docs/game/factions-squads-towns.md "Town wipe"): a started town in a loaded zone that is not yet <c>ded</c>, whose roaming
    /// pool is at most 3 and whose roaming platoons have at most 25 living, and none of whose resident squads (any with a home; engine reading of the resident pool) is intact
    /// (<see cref="ResidentIntact"/>: the platoon's flag, which a squad with a member held keeps true), is <c>ded</c> for good: it
    /// remakes no depleted squads (<see cref="CheckRegeneration"/>) and its <c>regenerates</c> squads die. Nothing is posted (the town's <c>setDead</c> is empty).
    /// </summary>
    void CheckTownWipes(World world)
    {
        if (townPools.Length != data.Sites.Count) return;
        var resident = new bool[data.Sites.Count];
        var roamers = new int[data.Sites.Count];
        foreach (var p in world.Platoons.All)
        {
            if (p.Origin < 0 || p.Origin >= resident.Length || p.Wild) continue;
            if (p.Pinned)
            {
                if (p.Stored is { HomeBuilding: null }) continue;
                if (RoamingMilitary(p.TemplateId)) roamers[p.Origin] += LivingOf(world, p);
                else if (ResidentIntact(world, p)) resident[p.Origin] = true;
            }
            else roamers[p.Origin] += LivingOf(world, p);
        }
        for (int s = 0; s < states.Length && s < resident.Length; s++)
            foreach (int id in states[s].Squads)
            {
                if (world.Squads.Find(id) is not { HomeBuilding: not null } squad) continue;
                if (RoamingMilitary(squad.TemplateId)) roamers[s] += squad.PlatoonId < 0 ? squad.Members.Count(m => Living(world, m)) : 0;   // a platoon's own are counted above
                else if ((squad.Ai?.Intact ?? true) && squad.Members.Any(m => Living(world, m))) resident[s] = true;
            }
        for (int s = 0; s < resident.Length; s++)
        {
            // A town coming into the world has neither its squads nor its platoons for a moment: not judged then.
            if (s < states.Length && (states[s].Status == SiteStatus.Loading || states[s].Pending)) continue;
            // Judged only while the zone at the town's position is loaded (zone byte +0xb1, FUN_1408f6760).
            if (world.LoadedZones is { } loaded && !loaded.Contains(Meitou.Data.World.WorldLayout.ZoneOf(data.Sites[s].Position.X, data.Sites[s].Position.Z))) continue;
            // Engine choice: a town of the player's faction is kept (as the repopulation and remake passes leave it alone); the original's wipe gates have no faction test,
            // though the town list's caller of them was not read.
            if (data.SiteFactions[s] is >= 0 and var owner && owner == PlayerFactionOf(world)) continue;
            if (townPools[s] is not { Started: true, Ded: false } pools || resident[s] || pools.Roaming.Current > WipeRoamingPool || roamers[s] > WipeRoamingLiving) continue;
            pools.Ded = true;
            pools.DiedAt = (float)Hours(world);
            if (townLog.Count >= 256) townLog.RemoveAt(0);
            townLog.Add($"{data.Sites[s].Town.Name} wiped out");
        }
    }

    /// <summary>
    /// A template with <c>roaming military</c> is made in squad mode 2 even from a town's resident list (<c>FUN_140583a10</c>, <b>Verified</b>): the mode only picks the town pool set the platoon is
    /// registered in (<c>FUN_1407ec8d0</c>), so it still has its home and stands in the town. Meitou takes it into account at the town wipe: it is no resident there and counts with the roaming living.
    /// </summary>
    bool RoamingMilitary(string templateId) => data.Db.Find(templateId)?.GetBool("roaming military") == true;

    /// <summary>
    /// Whether a resident platoon keeps its town from being wiped: <c>Platoon::isIntact</c> (<c>FUN_1407ac7b0</c>, <b>Verified</b>), false once declared dead, else its flag, which a loaded squad's
    /// AI keeps current (<see cref="Ai.Blackboard.Intact"/>, the rule of <c>FUN_1407a5be0</c>) and an unloaded platoon holds as it was at unload (or in the save). Meitou also needs someone
    /// alive in a platoon not in the world: its off-screen fights can kill members the flag never saw.
    /// </summary>
    bool ResidentIntact(World world, Platoon p)
    {
        if (p.Dead) return false;
        if (p.State == PlatoonState.Loaded && world.Squads.Find(p.SquadId) is { } squad)
            return p.IntactFlag = squad.Ai?.Intact ?? SquadIntact(world, squad);
        return p.IntactFlag && LivingOf(world, p) > 0;
    }

    /// <summary>SQUAD_TEMPLATE <c>regenerates</c> (<c>Platoon::declareDead</c>, <b>Verified</b>): a squad of it whose last member dies is not declared dead while its town is not <c>ded</c>; it is made afresh on its next activation.</summary>
    bool Regenerates(Platoon p) => p.Pinned && TownAlive(p.Origin) && data.Db.Find(p.TemplateId)?.GetBool("regenerates") == true;
    bool Regenerates(string templateId, int site) => TownAlive(site) && data.Db.Find(templateId)?.GetBool("regenerates") == true;
    bool TownAlive(int site) => site >= 0 && site < townPools.Length && townPools[site] is not { Ded: true };

    /// <summary>
    /// The original's regeneration check at a town's load (<c>0x14092a020</c>): each of its own squads (in platoon id order) that lost at least 60 % of its size, while the
    /// losses so far fit the residents' current max less the living, is marked to be made afresh from its template when it next comes into the world.
    /// </summary>
    void CheckRegeneration(World world, int site)
    {
        // A wiped-out town remakes nothing (TownBase::activate runs the pass only while it is not ded).
        if (townPools[site] is not { Ded: false } pools) return;
        var own = new List<(Platoon P, int Size, int Living)>();
        int running = 0;
        foreach (var p in world.Platoons.All)
        {
            if (p.Origin != site || !p.Pinned || p.State != PlatoonState.Unloaded || p.Unmade) continue;
            // Residents only: the pool counts them (base saves: <c>pop</c> = the resident platoons' size, not the bar squads').
            if (p.Faction != data.SiteFactions[site]) continue;   // and of the town's faction (save 2: Squin's 36 = its Shek residents, without a Tech Hunters shop)
            if (p.Stored is { HomeBuilding: null } || p.Saved is { } r && !r.Source.Record.Bools.GetValueOrDefault("is resident")) continue;
            int size, living;
            if (p.Stored is { } stored) (size, living) = (p.Size, stored.Members.Count(m => m.Cold.Medical is not { Dead: true }));
            else if (p.Saved is { } saved)
                (size, living) = (saved.Members.Count, saved.Members.Count(m => !m.Dead && !m.Gone && (m.Position is not null || m.Source.Medical?.Bools.GetValueOrDefault("dead") != true)));
            else continue;
            own.Add((p, size, living));
            running += living;
        }
        foreach (var (p, size, living) in own)
        {
            // A `regenerates` squad with no one left comes back whatever the budget (Platoon::declareDead).
            if (living == 0 && size > 0 && Regenerates(p)) { p.Regenerate = true; continue; }
            int lost = size - living;
            if (lost <= 0 || lost < 0.6 * size || lost > pools.Residents.Current - running) continue;
            p.Regenerate = true;
            running += lost;
        }
    }

    /// <summary>
    /// A town's squad marked for regeneration comes into the world made afresh from its template at its place, homed where it was; its old characters are gone (a save's
    /// platoon leaves the save, the new one is written as the town's). Returns the new platoon's squad (null when the template plans no one).
    /// </summary>
    Squad? Regenerate(World world, Platoon p)
    {
        var site = data.Sites[p.Origin];
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult;
        var home = p.Stored?.HomeBuilding ?? (p.Saved is { } sp ? Saving.SavedCharacters.SavedHome(world, sp, site) : null);
        // Its unique members are freed first, so the fresh squad makes them again rather than their replacements.
        ReleaseUniques(world, p);
        var (built, key) = RollTemplate(world, p);
        if (built is null) return null;
        var fresh = new Platoon
        {
            Id = world.Platoons.NextId(), Origin = p.Origin, TemplateId = p.TemplateId, Faction = p.Faction, Key = key, Size = built.Plan.TotalMembers,
            Position = p.Position, Money = p.Money, Package = p.Package, Stored = new StoredSquad { Name = data.Db.Find(p.TemplateId)?.Name ?? p.TemplateId, HomeBuilding = home },
            State = PlatoonState.Loaded,
        };
        // The old squad's building shop (kept when it left the world, else a save's) leaves its artifacts in its containers for the fresh one's.
        var oldShop = p.Stored?.Shop ?? (p.Saved is { } saved
            ? saved.Shop ?? Saving.ShopSave.Read(world, new Squad { Id = -1, TemplateId = p.TemplateId, Faction = p.Faction, Town = p.Origin, HomeBuilding = home }, saved.Source.Record, data)
            : null);
        if (oldShop is not null)
        {
            world.Fixtures.RegisterSite(world, data, p.Origin);   // the containers, if the town has not loaded them yet (once)
            Trading.TradeSystem.GiveBack(world, oldShop);
        }
        var squad = SpawnSquad(world, built with { Home = home }, site, radius, platoonId: fresh.Id, pinned: true);
        squad.Money = p.Money;
        fresh.SquadId = squad.Id;
        world.Platoons.Add(fresh);
        Despawn(world, p, release: false);   // its uniques were freed above and the fresh squad holds them
        if (townLog.Count >= 256) townLog.RemoveAt(0);
        townLog.Add($"{site.Town.Name}: {fresh.Stored.Name} made afresh ({squad.Members.Count})");
        return squad;
    }

    /// <summary>A template whose members are each by chance can plan no one: a few rolls (engine choice). Null when none plans anyone.</summary>
    (BuiltSquad? Built, ulong Key) RollTemplate(World world, Platoon p)
    {
        for (int t = 0; t < 8; t++)
        {
            ulong key = Rng.Mix(SeedSalts.RoamingPool ^ (ulong)p.Id << 8 ^ (ulong)world.Tick << 24 ^ (ulong)t << 56);
            if (BuildRoamer(p.TemplateId, p.Faction, key, world.Seed, p.Position) is { } built) return (built, key);
        }
        return (null, 0);
    }

    /// <summary>
    /// A save's never-made town squad (<see cref="Platoon.Unmade"/>) comes into the world made from its template into the same platoon, as the original builds the
    /// platoon object it has (<c>FUN_1404ffad0</c>, +0x114 set): homed at its <c>homebuilding</c>, it keeps the save's record, which the next save writes back with its
    /// characters and <c>never been activated</c> false (<c>FUN_140372ef0</c>). Returns its squad (null when the template plans no one).
    /// </summary>
    Squad? MakeUnmade(World world, Platoon p)
    {
        var site = data.Sites[p.Origin];
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult;
        var home = p.Saved is { } sp ? Saving.SavedCharacters.SavedHome(world, sp, site) : null;
        if (RollTemplate(world, p).Built is not { } built) return null;
        var squad = SpawnSquad(world, built with { Home = home }, site, radius, platoonId: p.Id, pinned: true);
        squad.Money = p.Money;
        p.Stored = new StoredSquad { Name = data.Db.Find(p.TemplateId)?.Name ?? p.TemplateId, HomeBuilding = home };
        p.Size = built.Plan.TotalMembers;
        p.Unmade = false;
        // Activation clears +0x114 on the platoon itself: once made, no later save rebuilds it from the template, whatever becomes of its squad.
        if (p.Saved is { } made) made.Source.Record.Bools["never been activated"] = false;
        p.State = PlatoonState.Loaded;
        p.SquadId = squad.Id;
        p.UnloadAtTick = -1;
        if (townLog.Count >= 256) townLog.RemoveAt(0);
        townLog.Add($"{site.Town.Name}: {p.Stored.Name} made from its template ({squad.Members.Count})");
        return squad;
    }

    // ---- nests ----

    /// <summary>The pools of a nest site: the save's nest state's, else (a nest made now) the original's start, 0.7 x <c>nest resident population</c> of it.</summary>
    SitePools NestPools(World world, WildSite site)
    {
        if (nestPools.TryGetValue(site.Index, out var pools)) return pools;
        double now = Hours(world);
        var saved = site.SaveInstance is { } instance ? world.Saved?.Nests.FirstOrDefault(n => n.Instance == instance) : null;
        if (saved is not null) pools = SitePools.Read(saved.State, now);
        else
        {
            int n = site.Nest?.NestResidentPopulation ?? 0;
            pools = new SitePools { Roaming = { Desired = n, Current = settings.NestShare * n, Stamp = now }, Residents = { Stamp = now } };
        }
        nestPools[site.Index] = pools;
        return pools;
    }

    /// <summary>Alive in the state being made (a squad made this tick counts).</summary>
    static bool Living(World world, CharacterId m) =>
        world.Characters.TryResolveNext(m, out int slot) && world.Characters.Next[slot].Alive && world.Characters.Cold(slot) is { DiedTick: < 0 };

    /// <summary>Living members of a nest's squads in the world and of the save's squads held by it (what its pool counts).</summary>
    int NestLiving(World world, int wildSite, SiteState state)
    {
        int n = HoldOf(world, wildSite)?.Members ?? 0;
        foreach (int id in state.Squads)
            if (world.Squads.Find(id) is { } squad)
                foreach (var m in squad.Members)
                    if (Living(world, m)) n++;
        return n;
    }

    /// <summary>The radius round a nest within which a player's character stops it making squads (<c>FUN_1407866d0</c>, 1250).</summary>
    public const float NestGuardRadius = 1250;

    /// <summary>
    /// One nest a tick, in turn over every nest (<c>TownList::vfunc_4</c>'s nest cursor, <b>Verified</b>; the original's frame is the tick here): when loaded and its pool has room it makes
    /// one more squad (built on the population thread): not a wiped-out nest, not one with a player character within <see cref="NestGuardRadius"/> (it is only weighed then, so a nest
    /// left with nothing to make is still found wiped out). The turn is the tick modulo the nest count, so it needs no saved cursor.
    /// </summary>
    void TopUpNest(World world)
    {
        if (wildSites is null) return;
        if (nests is null || nestsOf != wildSites.Count)
        {
            nests = [.. Enumerable.Range(0, wildSites.Count).Where(i => wildSites[i].Kind == WildKind.Nest)];
            nestsOf = wildSites.Count;
        }
        if (nests.Length == 0) return;
        int i = nests[(int)(world.Tick % nests.Length)];
        if (!wildLoaded.Contains(i)) return;
        var site = wildSites[i];
        var state = wildStates[i];
        if (site.Kind != WildKind.Nest || state.Status != SiteStatus.Loaded || state.Pending || !active.Contains(site.Zone)) return;   // held only by a squad away
        var centre = wildCentres[i] ?? site.Spots[0];
        var pools = NestPools(world, site);
        if (pools.Ded) return;
        var players = PlayerSpots(world);
        bool guarded = false;
        foreach (var at in players)
            if (Vector2.DistanceSquared(at, centre) < NestGuardRadius * NestGuardRadius) { guarded = true; break; }
        var budget = new NestBudget((int)MathF.Floor(pools.Roaming.Current), NestLiving(world, i, state), TemplateCounts(world, site, state), state.Made, 4, 1, !guarded, centre);
        if (budget.Members >= budget.Cap)
        {
            // Full: nothing to make. A pool spent below one member with no one left alive can make nothing again: wiped out.
            if (budget.Members == 0) CheckWipe(world, site, state);
            return;
        }
        state.Pending = true;
        long loadId = state.LoadId;
        ulong seed = world.Seed;
        var walk = world.Walkability;
        var spots = players;
        if (work is null) SpawnWild(world, BuildNest(site, loadId, seed, walk, spots, budget));
        else work.Add(() => builtWild.Enqueue(BuildNest(site, loadId, seed, walk, spots, budget)));
    }

    int[]? nests;
    int nestsOf;

    /// <summary>Squads per entry of a nest's <c>roaming squads</c> it has now: in the world (the dead ones too, while they lie there) and the save's held ones.</summary>
    int[] TemplateCounts(World world, WildSite site, SiteState state)
    {
        var entries = site.Nest!.RoamingSquads;
        var counts = new int[entries.Count];
        var hold = HoldOf(world, site.Index);
        for (int i = 0; i < entries.Count; i++)
        {
            counts[i] = hold?.Templates.GetValueOrDefault(entries[i].Id) ?? 0;
            foreach (int id in state.Squads)
                if (world.Squads.Find(id) is { } squad && squad.TemplateId == entries[i].Id && squad.Members.Count > 0) counts[i]++;
        }
        return counts;
    }

    /// <summary>
    /// A nest that could make no squad (no entry fits or weighs anything) and has no squad left standing is wiped out for good (<c>FUN_140927440</c>, <c>Nest::vfunc_103</c>):
    /// <c>ded</c>, the hour as <c>tod</c>, and "{nest} has been wiped out". Engine choice for the original's "intact" here (its rule is Unknown): a squad with someone alive.
    /// </summary>
    void CheckWipe(World world, WildSite site, SiteState state)
    {
        var pools = NestPools(world, site);
        if (pools.Ded) return;
        foreach (int id in state.Squads)
            if (world.Squads.Find(id) is { } squad && squad.Members.Any(m => Living(world, m))) return;
        if (HoldOf(world, site.Index) is { Members: > 0 }) return;
        pools.Ded = true;
        pools.DiedAt = (float)Hours(world);
        world.Post(Loc.Tr("{1} has been wiped out", site.Nest!.Name));
        if (townLog.Count >= 256) townLog.RemoveAt(0);
        townLog.Add($"{site.Nest.Name} (wild site {site.Index}) wiped out");
    }

    /// <summary>
    /// A town's roamer leaves the world for good (<c>FUN_1406bab60</c> -> <c>FUN_140796b30</c>, <b>Verified</b>): one not intact and not regenerating takes its living members out of its
    /// town's roaming pool, only while the pool is above 0 and with no floor. Its faction's prosperity drops by 10 (floor 0) with it, pool or not (<c>FUN_1406bab60</c>; platoon +0xa4
    /// read as the member count).
    /// </summary>
    void PayRoamerDespawn(World world, Platoon p, Squad squad)
    {
        if (Intact(world, p)) return;
        if (squad.Members.Count > 0) LowerProsperity(world, p.Faction, DamagedDespawnProsperity);
        if (p.Regenerate || p.Origin < 0 || p.Origin >= townPools.Length || townPools[p.Origin] is not { } pools) return;
        if (pools.Roaming.Current > 0) pools.Roaming.Current -= squad.Members.Count(m => Living(world, m));
    }

    /// <summary>
    /// A nest's squads leave the world: one that lost someone (engine choice for "not intact") takes its living members out of the pool (<c>FUN_140796b30</c>, the
    /// original's despawn of a damaged squad it does not keep).
    /// </summary>
    void PayNestDespawn(World world, WildSite site, SiteState state)
    {
        if (site.Kind != WildKind.Nest || !nestPools.TryGetValue(site.Index, out var pools)) return;
        foreach (int id in state.Squads)
        {
            if (world.Squads.Find(id) is not { } squad) continue;
            int living = squad.Members.Count(m => Living(world, m));
            if (living < squad.SpawnSize) pools.Roaming.Current = MathF.Max(0, pools.Roaming.Current - living);
        }
    }
}

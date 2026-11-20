using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Population;

/// <summary>
/// A population pool of a town or nest (docs/game/factions-squads-towns.md "Off-screen world"): its desired max (save key <c>popd</c> /
/// <c>popd2</c>), its current max (<c>pop</c> / <c>pop2</c>) and the game hour it last grew at (not saved: the original stamps it at load too).
/// </summary>
public sealed class PoolState
{
    /// <summary>
    /// The desired max (<c>popd</c> / <c>popd2</c>).
    /// </summary>
    public int Desired { get; set; }
    /// <summary>
    /// The current max (<c>pop</c> / <c>pop2</c>).
    /// </summary>
    public float Current { get; set; }
    /// <summary>
    /// The game hour it last grew at (not saved).
    /// </summary>
    public double Stamp { get; set; }

    /// <summary>
    /// The original's growth (<c>FUN_1408f4c20</c>): the current max rises 3600 / 33 x 0.5 x 0.004 = 0.21818 per game hour since the last growth, up
    /// to the desired max, whenever it is looked at.
    /// </summary>
    public void Grow(double nowHours)
    {
        double h = nowHours - Stamp;
        Stamp = nowHours;
        if (h < 0 || Current >= Desired) return;

        Current = MathF.Min(Desired, Current + (float)(h * GrowthPerHour));
    }

    /// <summary>
    /// The current max's growth per game hour (<see cref="Grow"/>).
    /// </summary>
    public const double GrowthPerHour = 3600.0 / 33 * 0.5 * 0.004;
}

/// <summary>
/// A town's or nest's two pools (1 residents, 2 roaming) and its flags: made once (<c>started</c>), wiped out (<c>ded</c>) and when (<c>tod</c>,
/// game hours).
/// </summary>
public sealed class SitePools
{
    /// <summary>
    /// Pool 1, the residents.
    /// </summary>
    public PoolState Residents { get; } = new();
    /// <summary>
    /// Pool 2, the roaming squads.
    /// </summary>
    public PoolState Roaming { get; } = new();
    /// <summary>
    /// <c>started</c>: the site was made once.
    /// </summary>
    public bool Started { get; set; }
    /// <summary>
    /// <c>ded</c>: the site was wiped out.
    /// </summary>
    public bool Ded { get; set; }
    /// <summary>
    /// <c>tod</c>: the game hour it was wiped out.
    /// </summary>
    public float DiedAt { get; set; }

    /// <summary>
    /// Both pools as the original grows them (<c>FUN_1408f6760</c>): residents, then roaming once the residents are within 1 of full. A roaming pool
    /// not grown keeps its stamp, so the time it waited is credited when it grows again (<b>Verified</b>, <c>FUN_1408f4c20</c>).
    /// </summary>
    public void Grow(double nowHours)
    {
        Residents.Grow(nowHours);
        if (Residents.Current >= Residents.Desired - 1) Roaming.Grow(nowHours);
    }

    /// <summary>
    /// Reads a town state's pool keys (<c>pop</c>, <c>popd</c>, <c>pop2</c>, <c>popd2</c>, <c>started</c>, <c>ded</c>, <c>tod</c>), stamped at
    /// <paramref name="nowHours"/>.
    /// </summary>
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

    /// <summary>
    /// Writes the pool keys into a town state (as floats, the save's type), and <c>ded</c>; <c>started</c> is the caller's.
    /// </summary>
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
/// The pools of the towns and nests (docs/simulation.md "Off-screen tier"): grown on the game clock for every town and nest, loaded or not; a town
/// made once makes no residents again; on a town's load its squads that lost at least 60 % are made afresh within its residents' budget.
/// </summary>
public sealed partial class PopulationSystem
{
    #region Pools

    /// <summary>
    /// Each town site's pools, by site (null: not made yet).
    /// </summary>
    SitePools?[] _townPools = [];

    /// <summary>
    /// The game hour of the world clock (ticks since the world began).
    /// </summary>
    static double Hours(World world) => world.Tick * (double)BodySystem.HoursPerTick;

    /// <summary>
    /// Every pool grows (the slow phase, every <see cref="PopulationSettings.RoamCheckTicks"/>).
    /// </summary>
    void GrowPools(World world)
    {
        double now = Hours(world);
        foreach (var p in _townPools) p?.Grow(now);
        foreach (var p in _nestPools.Values) p.Grow(now);
    }

    /// <summary>
    /// Alive in the state being made (a squad made this tick counts).
    /// </summary>
    static bool Living(World world, CharacterId m) =>
        world.Characters.TryResolveNext(m, out int slot) && world.Characters.Next[slot].Alive && world.Characters.Cold(slot) is { DiedTick: < 0 };

    /// <summary>
    /// A death takes one out of the pool its platoon is in (the death function <c>FUN_1407a6200</c> -&gt; <c>FUN_140796a50</c>, <b>Verified</b>):
    /// for a character of a faction that is not the player's, the current max of its town's or nest's pool that lists its platoon drops by 1 while
    /// it is above 0. So a nest whose squads are killed has less to make them again from, and is wiped out once nothing fits. The original has no
    /// off-screen deaths; an off-screen fight's are not charged.
    /// </summary>
    public void Died(World world, in CharacterDeath death)
    {
        if (death.OffScreen || death.Faction == PlayerFactionOf(world)) return;
        if (!world.Characters.TryResolveNext(death.Victim, out int slot) || world.Characters.Cold(slot) is not { } cold) return;
        if (world.Squads.Find(cold.SquadId) is not { } squad) return;

        if (DeathPool(world, squad) is { Current: > 0 } pool) pool.Current -= 1;
    }

    /// <summary>
    /// The pool a death in <paramref name="squad"/> is taken from: its nest's roaming pool, else the pool of its home town its platoon is in
    /// (<see cref="InTownPool"/>); null for a squad in no pool.
    /// </summary>
    PoolState? DeathPool(World world, Squad squad)
    {
        var p = world.Platoons.Find(squad.PlatoonId);
        if (p is { Dead: true }) return null;

        int nest = NestSiteOf(squad, p);
        if (nest >= 0) return NestPools(world, _wildSites![nest]).Roaming;

        if (p is null || p.Origin < 0 || p.Origin >= _townPools.Length || _townPools[p.Origin] is not { } pools) return null;
        if (InTownPool(world, p, p.Origin, residents: true)) return pools.Residents;

        return InTownPool(world, p, p.Origin, residents: false) ? pools.Roaming : null;
    }

    /// <summary>
    /// The wild site of the nest <paramref name="squad"/> belongs to: its save's platoon's nest, else the loaded nest that made it; -1 for none.
    /// </summary>
    int NestSiteOf(Squad squad, Platoon? p)
    {
        if (_wildSites is null) return -1;
        if (p is { Saved: { Wild: WildKind.Nest, WildSite: >= 0 } sp } && sp.WildSite < _wildSites.Count) return sp.WildSite;

        foreach (int i in _wildLoaded)
        {
            if (_wildSites[i].Kind == WildKind.Nest && _wildStates[i].Squads.Contains(squad.Id)) return i;
        }

        return -1;
    }

    #endregion

    #region Towns

    /// <summary>
    /// A town's pools (null: never made, and the save holds none).
    /// </summary>
    public SitePools? PoolsOf(int site)
    {
        if (_townPools.Length != _data.Sites.Count) return null;

        return _townPools[site];
    }

    /// <summary>
    /// The towns' pools from a loaded save's town states, once (towns the save made: <c>started</c> or a pool of their own).
    /// </summary>
    void EnsurePools(World world)
    {
        if (_townPools.Length == _data.Sites.Count) return;

        _townPools = new SitePools?[_data.Sites.Count];
        if (world.Saved is not { } saved) return;

        double now = Hours(world);
        foreach (var site in _data.Sites)
        {
            if (site.InstanceId.Length > 0 && saved.TownStates.TryGetValue(site.InstanceId, out var state)
                && (state.Bools.GetValueOrDefault("started") || state.Floats.GetValueOrDefault("popd") > 0
                    || state.Floats.GetValueOrDefault("popd2") > 0))
                _townPools[site.Index] = SitePools.Read(state, now);
        }
    }

    /// <summary>
    /// A town made: <c>started</c>, and each resident squad's size (one with a home; bar squads are not counted) into the residents' desired and
    /// current max (as the original's resident maker, <c>FUN_14057f820</c>).
    /// </summary>
    void MadeTown(World world, int site, IEnumerable<int> sizes)
    {
        var pools = _townPools[site] ??= new SitePools { Residents = { Stamp = Hours(world) }, Roaming = { Stamp = Hours(world) } };
        pools.Started = true;
        foreach (int n in sizes)
        {
            pools.Residents.Desired += n;
            pools.Residents.Current += n;
        }
    }

    /// <summary>
    /// The town wipe-out gates (<c>TownPopulationManager::vfunc_4</c>, <b>Verified</b>): a roaming pool over this many keeps the town, and so do more
    /// living roamers than <see cref="WipeRoamingLiving"/>.
    /// </summary>
    public const float WipeRoamingPool = 3;

    /// <summary>
    /// The living roamers that keep a town from being wiped out (see <see cref="WipeRoamingPool"/>).
    /// </summary>
    public const int WipeRoamingLiving = 25;

    /// <summary>
    /// The share of its size a squad must have lost to be made afresh at its town's or nest's load (<c>FUN_14092a020</c>, a double, <b>Verified</b>).
    /// </summary>
    public const double RegenerationLoss = 0.6;

    /// <summary>
    /// Town wipe-out (<c>TownPopulationManager::vfunc_4</c> <c>FUN_1408f7a80</c>, docs/game/factions-squads-towns.md "Town wipe"): a started town in
    /// a loaded zone that is not yet <c>ded</c>, whose roaming pool is at most 3 and whose roaming platoons have at most 25 living, and none of whose
    /// resident squads (any with a home; engine reading of the resident pool) is intact (<see cref="ResidentIntact"/>: the platoon's flag, which a
    /// squad with a member held keeps true; a mod's guest counts for neither), is <c>ded</c> for good: it remakes no depleted squads
    /// (<see cref="CheckRegeneration"/>) and its <c>regenerates</c> squads die. Nothing is posted (the town's <c>setDead</c> is empty).
    /// </summary>
    void CheckTownWipes(World world)
    {
        if (_townPools.Length != _data.Sites.Count) return;

        var resident = new bool[_data.Sites.Count];
        var roamers = new int[_data.Sites.Count];
        foreach (var p in world.Platoons.All)
        {
            if (p.Origin < 0 || p.Origin >= resident.Length || p.Wild || p.Guest) continue;

            if (p.Pinned)
            {
                if (p.Stored is { HomeBuilding: null }) continue;

                if (RoamingMilitary(p.TemplateId)) roamers[p.Origin] += LivingOf(world, p);
                else if (ResidentIntact(world, p)) resident[p.Origin] = true;
            }
            else
            {
                roamers[p.Origin] += LivingOf(world, p);
            }
        }

        for (int s = 0; s < _states.Length && s < resident.Length; s++)
        {
            foreach (int id in _states[s].Squads)
            {
                if (world.Squads.Find(id) is not { HomeBuilding: not null } squad) continue;

                if (RoamingMilitary(squad.TemplateId))
                    roamers[s] += squad.PlatoonId < 0 ? squad.Members.Count(m => Living(world, m)) : 0;   // a platoon's own are counted above
                else if ((squad.Ai?.Intact ?? true) && squad.Members.Any(m => Living(world, m))) resident[s] = true;
            }
        }

        for (int s = 0; s < resident.Length; s++)
        {
            // A town coming into the world has neither its squads nor its platoons for a moment: not judged then.
            if (s < _states.Length && (_states[s].Status == SiteStatus.Loading || _states[s].Pending)) continue;

            // Judged only while the zone at the town's position is loaded (zone byte +0xb1, FUN_1408f6760).
            var zone = Meitou.Data.World.WorldLayout.ZoneOf(_data.Sites[s].Position.X, _data.Sites[s].Position.Z);
            if (world.LoadedZones is { } loaded && !loaded.Contains(zone)) continue;

            if (_townPools[s] is not { Started: true, Ded: false } pools || resident[s] || pools.Roaming.Current > WipeRoamingPool
                || roamers[s] > WipeRoamingLiving)
                continue;

            pools.Ded = true;
            pools.DiedAt = (float)Hours(world);
            if (_townLog.Count >= 256) _townLog.RemoveAt(0);
            _townLog.Add($"{_data.Sites[s].Town.Name} wiped out");
        }
    }

    /// <summary>
    /// A template with <c>roaming military</c> is made in squad mode 2 even from a town's resident list (<c>FUN_140583a10</c>, <b>Verified</b>): the
    /// mode only picks the town pool set the platoon is registered in (<c>FUN_1407ec8d0</c>), so it still has its home and stands in the town. Meitou
    /// takes it into account at the town wipe: it is no resident there and counts with the roaming living.
    /// </summary>
    bool RoamingMilitary(string templateId) => _data.Db.Find(templateId)?.GetBool("roaming military") == true;

    /// <summary>
    /// Whether a resident platoon keeps its town from being wiped: <c>Platoon::isIntact</c> (<c>FUN_1407ac7b0</c>, <b>Verified</b>), false once
    /// declared dead, else its flag, which a loaded squad's AI keeps current (<see cref="Ai.Blackboard.Intact"/>, the rule of <c>FUN_1407a5be0</c>)
    /// and an unloaded platoon holds as it was at unload (or in the save). Meitou also needs someone alive in a platoon not in the world: its
    /// off-screen fights can kill members the flag never saw.
    /// </summary>
    bool ResidentIntact(World world, Platoon p)
    {
        if (p.Dead) return false;

        if (p.State == PlatoonState.Loaded && world.Squads.Find(p.SquadId) is { } squad)
            return p.IntactFlag = squad.Ai?.Intact ?? SquadIntact(world, squad);

        return p.IntactFlag && LivingOf(world, p) > 0;
    }

    /// <summary>
    /// SQUAD_TEMPLATE <c>regenerates</c> (<c>Platoon::declareDead</c>, <b>Verified</b>): a squad of it whose last member dies is not declared dead
    /// while its town is not <c>ded</c>; it is made afresh on its next activation.
    /// </summary>
    bool Regenerates(Platoon p) => p.Pinned && TownAlive(p.Origin) && _data.Db.Find(p.TemplateId)?.GetBool("regenerates") == true;

    /// <summary>
    /// Whether a squad of <paramref name="templateId"/> of a living town is made again (its template's <c>regenerates</c>).
    /// </summary>
    internal bool Regenerates(string templateId, int site) => TownAlive(site) && _data.Db.Find(templateId)?.GetBool("regenerates") == true;

    /// <summary>
    /// Whether a squad of <paramref name="templateId"/> of a living nest is made again (its template's <c>regenerates</c>; <c>Platoon::declareDead</c>
    /// asks the platoon's town, a nest's being its nest).
    /// </summary>
    internal bool NestRegenerates(string templateId, int wildSite) =>
        NestPoolsOf(wildSite) is not { Ded: true } && _data.Db.Find(templateId)?.GetBool("regenerates") == true;

    /// <summary>
    /// Whether a town site's pools say it is not wiped out.
    /// </summary>
    bool TownAlive(int site) => site >= 0 && site < _townPools.Length && _townPools[site] is not { Ded: true };

    /// <summary>
    /// The original's regeneration check at a town's load (<c>0x14092a020</c>): each of its resident squads, then each of its roaming ones (in
    /// platoon id order), that lost at least <see cref="RegenerationLoss"/> of its size, while the losses so far fit that pool's current max less
    /// the living (both pools' living, then each mark's losses, counted once for both loops), is marked to be made afresh from its template when it
    /// next comes into the world; a resident one whose template <c>regenerates</c> (platoon <c>+0xd9</c>) is marked with any loss, past both tests.
    /// </summary>
    void CheckRegeneration(World world, int site)
    {
        // A wiped-out town remakes nothing (TownBase::activate runs the pass only while it is not ded).
        if (_townPools[site] is not { Ded: false } pools) return;

        var residents = PoolSquads(world, site, residents: true);
        var roamers = PoolSquads(world, site, residents: false);
        int running = PoolLiving(world, site);
        running = MarkRegeneration(residents, running, pools.Residents.Current, Regenerates);
        MarkRegeneration(roamers, running, pools.Roaming.Current, static _ => false);
    }

    /// <summary>
    /// A town's squads out of the world in one of its pools (<see cref="InTownPool"/>) that the regeneration pass looks at
    /// (<see cref="RegenerationCandidate"/>), with their size and count as the pass reads them (<see cref="PoolSize"/>, <see cref="PoolCount"/>).
    /// </summary>
    List<(Platoon P, int Size, int Living)> PoolSquads(World world, int site, bool residents)
    {
        var squads = new List<(Platoon P, int Size, int Living)>();
        foreach (var p in world.Platoons.All)
        {
            if (!InTownPool(world, p, site, residents) || p.State != PlatoonState.Unloaded || !RegenerationCandidate(p)) continue;

            squads.Add((p, PoolSize(p), PoolCount(world, p)));
        }

        return squads;
    }

    /// <summary>
    /// A platoon's size as the pass reads it (platoon <c>+0xa4</c>): its template's expected size, at least 1, which <c>FUN_1407ed0f0</c> sets for
    /// a new platoon and a save's alike (<c>FUN_1408fe0f0</c>, <see cref="SquadFactory.ExpectedSize"/>; nothing else writes it, <b>Verified</b>).
    /// </summary>
    int PoolSize(Platoon p)
    {
        if (_data.Db.Find(p.TemplateId) is not { } record) return 1;

        return Math.Max(1, SquadFactory.ExpectedSize(SquadTemplate.From(record), _settings.SquadSizeMultiplier));
    }

    /// <summary>
    /// A platoon's count as the pools read it (<c>FUN_140792f90</c>): its living while in the world, else the count it kept (<c>+0xa0</c>): a
    /// save's platoon not in the world since the load its <c>char count</c> (<c>FUN_1407ed0f0</c>; the saved character list, dead included), a
    /// never-made one its size, one that left the world since its living then.
    /// </summary>
    int PoolCount(World world, Platoon p)
    {
        if (p is { State: PlatoonState.Unloaded, Unmade: false, Stored: null, Saved: { StoredTick: < 0 } saved })
            return saved.Source.Record.Ints.GetValueOrDefault("char count", saved.Members.Count);

        return p is { State: PlatoonState.Unloaded, Unmade: false, Stored: null, Saved: { } left } ? SavedLiving(left) : LivingOf(world, p);
    }

    /// <summary>
    /// The living of every member of a town's two pools, where the pass starts its running count (<c>FUN_1408f6a40</c> over pool 2, then pool 1:
    /// each member that resolves, whatever its state).
    /// </summary>
    int PoolLiving(World world, int site)
    {
        int living = 0;
        foreach (var p in world.Platoons.All)
        {
            if (!InTownPool(world, p, site, residents: true) && !InTownPool(world, p, site, residents: false)) continue;

            living += PoolCount(world, p);
        }

        return living;
    }

    /// <summary>
    /// Whether a platoon is in one of a town's pools (<see cref="PoolOf"/>). Joining one has no faction test (<c>FUN_1407ec8d0</c>, <b>Verified</b>):
    /// a platoon joins its home town's pool when its faction and the town's owner are both the player's or both not (faction <c>+0x250</c>), so a
    /// town's squads of other factions (the Hub's Mercenary guild) are members. A declared-dead one is out (<c>declareDead</c> erases it); a
    /// campaign's is left to its campaign, and a mod's guest (<see cref="Platoon.Guest"/>) is not one of its host's.
    /// </summary>
    bool InTownPool(World world, Platoon p, int site, bool residents)
    {
        if (p.Origin != site || p.Dead || p.Wild || p.Guest || p.Campaign is not null) return false;

        int player = PlayerFactionOf(world);
        bool playerTown = player >= 0 && _data.SiteFactions[site] == player;
        if ((player >= 0 && p.Faction == player) != playerTown) return false;

        return PoolOf(p) == (residents ? 1 : 2);
    }

    /// <summary>
    /// A town platoon's pool (platoon <c>+0xa8</c>, saved as <c>sqt</c>): 1 residents and bar squads, 2 roaming. A save's platoon keeps its
    /// <c>sqt</c> (<c>FUN_1407ed0f0</c> sets it through <c>FUN_1409c44b0</c>, which makes 0 into 1 and 3 or more into 2); one the population made is
    /// in pool 1 when it belongs to its town, unless its template is <c>roaming military</c>, which the town's spawn puts in pool 2
    /// (<c>FUN_14057f4c0</c>, <b>Verified</b>; as <c>SaveCapture</c> writes it).
    /// </summary>
    int PoolOf(Platoon p)
    {
        if (p.Saved is not { } saved) return p.Pinned && !RoamingMilitary(p.TemplateId) ? 1 : 2;

        int sqt = saved.Source.Record.Ints.GetValueOrDefault("sqt", 2);
        return sqt >= 2 ? 2 : 1;
    }

    /// <summary>
    /// Marks the squads of a regeneration pass (<c>FUN_14092a020</c>) in order: each that lost something and at least
    /// <see cref="RegenerationLoss"/> of its size while the losses so far fit <paramref name="budget"/> less <paramref name="running"/> (the pools'
    /// living), or that <paramref name="forced"/> passes; each marked adds its losses to the running count, which is returned.
    /// </summary>
    static int MarkRegeneration(List<(Platoon P, int Size, int Living)> squads, int running, float budget, Func<Platoon, bool> forced)
    {
        foreach (var (p, size, living) in squads)
        {
            int lost = size - living;
            if (lost <= 0) continue;
            if ((lost < RegenerationLoss * size || lost > budget - running) && !forced(p)) continue;

            p.Regenerate = true;
            running += lost;
        }

        return running;
    }

    /// <summary>
    /// A town's roaming squad marked by <see cref="CheckRegeneration"/> comes into the world built afresh from its template into the same platoon
    /// (<c>FUN_1404ffad0</c> with +0x118 set deletes its characters and rolls the template), keeping its record (<see cref="TakeRemade"/>). Null,
    /// leaving it as it is, when the template plans no one now.
    /// </summary>
    Squad? RenewRoamer(World world, Platoon p)
    {
        var site = _data.Sites[p.Origin];
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult;
        ReleaseUniques(world, p);
        if (RollTemplate(world, p).Built is not { } built) return null;

        var squad = SpawnSquad(world, built, site, radius, platoonId: p.Id);
        squad.Money = p.Money;
        squad.SpawnSize = built.Plan.TotalMembers;
        MoveOutOfPlayerWalls(world, squad);
        TakeRemade(p, squad, built, null, pinned: false);
        Log($"{site.Town.Name}: {squad.Name} made afresh ({squad.Members.Count})");
        return squad;
    }

    /// <summary>
    /// A platoon made afresh takes its new squad (<c>FUN_1404ffad0</c> builds the template into the same platoon object, so a save writes the squad
    /// into its record, name and handle kept, <c>SaveCapture</c>'s made-from-template path): it holds it as its own with the template's size, and a
    /// save's members are gone (their characters deleted).
    /// </summary>
    static void TakeRemade(Platoon p, Squad squad, BuiltSquad built, TownBuilding? home, bool pinned)
    {
        p.Stored = new StoredSquad { Name = squad.Name, HomeBuilding = home, Pinned = pinned };
        p.Size = built.Plan.TotalMembers;
        p.State = PlatoonState.Loaded;
        p.SquadId = squad.Id;
        p.UnloadAtTick = -1;
        foreach (var m in p.Saved?.Members ?? []) m.Gone = true;
    }

    /// <summary>
    /// Whether the regeneration pass looks at a platoon at all (<c>FUN_14092a020</c>, <b>Verified</b>): not split off another (its <c>separated</c>
    /// hand null, platoon <c>+0xb8</c>) and <c>canref</c> (<c>+0xd8</c>, true unless a save says otherwise).
    /// </summary>
    static bool RegenerationCandidate(Platoon p)
    {
        if (p.SeparatedFrom >= 0 || p.SeparatedFromSquad >= 0) return false;
        if (p.Saved is not { } sp) return true;

        return sp.Source.Record.Bools.GetValueOrDefault("canref", true) && Meitou.Data.Save.Hand.Read(sp.Source.Record, "separated").IsNull;
    }

    /// <summary>
    /// The living members of a save's platoon: not dead in the save nor since, not gone.
    /// </summary>
    static int SavedLiving(Saving.SavedPlatoon saved) =>
        saved.Members.Count(m => !m.Dead && !m.Gone && (m.Position is not null || m.Source.Medical?.Bools.GetValueOrDefault("dead") != true));

    /// <summary>
    /// A town's squad marked for regeneration comes into the world made afresh from its template at its place, homed where it was, into the same
    /// platoon (<see cref="TakeRemade"/>: its record kept, its old characters gone). Returns its squad (null when the template plans no one).
    /// </summary>
    Squad? Regenerate(World world, Platoon p)
    {
        var site = _data.Sites[p.Origin];
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult;
        var home = HomeOf(world, p, site);

        // Its unique members are freed first, so the fresh squad makes them again rather than their replacements.
        ReleaseUniques(world, p);
        var (built, _) = RollTemplate(world, p);
        if (built is null) return null;

        SettleOldShop(world, p, home);
        var squad = SpawnSquad(world, built with { Home = home }, site, radius, platoonId: p.Id, pinned: true);
        squad.Money = p.Money;
        MoveOutOfPlayerWalls(world, squad);
        if (home is not null) world.Fixtures.StampResidents(home, p.TemplateId);
        TakeRemade(p, squad, built, home, pinned: true);
        Log($"{site.Town.Name}: {squad.Name} made afresh ({squad.Members.Count})");
        return squad;
    }

    /// <summary>
    /// The building an unloaded town platoon lives in: the one it was stored with, else its save's <c>homebuilding</c> in <paramref name="site"/>.
    /// </summary>
    static TownBuilding? HomeOf(World world, Platoon p, TownSite site) =>
        p.Stored?.HomeBuilding ?? (p.Saved is { } sp ? Saving.SavedCharacters.SavedHome(world, sp, site) : null);

    /// <summary>
    /// The old squad's building shop (kept when it left the world, else a save's): its containers keep its goods for the fresh one, and an older
    /// save's stock read into its window goes to them.
    /// </summary>
    void SettleOldShop(World world, Platoon p, TownBuilding? home)
    {
        var oldShop = p.Stored?.Shop ?? (p.Saved is { } saved
            ? saved.Shop ?? Saving.ShopSave.Read(world,
                new Squad { Id = -1, TemplateId = p.TemplateId, Faction = p.Faction, Town = p.Origin, HomeBuilding = home }, saved.Source.Record,
                _data)
            : null);
        if (oldShop is null) return;

        world.Fixtures.RegisterSite(world, _data, p.Origin);   // the containers, if the town has not loaded them yet (once)
        Trading.TradeSystem.Settle(world, oldShop);
    }

    /// <summary>
    /// A template whose members are each by chance can plan no one: a few rolls (engine choice). Null when none plans anyone.
    /// </summary>
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
    /// A save's never-made town squad (<see cref="Platoon.Unmade"/>) comes into the world made from its template into the same platoon, as the
    /// original builds the platoon object it has (<c>FUN_1404ffad0</c>, +0x114 set): homed at its <c>homebuilding</c>, it keeps the save's record,
    /// which the next save writes back with its characters and <c>never been activated</c> false (<c>FUN_140372ef0</c>). Returns its squad (null when
    /// the template plans no one).
    /// </summary>
    Squad? MakeUnmade(World world, Platoon p)
    {
        if (p.Origin < 0) return MakeUnmadeWild(world, p);
        var site = _data.Sites[p.Origin];
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult;
        var home = p.Saved is { } sp ? Saving.SavedCharacters.SavedHome(world, sp, site) : null;
        if (RollTemplate(world, p).Built is not { } built) return null;

        var squad = SpawnSquad(world, built with { Home = home }, site, radius, platoonId: p.Id, pinned: true);
        squad.Money = p.Money;
        MoveOutOfPlayerWalls(world, squad);
        if (home is not null) world.Fixtures.StampResidents(home, p.TemplateId);
        p.Stored = new StoredSquad { Name = _data.Db.Find(p.TemplateId)?.Name ?? p.TemplateId, HomeBuilding = home };
        p.Size = built.Plan.TotalMembers;
        p.Unmade = false;

        // Activation clears +0x114 on the platoon itself: once made, no later save rebuilds it from the template, whatever becomes of its squad.
        if (p.Saved is { } made) made.Source.Record.Bools["never been activated"] = false;
        p.State = PlatoonState.Loaded;
        p.SquadId = squad.Id;
        p.UnloadAtTick = -1;
        if (_townLog.Count >= 256) _townLog.RemoveAt(0);
        _townLog.Add($"{site.Town.Name}: {p.Stored.Name} made from its template ({squad.Members.Count})");
        return squad;
    }

    /// <summary>
    /// A save's never-made wild platoon (<see cref="Saving.SaveLoader.UnmadeWild"/>) made from its template into the same platoon where it
    /// was saved, round its nest or (homeless) where it stands. Its generated characters are kept for streaming and written into its original
    /// save record, so neither activation nor a later load rolls its template again. Null when the template plans no one.
    /// </summary>
    Squad? MakeUnmadeWild(World world, Platoon p)
    {
        if (p.Saved is not { } sp || RollTemplate(world, p).Built is not { } built) return null;

        var (centre, homeRadius, nest) = WildHome(sp, p.Position);
        var squad = SpawnSquad(world, built, null, 0, platoonId: p.Id, wild: true);
        squad.HomeCentre = centre;
        squad.HomeRadius = homeRadius;
        BindNest(squad, nest, sp.WildSite);
        squad.Money = p.Money;

        p.Stored = new StoredSquad { Name = squad.Name, Pinned = false };
        p.Size = built.Plan.TotalMembers;
        p.Unmade = false;
        sp.Source.Record.Bools["never been activated"] = false;
        p.State = PlatoonState.Loaded;
        p.SquadId = squad.Id;
        p.UnloadAtTick = -1;
        return squad;
    }

    /// <summary>
    /// A town's roamer leaves the world for good (<c>FUN_1406bab60</c> -&gt; <c>FUN_140796b30</c>, <b>Verified</b>): one not intact and not
    /// regenerating takes its living members out of its town's roaming pool, only while the pool is above 0 and with no floor. Its faction's
    /// prosperity drops by 10 (floor 0) with it, pool or not (<c>FUN_1406bab60</c>, while platoon +0xa4 is above 0: its template's expected size,
    /// <see cref="PoolSize"/>, at least 1 for any template that makes someone).
    /// </summary>
    void PayRoamerDespawn(World world, Platoon p, Squad squad)
    {
        if (Intact(world, p)) return;

        LowerProsperity(world, p.Faction, DamagedDespawnProsperity);
        if (p.Regenerate || p.Origin < 0 || p.Origin >= _townPools.Length || _townPools[p.Origin] is not { } pools) return;

        if (pools.Roaming.Current > 0) pools.Roaming.Current -= squad.Members.Count(m => Living(world, m));
    }

    #endregion

    #region Nests

    /// <summary>
    /// Each nest site's pools, by wild site.
    /// </summary>
    readonly Dictionary<int, SitePools> _nestPools = [];

    /// <summary>
    /// The squared distance within which the camera focus stops a nest making squads (<c>FUN_1408f8740</c>:154-170, 1250 = 35.4 units; before any
    /// candidate is drawn).
    /// </summary>
    public const float NestFocusBlockSquared = 1250;

    /// <summary>
    /// The radius round a nest within which a player's character stops it making squads (<c>FUN_1407866d0</c>, 1250).
    /// </summary>
    public const float NestGuardRadius = 1250;

    /// <summary>
    /// The wild sites that are nests (null: not listed yet; listed again when the wild sites change).
    /// </summary>
    int[]? _nests;
    /// <summary>
    /// The wild site count <see cref="_nests"/> was listed for.
    /// </summary>
    int _nestsOf;

    /// <summary>
    /// A nest site's pools (null: not set up yet).
    /// </summary>
    public SitePools? NestPoolsOf(int wildSite) => _nestPools.GetValueOrDefault(wildSite);

    /// <summary>
    /// The regeneration pass of a nest's load (<c>FUN_14092a020</c>, slot 5 of <c>NestPopulationManager</c> as of the town's manager, run by
    /// <c>TownBase</c>'s activation <c>FUN_14092dbb0</c> for a nest as for a town while it is not <c>ded</c>, <b>Verified</b>): a nest's squads are
    /// all in its roaming pool, whose loop has no <c>regenerates</c> override, so each save's platoon of the nest out of the world (platoon id
    /// order; <see cref="RegenerationCandidate"/>) that lost at least <see cref="RegenerationLoss"/> of its size, while the losses so far fit the
    /// pool's current max less its living, is marked to be made afresh (<see cref="RegenerateWild"/>). Returns the members so marked.
    /// </summary>
    int CheckNestRegeneration(World world, WildSite site, SiteState state)
    {
        var pools = NestPools(world, site);
        if (pools.Ded) return 0;

        var own = new List<(Platoon P, int Size, int Living)>();
        foreach (var p in world.Platoons.All)
        {
            if (p is not { State: PlatoonState.Unloaded, Dead: false, Saved: { Wild: WildKind.Nest } sp } || sp.WildSite != site.Index)
                continue;

            if (RegenerationCandidate(p)) own.Add((p, PoolSize(p), PoolCount(world, p)));
        }

        MarkRegeneration(own, NestLiving(world, site.Index, state), pools.Roaming.Current, static _ => false);
        return own.Where(o => o.P.Regenerate).Sum(o => o.Size - o.Living);
    }

    /// <summary>
    /// A save's wild platoon marked by <see cref="CheckNestRegeneration"/> comes into the world made afresh from its template at its place, round its
    /// nest, into the same platoon (<c>FUN_1404ffad0</c> with +0x118 set deletes the old characters and builds the template;
    /// <see cref="TakeRemade"/>: its record kept). Null, leaving it as it is, when the template plans no one now.
    /// </summary>
    Squad? RegenerateWild(World world, Platoon p)
    {
        if (p.Saved is not { } sp) return null;

        ReleaseUniques(world, p);
        if (RollTemplate(world, p).Built is not { } built) return null;

        var (centre, homeRadius, nest) = WildHome(sp, p.Position);
        var squad = SpawnSquad(world, built, null, 0, platoonId: p.Id, wild: true);
        squad.HomeCentre = centre;
        squad.HomeRadius = homeRadius;
        squad.Money = p.Money;
        BindNest(squad, nest, sp.WildSite);
        TakeRemade(p, squad, built, null, pinned: false);
        Log($"{nest?.Name ?? sp.Name}: {squad.Name} made afresh ({squad.Members.Count})");
        return squad;
    }

    /// <summary>
    /// The pools of a nest site: the save's nest state's, else (a nest made now) the original's start, 0.7 x <c>nest resident population</c> of it.
    /// </summary>
    SitePools NestPools(World world, WildSite site)
    {
        if (_nestPools.TryGetValue(site.Index, out var pools)) return pools;

        double now = Hours(world);
        var saved = site.SaveInstance is { } instance ? world.Saved?.Nests.FirstOrDefault(n => n.Instance == instance) : null;
        if (saved is not null)
        {
            pools = SitePools.Read(saved.State, now);
        }
        else
        {
            int n = site.Nest?.NestResidentPopulation ?? 0;
            pools = new SitePools { Roaming = { Desired = n, Current = _settings.NestShare * n, Stamp = now }, Residents = { Stamp = now } };
        }

        _nestPools[site.Index] = pools;
        return pools;
    }

    /// <summary>
    /// Living members of a nest's squads in the world and of the save's squads held by it (what its pool counts).
    /// </summary>
    int NestLiving(World world, int wildSite, SiteState state)
    {
        int n = HoldOf(world, wildSite)?.Members ?? 0;
        foreach (int id in state.Squads)
        {
            if (world.Squads.Find(id) is { } squad)
            {
                foreach (var m in squad.Members)
                {
                    if (Living(world, m)) n++;
                }
            }
        }

        return n;
    }

    /// <summary>
    /// Whether the camera focus (none: no test) is within sqrt(<see cref="NestFocusBlockSquared"/>) of the nest centre in 3D, the centre taken on
    /// the ground.
    /// </summary>
    bool FocusBlocksNest(World world, Vector2 centre) =>
        _focus is { } f
        && Vector3.DistanceSquared(f, new Vector3(centre.X, world.Walkability.GroundHeight(centre.X, centre.Y), centre.Y)) < NestFocusBlockSquared;

    /// <summary>
    /// One nest a tick, in turn over every nest (<c>TownList::vfunc_4</c>'s nest cursor, <b>Verified</b>; the original's frame is the tick here):
    /// when loaded and its pool has room it makes one more squad (built on the population thread): not a wiped-out nest, not one with a player
    /// character within <see cref="NestGuardRadius"/> (it is only weighed then, so a nest left with nothing to make is still found wiped out). The
    /// turn is the tick modulo the nest count, so it needs no saved cursor.
    /// </summary>
    void TopUpNest(World world)
    {
        if (_wildSites is null) return;

        if (_nests is null || _nestsOf != _wildSites.Count)
        {
            _nests = [.. Enumerable.Range(0, _wildSites.Count).Where(i => _wildSites[i].Kind == WildKind.Nest)];
            _nestsOf = _wildSites.Count;
        }

        if (_nests.Length == 0) return;

        int i = _nests[(int)(world.Tick % _nests.Length)];
        if (!_wildLoaded.Contains(i)) return;

        var site = _wildSites[i];
        var state = _wildStates[i];

        // Held only by a squad away.
        if (site.Kind != WildKind.Nest || state.Status != SiteStatus.Loaded || state.Pending || !_active.Contains(site.Zone)) return;

        var centre = _wildCentres[i] ?? site.Spots[0];
        var pools = NestPools(world, site);
        if (pools.Ded) return;

        var players = PlayerSpots(world);
        bool guarded = FocusBlocksNest(world, centre) || PlayerNear(players, centre, NestGuardRadius);

        var budget = new NestBudget(
            (int)MathF.Floor(pools.Roaming.Current), NestLiving(world, i, state), TemplateCounts(world, site, state), state.Made, 4, 1, !guarded,
            centre);
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
        var regions = RegionsOf(world);
        var spots = players;
        if (_work is null)
            SpawnWild(world, BuildNest(site, loadId, seed, walk, regions, spots, budget));
        else
            _work.Add(() => _builtWild.Enqueue(BuildNest(site, loadId, seed, walk, regions, spots, budget)));
    }

    /// <summary>
    /// Squads per entry of a nest's <c>roaming squads</c> it has now: in the world (the dead ones too, while they lie there) and the save's held ones.
    /// </summary>
    int[] TemplateCounts(World world, WildSite site, SiteState state)
    {
        var entries = site.Nest!.RoamingSquads;
        var counts = new int[entries.Count];
        var hold = HoldOf(world, site.Index);
        for (int i = 0; i < entries.Count; i++)
        {
            counts[i] = hold?.Templates.GetValueOrDefault(entries[i].Id) ?? 0;
            foreach (int id in state.Squads)
            {
                if (world.Squads.Find(id) is { } squad && squad.TemplateId == entries[i].Id && squad.Members.Count > 0) counts[i]++;
            }
        }

        return counts;
    }

    /// <summary>
    /// A squad left the world: no site's squad list names it any more, so the nest wipe test (<see cref="CheckWipe"/>) sees no dangling handle.
    /// </summary>
    internal void ForgetSquad(int id)
    {
        if (_wildStates is null) return;

        foreach (var state in _wildStates) state.Squads.Remove(id);
    }

    /// <summary>
    /// A nest that could make no squad (no entry fits or weighs anything) and has no squad left standing is wiped out for good (<c>FUN_140927440</c>,
    /// <c>Nest::vfunc_103</c>): <c>ded</c>, the hour as <c>tod</c>, and "{nest} has been wiped out". Engine choice for the original's "intact" here
    /// (its rule is Unknown): a squad with someone alive.
    /// </summary>
    void CheckWipe(World world, WildSite site, SiteState state)
    {
        var pools = NestPools(world, site);
        if (pools.Ded) return;

        foreach (int id in state.Squads)
        {
            // A handle that no longer resolves keeps the nest (FUN_1408f8740).
            if (world.Squads.Find(id) is not { } squad || squad.Members.Any(m => Living(world, m))) return;
        }

        if (HoldOf(world, site.Index) is { Members: > 0 }) return;

        pools.Ded = true;
        pools.DiedAt = (float)Hours(world);
        world.Post(Loc.Tr("{1} has been wiped out", site.Nest!.Name));
        if (_townLog.Count >= 256) _townLog.RemoveAt(0);
        _townLog.Add($"{site.Nest.Name} (wild site {site.Index}) wiped out");
    }

    /// <summary>
    /// A wild site's squads leave the world: one that is not intact (the squad-intact rule, <see cref="SquadIntact"/>, <b>Verified</b>) takes its
    /// living members out of the pool while it is above 0, with no floor (<c>FUN_140796b30</c>, the original's despawn of a damaged squad it does not
    /// keep); one whose template <c>regenerates</c> is charged nothing (the same function only marks it to be remade).
    /// </summary>
    void PayNestDespawn(World world, WildSite site, SiteState state)
    {
        _nestPools.TryGetValue(site.Index, out var pools);
        foreach (int id in state.Squads)
        {
            if (world.Squads.Find(id) is not { } squad) continue;
            if (SquadIntact(world, squad)) continue;

            int living = squad.Members.Count(m => Living(world, m));

            // Every non-persistent platoon taken out of the world while not intact lowers its faction's prosperity (FUN_1406bab60, its +0xa4, the
            // template's expected size, above 0), nest squads and homeless ones too.
            LowerProsperity(world, squad.Faction, DamagedDespawnProsperity);
            if (_data.Db.Find(squad.TemplateId)?.GetBool("regenerates") == true) continue;
            if (site.Kind == WildKind.Nest && pools is not null && pools.Roaming.Current > 0) pools.Roaming.Current -= living;
        }
    }

    #endregion
}

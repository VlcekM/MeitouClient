using System.Numerics;

namespace Meitou.Simulation;

/// <summary>Where a roaming squad is in its life (docs/game/game-loop.md "Factions and squads": a platoon is active or unloaded).</summary>
public enum PlatoonState : byte
{
    /// <summary>Only a stand-in moving towards its target: no characters exist (the original's <c>UnloadedPlatoon</c>).</summary>
    Unloaded,
    /// <summary>Its characters are being made (on the build thread); it keeps its place meanwhile.</summary>
    Activating,
    /// <summary>Its characters exist in the world (the original's <c>ActivePlatoon</c>).</summary>
    Loaded,
}

/// <summary>
/// A roaming squad that outlives the loading of its zone: it belongs to a town's roaming pool (<see cref="Origin"/>), travels from
/// town to town, and is only drawn and simulated in detail while a zone near it is active. <see cref="Key"/> and
/// <see cref="TemplateId"/> reproduce the squad's members whenever it is loaded again (the plan is a pure function of them and
/// the world seed). Changed only in serial phases.
/// </summary>
public sealed class Platoon
{
    public required int Id { get; init; }
    /// <summary>The town (a position in the population's site list) whose pool it counts against; -1 for a save's wild platoon.</summary>
    public required int Origin { get; init; }
    public required string TemplateId { get; init; }
    public int Faction { get; init; } = -1;
    public required ulong Key { get; init; }
    /// <summary>Members when made: what the pool counts (a save's never-made platoon: its <c>char count</c> until it is made, <see cref="Unmade"/>).</summary>
    public int Size { get; set; }
    public Vector2 Position { get; set; }
    /// <summary>The town it is travelling to (-1: none chosen, it waits).</summary>
    public int Target { get; set; } = -1;
    /// <summary>It is on its way back to <see cref="Origin"/> (else out to another town).</summary>
    public bool GoingHome { get; set; }
    /// <summary>Seconds still to wait at the town it reached.</summary>
    public float WaitLeft { get; set; }
    /// <summary>The purse while the squad is not loaded (<see cref="Squad.Money"/> while it is; save key <c>money</c>).</summary>
    public long Money { get; set; }
    public PlatoonState State { get; set; }
    /// <summary>The squad object while loaded (-1 otherwise).</summary>
    public int SquadId { get; set; } = -1;
    /// <summary>The tick at which a loaded platoon far from every active zone is turned into a stand-in (-1: not counting down).</summary>
    public long UnloadAtTick { get; set; } = -1;
    /// <summary>Seconds before a loaded platoon's leader is sent on again when it has no go-to (a blocked path must not be retried every look).</summary>
    public float OrderCooldown { get; set; }
    /// <summary>The AI package it runs (an AI_PACKAGE id, saved as <c>currentPackage</c>; null: none) and the off-screen job that package gives (<see cref="Ai.PlatoonJobs"/>).</summary>
    public string? Package { get; set; }
    public Ai.PlatoonJob Job { get; set; }
    /// <summary>A short-range patrol's next point (null: none; it goes to <see cref="Target"/> instead).</summary>
    public Vector2? Point { get; set; }
    /// <summary>The save's platoon this stand-in is (null: one the population made): its characters are made from the save, not from <see cref="Key"/> (a never-made one's from its template, <see cref="Unmade"/>).</summary>
    public Saving.SavedPlatoon? Saved { get; init; }
    /// <summary>
    /// A squad the population made, kept as its characters while it is not in the world (null: none): a town's squad since its town unloaded, or (the Meitou
    /// <see cref="PopulationSystem.KeepRoamers"/> switch) a roaming one since it first left. Made of them again, never rolled anew. Set with <see cref="Saved"/> on a save's
    /// never-made platoon once made (<see cref="Unmade"/>): this holds its squad, the save's record is written with it.
    /// </summary>
    public StoredSquad? Stored { get; set; }
    /// <summary>A platoon of its town's own (residents, bar squads, slaves; saved or stored): it never travels, its squad is run by the AI like the town's, and it is not in the roaming pool.</summary>
    public bool Pinned => Saved?.Pinned == true || Stored?.Pinned == true;
    /// <summary>A save's wild platoon (a nest's or a homeless one, <see cref="Origin"/> -1): it never travels and counts against its wild site, not a town's pool.</summary>
    public bool Wild => Saved?.Wild is not null;
    /// <summary>Game seconds it has been a stand-in with no reason to be kept (the Faithful <see cref="PopulationSystem.KeepRoamers"/>: the original's orphan timer).</summary>
    public float OrphanSeconds { get; set; }
    /// <summary>A town's own squad that lost so many that its town makes it afresh when it next comes into the world (<see cref="PopulationSystem"/> regeneration).</summary>
    public bool Regenerate { get; set; }
    /// <summary>
    /// A save's town squad the game never made (<see cref="Saving.SaveLoader"/> "unmade": no file, <c>never been activated</c>, a <c>char count</c> and a home building):
    /// a stand-in made from its template when it activates (<c>FUN_1404ffad0</c>), into this same platoon: it then keeps the save's record (<see cref="Saved"/>, no members)
    /// and holds its squad as a population's own (<see cref="Stored"/>), and this is cleared. Not saved.
    /// </summary>
    public bool Unmade { get; set; }
    /// <summary>
    /// The faction campaign it marches in (null: none; <c>PopulationCampaigns.cs</c>): kept (never despawned) until the campaign is removed, moved by the campaign off
    /// screen and led to <see cref="CampaignGoal"/> while loaded, out of the roaming pools.
    /// </summary>
    public CampaignInstance? Campaign { get; set; }
    /// <summary>The platoon's intact flag (+0x100, <c>FUN_1407ac7b0</c>): the squad-intact rule, refreshed while loaded and kept as it was at unload; saved as <c>intact</c> (<b>Verified</b>, <c>FUN_1407ed0f0</c>). Hashed while false.</summary>
    public bool IntactFlag { get; set; } = true;
    /// <summary>
    /// The platoon was declared dead (+0x1f0, <c>Platoon::declareDead</c> <c>FUN_140797090</c>, <b>Verified</b>; saved as <c>dead</c>): every member is a dead character. It is out of its
    /// town's pools and never intact, and no longer kept: a loaded one is destroyed when it unloads (a campaign's stays with its campaign). Hashed while set.
    /// </summary>
    public bool Dead { get; set; }
    /// <summary>
    /// The platoon's <c>imprisoned</c> (+0x1f1, <b>Verified</b>, <c>FUN_1407ef4b0</c>): set at each unload to whether a member is held in a cage or restraint, else the save's value;
    /// while set its stand-in neither travels nor takes a job (<c>FUN_1407ee790</c>).
    /// </summary>
    public bool Imprisoned { get; set; }
    /// <summary>
    /// The unloaded platoon's activation dwell (+0x78, <c>FUN_1407ebf30</c>, <b>Verified</b>): 5 s when it becomes unloaded, 2 s again whenever it stands outside its zone's
    /// activation box; inside, it counts down and the platoon activates once it is below 0. Not saved (a loaded platoon starts at 5).
    /// </summary>
    public float DwellLeft { get; set; } = 5;
    /// <summary>Where its campaign sends it (the target for the leader platoon, the leader for the others; set each population look).</summary>
    public Vector2? CampaignGoal { get; set; }
    /// <summary>The roads it takes off screen to <see cref="RouteGoal"/> (null: straight there; <see cref="PopulationSystem"/> roads), and the next point of it. Not saved: worked out again.</summary>
    public List<Vector2>? Route { get; set; }
    public int RouteStep { get; set; }
    public Vector2? RouteGoal { get; set; }
}

/// <summary>
/// A squad the population made, kept while it is not in the world (docs/simulation.md "Population"): each member's own <see cref="CharacterCold"/> (name, look, body,
/// kit, bounties) with where it stood, and the squad's home, shop and AI package. Its platoon is made of them again; a save writes them as the platoon.
/// </summary>
public sealed class StoredSquad
{
    public required string Name { get; init; }
    /// <summary>A town's own squad (else a roaming one, which travels).</summary>
    public bool Pinned { get; init; } = true;
    public Meitou.Data.World.TownBuilding? HomeBuilding { get; init; }
    public Trading.Shop? Shop { get; set; }
    /// <summary>The squad's <see cref="Squad.NextHomeFillTick"/>, so an emptied home is not refilled when it comes back.</summary>
    public long NextHomeFillTick { get; set; }
    /// <summary>The AI package it ran (saved as <c>currentPackage</c>; null: none).</summary>
    public string? Package { get; set; }
    /// <summary>Its contract and <c>change AI</c> (<see cref="Saving.AiSave"/>; null: none).</summary>
    public Saving.AiSave? Ai { get; set; }
    /// <summary>What the squad remembered of characters when it left the world (the original keeps it on the platoon; <see cref="Dialogue.SquadMemory.Take"/>); null: nothing.</summary>
    public Dialogue.SquadMemoryData? Memory { get; set; }
    /// <summary>The platoon position the members' places belong to (a stand-in that travelled since takes them along), and the tick they were stored at.</summary>
    public Vector2 Anchor { get; set; }
    public long StoredTick { get; set; }
    /// <summary>The game hours (<see cref="World.GameHours"/>) they were stored at (0 for a save's: not saved), for the residents' schedule.</summary>
    public double StoredHours { get; set; }
    /// <summary>The characters while the platoon is not in the world (empty while it is).</summary>
    public List<StoredMember> Members { get; } = [];
}

/// <summary>A character of a <see cref="StoredSquad"/>: its cold state, cleared of what only meant something in the world, and its place.</summary>
public sealed record StoredMember(CharacterCold Cold, Vector3 Position, float Yaw, bool Leader);

/// <summary>The roaming squads of a world, by id. Ids are never reused. Part of the state hash.</summary>
public sealed class PlatoonRegistry
{
    readonly SortedDictionary<int, Platoon> platoons = [];
    int nextId;
    // The ids of a save's never-made town squads (Platoon.Unmade), counted down from the top so that they leave every other
    // platoon's id (and what is rolled from it) as it would be without them, and come last in id order.
    int nextReserved = int.MaxValue;

    public int Count => platoons.Count;
    public IEnumerable<Platoon> All => platoons.Values;
    public Platoon? Find(int id) => platoons.GetValueOrDefault(id);
    public int NextId() => nextId++;
    public int NextReservedId() => nextReserved--;
    public void Add(Platoon p) => platoons[p.Id] = p;
    public bool Remove(int id) => platoons.Remove(id);

    internal void Hash(ref StateHasher h)
    {
        h.Add(platoons.Count);
        h.Add(nextId);
        if (nextReserved != int.MaxValue) h.Add(nextReserved);   // only once used: a world without unmade squads hashes as before
        foreach (var p in platoons.Values)
        {
            h.Add(p.Id);
            h.Add(p.Origin);
            h.Add(p.Faction);
            h.Add(p.Key);
            h.Add(p.Size);
            h.Add(p.Money);
            h.Add(p.Position);
            h.Add(p.Target);
            h.Add(p.GoingHome);
            h.Add(p.WaitLeft);
            h.Add((int)p.State);
            h.Add(p.SquadId);
            h.Add(p.UnloadAtTick);
            h.Add(p.OrderCooldown);
            if (p.Job != Ai.PlatoonJob.None || p.Point is not null)
            {
                // Only for platoons whose package gives an off-screen job, so worlds without AI packages hash as before.
                h.Add((int)p.Job);
                h.Add(p.Point.HasValue);
                h.Add(p.Point ?? default);
            }
            if (p.Stored is { } stored)
            {
                // Only for stored squads, so worlds without them hash as before.
                h.Add(stored.Members.Count);
                foreach (var m in stored.Members) h.Add(m.Position);
                h.Add(stored.Anchor);
                h.Add(stored.Pinned);
            }
            if (!p.IntactFlag) h.Add(0x496E7463);   // likewise while not intact
            if (p.Dead) h.Add(0x44656164);   // only once declared dead, so worlds without one hash as before
            if (p.OrphanSeconds > 0) h.Add(p.OrphanSeconds);   // only while counting, so worlds without it hash as before
            if (p.Campaign is { } campaign)
            {
                // Only for campaign platoons, so worlds without them hash as before.
                h.Add(campaign.Faction);
                h.Add(campaign.Id);
                h.Add(p.CampaignGoal ?? default);
            }
            if (p.Saved is { } saved)
            {
                // Only for a save's platoons, so worlds without them hash as before.
                h.Add(saved.Pinned);
                h.Add(saved.Touched);
            }
        }
    }
}

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
    /// <summary>The town (a position in the population's site list) whose pool it counts against.</summary>
    public required int Origin { get; init; }
    public required string TemplateId { get; init; }
    public int Faction { get; init; } = -1;
    public required ulong Key { get; init; }
    /// <summary>Members when made: what the pool counts.</summary>
    public int Size { get; init; }
    public Vector2 Position { get; set; }
    /// <summary>The town it is travelling to (-1: none chosen, it waits).</summary>
    public int Target { get; set; } = -1;
    /// <summary>It is on its way back to <see cref="Origin"/> (else out to another town).</summary>
    public bool GoingHome { get; set; }
    /// <summary>Seconds still to wait at the town it reached.</summary>
    public float WaitLeft { get; set; }
    public PlatoonState State { get; set; }
    /// <summary>The squad object while loaded (-1 otherwise).</summary>
    public int SquadId { get; set; } = -1;
    /// <summary>The tick at which a loaded platoon far from every active zone is turned into a stand-in (-1: not counting down).</summary>
    public long UnloadAtTick { get; set; } = -1;
    /// <summary>Seconds before a loaded platoon's leader is sent on again when it has no go-to (a blocked path must not be retried every look).</summary>
    public float OrderCooldown { get; set; }
}

/// <summary>The roaming squads of a world, by id. Ids are never reused. Part of the state hash.</summary>
public sealed class PlatoonRegistry
{
    readonly SortedDictionary<int, Platoon> platoons = [];
    int nextId;

    public int Count => platoons.Count;
    public IEnumerable<Platoon> All => platoons.Values;
    public Platoon? Find(int id) => platoons.GetValueOrDefault(id);
    public int NextId() => nextId++;
    public void Add(Platoon p) => platoons[p.Id] = p;
    public bool Remove(int id) => platoons.Remove(id);

    internal void Hash(ref StateHasher h)
    {
        h.Add(platoons.Count);
        h.Add(nextId);
        foreach (var p in platoons.Values)
        {
            h.Add(p.Id);
            h.Add(p.Origin);
            h.Add(p.Faction);
            h.Add(p.Key);
            h.Add(p.Size);
            h.Add(p.Position);
            h.Add(p.Target);
            h.Add(p.GoingHome);
            h.Add(p.WaitLeft);
            h.Add((int)p.State);
            h.Add(p.SquadId);
            h.Add(p.UnloadAtTick);
            h.Add(p.OrderCooldown);
        }
    }
}

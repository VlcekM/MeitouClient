using System.Numerics;

namespace Meitou.Simulation;

/// <summary>The editor's SquadMemberType, the role a member plays in its squad (docs/game/factions-squads-towns.md section 5.1).</summary>
public enum SquadRole { Squad1 = 0, Squad2 = 1, Leader = 2, Slave = 4 }

/// <summary>
/// A squad in the world: the platoon of docs/game/game-loop.md "Factions and squads", reduced to what loaded squads need now (the
/// active half; the unloaded half and the AI packages come with the AI stage). Plain object, changed only in the serial phases.
/// </summary>
public sealed class Squad
{
    public required int Id { get; init; }
    /// <summary>The SQUAD_TEMPLATE it was made from.</summary>
    public required string TemplateId { get; init; }
    public string Name { get; init; } = "";
    /// <summary>The owner faction, a position in the world's faction list (-1: none).</summary>
    public int Faction { get; init; } = -1;
    /// <summary>The town it belongs to (a position in the population's site list; -1: none).</summary>
    public int Town { get; init; } = -1;
    /// <summary>Where the squad was created (members stand at offsets from it).</summary>
    public Vector3 Position { get; init; }
    /// <summary>The place and radius the squad wanders in when it has no orders (the town's centre and radius for residents).</summary>
    public Vector2 HomeCentre { get; init; }
    public float HomeRadius { get; init; }
    public List<CharacterId> Members { get; } = [];
    /// <summary>The member the others follow (the role-2 leader, else the first member); <see cref="CharacterId.None"/> when none is alive.</summary>
    public CharacterId Leader { get; set; } = CharacterId.None;
}

/// <summary>The squads of a world, by id. Ids are never reused.</summary>
public sealed class SquadRegistry
{
    readonly SortedDictionary<int, Squad> squads = [];
    int nextId;

    public int Count => squads.Count;
    public IEnumerable<Squad> All => squads.Values;
    public Squad? Find(int id) => squads.GetValueOrDefault(id);

    public int NextId() => nextId++;

    public void Add(Squad squad) => squads[squad.Id] = squad;

    public bool Remove(int id) => squads.Remove(id);

    /// <summary>After deaths and removals: drops dead members, and picks a new leader (the first member alive) when the old one is gone. Serial phases only.</summary>
    public void Refresh(CharacterTable table)
    {
        foreach (var squad in squads.Values)
        {
            squad.Members.RemoveAll(m => !table.TryResolveNext(m, out _));
            if (squad.Leader.IsNone || !table.TryResolveNext(squad.Leader, out _))
                squad.Leader = squad.Members.Count > 0 ? squad.Members[0] : CharacterId.None;
        }
    }

    internal void Hash(ref StateHasher hasher)
    {
        hasher.Add(squads.Count);
        hasher.Add(nextId);
        foreach (var s in squads.Values)
        {
            hasher.Add(s.Id);
            hasher.Add(s.Faction);
            hasher.Add(s.Town);
            hasher.Add(s.Position);
            hasher.Add(s.Leader.Slot);
            hasher.Add(s.Leader.Generation);
            hasher.Add(s.Members.Count);
            foreach (var m in s.Members)
            {
                hasher.Add(m.Slot);
                hasher.Add(m.Generation);
            }
        }
    }
}

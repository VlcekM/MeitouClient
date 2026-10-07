using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Simulation;

/// <summary>
/// The state every tick touches for one character, in a dense struct array (docs/simulation.md, "World model"). Plain data, no
/// references, so the table copies it by block and the state hash reads it by value. What the fields mean is up to the systems;
/// the stand-in movement uses position, velocity and yaw.
/// </summary>
public struct CharacterHot
{
    /// <summary>The slot holds a character. A dead slot's other fields are stale.</summary>
    public bool Alive;
    /// <summary>The slot's generation: raised each time the slot is used again, so an old <see cref="CharacterId"/> never names the new character.</summary>
    public int Generation;
    /// <summary>World position: X/Z on the map, Y the height the character stands at.</summary>
    public Vector3 Position;
    public Vector3 Velocity;
    /// <summary>Heading in radians, 0 along +Z, turning towards +X (the renderer's convention: yaw of <c>atan2(x, z)</c>).</summary>
    public float Yaw;
    /// <summary>Movement mode (walk, run, ...); the meaning is the movement system's.</summary>
    public byte Mode;
    public ushort Flags;
    /// <summary>Where the character is going (X/Z).</summary>
    public Vector2 Goal;
    /// <summary>Cursor into the character's path (the path itself is cold state).</summary>
    public int PathCursor;
    /// <summary>Animation state and its time in seconds.</summary>
    public ushort Animation;
    public float AnimationTime;
    public float Health;
    /// <summary>What the character is doing (a <see cref="CharacterTask"/>) and the time in seconds that task keeps (a wait, a repath cooldown).</summary>
    public byte Task;
    public float TaskTime;
    /// <summary>The speed stat S in world units (decimetres) per second, and the race walk speed (the cap of speed mode 0).</summary>
    public float MaxSpeed;
    public float WalkSpeed;
}

/// <summary>The rest of a character: things few systems touch per tick (stats, body parts, inventory, AI blackboard come here as they are built).</summary>
public sealed class CharacterCold
{
    public string Name { get; set; } = "";
    public int Faction { get; set; }
    /// <summary>What the renderer needs to draw the character; a character without one is simulated but not published in snapshots.</summary>
    public CharacterAppearance? Appearance { get; set; }
    /// <summary>The tick the character appeared at.</summary>
    public long SpawnedTick { get; set; }
    /// <summary>The CHARACTER (or ANIMAL_CHARACTER) record it was made from.</summary>
    public string RecordId { get; set; } = "";
    /// <summary>The squad it belongs to (-1 for none), its role in it (the editor's SquadMemberType) and where it stands relative to the leader.</summary>
    public int SquadId { get; set; } = -1;
    public int Role { get; set; }
    public System.Numerics.Vector2 FormationOffset { get; set; }
    /// <summary>The path being followed (world positions, the next one at <c>CharacterHot.PathCursor</c>); replaced whole in a serial phase.</summary>
    public Vector3[] Path { get; set; } = [];
    /// <summary>The path request in flight, so an answer to an older one is ignored.</summary>
    public long PathRequest { get; set; }
    /// <summary>Belongs to the player's faction: can be selected and ordered.</summary>
    public bool IsPlayer { get; set; }
    /// <summary>Queued move orders (X/Z) after the current one (shift + right click); changed only by the character's own partition or a serial phase.</summary>
    public List<System.Numerics.Vector2> OrderQueue { get; } = [];
    /// <summary>Animation layers in play (made by <see cref="AnimationSystem"/>; null before the first tick it runs).</summary>
    public CharacterAnimation? Animation { get; set; }
    /// <summary>The kind of weapon drawn in the right hand (None while sheathed): the stance animations are chosen for.</summary>
    public Meitou.Data.Gameplay.WeaponKinds DrawnWeapon { get; set; }
    /// <summary>Where the character came from in a loaded save, with the saved stats and medical state (null for a character the game made itself); see <c>Meitou.Simulation.Saving</c>.</summary>
    public Saving.SavedCharacterLink? Save { get; set; }
    /// <summary>RACE <c>pathfind footprint radius</c> (0: not known, paths use the default human); what the navmesh keeps clear.</summary>
    public float FootprintRadius { get; set; }
}

/// <summary>
/// The characters of a world: <see cref="CharacterHot"/> structs in two dense arrays, <see cref="Previous"/> (the last tick's
/// result, which every phase reads, other characters' included) and <see cref="Next"/> (the tick being computed, where a phase
/// writes only its own characters), plus a <see cref="CharacterCold"/> object per slot. A tick starts with <see cref="BeginTick"/>
/// (Next becomes a copy of Previous) and ends with <see cref="EndTick"/> (the two swap). Slots are reused, the most recently freed
/// first, with the generation raised; spawning and removing happen only in the serial phases, in the order the commit sorts them,
/// which is what keeps slot assignment independent of the thread count.
/// </summary>
public sealed class CharacterTable
{
    CharacterHot[] previous;
    CharacterHot[] next;
    CharacterCold?[] cold;
    readonly Stack<int> free = [];
    int highWater;
    bool inTick;
    TablePhase phase = TablePhase.Outside;

    public CharacterTable(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        previous = new CharacterHot[capacity];
        next = new CharacterHot[capacity];
        cold = new CharacterCold?[capacity];
    }

    /// <summary>Slots ever used: every character is in 0..HighWater-1.</summary>
    public int HighWater => highWater;
    /// <summary>Characters alive.</summary>
    public int Count => highWater - free.Count;

    /// <summary>The last tick's state of the slots 0..HighWater-1.</summary>
    public ReadOnlySpan<CharacterHot> Previous => previous.AsSpan(0, highWater);
    /// <summary>The state being computed, same slots. A phase writes only the slots of its own partition.</summary>
    public Span<CharacterHot> Next => next.AsSpan(0, highWater);

    public CharacterCold? Cold(int slot) => cold[slot];

    /// <summary>The id of the character in <paramref name="slot"/> as of the last tick.</summary>
    public CharacterId IdOf(int slot) => new(slot, previous[slot].Generation);

    /// <summary>Whether <paramref name="id"/> names a character alive as of the last tick.</summary>
    public bool IsAlive(CharacterId id) =>
        id.Slot >= 0 && id.Slot < highWater && previous[id.Slot].Alive && previous[id.Slot].Generation == id.Generation;

    /// <summary>Finds the slot of <paramref name="id"/> in the state being computed (for the commit, which sees removals and spawns of this tick).</summary>
    public bool TryResolveNext(CharacterId id, out int slot)
    {
        slot = id.Slot;
        return slot >= 0 && slot < highWater && next[slot].Alive && next[slot].Generation == id.Generation;
    }

    internal TablePhase Phase
    {
        get => phase;
        set => phase = value;
    }

    internal void BeginTick()
    {
        Array.Copy(previous, next, highWater);
        inTick = true;
    }

    internal void EndTick()
    {
        (previous, next) = (next, previous);
        inTick = false;
    }

    /// <summary>
    /// Adds a character; call only from the serial phases (inputs, commit, slow world) or before the first tick. The slot is the
    /// most recently freed one, else a new one. The generation and <c>Alive</c> in <paramref name="state"/> are set here.
    /// </summary>
    public CharacterId Spawn(CharacterHot state, CharacterCold cold, long tick)
    {
        ArgumentNullException.ThrowIfNull(cold);
        if (phase == TablePhase.Parallel) throw new InvalidOperationException("characters are spawned in the serial phases only");
        int slot;
        int generation;
        if (free.Count > 0)
        {
            slot = free.Pop();
            generation = next[slot].Generation + 1;
        }
        else
        {
            slot = highWater;
            if (slot == previous.Length) Grow();
            highWater++;
            generation = 0;
        }
        state.Alive = true;
        state.Generation = generation;
        cold.SpawnedTick = tick;
        next[slot] = state;
        // Outside a tick there is no "last tick" to keep apart from the new one.
        if (!inTick) previous[slot] = state;
        this.cold[slot] = cold;
        return new CharacterId(slot, generation);
    }

    /// <summary>Removes a character in the state being computed; false when <paramref name="id"/> is not alive there. Serial phases only.</summary>
    public bool Remove(CharacterId id)
    {
        if (phase is TablePhase.Parallel or TablePhase.Inputs or TablePhase.Schedule)
            throw new InvalidOperationException("characters are removed in the commit or the slow world only (a phase reads them as alive)");
        if (!TryResolveNext(id, out int slot)) return false;
        next[slot].Alive = false;
        if (!inTick) previous[slot].Alive = false;
        cold[slot] = null;
        free.Push(slot);
        return true;
    }

    void Grow()
    {
        int size = previous.Length * 2;
        Array.Resize(ref previous, size);
        Array.Resize(ref next, size);
        Array.Resize(ref cold, size);
    }

    /// <summary>Adds the table's canonical state, in slot order, to <paramref name="hasher"/>: every slot's state, the free list in reuse order and the cold fields that matter.</summary>
    internal void Hash(ref StateHasher hasher)
    {
        hasher.Add(highWater);
        for (int i = 0; i < highWater; i++)
        {
            ref readonly var c = ref previous[i];
            hasher.Add(c.Alive);
            hasher.Add(c.Generation);
            if (!c.Alive) continue;
            hasher.Add(c.Position);
            hasher.Add(c.Velocity);
            hasher.Add(c.Yaw);
            hasher.Add((int)c.Mode);
            hasher.Add((int)c.Flags);
            hasher.Add(c.Goal);
            hasher.Add(c.PathCursor);
            hasher.Add((int)c.Animation);
            hasher.Add(c.AnimationTime);
            hasher.Add(c.Health);
            hasher.Add((int)c.Task);
            hasher.Add(c.TaskTime);
            hasher.Add(c.MaxSpeed);
            hasher.Add(c.WalkSpeed);
            var k = cold[i]!;
            hasher.Add(k.Faction);
            hasher.Add(k.SpawnedTick);
            hasher.Add(k.SquadId);
            hasher.Add(k.Role);
            hasher.Add(k.FormationOffset);
            hasher.Add(k.Path.Length);
            hasher.Add(k.PathRequest);
            hasher.Add(k.IsPlayer);
            hasher.Add(k.OrderQueue.Count);
            foreach (var q in k.OrderQueue) hasher.Add(q);
            hasher.Add((int)k.DrawnWeapon);
            if (k.Animation is { } anim) anim.Hash(ref hasher); else hasher.Add(-1);
            hasher.Add(k.Name.Length);
        }
        hasher.Add(free.Count);
        foreach (int slot in free) hasher.Add(slot);
    }
}

/// <summary>Where in a tick the table is, so a spawn or removal in the wrong place fails loudly instead of corrupting a tick.</summary>
public enum TablePhase
{
    /// <summary>Between ticks (setup): anything goes.</summary>
    Outside,
    /// <summary>Spawns allowed; removals not (the phases that follow read the character as alive).</summary>
    Inputs,
    Schedule,
    /// <summary>Think, move, act: no spawns or removals.</summary>
    Parallel,
    Commit,
    SlowWorld,
}

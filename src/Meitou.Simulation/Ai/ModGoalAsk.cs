using Meitou.Data;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// One character and a goal of a mod's type as the chooser asks about it, in parallel with other asks (<see cref="IModGoal.Desire"/>): a read-only
/// view that offers only what is safe to read there, the asker's own state, other characters' state as of the last finished tick, its squad, faction
/// relations, the time and the game data, as values. A <c>ref struct</c>: it lives for the one call and cannot be kept or captured.
/// </summary>
public readonly ref struct ModGoalAsk
{
    /// <summary>
    /// The world being thought for.
    /// </summary>
    readonly World _world;

    /// <summary>
    /// The population data (faction relations, game data).
    /// </summary>
    readonly PopulationData _data;

    /// <summary>
    /// The asker's cold state.
    /// </summary>
    readonly CharacterCold _cold;

    /// <summary>
    /// The asker's squad.
    /// </summary>
    readonly Squad _squad;

    /// <summary>
    /// The ask of the character at <paramref name="slot"/> about goal <paramref name="type"/>.
    /// </summary>
    internal ModGoalAsk(World world, PopulationData data, CharacterCold cold, Squad squad, int slot, CharacterId id, TaskType type,
        TaskTargetType targeting)
    {
        (_world, _data, _cold, _squad) = (world, data, cold, squad);
        (Slot, Id, Type, Targeting) = (slot, id, type, targeting);
    }

    /// <summary>
    /// The asker's slot.
    /// </summary>
    public int Slot { get; }

    /// <summary>
    /// The asker's id.
    /// </summary>
    public CharacterId Id { get; }

    /// <summary>
    /// The goal type asked about.
    /// </summary>
    public TaskType Type { get; }

    /// <summary>
    /// The goal record's <c>targeting</c>.
    /// </summary>
    public TaskTargetType Targeting { get; }

    /// <summary>
    /// The asker's own state in the tick being computed (position, task, alive), read only.
    /// </summary>
    public ref readonly CharacterHot Self => ref _world.Characters.Next[Slot];

    /// <summary>
    /// The asker's faction.
    /// </summary>
    public int Faction => _cold.Faction;

    /// <summary>
    /// The role list the asker takes in its squad (<see cref="AiSystem.RoleOf"/>).
    /// </summary>
    public AiRole Role => AiSystem.RoleOf(_squad, Id, _cold);

    /// <summary>
    /// Whether the asker is of the player's faction (a player-faction mind, <see cref="AiMind.PlayerSide"/>).
    /// </summary>
    public bool PlayerSide => _cold.Ai is { PlayerSide: true };

    /// <summary>
    /// Whether the asker is an animal.
    /// </summary>
    public bool IsAnimal => _cold.Fighter is { IsAnimal: true };

    /// <summary>
    /// The character or body the asker carries (<see cref="CharacterId.None"/>: none).
    /// </summary>
    public CharacterId Carrying => _cold.Carrying;

    /// <summary>
    /// Whether the asker is in a fight.
    /// </summary>
    public bool InCombat => _cold.InCombat;

    /// <summary>
    /// The asker's standing orders (hold, sneak, jobs and the rest).
    /// </summary>
    public CharacterOrders Orders => _cold.Orders;

    /// <summary>
    /// The row of the task the asker runs (<see cref="TaskType.None"/>: none).
    /// </summary>
    public TaskType RunningRow => _cold.Running?.Row ?? TaskType.None;

    /// <summary>
    /// Whether this goal holds the asker now (its act-out runs): the chooser then weighs it with the running goal's continuity.
    /// </summary>
    public bool Running => _cold.Ai?.Duty is { } duty && duty.Task.Type == Type;

    /// <summary>
    /// The state of <paramref name="who"/> as of the last finished tick (position, task, alive); false when it is not in the world then.
    /// </summary>
    public bool TryPrevious(CharacterId who, out CharacterHot state)
    {
        var table = _world.Characters;
        bool alive = table.IsAlive(who);
        state = alive ? table.Previous[who.Slot] : default;
        return alive;
    }

    /// <summary>
    /// The faction of <paramref name="who"/> (-1 when it is not in the world).
    /// </summary>
    public int FactionOf(CharacterId who) => _world.Characters.IsAlive(who) && _world.Characters.Cold(who.Slot) is { } cold ? cold.Faction : -1;

    /// <summary>
    /// The asker's squad id.
    /// </summary>
    public int SquadId => _squad.Id;

    /// <summary>
    /// The asker's squad leader (<see cref="CharacterId.None"/>: none).
    /// </summary>
    public CharacterId Leader => _squad.Leader;

    /// <summary>
    /// How many members the asker's squad has.
    /// </summary>
    public int MemberCount => _squad.Members.Count;

    /// <summary>
    /// Member <paramref name="k"/> of the asker's squad (0 to <see cref="MemberCount"/> - 1).
    /// </summary>
    public CharacterId Member(int k) => _squad.Members[k];

    /// <summary>
    /// The placed town the squad's package runs in (-1: none, or no package).
    /// </summary>
    public int Town => _squad.Ai?.Town ?? -1;

    /// <summary>
    /// The AI_PACKAGE string id the squad runs now (null: none).
    /// </summary>
    public string? Package => _squad.Ai?.Current?.Id;

    /// <summary>
    /// The relation of faction <paramref name="a"/> towards <paramref name="b"/> (-100 to 100).
    /// </summary>
    public float Relation(int a, int b) => _data.Relations.Get(a, b);

    /// <summary>
    /// Whether factions <paramref name="a"/> and <paramref name="b"/> are hostile.
    /// </summary>
    public bool IsHostile(int a, int b) => _data.Relations.IsHostile(a, b);

    /// <summary>
    /// The tick being computed.
    /// </summary>
    public long Tick => _world.Tick;

    /// <summary>
    /// The game hour (0 to 23).
    /// </summary>
    public int Hour => _world.Hour;

    /// <summary>
    /// The game time in hours since the world began.
    /// </summary>
    public double GameHours => _world.GameHours;

    /// <summary>
    /// The world seed, for deterministic rolls (<see cref="Rng.Hash"/> with <see cref="Rng.Key"/> of <see cref="Id"/> and the tick).
    /// </summary>
    public ulong Seed => _world.Seed;

    /// <summary>
    /// The merged game data record of <paramref name="stringId"/> (null: none). Read it only.
    /// </summary>
    public GameRecord? Record(string stringId) => _data.Db.Find(stringId);
}

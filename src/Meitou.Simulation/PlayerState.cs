namespace Meitou.Simulation;

/// <summary>
/// The player in the world (docs/game/ui-input.md section 7, economy.md): the faction that is theirs, their money and the characters
/// selected. Selection is world state, changed by <see cref="SelectCommand"/>, so a scripted game of selects and orders replays the
/// same. There is no player until a new game starts (<see cref="Exists"/>).
/// </summary>
public sealed class PlayerState
{
    /// <summary>The player's faction, a position in the world's faction list (-1 before a game starts).</summary>
    public int Faction { get; set; } = -1;
    public bool Exists => Faction >= 0;
    /// <summary>Money in cats (NEW_GAME_STARTOFF <c>money</c>; how it is spent is the economy stage).</summary>
    public long Money { get; set; }
    /// <summary>The selected characters, in the order they were selected.</summary>
    public List<CharacterId> Selection { get; } = [];
    /// <summary>The player's squad (the one the start created).</summary>
    public int Squad { get; set; } = -1;

    /// <summary>Drops characters that are gone (serial phases, after the commit).</summary>
    internal void Prune(CharacterTable table) => Selection.RemoveAll(c => !table.TryResolveNext(c, out _));

    internal void Hash(ref StateHasher h)
    {
        h.Add(Faction);
        h.Add(Money);
        h.Add(Squad);
        h.Add(Selection.Count);
        foreach (var c in Selection)
        {
            h.Add(c);
        }
    }
}

/// <summary>Applies the player's selection and stop commands (step 1 of the tick). Move orders are the movement system's.</summary>
public sealed class PlayerSystem : ITickSystem
{
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        var player = world.Player;
        foreach (var command in commands)
        {
            switch (command)
            {
                case SelectCommand select:
                    if (!select.Additive) player.Selection.Clear();
                    foreach (var id in select.Characters)
                        if (table.TryResolveNext(id, out int slot) && table.Cold(slot)!.IsPlayer && !player.Selection.Contains(id)) player.Selection.Add(id);
                    break;
                case StopCommand stop:
                    foreach (var id in stop.Characters.Count > 0 ? stop.Characters : player.Selection.ToArray())
                    {
                        if (!table.TryResolveNext(id, out int slot) || !table.Cold(slot)!.IsPlayer) continue;
                        MovementSystem.Stop(table, slot);
                    }
                    break;
            }
        }
    }

    public void SlowWorld(World world) => world.Player.Prune(world.Characters);
}

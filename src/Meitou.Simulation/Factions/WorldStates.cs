using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation;

/// <summary>
/// The WORLD_EVENT_STATEs answered live, as the original's <c>FUN_1409a7e00</c> (docs/game/factions-squads-towns.md "Unique characters", queries): a state holds when every
/// <c>NPC is</c> unique has its state (and, with <c>player involvement</c>, one other than alive has the player flag), no <c>NPC is NOT</c> unique has its state (with the player
/// flag needed for an alive one), and each <c>player ally</c> / <c>player enemy</c> faction is or is not the player's ally (relation ≥ 50) / hostile to it (≤ -30) as the entry
/// says. A unique with no entry is alive and the player not involved, so a new game answers by the data alone. Nothing is saved: the unique table and the relations are.
/// <para>The population refreshes the unique states it reads every tick (<see cref="Refresh"/>, serial; a look's own changes to the table are taken again at once); a query reads that copy, so the build threads may ask at any time.</para>
/// </summary>
public sealed class WorldStates(GameDatabase db, FactionRelations relations) : IWorldStates
{
    readonly ConcurrentDictionary<string, WorldEventStateData?> states = new(StringComparer.Ordinal);
    volatile Snapshot current = new(new Dictionary<string, (UniqueState, bool)>(StringComparer.Ordinal), -1);
    long version = -1;

    sealed record Snapshot(IReadOnlyDictionary<string, (UniqueState State, bool Player)> Uniques, int Player);

    /// <summary>Takes the unique table's states and the player's faction (serial phase); cheap when nothing changed.</summary>
    public void Refresh(UniqueCharacters uniques, int playerFaction)
    {
        if (uniques.Version == version && current.Player == playerFaction) return;
        version = uniques.Version;
        var copy = new Dictionary<string, (UniqueState, bool)>(uniques.Count, StringComparer.Ordinal);
        foreach (var (id, e) in uniques.All) copy[id] = (e.State, e.Player);
        current = new Snapshot(copy, playerFaction);
    }

    /// <summary>The state's record, read once (null: not a WORLD_EVENT_STATE).</summary>
    public WorldEventStateData? Find(string id) =>
        states.GetOrAdd(id, static (key, d) => d.Find(key) is { Type: FcsRecordType.WORLD_EVENT_STATE } r ? WorldEventStateData.From(d, r) : null, db);

    public bool IsTrue(string worldStateId)
    {
        if (Find(worldStateId) is not { } s) return false;
        var now = current;
        (UniqueState State, bool Player) Of(string character) => now.Uniques.TryGetValue(character, out var e) ? e : (UniqueState.Alive, false);
        foreach (var (character, wanted) in s.NpcIs)
        {
            var (state, player) = Of(character);
            if ((int)state != wanted) return false;
            if (s.PlayerInvolvement && wanted != 1 && !player) return false;
        }
        foreach (var (character, unwanted) in s.NpcIsNot)
        {
            var (state, player) = Of(character);
            if ((int)state == unwanted) return false;
            if (s.PlayerInvolvement && unwanted == 1 && !player) return false;
        }
        int p = now.Player;
        if (s.PlayerAlly.Count + s.PlayerEnemy.Count > 0 && !relations.Contains(p)) return false;
        foreach (var (faction, ally) in s.PlayerAlly)
            if (relations.IndexOf(faction) is var f and >= 0 && relations.IsAlly(f, p) != ally) return false;
        foreach (var (faction, enemy) in s.PlayerEnemy)
            if (relations.IndexOf(faction) is var f and >= 0 && relations.IsHostile(f, p) != enemy) return false;
        return true;
    }
}

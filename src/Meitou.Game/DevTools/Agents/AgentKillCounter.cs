using Meitou.Simulation;

namespace Meitou.Game.DevTools.Agents;

/// <summary>
/// The agent play API's kill counter (docs/procedures/agent-play-api.md, observe's <c>score.kills</c>): the hostile characters seen dead since the
/// load.
/// </summary>
sealed class AgentKillCounter(PlayerInterface player)
{
    /// <summary>
    /// The world the ids are of; another one starts them again.
    /// </summary>
    object? _killsWorld;

    /// <summary>
    /// The hostile characters seen alive, and those seen dead.
    /// </summary>
    readonly HashSet<CharacterId> _hostileSeen = [], _hostileDead = [];

    /// <summary>
    /// Notes the hostile (non-player, non-animal, hostile to the player or fighting it) characters that are dead; a set of ids, reset when another
    /// world is loaded.
    /// </summary>
    public int ControlKills()
    {
        if (!ReferenceEquals(_killsWorld, player.Session.World))
        {
            _killsWorld = player.Session.World;
            _hostileSeen.Clear();
            _hostileDead.Clear();
        }

        foreach (var c in player.Session.CurrentSnapshot.Characters)
        {
            if (c.IsPlayer || c.Animal) continue;
            bool hostile = c.HostileToPlayer || c.FightsPlayer;
            if (c.Body is { Dead: true })
            {
                if (hostile || _hostileSeen.Contains(c.Id)) _hostileDead.Add(c.Id);
            }
            else if (hostile) _hostileSeen.Add(c.Id);
        }
        return _hostileDead.Count;
    }
}

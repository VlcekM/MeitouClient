using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Game.DevTools.Agents;

/// <summary>
/// The agent-play combat check (<c>combat-status</c>, <c>observe --brief</c> field <c>combat</c>): stance flags, target, what the character is doing,
/// damage dealt and taken over the last 5 s and 30 s, and the blow counters. Damage is <b>approximate</b>: sampled from the medical state at each
/// control poll (4 per wall second), a drop in a character's summed part flesh counts as damage TAKEN by it, and as DEALT by the character in its
/// combat slot's <c>HitFrom</c> (the last one that landed a blow on it); bleeding, fist recoil and a second attacker between two polls are attributed
/// the same way, healing is not subtracted. The blow counters (<c>Thrown, Landed, Parried, Evaded, Taken, Blocks, Dodges</c>) are the combat system's
/// own, exact.
/// </summary>
sealed class AgentCombatWatch(PlayerInterface player)
{
    /// <summary>
    /// One character's samples over the last 31 s.
    /// </summary>
    sealed class CombatTrack
    {
        /// <summary>
        /// The summed part flesh at the last sample; NaN before the first.
        /// </summary>
        public float Hp = float.NaN;

        /// <summary>
        /// The damage dealt and taken, by tick.
        /// </summary>
        public readonly List<(long Tick, float Amount)> Dealt = [], Taken = [];

        /// <summary>
        /// The combat system's blow counters, by tick (the player's characters only).
        /// </summary>
        public readonly List<(long Tick, int Thrown, int Landed, int Taken, int Blocks, int Parried, int Evaded, int Dodges)> Counts = [];
    }

    /// <summary>
    /// Each character's samples.
    /// </summary>
    readonly Dictionary<CharacterId, CombatTrack> _combatTracks = [];

    /// <summary>
    /// The world the samples are of; another one starts them again.
    /// </summary>
    World? _combatWorld;

    /// <summary>
    /// The tick of the last sample.
    /// </summary>
    long _combatSampleTick = -1;

    /// <summary>
    /// One sample of everyone's hp and the squad's blow counters; called every control poll.
    /// </summary>
    public void ControlCombatSample()
    {
        var world = player.Session.World;
        if (!ReferenceEquals(_combatWorld, world)) { _combatWorld = world; _combatTracks.Clear(); _combatSampleTick = -1; }
        long tick = world.Tick;
        if (tick == _combatSampleTick) return;
        _combatSampleTick = tick;
        var combat = world.Systems.OfType<CombatSystem>().FirstOrDefault();
        var table = world.Characters;
        long keep = tick - (long)(31 / world.TickSeconds);
        foreach (var c in player.Session.CurrentSnapshot.Characters)
        {
            if (!table.TryResolveNext(c.Id, out int slot) || table.Cold(slot)?.Medical is not { } medical) continue;
            float hp = 0;
            foreach (var p in medical.Parts) hp += p.Flesh;
            if (!_combatTracks.TryGetValue(c.Id, out var t)) _combatTracks[c.Id] = t = new CombatTrack();
            if (!float.IsNaN(t.Hp) && hp < t.Hp - 0.001f)
            {
                float drop = t.Hp - hp;
                t.Taken.Add((tick, drop));
                if (combat is not null && combat.StateOf(c.Id).HitFrom is int from and >= 0 && from < table.Previous.Length
                    && _combatTracks.TryGetValue(new CharacterId(from, table.Previous[from].Generation), out var attacker))
                    attacker.Dealt.Add((tick, drop));
            }
            t.Hp = hp;
            if (c.IsPlayer && combat is not null)
            {
                var s = combat.StateOf(c.Id);
                t.Counts.Add((tick, s.Thrown, s.Landed, s.Taken, s.Blocks, s.Parried, s.Evaded, s.Dodges));
            }
            t.Dealt.RemoveAll(e => e.Tick < keep); t.Taken.RemoveAll(e => e.Tick < keep); t.Counts.RemoveAll(e => e.Tick < keep);
        }
    }

    /// <summary>
    /// The combat check of one character: its status, stance, target and nearest hostile, damage over 5 s and 30 s, blows over 30 s and warnings.
    /// </summary>
    internal sealed record CombatReport(CharacterSnapshot C, string Status, List<string> Stance, CharacterId Target, string? TargetName, double? TargetDist, CharacterSnapshot? Hostile, double? HostileDist,
        double Dealt5, double Dealt30, double Taken5, double Taken30, int[] Blows30, double Span, List<string> Warnings)
    {
        /// <summary>
        /// The one-line summary.
        /// </summary>
        public string Line => $"blocking:{(Stance.Contains("block") ? "on" : "off")} dealt30:{Math.Round(Dealt30, 0)} taken30:{Math.Round(Taken30, 0)} target:{(TargetName is null ? "none" : TargetName)}";
    }

    /// <summary>
    /// The combat check of <paramref name="c"/>, after a fresh sample.
    /// </summary>
    internal CombatReport? CombatFor(CharacterSnapshot c)
    {
        var world = player.Session.World;
        ControlCombatSample();
        long tick = world.Tick;
        _combatTracks.TryGetValue(c.Id, out var t);
        double Sum(List<(long Tick, float Amount)>? l, double secs) => l is null ? 0 : l.Where(e => e.Tick > tick - (long)(secs / world.TickSeconds)).Sum(e => (double)e.Amount);
        var combat = world.Systems.OfType<CombatSystem>().FirstOrDefault();
        var s = combat?.StateOf(c.Id) ?? CombatSlot.Fresh(c.Id.Generation);
        var stance = AgentOrders.StanceNames.Where(n => (c.Orders & n.Order) != 0).Select(n => n.Name).ToList();
        var at = player.Where(c);
        var flat = new Vector2(at.X, at.Z);
        CharacterSnapshot? hostile = null; double? hd = null;
        foreach (var o in player.Session.CurrentSnapshot.Characters)
        {
            if (o.Id == c.Id || o.IsPlayer || o.Body is { Dead: true } or { Unconscious: true } || !(o.HostileToPlayer || o.FightsPlayer)) continue;
            var p = player.Where(o);
            double d = Vector2.Distance(new Vector2(p.X, p.Z), flat);
            if (hd is null || d < hd) (hostile, hd) = (o, d);
        }
        CharacterId target = CharacterId.None; string? tName = null; double? tDist = null;
        if (s.TargetSlot >= 0 && world.Characters.Previous.Length > s.TargetSlot && player.Session.CurrentSnapshot.Characters.FirstOrDefault(o => o.Id.Slot == s.TargetSlot && o.Id.Generation == s.TargetGeneration) is { } tc)
        {
            target = tc.Id; tName = tc.Name; var p = player.Where(tc); tDist = Vector2.Distance(new Vector2(p.X, p.Z), flat);
        }
        bool block = stance.Contains("block");
        bool fighting = target != CharacterId.None || c.Task == CharacterTask.Attack || c.InCombat;
        string status = c.Body is { Dead: true } ? "dead" : c.Body is { Unconscious: true } ? "ko"
            : s.AttackTech >= 0 ? "attacking" : block && fighting ? "blocking" : fighting ? "fighting-between-blows" : "idle";
        int[] blows = [0, 0, 0, 0, 0, 0, 0];
        double span = 0;
        if (t is { Counts.Count: > 0 })
        {
            var first = t.Counts.FirstOrDefault(e => e.Tick >= tick - (long)(30 / world.TickSeconds));
            var last = t.Counts[^1];
            span = (last.Tick - first.Tick) * world.TickSeconds;
            blows = [last.Thrown - first.Thrown, last.Landed - first.Landed, last.Taken - first.Taken, last.Blocks - first.Blocks, last.Parried - first.Parried, last.Evaded - first.Evaded, last.Dodges - first.Dodges];
        }
        var warn = new List<string>();
        if (block && fighting) warn.Add("block is on while attacking");
        if (stance.Contains("passive") || stance.Contains("hold")) warn.Add("passive/hold set");
        double dealt30 = Sum(t?.Dealt, 30);
        if (dealt30 <= 0 && hd is < 30 && t is { Counts.Count: > 0 } && span >= 29) warn.Add("no damage dealt in 30 s while a hostile is within 30 m");
        return new CombatReport(c, status, stance, target, tName, tDist, hostile, hd, Sum(t?.Dealt, 5), dealt30, Sum(t?.Taken, 5), Sum(t?.Taken, 30), blows, span, warn);
    }

    /// <summary>
    /// The one-line summary for <c>observe --brief</c>.
    /// </summary>
    internal string ControlCombatLine(CharacterSnapshot lead) => CombatFor(lead)?.Line ?? "n/a";
}

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Game;

/// <summary>
/// The agent-play combat check (<c>combat-status</c>, <c>observe --brief</c> field <c>combat</c>): stance flags, target, what the character is doing, damage dealt and taken over the last 5 s and 30 s, and the blow counters.
/// Damage is <b>approximate</b>: sampled from the medical state at each control poll (4 per wall second), a drop in a character's summed part flesh counts as damage TAKEN by it, and as DEALT by the character
/// in its combat slot's <c>HitFrom</c> (the last one that landed a blow on it); bleeding, fist recoil and a second attacker between two polls are attributed the same way, healing is not subtracted.
/// The blow counters (<c>Thrown, Landed, Parried, Evaded, Taken, Blocks, Dodges</c>) are the combat system's own, exact.
/// </summary>
sealed partial class PlayerInterface
{
    sealed class CombatTrack
    {
        public float Hp = float.NaN;
        public readonly List<(long Tick, float Amount)> Dealt = [], Taken = [];
        public readonly List<(long Tick, int Thrown, int Landed, int Taken, int Blocks, int Parried, int Evaded, int Dodges)> Counts = [];
    }

    readonly Dictionary<CharacterId, CombatTrack> combatTracks = [];
    World? combatWorld;
    long combatSampleTick = -1;

    /// <summary>One sample of everyone's hp and the squad's blow counters; called every control poll.</summary>
    public void ControlCombatSample()
    {
        var world = session.World;
        if (!ReferenceEquals(combatWorld, world)) { combatWorld = world; combatTracks.Clear(); combatSampleTick = -1; }
        long tick = world.Tick;
        if (tick == combatSampleTick) return;
        combatSampleTick = tick;
        var combat = world.Systems.OfType<CombatSystem>().FirstOrDefault();
        var table = world.Characters;
        long keep = tick - (long)(31 / world.TickSeconds);
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (!table.TryResolveNext(c.Id, out int slot) || table.Cold(slot)?.Medical is not { } medical) continue;
            float hp = 0;
            foreach (var p in medical.Parts) hp += p.Flesh;
            if (!combatTracks.TryGetValue(c.Id, out var t)) combatTracks[c.Id] = t = new CombatTrack();
            if (!float.IsNaN(t.Hp) && hp < t.Hp - 0.001f)
            {
                float drop = t.Hp - hp;
                t.Taken.Add((tick, drop));
                if (combat is not null && combat.StateOf(c.Id).HitFrom is int from and >= 0 && from < table.Previous.Length
                    && combatTracks.TryGetValue(new CharacterId(from, table.Previous[from].Generation), out var attacker))
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

    sealed record CombatReport(CharacterSnapshot C, string Status, List<string> Stance, CharacterId Target, string? TargetName, double? TargetDist, CharacterSnapshot? Hostile, double? HostileDist,
        double Dealt5, double Dealt30, double Taken5, double Taken30, int[] Blows30, double Span, List<string> Warnings)
    {
        public string Line => $"blocking:{(Stance.Contains("block") ? "on" : "off")} dealt30:{Math.Round(Dealt30, 0)} taken30:{Math.Round(Taken30, 0)} target:{(TargetName is null ? "none" : TargetName)}";
    }

    CombatReport? CombatFor(CharacterSnapshot c)
    {
        var world = session.World;
        ControlCombatSample();
        long tick = world.Tick;
        combatTracks.TryGetValue(c.Id, out var t);
        double Sum(List<(long Tick, float Amount)>? l, double secs) => l is null ? 0 : l.Where(e => e.Tick > tick - (long)(secs / world.TickSeconds)).Sum(e => (double)e.Amount);
        var combat = world.Systems.OfType<CombatSystem>().FirstOrDefault();
        var s = combat?.StateOf(c.Id) ?? CombatSlot.Fresh(c.Id.Generation);
        var stance = StanceNames.Where(n => (c.Orders & n.Order) != 0).Select(n => n.Name).ToList();
        var at = where(c);
        var flat = new Vector2(at.X, at.Z);
        CharacterSnapshot? hostile = null; double? hd = null;
        foreach (var o in session.CurrentSnapshot.Characters)
        {
            if (o.Id == c.Id || o.IsPlayer || o.Body is { Dead: true } or { Unconscious: true } || !(o.HostileToPlayer || o.FightsPlayer)) continue;
            var p = where(o);
            double d = Vector2.Distance(new Vector2(p.X, p.Z), flat);
            if (hd is null || d < hd) (hostile, hd) = (o, d);
        }
        CharacterId target = CharacterId.None; string? tName = null; double? tDist = null;
        if (s.TargetSlot >= 0 && world.Characters.Previous.Length > s.TargetSlot && session.CurrentSnapshot.Characters.FirstOrDefault(o => o.Id.Slot == s.TargetSlot && o.Id.Generation == s.TargetGeneration) is { } tc)
        {
            target = tc.Id; tName = tc.Name; var p = where(tc); tDist = Vector2.Distance(new Vector2(p.X, p.Z), flat);
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

    /// <summary>The one-line summary for <c>observe --brief</c>.</summary>
    string ControlCombatLine(CharacterSnapshot lead) => CombatFor(lead)?.Line ?? "n/a";

    /// <summary><c>combat-status</c>: JSON for one character (null id: the selected, else the first squad member).</summary>
    public string? ControlCombatStatusJson(CharacterId? id, out string error)
    {
        error = "";
        var snap = session.CurrentSnapshot;
        var c = id is { } want ? ControlCharacter(want) : Selected().FirstOrDefault() ?? snap.Characters.FirstOrDefault(x => x.IsPlayer);
        if (c is null) { error = id is null ? "no player character" : $"no character {CharacterKey(id.Value)}"; return null; }
        var r = CombatFor(c)!;
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("id", CharacterKey(c.Id)); w.WriteString("name", c.Name);
            w.WriteString("status", r.Status);
            w.WriteStartObject("stance");
            foreach (var (name, order) in StanceNames) w.WriteBoolean(name, (c.Orders & order) != 0);
            w.WriteEndObject();
            w.WriteString("task", c.Task.ToString()); w.WriteBoolean("inCombat", c.InCombat);
            if (r.TargetName is null) w.WriteNull("target");
            else { w.WriteStartObject("target"); w.WriteString("id", CharacterKey(r.Target)); w.WriteString("name", r.TargetName); w.WriteNumber("dist", Math.Round(r.TargetDist ?? 0, 1)); w.WriteEndObject(); }
            if (r.Hostile is null) w.WriteNull("nearestHostile");
            else { w.WriteStartObject("nearestHostile"); w.WriteString("id", CharacterKey(r.Hostile.Id)); w.WriteString("name", r.Hostile.Name); w.WriteNumber("dist", Math.Round(r.HostileDist ?? 0, 1)); w.WriteEndObject(); }
            w.WriteStartObject("damage");
            w.WriteNumber("dealt5", Math.Round(r.Dealt5, 1)); w.WriteNumber("dealt30", Math.Round(r.Dealt30, 1));
            w.WriteNumber("taken5", Math.Round(r.Taken5, 1)); w.WriteNumber("taken30", Math.Round(r.Taken30, 1));
            w.WriteString("note", "approximate: hp drops sampled 4x per second; dealt = drop of a character whose last blow-taker was this one (includes bleeding), taken = all drops");
            w.WriteEndObject();
            w.WriteStartObject("blows30");
            string[] names = ["thrown", "landed", "takenHits", "blocks", "parried", "evaded", "dodges"];
            for (int i = 0; i < names.Length; i++) w.WriteNumber(names[i], r.Blows30[i]);
            w.WriteNumber("windowSeconds", Math.Round(r.Span, 1));
            w.WriteEndObject();
            w.WriteStartArray("warn"); foreach (var m in r.Warnings) w.WriteStringValue("warn: " + m); w.WriteEndArray();
            w.WriteString("line", r.Line);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}

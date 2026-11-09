using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Building;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;

namespace Meitou.Game.DevTools.Agents;

/// <summary>
/// The interface's side of the agent control channel (docs/procedures/agent-play-api.md): the world and the selection as JSON, picking by entity id,
/// and the orders an agent plays with.
/// </summary>
sealed class AgentOrders(PlayerInterface player)
{
    // The agent control channel's view of the world (docs/procedures/todo-faster-ingame-agent-tests.md; AgentWorld has the commands): entity lists, screen positions, picking and the selection and order calls.
    // Everything reads the published snapshot and the picking code the pointer uses; orders go out as the same SimCommands the player's input gives. Entity ids: <c>c:&lt;slot&gt;.&lt;generation&gt;</c> for a character,
    // <c>b:&lt;instance key&gt;</c> for a building (a site of the player's or a level-data building).
    /// <summary>
    /// A building seen by the control channel, kept by id so a later command can find it again (level buildings have no lookup by key).
    /// </summary>
    sealed record ControlBuilding(string Id, string Name, string? Record, Vector3 Position, Vector2[]? Hull);

    readonly Dictionary<string, ControlBuilding> _controlBuildings = [];
    /// <summary>
    /// The ids of the last select command, until the world applies it (an order given in the same breath still counts them).
    /// </summary>
    IReadOnlyList<CharacterId> _controlSelectIssued = [];
    long _controlSelectTick = -1;

    static string CharacterKey(CharacterId id) => $"c:{id.Slot}.{id.Generation}";

    /// <summary>
    /// Parses <c>c:3.1</c> (or <c>3.1</c>) into a character id; false for anything else.
    /// </summary>
    public static bool TryParseCharacter(string text, out CharacterId id)
    {
        id = CharacterId.None;
        if (text.StartsWith("c:", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        string[] parts = text.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int slot) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int generation)) return false;
        id = new CharacterId(slot, generation);
        return true;
    }

    CharacterSnapshot? ControlCharacter(CharacterId id) => player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Where an entity id stands (a character as drawn, a building at its position); null when the id names nothing known.
    /// </summary>
    public Vector3? ControlPosition(string id)
    {
        if (id.StartsWith("b:", StringComparison.OrdinalIgnoreCase))
        {
            string key = id[2..];
            if (player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == key) is { } site) return site.Position;
            return _controlBuildings.TryGetValue(id, out var known) ? known.Position : null;
        }
        return TryParseCharacter(id, out var cid) && ControlCharacter(cid) is { } c ? player.Where(c) : null;
    }

    /// <summary>
    /// The first selected character's drawn position (waits and the state use it).
    /// </summary>
    public Vector3? ControlLeadPosition() => player.Selected().FirstOrDefault() is { } lead ? player.Where(lead) : null;

    bool OnScreen(Vector2 s) => s.X >= 0 && s.X <= player.Width && s.Y >= 0 && s.Y <= player.Height;

    void WritePosition(Utf8JsonWriter w, string name, Vector3 p)
    {
        w.WriteStartArray(name);
        foreach (float v in new[] { p.X, p.Y, p.Z }) w.WriteNumberValue(Math.Round(v, 1));
        w.WriteEndArray();
    }

    void WriteScreen(Utf8JsonWriter w, Vector3 world)
    {
        var s = player.Project(world);
        w.WriteBoolean("onScreen", s is { } v && OnScreen(v));
        if (s is { } p && OnScreen(p))
        {
            w.WriteStartArray("screen");
            w.WriteNumberValue((int)MathF.Round(p.X));
            w.WriteNumberValue((int)MathF.Round(p.Y));
            w.WriteEndArray();
        }
    }

    static string StateWord(CharacterSnapshot c) =>
        c.Body is { Dead: true } ? "dead" : c.Body is { Unconscious: true } ? "ko" : c.Body is { PlayingDead: true } ? "playing-dead" : !c.CarriedBy.IsNone ? "carried" : "alive";

    void WriteCharacter(Utf8JsonWriter w, CharacterSnapshot c, Vector3 from)
    {
        var at = player.Where(c);
        w.WriteStartObject();
        w.WriteString("id", CharacterKey(c.Id));
        w.WriteString("name", c.Name);
        w.WriteString("kind", "character");
        w.WriteString("faction", player.FactionName?.Invoke(c.Faction));
        WritePosition(w, "position", at);
        w.WriteNumber("distance", Math.Round(Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(from.X, from.Z)), 1));
        w.WriteString("state", StateWord(c));
        w.WriteBoolean("player", c.IsPlayer);
        w.WriteBoolean("animal", c.Animal);
        w.WriteBoolean("hostile", c.HostileToPlayer);
        w.WriteBoolean("inCombat", c.InCombat);
        if (c.HealthParts.Count > 0)
        {
            float flesh = c.HealthParts.Sum(p => p.Flesh), max = c.HealthParts.Sum(p => p.Maximum);
            var worst = c.HealthParts.Where(p => p.Maximum > 0).MinBy(p => p.Flesh / p.Maximum);
            w.WriteNumber("hp", Math.Round(flesh, 1));
            w.WriteNumber("hpMax", Math.Round(max, 1));
            if (worst is not null)
            {
                w.WriteNumber("minLimb", Math.Round(worst.Flesh / worst.Maximum, 2));
                w.WriteString("minLimbName", worst.Name);
            }
        }
        if (c.Body is { } body)
        {
            w.WriteNumber("worstPart", Math.Round(body.WorstPart, 2));   // fraction of the worst body part's maximum, known for everyone
            w.WriteNumber("blood", Math.Round(body.Blood, 2));
        }
        w.WriteString("task", c.Task.ToString());
        w.WriteString("order", c.Activity is { Length: > 0 } a ? a : c.Task.ToString());
        w.WriteBoolean("selected", c.Selected);
        WriteScreen(w, at + new Vector3(0, PlayerInterface.CharacterHeight / 2, 0));
        w.WriteEndObject();
    }

    void WriteBuilding(Utf8JsonWriter w, ControlBuilding b, Vector3 from)
    {
        w.WriteStartObject();
        w.WriteString("id", b.Id);
        w.WriteString("name", b.Name);
        w.WriteString("kind", "building");
        if (b.Record is not null) w.WriteString("record", b.Record);
        WritePosition(w, "position", b.Position);
        w.WriteNumber("distance", Math.Round(Vector2.Distance(new Vector2(b.Position.X, b.Position.Z), new Vector2(from.X, from.Z)), 1));
        WriteScreen(w, b.Position);
        w.WriteEndObject();
    }

    /// <summary>
    /// The entities within <paramref name="radius"/> of <paramref name="near"/> (X, Z), nearest first, as a JSON array; <paramref name="kind"/> is
    /// character, building or all.
    /// </summary>
    public string ControlWorldJson(Vector3 near, float radius, string kind, int limit)
    {
        var items = new List<(float D, Action<Utf8JsonWriter> Write)>();
        var flat = new Vector2(near.X, near.Z);
        if (kind is "character" or "all")
            foreach (var c in player.Session.CurrentSnapshot.Characters)
            {
                var at = player.Where(c);
                float d = Vector2.Distance(new Vector2(at.X, at.Z), flat);
                if (d <= radius) items.Add((d, w => WriteCharacter(w, c, near)));
            }
        if (kind is "building" or "all")
        {
            foreach (var s in player.Session.CurrentSnapshot.Buildings)
            {
                float d = Vector2.Distance(new Vector2(s.Position.X, s.Position.Z), flat);
                if (d > radius) continue;
                var b = new ControlBuilding("b:" + s.Id, player.Build.SiteName(s), s.Record, s.Position, null);
                _controlBuildings["b:" + s.Id] = b;
                items.Add((d, w => WriteBuilding(w, b, near)));
            }
            if (player.Build.Services is { } host)
                foreach (var p in host.Placement.StaticPlacementsAround(flat, radius))
                {
                    if (player.LevelEditorPanel.Services?.IsCleared(p.Key) == true) continue;
                    float y = p.WorldY ?? player.Ground(p.Position.X, p.Position.Z) + p.Position.Y;
                    var at = new Vector3(p.Position.X, y, p.Position.Z);
                    float d = Vector2.Distance(new Vector2(at.X, at.Z), flat);
                    if (d > radius) continue;
                    var info = host.WorldInfo(p);
                    var b = new ControlBuilding("b:" + p.Key, info.Name, p.BuildingId, at, host.Placement.Footprint(p.BuildingId)?.Place(p.Position, p.Rotation));
                    _controlBuildings[b.Id] = b;
                    items.Add((d, w => WriteBuilding(w, b, near)));
                }
        }
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (var (_, write) in items.OrderBy(i => i.D).Take(limit)) write(w);
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// What a click at the pixel would hit: a character first (the nearest the pointer would take, one hidden behind a closed building excepted),
    /// else a building, else the ground or floor; JSON object.
    /// </summary>
    public string ControlPickJson(Vector2 pixel)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartArray("pixel");
            w.WriteNumberValue(pixel.X);
            w.WriteNumberValue(pixel.Y);
            w.WriteEndArray();
            var ray = player.Ray(pixel);
            var terrain = ray is { } r ? player.Picking.MoveHit(r.Origin, r.Direction) : null;
            if (player.Picking.PickWhereAt(pixel, _ => true) is { } hit && ControlCharacter(hit.Id) is { } c)
            {
                w.WriteString("kind", "character");
                w.WriteString("id", CharacterKey(c.Id));
                w.WriteString("name", c.Name);
                WritePosition(w, "position", player.Where(c));
                w.WriteNumber("distance", Math.Round(hit.T, 1));
            }
            else if (player.BuildingSelection.PickBuilding(pixel) is { } building)
            {
                string id = "b:" + building.Instance;
                _controlBuildings[id] = new ControlBuilding(id, building.Name, building.Record, building.Position, building.Hull);
                w.WriteString("kind", "building");
                w.WriteString("id", id);
                w.WriteString("name", building.Name);
                if (building.Record is not null) w.WriteString("record", building.Record);
                WritePosition(w, "position", building.Position);
            }
            else if (terrain is { } ground)
            {
                w.WriteString("kind", "terrain");
                WritePosition(w, "position", ground);
            }
            else w.WriteString("kind", "none");
            if (terrain is { } t) WritePosition(w, "terrain", t);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The on-screen rectangle of an entity (JSON), or <c>offscreen</c>; null when the id names nothing. A character reports whether the nearest
    /// thing under its centre is something else.
    /// </summary>
    public string? ControlEntityScreenJson(string id)
    {
        Vector3[] corners;
        CharacterSnapshot? character = null;
        if (id.StartsWith("b:", StringComparison.OrdinalIgnoreCase))
        {
            if (ControlPosition(id) is not { } pos) return null;
            var hull = _controlBuildings.TryGetValue(id, out var known) ? known.Hull : null;
            if (hull is null && player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => "b:" + s.Id == id) is { } site && player.Build.Services is { } host
                && (host.Placement.Footprint(site.Record) ?? host.Placement.Plot(site.Record)) is { } footprint)
                hull = footprint.Place(site.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlacementCheck.YawOf(site.Rotation)));
            corners = hull is { Length: > 0 } ? [.. hull.Select(p => new Vector3(p.X, pos.Y, p.Y)), .. hull.Select(p => new Vector3(p.X, pos.Y + 12, p.Y))] : [pos, pos + new Vector3(0, 12, 0)];
        }
        else
        {
            if (!TryParseCharacter(id, out var cid) || ControlCharacter(cid) is not { } c) return null;
            character = c;
            var at = player.Where(c);
            const float half = 3.5f;
            corners = [at + new Vector3(-half, 0, -half), at + new Vector3(half, 0, -half), at + new Vector3(-half, 0, half), at + new Vector3(half, 0, half),
                at + new Vector3(-half, PlayerInterface.CharacterHeight, -half), at + new Vector3(half, PlayerInterface.CharacterHeight, -half), at + new Vector3(-half, PlayerInterface.CharacterHeight, half), at + new Vector3(half, PlayerInterface.CharacterHeight, half)];
        }
        var pixels = corners.Select(player.Project).Where(p => p is not null).Select(p => p!.Value).ToList();
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            if (pixels.Count == 0)
            {
                w.WriteBoolean("onScreen", false);
                w.WriteString("state", "offscreen");
                w.WriteEndObject();
                w.Flush();
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            float x0 = pixels.Min(p => p.X), x1 = pixels.Max(p => p.X), y0 = pixels.Min(p => p.Y), y1 = pixels.Max(p => p.Y);
            bool visible = x1 >= 0 && x0 <= player.Width && y1 >= 0 && y0 <= player.Height;
            w.WriteBoolean("onScreen", visible);
            if (!visible) w.WriteString("state", "offscreen");
            else
            {
                w.WriteString("state", "onscreen");
                w.WriteStartArray("rect");
                foreach (float v in new[] { x0, y0, x1 - x0, y1 - y0 }) w.WriteNumberValue((int)MathF.Round(v));
                w.WriteEndArray();
                var centre = new Vector2(MathF.Round((x0 + x1) / 2), MathF.Round((y0 + y1) / 2));
                w.WriteStartArray("center");
                w.WriteNumberValue(centre.X);
                w.WriteNumberValue(centre.Y);
                w.WriteEndArray();
                if (character is not null)
                {
                    // Occluded: what a click at the rect's centre takes first is not this character (or the centre is under the HUD).
                    var front = player.Picking.PickWhereAt(centre, _ => true);
                    bool occluded = front is null || front.Value.Id != character.Id;
                    w.WriteBoolean("occluded", occluded);
                    if (front is { } f && f.Id != character.Id) w.WriteString("front", CharacterKey(f.Id));
                    w.WriteBoolean("overHud", player.OverHud(centre));
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ---- selection and orders

    /// <summary>
    /// Selects characters like a click or box select would; false (with the reason) when an id names none.
    /// </summary>
    public bool ControlSelect(IReadOnlyList<CharacterId> ids, bool additive, out string message)
    {
        foreach (var id in ids)
            if (ControlCharacter(id) is null) { message = $"no character {CharacterKey(id)}"; return false; }
        (_controlSelectIssued, _controlSelectTick) = (ids, player.Session.World.Tick);
        if (ids.Count == 0 && !additive) player.Say(new SelectCommand([], false) { Clear = true, Tick = player.Session.World.Tick });   // the game's own empty select changes nothing
        else player.Select(ids, additive);
        message = ids.Count == 0 ? "selection cleared" : $"select issued for {ids.Count}";
        return true;
    }

    /// <summary>A move order for one character of any faction (an NPC, an animal), given straight to the world; false with the reason when it does not exist.</summary>
    public bool ControlMoveCharacter(CharacterId id, float x, float z, bool queued, out string message)
    {
        if (ControlCharacter(id) is null) { message = $"refused: no character {CharacterKey(id)}"; return false; }
        player.Session.World.Commands.Enqueue(new MoveOrder([id], new Vector3(x, player.Ground(x, z), z)) { Tick = player.Session.World.Tick, Queued = queued });
        message = $"move issued to {CharacterKey(id)} at {x:0.#} {z:0.#}{(queued ? " (queued)" : "")}";
        return true;
    }

    public IReadOnlyList<CharacterId> ControlSquad() => player.ActiveSquadMembers();

    /// <summary>
    /// Console Tab completion: character ids (c:slot.gen), the squad first, then the nearest others (at most <paramref name="limit"/> in all), from
    /// the live world; empty when none is loaded.
    /// </summary>
    public List<string> ControlCharacterKeys(int limit = 40)
    {
        var snap = player.Session.CurrentSnapshot;
        var squad = player.ActiveSquadMembers();
        var lead = player.Selected().FirstOrDefault() ?? snap.Characters.FirstOrDefault(c => c.IsPlayer);
        var from = lead is null ? Vector3.Zero : player.Where(lead);
        var keys = squad.Select(CharacterKey).ToList();
        keys.AddRange(snap.Characters.Where(c => !squad.Contains(c.Id)).OrderBy(c => Vector3.DistanceSquared(player.Where(c), from)).Select(c => CharacterKey(c.Id)));
        return [.. keys.Take(limit)];
    }

    /// <summary>
    /// Console Tab completion: the item names the first selected (else lead) character carries, once each.
    /// </summary>
    public List<string> ControlCarriedItemNames()
    {
        var who = ControlOrderers();
        var id = who.Count > 0 ? who[0] : (player.Selected().FirstOrDefault() ?? player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer))?.Id ?? CharacterId.None;
        return ControlCharacter(id)?.ItemGrid is { } grid ? [.. grid.Items.Select(i => i.Name).Where(n => n.Length > 0).Distinct()] : [];
    }

    /// <summary>
    /// Console Tab completion for pickup: <c>nearest</c> and the ids of the loose items nearest to the lead (at most 20).
    /// </summary>
    public List<string> ControlGroundItemKeys()
    {
        var lead = player.Selected().FirstOrDefault() ?? player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer);
        var from = lead is null ? Vector3.Zero : player.Where(lead);
        List<string> keys = ["nearest"];
        keys.AddRange(player.Session.CurrentSnapshot.GroundItems.OrderBy(g => Vector3.DistanceSquared(g.Position, from)).Take(20).Select(g => g.Id.ToString(CultureInfo.InvariantCulture)));
        return keys;
    }

    /// <summary>
    /// The characters an order would go to: the world's selection, else what the last select command asked for.
    /// </summary>
    internal IReadOnlyList<CharacterId> ControlOrderers()
    {
        if (_controlSelectTick == player.Session.World.Tick) return _controlSelectIssued;   // the world has not run a tick since the select: its selection is still the old one
        return player.Session.World.Player.Selection.ToArray();
    }

    /// <summary>
    /// A move order to the selection at (x, z) on the terrain (the same <see cref="MoveOrder"/> a right click gives); false with the reason when
    /// nothing is selected.
    /// </summary>
    public bool ControlMove(float x, float z, bool queued, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        var target = new Vector3(x, player.Ground(x, z), z);
        player.Move(target, queued);
        message = $"move issued to {who.Count} at {x:0.#} {z:0.#}{(queued ? " (queued)" : "")}";
        return true;
    }

    /// <summary>
    /// An attack order on a character (the quick right click's <see cref="Meitou.Simulation.Combat.AttackOrder"/>); false with the reason when it
    /// cannot be given.
    /// </summary>
    public bool ControlAttack(CharacterId target, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        if (ControlCharacter(target) is not { } t) { message = $"refused: no character {CharacterKey(target)}"; return false; }
        if (who.Contains(target)) { message = "refused: a character cannot attack itself"; return false; }
        if (t.IsPlayer) { message = $"refused: {t.Name} is one of the player's own"; return false; }
        if (t.Body is { Dead: true }) { message = $"refused: {t.Name} is dead"; return false; }
        player.Say(new AttackOrder(who, target) { Tick = player.Session.World.Tick, Picked = true, Defensive = t.FightsPlayer });
        message = $"attack issued: {who.Count} on {t.Name} {CharacterKey(target)}{(t.HostileToPlayer ? "" : " (not hostile to the player)")}";
        return true;
    }

    /// <summary>
    /// Centres the strategy camera on an entity and follows it when it is a character.
    /// </summary>
    public void ControlFocusCharacter(CharacterId id) => player.CentreOn(id);

    // The agent-play combat check (<c>combat-status</c>, <c>observe --brief</c> field <c>combat</c>): stance flags, target, what the character is doing, damage dealt and taken over the last 5 s and 30 s, and the blow counters.
    // Damage is <b>approximate</b>: sampled from the medical state at each control poll (4 per wall second), a drop in a character's summed part flesh counts as damage TAKEN by it, and as DEALT by the character
    // in its combat slot's <c>HitFrom</c> (the last one that landed a blow on it); bleeding, fist recoil and a second attacker between two polls are attributed the same way, healing is not subtracted.
    // The blow counters (<c>Thrown, Landed, Parried, Evaded, Taken, Blocks, Dodges</c>) are the combat system's own, exact.
    sealed class CombatTrack
    {
        public float Hp = float.NaN;
        public readonly List<(long Tick, float Amount)> Dealt = [], Taken = [];
        public readonly List<(long Tick, int Thrown, int Landed, int Taken, int Blocks, int Parried, int Evaded, int Dodges)> Counts = [];
    }

    readonly Dictionary<CharacterId, CombatTrack> _combatTracks = [];
    World? _combatWorld;
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

    sealed record CombatReport(CharacterSnapshot C, string Status, List<string> Stance, CharacterId Target, string? TargetName, double? TargetDist, CharacterSnapshot? Hostile, double? HostileDist,
        double Dealt5, double Dealt30, double Taken5, double Taken30, int[] Blows30, double Span, List<string> Warnings)
    {
        public string Line => $"blocking:{(Stance.Contains("block") ? "on" : "off")} dealt30:{Math.Round(Dealt30, 0)} taken30:{Math.Round(Taken30, 0)} target:{(TargetName is null ? "none" : TargetName)}";
    }

    CombatReport? CombatFor(CharacterSnapshot c)
    {
        var world = player.Session.World;
        ControlCombatSample();
        long tick = world.Tick;
        _combatTracks.TryGetValue(c.Id, out var t);
        double Sum(List<(long Tick, float Amount)>? l, double secs) => l is null ? 0 : l.Where(e => e.Tick > tick - (long)(secs / world.TickSeconds)).Sum(e => (double)e.Amount);
        var combat = world.Systems.OfType<CombatSystem>().FirstOrDefault();
        var s = combat?.StateOf(c.Id) ?? CombatSlot.Fresh(c.Id.Generation);
        var stance = StanceNames.Where(n => (c.Orders & n.Order) != 0).Select(n => n.Name).ToList();
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
    string ControlCombatLine(CharacterSnapshot lead) => CombatFor(lead)?.Line ?? "n/a";

    /// <summary>
    /// <c>combat-status</c>: JSON for one character (null id: the selected, else the first squad member).
    /// </summary>
    public string? ControlCombatStatusJson(CharacterId? id, out string error)
    {
        error = "";
        var snap = player.Session.CurrentSnapshot;
        var c = id is { } want ? ControlCharacter(want) : player.Selected().FirstOrDefault() ?? snap.Characters.FirstOrDefault(x => x.IsPlayer);
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
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // The agent control channel's play API (docs/procedures/agent-play-api.md): <c>observe</c> (one JSON for a decision) and the thin wrappers that give the selection the game's own order commands
    // (stop, follow, stance, pick up, loot, first aid, build, operate, research, craft). Every action goes out through PlayerInterface.Say as the same SimCommand the player's input gives;
    // each returns false with the reason when the game would refuse it. Characters are selected with <c>select</c> first; the orders go to the selection.
    static double R1(double v) => Math.Round(v, 1);
    static double R2(double v) => Math.Round(v, 2);

    /// <summary>
    /// Gives the order made by <paramref name="make"/> to the selection; false (with the reason) when nobody is selected.
    /// </summary>
    bool ControlOrder(Func<IReadOnlyList<CharacterId>, SimCommand> make, string what, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        player.Say(make(who));
        message = $"{what} issued to {who.Count}: {string.Join(' ', who.Select(CharacterKey))}";
        return true;
    }

    public bool ControlStop(out string message) => ControlOrder(w => new StopCommand(w) { Tick = player.Session.World.Tick }, "stop", out message);

    public bool ControlFollow(CharacterId target, bool guard, out string message)
    {
        if (ControlCharacter(target) is not { } t) { message = $"refused: no character {CharacterKey(target)}"; return false; }
        if (ControlOrderers().Contains(target)) { message = "refused: a character cannot follow itself"; return false; }
        if (t.Body is { Dead: true }) { message = $"refused: {t.Name} is dead"; return false; }
        return ControlOrder(w => new FollowOrder(w, target) { Guard = guard, Tick = player.Session.World.Tick }, $"{(guard ? "bodyguard" : "follow")} {t.Name} {CharacterKey(target)}", out message);
    }

    static readonly (string Name, CharacterOrders Order)[] StanceNames =
        [("block", CharacterOrders.Block), ("hold", CharacterOrders.Hold), ("passive", CharacterOrders.Passive), ("ranged", CharacterOrders.Ranged), ("taunt", CharacterOrders.Taunt), ("sneak", CharacterOrders.Sneak), ("jobs", CharacterOrders.Jobs)];

    public static IReadOnlyList<string> StanceWords => [.. StanceNames.Select(s => s.Name)];

    /// <summary>
    /// The stance toggles on: <c>on</c> true/false sets, null flips (the orders panel's click).
    /// </summary>
    public bool ControlStance(string name, bool? on, out string message)
    {
        var found = StanceNames.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (found.Name is null) { message = $"refused: unknown stance '{name}' (block hold passive ranged taunt sneak jobs)"; return false; }
        return ControlOrder(w => new OrdersCommand(w, found.Order, on) { Tick = player.Session.World.Tick }, $"stance {found.Name} {(on is null ? "toggle" : on.Value ? "on" : "off")}", out message);
    }

    /// <summary>
    /// The ground item to pick up: an id from observe / ground-items, or the nearest to the lead character.
    /// </summary>
    public bool ControlPickup(string which, out string message)
    {
        var items = player.Session.CurrentSnapshot.GroundItems;
        var lead = player.Selected().FirstOrDefault() ?? player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer);
        if (lead is null) { message = "refused: no player character"; return false; }
        var from = player.Where(lead);
        GroundItemSnapshot? pick = null;
        if (which.Equals("nearest", StringComparison.OrdinalIgnoreCase))
        {
            float best = float.MaxValue;
            foreach (var g in items)
            {
                float d = Vector2.Distance(new Vector2(g.Position.X, g.Position.Z), new Vector2(from.X, from.Z));
                if (d < best) (best, pick) = (d, g);
            }
            if (pick is null) { message = "refused: no loose item in the world"; return false; }
        }
        else
        {
            if (!int.TryParse(which, NumberStyles.None, CultureInfo.InvariantCulture, out int id)) { message = "refused: pickup needs an item id (observe.groundItems) or 'nearest'"; return false; }
            foreach (var g in items) if (g.Id == id) pick = g;
            if (pick is null) { message = $"refused: no loose item {id}"; return false; }
        }
        int itemId = pick.Value.Id;
        if (!ControlOrder(w => new PickUpItemOrder(w, itemId) { Tick = player.Session.World.Tick }, $"pickup {pick.Value.Name} #{itemId}", out message)) return false;
        return true;
    }

    /// <summary>
    /// Loot a downed or dead character (everything that fits goes to the looter).
    /// </summary>
    public bool ControlLoot(CharacterId target, out string message)
    {
        if (ControlCharacter(target) is not { } t) { message = $"refused: no character {CharacterKey(target)}"; return false; }
        if (t.IsPlayer) { message = $"refused: {t.Name} is one of the player's own"; return false; }
        if (!(t.Body is { Dead: true } or { Unconscious: true }) && !t.CanOpenInventory && !t.Down) { message = $"refused: {t.Name} is standing (loot works on a dead, knocked-out or carried character)"; return false; }
        return ControlOrder(w => new Simulation.Carrying.LootOrder(w, target) { Tick = player.Session.World.Tick }, $"loot {t.Name} {CharacterKey(target)}", out message);
    }

    public bool ControlFirstAid(CharacterId patient, out string message)
    {
        if (ControlCharacter(patient) is not { } t) { message = $"refused: no character {CharacterKey(patient)}"; return false; }
        if (t.Body is { Dead: true }) { message = $"refused: {t.Name} is dead"; return false; }
        string note = t.Body is { NeedsAid: false, NeedsOrganicAid: false } ? " (note: the patient reports nothing to bandage)" : "";
        if (!ControlOrder(w => new Simulation.FirstAid.TreatOrder(w, patient) { Tick = player.Session.World.Tick }, $"firstaid {t.Name} {CharacterKey(patient)}", out message)) return false;
        message += note + " (needs a first aid kit in the medic's inventory)";
        return true;
    }

    // ---- construction

    BuildingBuildData? ControlBuildingData(string query, out string error)
    {
        error = "";
        if (player.Build.Services is not { } host) { error = "no build services (no world loaded)"; return null; }
        var all = host.Menu.Catalog;
        var byId = all.FirstOrDefault(b => b.Id.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        var named = Enumerable.Where<BuildingBuildData>(all, b => b.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 0) named = [.. Enumerable.Where<BuildingBuildData>(all, b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];
        if (named.Count == 1) return named[0];
        var unlocked = named.Where(b => player.Build.Unlocked().Contains(b.Id)).ToList();
        if (unlocked.Count == 1) return unlocked[0];
        error = named.Count == 0 ? $"no building '{query}' (build-list shows the types)" : $"'{query}' matches {named.Count} buildings; use the record id: {string.Join(", ", named.Take(6).Select(b => $"{b.Id} ({b.Name})"))}";
        return null;
    }

    /// <summary>
    /// The buildings the player may place now (or all with <paramref name="all"/>), with their material costs, as a JSON object.
    /// </summary>
    public string ControlBuildListJson(string text, bool all, int limit)
    {
        if (player.Build.Services is not { } host) return "{\"types\":[],\"total\":0}";
        var unlocked = player.Build.Unlocked();
        var list = Enumerable.Where<BuildingBuildData>(host.Menu.Catalog, b => (all || unlocked.Contains(b.Id)) && !b.IsNode
                                                                                                                       && (text.Length == 0 || b.Id.Contains(text, StringComparison.OrdinalIgnoreCase) || b.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || b.Category.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(b => b.Category, StringComparer.Ordinal).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("total", list.Count);
            w.WriteStartArray("types");
            foreach (var b in list.Take(limit))
            {
                w.WriteStartObject();
                w.WriteString("id", b.Id);
                w.WriteString("name", b.Name);
                w.WriteString("category", b.Category);
                w.WriteBoolean("unlocked", unlocked.Contains(b.Id));
                w.WriteStartArray("materials");
                foreach (var m in b.Materials)
                {
                    w.WriteStartObject();
                    w.WriteString("item", host.ItemName(m.Item));
                    w.WriteString("record", m.Item);
                    w.WriteNumber("required", R1(m.Required));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// What the selection holds of an item record (null when a selected character's grid is not published yet).
    /// </summary>
    int? ControlHolds(IReadOnlyList<CharacterId> who, string record)
    {
        int sum = 0;
        foreach (var id in who)
        {
            if (ControlCharacter(id)?.ItemGrid is not { } grid) return null;
            foreach (var it in grid.Items) if (it.Record == record) sum += it.Quantity;
        }
        return sum;
    }

    /// <summary>
    /// Build mode's confirm without the pointer: the ghost is judged by the same <see cref="PlacementCheck"/> the build ghost uses, then
    /// <see cref="PlaceBuildings"/> goes out (which also sends the selection to build it). Refused with the reason when the type is unknown or
    /// locked, the spot fails the placement rules, or the selection does not hold the materials (unless <paramref name="anyway"/>: a base store may
    /// supply them). <paramref name="json"/> is the structured reply.
    /// </summary>
    public bool ControlBuild(string type, float x, float z, float yawDegrees, bool anyway, out string message, out string json)
    {
        json = "";
        if (player.Build.Services is not { } host) { message = "refused: no build services (no world loaded)"; return false; }
        if (ControlBuildingData(type, out string error) is not { } data) { message = "refused: " + error; return false; }
        if (!player.Build.Unlocked().Contains(data.Id)) { message = $"refused: {data.Name} ({data.Id}) is not unlocked by research (build-list shows what is)"; return false; }
        float yaw = yawDegrees * MathF.PI / 180;
        var footprint = host.Placement.Footprint(data.Id);
        var box = footprint?.Bounds ?? (new Vector2(-4), new Vector2(4));
        var at = new Vector2(x, z);
        var normal = PlacementCheck.GroundNormal(host.Placement.Ground, at, yaw, box);
        float angle = MathF.Acos(Math.Clamp(normal.Y, -1, 1)) * (data.MatchSlope ? 1 : BuildMode.DefaultTilt);
        if (data.MaxSlope > 0) angle = MathF.Min(angle, data.MaxSlope * MathF.PI / 180);
        var axis = Vector3.Cross(Vector3.UnitY, normal);
        var tilt = axis.LengthSquared() < 1e-10f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
        var rotation = tilt * Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        var position = new Vector3(x, player.Ground(x, z), z);
        var snapshot = player.Session.CurrentSnapshot;
        var query = new PlacementQuery(data, position, rotation, yaw, 0, host.Parts(data.Id), [], snapshot.Characters, snapshot.Buildings, host.Towns(), host.PlayerFactionId(), host.Placement);
        if (PlacementCheck.Settle(query) is { } settled) { position.Y += settled.Step; query = query with { Position = position }; }
        var failure = PlacementCheck.Evaluate(query);
        if (failure != PlacementFailure.None)
        {
            message = $"refused: {PlacementCheck.Message(failure)} ({failure}) at {x:0.#} {z:0.#}";
            return false;
        }
        var who = ControlOrderers();
        var missing = new List<(string Item, float Required, int Have)>();
        bool unknown = false;
        foreach (var m in data.Materials)
        {
            if (ControlHolds(who, m.Item) is not { } have) { unknown = true; break; }
            if (have < m.Required) missing.Add((host.ItemName(m.Item), m.Required, have));
        }
        using (var stream = new MemoryStream())
        {
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("record", data.Id);
                w.WriteString("name", data.Name);
                WritePosition(w, "position", position);
                w.WriteNumber("yaw", R1(yawDegrees));
                w.WriteBoolean("placed", missing.Count == 0 || anyway);
                w.WriteStartArray("materials");
                foreach (var m in data.Materials)
                {
                    w.WriteStartObject();
                    w.WriteString("item", host.ItemName(m.Item));
                    w.WriteNumber("required", R1(m.Required));
                    if (!unknown) w.WriteNumber("held", ControlHolds(who, m.Item) ?? 0);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("missing");
                foreach (var (item, req, have) in missing) { w.WriteStartObject(); w.WriteString("item", item); w.WriteNumber("required", R1(req)); w.WriteNumber("held", have); w.WriteEndObject(); }
                w.WriteEndArray();
                w.WriteNumber("builders", who.Count);
                w.WriteEndObject();
            }
            json = Encoding.UTF8.GetString(stream.ToArray());
        }
        if (missing.Count > 0 && !anyway)
        {
            message = $"refused: {data.Name} needs {string.Join(", ", missing.Select(m => $"{m.Required:0.#} x {m.Item} (the selection holds {m.Have})"))}; give them to the builder, or pass --anyway to place the site and supply it from a store";
            return false;
        }
        player.Say(new PlaceBuildings([new PlannedBuilding(data.Id, position, rotation)]) { Tick = player.Session.World.Tick });
        message = $"placed {data.Name} ({data.Id}) at {position.X:0.#} {position.Y:0.#} {position.Z:0.#} yaw {yawDegrees:0.#}; {who.Count} selected character(s) sent to build it{(who.Count == 0 ? " (nobody is selected: select builders and then 'work <site id>': observe.buildings.own lists the site)" : "")}{(unknown ? " (materials not checked: select the builders a tick earlier)" : "")}{(missing.Count > 0 ? " (materials missing, --anyway)" : "")}";
        return true;
    }

    // ---- production

    public bool ControlOperate(string site, out string message)
    {
        string id = site.StartsWith("b:", StringComparison.OrdinalIgnoreCase) ? site[2..] : site;
        var snap = player.Session.CurrentSnapshot;
        var machine = snap.Machines.FirstOrDefault(m => m.Site == id);
        if (machine is null) { message = $"refused: no machine '{site}' (observe.machines lists them: a complete building of yours with a machine)"; return false; }
        if (!machine.Operable && !machine.IsAutomatic) { message = $"refused: {machine.Record} cannot be operated by hand"; return false; }
        return ControlOrder(w => new OperateOrder(w, id) { Tick = player.Session.World.Tick }, $"operate {machine.Record} {site}", out message);
    }

    public string ControlResearchJson()
    {
        var r = player.Session.CurrentSnapshot.Research;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            WriteResearch(w, r, 40);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    void WriteResearch(Utf8JsonWriter w, ResearchSnapshot r, int limit)
    {
        w.WriteStartObject();
        w.WriteNumber("techLevel", r.TechLevel);
        w.WriteNumber("benchLevel", r.BenchLevel);
        w.WriteNumber("rate", R2(r.Rate));
        w.WriteNumber("finished", r.Finished);
        w.WriteNumber("knownCount", r.Known.Count);
        w.WriteStartArray("queue");
        foreach (var q in r.Queue) { w.WriteStartObject(); w.WriteString("id", q.Id); w.WriteString("name", q.Name); w.WriteNumber("progress", R2(q.Progress)); w.WriteEndObject(); }
        w.WriteEndArray();
        w.WriteNumber("availableCount", r.Available.Count);
        w.WriteStartArray("available");
        foreach (var o in r.Available.Where(o => !r.Known.Contains(o.Id) || o.Progress > 0).Take(limit))
        {
            w.WriteStartObject();
            w.WriteString("id", o.Id);
            w.WriteString("name", o.Name);
            w.WriteNumber("level", o.Level);
            w.WriteNumber("hours", R1(o.Hours));
            w.WriteBoolean("affordable", o.Affordable);
            w.WriteBoolean("benchTooLow", o.BenchTooLow);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public bool ControlQueueResearch(string query, out string message)
    {
        var r = player.Session.CurrentSnapshot.Research;
        var hits = r.Available.Where(o => o.Id.Equals(query, StringComparison.OrdinalIgnoreCase) || o.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) hits = [.. r.Available.Where(o => o.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];
        if (hits.Count != 1) { message = hits.Count == 0 ? $"refused: no queueable research '{query}' (research list shows them)" : $"refused: '{query}' matches {hits.Count}: {string.Join(", ", hits.Take(6).Select(h => h.Name))}"; return false; }
        var o = hits[0];
        if (o.BenchTooLow) { message = $"refused: {o.Name} needs a higher research bench level"; return false; }
        if (r.Queue.Any(q => q.Id == o.Id)) { message = $"refused: {o.Name} is already queued"; return false; }
        player.Say(new QueueResearch(o.Id) { Tick = player.Session.World.Tick });
        message = $"research queued: {o.Name} ({o.Id}){(o.Paid || o.Affordable ? "" : "; its cost is not held by the benches, so it may not start")}";
        return true;
    }

    public bool ControlCraft(string site, string? recipe, int count, out string message, out string json)
    {
        json = "";
        string id = site.StartsWith("b:", StringComparison.OrdinalIgnoreCase) ? site[2..] : site;
        if (!player.Session.CurrentSnapshot.Crafts.TryGetValue(id, out var options)) { message = $"refused: '{site}' is not one of your crafting benches"; return false; }
        if (recipe is null)
        {
            json = JsonSerializer.Serialize(new { site = id, recipes = options.Take(60).Select(o => new { key = o.Key, name = o.Name, unlocked = o.Unlocked, hours = R1(o.Hours), inputs = o.Inputs.Select(i => new { i.Name, i.PerItem }) }) });
            message = $"{options.Count} recipes";
            return true;
        }
        var hits = options.Where(o => o.Key.Equals(recipe, StringComparison.OrdinalIgnoreCase) || o.Name.Equals(recipe, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) hits = [.. options.Where(o => o.Name.Contains(recipe, StringComparison.OrdinalIgnoreCase))];
        if (hits.Count != 1) { message = hits.Count == 0 ? $"refused: no recipe '{recipe}' at {site} (craft {site} lists them)" : $"refused: '{recipe}' matches {hits.Count}: {string.Join(", ", hits.Take(6).Select(h => h.Name))}"; return false; }
        if (!hits[0].Unlocked) { message = $"refused: {hits[0].Name} is not unlocked (research)"; return false; }
        player.Say(new QueueCraft(id, hits[0].Key, count) { Tick = player.Session.World.Tick });
        message = $"craft queued: {count} x {hits[0].Name} at {site} (a smith must work the bench: operate {site})";
        return true;
    }

    // ---- observe

    static string StanceText(CharacterOrders o) => string.Join(',', StanceNames.Where(s => (o & s.Order) != 0).Select(s => s.Name));

    void WriteXz(Utf8JsonWriter w, string name, Vector3 p)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(R1(p.X));
        w.WriteNumberValue(R1(p.Z));
        w.WriteEndArray();
    }

    /// <summary>
    /// One squad member for observe: everything the snapshot holds, which is more for a selected character (limbs, inventory, skills) than for the
    /// others.
    /// </summary>
    void WriteSquadMember(Utf8JsonWriter w, CharacterSnapshot c, bool full)
    {
        var at = player.Where(c);
        bool detail = c.Hud is not null || c.HealthParts.Count > 0;
        w.WriteStartObject();
        w.WriteString("id", CharacterKey(c.Id));
        w.WriteString("name", c.Name);
        w.WriteBoolean("selected", c.Selected);
        w.WriteString("state", StateWord(c));
        w.WriteString("detail", detail ? "full" : "basic (select the character for limbs, inventory and skills)");
        WritePosition(w, "position", at);
        if (c.HealthParts.Count > 0)
        {
            float flesh = c.HealthParts.Sum(p => p.Flesh), max = c.HealthParts.Sum(p => p.Maximum);
            w.WriteNumber("hp", R1(flesh));
            w.WriteNumber("hpMax", R1(max));
        }
        else { w.WriteNull("hp"); w.WriteNull("hpMax"); }
        if (c.Body is { } b)
        {
            w.WriteNumber("worstLimb", R2(b.WorstPart));   // fraction of the worst body part's maximum (below 0 means that limb is past its KO point)
            w.WriteNumber("blood", R2(b.Blood));
            w.WriteNumber("hunger", R2(b.Hunger));   // 3 fed .. 0 starving; characters eat food they carry by themselves
            w.WriteBoolean("needsAid", b.NeedsAid);
            if (b.Unconscious) w.WriteNumber("koSeconds", R1(b.KoTimer));
        }
        else { w.WriteNull("worstLimb"); w.WriteNull("blood"); w.WriteNull("hunger"); }
        if (c.Hud is { } hud) w.WriteNumber("bleeding", R2(hud.Bleeding)); else w.WriteNull("bleeding");
        if (c.HealthParts.Count > 0)
        {
            w.WriteStartArray("limbs");
            foreach (var p in c.HealthParts)
            {
                if (!full && p.Maximum > 0 && p.Flesh >= p.Maximum * 0.999f) continue;
                w.WriteStartObject();
                w.WriteString("part", p.Name);
                w.WriteNumber("hp", R1(p.Flesh));
                w.WriteNumber("max", R1(p.Maximum));
                if (p.Bandage > 0) w.WriteNumber("bandage", R1(p.Bandage));
                if (p.Limb != Simulation.Bodies.LimbState.Original) w.WriteString("limb", p.Limb.ToString());
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        else w.WriteNull("limbs");
        w.WriteString("task", c.Task.ToString());
        w.WriteString("order", c.Activity is { Length: > 0 } a ? a : c.Task.ToString());
        w.WriteBoolean("inCombat", c.InCombat);
        w.WriteBoolean("weaponDrawn", c.WeaponDrawn);
        w.WriteString("stance", StanceText(c.Orders));
        if (c.ItemGrid is { } grid)
        {
            w.WriteStartObject("inventory");
            w.WriteNumber("weightKg", R1(grid.TotalWeight));
            if (grid.CarryCapacity is { } cap) w.WriteNumber("capacityKg", R1(cap)); else w.WriteNull("capacityKg");
            w.WriteNumber("count", grid.Items.Count);
            w.WriteStartArray("items");
            foreach (var it in grid.Items.Take(full ? 120 : 40))
            {
                w.WriteStartObject();
                w.WriteString("name", it.Name);
                w.WriteString("record", it.Record);
                w.WriteNumber("qty", it.Quantity);
                w.WriteString("slot", it.Section);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        else w.WriteNull("inventory");
        if (c.Skills is { } s)
        {
            w.WriteStartObject("skills");
            w.WriteNumber("attack", R1(s.Attack)); w.WriteNumber("defence", R1(s.Defence)); w.WriteNumber("dodge", R1(s.Dodge));
            w.WriteNumber("toughness", R1(s.Toughness)); w.WriteNumber("strength", R1(s.Strength)); w.WriteNumber("athletics", R1(s.Athletics));
            w.WriteEndObject();
        }
        else w.WriteNull("skills");
        if (full && c.Hud is { } h)
        {
            w.WriteStartObject("stats");
            foreach (var line in h.Stats) if (line.Text is null) w.WriteNumber(line.Label, R1(line.Value));
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    void WriteNearby(Utf8JsonWriter w, CharacterSnapshot c, Vector3 from, bool threat)
    {
        var at = player.Where(c);
        w.WriteStartObject();
        w.WriteString("id", CharacterKey(c.Id));
        w.WriteString("name", c.Name);
        w.WriteString("faction", player.FactionName?.Invoke(c.Faction));
        w.WriteNumber("dist", R1(Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(from.X, from.Z))));
        WriteXz(w, "pos", at);
        w.WriteString("state", StateWord(c));
        if (c.Body is { } b) w.WriteNumber("worstLimb", R2(b.WorstPart)); else w.WriteNull("worstLimb");
        if (threat)
        {
            w.WriteBoolean("inCombat", c.InCombat);
            w.WriteBoolean("armed", c.WeaponDrawn);
            w.WriteString("task", c.Task.ToString());
        }
        else if (c.Body is { Unconscious: true } or { Dead: true }) w.WriteBoolean("lootable", c.CanOpenInventory || c.Body is { Dead: true });
        w.WriteEndObject();
    }

    /// <summary>
    /// The values of the last <c>observe --brief</c> (raw JSON per field), for <c>--changed</c>.
    /// </summary>
    Dictionary<string, string>? _lastBrief;

    /// <summary>
    /// The terse state for polling (<c>observe --brief</c>, about 200 bytes): tick, clock, paused, speed, the lead's id, name, position [x,z], state,
    /// hp fraction, task, order, the squad as "alive/total", the nearest hostile's distance (null: none) and money. With
    /// <paramref name="changedOnly"/> only the fields that differ from the last brief call (always <c>tick</c>); the first call gives all.
    /// </summary>
    public string ControlBriefJson(bool changedOnly)
    {
        var snap = player.Session.CurrentSnapshot;
        var world = player.Session.World;
        var squad = snap.Characters.Where(c => c.IsPlayer).ToList();
        var lead = squad.FirstOrDefault(c => c.Selected) ?? squad.FirstOrDefault();
        var inv = CultureInfo.InvariantCulture;
        static string Str(string? v) => v is null ? "null" : JsonSerializer.Serialize(v);
        var f = new List<(string Key, string Value)>();
        double hour = player.Session.Clock.HourOfDay;
        f.Add(("tick", world.Tick.ToString(inv)));
        f.Add(("clock", Str($"{(int)hour:00}:{(int)((hour - Math.Floor(hour)) * 60):00}")));
        f.Add(("paused", player.Session.Simulation.IsPaused ? "true" : "false"));
        f.Add(("speed", player.Session.Simulation.Speed.ToString(inv)));
        if (lead is null) { f.Add(("lead", "null")); }
        else
        {
            var at = player.Where(lead);
            f.Add(("lead", Str(CharacterKey(lead.Id))));
            f.Add(("name", Str(lead.Name)));
            f.Add(("squadName", Str(snap.PlayerSquads.FirstOrDefault(q => q.Members.Contains(lead.Id))?.Name)));
            f.Add(("pos", $"[{Math.Round(at.X, 1).ToString(inv)},{Math.Round(at.Z, 1).ToString(inv)}]"));
            f.Add(("state", Str(StateWord(lead))));
            double max = lead.HealthParts.Sum(p => (double)p.Maximum);
            f.Add(("hp", max > 0 ? Math.Round(lead.HealthParts.Sum(p => (double)Math.Max(0, p.Flesh)) / max, 2).ToString(inv) : "null"));
            f.Add(("task", Str(lead.Task.ToString())));
            f.Add(("order", Str(lead.Activity is { Length: > 0 } a ? a : lead.Task.ToString())));
            var flat = new Vector2(at.X, at.Z);
            double? threat = null;
            foreach (var c in snap.Characters)
            {
                if (c.IsPlayer || c.Body is { Dead: true } or { Unconscious: true } || !(c.HostileToPlayer || c.FightsPlayer)) continue;
                var p = player.Where(c);
                double d = Vector2.Distance(new Vector2(p.X, p.Z), flat);
                if (threat is null || d < threat) threat = d;
            }
            f.Add(("threat", threat is { } t ? Math.Round(t, 1).ToString(inv) : "null"));
            f.Add(("combat", Str(ControlCombatLine(lead))));
        }
        f.Add(("squad", Str($"{squad.Count(c => c.Body is { Dead: false, Unconscious: false })}/{squad.Count}")));
        f.Add(("money", world.Player.Money.ToString(inv)));
        var previous = _lastBrief;
        _lastBrief = f.ToDictionary(x => x.Key, x => x.Value);
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var (key, value) in f)
        {
            if (changedOnly && previous is not null && key != "tick" && previous.TryGetValue(key, out var old) && old == value) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(key).Append("\":").Append(value);
        }
        return sb.Append('}').ToString();
    }

    /// <summary>
    /// The one JSON for a decision (docs/procedures/agent-play-api.md): time, speed, the squad, what is near (threats, allies, neutrals, animals,
    /// traders, bodies), loose items, buildings, containers, machines, research, resources (null: no cheap listing exists) and a score object. Lists
    /// are cut at a fixed length with the full count beside them.
    /// </summary>
    public string ControlObserveJson(float radius, bool full)
    {
        var snap = player.Session.CurrentSnapshot;
        var world = player.Session.World;
        var squad = snap.Characters.Where(c => c.IsPlayer).ToList();
        var lead = squad.FirstOrDefault(c => c.Selected) ?? squad.FirstOrDefault();
        var from = lead is null ? player.Session.Camera.Current.Target : player.Where(lead);
        var flat = new Vector2(from.X, from.Z);
        float D(Vector3 p) => Vector2.Distance(new Vector2(p.X, p.Z), flat);
        int listCap = full ? 40 : 12;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("tick", world.Tick);
            w.WriteStartObject("time");
            double hour = player.Session.Clock.HourOfDay;
            w.WriteNumber("day", (int)player.Session.Clock.Day);
            w.WriteNumber("hour", R2(hour));
            w.WriteString("clock", $"{(int)hour:00}:{(int)((hour - Math.Floor(hour)) * 60):00}");
            w.WriteBoolean("daytime", world.Daytime);
            w.WriteNumber("gameHours", R2(world.GameHours));
            w.WriteEndObject();
            w.WriteBoolean("paused", player.Session.Simulation.IsPaused);
            w.WriteNumber("speed", player.Session.Simulation.Speed);
            WritePosition(w, "origin", from);
            w.WriteNumber("radius", radius);

            // Squad
            w.WriteNumber("squadCount", squad.Count);
            w.WriteStartArray("squad");
            foreach (var c in squad.OrderByDescending(c => c.Selected).Take(full ? 20 : 8)) WriteSquadMember(w, c, full);
            w.WriteEndArray();
            w.WriteNumber("money", world.Player.Money);

            // Nearby characters
            var threats = new List<(float D, CharacterSnapshot C)>();
            var allies = new List<(float D, CharacterSnapshot C)>();
            var neutrals = new List<(float D, CharacterSnapshot C)>();
            var animals = new List<(float D, CharacterSnapshot C)>();
            var traders = new List<(float D, CharacterSnapshot C)>();
            var bodies = new List<(float D, CharacterSnapshot C)>();
            foreach (var c in snap.Characters)
            {
                if (c.IsPlayer) continue;
                float d = D(player.Where(c));
                if (d > radius) continue;
                if (c.Body is { Dead: true } or { Unconscious: true } || !c.CarriedBy.IsNone) bodies.Add((d, c));
                else if (c.HostileToPlayer || c.FightsPlayer) threats.Add((d, c));   // the game's own flags: hostile by faction relation, or fighting the player's characters; animals too (Bonedogs)
                else if (c.Animal) animals.Add((d, c));   // passive wildlife
                else if (c.IsTrader) traders.Add((d, c));
                else if (lead is not null && c.Faction == lead.Faction && c.Faction >= 0) allies.Add((d, c));
                else neutrals.Add((d, c));
            }
            w.WriteStartObject("nearby");
            void List(string name, List<(float D, CharacterSnapshot C)> list, bool threat)
            {
                w.WriteNumber(name + "Count", list.Count);
                w.WriteStartArray(name);
                foreach (var (_, c) in list.OrderBy(i => i.D).Take(listCap)) WriteNearby(w, c, from, threat);
                w.WriteEndArray();
            }
            if (threats.Count > 0) w.WriteNumber("nearestThreat", R1(threats.Min(t => t.D))); else w.WriteNull("nearestThreat");
            List("threats", threats, true);
            List("allies", allies, false);
            List("neutrals", neutrals, false);
            List("animals", animals, true);
            List("traders", traders, false);
            List("bodies", bodies, false);
            w.WriteEndObject();

            // Loose items
            var items = snap.GroundItems.Select(g => (D: D(g.Position), G: g)).Where(i => i.D <= radius).OrderBy(i => i.D).ToList();
            w.WriteNumber("groundItemsCount", items.Count);
            w.WriteStartArray("groundItems");
            foreach (var (d, g) in items.Take(full ? 40 : 15))
            {
                w.WriteStartObject();
                w.WriteNumber("id", g.Id);
                w.WriteString("name", g.Name);
                w.WriteNumber("dist", R1(d));
                WriteXz(w, "pos", g.Position);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // Buildings
            w.WriteStartObject("buildings");
            var own = snap.Buildings.Where(b => b.Owner == world.Player.Faction && b.LevelPlacement is null).ToList();
            w.WriteNumber("ownCount", own.Count);
            w.WriteStartArray("own");
            foreach (var b in own.OrderBy(b => D(b.Position)).Take(full ? 40 : 15))
            {
                w.WriteStartObject();
                w.WriteString("id", "b:" + b.Id);
                w.WriteString("name", player.Build.SiteName(b));
                w.WriteString("record", b.Record);
                WriteXz(w, "pos", b.Position);
                w.WriteNumber("dist", R1(D(b.Position)));
                w.WriteBoolean("complete", b.Complete);
                w.WriteNumber("progress", b.Target > 0 ? R2(b.Progress / b.Target) : 1);
                if (b.Destroyed) w.WriteBoolean("destroyed", true);
                if (!b.Complete)
                {
                    w.WriteStartArray("materials");
                    foreach (var m in b.Materials.Where(m => m.Delivered < m.Required).Take(6))
                    {
                        w.WriteStartObject();
                        w.WriteString("item", player.Build.Services?.ItemName(m.Item) ?? m.Item);
                        w.WriteNumber("required", R1(m.Required));
                        w.WriteNumber("delivered", R1(m.Delivered));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            var near = new List<(float D, string Id, string Name, Vector3 At)>();
            if (player.Build.Services is { } host)
                foreach (var p in host.Placement.StaticPlacementsAround(flat, radius))
                {
                    if (player.LevelEditorPanel.Services?.IsCleared(p.Key) == true) continue;
                    float y = p.WorldY ?? player.Ground(p.Position.X, p.Position.Z) + p.Position.Y;
                    var at = new Vector3(p.Position.X, y, p.Position.Z);
                    float d = D(at);
                    if (d > radius) continue;
                    near.Add((d, "b:" + p.Key, host.WorldInfo(p).Name, at));
                }
            w.WriteNumber("nearCount", near.Count);
            w.WriteStartArray("near");
            foreach (var n in near.OrderBy(n => n.D).Take(full ? 40 : 10))
            {
                w.WriteStartObject();
                w.WriteString("id", n.Id);
                w.WriteString("name", n.Name);
                WriteXz(w, "pos", n.At);
                w.WriteNumber("dist", R1(n.D));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            // Containers (fixture ids, for container-put / building-info)
            var boxes = snap.Containers.Select(c => (D: D(c.Position), C: c)).Where(i => i.D <= radius).OrderBy(i => i.D).ToList();
            w.WriteNumber("containersCount", boxes.Count);
            w.WriteStartArray("containers");
            foreach (var (d, c) in boxes.Take(full ? 30 : 8))
            {
                w.WriteStartObject();
                w.WriteString("id", c.Id);
                w.WriteString("record", c.Record);
                WriteXz(w, "pos", c.Position);
                w.WriteNumber("dist", R1(d));
                w.WriteBoolean("locked", c.Locked);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // Machines of your complete buildings
            w.WriteNumber("machinesCount", snap.Machines.Count(m => !m.IsContainer));
            w.WriteStartArray("machines");
            foreach (var m in snap.Machines.Where(m => !m.IsContainer).Take(full ? 40 : 12))
            {
                var bs = snap.Buildings.FirstOrDefault(b => b.Id == m.Site);
                w.WriteStartObject();
                w.WriteString("id", "b:" + m.Site);
                w.WriteString("name", bs is null ? m.Record : player.Build.SiteName(bs));
                w.WriteNumber("status", m.Status);
                w.WriteNumber("operators", m.Operators);
                w.WriteNumber("maxOperators", m.MaxOperators);
                w.WriteBoolean("operable", m.Operable);
                w.WriteBoolean("automatic", m.IsAutomatic);
                if (bs is not null) w.WriteNumber("dist", R1(D(bs.Position)));
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WritePropertyName("research");
            WriteResearch(w, snap.Research, full ? 30 : 8);
            if (ControlResourcesJson(flat) is { } resources) { w.WritePropertyName("resources"); w.WriteRawValue(resources); }
            else w.WriteNull("resources");   // null until 'prospect' has run in this world

            // Score
            int alive = squad.Count(c => c.Body is { Dead: false, Unconscious: false });
            var parted = squad.Where(c => c.HealthParts.Count > 0).ToList();
            w.WriteStartObject("score");
            w.WriteNumber("alive", alive);
            w.WriteNumber("squad", squad.Count);
            if (parted.Count > 0) w.WriteNumber("hpFraction", R2(parted.Sum(c => c.HealthParts.Sum(p => Math.Max(0, p.Flesh))) / Math.Max(1, parted.Sum(c => c.HealthParts.Sum(p => p.Maximum)))));
            else w.WriteNull("hpFraction");
            w.WriteNumber("money", world.Player.Money);
            w.WriteNumber("buildingsOwned", own.Count);
            w.WriteNumber("buildingsBuilt", own.Count(b => b.Complete));
            w.WriteNumber("kills", ControlKills());   // hostile characters seen dead since the load
            w.WriteNumber("elapsedGameSeconds", R1(world.Tick * world.TickSeconds));
            w.WriteNumber("tick", world.Tick);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // More of the agent control channel's play API (docs/procedures/agent-play-api.md): work on a site, prospect (and the last prospecting as observe.resources), equip, carry, trade, and the kill counter.
    // Same rules as the rest of the play API: the game's own commands, to the selection, refused with a reason.
    // ---- work on a site

    /// <summary>
    /// A <see cref="Meitou.Simulation.Construction.BuildOrder"/> on a site of yours (build it, or repair it when it is complete and damaged) for the
    /// selection.
    /// </summary>
    public bool ControlWork(string site, out string message)
    {
        string id = site.StartsWith("b:", StringComparison.OrdinalIgnoreCase) ? site[2..] : site;
        var found = player.Session.CurrentSnapshot.Buildings.FirstOrDefault(b => b.Id == id && b.LevelPlacement is null);
        if (found is null) { message = $"refused: no site '{site}' of yours (observe.buildings.own lists them)"; return false; }
        if (found.Owner != player.Session.World.Player.Faction) { message = $"refused: {site} is not yours"; return false; }
        if (found.Dismantling) { message = $"refused: {site} is being dismantled"; return false; }
        if (found.Complete && !found.NeedsWork) { message = $"refused: {player.Build.SiteName(found)} is complete and undamaged, nothing to do"; return false; }
        return ControlOrder(w => new BuildOrder(w, id) { Tick = player.Session.World.Tick }, $"{(found.Complete ? "repair" : "build")} {player.Build.SiteName(found)} b:{id}", out message);
    }

    // ---- prospecting

    /// <summary>
    /// ProspectOrder for the first selected character (the HUD's Prospect button); the result is in observe.resources after about a game second.
    /// </summary>
    public bool ControlProspect(out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        if (ControlCharacter(who[0]) is { } c && c.Task == CharacterTask.Prospect) { message = $"refused: {c.Name} is already prospecting"; return false; }
        player.Say(new ProspectOrder(who[0]) { Tick = player.Session.World.Tick });
        message = $"prospect issued to {CharacterKey(who[0])}; takes about a game second, then observe.resources holds the result (step 60)";
        return true;
    }

    /// <summary>
    /// The newest prospecting of this world as JSON (spot, zone, coverages, the iron and copper rocks nearest the spot with their yields); null when
    /// none was made.
    /// </summary>
    string? ControlResourcesJson(Vector2 near)
    {
        if (player.Session.World.Prospected is not { } r) return null;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("serial", r.Serial);
            WritePosition(w, "spot", r.Position);
            w.WriteString("prospector", r.Prospector);
            w.WriteString("zone", r.Zone);
            w.WriteNumber("altitude", r.Altitude);
            w.WriteNumber("areaMetres", r.AreaMetres);
            w.WriteStartObject("environment");
            foreach (var l in r.Environment) w.WriteNumber(l.Name, l.Percent);
            w.WriteEndObject();
            w.WriteStartObject("values");
            foreach (var l in r.Resources) w.WriteNumber(l.Name, l.Percent);
            w.WriteEndObject();
            w.WriteStartArray("rocks");
            if (r.Map is { } map)
                for (int i = 0; i < map.Rocks.Count && i < r.Resources.Count; i++)
                    foreach (var rock in map.Rocks[i].OrderBy(k => Vector2.Distance(new Vector2(k.X, k.Z), near)).Take(8))
                    {
                        w.WriteStartObject();
                        w.WriteString("resource", r.Resources[i].Name);
                        w.WriteNumber("x", Math.Round(rock.X, 1));
                        w.WriteNumber("z", Math.Round(rock.Z, 1));
                        w.WriteNumber("value", Math.Round(rock.Value, 2));
                        w.WriteNumber("dist", R1(Vector2.Distance(new Vector2(rock.X, rock.Z), near)));
                        w.WriteEndObject();
                    }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ---- equip, carry

    /// <summary>
    /// The right click on an item of the first selected character (<see cref="QuickMoveItem"/>): the item goes to the slot that takes it, a worn one
    /// back to the grid.
    /// </summary>
    public bool ControlEquip(string what, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        if (ControlCharacter(who[0])?.ItemGrid is not { } grid) { message = "refused: the inventory is not published yet (step a tick)"; return false; }
        var hits = grid.Items.Where(i => i.Name.Equals(what, StringComparison.OrdinalIgnoreCase) || i.Record.Equals(what, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) hits = [.. grid.Items.Where(i => i.Name.Contains(what, StringComparison.OrdinalIgnoreCase))];
        if (hits.Count == 0) { message = $"refused: {CharacterKey(who[0])} holds no item '{what}' (observe.squad[].inventory)"; return false; }
        var item = hits[0];
        player.Say(new QuickMoveItem(who[0], new InventoryItemToken(item.Index, item.Record, item.Section, item.X, item.Y, item.Quantity, item.Quality), false, false) { Tick = player.Session.World.Tick });
        message = $"equip (right click) {item.Name} [{item.Section}] of {CharacterKey(who[0])}{(hits.Count > 1 ? $"; {hits.Count - 1} more items match" : "")}";
        return true;
    }

    /// <summary>
    /// Takes a worn item off: <paramref name="what"/> is a slot (head, shirt, armour, legs, boots, back, hip, belt) or an item name; the right click
    /// of a worn item sends it to the grids (the reply cannot know if they had room: observe.squad[].inventory shows).
    /// </summary>
    public bool ControlUnequip(string what, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        if (ControlCharacter(who[0])?.ItemGrid is not { } grid) { message = "refused: the inventory is not published yet (step a tick)"; return false; }
        var worn = grid.Items.Where(i => InventoryCommands.IsEquipmentSection(i.Section) && i.Section != "backpack_attach").ToList();
        var item = worn.FirstOrDefault(i => i.Section.Equals(what, StringComparison.OrdinalIgnoreCase)) ?? worn.FirstOrDefault(i => i.Name.Equals(what, StringComparison.OrdinalIgnoreCase)) ?? worn.FirstOrDefault(i => i.Name.Contains(what, StringComparison.OrdinalIgnoreCase));
        if (item is null) { message = $"refused: {CharacterKey(who[0])} wears nothing matching '{what}' (worn slots: {string.Join(", ", worn.Select(i => i.Section))})"; return false; }
        player.Say(new QuickMoveItem(who[0], new InventoryItemToken(item.Index, item.Record, item.Section, item.X, item.Y, item.Quantity, item.Quality), false, false) { Tick = player.Session.World.Tick });
        message = $"unequip (right click) {item.Name} [{item.Section}] of {CharacterKey(who[0])}";
        return true;
    }

    /// <summary>
    /// PickUpOrder: the selection lifts a downed character (carry them, or kidnap a stranger).
    /// </summary>
    public bool ControlCarry(CharacterId target, out string message)
    {
        if (ControlCharacter(target) is not { } t) { message = $"refused: no character {CharacterKey(target)}"; return false; }
        if (ControlOrderers().Contains(target)) { message = "refused: a character cannot carry itself"; return false; }
        return ControlOrder(w => new Simulation.Carrying.PickUpOrder(w, target) { Tick = player.Session.World.Tick }, $"carry {t.Name} {CharacterKey(target)}", out message);
    }

    public bool ControlPutDown(out string message) => ControlOrder(w => new Simulation.Carrying.PutDownOrder(w) { Tick = player.Session.World.Tick }, "putdown", out message);

    // ---- trade

    /// <summary>
    /// OpenTrade: the first selected character walks to the trader; the window opens in reach (then <c>trade</c> shows it).
    /// </summary>
    public bool ControlTradeOpen(CharacterId trader, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        if (ControlCharacter(trader) is not { } t) { message = $"refused: no character {CharacterKey(trader)}"; return false; }
        if (!t.IsTrader) { message = $"refused: {t.Name} is not a trader (observe.nearby.traders)"; return false; }
        player.Say(new OpenTrade(who[0], trader) { Tick = player.Session.World.Tick });
        message = $"trade open issued: {CharacterKey(who[0])} walks to {t.Name}; step, then 'trade' shows the stock and prices";
        return true;
    }

    public bool ControlTradeClose(out string message)
    {
        if (player.Session.CurrentSnapshot.Trade is null) { message = "refused: no trade is open"; return false; }
        player.Say(new CloseTrade { Tick = player.Session.World.Tick });
        message = "trade closed";
        return true;
    }

    /// <summary>
    /// The open trade (stock the shop sells, goods of the customer it would buy, both with prices) as JSON; false when none is open.
    /// </summary>
    public bool ControlTradeJson(out string json)
    {
        json = "";
        if (player.Session.CurrentSnapshot.Trade is not { } t) return false;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            void Items(string name, IReadOnlyList<TradeItemView> list)
            {
                w.WriteNumber(name + "Count", list.Count);
                w.WriteStartArray(name);
                foreach (var i in list.Take(60))
                {
                    w.WriteStartObject();
                    w.WriteString("name", i.Name);
                    w.WriteString("record", i.Record);
                    w.WriteNumber("qty", i.Quantity);
                    w.WriteNumber("price", i.Price);
                    w.WriteNumber("unitPrice", i.UnitPrice);
                    if (i.Stolen) w.WriteBoolean("stolen", true);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteStartObject();
            w.WriteString("customer", CharacterKey(t.Customer));
            w.WriteString("trader", CharacterKey(t.Trader));
            w.WriteString("traderName", t.TraderName);
            w.WriteNumber("shopMoney", t.ShopMoney);
            w.WriteNumber("playerMoney", t.PlayerMoney);
            Items("stock", t.Stock);
            Items("goods", t.Goods);
            w.WriteEndObject();
        }
        json = Encoding.UTF8.GetString(stream.ToArray());
        return true;
    }

    /// <summary>
    /// Buys (<paramref name="buy"/>) or sells <paramref name="count"/> units of the item named <paramref name="what"/> in the open trade.
    /// </summary>
    public bool ControlTradeDeal(bool buy, string what, int count, out string message)
    {
        if (player.Session.CurrentSnapshot.Trade is not { } t) { message = "refused: no trade is open (trade open <trader>, step, then trade)"; return false; }
        var list = buy ? t.Stock : t.Goods;
        var hits = list.Where(i => i.Name.Equals(what, StringComparison.OrdinalIgnoreCase) || i.Record.Equals(what, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) hits = [.. list.Where(i => i.Name.Contains(what, StringComparison.OrdinalIgnoreCase))];
        if (hits.Count == 0) { message = $"refused: the {(buy ? "stock" : "goods")} hold no '{what}' ('trade' lists them)"; return false; }
        var item = hits[0];
        int n = Math.Clamp(count, 1, item.Quantity);
        long cost = item.UnitPrice * n;
        if (buy && cost > t.PlayerMoney) { message = $"refused: {n} x {item.Name} costs {cost}, you have {t.PlayerMoney}"; return false; }
        if (!buy && cost > t.ShopMoney) { message = $"refused: the shop pays at most {t.ShopMoney} (this is {cost})"; return false; }
        if (buy) player.Say(new BuyItem(item.Token, n) { Tick = player.Session.World.Tick, Equip = false, Into = t.Customer });
        else player.Say(new SellItem(item.Token, n) { Tick = player.Session.World.Tick, From = t.Customer });
        message = $"{(buy ? "buy" : "sell")} {n} x {item.Name} for about {cost} issued";
        return true;
    }

    /// <summary>
    /// One character as observe.nearby lists it (id, name, faction, dist from the lead, pos, state), as JSON; null when it is not in the snapshot.
    /// </summary>
    public string? ControlCharacterJson(CharacterId id)
    {
        if (ControlCharacter(id) is not { } c) return null;
        var lead = player.Selected().FirstOrDefault() ?? player.Session.CurrentSnapshot.Characters.FirstOrDefault(p => p.IsPlayer);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream)) WriteNearby(w, c, lead is null ? player.Session.Camera.Current.Target : player.Where(lead), true);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Character ids in the snapshot with how far each is from (x, z), and whether it is down (dead, knocked out or carried): the spawn command's
    /// lookup.
    /// </summary>
    public IEnumerable<(CharacterId Id, float Dist, bool Down, bool Player)> ControlCharactersNear(float x, float z) =>
        player.Session.CurrentSnapshot.Characters.Select(c => (c.Id, Vector2.Distance(new Vector2(player.Where(c).X, player.Where(c).Z), new Vector2(x, z)), c.Body is { Dead: true } or { Unconscious: true }, c.IsPlayer));

    // ---- kill counter

    object? _killsWorld;
    readonly HashSet<CharacterId> _hostileSeen = [], _hostileDead = [];

    /// <summary>
    /// Notes the hostile (non-player, non-animal, hostile to the player or fighting it) characters that are dead; a set of ids, reset when another
    /// world is loaded.
    /// </summary>
    public int ControlKills()
    {
        if (!ReferenceEquals(_killsWorld, player.Session.World)) { _killsWorld = player.Session.World; _hostileSeen.Clear(); _hostileDead.Clear(); }
        foreach (var c in player.Session.CurrentSnapshot.Characters)
        {
            if (c.IsPlayer || c.Animal) continue;
            bool hostile = c.HostileToPlayer || c.FightsPlayer;
            if (c.Body is { Dead: true }) { if (hostile || _hostileSeen.Contains(c.Id)) _hostileDead.Add(c.Id); }
            else if (hostile) _hostileSeen.Add(c.Id);
        }
        return _hostileDead.Count;
    }
}
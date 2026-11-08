using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The agent control channel's view of the world (docs/procedures/todo-faster-ingame-agent-tests.md; AgentWorld has the commands): entity lists, screen positions, picking and the selection and order calls.
/// Everything reads the published snapshot and the picking code the pointer uses; orders go out as the same <see cref="SimCommand"/>s the player's input gives. Entity ids: <c>c:&lt;slot&gt;.&lt;generation&gt;</c> for a character,
/// <c>b:&lt;instance key&gt;</c> for a building (a site of the player's or a level-data building).
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>A building seen by the control channel, kept by id so a later command can find it again (level buildings have no lookup by key).</summary>
    sealed record ControlBuilding(string Id, string Name, string? Record, Vector3 Position, Vector2[]? Hull);

    readonly Dictionary<string, ControlBuilding> controlBuildings = [];
    /// <summary>The ids of the last select command, until the world applies it (an order given in the same breath still counts them).</summary>
    IReadOnlyList<CharacterId> controlSelectIssued = [];
    long controlSelectTick = -1;

    static string CharacterKey(CharacterId id) => $"c:{id.Slot}.{id.Generation}";

    /// <summary>Parses <c>c:3.1</c> (or <c>3.1</c>) into a character id; false for anything else.</summary>
    public static bool TryParseCharacter(string text, out CharacterId id)
    {
        id = CharacterId.None;
        if (text.StartsWith("c:", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        string[] parts = text.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int slot) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int generation)) return false;
        id = new CharacterId(slot, generation);
        return true;
    }

    CharacterSnapshot? ControlCharacter(CharacterId id) => session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id);

    /// <summary>Where an entity id stands (a character as drawn, a building at its position); null when the id names nothing known.</summary>
    public Vector3? ControlPosition(string id)
    {
        if (id.StartsWith("b:", StringComparison.OrdinalIgnoreCase))
        {
            string key = id[2..];
            if (session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == key) is { } site) return site.Position;
            return controlBuildings.TryGetValue(id, out var known) ? known.Position : null;
        }
        return TryParseCharacter(id, out var cid) && ControlCharacter(cid) is { } c ? where(c) : null;
    }

    /// <summary>The first selected character's drawn position (waits and the state use it).</summary>
    public Vector3? ControlLeadPosition() => Selected().FirstOrDefault() is { } lead ? where(lead) : null;

    bool OnScreen(Vector2 s) => s.X >= 0 && s.X <= Width && s.Y >= 0 && s.Y <= Height;

    void WritePosition(Utf8JsonWriter w, string name, Vector3 p)
    {
        w.WriteStartArray(name);
        foreach (float v in new[] { p.X, p.Y, p.Z }) w.WriteNumberValue(Math.Round(v, 1));
        w.WriteEndArray();
    }

    void WriteScreen(Utf8JsonWriter w, Vector3 world)
    {
        var s = Project(world);
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
        var at = where(c);
        w.WriteStartObject();
        w.WriteString("id", CharacterKey(c.Id));
        w.WriteString("name", c.Name);
        w.WriteString("kind", "character");
        w.WriteString("faction", FactionName?.Invoke(c.Faction));
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
        WriteScreen(w, at + new Vector3(0, CharacterHeight / 2, 0));
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

    /// <summary>The entities within <paramref name="radius"/> of <paramref name="near"/> (X, Z), nearest first, as a JSON array; <paramref name="kind"/> is character, building or all.</summary>
    public string ControlWorldJson(Vector3 near, float radius, string kind, int limit)
    {
        var items = new List<(float D, Action<Utf8JsonWriter> Write)>();
        var flat = new Vector2(near.X, near.Z);
        if (kind is "character" or "all")
            foreach (var c in session.CurrentSnapshot.Characters)
            {
                var at = where(c);
                float d = Vector2.Distance(new Vector2(at.X, at.Z), flat);
                if (d <= radius) items.Add((d, w => WriteCharacter(w, c, near)));
            }
        if (kind is "building" or "all")
        {
            foreach (var s in session.CurrentSnapshot.Buildings)
            {
                float d = Vector2.Distance(new Vector2(s.Position.X, s.Position.Z), flat);
                if (d > radius) continue;
                var b = new ControlBuilding("b:" + s.Id, SiteName(s), s.Record, s.Position, null);
                controlBuildings["b:" + s.Id] = b;
                items.Add((d, w => WriteBuilding(w, b, near)));
            }
            if (buildHost is { } host)
                foreach (var p in host.Placement.StaticPlacementsAround(flat, radius))
                {
                    if (levelHost?.IsCleared(p.Key) == true) continue;
                    float y = p.WorldY ?? ground(p.Position.X, p.Position.Z) + p.Position.Y;
                    var at = new Vector3(p.Position.X, y, p.Position.Z);
                    float d = Vector2.Distance(new Vector2(at.X, at.Z), flat);
                    if (d > radius) continue;
                    var info = host.WorldInfo(p);
                    var b = new ControlBuilding("b:" + p.Key, info.Name, p.BuildingId, at, host.Placement.Footprint(p.BuildingId)?.Place(p.Position, p.Rotation));
                    controlBuildings[b.Id] = b;
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

    /// <summary>What a click at the pixel would hit: a character first (the nearest the pointer would take, one hidden behind a closed building excepted), else a building, else the ground or floor; JSON object.</summary>
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
            var ray = Ray(pixel);
            var terrain = ray is { } r ? MoveHit(r.Origin, r.Direction) : null;
            if (PickWhereAt(pixel, _ => true) is { } hit && ControlCharacter(hit.Id) is { } c)
            {
                w.WriteString("kind", "character");
                w.WriteString("id", CharacterKey(c.Id));
                w.WriteString("name", c.Name);
                WritePosition(w, "position", where(c));
                w.WriteNumber("distance", Math.Round(hit.T, 1));
            }
            else if (PickBuilding(pixel) is { } building)
            {
                string id = "b:" + building.Instance;
                controlBuildings[id] = new ControlBuilding(id, building.Name, building.Record, building.Position, building.Hull);
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

    /// <summary>The on-screen rectangle of an entity (JSON), or <c>offscreen</c>; null when the id names nothing. A character reports whether the nearest thing under its centre is something else.</summary>
    public string? ControlEntityScreenJson(string id)
    {
        Vector3[] corners;
        CharacterSnapshot? character = null;
        if (id.StartsWith("b:", StringComparison.OrdinalIgnoreCase))
        {
            if (ControlPosition(id) is not { } pos) return null;
            var hull = controlBuildings.TryGetValue(id, out var known) ? known.Hull : null;
            if (hull is null && session.CurrentSnapshot.Buildings.FirstOrDefault(s => "b:" + s.Id == id) is { } site && buildHost is { } host
                && (host.Placement.Footprint(site.Record) ?? host.Placement.Plot(site.Record)) is { } footprint)
                hull = footprint.Place(site.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Meitou.Game.Building.PlacementCheck.YawOf(site.Rotation)));
            corners = hull is { Length: > 0 } ? [.. hull.Select(p => new Vector3(p.X, pos.Y, p.Y)), .. hull.Select(p => new Vector3(p.X, pos.Y + 12, p.Y))] : [pos, pos + new Vector3(0, 12, 0)];
        }
        else
        {
            if (!TryParseCharacter(id, out var cid) || ControlCharacter(cid) is not { } c) return null;
            character = c;
            var at = where(c);
            const float Half = 3.5f;
            corners = [at + new Vector3(-Half, 0, -Half), at + new Vector3(Half, 0, -Half), at + new Vector3(-Half, 0, Half), at + new Vector3(Half, 0, Half),
                at + new Vector3(-Half, CharacterHeight, -Half), at + new Vector3(Half, CharacterHeight, -Half), at + new Vector3(-Half, CharacterHeight, Half), at + new Vector3(Half, CharacterHeight, Half)];
        }
        var pixels = corners.Select(Project).Where(p => p is not null).Select(p => p!.Value).ToList();
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
            bool visible = x1 >= 0 && x0 <= Width && y1 >= 0 && y0 <= Height;
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
                    var front = PickWhereAt(centre, _ => true);
                    bool occluded = front is null || front.Value.Id != character.Id;
                    w.WriteBoolean("occluded", occluded);
                    if (front is { } f && f.Id != character.Id) w.WriteString("front", CharacterKey(f.Id));
                    w.WriteBoolean("overHud", OverHud(centre));
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ---- selection and orders

    /// <summary>Selects characters like a click or box select would; false (with the reason) when an id names none.</summary>
    public bool ControlSelect(IReadOnlyList<CharacterId> ids, bool additive, out string message)
    {
        foreach (var id in ids)
            if (ControlCharacter(id) is null) { message = $"no character {CharacterKey(id)}"; return false; }
        (controlSelectIssued, controlSelectTick) = (ids, session.World.Tick);
        if (ids.Count == 0 && !additive) Say(new SelectCommand([], false) { Clear = true, Tick = session.World.Tick });   // the game's own empty select changes nothing
        else Select(ids, additive);
        message = ids.Count == 0 ? "selection cleared" : $"select issued for {ids.Count}";
        return true;
    }

    public IReadOnlyList<CharacterId> ControlSquad() => ActiveSquadMembers();

    /// <summary>Console Tab completion: character ids (c:slot.gen), the squad first, then the nearest others (at most <paramref name="limit"/> in all), from the live world; empty when none is loaded.</summary>
    public List<string> ControlCharacterKeys(int limit = 40)
    {
        var snap = session.CurrentSnapshot;
        var squad = ActiveSquadMembers();
        var lead = Selected().FirstOrDefault() ?? snap.Characters.FirstOrDefault(c => c.IsPlayer);
        var from = lead is null ? Vector3.Zero : where(lead);
        var keys = squad.Select(CharacterKey).ToList();
        keys.AddRange(snap.Characters.Where(c => !squad.Contains(c.Id)).OrderBy(c => Vector3.DistanceSquared(where(c), from)).Select(c => CharacterKey(c.Id)));
        return [.. keys.Take(limit)];
    }

    /// <summary>Console Tab completion: the item names the first selected (else lead) character carries, once each.</summary>
    public List<string> ControlCarriedItemNames()
    {
        var who = ControlOrderers();
        var id = who.Count > 0 ? who[0] : (Selected().FirstOrDefault() ?? session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer))?.Id ?? CharacterId.None;
        return ControlCharacter(id)?.ItemGrid is { } grid ? [.. grid.Items.Select(i => i.Name).Where(n => n.Length > 0).Distinct()] : [];
    }

    /// <summary>Console Tab completion for pickup: <c>nearest</c> and the ids of the loose items nearest to the lead (at most 20).</summary>
    public List<string> ControlGroundItemKeys()
    {
        var lead = Selected().FirstOrDefault() ?? session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer);
        var from = lead is null ? Vector3.Zero : where(lead);
        List<string> keys = ["nearest"];
        keys.AddRange(session.CurrentSnapshot.GroundItems.OrderBy(g => Vector3.DistanceSquared(g.Position, from)).Take(20).Select(g => g.Id.ToString(CultureInfo.InvariantCulture)));
        return keys;
    }

    /// <summary>The characters an order would go to: the world's selection, else what the last select command asked for.</summary>
    internal IReadOnlyList<CharacterId> ControlOrderers()
    {
        if (controlSelectTick == session.World.Tick) return controlSelectIssued;   // the world has not run a tick since the select: its selection is still the old one
        return session.World.Player.Selection.ToArray();
    }

    /// <summary>A move order to the selection at (x, z) on the terrain (the same <see cref="MoveOrder"/> a right click gives); false with the reason when nothing is selected.</summary>
    public bool ControlMove(float x, float z, bool queued, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        var target = new Vector3(x, ground(x, z), z);
        Move(target, queued);
        message = $"move issued to {who.Count} at {x:0.#} {z:0.#}{(queued ? " (queued)" : "")}";
        return true;
    }

    /// <summary>An attack order on a character (the quick right click's <see cref="Meitou.Simulation.Combat.AttackOrder"/>); false with the reason when it cannot be given.</summary>
    public bool ControlAttack(CharacterId target, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        if (ControlCharacter(target) is not { } t) { message = $"refused: no character {CharacterKey(target)}"; return false; }
        if (who.Contains(target)) { message = "refused: a character cannot attack itself"; return false; }
        if (t.IsPlayer) { message = $"refused: {t.Name} is one of the player's own"; return false; }
        if (t.Body is { Dead: true }) { message = $"refused: {t.Name} is dead"; return false; }
        Say(new Meitou.Simulation.Combat.AttackOrder(who, target) { Tick = session.World.Tick, Picked = true, Defensive = t.FightsPlayer });
        message = $"attack issued: {who.Count} on {t.Name} {CharacterKey(target)}{(t.HostileToPlayer ? "" : " (not hostile to the player)")}";
        return true;
    }

    /// <summary>Centres the strategy camera on an entity and follows it when it is a character.</summary>
    public void ControlFocusCharacter(CharacterId id) => CentreOn(id);
}

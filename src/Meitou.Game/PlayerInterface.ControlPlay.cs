using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Building;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Player;

namespace Meitou.Game;

/// <summary>
/// The agent control channel's play API (docs/procedures/agent-play-api.md): <c>observe</c> (one JSON for a decision) and the thin wrappers that give the selection the game's own order commands
/// (stop, follow, stance, pick up, loot, first aid, build, operate, research, craft). Every action goes out through <see cref="PlayerInterface.Say"/> as the same <see cref="SimCommand"/> the player's input gives;
/// each returns false with the reason when the game would refuse it. Characters are selected with <c>select</c> first; the orders go to the selection.
/// </summary>
sealed partial class PlayerInterface
{
    static double R1(double v) => Math.Round(v, 1);
    static double R2(double v) => Math.Round(v, 2);

    /// <summary>Gives the order made by <paramref name="make"/> to the selection; false (with the reason) when nobody is selected.</summary>
    bool ControlOrder(Func<IReadOnlyList<CharacterId>, SimCommand> make, string what, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        Say(make(who));
        message = $"{what} issued to {who.Count}: {string.Join(' ', who.Select(CharacterKey))}";
        return true;
    }

    public bool ControlStop(out string message) => ControlOrder(w => new StopCommand(w) { Tick = session.World.Tick }, "stop", out message);

    public bool ControlFollow(CharacterId target, bool guard, out string message)
    {
        if (ControlCharacter(target) is not { } t) { message = $"refused: no character {CharacterKey(target)}"; return false; }
        if (ControlOrderers().Contains(target)) { message = "refused: a character cannot follow itself"; return false; }
        if (t.Body is { Dead: true }) { message = $"refused: {t.Name} is dead"; return false; }
        return ControlOrder(w => new FollowOrder(w, target) { Guard = guard, Tick = session.World.Tick }, $"{(guard ? "bodyguard" : "follow")} {t.Name} {CharacterKey(target)}", out message);
    }

    static readonly (string Name, CharacterOrders Order)[] StanceNames =
        [("block", CharacterOrders.Block), ("hold", CharacterOrders.Hold), ("passive", CharacterOrders.Passive), ("ranged", CharacterOrders.Ranged), ("taunt", CharacterOrders.Taunt), ("sneak", CharacterOrders.Sneak), ("jobs", CharacterOrders.Jobs)];

    public static IReadOnlyList<string> StanceWords => [.. StanceNames.Select(s => s.Name)];

    /// <summary>The stance toggles on: <c>on</c> true/false sets, null flips (the orders panel's click).</summary>
    public bool ControlStance(string name, bool? on, out string message)
    {
        var found = StanceNames.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (found.Name is null) { message = $"refused: unknown stance '{name}' (block hold passive ranged taunt sneak jobs)"; return false; }
        return ControlOrder(w => new OrdersCommand(w, found.Order, on) { Tick = session.World.Tick }, $"stance {found.Name} {(on is null ? "toggle" : on.Value ? "on" : "off")}", out message);
    }

    /// <summary>The ground item to pick up: an id from observe / ground-items, or the nearest to the lead character.</summary>
    public bool ControlPickup(string which, out string message)
    {
        var items = session.CurrentSnapshot.GroundItems;
        var lead = Selected().FirstOrDefault() ?? session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer);
        if (lead is null) { message = "refused: no player character"; return false; }
        var from = where(lead);
        Meitou.Simulation.Items.GroundItemSnapshot? pick = null;
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
        if (!ControlOrder(w => new Meitou.Simulation.Items.PickUpItemOrder(w, itemId) { Tick = session.World.Tick }, $"pickup {pick.Value.Name} #{itemId}", out message)) return false;
        return true;
    }

    /// <summary>Loot a downed or dead character (everything that fits goes to the looter).</summary>
    public bool ControlLoot(CharacterId target, out string message)
    {
        if (ControlCharacter(target) is not { } t) { message = $"refused: no character {CharacterKey(target)}"; return false; }
        if (t.IsPlayer) { message = $"refused: {t.Name} is one of the player's own"; return false; }
        if (!(t.Body is { Dead: true } or { Unconscious: true }) && !t.CanOpenInventory && !t.Down) { message = $"refused: {t.Name} is standing (loot works on a dead, knocked-out or carried character)"; return false; }
        return ControlOrder(w => new Meitou.Simulation.Carrying.LootOrder(w, target) { Tick = session.World.Tick }, $"loot {t.Name} {CharacterKey(target)}", out message);
    }

    public bool ControlFirstAid(CharacterId patient, out string message)
    {
        if (ControlCharacter(patient) is not { } t) { message = $"refused: no character {CharacterKey(patient)}"; return false; }
        if (t.Body is { Dead: true }) { message = $"refused: {t.Name} is dead"; return false; }
        string note = t.Body is { NeedsAid: false, NeedsOrganicAid: false } ? " (note: the patient reports nothing to bandage)" : "";
        if (!ControlOrder(w => new Meitou.Simulation.FirstAid.TreatOrder(w, patient) { Tick = session.World.Tick }, $"firstaid {t.Name} {CharacterKey(patient)}", out message)) return false;
        message += note + " (needs a first aid kit in the medic's inventory)";
        return true;
    }

    // ---- construction

    BuildingBuildData? ControlBuildingData(string query, out string error)
    {
        error = "";
        if (buildHost is not { } host) { error = "no build services (no world loaded)"; return null; }
        var all = host.Menu.Catalog;
        var byId = all.FirstOrDefault(b => b.Id.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        var named = all.Where(b => b.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 0) named = [.. all.Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];
        if (named.Count == 1) return named[0];
        var unlocked = named.Where(b => Unlocked().Contains(b.Id)).ToList();
        if (unlocked.Count == 1) return unlocked[0];
        error = named.Count == 0 ? $"no building '{query}' (build-list shows the types)" : $"'{query}' matches {named.Count} buildings; use the record id: {string.Join(", ", named.Take(6).Select(b => $"{b.Id} ({b.Name})"))}";
        return null;
    }

    /// <summary>The buildings the player may place now (or all with <paramref name="all"/>), with their material costs, as a JSON object.</summary>
    public string ControlBuildListJson(string text, bool all, int limit)
    {
        if (buildHost is not { } host) return "{\"types\":[],\"total\":0}";
        var unlocked = Unlocked();
        var list = host.Menu.Catalog.Where(b => (all || unlocked.Contains(b.Id)) && !b.IsNode
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

    /// <summary>What the selection holds of an item record (null when a selected character's grid is not published yet).</summary>
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
    /// Build mode's confirm without the pointer: the ghost is judged by the same <see cref="PlacementCheck"/> the build ghost uses, then <see cref="PlaceBuildings"/> goes out (which also sends the selection to build it).
    /// Refused with the reason when the type is unknown or locked, the spot fails the placement rules, or the selection does not hold the materials (unless <paramref name="anyway"/>: a base store may supply them).
    /// <paramref name="json"/> is the structured reply.
    /// </summary>
    public bool ControlBuild(string type, float x, float z, float yawDegrees, bool anyway, out string message, out string json)
    {
        json = "";
        if (buildHost is not { } host) { message = "refused: no build services (no world loaded)"; return false; }
        if (ControlBuildingData(type, out string error) is not { } data) { message = "refused: " + error; return false; }
        if (!Unlocked().Contains(data.Id)) { message = $"refused: {data.Name} ({data.Id}) is not unlocked by research (build-list shows what is)"; return false; }
        float yaw = yawDegrees * MathF.PI / 180;
        var footprint = host.Placement.Footprint(data.Id);
        var box = footprint?.Bounds ?? (new Vector2(-4), new Vector2(4));
        var at = new Vector2(x, z);
        var normal = PlacementCheck.GroundNormal(host.Placement.Ground, at, yaw, box);
        float angle = MathF.Acos(Math.Clamp(normal.Y, -1, 1)) * (data.MatchSlope ? 1 : DefaultTilt);
        if (data.MaxSlope > 0) angle = MathF.Min(angle, data.MaxSlope * MathF.PI / 180);
        var axis = Vector3.Cross(Vector3.UnitY, normal);
        var tilt = axis.LengthSquared() < 1e-10f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
        var rotation = tilt * Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        var position = new Vector3(x, ground(x, z), z);
        var snapshot = session.CurrentSnapshot;
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
        Say(new PlaceBuildings([new PlannedBuilding(data.Id, position, rotation)]) { Tick = session.World.Tick });
        message = $"placed {data.Name} ({data.Id}) at {position.X:0.#} {position.Y:0.#} {position.Z:0.#} yaw {yawDegrees:0.#}; {who.Count} selected character(s) sent to build it{(who.Count == 0 ? " (nobody is selected: select builders and then 'work <site id>': observe.buildings.own lists the site)" : "")}{(unknown ? " (materials not checked: select the builders a tick earlier)" : "")}{(missing.Count > 0 ? " (materials missing, --anyway)" : "")}";
        return true;
    }

    // ---- production

    public bool ControlOperate(string site, out string message)
    {
        string id = site.StartsWith("b:", StringComparison.OrdinalIgnoreCase) ? site[2..] : site;
        var snap = session.CurrentSnapshot;
        var machine = snap.Machines.FirstOrDefault(m => m.Site == id);
        if (machine is null) { message = $"refused: no machine '{site}' (observe.machines lists them: a complete building of yours with a machine)"; return false; }
        if (!machine.Operable && !machine.IsAutomatic) { message = $"refused: {machine.Record} cannot be operated by hand"; return false; }
        return ControlOrder(w => new Meitou.Simulation.Production.OperateOrder(w, id) { Tick = session.World.Tick }, $"operate {machine.Record} {site}", out message);
    }

    public string ControlResearchJson()
    {
        var r = session.CurrentSnapshot.Research;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            WriteResearch(w, r, 40);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    void WriteResearch(Utf8JsonWriter w, Meitou.Simulation.Production.ResearchSnapshot r, int limit)
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
        var r = session.CurrentSnapshot.Research;
        var hits = r.Available.Where(o => o.Id.Equals(query, StringComparison.OrdinalIgnoreCase) || o.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) hits = [.. r.Available.Where(o => o.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];
        if (hits.Count != 1) { message = hits.Count == 0 ? $"refused: no queueable research '{query}' (research list shows them)" : $"refused: '{query}' matches {hits.Count}: {string.Join(", ", hits.Take(6).Select(h => h.Name))}"; return false; }
        var o = hits[0];
        if (o.BenchTooLow) { message = $"refused: {o.Name} needs a higher research bench level"; return false; }
        if (r.Queue.Any(q => q.Id == o.Id)) { message = $"refused: {o.Name} is already queued"; return false; }
        Say(new Meitou.Simulation.Production.QueueResearch(o.Id) { Tick = session.World.Tick });
        message = $"research queued: {o.Name} ({o.Id}){(o.Paid || o.Affordable ? "" : "; its cost is not held by the benches, so it may not start")}";
        return true;
    }

    public bool ControlCraft(string site, string? recipe, int count, out string message, out string json)
    {
        json = "";
        string id = site.StartsWith("b:", StringComparison.OrdinalIgnoreCase) ? site[2..] : site;
        if (!session.CurrentSnapshot.Crafts.TryGetValue(id, out var options)) { message = $"refused: '{site}' is not one of your crafting benches"; return false; }
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
        Say(new Meitou.Simulation.Production.QueueCraft(id, hits[0].Key, count) { Tick = session.World.Tick });
        message = $"craft queued: {count} x {hits[0].Name} at {site} (a smith must work the bench: operate {site})";
        return true;
    }

    // ---- observe

    static string StanceText(CharacterOrders o) => string.Join(',', StanceNames.Where(s => (o & s.Order) != 0).Select(s => s.Name));

    void WriteXZ(Utf8JsonWriter w, string name, Vector3 p)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(R1(p.X));
        w.WriteNumberValue(R1(p.Z));
        w.WriteEndArray();
    }

    /// <summary>One squad member for observe: everything the snapshot holds, which is more for a selected character (limbs, inventory, skills) than for the others.</summary>
    void WriteSquadMember(Utf8JsonWriter w, CharacterSnapshot c, bool full)
    {
        var at = where(c);
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
                if (p.Limb != Meitou.Simulation.Bodies.LimbState.Original) w.WriteString("limb", p.Limb.ToString());
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
        var at = where(c);
        w.WriteStartObject();
        w.WriteString("id", CharacterKey(c.Id));
        w.WriteString("name", c.Name);
        w.WriteString("faction", FactionName?.Invoke(c.Faction));
        w.WriteNumber("dist", R1(Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(from.X, from.Z))));
        WriteXZ(w, "pos", at);
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

    /// <summary>The values of the last <c>observe --brief</c> (raw JSON per field), for <c>--changed</c>.</summary>
    Dictionary<string, string>? lastBrief;

    /// <summary>
    /// The terse state for polling (<c>observe --brief</c>, about 200 bytes): tick, clock, paused, speed, the lead's id, name, position [x,z], state, hp fraction, task, order, the squad as "alive/total",
    /// the nearest hostile's distance (null: none) and money. With <paramref name="changedOnly"/> only the fields that differ from the last brief call (always <c>tick</c>); the first call gives all.
    /// </summary>
    public string ControlBriefJson(bool changedOnly)
    {
        var snap = session.CurrentSnapshot;
        var world = session.World;
        var squad = snap.Characters.Where(c => c.IsPlayer).ToList();
        var lead = squad.FirstOrDefault(c => c.Selected) ?? squad.FirstOrDefault();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string Str(string? v) => v is null ? "null" : JsonSerializer.Serialize(v);
        var f = new List<(string Key, string Value)>();
        double hour = session.Clock.HourOfDay;
        f.Add(("tick", world.Tick.ToString(inv)));
        f.Add(("clock", Str($"{(int)hour:00}:{(int)((hour - Math.Floor(hour)) * 60):00}")));
        f.Add(("paused", session.Simulation.IsPaused ? "true" : "false"));
        f.Add(("speed", session.Simulation.Speed.ToString(inv)));
        if (lead is null) { f.Add(("lead", "null")); }
        else
        {
            var at = where(lead);
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
                var p = where(c);
                double d = Vector2.Distance(new Vector2(p.X, p.Z), flat);
                if (threat is null || d < threat) threat = d;
            }
            f.Add(("threat", threat is { } t ? Math.Round(t, 1).ToString(inv) : "null"));
            f.Add(("combat", Str(ControlCombatLine(lead))));
        }
        f.Add(("squad", Str($"{squad.Count(c => c.Body is { Dead: false, Unconscious: false })}/{squad.Count}")));
        f.Add(("money", world.Player.Money.ToString(inv)));
        var previous = lastBrief;
        lastBrief = f.ToDictionary(x => x.Key, x => x.Value);
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
    /// The one JSON for a decision (docs/procedures/agent-play-api.md): time, speed, the squad, what is near (threats, allies, neutrals, animals, traders, bodies), loose items, buildings, containers, machines, research,
    /// resources (null: no cheap listing exists) and a score object. Lists are cut at a fixed length with the full count beside them.
    /// </summary>
    public string ControlObserveJson(float radius, bool full)
    {
        var snap = session.CurrentSnapshot;
        var world = session.World;
        var squad = snap.Characters.Where(c => c.IsPlayer).ToList();
        var lead = squad.FirstOrDefault(c => c.Selected) ?? squad.FirstOrDefault();
        var from = lead is null ? session.Camera.Current.Target : where(lead);
        var flat = new Vector2(from.X, from.Z);
        float D(Vector3 p) => Vector2.Distance(new Vector2(p.X, p.Z), flat);
        int listCap = full ? 40 : 12;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("tick", world.Tick);
            w.WriteStartObject("time");
            double hour = session.Clock.HourOfDay;
            w.WriteNumber("day", (int)session.Clock.Day);
            w.WriteNumber("hour", R2(hour));
            w.WriteString("clock", $"{(int)hour:00}:{(int)((hour - Math.Floor(hour)) * 60):00}");
            w.WriteBoolean("daytime", world.Daytime);
            w.WriteNumber("gameHours", R2(world.GameHours));
            w.WriteEndObject();
            w.WriteBoolean("paused", session.Simulation.IsPaused);
            w.WriteNumber("speed", session.Simulation.Speed);
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
                float d = D(where(c));
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
                WriteXZ(w, "pos", g.Position);
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
                w.WriteString("name", SiteName(b));
                w.WriteString("record", b.Record);
                WriteXZ(w, "pos", b.Position);
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
                        w.WriteString("item", buildHost?.ItemName(m.Item) ?? m.Item);
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
            if (buildHost is { } host)
                foreach (var p in host.Placement.StaticPlacementsAround(flat, radius))
                {
                    if (levelHost?.IsCleared(p.Key) == true) continue;
                    float y = p.WorldY ?? ground(p.Position.X, p.Position.Z) + p.Position.Y;
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
                WriteXZ(w, "pos", n.At);
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
                WriteXZ(w, "pos", c.Position);
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
                w.WriteString("name", bs is null ? m.Record : SiteName(bs));
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
}

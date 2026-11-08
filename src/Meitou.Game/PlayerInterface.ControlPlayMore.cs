using System.Numerics;
using System.Text;
using System.Text.Json;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;

namespace Meitou.Game;

/// <summary>
/// More of the agent control channel's play API (docs/procedures/agent-play-api.md): work on a site, prospect (and the last prospecting as observe.resources), equip, carry, trade, and the kill counter.
/// Same rules as PlayerInterface.ControlPlay.cs: the game's own commands, to the selection, refused with a reason.
/// </summary>
sealed partial class PlayerInterface
{
    // ---- work on a site

    /// <summary>A <see cref="Meitou.Simulation.Construction.BuildOrder"/> on a site of yours (build it, or repair it when it is complete and damaged) for the selection.</summary>
    public bool ControlWork(string site, out string message)
    {
        string id = site.StartsWith("b:", StringComparison.OrdinalIgnoreCase) ? site[2..] : site;
        var found = session.CurrentSnapshot.Buildings.FirstOrDefault(b => b.Id == id && b.LevelPlacement is null);
        if (found is null) { message = $"refused: no site '{site}' of yours (observe.buildings.own lists them)"; return false; }
        if (found.Owner != session.World.Player.Faction) { message = $"refused: {site} is not yours"; return false; }
        if (found.Dismantling) { message = $"refused: {site} is being dismantled"; return false; }
        if (found.Complete && !found.NeedsWork) { message = $"refused: {SiteName(found)} is complete and undamaged, nothing to do"; return false; }
        return ControlOrder(w => new Meitou.Simulation.Construction.BuildOrder(w, id) { Tick = session.World.Tick }, $"{(found.Complete ? "repair" : "build")} {SiteName(found)} b:{id}", out message);
    }

    // ---- prospecting

    /// <summary>ProspectOrder for the first selected character (the HUD's Prospect button); the result is in observe.resources after about a game second.</summary>
    public bool ControlProspect(out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        if (ControlCharacter(who[0]) is { } c && c.Task == CharacterTask.Prospect) { message = $"refused: {c.Name} is already prospecting"; return false; }
        Say(new ProspectOrder(who[0]) { Tick = session.World.Tick });
        message = $"prospect issued to {CharacterKey(who[0])}; takes about a game second, then observe.resources holds the result (step 60)";
        return true;
    }

    /// <summary>The newest prospecting of this world as JSON (spot, zone, coverages, the iron and copper rocks nearest the spot with their yields); null when none was made.</summary>
    string? ControlResourcesJson(Vector2 near)
    {
        if (session.World.Prospected is not { } r) return null;
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

    /// <summary>The right click on an item of the first selected character (<see cref="QuickMoveItem"/>): the item goes to the slot that takes it, a worn one back to the grid.</summary>
    public bool ControlEquip(string what, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        if (ControlCharacter(who[0])?.ItemGrid is not { } grid) { message = "refused: the inventory is not published yet (step a tick)"; return false; }
        var hits = grid.Items.Where(i => i.Name.Equals(what, StringComparison.OrdinalIgnoreCase) || i.Record.Equals(what, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) hits = [.. grid.Items.Where(i => i.Name.Contains(what, StringComparison.OrdinalIgnoreCase))];
        if (hits.Count == 0) { message = $"refused: {CharacterKey(who[0])} holds no item '{what}' (observe.squad[].inventory)"; return false; }
        var item = hits[0];
        Say(new QuickMoveItem(who[0], new InventoryItemToken(item.Index, item.Record, item.Section, item.X, item.Y, item.Quantity, item.Quality), false, false) { Tick = session.World.Tick });
        message = $"equip (right click) {item.Name} [{item.Section}] of {CharacterKey(who[0])}{(hits.Count > 1 ? $"; {hits.Count - 1} more items match" : "")}";
        return true;
    }

    /// <summary>Takes a worn item off: <paramref name="what"/> is a slot (head, shirt, armour, legs, boots, back, hip, belt) or an item name; the right click of a worn item sends it to the grids (the reply cannot know if they had room: observe.squad[].inventory shows).</summary>
    public bool ControlUnequip(string what, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        if (ControlCharacter(who[0])?.ItemGrid is not { } grid) { message = "refused: the inventory is not published yet (step a tick)"; return false; }
        var worn = grid.Items.Where(i => Meitou.Simulation.Items.InventoryCommands.IsEquipmentSection(i.Section) && i.Section != "backpack_attach").ToList();
        var item = worn.FirstOrDefault(i => i.Section.Equals(what, StringComparison.OrdinalIgnoreCase)) ?? worn.FirstOrDefault(i => i.Name.Equals(what, StringComparison.OrdinalIgnoreCase)) ?? worn.FirstOrDefault(i => i.Name.Contains(what, StringComparison.OrdinalIgnoreCase));
        if (item is null) { message = $"refused: {CharacterKey(who[0])} wears nothing matching '{what}' (worn slots: {string.Join(", ", worn.Select(i => i.Section))})"; return false; }
        Say(new QuickMoveItem(who[0], new InventoryItemToken(item.Index, item.Record, item.Section, item.X, item.Y, item.Quantity, item.Quality), false, false) { Tick = session.World.Tick });
        message = $"unequip (right click) {item.Name} [{item.Section}] of {CharacterKey(who[0])}";
        return true;
    }

    /// <summary>PickUpOrder: the selection lifts a downed character (carry them, or kidnap a stranger).</summary>
    public bool ControlCarry(CharacterId target, out string message)
    {
        if (ControlCharacter(target) is not { } t) { message = $"refused: no character {CharacterKey(target)}"; return false; }
        if (ControlOrderers().Contains(target)) { message = "refused: a character cannot carry itself"; return false; }
        return ControlOrder(w => new Meitou.Simulation.Carrying.PickUpOrder(w, target) { Tick = session.World.Tick }, $"carry {t.Name} {CharacterKey(target)}", out message);
    }

    public bool ControlPutDown(out string message) => ControlOrder(w => new Meitou.Simulation.Carrying.PutDownOrder(w) { Tick = session.World.Tick }, "putdown", out message);

    // ---- trade

    /// <summary>OpenTrade: the first selected character walks to the trader; the window opens in reach (then <c>trade</c> shows it).</summary>
    public bool ControlTradeOpen(CharacterId trader, out string message)
    {
        var who = ControlOrderers();
        if (who.Count == 0) { message = "refused: no character selected (use select first)"; return false; }
        if (ControlCharacter(trader) is not { } t) { message = $"refused: no character {CharacterKey(trader)}"; return false; }
        if (!t.IsTrader) { message = $"refused: {t.Name} is not a trader (observe.nearby.traders)"; return false; }
        Say(new OpenTrade(who[0], trader) { Tick = session.World.Tick });
        message = $"trade open issued: {CharacterKey(who[0])} walks to {t.Name}; step, then 'trade' shows the stock and prices";
        return true;
    }

    public bool ControlTradeClose(out string message)
    {
        if (session.CurrentSnapshot.Trade is null) { message = "refused: no trade is open"; return false; }
        Say(new CloseTrade { Tick = session.World.Tick });
        message = "trade closed";
        return true;
    }

    /// <summary>The open trade (stock the shop sells, goods of the customer it would buy, both with prices) as JSON; false when none is open.</summary>
    public bool ControlTradeJson(out string json)
    {
        json = "";
        if (session.CurrentSnapshot.Trade is not { } t) return false;
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

    /// <summary>Buys (<paramref name="buy"/>) or sells <paramref name="count"/> units of the item named <paramref name="what"/> in the open trade.</summary>
    public bool ControlTradeDeal(bool buy, string what, int count, out string message)
    {
        if (session.CurrentSnapshot.Trade is not { } t) { message = "refused: no trade is open (trade open <trader>, step, then trade)"; return false; }
        var list = buy ? t.Stock : t.Goods;
        var hits = list.Where(i => i.Name.Equals(what, StringComparison.OrdinalIgnoreCase) || i.Record.Equals(what, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) hits = [.. list.Where(i => i.Name.Contains(what, StringComparison.OrdinalIgnoreCase))];
        if (hits.Count == 0) { message = $"refused: the {(buy ? "stock" : "goods")} hold no '{what}' ('trade' lists them)"; return false; }
        var item = hits[0];
        int n = Math.Clamp(count, 1, item.Quantity);
        long cost = item.UnitPrice * n;
        if (buy && cost > t.PlayerMoney) { message = $"refused: {n} x {item.Name} costs {cost}, you have {t.PlayerMoney}"; return false; }
        if (!buy && cost > t.ShopMoney) { message = $"refused: the shop pays at most {t.ShopMoney} (this is {cost})"; return false; }
        if (buy) Say(new BuyItem(item.Token, n) { Tick = session.World.Tick, Equip = false, Into = t.Customer });
        else Say(new SellItem(item.Token, n) { Tick = session.World.Tick, From = t.Customer });
        message = $"{(buy ? "buy" : "sell")} {n} x {item.Name} for about {cost} issued";
        return true;
    }

    /// <summary>One character as observe.nearby lists it (id, name, faction, dist from the lead, pos, state), as JSON; null when it is not in the snapshot.</summary>
    public string? ControlCharacterJson(CharacterId id)
    {
        if (ControlCharacter(id) is not { } c) return null;
        var lead = Selected().FirstOrDefault() ?? session.CurrentSnapshot.Characters.FirstOrDefault(p => p.IsPlayer);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream)) WriteNearby(w, c, lead is null ? session.Camera.Current.Target : where(lead), true);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Character ids in the snapshot with how far each is from (x, z), and whether it is down (dead, knocked out or carried): the spawn command's lookup.</summary>
    public IEnumerable<(CharacterId Id, float Dist, bool Down, bool Player)> ControlCharactersNear(float x, float z) =>
        session.CurrentSnapshot.Characters.Select(c => (c.Id, Vector2.Distance(new Vector2(where(c).X, where(c).Z), new Vector2(x, z)), c.Body is { Dead: true } or { Unconscious: true }, c.IsPlayer));

    // ---- kill counter

    object? killsWorld;
    readonly HashSet<CharacterId> hostileSeen = [], hostileDead = [];

    /// <summary>Notes the hostile (non-player, non-animal, hostile to the player or fighting it) characters that are dead; a set of ids, reset when another world is loaded.</summary>
    public int ControlKills()
    {
        if (!ReferenceEquals(killsWorld, session.World)) { killsWorld = session.World; hostileSeen.Clear(); hostileDead.Clear(); }
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (c.IsPlayer || c.Animal) continue;
            bool hostile = c.HostileToPlayer || c.FightsPlayer;
            if (c.Body is { Dead: true }) { if (hostile || hostileSeen.Contains(c.Id)) hostileDead.Add(c.Id); }
            else if (hostile) hostileSeen.Add(c.Id);
        }
        return hostileDead.Count;
    }
}

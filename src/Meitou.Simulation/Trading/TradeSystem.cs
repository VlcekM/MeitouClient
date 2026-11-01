using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Trading;

/// <summary>The player opens a trade between one of their characters and a trader: the customer walks up to the trader and the trade window opens in reach.</summary>
public sealed record OpenTrade(CharacterId Customer, CharacterId Trader) : OrderCommand;
/// <summary>Closes the open trade.</summary>
public sealed record CloseTrade : SimCommand;
/// <summary>
/// Buys <see cref="Quantity"/> units of the stock item <see cref="Item"/> (its index in the shop's stock) into the customer's inventory: at the cell <see cref="X"/>, <see cref="Y"/> of
/// <see cref="Section"/> (<c>main</c> or <c>backpack_content</c>, the cell the item was dropped on) when that is free, else wherever it fits.
/// A backpack may also target the single <c>backpack_attach</c> equipment slot.
/// </summary>
public sealed record BuyItem(InventoryItemToken Item, int Quantity) : SimCommand
{
    public string? Section { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    /// <summary>The window's right click (no cell chosen): one unit goes into the equipment slot that takes it when that is free, before the grids (<see cref="InventoryCommands.TryEquip"/>).</summary>
    public bool Equip { get; init; }
}

/// <summary>Sells <see cref="Quantity"/> units of the customer's item <see cref="Item"/> (its index in the customer's inventory) to the shop, at the stock cell dropped on when free.</summary>
public sealed record SellItem(InventoryItemToken Item, int Quantity) : SimCommand
{
    public int? X { get; init; }
    public int Y { get; init; }
}

/// <summary>One item of a trade window, with what it costs (stock) or fetches (the customer's goods) as a whole stack.</summary>
public sealed record TradeItemView(int Index, string Record, string Name, string Section, int X, int Y, int Width, int Height, int Quantity, float Quality, long Price, long UnitPrice, bool Stolen)
{
    /// <summary>The current transfer reverses a purchase or sale from this trade window at its original price.</summary>
    public bool Buyback { get; init; }
    /// <summary>A bag (a container with a contents grid): it trades whole, and may go into the backpack slot.</summary>
    public bool Container { get; init; }
    /// <summary>The item's material record ("" none), which picks its icon.</summary>
    public string Material { get; init; } = "";
    /// <summary>The COLOR_DATA it is dyed with (<see cref="Items.ItemInstance.ColourId"/>), which picks its icon.</summary>
    public string Colour { get; init; } = "";
    /// <summary>How full the item is (0 to 1); null: no charge bar.</summary>
    public float? Charge { get; init; }
    /// <summary>The item's manufacturer record (<c>company sid</c>; "" none), model level and charges, and the trader factor of its price (<c>FUN_14079e6d0</c>, 1: none).</summary>
    public string Company { get; init; } = "";
    public int Level { get; init; }
    public float Charges { get; init; }
    public float MaxCharges { get; init; }
    public float PriceFactor { get; init; } = 1;
    /// <summary>The trader's town market factor of the item (<see cref="Market.MarketFactor"/>, 1: none); with <see cref="PriceFactor"/> it makes a trade item's <c>Price mark-up</c>.</summary>
    public float MarketFactor { get; init; } = 1;
    /// <summary>The faction a stolen item was stolen from, -1 when it is not stolen.</summary>
    public int StolenFrom { get; init; } = -1;
    /// <summary>The item's <c>unique</c> flag (<see cref="Items.ItemInstance.Unique"/>): the tooltip's [Unique] row.</summary>
    public bool Unique { get; init; }
    /// <summary>The tooltip's two prices of one unit (<c>FUN_14079e7a0</c> calls slot 81, <b>Verified</b>): what the shop asks for it (Value) and what it pays (Sell value).</summary>
    public long Value { get; init; }
    public long SellValue { get; init; }
    /// <summary>The stack's weight in kg.</summary>
    public float Weight { get; init; }
    /// <summary>A stolen good of the customer's that the shop would roll on (<c>FUN_140792830</c>, whoever it was stolen from): the chance (0 to 1) a sale goes through unnoticed, <see cref="TradeSystem"/>'s fence odds, the hover box's number; null otherwise.</summary>
    public float? FenceChance { get; init; }
    /// <summary>
    /// A stolen good of the customer's that a shop which is no fence would buy only with a risk: the chance (0 to 1, below 1) a sale goes through unnoticed, whoever it was stolen from
    /// (<c>FUN_140715290</c> asks "Fence it anyway?" with the opposite in per cent); null when the sale is safe or the item is not stolen.
    /// </summary>
    public float? FenceOdds { get; init; }
    /// <summary>A good of the customer's that the culture of the trader's faction lists as illegal (the smuggling prompt, <c>FUN_140712920</c>).</summary>
    public bool Illegal { get; init; }
    public InventoryItemToken Token => new(Index, Record, Section, X, Y, Quantity, Quality);
}

/// <summary>Arranges the open trade's stock (the trader window's ARRANGE button): the stock's grid as <see cref="InventoryCommands.Arrange"/>.</summary>
public sealed record ArrangeStock : SimCommand;

/// <summary>The open trade as the UI shows it (published with the snapshot).</summary>
public sealed record TradeView(CharacterId Customer, CharacterId Trader, string TraderName, long ShopMoney, long PlayerMoney, int StockWidth, int StockHeight,
    IReadOnlyList<TradeItemView> Stock, IReadOnlyList<TradeItemView> Goods, bool BuysStolen, bool BuysIllegal = false)
{
    /// <summary>The held item (<see cref="PlayerState.Held"/>) priced as stock (lifted from the stock) or as the customer's goods (lifted from the customer); null for none or another's.</summary>
    public TradeItemView? Held { get; init; }
    /// <summary>The stock cells outside every container of a building shop (<see cref="Shop.Unused"/>), which hold nothing; empty for a squad shop.</summary>
    public IReadOnlyList<GridRegion> Unused { get; init; } = [];
}

/// <summary>A system that has an open trade to publish (the trade system).</summary>
public interface ITradeSource
{
    TradeView? PublishTrade();
}

public sealed class TradeSettings
{
    /// <summary>How close the customer must stand to the trader (the original's "Out of trading range." distance is <b>Unknown</b>; engine choice).</summary>
    public float Range { get; init; } = 30;
    /// <summary>Ticks between looks at the squads for new shops and due restocks (engine choice: half a game second).</summary>
    public int CheckEveryTicks { get; init; } = 15;
    /// <summary>Game seconds the customer has to reach the trader before the opening is dropped (engine choice).</summary>
    public float ApproachSeconds { get; init; } = 60;
}

/// <summary>
/// The economy in play (docs/simulation.md "Trade", docs/game/economy.md sections 8 and 9):
/// <list type="bullet">
/// <item><b>Shops</b>: a squad made from a template with <c>is trader</c> and vendor lists gets a <see cref="Shop"/> when it appears (the game's first fill at squad creation), filled by
/// <see cref="ShopFiller"/>, with the purse in <see cref="Squad.Money"/>; it restocks every <c>vendors refresh time</c> game hours (9.3). Squads of the player's faction never get one.
/// A housed one gets a building shop when its home has stock containers (<see cref="Shop.ForBuilding"/>).</item>
/// <item><b>Trading</b> (the trade window <c>FUN_140715560</c>, <b>Observed</b>): a purchase is priced by <see cref="Market.StackPrice"/> without the sell flag, a sale with it; when the payer
/// cannot afford the stack the number of units it can afford moves instead, none: "You can't afford that." (3) or "The shopkeeper can't afford that." (4). What does not fit: "No room for
/// that item." (2). The cats move between the player's purse and the shop's.</item>
/// <item><b>Fencing</b> (8.4, <b>Observed</b>): a stolen item sold to a shop that is not a knowing fence (<c>buys stolen</c>; a knowing fence only for goods stolen from its own squad) is caught unless a roll is below
/// lerp(thievery / 100, 0.4, 1.25), halved when the item was stolen from the shop's own faction and again from its own squad (never caught when it was stolen from a bandit or adventurer faction, or from one the shop's faction is
/// not allied to whose home town is not the trader's, <see cref="FenceOdds"/>); the seller trains thievery either way; caught: "Got caught selling stolen item." (9), the sale is refused and the seller gets a
/// pending Fencing crime against the shop's faction, booked at once with the shopkeeper as the witness. The prompt that shows the chance is the UI's. An illegal item (the culture of the town's faction) is refused by a trader that does not buy illegal goods:
/// "Attempted smuggling!" (<see cref="Smuggling"/>); the rest of the smuggling rules (the trader's answer for a smuggler, read by no caller seen) are not built.</item>
/// </list>
/// The shops' stock and restock times are in the state hash (through the squads). The open trade is not, but the buyback receipts it leaves on items are, and they are not saved:
/// a save made mid-trade reloads with a different hash (docs/simulation.md, trading).
/// </summary>
public sealed class TradeSystem : ITickSystem, ITradeSource
{
    readonly PopulationData data;
    readonly CharacterAssembly assembly;
    readonly Dictionary<string, SquadTemplate?> templates = new(StringComparer.Ordinal), homeTemplates = new(StringComparer.Ordinal);
    Market? market;
    CharacterId customer = CharacterId.None, trader = CharacterId.None;
    bool open, traderWandered;
    TradeSession session = new();
    long approachUntil;
    TradeView? view;

    public TradeSystem(PopulationData data, TradeSettings? settings = null)
    {
        this.data = data;
        assembly = new(data);
        Settings = settings ?? new TradeSettings();
    }

    public TradeSettings Settings { get; }
    /// <summary>The crime system a caught fence is reported to (null: no crimes).</summary>
    public Crime.CrimeSystem? Crimes { get; set; }
    /// <summary>The prices of this world (made at the first tick, from the world's seed).</summary>
    public Market Market(World world) => market ??= new Market(data, world.Seed);

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case OpenTrade o:
                    Close(world);
                    customer = o.Customer;
                    trader = o.Trader;
                    approachUntil = world.Tick + (long)(Settings.ApproachSeconds / world.TickSeconds);
                    if (!Resolve(world, out int c, out int t, out _))
                    {
                        // A right click on an enemy is still an attack (the UI sends the trade for any trader; only the world knows who is hostile).
                        if (world.Characters.TryResolveNext(trader, out int who) && world.Characters.Cold(who) is { } tc && AggroSystem.IsEnemy(data.Relations, world.Player.Faction, tc.Faction))
                            world.Defer(new Combat.AttackOrder([customer], trader) { Tick = world.Tick + 1 });
                        Close(world);
                        break;
                    }
                    Hold(world, t);
                    if (!InReach(world, c, t)) world.Defer(new MoveOrder([customer], world.Characters.Next[t].Position) { Tick = world.Tick + 1 });
                    break;
                case CloseTrade:
                    Close(world);
                    break;
                case BuyItem buy when open:
                    FollowSelection(world);
                    Buy(world, buy);
                    break;
                case SellItem sell when open:
                    FollowSelection(world);
                    Sell(world, sell);
                    break;
                case ArrangeStock when open:
                    if (Resolve(world, out _, out _, out var arranged)) InventoryCommands.Arrange(arranged!.Stock);
                    break;
            }
        }
    }

    public void SlowWorld(World world)
    {
        if (world.Tick % Settings.CheckEveryTicks == 0) { Stock(world); PayWages(world); }
        if (open) FollowSelection(world);
        if (!customer.IsNone)
        {
            if (!Resolve(world, out int c, out int t, out _)) Close(world);
            else if (InReach(world, c, t))
            {
                open = true;
                Hold(world, t);
            }
            else if (open || world.Tick >= approachUntil) Close(world);   // walked away, or never arrived
            else Hold(world, t);
        }
        RefreshView(world);
    }


    // CHARACTER `wages` (Character::giveBirth and Character::dailyUpdate, Verified): a character not of the player's faction with wages of 1 or more sets (not adds to) its squad's money
    // to them when it is made and every day; with several waged members the last one wins. Engine choices: the day pass runs when the game day changes, members in squad order; "when
    // made" is the first pass that sees the squad, except the squads already there at this system's first pass and a squad of characters back from a save or from out of the world
    // (Squad.Restored: giveBirth pays only a character made without saved state, so a loaded game keeps its saved purses until the next day).
    readonly Dictionary<string, int> wages = new(StringComparer.Ordinal);
    readonly HashSet<int> paidSquads = [];
    long wageDay = long.MinValue;

    void PayWages(World world)
    {
        long day = (long)Math.Floor(world.GameHours / 24);
        bool first = wageDay == long.MinValue, newDay = !first && day != wageDay;
        wageDay = day;
        foreach (var squad in world.Squads.All)
        {
            bool appeared = paidSquads.Add(squad.Id);
            if (first || !newDay && (!appeared || squad.Restored)) continue;
            foreach (var id in squad.Members)
            {
                if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { } c || c.IsPlayer || data.PlayerFaction >= 0 && c.Faction == data.PlayerFaction) continue;
                if (!wages.TryGetValue(c.RecordId, out int pay)) wages[c.RecordId] = pay = data.Db.Find(c.RecordId) is { Type: Meitou.Data.Fcs.FcsRecordType.CHARACTER } r ? r.GetInt("wages", 0) : 0;
                if (pay >= 1) squad.Money = pay;
            }
        }
    }

    /// <summary>
    /// The open trade passes to another selected character of the player's in reach of the trader when its customer is no longer selected (the original buys into the window the stock is
    /// dropped on, <c>FUN_140712490</c>, <b>Observed</b>); out of reach the customer stays.
    /// </summary>
    void FollowSelection(World world)
    {
        var selection = world.Player.Selection;
        if (selection.Count == 0 || selection.Contains(customer) || !world.Characters.TryResolveNext(trader, out int t)) return;
        foreach (var id in selection)
            if (world.Characters.TryResolveNext(id, out int c) && world.Characters.Cold(c) is { IsPlayer: true, Inventory: not null, Medical: not { Incapacitated: true } } && InReach(world, c, t))
            {
                customer = id;
                return;
            }
    }

    internal void RefreshView(World world) =>
        view = open && Resolve(world, out int ci, out int ti, out var shop) ? Publish(world, ci, ti, shop!) : null;

    public TradeView? PublishTrade() => view;

    /// <summary>Keeps the trader standing while the customer comes and trades (residents wander; the original's shopkeepers stand at their node). Its wander resumes on close.</summary>
    void Hold(World world, int t)
    {
        ref var n = ref world.Characters.Next[t];
        world.Characters.Cold(t)!.Engaged = true;   // the AI leaves it alone until the trade closes
        if (n.Task == (byte)CharacterTask.Wander) traderWandered = true;
        if (n.Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Idle) TaskStart.Stand(world.Characters, t);
    }

    void Close(World world)
    {
        session.Active = false;
        session = new();
        if (world.Characters.TryResolveNext(trader, out int held)) world.Characters.Cold(held)!.Engaged = false;
        if (traderWandered && world.Characters.TryResolveNext(trader, out int t) && world.Characters.Next[t].Task == (byte)CharacterTask.Idle)
            TaskStart.Become(world, t, CharacterTask.Wander, Meitou.Data.Gameplay.Ai.TaskType.WANDERER, TaskSource.Npc);
        customer = trader = CharacterId.None;
        open = traderWandered = false;
        view = null;
    }

    // ------------------------------------------------------------------ shops

    SquadTemplate? TemplateOf(string id)
    {
        if (!templates.TryGetValue(id, out var t))
            templates[id] = t = data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r) && SquadTemplate.From(r) is { HasShop: true } shop ? shop : null;
        return t;
    }

    void Stock(World world)
    {
        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction >= 0 && (squad.Faction == world.Player.Faction || squad.Faction == data.PlayerFaction)) continue;
            bool living = Living(world, squad);
            if (squad.Shop is { Building: not null } closing && !living)
            {
                // Nobody of the squad is left alive: its stock goes back into its containers, which keep it, as the original's containers hold the stock (artifacts included).
                GiveBack(world, closing, all: true);
                squad.Shop = null;
                continue;
            }
            if (squad.Shop is { } shop)
            {
                if (world.Tick < shop.NextRestockTick || TemplateOf(squad.TemplateId) is not { } again) continue;
                Refill(world, squad, shop, again);
            }
            else if (living && TemplateOf(squad.TemplateId) is { } template)
            {
                var terms = new Trader(squad.Town, squad.Faction, template.BuyMult, template.BuysStolen, template.BuysIllegal);
                // A housed trader keeps a building shop when its home has stock containers, whether or not it made prisoner or slave squads (FUN_140583a10 / FUN_1404ff5e0, Observed); without containers the squad shop stays (Meitou fallback, the original stocks nothing).
                squad.Shop = (squad.HomeBuilding is { } home ? Shop.ForBuilding(home, terms) : null)
                    ?? new Shop { Terms = terms };
                TakeContents(world, squad.Shop);
                Refill(world, squad, squad.Shop, template);
            }
            else if (squad.HomeBuilding is { } home && HomeTemplateOf(squad.TemplateId) is { } contents && ShopFiller.VendorLinks(contents, FactionRecord(squad.Faction)).Count > 0) FillHome(world, squad, contents, home);
        }
    }

    /// <summary>
    /// A new building shop's stock containers hand what they hold (a town's artifacts) to the stock, each into its own rectangle (in the original the stock is what they hold,
    /// docs/game/economy.md "Artifacts").
    /// </summary>
    static void TakeContents(World world, Shop shop)
    {
        for (int k = 0; k < shop.Containers.Count; k++)
        {
            if (world.Fixtures.Find(shop.Containers[k]) is not { Contents: { } grid } || grid.Items.Count == 0) continue;
            var region = shop.Stock.Regions![k];
            foreach (var item in grid.Items)
            {
                item.X += region.X;
                item.Y += region.Y;
                shop.Stock.Items.Add(item);
            }
            grid.Items.Clear();
        }
    }

    /// <summary>Whether someone of the squad is alive.</summary>
    static bool Living(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int slot) && table.Cold(slot) is { DiedTick: < 0 }) return true;
        return false;
    }

    /// <summary>
    /// A building shop whose squad is dropped, made afresh or all dead hands its unique items (artifacts; with <paramref name="all"/>, its whole stock) back to their containers,
    /// which keep them for the next shop, as the original's containers keep them until taken (docs/game/economy.md "Artifacts").
    /// </summary>
    internal static void GiveBack(World world, Shop shop, bool all = false)
    {
        if (shop.Stock.Regions is not { } regions) return;
        for (int k = 0; k < shop.Containers.Count; k++)
        {
            if (world.Fixtures.Find(shop.Containers[k]) is not { Contents: { } grid }) continue;
            foreach (var item in shop.Stock.Items.Where(i => (all || i.Unique) && regions[k].Holds(i)).ToList())
            {
                shop.Stock.Items.Remove(item);
                (item.X, item.Y) = (item.X - regions[k].X, item.Y - regions[k].Y);
                if (grid.IsFree("main", item.X, item.Y, item.Width, item.Height)) grid.Items.Add(item);
                else grid.AddTo("main", item);
            }
        }
    }

    Meitou.Data.GameRecord? FactionRecord(int faction) => (uint)faction < (uint)data.Factions.Count ? data.Db.Find(data.Factions[faction].Id) : null;

    SquadTemplate? HomeTemplateOf(string id)
    {
        if (!homeTemplates.TryGetValue(id, out var t))
            homeTemplates[id] = t = data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r) && SquadTemplate.From(r) is { IsTrader: false } fill && (fill.Vendors.Count > 0 || fill.BuildingDesignation is 2 or 3 or 5 or 6 or 8 or 9 or 11) ? fill : null;
        return t;
    }

    /// <summary>
    /// A housed squad with <c>vendors</c> that is no trader (a resident's <c>house contents</c>): the building filler fills its home's storage containers as a trader's (the squad factory
    /// <c>FUN_140583a10</c> tests <c>vendors</c>, not <c>is trader</c>, <b>Observed</b>), unlocking them; here each unit goes into the first container with room and what fits nowhere is
    /// lost. Engine choice: again every <c>vendors refresh time</c> hours, but only while the containers hold nothing but unique items (a loaded save keeps what they hold); the purse is not touched.
    /// </summary>
    void FillHome(World world, Squad squad, SquadTemplate template, Meitou.Data.World.TownBuilding home)
    {
        if (world.Tick < squad.NextHomeFillTick) return;
        bool first = squad.NextHomeFillTick == 0;
        squad.NextHomeFillTick = world.Tick + (long)(template.VendorsRefreshTime / BodySystem.HoursPerTick);
        var boxes = new List<Locks.Fixture>();
        foreach (var f in home.Fixtures)
            if (f.HoldsShopStock && world.Fixtures.Find(f.Id) is { Contents: not null } box) boxes.Add(box);
        if (boxes.Count == 0 || boxes.Any(b => b.Contents!.Items.Any(i => !i.Unique))) return;
        if (Shop.ForBuilding(home, new Trader(squad.Town, squad.Faction, template.BuyMult, template.BuysStolen, template.BuysIllegal)) is not { } merged) return;
        merged.Fills = (int)world.Tick;
        ShopFiller.Fill(merged, template, Market(world), data, world.Seed, (ulong)squad.Id, specials: first);
        foreach (var item in merged.Stock.Items)
            foreach (var box in boxes)
                if (box.Contents!.Add(item)) break;
        foreach (var box in boxes)
            if (box.Lock is { } l) l.Locked = false;
    }

    void Refill(World world, Squad squad, Shop shop, SquadTemplate template)
    {
        squad.Money = ShopFiller.Fill(shop, template, Market(world), data, world.Seed, (ulong)squad.Id, specials: shop.Fills == 0);
        // The building filler unlocks the stock containers it fills (FUN_14095b210).
        if (shop.Building is not null && squad.HomeBuilding is { } home)
            foreach (var f in home.Fixtures)
                if (f.HoldsShopStock && world.Fixtures.Find(f.Id)?.Lock is { } l) l.Locked = false;
        shop.NextRestockTick = world.Tick + (long)(template.VendorsRefreshTime / BodySystem.HoursPerTick);
    }

    /// <summary>The shop a character trades for: its squad's (null: not a trader).</summary>
    public static Shop? ShopOf(World world, CharacterCold cold) => cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId)?.Shop : null;

    // ------------------------------------------------------------------ trading

    bool Resolve(World world, out int c, out int t, out Shop? shop)
    {
        var table = world.Characters;
        shop = null;
        t = -1;
        if (!table.TryResolveNext(customer, out c) || !table.TryResolveNext(trader, out t)) return false;
        var cc = table.Cold(c)!;
        var tc = table.Cold(t)!;
        if (!cc.IsPlayer || cc.Medical is { Incapacitated: true } || tc.Medical is { Incapacitated: true } || tc.InCombat || cc.Inventory is null) return false;
        if (AggroSystem.IsEnemy(data.Relations, cc.Faction, tc.Faction)) return false;
        shop = ShopOf(world, tc);
        return shop is not null;
    }

    bool InReach(World world, int c, int t)
    {
        var a = world.Characters.Next[c].Position;
        var b = world.Characters.Next[t].Position;
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return dx * dx + dz * dz <= Settings.Range * Settings.Range;
    }

    static ItemInstance? Find(Inventory inventory, InventoryItemToken token)
    {
        var item = inventory.All().ElementAtOrDefault(token.Index);
        return item is not null && item.Record == token.Record && item.Section == token.Section && item.X == token.X && item.Y == token.Y && item.Quantity == token.Quantity
            && item.Quality == token.Quality && (item.Contents is null || item.Quantity == 1) ? item : null;
    }

    // ------------------------------------------------------------------ the held item (PlayerSystem, Items/HeldItems.cs)

    /// <summary>The open trade's stock and its customer while the trade's windows show; null when no trade is open.</summary>
    internal Inventory? OpenStock(World world, out CharacterId buyer)
    {
        buyer = CharacterId.None;
        if (!open || !Resolve(world, out _, out _, out var shop)) return null;
        buyer = customer;
        return shop!.Stock;
    }

    /// <summary>
    /// How many of <paramref name="units"/> of a held item the payer can afford (the drop caps the quantity so, <c>FUN_140715560</c>); none: "You can't afford that." or "The shopkeeper can't
    /// afford that.".
    /// </summary>
    internal int AffordHeld(World world, ItemInstance item, int units, bool sell)
    {
        if (!Resolve(world, out int c, out int t, out var shop)) return 0;
        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
        int n = Affordable(Market(world), item, Math.Clamp(units, 0, item.Quantity), sell, shop!.Terms, sell ? world.Characters.Cold(c)!.Faction : squad.Faction,
            sell ? squad.Money : world.Player.Money);
        if (n == 0) world.Post(sell ? "The shopkeeper can't afford that." : "You can't afford that.");
        return n;
    }

    /// <summary>A held stolen item put on the stock of a shop that is no fence: the fence roll (8.4); caught, "Got caught selling stolen item." and the pending Fencing crime (true).</summary>
    internal bool CaughtSelling(World world, ItemInstance item)
    {
        if (!Resolve(world, out int c, out int t, out var shop)) return false;
        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
        if (!item.IsStolenFor(world.Characters.Cold(c)!.Faction, data) || !Fences(item, squad, shop!.Terms) || !Caught(world, c, t, item, squad, shop.Terms)) return Smuggling(world, c, item, shop!.Terms);
        world.Post("Got caught selling stolen item.");
        if (Crimes is { } crimes && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, trader, Crime.CrimeSystem.MinTimer, viaWrapper: true))
            crimes.Book(world, c, squad.Faction, trader);
        return true;
    }

    /// <summary>
    /// The smuggling check of a sale (<c>FUN_140794e80</c>, <b>Observed</b>, run after the fence check): an item the culture of the trader's town's faction lists as illegal is refused by a trader that
    /// does not buy illegal goods, "Attempted smuggling!" (code 0xf), and the trader says THIEF_CAUGHT_STEALING_FROM_ME (8) at the seller unless it buys stolen goods. True when refused.
    /// </summary>
    bool Smuggling(World world, int seller, ItemInstance item, Trader terms)
    {
        if (terms.BuysIllegal || (uint)terms.Site >= (uint)data.SiteFactions.Count || Market(world).FactionCulture(data.SiteFactions[terms.Site]) is not { } culture || !culture.Illegal.Contains(item.Record)) return false;
        world.Post("Attempted smuggling!");
        if (!terms.BuysStolen) world.Defer(new Dialogue.DialogueEvent(trader, world.Characters.IdOf(seller), 8) { Tick = world.Tick + 1 });
        return true;
    }

    /// <summary>
    /// Pays for the first <paramref name="units"/> of a held item that changed hands (priced before it moves, a buyback at its receipt's coins): the price goes from the buyer's purse to
    /// the seller's. Returns the moved units' receipt (null when this undid an earlier trade of this window).
    /// </summary>
    internal TradeReceipt? PayHeld(World world, ItemInstance item, int units, bool sell)
    {
        if (units <= 0 || !Resolve(world, out int c, out int t, out var shop)) return null;
        var cold = world.Characters.Cold(c)!;
        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
        var m = Market(world);
        int holder = sell ? cold.Faction : squad.Faction;
        var receipt = Receipt(m, item, units, sell, shop!.Terms, holder);
        long price = Price(m, item, units, sell, shop.Terms, holder);
        if (sell) { squad.Money -= price; world.Player.Money += price; }
        else { world.Player.Money -= price; squad.Money += price; }
        world.Post($"{cold.Name} {(sell ? "sold" : "bought")} {Units(units, item.Name)} for c.{price}");
        return receipt;
    }

    void Buy(World world, BuyItem buy)
    {
        if (!Resolve(world, out int c, out int t, out var shop)) return;
        var cold = world.Characters.Cold(c)!;
        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
        if (Find(shop!.Stock, buy.Item) is not { } item) return;
        if (item.Contents is { Items.Count: > 0 }) { world.Post(BagNotEmpty); return; }
        var m = Market(world);
        int units = Affordable(m, item, Math.Clamp(buy.Quantity, 1, item.Quantity), false, shop.Terms, squad.Faction, world.Player.Money);
        if (units == 0)
        {
            world.Post("You can't afford that.");
            return;
        }
        var receipt = Receipt(m, item, units, false, shop.Terms, squad.Faction);
        var bought = item.WithQuantity(units);
        bought.Buyback = receipt;
        long price = Price(m, item, units, false, shop.Terms, squad.Faction);
        int placed;
        if (item.Contents is not null)
        {
            // An empty bag moves itself (a copy would have no contents inventory); its receipt is replaced (none: this undid a sale).
            placed = TransferContainer(shop.Stock, buy.Item, cold.Inventory!, buy.Section, buy.X, buy.Y, buy.Equip, cold);
            if (placed > 0) item.Buyback = receipt;
            if (placed > 0 && InventoryCommands.IsEquipmentSection(item.Section)) assembly.RefreshEquipment(cold);
        }
        else if (buy.Section == "backpack_attach") placed = 0;
        else if (buy.Section is { } section) placed = PlaceAt(cold.Inventory!, bought, units, section, buy.X, buy.Y);
        else if (buy.Equip && InventoryCommands.TryEquip(cold.Inventory!, bought.WithQuantity(1), cold))
        {
            // One unit is worn; the rest of a stack goes where it fits.
            assembly.RefreshEquipment(cold);
            placed = 1;
            if (units > 1)
            {
                var rest = bought.WithQuantity(units - 1);
                rest.Buyback = bought.Buyback?.Skip(1);
                placed += Place(cold.Inventory!, rest, units - 1);
            }
        }
        else placed = Place(cold.Inventory!, bought, units);
        if (placed == 0)
        {
            world.Post("No room for that item.");
            return;
        }
        if (placed < units) price = Price(m, item, placed, false, shop.Terms, squad.Faction);
        if (item.Contents is null) Take(shop.Stock, item, placed);
        world.Player.Money -= price;
        squad.Money += price;
        world.Post($"{cold.Name} bought {Units(placed, item.Name)} for c.{price}");
    }

    void Sell(World world, SellItem sell)
    {
        if (!Resolve(world, out int c, out int t, out var shop)) return;
        var cold = world.Characters.Cold(c)!;
        var tc = world.Characters.Cold(t)!;
        var squad = world.Squads.Find(tc.SquadId)!;
        if (Find(cold.Inventory!, sell.Item) is not { } item || !InventoryCommands.IsMovableSection(item.Section)) return;
        if (item.LockedOn) { world.Post("This item is locked in place and cannot be removed."); return; }
        if (item.Contents is { Items.Count: > 0 }) { world.Post(BagNotEmpty); return; }
        var m = Market(world);
        int units = Affordable(m, item, Math.Clamp(sell.Quantity, 1, item.Quantity), true, shop!.Terms, cold.Faction, squad.Money);
        if (units == 0)
        {
            world.Post("The shopkeeper can't afford that.");
            return;
        }
        if (item.IsStolenFor(cold.Faction, data) && Fences(item, squad, shop.Terms) && Caught(world, c, t, item, squad, shop.Terms))
        {
            world.Post("Got caught selling stolen item.");
            if (Crimes is { } crimes && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, trader, Crime.CrimeSystem.MinTimer, viaWrapper: true))
                crimes.Book(world, c, squad.Faction, trader);
            return;
        }
        if (Smuggling(world, c, item, shop.Terms)) return;
        var receipt = Receipt(m, item, units, true, shop.Terms, cold.Faction);
        var sold = item.WithQuantity(units);
        sold.Buyback = receipt;
        long price = Price(m, item, units, true, shop.Terms, cold.Faction);
        bool equipped = InventoryCommands.IsEquipmentSection(item.Section);
        int placed = item.Contents is not null
            ? TransferContainer(cold.Inventory!, sell.Item, shop.Stock, sell.X is not null ? "main" : null, sell.X ?? 0, sell.Y, false, null)
            : sell.X is { } sx ? PlaceAt(shop.Stock, sold, units, "main", sx, sell.Y) : Place(shop.Stock, sold, units);
        if (placed == 0)
        {
            world.Post("No room for that item.");
            return;
        }
        if (item.Contents is not null) item.Buyback = receipt;
        if (placed < units) price = Price(m, item, placed, true, shop.Terms, cold.Faction);
        if (item.Contents is null) Take(cold.Inventory!, item, placed);
        if (equipped) assembly.RefreshEquipment(cold);
        squad.Money -= price;
        world.Player.Money += price;
        world.Post($"{cold.Name} sold {Units(placed, item.Name)} for c.{price}");
    }

    /// <summary>
    /// The chance a shop that is no fence lets a stolen item through (8.4, <c>FUN_140793490</c>): lerp(thievery / 100, 0.4, 1.25) of the seller, halved when the item was stolen from the
    /// shop's faction and halved again when it was stolen from the shop's own squad; 1 or more never gets caught. The roll is the seller's and the tick's.
    /// </summary>
    public static float FenceChance(float thievery, bool stolenFromShopFaction, bool stolenFromShopSquad = false)
    {
        float p = 0.4f + (1.25f - 0.4f) * (thievery / 100f);
        if (stolenFromShopFaction) p *= 0.5f;
        return stolenFromShopSquad ? p * 0.5f : p;
    }

    /// <summary>
    /// Whether the shop's squad asks about and rolls on a stolen good at all (<c>FUN_140792830</c>, <b>Observed</b>): always, unless it buys stolen goods; a squad that does only for goods
    /// stolen from one of its own members (the item's owner handle names the squad's platoon).
    /// </summary>
    static bool Fences(ItemInstance item, Squad shopSquad, Trader terms) => !terms.BuysStolen || item.OwnerSquad >= 0 && item.OwnerSquad == shopSquad.Id;

    /// <summary>The odds <c>FUN_140793490</c> answers for goods nobody looks for (<c>DAT_14169bb44</c>, <b>Verified</b>: 1.1).</summary>
    const float NeverCaught = 1.1f;

    /// <summary>The faction the shop's squad sits in regards another as its own (the game's <c>FUN_1406b2630</c>: the same faction, or a relation of 50 or more).</summary>
    bool Allied(int a, int b) => a == b || data.Relations.Contains(a) && data.Relations.Contains(b) && data.Relations.IsAlly(a, b);

    /// <summary>
    /// The chance the trader (slot <paramref name="trader"/>) lets the seller's stolen item through (<c>FUN_140793490</c>, <b>Observed</b>). With the robbed character known
    /// (<see cref="ItemInstance.Owner"/>): 1 when it is gone or of the seller's own squad, and its faction now is the one tested. Of another faction than the shop's: 1 for a shop that buys stolen
    /// goods (<see cref="Fences"/>) and for a bandit or adventurer faction; 1.1 (never caught) when the shop's faction is not allied to it and the owner's squad's home town is not the town the trader
    /// stands in (an item with no known owner has no home town). Else <see cref="FenceChance"/>, halved for the shop's faction and again for the shop's own squad.
    /// </summary>
    float FenceOdds(World world, float thievery, ItemInstance item, int seller, int trader, Squad shopSquad, Trader terms)
    {
        var table = world.Characters;
        int from = item.StolenFrom;
        CampaignTown? home = null;
        if (!item.Owner.IsNone)
        {
            if (!table.TryResolveNext(item.Owner, out int o) || table.Cold(o) is not { } owner) return 1;
            if (item.OwnerSquad == table.Cold(seller)!.SquadId) return 1;
            from = owner.Faction;
            home = world.Squads.Find(owner.SquadId) is { Town: >= 0 } squad ? CampaignTown.OfSite(squad.Town) : CampaignTown.None;
        }
        int shopFaction = shopSquad.Faction;
        if (from != shopFaction && from >= 0)
        {
            if (!Fences(item, shopSquad, terms) || data.Relations.Factions[from].FundamentalType is FundamentalType.Bandit or FundamentalType.Adventurer) return 1;
            if (!Allied(shopFaction, from) && home != Ai.AiSystem.TownAt(data, world, table.Next[trader].Position)) return NeverCaught;
        }
        return FenceChance(thievery, from == shopFaction, item.OwnerSquad >= 0 && item.OwnerSquad == shopSquad.Id);
    }

    bool Caught(World world, int seller, int trader, ItemInstance item, Squad shopSquad, Trader terms)
    {
        var cold = world.Characters.Cold(seller)!;
        float p = FenceOdds(world, cold.Stats?[StatsEnumerated.Thieving] ?? 0, item, seller, trader, shopSquad, terms);
        bool caught = p < 1 && Rng.Float(Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(seller)), RngPurpose.Trade, (ulong)world.Tick)) >= p;
        // The seller trains thievery whenever the odds are below 0.98 (<c>FUN_140793770</c>): 5 x 2 for a fence that works, 20 x 2 for one that is caught.
        if (p < Crime.CrimeSystem.FenceXpBelow) Crimes?.TrainThievery(world, seller, caught ? Crime.CrimeSystem.FenceCaughtXp : Crime.CrimeSystem.FenceXp);
        return caught;
    }

    /// <summary>How many of <paramref name="wanted"/> units the payer's <paramref name="money"/> covers (the trade window splits the stack to what is affordable).</summary>
    int Affordable(Market m, ItemInstance item, int wanted, bool sell, Trader terms, int holderFaction, long money)
    {
        for (int n = wanted; n > 0; n--)
            if (Price(m, item, n, sell, terms, holderFaction) <= money) return n;
        return 0;
    }

    // Containers move whole: stack copies intentionally omit Contents. Transfer validates the destination before changing either inventory.
    static int TransferContainer(Inventory from, InventoryItemToken token, Inventory into, string? section, int x, int y, bool equip, CharacterCold? wearer)
    {
        if (section is not (null or "main" or "backpack_attach")) return 0;
        if (section == "backpack_attach") return InventoryCommands.Transfer(from, token, into, section, 0, 0, wearer: wearer) ? 1 : 0;
        if (equip && InventoryCommands.Transfer(from, token, into, "backpack_attach", 0, 0, wearer: wearer)) return 1;
        if (section == "main" && InventoryCommands.Transfer(from, token, into, section, x, y)) return 1;
        var item = Find(from, token);
        return item is not null && into.FindCell("main", item.Width, item.Height, out int cx, out int cy)
            && InventoryCommands.Transfer(from, token, into, "main", cx, cy) ? 1 : 0;
    }

    bool IsBuyback(ItemInstance item, bool sell) => item.Buyback is { } r && ReferenceEquals(r.Session, session) && r.Sell == sell;

    long Price(Market market, ItemInstance item, int units, bool sell, Trader terms, int holder) => IsBuyback(item, sell)
        ? item.Buyback!.Price(units) : market.StackPrice(item.WithQuantity(units), sell, terms, holder);

    TradeReceipt? Receipt(Market market, ItemInstance item, int units, bool sell, Trader terms, int holder)
    {
        if (IsBuyback(item, sell)) return null;
        var coins = new long[units];
        long previous = 0;
        for (int n = 1; n <= units; n++)
        {
            long total = Price(market, item, n, sell, terms, holder);
            coins[n - 1] = total - previous;
            previous = total;
        }
        return new(session, !sell, coins);
    }

    /// <summary>
    /// Puts the units at the cell they were dropped on (the trade window's <c>placeItemFromMouse</c>): the cell of <paramref name="section"/> in <paramref name="root"/> (the backpack's
    /// contents for <c>backpack_content</c>) when the footprint is inside the grid, free and the stack within the limit; otherwise wherever they fit, as <see cref="Place"/>.
    /// </summary>
    static int PlaceAt(Inventory root, ItemInstance copy, int units, string section, int x, int y)
    {
        var grid = section == "main" ? root : section == "backpack_content" ? root.Backpack?.Contents : null;
        if (grid is not null && x >= 0 && y >= 0 && x + copy.Width <= grid.Width && y + copy.Height <= grid.Height && units <= grid.LimitOf(copy)
            && grid.IsFree(section, x, y, copy.Width, copy.Height))
        {
            copy.Section = section;
            copy.X = x;
            copy.Y = y;
            grid.Items.Add(copy);
            return units;
        }
        return Place(root, copy, units);
    }

    internal static int Place(Inventory into, ItemInstance copy, int units)
    {
        copy.Section = "main";
        return into.Add(copy) ? units : units - copy.Quantity;
    }

    /// <summary>Takes <paramref name="units"/> off an item's stack, removing it when none are left.</summary>
    internal static void Take(Inventory from, ItemInstance item, int units)
    {
        if (units >= item.Quantity) from.Remove(item);
        else item.Quantity -= units;
    }

    static string Units(int n, string name) => n == 1 ? name : $"{n} x {name}";

    /// <summary>A bag trades only empty (engine choice; its contents would go unpriced and unchecked for theft).</summary>
    internal const string BagNotEmpty = "That bag isn't empty.";

    TradeView Publish(World world, int c, int t, Shop shop)
    {
        var m = Market(world);
        var cold = world.Characters.Cold(c)!;
        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
        float thievery = cold.Stats?[StatsEnumerated.Thieving] ?? 0;
        TradeItemView View(ItemInstance item, int index, bool sell, int holder)
        {
            long value = m.UnitPrice(item, false, shop.Terms, holder), sellValue = m.UnitPrice(item, true, shop.Terms, holder);
            bool buyback = IsBuyback(item, sell);
            if (buyback)
            {
                if (sell) sellValue = Price(m, item, 1, true, shop.Terms, holder);
                else value = Price(m, item, 1, false, shop.Terms, holder);
            }
            bool stolen = item.IsStolenFor(holder, data);
            // The roll's own odds feed the hover box, the sale prompt and the roll alike; only where the shop would roll on the item at all.
            float? odds = sell && stolen && Fences(item, squad, shop.Terms) ? FenceOdds(world, thievery, item, c, t, squad, shop.Terms) : null;
            return new(index, item.Record, item.Name, item.Section, item.X, item.Y, item.Width, item.Height, item.Quantity,
                item.Quality, Price(m, item, item.Quantity, sell, shop.Terms, holder), Price(m, item, 1, sell, shop.Terms, holder), stolen)
            {
                Buyback = buyback, Container = item.Contents is not null,
                Material = item.MaterialId, Colour = item.ColourId, Charge = item.ChargeFraction, Value = value, SellValue = sellValue, Weight = item.Weight, PriceFactor = m.TraderFactor(item, shop.Terms, holder),
                MarketFactor = m.MarketFactor(shop.Terms.Site, item.Record), Company = item.CompanyId, Level = item.Level, Charges = item.Charges, MaxCharges = item.MaxCharges, StolenFrom = stolen ? item.StolenFrom : -1, Unique = item.Unique,
                FenceChance = odds is { } shown ? Math.Clamp(shown, 0, 1) : null,
                FenceOdds = odds is < 1 ? Math.Max(odds.Value, 0) : null,
                Illegal = sell && (uint)shop.Terms.Site < (uint)data.SiteFactions.Count && m.FactionCulture(data.SiteFactions[shop.Terms.Site]) is { } culture && culture.Illegal.Contains(item.Record),
            };
        }
        // Only the stock grid's own items, at their All() indices (which tokens use): a bag's contents are not loose stock.
        var stock = new List<TradeItemView>(shop.Stock.Items.Count);
        int at = 0;
        foreach (var item in shop.Stock.Items)
        {
            stock.Add(View(item, at, false, squad.Faction));
            at += 1 + (item.Contents?.All().Count() ?? 0);
        }
        var goods = cold.Inventory!.All().Select((item, index) => (item, index)).Where(p => InventoryCommands.IsMovableSection(p.item.Section))
            .Select(p => View(p.item, p.index, true, cold.Faction)).ToArray();
        var held = world.Player.Held switch
        {
            { From.Holder: ItemHolder.Stock } h => View(h.Item, -1, false, squad.Faction),
            { From.Holder: ItemHolder.Character } h when h.From.Character == customer => View(h.Item, -1, true, cold.Faction),
            _ => null,
        };
        return new TradeView(customer, trader, world.Characters.Cold(t)!.Name, squad.Money, world.Player.Money, shop.Stock.Width, shop.Stock.Height, stock, goods, shop.Terms.BuysStolen, shop.Terms.BuysIllegal) { Held = held, Unused = shop.UnusedCells };
    }
}

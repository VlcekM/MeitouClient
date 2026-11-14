using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Economy;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Trading;

/// <summary>
/// The player opens a trade between one of their characters and a trader: the customer walks up to the trader and the trade window opens in reach.
/// </summary>
public sealed record OpenTrade(CharacterId Customer, CharacterId Trader) : OrderCommand;
/// <summary>
/// Closes the open trade.
/// </summary>
public sealed record CloseTrade : SimCommand;
/// <summary>
/// Buys <see cref="Quantity"/> units of the stock item <see cref="Item"/> (its index in the shop's stock) into the customer's inventory: at the cell
/// <see cref="X"/>, <see cref="Y"/> of <see cref="Section"/> (<c>main</c> or <c>backpack_content</c>, the cell the item was dropped on) when that is
/// free, else wherever it fits. A backpack may also target the single <c>backpack_attach</c> equipment slot.
/// </summary>
public sealed record BuyItem(InventoryItemToken Item, int Quantity) : SimCommand
{
    /// <summary>
    /// The section of the cell the item was dropped on (<c>main</c>, <c>backpack_content</c> or <c>backpack_attach</c>); null: wherever it fits.
    /// </summary>
    public string? Section { get; init; }
    /// <summary>
    /// The column of the cell dropped on.
    /// </summary>
    public int X { get; init; }
    /// <summary>
    /// The row of the cell dropped on.
    /// </summary>
    public int Y { get; init; }
    /// <summary>
    /// The window's right click (no cell chosen): one unit goes into the equipment slot that takes it when that is free, before the grids
    /// (<see cref="InventoryCommands.TryEquip"/>).
    /// </summary>
    public bool Equip { get; init; }
    /// <summary>
    /// The character whose window it goes into: the trade passes to them (<see cref="TradeSystem.TradeWith"/>). None: the customer, or the
    /// selection's.
    /// </summary>
    public CharacterId Into { get; init; } = CharacterId.None;
}

/// <summary>
/// Sells <see cref="Quantity"/> units of the customer's item <see cref="Item"/> (its index in the customer's inventory) to the shop, at the stock
/// cell dropped on when free.
/// </summary>
public sealed record SellItem(InventoryItemToken Item, int Quantity) : SimCommand
{
    /// <summary>
    /// The column of the stock cell dropped on; null: wherever it fits.
    /// </summary>
    public int? X { get; init; }
    /// <summary>
    /// The row of the stock cell dropped on.
    /// </summary>
    public int Y { get; init; }
    /// <summary>
    /// The character whose window it comes from: the trade passes to them (<see cref="TradeSystem.TradeWith"/>). None: the customer, or the
    /// selection's.
    /// </summary>
    public CharacterId From { get; init; } = CharacterId.None;
}

/// <summary>
/// One item of a trade window, with what it costs (stock) or fetches (the customer's goods) as a whole stack.
/// </summary>
public sealed record TradeItemView(int Index, string Record, string Name, string Section, int X, int Y, int Width, int Height, int Quantity,
    float Quality, long Price, long UnitPrice, bool Stolen)
{
    /// <summary>
    /// The current transfer reverses a purchase or sale from this trade window at its original price.
    /// </summary>
    public bool Buyback { get; init; }
    /// <summary>
    /// A bag (a container with a contents grid): it trades whole, and may go into the backpack slot.
    /// </summary>
    public bool Container { get; init; }
    /// <summary>
    /// The item's material record ("" none), which picks its icon.
    /// </summary>
    public string Material { get; init; } = "";
    /// <summary>
    /// The item lies turned a quarter (<see cref="Items.ItemInstance.Rotated"/>): its size is the turned footprint's, and the icon is drawn turned.
    /// </summary>
    public bool Rotated { get; init; }
    /// <summary>
    /// The COLOR_DATA it is dyed with (<see cref="Items.ItemInstance.ColourId"/>), which picks its icon.
    /// </summary>
    public string Colour { get; init; } = "";
    /// <summary>
    /// How full the item is (0 to 1); null: no charge bar.
    /// </summary>
    public float? Charge { get; init; }
    /// <summary>
    /// The item's manufacturer record (<c>company sid</c>; "" none), model level and charges, and the trader factor of its price
    /// (<c>FUN_14079e6d0</c>, 1: none).
    /// </summary>
    public string Company { get; init; } = "";
    /// <summary>
    /// The item's model level.
    /// </summary>
    public int Level { get; init; }
    /// <summary>
    /// The item's charges.
    /// </summary>
    public float Charges { get; init; }
    /// <summary>
    /// The charges of a full unit.
    /// </summary>
    public float MaxCharges { get; init; }
    /// <summary>
    /// The trader factor of its price (1: none).
    /// </summary>
    public float PriceFactor { get; init; } = 1;
    /// <summary>
    /// The trader's town market factor of the item (<see cref="Market.MarketFactor"/>, 1: none); with <see cref="PriceFactor"/> it makes a trade
    /// item's <c>Price mark-up</c>.
    /// </summary>
    public float MarketFactor { get; init; } = 1;
    /// <summary>
    /// The faction a stolen item was stolen from, -1 when it is not stolen.
    /// </summary>
    public int StolenFrom { get; init; } = -1;
    /// <summary>
    /// The item's <c>unique</c> flag (<see cref="Items.ItemInstance.Unique"/>): the tooltip's [Unique] row.
    /// </summary>
    public bool Unique { get; init; }
    /// <summary>
    /// How many items a bag holds (-1: not a bag); an empty one's tooltip name ends in " (empty)".
    /// </summary>
    public int BagItems { get; init; } = -1;
    /// <summary>
    /// The tooltip's two prices of one unit (<c>FUN_14079e7a0</c> calls slot 81, <b>Verified</b>): what the shop asks for it (Value) and what it pays
    /// (Sell value).
    /// </summary>
    public long Value { get; init; }
    /// <summary>
    /// What the shop pays for one unit.
    /// </summary>
    public long SellValue { get; init; }
    /// <summary>
    /// The stack's weight in kg.
    /// </summary>
    public float Weight { get; init; }
    /// <summary>
    /// A stolen good of the customer's that the shop would roll on (<c>FUN_140792830</c>, whoever it was stolen from): the chance (0 to 1) a sale
    /// goes through unnoticed, <see cref="TradeSystem"/>'s fence odds, the hover box's number; null otherwise.
    /// </summary>
    public float? FenceChance { get; init; }
    /// <summary>
    /// A stolen good of the customer's that a shop which is no fence would buy only with a risk: the chance (0 to 1, below 1) a sale goes through
    /// unnoticed, whoever it was stolen from (<c>FUN_140715290</c> asks "Fence it anyway?" with the opposite in per cent); null when the sale is safe
    /// or the item is not stolen.
    /// </summary>
    public float? FenceOdds { get; init; }
    /// <summary>
    /// A good of the customer's that the culture of the trader's faction lists as illegal (the smuggling prompt, <c>FUN_140712920</c>).
    /// </summary>
    public bool Illegal { get; init; }
    /// <summary>
    /// The token addressing the item in its inventory.
    /// </summary>
    public InventoryItemToken Token => new(Index, Record, Section, X, Y, Quantity, Quality);
}

/// <summary>
/// Arranges the open trade's stock (the trader window's ARRANGE button): the stock's grid as <see cref="InventoryCommands.Arrange"/>.
/// </summary>
public sealed record ArrangeStock : SimCommand;

/// <summary>
/// The open trade as the UI shows it (published with the snapshot).
/// </summary>
public sealed record TradeView(CharacterId Customer, CharacterId Trader, string TraderName, long ShopMoney, long PlayerMoney,
    int StockWidth, int StockHeight,
    IReadOnlyList<TradeItemView> Stock, IReadOnlyList<TradeItemView> Goods, bool BuysStolen, bool BuysIllegal = false)
{
    /// <summary>
    /// The held item (<see cref="PlayerState.Held"/>) priced as stock (lifted from the stock) or as a seller's goods (lifted from one in
    /// <see cref="GoodsOf"/>); null otherwise.
    /// </summary>
    public TradeItemView? Held { get; init; }
    /// <summary>
    /// The movable items of every player character who could take the trade over (the customer's are <see cref="Goods"/>), priced for that seller;
    /// keyed by character.
    /// </summary>
    public IReadOnlyDictionary<CharacterId, IReadOnlyList<TradeItemView>> GoodsOf { get; init; } =
        new Dictionary<CharacterId, IReadOnlyList<TradeItemView>>();
    /// <summary>
    /// The stock cells outside every container of a building shop (<see cref="Shop.Unused"/>), which hold nothing; empty for a squad shop.
    /// </summary>
    public IReadOnlyList<GridRegion> Unused { get; init; } = [];
}

/// <summary>
/// A system that has an open trade to publish (the trade system).
/// </summary>
public interface ITradeSource
{
    /// <summary>
    /// The open trade as the window shows it; null: none.
    /// </summary>
    TradeView? PublishTrade();
}

/// <summary>
/// Settings of the <see cref="TradeSystem"/>.
/// </summary>
public sealed class TradeSettings
{
    /// <summary>
    /// How close another selected character must stand to the trader to take over the open trade: the original's item move between two characters'
    /// inventories (the stock dropped on a character window) allows 1000 when unflagged (<c>FUN_14070d290</c>, <b>Observed</b>; whether the trade
    /// window flags it is <b>Unknown</b>, docs/game/economy.md "Trading range").
    /// </summary>
    public float PassRange { get; init; } = 1000;
    /// <summary>
    /// Ticks between looks at the squads for new shops and due restocks (engine choice: half a game second).
    /// </summary>
    public int CheckEveryTicks { get; init; } = 15;
    /// <summary>
    /// Game seconds the customer may spend not moving on its walk to the trader before the opening is dropped (engine choice,
    /// <see cref="Dialogue.WalkUp"/>).
    /// </summary>
    public float ApproachSeconds { get; init; } = 60;
}

/// <summary>
/// The economy in play (docs/simulation.md "Trade", docs/game/economy.md sections 8 and 9):
/// <list type="bullet">
/// <item><b>Shops</b>: a squad made from a template with <c>is trader</c> and vendor lists gets a <see cref="Shop"/> when it appears (the game's
/// first fill at squad creation), filled by <see cref="ShopFiller"/>, with the purse in <see cref="Squad.Money"/>; it restocks every <c>vendors
/// refresh time</c> game hours (9.3). Squads of the player's faction never get one. A housed one gets a building shop when its home has stock
/// containers (<see cref="Shop.ForBuilding"/>).</item>
/// <item><b>Trading</b> (the trade window <c>FUN_140715560</c>, <b>Observed</b>): a purchase is priced by <see cref="Market.StackPrice"/> without the
/// sell flag, a sale with it; when the payer cannot afford the stack the number of units it can afford moves instead, none: "You can't afford that."
/// (3) or "The shopkeeper can't afford that." (4). What does not fit: "No room for that item." (2). The cats move between the player's purse and the
/// shop's.</item>
/// <item><b>Fencing</b> (8.4, <b>Observed</b>): a stolen item sold to a shop that is not a knowing fence (<c>buys stolen</c>; a knowing fence only
/// for goods stolen from its own squad) is caught unless a roll is below lerp(thievery / 100, 0.4, 1.25), halved when the item was stolen from the
/// shop's own faction and again from its own squad (never caught when it was stolen from a bandit or adventurer faction, or from one the shop's
/// faction is not allied to whose home town is not the trader's, <see cref="FenceOdds"/>); the seller trains thievery either way; caught: "Got caught
/// selling stolen item." (9), the sale is refused and the seller gets a pending Fencing crime against the shop's faction, booked at once with the
/// shopkeeper as the witness. The prompt that shows the chance is the UI's. An illegal item (the culture of the town's faction) is refused by a
/// trader that does not buy illegal goods: "Attempted smuggling!" (<see cref="Smuggling"/>); the rest of the smuggling rules (the trader's answer for
/// a smuggler, read by no caller seen) are not built.</item>
/// </list>
/// The shops' stock and restock times are in the state hash (through the squads). The open trade is not, but the buyback receipts it leaves on items
/// are, and they are not saved: a save made mid-trade reloads with a different hash (docs/simulation.md, trading).
/// </summary>
public sealed class TradeSystem : ITickSystem, ITradeSource
{
    /// <summary>
    /// The world's data: shops, templates, factions and the game database.
    /// </summary>
    readonly PopulationData _data;
    /// <summary>
    /// Re-dresses a character whose worn gear changed in a trade.
    /// </summary>
    readonly CharacterAssembly _assembly;
    /// <summary>
    /// The prices, made on first use.
    /// </summary>
    Market? _market;
    /// <summary>
    /// The open trade's customer and trader (none: no trade).
    /// </summary>
    CharacterId _customer = CharacterId.None, _trader = CharacterId.None;
    /// <summary>
    /// Whether the trade window is open (the customer reached the trader), and whether the trader was wandering when held.
    /// </summary>
    bool _open, _traderWandered;
    /// <summary>
    /// The open trade's session (its buy-back receipts); closed and replaced when the trade closes.
    /// </summary>
    TradeSession _session = new();
    /// <summary>
    /// The customer's walk up to the trader.
    /// </summary>
    readonly Dialogue.WalkUp _approach;
    /// <summary>
    /// The view of the open trade published with the snapshot.
    /// </summary>
    TradeView? _view;

    /// <summary>
    /// The waged NPCs' daily pay into their squads' purses.
    /// </summary>
    readonly SquadWages _wages;

    /// <summary>
    /// The trade system over <paramref name="data"/> (default settings when none are given).
    /// </summary>
    public TradeSystem(PopulationData data, TradeSettings? settings = null)
    {
        this._data = data;
        _assembly = new(data);
        Settings = settings ?? new TradeSettings();
        _approach = new(Settings.ApproachSeconds);
        _wages = new(data);
    }

    /// <summary>
    /// The settings.
    /// </summary>
    public TradeSettings Settings { get; }
    /// <summary>
    /// The crime system a caught fence is reported to (null: no crimes).
    /// </summary>
    public Crime.CrimeSystem? Crimes { get; set; }
    /// <summary>
    /// The prices of this world (made at the first tick, from the world's seed).
    /// </summary>
    public Market Market(World world) => _market ??= new Market(_data, world.Seed);

    #region Commands and the open trade

    /// <summary>
    /// Takes the open, close, buy, sell and arrange commands; a trade that cannot open closes again (an enemy trader is attacked instead).
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case OpenTrade o:
                    Close(world);
                    _customer = o.Customer;
                    _trader = o.Trader;
                    if (Parties(world, out _, out int stocked)) StockFor(world, stocked);

                    if (!Resolve(world, out int c, out int t, out _))
                    {
                        // A right click on an enemy is still an attack (the UI sends the trade for any trader; only the world knows who is hostile).
                        if (world.Characters.TryResolveNext(_trader, out int who) && world.Characters.Cold(who) is { } tc
                            && AggroSystem.IsEnemy(_data.Relations, world.Player.Faction, tc.Faction))
                        {
                            world.Defer(new Combat.AttackOrder([_customer], _trader) { Tick = world.Tick + 1 });
                        }

                        Close(world);
                        break;
                    }

                    Hold(world, t);
                    _approach.Begin(world, _customer, c, t);
                    break;
                case CloseTrade:
                    Close(world);
                    break;
                case BuyItem buy when _open:
                    if (CustomerTrades(world, buy.Into)) Buy(world, buy);
                    break;
                case SellItem sell when _open:
                    if (CustomerTrades(world, sell.From)) Sell(world, sell);
                    break;
                case ArrangeStock when _open:
                    if (Resolve(world, out _, out _, out var arranged)) InventoryCommands.Arrange(arranged!.Stock);
                    break;
            }
        }
    }

    /// <summary>
    /// Every <see cref="TradeSettings.CheckEveryTicks"/>: the wages and the shops; then the open trade follows the selection, and the customer's walk
    /// opens the trade in talk range, keeps it within <see cref="TradeSettings.PassRange"/> or closes it.
    /// </summary>
    public void SlowWorld(World world)
    {
        if (world.Tick % Settings.CheckEveryTicks == 0)
        {
            // The wages first: a new squad's members are paid as they are made (Character::giveBirth), before its shop's first fill sets the
            // purse (FUN_14095b210), so a trader with a waged guard starts with the fill's purse, not the guard's wages (#659).
            _wages.Pay(world);
            Stock(world);
        }

        if (_open) FollowSelection(world);

        if (!_customer.IsNone)
        {
            if (!Resolve(world, out int c, out int t, out _)) Close(world);
            // opens in talk range, stays open within the pass-on reach
            else if (_open ? InReach(world, c, t, Settings.PassRange) : InReach(world, c, t))
            {
                if (!_open)
                    TaskStart.Stand(world.Characters, c);   // the customer stops where it stands, not at the trader's spot behind a counter

                _open = true;
                Hold(world, t);
            }
            else if (_open || !_approach.Follow(world, _customer, c, t))
                Close(world);   // walked away, or gave up the walk (another order, or stuck)
            else Hold(world, t);
        }

        RefreshView(world);
    }

    /// <summary>
    /// The open trade passes to another selected character of the player's within <see cref="TradeSettings.PassRange"/> of the trader when its
    /// customer is no longer selected (the original buys into the window the stock is dropped on, <c>FUN_140712490</c>, <b>Observed</b>); farther the
    /// customer stays. False when it stays while not selected.
    /// </summary>
    bool FollowSelection(World world)
    {
        var selection = world.Player.Selection;
        if (selection.Count == 0 || selection.Contains(_customer) || !world.Characters.TryResolveNext(_trader, out int t))
            return true;

        foreach (var id in selection)
        {
            if (world.Characters.TryResolveNext(id, out int c) && Trades(world.Characters.Cold(c)) && InReach(world, c, t, Settings.PassRange))
            {
                _customer = id;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a buy or sell goes ahead: the trade passes to the character whose window it uses (<see cref="TradeWith"/>), or with none named follows
    /// the selection; when that character is out of reach nothing moves and "Out of trading range." is posted.
    /// </summary>
    bool CustomerTrades(World world, CharacterId window)
    {
        if (!window.IsNone) return TradeWith(world, window);
        if (FollowSelection(world)) return true;

        world.Post("Out of trading range.");
        return false;
    }

    /// <summary>
    /// Passes the open trade to the player's character <paramref name="who"/> whose window the stock is dropped on or taken from, when within
    /// <see cref="TradeSettings.PassRange"/> of the trader; farther it posts "Out of trading range." (one who cannot trade, not the player's, dead
    /// or without an inventory, is refused silently), as the original refuses such a drop beyond the item limit (<c>FUN_140712490</c>,
    /// docs/game/economy.md "Trading range").
    /// </summary>
    internal bool TradeWith(World world, CharacterId who)
    {
        if (who == _customer) return true;
        if (!world.Characters.TryResolveNext(who, out int c) || !world.Characters.TryResolveNext(_trader, out int t)
            || !Trades(world.Characters.Cold(c)))
            return false;

        if (InReach(world, c, t, Settings.PassRange))
        {
            _customer = who;
            return true;
        }

        world.Post("Out of trading range.");
        return false;
    }

    /// <summary>
    /// Whether the character can trade: a player character with an inventory, not dead. A knocked-out one trades through its window too: the
    /// original buys into and sells from a carried, unconscious squad member's window (docs/game/economy.md "Trading range").
    /// </summary>
    static bool Trades(CharacterCold? c) => c is { IsPlayer: true, Inventory: not null, Medical: not { Dead: true } };

    /// <summary>
    /// Rebuilds the published view of the open trade.
    /// </summary>
    internal void RefreshView(World world) =>
        _view = _open && Resolve(world, out int ci, out int ti, out var shop) ? Publish(world, ci, ti, shop!) : null;

    /// <inheritdoc/>
    public TradeView? PublishTrade() => _view;

    /// <summary>
    /// Keeps the trader standing while the customer comes and trades (residents wander; the original's shopkeepers stand at their node). Its wander
    /// resumes on close; a travelling trader (a caravan's boss) stops too and its AI walks on after (<see cref="TaskStart.OnAiWalk"/>). A walk
    /// another system holds it for is left alone.
    /// </summary>
    void Hold(World world, int t)
    {
        ref var n = ref world.Characters.Next[t];
        bool travelling = TaskStart.OnAiWalk(world.Characters, t);
        world.Characters.Cold(t)!.Engaged = true;   // the AI leaves it alone until the trade closes
        if (n.Task == (byte)CharacterTask.Wander) _traderWandered = true;
        if (n.Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Idle || travelling)
            TaskStart.Stand(world.Characters, t);
    }

    /// <summary>
    /// Closes the open trade: its session ends, the trader is released (and wanders again when it was), and the view goes.
    /// </summary>
    void Close(World world)
    {
        _session.Active = false;
        _session = new();
        if (world.Characters.TryResolveNext(_trader, out int held)) world.Characters.Cold(held)!.Engaged = false;
        if (_traderWandered && world.Characters.TryResolveNext(_trader, out int t) && world.Characters.Next[t].Task == (byte)CharacterTask.Idle)
            TaskStart.Become(world, t, CharacterTask.Wander, Data.Gameplay.Ai.TaskType.Wanderer, TaskSource.Npc);

        _customer = _trader = CharacterId.None;
        _open = _traderWandered = false;
        _view = null;
    }

    #endregion

    #region Shops

    /// <summary>
    /// Sets the stream that spreads a home's stock over its containers apart from the one that rolled the stock.
    /// </summary>
    const ulong SpreadSalt = 0x5EEDB0C5UL;

    /// <summary>
    /// The squad templates by id, as shops and homes read them (null: no such template).
    /// </summary>
    readonly Dictionary<string, SquadTemplate?> _templates = new(StringComparer.Ordinal), _homeTemplates = new(StringComparer.Ordinal);

    /// <summary>
    /// The template of <paramref name="id"/> when it is a squad template with a shop, cached; null otherwise.
    /// </summary>
    SquadTemplate? TemplateOf(string id)
    {
        if (!_templates.TryGetValue(id, out var t))
        {
            _templates[id] = t = _data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r) && SquadTemplate.From(r) is { HasShop: true } shop
                ? shop : null;
        }

        return t;
    }

    /// <summary>
    /// The shops of every squad not of the player's faction: made, refilled when due, given back to their containers, or a home's contents filled.
    /// </summary>
    void Stock(World world)
    {
        foreach (var squad in world.Squads.All)
        {
            if (PlayersSquad(world, squad)) continue;

            bool living = Living(world, squad);

            if (squad.Shop is { Building: not null } closing && !living)
            {
                // Nobody of the squad is left alive: its stock goes back into its containers, which keep it, as the original's containers hold the
                // stock (artifacts included).
                GiveBack(world, closing, all: true);
                squad.Shop = null;
                continue;
            }

            if (squad.Shop is { Building: not null } lent && TemplateOf(squad.TemplateId) is null && !TradesWith(world, squad))
            {
                // Containers lent to a trade by a housed squad that keeps no shop (StockFor) get their goods back once it ends; the house filling
                // goes on.
                GiveBack(world, lent, all: true);
                squad.Shop = null;
                continue;
            }

            if (squad.Shop is { } shop)
            {
                if (world.Tick < shop.NextRestockTick || TemplateOf(squad.TemplateId) is not { } again) continue;

                Refill(world, squad, shop, again);
            }
            else if (living && TemplateOf(squad.TemplateId) is { } template) MakeShop(world, squad, template);
            else if (squad.HomeBuilding is { } home && HomeTemplateOf(squad.TemplateId) is { } contents
                && ShopFiller.VendorLinks(contents, FactionRecord(squad.Faction)).Count > 0)
            {
                FillHome(world, squad, contents, home);
            }
        }
    }

    /// <summary>
    /// Whether the squad is of the player's faction (the world's or the data's).
    /// </summary>
    bool PlayersSquad(World world, Squad squad) =>
        squad.Faction >= 0 && (squad.Faction == world.Player.Faction || squad.Faction == _data.PlayerFaction);

    /// <summary>
    /// Makes a trader squad's shop and its first fill.
    /// </summary>
    void MakeShop(World world, Squad squad, SquadTemplate template)
    {
        // A squad with a home building stocks only that building's containers, whether or not it made prisoner or slave squads, and never a squad
        // stock; a home without stock containers gets nothing (FUN_140583a10 / FUN_1404ff5e0 / FUN_14095b210, Verified). Only a squad with no home
        // building has a squad shop (s14-f26).
        var terms = TermsOf(squad, template);
        squad.Shop = squad.HomeBuilding is { } home ? Shop.ForBuilding(home, terms) : new Shop { Terms = terms };
        if (squad.Shop is null) return;

        TakeContents(world, squad.Shop);
        Refill(world, squad, squad.Shop, template);
    }

    /// <summary>
    /// The trade terms of <paramref name="squad"/>: its town and faction, and the template's buy mult and stolen and illegal goods flags.
    /// </summary>
    internal static Trader TermsOf(Squad squad, SquadTemplate? template) =>
        new(squad.Town, squad.Faction, template?.BuyMult ?? 1, template?.BuysStolen ?? false, template?.BuysIllegal ?? false);

    /// <summary>
    /// A trade opened with a member of a squad that has no shop yet: a trader squad gets its shop now; any other trades from what the original's
    /// window shows (docs/game/economy.md "Who trades"): a squad with no home building (the Traders Guild's <c>Caravan</c>: <c>vendors</c> but no
    /// <c>is trader</c>) from a stock that is never filled and takes what its members' worn bags hold (<see cref="TakeBags"/>: the pack animals'
    /// loads, <see cref="Items.PackLoad"/>); a housed one from its home's stock containers, lent for the trade (<see cref="Stock"/> hands them
    /// back), none without.
    /// </summary>
    void StockFor(World world, int t)
    {
        if (world.Squads.Find(world.Characters.Cold(t)!.SquadId) is not { Shop: null } squad || PlayersSquad(world, squad))
            return;

        if (TemplateOf(squad.TemplateId) is { } template)
        {
            MakeShop(world, squad, template);
            return;
        }

        squad.Shop = UnfilledShop(squad, TemplateRecord(_data, squad.TemplateId));
        if (squad.Shop is { Building: not null } lent) TakeContents(world, lent);
        else if (squad.Shop is { } carried) TakeBags(world, squad, carried);
    }

    /// <summary>
    /// A homeless squad's new unfilled shop takes what its living members' worn bags hold: the original's window shows those bags merged
    /// (<c>FUN_140953850</c>, docs/game/ui-screens.md section 6). <b>Engine choice</b>: the goods stay in the squad's stock from then on, as a
    /// trader squad's do; what the stock has no room for stays in its bag.
    /// </summary>
    static void TakeBags(World world, Squad squad, Shop shop)
    {
        var table = world.Characters;

        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int slot) || table.Cold(slot) is not { DiedTick: < 0, Inventory.Backpack.Contents: { } bag }) continue;

            foreach (var item in bag.Items.ToList())
                if (shop.Stock.Add(item)) bag.Items.Remove(item);
        }
    }

    /// <summary>
    /// The shop of a squad whose template fills none: a housed squad's over its home's stock containers (null without), else an empty stock.
    /// </summary>
    internal static Shop? UnfilledShop(Squad squad, SquadTemplate? template) =>
        squad.HomeBuilding is { } home ? Shop.ForBuilding(home, TermsOf(squad, template)) : new Shop { Terms = TermsOf(squad, template) };

    /// <summary>
    /// Whether the open trade's trader is of <paramref name="squad"/>.
    /// </summary>
    bool TradesWith(World world, Squad squad) => world.Characters.TryResolveNext(_trader, out int t) && world.Characters.Cold(t)!.SquadId == squad.Id;

    /// <summary>
    /// The squad template <paramref name="id"/> names; null when it names none.
    /// </summary>
    internal static SquadTemplate? TemplateRecord(PopulationData data, string id) =>
        data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r) ? SquadTemplate.From(r) : null;

    /// <summary>
    /// A new building shop's stock containers hand what they hold (a town's artifacts) to the stock, each into its own rectangle (in the original the
    /// stock is what they hold, docs/game/economy.md "Artifacts").
    /// </summary>
    static void TakeContents(World world, Shop shop)
    {
        for (int k = 0; k < shop.Containers.Count; k++)
        {
            if (world.Fixtures.Find(shop.Containers[k]) is not { Contents: { } grid } || grid.Items.Count == 0)
                continue;

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

    /// <summary>
    /// Whether someone of the squad is alive.
    /// </summary>
    static bool Living(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int slot) && table.Cold(slot) is { DiedTick: < 0 }) return true;

        return false;
    }

    /// <summary>
    /// A building shop whose squad is dropped, made afresh or all dead hands its unique items (artifacts; with <paramref name="all"/>, its whole
    /// stock) back to their containers, which keep them for the next shop, as the original's containers keep them until taken (docs/game/economy.md
    /// "Artifacts").
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

    /// <summary>
    /// The FACTION record of faction <paramref name="faction"/>; null for none.
    /// </summary>
    Data.GameRecord? FactionRecord(int faction) => (uint)faction < (uint)_data.Factions.Count ? _data.Db.Find(_data.Factions[faction].Id) : null;

    /// <summary>
    /// The home-filling template of <paramref name="id"/> (<see cref="HomeFill"/>), cached.
    /// </summary>
    SquadTemplate? HomeTemplateOf(string id)
    {
        if (!_homeTemplates.TryGetValue(id, out var t)) _homeTemplates[id] = t = HomeFill(_data.Db.Find(id));

        return t;
    }

    /// <summary>
    /// The squad template of a record that fills a home: a squad that is no trader with vendors, with vendors or of a housing designation.
    /// </summary>
    static SquadTemplate? HomeFill(Data.GameRecord? r)
    {
        if (r is null || !SquadFactory.IsTemplate(r) || SquadTemplate.From(r) is not { } fill) return null;
        if (fill.IsTrader && fill.Vendors.Count != 0) return null;
        return fill.Vendors.Count > 0 || fill.BuildingDesignation is 2 or 3 or 5 or 6 or 8 or 9 or 11 ? fill : null;
    }

    /// <summary>
    /// A housed squad with <c>vendors</c> that is no trader (a resident's <c>house contents</c>): the building filler fills its home's storage
    /// containers as a trader's (the squad factory <c>FUN_140583a10</c> tests <c>vendors</c>, not <c>is trader</c>, <b>Observed</b>), unlocking them;
    /// here each unit goes into the first container with room and what fits nowhere is lost. Engine choice: again every <c>vendors refresh time</c>
    /// hours, but only while the containers hold nothing but unique items (a loaded save keeps what they hold); the purse is not touched.
    /// </summary>
    void FillHome(World world, Squad squad, SquadTemplate template, Meitou.Data.World.TownBuilding home)
    {
        if (world.Tick < squad.NextHomeFillTick) return;

        bool first = squad.NextHomeFillTick == 0;
        squad.NextHomeFillTick = world.Tick + (long)(template.VendorsRefreshTime / BodySystem.HoursPerTick);
        StockHome(world, home, template, new Trader(squad.Town, squad.Faction, template.BuyMult, template.BuysStolen, template.BuysIllegal),
            (ulong)squad.Id, first);
    }

    /// <summary>
    /// A home whose template made no squad (a ruin's or a shed's contents: every list empty, or every count 0) is stocked once when the town is made:
    /// the squad factory (<c>FUN_140583a10</c>, <b>Verified</b>) calls the building filler <c>FUN_14095b210</c> with the specials when the template
    /// has vendor lists (its own or its building designation's, by the faction) or special items, and no refresh follows (no platoon restocks it).
    /// Base data: 26 empty templates hold vendors or special items (the lost libraries, ghost bars, Flotsam safehouse).
    /// </summary>
    public void StockEmptyHome(World world, Meitou.Data.World.TownBuilding home, SquadTemplate template, int site, int faction)
    {
        if (!HoldsStock(template, FactionRecord(faction))) return;

        StockHome(world, home, template, new Trader(site, faction, template.BuyMult, template.BuysStolen, template.BuysIllegal),
            Rng.StableHash(home.Key), true);
    }

    /// <summary>
    /// Whether the template stocks anything: vendor lists for it and its faction, or special items or map items.
    /// </summary>
    bool HoldsStock(SquadTemplate template, Data.GameRecord? faction) =>
        ShopFiller.VendorLinks(template, faction).Count > 0
        || _data.Db.Find(template.Id) is { } r && (r.GetReferences("special items").Count > 0 || r.GetReferences("special map items").Count > 0);

    /// <summary>
    /// The building filler (<c>FUN_14095b210</c>, <b>Observed</b>) returns at once for a building that is another object's child: its location hand
    /// <c>+0x88</c> not null (set only from a parent), its parent <c>+0x238</c> set (a turret mounted on a building) or its door flag <c>+0x1a0</c>
    /// (<c>DoorStuff</c>, made only for a parent building's door). A level-data home is none of these; a turret post is.
    /// </summary>
    void StockHome(World world, Meitou.Data.World.TownBuilding home, SquadTemplate template, Trader terms, ulong key, bool first)
    {
        if (home.Turret is not null) return;

        var boxes = StockBoxes(world, home);
        if (boxes.Count == 0 || boxes.Any(b => b.Grid.Items.Any(i => !i.Unique))) return;
        if (Shop.ForBuilding(home, terms) is not { } merged) return;

        // The filler unlocks every stock container, then keeps those that can hold what the lists make (FUN_14095b210, FUN_14095a5b0).
        foreach (var box in boxes)
            if (box.Fixture.Lock is { } l) l.Locked = false;

        var usable = HomeStock.Usable(boxes, VendorEntries(template, FactionRecord(terms.Faction)));
        if (usable.Count == 0) return;

        merged.Fills = (int)world.Tick;
        ShopFiller.Fill(merged, template, Market(world), _data, world.Seed, key, specials: first);

        // Engine: the special items are spread with the vendor items (the original gives each to a container of a shuffle of its own first).
        var rng = new RandomStream(world.Seed, Rng.Mix(key ^ SpreadSalt ^ ((ulong)merged.Fills << 40)), RngPurpose.Trade);
        HomeStock.Spread(usable, merged.Stock.Items, Market(world).BaseValue, ref rng);

        foreach (var box in usable)
            if (box.Fixture.Lock is { } l) l.Locked = box.Grid.Items.Count > 0;   // FUN_1409565e0 locks the filled ones
    }

    /// <summary>
    /// The home's stock containers (<see cref="Meitou.Data.World.TownFixture.HoldsShopStock"/>) with their records' storage rules.
    /// </summary>
    List<StockBox> StockBoxes(World world, Meitou.Data.World.TownBuilding home)
    {
        var boxes = new List<StockBox>();

        foreach (var f in home.Fixtures)
        {
            if (!f.HoldsShopStock || world.Fixtures.Find(f.Id) is not { Contents: not null } box) continue;

            var record = _data.Db.Find(box.RecordId);
            boxes.Add(new StockBox(box, record is null ? null : Data.Gameplay.Construction.MachineData.From(_data.Db, record)));
        }

        return boxes;
    }

    /// <summary>
    /// Every entry of the vendor lists the template stocks from (<see cref="ShopFiller.VendorLinks"/>).
    /// </summary>
    List<VendorEntry> VendorEntries(SquadTemplate template, Data.GameRecord? faction)
    {
        var entries = new List<VendorEntry>();

        foreach (var link in ShopFiller.VendorLinks(template, faction))
        {
            if (_data.Db.Find(link.Id) is { Type: Data.Fcs.FcsRecordType.VendorList } record)
                entries.AddRange(VendorList.From(_data.Db, record).Entries);
        }

        return entries;
    }

    /// <summary>
    /// Fills the shop again (the specials on its first fill), sets the squad's purse, relocks a building shop's containers that hold stock and sets
    /// the next restock tick.
    /// </summary>
    void Refill(World world, Squad squad, Shop shop, SquadTemplate template)
    {
        squad.Money = ShopFiller.Fill(shop, template, Market(world), _data, world.Seed, (ulong)squad.Id, specials: shop.Fills == 0);

        // The building filler unlocks the stock containers it fills (FUN_14095b210), then locks each that holds something (FUN_1409565e0, #328).
        if (shop.Building is not null && squad.HomeBuilding is { } home)
        {
            foreach (var f in home.Fixtures)
            {
                if (f.HoldsShopStock && world.Fixtures.Find(f.Id)?.Lock is { } l)
                    l.Locked = shop.RegionOf(f.Id, out var region) && shop.Stock.Items.Any(region.Holds);
            }
        }

        shop.NextRestockTick = world.Tick + (long)(template.VendorsRefreshTime / BodySystem.HoursPerTick);
    }

    /// <summary>
    /// The shop a character trades for: its squad's (null: not a trader).
    /// </summary>
    public static Shop? ShopOf(World world, CharacterCold cold) => cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId)?.Shop : null;

    #endregion

    #region Trading parties

    /// <summary>
    /// The open trade's parties and the trader's shop; false when they cannot trade or the trader keeps no shop.
    /// </summary>
    bool Resolve(World world, out int c, out int t, out Shop? shop)
    {
        shop = null;
        if (!Parties(world, out c, out t)) return false;

        shop = ShopOf(world, world.Characters.Cold(t)!);
        return shop is not null;
    }

    /// <summary>
    /// Whether the customer and the trader can trade, whatever the trader stocks: the trader up, not asleep when the trade opens (a sleeper
    /// answers no Talk, <see cref="Dialogue.DialogueSystem.SleepsThrough"/>), not held in a cage (engine choice, #641), not fighting nor an enemy;
    /// the customer the player's with an inventory, alive, and awake to open a trade (an open one passes to a knocked-out squad member's window
    /// and stays with it, <see cref="Trades"/>).
    /// </summary>
    bool Parties(World world, out int c, out int t)
    {
        var table = world.Characters;
        t = -1;
        if (!table.TryResolveNext(_customer, out c) || !table.TryResolveNext(_trader, out t)) return false;

        var cc = table.Cold(c)!;
        var tc = table.Cold(t)!;
        bool customerDown = cc.Medical is { Dead: true } || (!_open && cc.Medical is { Incapacitated: true });
        if (!cc.IsPlayer || customerDown || tc.Medical is { Incapacitated: true } || tc.InCombat || tc.IsImprisoned || cc.Inventory is null)
            return false;
        if (!_open && Carrying.CarrySystem.Asleep(table, t)) return false;

        return !AggroSystem.IsEnemy(_data.Relations, cc.Faction, tc.Faction);
    }

    /// <summary>
    /// Whether the customer is near enough to open the trade: the talk range (<see cref="Dialogue.DialogueSystem.InTalkRange"/>), as the original
    /// opens a dialogue's trade where the conversation stands, with no distance test (<c>FUN_1407918a0</c>, docs/game/economy.md "Opening a trade").
    /// </summary>
    static bool InReach(World world, int c, int t) => Dialogue.DialogueSystem.InTalkRange(world.Characters, c, t);

    /// <summary>
    /// Whether <paramref name="c"/> and <paramref name="t"/> are within <paramref name="range"/> of each other in X/Z.
    /// </summary>
    static bool InReach(World world, int c, int t, float range)
    {
        var a = world.Characters.Next[c].Position;
        var b = world.Characters.Next[t].Position;
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return dx * dx + dz * dz <= range * range;
    }

    /// <summary>
    /// The item <paramref name="token"/> addresses in <paramref name="inventory"/>, when it still matches; a bag only as a single unit.
    /// </summary>
    static ItemInstance? Find(Inventory inventory, InventoryItemToken token)
    {
        var item = inventory.All().ElementAtOrDefault(token.Index);
        return item is not null && item.Record == token.Record && item.Section == token.Section && item.X == token.X && item.Y == token.Y
            && item.Quantity == token.Quantity
            && item.Quality == token.Quality && (item.Contents is null || item.Quantity == 1) ? item : null;
    }

    #endregion

    #region The held item (HeldItems, Player/HeldItems.cs)

    /// <summary>
    /// The open trade's stock and its customer while the trade's windows show; null when no trade is open.
    /// </summary>
    internal Inventory? OpenStock(World world, out CharacterId buyer)
    {
        buyer = CharacterId.None;
        if (!_open || !Resolve(world, out _, out _, out var shop)) return null;

        buyer = _customer;
        return shop!.Stock;
    }

    /// <summary>
    /// How many of <paramref name="units"/> of a held item the payer can afford (the drop caps the quantity so, <c>FUN_140715560</c>); none: "You
    /// can't afford that." or "The shopkeeper can't afford that.".
    /// </summary>
    internal int AffordHeld(World world, ItemInstance item, int units, bool sell)
    {
        if (!Resolve(world, out int c, out int t, out var shop)) return 0;

        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
        int n = Affordable(Market(world), item, Math.Clamp(units, 0, item.Quantity), sell, shop!.Terms,
            sell ? world.Characters.Cold(c)!.Faction : squad.Faction,
            sell ? squad.Money : world.Player.Money);
        if (n == 0) world.Post(sell ? "The shopkeeper can't afford that." : "You can't afford that.");

        return n;
    }

    /// <summary>
    /// A held stolen item put on the stock of a shop that is no fence: the fence roll (8.4); caught, "Got caught selling stolen item." and the
    /// pending Fencing crime (true).
    /// </summary>
    internal bool CaughtSelling(World world, ItemInstance item)
    {
        if (!Resolve(world, out int c, out int t, out var shop)) return false;

        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;

        if (!item.IsStolenFor(world.Characters.Cold(c)!.Faction, _data) || !Fences(item, squad, shop!.Terms)
            || !Caught(world, c, t, item, squad, shop.Terms))
        {
            return Smuggling(world, c, item, shop!.Terms);
        }

        world.Post("Got caught selling stolen item.");

        if (Crimes is { } crimes
            && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, _trader, Crime.CrimeSystem.MinTimer, viaWrapper: true))
        {
            crimes.Book(world, c, squad.Faction, _trader);
        }

        return true;
    }

    /// <summary>
    /// The smuggling check of a sale (<c>FUN_140794e80</c>, <b>Observed</b>, run after the fence check): an item the culture of the trader's town's
    /// faction lists as illegal is refused by a trader that does not buy illegal goods, "Attempted smuggling!" (code 0xf), and the trader says
    /// THIEF_CAUGHT_STEALING_FROM_ME (8) at the seller unless it buys stolen goods. True when refused.
    /// </summary>
    bool Smuggling(World world, int seller, ItemInstance item, Trader terms)
    {
        if (terms.BuysIllegal || (uint)terms.Site >= (uint)_data.SiteFactions.Count
            || Market(world).FactionCulture(_data.SiteFactions[terms.Site]) is not { } culture || !culture.Illegal.Contains(item.Record))
        {
            return false;
        }

        world.Post("Attempted smuggling!");
        if (!terms.BuysStolen)
            world.Defer(new Dialogue.DialogueEvent(_trader, world.Characters.IdOf(seller), 8) { Tick = world.Tick + 1 });

        return true;
    }

    /// <summary>
    /// Pays for the first <paramref name="units"/> of a held item that changed hands (priced before it moves, a buyback at its receipt's coins): the
    /// price goes from the buyer's purse to the seller's. Returns the moved units' receipt (null when this undid an earlier trade of this window).
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

        if (sell)
        {
            squad.Money -= price;
            world.Player.Money += price;
        }
        else
        {
            world.Player.Money -= price;
            squad.Money += price;
        }

        world.Post(Loc.Tr(sell ? "{1} sold {2} for c.{3}" : "{1} bought {2} for c.{3}", cold.Name, Units(units, item.Name), price));
        return receipt;
    }

    #endregion

    #region Buying and selling

    /// <summary>
    /// A bag trades only empty (engine choice; its contents would go unpriced and unchecked for theft).
    /// </summary>
    internal const string BagNotEmpty = "That bag isn't empty.";

    /// <summary>
    /// Buys the units of the stock item into the customer's (or the receiving character's) inventory, as many as are affordable and fit.
    /// </summary>
    void Buy(World world, BuyItem buy)
    {
        if (!Resolve(world, out int c, out int t, out var shop)) return;

        var cold = world.Characters.Cold(c)!;
        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
        if (Find(shop!.Stock, buy.Item) is not { } item) return;

        if (item.Contents is { Items.Count: > 0 })
        {
            world.Post(BagNotEmpty);
            return;
        }

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
            if (placed > 0 && InventoryCommands.IsEquipmentSection(item.Section)) _assembly.RefreshEquipment(cold);
        }
        else if (buy.Section == "backpack_attach") placed = 0;
        else if (buy.Section is { } section) placed = PlaceAt(cold.Inventory!, bought, units, section, buy.X, buy.Y);
        else if (buy.Equip && InventoryCommands.TryEquip(cold.Inventory!, bought.WithQuantity(1), cold))
        {
            // One unit is worn; the rest of a stack goes where it fits.
            _assembly.RefreshEquipment(cold);
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
        world.Post(Loc.Tr("{1} bought {2} for c.{3}", cold.Name, Units(placed, item.Name), price));
    }

    /// <summary>
    /// Sells the units of the customer's (or the giving character's) item to the shop, as many as the shop can afford, after the fence and
    /// smuggling tests.
    /// </summary>
    void Sell(World world, SellItem sell)
    {
        if (!Resolve(world, out int c, out int t, out var shop)) return;

        var cold = world.Characters.Cold(c)!;
        var tc = world.Characters.Cold(t)!;
        var squad = world.Squads.Find(tc.SquadId)!;
        if (Find(cold.Inventory!, sell.Item) is not { } item || !InventoryCommands.IsMovableSection(item.Section))
            return;

        if (item.LockedOn)
        {
            world.Post("This item is locked in place and cannot be removed.");
            return;
        }

        if (item.Contents is { Items.Count: > 0 })
        {
            world.Post(BagNotEmpty);
            return;
        }

        var m = Market(world);
        int units = Affordable(m, item, Math.Clamp(sell.Quantity, 1, item.Quantity), true, shop!.Terms, cold.Faction, squad.Money);

        if (units == 0)
        {
            world.Post("The shopkeeper can't afford that.");
            return;
        }

        if (item.IsStolenFor(cold.Faction, _data) && Fences(item, squad, shop.Terms) && Caught(world, c, t, item, squad, shop.Terms))
        {
            world.Post("Got caught selling stolen item.");

            if (Crimes is { } crimes
                && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, _trader, Crime.CrimeSystem.MinTimer, viaWrapper: true))
            {
                crimes.Book(world, c, squad.Faction, _trader);
            }

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
        if (equipped) _assembly.RefreshEquipment(cold);

        squad.Money -= price;
        world.Player.Money += price;
        world.Post(Loc.Tr("{1} sold {2} for c.{3}", cold.Name, Units(placed, item.Name), price));
    }

    #endregion

    #region Fencing

    /// <summary>
    /// The odds <c>FUN_140793490</c> answers for goods nobody looks for (<c>DAT_14169bb44</c>, <b>Verified</b>: 1.1).
    /// </summary>
    const float NeverCaught = 1.1f;

    /// <summary>
    /// The chance a shop that is no fence lets a stolen item through (8.4, <c>FUN_140793490</c>): lerp(thievery / 100, 0.4, 1.25) of the seller,
    /// halved when the item was stolen from the shop's faction and halved again when it was stolen from the shop's own squad; 1 or more never gets
    /// caught. The roll is the seller's and the tick's.
    /// </summary>
    public static float FenceChance(float thievery, bool stolenFromShopFaction, bool stolenFromShopSquad = false)
    {
        float p = 0.4f + (1.25f - 0.4f) * (thievery / 100f);
        if (stolenFromShopFaction) p *= 0.5f;

        return stolenFromShopSquad ? p * 0.5f : p;
    }

    /// <summary>
    /// Whether the shop's squad asks about and rolls on a stolen good at all (<c>FUN_140792830</c>, <b>Observed</b>): always, unless it buys stolen
    /// goods; a squad that does only for goods stolen from one of its own members (the item's owner handle names the squad's platoon).
    /// </summary>
    static bool Fences(ItemInstance item, Squad shopSquad, Trader terms) =>
        !terms.BuysStolen || item.OwnerSquad >= 0 && item.OwnerSquad == shopSquad.Id;

    /// <summary>
    /// The faction the shop's squad sits in regards another as its own (the game's <c>FUN_1406b2630</c>: the same faction, or a relation of 50 or
    /// more).
    /// </summary>
    bool Allied(int a, int b) => a == b || _data.Relations.Contains(a) && _data.Relations.Contains(b) && _data.Relations.IsAlly(a, b);

    /// <summary>
    /// The chance the trader (slot <paramref name="trader"/>) lets the seller's stolen item through (<c>FUN_140793490</c>, <b>Observed</b>). With the
    /// robbed character known (<see cref="ItemInstance.Owner"/>): 1 when it is gone or of the seller's own squad, and its faction now is the one
    /// tested. Of another faction than the shop's: 1 for a shop that buys stolen goods (<see cref="Fences"/>) and for a bandit or adventurer faction;
    /// 1.1 (never caught) when the shop's faction is not allied to it and the owner's squad's home town is not the town the trader stands in (an item
    /// with no known owner has no home town). Else <see cref="FenceChance"/>, halved for the shop's faction and again for the shop's own squad.
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
            if (!Fences(item, shopSquad, terms)
                || _data.Relations.Factions[from].FundamentalType is FundamentalType.Bandit or FundamentalType.Adventurer)
                return 1;
            if (!Allied(shopFaction, from) && home != AiSystem.TownAt(_data, world, table.Next[trader].Position))
                return NeverCaught;
        }

        return FenceChance(thievery, from == shopFaction, item.OwnerSquad >= 0 && item.OwnerSquad == shopSquad.Id);
    }

    /// <summary>
    /// Rolls the fence test of selling a stolen <paramref name="item"/> (<see cref="FenceOdds"/>) and trains the seller's thievery; true when the
    /// seller is caught.
    /// </summary>
    bool Caught(World world, int seller, int trader, ItemInstance item, Squad shopSquad, Trader terms)
    {
        var cold = world.Characters.Cold(seller)!;
        float p = FenceOdds(world, cold.Stats?[StatsEnumerated.Thieving] ?? 0, item, seller, trader, shopSquad, terms);
        bool caught = p < 1 && Rng.Float(Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(seller)), RngPurpose.Trade, (ulong)world.Tick)) >= p;

        // The seller trains thievery whenever the odds are below 0.98 (<c>FUN_140793770</c>): 5 x 2 for a fence that works, 20 x 2 for one that is
        // caught.
        if (p < Crime.CrimeSystem.FenceXpBelow)
            Crimes?.TrainThievery(world, seller, caught ? Crime.CrimeSystem.FenceCaughtXp : Crime.CrimeSystem.FenceXp);

        return caught;
    }

    #endregion

    #region Prices and placing

    /// <summary>
    /// How many of <paramref name="wanted"/> units the payer's <paramref name="money"/> covers (the trade window splits the stack to what is
    /// affordable).
    /// </summary>
    int Affordable(Market m, ItemInstance item, int wanted, bool sell, Trader terms, int holderFaction, long money)
    {
        for (int n = wanted; n > 0; n--)
            if (Price(m, item, n, sell, terms, holderFaction) <= money) return n;

        return 0;
    }

    /// <summary>
    /// Containers move whole: stack copies intentionally omit Contents. Transfer validates the destination before changing either inventory.
    /// </summary>
    static int TransferContainer(Inventory from, InventoryItemToken token, Inventory into, string? section, int x, int y, bool equip,
        CharacterCold? wearer)
    {
        if (section is not (null or "main" or "backpack_attach")) return 0;
        if (section == "backpack_attach")
            return InventoryCommands.Transfer(from, token, into, section, 0, 0, wearer: wearer) ? 1 : 0;
        if (equip && InventoryCommands.Transfer(from, token, into, "backpack_attach", 0, 0, wearer: wearer)) return 1;
        if (section == "main" && InventoryCommands.Transfer(from, token, into, section, x, y)) return 1;

        var item = Find(from, token);
        return item is not null && into.FindCell("main", item.Width, item.Height, out int cx, out int cy)
            && InventoryCommands.Transfer(from, token, into, "main", cx, cy) ? 1 : 0;
    }

    /// <summary>
    /// Whether the item's receipt is of the open trade and the opposite direction (<paramref name="sell"/>): it trades back at its own coins.
    /// </summary>
    bool IsBuyback(ItemInstance item, bool sell) => item.Buyback is { } r && ReferenceEquals(r.Session, _session) && r.Sell == sell;

    /// <summary>
    /// The price of <paramref name="units"/> units: a buy-back's coins, else the market's stack price.
    /// </summary>
    long Price(Market market, ItemInstance item, int units, bool sell, Trader terms, int holder) => IsBuyback(item, sell)
        ? item.Buyback!.Price(units) : market.StackPrice(item.WithQuantity(units), sell, terms, holder);

    /// <summary>
    /// The receipt of the units' coins, each unit's share of the stack price; null for a buy-back.
    /// </summary>
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

        return new(_session, !sell, coins);
    }

    /// <summary>
    /// Puts the units at the cell they were dropped on (the trade window's <c>placeItemFromMouse</c>): the cell of <paramref name="section"/> in
    /// <paramref name="root"/> (the backpack's contents for <c>backpack_content</c>) when the footprint is inside the grid, free and the stack within
    /// the limit; otherwise wherever they fit, as <see cref="Place"/>.
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

    /// <summary>
    /// Puts the units wherever they fit in <paramref name="into"/>'s main grid; the units placed.
    /// </summary>
    internal static int Place(Inventory into, ItemInstance copy, int units)
    {
        copy.Section = "main";
        return into.Add(copy) ? units : units - copy.Quantity;
    }

    /// <summary>
    /// Takes <paramref name="units"/> off an item's stack, removing it when none are left.
    /// </summary>
    internal static void Take(Inventory from, ItemInstance item, int units)
    {
        if (units >= item.Quantity) from.Remove(item);
        else item.Quantity -= units;
    }

    /// <summary>
    /// "N x name", or the name alone for one.
    /// </summary>
    static string Units(int n, string name) => n == 1 ? name : $"{n} x {name}";

    #endregion

    #region The trade view

    /// <summary>
    /// The open trade for the UI: the stock, and the goods of the customer and of every other player character who could take the trade over
    /// (<see cref="Sellers"/>), each priced for its seller, so any of their windows shows sell prices and asks before fencing.
    /// </summary>
    TradeView Publish(World world, int c, int t, Shop shop)
    {
        var m = Market(world);
        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;

        TradeItemView View(ItemInstance item, int index, bool sell, int seller)
        {
            int holder = sell ? world.Characters.Cold(seller)!.Faction : squad.Faction;
            long value = m.UnitPrice(item, false, shop.Terms, holder), sellValue = m.UnitPrice(item, true, shop.Terms, holder);
            bool buyback = IsBuyback(item, sell);

            if (buyback)
            {
                if (sell) sellValue = Price(m, item, 1, true, shop.Terms, holder);
                else value = Price(m, item, 1, false, shop.Terms, holder);
            }

            bool stolen = item.IsStolenFor(holder, _data);

            // The roll's own odds feed the hover box, the sale prompt and the roll alike; only where the shop would roll on the item at all.
            float thievery = world.Characters.Cold(seller)!.Stats?[StatsEnumerated.Thieving] ?? 0;
            float? odds = sell && stolen && Fences(item, squad, shop.Terms) ? FenceOdds(world, thievery, item, seller, t, squad, shop.Terms) : null;
            return new(index, item.Record, item.Name, item.Section, item.X, item.Y, item.Width, item.Height, item.Quantity,
                item.Quality, Price(m, item, item.Quantity, sell, shop.Terms, holder), Price(m, item, 1, sell, shop.Terms, holder), stolen)
            {
                Buyback = buyback,
                Container = item.Contents is not null,
                BagItems = item.Contents?.Items.Count ?? -1,
                Material = item.MaterialId,
                Rotated = item.Rotated,
                Colour = item.ColourId,
                Charge = item.ChargeFraction,
                Value = value,
                SellValue = sellValue,
                Weight = item.Weight,
                PriceFactor = m.TraderFactor(item, shop.Terms, holder),
                MarketFactor = m.MarketFactor(shop.Terms.Site, item.Record),
                Company = item.CompanyId,
                Level = item.Level,
                Charges = item.Charges,
                MaxCharges = item.MaxCharges,
                StolenFrom = stolen ? item.StolenFrom : -1,
                Unique = item.Unique,
                FenceChance = odds is { } shown ? Math.Clamp(shown, 0, 1) : null,
                FenceOdds = odds is < 1 ? Math.Max(odds.Value, 0) : null,
                Illegal = sell && (uint)shop.Terms.Site < (uint)_data.SiteFactions.Count
                    && m.FactionCulture(_data.SiteFactions[shop.Terms.Site]) is { } culture && culture.Illegal.Contains(item.Record),
            };
        }

        var stock = StockViews(shop, item => View(item.Item, item.Index, false, c));
        var goodsOf = new Dictionary<CharacterId, IReadOnlyList<TradeItemView>>();
        foreach (int seller in Sellers(world, c, t))
            goodsOf[world.Characters.IdOf(seller)] = Goods(world.Characters.Cold(seller)!, item => View(item.Item, item.Index, true, seller));

        var held = HeldView(world, goodsOf, (item, sell, seller) => View(item, -1, sell, seller), c);
        string name = world.Characters.Cold(t)!.Name;
        return new TradeView(_customer, _trader, name, squad.Money, world.Player.Money, shop.Stock.Width, shop.Stock.Height, stock,
            goodsOf[_customer], shop.Terms.BuysStolen, shop.Terms.BuysIllegal)
        {
            Held = held,
            GoodsOf = goodsOf,
            Unused = shop.UnusedCells,
        };
    }

    /// <summary>
    /// Only the stock grid's own items, at their All() indices (which tokens use): a bag's contents are not loose stock.
    /// </summary>
    static List<TradeItemView> StockViews(Shop shop, Func<(ItemInstance Item, int Index), TradeItemView> view)
    {
        var stock = new List<TradeItemView>(shop.Stock.Items.Count);
        int at = 0;

        foreach (var item in shop.Stock.Items)
        {
            stock.Add(view((item, at)));
            at += 1 + (item.Contents?.All().Count() ?? 0);
        }

        return stock;
    }

    /// <summary>
    /// The views of the seller's items in movable sections, with their inventory index.
    /// </summary>
    static TradeItemView[] Goods(CharacterCold seller, Func<(ItemInstance Item, int Index), TradeItemView> view) =>
        seller.Inventory!
            .All()
            .Select((item, index) => (item, index))
            .Where(p => InventoryCommands.IsMovableSection(p.item.Section))
            .Select(view)
            .ToArray();

    /// <summary>
    /// The customer, then the player's other characters who could take the trade over: able to trade and within <see cref="TradeSettings.PassRange"/>
    /// of the trader.
    /// </summary>
    IEnumerable<int> Sellers(World world, int c, int t)
    {
        yield return c;

        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction < 0 || squad.Faction != world.Player.Faction && squad.Faction != _data.PlayerFaction)
                continue;

            foreach (var id in squad.Members)
            {
                if (id != _customer && world.Characters.TryResolveNext(id, out int s) && Trades(world.Characters.Cold(s))
                    && InReach(world, s, t, Settings.PassRange))
                {
                    yield return s;
                }
            }
        }
    }

    /// <summary>
    /// The held item priced as stock (lifted from the stock) or as its seller's goods (lifted from a seller's window, <see cref="Sellers"/>); null
    /// otherwise.
    /// </summary>
    static TradeItemView? HeldView(World world, Dictionary<CharacterId, IReadOnlyList<TradeItemView>> goodsOf,
        Func<ItemInstance, bool, int, TradeItemView> view, int c) =>
        world.Player.Held switch
        {
            { From.Holder: ItemHolder.Stock } h => view(h.Item, false, c),
            { From.Holder: ItemHolder.Character } h when goodsOf.ContainsKey(h.From.Character)
                && world.Characters.TryResolveNext(h.From.Character, out int s) => view(h.Item, true, s),
            _ => null,
        };

    #endregion
}
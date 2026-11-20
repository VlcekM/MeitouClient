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
/// A code mod gives a squad of the player's faction a shop (<paramref name="On"/>) or ends it (docs/modding.md "Shopkeepers of the player's faction";
/// Meitou only: the original's squad factory fills no shop for the player's faction). The shop trades only while the squad is independent and keeps
/// its own purse, which this command gives it when it has none. <paramref name="Template"/>: the string id of a SQUAD_TEMPLATE for its terms (buy
/// mult, stolen and illegal goods), filled and restocked from its vendor lists when it is a trader's (an id that names none refuses the command);
/// none: terms of no template, the stock the mod's own (<see cref="Shop.Stock"/>). <paramref name="Owner"/>: the mod's manifest id, so a load
/// without the mod ends the shop (one that names none keeps the noted one; another mod's shop is left alone by a command naming that other mod).
/// </summary>
public sealed record SetSquadShop(int Squad, bool On, string? Template = null, string? Owner = null) : SimCommand;
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
    /// With <see cref="Equip"/>: the receiving character's bag window is open, so the units go into the worn bag first, then as
    /// <see cref="Equip"/> says (the right click's target window is the bag's before the character's, <c>FUN_140714a20</c> -> <c>FUN_14070dcd0</c>).
    /// </summary>
    public bool IntoBag { get; init; }
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
    /// The name of the character a stolen item was taken from, "" when that character is not in the world, null when the item is not stolen or its
    /// mark names no character (as <see cref="InventoryItemView.StolenOwner"/>).
    /// </summary>
    public string? StolenOwner { get; init; }
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
    /// How close a player's character must stand to the trader for the open trade's items to move through its window (and for another to take the
    /// trade over): the original's item move between two characters' inventories allows 1000 when unflagged, and a paid trade's move is unflagged
    /// (<c>FUN_14070d290</c>, the flag being the stealing test; docs/game/economy.md "Trading range"). The trade itself stays open farther.
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
/// refresh time</c> game hours (9.3). Squads of the player's faction get one only from a code mod (<see cref="SetSquadShop"/>, Meitou only:
/// a faction shop, a squad shop that trades with the player's money against the squad's own purse, which no fill sets). A housed one gets a
/// building shop when its home has stock containers (<see cref="Shop.ForBuilding"/>).</item>
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
/// A squad shop's stock and every shop's restock time are in the state hash (through the squads; a building shop's goods through its containers).
/// The open trade is not, but the buyback receipts it leaves on items are, and they are not saved: a save made mid-trade reloads with a different
/// hash (docs/simulation.md, trading).
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
    /// Whether the trade window is open (the customer reached the trader).
    /// </summary>
    bool _open;
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
                            world.Defer(new Combat.AttackOrder([_customer], _trader)
                            {
                                Tick = world.Tick + 1, Cause = "an order to trade with a hostile trader",
                            });
                        }

                        Close(world);
                        break;
                    }

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
                    if (Resolve(world, out _, out _, out var arranged)) InventoryCommands.Arrange(arranged!.Window);
                    break;
                case SetSquadShop given:
                    SetShop(world, given);
                    break;
            }
        }

        WriteOpenView(world);
    }

    /// <summary>
    /// Every tick the homes a player character shows (<see cref="MarkShownHomes"/>); every <see cref="TradeSettings.CheckEveryTicks"/> the wages and
    /// the shops; then the open trade follows the selection and lasts while its parties are there (<see cref="Parties"/>), and the customer's walk
    /// opens the trade in talk range (<see cref="Approach"/>).
    /// </summary>
    public void SlowWorld(World world)
    {
        MarkShownHomes(world);

        if (world.Tick % Settings.CheckEveryTicks == 0)
        {
            // The wages first: a new squad's members are paid as they are made (Character::giveBirth), before its shop's first fill sets the
            // purse (FUN_14095b210), so a trader with a waged guard starts with the fill's purse, not the guard's wages (#659).
            _wages.Pay(world);
            Stock(world);
        }

        if (_open) FollowSelection(world);

        // The original closes an open trade by no distance: farther than PassRange only its items stop moving (docs/game/economy.md "Trading range").
        if (_open && !Resolve(world, out _, out _, out _)) Close(world);
        else if (!_open && !_customer.IsNone) Approach(world);

        WriteOpenView(world);
        RefreshView(world);
    }

    /// <summary>
    /// The customer's walk to the trader, who goes on with what it does (the original holds no trader for a trade): in talk range the trade opens;
    /// it closes when either party cannot trade or the walk is given up (another order, or stuck).
    /// </summary>
    void Approach(World world)
    {
        if (!Resolve(world, out int c, out int t, out _))
        {
            Close(world);
            return;
        }

        if (InReach(world, c, t))
        {
            Open(world, c);
            return;
        }

        if (!_approach.Follow(world, _customer, c, t)) Close(world);
    }

    /// <summary>
    /// Opens the trade: the customer stops where it stands (not at the trader's spot behind a counter); the trader is not held, as the original
    /// holds none for a trade (the Trade effect <c>FUN_1409524f0</c> only queues the windows, docs/game/economy.md "Trading range").
    /// </summary>
    void Open(World world, int c)
    {
        TaskStart.Stand(world.Characters, c);
        BuildView(world);
        _open = true;
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
        if (FollowSelection(world)) return TradeWith(world, _customer);

        world.Post("Out of trading range.");
        return false;
    }

    /// <summary>
    /// Passes the open trade to the player's character <paramref name="who"/> whose window the stock is dropped on or taken from (the customer's
    /// own too), when within <see cref="TradeSettings.PassRange"/> of the trader; farther it posts "Out of trading range." (another who cannot
    /// trade, not the player's, dead or without an inventory, is refused silently), as the original refuses such a move beyond the item limit
    /// (<c>FUN_140712490</c>, docs/game/economy.md "Trading range").
    /// </summary>
    internal bool TradeWith(World world, CharacterId who)
    {
        if (!world.Characters.TryResolveNext(who, out int c) || !world.Characters.TryResolveNext(_trader, out int t)) return false;
        if (who != _customer && (!Trades(world.Characters.Cold(c)) || SameSquad(world, c, t))) return false;

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
    /// Whether the characters in slots <paramref name="a"/> and <paramref name="b"/> are of one squad: a faction shop's own members never trade
    /// with it.
    /// </summary>
    static bool SameSquad(World world, int a, int b) => world.Characters.Cold(a)!.SquadId == world.Characters.Cold(b)!.SquadId;

    /// <summary>
    /// Rebuilds the published view of the open trade.
    /// </summary>
    internal void RefreshView(World world) =>
        _view = _open && Resolve(world, out int ci, out int ti, out var shop) ? Publish(world, ci, ti, shop!) : null;

    /// <inheritdoc/>
    public TradeView? PublishTrade() => _view;

    /// <summary>
    /// Ends the open trade with every inventory window (<see cref="World.CloseInventories"/>); a customer walking up to a trader goes on.
    /// </summary>
    internal void EndTrade(World world)
    {
        if (!_open) return;

        WriteOpenView(world);
        Close(world);
    }

    /// <summary>
    /// Closes the open trade: its session ends and the view goes.
    /// </summary>
    void Close(World world)
    {
        _session.Active = false;
        _session = new();

        _customer = _trader = CharacterId.None;
        _open = false;
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
    /// The buildings the player's characters stand in or on at this check (<see cref="MarkShownHomes"/>), kept to spare an allocation.
    /// </summary>
    readonly HashSet<string> _playerBuildings = new(StringComparer.Ordinal);

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
            if (PlayersSquad(world, squad))
            {
                StockFactionShop(world, squad);
                continue;
            }

            bool living = Living(world, squad);
            if (squad.Shop is { } open) SettleView(world, open);

            if (squad.Shop is { Building: not null } && !living)
            {
                // Nobody of the squad is left alive: its containers keep the goods, as the original's do (artifacts included).
                squad.Shop = null;
                continue;
            }

            if (squad.Shop is { Building: not null } && TemplateOf(squad.TemplateId) is null && !TradesWith(world, squad))
            {
                // A housed squad that keeps no shop traded from its home's containers (StockFor) only for that trade; the house filling goes on.
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
    bool PlayersSquad(World world, Squad squad) => PlayersFaction(world, squad.Faction);

    /// <summary>
    /// Whether <paramref name="faction"/> is the player's (the world's or the data's).
    /// </summary>
    bool PlayersFaction(World world, int faction) => faction >= 0 && (faction == world.Player.Faction || faction == _data.PlayerFaction);

    /// <summary>
    /// Marks the squads whose standing home building a player character is in or on (<see cref="Squad.HomeInteriorShown"/>): the original's interior
    /// view (<c>FUN_1407f7230</c>, every frame over the player's characters, <c>PlayerInterface</c> <c>+0x2b0</c>) builds the interior parts of each
    /// building such a character's location (slot 59) names (<c>FUN_140562540</c>, <c>FUN_140561d10</c>), and they stay until the building unloads.
    /// Every tick, as the original's every frame.
    /// </summary>
    void MarkShownHomes(World world)
    {
        PlayerBuildings(world);
        if (_playerBuildings.Count == 0) return;

        foreach (var squad in world.Squads.All)
        {
            if (squad.HomeInteriorShown || squad.HomeBuilding is not { } home || !_playerBuildings.Contains(home.Key)) continue;

            if (!world.Fixtures.IsDestroyed(home)) squad.HomeInteriorShown = true;
        }
    }

    /// <summary>
    /// Gathers into <see cref="_playerBuildings"/> the building each living player character stands in or on (<see cref="Perception.SightMap.LocationOf"/>).
    /// </summary>
    void PlayerBuildings(World world)
    {
        _playerBuildings.Clear();
        var table = world.Characters;

        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { IsPlayer: true }) continue;

            int place = world.Sight.LocationOf(s);
            if (place >= 0) _playerBuildings.Add(world.Sight.PlaceKey(place));
        }
    }

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
    /// loads, <see cref="Items.PackLoad"/>); a housed one from its home's stock containers for that trade (<see cref="Stock"/> drops the shop after
    /// it), none without.
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
        if (squad.Shop is { Building: null } carried) TakeBags(world, squad, carried);
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
    /// A building shop whose squad is dropped, made afresh or all dead writes what its trader window changed into its containers
    /// (<see cref="SettleView"/>), which keep the goods for the next shop, as the original's containers keep them.
    /// </summary>
    internal static void Settle(World world, Shop shop) => world.SystemOf<TradeSystem>()?.SettleView(world, shop);

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
    /// the units are spread over them as the filler does (<see cref="HomeStock"/>) and what fits nowhere is lost. Engine choice: again every <c>vendors refresh time</c>
    /// hours, but only while the containers hold nothing but unique items (a loaded save keeps what they hold); the purse is not touched.
    /// </summary>
    void FillHome(World world, Squad squad, SquadTemplate template, Meitou.Data.World.TownBuilding home)
    {
        if (world.Tick < squad.NextHomeFillTick) return;

        // While the home's interior is shown the refill waits without moving its time, as the original's restock (FUN_1404ff5e0). The first fill,
        // with the specials, is the squad factory's when the squad is made (FUN_140583a10): a squad a save gave back comes with its time at 1
        // (SavedPopulation), having had it before that save.
        bool first = squad.NextHomeFillTick == 0;
        if (!first && squad.HomeInteriorShown) return;

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

        var usable = OpenBoxes(world, boxes, template, terms.Faction);
        if (usable.Count == 0) return;

        merged.Fills = (int)world.Tick;
        var fill = ShopFiller.Roll(merged, template, Market(world), _data, world.VendorTables, world.Seed, key, specials: first);
        PlaceFill(world, usable, fill, terms, key, merged.Fills);
        LockFilled(usable);
    }

    /// <summary>
    /// The building filler unlocks every stock container, then keeps those that can hold what the lists make (<c>FUN_14095b210</c>,
    /// <c>FUN_14095a5b0</c>); the others stay unlocked and untouched.
    /// </summary>
    List<StockBox> OpenBoxes(World world, List<StockBox> boxes, SquadTemplate template, int faction)
    {
        foreach (var box in boxes)
            if (box.Fixture?.Lock is { } l) l.Locked = false;

        return HomeStock.Usable(boxes, VendorEntries(world, template, faction));
    }

    /// <summary>
    /// The filler locks each container it used that holds something (<c>FUN_1409565e0</c>, #328).
    /// </summary>
    static void LockFilled(List<StockBox> usable)
    {
        foreach (var box in usable)
            if (box.Fixture?.Lock is { } l) l.Locked = box.Grid.Items.Count > 0;
    }

    /// <summary>
    /// The home's stock containers (<see cref="Meitou.Data.World.TownFixture.HoldsShopStock"/>) with their records' storage rules.
    /// </summary>
    List<StockBox> StockBoxes(World world, Meitou.Data.World.TownBuilding home)
    {
        var boxes = new List<StockBox>();

        foreach (var f in home.Fixtures)
            if (f.HoldsShopStock && world.Fixtures.Find(f.Id) is { Contents: not null } box) boxes.Add(BoxOf(box));

        return boxes;
    }

    /// <summary>
    /// A building shop's stock containers that are registered, in its order; <paramref name="all"/> false when some are not (their zone not loaded).
    /// A squad shop's goods are its one box.
    /// </summary>
    List<StockBox> BoxesOf(World world, Shop shop, out bool all)
    {
        all = true;
        if (shop.Building is null) return [new StockBox(shop.Stock)];

        var boxes = new List<StockBox>(shop.Containers.Count);

        foreach (var id in shop.Containers)
            if (world.Fixtures.Find(id) is { Contents: not null } box) boxes.Add(BoxOf(box));

        all = boxes.Count == shop.Containers.Count;
        return boxes;
    }

    /// <summary>
    /// A stock container with its record's storage rules.
    /// </summary>
    StockBox BoxOf(Locks.Fixture box)
    {
        var record = _data.Db.Find(box.RecordId);
        return new StockBox(box, record is null ? null : Data.Gameplay.Construction.MachineData.From(_data.Db, record));
    }

    /// <summary>
    /// Every entry of the tables of the vendor lists the template stocks from (<see cref="ShopFiller.VendorLinks"/>): the usable-container test
    /// fetches them from the session's list cache as the draw does (<c>FUN_14095a5b0</c> -> <c>FUN_140959d30</c> -> <c>FUN_1409592a0</c>,
    /// <b>Verified</b>, decompile).
    /// </summary>
    List<VendorEntry> VendorEntries(World world, SquadTemplate template, int faction)
    {
        var entries = new List<VendorEntry>();

        foreach (var link in ShopFiller.VendorLinks(template, FactionRecord(faction)))
        {
            if (_data.Db.Find(link.Id) is { Type: Data.Fcs.FcsRecordType.VendorList } record)
                entries.AddRange(world.VendorTables.Fetch(_data, VendorList.From(_data.Db, record), faction).Entries);
        }

        return entries;
    }

    /// <summary>
    /// Fills the shop again (the specials on its first fill), sets the squad's purse unless the fill made no unit, fills a building shop's
    /// containers (<see cref="RefillBuilding"/>), and sets the next restock tick. A building shop none of whose containers is registered waits: the
    /// original's filler returns while the building or its interior is missing and is called again (docs/game/economy.md "Building shops").
    /// </summary>
    void Refill(World world, Squad squad, Shop shop, SquadTemplate template)
    {
        bool done = shop.Building is not null ? RefillBuilding(world, squad, shop, template) : RefillSquad(world, squad, shop, template);
        if (!done) return;

        shop.NextRestockTick = world.Tick + (long)(template.VendorsRefreshTime / BodySystem.HoursPerTick);
    }

    /// <summary>
    /// A squad shop's fill (<c>FUN_14095ab10</c>, <see cref="ShopFiller.StockSquad"/>), its window built again when the trade shows it; false (it
    /// waits) while an item lifted from the window is held. A faction shop's purse is left as it is, and its stock keeps no money item.
    /// </summary>
    bool RefillSquad(World world, Squad squad, Shop shop, SquadTemplate template)
    {
        if (shop.View.Live && world.Player.Held?.From.Holder == ItemHolder.Stock) return false;

        var fill = ShopFiller.Roll(shop, template, Market(world), _data, world.VendorTables, world.Seed, (ulong)squad.Id, specials: shop.Fills == 0);
        var purse = ShopFiller.StockSquad(shop, fill, Market(world));

        // A faction shop's purse holds only what trades and its mod put in (Meitou: no fill makes money for the player's faction), and the money
        // stacks a fill places (never shown in the window) are taken out, so none reaches a player character when the shop ends.
        if (purse is { } set && !PlayersSquad(world, squad)) squad.Money = set;
        if (PlayersSquad(world, squad)) shop.Stock.Items.RemoveAll(i => i.ItemFunction == PlayerSystem.MoneyFunction);

        ReopenView(world, shop);
        return true;
    }

    /// <summary>
    /// A building shop's fill (<c>FUN_14095b210</c>): every container unlocked; with none that can hold what the lists make nothing else happens (no
    /// stock, purse or specials; the restock time still moves on, <c>FUN_1404ff5e0</c>); else the fill is placed in the usable ones
    /// (<see cref="PlaceFill"/>), the purse set from what was placed, and those holding something are locked again. False while it waits
    /// (<see cref="Refill"/>).
    /// </summary>
    bool RefillBuilding(World world, Squad squad, Shop shop, SquadTemplate template)
    {
        // Not while an item lifted from the window is held (it goes back to the grid it came from), nor before a save's goods are handed over.
        bool held = shop.View.Live && world.Player.Held?.From.Holder == ItemHolder.Stock;
        var boxes = BoxesOf(world, shop, out _);
        if (squad.HomeBuilding is null || boxes.Count == 0 || held || shop.View.Pending) return false;

        // A restock (not the first fill) waits, its time unmoved, while a player character has shown the home's interior (FUN_1404ff5e0
        // returns while the building's BuildingInterior +0x10 is set).
        if (shop.Fills > 0 && squad.HomeInteriorShown) return false;

        var usable = OpenBoxes(world, boxes, template, shop.Terms.Faction);
        if (usable.Count == 0)
        {
            shop.Fills++;   // the first fill, and with it the specials, is spent
            return true;
        }

        var fill = ShopFiller.Roll(shop, template, Market(world), _data, world.VendorTables, world.Seed, (ulong)squad.Id, specials: shop.Fills == 0);
        if (PlaceFill(world, usable, fill, shop.Terms, (ulong)squad.Id, shop.Fills) is { } value) squad.Money = ShopFiller.Purse(fill, value);

        LockFilled(usable);
        ReopenView(world, shop);
        return true;
    }

    /// <summary>
    /// Places a building fill as the building filler does (<c>FUN_14095b210</c>, <b>Verified</b>, decompile): the usable containers, and only
    /// those, cleared but their unique items (<c>FUN_14074a5b0</c>); each special item into the first that takes it of a fresh shuffle of them
    /// (<c>FUN_14096ac50</c>); then the units spread over them (<see cref="HomeStock.Spread"/>). What fits nowhere is lost. Returns the placed units'
    /// value at full price (the original sums the placed items' stack price, <c>vfunc 0x290</c>), or null when the fill made no unit.
    /// </summary>
    long? PlaceFill(World world, List<StockBox> usable, ShopFill fill, Trader terms, ulong key, int fills)
    {
        foreach (var box in usable) box.Grid.Items.RemoveAll(i => !i.Unique);

        var rng = new RandomStream(world.Seed, Rng.Mix(key ^ SpreadSalt ^ ((ulong)fills << 40)), RngPurpose.Trade);
        foreach (var special in fill.Specials) HomeStock.PlaceShuffled(usable, special, ref rng);
        if (fill.Units.Count == 0) return null;

        var market = Market(world);
        var placed = HomeStock.Spread(usable, fill.Units, market.BaseValue, ref rng);
        return placed.Sum(i => market.StackPrice(i, false, terms, terms.Faction));
    }

    /// <summary>
    /// The shop a character trades for: its squad's (null: not a trader).
    /// </summary>
    public static Shop? ShopOf(World world, CharacterCold cold) => cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId)?.Shop : null;

    #endregion

    #region Shopkeepers of the player's faction (Meitou only)

    /// <summary>
    /// Whether a squad of the player's faction keeps a shop the player's characters can trade with (<see cref="SetSquadShop"/>, a faction shop): a
    /// code mod gave it one, the player does not lead it (<see cref="Squad.Independent"/>) and it keeps its own purse (<see cref="Squad.OwnPurse"/>),
    /// so what the player pays and is paid stays the faction's (docs/modding.md "Shopkeepers of the player's faction").
    /// </summary>
    public static bool FactionShopOpen(World world, Squad squad) =>
        squad is { Shop: not null, Independent: true, OwnPurse: true, Dead: false } && squad.Faction >= 0 && squad.Faction == world.Player.Faction;

    /// <summary>
    /// Whether the character in <paramref name="slot"/> offers Trade as a shopkeeper of the player's faction: a player character, alive, not held in
    /// a cage, not asleep and not fighting (a trade would not open, <see cref="CanOpen"/>), in a squad whose shop is open to the player
    /// (<see cref="FactionShopOpen"/>). Every such member offers it.
    /// </summary>
    public static bool KeepsFactionShop(World world, int slot) =>
        world.Characters.Cold(slot) is { IsPlayer: true, IsImprisoned: false, InCombat: false, SquadId: >= 0, Medical: not { Dead: true } } cold
        && !Carrying.CarrySystem.Asleep(world.Characters, slot) && world.Squads.Find(cold.SquadId) is { } squad && FactionShopOpen(world, squad);

    /// <summary>
    /// Gives a squad of the player's faction a shop or ends it (<see cref="SetSquadShop"/>), noting the mod that set it; another mod's shop is left
    /// alone (<see cref="Factions.SquadManagement.MayChange"/>). A template that names no SQUAD_TEMPLATE refuses the command, with a log line. A new
    /// shop is a squad shop with the template's terms (none: of no template), first filled when that template is a trader's with vendor lists, and
    /// the squad gets its own purse when it has none. A shop it already has keeps its template and stock; only a new owner is noted.
    /// </summary>
    void SetShop(World world, SetSquadShop command)
    {
        if (world.Squads.Find(command.Squad) is not { Dead: false } squad || squad.Faction < 0 || squad.Faction != world.Player.Faction) return;
        if (!Factions.SquadManagement.MayChange(squad.ShopOwner, command.Owner)) return;

        if (!command.On)
        {
            EndShop(world, squad);
            return;
        }

        string? id = command.Template is { Length: > 0 } named ? named : null;
        var template = id is not null ? TemplateRecord(_data, id) : null;
        if (id is not null && template is null && squad.Shop is null)
        {
            string from = command.Owner ?? "a mod";
            Console.WriteLine($"trade     SetSquadShop from {from}: '{id}' is no SQUAD_TEMPLATE string id; squad {squad.Id} gets no shop");
            return;
        }

        if (command.Owner is { Length: > 0 } owner) squad.ShopOwner = owner;
        GivePurse(squad);
        if (squad.Shop is not null) return;

        squad.ShopTemplate = id;
        squad.Shop = new Shop { Terms = TermsOf(squad, template) };
        if (id is not null && TemplateOf(id) is { } fills) Refill(world, squad, squad.Shop, fills);
    }

    /// <summary>
    /// A faction shop trades against its squad's own purse: a squad without one gets it here, empty and owned by no mod, so a load without the
    /// shop's mod leaves the money with the squad (only <see cref="Factions.SetSquadPurse"/> off moves it to the player's money).
    /// </summary>
    static void GivePurse(Squad squad)
    {
        if (squad.OwnPurse) return;

        squad.OwnPurse = true;
        squad.Money = 0;
    }

    /// <summary>
    /// A faction shop at each stock pass: what its window changed goes to its stock, and with a template that fills it restocks when due while a
    /// member lives, as a trader's squad shop (<see cref="Refill"/>), its purse untouched.
    /// </summary>
    void StockFactionShop(World world, Squad squad)
    {
        if (squad.Shop is not { } shop) return;

        SettleView(world, shop);
        if (world.Tick < shop.NextRestockTick || squad.ShopTemplate is not { } id || TemplateOf(id) is not { } template || !Living(world, squad))
            return;

        Refill(world, squad, shop, template);
    }

    /// <summary>
    /// Ends a faction shop: an item the player holds from its window goes back into the window first, an open trade with it closes once the window
    /// is written back, and its goods go to its members (<see cref="GiveBack"/>). The purse stays (<see cref="Factions.SetSquadPurse"/> ends it).
    /// </summary>
    void EndShop(World world, Squad squad)
    {
        if (squad.Shop is { } shop)
        {
            if (Shows(world, shop) && world.Player.Held?.From.Holder == ItemHolder.Stock) world.SystemOf<PlayerSystem>()?.ReturnHeldItem(world);
            SettleView(world, shop);
        }

        if (TradesWith(world, squad)) EndTrade(world);

        int dropped = GiveBack(world, squad);
        if (dropped > 0) world.Post(Loc.Tr("{1} item stack(s) of a closed shop were left on the ground.", dropped));
    }

    /// <summary>
    /// Takes a faction shop away: its goods go to the squad's members, else onto the ground (<see cref="HandOut"/>), and its owner and template are
    /// forgotten. Returns how many stacks went onto the ground.
    /// </summary>
    internal static int GiveBack(World world, Squad squad)
    {
        int dropped = squad.Shop is { } shop ? HandOut(world, squad, shop.Stock) : 0;

        squad.Shop = null;
        squad.ShopOwner = null;
        squad.ShopTemplate = null;
        return dropped;
    }

    /// <summary>
    /// The goods of an ended faction shop go into its squad's living members' inventories, the first member's first, where they fit (a live
    /// animal's shut grid takes none, <see cref="InventoryCommands.MainClosed"/>); what fits nowhere goes onto the ground ahead of the first living
    /// member, or with none where the squad was made (engine choice: nothing is destroyed). Returns how many stacks went onto the ground.
    /// </summary>
    static int HandOut(World world, Squad squad, Inventory stock)
    {
        int dropped = 0;

        foreach (var item in stock.Items)
        {
            item.Buyback = null;
            if (Deliver(world.Characters, squad, item)) continue;

            Drop(world, squad, item);
            dropped++;
        }

        stock.Items.Clear();
        return dropped;
    }

    /// <summary>
    /// Puts <paramref name="item"/> into the squad's living members' inventories; false when units are left over (they stay in the item).
    /// </summary>
    static bool Deliver(CharacterTable table, Squad squad, ItemInstance item)
    {
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int slot) || table.Cold(slot) is not { DiedTick: < 0, Inventory: { } into } cold) continue;

            item.Section = "main";
            if (into.Add(item, InventoryCommands.MainClosed(cold))) return true;
        }

        return false;
    }

    /// <summary>
    /// Puts <paramref name="item"/> on the ground ahead of the squad's first living member (<see cref="HeldItems.DropAhead"/>), else at the place
    /// the squad was made (<see cref="Squad.Position"/>).
    /// </summary>
    static void Drop(World world, Squad squad, ItemInstance item)
    {
        var table = world.Characters;

        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int slot) || table.Cold(slot) is not { DiedTick: < 0 }) continue;

            HeldItems.DropAhead(world, slot, item);
            return;
        }

        item.Section = "main";
        world.GroundItems.Drop(item, squad.Position, 0);
    }

    /// <summary>
    /// Ends every faction shop a code mod gave whose mod is not running now (<paramref name="running"/>: whether a mod id runs), its goods going to
    /// its members or the ground (<see cref="GiveBack"/>; <paramref name="dropped"/>: the stacks on the ground). Between ticks after a load, before
    /// the first. Returns how many.
    /// </summary>
    public static int DropUnownedShops(World world, Func<string, bool> running, out int dropped)
    {
        int closed = 0;
        dropped = 0;

        foreach (var squad in world.Squads.All)
        {
            if (squad.ShopOwner is not { } owner || running(owner)) continue;

            dropped += GiveBack(world, squad);
            closed++;
        }

        return closed;
    }

    #endregion

    #region The trader window (ShopView)

    /// <summary>
    /// The trade window opens: the shop's grid is built from its goods (a building shop's containers, a squad shop's stock).
    /// </summary>
    void BuildView(World world)
    {
        if (!Resolve(world, out _, out _, out var shop) || shop is null) return;

        SettleView(world, shop);
        if (!shop.View.Pending) shop.View.Build(shop, BoxesOf(world, shop, out _));
    }

    /// <summary>
    /// What the open trade changed in the shop's grid goes to its goods.
    /// </summary>
    void WriteOpenView(World world)
    {
        if (_open && Resolve(world, out _, out _, out var shop)) SettleView(world, shop!);
    }

    /// <summary>
    /// A shop's grid writes what changed into its goods: a building shop's registered containers (an older save's goods once they all are), and empties when no
    /// trade window shows it and no item lifted from a stock is held (the held item still goes back to it).
    /// </summary>
    internal void SettleView(World world, Shop shop)
    {
        if (!shop.View.Live) return;

        var boxes = BoxesOf(world, shop, out bool all);
        if (!all && shop.View.Pending) return;

        shop.View.Write(shop, boxes);
        if (!Shows(world, shop) && world.Player.Held?.From.Holder != ItemHolder.Stock) shop.View.Drop(shop);
    }

    /// <summary>
    /// A shop's restock: a trader window that shows it is built again, else it empties.
    /// </summary>
    void ReopenView(World world, Shop shop)
    {
        if (Shows(world, shop)) shop.View.Build(shop, BoxesOf(world, shop, out _));
        else shop.View.Drop(shop);
    }

    /// <summary>
    /// Whether the open trade window shows <paramref name="shop"/>.
    /// </summary>
    bool Shows(World world, Shop shop) => _open && Resolve(world, out _, out _, out var open) && ReferenceEquals(open, shop);

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
    /// Whether the customer and the trader can trade, whatever the trader stocks: both in the world, the customer the player's with an inventory;
    /// a trader of the player's faction only while its squad's shop is open to the player (<see cref="FactionShopOpen"/>) and the customer is not of
    /// its squad. An open trade needs nothing more: the original closes it for no distance, fight, knock-out or cage of either party
    /// (docs/game/economy.md "Trading range"). Opening one needs <see cref="CanOpen"/>.
    /// </summary>
    bool Parties(World world, out int c, out int t)
    {
        var table = world.Characters;
        t = -1;
        if (!table.TryResolveNext(_customer, out c) || !table.TryResolveNext(_trader, out t)) return false;

        var cc = table.Cold(c)!;
        if (!cc.IsPlayer || cc.Inventory is null) return false;

        var tc = table.Cold(t)!;
        if (tc.IsPlayer && (tc.SquadId == cc.SquadId || world.Squads.Find(tc.SquadId) is not { } own || !FactionShopOpen(world, own)))
            return false;

        return _open || CanOpen(table, cc, t);
    }

    /// <summary>
    /// Whether a trade may open: the customer alive and awake; the trader up, not asleep (a sleeper answers no Talk,
    /// <see cref="Dialogue.DialogueSystem.SleepsThrough"/>), not held in a cage (engine choice, #641), not fighting nor an enemy.
    /// </summary>
    bool CanOpen(CharacterTable table, CharacterCold cc, int t)
    {
        var tc = table.Cold(t)!;
        if (cc.Medical is { Dead: true } or { Incapacitated: true }) return false;
        if (tc.Medical is { Incapacitated: true } || tc.InCombat || tc.IsImprisoned || Carrying.CarrySystem.Asleep(table, t)) return false;

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
    /// The deal <see cref="PayHeld"/> paid for, told to the trade observers once the item lands (<see cref="ReportHeld"/>); null with none.
    /// </summary>
    HeldDeal? _heldDeal;

    /// <summary>
    /// The open trade's stock and its customer while the trade's windows show; null when no trade is open.
    /// </summary>
    internal Inventory? OpenStock(World world, out CharacterId buyer)
    {
        buyer = CharacterId.None;
        if (!_open || !Resolve(world, out _, out _, out var shop)) return null;

        buyer = _customer;
        return shop!.Window;
    }

    /// <summary>
    /// How many of <paramref name="units"/> of a held item the payer can afford (the drop caps the quantity so, <c>FUN_140715560</c>); none: "You
    /// can't afford that." or "The shopkeeper can't afford that.".
    /// </summary>
    internal int AffordHeld(World world, ItemInstance item, int units, bool sell)
    {
        if (!Resolve(world, out int c, out int t, out var shop)) return 0;

        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
        int n = Affordable(world, Market(world), item, Math.Clamp(units, 0, item.Quantity), sell, shop!.Terms,
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
        world.CloseInventories();   // result 9 closes every inventory window (FUN_140715560)

        if (Crimes is { } crimes
            && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, _trader, Crime.CrimeSystem.MinTimer, viaWrapper: true))
        {
            crimes.Book(world, c, squad.Faction, _trader);
        }

        return true;
    }

    /// <summary>
    /// Whether the record is illegal where the trader of <paramref name="terms"/> trades: the culture of its town's faction lists it; never for a
    /// trader of the player's faction or with no town (<c>FUN_140794e80</c> checks only a trader of a non-player faction with a town,
    /// <b>Observed</b>).
    /// </summary>
    bool IsIllegal(World world, Market m, Trader terms, string record) =>
        !PlayersFaction(world, terms.Faction) && (uint)terms.Site < (uint)_data.SiteFactions.Count
        && m.FactionCulture(_data.SiteFactions[terms.Site]) is { } culture && culture.Illegal.Contains(record);

    /// <summary>
    /// The smuggling check of a sale (<c>FUN_140794e80</c>, <b>Observed</b>, run after the fence check): an item the culture of the trader's town's
    /// faction lists as illegal is refused by a trader that does not buy illegal goods, "Attempted smuggling!" (code 0xf), and the trader says
    /// THIEF_CAUGHT_STEALING_FROM_ME (8) at the seller unless it buys stolen goods. True when refused.
    /// </summary>
    bool Smuggling(World world, int seller, ItemInstance item, Trader terms)
    {
        if (terms.BuysIllegal || !IsIllegal(world, Market(world), terms, item.Record)) return false;

        world.Post("Attempted smuggling!");
        world.CloseInventories();   // result 0xf closes every inventory window (FUN_140713d20, FUN_140715560)
        if (!terms.BuysStolen)
            world.Defer(new Dialogue.DialogueEvent(_trader, world.Characters.IdOf(seller), 8) { Tick = world.Tick + 1 });

        return true;
    }

    /// <summary>
    /// Pays for the first <paramref name="units"/> of a held item that changed hands (priced before it moves, a buyback at its receipt's coins): the
    /// price goes from the buyer's purse to the seller's. Returns the moved units' receipt (null when this undid an earlier trade of this window).
    /// The caller places the item, then calls <see cref="ReportHeld"/>.
    /// </summary>
    internal TradeReceipt? PayHeld(World world, ItemInstance item, int units, bool sell)
    {
        _heldDeal = null;
        if (units <= 0 || !Resolve(world, out int c, out int t, out var shop)) return null;

        var cold = world.Characters.Cold(c)!;
        var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
        var m = Market(world);
        int holder = sell ? cold.Faction : squad.Faction;
        bool buyback = IsBuyback(item, sell);
        var receipt = Receipt(world, m, item, units, sell, shop!.Terms, holder);
        long price = Price(world, m, item, units, sell, shop.Terms, holder);

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
        _heldDeal = world.TradeObservers.Active ? new HeldDeal(c, t, sell, item.Record, units, price, buyback) : null;
        return receipt;
    }

    /// <summary>
    /// Tells the trade observers of the deal <see cref="PayHeld"/> paid for, once the held item has landed; nothing when it paid none.
    /// </summary>
    internal void ReportHeld(World world)
    {
        if (_heldDeal is not { } d) return;

        _heldDeal = null;
        ReportDeal(world, d.Customer, d.Trader, d.Sell, d.Record, d.Units, d.Price, d.Buyback);
    }

    /// <summary>
    /// A held item's deal waiting for the item to land: the customer's and trader's slots, the direction, the item, the units, the price and whether
    /// it undid an earlier trade of the window.
    /// </summary>
    readonly record struct HeldDeal(int Customer, int Trader, bool Sell, string Record, int Units, long Price, bool Buyback);

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
        if (Find(shop!.Window, buy.Item) is not { } item) return;

        if (item.Contents is { Items.Count: > 0 })
        {
            world.Post(BagNotEmpty);
            return;
        }

        var m = Market(world);
        int units = Affordable(world, m, item, Math.Clamp(buy.Quantity, 1, item.Quantity), false, shop.Terms, squad.Faction, world.Player.Money);

        if (units == 0)
        {
            world.Post("You can't afford that.");
            return;
        }

        bool buyback = IsBuyback(item, false);
        var receipt = Receipt(world, m, item, units, false, shop.Terms, squad.Faction);
        var bought = item.WithQuantity(units);
        bought.Buyback = receipt;
        long price = Price(world, m, item, units, false, shop.Terms, squad.Faction);
        int placed = PutBought(buy, shop.Window, item, bought, units, receipt, cold);

        if (placed == 0)
        {
            world.Post("No room for that item.");
            return;
        }

        if (placed < units) price = Price(world, m, item, placed, false, shop.Terms, squad.Faction);
        if (item.Contents is null) Take(shop.Window, item, placed);

        world.Player.Money -= price;
        squad.Money += price;
        world.Post(Loc.Tr("{1} bought {2} for c.{3}", cold.Name, Units(placed, item.Name), price));
        ReportDeal(world, c, t, false, item.Record, placed, price, buyback);
    }

    /// <summary>
    /// Puts the bought units into the receiving character's inventory: at the cell dropped on, into the open bag window's bag (a right click), worn
    /// (a right click), else wherever they fit; the
    /// units placed. A live animal's shut grid takes nothing (<see cref="InventoryCommands.MainClosed"/>), only its worn bag does: stock
    /// right-clicked into a bagless dog's window is "No room for that item." in the original.
    /// </summary>
    int PutBought(BuyItem buy, Inventory stock, ItemInstance item, ItemInstance bought, int units, TradeReceipt? receipt, CharacterCold cold)
    {
        var into = cold.Inventory!;
        bool shut = InventoryCommands.MainClosed(cold);

        if (item.Contents is not null)
        {
            // An empty bag moves itself with its contents inventory; its receipt is replaced (none: this undid a sale).
            int moved = TransferContainer(stock, buy.Item, into, buy.Section, buy.X, buy.Y, buy.Equip, cold);
            if (moved > 0) item.Buyback = receipt;
            if (moved > 0 && InventoryCommands.IsEquipmentSection(item.Section)) _assembly.RefreshEquipment(cold);
            return moved;
        }

        if (buy.Section == "backpack_attach") return 0;
        if (buy.Section is { } section) return PlaceAt(into, bought, units, section, buy.X, buy.Y, shut);

        int inBag = buy.Equip && buy.IntoBag ? IntoWornBag(into, bought) : 0;
        if (inBag == units) return units;

        return inBag + PutRest(buy, into, bought, units - inBag, shut, cold);
    }

    /// <summary>
    /// Puts as many of the bought units as fit into the worn bag's grid (stacks of it first, then free cells, <see cref="Inventory.AddTo"/>); the
    /// units placed, the others staying in <paramref name="bought"/>.
    /// </summary>
    static int IntoWornBag(Inventory into, ItemInstance bought)
    {
        if (into.Backpack?.Contents is not { } bag) return 0;

        int units = bought.Quantity;
        return bag.AddTo("backpack_content", bought) ? units : units - bought.Quantity;
    }

    /// <summary>
    /// Puts the bought units not in the bag: with <see cref="BuyItem.Equip"/> one unit into the equipment slot that takes it when that is free,
    /// then wherever they fit; the units placed.
    /// </summary>
    int PutRest(BuyItem buy, Inventory into, ItemInstance bought, int units, bool shut, CharacterCold cold)
    {
        if (!buy.Equip || !InventoryCommands.TryEquip(into, bought.WithQuantity(1), cold)) return Place(into, bought, units, shut);

        // One unit is worn; the rest of a stack goes where it fits.
        _assembly.RefreshEquipment(cold);
        if (units == 1) return 1;

        var rest = bought.WithQuantity(units - 1);
        rest.Buyback = bought.Buyback?.Skip(1);
        return 1 + Place(into, rest, units - 1, shut);
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
        int units = Affordable(world, m, item, Math.Clamp(sell.Quantity, 1, item.Quantity), true, shop!.Terms, cold.Faction, squad.Money);

        if (units == 0)
        {
            world.Post("The shopkeeper can't afford that.");
            return;
        }

        if (item.IsStolenFor(cold.Faction, _data) && Fences(item, squad, shop.Terms) && Caught(world, c, t, item, squad, shop.Terms))
        {
            world.Post("Got caught selling stolen item.");
            world.CloseInventories();   // result 9 closes every inventory window (FUN_140715560)

            if (Crimes is { } crimes
                && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, _trader, Crime.CrimeSystem.MinTimer, viaWrapper: true))
            {
                crimes.Book(world, c, squad.Faction, _trader);
            }

            return;
        }

        if (Smuggling(world, c, item, shop.Terms)) return;

        bool buyback = IsBuyback(item, true);
        var receipt = Receipt(world, m, item, units, true, shop.Terms, cold.Faction);
        var sold = item.WithQuantity(units);
        sold.Buyback = receipt;
        long price = Price(world, m, item, units, true, shop.Terms, cold.Faction);
        bool equipped = InventoryCommands.IsEquipmentSection(item.Section);
        int placed = item.Contents is not null
            ? TransferContainer(cold.Inventory!, sell.Item, shop.Window, sell.X is not null ? "main" : null, sell.X ?? 0, sell.Y, false, null)
            : sell.X is { } sx ? PlaceAt(shop.Window, sold, units, "main", sx, sell.Y) : Place(shop.Window, sold, units);

        if (placed == 0)
        {
            world.Post("No room for that item.");
            return;
        }

        if (item.Contents is not null) item.Buyback = receipt;
        if (placed < units) price = Price(world, m, item, placed, true, shop.Terms, cold.Faction);
        if (item.Contents is null) Take(cold.Inventory!, item, placed);
        if (equipped) _assembly.RefreshEquipment(cold);

        squad.Money -= price;
        world.Player.Money += price;
        world.Post(Loc.Tr("{1} sold {2} for c.{3}", cold.Name, Units(placed, item.Name), price));
        ReportDeal(world, c, t, true, item.Record, placed, price, buyback);
    }

    /// <summary>
    /// Tells the trade observers of a deal of the window (<see cref="ITradeObserver"/>) between the customer <paramref name="c"/> and the trader
    /// <paramref name="t"/>, once its money and goods moved; the window's stock is the trader's shop, so no site.
    /// </summary>
    static void ReportDeal(World world, int c, int t, bool sell, string record, int units, long price, bool buyback)
    {
        if (!world.TradeObservers.Active) return;

        var customer = TradeObservers.Party(world, c);
        var trader = TradeObservers.Party(world, t);
        var kind = sell ? TradeSaleKind.WindowSell : TradeSaleKind.WindowBuy;
        TradeObservers.Report(world, kind, record, units, price, sell ? trader : customer, sell ? customer : trader, buyback: buyback);
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
    /// The chance the trader (slot <paramref name="trader"/>) lets the seller's stolen item through (<c>FUN_140793490</c>, <b>Observed</b>): 1 for a
    /// trader of the player's faction (a code mod's faction shop). With the robbed character known (<see cref="ItemInstance.Owner"/>): 1 when it is
    /// gone or of the seller's own squad, and its faction now is the one tested. Of another faction than the shop's: 1 for a shop that buys stolen
    /// goods (<see cref="Fences"/>) and for a bandit or adventurer faction;
    /// 1.1 (never caught) when the shop's faction is not allied to it and the owner's squad's home town is not the town the trader stands in (an item
    /// with no known owner has no home town). Else <see cref="FenceChance"/>, halved for the shop's faction and again for the shop's own squad.
    /// </summary>
    float FenceOdds(World world, float thievery, ItemInstance item, int seller, int trader, Squad shopSquad, Trader terms)
    {
        if (PlayersFaction(world, shopSquad.Faction)) return 1;   // a trader of the player's faction (faction +0x250) never catches a fence

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
    int Affordable(World world, Market m, ItemInstance item, int wanted, bool sell, Trader terms, int holderFaction, long money)
    {
        for (int n = wanted; n > 0; n--)
            if (Price(world, m, item, n, sell, terms, holderFaction) <= money) return n;

        return 0;
    }

    /// <summary>
    /// Containers move whole: stack copies intentionally omit Contents. Transfer validates the destination before changing either inventory. A live
    /// animal's shut grid (<see cref="InventoryCommands.MainClosed"/>) takes none, only its bag slot.
    /// </summary>
    static int TransferContainer(Inventory from, InventoryItemToken token, Inventory into, string? section, int x, int y, bool equip,
        CharacterCold? wearer)
    {
        if (section is not (null or "main" or "backpack_attach")) return 0;
        if (section == "backpack_attach")
            return InventoryCommands.Transfer(from, token, into, section, 0, 0, wearer: wearer) ? 1 : 0;
        if (equip && InventoryCommands.Transfer(from, token, into, "backpack_attach", 0, 0, wearer: wearer)) return 1;
        if (InventoryCommands.MainClosed(wearer)) return 0;
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
    /// The price of <paramref name="units"/> units: a buy-back's coins, else the market's stack price as the price filters leave it
    /// (<see cref="ITradePriceFilter"/>).
    /// </summary>
    long Price(World world, Market market, ItemInstance item, int units, bool sell, Trader terms, int holder)
    {
        if (IsBuyback(item, sell)) return item.Buyback!.Price(units);

        return Filtered(world, item, units, sell, market.StackPrice(item.WithQuantity(units), sell, terms, holder));
    }

    /// <summary>
    /// <paramref name="price"/> for <paramref name="units"/> units of <paramref name="item"/> after the price filters, as the open window's
    /// customer and the trader's squad trade it; the price itself without a filter.
    /// </summary>
    long Filtered(World world, ItemInstance item, int units, bool sell, long price)
    {
        if (!world.TradePriceFilters.Active) return price;

        int shopSquad = world.Characters.TryResolveNext(_trader, out int t) ? world.Characters.Cold(t)?.SquadId ?? -1 : -1;
        return world.TradePriceFilters.Pass(world, new TradeDeal(world.Tick, item.Record, units, sell, _customer, shopSquad), price);
    }

    /// <summary>
    /// The receipt of the units' coins, each unit's share of the stack price; null for a buy-back.
    /// </summary>
    TradeReceipt? Receipt(World world, Market market, ItemInstance item, int units, bool sell, Trader terms, int holder)
    {
        if (IsBuyback(item, sell)) return null;

        var coins = new long[units];
        long previous = 0;

        for (int n = 1; n <= units; n++)
        {
            long total = Price(world, market, item, n, sell, terms, holder);
            coins[n - 1] = total - previous;
            previous = total;
        }

        return new(_session, !sell, coins);
    }

    /// <summary>
    /// Puts the units at the cell they were dropped on (the trade window's <c>placeItemFromMouse</c>): the cell of <paramref name="section"/> in
    /// <paramref name="root"/> (the backpack's contents for <c>backpack_content</c>) when the footprint is inside the grid, free and the stack within
    /// the limit; otherwise wherever they fit, as <see cref="Place"/>. <paramref name="mainShut"/>: the main grid takes nothing.
    /// </summary>
    static int PlaceAt(Inventory root, ItemInstance copy, int units, string section, int x, int y, bool mainShut = false)
    {
        var grid = section == "main" && !mainShut ? root
            : section == "backpack_content" ? root.Backpack?.Contents
            : null;

        if (grid is not null && x >= 0 && y >= 0 && x + copy.Width <= grid.Width && y + copy.Height <= grid.Height && units <= grid.LimitOf(copy)
            && grid.IsFree(section, x, y, copy.Width, copy.Height))
        {
            copy.Section = section;
            copy.X = x;
            copy.Y = y;
            grid.Items.Add(copy);
            return units;
        }

        return Place(root, copy, units, mainShut);
    }

    /// <summary>
    /// Puts the units wherever they fit in <paramref name="into"/> (<see cref="Inventory.Add"/>; <paramref name="mainShut"/>: its backpack only); the
    /// units placed.
    /// </summary>
    internal static int Place(Inventory into, ItemInstance copy, int units, bool mainShut = false)
    {
        copy.Section = "main";
        return into.Add(copy, mainShut) ? units : units - copy.Quantity;
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
            long value = Filtered(world, item, 1, false, m.UnitPrice(item, false, shop.Terms, holder));
            long sellValue = Filtered(world, item, 1, true, m.UnitPrice(item, true, shop.Terms, holder));
            bool buyback = IsBuyback(item, sell);

            if (buyback)
            {
                if (sell) sellValue = Price(world, m, item, 1, true, shop.Terms, holder);
                else value = Price(world, m, item, 1, false, shop.Terms, holder);
            }

            bool stolen = item.IsStolenFor(holder, _data);

            // The roll's own odds feed the hover box, the sale prompt and the roll alike; only where the shop would roll on the item at all.
            float thievery = world.Characters.Cold(seller)!.Stats?[StatsEnumerated.Thieving] ?? 0;
            float? odds = sell && stolen && Fences(item, squad, shop.Terms) ? FenceOdds(world, thievery, item, seller, t, squad, shop.Terms) : null;
            long stackPrice = Price(world, m, item, item.Quantity, sell, shop.Terms, holder);
            long unitPrice = Price(world, m, item, 1, sell, shop.Terms, holder);
            return new(index, item.Record, item.Name, item.Section, item.X, item.Y, item.Width, item.Height, item.Quantity, item.Quality, stackPrice,
                unitPrice, stolen)
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
                StolenOwner = stolen ? world.Characters.OwnerNameOf(item.Owner) : null,
                Unique = item.Unique,
                FenceChance = odds is { } shown ? Math.Clamp(shown, 0, 1) : null,
                FenceOdds = odds is < 1 ? Math.Max(odds.Value, 0) : null,
                Illegal = sell && IsIllegal(world, m, shop.Terms, item.Record),
            };
        }

        var stock = StockViews(shop, item => View(item.Item, item.Index, false, c));
        var goodsOf = new Dictionary<CharacterId, IReadOnlyList<TradeItemView>>();
        foreach (int seller in Sellers(world, c, t))
            goodsOf[world.Characters.IdOf(seller)] = Goods(world.Characters.Cold(seller)!, item => View(item.Item, item.Index, true, seller));

        var held = HeldView(world, goodsOf, (item, sell, seller) => View(item, -1, sell, seller), c);
        string name = world.Characters.Cold(t)!.Name;
        return new TradeView(_customer, _trader, name, squad.Money, world.Player.Money, shop.Window.Width, shop.Window.Height, stock,
            goodsOf[_customer], shop.Terms.BuysStolen, shop.Terms.BuysIllegal)
        {
            Held = held,
            GoodsOf = goodsOf,
        };
    }

    /// <summary>
    /// Only the window grid's own items, at their All() indices (which tokens use): a bag's contents are not loose stock.
    /// </summary>
    static List<TradeItemView> StockViews(Shop shop, Func<(ItemInstance Item, int Index), TradeItemView> view)
    {
        var stock = new List<TradeItemView>(shop.Window.Items.Count);
        int at = 0;

        foreach (var item in shop.Window.Items)
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
    /// The customer, then the player's other characters who could take the trade over: able to trade, within <see cref="TradeSettings.PassRange"/>
    /// of the trader and not of its squad (a faction shop's own members).
    /// </summary>
    IEnumerable<int> Sellers(World world, int c, int t)
    {
        yield return c;

        int shopSquad = world.Characters.Cold(t)!.SquadId;

        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction < 0 || squad.Faction != world.Player.Faction && squad.Faction != _data.PlayerFaction || squad.Id == shopSquad)
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
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;
using Meitou.Simulation.Trading;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A trader squad's shop on its PLATOON (docs/game/economy.md "Shops and transactions"): the original's <c>inventory refresh time</c>
/// (absolute game hours of the next restock) and Meitou's own <c>meitou shop</c> (a squad shop's stock, as <see cref="ContainerSave"/>'s JSON;
/// <c>[]</c> when sold out, and always for a building shop, whose goods are saved with its containers) and <c>meitou shop fills</c>
/// (<see cref="Shop.Fills"/>, the next fill's roll key). The purse is the platoon's <c>money</c>. A save without <c>meitou shop</c> (the original's)
/// keeps a building shop as filled, its goods in its containers, until its <c>inventory refresh time</c>; a squad shop there gets a fresh fill. An older Meitou save kept a building shop's stock under the key with its containers empty: read, it goes to the containers
/// (<see cref="ShopView.Adopt"/>). A player squad's shop a code mod gave it (<see cref="SetSquadShop"/>) is saved on its player platoon under
/// Meitou keys only (<see cref="WritePlayer"/>); its purse is the squad's own (<see cref="ModSquadSave"/>).
/// </summary>
internal static class ShopSave
{
    /// <summary>
    /// The PLATOON keys: the original's restock time, and Meitou's stock and fill key.
    /// </summary>
    public const string RefreshKey = "inventory refresh time", StockKey = "meitou shop", FillsKey = "meitou shop fills";
    /// <summary>
    /// A player platoon's further shop keys: the next restock in absolute game hours, the mod that gave the shop and its template.
    /// </summary>
    public const string PlayerRestockKey = "meitou shop restock", OwnerKey = "meitou shop owner", TemplateKey = "meitou shop template";
    /// <summary>
    /// The rows of the grid an older save's building shop stock is read into (its packed stock was at most a few hundred rows high).
    /// </summary>
    const int OldStockRows = 4096;

    /// <summary>
    /// Writes the shop over the platoon's keys; a squad with no shop keeps what was read.
    /// </summary>
    public static void Write(FcsRecord platoon, Shop? shop, World world)
    {
        if (shop is null) return;
        platoon.Floats[RefreshKey] = (float)(world.GameHours + (shop.NextRestockTick - world.Tick) * BodySystem.HoursPerTick);
        platoon.Ints[FillsKey] = shop.Fills;
        bool own = shop.Building is null || shop.View.Pending;   // a building shop's goods are its containers', unless not handed over yet
        platoon.Strings[StockKey] = own ? ContainerSave.Write(shop.Stock) ?? "[]" : "[]";
    }

    /// <summary>
    /// The shop a Meitou save holds for <paramref name="squad"/> (made from a saved platoon, its home already set); null without one.
    /// A squad whose template fills no shop gets back the stock a trade gave it (<see cref="TradeSystem.UnfilledShop"/>;
    /// a housed squad's goes back to its containers at the next stock pass).
    /// </summary>
    public static Shop? Read(World world, Squad squad, FcsRecord platoon, PopulationData data)
    {
        if (!platoon.Strings.TryGetValue(StockKey, out var text)) return OriginalBuildingShop(world, squad, platoon, data);
        if (TradeSystem.TemplateRecord(data, squad.TemplateId) is not { HasShop: true } template) return Unfilled(text, squad, data);

        var terms = TradeSystem.TermsOf(squad, template);
        var shop = HomeShop(squad, terms) ?? new Shop { Terms = terms };
        ReadStock(text, shop, data);
        shop.Fills = platoon.Ints.GetValueOrDefault(FillsKey);
        SetRestock(world, platoon, shop, template);
        return shop;
    }

    /// <summary>
    /// The building shop of a trader the original saved (its PLATOON has the original's <c>inventory refresh time</c> and no <c>meitou shop</c>),
    /// counted as filled: the original fills a shop when its squad is made and again only once that time passes (docs/game/economy.md "Restock"),
    /// and the goods are its containers', which the furniture entries of the save fill (<see cref="FurnitureInventorySave"/>). Null for any other
    /// squad (a squad shop, whose stock this does not read, gets a fresh fill).
    /// </summary>
    static Shop? OriginalBuildingShop(World world, Squad squad, FcsRecord platoon, PopulationData data)
    {
        if (!platoon.Floats.ContainsKey(RefreshKey)) return null;
        if (TradeSystem.TemplateRecord(data, squad.TemplateId) is not { HasShop: true } template) return null;
        if (HomeShop(squad, TradeSystem.TermsOf(squad, template)) is not { } shop) return null;

        shop.Fills = 1;
        SetRestock(world, platoon, shop, template);
        return shop;
    }

    /// <summary>
    /// The building shop the trade system makes for the squad (<see cref="TradeSystem"/> <c>MakeShop</c>): a housed trader has one when its home has
    /// stock containers, whether or not it makes prisoner or slave squads (docs/game/economy.md); null otherwise.
    /// </summary>
    static Shop? HomeShop(Squad squad, Trader terms) => squad.HomeBuilding is { } home ? Shop.ForBuilding(home, terms) : null;

    /// <summary>
    /// The next restock from the PLATOON's <c>inventory refresh time</c>, at most one refresh away (engine choice: older Meitou saves pushed it out
    /// by the save's clock at each load).
    /// </summary>
    static void SetRestock(World world, FcsRecord platoon, Shop shop, SquadTemplate template)
    {
        double hours = Math.Clamp(platoon.Floats.GetValueOrDefault(RefreshKey) - world.GameHours, 0, template.VendorsRefreshTime);
        shop.NextRestockTick = world.Tick + (long)Math.Ceiling(hours / BodySystem.HoursPerTick);
    }

    /// <summary>
    /// Writes a player squad's shop over its platoon's Meitou keys (the stock as <see cref="Write"/> writes a squad shop's, the fill count, the
    /// restock time, the owner and template while named); a squad without one loses them, so a vanilla platoon's record is left as it was.
    /// </summary>
    public static void WritePlayer(FcsRecord platoon, Squad squad, World world)
    {
        // Only the player's own squads: another squad written through a player platoon file (a prisoner's) keeps its shop out of these keys.
        if (squad.Shop is not { } shop || squad.Faction != world.Player.Faction)
        {
            platoon.Strings.Remove(StockKey);
            platoon.Ints.Remove(FillsKey);
            platoon.Floats.Remove(PlayerRestockKey);
            platoon.Strings.Remove(OwnerKey);
            platoon.Strings.Remove(TemplateKey);
            return;
        }

        platoon.Strings[StockKey] = ContainerSave.Write(shop.Stock) ?? "[]";
        platoon.Ints[FillsKey] = shop.Fills;
        platoon.Floats[PlayerRestockKey] = (float)(world.GameHours + (shop.NextRestockTick - world.Tick) * BodySystem.HoursPerTick);
        WriteName(platoon, OwnerKey, squad.ShopOwner);
        WriteName(platoon, TemplateKey, squad.ShopTemplate);
    }

    /// <summary>
    /// Sets the string <paramref name="key"/> to <paramref name="value"/>, or removes it for none.
    /// </summary>
    static void WriteName(FcsRecord platoon, string key, string? value)
    {
        if (value is not null) platoon.Strings[key] = value;
        else platoon.Strings.Remove(key);
    }

    /// <summary>
    /// The shop a Meitou save holds for a loaded player squad (<see cref="WritePlayer"/>), its owner and template noted on the squad; null without
    /// one. As <see cref="Trading.TradeSystem"/> makes it: a squad shop with the template's terms, its restock at most one refresh away.
    /// </summary>
    public static Shop? ReadPlayer(World world, Squad squad, FcsRecord platoon, PopulationData data)
    {
        if (!platoon.Strings.TryGetValue(StockKey, out var text)) return null;

        squad.ShopOwner = platoon.Strings.GetValueOrDefault(OwnerKey) is { Length: > 0 } owner ? owner : null;
        squad.ShopTemplate = platoon.Strings.GetValueOrDefault(TemplateKey) is { Length: > 0 } id ? id : null;
        var template = squad.ShopTemplate is { } named ? TradeSystem.TemplateRecord(data, named) : null;
        var shop = new Shop { Terms = TradeSystem.TermsOf(squad, template), Fills = platoon.Ints.GetValueOrDefault(FillsKey) };
        ContainerSave.Read(text, shop.Stock, data.Items);

        double hours = Math.Max(platoon.Floats.GetValueOrDefault(PlayerRestockKey) - world.GameHours, 0);
        if (template is { HasShop: true }) hours = Math.Min(hours, template.VendorsRefreshTime);

        shop.NextRestockTick = world.Tick + (long)Math.Ceiling(hours / BodySystem.HoursPerTick);
        return shop;
    }

    /// <summary>
    /// A fresh unfilled shop of the squad's template with the saved stock <paramref name="text"/>; null when the template has no shop.
    /// </summary>
    static Shop? Unfilled(string text, Squad squad, PopulationData data)
    {
        var shop = TradeSystem.UnfilledShop(squad, TradeSystem.TemplateRecord(data, squad.TemplateId));
        if (shop is not null) ReadStock(text, shop, data);
        return shop;
    }

    /// <summary>
    /// Reads the saved stock <paramref name="text"/>: a squad shop's into its stock; a building shop's (an older save's) into a grid tall enough
    /// for it, which the trade system hands to the containers once they are registered.
    /// </summary>
    static void ReadStock(string text, Shop shop, PopulationData data)
    {
        if (shop.Building is null)
        {
            ContainerSave.Read(text, shop.Stock, data.Items);
            return;
        }

        if (text == "[]") return;

        shop.Stock = new Inventory(shop.ViewSize.Columns, OldStockRows) { StackBonusMult = Shop.StockStackMult };
        ContainerSave.Read(text, shop.Stock, data.Items);
        shop.View.Adopt();
    }
}
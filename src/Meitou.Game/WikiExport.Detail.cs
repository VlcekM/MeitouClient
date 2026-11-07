using System.Globalization;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Gameplay.Economy;
using Meitou.Data.World;

namespace Meitou.Game;

/// <summary>
/// The second layer of <c>--wiki-export</c> (docs/agent-playtest.md, "The wiki"): town detail (buildings, houses for sale, the shops and their stock), town overrides
/// (which world states turn a town into another), and unique and bounty characters. All of it is read from the player's own game data; the same readers the game boots with
/// (<see cref="ResidentHomes"/>, <see cref="TownBuildings"/>) decide which building houses which shop, and the original's price rule gives the house prices
/// (docs/game/ui-screens.md "Selected building").
/// </summary>
static partial class WikiExport
{
    /// <summary>What <see cref="Run"/> keeps of each placed town for the detail pass.</summary>
    sealed record PlacedTown(TownPlacement Placement, TownData Town, Row Row);

    sealed record DetailRows(List<Row> Shops, List<Row> Vendors, List<Row> Overrides, List<Row> Characters);

    static DetailRows Detail(GameDatabase db, WorldLevelData world, List<PlacedTown> placed, Func<string, string, string> id)
    {
        var layouts = new BuildingLayouts(db, world.Interiors);
        ResidentRuins.Apply(db, world, layouts);
        var buildings = TownBuildings.Build(db, world, layouts, (_, _) => 0f);
        string? playerId = db.Find(FactionData.PlayerId) is not null ? FactionData.PlayerId : null;
        var player = playerId is null ? null : FactionData.From(db.Find(playerId)!);
        var functions = new Dictionary<string, GameRecord?>(StringComparer.Ordinal);
        var shops = new List<Row>();
        var vendorRows = new Dictionary<string, Row>(StringComparer.Ordinal);   // vendor list record -> row
        var templates = new Dictionary<string, SquadTemplate>(StringComparer.Ordinal);
        foreach (var t in SquadTemplate.LoadAll(db)) templates.TryAdd(t.Id, t);

        Row VendorRow(string recordId)
        {
            if (vendorRows.TryGetValue(recordId, out var known)) return known;
            var record = db.Find(recordId)!;
            var list = VendorList.From(db, record);
            double total = list.Entries.Sum(e => (double)e.Weight);
            var rows = new List<object?>();
            foreach (var e in list.Entries.Take(400))
            {
                if (db.Find(e.Id) is not { } item || item.Name.Length == 0) continue;
                rows.Add(new SortedDictionary<string, object?>
                {
                    ["item"] = item.Name,
                    ["type"] = e.Type == FcsRecordType.RESEARCH ? "blueprint" : e.Type.ToString().ToLowerInvariant(),
                    ["pct"] = total > 0 ? (float)Math.Round(e.Weight / total * 100, 1) : 0f,
                });
            }
            var makers = Names(db, list.Manufacturers.Select(m => m.Id));
            var data = new SortedDictionary<string, object?>
            {
                ["itemsCount"] = list.ItemsCount,
                ["items"] = rows,
                ["itemTotal"] = list.Entries.Count,
                ["moneyChance"] = list.MoneyItemProb,
                ["moneyMin"] = list.MoneyItemMin,
                ["moneyMax"] = list.MoneyItemMax,
                ["manufacturers"] = makers,
                ["record"] = recordId,
            };
            var row = new Row(id(record.Name, recordId), record.Name, "vendor-list", null, null, null, null, "Observed",
                $"Vendor list: {list.Entries.Count} possible items (pct is the relative chance of one pick; a shop picks around its template's fill amount, split between its lists by weight){(makers.Count > 0 ? ", weapons made by " + string.Join(", ", makers) : "")}.", recordId, data);
            vendorRows[recordId] = row;
            return row;
        }

        // --- Town detail: buildings, houses for sale, shops.
        foreach (var (placement, town, row) in placed)
        {
            var faction = town.Faction is { } fid && db.Find(fid) is { Type: FcsRecordType.FACTION } fr ? FactionData.From(fr) : null;
            var list = buildings.GetValueOrDefault(placement.InstanceId) ?? [];
            var housed = ResidentHomes.Assign(db, town, faction, list, playerId);
            var homeOf = new Dictionary<string, SquadTemplate>(StringComparer.Ordinal);
            foreach (var (b, t) in housed) homeOf[b.Key] = t;

            // The buildings, grouped by name and what lives in them.
            var groups = new SortedDictionary<string, (int Count, int Ruins, int Sale, List<long> Prices, string Role, string Kind)>(StringComparer.Ordinal);
            int walls = 0, gates = 0;
            var forSale = new List<object?>();
            bool saleAllowedTown = town.Type is TownType.Outpost or TownType.Town or TownType.Village;
            bool hostile = false;
            if (faction is not null && player is not null)
            {
                int rel = faction.Relations.TryGetValue(playerId!, out int r) ? r : Math.Min(faction.DefaultRelation, player.DefaultRelation);
                hostile = rel <= -30;
            }
            foreach (var b in list)
            {
                if (b.IsWall) { walls++; continue; }
                if (b.IsGate) { gates++; continue; }
                homeOf.TryGetValue(b.Key, out var home);
                bool shop = home is null ? b.IsShop : home.HasShop;   // a housed building is what its residents make of it; the exterior layout's flags only speak for an empty one
                bool bar = !shop && (home is null ? b.IsBar : town.BarSquads.Any(l => l.Id == home.Id));
                if (!b.CanHouse && !shop && !bar) continue;
                string role = home is null ? "" : ResidentRuins.DisplayName(home) ?? home.Name;
                string kind = shop ? "shop" : bar ? "bar" : home is null ? "house" : "building";
                string name = db.Find(b.BuildingId)?.Name ?? b.Name;
                long? price = SaleOf(db, b, home, faction, town, saleAllowedTown && !hostile, functions);
                string key = $"{name}|{role}|{kind}";
                var g = groups.GetValueOrDefault(key, (0, 0, 0, [], role, kind));
                g.Count++;
                if (b.Destroyed) g.Ruins++;
                if (price is { } p) { g.Sale++; g.Prices.Add(p); forSale.Add(new SortedDictionary<string, object?> { ["building"] = name, ["price"] = (int)p, ["ruin"] = b.Destroyed }); }
                groups[key] = g;
            }
            var buildingRows = new List<object?>();
            foreach (var (key, g) in groups)
            {
                var d = new SortedDictionary<string, object?> { ["name"] = key.Split('|')[0], ["count"] = g.Count, ["kind"] = g.Kind };
                if (g.Role.Length > 0) d["usedAs"] = g.Role;
                if (g.Ruins > 0) d["ruins"] = g.Ruins;
                if (g.Sale > 0) { d["forSale"] = g.Sale; d["priceMin"] = (int)g.Prices.Min(); d["priceMax"] = (int)g.Prices.Max(); }
                buildingRows.Add(d);
            }

            // Shops: every resident or bar template that trades, with the building that houses it when the assignment gave it one.
            var traders = new List<(SquadTemplate Template, string Source)>();
            foreach (var l in ResidentHomes.Entries(town, faction).Select(e => (e, "resident")).Concat(town.BarSquads.Select(e => (e, "bar"))))
                if (templates.TryGetValue(l.e.Id, out var tpl) && tpl.HasShop && traders.All(x => x.Template.Id != tpl.Id)) traders.Add((tpl, l.Item2));
            var shopNames = new List<string>();
            foreach (var (tpl, source) in traders)
            {
                string? building = housed.FirstOrDefault(h => h.Template.Id == tpl.Id).Building is { } hb ? db.Find(hb.BuildingId)?.Name : null;
                var vendors = new List<object?>();
                var listNames = new List<string>();
                long weightSum = tpl.Vendors.Where(v => db.Find(v.Id) is { Type: FcsRecordType.VENDOR_LIST }).Sum(v => (long)(v.V0 == 0 ? 100 : v.V0));
                foreach (var v in tpl.Vendors)
                {
                    if (db.Find(v.Id) is not { Type: FcsRecordType.VENDOR_LIST } vr) continue;
                    var vrow = VendorRow(v.Id);
                    listNames.Add(vr.Name);
                    vendors.Add(new SortedDictionary<string, object?> { ["id"] = vrow.Id, ["list"] = vr.Name, ["weight"] = v.V0 == 0 ? 100 : v.V0, ["approxItems"] = (int)Math.Round((double)(v.V0 == 0 ? 100 : v.V0) / Math.Max(1, weightSum) * tpl.VendorsFillTotalAmount) });
                }
                if (vendors.Count == 0) continue;
                string label = tpl.Name + (building is null ? "" : $" ({building})");
                shopNames.Add(label);
                var data = new SortedDictionary<string, object?>
                {
                    ["town"] = town.Name,
                    ["townId"] = row.Id,
                    ["squad"] = tpl.Name,
                    ["building"] = building,
                    ["via"] = source,
                    ["vendors"] = vendors,
                    ["fillItems"] = tpl.VendorsFillTotalAmount,
                    ["buyMult"] = tpl.BuyMult,
                    ["buysStolen"] = tpl.BuysStolen,
                    ["buysIllegal"] = tpl.BuysIllegal,
                    ["refreshHours"] = tpl.VendorsRefreshTime,
                    ["record"] = tpl.Id,
                };
                shops.Add(new Row(id($"{tpl.Name} {town.Name}", tpl.Id), $"{tpl.Name} ({town.Name})", "trader", FactionNameOf(db, tpl.Faction) is { Length: > 0 } tf ? tf : null, row.Zone, row.X, row.Z, "Inferred",
                    $"Trader in {town.Name}{(building is null ? "" : ", housed in " + building)}; stock rolled from {string.Join(", ", listNames)}. Position is the town's.", tpl.Id, data));
            }
            row.Data["buildings"] = buildingRows;
            row.Data["walls"] = walls;
            row.Data["gates"] = gates;
            row.Data["shops"] = shopNames;
            row.Data["forSale"] = forSale;
            if (hostile && saleAllowedTown) row.Data["saleNote"] = "the owning faction is hostile to your faction by default, so the game offers no houses here";
        }

        // --- Town overrides: which world states turn a town into another.
        var overrides = new List<Row>();
        var stateText = new Dictionary<string, (string Text, string Negated, List<string> Characters, List<string> Factions)>(StringComparer.Ordinal);
        (string Text, string Negated, List<string> Characters, List<string> Factions) State(string stateId)
        {
            if (stateText.TryGetValue(stateId, out var known)) return known;
            var chars = new List<string>(); var facs = new List<string>();
            var s = WorldEventStateData.From(db, db.Find(stateId)!);
            var parts = new List<string>(); var negated = new List<string>();
            string Unique(int v) => v switch { 0 => "dead", 1 => "alive", 2 => "imprisoned", _ => "state " + v };
            foreach (var (c, v) in s.NpcIs) { string n = db.Find(c)?.Name ?? c; chars.Add(n); parts.Add($"{n} is {Unique(v)}"); negated.Add($"{n} is not {Unique(v)}"); }
            foreach (var (c, v) in s.NpcIsNot) { string n = db.Find(c)?.Name ?? c; chars.Add(n); parts.Add($"{n} is not {Unique(v)}"); negated.Add($"{n} is {Unique(v)}"); }
            foreach (var (f, v) in s.PlayerAlly) { string n = db.Find(f)?.Name ?? f; facs.Add(n); parts.Add($"{n} {(v ? "is" : "is not")} allied with you"); negated.Add($"{n} {(v ? "is not" : "is")} allied with you"); }
            foreach (var (f, v) in s.PlayerEnemy) { string n = db.Find(f)?.Name ?? f; facs.Add(n); parts.Add($"{n} {(v ? "is" : "is not")} hostile to you"); negated.Add($"{n} {(v ? "is not" : "is")} hostile to you"); }
            string involved = s.PlayerInvolvement ? " (you took part)" : "";
            string text = parts.Count == 0 ? "(no condition)" : string.Join(" and ", parts) + involved;
            string neg = parts.Count == 0 ? "(never)" : parts.Count == 1 && involved.Length == 0 ? negated[0] : $"not ({text})";
            return stateText[stateId] = (text, neg, chars, facs);
        }
        var overridesOf = new Dictionary<string, List<(string Town, string Variant)>>(StringComparer.Ordinal);   // character name -> towns it changes
        foreach (var (placement, town, row) in placed)
        {
            if (town.OverrideTown.Count == 0) continue;
            var variants = new List<object?>();
            var allChars = new SortedSet<string>(StringComparer.Ordinal); var allFacs = new SortedSet<string>(StringComparer.Ordinal);
            var summary = new List<string>();
            foreach (var link in town.OverrideTown)
            {
                if (db.Find(link.Id) is not { Type: FcsRecordType.TOWN } cr) continue;
                var cand = TownData.From(cr);
                var conds = new List<object?>();
                var used = cand.WorldState.Where(w => db.Find(w.Id) is { Type: FcsRecordType.WORLD_EVENT_STATE }).DistinctBy(w => w.Id).ToList();
                var chars = new SortedSet<string>(StringComparer.Ordinal); var facs = new SortedSet<string>(StringComparer.Ordinal);
                var texts = new List<string>();
                foreach (var w in used)
                {
                    var (text, neg, cs, fs) = State(w.Id);
                    string t = w.V0 == 1 ? text : neg;
                    texts.Add(t);
                    conds.Add(new SortedDictionary<string, object?> { ["state"] = db.Find(w.Id)!.Name, ["mustHold"] = w.V0 == 1, ["text"] = t });
                    foreach (var c in cs) chars.Add(c);
                    foreach (var f in fs) facs.Add(f);
                }
                foreach (var c in chars) allChars.Add(c);
                foreach (var f in facs) allFacs.Add(f);
                string newFaction = cand.Faction is { } cf && cf != town.Faction ? FactionNameOf(db, cf) : "";
                variants.Add(new SortedDictionary<string, object?>
                {
                    ["becomes"] = cand.Name,
                    ["type"] = cand.Type.ToString(),
                    ["faction"] = newFaction.Length > 0 ? newFaction : FactionNameOf(db, town.Faction),
                    ["factionChanges"] = newFaction.Length > 0,
                    ["weight"] = used.Count,
                    ["when"] = texts,
                    ["conditions"] = conds,
                    ["record"] = cr.StringId,
                    ["furtherOverrides"] = cand.OverrideTown.Count,
                });
                summary.Add($"{cand.Name} ({cand.Type}{(newFaction.Length > 0 ? ", " + newFaction : "")}) when {(texts.Count == 0 ? "no condition" : string.Join("; ", texts))}");
                foreach (var c in chars)
                {
                    if (!overridesOf.TryGetValue(c, out var l)) overridesOf[c] = l = [];
                    l.Add((town.Name, cand.Name));
                }
            }
            if (variants.Count == 0) continue;
            var data = new SortedDictionary<string, object?>
            {
                ["town"] = town.Name,
                ["townId"] = row.Id,
                ["variants"] = variants,
                ["characters"] = allChars.ToList(),
                ["factions"] = allFacs.ToList(),
                ["rule"] = "Checked when the town's zone loads; the variant with the most conditions that all hold wins (ties: first listed); skipped for towns of your faction.",
                ["record"] = placement.TownId,
            };
            row.Data["overrides"] = summary;
            overrides.Add(new Row(id($"{town.Name} overrides", placement.TownId), $"{town.Name} overrides", "override", FactionNameOf(db, town.Faction) is { Length: > 0 } fn ? fn : null, row.Zone, row.X, row.Z, "Observed",
                $"{variants.Count} variant(s) of {town.Name}: " + string.Join(" | ", summary), placement.TownId, data));
        }

        // --- Unique and bounty characters.
        var characters = Characters(db, placed, templates, id);
        return new DetailRows(shops, [.. vendorRows.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal)], overrides, characters);
    }

    static string FactionNameOf(GameDatabase db, string? id) => id is not null && db.Find(id) is { } f ? f.Name : "";

    /// <summary>The price the game quotes for a town house (docs/game/ui-screens.md "Selected building"), or null when it is not for sale.</summary>
    static long? SaleOf(GameDatabase db, TownBuilding b, SquadTemplate? home, FactionData? owner, TownData town, bool allowed, Dictionary<string, GameRecord?> functions)
    {
        if (!allowed || owner is null || b.IsGate || b.IsWall || db.Find(b.BuildingId) is not { } record || record.GetBool("is wall gate") || record.GetBool("links together")) return null;
        int? function = TownBuildings.FunctionOf(db, record, functions)?.GetInt("function");
        if (function != 19 && !(function is null && record.GetReferences("interior").Count > 0)) return null;
        var state = b.LevelState;
        bool sale = state?.Bools.TryGetValue("sell home", out bool levelSale) == true ? levelSale
            : db.Find(b.ResidentsTemplate)?.GetBool("sell home") ?? state is null;
        if (!sale || (state?.GetBool("is inside building") ?? false)) return null;
        float multiplier = db.Find(owner.Id)?.GetFloat("building cost mult", 1) ?? 1;
        float price = (b.Destroyed ? 0.375f : 1) * BuildingBuildData.From(record).Target * multiplier * 400;
        return float.IsFinite(price) && price >= 0 && price <= int.MaxValue ? (long)price : null;
    }

    // --- Characters.
    static List<Row> Characters(GameDatabase db, List<PlacedTown> placed, Dictionary<string, SquadTemplate> templates, Func<string, string, string> id)
    {
        // Which squad templates name a character, and in which role.
        var usedBy = new Dictionary<string, List<(SquadTemplate Template, string Role)>>(StringComparer.Ordinal);
        void Use(string character, SquadTemplate t, string role)
        {
            if (!usedBy.TryGetValue(character, out var l)) usedBy[character] = l = [];
            if (!l.Any(x => x.Template.Id == t.Id && x.Role == role)) l.Add((t, role));
        }
        foreach (var t in templates.Values)
        {
            if (t.Leader is { } leader) Use(leader.Id, t, "leader");
            foreach (var m in t.Squad) Use(m.Id, t, "member");
            foreach (var m in t.Squad2) Use(m.Id, t, "member");
            foreach (var m in t.ChooseFrom) Use(m.Id, t, "member");
            foreach (var m in t.Prisoners) if (templates.TryGetValue(m.Id, out var pt)) { if (pt.Leader is { } pl) Use(pl.Id, t, "prisoner"); foreach (var x in pt.Squad) Use(x.Id, t, "prisoner"); }
        }
        // Towns that list a template (the town's own lists, and the lists of its override variants), with the town's position.
        var townsOf = new Dictionary<string, List<(string Town, float X, float Z, string Via)>>(StringComparer.Ordinal);
        void Home(string template, PlacedTown pt, string via)
        {
            if (!townsOf.TryGetValue(template, out var l)) townsOf[template] = l = [];
            if (!l.Any(x => x.Town == pt.Town.Name && x.Via == via)) l.Add((pt.Town.Name, pt.Placement.Position.X, pt.Placement.Position.Z, via));
        }
        foreach (var pt in placed)
        {
            foreach (var l in pt.Town.Residents.Concat(pt.Town.BarSquads)) Home(l.Id, pt, "");
            foreach (var o in pt.Town.OverrideTown)
                if (db.Find(o.Id) is { Type: FcsRecordType.TOWN } ovr)
                {
                    var ot = TownData.From(ovr);
                    foreach (var l in ot.Residents.Concat(ot.BarSquads)) Home(l.Id, pt, $"as {ot.Name}");
                }
        }
        // Campaigns that name a character as special leader.
        var campaignsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in db.OfType(FcsRecordType.FACTION_CAMPAIGN))
            foreach (var r in c.GetReferences("special leader"))
            {
                if (!campaignsOf.TryGetValue(r.TargetStringId, out var l)) campaignsOf[r.TargetStringId] = l = [];
                if (c.Name.Length > 0) l.Add(c.Name);
            }
        var joinByDialogue = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Joins(string dialogueId)
        {
            if (joinByDialogue.TryGetValue(dialogueId, out bool known)) return known;
            joinByDialogue[dialogueId] = false;
            bool found = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Walk(GameRecord node, int depth)
            {
                if (found || depth > 30 || !seen.Add(node.StringId)) return;
                foreach (var e in node.GetReferences("effects"))
                    if (db.Find(e.TargetStringId)?.GetInt("action name") is 3 or 18) { found = true; return; }
                foreach (string list in new[] { "lines", "interrupt" })
                    foreach (var r in node.GetReferences(list))
                        if (db.Find(r.TargetStringId) is { } next) Walk(next, depth + 1);
            }
            if (db.Find(dialogueId) is { Type: FcsRecordType.DIALOGUE } d) Walk(d, 0);
            return joinByDialogue[dialogueId] = found;
        }
        bool CanJoin(GameRecord character, IEnumerable<SquadTemplate> asLeader)
        {
            var dialogues = new List<string>();
            dialogues.AddRange(character.GetReferences("dialogue").Select(r => r.TargetStringId));
            foreach (var package in character.GetReferences("dialogue package").Select(r => db.Find(r.TargetStringId)))
                if (package is not null) dialogues.AddRange(package.GetReferences("dialogs").Select(r => r.TargetStringId));
            foreach (var t in asLeader)
                if (db.Find(t.Id) is { } tr)
                    foreach (var package in tr.GetReferences("dialog leader").Select(r => db.Find(r.TargetStringId)))
                        if (package is not null) dialogues.AddRange(package.GetReferences("dialogs").Select(r => r.TargetStringId));
            return dialogues.Distinct().Any(Joins);
        }

        var rows = new List<Row>();
        foreach (var record in db.OfType(FcsRecordType.CHARACTER).Concat(db.OfType(FcsRecordType.ANIMAL_CHARACTER)).OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.StringId, StringComparer.Ordinal))
        {
            if (record.Name.Length == 0) continue;
            bool unique = record.GetBool("unique");
            int chance = record.GetInt("bounty chance"), amount = record.GetInt("bounty amount"), fuzz = Math.Max(0, record.GetInt("bounty amount fuzz"));
            bool bounty = chance > 0 && amount > 0;
            if (!unique && !bounty) continue;
            string faction = record.GetReferences("faction") is { Count: > 0 } fl ? FactionNameOf(db, fl[0].TargetStringId) : "";
            var uses = usedBy.GetValueOrDefault(record.StringId) ?? [];
            if (faction.Length == 0 && uses.Select(u => FactionNameOf(db, u.Template.Faction)).FirstOrDefault(n => n.Length > 0) is { } sf) faction = sf;
            var homes = new List<object?>();
            var seenHome = new HashSet<string>(StringComparer.Ordinal);
            (float X, float Z)? first = null;
            string firstTown = "";
            // Living in a town beats being held there; the town's own list beats an override variant's.
            var found = new List<(int Rank, string Town, float X, float Z, string Via, string Squad, string Role)>();
            foreach (var (t, role) in uses)
                foreach (var h in townsOf.GetValueOrDefault(t.Id) ?? [])
                    found.Add(((role == "prisoner" ? 2 : 0) + (h.Via.Length > 0 ? 1 : 0), h.Town, h.X, h.Z, h.Via, t.Name, role));
            foreach (var (_, town, x, z, via, squad, role) in found.OrderBy(f => f.Rank).ThenBy(f => f.Town, StringComparer.Ordinal))
            {
                if (!seenHome.Add(town + "|" + (via.Length > 0) + "|" + role)) continue;
                first ??= (x, z);
                if (firstTown.Length == 0) firstTown = town;
                homes.Add(new SortedDictionary<string, object?> { ["town"] = town, ["x"] = (int)Math.Round(x), ["z"] = (int)Math.Round(z), ["squad"] = squad, ["role"] = role, ["via"] = via.Length == 0 ? "town lists the squad" : "town override " + via });
            }
            var bountyData = new SortedDictionary<string, object?>();
            string bountyNote = "";
            if (bounty)
            {
                var refs = record.GetReferences("bounty factions");
                var facs = refs.Count > 0 ? [.. refs.Select(r => FactionNameOf(db, r.TargetStringId)).Where(n => n.Length > 0)]
                    : new List<string> { FactionNameOf(db, "defaultEmpireFactionSID") is { Length: > 0 } de ? de + " (default)" : "the default empire faction" };
                bountyData["chancePct"] = chance;
                bountyData["amount"] = amount;
                bountyData["amountMax"] = amount + fuzz;
                bountyData["factions"] = facs;
                bountyNote = $" Bounty {(fuzz > 0 ? $"{amount} to {amount + fuzz}" : amount.ToString(CultureInfo.InvariantCulture))} with {string.Join(", ", facs)} ({(chance >= 100 ? "always" : chance + " % of spawns")}).";
            }
            bool join = record.Type == FcsRecordType.CHARACTER && CanJoin(record, uses.Where(u => u.Role == "leader").Select(u => u.Template));
            var camps = campaignsOf.GetValueOrDefault(record.StringId) ?? [];
            var data = new SortedDictionary<string, object?>
            {
                ["unique"] = unique,
                ["recruitable"] = join,
                ["squads"] = uses.Select(u => $"{u.Template.Name} ({u.Role})").Distinct().Take(12).ToList(),
                ["homes"] = homes.Take(12).ToList(),
                ["campaigns"] = camps.Distinct().Take(8).ToList(),
                ["replacement"] = Names(db, record.GetReferences("unique replacement spawn").Select(r => r.TargetStringId)),
                ["record"] = record.StringId,
            };
            if (bounty) data["bounty"] = bountyData;
            bool animal = record.Type == FcsRecordType.ANIMAL_CHARACTER;
            string where = first is null ? " No town lists their squad in the data (roaming, campaign, or placed another way)." : homes.Count == 1 ? $" Home: {firstTown} (position is the town's, not live)." : $" Homes: {string.Join(", ", homes.OfType<SortedDictionary<string, object?>>().Select(h => (string)h["town"]!).Distinct().Take(6))} (position is the first town's, not live).";
            rows.Add(new Row(id(record.Name, record.StringId), record.Name, animal ? "animal-character" : "character", faction.Length == 0 ? null : faction, first is { } f0 ? $"zone-{WorldLayout.ZoneOf(f0.X, f0.Z).X}-{WorldLayout.ZoneOf(f0.X, f0.Z).Y}" : null,
                first?.X, first?.Z, first is null ? "Observed" : "Inferred",
                (unique ? "Unique character" : "Character with a spawn bounty") + (faction.Length > 0 ? $" of {faction}" : "") + "." + bountyNote + where + (join ? " Has a dialogue line that makes them join your squad (recruit or hire)." : "") + (camps.Count > 0 ? $" Special leader of campaign {camps[0]}." : ""),
                record.StringId, data));
        }
        return rows;
    }
}

using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Host.Input;
using Meitou.Simulation;
using Meitou.Simulation.Player;

namespace Meitou.Game.Ui;

/// <summary>
/// The context menu (the original's ContextMenuGUI, <c>Kenshi_ContextMenu</c>; docs/game/ui-input.md "Context menu verbs"): holding the command
/// (right) button on a target for <see cref="ContextHoldSeconds"/> opens a menu of the orders the selection can give it, titled with its name; a
/// quicker click gives the default order at once. The menu lives while that button is held: letting it go over a verb gives it, anywhere else closes
/// the menu (<see cref="ContextMenuRelease"/>). The verbs carry the original's labels and are listed in the original's order-number order
/// (<see cref="VerbOrders"/>); one method per target kind builds them: characters here, containers and doors below, sites in BuildMode, rocks in
/// FurnitureUse.
/// </summary>
sealed class ContextMenuWindow(PlayerInterface player)
{
    /// <summary>
    /// How long the command button is held on a target before the menu opens (<b>Observed</b>: 0.2 s, FUN_140800250).
    /// </summary>
    public const double ContextHoldSeconds = 0.2;
    /// <summary>
    /// The hold on a squad bar portrait that opens its member's menu (<c>MainBarGUI</c>'s frame update, <c>DAT_141696c80</c>).
    /// </summary>
    public const double PortraitHoldSeconds = 0.25;

    internal sealed record ContextMenu(string Title, Vector2 At, IReadOnlyList<(string Caption, Action Give)> Verbs);
    internal ContextMenu? OpenMenu;
    // A right press on a target that has verbs: its menu (built again when the hold ends), where, when, the hold that opens it and the order a quick
    // click gives (null: none).
    (Func<Vector2, ContextMenu?> Menu, Vector2 At, long Since, double Hold, Action? Quick)? _contextPending;

    public bool ContextMenuOpen => OpenMenu is not null;

    /// <summary>
    /// The original's order number behind each verb caption (<c>FUN_14079b2d0</c>'s labels; where several orders share a label, the one the menu
    /// offers): the menu lists its verbs in ascending order number, as the original's set of orders. Meitou's own verbs (not in the table) follow, in
    /// the order they were added.
    /// </summary>
    static readonly Dictionary<string, int> VerbOrders = new()
    {
        ["Build"] = 2,
        ["Steal"] = 3,
        ["Attack Target"] = 5,
        ["Talk To"] = 12,
        ["Attack All"] = 16,
        ["First Aid"] = 25,
        ["Inventory"] = 26,
        ["Trade"] = 26,
        ["Loot"] = 26,
        ["Stand Up"] = 28,
        ["Follow"] = 31,
        ["Bodyguard"] = 45,
        ["Repairs"] = 60,
        ["Attack Unprovoked"] = 61,
        ["Put Down"] = 69,
        ["Open"] = 72,
        ["Close"] = 73,
        ["Pick Lock"] = 76,
        ["Lock"] = 77,
        ["Unlock"] = 78,
        ["Use"] = 87,
        ["Repair"] = 95,
        ["Dismantle"] = 96,
        ["Training"] = 97,
        ["Put in Bed"] = 99,
        ["Get in"] = 107,
        ["Put in"] = 108,
        ["Knock Out"] = 109,
        ["Set Free"] = 110,
        ["Break Out"] = 111,
        ["Auto-Haul"] = 124,
        ["Corpse Disposal"] = 136,
        ["Man Turret"] = 146,
        ["Capture"] = 166,
        ["Use Tools"] = 185,
        ["Use Strength"] = 186,
        ["Unlock Shackles"] = 201,
        ["Escape"] = 207,
        ["Throw out of home"] = 223,
        ["Pick Up"] = 225,
        ["Smash"] = 226,
        ["Stealth KO"] = 228,
        ["Stealth Kill"] = 229,
        ["Shoot At"] = 235,
        ["Kidnap"] = 246,
        ["Splint Injuries"] = 249,
        ["Escape (skill)"] = 251,
        ["Escape (strength)"] = 252,
        ["Sleep"] = 258,
        ["Throw out of town"] = 273,
    };

    /// <summary>
    /// The only orders an animals-only selection is offered (<c>FUN_1407f23b0</c>): attack, attack unprovoked, bodyguard, follow and the smash
    /// orders.
    /// </summary>
    static readonly HashSet<int> AnimalVerbOrders = [5, 61, 45, 31, 123, 226, 227];

    /// <summary>
    /// A caption with a chance column: the menu shows <paramref name="chance"/> (0..100) beside the verb as the original's <c>Value</c> box
    /// (<c>FUN_1407f65e0</c>).
    /// </summary>
    static string WithChance(string caption, float chance) => caption + "\t" + Math.Clamp(chance, 0, 100).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The Pick Lock row with its chance column (verb 76, <c>FUN_1407f65e0</c>, <b>Verified</b>): the lock's pick chance
    /// (<see cref="Meitou.Simulation.Locks.Lockpicking.Chance"/> of its <paramref name="level"/> and the lockpicking of the selected character
    /// nearest <paramref name="at"/>) in per cent; a building's lock (<paramref name="building"/>: not a caged character's) below the minimum pick
    /// chance shows 0 (<c>DAT_142133f28</c>, taken to be CONSTANTS <c>minimum lockpick chance</c>, <b>Observed</b>: the allowed test
    /// <c>FUN_1408ca120</c> compares with it).
    /// </summary>
    internal string PickLockCaption(Vector3 at, int level, bool building)
    {
        if (LockChance(at, level) is not { } chance) return "Pick Lock";
        if (building && chance < (player.SquadWindows.SquadLimits?.Invoke()?.MinimumLockpickChance ?? 0.05f))
            chance = 0;

        return WithChance("Pick Lock", chance * 100);
    }

    /// <summary>
    /// The pick chance (0..1, no minimum) of a lock of <paramref name="level"/> at <paramref name="at"/> for the selected character nearest it; null
    /// with nobody selected.
    /// </summary>
    internal float? LockChance(Vector3 at, int level) =>
        NearestSelected(at, player.Selected()) is { } lead ? Simulation.Locks.Lockpicking.Chance(level, lead.LockpickSkill) : null;

    /// <summary>
    /// The one of <paramref name="candidates"/> nearest <paramref name="at"/>; null when there is none.
    /// </summary>
    static CharacterSnapshot? NearestSelected(Vector3 at, IEnumerable<CharacterSnapshot> candidates) =>
        candidates.MinBy(c => Vector3.DistanceSquared(c.Position, at));

    static (string Caption, float? Chance) SplitChance(string caption) =>
        caption.IndexOf('\t') is var tab and >= 0 && float.TryParse(caption.AsSpan(tab + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float c) ? (caption[..tab], c) : (caption, null);

    static int? OrderOf(string caption) => VerbOrders.TryGetValue(SplitChance(caption).Caption, out int order) ? order : null;

    /// <summary>
    /// The menu for <paramref name="verbs"/>, as the original fills it: an animals-only selection keeps only its orders, the rest sorted by order
    /// number; null when none is left.
    /// </summary>
    internal ContextMenu? Menu(string title, Vector2 at, List<(string Caption, Action Give)> verbs)
    {
        var selected = player.Selected();
        IEnumerable<(string Caption, Action Give)> kept = verbs;
        if (selected.Count > 0 && selected.All(TradeWindows.IsAnimal))
            kept = kept.Where(v => OrderOf(v.Caption) is { } o && AnimalVerbOrders.Contains(o));

        var list = kept.Select((v, i) => (v, i)).OrderBy(x => OrderOf(x.v.Caption) ?? int.MaxValue).ThenBy(x => x.i).Select(x => x.v).ToList();
        return list.Count == 0 ? null : new ContextMenu(title, at, list);
    }

    /// <summary>
    /// The verbs the selection has for a character (<c>FUN_1407a6bc0</c>, docs/game/ui-input.md "Context menu verbs"), in the original's labels.
    /// Prisoners, carried bodies, the player's own, hostile and other characters each get their branch's verbs; then the medic verbs and the shackle
    /// verbs.
    /// </summary>
    internal List<(string Caption, Action Give)> Verbs(CharacterSnapshot target)
    {
        var verbs = new List<(string, Action)>();
        var selection = player.SelectedIds();
        if (selection.Count == 0) return verbs;

        var id = target.Id;
        bool down = target.Down;   // vfunc 0x30, FUN_1405c9e10, from the snapshot
        var carriers = player.Selected().Where(c => c.Carrying == id).Select(c => c.Id).ToArray();

        // A carried body: carried by a selected character, its inventory (26, the branch's own verb) and Put Down (and Meitou's cage stand-ins);
        // carried by someone not of the player's, nothing (FUN_1407a6bc0 empties the list). No pick-up verb for a carried body: the non-hostile
        // branches test +0x3d4, and a hostile one's Kidnap (246) cannot snatch.
        if (carriers.Length > 0)
        {
            if (target.CanOpenInventory) verbs.Add(InventoryVerb(target));

            verbs.Add(("Put Down", () => player.Say(new Simulation.Carrying.PutDownOrder(carriers) { Tick = player.Session.World.Tick })));

            // Meitou stand-in for the original's cage use (docs/simulation.md "Arrest and jail"): carry the body to the nearest free cage.
            verbs.Add(("Put in Cage", () => player.Say(new Simulation.Crime.PutInCageOrder(carriers) { Tick = player.Session.World.Tick })));

            // Meitou stand-in for the bed's own "Put in Bed" (FurnitureUse): carry the body to the bed the bed search finds.
            verbs.Add(("Put in Bed", () => player.Say(new Simulation.Locks.PutInBedOrder(carriers) { Tick = player.Session.World.Tick })));
            return verbs;
        }

        if (!target.CarriedBy.IsNone && player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == target.CarriedBy) is not { IsPlayer: true })
            return verbs;
        if (target.Prison is { } prison) PrisonerVerbs(verbs, target, prison, selection, down);
        else if (target.IsPlayer) OwnCharacterVerbs(verbs, target, selection, down);
        else OtherCharacterVerbs(verbs, target, selection, down);

        // The medic verbs, unless it is dead or a hostile one standing (the original: the lead's enemy test, vtable +0x3e8).
        if (target.Body is { Dead: false } && (down || target.IsPlayer || !player.PointerPanel.Hostile(target)))
        {
            // Repairs (60, the robot kit's need) and First Aid (25, the organic need) are separate tests (FUN_1407a6bc0 calls FUN_1407c05a0 with kind
            // 1 then 0); one order serves both kits.
            if (target.Body is { NeedsRepairs: true })
                verbs.Add(("Repairs", () => player.Say(new Simulation.FirstAid.TreatOrder(selection, id) { Tick = player.Session.World.Tick })));
            if (target.Body is { NeedsOrganicAid: true })
                verbs.Add(("First Aid", () => player.Say(new Simulation.FirstAid.TreatOrder(selection, id) { Tick = player.Session.World.Tick })));
            if (target.Body is { NeedsSplint: true })
                verbs.Add(("Splint Injuries", () => player.Say(new Simulation.FirstAid.SplintOrder(selection, id) { Tick = player.Session.World.Tick })));
        }

        // Locked shackles (the original's verbs 201, 185, 186): pick the lock, saw them off with a tool, or break them by strength; the wearer's own
        // or a mate's (another faction's: a crime).
        if (target.ShacklesLocked && target.Body is not { Dead: true })
        {
            verbs.Add(("Unlock Shackles", () => player.Say(new Simulation.Locks.ShackleOrder(Workers(), id, Simulation.Locks.ShackleWork.Pick) { Tick = player.Session.World.Tick })));
            verbs.Add(("Use Tools", () => player.Say(new Simulation.Locks.ShackleOrder(Workers(), id, Simulation.Locks.ShackleWork.Cut) { Tick = player.Session.World.Tick })));
            verbs.Add(("Use Strength", () => player.Say(new Simulation.Locks.ShackleOrder(Workers(), id, Simulation.Locks.ShackleWork.Break) { Tick = player.Session.World.Tick })));

            // The wearer works its own shackles when it is selected, else the selection.
            IReadOnlyList<CharacterId> Workers() => selection.Contains(id) ? [id] : selection;
        }

        // Meitou stand-in for dragging shackles onto a down or caged character's armour slot: a selected character carrying some puts them on,
        // locked.
        if (target.CanBeShackled && player.Selected().Any(c => c.CarriesShackles))
            verbs.Add(("Put on Shackles", () => player.Say(new Simulation.Locks.PutShacklesOrder(player.Selected().Where(c => c.CarriesShackles).Select(c => c.Id).ToArray(), id) { Tick = player.Session.World.Tick })));

        return [.. verbs.Select(v => (v.Item1, player.SquadWindows.JobOrOrder(v.Item1, id, v.Item2)))];   // Shift turns an order into a job (SquadPanel)
    }

    /// <summary>
    /// A caged character: Talk To (12) and Loot (26). Meitou points at an occupied cage with occupant selection as its occupant and shows one menu,
    /// so the cage's own verbs come here (engine stand-ins for the original's cage menu beside the occupant's): Unlock or Lock the player's own cage
    /// (78, 77), Pick Lock another faction's locked one (the prisoner its own, a mate from outside), Use Tools and Use Strength on it (285, 286, from
    /// outside), and once it is open Escape (207, the prisoner when selected; an escape unless served), or Set Free (110) and Pick Up (225, a mate
    /// lifts it out; out of another faction's cage a crime, locks.md "Cages and beds").
    /// </summary>
    void PrisonerVerbs(List<(string, Action)> verbs, CharacterSnapshot target, PrisonView prison, IReadOnlyList<CharacterId> selection, bool down)
    {
        var id = target.Id;
        if (!down && player.Session.World.CanTalk(id, selection[0]))
            verbs.Add(("Talk To", () => player.Say(new Simulation.Dialogue.TalkOrder(selection[0], id) { Tick = player.Session.World.Tick })));

        // 26 unless it is the player's and selected: Trade for the player's own, else Loot (a pickpocket while it is awake).
        if (target.CanOpenInventory && !(target.IsPlayer && selection.Contains(id))) verbs.Add(InventoryVerb(target));

        bool ownCage = prison.CageOwner == player.Session.World.Player.Faction;

        // The player's own cage has Unlock (78) or Lock (77) by its lock (FUN_1407a6bc0), worked from outside.
        if (ownCage && prison.CageId is { } own)
            verbs.Add((prison.Locked ? "Unlock" : "Lock", () => player.Say(new Simulation.Locks.DoorLockOrder([], own, !prison.Locked) { Tick = player.Session.World.Tick })));

        if (prison.Locked)
        {
            if (!down && !ownCage)
                verbs.Add((prison.LockLevel < 0 ? "Pick Lock" : PickLockCaption(target.Position, prison.LockLevel, false), () => player.Say(new Simulation.Locks.PickLockOrder(selection, id) { Tick = player.Session.World.Tick })));

            // Another faction's locked cage also has Use Tools and Use Strength (285, 286; FUN_1407a6bc0 tests the lock and owner, not the occupant),
            // worked from outside.
            if (prison.CageId is { } cage && !ownCage)
            {
                verbs.Add(("Use Tools", () => player.Say(new Simulation.Locks.ForceLockOrder([], cage, Simulation.Locks.LockForce.Tools) { Tick = player.Session.World.Tick })));
                verbs.Add(("Use Strength", () => player.Say(new Simulation.Locks.ForceLockOrder([], cage, Simulation.Locks.LockForce.Strength) { Tick = player.Session.World.Tick })));
            }

            // The player's own cage frees its prisoner while locked too (the release unlocks it); never a dead or knocked-out one (Set Free wants a
            // conscious target, precondition 125).
            if (!selection.Contains(id) && !down && target.CarriedBy.IsNone && ownCage)
                verbs.Add(("Set Free", () => player.Say(new Simulation.Locks.SetFreeOrder([], id) { Tick = player.Session.World.Tick })));
        }
        else if (selection.Contains(id)) { if (!down)
            verbs.Add(("Escape", () => player.Say(new Simulation.Locks.GetOutOrder([id]) { Tick = player.Session.World.Tick }))); }
        else if (target.CarriedBy.IsNone)
        {
            // Open, whoever's cage: Set Free (110) and the pick-up verb. The pointer picks the occupant over its cage, so the cage's own menu,
            // which offers Set Free on these terms, is not reached while it is occupied (#435).
            if (!down)
                verbs.Add(("Set Free", () => player.Say(new Simulation.Locks.SetFreeOrder([], id) { Tick = player.Session.World.Tick })));

            verbs.Add(PickUpVerb(target));
        }
    }

    /// <summary>
    /// One of the player's own characters: another one than the selection is followed (31) or guarded (45, <see cref="FollowOrder.Guard"/>), its
    /// inventory opened beside the selection's (26, labelled Trade for the player's own), and picked up (225), standing too when it carries no one
    /// (<c>FUN_1407a6bc0</c>, <b>Observed</b>). A selected one crawling by choice gets Stand Up (28, the selection's order).
    /// </summary>
    void OwnCharacterVerbs(List<(string, Action)> verbs, CharacterSnapshot target, IReadOnlyList<CharacterId> selection, bool down)
    {
        var id = target.Id;

        if (selection.Contains(id))
        {
            if (target.StayingLow)
                verbs.Add(("Stand Up", () => player.Say(new StandUpCommand(selection) { Tick = player.Session.World.Tick })));

            return;
        }

        // Bodyguard (45): follows and defends the target; with Shift it is the Bodyguard job (SquadPanel).
        verbs.Add(("Bodyguard", () => player.Say(new FollowOrder(selection, id) { Guard = true, Tick = player.Session.World.Tick })));
        verbs.Add(("Follow", () => player.Say(new FollowOrder(selection, id) { Tick = player.Session.World.Tick })));
        if (target.CarriedBy.IsNone && (down || target.Carrying.IsNone))
            verbs.Add(("Pick Up", () => player.Say(new Simulation.Carrying.PickUpOrder([], id) { Tick = player.Session.World.Tick })));
        if (target.CanOpenInventory) verbs.Add(InventoryVerb(target));
    }

    /// <summary>
    /// Loot (26), or Trade for the player's own character: opens the target's inventory beside the selection's. Offered when
    /// <see cref="CharacterSnapshot.CanOpenInventory"/> says the simulation will.
    /// </summary>
    (string, Action) InventoryVerb(CharacterSnapshot target)
    {
        var id = target.Id;

        void Open() => player.Say(new Simulation.Carrying.OpenLootOrder([], id) { Tick = player.Session.World.Tick });

        // With Shift the verb is an order on the target like any other: Trade on an own character (a character hand, not a building) falls through to
        // the TaskData lookup of order 26, so it adds the Foraging animals job like Loot does (FUN_1407fa650, Verified), not the window.
        return (target.IsPlayer ? "Trade" : "Loot", () => player.SquadWindows.OrderOrJob(Data.Gameplay.Ai.TaskType.LootTarget, id, Open));
    }

    /// <summary>
    /// Throw out of town (273) or home (223), exclusive (<c>FUN_140793d80</c>, docs/game/ui-input.md): in a town of the player's (the town the target
    /// stands in) 273 is offered when the target is inside the town walls and the lead does not regard it as friendly; elsewhere 223 when the
    /// character lies in one of the player's buildings (Meitou: <see cref="CharacterSnapshot.InPlayerBuilding"/>). Both are always jobs (the dispatch
    /// makes them one with or without Shift), carried out by the intruder errands.
    /// </summary>
    void ThrowOutVerb(List<(string, Action)> verbs, CharacterSnapshot target, IReadOnlyList<CharacterId> selection)
    {
        var id = target.Id;

        if (player.Selected().LastOrDefault() is { } lead && player.Session.World.VerbFactsOf(lead.Id, id) is { Town: TownOwnership.Player } facts)
        {
            if (facts.InsideWalls && !facts.LeadRegards)
                verbs.Add(("Throw out of town", () => player.Say(new Simulation.Production.AddOrderJob(selection, Data.Gameplay.Ai.TaskType.PickupIntrudersTown, id) { Tick = player.Session.World.Tick })));

            return;
        }

        if (!target.InPlayerBuilding) return;

        verbs.Add(("Throw out of home", () => player.Say(new Simulation.Production.AddOrderJob(selection, Data.Gameplay.Ai.TaskType.PickupIntruders, id) { Tick = player.Session.World.Tick })));
    }

    /// <summary>
    /// Another faction's character (docs/game/ui-input.md "Context menu verbs"). Standing: Talk To (12), Stealth KO (228, with its chance) while a
    /// selected character sneaks; against a hostile one Attack All (16) and Attack Target (5), against anyone else Attack Unprovoked (61), Bodyguard
    /// (45) and Follow (31); Meitou adds Trade with a trader (its quick order). With the lead on a turret every attack becomes Shoot At (235). Down
    /// or asleep: Loot (26, a pickpocket on a sleeper), the pick-up verb (<see cref="PickUpVerb"/>) and Throw out of home or town
    /// (<see cref="ThrowOutVerb"/>).
    /// </summary>
    void OtherCharacterVerbs(List<(string, Action)> verbs, CharacterSnapshot target, IReadOnlyList<CharacterId> selection, bool down)
    {
        var id = target.Id;

        if (down)
        {
            if (target.CanOpenInventory) verbs.Add(InventoryVerb(target));
            if (!target.CarriedBy.IsNone) return;   // no pick-up verb or Throw out of home for a carried body

            verbs.Add(PickUpVerb(target));
            if (target.StealthKoChance is { } downChance)   // 228 on a down character too (FUN_1407a6bc0:583-594)
                verbs.Add((WithChance("Stealth KO", downChance), () => player.Say(new Simulation.Carrying.StealthKoOrder(selection, id) { Tick = player.Session.World.Tick })));

            ThrowOutVerb(verbs, target, selection);
            return;
        }

        if (target.Asleep)
        {
            // Not standing (the original's state 1): Loot (a pickpocket), the pick-up verb and Stealth KO; not hostile, also Attack Unprovoked,
            // Bodyguard and Talk To.
            if (target.CanOpenInventory) verbs.Add(InventoryVerb(target));

            verbs.Add(PickUpVerb(target));
            ThrowOutVerb(verbs, target, selection);
            if (target.StealthKoChance is { } koChance)
                verbs.Add((WithChance("Stealth KO", koChance), () => player.Say(new Simulation.Carrying.StealthKoOrder(selection, id) { Tick = player.Session.World.Tick })));
            if (player.PointerPanel.Hostile(target)) return;

            verbs.Add(("Attack Unprovoked", () => player.Say(new Simulation.Combat.AttackOrder(selection, id) { Tick = player.Session.World.Tick, Picked = true, Unprovoked = true })));
            verbs.Add(("Bodyguard", () => player.Say(new FollowOrder(selection, id) { Guard = true, Tick = player.Session.World.Tick })));
            if (player.Session.World.CanTalk(id, selection[0]))
                verbs.Add(("Talk To", () => player.Say(new Simulation.Dialogue.TalkOrder(selection[0], id) { Tick = player.Session.World.Tick })));

            return;
        }

        bool onTurret = player.Selected().LastOrDefault() is { Turret: not null };
        bool hostile = player.PointerPanel.Hostile(target);
        if (player.Session.World.CanTalk(id, selection[0]))
            verbs.Add(("Talk To", () => player.Say(new Simulation.Dialogue.TalkOrder(selection[0], id) { Tick = player.Session.World.Tick })));

        // The customer is the selected character nearest the trader that stands (as the quick order's lead), not the first selected.
        if (target.IsTrader && !hostile && NearestSelected(target.Position, player.Selected().Where(c => !c.Down)) is { } customer)
            verbs.Add(("Trade", () => player.Say(new Simulation.Trading.OpenTrade(customer.Id, id) { Tick = player.Session.World.Tick })));

        if (onTurret)
        {
            // The lead mans a turret: the attacks give way to Shoot At (235), which sends every selected gunner's fire at the target.
            var gunners = player.Selected().Where(c => c.Turret is not null).Select(c => c.Id).ToArray();
            verbs.Add(("Shoot At", () => player.Say(new Simulation.Combat.ShootAtOrder(gunners, id) { Tick = player.Session.World.Tick })));
            return;
        }

        if (target.StealthKoChance is { } chance)
            verbs.Add((WithChance("Stealth KO", chance), () => player.Say(new Simulation.Carrying.StealthKoOrder(selection, id) { Tick = player.Session.World.Tick })));

        if (hostile)
        {
            // Fighting back at someone attacking the squad is defence, not an assault on its faction (as the quick order).
            verbs.Add(("Attack All", () => AttackAll(selection, target)));
            verbs.Add(("Attack Target", () => player.Say(new Simulation.Combat.AttackOrder(selection, id) { Tick = player.Session.World.Tick, Picked = true, Defensive = target.FightsPlayer })));
            return;
        }

        verbs.Add(("Attack Unprovoked", () => player.Say(new Simulation.Combat.AttackOrder(selection, id) { Tick = player.Session.World.Tick, Picked = true, Unprovoked = true })));
        verbs.Add(("Bodyguard", () => player.Say(new FollowOrder(selection, id) { Guard = true, Tick = player.Session.World.Tick })));
        verbs.Add(("Follow", () => player.Say(new FollowOrder(selection, id) { Tick = player.Session.World.Tick })));
        if (!target.IsDiplomat && player.Selected().LastOrDefault() is { } lead && player.Session.World.VerbFactsOf(lead.Id, id) is { RegardsLead: true })
            verbs.Add(("Pick Up", () => player.Say(new Simulation.Carrying.PickUpOrder([], id) { Tick = player.Session.World.Tick })));   // a plain 225 here: never Kidnap (FUN_1407a6bc0)
    }

    /// <summary>
    /// How far from the clicked enemy Attack All looks for the others (an engine choice: the original's order 16 is "Attacking enemies", whose reach
    /// is <b>Unknown</b>).
    /// </summary>
    const float AttackAllRadius = 800;

    /// <summary>
    /// Attack All (16, "Attacking enemies"; <b>engine</b> reading): every standing hostile character within <see cref="AttackAllRadius"/> of the
    /// clicked one is a target, and each selected character attacks the nearest of them to itself.
    /// </summary>
    void AttackAll(IReadOnlyList<CharacterId> selection, CharacterSnapshot clicked)
    {
        var snapshot = player.Session.CurrentSnapshot;
        var foes = snapshot.Characters
            .Where(c => c.Id == clicked.Id || player.PointerPanel.StandingHostile(c) && Vector3.Distance(c.Position, clicked.Position) <= AttackAllRadius)
            .ToList();
        var groups = new Dictionary<CharacterId, List<CharacterId>>();

        foreach (var id in selection)
        {
            if (snapshot.Characters.FirstOrDefault(c => c.Id == id) is not { } mine) continue;

            var foe = foes.MinBy(f => Vector3.DistanceSquared(f.Position, mine.Position))!;
            if (!groups.TryGetValue(foe.Id, out var list)) groups[foe.Id] = list = [];

            list.Add(id);
        }

        foreach (var (foeId, attackers) in groups)
            player.Say(new Simulation.Combat.AttackOrder(attackers, foeId) { Tick = player.Session.World.Tick, Defensive = foes.First(f => f.Id == foeId).FightsPlayer });
    }

    /// <summary>
    /// The pick-up verb (<c>FUN_140796f80</c>, docs/game/ui-input.md "Context menu verbs"): Pick Up (225) unless every gate passes for Kidnap (246):
    /// the target is of another faction than the lead, stands in a town of a faction that is not the player's, is of a real faction, does not regard
    /// the lead as friendly (or is a trader squad's member or in a campaign force), has no bounty with its town's law and is no Bandit, and is not
    /// caged. Kidnap shows the chance column (<c>Character::getKidnappingChance</c>) and gives the kidnap order (a sleeper too; the roll on arrival,
    /// docs/game/carry-loot.md "Kidnapping").
    /// </summary>
    (string, Action) PickUpVerb(CharacterSnapshot target)
    {
        var id = target.Id;
        if (target.Prison is null && player.Selected().LastOrDefault() is { } lead && KidnapOffered(lead, target))
            return (WithChance("Kidnap", target.KidnapChance ?? 100), () => player.Say(new Simulation.Carrying.PickUpOrder([], id) { Tick = player.Session.World.Tick, Kidnap = true }));

        return ("Pick Up", () => player.Say(new Simulation.Carrying.PickUpOrder([], id) { Tick = player.Session.World.Tick }));
    }

    /// <summary>
    /// The pick-up verb's Kidnap branch (<c>FUN_140796f80</c>); without the world's facts (no population) it is not offered.
    /// </summary>
    bool KidnapOffered(CharacterSnapshot lead, CharacterSnapshot target)
    {
        if (target.Faction == lead.Faction || player.Session.World.VerbFactsOf(lead.Id, target.Id) is not { } facts)
            return false;
        if (facts.Town != TownOwnership.Other || facts.NotReal) return false;
        if (facts.RegardsLead && !facts.SquadTrader && !target.WarParty) return false;

        return facts.Bounty < 1 && !facts.Bandit;
    }

    /// <summary>
    /// A container (the original's building branch for a storage with an inventory): unlocked, Inventory (the player's) or Loot (26); another
    /// faction's locked one, Pick Lock (76). The menu's orders skip the quick click's Alt test (<c>FUN_1407fa650</c>), so Loot on another faction's
    /// container takes from it at once (a shop container's stock is stolen from); a locked one also has Use Tools and Use Strength (285, 286). Not
    /// built: Auto-Haul (124) on an interior container.
    /// </summary>
    internal List<(string Caption, Action Give)> ContainerVerbs(Simulation.Locks.ContainerSpot container)
    {
        var verbs = new List<(string, Action)>();
        if (player.SelectedIds().Count == 0) return verbs;

        bool own = container.Owner == player.Session.World.Player.Faction;

        if (!container.Locked) verbs.Add((own ? "Inventory" : "Loot", () => OpenContainer(container.Id, true)));
        else
        {
            if (!own && container.Owner >= 0)
                verbs.Add((PickLockCaption(container.Position, container.Level, true), () => OpenContainer(container.Id, true)));

            verbs.Add(("Use Tools", () => player.Say(new Simulation.Locks.ForceLockOrder([], container.Id, Simulation.Locks.LockForce.Tools) { Tick = player.Session.World.Tick })));
            verbs.Add(("Use Strength", () => player.Say(new Simulation.Locks.ForceLockOrder([], container.Id, Simulation.Locks.LockForce.Strength) { Tick = player.Session.World.Tick })));
        }

        return verbs;
    }

    /// <summary>
    /// The open order for a container (Alt: another faction's locked one is picked, a shop's taken from).
    /// </summary>
    internal void OpenContainer(string id, bool alt)
    {
        player.Production.ClosedMachine = null;   // a container whose window was closed opens again when it is asked for (the sim may not have dropped it yet, e.g. while paused)
        player.Say(new Simulation.Items.OpenContainerOrder([], id, alt) { Tick = player.Session.World.Tick });
    }

    /// <summary>
    /// A door (the original's door branch): unlocked, the player's own gets Lock (77), every one Open (72) or Close (73); locked, the player's Unlock
    /// (140), another faction's Pick Lock (76). Lock and Unlock are task orders on this one door
    /// (<see cref="Meitou.Simulation.Locks.DoorLockOrder"/>: the nearest selected character walks there and works 3.3 s); the panel's buttons toggle
    /// the building. A locked one also has Use Tools, Use Strength and Smash (285, 286, 226: <see cref="Meitou.Simulation.Locks.ForceLockOrder"/>).
    /// </summary>
    internal List<(string Caption, Action Give)> DoorVerbs((Simulation.Locks.DoorPick Door, bool Locked) pick)
    {
        var verbs = new List<(string, Action)>();
        if (player.SelectedIds().Count == 0) return verbs;

        var door = pick.Door;
        bool own = door.Owner == player.Session.World.Player.Faction;

        // A broken door is repaired (95, the building branch of FUN_1407fc6e0); the job of Shift is not built (the Engineer job works sites only).
        if (player.Furniture.DoorBroken(door))
        {
            if (own)
                verbs.Add(("Repair", () => player.Say(new Simulation.Construction.BuildOrder([], door.Id) { Tick = player.Session.World.Tick })));

            return verbs;
        }

        if (pick.Locked)
        {
            if (own)
                verbs.Add(("Unlock", () => player.Say(new Simulation.Locks.DoorLockOrder([], door.Id, false) { Tick = player.Session.World.Tick })));
            else verbs.Add((PickLockCaption(door.Position, player.Session.CurrentSnapshot.Doors.GetValueOrDefault((door.Building, door.Door)).Level, true), () => player.Say(new Simulation.Locks.DoorOrder([], door.Id, true) { Tick = player.Session.World.Tick })));

            verbs.Add(("Use Tools", () => player.Say(new Simulation.Locks.ForceLockOrder([], door.Id, Simulation.Locks.LockForce.Tools) { Tick = player.Session.World.Tick })));
            verbs.Add(("Use Strength", () => player.Say(new Simulation.Locks.ForceLockOrder([], door.Id, Simulation.Locks.LockForce.Strength) { Tick = player.Session.World.Tick })));
            verbs.Add(("Smash", () => player.Say(new Simulation.Locks.ForceLockOrder([], door.Id, Simulation.Locks.LockForce.Smash) { Tick = player.Session.World.Tick })));
            return verbs;
        }

        if (own)
            verbs.Add(("Lock", () => player.Say(new Simulation.Locks.DoorLockOrder([], door.Id, true) { Tick = player.Session.World.Tick })));

        bool open = player.Session.CurrentSnapshot.Doors.GetValueOrDefault((door.Building, door.Door)).Motion is Simulation.Locks.DoorMotion.Open or Simulation.Locks.DoorMotion.Opening;
        verbs.Add((open ? "Close" : "Open", () => player.Say(new Simulation.Locks.DoorOrder([], door.Id) { Tick = player.Session.World.Tick })));
        return verbs;
    }

    /// <summary>
    /// A right press on a character with verbs starts the hold timer (true); the quick order, if any, waits for the release.
    /// </summary>
    internal bool ContextPress(Vector2 p, Action? quick)
    {
        if (player.Picking.PickWhere(p, _ => true) is not { } id) return false;

        return ContextPressOn(p, at => player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { } target ? Menu(target.Name, at, Verbs(target)) : null, quick);
    }

    /// <summary>
    /// A right press on a target whose <paramref name="menu"/> has verbs (true): the hold timer starts, the menu opens after <paramref name="hold"/>
    /// seconds (<see cref="ContextHoldSeconds"/> by default) and <paramref name="quick"/> waits for the release.
    /// </summary>
    internal bool ContextPressOn(Vector2 p, Func<Vector2, ContextMenu?> menu, Action? quick, double hold = ContextHoldSeconds)
    {
        if (menu(p) is null) return false;

        _contextPending = (menu, p, System.Diagnostics.Stopwatch.GetTimestamp(), hold, quick);
        return true;
    }

    /// <summary>
    /// Once a frame: a press held long enough opens the menu.
    /// </summary>
    internal void UpdateContextMenu()
    {
        if (_contextPending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < pending.Hold)
            return;

        _contextPending = null;
        OpenMenu = pending.Menu(pending.At);
    }

    /// <summary>
    /// The right button let go: a press released before the menu opened gives the quick order.
    /// </summary>
    internal void ContextRelease()
    {
        if (_contextPending is not { } pending) return;

        _contextPending = null;
        pending.Quick?.Invoke();
    }

    /// <summary>
    /// A mouse button let go while the menu is open (<c>FUN_1407a7f10</c>, <c>FUN_1407ac8c0</c>): a verb row under the cursor gives its order,
    /// whichever button it is (the row's release handler tests none), and the menu closes. The menu opens on the held command button and closes when
    /// that button is let go, so the right release always ends it; a left release over a row acts, elsewhere nothing (the menu stays while the
    /// command button is held). True when the event was the menu's.
    /// </summary>
    internal bool ContextMenuRelease(MouseButton button, Vector2 p)
    {
        if (OpenMenu is null) return false;

        var row = player.UiVisible ? MyGuiPresentation.HitTest(player.ArrangeUi(), p.X, p.Y) : null;

        if (row is { Action: "meitou:context" })
        {
            player.InterfaceSounds.SoundForAction("context", row.Argument);
            ContextAction(row.Argument);
            return true;
        }

        if (button != MouseButton.Right)
            return false;   // another button's release still ends what its press began (a drag, a window move)

        CloseContextMenu();
        return true;
    }

    internal void ContextAction(string argument)
    {
        var menu = OpenMenu;
        OpenMenu = null;
        if (menu is not null && int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < menu.Verbs.Count)
            menu.Verbs[i].Give();
    }

    /// <summary>
    /// Closes the menu (true when one was open).
    /// </summary>
    internal bool CloseContextMenu()
    {
        bool open = OpenMenu is not null;
        OpenMenu = null;
        _contextPending = null;
        return open;
    }

    /// <summary>
    /// The menu as the original lays it out: a floating panel an eighth of the screen wide, the name on top, a button per verb, beside the cursor and
    /// kept on screen. When a verb has a chance, every button keeps the layout's width and the chance shows beside it as "N%", red at 0 to green at
    /// 100 (<c>ContextMenuGUI::show</c>); else the buttons fill the row.
    /// </summary>
    internal void AddContextMenu(List<UiControl> controls)
    {
        if (OpenMenu is not { } menu) return;

        // Kenshi_ContextMenu: Root 0.125 x 0.0685 of the screen for the title and one option, the title 47.3 % of that height, an option the rest; in
        // an option the button at x 0.0208 w 0.7917 and the Value box at x 0.8125 w 0.1875, both y 0.0513 h 0.8974.
        float w = MathF.Max(200, player.Width * 0.125f), full = MathF.Max(64, player.Height * 0.0685185f);
        float titleH = full * 0.472973f, optionH = full - titleH, h = titleH + optionH * menu.Verbs.Count + 6;
        float x = Math.Clamp(menu.At.X + 12, 0, Math.Max(0, player.Width - w)), y = Math.Clamp(menu.At.Y, 0, Math.Max(0, player.Height - h));
        var ink = new Vector4(0.86f, 0.82f, 0.71f, 1);
        bool chances = menu.Verbs.Any(v => SplitChance(v.Caption).Chance is not null);
        float buttonW = chances ? w * 0.791667f : w * (1 - 2 * 0.0208333f);
        controls.Add(new("context-menu", "Widget", new(x, y, w, h), "", "", "Kenshi_FloatingPanelSkin", true, new Dictionary<string, string>(),
            new(0.1f, 0.09f, 0.08f, 0.94f), ink, Pickable: true));
        controls.Add(new("context-title", "TextBox", new(x + 6, y + 2, w - 12, titleH), menu.Title, "", "", true,
            new Dictionary<string, string> { ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Center" }, default, ink));

        for (int i = 0; i < menu.Verbs.Count; i++)
        {
            var (caption, chance) = SplitChance(menu.Verbs[i].Caption);
            float rowY = y + titleH + i * optionH + optionH * 0.0512821f, rowH = optionH * 0.897436f;
            controls.Add(new($"context-verb-{i}", "Button", new(x + w * 0.0208333f, rowY, buttonW, rowH), Loc.Tr(caption),
                "meitou:context", "Kenshi_Button1Skin", true, new Dictionary<string, string> { ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Center" },
                default, ink, Pickable: true, Argument: i.ToString(CultureInfo.InvariantCulture)));
            if (chance is not { } c) continue;

            float p = Math.Clamp(c * 0.01f, 0, 1);
            controls.Add(new($"context-chance-{i}", "TextBox", new(x + w * 0.8125f, rowY, w * 0.1875f, rowH), c.ToString("0", CultureInfo.InvariantCulture) + "%", "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Center" }, default,
                new Vector4(1 - p, p, 0, 1)));
        }
    }

    /// <summary>
    /// The <c>--scene menu</c> harness (ScreenshotScenes): opens at once, with no hold, the context menu of the <paramref name="kind"/> ("character",
    /// "door", "container", "cage") nearest to <paramref name="near"/> that is on screen; its title and verbs are printed. False when there is none
    /// or it has no verbs.
    /// </summary>
    internal bool OpenSceneMenu(string kind, Vector3 near)
    {
        CloseContextMenu();
        var snap = player.Session.CurrentSnapshot;
        (string Title, Vector3 At, List<(string Caption, Action Give)> Verbs)? found = kind switch
        {
            "character" => snap.Characters.Where(c => !c.IsPlayer).OrderBy(c => Vector3.DistanceSquared(c.Position, near)).Select(c => (c.Name, c.Position, Verbs(c))).FirstOrDefault(),
            "door" => snap.DoorPicks.OrderBy(d => Vector3.DistanceSquared(d.Position, near))
                .Select(d => (Loc.Tr("Door"), d.Position, DoorVerbs((d, snap.Doors.GetValueOrDefault((d.Building, d.Door)).Locked)))).FirstOrDefault(),
            "container" => snap.Containers.OrderBy(c => Vector3.DistanceSquared(c.Position, near)).Select(c => (c.Record, c.Position, ContainerVerbs(c))).FirstOrDefault(),
            "cage" => snap.Cages.OrderBy(c => Vector3.DistanceSquared(c.Position, near)).Select(c => (Loc.Tr("Cage"), c.Position, player.Furniture.CageVerbs(c))).FirstOrDefault(),
            _ => null,
        };
        if (found is not { } f || f.Verbs is null || player.Project(f.At) is not { } px) return false;

        OpenMenu = Menu(f.Title, px, f.Verbs);
        Console.WriteLine($"scene     menu {kind} '{f.Title}' at {f.At.X:0}, {f.At.Z:0}: {string.Join(" | ", f.Verbs.Select(v => v.Caption))}");
        return OpenMenu is not null;
    }
}
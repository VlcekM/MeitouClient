using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Crime;

/// <summary>
/// A player character carrying a body to a cage (<see cref="PutInCageOrder"/>; serial state of the <see cref="ArrestSystem"/>).
/// </summary>
public sealed class Caging
{
    public required CharacterId Carrier { get; init; }
    public required CharacterId Captive { get; init; }
    public required string Cage { get; set; }
    public int Tries { get; set; }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Carrier); h.Add(Captive); h.Add(Rng.StableHash(Cage)); h.Add(Tries);
    }
}

/// <summary>
/// A bounty hunter after a player character (economy.md 11.8): chasing until the target is down, then resting from it (<see cref="Rest"/> game
/// seconds left).
/// </summary>
public sealed class Hunt
{
    public required CharacterId Hunter { get; init; }
    public required CharacterId Target { get; init; }
    /// <summary>
    /// Game seconds since the last attack order.
    /// </summary>
    public float Time { get; set; }
    /// <summary>
    /// Above 0: the hunt is over and the hunter leaves this target alone for that many more game seconds.
    /// </summary>
    public float Rest { get; set; }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Hunter); h.Add(Target); h.Add(Time); h.Add(Rest);
    }
}

public sealed partial class ArrestSystem : IOrderOwner
{
    /// <summary>
    /// Bounty hunters go after a bounty above this (their "bounty spotted" dialogues answer only <c>b &gt; 4000</c>, economy.md 11.8, <b>Verified</b>
    /// by probe).
    /// </summary>
    public const int HuntLimit = 4000;
    /// <summary>
    /// The least bail for a prisoner not of the player's faction (economy.md 11.5, <b>Observed</b>).
    /// </summary>
    public const int MinNpcBail = 2000;

    readonly List<Caging> _cagings = [];
    readonly List<Hunt> _hunts = [];

    public IReadOnlyList<Caging> Cagings => _cagings;
    public IReadOnlyList<Hunt> Hunts => _hunts;

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case PayBailOrder bail: PayBail(world, bail.Prisoner); break;
                case PutInCageOrder put:
                    foreach (var id in put.Carriers.Count > 0 ? put.Carriers : world.Player.Selection.ToArray())
                        StartCaging(world, id, put.Cage, put.Queued);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ bail

    /// <summary>
    /// The bail of a caged character (economy.md 11.5, <b>Observed</b>): twice its bounty with the jail (the cage owner's faction), at least
    /// <see cref="MinNpcBail"/> when it is not of the player's faction. Null when none is offered: not caged, or the cage is the player's or of a
    /// lawless faction (<b>engine</b> stand-in for the original's speaker and hostility tests).
    /// </summary>
    public int? BailFor(World world, CharacterCold c)
    {
        if (!c.IsImprisoned || c.Kidnapped || c.InWhat is not { } id || world.Fixtures.Find(id) is not { Kind: FixtureKind.Cage } cage) return null;
        int owner = cage.Owner;
        if (owner < 0 || owner == world.Player.Faction || Crimes.Factions.Lawless(owner)) return null;
        long price = 2L * (c.Bounties?.BookedAmount(Crimes.Factions.Relations, owner) ?? 0);
        if (c.Faction != world.Player.Faction) price = Math.Max(price, MinNpcBail);
        return (int)Math.Min(price, int.MaxValue);
    }

    /// <summary>
    /// The prisoners <paramref name="speaker"/>'s bail list offers (<c>DA_CHOOSE_PRISONER_BAIL</c>'s <c>CharacterTrading_PrisonerBailout</c>,
    /// economy.md 11.5, <b>Observed</b>): the living occupants of cages in the speaker's home town (<b>engine reading</b>: within
    /// <see cref="ArrestSettings.TownSlack"/> times its resident squad's home radius) that are of the player's faction or not hostile to the
    /// speaker's, with their bail (<see cref="BailFor"/>). Empty when the speaker has no town.
    /// </summary>
    public List<(CharacterId Prisoner, string Name, int Price)> BailOffers(World world, CharacterId speaker)
    {
        var offers = new List<(CharacterId, string, int)>();
        var table = world.Characters;
        if (!table.TryResolveNext(speaker, out int s) || table.Cold(s) is not { SquadId: >= 0 } sc
            || world.Squads.Find(sc.SquadId) is not { TownOwn: true } squad)
        {
            return offers;
        }
        float r = squad.HomeRadius * Settings.TownSlack;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { IsImprisoned: true, Medical.Dead: false, InWhat: { } where } pc
                || world.Fixtures.Find(where) is not { } cage)
            {
                continue;
            }
            if (Vector2.DistanceSquared(new Vector2(cage.Position.X, cage.Position.Z), squad.HomeCentre) > r * r) continue;
            if (pc.Faction != world.Player.Faction && Crimes.Relations.Contains(pc.Faction) && Crimes.Relations.Contains(sc.Faction)
                && Crimes.Relations.IsHostile(pc.Faction, sc.Faction))
            {
                continue;
            }
            if (BailFor(world, pc) is { } price) offers.Add((table.IdOf(i), pc.Name, price));
        }
        return offers;
    }

    /// <summary>
    /// The bail list's Pay (<c>FUN_1406ae170</c>): <paramref name="payer"/>'s faction pays <paramref name="prisoner"/>'s bail at the desk, which
    /// frees it. False: not offered or too little money.
    /// </summary>
    public bool PayBail(World world, CharacterId prisoner, CharacterId payer)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(prisoner, out int p) || table.Cold(p) is not { IsImprisoned: true, InWhat: { } where } pc
            || world.Fixtures.Find(where) is not { } cage)
        {
            return false;
        }
        if (BailFor(world, pc) is not { } price || !table.TryResolveNext(payer, out int s)) return false;
        if (world.Player.Money < price)
        {
            world.Post(Loc.Tr("Not enough money: the bail is c.{1}.", price));
            return false;
        }
        Bail(world, p, pc, cage, price, s);
        return true;
    }

    /// <summary>
    /// <see cref="PayBailOrder"/>, the engine's bail without the desk (kept for tests and scripts; the game offers bail only through the dialogue): a
    /// free, awake player character within <see cref="ArrestSettings.BailRange"/> of the cage and money for it.
    /// </summary>
    void PayBail(World world, CharacterId prisoner)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(prisoner, out int p) || table.Cold(p) is not { IsImprisoned: true, InWhat: { } where } pc
            || world.Fixtures.Find(where) is not { } cage)
        {
            return;
        }
        if (BailFor(world, pc) is not { } price)
        {
            world.Post("Nobody here takes bail.");
            return;
        }
        int payer = -1;
        float best = Settings.BailRange * Settings.BailRange;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == p || !table.Next[s].Alive
                || table.Cold(s) is not { IsPlayer: true, IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true })
            {
                continue;
            }
            var at = table.Next[s].Position;
            float d2 = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(cage.Position.X, cage.Position.Z));
            if (d2 <= best) (payer, best) = (s, d2);
        }
        if (payer < 0)
        {
            world.Post("Nobody is near enough to the jail to pay the bail.");
            return;
        }
        if (world.Player.Money < price)
        {
            world.Post(Loc.Tr("Not enough money: the bail is c.{1}.", price));
            return;
        }
        Bail(world, p, pc, cage, price, payer);
    }

    /// <summary>
    /// Paying (<c>FUN_1406ae170</c>, economy.md 11.5): the money, the jail's entry erased, the prisoner freed (no escape).
    /// </summary>
    void Bail(World world, int p, CharacterCold pc, Fixture cage, int price, int payer)
    {
        var table = world.Characters;
        var prisoner = table.IdOf(p);
        world.Player.Money -= price;
        if (pc.Bounties is { } ledger && ledger.BookedFor(Crimes.Factions.Relations, cage.Owner) is { } entry)
        {
            ledger.Remove(entry);
            if (ledger.Count == 0) pc.Bounties = null;
        }
        world.Post(Loc.Tr("{1} paid a bail of c.{2} for {3}", table.Cold(payer)!.Name, price, pc.Name));
        world.Fixtures.Release(world, prisoner);
        FixtureRegistry.ClearSlave(world, pc);   // bail frees a slave (FUN_1406ae170)
        pc.Freed = true;
        pc.Recognition = 0;
        // Its squad remembers the payer as the one who freed it, and it says event 51 at the payer (FUN_1406ae170: FUN_140673930 tag 3, FUN_140684990
        // event 0x33; Verified).
        var payerId = table.IdOf(payer);
        Talk?.Memory.Remember(world, pc.SquadId, payerId, Dialogue.MemoryTag.FreedMe);
        bool spoke = Talk?.FireEvent(world, prisoner, payerId, Dialogue.DialogueSystem.BoughtMeFromSlavery) == true;
        SendOut(world, p, pc, payer, spoke);
        Note(world, ArrestEventKind.Bailed, table.IdOf(payer), prisoner, cage.Owner, price);
    }

    /// <summary>
    /// The end of each freed prisoner's block in <c>FUN_1406ae170</c>: one that could not speak (event 51 returned 0) is given 128 Retreat to
    /// hometown, then every one 133 Get out of cage (legit). Whether the later give replaces the earlier or queues behind it is <b>Unknown</b>
    /// (<c>FUN_1405d20d0</c>): Meitou walks a prisoner that spoke out of the cage to its payer (<see cref="Dialogue.CharacterTrading.WalkOutOfCage"/>,
    /// 133) and one that did not to its squad's home (128 standing for the pair).
    /// </summary>
    void SendOut(World world, int p, CharacterCold pc, int payer, bool spoke)
    {
        if (!spoke && world.Squads.Find(pc.SquadId) is { } squad)
            TaskStart.Walk(world, p, squad.HomeCentre, SpeedMode.Walk, TaskType.RetreatHometown, TaskSource.Npc);
        else
            Dialogue.CharacterTrading.WalkOutOfCage(world, p, payer);
    }

    // ------------------------------------------------------------------ putting a captive in a cage, and the claim

    void StartCaging(World world, CharacterId carrier, string? chosen, bool queued)
    {
        var table = world.Characters;
        if (Carry is null || !table.TryResolveNext(carrier, out int s) || table.Cold(s) is not { IsPlayer: true, Carrying.IsNone: false }) return;
        PlayerOrders.Give(world, s, new OrderEntry(TaskType.PutInCage, this) { Site = chosen }, queued);
    }

    /// <summary>
    /// The cage a put-in-cage order takes its body to: the one it named while free and nobody else's goal, else the nearest such one in reach; the
    /// carrier's own caging is no rival.
    /// </summary>
    Fixture? CageFor(World world, int s, CharacterCold c, string? chosen)
    {
        var carrier = world.Characters.IdOf(s);
        var own = _cagings.Find(k => k.Carrier == carrier);
        return chosen is not null ? ChosenCage(world, chosen, carrier) : FindCageFor(world, s, c, own);
    }

    /// <summary>
    /// The cage <paramref name="chosen"/> names while it is free, not reserved and no other carrier's goal.
    /// </summary>
    Fixture? ChosenCage(World world, string chosen, CharacterId carrier)
    {
        if (world.Fixtures.Find(chosen) is not { Kind: FixtureKind.Cage } picked || !FixtureRegistry.IsFree(world.Characters, picked)) return null;
        if (Reserved(picked, null) || _cagings.Exists(k => k.Carrier != carrier && k.Cage == picked.Id)) return null;
        return picked;
    }

    /// <summary>
    /// Put in prison 108 needs a player character carrying someone (else it leaves the queue), awake and free, and a free cage ("no free cage
    /// nearby", posted).
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        if (Carry is null || world.Characters.Cold(slot) is not { IsPlayer: true, Carrying.IsNone: false } c) return OrderCheck.Gone;
        if (c is not { IsImprisoned: false, Medical.Incapacitated: false }) return OrderCheck.Blocked();
        return CageFor(world, slot, c, order.Site) is null ? OrderCheck.Blocked($"{c.Name}: no free cage nearby", posted: true) : OrderCheck.Run;
    }

    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var c = world.Characters.Cold(slot)!;
        var cage = CageFor(world, slot, c, order.Site)!;
        var carrier = world.Characters.IdOf(slot);
        _cagings.RemoveAll(k => k.Carrier == carrier);
        TaskStart.Start(world, slot,
            new TaskRequest(CharacterTask.GoTo, TaskType.PutInCage, TaskSource.Order) { Site = cage.Id, Target = c.Carrying });
    }

    public void RegisterTasks(TaskHandlers tasks) => tasks.Register(TaskType.PutInCage, this);

    /// <summary>
    /// The put-in-cage order's walk (row 108): the carrier takes its body to the cage <see cref="TaskRequest.Site"/>.
    /// </summary>
    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        var table = world.Characters;
        var carrier = table.IdOf(slot);
        var cage = world.Fixtures.Find(request.Site!)!;
        _cagings.RemoveAll(k => k.Carrier == carrier);
        _cagings.Add(new Caging { Carrier = carrier, Captive = request.Target, Cage = cage.Id });
        MovementSystem.GoTo(table, slot, new Vector2(cage.Position.X, cage.Position.Z), table.Cold(slot)!.RunSpeed);
    }

    /// <summary>
    /// A caging another task replaces or the Stop order ends is given up.
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        var carrier = world.Characters.IdOf(slot);
        _cagings.RemoveAll(k => k.Carrier == carrier);
    }

    /// <summary>
    /// The cage finder from the carrier (other cagings' and arrests' cages reserved), within <see cref="ArrestSettings.CageSearchRange"/>; null when
    /// none.
    /// </summary>
    Fixture? FindCageFor(World world, int s, CharacterCold c, Caging? self)
    {
        var at = world.Characters.Next[s].Position;
        var cage = world.Fixtures.FindCage(world, c, at, FixtureRegistry.HomeOf(world, c),
            f => Reserved(f, null) || _cagings.Exists(k => !ReferenceEquals(k, self) && k.Cage == f.Id));
        float r = Settings.CageSearchRange;
        return cage is not null && Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(cage.Position.X, cage.Position.Z)) <= r * r
            ? cage : null;
    }

    /// <summary>
    /// One tick of a caging; false when it is over (put in, dropped, or another order).
    /// </summary>
    bool StepCaging(World world, Caging k)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(k.Carrier, out int s) || table.Cold(s) is not { Medical.Incapacitated: false } c || c.Carrying != k.Captive)
            return false;
        ref var n = ref table.Next[s];
        var cage = world.Fixtures.Find(k.Cage);
        if (cage is null || !FixtureRegistry.IsFree(table, cage))
        {
            if (FindCageFor(world, s, c, k) is not { } other)
            {
                world.Post(Loc.Tr("{1}: no free cage nearby", c.Name));
                return false;
            }
            k.Cage = other.Id;
            TaskStart.StepTo(table, s, new Vector2(other.Position.X, other.Position.Z), c.RunSpeed);
            return true;
        }
        var goal = new Vector2(cage.Position.X, cage.Position.Z);
        float d = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), goal);
        bool walking = n.Task == (byte)CharacterTask.GoTo;
        if (walking && Vector2.Distance(n.Goal, goal) > 1) return false;   // another order
        if (FixtureRegistry.AtCage(cage, n.Position, walking))
        {
            if (walking) TaskStart.Stand(table, s);
            Carry!.LetGo(world, s);
            if (!world.Fixtures.Imprison(world, k.Captive, cage, constants.PrisonTime))
            {
                world.Post(Loc.Tr("{1} could not put the captive in the cage", c.Name));
                return false;
            }
            Claim(world, s, k.Captive, cage);
            if (table.TryResolveNext(k.Captive, out int captive)) Deeds?.Caged(world, s, captive);   // after the claim, as the put-in task does
            return false;
        }
        if (!walking)
        {
            if (n.Task != (byte)CharacterTask.Idle || ++k.Tries > Settings.CageWalks) return false;   // a new order, or the cage cannot be reached
            TaskStart.StepTo(table, s, goal, c.RunSpeed);
        }
        return true;
    }

    /// <summary>
    /// The reward for a captive the carrier just put in <paramref name="cage"/> (the game's <c>FUN_140330d50</c>, economy.md 11.4, <b>Observed</b>):
    /// a carrier whose faction is the player's (the faction object's player flag, not whether the carrier is player-controlled, <b>Verified</b>), a
    /// captive not of the player's faction, a cage whose owner is not the player's faction and not lawless (<c>not real</c> or BANDIT), neither of
    /// them escaped; the captive's unclaimed entry for the cage owner's faction pays, halved (truncated) when the captive is dead, into the player's
    /// money, and is marked claimed. The slaver exception does not apply (the player's characters are never slaves here). Returns what it paid (0:
    /// nothing).
    /// </summary>
    public int Claim(World world, int carrier, CharacterId captive, Fixture cage)
    {
        var table = world.Characters;
        if (table.Cold(carrier) is not { Escaped: false } cc || !(cc.IsPlayer || world.Player.Faction >= 0 && cc.Faction == world.Player.Faction)
            || !table.TryResolveNext(captive, out int b) || table.Cold(b) is not { IsPlayer: false, Escaped: false } bc)
        {
            return 0;
        }
        int owner = cage.Owner;
        if (cage.Kind != FixtureKind.Cage || owner < 0 || owner == world.Player.Faction || bc.Faction == world.Player.Faction
            || Crimes.Factions.Lawless(owner))
        {
            return 0;
        }
        if (bc.Bounties?.BookedFor(Crimes.Factions.Relations, owner) is not { Amount: > 0, Claimed: false } entry) return 0;
        int amount = bc.Medical is { Dead: true } ? entry.Amount / 2 : entry.Amount;
        world.Player.Money += amount;
        entry.Claimed = true;
        world.Post(Loc.Tr("Received c.{1} reward.", amount));
        world.EmitSound(SoundKind.BountyPaid, carrier, table.Next[carrier].Position);
        Note(world, ArrestEventKind.Claimed, table.IdOf(carrier), captive, owner, amount);
        return amount;
    }

    /// <summary>
    /// <c>DA_ARREST_TARGET</c> (29, <c>FUN_140680560</c>): in his own town the speaker captures the target and cages it (the original's tasks 240 and
    /// 112: an arrest with no fine); elsewhere he and his squad attack it (task 4). <paramref name="capture"/> is <see cref="CapturesInTown"/>, asked
    /// once by the caller, which tags the memory by the same answer. False: the speaker is busy, or either is gone or down.
    /// </summary>
    public bool ArrestFromDialogue(World world, CharacterId guard, CharacterId target, bool capture)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(guard, out int g)
            || table.Cold(g) is not { IsPlayer: false, Medical.Incapacitated: false, Carrying.IsNone: true } || Busy(guard))
        {
            return false;
        }

        // a carried target is snatched by the capture (task 240)
        if (!table.TryResolveNext(target, out int t) || !table.Next[t].Alive || table.Cold(t) is not { IsImprisoned: false }) return false;
        Start(world, g, t, force: true, capture);
        return true;
    }

    /// <summary>
    /// DA 29's one predicate (<c>FUN_140680560</c> case 0x1d, Verified): the guard stands in his own town (<see cref="InOwnTown"/>) and the target is
    /// no animal (vfunc <c>0x248</c>) - capture (tasks 240 and 112, the target's squad remembers him as its captor); else attack (task 4, his squad
    /// tags the target a temporary enemy).
    /// </summary>
    public bool CapturesInTown(World world, CharacterId guard, CharacterCold target) =>
        world.Characters.TryResolveNext(guard, out int g) && InOwnTown(world, g) && !SquadRegistry.IsAnimal(target);

    /// <summary>
    /// The bounty desk's <c>DA_PAY_BOUNTY</c> (10, <c>FUN_140680560</c>, economy.md 11.4, <b>Observed</b>): the body <paramref name="carrier"/>
    /// carries pays its unclaimed entry for the speaker's booking faction, halved (truncated) when dead, into the player's money, and the entry is
    /// marked claimed. Returns what it paid (0: nothing).
    /// </summary>
    public int PayBounty(World world, CharacterId speaker, CharacterId carrier)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(speaker, out int s) || table.Cold(s) is not { } sc || !table.TryResolveNext(carrier, out int c)
            || table.Cold(c) is not { IsPlayer: true } cc)
        {
            return 0;
        }
        if (!table.TryResolveNext(cc.Carrying, out int b) || table.Cold(b) is not { IsPlayer: false } bc || bc.Faction == world.Player.Faction)
            return 0;
        int booking = Crimes.Factions.BookingOf(sc.Faction);
        if (booking < 0 || bc.Bounties?.Of(booking) is not { Amount: > 0, Claimed: false } entry) return 0;
        int amount = bc.Medical is { Dead: true } ? entry.Amount / 2 : entry.Amount;
        world.Player.Money += amount;
        entry.Claimed = true;
        world.Post(Loc.Tr("Received c.{1} reward.", amount));
        world.EmitSound(SoundKind.BountyPaid, c, table.Next[c].Position);
        Note(world, ArrestEventKind.Claimed, carrier, cc.Carrying, booking, amount);
        return amount;
    }

    /// <summary>
    /// <c>DA_ARREST_TARGETS_CARRIED_PERSON</c> (30, economy.md 11.7, <b>Observed</b>): <paramref name="carrier"/> drops the body it carries and
    /// <paramref name="guard"/> takes it over: he lifts it and carries it to a free cage as an arrest does (no cage: he drops it). False: no body, or
    /// the guard is busy. <paramref name="enslave"/>: <c>DA_ENSLAVE_TARGETS_CARRIED_PERSON</c> (40, the same hand-over, <c>FUN_140680560</c>): once
    /// caged the captive is the guard faction's slave (engine: the original's slaver processing, task 182, is not built).
    /// </summary>
    public bool TakeOver(World world, CharacterId guard, CharacterId carrier, bool enslave = false)
    {
        var table = world.Characters;
        if (Carry is null || !table.TryResolveNext(guard, out int g)
            || table.Cold(g) is not { IsPlayer: false, Medical.Incapacitated: false, Carrying.IsNone: true } gc || Busy(guard))
        {
            return false;
        }
        if (!table.TryResolveNext(carrier, out int c) || table.Cold(c) is not { } cc || !table.TryResolveNext(cc.Carrying, out int t)
            || table.Cold(t) is not { } tc)
        {
            return false;
        }
        var target = cc.Carrying;
        Carry.LetGo(world, c);
        var arrest = new Arrest { Guard = guard, Target = target, Faction = gc.Faction, Phase = ArrestPhase.Fight,
            Enslave = enslave, Takeover = true };
        _arrests.Add(arrest);
        gc.Engaged = true;
        if (tc.Medical is { Incapacitated: true }
            && Carry.BeginPickUp(world, g, target, source: TaskSource.Npc, row: TaskType.ArrestTargetsCarriedPerson))
        {
            arrest.Phase = ArrestPhase.Lift;
        }
        Note(world, ArrestEventKind.Arrest, guard, target, gc.Faction, tc.Bounties?.BookedAmount(Crimes.Factions.Relations, gc.Faction) ?? 0);
        return true;
    }

    // ------------------------------------------------------------------ bounty hunters

    /// <summary>
    /// The bounty a hunter of <paramref name="faction"/> sees on <paramref name="c"/>: the unclaimed entry of its faction's booking faction
    /// (<see cref="CrimeFactions.BookingOf"/>).
    /// </summary>
    int HuntBounty(CharacterCold c, int faction) =>
        Crimes.Factions.BookingOf(faction) is var f and >= 0 && c.Bounties?.Of(f) is { Claimed: false } e ? e.Amount : 0;

    /// <summary>
    /// The looker is a bounty hunter (<see cref="CrimeFactions.Hunts"/>) that would go after <paramref name="target"/>'s bounty.
    /// </summary>
    bool HuntsFor(CharacterCold looker, CharacterCold target) =>
        Crimes.Factions.Hunts(looker.Faction) && HuntBounty(target, looker.Faction) > HuntLimit;

    /// <summary>
    /// A hunter free to start a hunt on <paramref name="target"/>: awake, not fighting or carrying, in no arrest or hunt, and not resting from this
    /// target.
    /// </summary>
    bool FreeHunter(World world, int slot, CharacterId target)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive || table.Next[slot].Task is (byte)CharacterTask.Attack or (byte)CharacterTask.Treat) return false;
        if (table.Cold(slot) is not { IsPlayer: false, InCombat: false, Fleeing: false, IsImprisoned: false, Medical.Incapacitated: false,
            Carrying.IsNone: true, CarriedBy.IsNone: true, Fighter.CanAttack: true })
        {
            return false;
        }
        var id = table.IdOf(slot);
        if (Busy(id)) return false;
        foreach (var h in _hunts) if (h.Hunter == id && (h.Rest <= 0 || h.Target == target)) return false;
        return true;
    }

    /// <summary>
    /// The hunter and his squad attack the target and chase it (the dialogue's <c>DA_ATTACK_CHASE_FOREVER</c>, economy.md 11.8).
    /// </summary>
    void BeginHunt(World world, int hunter, int target)
    {
        var table = world.Characters;
        var hc = table.Cold(hunter)!;
        hc.Engaged = true;
        _hunts.Add(new Hunt { Hunter = table.IdOf(hunter), Target = table.IdOf(target) });
        Attack(world, hunter, target);
        world.Post(Loc.Tr("{1} is after the bounty on {2}!", hc.Name, table.Cold(target)!.Name));
        Note(world, ArrestEventKind.Hunt, table.IdOf(hunter), table.IdOf(target), hc.Faction, HuntBounty(table.Cold(target)!, hc.Faction));
    }

    /// <summary>
    /// One tick of a hunt; false when it is gone. The chase ends when the target is down (the hunter leaves it: nothing in the data cages it),
    /// carried, caged or beyond the leash.
    /// </summary>
    bool StepHunt(World world, Hunt h)
    {
        var table = world.Characters;
        float dt = world.TickSeconds;
        if (h.Rest > 0) return (h.Rest -= dt) > 0;
        if (!table.TryResolveNext(h.Hunter, out int g) || table.Cold(g) is not { Medical.Incapacitated: false } gc) return false;
        bool over = !table.TryResolveNext(h.Target, out int t)
            || table.Cold(t) is not { Medical.Incapacitated: false, IsImprisoned: false, CarriedBy.IsNone: true };
        if (!over)
        {
            h.Time += dt;
            ref var gn = ref table.Next[g];
            if (gn.Task != (byte)CharacterTask.Attack && !gc.InCombat && h.Time >= 1)
            {
                float gap = Vector3.Distance(gn.Position, table.Next[t].Position);
                if (gap > Settings.HuntLeash || HuntBounty(table.Cold(t)!, gc.Faction) <= HuntLimit) over = true;
                else
                {
                    h.Time = 0;
                    Attack(world, g, t);
                }
            }
        }
        if (!over) return true;
        gc.Engaged = false;
        h.Rest = Settings.HuntRestSeconds;
        Note(world, ArrestEventKind.HuntOver, h.Hunter, h.Target, gc.Faction, 0);
        return true;
    }

    void HashBounties(ref StateHasher hasher)
    {
        if (_cagings.Count > 0)
        {
            hasher.Add(0x43616765);
            hasher.Add(_cagings.Count);
            foreach (var k in _cagings) k.Hash(ref hasher);
        }
        if (_hunts.Count > 0)
        {
            hasher.Add(0x48756E74);
            hasher.Add(_hunts.Count);
            foreach (var h in _hunts) h.Hash(ref hasher);
        }
    }
}
using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
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
    /// <summary>
    /// The player character carrying the body.
    /// </summary>
    public required CharacterId Carrier { get; init; }
    /// <summary>
    /// The body carried.
    /// </summary>
    public required CharacterId Captive { get; init; }
    /// <summary>
    /// The id of the cage it is taken to.
    /// </summary>
    public required string Cage { get; set; }
    /// <summary>
    /// Walks to the cage so far; past <see cref="ArrestSettings.CageWalks"/> the caging gives up.
    /// </summary>
    public int Tries { get; set; }

    /// <summary>
    /// Adds the carrier, captive, cage (a stable hash of its id) and walks to the world hash.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(Carrier);
        h.Add(Captive);
        h.Add(Rng.StableHash(Cage));
        h.Add(Tries);
    }
}

/// <summary>
/// The arrest system's bail, the player's put-in-cage order with its bounty claim, and the bounty hunters.
/// </summary>
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

    /// <summary>
    /// The running put-in-cage walks, one per carrier.
    /// </summary>
    readonly List<Caging> _cagings = [];

    /// <summary>
    /// The running put-in-cage walks.
    /// </summary>
    public IReadOnlyList<Caging> Cagings => _cagings;

    /// <summary>
    /// Takes the player's bail and put-in-cage orders (the selection's carriers when the order names none).
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case PayBailOrder bail:
                    PayBail(world, bail.Prisoner);
                    break;
                case PutInCageOrder put:
                    foreach (var id in put.Carriers.Count > 0 ? put.Carriers : world.Player.Selection.ToArray())
                        StartCaging(world, id, put);
                    break;
            }
        }
    }

    #region Bail

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
    /// Paying (<c>FUN_1406ae170</c>, economy.md 11.5): the money, the jail's entry erased, the prisoner split from a squad of others
    /// into one of its own (<see cref="Dialogue.DialogueSystem.Separate"/>), its worn shackles unlocked and out through order 133.
    /// The end of each freed prisoner's block (<b>Verified</b> by disassembly): one that could not speak (event 51 returned 0) is given 128 Retreat
    /// to hometown, then every one 133 Get out of cage (legit); both gives clear the order queue first (<c>FUN_1405d20d0</c> with its clear argument
    /// 1: <c>FUN_1405074a0</c> and <c>FUN_140507530</c>), so 133 replaces 128 and the prisoner only steps out of its cage
    /// (<see cref="FixtureRegistry.GetOutLegit"/>); its own AI goes on from there. <b>Engine order</b>: 133 is given (and started) before event 51,
    /// since Meitou's events need a speaker out of its cage, so whether the event would fail is not known before it and 128, which 133 wipes unrun,
    /// is not given. A prisoner whose chooser does not run (no squad, out cold) keeps the order queued in its cage until it does. The cage stays locked: unlike the buy, the bail
    /// writes no cage lock (<b>Verified</b>).
    /// </summary>
    void Bail(World world, int p, CharacterCold pc, Fixture cage, int price, int payer)
    {
        var table = world.Characters;
        var prisoner = table.IdOf(p);
        world.Player.Money -= price;
        if (pc.Bounties is { } ledger && ledger.BookedFor(Crimes.Factions.Relations, cage.Owner) is { } entry)
            CrimeSystem.ClearBounty(world, p, pc, ledger, entry);
        world.Post(Loc.Tr("{1} paid a bail of c.{2} for {3}", world.ShownName(table.Cold(payer)!), price, world.ShownName(pc)));
        FixtureRegistry.ClearSlave(world, pc);   // bail frees a slave (FUN_1406ae170)
        pc.Freed = true;
        pc.Recognition = 0;

        // Out of a squad of others into a squad of its own, as a slave bought free (FUN_1406ae170 calls FUN_1405ce4e0 before event 51, for a
        // player's character too; Verified), so the payer below is tagged in the new squad's memory.
        Talk?.Separate(world, prisoner);

        // Its worn shackles are unlocked, still worn (FUN_1406ae170: FUN_1405c8a10's locked armour, lock byte +0x20 = 0; Verified, as the buy).
        if (LockSystem.WornShackles(pc)?.Lock is { } shackles) shackles.Locked = false;

        // Its squad remembers the payer as the one who freed it, and it says event 51 at the payer (FUN_1406ae170: FUN_140673930 tag 3, FUN_140684990
        // event 0x33; Verified).
        var payerId = table.IdOf(payer);
        Talk?.Memory.Remember(world, pc.SquadId, payerId, Dialogue.MemoryTag.FreedMe);
        FreedPrisoner.Give(world, p);
        Talk?.FireEvent(world, prisoner, payerId, Dialogue.DialogueSystem.BoughtMeFromSlavery);
        Note(world, ArrestEventKind.Bailed, table.IdOf(payer), prisoner, cage.Owner, price);
    }

    #endregion

    #region Putting a captive in a cage, and the claim

    /// <summary>
    /// Queues or gives the put-in-cage order (row 108) to a player character carrying a body; the order's cage names it, null: the nearest.
    /// </summary>
    void StartCaging(World world, CharacterId carrier, PutInCageOrder put)
    {
        var table = world.Characters;
        if (Carry is null || !table.TryResolveNext(carrier, out int s) || table.Cold(s) is not { IsPlayer: true, Carrying.IsNone: false }) return;

        var point = put.Cage is { } id && world.Fixtures.Find(id) is { } cage ? OrderEntry.AtBuilding(world, put, cage.Position) : default;
        PlayerOrders.Give(world, s, new OrderEntry(TaskType.PutInCage, this) { Site = put.Cage, TaskPoint = point }, put.Queued);
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
        if (Reserved(world, picked, null) || _cagings.Exists(k => k.Carrier != carrier && k.Cage == picked.Id)) return null;
        return picked;
    }

    /// <summary>
    /// Put in prison 108 needs a character carrying someone (else it leaves the queue), awake and free, and a free cage ("no free cage nearby",
    /// posted).
    /// </summary>
    OrderCheck CanCage(World world, int slot, OrderEntry order)
    {
        if (Carry is null || world.Characters.Cold(slot) is not { Carrying.IsNone: false } c) return OrderCheck.Gone;
        if (c is not { IsImprisoned: false, Medical.Incapacitated: false }) return OrderCheck.Blocked();
        return CageFor(world, slot, c, order.Site) is null
            ? OrderCheck.Blocked($"{world.ShownName(c)}: no free cage nearby", posted: true)
            : OrderCheck.Run;
    }

    /// <summary>
    /// Starts the put-in-cage order: drops the carrier's old caging and walks to the cage with the carried body as target.
    /// </summary>
    void StartPutInCage(World world, int slot, OrderEntry order)
    {
        var c = world.Characters.Cold(slot)!;
        var cage = CageFor(world, slot, c, order.Site)!;
        var carrier = world.Characters.IdOf(slot);
        _cagings.RemoveAll(k => k.Carrier == carrier);
        TaskStart.Start(world, slot,
            new TaskRequest(CharacterTask.GoTo, TaskType.PutInCage, TaskSource.Order) { Site = cage.Id, Target = c.Carrying });
    }

    /// <summary>
    /// Registers the put-in-cage task (row 108) with this system.
    /// </summary>
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
        WalkToCage(world, slot, cage, table.Cold(slot)!.RunSpeed);
    }

    /// <summary>
    /// Where a carrier walks for a cage: the floor beside it at the cage's height. A cage cuts its hole in a building's floor mesh, so its own X/Z
    /// resolves to the storey below, where the carrier would stand under it (#432; <see cref="MovementSystem.GoToFloor"/>).
    /// </summary>
    static Vector3 CageGoal(World world, Fixture cage) =>
        world.Walkability.FloorNear(cage.Position.X, cage.Position.Z, cage.Position.Y, MovementSystem.FloorSlack) ?? cage.Position;

    /// <summary>
    /// Walks the carrier to <see cref="CageGoal"/>, on the cage's floor.
    /// </summary>
    static void WalkToCage(World world, int slot, Fixture cage, byte mode)
    {
        var at = CageGoal(world, cage);
        var flat = new Vector2(at.X, at.Z);
        TaskStart.StepTo(world.Characters, slot, flat, mode);
        var c = world.Characters.Cold(slot)!;
        (c.GoalHeight, c.GoalHeightAt) = (at.Y, flat);
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
            f => Reserved(world, f, null) || _cagings.Exists(k => !ReferenceEquals(k, self) && k.Cage == f.Id));
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
                if (DestinationAccess.PlayerSide(world, c))
                    world.Post(Loc.Tr("{1}: no free cage nearby", world.ShownName(c)));
                return false;
            }
            k.Cage = other.Id;
            WalkToCage(world, s, other, c.RunSpeed);
            return true;
        }
        var at = CageGoal(world, cage);
        var goal = new Vector2(at.X, at.Z);
        bool walking = n.Task == (byte)CharacterTask.GoTo;
        if (walking && Vector2.Distance(n.Goal, goal) > 1) return false;   // another order
        if (world.Fixtures.AtCage(cage, n.Position, walking, world.Walkability))
        {
            if (walking) TaskStart.Stand(table, s);
            Carry!.LetGo(world, s);
            if (!world.Fixtures.Imprison(world, k.Captive, cage, constants.PrisonTime))
            {
                if (DestinationAccess.PlayerSide(world, c))
                    world.Post(Loc.Tr("{1} could not put the captive in the cage", world.ShownName(c)));
                return false;
            }
            Claim(world, s, k.Captive, cage);
            if (table.TryResolveNext(k.Captive, out int captive)) Deeds?.Caged(world, s, captive);   // after the claim, as the put-in task does
            return false;
        }
        if (!walking)
        {
            if (n.Task != (byte)CharacterTask.Idle || ++k.Tries > Settings.CageWalks) return false;   // a new order, or the cage cannot be reached
            WalkToCage(world, s, cage, c.RunSpeed);
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
    /// <c>DA_ARREST_TARGET</c> (29, <c>FUN_140680560</c>): in his own town the speaker is ordered 240 Capture Target and 112 Find and put in cage on
    /// the target, elsewhere 4 Attacking (<see cref="OrderArrest"/>). <paramref name="capture"/> is <see cref="CapturesInTown"/>, asked once by the
    /// caller, which tags the memory by the same answer. False: either is gone.
    /// </summary>
    public bool ArrestFromDialogue(World world, CharacterId guard, CharacterId target, bool capture)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(guard, out int g) || !table.TryResolveNext(target, out int t) || g == t) return false;

        OrderArrest(world, g, t, capture);
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
    /// The hand-over of DA 30 <c>DA_ARREST_TARGETS_CARRIED_PERSON</c>, 40 <c>DA_ENSLAVE_TARGETS_CARRIED_PERSON</c> and 10 <c>DA_PAY_BOUNTY</c>
    /// (<c>FUN_140680560</c>, <b>Verified</b>): <paramref name="carrier"/> drops the body it carries and <paramref name="guard"/> is ordered 213
    /// Lifting/Snatching on it, his queue cleared, then 112 Find and put in cage queued behind it, so he carries it to a free cage. The order check
    /// <c>FUN_1405d1940</c> refuses 213 to a guard whose left arm cannot carry ("I can't carry anyone with this arm."), and 112 is given all the same.
    /// DA 40 gives the same orders: the caged body is enslaved only by the town's slavers' processing (183). <paramref name="enslave"/> (code mods'
    /// call, docs/modding.md; no dialogue action sets it) makes the caging enslave the body to the guard's faction. False: no body.
    /// </summary>
    public bool TakeOver(World world, CharacterId guard, CharacterId carrier, bool enslave = false)
    {
        var table = world.Characters;
        if (Carry is null || !table.TryResolveNext(guard, out int g) || table.Cold(g) is not { } gc) return false;
        if (!table.TryResolveNext(carrier, out int c) || table.Cold(c) is not { } cc || !table.TryResolveNext(cc.Carrying, out int t) || t == g)
            return false;

        var body = cc.Carrying;
        Carry.LetGo(world, c);
        if (CanLift(gc))
            PlayerOrders.Give(world, g, new OrderEntry(TaskType.LiftPersonSnatchingAllowed, this) { Target = body }, queued: false);
        else
            world.Bark(guard, Loc.Tr("I can't carry anyone with this arm."));

        PlayerOrders.Give(world, g, CageOrder(body, enslave), queued: true);
        int bounty = table.Cold(t)!.Bounties?.BookedAmount(Crimes.Factions.Relations, gc.Faction) ?? 0;
        Note(world, ArrestEventKind.Arrest, guard, body, gc.Faction, bounty);
        return true;
    }

    #endregion

    #region Bounty hunters

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
    /// A hunter free to act on a sighting: awake, not fighting or carrying, in no arrest, with no order.
    /// </summary>
    bool FreeHunter(World world, int slot)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive || table.Next[slot].Task is (byte)CharacterTask.Attack or (byte)CharacterTask.Treat) return false;
        if (table.Cold(slot) is not { IsPlayer: false, InCombat: false, Fleeing: false, IsImprisoned: false, Medical.Incapacitated: false,
            Carrying.IsNone: true, CarriedBy.IsNone: true, Fighter.CanAttack: true } hc || DecisionSystem.Ordered(hc))
        {
            return false;
        }

        return !Busy(table.IdOf(slot));
    }

    /// <summary>
    /// The hunter recognised the target's bounty: it fires event 44 BOUNTY_SPOTTED at it (<c>FUN_140856f00</c>), whose bounty hunters' lines answer a
    /// bounty above 4000 with <c>DA_ATTACK_CHASE_FOREVER</c> (6, order 101; economy.md 11.8). With no line said nothing follows (the event is all the
    /// original's spotting does, <c>FUN_140685660</c>, <b>Verified</b>). There is no rest after a hunt: the hunter fires again at every spotting pass
    /// it is not fighting, and its lines' <c>repetition limit</c> (2 or 6 game hours in the base data, global per line) keeps it from repeating.
    /// </summary>
    void BeginHunt(World world, int hunter, int target)
    {
        var table = world.Characters;
        var hc = table.Cold(hunter)!;
        var hunterId = table.IdOf(hunter);
        var targetId = table.IdOf(target);
        if (!Talks || !Talk!.FireEvent(world, hunterId, targetId, BountySpotted)) return;

        Note(world, ArrestEventKind.Spotted, hunterId, targetId, hc.Faction, BountySpotted);
        Note(world, ArrestEventKind.Hunt, hunterId, targetId, hc.Faction, HuntBounty(table.Cold(target)!, hc.Faction));
    }

    /// <summary>
    /// A bounty the target carries that the guard sees (goal 204's finder, <c>FUN_140853fd0</c>, <b>Verified</b>): the entry of the guard's booking
    /// faction is above 0, and either the target is recently wanted (the status marker above 0) or it is down and the guard regards it as an enemy
    /// (<see cref="Enemy"/>, the guard's vfunc <c>0x3e8</c> with 1).
    /// </summary>
    internal bool PerceivedBounty(World world, int g, CharacterCold guard, int t, CharacterCold target)
    {
        if ((target.Bounties?.BookedAmount(Crimes.Relations, guard.Faction) ?? 0) <= 0) return false;
        if (target.RecentlyWanted > 0) return true;

        return target.Medical is { Incapacitated: true } && Enemy(world, g, guard, t, target);
    }

    /// <summary>
    /// Whether the guard regards the target as an enemy (<c>isEnemy</c>, <c>FUN_14079c950</c>, <b>Verified</b>, docs/game/combat.md "Enemy test"):
    /// never one of its own faction; the squads' memory decides first (<see cref="CharacterFacts.SquadMemoryEnemy"/>: a target the guard's squad
    /// tagged TEMP_ENEMY, as DA 29's fight does, is an enemy whatever the factions), else the faction part (<see cref="FactionEnemy"/>).
    /// </summary>
    internal bool Enemy(World world, int g, CharacterCold guard, int t, CharacterCold target)
    {
        if (guard.Faction == target.Faction) return false;
        if (CharacterFacts.SquadMemoryEnemy(world, Talk?.Memory, g, guard, t, target) is { } memory) return memory;

        return FactionEnemy(world, guard, target);
    }

    /// <summary>
    /// The faction part of <c>FUN_14079c950</c> with its disguise flag set, as both arrest callers pass it (<b>Verified</b>, decompile; docs/game/
    /// perception.md "Slot 125"): a working disguise stands in for the target's faction and skips the slave test; else a slave target (state 1)
    /// needs the guard hostile to its owner's faction, which is never the guard's own; a slave guard spares its master's faction; a freed guard
    /// regards nobody as an enemy, and a freed target only a guard of a faction that is not real or has a default relation below -30; then the
    /// guard's faction is hostile to the one the target passes for (relation -30 or less, <c>FUN_1406b25d0</c>, no coexistence test).
    /// </summary>
    bool FactionEnemy(World world, CharacterCold guard, CharacterCold target)
    {
        var relations = Crimes.Relations;
        if (!relations.Contains(guard.Faction)) return false;

        bool disguised = WorkingDisguise(target);
        if (!disguised && target.SlaveState == 1 && !HostileTo(guard.Faction, target.SlaveOwner)) return false;
        if (guard is { SlaveState: 1, SlaveMaster: >= 0 } && world.Squads.Find(guard.SlaveMaster)?.Faction == target.Faction) return false;
        if (guard.Freed || target.Freed && !FreedStillHostile(guard.Faction)) return false;

        return HostileTo(guard.Faction, disguised ? target.DisguiseFaction : target.Faction);
    }

    /// <summary>
    /// Whether <paramref name="mine"/> is hostile to <paramref name="theirs"/>, a known faction (relation -30 or less; never its own).
    /// </summary>
    bool HostileTo(int mine, int theirs) => Crimes.Relations.Contains(theirs) && Crimes.Relations.IsHostile(mine, theirs);

    /// <summary>
    /// Whether a guard of <paramref name="faction"/> still regards a freed target as an enemy: its faction is not real or its default relation is
    /// below <see cref="AggroSystem.FreedHostility"/>.
    /// </summary>
    bool FreedStillHostile(int faction) =>
        Crimes.Relations.Factions[faction] is { } own && (own.NotReal || own.DefaultRelation < AggroSystem.FreedHostility);

    /// <summary>
    /// Adds the cagings to the world hash, only while there are any.
    /// </summary>
    void HashBounties(ref StateHasher hasher)
    {
        if (_cagings.Count == 0) return;

        hasher.Add(0x43616765);
        hasher.Add(_cagings.Count);
        foreach (var k in _cagings) k.Hash(ref hasher);
    }

    #endregion
}
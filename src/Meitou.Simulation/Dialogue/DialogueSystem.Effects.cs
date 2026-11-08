using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>
    /// A line's effects (<c>FUN_140680560</c>, docs/game/dialogue.md "Effects"), run as it is said or clicked, with <paramref name="me"/> the NPC-side speaker and the player character the
    /// target, in the original's order: locks, interrupt, relation changes, then the DA effects in data order, then AI changes and contracts. Not built yet: arrests other
    /// than the bounty desk's hand-over, DA 48 (animals for sale), DA 5, 15, 17, 39 and 58 (docs/game/dialogue.md "Effects").
    /// </summary>
    void RunEffects(World world, Conversation c, DialogueNode line, CharacterId me)
    {
        var mc = Cold(world, me);
        if (mc is not null)
        {
            foreach (string d in line.Unlocks) Lock(mc, d, false);
            if (line.Unlocks.Count > 0 && c.Root is { } root) Lock(mc, root.StringId, true);
            foreach (string d in line.UnlockButKeepMe) Lock(mc, d, false);
            foreach (string d in line.Locks) Lock(mc, d, true);
        }
        if (line.Interrupt is { } interrupt) _interrupts[me] = interrupt;
        foreach (var (factionId, amount) in line.ChangeRelations)
        {
            int f = _data.Relations.IndexOf(factionId);
            if (f < 0 || amount == 0 || world.Player.Faction < 0) continue;
            ChangeRelation(world, f, world.Player.Faction, amount);
            world.Post(amount > 0 ? Loc.Tr("Relations with {1} improved by {2}", _data.Factions[f].Name, amount) : Loc.Tr("Relations with {1} decreased by {2}", _data.Factions[f].Name, -amount));
        }
        CrowdTrigger(world, c, line, me);
        var target = c.Player;
        var tc = Cold(world, target);
        if (tc is not null) GiveItems(world, line, target, tc);
        if (line.TriggerCampaigns.Count > 0) _population?.TriggerFromDialogue(world, me, c.Player, line.TriggerCampaigns);
        bool takeOver = false, enslave = false;
        foreach (var effect in line.Effects)
        {
            switch (effect.Action)
            {
                case DialogueActions.Trade:
                    // The window opens whatever the speaker's squad stocks (FUN_1409524f0 tests no stock); a squad with none trades from an empty one (TradeSystem.StockFor).
                    c.Ended = true;
                    if (mc is { SquadId: >= 0 }) world.Defer(new Trading.OpenTrade(target, me) { Tick = world.Tick + 1 });
                    return;
                case DialogueActions.AffectRelations when mc is not null && tc is not null:
                    ChangeRelation(world, mc.Faction, tc.Faction, effect.Value);
                    break;
                case DialogueActions.AffectReputation when mc is not null:
                    _data.Relations.AddGlobalTrust(mc.Faction, effect.Value);   // the speaker's faction's global trust (slot 9), whoever the target is
                    break;
                case DialogueActions.AttackChaseForever or DialogueActions.AttackStayNearHome:
                    if (mc is not null && tc is not null) FlagFrom(world, mc, me, target, tc, MemoryTag.TempEnemy);
                    Aggro?.Notice(world, me, target);
                    Attack(world, me, target);
                    break;
                case DialogueActions.AttackIfNoCoexist when mc is not null && tc is not null && !_data.Relations.Coexists(mc.Faction, tc.Faction):
                    FlagFrom(world, mc, me, target, tc, MemoryTag.TempEnemy);
                    Aggro?.Notice(world, me, target);
                    Attack(world, me, target);
                    break;
                case DialogueActions.TakeMoney when tc is not null:
                    Pay(world, tc, -effect.Value);
                    world.Post(effect.Value > 100000 ? Loc.Tr("Paid c.{1}k", effect.Value / 1000) : Loc.Tr("Paid c.{1}", effect.Value.ToString("N0", CultureInfo.InvariantCulture)));
                    break;
                case DialogueActions.GiveMoney when tc is not null:
                    Pay(world, tc, effect.Value);
                    break;
                case DialogueActions.CharacterEditor:
                    c.Ended = true;
                    _request = new DialogueRequest(++_requestId, DialogueRequestKind.CharacterEditor, target, me);
                    return;
                case DialogueActions.ChooseSlavesSelling or DialogueActions.ChooseSlavesBuying or DialogueActions.ChooseAnimalsBuying when target != me:
                    // The character trading window opens as the conversation closes (OpenCharacterTrade), a speech bubble's at once; val0 is the price override.
                    _characterTradeAfter = (effect.Action switch
                    {
                        DialogueActions.ChooseSlavesSelling => CharacterTradeKind.SlaveSelling,
                        DialogueActions.ChooseSlavesBuying => CharacterTradeKind.SlaveBuying,
                        _ => CharacterTradeKind.AnimalBuying,
                    }, me, target, effect.Value);
                    if (!ReferenceEquals(_open, c)) { OpenCharacterTrade(world); break; }
                    c.Ended = true;
                    return;
                case ChoosePrisonerBail when Arrests is not null:
                    c.Ended = true;
                    _bailAfter = (me, target);   // the bail list opens as the conversation closes (OpenBail)
                    return;
                case DialogueActions.AssignBounty when tc is not null && mc is not null && effect.Value > 0 && _data.Relations.Contains(mc.Faction):
                    {
                        // FUN_140853e20 (Verified): marks the pending crime booked, then adds the amount to the speaker's faction's booking entry (none kept without one).
                        if (tc.Pending is { } pending) pending.Booked = true;
                        if (Crime.CrimeFactions.BookingKey(_data.Relations, mc.Faction) is var key and >= 0)
                            (tc.Bounties ??= new Crime.BountyLedger()).Get(key, _data.Factions[key].Name).Amount += effect.Value;
                        break;
                    }
                // Without an amount only a speaker whose CHARACTER has `assigns bounties` books the target's pending crime (Character::canAssignBounties, Verified).
                case DialogueActions.AssignBounty when tc is not null && mc is not null && effect.Value <= 0 && Arrests is { } booker
                    && _data.Db.Find(mc.RecordId)?.GetBool("assigns bounties") == true && world.Characters.TryResolveNext(target, out int culprit):
                    booker.Crimes.Book(world, culprit, booker.Crimes.Factions.BookingOf(mc.Faction), me);   // for the law faction (getLawEnforcementFaction)
                    break;
                case DialogueActions.CrimeAlarm when tc is not null && mc is not null && Arrests is { } law
                    && world.Characters.TryResolveNext(me, out int alarmer) && world.Characters.TryResolveNext(target, out int alarmed):
                    law.Crimes.Alarm(world, alarmer, alarmed);
                    break;
                case DialogueActions.PayBounty:
                    Arrests?.PayBounty(world, me, target);
                    if (tc is { Carrying.IsNone: false }) takeOver = true;   // the original hands the body over whether or not a bounty was paid, in any town
                    break;
                // Only in the speaker's own town (the squad's town is the one it stands in); DA_ENSLAVE_TARGETS_CARRIED_PERSON has no such test.
                case DialogueActions.ArrestTargetsCarriedPerson when tc is { Carrying.IsNone: false } && Arrests is { } law && world.Characters.TryResolveNext(me, out int guardSlot) && law.InOwnTown(world, guardSlot):
                    takeOver = true;   // after the line's other effects: the original only gives the speaker the lift task, so DA_PAY_BOUNTY still finds the body
                    break;
                case DialogueActions.EnslaveTargetsCarriedPerson when tc is { Carrying.IsNone: false }:
                    takeOver = enslave = true;
                    break;
                case DialogueActions.GiveTargetMySlaves when tc is not null && mc is not null:
                    GiveSlaves(world, me, mc, tc);
                    break;
                case DialogueActions.SurrenderNonHumans when tc is not null && mc is not null:
                    SurrenderNonHumans(world, me, mc, tc);
                    break;
                case DialogueActions.TalkToLeader when tc is not null && mc is not null:
                    TalkToLeader(world, c, mc, target, tc);
                    break;
                case DialogueActions.ArrestTarget when tc is not null:
                    {
                        bool capture = Arrests?.CapturesInTown(world, me, tc) == true;   // one answer for the memory and the task
                        ArrestMemory(world, me, mc, target, tc, capture);
                        Arrests?.ArrestFromDialogue(world, me, target, capture);
                        break;
                    }
                case DialogueActions.TagEscapedSlave when tc is not null:
                    Locks.FixtureRegistry.TagEscaped(world, tc);
                    break;
                case DialogueActions.RemoveSlaveStatus when tc is not null && world.Characters.TryResolveNext(target, out int freed):
                    Locks.FixtureRegistry.FreeSlave(world, freed, tc);
                    break;
                case DialogueActions.ClearBounty when tc?.Bounties is { } ledger && mc is not null:
                    if (ledger.BookedFor(_data.Relations, mc.Faction) is { } entry) ledger.Remove(entry);
                    if (Position(world, target) is { } at && TownAt(at) is >= 0 and var town && town < _data.SiteFactions.Count && ledger.BookedFor(_data.Relations, _data.SiteFactions[town]) is { } local)
                        ledger.Remove(local);
                    if (ledger.Count == 0) tc.Bounties = null;
                    tc.Recognition = 0;
                    break;
                case DialogueActions.IncreaseFactionRank when mc is not null:
                    _data.Relations.RaiseRank(mc.Faction, effect.Value);
                    break;
                case DialogueActions.LockThisDialog when mc is not null && c.Root is { } current:
                    Lock(mc, current.StringId, true);
                    break;
                case DialogueActions.AssaultPhase or DialogueActions.RetreatPhase:
                    _population?.SetCampaignPhase(world, me, effect.Action == DialogueActions.AssaultPhase);
                    break;
                case DialogueActions.FollowWhileTalking when !target.IsNone && mc is { IsPlayer: false }:
                    _followers[me] = target;
                    StartFollowing(world, me, target);
                    break;
                case DialogueActions.ThugHunter when !target.IsNone && mc is { IsPlayer: false }:
                    _stalkers[me] = new Stalk(target);
                    break;
                case DialogueActions.RunAway when mc is not null && tc is not null:
                    RunAway(world, me, mc, target, tc);
                    break;
                case DialogueActions.GoHome when mc is not null && tc is not null:
                    GoHome(world, me, mc, target, tc);
                    break;
                case DialogueActions.RememberCharacter when mc is not null && tc is not null:
                    Memory.Remember(world, mc.SquadId, target, MemoryTag.None);
                    break;
                case DialogueActions.FlagTempAlly or DialogueActions.FlagTempEnemy when mc is not null && tc is not null:
                    Memory.Flag(world, mc.SquadId, target, effect.Action == DialogueActions.FlagTempAlly ? MemoryTag.TempAlly : MemoryTag.TempEnemy);
                    break;
                case DialogueActions.MakeTargetRunFaster when tc is not null && world.Characters.TryResolveNext(target, out int runner):
                    world.Characters.Next[runner].Mode = SpeedMode.Free;   // the original's speed mode 2
                    break;
                case DialogueActions.SeparateToMyOwnSquad when mc is not null:
                    Separate(world, mc.IsPlayer || mc.Faction == world.Player.Faction ? target : me);
                    break;
                case DialogueActions.DeclareWar when mc is not null && tc is not null && _data.Relations.Contains(mc.Faction) && _data.Relations.Contains(tc.Faction):
                    PlayerRelations.DeclareWar(world, _data.Relations, mc.Faction, tc.Faction);
                    break;
                case DialogueActions.EndWar when mc is not null && tc is not null && _data.Relations.Contains(mc.Faction) && _data.Relations.Contains(tc.Faction):
                    PlayerRelations.EndWar(world, _data.Relations, mc.Faction, tc.Faction);
                    break;
                case DialogueActions.SummonMySquad:
                    Summon(world, me);
                    break;
                case DialogueActions.OpenNearestGate:
                    OpenNearestGate(world, me);
                    break;
                case DialogueActions.MassiveAlarm when mc is not null && tc is not null:
                    MassiveAlarm(world, me, mc, target, tc);
                    break;
                case DialogueActions.JoinSquadWithEdit or DialogueActions.JoinSquadFast:
                    if (!Recruit(world, c, me, effect.Action == DialogueActions.JoinSquadWithEdit)) return;
                    break;
                default:
                    ModEffect(world, me, target, effect);
                    break;
            }
        }
        if (takeOver) Arrests?.TakeOver(world, me, target, enslave);
        // Last, for a speaker squad outside the player's faction (FUN_140273330): change AI replaces its packages, then AI contract starts the contract aimed at the target.
        if (mc is { SquadId: >= 0 } && mc.Faction != world.Player.Faction && !mc.IsPlayer)
        {
            if (line.ChangeAi is { } package) world.Defer(new ChangeAi(mc.SquadId, package) { Tick = world.Tick + 1 });
            if (line.AiContract is { } contract) world.Defer(new StartContract(mc.SquadId, contract.Package, contract.Hours, target.IsNone ? null : target) { Tick = world.Tick + 1 });
        }
    }

    /// <summary>The arrest system the bounty desk's effects pay and hand over through (<c>DA_PAY_BOUNTY</c>, <c>DA_ARREST_TARGETS_CARRIED_PERSON</c>); null: they do nothing.</summary>
    public Crime.ArrestSystem? Arrests { get; set; }

    /// <summary>The dialogue codes code mods handle (docs/modding.md); null without mods.</summary>
    public SimulationExtensions? Extensions { get; set; }

    /// <summary>A mod's effect code (<see cref="SimulationExtensions.FirstModCode"/> and up); an unregistered code does nothing, as an unknown vanilla one.</summary>
    void ModEffect(World world, CharacterId me, CharacterId target, DialogueEffect effect)
    {
        if (effect.Action >= SimulationExtensions.FirstModCode && Extensions?.DialogueEffects.GetValueOrDefault(effect.Action) is { } handler)
            handler(new DialogueAct(world, me, target, effect));
    }

    /// <summary>A mod's condition code: the handler's answer, or "no" when no mod handles it (as an unknown vanilla code computes).</summary>
    bool ModCondition(World world, CharacterId me, CharacterId subject, DialogueCondition dc)
    {
        if (Extensions?.DialogueConditions.GetValueOrDefault(dc.Condition) is not { } handler) return dc.Value <= 0;
        return handler(new DialogueCheck(world, me, subject, dc));
    }

    /// <summary>The attack-on-sight system a massive alarm alarms each member's hearing of the target through (<see cref="AggroSystem.Alarm"/>); null: no alarm.</summary>
    public AggroSystem? Aggro { get; set; }

    /// <summary>
    /// <c>give item</c> (<c>FUN_140680560</c>): each listed item made and put into the target's inventory, val0 2..49 its stack count, and "Received &lt;name&gt;" posted. What does not fit
    /// is dropped at the target's feet (engine choice; the original's add has no such case in this code).
    /// </summary>
    void GiveItems(World world, DialogueNode line, CharacterId target, CharacterCold tc)
    {
        foreach (var (id, count) in line.GiveItems)
        {
            if (_data.Items.Create(id, count is >= 2 and <= 49 ? count : 1) is not { } item) continue;
            string name = item.Name;
            if (tc.Inventory?.Add(item) != true && Position(world, target) is { } at) world.GroundItems.Drop(item, at, 0);
            world.Post($"{Loc.Tr("Received")} {name}");
        }
    }

    /// <summary>
    /// <c>DA_SURRENDER_NON_HUMANS</c> (47, <c>FUN_140680560</c>): the target faction's characters within 100 of the speaker, not animals, whose race is not the speaker's
    /// (<c>FUN_14042ecd0</c>: the speaker's race is the member's own or one of the member's RACE_GROUP) become slaves owned by the speaker's faction (<c>FUN_14032e590</c>), and each
    /// one's squad remembers the speaker. Their squads are left as they are (what follows is not read).
    /// </summary>
    void SurrenderNonHumans(World world, CharacterId me, CharacterCold mc, CharacterCold tc)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(me, out int s) || mc.Race is not { } race) return;
        var at = table.Next[s].Position;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Medical.Dead: false } c || c.Faction != tc.Faction || c.SlaveState != 0) continue;
            if (c.Fighter?.IsAnimal == true || c.Appearance?.Animal is not null || c.Race is null || _dialogue.RaceMatches(c.Race.StringId, race.StringId)) continue;
            if (Vector3.DistanceSquared(table.Next[i].Position, at) > 100 * 100) continue;
            Locks.FixtureRegistry.Enslave(world, c, mc.Faction, mc.SquadId);
            Memory.Remember(world, c.SquadId, me, MemoryTag.None);   // its squad remembers the speaker (the tag is not visible in the decompile; engine: none)
        }
    }

    /// <summary>
    /// DA_ARREST_TARGET's memory (<c>FUN_140680560</c>): when the speaker captures (<see cref="Crime.ArrestSystem.CapturesInTown"/>: in his own town, the target no animal) the target's
    /// squad tags the speaker MY_CAPTOR; otherwise the speaker's squad tags the target TEMP_ENEMY.
    /// </summary>
    void ArrestMemory(World world, CharacterId me, CharacterCold? mc, CharacterId target, CharacterCold tc, bool capture)
    {
        if (mc is null) return;
        if (capture) Memory.Remember(world, tc.SquadId, me, MemoryTag.Captor);
        else Memory.Flag(world, mc.SquadId, target, MemoryTag.TempEnemy);
    }

    static void Lock(CharacterCold cold, string dialogue, bool locked) => (cold.DialogueLocks ??= new(StringComparer.Ordinal))[dialogue] = locked;

    void ChangeRelation(World world, int a, int b, int amount)
    {
        var (before, after) = _data.Relations.Add(a, b, amount);
        int other = a == world.Player.Faction ? b : a;
        if (b == world.Player.Faction || a == world.Player.Faction)
            if (PlayerRelations.Notice(_data.Factions[other].Name, before, after) is { } text) world.Post(text);
    }

    /// <summary>Money to (or from, when negative) the character's faction: the player's purse, else its squad's.</summary>
    static void Pay(World world, CharacterCold who, long amount)
    {
        if (who.IsPlayer || who.Faction == world.Player.Faction) world.Player.Money = Math.Max(0, world.Player.Money + amount);
        else if (who.SquadId >= 0 && world.Squads.Find(who.SquadId) is { } squad) squad.Money = Math.Max(0, squad.Money + amount);
    }

    /// <summary>
    /// The speaker alone is stopped (orders cleared) and attacks the target from the next tick (DA 6/55/57, docs/game/dialogue.md).
    /// Its squad mates are not ordered: they follow through the TEMP ENEMY tag the caller writes.
    /// </summary>
    static void Attack(World world, CharacterId me, CharacterId target)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(target, out _) || !table.TryResolveNext(me, out int s) || !table.Next[s].Alive) return;
        TaskStart.Stand(table, s);
        if (world.SystemOf<AiSystem>() is { } ai && table.Cold(s) is { IsPlayer: false, IsImprisoned: false, Medical.Incapacitated: false, Fighter.CanAttack: true } && ai.OrderAssault(world, me, target)) return;
        if (table.Cold(s) is { IsPlayer: false, IsImprisoned: false, Medical.Incapacitated: false, Fighter.CanAttack: true })
            world.Defer(new Combat.AttackOrder([me], target) { Tick = world.Tick + 1, Source = TaskSource.Npc });
    }

    /// <summary>
    /// The text tokens (docs/game/dialogue.md "Tokens"): <c>/NAME/</c> pairs until none is left; COST, MYNAME, YOURNAME, TARGETFACTION and PLAYERFACTION are built in, any other name
    /// is the WORD_SWAPS record of that name (its lines chosen like a line's children, one of their texts; none: empty). A token at the start or after ". ", "! ", "? " is capitalised.
    /// </summary>
    /// <summary>A built line as shown: through the censorship when the player turned it on.</summary>
    string Said(string text) => Settings.Censored?.Invoke() == true ? Censorship.Apply(text) : text;

    /// <summary>
    /// A new character's name with its text tokens expanded (docs/characters.md "Names"): a name holding a <c>/</c> goes through the same expansion and censorship as a said line,
    /// spoken by the character itself to no one (<c>FUN_14062b210</c> end into <c>FUN_14067e8b0</c>), so a mod's "Trader /GENNAME/" takes a line of its GENNAME word swap.
    /// </summary>
    public string ExpandName(World world, CharacterId me, string name) =>
        name.IndexOf('/') < 0 ? name : Said(Expand(world, new Conversation { Npc = me, Player = CharacterId.None, Event = 0 }, null, name, me, CharacterId.None));

    string Expand(World world, Conversation c, DialogueNode? line, string text, CharacterId me, CharacterId other, int depth = 0)
    {
        if (depth > 4 || text.IndexOf('/') < 0) return text;
        var sb = new StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('/', i);
            int close = open < 0 ? -1 : text.IndexOf('/', open + 1);
            if (close < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, open - i);
            string token = text[(open + 1)..close];
            string value = Token(world, c, line, token.Trim(), me, other, depth);
            bool capital = sb.Length == 0 || sb.ToString().EndsWith(". ", StringComparison.Ordinal) || sb.ToString().EndsWith("! ", StringComparison.Ordinal) || sb.ToString().EndsWith("? ", StringComparison.Ordinal);
            if (capital && value.Length > 0) value = char.ToUpperInvariant(value[0]) + value[1..];
            sb.Append(value);
            i = close + 1;
        }
        return sb.ToString();
    }

    string Token(World world, Conversation c, DialogueNode? line, string token, CharacterId me, CharacterId other, int depth)
    {
        switch (token)
        {
            case "COST": return line?.Cost is { } cost ? cost.ToString("N0", CultureInfo.InvariantCulture) : "";
            case "MYNAME": return NameOf(world, me);
            case "YOURNAME": return Cold(world, other)?.Name ?? "you";
            case "TARGETFACTION": return Cold(world, other) is { } o && _data.Relations.Contains(o.Faction) ? _data.Factions[o.Faction].Name : "you";
            case "PLAYERFACTION": return _data.Relations.Contains(world.Player.Faction) ? _data.Factions[world.Player.Faction].Name : "";
        }
        if (_dialogue.WordSwap(token) is not { } swap) return "";
        // The full line walk over the swap's lines (FUN_14067a530 :168-220 into FUN_14067a0c0): the target is the conversation's, or the passed one when that is the owner; run a second time
        // when the first finds nothing; the picked line and its parents (the swap, a root it came through) are stamped used (FUN_140675630).
        var walkTarget = c.Player.IsNone || c.Player == me ? other : c.Player;
        var picked = Pick(world, c, swap.Children, 0, walkTarget) ?? Pick(world, c, swap.Children, 0, walkTarget);
        if (picked is not { } p) return "";
        double now = world.GameHours;
        _lastUse[p.Line.StringId] = now;
        _lastUse[swap.StringId] = now;
        if (p.Root is { } through) _lastUse[through.StringId] = now;
        return Expand(world, c, p.Line, PickText(world, p.Speaker, p.Line), me, other, depth + 1);
    }
}
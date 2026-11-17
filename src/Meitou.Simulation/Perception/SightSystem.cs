using Meitou.Data.Gameplay;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Perception;

/// <summary>
/// The sight pass (<c>FUN_14085a5f0</c> / <c>FUN_140859680</c>, docs/game/dialogue.md "Sight", <b>Verified</b>): on each of a character's update
/// turns it reacts to the next character of its sensed list: the greetings and other sight events, and the crime events of escaped prisoners,
/// escaping slaves and intruders (<see cref="SightCrimes"/>). It speaks through the dialogue system and runs only while that has its event lines on
/// (<see cref="Running"/>); without it the arrest and crime systems take the crime events' engine fallback.
/// </summary>
public sealed class SightSystem(PopulationData data) : ITickSystem, IStateHashed
{
    #region Rotation

    /// <summary>
    /// The NPC / player-character pairs sensed at the world's first look (a new game or a load) and not lost from the NPC's senses since: no sight
    /// line for them (engine choice; the original greets a character arriving, not one already beside it at the start, Observed). Hashed.
    /// </summary>
    readonly SortedSet<(CharacterId Npc, CharacterId Player)> _quiet = new(Comparer<(CharacterId, CharacterId)>.Create(
        (a, b) => DialogueSystem.CompareIds(a.Item1, b.Item1) is var c and not 0 ? c : DialogueSystem.CompareIds(a.Item2, b.Item2)));
    /// <summary>
    /// Whether the first look (<see cref="_quiet"/>'s fill) has run.
    /// </summary>
    bool _looked;
    /// <summary>
    /// The tick of the first sight step (-1 before it): the first look waits one aggro look round after it.
    /// </summary>
    long _firstTick = -1;
    /// <summary>
    /// Each character's sight cursor over its sensed list (<c>FUN_14085a5f0</c>'s <c>+0x100</c>), kept only while past the head. Hashed.
    /// </summary>
    readonly SortedDictionary<CharacterId, int> _sightCursor = new(Comparer<CharacterId>.Create(DialogueSystem.CompareIds));

    /// <summary>
    /// Whether the sight pass runs in <paramref name="world"/>: it has the pass and a dialogue system with its event lines on.
    /// </summary>
    public static bool Running(World world) =>
        world.SystemOf<SightSystem>() is not null && world.SystemOf<DialogueSystem>() is { Settings.AmbientEvents: true };

    /// <summary>
    /// The slow-world step: the sight pass, while the dialogue's event lines are on.
    /// </summary>
    public void SlowWorld(World world)
    {
        if (world.SystemOf<DialogueSystem>() is not { Settings.AmbientEvents: true } talk) return;

        SeeOthers(world, talk);
    }

    /// <summary>
    /// Adds the quiet pairs and the sight cursors to the world hash, only while there are any.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (_quiet.Count == 0 && _sightCursor.Count == 0) return;

        hasher.Add(0x53696768);
        foreach (var (npc, pc) in _quiet)
        {
            hasher.Add(npc);
            hasher.Add(pc);
        }
        foreach (var (who, cursor) in _sightCursor)
        {
            hasher.Add(who);
            hasher.Add(cursor);
        }
    }

    /// <summary>
    /// The pass (<c>FUN_14085a5f0</c>): on each of a character's update turns (<see cref="BodySystem.UpdateTurn"/>, the character loop's budgets;
    /// <c>FUN_1405c7d40</c> runs it for a living character with a squad) it reacts to the next entry of its sensed list
    /// (<see cref="AggroSystem.SensesOf"/>, <see cref="React"/>); past the list's end the cursor goes back to the head and that turn reacts to nobody.
    /// Nothing before the world's first look (<see cref="FirstLook"/>).
    /// </summary>
    void SeeOthers(World world, DialogueSystem talk)
    {
        if (world.SystemOf<BodySystem>() is not { } bodies || world.SystemOf<AggroSystem>() is not { } aggro) return;
        if (!FirstLook(world, aggro)) return;

        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (table.Next[i].Alive && bodies.UpdateTurn(i) && table.Cold(i) is { SquadId: >= 0 } && aggro.SensesOf(i) is { } senses)
                SeeNext(world, talk, i, senses);
        }

        DropGoneCursors(world);
    }

    /// <summary>
    /// Whether the world's first look has run: one aggro look round (<see cref="AggroSettings.ScanEveryTicks"/>) after the pass's first step (a new
    /// game or a load), when every character has sensed once, the pairs it finds fill <see cref="_quiet"/>.
    /// </summary>
    bool FirstLook(World world, AggroSystem aggro)
    {
        if (_looked) return true;
        if (_firstTick < 0) _firstTick = world.Tick;
        if (world.Tick - _firstTick < world.Every(aggro.Settings.ScanEveryTicks)) return false;

        _looked = true;
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { IsPlayer: false } || aggro.SensesOf(i) is not { } senses) continue;
            foreach (var e in senses.Entries)
                if (e.Now && SensedPlayer(world, e) is { } pc) _quiet.Add((table.IdOf(i), pc));
        }
        return true;
    }

    /// <summary>
    /// The player character an entry names, if it names one that still lives.
    /// </summary>
    static CharacterId? SensedPlayer(World world, in SenseEntry e)
    {
        var id = new CharacterId(e.Target, e.Generation);
        return world.Characters.TryResolveNext(id, out int t) && world.Characters.Cold(t) is { IsPlayer: true } ? id : null;
    }

    /// <summary>
    /// One turn of the looker's cursor over its sensed list (<c>FUN_14085a5f0</c>'s <c>+0x100</c>): the entry under it is reacted to and the cursor
    /// moves on; at the end it goes back to 0, and the quiet pairs whose player character the looker no longer senses are dropped.
    /// </summary>
    void SeeNext(World world, DialogueSystem talk, int slot, SenseBlock senses)
    {
        var id = world.Characters.IdOf(slot);
        var entries = senses.Entries;
        int cursor = _sightCursor.GetValueOrDefault(id);
        if (cursor >= entries.Count)
        {
            _sightCursor.Remove(id);
            if (_quiet.Count > 0) _quiet.RemoveWhere(q => q.Npc == id && !StillSensed(entries, q.Player));
            return;
        }

        _sightCursor[id] = cursor + 1;
        React(world, talk, slot, entries[cursor]);
    }

    /// <summary>
    /// Whether <paramref name="who"/> is among the entries, seen or heard at the last look.
    /// </summary>
    static bool StillSensed(IReadOnlyList<SenseEntry> entries, CharacterId who)
    {
        foreach (var e in entries)
            if (e.Target == who.Slot && e.Generation == who.Generation && e.Now) return true;

        return false;
    }

    /// <summary>
    /// Drops the cursors of characters that are gone (a cursor is kept only while it is past the head).
    /// </summary>
    void DropGoneCursors(World world)
    {
        if (_sightCursor.Count <= world.Characters.Count) return;

        List<CharacterId>? gone = null;
        foreach (var id in _sightCursor.Keys)
            if (!world.Characters.TryResolveNext(id, out _)) (gone ??= []).Add(id);
        if (gone is null) return;

        foreach (var id in gone) _sightCursor.Remove(id);
    }

    /// <summary>
    /// Whether the pair was held quiet at the first look (<see cref="_quiet"/>) and the player character is still sensed; one no longer sensed is
    /// let go.
    /// </summary>
    bool Quiet(CharacterId npc, CharacterId target, in SenseEntry e)
    {
        if (_quiet.Count == 0 || !_quiet.Contains((npc, target))) return false;
        if (e.Now) return true;

        _quiet.Remove((npc, target));
        return false;
    }

    #endregion

    #region Reactions

    /// <summary>
    /// EV_I_SEE_UNIFORM_IMPOSTER.
    /// </summary>
    const int UniformImposter = 34;
    /// <summary>
    /// EV_USING_MY_TRAINING_EQUIPMENT.
    /// </summary>
    const int UsingMyTrainingEquipment = 59;
    /// <summary>
    /// EV_I_SEE_ANIMAL_SQUAD (fired at an animal before the friend / other split).
    /// </summary>
    const int SeeAnimalSquad = 27;
    /// <summary>
    /// EV_I_SEE_NEUTRAL_SQUAD, EV_I_SEE_ENEMY_PLAYER, EV_I_SEE_ALLY_PLAYER.
    /// </summary>
    const int SeeNeutralSquad = 3, SeeEnemyPlayer = 66, SeeAllyPlayer = 67;

    /// <summary>
    /// The looker's reaction to one sensed character (<c>FUN_140859680</c>, <b>Verified</b>), in the original's order: the gates
    /// (<see cref="Gates"/>); a down target (slot 6) gets nothing; an animal event 27 I_SEE_ANIMAL_SQUAD whether seen now or not; the rest only with
    /// a sight line (Dialogue <c>+0x28</c>, <see cref="DialogueSystem.HasSightLines"/>) and an entry seen or heard at the last look: a target the
    /// looker regards as friendly (slot 126) <see cref="SeeFriend"/>, any other <see cref="SeeOther"/> for a looker free (slave state 0), not an
    /// escaped prisoner and without a pending crime. A pair held quiet since the first look stays quiet while sensed.
    /// </summary>
    void React(World world, DialogueSystem talk, int slot, SenseEntry e)
    {
        var table = world.Characters;
        var target = new CharacterId(e.Target, e.Generation);
        if (e.Target == slot || !table.TryResolveNext(target, out int t) || table.Cold(t) is not { } tc) return;
        if (Quiet(table.IdOf(slot), target, e)) return;

        var lc = table.Cold(slot)!;
        if (!Gates(world, talk, slot, lc, t, tc) || CharacterFacts.Down(tc)) return;
        if (SquadRegistry.IsAnimal(tc))
        {
            talk.Say(world, slot, t, SeeAnimalSquad);
            return;
        }
        if (!e.Now || !talk.HasSightLines(world, slot)) return;

        if (talk.RegardsFriendly(world, slot, t)) SeeFriend(world, talk, slot, lc, t, tc);
        else if (lc is { SlaveState: 0, Escaped: false, Pending: null }) SeeOther(world, talk, slot, lc, t, tc);
    }

    /// <summary>
    /// The sight pass's gates (<c>FUN_140859680</c>, <c>FUN_1408529c0</c>, <b>Verified</b>): an animal looker needs a line for event 3; a target in no
    /// squad (no platoon, slot 72) is passed over; a looker in a bed or cage (<c>+0x2f8</c>), down (slot 6) or a slave (state 1) reacts to nobody;
    /// and a looker out of view reacts only to a target in view or of the player's side (<c>+0x1a9</c>, <see cref="MedicalTimeBudget.Flagged"/>;
    /// <c>FUN_140856bc0</c>, <see cref="AggroSystem.PlayerSide"/>).
    /// </summary>
    static bool Gates(World world, DialogueSystem talk, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        if (SquadRegistry.IsAnimal(lc) && talk.SetOf(world, slot).For(SeeNeutralSquad).Count == 0) return false;
        if (tc.SquadId < 0) return false;
        if (lc.InSomething != Locks.HeldState.None || CharacterFacts.Down(lc) || lc.SlaveState == 1) return false;

        var table = world.Characters;
        return MedicalTimeBudget.Flagged(world, lc, table.Next[slot].Position) || AggroSystem.PlayerSide(world, tc)
            || MedicalTimeBudget.Flagged(world, tc, table.Next[t].Position);
    }

    /// <summary>
    /// Event 67 I_SEE_ALLY_PLAYER, the friend branch (<b>Verified</b>): only while the looker's platoon is in no campaign (<c>FUN_1409c4540</c>) and
    /// the target is no slave (state 1), of the player's faction and of a faction allied to the looker's (the same counts, <c>FUN_1406b2630</c>).
    /// </summary>
    void SeeFriend(World world, DialogueSystem talk, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        if (InCampaign(world, lc) || tc.SlaveState == 1 || !OfPlayerFaction(world, tc) || !Allied(lc.Faction, tc.Faction)) return;

        talk.Say(world, slot, t, SeeAllyPlayer);
    }

    /// <summary>
    /// The branch for a target not regarded as friendly (<b>Verified</b>): first the crime events (<see cref="SightCrimes.React"/>: 45, then for a
    /// target not regarded as an enemy 36 / 40, 17 and 9, several of which end the reaction); then, while neither side is in combat mode and the
    /// target has no pending crime, and the looker's squad does not love it (<c>FUN_140599590</c>), <see cref="SeePeaceful"/>; otherwise
    /// <see cref="TagBandit"/>; then, also in a fight, event 66 again under its test for a target without a pending crime (the original's tail,
    /// which a line just said refuses).
    /// </summary>
    void SeeOther(World world, DialogueSystem talk, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        if (SightCrimes.React(world, talk, data, slot, lc, t, tc)) return;

        var target = world.Characters.IdOf(t);
        if (!lc.InCombat && !tc.InCombat && tc.Pending is null)
        {
            if (!talk.Loves(world, lc, target)) SeePeaceful(world, talk, slot, lc, t, tc);
        }
        else TagBandit(world, talk, slot, lc, t, tc);

        if (tc.Pending is null && HostilePlayer(world, lc, tc) && !InCampaign(world, lc)) talk.Say(world, slot, t, SeeEnemyPlayer);
    }

    /// <summary>
    /// The fight branch's tag (<c>FUN_1406744a0(looker, target, 4)</c>, <b>Verified</b>): with either side in combat mode or a target with a pending
    /// crime, the looker's squad tags a target in no status town (status <c>+0x88</c>, <see cref="CharacterCold.StatusTown"/>) TEMP_ENEMY when
    /// either side's NPC class (status <c>+0xdc</c>) is Bandit (8); the wrapper's own skips apply (<see cref="DialogueSystem.TagEnemy"/>). Engine
    /// guard: also only outside every town's radius, where the status town is always set (the radius covers one not yet written by the arrest
    /// system's pass after a load or spawn), and never in a world without town data.
    /// </summary>
    static void TagBandit(World world, DialogueSystem talk, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        if (tc.StatusTown != CampaignTown.None) return;
        if (tc.NpcClass != FundamentalType.Bandit && lc.NpcClass != FundamentalType.Bandit) return;
        if (world.SystemOf<AggroSystem>()?.Towns is not { } towns) return;
        if (!Ai.AiSystem.TownAt(towns, world, world.Characters.Next[t].Position).IsNone) return;

        talk.TagEnemy(world, slot, t);
    }

    /// <summary>
    /// The peaceful branch (<b>Verified</b>): event 34 I_SEE_UNIFORM_IMPOSTER at a disguise the looker sees through (<see cref="Imposter"/>) when
    /// it has a line for it and senses no enemy; else 59 USING_MY_TRAINING_EQUIPMENT at a target training on a dummy of the looker's faction while
    /// it senses no enemy; else, with the looker's platoon in no campaign and a target that is no slave (state 1), 66 I_SEE_ENEMY_PLAYER at one of
    /// the player's faction hostile to the looker's (<c>FUN_1406b26d0</c>), 3 I_SEE_NEUTRAL_SQUAD at any other while the looker senses no enemy.
    /// </summary>
    void SeePeaceful(World world, DialogueSystem talk, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        int enemies = world.SystemOf<AggroSystem>()?.SensesOf(slot)?.Enemies ?? 0;
        if (Imposter(lc, tc) && enemies == 0 && talk.SetOf(world, slot).For(UniformImposter).Count > 0)
        {
            BookImposter(world, slot, lc, t, tc);
            talk.Say(world, slot, t, UniformImposter);
            return;
        }
        if (DialogueSystem.TrainingDummyOwner(world, t) == lc.Faction && enemies == 0)
        {
            talk.Say(world, slot, t, UsingMyTrainingEquipment);
            return;
        }
        if (InCampaign(world, lc) || tc.SlaveState == 1) return;

        if (HostilePlayer(world, lc, tc)) talk.Say(world, slot, t, SeeEnemyPlayer);
        else if (enemies == 0) talk.Say(world, slot, t, SeeNeutralSquad);
    }

    /// <summary>
    /// The uniform test of the peaceful branch (<b>Verified</b>, constants from the data dump): the target's disguise level (status <c>+0x100</c>,
    /// <see cref="CharacterCold.DisguiseLevel"/>) above 0.25 when it wears a uniform piece of the looker's faction in any slot but shirt and boots
    /// (Character vfunc <c>0x408</c>), else above 0.5 when its uniform's faction (<c>+0x108</c>) is allied to the looker's, else never (99).
    /// </summary>
    bool Imposter(CharacterCold lc, CharacterCold tc)
    {
        if (tc.DisguiseLevel <= 0) return false;

        float over = WearsUniformOf(lc.Faction, tc) ? 0.25f : tc.DisguiseFaction >= 0 && Allied(lc.Faction, tc.DisguiseFaction) ? 0.5f : 99;
        return tc.DisguiseLevel > over;
    }

    /// <summary>
    /// The seen-through impostor's booking before the line (<c>FUN_140852e10</c> with timer 20 and code 16, <b>Verified</b>): unless the looker's
    /// faction is <c>not real</c>, the target's disguise is blown (status <c>+0x110</c> 250, <see cref="CharacterCold.RecentlyWanted"/>) and a
    /// pending Uniform theft against the looker's faction is set (<see cref="Crime.CrimeSystem.SetPending"/>, the wrapper's path).
    /// </summary>
    void BookImposter(World world, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        if (data.Relations.Contains(lc.Faction) && data.Relations.Factions[lc.Faction].NotReal) return;

        tc.RecentlyWanted = Crime.CrimeSystem.RecentlyWantedSeconds;
        world.SystemOf<Crime.CrimeSystem>()?.SetPending(world, t, Crime.CrimeCode.UniformTheft, lc.Faction, world.Characters.IdOf(slot),
            Crime.CrimeSystem.WitnessTimer, viaWrapper: true);
    }

    /// <summary>
    /// Whether the character wears an item that is a uniform of <paramref name="faction"/> outside the shirt and boots slots.
    /// </summary>
    bool WearsUniformOf(int faction, CharacterCold c)
    {
        if (c.Inventory is not { } inv || !data.Relations.Contains(faction)) return false;

        string id = data.Factions[faction].Id;
        foreach (var item in inv.Items)
        {
            bool worn = item.Section is not ("shirt" or "boots") && Items.InventoryCommands.IsEquipmentSection(item.Section);
            if (worn && item.Uniform == id) return true;
        }
        return false;
    }

    /// <summary>
    /// The character's platoon is held in a campaign by its faction's war manager (<c>FUN_1409c4540</c>, any phase).
    /// </summary>
    static bool InCampaign(World world, CharacterCold c) =>
        c.SquadId >= 0 && world.Squads.Find(c.SquadId) is { PlatoonId: >= 0 } squad && world.Platoons.Find(squad.PlatoonId)?.Campaign is not null;

    /// <summary>
    /// Of the player's faction (the faction's <c>+0x250</c>).
    /// </summary>
    internal static bool OfPlayerFaction(World world, CharacterCold c) => world.Player.Faction >= 0 && c.Faction == world.Player.Faction;

    /// <summary>
    /// The same faction, or allied ones (<c>FUN_1406b2630</c>).
    /// </summary>
    bool Allied(int a, int b) => a == b || data.Relations.Contains(a) && data.Relations.Contains(b) && data.Relations.IsAlly(a, b);

    /// <summary>
    /// A target of the player's faction whose faction the looker's is hostile to (<c>FUN_1406b26d0</c>).
    /// </summary>
    bool HostilePlayer(World world, CharacterCold lc, CharacterCold tc) =>
        OfPlayerFaction(world, tc) && data.Relations.Contains(lc.Faction) && data.Relations.Contains(tc.Faction)
        && data.Relations.IsHostile(lc.Faction, tc.Faction);

    #endregion
}

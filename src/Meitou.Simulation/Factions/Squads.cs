using System.Numerics;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Factions;

/// <summary>
/// The editor's SquadMemberType, the role a member plays in its squad (docs/game/factions-squads-towns.md section 5.1).
/// </summary>
public enum SquadRole
{
    /// <summary>
    /// Member type 0, a squad member.
    /// </summary>
    Squad1 = 0,
    /// <summary>
    /// Member type 1.
    /// </summary>
    Squad2 = 1,
    /// <summary>
    /// Member type 2, the leader.
    /// </summary>
    Leader = 2,
    /// <summary>
    /// Member type 4, a slave.
    /// </summary>
    Slave = 4
}

/// <summary>
/// A squad in the world: the platoon of docs/game/game-loop.md "Factions and squads", reduced to what loaded squads need now (the active half; the
/// unloaded half is still the roaming platoon's stand-in). Plain object, changed only in the serial phases.
/// </summary>
public sealed class Squad
{
    /// <summary>
    /// The squad's id (never reused).
    /// </summary>
    public required int Id { get; init; }
    /// <summary>
    /// The SQUAD_TEMPLATE it was made from.
    /// </summary>
    public required string TemplateId { get; init; }
    /// <summary>
    /// The name the squad tabs and the squads screen show (a player's squad can be renamed, <see cref="RenameSquadCommand"/>; saved as its PLATOON
    /// record's name).
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// A player's squad's place in the squad order (the PLATOON's <c>squad index</c>, the original's platoon +0x1f2): the tabs and the squads screen
    /// go by it, then by id.
    /// </summary>
    public int Index { get; set; }
    /// <summary>
    /// The <c>platoon stringID</c> of the saved player platoon the squad was loaded from (its file); null for a squad made since, which a save writes
    /// as a new platoon.
    /// </summary>
    public string? SaveName { get; init; }
    /// <summary>
    /// The owner faction, a position in the world's faction list (-1: none).
    /// </summary>
    public int Faction { get; init; } = -1;
    /// <summary>
    /// The town it belongs to (a position in the population's site list; -1: none).
    /// </summary>
    public int Town { get; init; } = -1;
    /// <summary>
    /// Where the squad was created (members stand at offsets from it).
    /// </summary>
    public Vector3 Position { get; init; }
    /// <summary>
    /// The place and radius the squad wanders in when it has no orders (the town's centre and radius for residents).
    /// </summary>
    public Vector2 HomeCentre { get; set; }
    /// <summary>
    /// The radius of <see cref="HomeCentre"/>'s area.
    /// </summary>
    public float HomeRadius { get; set; }
    /// <summary>
    /// The building the town gave it as its home (docs/game/factions-squads-towns.md "Town residents"), or the one a save names; the AI takes it as
    /// the squad's own home. Null: none (the AI picks one).
    /// </summary>
    public Meitou.Data.World.TownBuilding? HomeBuilding { get; set; }
    /// <summary>
    /// The roaming platoon this loaded squad is the active half of (-1: a resident or other squad).
    /// </summary>
    public int PlatoonId { get; set; } = -1;
    /// <summary>
    /// The active half of a pinned saved platoon (<see cref="Platoon.Pinned"/>): a town's own squad from a save, treated as the town's squads are
    /// (AI, guards).
    /// </summary>
    public bool Pinned { get; set; }
    /// <summary>
    /// One of its town's own squads: the town made it, or a save holds it as the town's.
    /// </summary>
    public bool TownOwn => Town >= 0 && (PlatoonId < 0 || Pinned);
    /// <summary>
    /// The player faction's hidden <c>__DEAD_SQUAD__</c> its characters move to when they die (<see cref="SquadManagement.BuryDead"/>): no tab, never
    /// one of the player's squads.
    /// </summary>
    public bool Dead { get; init; }
    /// <summary>
    /// Back from its town's time unloaded: its members' minds take up their schedule where it puts them
    /// (<see cref="PopulationSettings.ResidentSchedule"/>); cleared once they have minds.
    /// </summary>
    public bool Resume { get; set; }
    /// <summary>
    /// Its members came back from a save or from their time out of the world (kept characters), not made now: their <c>wages</c> do not set its purse
    /// when it appears, as <c>Character::giveBirth</c> pays them only to a character made without saved state (<c>FUN_14062b210</c>,
    /// docs/game/economy.md "Wages"). Not saved or hashed.
    /// </summary>
    public bool Restored { get; init; }
    /// <summary>
    /// How many it was made with (its town's regeneration measures losses against it; not part of the state).
    /// </summary>
    public int SpawnSize { get; set; }
    /// <summary>
    /// The members.
    /// </summary>
    public List<CharacterId> Members { get; } = [];
    /// <summary>
    /// The member the others follow (the role-2 leader, else the first member); <see cref="CharacterId.None"/> when none is alive.
    /// </summary>
    public CharacterId Leader { get; set; } = CharacterId.None;
    /// <summary>
    /// The cached backup leader (the original's <c>backupLeader</c> <c>+0xa8</c>, found by <see cref="PopulationSystem"/>'s effective-leader rule
    /// when the leader is down or held); the platoon's place follows the effective leader. Not saved; hashed while set.
    /// </summary>
    public CharacterId Backup { get; set; } = CharacterId.None;
    /// <summary>
    /// The squad's purse in cats (save key <c>money</c> of the platoon; a roaming squad carries it over its unloading). The economy stage spends it.
    /// </summary>
    public long Money { get; set; }
    /// <summary>
    /// The squad's shop, when it is a trader (<see cref="Trading.TradeSystem"/>); null otherwise.
    /// </summary>
    public Trading.Shop? Shop { get; set; }
    /// <summary>
    /// When a squad that is no trader but has <c>vendors</c> next fills its home's containers (<see cref="Trading.TradeSystem"/>). Not hashed and not
    /// saved: the containers are.
    /// </summary>
    public long NextHomeFillTick { get; set; }
    /// <summary>
    /// The squad's AI packages and the one running (<see cref="Ai.AiSystem"/>); null for a squad the AI does not run.
    /// </summary>
    public Ai.Blackboard? Ai { get; set; }
    /// <summary>
    /// The dialogue-given AI a save or its stored state held, until the AI attaches a blackboard and applies it (then null).
    /// </summary>
    public Saving.AiSave? SavedAi { get; set; }
    /// <summary>
    /// The package it ran when it left the world (a kept or saved squad's), until the AI attaches a blackboard: an occupation goes on
    /// (<see cref="Ai.AiSystem"/>).
    /// </summary>
    public string? LeftPackage { get; set; }
    /// <summary>
    /// The active half of a save's wild platoon (<see cref="Platoon.Wild"/>): the AI runs it, as the wild sites' own squads.
    /// </summary>
    public bool Wild { get; init; }
    /// <summary>
    /// Whom a campaign squad in its assault also treats as enemies (<see cref="WarTargets"/>; written by the population's serial look, read by the
    /// aggro).
    /// </summary>
    public WarTargets War { get; set; } = WarTargets.None;
    /// <summary>
    /// Each member's formation slot (<see cref="Formation.Layout"/>), rebuilt by the movement's serial step when <see cref="FormationKey"/>
    /// (formation, head, members and roles) changes. Derived state: not saved or hashed.
    /// </summary>
    public Dictionary<CharacterId, Vector2> FormationSlots { get; set; } = [];
    /// <summary>
    /// Its template's <c>force speed</c> when 0 (walk), 1 (jog) or 2 (run), else 4 (none): the movement raises its NPC members' speed mode to it.
    /// Derived from <see cref="World.ForcedSpeeds"/> by the movement's serial step: not saved or hashed.
    /// </summary>
    public byte ForceSpeed { get; set; } = NoForceSpeed;
    /// <summary>
    /// The <see cref="ForceSpeed"/> of no forced speed.
    /// </summary>
    public const byte NoForceSpeed = 4;
    /// <summary>
    /// The key of what <see cref="FormationSlots"/> was built from (see <see cref="FormationSlots"/>).
    /// </summary>
    public ulong FormationKey { get; set; }
}

/// <summary>
/// A campaign squad's extra enemies while it assaults (docs/game/factions-squads-towns.md "Campaigns", the packages): <paramref name="Faction"/>'s
/// characters (the base package in a hostile campaign's phase 1), or with <paramref name="Everyone"/> every character the Package_WarAssault sensing
/// hook takes (<c>FUN_14028a3f0</c>, see <c>AggroSystem.WarEnemy</c>): not of its own faction, and in no town or in <paramref name="Town"/>, the
/// campaign's target (the package's mission town).
/// </summary>
public readonly record struct WarTargets(int Faction, bool Everyone, CampaignTown Town)
{
    /// <summary>
    /// No extra enemies.
    /// </summary>
    public static readonly WarTargets None = new(-1, false, CampaignTown.None);
    /// <summary>
    /// Whether the squad has extra enemies.
    /// </summary>
    public bool IsSet => Everyone || Faction >= 0;
}

/// <summary>
/// The squads of a world, by id. Ids are never reused.
/// </summary>
public sealed class SquadRegistry
{
    /// <summary>
    /// In id order for the walks (hash, refresh), and a hash index for the lookups: a find in the tree walked it, and the publish asks several
    /// per character.
    /// </summary>
    readonly SortedDictionary<int, Squad> _squads = [];
    /// <summary>
    /// The squads by id, for the lookups.
    /// </summary>
    readonly Dictionary<int, Squad> _byId = [];
    /// <summary>
    /// The next id <see cref="NextId"/> gives.
    /// </summary>
    int _nextId;

    /// <summary>
    /// The number of squads.
    /// </summary>
    public int Count => _squads.Count;
    /// <summary>
    /// The squads in id order.
    /// </summary>
    public IEnumerable<Squad> All => _squads.Values;
    /// <summary>
    /// The squad with id <paramref name="id"/>; null when there is none.
    /// </summary>
    public Squad? Find(int id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Takes the next id.
    /// </summary>
    public int NextId() => _nextId++;

    /// <summary>
    /// Adds a squad, replacing one of the same id.
    /// </summary>
    public void Add(Squad squad) => _squads[squad.Id] = _byId[squad.Id] = squad;

    /// <summary>
    /// Removes a squad; false when it is not there.
    /// </summary>
    public bool Remove(int id) => _byId.Remove(id) & _squads.Remove(id);

    /// <summary>
    /// After deaths and removals: drops removed members, and picks a new leader (<see cref="Successor"/>) when the old one is gone, or, in a squad not
    /// of <paramref name="playerFaction"/>, dead while a member lives (its corpse stays in the table, so its squad would follow it and patrol no more).
    /// Serial phases only.
    /// </summary>
    public void Refresh(CharacterTable table, int playerFaction)
    {
        foreach (var squad in _squads.Values)
        {
            squad.Members.RemoveAll(m => !table.TryResolveNext(m, out _));
            if (squad.Leader.IsNone || !table.TryResolveNext(squad.Leader, out int slot)
                || squad.Faction != playerFaction && IsDead(table, slot) && squad.Members.Exists(m => !IsDead(table, m.Slot)))
                squad.Leader = Successor(squad.Members, table);
        }
    }

    /// <summary>
    /// Who leads an NPC squad that has no leader: its first living member that is not an animal, else its first living one, else its first that is
    /// not an animal, else its first (docs/game/factions-squads-towns.md "Squad leader"; engine choice, so a goat never leads a caravan or says its
    /// leader's lines). None when it has no members.
    /// </summary>
    public static CharacterId Successor(List<CharacterId> members, CharacterTable table) =>
        FirstOf(members, table, living: true, person: true) ?? FirstOf(members, table, living: true, person: false)
        ?? FirstOf(members, table, living: false, person: true) ?? (members.Count > 0 ? members[0] : CharacterId.None);

    /// <summary>
    /// The first member that resolves and is alive (<paramref name="living"/>) and not an animal (<paramref name="person"/>) as asked; null: none.
    /// </summary>
    static CharacterId? FirstOf(List<CharacterId> members, CharacterTable table, bool living, bool person)
    {
        foreach (var id in members)
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } c) continue;
            if (living && IsDead(table, slot) || person && IsAnimal(c)) continue;
            return id;
        }

        return null;
    }

    /// <summary>
    /// Whether the character in <paramref name="slot"/> is dead (a corpse still in the table).
    /// </summary>
    static bool IsDead(CharacterTable table, int slot) => table.Cold(slot)?.Medical?.Dead == true;

    /// <summary>
    /// Whether a character is an animal: its fighter or its look says so.
    /// </summary>
    public static bool IsAnimal(CharacterCold c) => c.Fighter?.IsAnimal == true || c.Appearance?.Animal is not null;

    /// <summary>
    /// Adds the id counter and the squads to the world's state hash.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        hasher.Add(_squads.Count);
        hasher.Add(_nextId);
        foreach (var s in _squads.Values)
        {
            hasher.Add(s.Id);
            hasher.Add(s.Faction);
            hasher.Add(s.Town);
            hasher.Add(s.PlatoonId);
            hasher.Add(s.Money);
            hasher.Add(s.HomeCentre);
            hasher.Add(s.HomeRadius);
            hasher.Add(s.Position);
            hasher.Add(s.Leader);
            if (!s.Backup.IsNone) hasher.Add(s.Backup);   // nothing for a squad that never needed one, so the pinned hashes stay
            if (s.Index != 0) hasher.Add(0x51647800 + s.Index);   // nothing for a squad in the first place, so the pinned hashes stay
            if (s.Resume) hasher.Add(0x52657375);
            if (s.Dead) hasher.Add(0x44656164);
            hasher.Add(s.Members.Count);
            foreach (var m in s.Members)
            {
                hasher.Add(m);
            }
            s.Shop?.Hash(ref hasher);   // nothing for squads that do not trade, so the pinned hashes of worlds without shops stay
            s.Ai?.Hash(ref hasher);     // nor for squads without AI packages
            if (s.War.IsSet)
            {
                hasher.Add(s.War.Faction);
                hasher.Add(s.War.Everyone);
                hasher.Add(s.War.Town.Site);
                hasher.Add(s.War.Town.PlayerTown);
            }   // nor for squads not assaulting
        }
    }
}
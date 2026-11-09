using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Data.Gameplay.Economy;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>
    /// One condition (<c>FUN_140676950</c>, docs/game/dialogue.md "Conditions"): <c>who</c> 0 tests the speaker, any other value the target. Yes/no
    /// conditions pass when the computed answer equals value &gt; 0; counts compare by <c>compare by</c> (Num3: 1 &lt;, 2 &gt;, else ==; Num2: 1
    /// &lt;, else &gt;=). Conditions whose inputs Meitou does not have (town wall locks, strength ratings, head counts, training equipment) compute
    /// "no".
    /// </summary>
    bool Condition(World world, Conversation c, DialogueCondition dc, CharacterId me, CharacterCold mc, CharacterId other)
    {
        var subjectId = dc.Who == 0 ? me : other;
        var s = Cold(world, subjectId);
        bool b = dc.Value > 0;
        bool Bool(bool computed) => computed == b;
        bool Num3(double v) => dc.CompareBy switch { 1 => v < dc.Value, 2 => v > dc.Value, _ => v == dc.Value };
        bool Num2(double v) => dc.CompareBy == 1 ? v < dc.Value : v >= dc.Value;
        if (dc.Condition == 76) return Bool(s is not null);
        if (dc.Condition == 13) return Num3(_counters.GetValueOrDefault((me, c.Event)).Count);
        var relations = _data.Relations;
        // A missing target (who != 0) becomes the player's nearest selected character, else the active squad's first, for this and the later
        // conditions and as the conversation's target.
        if (s is null && dc.Who != 0 && SubjectFallback(world, me) is { } found)
        {
            if (!c.DryRun) c.Player = found;
            other = subjectId = found;
            s = Cold(world, found);
        }
        if (dc.Condition >= SimulationExtensions.FirstModCode) return ModCondition(world, me, s is null ? CharacterId.None : subjectId, dc);
        if (dc.Condition == 12) return Num3(world.Research.TechLevel);   // the research manager's tech level (*(*(game + 0x38) + 8), FUN_140676950), whatever the subject
        if (s is null)
        {
            // No player character at all: the faction-level test (FUN_140671530) against the player's faction; everything else passes; DC 11 is the
            // speaker's own squad's.
            int player = world.Player.Faction;
            return dc.Condition switch
            {
                1 => Num2(player == mc.Faction ? 100 : relations.Contains(mc.Faction) && relations.Contains(player) ? (int)relations.Get(mc.Faction, player) : 0),
                2 => Num2(world.Player.Money),
                3 => Num2((int)relations.GlobalTrust(player)),
                11 => Bool(HouseLocked(world, mc)),
                _ => true,
            };
        }
        if (dc.Condition == 10) return Num3(relations.Rank(mc.Faction));   // the speaker's faction's rank, whatever `who` says
        int meF = mc.Faction, sF = s.Faction;
        switch (dc.Condition)
        {
            case 6 or 15 or 34 or 51: return true;
            // WEAKER_THAN_ME / STRONGER_THAN_ME: the combat ratings, strictly more than 1.5 times (FUN_140676950).
            case 21 or 22 when world.Characters.TryResolveNext(me, out int ms) && world.Characters.TryResolveNext(subjectId, out int ss):
                {
                    float mine = Combat.CombatSystem.Rating(world, ms), theirs = Combat.CombatSystem.Rating(world, ss);
                    return Bool(dc.Condition == 21 ? mine > theirs * 1.5f : theirs > mine * 1.5f);
                }
            case 1: return Num2(sF == meF ? 100 : relations.Contains(meF) && relations.Contains(sF) ? (int)relations.Get(meF, sF) : 0);
            case 2: return Num2(world.Player.Money);
            case 3: return Num2((int)relations.GlobalTrust(sF));   // S's faction's global trust (FactionRelations +0x18, FUN_140671530)
            case 7 or 8:
                {
                    // Caged by the speaker's faction (7) or another (8); when S is the speaker, by the target's faction instead (FUN_140676950).
                    if (Cold(world, other) is not { } tcold || !s.IsImprisoned || s.InWhat is not { } cell || world.Fixtures.Find(cell) is not { } cell2) return !b;
                    int by = subjectId == me ? tcold.Faction : meF;
                    return Bool(dc.Condition == 7 ? cell2.Owner == by : cell2.Owner != by);
                }
            case 4 or 5:
                return Bool(Cold(world, s.Carrying) is { } body && (body.Medical?.Dead ?? false) == (dc.Condition == 5) && (body.Bounties?.BookedAmount(_data.Relations, meF) ?? 0) > 0);
            case 9: return Bool(s.SquadId >= 0 && world.Squads.Find(s.SquadId) is { } sq && _data.Db.Find(sq.TemplateId)?.GetBool("is trader") == true);   // the template's flag, not its stock
            case 11: return Bool(HouseLocked(world, mc));   // the speaker's squad, whatever `who` says
            case 14: return Bool(s.IsImprisoned);
            case 16:
                {
                    var whom = subjectId == me ? other : subjectId;
                    return Bool(Position(world, whom) is { } p && Position(world, me) is { } q && Vector3.DistanceSquared(p, q) < 67600);
                }
            case 18: return Num2((int)Memory.HoursSince(world, mc.SquadId, subjectId));   // `who` 0 asks the squad about its own speaker: never noted
            case 17: return Bool(mc.SquadId >= 0 && world.Squads.Find(mc.SquadId)?.HomeBuilding is { } home && Position(world, subjectId) is { } inside && InBuilding(home, inside));
            case 19: return Bool(s.SquadId < 0 || world.Squads.Find(s.SquadId)?.Leader == subjectId);
            case 20: return Bool(Memory.Find(world, mc.SquadId, subjectId) is not null);
            case 24: return Bool(relations.Contains(meF) && relations.Contains(sF) && relations.IsAlly(meF, sF));
            case 25: return Bool(relations.Contains(meF) && relations.Contains(sF) && relations.IsHostile(meF, sF));
            case 27: return Bool(Parts(s, BodyPartType.Leg).Any(p => p.Flesh < 0));
            case 28: return Bool(Parts(s, BodyPartType.Arm).Any(p => p.Flesh < 0));
            case 29: return Bool(Parts(s, BodyPartType.Head).Any(p => Fraction(p) < 0.2f));
            case 30 or 42: return Bool(s.Medical is { Unconscious: false } m && m.Parts.Any(p => p.Template.Vital && Fraction(p) < 0.1f));
            case 31: return Bool(Position(world, subjectId) is { } where && TownAt(where) is >= 0 and var town && (town >= _data.SiteFactions.Count || _data.SiteFactions[town] != world.Player.Faction));
            case 32: return subjectId == me ? b : Bool(Speed(world, subjectId) > (world.Characters.TryResolveNext(subjectId, out int rs) ? world.Characters.Next[rs].WalkSpeed * 1.2f : float.MaxValue));
            case 33: return Bool(CopsAround(world, me));
            case 35: return Num3(SquadOf(world, subjectId).Count(m => world.Characters.IsAlive(m)));
            case 36: return Bool(s.IsPlayer || (world.Player.Faction >= 0 && sF == world.Player.Faction));
            case 37: return Num3(SquadOf(world, subjectId).Count(m => Cold(world, m)?.Inventory?.Backpack is not null));
            case 38: return Bool(SquadOf(world, subjectId).All(m => Cold(world, m)?.Fighter?.IsAnimal == true));
            case 39:
                {
                    // IS_OUTNUMBERED: S's sensed enemies above 1.3 x its sensed conscious allies, strict (SensoryData +0x94 numEnemies, +0x4c
                    // numConsciousAllies; the same counts as playing dead).
                    if (world.SystemOf<Combat.CombatSystem>() is not { } combat || !world.Characters.TryResolveNext(subjectId, out int ss)) return !b;
                    var (enemies, _, allies) = BodySystem.Senses(world, combat, ss, s);
                    return Bool(enemies > allies * 1.3f);
                }
            case 40: return Num2(s.RecentlyWanted > 0 || s.Recognition >= 1 ? s.Bounties?.BookedAmount(_data.Relations, meF) ?? 0 : 0);
            case 41: return Bool(s.Medical is { Unconscious: true, Dead: false });
            case 43: return Bool(SquadOf(world, subjectId).All(m => Cold(world, m)?.Medical?.Incapacitated != false));
            case 44: return Bool(s.Medical?.Dead == true);
            case 45: return Bool(s.Appearance?.Female == true);
            // Carrying nobody it is the value's negation (value 0 true); carrying someone only value 1 can pass (FUN_140676950 case 0x2e).
            case 46:
                return Cold(world, s.Carrying) is not { } carried ? !b
                : b && carried is { Medical.Dead: false, NpcClass: FundamentalType.Adventurer or FundamentalType.Bandit or FundamentalType.Slave or FundamentalType.Civilian };
            case 47: return Num2(s.Bounties?.BookedAmount(_data.Relations, meF) ?? 0);
            case 48: return Bool(s.Fighter is { IsAnimal: false, Weapon: null });
            case 50: return Bool(world.Squads.Find(mc.SquadId)?.Ai is { } board && board.MissionTarget == subjectId);
            case 52: return Bool(Loves(world, mc, subjectId));
            case 53: return Bool(Hates(world, mc, subjectId));
            case 54: return Bool(ScrewOver(world, me, mc, subjectId, s));
            case 55: return Bool(Helps(world, me, mc, subjectId, s));
            case 56: return Bool(s.InCombat);
            // WITHIN_TOWN_WALLS: S's gate code above 0 (Character slot 35, docs/game/pathfinding.md "Gate codes"); no gate pass yet reads as the open
            // world.
            case 57: return Bool(Position(world, subjectId) is { } walled && world.SystemOf<Locks.LockSystem>()?.Regions?.CodeAt(walled) > 0);
            // TOWN_WALLS_LOCKED_UP: S's current town has gates and none of their doors is open (FUN_140929820 behind Town vfunc 0x300); S in no town
            // reads false.
            case 58: return Bool(Position(world, subjectId) is { } inTown && TownAt(inTown) is >= 0 and var walledTown && Locks.LockSystem.TownLockedUp(world, _data.Sites[walledTown]));
            case 59: return Bool(s.SlaveState is 1 or 2);
            case 61: return Bool(s.SlaveState is 1 or 2 && s.SlaveOwner >= 0 && s.SlaveOwner == meF);
            case 62: return Bool(s.SlaveState > 1);
            case 64: return Bool(Locks.LockSystem.WornShackles(s) is not null);
            case 60: return Bool(Position(world, me) is { } here && NearestTownOf(sF, here) is { } d && d <= 23040);
            case 63: return Bool(s.IsImprisoned && s.InWhat is { } cage && world.Fixtures.Find(cage)?.Lock is { EffectivelyLocked: true });
            // Same RACE_GROUP (two races in no group also count as the same).
            case 65: return Bool(s.Race is not null && mc.Race is not null && _dialogue.RaceGroupOf(s.Race.StringId)?.Group == _dialogue.RaceGroupOf(mc.Race.StringId)?.Group);
            case 66: return Bool(s.IsPlayer && world.Player.Money >= (s.Bounties?.BookedAmount(_data.Relations, meF) ?? 0));
            case 67: return Bool((s.Orders & CharacterOrders.Sneak) != 0 && !s.InCombat);
            case 68: return Bool(world.Characters.TryResolveNext(subjectId, out int indoors) && world.Sight.BuildingOf(indoors) is not null);
            case 69: return Num2(HasIllegalItem(world, c, me, mc, subjectId) ? 1 : 0);
            case 71: return Bool(s.Medical is { } fed && fed.Hunger < 1.7f);
            case 72:
                {
                    var looks = SquadOf(world, subjectId).Select(m => Cold(world, m)?.Appearance?.Female).Where(f => f is not null).ToArray();
                    return Bool(looks.Length == 0 || (looks.Contains(true) && looks.Contains(false)));
                }
            case 73:
                // The speaker's current town when it is the player's: that town's level (Town +0x388); false otherwise, whatever the value. Engine
                // reading of "current town": the player outpost whose centre is within the player-town extent of the speaker.
                return _population is not null && Position(world, me) is { } standing && PlayerTownAt(world, standing) is { } ownTown && Num3(_population.TownLevel(world, ownTown.Number));
            case 74: return Num3(PlayersBestTownLevel(world));
            case 75:
                return Bool(Position(world, subjectId) is { } loc && TownAt(loc) is >= 0 and var ptown && ptown < _data.SiteFactions.Count && world.Player.Faction >= 0
                && _data.SiteFactions[ptown] == world.Player.Faction);
            case 78: return Bool(world.Squads.Find(mc.SquadId)?.Ai?.Contract is not null);
            case 77:
                return s.NpcClass is FundamentalType.LawEnforcement or FundamentalType.Trader or FundamentalType.Diplomat or FundamentalType.Military ? !b : b;
            case 79: return Bool(s.Race?.IsRobot == true || (s.Medical?.Parts.Any(p => p.Limb == LimbState.Replaced) ?? false));
            default: return Bool(false);
        }
    }

    /// <summary>
    /// DC 11 BUILDING_IS_CLOSED_AND_SECURED (<c>FUN_140676950</c> case 0xb): the speaker's squad's "house locked" flag (<c>homelok</c>,
    /// <c>FUN_140791ba0</c> +0xe8), which Meitou keeps per home building (<see cref="Locks.LockSystem.IsHouseLocked"/>).
    /// </summary>
    static bool HouseLocked(World world, CharacterCold mc) =>
        mc.SquadId >= 0 && world.Squads.Find(mc.SquadId) is { } squad && (squad.Ai?.OwnHome ?? squad.HomeBuilding)?.Key is { } home
        && world.SystemOf<Locks.LockSystem>()?.IsHouseLocked(world, home) == true;

    /// <summary>
    /// The subject a condition about a missing target takes (<c>FUN_140676950</c>): the character the player has selected that is nearest to
    /// <paramref name="me"/> (<c>FUN_1407f4ba0</c>: squared distance, no cap, the first of equals), else the first member of the player's active
    /// squad (<c>FUN_1407f5710</c>, <see cref="ActiveSquad"/>); none when the player has neither.
    /// </summary>
    CharacterId? SubjectFallback(World world, CharacterId me)
    {
        var table = world.Characters;
        if (Position(world, me) is { } at)
        {
            CharacterId? best = null;
            float bestD = float.MaxValue;
            foreach (var id in world.Player.Selection)
            {
                if (!table.TryResolveNext(id, out int s) || !table.Next[s].Alive) continue;
                float d = Vector3.DistanceSquared(table.Next[s].Position, at);
                if (d < bestD) (best, bestD) = (id, d);
            }
            if (best is not null) return best;
        }
        if (ActiveSquad(world) is { } squad)
            foreach (var id in squad.Members)
                if (table.TryResolveNext(id, out int s) && table.Next[s].Alive) return id;
        return null;
    }

    // Per culture faction: its trade culture (Faction +0x90), built on first use.
    TradeCulture?[]? _factionCultures;
    // When "{1} has been discovered smuggling {2}" was last posted per carrier and item (game hours); display only, not hashed.
    readonly Dictionary<(CharacterId, string), double> _smugglingPosted = [];

    /// <summary>
    /// DC_HAS_ILLEGAL_ITEM (<c>FUN_140676950</c>, docs/game/dialogue.md "Conditions"): a member of S's squad that Me perceives (<see cref="Known"/>)
    /// carries an item on the <c>illegal goods</c> of the culture of Me's town's faction, else of Me's faction. Each carrier found posts the
    /// smuggling message (not on the Talk menu's dry run, and once per carrier and item per game hour: engine choice).
    /// </summary>
    bool HasIllegalItem(World world, Conversation c, CharacterId me, CharacterCold mc, CharacterId subject)
    {
        int faction = Position(world, me) is { } at && TownAt(at) is >= 0 and var town && town < _data.SiteFactions.Count && _data.SiteFactions[town] >= 0 ? _data.SiteFactions[town] : mc.Faction;
        _factionCultures ??= [.. _data.Factions.Select(f => TradeCulture.From(_data.Db, f.TradeCulture))];
        if ((uint)faction >= (uint)_factionCultures.Length || _factionCultures[faction] is not { Illegal.Count: > 0 } culture) return false;
        bool found = false;
        foreach (var m in Known(world, me, subject))
        {
            if (Cold(world, m) is not { Inventory: { } inventory } carrier || inventory.All().FirstOrDefault(i => culture.Illegal.Contains(i.Record)) is not { } item) continue;
            found = true;
            if (c.DryRun || (_smugglingPosted.TryGetValue((m, item.Record), out double last) && world.GameHours - last < 1)) continue;
            _smugglingPosted[(m, item.Record)] = world.GameHours;
            world.Post(Loc.Tr("{1} has been discovered smuggling {2}", carrier.Name, _data.Db.Find(item.Record)?.Name ?? item.Record));
        }
        return found;
    }

    /// <summary>
    /// The player's nearest outpost whose radius holds <paramref name="at"/>, or null.
    /// </summary>
    Construction.PlayerTown? PlayerTownAt(World world, Vector3 at)
    {
        Construction.PlayerTown? best = null;
        float bestD = float.MaxValue;
        foreach (var town in world.Buildings.Towns)
        {
            if (town.Owner != world.Player.Faction) continue;
            float d = Vector2.DistanceSquared(new(at.X, at.Z), new(town.Position.X, town.Position.Z));
            if (d <= town.Radius * town.Radius && d < bestD) (best, bestD) = (town, d);
        }
        return best;
    }

    /// <summary>
    /// DC_PLAYERS_BEST_TOWN_LEVEL (<c>FUN_140676950</c>): the highest level over the player faction's towns (Town +0x388,
    /// <see cref="PopulationSystem.TownLevel"/>); -1 with none.
    /// </summary>
    int PlayersBestTownLevel(World world)
    {
        int best = -1;
        if (_population is null) return best;
        foreach (var town in world.Buildings.Towns)
            if (town.Owner == world.Player.Faction) best = Math.Max(best, _population.TownLevel(world, town.Number));
        return best;
    }

    /// <summary>
    /// DC_IN_MY_BUILDING, engine reading (as the lock rounds'): within the building's farthest node or bed + 10 (at least 30) of its origin, on the
    /// ground.
    /// </summary>
    static bool InBuilding(Meitou.Data.World.TownBuilding b, Vector3 at)
    {
        float r = 0;
        foreach (var s in b.Spots) r = MathF.Max(r, Vector2.Distance(new(s.Position.X, s.Position.Z), new(b.Position.X, b.Position.Z)));
        r = MathF.Max(30, r + 10);
        return Vector2.DistanceSquared(new(at.X, at.Z), new(b.Position.X, b.Position.Z)) <= r * r;
    }

    static IEnumerable<HealthPart> Parts(CharacterCold c, BodyPartType type) => c.Medical?.Parts.Where(p => p.Template.Type == type) ?? [];

    static float Fraction(HealthPart p) => p.Flesh / MathF.Max(1e-3f, p.MaxHp);

    static Vector3? Position(World world, CharacterId id) => world.Characters.TryResolveNext(id, out int s) ? world.Characters.Next[s].Position : null;

    static float Speed(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int s) ? new Vector2(world.Characters.Next[s].Velocity.X, world.Characters.Next[s].Velocity.Z).Length() : 0;

    /// <summary>
    /// DC_COPS_AROUND, engine reading of the speaker's sensed law enforcement: an awake law-enforcement NPC within the outdoor sight.
    /// </summary>
    static bool CopsAround(World world, CharacterId me)
    {
        if (Position(world, me) is not { } at) return false;
        var table = world.Characters;
        float r = AggroSystem.OutdoorSight;
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { NpcClass: FundamentalType.LawEnforcement, Medical.Incapacitated: false } && table.IdOf(i) != me
                && Vector3.DistanceSquared(table.Next[i].Position, at) <= r * r) return true;
        return false;
    }

    /// <summary>
    /// The ground distance from <paramref name="at"/> to the nearest placed town of <paramref name="faction"/>; null: none.
    /// </summary>
    float? NearestTownOf(int faction, Vector3 at)
    {
        float? best = null;
        for (int i = 0; i < _data.Sites.Count && i < _data.SiteFactions.Count; i++)
        {
            if (_data.SiteFactions[i] != faction) continue;
            float d = Vector2.Distance(new(at.X, at.Z), new(_data.Sites[i].Position.X, _data.Sites[i].Position.Z));
            if (best is null || d < best) best = d;
        }
        return best;
    }
}
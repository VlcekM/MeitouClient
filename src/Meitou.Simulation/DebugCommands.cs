using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Towns;

namespace Meitou.Simulation;

/// <summary>
/// The game's F10 debug menu and dev console (Meitou only, docs/game/ui-input.md "Debug menu", "Dev console"): cheats sent as commands, so they apply
/// between ticks like any input.
/// </summary>
public abstract record DebugCommand : SimCommand;

/// <summary>
/// Adds <see cref="Delta"/> cats to the player's money (negative takes it away, never below 0).
/// </summary>
public sealed record DebugMoney(long Delta) : DebugCommand;

/// <summary>
/// Puts the characters on the ground at <see cref="Target"/> (side by side, 10 units apart) and drops their orders.
/// </summary>
public sealed record DebugTeleport(IReadOnlyList<CharacterId> Characters, Vector3 Target) : DebugCommand;

/// <summary>
/// Heals the characters (<see cref="MedicalState.Heal"/>); with <see cref="Limbs"/> their lost and crushed limbs come back first
/// (<see cref="MedicalState.RestoreLimbs"/>).
/// </summary>
public sealed record DebugHeal(IReadOnlyList<CharacterId> Characters, bool Limbs = false) : DebugCommand;

/// <summary>
/// Sets every stat of the characters to 100.
/// </summary>
public sealed record DebugMaxSkills(IReadOnlyList<CharacterId> Characters) : DebugCommand;

/// <summary>
/// Turns the debug god mode on or off (<see cref="PlayerState.Godmode"/>). While on, every player character (also one recruited later) has all stats
/// at <see cref="DebugCheats.GodStat"/> (10 x the game's maximum of 100) and is healed <see cref="DebugCheats.GodHealPerSecond"/> times a second;
/// off, each gets back exactly the stats it had. Decisions: docs/procedures/playtest/agent-play-api.md, "godmode".
/// </summary>
public sealed record DebugGodmode(bool On) : DebugCommand;

/// <summary>
/// Turns the debug guard on or off (<see cref="PlayerState.Guard"/>): while on, four times a second the hostile characters within
/// <see cref="Radius"/> metres of the selected (else first) player character are killed as <c>kill --radius</c> kills (<see cref="DebugKillArea"/>;
/// animals only with <see cref="Animals"/> or while one attacks a player character) and the player characters are healed as god mode heals them.
/// </summary>
public sealed record DebugGuard(bool On, float Radius = 30, bool Animals = false) : DebugCommand;

/// <summary>
/// A debug explosion at <see cref="At"/> (X, Z): every living character within <see cref="Radius"/> metres (not the player's own unless
/// <see cref="Squad"/>) takes <see cref="Damage"/> cut damage falling off linearly with the distance (to 0.35 at the radius edge), is knocked out,
/// and is thrown away from the centre with <see cref="Force"/> world units/s (also falling off), by the combat system's knock-down push.
/// </summary>
public sealed record DebugExplode(Vector2 At, float Radius = 40, float Force = 100, float Damage = 40, bool Squad = false, string On = "", int Level = ExplodeLevels.Normal) : DebugCommand;

/// <summary>
/// The explosion's five strength levels (<c>explode --level</c>, F10 "Explosion level"): radius m / force / damage per level; the level only sets
/// those three numbers, flags after it override them.
/// </summary>
public static class ExplodeLevels
{
    public const int Normal = 3;
    public static readonly string[] Names = ["underkill", "light", "normal", "overkill", "ridiculous"];

    /// <summary>
    /// The blast of a level; <paramref name="click"/> is the explode-on-click variant, whose normal level is larger (80 / 160 / 100).
    /// </summary>
    public static (float Radius, float Force, float Damage) Values(int level, bool click) => level switch
    {
        1 => (12, 35, 15),
        2 => (25, 65, 35),
        4 => (80, 200, 300),
        5 => (250, 600, 1000),
        _ => click ? (80, 160, 100) : (40, 100, 40),
    };

    /// <summary>
    /// A level from "1" to "5" or its name.
    /// </summary>
    public static bool TryParse(string word, out int level)
    {
        level = 0;
        if (int.TryParse(word, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 5) { level = n; return true; }
        int at = Array.FindIndex(Names, name => name.Equals(word, StringComparison.OrdinalIgnoreCase));
        level = at + 1;
        return at >= 0;
    }

    public static string Label(int level) => $"level {level} ({Names[Math.Clamp(level, 1, 5) - 1]})";
}

/// <summary>
/// Who a radius kill hits, shared by the console's <c>kill --radius</c> and the guard: never a player character or the player's faction, never the
/// dead, animals only when asked for.
/// </summary>
public static class DebugKillArea
{
    public static bool Hits(bool isPlayer, int faction, int playerFaction, bool animal, bool dead, bool includeAnimals) =>
        !(isPlayer || (playerFaction >= 0 && faction == playerFaction) || (animal && !includeAnimals) || dead);
}

/// <summary>
/// One cut of <see cref="Cut"/> damage to each character: on the part whose template string id is <see cref="Part"/> (a character without one is
/// skipped), else on a part chosen as a blow's is.
/// </summary>
public sealed record DebugDamage(IReadOnlyList<CharacterId> Characters, float Cut, string? Part = null) : DebugCommand;

/// <summary>
/// Makes every blow land on <see cref="Part"/> (template string id, name) from now on, or as usual when null
/// (<see cref="BodyOptions.ForcedHitPart"/>).
/// </summary>
public sealed record DebugForceHits((string Id, string Name)? Part) : DebugCommand;

/// <summary>
/// Sets the <b>Global damage multiplier</b> (<see cref="BodyOptions.GlobalDamageMultiplier"/>) of the running world: blows and XP read it at once, a
/// crossbow when it is next equipped.
/// </summary>
public sealed record DebugDamageMultiplier(float Multiplier) : DebugCommand;

/// <summary>
/// Puts <see cref="Quantity"/> of item record <see cref="Record"/> on the character, at <see cref="Quality"/> (0 to 100) for the types that have one:
/// one unit into its free equipment slot (<see cref="Items.InventoryCommands.TryEquip"/>), the rest into the inventory; what does not fit is dropped
/// at the character's feet.
/// </summary>
public sealed record DebugGiveItem(CharacterId Character, string Record, int Quantity, int Quality = 0) : DebugCommand
{
    /// <summary>
    /// A weapon's exact grade (maker, model, level) in place of the <see cref="Grade"/> of its quality; null: by quality.
    /// </summary>
    public DebugWeaponGrade? Exact { get; init; }

    /// <summary>
    /// Wear the unit even when its armour slot is taken: the worn item goes to the grids; with no room nothing changes and nothing is dropped (a
    /// message says so). A backpack goes into the attach slot when it is free.
    /// </summary>
    public bool Equip { get; init; }

    /// <summary>
    /// The record types whose items have a quality (grade): armour's is <see cref="Items.ItemInstance.Quality"/>, the others' the
    /// <see cref="Items.ItemInstance.Level"/> their stats read. A weapon gets the model of <see cref="Grade"/> instead, so its name shows the grade.
    /// </summary>
    public static readonly FcsRecordType[] QualityTypes = [FcsRecordType.Weapon, FcsRecordType.Armour, FcsRecordType.Crossbow, FcsRecordType.LimbReplacement];

    /// <summary>
    /// The <c>PLAYER_WEAPONS</c> model a weapon of <paramref name="quality"/> gets: the highest level at or below it, else the lowest (null without
    /// models).
    /// </summary>
    public static (string Model, string Name, int Level)? Grade(GameDatabase db, int quality)
    {
        var models = (db.Find(CraftBench.PlayerWeapons)?.GetReferences("weapon models") ?? [])
            .Select(r => (Record: db.Find(r.TargetStringId), Level: r.Values.Value0)).Where(m => m.Record is not null).ToList();
        if (models.Count == 0) return null;
        var pick = models.Where(m => m.Level <= quality).OrderByDescending(m => m.Level).FirstOrDefault();
        if (pick.Record is null) pick = models.OrderBy(m => m.Level).First();
        return (pick.Record!.StringId, pick.Record.Name, pick.Level);
    }

    /// <summary>
    /// The record types an item can be made from.
    /// </summary>
    public static readonly FcsRecordType[] ItemTypes =
        [FcsRecordType.Weapon, FcsRecordType.Armour, FcsRecordType.Item, FcsRecordType.Container, FcsRecordType.Blueprint, FcsRecordType.MapItem, FcsRecordType.Crossbow, FcsRecordType.LimbReplacement];
}

/// <summary>
/// Removes the characters' bounties (every player character when <see cref="Characters"/> is empty): only <see cref="Faction"/>'s entry, or every
/// entry when null. A character left without bounties is no longer recently wanted.
/// </summary>
public sealed record DebugClearBounties(IReadOnlyList<CharacterId> Characters, int? Faction = null) : DebugCommand;

/// <summary>
/// Destroys carried items of a character (a test character: nothing is dropped, nothing moves to a container): <see cref="BackpackOnly"/> the worn
/// backpack with everything in it; otherwise every item in every section (grids, worn clothing and weapons, backpack with contents) except prosthetic
/// limbs and locked-on shackles.
/// </summary>
public sealed record DebugStripItems(CharacterId Character, bool BackpackOnly) : DebugCommand;

/// <summary>
/// Kills the characters (<see cref="MedicalState.Kill"/>), as a lethal hit would: the medical state says dead, the body pass does the rest.
/// </summary>
public sealed record DebugKill(IReadOnlyList<CharacterId> Characters) : DebugCommand;

/// <summary>
/// Knocks the characters out for <see cref="Seconds"/> seconds of character time (<see cref="MedicalState.KnockOut"/>; agent-playtest control
/// channel: ko), standing them down as a blow's knock-out does.
/// </summary>
public sealed record DebugKnockOut(IReadOnlyList<CharacterId> Characters, float Seconds) : DebugCommand;

/// <summary>
/// Wakes the unconscious characters at once (<see cref="MedicalState.WakeUp"/>; agent-playtest control channel: wake); the dead and the conscious are
/// left alone.
/// </summary>
public sealed record DebugWake(IReadOnlyList<CharacterId> Characters) : DebugCommand;

/// <summary>
/// Says <see cref="Text"/> over the character as a speech bubble (<see cref="World.Bark"/>; agent-playtest control channel: say); display only,
/// nothing in the world changes.
/// </summary>
public sealed record DebugSay(CharacterId Speaker, string Text) : DebugCommand;

/// <summary>
/// Finishes every research and repetition (<see cref="Construction.ResearchState.CompleteAll"/>).
/// </summary>
public sealed record DebugUnlockResearch : DebugCommand;

/// <summary>
/// Spawns a character of record <see cref="Record"/> (of the RACE <see cref="Race"/> when given: control channel `spawn-at --race`) at
/// <see cref="At"/>, alone in a squad of the first faction hostile to the player (<see cref="PopulationSystem"/> applies it).
/// </summary>
public sealed record DebugSpawn(string Record, Vector2 At, string? Faction = null, string? Race = null) : DebugCommand;

/// <summary>
/// A building placed in the level editor (docs/game/level-editor.md "Simulation side"): <see cref="Building"/>, made by the host as the boot pass
/// makes a level building (<see cref="TownBuildings.Builder"/>), joins the building list of the town with placement id <see cref="Town"/> and is
/// registered as a loaded one (<see cref="TownEdits.Place"/>).
/// </summary>
public sealed record DebugPlaceBuilding(string Town, TownBuilding Building) : DebugCommand;

/// <summary>
/// A weapon's maker (WEAPON_MANUFACTURER) and model (MATERIAL_SPECS_WEAPON) by record id or name, and its level; unset parts keep the item's.
/// </summary>
public sealed record DebugWeaponGrade(string? Maker, string? Model, int? Level)
{
    /// <summary>
    /// Sets <paramref name="item"/>'s company, material and level; false (with the reason in <paramref name="error"/>) when a record is not found.
    /// </summary>
    public bool Apply(GameDatabase db, Items.ItemInstance item, out string error)
    {
        error = "";
        GameRecord? maker = null, model = null;
        if (Maker is { Length: > 0 } && (maker = Find(db, Maker, FcsRecordType.WeaponManufacturer)) is null)
        {
            error = $"no WEAPON_MANUFACTURER '{Maker}'";
            return false;
        }

        if (Model is { Length: > 0 } && (model = Find(db, Model, FcsRecordType.MaterialSpecsWeapon)) is null)
        {
            error = $"no MATERIAL_SPECS_WEAPON '{Model}'";
            return false;
        }

        if (maker is not null) item.CompanyId = maker.StringId;
        if (model is not null) item.MaterialId = model.StringId;
        if (Level is { } level) item.Level = level;
        return true;
    }

    static GameRecord? Find(GameDatabase db, string key, FcsRecordType type) =>
        db.Find(key) is { } r && r.Type == type
            ? r
            : db.Records.Values.FirstOrDefault(x => x.Type == type && x.BaseName.Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Lays a loose item of record <see cref="Record"/> (<see cref="Quantity"/> in a stack) on the ground at <see cref="At"/> turned <see cref="Yaw"/>
/// radians (agent-playtest control channel: place-item).
/// </summary>
public sealed record DebugPlaceItem(string Record, int Quantity, Vector3 At, float Yaw) : DebugCommand
{
    /// <summary>
    /// A weapon's exact grade (the console's <c>--maker</c>, <c>--model</c>, <c>--level</c>).
    /// </summary>
    public DebugWeaponGrade? Exact { get; init; }
}

/// <summary>
/// Puts items into the inventory of the container fixture <see cref="Container"/> (agent-playtest control channel: container-put); what does not fit
/// is reported.
/// </summary>
public sealed record DebugPutItem(string Container, string Record, int Quantity) : DebugCommand;

/// <summary>
/// A building cleared in the level editor, placed or loaded: leaves its town's list and registries (<see cref="TownEdits.Clear"/>). <see cref="Key"/>
/// is its <see cref="TownBuilding.Key"/>.
/// </summary>
public sealed record DebugClearBuilding(string Key) : DebugCommand;

static class DebugCheats
{
    /// <summary>
    /// The value every stat gets in god mode: 10 x the normal maximum of 100.
    /// </summary>
    public const float GodStat = 1000;
    /// <summary>
    /// How often god mode heals: this many times a second (the tick is 1/30 s).
    /// </summary>
    public const int GodHealPerSecond = 5;
    /// <summary>
    /// How often the guard kills and heals: this many times a second.
    /// </summary>
    public const int GuardPerSecond = 4;

    /// <summary>
    /// The god mode's upkeep (<see cref="PlayerSystem.SlowWorld"/>, every tick; the heal only on its cadence). Raises the stats of each living player
    /// character that is not yet raised, heals it (<see cref="MedicalState.Heal"/>: parts, blood, wounds and bleeding, knock-out; stumps and crushed
    /// limbs come back; hunger refilled). A character that is dead, no longer a player character or met while the mode is off gets its own stats
    /// back, so nothing raised outlives the mode, a death or a squad change; the save writes the kept values
    /// (<see cref="CharacterStats.WriteSave"/>).
    /// </summary>
    public static void GodTick(World world, PopulationData? data, CharacterAssembly? assembly, bool force = false)
    {
        var table = world.Characters;
        bool on = world.Player.Godmode;
        if (!on && !force) return;
        int cadence = (int)Math.Max(1, MathF.Round(1 / (world.TickSeconds * GodHealPerSecond)));
        bool heal = on && (force || world.Tick % cadence == 0);
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { Stats: { } stats } cold) continue;
            bool wanted = on && cold.IsPlayer && cold.Medical is { Dead: false };
            if (!wanted) { stats.Unboost(); continue; }
            stats.Boost(GodStat);
            if (!heal || cold.Medical is not { } medical) continue;
            HealPlayer(world, data, assembly, table, s, cold, medical);
        }
    }

    /// <summary>
    /// The god mode's heal of one living player character, also the guard's: parts, limbs, blood, wounds, knock-out, hunger.
    /// </summary>
    static void HealPlayer(World world, PopulationData? data, CharacterAssembly? assembly, CharacterTable table, int slot, CharacterCold cold, MedicalState medical)
    {
        var id = table.IdOf(slot);
        if (data is not null && Body(world, data, id) is ({ } body, var ctx))
        {
            if (body.RestoreLimbs()) assembly?.RefreshEquipment(cold);
            body.Heal(ctx);
        }
        medical.Hunger = MedicalState.MaxHunger;
    }

    /// <summary>
    /// The guard's upkeep (<see cref="PlayerSystem.SlowWorld"/>, every tick; acts on its cadence): kills the hostile characters near the lead (the
    /// selected player character, else the first) through the same <see cref="DebugKillArea"/> test and kill as <c>kill --radius</c>, then heals
    /// every living player character. A hostile animal that does not attack a player character is spared without <c>--animals</c>.
    /// </summary>
    public static void GuardTick(World world, PopulationData? data, CharacterAssembly? assembly)
    {
        var player = world.Player;
        if (!player.Guard || data is null) return;
        var table = world.Characters;
        int cadence = (int)Math.Max(1, MathF.Round(1 / (world.TickSeconds * GuardPerSecond)));
        if (world.Tick % cadence != 0) return;
        int lead = -1;
        foreach (var picked in player.Selection)
            if (table.TryResolveNext(picked, out int s) && table.Cold(s) is { IsPlayer: true, Medical: { Dead: false } }) { lead = s; break; }
        for (int s = 0; lead < 0 && s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Cold(s) is { IsPlayer: true, Medical: { Dead: false } }) lead = s;
        if (lead < 0) return;
        var centre = table.Next[lead].Position;
        float r2 = player.GuardRadius * player.GuardRadius;
        var combat = world.SystemOf<Combat.CombatSystem>();
        var victims = new List<CharacterId>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { } cold) continue;
            if (cold.IsPlayer)
            {
                if (cold.Medical is { Dead: false } medical) HealPlayer(world, data, assembly, table, s, cold, medical);
                continue;
            }
            bool animal = CharacterFacts.Animal(cold);
            if (!DebugKillArea.Hits(false, cold.Faction, player.Faction, animal, cold.Medical is { Dead: true }, player.GuardAnimals)) continue;
            float dx = table.Next[s].Position.X - centre.X, dz = table.Next[s].Position.Z - centre.Z;
            if (dx * dx + dz * dz > r2) continue;
            bool fightsPlayer = cold.InCombat && combat?.StateOf(table.IdOf(s)) is { TargetSlot: >= 0 and var fought } fight
                && fought < table.HighWater && table.Next[fought].Alive && table.Next[fought].Generation == fight.TargetGeneration && table.Cold(fought) is { IsPlayer: true };
            if (!CharacterFacts.HostileToPlayer(data.Relations, player.Faction, cold.Faction, fightsPlayer)) continue;
            victims.Add(table.IdOf(s));
        }
        if (victims.Count > 0) Kill(world, data, victims);
    }

    /// <summary>
    /// One cut of damage to a character (a body part chosen as a blow's, or the named one); what a hit does only on its events is done here.
    /// </summary>
    static bool Hurt(World world, PopulationData data, CharacterId id, float cut, string? partId)
    {
        var table = world.Characters;
        if (Body(world, data, id) is not ({ } medical, var ctx) || DamagePart(medical, ctx, partId) is not (var part and >= 0) || !table.TryResolveNext(id, out int slot)) return false;
        var events = new List<MedicalEvent>();
        medical.ApplyHit(part, new HitDamage(cut, 0, 0), ctx, events);
        foreach (var e in events)
        {
            if (e.Kind is MedicalEventKind.Died or MedicalEventKind.KnockedOut) { TaskStart.Stand(table, slot); table.Next[slot].Velocity = Vector3.Zero; }
            else if (e.Kind == MedicalEventKind.PartSevered) table.LimbsChanged.Enqueue((slot, medical.Parts[e.Part].Template.LimbSlot));
            else if (e.Kind == MedicalEventKind.Decapitated) table.LimbsChanged.Enqueue((slot, -1));
        }
        return true;
    }

    /// <summary>
    /// How long the explosion keeps a survivor out, in character seconds.
    /// </summary>
    const float ExplodeKnockOutSeconds = 30;

    /// <summary>
    /// Damage and throw share at the radius edge (1 at the centre, linear between), so the whole radius is affected, not a small core.
    /// </summary>
    internal const float ExplodeEdgeFalloff = 0.35f;

    /// <summary>
    /// Smallest throw speed (world units/s) given to anyone in range, capped at the blast's own force: about 2.7 m of flight for a human, so the
    /// outer ring visibly moves.
    /// </summary>
    const float ExplodeMinThrow = 30;

    static void Explode(World world, PopulationData data, DebugExplode blast)
    {
        if (!float.IsFinite(blast.Radius) || !float.IsFinite(blast.Force) || !float.IsFinite(blast.Damage) || !float.IsFinite(blast.At.X) || !float.IsFinite(blast.At.Y)) return;   // non-finite input would poison positions and health: no-op
        var table = world.Characters;
        var combat = world.SystemOf<Combat.CombatSystem>();
        float radius = MathF.Max(blast.Radius, 0.01f);
        int hit = 0, knocked = 0, thrown = 0, killed = 0;
        var events = new List<MedicalEvent>();
        var targets = new List<(CharacterId Id, int Slot, float Falloff, Vector2 Away, bool Dead, float Share)>();
        float nearest = float.MaxValue, nearestY = 0;
        int squadNear = 0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { } cold || cold.Medical is null) continue;
            if (!blast.Squad && !DebugKillArea.Hits(cold.IsPlayer, cold.Faction, world.Player.Faction, false, false, true))
            {
                if (Vector2.Distance(new Vector2(table.Next[s].Position.X, table.Next[s].Position.Z), blast.At) <= radius) squadNear++;
                continue;
            }
            var offset = new Vector2(table.Next[s].Position.X - blast.At.X, table.Next[s].Position.Z - blast.At.Y);
            float d = offset.Length();
            if (d < nearest) (nearest, nearestY) = (d, table.Next[s].Position.Y);
            if (d > radius) continue;
            targets.Add((table.IdOf(s), s, 1 - (1 - ExplodeEdgeFalloff) * d / radius, d > 1e-3f ? offset / d : new Vector2(1, 0), cold.Medical.Dead, d / radius));
        }
        var bands = new int[3];
        foreach (var (id, slot, falloff, away, wasDead, share) in targets)
        {
            if (table.Cold(slot) is not { Medical: { } medical } cold) continue;
            if (!wasDead)
            {
                hit++;
                bands[share < 1f / 3 ? 0 : share < 2f / 3 ? 1 : 2]++;
                Hurt(world, data, id, blast.Damage * falloff, null);
                if (!medical.Dead && !medical.Incapacitated)
                {
                    events.Clear();
                    medical.KnockOut(ExplodeKnockOutSeconds, events);
                    if (events.Count > 0) { TaskStart.Stand(table, slot); table.Next[slot].Velocity = Vector3.Zero; }
                }
                if (medical.Dead) killed++; else if (medical.Incapacitated) knocked++;
            }
            // Everyone down (just now, before, or dead) is thrown, ragdolled or not (CombatSystem.Blast).
            if (combat is not null && combat.Blast(cold, away, MathF.Max(blast.Force * falloff, MathF.Min(blast.Force, ExplodeMinThrow)))) thrown++;
        }
        string where = $"{blast.At.X:0},{blast.At.Y:0}{(blast.On.Length > 0 ? $" (on {blast.On})" : "")} r={radius:0.#}, {ExplodeLevels.Label(blast.Level)}";
        if (hit == 0 && thrown == 0)
            world.Post(squadNear > 0 ? $"Explode at {where}: nobody to hit; only your own squad ({squadNear}) is in range (--squad includes it)"
                : $"Explode at {where}: nobody in range{(nearest < float.MaxValue ? $" (nearest living character {nearest:0} m away)" : "")}; --squad includes your own");
        else world.Post($"Explode at {where}: {hit} hit ({bands[0]} within r/3, {bands[1]} within 2r/3, {bands[2]} outer), {knocked} knocked out, {thrown} thrown, {killed} killed.");
        // A visible mark at the centre: a ring of blood splashes (display only; needs the Blood setting on).
        if (world.BloodMarks is { } marks)
        {
            var centre = new Vector3(blast.At.X, nearest < float.MaxValue ? nearestY : 0, blast.At.Y);
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4;
                marks.Enqueue(new BloodMark(BloodMarkKind.Splash, default, centre, new Vector2(MathF.Cos(a), MathF.Sin(a)) * 3, 1, 0x8A0303));
            }
        }
    }

    static void Kill(World world, PopulationData data, IEnumerable<CharacterId> characters)
    {
        var table = world.Characters;
        var deaths = new List<MedicalEvent>();
        foreach (var id in characters)
        {
            if (Body(world, data, id) is not ({ } medical, _) || !table.TryResolveNext(id, out int slot)) continue;
            deaths.Clear();
            medical.Kill(deaths);
            if (deaths.Count > 0) { MovementSystem.Stop(table, slot); table.Next[slot].Velocity = Vector3.Zero; }
        }
    }

    public static void Apply(World world, PopulationData? data, CharacterAssembly? assembly, DebugCommand command)
    {
        var table = world.Characters;
        switch (command)
        {
            case DebugPlaceBuilding place when data is not null:
                TownEdits.Place(world, data, place.Town, place.Building);
                break;
            case DebugClearBuilding clear when data is not null:
                TownEdits.Clear(world, data, clear.Key);
                break;
            case DebugPlaceItem lay when data is not null:
                if (data.Items.Create(lay.Record, lay.Quantity) is { } laid)
                {
                    if (lay.Exact?.Apply(data.Db, laid, out string bad) == false) { world.Post(bad); break; }
                    world.GroundItems.Place(laid, lay.At, lay.Yaw, null, -1);
                }
                else world.Post($"No item record {lay.Record}.");
                break;
            case DebugPutItem put when data is not null:
                if (world.Fixtures.Find(put.Container) is not { Contents: { } box }) world.Post($"No container {put.Container}.");
                else if (data.Items.Create(put.Record, put.Quantity) is not { } stored) world.Post($"No item record {put.Record}.");
                else if (!box.Add(stored)) world.Post($"{put.Quantity} x {stored.Name} did not fit into {put.Container}.");
                break;
            case DebugMoney money:
                world.Player.Money = Math.Max(0, world.Player.Money + money.Delta);
                break;
            case DebugTeleport teleport:
                int k = 0;
                foreach (var id in teleport.Characters)
                {
                    if (!table.TryResolveNext(id, out int slot)) continue;
                    float x = teleport.Target.X + 10 * k++, z = teleport.Target.Z;
                    TaskStart.Stand(table, slot);
                    table.Next[slot].Position = new Vector3(x, world.Walkability.GroundHeight(x, z, teleport.Target.Y), z);
                    table.Next[slot].Velocity = Vector3.Zero;
                }
                break;
            case DebugHeal heal when data is not null:
                foreach (var id in heal.Characters)
                    if (Body(world, data, id) is ({ } medical, var ctx))
                    {
                        // The look and kit follow the limbs (boots, two-handed holds), as after a prosthetic is fitted.
                        if (heal.Limbs && medical.RestoreLimbs() && table.TryResolveNext(id, out int healed)) assembly?.RefreshEquipment(table.Cold(healed)!);
                        medical.Heal(ctx);
                    }
                break;
            case DebugGodmode god:
                world.Player.Godmode = god.On;
                GodTick(world, data, assembly, force: true);   // on: boosted and healed at once; off: every raised character gets its stats back
                break;
            case DebugGuard guard:
                (world.Player.Guard, world.Player.GuardRadius, world.Player.GuardAnimals) = (guard.On, guard.Radius, guard.Animals);
                break;
            case DebugMaxSkills max:
                foreach (var id in max.Characters)
                    if (table.TryResolveNext(id, out int skilled) && table.Cold(skilled)?.Stats is { } stats)
                        for (var s = StatsEnumerated.None + 1; s < StatsEnumerated.End; s++) stats[s] = 100;
                break;
            case DebugDamage damage when data is not null:
                foreach (var id in damage.Characters) Hurt(world, data, id, damage.Cut, damage.Part);
                break;
            case DebugExplode blast when data is not null:
                Explode(world, data, blast);
                break;
            case DebugForceHits force when data is not null:
                data.BodyOptions.ForcedHitPart = force.Part;
                break;
            case DebugDamageMultiplier multiplier when data is not null:
                data.BodyOptions.GlobalDamageMultiplier = multiplier.Multiplier;
                break;
            case DebugClearBounties clear:
                world.Post($"Bounties cleared: {ClearBounties(world, clear)}.");
                break;
            case DebugKill kill when data is not null:
                Kill(world, data, kill.Characters);
                break;
            case DebugKnockOut knock:
                var knocks = new List<MedicalEvent>();
                foreach (var id in knock.Characters)
                {
                    if (!table.TryResolveNext(id, out int down) || table.Cold(down)?.Medical is not { } m) continue;
                    knocks.Clear();
                    m.KnockOut(knock.Seconds, knocks);
                    if (knocks.Count > 0) { TaskStart.Stand(table, down); table.Next[down].Velocity = Vector3.Zero; }
                }
                break;
            case DebugWake wake:
                foreach (var id in wake.Characters)
                    if (table.TryResolveNext(id, out int up) && table.Cold(up)?.Medical is { } sleeper && sleeper.WakeUp()) table.Next[up].Velocity = Vector3.Zero;
                break;
            case DebugSay say:
                if (table.TryResolveNext(say.Speaker, out _)) world.Bark(say.Speaker, say.Text);
                break;
            case DebugUnlockResearch when data is not null:
                world.Post($"Research unlocked: {world.Research.CompleteAll(data.Db)} finished.");
                break;
            case DebugStripItems strip:
                if (!table.TryResolveNext(strip.Character, out int stripped) || table.Cold(stripped)?.Inventory is not { } carried) break;
                int gone = carried.Items.RemoveAll(i => strip.BackpackOnly ? i.Section == "backpack_attach" : !Items.InventoryCommands.IsLimbSection(i.Section) && !i.LockedOn);
                assembly?.RefreshEquipment(table.Cold(stripped)!);
                world.Post(strip.BackpackOnly ? (gone > 0 ? "Backpack removed and destroyed with its contents (nothing dropped)." : "No backpack worn.") : $"Inventory cleared: {gone} items destroyed (nothing dropped).");
                break;
            case DebugGiveItem give when data is not null:
                if (!table.TryResolveNext(give.Character, out int owner) || table.Cold(owner)?.Inventory is not { } inventory) break;
                if (data.Items.Create(give.Record, give.Quantity) is not { } item) { world.Post($"No item record {give.Record}."); break; }
                if (give.Exact is { } exact && item.Type is FcsRecordType.Weapon or FcsRecordType.Crossbow)
                {
                    if (!exact.Apply(data.Db, item, out string bad))
                    {
                        world.Post(bad);
                        break;
                    }
                }
                else if (item.Type == FcsRecordType.Armour) (item.Quality, item.Level) = (Math.Clamp(give.Quality, 0, 100), Math.Clamp(give.Quality, 0, 100));
                else if (item.Type == FcsRecordType.Weapon && DebugGiveItem.Grade(data.Db, give.Quality) is { } grade)
                    (item.CompanyId, item.MaterialId, item.Level) = (CraftBench.PlayerWeapons, grade.Model, grade.Level);
                else if (DebugGiveItem.QualityTypes.Contains(item.Type)) item.Level = Math.Clamp(give.Quality, 0, 100);
                var wearer = table.Cold(owner);
                if (give.Equip && item.Type == FcsRecordType.Container && item.Contents is not null)
                {
                    // A backpack: worn in the attach slot when it is free (strip or `unequip backpack` first); never put into the grids or dropped.
                    if (inventory.Backpack is not null) { world.Post($"{item.Name} not worn: a backpack is already worn (remove it first); nothing dropped."); break; }
                    (item.Section, item.X, item.Y) = ("backpack_attach", 0, 0);
                    inventory.Items.Add(item.WithQuantity(1));
                    assembly?.RefreshEquipment(wearer!);
                    break;
                }
                bool worn = Items.InventoryCommands.TryEquip(inventory, item.WithQuantity(1), wearer);
                if (!worn && give.Equip && item.Type is FcsRecordType.Armour or FcsRecordType.Item && Items.InventoryCommands.IsEquipmentSection(item.EquipmentSection)
                    && item.EquipmentSection is not ("back" or "hip" or "backpack_attach") && inventory.Items.Find(i => i.Section == item.EquipmentSection) is { } displaced)
                {
                    // The slot is taken: take the worn item off, wear the new one, give the old one a place in the grids; with no room nothing
                    // changes and nothing is dropped.
                    if (!inventory.HasRoomFor(displaced)) { world.Post($"{item.Name} not worn: the worn {displaced.Name} has no room in the grids (strip the inventory first); nothing dropped."); break; }
                    inventory.Items.Remove(displaced);
                    worn = Items.InventoryCommands.TryEquip(inventory, item.WithQuantity(1), wearer);
                    if (!worn) inventory.Items.Add(displaced);
                    else if (!inventory.Add(displaced)) world.Post($"{displaced.Name} came off and did not fit the inventory; destroyed (nothing dropped).");
                }
                if (worn)
                {
                    // One unit is worn when its slot is free (as the trade window's right click); the rest of a stack goes to the grids.
                    assembly?.RefreshEquipment(table.Cold(owner)!);
                    if (item.Quantity == 1) break;
                    item = item.WithQuantity(item.Quantity - 1);
                }
                if (inventory.Add(item)) break;
                if (give.Equip) { world.Post($"{item.Quantity} x {item.Name} did not fit; not given (nothing dropped)."); break; }
                world.GroundItems.Drop(item, table.Next[owner].Position, 0);
                world.Post($"{item.Quantity} x {item.Name} did not fit; dropped on the ground.");
                break;
        }
    }

    /// <summary>
    /// Clears the bounties of <see cref="DebugClearBounties"/>; returns how many entries went.
    /// </summary>
    static int ClearBounties(World world, DebugClearBounties clear)
    {
        int removed = 0;
        foreach (var c in BountyTargets(world, clear.Characters))
        {
            if (c.Bounties is not { } ledger) continue;
            for (int k = ledger.Count - 1; k >= 0; k--)
            {
                var e = ledger.Entries[k];
                if (clear.Faction is { } only && e.Faction != only) continue;
                ledger.Remove(e);
                removed++;
            }
            if (ledger.Count > 0) continue;
            c.Bounties = null;
            c.RecentlyWanted = 0;
        }
        return removed;
    }

    /// <summary>
    /// The characters <paramref name="ids"/> names, else every player character.
    /// </summary>
    static IEnumerable<CharacterCold> BountyTargets(World world, IReadOnlyList<CharacterId> ids)
    {
        var table = world.Characters;
        if (ids.Count > 0)
        {
            foreach (var id in ids)
                if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { } c) yield return c;
            yield break;
        }
        for (int s = 0; s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Cold(s) is { IsPlayer: true } c) yield return c;
    }

    static int DamagePart(MedicalState medical, in MedicalContext ctx, string? part)
    {
        if (part is null) return medical.ChoosePart(ctx);
        for (int i = 0; i < medical.Parts.Count; i++)
            if (medical.Parts[i].Template.StringId == part) return i;
        return -1;
    }

    static (MedicalState, MedicalContext)? Body(World world, PopulationData data, CharacterId id)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { Medical: { } medical, Race: { } race } cold) return null;
        return (medical, new MedicalContext(data.Bodies.Constants, data.Bodies.Options, race)
        {
            Toughness = cold.Stats?.ScaledToughness ?? 0,
            Strength = cold.Stats?.Strength ?? 50,
            HasStats = cold.Stats is not null,
            Seed = world.Seed,
            CharacterKey = Rng.Key(table.IdOf(slot)),
        });
    }
}
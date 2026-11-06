using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>The game's F10 debug menu and dev console (Meitou only, docs/game/ui-input.md "Debug menu", "Dev console"): cheats sent as commands, so they apply between ticks like any input.</summary>
public abstract record DebugCommand : SimCommand;

/// <summary>Adds <see cref="Delta"/> cats to the player's money (negative takes it away, never below 0).</summary>
public sealed record DebugMoney(long Delta) : DebugCommand;

/// <summary>Puts the characters on the ground at <see cref="Target"/> (side by side, 10 units apart) and drops their orders.</summary>
public sealed record DebugTeleport(IReadOnlyList<CharacterId> Characters, Vector3 Target) : DebugCommand;

/// <summary>Heals the characters (<see cref="MedicalState.Heal"/>); with <see cref="Limbs"/> their lost and crushed limbs come back first (<see cref="MedicalState.RestoreLimbs"/>).</summary>
public sealed record DebugHeal(IReadOnlyList<CharacterId> Characters, bool Limbs = false) : DebugCommand;

/// <summary>Sets every stat of the characters to 100.</summary>
public sealed record DebugMaxSkills(IReadOnlyList<CharacterId> Characters) : DebugCommand;

/// <summary>
/// One cut of <see cref="Cut"/> damage to each character: on the part whose template string id is <see cref="Part"/> (a character without one is skipped),
/// else on a part chosen as a blow's is.
/// </summary>
public sealed record DebugDamage(IReadOnlyList<CharacterId> Characters, float Cut, string? Part = null) : DebugCommand;

/// <summary>Makes every blow land on <see cref="Part"/> (template string id, name) from now on, or as usual when null (<see cref="BodyOptions.ForcedHitPart"/>).</summary>
public sealed record DebugForceHits((string Id, string Name)? Part) : DebugCommand;

/// <summary>Sets the <b>Global damage multiplier</b> (<see cref="BodyOptions.GlobalDamageMultiplier"/>) of the running world: blows and XP read it at once, a crossbow when it is next equipped.</summary>
public sealed record DebugDamageMultiplier(float Multiplier) : DebugCommand;

/// <summary>
/// Puts <see cref="Quantity"/> of item record <see cref="Record"/> on the character, at <see cref="Quality"/> (0 to 100) for the types that have one: one unit into its free
/// equipment slot (<see cref="Items.InventoryCommands.TryEquip"/>), the rest into the inventory; what does not fit is dropped at the character's feet.
/// </summary>
public sealed record DebugGiveItem(CharacterId Character, string Record, int Quantity, int Quality = 0) : DebugCommand
{
    /// <summary>Wear the unit even when its armour slot is taken: the worn item goes to the grids (dropped at the feet, with a message, when they have no room).</summary>
    public bool Equip { get; init; }

    /// <summary>
    /// The record types whose items have a quality (grade): armour's is <see cref="Items.ItemInstance.Quality"/>, the others' the <see cref="Items.ItemInstance.Level"/> their stats read.
    /// A weapon gets the model of <see cref="Grade"/> instead, so its name shows the grade.
    /// </summary>
    public static readonly FcsRecordType[] QualityTypes = [FcsRecordType.WEAPON, FcsRecordType.ARMOUR, FcsRecordType.CROSSBOW, FcsRecordType.LIMB_REPLACEMENT];

    /// <summary>The <c>PLAYER_WEAPONS</c> model a weapon of <paramref name="quality"/> gets: the highest level at or below it, else the lowest (null without models).</summary>
    public static (string Model, string Name, int Level)? Grade(GameDatabase db, int quality)
    {
        var models = (db.Find(CraftBench.PlayerWeapons)?.GetReferences("weapon models") ?? [])
            .Select(r => (Record: db.Find(r.TargetStringId), Level: r.Values.Value0)).Where(m => m.Record is not null).ToList();
        if (models.Count == 0) return null;
        var pick = models.Where(m => m.Level <= quality).OrderByDescending(m => m.Level).FirstOrDefault();
        if (pick.Record is null) pick = models.OrderBy(m => m.Level).First();
        return (pick.Record!.StringId, pick.Record.Name, pick.Level);
    }

    /// <summary>The record types an item can be made from.</summary>
    public static readonly FcsRecordType[] ItemTypes =
        [FcsRecordType.WEAPON, FcsRecordType.ARMOUR, FcsRecordType.ITEM, FcsRecordType.CONTAINER, FcsRecordType.BLUEPRINT, FcsRecordType.MAP_ITEM, FcsRecordType.CROSSBOW, FcsRecordType.LIMB_REPLACEMENT];
}

/// <summary>
/// Removes the characters' bounties (every player character when <see cref="Characters"/> is empty): only <see cref="Faction"/>'s entry, or every entry when null.
/// A character left without bounties is no longer recently wanted.
/// </summary>
public sealed record DebugClearBounties(IReadOnlyList<CharacterId> Characters, int? Faction = null) : DebugCommand;

/// <summary>Kills the characters (<see cref="MedicalState.Kill"/>), as a lethal hit would: the medical state says dead, the body pass does the rest.</summary>
public sealed record DebugKill(IReadOnlyList<CharacterId> Characters) : DebugCommand;

/// <summary>Knocks the characters out for <see cref="Seconds"/> seconds of character time (<see cref="MedicalState.KnockOut"/>; agent-playtest control channel: ko), standing them down as a blow's knock-out does.</summary>
public sealed record DebugKnockOut(IReadOnlyList<CharacterId> Characters, float Seconds) : DebugCommand;

/// <summary>Wakes the unconscious characters at once (<see cref="MedicalState.WakeUp"/>; agent-playtest control channel: wake); the dead and the conscious are left alone.</summary>
public sealed record DebugWake(IReadOnlyList<CharacterId> Characters) : DebugCommand;

/// <summary>Says <see cref="Text"/> over the character as a speech bubble (<see cref="World.Bark"/>; agent-playtest control channel: say); display only, nothing in the world changes.</summary>
public sealed record DebugSay(CharacterId Speaker, string Text) : DebugCommand;

/// <summary>Finishes every research and repetition (<see cref="Construction.ResearchState.CompleteAll"/>).</summary>
public sealed record DebugUnlockResearch : DebugCommand;

/// <summary>Spawns a character of record <see cref="Record"/> at <see cref="At"/>, alone in a squad of the first faction hostile to the player (<see cref="PopulationSystem"/> applies it).</summary>
public sealed record DebugSpawn(string Record, Vector2 At) : DebugCommand;

/// <summary>
/// A building placed in the level editor (docs/game/level-editor.md "Simulation side"): <see cref="Building"/>, made by the host as the boot pass makes a level building
/// (<see cref="TownBuildings.Builder"/>), joins the building list of the town with placement id <see cref="Town"/> and is registered as a loaded one (<see cref="TownEdits.Place"/>).
/// </summary>
public sealed record DebugPlaceBuilding(string Town, TownBuilding Building) : DebugCommand;

/// <summary>Lays a loose item of record <see cref="Record"/> (<see cref="Quantity"/> in a stack) on the ground at <see cref="At"/> turned <see cref="Yaw"/> radians (agent-playtest control channel: place-item).</summary>
public sealed record DebugPlaceItem(string Record, int Quantity, Vector3 At, float Yaw) : DebugCommand;

/// <summary>Puts items into the inventory of the container fixture <see cref="Container"/> (agent-playtest control channel: container-put); what does not fit is reported.</summary>
public sealed record DebugPutItem(string Container, string Record, int Quantity) : DebugCommand;

/// <summary>A building cleared in the level editor, placed or loaded: leaves its town's list and registries (<see cref="TownEdits.Clear"/>). <see cref="Key"/> is its <see cref="TownBuilding.Key"/>.</summary>
public sealed record DebugClearBuilding(string Key) : DebugCommand;

static class DebugCheats
{
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
                if (data.Items.Create(lay.Record, lay.Quantity) is { } laid) world.GroundItems.Place(laid, lay.At, lay.Yaw, null, -1);
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
            case DebugMaxSkills max:
                foreach (var id in max.Characters)
                    if (table.TryResolveNext(id, out int skilled) && table.Cold(skilled)?.Stats is { } stats)
                        for (var s = StatsEnumerated.None + 1; s < StatsEnumerated.End; s++) stats[s] = 100;
                break;
            case DebugDamage damage when data is not null:
                // Combat and the body pass read KO and death from the medical state; what they do only on the hit's events is done here.
                var events = new List<MedicalEvent>();
                foreach (var id in damage.Characters)
                {
                    if (Body(world, data, id) is not ({ } medical, var ctx) || DamagePart(medical, ctx, damage.Part) is not (var part and >= 0) || !table.TryResolveNext(id, out int slot)) continue;
                    events.Clear();
                    medical.ApplyHit(part, new HitDamage(damage.Cut, 0, 0), ctx, events);
                    foreach (var e in events)
                    {
                        if (e.Kind is MedicalEventKind.Died or MedicalEventKind.KnockedOut) { TaskStart.Stand(table, slot); table.Next[slot].Velocity = Vector3.Zero; }
                        else if (e.Kind == MedicalEventKind.PartSevered) table.LimbsChanged.Enqueue((slot, medical.Parts[e.Part].Template.LimbSlot));
                        else if (e.Kind == MedicalEventKind.Decapitated) table.LimbsChanged.Enqueue((slot, -1));
                    }
                }
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
                var deaths = new List<MedicalEvent>();
                foreach (var id in kill.Characters)
                {
                    if (Body(world, data, id) is not ({ } medical, _) || !table.TryResolveNext(id, out int slot)) continue;
                    deaths.Clear();
                    medical.Kill(deaths);
                    if (deaths.Count > 0) { MovementSystem.Stop(table, slot); table.Next[slot].Velocity = Vector3.Zero; }
                }
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
            case DebugGiveItem give when data is not null:
                if (!table.TryResolveNext(give.Character, out int owner) || table.Cold(owner)?.Inventory is not { } inventory) break;
                if (data.Items.Create(give.Record, give.Quantity) is not { } item) { world.Post($"No item record {give.Record}."); break; }
                if (item.Type == FcsRecordType.ARMOUR) (item.Quality, item.Level) = (Math.Clamp(give.Quality, 0, 100), Math.Clamp(give.Quality, 0, 100));
                else if (item.Type == FcsRecordType.WEAPON && DebugGiveItem.Grade(data.Db, give.Quality) is { } grade)
                    (item.CompanyId, item.MaterialId, item.Level) = (CraftBench.PlayerWeapons, grade.Model, grade.Level);
                else if (DebugGiveItem.QualityTypes.Contains(item.Type)) item.Level = Math.Clamp(give.Quality, 0, 100);
                var wearer = table.Cold(owner);
                bool worn = Items.InventoryCommands.TryEquip(inventory, item.WithQuantity(1), wearer);
                if (!worn && give.Equip && item.Type is FcsRecordType.ARMOUR or FcsRecordType.ITEM && Items.InventoryCommands.IsEquipmentSection(item.EquipmentSection)
                    && item.EquipmentSection is not ("back" or "hip" or "backpack_attach") && inventory.Items.Find(i => i.Section == item.EquipmentSection) is { } displaced)
                {
                    // The slot is taken: take the worn item off, wear the new one, give the old one a place in the grids (else the ground, said in the log).
                    inventory.Items.Remove(displaced);
                    worn = Items.InventoryCommands.TryEquip(inventory, item.WithQuantity(1), wearer);
                    if (!worn) inventory.Items.Add(displaced);
                    else if (!inventory.Add(displaced))
                    {
                        world.GroundItems.Drop(displaced, table.Next[owner].Position, 0);
                        world.Post($"{displaced.Name} came off and did not fit the inventory; dropped on the ground.");
                    }
                }
                if (worn)
                {
                    // One unit is worn when its slot is free (as the trade window's right click); the rest of a stack goes to the grids.
                    assembly?.RefreshEquipment(table.Cold(owner)!);
                    if (item.Quantity == 1) break;
                    item = item.WithQuantity(item.Quantity - 1);
                }
                if (inventory.Add(item)) break;
                world.GroundItems.Drop(item, table.Next[owner].Position, 0);
                world.Post($"{item.Quantity} x {item.Name} did not fit; dropped on the ground.");
                break;
        }
    }

    /// <summary>Clears the bounties of <see cref="DebugClearBounties"/>; returns how many entries went.</summary>
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

    /// <summary>The characters <paramref name="ids"/> names, else every player character.</summary>
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

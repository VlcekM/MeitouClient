using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>The game's F10 debug menu (Meitou only, docs/game/ui-input.md "Debug menu"): cheats sent as commands, so they apply between ticks like any input.</summary>
public abstract record DebugCommand : SimCommand;

/// <summary>Adds <see cref="Delta"/> cats to the player's money (negative takes it away, never below 0).</summary>
public sealed record DebugMoney(long Delta) : DebugCommand;

/// <summary>Puts the characters on the ground at <see cref="Target"/> (side by side, 10 units apart) and drops their orders.</summary>
public sealed record DebugTeleport(IReadOnlyList<CharacterId> Characters, Vector3 Target) : DebugCommand;

/// <summary>Heals the characters fully (<see cref="MedicalState.Heal"/>).</summary>
public sealed record DebugHeal(IReadOnlyList<CharacterId> Characters) : DebugCommand;

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

/// <summary>Finishes every research and repetition (<see cref="Construction.ResearchState.CompleteAll"/>).</summary>
public sealed record DebugUnlockResearch : DebugCommand;

/// <summary>Spawns a character of record <see cref="Record"/> at <see cref="At"/>, alone in a squad of the first faction hostile to the player (<see cref="PopulationSystem"/> applies it).</summary>
public sealed record DebugSpawn(string Record, Vector2 At) : DebugCommand;

static class DebugCheats
{
    public static void Apply(World world, PopulationData? data, CharacterAssembly? assembly, DebugCommand command)
    {
        var table = world.Characters;
        switch (command)
        {
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
                    if (Body(world, data, id) is ({ } medical, var ctx)) medical.Heal(ctx);
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
                if (Items.InventoryCommands.TryEquip(inventory, item.WithQuantity(1)))
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
            Toughness = cold.Stats?.Toughness ?? 0,
            Strength = cold.Stats?.Strength ?? 50,
            HasStats = cold.Stats is not null,
            Seed = world.Seed,
            CharacterKey = Rng.Key(table.IdOf(slot)),
        });
    }
}

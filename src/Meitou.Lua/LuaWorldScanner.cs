using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Lua;

/// <summary>
/// Turns a live <see cref="World"/> into the immutable snapshots Lua reads: characters with their body parts and relations,
/// doors, buildings, the player's inventory and the camera.
/// </summary>
internal sealed class LuaWorldScanner
{
    /// <summary>
    /// The most characters one scan lists.
    /// </summary>
    const int MaxScannedChars = 512;

    /// <summary>
    /// The most body parts one character snapshot lists.
    /// </summary>
    const int MaxPartsPerChar = 12;

    /// <summary>
    /// The first parts of a body count as fatal to lose, whatever their name.
    /// </summary>
    const int FatalLeadingParts = 3;

    /// <summary>
    /// The most inventory items one snapshot lists.
    /// </summary>
    const int MaxItemsPerInventory = 32;

    /// <summary>
    /// The faction relations ally and enemy status is read from; null in headless tests.
    /// </summary>
    readonly FactionRelations? _relations;

    /// <summary>
    /// Creates a scanner reading ally and enemy status from <paramref name="relations"/>.
    /// </summary>
    public LuaWorldScanner(FactionRelations? relations)
    {
        _relations = relations;
    }

    /// <summary>
    /// A snapshot of the world's living characters, doors and buildings, and the player's inventory.
    /// </summary>
    public LuaWorldSnapshot CaptureScan(World world)
    {
        int playerFaction = world.Player.Exists ? world.Player.Faction : -1;
        List<LuaCharSnap> chars = [];
        Dictionary<(int Slot, int Gen), IReadOnlyList<LuaItemSnap>> invs = [];

        CaptureCharacters(world, playerFaction, chars, invs);
        List<LuaDoorSnap> doors = CaptureDoors(world, playerFaction);
        List<LuaBuildingSnap> blds = CaptureBuildings(world, playerFaction);

        return new LuaWorldSnapshot(chars, doors, blds, invs);
    }

    /// <summary>
    /// The camera of the world's view; null when it has none.
    /// </summary>
    public static LuaCameraSnap? CaptureCamera(World world)
    {
        if (world.View is not { } v) return null;

        Vector3 fwd = v.Forward;
        float len = fwd.Length();
        if (len > 1e-5f) fwd /= len;

        double yaw = ((Math.Atan2(fwd.X, fwd.Z) * 180.0 / Math.PI) + 360.0) % 360.0;
        double pitch = Math.Asin(Math.Clamp((double)fwd.Y, -1.0, 1.0)) * 180.0 / Math.PI;
        double aspect = float.IsFinite(v.Aspect) && v.Aspect > 0.1f ? v.Aspect : (16.0 / 9.0);

        return new LuaCameraSnap(v.Eye.X, v.Eye.Y, v.Eye.Z, fwd.X, fwd.Y, fwd.Z, yaw, pitch, v.FovY, aspect);
    }

    /// <summary>
    /// Lists the awake living characters into <paramref name="chars"/>, and the player's inventory into <paramref name="invs"/>.
    /// </summary>
    void CaptureCharacters(
        World world,
        int playerFaction,
        List<LuaCharSnap> chars,
        Dictionary<(int Slot, int Gen), IReadOnlyList<LuaItemSnap>> invs)
    {
        CharacterTable table = world.Characters;
        ReadOnlySpan<CharacterHot> next = table.Next;
        int hw = table.HighWater;

        for (int slot = 0; slot < hw && chars.Count < MaxScannedChars; slot++)
        {
            ref readonly CharacterHot hot = ref next[slot];
            if (!hot.Alive || hot.Dormant) continue;

            CharacterCold? cold = table.Cold(slot);
            if (cold is null) continue;

            CharacterId id = table.IdOf(slot);
            chars.Add(CharacterSnap(id, in hot, cold, playerFaction));

            if (cold.IsPlayer && cold.Inventory is { } inv && inv.Items.Count > 0)
                invs[(id.Slot, id.Generation)] = ItemSnaps(inv);
        }
    }

    /// <summary>
    /// The snapshot of one character.
    /// </summary>
    LuaCharSnap CharacterSnap(CharacterId id, in CharacterHot hot, CharacterCold cold, int playerFaction)
    {
        bool uncon = cold.Medical?.Unconscious ?? false;
        bool dead = cold.Medical?.Dead ?? false;
        bool down = uncon || dead;
        double blood = BloodOf(cold.Medical);
        List<LuaBodyPartSnap>? parts = PartSnaps(cold.Medical);
        double tough = ToughnessOf(cold.Stats);
        (int rel, bool isAlly, bool isEnemy) = RelationOf(cold, playerFaction);

        return new LuaCharSnap(
            id.Slot, id.Generation,
            string.IsNullOrWhiteSpace(cold.Name) ? "Drifter" : cold.Name.Trim(),
            cold.Faction, cold.IsPlayer,
            hot.Position.X, hot.Position.Y, hot.Position.Z,
            hot.Health, down, dead, cold.InCombat, cold.SquadId,
            parts, tough, blood, 100, uncon, rel, isEnemy, isAlly,
            TemplateId: cold.RecordId ?? "",
            IsAnimal: cold.Animal is not null,
            Age: float.IsFinite(cold.Age) ? cold.Age : 1.0,
            RaceId: cold.Race?.Name ?? "");
    }

    /// <summary>
    /// The character's blood clamped to 0..200; 100 when it has no medical state or a non-finite value.
    /// </summary>
    static double BloodOf(MedicalState? medical)
    {
        if (medical is not { } m) return 100;

        return float.IsFinite(m.Blood) ? Math.Clamp(m.Blood, 0, 200) : 100;
    }

    /// <summary>
    /// The first <see cref="MaxPartsPerChar"/> body parts; null when the character has no medical state or no parts.
    /// </summary>
    static List<LuaBodyPartSnap>? PartSnaps(MedicalState? medical)
    {
        if (medical is not { } m || m.Parts.Count == 0) return null;

        int pn = Math.Min(m.Parts.Count, MaxPartsPerChar);
        List<LuaBodyPartSnap> parts = new(pn);
        for (int pi = 0; pi < pn; pi++)
        {
            HealthPart pt = m.Parts[pi];
            string pName = pt.Template?.StringId ?? $"part{pi}";
            bool fatal = pi < FatalLeadingParts || IsFatalPartName(pName);
            parts.Add(new LuaBodyPartSnap(pName, pt.Flesh, pt.Stun, Math.Max(1, pt.MaxHp), fatal, pt.Wear, pt.Rig));
        }

        return parts;
    }

    /// <summary>
    /// Whether a part of this name (head, chest or stomach) is fatal to lose.
    /// </summary>
    static bool IsFatalPartName(string name) =>
        name.Contains("head", StringComparison.OrdinalIgnoreCase)
        || name.Contains("chest", StringComparison.OrdinalIgnoreCase)
        || name.Contains("stomach", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The character's toughness clamped to 0..1000; 1 when it has no stats or a non-finite value.
    /// </summary>
    static double ToughnessOf(CharacterStats? stats)
    {
        return stats is { } st && float.IsFinite(st[StatsEnumerated.Toughness])
            ? Math.Clamp(st[StatsEnumerated.Toughness], 0, 1000)
            : 1;
    }

    /// <summary>
    /// The character's relation to the player and whether it counts as an ally or an enemy: from the faction relations when they and both
    /// factions are known, else the player's side (ally), or in combat (enemy).
    /// </summary>
    (int Relation, bool IsAlly, bool IsEnemy) RelationOf(CharacterCold cold, int playerFaction)
    {
        bool isAlly = cold.IsPlayer || (playerFaction >= 0 && cold.Faction == playerFaction);
        bool isEnemy = !isAlly && cold.InCombat;

        if (_relations is null || playerFaction < 0 || cold.Faction < 0)
        {
            return (isAlly ? 100 : (isEnemy ? -50 : 0), isAlly, isEnemy);
        }

        int rel = 0;
        float r = _relations.Get(playerFaction, cold.Faction);
        if (float.IsFinite(r))
        {
            rel = (int)MathF.Round(r);
            if (!isAlly && r >= FactionRelations.AllyThreshold) isAlly = true;
            if (!isAlly && r <= FactionRelations.HostileThreshold) isEnemy = true;
        }

        return (rel, isAlly, isEnemy);
    }

    /// <summary>
    /// The first <see cref="MaxItemsPerInventory"/> items of the inventory.
    /// </summary>
    static List<LuaItemSnap> ItemSnaps(Inventory inv)
    {
        int n = Math.Min(inv.Items.Count, MaxItemsPerInventory);
        List<LuaItemSnap> list = new(n);
        for (int i = 0; i < n; i++)
        {
            ItemInstance it = inv.Items[i];
            list.Add(new LuaItemSnap(it.Record ?? "", it.Name ?? "", it.Type.ToString(), it.Section ?? "", it.Quantity, it.Level));
        }

        return list;
    }

    /// <summary>
    /// Every door of the world.
    /// </summary>
    static List<LuaDoorSnap> CaptureDoors(World world, int playerFaction)
    {
        List<LuaDoorSnap> doors = [];
        foreach (Fixture f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Door) continue;

            bool locked = f.Lock?.EffectivelyLocked ?? false;
            doors.Add(new LuaDoorSnap(
                f.Id, f.Building, f.Owner, playerFaction >= 0 && f.Owner == playerFaction,
                f.Position.X, f.Position.Y, f.Position.Z,
                f.OpenAmount, f.Motion.ToString().ToLowerInvariant(),
                locked, f.DoorBroken, f.GateCode != 0));
        }

        return doors;
    }

    /// <summary>
    /// Every building of the world.
    /// </summary>
    static List<LuaBuildingSnap> CaptureBuildings(World world, int playerFaction)
    {
        List<LuaBuildingSnap> blds = [];
        foreach (var b in world.Buildings.All)
        {
            blds.Add(new LuaBuildingSnap(
                b.Id, b.Name ?? b.Record ?? "", b.Record ?? "",
                b.Owner, playerFaction >= 0 && b.Owner == playerFaction,
                b.Position.X, b.Position.Y, b.Position.Z,
                b.Complete, b.Progress));
        }

        return blds;
    }
}

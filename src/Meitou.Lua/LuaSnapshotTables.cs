using MoonSharp.Interpreter;

namespace Meitou.Lua;

/// <summary>
/// Builds the plain Lua tables the KenshiLua prelude's metatables wrap from the immutable world snapshots; the key set (and its insertion order)
/// is the contract with the prelude and with <c>json.encode</c>.
/// </summary>
internal static class LuaSnapshotTables
{
    /// <summary>
    /// A character's table: identity, position, state flags, relations, and its <c>parts</c> list.
    /// </summary>
    internal static Table Character(Script s, LuaCharSnap c)
    {
        Table t = new(s);
        t["slot"] = c.Slot;
        t["gen"] = c.Gen;
        t["name"] = c.Name;

        t["faction"] = c.Faction;
        t["is_player"] = c.IsPlayer;

        t["x"] = c.X;
        t["y"] = c.Y;
        t["z"] = c.Z;

        t["hp"] = c.Hp;
        t["down"] = c.Down;
        t["dead"] = c.Dead;

        t["unconcious"] = c.Unconscious || c.Down;

        t["in_combat"] = c.InCombat;
        t["squad_id"] = c.SquadId;

        t["toughness"] = c.Toughness;
        t["blood"] = c.Blood;
        t["max_blood"] = c.MaxBlood;

        t["relation"] = c.Relation;
        t["is_enemy"] = c.IsEnemy;
        t["is_ally"] = c.IsAlly;

        t["is_trader"] = c.IsTrader;
        t["bounty"] = c.Bounty;
        t["template_id"] = c.TemplateId;

        t["is_animal"] = c.IsAnimal;
        t["age"] = c.Age;
        t["race_id"] = c.RaceId;

        t["parts"] = BodyParts(s, c);
        return t;
    }

    /// <summary>
    /// The 1-based body part list of a character; one fatal chest at its hit points when the snapshot lists none.
    /// </summary>
    private static Table BodyParts(Script s, LuaCharSnap c)
    {
        Table parts = new(s);

        if (c.Parts is not { Count: > 0 } pl)
        {
            parts[1] = DefaultChest(s, c);
            return parts;
        }

        for (int i = 0; i < pl.Count; i++)
        {
            parts[i + 1] = BodyPart(s, pl[i]);
        }

        return parts;
    }

    /// <summary>
    /// One scanned body part.
    /// </summary>
    private static Table BodyPart(Script s, LuaBodyPartSnap p)
    {
        Table pt = new(s);
        pt["name"] = p.Name;
        pt["flesh"] = p.Flesh;
        pt["fleshStun"] = p.Stun;

        pt["max_hp"] = p.MaxHp;
        pt["fatal"] = p.Fatal;

        pt["wearDamage"] = p.Wear;
        pt["juryRigging"] = p.JuryRig;
        return pt;
    }

    /// <summary>
    /// The stand-in single chest part for a character whose snapshot lists no parts.
    /// </summary>
    private static Table DefaultChest(Script s, LuaCharSnap c)
    {
        Table pt = new(s);
        pt["name"] = "chest";
        pt["flesh"] = c.Hp;
        pt["fleshStun"] = 0;

        pt["max_hp"] = 100;
        pt["fatal"] = true;
        pt["wearDamage"] = 0;
        pt["juryRigging"] = 0;
        return pt;
    }

    /// <summary>
    /// A door or gate's table.
    /// </summary>
    internal static Table Door(Script s, LuaDoorSnap d)
    {
        Table t = new(s);
        t["id"] = d.Id;
        t["building"] = d.Building;
        t["name"] = d.Building;

        t["owner"] = d.Owner;
        t["is_player"] = d.IsPlayer;

        t["x"] = d.X;
        t["y"] = d.Y;
        t["z"] = d.Z;

        t["open_amount"] = d.OpenAmount;
        t["motion"] = d.Motion;

        t["locked"] = d.Locked;
        t["broken"] = d.Broken;
        t["is_gate"] = d.IsGate;
        return t;
    }

    /// <summary>
    /// An inventory item's table.
    /// </summary>
    internal static Table Item(Script s, LuaItemSnap it)
    {
        Table t = new(s);
        t["record"] = it.Record;
        t["name"] = it.Name;
        t["type"] = it.Type;

        t["section"] = it.Section;
        t["qty"] = it.Qty;
        t["level"] = it.Level;
        return t;
    }

    /// <summary>
    /// The camera's table: eye position, forward vector, yaw, pitch and field of view.
    /// </summary>
    internal static Table Camera(Script s, LuaCameraSnap c)
    {
        Table t = new(s);
        t["x"] = c.X;
        t["y"] = c.Y;
        t["z"] = c.Z;

        t["fx"] = c.FwdX;
        t["fy"] = c.FwdY;
        t["fz"] = c.FwdZ;

        t["yaw"] = c.Yaw;
        t["pitch"] = c.Pitch;
        t["fov"] = c.FovY;
        return t;
    }

    /// <summary>
    /// A game-data lookup result's table.
    /// </summary>
    internal static Table GameData(Script s, LuaGameDataRecord rec)
    {
        Table t = new(s);
        t["id"] = rec.Id;
        t["stringID"] = rec.Id;
        t["name"] = rec.Name;
        t["type"] = rec.Type;
        return t;
    }
}

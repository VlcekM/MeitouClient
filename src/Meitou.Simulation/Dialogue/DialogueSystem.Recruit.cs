using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The dialogue system's recruiting: joining the player's squad, leaving one, and slaves given.
/// </summary>
public sealed partial class DialogueSystem
{
    /// <summary>
    /// DA_JOIN_SQUAD_WITH_EDIT (3) / DA_JOIN_SQUAD_FAST (18) with the player character as the target (docs/game/dialogue.md "Recruiting"): the
    /// speaker leaves its squad and joins the player's active squad (<see cref="ActiveSquad"/>) as a player character; when that squad already holds
    /// CONSTANTS <c>max squad size</c> members, a new squad of the "blank squad" template takes it. At the faction's head cap (<c>max faction
    /// size</c>) the conversation ends with "It looks like your squad is a little full." and the line's remaining effects are skipped (false). DA 3
    /// then opens the character editor on the recruit (engine: the conversation ends for it).
    /// </summary>
    bool Recruit(World world, Conversation c, CharacterId me, bool withEditor)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(me, out int slot) || table.Cold(slot) is not { IsPlayer: false }
            || Cold(world, c.Player) is not { IsPlayer: true } tc)
        {
            return true;
        }
        if (SquadManagement.CappedHeads(world) >= _data.Bodies.MaxFactionSize)
        {
            world.Post("It looks like your squad is a little full.");
            c.Ended = true;
            return false;
        }
        if (!JoinPlayer(world, slot, tc)) return true;
        if (withEditor)
        {
            c.Ended = true;
            _request = new DialogueRequest(++_requestId, DialogueRequestKind.RecruitEditor, me, me);
        }
        return true;
    }

    /// <summary>
    /// The player's active squad (<c>FUN_1407f5520</c>): the cached one (<see cref="PlayerState.Squad"/>), else the squad of the first selected
    /// character, else the first squad of the player's faction (the squad of the dead is skipped: the original takes the second squad when the first
    /// is it, <c>FUN_1407f4960</c>); null with none. The talker's squad is never a fallback.
    /// </summary>
    internal static Squad? ActiveSquad(World world)
    {
        if (world.Squads.Find(world.Player.Squad) is { } cached) return cached;
        if (world.Player.Selection.Select(id => Cold(world, id)).FirstOrDefault(s => s is { IsPlayer: true }) is { } selected
            && world.Squads.Find(selected.SquadId) is { } selectedSquad)
        {
            return selectedSquad;
        }
        return SquadManagement.LedSquads(world).FirstOrDefault();
    }

    /// <summary>
    /// <c>FUN_1407ff8b0</c>: with the faction under its head cap, a recruit that overfills the active squad and the squad count at its cap make one
    /// extra, empty squad of the active squad's template at its position before the recruit is added (the recruit does not join it, so it stays
    /// empty, docs/game/dialogue.md "Recruiting").
    /// </summary>
    static void ExtraSquadAtTheCap(World world, PopulationData data, Squad active)
    {
        var limits = data.Bodies;
        if (SquadManagement.CappedHeads(world) >= limits.MaxFactionSize || active.Members.Count + 1 <= limits.MaxSquadSize
            || SquadManagement.LedSquads(world).Count < limits.MaxSquads)
        {
            return;
        }
        SquadManagement.NewSquad(world, data.Db, active.Position,
            data.Factions[world.Player.Faction].Name is { Length: > 0 } f ? f : "Nameless", active.TemplateId);
    }

    /// <summary>
    /// Why the debug menu's Recruit row cannot recruit the character (null when it can): it is already a player character, has no dialogue line
    /// that makes it join (DA 3 / 18), or the active squad is full.
    /// </summary>
    public static string? RecruitRefusal(World world, PopulationData data, CharacterId id)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold) return "that character cannot be recruited";
        if (cold.IsPlayer) return $"{cold.Name} is already in your squad";
        if (!table.IsAlive(id)) return $"{cold.Name} is dead";
        if (!Recruitable(world, data, slot)) return $"{cold.Name} has no dialogue that makes them join";
        if (ActiveSquad(world) is not { } squad) return "you have no squad";
        if (squad.Members.Count >= data.Bodies.MaxSquadSize) return "your squad is full";
        return null;
    }

    /// <summary>
    /// Whether the character's dialogue set (its own dialogue, its packages, and the leader dialogue of the squad it leads) has a line that makes it
    /// join the player's squad (DA 3 / 18).
    /// </summary>
    static bool Recruitable(World world, PopulationData data, int slot)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } cold) return false;
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        bool leader = squad is not null && squad.Leader == table.IdOf(slot);
        var set = data.Dialogue.SetFor(cold.RecordId, squad?.TemplateId, leader, player: false, animal: SquadRegistry.IsAnimal(cold));
        return set.HasEffect(DialogueActions.JoinSquadWithEdit) || set.HasEffect(DialogueActions.JoinSquadFast);
    }

    /// <summary>
    /// The character at <paramref name="slot"/> leaves its squad and joins the player's active squad (or a new blank squad when it is full) as a
    /// player character.
    /// </summary>
    internal bool JoinPlayer(World world, int slot, CharacterCold talker) => JoinPlayer(world, _data, slot);

    /// <summary>
    /// <see cref="JoinPlayer(World, int, CharacterCold)"/> with the data given, for callers outside the system (the debug menu's Recruit row).
    /// </summary>
    internal static bool JoinPlayer(World world, PopulationData data, int slot)
    {
        var table = world.Characters;
        var limits = data.Bodies;
        if (ActiveSquad(world) is not { } squad) return false;
        ExtraSquadAtTheCap(world, data, squad);
        if (squad.Members.Count >= limits.MaxSquadSize)
        {
            squad = SquadManagement.NewSquad(world, data.Db, table.Next[slot].Position,
                data.Factions[world.Player.Faction].Name is { Length: > 0 } f ? f : "Nameless");
        }

        Join(world, data, slot, squad);
        return true;
    }

    /// <summary>
    /// The character at <paramref name="slot"/> leaves its squad and joins the player's squad <paramref name="squad"/> as a player character, as a
    /// recruit does: the player's faction, orders and class, no AI, jobs or uniform marks, written as a new member at the next save; it leads the
    /// squad when the squad has no leader. The caller has checked the squad and the caps.
    /// </summary>
    internal static void Join(World world, PopulationData data, int slot, Squad squad)
    {
        var table = world.Characters;
        var me = table.IdOf(slot);
        var mc = table.Cold(slot)!;
        world.Events.Line(world, "recruit", $"{EventLog.Who(world, slot)} joins the player's squad");

        // The faction change empties its own goal lists (Character slot 20 FUN_1405cbac0 -> FUN_14050a9d0), so the engine duties those goals stand
        // for let it go; its order queue stays (nothing there empties it), the front planned again from its start.
        world.ReleaseDuties(slot);
        Player.PlayerOrders.Interrupt(mc);
        Leave(world, me, mc);
        mc.Faction = world.Player.Faction;
        mc.RoadPreference = data.RoadPreferenceOf(mc.Faction);
        mc.SquadId = squad.Id;
        mc.Role = (int)SquadRole.Squad1;
        mc.IsPlayer = true;
        mc.NpcClass = FundamentalType.Adventurer;   // every player-faction character's class
        mc.Orders = Orders.PlayerDefaults;
        mc.Ai = null;
        mc.Fleeing = false;
        Crime.CrimeSystem.DropPending(world, slot, mc);
        mc.Jobs.Clear();   // an NPC's list, if any, does not carry over
        // a recruit's clothes are no longer a faction's uniform (dialogue.md "Recruiting")
        if (mc.Inventory is { } kit)
        {
            foreach (var piece in kit.All()) piece.Uniform = "";
        }
        mc.Save = null;   // written as a new member of the player's platoon; the save's old slot of it is removed
        table.Next[slot].Mode = SpeedMode.Free;   // the original's speed mode 2
        TaskStart.Stand(table, slot);
        squad.Members.Add(me);
        if (squad.Leader.IsNone) squad.Leader = me;
    }

    /// <summary>
    /// Takes <paramref name="me"/> out of its squad, passing the lead to a successor when it led it, and out of the save's platoon it was made from.
    /// </summary>
    internal static void Leave(World world, CharacterId me, CharacterCold mc)
    {
        world.Saved?.Leave(mc);
        if (world.Squads.Find(mc.SquadId) is not { } old) return;
        old.Members.Remove(me);
        if (old.Leader == me) old.Leader = SquadRegistry.Successor(old.Members, world.Characters);
    }

    /// <summary>
    /// <c>DA_GIVE_TARGET_MY_SLAVES</c> (24): the speaker squad's slaves get the target as their owner (<see cref="Kidnap.CaptiveSystem.GiveSlaves"/>).
    /// </summary>
    static void GiveSlaves(World world, CharacterCold mc, CharacterCold tc)
    {
        if (world.Squads.Find(mc.SquadId) is { } squad) Kidnap.CaptiveSystem.GiveSlaves(world, squad, tc);
    }
}
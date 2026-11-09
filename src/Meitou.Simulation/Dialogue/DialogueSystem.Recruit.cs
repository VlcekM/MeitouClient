using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Dialogue;

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
        if (PlayerHeads(world) >= _data.Bodies.Constants.MaxFactionSize)
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

    internal static int PlayerHeads(World world)
    {
        var table = world.Characters;
        int heads = 0;
        for (int i = 0; i < table.Next.Length; i++)
            if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, DiedTick: < 0 }) heads++;
        return heads;
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
        return SquadManagement.PlayerSquads(world).FirstOrDefault();
    }

    /// <summary>
    /// <c>FUN_1407ff8b0</c>: with the faction under its head cap, a recruit that overfills the active squad and the squad count at its cap make one
    /// extra, empty squad of the active squad's template at its position before the recruit is added (the recruit does not join it, so it stays
    /// empty, docs/game/dialogue.md "Recruiting").
    /// </summary>
    void ExtraSquadAtTheCap(World world, Squad active, GameConstants limits)
    {
        if (PlayerHeads(world) >= limits.MaxFactionSize || active.Members.Count + 1 <= limits.MaxSquadSize
            || SquadManagement.PlayerSquads(world).Count < limits.MaxSquads)
        {
            return;
        }
        SquadManagement.NewSquad(world, _data.Db, active.Position,
            _data.Factions[world.Player.Faction].Name is { Length: > 0 } f ? f : "Nameless", active.TemplateId);
    }

    /// <summary>
    /// The character at <paramref name="slot"/> leaves its squad and joins the player's active squad (or a new blank squad when it is full) as a
    /// player character.
    /// </summary>
    internal bool JoinPlayer(World world, int slot, CharacterCold talker)
    {
        var table = world.Characters;
        var me = table.IdOf(slot);
        var mc = table.Cold(slot)!;
        var limits = _data.Bodies.Constants;
        if (ActiveSquad(world) is not { } squad) return false;
        ExtraSquadAtTheCap(world, squad, limits);
        if (squad.Members.Count >= limits.MaxSquadSize)
        {
            squad = SquadManagement.NewSquad(world, _data.Db, table.Next[slot].Position,
                _data.Factions[world.Player.Faction].Name is { Length: > 0 } f ? f : "Nameless");
        }

        Leave(world, me, mc);
        mc.Faction = world.Player.Faction;
        mc.RoadPreference = _data.RoadPreferenceOf(mc.Faction);
        mc.SquadId = squad.Id;
        mc.Role = (int)SquadRole.Squad1;
        mc.IsPlayer = true;
        mc.NpcClass = FundamentalType.Adventurer;   // every player-faction character's class
        mc.Orders = Orders.PlayerDefaults;
        mc.Ai = null;
        mc.Engaged = false;
        mc.Fleeing = false;
        mc.Pending = null;
        mc.Jobs.Clear();   // an NPC's list, if any, does not carry over
        // a recruit's clothes are no longer a faction's uniform (economy.md "Unknowns", Observed)
        if (mc.Inventory is { } kit)
        {
            foreach (var piece in kit.All()) piece.Uniform = "";
        }
        mc.Save = null;   // written as a new member of the player's platoon; the save's old slot of it is removed
        table.Next[slot].Mode = SpeedMode.Free;   // the original's speed mode 2
        TaskStart.Stand(table, slot);
        squad.Members.Add(me);
        if (squad.Leader.IsNone) squad.Leader = me;
        return true;
    }

    internal static void Leave(World world, CharacterId me, CharacterCold mc)
    {
        if (world.Squads.Find(mc.SquadId) is not { } old) return;
        old.Members.Remove(me);
        if (old.Leader == me) old.Leader = SquadRegistry.Successor(old.Members, world.Characters);
    }

    /// <summary>
    /// <c>DA_GIVE_TARGET_MY_SLAVES</c> (24, <c>FUN_140680560</c>): the slaves on the speaker's platoon's list are let out of their cages and join the
    /// target's squad. Engine reading of the list: the members of the speaker's squad in slave state (Meitou makes no slave squads yet, so it is
    /// often empty). They join the player as recruits do, keeping their slave state, now owned by the target's faction; a non-player target's squad
    /// takes them into its own.
    /// </summary>
    void GiveSlaves(World world, CharacterId me, CharacterCold mc, CharacterCold tc)
    {
        var table = world.Characters;
        if (mc.SquadId < 0 || world.Squads.Find(mc.SquadId) is not { } squad) return;
        foreach (var id in squad.Members.ToArray())
        {
            if (id == me || !table.TryResolveNext(id, out int s) || table.Cold(s) is not { SlaveState: 1 or 2, Medical.Dead: false } slave) continue;
            byte state = slave.SlaveState;
            if (slave.IsImprisoned) world.Fixtures.Release(world, id);
            if (tc.IsPlayer || tc.Faction == world.Player.Faction)
            {
                if (PlayerHeads(world) >= _data.Bodies.Constants.MaxFactionSize || !JoinPlayer(world, s, tc)) break;
            }
            else if (world.Squads.Find(tc.SquadId) is { } theirs)
            {
                Leave(world, id, slave);
                slave.SquadId = theirs.Id;
                theirs.Members.Add(id);
            }
            (slave.SlaveState, slave.SlaveOwner) = (state, tc.Faction);
        }
    }
}
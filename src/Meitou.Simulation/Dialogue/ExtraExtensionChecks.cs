using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// BFrizz's Extra Extensions' dialogue conditions 1000-1006 (docs/game/dialogue.md "Extra Extensions conditions"), tested ahead of the line check
/// as the plugin's hooks on <c>checkConditions</c> and <c>checkTags</c> test them. The line check's own pass over the conditions then passes them,
/// as the game passes every code it does not know.
/// </summary>
sealed class ExtraExtensionChecks(DialogueSystem dialogue)
{
    /// <summary>
    /// The plugin's squad radius: a T_WHOLE_SQUAD condition and a <c>from squad</c> action count the squad's members nearer than this.
    /// </summary>
    internal const float SquadRadius = 900;

    /// <summary>
    /// The plugin's <c>checkConditions</c> hook, run before the line check: DCs 1000, 1002, 1005 and 1006 in list order. The first condition whose
    /// checked side is missing (a condition on the target with none) ends the pass, the rest are left untested.
    /// </summary>
    public bool ConditionsPass(World world, DialogueNode node, CharacterId me, CharacterId target)
    {
        foreach (var dc in node.Conditions)
        {
            var (check, other) = Sides(dc, me, target);
            if (DialogueSystem.Cold(world, check) is null) break;
            if (!ExtraConditions.Contains(dc.Condition) || ExtraConditions.ReadsTag(dc.Condition)) continue;

            if (!SidesPass(world, dc, me, check, other, LineHolds)) return false;
        }
        return true;
    }

    /// <summary>
    /// The plugin's <c>checkTags</c> hook, run before the variables: DCs 1001, 1003 and 1004 in list order. A condition on a missing target is
    /// skipped (engine choice: the original's hook would read through a null character), as is a T_WHOLE_SQUAD one of a speaker without a squad.
    /// </summary>
    public bool TagConditionsPass(World world, DialogueNode node, CharacterId me, CharacterId target)
    {
        foreach (var dc in node.Conditions)
        {
            if (!ExtraConditions.ReadsTag(dc.Condition)) continue;

            var (check, other) = Sides(dc, me, target);
            if (DialogueSystem.Cold(world, check) is not { } cc) continue;
            if (dc.Who == (int)Talker.WholeSquad && cc.SquadId < 0) continue;

            if (!SidesPass(world, dc, me, check, other, TagHolds)) return false;
        }
        return true;
    }

    /// <summary>
    /// The checked character and the other side: <c>who</c> 0 (T_ME) and 6 (T_WHOLE_SQUAD) check the speaker, any other value the target.
    /// </summary>
    static (CharacterId Check, CharacterId Other) Sides(DialogueCondition dc, CharacterId me, CharacterId target) =>
        dc.Who is (int)Talker.Me or (int)Talker.WholeSquad ? (me, target) : (target, me);

    /// <summary>
    /// One condition on its checked character, or for T_WHOLE_SQUAD on the members of the speaker's squad nearer than <see cref="SquadRadius"/>
    /// to <paramref name="me"/>, down ones included: any member passing passes; none near fails. A speaker without a squad is tested alone (engine
    /// choice: every character of the original has one).
    /// </summary>
    static bool SidesPass(World world, DialogueCondition dc, CharacterId me, CharacterId check, CharacterId other,
        Func<World, DialogueCondition, CharacterId, CharacterId, bool> holds)
    {
        if (dc.Who != (int)Talker.WholeSquad) return holds(world, dc, check, other);

        if (SquadNear(world, me, me) is not { } near) return holds(world, dc, me, other);

        bool found = false;
        foreach (var member in near)
        {
            if (holds(world, dc, member, other)) found = true;
        }
        return found;
    }

    /// <summary>
    /// The members of <paramref name="owner"/>'s squad, in member order, nearer than <see cref="SquadRadius"/> (3D) to <paramref name="centre"/>'s
    /// position (<c>ActivePlatoon::getCharactersInArea</c> with its flag clear: down members count). Null without a squad; empty when the centre
    /// no longer resolves.
    /// </summary>
    internal static List<CharacterId>? SquadNear(World world, CharacterId owner, CharacterId centre)
    {
        if (DialogueSystem.Cold(world, owner) is not { SquadId: >= 0 } oc || world.Squads.Find(oc.SquadId) is not { } squad) return null;

        var near = new List<CharacterId>();
        if (DialogueSystem.Position(world, centre) is not { } at) return near;

        foreach (var member in squad.Members)
        {
            if (DialogueSystem.Position(world, member) is { } p && Vector3.DistanceSquared(p, at) < SquadRadius * SquadRadius) near.Add(member);
        }
        return near;
    }

    /// <summary>
    /// The plugin's comparison: compare by 0 ==, 1 &lt;, 2 &gt;, the computed value against val0; any other compare by fails.
    /// </summary>
    static bool Compare(DialogueCondition dc, int value) => dc.CompareBy switch
    {
        0 => value == dc.Value,
        1 => value < dc.Value,
        2 => value > dc.Value,
        _ => false,
    };

    /// <summary>
    /// DCs 1000, 1002, 1005 and 1006 on <paramref name="check"/> (a yes/no answer compares as 0 or 1).
    /// </summary>
    bool LineHolds(World world, DialogueCondition dc, CharacterId check, CharacterId other)
    {
        if (DialogueSystem.Cold(world, check) is not { } c) return true;
        return dc.Condition switch
        {
            ExtraConditions.IsSleeping => Compare(dc, c.InSomething == HeldState.Bed ? 1 : 0),
            ExtraConditions.IsAllyBecauseOfDisguise => Compare(dc, AllyByDisguise(world, check, c, other) ? 1 : 0),
            ExtraConditions.WeaponLevel => Compare(dc, WeaponLevel(c)),
            ExtraConditions.ArmourLevel => ArmourPasses(dc, c),
            _ => true,
        };
    }

    /// <summary>
    /// DCs 1001, 1003 and 1004 on <paramref name="check"/>: the squad's short-term tag <c>tag</c> on the other side, the stat <c>tag</c> raw or
    /// modified, truncated to an int.
    /// </summary>
    bool TagHolds(World world, DialogueCondition dc, CharacterId check, CharacterId other)
    {
        if (DialogueSystem.Cold(world, check) is not { } c) return true;
        return dc.Condition switch
        {
            ExtraConditions.HasShortTermTag => Compare(dc, HasShortTag(world, c, other, dc.Tag) ? 1 : 0),
            ExtraConditions.StatLevelUnmodified => Compare(dc, (int)StatGetter.Get(c, (StatsEnumerated)dc.Tag, true)),
            ExtraConditions.StatLevelModified => Compare(dc, (int)StatGetter.Get(c, (StatsEnumerated)dc.Tag, false)),
            _ => true,
        };
    }

    /// <summary>
    /// <c>getCharacterMemoryTag</c> (<c>FUN_140678960</c>): the checked character's squad holds short-term <paramref name="tag"/> on
    /// <paramref name="other"/>; false without a squad, without the other side or for a tag outside the 32 bits.
    /// </summary>
    bool HasShortTag(World world, CharacterCold c, CharacterId other, int tag) =>
        c.SquadId >= 0 && !other.IsNone && tag is >= 0 and < 32 && dialogue.Memory.HasShort(world, c.SquadId, other, tag);

    /// <summary>
    /// DC 1002: <c>isAlly(other, true) &amp;&amp; !isAlly(other, false)</c>, the checked character regards the other as friendly with the other's
    /// working disguise read and not without it (the dialogue system's <c>RegardsFriendly</c>); false without the other side.
    /// </summary>
    bool AllyByDisguise(World world, CharacterId check, CharacterCold c, CharacterId other)
    {
        if (DialogueSystem.Cold(world, other) is not { } oc) return false;
        return dialogue.RegardsFriendly(world, check, c, other, oc, true) && !dialogue.RegardsFriendly(world, check, c, other, oc, false);
    }

    /// <summary>
    /// DC 1005's level: the weapon in hand's (the crossbow in ranged mode), else the highest of every weapon and crossbow in the <c>back</c> and
    /// <c>hip</c> sections, else -1. The original reads its drawn weapon, else its preferred one (<c>getThePreferredWeapon</c>); Meitou has no drawn
    /// weapon and reads the one <see cref="Combat.Fighter.Weapon"/> holds (engine reading).
    /// </summary>
    static int WeaponLevel(CharacterCold c)
    {
        if (c.Inventory is not { } inventory) return -1;
        if (InHand(c, inventory) is { } held) return held.GearLevel;

        int level = -1;
        foreach (var item in inventory.Items)
        {
            if (item.Section is "back" or "hip" && item.Type is FcsRecordType.Weapon or FcsRecordType.Crossbow) level = Math.Max(level, item.GearLevel);
        }
        return level;
    }

    /// <summary>
    /// The item of the weapon in hand: in ranged mode the first crossbow of <c>back</c> or <c>hip</c>, else the first weapon of the section the
    /// fighter's <see cref="Combat.Fighter.Weapon"/> comes from; null with none.
    /// </summary>
    static ItemInstance? InHand(CharacterCold c, Inventory inventory)
    {
        if (c.RangedCombatMode && inventory.Items.Find(i => i.Type == FcsRecordType.Crossbow && i.Section is "back" or "hip") is { } crossbow)
            return crossbow;
        if (c.Fighter is not { Weapon: not null } f) return null;

        string section = f.WeaponIsBack ? "back" : "hip";
        return inventory.Items.Find(i => i.Type == FcsRecordType.Weapon && i.Section == section);
    }

    /// <summary>
    /// DC 1006: any worn piece of the <c>armour</c>, <c>legs</c>, <c>head</c>, <c>boots</c> and <c>shirt</c> sections (<c>getEquippedArmour</c>)
    /// whose level passes; with none worn, -1 compared.
    /// </summary>
    static bool ArmourPasses(DialogueCondition dc, CharacterCold c)
    {
        bool worn = false;
        foreach (var item in c.Inventory?.Items ?? [])
        {
            if (item.Section is not ("armour" or "legs" or "head" or "boots" or "shirt")) continue;

            worn = true;
            if (Compare(dc, item.GearLevel)) return true;
        }
        return !worn && Compare(dc, -1);
    }
}

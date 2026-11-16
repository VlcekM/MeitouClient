using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Factions;

/// <summary>
/// What moved a faction relation (<see cref="RelationCause"/>).
/// </summary>
public enum RelationCauseType
{
    /// <summary>
    /// A deed against one of the faction's people (<see cref="FactionDeeds"/>): a blow, a death, first aid or a caging.
    /// </summary>
    Deed,
    /// <summary>
    /// A crime a friendly witness saw (the crime system's betrayal).
    /// </summary>
    Crime,
    /// <summary>
    /// A captive sold to a slaver (the slaver's faction rises, the captive's falls) or a slave bought free (both rise).
    /// </summary>
    Trade,
    /// <summary>
    /// A dialogue's <c>change relations</c> or DA_AFFECT_RELATIONS.
    /// </summary>
    Dialogue,
    /// <summary>
    /// A declaration of war (the dialogue's DECLARE_WAR).
    /// </summary>
    WarDeclared,
    /// <summary>
    /// The end of a war (the dialogue's END_WAR).
    /// </summary>
    WarEnded,
    /// <summary>
    /// A debug command.
    /// </summary>
    Debug
}

/// <summary>
/// Why a relation changed.
/// </summary>
/// <param name="Type">What moved it.</param>
/// <param name="Deed">The deed's kind for <see cref="RelationCauseType.Deed"/>; 0 otherwise.</param>
/// <param name="Actor">The character whose act it was: the deed's doer, the crime's culprit, the trading player character, the dialogue's or the
/// war's speaker; none for a debug command.</param>
/// <param name="Target">The deed's victim; else none.</param>
public readonly record struct RelationCause(RelationCauseType Type, RelationEventKind Deed, CharacterId Actor, CharacterId Target)
{
    /// <summary>
    /// A cause other than a deed, with the character whose act it was.
    /// </summary>
    public RelationCause(RelationCauseType type, CharacterId actor) : this(type, 0, actor, CharacterId.None)
    {
    }

    /// <summary>
    /// A cause other than a deed, with no character.
    /// </summary>
    public RelationCause(RelationCauseType type) : this(type, CharacterId.None)
    {
    }
}

/// <summary>
/// A value-only observation of one changed relation, after the change and its threshold message. The player's faction has no table: a change between
/// it and another faction is reported as that faction's view of the player.
/// </summary>
/// <param name="Tick">The tick it changed in.</param>
/// <param name="Faction">The faction whose view changed (a position in <see cref="FactionRelations.Factions"/>).</param>
/// <param name="Towards">The faction it changed towards.</param>
/// <param name="Before">The relation before; an ended war can leave it as it was (the war flag cleared).</param>
/// <param name="After">The relation after.</param>
/// <param name="Cause">Why it changed.</param>
public readonly record struct RelationChange(long Tick, int Faction, int Towards, float Before, float After, RelationCause Cause);

/// <summary>
/// A system observing changed faction relations. Register it with the existing ModContext.AddSystem. Callbacks run serially, in system order, inside
/// the serial step that made the change (combat's blow, first aid, crime, dialogue, trading). The callback may read the world, update its own system
/// state (hashed through IStateHashed) and change relations; it must not change characters, orders or other world state. A change it makes through
/// <see cref="PlayerRelations"/> is not reported again (a direct <see cref="FactionRelations"/> write never is). A throw disables this observer for
/// that world.
/// </summary>
public interface IRelationChangeObserver : ITickSystem
{
    /// <summary>
    /// Called once for each changed relation, serially on the tick thread.
    /// </summary>
    void RelationChanged(World world, in RelationChange change);
}

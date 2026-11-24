using System.Numerics;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// Settings of the <see cref="KidnapSystem"/>; engine choices unless a doc label says otherwise.
/// </summary>
public sealed record KidnapSettings
{
    /// <summary>
    /// Each kidnapper's finder looks once in this many ticks, on its slot's turn.
    /// </summary>
    public int LookEveryTicks { get; init; } = 15;
    /// <summary>
    /// A kidnapper looks for a victim within its sight (the rescue finder's range; the sensed list's size is <b>Unknown</b>).
    /// </summary>
    public float Sight { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>
    /// Keep a <see cref="KidnapSystem.Log"/> (tests).
    /// </summary>
    public bool RecordLog { get; init; }
}

/// <summary>
/// What happened in a kidnapping (<see cref="KidnapEvent"/>).
/// </summary>
public enum KidnapEventKind
{
    /// <summary>
    /// The victim was lifted.
    /// </summary>
    Lifted,
    /// <summary>
    /// The victim was put in a cage.
    /// </summary>
    Caged,
    /// <summary>
    /// The carrier put the victim down for want of a cage.
    /// </summary>
    Dropped,
    /// <summary>
    /// The carry ended otherwise.
    /// </summary>
    GaveUp
}

/// <summary>
/// One kidnapping event of the log (<see cref="KidnapSystem.Log"/>): the tick, what happened, the carrier, the victim and the faction.
/// </summary>
public readonly record struct KidnapEvent(long Tick, KidnapEventKind Kind, CharacterId Carrier, CharacterId Victim, int Faction);

/// <summary>
/// Raiders kidnapping the downed (docs/game/factions-squads-towns.md "Campaigns", carry-loot.md 3.2; docs/simulation.md "Kidnapping"), in
/// <c>SlowWorld</c>, serially, after the carry system: goal 254 "Find and kidnap (with cages) urgent" is a goal of the one chooser
/// (<see cref="KidnapGoals"/>): a member whose lists hold it weighs it, and once chosen lifts the victim 132's finder found (<see cref="IsVictim"/>).
/// The lift ends the goal; the carrier's own goals (112 / 281) cage the victim, which is then <see cref="CharacterCold.Kidnapped"/> in a cage of the
/// kidnapper's faction. What the captors then do with the captive is <see cref="CaptiveSystem"/>'s. Not built: the witnesses' kidnapping crime.
/// </summary>
public sealed class KidnapSystem : ITickSystem, IStateHashed, IDutyGoalOwner
{
    #region State

    /// <summary>
    /// The population's data (relations, factions, prison time).
    /// </summary>
    readonly PopulationData _data;
    /// <summary>
    /// Goal 254: the finder's picks, the lifts it acts out and the victims lifted (<see cref="KidnapGoals"/>).
    /// </summary>
    readonly KidnapGoals _goals;
    /// <summary>
    /// The events since the world began, kept only with <see cref="KidnapSettings.RecordLog"/>.
    /// </summary>
    readonly List<KidnapEvent> _log = [];

    /// <summary>
    /// The kidnapping settings.
    /// </summary>
    public KidnapSettings Settings { get; }
    /// <summary>
    /// The relation event of a caging (<see cref="FactionDeeds.Caged"/>); null: none.
    /// </summary>
    public FactionDeeds? Deeds { get; set; }
    /// <summary>
    /// The events kept (<see cref="KidnapSettings.RecordLog"/>).
    /// </summary>
    public IReadOnlyList<KidnapEvent> Log => _log;

    /// <summary>
    /// Makes the system over the population's data, with the carry system that lifts and the settings (null: the defaults). The population system
    /// is not read: the goals read the members' lists through the AI.
    /// </summary>
    public KidnapSystem(PopulationData data, PopulationSystem population, CarrySystem carry, KidnapSettings? settings = null)
    {
        _data = data;
        Settings = settings ?? new KidnapSettings();
        _goals = new KidnapGoals(this, carry, data);
    }

    #endregion

    #region The victim

    /// <summary>
    /// Whether the character in <paramref name="slot"/> may be kidnapped by one of <paramref name="faction"/> (the finder of goal 132, carry-loot.md
    /// 3.2, <c>FUN_1408cd2d0</c> with the bed allowed, <b>Verified</b>): alive, of a carriable race that is not a robot (the exclude mask's
    /// bit 11, #1366), not carried, not an animal, and either lying in a bed (awake or not: the lift is then a kidnap that rolls) or down and not
    /// caged; and not of the faction or an ally of it (<b>engine reading</b> of the sensed-class masks).
    /// </summary>
    public bool IsVictim(World world, int slot, int faction)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive || table.Cold(slot) is not { Race: { Carriable: true, IsRobot: false }, Medical: { Dead: false } medical } c)
            return false;

        if (c.IsImprisoned || !c.CarriedBy.IsNone || c.Fighter is { IsAnimal: true }) return false;
        if (c.InSomething == HeldState.Bed ? !CarrySystem.CanBeKidnapped(table, slot) && !medical.Incapacitated : !medical.Incapacitated
                || c.InSomething != HeldState.None)
            return false;

        var relations = _data.Relations;
        return c.Faction != faction && !(relations.Contains(faction) && relations.Contains(c.Faction) && relations.IsAlly(faction, c.Faction));
    }

    /// <summary>
    /// Whether a free cage <paramref name="carrier"/> may use is among the candidates of its town at <paramref name="at"/>
    /// (<see cref="FixtureRegistry.CageCandidates"/>: the town it stands in, else its faction's nearest, plus its home's;
    /// <see cref="FixtureRegistry.FindCage"/>: the original's weights, its own faction's x100, any other faction's only when
    /// <see cref="FixtureRegistry.CanUse"/> passes).
    /// </summary>
    public bool HasCage(World world, Vector3 at, CharacterCold carrier)
    {
        var candidate = world.Fixtures.CageCandidates(world, _data, carrier, at);
        return world.Fixtures.FindCage(world, carrier, at, FixtureRegistry.HomeOf(world, carrier), f => !candidate(f)) is not null;
    }

    #endregion

    #region The tick

    /// <summary>
    /// Registers goal 254 (<see cref="KidnapGoals.Register"/>).
    /// </summary>
    void IDutyGoalOwner.RegisterGoals(DutyGoals goals) => _goals.Register(goals);

    /// <summary>
    /// The kidnapping step: 254's lifts move on (a lift that ended is done), then the kidnappers on their turn look for victims (each once in
    /// <see cref="KidnapSettings.LookEveryTicks"/>, docs/simulation.md "Spread sweeps").
    /// </summary>
    public void SlowWorld(World world)
    {
        _goals.Step(world);
        _goals.Look(world, world.Every(Settings.LookEveryTicks));
    }

    #endregion

    #region Log and hash

    /// <summary>
    /// Logs a kidnapping event (<see cref="KidnapSettings.RecordLog"/>) and writes it to the event log.
    /// </summary>
    internal void Note(World world, KidnapEventKind kind, CharacterId carrier, CharacterId victim, int faction)
    {
        if (Settings.RecordLog) _log.Add(new KidnapEvent(world.Tick, kind, carrier, victim, faction));

        string by = EventLog.Who(world, carrier), whom = EventLog.Who(world, victim);
        world.Events.Line(world, "kidnap", $"{kind}: {whom} by {by} ({EventLog.FactionName(world, faction)})");
    }

    /// <summary>
    /// Adds 254's records to the world's state hash, while there are any.
    /// </summary>
    public void Hash(ref StateHasher hasher) => _goals.Hash(ref hasher);

    #endregion
}

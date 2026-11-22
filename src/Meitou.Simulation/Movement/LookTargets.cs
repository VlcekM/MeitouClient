using System.Numerics;

namespace Meitou.Simulation.Movement;

/// <summary>
/// The points the characters' tasks have them look at this tick (the original's <c>Character::lookatPosition</c>, called by the updates of the
/// tasks listed in docs/animation.md "Head turning"), gathered once a publish from the systems that run those tasks and published as
/// <see cref="CharacterSnapshot.LookAt"/>. Read only: nothing here is hashed or saved, and only the host's drawn head turning reads it.
/// </summary>
public static class LookTargets
{
    /// <summary>
    /// Below this horizontal speed (units a second) a character stands for the tasks that look only while standing (the original tests its
    /// movement's <c>currentlyMoving</c>, <c>CharMovement +0x24</c>; the threshold is an engine choice).
    /// </summary>
    public const float StillSpeed = 1;

    /// <summary>
    /// Fills <paramref name="into"/> (slot to point) from the dialogue's walks, the slavers' shouts, the town duties' shoos, the alarm investigators
    /// and <see cref="World.DebugLooks"/> (last, so a debug look wins).
    /// </summary>
    public static void Gather(World world, Dictionary<int, Vector3> into)
    {
        into.Clear();
        var table = world.Characters;

        bool Still(int slot) =>
            new Vector2(table.Previous[slot].Velocity.X, table.Previous[slot].Velocity.Z).LengthSquared() < StillSpeed * StillSpeed;
        void Add(CharacterId who, CharacterId whom, bool stillOnly)
        {
            if (!table.IsAlive(who) || !table.IsAlive(whom) || who == whom || stillOnly && !Still(who.Slot)) return;
            into[who.Slot] = table.Previous[whom.Slot].Position;
        }

        // Task_PlayerTalkto, Task_TalktoPlayer (approaches), Task_FollowAndTalk, Task_TownStalker.
        if (world.SystemOf<Dialogue.DialogueSystem>() is { } talk)
            foreach (var (who, whom, stillOnly) in talk.LookPairs()) Add(who, whom, stillOnly);
        // Task_TalktoPlayer as goal 188 (a slaver shouting at its slave) and as goals 158, 161, 283 (shooing a stranger out).
        if (world.SystemOf<Kidnap.CaptiveSystem>() is { } captives)
            foreach (var job in captives.Jobs)
                if (job.Kind == Kidnap.CaptiveJobKind.Shout) Add(job.Worker, job.Captive, false);
        if (world.SystemOf<Kidnap.TownDutySystem>() is { } duties)
            foreach (var duty in duties.Duties)
                if (duty.Kind == Kidnap.TownDutyKind.Shoo) Add(duty.Member, duty.Body, false);
        if (world.SystemOf<Crime.CrimeSystem>() is { } crime) Investigators(world, crime, into);

        foreach (var (who, at) in world.DebugLooks)
            if (table.IsAlive(who)) into[who.Slot] = at;
    }

    /// <summary>
    /// <c>Task_InvestigateAlarms</c> (<c>FUN_140354260</c>, <b>Verified</b>): an investigator within <see cref="Crime.CrimeSystem.AlarmLookRange"/>
    /// of its alarm looks at it (the same alarm <see cref="Crime.CrimeSystem"/>'s serial step answers). No alarm: nobody has one to look at.
    /// </summary>
    static void Investigators(World world, Crime.CrimeSystem crime, Dictionary<int, Vector3> into)
    {
        if (crime.Alarms.Count == 0) return;

        var table = world.Characters;
        var state = table.Previous;
        for (int i = 0; i < state.Length; i++)
        {
            if (!state[i].Alive || table.Cold(i) is not { Ai: { Behaviour: Ai.AiBehaviour.Investigate, Goal: { } goal, Squad.Ai: { } board } } cold
                || !Ai.AiSystem.Holds(state[i], cold))
            {
                continue;
            }
            var alarm = crime.AlarmFor(board.Town, state[i].Position,
                goal.Task.Type == Data.Gameplay.Ai.TaskType.InvestigateAlarmsAlliesOnly ? cold.Faction : -1,
                Crime.CrimeSystem.FloorOf(world, i, state[i].Position), world.Sight.PlaceOf(i), world);
            if (alarm is not null && Vector3.DistanceSquared(alarm.Position, state[i].Position)
                    <= Crime.CrimeSystem.AlarmLookRange * Crime.CrimeSystem.AlarmLookRange)
                into[i] = alarm.Position;
        }
    }
}

namespace Meitou.Engine.Time;

/// <summary>
/// The world's clock: how many simulation ticks a frame owes (docs/simulation.md "Time model", owner decisions 1 and 2). One tick is
/// 1/30 s of GAME time at every speed, so the accumulator is fed <c>real dt x speed</c> and speeds 1, 2 and 5 run 30, 60 and 150
/// ticks per real second; a paused world runs none. Speeds are the game's {pause, 1, 2, 5} (Verified, docs/game/game-loop.md
/// "Speed and pause"). The pause key stores the speed and sets 0, and resumes at the stored one (<see cref="PauseKey"/>); the HUD pause button only sets 0; a separate <see cref="RequestedPause"/>
/// (menus, loading) stops the world without touching the speed, and the world is paused when either holds, as the game's paused
/// flag is "requested pause OR speed 0".
/// A frame owes at most <see cref="MaxTicksPerFrame"/> x speed ticks; the rest are dropped, so the game then runs slower than asked
/// (<see cref="AchievedSpeed"/>). Engine choice: the original caps the real frame time at 0.05 s BEFORE the speed multiplier (it slows
/// below 20 fps at every speed, Verified, docs/game/game-loop.md "Frame clocks"); this budget is 2 ticks (0.067 s of game time) at 1x and slows
/// below 15 fps: a larger one let a heavy world snowball, each late frame owing more ticks that made the next late too. The tick count depends only on the
/// summed <c>real time x speed</c>, not on how frames split it (the <see cref="FixedStepClock"/> property), as long as no tick is dropped.
/// </summary>
public sealed class SimulationClock
{
    /// <summary>The speeds the speed actions step through (the game's speed buttons without the pause).</summary>
    public static readonly double[] Speeds = [1, 2, 5];
    /// <summary>Simulation ticks per game second (owner decision 2).</summary>
    public const double TickRate = 30;
    /// <summary>Default most ticks a frame may run at speed 1 (engine choice): 1/15 s of game time; times the speed at the others.</summary>
    public const int DefaultMaxTicksPerFrame = 2;

    readonly FixedStepClock steps = new(TickRate);
    double speed = 1;
    double lastSpeed = 1;
    double rememberedSpeed;
    bool rememberedSet;
    int maxTicksPerFrame;

    public SimulationClock(int maxTicksPerFrame = DefaultMaxTicksPerFrame) => MaxTicksPerFrame = maxTicksPerFrame;

    /// <summary>Length of one tick in game seconds.</summary>
    public double TickSeconds => steps.TickSeconds;

    /// <summary>Most ticks one frame may run at speed 1; the budget at speed s is this times s.</summary>
    public int MaxTicksPerFrame
    {
        get => maxTicksPerFrame;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            maxTicksPerFrame = value;
        }
    }

    /// <summary>The speed: 0 (paused by the player), 1, 2 or 5.</summary>
    public double Speed => speed;
    /// <summary>The speed pause returns to.</summary>
    public double LastNonZeroSpeed => lastSpeed;
    /// <summary>The world is stopped from outside (menus, loading) without touching the speed.</summary>
    public bool RequestedPause { get; set; }
    /// <summary>No ticks run: a requested pause or speed 0.</summary>
    public bool IsPaused => RequestedPause || speed == 0;

    /// <summary>Sets the speed to one of 0, 1, 2 or 5 (the game's literals).</summary>
    public void SetSpeed(double value)
    {
        if (value != 0 && Array.IndexOf(Speeds, value) < 0) throw new ArgumentOutOfRangeException(nameof(value), "the speeds are 0, 1, 2 and 5");
        speed = value;
        if (value != 0) lastSpeed = value;
    }

    /// <summary>The pause action: speed 0, or back to the last non-zero speed.</summary>
    public void TogglePause() => SetSpeed(speed == 0 ? lastSpeed : 0);

    /// <summary>
    /// The original's pause function (docs/game/game-loop.md "Speed and pause"): the speed it resumes at is kept apart from the speed buttons. A
    /// call refreshes it from the current speed when that is above 0 (the first call also takes a speed of 0), so it is only ever written here.
    /// </summary>
    void Remember()
    {
        if (!rememberedSet) { rememberedSpeed = speed; rememberedSet = true; }
        if (speed > 0) rememberedSpeed = speed;
    }

    /// <summary>A player pause that the pause key resumes: stores the speed, then speed 0 (no change when already paused).</summary>
    public void Pause()
    {
        Remember();
        SetSpeed(0);
    }

    /// <summary>The pause key: running, it pauses (<see cref="Pause"/>); paused, it sets the stored speed, which stays paused when that is 0 (after a speed button paused before the key stored one).</summary>
    public void PauseKey()
    {
        if (speed > 0) { Pause(); return; }
        Remember();
        SetSpeed(rememberedSpeed);
    }

    /// <summary>Steps to the next speed (1, 2, 5); from a pause, resumes at the step above the last speed.</summary>
    public void Faster() => SetSpeed(Speeds[Math.Min(Array.IndexOf(Speeds, lastSpeed) + 1, Speeds.Length - 1)]);

    /// <summary>Steps to the previous speed (5, 2, 1); from a pause, resumes at the step below the last speed.</summary>
    public void Slower() => SetSpeed(Speeds[Math.Max(Array.IndexOf(Speeds, lastSpeed) - 1, 0)]);

    /// <summary>Ticks run so far.</summary>
    public long TotalTicks => steps.TotalTicks;
    /// <summary>Ticks dropped so far because frames owed more than the budget.</summary>
    public long DroppedTicks => steps.DroppedTicks;
    /// <summary>Position between the previous tick (0) and the next (1, exclusive), for interpolating the snapshots.</summary>
    public float Alpha => steps.Alpha;
    /// <summary>The speed the last <see cref="Advance"/> achieved: the asked speed scaled by the share of the owed ticks that ran.</summary>
    public double AchievedSpeed { get; private set; } = 1;

    /// <summary>The achieved speed over any span of time: the asked <paramref name="speed"/> scaled by the share of the owed ticks that ran (the speed itself when none were owed).</summary>
    public static double AchievedSpeedOver(double speed, long ran, long dropped) => ran + dropped == 0 ? speed : speed * ran / (ran + dropped);

    /// <summary>Drops the game time owed towards the next tick (<see cref="FixedStepClock.ResetPhase"/>): the duel's restart, so each lap ticks on the same frames.</summary>
    public void ResetPhase() => steps.ResetPhase();

    /// <summary>Adds a frame's real time and returns how many simulation ticks to run now.</summary>
    public int Advance(double realSeconds)
    {
        if (!(realSeconds >= 0) || double.IsInfinity(realSeconds)) throw new ArgumentOutOfRangeException(nameof(realSeconds));
        if (IsPaused)
        {
            steps.Advance(0);
            AchievedSpeed = speed;
            return 0;
        }
        long droppedBefore = steps.DroppedTicks;
        int run = steps.Advance(realSeconds * speed, (int)Math.Min((long)maxTicksPerFrame * (long)speed, int.MaxValue));
        long dropped = steps.DroppedTicks - droppedBefore;
        AchievedSpeed = AchievedSpeedOver(speed, run, dropped);
        return run;
    }
}

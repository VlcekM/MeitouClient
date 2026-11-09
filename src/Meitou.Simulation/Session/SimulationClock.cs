namespace Meitou.Simulation.Session;

/// <summary>
/// The world's clock: how many simulation ticks a frame owes (docs/simulation.md "Time model", owner decisions 1 and 2). One tick is 1/30 s of GAME
/// time at every speed, so the accumulator is fed <c>real dt x speed</c> and speeds 1, 2 and 5 run 30, 60 and 150 ticks per real second; a paused
/// world runs none. Speeds are the game's {pause, 1, 2, 5} (Verified, docs/game/game-loop.md "Speed and pause"); the two fast ones are settings
/// (<see cref="SetFastSpeeds"/>). The pause key stores the speed and sets 0, and resumes at the stored one (<see cref="PauseKey"/>); the HUD pause
/// button only sets 0; a separate <see cref="RequestedPause"/> (menus, loading) stops the world without touching the speed, and the world is paused
/// when either holds, as the game's paused flag is "requested pause OR speed 0". A frame's real time is capped at <see cref="MaxFrameSeconds"/>
/// BEFORE the speed multiplier, as the original does (FUN_14082ac70; it slows below 20 fps at every speed, Verified, docs/game/game-loop.md "Frame
/// clocks"). A frame also owes at most <see cref="MaxTicksPerFrame"/> x speed ticks, a safety limit above that cap (engine choice: 2 ticks is 0.067 s
/// of game time at 1x, the cap owes 1.5); the rest are dropped (<see cref="AchievedSpeed"/>). The tick count depends only on the summed <c>real time
/// x speed</c>, not on how frames split it (the <see cref="FixedStepClock"/> property), as long as no tick is dropped.
/// </summary>
public sealed class SimulationClock
{
    /// <summary>
    /// The game's speeds the speed actions step through (its speed buttons without the pause).
    /// </summary>
    public static readonly double[] DefaultSpeeds = [1, 2, 5];
    /// <summary>
    /// Range of the second speed (<see cref="SetFastSpeeds"/>); the third is above it.
    /// </summary>
    public const double MinFastSpeed = 1.5, MaxFastSpeed = 4;
    /// <summary>
    /// Range of the third speed (<see cref="SetFastSpeeds"/>).
    /// </summary>
    public const double MinFastestSpeed = 5, MaxFastestSpeed = 20;
    /// <summary>
    /// Simulation ticks per game second (owner decision 2).
    /// </summary>
    public const double TickRate = 30;
    /// <summary>
    /// The longest real frame time that counts, before the speed multiplier (the original's <c>DAT_141696c74</c>, 0.05 s).
    /// </summary>
    public const double MaxFrameSeconds = 0.05;
    /// <summary>
    /// Default most ticks a frame may run at speed 1 (engine choice): 1/15 s of game time; times the speed at the others.
    /// </summary>
    public const int DefaultMaxTicksPerFrame = 2;

    /// <summary>
    /// The fixed step that counts the ticks, fed game time.
    /// </summary>
    readonly FixedStepClock _steps = new(TickRate);
    /// <summary>
    /// The speeds the speed actions step through (see <see cref="Speeds"/>).
    /// </summary>
    readonly double[] _speeds = [.. DefaultSpeeds];
    /// <summary>
    /// The speed (see <see cref="Speed"/>).
    /// </summary>
    double _speed = 1;
    /// <summary>
    /// The last non-zero speed (see <see cref="LastNonZeroSpeed"/>).
    /// </summary>
    double _lastSpeed = 1;
    /// <summary>
    /// The speed the pause key resumes at (see <see cref="Remember"/>).
    /// </summary>
    double _rememberedSpeed;
    /// <summary>
    /// Whether <see cref="_rememberedSpeed"/> was ever set.
    /// </summary>
    bool _rememberedSet;
    /// <summary>
    /// The most ticks a frame may run at speed 1 (see <see cref="MaxTicksPerFrame"/>).
    /// </summary>
    int _maxTicksPerFrame;

    /// <summary>
    /// A clock at speed 1 that runs at most <paramref name="maxTicksPerFrame"/> ticks a frame at speed 1.
    /// </summary>
    public SimulationClock(int maxTicksPerFrame = DefaultMaxTicksPerFrame) => MaxTicksPerFrame = maxTicksPerFrame;

    /// <summary>
    /// The speeds the speed actions and buttons step through: 1, then the two fast ones (the game's 2 and 5 by default).
    /// </summary>
    public IReadOnlyList<double> Speeds => _speeds;
    /// <summary>
    /// The step of <see cref="Speeds"/> that is <paramref name="value"/>, or -1.
    /// </summary>
    public int StepOf(double value) => Array.IndexOf(_speeds, value);

    /// <summary>
    /// Length of one tick in game seconds.
    /// </summary>
    public double TickSeconds => _steps.TickSeconds;

    /// <summary>
    /// Most ticks one frame may run at speed 1; the budget at speed s is this times s.
    /// </summary>
    public int MaxTicksPerFrame
    {
        get => _maxTicksPerFrame;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxTicksPerFrame = value;
        }
    }

    /// <summary>
    /// The speed: 0 (paused by the player) or above; one of <see cref="Speeds"/> but for the console's and benches' own.
    /// </summary>
    public double Speed => _speed;
    /// <summary>
    /// The speed pause returns to.
    /// </summary>
    public double LastNonZeroSpeed => _lastSpeed;
    /// <summary>
    /// The world is stopped from outside (menus, loading) without touching the speed.
    /// </summary>
    public bool RequestedPause { get; set; }
    /// <summary>
    /// No ticks run: a requested pause or speed 0.
    /// </summary>
    public bool IsPaused => RequestedPause || _speed == 0;

    /// <summary>
    /// Sets the speed: 0 pauses; any other finite one runs (the buttons and keys pick one of <see cref="Speeds"/>).
    /// </summary>
    public void SetSpeed(double value)
    {
        if (!(value >= 0) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(nameof(value), "a speed is 0 or above");

        _speed = value;
        if (value != 0) _lastSpeed = value;
    }

    /// <summary>
    /// Sets the second and third speeds (Advanced settings), clamped to their ranges. A speed, last speed or pause-key speed that was one of the old
    /// fast speeds becomes the new one at its place, so the lit speed button and the resume stay on the same step.
    /// </summary>
    public void SetFastSpeeds(double fast, double fastest)
    {
        double[] old = [.. _speeds];
        _speeds[1] = Math.Clamp(fast, MinFastSpeed, MaxFastSpeed);
        _speeds[2] = Math.Clamp(fastest, MinFastestSpeed, MaxFastestSpeed);
        _speed = Remap(old, _speed);
        _lastSpeed = Remap(old, _lastSpeed);
        _rememberedSpeed = Remap(old, _rememberedSpeed);
    }

    /// <summary>
    /// The value at the same step of the new speeds when it was one of the old fast ones, else unchanged.
    /// </summary>
    double Remap(double[] old, double value) => Array.IndexOf(old, value) is var i and > 0 ? _speeds[i] : value;

    /// <summary>
    /// The pause action: speed 0, or back to the last non-zero speed.
    /// </summary>
    public void TogglePause() => SetSpeed(_speed == 0 ? _lastSpeed : 0);

    /// <summary>
    /// The original's pause function (docs/game/game-loop.md "Speed and pause"): the speed it resumes at is kept apart from the speed buttons. A
    /// call refreshes it from the current speed when that is above 0 (the first call also takes a speed of 0), so it is only ever written here.
    /// </summary>
    void Remember()
    {
        if (!_rememberedSet)
        {
            _rememberedSpeed = _speed;
            _rememberedSet = true;
        }

        if (_speed > 0) _rememberedSpeed = _speed;
    }

    /// <summary>
    /// A player pause that the pause key resumes: stores the speed, then speed 0 (no change when already paused).
    /// </summary>
    public void Pause()
    {
        Remember();
        SetSpeed(0);
    }

    /// <summary>
    /// The pause key: running, it pauses (<see cref="Pause"/>); paused, it sets the stored speed, which stays paused when that is 0 (after a speed
    /// button paused before the key stored one).
    /// </summary>
    public void PauseKey()
    {
        if (_speed > 0)
        {
            Pause();
            return;
        }

        Remember();
        SetSpeed(_rememberedSpeed);
    }

    /// <summary>
    /// Steps to the next of <see cref="Speeds"/> above the last speed (the top one at the top); from a pause, resumes there.
    /// </summary>
    public void Faster() => SetSpeed(_speeds.FirstOrDefault(s => s > _lastSpeed, _speeds[^1]));

    /// <summary>
    /// Steps to the next of <see cref="Speeds"/> below the last speed (1 at the bottom); from a pause, resumes there.
    /// </summary>
    public void Slower() => SetSpeed(_speeds.LastOrDefault(s => s < _lastSpeed, _speeds[0]));

    /// <summary>
    /// Ticks run so far.
    /// </summary>
    public long TotalTicks => _steps.TotalTicks;
    /// <summary>
    /// Ticks dropped so far because frames owed more than the budget.
    /// </summary>
    public long DroppedTicks => _steps.DroppedTicks;
    /// <summary>
    /// Position between the previous tick (0) and the next (1, exclusive), for interpolating the snapshots.
    /// </summary>
    public float Alpha => _steps.Alpha;
    /// <summary>
    /// The speed the last <see cref="Advance"/> achieved: the asked speed scaled by the share of the owed ticks that ran.
    /// </summary>
    public double AchievedSpeed { get; private set; } = 1;

    /// <summary>
    /// The achieved speed over any span of time: the asked <paramref name="speed"/> scaled by the share of the owed ticks that ran (the speed itself
    /// when none were owed).
    /// </summary>
    public static double AchievedSpeedOver(double speed, long ran, long dropped) => ran + dropped == 0 ? speed : speed * ran / (ran + dropped);

    /// <summary>
    /// Drops the game time owed towards the next tick (<see cref="FixedStepClock.ResetPhase"/>): the duel's restart, so each lap ticks on the same
    /// frames.
    /// </summary>
    public void ResetPhase() => _steps.ResetPhase();

    /// <summary>
    /// Adds a frame's real time and returns how many simulation ticks to run now.
    /// </summary>
    public int Advance(double realSeconds)
    {
        if (!(realSeconds >= 0) || double.IsInfinity(realSeconds))
            throw new ArgumentOutOfRangeException(nameof(realSeconds));

        if (IsPaused)
        {
            _steps.Advance(0);
            AchievedSpeed = _speed;
            return 0;
        }

        long droppedBefore = _steps.DroppedTicks;
        int run = _steps.Advance(Math.Min(realSeconds, MaxFrameSeconds) * _speed,
            (int)Math.Min(Math.Ceiling(_maxTicksPerFrame * _speed), int.MaxValue));
        long dropped = _steps.DroppedTicks - droppedBefore;
        AchievedSpeed = AchievedSpeedOver(_speed, run, dropped);
        return run;
    }
}
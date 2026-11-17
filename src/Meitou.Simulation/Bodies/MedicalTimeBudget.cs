using System.Numerics;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The original character loop's round-robin update budgets (<c>FUN_140786e30</c>, docs/game/game-loop.md "The character update budget"), kept
/// for the effects the simulation needs from them: a character inside a medical budget gets the frame time added to its medical time a second time
/// (<c>FUN_1405cfd90</c>), so the medical rates run 1 to 2 x dt (docs/game/character-stats.md "Time"); its sight pass takes its next sensed
/// character on that turn (slot <c>0x278</c>, <see cref="HadTurn"/>, docs/game/dialogue.md "Sight"); and the medical and periodic turns time
/// when a change of the top speed reaches the stats and the mover (<see cref="TopSpeedDelay"/>). One tick stands for one frame of that loop and the
/// occupied slots in slot order for its character list; each medical turn adds <see cref="World.TickSeconds"/> to
/// <see cref="CharacterCold.MedicalBudgetSeconds"/>. Serial, once a tick, before the body step.
/// </summary>
public sealed class MedicalTimeBudget
{
    /// <summary>
    /// Turns a frame for the characters without the update flag (<c>+0xe4</c> clear).
    /// </summary>
    public const int UnflaggedTurns = 6;

    /// <summary>
    /// The flagged characters' turns a frame when a quarter of last frame's flagged count is under 5.
    /// </summary>
    public const int FlaggedTurnsMin = 4;

    /// <summary>
    /// Periodic updates a frame (slot <c>0xe8</c> <c>periodicUpdate</c>, every character, tier A).
    /// </summary>
    public const int PeriodicTurns = 8;

    /// <summary>
    /// The settings' <c>npc range</c> default (settings.cfg, docs/formats/settings.md): without a camera view, within it of the camera an NPC counts
    /// as in view and so flagged. Meitou flags every NPC within this distance of <see cref="World.ViewPosition"/>.
    /// </summary>
    public const float NpcRange = 3250;

    /// <summary>
    /// The unflagged budget's cursor: the list position of its last turn of a full round (-1: start from the head).
    /// </summary>
    int _unflaggedCursor = -1;
    /// <summary>
    /// The flagged budget's cursor, as <see cref="_unflaggedCursor"/>.
    /// </summary>
    int _flaggedCursor = -1;
    /// <summary>
    /// The periodic budget's cursor, as <see cref="_unflaggedCursor"/>.
    /// </summary>
    int _periodicCursor = -1;
    /// <summary>
    /// How many characters were flagged last frame (sets this frame's flagged turns).
    /// </summary>
    int _flaggedLastFrame;
    /// <summary>
    /// Per slot: whether it had a turn this frame (<see cref="HadTurn"/>).
    /// </summary>
    bool[] _turns = [];

    /// <summary>
    /// The flagged characters' turns this frame: a quarter of last frame's flagged count, at least <see cref="FlaggedTurnsMin"/> while that quarter
    /// is under 5.
    /// </summary>
    public static int FlaggedTurns(int flaggedLastFrame)
    {
        int quarter = flaggedLastFrame / 4;
        return quarter < 5 ? FlaggedTurnsMin : quarter;
    }

    /// <summary>
    /// Whether the character in <paramref name="slot"/> had a medical turn this frame.
    /// </summary>
    public bool HadTurn(int slot) => (uint)slot < (uint)_turns.Length && _turns[slot];

    /// <summary>
    /// One frame of the budgets over the occupied slots: who gets a medical turn gets a tick of extra medical time; the job runs the medical turns
    /// before the periodic ones (<c>FUN_140787940</c>'s list order).
    /// </summary>
    public void Run(World world)
    {
        var table = world.Characters;
        int listed = table.Count;
        var unflaggedRound = new Round(_unflaggedCursor, UnflaggedTurns);
        var flaggedRound = new Round(_flaggedCursor, FlaggedTurns(_flaggedLastFrame));
        var periodicRound = new Round(_periodicCursor, PeriodicTurns);
        int flagged = 0;
        int position = 0;
        if (_turns.Length < table.HighWater) _turns = new bool[Math.Max(table.HighWater, 2 * _turns.Length)];
        Array.Clear(_turns);
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Previous[i].Alive || table.Cold(i) is not { } cold) continue;
            bool flag = Flagged(world, cold, table.Previous[i].Position);
            bool turn = flag ? flaggedRound.Take(position, listed) : unflaggedRound.Take(position, listed);
            _turns[i] = turn;
            if (flag) flagged++;
            if (turn && cold.Medical is { Dead: false }) cold.MedicalBudgetSeconds += world.TickSeconds;
            if (turn) TopSpeedDelay.MedicalTurn(world, cold, table.Previous[i].Position);
            if (periodicRound.Take(position, listed)) TopSpeedDelay.PeriodicTurn(cold);
            position++;
        }
        _unflaggedCursor = unflaggedRound.NextCursor;
        _flaggedCursor = flaggedRound.NextCursor;
        _periodicCursor = periodicRound.NextCursor;
        _flaggedLastFrame = flagged;
    }

    /// <summary>
    /// The update flag (<c>Character +0xe4</c>, <c>FUN_1405c9f60</c>, <b>Verified</b> for the two main terms): set for the player's faction
    /// (<c>FUN_1405c79c0</c>); for an NPC while it is on screen, the range and frustum test of <see cref="MedicalClock.OnScreen"/> against the camera
    /// the host passed (<see cref="World.View"/>), else within <see cref="NpcRange"/> of <see cref="World.ViewPosition"/>. Without a camera (headless
    /// worlds, tests) only the player's characters have it. The original's other terms (an NPC engaged with the player, building and town
    /// refinements) are not modelled. The NPC term is also the character's in-view flag (<c>+0x1a9</c>, written by the same function: on screen, or
    /// player's faction), which the sight pass reads.
    /// </summary>
    internal static bool Flagged(World world, CharacterCold cold, Vector3 position) =>
        cold.IsPlayer || (world.View is { } view ? MedicalClock.OnScreen(view, position)
            : world.ViewPosition is { } eye && Vector3.DistanceSquared(position, eye) <= NpcRange * NpcRange);

    /// <summary>
    /// One budget's pass over the list: it continues after last frame's last turn, and the list's head takes what the tail leaves (the original's
    /// wrap rule).
    /// </summary>
    struct Round(int cursor, int turns)
    {
        /// <summary>
        /// The turns given so far this frame.
        /// </summary>
        int _taken;
        /// <summary>
        /// The list position of the last turn given (-1: none).
        /// </summary>
        int _last = -1;

        /// <summary>
        /// The next frame's cursor: the last turn of a full round, else -1 (start from the head).
        /// </summary>
        public readonly int NextCursor => _taken == turns ? _last : -1;

        /// <summary>
        /// Whether the character at list <paramref name="position"/> gets a turn: it lies after the cursor, or the turns so far are under what the
        /// tail after the cursor would leave of a full round (the cursor less the list length, plus one, plus the turns). The last turn of a full
        /// round becomes the next frame's cursor; a round that runs out of characters starts the next frame from the head.
        /// </summary>
        public bool Take(int position, int listed)
        {
            if (_taken >= turns || cursor >= position && _taken >= cursor - listed + 1 + turns) return false;
            _taken++;
            if (_taken == turns) _last = position;
            return true;
        }
    }
}
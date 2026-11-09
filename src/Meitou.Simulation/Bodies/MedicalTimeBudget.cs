using System.Numerics;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The original character loop's two round-robin update budgets (<c>FUN_140786e30</c>, docs/game/game-loop.md "The character update budget"), kept
/// for the one effect the simulation needs from them: a character inside a budget gets the frame time added to its medical time a second time
/// (<c>FUN_1405cfd90</c>), so the medical rates run 1 to 2 x dt (docs/game/character-stats.md "Time"). One tick stands for one frame of that loop and
/// the occupied slots in slot order for its character list; each turn adds <see cref="World.TickSeconds"/> to
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
    /// How many characters were flagged last frame (sets this frame's flagged turns).
    /// </summary>
    int _flaggedLastFrame;

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
    /// One frame of the budgets over the occupied slots: who gets a turn gets a tick of extra medical time.
    /// </summary>
    public void Run(World world)
    {
        var table = world.Characters;
        int listed = table.Count;
        var unflaggedRound = new Round(_unflaggedCursor, UnflaggedTurns);
        var flaggedRound = new Round(_flaggedCursor, FlaggedTurns(_flaggedLastFrame));
        int flagged = 0;
        int position = 0;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Previous[i].Alive || table.Cold(i) is not { } cold) continue;
            bool flag = Flagged(world, cold, table.Previous[i].Position);
            bool turn = flag ? flaggedRound.Take(position, listed) : unflaggedRound.Take(position, listed);
            if (flag) flagged++;
            if (turn && cold.Medical is { Dead: false }) cold.MedicalBudgetSeconds += world.TickSeconds;
            position++;
        }
        _unflaggedCursor = unflaggedRound.NextCursor;
        _flaggedCursor = flaggedRound.NextCursor;
        _flaggedLastFrame = flagged;
    }

    /// <summary>
    /// The update flag (<c>Character +0xe4</c>, <c>FUN_1405c9f60</c>, <b>Verified</b> for the two main terms): set for the player's faction
    /// (<c>FUN_1405c79c0</c>); for an NPC while it is on screen, the range and frustum test of <see cref="MedicalClock.OnScreen"/> against the camera
    /// the host passed (<see cref="World.View"/>), else within <see cref="NpcRange"/> of <see cref="World.ViewPosition"/>. Without a camera (headless
    /// worlds, tests) only the player's characters have it. The original's other terms (an NPC engaged with the player, building and town
    /// refinements) are not modelled.
    /// </summary>
    static bool Flagged(World world, CharacterCold cold, Vector3 position) =>
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
using System.Numerics;

namespace Meitou.Game.Audio;

/// <summary>
/// A door as the sounds need it: where it is, its record's <c>door type</c> (DoorMaterial 0 Wood, 1 Mechanical, 2 Gate), its motion (0 closed, 1
/// open, 2 opening, 3 closing) and whether it is broken.
/// </summary>
public readonly record struct DoorSoundState(Vector3 Position, int Type, int Motion, bool Broken);

/// <summary>
/// The doors' sounds (docs/audio.md "Other world-side events", from the exe's door class): each door is a game object of its own with
/// <c>Door_Type</c> set from its record's <c>door type</c>; <c>Door_Open</c> when it starts to open (0 to 2), <c>Door_Close</c> when it starts to
/// close (1 to 3), <c>Door_Shut</c> when closing ends (3 to 0) and when opening ends (2 to 1), and <c>Break</c> when it breaks. The host hands every
/// door it knows each frame; a door it no longer hands is closed (the snapshot leaves out plainly closed doors). A door seen for the first time while
/// it moves was closed (opening) or open (closing) the frame before; one seen at rest, or broken, is taken as it is, so a load (whose open doors all
/// appear at once) is quiet.
/// </summary>
public sealed class DoorSounds(WwisePlayer player)
{
    /// <summary>
    /// The first game object id of the doors (above the world emitters' range).
    /// </summary>
    public const ulong FirstObject = 1UL << 40;
    /// <summary>
    /// Doors farther than this from the camera eye make no sound (<b>engine choice</b>: the longest emitter range; nothing that far is heard).
    /// </summary>
    public const float Range = 3000;

    /// <summary>
    /// The <c>Door_Type</c> switch values by the record's <c>door type</c> (0 Wood, 1 Mechanical, 2 Gate); other types are clamped into the table.
    /// </summary>
    static readonly string[] TypeNames = ["Wood", "Mechanical_Metal", "Gate"];

    /// <summary>
    /// One door's sound state: its game object and the motion and broken flag it was last stepped to.
    /// </summary>
    sealed class Door(ulong id)
    {
        /// <summary>
        /// The door's game object id (from <see cref="FirstObject"/> up).
        /// </summary>
        public readonly ulong Object = id;
        /// <summary>
        /// The motion last stepped to (0 closed, 1 open, 2 opening, 3 closing).
        /// </summary>
        public int Motion;
        /// <summary>
        /// Whether the door was broken at the last step.
        /// </summary>
        public bool Broken;
        /// <summary>
        /// Whether <see cref="Update"/> handed the door since <see cref="Begin"/> this frame.
        /// </summary>
        public bool Seen;
        /// <summary>
        /// The state last handed: its position places the object and its type picks <c>Door_Type</c>.
        /// </summary>
        public DoorSoundState Last;
    }

    /// <summary>
    /// The doors by building id and door index (the snapshot's key; a string made of the two per door a frame was garbage).
    /// </summary>
    readonly Dictionary<(string Building, int Door), Door> _doors = [];
    /// <summary>
    /// The next door's game object id.
    /// </summary>
    ulong _next = FirstObject;

    /// <summary>
    /// Fix (default): set the <c>Break</c> switch before <c>Break</c>, so doors and gates sound when they break. Off: as the exe, which never sets
    /// it, so <c>Break</c> plays nothing.
    /// </summary>
    public bool FixBreak { get; set; } = true;

    /// <summary>
    /// Events posted since the world was set (statistics and tests).
    /// </summary>
    public int Posted { get; private set; }

    /// <summary>
    /// Starts a frame's steps: every door is taken as gone until <see cref="Update"/> hands it.
    /// </summary>
    public void Begin()
    {
        foreach (var d in _doors.Values) d.Seen = false;
    }

    /// <summary>
    /// Steps door <paramref name="key"/> to its state now. <paramref name="eye"/> is the camera eye (the range's origin).
    /// </summary>
    public void Update((string Building, int Door) key, DoorSoundState state, Vector3 eye)
    {
        if (!_doors.TryGetValue(key, out var door))
        {
            _doors[key] = door = new Door(_next++);
            door.Motion = state.Motion switch { 2 => 0, 3 => 1, var m => m };
            door.Broken = state.Broken;
        }
        door.Seen = true;
        door.Last = state;
        Step(door, state.Motion, state.Broken, eye);
    }

    /// <summary>
    /// Ends a frame's steps: a door not handed since <see cref="Begin"/> is closed and unlocked again, where it was last seen.
    /// </summary>
    public void End(Vector3 eye)
    {
        foreach (var door in _doors.Values)
            if (!door.Seen) Step(door, 0, door.Broken, eye);
    }

    /// <summary>
    /// Moves a door to <paramref name="motion"/> and <paramref name="broken"/> and, when it changed and is within <see cref="Range"/> of the eye,
    /// places its object, sets <c>Door_Type</c> and posts <c>Break</c>, <c>Door_Open</c>, <c>Door_Close</c> and <c>Door_Shut</c> as the change asks
    /// (a state skipped between frames still gets its sounds).
    /// </summary>
    void Step(Door door, int motion, bool broken, Vector3 eye)
    {
        int before = door.Motion;
        bool breaks = broken && !door.Broken;
        door.Motion = motion;
        door.Broken = broken;
        if (before == motion && !breaks) return;
        var at = door.Last;
        if (player.HeardDistanceSquared(at.Position, eye) > Range * Range) return;
        player.SetPosition(door.Object, at.Position);
        player.SetSwitch("Door_Type", TypeNames[Math.Clamp(at.Type, 0, TypeNames.Length - 1)], door.Object);
        if (breaks)
        {
            // The exe never sets the Break switch group, so its Break plays nothing (docs/audio.md "Other world-side events"); the bank's cases are
            // Door and Gate.
            if (FixBreak) player.SetSwitch("Break", at.Type == 2 ? "Gate" : "Door", door.Object);
            Post("Break", door);
        }
        // A state skipped between two frames still gets its sounds: closed to open is a start and an end, and so on.
        if (motion == 2 || (motion == 1 && before == 0)) Post("Door_Open", door);
        if (motion == 3 || (motion == 0 && before == 1)) Post("Door_Close", door);
        if ((motion == 0 && before is 3 or 1) || (motion == 1 && before is 2 or 0)) Post("Door_Shut", door);
    }

    /// <summary>
    /// Posts an event on the door's object and counts it in <see cref="Posted"/>.
    /// </summary>
    void Post(string eventName, Door door)
    {
        player.Post(eventName, door.Object);
        Posted++;
    }

    /// <summary>
    /// A new world: every door's object is let go.
    /// </summary>
    public void Clear()
    {
        foreach (var d in _doors.Values) player.UnregisterObject(d.Object);
        _doors.Clear();
        Posted = 0;
    }
}
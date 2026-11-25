using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Game.Rendering.Frame;
using Meitou.Simulation;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// Photo mode's posing (docs/viewer.md "Photo mode", Pose tab): a character is chosen (a click on it, or the arrows through those in view, nearest
/// first) and drawn in another animation (any of the database's records its skeleton has, held at a moment or playing), somewhere else on the ground,
/// turned, raised, with its weapon drawn or sheathed, or hidden; the other characters can be hidden too. All of it is drawn only: the simulation keeps
/// every character where and as it is, and leaving the mode drops it.
/// </summary>
sealed class PhotoPoses(GameHost host)
{
    #region State

    /// <summary>
    /// What is changed about one character's drawing.
    /// </summary>
    sealed class Posed
    {
        /// <summary>
        /// The animation record drawn in place of the simulation's layers; null: its own.
        /// </summary>
        public string? Pose;

        /// <summary>
        /// The pose's moment, seconds into its clip.
        /// </summary>
        public float Time;

        /// <summary>
        /// The pose's clip length in seconds (1 when unknown).
        /// </summary>
        public float Length = 1;

        /// <summary>
        /// The pose plays (looping, at <see cref="Speed"/> in real time) rather than holding <see cref="Time"/>.
        /// </summary>
        public bool Playing;

        /// <summary>
        /// The pose's play rate while it plays.
        /// </summary>
        public float Speed = 1;

        /// <summary>
        /// Where the moves are measured from and the way they face: the character's drawn place and facing when it was first changed.
        /// </summary>
        public Vector3 Origin;

        /// <summary>
        /// The facing the moves are measured along (radians, the simulation's yaw: forward is (sin, 0, cos)).
        /// </summary>
        public float Facing;

        /// <summary>
        /// Units moved forward and to the side of <see cref="Origin"/> along <see cref="Facing"/>; both 0: its own place.
        /// </summary>
        public float Forward, Side;

        /// <summary>
        /// The ground's height at the moved place (a dragged character stands where the pointer met a floor or the terrain).
        /// </summary>
        public float Ground;

        /// <summary>
        /// Units raised above its place.
        /// </summary>
        public float Lift;

        /// <summary>
        /// Radians turned from its own facing.
        /// </summary>
        public float Turn;

        /// <summary>
        /// The weapon in hand: null as the simulation has it, else drawn or sheathed.
        /// </summary>
        public bool? Weapon;

        /// <summary>
        /// No weapon is drawn, in hand or stowed (<see cref="Weapon"/> is then of no matter).
        /// </summary>
        public bool NoWeapon;

        /// <summary>
        /// Not drawn.
        /// </summary>
        public bool Hidden;

        /// <summary>
        /// The head turns to the camera (people only).
        /// </summary>
        public bool Look;

        /// <summary>
        /// The character is moved off its own place.
        /// </summary>
        public bool Moved => Forward != 0 || Side != 0;
    }

    /// <summary>
    /// The changed characters.
    /// </summary>
    readonly Dictionary<CharacterId, Posed> _posed = [];

    /// <summary>
    /// The character the Pose tab changes; null: none chosen.
    /// </summary>
    public CharacterId? Selected { get; private set; }

    /// <summary>
    /// Every character but the selected and the changed ones is hidden.
    /// </summary>
    public bool OthersHidden;

    /// <summary>
    /// The chosen character is being dragged over the ground.
    /// </summary>
    public bool Dragging { get; private set; }

    /// <summary>
    /// The farthest a character is offered by the arrows (units: 300 m).
    /// </summary>
    const float Reach = 3000;

    /// <summary>
    /// The furthest a character is moved from its own place by the sliders, each way (units: 20 m).
    /// </summary>
    public const float MoveRange = 200;

    /// <summary>
    /// The most a character is raised or lowered (units: up 5 m, down 2 m).
    /// </summary>
    public const float LiftUp = 50, LiftDown = -20;

    #endregion

    #region Choosing

    /// <summary>
    /// The chosen character's snapshot, null when none is chosen or it is gone.
    /// </summary>
    public CharacterSnapshot? SelectedCharacter =>
        Selected is { } id ? host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>
    /// The chosen character's name, or "none".
    /// </summary>
    public string SelectedName => SelectedCharacter is { } c ? c.Name.Length > 0 ? c.Name : "unnamed" : "none";

    /// <summary>
    /// Chooses <paramref name="id"/>.
    /// </summary>
    public void Select(CharacterId? id)
    {
        (Selected, _poseNames, ListOpen, Dragging) = (id, null, false, false);
    }

    /// <summary>
    /// Chooses the next character in view (<paramref name="by"/> 1) or the one before (-1): those drawn within <see cref="Reach"/> of the eye and in
    /// front of it, nearest first.
    /// </summary>
    public void Step(int by)
    {
        var eye = host.Camera.Eye;
        var forward = host.Camera.Forward;
        var shown = host.Session.CurrentSnapshot.Characters
            .Where(c => c.Appearance is not null && Vector3.DistanceSquared(c.Position, eye) < Reach * Reach
                && Vector3.Dot(c.Position - eye, forward) > 0)
            .OrderBy(c => Vector3.DistanceSquared(c.Position, eye))
            .Select(c => c.Id)
            .ToList();
        if (shown.Count == 0)
        {
            Select(null);
            return;
        }

        int at = Selected is { } id ? shown.IndexOf(id) : -1;
        Select(shown[at < 0 ? (by > 0 ? 0 : shown.Count - 1) : ((at + by) % shown.Count + shown.Count) % shown.Count]);
    }

    /// <summary>
    /// The character drawn under <paramref name="pixel"/>, if any.
    /// </summary>
    public CharacterId? At(Vector2 pixel) => host.Player.Picking.PickWhere(pixel, c => c.Appearance is not null);

    #endregion

    #region Changing

    /// <summary>
    /// The chosen character's changes, made (from where and how it is drawn now) when it has none yet; null when none is chosen.
    /// </summary>
    Posed? Edit()
    {
        if (SelectedCharacter is not { } c) return null;
        if (_posed.TryGetValue(c.Id, out var posed)) return posed;

        float facing = float.IsNaN(c.DrawYaw) ? c.Yaw : c.DrawYaw;
        return _posed[c.Id] = new Posed { Origin = c.Position, Facing = facing, Ground = c.Position.Y };
    }

    /// <summary>
    /// The chosen character's changes, without making them.
    /// </summary>
    Posed? Current => Selected is { } id ? _posed.GetValueOrDefault(id) : null;

    /// <summary>
    /// Whether character <paramref name="id"/> is drawn in a chosen pose.
    /// </summary>
    public bool HasPose(CharacterId id) => _posed.TryGetValue(id, out var p) && p.Pose is not null;

    /// <summary>
    /// The chosen character's pose record, null for its own animation.
    /// </summary>
    public string? Pose => Current?.Pose;

    /// <summary>
    /// The chosen character's pose moment in seconds, and its clip's length.
    /// </summary>
    public (float Time, float Length) PoseTime => Current is { Pose: not null } p ? (p.Time, p.Length) : (0, 1);

    /// <summary>
    /// How many keys the chosen pose's clip has on the root bone (0 when unknown, for the control channel's checks).
    /// </summary>
    public int RootKeys
    {
        get
        {
            if (Current is not { Pose: not null } p || host.AnimationLibrary is not { } library) return 0;

            int at = library.IndexOfAny(p.Pose);
            return Lengths(SelectedCharacter)?.RootKeys(at >= 0 ? library.Definitions[at].Clip : p.Pose) ?? 0;
        }
    }

    /// <summary>
    /// Holds the chosen character's pose at <paramref name="seconds"/> into its clip.
    /// </summary>
    public void SetPoseTime(float seconds)
    {
        if (Current is not { Pose: not null } p) return;
        p.Time = Math.Clamp(seconds, 0, p.Length);
    }

    /// <summary>
    /// The chosen character's pose plays.
    /// </summary>
    public bool Playing
    {
        get => Current?.Playing ?? false;
        set
        {
            if (Edit() is { } p) p.Playing = value;
        }
    }

    /// <summary>
    /// The chosen character's pose's play rate.
    /// </summary>
    public float Speed
    {
        get => Current?.Speed ?? 1;
        set
        {
            if (Edit() is { } p) p.Speed = value;
        }
    }

    /// <summary>
    /// Units the chosen character is moved forward of its own place (along its own facing).
    /// </summary>
    public float Forward
    {
        get => Current?.Forward ?? 0;
        set => Move(value, Current?.Side ?? 0);
    }

    /// <summary>
    /// Units the chosen character is moved to its side.
    /// </summary>
    public float Side
    {
        get => Current?.Side ?? 0;
        set => Move(Current?.Forward ?? 0, value);
    }

    /// <summary>
    /// Units the chosen character is raised.
    /// </summary>
    public float Lift
    {
        get => Current?.Lift ?? 0;
        set
        {
            if (Edit() is { } p) p.Lift = Math.Clamp(value, LiftDown, LiftUp);
        }
    }

    /// <summary>
    /// Degrees the chosen character is turned from its own facing.
    /// </summary>
    public float TurnDegrees
    {
        get => (Current?.Turn ?? 0) * 180 / MathF.PI;
        set
        {
            if (Edit() is { } p) p.Turn = Math.Clamp(value, -180, 180) * MathF.PI / 180;
        }
    }

    /// <summary>
    /// The chosen character's weapon: 0 as the simulation has it, 1 sheathed, 2 drawn, 3 hidden (none drawn, in hand or stowed).
    /// </summary>
    public int Weapon
    {
        get => Current is { NoWeapon: true } ? 3 : Current?.Weapon switch { null => 0, false => 1, true => 2 };
        set
        {
            if (Edit() is not { } p) return;

            p.NoWeapon = value == 3;
            p.Weapon = value switch { 1 => false, 2 => true, _ => null };
        }
    }

    /// <summary>
    /// The chosen character's head turns to the camera.
    /// </summary>
    public bool Look
    {
        get => Current?.Look ?? false;
        set
        {
            if (Edit() is { } p) p.Look = value;
        }
    }

    /// <summary>
    /// The chosen character is a person (its head can turn: no animal has the <c>head turning</c> clip).
    /// </summary>
    public bool SelectedIsPerson => SelectedCharacter is { Animal: false };

    /// <summary>
    /// The chosen character is hidden.
    /// </summary>
    public bool Hidden
    {
        get => Current?.Hidden ?? false;
        set
        {
            if (Edit() is { } p) p.Hidden = value;
        }
    }

    /// <summary>
    /// The chosen character plays <paramref name="record"/> (an animation record's name; null: its own animation), from its start, held there.
    /// </summary>
    public void SetPose(string? record)
    {
        if (Edit() is not { } p) return;

        p.Pose = record;
        p.Time = 0;
        int at = record is null ? -1 : host.AnimationLibrary?.IndexOfAny(record) ?? -1;
        string clip = at >= 0 ? host.AnimationLibrary!.Definitions[at].Clip : record ?? "";
        p.Length = record is null ? 1 : MathF.Max(Lengths(SelectedCharacter)?.Of(clip) ?? 1, 1 / 30f);
    }

    /// <summary>
    /// Moves the chosen character <paramref name="forward"/> and <paramref name="side"/> units from its own place, standing on the ground there.
    /// </summary>
    void Move(float forward, float side)
    {
        if (Edit() is not { } p) return;

        (p.Forward, p.Side) = (Math.Clamp(forward, -MoveRange, MoveRange), Math.Clamp(side, -MoveRange, MoveRange));
        var at = Place(p);
        p.Ground = p.Moved ? host.Session.World.Walkability.GroundHeight(at.X, at.Z, p.Origin.Y) : p.Origin.Y;
    }

    /// <summary>
    /// Where the moves put a changed character, on the plane of its origin.
    /// </summary>
    static Vector3 Place(Posed p)
    {
        var forward = new Vector3(MathF.Sin(p.Facing), 0, MathF.Cos(p.Facing));
        var side = new Vector3(-forward.Z, 0, forward.X);
        return p.Origin + forward * p.Forward + side * p.Side;
    }

    /// <summary>
    /// Starts dragging the chosen character when the press at <paramref name="pixel"/> is on it; else chooses what is there. False when no character
    /// is there.
    /// </summary>
    public bool Press(Vector2 pixel)
    {
        if (At(pixel) is not { } id) return false;
        if (id != Selected)
        {
            Select(id);
            return true;
        }

        Dragging = true;
        return true;
    }

    /// <summary>
    /// The pointer moved while dragging: the chosen character stands where the pointer meets a floor or the terrain (within the sliders' reach).
    /// </summary>
    public void Drag(Vector2 pixel)
    {
        if (!Dragging || Edit() is not { } p || host.Player.Ray(pixel) is not { } ray) return;
        if (host.Player.Picking.MoveHit(ray.Origin, ray.Direction) is not { } hit) return;

        var forward = new Vector3(MathF.Sin(p.Facing), 0, MathF.Cos(p.Facing));
        var side = new Vector3(-forward.Z, 0, forward.X);
        var d = hit - p.Origin;
        (p.Forward, p.Side) = (Math.Clamp(Vector3.Dot(d, forward), -MoveRange, MoveRange), Math.Clamp(Vector3.Dot(d, side), -MoveRange, MoveRange));
        p.Ground = Vector3.DistanceSquared(Place(p) with { Y = hit.Y }, hit) < 1 ? hit.Y
            : host.Session.World.Walkability.GroundHeight(Place(p).X, Place(p).Z, hit.Y);
    }

    /// <summary>
    /// The drag ended.
    /// </summary>
    public void Release() => Dragging = false;

    /// <summary>
    /// How high above its feet the gizmo stands on a character (units: 0.9 m), at scale 1.
    /// </summary>
    const float GizmoHeight = 9;

    /// <summary>
    /// The transform gizmo's subject: the chosen character (null when none is, it is hidden or carried). The arrows drive Forward, Sideways and Height
    /// (so the sliders follow and keep their bounds), the ring Turn.
    /// </summary>
    public GizmoSubject? GizmoSubject()
    {
        if (SelectedCharacter is not { Carry: null } c || Current is { Hidden: true }) return null;

        float lift = GizmoHeight * c.Scale;
        var feet = Current is { } p ? (p.Moved ? Place(p) with { Y = p.Ground } : c.Position) + Vector3.UnitY * p.Lift : c.Position;
        return new GizmoSubject(feet + Vector3.UnitY * lift, world => GizmoMove(world, lift), () => Current?.Turn ?? 0, SetTurn);
    }

    /// <summary>
    /// Puts the chosen character's gizmo point at <paramref name="world"/>: the horizontal part is Forward and Sideways, the height Height.
    /// </summary>
    void GizmoMove(Vector3 world, float lift)
    {
        if (Edit() is not { } p) return;

        var forward = new Vector3(MathF.Sin(p.Facing), 0, MathF.Cos(p.Facing));
        var side = new Vector3(-forward.Z, 0, forward.X);
        var d = world - p.Origin;
        Move(Vector3.Dot(d, forward), Vector3.Dot(d, side));
        p.Lift = Math.Clamp(world.Y - lift - p.Ground, LiftDown, LiftUp);
    }

    /// <summary>
    /// The chosen character's turn from its own facing, in radians brought to -pi..pi.
    /// </summary>
    void SetTurn(float radians)
    {
        if (Edit() is not { } p) return;

        while (radians > MathF.PI) radians -= MathF.Tau;
        while (radians < -MathF.PI) radians += MathF.Tau;
        p.Turn = radians;
    }

    /// <summary>
    /// The chosen character drawn as the simulation has it again.
    /// </summary>
    public void ResetSelected()
    {
        if (Selected is { } id) _posed.Remove(id);
    }

    /// <summary>
    /// Every character drawn as the simulation has it, none chosen, the others shown: on entering and leaving.
    /// </summary>
    public void Clear()
    {
        _posed.Clear();
        (Selected, OthersHidden, Dragging, _poseNames, ListOpen, Filter) = (null, false, false, null, false, "");
        (Highlight, Scroll) = (0, 0);
    }

    /// <summary>
    /// Moves the playing poses on by <paramref name="seconds"/> of real time (the simulation may stand paused), looping.
    /// </summary>
    public void Advance(float seconds)
    {
        foreach (var p in _posed.Values)
            if (p is { Pose: not null, Playing: true }) p.Time = ((p.Time + seconds * p.Speed) % p.Length + p.Length) % p.Length;
    }

    #endregion

    #region Drawing

    /// <summary>
    /// Character <paramref name="c"/> as photo mode draws it: its place, facing, poses and weapon changed, or hidden; false when it is drawn as the
    /// simulation has it (<paramref name="noWeapon"/>: no weapon drawn at all). A carried body keeps its place on its carrier.
    /// </summary>
    public bool Drawn(CharacterSnapshot c, ref Vector3 position, ref float yaw, ref CharacterPose[] poses, ref bool hidden, out PosedLook look)
    {
        look = default;
        if (!_posed.TryGetValue(c.Id, out var p))
        {
            if (!OthersHidden || c.Id == Selected) return false;

            hidden = true;
            return true;
        }

        hidden |= p.Hidden;
        look = new PosedLook(p.Weapon, p.NoWeapon, 0);
        if (p.Pose is { } pose) poses = [new CharacterPose(pose, p.Time, 1)];
        if (c.Carry is not null) return true;

        if (p.Moved) position = Place(p) with { Y = p.Ground };
        position.Y += p.Lift;
        yaw += p.Turn;
        if (!p.Look || c.Animal) return true;

        poses = LookingAt(poses, host.Camera.Eye - position - Vector3.UnitY * HeadHeight * c.Scale, yaw, out float pitch);
        look = look with { HeadPitch = pitch };
        return true;
    }

    /// <summary>
    /// What photo mode changes of a character's drawing besides its place, facing and poses: the weapon in hand (null as the simulation has it, else
    /// drawn or sheathed), no weapon drawn at all, and the head's pitch in radians (positive looks up).
    /// </summary>
    public record struct PosedLook(bool? Weapon, bool NoWeapon, float HeadPitch);

    /// <summary>
    /// How high a head's eyes are above a character's feet (units: 1.6 m), at scale 1.
    /// </summary>
    const float HeadHeight = 16;

    /// <summary>
    /// The farthest a head is pitched up and down (radians: 35 degrees up, 40 down), a natural range for the neck and head together.
    /// </summary>
    const float PitchUp = 35 * MathF.PI / 180, PitchDown = 40 * MathF.PI / 180;

    /// <summary>
    /// The ANIMATION record the game turns a head with (docs/animation.md "Head turning").
    /// </summary>
    public const string HeadTurning = "head turning";

    /// <summary>
    /// The clip time of <see cref="HeadTurning"/> at +90 degrees (the game's constant, docs/animation.md "Head turning").
    /// </summary>
    const float HeadTimeSpan = 0.07f;

    /// <summary>
    /// The last clip time drawn: just short of the clip's 0.0667 s (the renderer wraps a time at the length round to the start).
    /// </summary>
    const float HeadLastTime = 0.0665f;

    /// <summary>
    /// <paramref name="poses"/> with the head turned the way <paramref name="way"/> (from the body to the camera) from a body facing
    /// <paramref name="yaw"/>, as the game turns a head toward what its character looks at: the <see cref="HeadTurning"/> layer at the time the
    /// horizontal angle maps to, within 90 degrees of the facing (behind that the head stays as it is), and <paramref name="pitch"/> the vertical angle
    /// (clamped to <see cref="PitchUp"/> and <see cref="PitchDown"/>). Any head layer already there is replaced,
    /// so the head is never turned twice.
    /// </summary>
    static CharacterPose[] LookingAt(CharacterPose[] poses, Vector3 way, float yaw, out float pitch)
    {
        pitch = 0;
        var flat = new Vector2(way.X, way.Z);
        if (flat.LengthSquared() < 1e-6f) return poses;

        float angle = SignedAngle(yaw, Vector2.Normalize(flat));
        if (MathF.Abs(angle) > MathF.PI / 2) return poses;

        // The clip only turns sideways: the way up or down is a pitch of the neck and head bones (the renderer's, split between them).
        pitch = Math.Clamp(MathF.Atan2(way.Y, flat.Length()), -PitchDown, PitchUp);
        float time = Math.Clamp((angle + MathF.PI / 2) / MathF.PI * HeadTimeSpan, 0, HeadLastTime);
        return [.. poses.Where(p => p.Animation != HeadTurning), new CharacterPose(HeadTurning, time, 1)];
    }

    /// <summary>
    /// The game's head angle: the angle between the facing (sin yaw, cos yaw) and the way to the point, negative when the point is on the side where
    /// facing x way has a positive Y (docs/animation.md "Head turning").
    /// </summary>
    static float SignedAngle(float yaw, Vector2 way)
    {
        var facing = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
        float angle = MathF.Acos(Math.Clamp(Vector2.Dot(facing, way), -1, 1));
        return facing.Y * way.X - facing.X * way.Y > 0 ? -angle : angle;
    }

    #endregion

    #region Pose list

    /// <summary>
    /// The pose records the chosen character's skeleton has, by name (made when the choice changes).
    /// </summary>
    string[]? _poseNames;

    /// <summary>
    /// The skeleton clip lengths of <paramref name="c"/>'s body: an animal's species' own, a person's with its linked animation files.
    /// </summary>
    AnimationLengths? Lengths(CharacterSnapshot? c)
    {
        if (host.AnimationLengths is not { } lengths) return null;

        return c is { Animal: true } ? lengths.For(c.Appearance?.Race.StringId) : lengths.ForPerson(c?.Appearance);
    }

    /// <summary>
    /// The animation records the chosen character can be posed in, by name: the animals' for an animal and the people's for a person, those whose clip
    /// its skeleton has (all of its kind when the lengths are not known).
    /// </summary>
    public IReadOnlyList<string> PoseNames()
    {
        if (_poseNames is not null) return _poseNames;
        if (host.AnimationLibrary is not { } library || SelectedCharacter is not { } c) return [];

        var lengths = Lengths(c);
        var kind = library.Definitions.Where(d => (d.Category == AnimationLibrary.AnimalCategory) == c.Animal).ToList();
        var known = kind.Where(d => lengths is null || lengths.Has(d.Clip)).ToList();
        return _poseNames = [.. (known.Count > 0 ? known : kind)
            .Select(d => d.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The list's first entry: the character's own animation.
    /// </summary>
    public const string OwnAnimation = "own animation";

    /// <summary>
    /// The rows the open list shows at once.
    /// </summary>
    public const int ListRows = 12;

    /// <summary>
    /// The pose list is open (the panel draws it over the bar; typed letters filter it and no key reaches the camera).
    /// </summary>
    public bool ListOpen { get; private set; }

    /// <summary>
    /// The typed filter: letters a pose's name contains, case ignored (kept while the mode is on, across openings and characters).
    /// </summary>
    public string Filter { get; private set; } = "";

    /// <summary>
    /// The entries passing the filter (<see cref="OwnAnimation"/> first, always).
    /// </summary>
    public List<string> Shown { get; private set; } = [];

    /// <summary>
    /// The highlighted entry (Up / Down, Enter) and the first row shown.
    /// </summary>
    public int Highlight { get; private set; }

    /// <summary>
    /// The first row shown.
    /// </summary>
    public int Scroll { get; private set; }

    /// <summary>
    /// Opens the list with the filter, highlight and scroll it was closed with (kept while the mode is on); the chosen character's own pose is
    /// highlighted when the filter shows it. Closes it when open.
    /// </summary>
    public void ToggleList()
    {
        if (ListOpen || Selected is null)
        {
            ListOpen = false;
            return;
        }

        ListOpen = true;
        var (highlight, scroll) = (Highlight, Scroll);
        Refilter();
        (Highlight, Scroll) = (highlight, scroll);
        if (Pose is not null && Shown.IndexOf(Pose) is var at and >= 0) Highlight = at;
        Keep();
    }

    /// <summary>
    /// The entry <paramref name="index"/> of <see cref="Shown"/> becomes the chosen character's pose; the list closes.
    /// </summary>
    public void Pick(int index)
    {
        ListOpen = false;
        if (index < 0 || index >= Shown.Count) return;

        SetPose(Shown[index] == OwnAnimation ? null : Shown[index]);
    }

    /// <summary>
    /// A typed character extends the filter.
    /// </summary>
    public void Type(char c)
    {
        if (char.IsControl(c)) return;

        Filter += c;
        Refilter();
    }

    /// <summary>
    /// A key while the list is open: Esc closes it, Enter picks the highlighted entry, Up / Down / PageUp / PageDown move, Backspace edits the
    /// filter.
    /// </summary>
    public void ListKey(Input.Key key, bool escape)
    {
        if (escape || key == Input.Key.Escape)
        {
            ListOpen = false;
            return;
        }

        switch (key)
        {
            case Input.Key.Enter:
                Pick(Highlight);
                return;
            case Input.Key.Up:
                Highlight--;
                break;
            case Input.Key.Down:
                Highlight++;
                break;
            case Input.Key.PageUp:
                Highlight -= ListRows;
                break;
            case Input.Key.PageDown:
                Highlight += ListRows;
                break;
            case Input.Key.Backspace when Filter.Length > 0:
                Filter = Filter[..^1];
                Refilter();
                return;
        }

        Keep();
    }

    /// <summary>
    /// The wheel over the open list moves it by three rows a notch (away from the user goes up).
    /// </summary>
    public void ListWheel(float notches)
    {
        Highlight -= (int)MathF.Round(notches) * 3;
        Keep();
    }

    /// <summary>
    /// The filter applied afresh, the highlight back at the top.
    /// </summary>
    void Refilter()
    {
        Shown = [OwnAnimation, .. PoseNames().Where(n => n.Contains(Filter, StringComparison.OrdinalIgnoreCase))];
        (Highlight, Scroll) = (0, 0);
    }

    /// <summary>
    /// Keeps the highlight on the list and in view.
    /// </summary>
    void Keep()
    {
        Highlight = Math.Clamp(Highlight, 0, Math.Max(0, Shown.Count - 1));
        Scroll = Math.Clamp(Scroll, Math.Max(0, Highlight - ListRows + 1), Math.Min(Highlight, Math.Max(0, Shown.Count - ListRows)));
    }

    #endregion
}

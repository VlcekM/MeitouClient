using System.Numerics;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Title;

/// <summary>
/// One skeleton animation of the wanderer's flight: its name, its rate (clip seconds per world unit moved, a move clip's play speed; for a clip that
/// is not a move, clip seconds per second) and its length in seconds.
/// </summary>
readonly record struct FlightClip(string Clip, float Rate, float Length)
{
    /// <summary>
    /// The pose of a move clip, looping, after moving <paramref name="moved"/> world units.
    /// </summary>
    public CharacterPose Looped(float moved) => new(Clip, Length > 0 ? moved * Rate % Length : 0, 1);

    /// <summary>
    /// The pose <paramref name="seconds"/> into a clip played once, held on its last frame.
    /// </summary>
    public CharacterPose Once(float seconds) => new(Clip, MathF.Min(seconds * Rate, MathF.Max(Length - 0.01f, 0)), 1);

    /// <summary>
    /// The seconds the clip takes played once.
    /// </summary>
    public float Seconds => Rate > 0 ? Length / Rate : 0;
}

/// <summary>
/// The skeleton animations of the wanderer's flight from the title's Beak Thing (<see cref="TitleFlight"/>): the start, the walk of the turn and the
/// run, each in parts (legs and arms) or none, and the start clip's own travel across the ground (its <c>Bip01</c> track in model X and Z, Z forward
/// and X to his left, by clip seconds; null: none).
/// </summary>
sealed record FlightClips(IReadOnlyList<FlightClip> Startle, IReadOnlyList<FlightClip> Walk, IReadOnlyList<FlightClip> Run,
    Func<float, Vector2>? StartleTravel = null)
{
    /// <summary>
    /// No clips: the wanderer keeps his idle and only moves.
    /// </summary>
    public static FlightClips None { get; } = new([], [], []);

    /// <summary>
    /// How long the startle lasts: the whole of the start clip; 0 without one.
    /// </summary>
    public float StartleSeconds => Startle.Count > 0 ? Startle.Max(c => c.Seconds) : 0;
}

/// <summary>
/// The wanderer's flight from the title's Beak Thing (<see cref="TitleAmbush"/>), display only, from when it starts its walk at him: he staggers
/// back, startled, still facing it, for the whole of the start clip and as far as the clip itself steps; a moment after it ends he turns round in a
/// small walking circle, so his turn shows; then runs at the camera and out of the frame.
/// </summary>
/// <param name="Start">Where he stands.</param>
/// <param name="Yaw">His facing as he watches (out over the valley; the characters' yaw: 0 faces +Z).</param>
/// <param name="Eye">The camera's eye: he runs under it.</param>
/// <param name="Clips">His clips.</param>
sealed record TitleFlight(Vector3 Start, float Yaw, Vector3 Eye, FlightClips Clips)
{
    /// <summary>
    /// How long the startled stagger lasts without a start clip.
    /// </summary>
    public const float DefaultStaggerSeconds = 1.1f;

    /// <summary>
    /// How far back towards the camera the startled stagger carries him without a start clip (world units).
    /// </summary>
    public const float StaggerDistance = 7;

    /// <summary>
    /// The farthest the start clip's own step carries him (world units): the dodge back steps about 17, which brings him up against the camera
    /// close behind him, so its step is scaled down to this, keeping its pace.
    /// </summary>
    public const float StartleStepMax = 4;

    /// <summary>
    /// The moment he holds the stagger's last frame before he turns, so the clip is seen to finish.
    /// </summary>
    public const float HoldSeconds = 0.15f;

    /// <summary>
    /// How long the turn's walking circle lasts.
    /// </summary>
    public const float TurnSeconds = 1.3f;

    /// <summary>
    /// The radius of the turn's walking circle (world units).
    /// </summary>
    public const float TurnRadius = 5;

    /// <summary>
    /// His running speed (world units/s).
    /// </summary>
    public const float RunSpeed = 70;

    /// <summary>
    /// The longest he is followed: by then he is far out of the frame.
    /// </summary>
    const float LastSeconds = 30;

    /// <summary>
    /// The idle he watches in, and holds without clips.
    /// </summary>
    static readonly CharacterPose Watching = new("idle_stand_confident", 0.5f, 1);

    /// <summary>
    /// How long the stagger lasts: the whole of the start clip, so he moves on only once it has played out.
    /// </summary>
    public float StaggerSeconds => Clips.StartleSeconds > 0 ? Clips.StartleSeconds : DefaultStaggerSeconds;

    /// <summary>
    /// When he starts his turn: just after the stagger has played out.
    /// </summary>
    public float TurnStart => StaggerSeconds + HoldSeconds;

    /// <summary>
    /// The way back from where he stands: towards the camera, across the ground.
    /// </summary>
    Vector2 Back => Vector2.Normalize(new Vector2(Eye.X - Start.X, Eye.Z - Start.Z) + new Vector2(1e-4f, 0));

    /// <summary>
    /// The turn's whole angle: from his watching facing round to the way back from where the stagger left him (signed: positive turns to
    /// increasing yaw).
    /// </summary>
    float TurnAngle
    {
        get
        {
            var back = new Vector2(Eye.X - Staggered.X, Eye.Z - Staggered.Z);
            return MathF.IEEERemainder(MathF.Atan2(back.X, back.Y) - Yaw, MathF.Tau);
        }
    }

    /// <summary>
    /// Where he is, his facing and his poses <paramref name="seconds"/> into the flight.
    /// </summary>
    public (Vector3 At, float Yaw, CharacterPose[] Poses) At(float seconds)
    {
        seconds = Math.Clamp(seconds, 0, LastSeconds);
        if (seconds < TurnStart) return Stagger(MathF.Min(seconds, StaggerSeconds));
        if (seconds < TurnStart + TurnSeconds) return Turn(seconds - TurnStart);

        return Run(seconds - TurnStart - TurnSeconds);
    }

    /// <summary>
    /// The stagger <paramref name="seconds"/> in: still facing out, carried back as the start clip steps, playing it once and holding its last frame.
    /// </summary>
    (Vector3, float, CharacterPose[]) Stagger(float seconds)
    {
        var poses = Clips.Startle.Count > 0 ? Clips.Startle.Select(c => c.Once(seconds)).ToArray() : [Watching];

        return (Start + Travel(seconds), Yaw, poses);
    }

    /// <summary>
    /// How far the stagger has carried him <paramref name="seconds"/> in: the start clip's own step turned by his facing (at most
    /// <see cref="StartleStepMax"/>), else a step back towards the camera, fast then slowing.
    /// </summary>
    Vector3 Travel(float seconds)
    {
        if (Clips.StartleTravel is { } clip)
        {
            float whole = (clip(StaggerSeconds) - clip(0)).Length();
            var model = (clip(seconds) - clip(0)) * (whole > StartleStepMax ? StartleStepMax / whole : 1);
            var forward = new Vector2(MathF.Sin(Yaw), MathF.Cos(Yaw));
            var left = new Vector2(MathF.Cos(Yaw), -MathF.Sin(Yaw));
            var world = left * model.X + forward * model.Y;
            return new Vector3(world.X, 0, world.Y);
        }

        float u = Math.Clamp(seconds / StaggerSeconds, 0, 1);
        return new Vector3(Back.X, 0, Back.Y) * (StaggerDistance * (1 - (1 - u) * (1 - u)));
    }

    /// <summary>
    /// Where the stagger left him.
    /// </summary>
    Vector3 Staggered => Start + Travel(StaggerSeconds);

    /// <summary>
    /// The turn <paramref name="seconds"/> in: walking forward round a circle of <see cref="TurnRadius"/>, his facing along it, from his watching
    /// facing to the way back.
    /// </summary>
    (Vector3, float, CharacterPose[]) Turn(float seconds)
    {
        float turned = TurnAngle * (seconds / TurnSeconds);
        var (at, yaw) = OnCircle(turned);
        float walked = MathF.Abs(turned) * TurnRadius;
        var poses = Clips.Walk.Count > 0 ? Clips.Walk.Select(c => c.Looped(walked)).ToArray() : [Watching];

        return (at, yaw, poses);
    }

    /// <summary>
    /// The place and facing after turning <paramref name="turned"/> round the circle: moving along the facing <c>(sin yaw, cos yaw)</c> while it
    /// turns.
    /// </summary>
    (Vector3 At, float Yaw) OnCircle(float turned)
    {
        float s = MathF.Sign(turned) is 0 ? 1 : MathF.Sign(turned);
        float yaw = Yaw + turned;
        float x = TurnRadius * s * (MathF.Cos(Yaw) - MathF.Cos(yaw));
        float z = TurnRadius * s * (MathF.Sin(yaw) - MathF.Sin(Yaw));

        return (Staggered + new Vector3(x, 0, z), yaw);
    }

    /// <summary>
    /// The run <paramref name="seconds"/> in: from the end of the circle, straight at the camera.
    /// </summary>
    (Vector3, float, CharacterPose[]) Run(float seconds)
    {
        var (from, _) = OnCircle(TurnAngle);
        var dir = Vector2.Normalize(new Vector2(Eye.X - from.X, Eye.Z - from.Z) + new Vector2(1e-4f, 0));
        float run = RunSpeed * seconds;
        var poses = Clips.Run.Count > 0 ? Clips.Run.Select(c => c.Looped(run)).ToArray() : [Watching];

        return (from + new Vector3(dir.X, 0, dir.Y) * run, MathF.Atan2(dir.X, dir.Y), poses);
    }
}

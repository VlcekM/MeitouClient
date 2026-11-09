using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// One animation to pose a character with: the skeleton animation's name, its time in seconds and its blend weight.
/// </summary>
public readonly record struct CharacterPose(string Animation, float Time, float Weight);

/// <summary>
/// A character to draw this frame, already interpolated by the host. <see cref="Key"/> is stable while the character exists, so
/// the renderer can keep its GPU resources and last frame's bones (motion vectors) by it; <see cref="Appearance"/> is compared by
/// reference to find the meshes and materials.
/// </summary>
public readonly record struct CharacterInstance(long Key, CharacterAppearance Appearance, Vector3 Position, float Yaw,
    IReadOnlyList<CharacterPose> Poses)
{
    /// <summary>
    /// Show the weapon in hand (the parts tagged <see cref="WeaponShown.Drawn"/>) instead of sheathed.
    /// </summary>
    public bool WeaponDrawn { get; init; }

    /// <summary>
    /// The weapon in hand is the crossbow: its in-hand parts (<see cref="WeaponShown.RangedDrawn"/>) show, the melee weapon's sheathed ones.
    /// </summary>
    public bool RangedDrawn { get; init; }

    /// <summary>
    /// The melee weapon in hand is the back one: its drawn parts (<see cref="WeaponShown.BackDrawn"/>) show, the hip one's sheathed.
    /// </summary>
    public bool BackDrawn { get; init; }

    /// <summary>
    /// The bolts loaded in the crossbow: which of its loaded / unloaded strings and bolts show (<see cref="CharacterPart.ShownShots"/>).
    /// </summary>
    public int Shots { get; init; }

    /// <summary>
    /// The bolts stuck in the character (<see cref="Characters.CharacterRenderer.CollectBolts"/>).
    /// </summary>
    public IReadOnlyList<StuckBoltMesh> Bolts { get; init; } = [];

    /// <summary>
    /// The limb an animal holds at its <c>mouth</c> point (<see cref="Characters.CharacterRenderer.CollectBolts"/>); null: none.
    /// </summary>
    public MouthMesh? Mouth { get; init; }

    /// <summary>
    /// The node's whole rotation, in place of <see cref="Yaw"/> (a carried body's on its carrier's shoulder); null: turned by the yaw only.
    /// </summary>
    public Quaternion? Rotation { get; init; }

    /// <summary>
    /// Uniform size the body is drawn at (an animal's age-dependent size; 1 for people).
    /// </summary>
    public float Scale { get; init; } = 1;

    /// <summary>
    /// The factor the draw distance is multiplied by for this body (7 for a gigantic race, <c>FUN_140539020</c>); 1 for the rest.
    /// </summary>
    public float DrawDistanceScale { get; init; } = 1;

    /// <summary>
    /// Not drawn in the world (still posed for its portrait): on a floor of a building that the interior view hides (docs/formats/zones.md "Interior
    /// view").
    /// </summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// Pose every frame regardless of distance, for presentation actors whose animation must stay smooth.
    /// </summary>
    public bool FullRateAnimation { get; init; }

    /// <summary>
    /// The ragdoll's bones in model space, set over the animation (null: none); the host interpolates them between ticks.
    /// </summary>
    public BonePose? Bones { get; init; }

    /// <summary>
    /// The character editor's glide between two looks: the look being left (null: none) and how far the way to <see cref="Appearance"/> is, 0..1.
    /// Only the body shape, the feet's lift, the posture sliders, the face and body poses and the skin and hair colours are mixed; looks that differ
    /// in other ways switch at once.
    /// </summary>
    public CharacterAppearance? BlendFrom { get; init; }

    /// <summary>
    /// How far the glide from <see cref="BlendFrom"/> to <see cref="Appearance"/> is, 0..1.
    /// </summary>
    public float Blend { get; init; }

    /// <summary>
    /// The stat-driven body (bone bulk, the body normal blend); null keeps the appearance's own.
    /// </summary>
    public BodyBuild? Build { get; init; }

    /// <summary>
    /// The blood each of the first 8 body parts shows, a byte each (<c>CharacterSnapshot.BloodAmounts</c>).
    /// </summary>
    public ulong Blood { get; init; }

    /// <summary>
    /// The whole placement, in place of <see cref="Position"/>, <see cref="Yaw"/> and <see cref="Rotation"/> (a falling head's); <see
    /// cref="Position"/> still places it for culling.
    /// </summary>
    public Matrix4x4? World { get; init; }

    /// <summary>
    /// Holds the bones of the character with this key as it was last posed, taken the first time this one is drawn and never posed again (a cut-off
    /// head keeps the pose of the moment it was cut); <see cref="Poses"/> are then ignored.
    /// </summary>
    public long? FreezeFrom { get; init; }
}

/// <summary>
/// The characters of one frame. The renderers know nothing of the simulation (docs/engine.md): the host fills this list from the
/// simulation's snapshots each frame, as it copies the camera into <see cref="WorldCamera"/>; those beyond reach only when the snapshot changes (<see
/// cref="Distant"/>).
/// </summary>
public sealed class CharacterDrawList
{
    /// <summary>
    /// The characters that may be drawn or posed this frame: emptied by the renderer (<see cref="Clear"/>) and filled again each frame.
    /// </summary>
    public List<CharacterInstance> Items { get; } = [];

    /// <summary>
    /// The characters beyond the renderer's reach (<see cref="Characters.CharacterRenderer.InReach"/>), with only their look, place and what loading
    /// reads: kept across frames and filled again by the host when its simulation snapshot changes (<see cref="BeginDistant"/>), as nothing in them
    /// moves between ticks. Read only to build looks ahead and to keep the textures round kept points; a character that came within reach since is in
    /// <see cref="Items"/> as well.
    /// </summary>
    public List<CharacterInstance> Distant { get; } = [];

    /// <summary>
    /// Counts the refills of <see cref="Distant"/>.
    /// </summary>
    public int DistantVersion { get; private set; }

    /// <summary>
    /// Empties <see cref="Items"/> for the next frame.
    /// </summary>
    public void Clear() => Items.Clear();

    /// <summary>
    /// Adds a character to <see cref="Items"/>.
    /// </summary>
    public void Add(in CharacterInstance instance) => Items.Add(instance);

    /// <summary>
    /// Empties <see cref="Distant"/> to be filled again.
    /// </summary>
    public void BeginDistant()
    {
        Distant.Clear();
        DistantVersion++;
    }
}

/// <summary>
/// A bolt stuck in a character: its mesh, the bone it is on and its flight direction in the character's frame (its yaw taken off) when it struck.
/// </summary>
public readonly record struct StuckBoltMesh(int Id, string Mesh, string Bone, Vector3 Direction);

/// <summary>
/// A limb held in an animal's mouth: its mesh and its severed-limb look (null: drawn as a plain loose mesh).
/// </summary>
public sealed record MouthMesh(string Mesh, LimbLook? Look);
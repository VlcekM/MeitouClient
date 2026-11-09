namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// How far a turret's turning parts have turned (<see cref="JointPoses.Turrets"/>): <see cref="PartIds"/> are the BUILDING_PART records of
/// its joints (<see cref="Data.World.TurnJoint.PartId"/>), <see cref="Angles"/> the turn of each in radians (the simulation's
/// <c>TurretRig</c> angles, same order). The host updates the angles in place and raises <see cref="Version"/> when they change.
/// </summary>
public sealed class TurretPose(string[] partIds)
{
    /// <summary>
    /// The BUILDING_PART records of the turret's joints.
    /// </summary>
    public readonly string[] PartIds = partIds;

    /// <summary>
    /// The turn of each joint of <see cref="PartIds"/> in radians.
    /// </summary>
    public readonly float[] Angles = new float[partIds.Length];

    /// <summary>
    /// Starts at 1; instances posed at another version are posed again.
    /// </summary>
    public int Version = 1;

    /// <summary>
    /// The turn of the joint of part <paramref name="partId"/>; 0 for a part this turret does not turn.
    /// </summary>
    public float AngleOf(string partId)
    {
        int k = Array.IndexOf(PartIds, partId);
        return k < 0 ? 0 : Angles[k];
    }
}

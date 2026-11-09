using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Data.Fcs;
using Meitou.Simulation.Ragdolls;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A whole-body ragdoll as saved: settled (<see cref="Frozen"/>) or not, its age in seconds and every body's world pose in the ragdoll definition's order.
/// </summary>
public sealed record SavedRagdoll(bool Frozen, float Age, RigidTransform[] Pose);

/// <summary>
/// A knocked-down or dead character's ragdoll in a save (docs/game/ragdolls.md "Saves"): Meitou's own GAMESTATE_CHARACTER string <c>meitou ragdoll</c>,
/// "frozen age count" then position and rotation (x y z, x y z w) per body, invariant floats. Only a whole-body ragdoll is written (carried and limb ragdolls
/// follow the animation and are made again); the original saves none and ignores the key. Read once, when the character is made from the save; the ragdoll
/// system takes it the first time it sees the character.
/// </summary>
public static class RagdollSave
{
    public const string Key = "meitou ragdoll";

    public static void Write(FcsRecord state, SavedRagdoll? ragdoll)
    {
        if (ragdoll is null)
        {
            state.Strings.Remove(Key);
            return;
        }

        var s = new StringBuilder();
        s.Append(ragdoll.Frozen ? '1' : '0').Append(' ').Append(F(ragdoll.Age)).Append(' ').Append(ragdoll.Pose.Length);
        foreach (var p in ragdoll.Pose)
        {
            foreach (float v in (ReadOnlySpan<float>)[p.Position.X, p.Position.Y, p.Position.Z, p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W])
                s.Append(' ').Append(F(v));
        }

        state.Strings[Key] = s.ToString();
    }

    /// <summary>
    /// The saved ragdoll; null when there is none or the text does not parse.
    /// </summary>
    public static SavedRagdoll? Read(FcsRecord? state)
    {
        if (state is null || !state.Strings.TryGetValue(Key, out var text)) return null;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0 || parts.Length != 3 + 7 * n
            || !TryF(parts[1], out float age)) return null;

        var pose = new RigidTransform[n];
        Span<float> v = stackalloc float[7];
        for (int b = 0; b < n; b++)
        {
            for (int k = 0; k < 7; k++)
                if (!TryF(parts[3 + 7 * b + k], out v[k])) return null;

            pose[b] = new RigidTransform(new Vector3(v[0], v[1], v[2]), Quaternion.Normalize(new Quaternion(v[3], v[4], v[5], v[6])));
        }

        return new SavedRagdoll(parts[0] == "1", age, pose);
    }

    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    static bool TryF(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && float.IsFinite(v);
}

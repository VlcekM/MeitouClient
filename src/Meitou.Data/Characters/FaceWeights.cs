namespace Meitou.Data.Characters;

/// <summary>
/// The shape-key weights one character is drawn with this frame on top of its appearance's own pose weights (docs/face-shape-keys.md): a small list of
/// (pose name, weight 0..1) that a face controller fills and the renderer applies to the body mesh's poses. A key set here replaces the appearance's
/// weight for that pose (so 0 switches a slider's pose off); poses not set keep the appearance's. Weights are rounded to 1/128, so a value that barely
/// moves does not rebuild the character's morph. Reused from frame to frame by the host: <see cref="Clear"/>, then <see cref="Set"/>.
/// </summary>
public sealed class FaceWeights
{
    public const float Resolution = 128;

    string[] _keys = new string[8];
    float[] _values = new float[8];

    /// <summary>The number of keys set.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// The pose names of the character's face mesh (its body mesh's shape keys), set by the host before the controllers run; a key not in it has no effect.
    /// Null when the look has no poses or is not built yet.
    /// </summary>
    public IReadOnlySet<string>? Available { get; set; }

    public string KeyAt(int index) => _keys[index];
    public float WeightAt(int index) => _values[index];

    public void Clear() => Count = 0;

    /// <summary>The weight set for <paramref name="key"/> this frame; false when none.</summary>
    public bool TryGet(string key, out float weight)
    {
        for (int i = 0; i < Count; i++)
            if (string.Equals(_keys[i], key, StringComparison.Ordinal)) { weight = _values[i]; return true; }
        weight = 0;
        return false;
    }

    /// <summary>Sets <paramref name="key"/> to <paramref name="weight"/> (clamped to 0..1, rounded to 1/128), replacing an earlier value of the frame.</summary>
    public void Set(string key, float weight)
    {
        float w = MathF.Round(Math.Clamp(float.IsFinite(weight) ? weight : 0, 0, 1) * Resolution) / Resolution;
        for (int i = 0; i < Count; i++)
            if (string.Equals(_keys[i], key, StringComparison.Ordinal)) { _values[i] = w; return; }
        if (Count == _keys.Length)
        {
            Array.Resize(ref _keys, Count * 2);
            Array.Resize(ref _values, Count * 2);
        }
        _keys[Count] = key;
        _values[Count++] = w;
    }

    /// <summary>Whether the arrays (the last <see cref="CopyTo"/>) hold the same keys with the same weights in the same order.</summary>
    public bool SameAs(string[] otherKeys, float[] otherValues, int otherCount)
    {
        if (otherCount != Count) return false;
        for (int i = 0; i < Count; i++)
        {
            if (!ReferenceEquals(_keys[i], otherKeys[i]) && _keys[i] != otherKeys[i]) return false;
            if (_values[i] != otherValues[i]) return false;
        }
        return true;
    }

    /// <summary>Copies the current keys and weights into the arrays (grown when needed) and returns their count; for the renderer's "unchanged" test.</summary>
    public int CopyTo(ref string[] otherKeys, ref float[] otherValues)
    {
        if (otherKeys.Length < Count) { otherKeys = new string[_keys.Length]; otherValues = new float[_keys.Length]; }
        Array.Copy(_keys, otherKeys, Count);
        Array.Copy(_values, otherValues, Count);
        return Count;
    }
}

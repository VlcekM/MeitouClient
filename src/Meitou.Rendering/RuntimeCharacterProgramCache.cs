using Meitou.Data.Characters;

namespace Meitou.Rendering;

/// <summary>Render-thread identity cache. The caller supplies the backend's fence-aware retirement callback.</summary>
internal sealed class RuntimeCharacterProgramCache<T>(Func<RuntimeCharacterShaderSet, T> create, Action<T> retire) where T : class
{
    readonly Dictionary<RuntimeCharacterShaderSet, T> entries = new(ReferenceEqualityComparer.Instance);
    public IEnumerable<T> Values => entries.Values;
    public bool Contains(RuntimeCharacterShaderSet source) => entries.ContainsKey(source);
    public T Get(RuntimeCharacterShaderSet source)
    {
        if (!entries.TryGetValue(source, out var value)) entries.Add(source, value = create(source));
        return value;
    }
    public bool Retain(ISet<RuntimeCharacterShaderSet> live)
    {
        bool changed = false;
        foreach (var key in entries.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            var value = entries[key];
            entries.Remove(key);
            retire(value);
            changed = true;
        }
        return changed;
    }
    public void Clear()
    {
        foreach (var value in entries.Values) retire(value);
        entries.Clear();
    }
}

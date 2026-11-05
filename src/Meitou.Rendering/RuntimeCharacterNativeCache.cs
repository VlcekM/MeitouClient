using System.Runtime.CompilerServices;

namespace Meitou.Rendering;

/// <summary>Render-thread input-layout metadata only. Eviction drops references; submitted draws own their binding snapshots and GPU resources retire elsewhere.</summary>
internal sealed class RuntimeCharacterNativeCache<TPart,TProgram,TValue>
    where TPart : class where TProgram : class where TValue : class
{
    sealed class Identity : IEqualityComparer<(TPart Part,TProgram Program)>
    {
        public bool Equals((TPart Part,TProgram Program) a,(TPart Part,TProgram Program) b)=>ReferenceEquals(a.Part,b.Part)&&ReferenceEquals(a.Program,b.Program);
        public int GetHashCode((TPart Part,TProgram Program) key)=>HashCode.Combine(RuntimeHelpers.GetHashCode(key.Part),RuntimeHelpers.GetHashCode(key.Program));
    }
    readonly Dictionary<(TPart Part,TProgram Program),TValue> entries=new(new Identity());
    public int Count=>entries.Count;
    public TValue Get(TPart part,TProgram program,Func<TValue> create)
    {
        if(!entries.TryGetValue((part,program),out var value)) entries.Add((part,program),value=create());
        return value;
    }
    public bool RetainParts(ISet<TPart> live)=>Remove(key=>!live.Contains(key.Part));
    public bool RetainPrograms(ISet<TProgram> live)=>Remove(key=>!live.Contains(key.Program));
    bool Remove(Func<(TPart Part,TProgram Program),bool> remove)
    {
        bool changed=false;
        foreach(var key in entries.Keys.Where(remove).ToArray()) { entries.Remove(key); changed=true; }
        return changed;
    }
    public void Clear()=>entries.Clear();
}

// Port of upstream/ProvingGrounds/plugin/src/FighterIdentityPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>Which live character object owns which saved fighter id, with sticky duplicate blocking. C++: FighterIdentityPolicy.h
/// (PA §2.16).</summary>
public static class FighterIdentityPolicy
{
    // C++: FighterIdentityPolicy.h:8 `typedef unsigned long long Lifetime;` -> ulong (0 = none)

    /// <summary>C++: FighterIdentityPolicy.h:9-82 <c>class Registry</c>. The C++ keys the registry by the character object's address
    /// (<c>std::map&lt;const void*, Entry&gt;</c>, compared by pointer identity); here the key is generic so the glue can use its
    /// character key (M2 task 2.3: PortraitSerial-based), compared with <paramref name="comparer"/> (default equality, which is
    /// reference identity for a class key like the C++ pointer). The map's address order is never observable: the only loop over it
    /// (<see cref="Bind"/>) gives the same result in any order, so an unordered Dictionary is used. The C++ null pointer is the key for which
    /// <c>isNoKey</c> (constructor) returns true: by default a null key. A value-type key (an int or ulong serial) has no null, so the
    /// glue MUST supply <c>isNoKey</c> for one (for example <c>k =&gt; k == 0</c>); without it every value is a real key.</summary>
    public sealed class Registry<TKey> where TKey : notnull
    {
        // C++: FighterIdentityPolicy.h:11-16 (a struct in the map, mutated in place through the iterator: a class here)
        sealed class Entry
        {
            public ulong Lifetime;
            public string Id = string.Empty;
        }

        readonly Dictionary<TKey, Entry> entries;
        // Sticky within a world: disappearance of a duplicate is not an
        // authorized decision about which fighter owns its saved career.
        // C++: std::set<std::string> -> ordinal SortedSet (std::string compares bytes)
        readonly SortedSet<string> conflicts = new SortedSet<string>(StringComparer.Ordinal);
        ulong sequence;
        // port note: stands in for the C++ `!object` test (no C++ counterpart as a member)
        readonly Func<TKey?, bool> isNoKey;

        // C++: FighterIdentityPolicy.h:24
        public Registry(IEqualityComparer<TKey>? comparer = null, Func<TKey, bool>? isNoKey = null)
        {
            entries = new Dictionary<TKey, Entry>(comparer ?? EqualityComparer<TKey>.Default);
            sequence = 0;
            this.isNoKey = isNoKey is null ? (k => k is null) : (k => k is null || isNoKey(k!));
        }

        // C++: FighterIdentityPolicy.h:25-32
        public ulong Begin(TKey? obj)
        {
            if (isNoKey(obj) || sequence == ~0UL) return 0;
            Entry entry = new Entry();
            entry.Lifetime = ++sequence;
            entries[obj!] = entry;
            return entry.Lifetime;
        }

        // C++: FighterIdentityPolicy.h:33-37
        public ulong Current(TKey? obj)
        {
            if (isNoKey(obj)) return 0;   // port note: the null pointer is never a map key in the C++ (Begin refuses it)
            return entries.TryGetValue(obj!, out Entry? it) ? it.Lifetime : 0;
        }

        // C++: FighterIdentityPolicy.h:38-46
        public void Load(TKey? obj, bool hasSaveState, string id, out bool conflict)
        {
            conflict = false;
            // Native loadFromSerialise(NULL) is a no-op, not a new lifetime.
            // A real legacy save lacking our field still creates a binding.
            if (!hasSaveState) return;
            ulong lifetime = Begin(obj);
            if (id.Length != 0) Bind(obj, lifetime, id, out conflict);
        }

        // C++: FighterIdentityPolicy.h:47-58
        public bool Bind(TKey? obj, ulong lifetime, string id, out bool conflict)
        {
            conflict = false;
            if (isNoKey(obj) || !entries.TryGetValue(obj!, out Entry? it) || it.Lifetime != lifetime || id.Length == 0) return false;
            if (it.Id.Length != 0 && !string.Equals(it.Id, id, StringComparison.Ordinal)) return false;
            it.Id = id;
            foreach (KeyValuePair<TKey, Entry> other in entries)
                if (!entries.Comparer.Equals(other.Key, obj) && string.Equals(other.Value.Id, id, StringComparison.Ordinal))
                    conflict = conflicts.Add(id) || conflict;
            return !conflicts.Contains(id);
        }

        // C++: FighterIdentityPolicy.h:59-65
        public bool Find(TKey? obj, ulong lifetime, out string id)
        {
            if (!SavedId(obj, lifetime, out id)) return false;
            if (!conflicts.Contains(id)) return true;
            id = string.Empty;
            return false;
        }

        // Serialization only. A conflicted token still belongs in native
        // metadata, but must never authorize a ledger read or mutation.
        // C++: FighterIdentityPolicy.h:68-75
        public bool SavedId(TKey? obj, ulong lifetime, out string id)
        {
            id = string.Empty;
            if (isNoKey(obj) || !entries.TryGetValue(obj!, out Entry? it) || it.Lifetime != lifetime || it.Id.Length == 0) return false;
            id = it.Id;
            return true;
        }

        // C++: FighterIdentityPolicy.h:76-80
        public void End(TKey? obj, ulong lifetime)
        {
            if (!isNoKey(obj) && entries.TryGetValue(obj!, out Entry? it) && it.Lifetime == lifetime) entries.Remove(obj!);
        }

        // C++: FighterIdentityPolicy.h:81
        public void Clear() { entries.Clear(); conflicts.Clear(); }
    }
}

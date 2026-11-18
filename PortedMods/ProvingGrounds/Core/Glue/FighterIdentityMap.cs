namespace ProvingGrounds.Core.Glue;

/// <summary>
/// The decisions of the fighter identity (task 2.3, PA §2.16, CAP 9.2) without game types: which token a character has, which live
/// characters have one bound, and the duplicate guard. The glue (src/Game/FighterIdentity.cs) feeds it the live characters.
/// <para>The original (FighterIdentity.cpp) writes a fresh UUID into a character's native save state the first time it is rated
/// (<c>GetOrCreate</c>), binds the saved UUID again when the character loads (the <c>loadFromSerialise</c> hook) and unbinds it when the
/// object is destroyed. The port derives the token from the character instead (<see cref="Token"/>: PortraitSerial + RecordId, which
/// Meitou saves for every character and keeps through squad moves and recruiting, CAP 9.2), so "create" = add the token to
/// <see cref="Registered"/> (the world's list of created ids, the counterpart of the UUIDs sitting in character save states; task 2.4
/// saves it), and "load" = bind a registered token to every live character that carries it, when the glue's lazy scan first sees that
/// character (<see cref="Rebuild"/>) or when it is first asked about (<see cref="Find"/> / <see cref="GetOrCreate"/>).</para>
/// <para>Who-owns-what and the sticky duplicate block are <see cref="FighterIdentityPolicy.Registry{TKey}"/>, unchanged: the key is the
/// live character id (a new id = a new lifetime, as a new C++ object), the id its token.</para>
/// <para>Callers pass only keys of characters that are live now (the glue checks first, as the C++ callers check <c>isValid()</c>): a
/// key asked about is taken as live, and one not seen before begins a lifetime.</para>
/// Sim thread only (the C++ locks a critical section because its hooks ran on native threads; nothing here is shared).
/// </summary>
public sealed class FighterIdentityMap<TKey> where TKey : notnull
{
    readonly FighterIdentityPolicy.Registry<TKey> registry;
    readonly Dictionary<TKey, string?> live;     // the live characters the registry has a lifetime for, and their tokens
    readonly HashSet<string> registered = new(StringComparer.Ordinal);
    readonly HashSet<TKey> now;                  // Rebuild's scratch sets, kept to avoid allocating per scan
    readonly List<TKey> gone = [];
    readonly Func<TKey, bool> isNoKey;
    readonly Action<string>? log;
    long stampSpawns = -1;
    int stampCount = -1;
    bool scanned;

    /// <param name="isNoKey">True for the "no character" key (the C++ null pointer), e.g. <c>CharacterId.IsNone</c>.</param>
    /// <param name="log">Receives the C++ log lines, whole (C++ <c>PGLog::Debug</c>).</param>
    public FighterIdentityMap(Func<TKey, bool> isNoKey, Action<string>? log = null, IEqualityComparer<TKey>? comparer = null)
    {
        this.isNoKey = isNoKey;
        this.log = log;
        registry = new FighterIdentityPolicy.Registry<TKey>(comparer, isNoKey);
        live = new Dictionary<TKey, string?>(comparer ?? EqualityComparer<TKey>.Default);
        now = new HashSet<TKey>(live.Comparer);
    }

    // C++: FighterIdentity.cpp:45-49
    void Log(string message) => log?.Invoke("Proving Grounds fighter identity: " + message);

    /// <summary>The identity token of a character: its PortraitSerial as 8 lowercase hex digits, '-', its RecordId (CAP 9.2: the record
    /// id tells apart the rare serial clashes of different templates). Null = no identity: no serial, or serial 0 (the value Meitou's
    /// own readers use for "none", WorldPublish.cs:542, PortraitBar.cs:150-152). Replaces C++ NewId (FighterIdentity.cpp:51-63).</summary>
    public static string? Token(uint? portraitSerial, string? recordId) =>
        portraitSerial is { } s && s != 0 ? s.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + "-" + (recordId ?? "") : null;

    /// <summary>C++: FighterIdentity.cpp:186-190 <c>CanPersist</c> = ready &amp;&amp; healthy. Always true: there is no hook to install and
    /// no metadata write that can fail (the token is the character's own saved data).</summary>
    public bool CanPersist => true;

    /// <summary>The tokens created this world (and restored from the save): the port's counterpart of the UUIDs in character save states.</summary>
    public IReadOnlyCollection<string> Registered => registered;

    /// <summary>A copy of <see cref="Registered"/> in ordinal order (std::string order for ASCII): what task 2.4 saves, so the same
    /// world writes the same bytes.</summary>
    public List<string> RegisteredSorted()
    {
        var list = new List<string>(registered);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>Whether the live set may have changed since the last <see cref="Rebuild"/>: the character table's spawn counter or
    /// count moved (a spawn always bumps the counter, a removal always lowers the count), or nothing was scanned yet.</summary>
    public bool IsStale(long spawns, int count) => !scanned || spawns != stampSpawns || count != stampCount;

    /// <summary>Brings the bindings in line with the live characters: a character gone ends its lifetime (C++ Destroy hook,
    /// FighterIdentity.cpp:146-155), a new one begins one and binds its token if registered (C++ Load hook, :125-144). Ends come first,
    /// so a character that left never counts as a duplicate of one that came.</summary>
    /// <param name="tokenOf">The token of a key; asked only for keys not live before (a live key keeps the token it had).</param>
    public void Rebuild(IEnumerable<TKey> characters, Func<TKey, string?> tokenOf, long spawns, int count)
    {
        now.Clear();
        foreach (var key in characters)
            if (!isNoKey(key)) now.Add(key);
        gone.Clear();
        foreach (var key in live.Keys)
            if (!now.Contains(key)) gone.Add(key);
        foreach (var key in gone) { registry.End(key, registry.Current(key)); live.Remove(key); }
        foreach (var key in now)
            if (!live.ContainsKey(key)) Appear(key, tokenOf(key));
        now.Clear();
        gone.Clear();
        stampSpawns = spawns;
        stampCount = count;
        scanned = true;
    }

    /// <summary>A character seen for the first time: a new lifetime, and its token bound when it was created before.</summary>
    void Appear(TKey key, string? token)
    {
        live[key] = token;
        ulong lifetime = registry.Begin(key);
        // C++: FighterIdentity.cpp:131-141 (Load: bind the saved id; a duplicate is logged with its id)
        if (token is not null && registered.Contains(token))
        {
            registry.Bind(key, lifetime, token, out bool conflict);
            if (conflict) Log("duplicate live fighter ID " + token + "; both careers blocked until corrected reload");
        }
    }

    /// <summary>The key is live with this token: seen before with the same token = nothing to do; not seen = it appears; seen with
    /// another token (its serial or record id changed, which Meitou does not do today) = it is another fighter: the old lifetime ends
    /// and a new one begins.</summary>
    void EnsureLive(TKey key, string? token)
    {
        if (live.TryGetValue(key, out string? had))
        {
            if (string.Equals(had, token, StringComparison.Ordinal)) return;
            registry.End(key, registry.Current(key));
            live.Remove(key);
        }
        Appear(key, token);
    }

    /// <summary>
    /// C++: FighterIdentity.cpp:192-218 <c>GetOrCreate</c>. False (id empty) for no character or no token. A character whose token is
    /// already bound gets it (blocked = false, as C++ Find). Otherwise the token is created (registered) and bound to every live character
    /// that carries it (they all hold it, as each would hold a saved UUID); a second holder is the duplicate case: logged with the C++
    /// text, every holder blocked for the world.
    /// </summary>
    public bool GetOrCreate(TKey key, string? token, out string id)
    {
        id = string.Empty;
        if (isNoKey(key) || token is null) return false;
        EnsureLive(key, token);
        ulong lifetime = registry.Current(key);
        if (registry.SavedId(key, lifetime, out _)) return registry.Find(key, lifetime, out id);
        registered.Add(token);
        bool conflict = false;
        foreach (var (other, t) in live)
            if (string.Equals(t, token, StringComparison.Ordinal))
            {
                registry.Bind(other, registry.Current(other), token, out bool c);
                conflict |= c;
            }
        if (conflict) Log("duplicate generated fighter ID " + token + "; careers blocked");
        return registry.Find(key, lifetime, out id);
    }

    /// <summary>C++: FighterIdentity.cpp:220-225 <c>Find</c>: the bound, unblocked token; false (id empty) for no character, no token, a
    /// token never created, or a blocked one.</summary>
    public bool Find(TKey key, string? token, out string id)
    {
        id = string.Empty;
        if (isNoKey(key)) return false;
        EnsureLive(key, token);
        return registry.Find(key, registry.Current(key), out id);
    }

    /// <summary>The duplicate guard holds this character's career: it has a bound token, and that token is blocked.</summary>
    public bool IsBlocked(TKey key)
    {
        ulong lifetime = registry.Current(key);
        return registry.SavedId(key, lifetime, out _) && !registry.Find(key, lifetime, out _);
    }

    /// <summary>Task 2.4: the saved token list after a load. Binds each to the live characters that carry it (as C++ Load would).</summary>
    public void Restore(IEnumerable<string> tokens)
    {
        foreach (var t in tokens)
        {
            if (t.Length == 0 || !registered.Add(t)) continue;
            foreach (var (key, token) in live)
                if (string.Equals(token, t, StringComparison.Ordinal))
                {
                    registry.Bind(key, registry.Current(key), t, out bool conflict);
                    if (conflict) Log("duplicate live fighter ID " + t + "; both careers blocked until corrected reload");
                }
        }
    }

    /// <summary>C++: FighterIdentity.cpp:227-231 <c>AbandonWorldState</c>: drops every binding and block (registry Clear). The registered
    /// tokens stay, as the C++ UUIDs stay in the characters' save states; the next scan binds again.</summary>
    public void AbandonWorldState()
    {
        registry.Clear();
        live.Clear();
        scanned = false;
    }
}

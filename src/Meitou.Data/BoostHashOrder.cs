namespace Meitou.Data;

/// <summary>
/// The iteration order of the original's boost 1.5x <c>unordered_map</c> / <c>unordered_set</c> (docs/formats/boost-unordered.md): the game lists
/// some tables in that order (RACE STATS, the random groups of the appearance rolls, the records of a type).
/// </summary>
public static class BoostHashOrder
{
    /// <summary>
    /// The keys of a map that got <paramref name="inserted"/> in that order (repeats ignored), starting at <paramref name="buckets"/> buckets with a
    /// maximum load factor of <paramref name="maxLoad"/>, as the map iterates them. Integer keys hash to themselves.
    /// </summary>
    public static List<long> Of(IEnumerable<long> inserted, int buckets = 16, float maxLoad = 1) => Of(inserted, k => (ulong)k, buckets, maxLoad);

    /// <summary>
    /// The same for keys of any type, with the hash boost gives them (<paramref name="hash"/>, before the bucket policy's <see cref="Mix64"/>).
    /// </summary>
    public static List<T> Of<T>(IEnumerable<T> inserted, Func<T, ulong> hash, int buckets = 16, float maxLoad = 1) where T : notnull
    {
        var order = new List<(T Key, ulong Hash)>();
        var seen = new HashSet<T>();
        int count = 0, limit = 0;
        foreach (var key in inserted)
        {
            if (!seen.Add(key)) continue;
            int size = order.Count + 1;
            if (count == 0)
            {
                count = Math.Max(buckets, BucketsFor(size, maxLoad));
                limit = (int)(count * maxLoad);
            }
            else if (size > limit && BucketsFor(Math.Max(size, order.Count + (order.Count >> 1)), maxLoad) is int grown && grown != count)
            {
                // Rehash: the list is walked in order; a node whose new bucket is empty stays where it is, one whose bucket has nodes moves to the
                // front of their run.
                count = grown;
                limit = (int)(count * maxLoad);
                var rehashed = new List<(T, ulong)>(order.Count);
                foreach (var node in order) Place(rehashed, node, count, newRunFirst: false);
                order = rehashed;
            }
            Place(order, (key, Mix64(hash(key))), count, newRunFirst: true);
        }
        return order.ConvertAll(n => n.Key);
    }

    /// <summary>
    /// A key into the list: at the front of its bucket's run, else (a bucket new to the list) first (an insert) or last (a rehash's walk).
    /// </summary>
    static void Place<T>(List<(T Key, ulong Hash)> order, (T Key, ulong Hash) node, int count, bool newRunFirst)
    {
        ulong mask = (ulong)(count - 1), bucket = node.Hash & mask;
        int run = order.FindIndex(k => (k.Hash & mask) == bucket);
        if (run >= 0) order.Insert(run, node);
        else if (newRunFirst) order.Insert(0, node);
        else order.Add(node);
    }

    /// <summary>
    /// boost's bucket count for a size: the power of two (at least 4) above size / max load.
    /// </summary>
    static int BucketsFor(int size, float maxLoad)
    {
        int wanted = (int)(size / maxLoad) + 1, n = 4;
        while (n < wanted) n <<= 1;
        return n;
    }

    /// <summary>
    /// Thomas Wang's 64-bit mix, boost's hash policy for power-of-two bucket counts (an integer key hashes to itself first).
    /// </summary>
    public static ulong Mix64(ulong k)
    {
        k = ~k + (k << 21);
        k ^= k >> 24;
        k = k + (k << 3) + (k << 8);
        k ^= k >> 14;
        k = k + (k << 2) + (k << 4);
        k ^= k >> 28;
        return k + (k << 31);
    }

    /// <summary>
    /// <c>boost::hash&lt;std::string&gt;</c> before boost 1.56 (<c>hash_range</c>: each character, sign-extended, combined as seed ^= c + 0x9e3779b9
    /// + (seed &lt;&lt; 6) + (seed &gt;&gt; 2) on a 64-bit seed). <b>Observed</b>: the exe holds the 0x9e3779b9 constant and not the later 64-bit
    /// combine's 0xc6a4a7935bd1e995.
    /// </summary>
    public static ulong StringHash(string s)
    {
        ulong seed = 0;
        foreach (byte c in System.Text.Encoding.UTF8.GetBytes(s)) seed ^= (ulong)(long)(sbyte)c + 0x9e3779b9UL + (seed << 6) + (seed >> 2);
        return seed;
    }
}
namespace Meitou.Data.World;

/// <summary>
/// Arrays a laid-out zone keeps for as long as the zone is resident (its ground heights, density maps and instance records), which at travel speed is tens of
/// seconds. Allocated on the pinned object heap: the 66 KB heights, the 16 KB density maps and most groups are under the large-object threshold, so on the ordinary
/// heap each one was copied from generation 0 to 1 and again to 2 by blocking collections, 40 to 70 MB at a time, 14 to 30 ms a pause (<c>MEITOU_ALLOC_STATS=1</c>).
/// The pinned heap is never compacted or copied, only swept with generation 2.
/// </summary>
static class ZoneArrays
{
    /// <summary>An array of <paramref name="length"/> elements whose contents the caller overwrites in full.</summary>
    public static T[] Uninitialized<T>(int length) where T : unmanaged => GC.AllocateUninitializedArray<T>(length, pinned: true);
}

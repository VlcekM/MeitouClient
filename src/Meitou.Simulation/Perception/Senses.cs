using System.Numerics;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Perception;

/// <summary>
/// The tags of a sense entry (the original's tag word, entry +0x30, docs/game/perception.md "Sense tags"): the bits the readers test.
/// </summary>
[Flags]
public enum SenseTag : ushort
{
    /// <summary>
    /// No tag.
    /// </summary>
    None = 0,

    /// <summary>
    /// Bit 0 SENSE_ALLY: slot 126 calls it friendly (Meitou: the same or an allied faction).
    /// </summary>
    Ally = 1,

    /// <summary>
    /// Bit 1 SENSE_NEUTRAL: neither.
    /// </summary>
    Neutral = 2,

    /// <summary>
    /// Bit 2 SENSE_ENEMY: slot 125 calls it an enemy.
    /// </summary>
    Enemy = 4,

    /// <summary>
    /// Bit 3 SENSE_KO: down or dead when last sensed.
    /// </summary>
    Ko = 8,

    /// <summary>
    /// Bit 9: imprisoned (Character +0x2f8 2) when last sensed.
    /// </summary>
    Imprisoned = 0x200,
}

/// <summary>
/// One sense entry (the original's 0x40-byte entry, <c>FUN_1408580d0</c>, <b>Verified</b> layout): the character, where it was last sensed (+0x0,
/// followed for a while after, see <see cref="SenseBlock"/>), the tick it was last seen or heard (+0x10), whether the last look saw (+0x38) or heard
/// (+0x39) it, and its tags.
/// </summary>
public struct SenseEntry
{
    /// <summary>
    /// The sensed character's slot and generation.
    /// </summary>
    public int Target, Generation;
    /// <summary>
    /// The tick it was last seen or heard (+0x10).
    /// </summary>
    public long Tick;
    /// <summary>
    /// Where it was last sensed (+0x0).
    /// </summary>
    public Vector3 Place;
    /// <summary>
    /// Whether the last look saw (+0x38) or heard (+0x39) it.
    /// </summary>
    public bool Seen, Heard;
    /// <summary>
    /// Its tags (+0x30).
    /// </summary>
    public SenseTag Tags;

    /// <summary>
    /// The ENEMY / NEUTRAL / ALLY part of <see cref="Tags"/> from the relations, last worked out at a full look (<see cref="AggroSystem"/>; the
    /// fights override it at every look).
    /// </summary>
    internal SenseTag Base;

    /// <summary>
    /// Seen or heard at the last look (the finders' "sensed now").
    /// </summary>
    public readonly bool Now => Seen || Heard;
}

/// <summary>
/// A character's senses (the original's <c>SensoryData</c>, docs/game/perception.md "Senses"): the entries, kept in target slot order
/// and bounded by <see cref="Cap"/>, and what the last look counted from them. Written by <see cref="AggroSystem"/>'s look in
/// <c>Act</c> (the looker's own slot only) and by the forced sense (<see cref="AggroSystem.Force"/>, serial); read from any phase after
/// that look (<see cref="CharacterFacts.Senses"/>).
/// </summary>
public sealed class SenseBlock
{
    /// <summary>
    /// The most entries a character keeps (engine choice: the original's map has no bound, <b>Verified</b>; it is fed by a gather that has none
    /// either). A new sensed character past the cap replaces the entry sensed longest ago, when that one is older than the look.
    /// </summary>
    public const int Cap = 40;

    /// <summary>
    /// The entries, by target slot.
    /// </summary>
    internal readonly List<SenseEntry> EntryList = [];
    /// <summary>
    /// The threats sensed at the last look.
    /// </summary>
    internal readonly List<CharacterId> ThreatList = [];

    /// <summary>
    /// The entries, by target slot.
    /// </summary>
    public IReadOnlyList<SenseEntry> Entries => EntryList;

    /// <summary>
    /// The threats list (+0x50): the characters of another faction sensed at the last look whose fight targets this one.
    /// </summary>
    public IReadOnlyList<CharacterId> Threats => ThreatList;

    /// <summary>
    /// <c>numEnemies</c> (+0x94, the AI's +0xbc): entries tagged enemy whose character is not down, dead or carried.
    /// </summary>
    public int Enemies { get; internal set; }

    /// <summary>
    /// <c>numNeutrals</c> (+0x98): the same for the entries tagged neutral.
    /// </summary>
    public int Neutrals { get; internal set; }

    /// <summary>
    /// Allies sensed at the last look standing (posture 0), awake and alive (+0x4c), and the others (+0x48).
    /// </summary>
    public int Allies { get; internal set; }

    /// <summary>
    /// The other allies sensed at the last look (+0x48).
    /// </summary>
    public int AlliesDown { get; internal set; }

    /// <summary>
    /// The threat towards this character (+0x88, the AI's +0xb0) and towards characters of its faction (+0x8c): one per fighting threat (the original
    /// sums aggression, Meitou has none).
    /// </summary>
    public float ThreatToMe { get; internal set; }

    /// <summary>
    /// The threat towards characters of its faction (+0x8c), one per fighting threat.
    /// </summary>
    public float ThreatToAllies { get; internal set; }

    /// <summary>
    /// The last look was a fade (<c>SensoryData::fade</c>: carried or knocked out), so the counts are zero, not sensed.
    /// </summary>
    public bool Faded { get; internal set; }

    /// <summary>
    /// The entry of <paramref name="slot"/> (its generation), by binary search; -1 when none.
    /// </summary>
    public int IndexOf(int slot, int generation)
    {
        int lo = 0;
        int hi = EntryList.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int t = EntryList[mid].Target;
            if (t == slot) return EntryList[mid].Generation == generation ? mid : -1;

            if (t < slot)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return -1;
    }

    /// <summary>
    /// Whether the block holds an entry of <paramref name="who"/> (the knows test <c>FUN_140855960</c> with its flag clear; with
    /// <paramref name="now"/> only one sensed at the last look).
    /// </summary>
    public bool Knows(CharacterId who, bool now = false)
    {
        int k = IndexOf(who.Slot, who.Generation);
        return k >= 0 && (!now || EntryList[k].Now);
    }

    /// <summary>
    /// The entry of <paramref name="who"/>, if any.
    /// </summary>
    public bool TryGet(CharacterId who, out SenseEntry entry)
    {
        int k = IndexOf(who.Slot, who.Generation);
        entry = k >= 0 ? EntryList[k] : default;
        return k >= 0;
    }

    /// <summary>
    /// Where the entry of <paramref name="slot"/> sits or would be inserted (the first entry whose target slot is not below it).
    /// </summary>
    internal int LowerBound(int slot)
    {
        int lo = 0;
        int hi = EntryList.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (EntryList[mid].Target < slot)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    /// <summary>
    /// Adds the entries (all fields, the base tags too) to the world hash.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(EntryList.Count);
        foreach (var e in EntryList)
        {
            h.Add(e.Target);
            h.Add(e.Generation);
            h.Add(e.Tick);
            h.Add(e.Place.X);
            h.Add(e.Place.Y);
            h.Add(e.Place.Z);
            h.Add(e.Seen);
            h.Add(e.Heard);
            h.Add((int)e.Tags);
            h.Add((int)e.Base);
        }

        h.Add(ThreatList.Count);
        foreach (var t in ThreatList)
        {
            h.Add(t.Slot);
            h.Add(t.Generation);
        }

        h.Add(Enemies);
        h.Add(Neutrals);
        h.Add(Allies);
        h.Add(AlliesDown);
        h.Add(ThreatToMe);
        h.Add(ThreatToAllies);
    }
}

using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Game.Audio;

/// <summary>
/// One sound riding on an animation (an ANIMATION_EVENT record the way the original's <c>AnimationSFXEvent</c> keeps it, docs/audio.md "Footsteps and
/// animation events"): the Wwise event to post at <see cref="Fraction"/> of the clip, the switch to set first (<see cref="OverrideState"/> =
/// <see cref="OverrideValue"/>) and the event to post when the animation ends (<see cref="Stop"/>). <see cref="Footstep"/> is an event called exactly
/// <c>Footsteps</c>.
/// </summary>
public sealed record AnimationSoundEvent(string Event, string OverrideState, string OverrideValue, string Stop, bool Footstep, float Fraction);

/// <summary>
/// The sound events of the animation records: the <c>events</c> of ANIMATION, ANIMAL_ANIMATION and COMBAT_TECHNIQUE records and the <c>animation
/// events</c> of BUILDING_FUNCTIONALITY ones, each a reference to an ANIMATION_EVENT with the time in per cent as its first value (<b>Verified</b>:
/// the exe adds an event at value x 0.01, <c>FUN_140064e10</c>). Kept per record string id, sorted by time like the original's insertion
/// (<c>FUN_140064870</c>). Records whose <c>event</c> is empty make no sound event.
/// </summary>
public sealed class AnimationSoundEvents
{
    /// <summary>
    /// The list fields that carry the sound events: <c>events</c> (animations, techniques) and <c>animation events</c> (building functionality).
    /// </summary>
    static readonly string[] Lists = ["events", "animation events"];
    /// <summary>
    /// The events per record string id, each array in time order.
    /// </summary>
    readonly Dictionary<string, AnimationSoundEvent[]> _byRecord = new(StringComparer.Ordinal);

    /// <summary>
    /// Made only by <see cref="FromDatabase"/>.
    /// </summary>
    AnimationSoundEvents() { }

    /// <summary>
    /// Reads the events of every animation-carrying record of the database, per record string id.
    /// </summary>
    public static AnimationSoundEvents FromDatabase(GameDatabase db)
    {
        var result = new AnimationSoundEvents();
        var types = new[]
        {
            FcsRecordType.Animation, FcsRecordType.AnimalAnimation, FcsRecordType.CombatTechnique, FcsRecordType.BuildingFunctionality,
        };
        foreach (var type in types)
        {
            foreach (var record in db.OfType(type))
                result.ReadRecord(db, record);
        }

        return result;
    }

    /// <summary>
    /// Adds the sound events of one record's <see cref="Lists"/> references, when it has any.
    /// </summary>
    void ReadRecord(GameDatabase db, GameRecord record)
    {
        List<AnimationSoundEvent>? list = null;
        foreach (var name in Lists)
        {
            foreach (var reference in record.GetReferences(name))
            {
                if (db.Find(reference.TargetStringId) is not { Type: FcsRecordType.AnimationEvent } def) continue;
                if (def.GetString("event") is not { Length: > 0 } ev) continue;

                var sound = new AnimationSoundEvent(ev, def.GetString("override state"), def.GetString("override value"), def.GetString("stop"),
                    ev == "Footsteps", reference.Values.Value0 * 0.01f);
                Insert(list ??= [], sound);
            }
        }

        if (list is not null) _byRecord[record.StringId] = [.. list];
    }

    /// <summary>
    /// Inserts before the first event later than this one; the same event at the same time twice is kept once (<c>FUN_140064870</c>).
    /// </summary>
    static void Insert(List<AnimationSoundEvent> list, AnimationSoundEvent sound)
    {
        int at = 0;
        for (; at < list.Count; at++)
        {
            if (IsSameEvent(list[at], sound)) return;
            if (sound.Fraction < list[at].Fraction) break;
        }

        list.Insert(at, sound);
    }

    /// <summary>
    /// Whether two events are the same one: same time, event and override value.
    /// </summary>
    static bool IsSameEvent(AnimationSoundEvent a, AnimationSoundEvent b) =>
        a.Fraction == b.Fraction && a.Event == b.Event && a.OverrideValue == b.OverrideValue;

    /// <summary>
    /// A clip's events with <paramref name="added"/> inserted the same way: what a building's functionality puts on its operator's clip (its
    /// <c>animation events</c>, added by <c>Task_OperateMachine</c> and <c>Task_UseTrainingDummy</c> each update, docs/audio.md "Character tasks").
    /// </summary>
    public static AnimationSoundEvent[] Attach(AnimationSoundEvent[]? clip, AnimationSoundEvent[] added)
    {
        var list = new List<AnimationSoundEvent>(clip ?? []);
        foreach (var sound in added) Insert(list, sound);
        return [.. list];
    }

    /// <summary>
    /// The events of the record with this string id, in time order; null when it has none.
    /// </summary>
    public AnimationSoundEvent[]? Of(string recordId) => _byRecord.GetValueOrDefault(recordId);

    /// <summary>
    /// How many records carry events.
    /// </summary>
    public int Records => _byRecord.Count;
}

/// <summary>
/// The position of one animation's events (<b>Verified</b>, <c>FUN_140064690</c>): the last fraction seen and the next event to fire. An update with
/// an unchanged fraction does nothing; one that went back (the clip looped) first fires the events left from the pass before, then goes on from the
/// first. While the animation is not <paramref name="enabled"/> only the fraction is kept, so an animation that becomes the dominant one fires every
/// event up to its fraction at once.
/// </summary>
public struct AnimationEventCursor
{
    /// <summary>
    /// The clip fraction (0..1) at the last update.
    /// </summary>
    public float Last;
    /// <summary>
    /// The index of the next event to fire; the event count once all of this pass have fired.
    /// </summary>
    public int Next;

    /// <summary>
    /// The events changed under the cursor (a machine's added to its clip): the next to fire is the first not before the last fraction, as the exe's
    /// insert moves the index past an event added behind it (<c>FUN_140064870</c>).
    /// </summary>
    public void Rebase(AnimationSoundEvent[] events)
    {
        Next = 0;
        while (Next < events.Length && events[Next].Fraction < Last) Next++;
    }

    /// <summary>
    /// Fires, through <paramref name="fire"/>, the events passed between the last fraction and <paramref name="fraction"/> (those left from the pass
    /// before first when the clip looped); keeps only the fraction while not <paramref name="enabled"/>.
    /// </summary>
    public void Update(AnimationSoundEvent[] events, float fraction, bool enabled, Action<AnimationSoundEvent> fire)
    {
        if (events.Length == 0 || fraction == Last) return;
        bool wrapped = fraction < Last;
        Last = fraction;
        if (!enabled) return;
        if (wrapped && Next == events.Length)
        {
            Next = 0;
            wrapped = false;
        }
        while (Next < events.Length)
        {
            if (!wrapped && fraction < events[Next].Fraction) return;
            fire(events[Next]);
            Next++;
            if (wrapped && Next == events.Length)
            {
                Next = 0;
                wrapped = false;
            }
        }
    }
}
using System.Collections.Concurrent;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Movement;

/// <summary>
/// An animal's clips with those its skeleton lacks taken out, and the fall and lying clip chosen (<see cref="AnimalPlans.Of"/>).
/// </summary>
sealed record AnimalPlan(int[] Movement, IReadOnlyList<(int Index, float Weight)> Idles, int Fall, int Lying, AnimationLengths Lengths);

/// <summary>
/// The animals' plans (<see cref="AnimalPlan"/>), made once per clip set on first use from the clip library and the races' skeleton lengths; read
/// on every worker.
/// </summary>
sealed class AnimalPlans(AnimationLibrary library, AnimationLengths lengths)
{
    /// <summary>
    /// The clip library the plans index.
    /// </summary>
    AnimationLibrary Library { get; } = library;

    /// <summary>
    /// The skeleton lengths of the races.
    /// </summary>
    AnimationLengths Lengths { get; } = lengths;

    /// <summary>
    /// The plans made so far, by animal clip set (<see cref="Of"/>).
    /// </summary>
    readonly ConcurrentDictionary<string, AnimalPlan> _plans = new(StringComparer.Ordinal);

    /// <summary>
    /// The plan of an animal's clip set: the race's skeleton lengths (<see cref="AnimationLengths.For"/>), and when that skeleton was read, only the
    /// clips it has (a record whose clip the skeleton lacks would pose nothing: the skimmer's <c>idle1</c>); the first fall and lying candidates it
    /// has.
    /// </summary>
    public AnimalPlan Of(AnimalClips clips) => _plans.GetOrAdd(clips.RecordId, static (_, s) =>
    {
        var (self, c) = s;
        var lengths = self.Lengths.For(c.RaceId);
        bool known = c.RaceId is { } race && self.Lengths.HasSpecies(race);
        bool Has(int i) => !known || lengths.Has(self.Library.Definitions[i].Clip);
        int First(int[] list)
        {
            foreach (int i in list) if (Has(i)) return i;
            return -1;
        }
        return new AnimalPlan([.. c.Movement.Where(Has)], [.. c.Idles.Where(x => Has(x.Index))], First(c.Fall), First(c.Lying), lengths);
    }, (this, clips));
}

using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// An AMBIENT_SOUND record (docs/audio.md "Point emitters"): which looping sound a building (and a foliage mesh, map feature, effect or race) makes
/// and how far it carries.
/// </summary>
/// <param name="Emitter">The <c>emitter</c> name: the value of the <c>Emitters</c> switch (<c>Fire</c>, <c>Generator</c>, …).</param>
/// <param name="Chance">Probability 0..1 that the placed object gets the sound at all.</param>
/// <param name="Cutoff">The <c>cutoff</c> in "meters": the sound is on while the camera eye is within <see cref="Range"/> units.</param>
/// <param name="Intensity">Base value 0..1 of the <c>Intensity</c> game parameter (the game sends it × 100).</param>
/// <param name="EfficiencyMultiplier">How much the machine's efficiency adds to the intensity, 0..1 (× 100 as well).</param>
/// <param name="NeedsPower">The emitter is off until its building runs.</param>
public sealed record AmbientSound(string Id, string Emitter, float Chance, float Cutoff, float Intensity, float EfficiencyMultiplier, bool NeedsPower)
{
    /// <summary>
    /// Units per cutoff meter: the game compares the squared distance with <c>cutoff² × 100</c>.
    /// </summary>
    public const float UnitsPerCutoff = 10;

    /// <summary>
    /// The distance in world units beyond which the emitter is stopped.
    /// </summary>
    public float Range => Cutoff * UnitsPerCutoff;

    /// <summary>
    /// The AMBIENT_SOUND record <paramref name="record"/> with the fcs.def defaults; null for a record of another type.
    /// </summary>
    public static AmbientSound? From(GameRecord record) => record.Type != FcsRecordType.AmbientSound ? null : new(
        record.StringId, record.GetString("emitter"), record.GetFloat("chance", 1), record.GetFloat("cutoff", 50), record.GetFloat("intensity"),
        record.GetFloat("efficiency multiplier"), record.GetBool("needs power"));

    /// <summary>
    /// The sound of the first entry of <paramref name="holder"/>'s <paramref name="field"/> list (EFFECT <c>sound</c>, RACE <c>ambient sound</c>), or
    /// null when the list is empty, the AMBIENT_SOUND is missing or names no emitter. The exe takes the first entry only and rolls no <c>chance</c>
    /// for them (docs/audio.md "Point emitters").
    /// </summary>
    public static AmbientSound? FirstOf(GameDatabase db, GameRecord holder, string field)
    {
        if (holder.GetReferences(field) is not { Count: > 0 } list || db.Find(list[0].TargetStringId) is not { } record) return null;

        return From(record) is { Emitter.Length: > 0 } sound ? sound : null;
    }
}

/// <summary>
/// A building's sound emitter in the world: the record, where it sounds and the id of the building it belongs to; <paramref name="Building"/> is the
/// BUILDING record that made it and <paramref name="Origin"/> the building's own position (the emitter sits at an offset from it).
/// </summary>
public readonly record struct PlacedEmitter(string Key, AmbientSound Sound, Vector3 Position, string Building = "", Vector3 Origin = default);

/// <summary>
/// The emitters of buildings (docs/audio.md "Point emitters", the exe's building audio set-up <c>FUN_14055c240</c>).
/// </summary>
public static class BuildingEmitters
{
    /// <summary>
    /// The emitter of a building placed at <paramref name="position"/> / <paramref name="rotation"/>, or null when its record has no <c>sounds</c>,
    /// the AMBIENT_SOUND is missing, has no emitter name, or the chance roll fails. Only the first entry of <c>sounds</c> counts (the game keeps one
    /// emitter per building); its three values are an offset in the building's own axes, scaled by the building's <c>scale</c>. The roll is a stable
    /// hash of the placement (the game rolls once with <c>rand()</c>).
    /// </summary>
    public static PlacedEmitter? For(GameDatabase db, GameRecord building, string instanceId, Vector3 position, Quaternion rotation)
    {
        var sounds = building.GetReferences("sounds");
        if (sounds.Count == 0 || db.Find(sounds[0].TargetStringId) is not { } record
            || AmbientSound.From(record) is not { Emitter.Length: > 0 } sound)
            return null;
        if (sound.Chance <= Roll(instanceId)) return null;
        float scale = building.GetFloat("scale", 1);
        var offset = new Vector3(sounds[0].Values.Value0, sounds[0].Values.Value1, sounds[0].Values.Value2) * (scale > 0 ? scale : 1);
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        return new PlacedEmitter(instanceId, sound, position + Vector3.Transform(offset, q), building.StringId, position);
    }

    /// <summary>
    /// A number in [0, 1) that depends only on the placement id (FNV-1a).
    /// </summary>
    static float Roll(string id)
    {
        uint h = 2166136261;
        foreach (char c in id) h = (h ^ c) * 16777619;
        return (h >> 8) / (float)(1 << 24);
    }
}
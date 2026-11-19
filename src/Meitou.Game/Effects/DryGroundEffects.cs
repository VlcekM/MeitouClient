using System.Diagnostics;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Particles;
using Meitou.Game.Audio;
using Meitou.Game.Rendering.Particles;

namespace Meitou.Game.Effects;

/// <summary>
/// The animations' ground effects on dry ground (docs/audio.md "Ground effect"; <c>FUN_140065330</c>, the dry half): a ground event above the water
/// fires the biome's <c>ground effects</c> slot for the overlay channel under the point (the dust kicks) when the character is fast enough and the
/// chance rolls, on the terrain only, through a pool of 100 instances that live 10 s at most. Display only.
/// </summary>
sealed class DryGroundEffects(GameHost host)
{
    /// <summary>
    /// One <c>ground effects</c> slot of a biome: the EFFECT's particle system, the speed it needs at least (v1) and its chance (v2 x 0.01).
    /// </summary>
    internal readonly record struct Slot(string System, float MinSpeed, float Chance);

    /// <summary>
    /// The ground effects' pool (<c>FUN_140a0ccd0</c>: 100 instances; <c>FUN_14040be00</c> retires one after 10 s, <c>DAT_14167fad4</c>;
    /// <b>Verified</b>).
    /// </summary>
    const int PoolSize = 100;
    /// <summary>
    /// The seconds an instance holds its place in the pool.
    /// </summary>
    const float PoolSeconds = 10;
    /// <summary>
    /// The seconds a kick's unit emits: its emitters are forced, so they throw one burst at the start.
    /// </summary>
    const float EmitSeconds = 0.25f;
    /// <summary>
    /// The longest frame step the pool's clock takes (a stall does not empty the pool at once).
    /// </summary>
    const float MaxStep = 0.25f;

    /// <summary>
    /// Each biome's five slots (by overlay channel), by its 24-bit index; a null slot fires nothing.
    /// </summary>
    Dictionary<uint, Slot?[]> _slots = [];
    /// <summary>
    /// The pool clock's time of each live instance, oldest first.
    /// </summary>
    readonly Queue<float> _pool = [];
    /// <summary>
    /// The pool's clock: the frame clock's seconds, each step capped at <see cref="MaxStep"/>.
    /// </summary>
    float _clock;
    /// <summary>
    /// The timestamp of the last <see cref="Advance"/>; 0 before the first.
    /// </summary>
    long _last;

    /// <summary>
    /// Whether the kicks fire (the console's <c>dust-stats on|off</c>; the original's always do).
    /// </summary>
    internal bool Enabled = true;
    /// <summary>
    /// Kicks fired since the start or the last <c>dust-stats reset</c>.
    /// </summary>
    internal long Kicks;
    /// <summary>
    /// Kicks refused by the full pool since the start or the last <c>dust-stats reset</c>.
    /// </summary>
    internal long Refused;

    /// <summary>
    /// The instances alive in the pool.
    /// </summary>
    internal int Alive => _pool.Count;

    /// <summary>
    /// Reads every BIOMES record's <c>ground effects</c> slots (biome loader <c>FUN_140a13750</c>: an entry's v0 above 2 is lowered by one, then 0..4
    /// picks the slot, a later entry for a slot replaces an earlier one; v1 the min speed, v2 x 0.01 the chance); returns the particle systems they
    /// fire.
    /// </summary>
    internal HashSet<string> Load(GameDatabase db)
    {
        _slots = [];
        var systems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in db.OfType(FcsRecordType.Biomes).OrderBy(r => r.StringId, StringComparer.Ordinal))
        {
            var slots = new Slot?[5];
            foreach (var reference in r.GetReferences("ground effects"))
            {
                if (db.Find(reference.TargetStringId) is not { Type: FcsRecordType.Effect } effect
                    || EffectRecord.From(effect, db).ParticleSystem is not { Length: > 0 } system)
                    continue;

                int index = reference.Values.Value0 > 2 ? reference.Values.Value0 - 1 : reference.Values.Value0;
                if ((uint)index >= (uint)slots.Length) continue;

                slots[index] = new Slot(system, reference.Values.Value1, reference.Values.Value2 * 0.01f);
                systems.Add(system);
            }

            _slots.TryAdd((uint)r.GetInt("index") & 0xFFFFFF, slots);
        }

        return systems;
    }

    /// <summary>
    /// Once a frame, before the ground events: moves the pool's clock on and retires the instances <see cref="PoolSeconds"/> old.
    /// </summary>
    internal void Advance()
    {
        long now = Stopwatch.GetTimestamp();
        if (_last != 0) _clock += Math.Clamp((now - _last) / (float)Stopwatch.Frequency, 0, MaxStep);
        _last = now;
        while (_pool.Count > 0 && _pool.Peek() + PoolSeconds <= _clock) _pool.Dequeue();
    }

    /// <summary>
    /// The slot under a point: the biome's for the overlay channel there; null for none, or while the maps load.
    /// </summary>
    internal Slot? SlotAt(GroundSurfaces ground, float x, float z)
    {
        if (ground.BiomeAt(x, z) is not { } biome || !_slots.TryGetValue(biome, out var slots)) return null;

        int channel = ground.ChannelAt(x, z);
        return (uint)channel < (uint)slots.Length ? slots[channel] : null;
    }

    /// <summary>
    /// A ground event's kick, before the frustum test: the slot under <paramref name="point"/> when <paramref name="speed"/> reaches its minimum and
    /// <c>rand() / 32768</c> is at most its chance, and the character at <paramref name="position"/> stands on the terrain, not a building floor (the
    /// ground ray's null handle). A bone puts the point on the terrain. Null for no kick.
    /// </summary>
    internal (string System, Vector3 At)? Pick(Vector3 point, bool bone, Vector3 position, float speed)
    {
        if (!Enabled || host.Sound.Ground is not { } ground || SlotAt(ground, point.X, point.Z) is not { } slot) return null;
        if (speed < slot.MinSpeed || Random.Shared.NextSingle() > slot.Chance) return null;
        if (host.Sound.Characters.FloorSurface(position) >= 0) return null;

        if (bone)
        {
            float terrain = host.Gpu.Terrain.HeightAt(point.X, point.Z);
            if (float.IsFinite(terrain)) point.Y = terrain;
        }

        return (slot.System, point);
    }

    /// <summary>
    /// Fires a picked kick through the pool (<c>FUN_14040bcc0</c>: none when <see cref="PoolSize"/> are alive), at the point with no turn.
    /// </summary>
    internal void Fire(ParticleRenderer particles, string system, Vector3 at)
    {
        if (_pool.Count >= PoolSize)
        {
            Refused++;
            return;
        }

        _pool.Enqueue(_clock);
        Kicks++;
        particles.Burst(system, at, Quaternion.Identity, EmitSeconds, scatters: true);
    }
}

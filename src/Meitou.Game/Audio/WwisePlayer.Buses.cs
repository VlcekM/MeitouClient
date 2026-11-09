using Meitou.Data.Audio;

namespace Meitou.Game.Audio;

/// <summary>
/// Buses (docs/audio.md#buses-pause-and-reverb): the bus gain chain with action-driven offsets that fade (the pause ducking), and the auxiliary
/// buses with their reverb: sounds send a copy of their signal to the aux buses their node lists, each aux bus runs the effects in its
/// un-bypassed slots (the banks' bypass actions switch the outdoor and the indoor rooms on and off) and adds the result to the mix.
/// </summary>
public sealed partial class WwisePlayer
{
    /// <summary>
    /// Offsets set by actions, keyed by (target node or bus, game object; 0 for the global scope), fading to their target value. Volume and bus
    /// volume offsets are in dB, pitch in cents, low-pass in its own units.
    /// </summary>
    readonly OffsetTable _volumeOffsets = new(decibels: true), _pitchOffsets = new(), _lowPassOffsets = new(), _busVolumes = new(decibels: true);
    /// <summary>
    /// The auxiliary buses' states by bus id, made on first use (<see cref="AuxBus"/>).
    /// </summary>
    readonly Dictionary<uint, AuxBusState> _auxBuses = [];

    /// <summary>
    /// A value in dB (or cents, or low-pass units) set by an action: it moves to its target over the action's fade time and an entry that has fallen
    /// back to 0 is dropped. Entries are looked up by (target, game object). A dB value moves in a straight line in linear gain (Wwise's transitions
    /// on volumes, docs/audio.md "Pause and speed"): a reset from −96 dB over 3 s is at −6 dB half way, not at −48 dB. The others move straight in
    /// their units.
    /// </summary>
    sealed class OffsetTable(bool decibels = false)
    {
        /// <summary>
        /// One offset: its value now, where its fade started and goes, the fade's length and the time faded so far (seconds).
        /// </summary>
        sealed class Entry
        {
            /// <summary>
            /// The offset now, the value its fade started from, its target, the fade's length and the time faded so far (seconds).
            /// </summary>
            public float Value, Start, Target, Seconds, Elapsed;
        }

        /// <summary>
        /// The offsets by (target node or bus, game object; 0 for the global scope).
        /// </summary>
        readonly Dictionary<(uint Id, ulong Object), Entry> _entries = [];

        /// <summary>
        /// Whether no offset is set.
        /// </summary>
        public bool IsEmpty => _entries.Count == 0;

        /// <summary>
        /// Fades the offset of (<paramref name="id"/>, <paramref name="gameObject"/>) to <paramref name="target"/> over <paramref name="seconds"/>.
        /// </summary>
        public void Set(uint id, ulong gameObject, float target, float seconds)
        {
            if (!_entries.TryGetValue((id, gameObject), out var e)) _entries[(id, gameObject)] = e = new Entry();
            Start(e, target, seconds);
        }

        /// <summary>
        /// Fades a set offset back to 0 over <paramref name="seconds"/>; nothing for one never set.
        /// </summary>
        public void Reset(uint id, ulong gameObject, float seconds)
        {
            if (_entries.TryGetValue((id, gameObject), out var e)) Start(e, 0, seconds);
        }

        /// <summary>
        /// Starts a fade from the entry's value now (at once when <paramref name="seconds"/> is 0 or less).
        /// </summary>
        static void Start(Entry e, float target, float seconds)
        {
            (e.Start, e.Target, e.Seconds, e.Elapsed) = (e.Value, target, Math.Max(seconds, 0), 0);
            if (seconds <= 0) e.Value = target;
        }

        /// <summary>
        /// Moves every fading offset on by <paramref name="dt"/> seconds (a dB one straight in linear gain) and drops those back at 0.
        /// </summary>
        public void Advance(double dt)
        {
            if (_entries.Count == 0) return;
            List<(uint, ulong)>? gone = null;
            foreach (var (key, e) in _entries)
            {
                if (e.Value != e.Target)
                {
                    e.Elapsed += (float)dt;
                    float t = e.Seconds > 0 ? Math.Min(e.Elapsed / e.Seconds, 1) : 1;
                    e.Value = t >= 1 ? e.Target
                        : decibels ? GainToDb(float.Lerp(WwiseCurves.DbToGain(e.Start), WwiseCurves.DbToGain(e.Target), t))
                        : float.Lerp(e.Start, e.Target, t);
                }
                if (e.Value == 0 && e.Target == 0) (gone ??= []).Add(key);
            }
            if (gone is not null) foreach (var key in gone) _entries.Remove(key);
        }

        /// <summary>
        /// The global offset plus the one for the object.
        /// </summary>
        public float Get(uint id, ulong gameObject)
        {
            if (_entries.Count == 0) return 0;
            float sum = _entries.TryGetValue((id, 0), out var g) ? g.Value : 0;
            if (gameObject != 0 && _entries.TryGetValue((id, gameObject), out var o)) sum += o.Value;
            return sum;
        }

        /// <summary>
        /// Drops the object's offsets (it was unregistered).
        /// </summary>
        public void ForgetObject(ulong gameObject)
        {
            foreach (var key in _entries.Keys.Where(k => k.Object == gameObject).ToList()) _entries.Remove(key);
        }

        /// <summary>
        /// A linear gain in dB, −96 (the floor <see cref="WwiseCurves.DbToGain"/> reads as silence) at or below 10^(−96/20).
        /// </summary>
        static float GainToDb(float gain) => gain <= 1.5849e-5f ? -96 : 20 * MathF.Log10(gain);
    }

    /// <summary>
    /// An auxiliary bus: the stereo input its senders add to, the bypass state of its effect slots and the reverbs it runs.
    /// </summary>
    sealed class AuxBusState(WwiseBus bus)
    {
        /// <summary>
        /// The bus record: its effect slots, volume, parent and RTPCs.
        /// </summary>
        public readonly WwiseBus Bus = bus;
        /// <summary>
        /// Bit n set: the effect in slot n is bypassed (starts as the bank's, changed by bypass actions).
        /// </summary>
        public byte Bypass = bus.FxBypass;
        /// <summary>
        /// The reverb of each RoomVerb effect slot, by slot index, made on first use.
        /// </summary>
        public readonly Dictionary<byte, RoomReverb> Units = [];
        /// <summary>
        /// The stereo input of the block being mixed (interleaved), grown as needed; cleared after each block that received.
        /// </summary>
        public float[] Input = [];
        /// <summary>
        /// Senders added to <see cref="Input"/> in the block being mixed.
        /// </summary>
        public bool Received;

        /// <summary>
        /// The input buffer for a sender this block (at least <paramref name="frames"/> stereo frames), marked as received.
        /// </summary>
        public float[] Prepare(int frames)
        {
            if (Input.Length < frames * 2) Input = new float[frames * 2];
            Received = true;
            return Input;
        }
    }

    /// <summary>
    /// The indoor auxiliary bus (two RoomVerb slots: 0 the big room, 1 the small one) and the outdoor one (slot 0), both bypassed until a bank event
    /// enables them.
    /// </summary>
    public const uint IndoorAuxBus = 0x23F4785D, OutdoorAuxBus = 0x5E1D37D4;

    /// <summary>
    /// Bypasses or enables effect slots of an auxiliary bus directly, as the banks' bypass actions do (<c>Enter_Building</c>, a biome's <c>Enter_</c>
    /// event): <paramref name="slots"/> bits 0-3 are the slots, bit 4 means all. A bypassed reverb takes no more input and its tail rings out.
    /// </summary>
    public void SetAuxBypass(uint auxBus, bool bypass, byte slots = 0x10) => _commands.Enqueue(() => ApplyBypass(auxBus, bypass, slots));

    /// <summary>
    /// The state of an auxiliary bus (made on first use); null when the bus is not a known aux bus (its bank is not loaded).
    /// </summary>
    AuxBusState? AuxBus(uint id)
    {
        if (_auxBuses.TryGetValue(id, out var state)) return state;
        if (!_objects.TryGetValue(id, out var o) || o is not WwiseBus { IsAux: true } bus) return null;
        return _auxBuses[id] = new AuxBusState(bus);
    }

    /// <summary>
    /// A bypass action on a bus: sets or clears the bypass of the slots in <paramref name="mask"/> (bits 0-3 are the slots, bit 4 means all of them).
    /// </summary>
    void ApplyBypass(uint busId, bool bypass, byte mask)
    {
        if (AuxBus(busId) is not { } aux) return;
        byte slots = (byte)((mask & 0x10) != 0 ? 0x0F : mask & 0x0F);
        aux.Bypass = (byte)(bypass ? aux.Bypass | slots : aux.Bypass & ~slots);
    }

    /// <summary>
    /// The linear gain of a bus and its parents: bus volume (dB), the offsets set by actions and the volume curves on the user volume parameters
    /// (the sliders, which store gain - 1 as the RTPC volume curves).
    /// </summary>
    float BusGain(uint busId, ulong gameObject)
    {
        float db = 0, linear = 1;
        for (int guard = 0; busId != 0 && guard < 16 && _objects.TryGetValue(busId, out var o) && o is WwiseBus bus; guard++)
        {
            db += bus.Properties.Float(WwisePropertyId.BusVolume) + _busVolumes.Get(bus.Id, gameObject);
            foreach (var rtpc in bus.Rtpcs)
            {
                if (rtpc.ParameterId != WwisePropertyId.Volume) continue;
                float y = WwiseCurves.Evaluate(rtpc.Points, Parameter(rtpc.GameParameterId, gameObject));
                if (rtpc.Scaling == 2) linear *= Math.Max(0, 1 + y);
                else db += y;
            }
            busId = bus.ParentBusId;
        }
        return WwiseCurves.DbToGain(db) * linear;
    }

    /// <summary>
    /// Runs the effects of every auxiliary bus that received sound (or whose tail still rings) and adds their output to <paramref name="mix"/>.
    /// Only RoomVerb is implemented; the bus's other effects (none on an aux bus in Kenshi) are skipped. Slots run side by side, not in series.
    /// </summary>
    void MixAuxBuses(float[] mix, int frames)
    {
        foreach (var aux in _auxBuses.Values)
        {
            float busGain = BusGain(aux.Bus.Id, 0);
            foreach (var slot in aux.Bus.Effects)
            {
                if (!_objects.TryGetValue(slot.EffectId, out var o) || o is not WwiseEffect { IsRoomVerb: true } effect) continue;
                if (!aux.Units.TryGetValue(slot.Index, out var unit)) aux.Units[slot.Index] = unit = new RoomReverb(_outputRate);
                bool enabled = (aux.Bypass & (1 << slot.Index)) == 0;
                unit.Enabled = enabled;
                if (!aux.Received && unit.TailDead) continue;
                float level = ConfigureReverb(unit, effect);
                // A block with no senders still runs the tail through; the input is silence (cleared after each block).
                if (aux.Input.Length < frames * 2) aux.Input = new float[frames * 2];
                unit.Process(aux.Input, mix, frames, level * busGain);
            }
            if (aux.Received) Array.Clear(aux.Input, 0, frames * 2);
            aux.Received = false;
        }
    }

    /// <summary>
    /// Sets a reverb from its effect's decoded values and the current game parameters, and returns the wet level (linear). The level is the
    /// root-sum-square of the effect's two output levels (words 18 and 19, which one is the early reflections and which the late reverb is
    /// <b>Unknown</b>), each scaled by the RTPC curve on its parameter (60 and 62, Observed: the outdoor room fades with the camera's height); the
    /// RTPC on parameter 16 (25 to 175 for the outdoor room) is read as the pre-delay in ms.
    /// </summary>
    float ConfigureReverb(RoomReverb unit, WwiseEffect effect)
    {
        float preDelay = 0, gain18 = 1, gain19 = 1;
        foreach (var rtpc in effect.Rtpcs)
        {
            float y = WwiseCurves.Evaluate(rtpc.Points, Parameter(rtpc.GameParameterId));
            switch (rtpc.ParameterId)
            {
                case 16:
                    preDelay = y;
                    break;
                case 60:
                    gain18 *= rtpc.Scaling == 2 ? Math.Max(0, 1 + y) : y;
                    break;
                case 62:
                    gain19 *= rtpc.Scaling == 2 ? Math.Max(0, 1 + y) : y;
                    break;
            }
        }
        unit.Configure(effect.RoomVerbValue(0), effect.RoomVerbValue(1), effect.RoomVerbValue(2), effect.RoomVerbValue(3), preDelay);
        float a = WwiseCurves.DbToGain(effect.RoomVerbValue(18)) * gain18, b = WwiseCurves.DbToGain(effect.RoomVerbValue(19)) * gain19;
        return MathF.Sqrt(a * a + b * b);
    }
}
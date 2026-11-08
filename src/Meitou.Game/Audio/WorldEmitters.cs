using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Audio;

/// <summary>
/// The game's point emitters (docs/audio.md "Point emitters", from the exe's emitter class <c>FUN_140259a80</c> and its update <c>FUN_140258cb0</c>): a looping sound attached to a
/// building (an AMBIENT_SOUND record) that plays while the camera eye is within the sound's <see cref="AmbientSound.Range"/>. Each emitter is a game object of its own, placed
/// at the emitter, with the <c>Emitters</c> switch set to its name and the <c>Intensity</c> parameter, started and stopped by <c>Start_Emitter</c> / <c>Stop_Emitter</c>.
/// The game has no cap on how many play at once: the range is the only limit.
/// </summary>
public sealed class WorldEmitters(WwisePlayer player)
{
    /// <summary>The first game object id of the emitters (the exe counts up from 0xFFFFF; Meitou's other sound sources keep below this one).</summary>
    public const ulong FirstObject = 1UL << 32;

    sealed class Emitter(string key, AmbientSound sound, Vector3 position)
    {
        public readonly string Key = key;
        public readonly AmbientSound Sound = sound;
        public Vector3 Position = position;
        /// <summary>The game object; 0 until the first start registers it (the game registers lazily).</summary>
        public ulong Object;
        public bool Started;
        /// <summary>The eye is beyond the range (the game starts every emitter far away, then updates it once on creation).</summary>
        public bool Far = true;
        public bool Powered;
        /// <summary>The last <c>Intensity</c> the game decided on (the base intensity at first, sent or not).</summary>
        public float Level = sound.Intensity * 100;
    }

    readonly Dictionary<string, Emitter> emitters = new(StringComparer.Ordinal);
    ulong next = FirstObject;

    /// <summary>Emitters that exist (their building is complete and standing).</summary>
    public int Count => emitters.Count;

    /// <summary>Emitters playing now.</summary>
    public int Started { get; private set; }

    /// <summary>The distance in units from the eye to the nearest emitter at the last <see cref="Update"/>, and its name (infinity and null with none).</summary>
    public float NearestDistance { get; private set; } = float.PositiveInfinity;

    public string? NearestName { get; private set; }

    /// <summary>
    /// Adds the emitter (replacing the one with the same key). Nothing sounds until <see cref="Update"/> finds the eye in range. A <paramref name="powered"/> emitter starts out powered with
    /// <paramref name="efficiency"/> in its <c>Intensity</c> (a building nothing calls <see cref="SetPower"/> for: the level's, which the simulation does not run).
    /// </summary>
    public void Add(PlacedEmitter placed, bool powered = false, float efficiency = 0)
    {
        Remove(placed.Key);
        var e = new Emitter(placed.Key, placed.Sound, placed.Position);
        if (powered)
        {
            e.Powered = true;
            e.Level = efficiency * placed.Sound.EfficiencyMultiplier * 100 + placed.Sound.Intensity * 100;
        }
        emitters[placed.Key] = e;
    }

    /// <summary>Removes the emitter: <c>Stop_Emitter</c> if it plays, then its object is unregistered at once, which cuts what is left of the sound (the game's <c>FUN_140258260</c>).</summary>
    public void Remove(string key)
    {
        if (!emitters.Remove(key, out var e)) return;
        if (e.Started)
        {
            Started--;
            player.Post("Stop_Emitter", e.Object);
        }
        e.Started = false;
        if (e.Object != 0) player.UnregisterObject(e.Object);
    }

    public bool Contains(string key) => emitters.ContainsKey(key);

    /// <summary>Moves an emitter that follows something (an effect's node, <c>FUN_140258120</c>): the next <see cref="Update"/> measures the range from the new place, and a registered object is placed there.</summary>
    public void Move(string key, Vector3 position)
    {
        if (!emitters.TryGetValue(key, out var e)) return;
        e.Position = position;
        if (e.Object != 0) player.SetPosition(e.Object, position);
    }

    public void Clear()
    {
        foreach (string key in emitters.Keys.ToArray()) Remove(key);
    }

    /// <summary>
    /// Once a frame with the camera eye: an emitter whose squared distance is beyond its squared range stops; one within it starts if it is not running and either does not need
    /// power or has been told it is powered (<see cref="SetPower"/>).
    /// </summary>
    public void Update(Vector3 eye)
    {
        float nearest = float.PositiveInfinity;
        NearestName = null;
        foreach (var e in emitters.Values)
        {
            float range = e.Sound.Range;
            float d2 = player.HeardDistanceSquared(e.Position, eye);
            if (d2 < nearest)
            {
                nearest = d2;
                NearestName = e.Sound.Emitter;
            }
            e.Far = d2 > range * range;
            if (e.Far)
            {
                if (e.Started) Stop(e);
            }
            else if (!e.Started && (e.Powered || !e.Sound.NeedsPower))
                Start(e);
        }
        NearestDistance = MathF.Sqrt(nearest);
    }

    /// <summary>
    /// A machine's state (the game's <c>FUN_140258c20</c>, called each frame by production buildings): <paramref name="powered"/> starts or stops an emitter that needs power,
    /// and the <c>Intensity</c> becomes <c>efficiency × efficiency multiplier × 100 + intensity × 100</c> when it changed. Only for an emitter within range, as the game does.
    /// The host calls it each frame for the machines of the player's buildings (docs/audio.md "Point emitters").
    /// </summary>
    public void SetPower(string key, float efficiency, bool powered)
    {
        if (!emitters.TryGetValue(key, out var e)) return;
        e.Powered = powered;
        if (e.Far) return;
        float level = efficiency * e.Sound.EfficiencyMultiplier * 100 + e.Sound.Intensity * 100;
        if (e.Sound.NeedsPower)
        {
            if (powered) Start(e);
            else Stop(e);
        }
        if (e.Object != 0 && e.Level != level)
        {
            player.SetParameter("Intensity", level, e.Object);
            e.Level = level;
        }
    }

    void Start(Emitter e)
    {
        if (!e.Started)
        {
            if (e.Object == 0) Register(e);
            player.Post("Start_Emitter", e.Object);
            Started++;
        }
        e.Started = true;
    }

    void Stop(Emitter e)
    {
        if (e.Started)
        {
            player.Post("Stop_Emitter", e.Object);
            Started--;
        }
        e.Started = false;
    }

    void Register(Emitter e)
    {
        e.Object = next++;
        player.RegisterObject(e.Object);
        player.SetSwitch("Emitters", e.Sound.Emitter, e.Object);
        player.SetPosition(e.Object, e.Position);
        // The game sends the base intensity only when it is not 0: an emitter with none hears the global Intensity (the wind's) until a machine sets its own.
        if (e.Level != 0) player.SetParameter("Intensity", e.Level, e.Object);
    }
}

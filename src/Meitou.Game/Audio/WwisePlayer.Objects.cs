using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Audio;

namespace Meitou.Audio;

/// <summary>
/// Game objects and the listener (docs/audio.md#3d-positioning). A game object is a number the host picks (the game uses 100 for the music and
/// ambience, 110 for the interface, a counter for emitters); sounds are posted <i>on</i> an object, take their position from it and die with it.
/// Object 0 is the default object: never placed, so its sounds are heard at the listener.
/// </summary>
public sealed partial class WwisePlayer
{
    // The objects the host registered; positions are written from the host's threads and read by the mixer (a tiny lock), the rest is the audio thread's.
    readonly ConcurrentDictionary<ulong, GameObjectState> gameObjects = new();
    readonly object listenerGate = new();
    Listener listener = Listener.Default;

    /// <summary>The object an action's sounds are made on while <see cref="Run"/> plays it (instances take it from their parent otherwise).</summary>
    ulong postingObject;

    sealed class GameObjectState
    {
        readonly object gate = new();
        Vector3 position, front;
        bool placed;

        /// <summary>Per-object game parameter values (override the global ones) and switch values: the audio thread's.</summary>
        public readonly Dictionary<uint, float> Parameters = [];
        public readonly Dictionary<uint, uint> Switches = [];

        public void Place(Vector3 position, Vector3 front)
        {
            lock (gate)
            {
                this.position = position;
                this.front = front;
                placed = true;
            }
        }

        public bool TryGetPlace(out Vector3 position, out Vector3 front)
        {
            lock (gate)
            {
                position = this.position;
                front = this.front;
                return placed;
            }
        }
    }

    GameObjectState ObjectState(ulong id) => gameObjects.GetOrAdd(id, _ => new GameObjectState());

    /// <summary>
    /// Makes the object known (a game object exists from its first use anyway; this is for hosts that want to say so). Registering an object that
    /// exists does nothing.
    /// </summary>
    public void RegisterObject(ulong id) => ObjectState(id);

    /// <summary>
    /// Forgets the object and ends everything playing on it at once (what Wwise does when a game object is unregistered); its position, switches and
    /// parameters are gone. A later use registers it afresh.
    /// </summary>
    public void UnregisterObject(ulong id)
    {
        gameObjects.TryRemove(id, out _);
        commands.Enqueue(() =>
        {
            foreach (var r in roots)
                if (r.GameObject == id) r.End();
            volumeOffsets.ForgetObject(id);
            pitchOffsets.ForgetObject(id);
            lowPassOffsets.ForgetObject(id);
            busVolumes.ForgetObject(id);
        });
    }

    /// <summary>
    /// Places the object in the world (the units of the attenuation curves: the game's Ogre units). <paramref name="front"/> is the direction the
    /// object faces for attenuation cones; zero (the game's choice) means it has none. Cheap: no command is queued.
    /// </summary>
    public void SetPosition(ulong id, Vector3 position, Vector3 front = default) => ObjectState(id).Place(position, front);

    /// <summary>
    /// The listener: where the ears are and the way they face (<paramref name="front"/>, <paramref name="up"/> in the same world as the objects).
    /// </summary>
    public void SetListener(Vector3 position, Vector3 front, Vector3 up)
    {
        if (front.LengthSquared() < 1e-12f || up.LengthSquared() < 1e-12f) return;
        lock (listenerGate) listener = new Listener(position, front, up);
    }

    Listener CurrentListener()
    {
        lock (listenerGate) return listener;
    }

    /// <summary>Posts an event on a game object: its sounds are placed at the object and stopped by stop actions restricted to the object.</summary>
    public void Post(string eventName, ulong gameObject) => Post(WwiseHash.Id(eventName), gameObject);

    public void Post(uint eventId, ulong gameObject)
    {
        ObjectState(gameObject);
        commands.Enqueue(() => PostNow(eventId, gameObject, expires: time + 10));
    }

    /// <summary>Sets a game parameter for one object (overriding the global value for the sounds playing on it).</summary>
    public void SetParameter(string name, float value, ulong gameObject) => SetParameter(WwiseHash.Id(name), value, gameObject);

    public void SetParameter(uint id, float value, ulong gameObject)
    {
        if (gameObject == 0)
        {
            SetParameter(id, value);
            return;
        }
        var state = ObjectState(gameObject);
        commands.Enqueue(() => state.Parameters[id] = value);
    }

    /// <summary>Sets a switch group for one object (<c>Emitters</c> on an emitter, <c>Indoors</c>); object 0 sets the global value, as <see cref="SetState(string, string)"/>.</summary>
    public void SetSwitch(string group, string value, ulong gameObject) => SetSwitch(WwiseHash.Id(group), WwiseHash.Id(value), gameObject);

    public void SetSwitch(uint group, uint value, ulong gameObject)
    {
        if (gameObject == 0)
        {
            SetState(group, value);
            return;
        }
        var state = ObjectState(gameObject);
        commands.Enqueue(() =>
        {
            if (state.Switches.TryGetValue(group, out var old) && old == value) return;
            state.Switches[group] = value;
            foreach (var r in roots)
                if (r.GameObject == gameObject) r.StateChanged(group);
        });
    }

    /// <summary>A game parameter's value for an object: its own if the object has one, else the global one.</summary>
    float Parameter(uint id, ulong gameObject) =>
        gameObject != 0 && gameObjects.TryGetValue(gameObject, out var state) && state.Parameters.TryGetValue(id, out var v) ? v : Parameter(id);

    /// <summary>A state or switch group's value for an object: its own switch if it has one, else the global value (0 when never set).</summary>
    bool TryGetGroupValue(uint group, ulong gameObject, out uint value)
    {
        if (gameObject != 0 && gameObjects.TryGetValue(gameObject, out var state) && state.Switches.TryGetValue(group, out value)) return true;
        return states.TryGetValue(group, out value);
    }
}

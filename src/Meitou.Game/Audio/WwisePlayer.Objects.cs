using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Audio;

namespace Meitou.Game.Audio;

/// <summary>
/// Game objects and the listener (docs/audio.md#3d-positioning). A game object is a number the host picks (the game uses 100 for the music and
/// ambience, 110 for the interface, a counter for emitters); sounds are posted <i>on</i> an object, take their position from it and die with it.
/// Object 0 is the default object: never placed, so its sounds are heard at the listener.
/// </summary>
public sealed partial class WwisePlayer
{
    /// <summary>
    /// The objects the host registered; positions are written from the host's threads and read by the mixer (a tiny lock), the rest is the
    /// audio thread's.
    /// </summary>
    readonly ConcurrentDictionary<ulong, GameObjectState> _gameObjects = new();
    /// <summary>
    /// Guards <see cref="_listener"/> (set from the host's threads, read by the mixer).
    /// </summary>
    readonly object _listenerGate = new();
    /// <summary>
    /// Where the ears are and the way they face (<see cref="SetListener"/>).
    /// </summary>
    Listener _listener = Listener.Default;

    /// <summary>
    /// The object an action's sounds are made on while <see cref="Run"/> plays it (instances take it from their parent otherwise).
    /// </summary>
    ulong _postingObject;

    /// <summary>
    /// One game object: its place (behind a lock, written from the host's threads) and its own game parameters and switches (the audio thread's).
    /// </summary>
    sealed class GameObjectState
    {
        /// <summary>
        /// Guards the place.
        /// </summary>
        readonly object _gate = new();
        /// <summary>
        /// Where the object is and the way it faces (zero: no facing).
        /// </summary>
        Vector3 _position, _front;
        /// <summary>
        /// Whether the object was ever placed; an unplaced one's sounds are heard at the listener.
        /// </summary>
        bool _placed;

        /// <summary>
        /// Per-object game parameter values (override the global ones) and switch values: the audio thread's.
        /// </summary>
        public readonly Dictionary<uint, float> Parameters = [];
        /// <summary>
        /// The object's own switch values by group id (override the global states for the sounds playing on it).
        /// </summary>
        public readonly Dictionary<uint, uint> Switches = [];

        /// <summary>
        /// Places the object (any thread).
        /// </summary>
        public void Place(Vector3 position, Vector3 front)
        {
            lock (_gate)
            {
                this._position = position;
                this._front = front;
                _placed = true;
            }
        }

        /// <summary>
        /// The object's place; false when it was never placed.
        /// </summary>
        public bool TryGetPlace(out Vector3 position, out Vector3 front)
        {
            lock (_gate)
            {
                position = this._position;
                front = this._front;
                return _placed;
            }
        }
    }

    /// <summary>
    /// The object's state, registering it on first use.
    /// </summary>
    GameObjectState ObjectState(ulong id) => _gameObjects.GetOrAdd(id, _ => new GameObjectState());

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
        _gameObjects.TryRemove(id, out _);
        _commands.Enqueue(() =>
        {
            foreach (var r in _roots)
                if (r.GameObject == id) r.End();
            _volumeOffsets.ForgetObject(id);
            _pitchOffsets.ForgetObject(id);
            _lowPassOffsets.ForgetObject(id);
            _busVolumes.ForgetObject(id);
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
        lock (_listenerGate) _listener = new Listener(position, front, up);
    }

    /// <summary>
    /// The listener as last set (under its lock).
    /// </summary>
    Listener CurrentListener()
    {
        lock (_listenerGate) return _listener;
    }

    /// <summary>
    /// Posts an event on a game object: its sounds are placed at the object and stopped by stop actions restricted to the object.
    /// </summary>
    public void Post(string eventName, ulong gameObject) => Post(WwiseHash.Id(eventName), gameObject);

    /// <summary>
    /// Posts an event by id on a game object (any thread; run at the next block, retried for 10 s while its bank loads).
    /// </summary>
    public void Post(uint eventId, ulong gameObject)
    {
        ObjectState(gameObject);
        _commands.Enqueue(() => PostNow(eventId, gameObject, expires: _time + 10));
    }

    /// <summary>
    /// Sets a game parameter for one object (overriding the global value for the sounds playing on it).
    /// </summary>
    public void SetParameter(string name, float value, ulong gameObject) => SetParameter(WwiseHash.Id(name), value, gameObject);

    /// <summary>
    /// Sets a game parameter by id for one object; object 0 sets the global value.
    /// </summary>
    public void SetParameter(uint id, float value, ulong gameObject)
    {
        if (gameObject == 0)
        {
            SetParameter(id, value);
            return;
        }
        var state = ObjectState(gameObject);
        _commands.Enqueue(() => state.Parameters[id] = value);
    }

    /// <summary>
    /// Sets a switch group for one object (<c>Emitters</c> on an emitter, <c>Indoors</c>); object 0 sets the global value, as
    /// <see cref="SetState(string, string)"/>.
    /// </summary>
    public void SetSwitch(string group, string value, ulong gameObject) => SetSwitch(WwiseHash.Id(group), WwiseHash.Id(value), gameObject);

    /// <summary>
    /// Sets a switch group by id for one object (the object's instances hear of a change); object 0 sets the global state.
    /// </summary>
    public void SetSwitch(uint group, uint value, ulong gameObject)
    {
        if (gameObject == 0)
        {
            SetState(group, value);
            return;
        }
        var state = ObjectState(gameObject);
        _commands.Enqueue(() =>
        {
            if (state.Switches.TryGetValue(group, out var old) && old == value) return;
            state.Switches[group] = value;
            foreach (var r in _roots)
                if (r.GameObject == gameObject) r.StateChanged(group);
        });
    }

    /// <summary>
    /// A game parameter's value for an object: its own if the object has one, else the global one.
    /// </summary>
    float Parameter(uint id, ulong gameObject) =>
        gameObject != 0 && _gameObjects.TryGetValue(gameObject, out var state) && state.Parameters.TryGetValue(id, out var v) ? v : Parameter(id);

    /// <summary>
    /// A state or switch group's value for an object: its own switch if it has one, else the global value (0 when never set).
    /// </summary>
    bool TryGetGroupValue(uint group, ulong gameObject, out uint value)
    {
        if (gameObject != 0 && _gameObjects.TryGetValue(gameObject, out var state) && state.Switches.TryGetValue(group, out value)) return true;
        return _states.TryGetValue(group, out value);
    }
}
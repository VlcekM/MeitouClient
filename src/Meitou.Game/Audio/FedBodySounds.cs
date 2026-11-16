using System.Diagnostics;
using System.Numerics;

namespace Meitou.Game.Audio;

/// <summary>
/// The sound of a body fed into a machine (docs/audio.md "Characters", <c>Task_FeedCorpseIntoMachine</c> slot 3 <c>FUN_1403567a0</c>): the exe
/// destroys the body (<c>FUN_140799af0</c>, which only queues it for deletion), then sets its <c>Armor_Worn</c> and posts <c>Character_Drop_Person</c>
/// on the body's own object, 1000 units. The object stays where the body lay until the deletion 5 game seconds later, whose destructor
/// (<c>FUN_140622a80</c>) stops everything on it and unregisters it. The body is gone from Meitou's snapshot when the sound is played, so each
/// body gets an object of its own at its last place, ended the same 5 game seconds later.
/// </summary>
sealed class FedBodySounds(WwisePlayer player)
{
    /// <summary>
    /// The first body object's id (above the turrets' range).
    /// </summary>
    public const ulong FirstObject = 1UL << 42;

    /// <summary>
    /// The deletion timer the destroy starts (<c>FUN_140799af0</c>: 5.0 in the world's dying map), counted down by the characters' frame time
    /// (<c>DAT_142133794</c> in <c>FUN_14079d8a0</c>).
    /// </summary>
    const float Linger = 5;

    /// <summary>
    /// The characters' frame time while the game speed is 0 (<c>DAT_142133794</c> is 0.01 when the frame time × speed is not above 0).
    /// </summary>
    const float StoppedStep = 0.01f;

    /// <summary>
    /// The bodies whose objects still sound, with the game seconds left before they end.
    /// </summary>
    readonly List<(ulong Object, float Left)> _bodies = [];

    /// <summary>
    /// Real time between frames.
    /// </summary>
    readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>
    /// The next body object's id.
    /// </summary>
    ulong _next = FirstObject;

    /// <summary>
    /// Sounds posted since the start, for the statistics.
    /// </summary>
    public int Posted { get; private set; }

    /// <summary>
    /// A body fed into a machine at <paramref name="at"/> with its body armour <paramref name="armourMaterial"/>: <c>Armor_Worn</c> is set on its
    /// object whatever the distance, the sound is posted within 1000 units of <paramref name="eye"/>.
    /// </summary>
    public void Post(Vector3 at, int armourMaterial, Vector3 eye)
    {
        ulong id = _next++;
        player.SetPosition(id, at);
        player.SetSwitch("Armor_Worn", CharacterSounds.ArmourName(armourMaterial), id);
        _bodies.Add((id, Linger));

        if (player.HeardDistanceSquared(at, eye) >= CharacterSounds.EventLimit) return;
        player.Post("Character_Drop_Person", id);
        Posted++;
    }

    /// <summary>
    /// Once a frame: the timers count down by the real time since the last frame × <paramref name="speed"/> (0.01 when that is not above 0), and an
    /// object whose time is up ends (everything on it stops).
    /// </summary>
    public void Update(double speed)
    {
        double real = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        if (_bodies.Count == 0) return;

        float step = (float)(real * speed);
        if (step <= 0) step = StoppedStep;

        for (int i = _bodies.Count - 1; i >= 0; i--)
        {
            var (id, left) = _bodies[i];
            left -= step;
            if (left > 0)
            {
                _bodies[i] = (id, left);
                continue;
            }

            player.UnregisterObject(id);
            _bodies.RemoveAt(i);
        }
    }

    /// <summary>
    /// Ends every body object (a new world, the main menu).
    /// </summary>
    public void Clear()
    {
        foreach (var (id, _) in _bodies) player.UnregisterObject(id);
        _bodies.Clear();
    }
}

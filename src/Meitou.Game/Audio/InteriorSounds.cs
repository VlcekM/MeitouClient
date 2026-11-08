namespace Meitou.Audio;

/// <summary>The building under the camera's pivot, as the interior ambience needs it (<see cref="Meitou.Data.World.InteriorVolume"/>'s fields).</summary>
/// <param name="Id">The building's instance id: what tells one building from the next.</param>
/// <param name="Ambience">The building's <c>interior ambience</c>: 0 None, 1 Metal, 2 Stone, 3 Wicker, 4 Wood.</param>
/// <param name="Integrity">The building's <c>interior integrity</c> (0..100).</param>
/// <param name="Cavernous">The building's <c>cavernous</c> flag.</param>
public readonly record struct InteriorHit(string Id, int Ambience, float Integrity, bool Cavernous);

/// <summary>
/// The game's interior ambience (docs/audio.md "Interiors", from the exe's camera update <c>FUN_1406b0140</c>): when the building under the camera's pivot changes, the
/// old one ends with <c>Leave_Building</c> and a new one with an <c>interior ambience</c> sets the <c>Indoors</c> switch and <c>Building_Integrity</c> and starts
/// <c>Enter_Building</c> (<c>Enter_Building_Big</c> when cavernous), all on the music object. The events also switch the indoor and outdoor reverbs.
/// </summary>
public sealed class InteriorSounds(WwisePlayer player)
{
    /// <summary>The <c>InteriorSound</c> enum's names by number; 0 (None) starts nothing.</summary>
    public static readonly string[] Names = ["None", "Metal", "Stone", "Wicker", "Wood"];

    string? last;

    /// <summary>The <c>Indoors</c> switch value last set (null before any building with an ambience was entered).</summary>
    public string? Indoors { get; private set; }

    /// <summary>The ambience is on: the building under the pivot has an <c>interior ambience</c> and its event was posted.</summary>
    public bool Playing { get; private set; }

    /// <summary>Whether the pivot is over a building with an indoor volume (whatever its ambience).</summary>
    public bool Inside => last is not null;

    /// <summary>
    /// Once a frame with the building under the pivot, or null (outdoors, no building loaded there). Nothing happens while the building stays the same.
    /// A building without an ambience still counts as a building: leaving it posts <c>Leave_Building</c> as leaving any other does.
    /// </summary>
    public void Update(InteriorHit? hit)
    {
        string? id = hit?.Id;
        if (id == last) return;
        if (last is not null) player.Post("Leave_Building", AudioSystem.MusicObject);
        Playing = false;
        if (hit is { Ambience: > 0 and < 5 } building)
        {
            Indoors = Names[building.Ambience];
            player.SetSwitch("Indoors", Indoors, AudioSystem.MusicObject);
            player.SetParameter("Building_Integrity", building.Integrity, AudioSystem.MusicObject);
            player.Post(building.Cavernous ? "Enter_Building_Big" : "Enter_Building", AudioSystem.MusicObject);
            Playing = true;
        }
        last = id;
    }

    /// <summary>Ends the interior ambience and forgets the building (the main menu: the next game's first update enters its building afresh).</summary>
    public void Silence() => Update(null);
}

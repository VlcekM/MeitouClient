using System.Diagnostics;
using Meitou.Data.World;

namespace Meitou.Game.Audio;

/// <summary>
/// The game's music rules (docs/audio.md "Music", from the exe's audio manager): the music of the region under the camera's pivot is only
/// <i>remembered</i> (with the <c>Biomes</c> switch set); nothing plays until a real-time timer fires, then <c>Music_&lt;Name&gt;_Play</c> is
/// posted, again every 0.8 to 1.4 times <c>Music frequency</c>. Biome music banks load on demand and unload 60 s after they stop being the
/// current one. The main theme (title screen) has an API but the viewer does not call it.
/// </summary>
public sealed class MusicDirector
{
    /// <summary>
    /// The exe's names of the <c>music</c> indices 0..3 (<see cref="BiomeMusic"/>'s spellings differ); 4 is no music.
    /// </summary>
    static readonly string[] Names = ["Dunes", "Savannah", "Swamplands", "Tarsands"];

    const string MainThemeBank = "MT_Music_Bank";
    const double UnloadAfter = 60;

    readonly WwisePlayer _player;
    readonly Func<double> _clock;
    readonly Random _random;
    // The biome music banks asked for, with the time each was last the current one.
    readonly Dictionary<string, double> _lastCurrent = new(StringComparer.OrdinalIgnoreCase);
    float _frequencyMinutes;
    double _deadline;
    int _index = -1;
    string? _remembered;
    string? _pendingBank;
    bool _firstChange = true;

    /// <summary>
    /// The music of the region the camera is over, null for none.
    /// </summary>
    public string? Biome { get; private set; }

    /// <summary>
    /// True from <see cref="StartMainTheme"/> until <see cref="OnCreateOrLoadGame"/>: the timer does not run (the game's title screen).
    /// </summary>
    public bool TitleScreen { get; private set; }

    /// <summary>
    /// <c>Music volume</c> (0..1); music is only posted when it is above 0.
    /// </summary>
    public float Volume { get; private set; }

    /// <summary>
    /// The event the timer would post now (<c>Music_Dunes_Play</c>), null when no biome music is remembered.
    /// </summary>
    public string? Remembered => _remembered;

    /// <summary>
    /// Real seconds until the timer next fires (negative once due, e.g. during the title screen).
    /// </summary>
    public double SecondsToNext => _deadline - _clock();

    /// <summary>
    /// <paramref name="frequencyMinutes"/>: settings.cfg <c>Music frequency</c> (default 5); <paramref name="volume"/>: <c>Music volume</c> (default
    /// 0.5), sent as the game parameter <c>User_Volume_Music</c> × 100. <paramref name="clock"/>: real seconds (default a stopwatch started now);
    /// the first deadline is its now + the frequency.
    /// </summary>
    public MusicDirector(WwisePlayer player, float frequencyMinutes, float volume, Func<double>? clock = null, Random? random = null)
    {
        this._player = player;
        if (clock is null)
        {
            var watch = Stopwatch.StartNew();
            clock = () => watch.Elapsed.TotalSeconds;
        }
        this._clock = clock;
        this._random = random ?? new Random();
        this._frequencyMinutes = Math.Max(0.1f, frequencyMinutes);
        _deadline = clock() + Interval();
        SetVolume(volume);
    }

    double Interval() => _frequencyMinutes * 60;

    public void SetVolume(float volume)
    {
        Volume = Math.Clamp(volume, 0, 1);
        _player.SetParameter("User_Volume_Music", Volume * 100);
    }

    /// <summary>
    /// A changed <c>Music frequency</c> moves the deadline earlier when now + the new interval is sooner, never later.
    /// </summary>
    public void SetFrequency(float minutes)
    {
        _frequencyMinutes = Math.Max(0.1f, minutes);
        _deadline = Math.Min(_deadline, _clock() + Interval());
    }

    /// <summary>
    /// The title screen's music: loads the main theme's bank and posts <c>Boot_Up_Game</c> (the event waits for the bank).
    /// </summary>
    public void StartMainTheme()
    {
        TitleScreen = true;
        _player.LoadBank(MainThemeBank);
        _player.Post("Boot_Up_Game", AudioSystem.MusicObject);
    }

    /// <summary>
    /// A new game, load or import starts: the title screen is gone and <c>Create_or_Load_Game</c> is posted on the GUI object. Its stop of the main
    /// theme is limited to that object (docs/audio.md#music), so the theme, started on the music object, plays on until a biome's <c>Enter_</c> event
    /// stops it.
    /// </summary>
    public void OnCreateOrLoadGame()
    {
        TitleScreen = false;
        _player.Post("Create_or_Load_Game", AudioSystem.GuiObject);
    }

    /// <summary>
    /// Each frame: <paramref name="music"/> is the <c>music</c> of the region under the camera's pivot, null when no region was found (the game
    /// then skips the biome step, not the timer). The timer is skipped while <see cref="TitleScreen"/>.
    /// </summary>
    public void Update(BiomeMusic? music)
    {
        double now = _clock();
        if (music is { } m) ChangeBiome((int)m, now);
        CompleteLoad();
        if (!TitleScreen && now >= _deadline)
        {
            _deadline = now + Interval() * (0.8 + 0.6 * _random.NextDouble());
            // Moves on whether or not anything is posted; nothing checks whether music still plays, and no Stop is ever posted.
            if (_remembered is not null && Volume > 0) _player.Post(_remembered, AudioSystem.MusicObject);
        }
        if (_remembered is not null) _lastCurrent[BankOf(Biome!)] = now;
        UnloadStale(now);
    }

    static string BankOf(string name) => $"{name}_Music_Bank";

    /// <summary>
    /// The game's biome-music step (an index outside 0..4 counts as 4); does nothing when the index is the same as last time.
    /// </summary>
    void ChangeBiome(int next, double now)
    {
        if (next is < 0 or > 4) next = 4;
        if (next == _index) return;
        _index = next;
        // The first call after start drops the main theme's bank, whatever biome it is (a no-op when it never loaded).
        if (_firstChange)
        {
            _firstChange = false;
            _player.UnloadBank(MainThemeBank);
        }
        if (next == 4)
        {
            Biome = null;
            _remembered = null;
            _pendingBank = null;
            return;
        }
        string bank = BankOf(Names[next]);
        _lastCurrent[bank] = now;
        if (_player.IsBankLoaded(bank)) Remember(Names[next]);
        else
        {
            // Queued: the switch and the event are set when the bank has loaded.
            Biome = null;
            _remembered = null;
            _pendingBank = bank;
            _player.LoadBank(bank);
        }
    }

    void CompleteLoad()
    {
        if (_pendingBank is null || !_player.IsBankLoaded(_pendingBank)) return;
        if (_index is >= 0 and <= 3 && BankOf(Names[_index]) == _pendingBank) Remember(Names[_index]);
        _pendingBank = null;
    }

    void Remember(string name)
    {
        Biome = name;
        _remembered = $"Music_{name}_Play";
        _player.SetSwitch("Biomes", name);
    }

    /// <summary>
    /// Unloads biome music banks 60 s after they were last current, when no load is pending.
    /// </summary>
    void UnloadStale(double now)
    {
        if (_pendingBank is not null) return;
        foreach (var (bank, last) in _lastCurrent.ToArray())
        {
            if (now - last < UnloadAfter || !_player.IsBankLoaded(bank)) continue;
            _lastCurrent.Remove(bank);
            _player.UnloadBank(bank);
        }
    }
}
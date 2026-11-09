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

    /// <summary>
    /// The main theme's bank, loaded by <see cref="StartMainTheme"/> and dropped at the first biome change.
    /// </summary>
    const string MainThemeBank = "MT_Music_Bank";
    /// <summary>
    /// Real seconds after a biome music bank was last current that it is unloaded.
    /// </summary>
    const double UnloadAfter = 60;

    /// <summary>
    /// The bank player the music events go to.
    /// </summary>
    readonly WwisePlayer _player;
    /// <summary>
    /// Real seconds, the timer's clock.
    /// </summary>
    readonly Func<double> _clock;
    /// <summary>
    /// Draws the factor (0.8..1.4) on each interval after the first.
    /// </summary>
    readonly Random _random;
    /// <summary>
    /// The biome music banks asked for, with the time each was last the current one. Unloaded by <see cref="UnloadStale"/>.
    /// </summary>
    readonly Dictionary<string, double> _lastCurrent = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// <c>Music frequency</c> in minutes, at least 0.1: the timer's base interval.
    /// </summary>
    float _frequencyMinutes;
    /// <summary>
    /// The real time (of <see cref="_clock"/>) the timer next fires.
    /// </summary>
    double _deadline;
    /// <summary>
    /// The <c>music</c> index of the last biome step (0..3 a biome, 4 none); -1 before the first.
    /// </summary>
    int _index = -1;
    /// <summary>
    /// The event the timer posts (<c>Music_&lt;Name&gt;_Play</c>); null when no biome music is remembered.
    /// </summary>
    string? _remembered;
    /// <summary>
    /// The biome music bank loading for the current biome; its switch and event are set once it has loaded.
    /// </summary>
    string? _pendingBank;
    /// <summary>
    /// Whether no biome step has run yet (the first drops the main theme's bank).
    /// </summary>
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

    /// <summary>
    /// The timer's base interval in real seconds.
    /// </summary>
    double Interval() => _frequencyMinutes * 60;

    /// <summary>
    /// Sets <c>Music volume</c> (clamped to 0..1) and sends it as <c>User_Volume_Music</c> × 100.
    /// </summary>
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

    /// <summary>
    /// A biome music bank's name, <c>&lt;Name&gt;_Music_Bank</c>.
    /// </summary>
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

    /// <summary>
    /// Remembers the current biome's music once its pending bank has loaded, and clears the pending bank.
    /// </summary>
    void CompleteLoad()
    {
        if (_pendingBank is null || !_player.IsBankLoaded(_pendingBank)) return;
        if (_index is >= 0 and <= 3 && BankOf(Names[_index]) == _pendingBank) Remember(Names[_index]);
        _pendingBank = null;
    }

    /// <summary>
    /// Makes a biome's music the remembered one: sets <see cref="Biome"/>, the event the timer posts and the <c>Biomes</c> switch.
    /// </summary>
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
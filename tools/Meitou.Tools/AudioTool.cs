using System.Text;
using Meitou.Content;
using Meitou.Data.Audio;

/// <summary>
/// Surveys and dumps of the install's Wwise banks (docs/formats/wwise-bnk.md).
/// </summary>
static class AudioTool
{
    #region Run

    /// <summary>
    /// The install's Wwise bank folder, <c>data/audio</c>.
    /// </summary>
    static string AudioDirectory(GameInstall install) => Path.Combine(install.DataDirectory, "audio");

    /// <summary>
    /// <c>meitou-tools audio</c>: no argument surveys every bank, <c>render</c> mixes events into a WAV, otherwise dumps one bank (a file or a name
    /// in the audio folder) or, with <c>--wav</c> or <c>--decode</c>, exports or decodes its sounds. Returns the exit code.
    /// </summary>
    public static int Run(GameInstall install, string[] args)
    {
        if (args.Length == 0) return Survey(install);
        if (args[0] == "render") return Render(install, args[1..]);
        var path = File.Exists(args[0]) ? args[0]
                : Path.Combine(AudioDirectory(install), args[0].EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) ? args[0] : args[0] + ".bnk");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No bank {args[0]}");
            return 1;
        }
        var bank = WwiseBank.ReadFile(path);
        int wav = Array.IndexOf(args, "--wav");
        if (wav >= 0 && wav + 1 < args.Length)
            return ExportWav(install, bank, args[wav + 1], args.Length > wav + 2 ? args[wav + 2] : null);
        if (args.Contains("--decode")) return DecodeAll(install, bank);
        Dump(bank, args.Contains("--media"));
        return 0;
    }

    #endregion

    #region Render

    /// <summary>
    /// <c>audio render &lt;seconds&gt; &lt;out.wav&gt; [banks and events...] [name=value parameters] [group:state states]</c>: plays events through
    /// <see cref="Meitou.Game.Audio.WwisePlayer"/> offline (no sound card) and writes the mix; a word ending in <c>.bnk</c> loads that bank first. An
    /// event word may carry a place, an object and a time: <c>Footsteps@500,0,0~3</c> posts it at second 3 on a new game object at (500, 0, 0),
    /// <c>Stop_Emitter#1~9</c> on object 1001 (<c>#1</c> names it; <c>Start_Emitter@50,0,0#1</c> places it), while <c>Pause_Game~10</c> posts it on
    /// the music object (100) at second 10; <c>@listener=x,y,z[,fx,fy,fz]</c> puts the listener there (facing -z by default). Prints the level each
    /// second, with the left and right levels.
    /// </summary>
    static int Render(GameInstall install, string[] args)
    {
        if (args.Length < 2 || !double.TryParse(args[0], System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            Console.Error.WriteLine("audio render <seconds> <out.wav> [Bank.bnk ...] [Event[@x,y,z][~seconds] "
                + "or 8-digit hex id ...] [Parameter=value ...] [Group:State ...] [@listener=x,y,z[,fx,fy,fz]]");
            return 2;
        }

        if (Library(install) is not { } library) return 1;
        const int rate = Meitou.Game.Audio.AudioSystem.SampleRate;
        using var player = new Meitou.Game.Audio.WwisePlayer(library, AudioDirectory(install), rate);
        var loads = new List<Task>();
        var events = ReadRenderArguments(player, args[2..], loads);
        Task.WaitAll([.. loads]);
        events.Sort((x, y) => x.At.CompareTo(y.At));
        int total = (int)(seconds * rate);
        var all = new float[total * 2];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        MixRender(player, events, all, rate, total);
        WriteWav(args[1], all, 2, rate);
        Console.WriteLine($"{args[1]}: {seconds} s mixed in {watch.Elapsed.TotalSeconds:0.00} s");
        return 0;
    }

    /// <summary>
    /// Loads the banks, sets parameters, states and the listener, and returns the events to post while mixing.
    /// </summary>
    static List<(double At, string Event, ulong Object)> ReadRenderArguments(
        Meitou.Game.Audio.WwisePlayer player, string[] args, List<Task> loads)
    {
        var events = new List<(double At, string Event, ulong Object)>();
        ulong nextObject = 2000;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var a in args)
        {
            if (a.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)) loads.Add(player.LoadBank(a[..^4]));
            else if (a.StartsWith("@listener=", StringComparison.Ordinal))
            {
                var n = a["@listener=".Length..].Split(',').Select(x => float.Parse(x, ci)).ToArray();
                var front = n.Length >= 6 ? new System.Numerics.Vector3(n[3], n[4], n[5]) : -System.Numerics.Vector3.UnitZ;
                player.SetListener(new System.Numerics.Vector3(n[0], n[1], n[2]), front, System.Numerics.Vector3.UnitY);
            }
            else if (a.Split('=') is [var p, var v]) player.SetParameter(p, float.Parse(v, ci));
            else if (a.Split(':') is [var g, var s]) player.SetState(g, s);
            else
            {
                // An event by name, or by its id when it has no name we know (eight hex digits), with an optional place and time.
                double at = 0;
                string word = a;
                if (word.LastIndexOf('~') is var tilde and >= 0)
                {
                    at = double.Parse(word[(tilde + 1)..], ci);
                    word = word[..tilde];
                }

                ulong obj = Meitou.Game.Audio.AudioSystem.MusicObject;
                if (word.LastIndexOf('#') is var hash and >= 0)
                {
                    obj = 1000 + ulong.Parse(word[(hash + 1)..], ci);
                    word = word[..hash];
                }

                if (word.IndexOf('@') is var pos and >= 0)
                {
                    var n = word[(pos + 1)..].Split(',').Select(x => float.Parse(x, ci)).ToArray();
                    if (obj == Meitou.Game.Audio.AudioSystem.MusicObject) obj = nextObject++;
                    player.SetPosition(obj, new System.Numerics.Vector3(n[0], n[1], n[2]));
                    word = word[..pos];
                }

                events.Add((at, word, obj));
            }
        }

        return events;
    }

    /// <summary>
    /// Mixes <paramref name="events"/> (each posted at its time in seconds) through <paramref name="player"/> into <paramref name="all"/>, one output
    /// block at a time, <paramref name="total"/> frames at <paramref name="rate"/>.
    /// </summary>
    static void MixRender(
        Meitou.Game.Audio.WwisePlayer player, List<(double At, string Event, ulong Object)> events, float[] all, int rate, int total)
    {
        int nextEvent = 0;
        var block = new float[Meitou.Game.Audio.AudioOutput.BlockFrames * 2];
        for (int at = 0; at < total; at += Meitou.Game.Audio.AudioOutput.BlockFrames)
        {
            for (; nextEvent < events.Count && events[nextEvent].At <= (double)at / rate; nextEvent++)
            {
                var (_, name, obj) = events[nextEvent];
                if (name.Length == 8 && uint.TryParse(name, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var id))
                    player.Post(id, obj);
                else
                    player.Post(name, obj);
            }

            Array.Clear(block);
            player.Mix(block, Meitou.Game.Audio.AudioOutput.BlockFrames);
            Array.Copy(block, 0, all, at * 2, Math.Min(block.Length, (total - at) * 2));
            if ((at / Meitou.Game.Audio.AudioOutput.BlockFrames) % 43 == 0)
                PrintLevel(player, all, at, rate);
        }
    }

    /// <summary>
    /// The level of the last second mixed up to block start 'at': RMS and peak, with the left and right RMS.
    /// </summary>
    static void PrintLevel(Meitou.Game.Audio.WwisePlayer player, float[] all, int at, int rate)
    {
        var (instances, voices, _, clips) = player.Stats;
        int from = Math.Max(0, at - rate), to = at;
        double left = 0, right = 0;
        float peak = 0;
        for (int i = from; i < to; i++)
        {
            left += all[2 * i] * all[2 * i];
            right += all[2 * i + 1] * all[2 * i + 1];
            peak = Math.Max(peak, Math.Max(Math.Abs(all[2 * i]), Math.Abs(all[2 * i + 1])));
        }

        int n = Math.Max(1, to - from);
        double rms = Math.Sqrt((left + right) / (2 * n));
        static double Db(double x) => 20 * Math.Log10(x + 1e-9);
        string position = $"{(double)at / rate,6:0.0} s  instances {instances,2} voices {voices,2} (music {player.MusicVoices,2}) clips {clips,3}";
        string level = $"rms {Db(rms),6:0.0} dBFS (L {Db(Math.Sqrt(left / n)),6:0.0} R {Db(Math.Sqrt(right / n)),6:0.0}) peak {peak:0.000}";
        Console.WriteLine($"{position}  last second: {level}");
    }

    #endregion

    #region Decode

    /// <summary>
    /// The Vorbis codebook library read from the game's <c>kenshi_x64.exe</c>, null (said on the console) when it is not found.
    /// </summary>
    static WwiseCodebookLibrary? Library(GameInstall install)
    {
        var exe = Path.Combine(install.Root, "kenshi_x64.exe");
        var library = WwiseCodebookLibrary.Load(exe);
        if (library is null) Console.Error.WriteLine($"No Wwise codebook library found in {exe}");
        return library;
    }

    /// <summary>
    /// Converts every Vorbis WEM of the bank and reports failures and sample counts.
    /// </summary>
    static int DecodeAll(GameInstall install, WwiseBank bank)
    {
        if (Library(install) is not { } library) return 1;
        Console.WriteLine($"codebook library: {library.Count} codebooks");
        int failed = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double seconds = 0;
        foreach (var m in bank.Media)
        {
            try
            {
                var w = WwiseVorbis.Read(bank.MediaBytes(m));
                var pcm = w.Decode(library);
                seconds += (double)w.SampleCount / w.SampleRate;
                int frames = pcm.Length / w.Channels;
                float peak = pcm.Length == 0 ? 0 : pcm.Max(MathF.Abs);
                if (frames != w.SampleCount
                        || peak is 0 or > 4)
                    Console.WriteLine($"  {m.Id:X8} frames {frames} of {w.SampleCount}, peak {peak:0.###}");
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                failed++;
                Console.WriteLine($"  {m.Id:X8} FAIL {e.Message}");
            }
        }
        Console.WriteLine($"{bank.Media.Count - failed} of {bank.Media.Count} decoded, "
            + $"{seconds:0.0} s of audio in {watch.Elapsed.TotalSeconds:0.00} s");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Writes WEMs of the bank (one id, or all) as 16-bit WAV files into a folder.
    /// </summary>
    static int ExportWav(GameInstall install, WwiseBank bank, string folder, string? id)
    {
        if (Library(install) is not { } library) return 1;
        Directory.CreateDirectory(folder);
        foreach (var m in bank.Media.Where(m => id is null || m.Id.ToString("X8").Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            var w = WwiseVorbis.Read(bank.MediaBytes(m));
            var pcm = w.Decode(library);
            var file = Path.Combine(folder, $"{m.Id:X8}.wav");
            WriteWav(file, pcm, w.Channels, w.SampleRate);
            Console.WriteLine($"{file}: {pcm.Length / w.Channels} frames, {w.Channels} ch, {w.SampleRate} Hz");
        }
        return 0;
    }

    /// <summary>
    /// Writes interleaved float samples as a 16-bit PCM WAV file.
    /// </summary>
    static void WriteWav(string file, float[] pcm, int channels, int rate)
    {
        using var s = new BinaryWriter(File.Create(file));
        s.Write("RIFF"u8);
        s.Write(36 + pcm.Length * 2);
        s.Write("WAVEfmt "u8);
        s.Write(16);
        s.Write((short)1);
        s.Write((short)channels);
        s.Write(rate);
        s.Write(rate * channels * 2);
        s.Write((short)(channels * 2));
        s.Write((short)16);
        s.Write("data"u8);
        s.Write(pcm.Length * 2);
        foreach (var x in pcm) s.Write((short)Math.Clamp(MathF.Round(x * 32767), -32768, 32767));
    }

    #endregion

    #region Survey

    /// <summary>
    /// Every bank: version, sections, object types, decode failures, media and references that do not resolve.
    /// </summary>
    static int Survey(GameInstall install)
    {
        int failures = 0;
        var allMedia = new Dictionary<uint, string>();
        var banks = Directory.GetFiles(AudioDirectory(install), "*.bnk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => (Name: Path.GetFileName(f), Bank: WwiseBank.ReadFile(f))).ToList();
        foreach (var (name, bank) in banks)
            foreach (var m in bank.Media) allMedia.TryAdd(m.Id, name);
        foreach (var (name, bank) in banks)
        {
            var types = bank.Objects.GroupBy(o => o.Type).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}");
            Console.WriteLine($"{name,-28} v{bank.Version} {string.Join(' ', bank.Sections.Select(s => $"{s.Tag}={s.Length}"))}");
            Console.WriteLine($"    {string.Join(", ", types)}");
            if (bank.Version != WwiseBank.DecodedVersion) continue;
            foreach (var bad in bank.Objects.OfType<WwiseRawObject>().Where(o => o.Error is not null))
            {
                failures++;
                Console.WriteLine($"    FAIL {bad.Type} {bad.Id:X8}: {bad.Error}");
            }
            var sounds = bank.Objects.OfType<WwiseSound>().ToList();
            var external = sounds
                .Where(s => s.Source.IsVorbis && bank.FindMedia(s.Source.SourceId) is null)
                .Select(s => allMedia.TryGetValue(s.Source.SourceId, out var other) ? other : "nowhere")
                .GroupBy(x => x)
                .Select(g => $"{g.Key} x{g.Count()}")
                .ToList();
            var unused = bank.Media.Count(m => !sounds.Any(s => s.Source.SourceId == m.Id));
            var misplaced = sounds.Count(s =>
            {
                return bank.FindMedia(s.Source.SourceId) is { } m && (m.Offset != s.Source.Offset || m.Length != s.Source.Size);
            });
            var dangling = Dangling(bank).ToList();
            if (external.Count > 0)
                Console.WriteLine($"    sounds with media in another bank: {string.Join(", ", external)}");
            if (unused > 0) Console.WriteLine($"    media no sound uses: {unused}");
            if (misplaced > 0) Console.WriteLine($"    sounds whose offset/size differ from DIDX: {misplaced}");
            if (dangling.Count > 0)
            {
                Console.WriteLine("    references outside the bank: "
                    + $"{string.Join(", ", dangling.GroupBy(d => d.Kind).Select(g => $"{g.Key} x{g.Count()}"))}");
            }
        }
        Console.WriteLine(failures == 0 ? "All version 112 objects decoded." : $"{failures} objects failed to decode.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Child, parent, playlist and action-target references that do not resolve inside the bank.
    /// </summary>
    public static IEnumerable<(string Kind, uint Id)> Dangling(WwiseBank bank)
    {
        foreach (var o in bank.Objects)
        {
            switch (o)
            {
                case WwiseEvent e:
                    foreach (var a in e.Actions) if (bank.Find(a) is null) yield return ("event action", a);
                    break;
                case WwiseAction a when a.Target != 0 && !a.TargetIsBus && bank.Find(a.Target) is null:
                    yield return ($"{a.Kind} target", a.Target);
                    break;
            }
            if (Node(o) is { } node && node.ParentId != 0 && bank.Find(node.ParentId) is null)
                yield return ("parent", node.ParentId);
            foreach (var c in Children(o)) if (bank.Find(c) is null) yield return ("child", c);
        }
    }

    #endregion

    #region Objects

    /// <summary>
    /// The node part (properties, positioning, parent) of a hierarchy object, null for an object without one.
    /// </summary>
    public static WwiseNodeBase? Node(WwiseObject o) => o switch
    {
        WwiseSound s => s.Node,
        WwiseActorMixer m => m.Node,
        WwiseRandomSequence r => r.Node,
        WwiseSwitchContainer s => s.Node,
        WwiseLayerContainer l => l.Node,
        WwiseMusicTrack t => t.Node,
        _ => Music(o)?.Node,
    };

    /// <summary>
    /// The music node part of a music segment, switch or playlist, null for any other object.
    /// </summary>
    public static WwiseMusicNode? Music(WwiseObject o) => o switch
    {
        WwiseMusicSegment s => s.Music,
        WwiseMusicSwitch s => s.Music,
        WwiseMusicPlaylist p => p.Music,
        _ => null,
    };

    /// <summary>
    /// Child ids stored in the bank. A music switch's decision tree may also name objects of other banks (the shared top switches).
    /// </summary>
    public static uint[] Children(WwiseObject o) => o switch
    {
        WwiseActorMixer m => m.Children,
        WwiseRandomSequence r => r.Children,
        WwiseSwitchContainer s => s.Children,
        WwiseLayerContainer l => l.Children,
        _ => Music(o)?.Children ?? [],
    };

    #endregion

    #region Dump

    /// <summary>
    /// Names of the music banks' events, groups and states that match the exe's strings by hash (docs/formats/wwise-bnk.md#music).
    /// </summary>
    static readonly Dictionary<uint, string> KnownNames = new[]
    {
        "Biomes", "General", "Time_in_Game", "Day", "Night", "Radio", "menu", "In_Game", "Beds", "Frames",
        "Dunes", "Savannah", "Swamplands", "Tarsands", "Ashlands", "Create_or_Load_Game", "Boot_Up_Game", "Radio_On", "Radio_Off",
    }.Concat(new[] { "Dunes", "Savannah", "Swamplands", "Tarsands" }.SelectMany(b => new[] { $"Music_{b}_Play", $"Music_{b}_Stop" }))
        .ToDictionary(WwiseHash.Id);

    /// <summary>
    /// An id in hexadecimal, with its name when it is one of the known names (<see cref="KnownNames"/>).
    /// </summary>
    static string Name(uint id) => KnownNames.TryGetValue(id, out var n) ? $"{id:X8} ({n})" : id.ToString("X8");

    /// <summary>
    /// Whether <paramref name="o"/> is a node not printed yet whose parent is none or outside the bank: a root of the rest of the tree.
    /// </summary>
    static bool IsRoot(WwiseBank bank, WwiseObject o, HashSet<uint> printed) =>
        Node(o) is not null && !printed.Contains(o.Id) && (Node(o)!.ParentId == 0 || bank.Find(Node(o)!.ParentId) is null);

    /// <summary>
    /// Prints a bank: its header and sections, every object with its details, the hierarchy as trees from its roots and, with <paramref
    /// name="media"/>, the embedded media.
    /// </summary>
    static void Dump(WwiseBank bank, bool media)
    {
        Console.WriteLine($"bank {bank.Id:X8} v{bank.Version} language {bank.LanguageId} names "
            + $"{string.Join(", ", bank.BankNames.Select(kv => $"{kv.Key:X8}={kv.Value}"))}");
        Console.WriteLine($"sections {string.Join(' ', bank.Sections.Select(s => $"{s.Tag}={s.Length}"))}");
        if (media)
        {
            foreach (var m in bank.Media)
                Console.WriteLine($"  wem {m.Id:X8} {m.Length,9} bytes  {WemSummary(bank.MediaBytes(m).Span)}");
        }
        var printed = new HashSet<uint>();
        foreach (var e in bank.Objects.OfType<WwiseEvent>())
        {
            Console.WriteLine($"event {Name(e.Id)}");
            foreach (var a in e.Actions)
            {
                if (bank.Find<WwiseAction>(a) is not { } action)
                {
                    Console.WriteLine($"  action {a:X8} (missing)");
                    continue;
                }
                Console.WriteLine($"  {action.Kind}/{action.Scope} -> "
                    + $"{action.Target:X8}{(action.TargetIsBus ? " (bus)" : "")}{Props(action.Properties)}{ActionDetail(action)}");
                if (action.Kind == WwiseActionKind.Play) Tree(bank, action.Target, 2, printed);
            }
        }
        var rest = bank.Objects.Where(o => IsRoot(bank, o, printed)).ToList();
        foreach (var o in rest) Tree(bank, o.Id, 0, printed);
        foreach (var raw in bank.Objects.OfType<WwiseRawObject>())
            Console.WriteLine($"{raw.Type} {raw.Id:X8} {raw.Body.Length} bytes{(raw.Error is null ? "" : " FAIL " + raw.Error)}");
    }

    /// <summary>
    /// The kind-specific part of an action's line: the bank of a Play, the group and state of a SetState, the value of a volume, pitch or low-pass
    /// change.
    /// </summary>
    static string ActionDetail(WwiseAction a) => a.Kind switch
    {
        WwiseActionKind.Play => $" bank {a.BankId:X8}",
        WwiseActionKind.SetState => $" group {a.GroupId:X8} state {a.StateId:X8}",
        WwiseActionKind.SetVolume or WwiseActionKind.SetPitch or WwiseActionKind.SetLowPass or WwiseActionKind.SetBusVolume
            => $" value {a.Value} ({a.ValueMin}..{a.ValueMax}) meaning {a.ValueMeaning}",
        _ => "",
    };

    /// <summary>
    /// Prints the object <paramref name="id"/> and its children <paramref name="depth"/> levels in, each once (<paramref name="printed"/>).
    /// </summary>
    static void Tree(WwiseBank bank, uint id, int depth, HashSet<uint> printed)
    {
        var pad = new string(' ', depth * 2);
        var o = bank.Find(id);
        if (o is null)
        {
            Console.WriteLine($"{pad}{id:X8} (not in this bank)");
            return;
        }
        if (!printed.Add(id))
        {
            Console.WriteLine($"{pad}{o.Type} {id:X8} (above)");
            return;
        }
        var node = Node(o);
        var sb = new StringBuilder($"{pad}{o.Type} {id:X8}");
        if (node is not null) AppendNodeDetails(sb, node);
        AppendKindDetails(sb, o);
        if (Music(o) is { } music && music.Meter.Flags != 0)
            sb.Append($" grid {music.Meter.GridPeriod:0.#} ms {music.Meter.Tempo} bpm {music.Meter.Beats}/{music.Meter.BeatUnit}");
        Console.WriteLine(sb);
        var rules = o switch { WwiseMusicSwitch w => w.Transitions, WwiseMusicPlaylist p => p.Transitions, _ => [] };
        foreach (var t in rules) Console.WriteLine($"{pad}  {Transition(t)}");
        if (o is WwiseMusicSwitch ms)
        {
            foreach (var d in ms.Tree.Children) Decision(bank, d, depth + 1, printed);
            return;
        }
        if (o is WwiseMusicPlaylist pl)
        {
            PlaylistItem(bank, pl.Playlist, depth + 1, printed);
            return;
        }

        PrintChildren(bank, o, depth, pad, printed);
    }

    /// <summary>
    /// Prints an object's children: a switch container's per switch, a music switch's decision tree, a playlist's items, else its child list.
    /// </summary>
    static void PrintChildren(WwiseBank bank, WwiseObject o, int depth, string pad, HashSet<uint> printed)
    {
        if (o is WwiseSwitchContainer sw)
        {
            foreach (var (switchId, nodes) in sw.Switches)
            {
                Console.WriteLine($"{pad}  when {switchId:X8}:");
                foreach (var n in nodes) Tree(bank, n, depth + 2, printed);
            }
        }

        foreach (var c in Children(o))
            if (o is not WwiseSwitchContainer || !printed.Contains(c)) Tree(bank, c, depth + 1, printed);
    }

    /// <summary>
    /// Appends a node's properties, its positioning (when it overrides the parent's), RTPCs and state groups.
    /// </summary>
    static void AppendNodeDetails(StringBuilder sb, WwiseNodeBase node)
    {
        sb.Append(Props(node.Properties));
        if (node.Positioning.OverridesParent)
        {
            string attenuation = node.Positioning.Is3D ? $" att {node.Positioning.AttenuationId:X8}" : "";
            string paths = node.Positioning.Vertices.Length > 0 ? $" paths {node.Positioning.Paths.Length}" : "";
            sb.Append($" pos {node.Positioning.Bits:X2}{attenuation}{paths}");
        }

        foreach (var rtpc in node.Rtpcs)
            sb.Append($" rtpc {rtpc.GameParameterId:X8}->p{rtpc.ParameterId}[{rtpc.Points.Length}]");
        foreach (var s in node.StateGroups) sb.Append($" states {s.GroupId:X8}[{s.States.Length}]");
    }

    /// <summary>
    /// Appends what an object's kind holds: a sound's source, and the settings of a random or sequence, switch or layer container, music segment,
    /// track or switch.
    /// </summary>
    static void AppendKindDetails(StringBuilder sb, WwiseObject o)
    {
        switch (o)
        {
            case WwiseSound s:
                sb.Append($" wem {s.Source.SourceId:X8} {s.Source.Size} bytes{(s.Source.IsVorbis ? "" : $" plugin {s.Source.PluginId:X8}")}");
                break;
            case WwiseRandomSequence r:
                sb.Append($" {(r.Mode == 0 ? "random" : "sequence")} loop {r.LoopCount} transition "
                    + $"{r.TransitionMode}:{r.TransitionTime}ms avoid {r.AvoidRepeatCount} flags {r.Flags:X2}");
                break;
            case WwiseSwitchContainer w:
                sb.Append($" group {w.GroupType}:{w.GroupId:X8} default {w.DefaultSwitch:X8}");
                break;
            case WwiseLayerContainer l:
                foreach (var layer in l.Layers)
                    sb.Append($" layer {layer.Id:X8} x-fade {layer.CrossfadeParameterId:X8}[{layer.Associations.Length}]");
                break;
            case WwiseMusicSegment s:
                sb.Append($" {s.Duration:0.#} ms entry {s.EntryCue:0.#} exit {s.ExitCue:0.#}{(s.Music.Children.Length == 0 ? " (silent)" : "")}");
                break;
            case WwiseMusicTrack t:
                foreach (var c in t.Clips)
                    sb.Append($" clip wem {c.SourceId:X8} at {c.PlayAt:0.#} trim {c.BeginTrim:0.#}/{c.EndTrim:0.#} of {c.SourceDuration:0.#} ms");
                break;
            case WwiseMusicSwitch w:
                string arguments = string.Join(", ", w.Arguments.Select(a => $"{(a.GroupType == 1 ? "state" : "switch")} {Name(a.GroupId)}"));
                sb.Append($" on {arguments}{(w.ContinuePlayback ? " continue" : "")}");
                break;
        }
    }

    /// <summary>
    /// A transition fade: "cut" when it has no time and no offset, else its time, curve and offset.
    /// </summary>
    static string Fade(WwiseFade f) => f.Time == 0 && f.Offset == 0 ? "cut" : $"{f.Time} ms curve {f.Curve} offset {f.Offset}";

    /// <summary>
    /// A music transition rule's line: sources to destinations, the exit's sync and fade-out, the entry's fade-in, jump and cues.
    /// </summary>
    static string Transition(WwiseMusicTransition t)
    {
        static string Ids(uint[] ids) => string.Join("|", ids.Select(i => i == WwiseMusicTransition.Any ? "any" : i.ToString("X8")));
        var (s, d) = (t.Source, t.Destination);
        return $"rule {Ids(t.Sources)} -> {Ids(t.Destinations)}: "
            + $"exit sync {s.SyncType} fade-out {Fade(s.FadeOut)}{(s.PlayPostExit ? " post-exit" : "")}"
            + $"; enter fade-in {Fade(d.FadeIn)}{(d.PlayPreEntry ? " pre-entry" : "")}{(d.JumpToId != 0 ? $" jump to item {d.JumpToId:X8}" : "")}"
            + $"{(d.EntryType != 0 ? $" entry {d.EntryType}" : "")}"
            + $"{(s.CueFilter != 0 || d.CueFilter != 0 ? $" cues {s.CueFilter:X8}/{d.CueFilter:X8}" : "")}";
    }

    /// <summary>
    /// Prints a music switch's decision tree node: each key's children or the audio node it plays.
    /// </summary>
    static void Decision(WwiseBank bank, WwiseDecisionNode d, int depth, HashSet<uint> printed)
    {
        var pad = new string(' ', depth * 2);
        string key = d.Key == 0 ? "any" : Name(d.Key);
        if (d.Children.Length > 0)
        {
            Console.WriteLine($"{pad}when {key}:");
            foreach (var c in d.Children) Decision(bank, c, depth + 1, printed);
        }
        else if (d.AudioNodeId == 0) Console.WriteLine($"{pad}when {key}: nothing");
        else if (bank.Find(d.AudioNodeId) is null)
        {
            Console.WriteLine($"{pad}when {key}: {d.AudioNodeId:X8} (in another bank)");
        }
        else
        {
            Console.WriteLine($"{pad}when {key}:");
            Tree(bank, d.AudioNodeId, depth + 1, printed);
        }
    }

    /// <summary>
    /// Prints a playlist item (group or segment) with its loop count, weight and children.
    /// </summary>
    static void PlaylistItem(WwiseBank bank, WwisePlaylistItem item, int depth, HashSet<uint> printed)
    {
        var pad = new string(' ', depth * 2);
        string loop = item.LoopCount == 0 ? " loop forever" : item.LoopCount == 1 ? "" : $" loop {item.LoopCount}";
        string weight = item.Weight == 50000 ? "" : $" weight {item.Weight / 50000.0:0.##}";
        if (item.IsGroup)
        {
            string type = item.PlaylistType switch
            {
                0 => "sequence (continuous)",
                1 => "sequence (step)",
                2 => "random (continuous)",
                3 => "random (step)",
                _ => $"type {item.PlaylistType}",
            };
            Console.WriteLine($"{pad}group {item.ItemId:X8} {type}{loop}{weight}"
                + $"{(item.AvoidRepeatCount > 0 && item.PlaylistType >= 2 ? $" avoid {item.AvoidRepeatCount}" : "")}"
                + $"{(item.Shuffle ? " shuffle" : "")}, {item.Children.Length} items");
            foreach (var c in item.Children) PlaylistItem(bank, c, depth + 1, printed);
            return;
        }
        Console.WriteLine($"{pad}item {item.ItemId:X8}{loop}{weight}:");
        Tree(bank, item.SegmentId, depth + 1, printed);
    }

    /// <summary>
    /// A properties block as " {name=value ... name~[min,max]}", empty when it has none.
    /// </summary>
    static string Props(WwiseProperties p)
    {
        if (p.Values.Count == 0 && p.Ranges.Count == 0) return "";
        var parts = p.Values.Select(kv => $"{PropName(kv.Key)}={PropValue(kv.Key, kv.Value)}")
            .Concat(p.Ranges.Select(kv => $"{PropName(kv.Key)}~[{kv.Value.Min},{kv.Value.Max}]"));
        return $" {{{string.Join(' ', parts)}}}";
    }

    /// <summary>
    /// A short name for a property id (vol, pitch, lpf, fade, loop), else its number.
    /// </summary>
    static string PropName(byte id) => id switch
    {
        WwisePropertyId.Volume => "vol",
        WwisePropertyId.Pitch => "pitch",
        WwisePropertyId.LowPass => "lpf",
        WwisePropertyId.FadeTime => "fade",
        WwisePropertyId.Loop => "loop",
        _ => $"p{id:X2}",
    };

    /// <summary>
    /// A property's raw 32 bits as text: as an integer when it reads as a denormal float (small integers stored as ints), else as the float.
    /// </summary>
    static string PropValue(byte id, uint raw)
    {
        float f = BitConverter.UInt32BitsToSingle(raw);
        // Small integers stored as ints read as denormals; print those as ints.
        return raw < 0x00800000 ? raw.ToString() : f.ToString("0.###");
    }

    /// <summary>
    /// The RIFF header of a WEM: the codec tag, channels, rate, the fmt chunk's size and the other chunks.
    /// </summary>
    public static string WemSummary(ReadOnlySpan<byte> wem)
    {
        if (wem.Length < 12 || !wem.StartsWith("RIFF"u8)) return "not RIFF";
        var sb = new StringBuilder();
        int p = 12;
        while (p + 8 <= wem.Length)
        {
            var tag = Encoding.ASCII.GetString(wem.Slice(p, 4));
            int len = BitConverter.ToInt32(wem.Slice(p + 4, 4));
            if (tag == "fmt ")
            {
                sb.Append($"codec {BitConverter.ToUInt16(wem.Slice(p + 8, 2)):X4} ch {BitConverter.ToUInt16(wem.Slice(p + 10, 2))} "
                    + $"{BitConverter.ToUInt32(wem.Slice(p + 12, 4))} Hz fmt {len}; ");
            }
            else
                sb.Append($"{tag.Trim()} {len}; ");
            if (len < 0) break;
            p += 8 + len + (len & 1);
        }
        return sb.ToString().TrimEnd(' ', ';');
    }

    #endregion
}

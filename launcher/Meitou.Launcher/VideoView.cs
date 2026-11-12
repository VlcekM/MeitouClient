using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Meitou.Launcher;

/// <summary>
/// A news post's <c>.webm</c> video: its first picture with a play button. A click plays it with its sound (<see cref="VideoAudio"/>);
/// then a click (or Space) pauses and resumes, the bar at the bottom skips 10 s back or forward (or Left/Right) and seeks anywhere.
/// One video plays at a time; it pauses when its post leaves the screen and returns to the poster at the end.
/// </summary>
sealed class VideoView : Border
{
    /// <summary>
    /// Bytes fetched for the poster: enough for the first frame of a 720p or 1080p clip.
    /// </summary>
    const int PosterBytes = 1 << 20;

    /// <summary>
    /// How far the skip buttons and the arrow keys jump.
    /// </summary>
    static readonly TimeSpan Skip = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The play and pause icons.
    /// </summary>
    static readonly Geometry PlayIcon = Geometry.Parse("M 0,0 L 0,14 L 12,7 Z"),
        PauseIcon = Geometry.Parse("M 0,0 H 4 V 14 H 0 Z M 8,0 H 12 V 14 H 8 Z");

    /// <summary>
    /// The video playing now, null when none is (one plays at a time).
    /// </summary>
    static VideoView? s_playing;

    /// <summary>
    /// The last playback session of any video: the next waits for it, so two never hold OpenAL's current context at once.
    /// </summary>
    static Task s_lastSession = Task.CompletedTask;

    /// <summary>
    /// What the view shows.
    /// </summary>
    enum State
    {
        /// <summary>
        /// The first picture with the play button.
        /// </summary>
        Poster,

        /// <summary>
        /// The whole video is being downloaded (the first Play).
        /// </summary>
        Loading,

        /// <summary>
        /// The video plays with its sound.
        /// </summary>
        Playing,

        /// <summary>
        /// Stopped at <see cref="_position"/>.
        /// </summary>
        Paused,
    }

    /// <summary>
    /// The video's URL.
    /// </summary>
    readonly string _url;

    /// <summary>
    /// The news media cache the video comes from.
    /// </summary>
    readonly NewsMediaCache _media;

    /// <summary>
    /// The whole video, downloaded on the first Play; null before.
    /// </summary>
    WebmFile? _file; // the whole video, downloaded on the first Play

    /// <summary>
    /// The picture.
    /// </summary>
    readonly Image _image = new() { Stretch = Stretch.Uniform };

    /// <summary>
    /// The play button over the poster.
    /// </summary>
    readonly Control _button;

    /// <summary>
    /// The control bar at the bottom.
    /// </summary>
    readonly Border _bar;

    /// <summary>
    /// The bar's play and pause icon.
    /// </summary>
    readonly Path _playPause;

    /// <summary>
    /// The seek bar's track and its filled part.
    /// </summary>
    readonly Border _track, _fill;

    /// <summary>
    /// The time text (position and length).
    /// </summary>
    readonly TextBlock _time;

    /// <summary>
    /// The first picture.
    /// </summary>
    readonly WriteableBitmap _poster;

    /// <summary>
    /// The bitmap the playing frames are copied into; null before the first.
    /// </summary>
    WriteableBitmap? _frame;

    /// <summary>
    /// Cancels the playback session; null when none runs.
    /// </summary>
    CancellationTokenSource? _run;

    /// <summary>
    /// What the view shows.
    /// </summary>
    State _state = State.Poster;

    /// <summary>
    /// Where the video is (seconds into it).
    /// </summary>
    TimeSpan _position;

    /// <summary>
    /// The pointer is over the view (the bar shows).
    /// </summary>
    bool _hovered;

    /// <summary>
    /// The seek bar's fraction while it is held; null when not.
    /// </summary>
    double? _dragging; // the seek bar's fraction while it is held

    /// <summary>
    /// Fills the post's width, its height following the picture's shape.
    /// </summary>
    VideoView(string url, NewsMediaCache media, WebmFile? file, byte[] firstPicture, int width, int height,
        (string Url, Action<string> Open)? fullVideo)
    {
        (this._url, this._media, this._file) = (url, media, file);
        CornerRadius = new CornerRadius(6);
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);
        _poster = Bitmap(width, height);
        Copy(firstPicture, _poster);
        _image.Source = _poster;
        var shade = new SolidColorBrush(Color.FromArgb(0xB0, 0x11, 0x0F, 0x0D));
        var ink = MarkdownView.Brush("PaperLight");
        _button = PlayButton(shade, ink);

        _playPause = new Path { Data = PauseIcon, Fill = ink, VerticalAlignment = VerticalAlignment.Center };
        _fill = new Border { Background = MarkdownView.Brush("BrassLight"), HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(3) };
        _track = new Border
        {
            Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x60, 0xD9, 0xCE, 0xB8)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), Child = _fill,
        };

        var seek = SeekStrip();
        _track.SizeChanged += (_, _) => UpdateBar();
        _time = new TextBlock { Foreground = ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        _bar = Bar(BarRow(seek));
        _bar.PointerPressed += (_, e) => e.Handled = true; // the bar's gaps are not clicks on the picture

        var layers = new Panel { Children = { _image, _button, _bar } };
        if (fullVideo is var (page, open)) layers.Children.Add(FullVideoLink(page, open, shade, ink));
        Child = layers;
        HookInput();
    }

    /// <summary>
    /// The round play button in the middle of the poster.
    /// </summary>
    static Border PlayButton(IBrush shade, IBrush ink) =>
        new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = shade,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new Path
            {
                Data = Geometry.Parse("M 0,0 L 0,22 L 19,11 Z"), Fill = ink, Margin = new Thickness(5, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

    /// <summary>
    /// A taller transparent strip around the thin track, so it is easy to hit: pressing or dragging moves the bar, releasing seeks there.
    /// </summary>
    Border SeekStrip()
    {
        var seek = new Border { Background = Brushes.Transparent, Height = 22, Child = _track };
        seek.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            e.Pointer.Capture(seek);
            _dragging = Fraction(e);
            UpdateBar();
        };
        seek.PointerMoved += (_, e) =>
        {
            if (_dragging is not null)
            {
                _dragging = Fraction(e);
                UpdateBar();
            }
        };
        seek.PointerReleased += (_, e) =>
        {
            if (_dragging is not { } f) return;
            e.Handled = true;
            _dragging = null;
            e.Pointer.Capture(null);
            SeekTo(Duration * f);
        };
        return seek;
    }

    /// <summary>
    /// The bar's row: skip back, play/pause and skip forward on the left, the time on the right, <paramref name="seek"/> between them.
    /// </summary>
    DockPanel BarRow(Border seek)
    {
        var row = new DockPanel();
        foreach (var control in new[]
                 {
                     BarButton("M 9,0 L 9,14 L 0,7 Z M 18,0 L 18,14 L 9,7 Z", () => SeekTo(_position - Skip)),
                     BarButton(null, TogglePause, _playPause),
                     BarButton("M 0,0 L 0,14 L 9,7 Z M 9,0 L 9,14 L 18,7 Z", () => SeekTo(_position + Skip)),
                 })
        {
            DockPanel.SetDock(control, Dock.Left);
            row.Children.Add(control);
        }
        DockPanel.SetDock(_time, Dock.Right);
        row.Children.Add(_time);
        row.Children.Add(seek);
        return row;
    }

    /// <summary>
    /// The bar at the bottom holding <paramref name="row"/> over a dark gradient; hidden until <see cref="UpdateBar"/> shows it.
    /// </summary>
    static Border Bar(DockPanel row) =>
        new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(8, 4), IsVisible = false, Child = row,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0x11, 0x0F, 0x0D), 0), new GradientStop(Color.FromArgb(0xD0, 0x11, 0x0F, 0x0D),
                        1) },
            },
        };

    /// <summary>
    /// "Full video" in the top right corner: the post's full-quality video on the web (front matter `video`); a click pauses this one and
    /// opens <paramref name="page"/>.
    /// </summary>
    Border FullVideoLink(string page, Action<string> open, IBrush shade, IBrush ink)
    {
        var link = new Border
        {
            Background = shade, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4), Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Full video", Foreground = ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    new Path { Data = Geometry.Parse("M 0,3 H 5 V 5 H 2 V 10 H 7 V 7 H 9 V 12 H 0 Z M 6,0 H 12 V 6 L 10,4 L 6,8 L 4,6 L 8,2 Z"),
                            Fill = ink, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        ToolTip.SetTip(link, page);
        link.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Pause();
            open(page);
        };
        return link;
    }

    /// <summary>
    /// A left click plays the poster or pauses and resumes; Space, Left and Right do the same from the keyboard; the bar shows while the
    /// pointer is over the video; leaving the screen returns to the poster.
    /// </summary>
    void HookInput()
    {
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus();
            if (_state == State.Poster) Play(); else TogglePause();
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (_state is State.Poster or State.Loading) return;
            if (e.Key == Key.Space) TogglePause();
            else if (e.Key == Key.Left) SeekTo(_position - Skip);
            else if (e.Key == Key.Right) SeekTo(_position + Skip);
            else return;
            e.Handled = true;
        };
        PointerEntered += (_, _) =>
        {
            _hovered = true;
            UpdateBar();
        };
        PointerExited += (_, _) =>
        {
            _hovered = false;
            UpdateBar();
        };
        DetachedFromVisualTree += (_, _) => ToPoster();
    }

    /// <summary>
    /// A button of the control bar: an icon (or the given path) that calls <paramref name="click"/>.
    /// </summary>
    Control BarButton(string? icon, Action click, Path? path = null)
    {
        var control = new Border
        {
            Width = 30, Height = 26, Background = Brushes.Transparent,
            Child = path ?? new Path { Data = Geometry.Parse(icon!), Fill = MarkdownView.Brush("PaperLight"),
                    VerticalAlignment = VerticalAlignment.Center },
        };
        if (control.Child is Path p) p.HorizontalAlignment = HorizontalAlignment.Center;
        control.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Focus();
            click();
        };
        return control;
    }

    /// <summary>
    /// A video whose first picture loads in the background from the file's start (<see cref="NewsMediaCache.HeadAsync"/>); the rest downloads
    /// only when it is played. Until then (or when it cannot be played) it takes no room, like an image.
    /// </summary>
    public static Control Create(string url, NewsMediaCache media, (string Url, Action<string> Open)? fullVideo = null)
    {
        var host = new Panel();
        _ = Task.Run(async () =>
        {
            if (!VpxDecoder.Available) return;
            var bytes = await media.HeadAsync(url, PosterBytes);
            bool whole = bytes is not null && bytes.Length < PosterBytes; // a short file comes whole
            if (bytes is null) return;
            if (Poster(bytes, head: !whole) is not var (head, pixels, width, height))
            {
                // A first frame larger than the head: take the whole file after all.
                if (whole || await media.ImageAsync(url) is not { } all || Poster(all, head: false) is not var (full, p, w, h))
                    return;
                (head, pixels, width, height, whole) = (full, p, w, h, true);
            }
            Dispatcher.UIThread.Post(() => host.Children.Add(new VideoView(url, media, whole ? head : null, pixels, width, height, fullVideo)));
        });
        return host;
    }

    /// <summary>
    /// The poster: the file read from <paramref name="bytes"/> (only its start when <paramref name="head"/>) and its first picture as BGRA; null when
    /// it has no video or cannot be decoded.
    /// </summary>
    static (WebmFile File, byte[] Pixels, int Width, int Height)? Poster(byte[] bytes, bool head)
    {
        try
        {
            var file = WebmFile.Read(bytes, head);
            if (file.VideoCodec is null) return null;
            using var decoder = new VpxDecoder(file.VideoCodec);
            byte[] pixels = [];
            foreach (var f in file.VideoFrames)
            {
                if (decoder.Decode(f.Data.Span, ref pixels) is var (width, height))
                    return (file, pixels, width, height);
            }
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
        }
        return null;
    }

    /// <summary>
    /// The last picture's time: what the seek bar spans.
    /// </summary>
    TimeSpan Duration => _file is { VideoFrames.Count: > 0 } f ? f.VideoFrames[^1].Time : TimeSpan.Zero;

    /// <summary>
    /// From the poster: the whole file is downloaded the first time (the play button dimmed meanwhile), then it plays from the start.
    /// </summary>
    void Play()
    {
        s_playing?.Pause();
        s_playing = this;
        if (_file is not null)
        {
            Start(TimeSpan.Zero, play: true);
            return;
        }
        _state = State.Loading;
        _button.Opacity = 0.5;
        _ = Task.Run(async () =>
        {
            var bytes = await _media.ImageAsync(_url);
            WebmFile? loaded = null;
            try
            {
                if (bytes is not null) loaded = WebmFile.Read(bytes);
            }
            catch (InvalidDataException)
            {
            }
            Dispatcher.UIThread.Post(() =>
            {
                _button.Opacity = 1;
                _file ??= loaded;
                if (_state != State.Loading) return; // stopped meanwhile
                if (_file is null) ToPoster(); // could not be downloaded
                else Start(TimeSpan.Zero, play: true);
            });
        });
    }

    /// <summary>
    /// Space or a click: pauses while playing, resumes while paused (pausing any other video).
    /// </summary>
    void TogglePause()
    {
        if (_state == State.Playing) Pause();
        else if (_state == State.Paused)
        {
            s_playing?.Pause();
            s_playing = this;
            Start(_position, play: true);
        }
    }

    /// <summary>
    /// Pauses: the session stops, the position stays.
    /// </summary>
    void Pause()
    {
        if (_state != State.Playing) return;
        Cancel();
        _state = State.Paused;
        UpdateBar();
    }

    /// <summary>
    /// Jumps to a time: playing on from there, or showing its picture when paused.
    /// </summary>
    void SeekTo(TimeSpan target)
    {
        if (_file is null || _state is State.Poster or State.Loading) return;
        target = target < TimeSpan.Zero ? TimeSpan.Zero : target > Duration ? Duration : target;
        _position = target;
        Start(target, play: _state == State.Playing);
    }

    /// <summary>
    /// Back to the poster: stops, rewinds and shows the first picture.
    /// </summary>
    void ToPoster()
    {
        Cancel();
        if (s_playing == this) s_playing = null;
        _state = State.Poster;
        _position = TimeSpan.Zero;
        _image.Source = _poster;
        _button.IsVisible = true;
        UpdateBar();
    }

    /// <summary>
    /// Stops the playback session.
    /// </summary>
    void Cancel()
    {
        _run?.Cancel();
        _run = null;
    }

    /// <summary>
    /// A new session from <paramref name="from"/> (the previous one cancelled): it shows that moment's picture, then plays on if asked.
    /// </summary>
    void Start(TimeSpan from, bool play)
    {
        Cancel();
        _run = new CancellationTokenSource();
        var token = _run.Token;
        _state = play ? State.Playing : State.Paused;
        _button.IsVisible = false;
        UpdateBar();
        var video = _file!;
        var previous = s_lastSession;
        s_lastSession = Task.Run(async () =>
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // a failed session must not block the next
            if (!token.IsCancellationRequested) Session(video, from, play, token);
        });
    }

    /// <summary>
    /// Decodes from the keyframe before <paramref name="from"/> up to it without drawing, shows that picture, and when playing shows each
    /// next one at its time, on the sound's clock when there is sound, else a stopwatch. At the end the poster returns.
    /// </summary>
    void Session(WebmFile file, TimeSpan from, bool play, CancellationToken token)
    {
        bool ended = false;
        try
        {
            using var decoder = new VpxDecoder(file.VideoCodec!);
            var frames = file.VideoFrames;
            int target = Math.Max(0, frames.FindLastIndex(f => f.Time <= from));
            int key = Math.Max(0, frames.FindLastIndex(target, f => f.Key));
            byte[] pixels = [];
            for (int i = key; i < target && !token.IsCancellationRequested; i++)
                decoder.Decode(frames[i].Data.Span, ref pixels, convert: false);
            if (decoder.Decode(frames[target].Data.Span, ref pixels) is var (w, h))
                Show(pixels, w, h, frames[target].Time, token);
            if (!play || token.IsCancellationRequested) return;

            using var audio = VideoAudio.TryOpen(file, frames[target].Time);
            var start = frames[target].Time;
            var watch = Stopwatch.StartNew();
            TimeSpan heard = start, heardAt = TimeSpan.Zero; // the sound's last position, and when it was read
            TimeSpan Clock()
            {
                if (audio is null) return start + watch.Elapsed;

                // Before the sound's first frame, and once it has ended (or runs shorter than the pictures), the clock runs on its own.
                if (!audio.Finished && watch.Elapsed > TimeSpan.FromMilliseconds(50))
                    (heard, heardAt) = (audio.Position, watch.Elapsed);
                return heard + (watch.Elapsed - heardAt);
            }
            audio?.Start();
            for (int i = target + 1; i < frames.Count && !token.IsCancellationRequested; i++)
            {
                // A picture whose successor is already due is decoded (the next ones depend on it) but not drawn.
                bool late = i + 1 < frames.Count && frames[i + 1].Time <= Clock();
                if (decoder.Decode(frames[i].Data.Span, ref pixels, convert: !late) is not var (width, height))
                    continue;
                for (TimeSpan wait; (wait = frames[i].Time - Clock()) > TimeSpan.Zero && !token.IsCancellationRequested;)
                    Thread.Sleep(Math.Clamp((int)wait.TotalMilliseconds, 1, 15));
                Show(pixels, width, height, frames[i].Time, token);
            }
            while (audio is { Finished: false } && !token.IsCancellationRequested) Thread.Sleep(20);
            ended = !token.IsCancellationRequested;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or TaskCanceledException)
        {
        }
        if (ended) Dispatcher.UIThread.Post(() => { if (!token.IsCancellationRequested) ToPoster(); });
    }

    /// <summary>
    /// Shows a decoded frame at <paramref name="at"/> on the UI thread; pauses when the view is no longer visible (the Home page was left).
    /// </summary>
    void Show(byte[] pixels, int width, int height, TimeSpan at, CancellationToken token) =>
        Dispatcher.UIThread.Invoke(() =>
        {
            if (token.IsCancellationRequested) return;
            if (!IsEffectivelyVisible)
            {
                Pause();
                return;
            } // the Home page was left
            if (_frame is null || _frame.PixelSize.Width != width || _frame.PixelSize.Height != height)
                _frame = Bitmap(width, height);
            Copy(pixels, _frame);

            // A new bitmap object each time is not needed; the Image only has to redraw.
            if (_image.Source != _frame) _image.Source = _frame;
            _image.InvalidateVisual();
            _position = at;
            UpdateBar();
        });

    /// <summary>
    /// The bar shows while paused, or while playing with the pointer over the video.
    /// </summary>
    void UpdateBar()
    {
        _bar.IsVisible = _state == State.Paused || _state == State.Playing && (_hovered || _dragging is not null);
        _playPause.Data = _state == State.Playing ? PauseIcon : PlayIcon;
        var duration = Duration;
        double f = _dragging ?? (duration > TimeSpan.Zero ? _position / duration : 0);
        _fill.Width = Math.Clamp(f, 0, 1) * _track.Bounds.Width;
        _time.Text = $"{Format(duration * f)} / {Format(duration)}";
    }

    /// <summary>
    /// A time as m:ss, or h:mm:ss from an hour.
    /// </summary>
    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>
    /// The pointer's place on the seek bar, 0 to 1.
    /// </summary>
    double Fraction(PointerEventArgs e) => _track.Bounds.Width > 0 ? Math.Clamp(e.GetPosition(_track).X / _track.Bounds.Width, 0, 1) : 0;

    /// <summary>
    /// An opaque BGRA bitmap of the size.
    /// </summary>
    static WriteableBitmap Bitmap(int width, int height) =>
        new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    /// <summary>
    /// Copies BGRA pixels into the bitmap, row by row.
    /// </summary>
    static void Copy(byte[] bgra, WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        int row = bitmap.PixelSize.Width * 4;
        for (int y = 0; y < bitmap.PixelSize.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(bgra, y * row, buffer.Address + y * buffer.RowBytes, row);
    }
}

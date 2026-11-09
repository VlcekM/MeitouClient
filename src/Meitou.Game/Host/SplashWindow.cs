using System.Runtime.InteropServices;

namespace Meitou.Game.Host;

/// <summary>
/// The splash shown from the start of the program until the game window draws its first loading frame (docs/engine.md "Loading"): nothing can be
/// drawn with Vulkan before the device exists (a second or two), so this is a plain Win32 window painted with GDI on a thread of its own: one of the
/// original's loading pictures, LOADING... and a spinner. Windows only; elsewhere it shows nothing.
/// </summary>
sealed unsafe partial class SplashWindow : IDisposable
{
    /// <summary>
    /// The splash window's size, pixels.
    /// </summary>
    const int Width = 960, Height = 540;
    /// <summary>
    /// The thread that makes the window and runs its message loop.
    /// </summary>
    readonly Thread _thread;
    /// <summary>
    /// Set once the window was made (or failed): the constructor waits for it, up to 2 s.
    /// </summary>
    readonly ManualResetEventSlim _created = new();
    /// <summary>
    /// The splash window; 0 before it is made or once closed.
    /// </summary>
    static nint s_window;
    /// <summary>
    /// The loading picture, BGRA, top row first; null until decoded (or without one).
    /// </summary>
    static byte[]? s_picture;
    /// <summary>
    /// The picture's size, pixels.
    /// </summary>
    static int s_pictureWidth, s_pictureHeight;
    /// <summary>
    /// When the window was made (<c>Environment.TickCount64</c>), the spinner's clock.
    /// </summary>
    static long s_started;
    /// <summary>
    /// The original's LOADING... face (Kenshi_UI_Messages: MailartRubberstamp-Regular.ttf), 0 when missing.
    /// </summary>
    static nint s_font;

    /// <summary>
    /// Starts the splash thread and waits until its window was made (at most 2 s).
    /// </summary>
    SplashWindow(string? picturePath, string? fontPath)
    {
        _thread = new Thread(() => Run(picturePath, fontPath)) { IsBackground = true, Name = "splash" };
        _thread.Start();
        _created.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Shows the splash (null off Windows or when it cannot be made). <paramref name="dataDirectory"/> is the game's <c>data</c> folder, for a
    /// loading picture.
    /// </summary>
    public static SplashWindow? Show(string? dataDirectory)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? path = null, fontPath = null;
        if (dataDirectory is not null)
        {
            var pictures = Enumerable.Range(0, 16)
                .Select(i => Path.Combine(dataDirectory, "gui", "gfx", $"loading_screen{i}.dds"))
                .Where(File.Exists)
                .ToArray();
            if (pictures.Length > 0) path = pictures[Random.Shared.Next(pictures.Length)];
            if (Path.Combine(dataDirectory, "gui", "fonts", "MailartRubberstamp-Regular.ttf") is var stamp && File.Exists(stamp)) fontPath = stamp;
        }
        try
        {
            return new SplashWindow(path, fontPath);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The splash thread: loads the font, makes and shows the window centred on the screen, starts the 33 ms spinner timer, decodes the picture, then
    /// runs the message loop until the window is closed.
    /// </summary>
    void Run(string? picturePath, string? fontPath)
    {
        s_started = Environment.TickCount64;
        if (fontPath is not null && AddFontResourceExW(fontPath, 0x10 /* FR_PRIVATE */, 0) > 0)
        {
            fixed (char* face = "Mailart Rubberstamp")
                s_font = CreateFontW(-40, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, face);
        }
        var instance = GetModuleHandleW(null);
        fixed (char* className = "MeitouSplash")
        fixed (char* title = "Meitou")
        {
            var wc = new Wndclassexw
            {
                cbSize = (uint)sizeof(Wndclassexw),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = className,
                hCursor = LoadCursorW(0, (nint)32514),   // IDC_WAIT
            };
            RegisterClassExW(&wc);
            int x = (GetSystemMetrics(0) - Width) / 2, y = (GetSystemMetrics(1) - Height) / 2;
            s_window = CreateWindowExW(0x00040000 /* WS_EX_APPWINDOW */, className, title, 0x80000000u | 0x10000000u /* WS_POPUP | WS_VISIBLE */,
                x, y, Width, Height, 0, 0, instance, null);
        }
        _created.Set();
        if (s_window == 0) return;
        SetTimer(s_window, 1, 33, 0);
        // The picture is decoded here, after the window shows (a DDS of the original, about 100 ms).
        if (picturePath is not null)
        {
            try
            {
                var image = Data.Textures.TextureLoader.LoadFile(picturePath, allMips: false).Levels[0];
                var bgra = new byte[image.Width * image.Height * 4];
                for (int i = 0; i < bgra.Length; i += 4)
                    (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (image.Pixels[i + 2], image.Pixels[i + 1], image.Pixels[i], 255);
                (s_pictureWidth, s_pictureHeight, s_picture) = (image.Width, image.Height, bgra);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
            {
                Console.Error.WriteLine($"splash    {e.Message}");
            }
        }
        Msg msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    /// <summary>
    /// The window's procedure: WM_TIMER repaints, WM_ERASEBKGND is skipped, WM_PAINT paints, WM_DESTROY ends the loop.
    /// </summary>
    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x0113:   // WM_TIMER: the next spinner frame
                InvalidateRect(hwnd, null, 0);
                return 0;
            case 0x0014:   // WM_ERASEBKGND: the whole window is painted
                return 1;
            case 0x000F:   // WM_PAINT
                Paint(hwnd);
                return 0;
            case 0x0002:   // WM_DESTROY
                KillTimer(hwnd, 1);
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// The picture, a dark band with LOADING..., and twelve spokes round the middle, the bright one going round once a second; drawn off screen, then
    /// copied.
    /// </summary>
    static void Paint(nint hwnd)
    {
        Paintstruct ps;
        var dc = BeginPaint(hwnd, &ps);
        var memory = CreateCompatibleDC(dc);
        var bitmap = CreateCompatibleBitmap(dc, Width, Height);
        var old = SelectObject(memory, bitmap);
        var background = CreateSolidBrush(Rgb(5, 5, 4));
        var rect = new Rect { right = Width, bottom = Height };
        FillRect(memory, &rect, background);
        DeleteObject(background);
        if (s_picture is { } pixels)
        {
            var info = new Bitmapinfoheader { biSize = (uint)sizeof(Bitmapinfoheader), biWidth = s_pictureWidth, biHeight = -s_pictureHeight,
                biPlanes = 1, biBitCount = 32 };
            SetStretchBltMode(memory, 4);   // HALFTONE
            fixed (byte* bits = pixels) StretchDIBits(memory, 0, 0, Width, Height, 0, 0, s_pictureWidth, s_pictureHeight, bits, &info, 0, 0x00CC0020);
        }
        // The band along the bottom (the original's panels: dark with a rust edge), LOADING... on its left in the original's stamp face, the spinner
        // on its right.
        const int bandHeight = 72;
        var band = CreateSolidBrush(Rgb(24, 21, 18));
        var bandRect = new Rect { left = 0, top = Height - bandHeight, right = Width, bottom = Height };
        FillRect(memory, &bandRect, band);
        DeleteObject(band);
        var edge = CreateSolidBrush(Rgb(112, 62, 36));
        var edgeRect = new Rect { left = 0, top = Height - bandHeight, right = Width, bottom = Height - bandHeight + 3 };
        FillRect(memory, &edgeRect, edge);
        DeleteObject(edge);
        SetBkMode(memory, 1);   // TRANSPARENT
        var oldFont = s_font != 0 ? SelectObject(memory, s_font) : 0;
        fixed (char* text = "LOADING...")
        {
            SetTextColor(memory, Rgb(0, 0, 0));
            TextOutW(memory, 30, Height - bandHeight + 18, text, 10);   // a shadow
            SetTextColor(memory, Rgb(222, 215, 206));
            TextOutW(memory, 28, Height - bandHeight + 16, text, 10);
        }
        if (oldFont != 0) SelectObject(memory, oldFont);

        int cx = Width - 44, cy = Height - bandHeight / 2 + 1;
        double turn = (Environment.TickCount64 - s_started) / 1000.0 % 1;
        const int spokes = 12;
        for (int i = 0; i < spokes; i++)
        {
            double a = i * Math.Tau / spokes;
            double behind = ((turn * spokes - i) % spokes + spokes) % spokes / spokes;
            double k = 0.12 + 0.88 * (1 - behind);
            var pen = CreatePen(0, 3, Rgb((byte)(24 + 206 * k), (byte)(21 + 193 * k), (byte)(18 + 160 * k)));
            var previous = SelectObject(memory, pen);
            MoveToEx(memory, cx + (int)Math.Round(Math.Sin(a) * 9), cy - (int)Math.Round(Math.Cos(a) * 9), null);
            LineTo(memory, cx + (int)Math.Round(Math.Sin(a) * 19), cy - (int)Math.Round(Math.Cos(a) * 19));
            SelectObject(memory, previous);
            DeleteObject(pen);
        }
        BitBlt(dc, 0, 0, Width, Height, memory, 0, 0, 0x00CC0020);   // SRCCOPY
        SelectObject(memory, old);
        DeleteObject(bitmap);
        DeleteDC(memory);
        EndPaint(hwnd, &ps);
    }

    /// <summary>
    /// A GDI <c>COLORREF</c>.
    /// </summary>
    static uint Rgb(byte r, byte g, byte b) => r | (uint)g << 8 | (uint)b << 16;

    /// <summary>
    /// Closes the splash (the game window is showing).
    /// </summary>
    public void Dispose()
    {
        if (s_window != 0) PostMessageW(s_window, 0x0010, 0, 0);   // WM_CLOSE
        _thread.Join(TimeSpan.FromSeconds(2));
        s_window = 0;
        if (s_font != 0)
        {
            DeleteObject(s_font);
            s_font = 0;
        }
        s_picture = null;
        _created.Dispose();
    }

    // The Win32 structures keep the field names of the Windows headers.
#pragma warning disable IDE1006
    /// <summary>
    /// Win32 <c>WNDCLASSEXW</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Wndclassexw
    {
        /// <summary>
        /// The structure's size and the class styles.
        /// </summary>
        public uint cbSize, style;
        /// <summary>
        /// The window procedure.
        /// </summary>
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        /// <summary>
        /// Extra bytes for the class and each window.
        /// </summary>
        public int cbClsExtra, cbWndExtra;
        /// <summary>
        /// The module, icon, cursor and background brush.
        /// </summary>
        public nint hInstance, hIcon, hCursor, hbrBackground;
        /// <summary>
        /// The menu and class names.
        /// </summary>
        public char* lpszMenuName, lpszClassName;
        /// <summary>
        /// The small icon.
        /// </summary>
        public nint hIconSm;
    }

    /// <summary>
    /// Win32 <c>MSG</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        /// <summary>
        /// The window the message is for.
        /// </summary>
        public nint hwnd;
        /// <summary>
        /// The message.
        /// </summary>
        public uint message;
        /// <summary>
        /// The message's parameters.
        /// </summary>
        public nint wParam, lParam;
        /// <summary>
        /// When it was posted.
        /// </summary>
        public uint time;
        /// <summary>
        /// The pointer then, screen pixels.
        /// </summary>
        public int x, y;
    }

    /// <summary>
    /// Win32 <c>RECT</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        /// <summary>
        /// The rectangle's edges, pixels.
        /// </summary>
        public int left, top, right, bottom;
    }

    /// <summary>
    /// Win32 <c>PAINTSTRUCT</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Paintstruct
    {
        /// <summary>
        /// The device context to paint with.
        /// </summary>
        public nint hdc;
        /// <summary>
        /// Whether the background must be erased.
        /// </summary>
        public int fErase;
        /// <summary>
        /// The rectangle to paint.
        /// </summary>
        public Rect rcPaint;
        /// <summary>
        /// Reserved for the system.
        /// </summary>
        public int fRestore, fIncUpdate;
        /// <summary>
        /// Reserved for the system.
        /// </summary>
        public fixed byte rgbReserved[32];
    }

    /// <summary>
    /// Win32 <c>BITMAPINFOHEADER</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Bitmapinfoheader
    {
        /// <summary>
        /// The structure's size.
        /// </summary>
        public uint biSize;
        /// <summary>
        /// The bitmap's size, pixels (a negative height: top row first).
        /// </summary>
        public int biWidth, biHeight;
        /// <summary>
        /// Planes (1) and bits per pixel.
        /// </summary>
        public ushort biPlanes, biBitCount;
        /// <summary>
        /// The compression and the image's size in bytes.
        /// </summary>
        public uint biCompression, biSizeImage;
        /// <summary>
        /// The resolution, pixels per metre.
        /// </summary>
        public int biXPelsPerMeter, biYPelsPerMeter;
        /// <summary>
        /// Colours used and important.
        /// </summary>
        public uint biClrUsed, biClrImportant;
    }
#pragma warning restore IDE1006

    /// <summary>
    /// kernel32 <c>GetModuleHandleW</c>: the handle of a loaded module (null: this program's).
    /// </summary>
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    /// <summary>
    /// user32 <c>RegisterClassExW</c>: registers a window class.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(Wndclassexw* wc);
    /// <summary>
    /// user32 <c>CreateWindowExW</c>: makes a window.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial nint CreateWindowExW(uint exStyle, char* className, char* title, uint style, int x, int y,
        int w, int h, nint parent, nint menu, nint instance, void* param);
    /// <summary>
    /// user32 <c>DefWindowProcW</c>: the default handling of a window message.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    /// <summary>
    /// user32 <c>GetMessageW</c>: waits for the thread's next message; 0 after WM_QUIT.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    /// <summary>
    /// user32 <c>TranslateMessage</c>: turns key messages into character messages.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(Msg* msg);
    /// <summary>
    /// user32 <c>DispatchMessageW</c>: hands a message to its window's procedure.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(Msg* msg);
    /// <summary>
    /// user32 <c>PostQuitMessage</c>: ends the thread's message loop.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial void PostQuitMessage(int code);
    /// <summary>
    /// user32 <c>PostMessageW</c>: posts a message to a window's thread.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    /// <summary>
    /// user32 <c>SetTimer</c>: a timer that posts WM_TIMER every so many milliseconds.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint callback);
    /// <summary>
    /// user32 <c>KillTimer</c>: stops a timer.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial int KillTimer(nint hwnd, nuint id);
    /// <summary>
    /// user32 <c>InvalidateRect</c>: asks for a window (or part of it) to be painted again.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial int InvalidateRect(nint hwnd, Rect* rect, int erase);
    /// <summary>
    /// user32 <c>GetSystemMetrics</c>: a system metric (0 and 1: the screen's width and height).
    /// </summary>
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    /// <summary>
    /// user32 <c>LoadCursorW</c>: a system cursor.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial nint LoadCursorW(nint instance, nint name);
    /// <summary>
    /// user32 <c>BeginPaint</c>: starts painting a window, giving its device context.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial nint BeginPaint(nint hwnd, Paintstruct* ps);
    /// <summary>
    /// user32 <c>EndPaint</c>: ends painting a window.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial int EndPaint(nint hwnd, Paintstruct* ps);
    /// <summary>
    /// user32 <c>FillRect</c>: fills a rectangle with a brush.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial int FillRect(nint dc, Rect* rect, nint brush);
    /// <summary>
    /// gdi32 <c>CreateCompatibleDC</c>: a memory device context like a given one.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    /// <summary>
    /// gdi32 <c>CreateCompatibleBitmap</c>: a bitmap for a device context.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleBitmap(nint dc, int w, int h);
    /// <summary>
    /// gdi32 <c>SelectObject</c>: selects an object (bitmap, pen, brush, font) into a device context, giving the one it replaces.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    /// <summary>
    /// gdi32 <c>DeleteObject</c>: frees a GDI object.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(nint obj);
    /// <summary>
    /// gdi32 <c>DeleteDC</c>: frees a memory device context.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial int DeleteDC(nint dc);
    /// <summary>
    /// gdi32 <c>CreateSolidBrush</c>: a brush of one colour.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial nint CreateSolidBrush(uint colour);
    /// <summary>
    /// gdi32 <c>CreatePen</c>: a pen.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial nint CreatePen(int style, int width, uint colour);
    /// <summary>
    /// gdi32 <c>MoveToEx</c>: moves a device context's pen position.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial int MoveToEx(nint dc, int x, int y, void* previous);
    /// <summary>
    /// gdi32 <c>LineTo</c>: draws a line from the pen position.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial int LineTo(nint dc, int x, int y);
    /// <summary>
    /// gdi32 <c>SetBkMode</c>: whether text is drawn over a filled background.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint dc, int mode);
    /// <summary>
    /// gdi32 <c>SetTextColor</c>: the text colour.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(nint dc, uint colour);
    /// <summary>
    /// gdi32 <c>TextOutW</c>: draws text.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial int TextOutW(nint dc, int x, int y, char* text, int length);
    /// <summary>
    /// gdi32 <c>SetStretchBltMode</c>: how a stretched copy samples.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial int SetStretchBltMode(nint dc, int mode);
    /// <summary>
    /// gdi32 <c>StretchDIBits</c>: copies device-independent pixels to a device context, scaled.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial int StretchDIBits(nint dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh,
        void* bits, Bitmapinfoheader* info, uint usage, uint rop);
    /// <summary>
    /// gdi32 <c>AddFontResourceExW</c>: adds a font file (FR_PRIVATE: for this process only).
    /// </summary>
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int AddFontResourceExW(string path, uint flags,
        nint reserved);
    /// <summary>
    /// gdi32 <c>CreateFontW</c>: a font of a face and size.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
            char* face);
    /// <summary>
    /// gdi32 <c>BitBlt</c>: copies a rectangle between device contexts.
    /// </summary>
    [LibraryImport("gdi32.dll")] private static partial int BitBlt(nint dc, int x, int y, int w, int h, nint source, int sx, int sy, uint rop);
}
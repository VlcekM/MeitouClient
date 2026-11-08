using System.Runtime.InteropServices;

namespace Meitou.Game;

/// <summary>
/// The splash shown from the start of the program until the game window draws its first loading frame (docs/engine.md "Loading"): nothing can be drawn with
/// Vulkan before the device exists (a second or two), so this is a plain Win32 window painted with GDI on a thread of its own: one of the original's loading
/// pictures, LOADING... and a spinner. Windows only; elsewhere it shows nothing.
/// </summary>
sealed unsafe partial class SplashWindow : IDisposable
{
    const int Width = 960, Height = 540;
    readonly Thread thread;
    readonly ManualResetEventSlim created = new();
    static nint window;
    static byte[]? picture;   // BGRA, top row first
    static int pictureWidth, pictureHeight;
    static long started;
    static nint font;   // the original's LOADING... face (Kenshi_UI_Messages: MailartRubberstamp-Regular.ttf), 0 when missing

    SplashWindow(string? picturePath, string? fontPath)
    {
        thread = new Thread(() => Run(picturePath, fontPath)) { IsBackground = true, Name = "splash" };
        thread.Start();
        created.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>Shows the splash (null off Windows or when it cannot be made). <paramref name="dataDirectory"/> is the game's <c>data</c> folder, for a loading picture.</summary>
    public static SplashWindow? Show(string? dataDirectory)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? path = null, fontPath = null;
        if (dataDirectory is not null)
        {
            var pictures = Enumerable.Range(0, 16).Select(i => Path.Combine(dataDirectory, "gui", "gfx", $"loading_screen{i}.dds")).Where(File.Exists).ToArray();
            if (pictures.Length > 0) path = pictures[Random.Shared.Next(pictures.Length)];
            if (Path.Combine(dataDirectory, "gui", "fonts", "MailartRubberstamp-Regular.ttf") is var stamp && File.Exists(stamp)) fontPath = stamp;
        }
        try { return new SplashWindow(path, fontPath); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    void Run(string? picturePath, string? fontPath)
    {
        started = Environment.TickCount64;
        if (fontPath is not null && AddFontResourceExW(fontPath, 0x10 /* FR_PRIVATE */, 0) > 0)
            fixed (char* face = "Mailart Rubberstamp") font = CreateFontW(-40, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, face);
        var instance = GetModuleHandleW(null);
        fixed (char* className = "MeitouSplash")
        fixed (char* title = "Meitou")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = instance, lpszClassName = className,
                hCursor = LoadCursorW(0, (nint)32514),   // IDC_WAIT
            };
            RegisterClassExW(&wc);
            int x = (GetSystemMetrics(0) - Width) / 2, y = (GetSystemMetrics(1) - Height) / 2;
            window = CreateWindowExW(0x00040000 /* WS_EX_APPWINDOW */, className, title, 0x80000000u | 0x10000000u /* WS_POPUP | WS_VISIBLE */, x, y, Width, Height, 0, 0, instance, null);
        }
        created.Set();
        if (window == 0) return;
        SetTimer(window, 1, 33, 0);
        // The picture is decoded here, after the window shows (a DDS of the original, about 100 ms).
        if (picturePath is not null)
        {
            try
            {
                var image = Meitou.Data.Textures.TextureLoader.LoadFile(picturePath, allMips: false).Levels[0];
                var bgra = new byte[image.Width * image.Height * 4];
                for (int i = 0; i < bgra.Length; i += 4)
                    (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (image.Pixels[i + 2], image.Pixels[i + 1], image.Pixels[i], 255);
                (pictureWidth, pictureHeight, picture) = (image.Width, image.Height, bgra);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException) { Console.Error.WriteLine($"splash    {e.Message}"); }
        }
        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x0113: InvalidateRect(hwnd, null, 0); return 0;   // WM_TIMER: the next spinner frame
            case 0x0014: return 1;   // WM_ERASEBKGND: the whole window is painted
            case 0x000F: Paint(hwnd); return 0;   // WM_PAINT
            case 0x0002: KillTimer(hwnd, 1); PostQuitMessage(0); return 0;   // WM_DESTROY
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>The picture, a dark band with LOADING..., and twelve spokes round the middle, the bright one going round once a second; drawn off screen, then copied.</summary>
    static void Paint(nint hwnd)
    {
        PAINTSTRUCT ps;
        var dc = BeginPaint(hwnd, &ps);
        var memory = CreateCompatibleDC(dc);
        var bitmap = CreateCompatibleBitmap(dc, Width, Height);
        var old = SelectObject(memory, bitmap);
        var background = CreateSolidBrush(Rgb(5, 5, 4));
        var rect = new RECT { right = Width, bottom = Height };
        FillRect(memory, &rect, background);
        DeleteObject(background);
        if (picture is { } pixels)
        {
            var info = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = pictureWidth, biHeight = -pictureHeight, biPlanes = 1, biBitCount = 32 };
            SetStretchBltMode(memory, 4);   // HALFTONE
            fixed (byte* bits = pixels) StretchDIBits(memory, 0, 0, Width, Height, 0, 0, pictureWidth, pictureHeight, bits, &info, 0, 0x00CC0020);
        }
        // The band along the bottom (the original's panels: dark with a rust edge), LOADING... on its left in the original's stamp face, the spinner on its right.
        const int BandHeight = 72;
        var band = CreateSolidBrush(Rgb(24, 21, 18));
        var bandRect = new RECT { left = 0, top = Height - BandHeight, right = Width, bottom = Height };
        FillRect(memory, &bandRect, band);
        DeleteObject(band);
        var edge = CreateSolidBrush(Rgb(112, 62, 36));
        var edgeRect = new RECT { left = 0, top = Height - BandHeight, right = Width, bottom = Height - BandHeight + 3 };
        FillRect(memory, &edgeRect, edge);
        DeleteObject(edge);
        SetBkMode(memory, 1);   // TRANSPARENT
        var oldFont = font != 0 ? SelectObject(memory, font) : 0;
        fixed (char* text = "LOADING...")
        {
            SetTextColor(memory, Rgb(0, 0, 0));
            TextOutW(memory, 30, Height - BandHeight + 18, text, 10);   // a shadow
            SetTextColor(memory, Rgb(222, 215, 206));
            TextOutW(memory, 28, Height - BandHeight + 16, text, 10);
        }
        if (oldFont != 0) SelectObject(memory, oldFont);

        int cx = Width - 44, cy = Height - BandHeight / 2 + 1;
        double turn = (Environment.TickCount64 - started) / 1000.0 % 1;
        const int Spokes = 12;
        for (int i = 0; i < Spokes; i++)
        {
            double a = i * Math.Tau / Spokes;
            double behind = ((turn * Spokes - i) % Spokes + Spokes) % Spokes / Spokes;
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

    static uint Rgb(byte r, byte g, byte b) => r | (uint)g << 8 | (uint)b << 16;

    /// <summary>Closes the splash (the game window is showing).</summary>
    public void Dispose()
    {
        if (window != 0) PostMessageW(window, 0x0010, 0, 0);   // WM_CLOSE
        thread.Join(TimeSpan.FromSeconds(2));
        window = 0;
        if (font != 0) { DeleteObject(font); font = 0; }
        picture = null;
        created.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; public fixed byte rgbReserved[32]; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll")] private static partial nint CreateWindowExW(uint exStyle, char* className, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, void* param);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(MSG* msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(MSG* msg);
    [LibraryImport("user32.dll")] private static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] private static partial int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint callback);
    [LibraryImport("user32.dll")] private static partial int KillTimer(nint hwnd, nuint id);
    [LibraryImport("user32.dll")] private static partial int InvalidateRect(nint hwnd, RECT* rect, int erase);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial nint LoadCursorW(nint instance, nint name);
    [LibraryImport("user32.dll")] private static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] private static partial int EndPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] private static partial int FillRect(nint dc, RECT* rect, nint brush);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleBitmap(nint dc, int w, int h);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] private static partial int DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateSolidBrush(uint colour);
    [LibraryImport("gdi32.dll")] private static partial nint CreatePen(int style, int width, uint colour);
    [LibraryImport("gdi32.dll")] private static partial int MoveToEx(nint dc, int x, int y, void* previous);
    [LibraryImport("gdi32.dll")] private static partial int LineTo(nint dc, int x, int y);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(nint dc, uint colour);
    [LibraryImport("gdi32.dll")] private static partial int TextOutW(nint dc, int x, int y, char* text, int length);
    [LibraryImport("gdi32.dll")] private static partial int SetStretchBltMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] private static partial int StretchDIBits(nint dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, void* bits, BITMAPINFOHEADER* info, uint usage, uint rop);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int AddFontResourceExW(string path, uint flags, nint reserved);
    [LibraryImport("gdi32.dll")] private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, char* face);
    [LibraryImport("gdi32.dll")] private static partial int BitBlt(nint dc, int x, int y, int w, int h, nint source, int sx, int sy, uint rop);
}

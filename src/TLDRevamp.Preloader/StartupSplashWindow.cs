using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;

namespace TLDRevamp.Preloader
{
    internal sealed class StartupSplashWindow
    {
        const uint WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_ERASEBKGND = 0x0014, WM_MOUSEACTIVATE = 0x0021, WM_TIMER = 0x0113;
        const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080,
            WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
        const int Width = 508, Height = 164, TimeoutMs = 60000, FadeMs = 132;
        static int started;
        readonly SplashProgress progress;
        readonly string[] args;
        readonly Stopwatch time = Stopwatch.StartNew();
        readonly WindowProc procedure;
        SplashSnapshot snapshot;
        IntPtr window, memory, bitmap, oldBitmap, background, track, fill, smallFont, titleFont;
        int width, height;
        double scale = 1;
        long fadeStart = -1;
        bool done;

        StartupSplashWindow(SplashProgress progress, string[] args)
        { this.progress = progress; this.args = args; procedure = WndProc; snapshot = progress.Snapshot(); }

        internal static void Start()
        {
            try
            {
                if (Interlocked.Exchange(ref started, 1) != 0 || Environment.OSVersion.Platform != PlatformID.Win32NT) return;
                var args = Environment.GetCommandLineArgs();
                if (SplashOptions.Skip(args) || !SplashOptions.EnabledFile(Path.Combine(Paths.ConfigPath, "tldrevamp.core.cfg"))) return;
                var progress = new SplashProgress();
                var splash = new StartupSplashWindow(progress, args);
                AppDomain.CurrentDomain.SetData(StartupSplash.DataKey, new Action<int, int, int>(progress.Report));
                new Thread(() => { try { splash.Run(); } catch { ClearBridge(); } })
                    { IsBackground = true, Name = "RevaMP startup splash" }.Start();
            }
            catch { ClearBridge(); }
        }

        static void ClearBridge()
        {
            try { AppDomain.CurrentDomain.SetData(StartupSplash.DataKey, null); }
            catch { }
        }

        void Run()
        {
            string name = "RevaMP.StartupSplash";
            IntPtr instance = IntPtr.Zero;
            ushort atom = 0;
            UnhandledExceptionEventHandler failed = (sender, e) => progress.Report(StartupSplash.Failed, 0, 0);
            try
            {
                AppDomain.CurrentDomain.UnhandledException += failed;
                if (progress.Snapshot().Closing || time.ElapsedMilliseconds >= TimeoutMs) return;
                // Thread-local awareness leaves Unity's process/window DPI policy alone.
                try
                {
                    if (SetThreadDpiAwarenessContext(new IntPtr(-4)) == IntPtr.Zero) SetThreadDpiAwarenessContext(new IntPtr(-3));
                }
                catch (EntryPointNotFoundException) { }
                var monitors = new List<MonitorInfo>();
                bool enumFailed = false;
                MonitorProc collect = (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
                {
                    try
                    {
                        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                        if (GetMonitorInfo(monitor, ref info) && info.Monitor.Right > info.Monitor.Left && info.Monitor.Bottom > info.Monitor.Top)
                            monitors.Add(info);
                        return true;
                    }
                    catch { enumFailed = true; return false; }
                };
                if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero) || enumFailed || monitors.Count == 0) return;
                int primary = monitors.FindIndex(m => (m.Flags & 1) != 0);
                var bounds = monitors[SplashOptions.MonitorIndex(args, monitors.Count, Math.Max(0, primary))].Monitor;
                using (var process = Process.GetCurrentProcess()) instance = process.MainModule.BaseAddress;
                var wc = new WindowClass
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
                    Instance = instance, ClassName = name
                };
                atom = RegisterClassEx(ref wc);
                if (atom == 0) return;
                window = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
                    name, "RevaMP", WS_POPUP, bounds.Left, bounds.Top, Width, Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (window == IntPtr.Zero) return;
                uint dpi = 96;
                try { dpi = GetDpiForWindow(window); }
                catch (EntryPointNotFoundException)
                {
                    IntPtr dc = GetDC(window);
                    try { if (dc != IntPtr.Zero) dpi = (uint)GetDeviceCaps(dc, 88); }
                    finally { if (dc != IntPtr.Zero) ReleaseDC(window, dc); }
                }
                scale = (dpi == 0 ? 96 : dpi) / 96d;
                scale = Math.Min(scale, Math.Min((bounds.Right - bounds.Left) / (double)Width, (bounds.Bottom - bounds.Top) / (double)Height));
                width = S(Width); height = S(Height);
                if (!SetWindowPos(window, new IntPtr(-1), bounds.Left + (bounds.Right - bounds.Left - width) / 2,
                        bounds.Top + (bounds.Bottom - bounds.Top - height) / 2, width, height, 0x0010)) return; // SWP_NOACTIVATE
                CreateDrawing();
                if (!SetLayeredWindowAttributes(window, 0, 255, 2) || SetTimer(window, new UIntPtr(1), 33, IntPtr.Zero) == UIntPtr.Zero) return;
                if (done || progress.Snapshot().Closing || time.ElapsedMilliseconds >= TimeoutMs) return;
                ShowWindow(window, 4); // SW_SHOWNOACTIVATE
                InvalidateRect(window, IntPtr.Zero, false);
                while (!done && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                    if (time.ElapsedMilliseconds >= TimeoutMs) break;
                }
            }
            catch { }
            finally
            {
                ClearBridge();
                try { AppDomain.CurrentDomain.UnhandledException -= failed; } catch { }
                if (window != IntPtr.Zero)
                {
                    try { KillTimer(window, new UIntPtr(1)); ShowWindow(window, 0); DestroyWindow(window); } catch { }
                    window = IntPtr.Zero;
                }
                try
                {
                    if (memory != IntPtr.Zero && oldBitmap != IntPtr.Zero) SelectObject(memory, oldBitmap);
                    if (memory != IntPtr.Zero) DeleteDC(memory);
                    foreach (IntPtr obj in new[] { bitmap, background, track, fill, smallFont, titleFont })
                        if (obj != IntPtr.Zero) DeleteObject(obj);
                }
                catch { }
                try { if (atom != 0) UnregisterClass(name, instance); } catch { }
                GC.KeepAlive(procedure);
            }
        }

        int S(int value) => Math.Max(1, (int)Math.Round(value * scale));
        static IntPtr Required(IntPtr handle)
        {
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) throw new InvalidOperationException("Splash GDI allocation failed");
            return handle;
        }

        void CreateDrawing()
        {
            IntPtr dc = Required(GetDC(window));
            try
            {
                memory = Required(CreateCompatibleDC(dc));
                bitmap = Required(CreateCompatibleBitmap(dc, width, height));
                oldBitmap = Required(SelectObject(memory, bitmap));
            }
            finally { ReleaseDC(window, dc); }
            background = Required(CreateSolidBrush(0x0E110E));
            track = Required(CreateSolidBrush(0x404040));
            fill = Required(CreateSolidBrush(0xC8FFC8));
            smallFont = Required(CreateFont(-S(9), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"));
            titleFont = Required(CreateFont(-S(24), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"));
            SetBkMode(memory, 1); // TRANSPARENT
        }

        IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam)
        {
            try
            {
                switch (message)
                {
                    case WM_ERASEBKGND: return new IntPtr(1);
                    case WM_MOUSEACTIVATE: return new IntPtr(3); // MA_NOACTIVATE
                    case WM_CLOSE: done = true; return IntPtr.Zero;
                    case WM_TIMER: Tick(); return IntPtr.Zero;
                    case WM_PAINT: Paint(hwnd); return IntPtr.Zero;
                }
                return DefWindowProc(hwnd, message, wparam, lparam);
            }
            catch { done = true; return IntPtr.Zero; }
        }

        void Tick()
        {
            snapshot = progress.Snapshot();
            long now = time.ElapsedMilliseconds;
            if (snapshot.Failed || now >= TimeoutMs) { done = true; return; }
            if (snapshot.Closing)
            {
                if (fadeStart < 0) fadeStart = now;
                if (now - fadeStart >= FadeMs) { done = true; return; }
                if (!SetLayeredWindowAttributes(window, 0, (byte)(255 * (FadeMs - (now - fadeStart)) / FadeMs), 2))
                { done = true; return; }
            }
            InvalidateRect(window, IntPtr.Zero, false);
        }

        void Paint(IntPtr hwnd)
        {
            IntPtr dc = BeginPaint(hwnd, out var paint);
            try
            {
                if (dc == IntPtr.Zero || memory == IntPtr.Zero || smallFont == IntPtr.Zero || titleFont == IntPtr.Zero) return;
                var rect = new Rect { Right = width, Bottom = height };
                FillRect(memory, ref rect, background);
                Text("MULTIPLAYER ENHANCED BY", smallFont, 0x327D2E, 42, 57);
                Text("RevaMP", titleFont, 0xC8FFC8, 62, 94);
                rect = new Rect { Left = S(11), Top = S(101), Right = width - S(19), Bottom = S(101) + S(2) };
                FillRect(memory, ref rect, track);
                int pixels = (int)Math.Round((rect.Right - rect.Left) * snapshot.Fraction);
                if (pixels > 0) { rect.Right = rect.Left + pixels; FillRect(memory, ref rect, fill); }
                Text(snapshot.Status, smallFont, 0xC8FFC8, 109, 125);
                BitBlt(dc, 0, 0, width, height, memory, 0, 0, 0x00CC0020); // SRCCOPY
            }
            finally { EndPaint(hwnd, ref paint); }
        }

        void Text(string value, IntPtr font, uint color, int top, int bottom)
        {
            IntPtr old = Required(SelectObject(memory, font));
            try
            {
                SetTextColor(memory, color);
                var rect = new Rect { Right = width, Top = S(top), Bottom = S(bottom) };
                DrawText(memory, value, value.Length, ref rect, 0x0025); // CENTER | VCENTER | SINGLELINE
            }
            finally { SelectObject(memory, old); }
        }

        delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        delegate bool MonitorProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MonitorInfo
        {
            public uint Size;
            public Rect Monitor, Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass
        {
            public uint Size, Style;
            public IntPtr Procedure;
            public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            public string MenuName, ClassName;
            public IntPtr SmallIcon;
        }
        [StructLayout(LayoutKind.Sequential)] struct Message
        {
            public IntPtr Window;
            public uint Id;
            public IntPtr Wparam, Lparam;
            public uint Time;
            public int X, Y;
            public uint Private;
        }
        [StructLayout(LayoutKind.Sequential)] struct PaintStruct
        {
            public IntPtr DC;
            public int Erase;
            public Rect Paint;
            public int Restore, IncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
        }

        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WindowClass wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string name, IntPtr instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint interval, IntPtr callback);
        [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMessage(out Message message, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
        [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);
        [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawText(IntPtr dc, string text, int count, ref Rect rect, uint format);
        [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFont(int height, int width, int escapement, int orientation,
            int weight, uint italic, uint underline, uint strikeout, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr dc, uint color);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    }
}

using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;

namespace TLDInstalls.App;

static class Program
{
    /// <summary>TLDCopies: the window. `TLDCopies --render out.png [--dark|--light] [--width W --height H] [--wait S]
    /// [--make NAME]` draws it without a display into a PNG (to look at the layout; --make first makes a vanilla copy
    /// through the New copy panel).</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var i = Array.IndexOf(args, "--render");
        if (i >= 0 && i + 1 < args.Length) return Render(args[i + 1], args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    static int Render(string png, string[] args)
    {
        int Arg(string n, int d) => Array.IndexOf(args, n) is var j and >= 0 && j + 1 < args.Length ? int.Parse(args[j + 1]) : d;
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        if (args.Contains("--dark")) Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        if (args.Contains("--light")) Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var w = new MainWindow { Width = Arg("--width", 1180), Height = Arg("--height", 760) };
        w.Show();
        if (Array.IndexOf(args, "--make") is var m and >= 0 && m + 1 < args.Length) _ = w.MakeForRender(args[m + 1]);
        // let the background loads (copies, sizes, releases) finish
        var until = DateTime.UtcNow.AddSeconds(Arg("--wait", 6));
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(50); }
        w.CaptureRenderedFrame()?.Save(png);
        return 0;
    }
}

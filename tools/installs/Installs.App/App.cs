using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace TLDInstalls.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;   // light or dark, as the system is
        Resources.ThemeDictionaries[ThemeVariant.Light] = Palette(light: true);
        Resources.ThemeDictionaries[ThemeVariant.Dark] = Palette(light: false);
    }

    /// <summary>The window's own colours (the Fluent theme does the controls).</summary>
    static ResourceDictionary Palette(bool light)
    {
        IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
        return light
            ? new ResourceDictionary
            {
                ["Kind.source"] = B("#B54708"), ["KindBg.source"] = B("#FFFAEB"),
                ["Bg"] = B("#F3F4F6"), ["Card"] = B("#FFFFFF"), ["CardLine"] = B("#E2E5EA"), ["Muted"] = B("#667085"), ["Text"] = B("#111827"),
                ["Panel"] = B("#FFFFFF"), ["LogBg"] = B("#F8F9FB"), ["Warn"] = B("#B42318"), ["Ok"] = B("#067647"),
                ["Kind.clean"] = B("#175CD3"), ["Kind.vanilla"] = B("#475467"), ["Kind.release"] = B("#067647"), ["Kind.dev build"] = B("#B54708"), ["Kind.test"] = B("#6941C6"),
                ["KindBg.clean"] = B("#EFF8FF"), ["KindBg.vanilla"] = B("#F2F4F7"), ["KindBg.release"] = B("#ECFDF3"), ["KindBg.dev build"] = B("#FFFAEB"), ["KindBg.test"] = B("#F4F3FF"),
            }
            : new ResourceDictionary
            {
                ["Kind.source"] = B("#FDB022"), ["KindBg.source"] = B("#33260C"),
                ["Bg"] = B("#15171B"), ["Card"] = B("#1E2126"), ["CardLine"] = B("#2E333B"), ["Muted"] = B("#98A2B3"), ["Text"] = B("#F2F4F7"),
                ["Panel"] = B("#1E2126"), ["LogBg"] = B("#121418"), ["Warn"] = B("#F97066"), ["Ok"] = B("#47CD89"),
                ["Kind.clean"] = B("#84CAFF"), ["Kind.vanilla"] = B("#D0D5DD"), ["Kind.release"] = B("#47CD89"), ["Kind.dev build"] = B("#FDB022"), ["Kind.test"] = B("#BDB4FE"),
                ["KindBg.clean"] = B("#102A45"), ["KindBg.vanilla"] = B("#2B3038"), ["KindBg.release"] = B("#0F2E20"), ["KindBg.dev build"] = B("#33260C"), ["KindBg.test"] = B("#272347"),
            };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

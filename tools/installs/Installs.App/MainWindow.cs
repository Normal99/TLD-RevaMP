using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace TLDInstalls.App;

/// <summary>The window: a card per copy (Launch/Stop, Update, Open folder, Remove), the New copy panel, and the log of
/// what the current job does. All the work is TLDInstalls.Core's (the same as `tld-installs` on the command line).</summary>
public sealed class MainWindow : Window
{
    Store store = new();
    List<CopyInfo> copies = new();
    HashSet<string> running = new();
    HashSet<string> running2 = new();
    readonly string? sandboxie = SecondAccount.Sandboxie();
    List<Release> releases = new();
    SteamGame? steam;
    string? steamBuild;
    string? steamBranch;
    bool busy;
    bool fetchingSource;
    DateTime sourceChecked;
    readonly Dictionary<string, int> newer = new();
    /// <summary>Bug reports being typed, per copy: the cards are built again whenever a copy changes.</summary>
    readonly Dictionary<string, string> bugDrafts = new();

    readonly WrapPanel cards = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock rootText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    readonly TextBlock steamText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock emptyText = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly TextBox nameBox = new() { Watermark = "e.g. release-0.68.5" };
    readonly ComboBox releaseBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "loading the releases…" };
    readonly RadioButton rbClean = new() { GroupName = "kind" }, rbVanilla = new() { GroupName = "kind" }, rbRelease = new() { GroupName = "kind", IsChecked = true },
                         rbDev = new() { GroupName = "kind" }, rbTest = new() { GroupName = "kind" }, rbSource = new() { GroupName = "kind" };
    readonly TextBox refBox = new() { Text = SourceBuild.DefaultRef, Watermark = "branch or commit SHA", IsEnabled = false };
    readonly TextBlock estimateText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock sourceText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly Button makeButton = new() { Content = "Make copy", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly TextBox logBox = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontSize = 12, BorderThickness = new Thickness(0),
                                      FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,Liberation Mono,monospace") };
    readonly TextBlock statusText = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ProgressBar progress = new() { IsIndeterminate = true, Width = 120, MinHeight = 4, Height = 4, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    readonly ScrollViewer logScroll = new() { Height = 150, IsVisible = false };
    readonly Button logButton = new() { Content = "Show log", FontSize = 12, Padding = new Thickness(10, 2), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };

    public MainWindow()
    {
        Title = "TLD Copies";
        Width = 1180; Height = 780; MinWidth = 820; MinHeight = 560;
        Res(this, BackgroundProperty, "Bg");
        Content = Layout();
        makeButton.Classes.Add("accent");
        makeButton.Click += async (_, _) => await MakeClicked();
        foreach (var rb in new[] { rbClean, rbVanilla, rbRelease, rbDev, rbTest, rbSource }) rb.IsCheckedChanged += (_, _) => KindChanged();
        if (Env.RepoDir == null)
            foreach (var rb in new[] { rbDev, rbTest }) { rb.IsEnabled = false; ToolTip.SetTip(rb, "built from a checkout of the repository: run TLD Copies from one"); }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) => { if (!busy) { Reload(); await RefreshSource(); } };
        timer.Start();
        Opened += async (_, _) => await Start();
    }

    // ---- layout

    Control Layout()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), ColumnDefinitions = new ColumnDefinitions("*,340") };

        // header
        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(new TextBlock { Text = "TLD Copies", FontSize = 24, FontWeight = FontWeight.Bold });
        title.Children.Add(Muted("Copies of The Long Drive next to Steam's, each with its own saves. Steam's own install is never changed.", 13));
        var where = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 460 };
        var change = new Button { Content = "Change…", FontSize = 12, Padding = new Thickness(10, 3), VerticalAlignment = VerticalAlignment.Center };
        change.Click += async (_, _) => await ChangeRoot();
        var rootRow = new DockPanel();
        DockPanel.SetDock(change, Dock.Right);
        rootRow.Children.Add(change);
        var rootLabel = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        rootLabel.Children.Add(Muted("Copies folder", 11));
        rootLabel.Children.Add(rootText);
        rootRow.Children.Add(rootLabel);
        where.Children.Add(rootRow);
        Res(steamText, TextBlock.ForegroundProperty, "Muted");
        where.Children.Add(steamText);
        var header = new DockPanel { Margin = new Thickness(24, 20, 24, 14) };
        DockPanel.SetDock(where, Dock.Right);
        header.Children.Add(where);
        header.Children.Add(title);
        Grid.SetColumnSpan(header, 2);
        root.Children.Add(header);

        // the cards
        var list = new StackPanel();
        Res(emptyText, TextBlock.ForegroundProperty, "Muted");
        emptyText.Text = "No copies yet. Make one with the New copy panel: clean for the game alone, vanilla without gameplay mods, a release, a dev build of this checkout, or the test copy the test tools run.";
        list.Children.Add(emptyText);
        list.Children.Add(cards);
        var scroll = new ScrollViewer { Content = list, Padding = new Thickness(24, 4, 10, 10), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // new copy
        var panel = NewCopyPanel();
        Grid.SetRow(panel, 1); Grid.SetColumn(panel, 1);
        root.Children.Add(panel);

        // the job: one status line; its log behind Show log (opened by itself on a failure)
        var job = new Border { Margin = new Thickness(24, 4, 24, 14), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true };
        Res(job, Border.BackgroundProperty, "LogBg"); Res(job, Border.BorderBrushProperty, "CardLine");
        var jobGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        var bar = new DockPanel { Margin = new Thickness(14, 4, 6, 4) };
        logButton.Click += (_, _) => ShowLog(!logScroll.IsVisible);
        DockPanel.SetDock(logButton, Dock.Right);
        bar.Children.Add(logButton);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        statusText.Text = "Ready";
        left.Children.Add(statusText);
        left.Children.Add(progress);
        bar.Children.Add(left);
        jobGrid.Children.Add(bar);
        logBox.Background = Brushes.Transparent;
        logBox.Padding = new Thickness(14, 6);
        logScroll.Content = logBox;
        Grid.SetRow(logScroll, 1);
        jobGrid.Children.Add(logScroll);
        job.Child = jobGrid;
        Grid.SetRow(job, 2); Grid.SetColumnSpan(job, 2);
        root.Children.Add(job);
        return root;
    }

    Control NewCopyPanel()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(new TextBlock { Text = "New copy", FontSize = 17, FontWeight = FontWeight.SemiBold });
        Res(sourceText, TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(sourceText);
        p.Children.Add(Label("Name"));
        p.Children.Add(nameBox);
        p.Children.Add(Label("What goes in"));
        p.Children.Add(Choice(rbClean, "Clean", "The game exactly as Steam has it: no BepInEx, no mods; its own save folder"));
        p.Children.Add(Choice(rbVanilla, "Vanilla", Env.Windows ? "No gameplay mods; BepInEx + save isolation only" : "The game alone, no mods"));
        p.Children.Add(Choice(rbRelease, "Release", "BepInEx and a TLD RevaMP release"));
        p.Children.Add(releaseBox);
        p.Children.Add(Choice(rbSource, "Newest dev, built here", "The newest commit, no draft needed. Requires Git and the .NET SDK."));
        p.Children.Add(refBox);
        p.Children.Add(Choice(rbDev, "Dev build", "This checkout built as a dev build (bridge off)"));
        p.Children.Add(Choice(rbTest, "Test", "This checkout's test build, debug bridge on"));
        Res(estimateText, TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(estimateText);
        p.Children.Add(makeButton);
        var b = new Border { Child = p, Padding = new Thickness(18), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(4, 4, 24, 10), VerticalAlignment = VerticalAlignment.Top };
        Res(b, Border.BackgroundProperty, "Panel"); Res(b, Border.BorderBrushProperty, "CardLine");
        return new ScrollViewer { Content = b };
    }

    static Control Choice(RadioButton rb, string title, string sub)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14 });
        s.Children.Add(Muted(sub, 12));
        rb.Content = s;
        return rb;
    }

    Control Card(CopyInfo c)
    {
        bool isRunning = running.Contains(c.Name);
        var s = new StackPanel { Spacing = 5 };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (isRunning) badges.Children.Add(Pill("● running", "Ok", "KindBg.release"));
        badges.Children.Add(Pill(c.KindLabel, "Kind." + c.KindLabel, "KindBg." + c.KindLabel));
        DockPanel.SetDock(badges, Dock.Right);
        top.Children.Add(badges);
        top.Children.Add(new TextBlock { Text = c.Name, FontSize = 19, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        s.Children.Add(top);

        s.Children.Add(new TextBlock { Text = c.Kind == Kind.Clean ? "No BepInEx, no mods" : c.Kind == Kind.Vanilla ? "No gameplay mods" : $"TLD RevaMP {c.RevampVersion}", FontSize = 15, FontWeight = FontWeight.Medium });
        var src = c.Kind switch
        {
            Kind.Clean => "the game exactly as Steam has it; its own save folder",
            Kind.Release => $"release {c.RevampSource}, BepInEx {c.BepInEx}",
            Kind.Dev => $"dev build of commit {c.RevampSource}, BepInEx {c.BepInEx}",
            Kind.Test => $"test build of commit {c.RevampSource}, debug bridge on",
            Kind.Source => c.InstalledIdentity().Commit == c.Commit?.Sha
                ? $"{c.Commit?.Title}\n{c.Commit?.Date} · {c.SourceRef}" : c.SourceRef ?? SourceBuild.DefaultRef,
            _ => Env.Windows ? "Steam's game, BepInEx + save isolation only" : "the game exactly as Steam has it",
        };
        s.Children.Add(Muted(src, 12));
        if (c.HasRevamp) s.Children.Add(Muted(c.InstalledSourceLabel, 12));
        if (c.Kind == Kind.Source)
        {
            if (newer.GetValueOrDefault(c.Name, c.NewerCommits ?? 0) is var n && n > 0)
                s.Children.Add(Muted($"{n} newer commits on {c.SourceRef}", 12));
            var auto = new CheckBox { Content = "Update to the newest commit before launching", IsChecked = c.UpdateBeforeLaunch, FontSize = 12, IsEnabled = !busy && !isRunning };
            auto.IsCheckedChanged += (_, _) => { c.UpdateBeforeLaunch = auto.IsChecked == true; c.Save(); };
            s.Children.Add(auto);
            var reference = new TextBox { Text = c.SourceRef, Watermark = "branch or commit SHA", FontSize = 12, IsEnabled = !busy && !isRunning };
            s.Children.Add(reference);
            var build = Btn("Build"); build.IsEnabled = !busy && !isRunning;
            build.Click += async (_, _) => await Job($"Building {c.Name}", log => new Builder(store, log).Build(c.Name, reference.Text?.Trim()));
            s.Children.Add(build);
        }
        var game = Muted(c.GameLabel, 12);
        s.Children.Add(game);
        if (steamBuild != null && (c.GameBuildId != steamBuild || c.GameBranch != steamBranch))
        {
            var w = new TextBlock { Text = $"Steam has the {steamBranch ?? "unknown"} branch, build {steamBuild}: Update brings it over", FontSize = 12, TextWrapping = TextWrapping.Wrap };
            Res(w, TextBlock.ForegroundProperty, "Warn");
            s.Children.Add(w);
        }
        s.Children.Add(Muted(Files.Human(c.SizeBytes) + (c.OwnBytes is { } o ? $" · {Files.Human(o)} of its own" + (c.Reflink ? " (the rest shared with Steam's)" : "") : ""), 12));
        s.Children.Add(Muted(Env.Windows ? c.Kind == Kind.Clean ? $"Saves: {c.UserData}; PlayerPrefs shared with Steam" : "Saves: its own UserData folder; registry settings per copy"
                           : c.Kind == Kind.Test && c.Compat != null && !c.Compat.StartsWith(c.Dir) ? "Saves: the tests' Proton prefix" : "Saves: its own Proton prefix", 12));
        s.Children.Add(Muted(c.LastLaunched is { } l ? $"Last launched {l.ToLocalTime():d MMM yyyy, HH:mm}" : "Never launched", 12));
        if (!string.IsNullOrWhiteSpace(c.LaunchOptions))
        {
            var lo = Muted("Launch options: " + c.LaunchOptions, 12);
            lo.TextWrapping = TextWrapping.NoWrap; lo.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(lo, c.LaunchOptions);
            s.Children.Add(lo);
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var launch = Btn(isRunning ? "Stop" : "Launch", accent: !isRunning);
        if (isRunning) ToolTip.SetTip(launch, "closes the game at once (it doesn't save)");
        launch.Click += async (_, _) => { if (isRunning) await Job($"Stopping {c.Name}", log => { TLDInstalls.Launcher.Stop(store, c, log); return Task.CompletedTask; }); else await Launch(c); };
        var update = Btn("Update");
        ToolTip.SetTip(update, c.Kind switch
        {
            Kind.Clean or Kind.Vanilla => "game files up to Steam's",
            Kind.Release => "game files up to Steam's, and the release again (or another one)",
            Kind.Source => "fetch the ref and build if its commit changed; game files up to Steam's",
            _ => "game files up to Steam's, and the checkout built again",
        });
        if (c.Kind == Kind.Release && releases.Count > 0)
        {
            var menu = new MenuFlyout();
            var keep = new MenuItem { Header = $"Keep {c.RevampVersion}" };
            keep.Click += async (_, _) => await Update(c, null);
            menu.Items.Add(keep);
            menu.Items.Add(new Separator());
            foreach (var r in releases.Take(10).Where(r => r.Version != c.RevampVersion))
            {
                var mi = new MenuItem { Header = $"Change to {r.Label}" };
                mi.Click += async (_, _) => await Update(c, r.Version);
                menu.Items.Add(mi);
            }
            update.Flyout = menu;
        }
        else update.Click += async (_, _) => await Update(c, null);
        var open = Btn("Open folder");
        open.Click += (_, _) => OpenFolder(c.Dir);
        var options = Btn("Launch options");
        ToolTip.SetTip(options, "variables, a wrapper like gamemoderun %command%, and game arguments, as in Steam (used from the next launch)");
        options.Click += async (_, _) => await EditOptions(c);
        var remove = Btn("Remove");
        remove.Click += async (_, _) => await Remove(c);
        foreach (var b in new[] { launch, update, open, options, remove })
        {
            b.IsEnabled = !busy && (b == launch || b == open || b == options || !isRunning) && (b != remove || !running2.Contains(c.Name));
            buttons.Children.Add(b);
        }
        s.Children.Add(buttons);
        if (SecondAccount.Supported(c)) s.Children.Add(SecondBox(c));
        if (c.HasRevamp) s.Children.Add(BugBox(c));

        var card = new Border { Child = s, Width = 384, Padding = new Thickness(18, 16), Margin = new Thickness(0, 0, 14, 14), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        Res(card, Border.BackgroundProperty, "Card"); Res(card, Border.BorderBrushProperty, isRunning ? "Ok" : "CardLine");
        return card;
    }

    /// <summary>The same copy on a second Steam account, in a Sandboxie box (SecondAccount): start/stop and its monitor.</summary>
    Control SecondBox(CopyInfo c)
    {
        bool on = running2.Contains(c.Name);
        var s = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        var head = Muted("Second Steam account" + (on ? "  ● running" : ""), 12);
        head.FontWeight = FontWeight.SemiBold;
        if (on) Res(head, TextBlock.ForegroundProperty, "Ok");
        s.Children.Add(head);
        if (sandboxie == null)
        {
            s.Children.Add(Muted("Install Sandboxie-Plus (sandboxie-plus.com) to run this copy a second time, on another Steam account.", 12));
            return s;
        }
        var row = new WrapPanel();
        var launch = Btn(on ? "Stop" : "Launch on 2nd account", accent: !on);
        ToolTip.SetTip(launch, on ? "closes the second account's game at once (it doesn't save)"
            : $"mirrors this copy into its account2 folder and starts it in Sandboxie box {SecondAccount.Box} with that box's Steam (the first time, sign in there with the second account)");
        launch.IsEnabled = !busy;
        launch.Click += async (_, _) =>
        {
            if (on) await Job($"Stopping {c.Name} (2nd account)", log => { SecondAccount.Stop(c, log); return Task.CompletedTask; });
            else await Job($"Launching {c.Name} on the 2nd account", log => { SecondAccount.Launch(store, c, log, c.SecondMonitor); return Task.CompletedTask; });
        };
        row.Children.Add(launch);
        var monitor = new ComboBox { FontSize = 12, Margin = new Thickness(0, 0, 7, 6), IsEnabled = !busy && !on, VerticalAlignment = VerticalAlignment.Center,
                                     ItemsSource = new[] { "Game's own screen", "Monitor 1", "Monitor 2", "Monitor 3", "Monitor 4" } };
        monitor.SelectedIndex = Math.Clamp(c.SecondMonitor, 0, 4);
        ToolTip.SetTip(monitor, "where the second game opens (borderless fullscreen)");
        monitor.SelectionChanged += (_, _) => { if (monitor.SelectedIndex >= 0 && monitor.SelectedIndex != c.SecondMonitor) { c.SecondMonitor = monitor.SelectedIndex; c.Save(); } };
        row.Children.Add(monitor);
        s.Children.Add(row);
        return s;
    }

    /// <summary>A bug in this copy's RevaMP version: typed here, sent as an issue on the bug board (BugReports).</summary>
    Control BugBox(CopyInfo c)
    {
        var s = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        var head = Muted("Report a bug in " + c.RevampVersion, 12);
        head.FontWeight = FontWeight.SemiBold;
        s.Children.Add(head);
        var box = new TextBox
        {
            Watermark = "What's broken? The first line becomes the title.", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 64, MaxHeight = 180, FontSize = 13, Text = bugDrafts.GetValueOrDefault(c.Name, ""),
        };
        var send = Btn("Review bug report…", accent: true);
        ToolTip.SetTip(send, "choose files to keep privately, then send the description and build info to GitHub (Ctrl+Enter)");
        void Changed()
        {
            bugDrafts[c.Name] = box.Text ?? "";
            send.IsEnabled = !busy && !string.IsNullOrWhiteSpace(box.Text);
        }
        Changed();
        box.TextChanged += (_, _) => Changed();
        box.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && send.IsEnabled)
            { e.Handled = true; await SendBug(c, box.Text!); }
        };
        send.Click += async (_, _) => await SendBug(c, box.Text ?? "");
        s.Children.Add(box);
        s.Children.Add(Muted(BugReportFiles.DescriptionNotice, 12));
        s.Children.Add(send);
        return s;
    }

    // ---- small builders

    static void Res(AvaloniaObject o, AvaloniaProperty p, string key)
    {
        if (o is IResourceHost h) o.Bind(p, h.GetResourceObservable(key));
    }

    static TextBlock Muted(string text, double size)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        Res(t, TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    static TextBlock Label(string text)
    {
        var t = Muted(text.ToUpperInvariant(), 11);
        t.FontWeight = FontWeight.SemiBold; t.LetterSpacing = 0.6; t.Margin = new Thickness(0, 6, 0, -4);
        return t;
    }

    static Border Pill(string text, string fg, string bg)
    {
        var t = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold };
        Res(t, TextBlock.ForegroundProperty, fg);
        var b = new Border { Child = t, CornerRadius = new CornerRadius(20), Padding = new Thickness(9, 2) };
        Res(b, Border.BackgroundProperty, bg);
        return b;
    }

    static Button Btn(string text, bool accent = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 7, 6), Padding = new Thickness(11, 5), FontSize = 13 };
        if (accent) b.Classes.Add("accent");
        return b;
    }

    // ---- state

    async Task Start()
    {
        Reload();
        if (Env.Windows && !TLDInstalls.Launcher.AnyGame())   // a swap left from a run this window didn't see end
            await Job("Checking the save folders", log => { Saves.ForThisMachine(store).Recover(log); return Task.CompletedTask; }, quiet: true);
        _ = LoadReleases();
        _ = Task.Run(Estimate);
        KindChanged();
    }

    async Task RefreshSource()
    {
        if (busy || fetchingSource || DateTime.UtcNow - sourceChecked < TimeSpan.FromMinutes(2)) return;
        fetchingSource = true;
        sourceChecked = DateTime.UtcNow;
        try
        {
            foreach (var c in store.List().Where(c => c.Kind == Kind.Source))
            {
                try { newer[c.Name] = await Task.Run(() => new SourceBuild(_ => { }).Newer(c)); }
                catch (UserError e) { Log("Source status: " + e.Message); }
            }
            Reload(force: true);
        }
        finally { fetchingSource = false; }
    }

    async Task LoadReleases()
    {
        try
        {
            var list = await Releases.List(log: Log);
            var cur = Releases.Current(list);
            Dispatcher.UIThread.Post(() =>
            {
                releases = list;
                releaseBox.ItemsSource = list.Select(r => r.Label + (r == cur && !r.Draft ? "  (what players install)" : "")).ToList();
                releaseBox.SelectedIndex = list.IndexOf(cur);
                releaseBox.PlaceholderText = null;
                KindChanged();
                Reload(force: true);
            });
        }
        catch (Exception e) when (e is UserError or HttpRequestException)
        {
            Dispatcher.UIThread.Post(() => { releaseBox.PlaceholderText = "no release list (offline?)"; Log("Release list: " + e.Message); });
        }
    }

    void Estimate()
    {
        string text;
        try
        {
            var (bytes, reflink) = new Builder(store, _ => { }).Estimate();
            text = reflink
                ? $"Game files: {Files.Human(bytes)}, shared with Steam's copy on this disk (reflink): a new copy takes only a few MB until Steam updates the game."
                : $"A full copy: about {Files.Human(bytes)} of game files" + (Env.Windows ? "." : ", plus its Proton prefix.");
        }
        catch (Exception e) when (e is UserError or IOException or UnauthorizedAccessException) { text = e.Message; }
        Dispatcher.UIThread.Post(() => estimateText.Text = text);
    }

    string signature = "";

    void Reload(bool force = false)
    {
        copies = store.List();
        running = copies.Where(TLDInstalls.Launcher.IsRunning).Select(c => c.Name).ToHashSet();
        running2 = copies.Where(c => SecondAccount.Supported(c) && SecondAccount.IsRunning(c)).Select(c => c.Name).ToHashSet();
        steam ??= Steam.FindGame();
        (steamBuild, steamBranch) = steam != null ? Steam.BuildInfo(steam) : default;
        sourceText.Text = steam == null ? "Steam's game: not found"
            : $"Steam has the {steamBranch ?? "unknown"} branch, build {steamBuild ?? "?"}: new copies are copies of it";
        rootText.Text = store.Root;
        steamText.Text = steam == null ? "Steam's game: not found"
            : $"Steam's game: {steamBranch ?? "unknown"} branch, build {steamBuild ?? "?"} · Steam {(Steam.ClientRunning() ? "running" : "not running (start it to launch a copy)")}";
        var sig = string.Join("|", copies.Select(c => $"{c.Name}:{c.Kind}:{c.GameBranch}:{c.GameBuildId}:{c.Updated}:{c.LastLaunched}:{c.SizeBytes}:{running.Contains(c.Name)}:{running2.Contains(c.Name)}:{c.LaunchOptions}:{(c.HasRevamp ? c.InstalledSourceLabel : "")}")) + $"|{busy}|{steamBranch}|{steamBuild}|{releases.Count}";
        if (!force && sig == signature) return;
        signature = sig;
        cards.Children.Clear();
        foreach (var c in copies) cards.Children.Add(Card(c));
        emptyText.IsVisible = copies.Count == 0;
    }

    void KindChanged()
    {
        releaseBox.IsEnabled = rbRelease.IsChecked == true;
        refBox.IsEnabled = rbSource.IsChecked == true;
        // a name to start from, while the box holds one of these suggestions (or nothing)
        var suggestions = new[] { "clean", "vanilla", "release", "dev", "test", "source" }.Concat(releases.Select(r => "release-" + r.Version)).ToHashSet();
        if (nameBox.Text is { Length: > 0 } now && !suggestions.Any(x => now == x || now.StartsWith(x + "-"))) return;
        string baseName = rbSource.IsChecked == true ? "source" : rbClean.IsChecked == true ? "clean" : rbVanilla.IsChecked == true ? "vanilla" : rbDev.IsChecked == true ? "dev" : rbTest.IsChecked == true ? "test"
                        : releaseBox.SelectedIndex >= 0 && releaseBox.SelectedIndex < releases.Count ? "release-" + releases[releaseBox.SelectedIndex].Version : "release";
        var name = baseName;
        for (int i = 2; store.Get(name) != null; i++) name = $"{baseName}-{i}";
        nameBox.Text = name;
    }

    void Log(string line)
    {
        void Add()
        {
            logBox.Text = (logBox.Text is { Length: > 0 } t ? t + "\n" : "") + line;
            logBox.CaretIndex = logBox.Text.Length;
            if (line.TrimStart().StartsWith('✗')) ShowLog(true);
        }
        if (Dispatcher.UIThread.CheckAccess()) Add(); else Dispatcher.UIThread.Post(Add);
    }

    internal void ShowLog(bool show)
    {
        logScroll.IsVisible = show;
        logButton.Content = show ? "Hide log" : "Show log";
        if (show) logBox.CaretIndex = logBox.Text?.Length ?? 0;
    }

    /// <summary>One job at a time: its steps go to the log, a failure is shown as it is.</summary>
    async Task Job(string title, Func<Action<string>, Task> work, bool quiet = false)
    {
        if (busy) return;
        busy = true; Reload(force: true);
        makeButton.IsEnabled = false;
        statusText.Text = title + "…";
        progress.IsVisible = true;
        if (!quiet) Log($"── {title}");
        try
        {
            await Task.Run(() => work(Log));
            statusText.Text = title + ": done";
        }
        catch (UserError e) when (e.Message.Trim().Length > 0) { Log("✗ " + e.Message); statusText.Text = title + ": failed"; }
        catch (Exception e)
        {
            // Every failure says what it was: an exception with a blank message showed only "✗" before.
            Log("✗ " + Failure.Describe(e));
            Log("  (full details: " + Failure.Record(title, e) + ")");
            statusText.Text = title + ": failed";
        }
        finally
        {
            busy = false; progress.IsVisible = false; makeButton.IsEnabled = true;
            newer.Clear(); sourceChecked = DateTime.MinValue;
            Reload(force: true);
        }
    }

    // ---- actions

    async Task MakeClicked()
    {
        var name = nameBox.Text?.Trim() ?? "";
        Kind kind = rbSource.IsChecked == true ? Kind.Source : rbClean.IsChecked == true ? Kind.Clean : rbVanilla.IsChecked == true ? Kind.Vanilla : rbDev.IsChecked == true ? Kind.Dev : rbTest.IsChecked == true ? Kind.Test : Kind.Release;
        string? version = kind == Kind.Release && releaseBox.SelectedIndex >= 0 && releaseBox.SelectedIndex < releases.Count ? releases[releaseBox.SelectedIndex].Version : null;
        if (kind == Kind.Release && version == null && releases.Count > 0) { Log("✗ choose a release"); return; }
        var reference = kind == Kind.Source ? refBox.Text?.Trim() : null;
        await Job($"Making {name}", log => new Builder(store, log).Make(name, kind, version, reference));
        nameBox.Text = ""; KindChanged();
    }

    /// <summary>For --render: make a vanilla copy through the panel, as a click would.</summary>
    internal Task MakeForRender(string name)
    {
        rbVanilla.IsChecked = true; nameBox.Text = name;
        return MakeClicked();
    }

    Task Update(CopyInfo c, string? version) => Job($"Updating {c.Name}", log => new Builder(store, log).Update(c.Name, version));

    async Task Launch(CopyInfo c)
    {
        await Job($"Launching {c.Name}", log =>
        {
            TLDInstalls.Launcher.Launch(store, c, log, onExit: () => Dispatcher.UIThread.Post(() =>
            {
                Log($"'{c.Name}' closed");
                Reload(force: true);
            }));
            return Task.CompletedTask;
        });
    }

    /// <summary>The copy's launch options, like Steam's Properties → Launch options: one line, checked as it is typed.</summary>
    internal Window OptionsDialog(CopyInfo c, Action<string?> saved)
    {
        var dlg = new Window { Title = $"Launch options: {c.Name}", Width = 620, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Res(dlg, BackgroundProperty, "Card");
        var s = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        s.Children.Add(new TextBlock { Text = $"Launch options for '{c.Name}'", FontSize = 17, FontWeight = FontWeight.SemiBold });
        s.Children.Add(Muted("Written like Steam's. NAME=VALUE at the start sets a variable. " +
            (Env.Windows ? "Anything else goes to the game as arguments. (Wrappers around %command% work on Linux only.)"
                         : "Words before %command% wrap the game, words after it are the game's arguments; without %command% the whole line is arguments."), 13));
        var box = new TextBox { Text = c.LaunchOptions ?? "", Watermark = Env.Windows ? "-screen-fullscreen 0 -screen-width 1280" : "PROTON_LOG=1 gamemoderun %command% -screen-fullscreen 0",
                                FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,Liberation Mono,monospace"), FontSize = 13 };
        s.Children.Add(box);
        var examples = Muted(Env.Windows
            ? "Examples: -screen-fullscreen 0 · -screen-width 1920 -screen-height 1080 · -popupwindow · -force-d3d11"
            : "Examples: gamemoderun %command% · mangohud %command% · PROTON_LOG=1 %command% · WINEDLLOVERRIDES=\"dxgi=n,b\" %command% · -screen-fullscreen 0", 12);
        s.Children.Add(examples);
        var preview = Muted("", 12);
        var problem = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Res(problem, TextBlock.ForegroundProperty, "Warn");
        s.Children.Add(preview); s.Children.Add(problem);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Btn("Cancel"); cancel.Click += (_, _) => dlg.Close();
        var save = Btn("Save", accent: true);
        void Check()
        {
            try
            {
                var o = TLDInstalls.LaunchOptions.Parse(box.Text);
                if (Env.Windows && o.Wrapper.Count > 0) throw new UserError($"a wrapper around %command% ({o.Wrapper[0]}) works on Linux only");
                preview.Text = o.IsEmpty ? "No launch options: the game starts as it is." : "Starts with " + (c.HasRevamp && !Env.Windows && o.Variables.Any(kv => kv.Key == "WINEDLLOVERRIDES")
                    ? o.Describe() + " (winhttp=n,b is kept for BepInEx)" : o.Describe()) + (running.Contains(c.Name) ? ". Used from the next launch." : "");
                problem.Text = "";
                save.IsEnabled = true;
            }
            catch (UserError e)
            {
                problem.Text = e.Message.Replace("launch options: ", "");
                save.IsEnabled = false;
            }
            preview.IsVisible = save.IsEnabled; problem.IsVisible = !save.IsEnabled;
        }
        void Save()
        {
            if (!save.IsEnabled) return;
            saved(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
            dlg.Close();
        }
        Check();
        box.TextChanged += (_, _) => Check();
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; Save(); } };
        save.Click += (_, _) => Save();
        row.Children.Add(cancel); row.Children.Add(save);
        s.Children.Add(row);
        dlg.Content = s;
        dlg.Opened += (_, _) => { box.Focus(); box.CaretIndex = box.Text?.Length ?? 0; };
        return dlg;
    }

    /// <summary>For --render --options NAME: the copy's launch options dialog, shown without waiting for it.</summary>
    internal Window OptionsForRender(string name, string? text)
    {
        var c = store.Need(name);
        if (text != null) c.LaunchOptions = text;
        var dlg = OptionsDialog(c, _ => { });
        dlg.Show(this);
        return dlg;
    }

    async Task EditOptions(CopyInfo c)
    {
        await OptionsDialog(c, line =>
        {
            var now = store.Get(c.Name) ?? c;   // the copy as it is on disk now (a job may have saved it meanwhile)
            now.LaunchOptions = line;
            now.Save();
            Log(line == null ? $"'{c.Name}': launch options cleared" : $"'{c.Name}': launch options {line}");
            Reload(force: true);
        }).ShowDialog(this);
    }

    async Task Remove(CopyInfo c)
    {
        var what = $"This deletes {c.Dir}: its game files" + (Env.Windows ? " and its saves" : c.Compat != null && c.Compat.StartsWith(c.Dir) ? ", its Proton prefix and the saves in it" : "") +
                   (c.Compat != null && !c.Compat.StartsWith(c.Dir) ? $". The tests' prefix ({c.Compat}) stays." : ".");
        if (!await Confirm($"Remove '{c.Name}'?", what, "Remove")) return;
        await Job($"Removing {c.Name}", log => { new Builder(store, log).Remove(c.Name); return Task.CompletedTask; });
    }

    async Task SendBug(CopyInfo c, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var messages = new List<string>();
        var files = await Task.Run(() => BugReportFiles.Discover(c, messages.Add));
        var dlg = new Window { Title = "Report a bug", Width = 620, Height = 650, MinWidth = 420, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Res(dlg, BackgroundProperty, "Card");
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(22) };
        var intro = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 12) };
        intro.Children.Add(new TextBlock { Text = "Choose files for this bug (local zip, up to 100 MB)", FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        intro.Children.Add(Muted(BugReportFiles.Notice, 13));
        intro.Children.Add(Muted(BugReportFiles.Contents, 13));
        intro.Children.Add(Muted(BugReportFiles.DescriptionNotice, 13));
        layout.Children.Add(intro);
        var list = new StackPanel { Spacing = 6 };
        var ticks = new List<(ReportFile File, CheckBox Tick)>();
        foreach (var file in files)
        {
            var tick = new CheckBox { Content = Muted(file.Label, 13), IsChecked = file.Selected, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ticks.Add((file, tick)); list.Children.Add(tick);
        }
        if (files.Count == 0) list.Children.Add(Muted("No marks, report zips or play logs found. You can still send the bug description.", 13));
        foreach (var message in messages) list.Children.Add(Muted(message, 12));
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = Btn("Cancel"); cancel.Click += (_, _) => dlg.Close(false); row.Children.Add(cancel);
        var send = Btn("Keep files privately and send issue", accent: true); send.Click += (_, _) => dlg.Close(true); row.Children.Add(send);
        Grid.SetRow(row, 2); layout.Children.Add(row); dlg.Content = layout;
        if (!await dlg.ShowDialog<bool>(this)) return;
        var selected = ticks.Where(t => t.Tick.IsChecked == true).Select(t => t.File).ToList();
        string? url = null;
        await Job($"Sending a bug report for {c.RevampVersion}", async log =>
        {
            var telemetry = BugReportFiles.Pack(c, text, selected, log);
            if (telemetry != null) Dispatcher.UIThread.Post(() => OpenFolder(Path.GetDirectoryName(telemetry.Path)!));
            url = await BugReports.Send(c, text, log, telemetry);
        });
        if (url != null) { bugDrafts.Remove(c.Name); Reload(force: true); statusText.Text = "Bug report sent: " + url; }
    }

    void OpenFolder(string dir)
    {
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log("✗ couldn't open " + dir + ": " + e.Message); }
    }

    async Task ChangeRoot()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "The folder for the copies", AllowMultiple = false });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        Env.SaveRoot(path);
        store = new Store(path);
        Log($"Copies folder: {path}");
        _ = Task.Run(Estimate);
        Reload(force: true);
    }

    async Task<bool> Confirm(string title, string text, string? yes)
    {
        var dlg = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Res(dlg, BackgroundProperty, "Card");
        var s = new StackPanel { Margin = new Thickness(22), Spacing = 14 };
        s.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold });
        s.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Btn(yes == null ? "OK" : "Cancel");
        cancel.Click += (_, _) => dlg.Close(false);
        row.Children.Add(cancel);
        if (yes != null)
        {
            var ok = Btn(yes, accent: true);
            ok.Click += (_, _) => dlg.Close(true);
            row.Children.Add(ok);
        }
        s.Children.Add(row);
        dlg.Content = s;
        return await dlg.ShowDialog<bool>(this);
    }
}

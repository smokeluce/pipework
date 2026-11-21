using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace Pipework.Views;

public class VoiceInfo
{
    public string Key = "";
    public string LangName = "";
    public string Quality = "";
    public long Size;
    public List<string> Files = new();
    public string Search => (Key + " " + LangName).ToLowerInvariant();
    public string OnnxName => Files.Where(f => f.EndsWith(".onnx")).Select(Path.GetFileName).FirstOrDefault() ?? "";
}

class PauseRule
{
    public string Find { get; set; } = "";
    public string Pause { get; set; } = "comma";
}

class PipeworkSettings
{
    public string? Voice { get; set; }
    public double Speed { get; set; } = 1.0;
    public string? Filter { get; set; }
    public bool PaneHidden { get; set; }
    public string? DashMode { get; set; }
    public List<PauseRule>? Rules { get; set; }
}

public class MainWindow : Window
{
    const string AppVersion = "0.1";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string Root = Path.Combine(Home, "Library/Application Support/Pipework");
    static readonly string VoiceDir = Path.Combine(Home, ".local/share/piper-voices");
    // TODO(settings): samples cache screen with size display and a clear button
    static readonly string SampleDir = Path.Combine(Root, "samples");
    static readonly string SettingsFile = Path.Combine(Root, "settings.json");
    static string dashMode = "comma";
    static List<PauseRule> pauseRules = new();
    const string VoicesBase = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";
    const string SampleText = "Hello, this is a sample of this voice. The quick brown fox jumps over the lazy dog.";

    List<VoiceInfo> all = new();
    List<VoiceInfo> shown = new();
    readonly TextBox search = new() { Text = "en_US" };
    readonly TextBox input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 15, Padding = new Thickness(12), CornerRadius = new CornerRadius(10) };
    readonly ListBox list = new() { Background = Brushes.Transparent };
    readonly TextBlock count = new() { Foreground = Brush.Parse("#8B93A1"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock status = new() { Foreground = Brush.Parse("#8B93A1"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly Slider speed = new() { Minimum = 0.5, Maximum = 2.0, Value = 1.0, Width = 170, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock speedLabel = new() { Text = "Speed 1.00x", Foreground = Brush.Parse("#8B93A1"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6), Width = 92 };
    readonly Button pauseBtn;
    Process? piperProc, player;
    string? piperPath, lastRaw, lastLabel;
    bool busy, stopRequested, paused;
    string? doneNote, wantVoice, lastVoice;
    TimeSpan? lastSynthTime;
    bool loadingSettings, paneHidden;
    Grid? rootGrid;
    Border? leftCard, rightCard;
    Button? paneBtn;

    double segOffset, segPad, segRate, resumePos, totalSecs;
    Stopwatch? segClock;
    bool pauseRequested;
    TaskCompletionSource<bool>? resumeTcs;

    VoiceInfo? Selected => list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count ? shown[list.SelectedIndex] : null;
    double Rate => Math.Round(speed.Value * 20) / 20;

    public MainWindow()
    {
        Title = "Pipework";
        Width = 1060; Height = 720; MinWidth = 900; MinHeight = 560;
        Background = Brush.Parse("#121418");
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Pipework/Assets/pipework-icon.png"))); } catch { }
        if (Application.Current != null) Application.Current.RequestedThemeVariant = ThemeVariant.Dark;

        var dl = Btn("Download voice", "#2D6CDF");
        var sample = Btn("Sample", "#2A2F3A");
        var speak = Btn("Speak", "#2D6CDF");
        pauseBtn = Btn("Pause", "#2A2F3A");
        var stop = Btn("Stop", "#3A2A2E");
        var replay = Btn("↻ Replay", "#2A2F3A");
        var saveWav = Btn("Save WAV…", "#2A2F3A");
        var saveTxt = Btn("Save text…", "#2A2F3A");

        dl.Click += async (_, _) => await Run(async () => { var v = Selected; if (v == null) { SetStatus("Pick a voice first"); return; } await DownloadVoice(v); SetStatus("Downloaded " + v.Key); });
        sample.Click += async (_, _) => await Run(async () => { var v = Selected; if (v == null) { SetStatus("Pick a voice first"); return; } await SpeakAndPlay(SampleText, v, true); });
        speak.Click += async (_, _) => await Run(async () =>
        {
            var v = Selected;
            if (v == null) { SetStatus("Pick a voice first"); return; }
            if (string.IsNullOrWhiteSpace(input.Text)) { SetStatus("Paste some text first"); return; }
            await SpeakAndPlay(input.Text!, v, false);
        });
        pauseBtn.Click += (_, _) => TogglePause();
        stop.Click += (_, _) => { stopRequested = true; Kill(piperProc); Kill(player); resumeTcs?.TrySetResult(false); };
        replay.Click += async (_, _) => await Run(async () =>
        {
            if (lastRaw == null || !File.Exists(lastRaw)) { SetStatus("Nothing to replay yet"); return; }
            stopRequested = false;
            doneNote = "Replayed  ·  " + ClipInfo(lastRaw);
            await Play(lastRaw, lastLabel ?? "");
        });
        saveWav.Click += async (_, _) => await Guard(SaveWav);
        saveTxt.Click += async (_, _) => await Guard(SaveText);

        search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) { RefreshList(); SaveSettings(); } };
        speed.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { speedLabel.Text = "Speed " + Rate.ToString("0.00", CultureInfo.InvariantCulture) + "x"; SaveSettings(); } };
        list.SelectionChanged += (_, _) => { if (Selected != null && Selected.Key != lastVoice) { lastVoice = Selected.Key; SaveSettings(); } };
        speedLabel.PointerPressed += (_, _) => speed.Value = 1.0;

        var left = new DockPanel();
        var head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "Voices", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
        head.Children.Add(new TextBlock { Text = "Filter by language, name or quality", Foreground = Brush.Parse("#8B93A1"), FontSize = 12 });
        head.Children.Add(search);
        head.Children.Add(count);
        DockPanel.SetDock(head, Dock.Top);
        var lb = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        lb.Children.Add(dl); lb.Children.Add(sample);
        DockPanel.SetDock(lb, Dock.Bottom);
        left.Children.Add(head); left.Children.Add(lb); left.Children.Add(list);

        var right = new DockPanel();
        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var pb = new Button { Content = "‹ Voices", Background = Brush.Parse("#2A2F3A"), Foreground = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4), FontSize = 12, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
        pb.Click += (_, _) => { SetPane(!paneHidden); SaveSettings(); };
        paneBtn = pb;
        var sb = new Button { Content = "Settings", Background = Brush.Parse("#2A2F3A"), Foreground = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4), FontSize = 12, Margin = new Thickness(8, 0, 0, 10) };
        sb.Click += async (_, _) => await Guard(ShowSettings);
        var topRow = new StackPanel { Orientation = Orientation.Horizontal };
        topRow.Children.Add(pb); topRow.Children.Add(sb);
        title.Children.Add(topRow);
        title.Children.Add(new TextBlock { Text = "Pipework", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        title.Children.Add(new TextBlock { Text = "Paste text, pick a voice, hit Speak", Foreground = Brush.Parse("#8B93A1"), FontSize = 12 });
        title.Children.Add(new TextBlock { Text = "v" + AppVersion + "  ·  " + BuildInfo.Text, Foreground = Brush.Parse("#5C6370"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        DockPanel.SetDock(title, Dock.Top);

        var transport = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        transport.Children.Add(speak); transport.Children.Add(pauseBtn); transport.Children.Add(stop); transport.Children.Add(replay);
        var tools = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        tools.Children.Add(speedLabel); tools.Children.Add(speed);
        var spacer = new Border { Width = 16, Height = 1 };
        tools.Children.Add(spacer); tools.Children.Add(saveWav); tools.Children.Add(saveTxt);
        var bottom = new StackPanel();
        bottom.Children.Add(transport); bottom.Children.Add(tools); bottom.Children.Add(status);
        DockPanel.SetDock(bottom, Dock.Bottom);
        right.Children.Add(title); right.Children.Add(bottom); right.Children.Add(input);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*"), Margin = new Thickness(16) };
        var lc = Card(left); var rc = Card(right);
        rc.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(rc, 1);
        root.Children.Add(lc); root.Children.Add(rc);
        Content = root;
        rootGrid = root; leftCard = lc; rightCard = rc;
        LoadSettings();

        Opened += async (_, _) => { SetDockIcon(); await Run(LoadVoices); };
    }

    static Border Card(Control child) => new() { Background = Brush.Parse("#1B1E25"), CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Child = child };

    static Button Btn(string text, string bg) => new()
    {
        Content = text, Background = Brush.Parse(bg), Foreground = Brushes.White,
        CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 9),
        FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 8, 6)
    };

    void SetStatus(string s) => status.Text = s;

    async Task Run(Func<Task> work)
    {
        if (busy) return;
        busy = true;
        try { await work(); }
        catch (Exception ex) { SetStatus(stopRequested ? "Stopped" : "Error: " + ex.Message); }
        finally { busy = false; RefreshList(); }
    }

    async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { SetStatus("Error: " + ex.Message); }
    }

    static void Kill(Process? p) { try { if (p != null && !p.HasExited) p.Kill(); } catch { } }
    static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    static bool Installed(VoiceInfo v) => v.OnnxName != "" && File.Exists(Path.Combine(VoiceDir, v.OnnxName)) && File.Exists(Path.Combine(VoiceDir, v.OnnxName + ".json"));

    void RefreshList()
    {
        var terms = (search.Text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sel = Selected?.Key ?? wantVoice;
        shown = all.Where(v => terms.All(t => v.Search.Contains(t))).ToList();
        list.ItemsSource = shown.Select(v => (Installed(v) ? "●  " : "↓  ") + v.Key + "   " + (v.Size / 1048576) + " MB").ToList();
        list.SelectedIndex = sel == null ? -1 : shown.FindIndex(v => v.Key == sel);
        if (all.Count > 0) wantVoice = null;
        count.Text = $"{shown.Count} shown  ·  {all.Count(Installed)} installed  ·  ● = installed  ·  ↓ = download";
    }

    async Task LoadVoices()
    {
        Directory.CreateDirectory(Root);
        var cache = Path.Combine(Root, "voices.json");
        SetStatus("Loading voice list…");
        try { File.WriteAllText(cache, await Http.GetStringAsync(VoicesBase + "voices.json")); }
        catch { if (!File.Exists(cache)) throw; }
        using var doc = JsonDocument.Parse(File.ReadAllText(cache));
        all = new List<VoiceInfo>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var e = p.Value;
            var lang = e.GetProperty("language");
            var v = new VoiceInfo
            {
                Key = p.Name,
                Quality = e.GetProperty("quality").GetString() ?? "",
                LangName = $"{lang.GetProperty("name_english").GetString()} {lang.GetProperty("country_english").GetString()}"
            };
            foreach (var f in e.GetProperty("files").EnumerateObject())
            {
                v.Files.Add(f.Name);
                if (f.Name.EndsWith(".onnx")) v.Size = f.Value.GetProperty("size_bytes").GetInt64();
            }
            all.Add(v);
        }
        all = all.OrderBy(v => v.Key).ToList();
        SetStatus("Ready");
    }

    async Task DownloadFile(string url, string dest, string label)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".part";
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var s = await resp.Content.ReadAsStreamAsync();
        await using (var f = File.Create(tmp))
        {
            var buf = new byte[81920]; long read = 0; int n; var last = DateTime.MinValue;
            while ((n = await s.ReadAsync(buf)) > 0)
            {
                await f.WriteAsync(buf.AsMemory(0, n));
                read += n;
                if (total > 0 && (DateTime.Now - last).TotalMilliseconds > 150) { last = DateTime.Now; SetStatus($"{label} {read * 100 / total}%"); }
            }
        }
        File.Move(tmp, dest, true);
    }

    async Task DownloadVoice(VoiceInfo v)
    {
        foreach (var f in v.Files.Where(f => f.EndsWith(".onnx") || f.EndsWith(".onnx.json")))
            await DownloadFile(VoicesBase + f, Path.Combine(VoiceDir, Path.GetFileName(f)), "Downloading " + v.Key);
    }

    // Finds the pip-installed piper at runtime: env override, then the login shell's PATH, then common spots.
    static string? FindPiper()
    {
        var env = Environment.GetEnvironmentVariable("PIPEWORK_PIPER");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        try
        {
            var psi = new ProcessStartInfo("/bin/zsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-lic"); psi.ArgumentList.Add("command -v piper");
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (p.WaitForExit(8000))
            {
                var hit = outTask.Result.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.EndsWith("piper") && File.Exists(l));
                if (hit != null) return hit;
            }
            else Kill(p);
        }
        catch { }
        var cands = new List<string> { "/opt/homebrew/bin/piper", "/usr/local/bin/piper", Path.Combine(Home, ".local/bin/piper") };
        var pyRoot = Path.Combine(Home, "Library/Python");
        if (Directory.Exists(pyRoot)) cands.AddRange(Directory.GetDirectories(pyRoot).Select(d => Path.Combine(d, "bin/piper")));
        return cands.FirstOrDefault(File.Exists);
    }

    async Task<string?> Synthesize(string text, VoiceInfo v, bool cache)
    {
        stopRequested = false;
        lastSynthTime = null;
        var raw = cache ? Path.Combine(SampleDir, v.Key + ".wav") : Path.Combine(Path.GetTempPath(), "pipework-" + DateTime.Now.ToString("HHmmssfff") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        if (cache && File.Exists(raw)) return raw;

        piperPath ??= await Task.Run(FindPiper);
        if (piperPath == null) throw new Exception("piper not found. Install it with: pip3 install piper-tts");
        if (!Installed(v)) await DownloadVoice(v);
        if (File.Exists(raw)) File.Delete(raw);

        SetStatus("Synthesizing… (0 secs)");
        var sw = Stopwatch.StartNew();
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        ticker.Tick += (_, _) => SetStatus("Synthesizing… (" + (int)sw.Elapsed.TotalSeconds + " secs)");
        ticker.Start();
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Wait);
        try
        {
            var psi = new ProcessStartInfo(piperPath)
            {
                WorkingDirectory = Root, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false
            };
            psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(Path.Combine(VoiceDir, v.OnnxName));
            psi.ArgumentList.Add("--output_file"); psi.ArgumentList.Add(raw);
            using var p = Process.Start(psi)!;
            piperProc = p;
            var errTask = p.StandardError.ReadToEndAsync();
            await p.StandardInput.WriteAsync(PrepText(text));
            p.StandardInput.Close();
            await p.WaitForExitAsync();
            var err = await errTask;
            piperProc = null;
            if (stopRequested) { TryDelete(raw); SetStatus("Stopped"); return null; }
            if (p.ExitCode != 0 || !File.Exists(raw))
            {
                TryDelete(raw);
                throw new Exception(err.Trim().Split('\n').LastOrDefault() ?? "piper failed");
            }
        }
        finally { ticker.Stop(); Cursor = Avalonia.Input.Cursor.Default; }
        lastSynthTime = sw.Elapsed;
        return raw;
    }

    async Task SpeakAndPlay(string text, VoiceInfo v, bool cache)
    {
        var raw = await Synthesize(text, v, cache);
        if (raw == null) return;
        var prev = lastRaw;
        lastRaw = raw; lastLabel = v.Key;
        if (prev != null && prev != raw && prev.StartsWith(Path.GetTempPath())) TryDelete(prev);
        var words = Regex.Matches(text, @"\S+").Count;
        var info = ClipInfo(raw);
        doneNote = lastSynthTime == null
            ? "Sample from cache  ·  " + info
            : "Synthesized " + words + (words == 1 ? " word" : " words") + " in " + lastSynthTime.Value.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s  ·  " + info;
        await Play(raw, v.Key);
    }

    async Task Play(string raw, string label)
    {
        totalSecs = WavSeconds(raw);
        double from = 0;
        pauseRequested = false; paused = false; pauseBtn.Content = "Pause";
        try
        {
            while (true)
            {
                var rate = Rate;
                SetStatus("Speaking with " + label + (Math.Abs(rate - 1.0) > 0.001 ? " at " + rate.ToString("0.00", CultureInfo.InvariantCulture) + "x" : "") + "  ·  " + ClipInfo(raw, rate));
                await PlaySegment(raw, from, rate);
                if (!pauseRequested || stopRequested) break;
                resumeTcs = new TaskCompletionSource<bool>();
                SetStatus("Paused");
                if (!await resumeTcs.Task) break;
                pauseRequested = false; paused = false; pauseBtn.Content = "Pause";
                from = Math.Max(0, resumePos - 0.4);
            }
        }
        finally { player = null; resumeTcs = null; pauseRequested = false; paused = false; pauseBtn.Content = "Pause"; }
        SetStatus(stopRequested ? "Stopped" : (doneNote ?? "Done"));
    }

    async Task PlaySegment(string raw, double from, double rate)
    {
        var play = Path.Combine(Path.GetTempPath(), "pipework-play.wav");
        var pad = from > 0 ? 0.5 : 1.0;
        if (from > 0) TrimWav(raw, play, from); else File.Copy(raw, play, true);
        PadWav(play, pad);
        var psi = new ProcessStartInfo("afplay") { UseShellExecute = false };
        if (Math.Abs(rate - 1.0) > 0.001)
        {
            psi.ArgumentList.Add("-r"); psi.ArgumentList.Add(rate.ToString("0.00", CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-q"); psi.ArgumentList.Add("1");
        }
        psi.ArgumentList.Add(play);
        segOffset = from; segPad = pad; segRate = rate;
        using var pl = Process.Start(psi)!;
        player = pl;
        segClock = Stopwatch.StartNew();
        try { await pl.WaitForExitAsync(); }
        finally { player = null; }
    }


    void TogglePause()
    {
        if (paused)
        {
            paused = false;
            resumeTcs?.TrySetResult(true);
            return;
        }
        var p = player;
        if (p == null || segClock == null) return;
        var pos = segOffset + Math.Max(0, segClock.Elapsed.TotalSeconds * segRate - segPad);
        if (pos >= totalSecs - 0.15) return;
        resumePos = pos;
        paused = true; pauseRequested = true;
        pauseBtn.Content = "Resume";
        Kill(p);
    }

    async Task SaveWav()
    {
        if (lastRaw == null || !File.Exists(lastRaw)) { SetStatus("Speak or sample something first, then save it"); return; }
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save audio", SuggestedFileName = (lastLabel ?? "pipework") + ".wav", DefaultExtension = "wav",
            FileTypeChoices = new[] { new FilePickerFileType("WAV audio") { Patterns = new[] { "*.wav" } } }
        });
        if (f == null) return;
        File.Copy(lastRaw, f.Path.LocalPath, true);
        SetStatus("Saved " + Path.GetFileName(f.Path.LocalPath));
    }

    async Task SaveText()
    {
        var text = input.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) { SetStatus("The text box is empty"); return; }
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save text", SuggestedFileName = "pipework.txt", DefaultExtension = "txt",
            FileTypeChoices = new[] { new FilePickerFileType("Text") { Patterns = new[] { "*.txt" } } }
        });
        if (f == null) return;
        File.WriteAllText(f.Path.LocalPath, text);
        SetStatus("Saved " + Path.GetFileName(f.Path.LocalPath));
    }

    [DllImport("/usr/lib/libobjc.dylib")] static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")] static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg);

    // Sets the Dock icon at runtime so it shows even under `dotnet run` (no .app bundle).
    static void SetDockIcon()
    {
        try
        {
            Directory.CreateDirectory(Root);
            var tmp = Path.Combine(Root, "dock-icon.png");
            using (var src = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Pipework/Assets/pipework-icon.png")))
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            var path = MsgSend(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), Marshal.StringToHGlobalAnsi(tmp));
            var img = MsgSend(MsgSend(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithContentsOfFile:"), path);
            if (img == IntPtr.Zero) return;
            MsgSend(MsgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication")), sel_registerName("setApplicationIconImage:"), img);
        }
        catch { }
    }

    async Task ShowSettings()
    {
        static TextBlock Section(string t) => new() { Text = t, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) };
        static TextBlock Note(string t) => new() { Text = t, FontSize = 12, Foreground = Brush.Parse("#8B93A1"), TextWrapping = TextWrapping.Wrap };

        var cacheInfo = new TextBlock { Foreground = Brush.Parse("#C9CED6"), FontSize = 13 };
        void RefreshCache()
        {
            var files = Directory.Exists(SampleDir) ? Directory.GetFiles(SampleDir, "*.wav") : Array.Empty<string>();
            long bytes = files.Sum(f => new FileInfo(f).Length);
            cacheInfo.Text = files.Length + (files.Length == 1 ? " cached sample  ·  " : " cached samples  ·  ")
                + (bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : (bytes / 1024) + " KB");
        }
        RefreshCache();

        var clear = Btn("Clear samples cache", "#3A2A2E");
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        clear.Click += (_, _) =>
        {
            if (Directory.Exists(SampleDir)) foreach (var f in Directory.GetFiles(SampleDir, "*.wav")) TryDelete(f);
            RefreshCache();
        };

        var modes = new[] { ("comma", "Comma (short pause)"), ("period", "Period (medium pause)"), ("ellipsis", "Ellipsis (long pause)"), ("off", "Off (leave -- as typed)") };
        var combo = new ComboBox { ItemsSource = modes.Select(m => m.Item2).ToList(), MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(modes, m => m.Item1 == dashMode));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { dashMode = modes[combo.SelectedIndex].Item1; SaveSettings(); } };

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Settings", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        panel.Children.Add(Section("Samples cache"));
        panel.Children.Add(Note("Voice previews are kept so repeat samples play instantly."));
        panel.Children.Add(cacheInfo);
        panel.Children.Add(clear);
        panel.Children.Add(Section("Double-dash pause"));
        panel.Children.Add(Note("What -- between words becomes before the text goes to Piper."));
        panel.Children.Add(combo);
        panel.Children.Add(Note("Heads up: pause length differs between voices. Each one learned its own timing, so the longest option can change from voice to voice. Try a few on your favorite voice to find what works."));
        panel.Children.Add(Section("Custom pause rules"));
        panel.Children.Add(Note("Add a symbol or word and pick a pause. Symbols (like / or •) are replaced by the pause. Words keep their text and get a pause after them. Matching ignores case."));
        var ruleList = new ListBox { Height = 130, Background = Brush.Parse("#1B1E25") };
        void RefreshRules() => ruleList.ItemsSource = pauseRules.Select(r => "\"" + r.Find + "\"  →  " + r.Pause).ToList();
        RefreshRules();
        var findBox = new TextBox();
        var pauseBox = new ComboBox { ItemsSource = new[] { "comma", "period", "ellipsis" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var add = Btn("Add", "#2A2F3A");
        add.Click += (_, _) =>
        {
            var f = (findBox.Text ?? "").Trim();
            if (f.Length == 0) return;
            pauseRules.Add(new PauseRule { Find = f, Pause = (string)pauseBox.SelectedItem! });
            findBox.Text = "";
            RefreshRules(); SaveSettings();
        };
        var addCol = new StackPanel { Spacing = 6, Margin = new Thickness(16, 0, 0, 0) };
        addCol.Children.Add(Note("Text to match"));
        addCol.Children.Add(findBox);
        addCol.Children.Add(Note("Pause type"));
        addCol.Children.Add(pauseBox);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        addCol.Children.Add(add);
        var remove = Btn("Remove", "#3A2A2E");
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        remove.Click += (_, _) =>
        {
            var i = ruleList.SelectedIndex;
            if (i >= 0 && i < pauseRules.Count) { pauseRules.RemoveAt(i); RefreshRules(); SaveSettings(); }
        };
        var listCol = new StackPanel { Spacing = 6 };
        listCol.Children.Add(ruleList);
        addCol.Children.Remove(add);
        var btnRow = new WrapPanel();
        btnRow.Children.Add(add); btnRow.Children.Add(remove);
        addCol.Children.Add(btnRow);
        var rulesGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(addCol, 1);
        rulesGrid.Children.Add(listCol);
        rulesGrid.Children.Add(addCol);
        panel.Children.Add(rulesGrid);
        var done = Btn("Done", "#2D6CDF");
        btnRow.Children.Add(done);

        var win = new Window
        {
            Title = "Settings", Width = 680, Height = 610, CanResize = false,
            Background = Brush.Parse("#121418"), WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel }
        };
        done.Click += (_, _) => win.Close();
        await win.ShowDialog(this);
    }

    static string PauseText(string name) => name switch { "period" => ". ", "ellipsis" => "... ", _ => ", " };

    // Symbols are replaced by the pause; words keep their text and get the pause after them.
    static string ApplyRules(string t)
    {
        foreach (var r in pauseRules)
        {
            if (string.IsNullOrEmpty(r.Find)) continue;
            var pause = PauseText(r.Pause);
            var pat = Regex.Escape(r.Find);
            if (r.Find.Any(char.IsLetterOrDigit))
            {
                var pre = char.IsLetterOrDigit(r.Find[0]) ? @"(?<!\w)" : "";
                var post = char.IsLetterOrDigit(r.Find[^1]) ? @"(?!\w)" : "";
                t = Regex.Replace(t, pre + pat + post, m => m.Value + pause, RegexOptions.IgnoreCase);
            }
            else t = Regex.Replace(t, @"\s*" + pat + @"\s*", pause);
        }
        return t;
    }

    static double WavSeconds(string path)
    {
        var b = File.ReadAllBytes(path);
        int pos = 12, ch = 1, rate = 22050, bits = 16, dataLen = 0;
        while (pos + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int len = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ") { ch = BitConverter.ToInt16(b, pos + 10); rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToInt16(b, pos + 22); }
            else if (id == "data") { dataLen = len; if (dataLen <= 0 || pos + 8 + dataLen > b.Length) dataLen = b.Length - (pos + 8); break; }
            pos += 8 + len + (len & 1);
        }
        int bps = rate * ch * (bits / 8);
        return bps > 0 ? (double)dataLen / bps : 0;
    }

    static string ClipInfo(string path, double rate = 1.0)
    {
        try
        {
            var secs = WavSeconds(path) / rate;
            var bytes = new FileInfo(path).Length;
            var size = bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : (bytes / 1024) + " KB";
            return ((int)secs / 60) + ":" + ((int)secs % 60).ToString("00") + "  ·  " + size;
        }
        catch { return "?"; }
    }

    void SetPane(bool hidden)
    {
        paneHidden = hidden;
        if (leftCard == null || rootGrid == null || rightCard == null) return;
        leftCard.IsVisible = !hidden;
        rootGrid.ColumnDefinitions[0].Width = hidden ? new GridLength(0) : new GridLength(360);
        rightCard.Margin = new Thickness(hidden ? 0 : 16, 0, 0, 0);
        if (paneBtn != null) paneBtn.Content = hidden ? "› Voices" : "‹ Voices";
    }

    void LoadSettings()
    {
        loadingSettings = true;
        try
        {
            if (File.Exists(SettingsFile))
            {
                var st = JsonSerializer.Deserialize<PipeworkSettings>(File.ReadAllText(SettingsFile));
                if (st != null)
                {
                    wantVoice = st.Voice; lastVoice = st.Voice;
                    if (st.Filter != null) search.Text = st.Filter;
                    speed.Value = st.Speed;
                    SetPane(st.PaneHidden);
                    dashMode = st.DashMode ?? "comma";
                    pauseRules = st.Rules ?? new List<PauseRule>();
                }
            }
        }
        catch { }
        finally { loadingSettings = false; }
    }

    void SaveSettings()
    {
        if (loadingSettings) return;
        try
        {
            Directory.CreateDirectory(Root);
            var st = new PipeworkSettings { Voice = lastVoice, Speed = Rate, Filter = search.Text, PaneHidden = paneHidden, DashMode = dashMode, Rules = pauseRules };
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(st));
        }
        catch { }
    }

    static string PrepText(string t)
    {
        var r = dashMode switch { "period" => ". ", "ellipsis" => "... ", "off" => null, _ => ", " };
        return ApplyRules(r == null ? t : Regex.Replace(t, @"(?<=\w)\s*-{2,}\s*(?=\w)", r));
    }

    static void TrimWav(string src, string dst, double fromSeconds)
    {
        var b = File.ReadAllBytes(src);
        int pos = 12, ch = 1, rate = 22050, bits = 16, dataPos = -1, dataLen = 0;
        while (pos + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int len = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ") { ch = BitConverter.ToInt16(b, pos + 10); rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToInt16(b, pos + 22); }
            else if (id == "data") { dataPos = pos + 8; dataLen = len; break; }
            pos += 8 + len + (len & 1);
        }
        if (dataPos < 0) { File.Copy(src, dst, true); return; }
        if (dataLen <= 0 || dataPos + dataLen > b.Length) dataLen = b.Length - dataPos;
        int block = Math.Max(1, ch * (bits / 8));
        int skip = Math.Min((int)(rate * Math.Max(0, fromSeconds)) * block, dataLen);
        int newLen = dataLen - skip;
        var o = new byte[dataPos + newLen];
        Array.Copy(b, 0, o, 0, dataPos);
        Array.Copy(b, dataPos + skip, o, dataPos, newLen);
        BitConverter.GetBytes(newLen).CopyTo(o, dataPos - 4);
        BitConverter.GetBytes(o.Length - 8).CopyTo(o, 4);
        File.WriteAllBytes(dst, o);
    }

    static void PadWav(string path, double seconds = 1.0)
    {
        var b = File.ReadAllBytes(path);
        int pos = 12, ch = 1, rate = 22050, bits = 16, dataPos = -1, dataLen = 0;
        while (pos + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int len = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ") { ch = BitConverter.ToInt16(b, pos + 10); rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToInt16(b, pos + 22); }
            else if (id == "data") { dataPos = pos + 8; dataLen = len; break; }
            pos += 8 + len + (len & 1);
        }
        if (dataPos < 0) return;
        if (dataLen <= 0 || dataPos + dataLen > b.Length) dataLen = b.Length - dataPos;
        int pad = (int)(rate * seconds) * ch * (bits / 8);
        var o = new byte[dataPos + pad + dataLen];
        Array.Copy(b, 0, o, 0, dataPos);
        Array.Copy(b, dataPos, o, dataPos + pad, dataLen);
        BitConverter.GetBytes(dataLen + pad).CopyTo(o, dataPos - 4);
        BitConverter.GetBytes(o.Length - 8).CopyTo(o, 4);
        File.WriteAllBytes(path, o);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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

public class MainWindow : Window
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/Pipework");
    static readonly string VoiceDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/piper-voices");
    static readonly string PiperDir = Path.Combine(Root, "piper");
    static readonly string PiperBin = "/Users/paulswonger/Library/Python/3.9/bin/piper";
    const string PiperUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_macos_x64.tar.gz";
    const string VoicesBase = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";
    const string SampleText = "Hello, this is a sample of this voice. The quick brown fox jumps over the lazy dog.";

    List<VoiceInfo> all = new();
    List<VoiceInfo> shown = new();
    readonly TextBox search = new() { Text = "en_US" };
    readonly TextBox input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 15, Padding = new Thickness(12), CornerRadius = new CornerRadius(10) };
    readonly ListBox list = new() { Background = Brushes.Transparent };
    readonly TextBlock count = new() { Foreground = Brush.Parse("#8B93A1"), FontSize = 12 };
    readonly TextBlock status = new() { Foreground = Brush.Parse("#8B93A1"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    Process? piperProc, player;
    bool busy, stopRequested;

    VoiceInfo? Selected => list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count ? shown[list.SelectedIndex] : null;

    public MainWindow()
    {
        Title = "Pipework";
        Width = 1000; Height = 700; MinWidth = 760; MinHeight = 520;
        Background = Brush.Parse("#121418");
        if (Application.Current != null) Application.Current.RequestedThemeVariant = ThemeVariant.Dark;

        var dl = Btn("Download voice", "#2D6CDF");
        var sample = Btn("Sample", "#2A2F3A");
        var speak = Btn("Speak", "#2D6CDF");
        var stop = Btn("Stop", "#3A2A2E");
        dl.Click += async (_, _) => await Run(async () => { var v = Selected; if (v == null) { SetStatus("Pick a voice first"); return; } await DownloadVoice(v); SetStatus("Downloaded " + v.Key); });
        sample.Click += async (_, _) => await Run(async () => { var v = Selected; if (v == null) { SetStatus("Pick a voice first"); return; } await Speak(SampleText, v); });
        speak.Click += async (_, _) => await Run(async () =>
        {
            var v = Selected;
            if (v == null) { SetStatus("Pick a voice first"); return; }
            if (string.IsNullOrWhiteSpace(input.Text)) { SetStatus("Paste some text first"); return; }
            await Speak(input.Text!, v);
        });
        stop.Click += (_, _) => { stopRequested = true; Kill(piperProc); Kill(player); };

        search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) RefreshList(); };

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
        title.Children.Add(new TextBlock { Text = "Pipework", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        title.Children.Add(new TextBlock { Text = "Paste text, pick a voice, hit Speak", Foreground = Brush.Parse("#8B93A1"), FontSize = 12 });
        DockPanel.SetDock(title, Dock.Top);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(speak); bar.Children.Add(stop); bar.Children.Add(status);
        DockPanel.SetDock(bar, Dock.Bottom);
        right.Children.Add(title); right.Children.Add(bar); right.Children.Add(input);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*"), Margin = new Thickness(16) };
        var lc = Card(left); var rc = Card(right);
        rc.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(rc, 1);
        root.Children.Add(lc); root.Children.Add(rc);
        Content = root;

        Opened += async (_, _) => await Run(LoadVoices);
    }

    static Border Card(Control child) => new() { Background = Brush.Parse("#1B1E25"), CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Child = child };

    static Button Btn(string text, string bg) => new()
    {
        Content = text, Background = Brush.Parse(bg), Foreground = Brushes.White,
        CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 9),
        FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 8, 0)
    };

    void SetStatus(string s) => status.Text = s;

    async Task Run(Func<Task> work)
    {
        if (busy) return;
        busy = true;
        try { await work(); }
        catch (Exception ex) { SetStatus(stopRequested ? "Stopped" : "Error: " + ex.Message); }
        finally { busy = false; Cursor = Avalonia.Input.Cursor.Default; RefreshList(); }
    }

    static void Kill(Process? p) { try { if (p != null && !p.HasExited) p.Kill(); } catch { } }

    static bool Installed(VoiceInfo v) => v.OnnxName != "" && File.Exists(Path.Combine(VoiceDir, v.OnnxName)) && File.Exists(Path.Combine(VoiceDir, v.OnnxName + ".json"));

    void RefreshList()
    {
        var terms = (search.Text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sel = Selected?.Key;
        shown = all.Where(v => terms.All(t => v.Search.Contains(t))).ToList();
        list.ItemsSource = shown.Select(v => (Installed(v) ? "●  " : "○  ") + v.Key + "   " + (v.Size / 1048576) + " MB").ToList();
        list.SelectedIndex = sel == null ? -1 : shown.FindIndex(v => v.Key == sel);
        count.Text = $"{shown.Count} voices  ·  ● = installed";
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

    async Task EnsurePiper()
    {
        if (File.Exists(PiperBin)) return;
        Directory.CreateDirectory(Root);
        var tgz = Path.Combine(Root, "piper.tar.gz");
        await DownloadFile(PiperUrl, tgz, "Downloading Piper engine");
        SetStatus("Unpacking Piper…");
        var tar = Process.Start("tar", new[] { "-xzf", tgz, "-C", Root })!;
        await tar.WaitForExitAsync();
        File.Delete(tgz);
        if (!File.Exists(PiperBin)) throw new Exception("Piper unpack failed");
        Process.Start("chmod", new[] { "+x", PiperBin })!.WaitForExit();
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

    async Task Speak(string text, VoiceInfo v)
    {
        stopRequested = false;
        await EnsurePiper();
        if (!Installed(v)) await DownloadVoice(v);
        var wav = Path.Combine(Path.GetTempPath(), "pipework.wav");
        if (File.Exists(wav)) File.Delete(wav);
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Wait);
        SetStatus("Synthesizing…");
        var psi = new ProcessStartInfo(PiperBin)
        {
            WorkingDirectory = Root, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false
        };
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(Path.Combine(VoiceDir, v.OnnxName));
        psi.ArgumentList.Add("--output_file"); psi.ArgumentList.Add(wav);
        using var p = Process.Start(psi)!;
        piperProc = p;
        var errTask = p.StandardError.ReadToEndAsync();
        await p.StandardInput.WriteAsync(text);
        p.StandardInput.Close();
        await p.WaitForExitAsync();
        var err = await errTask;
        Cursor = Avalonia.Input.Cursor.Default;
        if (stopRequested) { SetStatus("Stopped"); return; }
        if (p.ExitCode != 0 || !File.Exists(wav))
            throw new Exception(err.Trim().Split('\n').LastOrDefault() ?? "piper failed");
        PadWav(wav);
        SetStatus("Speaking with " + v.Key);
        var pl = Process.Start("afplay", wav)!;
        player = pl;
        await pl.WaitForExitAsync();
        SetStatus(stopRequested ? "Stopped" : "Done");
    }
}

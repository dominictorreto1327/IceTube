using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IceTube;
using IceTube.Controls;
using IceTube.Models;
using IceTube.Services;

// Runs the real form, playback service, loopback proxy and bundled mpv against
// deterministic generated media. No dependency on YouTube availability.
internal static class EmbeddedPlaybackSmoke
{
    private static int result = 1;
    private static string fixtures;
    private static string report;
    private static string youtubeUrl;
    [STAThread]
    private static int Main(string[] args)
    {
        fixtures = Path.GetFullPath(args[0]);
        youtubeUrl = args.Length > 1 ? args[1] : null;
        report = Path.Combine(fixtures, youtubeUrl == null ? "results.txt" : "youtube-results.txt");
        File.WriteAllText(report, "Embedded playback integration tests\r\n");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (MainForm form = new MainForm())
        {
            form.Shown += async (s, e) =>
            {
                try { await Run(form); result = 0; Log("ALL PASS"); }
                catch (Exception ex) { Log("FAIL: " + ex); }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        return result;
    }

    private static T Field<T>(MainForm form, string name)
    {
        return (T)typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
    }
    private static void Log(string value) { Console.WriteLine(value); File.AppendAllText(report, value + "\r\n"); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition, string message, int timeout = 15000)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeout) throw new Exception(message);
            await Task.Delay(100);
        }
    }
    private static string MpvLog()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "mpv-last.log");
        if (!File.Exists(path)) return "";
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(input)) return reader.ReadToEnd();
    }
    private static async Task Run(MainForm form)
    {
        var surface = Field<VideoSurface>(form, "_videoSurface");
        var player = Field<PlayerService>(form, "_player");
        var play = Field<Button>(form, "_playButton");
        var stop = Field<Button>(form, "_stopButton");
        var status = Field<Label>(form, "_statusValue");
        var url = Field<TextBox>(form, "_urlTextBox");
        IntPtr host = surface.Handle;
        Assert(surface.BackColor == Color.Black && !player.IsPlaying, "Idle state must be black");
        for (int w = 1; w < 2048; w += 13)
            for (int h = 1; h < 1200; h += 31)
            {
                Rectangle box = VideoSurface.FitBounds(new Size(w, h));
                Assert(box.Width * 9 == box.Height * 16 && box.Right <= w && box.Bottom <= h, "16:9 fit");
            }
        Log("PASS idle black / exact 16:9 layout across sizes");
        if (youtubeUrl != null)
        {
            url.Text = youtubeUrl;
            play.PerformClick();
            await Until(() => player.IsPlaying && !MpvLog().Contains("Embedded test:") && MpvLog().Contains("playback restart complete"), "YouTube did not start; inspect IceTube log", 120000);
            Assert(MpvLog().Contains("first video frame") && MpvLog().Contains("audio ready"), "YouTube audio/video missing");
            IntPtr child = GetWindow(host, 5);
            Assert(child != IntPtr.Zero && GetParent(child) == host, "YouTube mpv not embedded");
            Log("PASS YouTube resolved / first frame / audio / embedded HWND");
            await Task.Delay(15000);
            Assert(player.IsPlaying, "YouTube unexpectedly exited");
            File.WriteAllText(Path.Combine(fixtures, "youtube-mpv.log"), MpvLog());
            stop.PerformClick();
            await Until(() => !player.IsPlaying && GetWindow(host, 5) == IntPtr.Zero, "YouTube Stop failed");
            Log("PASS YouTube continued playback / stop / black idle");
            return;
        }
        using (var source = new FixtureSource(fixtures))
        {
            var resolver = new FixtureResolver(source);
            typeof(MainForm).GetField("_resolver", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, resolver);
            foreach (string name in new[] { "wide", "classic", "portrait", "cinema", "small", "separate" })
            {
                resolver.Name = name;
                url.Text = "https://www.youtube.com/watch?v=fixture";
                play.PerformClick();
                await Until(() => player.IsPlaying && MpvLog().Contains(resolver.ExpectedTitle) && MpvLog().Contains("playback restart complete"), name + " first frame/audio missing");
                Assert(MpvLog().Contains("first video frame") && MpvLog().Contains("audio ready"), "Audio/video not ready");
                IntPtr child = GetWindow(host, 5);
                Assert(child != IntPtr.Zero && GetParent(child) == host, "mpv not a child of surface");
                uint pid; GetWindowThreadProcessId(child, out pid);
                EnumWindows((window, parameter) =>
                {
                    uint owner; GetWindowThreadProcessId(window, out owner);
                    Assert(owner != pid || !IsWindowVisible(window), "Unexpected standalone mpv window");
                    return true;
                }, IntPtr.Zero);
                foreach (Size size in new[] { new Size(760, 688), new Size(960, 660), new Size(520, 520) })
                {
                    form.ClientSize = size;
                    await Task.Delay(400);
                    RECT bounds; GetClientRect(child, out bounds);
                    Assert(surface.Width * 9 == surface.Height * 16, "Host ratio changed");
                    Assert(bounds.Right == surface.Width && bounds.Bottom == surface.Height, "mpv child did not fill resized host");
                }
                form.ClientSize = new Size(760, 688);
                await Task.Delay(500);
                File.WriteAllText(Path.Combine(fixtures, name + "-mpv.log"), MpvLog());
                MatchCollection displays = Regex.Matches(MpvLog(), @"Video display: .* -> \((\d+), (\d+)\) (\d+)x(\d+)");
                if (displays.Count > 0)
                {
                    Match display = displays[displays.Count - 1];
                    int x = int.Parse(display.Groups[1].Value), y = int.Parse(display.Groups[2].Value);
                    int width = int.Parse(display.Groups[3].Value), height = int.Parse(display.Groups[4].Value);
                    Assert(x == 0 || y == 0, "Renderer introduced black bars on all four sides");
                    Assert(width == surface.Width || height == surface.Height, "Renderer did not fit viewport");
                    Assert(name != "classic" && name != "portrait" || x > 0 && y == 0, "Expected side bars");
                    Assert(name != "cinema" || x == 0 && y > 0, "Expected top/bottom bars");
                    Log("PASS renderer fit " + name + ": offset=" + x + "," + y + " size=" + width + "x" + height);
                }
                // Save only this test form's rendering, never the desktop.
                using (var bitmap = new Bitmap(form.Width, form.Height))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    IntPtr dc = graphics.GetHdc();
                    try { PrintWindow(form.Handle, dc, 0); }
                    finally { graphics.ReleaseHdc(dc); }
                    bitmap.Save(Path.Combine(fixtures, name + ".png"));
                }
                if (name == "classic")
                {
                    // Replace an active video and ensure its queued exit cannot
                    // reset the UI of the new playback.
                    play.PerformClick();
                    await Task.Delay(700);
                    await Until(() => player.IsPlaying && MpvLog().Contains(resolver.ExpectedTitle) && MpvLog().Contains("playback restart complete"), "replacement failed");
                    Assert(status.Text.StartsWith("Playing"), "Stale exit reset new playback");
                    Log("PASS replace playing video / no stale status");
                }
                if (name == "small")
                {
                    await Until(() => !player.IsPlaying, "EOF did not release player", 20000);
                    await Task.Delay(200);
                    Assert(status.Text.Contains("播放结束"), "EOF status missing");
                    Log("PASS natural EOF");
                }
                else stop.PerformClick();
                await Until(() => !player.IsPlaying && GetWindow(host, 5) == IntPtr.Zero, "Stop left a video child");
                Assert(surface.Handle == host && surface.Visible && surface.BackColor == Color.Black, "Persistent black host lost");
                Log("PASS " + name + " / audio+video / child HWND / resize / black idle");
            }
            File.WriteAllText(Path.Combine(fixtures, "invalid.mp4"), "not a media file");
            resolver.Name = "invalid";
            play.PerformClick();
            await Until(() => !player.IsPlaying && status.Text.StartsWith("Error"), "Invalid media did not report error");
            await Until(() => GetWindow(host, 5) == IntPtr.Zero, "Error left child window");
            Assert(surface.Handle == host && surface.BackColor == Color.Black, "Error lost black idle host");
            Log("PASS playback error / black idle");
            resolver.Name = "wide";
            resolver.Delay = true;
            play.PerformClick();
            stop.PerformClick();
            resolver.Delay = false;
            play.PerformClick();
            await Task.Delay(500);
            await Until(() => player.IsPlaying && MpvLog().Contains(resolver.ExpectedTitle) && MpvLog().Contains("playback restart complete"), "Cancel/replay failed");
            Assert(status.Text.StartsWith("Playing"), "Cancelled resolution overwrote new playback");
            Log("PASS cancel resolution then replay");
            form.Close();
            Assert(!player.IsPlaying, "Close left mpv running");
            Log("PASS close while playing");
        }
    }

    private sealed class FixtureResolver : IStreamResolver
    {
        private readonly FixtureSource source;
        public string Name = "wide";
        public bool Delay;
        public string ExpectedTitle;
        public FixtureResolver(FixtureSource source) { this.source = source; }
        public async Task<VideoInfo> ResolveAsync(string url, CancellationToken token)
        {
            if (Delay) await Task.Delay(1500, token);
            ExpectedTitle = "Embedded test: " + Name + " " + Guid.NewGuid().ToString("N");
            return new VideoInfo
            {
                Title = ExpectedTitle, FormatId = Name, VideoCodec = "avc1", Height = 360, Fps = 25,
                VideoStreamUrl = source.Url + (Name == "separate" ? "video.mp4" : Name + ".mp4"),
                AudioStreamUrl = Name == "separate" ? source.Url + "audio.m4a" : null
            };
        }
    }
    private sealed class FixtureSource : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly string directory;
        public string Url { get; private set; }
        public FixtureSource(string directory)
        {
            this.directory = directory;
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            Task.Run(async () =>
            {
                try { while (true) { var client = await listener.AcceptTcpClientAsync(); _ = Task.Run(() => Serve(client)); } }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            });
        }
        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    string first = reader.ReadLine();
                    string name = Path.GetFileName(first.Split(' ')[1]);
                    byte[] data = File.ReadAllBytes(Path.Combine(directory, name));
                    long start = 0, end = data.Length - 1;
                    string line; bool range = false;
                    while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            range = true;
                            string[] parts = line.Substring(13).Split('-');
                            start = long.Parse(parts[0]);
                            if (parts[1].Length > 0) end = Math.Min(end, long.Parse(parts[1]));
                        }
                    string headers = "HTTP/1.1 " + (range ? "206 Partial Content" : "200 OK") +
                        "\r\nContent-Type: " + (name.EndsWith(".m4a") ? "audio/mp4" : "video/mp4") +
                        "\r\nAccept-Ranges: bytes\r\nContent-Length: " + (end - start + 1) +
                        (range ? "\r\nContent-Range: bytes " + start + "-" + end + "/" + data.Length : "") +
                        "\r\nConnection: close\r\n\r\n";
                    byte[] bytes = Encoding.ASCII.GetBytes(headers);
                    stream.Write(bytes, 0, bytes.Length);
                    if (!first.StartsWith("HEAD ")) stream.Write(data, (int)start, (int)(end - start + 1));
                }
            }
            catch (IOException) { }
        }
        public void Dispose() { listener.Stop(); }
    }
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}

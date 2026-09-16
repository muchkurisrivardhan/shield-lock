// S.H.I.E.L.D. Lock: animated wallpaper reveals around Windows sign-in.
//  - Every unlock: a 10-second reveal animation of the current wallpaper, drawn over the desktop
//    (click-through, never takes focus). The effect is picked from the wallpaper's own fingerprint,
//    so each wallpaper always gets the same animation; the "Wall. Paper." image has a bespoke one.
//  - New wallpaper set: detected instantly, the new animation is previewed, rendered to an MP4 in
//    renders/ (ffmpeg) and committed + pushed to git in the background.
//  - Failed sign-ins: a full-screen red "UNAUTHORIZED ACCESS" alert, played before the intro.
// Windows won't let apps draw on the lock screen, so failures that happen while locked are queued
// and played the moment the PC is unlocked.
// Written in C# 5 because that's what the compiler bundled with Windows supports (see build.cmd).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

static class Program
{
    // Windows Hello logs event 7001 ("A user failed to sign into the device") for every wrong PIN.
    // Unlike the Security log (event 4625), this log is readable without admin rights.
    const string HelloLog = "Microsoft-Windows-HelloForBusiness/Operational";
    const string FailedSignIn = "*[System[EventID=7001]]";
    const double Duration = 10.0, Fps = 30;

    static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string RendersDir = System.IO.Path.Combine(BaseDir, "renders");
    static readonly List<DateTime> pending = new List<DateTime>();
    static readonly SoundPlayer alarm = new SoundPlayer(AlarmWav());
    static Application app;
    static Forms.NotifyIcon tray;      // static so the GC can't collect them while the app sits idle
    static EventLogWatcher watcher;
    static bool locked, showing, testMode, rendering;
    static bool welcomePending = true; // the app starts at sign-in, so greet once on launch too
    static string currentWallpaper;
    static DateTime currentWallpaperTime;

    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

    [STAThread]
    static void Main(string[] args)
    {
        int at = Array.IndexOf(args, "--effect");
        if (at >= 0 && at + 1 < args.Length) forcedEffect = int.Parse(args[at + 1]);
        at = Array.IndexOf(args, "--render");
        if (at >= 0) // render an image's animation (default: the current wallpaper) to renders/ and exit
        {
            string path = at + 1 < args.Length && File.Exists(args[at + 1]) ? args[at + 1] : WallpaperPath();
            if (path == null) { Log("--render: no wallpaper set"); return; }
            RenderVideo(path, RenderPath(path), args.Contains("--no-push"));
            return;
        }

        bool firstInstance;
        using (new Mutex(true, "ShieldLock.SingleInstance", out firstInstance))
        {
            if (!firstInstance) return;
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            if (args.Contains("--test")) // alert + welcome with fake data, then exit
            {
                testMode = true;
                if (!args.Contains("--welcome-only"))
                {
                    pending.Add(DateTime.Now.AddMinutes(-3));
                    pending.Add(DateTime.Now.AddMinutes(-2));
                }
                Flush();
                app.Run();
                return;
            }

            tray = new Forms.NotifyIcon { Icon = Drawing.SystemIcons.Shield, Text = "S.H.I.E.L.D. Lock - watching sign-ins", Visible = true };
            tray.ContextMenuStrip = new Forms.ContextMenuStrip();
            tray.ContextMenuStrip.Items.Add("Test animation", null, delegate { pending.Add(DateTime.Now); welcomePending = true; Flush(); });
            tray.ContextMenuStrip.Items.Add("Re-render wallpaper video", null, delegate { if (currentWallpaper != null) StartRender(currentWallpaper); });
            tray.ContextMenuStrip.Items.Add("Open renders folder", null, delegate { Directory.CreateDirectory(RendersDir); Process.Start(RendersDir); });
            tray.ContextMenuStrip.Items.Add("Exit", null, delegate { tray.Visible = false; app.Shutdown(); });

            SystemEvents.SessionSwitch += (s, e) => app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
                if (e.Reason == SessionSwitchReason.SessionUnlock) { locked = false; welcomePending = true; Flush(); }
            }));
            // Windows raises this when the wallpaper changes (Settings, Photos, right-click "Set as background")
            SystemEvents.UserPreferenceChanged += (s, e) => app.Dispatcher.BeginInvoke(new Action(CheckWallpaper));

            watcher = new EventLogWatcher(new EventLogQuery(HelloLog, PathType.LogName, FailedSignIn));
            watcher.EventRecordWritten += (s, e) =>
            {
                if (e.EventRecord == null) return;
                DateTime when = e.EventRecord.TimeCreated ?? DateTime.Now;
                app.Dispatcher.BeginInvoke(new Action(() => { pending.Add(when); if (!locked) Flush(); }));
            };
            watcher.Enabled = true;

            // Failures at the sign-in screen after a reboot happen before this app starts; pick them up now.
            string sinceBoot = "*[System[EventID=7001 and TimeCreated[timediff(@SystemTime) <= " + GetTickCount64() + "]]]";
            using (var reader = new EventLogReader(new EventLogQuery(HelloLog, PathType.LogName, sinceBoot)))
            {
                EventRecord r;
                while ((r = reader.ReadEvent()) != null)
                    using (r) pending.Add(r.TimeCreated ?? DateTime.Now);
            }
            CheckWallpaper();
            // At sign-in the shell is still painting; let the desktop settle before the intro plays over it.
            var firstPlay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            firstPlay.Tick += delegate { firstPlay.Stop(); Flush(); };
            firstPlay.Start();
            // Photos-app changes don't always raise UserPreferenceChanged, so also poll (cheap registry read)
            var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            poll.Tick += delegate { CheckWallpaper(); };
            poll.Start();

            app.Run();
        }
    }

    // New wallpaper? Preview its animation now and render the video (once per distinct image).
    static void CheckWallpaper()
    {
        string path = WallpaperPath();
        if (path == null) return;
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        if (path == currentWallpaper && stamp == currentWallpaperTime) return;
        bool startup = currentWallpaper == null;
        currentWallpaper = path;
        currentWallpaperTime = stamp;
        Log("wallpaper: " + path);
        if (!startup) { welcomePending = true; Flush(); }
        if (File.Exists(RenderPath(path))) return;
        // render after the preview has finished so the two don't fight for the CPU
        var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(startup ? 1 : Duration + 2) };
        later.Tick += delegate { later.Stop(); StartRender(path); };
        later.Start();
    }

    // Plays whatever is queued, one overlay at a time: intrusion alert first, then the welcome.
    static void Flush()
    {
        if (showing) return;
        if (pending.Count > 0)
        {
            var attempts = new List<DateTime>(pending);
            pending.Clear();
            ShowAlert(attempts);
        }
        else if (welcomePending)
        {
            welcomePending = false;
            ShowWelcome();
        }
        else if (testMode) app.Shutdown();
    }

    static void Track(Window w)
    {
        showing = true;
        w.Closed += delegate { showing = false; Flush(); }; // play anything that queued up meanwhile
    }

    static void ShowAlert(List<DateTime> attempts)
    {
        Window w;
        using (Stream xaml = typeof(Program).Assembly.GetManifestResourceStream("ShieldAlert.xaml"))
            w = (Window)XamlReader.Load(xaml);
        if (!SystemParameters.ClientAreaAnimation) w.Triggers.Clear(); // Windows "Animation effects" is off
        Track(w);

        var lines = new List<string>();
        lines.Add(string.Format("> {0} FAILED SIGN-IN ATTEMPT{1} DETECTED", attempts.Count, attempts.Count == 1 ? "" : "S"));
        foreach (DateTime t in attempts.Skip(Math.Max(0, attempts.Count - 3)))
            lines.Add("> WRONG PIN ........ " + t.ToString("HH:mm:ss"));
        lines.Add("> INTRUSION LOGGED");
        lines.Add("> NOTIFYING LEVEL 7 SECURITY PERSONNEL");

        var log = (TextBlock)w.FindName("Log");
        string text = string.Join("\n", lines);
        int typed = 0;
        var typer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(22) };
        var hold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        typer.Tick += delegate
        {
            if (typed < text.Length) log.Text = text.Substring(0, ++typed);
            else { typer.Stop(); hold.Start(); }
        };
        hold.Tick += delegate { w.Close(); };
        w.KeyDown += delegate { w.Close(); };
        w.MouseDown += delegate { w.Close(); };
        w.Closed += delegate { typer.Stop(); hold.Stop(); };

        w.Show();
        w.Activate();
        alarm.Play();
        typer.Start();
    }

    // The live intro: a transparent, click-through window running the same scene the video renderer uses.
    static void ShowWelcome()
    {
        if (!SystemParameters.ClientAreaAnimation) return; // Windows "Animation effects" is off: skip the intro
        string path = WallpaperPath();
        BitmapSource bmp = path == null ? null : LoadBitmap(path);
        if (bmp == null) return;

        var w = new Window
        {
            Title = "Agent Welcome", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, Left = 0, Top = 0, Width = SystemParameters.PrimaryScreenWidth, Height = SystemParameters.PrimaryScreenHeight,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false
        };
        Track(w);
        w.SourceInitialized += delegate
        {
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };

        var root = new Grid { Background = Brushes.Black };
        w.Content = root;
        var sb = new Storyboard();
        BuildScene(root, bmp, w.Width, w.Height, AHash(path), Accent(path), sb);

        var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Duration + 0.1) };
        close.Tick += delegate { w.Close(); };
        w.Closed += delegate { close.Stop(); };
        w.Loaded += delegate { sb.Begin(); close.Start(); };
        w.Show();
    }

    // ---------------------------------------------------------------------------------------------
    // Video rendering: the scene is built off-screen on its own thread, the storyboard is stepped
    // frame by frame and the frames are piped raw into ffmpeg. Then the MP4 is committed and pushed.
    // ---------------------------------------------------------------------------------------------
    static string RenderPath(string wallpaper)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(wallpaper);
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return System.IO.Path.Combine(RendersDir, name.Replace(' ', '_').Replace('.', '_') + "-" + AHash(wallpaper).ToString("x16").Substring(0, 8) + ".mp4");
    }

    static void StartRender(string wallpaper)
    {
        if (rendering) return;
        rendering = true;
        var t = new Thread(delegate () { try { RenderVideo(wallpaper, RenderPath(wallpaper), false); } finally { rendering = false; } });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    static void RenderVideo(string wallpaper, string mp4, bool noPush)
    {
        try
        {
            BitmapSource bmp = LoadBitmap(wallpaper);
            if (bmp == null) { Log("render: can't load " + wallpaper); return; }
            int w = (int)SystemParameters.PrimaryScreenWidth & ~1, h = (int)SystemParameters.PrimaryScreenHeight & ~1; // even sizes for yuv420p
            var root = new Grid { Width = w, Height = h, Background = Brushes.Black, ClipToBounds = true };
            var sb = new Storyboard();
            BuildScene(root, bmp, w, h, AHash(wallpaper), Accent(wallpaper), sb);
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            sb.Begin(root, true);
            sb.Pause(root);

            Directory.CreateDirectory(RendersDir);
            string tmp = mp4 + ".part.mp4";
            var ff = new Process();
            ff.StartInfo = new ProcessStartInfo("ffmpeg", string.Format(CultureInfo.InvariantCulture,
                "-y -loglevel error -f rawvideo -pix_fmt bgra -s {0}x{1} -r {2} -i - -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart \"{3}\"", w, h, Fps, tmp))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
            ff.Start();
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var buf = new byte[w * h * 4];
            int frames = (int)(Duration * Fps);
            Stream stdin = ff.StandardInput.BaseStream;
            var clock = Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                sb.SeekAlignedToLastTick(root, TimeSpan.FromSeconds(f / Fps), TimeSeekOrigin.BeginTime);
                root.UpdateLayout();
                rtb.Clear();
                rtb.Render(root);
                rtb.CopyPixels(buf, w * 4, 0);
                stdin.Write(buf, 0, buf.Length);
            }
            stdin.Close();
            string err = ff.StandardError.ReadToEnd();
            ff.WaitForExit();
            if (ff.ExitCode != 0) { Log("ffmpeg failed: " + err); return; }
            if (File.Exists(mp4)) File.Delete(mp4);
            File.Move(tmp, mp4);
            Log(string.Format("rendered {0} ({1}x{2}, {3} frames, {4:0}s)", System.IO.Path.GetFileName(mp4), w, h, frames, clock.Elapsed.TotalSeconds));
            if (!noPush) Publish(mp4);
        }
        catch (Exception ex) { Log("render error: " + ex); }
    }

    // git add/commit/push the new video, if this folder is a git repo with a remote.
    static void Publish(string mp4)
    {
        if (!Directory.Exists(System.IO.Path.Combine(BaseDir, ".git"))) return;
        string rel = "renders/" + System.IO.Path.GetFileName(mp4);
        Git("add -- \"" + rel + "\"");
        Git("commit -m \"Add wallpaper animation " + System.IO.Path.GetFileNameWithoutExtension(mp4) + "\" -- \"" + rel + "\"");
        Git("push");
    }

    static void Git(string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = BaseDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(180000);
            Log("git " + args + " -> " + p.ExitCode + " " + output.Trim());
        }
        catch (Exception ex) { Log("git " + args + " failed: " + ex.Message); }
    }

    static void Log(string line)
    {
        try { File.AppendAllText(System.IO.Path.Combine(BaseDir, "shieldlock.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + "\r\n"); } catch { }
    }

    // ---------------------------------------------------------------------------------------------
    // Scenes. Everything animates through one Storyboard so the same scene plays live and renders
    // to video by seeking. The picture is drawn exactly where Windows places the wallpaper.
    // ---------------------------------------------------------------------------------------------
    // Fingerprint of the "Wall. Paper." wallpaper (see AHash); that one gets its hand-made animation.
    const ulong WallPaperHash = 0x0000383838080000UL;
    static int forcedEffect = -1; // --effect N (testing)

    static void BuildScene(Grid root, BitmapSource bmp, double W, double H, ulong hash, Color accent, Storyboard sb)
    {
        Rect placed = WallpaperRect(bmp, W, H);
        var rnd = new Random((int)(hash ^ (hash >> 32)));
        int effect = forcedEffect >= 0 ? forcedEffect
            : Hamming(hash, WallPaperHash) <= 4 ? 5
            : Hamming(hash, ItachiHash) <= 4 ? 6
            : (int)((hash ^ (hash >> 17) ^ (hash >> 41)) % 5);
        if (effect == 5) WallPaperScene(root, bmp, placed, sb);
        else if (effect == 6) ItachiScene(root, bmp, placed, W, H, sb, rnd);
        else switch (effect)
        {
            case 0: Mosaic(root, bmp, placed, accent, sb, rnd); break;
            case 1: ScanBeam(root, bmp, placed, accent, sb); break;
            case 2: Pixelate(root, bmp, placed, sb); break;
            case 3: Strips(root, bmp, placed, accent, sb); break;
            default: Iris(root, bmp, placed, accent, sb, rnd); break;
        }
        Animate(sb, root, UIElement.OpacityProperty, Keys(Duration - 0.7, 1, Duration, 0)); // dissolve to the desktop
    }

    // 1. Mosaic: the picture assembles from tiles flying in from every direction, rippling out from a random point.
    static void Mosaic(Grid root, BitmapSource bmp, Rect p, Color accent, Storyboard sb, Random rnd)
    {
        var canvas = new Canvas();
        root.Children.Add(canvas);
        const int cols = 12, rows = 8;
        var origin = new Point(rnd.NextDouble(), rnd.NextDouble());
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Rectangle tile = Tile(canvas, bmp, p, (double)c / cols, (double)r / rows, (c + 1.0) / cols, (r + 1.0) / rows);
                double d = Math.Sqrt(Math.Pow((c + 0.5) / cols - origin.X, 2) + Math.Pow((r + 0.5) / rows - origin.Y, 2)) / Math.Sqrt(2);
                double ang = rnd.NextDouble() * Math.PI * 2, dist = 500 + rnd.NextDouble() * 700;
                var tg = new TransformGroup();
                tg.Children.Add(new ScaleTransform(0.4, 0.4));
                tg.Children.Add(new RotateTransform(rnd.Next(-160, 160)));
                tg.Children.Add(new TranslateTransform(Math.Cos(ang) * dist, Math.Sin(ang) * dist));
                tile.RenderTransformOrigin = new Point(0.5, 0.5);
                tile.RenderTransform = tg;
                tile.Opacity = 0;
                double at = 0.3 + 5.5 * d + rnd.NextDouble() * 0.2, end = at + 0.9;
                AnimatePath(sb, tile, "RenderTransform.Children[0].ScaleX", Keys(at, 0.4, end, 1), ease);
                AnimatePath(sb, tile, "RenderTransform.Children[0].ScaleY", Keys(at, 0.4, end, 1), ease);
                AnimatePath(sb, tile, "RenderTransform.Children[1].Angle", Keys(at, ((RotateTransform)tg.Children[1]).Angle, end, 0), ease);
                AnimatePath(sb, tile, "RenderTransform.Children[2].X", Keys(at, Math.Cos(ang) * dist, end, 0), ease);
                AnimatePath(sb, tile, "RenderTransform.Children[2].Y", Keys(at, Math.Sin(ang) * dist, end, 0), ease);
                Animate(sb, tile, UIElement.OpacityProperty, Keys(at, 0, at + 0.25, 1));
            }
        Sweep(root, p, accent, sb, 7.6, 0.9);
    }

    // 2. Scan beam: a glowing line paints the sharp picture in over a blurred ghost of it.
    static void ScanBeam(Grid root, BitmapSource bmp, Rect p, Color accent, Storyboard sb)
    {
        Image ghost = Placed(bmp, p);
        ghost.Effect = new BlurEffect { Radius = 30 };
        ghost.Opacity = 0;
        root.Children.Add(ghost);
        Animate(sb, ghost, UIElement.OpacityProperty, Keys(0, 0, 0.7, 0.25, 7.8, 0.25, 8.4, 0));

        Image sharp = Placed(bmp, p);
        var clip = new RectangleGeometry(new Rect(0, 0, p.Width, 0));
        sharp.Clip = clip;
        root.Children.Add(sharp);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var reveal = new RectAnimationUsingKeyFrames();
        reveal.KeyFrames.Add(new LinearRectKeyFrame(new Rect(0, 0, p.Width, 0), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.5))));
        reveal.KeyFrames.Add(new EasingRectKeyFrame(new Rect(0, 0, p.Width, p.Height), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8)), ease));
        Storyboard.SetTarget(reveal, sharp);
        Storyboard.SetTargetProperty(reveal, new PropertyPath("Clip.Rect"));
        sb.Children.Add(reveal);

        var beam = new Canvas { RenderTransform = new TranslateTransform(), Opacity = 0 };
        var trail = new Rectangle { Width = p.Width, Height = 160, Fill = new LinearGradientBrush(Color.FromArgb(0, accent.R, accent.G, accent.B), Color.FromArgb(90, accent.R, accent.G, accent.B), 90) };
        Canvas.SetLeft(trail, p.X); Canvas.SetTop(trail, p.Y - 160);
        var line = new Rectangle { Width = p.Width, Height = 5, Fill = new SolidColorBrush(accent), Effect = new DropShadowEffect { Color = accent, BlurRadius = 30, ShadowDepth = 0 } };
        Canvas.SetLeft(line, p.X); Canvas.SetTop(line, p.Y - 2);
        beam.Children.Add(trail);
        beam.Children.Add(line);
        root.Children.Add(beam);
        AnimatePath(sb, beam, "RenderTransform.Y", Keys(0.5, 0, 8, p.Height), ease);
        Animate(sb, beam, UIElement.OpacityProperty, Keys(0.4, 0, 0.6, 1, 8, 1, 8.3, 0));
    }

    // 3. Pixelate: the picture resolves from huge blocks down to sharp while slowly settling from a zoom.
    static void Pixelate(Grid root, BitmapSource bmp, Rect p, Storyboard sb)
    {
        var stack = new Grid { RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1.06, 1.06) };
        root.Children.Add(stack);
        int[] factors = { 48, 24, 12, 6, 3 };
        for (int i = 0; i < factors.Length; i++)
        {
            var small = new TransformedBitmap(bmp, new ScaleTransform(1.0 / factors[i], 1.0 / factors[i]));
            small.Freeze();
            Image img = Placed(small, p);
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            img.Opacity = 0;
            stack.Children.Add(img);
            double at = 0.3 + i * 1.35;
            Animate(sb, img, UIElement.OpacityProperty, i < factors.Length - 1 ? Steps(at, 1, at + 1.35, 0) : Steps(at, 1, 7.6, 0));
        }
        Image sharp = Placed(bmp, p);
        sharp.Opacity = 0;
        stack.Children.Add(sharp);
        Animate(sb, sharp, UIElement.OpacityProperty, Keys(7.0, 0, 7.6, 1));
        AnimatePath(sb, stack, "RenderTransform.ScaleX", Keys(0, 1.06, 8, 1), null);
        AnimatePath(sb, stack, "RenderTransform.ScaleY", Keys(0, 1.06, 8, 1), null);
    }

    // 4. Strips: vertical slices slide in alternately from top and bottom, centre first, then a flash.
    static void Strips(Grid root, BitmapSource bmp, Rect p, Color accent, Storyboard sb)
    {
        var canvas = new Canvas();
        root.Children.Add(canvas);
        const int n = 24;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < n; i++)
        {
            Rectangle strip = Tile(canvas, bmp, p, (double)i / n, 0, (i + 1.0) / n, 1);
            strip.RenderTransform = new TranslateTransform();
            strip.Opacity = 0;
            double order = Math.Abs(i - (n - 1) / 2.0) / ((n - 1) / 2.0);
            double at = 0.3 + 4.5 * order, from = i % 2 == 0 ? -(p.Height + 120) : p.Height + 120;
            AnimatePath(sb, strip, "RenderTransform.Y", Keys(at, from, at + 0.8, 0), ease);
            Animate(sb, strip, UIElement.OpacityProperty, Keys(at, 0, at + 0.1, 1));
        }
        var flash = new Rectangle { Fill = new SolidColorBrush(accent), Opacity = 0 };
        canvas.Children.Add(flash);
        flash.Width = p.Width; flash.Height = p.Height;
        Canvas.SetLeft(flash, p.X); Canvas.SetTop(flash, p.Y);
        Animate(sb, flash, UIElement.OpacityProperty, Keys(6.9, 0, 7.1, 0.35, 7.9, 0));
    }

    // 5. Iris: the sharp picture blooms out from a random point over a blurred ghost, with a glowing ring on the edge.
    static void Iris(Grid root, BitmapSource bmp, Rect p, Color accent, Storyboard sb, Random rnd)
    {
        Image ghost = Placed(bmp, p);
        ghost.Effect = new BlurEffect { Radius = 30 };
        ghost.Opacity = 0;
        root.Children.Add(ghost);
        Animate(sb, ghost, UIElement.OpacityProperty, Keys(0, 0, 0.7, 0.25, 7.6, 0.25, 8.2, 0));

        var c = new Point(0.2 + rnd.NextDouble() * 0.6, 0.2 + rnd.NextDouble() * 0.6);
        Image sharp = Placed(bmp, p);
        var mask = new RadialGradientBrush { Center = c, GradientOrigin = c, RadiusX = 0, RadiusY = 0 };
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.85));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        sharp.OpacityMask = mask;
        root.Children.Add(sharp);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        double rx = 1.7, ry = 1.7 * p.Width / p.Height;
        AnimatePath(sb, sharp, "OpacityMask.RadiusX", Keys(0.5, 0, 7.5, rx), ease);
        AnimatePath(sb, sharp, "OpacityMask.RadiusY", Keys(0.5, 0, 7.5, ry), ease);

        var ring = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(accent), StrokeThickness = 3, Opacity = 0,
            Effect = new DropShadowEffect { Color = accent, BlurRadius = 25, ShadowDepth = 0 },
            Data = new EllipseGeometry(new Point(p.X + c.X * p.Width, p.Y + c.Y * p.Height), 0, 0)
        };
        root.Children.Add(ring);
        AnimatePath(sb, ring, "Data.RadiusX", Keys(0.5, 0, 7.5, rx * p.Width * 0.9), ease);
        AnimatePath(sb, ring, "Data.RadiusY", Keys(0.5, 0, 7.5, rx * p.Width * 0.9), ease);
        Animate(sb, ring, UIElement.OpacityProperty, Keys(0.4, 0, 0.6, 1, 6.8, 1, 7.5, 0));
    }

    // A glowing accent line sweeping top to bottom over the picture: the "polish" beat.
    static void Sweep(Grid root, Rect p, Color accent, Storyboard sb, double at, double dur)
    {
        var canvas = new Canvas();
        root.Children.Add(canvas);
        var line = new Rectangle { Width = p.Width, Height = 4, Fill = new SolidColorBrush(accent), Opacity = 0, RenderTransform = new TranslateTransform(), Effect = new DropShadowEffect { Color = accent, BlurRadius = 30, ShadowDepth = 0 } };
        Canvas.SetLeft(line, p.X); Canvas.SetTop(line, p.Y);
        canvas.Children.Add(line);
        AnimatePath(sb, line, "RenderTransform.Y", Keys(at, 0, at + dur, p.Height), new QuadraticEase { EasingMode = EasingMode.EaseInOut });
        Animate(sb, line, UIElement.OpacityProperty, Keys(at, 0, at + 0.1, 1, at + dur - 0.1, 1, at + dur, 0));
    }

    // The picture, placed exactly where Windows draws the wallpaper.
    static Image Placed(BitmapSource bmp, Rect p)
    {
        return new Image { Source = bmp, Stretch = Stretch.Fill, Width = p.Width, Height = p.Height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(p.X, p.Y, 0, 0) };
    }

    // One rectangular piece of the picture (u/v in 0..1), in place.
    static Rectangle Tile(Canvas canvas, BitmapSource bmp, Rect p, double u0, double v0, double u1, double v1)
    {
        var tile = new Rectangle
        {
            Width = p.Width * (u1 - u0) + 0.5, Height = p.Height * (v1 - v0) + 0.5,
            Fill = new ImageBrush(bmp) { Viewbox = new Rect(u0, v0, u1 - u0, v1 - v0), ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill }
        };
        Canvas.SetLeft(tile, p.X + u0 * p.Width);
        Canvas.SetTop(tile, p.Y + v0 * p.Height);
        canvas.Children.Add(tile);
        return tile;
    }

    // --- "Itachi" bespoke scene: rain, then the eyes open ---------------------------------------
    //   0.0s rain in the dark        1.0s / 2.6s lightning glimpses the face, eyes shut
    //   1.8s face slowly surfaces    3.4s red light leaks between the lids
    //   3.6s lids part, droop, then open fully by 6.0s     6.0s Sharingan flare: shockwave, picture lights up
    //   6.6s name card               8.4s crossfade to the untouched picture
    const ulong ItachiHash = 0x83c31230b0969697UL;

    // Eye openings in the 3840x2160 picture, traced by hand from a zoomed grid: the outline the lids are
    // clipped to, the two corners the closed lash line runs between, how far it sags, and the skin grey beside it.
    sealed class EyeSpec { public string Outline; public Point Left, Right; public double Sag, Skin; }
    static readonly EyeSpec[] ItachiEyes =
    {
        new EyeSpec { Outline = "1493,611 1550,612 1607,646 1651,675 1656,688 1629,707 1604,718 1580,724 1560,727 1540,725 1520,723 1506,721 1493,718",
                      Left = new Point(1493, 692), Right = new Point(1656, 688), Sag = 10, Skin = 104 },
        new EyeSpec { Outline = "2255,670 2280,653 2297,640 2317,621 2340,613 2370,613 2383,598 2410,593 2432,590 2434,676 2418,695 2390,709 2357,718 2327,719 2297,710 2270,695 2251,680",
                      Left = new Point(2251, 677), Right = new Point(2434, 668), Sag = 13, Skin = 117 },
    };

    static void ItachiScene(Grid root, BitmapSource bmp, Rect p, double W, double H, Storyboard sb, Random rnd)
    {
        double iw = bmp.PixelWidth, ih = bmp.PixelHeight;
        Color red = Color.FromRgb(0xFF, 0x1A, 0x2E);
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var outlines = ItachiEyes.Select(e => Geo("M " + e.Outline.Replace(" ", " L ") + " Z")).ToArray();
        double midX = (outlines[0].Bounds.Left + outlines[1].Bounds.Right) / 2 / iw, midY = (outlines[0].Bounds.Top + outlines[1].Bounds.Bottom) / 2 / ih;

        // camera: creeps toward the eyes while they open, punches on the flare, then settles to 1:1
        var cam = new Grid { RenderTransformOrigin = new Point((p.X + midX * p.Width) / W, (p.Y + midY * p.Height) / H), RenderTransform = new ScaleTransform(1.16, 1.16) };
        root.Children.Add(cam);
        foreach (string axis in new[] { "ScaleX", "ScaleY" })
            AnimatePath(sb, cam, "RenderTransform." + axis, Mix(0, 1.16, 1, 5.95, 1.10, 3, 6.0, 1.14, 1, 8.4, 1.0, 2), null);

        // the face and its eyelids dim and brighten together: black, two lightning glimpses, slow surfacing, full light on the flare
        var face = new Grid();
        cam.Children.Add(face);
        face.Children.Add(Placed(bmp, p));
        Canvas lids = PictureCanvas(p, iw);
        face.Children.Add(lids);
        Animate(sb, face, UIElement.OpacityProperty, Mix(0, 0, 1, 1.0, 0.75, 1, 1.07, 0.1, 1, 1.15, 0.5, 1, 1.28, 0, 1, 1.8, 0, 1,
                                                        2.59, 0.22, 0, 2.6, 0.9, 1, 2.68, 0.25, 1, 3.4, 0.45, 0, 5.95, 0.55, 0, 6.0, 1, 1));

        Canvas fx = PictureCanvas(p, iw); // glows and shockwaves stay bright over the dim face
        cam.Children.Add(fx);

        for (int n = 0; n < ItachiEyes.Length; n++)
        {
            EyeSpec e = ItachiEyes[n];
            Rect box = outlines[n].Bounds;
            double cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
            double ctrlX = (e.Left.X + e.Right.X) / 2, ctrlY = (e.Left.Y + e.Right.Y) / 2 + 2 * e.Sag, seamMid = (e.Left.Y + e.Right.Y) / 2 + e.Sag;
            string seam = Fmt("{0},{1} Q {2},{3} {4},{5}", e.Left.X, e.Left.Y, ctrlX, ctrlY, e.Right.X, e.Right.Y);
            double L = box.Left - 30, R = box.Right + 30, T = box.Top - 60, B = box.Bottom + 60;
            byte skin = (byte)e.Skin, lowSkin = (byte)(e.Skin * 0.92), shadow = (byte)(e.Skin * 0.3);

            // eyelids clipped to the traced opening: the upper one shades from the art's dark lid shadow to skin,
            // carries the lash line and casts a soft shadow onto the eye as it lifts
            var eye = new Canvas { Clip = outlines[n] };
            lids.Children.Add(eye);
            var lower = new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(Color.FromRgb(lowSkin, lowSkin, lowSkin)), RenderTransform = new TranslateTransform(),
                Data = Geo("M {0},{1} L " + seam + " L {2},{3} L {2},{4} L {0},{4} Z", L, e.Left.Y, R, e.Right.Y, B)
            };
            var upper = new Canvas { RenderTransform = new TranslateTransform() };
            upper.Children.Add(new System.Windows.Shapes.Path
            {
                Fill = new LinearGradientBrush(Color.FromRgb(shadow, shadow, shadow), Color.FromRgb(skin, skin, skin), new Point(0, box.Top), new Point(0, seamMid)) { MappingMode = BrushMappingMode.Absolute },
                Data = Geo("M {0},{1} L " + seam + " L {2},{3} L {2},{4} L {0},{4} Z", L, e.Left.Y, R, e.Right.Y, T)
            });
            upper.Children.Add(new System.Windows.Shapes.Path
            {
                Stroke = Brushes.Black, StrokeThickness = 14, Opacity = 0.35,
                Data = Geo("M {0},{1} Q {2},{3} {4},{5}", e.Left.X, e.Left.Y + 9, ctrlX, ctrlY + 9, e.Right.X, e.Right.Y + 9)
            });
            upper.Children.Add(new System.Windows.Shapes.Path
            {
                Stroke = Brushes.Black, StrokeThickness = 8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geo("M " + seam)
            });
            eye.Children.Add(lower);
            eye.Children.Add(upper);

            // open slowly: crack, droop as if heavy, then all the way (lids end clear of the opening)
            double up = seamMid + 16 - box.Top + 10, down = box.Bottom - Math.Min(e.Left.Y, e.Right.Y) + 10;
            AnimatePath(sb, upper, "RenderTransform.Y", Mix(0, 0, 1, 3.6, 0, 1, 4.3, -0.22 * up, 2, 4.55, -0.12 * up, 3, 4.75, -0.12 * up, 0, 6.0, -up, 3), null);
            AnimatePath(sb, lower, "RenderTransform.Y", Mix(0, 0, 1, 3.6, 0, 1, 4.3, 0.25 * down, 2, 4.55, 0.15 * down, 3, 4.75, 0.15 * down, 0, 6.0, down, 3), null);

            // red light leaking along the shut lash line
            var slit = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(red), StrokeThickness = 5, Opacity = 0, Data = Geo("M " + seam),
                Effect = new DropShadowEffect { Color = red, BlurRadius = 35, ShadowDepth = 0 }
            };
            fx.Children.Add(slit);
            Animate(sb, slit, UIElement.OpacityProperty, Keys(3.3, 0, 3.6, 1, 4.3, 0.8, 4.8, 0));

            // glow that swells with the opening and flares when the eyes are fully open
            double gr = box.Height * 2.4;
            var glow = new Ellipse { Width = gr * 2, Height = gr * 2, Opacity = 0, Fill = new RadialGradientBrush(Color.FromArgb(150, red.R, red.G, red.B), Color.FromArgb(0, red.R, red.G, red.B)) };
            Canvas.SetLeft(glow, cx - gr); Canvas.SetTop(glow, cy - gr);
            fx.Children.Add(glow);
            Animate(sb, glow, UIElement.OpacityProperty, Keys(3.5, 0, 4.3, 0.45, 4.55, 0.25, 4.75, 0.3, 5.95, 0.7, 6.05, 1, 6.5, 0.5, 7.2, 0.75, 7.9, 0.45, 8.6, 0));

            // shockwave ring off each eye on the flare
            var wave = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(red), StrokeThickness = 16, Opacity = 0, Data = new EllipseGeometry(new Point(cx, cy), 1, 1),
                Effect = new DropShadowEffect { Color = red, BlurRadius = 40, ShadowDepth = 0 }
            };
            fx.Children.Add(wave);
            AnimatePath(sb, wave, "Data.RadiusX", Keys(6.0, 1, 7.1, 2600), easeOut);
            AnimatePath(sb, wave, "Data.RadiusY", Keys(6.0, 1, 7.1, 2600), easeOut);
            Animate(sb, wave, UIElement.OpacityProperty, Keys(5.99, 0, 6.0, 1, 7.1, 0));
            Animate(sb, wave, Shape.StrokeThicknessProperty, Keys(6.0, 16, 7.1, 2));
        }

        // rain: a far layer of fine slow streaks and a near layer of heavy fast ones, all the way through
        foreach (Canvas layer in new[] { Rain(sb, rnd, W, H, 170, 1, 0.12, 0.3, 18, 45, 0.7, 1.0), Rain(sb, rnd, W, H, 55, 2, 0.35, 0.65, 60, 140, 0.32, 0.45) })
        {
            root.Children.Add(layer);
            Animate(sb, layer, UIElement.OpacityProperty, Keys(0, 0, 0.6, 1, 8.3, 1, 9.0, 0));
        }

        // lightning (two close strikes, one distant rumble) and the red flash as the eyes lock open
        var flash = new Rectangle { Fill = Brushes.White, Opacity = 0 };
        root.Children.Add(flash);
        Animate(sb, flash, UIElement.OpacityProperty, Steps(1.0, 0.7, 1.07, 0.1, 1.15, 0.45, 1.28, 0, 2.6, 0.8, 2.68, 0.15, 2.75, 0.3, 2.85, 0, 7.3, 0.12, 7.4, 0));
        var redFlash = new Rectangle { Fill = new SolidColorBrush(red), Opacity = 0 };
        root.Children.Add(redFlash);
        Animate(sb, redFlash, UIElement.OpacityProperty, Mix(5.99, 0, 1, 6.0, 0.35, 1, 6.5, 0, 2));

        // embers: red flecks drifting up after the flare
        var embers = new Canvas();
        root.Children.Add(embers);
        for (int i = 0; i < 40; i++)
        {
            double size = 3 + rnd.NextDouble() * 4, t0 = 6.2 + rnd.NextDouble() * 1.2, dur = 1.4 + rnd.NextDouble() * 0.8;
            var ember = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(red), Opacity = 0, RenderTransform = new TranslateTransform() };
            Canvas.SetLeft(ember, rnd.NextDouble() * W); Canvas.SetTop(ember, H * 0.45 + rnd.NextDouble() * H * 0.6);
            embers.Children.Add(ember);
            AnimatePath(sb, ember, "RenderTransform.Y", Keys(t0, 0, t0 + dur, -(150 + rnd.NextDouble() * 250)), easeOut);
            AnimatePath(sb, ember, "RenderTransform.X", Keys(t0, 0, t0 + dur / 2, (rnd.NextDouble() - 0.5) * 60, t0 + dur, (rnd.NextDouble() - 0.5) * 60), null);
            Animate(sb, ember, UIElement.OpacityProperty, Keys(t0, 0, t0 + 0.3, 0.9, t0 + dur - 0.3, 0.9, t0 + dur, 0));
        }

        // name card, bottom centre
        var card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, H * 0.09), Opacity = 0, RenderTransform = new TranslateTransform() };
        card.Children.Add(new TextBlock { Text = "うちはイタチ", FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI"), FontSize = 20, Foreground = new SolidColorBrush(red), HorizontalAlignment = HorizontalAlignment.Center });
        card.Children.Add(new TextBlock { Text = "I T A C H I   U C H I H A", FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Light, FontSize = 36, Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 8) });
        var rule = new Rectangle { Height = 2, Width = 260, Fill = new SolidColorBrush(red), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0, 1) };
        card.Children.Add(rule);
        root.Children.Add(card);
        Animate(sb, card, UIElement.OpacityProperty, Keys(6.6, 0, 7.1, 1, 8.0, 1, 8.4, 0));
        AnimatePath(sb, card, "RenderTransform.Y", Keys(6.6, 24, 7.3, 0), easeOut);
        AnimatePath(sb, rule, "RenderTransform.ScaleX", Keys(6.9, 0, 7.6, 1), easeOut);

        // crossfade onto the untouched picture so the dissolve to the desktop is seamless
        Image real = Placed(bmp, p);
        real.Opacity = 0;
        root.Children.Add(real);
        Animate(sb, real, UIElement.OpacityProperty, Keys(8.4, 0, 9.0, 1));
    }

    // One layer of looping rain streaks, slanted by the wind. Drops start above the screen so none sit waiting at the top.
    static Canvas Rain(Storyboard sb, Random rnd, double W, double H, int count, double thick, double o0, double o1, double l0, double l1, double s0, double s1)
    {
        var layer = new Canvas { Opacity = 0, RenderTransform = new SkewTransform(-8, 0) };
        for (int i = 0; i < count; i++)
        {
            double len = l0 + rnd.NextDouble() * (l1 - l0), x = rnd.NextDouble() * W * 1.3 - W * 0.05, speed = s0 + rnd.NextDouble() * (s1 - s0);
            var drop = new Line { X1 = x, X2 = x, Y1 = 0, Y2 = len, Stroke = Brushes.White, StrokeThickness = thick, Opacity = o0 + rnd.NextDouble() * (o1 - o0), RenderTransform = new TranslateTransform(0, -len - 10) };
            layer.Children.Add(drop);
            var fall = new DoubleAnimation(-len - 10, H + 10, TimeSpan.FromSeconds(speed)) { BeginTime = TimeSpan.FromSeconds(rnd.NextDouble() * speed), RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(fall, drop);
            Storyboard.SetTargetProperty(fall, new PropertyPath("RenderTransform.Y"));
            sb.Children.Add(fall);
        }
        return layer;
    }

    // A zero-size canvas whose coordinates are the picture's own pixels, mapped to where Windows draws it.
    static Canvas PictureCanvas(Rect p, double iw)
    {
        double s = p.Width / iw;
        var c = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(s, s));
        tg.Children.Add(new TranslateTransform(p.X, p.Y));
        c.RenderTransform = tg;
        return c;
    }

    static string Fmt(string format, params object[] args) { return string.Format(CultureInfo.InvariantCulture, format, args); }
    static Geometry Geo(string format, params object[] args) { return Geometry.Parse(Fmt(format, args)); }

    // Keyframes from (time, value, kind) triples; kind 0 = linear, 1 = jump, 2 = ease out, 3 = ease in-out.
    static DoubleAnimationUsingKeyFrames Mix(params double[] t)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        for (int i = 0; i < t.Length; i += 3)
        {
            KeyTime k = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t[i]));
            switch ((int)t[i + 2])
            {
                case 1: a.KeyFrames.Add(new DiscreteDoubleKeyFrame(t[i + 1], k)); break;
                case 2: a.KeyFrames.Add(new EasingDoubleKeyFrame(t[i + 1], k, new CubicEase { EasingMode = EasingMode.EaseOut })); break;
                case 3: a.KeyFrames.Add(new EasingDoubleKeyFrame(t[i + 1], k, new SineEase { EasingMode = EasingMode.EaseInOut })); break;
                default: a.KeyFrames.Add(new LinearDoubleKeyFrame(t[i + 1], k)); break;
            }
        }
        return a;
    }

    // --- "Wall. Paper." bespoke scene -----------------------------------------------------------
    //   0.0s blueprint outline      0.3-4.5s bricks drop in     4.6-5.8s paper flutters down
    //   5.9s lines write            6.6-8.2s caption types      8.4s crossfade to the real wallpaper
    static readonly Brush White = Brushes.White;
    const double ImgW = 1920, ImgH = 1200;
    static readonly Rect Wall = new Rect(613, 350, 317, 398);
    static readonly Rect Paper = new Rect(985, 428, 220, 244);

    static void WallPaperScene(Grid root, BitmapSource bmp, Rect placed, Storyboard sb)
    {
        double scale = placed.Width / ImgW;
        var scene = new Canvas { Width = ImgW, Height = ImgH, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var map = new TransformGroup();
        map.Children.Add(new ScaleTransform(scale, scale));
        map.Children.Add(new TranslateTransform(placed.X, placed.Y));
        scene.RenderTransform = map;
        root.Children.Add(scene);

        BuildBlueprint(scene, sb);
        BuildWall(scene, sb);
        BuildPaper(scene, sb);
        TypeAnim(sb, Caption(scene, "Wall.", 772), "Wall.", 6.6, 8.3);
        TypeAnim(sb, Caption(scene, "Paper.", 1115), "Paper.", 7.3, 8.3);

        Image real = Placed(bmp, placed);
        real.Opacity = 0;
        root.Children.Add(real);
        Animate(sb, real, UIElement.OpacityProperty, Keys(8.4, 0, 9.1, 1));
        Animate(sb, scene, UIElement.OpacityProperty, Keys(8.4, 1, 9.1, 0));
    }

    // Dashed "blueprint" outline where the wall will stand, with marching ants; gone once the wall is built.
    static void BuildBlueprint(Canvas scene, Storyboard sb)
    {
        var outline = new Rectangle { Width = Wall.Width + 12, Height = Wall.Height + 12, Stroke = White, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 6, 6 }, Opacity = 0 };
        Canvas.SetLeft(outline, Wall.X - 6);
        Canvas.SetTop(outline, Wall.Y - 6);
        scene.Children.Add(outline);
        Animate(sb, outline, UIElement.OpacityProperty, Keys(0, 0, 0.4, 0.6, 4.3, 0.6, 4.8, 0));
        Animate(sb, outline, Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -120, TimeSpan.FromSeconds(5)));
    }

    // Bricks laid bottom row first, left to right, each dropping in with a bounce; the pace speeds up.
    static void BuildWall(Canvas scene, Storyboard sb)
    {
        const double bw = 40, bh = 16, gap = 5, pitch = bh + gap;
        var bricks = new List<Rectangle>();
        for (int row = 0; row * pitch + bh <= Wall.Height + 1; row++)
        {
            double y = Wall.Bottom - bh - row * pitch;
            double x = Wall.X;
            bool odd = row % 2 == 1;
            if (odd) { bricks.Add(Brick(scene, x, y, (bw - gap) / 2, bh)); x += (bw - gap) / 2 + gap; }
            while (x + bw <= Wall.Right + 1) { bricks.Add(Brick(scene, x, y, bw, bh)); x += bw + gap; }
            if (odd) bricks.Add(Brick(scene, x, y, Wall.Right - x, bh));
        }
        for (int i = 0; i < bricks.Count; i++)
        {
            double at = 0.3 + 4.2 * Math.Pow((double)i / (bricks.Count - 1), 0.7); // gaps shrink: time-lapse feel
            Animate(sb, bricks[i], UIElement.OpacityProperty, Keys(at, 0, at + 0.05, 1));
            var drop = new DoubleAnimation(-60, 0, TimeSpan.FromSeconds(0.3)) { BeginTime = TimeSpan.FromSeconds(at), EasingFunction = new BounceEase { Bounces = 1, Bounciness = 3, EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(drop, bricks[i]);
            Storyboard.SetTargetProperty(drop, new PropertyPath("RenderTransform.Y"));
            sb.Children.Add(drop);
        }
    }

    static Rectangle Brick(Canvas scene, double x, double y, double w, double h)
    {
        var b = new Rectangle { Width = w, Height = h, Fill = White, Opacity = 0, RenderTransform = new TranslateTransform() };
        Canvas.SetLeft(b, x);
        Canvas.SetTop(b, y);
        scene.Children.Add(b);
        return b;
    }

    // The sheet falls in from above the screen, rocking like a leaf, then its three lines write themselves.
    static void BuildPaper(Canvas scene, Storyboard sb)
    {
        var sheet = new Canvas { Width = Paper.Width, Height = Paper.Height, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
        var tg = new TransformGroup();
        tg.Children.Add(new RotateTransform());
        tg.Children.Add(new TranslateTransform());
        sheet.RenderTransform = tg;
        Canvas.SetLeft(sheet, Paper.X);
        Canvas.SetTop(sheet, Paper.Y);
        scene.Children.Add(sheet);

        const double ear = 72; // dog-eared corner
        sheet.Children.Add(new System.Windows.Shapes.Path
        {
            Stroke = White, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Miter,
            Data = Geometry.Parse(string.Format(CultureInfo.InvariantCulture,
                "M2,2 L{0},2 L{1},{2} L{1},{3} L2,{3} Z", Paper.Width - ear, Paper.Width - 2, ear, Paper.Height - 2))
        });
        double[][] lines = { new[] { 62.0, 105.0 }, new[] { 125.0, 160.0 }, new[] { 185.0, 160.0 } };
        for (int i = 0; i < lines.Length; i++)
        {
            var l = new Rectangle { Width = lines[i][1], Height = 4, Fill = White, RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new ScaleTransform(0, 1) };
            Canvas.SetLeft(l, 30);
            Canvas.SetTop(l, lines[i][0] - 2);
            sheet.Children.Add(l);
            AnimatePath(sb, l, "RenderTransform.ScaleX", Keys(5.9 + 0.2 * i, 0, 6.15 + 0.2 * i, 1), new CubicEase { EasingMode = EasingMode.EaseOut });
        }

        Animate(sb, sheet, UIElement.OpacityProperty, Keys(4.6, 0, 4.7, 1));
        AnimatePath(sb, sheet, "RenderTransform.Children[1].Y", Keys(4.6, -900, 5.8, 0), new QuadraticEase { EasingMode = EasingMode.EaseOut });
        AnimatePath(sb, sheet, "RenderTransform.Children[1].X", Keys(4.6, -90, 4.9, 70, 5.2, -50, 5.5, 25, 5.8, 0), null);
        AnimatePath(sb, sheet, "RenderTransform.Children[0].Angle", Keys(4.6, -16, 4.9, 13, 5.2, -9, 5.5, 4, 5.8, 0), null);
    }

    // Caption positioned so the finished word is centred on centerX; typing then never shifts it.
    static TextBlock Caption(Canvas scene, string text, double centerX)
    {
        var tb = new TextBlock { FontFamily = new FontFamily("Arial Black, Arial"), FontWeight = FontWeights.Black, FontSize = 46, Foreground = White };
        var measure = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(tb.FontFamily, FontStyles.Normal, tb.FontWeight, FontStretches.Normal), tb.FontSize, White, 1.0);
        Canvas.SetLeft(tb, centerX - measure.Width / 2);
        Canvas.SetTop(tb, 768);
        scene.Children.Add(tb);
        return tb;
    }

    // Typewriter with a blinking cursor, as keyframes so it can be seeked for video rendering.
    static void TypeAnim(Storyboard sb, TextBlock tb, string text, double start, double until)
    {
        var a = new StringAnimationUsingKeyFrames();
        a.KeyFrames.Add(new DiscreteStringKeyFrame("", KeyTime.FromTimeSpan(TimeSpan.Zero)));
        int typed = 0;
        bool cursor = true;
        for (double t = start; t < until; t += 0.11, cursor = !cursor)
        {
            if (typed < text.Length) typed++;
            a.KeyFrames.Add(new DiscreteStringKeyFrame(text.Substring(0, typed) + (cursor ? "|" : ""), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        }
        a.KeyFrames.Add(new DiscreteStringKeyFrame(text, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(until))));
        Storyboard.SetTarget(a, tb);
        Storyboard.SetTargetProperty(a, new PropertyPath(TextBlock.TextProperty));
        sb.Children.Add(a);
    }

    // --- wallpaper: path, placement, fingerprint, accent colour ------------------------------------
    static string WallpaperPath()
    {
        string path = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", "") as string;
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
    }

    static BitmapSource LoadBitmap(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // Where the wallpaper lands on screen (in DIPs): Fit (6), Stretch (2), Center (0), else Fill/Span.
    static Rect WallpaperRect(BitmapSource bmp, double sw, double sh)
    {
        double iw = bmp.PixelWidth, ih = bmp.PixelHeight, s;
        string style = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallpaperStyle", "10") as string ?? "10";
        switch (style)
        {
            case "6": s = Math.Min(sw / iw, sh / ih); break;
            case "2": return new Rect(0, 0, sw, sh);
            case "0": s = 1; break;
            default: s = Math.Max(sw / iw, sh / ih); break;
        }
        return new Rect((sw - iw * s) / 2, (sh - ih * s) / 2, iw * s, ih * s);
    }

    // 64-bit average hash: 8x8 greyscale thumbnail, one bit per pixel above the mean. Stable across
    // re-encodes and resizes of the same picture, so it doubles as the animation seed.
    static ulong AHash(string path)
    {
        using (var src = Drawing.Image.FromFile(path))
        using (var small = new Drawing.Bitmap(8, 8))
        {
            using (var g = Drawing.Graphics.FromImage(small)) { g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, 8, 8); }
            var v = new int[64];
            int sum = 0;
            for (int i = 0; i < 64; i++) { Drawing.Color c = small.GetPixel(i % 8, i / 8); v[i] = (c.R * 299 + c.G * 587 + c.B * 114) / 1000; sum += v[i]; }
            ulong h = 0;
            for (int i = 0; i < 64; i++) if (v[i] > sum / 64) h |= 1UL << i;
            return h;
        }
    }

    static int Hamming(ulong a, ulong b) { ulong x = a ^ b; int n = 0; while (x != 0) { n++; x &= x - 1; } return n; }

    // The wallpaper's most saturated colour, brightened, for beams and flashes. Greyscale pictures get white.
    static Color Accent(string path)
    {
        using (var src = Drawing.Image.FromFile(path))
        using (var small = new Drawing.Bitmap(16, 16))
        {
            using (var g = Drawing.Graphics.FromImage(small)) { g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, 16, 16); }
            double r = 0, gg = 0, b = 0, wsum = 0;
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    Drawing.Color c = small.GetPixel(x, y);
                    double weight = Math.Pow(c.GetSaturation() * c.GetBrightness() * 2, 2);
                    r += c.R * weight; gg += c.G * weight; b += c.B * weight; wsum += weight;
                }
            if (wsum < 0.5) return Colors.White;
            var avg = Drawing.Color.FromArgb((int)(r / wsum), (int)(gg / wsum), (int)(b / wsum));
            return Hsv(avg.GetHue(), Math.Max(avg.GetSaturation(), 0.75), 1);
        }
    }

    static Color Hsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c, r, g, b;
        if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; } else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; } else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    // --- tiny storyboard helpers ------------------------------------------------------------------
    static DoubleAnimationUsingKeyFrames Keys(params double[] timeValuePairs)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        for (int i = 0; i < timeValuePairs.Length; i += 2)
            a.KeyFrames.Add(new LinearDoubleKeyFrame(timeValuePairs[i + 1], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(timeValuePairs[i]))));
        return a;
    }

    static DoubleAnimationUsingKeyFrames Steps(params double[] timeValuePairs)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        for (int i = 0; i < timeValuePairs.Length; i += 2)
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(timeValuePairs[i + 1], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(timeValuePairs[i]))));
        return a;
    }

    static void Animate(Storyboard sb, DependencyObject target, DependencyProperty prop, AnimationTimeline a)
    {
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, new PropertyPath(prop));
        sb.Children.Add(a);
    }

    static void AnimatePath(Storyboard sb, DependencyObject target, string path, DoubleAnimationUsingKeyFrames a, IEasingFunction ease)
    {
        if (ease != null)
        {
            var eased = new DoubleAnimationUsingKeyFrames();
            foreach (DoubleKeyFrame k in a.KeyFrames) eased.KeyFrames.Add(new EasingDoubleKeyFrame(k.Value, k.KeyTime, ease));
            a = eased;
        }
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, new PropertyPath(path));
        sb.Children.Add(a);
    }

    // Three two-tone klaxon beeps, synthesised as an in-memory WAV so there's no sound file to ship.
    static Stream AlarmWav()
    {
        const int rate = 22050;
        var samples = new List<short>();
        for (int beep = 0; beep < 3; beep++)
        {
            for (int n = 0; n < rate * 4 / 10; n++)
            {
                double freq = n < rate / 5 ? 880 : 660;
                samples.Add((short)((n * freq / rate) % 1 < 0.5 ? 2000 : -2000));
            }
            for (int n = 0; n < rate / 10; n++) samples.Add(0);
        }

        var ms = new MemoryStream();
        var wav = new BinaryWriter(ms);
        wav.Write(Encoding.ASCII.GetBytes("RIFF")); wav.Write(36 + samples.Count * 2);
        wav.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); wav.Write(16);
        wav.Write((short)1); wav.Write((short)1);            // PCM, mono
        wav.Write(rate); wav.Write(rate * 2);                 // sample rate, byte rate
        wav.Write((short)2); wav.Write((short)16);           // block align, bits per sample
        wav.Write(Encoding.ASCII.GetBytes("data")); wav.Write(samples.Count * 2);
        foreach (short s in samples) wav.Write(s);
        ms.Position = 0;
        return ms;
    }
}

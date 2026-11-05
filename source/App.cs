using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace QuietFocus
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
        [STAThread]
        public static int Main(string[] args)
        {
            bool uiTest = args.Length > 0 && args[0] == "--ui-test";
            bool acquired;
            using (var mutex = new Mutex(true, uiTest ? "QuietFocus-UITest" : "QuietFocus-Desktop-v1", out acquired))
            {
                if (!acquired)
                {
                    foreach (Process p in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
                        if (p.Id != Process.GetCurrentProcess().Id && p.MainWindowHandle != IntPtr.Zero)
                        { ShowWindow(p.MainWindowHandle, 9); SetForegroundWindow(p.MainWindowHandle); break; }
                    return 0;
                }
                try
                {
                    var application = new Application();
                    string dataPath = uiTest ? System.IO.Path.Combine(args[1], "ui-test-state.json") :
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietFocus", "state.json");
                    var controller = new Controller(dataPath, uiTest, args.Length > 1 ? args[1] : null);
                    application.Run(controller.Window);
                    return 0;
                }
                catch (Exception e)
                {
                    if (uiTest) { Console.Error.WriteLine(e); return 1; }
                    MessageBox.Show(e.Message, "Quiet Focus", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
            }
        }
    }

    internal sealed class Controller
    {
        public readonly Window Window;
        private readonly TimerEngine engine;
        private readonly StateStore store;
        private readonly DispatcherTimer refresh;
        private readonly Forms.NotifyIcon tray;
        private readonly bool testing;
        private bool allTime;
        private bool closing;
        private bool saveFailed;
        private double lastSave;
        private string status = "A 5-minute break starts automatically after focus.";

        private T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        [DllImport("kernel32.dll")]
        private static extern bool QueryUnbiasedInterruptTime(out ulong time);
        private static double Now
        {
            get
            {
                ulong time;
                if (!QueryUnbiasedInterruptTime(out time)) throw new InvalidOperationException("Windows could not read the timer clock.");
                return time / 10000.0;
            }
        }
        private static Brush Brush(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

        public Controller(string path, bool test, string testDirectory)
        {
            testing = test;
            store = new StateStore(path);
            engine = new TimerEngine(store.Load(), TimeZoneInfo.Local);
            if (engine.Recover(DateTimeOffset.UtcNow)) status = "Your previous timer was paused after the app stopped.";
            if (store.Notice != null) status = store.Notice;
            using (Stream xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuietFocus.MainWindow.xaml"))
                Window = (Window)XamlReader.Load(xaml);
            // WPF work-area dimensions are device-independent and exclude the taskbar.
            Window.Height = Math.Min(Window.Height, SystemParameters.WorkArea.Height - 16);
            Window.Width = Math.Min(Window.Width, SystemParameters.WorkArea.Width - 16);
            string icon = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QuietFocus.ico");
            if (File.Exists(icon)) Window.Icon = BitmapFrame.Create(new Uri(icon));
            Window.Topmost = engine.State.OnTop;
            Find<CheckBox>("PinBox").IsChecked = engine.State.OnTop;
            Find<CheckBox>("SoundBox").IsChecked = engine.State.Sound;
            Find<Button>("StartButton").Click += delegate { Toggle(); };
            Find<Button>("ResetButton").Click += delegate
            {
                engine.Reset(Now, DateTimeOffset.UtcNow);
                engine.Transitions.Clear();
                status = "Timer reset. Your accumulated time has been kept.";
                Save(); Paint();
            };
            Find<Button>("SkipButton").Click += delegate
            {
                engine.SkipBreak(Now, DateTimeOffset.UtcNow); engine.Transitions.Clear();
                status = "Ready for a fresh focus session."; Save(); Paint();
            };
            Find<Button>("TodayButton").Click += delegate { allTime = false; Paint(); };
            Find<Button>("AllButton").Click += delegate { allTime = true; Paint(); };
            Find<CheckBox>("PinBox").Click += delegate
            { Window.Topmost = engine.State.OnTop = Find<CheckBox>("PinBox").IsChecked == true; Save(); };
            Find<CheckBox>("SoundBox").Click += delegate
            { engine.State.Sound = Find<CheckBox>("SoundBox").IsChecked == true; Save(); };
            Find<Button>("ExportButton").Click += delegate { Export(); };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                // Native buttons/checkboxes keep their normal Space behaviour.
                if (e.Key == Key.Space && !e.IsRepeat && !(Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase))
                { Toggle(); e.Handled = true; }
            };
            tray = new Forms.NotifyIcon { Text = "Quiet Focus", Visible = !test };
            tray.Icon = File.Exists(icon) ? new System.Drawing.Icon(icon) : System.Drawing.SystemIcons.Application;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Quiet Focus", null, delegate { Show(); });
            menu.Items.Add("Exit", null, delegate { Window.Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Show(); };
            tray.BalloonTipClicked += delegate { Show(); };
            refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            refresh.Tick += delegate { Tick(); };
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                engine.Pause(Now, DateTimeOffset.UtcNow);
                if (!Save() && !test)
                {
                    // A real I/O error must not silently discard tracked work on exit.
                    e.Cancel = true; status = "Could not save. Check available disk space, then close again."; Paint(); return;
                }
                closing = true; refresh.Stop();
                SystemEvents.PowerModeChanged -= OnPower;
                tray.Visible = false; tray.Dispose();
            };
            SystemEvents.PowerModeChanged += OnPower;
            Save(); Paint();
            Window.Loaded += delegate
            {
                if (test) Window.Dispatcher.BeginInvoke(new Action(delegate { UiTest(testDirectory); }), DispatcherPriority.ApplicationIdle);
                else refresh.Start();
            };
        }
        private void Show() { Window.Show(); Window.WindowState = WindowState.Normal; Window.Activate(); }
        private void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            double captured = Now;
            DateTimeOffset utc = DateTimeOffset.UtcNow;
            if (closing) return;
            if (e.Mode == PowerModes.Suspend)
                Window.Dispatcher.Invoke(new Action(delegate
                {
                    engine.Pause(captured, utc);
                    status = "Paused for sleep. Resume when you are ready.";
                    Save(); Paint();
                }));
            else if (e.Mode == PowerModes.Resume)
                Window.Dispatcher.BeginInvoke(new Action(delegate
                {
                    // If suspend notification was missed, do not count sleeping hours.
                    if (engine.State.Running) engine.Recover(utc);
                    status = "Welcome back. Your timer is paused.";
                    Save(); Paint();
                }));
        }
        private void Toggle()
        {
            if (engine.State.Running)
            {
                engine.Pause(Now, DateTimeOffset.UtcNow);
                status = "Paused. Paused time is not included in your statistics.";
            }
            else
            {
                engine.Start(Now);
                status = engine.State.Phase == "Focus" ? "You have time. Give this one thing your attention." : "Step away, stretch, and breathe.";
            }
            NotifyTransitions(); Save(); Paint();
        }
        private void Tick()
        {
            engine.Advance(Now, DateTimeOffset.UtcNow);
            bool transitioned = engine.Transitions.Count > 0;
            NotifyTransitions();
            if (transitioned || (engine.State.Running && Now - lastSave >= 5000)) Save();
            Paint();
        }
        private void NotifyTransitions()
        {
            if (engine.Transitions.Count == 0) return;
            status = engine.Transitions[engine.Transitions.Count - 1];
            if (!testing)
            {
                if (engine.State.Sound) System.Media.SystemSounds.Asterisk.Play();
                tray.ShowBalloonTip(6000, "Quiet Focus", status, Forms.ToolTipIcon.Info);
            }
            engine.Transitions.Clear();
        }
        private bool Save()
        {
            try
            {
                store.Save(engine.State); lastSave = Now; saveFailed = false;
                return true;
            }
            catch (Exception e)
            {
                if (!(e is IOException || e is UnauthorizedAccessException)) throw;
                saveFailed = true;
                lastSave = Now;
                Find<TextBlock>("SaveText").ToolTip = e.Message;
                return false;
            }
        }
        private static string Time(double ms)
        {
            long s = (long)Math.Floor(ms / 1000);
            return s >= 3600 ? String.Format("{0}h {1:00}m {2:00}s", s / 3600, (s / 60) % 60, s % 60) :
                String.Format("{0}m {1:00}s", s / 60, s % 60);
        }
        private void Paint()
        {
            var s = engine.State;
            bool focus = s.Phase == "Focus";
            int seconds = (int)Math.Ceiling(s.RemainingMs / 1000);
            string time = String.Format("{0:00}:{1:00}", seconds / 60, seconds % 60);
            Find<TextBlock>("ClockText").Text = time;
            Find<TextBlock>("PhaseLabel").Text = focus ? "FOCUS SESSION" : "SHORT BREAK";
            Find<TextBlock>("TimerHint").Text = s.Running ? (focus ? "One thing at a time" : "A moment to recharge") :
                s.RemainingMs < engine.Duration ? "Paused — your time is safe" : focus ? "25 minutes, just for focus" : "5 minutes to recharge";
            Find<Button>("StartButton").Content = s.Running ? "Pause" :
                (s.RemainingMs < engine.Duration ? "Resume " : "Start ") + (focus ? "focus" : "break");
            Find<Button>("SkipButton").Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
            Find<Button>("ResetButton").IsEnabled = s.Running || s.RemainingMs < engine.Duration;
            double progress = Math.Min(.999999, Math.Max(0, 1 - s.RemainingMs / engine.Duration));
            var arc = Find<System.Windows.Shapes.Path>("ProgressArc");
            arc.Stroke = Brush(focus ? "#79B9A6" : "#90ACD2");
            if (progress < .00001) arc.Data = null;
            else
            {
                double angle = progress * Math.PI * 2;
                var figure = new PathFigure { StartPoint = new Point(106, 8.5), IsClosed = false };
                figure.Segments.Add(new ArcSegment(new Point(106 + 97.5 * Math.Sin(angle), 106 - 97.5 * Math.Cos(angle)),
                    new Size(97.5, 97.5), 0, progress > .5, SweepDirection.Clockwise, true));
                var geometry = new PathGeometry(); geometry.Figures.Add(figure); arc.Data = geometry;
            }
            DayStats totals = engine.Totals(allTime, DateTimeOffset.UtcNow);
            Find<TextBlock>("FocusValue").Text = Time(totals.FocusMs);
            Find<TextBlock>("BreakValue").Text = Time(totals.BreakMs);
            Find<TextBlock>("PercentValue").Text = totals.BreakPercent.ToString("0.0", CultureInfo.CurrentCulture) + "%";
            Find<TextBlock>("InterruptValue").Text = totals.Interruptions.ToString();
            Find<TextBlock>("CompleteValue").Text = totals.Completed.ToString();
            Find<Button>("TodayButton").Background = Brush(allTime ? "#F6F7F9" : "#E1E9EF");
            Find<Button>("AllButton").Background = Brush(allTime ? "#E1E9EF" : "#F6F7F9");
            Find<TextBlock>("StatusText").Text = status;
            Find<TextBlock>("SaveText").Text = saveFailed ? "Not saved — check disk access before exiting" : "Saved on this PC  ·  Space to start / pause";
            Find<TextBlock>("SaveText").Foreground = Brush(saveFailed ? "#AD493B" : "#87929F");
            Window.Title = s.Running ? time + " · " + s.Phase + " — Quiet Focus" : "Quiet Focus";
            tray.Text = "Quiet Focus · " + s.Phase + " " + time;
        }
        private void Export()
        {
            engine.Advance(Now, DateTimeOffset.UtcNow); NotifyTransitions(); Save(); Paint();
            var dialog = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = "QuietFocus-stats.csv" };
            if (dialog.ShowDialog(Window) != true) return;
            try
            {
                var text = new StringBuilder("Date,FocusSeconds,BreakSeconds,BreakPercent,CompletedPomodoros,Interruptions\r\n");
                foreach (DayStats d in engine.State.Days.OrderBy(x => x.Date))
                    text.AppendFormat(CultureInfo.InvariantCulture, "{0},{1:0.###},{2:0.###},{3:0.00},{4},{5}\r\n",
                        d.Date, d.FocusMs / 1000, d.BreakMs / 1000, d.BreakPercent, d.Completed, d.Interruptions);
                File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(true));
                status = "Statistics exported."; Paint();
            }
            catch (IOException e) { MessageBox.Show(Window, e.Message, "Could not export"); }
            catch (UnauthorizedAccessException e) { MessageBox.Show(Window, e.Message, "Could not export"); }
        }
        private void Snapshot(string filename)
        {
            Window.UpdateLayout();
            var target = new RenderTargetBitmap((int)Window.ActualWidth, (int)Window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            target.Render(Window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(target));
            using (Stream file = File.Create(filename)) png.Save(file);
        }
        private void UiTest(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                if (Find<TextBlock>("ClockText").Text != "25:00") throw new Exception("Initial timer incorrect.");
                Snapshot(System.IO.Path.Combine(folder, "ready.png"));
                Find<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!engine.State.Running) throw new Exception("Start button did not start.");
                Find<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (engine.State.Running || engine.Totals(true, DateTimeOffset.UtcNow).Interruptions != 1)
                    throw new Exception("Pause button did not count one interruption.");
                engine.Reset(Now, DateTimeOffset.UtcNow);
                engine.Start(0);
                engine.Advance(1500000, DateTimeOffset.UtcNow);
                NotifyTransitions(); Paint();
                if (Find<TextBlock>("ClockText").Text != "05:00" || engine.State.Phase != "Break" || !engine.State.Running)
                    throw new Exception("Automatic break UI incorrect.");
                Snapshot(System.IO.Path.Combine(folder, "break.png"));
                engine.Advance(1800000, DateTimeOffset.UtcNow.AddMinutes(5));
                NotifyTransitions(); Paint();
                if (engine.State.Running || Find<TextBlock>("ClockText").Text != "25:00") throw new Exception("Break must stop at next focus.");
                Find<Button>("AllButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!allTime) throw new Exception("All-time button failed.");
                Save(); Snapshot(System.IO.Path.Combine(folder, "completed.png"));
                File.WriteAllText(System.IO.Path.Combine(folder, "ui-test-result.txt"), "PASS: initial UI, Start/Pause, interruption count, automatic break, break completion, All time, persistence.");
                Window.Close();
            }
            catch (Exception e)
            {
                File.WriteAllText(System.IO.Path.Combine(folder, "ui-test-result.txt"), "FAIL: " + e);
                Environment.Exit(1);
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace VideoCut
{
    internal static class Smoke
    {
        [STAThread]
        private static int Main()
        {
            string ffmpeg = File.ReadAllText("ffmpeg.path").Trim();
            string folder = Path.Combine(Path.GetTempPath(), "VideoCutSmoke_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string source = Path.Combine(folder, "source.mp4");
            string input = "-hide_banner -loglevel error -f lavfi -i testsrc2=duration=6:size=320x180:rate=25 " +
                "-f lavfi -i sine=frequency=880:duration=6 -shortest -c:v libx264 -pix_fmt yuv420p " +
                "-c:a aac -y " + Exporter.Quote(source);
            Run(ffmpeg, input);
            var clip = new Clip { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(3) };
            foreach (bool reencode in new[] { false, true })
            {
                string target = Exporter.UniqueTarget(folder, source, reencode ? 2 : 1, reencode);
                Run(ffmpeg, Exporter.Arguments(source, target, clip, reencode));
                if (!File.Exists(target) || new FileInfo(target).Length == 0)
                    throw new Exception("Empty output: " + target);
                Run(ffmpeg, "-hide_banner -loglevel error -i " + Exporter.Quote(target) + " -f null NUL");
                Console.WriteLine((reencode ? "Reencode" : "Stream copy") + " OK: " + target);
            }
            TestUi(source);
            return 0;
        }

        private static void TestUi(string source)
        {
            var app = new Application();
            var window = new MainWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(MainWindow);
            string failure = null;
            int checks = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            window.Loaded += delegate
            {
                type.GetMethod("OpenVideo", flags).Invoke(window, new object[] { source });
                timer.Start();
            };
            timer.Tick += delegate
            {
                checks++;
                var duration = (TimeSpan)type.GetField("duration", flags).GetValue(window);
                if (duration == TimeSpan.Zero && checks < 100) return;
                timer.Stop();
                try
                {
                    if (duration == TimeSpan.Zero) throw new Exception("Media preview did not open");
                    var player = (MediaElement)type.GetField("player", flags).GetValue(window);
                    var startButton = (Button)type.GetField("markStartButton", flags).GetValue(window);
                    var endButton = (Button)type.GetField("markEndButton", flags).GetValue(window);
                    var undoButton = (Button)type.GetField("undoButton", flags).GetValue(window);
                    player.Position = TimeSpan.FromSeconds(1);
                    type.GetMethod("OnClock", flags).Invoke(window, new object[] { null, EventArgs.Empty });
                    startButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, startButton));
                    if (type.GetField("startMark", flags).GetValue(window) == null)
                        throw new Exception("Start button did not set a mark");
                    player.Position = TimeSpan.FromSeconds(3);
                    type.GetMethod("OnClock", flags).Invoke(window, new object[] { null, EventArgs.Empty });
                    endButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, endButton));
                    var clips = (System.Collections.ICollection)type.GetField("clips", flags).GetValue(window);
                    if (clips.Count != 1) throw new Exception("Start/end buttons did not add a clip");
                    undoButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, undoButton));
                    if (clips.Count != 0 || type.GetField("startMark", flags).GetValue(window) == null)
                        throw new Exception("First C did not undo end");
                    undoButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, undoButton));
                    if (type.GetField("startMark", flags).GetValue(window) != null)
                        throw new Exception("Second C did not undo start");
                    var startInput = (TextBox)type.GetField("startInput", flags).GetValue(window);
                    var endInput = (TextBox)type.GetField("endInput", flags).GetValue(window);
                    startInput.Text = "00:00:01.500";
                    startButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, startButton));
                    endInput.Text = "00:00:02.750";
                    endButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, endButton));
                    var manualClip = (Clip)((System.Collections.IList)clips)[0];
                    if (manualClip.Start != TimeSpan.FromMilliseconds(1500) ||
                        manualClip.End != TimeSpan.FromMilliseconds(2750))
                        throw new Exception("Manual time inputs were not used");
                    type.GetField("duration", flags).SetValue(window, TimeSpan.Zero);
                    startInput.Text = "00:00:03.000";
                    RaiseKey(window, Key.Z);
                    if (type.GetField("startMark", flags).GetValue(window) == null)
                        throw new Exception("Z shortcut did not set a mark without duration metadata");
                    endInput.Text = "00:00:04.000";
                    RaiseKey(window, Key.X);
                    if (clips.Count != 2) throw new Exception("X shortcut did not add a clip");
                    RaiseKey(window, Key.C);
                    if (clips.Count != 1 || type.GetField("startMark", flags).GetValue(window) == null)
                        throw new Exception("C shortcut did not undo end");
                    RaiseKey(window, Key.C);
                    if (type.GetField("startMark", flags).GetValue(window) != null)
                        throw new Exception("C shortcut did not undo start");
                }
                catch (Exception ex) { failure = ex.ToString(); }
                app.Shutdown();
            };
            app.Run(window);
            if (failure != null) throw new Exception(failure);
            Console.WriteLine("Preview, Z/X/C buttons and shortcuts, and manual time input OK");
        }

        private static void RaiseKey(Window window, Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(args);
        }

        private static void Run(string executable, string arguments)
        {
            var info = new ProcessStartInfo(executable, arguments) { UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(info))
            {
                process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new Exception(error);
            }
        }
    }
}

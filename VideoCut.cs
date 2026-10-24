using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace VideoCut
{
    internal sealed class Clip
    {
        public Guid Id = Guid.NewGuid();
        public TimeSpan Start;
        public TimeSpan End;
    }

    internal sealed class MarkAction
    {
        public TimeSpan? PreviousStart;
        public TimeSpan? PreviousEnd;
        public TimeSpan? PreviousClipEnd;
        public Guid CreatedClipId;
        public Guid EditedClipId;
        public bool IsStartChange;
    }

    internal static class Exporter
    {
        public static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        public static string Arguments(string source, string target, Clip clip, bool reencode)
        {
            string start = clip.Start.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
            string length = (clip.End - clip.Start).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
            string common = "-hide_banner -nostdin -loglevel error -progress pipe:1 -ss " + start +
                " -i " + Quote(source) + " -t " + length + " ";
            if (reencode)
                return common + "-map 0:v:0 -map 0:a? -c:v libx264 -preset medium -crf 18 " +
                    "-c:a aac -b:a 192k -movflags +faststart -n " + Quote(target);
            return common + "-map 0:v:0 -map 0:a? -c copy -avoid_negative_ts make_zero -n " + Quote(target);
        }

        public static string ConcatArguments(string listFile, string target, bool reencode = false)
        {
            string codec = reencode ? "-c:v libx264 -preset medium -crf 18 -c:a aac -b:a 192k -movflags +faststart" : "-c copy";
            return "-hide_banner -nostdin -loglevel error -progress pipe:1 -f concat -safe 0 -i " +
                Quote(listFile) + " -map 0:v:0 -map 0:a? " + codec + " -n " + Quote(target);
        }

        public static string UniqueTarget(string folder, string source, int number, bool reencode)
        {
            string baseName = Path.GetFileNameWithoutExtension(source) + "_片段_" + number.ToString("000");
            string extension = reencode ? ".mp4" : Path.GetExtension(source);
            if (String.IsNullOrEmpty(extension)) extension = ".mkv";
            string path = Path.Combine(folder, baseName + extension);
            int suffix = 2;
            while (File.Exists(path) || File.Exists(PartialPath(path)))
            {
                path = Path.Combine(folder, baseName + "_" + suffix + extension);
                suffix++;
            }
            return path;
        }

        public static string UniqueMergedTarget(string folder, string source, bool reencode)
        {
            string extension = reencode ? ".mp4" : Path.GetExtension(source);
            if (String.IsNullOrEmpty(extension)) extension = ".mkv";
            string baseName = Path.GetFileNameWithoutExtension(source) + "_合并";
            string path = Path.Combine(folder, baseName + extension);
            int suffix = 2;
            while (File.Exists(path) || File.Exists(PartialPath(path)))
            {
                path = Path.Combine(folder, baseName + "_" + suffix + extension);
                suffix++;
            }
            return path;
        }

        public static string PartialPath(string target)
        {
            return Path.Combine(Path.GetDirectoryName(target),
                Path.GetFileNameWithoutExtension(target) + ".partial" + Path.GetExtension(target));
        }
    }

    internal sealed class MainWindow : Window
    {
        private readonly List<Clip> clips = new List<Clip>();
        private readonly List<string> mergeVideos = new List<string>();
        private readonly Stack<MarkAction> markHistory = new Stack<MarkAction>();
        private readonly DispatcherTimer clock = new DispatcherTimer();
        private MediaElement player;
        private Slider timeline;
        private TextBlock currentTime;
        private TextBlock placeholder;
        private TextBlock startLabel;
        private TextBlock endLabel;
        private TextBox startInput;
        private TextBox endInput;
        private TextBlock countLabel;
        private TextBlock statusLabel;
        private TextBlock folderLabel;
        private TextBlock ffmpegLabel;
        private StackPanel clipRows;
        private Button playButton;
        private Button markStartButton;
        private Button markEndButton;
        private Button undoButton;
        private Button exportButton;
        private Button mergeButton;
        private Button cancelButton;
        private ComboBox speedBox;
        private ComboBox modeBox;
        private ProgressBar progress;
        private RowDefinition clipListArea;
        private double expandedClipArea;
        private string sourcePath;
        private string outputFolder;
        private string ffmpegPath;
        private int preferredSpeedIndex = 2;
        private int preferredModeIndex;
        private TimeSpan duration;
        private TimeSpan? startMark;
        private TimeSpan? endMark;
        private Guid? editingClipId;
        private bool isPlaying;
        private bool isSeeking;
        private bool settingSlider;
        private bool exporting;
        private bool updatingTimeInputs;
        private bool startInputEdited;
        private bool endInputEdited;
        private CancellationTokenSource cancellation;
        private Process runningProcess;

        private static SolidColorBrush Brush(string hex)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFrom(hex);
        }

        private static TextBlock Text(string value, double size, string color, FontWeight weight)
        {
            return new TextBlock { Text = value, FontSize = size, Foreground = Brush(color),
                FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };
        }

        private static Border Card(UIElement child, string background, double radius)
        {
            return new Border { Background = Brush(background), BorderBrush = Brush("#E2E8F1"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(radius),
                Child = child, SnapsToDevicePixels = true };
        }

        private static Button Button(string value, string background, string foreground, double width)
        {
            var button = new Button { Content = value, Width = width, Height = 38,
                Background = Brush(background), Foreground = Brush(foreground),
                BorderThickness = new Thickness(0), FontSize = 13, FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand, Padding = new Thickness(12, 0, 12, 0) };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            button.MouseEnter += delegate { button.Opacity = 0.82; };
            button.MouseLeave += delegate { button.Opacity = 1; };
            return button;
        }

        private static ComboBox Combo(double width)
        {
            return new ComboBox { Width = width, Height = 36, FontSize = 13,
                Foreground = Brush("#243044"), Background = Brushes.White,
                BorderBrush = Brush("#DDE3EB"), BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(7, 0, 4, 0) };
        }

        private static TextBox TimeInput()
        {
            var input = new TextBox { Width = 112, Height = 36, Text = "00:00:00.000",
                FontSize = 12, Foreground = Brush("#334155"), Background = Brush("#F6F8FB"),
                BorderBrush = Brush("#DDE3EB"), BorderThickness = new Thickness(1),
                TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "输入时间，例如 00:01:23.500；留在默认值时取当前播放位置" };
            var outline = new FrameworkElementFactory(typeof(Border));
            outline.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            outline.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            outline.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            outline.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            var host = new FrameworkElementFactory(typeof(ScrollViewer));
            host.Name = "PART_ContentHost";
            outline.AppendChild(host);
            input.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = outline };
            return input;
        }

        private static void Shadow(Border border)
        {
            border.Effect = new DropShadowEffect { Color = Color.FromRgb(45, 62, 89), BlurRadius = 22,
                ShadowDepth = 4, Opacity = 0.075 };
        }

        public MainWindow()
        {
            Title = "片刻 · VideoCut";
            Width = 1200;
            Height = 900;
            MinWidth = 1100;
            MinHeight = 800;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 46;
            Top = 34;
            Background = Brush("#F2F5FA");
            FontFamily = new FontFamily("Segoe UI");
            AllowDrop = true;
            Drop += OnDrop;
            PreviewKeyDown += OnKeyDown;
            LoadPreferences();
            Closing += delegate { SavePreferences(); CancelExport(); };
            BuildInterface();
            clock.Interval = TimeSpan.FromMilliseconds(100);
            clock.Tick += OnClock;
            clock.Start();
            ffmpegPath = FindFfmpeg();
            UpdateFfmpegLabel();
            SetStatus("打开视频后，可用 Z / X 标记片段。", false);
        }

        private void BuildInterface()
        {
            var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            Content = root;

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var logoText = Text("✂", 23, "#FFFFFF", FontWeights.Medium);
            var logo = Card(logoText, "#4B83F5", 15);
            logo.Width = logo.Height = 48;
            logoText.HorizontalAlignment = HorizontalAlignment.Center;
            logoText.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(logo);
            var titles = new StackPanel { Margin = new Thickness(14, 2, 0, 0) };
            titles.Children.Add(Text("片刻", 21, "#1C2737", FontWeights.Bold));
            titles.Children.Add(Text("轻巧的视频裁剪工具", 12, "#8793A4", FontWeights.Normal));
            Grid.SetColumn(titles, 1);
            header.Children.Add(titles);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(112) });
            clipListArea = new RowDefinition { Height = new GridLength(240) };
            body.RowDefinitions.Add(clipListArea);
            Grid.SetRow(body, 1);
            root.Children.Add(body);
            BuildPlayer(body);
            BuildSidebar(body);

            var footer = new Grid { Margin = new Thickness(2, 7, 2, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusLabel = Text("", 12, "#8A96A6", FontWeights.Normal);
            footer.Children.Add(statusLabel);
            var version = Text("VideoCut  ·  Windows", 11, "#A5AFBC", FontWeights.Normal);
            Grid.SetColumn(version, 1);
            footer.Children.Add(version);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
        }

        private void BuildPlayer(Grid body)
        {
            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(112) });
            Grid.SetRowSpan(left, 2);
            body.Children.Add(left);

            var videoGrid = new Grid { Background = Brush("#111825") };
            player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Stop,
                Stretch = Stretch.Uniform, ScrubbingEnabled = true };
            player.MediaOpened += OnMediaOpened;
            player.MediaEnded += delegate { Pause(); player.Position = TimeSpan.Zero; };
            player.MediaFailed += delegate(object sender, ExceptionRoutedEventArgs e)
            {
                SetStatus("无法预览该视频：" + e.ErrorException.Message, true);
                placeholder.Visibility = Visibility.Visible;
                placeholder.Text = "Windows 无法预览此视频编码。";
            };
            videoGrid.Children.Add(player);
            placeholder = Text("把一个或多个视频拖到这里，或点击下方“打开视频”", 16, "#D2D9E5", FontWeights.Medium);
            placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            placeholder.TextAlignment = TextAlignment.Center;
            videoGrid.Children.Add(placeholder);
            var videoCard = Card(videoGrid, "#111825", 20);
            videoCard.ClipToBounds = true;
            videoGrid.SizeChanged += delegate
            {
                videoGrid.Clip = new RectangleGeometry(new Rect(0, 0,
                    videoGrid.ActualWidth, videoGrid.ActualHeight), 20, 20);
            };
            Shadow(videoCard);
            Grid.SetRow(videoCard, 0);
            left.Children.Add(videoCard);

            var controls = new Grid { Margin = new Thickness(12, 7, 12, 5) };
            controls.RowDefinitions.Add(new RowDefinition { Height = new GridLength(49) });
            controls.RowDefinitions.Add(new RowDefinition { Height = new GridLength(45) });
            var timelineRow = new Grid();
            timeline = new Slider { Minimum = 0, Maximum = 1, Value = 0,
                IsEnabled = false, IsMoveToPointEnabled = true, Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center, Height = 25 };
            timeline.ValueChanged += OnTimelineChanged;
            timeline.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(delegate { isSeeking = true; }));
            timeline.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(delegate { SeekToSlider(); isSeeking = false; }));
            timeline.PreviewMouseLeftButtonUp += delegate { if (isSeeking) { SeekToSlider(); isSeeking = false; } };
            timelineRow.Children.Add(timeline);
            controls.Children.Add(timelineRow);

            var actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var back = Button("↶ 10 秒", "#F0F3F8", "#576478", 78);
            back.Click += delegate { Jump(-10); };
            actions.Children.Add(back);
            playButton = Button("▶  播放", "#4B83F5", "#FFFFFF", 91);
            playButton.Margin = new Thickness(8, 0, 0, 0);
            playButton.Click += delegate { TogglePlay(); };
            Grid.SetColumn(playButton, 1);
            actions.Children.Add(playButton);
            var forward = Button("10 秒 ↷", "#F0F3F8", "#576478", 78);
            forward.Margin = new Thickness(8, 0, 0, 0);
            forward.Click += delegate { Jump(10); };
            Grid.SetColumn(forward, 2);
            actions.Children.Add(forward);
            var marks = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            startInput = TimeInput();
            startInput.TextChanged += delegate { if (!updatingTimeInputs) startInputEdited = true; };
            startInput.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { MarkStart(); e.Handled = true; }
            };
            marks.Children.Add(startInput);
            markStartButton = Button("Z 起点", "#F2C94C", "#4B3D12", 83);
            markStartButton.Margin = new Thickness(5, 0, 0, 0);
            markStartButton.Click += delegate { MarkStart(); };
            marks.Children.Add(markStartButton);
            currentTime = Text("00:00:00.000", 12, "#4B83F5", FontWeights.SemiBold);
            currentTime.Width = 96;
            currentTime.TextAlignment = TextAlignment.Center;
            currentTime.Margin = new Thickness(9, 0, 9, 0);
            marks.Children.Add(currentTime);
            endInput = TimeInput();
            endInput.Margin = new Thickness(13, 0, 0, 0);
            endInput.TextChanged += delegate { if (!updatingTimeInputs) endInputEdited = true; };
            endInput.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { MarkEnd(); e.Handled = true; }
            };
            marks.Children.Add(endInput);
            markEndButton = Button("X 终点", "#35A875", "#FFFFFF", 83);
            markEndButton.Margin = new Thickness(5, 0, 0, 0);
            markEndButton.Click += delegate { MarkEnd(); };
            marks.Children.Add(markEndButton);
            undoButton = Button("C 撤销", "#E45A66", "#FFFFFF", 65);
            undoButton.Margin = new Thickness(13, 0, 0, 0);
            undoButton.Click += delegate { UndoMark(); };
            marks.Children.Add(undoButton);
            Grid.SetColumn(marks, 3);
            actions.Children.Add(marks);
            speedBox = Combo(92);
            speedBox.Items.Add("0.5×");
            speedBox.Items.Add("0.75×");
            speedBox.Items.Add("1×");
            speedBox.Items.Add("1.25×");
            speedBox.Items.Add("1.5×");
            speedBox.Items.Add("2×");
            speedBox.Items.Add("3×");
            speedBox.SelectedIndex = Math.Max(0, Math.Min(6, preferredSpeedIndex));
            speedBox.SelectionChanged += delegate
            {
                preferredSpeedIndex = speedBox.SelectedIndex;
                ApplySpeed();
                SavePreferences();
            };
            Grid.SetColumn(speedBox, 4);
            actions.Children.Add(speedBox);
            var open = Button("＋  打开视频", "#4B83F5", "#FFFFFF", 132);
            open.Margin = new Thickness(9, 0, 0, 0);
            open.Click += delegate { OpenVideoDialog(); };
            Grid.SetColumn(open, 5);
            actions.Children.Add(open);
            Grid.SetRow(actions, 1);
            controls.Children.Add(actions);
            var controlCard = Card(controls, "#EAF1FF", 18);
            Shadow(controlCard);
            Grid.SetRow(controlCard, 1);
            left.Children.Add(controlCard);
        }

        private void BuildSidebar(Grid body)
        {
            var lower = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(lower, 2);
            body.Children.Add(lower);
            var listPanel = new Grid();
            listPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(47) });
            listPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var listCard = Card(listPanel, "#F0F0FA", 18);
            Shadow(listCard);
            lower.Children.Add(listCard);

            var header = new Grid { Margin = new Thickness(19, 14, 19, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(Text("裁剪片段", 17, "#1F2A3A", FontWeights.Bold));
            var markSummary = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center };
            startLabel = Text("起点  —", 11, "#8390A2", FontWeights.Normal);
            endLabel = Text("终点  —", 11, "#8390A2", FontWeights.Normal);
            endLabel.Margin = new Thickness(16, 0, 0, 0);
            markSummary.Children.Add(startLabel);
            markSummary.Children.Add(endLabel);
            Grid.SetColumn(markSummary, 1);
            header.Children.Add(markSummary);
            countLabel = Text("0 条", 12, "#4B83F5", FontWeights.SemiBold);
            Grid.SetColumn(countLabel, 2);
            header.Children.Add(countLabel);
            listPanel.Children.Add(header);

            var list = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(15, 6, 15, 6) };
            clipRows = new StackPanel();
            list.Content = clipRows;
            Grid.SetRow(list, 1);
            listPanel.Children.Add(list);
            RenderClips();

            var bottom = new StackPanel { Margin = new Thickness(17, 13, 17, 13) };
            var outputCard = Card(bottom, "#EDF7F2", 18);
            Shadow(outputCard);
            Grid.SetColumn(outputCard, 2);
            lower.Children.Add(outputCard);
            var modeRow = new Grid();
            modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            modeRow.Children.Add(Text("批量转换", 16, "#1F2A3A", FontWeights.Bold));
            modeBox = Combo(139);
            modeBox.Items.Add("原视频流复制");
            modeBox.Items.Add("重新编码 · H.264");
            modeBox.SelectedIndex = Math.Max(0, Math.Min(1, preferredModeIndex));
            modeBox.SelectionChanged += delegate
            {
                preferredModeIndex = modeBox.SelectedIndex;
                SavePreferences();
            };
            Grid.SetColumn(modeBox, 1);
            modeRow.Children.Add(modeBox);
            bottom.Children.Add(modeRow);
            var folderRow = new Grid { Margin = new Thickness(0, 9, 0, 0) };
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var folderButton = Button("选择输出文件夹", "#E3EFE9", "#456354", Double.NaN);
            folderButton.Click += delegate { ChooseFolder(); };
            folderRow.Children.Add(folderButton);
            folderLabel = Text(String.IsNullOrWhiteSpace(outputFolder)
                ? "默认：源视频旁的“裁剪输出”文件夹" : outputFolder,
                11, "#9AA6B6", FontWeights.Normal);
            if (!String.IsNullOrWhiteSpace(outputFolder)) folderLabel.ToolTip = outputFolder;
            folderLabel.Margin = new Thickness(10, 0, 0, 0);
            folderLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(folderLabel, 1);
            folderRow.Children.Add(folderLabel);
            bottom.Children.Add(folderRow);
            var ffmpegRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            ffmpegRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
            ffmpegRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var ffmpegButton = Button("设置 FFmpeg", "#E3EFE9", "#456354", Double.NaN);
            ffmpegButton.Click += delegate { ChooseFfmpeg(); };
            ffmpegRow.Children.Add(ffmpegButton);
            ffmpegLabel = Text("", 11, "#9AA6B6", FontWeights.Normal);
            ffmpegLabel.Margin = new Thickness(10, 0, 0, 0);
            ffmpegLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(ffmpegLabel, 1);
            ffmpegRow.Children.Add(ffmpegLabel);
            bottom.Children.Add(ffmpegRow);
            progress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = 0,
                Foreground = Brush("#3B8D6B"), Background = Brush("#DDECE4"),
                Margin = new Thickness(0, 9, 0, 0) };
            bottom.Children.Add(progress);
            var exportRow = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            exportRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            exportRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            exportRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            exportButton = Button("转换", "#4B83F5", "#FFFFFF", Double.NaN);
            exportButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            exportButton.Margin = new Thickness(0, 0, 8, 0);
            exportButton.Click += async delegate { await ExportAll(false); };
            exportRow.Children.Add(exportButton);
            mergeButton = Button("合并转换", "#355FAE", "#FFFFFF", Double.NaN);
            mergeButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            mergeButton.Margin = new Thickness(0, 0, 8, 0);
            mergeButton.IsEnabled = false;
            mergeButton.Opacity = 0.56;
            mergeButton.ToolTip = "至少添加两条裁剪记录后可用";
            mergeButton.Click += async delegate
            {
                if (mergeVideos.Count > 1) await MergeVideos();
                else await ExportAll(true);
            };
            Grid.SetColumn(mergeButton, 1);
            exportRow.Children.Add(mergeButton);
            cancelButton = Button("取消", "#FEEAEC", "#C64F5D", 65);
            cancelButton.Margin = new Thickness(8, 0, 0, 0);
            cancelButton.Visibility = Visibility.Collapsed;
            cancelButton.Click += delegate { CancelExport(); };
            Grid.SetColumn(cancelButton, 2);
            exportRow.Children.Add(cancelButton);
            bottom.Children.Add(exportRow);
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 1) OpenVideos(files);
                else if (files.Length == 1) OpenVideo(files[0]);
            }
        }

        private void OpenVideoDialog()
        {
            var dialog = new OpenFileDialog { Title = "打开视频", Filter =
                "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.m4v;*.ts|所有文件|*.*",
                Multiselect = true };
            if (dialog.ShowDialog(this) == true)
            {
                if (dialog.FileNames.Length > 1) OpenVideos(dialog.FileNames);
                else if (dialog.FileNames.Length == 1) OpenVideo(dialog.FileNames[0]);
            }
        }

        private void OpenVideo(string path)
        {
            mergeVideos.Clear();
            LoadVideo(path);
        }

        private void OpenVideos(IEnumerable<string> paths)
        {
            if (exporting) { SetStatus("转换时不能更换视频。", true); return; }
            var validPaths = paths.Where(path => !String.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (validPaths.Count == 0) { SetStatus("没有找到可打开的视频文件。", true); return; }
            mergeVideos.Clear();
            mergeVideos.AddRange(validPaths);
            LoadVideo(mergeVideos[0]);
            SetStatus("已载入 " + mergeVideos.Count + " 个视频，按列表顺序合并。", false);
        }

        private void LoadVideo(string path)
        {
            if (exporting) { SetStatus("转换时不能更换视频。", true); return; }
            if (!File.Exists(path)) { SetStatus("文件不存在。", true); return; }
            Pause();
            sourcePath = path;
            clips.Clear();
            editingClipId = null;
            markHistory.Clear();
            startMark = endMark = null;
            startInputEdited = endInputEdited = false;
            duration = TimeSpan.Zero;
            timeline.IsEnabled = false;
            timeline.Maximum = 1;
            timeline.Value = 0;
            timeline.ToolTip = "总时长读取中";
            currentTime.Text = "00:00:00.000";
            placeholder.Text = "正在打开 " + Path.GetFileName(path) + "…";
            placeholder.Visibility = Visibility.Visible;
            player.Source = new Uri(path, UriKind.Absolute);
            player.Position = TimeSpan.Zero;
            RenderClips();
            UpdateMarks();
            SyncTimeInputs();
            SetStatus("已打开 " + Path.GetFileName(path), false);
        }

        private void OnMediaOpened(object sender, RoutedEventArgs e)
        {
            if (player.NaturalDuration.HasTimeSpan)
            {
                duration = player.NaturalDuration.TimeSpan;
                timeline.Maximum = duration.TotalMilliseconds;
                timeline.IsEnabled = true;
                timeline.ToolTip = "总时长 " + Format(duration);
                placeholder.Visibility = Visibility.Collapsed;
                SetStatus("视频已就绪 · " + Path.GetFileName(sourcePath), false);
            }
            else SetStatus("无法读取视频时长。", true);
            ApplySpeed();
            SyncTimeInputs();
        }

        private void OnClock(object sender, EventArgs e)
        {
            if (sourcePath == null || !player.NaturalDuration.HasTimeSpan || isSeeking) return;
            var position = player.Position;
            currentTime.Text = Format(position);
            settingSlider = true;
            timeline.Value = Math.Max(0, Math.Min(timeline.Maximum, position.TotalMilliseconds));
            settingSlider = false;
            SyncTimeInputs();
        }

        private void OnTimelineChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (settingSlider || !timeline.IsEnabled) return;
            currentTime.Text = Format(TimeSpan.FromMilliseconds(timeline.Value));
            if (!isSeeking) SeekToSlider();
        }

        private void SeekToSlider()
        {
            if (sourcePath == null || !timeline.IsEnabled) return;
            player.Position = TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(duration.TotalMilliseconds, timeline.Value)));
        }

        private void ApplySpeed()
        {
            if (player == null || speedBox == null || speedBox.SelectedItem == null) return;
            string value = speedBox.SelectedItem.ToString().Replace("×", "");
            double ratio;
            if (Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio))
                player.SpeedRatio = ratio;
        }

        private void TogglePlay()
        {
            if (sourcePath == null) { SetStatus("请先打开视频。", true); return; }
            if (isPlaying) Pause();
            else { player.Play(); isPlaying = true; playButton.Content = "Ⅱ  暂停"; }
        }

        private void Pause()
        {
            if (player != null) player.Pause();
            isPlaying = false;
            if (playButton != null) playButton.Content = "▶  播放";
        }

        private void Jump(int seconds)
        {
            if (sourcePath == null || duration == TimeSpan.Zero) return;
            double target = Math.Max(0, Math.Min(duration.TotalSeconds, player.Position.TotalSeconds + seconds));
            player.Position = TimeSpan.FromSeconds(target);
            currentTime.Text = Format(player.Position);
            SyncTimeInputs();
        }

        private void MarkStart()
        {
            if (!CanMark()) return;
            TimeSpan chosen;
            if (!ResolveMarkTime(startInput, startInputEdited, out chosen)) return;
            markHistory.Push(new MarkAction { PreviousStart = startMark, PreviousEnd = endMark,
                IsStartChange = true });
            startMark = chosen;
            endMark = null;
            startInputEdited = false;
            UpdateMarks();
            SetStatus("已设置起点 " + Format(startMark.Value) + "；按 X 设置终点。", false);
        }

        private void MarkEnd()
        {
            if (!CanMark()) return;
            TimeSpan end;
            if (!ResolveMarkTime(endInput, endInputEdited, out end)) return;

            if (!startMark.HasValue)
            {
                if (clips.Count == 0) { SetStatus("请先按 Z 设置起点。", true); return; }
                var previousClip = clips[clips.Count - 1];
                if (end <= previousClip.Start) { SetStatus("终点必须晚于片段起点。", true); return; }
                markHistory.Push(new MarkAction { EditedClipId = previousClip.Id,
                    PreviousClipEnd = previousClip.End });
                previousClip.End = end;
                endInputEdited = false;
                RenderClips();
                SetStatus("已修改片段 " + clips.Count + " 的终点为 " + Format(end) +
                    "；按 C 撤销，按 Z 开始新片段。", false);
                return;
            }
            if (end <= startMark.Value) { SetStatus("终点必须晚于起点。", true); return; }
            var clip = new Clip { Start = startMark.Value, End = end };
            markHistory.Push(new MarkAction { PreviousStart = startMark, PreviousEnd = endMark,
                CreatedClipId = clip.Id });
            endMark = end;
            clips.Add(clip);
            startMark = endMark = null;
            startInputEdited = endInputEdited = false;
            UpdateMarks();
            RenderClips();
            SetStatus("已添加片段 " + clips.Count + "；按 C 可撤销刚才的终点。", false);
        }

        private bool CanMark()
        {
            if (sourcePath == null || !File.Exists(sourcePath))
            {
                SetStatus("请先打开视频。", true);
                return false;
            }
            return true;
        }

        private bool ResolveMarkTime(TextBox input, bool edited, out TimeSpan value)
        {
            value = timeline.IsEnabled ? TimeSpan.FromMilliseconds(timeline.Value) : player.Position;
            if (edited && !TryTime(input.Text, out value))
            {
                SetStatus("时间格式无效，请输入例如 00:01:23.500。", true);
                input.Focus();
                return false;
            }
            if (value < TimeSpan.Zero || (duration > TimeSpan.Zero && value > duration))
            {
                SetStatus("标记时间必须在视频时长内。", true);
                input.Focus();
                return false;
            }
            return true;
        }

        private void SyncTimeInputs()
        {
            if (startInput == null || endInput == null || player == null) return;
            updatingTimeInputs = true;
            try
            {
                if (!startInputEdited && !startInput.IsKeyboardFocusWithin)
                    startInput.Text = Format(startMark ?? player.Position);
                if (!endInputEdited && !endInput.IsKeyboardFocusWithin)
                    endInput.Text = Format(endMark ?? player.Position);
            }
            finally { updatingTimeInputs = false; }
        }

        private void UndoMark()
        {
            if (markHistory.Count == 0) { SetStatus("没有可撤销的时间设置。", true); return; }
            var action = markHistory.Pop();
            if (action.CreatedClipId != Guid.Empty)
                clips.RemoveAll(c => c.Id == action.CreatedClipId);
            Clip editedClip = null;
            if (action.EditedClipId != Guid.Empty)
            {
                editedClip = clips.FirstOrDefault(c => c.Id == action.EditedClipId);
                if (editedClip != null && action.PreviousClipEnd.HasValue)
                    editedClip.End = action.PreviousClipEnd.Value;
            }
            startMark = action.PreviousStart;
            endMark = action.PreviousEnd;
            startInputEdited = endInputEdited = false;
            UpdateMarks();
            if (editedClip != null && endInput != null)
            {
                updatingTimeInputs = true;
                try { endInput.Text = Format(editedClip.End); }
                finally { updatingTimeInputs = false; }
            }
            RenderClips();
            if (action.IsStartChange)
                SetStatus(startMark.HasValue ? "已恢复上一个起点 " + Format(startMark.Value) + "。" : "已撤销起点设置。", false);
            else if (action.EditedClipId != Guid.Empty)
                SetStatus(editedClip == null ? "已撤销终点修改。" :
                    "已恢复片段 " + (clips.IndexOf(editedClip) + 1) + " 的原终点 " + Format(editedClip.End) + "。", false);
            else
                SetStatus("已撤销终点，裁剪记录已移除；可以重新按 X。", false);
        }

        private void UpdateMarks()
        {
            startLabel.Text = startMark.HasValue ? "起点  " + Format(startMark.Value) : "起点  —";
            startLabel.Foreground = Brush(startMark.HasValue ? "#3F78E8" : "#8390A2");
            startLabel.FontSize = startMark.HasValue ? 11.5 : 11;
            startLabel.FontWeight = startMark.HasValue ? FontWeights.SemiBold : FontWeights.Normal;
            endLabel.Text = endMark.HasValue ? "终点  " + Format(endMark.Value) : "终点  —";
            markStartButton.Content = startMark.HasValue ? "✓ 起点" : "Z 起点";
            markStartButton.Background = Brush(startMark.HasValue ? "#DFB33C" : "#F2C94C");
            markStartButton.Foreground = Brush("#4B3D12");
            SyncTimeInputs();
        }

        private void RenderClips()
        {
            if (clipRows == null) return;
            UpdateClipListArea();
            clipRows.Children.Clear();
            countLabel.Text = mergeVideos.Count > 0 ? mergeVideos.Count + " 个视频" +
                (clips.Count > 0 ? " · " + clips.Count + " 条片段" : "") : clips.Count + " 条";
            UpdateMergeButton();
            if (mergeVideos.Count > 0)
            {
                var videoHeading = Text("待合并视频（按列表顺序）", 12, "#718097", FontWeights.SemiBold);
                videoHeading.Margin = new Thickness(4, 4, 0, 6);
                clipRows.Children.Add(videoHeading);
                for (int i = 0; i < mergeVideos.Count; i++)
                    clipRows.Children.Add(VideoRow(mergeVideos[i], i));
            }
            if (clips.Count > 0 && mergeVideos.Count > 0)
            {
                var clipHeading = Text("裁剪片段", 12, "#718097", FontWeights.SemiBold);
                clipHeading.Margin = new Thickness(4, 8, 0, 6);
                clipRows.Children.Add(clipHeading);
            }
            if (clips.Count == 0 && mergeVideos.Count == 0)
            {
                var empty = new StackPanel { Margin = new Thickness(0, 45, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center };
                empty.Children.Add(Text("◌", 34, "#C4D0DF", FontWeights.Light));
                empty.Children.Add(Text("还没有裁剪片段", 13, "#9BA7B6", FontWeights.Medium));
                clipRows.Children.Add(empty);
                return;
            }
            for (int i = 0; i < clips.Count; i++)
                clipRows.Children.Add(ClipRow(clips[i], i + 1));
        }

        private void UpdateMergeButton()
        {
            if (mergeButton == null) return;
            bool videoMerge = mergeVideos.Count > 1;
            bool clipMerge = clips.Count > 1;
            mergeButton.Content = videoMerge ? "合并视频" : "合并转换";
            mergeButton.IsEnabled = (videoMerge || clipMerge) && !exporting;
            mergeButton.Opacity = mergeButton.IsEnabled ? 1 : 0.56;
            mergeButton.ToolTip = videoMerge ? "按视频列表顺序合并完整视频，名称取第一条视频名加“_合并”" :
                clipMerge ? "按片段列表顺序合并输出一个视频" : "至少添加两条裁剪记录或载入两个视频后可用";
        }

        private Border VideoRow(string path, int index)
        {
            var row = new Grid { Margin = new Thickness(11, 2, 9, 2),
                VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new Grid { VerticalAlignment = VerticalAlignment.Center };
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            info.Children.Add(Text("视频 " + (index + 1).ToString("00"), 11, "#253246", FontWeights.SemiBold));
            var name = Text(Path.GetFileName(path), 12, "#52627A", FontWeights.Normal);
            name.Margin = new Thickness(12, 0, 8, 0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = path;
            Grid.SetColumn(name, 1);
            info.Children.Add(name);
            row.Children.Add(info);

            var actions = new StackPanel { Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center };
            var up = Button("↑", "#EAF2FF", "#4B83F5", 28);
            up.Height = 26;
            up.FontSize = 13;
            up.IsEnabled = index > 0 && !exporting;
            up.Opacity = up.IsEnabled ? 1 : 0.45;
            up.ToolTip = "上移";
            up.Click += delegate { MoveVideo(index, -1); };
            actions.Children.Add(up);
            var down = Button("↓", "#EAF2FF", "#4B83F5", 28);
            down.Height = 26;
            down.FontSize = 13;
            down.Margin = new Thickness(4, 0, 0, 0);
            down.IsEnabled = index < mergeVideos.Count - 1 && !exporting;
            down.Opacity = down.IsEnabled ? 1 : 0.45;
            down.ToolTip = "下移";
            down.Click += delegate { MoveVideo(index, 1); };
            actions.Children.Add(down);
            var remove = Button("×", "#F8ECEE", "#B96570", 28);
            remove.Height = 26;
            remove.FontSize = 16;
            remove.Margin = new Thickness(5, 0, 0, 0);
            remove.IsEnabled = !exporting;
            remove.Click += delegate { RemoveVideo(index); };
            actions.Children.Add(remove);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            var card = Card(row, "#FFFFFF", 12);
            card.MinHeight = 32;
            card.Margin = new Thickness(0, 0, 0, 4);
            return card;
        }

        private void MoveVideo(int index, int offset)
        {
            int target = index + offset;
            if (exporting || index < 0 || index >= mergeVideos.Count || target < 0 || target >= mergeVideos.Count)
                return;
            string path = mergeVideos[index];
            mergeVideos.RemoveAt(index);
            mergeVideos.Insert(target, path);
            if (!String.Equals(sourcePath, mergeVideos[0], StringComparison.OrdinalIgnoreCase))
                LoadVideo(mergeVideos[0]);
            else RenderClips();
            SetStatus("已调整合并顺序。", false);
        }

        private void RemoveVideo(int index)
        {
            if (exporting || index < 0 || index >= mergeVideos.Count) return;
            bool removedFirst = index == 0;
            mergeVideos.RemoveAt(index);
            if (mergeVideos.Count == 0)
            {
                RenderClips();
                SetStatus("视频列表已清空。", false);
            }
            else if (mergeVideos.Count == 1)
            {
                string remaining = mergeVideos[0];
                mergeVideos.Clear();
                if (removedFirst || !String.Equals(sourcePath, remaining, StringComparison.OrdinalIgnoreCase))
                    LoadVideo(remaining);
                else RenderClips();
                SetStatus("已保留一个视频，可继续裁剪。", false);
            }
            else if (removedFirst)
            {
                LoadVideo(mergeVideos[0]);
                SetStatus("已移除视频并更新合并顺序。", false);
            }
            else
            {
                RenderClips();
                SetStatus("已从合并列表移除视频。", false);
            }
        }

        private void UpdateClipListArea()
        {
            if (clipListArea == null) return;
            const int baseVisibleRows = 5;
            const int maximumVisibleRows = 10;
            const double rowHeight = 36;
            int visibleRows = Math.Min(clips.Count + mergeVideos.Count + (mergeVideos.Count > 0 ? 1 : 0), maximumVisibleRows);
            double requestedExtraHeight = Math.Max(0, visibleRows - baseVisibleRows) * rowHeight;
            double heightChange = requestedExtraHeight - expandedClipArea;
            if (heightChange == 0) return;

            clipListArea.Height = new GridLength(240 + requestedExtraHeight);
            Height += heightChange;
            expandedClipArea = requestedExtraHeight;
        }

        private Border ClipRow(Clip clip, int number)
        {
            bool isEditing = editingClipId.HasValue && editingClipId.Value == clip.Id;
            var row = new Grid { Margin = new Thickness(11, 2, 9, 2),
                VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new Grid { VerticalAlignment = VerticalAlignment.Center };
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            info.Children.Add(Text("片段 " + number.ToString("00"), 12, "#253246", FontWeights.SemiBold));
            var length = Text(Format(clip.End - clip.Start), 12, "#52627A", FontWeights.SemiBold);
            length.Margin = new Thickness(12, 0, 18, 0);
            Grid.SetColumn(length, 1);
            info.Children.Add(length);
            TextBox startBox = null;
            TextBox endBox = null;
            if (isEditing)
            {
                startBox = TimeInput();
                startBox.Text = Format(clip.Start);
                startBox.Width = 106;
                startBox.Height = 28;
                startBox.FontSize = 11;
                startBox.Margin = new Thickness(0, 0, 5, 0);
                Grid.SetColumn(startBox, 2);
                info.Children.Add(startBox);
                var arrow = Text("→", 12, "#7D8DA4", FontWeights.Medium);
                arrow.Margin = new Thickness(0, 0, 5, 0);
                Grid.SetColumn(arrow, 3);
                info.Children.Add(arrow);
                endBox = TimeInput();
                endBox.Text = Format(clip.End);
                endBox.Width = 106;
                endBox.Height = 28;
                endBox.FontSize = 11;
                Grid.SetColumn(endBox, 4);
                info.Children.Add(endBox);
                startBox.Loaded += delegate { startBox.Focus(); startBox.SelectAll(); };
            }
            else
            {
                var span = Text(Format(clip.Start) + "  →  " + Format(clip.End), 12,
                    "#8290A3", FontWeights.Normal);
                span.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(span, 2);
                Grid.SetColumnSpan(span, 3);
                info.Children.Add(span);
            }
            row.Children.Add(info);
            var actions = new StackPanel { Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center };
            if (isEditing)
            {
                var save = Button("保存", "#4B83F5", "#FFFFFF", 44);
                save.Height = 26;
                save.FontSize = 10;
                save.Click += delegate { SaveClipEdit(clip, startBox, endBox); };
                actions.Children.Add(save);
                var cancel = Button("取消", "#E9EDF4", "#5D6B80", 44);
                cancel.Height = 26;
                cancel.FontSize = 10;
                cancel.Margin = new Thickness(5, 0, 0, 0);
                cancel.Click += delegate { CancelClipEdit(); };
                actions.Children.Add(cancel);
                KeyEventHandler handleEditKey = delegate(object sender, KeyEventArgs e)
                {
                    if (e.Key == Key.Enter) { SaveClipEdit(clip, startBox, endBox); e.Handled = true; }
                    else if (e.Key == Key.Escape) { CancelClipEdit(); e.Handled = true; }
                };
                startBox.PreviewKeyDown += handleEditKey;
                endBox.PreviewKeyDown += handleEditKey;
            }
            else
            {
                var edit = Button("编辑", "#EAF2FF", "#4B83F5", 47);
                edit.Height = 26;
                edit.FontSize = 10;
                edit.Click += delegate { EditClip(clip); };
                actions.Children.Add(edit);
            }
            int clipIndex = clips.IndexOf(clip);
            var up = Button("↑", "#EAF2FF", "#4B83F5", 28);
            up.Height = 26;
            up.FontSize = 13;
            up.Margin = new Thickness(5, 0, 0, 0);
            up.IsEnabled = clipIndex > 0 && !isEditing && !exporting;
            up.Opacity = up.IsEnabled ? 1 : 0.45;
            up.ToolTip = "上移";
            up.Click += delegate { MoveClip(clip, -1); };
            actions.Children.Add(up);
            var down = Button("↓", "#EAF2FF", "#4B83F5", 28);
            down.Height = 26;
            down.FontSize = 13;
            down.Margin = new Thickness(4, 0, 0, 0);
            down.IsEnabled = clipIndex < clips.Count - 1 && !isEditing && !exporting;
            down.Opacity = down.IsEnabled ? 1 : 0.45;
            down.ToolTip = "下移";
            down.Click += delegate { MoveClip(clip, 1); };
            actions.Children.Add(down);
            var remove = Button("×", "#F8ECEE", "#B96570", 28);
            remove.Height = 26;
            remove.FontSize = 16;
            remove.Margin = new Thickness(5, 0, 0, 0);
            remove.Click += delegate
            {
                if (isEditing) editingClipId = null;
                clips.Remove(clip);
                RenderClips();
                SetStatus("片段已删除。", false);
            };
            actions.Children.Add(remove);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            var card = Card(row, "#FFFFFF", 12);
            card.MinHeight = 32;
            card.Margin = new Thickness(0, 0, 0, 4);
            return card;
        }

        private void MoveClip(Clip clip, int offset)
        {
            if (exporting || editingClipId.HasValue) return;
            int index = clips.IndexOf(clip);
            int target = index + offset;
            if (index < 0 || target < 0 || target >= clips.Count) return;
            clips.RemoveAt(index);
            clips.Insert(target, clip);
            RenderClips();
            SetStatus("已调整片段顺序，输出将按当前列表顺序执行。", false);
        }

        private void EditClip(Clip clip)
        {
            editingClipId = clip.Id;
            RenderClips();
        }

        private void SaveClipEdit(Clip clip, TextBox startBox, TextBox endBox)
        {
            TimeSpan start, end;
            if (!TryTime(startBox.Text, out start) || !TryTime(endBox.Text, out end) ||
                start < TimeSpan.Zero || end <= start || (duration > TimeSpan.Zero && end > duration))
            {
                startBox.BorderBrush = Brush("#D15C68");
                endBox.BorderBrush = Brush("#D15C68");
                SetStatus("请输入有效时间，且终点必须晚于起点并在视频时长内。", true);
                return;
            }
            clip.Start = start;
            clip.End = end;
            editingClipId = null;
            RenderClips();
            SetStatus("片段时间已更新。", false);
        }

        private void CancelClipEdit()
        {
            editingClipId = null;
            RenderClips();
        }

        private static bool TryTime(string text, out TimeSpan result)
        {
            return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out result);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.Z) { MarkStart(); e.Handled = true; return; }
            if (e.Key == Key.X) { MarkEnd(); e.Handled = true; return; }
            if (e.Key == Key.C) { UndoMark(); e.Handled = true; return; }
            if (Keyboard.FocusedElement is TextBox) return;
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.V: Jump(-10); break;
                case Key.B: Jump(10); break;
                default: return;
            }
            e.Handled = true;
        }

        private void ChooseFolder()
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择裁剪输出文件夹";
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    outputFolder = dialog.SelectedPath;
                    folderLabel.Text = outputFolder;
                    folderLabel.ToolTip = outputFolder;
                    SavePreferences();
                }
            }
        }

        private static string PreferencesPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoCut", "preferences.ini");
        }

        private static string EncodePreference(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string DecodePreference(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private void LoadPreferences()
        {
            try
            {
                string path = PreferencesPath();
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int separator = line.IndexOf('=');
                    if (separator < 0) continue;
                    string key = line.Substring(0, separator);
                    string value;
                    try { value = DecodePreference(line.Substring(separator + 1)); }
                    catch { continue; }
                    int index;
                    if (key == "playbackSpeedIndex" && Int32.TryParse(value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out index))
                        preferredSpeedIndex = index;
                    else if (key == "outputModeIndex" && Int32.TryParse(value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out index))
                        preferredModeIndex = index;
                    else if (key == "outputFolder")
                        outputFolder = String.IsNullOrWhiteSpace(value) ? null : value;
                }
            }
            catch { }
        }

        private void SavePreferences()
        {
            if (speedBox != null && speedBox.SelectedIndex >= 0)
                preferredSpeedIndex = speedBox.SelectedIndex;
            if (modeBox != null && modeBox.SelectedIndex >= 0)
                preferredModeIndex = modeBox.SelectedIndex;
            try
            {
                string path = PreferencesPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new[]
                {
                    "playbackSpeedIndex=" + EncodePreference(preferredSpeedIndex.ToString(CultureInfo.InvariantCulture)),
                    "outputModeIndex=" + EncodePreference(preferredModeIndex.ToString(CultureInfo.InvariantCulture)),
                    "outputFolder=" + EncodePreference(outputFolder)
                }, new UTF8Encoding(false));
            }
            catch
            {
                if (statusLabel != null) SetStatus("设置无法保存到本机用户目录。", true);
            }
        }

        private void ChooseFfmpeg()
        {
            var dialog = new OpenFileDialog { Title = "选择 ffmpeg.exe", Filter = "FFmpeg|ffmpeg.exe|程序|*.exe" };
            if (dialog.ShowDialog(this) == true)
            {
                ffmpegPath = dialog.FileName;
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.path"), ffmpegPath);
                UpdateFfmpegLabel();
            }
        }

        private static string FindFfmpeg()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string local = Path.Combine(directory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            string setting = Path.Combine(directory, "ffmpeg.path");
            if (File.Exists(setting))
            {
                string path = File.ReadAllText(setting).Trim();
                if (File.Exists(path)) return path;
            }
            string environment = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string part in environment.Split(';'))
            {
                if (String.IsNullOrWhiteSpace(part)) continue;
                try
                {
                    string path = Path.Combine(part.Trim(), "ffmpeg.exe");
                    if (File.Exists(path)) return path;
                }
                catch { }
            }
            return null;
        }

        private void UpdateFfmpegLabel()
        {
            if (ffmpegLabel == null) return;
            ffmpegLabel.Text = ffmpegPath == null ? "未找到 FFmpeg，转换前请设置路径" : ffmpegPath;
            ffmpegLabel.ToolTip = ffmpegLabel.Text;
            ffmpegLabel.Foreground = Brush(ffmpegPath == null ? "#D77777" : "#9AA6B6");
        }

        private async Task MergeVideos()
        {
            if (exporting) return;
            var videos = mergeVideos.Where(File.Exists).ToList();
            if (videos.Count < 2) { SetStatus("合并视频至少需要两个有效视频文件。", true); return; }
            if (ffmpegPath == null || !File.Exists(ffmpegPath))
            {
                SetStatus("请先设置 FFmpeg 路径。", true);
                ChooseFfmpeg();
                if (ffmpegPath == null) return;
            }

            string folder = outputFolder ?? Path.Combine(Path.GetDirectoryName(videos[0]), "裁剪输出");
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) { SetStatus("无法创建输出文件夹：" + ex.Message, true); return; }
            bool reencode = modeBox.SelectedIndex == 1;
            string target = Exporter.UniqueMergedTarget(folder, videos[0], reencode);
            string tempDirectory = Path.Combine(folder, ".videocut-" + Guid.NewGuid().ToString("N"));
            exporting = true;
            exportButton.IsEnabled = false;
            UpdateMergeButton();
            cancelButton.Visibility = Visibility.Visible;
            cancellation = new CancellationTokenSource();
            progress.Value = 0;
            try
            {
                Directory.CreateDirectory(tempDirectory);
                string listFile = Path.Combine(tempDirectory, "concat.txt");
                var entries = videos.Select(path => "file '" + path.Replace("\\", "/").Replace("'", "'\\''") + "'");
                File.WriteAllLines(listFile, entries, new System.Text.UTF8Encoding(false));
                string partial = Exporter.PartialPath(target);
                SetStatus("正在按列表顺序合并 " + videos.Count + " 个视频…", false);
                await RunConcat(listFile, partial, TimeSpan.Zero, cancellation.Token, 0, 100, reencode);
                File.Move(partial, target);
                progress.Value = 100;
                SetStatus("完成：已合并输出 " + Path.GetFileName(target), false);
                MessageBox.Show(this, "已按列表顺序合并输出视频。\n\n" + target,
                    "合并视频完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                SetStatus("合并视频已取消；临时文件已清理。", true);
            }
            catch (Exception ex)
            {
                SetStatus("合并视频失败：" + ex.Message, true);
                MessageBox.Show(this, ex.Message, "合并视频失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                exporting = false;
                exportButton.IsEnabled = true;
                UpdateMergeButton();
                cancelButton.Visibility = Visibility.Collapsed;
                cancellation.Dispose();
                cancellation = null;
                runningProcess = null;
                if (Directory.Exists(tempDirectory))
                    try { Directory.Delete(tempDirectory, true); } catch { }
            }
        }

        private async Task ExportAll(bool merge)
        {
            if (exporting) return;
            if (sourcePath == null || !File.Exists(sourcePath)) { SetStatus("请先打开视频。", true); return; }
            if (clips.Count == 0) { SetStatus("请先添加至少一条裁剪记录。", true); return; }
            if (merge && clips.Count < 2) { SetStatus("合并转换至少需要两条裁剪记录。", true); return; }
            if (ffmpegPath == null || !File.Exists(ffmpegPath))
            {
                SetStatus("请先设置 FFmpeg 路径。", true);
                ChooseFfmpeg();
                if (ffmpegPath == null) return;
            }
            string folder = outputFolder ?? Path.Combine(Path.GetDirectoryName(sourcePath), "裁剪输出");
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) { SetStatus("无法创建输出文件夹：" + ex.Message, true); return; }
            bool reencode = modeBox.SelectedIndex == 1;
            var jobs = clips.Select(c => new Clip { Start = c.Start, End = c.End }).ToList();
            string tempDirectory = null;
            string mergedTarget = merge ? Exporter.UniqueMergedTarget(folder, sourcePath, reencode) : null;
            exporting = true;
            exportButton.IsEnabled = false;
            mergeButton.IsEnabled = false;
            mergeButton.Opacity = 0.56;
            cancelButton.Visibility = Visibility.Visible;
            cancellation = new CancellationTokenSource();
            progress.Value = 0;
            int succeeded = 0;
            try
            {
                if (!merge)
                {
                    for (int i = 0; i < jobs.Count; i++)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        string target = Exporter.UniqueTarget(folder, sourcePath, i + 1, reencode);
                        string partial = Exporter.PartialPath(target);
                        SetStatus("正在转换 " + (i + 1) + " / " + jobs.Count + "：" + Path.GetFileName(target), false);
                        await RunFfmpeg(jobs[i], partial, reencode, i, jobs.Count, cancellation.Token);
                        File.Move(partial, target);
                        succeeded++;
                        progress.Value = 100.0 * succeeded / jobs.Count;
                    }
                    SetStatus("完成：已输出 " + succeeded + " 条片段到 " + folder, false);
                    MessageBox.Show(this, "已输出 " + succeeded + " 条片段。\n\n" + folder,
                        "转换完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    tempDirectory = Path.Combine(folder, ".videocut-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDirectory);
                    string extension = reencode ? ".mp4" : Path.GetExtension(sourcePath);
                    if (String.IsNullOrEmpty(extension)) extension = ".mkv";
                    var segmentNames = new List<string>();
                    for (int i = 0; i < jobs.Count; i++)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        string name = "part_" + (i + 1).ToString("000") + extension;
                        string target = Path.Combine(tempDirectory, name);
                        string partial = Exporter.PartialPath(target);
                        segmentNames.Add(name);
                        SetStatus("正在裁剪 " + (i + 1) + " / " + jobs.Count + "：准备合并", false);
                        await RunFfmpeg(jobs[i], partial, reencode, i, jobs.Count,
                            cancellation.Token, 0, 85);
                        File.Move(partial, target);
                    }

                    string listFile = Path.Combine(tempDirectory, "concat.txt");
                    File.WriteAllLines(listFile, segmentNames.Select(name => "file '" + name + "'"),
                        new System.Text.UTF8Encoding(false));
                    string mergedPartial = Exporter.PartialPath(mergedTarget);
                    TimeSpan total = TimeSpan.FromMilliseconds(jobs.Sum(c => (c.End - c.Start).TotalMilliseconds));
                    SetStatus("正在按列表顺序合并 " + jobs.Count + " 条片段…", false);
                    await RunConcat(listFile, mergedPartial, total, cancellation.Token, 85, 15);
                    File.Move(mergedPartial, mergedTarget);
                    progress.Value = 100;
                    SetStatus("完成：已合并输出 " + Path.GetFileName(mergedTarget), false);
                    MessageBox.Show(this, "已按列表顺序合并输出视频。\n\n" + mergedTarget,
                        "合并转换完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus(merge ? "合并转换已取消；临时片段已清理。" : "转换已取消；已完成 " + succeeded + " 条。", true);
            }
            catch (Exception ex)
            {
                SetStatus((merge ? "合并转换失败：" : "转换失败：") + ex.Message, true);
                MessageBox.Show(this, ex.Message, merge ? "合并转换失败" : "转换失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                exporting = false;
                exportButton.IsEnabled = true;
                UpdateMergeButton();
                cancelButton.Visibility = Visibility.Collapsed;
                cancellation.Dispose();
                cancellation = null;
                runningProcess = null;
                if (tempDirectory != null && Directory.Exists(tempDirectory))
                    try { Directory.Delete(tempDirectory, true); } catch { }
            }
        }

        private async Task RunFfmpeg(Clip clip, string partial, bool reencode,
            int index, int count, CancellationToken token, double progressOffset = 0, double progressScale = 100)
        {
            string args = Exporter.Arguments(sourcePath, partial, clip, reencode);
            var info = new ProcessStartInfo(ffmpegPath, args) { UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var errors = new System.Text.StringBuilder();
            using (var process = new Process { StartInfo = info })
            {
                runningProcess = process;
                bool started = false;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null || !e.Data.StartsWith("out_time_ms=")) return;
                    long micros;
                    if (!Int64.TryParse(e.Data.Substring(12), out micros)) return;
                    double ratio = Math.Min(1, Math.Max(0,
                        micros / 1000000.0 / (clip.End - clip.Start).TotalSeconds));
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        progress.Value = progressOffset + progressScale * (index + ratio) / count;
                    }));
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null && errors.Length < 5000) errors.AppendLine(e.Data);
                };
                try
                {
                    process.Start();
                    started = true;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await Task.Run(delegate { process.WaitForExit(); });
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException(errors.ToString().Trim());
                }
                finally
                {
                    runningProcess = null;
                    if (File.Exists(partial) && (!started || token.IsCancellationRequested ||
                        !process.HasExited || process.ExitCode != 0))
                        try { File.Delete(partial); } catch { }
                }
            }
        }

        private async Task RunConcat(string listFile, string partial, TimeSpan totalDuration,
            CancellationToken token, double progressOffset, double progressScale, bool reencode = false)
        {
            string args = Exporter.ConcatArguments(listFile, partial, reencode);
            var info = new ProcessStartInfo(ffmpegPath, args) { UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(listFile) };
            var errors = new System.Text.StringBuilder();
            using (var process = new Process { StartInfo = info })
            {
                runningProcess = process;
                bool started = false;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null || !e.Data.StartsWith("out_time_ms=")) return;
                    long micros;
                    if (!Int64.TryParse(e.Data.Substring(12), out micros)) return;
                    if (totalDuration.TotalSeconds <= 0) return;
                    double ratio = Math.Min(1, Math.Max(0,
                        micros / 1000000.0 / totalDuration.TotalSeconds));
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        progress.Value = progressOffset + progressScale * ratio;
                    }));
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null && errors.Length < 5000) errors.AppendLine(e.Data);
                };
                try
                {
                    process.Start();
                    started = true;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await Task.Run(delegate { process.WaitForExit(); });
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException(errors.ToString().Trim());
                }
                finally
                {
                    runningProcess = null;
                    if (File.Exists(partial) && (!started || token.IsCancellationRequested ||
                        !process.HasExited || process.ExitCode != 0))
                        try { File.Delete(partial); } catch { }
                }
            }
        }

        private void CancelExport()
        {
            if (cancellation == null) return;
            cancellation.Cancel();
            try { if (runningProcess != null && !runningProcess.HasExited) runningProcess.Kill(); }
            catch { }
        }

        private void SetStatus(string message, bool error)
        {
            if (statusLabel == null) return;
            statusLabel.Text = message;
            statusLabel.Foreground = Brush(error ? "#C55D69" : "#8290A2");
            statusLabel.ToolTip = message;
        }

        private static string Format(TimeSpan value)
        {
            int hours = (int)value.TotalHours;
            return String.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}",
                hours, value.Minutes, value.Seconds, value.Milliseconds);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            var app = new Application();
            var window = new MainWindow();
            if (args.Length == 2 && args[0] == "--preview")
            {
                window.Loaded += delegate
                {
                    window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
                    {
                        var content = (FrameworkElement)window.Content;
                        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
                            96, 96, PixelFormats.Pbgra32);
                        image.Render(window);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(image,
                            new Int32Rect(0, 0, image.PixelWidth,
                                Math.Min(image.PixelHeight, (int)content.ActualHeight + 40)))));
                        using (var file = File.Create(args[1])) encoder.Save(file);
                        app.Shutdown();
                    }));
                };
            }
            app.Run(window);
        }
    }
}

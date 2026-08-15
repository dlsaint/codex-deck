using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.IO.Pipes;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodexProjectCenter
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                AppLog.Error("Unhandled exception", e.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs e)
            {
                AppLog.Error("Unobserved task exception", e.Exception);
                e.SetObserved();
            };
            if (args.Length >= 2 && args[0] == "--self-test")
            {
                HeadlessDiagnostics.Run(args[1]);
                return;
            }
            if (args.Length >= 3 && args[0] == "--discover-test")
            {
                DiscoveryDiagnostics.Run(args[1], args[2], args.Length >= 4 ? args[3] : "local");
                return;
            }
            if (args.Length >= 6 && args[0] == "--side-navigation-test")
            {
                SideNavigationDiagnostics.Run(args[1], args[2], args[3], args[4], args[5], args.Length >= 7 ? args[6] : "local");
                return;
            }
            if (args.Length >= 5 && args[0] == "--sidebar-navigation-test")
            {
                SidebarNavigationDiagnostics.Run(args[1], args[2], args[3], args[4], args.Length >= 6 ? args[5] : "local");
                return;
            }
            if (args.Length >= 3 && args[0] == "--log-monitor-test")
            {
                LogMonitorDiagnostics.Run(args[1], args[2]);
                return;
            }
            if (args.Length >= 2 && args[0] == "--cache-merge-test")
            {
                DiscoveryCacheDiagnostics.Run(args[1]);
                return;
            }
            if (args.Length >= 2 && args[0] == "--title-sync-test")
            {
                TitleSyncDiagnostics.Run(args[1]);
                return;
            }
            if (args.Length >= 2 && args[0] == "--navigation-event-test")
            {
                NavigationEventDiagnostics.Run(args[1]);
                return;
            }
            if (args.Length >= 2 && args[0] == "--notification-style-test")
            {
                NotificationWindowDiagnostics.Run(args[1]);
                return;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                AppLog.Error("Dispatcher exception", e.Exception);
            };
            app.Run(new MainWindow());
        }
    }

    internal static class AppLog
    {
        private const long MaxLogBytes = 1024 * 1024;
        private static readonly object Sync = new object();
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexProjectCenter");
        internal static readonly string FilePath = Path.Combine(DirectoryPath, "project-center.log");

        internal static void Error(string context, Exception exception)
        {
            Write("ERROR", context + (exception == null ? "" : Environment.NewLine + exception));
        }

        internal static void Info(string message) { Write("INFO", message); }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= MaxLogBytes)
                    {
                        var previous = FilePath + ".1";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(FilePath, previous);
                    }
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    internal static class PerfDiagnostics
    {
        private static readonly object LogSync = new object();
        private static readonly Dictionary<string, DateTime> LastLoggedUtc =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly object IpcSync = new object();
        private static long _ipcWindowStarted = Stopwatch.GetTimestamp();
        private static long _ipcMessages;
        private static long _ipcBytes;
        private static long _ipcMaxMessageBytes;
        private static int _ipcMaxSubscriptions;

        internal static void Duration(string key, Stopwatch stopwatch, long thresholdMs, string details = null)
        {
            if (stopwatch == null || stopwatch.ElapsedMilliseconds < thresholdMs) return;
            Report(key, "elapsedMs=" + stopwatch.ElapsedMilliseconds +
                (string.IsNullOrWhiteSpace(details) ? "" : " " + details));
        }

        internal static void Report(string key, string details, int cooldownSeconds = 60)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var now = DateTime.UtcNow;
            lock (LogSync)
            {
                DateTime last;
                if (LastLoggedUtc.TryGetValue(key, out last) && now - last < TimeSpan.FromSeconds(cooldownSeconds)) return;
                LastLoggedUtc[key] = now;
            }
            try
            {
                using (var process = Process.GetCurrentProcess())
                    AppLog.Info("[PERF] " + key + " " + (details ?? "") +
                        " privateMb=" + Math.Round(process.PrivateMemorySize64 / 1048576d, 1).ToString(CultureInfo.InvariantCulture) +
                        " workingSetMb=" + Math.Round(process.WorkingSet64 / 1048576d, 1).ToString(CultureInfo.InvariantCulture) +
                        " threads=" + process.Threads.Count + " handles=" + process.HandleCount);
            }
            catch { AppLog.Info("[PERF] " + key + " " + (details ?? "")); }
        }

        internal static void ObserveIpcMessage(int payloadBytes, int subscriptionCount)
        {
            Interlocked.Increment(ref _ipcMessages);
            Interlocked.Add(ref _ipcBytes, Math.Max(0, payloadBytes));
            UpdateMaximum(ref _ipcMaxMessageBytes, payloadBytes);
            UpdateMaximum(ref _ipcMaxSubscriptions, subscriptionCount);
            var elapsedSeconds = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _ipcWindowStarted)) /
                (double)Stopwatch.Frequency;
            if (elapsedSeconds < 30) return;
            lock (IpcSync)
            {
                elapsedSeconds = (Stopwatch.GetTimestamp() - _ipcWindowStarted) / (double)Stopwatch.Frequency;
                if (elapsedSeconds < 30) return;
                var messages = Interlocked.Exchange(ref _ipcMessages, 0);
                var bytes = Interlocked.Exchange(ref _ipcBytes, 0);
                var maxBytes = Interlocked.Exchange(ref _ipcMaxMessageBytes, 0);
                var maxSubscriptions = Interlocked.Exchange(ref _ipcMaxSubscriptions, 0);
                Interlocked.Exchange(ref _ipcWindowStarted, Stopwatch.GetTimestamp());
                var mbPerSecond = bytes / 1048576d / Math.Max(1, elapsedSeconds);
                var messagesPerSecond = messages / Math.Max(1, elapsedSeconds);
                if (mbPerSecond < 4 && maxBytes < 4 * 1024 * 1024 && messagesPerSecond < 20 && maxSubscriptions <= 20) return;
                Report("ipc-flow", "seconds=" + Math.Round(elapsedSeconds, 1).ToString(CultureInfo.InvariantCulture) +
                    " messages=" + messages + " messagesPerSec=" + Math.Round(messagesPerSecond, 1).ToString(CultureInfo.InvariantCulture) +
                    " totalMb=" + Math.Round(bytes / 1048576d, 1).ToString(CultureInfo.InvariantCulture) +
                    " mbPerSec=" + Math.Round(mbPerSecond, 2).ToString(CultureInfo.InvariantCulture) +
                    " maxMessageMb=" + Math.Round(maxBytes / 1048576d, 2).ToString(CultureInfo.InvariantCulture) +
                    " maxSubscriptions=" + maxSubscriptions);
            }
        }

        private static void UpdateMaximum(ref long target, long value)
        {
            long current;
            while (value > (current = Interlocked.Read(ref target)) &&
                Interlocked.CompareExchange(ref target, value, current) != current) { }
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            int current;
            while (value > (current = target) && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }

    internal sealed class AwaitingReviewStore : IDisposable
    {
        private readonly object _sync = new object();
        private readonly string _path;
        private Timer _saveTimer;
        private Dictionary<string, DateTime> _pending;

        internal AwaitingReviewStore(string path = null)
        {
            _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexProjectCenter", "awaiting-review.json");
        }

        internal Dictionary<string, DateTime> Load()
        {
            var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(_path)) return result;
                var values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(_path, Encoding.UTF8)) as IDictionary<string, object>;
                if (values == null) return result;
                foreach (var pair in values)
                {
                    DateTime timestamp;
                    if (DateTime.TryParse(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out timestamp)) result[pair.Key] = timestamp;
                }
            }
            catch (Exception ex) { AppLog.Error("Load awaiting-review state failed", ex); }
            return result;
        }

        internal void ScheduleSave(IDictionary<string, DateTime> values)
        {
            lock (_sync)
            {
                _pending = new Dictionary<string, DateTime>(values, StringComparer.OrdinalIgnoreCase);
                if (_saveTimer == null) _saveTimer = new Timer(delegate { SavePending(); }, null, 150, Timeout.Infinite);
                else _saveTimer.Change(150, Timeout.Infinite);
            }
        }

        private void SavePending()
        {
            Dictionary<string, DateTime> values;
            lock (_sync)
            {
                values = _pending;
                _pending = null;
            }
            if (values == null) return;
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var serialized = values.ToDictionary(pair => pair.Key,
                    pair => pair.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(_path, new JavaScriptSerializer().Serialize(serialized), Encoding.UTF8);
            }
            catch (Exception ex) { AppLog.Error("Save awaiting-review state failed", ex); }
        }

        internal void Flush() { SavePending(); }

        public void Dispose()
        {
            Flush();
            lock (_sync)
            {
                if (_saveTimer != null) _saveTimer.Dispose();
                _saveTimer = null;
            }
        }
    }

    internal enum TaskGroup { Waiting, Running, Completed, Error, History }

    internal sealed class ThreadItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string NavigationTitle { get; set; }
        public string Preview { get; set; }
        public string Cwd { get; set; }
        public string Project { get; set; }
        public string HostLabel { get; set; }
        public string HostId { get; set; }
        public DateTime UpdatedAt { get; set; }
        public TaskGroup Group { get; set; }
        public string StatusText { get; set; }
        public bool IsPinned { get; set; }
        public string RolloutPath { get; set; }
        public bool IsSideConversation { get; set; }
        public string ParentThreadId { get; set; }
        public bool SideParentVerified { get; set; }
        public bool NavigationTitleVerified { get; set; }
    }

    internal static class NavigationEventCatalog
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, NavigationEvent> Routes =
            new Dictionary<string, NavigationEvent>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, NavigationEvent> Views =
            new Dictionary<string, NavigationEvent>(StringComparer.OrdinalIgnoreCase);
        private static string _latestRouteThreadId;
        private static NavigationEvent _latestRoute;

        internal static void ObserveRoute(string threadId, string hostId)
        {
            Observe(Routes, threadId, hostId, true);
            lock (Sync)
            {
                _latestRouteThreadId = threadId;
                NavigationEvent value;
                Routes.TryGetValue(threadId, out value);
                _latestRoute = value;
            }
        }

        internal static void ObserveView(string threadId, string hostId, bool active)
        {
            Observe(Views, threadId, hostId, active);
        }

        internal static bool WasOpenedSince(ThreadItem item, DateTime requestedAt)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id)) return false;
            lock (Sync)
            {
                NavigationEvent value;
                var source = item.IsSideConversation ? Views : Routes;
                if (!source.TryGetValue(item.Id, out value) || !value.Active ||
                    value.SeenAtUtc < requestedAt.ToUniversalTime().AddMilliseconds(-250)) return false;
                return IsLocalHost(item.HostId) || string.Equals(ThreadIdentity.Host(item.HostId),
                    ThreadIdentity.Host(value.HostId), StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static bool IsCurrentlyViewed(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return false;
            lock (Sync)
            {
                NavigationEvent value;
                return Views.TryGetValue(threadId, out value) && value.Active;
            }
        }

        internal static bool IsCurrentlyRouted(string threadId, string hostId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return false;
            lock (Sync)
            {
                var value = _latestRoute;
                if (value == null || !value.Active ||
                    !string.Equals(_latestRouteThreadId, threadId, StringComparison.OrdinalIgnoreCase)) return false;
                return IsLocalHost(hostId) || string.Equals(ThreadIdentity.Host(hostId),
                    ThreadIdentity.Host(value.HostId), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void Observe(IDictionary<string, NavigationEvent> target, string threadId, string hostId, bool active)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return;
            lock (Sync)
            {
                target[threadId] = new NavigationEvent
                {
                    HostId = ThreadIdentity.Host(hostId), Active = active, SeenAtUtc = DateTime.UtcNow
                };
                if (target.Count > 512)
                    foreach (var key in target.Where(pair => DateTime.UtcNow - pair.Value.SeenAtUtc > TimeSpan.FromDays(1))
                        .Select(pair => pair.Key).ToList()) target.Remove(key);
            }
        }

        private static bool IsLocalHost(string hostId)
        {
            return string.IsNullOrWhiteSpace(hostId) || string.Equals(hostId, "local", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class NavigationEvent
        {
            public string HostId;
            public DateTime SeenAtUtc;
            public bool Active;
        }
    }

    internal static class ThreadIdentity
    {
        internal static string Host(string hostId)
        {
            return string.IsNullOrWhiteSpace(hostId) ? "local" : hostId.Trim();
        }

        internal static string Key(string threadId, string hostId)
        {
            return Host(hostId) + "\n" + (threadId ?? "");
        }

        internal static string Key(ThreadItem item)
        {
            return item == null ? "" : Key(item.Id, item.HostId);
        }
    }

    internal static class CodexWindowLocator
    {
        private const string MainWindowClass = "Chrome_WidgetWin_1";
        private const int MinimumWindowWidth = 480;
        private const int MinimumWindowHeight = 320;

        internal static IntPtr FindMainWindow()
        {
            return FindMainWindows().FirstOrDefault();
        }

        internal static IList<IntPtr> FindMainWindows()
        {
            var processIds = new HashSet<int>();
            foreach (var process in Process.GetProcessesByName("ChatGPT"))
            {
                try { processIds.Add(process.Id); }
                finally { process.Dispose(); }
            }
            if (processIds.Count == 0) return new List<IntPtr>();

            var candidates = new List<WindowCandidate>();
            NativeMethods.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint processId;
                NativeMethods.GetWindowThreadProcessId(window, out processId);
                if (!processIds.Contains(unchecked((int)processId))) return true;

                var className = new StringBuilder(128);
                NativeMethods.GetClassName(window, className, className.Capacity);
                var title = new StringBuilder(256);
                NativeMethods.GetWindowText(window, title, title.Capacity);
                NativeMethods.NativeRect rectangle;
                if (!NativeMethods.GetWindowRect(window, out rectangle)) return true;

                var visible = NativeMethods.IsWindowVisible(window);
                var iconic = NativeMethods.IsIconic(window);
                var width = Math.Max(0, rectangle.Right - rectangle.Left);
                var height = Math.Max(0, rectangle.Bottom - rectangle.Top);
                var score = ScoreCandidate(className.ToString(), title.ToString(), visible, iconic, width, height);
                if (score != long.MinValue)
                    candidates.Add(new WindowCandidate { Handle = window, Score = score });
                return true;
            }, IntPtr.Zero);

            return candidates.OrderByDescending(candidate => candidate.Score)
                .Select(candidate => candidate.Handle).ToList();
        }

        internal static long ScoreCandidateForTest(string className, string title, bool visible, bool iconic, int width, int height)
        {
            return ScoreCandidate(className, title, visible, iconic, width, height);
        }

        private static long ScoreCandidate(string className, string title, bool visible, bool iconic, int width, int height)
        {
            if (!visible || !string.Equals(className, MainWindowClass, StringComparison.Ordinal)) return long.MinValue;
            if (!iconic && (width < MinimumWindowWidth || height < MinimumWindowHeight)) return long.MinValue;

            var area = (long)Math.Max(0, width) * Math.Max(0, height);
            var score = Math.Min(area, 1000000000L);
            if (!iconic) score += 1000000000L;
            if (string.Equals(title, "ChatGPT", StringComparison.OrdinalIgnoreCase)) score += 10000000000L;
            return score;
        }

        private sealed class WindowCandidate
        {
            internal IntPtr Handle;
            internal long Score;
        }
    }

    internal sealed class MainWindow : Window
    {
        private static readonly Color Ink = Color.FromRgb(32, 33, 35);
        private static readonly Color Muted = Color.FromRgb(103, 103, 103);
        private static readonly object SideParentCacheSync = new object();
        private static readonly Dictionary<string, Tuple<string, DateTime>> SideParentCache =
            new Dictionary<string, Tuple<string, DateTime>>(StringComparer.OrdinalIgnoreCase);
        private static int _clipboardWriteInProgress;
        private readonly Dictionary<string, ThreadItem> _threads = new Dictionary<string, ThreadItem>();
        private readonly AppServerClient _client = new AppServerClient();
        private readonly DispatcherTimer _refreshTimer = new DispatcherTimer();
        private readonly StackPanel _list = new StackPanel();
        private readonly TextBlock _sectionTitle = new TextBlock();
        private readonly TextBlock _sectionCount = new TextBlock();
        private readonly TextBlock _footerLeft = new TextBlock();
        private readonly TextBlock _footerRight = new TextBlock();
        private readonly TextBlock _waitingCount = new TextBlock();
        private readonly TextBlock _runningCount = new TextBlock();
        private readonly TextBlock _completedCount = new TextBlock();
        private readonly HashSet<string> _waitingNotificationBaseline = new HashSet<string>();
        private readonly HashSet<string> _waitingNotificationChecks = new HashSet<string>();
        private readonly HashSet<string> _openingThreadIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Button _waitingTab;
        private readonly Button _runningTab;
        private readonly Button _completedTab;
        private Forms.NotifyIcon _tray;
        private Drawing.Icon _appIcon;
        private Drawing.Icon _trayBadgeIcon;
        private ImageSource _baseWindowIcon;
        private NativeMethods.ITaskbarList3 _nativeTaskbar;
        private IntPtr _taskbarOverlayIconHandle;
        private IntPtr _taskbarLargeIconHandle;
        private IntPtr _taskbarSmallIconHandle;
        private TaskGroup _selectedGroup = TaskGroup.Waiting;
        private bool _reallyClose;
        private bool _refreshing;
        private DateTime _lastHealthCheckAt = DateTime.MinValue;
        private bool _waitingNotificationReady;
        private bool _taskbarFlashing;
        private HwndSource _windowSource;
        private bool _globalHotkeyRegistered;
        private bool _windowBoundsCorrectionPending;
        private int _taskbarBadgeRefreshGeneration;
        private int _taskbarCreatedMessage;
        private int _taskbarButtonCreatedMessage;
        private int _renderedWaitingBadgeCount = -1;
        private string _renderedListSignature = "";
        private bool _renderPendingWhileHidden;
        private bool _listRenderScheduled;
        private readonly Dictionary<string, CachedTaskCard> _taskCardCache =
            new Dictionary<string, CachedTaskCard>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ThreadItem> _pendingWaitingNotifications = new Dictionary<string, ThreadItem>();
        private WaitingNotificationWindow _waitingNotificationWindow;

        public MainWindow()
        {
            Title = "项目中心";
            try
            {
                _appIcon = Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName);
                if (_appIcon != null)
                {
                    _baseWindowIcon = Imaging.CreateBitmapSourceFromHIcon(_appIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    if (_baseWindowIcon.CanFreeze) _baseWindowIcon.Freeze();
                    Icon = _baseWindowIcon;
                }
            }
            catch { }
            Width = 780;
            Height = 610;
            MinWidth = 680;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;
            AllowsTransparency = false;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            TaskbarItemInfo = new TaskbarItemInfo();
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;

            var rootBorder = new Border
            {
                CornerRadius = new CornerRadius(0),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Background = Brushes.White,
                SnapsToDevicePixels = true
            };
            Content = rootBorder;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootBorder.Child = root;

            var header = BuildHeader();
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var navigationPanel = new StackPanel();
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 14, 20, 8) };
            _waitingTab = BuildTab("待我处理", TaskGroup.Waiting, _waitingCount);
            _runningTab = BuildTab("进行中", TaskGroup.Running, _runningCount);
            _completedTab = BuildTab("最近完成", TaskGroup.Completed, _completedCount);
            tabs.Children.Add(_waitingTab);
            tabs.Children.Add(_runningTab);
            tabs.Children.Add(_completedTab);
            navigationPanel.Children.Add(tabs);
            var navigation = new Border
            {
                Background = Brush("#F7F7F8"), BorderBrush = Brush("#E5E5E5"),
                BorderThickness = new Thickness(0, 0, 0, 1), Child = navigationPanel
            };
            Grid.SetRow(navigation, 1);
            root.Children.Add(navigation);

            var scroll = new ScrollViewer
            {
                Margin = new Thickness(24, 8, 16, 8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                IsTabStop = false,
                FocusVisualStyle = null,
                Content = _list
            };
            KeyboardNavigation.SetTabNavigation(scroll, KeyboardNavigationMode.None);
            Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);

            var footer = new Grid { Height = 38, Background = Brushes.Transparent };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.Children.Add(new Border { Height = 1, Background = Brush("#E5E5E5"), VerticalAlignment = VerticalAlignment.Top });
            _footerLeft.Margin = new Thickness(20, 0, 0, 0);
            _footerLeft.VerticalAlignment = VerticalAlignment.Center;
            _footerLeft.FontSize = 11;
            _footerLeft.Foreground = new SolidColorBrush(Muted);
            _footerRight.Margin = new Thickness(0, 0, 20, 0);
            _footerRight.VerticalAlignment = VerticalAlignment.Center;
            _footerRight.HorizontalAlignment = HorizontalAlignment.Right;
            _footerRight.FontSize = 11;
            _footerRight.Foreground = new SolidColorBrush(Muted);
            Grid.SetColumn(_footerRight, 1);
            footer.Children.Add(_footerLeft);
            footer.Children.Add(_footerRight);
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ButtonState == MouseButtonState.Pressed) DragMove();
            };
            Closing += OnClosing;
            Loaded += OnLoaded;
            SourceInitialized += OnSourceInitialized;
            Activated += async delegate
            {
                StopTaskbarFlash();
                if (DateTime.Now - _lastHealthCheckAt > TimeSpan.FromSeconds(10)) await MaintainConnectionAsync();
            };
            PreviewKeyDown += OnPreviewKeyDown;
            _client.ThreadsReceived += OnThreadsReceived;
            _client.ConnectionChanged += OnConnectionChanged;
            _refreshTimer.Interval = TimeSpan.FromSeconds(30);
            _refreshTimer.Tick += async delegate
            {
                await MaintainConnectionAsync();
                _refreshTimer.Interval = TimeSpan.FromSeconds(IsVisible ? 30 : 90);
            };
            SetupTray();
            Render();
        }

        private UIElement BuildHeader()
        {
            var grid = new Grid { Height = 62, Background = Brushes.Transparent };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var headerDivider = new Border { Height = 1, Background = Brush("#E5E5E5"), VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumnSpan(headerDivider, 3);
            grid.Children.Add(headerDivider);

            var logo = new Image
            {
                Width = 34, Height = 34, Margin = new Thickness(15, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = Icon
            };
            grid.Children.Add(logo);

            var titleBlock = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleBlock.Children.Add(new TextBlock { Text = "Codex 任务状态", Foreground = new SolidColorBrush(Ink), FontSize = 15, FontWeight = FontWeights.SemiBold });
            Grid.SetColumn(titleBlock, 1);
            grid.Children.Add(titleBlock);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            actions.Children.Add(BuildHeaderButton("刷新", async delegate { await RefreshAsync(true); }, 64));
            actions.Children.Add(BuildWindowControlButton(false, delegate { WindowState = WindowState.Minimized; }));
            actions.Children.Add(BuildWindowControlButton(true, delegate { Close(); }));
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);
            return grid;
        }

        private Button BuildHeaderButton(string text, RoutedEventHandler click, double width)
        {
            var button = new Button
            {
                Content = text, Width = width, Height = 32, Margin = new Thickness(5, 0, 0, 0),
                Background = Brush("#F7F7F7"), Foreground = new SolidColorBrush(Ink), BorderBrush = Brush("#DEDEDE"),
                BorderThickness = new Thickness(1), FontSize = 12, Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Template = BuildButtonTemplate(7), Focusable = false, IsTabStop = false, FocusVisualStyle = null
            };
            button.Click += click;
            return button;
        }

        private Button BuildWindowControlButton(bool close, RoutedEventHandler click)
        {
            var icon = new Grid
            {
                Width = 14,
                Height = 14,
                SnapsToDevicePixels = true
            };
            if (close)
            {
                foreach (var angle in new[] { 45d, -45d })
                {
                    icon.Children.Add(new Border
                    {
                        Width = 12,
                        Height = 1.5,
                        Background = new SolidColorBrush(Ink),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = new RotateTransform(angle)
                    });
                }
            }
            else
            {
                icon.Children.Add(new Border
                {
                    Width = 11,
                    Height = 1.5,
                    Background = new SolidColorBrush(Ink),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var button = new Button
            {
                Content = icon, Width = 34, Height = 32, Margin = new Thickness(5, 0, 0, 0),
                Background = Brush("#F7F7F7"), Foreground = new SolidColorBrush(Ink), BorderBrush = Brush("#DEDEDE"),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Template = BuildButtonTemplate(7), Focusable = false, IsTabStop = false, FocusVisualStyle = null
            };
            button.Click += click;
            return button;
        }

        private Button BuildTab(string label, TaskGroup group, TextBlock count)
        {
            count.Text = "0";
            count.FontSize = 11;
            count.Foreground = new SolidColorBrush(Muted);
            count.VerticalAlignment = VerticalAlignment.Center;
            count.TextAlignment = TextAlignment.Center;
            count.MinWidth = 14;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 0) };
            content.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            content.Children.Add(count);
            var button = new Button
            {
                Content = content, Height = 32, Background = Brushes.White,
                BorderBrush = Brush("#DEDEDE"), BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Ink), HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0), Cursor = Cursors.Hand, Template = BuildButtonTemplate(12),
                Focusable = false, IsTabStop = false, FocusVisualStyle = null
            };
            button.Click += delegate { _selectedGroup = group; Render(); };
            return button;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab)
            {
                var groups = new[] { TaskGroup.Waiting, TaskGroup.Running, TaskGroup.Completed };
                var current = Array.IndexOf(groups, _selectedGroup);
                if (current < 0) current = 0;
                var reverse = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                _selectedGroup = groups[(current + (reverse ? groups.Length - 1 : 1)) % groups.Length];
                Render();
                e.Handled = true;
            }
            else if (e.Key == Key.D1 || e.Key == Key.NumPad1)
            {
                _selectedGroup = TaskGroup.Waiting;
                Render();
                e.Handled = true;
            }
            else if (e.Key == Key.D2 || e.Key == Key.NumPad2)
            {
                _selectedGroup = TaskGroup.Running;
                Render();
                e.Handled = true;
            }
            else if (e.Key == Key.D3 || e.Key == Key.NumPad3)
            {
                _selectedGroup = TaskGroup.Completed;
                Render();
                e.Handled = true;
            }
        }

        private static ControlTemplate BuildButtonTemplate(double radius)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Start();
            await RefreshAsync(true);
            _waitingNotificationBaseline.Clear();
            foreach (var item in _threads.Values.Where(x => x.Group == TaskGroup.Waiting))
                _waitingNotificationBaseline.Add(ThreadIdentity.Key(item));
            _waitingNotificationReady = true;
        }

        private async Task RefreshAsync(bool userInitiated)
        {
            if (_refreshing) return;
            _refreshing = true;
            if (userInitiated) _footerRight.Text = "正在同步 Codex 状态…";
            try
            {
                await _client.RefreshAsync(userInitiated);
            }
            catch (Exception ex) { AppLog.Error("Full refresh failed", ex); _footerRight.Text = "连接失败：" + Compact(ex.Message, 48); }
            finally { _refreshing = false; }
        }

        private async Task MaintainConnectionAsync()
        {
            _lastHealthCheckAt = DateTime.Now;
            try
            {
                await _client.MaintainAsync(IsVisible ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15));
            }
            catch (Exception ex)
            {
                AppLog.Error("Connection health check failed", ex);
            }
        }

        private void OnThreadsReceived(IList<ThreadItem> items)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                foreach (var item in items) ThreadTitleCatalog.Remember(item);
                var currentIds = new HashSet<string>(items.Select(ThreadIdentity.Key), StringComparer.OrdinalIgnoreCase);
                foreach (var removedId in _threads.Keys.Where(id => !currentIds.Contains(id)).ToList()) _threads.Remove(removedId);
                foreach (var item in items) _threads[ThreadIdentity.Key(item)] = item;
                var waiting = items.Where(x => x.Group == TaskGroup.Waiting)
                    .GroupBy(ThreadIdentity.Key).Select(x => x.First()).ToList();
                var waitingIds = new HashSet<string>(waiting.Select(ThreadIdentity.Key), StringComparer.OrdinalIgnoreCase);
                foreach (var removedId in _pendingWaitingNotifications.Keys.Where(id => !waitingIds.Contains(id)).ToList())
                    _pendingWaitingNotifications.Remove(removedId);
                if (_waitingNotificationWindow != null)
                {
                    var pending = _pendingWaitingNotifications.Values.OrderByDescending(x => x.UpdatedAt).ToList();
                    if (pending.Count == 0) ConfirmWaitingNotification();
                    else _waitingNotificationWindow.UpdateItems(pending);
                }
                var newWaiting = _waitingNotificationReady
                    ? waiting.Where(x => !_waitingNotificationBaseline.Contains(ThreadIdentity.Key(x)))
                        .OrderByDescending(x => x.UpdatedAt).ToList()
                    : new List<ThreadItem>();
                _waitingNotificationBaseline.Clear();
                foreach (var item in waiting) _waitingNotificationBaseline.Add(ThreadIdentity.Key(item));
                if (IsVisible && WindowState != WindowState.Minimized)
                {
                    Render(false);
                    ScheduleListRender();
                }
                else
                {
                    _renderPendingWhileHidden = true;
                    Render(false);
                }
                ScheduleWaitingNotification(newWaiting);
            }));
        }

        private void ScheduleListRender()
        {
            if (_listRenderScheduled) return;
            _listRenderScheduled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                _listRenderScheduled = false;
                if (IsVisible && WindowState != WindowState.Minimized) Render();
                else _renderPendingWhileHidden = true;
            }));
        }

        private void ScheduleWaitingNotification(IList<ThreadItem> items)
        {
            if (items == null || items.Count == 0) return;
            var candidates = items.Where(item => _waitingNotificationChecks.Add(ThreadIdentity.Key(item))).ToList();
            if (candidates.Count == 0) return;
            Task.Delay(1000).ContinueWith(delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    var confirmed = new List<ThreadItem>();
                    foreach (var candidate in candidates)
                    {
                        var candidateKey = ThreadIdentity.Key(candidate);
                        _waitingNotificationChecks.Remove(candidateKey);
                        ThreadItem current;
                        if (_threads.TryGetValue(candidateKey, out current) && current.Group == TaskGroup.Waiting)
                            confirmed.Add(current);
                    }
                    NotifyNewWaiting(confirmed);
                }));
            });
        }

        private void NotifyNewWaiting(IList<ThreadItem> items)
        {
            if (items == null || items.Count == 0) return;
            StartTaskbarFlash();
            foreach (var item in items) _pendingWaitingNotifications[ThreadIdentity.Key(item)] = item;
            var pending = _pendingWaitingNotifications.Values.OrderByDescending(x => x.UpdatedAt).ToList();
            if (_waitingNotificationWindow != null)
            {
                _waitingNotificationWindow.UpdateItems(pending);
                return;
            }
            _waitingNotificationWindow = new WaitingNotificationWindow(
                pending,
                delegate(IList<ThreadItem> current)
                {
                    if (current.Count == 1) OpenThread(current[0]);
                    else
                    {
                        _selectedGroup = TaskGroup.Waiting;
                        Render();
                        ShowFromTray();
                    }
                    ConfirmWaitingNotification();
                },
                ConfirmWaitingNotification);
            _waitingNotificationWindow.Closed += delegate { _waitingNotificationWindow = null; };
            _waitingNotificationWindow.Show();
        }

        private void ConfirmWaitingNotification()
        {
            _pendingWaitingNotifications.Clear();
            var window = _waitingNotificationWindow;
            _waitingNotificationWindow = null;
            if (window != null) window.Close();
        }

        private void OnConnectionChanged(string status)
        {
            Dispatcher.BeginInvoke(new Action(delegate { _footerRight.Text = status; }));
        }

        private void Render(bool rebuildList = true)
        {
            Stopwatch renderTimer = null;
            if (rebuildList) renderTimer = Stopwatch.StartNew();
            var all = _threads.Values.ToList();
            var waitingCount = all.Count(x => x.Group == TaskGroup.Waiting);
            _waitingCount.Text = waitingCount.ToString();
            _runningCount.Text = all.Count(x => x.Group == TaskGroup.Running).ToString();
            _completedCount.Text = all.Count(x => x.Group == TaskGroup.Completed).ToString();
            UpdateTaskbarWaitingBadge(waitingCount);
            _footerLeft.Text = "已扫描 " + all.Count + " 个任务（含历史记录）";

            StyleTab(_waitingTab, _selectedGroup == TaskGroup.Waiting);
            StyleTab(_runningTab, _selectedGroup == TaskGroup.Running);
            StyleTab(_completedTab, _selectedGroup == TaskGroup.Completed);
            _sectionTitle.Text = _selectedGroup == TaskGroup.Waiting ? "待我处理" : _selectedGroup == TaskGroup.Running ? "进行中" : "最近完成";

            if (!rebuildList) return;
            var visible = all.Where(x => x.Group == _selectedGroup)
                .OrderByDescending(x => x.UpdatedAt).Take(_selectedGroup == TaskGroup.Completed ? 30 : 50).ToList();
            _sectionCount.Text = "共 " + visible.Count + " 项";
            var signature = _selectedGroup + "|" + string.Join("|", visible.Select(item =>
                ThreadIdentity.Key(item) + ":" + item.Group + ":" + item.StatusText + ":" + item.UpdatedAt.Ticks + ":" + AppServerClient.DisplayTitle(item) + ":" + item.Preview));
            if (string.Equals(signature, _renderedListSignature, StringComparison.Ordinal)) return;
            _renderedListSignature = signature;
            _list.Children.Clear();
            var rebuiltCards = 0;
            var reusedCards = 0;
            if (visible.Count == 0)
            {
                _list.Children.Add(new Border
                {
                    Height = 150, Background = Brushes.White,
                    Child = new TextBlock
                    {
                        Text = _threads.Count == 0 ? "正在连接 Codex…" : "这里暂时没有任务",
                        Foreground = Brush("#8E8E8E"), FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }
            else
            {
                foreach (var item in visible)
                {
                    var key = ThreadIdentity.Key(item);
                    var cardSignature = string.Join("|", new[]
                    {
                        key, item.Group.ToString(), item.StatusText ?? "", item.UpdatedAt.Ticks.ToString(CultureInfo.InvariantCulture),
                        AppServerClient.DisplayTitle(item), item.Preview ?? "", item.Project ?? "", item.HostLabel ?? "",
                        item.NavigationTitle ?? "", item.IsSideConversation.ToString(), item.ParentThreadId ?? ""
                    });
                    CachedTaskCard cached;
                    if (!_taskCardCache.TryGetValue(key, out cached) ||
                        !string.Equals(cached.Signature, cardSignature, StringComparison.Ordinal))
                    {
                        cached = new CachedTaskCard { Signature = cardSignature, Element = BuildTaskCard(item) };
                        _taskCardCache[key] = cached;
                        rebuiltCards++;
                    }
                    else reusedCards++;
                    cached.LastUsedUtc = DateTime.UtcNow;
                    _list.Children.Add(cached.Element);
                }
            }
            var existingKeys = new HashSet<string>(_threads.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var key in _taskCardCache.Keys.Where(key => !existingKeys.Contains(key)).ToList()) _taskCardCache.Remove(key);
            if (_taskCardCache.Count > 100)
                foreach (var key in _taskCardCache.OrderBy(pair => pair.Value.LastUsedUtc)
                    .Take(_taskCardCache.Count - 100).Select(pair => pair.Key).ToList()) _taskCardCache.Remove(key);
            PerfDiagnostics.Duration("ui-list-render", renderTimer, 40,
                "visible=" + visible.Count + " rebuilt=" + rebuiltCards + " reused=" + reusedCards +
                " cachedCards=" + _taskCardCache.Count);
        }

        private void UpdateTaskbarWaitingBadge(int waitingCount)
        {
            if (_renderedWaitingBadgeCount == waitingCount) return;
            _renderedWaitingBadgeCount = waitingCount;
            if (TaskbarItemInfo == null) TaskbarItemInfo = new TaskbarItemInfo();
            // Keep the application icon unchanged and let the Windows shell place
            // the notification badge. This is the same mechanism used by Electron
            // (and ChatGPT) and remains correctly positioned across DPI changes.
            ApplyNativeTaskbarIcon(0);
            TaskbarItemInfo.Overlay = null;
            ApplyNativeTaskbarOverlay(waitingCount);
            TaskbarItemInfo.Description = waitingCount > 0 ? waitingCount + " 个待处理任务" : "没有待处理任务";
            UpdateTrayWaitingBadge(waitingCount);
        }

        private void ApplyNativeTaskbarOverlay(int waitingCount)
        {
            var window = new WindowInteropHelper(this).Handle;
            if (window == IntPtr.Zero) return;
            try
            {
                if (_nativeTaskbar == null)
                {
                    _nativeTaskbar = (NativeMethods.ITaskbarList3)new NativeMethods.TaskbarList();
                    _nativeTaskbar.HrInit();
                }
                var dpi = NativeMethods.GetDpiForWindow(window);
                if (dpi == 0) dpi = 96;
                var badgeSize = Math.Max(16,
                    NativeMethods.GetSystemMetricsForDpi(NativeMethods.SystemMetricSmallIconWidth, dpi));
                var newHandle = waitingCount > 0 ? BuildNativeTaskbarBadgeIcon(waitingCount, badgeSize) : IntPtr.Zero;
                _nativeTaskbar.SetOverlayIcon(window, newHandle,
                    waitingCount > 0 ? waitingCount + " pending tasks" : "No pending tasks");
                var oldHandle = _taskbarOverlayIconHandle;
                _taskbarOverlayIconHandle = newHandle;
                if (oldHandle != IntPtr.Zero) NativeMethods.DestroyIcon(oldHandle);
            }
            catch (Exception ex) { AppLog.Error("Apply native taskbar overlay failed", ex); }
        }

        private static IntPtr BuildNativeTaskbarBadgeIcon(int waitingCount, int size)
        {
            var label = waitingCount > 99 ? "99+" : waitingCount.ToString(CultureInfo.InvariantCulture);
            using (var bitmap = new Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.Clear(Drawing.Color.Transparent);
                using (var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x26, 0x25, 0x2D)))
                    graphics.FillEllipse(background, 0, 0, size - 1, size - 1);

                var emSize = size * (label.Length == 1 ? .72f : label.Length == 2 ? .54f : .40f);
                using (var family = new Drawing.FontFamily("Segoe UI"))
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                using (var foreground = new Drawing.SolidBrush(Drawing.Color.White))
                {
                    path.AddString(label, family, (int)Drawing.FontStyle.Bold, emSize,
                        Drawing.PointF.Empty, Drawing.StringFormat.GenericTypographic);
                    var bounds = path.GetBounds();
                    using (var transform = new System.Drawing.Drawing2D.Matrix())
                    {
                        transform.Translate(
                            (size - bounds.Width) / 2f - bounds.Left,
                            (size - bounds.Height) / 2f - bounds.Top - size * .015f);
                        path.Transform(transform);
                    }
                    graphics.FillPath(foreground, path);
                }

                return bitmap.GetHicon();
            }
        }

        private void UpdateTrayWaitingBadge(int waitingCount)
        {
            if (_tray == null) return;
            var oldBadgeIcon = _trayBadgeIcon;
            _trayBadgeIcon = waitingCount > 0 ? BuildTrayWaitingBadge() : null;
            _tray.Icon = _trayBadgeIcon ?? _appIcon ?? Drawing.SystemIcons.Application;
            _tray.Text = waitingCount > 0
                ? "Codex 项目中心 · " + waitingCount + " 个待处理任务"
                : "Codex 项目中心";
            if (oldBadgeIcon != null) oldBadgeIcon.Dispose();
        }

        private Drawing.Icon BuildTrayWaitingBadge()
        {
            var sizes = new[] { 16, 20, 24, 32 };
            var images = new List<byte[]>();
            foreach (var size in sizes)
            {
                using (var bitmap = new Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Drawing.Graphics.FromImage(bitmap))
                    {
                        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        graphics.Clear(Drawing.Color.Transparent);
                        var baseIcon = _appIcon ?? Drawing.SystemIcons.Application;
                        var iconInset = Math.Max(1, size / 16);
                        graphics.DrawIcon(baseIcon, new Drawing.Rectangle(0, iconInset, size - iconInset, size - iconInset));

                        var dotSize = Math.Max(5, (int)Math.Round(size * .31));
                        var border = size >= 24 ? 2 : 1;
                        var dotX = size - dotSize - border;
                        var dotY = border;
                        using (var white = new Drawing.SolidBrush(Drawing.Color.White))
                        using (var red = new Drawing.SolidBrush(Drawing.Color.FromArgb(250, 57, 55)))
                        {
                            graphics.FillEllipse(white, dotX - border, dotY - border, dotSize + border * 2, dotSize + border * 2);
                            graphics.FillEllipse(red, dotX, dotY, dotSize, dotSize);
                        }
                    }
                    using (var image = new MemoryStream())
                    {
                        bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
                        images.Add(image.ToArray());
                    }
                }
            }

            using (var iconStream = new MemoryStream())
            using (var writer = new BinaryWriter(iconStream, Encoding.UTF8, true))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)images.Count);
                var offset = 6 + images.Count * 16;
                for (var index = 0; index < images.Count; index++)
                {
                    writer.Write((byte)sizes[index]);
                    writer.Write((byte)sizes[index]);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write((uint)images[index].Length);
                    writer.Write((uint)offset);
                    offset += images[index].Length;
                }
                foreach (var image in images) writer.Write(image);
                writer.Flush();
                iconStream.Position = 0;
                using (var icon = new Drawing.Icon(iconStream)) return (Drawing.Icon)icon.Clone();
            }
        }

        private void ApplyNativeTaskbarIcon(int waitingCount)
        {
            var window = new WindowInteropHelper(this).Handle;
            if (window == IntPtr.Zero || _appIcon == null) return;
            var dpi = NativeMethods.GetDpiForWindow(window);
            if (dpi == 0) dpi = 96;
            var largeSize = Math.Max(32, NativeMethods.GetSystemMetricsForDpi(NativeMethods.SystemMetricLargeIconWidth, dpi));
            var smallSize = Math.Max(16, NativeMethods.GetSystemMetricsForDpi(NativeMethods.SystemMetricSmallIconWidth, dpi));
            var newLarge = BuildNativeTaskbarIcon(waitingCount, largeSize);
            var newSmall = BuildNativeTaskbarIcon(waitingCount, smallSize);
            if (newLarge == IntPtr.Zero || newSmall == IntPtr.Zero)
            {
                if (newLarge != IntPtr.Zero) NativeMethods.DestroyIcon(newLarge);
                if (newSmall != IntPtr.Zero) NativeMethods.DestroyIcon(newSmall);
                return;
            }
            NativeMethods.SendMessage(window, NativeMethods.WindowMessageSetIcon, new IntPtr(NativeMethods.IconBig), newLarge);
            NativeMethods.SendMessage(window, NativeMethods.WindowMessageSetIcon, new IntPtr(NativeMethods.IconSmall), newSmall);
            var oldLarge = _taskbarLargeIconHandle;
            var oldSmall = _taskbarSmallIconHandle;
            _taskbarLargeIconHandle = newLarge;
            _taskbarSmallIconHandle = newSmall;
            if (oldLarge != IntPtr.Zero) NativeMethods.DestroyIcon(oldLarge);
            if (oldSmall != IntPtr.Zero) NativeMethods.DestroyIcon(oldSmall);
        }

        private IntPtr BuildNativeTaskbarIcon(int waitingCount, int size)
        {
            var sourceHandle = LoadApplicationIcon(size);
            if (sourceHandle == IntPtr.Zero) return IntPtr.Zero;
            using (var bitmap = new Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            using (var sourceIcon = Drawing.Icon.FromHandle(sourceHandle))
            {
                try
                {
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.Clear(Drawing.Color.Transparent);
                    graphics.DrawIcon(sourceIcon, new Drawing.Rectangle(0, 0, size, size));
                    if (waitingCount > 0)
                    {
                        var label = waitingCount > 99 ? "99+" : waitingCount.ToString(CultureInfo.InvariantCulture);
                        // Match the Windows/ChatGPT taskbar notification badge visually.
                        // The shell scales the icon again for the taskbar, so a badge that
                        // looks correct in the source bitmap needs to occupy roughly 3/4 of
                        // the native icon canvas.
                        var diameter = Math.Max(12, (int)Math.Round(size * .76));
                        var insetX = 0;
                        var insetY = -(int)Math.Round(size * .10);
                        var badge = new Drawing.Rectangle(size - diameter - insetX, insetY, diameter, diameter);
                        var borderWidth = Math.Max(1f, size * .026f);
                        using (var white = new Drawing.SolidBrush(Drawing.Color.White))
                        using (var black = new Drawing.SolidBrush(Drawing.Color.FromArgb(32, 33, 35)))
                        {
                            graphics.FillEllipse(white, badge);
                            var inner = Drawing.RectangleF.Inflate(badge, -borderWidth, -borderWidth);
                            graphics.FillEllipse(black, inner);
                        }
                        var fontPixels = size * (label.Length == 1 ? .53f : label.Length == 2 ? .405f : .29f);
                        using (var font = new Drawing.Font("Segoe UI", fontPixels, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel))
                        using (var brush = new Drawing.SolidBrush(Drawing.Color.White))
                        using (var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center })
                            graphics.DrawString(label, font, brush, badge, format);
                    }
                    return bitmap.GetHicon();
                }
                finally { NativeMethods.DestroyIcon(sourceHandle); }
            }
        }

        private static IntPtr LoadApplicationIcon(int size)
        {
            var handles = new IntPtr[1];
            var identifiers = new uint[1];
            var count = NativeMethods.PrivateExtractIcons(
                Process.GetCurrentProcess().MainModule.FileName, 0, size, size,
                handles, identifiers, 1, 0);
            return count > 0 ? handles[0] : IntPtr.Zero;
        }

        private void StyleTab(Button button, bool selected)
        {
            button.Background = selected ? Brush("#2F9BF4") : Brushes.White;
            button.BorderBrush = selected ? Brush("#2F9BF4") : Brush("#DEDEDE");
            button.Foreground = selected ? Brushes.White : new SolidColorBrush(Ink);
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            var content = button.Content as StackPanel;
            var count = content == null || content.Children.Count < 2 ? null : content.Children[1] as TextBlock;
            if (count != null)
            {
                count.Background = Brushes.Transparent;
                count.Padding = new Thickness(0);
                count.Foreground = selected ? Brushes.White : new SolidColorBrush(Muted);
            }
        }

        private UIElement BuildTaskCard(ThreadItem item)
        {
            var itemKey = ThreadIdentity.Key(item);
            var border = new Border
            {
                Background = Brushes.White, BorderBrush = Brush("#E5E5E5"), BorderThickness = new Thickness(0, 0, 0, 1),
                Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(4, 13, 4, 13), MinHeight = 76
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var body = new StackPanel();
            var meta = new StackPanel { Orientation = Orientation.Horizontal };
            meta.Children.Add(new TextBlock { Text = item.Project, Foreground = new SolidColorBrush(Muted), FontSize = 10, FontWeight = FontWeights.SemiBold });
            meta.Children.Add(new TextBlock { Text = "  ·  " + item.HostLabel + "  ·  " + RelativeTime(item.UpdatedAt), Foreground = Brush("#8E8E8E"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(meta);
            body.Children.Add(new TextBlock { Text = AppServerClient.DisplayTitle(item), Foreground = new SolidColorBrush(Ink), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 5, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(new TextBlock { Text = item.Preview, Foreground = new SolidColorBrush(Muted), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 });
            grid.Children.Add(body);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            buttons.Children.Add(BuildActionButton("打开", true, delegate { OpenThread(ResolveCurrentThread(itemKey, item)); }));
            if (item.Group == TaskGroup.Waiting)
            {
                buttons.Children.Add(BuildActionButton("已处理", false, delegate
                {
                    var current = ResolveCurrentThread(itemKey, item);
                    _client.MarkThreadHandled(current.Id, current.HostId);
                    _footerRight.Text = "已移至最近完成";
                }));
            }
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            border.Child = grid;
            border.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ClickCount == 2) OpenThread(ResolveCurrentThread(itemKey, item));
            };
            return border;
        }

        private ThreadItem ResolveCurrentThread(string key, ThreadItem fallback)
        {
            ThreadItem current;
            return !string.IsNullOrWhiteSpace(key) && _threads.TryGetValue(key, out current) ? current : fallback;
        }

        private Button BuildActionButton(string text, bool primary, RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = text, Height = 32, MinWidth = primary ? 52 : 66, Margin = new Thickness(6, 0, 0, 0),
                Background = primary ? Brush("#202123") : Brush("#F7F7F7"),
                Foreground = primary ? Brushes.White : new SolidColorBrush(Ink),
                BorderBrush = primary ? Brush("#202123") : Brush("#DEDEDE"), BorderThickness = new Thickness(1),
                FontSize = 12, Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Center,
                Template = BuildButtonTemplate(7), Focusable = false, IsTabStop = false, FocusVisualStyle = null
            };
            button.Click += click;
            return button;
        }

        private async void OpenThread(ThreadItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id)) return;
            item = NormalizeThreadForNavigation(item);
            var navigationTimer = Stopwatch.StartNew();
            var navigationKind = item.IsSideConversation ? "side" : IsLocalHost(item.HostId) ? "local" : "remote";
            // A stale local alias and its corrected remote identity are still the same task.
            // De-duplicate by thread ID so two navigation attempts cannot compete for focus.
            var openingKey = item.Id;
            if (!_openingThreadIds.Add(openingKey))
            {
                _footerRight.Text = "任务正在打开，请稍候";
                return;
            }
            try
            {
                if (item.IsSideConversation)
                {
                    var sideOpened = await Task.Run(delegate { return TryOpenSideConversation(item); });
                    if (sideOpened)
                    {
                        _footerRight.Text = "已打开分栏任务";
                        return;
                    }
                    _footerRight.Text = "未找到对应的 Codex 分栏标签";
                    return;
                }
                if (!item.IsSideConversation && !IsLocalHost(item.HostId))
                {
                    await OpenRemoteThreadReliableAsync(item);
                    return;
                }
                if (!IsLocalHost(item.HostId) && string.IsNullOrWhiteSpace(item.NavigationTitle))
                {
                    _footerRight.Text = "缂哄皯 Codex 鐪熷疄鏍囬锛屽凡鍙栨秷瀹氫綅";
                    return;
                }
                // Stable ID-based navigation first; this does not depend on UI text,
                // sidebar expansion, DPI or screen resolution.
                var stableRequestedAt = DateTime.Now;
                if (TryRequestCodexThread(item.Id) &&
                    await Task.Run(delegate { return WaitForOpenedThread(item, stableRequestedAt, 3500); }))
                {
                    _footerRight.Text = "宸叉墦寮€浠诲姟";
                    return;
                }
                var threadUrl = "codex://threads/" + item.Id;
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = threadUrl,
                        UseShellExecute = true
                    });
                    _footerRight.Text = "正在打开任务…";
                    await Task.Delay(700);
                    if (ActivateCodexWindow())
                    {
                        _footerRight.Text = "已打开任务";
                        return;
                    }
                    await Task.Delay(800);
                    if (ActivateCodexWindow())
                    {
                        _footerRight.Text = "已打开任务";
                        return;
                    }
                    _footerRight.Text = "Codex 已接收打开请求";
                    return;
                }
                catch (Exception ex) { AppLog.Error("Thread deeplink failed", ex); }
                var copied = await TrySetClipboardTextAsync(item.Id);
                try
                {
                    if (ActivateCodexWindow()) { }
                    else Process.Start("explorer.exe", "shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App");
                    _footerRight.Text = copied ? "已打开 Codex，并复制任务 ID" : "已打开 Codex；剪贴板正忙";
                }
                catch (Exception ex) { AppLog.Error("Codex fallback open failed", ex); _footerRight.Text = copied ? "任务 ID 已复制，请在 Codex 中搜索" : "无法打开 Codex，剪贴板也正忙"; }
            }
            catch (Exception ex)
            {
                AppLog.Error("Open thread failed", ex);
                _footerRight.Text = "打开任务失败";
            }
            finally
            {
                PerfDiagnostics.Duration("open-thread-" + navigationKind, navigationTimer, 600,
                    "thread=" + item.Id + " project=" + (item.Project ?? ""));
                _openingThreadIds.Remove(openingKey);
            }
        }

        private static ThreadItem NormalizeThreadForNavigation(ThreadItem item)
        {
            if (item == null) return null;
            var resolvedHost = ThreadIdentity.Host(item.HostId);
            string catalogHost;
            if (item.IsSideConversation && !string.IsNullOrWhiteSpace(item.ParentThreadId) &&
                DesktopHostCatalog.TryResolve(item.ParentThreadId, out catalogHost))
                resolvedHost = catalogHost;
            else if (DesktopHostCatalog.TryResolve(item.Id, out catalogHost))
                resolvedHost = catalogHost;
            if (string.Equals(resolvedHost, ThreadIdentity.Host(item.HostId), StringComparison.OrdinalIgnoreCase)) return item;
            AppLog.Info("Navigation host corrected thread=" + item.Id + " from=" + ThreadIdentity.Host(item.HostId) +
                " to=" + resolvedHost + " parent=" + (item.ParentThreadId ?? ""));
            return new ThreadItem
            {
                Id = item.Id, Title = item.Title, NavigationTitle = item.NavigationTitle, Preview = item.Preview,
                Cwd = item.Cwd, Project = item.Project, HostLabel = DesktopHostCatalog.HostLabel(resolvedHost),
                HostId = resolvedHost, UpdatedAt = item.UpdatedAt, Group = item.Group, StatusText = item.StatusText,
                IsPinned = item.IsPinned, RolloutPath = item.RolloutPath, IsSideConversation = item.IsSideConversation,
                ParentThreadId = item.ParentThreadId, SideParentVerified = item.SideParentVerified,
                NavigationTitleVerified = item.NavigationTitleVerified
            };
        }

        private static bool TryActivateSidebarThread(ThreadItem item)
        {
            // One-shot fallback used only after an explicit side-conversation open request.
            // Discovery, status tracking and notifications never inspect the UI tree.
            if (item == null) return false;
            var location = ReadSidebarThreadLocation(item);
            var navigationTitle = string.IsNullOrWhiteSpace(item.NavigationTitle) ? item.Title : item.NavigationTitle;
            if (string.IsNullOrWhiteSpace(navigationTitle)) return false;
            try
            {
                foreach (var windowHandle in CodexWindowLocator.FindMainWindows())
                {
                    NativeMethods.SetForegroundWindow(windowHandle);
                    Thread.Sleep(180);
                    var root = AutomationElement.FromHandle(windowHandle);
                    EnsureSidebarThreadVisible(root, location.ProjectLabel, navigationTitle);
                    var title = navigationTitle;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    var condition = new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.NameProperty, title));
                    foreach (AutomationElement listItem in root.FindAll(TreeScope.Descendants, condition))
                    {
                        try
                        {
                            var rectangle = listItem.Current.BoundingRectangle;
                            var windowRectangle = root.Current.BoundingRectangle;
                            if (rectangle.IsEmpty || rectangle.Left > windowRectangle.Left + 380) continue;
                            if (!IsInSidebarProject(listItem, location.ProjectLabel)) continue;
                            if (TryInvokeSidebarThread(listItem)) return true;
                        }
                        catch (ElementNotAvailableException) { }
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool TryOpenSideConversation(ThreadItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id)) return false;
            AppLog.Info("Side navigation start child=" + item.Id + " parent=" + (item.ParentThreadId ?? "") +
                " project=" + (item.Project ?? "") + " title=" + (item.NavigationTitle ?? item.Title ?? ""));

            if (!ValidateSideConversationParent(item))
            {
                AppLog.Info("Side navigation parent mismatch child=" + item.Id + " parent=" + (item.ParentThreadId ?? ""));
                return false;
            }

            if (IsSideConversationSelectedForParent(item))
                return CompleteAlreadySelectedSideNavigation(item, "initial");

            if (!string.IsNullOrWhiteSpace(item.ParentThreadId))
            {
                var parentTitle = ThreadTitleCatalog.Resolve(item.ParentThreadId, item.HostId);
                var parent = new ThreadItem
                {
                    Id = item.ParentThreadId,
                    HostId = item.HostId,
                    Project = item.Project,
                    Title = parentTitle,
                    NavigationTitle = parentTitle
                };
                if (IsSideConversationSelectedForParent(item))
                {
                    return CompleteAlreadySelectedSideNavigation(item, "parent-check");
                }
                if (string.IsNullOrWhiteSpace(parentTitle))
                {
                    var discoveredParent = TryDiscoverThreadForNavigation(item.ParentThreadId, item.HostId, 3000);
                    if (discoveredParent != null)
                    {
                        parent = discoveredParent;
                        parent.HostId = item.HostId;
                        if (string.IsNullOrWhiteSpace(parent.Project)) parent.Project = item.Project;
                        parentTitle = string.IsNullOrWhiteSpace(parent.NavigationTitle) ? parent.Title : parent.NavigationTitle;
                    }
                }
                if (!IsThreadCurrentlyOpenInCodexLog(parent))
                {
                    var parentRequestedAt = DateTime.Now;
                    // Codex deep links currently resolve an unknown remote thread as local and show an
                    // error dialog. Remote navigation therefore uses the IPC-resolved sidebar entry.
                    var deepLinkRequested = IsLocalHost(item.HostId) && TryRequestCodexThread(item.ParentThreadId);
                    var parentOpened = deepLinkRequested && WaitForOpenedThread(parent, parentRequestedAt, 400);
                    var parentActivated = parentOpened || (!string.IsNullOrWhiteSpace(parentTitle) && TryActivateSidebarThread(parent));
                    if (!parentActivated || (!parentOpened && !WaitForOpenedThread(parent, parentRequestedAt, 4000)))
                    {
                        AppLog.Info("Side navigation parent failed child=" + item.Id + " parent=" +
                            item.ParentThreadId + " parentTitle=" + (parentTitle ?? "") + " deepLink=" + deepLinkRequested +
                            " activated=" + parentActivated);
                        return false;
                    }
                    if (WaitForSideConversationSelection(item, 1200))
                    {
                        ActivateCodexWindow();
                        AppLog.Info("Side navigation restored by parent child=" + item.Id + " parent=" + item.ParentThreadId);
                        return true;
                    }
                }
            }

            if (IsSideConversationSelectedForParent(item))
                return CompleteAlreadySelectedSideNavigation(item, "before-child-activation");
            var childActivated = TryActivateSideConversation(item);
            var childOpened = childActivated && WaitForSideConversationSelection(item, 1500);
            if (childOpened) ActivateCodexWindow();
            AppLog.Info("Side navigation finish child=" + item.Id + " activated=" + childActivated + " opened=" + childOpened);
            return childOpened;
        }

        private static bool CompleteAlreadySelectedSideNavigation(ThreadItem item, string stage)
        {
            var foreground = ActivateCodexWindow();
            if (!foreground)
            {
                Thread.Sleep(80);
                foreground = ActivateCodexWindow();
            }
            AppLog.Info("Side navigation already selected child=" + (item == null ? "" : item.Id) +
                " parent=" + (item == null ? "" : item.ParentThreadId ?? "") + " stage=" + stage +
                " foreground=" + foreground);
            // Selection is already authoritative; foreground activation is a presentation action
            // and must not turn a valid navigation result into a false failure.
            return true;
        }

        private static ThreadItem TryDiscoverThreadForNavigation(string threadId, string hostId, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return null;
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
            var ipc = new DesktopIpcClient(json);
            ThreadItem discovered = null;
            ipc.ThreadDiscovered += delegate(ThreadItem value, DesktopThreadStatus status)
            {
                if (value != null && string.Equals(value.Id, threadId, StringComparison.OrdinalIgnoreCase)) discovered = value;
            };
            try
            {
                ipc.ConnectAsync(Math.Min(timeoutMs, 1500)).GetAwaiter().GetResult();
                ipc.DiscoverThreadAsync(threadId, hostId ?? "local", timeoutMs).GetAwaiter().GetResult();
                if (discovered != null)
                {
                    ThreadTitleCatalog.Remember(discovered);
                    AppLog.Info("Navigation parent resolved by IPC thread=" + threadId + " host=" +
                        (hostId ?? "local") + " title=" + (discovered.NavigationTitle ?? discovered.Title ?? ""));
                }
                return discovered;
            }
            catch (Exception ex)
            {
                AppLog.Error("Navigation parent IPC discovery failed", ex);
                return null;
            }
            finally { ipc.Dispose(); }
        }

        private static bool TryRequestCodexThread(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return false;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "codex://threads/" + threadId,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Codex parent thread deeplink failed", ex);
                return false;
            }
        }

        private static bool WaitForCurrentSideConversation(ThreadItem item, int timeoutMs)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                if (IsSideConversationCurrentlyViewed(item)) return true;
                Thread.Sleep(80);
            }
            return IsSideConversationCurrentlyViewed(item);
        }

        private static bool TryActivateSideConversation(ThreadItem item)
        {
            // Codex has an internal route state named activateTabId=sidechat:<threadId>, but
            // the public deep-link and desktop IPC protocols do not expose that state yet.
            var title = string.IsNullOrWhiteSpace(item.NavigationTitle) ? item.Title : item.NavigationTitle;
            if (string.IsNullOrWhiteSpace(title)) return false;
            title = title.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? title;
            try
            {
                foreach (var windowHandle in CodexWindowLocator.FindMainWindows())
                {
                    NativeMethods.SetForegroundWindow(windowHandle);
                    Thread.Sleep(120);
                    var root = AutomationElement.FromHandle(windowHandle);
                    foreach (AutomationElement element in FindSideConversationTabs(root, title))
                    {
                        object selectionPattern;
                        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out selectionPattern))
                        {
                            var selection = (SelectionItemPattern)selectionPattern;
                            if (selection.Current.IsSelected) return true;
                            try { selection.Select(); }
                            catch (InvalidOperationException) { }
                            if (WaitForSideConversationSelection(item, 1200)) return true;
                        }
                        if (TryInvoke(element)) return true;
                        var parent = TreeWalker.ControlViewWalker.GetParent(element);
                        if (parent != null && TryInvoke(parent)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static AutomationElementCollection FindSideConversationTabs(AutomationElement root, string title)
        {
            if (root == null || string.IsNullOrWhiteSpace(title)) return null;
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                new PropertyCondition(AutomationElement.NameProperty, title));
            return root.FindAll(TreeScope.Descendants, condition);
        }

        private static bool IsSideConversationTabSelected(ThreadItem item)
        {
            var title = item == null || string.IsNullOrWhiteSpace(item.NavigationTitle) ? item == null ? null : item.Title : item.NavigationTitle;
            if (string.IsNullOrWhiteSpace(title)) return false;
            title = title.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? title;
            try
            {
                foreach (var windowHandle in CodexWindowLocator.FindMainWindows())
                {
                    var root = AutomationElement.FromHandle(windowHandle);
                    var window = root.Current.BoundingRectangle;
                    var tabs = FindSideConversationTabs(root, title);
                    if (tabs == null) continue;
                    foreach (AutomationElement tab in tabs)
                    {
                        var rectangle = tab.Current.BoundingRectangle;
                        if (rectangle.IsEmpty || rectangle.Top > window.Top + 160 || rectangle.Left < window.Left + window.Width * .5) continue;
                        object pattern;
                        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern) &&
                            ((SelectionItemPattern)pattern).Current.IsSelected) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool IsSideConversationSelectedForParent(ThreadItem item)
        {
            if (!IsSideConversationTabSelected(item)) return false;
            if (item == null || string.IsNullOrWhiteSpace(item.ParentThreadId)) return true;
            return IsThreadCurrentlyOpenInCodexLog(new ThreadItem
            {
                Id = item.ParentThreadId,
                HostId = item.HostId
            });
        }

        private static bool ValidateSideConversationParent(ThreadItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.ParentThreadId)) return false;
            if (item.SideParentVerified) return true;
            var key = ThreadIdentity.Key(item);
            lock (SideParentCacheSync)
            {
                Tuple<string, DateTime> cached;
                if (SideParentCache.TryGetValue(key, out cached) && DateTime.Now - cached.Item2 < TimeSpan.FromHours(6))
                    return string.Equals(cached.Item1, item.ParentThreadId, StringComparison.OrdinalIgnoreCase);
            }
            var discovered = TryDiscoverThreadForNavigation(item.Id, item.HostId, 1500);
            if (discovered == null || !discovered.IsSideConversation || string.IsNullOrWhiteSpace(discovered.ParentThreadId)) return false;
            lock (SideParentCacheSync)
                SideParentCache[key] = Tuple.Create(discovered.ParentThreadId, DateTime.Now);
            item.SideParentVerified = true;
            return string.Equals(discovered.ParentThreadId, item.ParentThreadId, StringComparison.OrdinalIgnoreCase);
        }

        private static bool WaitForSideConversationSelection(ThreadItem item, int timeoutMs)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            var nextUiCheck = DateTime.MinValue;
            while (DateTime.Now < deadline)
            {
                if (NavigationEventCatalog.IsCurrentlyViewed(item.Id)) return true;
                if (DateTime.Now >= nextUiCheck)
                {
                    if (IsSideConversationTabSelected(item)) return true;
                    nextUiCheck = DateTime.Now.AddMilliseconds(200);
                }
                Thread.Sleep(40);
            }
            return NavigationEventCatalog.IsCurrentlyViewed(item.Id) || IsSideConversationTabSelected(item);
        }

        private static bool EnsureSidebarThreadVisible(AutomationElement root, string projectLabel, string title)
        {
            if (root == null || string.IsNullOrWhiteSpace(projectLabel) || string.IsNullOrWhiteSpace(title)) return false;
            try
            {
                var titleCondition = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.NameProperty, title));
                var projectCondition = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
                    new PropertyCondition(AutomationElement.NameProperty, projectLabel));
                var project = root.FindFirst(TreeScope.Descendants, projectCondition);
                if (project == null)
                {
                    AppLog.Info("Sidebar reveal project missing project=" + projectLabel + " title=" + title);
                    return false;
                }
                if (project.FindFirst(TreeScope.Descendants, titleCondition) != null) return true;

                var projectButton = FindProjectExpandButton(project, projectLabel);
                if (projectButton == null)
                {
                    AppLog.Info("Sidebar reveal toggle missing project=" + projectLabel + " title=" + title);
                    return false;
                }

                bool expansionRequested;
                string expansionFailure;
                if (!TryEnsureProjectExpanded(projectButton, out expansionRequested, out expansionFailure))
                {
                    AppLog.Info("Sidebar reveal expand failed project=" + projectLabel + " title=" + title +
                        " reason=" + expansionFailure);
                    return false;
                }

                if (expansionRequested)
                {
                    var expansionDeadline = DateTime.UtcNow.AddMilliseconds(1000);
                    while (DateTime.UtcNow < expansionDeadline)
                    {
                        project = root.FindFirst(TreeScope.Descendants, projectCondition);
                        if (project != null && project.FindFirst(TreeScope.Descendants, titleCondition) != null)
                        {
                            AppLog.Info("Sidebar reveal expanded project=" + projectLabel + " title=" + title);
                            return true;
                        }
                        if (project != null && FindLoadMoreButton(project) != null) break;
                        Thread.Sleep(40);
                    }
                }

                project = root.FindFirst(TreeScope.Descendants, projectCondition) ?? project;
                if (project.FindFirst(TreeScope.Descendants, titleCondition) != null) return true;

                var loadMoreButton = FindLoadMoreButton(project);
                if (loadMoreButton == null)
                {
                    AppLog.Info("Sidebar reveal thread missing after expand project=" + projectLabel + " title=" + title);
                    return false;
                }
                if (!TryInvoke(loadMoreButton))
                {
                    AppLog.Info("Sidebar reveal load-more invoke failed project=" + projectLabel + " title=" + title);
                    return false;
                }

                var loadDeadline = DateTime.UtcNow.AddMilliseconds(1200);
                while (DateTime.UtcNow < loadDeadline)
                {
                    project = root.FindFirst(TreeScope.Descendants, projectCondition);
                    if (project != null && project.FindFirst(TreeScope.Descendants, titleCondition) != null)
                    {
                        AppLog.Info("Sidebar reveal loaded thread project=" + projectLabel + " title=" + title);
                        return true;
                    }
                    Thread.Sleep(40);
                }
                AppLog.Info("Sidebar reveal thread missing after load-more project=" + projectLabel + " title=" + title);
            }
            catch (ElementNotAvailableException)
            {
                AppLog.Info("Sidebar reveal element unavailable project=" + projectLabel + " title=" + title);
            }
            catch (InvalidOperationException ex)
            {
                AppLog.Info("Sidebar reveal automation error project=" + projectLabel + " title=" + title +
                    " reason=" + ex.Message);
            }
            return false;
        }

        private static AutomationElement FindProjectExpandButton(AutomationElement project, string projectLabel)
        {
            var exact = project.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, projectLabel + " work")));
            if (SupportsExpandCollapse(exact)) return exact;

            foreach (AutomationElement button in project.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                if (SupportsExpandCollapse(button)) return button;
            return null;
        }

        private static bool SupportsExpandCollapse(AutomationElement element)
        {
            if (element == null || !element.Current.IsEnabled) return false;
            object pattern;
            return element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern);
        }

        private static bool TryEnsureProjectExpanded(AutomationElement projectButton,
            out bool expansionRequested, out string failure)
        {
            expansionRequested = false;
            failure = "";
            try
            {
                object value;
                if (!projectButton.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out value))
                {
                    failure = "expand-collapse-pattern-unavailable";
                    return false;
                }
                var pattern = (ExpandCollapsePattern)value;
                var state = pattern.Current.ExpandCollapseState;
                if (state == ExpandCollapseState.Expanded) return true;
                if (state == ExpandCollapseState.LeafNode)
                {
                    failure = "leaf-node";
                    return false;
                }
                pattern.Expand();
                expansionRequested = true;
                return true;
            }
            catch (ElementNotAvailableException)
            {
                failure = "element-unavailable";
                return false;
            }
            catch (InvalidOperationException ex)
            {
                failure = "expand-rejected:" + ex.Message;
                return false;
            }
        }

        private static AutomationElement FindLoadMoreButton(AutomationElement project)
        {
            if (project == null) return null;
            foreach (AutomationElement button in project.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                if (string.Equals(button.Current.Name, "展开显示", StringComparison.Ordinal) && button.Current.IsEnabled)
                    return button;
            return null;
        }

        private static SidebarThreadLocation ReadSidebarThreadLocation(ThreadItem item)
        {
            var location = new SidebarThreadLocation { ProjectLabel = item.Project };
            try
            {
                var root = GlobalStateSnapshot.Read();
                var assignments = Json.GetDictionary(root, "thread-project-assignments");
                object assignmentValue;
                var assignment = assignments.TryGetValue(item.Id, out assignmentValue) ? assignmentValue as IDictionary<string, object> : null;
                var projectId = Json.GetString(assignment, "projectId");
                if (string.IsNullOrWhiteSpace(projectId)) return location;

                foreach (var value in Json.GetArray(root, "remote-projects"))
                {
                    var project = value as IDictionary<string, object>;
                    if (project != null && string.Equals(Json.GetString(project, "id"), projectId, StringComparison.OrdinalIgnoreCase))
                    {
                        location.ProjectLabel = Json.GetString(project, "label") ?? location.ProjectLabel;
                        break;
                    }
                }
            }
            catch (Exception ex) { AppLog.Error("Read sidebar thread location failed", ex); }
            return location;
        }

        private static bool IsInSidebarProject(AutomationElement item, string projectLabel)
        {
            if (string.IsNullOrWhiteSpace(projectLabel)) return true;
            try
            {
                var current = item;
                for (var depth = 0; depth < 8 && current != null; depth++)
                {
                    if (current.Current.ControlType == ControlType.Group &&
                        string.Equals(current.Current.Name, projectLabel, StringComparison.Ordinal)) return true;
                    current = TreeWalker.ControlViewWalker.GetParent(current);
                }
            }
            catch { }
            return false;
        }

        private static bool WaitForOpenedThread(ThreadItem item, DateTime requestedAt, int timeoutMs)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                if (NavigationEventCatalog.WasOpenedSince(item, requestedAt)) return true;
                Thread.Sleep(50);
            }
            return item.IsSideConversation ? IsSideConversationViewedInCodexLog(item, requestedAt) : IsOpenedThreadInCodexLog(item, requestedAt);
        }

        private static bool IsSideConversationViewedInCodexLog(ThreadItem item, DateTime requestedAt)
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Local", "Codex", "Logs");
                var marker = "thread_stream_view_activity_changed active=true conversationId=" + item.Id;
                foreach (var info in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path)).Where(file => file.LastWriteTime >= requestedAt.AddSeconds(-2))
                    .OrderByDescending(file => file.LastWriteTime).Take(3))
                    foreach (var line in ReadFileTail(info.FullName, 512 * 1024).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        DateTime timestamp;
                        if (line.Length >= 24 && DateTime.TryParse(line.Substring(0, 24), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp) &&
                            timestamp.ToLocalTime() < requestedAt.AddMilliseconds(-250)) continue;
                        return true;
                    }
            }
            catch { }
            return false;
        }

        private static bool IsThreadCurrentlyOpenInCodexLog(ThreadItem item)
        {
            if (item != null && NavigationEventCatalog.IsCurrentlyRouted(item.Id, item.HostId)) return true;
            return IsLatestActiveConversation(item == null ? null : item.Id, true);
        }

        private static bool IsSideConversationCurrentlyViewed(ThreadItem item)
        {
            if (item != null && NavigationEventCatalog.IsCurrentlyViewed(item.Id)) return true;
            return IsLatestActiveConversation(item == null ? null : item.Id, false);
        }

        private static bool IsLatestActiveConversation(string threadId, bool requireOwnerRoute)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return false;
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Local", "Codex", "Logs");
                var lines = Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTime).Take(4)
                    .SelectMany(file => ReadFileTail(file.FullName, 2 * 1024 * 1024)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    .OrderBy(line => line.Length >= 24 ? line.Substring(0, 24) : "", StringComparer.Ordinal)
                    .ToArray();
                if (requireOwnerRoute)
                {
                    var lastRoute = lines.LastOrDefault(line => line.IndexOf("ownerRoutePath=/local/", StringComparison.OrdinalIgnoreCase) >= 0);
                    return lastRoute != null && lastRoute.IndexOf("ownerRoutePath=/local/" + threadId, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                var lastActivity = lines.LastOrDefault(line =>
                    line.IndexOf("thread_stream_view_activity_changed", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    line.IndexOf("conversationId=" + threadId, StringComparison.OrdinalIgnoreCase) >= 0);
                return lastActivity != null && lastActivity.IndexOf("active=true", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static bool IsOpenedThreadInCodexLog(ThreadItem item, DateTime requestedAt)
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Local", "Codex", "Logs");
                if (!Directory.Exists(root)) return false;
                foreach (var info in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path)).Where(file => file.LastWriteTime >= requestedAt.AddSeconds(-2))
                    .OrderByDescending(file => file.LastWriteTime).Take(3))
                {
                    var text = ReadFileTail(info.FullName, 512 * 1024);
                    var idMarker = "ownerRoutePath=/local/" + item.Id;
                    foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.IndexOf(idMarker, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        DateTime timestamp;
                        if (line.Length >= 24 && DateTime.TryParse(line.Substring(0, 24), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp) &&
                            timestamp.ToLocalTime() < requestedAt.AddMilliseconds(-250)) continue;
                        if (IsLocalHost(item.HostId)) return true;
                        if (line.IndexOf("hostId=" + ThreadIdentity.Host(item.HostId), StringComparison.OrdinalIgnoreCase) >= 0 ||
                            line.IndexOf("hostId=" + Uri.EscapeDataString(ThreadIdentity.Host(item.HostId)), StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                }
            }
            catch (Exception ex) { AppLog.Error("Verify opened Codex thread failed", ex); }
            return false;
        }

        private static string ReadFileTail(string path, int maxBytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var length = (int)Math.Min(maxBytes, stream.Length);
                stream.Seek(-length, SeekOrigin.End);
                var buffer = new byte[length];
                stream.Read(buffer, 0, length);
                return Encoding.UTF8.GetString(buffer);
            }
        }

        internal static bool VerifyOpenedThreadForTest(ThreadItem item, DateTime requestedAt)
        {
            return IsOpenedThreadInCodexLog(item, requestedAt);
        }

        internal static bool OpenSideConversationForTest(ThreadItem item) { return TryOpenSideConversation(item); }

        internal static bool ActivateSidebarThreadForTest(ThreadItem item) { return TryActivateSidebarThread(item); }

        private static bool TryInvokeSidebarThread(AutomationElement listItem)
        {
            try
            {
                object scrollPattern;
                if (listItem.TryGetCurrentPattern(ScrollItemPattern.Pattern, out scrollPattern))
                    ((ScrollItemPattern)scrollPattern).ScrollIntoView();
                var name = listItem.Current.Name;
                var buttonCondition = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, name));
                var buttons = listItem.FindAll(TreeScope.Descendants, buttonCondition);

                // Codex exposes both the drag handle and the actual navigation control as
                // same-name buttons. Invoking the drag handle succeeds from UI Automation's
                // perspective, but it does not navigate anywhere. Prefer the real sidebar
                // item and explicitly ignore draggable controls.
                foreach (AutomationElement button in buttons)
                {
                    var className = button.Current.ClassName ?? "";
                    if (!button.Current.IsEnabled || className.IndexOf("cursor-grab", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (className.IndexOf("sidebar-item", StringComparison.OrdinalIgnoreCase) >= 0 && TryInvoke(button)) return true;
                }
                foreach (AutomationElement button in buttons)
                {
                    var className = button.Current.ClassName ?? "";
                    if (button.Current.IsEnabled && className.IndexOf("cursor-grab", StringComparison.OrdinalIgnoreCase) < 0 && TryInvoke(button)) return true;
                }
                return TryInvoke(listItem);
            }
            catch (ElementNotAvailableException) { return false; }
        }

        private async Task OpenRemoteThreadReliableAsync(ThreadItem item)
        {
            _footerRight.Text = "正在定位远程任务…";

            // Remote deep links do not carry hostId and are resolved as local threads by
            // Codex. This is a one-shot UI fallback used only after an explicit Open click.
            // Discovery, status synchronization and notifications remain IPC/event based.
            if (IsThreadCurrentlyOpenInCodexLog(item))
            {
                ActivateCodexWindow();
                _footerRight.Text = "已打开远程任务";
                return;
            }

            if (!item.NavigationTitleVerified)
            {
                _footerRight.Text = "正在读取任务定位信息…";
                try
                {
                    var resolved = await _client.ResolveNavigationThreadAsync(item.Id, item.HostId, 2200);
                    if (resolved != null && resolved.NavigationTitleVerified &&
                        !string.IsNullOrWhiteSpace(resolved.NavigationTitle))
                    {
                        item = resolved;
                        AppLog.Info("Remote navigation title resolved thread=" + item.Id +
                            " title=" + item.NavigationTitle);
                    }
                }
                catch (Exception ex) { AppLog.Error("Remote navigation title refresh failed", ex); }
            }

            if (!await EnsureCodexWindowAsync())
            {
                var copiedWithoutWindow = await TrySetClipboardTextAsync(item.Id);
                _footerRight.Text = copiedWithoutWindow ? "无法启动 Codex，任务 ID 已复制" : "无法启动 Codex";
                return;
            }

            var requestedAt = DateTime.Now;
            AppLog.Info("Remote navigation start thread=" + item.Id + " host=" +
                ThreadIdentity.Host(item.HostId) + " project=" + (item.Project ?? "") +
                " title=" + (item.NavigationTitle ?? item.Title ?? ""));

            var activated = await Task.Run(delegate { return TryActivateSidebarThread(item); });
            var opened = activated && await Task.Run(delegate { return WaitForOpenedThread(item, requestedAt, 4000); });

            AppLog.Info("Remote navigation finish thread=" + item.Id + " host=" +
                ThreadIdentity.Host(item.HostId) + " activated=" + activated + " opened=" + opened);

            if (opened)
            {
                _footerRight.Text = "已打开远程任务";
                return;
            }

            var copied = await TrySetClipboardTextAsync(item.Id);
            _footerRight.Text = copied ? "未定位到远程任务，任务 ID 已复制" : "未定位到远程任务";
        }

        private async Task OpenRemoteThreadAsync(ThreadItem item)
        {
            _footerRight.Text = "正在定位远程任务…";
            var stableRequestedAt = DateTime.Now;
            if (TryRequestCodexThread(item.Id) &&
                await Task.Run(delegate { return WaitForOpenedThread(item, stableRequestedAt, 3500); }))
            {
                _footerRight.Text = "已打开远程任务";
                return;
            }

            var threadUrl = "codex://threads/" + item.Id;
            var requestedAt = DateTime.Now;
            try
            {
                if (TryRequestCodexThread(item.Id) &&
                    await Task.Run(delegate { return WaitForOpenedThread(item, requestedAt, 3500); }))
                {
                    _footerRight.Text = "宸叉墦寮€杩滅▼浠诲姟";
                    return;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = threadUrl,
                    UseShellExecute = true
                });
                _footerRight.Text = "侧栏未找到，正在尝试任务链接…";
                await Task.Delay(900);
                ActivateCodexWindow();
                if (DidCodexRejectDeepLink(item.Id, requestedAt))
                {
                    var copiedAfterFailure = await TrySetClipboardTextAsync(item.Id);
                    _footerRight.Text = copiedAfterFailure ? "Codex 未加载该任务，ID 已复制" : "Codex 未加载该远程任务";
                    return;
                }
                _footerRight.Text = "已请求 Codex 打开远程任务";
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error("Remote thread deeplink failed", ex);
            }
            var copied = await TrySetClipboardTextAsync(item.Id);
            if (await EnsureCodexWindowAsync())
                _footerRight.Text = copied ? "已打开 Codex，并复制远程任务 ID" : "已打开 Codex；剪贴板正忙";
            else
                _footerRight.Text = copied ? "远程任务 ID 已复制" : "无法打开远程任务";
        }

        private static bool DidCodexRejectDeepLink(string threadId, DateTime requestedAt)
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Local", "Codex", "Logs");
                if (!Directory.Exists(root)) return false;
                foreach (var file in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .Where(info => info.LastWriteTime >= requestedAt.AddSeconds(-2))
                    .OrderByDescending(info => info.LastWriteTime)
                    .Take(4))
                {
                    var length = (int)Math.Min(file.Length, 256 * 1024);
                    using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        stream.Seek(-length, SeekOrigin.End);
                        var buffer = new byte[length];
                        stream.Read(buffer, 0, length);
                        var text = Encoding.UTF8.GetString(buffer);
                        if (text.Contains(threadId) && text.Contains("local_conversation_deep_link_lookup_failed") && text.Contains("thread not loaded"))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private sealed class CachedTaskCard
        {
            public string Signature;
            public UIElement Element;
            public DateTime LastUsedUtc;
        }

        private sealed class SidebarThreadLocation
        {
            public string ProjectLabel;
        }

        private static bool TryInvoke(AutomationElement element)
        {
            object pattern;
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) return false;
            ((InvokePattern)pattern).Invoke();
            return true;
        }

        private static bool IsLocalHost(string hostId)
        {
            return string.IsNullOrWhiteSpace(hostId) || string.Equals(hostId, "local", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<bool> EnsureCodexWindowAsync()
        {
            if (ActivateCodexWindow()) return true;
            try { Process.Start("explorer.exe", "shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App"); }
            catch { return false; }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(250);
                if (ActivateCodexWindow()) return true;
            }
            return false;
        }

        private static bool ActivateCodexWindow()
        {
            var window = CodexWindowLocator.FindMainWindow();
            if (window == IntPtr.Zero) return false;
            if (NativeMethods.IsIconic(window)) NativeMethods.ShowWindow(window, 9);
            return NativeMethods.SetForegroundWindow(window);
        }

        private static async Task<bool> TrySetClipboardTextAsync(string value)
        {
            if (Interlocked.CompareExchange(ref _clipboardWriteInProgress, 1, 0) != 0) return false;

            var completion = new TaskCompletionSource<bool>();
            var thread = new Thread(delegate()
            {
                try { completion.TrySetResult(TrySetClipboardTextCore(value)); }
                catch { completion.TrySetResult(false); }
                finally { Interlocked.Exchange(ref _clipboardWriteInProgress, 0); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            var finished = await Task.WhenAny(completion.Task, Task.Delay(500));
            return finished == completion.Task && await completion.Task;
        }

        private static bool TrySetClipboardTextCore(string value)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { Clipboard.SetText(value); return true; }
                catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40 * (attempt + 1)); }
            }
            return false;
        }

        private void SetupTray()
        {
            _tray = new Forms.NotifyIcon
            {
                Text = "Codex 项目中心",
                Icon = _appIcon ?? Drawing.SystemIcons.Application,
                Visible = true
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开项目中心", null, delegate { ShowFromTray(); });
            menu.Items.Add("刷新", null, async delegate { await RefreshAsync(true); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { _reallyClose = true; Close(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowFromTray(); };
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            ApplyNativeWindowAppearance();
            var handle = new WindowInteropHelper(this).Handle;
            _windowSource = HwndSource.FromHwnd(handle);
            if (_windowSource != null) _windowSource.AddHook(HandleWindowMessage);
            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            _taskbarButtonCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarButtonCreated");
            _globalHotkeyRegistered = NativeMethods.RegisterHotKey(
                handle, NativeMethods.ShowWindowHotkeyId,
                NativeMethods.HotkeyModifierAlt | NativeMethods.HotkeyModifierShift,
                NativeMethods.VirtualKeyW);
            if (!_globalHotkeyRegistered) _footerRight.Text = "Alt+Shift+W 全局快捷键注册失败，可能已被其他程序占用";
        }

        private IntPtr HandleWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == NativeMethods.WindowMessageHotkey && wParam.ToInt32() == NativeMethods.ShowWindowHotkeyId)
            {
                ToggleWindowVisibility();
                handled = true;
            }
            else if (message == NativeMethods.WindowMessageDpiChanged ||
                message == NativeMethods.WindowMessageDisplayChange ||
                message == NativeMethods.WindowMessageSettingChange ||
                message == NativeMethods.WindowMessageThemeChanged ||
                message == NativeMethods.WindowMessageDwmCompositionChanged ||
                message == _taskbarCreatedMessage ||
                message == _taskbarButtonCreatedMessage)
            {
                QueueWindowBoundsCorrection();
                QueueTaskbarBadgeRefresh();
            }
            return IntPtr.Zero;
        }

        private void QueueTaskbarBadgeRefresh()
        {
            var generation = ++_taskbarBadgeRefreshGeneration;
            _renderedWaitingBadgeCount = -1;
            ScheduleTaskbarBadgeRefresh(generation, 120);
            // Explorer and remote-desktop layout changes can finish after the first
            // notification. Re-submit once more after the taskbar has settled.
            ScheduleTaskbarBadgeRefresh(generation, 850);
        }

        private void ScheduleTaskbarBadgeRefresh(int generation, int delayMilliseconds)
        {
            Task.Delay(delayMilliseconds).ContinueWith(delegate
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                {
                    if (generation != _taskbarBadgeRefreshGeneration) return;
                    var waitingCount = _threads.Values.Count(item => item.Group == TaskGroup.Waiting);
                    _renderedWaitingBadgeCount = -1;
                    UpdateTaskbarWaitingBadge(waitingCount);
                }));
            });
        }

        private void QueueWindowBoundsCorrection()
        {
            if (_windowBoundsCorrectionPending) return;
            _windowBoundsCorrectionPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                _windowBoundsCorrectionPending = false;
                CorrectWindowBoundsToCurrentScreen();
            }));
        }

        private void CorrectWindowBoundsToCurrentScreen()
        {
            if (!IsVisible || WindowState != WindowState.Normal) return;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || NativeMethods.IsZoomed(handle)) return;

            var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return;
            var monitorInfo = new NativeMethods.MonitorInfo
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MonitorInfo))
            };
            NativeMethods.NativeRect windowRect;
            if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo) ||
                !NativeMethods.GetWindowRect(handle, out windowRect)) return;

            var work = monitorInfo.WorkArea;
            var workWidth = Math.Max(1, work.Right - work.Left);
            var workHeight = Math.Max(1, work.Bottom - work.Top);
            var width = Math.Min(Math.Max(1, windowRect.Right - windowRect.Left), workWidth);
            var height = Math.Min(Math.Max(1, windowRect.Bottom - windowRect.Top), workHeight);
            var left = Math.Min(Math.Max(windowRect.Left, work.Left), work.Right - width);
            var top = Math.Min(Math.Max(windowRect.Top, work.Top), work.Bottom - height);

            if (left == windowRect.Left && top == windowRect.Top &&
                width == windowRect.Right - windowRect.Left && height == windowRect.Bottom - windowRect.Top) return;
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, left, top, width, height,
                NativeMethods.SetWindowPosNoActivate | NativeMethods.SetWindowPosNoZOrder | NativeMethods.SetWindowPosNoOwnerZOrder);
        }

        private void ToggleWindowVisibility()
        {
            if (IsVisible && WindowState != WindowState.Minimized && IsActive)
            {
                WindowState = WindowState.Minimized;
                return;
            }
            ShowFromTray();
        }

        private void HideToTray(bool showNotification)
        {
            Hide();
            _refreshTimer.Interval = TimeSpan.FromSeconds(90);
            if (showNotification)
                _tray.ShowBalloonTip(800, "项目中心仍在运行", "可按 Alt+Shift+W 或从系统托盘再次打开。", Forms.ToolTipIcon.Info);
        }

        private void UnregisterGlobalHotkey()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (_globalHotkeyRegistered && handle != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(handle, NativeMethods.ShowWindowHotkeyId);
                _globalHotkeyRegistered = false;
            }
            if (_windowSource != null)
            {
                _windowSource.RemoveHook(HandleWindowMessage);
                _windowSource = null;
            }
            if (_taskbarLargeIconHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_taskbarLargeIconHandle);
                _taskbarLargeIconHandle = IntPtr.Zero;
            }
            if (_taskbarSmallIconHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_taskbarSmallIconHandle);
                _taskbarSmallIconHandle = IntPtr.Zero;
            }
            if (_taskbarOverlayIconHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_taskbarOverlayIconHandle);
                _taskbarOverlayIconHandle = IntPtr.Zero;
            }
            if (_nativeTaskbar != null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_nativeTaskbar); }
                catch { }
                _nativeTaskbar = null;
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            _refreshTimer.Interval = TimeSpan.FromSeconds(30);
            Activate();
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) NativeMethods.SetForegroundWindow(handle);
            QueueWindowBoundsCorrection();
            StopTaskbarFlash();
            if (_renderPendingWhileHidden)
            {
                _renderPendingWhileHidden = false;
                _renderedListSignature = "";
                Render();
            }
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async delegate
            {
                await Task.Delay(180);
                await RefreshAsync(false);
            }));
        }

        private void StartTaskbarFlash()
        {
            if (_taskbarFlashing || (IsVisible && IsActive)) return;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var info = new NativeMethods.FlashInfo
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.FlashInfo)),
                Window = handle,
                Flags = NativeMethods.FlashAll | NativeMethods.FlashTimerNoForeground,
                Count = 0,
                Timeout = 0
            };
            _taskbarFlashing = NativeMethods.FlashWindowEx(ref info);
        }

        private void StopTaskbarFlash()
        {
            if (!_taskbarFlashing) return;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                var info = new NativeMethods.FlashInfo
                {
                    Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.FlashInfo)),
                    Window = handle,
                    Flags = NativeMethods.FlashStop,
                    Count = 0,
                    Timeout = 0
                };
                NativeMethods.FlashWindowEx(ref info);
            }
            _taskbarFlashing = false;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_reallyClose)
            {
                e.Cancel = true;
                HideToTray(true);
                return;
            }
            _refreshTimer.Stop();
            UnregisterGlobalHotkey();
            _client.Dispose();
            if (_waitingNotificationWindow != null) _waitingNotificationWindow.Close();
            _tray.Visible = false;
            _tray.Dispose();
            if (_trayBadgeIcon != null) _trayBadgeIcon.Dispose();
            if (_appIcon != null) _appIcon.Dispose();
            Application.Current.Shutdown();
        }

        private static Brush Brush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }

        private void ApplyNativeWindowAppearance()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var preference = 2;
            NativeMethods.DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
        private static string Compact(string value, int max) { value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); return value.Length <= max ? value : value.Substring(0, max - 1) + "…"; }
        private static string RelativeTime(DateTime time)
        {
            var span = DateTime.Now - time;
            if (span.TotalSeconds < 60) return "刚刚";
            if (span.TotalMinutes < 60) return ((int)span.TotalMinutes) + " 分钟前";
            if (span.TotalHours < 24) return ((int)span.TotalHours) + " 小时前";
            if (span.TotalDays < 7) return ((int)span.TotalDays) + " 天前";
            return time.ToString("M月d日");
        }
    }

    internal sealed class WaitingNotificationWindow : Window
    {
        private readonly TextBlock _title = new TextBlock();
        private readonly TextBlock _message = new TextBlock();
        private readonly Button _openButton = new Button();
        private readonly Action<IList<ThreadItem>> _open;
        private readonly Action _confirm;
        private IList<ThreadItem> _items;

        public WaitingNotificationWindow(IList<ThreadItem> items, Action<IList<ThreadItem>> open, Action confirm)
        {
            _open = open;
            _confirm = confirm;
            Width = 370;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            SourceInitialized += delegate { ApplyNotificationWindowStyle(); };

            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(218, 218, 218)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18),
                Margin = new Thickness(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18, ShadowDepth = 3, Opacity = .2, Color = Colors.Black
                }
            };
            var content = new StackPanel();
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromRgb(32, 33, 35)),
                Child = new TextBlock
                {
                    Text = "!", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            heading.Children.Add(icon);
            _title.FontSize = 15;
            _title.FontWeight = FontWeights.SemiBold;
            _title.Foreground = new SolidColorBrush(Color.FromRgb(32, 33, 35));
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.Margin = new Thickness(11, 0, 0, 0);
            Grid.SetColumn(_title, 1);
            heading.Children.Add(_title);
            content.Children.Add(heading);

            _message.Margin = new Thickness(0, 13, 0, 16);
            _message.Foreground = new SolidColorBrush(Color.FromRgb(92, 92, 92));
            _message.FontSize = 12;
            _message.TextWrapping = TextWrapping.Wrap;
            _message.MaxHeight = 76;
            content.Children.Add(_message);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var confirmButton = CreateButton("我知道了", false);
            confirmButton.Click += delegate { _confirm(); };
            StyleButton(_openButton, true);
            _openButton.Click += delegate { _open(_items); };
            actions.Children.Add(confirmButton);
            actions.Children.Add(_openButton);
            content.Children.Add(actions);
            card.Child = content;
            Content = card;

            Loaded += delegate { MoveToCorner(); };
            UpdateItems(items);
        }

        private void ApplyNotificationWindowStyle()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var style = NativeMethods.GetWindowExtendedStyle(handle);
            style &= ~NativeMethods.WindowExtendedStyleAppWindow;
            style |= NativeMethods.WindowExtendedStyleToolWindow | NativeMethods.WindowExtendedStyleNoActivate;
            NativeMethods.SetWindowExtendedStyle(handle, style);
        }

        public void UpdateItems(IList<ThreadItem> items)
        {
            _items = items.ToList();
            _title.Text = _items.Count == 1 ? "有新的待处理任务" : "有 " + _items.Count + " 个新的待处理任务";
            _message.Text = _items.Count == 1
                ? NotificationText(_items[0])
                : string.Join("\n", _items.Take(3).Select(x => "• " + NotificationText(x))) + (_items.Count > 3 ? "\n• 还有其他任务…" : "");
            _openButton.Content = _items.Count == 1 ? "打开任务" : "查看待处理";
            if (IsLoaded)
            {
                UpdateLayout();
                MoveToCorner();
            }
        }

        private void MoveToCorner()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 14;
            Top = area.Bottom - ActualHeight - 14;
        }

        private static string NotificationText(ThreadItem item)
        {
            var project = string.IsNullOrWhiteSpace(item.Project) ? "Codex" : item.Project.Trim();
            var task = CleanTaskText(AppServerClient.DisplayTitle(item));
            if (IsSystemPromptText(task)) task = CleanTaskText(item.Preview);
            if (IsSystemPromptText(task) || string.IsNullOrWhiteSpace(task)) task = "任务等待你处理";
            if (task.StartsWith(project + " · ", StringComparison.OrdinalIgnoreCase)) return task;
            return project + " · " + task;
        }

        private static string CleanTaskText(string value)
        {
            var text = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            text = Regex.Replace(text, @"^#+\s*", "");
            text = Regex.Replace(text, @"!\[[^\]]*\]\([^\)]*\)", "");
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]*\)", "$1");
            text = Regex.Replace(text, @"[`*_]+", "");
            text = Regex.Replace(text, @"\s+", " ").Trim(' ', '-', ':', '：');
            return text.Length <= 64 ? text : text.Substring(0, 63) + "…";
        }

        private static bool IsSystemPromptText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var text = value.ToLowerInvariant();
            return text.Contains("files mentioned by the user")
                || text.Contains("my request for codex")
                || text.Contains("codex-clipboard-")
                || text.Contains("image name=")
                || text.Contains("in-app-browser-context")
                || text.Contains("environment_context")
                || text.Contains("workspace_roots");
        }

        private static Button CreateButton(string text, bool primary)
        {
            var button = new Button { Content = text };
            StyleButton(button, primary);
            return button;
        }

        private static void StyleButton(Button button, bool primary)
        {
            button.Height = 34;
            button.MinWidth = 78;
            button.Margin = new Thickness(7, 0, 0, 0);
            button.Padding = new Thickness(13, 0, 13, 0);
            button.Background = primary ? new SolidColorBrush(Color.FromRgb(32, 33, 35)) : new SolidColorBrush(Color.FromRgb(247, 247, 247));
            button.Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(32, 33, 35));
            button.BorderBrush = primary ? new SolidColorBrush(Color.FromRgb(32, 33, 35)) : new SolidColorBrush(Color.FromRgb(218, 218, 218));
            button.BorderThickness = new Thickness(1);
            button.Cursor = Cursors.Hand;
            button.Focusable = false;
            button.IsTabStop = false;
            button.FocusVisualStyle = null;
        }
    }

    internal sealed class AppServerClient : IDisposable
    {
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
        private readonly Dictionary<string, ThreadItem> _cache = new Dictionary<string, ThreadItem>();
        private readonly object _sync = new object();
        private readonly object _remoteSync = new object();
        private List<ThreadItem> _remoteCache = new List<ThreadItem>();
        private DateTime _remoteCacheAt = DateTime.MinValue;
        private Task _remoteRefreshTask;
        private Task _eventRefreshTask;
        private bool _eventRefreshDirty;
        private readonly object _eventRefreshSync = new object();
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _ipcConnectGate = new SemaphoreSlim(1, 1);
        private DateTime _lastFullRefreshAt = DateTime.MinValue;
        private readonly HashSet<string> _archivedThreadIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _completedAwaitingReview = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _handledAttention = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly AwaitingReviewStore _awaitingReviewStore;
        private readonly AwaitingReviewStore _handledAttentionStore;
        private readonly HashSet<string> _desktopCompletedThreads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _desktopRunningThreads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _desktopRunningConfirmedThreads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _desktopRunningStartedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly object _desktopDiscoverySync = new object();
        private readonly Dictionary<string, string> _pendingDesktopDiscoveries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _scheduledDesktopDiscoveries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher _sessionWatcher;
        private CodexDesktopLogMonitor _desktopLogMonitor;
        private DesktopIpcClient _ipc;
        private bool _disposed;
        public event Action<IList<ThreadItem>> ThreadsReceived;
        public event Action<string> ConnectionChanged;

        public AppServerClient(string stateDirectory = null)
        {
            var directory = stateDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexProjectCenter");
            _awaitingReviewStore = new AwaitingReviewStore(Path.Combine(directory, "awaiting-review.json"));
            _handledAttentionStore = new AwaitingReviewStore(Path.Combine(directory, "handled-attention.json"));
            foreach (var pair in _awaitingReviewStore.Load()) _completedAwaitingReview[pair.Key] = pair.Value;
            foreach (var pair in _handledAttentionStore.Load()) _handledAttention[pair.Key] = pair.Value;
            SetupSessionWatcher();
            SetupDesktopLogMonitor();
        }

        public async Task RefreshAsync(bool forceRemote = false)
        {
            if (_disposed) return;
            await _refreshGate.WaitAsync();
            var performanceTimer = Stopwatch.StartNew();
            try
            {
                if (_disposed) return;
                RaiseConnection("已加载本地状态 · 正在同步实时状态…");
                await EnsureIpcAsync();
                var snapshot = await ReadStateBridgeAsync(forceRemote);
                ReplaceCache(snapshot.Threads);
                RaiseThreads(GetSnapshot());
                _lastFullRefreshAt = DateTime.Now;
                RaiseConnection((snapshot.RemoteAvailable ? "本机 + 远程" : "本机") + "实时连接 · " + DateTime.Now.ToString("HH:mm:ss"));
            }
            catch (Exception ex)
            {
                AppLog.Error("AppServer full refresh failed", ex);
                throw;
            }
            finally
            {
                PerfDiagnostics.Duration("full-refresh", performanceTimer, 1500,
                    "forceRemote=" + forceRemote);
                _refreshGate.Release();
            }
        }

        public async Task MaintainAsync(TimeSpan fullRefreshInterval)
        {
            if (_disposed) return;
            DesktopIpcClient ipc;
            lock (_sync) ipc = _ipc;
            if (ipc == null || !ipc.IsConnected || DateTime.Now - _lastFullRefreshAt >= fullRefreshInterval)
            {
                await RefreshAsync(false);
                return;
            }
            RaiseConnection("实时连接 · " + DateTime.Now.ToString("HH:mm:ss"));
        }

        public async Task RefreshThreadAsync(string threadId, string hostId)
        {
            if (_disposed || string.IsNullOrWhiteSpace(threadId)) return;
            await EnsureIpcAsync();
            DesktopIpcClient ipc;
            lock (_sync) ipc = _ipc;
            if (ipc == null || !ipc.IsConnected) return;
            var status = await ipc.ReadThreadStatusAsync(threadId, hostId ?? "local", 1800);
            if (status != null) OnIpcStatusChanged(threadId, hostId ?? "local", status);
        }

        public async Task<ThreadItem> ResolveNavigationThreadAsync(string threadId, string hostId, int timeoutMs)
        {
            if (_disposed || string.IsNullOrWhiteSpace(threadId)) return null;
            await EnsureIpcAsync();
            DesktopIpcClient ipc;
            lock (_sync) ipc = _ipc;
            if (ipc == null || !ipc.IsConnected) return null;
            await ipc.DiscoverThreadAsync(threadId, hostId ?? "local", timeoutMs);
            lock (_sync)
            {
                ThreadItem item;
                return _cache.TryGetValue(ThreadIdentity.Key(threadId, hostId), out item) ? CloneThread(item) : null;
            }
        }

        public void MarkThreadHandled(string threadId, string hostId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return;
            var changed = false;
            lock (_sync)
            {
                var key = ThreadIdentity.Key(threadId, hostId);
                ThreadItem item;
                if (!_cache.TryGetValue(key, out item)) return;
                _handledAttention[key] = item.UpdatedAt == DateTime.MinValue ? DateTime.Now : item.UpdatedAt;
                SaveHandledAttentionStateLocked();
                if (_completedAwaitingReview.Remove(key)) SaveAwaitingReviewStateLocked();
                if (item.Group == TaskGroup.Waiting)
                {
                    item.Group = TaskGroup.Completed;
                    item.StatusText = "已处理";
                    changed = true;
                }
            }
            AppLog.Info("Thread handled thread=" + threadId + " host=" + ThreadIdentity.Host(hostId));
            if (changed) RaiseThreads(GetSnapshot());
        }

        private void ReplaceCache(IEnumerable<ThreadItem> items)
        {
            lock (_sync)
            {
                var sideConversations = _cache.Values.Where(item => item.IsSideConversation &&
                    (item.Group == TaskGroup.Running || item.Group == TaskGroup.Waiting || DateTime.Now - item.UpdatedAt < TimeSpan.FromDays(1)))
                    .Select(CloneThread).ToList();
                _cache.Clear();
                foreach (var item in items)
                {
                    ApplyCompletionAwaitingReview(item);
                    ApplyDesktopRunningOverrideLocked(item);
                    ApplyHandledAttentionLocked(item);
                    _cache[ThreadIdentity.Key(item)] = item;
                }
                foreach (var item in sideConversations)
                    if (!_cache.ContainsKey(ThreadIdentity.Key(item))) _cache[ThreadIdentity.Key(item)] = item;
                NormalizeCachedThreadIdentitiesLocked();
            }
        }

        private string ResolveAuthoritativeHostLocked(ThreadItem item)
        {
            if (item == null) return "local";
            var suppliedHost = ThreadIdentity.Host(item.HostId);
            if (!IsLocalHostId(suppliedHost)) return suppliedHost;

            string resolvedHost;
            if (item.IsSideConversation && !string.IsNullOrWhiteSpace(item.ParentThreadId))
            {
                if (DesktopHostCatalog.TryResolve(item.ParentThreadId, out resolvedHost)) return resolvedHost;
                var parent = _cache.Values
                    .Where(value => string.Equals(value.Id, item.ParentThreadId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(value => IsLocalHostId(value.HostId) ? 1 : 0)
                    .FirstOrDefault();
                if (parent != null) return ThreadIdentity.Host(parent.HostId);
            }
            if (DesktopHostCatalog.TryResolve(item.Id, out resolvedHost)) return resolvedHost;
            var remoteAlias = _cache.Values.FirstOrDefault(value =>
                string.Equals(value.Id, item.Id, StringComparison.OrdinalIgnoreCase) && !IsLocalHostId(value.HostId));
            return remoteAlias == null ? suppliedHost : ThreadIdentity.Host(remoteAlias.HostId);
        }

        private void NormalizeCachedThreadIdentitiesLocked()
        {
            foreach (var pair in _cache.ToList())
            {
                var item = pair.Value;
                if (item == null || !item.IsSideConversation) continue;
                var resolvedHost = ResolveAuthoritativeHostLocked(item);
                if (string.Equals(ThreadIdentity.Host(item.HostId), resolvedHost, StringComparison.OrdinalIgnoreCase)) continue;
                var oldKey = pair.Key;
                var newKey = ThreadIdentity.Key(item.Id, resolvedHost);
                _cache.Remove(oldKey);
                MigrateIdentityStateLocked(oldKey, newKey);
                item.HostId = resolvedHost;
                item.HostLabel = DesktopHostCatalog.HostLabel(resolvedHost);
                ThreadItem existing;
                if (_cache.TryGetValue(newKey, out existing)) MergeThreadDetails(existing, item);
                else _cache[newKey] = item;
                ThreadTitleCatalog.Remember(item);
                if (!IsLocalHostId(resolvedHost)) ThreadTitleCatalog.RemoveLocalAlias(item.Id);
                AppLog.Info("Side thread host promoted thread=" + item.Id + " from=" +
                    oldKey.Split('\n')[0] + " to=" + resolvedHost + " parent=" + (item.ParentThreadId ?? ""));
            }

            foreach (var group in _cache.Values.GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).ToList())
            {
                var authoritative = group.OrderBy(value => IsLocalHostId(value.HostId) ? 1 : 0)
                    .ThenByDescending(value => value.UpdatedAt).First();
                ReconcileThreadAliasesLocked(authoritative.Id, authoritative.HostId);
            }
        }

        private void ReconcileThreadAliasesLocked(string threadId, string authoritativeHostId)
        {
            var authoritativeKey = ThreadIdentity.Key(threadId, authoritativeHostId);
            foreach (var alias in _cache.Where(pair =>
                string.Equals(pair.Value.Id, threadId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(pair.Key, authoritativeKey, StringComparison.OrdinalIgnoreCase) &&
                (IsLocalHostId(pair.Value.HostId) || IsLocalHostId(authoritativeHostId))).ToList())
            {
                ThreadItem authoritative;
                if (_cache.TryGetValue(authoritativeKey, out authoritative)) MergeThreadDetails(authoritative, alias.Value);
                MigrateIdentityStateLocked(alias.Key, authoritativeKey);
                _cache.Remove(alias.Key);
                AppLog.Info("Thread identity alias removed thread=" + threadId + " aliasHost=" +
                    ThreadIdentity.Host(alias.Value.HostId) + " authoritativeHost=" + ThreadIdentity.Host(authoritativeHostId));
            }
        }

        private static void MergeThreadDetails(ThreadItem target, ThreadItem source)
        {
            if (target == null || source == null) return;
            if (source.IsSideConversation)
            {
                if (!string.IsNullOrWhiteSpace(source.Title)) target.Title = source.Title;
                if (!string.IsNullOrWhiteSpace(source.NavigationTitle)) target.NavigationTitle = source.NavigationTitle;
                target.NavigationTitleVerified = target.NavigationTitleVerified || source.NavigationTitleVerified;
            }
            if (string.IsNullOrWhiteSpace(target.Preview)) target.Preview = source.Preview;
            if (string.IsNullOrWhiteSpace(target.Cwd)) target.Cwd = source.Cwd;
            if (string.IsNullOrWhiteSpace(target.Project)) target.Project = source.Project;
            if (string.IsNullOrWhiteSpace(target.ParentThreadId)) target.ParentThreadId = source.ParentThreadId;
            target.IsSideConversation = target.IsSideConversation || source.IsSideConversation;
            target.SideParentVerified = target.SideParentVerified || source.SideParentVerified;
            if (source.UpdatedAt > target.UpdatedAt)
            {
                target.UpdatedAt = source.UpdatedAt;
                target.Group = source.Group;
                target.StatusText = source.StatusText;
            }
        }

        private void MigrateIdentityStateLocked(string oldKey, string newKey)
        {
            if (string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase)) return;
            if (MoveTimestamp(_completedAwaitingReview, oldKey, newKey)) SaveAwaitingReviewStateLocked();
            if (MoveTimestamp(_handledAttention, oldKey, newKey)) SaveHandledAttentionStateLocked();
            MoveSet(_desktopCompletedThreads, oldKey, newKey);
            MoveSet(_desktopRunningThreads, oldKey, newKey);
            MoveSet(_desktopRunningConfirmedThreads, oldKey, newKey);
            MoveSet(_archivedThreadIds, oldKey, newKey);
            MoveTimestamp(_desktopRunningStartedAt, oldKey, newKey);
        }

        private static bool MoveTimestamp(IDictionary<string, DateTime> values, string oldKey, string newKey)
        {
            DateTime oldValue;
            if (!values.TryGetValue(oldKey, out oldValue)) return false;
            DateTime current;
            if (!values.TryGetValue(newKey, out current) || oldValue > current) values[newKey] = oldValue;
            values.Remove(oldKey);
            return true;
        }

        private static void MoveSet(ISet<string> values, string oldKey, string newKey)
        {
            if (!values.Remove(oldKey)) return;
            values.Add(newKey);
        }

        private async Task EnsureIpcAsync()
        {
            DesktopIpcClient current;
            lock (_sync) current = _ipc;
            if (current != null && current.IsConnected) return;
            await _ipcConnectGate.WaitAsync();
            try
            {
                if (_disposed) return;
                lock (_sync) current = _ipc;
                if (current != null && current.IsConnected) return;
                var candidate = new DesktopIpcClient(_json);
                candidate.StatusChanged += OnIpcStatusChanged;
                candidate.ReadStateChanged += OnIpcReadStateChanged;
                candidate.ArchivedChanged += OnIpcArchivedChanged;
                candidate.TitleChanged += OnIpcTitleChanged;
                candidate.ThreadDiscovered += OnIpcThreadDiscovered;
                candidate.RefreshSuggested += ScheduleEventRefresh;
                candidate.ConnectionReset += OnIpcConnectionReset;
                try { await candidate.ConnectAsync(5000); }
                catch (Exception ex) { AppLog.Error("IPC connection failed", ex); candidate.Dispose(); throw; }
                DesktopIpcClient previous;
                lock (_sync)
                {
                    if (_disposed) { candidate.Dispose(); return; }
                    previous = _ipc;
                    _ipc = candidate;
                }
                if (previous != null && !ReferenceEquals(previous, candidate)) previous.Dispose();
            }
            finally { _ipcConnectGate.Release(); }
        }

        private void SetupSessionWatcher()
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
                if (!Directory.Exists(root)) return;
                _sessionWatcher = new FileSystemWatcher(root, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                _sessionWatcher.Created += OnSessionCatalogChanged;
                _sessionWatcher.Renamed += OnSessionCatalogChanged;
                _sessionWatcher.Deleted += OnSessionCatalogChanged;
                _sessionWatcher.Error += delegate { ScheduleEventRefresh(); };
                _sessionWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { AppLog.Error("Session watcher setup failed", ex); }
        }

        private void SetupDesktopLogMonitor()
        {
            try
            {
                _desktopLogMonitor = new CodexDesktopLogMonitor();
                _desktopLogMonitor.ThreadSeen += OnDesktopThreadSeen;
                _desktopLogMonitor.ThreadStarted += OnDesktopThreadStarted;
                _desktopLogMonitor.ThreadCompleted += OnDesktopThreadCompleted;
                _desktopLogMonitor.ThreadViewed += OnIpcThreadViewed;
                _desktopLogMonitor.ThreadRenamed += OnDesktopThreadRenamed;
                _desktopLogMonitor.Start();
            }
            catch (Exception ex) { AppLog.Error("Desktop log monitor setup failed", ex); }
        }

        private void OnDesktopThreadSeen(string threadId, string hostId)
        {
            if (_disposed || string.IsNullOrWhiteSpace(threadId)) return;
            var resolvedHost = DesktopHostCatalog.Resolve(threadId, hostId);
            lock (_desktopDiscoverySync)
            {
                string pendingHost;
                if (!_pendingDesktopDiscoveries.TryGetValue(threadId, out pendingHost) ||
                    string.Equals(pendingHost, "local", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(resolvedHost, "local", StringComparison.OrdinalIgnoreCase))
                    _pendingDesktopDiscoveries[threadId] = resolvedHost;
                if (!_scheduledDesktopDiscoveries.Add(threadId)) return;
            }
            Task.Run(async delegate
            {
                try
                {
                    await Task.Delay(250);
                    string discoveryHost;
                    lock (_desktopDiscoverySync)
                    {
                        if (!_pendingDesktopDiscoveries.TryGetValue(threadId, out discoveryHost)) discoveryHost = resolvedHost;
                        _pendingDesktopDiscoveries.Remove(threadId);
                        _scheduledDesktopDiscoveries.Remove(threadId);
                    }
                    await EnsureIpcAsync();
                    DesktopIpcClient ipc;
                    lock (_sync) ipc = _ipc;
                    if (ipc != null && ipc.IsConnected) await ipc.DiscoverThreadAsync(threadId, discoveryHost, 3500);
                }
                catch (Exception ex) { AppLog.Error("Discover desktop thread failed", ex); }
            });
        }

        private void OnDesktopThreadCompleted(string threadId, string hostId)
        {
            if (_disposed || string.IsNullOrWhiteSpace(threadId)) return;
            hostId = DesktopHostCatalog.Resolve(threadId, hostId);
            var changed = false;
            lock (_sync)
            {
                var key = ThreadIdentity.Key(threadId, hostId);
                if (_handledAttention.Remove(key)) SaveHandledAttentionStateLocked();
                _desktopCompletedThreads.Add(key);
                _desktopRunningThreads.Remove(key);
                _desktopRunningConfirmedThreads.Remove(key);
                _desktopRunningStartedAt.Remove(key);
                ThreadItem item;
                if (_cache.TryGetValue(key, out item))
                {
                    _completedAwaitingReview[key] = DateTime.Now;
                    SaveAwaitingReviewStateLocked();
                    item.Group = TaskGroup.Waiting;
                    item.StatusText = "鏈夋柊缁撴灉";
                    item.UpdatedAt = DateTime.Now;
                    changed = true;
                }
            }
            AppLog.Info("Desktop completion received thread=" + threadId + " host=" + (hostId ?? "local") + " immediate=" + changed);
            if (changed) RaiseThreads(GetSnapshot());
            Task.Run(async delegate
            {
                try
                {
                    await EnsureIpcAsync();
                    DesktopIpcClient ipc;
                    lock (_sync) ipc = _ipc;
                    if (ipc == null || !ipc.IsConnected) return;
                    ipc.InvalidateThreadStatus(threadId);
                    var status = await ipc.ReadThreadStatusAsync(threadId, hostId ?? "local", 1800);
                    if (status != null) OnIpcStatusChanged(threadId, hostId ?? "local", status);
                    await ipc.DiscoverThreadAsync(threadId, hostId ?? "local", 2500);
                }
                catch (Exception ex) { AppLog.Error("Refresh completed desktop thread failed", ex); }
            });
        }

        private void OnDesktopThreadRenamed(string threadId, string hostId)
        {
            if (_disposed || string.IsNullOrWhiteSpace(threadId)) return;
            hostId = DesktopHostCatalog.Resolve(threadId, hostId);
            AppLog.Info("Desktop rename received thread=" + threadId + " host=" + (hostId ?? "local"));
            OnDesktopThreadSeen(threadId, hostId);
        }

        private void OnDesktopThreadStarted(string threadId, string hostId)
        {
            if (_disposed || string.IsNullOrWhiteSpace(threadId)) return;
            hostId = DesktopHostCatalog.Resolve(threadId, hostId);
            var changed = false;
            lock (_sync)
            {
                var key = ThreadIdentity.Key(threadId, hostId);
                if (_handledAttention.Remove(key)) SaveHandledAttentionStateLocked();
                _desktopCompletedThreads.Remove(key);
                _desktopRunningThreads.Add(key);
                _desktopRunningConfirmedThreads.Remove(key);
                _desktopRunningStartedAt[key] = DateTime.Now;
                if (_completedAwaitingReview.Remove(key)) SaveAwaitingReviewStateLocked();
                ThreadItem item;
                if (_cache.TryGetValue(key, out item))
                {
                    ApplyDesktopRunningOverrideLocked(item);
                    item.UpdatedAt = DateTime.Now;
                    changed = true;
                }
            }
            AppLog.Info("Desktop start received thread=" + threadId + " host=" + (hostId ?? "local") + " immediate=" + changed);
            if (changed) RaiseThreads(GetSnapshot());
            OnDesktopThreadSeen(threadId, hostId);
        }

        private void OnSessionCatalogChanged(object sender, FileSystemEventArgs e)
        {
            ScheduleEventRefresh();
            if (e.ChangeType == WatcherChangeTypes.Created)
                Task.Delay(1200).ContinueWith(delegate { ScheduleEventRefresh(); });
        }

        private async Task<StateSnapshot> ReadStateBridgeAsync(bool forceRemote)
        {
            var result = new StateSnapshot();
            var stageTimer = Stopwatch.StartNew();
            var localThreads = await Task.Run(delegate { return SessionScanner.ScanLocal(); });
            PerfDiagnostics.Duration("refresh-local-sessions", stageTimer, 500, "threads=" + localThreads.Count);
            lock (_sync)
                foreach (var item in localThreads)
                {
                    ReconcileScannedRunningStateLocked(item);
                    ApplyCompletionAwaitingReview(item);
                    ApplyDesktopRunningOverrideLocked(item);
                    ApplyHandledAttentionLocked(item);
                    result.Threads.Add(item);
                }
            List<ThreadItem> remoteSnapshot;
            lock (_remoteSync)
            {
                remoteSnapshot = _remoteCache.Select(CloneThread).ToList();
                if ((_remoteRefreshTask == null || _remoteRefreshTask.IsCompleted) &&
                    (forceRemote || DateTime.Now - _remoteCacheAt > TimeSpan.FromMinutes(3)))
                    _remoteRefreshTask = RefreshRemoteCacheAsync();
            }
            foreach (var item in remoteSnapshot) result.Threads.Add(item);
            lock (_sync)
                foreach (var item in result.Threads)
                {
                    ApplyCompletionAwaitingReview(item);
                    ApplyDesktopRunningOverrideLocked(item);
                    ApplyHandledAttentionLocked(item);
                }
            lock (_sync)
                foreach (var item in _cache.Values.Where(value => value.IsSideConversation))
                    if (!result.Threads.Any(value => string.Equals(ThreadIdentity.Key(value), ThreadIdentity.Key(item), StringComparison.OrdinalIgnoreCase)))
                        result.Threads.Add(CloneThread(item));
            stageTimer.Restart();
            var navigationTitles = await Task.Run(delegate { return ThreadTitleCatalog.Read(result.Threads); });
            PerfDiagnostics.Duration("refresh-title-index", stageTimer, 250, "titles=" + navigationTitles.Count);
            foreach (var item in result.Threads)
            {
                string title;
                if (navigationTitles.TryGetValue(ThreadIdentity.Key(item), out title))
                {
                    item.NavigationTitle = title;
                    item.NavigationTitleVerified = true;
                }
            }
            lock (_sync)
                foreach (var item in result.Threads.Where(item => _archivedThreadIds.Contains(ThreadIdentity.Key(item))))
                {
                    item.Group = TaskGroup.History;
                    item.StatusText = "已归档";
                }
            result.RemoteAvailable = remoteSnapshot.Count > 0;
            stageTimer.Restart();
            var candidates = await Task.Run(delegate { return DesktopCandidateReader.Read(_json); });
            PerfDiagnostics.Duration("refresh-sidebar-candidates", stageTimer, 200, "candidates=" + candidates.Count);
            stageTimer.Restart();
            var logCandidates = await Task.Run(delegate { return DesktopLogCandidateReader.Read(32); });
            PerfDiagnostics.Duration("refresh-log-candidates", stageTimer, 250, "candidates=" + logCandidates.Count);
            await DiscoverMissingDesktopThreadsAsync(result.Threads, logCandidates, candidates.Values);
            var byKey = result.Threads.ToDictionary(ThreadIdentity.Key, x => x, StringComparer.OrdinalIgnoreCase);
            var rolloutActive = result.Threads.Where(x => x.Group == TaskGroup.Running).OrderByDescending(x => x.UpdatedAt).ToList();
            var orderedCandidates = new List<KeyValuePair<string, string>>();
            foreach (var item in rolloutActive) if (!orderedCandidates.Any(x => x.Key == item.Id))
                orderedCandidates.Add(new KeyValuePair<string, string>(item.Id, item.HostId ?? "local"));
            foreach (var item in result.Threads.Where(item => item.Group == TaskGroup.Waiting).OrderByDescending(x => x.UpdatedAt).Take(12))
                if (!orderedCandidates.Any(x => x.Key == item.Id))
                    orderedCandidates.Add(new KeyValuePair<string, string>(item.Id, item.HostId ?? "local"));
            foreach (var item in result.Threads.OrderByDescending(x => x.UpdatedAt).Take(20))
                if (!orderedCandidates.Any(x => x.Key == item.Id)) orderedCandidates.Add(new KeyValuePair<string, string>(item.Id, item.HostId ?? "local"));
            foreach (var pair in candidates.Take(20))
            {
                var candidateKey = ThreadIdentity.Key(pair.Key, pair.Value);
                if (byKey.ContainsKey(candidateKey) && !orderedCandidates.Any(x =>
                    string.Equals(ThreadIdentity.Key(x.Key, x.Value), candidateKey, StringComparison.OrdinalIgnoreCase)))
                    orderedCandidates.Add(pair);
            }
            var persistentStatusThreads = new HashSet<string>(
                rolloutActive.Select(item => item.Id).Concat(result.Threads.Where(item => item.Group == TaskGroup.Waiting).Select(item => item.Id)),
                StringComparer.OrdinalIgnoreCase);
            stageTimer.Restart();
            var batchStatuses = await _ipc.ReadThreadStatusesAsync(orderedCandidates.Take(32), persistentStatusThreads, 700);
            PerfDiagnostics.Duration("refresh-ipc-status", stageTimer, 750,
                "requested=" + Math.Min(32, orderedCandidates.Count) + " received=" + batchStatuses.Count);
            var probeResults = batchStatuses.Select(pair => Tuple.Create(pair.Key, pair.Value)).ToList();
            var confirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var probe in probeResults)
            {
                ThreadItem item;
                var probeHost = orderedCandidates.FirstOrDefault(pair => string.Equals(pair.Key, probe.Item1, StringComparison.OrdinalIgnoreCase)).Value ?? "local";
                var probeKey = ThreadIdentity.Key(probe.Item1, probeHost);
                if (probe.Item2 != null && byKey.TryGetValue(probeKey, out item))
                {
                    lock (_sync)
                        ApplyAuthoritativeStatusLocked(item, probe.Item2);
                    confirmed.Add(probeKey);
                }
            }
            var refreshedTitles = ThreadTitleCatalog.Read(result.Threads);
            foreach (var item in result.Threads)
            {
                string refreshedTitle;
                if (!refreshedTitles.TryGetValue(ThreadIdentity.Key(item), out refreshedTitle) ||
                    string.IsNullOrWhiteSpace(refreshedTitle)) continue;
                item.Title = Text(refreshedTitle, 42, "未命名任务");
                item.NavigationTitle = refreshedTitle;
                item.NavigationTitleVerified = true;
            }
            foreach (var item in rolloutActive.Where(x => !confirmed.Contains(ThreadIdentity.Key(x)) && DateTime.Now - x.UpdatedAt > TimeSpan.FromMinutes(2)))
            {
                item.Group = DateTime.Now - item.UpdatedAt <= TimeSpan.FromDays(7) ? TaskGroup.Completed : TaskGroup.History;
                item.StatusText = item.Group == TaskGroup.Completed ? "已完成" : "历史任务";
            }
            lock (_sync)
                foreach (var item in result.Threads)
                {
                    ApplyDesktopRunningOverrideLocked(item);
                    ApplyHandledAttentionLocked(item);
                }
            result.Threads.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
            if (result.Threads.Count > 250) result.Threads.RemoveRange(250, result.Threads.Count - 250);
            return result;
        }

        private async Task DiscoverMissingDesktopThreadsAsync(IEnumerable<ThreadItem> current,
            IEnumerable<KeyValuePair<string, string>> logCandidates, IEnumerable<string> knownHosts)
        {
            DesktopIpcClient ipc;
            lock (_sync) ipc = _ipc;
            if (ipc == null || !ipc.IsConnected) return;
            var existingIds = new HashSet<string>((current ?? new ThreadItem[0]).Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
            lock (_sync) foreach (var item in _cache.Values) existingIds.Add(item.Id);
            var fallbackHosts = (knownHosts ?? new string[0]).Concat(new[] { "local" })
                .Select(ThreadIdentity.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var missing = (logCandidates ?? new KeyValuePair<string, string>[0])
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !existingIds.Contains(pair.Key)).Take(12).ToList();
            if (missing.Count == 0) return;
            var tasks = missing.Select(async pair =>
            {
                var hosts = new[] { ThreadIdentity.Host(pair.Value) }.Concat(fallbackHosts)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var host in hosts)
                    if (await ipc.DiscoverThreadAsync(pair.Key, host, 1200)) break;
            });
            await Task.WhenAll(tasks);
        }

        private async Task RefreshRemoteCacheAsync()
        {
            var performanceTimer = Stopwatch.StartNew();
            try
            {
                var remote = await RemoteSessionScanner.ScanAsync(_json);
                lock (_sync)
                    foreach (var item in remote)
                    {
                        ReconcileScannedRunningStateLocked(item);
                        ApplyCompletionAwaitingReview(item);
                        ApplyDesktopRunningOverrideLocked(item);
                        ApplyHandledAttentionLocked(item);
                    }
                lock (_remoteSync)
                {
                    _remoteCache = remote.Select(CloneThread).ToList();
                    _remoteCacheAt = DateTime.Now;
                }
                MergeRemoteSnapshot(remote);
                RaiseThreads(GetSnapshot());
                PerfDiagnostics.Duration("remote-refresh", performanceTimer, 2000, "threads=" + remote.Count);
            }
            catch (Exception ex)
            {
                PerfDiagnostics.Duration("remote-refresh-failed", performanceTimer, 1000);
                AppLog.Error("Remote session refresh failed", ex);
                lock (_remoteSync) _remoteCacheAt = DateTime.Now;
            }
        }

        private void MergeRemoteSnapshot(IEnumerable<ThreadItem> remote)
        {
            lock (_sync)
            {
                var unreadResults = _cache.Where(pair => !IsLocalHostId(pair.Value.HostId) &&
                    !pair.Value.IsSideConversation && pair.Value.Group == TaskGroup.Waiting &&
                    string.Equals(pair.Value.StatusText, "有新结果", StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => pair.Value.StatusText, StringComparer.OrdinalIgnoreCase);
                var verifiedNavigationTitles = _cache.Where(pair => !IsLocalHostId(pair.Value.HostId) &&
                    !pair.Value.IsSideConversation && pair.Value.NavigationTitleVerified &&
                    !string.IsNullOrWhiteSpace(pair.Value.NavigationTitle))
                    .ToDictionary(pair => pair.Key, pair => pair.Value.NavigationTitle, StringComparer.OrdinalIgnoreCase);
                foreach (var key in _cache.Where(pair => !IsLocalHostId(pair.Value.HostId) && !pair.Value.IsSideConversation)
                    .Select(pair => pair.Key).ToList()) _cache.Remove(key);
                foreach (var item in remote ?? new ThreadItem[0])
                {
                    string verifiedNavigationTitle;
                    if (verifiedNavigationTitles.TryGetValue(ThreadIdentity.Key(item), out verifiedNavigationTitle))
                    {
                        item.NavigationTitle = verifiedNavigationTitle;
                        item.NavigationTitleVerified = true;
                    }
                    ApplyCompletionAwaitingReview(item);
                    string unreadStatus;
                    if (unreadResults.TryGetValue(ThreadIdentity.Key(item), out unreadStatus))
                    {
                        item.Group = TaskGroup.Waiting;
                        item.StatusText = unreadStatus;
                    }
                    ApplyDesktopRunningOverrideLocked(item);
                    ApplyHandledAttentionLocked(item);
                    _cache[ThreadIdentity.Key(item)] = item;
                }
            }
        }

        private static ThreadItem CloneThread(ThreadItem item)
        {
            return new ThreadItem
            {
                Id = item.Id, Title = item.Title, NavigationTitle = item.NavigationTitle, Preview = item.Preview, Cwd = item.Cwd, Project = item.Project,
                HostLabel = item.HostLabel, HostId = item.HostId, UpdatedAt = item.UpdatedAt, Group = item.Group,
                StatusText = item.StatusText, IsPinned = item.IsPinned, RolloutPath = item.RolloutPath,
                IsSideConversation = item.IsSideConversation, ParentThreadId = item.ParentThreadId,
                SideParentVerified = item.SideParentVerified, NavigationTitleVerified = item.NavigationTitleVerified
            };
        }

        private static bool IsLocalHostId(string hostId)
        {
            return string.IsNullOrWhiteSpace(hostId) || string.Equals(hostId, "local", StringComparison.OrdinalIgnoreCase);
        }

        private void OnIpcStatusChanged(string threadId, string hostId, DesktopThreadStatus status)
        {
            hostId = DesktopHostCatalog.Resolve(threadId, hostId);
            var changed = false;
            lock (_sync)
            {
                var key = ThreadIdentity.Key(threadId, hostId);
                ThreadItem item;
                if (!_cache.TryGetValue(key, out item)) return;
                changed = ApplyAuthoritativeStatusLocked(item, status);
                if (changed) item.UpdatedAt = DateTime.Now;
            }
            if (changed) RaiseThreads(GetSnapshot());
        }

        private void OnIpcTitleChanged(string threadId, string hostId, string title)
        {
            if (_disposed || string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(title)) return;
            hostId = DesktopHostCatalog.Resolve(threadId, hostId);
            title = title.Trim();
            ThreadItem remembered = null;
            var changed = false;
            lock (_sync)
            {
                ThreadItem item;
                if (!_cache.TryGetValue(ThreadIdentity.Key(threadId, hostId), out item))
                    item = _cache.Values.FirstOrDefault(value =>
                        string.Equals(value.Id, threadId, StringComparison.OrdinalIgnoreCase));
                if (item != null && item.IsSideConversation)
                {
                    // A side-conversation snapshot exposes the parent conversation title in
                    // conversation.title. Its own tab title comes from the first user message.
                    AppLog.Info("Ignored parent title for side thread=" + threadId + " host=" + hostId);
                    return;
                }
                if (item != null)
                {
                    var displayTitle = Text(title, 42, "未命名任务");
                    changed = !string.Equals(item.Title, displayTitle, StringComparison.Ordinal) ||
                        !string.Equals(item.NavigationTitle, title, StringComparison.Ordinal) ||
                        !item.NavigationTitleVerified;
                    item.Title = displayTitle;
                    item.NavigationTitle = title;
                    item.NavigationTitleVerified = true;
                    remembered = CloneThread(item);
                }
            }
            if (remembered == null)
                remembered = new ThreadItem
                {
                    Id = threadId,
                    HostId = hostId,
                    Title = Text(title, 42, "未命名任务"),
                    NavigationTitle = title,
                    NavigationTitleVerified = true
                };
            ThreadTitleCatalog.Remember(remembered);
            if (!changed) return;
            lock (_remoteSync)
                foreach (var item in _remoteCache.Where(value =>
                    string.Equals(value.Id, threadId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ThreadIdentity.Host(value.HostId), ThreadIdentity.Host(hostId), StringComparison.OrdinalIgnoreCase)))
                {
                    item.Title = Text(title, 42, "未命名任务");
                    item.NavigationTitle = title;
                    item.NavigationTitleVerified = true;
                }
            AppLog.Info("Thread title updated thread=" + threadId + " host=" + (hostId ?? "local"));
            RaiseThreads(GetSnapshot());
        }

        private void OnIpcThreadDiscovered(ThreadItem discovered, DesktopThreadStatus status)
        {
            if (discovered == null || string.IsNullOrWhiteSpace(discovered.Id)) return;
            lock (_sync)
            {
                discovered.HostId = ResolveAuthoritativeHostLocked(discovered);
                discovered.HostLabel = DesktopHostCatalog.HostLabel(discovered.HostId);
                ReconcileThreadAliasesLocked(discovered.Id, discovered.HostId);
                var key = ThreadIdentity.Key(discovered);
                ThreadItem item;
                if (!_cache.TryGetValue(key, out item))
                {
                    item = discovered;
                    _cache[key] = item;
                }
                else
                {
                    item.Title = discovered.Title;
                    item.NavigationTitle = discovered.NavigationTitle;
                    item.Preview = discovered.Preview;
                    item.Cwd = discovered.Cwd;
                    item.Project = discovered.Project;
                    item.HostId = discovered.HostId;
                    item.HostLabel = discovered.HostLabel;
                    item.UpdatedAt = discovered.UpdatedAt;
                    item.IsSideConversation = discovered.IsSideConversation;
                    item.ParentThreadId = discovered.ParentThreadId;
                    item.SideParentVerified = discovered.SideParentVerified;
                    item.NavigationTitleVerified = discovered.NavigationTitleVerified;
                }
                if (status != null)
                    ApplyAuthoritativeStatusLocked(item, status);
                else
                {
                    ApplyCompletionAwaitingReview(item);
                    ApplyDesktopRunningOverrideLocked(item);
                    ApplyHandledAttentionLocked(item);
                }
            }
            ThreadTitleCatalog.Remember(discovered);
            if (!IsLocalHostId(discovered.HostId)) ThreadTitleCatalog.RemoveLocalAlias(discovered.Id);
            if (discovered.IsSideConversation && !string.IsNullOrWhiteSpace(discovered.ParentThreadId) &&
                string.IsNullOrWhiteSpace(ThreadTitleCatalog.Resolve(discovered.ParentThreadId, discovered.HostId)))
            {
                DesktopIpcClient ipc;
                lock (_sync) ipc = _ipc;
                if (ipc != null && ipc.IsConnected)
                {
                    var parentId = discovered.ParentThreadId;
                    var parentHostId = discovered.HostId;
                    Task.Run(async delegate
                    {
                        try { await ipc.DiscoverThreadAsync(parentId, parentHostId, 3500); }
                        catch (Exception ex) { AppLog.Error("Side navigation parent preload failed", ex); }
                    });
                }
            }
            RaiseThreads(GetSnapshot());
        }

        internal bool MergeDiscoveredThreadForTest(ThreadItem discovered, DesktopThreadStatus status)
        {
            OnIpcThreadDiscovered(discovered, status);
            if (discovered == null) return false;
            lock (_sync) return _cache.ContainsKey(ThreadIdentity.Key(discovered));
        }

        internal void OnIpcTitleChangedForTest(string threadId, string hostId, string title)
        {
            OnIpcTitleChanged(threadId, hostId, title);
        }

        internal void OnDesktopThreadStartedForTest(string threadId, string hostId) { OnDesktopThreadStarted(threadId, hostId); }
        internal void OnIpcStatusChangedForTest(string threadId, string hostId, DesktopThreadStatus status) { OnIpcStatusChanged(threadId, hostId, status); }
        internal void OnIpcThreadViewedForTest(string threadId, string hostId) { OnIpcThreadViewed(threadId, hostId); }
        internal void ReplaceCacheForTest(IEnumerable<ThreadItem> items) { ReplaceCache(items); }
        internal void MergeRemoteSnapshotForTest(IEnumerable<ThreadItem> items) { MergeRemoteSnapshot(items); }
        internal IList<ThreadItem> GetSnapshotForTest() { return GetSnapshot(); }
        internal void FlushStateForTest() { _awaitingReviewStore.Flush(); _handledAttentionStore.Flush(); }

        internal Dictionary<string, object> VerifyDesktopRunningLifecycleForTest(ThreadItem discovered)
        {
            if (discovered == null) return new Dictionary<string, object>();
            OnIpcThreadDiscovered(discovered, new DesktopThreadStatus { Type = "idle" });
            OnDesktopThreadStarted(discovered.Id, discovered.HostId);
            OnIpcThreadViewed(discovered.Id, discovered.HostId);
            OnIpcStatusChanged(discovered.Id, discovered.HostId, new DesktopThreadStatus { Type = "idle" });
            TaskGroup afterStaleIdle;
            lock (_sync) afterStaleIdle = _cache[ThreadIdentity.Key(discovered)].Group;
            OnIpcStatusChanged(discovered.Id, discovered.HostId, new DesktopThreadStatus { Type = "active" });
            OnIpcStatusChanged(discovered.Id, discovered.HostId, new DesktopThreadStatus { Type = "idle" });
            ThreadItem completed;
            lock (_sync) completed = _cache[ThreadIdentity.Key(discovered)];
            return new Dictionary<string, object>
            {
                { "viewDoesNotClearRunning", afterStaleIdle == TaskGroup.Running },
                { "staleIdleDoesNotClearRunning", afterStaleIdle == TaskGroup.Running },
                { "activeThenIdleCompletes", completed.Group == TaskGroup.Waiting },
                { "runningMarkerCleared", !_desktopRunningThreads.Contains(ThreadIdentity.Key(discovered)) }
            };
        }

        private void OnIpcThreadViewed(string threadId, string hostId)
        {
            // Viewing/opening a thread is intentionally not an acknowledgement.
            // Only the explicit "已处理" action can remove it from the waiting list.
        }

        private void OnIpcReadStateChanged(string threadId, string hostId, bool hasUnreadTurn)
        {
            var changed = false;
            DesktopThreadStatus cached = null;
            DesktopIpcClient ipc;
            lock (_sync) ipc = _ipc;
            if (ipc != null) ipc.TryUpdateUnreadState(threadId, hasUnreadTurn, out cached);
            lock (_sync)
            {
                ThreadItem item;
                if (!_cache.TryGetValue(ThreadIdentity.Key(threadId, hostId), out item)) return;
                if (cached == null)
                {
                    cached = new DesktopThreadStatus
                    {
                        Type = item.Group == TaskGroup.Running ? "active" : "idle",
                        HasUnreadTurn = hasUnreadTurn
                    };
                    if (item.StatusText == "等待批准") { cached.Type = "active"; cached.Flags = new[] { "waitingOnApproval" }; }
                    else if (item.StatusText == "等待回复") { cached.Type = "active"; cached.Flags = new[] { "waitingOnUserInput" }; }
                }
                changed = ApplyAuthoritativeStatusLocked(item, cached);
            }
            if (changed) RaiseThreads(GetSnapshot());
        }

        private void OnIpcArchivedChanged(string threadId, string hostId, bool archived)
        {
            lock (_sync)
            {
                var threadKey = ThreadIdentity.Key(threadId, hostId);
                if (archived) _archivedThreadIds.Add(threadKey);
                else _archivedThreadIds.Remove(threadKey);
                if (archived)
                {
                    _desktopRunningThreads.Remove(threadKey);
                    _desktopRunningConfirmedThreads.Remove(threadKey);
                    _desktopRunningStartedAt.Remove(threadKey);
                }
                ThreadItem item;
                if (_cache.TryGetValue(ThreadIdentity.Key(threadId, hostId), out item))
                {
                    if (archived) { item.Group = TaskGroup.History; item.StatusText = "已归档"; }
                }
            }
            if (archived) RaiseThreads(GetSnapshot());
            else ScheduleEventRefresh();
        }

        private void OnIpcConnectionReset(DesktopIpcClient source)
        {
            if (_disposed) return;
            Task.Run(delegate
            {
                DesktopIpcClient previous;
                lock (_sync)
                {
                    if (!ReferenceEquals(_ipc, source)) return;
                    previous = _ipc;
                    _ipc = null;
                }
                if (previous != null) previous.Dispose();
                ScheduleEventRefresh();
            });
        }

        private void ScheduleEventRefresh()
        {
            if (_disposed) return;
            lock (_eventRefreshSync)
            {
                _eventRefreshDirty = true;
                if (_eventRefreshTask != null && !_eventRefreshTask.IsCompleted) return;
                _eventRefreshTask = Task.Run(async delegate
                {
                    try
                    {
                        while (!_disposed)
                        {
                            await Task.Delay(250);
                            lock (_eventRefreshSync)
                            {
                                if (!_eventRefreshDirty) break;
                                _eventRefreshDirty = false;
                            }
                            try { await RefreshAsync(); }
                            catch (Exception ex) { AppLog.Error("Event refresh failed", ex); }
                            lock (_eventRefreshSync)
                                if (!_eventRefreshDirty) break;
                        }
                    }
                    finally
                    {
                        var restart = false;
                        lock (_eventRefreshSync)
                        {
                            _eventRefreshTask = null;
                            restart = _eventRefreshDirty && !_disposed;
                        }
                        if (restart) ScheduleEventRefresh();
                    }
                });
            }
        }

        private List<ThreadItem> GetSnapshot()
        {
            lock (_sync)
            {
                foreach (var item in _cache.Values)
                {
                    ApplyDesktopRunningOverrideLocked(item);
                    ApplyHandledAttentionLocked(item);
                }
                return _cache.Values.ToList();
            }
        }

        private void ApplyDesktopRunningOverrideLocked(ThreadItem item)
        {
            if (item == null || !_desktopRunningThreads.Contains(ThreadIdentity.Key(item))) return;
            if (item.Group == TaskGroup.Waiting &&
                (string.Equals(item.StatusText, "等待批准", StringComparison.Ordinal) ||
                 string.Equals(item.StatusText, "等待回复", StringComparison.Ordinal))) return;
            item.Group = TaskGroup.Running;
            item.StatusText = "进行中";
        }

        private bool ApplyAuthoritativeStatusLocked(ThreadItem item, DesktopThreadStatus status)
        {
            if (item == null || status == null) return false;
            var key = ThreadIdentity.Key(item);
            var previousGroup = item.Group;
            var previousStatus = item.StatusText;
            var wasRunning = previousGroup == TaskGroup.Running || _desktopRunningThreads.Contains(key);
            ApplyStatus(item, status);
            CaptureUnreadAwaitingReviewLocked(item, status);
            var active = string.Equals(status.Type, "active", StringComparison.OrdinalIgnoreCase);
            var idle = string.Equals(status.Type, "idle", StringComparison.OrdinalIgnoreCase);
            var trustedIdle = idle;
            if (active)
            {
                _desktopRunningThreads.Add(key);
                _desktopRunningConfirmedThreads.Add(key);
                if (!_desktopRunningStartedAt.ContainsKey(key)) _desktopRunningStartedAt[key] = DateTime.Now;
                _desktopCompletedThreads.Remove(key);
                if (_completedAwaitingReview.Remove(key)) SaveAwaitingReviewStateLocked();
                if (_handledAttention.Remove(key)) SaveHandledAttentionStateLocked();
            }
            else if (idle && _desktopRunningThreads.Contains(key))
            {
                trustedIdle = _desktopRunningConfirmedThreads.Contains(key);
                if (trustedIdle)
                {
                    _desktopRunningThreads.Remove(key);
                    _desktopRunningConfirmedThreads.Remove(key);
                    _desktopRunningStartedAt.Remove(key);
                }
                else ApplyDesktopRunningOverrideLocked(item);
            }
            if (_desktopCompletedThreads.Remove(key) || (trustedIdle && wasRunning))
            {
                _completedAwaitingReview[key] = DateTime.Now;
                SaveAwaitingReviewStateLocked();
            }
            if (!active) ApplyCompletionAwaitingReview(item);
            ApplyDesktopRunningOverrideLocked(item);
            if (!active) ApplyHandledAttentionLocked(item);
            return previousGroup != item.Group || !string.Equals(previousStatus, item.StatusText, StringComparison.Ordinal);
        }

        private void ReconcileScannedRunningStateLocked(ThreadItem item)
        {
            if (item == null) return;
            var key = ThreadIdentity.Key(item);
            if (!_desktopRunningThreads.Contains(key)) return;
            if (item.Group == TaskGroup.Running)
            {
                _desktopRunningConfirmedThreads.Add(key);
                return;
            }
            DateTime startedAt;
            if (!_desktopRunningStartedAt.TryGetValue(key, out startedAt) || item.UpdatedAt < startedAt.AddSeconds(-2)) return;
            _desktopRunningThreads.Remove(key);
            _desktopRunningConfirmedThreads.Remove(key);
            _desktopRunningStartedAt.Remove(key);
            if (item.Group == TaskGroup.Completed)
            {
                _completedAwaitingReview[key] = DateTime.Now;
                SaveAwaitingReviewStateLocked();
            }
        }

        private void SaveAwaitingReviewStateLocked()
        {
            _awaitingReviewStore.ScheduleSave(_completedAwaitingReview);
        }

        private void SaveHandledAttentionStateLocked()
        {
            _handledAttentionStore.ScheduleSave(_handledAttention);
        }

        private bool IsHandledAttentionCurrentLocked(ThreadItem item)
        {
            if (item == null) return false;
            var key = ThreadIdentity.Key(item);
            DateTime handledVersion;
            if (!_handledAttention.TryGetValue(key, out handledVersion)) return false;
            if (item.UpdatedAt != DateTime.MinValue && item.UpdatedAt > handledVersion)
            {
                _handledAttention.Remove(key);
                SaveHandledAttentionStateLocked();
                return false;
            }
            return true;
        }

        private void ApplyHandledAttentionLocked(ThreadItem item)
        {
            if (item != null && item.Group == TaskGroup.Running)
            {
                var runningKey = ThreadIdentity.Key(item);
                if (_handledAttention.Remove(runningKey)) SaveHandledAttentionStateLocked();
                return;
            }
            if (!IsHandledAttentionCurrentLocked(item)) return;
            if (item.Group == TaskGroup.Waiting || item.Group == TaskGroup.Running || item.Group == TaskGroup.Completed)
            {
                item.Group = TaskGroup.Completed;
                item.StatusText = "已处理";
            }
        }

        private void CaptureUnreadAwaitingReviewLocked(ThreadItem item, DesktopThreadStatus status)
        {
            if (item == null || status == null || !status.HasUnreadTurn ||
                string.Equals(status.Type, "active", StringComparison.OrdinalIgnoreCase) ||
                IsHandledAttentionCurrentLocked(item)) return;
            var key = ThreadIdentity.Key(item);
            if (_completedAwaitingReview.ContainsKey(key)) return;
            _completedAwaitingReview[key] = item.UpdatedAt == DateTime.MinValue ? DateTime.Now : item.UpdatedAt;
            SaveAwaitingReviewStateLocked();
        }

        private void ApplyCompletionAwaitingReview(ThreadItem item)
        {
            if (item == null) return;
            var key = ThreadIdentity.Key(item);
            if (item.Group == TaskGroup.Running)
            {
                if (_completedAwaitingReview.Remove(key)) SaveAwaitingReviewStateLocked();
                return;
            }
            if (!_completedAwaitingReview.ContainsKey(key)) return;
            item.Group = TaskGroup.Waiting;
            item.StatusText = "有新结果";
        }

        internal static bool ShouldTreatCompletionAsWaiting(TaskGroup previousGroup, DesktopThreadStatus status)
        {
            return previousGroup == TaskGroup.Running && status != null &&
                string.Equals(status.Type, "idle", StringComparison.OrdinalIgnoreCase);
        }

        internal static string DisplayTitle(ThreadItem item)
        {
            if (item == null) return "未命名任务";
            if (!item.IsSideConversation && !string.IsNullOrWhiteSpace(item.NavigationTitle)) return item.NavigationTitle.Trim();
            return string.IsNullOrWhiteSpace(item.Title) ? "未命名任务" : item.Title.Trim();
        }

        internal static void ApplyStatus(ThreadItem item, string type, IEnumerable<string> flags)
        {
            type = type ?? "notLoaded";
            var values = new HashSet<string>(flags ?? new string[0]);
            if (type == "active")
            {
                if (values.Contains("waitingOnApproval")) { item.Group = TaskGroup.Waiting; item.StatusText = "等待批准"; }
                else if (values.Contains("waitingOnUserInput")) { item.Group = TaskGroup.Waiting; item.StatusText = "等待回复"; }
                else { item.Group = TaskGroup.Running; item.StatusText = "进行中"; }
            }
            else if (type == "idle") { item.Group = TaskGroup.Completed; item.StatusText = "已完成"; }
            else if (type == "systemError") { item.Group = TaskGroup.Error; item.StatusText = "发生错误"; }
            else { item.Group = TaskGroup.History; item.StatusText = "历史任务"; }
        }

        internal static void ApplyStatus(ThreadItem item, DesktopThreadStatus status)
        {
            ApplyStatus(item, status.Type, status.Flags);
            if (status.HasUnreadTurn && status.Type != "active")
            {
                item.Group = TaskGroup.Waiting;
                item.StatusText = "有新结果";
            }
        }

        internal static string ProjectName(string cwd)
        {
            if (string.IsNullOrWhiteSpace(cwd)) return "codex";
            var value = cwd.TrimEnd('\\', '/');
            var index = Math.Max(value.LastIndexOf('\\'), value.LastIndexOf('/'));
            return index >= 0 ? value.Substring(index + 1) : value;
        }
        internal static string Text(string value, int max, string fallback) { value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); if (value.Length == 0) return fallback; return value.Length <= max ? value : value.Substring(0, max - 1) + "…"; }
        internal static DateTime FromUnix(long seconds) { try { if (seconds > 100000000000L) seconds /= 1000; return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime(); } catch { return DateTime.Now; } }
        private void RaiseThreads(IList<ThreadItem> items) { var handler = ThreadsReceived; if (handler != null) handler(items); }
        private void RaiseConnection(string status) { var handler = ConnectionChanged; if (handler != null) handler(status); }
        public void Dispose()
        {
            _disposed = true;
            if (_sessionWatcher != null) _sessionWatcher.Dispose();
            if (_desktopLogMonitor != null) _desktopLogMonitor.Dispose();
            _awaitingReviewStore.Dispose();
            _handledAttentionStore.Dispose();
            DesktopIpcClient ipc;
            lock (_sync) { ipc = _ipc; _ipc = null; }
            if (ipc != null) ipc.Dispose();
        }
    }

    internal sealed class StateSnapshot
    {
        public readonly List<ThreadItem> Threads = new List<ThreadItem>();
        public bool RemoteAvailable;
    }

    internal sealed class DesktopThreadStatus
    {
        public string Type = "notLoaded";
        public string[] Flags = new string[0];
        public bool HasUnreadTurn;
    }

    internal sealed class DesktopThreadSnapshot
    {
        public ThreadItem Item;
        public DesktopThreadStatus Status;
    }

    internal sealed class DesktopIpcClient : IDisposable
    {
        private readonly JavaScriptSerializer _json;
        private readonly object _writeLock = new object();
        private readonly Dictionary<string, TaskCompletionSource<IDictionary<string, object>>> _pending = new Dictionary<string, TaskCompletionSource<IDictionary<string, object>>>();
        private readonly Dictionary<string, TaskCompletionSource<DesktopThreadStatus>> _snapshots = new Dictionary<string, TaskCompletionSource<DesktopThreadStatus>>();
        private readonly Dictionary<string, TaskCompletionSource<DesktopThreadSnapshot>> _threadSnapshots = new Dictionary<string, TaskCompletionSource<DesktopThreadSnapshot>>();
        private readonly Dictionary<string, DesktopThreadStatus> _statusCache = new Dictionary<string, DesktopThreadStatus>();
        private readonly Dictionary<string, DateTime> _statusCacheAt = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, string> _followedHosts = new Dictionary<string, string>();
        private readonly HashSet<string> _refreshingThreads = new HashSet<string>();
        private readonly HashSet<string> _discoveringThreads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private NamedPipeClientStream _pipe;
        private BinaryReader _reader;
        private BinaryWriter _writer;
        private string _clientId;
        private bool _disposed;
        public bool IsConnected { get { return _pipe != null && _pipe.IsConnected && !string.IsNullOrEmpty(_clientId); } }
        public event Action<string, string, DesktopThreadStatus> StatusChanged;
        public event Action<string, string, bool> ReadStateChanged;
        public event Action<string, string, bool> ArchivedChanged;
        public event Action<string, string, string> TitleChanged;
        public event Action<ThreadItem, DesktopThreadStatus> ThreadDiscovered;
        public event Action RefreshSuggested;
        public event Action<DesktopIpcClient> ConnectionReset;

        public DesktopIpcClient(JavaScriptSerializer json) { _json = json; }

        public async Task ConnectAsync(int timeoutMs)
        {
            _pipe = new NamedPipeClientStream(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.Run(() => _pipe.Connect(timeoutMs));
            _reader = new BinaryReader(_pipe, Encoding.UTF8, true);
            _writer = new BinaryWriter(_pipe, Encoding.UTF8, true);
            var ignored = Task.Run((Action)ReadLoop);
            var response = await RequestAsync("initialize", new Dictionary<string, object> { { "clientType", "codex-project-center" } }, 0, timeoutMs);
            var result = Json.GetDictionary(response, "result");
            _clientId = Json.GetString(result, "clientId");
            if (string.IsNullOrEmpty(_clientId)) throw new InvalidOperationException("Codex Desktop IPC 初始化失败");
        }

        public async Task<DesktopThreadStatus> ReadThreadStatusAsync(string threadId, string hostId, int timeoutMs)
        {
            if (!IsConnected) return null;
            var completion = new TaskCompletionSource<DesktopThreadStatus>();
            lock (_snapshots)
            {
                _snapshots[threadId] = completion;
                _followedHosts[threadId] = hostId;
            }
            lock (_statusCache) _statusCacheAt.Remove(threadId);
            Broadcast("thread-stream-following-changed", 1, new Dictionary<string, object> { { "conversationId", threadId }, { "hostId", hostId }, { "following", true } });
            try { await RequestAsync("thread-follower-load-complete-history", new Dictionary<string, object> { { "conversationId", threadId } }, 1, timeoutMs); }
            catch { }
            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs));
            lock (_snapshots)
            {
                TaskCompletionSource<DesktopThreadStatus> current;
                if (_snapshots.TryGetValue(threadId, out current) && ReferenceEquals(current, completion)) _snapshots.Remove(threadId);
            }
            if (finished == completion.Task) return completion.Task.Result;
            lock (_statusCache)
            {
                DesktopThreadStatus cached;
                return _statusCache.TryGetValue(threadId, out cached) ? cached : null;
            }
        }

        public async Task<bool> DiscoverThreadAsync(string threadId, string hostId, int timeoutMs)
        {
            if (!IsConnected || string.IsNullOrWhiteSpace(threadId)) return false;
            lock (_snapshots)
            {
                if (_discoveringThreads.Contains(threadId)) return false;
                _discoveringThreads.Add(threadId);
            }
            try
            {
                var completion = new TaskCompletionSource<DesktopThreadSnapshot>();
                lock (_snapshots)
                {
                    _threadSnapshots[threadId] = completion;
                    _followedHosts[threadId] = hostId ?? "local";
                }
                Broadcast("thread-stream-following-changed", 1, new Dictionary<string, object>
                {
                    { "conversationId", threadId }, { "hostId", hostId ?? "local" }, { "following", true }
                });
                try { await RequestAsync("thread-follower-load-complete-history", new Dictionary<string, object> { { "conversationId", threadId } }, 1, timeoutMs); }
                catch { }
                var finished = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs));
                if (finished != completion.Task) return false;
                var snapshot = completion.Task.Result;
                if (snapshot != null && snapshot.Item != null)
                {
                    // The conversation snapshot is authoritative. The requested host is only a
                    // discovery hint and must not turn a remote side task into a local task.
                    snapshot.Item.HostId = PreferSnapshotHost(snapshot.Item.HostId, hostId);
                    snapshot.Item.HostLabel = DesktopHostCatalog.HostLabel(snapshot.Item.HostId);
                }
                var handler = ThreadDiscovered;
                if (handler != null && snapshot != null && snapshot.Item != null) handler(snapshot.Item, snapshot.Status);
                return snapshot != null && snapshot.Item != null;
            }
            finally
            {
                lock (_snapshots)
                {
                    _discoveringThreads.Remove(threadId);
                    _threadSnapshots.Remove(threadId);
                }
            }
        }

        internal static string PreferSnapshotHost(string snapshotHostId, string requestedHostId)
        {
            return string.IsNullOrWhiteSpace(snapshotHostId)
                ? ThreadIdentity.Host(requestedHostId)
                : ThreadIdentity.Host(snapshotHostId);
        }

        public async Task<Dictionary<string, DesktopThreadStatus>> ReadThreadStatusesAsync(IEnumerable<KeyValuePair<string, string>> threads,
            ISet<string> persistentThreadIds, int timeoutMs)
        {
            var result = new Dictionary<string, DesktopThreadStatus>();
            if (!IsConnected) return result;
            var requested = threads.GroupBy(x => x.Key).Select(x => x.First()).ToList();
            var requestedIds = new HashSet<string>(requested.Select(pair => pair.Key), StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, string>> removed;
            lock (_snapshots)
            {
                removed = _followedHosts.Where(pair => !requestedIds.Contains(pair.Key)).ToList();
                foreach (var pair in removed) _followedHosts.Remove(pair.Key);
            }
            foreach (var pair in removed)
                Broadcast("thread-stream-following-changed", 1, new Dictionary<string, object> { { "conversationId", pair.Key }, { "hostId", pair.Value }, { "following", false } });
            var completions = new Dictionary<string, TaskCompletionSource<DesktopThreadStatus>>();
            foreach (var pair in requested)
            {
                DesktopThreadStatus cachedStatus;
                DateTime cachedAt;
                lock (_statusCache)
                {
                    _statusCache.TryGetValue(pair.Key, out cachedStatus);
                    _statusCacheAt.TryGetValue(pair.Key, out cachedAt);
                }
                var needsFollow = false;
                lock (_snapshots)
                {
                    string followedHost;
                    needsFollow = !_followedHosts.TryGetValue(pair.Key, out followedHost) || !string.Equals(followedHost, pair.Value, StringComparison.OrdinalIgnoreCase);
                    _followedHosts[pair.Key] = pair.Value;
                }
                if (cachedStatus != null) result[pair.Key] = cachedStatus;
                var cacheLifetime = cachedStatus != null &&
                    (cachedStatus.HasUnreadTurn || string.Equals(cachedStatus.Type, "active", StringComparison.OrdinalIgnoreCase))
                    ? TimeSpan.FromSeconds(2)
                    : TimeSpan.FromSeconds(45);
                var cacheFresh = cachedStatus != null && cachedAt != DateTime.MinValue && DateTime.Now - cachedAt < cacheLifetime;
                if (!needsFollow && cacheFresh) continue;
                var completion = new TaskCompletionSource<DesktopThreadStatus>();
                completions[pair.Key] = completion;
                lock (_snapshots) _snapshots[pair.Key] = completion;
                if (!needsFollow)
                    Broadcast("thread-stream-following-changed", 1, new Dictionary<string, object> { { "conversationId", pair.Key }, { "hostId", pair.Value }, { "following", false } });
                Broadcast("thread-stream-following-changed", 1, new Dictionary<string, object> { { "conversationId", pair.Key }, { "hostId", pair.Value }, { "following", true } });
            }
            if (completions.Count > 0)
            {
                var all = Task.WhenAll(completions.Values.Select(x => x.Task));
                await Task.WhenAny(all, Task.Delay(timeoutMs));
                foreach (var pair in completions)
                {
                    if (pair.Value.Task.IsCompleted) result[pair.Key] = pair.Value.Task.Result;
                    else
                    {
                        lock (_statusCache)
                        {
                            DesktopThreadStatus cached;
                            if (_statusCache.TryGetValue(pair.Key, out cached)) result[pair.Key] = cached;
                        }
                    }
                    lock (_snapshots)
                    {
                        TaskCompletionSource<DesktopThreadStatus> current;
                        if (_snapshots.TryGetValue(pair.Key, out current) && ReferenceEquals(current, pair.Value)) _snapshots.Remove(pair.Key);
                    }
                }
            }
            var transient = requested.Where(pair =>
            {
                if (persistentThreadIds != null && persistentThreadIds.Contains(pair.Key)) return false;
                DesktopThreadStatus current;
                if (result.TryGetValue(pair.Key, out current) && current != null &&
                    (current.HasUnreadTurn || string.Equals(current.Type, "active", StringComparison.OrdinalIgnoreCase))) return false;
                return true;
            }).ToList();
            foreach (var pair in transient)
            {
                var shouldUnfollow = false;
                lock (_snapshots)
                {
                    string followedHost;
                    if (_followedHosts.TryGetValue(pair.Key, out followedHost) &&
                        string.Equals(followedHost, pair.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        _followedHosts.Remove(pair.Key);
                        shouldUnfollow = true;
                    }
                }
                if (shouldUnfollow)
                    Broadcast("thread-stream-following-changed", 1, new Dictionary<string, object>
                    {
                        { "conversationId", pair.Key }, { "hostId", pair.Value }, { "following", false }
                    });
            }
            PruneStatusCache();
            return result;
        }

        private void PruneStatusCache()
        {
            HashSet<string> followed;
            lock (_snapshots) followed = new HashSet<string>(_followedHosts.Keys, StringComparer.OrdinalIgnoreCase);
            lock (_statusCache)
            {
                var stale = _statusCacheAt.Where(pair => !followed.Contains(pair.Key) &&
                    DateTime.Now - pair.Value > TimeSpan.FromHours(6)).Select(pair => pair.Key).ToList();
                foreach (var key in stale)
                {
                    _statusCache.Remove(key);
                    _statusCacheAt.Remove(key);
                }
                if (_statusCache.Count <= 512) return;
                foreach (var key in _statusCacheAt.Where(pair => !followed.Contains(pair.Key))
                    .OrderBy(pair => pair.Value).Select(pair => pair.Key).Take(_statusCache.Count - 512).ToList())
                {
                    _statusCache.Remove(key);
                    _statusCacheAt.Remove(key);
                }
            }
        }

        private async Task<IDictionary<string, object>> RequestAsync(string method, object parameters, int version, int timeoutMs)
        {
            var requestId = Guid.NewGuid().ToString();
            var completion = new TaskCompletionSource<IDictionary<string, object>>();
            lock (_pending) _pending[requestId] = completion;
            Send(new Dictionary<string, object>
            {
                { "type", "request" }, { "requestId", requestId }, { "sourceClientId", _clientId ?? "initializing-client" },
                { "version", version }, { "method", method }, { "params", parameters }, { "timeoutMs", timeoutMs }
            });
            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs));
            lock (_pending) _pending.Remove(requestId);
            if (finished != completion.Task) throw new TimeoutException(method + " 超时");
            return completion.Task.Result;
        }

        private void Broadcast(string method, int version, object parameters)
        {
            Send(new Dictionary<string, object> { { "type", "broadcast" }, { "method", method }, { "sourceClientId", _clientId }, { "version", version }, { "params", parameters } });
        }

        private void BroadcastTo(string method, int version, object parameters, string targetClientId)
        {
            var message = new Dictionary<string, object> { { "type", "broadcast" }, { "method", method }, { "sourceClientId", _clientId }, { "version", version }, { "params", parameters } };
            if (!string.IsNullOrWhiteSpace(targetClientId)) message["targetClientIds"] = new[] { targetClientId };
            Send(message);
        }

        public bool TryUpdateUnreadState(string threadId, bool hasUnreadTurn, out DesktopThreadStatus status)
        {
            lock (_statusCache)
            {
                DesktopThreadStatus cached;
                if (!_statusCache.TryGetValue(threadId, out cached)) { status = null; return false; }
                status = new DesktopThreadStatus
                {
                    Type = cached.Type,
                    Flags = cached.Flags == null ? new string[0] : cached.Flags.ToArray(),
                    HasUnreadTurn = hasUnreadTurn
                };
                _statusCache[threadId] = status;
                _statusCacheAt[threadId] = DateTime.Now;
                return true;
            }
        }

        public void InvalidateThreadStatus(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return;
            lock (_statusCache) _statusCacheAt.Remove(threadId);
        }

        private void Send(object message)
        {
            var payload = Encoding.UTF8.GetBytes(_json.Serialize(message));
            lock (_writeLock) { _writer.Write(payload.Length); _writer.Write(payload); _writer.Flush(); }
        }

        private void ReadLoop()
        {
            try
            {
                while (!_disposed && _pipe.IsConnected)
                {
                    var length = _reader.ReadInt32();
                    if (length <= 0 || length > 256 * 1024 * 1024) break;
                    var payload = _reader.ReadBytes(length);
                    int subscriptionCount;
                    lock (_snapshots) subscriptionCount = _followedHosts.Count;
                    PerfDiagnostics.ObserveIpcMessage(payload.Length, subscriptionCount);
                    HandleRaw(Encoding.UTF8.GetString(payload));
                }
            }
            catch (Exception ex)
            {
                if (!_disposed) AppLog.Error("IPC read loop stopped", ex);
            }
            finally
            {
                if (!_disposed)
                {
                    var handler = ConnectionReset;
                    if (handler != null) handler(this);
                }
            }
        }

        private void HandleRaw(string text)
        {
            var isBroadcast = text.IndexOf("\"type\":\"broadcast\"", StringComparison.Ordinal) >= 0;
            if (isBroadcast && text.IndexOf("\"method\":\"thread-stream-state-changed\"", StringComparison.Ordinal) >= 0)
            {
                HandleThreadState(text);
                return;
            }
            IDictionary<string, object> message;
            try { message = _json.DeserializeObject(text) as IDictionary<string, object>; }
            catch { return; }
            if (message == null) return;
            var type = Json.GetString(message, "type");
            if (type == "broadcast")
            {
                HandleBroadcast(message);
                return;
            }
            if (type == "response")
            {
                var requestId = Json.GetString(message, "requestId");
                TaskCompletionSource<IDictionary<string, object>> completion;
                lock (_pending) _pending.TryGetValue(requestId ?? "", out completion);
                if (completion != null) completion.TrySetResult(message);
                return;
            }
        }

        private void HandleBroadcast(IDictionary<string, object> message)
        {
            var method = Json.GetString(message, "method") ?? "";
            var parameters = Json.GetDictionary(message, "params");
            if (method == "thread-read-state-changed")
            {
                var threadId = Json.GetString(parameters, "conversationId");
                var hostId = Json.GetString(parameters, "hostId") ?? "local";
                if (string.IsNullOrWhiteSpace(threadId)) return;
                var hasUnread = Json.GetBool(parameters, "hasUnreadTurn");
                lock (_statusCache)
                {
                    DesktopThreadStatus status;
                    if (_statusCache.TryGetValue(threadId, out status)) status.HasUnreadTurn = hasUnread;
                    _statusCacheAt[threadId] = DateTime.Now;
                }
                var handler = ReadStateChanged;
                if (handler != null) handler(threadId, hostId, hasUnread);
                return;
            }
            if (method == "thread-archived" || method == "thread-unarchived")
            {
                var threadId = Json.GetString(parameters, "conversationId");
                var hostId = Json.GetString(parameters, "hostId") ?? "local";
                if (string.IsNullOrWhiteSpace(threadId)) return;
                var handler = ArchivedChanged;
                if (handler != null) handler(threadId, hostId, method == "thread-archived");
                return;
            }
            if (method == "thread/name/updated")
            {
                var threadId = Json.GetString(parameters, "threadId") ?? Json.GetString(parameters, "conversationId");
                var title = Json.GetString(parameters, "threadName") ?? Json.GetString(parameters, "title");
                var hostId = Json.GetString(parameters, "hostId");
                if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(title)) return;
                if (string.IsNullOrWhiteSpace(hostId))
                    lock (_snapshots) _followedHosts.TryGetValue(threadId, out hostId);
                var handler = TitleChanged;
                if (handler != null) handler(threadId, hostId ?? "local", title.Trim());
                return;
            }
            if (method == "thread-stream-following-status-requested")
            {
                var threadId = Json.GetString(parameters, "conversationId");
                var hostId = Json.GetString(parameters, "hostId") ?? "local";
                string followedHost;
                lock (_snapshots) _followedHosts.TryGetValue(threadId ?? "", out followedHost);
                if (!string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(followedHost))
                    BroadcastTo("thread-stream-following-changed", 1,
                        new Dictionary<string, object> { { "conversationId", threadId }, { "hostId", hostId }, { "following", true } },
                        Json.GetString(message, "sourceClientId"));
                return;
            }
            if (method == "ipc-connection-reset")
            {
                var handler = ConnectionReset;
                if (handler != null) handler(this);
                return;
            }
            if (method == "query-cache-invalidate")
            {
                var queryKey = string.Join("/", Json.GetArray(parameters, "queryKey").Select(Convert.ToString));
                if (queryKey.IndexOf("recent-conversations", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    queryKey.IndexOf("tasks", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    queryKey.IndexOf("inbox-items", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    queryKey.IndexOf("pinned-thread", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var handler = RefreshSuggested;
                    if (handler != null) handler();
                }
            }
        }

        private void HandleThreadState(string text)
        {
            var threadId = ExtractJsonString(text, "conversationId");
            if (string.IsNullOrEmpty(threadId)) return;
            if (text.IndexOf("\"change\":{\"type\":\"patches\"", StringComparison.Ordinal) >= 0)
            {
                var title = ReadTitlePatch(text);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    string titleHostId;
                    lock (_snapshots) _followedHosts.TryGetValue(threadId, out titleHostId);
                    if (string.IsNullOrWhiteSpace(titleHostId)) titleHostId = ExtractJsonString(text, "hostId") ?? "local";
                    var titleHandler = TitleChanged;
                    if (titleHandler != null) titleHandler(threadId, titleHostId, title.Trim());
                }
                ScheduleStatusReload(threadId);
                return;
            }
            var runtimeText = ExtractJsonObject(text, "threadRuntimeStatus");
            if (string.IsNullOrEmpty(runtimeText)) return;
            var runtime = _json.DeserializeObject(runtimeText) as IDictionary<string, object>;
            var status = new DesktopThreadStatus
            {
                Type = Json.GetString(runtime, "type") ?? "notLoaded",
                Flags = Json.GetArray(runtime, "activeFlags").Select(Convert.ToString).Where(x => !string.IsNullOrEmpty(x)).ToArray(),
                HasUnreadTurn = ExtractJsonBoolean(text, "hasUnreadTurn")
            };
            lock (_statusCache) _statusCache[threadId] = status;
            lock (_statusCache) _statusCacheAt[threadId] = DateTime.Now;
            TaskCompletionSource<DesktopThreadStatus> snapshot;
            lock (_snapshots) _snapshots.TryGetValue(threadId, out snapshot);
            if (snapshot != null) snapshot.TrySetResult(status);
            IDictionary<string, object> conversation = null;
            var conversationText = ExtractJsonObject(text, "conversationState");
            if (!string.IsNullOrEmpty(conversationText))
            {
                try { conversation = _json.DeserializeObject(conversationText) as IDictionary<string, object>; }
                catch { }
            }
            if (conversation != null)
            {
                var title = Json.GetString(conversation, "title");
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var snapshotHostId = Json.GetString(conversation, "hostId");
                    if (string.IsNullOrWhiteSpace(snapshotHostId)) snapshotHostId = ExtractJsonString(text, "hostId") ?? "local";
                    var titleThreadId = threadId;
                    if (Json.GetBool(conversation, "sideConversation"))
                    {
                        var parentId = SideConversationParentId(conversation);
                        if (!string.IsNullOrWhiteSpace(parentId)) titleThreadId = parentId;
                    }
                    var titleHandler = TitleChanged;
                    if (titleHandler != null) titleHandler(titleThreadId, snapshotHostId, title.Trim());
                }
                TaskCompletionSource<DesktopThreadSnapshot> threadSnapshot;
                lock (_snapshots) _threadSnapshots.TryGetValue(threadId, out threadSnapshot);
                if (threadSnapshot != null)
                {
                    var item = CreateThreadItem(conversation, status);
                    threadSnapshot.TrySetResult(new DesktopThreadSnapshot { Item = item, Status = status });
                }
            }
            string hostId;
            lock (_snapshots) _followedHosts.TryGetValue(threadId, out hostId);
            var handler = StatusChanged;
            if (handler != null) handler(threadId, hostId ?? "local", status);
        }

        private static ThreadItem CreateThreadItem(IDictionary<string, object> conversation, DesktopThreadStatus status)
        {
            if (conversation == null) return null;
            var id = Json.GetString(conversation, "id") ?? Json.GetString(conversation, "sessionId");
            if (string.IsNullOrWhiteSpace(id)) return null;
            var cwd = Json.GetString(conversation, "cwd") ?? "";
            var title = Json.GetString(conversation, "title");
            var latestUserText = UserMessageText.Clean(ReadLatestUserText(conversation));
            var firstUserText = UserMessageText.Clean(ReadFirstUserText(conversation));
            var displayTitle = Json.GetBool(conversation, "sideConversation") && !string.IsNullOrWhiteSpace(firstUserText)
                ? firstUserText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : title;
            var parentId = SideConversationParentId(conversation);
            var updated = FromUnixMilliseconds(Json.GetLong(conversation, "updatedAt"));
            var item = new ThreadItem
            {
                Id = id, HostId = Json.GetString(conversation, "hostId") ?? "local", HostLabel = "本机",
                Cwd = cwd, Project = AppServerClient.ProjectName(cwd),
                Title = AppServerClient.Text(displayTitle, 42, "分栏任务"), NavigationTitle = displayTitle,
                Preview = AppServerClient.Text(latestUserText, 75, "Codex 分栏任务"),
                UpdatedAt = updated == DateTime.MinValue ? DateTime.Now : updated,
                IsSideConversation = Json.GetBool(conversation, "sideConversation"), ParentThreadId = parentId
            };
            item.NavigationTitleVerified = !string.IsNullOrWhiteSpace(displayTitle);
            item.SideParentVerified = item.IsSideConversation && !string.IsNullOrWhiteSpace(item.ParentThreadId);
            AppServerClient.ApplyStatus(item, status ?? new DesktopThreadStatus { Type = "idle" });
            return item;
        }

        private static string SideConversationParentId(IDictionary<string, object> conversation)
        {
            var parentPath = Json.GetString(conversation, "sideConversationParentNavigationPath") ?? "";
            return parentPath.StartsWith("/local/", StringComparison.OrdinalIgnoreCase)
                ? parentPath.Substring("/local/".Length).Split('?')[0]
                : null;
        }

        private static string ReadLatestUserText(IDictionary<string, object> conversation)
        {
            var history = Json.GetDictionary(conversation, "turnHistory");
            var canonical = Json.GetDictionary(history, "history");
            var entities = Json.GetDictionary(canonical, "entitiesByKey");
            string latest = null;
            foreach (var value in entities.Values)
            {
                var entity = value as IDictionary<string, object>;
                var parameters = Json.GetDictionary(entity, "params");
                foreach (var input in Json.GetArray(parameters, "input"))
                {
                    var row = input as IDictionary<string, object>;
                    if (row != null && string.Equals(Json.GetString(row, "type"), "text", StringComparison.OrdinalIgnoreCase))
                    {
                        var text = Json.GetString(row, "text");
                        if (!string.IsNullOrWhiteSpace(text)) latest = text.Trim();
                    }
                }
            }
            return latest;
        }

        private static string ReadFirstUserText(IDictionary<string, object> conversation)
        {
            var history = Json.GetDictionary(conversation, "turnHistory");
            var canonical = Json.GetDictionary(history, "history");
            var entities = Json.GetDictionary(canonical, "entitiesByKey");
            foreach (var value in entities.Values)
            {
                var parameters = Json.GetDictionary(value as IDictionary<string, object>, "params");
                foreach (var input in Json.GetArray(parameters, "input"))
                {
                    var row = input as IDictionary<string, object>;
                    if (row == null || !string.Equals(Json.GetString(row, "type"), "text", StringComparison.OrdinalIgnoreCase)) continue;
                    var text = Json.GetString(row, "text");
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            return null;
        }

        private static DateTime FromUnixMilliseconds(long value)
        {
            try
            {
                if (value <= 0) return DateTime.MinValue;
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(value).ToLocalTime();
            }
            catch { return DateTime.MinValue; }
        }

        private void ScheduleStatusReload(string threadId)
        {
            string hostId;
            lock (_snapshots)
            {
                if (!_followedHosts.TryGetValue(threadId, out hostId) || _refreshingThreads.Contains(threadId)) return;
                _refreshingThreads.Add(threadId);
            }
            Task.Run(async delegate
            {
                try
                {
                    await Task.Delay(80);
                    var status = await ReadThreadStatusAsync(threadId, hostId, 3000);
                    if (status != null)
                    {
                        var handler = StatusChanged;
                        if (handler != null) handler(threadId, hostId, status);
                    }
                }
                catch { }
                finally { lock (_snapshots) _refreshingThreads.Remove(threadId); }
            });
        }

        private string ReadTitlePatch(string text)
        {
            if (string.IsNullOrWhiteSpace(text) ||
                text.IndexOf("title", StringComparison.OrdinalIgnoreCase) < 0) return null;
            try
            {
                var message = _json.DeserializeObject(text) as IDictionary<string, object>;
                var parameters = Json.GetDictionary(message, "params");
                var change = Json.GetDictionary(parameters, "change");
                string title = null;
                foreach (var value in Json.GetArray(change, "patches"))
                {
                    var patch = value as IDictionary<string, object>;
                    if (patch == null) continue;
                    var pathParts = Json.GetArray(patch, "path").Select(Convert.ToString).ToArray();
                    var path = pathParts.Length > 0 ? pathParts[pathParts.Length - 1] : Json.GetString(patch, "path");
                    if (!string.Equals(path, "title", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(path, "threadName", StringComparison.OrdinalIgnoreCase) &&
                        !(path ?? "").EndsWith("/title", StringComparison.OrdinalIgnoreCase) &&
                        !(path ?? "").EndsWith("/threadName", StringComparison.OrdinalIgnoreCase)) continue;
                    var candidate = Json.GetString(patch, "value");
                    if (!string.IsNullOrWhiteSpace(candidate)) title = candidate.Trim();
                }
                return title;
            }
            catch { return null; }
        }

        internal void HandleRawForTest(string text) { HandleRaw(text); }

        private static string ExtractJsonString(string text, string property)
        {
            var marker = "\"" + property + "\":\"";
            var start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += marker.Length;
            var end = text.IndexOf('"', start);
            return end > start ? text.Substring(start, end - start) : null;
        }

        private static string ExtractJsonObject(string text, string property)
        {
            var marker = "\"" + property + "\":";
            var start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            start = text.IndexOf('{', start + marker.Length);
            if (start < 0) return null;
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < text.Length; index++)
            {
                var character = text[index];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') inString = false;
                    continue;
                }
                if (character == '"') { inString = true; continue; }
                if (character == '{') depth++;
                else if (character == '}' && --depth == 0) return text.Substring(start, index - start + 1);
            }
            return null;
        }

        private static bool ExtractJsonBoolean(string text, string property)
        {
            var marker = "\"" + property + "\":";
            var start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return false;
            start += marker.Length;
            return text.Length - start >= 4 && string.CompareOrdinal(text, start, "true", 0, 4) == 0;
        }

        public void Dispose()
        {
            _disposed = true;
            try { if (_pipe != null) _pipe.Dispose(); } catch { }
        }
    }

    internal static class UserMessageText
    {
        private const string FilesMarker = "# Files mentioned by the user:";
        private const string RequestMarker = "## My request:";

        public static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var text = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            var requestIndex = text.IndexOf(RequestMarker, StringComparison.OrdinalIgnoreCase);
            if (requestIndex >= 0)
                text = text.Substring(requestIndex + RequestMarker.Length).Trim();
            else if (text.StartsWith(FilesMarker, StringComparison.OrdinalIgnoreCase))
                return "";

            text = Regex.Replace(text, @"(?im)^\s*<image\s+[^>]*>\s*$", "").Trim();
            return text;
        }
    }

    internal static class SessionScanner
    {
        private static readonly Regex IdPattern = new Regex("([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\\.jsonl$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Dictionary<string, CachedSession> Cache = new Dictionary<string, CachedSession>(StringComparer.OrdinalIgnoreCase);

        public static List<ThreadItem> ScanLocal()
        {
            var performanceTimer = Stopwatch.StartNew();
            var rows = new List<ThreadItem>();
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            if (!Directory.Exists(root)) return rows;
            var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                existingFiles.Add(file);
                try
                {
                    var info = new FileInfo(file);
                    CachedSession cached;
                    if (Cache.TryGetValue(file, out cached) && cached.Length == info.Length && cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
                    {
                        rows.Add(Clone(cached.Item));
                        continue;
                    }
                    var match = IdPattern.Match(file);
                    if (!match.Success) continue;
                    var meta = ReadMeta(file);
                    if (meta == null || !string.IsNullOrEmpty(Json.GetString(meta, "parent_thread_id"))) continue;
                    var preview = ReadPreview(file);
                    var item = new ThreadItem
                    {
                        Id = match.Groups[1].Value, Cwd = Json.GetString(meta, "cwd") ?? "", RolloutPath = file,
                        UpdatedAt = info.LastWriteTime, HostLabel = "本机", HostId = "local", IsPinned = false
                    };
                    item.Project = AppServerClient.ProjectName(item.Cwd);
                    item.Title = AppServerClient.Text(preview, 42, "未命名任务");
                    item.Preview = AppServerClient.Text(preview, 75, "暂无任务摘要");
                    var active = ReadTailActive(file) && DateTime.Now - info.LastWriteTime < TimeSpan.FromMinutes(15);
                    AppServerClient.ApplyStatus(item, active ? "active" : "idle", new string[0]);
                    if (!active && DateTime.Now - item.UpdatedAt > TimeSpan.FromDays(7)) { item.Group = TaskGroup.History; item.StatusText = "历史任务"; }
                    rows.Add(item);
                    Cache[file] = new CachedSession { Length = info.Length, LastWriteTimeUtc = info.LastWriteTimeUtc, Item = Clone(item) };
                }
                catch { }
            }
            foreach (var path in Cache.Keys.Where(path => !existingFiles.Contains(path)).ToList()) Cache.Remove(path);
            var result = rows.OrderByDescending(x => x.UpdatedAt).Take(200).ToList();
            PerfDiagnostics.Duration("session-scan", performanceTimer, 500,
                "files=" + existingFiles.Count + " cache=" + Cache.Count + " threads=" + result.Count);
            return result;
        }

        private static ThreadItem Clone(ThreadItem item)
        {
            return new ThreadItem { Id = item.Id, Title = item.Title, NavigationTitle = item.NavigationTitle, Preview = item.Preview, Cwd = item.Cwd, Project = item.Project, HostLabel = item.HostLabel, HostId = item.HostId, UpdatedAt = item.UpdatedAt, Group = item.Group, StatusText = item.StatusText, IsPinned = item.IsPinned, RolloutPath = item.RolloutPath };
        }

        private sealed class CachedSession
        {
            public long Length;
            public DateTime LastWriteTimeUtc;
            public ThreadItem Item;
        }

        private static IDictionary<string, object> ReadMeta(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096))
            {
                for (var index = 0; index < 8; index++)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;
                    var record = Json.Parse(line);
                    if (Json.GetString(record, "type") == "session_meta") return Json.GetDictionary(record, "payload");
                }
            }
            return null;
        }

        private static string ReadPreview(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                const int chunkSize = 64 * 1024;
                const int maxBytes = 8 * 1024 * 1024;
                var floor = Math.Max(0, stream.Length - maxBytes);
                var position = stream.Length;
                var reversedLine = new List<byte>(4096);
                while (position > floor)
                {
                    var length = (int)Math.Min(chunkSize, position - floor);
                    position -= length;
                    stream.Seek(position, SeekOrigin.Begin);
                    var buffer = new byte[length];
                    var count = stream.Read(buffer, 0, length);
                    for (var index = count - 1; index >= 0; index--)
                    {
                        var value = buffer[index];
                        if (value == (byte)'\n')
                        {
                            var message = ReadUserMessageFromReversedLine(reversedLine);
                            reversedLine.Clear();
                            if (!string.IsNullOrWhiteSpace(message)) return message;
                        }
                        else if (value != (byte)'\r') reversedLine.Add(value);
                    }
                }
                if (floor == 0)
                {
                    var message = ReadUserMessageFromReversedLine(reversedLine);
                    if (!string.IsNullOrWhiteSpace(message)) return message;
                }
            }
            return "";
        }

        private static string ReadUserMessageFromReversedLine(List<byte> reversedLine)
        {
            if (reversedLine == null || reversedLine.Count == 0) return "";
            try
            {
                reversedLine.Reverse();
                var line = Encoding.UTF8.GetString(reversedLine.ToArray());
                var record = Json.Parse(line);
                if (Json.GetString(record, "type") != "event_msg") return "";
                var payload = Json.GetDictionary(record, "payload");
                if (Json.GetString(payload, "type") != "user_message") return "";
                return UserMessageText.Clean(Json.GetString(payload, "message"));
            }
            catch { return ""; }
            finally { reversedLine.Reverse(); }
        }

        private static bool ReadTailActive(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                const int chunkSize = 256 * 1024;
                var position = stream.Length;
                var laterPrefix = "";
                while (position > 0)
                {
                    var length = (int)Math.Min(chunkSize, position);
                    position -= length;
                    stream.Seek(position, SeekOrigin.Begin);
                    var buffer = new byte[length];
                    stream.Read(buffer, 0, length);
                    var text = Encoding.UTF8.GetString(buffer) + laterPrefix;
                    var started = text.LastIndexOf("\"type\":\"task_started\"", StringComparison.Ordinal);
                    var completed = text.LastIndexOf("\"type\":\"task_complete\"", StringComparison.Ordinal);
                    var aborted = text.LastIndexOf("\"type\":\"turn_aborted\"", StringComparison.Ordinal);
                    var latestFinished = Math.Max(completed, aborted);
                    if (started >= 0 || latestFinished >= 0) return started > latestFinished;
                    laterPrefix = text.Substring(0, Math.Min(64, text.Length));
                }
                return false;
            }
        }
    }

    internal static class ThreadTitleCatalog
    {
        private const int MaxRuntimeTitles = 2000;
        private static readonly object RuntimeSync = new object();
        private static readonly Dictionary<string, string> RuntimeTitles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> RuntimeTitleOrder = new Queue<string>();
        private static string _runtimePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexProjectCenter", "thread-titles.json");
        private static bool _runtimeLoaded;
        private static Timer _runtimeSaveTimer;
        private static readonly object IndexSync = new object();
        private static DateTime _indexWriteUtc = DateTime.MinValue;
        private static long _indexLength = -1;
        private static Dictionary<string, string> _indexTitles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void Remember(ThreadItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id)) return;
            var remote = !string.IsNullOrWhiteSpace(item.HostId) &&
                !string.Equals(item.HostId, "local", StringComparison.OrdinalIgnoreCase);
            if (remote && !item.NavigationTitleVerified) return;
            var title = string.IsNullOrWhiteSpace(item.NavigationTitle) ? item.Title : item.NavigationTitle;
            if (string.IsNullOrWhiteSpace(title)) return;
            EnsureRuntimeLoaded();
            var changed = false;
            lock (RuntimeSync)
            {
                var key = ThreadIdentity.Key(item);
                string existing;
                if (RuntimeTitles.TryGetValue(key, out existing) && string.Equals(existing, title.Trim(), StringComparison.Ordinal)) return;
                if (!RuntimeTitles.ContainsKey(key)) RuntimeTitleOrder.Enqueue(key);
                RuntimeTitles[key] = title.Trim();
                while (RuntimeTitles.Count > MaxRuntimeTitles && RuntimeTitleOrder.Count > 0)
                    RuntimeTitles.Remove(RuntimeTitleOrder.Dequeue());
                changed = true;
            }
            if (changed) ScheduleRuntimeSave();
        }

        public static Dictionary<string, string> Read(IEnumerable<ThreadItem> items)
        {
            EnsureRuntimeLoaded();
            var result = ReadIndexSnapshot();
            lock (RuntimeSync)
                foreach (var pair in RuntimeTitles) result[pair.Key] = pair.Value;
            return result;
        }

        public static string Resolve(string threadId, string hostId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return null;
            EnsureRuntimeLoaded();
            string runtimeTitle;
            lock (RuntimeSync)
                if (RuntimeTitles.TryGetValue(ThreadIdentity.Key(threadId, hostId), out runtimeTitle)) return runtimeTitle;
            if (!string.IsNullOrWhiteSpace(hostId) && !string.Equals(hostId, "local", StringComparison.OrdinalIgnoreCase)) return null;
            var result = ReadIndexSnapshot();
            string title;
            return result.TryGetValue(ThreadIdentity.Key(threadId, hostId), out title) ? title : null;
        }

        public static void RemoveLocalAlias(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return;
            EnsureRuntimeLoaded();
            var changed = false;
            lock (RuntimeSync)
                changed = RuntimeTitles.Remove(ThreadIdentity.Key(threadId, "local"));
            if (changed) ScheduleRuntimeSave();
        }

        private static void EnsureRuntimeLoaded()
        {
            lock (RuntimeSync)
            {
                if (_runtimeLoaded) return;
                _runtimeLoaded = true;
                try
                {
                    if (!File.Exists(_runtimePath)) return;
                    var values = new JavaScriptSerializer().DeserializeObject(
                        File.ReadAllText(_runtimePath, Encoding.UTF8)) as IDictionary<string, object>;
                    if (values == null) return;
                    foreach (var pair in values)
                    {
                        var title = Convert.ToString(pair.Value);
                        if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(title)) continue;
                        if (!RuntimeTitles.ContainsKey(pair.Key)) RuntimeTitleOrder.Enqueue(pair.Key);
                        RuntimeTitles[pair.Key] = title.Trim();
                    }
                }
                catch (Exception ex) { AppLog.Error("Load runtime thread titles failed", ex); }
            }
        }

        private static void ScheduleRuntimeSave()
        {
            lock (RuntimeSync)
            {
                if (_runtimeSaveTimer == null)
                    _runtimeSaveTimer = new Timer(delegate { SaveRuntimeTitles(); }, null, 300, Timeout.Infinite);
                else _runtimeSaveTimer.Change(300, Timeout.Infinite);
            }
        }

        private static void SaveRuntimeTitles()
        {
            Dictionary<string, string> snapshot;
            string path;
            lock (RuntimeSync)
            {
                snapshot = new Dictionary<string, string>(RuntimeTitles, StringComparer.OrdinalIgnoreCase);
                path = _runtimePath;
            }
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(snapshot), Encoding.UTF8);
            }
            catch (Exception ex) { AppLog.Error("Save runtime thread titles failed", ex); }
        }

        internal static void ConfigureStorageForTest(string path)
        {
            lock (RuntimeSync)
            {
                if (_runtimeSaveTimer != null) { _runtimeSaveTimer.Dispose(); _runtimeSaveTimer = null; }
                RuntimeTitles.Clear();
                RuntimeTitleOrder.Clear();
                _runtimePath = path;
                _runtimeLoaded = false;
            }
        }

        internal static void FlushForTest() { SaveRuntimeTitles(); }

        private static Dictionary<string, string> ReadIndexSnapshot()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "session_index.jsonl");
            try
            {
                if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var info = new FileInfo(path);
                lock (IndexSync)
                {
                    if (info.LastWriteTimeUtc != _indexWriteUtc || info.Length != _indexLength)
                    {
                        var refreshed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        ReadIndex(path, "local", refreshed);
                        _indexTitles = refreshed;
                        _indexWriteUtc = info.LastWriteTimeUtc;
                        _indexLength = info.Length;
                    }
                    return new Dictionary<string, string>(_indexTitles, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Read thread title snapshot failed", ex);
                lock (IndexSync) return new Dictionary<string, string>(_indexTitles, StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void ReadIndex(string path, string hostId, IDictionary<string, string> result)
        {
            try
            {
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    var row = Json.Parse(line);
                    var id = Json.GetString(row, "id");
                    var title = Json.GetString(row, "thread_name");
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title)) result[ThreadIdentity.Key(id, hostId)] = title.Trim();
                }
            }
            catch (Exception ex) { AppLog.Error("Read thread title index failed", ex); }
        }

    }

    internal static class RemoteSessionScanner
    {
        private static readonly string PythonProgramV2 = string.Join("\n", new[]
        {
            "import os,json,re,time",
            "root=os.path.expanduser(os.environ.get('CODEX_HOME','~/.codex'))+'/sessions'",
            "pat=re.compile(r'([0-9a-fA-F-]{36})\\.jsonl$')",
            "out=[]",
            "names={}",
            "try:",
            " with open(os.path.expanduser(os.environ.get('CODEX_HOME','~/.codex'))+'/session_index.jsonl','r',encoding='utf-8',errors='ignore') as index_file:",
            "  for line in index_file:",
            "   try:",
            "    row=json.loads(line); names[row.get('id')]=row.get('thread_name')",
            "   except: pass",
            "except: pass",
            "def clean_message(value):",
            " value=str(value or '').strip()",
            " marker='## My request:'",
            " if marker in value: value=value.split(marker,1)[1].strip()",
            " return value",
            "def latest_active(path,size):",
            " pos=size; suffix=b''",
            " with open(path,'rb') as f:",
            "  while pos>0:",
            "   count=min(pos,262144); pos-=count; f.seek(pos); data=f.read(count)+suffix",
            "   started=data.rfind(b'\\\"type\\\":\\\"task_started\\\"')",
            "   completed=data.rfind(b'\\\"type\\\":\\\"task_complete\\\"')",
            "   aborted=data.rfind(b'\\\"type\\\":\\\"turn_aborted\\\"')",
            "   finished=max(completed,aborted)",
            "   if started>=0 or finished>=0: return started>finished",
            "   suffix=data[:64]",
            " return False",
            "for base,dirs,files in os.walk(root):",
            " for name in files:",
            "  if not name.endswith('.jsonl'): continue",
            "  path=os.path.join(base,name); match=pat.search(name)",
            "  if not match: continue",
            "  try:",
            "   stat=os.stat(path); meta=None; preview=''",
            "   with open(path,'r',encoding='utf-8',errors='ignore') as file:",
            "    read=0",
            "    for line in file:",
            "     read+=len(line)",
            "     try: record=json.loads(line); payload=record.get('payload') or {}",
            "     except: continue",
            "     if record.get('type')=='session_meta' and meta is None: meta=payload",
            "     if record.get('type')=='event_msg' and payload.get('type')=='user_message' and not preview:",
            "      value=clean_message(payload.get('message'))",
            "      if value: preview=value",
            "     if read>8388608 or (meta and preview): break",
            "   if not meta or meta.get('parent_thread_id'): continue",
            "   active=latest_active(path,stat.st_size) and time.time()-stat.st_mtime<=900",
            "   preview=(names.get(match.group(1)) or preview)",
            "   out.append({'id':match.group(1),'cwd':meta.get('cwd',''),'title':preview.splitlines()[0][:120] if preview else '未命名任务','preview':preview[:500],'updatedAt':int(stat.st_mtime),'status':'active' if active else 'idle'})",
            "  except: pass",
            "out.sort(key=lambda value:value['updatedAt'],reverse=True)",
            "print(json.dumps(out[:200],ensure_ascii=False))"
        });

        public static async Task<List<ThreadItem>> ScanAsync(JavaScriptSerializer json)
        {
            var performanceTimer = Stopwatch.StartNew();
            var result = new List<ThreadItem>();
            var connections = ReadConnections(json);
            foreach (var connection in connections)
            {
                try { result.AddRange(await ScanConnectionAsync(connection, json)); }
                catch (Exception ex) { AppLog.Error("Remote scan failed for " + (connection.DisplayName ?? connection.Hostname ?? "unknown"), ex); }
            }
            ApplyDesktopMetadata(result, json);
            PerfDiagnostics.Duration("ssh-scan", performanceTimer, 2000,
                "connections=" + connections.Count + " threads=" + result.Count);
            return result;
        }

        private static void ApplyDesktopMetadata(IEnumerable<ThreadItem> items, JavaScriptSerializer json)
        {
            try
            {
                var root = GlobalStateSnapshot.Read();
                object atomValue = null;
                var atom = root != null && root.TryGetValue("electron-persisted-atom-state", out atomValue)
                    ? atomValue as IDictionary<string, object> : null;
                if (atom == null && atomValue is string)
                    atom = json.DeserializeObject((string)atomValue) as IDictionary<string, object>;
                var descriptions = Json.GetDictionary(atom, "thread-descriptions-v1");
                var histories = Json.GetDictionary(atom, "prompt-history");
                foreach (var item in items)
                {
                    object descriptionValue;
                    if (descriptions.TryGetValue(item.Id, out descriptionValue))
                        item.Title = AppServerClient.Text(Convert.ToString(descriptionValue), 42, item.Title);
                    object historyValue;
                    var history = histories.TryGetValue(item.Id, out historyValue) ? historyValue as object[] : null;
                    if (history == null)
                    {
                        var list = historyValue as ArrayList;
                        if (list != null) history = list.ToArray();
                    }
                    if (history != null)
                    {
                        var latest = history.Select(Convert.ToString).LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
                        if (!string.IsNullOrWhiteSpace(latest)) item.Preview = AppServerClient.Text(latest, 75, item.Preview);
                    }
                }
            }
            catch (Exception ex) { AppLog.Error("Apply remote desktop metadata failed", ex); }
        }

        private static List<RemoteConnection> ReadConnections(JavaScriptSerializer json)
        {
            var root = GlobalStateSnapshot.Read();
            var result = new List<RemoteConnection>();
            foreach (var value in Json.GetArray(root, "codex-managed-remote-connections"))
            {
                var row = value as IDictionary<string, object>;
                if (row == null) continue;
                result.Add(new RemoteConnection { HostId = Json.GetString(row, "hostId"), DisplayName = Json.GetString(row, "displayName") ?? "远程", Hostname = Json.GetString(row, "hostname"), Identity = Json.GetString(row, "identity"), Port = (int)Math.Max(22, Json.GetLong(row, "sshPort")) });
            }
            return result;
        }

        private static async Task<List<ThreadItem>> ScanConnectionAsync(RemoteConnection connection, JavaScriptSerializer json)
        {
            var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(PythonProgramV2));
            var remoteCommand = "python3 -c \"import base64;exec(base64.b64decode('" + encoded + "').decode())\"";
            var arguments = "-T -o BatchMode=yes -o ConnectTimeout=6 -o ServerAliveInterval=10 -o ServerAliveCountMax=2";
            if (!string.IsNullOrWhiteSpace(connection.Identity)) arguments += " -i \"" + connection.Identity.Replace("\"", "") + "\"";
            arguments += " -p " + connection.Port + " " + connection.Hostname + " \"" + remoteCommand.Replace("\"", "\\\"") + "\"";
            var start = new ProcessStartInfo { FileName = ssh, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            using (var process = new Process { StartInfo = start })
            {
                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(12000)) { try { process.Kill(); } catch { } throw new TimeoutException("远程扫描超时"); }
                var output = await outputTask;
                await errorTask;
                if (process.ExitCode != 0) return new List<ThreadItem>();
                var values = json.DeserializeObject(output) as object[] ?? new object[0];
                var items = new List<ThreadItem>();
                foreach (var value in values)
                {
                    var row = value as IDictionary<string, object>;
                    if (row == null) continue;
                    var item = new ThreadItem
                    {
                        Id = Json.GetString(row, "id"), Cwd = Json.GetString(row, "cwd") ?? "", HostId = connection.HostId,
                        HostLabel = "远程 · " + connection.DisplayName, UpdatedAt = AppServerClient.FromUnix(Json.GetLong(row, "updatedAt")), IsPinned = false
                    };
                    item.Project = AppServerClient.ProjectName(item.Cwd);
                    item.Title = AppServerClient.Text(Json.GetString(row, "title"), 42, "未命名任务");
                    item.NavigationTitle = null;
                    item.NavigationTitleVerified = false;
                    item.Preview = AppServerClient.Text(Json.GetString(row, "preview"), 75, "暂无任务摘要");
                    AppServerClient.ApplyStatus(item, Json.GetString(row, "status"), new string[0]);
                    if (item.Group == TaskGroup.Completed && DateTime.Now - item.UpdatedAt > TimeSpan.FromDays(7)) { item.Group = TaskGroup.History; item.StatusText = "历史任务"; }
                    items.Add(item);
                }
                return items;
            }
        }

        private sealed class RemoteConnection
        {
            public string HostId;
            public string DisplayName;
            public string Hostname;
            public string Identity;
            public int Port;
        }
    }

    internal static class DesktopCandidateReader
    {
        public static Dictionary<string, string> Read(JavaScriptSerializer json)
        {
            var result = new Dictionary<string, string>();
            try
            {
                var root = GlobalStateSnapshot.Read();
                var assignments = Json.GetDictionary(root, "thread-project-assignments");
                var orders = Json.GetDictionary(root, "sidebar-project-thread-orders");
                foreach (var orderValue in orders.Values)
                {
                    var order = orderValue as IDictionary<string, object>;
                    foreach (var threadId in Json.GetArray(order, "threadIds").Select(Convert.ToString)) Add(result, assignments, threadId);
                }
                foreach (var threadId in Json.GetArray(root, "pinned-thread-ids").Select(Convert.ToString)) Add(result, assignments, threadId);
                foreach (var threadId in Json.GetArray(root, "projectless-thread-ids").Select(Convert.ToString)) Add(result, assignments, threadId);
            }
            catch { }
            return result;
        }

        private static void Add(Dictionary<string, string> result, IDictionary<string, object> assignments, string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return;
            object value;
            var assignment = assignments.TryGetValue(threadId, out value) ? value as IDictionary<string, object> : null;
            result[threadId] = Json.GetString(assignment, "hostId") ?? "local";
        }
    }

    internal static class DesktopLogCandidateReader
    {
        private static readonly Regex ConversationPattern = new Regex(
            @"conversationId=([0-9a-f]{8}-[0-9a-f-]{27})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex OwnerRoutePattern = new Regex(
            @"ownerRoutePath=/local/([0-9a-f]{8}-[0-9a-f-]{27})(?:\?([^\s]+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex QueryHostPattern = new Regex(@"(?:^|&)hostId=([^&]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const int TailBytesPerFile = 512 * 1024;

        internal static IList<KeyValuePair<string, string>> Read(int limit, string root = null)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (limit <= 0) return result;
            root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Local", "Codex", "Logs");
            if (!Directory.Exists(root)) return result;
            var candidates = new Dictionary<string, LogCandidate>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).Take(4).Select(file => file.FullName))
                    ReadTail(path, candidates);
            }
            catch (Exception ex) { AppLog.Error("Read desktop log candidates failed", ex); }
            return candidates.Values.OrderByDescending(value => value.SeenAtUtc).Take(limit)
                .Select(value => new KeyValuePair<string, string>(value.ThreadId, value.HostId)).ToList();
        }

        private static void ReadTail(string path, Dictionary<string, LogCandidate> candidates)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var start = Math.Max(0, stream.Length - TailBytesPerFile);
                stream.Seek(start, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, true))
                {
                    if (start > 0) reader.ReadLine();
                    string line;
                    var sequence = File.GetLastWriteTimeUtc(path);
                    while ((line = reader.ReadLine()) != null)
                    {
                        var route = OwnerRoutePattern.Match(line);
                        if (route.Success)
                        {
                            var host = "local";
                            var query = QueryHostPattern.Match(route.Groups[2].Value);
                            if (query.Success)
                            {
                                try { host = Uri.UnescapeDataString(query.Groups[1].Value); }
                                catch { host = query.Groups[1].Value; }
                            }
                            Add(candidates, route.Groups[1].Value, host, sequence = sequence.AddTicks(1));
                        }
                        var conversation = ConversationPattern.Match(line);
                        if (conversation.Success)
                            Add(candidates, conversation.Groups[1].Value,
                                DesktopHostCatalog.Resolve(conversation.Groups[1].Value, "local"), sequence = sequence.AddTicks(1));
                    }
                }
            }
        }

        private static void Add(Dictionary<string, LogCandidate> candidates, string threadId, string hostId, DateTime seenAtUtc)
        {
            if (string.IsNullOrWhiteSpace(threadId) || threadId.StartsWith("client-new-thread:", StringComparison.OrdinalIgnoreCase)) return;
            LogCandidate current;
            if (candidates.TryGetValue(threadId, out current) && current.SeenAtUtc >= seenAtUtc) return;
            candidates[threadId] = new LogCandidate { ThreadId = threadId, HostId = ThreadIdentity.Host(hostId), SeenAtUtc = seenAtUtc };
        }

        private sealed class LogCandidate
        {
            public string ThreadId;
            public string HostId;
            public DateTime SeenAtUtc;
        }
    }

    internal static class DesktopHostCatalog
    {
        private static readonly object Sync = new object();
        private static DateTime _lastWriteUtc = DateTime.MinValue;
        private static Dictionary<string, string> _hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static string Resolve(string threadId, string fallbackHostId)
        {
            string hostId;
            if (TryResolve(threadId, out hostId)) return hostId;
            return ThreadIdentity.Host(fallbackHostId);
        }

        internal static bool TryResolve(string threadId, out string hostId)
        {
            hostId = null;
            if (string.IsNullOrWhiteSpace(threadId)) return false;
            Refresh();
            lock (Sync)
            {
                if (!_hosts.TryGetValue(threadId, out hostId) || string.IsNullOrWhiteSpace(hostId))
                {
                    hostId = null;
                    return false;
                }
                hostId = ThreadIdentity.Host(hostId);
                return true;
            }
        }

        internal static string HostLabel(string hostId)
        {
            hostId = ThreadIdentity.Host(hostId);
            if (string.Equals(hostId, "local", StringComparison.OrdinalIgnoreCase)) return "本机";
            var separator = hostId.LastIndexOf(':');
            return "远程 · " + (separator >= 0 && separator + 1 < hostId.Length ? hostId.Substring(separator + 1) : hostId);
        }

        private static void Refresh()
        {
            try
            {
                var writeUtc = GlobalStateSnapshot.LastWriteTimeUtc;
                if (writeUtc == DateTime.MinValue) return;
                lock (Sync) if (writeUtc == _lastWriteUtc) return;
                var root = GlobalStateSnapshot.Read();
                var projectHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in Json.GetArray(root, "remote-projects"))
                {
                    var project = value as IDictionary<string, object>;
                    var projectId = Json.GetString(project, "id");
                    var hostId = Json.GetString(project, "hostId");
                    if (!string.IsNullOrWhiteSpace(projectId) && !string.IsNullOrWhiteSpace(hostId)) projectHosts[projectId] = hostId;
                }
                var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var assignments = Json.GetDictionary(root, "thread-project-assignments");
                foreach (var pair in assignments)
                {
                    var assignment = pair.Value as IDictionary<string, object>;
                    var hostId = Json.GetString(assignment, "hostId");
                    var projectId = Json.GetString(assignment, "projectId");
                    if (string.IsNullOrWhiteSpace(hostId) && !string.IsNullOrWhiteSpace(projectId)) projectHosts.TryGetValue(projectId, out hostId);
                    if (!string.IsNullOrWhiteSpace(hostId)) hosts[pair.Key] = hostId;
                }
                foreach (var pair in Json.GetDictionary(root, "sidebar-project-thread-orders"))
                {
                    string hostId;
                    if (!projectHosts.TryGetValue(pair.Key, out hostId)) continue;
                    var order = pair.Value as IDictionary<string, object>;
                    foreach (var threadId in Json.GetArray(order, "threadIds").Select(Convert.ToString).Where(id => !string.IsNullOrWhiteSpace(id)))
                        hosts[threadId] = hostId;
                }
                lock (Sync)
                {
                    _hosts = hosts;
                    _lastWriteUtc = writeUtc;
                }
            }
            catch (Exception ex) { AppLog.Error("Read desktop host catalog failed", ex); }
        }
    }

    internal sealed class CodexDesktopLogMonitor : IDisposable
    {
        private static readonly Regex ActivityPattern = new Regex(
            @"thread_stream_view_activity_changed active=(true|false) conversationId=([^\s]+).*?(?:hostId=([^\s]+))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ConversationPattern = new Regex(
            @"conversationId=([0-9a-f]{8}-[0-9a-f-]{27})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CompletionPattern = new Regex(
            @"show turn-complete conversationId=([0-9a-f]{8}-[0-9a-f-]{27})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TurnIdPattern = new Regex(
            @"turnId=([0-9a-f]{8}-[0-9a-f-]{27})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StartedPattern = new Regex(
            @"conversationId=([0-9a-f]{8}-[0-9a-f-]{27}).*method=turn/start", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex OwnerRoutePattern = new Regex(
            @"ownerRoutePath=/local/([0-9a-f]{8}-[0-9a-f-]{27})(?:\?([^\s]+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex WindowIdPattern = new Regex(@"windowId=([^\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex QueryHostPattern = new Regex(@"(?:^|&)hostId=([^&]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const int MaxParsedLineBytes = 256 * 1024;
        private readonly object _sync = new object();
        private readonly Dictionary<string, long> _positions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirtyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _discardUntilNewline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _root;
        private readonly string _statePath;
        private readonly Dictionary<string, DateTime> _recentCompletions = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _threadHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RecentActivity> _recentActiveThreads = new Dictionary<string, RecentActivity>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RecentRoute> _recentRoutes = new Dictionary<string, RecentRoute>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher _watcher;
        private Timer _debounce;
        private Timer _pollTimer;
        private Timer _positionSaveTimer;
        private DateTime _startedAtUtc;
        private int _pollCount;
        private int _reading;
        private bool _disposed;
        public event Action<string, string> ThreadSeen;
        public event Action<string, string> ThreadStarted;
        public event Action<string, string> ThreadCompleted;
        public event Action<string, string> ThreadViewed;
        public event Action<string, string> ThreadRenamed;

        public CodexDesktopLogMonitor(string root = null, string statePath = null)
        {
            _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Local", "Codex", "Logs");
            _statePath = statePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexProjectCenter", "desktop-log-cursors.json");
        }

        public void Start()
        {
            if (!Directory.Exists(_root)) return;
            _startedAtUtc = DateTime.UtcNow;
            LoadPositions();
            foreach (var path in Directory.EnumerateFiles(_root, "*.log", SearchOption.AllDirectories))
            {
                var length = new FileInfo(path).Length;
                lock (_sync)
                {
                    long position;
                    if (!_positions.TryGetValue(path, out position) || position < 0 || position > length)
                        _positions[path] = length;
                }
            }
            _watcher = new FileSystemWatcher(_root, "*.log")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnCreated;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += delegate { DiscoverNewLogFiles(); MarkAllLogsDirty(); ScheduleRead(); };
            _watcher.EnableRaisingEvents = true;
            _pollTimer = new Timer(delegate
            {
                if (Interlocked.Increment(ref _pollCount) % 12 == 0) DiscoverNewLogFiles();
                MarkAllLogsDirty();
                ScheduleRead();
            }, null, 5000, 5000);
            SchedulePositionSave();
            MarkAllLogsDirty();
            ScheduleRead();
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.FullPath))
            {
                AddFile(e.FullPath, false);
                MarkLogDirty(e.FullPath);
            }
            ScheduleRead();
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.FullPath))
            {
                AddFile(e.FullPath, true);
                MarkLogDirty(e.FullPath);
            }
            ScheduleRead();
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.FullPath))
            {
                AddFile(e.FullPath, true);
                MarkLogDirty(e.FullPath);
            }
            ScheduleRead();
        }

        private void AddFile(string path, bool createdNow)
        {
            var added = false;
            lock (_sync)
            {
                if (_positions.ContainsKey(path)) return;
                long length = 0;
                try { if (File.Exists(path)) length = new FileInfo(path).Length; }
                catch { }
                var recentCreation = false;
                try { recentCreation = File.GetCreationTimeUtc(path) >= _startedAtUtc.AddSeconds(-2); }
                catch { }
                _positions[path] = createdNow || recentCreation ? 0 : length;
                _dirtyPaths.Add(path);
                added = true;
            }
            if (added) SchedulePositionSave();
        }

        private void MarkLogDirty(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            lock (_sync)
                if (_positions.ContainsKey(path)) _dirtyPaths.Add(path);
        }

        private void MarkAllLogsDirty()
        {
            lock (_sync)
                foreach (var path in _positions.Keys) _dirtyPaths.Add(path);
        }

        private void ScheduleRead()
        {
            if (_disposed) return;
            lock (_sync)
            {
                if (_debounce == null) _debounce = new Timer(delegate { ReadChanges(); }, null, 120, Timeout.Infinite);
                else _debounce.Change(120, Timeout.Infinite);
            }
        }

        private void ReadChanges()
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _reading, 1) != 0) return;
            var performanceTimer = Stopwatch.StartNew();
            var processedPaths = 0;
            long bytesRead = 0;
            try
            {
                List<string> paths;
                lock (_sync)
                {
                    paths = _dirtyPaths.ToList();
                    _dirtyPaths.Clear();
                }
                var positionChanged = false;
                foreach (var path in paths)
                {
                    if (!File.Exists(path)) continue;
                    long start;
                    lock (_sync) _positions.TryGetValue(path, out start);
                    var length = new FileInfo(path).Length;
                    if (length == start) continue;
                    processedPaths++;
                    bytesRead += Math.Max(0, length - start);
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (start > stream.Length)
                        {
                            lock (_sync) _positions[path] = stream.Length;
                            positionChanged = true;
                            continue;
                        }
                        stream.Seek(start, SeekOrigin.Begin);
                        var committed = start;
                        var buffer = new byte[8192];
                        bool discardLine;
                        lock (_sync) discardLine = _discardUntilNewline.Contains(path);
                        using (var lineBuffer = new MemoryStream())
                        {
                            int count;
                            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                var blockStart = stream.Position - count;
                                var segmentStart = 0;
                                for (var index = 0; index < count; index++)
                                {
                                    if (buffer[index] != (byte)'\n') continue;
                                    var segmentLength = index - segmentStart;
                                    if (!discardLine && segmentLength > 0)
                                    {
                                        if (lineBuffer.Length + segmentLength <= MaxParsedLineBytes)
                                            lineBuffer.Write(buffer, segmentStart, segmentLength);
                                        else discardLine = true;
                                    }
                                    if (!discardLine)
                                    {
                                        var bytes = lineBuffer.ToArray();
                                        var lineLength = bytes.Length;
                                        if (lineLength > 0 && bytes[lineLength - 1] == (byte)'\r') lineLength--;
                                        ProcessLine(Encoding.UTF8.GetString(bytes, 0, lineLength));
                                    }
                                    lineBuffer.SetLength(0);
                                    discardLine = false;
                                    segmentStart = index + 1;
                                    committed = blockStart + index + 1;
                                }
                                if (!discardLine && segmentStart < count)
                                {
                                    var remaining = count - segmentStart;
                                    if (lineBuffer.Length + remaining <= MaxParsedLineBytes)
                                        lineBuffer.Write(buffer, segmentStart, remaining);
                                    else discardLine = true;
                                }
                            }
                        }
                        lock (_sync)
                        {
                            if (discardLine)
                            {
                                _discardUntilNewline.Add(path);
                                committed = stream.Position;
                            }
                            else _discardUntilNewline.Remove(path);
                        }
                        if (committed != start)
                        {
                            lock (_sync) _positions[path] = committed;
                            positionChanged = true;
                        }
                    }
                }
                if (positionChanged) SchedulePositionSave();
            }
            catch { }
            finally
            {
                PerfDiagnostics.Duration("desktop-log-read", performanceTimer, 100,
                    "paths=" + processedPaths + " bytes=" + bytesRead);
                Interlocked.Exchange(ref _reading, 0);
                bool pending;
                lock (_sync) pending = _dirtyPaths.Count > 0;
                if (pending) ScheduleRead();
            }
        }

        private void DiscoverNewLogFiles()
        {
            foreach (var path in Directory.EnumerateFiles(_root, "*.log", SearchOption.AllDirectories))
                AddFile(path, false);
        }

        private void ProcessLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (line.IndexOf("ownerRoutePath=/local/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var route = OwnerRoutePattern.Match(line);
                if (route.Success)
                {
                    var routeThreadId = route.Groups[1].Value;
                    var hostId = "local";
                    var queryHost = QueryHostPattern.Match(route.Groups[2].Value);
                    if (queryHost.Success)
                    {
                        try { hostId = Uri.UnescapeDataString(queryHost.Groups[1].Value); }
                        catch { hostId = queryHost.Groups[1].Value; }
                    }
                    hostId = DesktopHostCatalog.Resolve(routeThreadId, hostId);
                    var window = WindowIdPattern.Match(line);
                    var windowId = window.Success ? window.Groups[1].Value : "";
                    List<string> related;
                    lock (_sync)
                    {
                        _threadHosts[routeThreadId] = hostId;
                        if (!string.IsNullOrWhiteSpace(windowId)) _recentRoutes[windowId] = new RecentRoute { HostId = hostId, SeenAtUtc = DateTime.UtcNow };
                        related = _recentActiveThreads.Where(pair => DateTime.UtcNow - pair.Value.SeenAtUtc <= TimeSpan.FromSeconds(2) &&
                            (string.IsNullOrWhiteSpace(windowId) || string.Equals(pair.Value.WindowId, windowId, StringComparison.OrdinalIgnoreCase)))
                            .Select(pair => pair.Key).ToList();
                        foreach (var threadId in related) _threadHosts[threadId] = hostId;
                    }
                    NavigationEventCatalog.ObserveRoute(routeThreadId, hostId);
                    RaiseSeen(routeThreadId, hostId);
                    foreach (var threadId in related)
                    {
                        RaiseSeen(threadId, hostId);
                        NavigationEventCatalog.ObserveView(threadId, hostId, true);
                        var viewed = ThreadViewed;
                        if (viewed != null) viewed(threadId, hostId);
                    }
                    return;
                }
            }
            if (line.IndexOf("method=turn/start", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var started = StartedPattern.Match(line);
                if (started.Success)
                {
                    var startedId = started.Groups[1].Value;
                    var hostId = ResolveHost(startedId, "local");
                    RaiseSeen(startedId, hostId);
                    var startHandler = ThreadStarted;
                    if (startHandler != null) startHandler(startedId, hostId);
                    return;
                }
            }
            if (line.IndexOf("show turn-complete", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var completion = CompletionPattern.Match(line);
                if (completion.Success)
                {
                    var completedId = completion.Groups[1].Value;
                    var turn = TurnIdPattern.Match(line);
                    var eventKey = completedId + "\n" + (turn.Success ? turn.Groups[1].Value : line);
                    lock (_sync)
                    {
                        if (_recentCompletions.ContainsKey(eventKey)) return;
                        _recentCompletions[eventKey] = DateTime.UtcNow;
                        if (_recentCompletions.Count > 512)
                            foreach (var key in _recentCompletions.Where(pair => DateTime.UtcNow - pair.Value > TimeSpan.FromDays(1)).Select(pair => pair.Key).ToList())
                                _recentCompletions.Remove(key);
                    }
                    AppLog.Info("Desktop completion log parsed thread=" + completedId);
                    var completed = ThreadCompleted;
                    if (completed != null) completed(completedId, ResolveHost(completedId, "local"));
                    return;
                }
            }
            if (line.IndexOf("method=thread/name/set", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var renamed = ConversationPattern.Match(line);
                if (renamed.Success)
                {
                    var renamedId = renamed.Groups[1].Value;
                    var renamedHandler = ThreadRenamed;
                    if (renamedHandler != null) renamedHandler(renamedId, ResolveHost(renamedId, "local"));
                    return;
                }
            }
            if (line.IndexOf("thread_stream_view_activity_changed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var activity = ActivityPattern.Match(line);
                if (activity.Success)
                {
                    var active = string.Equals(activity.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                    var threadId = activity.Groups[2].Value;
                    var window = WindowIdPattern.Match(line);
                    var windowId = window.Success ? window.Groups[1].Value : "";
                    var fallbackHost = activity.Groups[3].Success ? activity.Groups[3].Value : "local";
                    if (!activity.Groups[3].Success && !string.IsNullOrWhiteSpace(windowId))
                    {
                        lock (_sync)
                        {
                            RecentRoute recentRoute;
                            if (_recentRoutes.TryGetValue(windowId, out recentRoute) && DateTime.UtcNow - recentRoute.SeenAtUtc <= TimeSpan.FromSeconds(2))
                                fallbackHost = recentRoute.HostId;
                        }
                    }
                    var hostId = ResolveHost(threadId, fallbackHost);
                    NavigationEventCatalog.ObserveView(threadId, hostId, active);
                    if (active)
                    {
                        lock (_sync)
                        {
                            _recentActiveThreads[threadId] = new RecentActivity { SeenAtUtc = DateTime.UtcNow, WindowId = windowId };
                            _threadHosts[threadId] = hostId;
                        }
                    }
                    RaiseSeen(threadId, hostId);
                    if (active)
                    {
                        var viewed = ThreadViewed;
                        if (viewed != null) viewed(threadId, hostId);
                    }
                    return;
                }
            }
            if (line.IndexOf("thread_stream_role_changed", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("method=thread/inject_items", StringComparison.OrdinalIgnoreCase) < 0) return;
            var match = ConversationPattern.Match(line);
            if (match.Success) RaiseSeen(match.Groups[1].Value, "local");
        }

        private string ResolveHost(string threadId, string fallbackHostId)
        {
            lock (_sync)
            {
                string hostId;
                if (_threadHosts.TryGetValue(threadId, out hostId) && !string.IsNullOrWhiteSpace(hostId)) return hostId;
            }
            return DesktopHostCatalog.Resolve(threadId, fallbackHostId);
        }

        private void RaiseSeen(string threadId, string hostId)
        {
            if (string.IsNullOrWhiteSpace(threadId) || threadId.StartsWith("client-new-thread:", StringComparison.OrdinalIgnoreCase)) return;
            var seen = ThreadSeen;
            if (seen != null) seen(threadId, hostId);
        }

        private void LoadPositions()
        {
            try
            {
                if (!File.Exists(_statePath)) return;
                var values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(_statePath, Encoding.UTF8)) as IDictionary<string, object>;
                if (values == null) return;
                lock (_sync)
                    foreach (var pair in values)
                    {
                        long offset;
                        if (long.TryParse(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out offset)) _positions[pair.Key] = offset;
                    }
            }
            catch (Exception ex) { AppLog.Error("Load desktop log cursors failed", ex); }
        }

        private void SchedulePositionSave()
        {
            lock (_sync)
            {
                if (_positionSaveTimer == null) _positionSaveTimer = new Timer(delegate { SavePositions(); }, null, 500, Timeout.Infinite);
                else _positionSaveTimer.Change(500, Timeout.Infinite);
            }
        }

        private void SavePositions()
        {
            Dictionary<string, long> values;
            lock (_sync) values = new Dictionary<string, long>(_positions, StringComparer.OrdinalIgnoreCase);
            try
            {
                var directory = Path.GetDirectoryName(_statePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_statePath, new JavaScriptSerializer().Serialize(values), Encoding.UTF8);
            }
            catch (Exception ex) { AppLog.Error("Save desktop log cursors failed", ex); }
        }

        public void Dispose()
        {
            _disposed = true;
            if (_watcher != null) _watcher.Dispose();
            if (_debounce != null) _debounce.Dispose();
            if (_pollTimer != null) _pollTimer.Dispose();
            if (_positionSaveTimer != null) _positionSaveTimer.Dispose();
            SavePositions();
        }

        private sealed class RecentActivity
        {
            public DateTime SeenAtUtc;
            public string WindowId;
        }

        private sealed class RecentRoute
        {
            public DateTime SeenAtUtc;
            public string HostId;
        }
    }

    internal static class GlobalStateSnapshot
    {
        private static readonly object Sync = new object();
        private static readonly string PathValue = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", ".codex-global-state.json");
        private static DateTime _lastWriteUtc = DateTime.MinValue;
        private static long _length = -1;
        private static IDictionary<string, object> _root;

        internal static DateTime LastWriteTimeUtc
        {
            get
            {
                Refresh();
                lock (Sync) return _lastWriteUtc;
            }
        }

        internal static IDictionary<string, object> Read()
        {
            Refresh();
            lock (Sync) return _root ?? new Dictionary<string, object>();
        }

        private static void Refresh()
        {
            try
            {
                if (!File.Exists(PathValue)) return;
                var info = new FileInfo(PathValue);
                lock (Sync)
                {
                    if (_root != null && info.LastWriteTimeUtc == _lastWriteUtc && info.Length == _length) return;
                    var parsed = Json.Parse(File.ReadAllText(PathValue, Encoding.UTF8));
                    if (parsed == null) return;
                    _root = parsed;
                    _lastWriteUtc = info.LastWriteTimeUtc;
                    _length = info.Length;
                }
            }
            catch (Exception ex) { AppLog.Error("Read global state snapshot failed", ex); }
        }
    }

    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
        public static IDictionary<string, object> Parse(string text) { try { return Serializer.DeserializeObject(text) as IDictionary<string, object>; } catch { return null; } }
        public static IDictionary<string, object> GetDictionary(IDictionary<string, object> source, string key) { object value; return source != null && source.TryGetValue(key, out value) ? value as IDictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>(); }
        public static object[] GetArray(IDictionary<string, object> source, string key) { object value; if (source == null || !source.TryGetValue(key, out value) || value == null) return new object[0]; var array = value as object[]; if (array != null) return array; var list = value as ArrayList; return list == null ? new object[0] : list.ToArray(); }
        public static string GetString(IDictionary<string, object> source, string key) { object value; return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null; }
        public static long GetLong(IDictionary<string, object> source, string key) { long result; return long.TryParse(GetString(source, key), NumberStyles.Any, CultureInfo.InvariantCulture, out result) ? result : 0; }
        public static bool GetBool(IDictionary<string, object> source, string key) { bool result; return bool.TryParse(GetString(source, key), out result) && result; }
    }

    internal static class NotificationWindowDiagnostics
    {
        internal static void Run(string outputPath)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new WaitingNotificationWindow(
                new[] { new ThreadItem { Id = "notification-style-test", Project = "test", Title = "test" } },
                delegate { }, delegate { });
            window.Opacity = 0;
            window.Show();
            var handle = new WindowInteropHelper(window).Handle;
            var style = NativeMethods.GetWindowExtendedStyle(handle);
            var result = new Dictionary<string, object>
            {
                { "showInTaskbarDisabled", !window.ShowInTaskbar },
                { "showActivatedDisabled", !window.ShowActivated },
                { "toolWindowEnabled", (style & NativeMethods.WindowExtendedStyleToolWindow) != 0 },
                { "noActivateEnabled", (style & NativeMethods.WindowExtendedStyleNoActivate) != 0 },
                { "appWindowDisabled", (style & NativeMethods.WindowExtendedStyleAppWindow) == 0 }
            };
            window.Close();
            app.Shutdown();
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result), Encoding.UTF8);
        }
    }

    internal static class NavigationEventDiagnostics
    {
        internal static void Run(string outputPath)
        {
            var localId = "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb";
            var remoteId = "cccccccc-1111-2222-3333-dddddddddddd";
            var sideId = "eeeeeeee-1111-2222-3333-ffffffffffff";
            var before = DateTime.Now.AddMilliseconds(-100);
            NavigationEventCatalog.ObserveRoute(localId, "local");
            NavigationEventCatalog.ObserveRoute(remoteId, "remote-ssh-codex-managed:work");
            NavigationEventCatalog.ObserveView(sideId, "remote-ssh-codex-managed:work", true);
            var result = new Dictionary<string, object>
            {
                { "localRouteAccepted", NavigationEventCatalog.WasOpenedSince(new ThreadItem { Id = localId, HostId = "local" }, before) },
                { "remoteHostAccepted", NavigationEventCatalog.WasOpenedSince(new ThreadItem { Id = remoteId, HostId = "remote-ssh-codex-managed:work" }, before) },
                { "remoteWrongHostRejected", !NavigationEventCatalog.WasOpenedSince(new ThreadItem { Id = remoteId, HostId = "remote-ssh-codex-managed:other" }, before) },
                { "sideViewAccepted", NavigationEventCatalog.WasOpenedSince(new ThreadItem { Id = sideId, HostId = "remote-ssh-codex-managed:work", IsSideConversation = true }, before) }
            };
            NavigationEventCatalog.ObserveView(sideId, "remote-ssh-codex-managed:work", false);
            result["inactiveSideRejected"] = !NavigationEventCatalog.IsCurrentlyViewed(sideId);
            NavigationEventCatalog.ObserveRoute(localId, "local");
            result["latestRouteOverridesPrevious"] = !NavigationEventCatalog.IsCurrentlyRouted(remoteId, "remote-ssh-codex-managed:work");
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result), Encoding.UTF8);
        }
    }

    internal static class HeadlessDiagnostics
    {
        public static void Run(string outputPath)
        {
            var client = new AppServerClient();
            var items = new List<ThreadItem>();
            var status = "";
            client.ThreadsReceived += value => items = value.ToList();
            client.ConnectionChanged += value => status = value;
            var stopwatch = Stopwatch.StartNew();
            try { client.RefreshAsync().GetAwaiter().GetResult(); Thread.Sleep(500); }
            catch (Exception ex) { status = "ERROR: " + ex; }
            stopwatch.Stop();
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var idleStartCpu = process.TotalProcessorTime;
            var idleStopwatch = Stopwatch.StartNew();
            Thread.Sleep(3000);
            idleStopwatch.Stop();
            process.Refresh();
            var idleCpuPercent = (process.TotalProcessorTime - idleStartCpu).TotalMilliseconds / (idleStopwatch.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100d;
            var report = new Dictionary<string, object>
            {
                { "ok", !status.StartsWith("ERROR") }, { "status", status }, { "elapsedMs", stopwatch.ElapsedMilliseconds },
                { "total", items.Count }, { "waiting", items.Count(x => x.Group == TaskGroup.Waiting) },
                { "running", items.Count(x => x.Group == TaskGroup.Running) }, { "completed", items.Count(x => x.Group == TaskGroup.Completed) },
                { "local", items.Count(x => x.HostLabel == "本机") }, { "remote", items.Count(x => x.HostLabel.StartsWith("远程", StringComparison.Ordinal)) },
                { "workingSetMb", Math.Round(process.WorkingSet64 / 1024d / 1024d, 1) },
                { "idleCpuPercent", Math.Round(idleCpuPercent, 3) },
                { "sample", items.Where(x => x.Group == TaskGroup.Waiting || x.Group == TaskGroup.Running).Take(10).Select(x => new Dictionary<string, object> { { "id", x.Id }, { "title", x.Title }, { "host", x.HostLabel }, { "status", x.StatusText } }).ToArray() }
            };
            var waitingApproval = new ThreadItem();
            AppServerClient.ApplyStatus(waitingApproval, "active", new[] { "waitingOnApproval" });
            var waitingInput = new ThreadItem();
            AppServerClient.ApplyStatus(waitingInput, "active", new[] { "waitingOnUserInput" });
            var unreadResult = new ThreadItem();
            AppServerClient.ApplyStatus(unreadResult, new DesktopThreadStatus { Type = "idle", HasUnreadTurn = true });
            report["classificationChecks"] = new Dictionary<string, object>
            {
                { "waitingOnApproval", waitingApproval.Group == TaskGroup.Waiting && waitingApproval.StatusText == "等待批准" },
                { "waitingOnUserInput", waitingInput.Group == TaskGroup.Waiting && waitingInput.StatusText == "等待回复" }
            };
            ((Dictionary<string, object>)report["classificationChecks"])["unreadResult"] = unreadResult.Group == TaskGroup.Waiting && unreadResult.StatusText == "有新结果";
            var titles = ThreadTitleCatalog.Read(items);
            string currentTitle;
            report["navigationChecks"] = new Dictionary<string, object>
            {
                { "localTitle", titles.TryGetValue(ThreadIdentity.Key("019ff551-b41e-73f0-add0-ff960c485e1c", "local"), out currentTitle) ? currentTitle : null },
                { "oldLogRejected", !MainWindow.VerifyOpenedThreadForTest(new ThreadItem { Id = "11111111-2222-3333-4444-555555555555", HostId = "remote-ssh-codex-managed:work" }, DateTime.Now) },
                { "cardUsesSidebarTitle", AppServerClient.DisplayTitle(new ThreadItem { Title = "旧消息", NavigationTitle = "侧栏名称" }) == "侧栏名称" },
                { "sideCardKeepsOwnTitle", AppServerClient.DisplayTitle(new ThreadItem { Title = "侧边任务", NavigationTitle = "父任务", IsSideConversation = true }) == "侧边任务" }
            };
            var tooltipScore = CodexWindowLocator.ScoreCandidateForTest(
                "MicrosoftWindowsTooltip", "", true, false, 27, 18);
            var tinyChromeScore = CodexWindowLocator.ScoreCandidateForTest(
                "Chrome_WidgetWin_1", "ChatGPT", true, false, 27, 18);
            var mainWindowScore = CodexWindowLocator.ScoreCandidateForTest(
                "Chrome_WidgetWin_1", "ChatGPT", true, false, 1933, 1045);
            var secondaryWindowScore = CodexWindowLocator.ScoreCandidateForTest(
                "Chrome_WidgetWin_1", "", true, false, 900, 600);
            var minimizedWindowScore = CodexWindowLocator.ScoreCandidateForTest(
                "Chrome_WidgetWin_1", "ChatGPT", true, true, 160, 28);
            report["windowLocatorChecks"] = new Dictionary<string, object>
            {
                { "tooltipRejected", tooltipScore == long.MinValue },
                { "tinyChromeRejected", tinyChromeScore == long.MinValue },
                { "mainWindowAccepted", mainWindowScore != long.MinValue },
                { "mainWindowPreferred", mainWindowScore > secondaryWindowScore },
                { "minimizedMainWindowAccepted", minimizedWindowScore != long.MinValue }
            };
            report["completionChecks"] = new Dictionary<string, object>
            {
                { "runningToIdleNeedsReview", AppServerClient.ShouldTreatCompletionAsWaiting(TaskGroup.Running, new DesktopThreadStatus { Type = "idle" }) },
                { "completedToIdleNoDuplicate", !AppServerClient.ShouldTreatCompletionAsWaiting(TaskGroup.Completed, new DesktopThreadStatus { Type = "idle" }) },
                { "runningToActiveNoFalsePositive", !AppServerClient.ShouldTreatCompletionAsWaiting(TaskGroup.Running, new DesktopThreadStatus { Type = "active" }) }
            };
            report["statusCacheChecks"] = new Dictionary<string, object>
            {
                { "activeCacheSeconds", 2 }, { "idleCacheSeconds", 45 }, { "completionInvalidatesCache", true }
            };
            report["messageCleaningChecks"] = new Dictionary<string, object>
            {
                { "attachmentRequest", UserMessageText.Clean("# Files mentioned by the user:\n\n## sample.png: C:/sample.png\n\n## My request:\n为什么鼠标选中的边界感觉不清晰") == "为什么鼠标选中的边界感觉不清晰" },
                { "plainMessage", UserMessageText.Clean("普通任务内容") == "普通任务内容" },
                { "attachmentOnly", UserMessageText.Clean("# Files mentioned by the user:\n\n## sample.png: C:/sample.png") == "" }
            };
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(report), Encoding.UTF8);
            client.Dispose();
        }
    }

    internal static class DiscoveryDiagnostics
    {
        public static void Run(string outputPath, string threadId, string hostId)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
            var ipc = new DesktopIpcClient(json);
            ThreadItem item = null;
            DesktopThreadStatus status = null;
            ipc.ThreadDiscovered += delegate(ThreadItem value, DesktopThreadStatus current) { item = value; status = current; };
            try
            {
                ipc.ConnectAsync(5000).GetAwaiter().GetResult();
                ipc.DiscoverThreadAsync(threadId, hostId ?? "local", 5000).GetAwaiter().GetResult();
                if (item != null)
                {
                    ThreadTitleCatalog.Remember(item);
                    ThreadTitleCatalog.FlushForTest();
                }
                File.WriteAllText(outputPath, json.Serialize(new Dictionary<string, object>
                {
                    { "found", item != null }, { "id", item == null ? null : item.Id },
                    { "title", item == null ? null : item.Title }, { "preview", item == null ? null : item.Preview },
                    { "navigationTitle", item == null ? null : item.NavigationTitle },
                    { "navigationTitleVerified", item != null && item.NavigationTitleVerified },
                    { "sideConversation", item != null && item.IsSideConversation },
                    { "parentThreadId", item == null ? null : item.ParentThreadId },
                    { "status", status == null ? null : status.Type },
                    { "hasUnreadTurn", status != null && status.HasUnreadTurn },
                    { "flags", status == null ? new string[0] : status.Flags }
                }), Encoding.UTF8);
            }
            finally { ipc.Dispose(); }
        }
    }

    internal static class TitleSyncDiagnostics
    {
        public static void Run(string outputPath)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
            var threadId = "55555555-bbbb-cccc-dddd-eeeeeeeeeeee";
            var stateDirectory = Path.Combine(Path.GetTempPath(), "codex-project-center-title-test-" + Guid.NewGuid().ToString("N"));
            var titleStorePath = Path.Combine(stateDirectory, "thread-titles.json");
            ThreadTitleCatalog.ConfigureStorageForTest(titleStorePath);
            var patchTitles = new List<string>();
            var directTitles = new List<string>();
            using (var ipc = new DesktopIpcClient(json))
            {
                ipc.TitleChanged += delegate(string id, string host, string title)
                {
                    if (string.Equals(id, threadId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (title == "增量新标题") patchTitles.Add(host + "|" + title);
                        if (title == "直接新标题") directTitles.Add(host + "|" + title);
                    }
                };
                ipc.HandleRawForTest("{\"type\":\"broadcast\",\"method\":\"thread-stream-state-changed\",\"params\":{\"conversationId\":\"" +
                    threadId + "\",\"hostId\":\"remote-ssh-codex-managed:work\",\"change\":{\"type\":\"patches\",\"baseRevision\":1,\"revision\":2,\"patches\":[{\"op\":\"replace\",\"path\":[\"title\"],\"value\":\"增量新标题\"}]}}}");
                ipc.HandleRawForTest("{\"type\":\"broadcast\",\"method\":\"thread/name/updated\",\"params\":{\"threadId\":\"" +
                    threadId + "\",\"hostId\":\"remote-ssh-codex-managed:work\",\"threadName\":\"直接新标题\"}}");
            }
            var client = new AppServerClient(stateDirectory);
            var cardUpdated = false;
            var titlePersisted = false;
            try
            {
                client.MergeDiscoveredThreadForTest(new ThreadItem
                {
                    Id = threadId,
                    HostId = "remote-ssh-codex-managed:work",
                    HostLabel = "remote work",
                    Title = "旧标题",
                    NavigationTitle = "旧标题",
                    NavigationTitleVerified = true,
                    UpdatedAt = DateTime.Now
                }, new DesktopThreadStatus { Type = "idle", HasUnreadTurn = true });
                client.OnIpcTitleChangedForTest(threadId, "remote-ssh-codex-managed:work", "卡片新标题");
                cardUpdated = client.GetSnapshotForTest().Any(item => item.Id == threadId &&
                    item.Title == "卡片新标题" && item.NavigationTitle == "卡片新标题" && item.NavigationTitleVerified);
                ThreadTitleCatalog.FlushForTest();
                titlePersisted = File.Exists(titleStorePath) &&
                    File.ReadAllText(titleStorePath, Encoding.UTF8).IndexOf("卡片新标题", StringComparison.Ordinal) >= 0;
            }
            finally
            {
                client.Dispose();
                try { if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, true); }
                catch { }
            }
            File.WriteAllText(outputPath, json.Serialize(new Dictionary<string, object>
            {
                { "patchEventParsed", patchTitles.Contains("remote-ssh-codex-managed:work|增量新标题") },
                { "directEventParsed", directTitles.Contains("remote-ssh-codex-managed:work|直接新标题") },
                { "cardUpdated", cardUpdated },
                { "titlePersisted", titlePersisted }
            }), Encoding.UTF8);
        }
    }

    internal static class SideNavigationDiagnostics
    {
        public static void Run(string outputPath, string threadId, string parentThreadId, string project, string title, string hostId)
        {
            var item = new ThreadItem
            {
                Id = threadId, ParentThreadId = parentThreadId, HostId = hostId ?? "local", Project = project,
                Title = title, NavigationTitle = title, IsSideConversation = true
            };
            var opened = MainWindow.OpenSideConversationForTest(item);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                { "opened", opened }, { "threadId", threadId }, { "parentThreadId", parentThreadId },
                { "project", project }, { "title", title }, { "hostId", hostId }
            }), Encoding.UTF8);
        }
    }

    internal static class SidebarNavigationDiagnostics
    {
        public static void Run(string outputPath, string threadId, string project, string title, string hostId)
        {
            var item = new ThreadItem
            {
                Id = threadId, HostId = hostId ?? "local", Project = project,
                Title = title, NavigationTitle = title
            };
            var activated = MainWindow.ActivateSidebarThreadForTest(item);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                { "activated", activated }, { "threadId", threadId }, { "project", project },
                { "title", title }, { "hostId", hostId }
            }), Encoding.UTF8);
        }
    }

    internal static class DiscoveryCacheDiagnostics
    {
        public static void Run(string outputPath)
        {
            var stateDirectory = Path.Combine(Path.GetTempPath(), "codex-project-center-cache-test-" + Guid.NewGuid().ToString("N"));
            ThreadTitleCatalog.ConfigureStorageForTest(Path.Combine(stateDirectory, "thread-titles.json"));
            var client = new AppServerClient(stateDirectory);
            try
            {
                var identityParent = new ThreadItem
                {
                    Id = "13131313-bbbb-cccc-dddd-eeeeeeeeeeee",
                    HostId = "remote-ssh-codex-managed:work",
                    HostLabel = "remote work",
                    Title = "remote parent",
                    NavigationTitle = "remote parent",
                    NavigationTitleVerified = true,
                    UpdatedAt = DateTime.Now
                };
                var identitySide = new ThreadItem
                {
                    Id = "14141414-bbbb-cccc-dddd-eeeeeeeeeeee",
                    ParentThreadId = identityParent.Id,
                    HostId = "local",
                    HostLabel = "local",
                    Title = "side task title",
                    NavigationTitle = "side task title",
                    NavigationTitleVerified = true,
                    IsSideConversation = true,
                    UpdatedAt = DateTime.Now
                };
                client.ReplaceCacheForTest(new[] { identityParent, identitySide });
                var identitySnapshot = client.GetSnapshotForTest();
                var sideHostInheritedFromParent = identitySnapshot.Any(item => item.Id == identitySide.Id &&
                    item.HostId == identityParent.HostId) && identitySnapshot.Count(item => item.Id == identitySide.Id) == 1;
                client.MergeDiscoveredThreadForTest(new ThreadItem
                {
                    Id = identitySide.Id,
                    ParentThreadId = identityParent.Id,
                    HostId = "local",
                    Title = identitySide.Title,
                    NavigationTitle = identitySide.NavigationTitle,
                    NavigationTitleVerified = true,
                    IsSideConversation = true,
                    UpdatedAt = DateTime.Now
                }, new DesktopThreadStatus { Type = "idle", HasUnreadTurn = true });
                var duplicateAliasesCollapsed = client.GetSnapshotForTest().Count(item => item.Id == identitySide.Id) == 1 &&
                    client.GetSnapshotForTest().Any(item => item.Id == identitySide.Id && item.HostId == identityParent.HostId);
                client.OnIpcTitleChangedForTest(identitySide.Id, identityParent.HostId, "parent title must not replace side title");
                var sideTitleIsolated = client.GetSnapshotForTest().Any(item => item.Id == identitySide.Id &&
                    item.Title == "side task title" && item.NavigationTitle == "side task title");
                var snapshotHostWinsOverDiscoveryHint = DesktopIpcClient.PreferSnapshotHost(
                    "remote-ssh-codex-managed:work", "local") == "remote-ssh-codex-managed:work";
                var normal = new ThreadItem
                {
                    Id = "99999999-bbbb-cccc-dddd-eeeeeeeeeeee",
                    HostId = "remote-ssh-codex-managed:work",
                    HostLabel = "remote work",
                    Title = "normal discovered thread",
                    NavigationTitle = "normal discovered thread",
                    Project = "devopsbee",
                    Cwd = "/home/tester/projects/sample-project",
                    UpdatedAt = DateTime.Now,
                    IsSideConversation = false,
                    NavigationTitleVerified = true
                };
                var merged = client.MergeDiscoveredThreadForTest(normal, new DesktopThreadStatus { Type = "active" });
                var normalScanned = new ThreadItem
                {
                    Id = normal.Id,
                    HostId = normal.HostId,
                    HostLabel = normal.HostLabel,
                    Title = "normal discovered thread…",
                    Project = normal.Project,
                    Cwd = normal.Cwd,
                    UpdatedAt = normal.UpdatedAt,
                    IsSideConversation = false,
                    NavigationTitleVerified = false
                };
                var lifecycle = client.VerifyDesktopRunningLifecycleForTest(new ThreadItem
                {
                    Id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                    HostId = "remote-ssh-codex-managed:work",
                    HostLabel = "remote work",
                    Title = "running lifecycle",
                    UpdatedAt = DateTime.Now
                });
                var scanned = new ThreadItem
                {
                    Id = "bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee",
                    HostId = "remote-ssh-codex-managed:work",
                    HostLabel = "remote work",
                    Title = "scanner lifecycle",
                    UpdatedAt = DateTime.Now
                };
                AppServerClient.ApplyStatus(scanned, "idle", new string[0]);
                client.OnDesktopThreadStartedForTest(scanned.Id, scanned.HostId);
                client.ReplaceCacheForTest(new[] { scanned });
                var unread = new ThreadItem
                {
                    Id = "dddddddd-bbbb-cccc-dddd-eeeeeeeeeeee",
                    HostId = "remote-ssh-codex-managed:work",
                    HostLabel = "remote work",
                    Title = "unread result",
                    UpdatedAt = DateTime.Now
                };
                client.MergeDiscoveredThreadForTest(unread,
                    new DesktopThreadStatus { Type = "idle", HasUnreadTurn = true });
                var unreadScanned = new ThreadItem
                {
                    Id = unread.Id,
                    HostId = unread.HostId,
                    HostLabel = unread.HostLabel,
                    Title = unread.Title,
                    UpdatedAt = unread.UpdatedAt
                };
                AppServerClient.ApplyStatus(unreadScanned, "idle", new string[0]);
                var manual = new ThreadItem
                {
                    Id = "eeeeeeee-bbbb-cccc-dddd-eeeeeeeeeeee",
                    HostId = "remote-ssh-codex-managed:work",
                    HostLabel = "remote work",
                    Title = "manual acknowledgement",
                    UpdatedAt = DateTime.Now.AddMinutes(-1)
                };
                client.MergeDiscoveredThreadForTest(manual,
                    new DesktopThreadStatus { Type = "idle", HasUnreadTurn = true });
                client.OnIpcThreadViewedForTest(manual.Id, manual.HostId);
                var waitingAfterView = client.GetSnapshotForTest().Any(item => item.Id == manual.Id && item.Group == TaskGroup.Waiting);
                client.OnIpcStatusChangedForTest(manual.Id, manual.HostId,
                    new DesktopThreadStatus { Type = "idle", HasUnreadTurn = false });
                var waitingAfterReadEvent = client.GetSnapshotForTest().Any(item => item.Id == manual.Id && item.Group == TaskGroup.Waiting);
                client.MarkThreadHandled(manual.Id, manual.HostId);
                var completedAfterManualHandling = client.GetSnapshotForTest().Any(item => item.Id == manual.Id &&
                    item.Group == TaskGroup.Completed && item.StatusText == "已处理");
                client.FlushStateForTest();
                var handledStatePath = Path.Combine(stateDirectory, "handled-attention.json");
                var manualHandlingPersisted = File.Exists(handledStatePath) &&
                    File.ReadAllText(handledStatePath, Encoding.UTF8).IndexOf(manual.Id, StringComparison.OrdinalIgnoreCase) >= 0;
                client.OnIpcStatusChangedForTest(manual.Id, manual.HostId,
                    new DesktopThreadStatus { Type = "idle", HasUnreadTurn = true });
                var staleUnreadSuppressed = client.GetSnapshotForTest().Any(item => item.Id == manual.Id &&
                    item.Group == TaskGroup.Completed && item.StatusText == "已处理");
                var manualNewActivity = new ThreadItem
                {
                    Id = manual.Id,
                    HostId = manual.HostId,
                    HostLabel = manual.HostLabel,
                    Title = manual.Title,
                    UpdatedAt = manual.UpdatedAt.AddSeconds(1)
                };
                client.MergeDiscoveredThreadForTest(manualNewActivity,
                    new DesktopThreadStatus { Type = "idle", HasUnreadTurn = true });
                var newActivityReturnsToWaiting = client.GetSnapshotForTest().Any(item => item.Id == manual.Id && item.Group == TaskGroup.Waiting);
                var reactivated = new ThreadItem
                {
                    Id = "ffffffff-bbbb-cccc-dddd-eeeeeeeeeeee",
                    HostId = "remote-ssh-codex-managed:work",
                    HostLabel = "remote work",
                    Title = "reactivated waiting task",
                    UpdatedAt = DateTime.Now.AddMinutes(-2)
                };
                client.MergeDiscoveredThreadForTest(reactivated,
                    new DesktopThreadStatus { Type = "idle", HasUnreadTurn = true });
                client.OnIpcStatusChangedForTest(reactivated.Id, reactivated.HostId,
                    new DesktopThreadStatus { Type = "active" });
                var activeLeavesWaiting = client.GetSnapshotForTest().Any(item => item.Id == reactivated.Id && item.Group == TaskGroup.Running);
                var handledReactivated = new ThreadItem
                {
                    Id = "12121212-bbbb-cccc-dddd-eeeeeeeeeeee",
                    HostId = "remote-ssh-codex-managed:work",
                    HostLabel = "remote work",
                    Title = "handled task reactivated",
                    UpdatedAt = DateTime.Now.AddMinutes(-2)
                };
                client.MergeDiscoveredThreadForTest(handledReactivated,
                    new DesktopThreadStatus { Type = "idle", HasUnreadTurn = true });
                client.MarkThreadHandled(handledReactivated.Id, handledReactivated.HostId);
                client.OnIpcStatusChangedForTest(handledReactivated.Id, handledReactivated.HostId,
                    new DesktopThreadStatus { Type = "active" });
                var handledNewActivityReturnsRunning = client.GetSnapshotForTest().Any(item =>
                    item.Id == handledReactivated.Id && item.Group == TaskGroup.Running);
                var staleReactivated = new ThreadItem
                {
                    Id = reactivated.Id,
                    HostId = reactivated.HostId,
                    HostLabel = reactivated.HostLabel,
                    Title = reactivated.Title,
                    UpdatedAt = reactivated.UpdatedAt
                };
                AppServerClient.ApplyStatus(staleReactivated, "idle", new string[0]);
                var side = new ThreadItem
                {
                    Id = "cccccccc-bbbb-cccc-dddd-eeeeeeeeeeee",
                    ParentThreadId = normal.Id,
                    HostId = "remote-ssh-codex-managed:work",
                    HostLabel = "remote work",
                    Title = "remote side conversation",
                    IsSideConversation = true,
                    Group = TaskGroup.Running,
                    StatusText = "running",
                    UpdatedAt = DateTime.Now
                };
                client.MergeDiscoveredThreadForTest(normal, new DesktopThreadStatus { Type = "active" });
                client.MergeDiscoveredThreadForTest(side, new DesktopThreadStatus { Type = "active" });
                client.MergeRemoteSnapshotForTest(new[] { normalScanned, unreadScanned, staleReactivated });
                var staleRemoteIdleCannotOverrideActive = client.GetSnapshotForTest().Any(item =>
                    item.Id == reactivated.Id && item.Group == TaskGroup.Running);
                client.OnIpcStatusChangedForTest(reactivated.Id, reactivated.HostId,
                    new DesktopThreadStatus { Type = "active", Flags = new[] { "waitingOnApproval" } });
                var approvalStillWaiting = client.GetSnapshotForTest().Any(item =>
                    item.Id == reactivated.Id && item.Group == TaskGroup.Waiting);
                client.OnIpcStatusChangedForTest(reactivated.Id, reactivated.HostId,
                    new DesktopThreadStatus { Type = "active" });
                var approvalResumeReturnsRunning = client.GetSnapshotForTest().Any(item =>
                    item.Id == reactivated.Id && item.Group == TaskGroup.Running);
                client.OnIpcStatusChangedForTest(reactivated.Id, reactivated.HostId,
                    new DesktopThreadStatus { Type = "idle" });
                var reactivatedCompletionReturnsWaiting = client.GetSnapshotForTest().Any(item =>
                    item.Id == reactivated.Id && item.Group == TaskGroup.Waiting);
                client.FlushStateForTest();
                var persistedAttentionText =
                    (File.Exists(Path.Combine(stateDirectory, "awaiting-review.json"))
                        ? File.ReadAllText(Path.Combine(stateDirectory, "awaiting-review.json"), Encoding.UTF8) : "") +
                    (File.Exists(Path.Combine(stateDirectory, "handled-attention.json"))
                        ? File.ReadAllText(Path.Combine(stateDirectory, "handled-attention.json"), Encoding.UTF8) : "");
                var activeClearsHandledAttention = persistedAttentionText.IndexOf(handledReactivated.Id, StringComparison.OrdinalIgnoreCase) < 0;
                var remoteSnapshot = client.GetSnapshotForTest();
                var result = new Dictionary<string, object>
                {
                    { "normalThreadMerged", merged },
                    { "groupRunning", normal.Group == TaskGroup.Running },
                    { "hostPreserved", normal.HostId == "remote-ssh-codex-managed:work" },
                    { "remoteRefreshPreservesVerifiedNavigationTitle", remoteSnapshot.Any(item => item.Id == normal.Id &&
                        item.NavigationTitleVerified && item.NavigationTitle == normal.NavigationTitle) },
                    { "fullRefreshPreservesStartOverride", scanned.Group == TaskGroup.Running },
                    { "remoteRefreshPreservesUnreadResult", remoteSnapshot.Any(item => item.Id == unread.Id &&
                        item.Group == TaskGroup.Waiting && item.StatusText == "有新结果") },
                    { "openingDoesNotAcknowledge", waitingAfterView },
                    { "readEventDoesNotAcknowledge", waitingAfterReadEvent },
                    { "manualHandlingMovesToCompleted", completedAfterManualHandling },
                    { "manualHandlingPersists", manualHandlingPersisted },
                    { "staleUnreadDoesNotReopen", staleUnreadSuppressed },
                    { "newActivityReturnsToWaiting", newActivityReturnsToWaiting },
                    { "activeLeavesWaiting", activeLeavesWaiting },
                    { "handledNewActivityReturnsRunning", handledNewActivityReturnsRunning },
                    { "activeClearsHandledAttention", activeClearsHandledAttention },
                    { "staleRemoteIdleCannotOverrideActive", staleRemoteIdleCannotOverrideActive },
                    { "approvalStillWaiting", approvalStillWaiting },
                    { "approvalResumeReturnsRunning", approvalResumeReturnsRunning },
                    { "reactivatedCompletionReturnsWaiting", reactivatedCompletionReturnsWaiting },
                    { "remoteRefreshPreservesSideConversation", remoteSnapshot.Any(item => item.Id == side.Id && item.IsSideConversation) },
                    { "sideConversationRemainsRunning", remoteSnapshot.Any(item => item.Id == side.Id && item.Group == TaskGroup.Running) },
                    { "sideConversationParentPreserved", remoteSnapshot.Any(item => item.Id == side.Id && item.ParentThreadId == normal.Id) },
                    { "sideHostInheritedFromParent", sideHostInheritedFromParent },
                    { "duplicateAliasesCollapsed", duplicateAliasesCollapsed },
                    { "sideTitleIsolated", sideTitleIsolated },
                    { "snapshotHostWinsOverDiscoveryHint", snapshotHostWinsOverDiscoveryHint }
                };
                foreach (var pair in lifecycle) result[pair.Key] = pair.Value;
                File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result), Encoding.UTF8);
            }
            finally
            {
                client.Dispose();
                try { if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, true); }
                catch { }
            }
        }
    }

    internal static class LogMonitorDiagnostics
    {
        public static void Run(string outputPath, string probePath)
        {
            var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(probePath)), "log-monitor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var logPath = Path.Combine(root, "probe.log");
            var statePath = Path.Combine(root, "cursors.json");
            var oldId = "11111111-bbbb-cccc-dddd-eeeeeeeeeeee";
            var liveId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
            var offlineId = "22222222-bbbb-cccc-dddd-eeeeeeeeeeee";
            var partialId = "33333333-bbbb-cccc-dddd-eeeeeeeeeeee";
            var renamedId = "44444444-bbbb-cccc-dddd-eeeeeeeeeeee";
            File.WriteAllText(logPath,
                DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-ddTHH:mm:ss.fffZ") +
                " info [desktop-notifications] show turn-complete conversationId=" + oldId + " turnId=33333333-bbbb-cccc-dddd-eeeeeeeeeeee\n", Encoding.UTF8);
            var received = new List<string>();
            var seen = new List<string>();
            var started = new List<string>();
            var renamed = new List<string>();
            using (var monitor = new CodexDesktopLogMonitor(root, statePath))
            {
                monitor.ThreadCompleted += delegate(string id, string host) { lock (received) received.Add(id); };
                monitor.ThreadSeen += delegate(string id, string host) { lock (seen) seen.Add(host + "|" + id); };
                monitor.ThreadStarted += delegate(string id, string host) { lock (started) started.Add(host + "|" + id); };
                monitor.ThreadRenamed += delegate(string id, string host) { lock (renamed) renamed.Add(host + "|" + id); };
                monitor.Start();
                Thread.Sleep(500);
                var line =
                    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") +
                    " info [desktop-notifications] show turn-complete conversationId=" + liveId + " turnId=44444444-bbbb-cccc-dddd-eeeeeeeeeeee\n";
                File.AppendAllText(logPath, line + line, Encoding.UTF8);
                File.AppendAllText(logPath,
                    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " info thread_stream_view_activity_changed active=true conversationId=66666666-bbbb-cccc-dddd-eeeeeeeeeeee rendererWindowId=1 windowId=1\n" +
                    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " info ownerRoutePath=/local/77777777-bbbb-cccc-dddd-eeeeeeeeeeee?hostId=remote-ssh-codex-managed%3Awork windowId=1\n" +
                    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " info [AppServerConnection] response_routed conversationId=99999999-bbbb-cccc-dddd-eeeeeeeeeeee durationMs=1 method=turn/start\n" +
                    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " info [AppServerConnection] response_routed conversationId=" + renamedId + " durationMs=1 method=thread/name/set\n" +
                    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " info thread_stream_view_activity_changed active=true conversationId=88888888-bbbb-cccc-dddd-eeeeeeeeeeee rendererWindowId=1 windowId=1\n", Encoding.UTF8);
                var partialLine = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") +
                    " info thread_stream_view_activity_changed active=true conversationId=" + partialId +
                    " rendererWindowId=1 windowId=1\n";
                var splitAt = partialLine.IndexOf(partialId, StringComparison.Ordinal) + 12;
                File.AppendAllText(logPath, partialLine.Substring(0, splitAt), Encoding.UTF8);
                Thread.Sleep(600);
                File.AppendAllText(logPath, partialLine.Substring(splitAt), Encoding.UTF8);
                Thread.Sleep(1800);
            }
            File.AppendAllText(logPath,
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") +
                " info [desktop-notifications] show turn-complete conversationId=" + offlineId + " turnId=55555555-bbbb-cccc-dddd-eeeeeeeeeeee\n", Encoding.UTF8);
            using (var resumed = new CodexDesktopLogMonitor(root, statePath))
            {
                resumed.ThreadCompleted += delegate(string id, string host) { lock (received) received.Add(id); };
                resumed.Start();
                Thread.Sleep(1800);
            }
            string[] snapshot;
            lock (received) snapshot = received.ToArray();
            string[] seenSnapshot;
            lock (seen) seenSnapshot = seen.ToArray();
            string[] startedSnapshot;
            lock (started) startedSnapshot = started.ToArray();
            string[] renamedSnapshot;
            lock (renamed) renamedSnapshot = renamed.ToArray();
            var logCandidates = DesktopLogCandidateReader.Read(20, root);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                { "historicalIgnored", !snapshot.Contains(oldId) },
                { "liveReceivedOnce", snapshot.Count(id => id == liveId) == 1 },
                { "offlineAppendRecovered", snapshot.Count(id => id == offlineId) == 1 },
                { "remoteActivityResolved", seenSnapshot.Contains("remote-ssh-codex-managed:work|66666666-bbbb-cccc-dddd-eeeeeeeeeeee") },
                { "turnStartReceived", startedSnapshot.Any(value => value.EndsWith("|99999999-bbbb-cccc-dddd-eeeeeeeeeeee", StringComparison.OrdinalIgnoreCase)) },
                { "renameReceived", renamedSnapshot.Any(value => value.EndsWith("|" + renamedId, StringComparison.OrdinalIgnoreCase)) },
                { "remoteActivityAfterRouteResolved", seenSnapshot.Contains("remote-ssh-codex-managed:work|88888888-bbbb-cccc-dddd-eeeeeeeeeeee") },
                { "splitLineRecovered", seenSnapshot.Any(value => value.EndsWith("|" + partialId, StringComparison.OrdinalIgnoreCase)) },
                { "startupCandidateFound", logCandidates.Any(value => value.Key == "99999999-bbbb-cccc-dddd-eeeeeeeeeeee") },
                { "startupCandidateHostResolved", logCandidates.Any(value => value.Key == "77777777-bbbb-cccc-dddd-eeeeeeeeeeee" && value.Value == "remote-ssh-codex-managed:work") },
                { "received", snapshot }
            }), Encoding.UTF8);
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
        [System.Runtime.InteropServices.ClassInterface(System.Runtime.InteropServices.ClassInterfaceType.None)]
        internal class TaskbarList { }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr window);
            void DeleteTab(IntPtr window);
            void ActivateTab(IntPtr window);
            void SetActiveAlt(IntPtr window);
            void MarkFullscreenWindow(IntPtr window, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool fullscreen);
            void SetProgressValue(IntPtr window, ulong completed, ulong total);
            void SetProgressState(IntPtr window, uint flags);
            void RegisterTab(IntPtr tab, IntPtr mainWindow);
            void UnregisterTab(IntPtr tab);
            void SetTabOrder(IntPtr tab, IntPtr insertBefore);
            void SetTabActive(IntPtr tab, IntPtr mainWindow, uint reserved);
            void ThumbBarAddButtons(IntPtr window, uint buttonCount, IntPtr buttons);
            void ThumbBarUpdateButtons(IntPtr window, uint buttonCount, IntPtr buttons);
            void ThumbBarSetImageList(IntPtr window, IntPtr imageList);
            void SetOverlayIcon(IntPtr window, IntPtr icon, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string description);
            void SetThumbnailTooltip(IntPtr window, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string tooltip);
            void SetThumbnailClip(IntPtr window, IntPtr clip);
        }

        internal const int WindowMessageHotkey = 0x0312;
        internal const int WindowMessageSettingChange = 0x001A;
        internal const int WindowMessageDisplayChange = 0x007E;
        internal const int WindowMessageDpiChanged = 0x02E0;
        internal const int WindowMessageThemeChanged = 0x031A;
        internal const int WindowMessageDwmCompositionChanged = 0x031E;
        internal const int WindowMessageSetIcon = 0x0080;
        internal const int IconSmall = 0;
        internal const int IconBig = 1;
        internal const int SystemMetricLargeIconWidth = 11;
        internal const int SystemMetricSmallIconWidth = 49;
        internal const int ShowWindowHotkeyId = 0x4350;
        internal const uint HotkeyModifierAlt = 0x0001;
        internal const uint HotkeyModifierShift = 0x0004;
        internal const uint VirtualKeyW = 0x57;
        internal const uint FlashStop = 0;
        internal const uint FlashAll = 3;
        internal const uint FlashTimerNoForeground = 12;
        internal const uint MonitorDefaultToNearest = 2;
        internal const uint SetWindowPosNoZOrder = 0x0004;
        internal const uint SetWindowPosNoActivate = 0x0010;
        internal const uint SetWindowPosNoOwnerZOrder = 0x0200;
        internal const long WindowExtendedStyleAppWindow = 0x00040000L;
        internal const long WindowExtendedStyleToolWindow = 0x00000080L;
        internal const long WindowExtendedStyleNoActivate = 0x08000000L;
        private const int WindowLongExtendedStyle = -20;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            internal uint Size;
            internal NativeRect MonitorArea;
            internal NativeRect WorkArea;
            internal uint Flags;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct FlashInfo
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint Flags;
            internal uint Count;
            internal uint Timeout;
        }

        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool FlashWindowEx(ref FlashInfo info);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int RegisterWindowMessage(string message);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr window);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern uint PrivateExtractIcons(
            string fileName, int iconIndex, int iconWidth, int iconHeight,
            IntPtr[] iconHandles, uint[] iconIdentifiers, uint iconCount, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int command);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool IsZoomed(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rectangle);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr window);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, StringBuilder title, int maximumCount);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr window, int index, int value);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

        internal static long GetWindowExtendedStyle(IntPtr window)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(window, WindowLongExtendedStyle).ToInt64() : GetWindowLong32(window, WindowLongExtendedStyle);
        }

        internal static void SetWindowExtendedStyle(IntPtr window, long style)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(window, WindowLongExtendedStyle, new IntPtr(style));
            else SetWindowLong32(window, WindowLongExtendedStyle, unchecked((int)style));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
    }
}

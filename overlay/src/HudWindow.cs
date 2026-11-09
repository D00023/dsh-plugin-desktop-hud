using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DshDesktopHud
{
    /// <summary>
    /// The HUD window. Spec: PROTOCOL.md + overlay/UI-SPEC.md.
    /// </summary>
    internal sealed class HudWindow : Window
    {
        // ------------------------------------------------------------------ visual constants
        private const double ShadowPad = 12.0;      // transparent room for the drop shadow
        private const double CardW = 320.0;
        private const double CardH = 72.0;
        private const double PillW = 132.0;
        private const double PillH = 40.0;
        private const double WorkMargin = 16.0;     // content distance from the work-area edge
        private const double SizeAnimMs = 180.0;
        private const double DragThreshold = 4.0;

        // Expanded task list (v3 transport): 42 DIP rows, 14 DIP card padding, max 4 rows
        // plus a 26 DIP "+N 个任务" summary row (per UI-SPEC-multi).
        private const double RowH = 42.0;
        private const double ExpandedPad = 14.0;
        private const double SummaryH = 26.0;
        private const int MaxRows = 4;
        private const double RowsFadeMs = 120.0;
        private const double ExpandAutoCollapseMs = 300.0;
        private const double TextReserveMulti = 58.0;

        private static readonly SolidColorBrush SeparatorBrush = MakeBrush(0x0F, 0xFF, 0xFF, 0xFF);
        private static readonly SolidColorBrush RowHoverBrush = MakeBrush(0x14, 0xFF, 0xFF, 0xFF);
        private static readonly SolidColorBrush ChevronBrush = MakeBrush(0xFF, 0xA0, 0xA6, 0xAD);
        private static readonly SolidColorBrush ChevronHoverBrush = MakeBrush(0xFF, 0xF9, 0xFA, 0xFB);

        private static readonly SolidColorBrush BlueBrush = MakeBrush(0xFF, 0x7A, 0xAA, 0xFF);
        private static readonly SolidColorBrush AmberBrush = MakeBrush(0xFF, 0xF5, 0x9E, 0x0B);
        private static readonly SolidColorBrush GreenBrush = MakeBrush(0xFF, 0x34, 0xD3, 0x99);
        private static readonly SolidColorBrush RedBrush = MakeBrush(0xFF, 0xF8, 0x71, 0x71);
        private static readonly SolidColorBrush GreyBrush = MakeBrush(0xFF, 0x9A, 0xA0, 0xA6);
        private static readonly SolidColorBrush SecondaryBrush = MakeBrush(0xFF, 0xA0, 0xA6, 0xAD);
        private static readonly SolidColorBrush TitleBrush = MakeBrush(0xFF, 0xF9, 0xFA, 0xFB);

        private static readonly Color BlueColor = Color.FromRgb(0x7A, 0xAA, 0xFF);
        private static readonly Color AmberColor = Color.FromRgb(0xF5, 0x9E, 0x0B);

        // ------------------------------------------------------------------ state
        private readonly CliOptions _options;
        private readonly double _dpiScale;
        private readonly string _positionFile;
        private readonly double _doneSeconds;
        private readonly double _hideSeconds;
        private readonly double _demoStepSeconds;
        private readonly double _titleRefreshSeconds;
        private readonly double _staleSeconds;
        private readonly double _missingSeconds;
        private readonly double _disappearSeconds;
        private readonly double _testQuitSeconds;
        private readonly string _testActions;
        private readonly double _testActionDelaySeconds;

        private HudState _current = new HudState(HudMode.Idle, "", "");
        private PipeClient _pipe;
        private StateFileClient _stateFile;
        private bool _firstApply = true;
        private bool _pillShown = true;
        private bool _exiting;
        private bool _doneCollapsed;

        // ------------------------------------------------------------------ v3 task list
        private List<HudTask> _tasks;
        private bool _expanded;
        private bool _startExpandedApplied;
        private bool _pendingCollapseLayout;

        // ------------------------------------------------------------------ visual tree
        private Grid _root;
        private Grid _cardRoot;
        private Grid _pillRoot;
        private Grid _shimmerClip;
        private Border _cardShadowHost;
        private Border _cardBaseBorder;
        private Border _cardStateBorder;
        private Border _pillBaseBorder;
        private Border _shimmerBar;
        private System.Windows.Shapes.Ellipse _cardDot;
        private TextBlock _cardCheck;
        private TextBlock _cardBang;
        private TextBlock _cardTitle;
        private TextBlock _cardLine2;
        private TranslateTransform _shimmerTranslate;
        private DropShadowEffect _glow;
        private ContextMenu _menu;

        // v3 card extras: expanded rows, top-right count badge + chevron toggle.
        private Grid _cardClip;
        private Grid _cardHeader;
        private StackPanel _cardTextPanel;
        private StackPanel _cardRows;
        private Grid _cardChrome;
        private Border _taskBadge;
        private TextBlock _taskBadgeText;
        private Grid _expandHit;
        private System.Windows.Shapes.Path _chevronPath;

        // ------------------------------------------------------------------ geometry / drag
        private Rect _workArea;
        private bool _defaultAnchor = true;
        private double _customContentX;
        private double _customContentY;
        private bool _leftDown;
        private bool _dragging;
        private Point _downScreenDevice;
        private double _downLeft;
        private double _downTop;
        private bool _mouseCaptured;
        private double _lastContentH;
        private bool _bottomAnchorAnim;

        // ------------------------------------------------------------------ animations
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private DispatcherTimer _resizeTimer;
        private double _animFromW;
        private double _animFromH;
        private double _animToW;
        private double _animToH;
        private double _animStartMs;
        private bool _animating;

        // ------------------------------------------------------------------ timers / caches
        private DispatcherTimer _doneTimer;
        private DispatcherTimer _hideTimer;
        private DispatcherTimer _demoTimer;
        private DispatcherTimer _titleTimer;
        private DispatcherTimer _ownerTimer;
        private DispatcherTimer _expandAutoTimer;
        private string _titleCache;
        private DateTime _titleCachedUtc = DateTime.MinValue;
        private bool _titleUsesFallback;
        private int _demoIndex;
        private int _demoMultiIndex;
        private int _ownerPid;
        private string _rowsSignature;

        private static readonly HudMode[] DemoOrder = new HudMode[]
        {
            HudMode.Idle, HudMode.Running, HudMode.Attention,
            HudMode.Question, HudMode.Done, HudMode.Error
        };

        // ================================================================== construction
        public HudWindow(CliOptions options)
        {
            _options = options == null ? new CliOptions() : options;
            _dpiScale = Native.GetDpiScale();
            _positionFile = Path.Combine(PositionDirectory, "window-v1.json");

            _doneSeconds = Env.Seconds("DSH_HUD_DONE_SECONDS", 15.0);
            _hideSeconds = Env.Seconds("DSH_HUD_HIDE_SECONDS", 1800.0);
            _demoStepSeconds = Env.Seconds("DSH_HUD_DEMO_SECONDS", 2.5);
            _titleRefreshSeconds = Env.Seconds("DSH_HUD_TITLE_SECONDS", 2.0);
            _staleSeconds = Env.Seconds("DSH_HUD_STALE_SECONDS", 10.0);
            _missingSeconds = Env.Seconds("DSH_HUD_MISSING_SECONDS", 90.0);
            _disappearSeconds = Env.Seconds("DSH_HUD_DISAPPEAR_SECONDS", 2.5);
            _testQuitSeconds = Env.Seconds("DSH_HUD_TEST_QUIT_SECONDS", 0.0);
            _testActions = Environment.GetEnvironmentVariable("DSH_HUD_TEST_ACTIONS");
            _testActionDelaySeconds = Env.Seconds("DSH_HUD_TEST_ACTION_DELAY_SECONDS", 0.0);

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            Opacity = 0.96;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

            _workArea = SystemParameters.WorkArea;

            _root = LoadLayout();
            _root.Margin = new Thickness(ShadowPad);
            Content = _root;

            HookEvents();
            BuildContextMenu();
            RestorePosition();

            _pillRoot.Visibility = Visibility.Visible;
            _pillRoot.Opacity = 0.8;
            _cardRoot.Visibility = Visibility.Collapsed;
            UpdateGeometry(PillW, PillH);
        }

        private static SolidColorBrush MakeBrush(byte a, byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private static string PositionDirectory
        {
            get
            {
                // Test hook only: never set in production, defaults to the documented location.
                string overridden = Environment.GetEnvironmentVariable("DSH_HUD_DATA_DIR");
                if (!string.IsNullOrEmpty(overridden)) return overridden;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "dsh-desktop-hud");
            }
        }

        private Grid LoadLayout()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            Stream stream = assembly.GetManifestResourceStream("HudWindow.xaml");
            if (stream == null)
            {
                throw new InvalidOperationException("Embedded HudWindow.xaml not found");
            }
            object loaded;
            using (stream)
            {
                loaded = XamlReader.Load(stream);
            }
            Grid root = loaded as Grid;
            if (root == null) throw new InvalidOperationException("HudWindow.xaml root is not a Grid");

            _cardRoot = root.FindName("CardRoot") as Grid;
            _pillRoot = root.FindName("PillRoot") as Grid;
            _shimmerClip = root.FindName("ShimmerClip") as Grid;
            _cardShadowHost = root.FindName("CardShadowHost") as Border;
            _cardBaseBorder = root.FindName("CardBaseBorder") as Border;
            _cardStateBorder = root.FindName("CardStateBorder") as Border;
            _pillBaseBorder = root.FindName("PillBaseBorder") as Border;
            _shimmerBar = root.FindName("ShimmerBar") as Border;
            _cardDot = root.FindName("CardDot") as System.Windows.Shapes.Ellipse;
            _cardCheck = root.FindName("CardCheck") as TextBlock;
            _cardBang = root.FindName("CardBang") as TextBlock;
            _cardTitle = root.FindName("CardTitle") as TextBlock;
            _cardLine2 = root.FindName("CardLine2") as TextBlock;
            _shimmerTranslate = root.FindName("ShimmerTranslate") as TranslateTransform;
            _cardClip = root.FindName("CardClip") as Grid;
            _cardHeader = root.FindName("CardHeader") as Grid;
            _cardTextPanel = root.FindName("CardTextPanel") as StackPanel;
            _cardRows = root.FindName("CardRows") as StackPanel;
            _cardChrome = root.FindName("CardChrome") as Grid;
            _taskBadge = root.FindName("TaskBadge") as Border;
            _taskBadgeText = root.FindName("TaskBadgeText") as TextBlock;
            _expandHit = root.FindName("ExpandHit") as Grid;
            _chevronPath = root.FindName("ChevronPath") as System.Windows.Shapes.Path;

            if (_cardRoot == null || _pillRoot == null || _cardStateBorder == null || _cardDot == null ||
                _cardTitle == null || _cardLine2 == null || _shimmerClip == null || _shimmerBar == null ||
                _shimmerTranslate == null || _cardClip == null || _cardHeader == null ||
                _cardTextPanel == null || _cardRows == null || _cardChrome == null ||
                _taskBadge == null || _taskBadgeText == null || _expandHit == null || _chevronPath == null)
            {
                throw new InvalidOperationException("HudWindow.xaml is missing named elements");
            }

            // Runtime-loaded freezables may be frozen; animation needs a mutable clone.
            if (_shimmerTranslate.IsFrozen)
            {
                _shimmerTranslate = _shimmerTranslate.Clone();
                _shimmerBar.RenderTransform = _shimmerTranslate;
            }

            // Amber glow used by the attention border (created in code so it stays animatable).
            _glow = new DropShadowEffect();
            _glow.Color = AmberColor;
            _glow.BlurRadius = 22.0;
            _glow.ShadowDepth = 0.0;
            _glow.Direction = 270.0;
            _glow.Opacity = 0.0;

            // Dark tooltip for the idle pill.
            ToolTip tip = new ToolTip();
            tip.Content = "点击回到 DSH";
            tip.Background = MakeBrush(0xF7, 0x1B, 0x1B, 0x1E);
            tip.Foreground = MakeBrush(0xFF, 0xF9, 0xFA, 0xFB);
            tip.BorderBrush = MakeBrush(0x33, 0xFF, 0xFF, 0xFF);
            tip.BorderThickness = new Thickness(1);
            tip.Padding = new Thickness(8, 5, 8, 5);
            tip.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            tip.FontSize = 12.0;
            tip.HasDropShadow = true;
            _pillRoot.ToolTip = tip;

            return root;
        }

        private void HookEvents()
        {
            PreviewMouseLeftButtonDown += OnLeftDown;
            PreviewMouseMove += OnMouseMove;
            PreviewMouseLeftButtonUp += OnLeftUp;
            PreviewMouseRightButtonUp += OnRightClick;

            _cardRoot.MouseEnter += OnCardMouseEnter;
            _cardRoot.MouseLeave += OnCardMouseLeave;

            // Chevron toggle hover highlight (the click itself is handled in OnLeftDown so a
            // click on the chevron never turns into a drag/focus click on the card).
            _expandHit.MouseEnter += delegate { _chevronPath.Stroke = ChevronHoverBrush; };
            _expandHit.MouseLeave += delegate { _chevronPath.Stroke = ChevronBrush; };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
            }
            catch (Exception)
            {
            }
        }

        // ================================================================== startup
        /// <summary>Called after Show(): starts demo, pipe client or owner-pid watch.</summary>
        public void StartCore()
        {
            Trace.Log("StartCore enter demo=" + _options.Demo + " pipe=[" + (_options.Pipe == null ? "" : _options.Pipe)
                + "] stateFile=[" + (_options.StateFile == null ? "" : _options.StateFile + "]")
                + " ownerPid=" + _options.OwnerPid);
            StartTitleTimer();

            if (_options.DemoMulti)
            {
                Trace.Log("StartCore: demo-multi mode");
                StartDemoMulti();
                return;
            }

            if (_options.Demo)
            {
                Trace.Log("StartCore: demo mode");
                StartDemo();
                return;
            }

            if (!string.IsNullOrEmpty(_options.StateFile))
            {
                Trace.Log("StartCore: state-file mode");
                StartStateFile();
                StartTestActionHook();
                return;
            }

            if (_options.OwnerPid > 0)
            {
                // Only the pipe fallback needs an owner poll: the state file has its own
                // stale-heartbeat exit, which is immune to sandboxed process-visibility quirks.
                StartOwnerWatch(_options.OwnerPid);
            }

            if (!string.IsNullOrEmpty(_options.Pipe))
            {
                Trace.Log("StartCore: pipe mode (fallback)");
                StartPipe();
            }
            else
            {
                Trace.Log("StartCore: no transport, staying idle");
            }
        }

        private void StartDemo()
        {
            if (_options.DemoFrozen)
            {
                ApplyState(HudState.Sample(_options.DemoState));
                return;
            }

            _demoIndex = 0;
            ApplyState(HudState.Sample(DemoOrder[0]));
            _demoIndex = 1;

            _demoTimer = new DispatcherTimer();
            _demoTimer.Interval = TimeSpan.FromSeconds(_demoStepSeconds);
            _demoTimer.Tick += delegate
            {
                if (_exiting) return;
                HudMode mode = DemoOrder[_demoIndex % DemoOrder.Length];
                _demoIndex++;
                ApplyState(HudState.Sample(mode));
            };
            _demoTimer.Start();
        }

        private void StartDemoMulti()
        {
            List<MultiDemoScene> scenes = MultiDemo.Scenes();
            if (scenes.Count == 0) return;

            ApplyState(scenes[0].State);
            SetExpanded(scenes[0].Expanded, false);
            _demoMultiIndex = 1;

            _demoTimer = new DispatcherTimer();
            _demoTimer.Interval = TimeSpan.FromSeconds(_demoStepSeconds);
            _demoTimer.Tick += delegate
            {
                if (_exiting) return;
                MultiDemoScene scene = scenes[_demoMultiIndex % scenes.Count];
                _demoMultiIndex++;
                ApplyState(scene.State);
                SetExpanded(scene.Expanded, false);
            };
            _demoTimer.Start();
            Trace.Log("demo-multi started scenes=" + scenes.Count + " stepSeconds=" + _demoStepSeconds);
        }

        private void StartPipe()
        {
            Trace.Log("StartPipe arg=[" + _options.Pipe + "]");
            _pipe = new PipeClient(_options.Pipe, _dpiScale);

            _pipe.Connected += delegate
            {
                Trace.Log("event connected -> refresh title");
                Dispatch(delegate
                {
                    _titleCachedUtc = DateTime.MinValue;
                    RefreshWindowTitle(true);
                });
            };
            _pipe.Disconnected += delegate
            {
                // Keep the last state on screen; reconnect is silent and bounded by PROTOCOL.md.
                Trace.Log("event disconnected");
            };
            _pipe.Message += delegate(Dictionary<string, object> message)
            {
                Trace.Log("event message");
                Dispatch(delegate { HandlePipeMessage(message); });
            };
            _pipe.Fatal += delegate
            {
                Trace.Log("event fatal -> exit 0");
                Dispatch(delegate { ExitNow(0, "pipe fatal"); });
            };

            _pipe.Start();
            Trace.Log("pipe client started");
        }

        private void StartStateFile()
        {
            Trace.Log("StartStateFile arg=[" + _options.StateFile + "] staleSeconds=" + _staleSeconds
                + " missingSeconds=" + _missingSeconds + " disappearSeconds=" + _disappearSeconds);
            _stateFile = new StateFileClient(_options.StateFile, _staleSeconds, _missingSeconds, _disappearSeconds);

            _stateFile.StateChanged += delegate(Dictionary<string, object> obj)
            {
                Dispatch(delegate
                {
                    if (_exiting) return;
                    HudState state = HudState.FromMessage(obj);
                    if (state == null) return; // unknown mode / malformed content: ignore, keep last
                    _titleCachedUtc = DateTime.MinValue;
                    ApplyState(state);
                });
            };
            _stateFile.Fatal += delegate
            {
                Trace.Log("event state-file fatal -> exit 0");
                Dispatch(delegate { ExitNow(0, "state file stale or deleted"); });
            };

            _stateFile.Start();
            Trace.Log("state file client started");
            StartTestQuitHook();
        }

        /// <summary>Internal test hook: quit through the user path after N seconds.</summary>
        private void StartTestQuitHook()
        {
            if (_testQuitSeconds <= 0) return;
            Trace.Log("test hook: quit through user path after " + _testQuitSeconds + "s");
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(_testQuitSeconds);
            timer.Tick += delegate
            {
                try
                {
                    timer.Stop();
                }
                catch (Exception)
                {
                }
                if (_exiting) return;
                QuitByUser();
            };
            timer.Start();
        }

        /// <summary>Internal test hook: drive interaction paths from a script while the
        /// desktop is locked (real mouse input is unavailable in that state).</summary>
        private void StartTestActionHook()
        {
            if (string.IsNullOrEmpty(_testActions) || _testActionDelaySeconds <= 0) return;
            Trace.Log("test hook: actions=[" + _testActions + "] stepDelay="
                + _testActionDelaySeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s");
            string[] tokens = _testActions.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            RunTestAction(tokens, 0);
        }

        private void RunTestAction(string[] tokens, int index)
        {
            if (_exiting || index >= tokens.Length) return;
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(_testActionDelaySeconds);
            timer.Tick += delegate
            {
                try
                {
                    timer.Stop();
                }
                catch (Exception)
                {
                }
                if (_exiting) return;
                ExecuteTestAction(tokens[index].Trim());
                RunTestAction(tokens, index + 1);
            };
            timer.Start();
        }

        private void ExecuteTestAction(string action)
        {
            Trace.Log("test action=[" + action + "]");
            try
            {
                if (action == "focus")
                {
                    Trace.Log("test action: simulated plain click");
                    HandlePlainClick();
                }
                else if (action == "drag")
                {
                    Trace.Log("test action: simulated drag by (-400,-300) physical px");
                    SimulateDrag(-400.0, -300.0);
                }
                else if (action == "menu")
                {
                    OpenMenu();
                }
                else if (action == "expand")
                {
                    Trace.Log("test action: expand task list");
                    SetExpanded(true, true);
                }
                else if (action == "collapse")
                {
                    Trace.Log("test action: collapse task list");
                    SetExpanded(false, true);
                }
                else if (action == "toggle")
                {
                    Trace.Log("test action: toggle task list");
                    ToggleExpanded();
                }
                else if (action == "hide")
                {
                    HideForAWhile();
                }
                else if (action == "quit")
                {
                    QuitByUser();
                }
                else if (action.StartsWith("menu-item:", StringComparison.Ordinal))
                {
                    int idx;
                    if (int.TryParse(action.Substring("menu-item:".Length), out idx))
                    {
                        InvokeMenuItem(idx);
                    }
                }
                else
                {
                    Trace.Log("test action: unknown action ignored");
                }
            }
            catch (Exception ex)
            {
                Trace.LogException("test action failed (ignored)", ex);
            }
        }

        private void InvokeMenuItem(int index)
        {
            if (_menu == null || index < 0 || index >= _menu.Items.Count) return;
            MenuItem item = _menu.Items[index] as MenuItem;
            if (item == null) return;
            Trace.Log("test action: invoking menu item index=" + index + " header=[" + item.Header + "]");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        }

        private void SimulateDrag(double deviceDx, double deviceDy)
        {
            double left = Left + (deviceDx / _dpiScale);
            double top = Top + (deviceDy / _dpiScale);
            ClampWindowPosition(ref left, ref top);
            Left = left;
            Top = top;
            CompleteDrag();
        }

        private void HandlePipeMessage(Dictionary<string, object> message)
        {
            if (_exiting) return;
            string type = Json.GetString(message, "t");
            if (type == null) return;

            if (type == "quit")
            {
                ExitNow(0, "quit message from host");
                return;
            }
            if (type == "state")
            {
                HudState state = HudState.FromMessage(message);
                if (state == null) return; // unknown mode / malformed: ignore
                _titleCachedUtc = DateTime.MinValue;
                ApplyState(state);
            }
        }

        private void StartOwnerWatch(int pid)
        {
            _ownerPid = pid;
            Trace.Log("owner watch start pid=" + pid);
            _ownerTimer = new DispatcherTimer();
            _ownerTimer.Interval = TimeSpan.FromSeconds(3.0);
            _ownerTimer.Tick += delegate
            {
                if (_exiting) return;
                bool gone = OwnerGone(_ownerPid);
                Trace.Log("owner check pid=" + _ownerPid + " gone=" + gone);
                if (gone)
                {
                    ExitNow(0, "owner process gone");
                }
            };
            _ownerTimer.Start();
        }

        private static bool OwnerGone(int pid)
        {
            try
            {
                // OpenProcess distinguishes "no such process" from "sandbox says access denied";
                // Process.GetProcessById reports both as "not running", which used to kill the HUD.
                return !Native.IsProcessAlive(pid);
            }
            catch (Exception ex)
            {
                Trace.Log("OwnerGone check failed (treated as alive): " + ex.Message);
                return false; // cannot read it: do not kill the HUD on a transient access error
            }
        }

        // ================================================================== title fallback
        private void StartTitleTimer()
        {
            _titleTimer = new DispatcherTimer();
            _titleTimer.Interval = TimeSpan.FromSeconds(_titleRefreshSeconds);
            _titleTimer.Tick += delegate { RefreshWindowTitle(true); };
            _titleTimer.Start();
        }

        private static bool IsPlaceholderTitle(string title)
        {
            if (title == null) return true;
            string t = title.Trim();
            if (t.Length == 0) return true;
            return string.Equals(t, TitleUtil.Placeholder, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// PROTOCOL.md "Title fallback": when the Host cannot supply a session title, show the
        /// DSH main-window title (cached, refreshed every 2 s), or "DSH" when unavailable.
        /// </summary>
        private string BuildTitle(HudState state, bool mayRefresh)
        {
            string raw = state == null || state.Title == null ? "" : state.Title.Trim();
            if (raw.Length > 0 && !string.Equals(raw, TitleUtil.Placeholder, StringComparison.OrdinalIgnoreCase))
            {
                _titleUsesFallback = false;
                return raw;
            }

            _titleUsesFallback = true;
            if (mayRefresh && IsTitleCacheStale())
            {
                RefreshWindowTitle(true);
            }
            return string.IsNullOrEmpty(_titleCache) ? "DSH" : _titleCache;
        }

        private bool IsTitleCacheStale()
        {
            if (_titleCachedUtc == DateTime.MinValue) return true;
            return (DateTime.UtcNow - _titleCachedUtc).TotalSeconds >= _titleRefreshSeconds;
        }

        private void RefreshWindowTitle(bool updateCard)
        {
            try
            {
                _titleCache = Native.GetDshWindowTitleDisplay();
                _titleCachedUtc = DateTime.UtcNow;
            }
            catch (Exception)
            {
                return;
            }

            if (updateCard && _titleUsesFallback && !_pillShown && !_exiting)
            {
                string title = BuildTitle(_current, false);
                if (!string.Equals(_cardTitle.Text, title, StringComparison.Ordinal))
                {
                    _cardTitle.Text = title;
                }
            }
        }

        // ================================================================== state rendering
        public void ApplyState(HudState state)
        {
            if (state == null || _exiting) return;

            // Done auto-collapses to the idle pill after 15 s. The Host keeps rewriting the last
            // state (mode=done) as a heartbeat, so later Done repeats must not pop the card back up.
            if (_doneCollapsed && state.Mode == HudMode.Done)
            {
                _current = state;
                Trace.Log("apply state: done already collapsed -> ignore Done heartbeat");
                return;
            }

            // Heartbeat dedupe: the Host rewrites the same state file every 2 s to prove it is
            // still alive. Re-rendering on every heartbeat would restart the animations (the one
            // shot done flash, attention/error breathing, running shimmer), so identical states
            // only update the bookkeeping and leave the visuals untouched.
            bool visualMatches = (state.Mode == HudMode.Idle) ? _pillShown : !_pillShown;
            if (!_firstApply && visualMatches && _current != null && SameVisualState(_current, state))
            {
                _current = state;
                Trace.Log("apply state: duplicate heartbeat mode=" + state.Mode + " -> no re-render");
                return;
            }

            Trace.Log("apply state mode=" + state.Mode + " title=[" + state.Title + "] detail=[" + state.Detail + "] tasks=" + (state.Tasks == null ? -1 : state.Tasks.Count));
            _current = state;
            _tasks = state.Tasks;
            if (state.Mode != HudMode.Done) _doneCollapsed = false;

            if (state.Mode == HudMode.Idle)
            {
                ShowPill();
            }
            else
            {
                ShowCard(state);
                MaybeAutoCollapse();
            }
        }

        /// <summary>True when two states produce exactly the same visuals (ignores since/ts/session id changes).</summary>
        private static bool SameVisualState(HudState a, HudState b)
        {
            if (a == null || b == null) return false;
            if (a.Mode != b.Mode) return false;
            if (!string.Equals(a.Session == null ? "" : a.Session, b.Session == null ? "" : b.Session, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.Title == null ? "" : a.Title, b.Title == null ? "" : b.Title, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.Detail == null ? "" : a.Detail, b.Detail == null ? "" : b.Detail, StringComparison.Ordinal)) return false;
            return SameTasks(a.Tasks, b.Tasks);
        }

        /// <summary>Task-list equality used by the heartbeat dedupe (order matters).</summary>
        private static bool SameTasks(List<HudTask> a, List<HudTask> b)
        {
            int countA = a == null ? -1 : a.Count;
            int countB = b == null ? -1 : b.Count;
            if (countA <= 0 && countB <= 0) return true;
            if (countA != countB) return false;
            if (countA <= 0) return true;
            for (int i = 0; i < a.Count; i++)
            {
                HudTask x = a[i];
                HudTask y = b[i];
                if (x.Status != y.Status) return false;
                if (x.Since != y.Since) return false;
                if (!string.Equals(x.Title == null ? "" : x.Title, y.Title == null ? "" : y.Title, StringComparison.Ordinal)) return false;
                if (!string.Equals(x.Detail == null ? "" : x.Detail, y.Detail == null ? "" : y.Detail, StringComparison.Ordinal)) return false;
                if (!string.Equals(x.Session == null ? "" : x.Session, y.Session == null ? "" : y.Session, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private void ShowPill()
        {
            bool resize = !_pillShown || _firstApply;
            StopDoneTimer();
            StopExpandAutoTimer();

            _expanded = false;
            _pendingCollapseLayout = false;
            _tasks = null;
            _rowsSignature = null;
            _cardRows.Children.Clear();
            _cardRows.Visibility = Visibility.Collapsed;
            _cardHeader.Visibility = Visibility.Visible;
            _cardChrome.Visibility = Visibility.Collapsed;
            _cardRoot.Height = CardH;

            _cardRoot.Visibility = Visibility.Collapsed;
            _pillRoot.Visibility = Visibility.Visible;
            _pillRoot.Opacity = 0.8;
            _pillShown = true;
            _titleUsesFallback = false;

            if (resize)
            {
                if (_firstApply)
                {
                    UpdateGeometry(PillW, PillH);
                    _firstApply = false;
                }
                else
                {
                    AnimateSize(PillW, PillH);
                }
            }
        }

        private void ShowCard(HudState state)
        {
            bool resize = _pillShown || _firstApply;

            _pillRoot.Visibility = Visibility.Collapsed;
            _cardRoot.Visibility = Visibility.Visible;
            _pillShown = false;

            _cardTitle.Text = BuildTitle(state, true);
            Trace.Log("card title resolved=[" + _cardTitle.Text + "] usesTitleFallback=" + _titleUsesFallback);

            string label = HudState.LabelFor(state.Mode);
            string line2 = state.Detail == null ? "" : state.Detail.Trim();
            if (line2.Length == 0) line2 = label;
            _cardLine2.Text = line2;

            _tasks = state.Tasks;
            int count = TaskCount();
            bool multi = count >= 2;

            // --expanded only affects the first card shown after startup; after that the chevron
            // (or the auto-collapse rule when the list shrinks) owns the expand state.
            if (_options.StartExpanded && !_startExpandedApplied && multi) _expanded = true;
            _startExpandedApplied = true;
            if (!multi)
            {
                _expanded = false;
                _pendingCollapseLayout = false;
            }

            ResetVisuals();
            BuildRowsIfNeeded();
            UpdateChromeAndLayout();

            HudMode visualMode = multi ? HighestPriorityMode(_tasks) : state.Mode;

            switch (visualMode)
            {
                case HudMode.Running:
                    ShowDot(BlueBrush);
                    StartBreathing();
                    _shimmerClip.Visibility = Visibility.Visible;
                    StartShimmer();
                    break;

                case HudMode.Attention:
                    ShowDot(AmberBrush);
                    StartBreathing();
                    // Single-task card emphasises its 需要你授权 line; a multi-task card keeps the
                    // neutral title line (the amber highlight belongs to the attention row itself).
                    if (!multi)
                    {
                        _cardLine2.FontSize = 15.0;
                        _cardLine2.FontWeight = FontWeights.SemiBold;
                        _cardLine2.Foreground = AmberBrush;
                    }
                    _cardStateBorder.BorderBrush = AmberBrush;
                    _cardStateBorder.Effect = _glow;
                    StartBorderPulse(0.45, 1.0);
                    StartGlowPulse(0.35, 0.8);
                    break;

                case HudMode.Question:
                    ShowDot(BlueBrush);
                    _cardStateBorder.BorderBrush = BlueBrush;
                    StartBorderPulse(0.35, 0.8);
                    break;

                case HudMode.Done:
                    ShowCheck();
                    _cardStateBorder.BorderBrush = GreenBrush;
                    if (multi)
                    {
                        // The list is the source of truth for "task finished": steady green,
                        // no one-shot flash (each row already shows its own state).
                        _cardStateBorder.Opacity = 0.25;
                    }
                    else
                    {
                        StartDoneFlash();
                        StartDoneTimer();
                    }
                    break;

                case HudMode.Error:
                    ShowBang();
                    _cardStateBorder.BorderBrush = RedBrush;
                    _cardStateBorder.Opacity = 0.55;
                    break;
            }

            double targetH = ContentHeightForCard();
            // Pill -> card is a size swap at a fixed top-left corner (same as v2); only
            // expand/collapse of the task list keeps the card's bottom edge in place.
            _bottomAnchorAnim = !resize;
            if (resize)
            {
                _cardRoot.Opacity = 0.0;
                if (_firstApply)
                {
                    UpdateGeometry(CardW, targetH);
                    _cardRoot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(140)));
                    _firstApply = false;
                }
                else
                {
                    AnimateSize(CardW, targetH);
                    _cardRoot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(140)));
                }
            }
            else if (Math.Abs(targetH - _lastContentH) > 0.5)
            {
                // Expanding/collapsing keeps the card's bottom edge in place (it grows upward).
                AnimateSize(CardW, targetH);
            }
            else
            {
                UpdateGeometry(CardW, targetH);
            }
        }

        private void ResetVisuals()
        {
            StopCardAnimations();
            StopDoneTimer();

            _cardRoot.Opacity = 1.0;
            _cardDot.Visibility = Visibility.Visible;
            _cardDot.Fill = GreyBrush;
            _cardDot.Opacity = 1.0;
            _cardCheck.Visibility = Visibility.Collapsed;
            _cardBang.Visibility = Visibility.Collapsed;

            _cardStateBorder.Opacity = 0.0;
            _cardStateBorder.Effect = null;

            _cardLine2.FontSize = 12.0;
            _cardLine2.FontWeight = FontWeights.Normal;
            _cardLine2.Foreground = SecondaryBrush;
        }

        private void StopCardAnimations()
        {
            _cardDot.BeginAnimation(UIElement.OpacityProperty, null);
            _cardDot.Opacity = 1.0;
            _cardStateBorder.BeginAnimation(UIElement.OpacityProperty, null);
            _cardStateBorder.Effect = null;
            _glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            _glow.Opacity = 0.0;
            _shimmerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            _shimmerTranslate.X = -130.0;
            _shimmerClip.Visibility = Visibility.Collapsed;
            _cardRoot.BeginAnimation(UIElement.OpacityProperty, null);
            _cardRoot.Opacity = 1.0;
        }

        // ------------------------------------------------------------------ glyphs / animations
        private void ShowDot(SolidColorBrush brush)
        {
            _cardDot.Visibility = Visibility.Visible;
            _cardDot.Fill = brush;
            _cardCheck.Visibility = Visibility.Collapsed;
            _cardBang.Visibility = Visibility.Collapsed;
        }

        private void ShowCheck()
        {
            _cardDot.Visibility = Visibility.Collapsed;
            _cardCheck.Visibility = Visibility.Visible;
            _cardBang.Visibility = Visibility.Collapsed;
        }

        private void ShowBang()
        {
            _cardDot.Visibility = Visibility.Collapsed;
            _cardCheck.Visibility = Visibility.Collapsed;
            _cardBang.Visibility = Visibility.Visible;
        }

        private void StartBreathing()
        {
            DoubleAnimation animation = new DoubleAnimation(1.0, 0.45, TimeSpan.FromMilliseconds(800));
            animation.AutoReverse = true;
            animation.RepeatBehavior = RepeatBehavior.Forever;
            animation.EasingFunction = new SineEase();
            _cardDot.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        private void StartBorderPulse(double from, double to)
        {
            DoubleAnimation animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(700));
            animation.AutoReverse = true;
            animation.RepeatBehavior = RepeatBehavior.Forever;
            animation.EasingFunction = new SineEase();
            _cardStateBorder.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        private void StartGlowPulse(double from, double to)
        {
            DoubleAnimation animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(700));
            animation.AutoReverse = true;
            animation.RepeatBehavior = RepeatBehavior.Forever;
            animation.EasingFunction = new SineEase();
            _glow.BeginAnimation(DropShadowEffect.OpacityProperty, animation);
        }

        private void StartShimmer()
        {
            DoubleAnimation animation = new DoubleAnimation(-130.0, 300.0, TimeSpan.FromMilliseconds(2200));
            animation.RepeatBehavior = RepeatBehavior.Forever;
            _shimmerTranslate.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private void StartDoneFlash()
        {
            DoubleAnimationUsingKeyFrames animation = new DoubleAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350))));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
            animation.Completed += delegate
            {
                if (_exiting) return;
                if (_current != null && _current.Mode == HudMode.Done)
                {
                    _cardStateBorder.BeginAnimation(UIElement.OpacityProperty, null);
                    _cardStateBorder.Opacity = 0.25;
                }
            };
            _cardStateBorder.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        private void StartDoneTimer()
        {
            StopDoneTimer();
            _doneTimer = new DispatcherTimer();
            _doneTimer.Interval = TimeSpan.FromSeconds(_doneSeconds);
            _doneTimer.Tick += delegate
            {
                StopDoneTimer();
                if (_exiting) return;
                if (_current != null && _current.Mode == HudMode.Done)
                {
                    ApplyState(new HudState(HudMode.Idle, "", ""));
                    _doneCollapsed = true;
                }
            };
            _doneTimer.Start();
        }

        private void StopDoneTimer()
        {
            if (_doneTimer != null)
            {
                _doneTimer.Stop();
                _doneTimer = null;
            }
        }

        // ------------------------------------------------------------------ hover
        private void OnCardMouseEnter(object sender, MouseEventArgs e)
        {
            if (_pillShown || _exiting) return;
            AnimateCardBaseOpacity(0.45);
        }

        private void OnCardMouseLeave(object sender, MouseEventArgs e)
        {
            AnimateCardBaseOpacity(0.133);
        }

        private void AnimateCardBaseOpacity(double target)
        {
            try
            {
                _cardBaseBorder.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(target, TimeSpan.FromMilliseconds(150)));
            }
            catch (Exception)
            {
            }
        }

        // ================================================================== geometry / animation
        private void UpdateGeometry(double contentW, double contentH)
        {
            double windowW = contentW + 2.0 * ShadowPad;
            double windowH = contentH + 2.0 * ShadowPad;

            Width = windowW;
            Height = windowH;

            if (_defaultAnchor)
            {
                Left = _workArea.Right - (WorkMargin - ShadowPad) - windowW;
                Top = _workArea.Bottom - (WorkMargin - ShadowPad) - windowH;
            }
            else
            {
                // Growing/shrinking the card keeps its BOTTOM edge in place, so the list grows
                // upward from the saved position instead of pushing past the taskbar.
                if (_bottomAnchorAnim && _lastContentH > 0 && Math.Abs(_lastContentH - contentH) > 0.01)
                {
                    _customContentY += _lastContentH - contentH;
                }
                ClampCustomContent(contentW, contentH);
                Left = _customContentX - ShadowPad;
                Top = _customContentY - ShadowPad;
            }

            if (_cardRoot != null)
            {
                _cardRoot.Height = Math.Max(CardH, contentH);
            }
            _lastContentH = contentH;
        }

        private void ClampCustomContent(double contentW, double contentH)
        {
            double maxX = _workArea.Right - contentW;
            double maxY = _workArea.Bottom - contentH;
            if (maxX < _workArea.Left) maxX = _workArea.Left;
            if (maxY < _workArea.Top) maxY = _workArea.Top;

            if (_customContentX < _workArea.Left) _customContentX = _workArea.Left;
            if (_customContentX > maxX) _customContentX = maxX;
            if (_customContentY < _workArea.Top) _customContentY = _workArea.Top;
            if (_customContentY > maxY) _customContentY = maxY;
        }

        private void ClampWindowPosition(ref double left, ref double top)
        {
            double w = Width;
            double h = Height;
            if (double.IsNaN(w) || w <= 0) w = PillW + 2.0 * ShadowPad;
            if (double.IsNaN(h) || h <= 0) h = PillH + 2.0 * ShadowPad;

            double maxLeft = _workArea.Right - w;
            double maxTop = _workArea.Bottom - h;
            if (maxLeft < _workArea.Left) maxLeft = _workArea.Left;
            if (maxTop < _workArea.Top) maxTop = _workArea.Top;

            if (left < _workArea.Left) left = _workArea.Left;
            if (left > maxLeft) left = maxLeft;
            if (top < _workArea.Top) top = _workArea.Top;
            if (top > maxTop) top = maxTop;
        }

        private void AnimateSize(double contentW, double contentH)
        {
            double targetW = contentW + 2.0 * ShadowPad;
            double targetH = contentH + 2.0 * ShadowPad;

            double fromW = Width;
            double fromH = Height;
            if (double.IsNaN(fromW) || fromW <= 0) fromW = targetW;
            if (double.IsNaN(fromH) || fromH <= 0) fromH = targetH;

            _animFromW = fromW;
            _animFromH = fromH;
            _animToW = targetW;
            _animToH = targetH;
            _animStartMs = _clock.Elapsed.TotalMilliseconds;
            _animating = true;

            if (_resizeTimer == null)
            {
                _resizeTimer = new DispatcherTimer(DispatcherPriority.Render);
                _resizeTimer.Interval = TimeSpan.FromMilliseconds(16);
                _resizeTimer.Tick += OnResizeTick;
            }
            _resizeTimer.Start();
        }

        private void OnResizeTick(object sender, EventArgs e)
        {
            if (!_animating)
            {
                _resizeTimer.Stop();
                return;
            }

            double elapsed = _clock.Elapsed.TotalMilliseconds - _animStartMs;
            double t = elapsed / SizeAnimMs;
            if (t < 0) t = 0;
            if (t > 1) t = 1;

            double eased = 1.0 - Math.Pow(1.0 - t, 3.0); // CubicEase EaseOut
            double windowW = _animFromW + (_animToW - _animFromW) * eased;
            double windowH = _animFromH + (_animToH - _animFromH) * eased;

            UpdateGeometry(windowW - 2.0 * ShadowPad, windowH - 2.0 * ShadowPad);

            if (t >= 1.0)
            {
                _animating = false;
                _resizeTimer.Stop();
                UpdateGeometry(_animToW - 2.0 * ShadowPad, _animToH - 2.0 * ShadowPad);
                if (_pendingCollapseLayout)
                {
                    // Height animation finished: now it is safe to hide the rows.
                    _pendingCollapseLayout = false;
                    _cardRows.Visibility = Visibility.Collapsed;
                    _cardHeader.Visibility = Visibility.Visible;
                }
            }
        }

        // ================================================================== position persistence
        private void RestorePosition()
        {
            try
            {
                if (!File.Exists(_positionFile)) return;
                string text = File.ReadAllText(_positionFile);
                Dictionary<string, object> obj = Json.ParseObjectOrNull(text);
                if (obj == null) return;

                double x;
                double y;
                if (!Json.TryGetDouble(obj, "x", out x)) return;
                if (!Json.TryGetDouble(obj, "y", out y)) return;
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) return;

                _customContentX = x;
                _customContentY = y;
                _defaultAnchor = false;
                Trace.Log("position restored path=[" + _positionFile + "] x=" + x.ToString("0.###", CultureInfo.InvariantCulture)
                    + " y=" + y.ToString("0.###", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Trace.LogException("position restore failed (default anchor)", ex);
            }
        }

        private void SavePosition()
        {
            try
            {
                string dir = PositionDirectory;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string text = "{\"x\":" + (_customContentX).ToString("0.###", CultureInfo.InvariantCulture) +
                              ",\"y\":" + (_customContentY).ToString("0.###", CultureInfo.InvariantCulture) + "}";
                File.WriteAllText(_positionFile, text);
                Trace.Log("position saved path=[" + _positionFile + "] text=" + text);
            }
            catch (Exception ex)
            {
                Trace.LogException("position save failed (degraded: not persisted)", ex);
            }
        }

        // ================================================================== mouse input
        private void OnLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (_exiting) return;

            // The count badge + chevron sit on top of the card: a click there toggles the task
            // list and must never start a drag or count as a "focus DSH" click.
            if (TaskCount() >= 2 && _cardChrome.Visibility == Visibility.Visible && _expandHit.IsMouseOver)
            {
                ToggleExpanded();
                e.Handled = true;
                return;
            }

            _leftDown = true;
            _dragging = false;
            _downScreenDevice = PointToScreen(e.GetPosition(this));
            _downLeft = Left;
            _downTop = Top;

            try
            {
                CaptureMouse();
                _mouseCaptured = true;
            }
            catch (Exception)
            {
                _mouseCaptured = false;
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_leftDown || _exiting) return;

            Point current = PointToScreen(e.GetPosition(this));
            double dx = (current.X - _downScreenDevice.X) / _dpiScale;
            double dy = (current.Y - _downScreenDevice.Y) / _dpiScale;

            if (!_dragging)
            {
                if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;
                _dragging = true;
                Trace.Log("drag start screen=(" + current.X.ToString("0", CultureInfo.InvariantCulture)
                    + "," + current.Y.ToString("0", CultureInfo.InvariantCulture) + ")");
            }

            double left = _downLeft + dx;
            double top = _downTop + dy;
            ClampWindowPosition(ref left, ref top);
            Left = left;
            Top = top;
        }

        private void OnLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (!_leftDown) return;
            _leftDown = false;

            if (_mouseCaptured)
            {
                try
                {
                    ReleaseMouseCapture();
                }
                catch (Exception)
                {
                }
                _mouseCaptured = false;
            }

            if (_dragging)
            {
                CompleteDrag();
                return;
            }

            // Plain click: bring DSH back to the foreground.
            HandlePlainClick();
        }

        private void CompleteDrag()
        {
            _dragging = false;
            _defaultAnchor = false;
            _customContentX = Left + ShadowPad;
            _customContentY = Top + ShadowPad;
            SavePosition();
            Trace.Log("drag end left=" + Left.ToString("0.###", CultureInfo.InvariantCulture)
                + " top=" + Top.ToString("0.###", CultureInfo.InvariantCulture)
                + " contentX=" + _customContentX.ToString("0.###", CultureInfo.InvariantCulture)
                + " contentY=" + _customContentY.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private void HandlePlainClick()
        {
            Trace.Log("click -> focus DSH");
            FocusDsh();
        }

        private void FocusDsh()
        {
            try
            {
                Native.FocusDshMainWindow();
                Trace.Log("focus DSH requested (no modal error on failure)");
            }
            catch (Exception ex)
            {
                Trace.LogException("focus DSH failed (ignored)", ex);
            }

            if (_pipe != null)
            {
                _pipe.SendLine("{\"t\":\"focus\"}");
            }
        }

        // ================================================================== context menu
        private void BuildContextMenu()
        {
            _menu = new ContextMenu();
            Style menuStyle = _root.Resources["HudContextMenu"] as Style;
            if (menuStyle != null) _menu.Style = menuStyle;
            _menu.Placement = PlacementMode.MousePoint;
            _menu.PlacementTarget = _root;

            AddMenuItem("回到 DSH", delegate { FocusDsh(); });
            AddMenuItem("隐藏 30 分钟", delegate { HideForAWhile(); });
            AddMenuItem("退出小窗", delegate { QuitByUser(); });
        }

        private void AddMenuItem(string header, Action action)
        {
            MenuItem item = new MenuItem();
            item.Header = header;
            Style itemStyle = _root.Resources["HudMenuItem"] as Style;
            if (itemStyle != null) item.Style = itemStyle;
            item.Click += delegate
            {
                if (_exiting) return;
                Trace.Log("menu click header=[" + header + "]");
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Trace.LogException("menu action failed (ignored)", ex);
                }
            };
            _menu.Items.Add(item);
        }

        private void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            OpenMenu();
            e.Handled = true;
        }

        private void OpenMenu()
        {
            if (_exiting) return;

            _leftDown = false;
            _dragging = false;
            if (_mouseCaptured)
            {
                try
                {
                    ReleaseMouseCapture();
                }
                catch (Exception)
                {
                }
                _mouseCaptured = false;
            }

            try
            {
                _menu.PlacementTarget = _root;
                _menu.IsOpen = false;
                _menu.IsOpen = true;
                Trace.Log("context menu opened");
            }
            catch (Exception ex)
            {
                Trace.LogException("context menu open failed (ignored)", ex);
            }
        }

        private void HideForAWhile()
        {
            Trace.Log("hide for " + _hideSeconds.ToString("0", CultureInfo.InvariantCulture) + "s");
            try
            {
                Hide();
            }
            catch (Exception ex)
            {
                Trace.LogException("hide failed (ignored)", ex);
            }

            if (_hideTimer != null) _hideTimer.Stop();
            _hideTimer = new DispatcherTimer();
            _hideTimer.Interval = TimeSpan.FromSeconds(_hideSeconds);
            _hideTimer.Tick += delegate
            {
                if (_hideTimer != null) _hideTimer.Stop();
                _hideTimer = null;
                if (_exiting) return;
                try
                {
                    Show();
                    Trace.Log("hide timer expired -> show again");
                }
                catch (Exception ex)
                {
                    Trace.LogException("re-show failed (ignored)", ex);
                }
            };
            _hideTimer.Start();
        }

        // ================================================================== v3 expanded task list
        private int TaskCount()
        {
            return _tasks == null ? 0 : _tasks.Count;
        }

        /// <summary>Card content height in DIP: 72 collapsed, otherwise padding + rows + optional "+N" row.</summary>
        private double ContentHeightForCard()
        {
            int count = TaskCount();
            if (count < 2 || !_expanded) return CardH;
            int rows = Math.Min(count, MaxRows);
            double h = 2.0 * ExpandedPad + rows * RowH;
            if (count > MaxRows) h += SummaryH;
            return h;
        }

        private string ComputeRowsSignature()
        {
            if (_tasks == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _tasks.Count; i++)
            {
                HudTask t = _tasks[i];
                sb.Append((int)t.Status).Append('\u0001');
                sb.Append(t.Session == null ? "" : t.Session).Append('\u0001');
                sb.Append(t.Title == null ? "" : t.Title).Append('\u0001');
                sb.Append(t.Detail == null ? "" : t.Detail).Append('\u0001');
            }
            return sb.ToString();
        }

        private void BuildRowsIfNeeded()
        {
            if (_tasks == null || _tasks.Count < 2)
            {
                if (_cardRows.Children.Count > 0) _cardRows.Children.Clear();
                _rowsSignature = null;
                return;
            }
            string signature = ComputeRowsSignature();
            if (signature == _rowsSignature) return;
            _rowsSignature = signature;
            BuildRows();
        }

        private void BuildRows()
        {
            _cardRows.Children.Clear();
            int count = _tasks.Count;
            int display = Math.Min(count, MaxRows);

            for (int i = 0; i < display; i++)
            {
                HudTask task = _tasks[i];

                Border row = new Border();
                row.Height = RowH;
                row.Background = Brushes.Transparent;
                row.BorderBrush = SeparatorBrush;
                bool hasSeparator = (i < display - 1) || (count > MaxRows);
                row.BorderThickness = new Thickness(0, 0, 0, hasSeparator ? 1.0 : 0.0);
                row.MouseEnter += delegate { row.Background = RowHoverBrush; };
                row.MouseLeave += delegate { row.Background = Brushes.Transparent; };

                Grid grid = new Grid();
                ColumnDefinition c0 = new ColumnDefinition();
                c0.Width = GridLength.Auto;
                ColumnDefinition c1 = new ColumnDefinition();
                c1.Width = new GridLength(1.0, GridUnitType.Star);
                ColumnDefinition c2 = new ColumnDefinition();
                c2.Width = GridLength.Auto;
                grid.ColumnDefinitions.Add(c0);
                grid.ColumnDefinitions.Add(c1);
                grid.ColumnDefinitions.Add(c2);

                System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse();
                dot.Width = 6.0;
                dot.Height = 6.0;
                dot.Fill = StatusBrush(task.Status);
                dot.VerticalAlignment = VerticalAlignment.Center;
                dot.Margin = new Thickness(2, 0, 0, 0);
                Grid.SetColumn(dot, 0);
                if (task.Status == HudMode.Running)
                {
                    DoubleAnimation breathing = new DoubleAnimation(1.0, 0.45, TimeSpan.FromMilliseconds(800));
                    breathing.AutoReverse = true;
                    breathing.RepeatBehavior = RepeatBehavior.Forever;
                    breathing.EasingFunction = new SineEase();
                    dot.BeginAnimation(UIElement.OpacityProperty, breathing);
                }
                grid.Children.Add(dot);

                TextBlock title = new TextBlock();
                title.Text = string.IsNullOrEmpty(task.Title) ? "未命名会话" : task.Title;
                title.FontSize = 15.0;
                title.FontWeight = FontWeights.SemiBold;
                title.Foreground = TitleBrush;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                title.VerticalAlignment = VerticalAlignment.Center;
                title.Margin = new Thickness(10, 0, 8, 0);
                Grid.SetColumn(title, 1);
                grid.Children.Add(title);

                TextBlock status = new TextBlock();
                status.Text = StatusText(task);
                status.FontSize = 12.0;
                status.Foreground = StatusTextBrush(task.Status);
                status.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(status, 2);
                grid.Children.Add(status);

                row.Child = grid;
                _cardRows.Children.Add(row);
            }

            if (count > MaxRows)
            {
                Border summary = new Border();
                summary.Height = SummaryH;
                summary.Background = Brushes.Transparent;
                summary.IsHitTestVisible = false;

                TextBlock more = new TextBlock();
                more.Text = "+" + (count - MaxRows).ToString(CultureInfo.InvariantCulture) + " 个任务";
                more.FontSize = 12.5;
                more.Foreground = SecondaryBrush;
                more.VerticalAlignment = VerticalAlignment.Center;
                more.Margin = new Thickness(18, 0, 0, 0);
                more.IsHitTestVisible = false;

                summary.Child = more;
                _cardRows.Children.Add(summary);
            }
        }

        private static SolidColorBrush StatusBrush(HudMode status)
        {
            switch (status)
            {
                case HudMode.Attention: return AmberBrush;
                case HudMode.Question: return BlueBrush;
                case HudMode.Done: return GreenBrush;
                case HudMode.Error: return RedBrush;
                default: return BlueBrush;
            }
        }

        private static SolidColorBrush StatusTextBrush(HudMode status)
        {
            switch (status)
            {
                case HudMode.Attention: return AmberBrush;
                case HudMode.Question: return BlueBrush;
                case HudMode.Done: return GreenBrush;
                case HudMode.Error: return RedBrush;
                default: return SecondaryBrush;
            }
        }

        private static string StatusText(HudTask task)
        {
            if (task == null) return "";
            string detail = task.Detail == null ? "" : task.Detail.Trim();
            if (detail.Length > 0) return detail;
            return HudState.LabelFor(task.Status);
        }

        private static int ModePriority(HudMode mode)
        {
            switch (mode)
            {
                case HudMode.Attention: return 5;
                case HudMode.Question: return 4;
                case HudMode.Error: return 3;
                case HudMode.Running: return 2;
                case HudMode.Done: return 1;
                default: return 0;
            }
        }

        private static HudMode HighestPriorityMode(List<HudTask> tasks)
        {
            HudMode best = HudMode.Running;
            int bestPriority = -1;
            if (tasks != null)
            {
                for (int i = 0; i < tasks.Count; i++)
                {
                    int priority = ModePriority(tasks[i].Status);
                    if (priority > bestPriority)
                    {
                        bestPriority = priority;
                        best = tasks[i].Status;
                    }
                }
            }
            return best;
        }

        private bool HasStatus(HudMode status)
        {
            if (_tasks == null) return false;
            for (int i = 0; i < _tasks.Count; i++)
            {
                if (_tasks[i].Status == status) return true;
            }
            return false;
        }

        private void UpdateChromeButton()
        {
            int count = TaskCount();
            bool multi = count >= 2;

            if (multi && !_expanded)
            {
                _taskBadge.Visibility = Visibility.Visible;
                _taskBadgeText.Text = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                _taskBadge.Visibility = Visibility.Collapsed;
            }

            _chevronPath.Data = Geometry.Parse(_expanded ? "M 3,10 L 8,5 L 13,10" : "M 3,6 L 8,11 L 13,6");
            _chevronPath.Stroke = ChevronBrush;
            try
            {
                ToolTipService.SetToolTip(_expandHit, _expanded ? "收起任务列表" : ("展开全部 " + count.ToString(CultureInfo.InvariantCulture) + " 个任务"));
            }
            catch (Exception)
            {
            }

            _cardChrome.Margin = _expanded ? new Thickness(0, 0, 12, 0) : new Thickness(0, 13, 12, 0);
            _cardTextPanel.Margin = new Thickness(10, 0, multi && !_expanded ? TextReserveMulti : 0, 0);
        }

        private void UpdateChromeAndLayout()
        {
            int count = TaskCount();
            bool multi = count >= 2;

            _cardChrome.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            if (!multi) _taskBadge.Visibility = Visibility.Collapsed;

            if (_expanded)
            {
                _cardHeader.Visibility = Visibility.Collapsed;
                _cardRows.Visibility = Visibility.Visible;
            }
            else
            {
                _cardHeader.Visibility = Visibility.Visible;
                _cardRows.Visibility = Visibility.Collapsed;
            }
            UpdateChromeButton();
        }

        public void ToggleExpanded()
        {
            if (_exiting) return;
            if (TaskCount() < 2) return;
            SetExpanded(!_expanded, true);
            Trace.Log("expand toggled expanded=" + _expanded + " tasks=" + TaskCount());
        }

        public void SetExpanded(bool expanded, bool animate)
        {
            if (_exiting) return;

            _expanded = expanded;
            _pendingCollapseLayout = false;
            BuildRowsIfNeeded();

            if (expanded)
            {
                _cardRows.Visibility = Visibility.Visible;
                _cardHeader.Visibility = Visibility.Collapsed;
                StartRowsFade();
            }
            else if (animate && !_pillShown)
            {
                // Keep the rows visible while the card shrinks; OnResizeTick hides them at the end.
                _pendingCollapseLayout = true;
                _cardRows.Visibility = Visibility.Visible;
                _cardHeader.Visibility = Visibility.Collapsed;
            }
            else
            {
                _cardRows.Visibility = Visibility.Collapsed;
                _cardHeader.Visibility = Visibility.Visible;
            }
            UpdateChromeButton();

            double targetH = ContentHeightForCard();
            if (animate && !_pillShown && _cardRoot.Visibility == Visibility.Visible)
            {
                _bottomAnchorAnim = true;
                AnimateSize(CardW, targetH);
            }
            else
            {
                UpdateGeometry(CardW, targetH);
            }

            if (!expanded) MaybeAutoCollapse();
        }

        private void StartRowsFade()
        {
            try
            {
                _cardRows.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(RowsFadeMs)));
            }
            catch (Exception)
            {
            }
        }

        private void MaybeAutoCollapse()
        {
            if (!_expanded || TaskCount() >= 2)
            {
                StopExpandAutoTimer();
                return;
            }
            if (_expandAutoTimer != null) return;

            _expandAutoTimer = new DispatcherTimer();
            _expandAutoTimer.Interval = TimeSpan.FromMilliseconds(ExpandAutoCollapseMs);
            _expandAutoTimer.Tick += delegate
            {
                StopExpandAutoTimer();
                if (_exiting) return;
                if (TaskCount() >= 2) return;
                Trace.Log("auto collapse: task list is no longer multi");
                SetExpanded(false, true);
            };
            _expandAutoTimer.Start();
        }

        private void StopExpandAutoTimer()
        {
            if (_expandAutoTimer != null)
            {
                _expandAutoTimer.Stop();
                _expandAutoTimer = null;
            }
        }

        // ================================================================== shutdown
        private void QuitByUser()
        {
            if (_pipe != null)
            {
                _pipe.SendLine("{\"t\":\"bye\"}");
            }
            if (!string.IsNullOrEmpty(_options.StateFile))
            {
                WriteByeFile();
            }
            ExitNow(0, "user chose quit");
        }

        /// <summary>
        /// User chose 退出小窗 in the state-file transport: leave a bye marker so the Host
        /// does not respawn the HUD for the rest of its lifetime.
        /// </summary>
        private void WriteByeFile()
        {
            try
            {
                string dir = PositionDirectory;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "hud-bye-v1.json");
                string tmp = path + ".tmp";
                long ms = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                string content = "{\"t\":\"bye\",\"ts\":" + ms.ToString(CultureInfo.InvariantCulture) + "}";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                Trace.Log("bye file written path=[" + path + "] content=" + content);
            }
            catch (Exception ex)
            {
                // Sandboxed test environments may forbid %LOCALAPPDATA% writes; the exit code
                // still communicates the user's choice, so this degradation is trace-only.
                Trace.LogException("bye file write failed (degraded: exit code only)", ex);
            }
        }

        public void ExitNow(int code, string reason)
        {
            if (_exiting) return;
            _exiting = true;
            Trace.Log("ExitNow code=" + code + " reason=[" + (reason == null ? "" : reason) + "]");

            if (_pipe != null)
            {
                try
                {
                    _pipe.Stop();
                }
                catch (Exception)
                {
                }
            }

            if (_stateFile != null)
            {
                try
                {
                    _stateFile.Dispose();
                }
                catch (Exception)
                {
                }
                _stateFile = null;
            }

            StopTimer(_doneTimer);
            StopTimer(_hideTimer);
            StopTimer(_demoTimer);
            StopTimer(_titleTimer);
            StopTimer(_ownerTimer);
            StopTimer(_resizeTimer);
            StopExpandAutoTimer();

            try
            {
                Application.Current.Shutdown(code);
            }
            catch (Exception)
            {
                try
                {
                    Close();
                }
                catch (Exception)
                {
                }
            }
        }

        private static void StopTimer(DispatcherTimer timer)
        {
            try
            {
                if (timer != null) timer.Stop();
            }
            catch (Exception)
            {
            }
        }

        private void Dispatch(Action action)
        {
            if (action == null) return;
            try
            {
                if (Dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
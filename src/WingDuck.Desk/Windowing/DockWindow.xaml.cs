using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WingDuck.Desk.Interop;
using WingDuck.Desk.Items;
using WingDuck.Desk.Shell;

namespace WingDuck.Desk.Windowing;

/// <summary>DESIGN §8.2 的五个可见状态（托盘驻留单列一个，便于解释"为什么窗口不可见还不轮询"）。</summary>
public enum DockState
{
    Floating,
    SnappedExpanded,
    SnappedCollapsed,

    /// <summary>钉住（v1.1 条目 3）：不贴边、不收起、定时器也停掉，停在用户放的地方。</summary>
    Pinned,
    Hidden,
}

/// <summary>
/// 侧栏本体。几何一律以<b>设备像素</b>算（与 Win32 的指针、工作区同单位），
/// 只在 <see cref="ApplyBox"/> / <see cref="DeviceBox"/> 两处出入口换算成 WPF 的 DIP。
/// </summary>
public partial class DockWindow : Window
{
    /// <summary>内部排序拖放专用的私有格式。刻意不放 FileDrop：数据里没有文件列表，shell 就无从搬文件。</summary>
    private const string ReorderFormat = "wingduck-desktop/reorder";

    public static readonly DependencyProperty PanelOrientationProperty =
        DependencyProperty.Register(nameof(PanelOrientation), typeof(Orientation), typeof(DockWindow),
            new PropertyMetadata(Orientation.Vertical));

    /// <summary>吸附边决定的排列方向：左右边竖排、上下边横排（DESIGN §8.2 四边差异）。</summary>
    public Orientation PanelOrientation
    {
        get => (Orientation)GetValue(PanelOrientationProperty);
        set => SetValue(PanelOrientationProperty, value);
    }

    private SnapOptions _options = DockMetrics.Default;
    private HoverTracker _hover;

    /// <summary>DIP 口径的设置值；赋值时重建状态机，保证改延时立刻生效（DESIGN 纪律第 10 条）。</summary>
    public SnapOptions Options
    {
        get => _options;
        set
        {
            _options = value;
            _hover = new HoverTracker(value, TickClock)
            {
                Expanded = _hover.Expanded,
                InHotZone = _hover.InHotZone,
            };
        }
    }

    public DockState State { get; private set; } = DockState.Floating;
    public Edge Edge => _snap.Edge;

    /// <summary>这条栏的 id（v1.1 多条侧栏）。0 = 原始那条：清单文件仍是 <c>items.json</c>，界面上也不给删。
    /// 走构造参数而不是 <c>init</c>：标题要在构造函数里按它分流，而对象初始化器跑得比构造函数晚。</summary>
    public int CardId { get; }

    /// <summary>用户拖边框定下的沿边长度（DIP）。0 = 按条目数自适应（DESIGN §8.1）。</summary>
    public double AlongLength { get; set; }

    /// <summary>钉住中：不贴边、不收起（v1.1 条目 3）。</summary>
    public bool Pinned => State == DockState.Pinned;

    /// <summary>沿边起点（相对工作区，DIP）。-1 = 还没定过，此时按工作区中点居中落位。</summary>
    public double AlongOffset => _alongOffset;

    /// <summary>
    /// 这个起点是不是<b>用户亲手</b>定的（拖过、或磁盘上本来就记着）。居中落位不算——
    /// 把"我们算出来的中点"当作用户位置落盘，条目一变多，下次开机就再也不居中了。
    /// </summary>
    public bool AlongUserSet => _alongUserSet;

    /// <summary>工作区在沿边方向有多长（DIP）。App 算"新栏紧贴上一条、别贴出屏幕"要用。</summary>
    public double WorkAlong =>
        (_snap.Axis == LayoutAxis.Vertical ? WorkArea.ForScreen(_hwnd).Height : WorkArea.ForScreen(_hwnd).Width) / _scale;

    /// <summary>拖过边框定下的展开厚度（DIP，0 = 自适应）。就存在 Options 里，读它即可。</summary>
    public double ThicknessOverride => _options.ExpandedThickness;

    /// <summary>是否叠加 WS_EX_NOACTIVATE。默认 true；调试手感时可关掉看差异（DESIGN §8.3）。</summary>
    public bool NoActivate { get; set; } = true;

    /// <summary>色罩不透明度 0–255（158≈DESIGN §8.1 的 62%）。基色固定 #1B262C，只有 alpha 可调。</summary>
    public int VeilAlpha
    {
        set
        {
            var a = (byte)Math.Clamp(value, 0, 255);
            Shell.Background = new SolidColorBrush(Color.FromArgb(a, 0x1B, 0x26, 0x2C));
        }
    }

    /// <summary>条目数据。由 App 建好再塞进来，窗口负责展示与就地增删。</summary>
    public ItemStore? Store { get; set; }

    /// <summary>空态提示是否已经看过（第一次成功拖入后置 true，永久不再提示）。</summary>
    public bool EmptyHintSeen
    {
        get => _emptyHintSeen;
        set
        {
            if (_emptyHintSeen == value) return;
            _emptyHintSeen = value;
            EmptyHintSeenChanged?.Invoke(value);
        }
    }

    private bool _emptyHintSeen;

    /// <summary>拖拽落位、吸附边变化后触发，T17 用它把边记进 settings。</summary>
    public event Action? LayoutChanged;

    /// <summary>用户点了"隐藏"。T16 的托盘订阅它来弹提示。</summary>
    public event Action? HideRequested;

    /// <summary>需要告诉用户一句话（启动失败、移除条目、出错降级）。T16 之前由托盘或提示条承接。</summary>
    public event Action<string>? NoticeRequested;

    /// <summary>空态提示的"看过与否"变了，让 App 落盘。窗口不直接碰 settings 文件。</summary>
    public event Action<bool>? EmptyHintSeenChanged;

    /// <summary>右键"新增卡片栏"（v1.1 条目 8）。谁提的（CardId）带上，App 据此决定贴在哪条之后。</summary>
    public event Action<int>? AddCardRequested;

    /// <summary>点了顶部的 ✕（v1.1 条目 9）。确认框由 App 弹，窗口自己什么都不删。</summary>
    public event Action<int>? DeleteRequested;

    private IntPtr _hwnd;
    private double _scale = 1.0;
    private SnapResult _snap = new(Edge.None, LayoutAxis.Vertical, default, default, default);
    private DispatcherTimer? _tick;          // 全窗口唯一定时器，见 DESIGN §8.4
    private bool _transitioning;
    private int _itemCount;
    private DockItem? _menuTarget;
    private DockItem? _dragCandidate;
    private Point _dragStart;
    private double _alongOffset = -1;        // 沿边起点（DIP），-1 = 未定
    private bool _alongUserSet;              // 只有"用户拖过"或"磁盘上本来就记着"才置位，居中落位不算
    private DockState _stateBeforeHide;      // 从托盘恢复时要知道该回到收起还是钉住
    /// <summary>指针当前压在哪根边上，只用来换光标形状。它跟着指针实时变，拖拽期间不许拿它当依据，要用 <see cref="_resizeGrip"/>。</summary>
    private Grip _grip;
    /// <summary>按下那一刻定住的把手，整次拖拽只认它：光标越出窗口后 <see cref="_grip"/> 就不再是当初抓住的那根边了。</summary>
    private Grip _resizeGrip;
    private bool _resizing;
    private BoxD _resizeStart;

    private static long TickClock() => Environment.TickCount64;

    public DockWindow(int cardId = 0)
    {
        CardId = cardId;
        InitializeComponent();
        _hover = new HoverTracker(_options, TickClock);
        MouseEnter += (_, _) => _hover.InHotZone = true;
        MouseLeave += (_, _) => _hover.InHotZone = false;
        DragHandle.MouseLeftButtonDown += OnDragStart;

        // 拖边框改大小（v1.1 条目 2）。Preview 隧道路道：指针压在把手上时不许顶部那条把手条抢走这一下，
        // 否则贴着上边缘的栏既改不了大小也谈不上别的。
        PreviewMouseLeftButtonDown += OnResizeButtonDown;
        MouseMove += OnResizeMoving;
        MouseLeftButtonUp += OnResizeUp;
        MouseLeave += (_, _) => { if (!_resizing) { _grip = Grip.None; Cursor = Cursors.Arrow; } };

        ItemList.MouseLeftButtonUp += OnItemClick;
        // 右键目标改在窗口层记：落在条目上才有"打开/重命名/移除"，落在背景上只给"新增卡片栏"
        PreviewMouseRightButtonDown += OnItemRightClick;
        ItemList.PreviewMouseLeftButtonDown += OnItemDragStart;
        ItemList.MouseMove += OnItemDragging;
        DragOver += OnDragOver;
        Drop += OnDrop;

        // 只有原始那条顶 SingleInstance.WindowTitle 这个名字：第二个进程靠 FindWindow 按标题找它。
        // 多出来的栏共用同一个标题的话，唤醒信号可能被发给没挂监听钩子的那一条，然后就静默丢了。
        Title = CardId == 0 ? SingleInstance.WindowTitle : $"{SingleInstance.WindowTitle} #{CardId}";

        // 删栏走 Close：静态的 SystemEvents 会一直攥着这个窗口，不注销的话关掉的栏还会被"工作区变了"叫醒
        Closed += (_, _) =>
        {
            _tick?.Stop();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnWorkAreaChanged;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _scale = WorkArea.DpiScaleFor(_hwnd);
        ApplyExtendedStyles();

        _tick = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DockMetrics.PollIntervalMs),
        };
        _tick.Tick += OnTick;

        // 工作区一变，贴边几何就全废了（DESIGN §10）：任务栏挪位走消息，改分辨率走系统事件
        HwndSource.FromHwnd(_hwnd)?.AddHook(OnWindowMessage);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnWorkAreaChanged;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Win32.WM_SETTINGCHANGE) return IntPtr.Zero;
        handled = true;
        // 别在窗口过程里直接改位置：先让这条消息走完，否则系统可能重入
        Dispatcher.BeginInvoke(Reanchor);
        return IntPtr.Zero;
    }

    private void OnWorkAreaChanged(object? sender, EventArgs e) => Reanchor();

    private void ApplyExtendedStyles()
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        // TOOLWINDOW 顺带解决隐藏后 Alt+Tab 残留窗口（DESIGN §8.3）
        var add = Win32.WS_EX_TOOLWINDOW | (NoActivate ? Win32.WS_EX_NOACTIVATE : 0);
        _ = Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex | add));
    }

    // ---- 单位换算 ----

    private SnapOptions Scaled => DockMetrics.Scale(_options, _scale);

    private BoxD DeviceBox() => new(Left * _scale, Top * _scale, Width * _scale, Height * _scale);

    private void ApplyBox(BoxD device)
    {
        Left = device.X / _scale;
        Top = device.Y / _scale;
        Width = device.Width / _scale;
        Height = device.Height / _scale;
    }

    // ---- 吸附 ----

    private LayoutAxis Axis => PanelOrientation == Orientation.Vertical ? LayoutAxis.Vertical : LayoutAxis.Horizontal;

    /// <summary>这条栏沿边方向该多长（DIP）：拖过边框就用覆盖值，否则按条目数自适应。</summary>
    private double AlongDip() => AlongLength > 0 ? AlongLength : DockMetrics.ContentLength(_itemCount);

    /// <summary>
    /// 送去算吸附的矩形：沿边那一维填成"该有多长"，起点带上 <see cref="_alongOffset"/>（多条栏紧贴上一条靠它）。
    /// 没定过起点时取工作区中点居中（v1.2 条目 5a）——老写法沿用窗口当前位置，结果每次开机都落在
    /// XAML 默认那个偏上的位置，用户看到的就是"吸附没居中"。
    /// EdgeSnap 只取沿边那一维，交叉方向由展开厚度覆盖，所以不必先知道贴哪条边。
    /// </summary>
    private BoxD DesiredBox()
    {
        var along = AlongDip() * _scale;
        var box = DeviceBox() with { Width = along, Height = along };
        var work = WorkArea.ForScreen(_hwnd);
        if (_alongOffset < 0)
        {
            var centered = EdgeSnap.CenteredAlong(work, Axis, along);
            return Axis == LayoutAxis.Vertical ? box with { Y = centered } : box with { X = centered };
        }
        var start = Axis == LayoutAxis.Vertical
            ? work.Top + _alongOffset * _scale
            : work.Left + _alongOffset * _scale;
        return Axis == LayoutAxis.Vertical ? box with { Y = start } : box with { X = start };
    }

    /// <summary>悬浮/钉住态没有吸附计算来定尺寸，就按"该有多厚、该有多长"直接摆（DIP 口径）。</summary>
    private void ApplyFreeSize()
    {
        var thickness = DockMetrics.ThicknessFor(_options, Axis);
        var along = AlongDip();
        if (Axis == LayoutAxis.Vertical)
        {
            Width = thickness;
            Height = along;
        }
        else
        {
            Height = thickness;
            Width = along;
        }
    }

    /// <summary>贴到指定边（设置里手动选边、托盘恢复、启动落位都用这条），不经过指针检测。</summary>
    public void SnapTo(Edge edge)
    {
        if (edge == Edge.None) return;
        // 排列方向先定下来：DesiredBox 要按它决定沿边起点落在 X 还是 Y 上
        PanelOrientation = edge is Edge.Left or Edge.Right ? Orientation.Vertical : Orientation.Horizontal;
        ApplySnap(EdgeSnap.ComputeFor(DesiredBox(), WorkArea.ForScreen(_hwnd), edge, Scaled));
    }

    /// <summary>落一整套吸附几何：切排列方向、以展开态就位，然后按 §8.2 的落位延时收起。</summary>
    public void ApplySnap(SnapResult result)
    {
        _snap = result;
        PanelOrientation = result.Axis == LayoutAxis.Vertical ? Orientation.Vertical : Orientation.Horizontal;
        Scroller.VerticalScrollBarVisibility = result.Axis == LayoutAxis.Vertical
            ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        Scroller.HorizontalScrollBarVisibility = result.Axis == LayoutAxis.Vertical
            ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;

        State = DockState.SnappedExpanded;
        _hover.Expanded = true;
        ApplyBox(result.Expanded);
        Shell.CornerRadius = new CornerRadius(DockMetrics.CornerRadius);
        ShowContent();
        UpdateAlongOffset();
        _hover.BeginSettle();
        _tick?.Start();
        LayoutChanged?.Invoke();
    }

    /// <summary>
    /// 把展开态的沿边起点回读成 DIP。落盘（<c>cards[].along</c>）与"新栏紧贴上一条"都读这一个值，
    /// 所以拖拽、改大小、工作区变化之后都要调一次，不能各自另算。
    /// </summary>
    private void UpdateAlongOffset()
    {
        if (_hwnd == IntPtr.Zero) return;
        var work = WorkArea.ForScreen(_hwnd);
        _alongOffset = (Axis == LayoutAxis.Vertical
            ? DeviceBox().Y - work.Top
            : DeviceBox().X - work.Left) / _scale;
    }

    /// <summary>
    /// 启动时把落盘的沿边起点灌回来。缺这一步的话 <see cref="DesiredBox"/> 认不出这条栏该从哪儿起，
    /// 每次开机都退回 XAML 默认位置，多条栏"紧贴上一条"也就白贴了。
    /// </summary>
    public void ApplyAlongOffset(double dip)
    {
        _alongOffset = dip;
        _alongUserSet = true;   // 磁盘上记着的位置＝用户亲手定过的，之后不再抢回中点
    }

    // ---- 钉住（v1.1 条目 3）----

    /// <summary>
    /// 钉住＝"我就是要它停在这儿"：不吸附、不自动收起，60ms 那个轮询定时器也停掉
    /// （DESIGN §8.4 说清了它只为收起态存在，钉住态没有收起这回事）。仍可拖顶部把手挪位置、仍可被工作区变化夹回屏内。
    /// </summary>
    public void SetPinned(bool pinned)
    {
        if (Pinned == pinned || _hwnd == IntPtr.Zero) return;

        if (pinned)
        {
            State = DockState.Pinned;
            _snap = new SnapResult(Edge.None, Axis, DeviceBox(), DeviceBox(), DeviceBox());
            _tick?.Stop();               // 唯一一个定时器停掉：钉住态空闲 CPU 比贴边态还低
            _hover.Expanded = true;
            ShowContent();
            UpdateAlongOffset();
            LayoutChanged?.Invoke();
            return;
        }

        var work = WorkArea.ForScreen(_hwnd);
        ApplySnap(EdgeSnap.ComputeFor(DesiredBox(), work, EdgeSnap.NearestEdge(DeviceBox(), work), Scaled));
    }

    /// <summary>解除吸附，回到桌面中间的悬浮态（不轮询指针，省 CPU）。</summary>
    public void Float()
    {
        State = DockState.Floating;
        ApplyFreeSize();
        _snap = new SnapResult(Edge.None, Axis, DeviceBox(), DeviceBox(), DeviceBox());
        _hover.Expanded = true;
        ClampIntoWorkArea();
        _tick?.Stop();
        LayoutChanged?.Invoke();
    }

    private void ClampIntoWorkArea()
    {
        var work = WorkArea.ForScreen(_hwnd);
        var box = DeviceBox();
        var width = Math.Min(box.Width, work.Width);
        var height = Math.Min(box.Height, work.Height);
        ApplyBox(new BoxD(
            Math.Clamp(box.X, work.Left, work.Right - width),
            Math.Clamp(box.Y, work.Top, work.Bottom - height),
            width, height));
    }

    // ---- 展开 / 收起 ----

    public void Expand(bool animate)
    {
        if (State != DockState.SnappedCollapsed || _transitioning) return;
        State = DockState.SnappedExpanded;
        _hover.Expanded = true;
        ShowContent();
        Shell.CornerRadius = new CornerRadius(DockMetrics.CornerRadius);
        AnimateTo(_snap.Expanded, animate);
    }

    public void Collapse(bool animate)
    {
        if (State != DockState.SnappedExpanded || _transitioning) return;
        State = DockState.SnappedCollapsed;
        _hover.Expanded = false;
        HideContent();   // 收起过程中图标会被压扁，动画开始前先摘掉内容（DESIGN §8.2）
        AnimateTo(_snap.Collapsed, animate);
    }

    private void ShowContent()
    {
        Scroller.Visibility = Visibility.Visible;
        DragHandle.Visibility = Visibility.Visible;
        // 原始那条不给删（FR-16）；钉住态换个高亮色表示"现在归你管，不归边管"，不换字形——
        // 18px 高的小按钮里换字形会抖一下。
        DeleteButton.Visibility = CardDeleteConfirm.Deletable(CardId) ? Visibility.Visible : Visibility.Collapsed;
        PinButton.Foreground = new SolidColorBrush(Pinned ? Color.FromRgb(0xF5, 0xB0, 0x41) : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        PinButton.ToolTip = Pinned ? "已钉住：不贴边、不收起。点这里恢复吸附" : "钉住：停在这儿，不再贴边收起";
    }

    private void HideContent()
    {
        Scroller.Visibility = Visibility.Hidden;
        DragHandle.Visibility = Visibility.Hidden;
    }

    private void AnimateTo(BoxD target, bool animate)
    {
        var to = new BoxD(target.X / _scale, target.Y / _scale, target.Width / _scale, target.Height / _scale);
        if (!animate)
        {
            ApplyBox(target);
            FinishTransition();
            return;
        }

        _transitioning = true;
        var sb = new Storyboard { FillBehavior = FillBehavior.Stop };
        Animate(sb, WidthProperty, to.Width);
        Animate(sb, HeightProperty, to.Height);
        Animate(sb, LeftProperty, to.X);
        Animate(sb, TopProperty, to.Y);
        sb.Completed += (_, _) =>
        {
            ApplyBox(target);
            Shell.CornerRadius = new CornerRadius(
                State == DockState.SnappedCollapsed ? DockMetrics.CornerRadiusCollapsed : DockMetrics.CornerRadius);
            FinishTransition();
        };
        sb.Begin(this);
    }

    private static void Animate(Storyboard sb, DependencyProperty property, double to)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(DockMetrics.AnimationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        sb.Children.Add(animation);
    }

    private void FinishTransition() => _transitioning = false;

    // ---- 唯一的定时器 ----

    private void OnTick(object? sender, EventArgs e)
    {
        if (_transitioning) return;

        // 只有收起态需要轮询指针：展开态有可靠的 WPF MouseEnter/Leave，不必再查 Win32
        if (State == DockState.SnappedCollapsed && Win32.GetCursorPos(out var cursor))
            _hover.InHotZone = _snap.HotZone.Contains(new PointD(cursor.X, cursor.Y));

        switch (_hover.Evaluate())
        {
            case HoverIntent.Expand: Expand(true); break;
            case HoverIntent.Recollapse: Collapse(true); break;
        }
    }

    // ---- 拖拽移动 ----

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || State == DockState.SnappedCollapsed) return;
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;     // 按钮已松开，系统不让拖
        }
        AfterDrag();
    }

    private void AfterDrag()
    {
        _alongUserSet = true;   // 赶在下面 LayoutChanged 落盘之前定性的：这条栏的位置归用户了
        if (Pinned)
        {
            // 钉住态拖到哪停哪，不判吸附边（DESIGN §8.2 第五条转移：钉住 = 用户明说"就停这儿"）
            ClampIntoWorkArea();
            _snap = new SnapResult(Edge.None, Axis, DeviceBox(), DeviceBox(), DeviceBox());
            UpdateAlongOffset();
            LayoutChanged?.Invoke();
            return;
        }

        var work = WorkArea.ForScreen(_hwnd);
        var cursor = Win32.GetCursorPos(out var p) ? new PointD(p.X, p.Y) : new PointD(Left, Top);
        var result = EdgeSnap.Compute(DesiredBox(), work, cursor, Edge.None, Scaled);
        if (result.Edge == Edge.None) Float();
        else ApplySnap(result);
    }

    // ---- 拖边框改大小（v1.1 条目 2）----

    /// <summary>只有看得见完整内容的状态才给改大小：8px 细条上任何一处都算把手，会毁掉 hover 展开。</summary>
    private bool CanResize => !_transitioning && State is DockState.SnappedExpanded or DockState.Pinned or DockState.Floating;

    private PointD DevicePoint(Point dipInsideWindow)
    {
        var box = DeviceBox();
        return new PointD(box.X + dipInsideWindow.X * _scale, box.Y + dipInsideWindow.Y * _scale);
    }

    private Grip GripAt(MouseEventArgs e)
    {
        if (!CanResize) return Grip.None;
        var hit = ResizeGrip.HitTest(DeviceBox(), DevicePoint(e.GetPosition(this)), DockMetrics.ResizeBand * _scale);
        return ResizeGrip.Docked(hit, _snap.Edge);
    }

    private static Cursor CursorFor(Grip grip) => grip switch
    {
        Grip.Left or Grip.Right => Cursors.SizeWE,
        Grip.Top or Grip.Bottom => Cursors.SizeNS,
        Grip.Left | Grip.Top or Grip.Right | Grip.Bottom => Cursors.SizeNWSE,
        Grip.Right | Grip.Top or Grip.Left | Grip.Bottom => Cursors.SizeNESW,
        _ when grip.HasFlag(Grip.Left) || grip.HasFlag(Grip.Right) => Cursors.SizeWE,
        _ => Cursors.Arrow,
    };

    private void OnResizeButtonDown(object sender, MouseButtonEventArgs e)
    {
        var grip = GripAt(e);
        if (grip == Grip.None || e.ButtonState != MouseButtonState.Pressed) return;
        // 三颗小按钮就压在窗口上边缘那条把手带里，指针停在它们身上时这一下归按钮，不归改大小
        if (AncestorOf<ButtonBase>(e.OriginalSource) is not null) return;
        _grip = grip;
        _resizeGrip = grip;
        _resizeStart = DeviceBox();
        _resizing = true;
        CaptureMouse();
        e.Handled = true;     // 别再让顶部那条把手条把这一下当成"移动窗口"
    }

    private void OnResizeMoving(object sender, MouseEventArgs e)
    {
        if (_resizing)
        {
            var work = WorkArea.ForScreen(_hwnd);
            var vertical = Axis == LayoutAxis.Vertical;
            var minThickness = DockMetrics.MinThickness * _scale;
            var minAlong = DockMetrics.MinAlongLength * _scale;
            var maxCross = DockMetrics.MaxThickness * _scale;
            var box = ResizeGrip.Resize(_resizeStart, _resizeGrip, DevicePoint(e.GetPosition(this)),
                vertical ? minThickness : minAlong, vertical ? minAlong : minThickness, work);
            // 交叉方向实时夹住：留到松手再夹会出现"松手一瞬间弹回去"，看着像 bug
            box = vertical
                ? box with { Width = Math.Min(box.Width, maxCross) }
                : box with { Height = Math.Min(box.Height, maxCross) };
            ApplyBox(box);
            return;
        }

        var grip = GripAt(e);
        if (grip == _grip) return;
        _grip = grip;
        Cursor = CursorFor(grip);
    }

    private void OnResizeUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        var grip = _resizeGrip;
        _resizeGrip = Grip.None;
        _grip = Grip.None;
        var vertical = Axis == LayoutAxis.Vertical;
        var crossGrabbed = vertical
            ? grip.HasFlag(Grip.Left) || grip.HasFlag(Grip.Right)
            : grip.HasFlag(Grip.Top) || grip.HasFlag(Grip.Bottom);
        var alongGrabbed = vertical
            ? grip.HasFlag(Grip.Top) || grip.HasFlag(Grip.Bottom)
            : grip.HasFlag(Grip.Left) || grip.HasFlag(Grip.Right);

        var box = DeviceBox();
        if (crossGrabbed)
            SetThicknessOverride(DockMetrics.ClampThickness((vertical ? box.Width : box.Height) / _scale));
        if (alongGrabbed)
            AlongLength = Math.Max(DockMetrics.MinAlongLength, (vertical ? box.Height : box.Width) / _scale);

        // 松手后按吸附口径重落一次：贴边坐标、收起细条、热区三档矩形必须和新的厚度自洽
        if (Pinned)
        {
            ClampIntoWorkArea();
            _snap = new SnapResult(Edge.None, Axis, DeviceBox(), DeviceBox(), DeviceBox());
            UpdateAlongOffset();
            LayoutChanged?.Invoke();
        }
        else if (_snap.Edge != Edge.None)
        {
            ApplySnap(EdgeSnap.ComputeFor(DesiredBox(), WorkArea.ForScreen(_hwnd), _snap.Edge, Scaled));
        }
        else Float();
    }

    /// <summary>
    /// 只改 DIP 口径的厚度覆盖值，<b>不重排状态机</b>：拖边框一下能出上百个 MouseUp 之外的中间态，
    /// 每次都重建 <see cref="_hover"/> 会把展开/收起的计时清零。
    /// </summary>
    private void SetThicknessOverride(double dip) => _options = _options with { ExpandedThickness = dip };

    /// <summary>App 启动或设置变更时把某条栏的厚度覆盖值灌进来（0 = 自适应）。带状态机重建，一次性的。</summary>
    public void ApplyThicknessOverride(double dip) => Options = _options with { ExpandedThickness = dip };

    // ---- 条目 ----

    /// <summary>把条目列表刷进界面，并决定空态提示是否还要占位。</summary>
    public void SetItems(IReadOnlyList<DockItem> items)
    {
        ItemList.ItemsSource = items;
        EmptyHint.Visibility = items.Count == 0 && !EmptyHintSeen ? Visibility.Visible : Visibility.Collapsed;
        _itemCount = items.Count;
        Relayout();
    }

    /// <summary>
    /// 条目数变了：沿边长度跟着重算，但保持当前展开/收起状态，也不重跑落位延时
    /// （刚拖完鼠标还在侧栏里，收起计划会被热区作废，何必再排一次）。
    /// </summary>
    private void Relayout()
    {
        if (_hwnd == IntPtr.Zero || _transitioning || State == DockState.Hidden) return;

        // 长度变了，中点也就变了。用户没定过位置时把起点抹回"未定"，让 DesiredBox 重新居中：
        // 开机第一次落位是照空栏长度算的（120px），条目灌进来之后不重算就偏一截（实测偏 98px）。
        if (!_alongUserSet) _alongOffset = -1;

        if (Pinned)
        {
            ApplyFreeSize();
            ClampIntoWorkArea();
            UpdateAlongOffset();
            return;
        }

        if (_snap.Edge == Edge.None)
        {
            Float();
            return;
        }

        _snap = EdgeSnap.ComputeFor(DesiredBox(), WorkArea.ForScreen(_hwnd), _snap.Edge, Scaled);
        ApplyBox(State == DockState.SnappedCollapsed ? _snap.Collapsed : _snap.Expanded);
    }

    public void Refresh()
    {
        if (Store is { } store) SetItems(store.Items);
    }

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(e.OriginalSource) is not { } item) return;
        if (item.IsMissing)
        {
            // 失效条目点了不静默：告诉他怎么办（DESIGN §14 的验收项）
            Notify($"「{item.Name}」现在找不到，右键可以重新定位或从侧栏移除");
            return;
        }
        var result = Launcher.Open(item);
        if (!result.Ok) Notify($"打不开「{item.Name}」：{result.Error}");
    }

    private void OnItemRightClick(object sender, MouseButtonEventArgs e) => _menuTarget = ItemOf(e.OriginalSource);

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        if (ItemMenu.ItemContainerGenerator.ContainerFromIndex(0) is not MenuItem) return;
        foreach (var entry in ItemMenu.Items.OfType<MenuItem>())
        {
            if (entry.Header is string header and ("打开" or "打开所在位置" or "重命名"))
                entry.IsEnabled = _menuTarget is { IsMissing: false };
            else if (entry is MenuItem removable && removable.Header as string == "从侧栏移除")
                removable.IsEnabled = _menuTarget is not null;
        }
    }

    private void OnMenuOpen(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is not { } item) return;
        var result = Launcher.Open(item);
        if (!result.Ok) Notify($"打不开「{item.Name}」：{result.Error}");
    }

    private void OnMenuReveal(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is not { } item) return;
        var result = Launcher.Reveal(item);
        if (!result.Ok) Notify(result.Error ?? "没能打开所在位置");
    }

    private void OnMenuRename(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is not { } item || Store is not { } store) return;
        var dialog = new RenameWindow(item.Name) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        store.Rename(item.Id, dialog.Value);
        store.Save();
        Refresh();
    }

    private void OnMenuRemove(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is not { } item || Store is not { } store) return;
        store.Remove(item.Id);
        store.Save();
        Refresh();
        // 措辞强调"只从侧栏移除"，DESIGN §9 要的就是这句
        Notify($"已从侧栏移除「{item.Name}」，原文件还在原来的位置");
    }

    /// <summary>从可视元素往上找到它所属的条目。</summary>
    private static DockItem? ItemOf(object? source) => AncestorOf<FrameworkElement>(source)?.DataContext as DockItem;

    /// <summary>沿视觉树往上找第一类符合条件的祖先——判断"这一下点在哪块控件上"共用这一条遍历。</summary>
    private static T? AncestorOf<T>(object? source) where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ---- 拖放：外部收录 + 内部排序 ----

    private void OnItemDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = e.ButtonState == MouseButtonState.Pressed ? ItemOf(e.OriginalSource) : null;
        if (_dragCandidate is not null) _dragStart = e.GetPosition(this);
    }

    private void OnItemDragging(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragCandidate = null;
        // 只带自己的 Id，不带 FileDrop：源是本进程、数据里没有文件列表，shell 无从搬动用户文件
        DragDrop.DoDragDrop(ItemList, new DataObject(ReorderFormat, item.Id), DragDropEffects.Move);
    }

    private DragDropEffects _lastDragEffect = (DragDropEffects)(-1);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DropIn.AllowedEffect
            : e.Data.GetDataPresent(ReorderFormat) ? DragDropEffects.Move
            : DragDropEffects.None;
        // 只在应答变化时记一行：DragOver 每个像素都来一次，全记会淹掉日志
        if (e.Effects != _lastDragEffect)
        {
            CrashLog.Line($"dragover: 格式=[{string.Join(",", e.Data.GetFormats(false))}] 应答={e.Effects}");
            _lastDragEffect = e.Effects;
        }
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Store is not { } store)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(ReorderFormat) is string movingId)
        {
            var target = ItemAt(ItemList, e.GetPosition(ItemList));
            if (target is not null)
            {
                store.Move(movingId, store.Items.ToList().FindIndex(i => i.Id == target.Id));
                store.Save();
                Refresh();
            }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        // 外部拖入：只登记路径字符串，回报的效果永远只有 Link（DESIGN §9.2）
        var dropped = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var accepted = DropIn.Accept(dropped, store.Items.Select(i => i.Path).ToArray(), DefaultDisplayName);
        foreach (var item in accepted) store.Add(item);
        if (accepted.Count > 0)
        {
            store.Save();
            EmptyHintSeen = true;
            Refresh();
            Notify($"已收录 {accepted.Count} 个快捷方式，原文件没有移动");
        }
        else CrashLog.Line($"drop: 收到 {dropped.Length} 个路径，一个都没收下（重复或目标已不存在）");
        e.Effects = accepted.Count > 0 ? DropIn.AllowedEffect : DragDropEffects.None;
        e.Handled = true;
    }
    /// <summary>落在谁身上：命中测试后沿视觉树往上找条目的 DataContext。</summary>
    private static DockItem? ItemAt(ItemsControl list, Point position)
    {
        var current = VisualTreeHelper.HitTest(list, position)?.VisualHit;
        while (current is not null)
        {
            if (current is FrameworkElement fe && fe.DataContext is DockItem item) return item;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>显示名：文件夹取目录名，文件去掉扩展名（.lnk 也去掉，别把后缀留在脸上）。</summary>
    internal static string DefaultDisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return Path.HasExtension(name) ? Path.GetFileNameWithoutExtension(name) : name;
    }

    /// <summary>只发事件，不落盘——落盘由 App 统一负责，否则一条提示会被写两遍。</summary>
    private void Notify(string message) => NoticeRequested?.Invoke(message);

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        HideToTray();
        HideRequested?.Invoke();
    }

    /// <summary>钉住按钮：一条切换走两态。几何重算、定时器停起都在 <see cref="SetPinned"/> 里。</summary>
    private void OnPinClick(object sender, RoutedEventArgs e) => SetPinned(!Pinned);

    /// <summary>✕ 只管举手，确认框和落盘都在 App：窗口手里不该有删条栏的权力。</summary>
    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (CardDeleteConfirm.Deletable(CardId)) DeleteRequested?.Invoke(CardId);
    }

    private void OnMenuAddCard(object sender, RoutedEventArgs e) => AddCardRequested?.Invoke(CardId);

    // ---- 托盘驻留 ----

    public void HideToTray()
    {
        _stateBeforeHide = State;
        State = DockState.Hidden;
        _tick?.Stop();
        Hide();
    }

    /// <summary>从托盘恢复。Show 不 Activate：不抢键盘焦点（DESIGN §8.3）。</summary>
    public void ShowFromTray()
    {
        Show();
        // 钉住态被隐藏前没有贴边几何可言，恢复也不该把它拍回边上
        if (_stateBeforeHide == DockState.Pinned)
        {
            SetPinned(true);
            return;
        }
        if (_snap.Edge == Edge.None)
        {
            Float();
            return;
        }
        ApplySnap(EdgeSnap.ComputeFor(DesiredBox(), WorkArea.ForScreen(_hwnd), _snap.Edge, Scaled));
    }

    /// <summary>
    /// 唤出后留 ms 毫秒别急着收回（托盘左键）。不新建定时器，
    /// 豁免期由已有的那个 60ms 节拍判定——DESIGN §12 只允许收起态存在一个定时器。
    /// </summary>
    public void HoldExpanded(int ms) => _hover.HoldExpanded(ms);

    /// <summary>工作区变了（换分辨率 / 任务栏挪位）：贴回新的边，别让窗口跑到屏外。</summary>
    public void Reanchor()
    {
        _scale = WorkArea.DpiScaleFor(_hwnd);
        if (Pinned)
        {
            // 钉住态没有"贴回新的边"这回事：只按新缩放重摆尺寸，再保证别留在屏外
            ApplyFreeSize();
            ClampIntoWorkArea();
            _snap = new SnapResult(Edge.None, Axis, DeviceBox(), DeviceBox(), DeviceBox());
            UpdateAlongOffset();
            LayoutChanged?.Invoke();
            return;
        }
        if (_snap.Edge == Edge.None)
        {
            Float();
            return;
        }
        var collapsed = State == DockState.SnappedCollapsed;
        var before = collapsed ? _snap.Collapsed : _snap.Expanded;
        var result = EdgeSnap.ComputeFor(DesiredBox(), WorkArea.ForScreen(_hwnd), _snap.Edge, Scaled);
        _snap = result;
        var after = collapsed ? result.Collapsed : result.Expanded;
        // 只在位置真的变了时留痕：这条路径平时看不见，没日志就只能靠人盯屏幕
        if (before != after) CrashLog.Line($"工作区变化重贴：{before} → {after}");
        ApplyBox(after);
        LayoutChanged?.Invoke();
    }
}

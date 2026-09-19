using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using WingDuck.Desk.Interop;
using WingDuck.Desk.Windowing;
using WF = System.Windows.Forms;

namespace WingDuck.Desk.Shell;

/// <summary>
/// 托盘（T16）。用 WindowsDesktop 共享框架自带的 <see cref="WF.NotifyIcon"/>，
/// 不引第三方包（DESIGN §12 第 3 条）。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>托盘提示文字，DESIGN §16 定死的三个标识之一。</summary>
    private const string ProductName = "wingduck-desktop";

    /// <summary>托盘"显示/左键"要走的路：v1.1 起侧栏不止一条，唤回哪几条由 App 定。</summary>
    private readonly Action _restore;
    /// <summary>开机自启的当前状态。真值只有一份，在 App 的设置里；这里只读不问。</summary>
    private readonly Func<bool> _autoStartEnabled;
    private readonly uint _taskbarCreated;
    private WF.NotifyIcon? _icon;

    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    /// <summary>托盘上的"新增卡片栏"（v1.2 条目 5b）。挂在托盘时没有"哪条栏提的意见"，由 App 决定接在哪儿。</summary>
    public event Action? AddCardRequested;

    /// <summary>托盘上的"开机自启"（v1.3）：只转达"他要拧一下"，落盘与写注册表都由 App 走设置那条老路。</summary>
    public event Action? AutoStartToggleRequested;

    /// <param name="anchor">挂 TaskbarCreated 消息钩子用的那条栏。约定是原始侧栏（id=0，界面上删不掉），
    /// 所以钩子不会跟着窗口一起没。</param>
    public TrayIcon(DockWindow anchor, Action restore, Func<bool> autoStartEnabled)
    {
        _restore = restore;
        _autoStartEnabled = autoStartEnabled;
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(new WindowInteropHelper(anchor).Handle)?.AddHook(OnWindowMessage);
        Rebuild();
    }

    /// <summary>explorer 重启后托盘图标不会自己回来，这是本项目必踩的坑（DESIGN §10 第一行）。</summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_taskbarCreated) Rebuild();
        return IntPtr.Zero;
    }

    private void Rebuild()
    {
        _icon?.Dispose();
        CrashLog.Line("托盘图标（重）建");
        _icon = new WF.NotifyIcon
        {
            Text = ProductName,
            Icon = LoadAppIcon(),
            Visible = true,
        };
        _icon.ContextMenuStrip = BuildMenu();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != WF.MouseButtons.Left) return;
            Restore();
        };
    }

    /// <summary>
    /// 托盘图标取 <c>assets/wingduck.ico</c>。按 <see cref="WF.SystemInformation.SmallIconSize"/>
    /// 挑帧而不是 <c>new Icon(stream)</c> 一把梭：后者只拿文件里的第一帧（16px），150% 缩放的屏上
    /// 托盘要画 24px，结果就是一团糊。
    /// URI 必须写成程序集限定形式：<c>pack://application:,,,/x</c> 不带程序集名时按<b>入口程序集</b>找资源，
    /// 探针宿主下入口是 wingduck-probe，直接报"找不到资源"。
    /// 读不到就抛——回退成系统占位图的话，"图标到底进没进包"这件事永远查不出来。
    /// </summary>
    private static Icon LoadAppIcon()
    {
        const string ResourceUri = "pack://application:,,,/wingduck-desktop;component/assets/wingduck.ico";
        var part = System.Windows.Application.GetResourceStream(new System.Uri(ResourceUri, System.UriKind.Absolute))
            ?? throw new InvalidOperationException($"程序集里没有资源 {ResourceUri}");
        using var stream = part.Stream;
        var want = WF.SystemInformation.SmallIconSize;
        return new Icon(stream, Math.Max(16, want.Width), Math.Max(16, want.Height));
    }

    private WF.ContextMenuStrip BuildMenu()
    {
        // ShowImageMargin=false 时 WinForms 画不出 Checked 的勾（勾要占那条图文槽），
        // 所以自启的状态直接写进文字里，每次右键现读一次，不和设置面板各说一套。
        var menu = new WF.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("显示 " + ProductName, null, (_, _) => Restore());
        menu.Items.Add("新增卡片栏", null, (_, _) => AddCardRequested?.Invoke());
        menu.Items.Add("设置…", null, (_, _) => SettingsRequested?.Invoke());
        var autoStart = new WF.ToolStripMenuItem { Text = AutoStartText(_autoStartEnabled()) };
        autoStart.Click += (_, _) => AutoStartToggleRequested?.Invoke();
        menu.Opening += (_, _) => autoStart.Text = AutoStartText(_autoStartEnabled());
        menu.Items.Add(autoStart);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());
        return menu;
    }

    private static string AutoStartText(bool enabled) => enabled ? "✓ 开机自启" : "开机自启";

    private void Restore() => _restore();

    /// <summary>气泡提示。标题固定是产品名，正文由调用方给（DESIGN §10 要求文案里带日志路径）。</summary>
    public void Balloon(string message)
    {
        if (_icon is not { } icon) return;
        icon.BalloonTipTitle = ProductName;
        icon.BalloonTipText = message;
        icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        // 不显式 Dispose 会在任务栏留一个幽灵图标，直到下次 explorer 重启
        _icon?.Dispose();
        _icon = null;
    }
}

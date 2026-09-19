using System.Windows;
using System.Windows.Interop;
using WingDuck.Desk.Interop;

namespace WingDuck.Desk.Shell;

/// <summary>
/// 单实例（DESIGN §10）。第二份进程起来会把两份 items.json 互相覆盖，
/// 所以它必须立刻唤出第一份并自己退掉。
/// </summary>
internal static class SingleInstance
{
    /// <summary>用 Local\ 而非 Global\：非管理员在 Global 命名空间建内核对象需要 SeCreateGlobalPrivilege。</summary>
    private static string MutexName => @"Local\wingduck-desktop.instance" + NameSuffix;

    /// <summary>与窗口标题一致（DESIGN §16），也是 FindWindow 的键。</summary>
    public static string WindowTitle => "wingduck-desktop" + NameSuffix;

    /// <summary>
    /// 配置目录被 <see cref="AppPaths.ConfigDirEnvVar"/> 改过时给名字加后缀。
    /// 不加的话，隔离取证那份会和桌面上用户的真实例抢互斥体、并且把唤醒信号发给**它**（FindWindow 只按标题找）。
    /// </summary>
    private static string NameSuffix => AppPaths.IsOverridden ? ".dev" : string.Empty;

    private const int ShowCommand = 0x5744;   // "WD"，收到时校验，避免把别人的 WM_COPYDATA 当指令

    private static Mutex? _held;
    private static Action? _onShow;

    /// <summary>拿到唯一实例权返回 true；false 表示已有实例在跑，调用方应唤出它然后退出。</summary>
    public static bool TryAcquire()
    {
        _held = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return createdNew;
    }

    /// <summary>告诉正在跑的那一份"用户又双击了一次，出来露个面"。</summary>
    public static void SignalExistingToShow()
    {
        var hwnd = Win32.FindWindow(null, WindowTitle);
        if (hwnd == IntPtr.Zero) return;

        var data = new Win32.COPYDATASTRUCT
        {
            dwData = new IntPtr(ShowCommand),
            lpData = WindowTitle,
            cbData = WindowTitle.Length + 1,
        };
        _ = Win32.SendMessageTimeout(hwnd, Win32.WM_COPYDATA, IntPtr.Zero, ref data,
            0x0002 /* SMTO_ABORTIFHUNG */, 1000, out _);
    }

    /// <summary>在侧栏的消息通道上监听唤醒指令。</summary>
    public static void StartListening(Window window, Action onShow)
    {
        _onShow = onShow;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook(OnWindowMessage);
    }

    private static IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Win32.WM_COPYDATA) return IntPtr.Zero;
        handled = true;
        // 结构体是发送方摆好的内存，只在这一刻有效，读完就走
        var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.COPYDATASTRUCT>(lParam);
        if (data.dwData.ToInt32() == ShowCommand) _onShow?.Invoke();
        return IntPtr.Zero;
    }
}

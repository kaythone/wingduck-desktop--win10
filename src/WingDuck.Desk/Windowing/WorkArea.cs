using WingDuck.Desk.Interop;

namespace WingDuck.Desk.Windowing;

/// <summary>
/// 取"窗口所在那块屏幕的工作区"（屏幕减掉任务栏与停靠栏）。
/// 刻意不用 <c>System.Windows.SystemParameters.WorkArea</c>：它只有主屏，
/// 多屏时把侧栏吸附到副屏就会算错边（DESIGN §4 ADR-3）。
/// </summary>
public static class WorkArea
{
    /// <summary>设备像素。hwnd 还没建出来时传 <see cref="IntPtr.Zero"/>，取主屏。</summary>
    public static BoxD ForScreen(IntPtr hwnd)
    {
        var monitor = Win32.MonitorFromWindow(hwnd,
            hwnd == IntPtr.Zero ? Win32.MONITOR_DEFAULTTOPRIMARY : Win32.MONITOR_DEFAULTTONEAREST);

        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (Win32.GetMonitorInfo(monitor, ref info)) return Win32.ToBox(info.rcWork);

        // 拿不到就退化成主屏整屏：宁可少掉任务栏那一条，也不能返回 0x0 让窗口消失
        return new BoxD(0, 0, Win32.GetSystemMetrics(Win32.SM_CXSCREEN), Win32.GetSystemMetrics(Win32.SM_CYSCREEN));
    }

    /// <summary>该窗口的 DPI 缩放系数（1.0 / 1.25 / 1.5 …）。PerMonitorV2 下随所在屏幕变化。</summary>
    public static double DpiScaleFor(IntPtr hwnd)
    {
        var dpi = Win32.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }
}

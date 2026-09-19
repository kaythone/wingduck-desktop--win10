namespace WingDuck.Desk.Windowing;

/// <summary>吸附到的边。None = 悬浮在桌面中间，不贴任何边。</summary>
public enum Edge
{
    None,
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>条目排列方向。左右边只能竖排，上下边只能横排。</summary>
public enum LayoutAxis
{
    Vertical,
    Horizontal,
}

/// <summary>
/// 本项目所有几何值的单位是 <b>设备像素</b>（与 GetCursorPos / GetWindowRect 一致）。
/// 换算成 WPF 的 DIP 只发生在 DockWindow 一处，几何模块内部不出现 dpi。
/// </summary>
public readonly record struct PointD(double X, double Y);

/// <summary>设备像素矩形。X/Y 是左上角。</summary>
public readonly record struct BoxD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(PointD p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

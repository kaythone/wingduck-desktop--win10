namespace WingDuck.Desk.Windowing;

/// <summary>
/// 窗口八向把手（DESIGN §8.1 条目 2，v1.1）。侧栏是自绘无边框窗口（<c>WindowStyle=None</c> +
/// <c>AllowsTransparency</c>），系统那套可拖边框根本没有，所以命中测试与算新矩形都得自己做。
/// 纯函数：只吃矩形和指针，不碰 Win32，能被单测全覆盖。
/// </summary>
[Flags]
public enum Grip
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

public static class ResizeGrip
{
    /// <summary>
    /// 指针落在哪几根边框上。边框带在<b>矩形内侧</b>：窗口就这么大，外侧收不到鼠标消息。
    /// 角上同时命中两条边，于是两位一起置起来（拖角 = 同时改两边）。
    /// </summary>
    public static Grip HitTest(BoxD box, PointD p, double band)
    {
        if (band <= 0 || !box.Contains(p)) return Grip.None;
        var grip = Grip.None;
        if (p.X - box.Left <= band) grip |= Grip.Left;
        if (box.Right - p.X <= band) grip |= Grip.Right;
        if (p.Y - box.Top <= band) grip |= Grip.Top;
        if (box.Bottom - p.Y <= band) grip |= Grip.Bottom;
        return grip;
    }

    /// <summary>
    /// 贴了边的栏：靠屏幕那一侧的把手不算数。那里本来就贴着屏幕外沿，
    /// 让它能动的话，"拖一下就脱边"会变成人人误触的事故。
    /// </summary>
    public static Grip Docked(Grip hit, Edge edge) => edge switch
    {
        Edge.Left => hit & ~Grip.Left,
        Edge.Right => hit & ~Grip.Right,
        Edge.Top => hit & ~Grip.Top,
        Edge.Bottom => hit & ~Grip.Bottom,
        _ => hit,
    };

    /// <summary>
    /// 按把手方向重算矩形：没被抓住的那条边原地不动，抓住的边跟指针走，
    /// 结果既不小于 <paramref name="minWidth"/>/<paramref name="minHeight"/>，也不超出 <paramref name="limit"/>。
    /// </summary>
    public static BoxD Resize(BoxD start, Grip grip, PointD cursor, double minWidth, double minHeight, BoxD limit)
    {
        if (grip == Grip.None) return start;
        var left = start.Left;
        var top = start.Top;
        var right = start.Right;
        var bottom = start.Bottom;

        // 不用 Math.Clamp：起始矩形本身可能比 min 还窄（钉住态被夹到屏角过），那时 min > max 会直接抛异常。
        // 宁可让某一维临时越出工作区，也不能在拖拽中途把进程崩掉。
        if (grip.HasFlag(Grip.Left)) left = Math.Min(Math.Max(cursor.X, limit.Left), right - minWidth);
        if (grip.HasFlag(Grip.Right)) right = Math.Max(Math.Min(cursor.X, limit.Right), left + minWidth);
        if (grip.HasFlag(Grip.Top)) top = Math.Min(Math.Max(cursor.Y, limit.Top), bottom - minHeight);
        if (grip.HasFlag(Grip.Bottom)) bottom = Math.Max(Math.Min(cursor.Y, limit.Bottom), top + minHeight);

        return new BoxD(left, top, right - left, bottom - top);
    }
}

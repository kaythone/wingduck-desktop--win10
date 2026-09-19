namespace WingDuck.Desk.Windowing;

/// <summary>
/// 四边吸附的全部几何判定。纯函数：输入工作区/窗口矩形与指针位置，输出三档矩形。
/// 不读屏幕、不碰 Win32、不依赖 WPF，所以能被单元测试全覆盖（DESIGN §13.1）。
/// </summary>
public static class EdgeSnap
{
    /// <summary>
    /// 指针落在哪条边的吸附范围内。角落同时命中两条边时优先左右（DESIGN §8.2），
    /// 这个优先级由"先判左右"的检测顺序表达，不写 if 特例。
    /// </summary>
    public static Edge DetectEdge(BoxD work, PointD cursor, SnapOptions opt)
    {
        // 指针被甩到屏幕外（负坐标 / 超过分辨率）时先夹进工作区：
        // 不夹的话"往屏幕外一推"永远判不出边，也就没法靠贴边触发展开。
        var p = new PointD(
            Math.Clamp(cursor.X, work.Left, work.Right),
            Math.Clamp(cursor.Y, work.Top, work.Bottom));

        double dl = p.X - work.Left, dr = work.Right - p.X;
        double dt = p.Y - work.Top, db = work.Bottom - p.Y;

        if (Math.Min(dl, dr) <= opt.SnapThreshold) return dl <= dr ? Edge.Left : Edge.Right;
        if (Math.Min(dt, db) <= opt.SnapThreshold) return dt <= db ? Edge.Top : Edge.Bottom;
        return Edge.None;
    }

    /// <summary>
    /// 离哪条边最近。解除钉住用它（DESIGN §8.2 "按当前位置重算吸附边"）：
    /// 不能用 <see cref="DetectEdge"/>——展开态的栏中心离边有 40px，早过了 24px 的吸附阈值，那样判永远是 None。
    /// 角落同样优先左右，与 DetectEdge 一条口径。
    /// </summary>
    public static Edge NearestEdge(BoxD win, BoxD work)
    {
        double dl = win.Left - work.Left, dr = work.Right - win.Right;
        double dt = win.Top - work.Top, db = work.Bottom - win.Bottom;

        // 已经越过工作区边线的算 0：钉住态被夹回屏内时可能出现，此时就该贴那条边
        dl = Math.Max(0, dl); dr = Math.Max(0, dr); dt = Math.Max(0, dt); db = Math.Max(0, db);
        return Math.Min(dl, dr) <= Math.Min(dt, db)
            ? (dl <= dr ? Edge.Left : Edge.Right)
            : (dt <= db ? Edge.Top : Edge.Bottom);
    }

    /// <summary>
    /// 算出展开态 / 收起态 / 热区三个矩形。
    /// <paramref name="current"/> 只在调用方明确要求"保持原边重算"时传（T19 工作区变化）；
    /// 拖拽落点请传 <see cref="Edge.None"/>，否则悬浮到中间也解不开吸附。
    /// 未吸附时三个矩形都原样返回 <paramref name="win"/>。
    /// </summary>
    public static SnapResult Compute(BoxD win, BoxD work, PointD cursor, Edge current, SnapOptions opt)
    {
        var edge = DetectEdge(work, cursor, opt);
        if (edge == Edge.None)
        {
            if (current == Edge.None) return new SnapResult(Edge.None, LayoutAxis.Vertical, win, win, win);
            edge = current;
        }
        return ComputeFor(win, work, edge, opt);
    }

    /// <summary>
    /// 已知要贴哪条边时直接算三档矩形，不经过指针检测。
    /// 设置里手动指定边（T17）、以及托盘"显示"时回到上次的边都走这条。
    /// </summary>
    public static SnapResult ComputeFor(BoxD win, BoxD work, Edge edge, SnapOptions opt)
    {
        var vertical = edge is Edge.Left or Edge.Right;
        var thickness = DockMetrics.ThicknessFor(opt, vertical ? LayoutAxis.Vertical : LayoutAxis.Horizontal);
        return edge switch
        {
            Edge.Left => Vertical(win, work, opt, edge, thickness, work.Left),
            Edge.Right => Vertical(win, work, opt, edge, thickness, work.Right - thickness),
            Edge.Top => Horizontal(win, work, opt, edge, thickness, work.Top),
            Edge.Bottom => Horizontal(win, work, opt, edge, thickness, work.Bottom - thickness),
            Edge.None => new SnapResult(Edge.None, LayoutAxis.Vertical, win, win, win),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
    }

    private static SnapResult Vertical(BoxD win, BoxD work, SnapOptions opt, Edge edge, double thickness, double expandedX)
    {
        var height = Along(win.Height, work.Height);
        var y = Math.Clamp(win.Y, work.Top, work.Bottom - height);
        var collapsedX = edge == Edge.Left ? work.Left : work.Right - opt.CollapsedThickness;
        var hotX = edge == Edge.Left ? work.Left : work.Right - (opt.CollapsedThickness + opt.HotZone);
        return new SnapResult(
            edge,
            LayoutAxis.Vertical,
            new BoxD(expandedX, y, thickness, height),
            new BoxD(collapsedX, y, opt.CollapsedThickness, height),
            new BoxD(hotX, y, opt.CollapsedThickness + opt.HotZone, height));
    }

    private static SnapResult Horizontal(BoxD win, BoxD work, SnapOptions opt, Edge edge, double thickness, double expandedY)
    {
        var width = Along(win.Width, work.Width);
        var x = Math.Clamp(win.X, work.Left, work.Right - width);
        var collapsedY = edge == Edge.Top ? work.Top : work.Bottom - opt.CollapsedThickness;
        var hotY = edge == Edge.Top ? work.Top : work.Bottom - (opt.CollapsedThickness + opt.HotZone);
        return new SnapResult(
            edge,
            LayoutAxis.Horizontal,
            new BoxD(x, expandedY, width, thickness),
            new BoxD(x, collapsedY, width, opt.CollapsedThickness),
            new BoxD(x, hotY, width, opt.CollapsedThickness + opt.HotZone));
    }

    /// <summary>沿边方向的长度：不超过工作区，也不为 0（0 会让热区判定永远不命中）。</summary>
    private static double Along(double desired, double available)
        => Math.Clamp(desired, Math.Min(1, available), available);

    /// <summary>
    /// 还没定过位置的栏子该从哪儿起：<b>以工作区中点居中</b>（v1.2 条目 5a 的前半，用户原话
    /// "窗口上下左右边的吸附时都没有居中吸附"）。拖过的栏不走这里，它的位置由 <c>cards[].along</c> 记着。
    /// </summary>
    public static double CenteredAlong(BoxD work, LayoutAxis axis, double along) =>
        axis == LayoutAxis.Vertical
            ? work.Top + Math.Max(0, work.Height - along) / 2
            : work.Left + Math.Max(0, work.Width - along) / 2;
}

/// <summary>一次吸附的结果：贴哪条边、条目怎么排、三档矩形各是什么。</summary>
public readonly record struct SnapResult(Edge Edge, LayoutAxis Axis, BoxD Expanded, BoxD Collapsed, BoxD HotZone);

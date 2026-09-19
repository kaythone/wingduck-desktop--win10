namespace WingDuck.Desk.Windowing;

/// <summary>
/// 可调的吸附/悬停参数。这里的字段一律是 <b>DIP</b>（96dpi 基准，DESIGN §8 的原始数字），
/// 传给几何模块前先用 <see cref="DockMetrics.Scale"/> 乘上目标显示器的 dpi 缩放变成设备像素。
/// <c>ExpandedThickness</c> 传 0 表示"按排列方向自适应"，传 &gt;0 表示用户拖边框指定的覆盖值。
/// </summary>
public readonly record struct SnapOptions(
    double SnapThreshold,
    double CollapsedThickness,
    double ExpandedThickness,
    double HotZone,
    int CollapseDelayMs,
    int ExpandHoverMs,
    int RecollapseMs,
    /// <summary>dpi 缩放，只用于把自适应厚度从 DIP 换成设备像素；0/1 都表示不换算。</summary>
    double DpiScale = 1);

/// <summary>
/// 全部尺寸与延时常量的唯一出处（DESIGN §8、§12）。改数值只改这里，别在 UI 代码里写字面量。
/// </summary>
public static class DockMetrics
{
    // 几何（DIP）
    public const double SnapThreshold = 24;      // 距工作区边缘多近算吸附
    public const double CollapsedThickness = 8;  // 收起态细条厚度
    public const double HotZone = 6;             // 热区比细条多出的可命中宽度

    /// <summary>
    /// 拖边框改大小的命中带宽（DIP）。窗口是透明的，边框外侧收不到鼠标消息，所以这条带只能落在<b>内侧</b>；
    /// 比 1px 描边宽出几倍是故意的，不然没人能一把抓住它。
    /// </summary>
    public const double ResizeBand = 7;

    /// <summary>竖排（左右边）自适应厚度：交叉方向是宽度，条目 64 + 左右各 8。</summary>
    public const double ExpandedThickness = 80;

    /// <summary>
    /// 横排（上下边）自适应厚度：交叉方向是高度。
    /// <b>不能按"上留白 20 + 卡片 64 + 下留白 8 + 描边 2 = 94"算</b>——那是把卡片当 64 高，
    /// 实际条目容器还带 8px 底部间距、中文行高又比拉丁字母高出一截，94 会把名字横向切掉半行
    /// （用户 2026-09-17 实测，截图 <c>artifacts/m5-2026-09-17/shots/a1-top-expanded.png</c>）。
    /// <para>104 是逐行量出来的：124 那张标定图里文字底边落在第 94 行、下面空 29 行
    /// （<c>shots/a2-top-124.png</c>），94 + 留白 8 + 描边 2 = 104 刚好装下且不堆空白。
    /// 嫌挤还能拖边框自己加（条目 2 的覆盖值）。</para>
    /// </summary>
    public const double HorizontalExpandedThickness = 104;

    /// <summary>Shell 那一圈自绘描边的宽度，与 XAML 的 BorderThickness 同源；算可用空间时两头都要减。</summary>
    public const double ShellBorder = 1;

    /// <summary>拖边框改厚度的合法区间（DIP）。下限再小就放不下 48px 图标，上限纯属防止拖出屏幕。</summary>
    public const double MinThickness = 56;
    public const double MaxThickness = 240;

    public const double ItemSize = 64;
    public const double IconSize = 48;
    public const double ItemGap = 8;
    public const double AlongPaddingTop = 20;      // 让开顶部那条拖动热区
    public const double AlongPaddingBottom = 8;
    public const double MinAlongLength = 120;      // 空侧栏也留一段能拖住的范围
    public const double DragHandleHeight = 18;
    public const double CornerRadius = 10;
    public const double CornerRadiusCollapsed = 4;

    /// <summary>
    /// 沿边方向该开多长：按条目数撑，超出工作区时由 <see cref="EdgeSnap"/> 截断并交给滚轮。
    /// 80px 宽的侧栏塞不下系统滚动条（17px 会把 64px 的条目压扁），所以宁可让它长够。
    /// </summary>
    public static double ContentLength(int itemCount) => itemCount <= 0
        ? MinAlongLength
        : AlongPaddingTop + AlongPaddingBottom + itemCount * (ItemSize + ItemGap);

    // 时间（毫秒）
    public const int CollapseDelayMs = 400;   // 拖拽结束后延迟收起
    public const int ExpandHoverMs = 180;     // 指针停在热区多久才展开
    public const int RecollapseMs = 700;      // 指针离开多久后收回
    public const int AnimationMs = 140;       // 展开/收起动画时长
    public const int PollIntervalMs = 60;     // 收起态唯一的轮询定时器

    /// <summary>
    /// 启动后侧栏以展开态露面多久再收回。用户 2026-09-17 原话："侧栏不会自动弹出来显示一次，
    /// 需要我点击状态栏小图标"——静默进托盘等于他不承认这是"开机自启"。
    /// <para>计时从"这条栏建好"起算，不是从进程启动起算（进程起来到窗口出现本身要 ~1.0s）。
    /// 发布产物在隔离配置目录实测：1.25s 已展开、4.25s 收完，屏幕上看得见的露面就是约 3 秒
    /// （<c>artifacts/m5-2026-09-17/t41/boot-timeline.txt</c>）。想调露面时长只改这里，别去动冷启动。</para>
    /// </summary>
    public const int BootRevealMs = 3000;

    public static readonly SnapOptions Default = new(
        SnapThreshold, CollapsedThickness, 0, HotZone,
        CollapseDelayMs, ExpandHoverMs, RecollapseMs);

    /// <summary>自适应厚度：竖排与横排各一个数，出处就是上面那两个常量。</summary>
    public static double AutoThickness(LayoutAxis axis) =>
        axis == LayoutAxis.Horizontal ? HorizontalExpandedThickness : ExpandedThickness;

    /// <summary>
    /// 这次展开到底多厚：<paramref name="opt"/> 的 ExpandedThickness 为 0 时按方向自适应，
    /// 否则用用户拖边框定下的覆盖值（DESIGN §8.1，条目 2）。
    /// </summary>
    public static double ThicknessFor(SnapOptions opt, LayoutAxis axis) =>
        opt.ExpandedThickness > 0
            ? opt.ExpandedThickness
            : AutoThickness(axis) * (opt.DpiScale > 0 ? opt.DpiScale : 1);

    /// <summary>把拖出来的厚度夹进合法区间。</summary>
    public static double ClampThickness(double thickness) =>
        Math.Clamp(thickness, MinThickness, MaxThickness);

    /// <summary>
    /// 用户设置 + 某条栏的设置 → 吸附参数（T17 的接线处，T24/T27 之后按栏取覆盖值）。
    /// 吸附阈值与收起厚度仍不吃设置是刻意的：8/24px 一改，"贴边侧栏"就不是这个东西了。
    /// 能改的是手感快慢，以及每条栏自己的展开厚度与沿边长度（0 = 自适应）。
    /// </summary>
    public static SnapOptions ToOptions(WingDuck.Desk.Settings.AppSettings s,
        WingDuck.Desk.Settings.CardSettings? card = null) => Default with
    {
        CollapseDelayMs = s.CollapseDelayMs,
        ExpandHoverMs = s.ExpandHoverMs,
        RecollapseMs = s.RecollapseMs,
        ExpandedThickness = card?.Thickness ?? 0,
    };

    /// <summary>
    /// 沿边方向的长度：栏上拖过边框就用覆盖值，否则按条目数自适应。
    /// 与 <see cref="ContentLength"/> 一样是 DIP 口径。
    /// </summary>
    public static double AlongLengthFor(WingDuck.Desk.Settings.CardSettings? card, int itemCount)
    {
        if (card is not null && card.AlongLength > 0) return card.AlongLength;
        return ContentLength(itemCount);
    }

    /// <summary>把 DIP 口径的几何项换算成设备像素；时间项与缩放无关，原样带过。</summary>
    public static SnapOptions Scale(SnapOptions options, double dpiScale)
    {
        if (dpiScale <= 0) throw new ArgumentOutOfRangeException(nameof(dpiScale));
        return options with
        {
            SnapThreshold = options.SnapThreshold * dpiScale,
            CollapsedThickness = options.CollapsedThickness * dpiScale,
            ExpandedThickness = options.ExpandedThickness * dpiScale,
            HotZone = options.HotZone * dpiScale,
            DpiScale = dpiScale,
        };
    }
}

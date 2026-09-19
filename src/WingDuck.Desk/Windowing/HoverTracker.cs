namespace WingDuck.Desk.Windowing;

/// <summary>一次 tick 的结论：宿主据此决定要不要放动画。</summary>
public enum HoverIntent
{
    None,
    Wait,
    Expand,
    Recollapse,
}

/// <summary>
/// DESIGN §8.2 的悬停状态机，纯逻辑：时间戳比较，不建定时器、不读指针、不碰 WPF。
/// 宿主每 60ms 喂一次 <see cref="InHotZone"/> 再调 <see cref="Evaluate"/>。
/// 时钟注入是为了让 180/400/700ms 这三条延时能被单元测试"拨表"验证。
/// </summary>
public sealed class HoverTracker
{
    private readonly SnapOptions _opt;
    private readonly Func<long> _clock;

    private long _zoneSince = NoTimestamp;
    private long _leftAt = NoTimestamp;
    private long _settleUntil = NoTimestamp;
    private long _holdUntil = NoTimestamp;

    private const long NoTimestamp = -1;

    public HoverTracker(SnapOptions opt, Func<long> clock)
    {
        _opt = opt;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>指针是否落在热区（含 ±HotZone 的余量）。由宿主每 tick 更新。</summary>
    public bool InHotZone { get; set; }

    /// <summary>当前是否处于展开态。宿主拖拽、隐藏、手动切换后可直接改写。</summary>
    public bool Expanded { get; set; }

    public HoverIntent Intent { get; private set; }

    /// <summary>
    /// 声明"刚刚吸附落位、正处于展开态"：此时若指针不在热区，只等 <see cref="SnapOptions.CollapseDelayMs"/>
    /// 就收起，不等完整的 RecollapseMs（DESIGN §8.2 第二条）。
    /// </summary>
    public void BeginSettle() => _settleUntil = _clock() + _opt.CollapseDelayMs;

    /// <summary>
    /// 豁免期内不收回。给托盘左键用：侧栏被唤出来时指针还在托盘上，
    /// 不豁免的话 400ms 就被收走了，用户来不及看清有哪几条（DESIGN §10 托盘那行）。
    /// </summary>
    public void HoldExpanded(int ms) => _holdUntil = _clock() + Math.Max(0, ms);

    public HoverIntent Evaluate()
    {
        var now = _clock();
        if (InHotZone)
        {
            if (_zoneSince == NoTimestamp) _zoneSince = now;
            _leftAt = NoTimestamp;
            _settleUntil = NoTimestamp;   // 落位期间人回来了，收起计划作废
        }
        else
        {
            _zoneSince = NoTimestamp;
            if (_leftAt == NoTimestamp) _leftAt = now;
        }

        if (!Expanded)
        {
            if (_zoneSince == NoTimestamp) return Intent = HoverIntent.None;
            if (now - _zoneSince < _opt.ExpandHoverMs) return Intent = HoverIntent.Wait;
            Expanded = true;
            _leftAt = NoTimestamp;        // 展开后的"离开"从下一次真正离开开始算
            return Intent = HoverIntent.Expand;
        }

        if (InHotZone) return Intent = HoverIntent.None;

        if (_holdUntil != NoTimestamp)
        {
            if (now < _holdUntil) return Intent = HoverIntent.Wait;
            _holdUntil = NoTimestamp;     // 豁免期满，回到正常收回判定
        }

        var deadline = _settleUntil != NoTimestamp ? _settleUntil : _leftAt + _opt.RecollapseMs;
        if (now < deadline) return Intent = HoverIntent.Wait;
        Expanded = false;
        _settleUntil = NoTimestamp;
        return Intent = HoverIntent.Recollapse;
    }
}

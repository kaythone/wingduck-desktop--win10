using WingDuck.Desk.Settings;

namespace WingDuck.Desk.Windowing;

/// <summary>
/// 多条侧栏（卡片栏）的排布规则（DESIGN §7.2、§9 条目 8，v1.1）。
/// 全部 DIP 口径、纯函数：新栏该叫什么 id、该多大、该从哪儿起，都在这里定，
/// 窗口只负责把自己算好的矩形摆出来。
/// </summary>
public static class CardLayout
{
    /// <summary>新栏的 id = 现有最大 id 加一。原始那条永远是 0，不参与"最大"竞争。</summary>
    public static int NextId(IReadOnlyList<CardSettings> cards)
    {
        var max = 0;
        foreach (var card in cards) max = Math.Max(max, card.Id);
        return max + 1;
    }

    /// <summary>沿边方向实际占多长：拖过边框用覆盖值，否则按条目数自适应。</summary>
    public static double LengthOf(CardSettings card, int itemCount) =>
        DockMetrics.AlongLengthFor(card, itemCount);

    /// <summary>
    /// 从"上一条"生出新栏（DESIGN §9 条目 8 的原话：同尺寸、紧贴上一条）：
    /// 尺寸两项照抄，边照抄（不照抄就没法紧贴），钉住不照抄——钉住是"我就要停在这儿"的意思，
    /// 新栏刚出生就钉住会让用户以为它坏了。<c>along</c> 落在上一条的末端，超出工作区则夹回末端对齐。
    /// </summary>
    public static CardSettings Spawn(CardSettings prev, int prevItemCount, int newId, double workAlong)
    {
        var length = LengthOf(prev, prevItemCount);
        return new CardSettings
        {
            Id = newId,
            Edge = prev.Edge,
            Thickness = prev.Thickness,
            AlongLength = prev.AlongLength,
            Along = ClampAlong(prev.Along + length, length, workAlong),
        };
    }

    /// <summary>沿边起点夹进工作区：贴不下就与末端对齐（宁可重叠，也不让栏跑到屏外抓不到）。</summary>
    public static double ClampAlong(double along, double length, double workAlong)
    {
        var latest = Math.Max(0, workAlong - length);
        return Math.Clamp(along, 0, latest);
    }

    /// <summary>
    /// 删掉一条之后，把<b>仍然紧贴着它</b>的同边栏整体前移（FR-16 的"其余栏重排紧贴"）。
    /// 判据是"上一条的末端 == 这一条的起点"（容差 <see cref="TouchTolerance"/>）；一旦碰到用户自己
    /// 留出来的间隙就停手——那是他摆的布局，不许被我们的重排顺手抹掉。
    /// </summary>
    /// <param name="remaining">已经摘掉被删那条的列表。</param>
    /// <param name="from">被删那条原先所在位置（在 <paramref name="remaining"/> 的坐标系里）。</param>
    /// <param name="removedLength">被删那条沿边方向实际占多长。</param>
    public static IReadOnlyList<CardSettings> CloseGap(
        IReadOnlyList<CardSettings> remaining, CardSettings removed, double removedLength,
        Func<CardSettings, int> itemCount, int from)
    {
        var cards = remaining.ToList();
        var expected = removed.Along + removedLength;      // 删除前"紧贴"该落在哪儿
        for (var i = Math.Max(0, from); i < cards.Count; i++)
        {
            if (cards[i].Edge != removed.Edge) continue;
            if (Math.Abs(cards[i].Along - expected) > TouchTolerance) break;
            var length = LengthOf(cards[i], itemCount(cards[i]));
            cards[i] = cards[i] with { Along = Math.Max(0, cards[i].Along - removedLength) };
            expected += length;
        }
        return cards;
    }

    /// <summary>
    /// 算"紧贴"的容差（DIP）。沿边位置是从设备像素除以 dpi 缩放反推回来的，
    /// 125%/150% 下必然带小数，所以判等只能带容差，不能写 ==。
    /// </summary>
    public const double TouchTolerance = 2;
}

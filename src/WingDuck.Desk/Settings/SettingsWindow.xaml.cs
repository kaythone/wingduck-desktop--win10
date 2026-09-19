using System.Windows;
using System.Windows.Controls;
using WingDuck.Desk.Windowing;

namespace WingDuck.Desk.Settings;

/// <summary>
/// 设置面板（T17 Step 4）：拖动即生效，关窗才落盘——滑块一次拖动能出上百个事件，
/// 每个都写文件等于拿磁盘刷手感。
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] EdgeOrder = ["Left", "Right", "Top", "Bottom"];

    private readonly Func<AppSettings> _live;
    private readonly Action<AppSettings> _apply;
    private readonly Action<AppSettings> _persist;
    private bool _ready;

    /// <param name="live">取当前运行中的设置。刻意不传快照：面板开着的时候用户还能新增/删除侧栏，
    /// 攥着旧快照到关窗才落盘会把那些栏从设置里抹掉。</param>
    public SettingsWindow(Func<AppSettings> live, Action<AppSettings> apply, Action<AppSettings> persist)
    {
        _live = live;
        _apply = apply;
        _persist = persist;
        InitializeComponent();

        var current = live();
        // XAML 解到 Maximum 时 RangeBase 立刻回调 ValueChanged，那时后面的命名字段还没生成，
        // 所以事件在 _ready 之前一律当没发生过（见 OnChanged）。
        Veil.Value = current.VeilAlpha;
        CollapseDelay.Value = current.CollapseDelayMs;
        ExpandHover.Value = current.ExpandHoverMs;
        Recollapse.Value = current.RecollapseMs;
        EdgeBox.SelectedIndex = Array.IndexOf(EdgeOrder, Original(current).Edge);
        AutoStartBox.IsChecked = current.AutoStart;
        _ready = true;
        WriteLabels();

        Closed += (_, _) => _persist(Build());
    }

    /// <summary>设置面板只管原始那条栏的边；其余栏由各自的拖拽落位决定（DESIGN §7.2 v1.1）。</summary>
    private static CardSettings Original(AppSettings s) => s.Cards.Count > 0 ? s.Cards[0] : new CardSettings();

    private static IReadOnlyList<CardSettings> ReplaceOriginal(AppSettings s, CardSettings card)
    {
        var list = s.Cards.ToList();
        if (list.Count == 0) list.Add(card with { Id = 0 });
        else list[0] = list[0] with { Id = 0, Edge = card.Edge };
        return list;
    }

    /// <summary>托盘上把自启拧了，面板正开着也得跟着改勾——不然它关窗落盘时会把旧值写回去。</summary>
    public void SyncAutoStartBox(bool enabled)
    {
        _ready = false;
        AutoStartBox.IsChecked = enabled;
        _ready = true;
    }

    private void OnSlider(object sender, RoutedPropertyChangedEventArgs<double> e) => OnChanged(sender, e);

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        WriteLabels();
        _apply(Build());
    }

    private AppSettings Build()
    {
        var current = _live();
        var edge = EdgeBox.SelectedIndex >= 0 ? EdgeOrder[EdgeBox.SelectedIndex] : Original(current).Edge;
        return current with
        {
            VeilAlpha = (int)Veil.Value,
            CollapseDelayMs = (int)CollapseDelay.Value,
            ExpandHoverMs = (int)ExpandHover.Value,
            RecollapseMs = (int)Recollapse.Value,
            Cards = ReplaceOriginal(current, new CardSettings { Edge = edge }),
            AutoStart = AutoStartBox.IsChecked == true,
        };
    }

    private void WriteLabels()
    {
        VeilValue.Text = $" {(int)Veil.Value}";
        CollapseValue.Text = $" {(int)CollapseDelay.Value} ms";
        ExpandValue.Text = $" {(int)ExpandHover.Value} ms";
        RecollapseValue.Text = $" {(int)Recollapse.Value} ms";
    }
}

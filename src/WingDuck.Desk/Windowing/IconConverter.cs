using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WingDuck.Desk.Items;

namespace WingDuck.Desk.Windowing;

/// <summary>
/// 条目路径 → 48px 图标。缓存挂在转换器上，跟着窗口活一辈子。
/// 绑定而不是代码塞图，是因为 ItemsControl 复用容器时只有转换器会正确地重新求值。
/// </summary>
public sealed class IconConverter : IValueConverter
{
    public IconCache Cache { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string path ? Cache.Get(path) : Cache.Get(string.Empty);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

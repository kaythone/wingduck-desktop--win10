using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WingDuck.Desk.Windowing;

/// <summary>
/// 删除一条侧栏的确认框（DESIGN §9 条目 9 / FR-16，v1.1）。纯代码构建，同 <see cref="RenameWindow"/>。
///
/// <para>措辞是这条路径上唯一要紧的事：删的是<b>我们自己的配置文件</b>（<c>items-&lt;id&gt;.json</c>），
/// 里面只有快捷方式的路径引用。所以必须当场写明"磁盘上的原文件不动"，否则用户会以为点下去
/// 自己挑的那些快捷方式就没了——那是这个项目最不能让人误会的一件事。</para>
/// </summary>
public sealed class CardDeleteConfirm : Window
{
    public CardDeleteConfirm(int itemCount)
    {
        Title = "删除这条侧栏？";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x26, 0x2C));
        Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xF0, 0xF1));

        var ok = new Button
        {
            Content = "删除这条侧栏",
            IsDefault = true,
            MinWidth = 120,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 4, 10, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0x4D, 0x00)),
        };
        ok.Click += (_, _) => DialogResult = true;

        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            MinWidth = 84,
            Padding = new Thickness(10, 4, 10, 4),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = MessageFor(itemCount),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14),
        });
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => cancel.Focus();   // 默认按钮是"删除"，所以焦点先给"取消"，回车不该直接删
    }

    /// <summary>确认框正文。拆出来是为了让"必须写明原文件不动"这件事能被单测钉住。</summary>
    public static string MessageFor(int itemCount) =>
        itemCount <= 0
            ? "这条侧栏里还没有条目。删除只会去掉这条栏本身，磁盘上的原文件一个都不动。"
            : $"这条侧栏里的 {itemCount} 个快捷方式会一起消失。它们只是引用，磁盘上的原文件一个都不动。";

    /// <summary>原始那条（id=0）不给删——它是这个产品的本体，删了就没有侧栏可用了。</summary>
    public static bool Deletable(int cardId) => cardId != 0;
}

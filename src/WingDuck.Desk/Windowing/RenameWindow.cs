using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WingDuck.Desk.Windowing;

/// <summary>
/// 重命名条目的模态框。纯代码构建：为一个输入框开一份 XAML 不值当。
/// 只改 <c>items.json</c> 里的显示名，不碰磁盘上的文件名（DESIGN §9）。
/// </summary>
public sealed class RenameWindow : Window
{
    private readonly TextBox _input;

    public RenameWindow(string currentName)
    {
        Title = "重命名";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x26, 0x2C));
        Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xF0, 0xF1));

        _input = new TextBox
        {
            Text = currentName,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 14,
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xF7)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x26, 0x2C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0x8C, 0x8D)),
            BorderThickness = new Thickness(1),
        };

        var error = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 10),
            MinHeight = 16,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0x4D, 0x00)),
        };

        var ok = new Button
        {
            Content = "确定",
            IsDefault = true,
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 4, 10, 4),
        };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_input.Text))
            {
                error.Text = "名字不能为空";
                _input.Focus();
                return;
            }
            DialogResult = true;
        };

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
        root.Children.Add(new TextBlock { Text = "侧栏里显示的名字", Margin = new Thickness(0, 0, 0, 6), FontSize = 12 });
        root.Children.Add(_input);
        root.Children.Add(error);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    public string Value => _input.Text.Trim();
}

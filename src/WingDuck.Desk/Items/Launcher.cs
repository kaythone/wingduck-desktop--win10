using System.Diagnostics;
using System.Reflection;

namespace WingDuck.Desk.Items;

/// <summary>启动结果。失败带一句能直接显示给用户的话，UI 层不接异常（DESIGN §10）。</summary>
public sealed record LaunchResult(bool Ok, string? Error)
{
    public static LaunchResult Success { get; } = new(true, null);

    public static LaunchResult Failure(string error) => new(false, error);
}

/// <summary>
/// 点击语义（DESIGN §9.1）。要点：<b>.lnk 必须走 ShellExecute</b>，让 shell 自己去解析目标与
/// 起始位置；只有文件夹才交给 explorer.exe。
/// </summary>
public static class Launcher
{
    /// <summary>
    /// 拼 <c>explorer.exe</c> 的定位参数。<c>/select,</c> 的逗号是协议的一部分，
    /// 路径里的空格与逗号都原样带着——引号交给 <c>ArgumentList</c> 处理，我们不加。
    /// </summary>
    public static string RevealArgs(string path) => "/select," + path;

    public static LaunchResult Open(DockItem item)
    {
        try
        {
            if (item.Kind == ItemKind.Folder)
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { item.Path } });
            else
                Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true });
            return LaunchResult.Success;
        }
        catch (Exception ex)
        {
            return LaunchResult.Failure(ex.Message);
        }
    }

    public static LaunchResult Reveal(DockItem item)
    {
        var target = item.Kind == ItemKind.Shortcut ? ResolveShortcutTarget(item.Path) : null;
        var path = target ?? item.Path;
        if (!File.Exists(path) && !Directory.Exists(path))
            return LaunchResult.Failure($"要定位的路径已经不在了：{path}");

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { RevealArgs(path) } });
            return LaunchResult.Success;
        }
        catch (Exception ex)
        {
            return LaunchResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// 解析快捷方式指向的目标。走 WScript.Shell 后期绑定（DESIGN §3.2 实测可用），
    /// 不引入 ShellLink 之类的第三方包。目标已不存在时返回 null。
    /// </summary>
    public static string? ResolveShortcutTarget(string lnkPath)
    {
        if (string.IsNullOrEmpty(lnkPath)
            || !lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(lnkPath))
            return null;

        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            var shell = type is null ? null : Activator.CreateInstance(type);
            if (shell is null) return null;

            var shortcut = type!.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { lnkPath });
            if (shortcut is null) return null;

            var target = shortcut.GetType()
                .InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            return !string.IsNullOrEmpty(target) && File.Exists(target) ? target : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

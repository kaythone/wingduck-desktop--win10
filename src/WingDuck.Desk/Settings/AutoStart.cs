using Microsoft.Win32;
using WingDuck.Desk.Shell;

namespace WingDuck.Desk.Settings;

/// <summary>对 <c>HKCU\...\Run</c> 该动哪一手。<see cref="Write"/> 既管"第一次开"，也管"路径变了要改"。</summary>
public enum AutoStartChange
{
    None,
    Write,
    Remove,
}

/// <summary>
/// 开机自启（T18，T28 改路径口径）。只写 <c>HKCU\...\Run</c>，值名 <c>wingduck-desktop</c>，
/// <b>绝不碰 HKLM</b>——那要管理员，而本产品一旦以管理员运行，桌面拖放会被 UIPI 静默拦掉（DESIGN §10）。
///
/// <para>v1.1 的两条改动：</para>
/// <para>① 路径优先指向<b>装机版</b>（<c>%LOCALAPPDATA%\Programs\wingduck-desktop\</c>），
/// 因为便携目录随时会被挪走，指过去等于埋一个"重启后侧栏不出现"的雷；装机版不存在（开发期、纯便携用法）才回落到正在跑的这个 exe。</para>
/// <para>② 同步的判据从"值存在不存在"改成"<b>值和期望值一不一致</b>"。老写法在装过旧版本、
/// 或 exe 换过位置之后会认为"已经开着自启了"而什么都不写，于是自启指向一个空路径——这正是①要防的那种雷。</para>
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "wingduck-desktop";

    /// <summary>该用哪个 exe：装机版存在就用它，否则用当前正在跑的这个。</summary>
    public static string ChooseExe(string? installedExe, string? runningExe, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(installedExe) && exists(installedExe)) return installedExe;
        if (!string.IsNullOrWhiteSpace(runningExe)) return runningExe;
        return AppContext.BaseDirectory.TrimEnd('\\', '/') + "\\wingduck-desktop.exe";
    }

    /// <summary>
    /// 自启命令<b>不带</b>参数。v1.0 曾带 <c>--hidden</c> 静默进托盘，用户 2026-09-17 重启后实测否决：
    /// 侧栏完全不出现，他必须先点托盘图标才知道程序在不在——开机露面这件事不能靠他主动问。
    /// </summary>
    public static string CommandFor(string exe) => $"\"{exe}\"";

    /// <summary>自启该写入的完整值。</summary>
    public static string ExpectedCommand() => CommandFor(ChooseExe(AppPaths.InstalledExe, Environment.ProcessPath, File.Exists));

    /// <summary>
    /// 纯决策，不碰注册表：<paramref name="current"/> 是注册表里现有的值（没有则为 null）。
    /// </summary>
    public static AutoStartChange Decide(string? current, string expected, bool want)
    {
        if (want) return current == expected ? AutoStartChange.None : AutoStartChange.Write;
        return current is null ? AutoStartChange.None : AutoStartChange.Remove;
    }

    public static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>按 <paramref name="enabled"/> 把 Run 键拧到期望状态；已经对了就不动磁盘。</summary>
    public static void Sync(bool enabled, Action<string>? log = null)
    {
        var expected = ExpectedCommand();
        var change = Decide(Read(), expected, enabled);
        if (change == AutoStartChange.None) return;

        if (change == AutoStartChange.Write)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
                key.SetValue(ValueName, expected, RegistryValueKind.String);
            log?.Invoke($"自启已写入：{expected}");
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            log?.Invoke("自启已关闭（Run 键里的值已删）");
        }
    }
}

namespace WingDuck.Desk.Shell;

/// <summary>
/// 落盘位置集中一处（DESIGN §7.1）：默认 <c>%APPDATA%\wingduck-desktop\</c>。
/// 装的全是路径引用与配置，没有任何用户文件被搬进这里。
/// </summary>
public static class AppPaths
{
    public const string ProductFolderName = "wingduck-desktop";

    /// <summary>覆盖配置目录的环境变量名。存在的意义只有一个：让 agent 能起一份隔离实例做取证，而不碰用户真清单。</summary>
    public const string ConfigDirEnvVar = "WINGDUCK_CONFIG_DIR";

    public static string Folder { get; } = Resolve();

    private static string Resolve()
    {
        var over = Environment.GetEnvironmentVariable(ConfigDirEnvVar);
        if (!string.IsNullOrWhiteSpace(over)) return Path.GetFullPath(over.Trim());
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductFolderName);
    }

    /// <summary>是否被环境变量改到了别处（改过就不是用户那份，日志与窗口标题都要留痕）。</summary>
    public static bool IsOverridden { get; } =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConfigDirEnvVar));

    /// <summary>原始那条侧栏的清单（v1.0 起就是这个文件名，用户的条目不许被迁移折腾）。</summary>
    public static string Items => Path.Combine(Folder, "items.json");

    /// <summary>DESIGN §7.1：第 0 条用 <c>items.json</c>，第 N 条用 <c>items-N.json</c>。</summary>
    public static string ItemsFor(int cardId) => cardId == 0
        ? Items
        : Path.Combine(Folder, $"items-{cardId}.json");

    public static string Settings => Path.Combine(Folder, "settings.json");

    public static string Log => Path.Combine(Folder, "log.txt");

    /// <summary>装机版路径（DESIGN §11）。自启键优先写它——用户第 6 条。</summary>
    public static string InstalledExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Programs\wingduck-desktop\wingduck-desktop.exe");

    public static void EnsureFolder() => Directory.CreateDirectory(Folder);
}

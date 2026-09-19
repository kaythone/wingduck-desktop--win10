using WingDuck.Desk.Shell;

namespace WingDuck.Desk;

/// <summary>
/// 落盘到 <c>%APPDATA%\wingduck-desktop\log.txt</c> 的极简日志（DESIGN §10）。
/// 这个程序没有控制台，出错时如果只往屏幕上闪一下就没人能看到第二次；
/// 而且"有没有动过用户文件"这件事需要一份可核查的时间线。
/// 写日志本身失败绝不能再把程序带崩，所以所有 IO 异常一律吞掉。
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 500 * 1024;
    private static readonly object Gate = new();
    private static bool _folderReady;

    public static void Line(string message)
    {
        try
        {
            lock (Gate)
            {
                if (!_folderReady)
                {
                    Directory.CreateDirectory(AppPaths.Folder);
                    _folderReady = true;
                }
                RotateIfNeeded();
                File.AppendAllText(AppPaths.Log,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Failure(string where, Exception ex)
        => Line($"!! {where}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>超上限就改名为 log.txt.1 另起一份，只留一代，不无限涨。</summary>
    private static void RotateIfNeeded()
    {
        if (!File.Exists(AppPaths.Log)) return;
        var info = new FileInfo(AppPaths.Log);
        if (info.Length < MaxBytes) return;
        var backup = AppPaths.Log + ".1";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(AppPaths.Log, backup);
    }
}

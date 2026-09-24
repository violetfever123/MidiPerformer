using System.Diagnostics;

namespace MidiPerformer.App.Logging;

/// <summary>
/// 日志落盘的**唯一一处**：目录在哪儿、旧的要怎么清、怎么把它打开给用户看。
///
/// <b>全程序只有这里拼 <c>%LOCALAPPDATA%</c>。</b> 61 号票引入它时全仓一处都没用过这个位置，
/// 所以「别在别处再猜路径」是这一节的头一条规矩 —— 谁再拼一次，两处迟早对不上，
/// 而对不上是看不出来的（只是某些日志落在了另一个文件夹里）。
///
/// 这里全是纯文件系统的事：给它一个目录（测试塞临时目录），它不猜、不问、不碰网络。
/// </summary>
public static class LogFolder
{
    /// <summary>留几天。<b>正好这么多天的留着</b> —— 见 <see cref="Prune"/> 的边界。</summary>
    public const int KeepDays = 7;

    /// <summary>最多留几份。<b>正好这么多份一份都不删</b> —— 见 <see cref="Prune"/> 的边界。</summary>
    public const int MaxFiles = 20;

    /// <summary>本程序自己写的日志文件长这样。清理只认这个名字的文件 —— 邻居的文件一个都不动。</summary>
    public const string FilePrefix = "MidiPerformer-";

    public const string FileSuffix = ".log";

    /// <summary>
    /// <c>%LOCALAPPDATA%\MidiPerformer\logs\</c>。
    /// 用 <see cref="Environment.SpecialFolder.LocalApplicationData"/> 而不是拼 <c>"LOCALAPPDATA"</c> 环境变量：
    /// 后者在被裁剪掉环境块的环境里是空串，那样拼出来的是相对路径 —— 日志会静默落到当前工作目录。
    /// </summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MidiPerformer",
        "logs");

    /// <summary>
    /// 把目录建出来（已经在就什么都不做），返回它。
    /// <b>不存在时自建、不抛</b>：调用方要的是一条能用的路径，不是一次异常。
    /// </summary>
    public static string EnsureExists(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// 清一次旧日志，返回删掉的那些路径。两条规矩：<b>超过 <paramref name="keepDays"/> 天的删掉</b>，
    /// 剩下的<b>只留最新的 <paramref name="maxFiles"/> 份</b>。
    ///
    /// 两条边界都是「不删」那一侧，因为「留 7 天」「最多 20 份」是**下限式承诺**：
    /// 正好 7 天的还在「留 7 天」里，正好 20 份还在「最多 20 份」里。多删一份用户就少一份线索，
    /// 而少一份是看不出来的。
    ///
    /// 目录不在、某个文件删不掉（被另一个实例占着、权限不够）—— 一律不算错。
    /// 清理是**尽力而为**：它跑在启动路径上，抛一次就是程序起不来。
    /// </summary>
    public static IReadOnlyList<string> Prune(
        string directory, DateTimeOffset now, int keepDays = KeepDays, int maxFiles = MaxFiles)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(directory)) return deleted;

        var files = new DirectoryInfo(directory)
            .GetFiles(FilePrefix + "*" + FileSuffix, SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        // ① 年龄：老的先走。正好 keepDays 天的落在 else 那一支 = 留着
        var survivors = new List<FileInfo>(files.Count);
        var oldestAllowed = TimeSpan.FromDays(keepDays);
        foreach (var f in files)
        {
            if (now.UtcDateTime - f.LastWriteTimeUtc > oldestAllowed)
                Delete(f, deleted);
            else
                survivors.Add(f);
        }

        // ② 份数：survivors 已经是从新到旧，跳过前 maxFiles 份，剩下的都是多的
        foreach (var f in survivors.Skip(maxFiles))
            Delete(f, deleted);

        return deleted;
    }

    private static void Delete(FileInfo file, List<string> deleted)
    {
        try
        {
            file.Delete();
            deleted.Add(file.FullName);
        }
        catch (IOException)
        {
            // 正被另一个实例占着写字 —— 下一次启动再清
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 在资源管理器里打开这个目录。目录不在就先建出来 ——
    /// 「日志还没写出来时点它」不能是一条死路（61 号票 E 组）。
    /// </summary>
    public static void Open(string directory)
    {
        EnsureExists(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }
}

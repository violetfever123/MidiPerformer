using System.Reflection;
using Microsoft.Extensions.Logging;

namespace MidiPerformer.App.Logging;

/// <summary>
/// 日志的组装点：清旧的、把 provider 挂上、写启动那一行。**在 <c>App.axaml.cs</c> 的产品分支里调一次。**
///
/// 拿到的那个 <see cref="ILoggerFactory"/> 由组装点往下递（构造器注入），别处不自己 new：
/// <list type="bullet">
///   <item>主窗口拿 <c>CreateLogger("MidiPerformer.App.Views.MainWindow")</c>；</item>
///   <item>后面几票（62 号要把「打开一首曲子花了多少毫秒」写进来）同样从组装点递 —— 这就是这里
///         返回工厂而不是返回一个现成 logger 的原因。</item>
/// </list>
///
/// <b>这个类不联网、不上传。</b> 61 号票里没有任何一行网络代码，这个文件也不例外：
/// 它只会建目录、删自己写的旧文件、往里追加字。
/// </summary>
public static class LoggingSetup
{
    /// <summary>
    /// 程序自己的版本号。写在启动那一行里 —— 事后翻日志要能对上「这是哪一版跑出来的」。
    /// 取的是信息版本（SourceLink 会往后面缀上 commit），拿不到就退回程序集版本。
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(LoggingSetup).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "未知";
    }

    /// <summary>
    /// 起日志：先清一次旧的，再把工厂建好，最后写启动那一行。
    /// <b>怎么都返回一个能用的工厂</b> —— 日志起不来不该挡住程序启动，
    /// 那是「写失败只是没日志」的同一句话（写不进去就当没有，程序照跑）。
    /// </summary>
    /// <param name="now">「现在」，只为清理的年龄判据。测试塞固定值。</param>
    /// <param name="directory">日志目录，默认 <see cref="LogFolder.DefaultDirectory"/>。</param>
    public static ILoggerFactory Start(DateTimeOffset now, string? directory = null)
    {
        string dir = directory ?? LogFolder.DefaultDirectory;

        int pruned = 0;
        try
        {
            LogFolder.EnsureExists(dir);
            pruned = LogFolder.Prune(dir, now).Count;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(dir));
        });

        var log = factory.CreateLogger("MidiPerformer.App");
        log.LogInformation("启动 MidiPerformer {版本}", Version);
        log.LogInformation("日志目录 {目录}", dir);
        if (pruned > 0)
            log.LogInformation("启动清理：删掉 {份数} 份过期日志", pruned);

        return factory;
    }
}

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MidiPerformer.App.Logging;

/// <summary>
/// 往一个文件里追加的 <see cref="ILoggerProvider"/>。自己写的，不引 Serilog / NLog
/// （用户明确要求「不引第三方日志库」）—— 所以这一层只许用 BCL 和
/// <c>Microsoft.Extensions.Logging</c> 的抽象。
///
/// <b>一行日志长这样，没有第五个字段位：</b>
/// <code>
/// 2026-09-24 15:30:12.345 [Information] MidiPerformer.App.Views.MainWindow: 已另存到 C:\曲库\勾指起誓.mid
/// </code>
/// 时间 · 级别 · 类别 · 正文，异常栈另起、缩进四格。就这四样。
///
/// <b>🔴 这不是排版偏好，是 61 号票的红线。</b> 这个程序是往游戏里发按键的，
/// 于是「哪个键、什么时候按的」+「开着哪个游戏窗口」+「哪个进程」三样凑起来
/// 就是一份「用户在玩什么、什么时候玩、手速多快」的记录。**日志里不许出现那三样。**
/// 所以：
/// <list type="bullet">
///   <item>这个类**不去问**任何环境事实 —— 不取进程名、不取机器名、不取用户名、不取线程号、
///         不取前台窗口。它连 <see cref="System.Diagnostics.Process"/> 都不碰。行格式里也就没有位置放它们。</item>
///   <item><see cref="FileLogger.BeginScope{TState}"/> **故意返回 null**：scope 是「随手带上一点环境上下文」
///         的现成入口，这里不接。以后谁想加 scope，得先想清楚要带的是什么。</item>
///   <item>要记的是**程序自己的事**：哪一步失败了、异常栈、版本号、耗时。</item>
/// </list>
///
/// 写失败一律咽掉：**写失败只是没日志，写大了是另一种性质的事** ——
/// 反过来也一样，写日志这件事绝不能把程序带崩。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    /// <summary>这一次运行写哪一个文件。第一次真要写的时候才定下来 —— 想文件名用哪一刻的时间。</summary>
    private string? _path;

    /// <param name="directory">日志目录。不存在会在第一次写入时建出来。</param>
    /// <param name="now">取「现在」的入口，只为文件名与行首时间戳。测试塞固定值。</param>
    public FileLoggerProvider(string directory, Func<DateTimeOffset>? now = null)
    {
        _directory = directory;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        // 每一行都是「开 → 追加 → 关」，没有留在缓冲区里没落盘的东西，所以这里没有要收的尾。
        // 留着这个方法是给以后真加了缓冲的时候有个明确的位置。
    }

    /// <summary>这个 provider 这次运行落在哪个文件上。还没写过任何一行时是 <c>null</c>。</summary>
    public string? CurrentPath => _path;

    internal void Append(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                _path ??= PickPath();
                File.AppendAllText(_path, line + Environment.NewLine, Utf8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
    }

    /// <summary>同一秒里开两次就撞名了，加个序号让后一份另起一个文件 —— 两份日志不许混进同一个文件。</summary>
    private string PickPath()
    {
        string stamp = _now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string candidate = Path.Combine(_directory, LogFolder.FilePrefix + stamp + LogFolder.FileSuffix);
        for (int n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(_directory, $"{LogFolder.FilePrefix}{stamp}-{n}{LogFolder.FileSuffix}");
        return candidate;
    }

    /// <summary>
    /// 一条日志（含异常栈）拼成落盘的那几行。异常的每一行都缩进四格 ——
    /// 于是「文件里每一行，要么是时间戳开头，要么是四个空格开头」成立，
    /// 红线那条测试就拿这条格式当锁：**行里长不出第五个字段位**。
    /// </summary>
    internal static string Format(
        DateTimeOffset now, LogLevel level, string category, string message, Exception? exception)
    {
        var lines = new List<string>
        {
            string.Concat(
                now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                " [", level.ToString(), "] ",
                category, ": ", message),
        };

        if (exception is not null)
        {
            foreach (string l in exception.ToString().Split('\n'))
                lines.Add("    " + l.TrimEnd('\r'));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>一条日志。类别名由 <see cref="CreateLogger"/> 记着，正文由 formatter 现拼。</summary>
    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        internal FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        /// <summary>
        /// <c>Information</c> 起才写。<c>Trace</c> / <c>Debug</c> 不落盘：
        /// 这个日志是给用户「出事之后翻一翻」的，不是给开发时看的 ——
        /// 塞满了真正的线索就找不到了。
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        /// <summary>
        /// 不接。见 <see cref="FileLoggerProvider"/> 那段红线：scope 正是「随手捎上一点环境」的口子，
        /// 这个日志的立场是**只写程序自己说出来的话**。
        /// </summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            _provider.Append(Format(
                _provider._now(), logLevel, _category, formatter(state, exception), exception));
        }
    }
}

using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace MidiPerformer.Tests.Repertoire;

/// <summary>
/// 移植的逐行保真度检查：把 <c>Core/UseCases/Perform/Repertoire/</c> 里的文件与原版源码文本比。
/// 判据是子序列 —— 我们的每一行代码（剥掉文档注释、命名空间、using）都必须按原顺序出现在原版文件里；
/// 整文件复制的那两个文件额外查行数相等（删行在那里会被抓住）。对拍管行为等价，这里管文本一致。
/// </summary>
public class PortFidelityTests
{
    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    /// <summary>MidiPerformer 仓库根。</summary>
    private static string RepoRoot => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", ".."));

    private static string OurRepertoire => Path.Combine(
        RepoRoot, "MidiPerformer.Core", "UseCases", "Perform", "Repertoire");

    /// <summary>原版仓库。</summary>
    private static string OriginalProject => Path.GetFullPath(
        Path.Combine(RepoRoot, "..", "harmonica-auto-player", "HarpAutoPlayer"));

    /// <summary>抽取式移植里，属于我们自己的那几行（不是从原版搬来的）。</summary>
    private static readonly string[] OurOwnLines =
    {
        "public sealed class EventBuilder",
    };

    public static IEnumerable<TestCaseData> PortedFiles()
    {
        // 整文件复制：只差命名空间（NoteMapper 另有两行键位指向 PlayKeys）
        yield return new TestCaseData("InputTiming.cs", "Engine/InputTiming.cs", true);
        yield return new TestCaseData("NoteMapper.cs", "Engine/NoteMapper.cs", true);
        // 抽取：Music.cs 取自 MidiModels.cs 的 RawNote + Music 两块
        yield return new TestCaseData("Music.cs", "Midi/MidiModels.cs", false);
        // 抽取：EventBuilder.cs 取自 PlaybackEngine.cs 的 BuildSchedule + 三个伴生类型
        yield return new TestCaseData("EventBuilder.cs", "Engine/PlaybackEngine.cs", false);
        // PlayKeys.cs 是我们自己的文件，不在此列
    }

    [TestCaseSource(nameof(PortedFiles))]
    public void 移植文件逐行来自原版(string ourFileName, string originalRelativePath, bool isWholeFileCopy)
    {
        var originalPath = Path.Combine(OriginalProject, originalRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var ourPath = Path.Combine(OurRepertoire, ourFileName);

        Assert.That(File.Exists(originalPath), Is.True,
            $"原版文件不存在：{originalPath}（原版仓库应与 MidiPerformer 并排）");
        Assert.That(File.Exists(ourPath), Is.True, $"移植文件不存在：{ourPath}");

        var originalLines = CodeLines(originalPath);
        var ourLines = CodeLines(ourPath, OurOwnLines);

        int o = 0;
        foreach (var line in ourLines)
        {
            while (o < originalLines.Count && originalLines[o] != line) o++;
            Assert.That(o, Is.LessThan(originalLines.Count),
                $"{ourFileName}：这一行在原版 {originalRelativePath} 里找不到，或次序不对\n    {line}");
            o++;
        }

        if (isWholeFileCopy)
        {
            Assert.That(ourLines.Count, Is.EqualTo(originalLines.Count),
                $"{ourFileName}：整文件复制，行数必须一致（少了说明有行被删）");
        }
    }

    /// <summary>
    /// 剥掉三类行 —— 文档注释、<c>namespace</c>、<c>using</c> —— 再把有意做的改动
    /// 反向归一化回原版形态，剩下的就是必须逐行相同的代码。
    /// </summary>
    private static List<string> CodeLines(string path, params string[] extraSkip)
    {
        var lines = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("///", StringComparison.Ordinal)) continue;
            if (line.StartsWith("namespace ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("using ", StringComparison.Ordinal)) continue;
            if (extraSkip.Contains(line)) continue;
            lines.Add(Normalize(line));
        }
        return lines;
    }

    /// <summary>移植时对原版做的全部有意改动，逐条列在这里。</summary>
    private static string Normalize(string line) => line
        // 1. 三个伴生类型从"引擎私有"变成"对派发方可见"
        .Replace("public sealed class PhysicalEvent", "private sealed class PhysicalEvent")
        .Replace("public const int K_", "private const int K_")
        .Replace("public readonly record struct ModState", "private readonly record struct ModState")
        // 2. 方法名与可见性：BuildSchedule → Build
        .Replace("public (List<PhysicalEvent>, double) Build(",
                 "private (List<PhysicalEvent>, double) BuildSchedule(")
        // 3. 键位表被抽到 PlayKeys.cs，这两行改为指向它
        .Replace("public static readonly char[] Keys = PlayKeys.Keys;",
                 "public static readonly char[] Keys = { 'Z', 'X', 'C', 'V', 'B', 'N', 'M' };")
        .Replace("public const char TopKey = PlayKeys.TopKey;",
                 "public const char TopKey = ',';");
    // 原版 BuildSchedule 里的 #if HARP_TEST / #endif 两行没有搬（TraceSink 改为常开），
    // 只出现在原版一侧，子序列判据不受影响。
}

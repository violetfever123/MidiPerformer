using System.Runtime.CompilerServices;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NUnit.Framework;

namespace MidiPerformer.Tests.Corpus;

/// <summary>
/// 真实 MIDI 语料 —— 用的是并排的 <c>drywetmidi</c> 仓库里那批 <c>Resources/MIDI files/Valid</c>。
///
/// 为什么借它：那批文件是 DryWetMidi 自己的测试语料，**格式 0 / 1 / 2 齐全、分辨率从 30 到 24576
/// 都有、63 首里有 23 首带变速**，而且都是真歌（不是合成出来的规整样本）。
/// 自己攒一份同样覆盖面的语料比借这份贵得多。
///
/// 依赖是**并排目录**，和 <c>PortFidelityTests</c> 依赖原版 <c>harmonica-auto-player</c> 是同一种做法：
/// 找不到就直接红，不静默跳过 —— 一条永远绿的测试比没有测试更坏。
/// </summary>
internal static class MidiCorpus
{
    private static string ThisFileDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    /// <summary>MidiPerformer 仓库根。</summary>
    public static string RepoRoot => Path.GetFullPath(Path.Combine(ThisFileDir(), "..", ".."));

    /// <summary>并排的 DryWetMidi 仓库里的有效 MIDI 目录。</summary>
    public static string ValidDir => Path.GetFullPath(
        Path.Combine(RepoRoot, "..", "drywetmidi", "Resources", "MIDI files", "Valid"));

    /// <summary>全部语料文件，按路径排序（顺序稳定，失败信息可复现）。</summary>
    public static IReadOnlyList<string> Files { get; } = Load();

    private static IReadOnlyList<string> Load()
    {
        if (!Directory.Exists(ValidDir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(ValidDir, "*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>NUnit 的语料源。用例名用相对路径，红了能一眼看出是哪首。</summary>
    public static IEnumerable<TestCaseData> TestFiles()
    {
        foreach (var f in Files)
            yield return new TestCaseData(f).SetName($"语料/{Path.GetFileName(f)}");
    }

    /// <summary>语料目录缺失时，把话说明白。</summary>
    public static void AssertCorpusPresent()
    {
        Assert.That(Directory.Exists(ValidDir), Is.True,
            $"MIDI 语料目录不存在：{ValidDir}\n" +
            "（drywetmidi 仓库应与 MidiPerformer 并排）");
        Assert.That(Files, Is.Not.Empty, $"语料目录里一个 .mid 都没有：{ValidDir}");
    }

    /// <summary>语料里带变速的文件（速度事件不止一个值）。</summary>
    public static IEnumerable<TestCaseData> VariableTempoFiles()
    {
        foreach (var f in VariableTempoPaths)
            yield return new TestCaseData(f).SetName($"变速语料/{Path.GetFileName(f)}");
    }

    /// <summary>带变速的语料文件路径。给「在几首变速曲上扫一遍」这类测试用。</summary>
    public static IReadOnlyList<string> VariableTempoPaths { get; } =
        Files.Where(HasVariableTempo).ToArray();

    private static bool HasVariableTempo(string path)
    {
        try
        {
            var map = MidiFile.Read(path).GetTempoMap();
            return map.GetTempoChanges()
                .Select(c => c.Value.MicrosecondsPerQuarterNote)
                .Distinct()
                .Count() > 1;
        }
        catch
        {
            return false;
        }
    }
}

using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Tests.Corpus;
using NUnit.Framework;
using Original = HarpAutoPlayer.Engine;
using OriginalMidi = HarpAutoPlayer.Midi;
using Ported = MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Tests.Repertoire;

/// <summary>
/// 全链对拍 —— 同一个 <c>.mid</c> 文件进两边，事件表逐条相等。
///
/// 原版（<c>harmonica-auto-player@a14335c</c>，ProjectReference 引工程本体，跑的不是副本）：
/// <code>
///   MidiLoader.Parse → 自己的 DryWetMidi 读取 → 自己的 tick→秒 → NoteMapper.Map → BuildSchedulePreview
/// </code>
/// 我们：
/// <code>
///   SongProject.Read → tick 模型 → TempoMap → RepertoireToSeconds → NoteMapper.Map → EventBuilder.Build
/// </code>
///
/// **后半段（映射 + 建表）两边是各跑各的，没有一行共用** —— 01 已经把这两段对拍过，
/// 证明它们逐字等价。所以这一条新盖住的是**前半段**：PPQ 读错、变速算错、声道挑错、
/// 该按秒还是按 tick 弄反 —— 这些错都会让事件时间整体偏掉，在这里立刻现形。
///
/// 时间比的是**音乐时间**（speed=1.0），与曲子自己的 BPM 无关，也不含播放倍速。
/// 全程不发任何按键：两边调的都是纯建表入口。
/// </summary>
public class FullChainParityTests
{
    /// <summary>移调与基准八度的组合。换一换能让更多音落到升半音 / 跨八度上。</summary>
    private static readonly (int Transpose, int? BaseOctave)[] MapSettings =
    {
        (0, null),
        (2, null),
        (-3, null),
        (0, 3),
        (0, 5),
        (7, 4),
    };

    // ============================ 核心断言 ============================

    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 全链对拍_每条轨的事件表逐条相等(string path)
    {
        var parsed = OriginalMidi.MidiLoader.Parse(path);
        var song = SongProject.Read(path);

        Assert.That(song.Tracks.Count, Is.EqualTo(parsed.Candidates.Count),
            $"{Path.GetFileName(path)}：轨数两边对不上");

        for (int i = 0; i < parsed.Candidates.Count; i++)
            CompareTrack(path, parsed.Candidates[i], song.Tracks[i], song, 0, null, allTimings: true);
    }

    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 全链对拍_各种移调与基准八度下也相等(string path)
    {
        var parsed = OriginalMidi.MidiLoader.Parse(path);
        var song = SongProject.Read(path);

        for (int i = 0; i < parsed.Candidates.Count; i++)
            foreach (var (transpose, baseOctave) in MapSettings)
                CompareTrack(path, parsed.Candidates[i], song.Tracks[i], song, transpose, baseOctave, allTimings: false);
    }

    private static void CompareTrack(
        string path, OriginalMidi.MidiCandidate candidate, Track track, Song song,
        int transpose, int? baseOctave, bool allTimings)
    {
        // 配对凭据：轨块序号 + 声道。两边都必须一致，否则比的根本不是同一条轨。
        Assert.Multiple(() =>
        {
            Assert.That(track.TrackIndex, Is.EqualTo(candidate.TrackIndex), "轨块序号对不上，配对无效");
            Assert.That(track.Channel, Is.EqualTo(candidate.Channel), "声道对不上，配对无效");
            Assert.That(track.Name, Is.EqualTo(candidate.Name), "轨名对不上");
        });

        // 我们的秒数：由 tick 经速度表算出来（原版那边是 DryWetMidi 直接给的）
        var ourNotes = Ported.RepertoireToSeconds.Convert(track.Notes, song.TempoMap);

        var originalMapping = Original.NoteMapper.Map(candidate.Notes, transpose, baseOctave);
        var ourMapping = Ported.NoteMapper.Map(ourNotes, transpose, baseOctave);

        string where = $"{Path.GetFileName(path)} / {candidate.Name}（轨{candidate.TrackIndex} 声道{candidate.Channel}）" +
                       $" 移调{transpose} 基准八度{baseOctave?.ToString() ?? "自动"}";

        foreach (var (timingName, originalTiming, ourTiming) in Timings(allTimings))
        {
            var expected = Original
                .PlaybackEngine.BuildSchedulePreview(originalMapping.Notes, originalTiming, speed: 1.0)
                .Select(e => (e.MusicTime, e.Kind, e.Key, e.Down))
                .ToArray();

            var builder = new Ported.EventBuilder { Timing = ourTiming };
            var (ourEvents, _) = builder.Build(FilterInRange(ourMapping.Notes), Ported.EventBuilder.ModState.None);
            var actual = ourEvents.Select(e => (e.T, KindName(e.Kind), e.Code, e.Down)).ToArray();

            // 防假绿：有可演奏的音却建出空表，说明语料或过滤出了问题，"两边都空"不算通过
            if (originalMapping.InRangeCount > 0)
                Assert.That(expected, Is.Not.Empty, $"{where} [{timingName}]：原版建出了空事件表");

            Assert.That(actual, Is.EqualTo(expected),
                $"{where} [{timingName}]：{candidate.Notes.Count} 个音符，事件表不一致");
        }
    }

    // ============================ 语料覆盖面 ============================

    /// <summary>
    /// 对拍要有意义，语料就得真的踩到那几条难路。逐条把「踩到了没有」数出来，
    /// 否则「语料覆盖了升半音与跨八度」只是一句没有证据的话。
    /// </summary>
    [Test]
    public void 全链对拍语料覆盖了升半音与跨八度与变速与多轨()
    {
        MidiCorpus.AssertCorpusPresent();

        int sharpSwitches = 0, octaveSwitches = 0, variableTempoFiles = 0, multiTrackFiles = 0;
        int formats0 = 0, formats1 = 0, formats2 = 0;

        foreach (var path in MidiCorpus.Files)
        {
            var song = SongProject.Read(path);
            if (song.Tracks.Count > 1) multiTrackFiles++;
            if (song.TempoMap.TempoChanges.Count > 0) variableTempoFiles++;

            // 升半音 = 鼠标中键；跨八度 = 鼠标左/右键。扫几种移调，保证能踩到。
            foreach (var (transpose, baseOctave) in MapSettings)
            {
                foreach (var track in song.Tracks)
                {
                    var notes = Ported.RepertoireToSeconds.Convert(track.Notes, song.TempoMap);
                    var mapping = Ported.NoteMapper.Map(notes, transpose, baseOctave);
                    var builder = new Ported.EventBuilder { Timing = Ported.InputTiming.Standard };
                    var (events, _) = builder.Build(FilterInRange(mapping.Notes), Ported.EventBuilder.ModState.None);

                    foreach (var e in events)
                    {
                        if (!e.Down) continue;
                        if (e.Kind == Ported.EventBuilder.K_MouseMiddle) sharpSwitches++;
                        else if (e.Kind is Ported.EventBuilder.K_MouseLeft or Ported.EventBuilder.K_MouseRight) octaveSwitches++;
                    }
                }
            }
        }

        foreach (var path in MidiCorpus.Files)
        {
            using var stream = File.OpenRead(path);
            var file = Melanchall.DryWetMidi.Core.MidiFile.Read(stream,
                new Melanchall.DryWetMidi.Core.ReadingSettings
                {
                    NotEnoughBytesPolicy = Melanchall.DryWetMidi.Core.NotEnoughBytesPolicy.Ignore
                });
            switch ((int)file.OriginalFormat)
            {
                case 0: formats0++; break;
                case 1: formats1++; break;
                case 2: formats2++; break;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(sharpSwitches, Is.GreaterThan(0), "语料一首都没踩到升半音（鼠标中键）");
            Assert.That(octaveSwitches, Is.GreaterThan(0), "语料一首都没踩到跨八度（鼠标左右键）");
            Assert.That(variableTempoFiles, Is.GreaterThan(0), "语料里没有变速曲目");
            Assert.That(multiTrackFiles, Is.GreaterThan(0), "语料里没有多轨曲目");
            Assert.That(formats0, Is.GreaterThan(0), "语料里没有格式 0");
            Assert.That(formats1, Is.GreaterThan(0), "语料里没有格式 1");
            Assert.That(formats2, Is.GreaterThan(0), "语料里没有格式 2");
        });
    }

    // ============================ 帮手 ============================

    /// <summary>原版 <c>BuildSchedulePreview</c> 内部就是这一行，我们这边跟着做同一件事。</summary>
    private static List<Ported.MappedNote> FilterInRange(IReadOnlyList<Ported.MappedNote> notes)
        => notes.Where(n => n.InRange).ToList();

    /// <summary>与原版 <c>BuildSchedulePreview</c> 里那支 switch 逐字对应。</summary>
    private static string KindName(int kind) => kind switch
    {
        Ported.EventBuilder.K_Key => "key",
        Ported.EventBuilder.K_MouseLeft => "mouse-left",
        Ported.EventBuilder.K_MouseRight => "mouse-right",
        _ => "mouse-middle"
    };

    /// <summary><paramref name="all"/> = 三档时序各跑一遍；否则只跑「标准」这一档。</summary>
    private static IEnumerable<(string Name, Original.InputTiming Original, Ported.InputTiming Ported)>
        Timings(bool all)
    {
        yield return ("标准", Original.InputTiming.Standard, Ported.InputTiming.Standard);
        if (!all) yield break;
        yield return ("稳健", Original.InputTiming.Safe, Ported.InputTiming.Safe);
        yield return ("极限", Original.InputTiming.Aggressive, Ported.InputTiming.Aggressive);
    }
}

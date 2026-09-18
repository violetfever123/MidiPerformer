using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;
using MidiPerformer.Core.UseCases.Analysis;
using MidiPerformer.Core.UseCases.Perform.Preflight;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Tests.Corpus;
using NUnit.Framework;

namespace MidiPerformer.Tests.Perform;

/// <summary>
/// 预检 —— **按下开始之前该不该放行**。
///
/// 它是纯函数：两个环境事实（是不是管理员、输入法是不是中文）由界面问网关取好传进来，
/// 这里只判断。所以这一组测试不需要窗口、不需要 Win32，连假时钟都不用。
///
/// 守两件事：
/// <list type="number">
/// <item>三种失败各回各的原因 —— 回错了，界面就会给人指错方向。</item>
/// <item><b>判断的顺序</b>。三个条件同时不满足时先说哪一个，是有讲究的：
///   权限不够是环境不对，这时候报「这条轨弹不了」会让人去换轨，换了还是不行。</item>
/// </list>
/// </summary>
public class PreflightTests
{
    [Test]
    public void 不是管理员就不放行()
    {
        var outcome = 检查(单声部曲(), trackIndex: 0, elevated: false, imeInChinese: false);

        Assert.That(outcome, Is.EqualTo(PerformanceStartOutcome.NotElevated));
    }

    [Test]
    public void 输入法是中文就不放行()
    {
        var outcome = 检查(单声部曲(), trackIndex: 0, elevated: true, imeInChinese: true);

        Assert.That(outcome, Is.EqualTo(PerformanceStartOutcome.ImeActive));
    }

    /// <summary>
    /// 「弹不了」的三种长相都要认出来，而且认的是 <c>TrackRanking.IsPlayable</c> 那一条判据
    /// （有音 + 单声部 + 非打击乐），不是这里另立一套。
    /// </summary>
    [Test]
    public void 轨弹不了就不放行()
    {
        Assert.Multiple(() =>
        {
            Assert.That(检查(和弦曲(), trackIndex: 0), Is.EqualTo(PerformanceStartOutcome.NoPlayableTrack),
                "多声部的轨放行了 —— 口琴一次只响一个音");
            Assert.That(检查(打击乐曲(), trackIndex: 0), Is.EqualTo(PerformanceStartOutcome.NoPlayableTrack),
                "打击乐轨放行了 —— 鼓点发过去只是一串没意义的音");
            Assert.That(检查(单声部曲(), trackIndex: 7), Is.EqualTo(PerformanceStartOutcome.NoPlayableTrack),
                "轨下标越界放行了");
        });
    }

    /// <summary>
    /// 一条音都没有的轨也弹不了。
    ///
    /// <b>这一条只能用 Core 对象摆，SMF 造不出来</b>：解析器是按轨块里的音分组生成轨的，
    /// 没有音的轨块根本不会成轨（见 <c>SongProject</c>）。空轨来自编辑器 —— 把一条轨的音删光，
    /// 轨还在。漏了这一条，预检会放行、起跑、建出一张空事件表，用户看到的是
    /// 「按了开始什么都没发生，也没有任何解释」，而预检存在的全部理由就是给那句解释。
    /// </summary>
    [Test]
    public void 空轨也不放行()
    {
        var song = new Song(new[] { new Track(0, 0, "空轨", 24, Array.Empty<Note>()) }, Tempo());

        Assert.That(检查(song, trackIndex: 0), Is.EqualTo(PerformanceStartOutcome.NoPlayableTrack),
            "空轨放行了 —— 一个音都发不出来，界面还什么都不说");
    }

    /// <summary>
    /// 顺序：权限 → 输入法 → 可弹轨。三个条件**同时**不满足时，先说的那一个必须是用户该先改的那一个。
    /// </summary>
    [Test]
    public void 三个条件同时不满足时先说权限()
    {
        Assert.Multiple(() =>
        {
            Assert.That(检查(和弦曲(), 0, elevated: false, imeInChinese: true),
                Is.EqualTo(PerformanceStartOutcome.NotElevated), "权限不够却先报了别的");
            Assert.That(检查(和弦曲(), 0, elevated: true, imeInChinese: true),
                Is.EqualTo(PerformanceStartOutcome.ImeActive), "输入法是中文却先报了轨弹不了");
        });
    }

    [Test]
    public void 都过关就放行()
    {
        var outcome = 检查(单声部曲(), trackIndex: 0, elevated: true, imeInChinese: false);

        Assert.That(outcome, Is.EqualTo(PerformanceStartOutcome.Started));
    }

    // ==================== 夹具 ====================

    private static PerformanceStartOutcome 检查(
        Song song, int trackIndex, bool elevated = true, bool imeInChinese = false)
        => PerformancePreflight.Check(
            new StartPerformanceRequest(song, trackIndex, null, InputTiming.Standard, 3),
            elevated,
            imeInChinese);

    /// <summary>240BPM、480 tick/四分音符 → 480 tick = 0.25 秒。两个音前后错开，是单声部。</summary>
    private static Song 单声部曲() => SongProject.ReadBytes(SmfWriter.Build(1, 480,
        SmfTrack.Named("旋律")
            .Tempo(0, 250_000)
            .Note(0, 240, 0, 60)
            .Note(480, 240, 0, 62)));

    /// <summary>1 tick = 0.1ms。空轨那一例要自己摆 Core 对象，所以这份表也得自己给。</summary>
    private static TempoMap Tempo() => new(
        TimeDivision.PulsesPerQuarter(5000),
        new[] { new TempoChange(0, 500_000) });

    /// <summary>同一 tick 上两个起音 —— 口琴同时只能响一个，这条轨弹不了。</summary>
    private static Song 和弦曲() => SongProject.ReadBytes(SmfWriter.Build(1, 480,
        SmfTrack.Named("和弦")
            .Tempo(0, 250_000)
            .NoteOn(0, 0, 60)
            .NoteOn(0, 0, 64)
            .NoteOff(240, 0, 60)
            .NoteOff(240, 0, 64)));

    /// <summary>GM 规定第 10 声道（下标 9）是打击乐。</summary>
    private static Song 打击乐曲() => SongProject.ReadBytes(SmfWriter.Build(1, 480,
        SmfTrack.Named("鼓")
            .Tempo(0, 250_000)
            .Note(0, 120, TrackRanking.PercussionChannel, 38)
            .Note(240, 120, TrackRanking.PercussionChannel, 38)));
}

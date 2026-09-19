using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform.Dispatch;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Perform.Safety;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Core.UseCases.Timeline;
using MidiPerformer.Tests.Corpus;
using MidiPerformer.Tests.Fakes;
using NUnit.Framework;

namespace MidiPerformer.Tests.Perform;

/// <summary>
/// 整条演奏链拼起来跑一遍：**曲子 → 秒 → 键位 → 事件表 → 派发**。
///
/// 各段自己都有测试（02 的 <c>EventBuilderParityTests</c>、这里的 DispatcherTests），
/// 这一条守的是**接缝**：单位换得对不对（tick 只在这里变成秒）、
/// 走子的锚点和看门狗的截止点是不是同一个原点、空轨和越界音会不会把链条打断。
///
/// 窗口里的编排逻辑（06 起搬进了用例层 <c>StartPerformance</c>）就是照这个顺序写的，
/// 所以这一条同时是那块编排的可执行说明。
/// </summary>
public class PerformChainTests
{
    /// <summary>假时钟每被读一次前进的秒数。见 DispatcherTests 关于容差的说明。</summary>
    private const double Step = 0.005;

    /// <summary>
    /// 比 DispatcherTests 宽一档：这条链上**看门狗线程也在读同一个假时钟**，
    /// 而假时钟每被读一次就前进一步 —— 它的读会和派发器自己的读交错，
    /// 于是记账时刻会比「派发器自己读两次」再多跨一两步。
    /// 这不是实现的问题，是自动步进时钟被两个线程共用时必然的噪声。
    /// </summary>
    private const double Tol = 4 * Step + 0.001;

    [Test]
    public void 整曲放完后每个音都发到了并且收好了尾()
    {
        // 240BPM、480 tick/四分音符 → 480 tick = 0.25 秒。
        // 三个音从 0.25s 起，各 0.125s —— 刻意不摆在 0 秒：目标时刻落在过去的事件
        // 是「一上来就直接发」的另一条路径，这里要测的是正常的时间轴。
        var song = MidiReader.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("旋律")
                .Tempo(0, 250_000)
                .Note(480, 240, 0, 60)
                .Note(720, 240, 0, 62)
                .Note(960, 240, 0, 64)));

        var clock = new FakeClock { AutoStepSeconds = Step };
        var sink = new RecordingEventSink(clock);
        var timing = InputTiming.Standard;

        var track = song.Tracks[0];
        var seconds = RepertoireToSeconds.Convert(track.Notes, song.TempoMap);
        var mapped = NoteMapper.Map(seconds, track.Transpose, null);
        var builder = new EventBuilder { Timing = timing };
        var (events, total) = builder.Build(mapped.Notes.Where(n => n.InRange).ToList(), EventBuilder.ModState.None);

        Assert.That(events, Is.Not.Empty, "三个音一个都没映射成事件");

        // 锚点定在物理 0 秒：走子、看门狗、派发器三者共用这一个原点。
        var walker = new SongWalker(song);
        walker.Start(0.0);

        var dispatcher = new Dispatcher(clock, sink);
        var watchdog = Watchdog.For(events, walker, clock, sink, dispatcher.Stop);
        watchdog.Start();

        try
        {
            dispatcher.Run(events, walker, timing);
        }
        finally
        {
            watchdog.Cancel();
        }

        var sent = sink.Sent;
        Assert.Multiple(() =>
        {
            Assert.That(sent.Count, Is.EqualTo(events.Count), "有事件没发出去");
            Assert.That(sent.Count(e => e.Down && IsKey(e)), Is.EqualTo(3), "三个音应当各按下一次");
            Assert.That(sent.Zip(sent.Skip(1)).All(p => p.First.At <= p.Second.At), Is.True,
                "发出的时刻不是单调不减的");

            // 事件表里最后一个是抬键，它的目标时刻就是整场演奏的收尾时刻
            double lastTarget = total - timing.LeadMs / 1000.0;
            Assert.That(sent[^1].At, Is.EqualTo(lastTarget).Within(Tol), "收尾时刻不对");

            Assert.That(sink.ReleaseAllCount, Is.GreaterThanOrEqualTo(1), "放完了没有松键");
            Assert.That(watchdog.Fired, Is.False, "曲子正常放完，看门狗不该出场");
        });
    }

    /// <summary>
    /// 一条空轨（或者整轨都超出口琴音域）不该把链条打断：事件表是空的，
    /// 但收尾照样发生。演奏器上「选了一条没音可弹的轨」是正常操作，不是异常。
    /// </summary>
    [Test]
    public void 空事件表也走得完()
    {
        var song = MidiReader.ReadBytes(SmfWriter.Build(1, 480,
            SmfTrack.Named("空的").Tempo(0, 250_000)));

        var clock = new FakeClock { AutoStepSeconds = Step };
        var sink = new RecordingEventSink(clock);
        var walker = new SongWalker(song);
        walker.Start(0.0);

        var dispatcher = new Dispatcher(clock, sink);
        dispatcher.Run(Array.Empty<EventBuilder.PhysicalEvent>(), walker, InputTiming.Standard);

        Assert.That(sink.ReleaseAllCount, Is.GreaterThanOrEqualTo(1));
    }

    private static bool IsKey(RecordingEventSink.SentEvent e) => e.Kind == EventBuilder.K_Key;
}

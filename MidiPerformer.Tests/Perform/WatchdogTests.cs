using System.Diagnostics;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform.Dispatch;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Perform.Safety;
using MidiPerformer.Core.UseCases.Timeline;
using MidiPerformer.Tests.Fakes;
using NUnit.Framework;

namespace MidiPerformer.Tests.Perform;

/// <summary>
/// 看门狗 —— **放不停怎么办**。
///
/// 它是个兜底，所以最要紧的两条是「到点一定会开火」和「没到点绝不开火」：
/// 前者漏了等于没有兜底，后者漏了会把一首正放着的曲子拦腰砍断。
///
/// 这里用**手动**假时钟（与 DispatcherTests 相反）：看门狗等的是「时钟过没过截止点」，
/// 时间点由测试直接摆过去，不用等它自己走到。
/// </summary>
public class WatchdogTests
{
    [Test]
    public void 超时时刻是最后一个事件的音乐时间加五秒()
    {
        var clock = new FakeClock();
        var sink = new RecordingEventSink(clock);
        var walker = 走子();
        var events = 音键表(0.20, 0.30);

        var watchdog = Watchdog.For(events, walker, clock, sink, () => { });

        // 走子的锚点在物理 0 秒、倍速 1.0，所以物理时刻 = 音乐时刻。
        // 事件表里最后一个是 0.30 的抬键，末尾那个 0.33 的抬键才是最大的时间戳。
        Assert.That(watchdog.DeadlineSeconds, Is.EqualTo(0.33 + Watchdog.GraceSeconds).Within(1e-9));
    }

    [Test]
    public void 空事件表退回曲子开头加五秒()
    {
        var clock = new FakeClock();
        var sink = new RecordingEventSink(clock);

        var watchdog = Watchdog.For(
            Array.Empty<EventBuilder.PhysicalEvent>(), 走子(), clock, sink, () => { });

        Assert.That(watchdog.DeadlineSeconds, Is.EqualTo(Watchdog.GraceSeconds).Within(1e-9));
    }

    [Test]
    public void 到点叫停并松开所有按键()
    {
        var clock = new FakeClock();
        var sink = new RecordingEventSink(clock);
        int stopCount = 0;
        var watchdog = Watchdog.For(音键表(0.20, 0.30), 走子(), clock, sink, () => stopCount++);

        watchdog.Start();

        // 先推到截止前一瞬：这时还不能开火。等一会儿是为了给守着的线程一个开火的机会
        // —— 它等的是时钟，时钟没到点它就一直在睡，所以这段真实时间不会让它误开火。
        clock.Seconds = watchdog.DeadlineSeconds - 0.001;
        Thread.Sleep(50);

        Assert.Multiple(() =>
        {
            Assert.That(stopCount, Is.EqualTo(0), "还没到点就叫停了");
            Assert.That(sink.ReleaseAllCount, Is.EqualTo(0), "还没到点就把键松了");
            Assert.That(watchdog.Fired, Is.False);
        });

        // 推过截止点
        clock.Seconds = watchdog.DeadlineSeconds + 0.5;

        Assert.That(等到(() => watchdog.Fired), Is.True, "过点了却没开火");
        Assert.Multiple(() =>
        {
            Assert.That(stopCount, Is.EqualTo(1), "开火时要通知派发器停，而且只通知一次");
            Assert.That(sink.ReleaseAllCount, Is.GreaterThanOrEqualTo(1), "开火了却没有松开按键");
        });

        watchdog.Dispose();
    }

    [Test]
    public void 撤掉之后不再开火()
    {
        var clock = new FakeClock();
        var sink = new RecordingEventSink(clock);
        int stopCount = 0;
        var watchdog = Watchdog.For(音键表(0.20), 走子(), clock, sink, () => stopCount++);

        watchdog.Start();
        watchdog.Cancel();          // 曲子自然放完，派发器自己已经收好尾了

        clock.Seconds = watchdog.DeadlineSeconds + 100;
        Thread.Sleep(50);

        Assert.Multiple(() =>
        {
            Assert.That(watchdog.Fired, Is.False, "撤掉了还开火");
            Assert.That(stopCount, Is.EqualTo(0));
            Assert.That(sink.ReleaseAllCount, Is.EqualTo(0), "撤掉了还去松键");
        });

        watchdog.Dispose();
    }

    /// <summary>
    /// 派发器卡在等待里时，看门狗真的能把它收掉。
    ///
    /// 这是这一对东西存在的理由：曲子放到一半遇上预料之外的情况（派发线程被系统冻住、
    /// 时钟被拨过），它会一直等下去 —— 只有看门狗能收场。
    ///
    /// 这里**刻意用显式截止点**而不是 <see cref="Watchdog.For"/>：<c>For</c> 算出来的截止点
    /// 总在最后一个事件之后 5 秒，跳表跳不出「事件还没放完、看门狗已经到点」这个局面，
    /// 而那正是要测的那个局面。
    /// </summary>
    [Test]
    public void 派发器卡住时看门狗能收场()
    {
        var clock = new FakeClock { Seconds = 0.0 };      // 手动模式：时间不自己往前走
        var sink = new RecordingEventSink(clock);
        var walker = 走子();
        var events = 音键表(100.0);                       // 第一个事件的目标就在 100 秒外

        var dispatcher = new Dispatcher(clock, sink);
        var watchdog = new Watchdog(clock, sink, dispatcher.Stop, deadlinePhysicalSeconds: 5.0);

        var thread = new Thread(() => dispatcher.Run(events, walker, InputTiming.Standard))
        {
            IsBackground = true
        };
        thread.Start();
        watchdog.Start();

        // 让派发器先在等待里待住，再把时钟推过看门狗的截止点
        Thread.Sleep(30);
        clock.Seconds = watchdog.DeadlineSeconds + 0.5;

        Assert.Multiple(() =>
        {
            Assert.That(thread.Join(TimeSpan.FromSeconds(5)), Is.True, "看门狗没能把派发器收掉");
            Assert.That(sink.Sent, Is.Empty, "还没到发的时候，不该有事件发出去");
            Assert.That(sink.ReleaseAllCount, Is.GreaterThanOrEqualTo(1), "收场时没有松开按键");
        });

        watchdog.Dispose();
    }

    // ==================== 夹具 ====================

    private static SongWalker 走子()
    {
        var song = new Song(Array.Empty<Track>(), new TempoMap(TimeDivision.PulsesPerQuarter(480)));
        var walker = new SongWalker(song);
        walker.Start(0.0);
        return walker;
    }

    /// <summary>每个音乐时刻一个按下，30ms 后抬起 —— 与 DispatcherTests 里那张表同一形状。</summary>
    private static List<EventBuilder.PhysicalEvent> 音键表(params double[] musicTimes)
    {
        var list = new List<EventBuilder.PhysicalEvent>(musicTimes.Length * 2);
        for (int i = 0; i < musicTimes.Length; i++)
        {
            char key = PlayKeys.Keys[i % PlayKeys.Keys.Length];
            list.Add(new EventBuilder.PhysicalEvent(musicTimes[i], EventBuilder.K_Key, key, true, $"音{i}"));
            list.Add(new EventBuilder.PhysicalEvent(musicTimes[i] + 0.03, EventBuilder.K_Key, key, false, ""));
        }
        return list;
    }

    private static bool 等到(Func<bool> condition, int milliseconds = 3000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
            if (condition()) return true;
            Thread.Sleep(1);
        }
        return condition();
    }
}

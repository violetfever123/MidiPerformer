using System.Diagnostics;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Perform.Dispatch;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Timeline;
using MidiPerformer.Tests.Fakes;
using NUnit.Framework;

namespace MidiPerformer.Tests.Perform;

/// <summary>
/// 派发器 —— 什么时候发。断言的是「事件表 + 时钟」进去、「什么时候发了什么」出来。
/// 假时钟必须开 <see cref="FakeClock.AutoStepSeconds"/>：等待循环靠读时钟往前走，
/// 手动模式下时钟不动，自旋那一段会一直转下去。
/// 容差是两步：假时钟只在被读时前进、且返回步进之前的值，
/// 「判到点了」一读、<c>Send</c> 记账又一读，中间必然跨过一步。
/// </summary>
public class DispatcherTests
{
    /// <summary>假时钟每被读一次前进的秒数，也是这组测试的容差基准。</summary>
    private const double Step = 0.005;

    /// <summary>两步步长加一点余量，覆盖「自旋判到」与「Send 记账」之间的两次读。</summary>
    private const double Tol = 2 * Step + 0.001;

    // ==================== 时间戳 ====================

    [Test]
    public void 事件按时间戳顺序发出且都落在目标时刻上()
    {
        var (clock, sink, walker) = 开工();
        var timing = InputTiming.Standard;
        var events = 音键表(0.20, 0.30, 0.40);

        new Dispatcher(clock, sink).Run(events, walker, timing);

        var sent = sink.Sent;
        Assert.That(sent.Count, Is.EqualTo(events.Count), "有事件没发出去");

        double lead = timing.LeadMs / 1000.0;
        for (int i = 0; i < events.Count; i++)
        {
            double target = events[i].T - lead;

            Assert.Multiple(() =>
            {
                // 顺序：发出顺序必须是事件表顺序，时间戳单调不减
                Assert.That(sent[i].Code, Is.EqualTo(events[i].Code), $"第 {i} 个事件发错了");
                Assert.That(sent[i].Down, Is.EqualTo(events[i].Down));

                // 不许提前：LeadMs 是给游戏采样留的提前量
                Assert.That(sent[i].At, Is.GreaterThanOrEqualTo(target - 0.0005),
                    $"第 {i} 个事件比目标时刻早发了，目标的提前量就不够了");

                // 也不许迟到太多：迟到同样吃掉那个余量
                Assert.That(sent[i].At, Is.EqualTo(target).Within(Tol),
                    $"第 {i} 个事件晚了 {sent[i].At - target:F4}s");
            });
        }

        Assert.That(sent.Zip(sent.Skip(1)).All(p => p.First.At <= p.Second.At), Is.True,
            "发出的时刻不是单调不减的");
    }

    /// <summary>
    /// 两档时序各跑一遍，每一档落在它自己那个 LeadMs 算出的目标上（拦「换了档却还留着另一个数」）。
    /// </summary>
    [TestCase(0.104)]   // 稳健
    [TestCase(0.057)]   // 标准
    public void 提前量来自所选档位(double leadSeconds)
    {
        var (clock, sink, walker) = 开工();
        var timing = 档位(leadSeconds);
        var events = 音键表(0.20, 0.30);

        new Dispatcher(clock, sink).Run(events, walker, timing);

        for (int i = 0; i < events.Count; i++)
        {
            double target = events[i].T - leadSeconds;
            Assert.That(sink.Sent[i].At, Is.EqualTo(target).Within(Tol),
                $"档位 LeadMs={leadSeconds * 1000:F0} 时第 {i} 个事件没落在目标上");
        }
    }

    /// <summary>
    /// 同一条事件表换档位后整体平移，位移量正好是两档 LeadMs 之差
    /// （只看「各自落在自己的目标上」的话，拿整曲第一个时刻一次性算偏移的实现也能过）。
    /// </summary>
    [Test]
    public void 换档位时提前量整体平移()
    {
        var events = 音键表(0.20, 0.30);

        var (clockA, sinkA, walkerA) = 开工();
        new Dispatcher(clockA, sinkA).Run(events, walkerA, InputTiming.Standard);

        var (clockB, sinkB, walkerB) = 开工();
        new Dispatcher(clockB, sinkB).Run(events, walkerB, InputTiming.Safe);

        // 提前量越大发得越早，所以位移是负的：稳健档比标准档早 47ms
        double expected = (InputTiming.Standard.LeadMs - InputTiming.Safe.LeadMs) / 1000.0;
        Assert.That(expected, Is.LessThan(0), "稳健档的提前量本来就比标准档大，所以它该发得更早");

        for (int i = 0; i < events.Count; i++)
        {
            // 两边各有两步的记账误差，容差要放宽到两倍
            Assert.That(sinkB.Sent[i].At - sinkA.Sent[i].At, Is.EqualTo(expected).Within(2 * Tol),
                $"第 {i} 个事件的位移不等于提前量之差");
        }
    }

    // ==================== 收尾 ====================

    [Test]
    public void 自然放完后松开所有按键()
    {
        var (clock, sink, walker) = 开工();

        new Dispatcher(clock, sink).Run(音键表(0.20, 0.30), walker, InputTiming.Standard);

        Assert.That(sink.ReleaseAllCount, Is.GreaterThanOrEqualTo(1), "放完了却没有松键");
    }

    [Test]
    public void 空事件表也要走收尾()
    {
        var (clock, sink, walker) = 开工();

        new Dispatcher(clock, sink).Run(Array.Empty<EventBuilder.PhysicalEvent>(), walker, InputTiming.Standard);

        Assert.Multiple(() =>
        {
            Assert.That(sink.Sent, Is.Empty);
            Assert.That(sink.ReleaseAllCount, Is.GreaterThanOrEqualTo(1), "没有事件也得把上一轮的残留清掉");
        });
    }

    // ==================== 急停 ====================

    /// <summary>
    /// 急停立刻生效，不等当前那一觉睡完：让第二个事件落在 30 秒之外，
    /// 裸 <c>Thread.Sleep</c> 会睡满，能中断的等待则一叫就醒（时钟用手动模式）。
    /// </summary>
    [Test]
    public void 急停立刻生效不用等当前那次等待睡完()
    {
        var clock = new FakeClock { Seconds = 1.0 };      // 手动模式
        var sink = new RecordingEventSink(clock);
        var walker = 走子();
        // 时钟从 1.0 秒起，前两个事件（目标 0.143 / 0.173）已经在过去、会被立刻发出；
        // 第三个的目标在 30 秒外，那才是正被子等待的那个
        var events = 音键表(0.20, 30.0);

        var dispatcher = new Dispatcher(clock, sink);
        var thread = new Thread(() => dispatcher.Run(events, walker, InputTiming.Standard))
        {
            IsBackground = true
        };

        thread.Start();
        Assert.That(等到(() => sink.Sent.Count >= 2), Is.True, "该立刻发的那两个事件没发出来，后面不用测了");

        var stopwatch = Stopwatch.StartNew();
        dispatcher.Stop();
        bool joined = thread.Join(TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(joined, Is.True, "叫停之后派发线程没退出");
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(500),
                $"叫停到退出花了 {stopwatch.ElapsedMilliseconds}ms，而它在等的是 30 秒后的下一个事件");
            Assert.That(sink.Sent.Count, Is.EqualTo(2), "叫停之后还继续发事件了");
            Assert.That(sink.ReleaseAllCount, Is.GreaterThanOrEqualTo(1), "急停没有松键");
        });
    }

    [Test]
    public void 叫停后再调Run也不会发任何东西()
    {
        var (clock, sink, walker) = 开工();
        var dispatcher = new Dispatcher(clock, sink);
        dispatcher.Stop();

        dispatcher.Run(音键表(0.20, 0.30), walker, InputTiming.Standard);

        Assert.Multiple(() =>
        {
            Assert.That(sink.Sent, Is.Empty);
            Assert.That(sink.ReleaseAllCount, Is.GreaterThanOrEqualTo(1));
        });
    }

    // ==================== 夹具 ====================

    /// <summary>
    /// 一副能跑的夹具：自动步进的假时钟 + 记录型 sink + 锚点定在物理 0 秒的走子
    /// （锚点 0、倍速 1，物理时刻 = 音乐时刻）。
    /// </summary>
    private static (FakeClock Clock, RecordingEventSink Sink, SongWalker Walker) 开工()
    {
        var clock = new FakeClock { AutoStepSeconds = Step };
        var sink = new RecordingEventSink(clock);
        return (clock, sink, 走子());
    }

    private static SongWalker 走子()
    {
        // 派发只用到 SongWalker.PhysicalAt，那是一段与曲子内容无关的仿射变换，空曲子就够（事件表另造）
        var song = new Song(Array.Empty<Track>(), new TempoMap(TimeDivision.PulsesPerQuarter(480)));
        var walker = new SongWalker(song);
        walker.Start(0.0);
        return walker;
    }

    /// <summary>
    /// 手搓一张事件表：每个音乐时刻一个按下，30ms 之后抬起，键位在 z..m 上轮着来
    /// （不经过 <c>EventBuilder</c>，每个事件的目标时刻一眼看得清）。
    /// </summary>
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

    /// <summary>按提前量反查档位，免得测试自己写第二套档位定义（第三档「极限」已随 47 号收掉）。</summary>
    private static InputTiming 档位(double leadSeconds) =>
        new[] { InputTiming.Safe, InputTiming.Standard }
            .Single(t => Math.Abs(t.LeadMs / 1000.0 - leadSeconds) < 1e-9);

    /// <summary>轮询等待一个条件成立。只用于等后台线程做事，等不到就判失败。</summary>
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

using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;
using MidiPerformer.Core.UseCases.Perform;
using MidiPerformer.Core.UseCases.Perform.Repertoire;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Tests.Corpus;
using MidiPerformer.Tests.Fakes;
using NUnit.Framework;

namespace MidiPerformer.Tests.Perform;

/// <summary>
/// 起一场演奏 —— 重点是盲倒计时那几秒：那是用户切到游戏窗口的窗口期，期间一个音都不能发出去。
/// 全部用假时钟 + <see cref="RecordingEventSink"/>，不等真时间（时钟每被读一次自己前进 5ms）；
/// 只有「倒计时里空转一会儿」那一处要一点真实时间，留给一个不等倒计时的实现去犯错的机会。
/// </summary>
public class StartPerformanceTests
{
    /// <summary>假时钟每被读一次前进的秒数。</summary>
    private const double Step = 0.005;

    [Test]
    public void 预检不放行时不起线程也不发一个音()
    {
        var clock = new FakeClock();
        var sink = new RecordingEventSink(clock);
        var performance = new StartPerformance(clock, sink);

        var outcome = performance.Start(请求(倒计时秒: 3), elevated: false, imeInChinese: false);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(PerformanceStartOutcome.NotElevated));
            Assert.That(performance.Running, Is.False, "没放行却把自己标成在跑");
            Assert.That(performance.CountdownSecondsLeft, Is.EqualTo(0), "没起跑却报着倒计时剩余秒数");
        });

        // 不放行的那条路上没有线程；等一会儿才拦得住偷偷起了一条线程的实现
        Thread.Sleep(50);
        Assert.That(sink.Sent, Is.Empty, "预检没放行，却发了按键");
    }

    /// <summary>
    /// 倒计时没走完，一个音都不能发。
    /// 假时钟每读一次走 0.1 秒、走到 1 秒就停住，而倒计时的终点在 3 秒 —— 它卡在等待里空转，
    /// 读数稳定地停在「还剩 2 秒」。曲子把第一个音摆在音乐 0 秒，不等倒计时的实现会当场把它发出去。
    /// </summary>
    [Test]
    public void 倒计时没走完一个音都不发()
    {
        var clock = new FakeClock { AutoStepSeconds = 0.1, AutoStopAtSeconds = 1.0 };
        var sink = new RecordingEventSink(clock);
        var performance = new StartPerformance(clock, sink);

        var outcome = performance.Start(请求(倒计时秒: 3), elevated: true, imeInChinese: false);
        Assert.That(outcome, Is.EqualTo(PerformanceStartOutcome.Started));

        Thread.Sleep(120);                  // 留给不等倒计时的实现一个犯错的机会

        Assert.Multiple(() =>
        {
            Assert.That(sink.Sent, Is.Empty, "倒计时还没走完就把音发出去了");
            Assert.That(sink.ReleaseAllCount, Is.EqualTo(0), "倒计时期间不该碰键盘");
            Assert.That(performance.Running, Is.True, "还在倒计时，却已经算收场了");

            // 悬浮层的大数字：3 秒的倒计时走到还剩 2 秒；等于 3 说明没在走，等于 0 说明已当成结束
            Assert.That(performance.CountdownSecondsLeft, Is.EqualTo(2), "悬浮层的大数字不对");
        });

        performance.Stop();
    }

    /// <summary>
    /// 倒计时走完，第一个音真的发出去，而且不早于倒计时终点。
    /// 倒计时取 0.02 秒而不是 3 秒：等待是分片的，每片最多 5ms 真实时间。
    /// </summary>
    [Test]
    public void 倒计时走完就发出第一个音()
    {
        const double countdown = 0.02;

        var clock = new FakeClock { AutoStepSeconds = Step };
        var sink = new RecordingEventSink(clock);
        var performance = new StartPerformance(clock, sink);

        // 先挂上再起跑：Finished 在派发线程上触发，起跑之后再挂有可能错过它
        using var finished = new ManualResetEventSlim(false);
        performance.Finished += () => finished.Set();

        performance.Start(请求(countdown), elevated: true, imeInChinese: false);

        Assert.That(finished.Wait(TimeSpan.FromSeconds(10)), Is.True, "一场短曲子放不完");

        var 按下 = sink.KeyEvents.Where(e => e.Down).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(按下, Is.Not.Empty, "倒计时走完了，第一个音却没发出去");
            Assert.That(按下[0].At, Is.GreaterThanOrEqualTo(countdown),
                "第一个音发得比倒计时终点还早");
            Assert.That(performance.Error, Is.Null);
            Assert.That(performance.Running, Is.False);

            // 进度条的分母是整曲时长，分子是「距锚点过了多久」——收场比最后一个事件还早
            Assert.That(performance.TotalSeconds, Is.EqualTo(0.125).Within(1e-9), "进度条的分母不对");
            Assert.That(performance.MusicNow, Is.GreaterThan(0), "进度一直没走");
        });
    }

    /// <summary>
    /// 倒计时期间急停 = 取消：派发器还没出生，能解开等待循环的只有 <see cref="StartPerformance.Stop"/>。
    /// 手动假时钟，时间不自己走。
    /// </summary>
    [Test]
    public void 倒计时期间急停就取消整场()
    {
        var clock = new FakeClock();                       // 手动：时钟不走，倒计时永远走不完
        var sink = new RecordingEventSink(clock);
        var performance = new StartPerformance(clock, sink);

        using var finished = new ManualResetEventSlim(false);
        performance.Finished += () => finished.Set();

        performance.Start(请求(倒计时秒: 3), elevated: true, imeInChinese: false);
        performance.Stop();

        Assert.That(finished.Wait(TimeSpan.FromSeconds(10)), Is.True, "取消之后线程没收场");
        Assert.Multiple(() =>
        {
            Assert.That(sink.Sent, Is.Empty, "取消了却还是发了音");
            Assert.That(sink.ReleaseAllCount, Is.GreaterThanOrEqualTo(1), "取消要立刻松开按键");
            Assert.That(performance.Running, Is.False);
        });
    }

    /// <summary>
    /// 三档倒计时（3 / 5 / 10 秒）都按用户选的秒数等满才发第一个音，第一个音不早于所选秒数。
    /// 上界按假时钟的步长给（<c>TierStepSeconds × 8</c>）而不是死数：假时钟是共享的，
    /// 别的线程的等待循环也在读它，所以 <c>按下[0].At</c> 不是「倒计时等了多久」的精确读数；
    /// 卡死「等没等够」的是下界，上界只拦量级错误。
    /// </summary>
    [TestCase(3.0)]
    [TestCase(5.0)]
    [TestCase(10.0)]
    public void 三档倒计时都等满了才发第一个音(double 倒计时秒)
    {
        var clock = new FakeClock { AutoStepSeconds = TierStepSeconds };
        var sink = new RecordingEventSink(clock);
        var performance = new StartPerformance(clock, sink);

        using var finished = new ManualResetEventSlim(false);
        performance.Finished += () => finished.Set();

        Assert.That(performance.Start(请求(倒计时秒), elevated: true, imeInChinese: false),
            Is.EqualTo(PerformanceStartOutcome.Started));

        Assert.That(finished.Wait(TimeSpan.FromSeconds(20)), Is.True, "一场短曲子放不完");

        // 只取音键：修饰键（鼠标）在倒计时终点之前就有，拿它算会误判成发早了
        var 按下 = sink.KeyEvents.Where(e => e.Down).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(按下, Is.Not.Empty, "倒计时走完了，第一个音却没发出去");
            Assert.That(按下[0].At, Is.GreaterThanOrEqualTo(倒计时秒), "第一个音发得比倒计时终点还早");
            Assert.That(按下[0].At, Is.LessThan(倒计时秒 + TierStepSeconds * 8),
                "等得比所选秒数长太多 —— 倒计时的长度多半没读用户选的那个");
        });
    }

    /// <summary>
    /// 悬浮层那个大数字的第一帧就是用户选的那一档。
    /// 用手动时钟读一次，避开线程启动那几毫秒的竞争。
    /// </summary>
    [TestCase(3.0)]
    [TestCase(5.0)]
    [TestCase(10.0)]
    public void 倒计时的大数字从选的档位开始数(double 倒计时秒)
    {
        var clock = new FakeClock();                      // 手动：时钟不走，倒计时永远走不完
        var performance = new StartPerformance(clock, new RecordingEventSink(clock));

        performance.Start(请求(倒计时秒), elevated: true, imeInChinese: false);

        Assert.That(performance.CountdownSecondsLeft, Is.EqualTo((int)倒计时秒));

        performance.Stop();                                // 把那条还在等的线程收掉
    }

    /// <summary>三档倒计时那几条用的假时钟步长（秒）。</summary>
    private const double TierStepSeconds = 0.25;

    private static StartPerformanceRequest 请求(double 倒计时秒)
        => new(短曲(), TrackIndex: 0, BaseOctave: null, InputTiming.Standard, 倒计时秒);

    /// <summary>
    /// 240BPM、480 tick/四分音符 → 480 tick = 0.25 秒，整曲 0.125 秒。
    /// 一个音摆在 tick 0，映射出来的事件目标时刻就在起跑点上，「有没有等倒计时」一看便知。
    /// </summary>
    private static Song 短曲() => MidiReader.ReadBytes(SmfWriter.Build(1, 480,
        SmfTrack.Named("旋律")
            .Tempo(0, 250_000)
            .Note(0, 240, 0, 60)));
}

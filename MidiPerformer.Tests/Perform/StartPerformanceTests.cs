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
/// 起一场演奏 —— 这一组的重点是**盲倒计时那几秒**：它是用户切到游戏窗口的窗口期，
/// 期间一个音都不能发出去（发了就是弹给桌面听，而且用户还没到游戏里）。
///
/// 全部用假时钟 + <see cref="RecordingEventSink"/>，**不等真时间**：
/// 时钟每被读一次自己前进 5ms（<c>FakeClock.AutoStepSeconds</c>），
/// 等待循环就是靠读时钟推进的（见 <c>StartPerformance.WaitCountdown</c> 的说明）。
/// 只有「倒计时里空转一会儿」那一处要一点真实时间 —— 那是留给一个不等倒计时的实现
/// 去犯错的机会，不留它这一条就拦不住人。
/// </summary>
public class StartPerformanceTests
{
    /// <summary>假时钟每被读一次前进的秒数 —— 和 DispatcherTests 取同一个值。</summary>
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

        // 不放行的那条路上根本没有线程，所以这里等的是「本来就不该发生的事」。
        // 不等也能过，但等一会儿才拦得住「偷偷起了一条线程」的那种实现。
        Thread.Sleep(50);
        Assert.That(sink.Sent, Is.Empty, "预检没放行，却发了按键");
    }

    /// <summary>
    /// 倒计时没走完，一个音都不能发 —— <b>这是这一块最要紧的那条</b>。
    ///
    /// 假时钟每读一次走 0.1 秒，走到 1 秒就停住（<c>AutoStopAtSeconds</c>），
    /// 而倒计时的终点在 3 秒 —— 于是它卡在等待里空转，读数稳定地停在「还剩 2 秒」。
    /// 曲子刻意把第一个音摆在**音乐 0 秒**：一个不等倒计时的实现会当场把它发出去，
    /// 摆在 0.25 秒的话它反而「还没来得及发」，这一条就白测了。
    /// </summary>
    [Test]
    public void 倒计时没走完一个音都不发()
    {
        var clock = new FakeClock { AutoStepSeconds = 0.1, AutoStopAtSeconds = 1.0 };
        var sink = new RecordingEventSink(clock);
        var performance = new StartPerformance(clock, sink);

        var outcome = performance.Start(请求(倒计时秒: 3), elevated: true, imeInChinese: false);
        Assert.That(outcome, Is.EqualTo(PerformanceStartOutcome.Started));

        Thread.Sleep(120);                  // 留给「不等倒计时」的实现一个犯错的机会

        Assert.Multiple(() =>
        {
            Assert.That(sink.Sent, Is.Empty, "倒计时还没走完就把音发出去了");
            Assert.That(sink.ReleaseAllCount, Is.EqualTo(0), "倒计时期间不该碰键盘");
            Assert.That(performance.Running, Is.True, "还在倒计时，却已经算收场了");

            // 悬浮层那个大数字：3 秒的倒计时走到还剩 2 秒。等于 3 说明它没在走，
            // 等于 0 说明它已经当成倒计时结束了 —— 两种都是用户看得见的错。
            Assert.That(performance.CountdownSecondsLeft, Is.EqualTo(2), "悬浮层的大数字不对");
        });

        performance.Stop();
    }

    /// <summary>
    /// 倒计时走完，第一个音**真的**发出去，而且不早于倒计时终点。
    ///
    /// 倒计时取 0.02 秒而不是 3 秒：等待是分片的，每片最多 5ms 真实时间，
    /// 3 秒的倒计时就要真等 3 秒。0.02 秒照样走完「等到终点才起跑」这条路径，
    /// 却只要等四五个分片 —— 断言的是同一条规则。
    /// </summary>
    [Test]
    public void 倒计时走完就发出第一个音()
    {
        const double countdown = 0.02;

        var clock = new FakeClock { AutoStepSeconds = Step };
        var sink = new RecordingEventSink(clock);
        var performance = new StartPerformance(clock, sink);

        // 先挂上再起跑：Finished 是在派发线程上触发的，起跑之后再挂有错过它的可能。
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

            // 进度条的分母取自整曲时长，分子是「距锚点过了多久」。分子不追着分母跑到头：
            // 最后一个事件本来就在曲尾之前（提前量），收场比它更早 —— 进度条停在九成多
            // 然后切到「已停止」那一态，用户看不到那个差。
            Assert.That(performance.TotalSeconds, Is.EqualTo(0.125).Within(1e-9), "进度条的分母不对");
            Assert.That(performance.MusicNow, Is.GreaterThan(0), "进度一直没走");
        });
    }

    /// <summary>
    /// 倒计时期间急停 = 取消：派发器还没出生，喊停的只有那个取消信号。
    /// 这里用手动假时钟（时间不自己走），所以等待循环原地打转 —— 唯一能解开它的是 <see cref="StartPerformance.Stop"/>。
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

    // ==================== 夹具 ====================

    private static StartPerformanceRequest 请求(double 倒计时秒)
        => new(短曲(), TrackIndex: 0, BaseOctave: null, InputTiming.Standard, 倒计时秒);

    /// <summary>
    /// 240BPM、480 tick/四分音符 → 480 tick = 0.25 秒。一个音摆在 tick 0：
    /// 它映射出来的事件目标时刻就在起跑点上，所以「有没有等倒计时」一看便知（见上面那条测试）。
    /// 整曲 0.125 秒，放完不用等真时间。
    /// </summary>
    private static Song 短曲() => SongProject.ReadBytes(SmfWriter.Build(1, 480,
        SmfTrack.Named("旋律")
            .Tempo(0, 250_000)
            .Note(0, 240, 0, 60)));
}

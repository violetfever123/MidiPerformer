using MidiPerformer.Core.Ports.Outbound;

namespace MidiPerformer.Tests.Fakes;

/// <summary>
/// 假时钟：时刻由测试说了算，一秒真时间都不用等。
///
/// **两种驱动方式，别混着用：**
///
/// <list type="bullet">
/// <item><b>手动</b>（默认）—— 测试自己调 <see cref="Advance"/> / 设 <see cref="Seconds"/>。
/// 适合把被测代码放在当前线程上跑、由测试一步步推进的单线程场景。</item>
/// <item><b>自动</b>（设了 <see cref="AutoStepSeconds"/>）—— **每次读 <see cref="NowSeconds"/> 都自动往前跳一步**。
/// 适合被测代码跑在别的线程上、循环里反复读时钟的场景（<c>Dispatcher</c> 就是）：
/// 那种循环「等时钟到某个时刻」，手动模式下时钟不动，它会一直转下去，测试挂死。
/// 自动步进让它每问一次就往前挪一点，于是循环必然推进到终点，而且**不花真实时间**。</item>
/// </list>
///
/// 线程安全：驱动线程和被测线程通常不是同一个，读改写一律加锁。
/// </summary>
public sealed class FakeClock : IClock
{
    private readonly object _gate = new();
    private double _seconds;

    /// <summary>每读一次 <see cref="NowSeconds"/> 自动前进的秒数。null（默认）= 手动模式。</summary>
    public double? AutoStepSeconds { get; set; }

    /// <summary>
    /// 自动模式下允许被推进到的上限。到了就不再前进 —— 给「必须停下」留一条硬底线，
    /// 免得被测代码死循环时测试也陪着转到天荒地老。
    /// </summary>
    public double AutoStopAtSeconds { get; set; } = double.PositiveInfinity;

    /// <summary>当前时刻（秒）。可读可写。</summary>
    public double Seconds
    {
        get { lock (_gate) return _seconds; }
        set { lock (_gate) _seconds = value; }
    }

    public double NowSeconds()
    {
        lock (_gate)
        {
            double now = _seconds;
            if (AutoStepSeconds is double step)
            {
                double next = _seconds + step;
                _seconds = next > AutoStopAtSeconds ? AutoStopAtSeconds : next;
            }
            return now;
        }
    }

    /// <summary>往前推 <paramref name="seconds"/> 秒（手动模式用）。</summary>
    public void Advance(double seconds)
    {
        lock (_gate) _seconds += seconds;
    }

    /// <summary>往前推 <paramref name="ms"/> 毫秒（手动模式用；测试里写毫秒比写 0.033 好读）。</summary>
    public void AdvanceMs(double ms) => Advance(ms / 1000.0);
}

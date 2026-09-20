using MidiPerformer.Core.Ports.Outbound;

namespace MidiPerformer.Tests.Fakes;

/// <summary>
/// 假时钟：时刻由测试说了算，不花真实时间。
/// 默认手动模式，由测试调 <see cref="Advance"/> / 设 <see cref="Seconds"/>；
/// 设了 <see cref="AutoStepSeconds"/> 则是自动模式，每读一次 <see cref="NowSeconds"/> 就自动前进一步，
/// 供跑在别的线程上、循环读时钟的被测代码使用。线程安全，读改写一律加锁。
/// </summary>
public sealed class FakeClock : IClock
{
    private readonly object _gate = new();
    private double _seconds;

    /// <summary>每读一次 <see cref="NowSeconds"/> 自动前进的秒数。null（默认）= 手动模式。</summary>
    public double? AutoStepSeconds { get; set; }

    /// <summary>自动模式下允许被推进到的上限，到了就不再前进。</summary>
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

    /// <summary>往前推 <paramref name="ms"/> 毫秒（手动模式用）。</summary>
    public void AdvanceMs(double ms) => Advance(ms / 1000.0);
}

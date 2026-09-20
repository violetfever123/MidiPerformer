using System.Diagnostics;
using MidiPerformer.Core.Ports.Outbound;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// <see cref="IClock"/> 的真实实现：构造即起表的系统单调时钟，底层是 <see cref="Stopwatch"/>。
/// 它只增不减，而 <c>DateTime.Now</c> 会被对时往回拨，播放头也会跟着倒退。
/// </summary>
public sealed class SystemClock : IClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public double NowSeconds() => _watch.Elapsed.TotalSeconds;
}

using System.Diagnostics;
using MidiPerformer.Core.Ports.Outbound;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// <see cref="IClock"/> 的真实实现 —— 系统单调时钟。
///
/// 用 <see cref="Stopwatch"/> 而不是 <c>DateTime.Now</c>：后者会被对时 / NTP / 夏令时
/// 往回拨，而 <c>SongWalker</c> 的积分假定时刻单调不减，倒退一下播放头就跟着倒退。
/// <c>Stopwatch</c> 底层是 QPC，只增不减。
///
/// 构造即起表：整个程序共用一个原点，符合端口「同一次运行里只有一个原点」的要求。
/// </summary>
public sealed class SystemClock : IClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public double NowSeconds() => _watch.Elapsed.TotalSeconds;
}

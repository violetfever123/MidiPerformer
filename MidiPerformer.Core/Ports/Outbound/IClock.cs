namespace MidiPerformer.Core.Ports.Outbound;

/// <summary>
/// 墙上钟。演奏器和试听都要按时间戳等待，等待的判据就是它。
///
/// **出站端口**：用例层引用、适配层实现（依赖倒置）。用例层不 new 任何时钟，
/// 于是 <c>Dispatcher</c> 能拿假时钟跑测试 —— 演奏器接管键盘、发到游戏里，
/// 跑起来没法调试，这种代码测试价值最高。
/// </summary>
public interface IClock
{
    /// <summary>
    /// 从某个任意原点起累计的秒数。
    ///
    /// **必须单调不减。** 原点在哪不管（程序启动、系统开机都行），只要求同一次运行里
    /// 只有一个原点 —— <c>SongWalker</c> 的积分拿两次读数相减当流逝时长，
    /// 被往回拨一下，播放头就跟着倒退（见 <c>SongWalker.AdvanceTo</c> 的说明）。
    /// 这也是 <c>SystemClock</c> 用 <c>Stopwatch</c> 而不是 <c>DateTime.Now</c> 的原因。
    /// </summary>
    double NowSeconds();
}

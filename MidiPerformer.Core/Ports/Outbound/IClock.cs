namespace MidiPerformer.Core.Ports.Outbound;

/// <summary>
/// 墙上钟。演奏器和试听都要按时间戳等待，等待的判据就是它。
/// 出站端口：用例层引用、适配层实现，用例层不 new 任何时钟，于是能拿假时钟跑测试。
/// </summary>
public interface IClock
{
    /// <summary>
    /// 从某个任意原点起累计的秒数，原点在哪不管，只要求同一次运行里只有一个。
    ///
    /// 必须单调不减：<c>SongWalker</c> 的积分拿两次读数相减当流逝时长，往回拨一下播放头就倒退。
    /// 这也是实现用 <c>Stopwatch</c> 而不是 <c>DateTime.Now</c> 的原因。
    /// </summary>
    double NowSeconds();
}

using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Tests.Fakes;

/// <summary>
/// 记录型 sink：记下每次 <see cref="Send"/> 的内容与发出时刻，<see cref="ReleaseAll"/> 只计数，
/// 让用它的代码不必碰真键盘。时刻取自构造时注入的时钟，配 <see cref="FakeClock"/> 用。
/// </summary>
public sealed class RecordingEventSink : IEventSink
{
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly List<SentEvent> _sent = new();
    private int _releaseAllCount;

    public RecordingEventSink(IClock clock) => _clock = clock;

    /// <summary>一条已发出的事件：`At` 是发出时的物理时刻（秒）。</summary>
    public sealed record SentEvent(double At, int Kind, char Code, bool Down, string Label);

    /// <summary>已发出的事件，按发出顺序（不是按时间戳顺序）。</summary>
    public IReadOnlyList<SentEvent> Sent
    {
        get { lock (_gate) return _sent.ToArray(); }
    }

    /// <summary><see cref="ReleaseAll"/> 被调了几次（急停、看门狗、放完收尾都要求它 ≥ 1）。</summary>
    public int ReleaseAllCount
    {
        get { lock (_gate) return _releaseAllCount; }
    }

    /// <summary>只取音键事件（<c>K_Key</c>），按发出顺序。</summary>
    public IReadOnlyList<SentEvent> KeyEvents
        => Sent.Where(e => e.Kind == EventBuilder.K_Key).ToArray();

    /// <summary>把记录清空，不清 <see cref="ReleaseAllCount"/>。</summary>
    public void Clear()
    {
        lock (_gate) _sent.Clear();
    }

    public void Send(EventBuilder.PhysicalEvent e)
    {
        lock (_gate)
            _sent.Add(new SentEvent(_clock.NowSeconds(), e.Kind, e.Code, e.Down, e.Label));
    }

    public void ReleaseAll()
    {
        lock (_gate) _releaseAllCount++;
    }
}

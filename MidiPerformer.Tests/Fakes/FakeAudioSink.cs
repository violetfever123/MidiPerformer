using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Tests.Fakes;

/// <summary>
/// 假声卡：把 winmm 换成三个可以断言的计数器。
///
/// winmm 出声没法断言，所以缝开在 <see cref="IAudioSink"/> 上；但试听那条路上
/// 真正容易错的是**时间积分**（<c>SongWalker</c>，与演奏器共用），
/// 拿它测一次，两边都覆盖到。
/// </summary>
public sealed class FakeAudioSink : IAudioSink
{
    private readonly object _gate = new();
    private readonly List<MappedNote> _played = new();

    /// <summary>最近一次 <see cref="Play"/> 收到的音符；没播过就是空表。</summary>
    public IReadOnlyList<MappedNote> LastPlayed
    {
        get { lock (_gate) return _played.ToArray(); }
    }

    public int PlayCount { get; private set; }
    public int StopCount { get; private set; }

    /// <summary>最近一次 <see cref="Seek"/> 的音乐时间（秒）。没跳转过就是 null。</summary>
    public double? LastSeek { get; private set; }

    public void Play(IReadOnlyList<MappedNote> notes)
    {
        lock (_gate)
        {
            PlayCount++;
            _played.Clear();
            _played.AddRange(notes);
        }
    }

    public void Stop()
    {
        lock (_gate) StopCount++;
    }

    public void Seek(double musicSeconds)
    {
        lock (_gate) LastSeek = musicSeconds;
    }
}

using MidiPerformer.Core.Ports.Outbound;

namespace MidiPerformer.Tests.Fakes;

/// <summary>
/// 假声卡：把 winmm 换成几个可以断言的计数器。
///
/// winmm 出声没法断言，所以缝开在 <see cref="IAudioSink"/> 上；但试听那条路上
/// 真正容易错的是**时间积分**（<c>SongWalker</c>，与演奏器共用），
/// 拿它测一次，两边都覆盖到。
///
/// 收的是整条 <see cref="PreviewNote"/>（含声道与音色），不是光秃秃一个音高：
/// 「这条轨用哪个音色」正是 16 加进来的那一维，假声卡要是把它滤掉，
/// 那条路就只能靠耳朵验了。
/// </summary>
public sealed class FakeAudioSink : IAudioSink
{
    private readonly object _gate = new();
    private readonly List<PreviewNote> _played = new();

    /// <summary>最近一次 <see cref="Play"/> 收到的音符（含声道与音色）；没播过就是空表。</summary>
    public IReadOnlyList<PreviewNote> LastPlayed
    {
        get { lock (_gate) return _played.ToArray(); }
    }

    public int PlayCount { get; private set; }
    public int StopCount { get; private set; }

    /// <summary>最近一次 <see cref="Seek"/> 的音乐时间（秒）。没跳转过就是 null。</summary>
    public double? LastSeek { get; private set; }

    public void Play(IReadOnlyList<PreviewNote> notes)
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

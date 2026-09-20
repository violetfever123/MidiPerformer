using MidiPerformer.Core.Ports.Outbound;

namespace MidiPerformer.Tests.Fakes;

/// <summary>
/// 假声卡：把 winmm 换成几个可断言的计数器，记下最近一次 <see cref="Play"/> 收到的整条
/// <see cref="PreviewNote"/>（含声道与音色）。
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

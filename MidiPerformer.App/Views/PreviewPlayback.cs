using Avalonia.Threading;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Preview;
using MidiPerformer.Core.UseCases.Timeline;

namespace MidiPerformer.App.Views;

/// <summary>
/// 试听播放：把「现在播到哪儿了」一帧一帧地推给界面，同时把声音交给 <see cref="IAudioSink"/>。
///
/// **时间积分一行都不自己算**：走的是演奏器同源的 <see cref="SongWalker"/> ——
/// 变速、跳转、曲子自己的速度表都在它里面。这里只做三件事：
/// 每一帧把墙上钟喂给它、把光标位置报出去、走到曲尾就停。
///
/// 两条时间线并排跑：声音在 <c>WinmmPreview</c> 自己的线程上按音乐时间发 NoteOn / NoteOff，
/// 界面这一条用 <c>DispatcherTimer</c> 每 1/30 秒推进一次。两边的音乐时间是同一个数
/// （起播时用 <c>Seek</c> 对齐），差的是线程调度那几毫秒 —— 试听够用，
/// 真要逐毫秒对齐得让声音那头反过来驱动界面，那是演奏器（06）的事。
/// </summary>
public sealed class PreviewPlayback : IDisposable
{
    /// <summary>界面这一条时间线多久推一次。30Hz 足够让播放头看着是滑的。</summary>
    private const double FrameSeconds = 1.0 / 30;

    private readonly IAudioSink _sink;
    private readonly IClock _clock;
    private readonly DispatcherTimer _timer;

    private SongWalker? _walker;
    private IReadOnlyList<PreviewNote> _notes = Array.Empty<PreviewNote>();

    public PreviewPlayback(IAudioSink sink, IClock clock)
    {
        _sink = sink;
        _clock = clock;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(FrameSeconds) };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>正在播。</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>装好曲子了没有。没装的时候「播放」按钮该是灰的。</summary>
    public bool HasSong => _walker is not null;

    /// <summary>播放头在哪（tick）。没装曲子是 0。</summary>
    public long PlayheadTick => _walker?.TickNow ?? 0;

    /// <summary>播放位置（音乐时间，秒）。</summary>
    public double MusicSeconds => _walker?.MusicNow ?? 0;

    /// <summary>整曲时长（音乐时间，秒）。</summary>
    public double TotalSeconds => _walker?.TotalMusicSeconds ?? 0;

    /// <summary>界面该重画一帧了。播放中每帧一次，停下那一帧也有一次。</summary>
    public event EventHandler? Frame;

    /// <summary>放完了（无循环）—— 已自动停止并松开所有按键，界面该把视图对齐回小节线。</summary>
    public event EventHandler? Finished;

    /// <summary>
    /// 换一首曲子。会先把正在响的音停掉。
    ///
    /// 出声那张表（**每个音带哪个声道、哪个音色**）在用例层摊好，这儿只管拿 ——
    /// 摊法本身有它自己的测试（<c>PreviewMixer</c>），视图这一层不重算一遍。
    /// </summary>
    public void Load(Song song)
    {
        Stop();
        _walker = new SongWalker(song);
        _notes = PreviewMixer.Mix(song);
    }

    /// <summary>
    /// 从当前位置开始播。
    ///
    /// 已经播到（或停在）曲尾时**从头再来** —— 否则按下去什么也不会发生，
    /// 而「按了没反应」比「从头再放一遍」难懂得多。
    /// </summary>
    public void Play()
    {
        if (_walker is null) return;

        double now = _clock.NowSeconds();
        if (_walker.MusicNow >= _walker.TotalMusicSeconds) _walker.Seek(0, now);
        else _walker.Seek(_walker.MusicNow, now);   // 重新落锚：不落的话下一步会补上开机以来的时长

        _sink.Seek(_walker.MusicNow);   // 声音那头从同一个音乐时间起算
        _sink.Play(_notes);
        IsPlaying = true;
        _timer.Start();
        Frame?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>停止并松开所有正在响的音。**无循环**：放完走的也是这里。</summary>
    public void Stop()
    {
        _timer.Stop();
        _sink.Stop();
        IsPlaying = false;
    }

    /// <summary>跳到某个音乐时间（秒）。播放中跳会让声音从新位置接着排。</summary>
    public void SeekSeconds(double musicSeconds)
    {
        if (_walker is null) return;
        _walker.Seek(musicSeconds, _clock.NowSeconds());
        _sink.Seek(_walker.MusicNow);
    }

    public void Dispose()
    {
        _timer.Tick -= OnTimerTick;
        Stop();
    }

    /// <summary>
    /// 每一帧：把墙上钟喂给积分器，走到曲尾就自动停下来。
    /// </summary>
    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_walker is null) return;

        _walker.AdvanceTo(_clock.NowSeconds());

        if (_walker.Finished)
        {
            // 放完自动停止 + 松开所有按键（IAudioSink 的约定）。光标**留在原地**，
            // 别自己跳回开头：人看着曲子放完，光标就应该停在末尾。
            Stop();
            Finished?.Invoke(this, EventArgs.Empty);
        }

        Frame?.Invoke(this, EventArgs.Empty);
    }
}

using Avalonia.Threading;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Preview;
using MidiPerformer.Core.UseCases.Timeline;

namespace MidiPerformer.App.Views;

/// <summary>
/// 试听播放：把「现在播到哪儿了」一帧一帧推给界面，同时把声音交给 <see cref="IAudioSink"/>。
/// 时间积分不自己算，走的是演奏器同源的 <see cref="SongWalker"/>（变速、跳转、曲子的速度表
/// 都在它里面），这里只做三件事：每帧把墙上钟喂给它、把光标位置报出去、走到曲尾就停。
/// 两条时间线并排跑：声音在 <c>WinmmPreview</c> 自己的线程上按音乐时间发 NoteOn / NoteOff，
/// 界面这条用 <c>DispatcherTimer</c> 每 1/30 秒推进一次；两边音乐时间是同一个数
/// （起播时用 <c>Seek</c> 对齐），差的只是线程调度那几毫秒。
/// </summary>
public sealed class PreviewPlayback : IDisposable
{
    /// <summary>界面这条时间线多久推一次，30Hz 足够让播放头看着是滑的。</summary>
    private const double FrameSeconds = 1.0 / 30;

    private readonly IAudioSink _sink;
    private readonly IClock _clock;
    private readonly DispatcherTimer _timer;

    private SongWalker? _walker;
    private IReadOnlyList<PreviewNote> _notes = Array.Empty<PreviewNote>();

    /// <summary>手上这份谱面，只为了静音名单变了时按同一份曲子重摊那张表。</summary>
    private Song? _song;

    public PreviewPlayback(IAudioSink sink, IClock clock)
    {
        _sink = sink;
        _clock = clock;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(FrameSeconds) };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>正在播。</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>
    /// 停在半路（<see cref="Pause"/> 过，还没接着放也没停）。
    /// 和「没在放」不是一回事：<see cref="Stop"/> 之后也是「没在放」，但那一路是「这段听完了」，
    /// 界面跟着把视野拉回小节线；暂停是「停在这，等下接着听」，视野一个像素都不许动。
    /// 按钮上的 `▶ 继续` 和 `▶ 播放` 也靠这个分开。
    /// </summary>
    public bool IsPaused { get; private set; }

    /// <summary>装好曲子了没有，没装的时候「播放」按钮该是灰的。</summary>
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
    /// 换一首曲子，会先把正在响的音停掉。
    /// 出声那张表（每个音带哪个声道、哪个音色）在用例层摊好，这儿只管拿。
    /// </summary>
    /// <param name="mutedTracks">
    /// 不发声的轨（收起来的那几条），按 <c>(轨块号, 声道)</c> 给。换曲子这一路是空的：
    /// 折叠是用户对某一条轨的标记，不该跨曲子漏过去（见 <c>MainWindow.SyncLanes</c>）。
    /// </param>
    public void Load(Song song, IReadOnlySet<(int TrackIndex, int Channel)> mutedTracks)
    {
        Stop();
        _song = song;
        _walker = new SongWalker(song);
        // 长度也跟这份名单走：收起来的轨不出声，它多出来的小节不该继续空转（见 AudibleLength）
        _walker.SetEndTick(AudibleLength.EndTick(song, mutedTracks));
        _notes = PreviewMixer.Mix(song, mutedTracks);
    }

    /// <summary>
    /// 只换那份「哪几条轨不发声」的名单，曲子不动；折叠 / 展开一条轨走这条。
    /// 正在播的话接着放，从此刻的音乐时间把新那张表重排一遍：先 <c>Stop</c>（松开正在响的音）
    /// 再 <c>Seek</c> + <c>Play</c>，和起播那条路同一个次序 —— 光换掉 <c>_notes</c> 的话，
    /// 出声那头手上还是上一批音，被静音的轨会一直响到它自己结束。
    /// 曲尾也跟着这份名单重算：收起来的轨要是本来就比别的长，长度当场变短，
    /// 「剩下的小节里一条轨都不出声」于是立刻生效 —— 正在播的话下一次 <c>OnTimerTick</c>
    /// 就会看见 <see cref="SongWalker.Finished"/>、停钟、报 <see cref="Finished"/>；
    /// 暂停中则停在原地（把播放头拉回范围内是窗口的事，见 <c>MainWindow.OnLaneCollapseChanged</c>）。
    /// </summary>
    public void SetMutedTracks(IReadOnlySet<(int TrackIndex, int Channel)> mutedTracks)
    {
        if (_song is not { } song || _walker is not { } walker) return;

        _notes = PreviewMixer.Mix(song, mutedTracks);
        walker.SetEndTick(AudibleLength.EndTick(song, mutedTracks));

        if (!IsPlaying) return;

        // 先把积分推到此刻：不推的话声音会从上一帧（最多 33ms 前）接着排，听着像重放了一小段
        walker.AdvanceTo(_clock.NowSeconds());
        _sink.Stop();
        _sink.Seek(walker.MusicNow);
        _sink.Play(_notes);
    }

    /// <summary>
    /// 从当前位置开始播。从头开始、<see cref="Pause"/> 之后接着放、<see cref="Stop"/> 之后再放，
    /// 三种情况走的是同一句 <c>Seek(MusicNow)</c>，只在「当前位置在哪」上有区别。
    /// 已经播到（或停在）曲尾时从头再来 —— 否则按下去什么也不会发生。
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
        IsPaused = false;
        _timer.Start();
        Frame?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 停在原地。声音松开，播放头一个 tick 都不动 —— 再 <see cref="Play"/> 就从这儿接着放
    /// （位置一直在 <c>_walker</c> 身上，不用在这儿另存一个暂停点，那会多出第二个真相源）。
    /// 先 <c>AdvanceTo</c> 再停：积分是每帧喂一次墙上钟的，最后一次喂在上一帧（最多 33ms 前），
    /// 不补这一下恢复时会从 33ms 前接上，听着像暂停前那一小段又被放了一遍。
    /// 改静音名单（<see cref="SetMutedTracks"/>）那儿是同一个道理、同一步。
    /// </summary>
    public void Pause()
    {
        if (!IsPlaying || _walker is not { } walker) return;

        walker.AdvanceTo(_clock.NowSeconds());
        _timer.Stop();
        _sink.Stop();
        IsPlaying = false;
        IsPaused = true;
        // 报一帧：按钮上的字（⏸ → ▶ 继续）是靠这一帧刷的
        Frame?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>停止并松开所有正在响的音。无循环：放完走的也是这里。</summary>
    public void Stop()
    {
        _timer.Stop();
        _sink.Stop();
        IsPlaying = false;
        // 停是「这段听完了」，不是「停在这」，所以暂停态一并清掉：
        // 否则放完自动停之后再按空格会显示「继续」，而那一路的语义已经变了
        IsPaused = false;
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

    /// <summary>每一帧：把墙上钟喂给积分器，走到曲尾就自动停下来。</summary>
    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_walker is null) return;

        _walker.AdvanceTo(_clock.NowSeconds());

        if (_walker.Finished)
        {
            // 放完自动停止 + 松开所有按键（IAudioSink 的约定）；光标留在原地，别自己跳回开头
            Stop();
            Finished?.Invoke(this, EventArgs.Empty);
        }

        Frame?.Invoke(this, EventArgs.Empty);
    }
}

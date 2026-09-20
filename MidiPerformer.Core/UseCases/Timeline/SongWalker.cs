using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.UseCases.Timeline;

/// <summary>
/// 音乐时间 ⇄ 物理时间的积分器。试听和演奏共用它，时间积分、变速、跳转只写一遍。
///
/// 纯数学：物理时刻由调用方喂进来（<see cref="AdvanceTo"/>），不读时钟。
///
/// 三种时间，别混：
/// <list type="bullet">
/// <item>tick —— 谱面的时间，只认它的是模型。</item>
/// <item>音乐时间（秒）—— 算进了曲子自己的速度表（<see cref="TempoMap"/>），但不含播放倍速；事件表用的就是它。</item>
/// <item>物理时间（秒）—— 墙上钟，音乐时间 = ∫ 播放倍速 d(物理时间)。</item>
/// </list>
/// </summary>
public sealed class SongWalker
{
    private readonly Song _song;
    private double _anchorPhysical;   // 上一次积分的物理时刻
    private double _musicNow;         // 到 _anchorPhysical 为止的音乐时间
    private long _endTick;            // 曲尾（见 EndTick）

    public SongWalker(Song song, double speed = 1.0)
    {
        _song = song;
        _endTick = song.EndTick;
        Speed = Clamp(speed);
    }

    public Song Song => _song;

    public TempoMap TempoMap => _song.TempoMap;

    /// <summary>播放倍速（1.0 = 原速）。BPM 是谱面，倍速是播放器，两回事。</summary>
    public double Speed { get; private set; }

    /// <summary>当前音乐时间（秒）。</summary>
    public double MusicNow => _musicNow;

    /// <summary>当前 tick。由音乐时间经速度表换算，变速点上不会跳变。</summary>
    public long TickNow => TempoMap.TickAt(_musicNow);

    /// <summary>
    /// 整曲音乐时长（秒），算到 <see cref="EndTick"/> 为止。不含演奏路径给极短音垫的那 20ms，
    /// 也不含事件表末尾的抬键，所以 <see cref="Finished"/> 可能比最后一个事件早 20ms 左右。
    /// </summary>
    public double TotalMusicSeconds => TempoMap.SecondsAt(_endTick);

    /// <summary>
    /// 曲尾落在哪个 tick。默认 = 整份谱面最后一个音的结束 tick，可以被 <see cref="SetEndTick"/> 改小；
    /// 收起来的轨不出声，它多出来的小节不该继续空转，所以要能搬动。
    /// </summary>
    public long EndTick => _endTick;

    /// <summary>
    /// 改曲尾。只影响 <see cref="TotalMusicSeconds"/> 与 <see cref="Finished"/>，不动积分与锚点 ——
    /// 改的是「曲子多长」，不是「现在放到哪了」。终点改小后当前位置可能落在曲子外面，
    /// 把播放头拉回范围是调用方的事。负数当 0。
    /// </summary>
    public void SetEndTick(long tick) => _endTick = Math.Max(0, tick);

    /// <summary>是否已经走到曲尾。含义见 <see cref="TotalMusicSeconds"/> 的说明。</summary>
    public bool Finished => _musicNow >= TotalMusicSeconds;

    /// <summary>从曲子开头开始，把积分锚点定在 <paramref name="physicalNow"/>。</summary>
    public void Start(double physicalNow) => Seek(0, physicalNow);

    /// <summary>跳到指定音乐时间，锚点一并重置到 <paramref name="physicalNow"/>（否则下一步会补上跳过的时长）。</summary>
    public void Seek(double musicSeconds, double physicalNow)
    {
        _musicNow = Math.Max(0, musicSeconds);
        _anchorPhysical = physicalNow;
    }

    /// <summary>
    /// 改播放倍速。必须传当前物理时刻：改速前要先把旧速度这一段结清，
    /// 否则新速度会追回去把已经走过的时长按新倍速重算一遍。
    /// </summary>
    public void SetSpeed(double speed, double physicalNow)
    {
        AdvanceTo(physicalNow);
        Speed = Clamp(speed);
    }

    /// <summary>
    /// 按物理时刻推进积分。<paramref name="physicalNow"/> 必须是单调不减的时刻 ——
    /// 传一个更早的时刻不会报错，播放头会跟着倒退，这里不做拦截。
    /// </summary>
    public void AdvanceTo(double physicalNow)
    {
        _musicNow += (physicalNow - _anchorPhysical) * Speed;
        if (_musicNow < 0) _musicNow = 0;
        _anchorPhysical = physicalNow;
    }

    /// <summary>若在 <paramref name="physicalNow"/> 时刻走到，音乐时间会是多少。纯查询，不动状态。</summary>
    public double MusicAt(double physicalNow) => _musicNow + (physicalNow - _anchorPhysical) * Speed;

    /// <summary>音乐时间 <paramref name="musicSeconds"/> 对应哪个物理时刻。<see cref="MusicAt"/> 的逆运算。</summary>
    public double PhysicalAt(double musicSeconds) => _anchorPhysical + (musicSeconds - _musicNow) / Speed;

    /// <summary>
    /// 倍速的合法范围：正的有限数。
    /// <c>NaN &lt;= 0</c> 是 false，只写 <c>speed &lt;= 0</c> 会放行 NaN，之后播放器卡死 ——
    /// 界面上的倍速输入框真能递进来一个 NaN。
    /// </summary>
    private static double Clamp(double speed) => double.IsFinite(speed) && speed > 0 ? speed : 1.0;
}

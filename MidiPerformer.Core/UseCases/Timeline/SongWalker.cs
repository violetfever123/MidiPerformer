using MidiPerformer.Core.Model;

namespace MidiPerformer.Core.UseCases.Timeline;

/// <summary>
/// 音乐时间 ⇄ 物理时间的积分器。**试听和演奏共用同一个它** —— 时间积分、变速、跳转
/// 这几件事只写一遍，两边就不会各自跑偏。
///
/// 它放在 <c>Timeline/</c> 而不是 <c>Perform/</c>，就是为了这个共用：留在 <c>Perform/</c> 里
/// 会让试听反过来依赖演奏。
///
/// **纯数学，不依赖任何外部系统。** 物理时刻由调用方喂进来（<c>AdvanceTo</c>），
/// 不读时钟 —— 读时钟是 <c>IClock</c> 端口的事，由 Dispatcher 在 03 里注进来。
/// 所以这一整块能用假时钟测，一行真时间都不用等。
///
/// 三种时间，别混：
/// <list type="bullet">
/// <item><b>tick</b> —— 谱面的时间。只认它的是模型。</item>
/// <item><b>音乐时间</b>（秒）—— 已经算进曲子自己的速度表（<see cref="TempoMap"/>），
///   但**不含**播放倍速。事件表用的就是它。</item>
/// <item><b>物理时间</b>（秒）—— 墙上钟。音乐时间 = ∫ 播放倍速 d(物理时间)。</item>
/// </list>
///
/// 变速曲目在这里是免费的：倍速恒定 ⇒ 音乐时间是物理时间的线性函数，
/// 曲子内部的速度变化由 <see cref="TempoMap"/> 在 tick 那一层吃掉。
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

    /// <summary>播放倍速（1.0 = 原速）。与曲子自己的 BPM 是两回事：BPM 是谱面，倍速是播放器。</summary>
    public double Speed { get; private set; }

    /// <summary>当前音乐时间（秒）。</summary>
    public double MusicNow => _musicNow;

    /// <summary>当前 tick。由音乐时间经速度表换算，变速点上不会跳变。</summary>
    public long TickNow => TempoMap.TickAt(_musicNow);

    /// <summary>
    /// 整曲音乐时长（秒）。
    ///
    /// 注意它算到最后一个音的**谱面结束 tick** 为止，不含演奏路径给极短音垫的那 20ms
    /// （见 <c>RepertoireToSeconds.MinNoteSeconds</c>），也不含事件表末尾的抬键。
    /// 所以 <see cref="Finished"/> 可能比最后一个事件早 20ms 左右。03 / 06 要停钟或收尾时，
    /// 以事件表的时间戳为准，别拿它当「最后一个事件已经发完」。
    ///
    /// 终点是 <see cref="EndTick"/>，默认就是整份谱面的末尾；试听那边会把它改小
    /// （折叠起来的轨不算长度，见 <c>AudibleLength</c>）。
    /// </summary>
    public double TotalMusicSeconds => TempoMap.SecondsAt(_endTick);

    /// <summary>
    /// 曲尾落在哪个 tick。默认 = 整份谱面最后一个音的结束 tick，可以被 <see cref="SetEndTick"/> 改小。
    ///
    /// 它是一个**可以搬动的终点**，而不是从 <see cref="Song"/> 现算的 —— 因为「曲子多长」
    /// 和「谱面有多长」不是一回事：收起来的轨不出声，它多出来的那几小节就不该继续空转。
    /// </summary>
    public long EndTick => _endTick;

    /// <summary>
    /// 改曲尾。**只影响 <see cref="TotalMusicSeconds"/> 与 <see cref="Finished"/>，
    /// 不动积分、不动锚点** —— 改的是「曲子多长」，不是「现在放到哪了」，播放头一个 tick 都不该跳。
    ///
    /// 终点改小之后**当前位置可能落在曲子外面**（正在播的话，下一次 <see cref="Finished"/>
    /// 就是 true），把播放头拉回范围内是调用方的事：积分器只管时间，不管屏幕。
    ///
    /// 负数当 0（空曲就是 0）。
    /// </summary>
    public void SetEndTick(long tick) => _endTick = Math.Max(0, tick);

    /// <summary>是否已经走到曲尾。含义见 <see cref="TotalMusicSeconds"/> 的说明。</summary>
    public bool Finished => _musicNow >= TotalMusicSeconds;

    /// <summary>从曲子开头开始，把积分锚点定在 <paramref name="physicalNow"/>。</summary>
    public void Start(double physicalNow) => Seek(0, physicalNow);

    /// <summary>跳到指定音乐时间。锚点一并重置到 <paramref name="physicalNow"/>，否则下一步会一次性补上跳过的时长。</summary>
    public void Seek(double musicSeconds, double physicalNow)
    {
        _musicNow = Math.Max(0, musicSeconds);
        _anchorPhysical = physicalNow;
    }

    /// <summary>
    /// 改播放倍速。**必须传当前物理时刻**：改速之前要先把旧速度这一段结清，
    /// 否则新速度会追回去把已经走过的时长按新倍速重算一遍 —— 播放中拖速度条时，
    /// 这一下会让播放头原地倒退或前跳。
    /// </summary>
    public void SetSpeed(double speed, double physicalNow)
    {
        AdvanceTo(physicalNow);
        Speed = Clamp(speed);
    }

    /// <summary>
    /// 按物理时刻推进积分。工作线程每一步调一次。
    ///
    /// <paramref name="physicalNow"/> 必须是**单调不减**的时刻（03 的 <c>IClock</c> 就是）。
    /// 传一个比上次早的时刻不会报错，播放头会跟着**倒退** —— 这里不做拦截，
    /// 因为静默容忍反而会把调用方的时钟错误藏起来；真出现就是时钟坏了，该在那一层查。
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
    ///
    /// <c>NaN &lt;= 0</c> 是 false，所以只写 <c>speed &lt;= 0</c> 的话 NaN 会被放行，
    /// 然后 <c>_musicNow</c> 变成 NaN、<c>Finished</c> 永远是 false、<c>TickNow</c> 是垃圾 ——
    /// 播放器卡死且不自愈。而 <c>double.Parse("NaN")</c> 是**会成功**的，
    /// 所以界面上的倍速输入框真能递进来一个 NaN。
    /// </summary>
    private static double Clamp(double speed) => double.IsFinite(speed) && speed > 0 ? speed : 1.0;
}

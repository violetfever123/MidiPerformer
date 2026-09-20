namespace MidiPerformer.Core.Model;

/// <summary>时间分辨率：曲子里一个四分音符切成多少 tick。</summary>
/// <remarks>
/// 两种模式二选一：PPQ（<see cref="TicksPerQuarterNote"/>）是绝大多数文件的写法、速度表说了算；
/// SMPTE（<see cref="SmpteFramesPerSecond"/> × <see cref="SmpteTicksPerFrame"/>）按秒切、与速度无关。
/// </remarks>
public sealed record TimeDivision
{
    private TimeDivision(int ticksPerQuarterNote, int framesPerSecond, int ticksPerFrame)
    {
        TicksPerQuarterNote = ticksPerQuarterNote;
        SmpteFramesPerSecond = framesPerSecond;
        SmpteTicksPerFrame = ticksPerFrame;
    }

    /// <summary>PPQ：每四分音符多少 tick。</summary>
    public int TicksPerQuarterNote { get; }

    /// <summary>SMPTE：每秒多少帧（0 = 不是 SMPTE）。</summary>
    public int SmpteFramesPerSecond { get; }

    /// <summary>SMPTE：每帧多少 tick。</summary>
    public int SmpteTicksPerFrame { get; }

    public bool IsSmpte => SmpteFramesPerSecond > 0;

    /// <summary>PPQ 模式。</summary>
    public static TimeDivision PulsesPerQuarter(int ticksPerQuarterNote)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerQuarterNote, 1);
        return new TimeDivision(ticksPerQuarterNote, 0, 0);
    }

    /// <summary>SMPTE 模式。</summary>
    public static TimeDivision Smpte(int framesPerSecond, int ticksPerFrame)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(framesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerFrame, 1);
        return new TimeDivision(0, framesPerSecond, ticksPerFrame);
    }
}

/// <summary>一次变速：从 <paramref name="Tick"/> 起，一个四分音符占 <paramref name="MicrosecondsPerQuarterNote"/> 微秒。</summary>
/// <remarks>
/// 存微秒而不是 BPM 小数：MIDI 文件里本来就是微秒，存 BPM 要先除再乘，一个来回就可能把
/// tick → 秒 算歪最后一两个微秒。BPM 只是显示形式，见 <see cref="BeatsPerMinute"/>。
/// </remarks>
public sealed record TempoChange(long Tick, long MicrosecondsPerQuarterNote)
{
    /// <summary>这个速度等于多少 BPM（显示用）。</summary>
    public double BeatsPerMinute => 60_000_000.0 / MicrosecondsPerQuarterNote;
}

/// <summary>一次变拍：从 <paramref name="Tick"/> 起，每小节 <paramref name="Numerator"/> 拍、以 <paramref name="Denominator"/> 分音符为一拍。</summary>
public sealed record TimeSignatureChange(long Tick, int Numerator, int Denominator);

/// <summary>
/// 速度表 —— 唯一负责 tick ⇄ 秒 换算的地方。音符只存 tick，「这首曲子多快」完整地住在这里。
///
/// 算法照 DryWetMidi 的 <c>MetricTimeSpanConverter</c> 复刻，只求秒数与原版逐位相同：
/// <see cref="BuildPpq"/> 里那两处形状差异（累积段先乘后除、末段先除后乘）是在对齐浮点舍入。
/// </summary>
public sealed class TempoMap
{
    /// <summary>MIDI 规定的默认速度：500000 微秒/四分音符 = 120 BPM。</summary>
    public const long DefaultMicrosecondsPerQuarterNote = 500_000;

    /// <summary>
    /// 换算结果的上限（微秒）：<see cref="SecondsAt"/> 最后要交给 <c>new TimeSpan(微秒 * 10)</c>，
    /// 所以微秒数不能超过 <see cref="long.MaxValue"/> 的十分之一。
    /// </summary>
    private const double MaxMicroseconds = long.MaxValue / 10.0;

    private static InvalidOperationException TooBig() =>
        new("时间跨度太大，超出了能表示的范围（tick 或秒数是个不可能的值，文件多半损坏了）。");

    private readonly long[] _changeTicks;
    private readonly double[] _accumulatedMicros;   // 每个变速点之前的累计微秒
    private readonly double[] _microsPerTick;       // 该变速点起生效的微秒/tick
    private readonly double[] _ticksPerMicro;       // 上者的倒数
    private readonly double _defaultMicrosPerTick;
    private readonly double _defaultTicksPerMicro;

    /// <param name="division">时间分辨率。</param>
    /// <param name="tempoChanges">变速事件，任意顺序（内部按 tick 排序）。空 = 全曲 120 BPM。</param>
    /// <param name="timeSignatureChanges">变拍事件。只影响显示的小节线，不参与任何时序换算。</param>
    public TempoMap(
        TimeDivision division,
        IEnumerable<TempoChange>? tempoChanges = null,
        IEnumerable<TimeSignatureChange>? timeSignatureChanges = null)
    {
        Division = division;

        // 同一 tick 上的重复变速只留最后一条 —— DryWetMidi 的 ValueLine 就是这个行为。
        var byTick = new SortedDictionary<long, TempoChange>();
        foreach (var c in tempoChanges ?? Enumerable.Empty<TempoChange>())
            byTick[c.Tick] = c;
        // 只处理「tick 0 上是默认速度」这一种冗余（DryWetMidi 的 ValueLine 会丢掉任何与当前值相同的
        // 变速，我们没跟）：漏了也只是让分段表多一个斜率相同的段，算出来的秒数不变。
        if (byTick.TryGetValue(0, out var atZero) &&
            atZero.MicrosecondsPerQuarterNote == DefaultMicrosecondsPerQuarterNote)
        {
            byTick.Remove(0);
        }
        TempoChanges = byTick.Values.ToArray();

        var signatures = new SortedDictionary<long, TimeSignatureChange>();
        foreach (var s in timeSignatureChanges ?? Enumerable.Empty<TimeSignatureChange>())
            signatures[s.Tick] = s;
        TimeSignatureChanges = signatures.Values.ToArray();

        _changeTicks = TempoChanges.Select(c => c.Tick).ToArray();
        _accumulatedMicros = new double[_changeTicks.Length];
        _microsPerTick = new double[_changeTicks.Length];
        _ticksPerMicro = new double[_changeTicks.Length];
        BuildPpq();

        _defaultMicrosPerTick = DefaultMicrosecondsPerQuarterNote / (double)PulsesPerQuarter;
        _defaultTicksPerMicro = 1.0 / _defaultMicrosPerTick;
    }

    public TimeDivision Division { get; }

    /// <summary>变速事件，按 tick 升序。</summary>
    public IReadOnlyList<TempoChange> TempoChanges { get; }

    /// <summary>变拍事件，按 tick 升序。</summary>
    public IReadOnlyList<TimeSignatureChange> TimeSignatureChanges { get; }

    private int PulsesPerQuarter => Division.TicksPerQuarterNote <= 0 ? 1 : Division.TicksPerQuarterNote;

    /// <summary>累计微秒表。逐条对齐 DryWetMidi <c>MetricTempoMapValuesCache.Invalidate</c>。</summary>
    private void BuildPpq()
    {
        double accumulated = 0;
        long lastTick = 0;
        long lastMicrosPerQuarter = DefaultMicrosecondsPerQuarterNote;

        for (int i = 0; i < _changeTicks.Length; i++)
        {
            // 先乘后除：DryWetMidi 的 GetMicroseconds 是 `time * usPerQuarter / (double)tpqn`，
            // 乘法在 long 上完成再转 double，换成先除会差最后一两位。
            accumulated += (double)((_changeTicks[i] - lastTick) * lastMicrosPerQuarter) / PulsesPerQuarter;

            lastMicrosPerQuarter = TempoChanges[i].MicrosecondsPerQuarterNote;
            lastTick = _changeTicks[i];

            _accumulatedMicros[i] = accumulated;
            // 先除后乘：末段用的是这个值，DryWetMidi 在这里就是先除。
            _microsPerTick[i] = lastMicrosPerQuarter / (double)PulsesPerQuarter;
            _ticksPerMicro[i] = 1.0 / _microsPerTick[i];
        }
    }

    /// <summary>该 tick 时刻生效的速度（显示/输入用）。</summary>
    public double BeatsPerMinuteAt(long tick)
    {
        // 这里是「不晚于该 tick 的最后一条」，比 SecondsAt 里的「严格早于」松一格：变速点的语义是
        //「从这一 tick 起改成新速度」，所以换算累计值时该 tick 用旧速度，而「此刻速度是多少」答新速度。
        int i = LastChangeAtOrBefore(tick);
        long micros = i >= 0
            ? TempoChanges[i].MicrosecondsPerQuarterNote
            : DefaultMicrosecondsPerQuarterNote;
        return 60_000_000.0 / micros;
    }

    /// <summary>tick → 秒。</summary>
    /// <exception cref="InvalidOperationException">时间跨度大到微秒数装不进 <see cref="long"/>。</exception>
    public double SecondsAt(long tick)
    {
        if (Division.IsSmpte)
            return tick / ((double)Division.SmpteFramesPerSecond * Division.SmpteTicksPerFrame);

        if (tick == 0) return 0;

        int i = LastChangeBelow(tick);
        double accumulated = i >= 0 ? _accumulatedMicros[i] : 0;
        long lastTick = i >= 0 ? _changeTicks[i] : 0;
        double microsPerTick = i >= 0 ? _microsPerTick[i] : _defaultMicrosPerTick;

        double totalMicros = accumulated + (tick - lastTick) * microsPerTick;

        // DryWetMidi 的 TicksToMicroseconds 在这儿有一道 "Time span is too big." 的越界检查，照搬过来：
        // 少了它，一个被改坏的 tick 会静默地算出 long 溢出后的垃圾值。写成 `!(x < max)` 是为了把 NaN 一起拦下。
        if (!(Math.Abs(totalMicros) < MaxMicroseconds)) throw TooBig();

        // 与 DryWetMidi 同一条路：舍入到整微秒 → 交给 TimeSpan → 取 TotalSeconds，
        // 自己写 `micros / 1e6` 会在最后一两位 ULP 上飘。
        long rounded = (long)Math.Round(totalMicros, MidpointRounding.AwayFromZero);
        return new TimeSpan(rounded * (TimeSpan.TicksPerMillisecond / 1000)).TotalSeconds;
    }

    /// <summary>秒 → tick。是 <see cref="SecondsAt"/> 的逆运算：<c>TickAt(SecondsAt(t)) == t</c>。</summary>
    /// <exception cref="InvalidOperationException"><paramref name="seconds"/> 不是有限数，或大到 tick 装不进 <see cref="long"/>。</exception>
    public long TickAt(double seconds)
    {
        // 非有限数会被下面的 `(long)` 转换变成未定义值，而且一旦进了播放器就再也出不来。
        if (!double.IsFinite(seconds)) throw TooBig();

        if (Division.IsSmpte)
        {
            double ticksPerSecond = (double)Division.SmpteFramesPerSecond * Division.SmpteTicksPerFrame;
            double ticksRaw = seconds * ticksPerSecond;
            if (!(Math.Abs(ticksRaw) < long.MaxValue)) throw TooBig();
            return (long)Math.Round(ticksRaw, MidpointRounding.AwayFromZero);
        }

        if (seconds == 0) return 0;

        double microsRaw = seconds * 1_000_000.0;
        if (!(Math.Abs(microsRaw) < long.MaxValue)) throw TooBig();

        long micros = (long)Math.Round(microsRaw, MidpointRounding.AwayFromZero);
        if (micros == 0) return 0;

        // 找最后一个「累计微秒 < micros」的变速点：比的是累计微秒而不是 tick，
        // 与 DryWetMidi 的 MetricTimeSpanToTicks 一致。
        int i = LastAccumulatedBelowMicros(micros);
        double accumulated = i >= 0 ? _accumulatedMicros[i] : 0;
        long lastTick = i >= 0 ? _changeTicks[i] : 0;
        double ticksPerMicro = i >= 0 ? _ticksPerMicro[i] : _defaultTicksPerMicro;

        return (long)Math.Round(lastTick + (micros - accumulated) * ticksPerMicro, MidpointRounding.AwayFromZero);
    }

    /// <summary>最后一个 <c>Time &lt; tick</c> 的变速点下标（严格小于，无则 -1）。</summary>
    private int LastChangeBelow(long tick) => LastAt(_changeTicks, tick, strict: true);

    /// <summary>最后一个 <c>Time &lt;= tick</c> 的变速点下标（无则 -1）。</summary>
    private int LastChangeAtOrBefore(long tick) => LastAt(_changeTicks, tick, strict: false);

    private int LastAccumulatedBelowMicros(long micros) => LastAt(_accumulatedMicros, (double)micros, strict: true);

    private static int LastAt<T>(T[] sorted, T threshold, bool strict) where T : IComparable<T>
    {
        int lo = 0, hi = sorted.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int cmp = sorted[mid].CompareTo(threshold);
            bool qualifies = strict ? cmp < 0 : cmp <= 0;
            if (qualifies) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }
}

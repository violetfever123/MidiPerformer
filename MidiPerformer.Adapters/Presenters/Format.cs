using System.Globalization;
using MidiPerformer.Core.Ports.Inbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Adapters.Presenters;

/// <summary>
/// 界面上的文案：音名、小节号、拍位、时值、时长、音色名都在这儿拼。
/// 集中一处便于逐条比对与改动（以后要做英文界面只动这里）；不含颜色值，外观归 Tokens.axaml。
/// </summary>
public static class Format
{
    /// <summary>空着的那一格（长破折号）。</summary>
    public const string Placeholder = "—";

    /// <summary>
    /// 读数条右边那行操作提示：未选中任何音时的走带与换轨提示。
    /// 分层的规矩见 <see cref="ReadoutHintEditing"/> 上面那段说明。
    /// </summary>
    public const string ReadoutHintPerforming =
        "空格 播放/暂停 · Shift + 空格 回跳一小节并播放 · Ctrl + ↑ ↓ 换轨";

    /// <summary>
    /// 读数条右边那行操作提示：选中了音（判据是选中集个数 &gt; 0，不是恰好一个）时的编辑提示。
    /// 每一句都要和真按键对得上；全文见 <see cref="ReadoutHintTooltip"/>。
    /// </summary>
    public const string ReadoutHintEditing =
        "← → 移时间（一格 = 十六分）· ↑ ↓ 移音高 · Shift + ← → 改时值 · "
        + "Ctrl + ← → 选同轨前/后一个音 · Delete 删除 · Esc 取消选中";

    /// <summary>
    /// 提示行的 ToolTip：两行合起来的全文（<see cref="ReadoutHintPerforming"/> +
    /// <see cref="ReadoutHintEditing"/>），一条不落。
    /// 撤销 / 重做的快捷键只在「操作」菜单项右侧说一次，这里不再重复。
    /// </summary>
    public const string ReadoutHintTooltip =
        ReadoutHintPerforming + " · " + ReadoutHintEditing;

    /// <summary>
    /// 轨头上移调那一格的读数：几个半音。
    /// 正负号只在真有方向时才出现，零就是零 —— 它同时也是「没移调」这个默认状态的样子。
    /// </summary>
    public static string Transpose(int semitones) => $"{semitones:+0;-0;0} 半音";

    /// <summary>音高：音名 + 简谱记号。</summary>
    public static string Pitch(int pitch) =>
        $"{Music.NoteName(pitch)}（{Music.DegreeName(pitch)}）";

    /// <summary>小节的序号（1 起）。</summary>
    public static string BarNumber(int bar) => bar.ToString(CultureInfo.InvariantCulture);

    /// <summary>小节内的拍位（1 起，带两位小数）。</summary>
    public static string Beat(double beatInBar) =>
        beatInBar.ToString("F2", CultureInfo.InvariantCulture) + " 拍";

    /// <summary>时值，单位拍。</summary>
    public static string Length(double beats) =>
        beats.ToString("F2", CultureInfo.InvariantCulture) + " 拍";

    /// <summary>
    /// 「位置」读数：当前小节 / 总小节。显示的是播放头那一小节，不是视口起始。
    /// </summary>
    public static string Position(int bar, int barCount) =>
        $"{BarNumber(bar)} / {barCount} 小节";

    /// <summary>时长 m:ss。曲子长度以秒给，超过一小时也只进位到分。</summary>
    public static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalMinutes}:{span.Seconds:D2}";
    }

    /// <summary>
    /// 预检不放行时，状态行上那句话：三种失败各说各的下一步动作，用户看完得知道去改什么。
    /// 放行也照样回一句话，不返回空串。
    /// </summary>
    public static string PreflightRefusal(PerformanceStartOutcome outcome) => outcome switch
    {
        PerformanceStartOutcome.NotElevated =>
            "没开始：要以管理员身份运行。不然发的按键会被系统挡在游戏窗口外面 —— 一个音都收不到，还不报错。",
        PerformanceStartOutcome.ImeActive =>
            "当前输入法为中文，请切成英文输入法。",
        PerformanceStartOutcome.NoPlayableTrack =>
            "没开始：这条轨弹不了。口琴一次只响一个音，所以只能弹单声部、不带打击乐的轨。换一条试试。",
        _ => "没开始：预检没放行。"
    };

    /// <summary>轨序号：两位的 <c>01</c>，对齐全靠它。</summary>
    public static string TrackNumber(int oneBased) =>
        oneBased.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>轨头上的「128 音」。</summary>
    public static string NoteCount(int count) => $"{count} 音";

    /// <summary>轨头上的音色名。9 号声道是打击乐，整条都是鼓组，音色号在那一轨没有意义。</summary>
    public static string Timbre(int program, int channel) =>
        channel == 9 ? "标准鼓组 · 通道 10" : ProgramLabel(program);

    /// <summary>
    /// 音色下拉里那一行的写法：<c>口琴 · GM 23</c>。
    /// 必须和轨头上那句话用同一个算法：下拉合上之后显示的就是选中的那一行。
    /// </summary>
    public static string ProgramLabel(int program) => $"{ProgramName(program)} · GM {program + 1}";

    /// <summary>GM 音色名（0 起的音色号 → 中文名）。越界就退回编号，不编一个名字出来。</summary>
    public static string ProgramName(int program) =>
        program >= 0 && program < ProgramNames.Count
            ? ProgramNames[program]
            : $"音色 {program + 1}";

    /// <summary>
    /// MIDI 标准 128 个音色的中文名，顺序即 GM 编号；公开出去是给音色下拉用的。
    /// </summary>
    public static IReadOnlyList<string> ProgramNames { get; } = new string[]
    {
        "大钢琴", "明亮钢琴", "电大钢琴", "酒吧钢琴", "电钢琴 1", "电钢琴 2", "羽管键琴", "击弦古钢琴",
        "钢片琴", "钟琴", "八音盒", "颤音琴", "马林巴", "木琴", "管钟", "扬琴",
        "拉杆风琴", "打击风琴", "摇滚风琴", "教堂管风琴", "簧风琴", "手风琴", "口琴", "探戈手风琴",
        "尼龙弦吉他", "钢弦吉他", "爵士电吉他", "清音电吉他", "闷音电吉他", "过载吉他", "失真吉他", "吉他泛音",
        "原声贝斯", "指弹贝斯", "拨片贝斯", "无品贝斯", "击弦贝斯 1", "击弦贝斯 2", "合成贝斯 1", "合成贝斯 2",
        "小提琴", "中提琴", "大提琴", "低音提琴", "颤音弦乐", "拨弦弦乐", "竖琴", "定音鼓",
        "弦乐合奏 1", "弦乐合奏 2", "合成弦乐 1", "合成弦乐 2", "人声合唱", "人声", "合成人声", "管弦乐齐奏",
        "小号", "长号", "大号", "弱音小号", "圆号", "铜管组", "合成铜管 1", "合成铜管 2",
        "高音萨克斯", "中音萨克斯", "次中音萨克斯", "上低音萨克斯", "双簧管", "英国管", "大管", "单簧管",
        "短笛", "长笛", "竖笛", "排箫", "吹瓶", "尺八", "口哨", "陶笛",
        "方波主音", "锯齿主音", "汽笛主音", "鸣笛主音", "失真主音", "人声主音", "五度主音", "贝斯主音",
        "新世纪音色", "温暖音色", "复合成音色", "合唱音色", "弓弦音色", "金属音色", "光环音色", "扫描音色",
        "雨声", "音轨", "水晶", "氛围", "明亮", "哥布林", "回声", "科幻",
        "西塔琴", "班卓琴", "三味线", "日本筝", "卡林巴", "风笛", "民族提琴", "唢呐",
        "叮当铃", "阿哥哥铃", "钢鼓", "木鱼", "太鼓", "嗵鼓", "合成鼓", "反镲",
        "吉他品噪", "呼吸声", "海浪", "鸟鸣", "电话铃", "直升机", "掌声", "枪声"
    };

    /// <summary>tick → 拍。刻度换算只用四分音符；拍号只影响小节线的位置，不影响「一拍多长」。</summary>
    public static double Beats(long ticks, int ticksPerQuarterNote) =>
        ticksPerQuarterNote <= 0 ? 0 : ticks / (double)ticksPerQuarterNote;

    /// <summary>「抽掉一段」还没在卷帘上拖出范围的时候，那一行里写的话：说清那一段从哪儿来。</summary>
    public const string CutNeedRange = "在这条轨的卷帘上横向拖一段 —— 拖出来的那一段就是要消失的";

    /// <summary>
    /// 某个 tick 落在小节内的哪个位置：整小节头上写「第 5 小节」，
    /// 正好落在拍线上写「第 5 小节第 2 拍」，其余写小数拍「第 5 小节第 1.75 拍」。
    /// 拍从 1 起算，和小节内拍位的读数同一个口径；「一拍」按四分音符算，不看拍号。
    /// </summary>
    public static string BarPosition(long tick, long ticksPerBar, int ticksPerQuarterNote)
    {
        long barTicks = Math.Max(1, ticksPerBar);
        long at = Math.Max(0, tick);
        long bar = at / barTicks;                 // 0 起
        long intoBar = at - bar * barTicks;
        if (intoBar == 0) return $"第 {BarNumber((int)bar + 1)} 小节";

        double beats = Beats(intoBar, ticksPerQuarterNote) + 1;
        return $"第 {BarNumber((int)bar + 1)} 小节第 {BeatCount(beats)} 拍";
    }

    /// <summary>
    /// 一段时间的长度写法：「4 小节」「3 拍」「2 小节 3 拍」「2 小节 1.25 拍」。
    /// 抽掉一段之后后半截提前多少就说这个数。
    /// </summary>
    public static string SpanLength(long ticks, long ticksPerBar, int ticksPerQuarterNote)
    {
        if (ticks <= 0) return "0 拍";

        long barTicks = Math.Max(1, ticksPerBar);
        long bars = ticks / barTicks;
        double beats = Beats(ticks % barTicks, ticksPerQuarterNote);

        var parts = new List<string>();
        if (bars > 0) parts.Add($"{bars} 小节");
        if (beats > 0) parts.Add($"{BeatCount(beats)} 拍");
        return parts.Count == 0 ? "0 拍" : string.Join(" ", parts);
    }

    /// <summary>
    /// 拍数怎么写：整拍给整数（<c>3</c>），非整拍给小数（<c>2.75</c>）。
    /// 不套 <c>F2</c>，整拍写「3.00 拍」多两位；十六分的偏移只要两位小数就够精确。
    /// </summary>
    private static string BeatCount(double beats)
        => Math.Abs(beats - Math.Round(beats)) < 1e-9
            ? ((long)Math.Round(beats)).ToString(CultureInfo.InvariantCulture)
            : beats.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// 「抽掉这一段」按下去会发生什么：删掉哪些音、后面多少音提前、这条轨会短掉几小节。
    /// 收的是 tick 而不是小节号；整小节对齐时说「第 5–8 小节（共 4 小节）」，否则说两端各在小节内的哪个位置。
    /// 这里只说这条轨会短掉几小节，数字由 <see cref="CutPreview"/> 算出，和真跑一遍命令的结果一致。
    /// </summary>
    /// <param name="startTick">要抽掉的那一段的起点（含）。</param>
    /// <param name="endTick">终点（不含）。</param>
    /// <param name="trackNumber">轨号，1 起。</param>
    /// <param name="preview">抽完会怎样，见 <see cref="CutPreview.Of"/>。</param>
    /// <param name="ticksPerBar">一小节多少 tick。</param>
    /// <param name="ticksPerQuarterNote">四分音符多少 tick，拍数换算只用它。</param>
    public static string CutSummary(
        long startTick, long endTick, int trackNumber, CutPreview.Result preview,
        long ticksPerBar, int ticksPerQuarterNote)
    {
        string range = CutRangeLabel(startTick, endTick, ticksPerBar, ticksPerQuarterNote);

        // 一个音都不动：命令原样返回同一份曲子、连撤销都不记一笔，直说，别让人按了等着看变化
        if (!preview.Changes) return $"{range}：这一段里没有音，抽了和没抽一样";

        // 三段分开写、各自可能不出现：硬凑成一句就会出现「后面 0 个提前 4 小节」这种没人看得懂的话
        string shift = SpanLength(endTick - startTick, ticksPerBar, ticksPerQuarterNote);
        var parts = new List<string>();
        if (preview.Deleted > 0) parts.Add($"删掉 {preview.Deleted} 个音");
        if (preview.Trimmed > 0) parts.Add($"在切口上剪短 {preview.Trimmed} 个");
        if (preview.Shifted > 0) parts.Add($"后面 {preview.Shifted} 个提前 {shift}");

        return $"{range}：{string.Join("、", parts)}"
             + $" · 第 {TrackNumber(trackNumber)} 轨 {preview.BarsBefore} → {preview.BarsAfter} 小节";
    }

    /// <summary>
    /// 那一段的写法：两端都落在小节线上时说「第 5–8 小节（共 4 小节）」，
    /// 否则说两端各在小节内的哪儿。
    /// 终点那个 tick 是不含的（照 <c>ISongEditor.CutRange</c> 的约定），
    /// 所以它正好落在小节线上时，说的那一小节是它前面的一小节。
    /// </summary>
    private static string CutRangeLabel(
        long startTick, long endTick, long ticksPerBar, int ticksPerQuarterNote)
    {
        long barTicks = Math.Max(1, ticksPerBar);
        if (startTick % barTicks == 0 && endTick % barTicks == 0 && endTick > startTick)
        {
            long firstBar = startTick / barTicks;        // 0 起
            long lastBar = endTick / barTicks;           // 0 起，不含
            return $"第 {BarNumber((int)firstBar + 1)}–{BarNumber((int)lastBar)} 小节"
                 + $"（共 {lastBar - firstBar} 小节）";
        }

        return $"{BarPosition(startTick, barTicks, ticksPerQuarterNote)}"
             + $" 到 {BarPosition(endTick, barTicks, ticksPerQuarterNote)}";
    }
}

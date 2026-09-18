using System.Globalization;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Adapters.Presenters;

/// <summary>
/// 界面上的**文案**。音名、小节号、拍位、时值、时长、音色名，全在这儿拼。
///
/// 为什么单独一个文件、而不是散在视图里：这些字符串是「人看得懂的东西」，
/// 和「画在哪里」是两件事。放在一处才好逐条比对，也才改得动（以后要英文界面只动这里）。
///
/// 一个颜色值都不在这儿 —— 外观归 <c>Tokens.axaml</c>。
/// </summary>
public static class Format
{
    /// <summary>空着的那一格。wireframe 里到处都是这个长破折号。</summary>
    public const string Placeholder = "—";

    /// <summary>
    /// 读数条右边那行操作提示。
    ///
    /// **只写这一张真做得到的。** 拖动、框选、方向键微调那些是 09 的事，
    /// 写上去等于承诺一个按了没反应的键 —— 那比不写更坏。方向和归属都在这儿说清楚。
    /// </summary>
    public const string ReadoutHint =
        "← → 在音符之间前后跳（只定位，不改）· 编辑（拖音符 / 改时值 / 框选）归 09 与 08";

    /// <summary>音高：音名 + 简谱记号。需求里两样都要，缺一个都得让人对着谱子数半天。</summary>
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

    /// <summary>读数条的「选中」格。</summary>
    public static string Selection(int trackNumber, int pitch, double lengthBeats) =>
        $"轨 {TrackNumber(trackNumber)} · {Music.NoteName(pitch)} · {Length(lengthBeats)}";

    /// <summary>走带条上的「位置」：当前小节 / 总小节。</summary>
    public static string Position(int bar, int barCount) =>
        $"{BarNumber(bar)} / {barCount} 小节";

    /// <summary>导航条右边的「第 a–b 小节 / 共 n 小节」。用短破折号，别用连字符 —— 那是两回事。</summary>
    public static string BarRange(int firstBar, int lastBar, int barCount) =>
        $"第 {BarNumber(firstBar)}–{BarNumber(lastBar)} 小节 / 共 {barCount}";

    /// <summary>时长 m:ss。曲子长度以秒给，超过一小时也只进位到分 —— 单人练习用的谱子到不了那个量级。</summary>
    public static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalMinutes}:{span.Seconds:D2}";
    }

    /// <summary>轨序号：wireframe 里是两位的 <c>01</c>，对齐全靠它。</summary>
    public static string TrackNumber(int oneBased) =>
        oneBased.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>轨头上的「128 音」。</summary>
    public static string NoteCount(int count) => $"{count} 音";

    /// <summary>
    /// 轨头上的音色名。9 号声道是打击乐，MIDI 规定它整条都是鼓组，
    /// 音色号在那一轨没有意义 —— 直接说「标准鼓组」，比报一个 GM 编号清楚。
    /// </summary>
    public static string Timbre(int program, int channel) =>
        channel == 9 ? "标准鼓组 · 通道 10" : $"{ProgramName(program)} · GM {program + 1}";

    /// <summary>GM 音色名（0 起的音色号 → 中文名）。越界就退回编号，不编一个名字出来。</summary>
    public static string ProgramName(int program) =>
        program >= 0 && program < ProgramNames.Length
            ? ProgramNames[program]
            : $"音色 {program + 1}";

    /// <summary>
    /// MIDI 标准 128 个音色的中文名，顺序即 GM 编号。
    /// 抄的是通用译名表：只为了让人一眼认出「这条是小提琴还是贝斯」，
    /// 不追求和哪个软件逐字一致。
    /// </summary>
    private static readonly string[] ProgramNames =
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
}

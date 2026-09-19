using System.Globalization;
using MidiPerformer.Core.Ports.Inbound;
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
    /// **只写这一张真做得到的。** 09 把「拖动 / 改时值 / 框选」做完了，原来那句
    /// 「编辑归 09」的欠条就作废了（留着它比不写更坏：用户会以为拖不动是自己没点对）。
    ///
    /// 键位是**方案 A**（用户在手感样机上拍的）：`←/→` 移时间、`↑/↓` 移音高、
    /// `Shift + ←/→` 改时值、`Ctrl + ←/→` 前后跳。07 已经把 `←/→` 钉成「前后跳」，
    /// 09 要的是「移时间」——解法是把 07 那条**挪到 Ctrl 上**，不是砍掉：
    /// 能力一个都没少，变的只是哪个键绑到它上面。
    ///
    /// **空格**和 `Ctrl + ↑/↓` 是后加的两条，写在这儿的理由和当初一样：
    /// 一条快捷键要是屏幕上没有一处说得出它，就等于没有 —— 用户不会去翻代码。
    /// 这两条也确实是**按键本身看不出来**的那种（不像按钮上印着字）。
    /// </summary>
    public const string ReadoutHint =
        "空格 播放 · ← → 移时间（一格 = 十六分）· ↑ ↓ 移音高 · Shift + ← → 改时值 · "
        + "Ctrl + ← → 前后跳 · Ctrl + ↑ ↓ 换轨 · Ctrl+Z 撤销 / Ctrl+Y 重做";

    /// <summary>
    /// 轨头上移调那一格的读数：几个半音。
    ///
    /// 正负号只在真有方向时才出现（<c>+0</c> / <c>-0</c> 都落到 <c>0</c> 那一节），
    /// 零就是零 —— 它同时也是「没移调」这个默认状态的样子。
    /// </summary>
    public static string Transpose(int semitones) => $"{semitones:+0;-0;0} 半音";

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

    /// <summary>
    /// 「位置」读数：当前小节 / 总小节。**显示的是播放头那一小节**，不是视口起始 ——
    /// 它和「跳到某小节」的输入框挨着，两个数摆在一起才是「我在哪 / 我要去哪」的对照。
    /// </summary>
    public static string Position(int bar, int barCount) =>
        $"{BarNumber(bar)} / {barCount} 小节";

    /// <summary>时长 m:ss。曲子长度以秒给，超过一小时也只进位到分 —— 单人练习用的谱子到不了那个量级。</summary>
    public static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalMinutes}:{span.Seconds:D2}";
    }

    /// <summary>
    /// 预检不放行时，状态行上那句话。
    ///
    /// <b>三种失败各说各的下一步动作</b>，不是三句「不能开始」：用户看完得知道去改什么 ——
    /// 权限不够就重开程序、输入法是中文就切英文、轨弹不了就换轨。说不清下一步的提示
    /// 等于把人支到错方向上去，比不说更坏。
    ///
    /// <b>为什么这段文案要从窗口里搬出来</b>：它原先是 <c>PerformerWindow</c> 里一段写死的
    /// <c>switch</c>，而「两种预检都要给明确的中文提示、不是静默失败」正是这条工单的硬要求 ——
    /// 写死在视图里就没人给它加得了断言，日后往 <see cref="PerformanceStartOutcome"/> 里
    /// 添一个失败原因、忘了配文案，用户看到的就是「按了开始什么都没发生」，
    /// 而那恰恰是要防的那一件事。文案归这一层（见类注释：以后要英文界面只动这里），
    /// 于是「每个失败原因都有一句话」这条有测试守着。
    ///
    /// <b>放行也照样回一句话，不返回空串。</b>调用方只在 <c>!= Started</c> 时才用它，
    /// 但空串在这里是个陷阱：哪天真有人漏了那个判断，状态行会变成一片空白 ——
    /// 又回到「静默失败」。回一句「预检没放行」至少让人知道发生了什么。
    /// </summary>
    public static string PreflightRefusal(PerformanceStartOutcome outcome) => outcome switch
    {
        PerformanceStartOutcome.NotElevated =>
            "没开始：要以管理员身份运行。不然发的按键会被系统挡在游戏窗口外面 —— 一个音都收不到，还不报错。",
        PerformanceStartOutcome.ImeActive =>
            "没开始：输入法现在是中文。中文态下按键会被输入法截走，弹出来就是整段整段地漏音。切成英文再按一次。",
        PerformanceStartOutcome.NoPlayableTrack =>
            "没开始：这条轨弹不了。口琴一次只响一个音，所以只能弹单声部、不带打击乐的轨。换一条试试。",
        _ => "没开始：预检没放行。"
    };

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
        channel == 9 ? "标准鼓组 · 通道 10" : ProgramLabel(program);

    /// <summary>
    /// 音色下拉里那一行的写法：<c>口琴 · GM 23</c>。
    ///
    /// 轨头上的那句话和下拉里那一行**必须是同一个算法**：下拉合上之后显示的是选中的那一行，
    /// 两处各拼各的，用户在列表里挑的和轨头上显示的就可能差一个字（GM 编号从 0 起还是从 1 起，
    /// 正是最容易差的那一处）。
    /// </summary>
    public static string ProgramLabel(int program) => $"{ProgramName(program)} · GM {program + 1}";

    /// <summary>GM 音色名（0 起的音色号 → 中文名）。越界就退回编号，不编一个名字出来。</summary>
    public static string ProgramName(int program) =>
        program >= 0 && program < ProgramNames.Count
            ? ProgramNames[program]
            : $"音色 {program + 1}";

    /// <summary>
    /// MIDI 标准 128 个音色的中文名，顺序即 GM 编号。
    /// 抄的是通用译名表：只为了让人一眼认出「这条是小提琴还是贝斯」，
    /// 不追求和哪个软件逐字一致。
    ///
    /// <b>公开</b>出去是给音色下拉用的（16）：它要的就是这 128 行，
    /// 在视图里再抄一份的话，改一处漏一处是迟早的事。
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

    /// <summary>「抽掉一段」那两个框还没填好的时候，那一行预览里写的话。</summary>
    public const string CutNeedNumbers = "两个框都填上小节号（1 起，两头都算在内）";

    /// <summary>
    /// 「抽掉第 5–8 小节」按下去**会发生什么**。
    ///
    /// 这条预览是这一步的主心骨：抽掉一段是这个软件里唯一会**改时间轴**的编辑
    /// （别的编辑只动音高、时值、名字），所以它是唯一一个「光看界面看不出结果」的动作 ——
    /// 屏幕上得有一句话说出哪几个音会没、后面有多少音会提前、这条轨会短掉几小节。
    ///
    /// <b>不说「整曲长度不变」。</b>那句话是错的：整曲长度取的是所有轨的末尾最大值，
    /// 剪的那条要是本来就是最长的那条（这个软件最常见的用法就是只留一条轨来吹），
    /// 整曲跟着一起短。所以这里只说**这条轨**的 96 → 92 ——
    /// 它由 <see cref="CutPreview"/> 算出来，和真跑一遍命令的结果一致（见那边的对照测试）。
    /// </summary>
    /// <param name="firstBar">起点小节（1 起，含）。</param>
    /// <param name="lastBar">终点小节（1 起，含）。</param>
    /// <param name="trackNumber">轨号，1 起。</param>
    /// <param name="preview">抽完会怎样，见 <see cref="CutPreview.Of"/>。</param>
    public static string CutSummary(int firstBar, int lastBar, int trackNumber, CutPreview.Result preview)
    {
        int bars = lastBar - firstBar + 1;
        string range = $"第 {BarNumber(firstBar)}–{BarNumber(lastBar)} 小节（共 {bars} 小节）";

        // 一个音都不动：命令会原样返回同一份曲子，连撤销都不记一笔 —— 直说，别让人按了等着看变化
        if (!preview.Changes) return $"{range}：这一段里没有音，抽了和没抽一样";

        // 三段分开写、各自可能不出现：只有「删掉」而没有「前移」是常事（剪的是尾巴上的一段），
        // 硬凑成一句就会出现「后面 0 个提前 4 小节」这种没人看得懂的话
        var parts = new List<string>();
        if (preview.Deleted > 0) parts.Add($"删掉 {preview.Deleted} 个音");
        if (preview.Trimmed > 0) parts.Add($"在切口上剪短 {preview.Trimmed} 个");
        if (preview.Shifted > 0) parts.Add($"后面 {preview.Shifted} 个提前 {bars} 小节");

        return $"{range}：{string.Join("、", parts)}"
             + $" · 第 {TrackNumber(trackNumber)} 轨 {preview.BarsBefore} → {preview.BarsAfter} 小节";
    }
}

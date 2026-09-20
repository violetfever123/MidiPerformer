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
    /// 读数条右边那行操作提示 —— **没选中任何音**的时候那一行（走带 + 换轨那一类）。
    ///
    /// 分层的规矩见 <see cref="ReadoutHintEditing"/> 上面那段说明。
    ///
    /// 这一行里的三样：
    /// - `空格 播放/暂停`：20 把它从「开始」改成**播放 / 暂停**（再按停在原地，再按从那儿接着放）。
    ///   退回去写「播放」，就是 20 号工单点名推翻的那条旧决定。
    /// - `Shift + 空格 回跳一小节并播放`：35 号新加的动作（退一小节头、立刻开始放）。
    /// - `Ctrl + ↑ ↓ 换轨`：和 `Ctrl + ← →` 对称的一对，一个在轨之间走、一个在时间上走，都不动谱面。
    ///   它归**这一行**（36 号问过用户：「将『音轨的选择』放到『播放』和『滚回上一小节播放』里面
    ///   会比较好」）—— 理由不只是分类好看：它换的是**焦点轨**，而焦点轨决定你弹哪条轨。
    ///   它是**演奏手势**，不是编辑手势。
    /// </summary>
    public const string ReadoutHintPerforming =
        "空格 播放/暂停 · Shift + 空格 回跳一小节并播放 · Ctrl + ↑ ↓ 换轨";

    /// <summary>
    /// 读数条右边那行操作提示 —— **选中了音**的时候那一行（编辑那一类）。
    ///
    /// <b>为什么分层（36 号，用户提的）：</b>原来是一整行（148 字、约 1959 物理 px），
    /// 而装了曲子之后左边那套读数一在场，提示行能用的宽度只剩 1486 物理 px ——
    /// 屏幕上**被省略号截掉约四分之一**，而且截掉哪几条**由像素宽度决定、不由重要程度决定**
    ///（量在 <c>.scratch/probe-35-hint.ps1</c> 和 35 号工单里）。分层之后两行都很短，
    /// 一句话说完「此刻该干什么」，而**全文永远在 ToolTip 里**（见 <see cref="ReadoutHintTooltip"/>）。
    ///
    /// <b>判据是「选中集的个数 &gt; 0」</b>，不是「恰好一个」（36 号问过用户，两个选项是
    /// 「≥1 就切」/「恰好 1 个才切」）：`← →` / `↑ ↓` / `Delete` 动的都是**整批选中**
    ///（见 <c>MainWindow.NudgeNotes</c> / <c>DeleteSelection</c>，多选在 UI 里做得出来：
    /// `Shift` 点一个音、或者在空白处横拖框一段）—— 选中一批的时候显示「空格 播放/暂停」
    /// 是答非所问。<c>Shift + ← →</c> 只动**主选中**那一个（<c>NudgeLength</c>），
    /// 这个「一个 / 一批」的区别是真的，不是文案。
    ///
    /// 逐条交代（每一句都得和真按键对得上，真身在 <c>MainWindow.OnWindowKeyDown</c>；
    /// 一句错的快捷键提示比没有提示更坏 —— 人会以为功能坏了，然后去修一个没坏的东西）：
    ///
    /// - `←/→` 移时间、`↑/↓` 移音高、`Shift + ←/→` 改时值：**方案 A**（用户在手感样机上拍的）。
    ///   07 原本把裸 `←/→` 钉成「前后跳」，09 要的是「移时间」—— 解法是把 07 那条**挪到 Ctrl 上**，
    ///   不是砍掉：能力一个都没少，变的只是哪个键绑到它上面。
    /// - `Ctrl + ←/→` **选同轨前/后一个音**：18 把「前后跳」**限制在焦点轨里**（跨轨那版按着按着
    ///   会莫名其妙换到别的轨上），**「同轨」两个字必须留着**。36 号把说法从「同轨前后跳」
    ///   改成现在这句 —— 用户要的是「如何选下一个音」看得见，而原文里**没有「选」字**：
    ///   它其实是**把选中挪到**上/下一个音上（<c>MoveSelection</c> 走 <c>SelectOnly</c>），
    ///   不是移动视野。改口改的是说法，键和落点一个字节没动。
    /// - `Delete 删除`：19 新接的两个键之一（`Backspace` 是同一个动作的第二个落点）。
    /// - `Esc 取消选中`：**动作是 36 号做的，这一行是 37 号才写上的**。36 号那天它只在代码里
    ///   （<c>MainWindow.OnWindowKeyDown</c> 的 Esc 那一支），屏幕上没有一处说得清它 ——
    ///   而这一行正是它该待的地方：**它只在「已经选中了音」的时候才有意义**，
    ///   所以它归**编辑那一层**（用户 2026-09-20 的原话：「取消选中放在『选中一些音符之后』的
    ///   那个提示行」）。用户那两条要求一并记在这儿：`Esc` **只放开选中的音**，
    ///   **焦点轨不能取消选择**（必须有一个）—— 谁给它加第二层意思，先看
    ///   <c>ShortcutHintTests.Escape放开选中但不动焦点轨</c>。
    ///   排在这一行的**末尾**：它读起来是「干完这些之后想放手就按 Esc」，
    ///   而且它和 `Delete 删除` 一样，是这一层里**唯一**能在界面上说出口的地方（菜单里没有它）。
    ///
    /// **一行只列一把钥匙。** `Delete` / `Backspace` 是同一个动作的两个键，第二把
    /// **不写不等于没有**：它是多给的，按了照样管用。
    /// </summary>
    public const string ReadoutHintEditing =
        "← → 移时间（一格 = 十六分）· ↑ ↓ 移音高 · Shift + ← → 改时值 · "
        + "Ctrl + ← → 选同轨前/后一个音 · Delete 删除 · Esc 取消选中";

    /// <summary>
    /// 提示行的 ToolTip：**两行合起来的全文**（<see cref="ReadoutHintPerforming"/> +
    /// <see cref="ReadoutHintEditing"/>），一条不落。
    ///
    /// 「屏幕上那一行 = 此刻该看的那一类」之后，ToolTip 就是**唯一**能看全的地方 ——
    /// 原来它是「被截掉那半行的全文」，现在它是「另外那一层也在里面」。所以要它一条不缺。
    ///
    /// <b>撤销 / 重做不在这里面。</b>用户 2026-09-20 的原话：
    /// 「CTRL+Y、CTRL+Z 及『撤销』与『重做』这两个点不需要单独写，将它们作为快捷键，
    /// 直接放到『操作』里面作为提示就可以了」—— 它们在「操作」菜单项的右侧（23 号印的
    /// <c>InputGesture</c>）。ToolTip 里再写一遍就又把那句「不需要单独写」推翻了，
    /// 而这条 ToolTip 正是「看全」的出口 —— 它多一句，屏幕上就多一处。
    /// 守着这一条的是 <c>ShortcutHintTests.撤销和重做的快捷键只在菜单项右侧说一次</c>。
    /// </summary>
    public const string ReadoutHintTooltip =
        ReadoutHintPerforming + " · " + ReadoutHintEditing;

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

    /// <summary>
    /// 「抽掉一段」还没在卷帘上拖出范围的时候，那一行里写的话。
    ///
    /// 38 号之前这儿写的是「两个框都填上小节号」—— 那两个框已经退役了，
    /// 那一段现在是在卷帘上**直接拖**出来的（用户 2026-09-20：「原来的按小节切放弃
    /// 不要再出现填小节数字的窗口了」）。所以这句话的任务只剩下一个：
    /// 说清楚那一段**从哪儿来**。
    /// </summary>
    public const string CutNeedRange = "在这条轨的卷帘上横向拖一段 —— 拖出来的那一段就是要消失的";

    /// <summary>
    /// 某个 tick 落在**小节内的哪个位置**：整小节头上写「第 5 小节」，
    /// 正好落在拍线上写「第 5 小节第 2 拍」，其余写小数拍「第 5 小节第 1.75 拍」。
    ///
    /// **拍从 1 起算**，和小节内拍位的读数（<c>PianoRollController.NoteInfo.BeatInBar</c>）
    /// 是同一个口径：那儿是 <c>Beats(intoBar, …) + 1</c>，这儿也得 +1，
    /// 不然同一根拍线在读数条上写「2.00 拍」、在剪的预览里写「第 1 拍」——
    /// 两处说的是同一件事，对不上就没人敢信。
    ///
    /// 为什么要小数拍：拖出来的那一段现在可以停在**任何一条十六分线上**（38 号），
    /// 于是「第 5 小节第 1.75 拍」这种位置是常事。只说「第 5 小节」等于把落点抹掉，
    /// 而用户正是为了那个落点才放弃填小节号的。
    ///
    /// 「一拍」的口径和 <see cref="Beats"/> 一致（四分音符），**不看拍号** ——
    /// 6/8 里的一拍也是四分音符，这样同一个 tick 在读数条上和在这儿是同一个数。
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
    ///
    /// 抽掉一段之后后半截**提前多少**就说这个数。从前那一格写死「{n} 小节」，
    /// 因为切点只能落在小节线上；38 号之后切点可以是任何一条十六分线，
    /// 再写「提前 0 小节」就是一句错话 —— 明明提前了三拍。
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
    ///
    /// 不套 <c>F2</c>：整拍写成「3.00 拍」是多余的两位，
    /// 而十六分的偏移只需要两位小数就够精确（再细的格子在界面上也点不出来）。
    /// </summary>
    private static string BeatCount(double beats)
        => Math.Abs(beats - Math.Round(beats)) < 1e-9
            ? ((long)Math.Round(beats)).ToString(CultureInfo.InvariantCulture)
            : beats.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// 「抽掉这一段」按下去**会发生什么**。
    ///
    /// 这条预览是这一步的主心骨：抽掉一段是这个软件里唯一会**改时间轴**的编辑
    /// （别的编辑只动音高、时值、名字），所以它是唯一一个「光看界面看不出结果」的动作 ——
    /// 屏幕上得有一句话说出哪几个音会没、后面有多少音会提前、这条轨会短掉几小节。
    ///
    /// <b>不说「整曲长度不变」。</b>那句话是错的：整曲长度取的是所有轨的末尾最大值，
    /// 剪的那条要是本来就是最长的那条（这个软件最常见的用法就是只留一条轨来吹），
    /// 整曲跟着一起短。所以这里只说**这条轨**的 96 → 92 ——
    /// 它由 <see cref="CutPreview"/> 算出来，和真跑一遍命令的结果一致（见那边的对照测试）。
    ///
    /// <b>收的是 tick，不是小节号</b>（38 号）：那一段是在卷帘上拖出来的，
    /// 它可以停在任何一条十六分线上。整小节对齐的时候仍然说「第 5–8 小节（共 4 小节）」——
    /// 那是最常见的一刀，读起来最短；没对齐才改说两端各在**小节内的哪个位置**。
    /// 两种说法都只由这两个 tick 决定，不会出现「预览说 5–8、实际剪了 5–9」。
    /// </summary>
    /// <param name="startTick">要抽掉的那一段的起点（含）。</param>
    /// <param name="endTick">终点（不含）。</param>
    /// <param name="trackNumber">轨号，1 起。</param>
    /// <param name="preview">抽完会怎样，见 <see cref="CutPreview.Of"/>。</param>
    /// <param name="ticksPerBar">一小节多少 tick。</param>
    /// <param name="ticksPerQuarterNote">四分音符多少 tick —— 拍数换算只用它（见 <see cref="Beats"/>）。</param>
    public static string CutSummary(
        long startTick, long endTick, int trackNumber, CutPreview.Result preview,
        long ticksPerBar, int ticksPerQuarterNote)
    {
        string range = CutRangeLabel(startTick, endTick, ticksPerBar, ticksPerQuarterNote);

        // 一个音都不动：命令会原样返回同一份曲子，连撤销都不记一笔 —— 直说，别让人按了等着看变化
        if (!preview.Changes) return $"{range}：这一段里没有音，抽了和没抽一样";

        // 三段分开写、各自可能不出现：只有「删掉」而没有「前移」是常事（剪的是尾巴上的一段），
        // 硬凑成一句就会出现「后面 0 个提前 4 小节」这种没人看得懂的话
        string shift = SpanLength(endTick - startTick, ticksPerBar, ticksPerQuarterNote);
        var parts = new List<string>();
        if (preview.Deleted > 0) parts.Add($"删掉 {preview.Deleted} 个音");
        if (preview.Trimmed > 0) parts.Add($"在切口上剪短 {preview.Trimmed} 个");
        if (preview.Shifted > 0) parts.Add($"后面 {preview.Shifted} 个提前 {shift}");

        return $"{range}：{string.Join("、", parts)}"
             + $" · 第 {TrackNumber(trackNumber)} 轨 {preview.BarsBefore} → {preview.BarsAfter} 小节";
    }

    /// <summary>
    /// 那一段的写法。两端都落在小节线上时说「第 5–8 小节（共 4 小节）」，
    /// 否则说两端各在小节内的哪儿（「第 5 小节第 2.75 拍 到 第 6 小节第 1 拍」）。
    ///
    /// 终点那个 tick 是**不含**的（照 <c>ISongEditor.CutRange</c> 的约定），
    /// 所以它正好落在小节线上时，说的那一小节是**它前面的一小节**。
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

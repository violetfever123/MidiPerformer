using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;

namespace MidiPerformer.Adapters.Presenters;

/// <summary>
/// 「这一段抽掉之后会变成什么样」——<b>说在按下去之前</b>。
///
/// 抽掉一段是这条轨上唯一一个**会改时间轴**的动作（别的编辑只动音高、时值、名字），
/// 所以它是这个软件里最需要「先说清楚再动手」的一步：用户按下「抽掉」之前，
/// 屏幕上得有一句话告诉他哪几个音会没、后面有多少音会提前、这条轨会短掉几小节。
///
/// <b>这是一份预测，不是一份实现。</b>真正的改动在
/// <see cref="ISongEditor.CutRange"/> 里，这里照着它那张分支表再走一遍、
/// 只数数、不动音符。<b>两处必须一致</b>，靠的是
/// <c>CutPreviewTests</c> 里那条对照测试：拿同一份谱子先预测、再真跑一遍命令，
/// 逐个断言「预测的条数」和「实际少掉的音 / 实际剩下的末尾」相等 ——
/// 命令那边的分支表要是改了而这里没跟上，那条测试当场红，用户不会先看到一句假话。
///
/// 为什么不去调命令拿结果：那要造一份新曲子、还得绕过撤销记账（预测不该记一笔撤销），
/// 而且每敲一个数字都要跑一遍全曲的拷贝。数数只要过一遍这条轨的音符，便宜得多。
/// </summary>
public static class CutPreview
{
    /// <summary>
    /// 抽掉 <c>[startTick, endTick)</c> 之后会怎样。
    /// </summary>
    /// <param name="Deleted">整个落在区间里、会被删掉的音有几个。</param>
    /// <param name="Trimmed">切口上被剪短（但留着）的音有几个 —— 左切口剪掉尾巴的、右切口剪掉脑袋挪到左切口接上的，都算。</param>
    /// <param name="Shifted">整个在右切口之后、要整体前移的音有几个。</param>
    /// <param name="BarsBefore">这条轨现在几小节（按它自己的末尾算，不是整曲）。</param>
    /// <param name="BarsAfter">抽完之后这条轨会剩几小节。</param>
    public sealed record Result(
        int Deleted, int Trimmed, int Shifted, int BarsBefore, int BarsAfter)
    {
        /// <summary>
        /// 这一刀下去谱面上有没有动静。
        ///
        /// 为假时命令会**原样返回同一份曲子**（见 <see cref="ISongEditor.CutRange"/> 的返回值说明），
        /// 也就是连一笔撤销都不会记 —— 所以界面上那颗「抽掉」该灰着：
        /// 按下去什么都不发生、还看不出为什么，比灰着更坏。
        /// </summary>
        public bool Changes => Deleted > 0 || Trimmed > 0 || Shifted > 0;
    }

    /// <summary>
    /// 数一遍。区间由界面算好（小节 → tick），这里只认 tick —— 和命令同一条规矩。
    /// </summary>
    /// <param name="notes">这条轨的音符，起点升序（模型保证的次序，这里不再排）。</param>
    /// <param name="startTick">切口起点，含。</param>
    /// <param name="endTick">切口终点，不含。小于起点时当空区间处理（界面本来就该先换过来）。</param>
    /// <param name="ticksPerBar">一个小节多少 tick，算「几小节」用。</param>
    public static Result Of(
        IReadOnlyList<Note> notes, long startTick, long endTick, long ticksPerBar)
    {
        if (startTick < 0) startTick = 0;
        long span = Math.Max(0, endTick - startTick);

        int deleted = 0, trimmed = 0, shifted = 0;
        long before = 0;   // 抽之前这条轨的末尾
        long after = 0;    // 抽之后

        foreach (var note in notes)
            if (note.EndTick > before) before = note.EndTick;

        // 空区间是「抽掉零个 tick」：命令在那儿**原样返回同一份曲子**（连撤销都不记）。
        // 这一支不能省 —— 照下面那张分支表走的话，「右切口之后」那一条会把整条轨
        // 都算成「前移 0 tick」（起点 3840 >= 终点 1920，条件成立），
        // 于是预览说「后面 30 个音提前 0 小节」，按下去却什么都不发生。
        // 界面上那一颗「抽掉」会亮着，是个空按钮 —— 对照测试抓的就是这一条。
        if (span == 0)
        {
            int bars = Bars(before, ticksPerBar);
            return new Result(0, 0, 0, bars, bars);
        }

        foreach (var note in notes)
        {
            long start = note.StartTick;
            long end = note.EndTick;

            // 下面这一串分支和 SongEditor.CutRange 里那张表**一一对应，次序也一样**：
            // 跨过左切口那一条排在前，所以「整个区间都被同一个音盖住」的音也走它
            //（剪断、右边那截丢掉），和命令一致。
            long keptEnd;
            if (end <= startTick)
            {
                keptEnd = end;                                   // 整个在左切口之前：一个字节不动
            }
            else if (start >= endTick)
            {
                shifted++;
                keptEnd = end - span;                            // 整个在右切口之后：整体前移
            }
            else if (start < startTick)
            {
                trimmed++;
                keptEnd = startTick;                             // 跨过左切口：剪断，留下左边那截
            }
            else if (end > endTick)
            {
                trimmed++;
                keptEnd = startTick + (end - endTick);           // 伸出右切口：外面那截挪到左切口接上
            }
            else
            {
                deleted++;                                       // 整个落在区间里：没了
                continue;
            }

            if (keptEnd > after) after = keptEnd;
        }

        return new Result(
            deleted, trimmed, shifted,
            Bars(before, ticksPerBar), Bars(after, ticksPerBar));
    }

    /// <summary>
    /// 末尾 tick → 几小节，向上取整（空轨算 1 小节，和导航条那边同一个口径）。
    ///
    /// 走 <see cref="PianoRollGeometry.BarCount"/> 而不是自己 <c>/</c> 一下：
    /// 「共几小节」在整曲和单轨两处必须是同一个算法，不然导航条写着 96、轨头上写着 95，
    /// 而这种差一个的数最难查。
    /// </summary>
    private static int Bars(long endTick, long ticksPerBar) =>
        PianoRollGeometry.BarCount(endTick, ticksPerBar);
}

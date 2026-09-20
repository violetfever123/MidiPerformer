using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;

namespace MidiPerformer.Adapters.Presenters;

/// <summary>
/// 「这一段抽掉之后会变成什么样」的预测：照着 <see cref="ISongEditor.CutRange"/> 的分支表只再走一遍、只数数、不动音符。
/// 结果说明哪几个音会没、有多少音会提前、这条轨会短掉几小节，两处的分支表必须一致。
/// </summary>
public static class CutPreview
{
    /// <summary>抽掉 <c>[startTick, endTick)</c> 之后会怎样。</summary>
    /// <param name="Deleted">整个落在区间里、会被删掉的音有几个。</param>
    /// <param name="Trimmed">切口上被剪短但留着的音有几个（左切口剪尾、右切口挪头都算）。</param>
    /// <param name="Shifted">整个在右切口之后、要整体前移的音有几个。</param>
    /// <param name="BarsBefore">这条轨现在几小节（按它自己的末尾算，不是整曲）。</param>
    /// <param name="BarsAfter">抽完之后这条轨会剩几小节。</param>
    public sealed record Result(
        int Deleted, int Trimmed, int Shifted, int BarsBefore, int BarsAfter)
    {
        /// <summary>这一刀下去谱面上有没有动静；为假时命令会原样返回同一份曲子、连撤销都不记，界面上那颗「抽掉」该灰着。</summary>
        public bool Changes => Deleted > 0 || Trimmed > 0 || Shifted > 0;
    }

    /// <summary>
    /// 数一遍。区间由界面算好（小节 → tick），这里只认 tick。
    /// </summary>
    /// <param name="notes">这条轨的音符，起点升序（模型保证的次序，这里不再排）。</param>
    /// <param name="startTick">切口起点，含。</param>
    /// <param name="endTick">切口终点，不含。小于起点时当空区间处理。</param>
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

        // 空区间是「抽掉零个 tick」，命令在那儿原样返回同一份曲子；不单独处理这一支的话，
        // 下面的分支表会把整条轨都算成「前移 0 tick」，预览说有音提前却按下去什么都不发生。
        if (span == 0)
        {
            int bars = Bars(before, ticksPerBar);
            return new Result(0, 0, 0, bars, bars);
        }

        foreach (var note in notes)
        {
            long start = note.StartTick;
            long end = note.EndTick;

            // 下面这串分支和 SongEditor.CutRange 里那张表一一对应、次序也一样（跨过左切口那条排在前）。
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
    /// 走 <see cref="PianoRollGeometry.BarCount"/>，整曲和单轨必须用同一个算法。
    /// </summary>
    private static int Bars(long endTick, long ticksPerBar) =>
        PianoRollGeometry.BarCount(endTick, ticksPerBar);
}

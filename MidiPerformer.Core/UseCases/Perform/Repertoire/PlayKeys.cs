namespace MidiPerformer.Core.UseCases.Perform.Repertoire;

/// <summary>
/// 游戏口琴的键位表。
///
/// **这个文件不是移植来的**——原版 harmonica-auto-player 里没有它，键位表原本长在
/// <c>Engine/NoteMapper.cs</c> 的 <c>Keys</c> 和 <c>TopKey</c> 两个成员上，是我们把它们抽出来
/// 单独成文件的。因此它**可以改**：换键位、加乐器都只动这里，不需要碰移植代码。
///
/// <see cref="NoteMapper"/> 里的同名成员现在只是指向这里的别名，不再是第二份定义。
/// </summary>
public static class PlayKeys
{
    /// <summary>do..ti 对应的键位（z x c v b n m）。</summary>
    public static readonly char[] Keys = { 'Z', 'X', 'C', 'V', 'B', 'N', 'M' };

    /// <summary>高高音do 用的键：键盘逗号“，”。</summary>
    public const char TopKey = ',';
}

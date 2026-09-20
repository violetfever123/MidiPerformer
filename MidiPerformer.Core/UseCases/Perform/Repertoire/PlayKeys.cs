namespace MidiPerformer.Core.UseCases.Perform.Repertoire;

/// <summary>
/// 游戏口琴的键位表。不是移植来的，换键位、加乐器只动这里，不需要碰移植代码。
/// <see cref="NoteMapper"/> 里的同名成员只是指向这里的别名。
/// </summary>
public static class PlayKeys
{
    /// <summary>do..ti 对应的键位（z x c v b n m）。</summary>
    public static readonly char[] Keys = { 'Z', 'X', 'C', 'V', 'B', 'N', 'M' };

    /// <summary>高高音do 用的键：键盘逗号“，”。</summary>
    public const char TopKey = ',';
}

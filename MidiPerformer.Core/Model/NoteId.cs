namespace MidiPerformer.Core.Model;

/// <summary>
/// 一个音符的稳定身份：它在自己那条轨里叫什么名字。和内容无关 ——
/// 音高、起点、时值、力度改多少遍，身份都不变，于是「刚才选中的是哪一个音」在改动前后指的是同一个音。
/// 只在一条轨内唯一，跨轨不保证。
/// </summary>
/// <param name="Value">身份号。0 = 没有身份（<see cref="None"/>），其余从 1 起。</param>
public readonly record struct NoteId(int Value)
{
    /// <summary>「这个音没有身份」：手工构造出来、还没经过导入或剪切的音符就是它。</summary>
    public static NoteId None => default;

    /// <summary>下一个号。发号的人拿它连号发（见 <see cref="NoteIdentity"/>）；它不管这个号能不能用。</summary>
    public NoteId Next => new(Value + 1);

    /// <summary>
    /// <c>ToString()</c> 只说号码，打印成「3」而不是 <c>{ Value = 3, Next = … }</c>。
    /// 必须覆盖：自动生成的 <c>PrintMembers</c> 会印 <see cref="Next"/>，印它就递归到栈溢出。
    /// </summary>
    private readonly bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(Value);
        return true;
    }
}

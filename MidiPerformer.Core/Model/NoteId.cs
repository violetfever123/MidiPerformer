namespace MidiPerformer.Core.Model;

/// <summary>
/// 一个音符的**稳定身份**：它在自己那条轨里叫什么名字。
///
/// <b>它和内容无关。</b>音高、起点、时值、力度改多少遍，身份都不变 ——
/// 于是「刚才选中的是哪一个音」这句话在改动前后指的是同一个音。
/// <see cref="NoteRef"/> 就靠这一条做到「一次编辑之后不必重新认音」：
/// 从前的坐标是下标，一次重排就作废，而且是**一声不吭地**作废 ——
/// 它不会报错，只是指向了另一个音，界面只好存一份按值的镜像去重新找（31 号工单删掉了那套）。
///
/// <b>0 号是「没有身份」</b>（<see cref="None"/>）：手工 <c>new</c> 出来的音符没有走过发号那一步，
/// 就是 0 号。留出这个号、而不是让身份从 0 开始数，是因为「没发过号」和「0 号音」必须是两件事 ——
/// 混在一起的话，一份手工拼的谱子和一份从文件读进来的谱子在身份上会撞车，而这种撞车查不出来。
///
/// <b>身份只在一条轨内唯一</b>，跨轨不保证（两条轨各有自己的 1 号音、2 号音……）。
/// 这是够用的：指着某个音的东西本来就带着轨那一半（<see cref="NoteRef"/> 就是 (轨, 什么) 的形状），
/// 而一轨一数少一层依赖 —— 整曲一个计数器的话，导入时轨的顺序一变，号就跟着变。
///
/// 为什么不直接用 <c>int</c>：下标也是 <c>int</c>。这条工单要治的正是「把下标当身份使」那一类错，
/// 而那种错不会报错。两个类型分开之后，「这里要的是下标还是身份」在签名上就看得见，写混了编不过。
/// </summary>
/// <param name="Value">身份号。0 = 没有身份（<see cref="None"/>），其余从 1 起。</param>
public readonly record struct NoteId(int Value)
{
    /// <summary>「这个音没有身份」：手工构造出来、还没经过导入或剪切的音符就是它。</summary>
    public static NoteId None => default;

    /// <summary>
    /// 下一个号。发号的人拿它连号发 —— 见 <see cref="NoteIdentity"/>。
    ///
    /// 放在这里而不是让调用方自己 <c>+ 1</c>：号码怎么往上走是身份自己的事，
    /// 调用方只需要说「再来一个」。它也不管「下一个号能不能用」——
    /// 那要看得见整条轨现有的号，住在 <see cref="NoteIdentity"/> 里。
    /// </summary>
    public NoteId Next => new(Value + 1);

    /// <summary>
    /// <c>ToString()</c> 只说号码。
    ///
    /// 不覆盖的话，record struct 自动生成的 <c>PrintMembers</c> 会把**每个公开属性**印一遍，
    /// 而 <see cref="Next"/> 是个 <see cref="NoteId"/> 类型的属性 —— 印它就得印它的 Next……
    /// 于是一句 <c>ToString()</c> 直接栈溢出（实测：整个测试进程就是这么被带停的，
    /// 输出里刷的全是同一段递归堆栈，看不出是谁在打印）。
    ///
    /// 覆盖之后断言消息里看到的是「3」而不是 <c>{ Value = 3, Next = { Value = 4, … } }</c>，
    /// 而这正是身份该有的样子：它就是个号，别的东西都是实现细节。
    /// </summary>
    private readonly bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(Value);
        return true;
    }
}

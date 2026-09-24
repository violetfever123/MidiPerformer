namespace MidiPerformer.App.Views;

/// <summary>「改过还没存」是在哪一处被拦下的。三处问的第一行一样，第二行与第二颗按钮按这一格走。</summary>
public enum UnsavedScene
{
    /// <summary>切曲子 —— 去打开另一首。这一处的改动是真没。</summary>
    SwitchSong,

    /// <summary>点演奏 —— 弹的是盘上那一份，草稿还留在编辑器里。</summary>
    Perform,

    /// <summary>关窗口 —— 这一处的改动也是真没。</summary>
    CloseWindow
}

/// <summary>弹窗上那几条出口。三处场景的意思完全一样，只是第二颗的名字跟着场景走。</summary>
public enum UnsavedChoice
{
    /// <summary>「是」：先存再继续。它永远不会丢东西。</summary>
    Save,

    /// <summary>第二颗：不存也继续。它只是「继续」的另一种方式。</summary>
    Continue,

    /// <summary>右上角那颗 ✕（和 Esc）：什么都不做，留在原地。</summary>
    Cancel
}

/// <summary>
/// 未保存弹窗要说的话 —— 三处场景各一份，落在 <see cref="Of"/> 那一张表里。
/// 纯数据，没有控件：断言「这一处该说什么、第二颗穿不穿警示色」不必把窗口开起来。
/// </summary>
/// <param name="SecondLine">正文第二行。三处的第一行逐字相同（见 <see cref="FirstLine"/>），只有这一行不同。</param>
/// <param name="SecondButton">第二颗按钮上的字。**不写「否」** —— 写它真会干什么。</param>
/// <param name="Warn">第二颗穿不穿警示色（<c>TokenWarn</c>）。</param>
public readonly record struct UnsavedPrompt(string SecondLine, string SecondButton, bool Warn)
{
    /// <summary>窗口标题。三处同一句。</summary>
    public const string Title = "改动还没保存";

    /// <summary>
    /// 正文第一行。**三处逐字相同**，只有曲名不同 —— 因为「继续」在三处指的不是同一件事，
    /// 而这句话说的是三处共有的那件事（改过了、还没存）。
    /// 名字为空时兜一句「这一份」：正常路径上不会是空的（没装曲子时「保存」那格是灰的），
    /// 但正文里露出一个空书名号比多这一行难看得多。
    /// </summary>
    public static string FirstLine(string songName)
        => $"「{(string.IsNullOrWhiteSpace(songName) ? "这一份" : songName)}」改过了，还没存。";

    /// <summary>
    /// 场景 → （正文第二行 / 第二颗按钮文案 / 第二颗是否着色）这张表。三处只差这三样。
    ///
    /// 「点演奏」那一处是**刻意不一样**的那一格：按下去不丢东西 —— 弹的是曲库里存的那份，
    /// 草稿还在编辑器里，弹完还能接着改、接着存。所以那颗按钮既不叫「丢掉」、也不穿警示色。
    ///
    /// 该穿色的两处用 <c>TokenWarn</c> 而**不是** <c>TokenStop</c>：用户没做错什么，
    /// 只是这条路会丢东西 —— 那是「注意」不是「出错」。
    /// </summary>
    public static UnsavedPrompt Of(UnsavedScene scene) => scene switch
    {
        UnsavedScene.SwitchSong => new(
            "点「丢掉」就去打开另一首，这次的改动没了。", "丢掉", true),

        UnsavedScene.Perform => new(
            "点「用已存的」就拿曲库里存的那份去弹，改动留着。", "用已存的", false),

        UnsavedScene.CloseWindow => new(
            "点「丢掉」就关掉，这次的改动没了。", "丢掉", true),

        _ => throw new ArgumentOutOfRangeException(nameof(scene), scene, "没有这一处场景")
    };
}

using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.Model;

/// <summary>
/// <see cref="Note"/> 的相等语义里**身份那一半**：身份是身份，内容是内容，两者不许混。
///
/// 这一条也卡着 S1 缝，方向和 <see cref="TrackTests"/> 相反：那边要的是「内容一样就算相等」，
/// 这边要的是「身份不参与这件事」—— MIDI 里没有地方放身份，导出再导入身份是重新发的，
/// 身份要是参与值相等，那条缝会红得让人以为读取逻辑坏了。
///
/// 代价一并钉在这儿（<c>身份不同、内容相同的两个音是值相等的</c>）：那不是 bug，是这件事的定义 ——
/// 分开两个一模一样的音靠身份，不靠 <c>Equals</c>。
/// </summary>
public class NoteTests
{
    // ==================== 身份本身 ====================

    [Test]
    public void 手工构造的音符没有身份()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new Note(60, 0, 480, 100).Id, Is.EqualTo(NoteId.None),
                "不带身份构造出来的音是 0 号 —— 身份是读取时发的，不是凭空有的");
            Assert.That(default(Note).Id, Is.EqualTo(NoteId.None));
            Assert.That(NoteId.None.Value, Is.EqualTo(0), "0 = 没有身份，这是全链的约定");
        });
    }

    /// <summary>
    /// 号本身是发出来的：<see cref="NoteId.Next"/> 就是下一个号，
    /// 一轨之内从 1 往上发（见 <c>NoteIdentity</c>）。
    /// </summary>
    [Test]
    public void 身份的号往下发()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NoteId.None.Next, Is.EqualTo(new NoteId(1)), "没有身份的下一个是 1 号，不是 0 号");
            Assert.That(new NoteId(7).Next, Is.EqualTo(new NoteId(8)));
            Assert.That(new NoteId(7), Is.EqualTo(new NoteId(7)), "身份按值比");
            Assert.That(new NoteId(7), Is.Not.EqualTo(new NoteId(8)));
        });
    }

    /// <summary>
    /// <c>ToString()</c> 只说号码 —— 这也是那个栈溢出 bug 的回归测试。
    ///
    /// record struct 自动生成的 <c>PrintMembers</c> 会把**每个公开属性**印一遍，
    /// 而 <see cref="NoteId.Next"/> 是个 <see cref="NoteId"/> 类型的属性，于是「印身份」
    /// 会一路递归到自己身上（实测：整轮测试就是这么被刷停的，输出里全是同一段递归堆栈）。
    /// 覆盖掉 PrintMembers 之后只印号码，这里把它钉住。
    ///
    /// 真有人把那个覆盖删了的话，这条测试会**崩**而不是断言失败（栈溢出不是失败，是进程没了）——
    /// 崩在这一条上，总好过崩在别处一句顺手的日志里。
    /// </summary>
    [Test]
    public void 身份的ToString只说号码()
    {
        string text = new NoteId(7).ToString();

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("7"), "号得印出来，不然断言消息里什么都看不到");
            Assert.That(text, Does.Not.Contain("Next"), "别把 Next 也印出来 —— 它一印就是无限的");
        });
    }

    // ==================== 值相等：只比内容 ====================

    [Test]
    public void 身份不参与音符的值相等()
    {
        var one = new Note(60, 0, 480, 100, new NoteId(1));
        var two = new Note(60, 0, 480, 100, new NoteId(2));

        Assert.Multiple(() =>
        {
            Assert.That(one, Is.EqualTo(two), "内容一样就是值相等，身份不参与");
            Assert.That(one == two, Is.True, "== 也得是同一个判据");
            Assert.That(one != two, Is.False);
            Assert.That(one.GetHashCode(), Is.EqualTo(two.GetHashCode()),
                "相等的两个音哈希必须相等，否则 HashSet / Dictionary 会把它俩当成两个键");
            Assert.That(one.Id, Is.Not.EqualTo(two.Id), "前提：它俩的身份确实不一样");
        });
    }

    /// <summary>
    /// 有身份的音和没有身份的音，内容一样就还相等 —— 手工拼出来的谱面（测试里那种）
    /// 和从文件读进来的谱面因此比得到一起去。身份要是参与相等，这两边永远比不相等。
    /// </summary>
    [Test]
    public void 有身份的和手工拼的内容一样就相等()
    {
        var fromFile = new Note(60, 0, 480, 100, new NoteId(3));
        var handmade = new Note(60, 0, 480, 100);

        Assert.Multiple(() =>
        {
            Assert.That(fromFile, Is.EqualTo(handmade));
            Assert.That(handmade, Is.EqualTo(fromFile));
            Assert.That(fromFile.GetHashCode(), Is.EqualTo(handmade.GetHashCode()));
        });
    }

    /// <summary>
    /// 内容一样的意思是四个字段都一样：音高、起点、时值、力度，少比哪一个都会让
    /// 两份不同的谱面看起来相等（而导出、试听都会把它们当成两个音）。
    /// </summary>
    [Test]
    public void 内容不一样就是不相等()
    {
        var note = new Note(60, 0, 480, 100);

        Assert.Multiple(() =>
        {
            Assert.That(note, Is.Not.EqualTo(new Note(61, 0, 480, 100)), "音高");
            Assert.That(note, Is.Not.EqualTo(new Note(60, 1, 480, 100)), "起始 tick");
            Assert.That(note, Is.Not.EqualTo(new Note(60, 0, 481, 100)), "时值");
            Assert.That(note, Is.Not.EqualTo(new Note(60, 0, 480, 101)), "力度");
        });
    }

    /// <summary>
    /// 后果说清楚：两个内容一样的音会**掉进同一个哈希集合**（这就是「分不开」那句话的机器样子）。
    ///
    /// 要分开它俩的地方（界面的选中集、按身份认音）拿的是 <see cref="Note.Id"/>，不是相等 ——
    /// 这条测试就是那份合同的反面：<c>Equals</c> 帮不上忙，别指望它。
    /// </summary>
    [Test]
    public void 内容相同的两个音在集合里挤成一个()
    {
        var set = new HashSet<Note>
        {
            new(60, 0, 480, 100, new NoteId(1)),
            new(60, 0, 480, 100, new NoteId(2))
        };

        Assert.That(set, Has.Count.EqualTo(1), "HashSet 按内容去重，身份分不开它们");
    }
}

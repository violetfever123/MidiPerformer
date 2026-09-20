using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.Model;

/// <summary>
/// <see cref="Note"/> 的相等语义里身份那一半：身份不参与值相等，
/// 身份不同、内容相同的两个音就是值相等的。
/// </summary>
public class NoteTests
{
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

    /// <summary><see cref="NoteId.Next"/> 就是下一个号，一轨之内从 1 往上发。</summary>
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
    /// <c>ToString()</c> 只说号码：<see cref="NoteId.Next"/> 是 <see cref="NoteId"/> 类型的属性，
    /// 不覆盖 <c>PrintMembers</c> 就会递归印到自己身上，栈溢出。
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

    /// <summary>有身份的音和手工拼的音，内容一样就相等。</summary>
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

    /// <summary>内容指音高、起点、时值、力度四个字段，少比哪一个都不行。</summary>
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

    /// <summary>内容相同的两个音会掉进同一个哈希集合，要分开它们得靠 <see cref="Note.Id"/>。</summary>
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

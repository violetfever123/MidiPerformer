using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Editing;
using NUnit.Framework;

namespace MidiPerformer.Tests.Editing;

/// <summary>
/// 音符级的编辑命令（挪 / 改时值 / 删 / 剪一段 / 改名 / 删轨）的外部行为：
/// 进去一份 <see cref="Song"/>，出来一份 <see cref="Song"/>，一个私有字段都不碰。
/// <see cref="Track.Notes"/> 承诺按起点升序，所以越过邻居的改动都要重排。
///
/// 每条命令的两条共同不变量：
/// <list type="number">
/// <item>没改就返回传进来的同一个引用（装饰器拿它当「这条命令改没改」的判据）；</item>
/// <item>没被碰到的 <see cref="Track"/> 原样复用引用。</item>
/// </list>
/// </summary>
public class SongEditorNoteCommandTests
{
    private readonly SongEditor _editor = new();

    // ==================== 挪音符：基本形状 ====================

    [Test]
    public void 挪一个音改的是起点和音高()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 240, 90)));

        var edited = _editor.MoveNotes(song, new[] { Ref(0, 0) }, 240, 2);

        var note = edited.Tracks[0].Notes[0];
        Assert.Multiple(() =>
        {
            Assert.That(note.StartTick, Is.EqualTo(720));
            Assert.That(note.Pitch, Is.EqualTo(62));
            Assert.That(note.LengthTicks, Is.EqualTo(240), "时值不跟着挪");
            Assert.That(note.Velocity, Is.EqualTo(90), "力度也不跟着挪");
        });
    }

    /// <summary>一组音挪的是同一个量，相对位置一个都不变。</summary>
    [Test]
    public void 挪一组音相对位置一个都不变()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100),
            new Note(64, 120, 240, 100),
            new Note(67, 360, 240, 100)));

        var edited = _editor.MoveNotes(song, All(0, 3), 96, 3);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 96, 216, 456 }));
            Assert.That(Pitches(edited, 0), Is.EqualTo(new[] { 63, 67, 70 }));
            Assert.That(Gaps(edited, 0), Is.EqualTo(Gaps(song, 0)), "相邻音的间距一个都没变");
        });
    }

    [Test]
    public void 挪动可以跨轨一条命令动两条轨()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100)),
            Bass(new Note(40, 480, 240, 100)));

        var edited = _editor.MoveNotes(song, new[] { Ref(0, 0), Ref(1, 0) }, 240, 12);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].Notes[0], Is.EqualTo(new Note(72, 240, 240, 100)));
            Assert.That(edited.Tracks[1].Notes[0], Is.EqualTo(new Note(52, 720, 240, 100)));
        });
    }

    /// <summary>整条轨一起挪：彼此的先后顺序不变，数组仍按起点升序。</summary>
    [Test]
    public void 整条轨一起挪之后音符之间的先后顺序不变()
    {
        var song = SongOf(Map(), Melody(
            new Note(72, 0, 120, 100),
            new Note(60, 120, 120, 100),
            new Note(65, 240, 120, 100)));

        var edited = _editor.MoveNotes(song, All(0, 3), 240, 1);

        Assert.Multiple(() =>
        {
            // 音高是打乱的：数组顺序只按起点排，与音高无关
            Assert.That(Pitches(edited, 0), Is.EqualTo(new[] { 73, 61, 66 }), "谁在谁前面一个都没变");
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 240, 360, 480 }), "仍然按起点升序");
        });
    }

    /// <summary>挪动越过没被选中的音之后，数组重排回起点升序。</summary>
    [Test]
    public void 挪动越过没选中的音之后数组重排了()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100),
            new Note(64, 480, 240, 100)));

        // 只挪第一个音，越过没被选中的第二个
        var edited = _editor.MoveNotes(song, new[] { Ref(0, 0) }, 960, 0);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 480, 960 }), "重排回起点升序");
            Assert.That(Pitches(edited, 0), Is.EqualTo(new[] { 64, 60 }), "没动的那个跑到前面去了");
        });
    }

    // ==================== 挪音符：整组一起夹 ====================

    /// <summary>拖到最左边时整组一起夹住：只挪得动「最小起点」那么多，间距保住。</summary>
    [Test]
    public void 拖到最左整组一起夹住间距还在()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 480, 240, 100),
            new Note(64, 960, 240, 100),
            new Note(67, 1440, 240, 100)));

        var edited = _editor.MoveNotes(song, All(0, 3), -1920, 0);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 0, 480, 960 }),
                "整组只挪得动 480（= 最小起点），不是每个都往左挪 1920");
            Assert.That(Gaps(edited, 0), Is.EqualTo(new long[] { 480, 480 }), "间距原封不动，不是压成一摞");
            Assert.That(Pitches(edited, 0), Is.EqualTo(new[] { 60, 64, 67 }), "音高一点没动");
        });
    }

    /// <summary>整组贴着左边界时夹完等于没挪，返回同一个引用。</summary>
    [Test]
    public void 整组贴着左边界时挪不动返回同一份曲子()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(64, 480, 240, 100)));

        Assert.That(_editor.MoveNotes(song, All(0, 2), -100, 0), Is.SameAs(song));
    }

    /// <summary>顶到 0 时最低音停住，组内高音跟着少挪。</summary>
    [Test]
    public void 音高顶到0时整组一起夹住音程还在()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(67, 0, 240, 100)));

        var edited = _editor.MoveNotes(song, All(0, 2), 0, -100);

        Assert.Multiple(() =>
        {
            Assert.That(Pitches(edited, 0), Is.EqualTo(new[] { 0, 7 }), "最低音顶到 0，高的那个也少挪 60");
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 0, 0 }), "时间一点没动");
        });
    }

    /// <summary>顶到 127 时最高音停住，组内低音跟着少挪。</summary>
    [Test]
    public void 音高顶到127时整组一起夹住音程还在()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(64, 0, 240, 100)));

        var edited = _editor.MoveNotes(song, All(0, 2), 0, 100);

        Assert.Multiple(() =>
        {
            Assert.That(Pitches(edited, 0), Is.EqualTo(new[] { 123, 127 }), "最高音顶到 127，低的那两个也少挪");
            Assert.That(Pitches(song, 0), Is.EqualTo(new[] { 60, 64 }), "原来那份一个字节没动");
        });
    }

    /// <summary>本来就贴着音高上界：夹完等于没挪。</summary>
    [Test]
    public void 整组贴着音高上界时挪不动返回同一份曲子()
    {
        var song = SongOf(Map(), Melody(new Note(124, 0, 240, 100), new Note(127, 0, 240, 100)));

        Assert.That(_editor.MoveNotes(song, All(0, 2), 0, 5), Is.SameAs(song));
    }

    /// <summary>时间和音高可以同时挪；两个方向各自夹各自的边。</summary>
    [Test]
    public void 时间和音高各夹各的边()
    {
        var song = SongOf(Map(), Melody(new Note(60, 240, 240, 100), new Note(64, 480, 240, 100)));

        var edited = _editor.MoveNotes(song, All(0, 2), -480, -100);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 0, 240 }), "整组只挪得动 240");
            Assert.That(Pitches(edited, 0), Is.EqualTo(new[] { 0, 4 }), "整组只降得动 60");
        });
    }

    // ==================== 挪音符：没改与越界 ====================

    [Test]
    public void 挪零格返回同一份曲子()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        Assert.Multiple(() =>
        {
            Assert.That(_editor.MoveNotes(song, All(0, 1), 0, 0), Is.SameAs(song), "一个都没挪");
            Assert.That(_editor.MoveNotes(song, Array.Empty<NoteRef>(), 240, 12), Is.SameAs(song), "空集没人可挪");
        });
    }

    /// <summary>没被碰到的轨连音符数组都原样复用，动过的那条才是新对象。</summary>
    [Test]
    public void 没被碰到的轨原样复用引用()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100)),
            Bass(new Note(40, 0, 240, 100)),
            Third(new Note(50, 0, 240, 100)));

        var edited = _editor.MoveNotes(song, new[] { Ref(1, 0) }, 240, 0);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0], Is.SameAs(song.Tracks[0]));
            Assert.That(edited.Tracks[0].Notes, Is.SameAs(song.Tracks[0].Notes), "连音符数组都是同一份");
            Assert.That(edited.Tracks[2], Is.SameAs(song.Tracks[2]));
            Assert.That(edited.Tracks[1], Is.Not.SameAs(song.Tracks[1]), "动过的那条是新对象");
            Assert.That(edited.Tracks[1].Notes, Is.Not.SameAs(song.Tracks[1].Notes));
            Assert.That(edited.TempoMap, Is.SameAs(song.TempoMap), "速度表不动");
        });
    }

    /// <summary>
    /// 认不出的 <see cref="NoteRef"/> 一律抛：轨下标越界报「越界」，号不在这条轨上报「几号音」。
    /// 增量为 0 时也照抛。
    /// </summary>
    [Test]
    public void 挪动时认不出的音符坐标抛中文错()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        var byTrack = Assert.Throws<ArgumentOutOfRangeException>(
            () => _editor.MoveNotes(song, new[] { Ref(2, 0) }, 10, 0));
        // 7 号是凭空来的：Ref(0, 5) 算出来的号还是照位置来的，这里要的是不照位置来的坏号
        var byId = Assert.Throws<ArgumentOutOfRangeException>(
            () => _editor.MoveNotes(song, new[] { new NoteRef(0, new NoteId(7)) }, 10, 0));
        var byZeroDelta = Assert.Throws<ArgumentOutOfRangeException>(
            () => _editor.MoveNotes(song, new[] { new NoteRef(0, new NoteId(7)) }, 0, 0));

        Assert.Multiple(() =>
        {
            Assert.That(byTrack!.Message, Does.Contain("越界"), "错误消息得是给人看的中文");
            Assert.That(byTrack.Message, Does.Contain("2"), "把实际值写出来");
            Assert.That(byTrack.Message, Does.Contain("1 条轨"), "合法范围也写出来");
            Assert.That(byId!.Message, Does.Contain("7 号音"), "报的是那个号，不是「越界」");
            Assert.That(byId.Message, Does.Contain("1 个音"), "顺带说清这条轨上有几个音");
            Assert.That(byId.Message, Does.Not.Contain("越界"), "说「越界」会把人往「号太大」带");
            Assert.That(byZeroDelta!.Message, Does.Contain("7 号音"));
        });
    }

    // ==================== 改时值 ====================

    [Test]
    public void 拉长一个音()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 240, 100)));

        var edited = _editor.SetNoteSpan(song, Ref(0, 0), 480, 960);

        Assert.That(edited.Tracks[0].Notes[0], Is.EqualTo(new Note(60, 480, 960, 100)));
    }

    [Test]
    public void 缩短一个音()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 960, 100)));

        var edited = _editor.SetNoteSpan(song, Ref(0, 0), 480, 240);

        Assert.That(edited.Tracks[0].Notes[0], Is.EqualTo(new Note(60, 480, 240, 100)), "起点不动，只有时值变短");
    }

    /// <summary>起点左移、尾巴钉住。</summary>
    [Test]
    public void 起点左移尾巴钉住()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 480, 100)));

        var edited = _editor.SetNoteSpan(song, Ref(0, 0), 240, 720);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].Notes[0].StartTick, Is.EqualTo(240));
            Assert.That(edited.Tracks[0].Notes[0].EndTick, Is.EqualTo(960), "尾巴一动没动");
        });
    }

    [Test]
    public void 时值最短夹到一个tick()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 480, 100)));

        Assert.Multiple(() =>
        {
            Assert.That(_editor.SetNoteSpan(song, Ref(0, 0), 480, 0).Tracks[0].Notes[0].LengthTicks,
                Is.EqualTo(1), "0 不算时值");
            Assert.That(_editor.SetNoteSpan(song, Ref(0, 0), 480, -100).Tracks[0].Notes[0].LengthTicks,
                Is.EqualTo(1));
        });
    }

    [Test]
    public void 起点夹到0()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 480, 100)));

        var edited = _editor.SetNoteSpan(song, Ref(0, 0), -500, 480);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].Notes[0].StartTick, Is.EqualTo(0), "起点不能是负的");
            Assert.That(edited.Tracks[0].Notes[0].LengthTicks, Is.EqualTo(480), "夹的是起点，时值不跟着缩");
        });
    }

    /// <summary>起点 + 时值不溢出成负数。</summary>
    [Test]
    public void 起点加时值不溢出()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100)));

        var stretched = _editor.SetNoteSpan(song, Ref(0, 0), long.MaxValue - 10, 480);
        var pinned = _editor.SetNoteSpan(song, Ref(0, 0), long.MaxValue, 480);

        Assert.Multiple(() =>
        {
            Assert.That(stretched.Tracks[0].Notes[0].StartTick, Is.EqualTo(long.MaxValue - 10),
                "起点是用户钉住的那一头，不动它");
            Assert.That(stretched.Tracks[0].Notes[0].LengthTicks, Is.EqualTo(10), "收时值到装得下为止");
            Assert.That(stretched.Tracks[0].Notes[0].EndTick, Is.EqualTo(long.MaxValue));

            // 起点贴在 long.MaxValue 上，连 1 个 tick 都塞不下，只好把起点退一格
            Assert.That(pinned.Tracks[0].Notes[0].StartTick, Is.EqualTo(long.MaxValue - 1));
            Assert.That(pinned.Tracks[0].Notes[0].LengthTicks, Is.EqualTo(1), "「时值至少 1」不让给溢出");
        });
    }

    /// <summary>改时值越过后面的邻居之后，数组重排回按起点升序。</summary>
    [Test]
    public void 改时值越过后面的邻居之后数组重排了()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100),      // 甲：被挪到乙丙之间
            new Note(64, 480, 240, 100),    // 乙
            new Note(67, 960, 240, 100)));  // 丙

        var edited = _editor.SetNoteSpan(song, Ref(0, 0), 700, 240);

        var notes = edited.Tracks[0].Notes;
        Assert.Multiple(() =>
        {
            Assert.That(notes.Select(n => n.Pitch), Is.EqualTo(new[] { 64, 60, 67 }),
                "原来的第一个音现在排在乙后面");
            Assert.That(notes.Select(n => n.StartTick), Is.EqualTo(new long[] { 480, 700, 960 }),
                "数组仍然按起点升序");
            Assert.That(notes, Has.Count.EqualTo(3), "一个音都没丢");
            Assert.That(notes, Is.EquivalentTo(new[] { new Note(60, 700, 240, 100), new Note(64, 480, 240, 100), new Note(67, 960, 240, 100) }),
                "三个音的内容都对，只是换了顺序");
        });
    }

    /// <summary>
    /// 挪到前面时也重排，同起点的两个音保持原来的先后（排序稳定，归 <see cref="Track.WithNotes"/> 维护）。
    /// </summary>
    [Test]
    public void 挪到最前面时也重排同起点的音保持原来的先后()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100),      // 甲：本来就在 0
            new Note(64, 480, 240, 100),    // 乙
            new Note(67, 960, 240, 100)));  // 丙：被挪到 0，和甲撞在一起

        var edited = _editor.SetNoteSpan(song, Ref(0, 2), 0, 240);

        var notes = edited.Tracks[0].Notes;
        Assert.Multiple(() =>
        {
            Assert.That(notes.Select(n => n.StartTick), Is.EqualTo(new long[] { 0, 0, 480 }));
            Assert.That(notes.Select(n => n.Pitch), Is.EqualTo(new[] { 60, 67, 64 }),
                "两个 0 起点的音按原来的先后排：甲在丙前面");
        });
    }

    /// <summary>改完和原来一模一样时返回同一个引用。</summary>
    [Test]
    public void 时值本来就是这一段时返回同一份曲子()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 240, 100)));

        Assert.Multiple(() =>
        {
            Assert.That(_editor.SetNoteSpan(song, Ref(0, 0), 480, 240), Is.SameAs(song));
            // 夹完正好等于原来：一个 1 tick 的音，起点要 -5、时值要 -5，夹完还是 (0, 1)
            var one = SongOf(Map(), Melody(new Note(60, 0, 1, 100)));
            Assert.That(_editor.SetNoteSpan(one, Ref(0, 0), -5, -5), Is.SameAs(one), "夹完等于没夹");
        });
    }

    [Test]
    public void 改时值只动目标轨别的轨原样复用()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100)),
            Bass(new Note(40, 0, 240, 100)));

        var edited = _editor.SetNoteSpan(song, Ref(0, 0), 0, 480);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[1], Is.SameAs(song.Tracks[1]));
            Assert.That(edited.Tracks[1].Notes, Is.SameAs(song.Tracks[1].Notes));
            Assert.That(edited.TempoMap, Is.SameAs(song.TempoMap));
        });
    }

    [Test]
    public void 改时值时越界的音符坐标抛中文错()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _editor.SetNoteSpan(song, Ref(9, 0), 0, 240));

        Assert.That(ex!.Message, Does.Contain("越界"));
    }

    // ==================== 删音符 ====================

    [Test]
    public void 删一个音()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(64, 480, 240, 100)));

        var edited = _editor.DeleteNotes(song, new[] { Ref(0, 0) });

        Assert.That(edited.Tracks[0].Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 64 }), "删掉的是说到的那个");
    }

    [Test]
    public void 删一组音只删说到的那些()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100), new Note(64, 480, 240, 100), new Note(67, 960, 240, 100)),
            Bass(new Note(40, 0, 240, 100)));

        var edited = _editor.DeleteNotes(song, new[] { Ref(0, 0), Ref(0, 2), Ref(1, 0) });

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 64 }), "没说到的不动");
            Assert.That(edited.Tracks[1].NoteCount, Is.EqualTo(0));
            Assert.That(edited.Tracks[0], Is.Not.SameAs(song.Tracks[0]));
        });
    }

    /// <summary>同一个音在 <c>notes</c> 里说三遍还是删那一个。</summary>
    [Test]
    public void 重复的音符坐标只说一遍()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(64, 480, 240, 100)));

        var edited = _editor.DeleteNotes(
            song, new[] { Ref(0, 0), Ref(0, 0), Ref(0, 0) });

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].NoteCount, Is.EqualTo(1));
            Assert.That(edited.Tracks[0].Notes[0].Pitch, Is.EqualTo(64));
        });
    }

    /// <summary>删光一条轨的音符之后轨还在。</summary>
    [Test]
    public void 删光一条轨的音符之后轨还在()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100)),
            Bass(new Note(40, 0, 240, 100)));

        var edited = _editor.DeleteNotes(song, new[] { Ref(0, 0) });

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks, Has.Count.EqualTo(2), "轨数不变");
            Assert.That(edited.Tracks[0].NoteCount, Is.EqualTo(0));
            Assert.That(edited.Tracks[0].Notes, Is.Empty);
            Assert.That(edited.Tracks[0].Name, Is.EqualTo("主旋律"), "名字还留着");
            Assert.That(edited.Tracks[0].Channel, Is.EqualTo(0));
            Assert.That(edited.Tracks[1], Is.SameAs(song.Tracks[1]), "没被碰到的轨原样复用");
            Assert.That(edited.TempoMap, Is.SameAs(song.TempoMap), "速度表不动");
        });
    }

    [Test]
    public void 删空集返回同一份曲子()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        Assert.That(_editor.DeleteNotes(song, Array.Empty<NoteRef>()), Is.SameAs(song));
    }

    [Test]
    public void 删音时认不出的音符坐标抛中文错()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        // 一好一坏：这条命令先查完再动手，坏的不会把好的拖下水
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _editor.DeleteNotes(song, new[] { Ref(0, 0), new NoteRef(0, new NoteId(7)) }));

        Assert.That(ex!.Message, Does.Contain("7 号音"));
    }

    // ==================== 剪一段（连时间一起抽走） ====================

    /// <summary>剪掉中间一段，后面的音整体前移接上，而不是留一段空白。</summary>
    [Test]
    public void 剪掉中间一段后面的音整体前移()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100),        // 切口之前
            new Note(64, 1920, 240, 100),     // 切口里
            new Note(67, 2400, 240, 100),     // 切口里
            new Note(72, 3840, 240, 100)));   // 切口之后

        var edited = _editor.CutRange(song, 0, 1920, 3840);   // 抽掉 [1920, 3840)，1920 tick

        Assert.Multiple(() =>
        {
            Assert.That(Pitches(edited, 0), Is.EqualTo(new[] { 60, 72 }), "切口里的两个没了，外面两个还在");
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 0, 1920 }),
                "后面那个提前了整整 1920 tick（3840 - 1920）");
            Assert.That(edited.Tracks[0].Notes[1].LengthTicks, Is.EqualTo(240), "前移的是位置，时值不跟着变");
            Assert.That(edited.Tracks[0].Notes[1].Velocity, Is.EqualTo(100), "力度也不动");
        });
    }

    /// <summary>
    /// 只剪这一条轨，别的轨原样复用引用；<see cref="Song.EndTick"/> 是所有轨的最大值，
    /// 剪一条不会让整曲变短。
    /// </summary>
    [Test]
    public void 只剪这一条轨别的轨一个字节都不动()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 1920, 240, 100), new Note(64, 3840, 240, 100)),
            Bass(new Note(40, 1920, 240, 100), new Note(43, 3840, 240, 100)));

        var edited = _editor.CutRange(song, 0, 1920, 3840);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[1], Is.SameAs(song.Tracks[1]), "没被碰到的轨原样复用");
            Assert.That(Starts(edited, 1), Is.EqualTo(new long[] { 1920, 3840 }), "贝斯还在原地");
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 1920 }), "主旋律接上了");
            Assert.That(edited.EndTick, Is.EqualTo(song.EndTick),
                "主旋律自己短了，整曲长度是两条轨的最大值，贝斯那条没剪，所以整曲不变");
            Assert.That(edited.TempoMap, Is.SameAs(song.TempoMap),
                "速度表不跟着挪 —— 变速曲子里被前移的那段会按它新位置上的速度演奏");
        });
    }

    /// <summary>跨过左切口的音在切口处剪断：左边那截留下。</summary>
    [Test]
    public void 跨过左切口的音在切口处剪断()
    {
        var song = SongOf(Map(), Melody(new Note(60, 1440, 720, 100)));   // [1440, 2160)，切口从 1920 起

        var edited = _editor.CutRange(song, 0, 1920, 3840);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 1440 }), "起点不动");
            Assert.That(edited.Tracks[0].Notes[0].EndTick, Is.EqualTo(1920), "尾巴剪在切口上");
        });
    }

    /// <summary>从切口里伸出右边的音：剪下伸出去的那一截，挪到左切口接上。</summary>
    [Test]
    public void 伸出右切口的音右边那截挪到左切口接上()
    {
        var song = SongOf(Map(), Melody(new Note(60, 3360, 720, 100)));   // [3360, 4080)，切口到 3840 止

        var edited = _editor.CutRange(song, 0, 1920, 3840);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 1920 }), "落到左切口上");
            Assert.That(edited.Tracks[0].Notes[0].LengthTicks, Is.EqualTo(240),
                "留下的是伸出去的那截 [3840, 4080)，240 tick，不是整个音的 720");
        });
    }

    /// <summary>一个音把整个切口盖住时只在左切口剪断，右边那截丢掉，不挪回来（音符数只减不增）。</summary>
    [Test]
    public void 整个切口被一个音盖住时只留左边那截()
    {
        var song = SongOf(Map(), Melody(new Note(60, 960, 3840, 100)));   // [960, 4800)，切口 [1920, 3840)

        var edited = _editor.CutRange(song, 0, 1920, 3840);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].NoteCount, Is.EqualTo(1), "一个音还是一个音，不分裂成两个");
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 960 }));
            Assert.That(edited.Tracks[0].Notes[0].EndTick, Is.EqualTo(1920), "剪在左切口，右边那截没了");
        });
    }

    /// <summary>混着各种形状的轨剪一刀：音符数只减不增（剪，不是分裂），数组仍然按起点升序。</summary>
    [Test]
    public void 剪完音符数只减不增而且仍然按起点升序()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100),        // 整个在切口之前
            new Note(62, 480, 1920, 100),     // 跨过左切口
            new Note(64, 1440, 3840, 100),    // 整个切口都被它盖住
            new Note(65, 1920, 240, 100),     // 整个在切口里
            new Note(67, 3360, 720, 100),     // 从切口里伸出右切口
            new Note(69, 3840, 240, 100),     // 正好从右切口起步，前移之后落在左切口上
            new Note(72, 4320, 240, 100)));   // 整个在切口之后

        var edited = _editor.CutRange(song, 0, 1920, 3840);
        var starts = Starts(edited, 0);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].NoteCount, Is.LessThanOrEqualTo(song.Tracks[0].NoteCount),
                "剪，不是分裂");
            Assert.That(starts, Is.Ordered, "按起点升序");
            Assert.That(edited.Tracks[0].Notes.All(n => n.LengthTicks >= 1), Is.True, "时值至少 1 个 tick");
            Assert.That(edited.Tracks[0].Notes.All(n => n.StartTick >= 0), Is.True, "起点不为负");
            // 67 和 69 都落到 1920 上（一个是挪回来的碎片，一个是前移过来的音）：
            // 同起点本来就合法，关键是两个都在
            Assert.That(starts, Is.EqualTo(new long[] { 0, 480, 1440, 1920, 1920, 2400 }));
        });
    }

    /// <summary>剪了等于没剪（零长度、剪到最后一个音之后）是正常输入，返回同一个引用。</summary>
    [Test]
    public void 剪了等于没剪时返回同一份曲子()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        Assert.Multiple(() =>
        {
            Assert.That(_editor.CutRange(song, 0, 1920, 1920), Is.SameAs(song), "起终点相等：零个 tick");
            Assert.That(_editor.CutRange(song, 0, 1920, 3840), Is.SameAs(song), "剪在最后一个音之后，没有音要动");
            Assert.That(_editor.CutRange(song, 0, 0, 0), Is.SameAs(song), "都是 0");
        });
    }

    [Test]
    public void 空轨上剪一段也是返回同一份曲子()
    {
        var song = SongOf(Map(), Melody());

        Assert.That(_editor.CutRange(song, 0, 0, 1920), Is.SameAs(song));
    }

    /// <summary>剪空一条轨之后轨还在：空声部不是没有声部。</summary>
    [Test]
    public void 剪空一条轨之后轨还在()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100)),
            Bass(new Note(40, 960, 240, 100)));

        var edited = _editor.CutRange(song, 0, 0, 1920);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks, Has.Count.EqualTo(2), "轨数不变");
            Assert.That(edited.Tracks[0].NoteCount, Is.EqualTo(0));
            Assert.That(edited.Tracks[0].Name, Is.EqualTo("主旋律"), "名字还留着");
            Assert.That(edited.Tracks[1], Is.SameAs(song.Tracks[1]), "没被碰到的轨原样复用");
        });
    }

    /// <summary>负的起点夹到 0 不抛：这两个 tick 是从用户填的小节号算出来的，算出来的坐标一律夹住。</summary>
    [Test]
    public void 负的起点夹到零()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(64, 1920, 240, 100)));

        Assert.That(
            Starts(_editor.CutRange(song, 0, -500, 1920), 0),
            Is.EqualTo(Starts(_editor.CutRange(song, 0, 0, 1920), 0)));
    }

    /// <summary>终点在起点之前是调用方的错，直接抛。</summary>
    [Test]
    public void 终点在起点之前抛中文错()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        var ex = Assert.Throws<ArgumentException>(() => _editor.CutRange(song, 0, 3840, 1920));

        Assert.That(ex!.Message, Does.Contain("之前"));
    }

    [Test]
    public void 剪一段时越界的轨下标抛中文错()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _editor.CutRange(song, 3, 0, 1920));

        Assert.That(ex!.Message, Does.Contain("越界"));
    }

    // ==================== 身份（编辑不换身份，剪断换） ====================

    /// <summary>挪动（含改音高）不换身份：还是同一个音换了个位置。</summary>
    [Test]
    public void 挪动之后身份不换()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100, new NoteId(1)),
            new Note(64, 480, 240, 100, new NoteId(2)),
            new Note(67, 960, 240, 100, new NoteId(3))));

        var edited = _editor.MoveNotes(song, new[] { Ref(0, 0), Ref(0, 2) }, 240, 2);

        Assert.Multiple(() =>
        {
            Assert.That(Pitches(edited, 0), Is.EqualTo(new[] { 62, 64, 69 }), "前提：动的真是第 1、3 个音");
            Assert.That(Ids(edited, 0), Is.EqualTo(new[] { 1, 2, 3 }), "挪位置、改音高都不换身份");
        });
    }

    /// <summary>越过邻居重排之后身份还跟着音走：被挪的音排在第二个，但它还是 1 号。</summary>
    [Test]
    public void 越过邻居重排之后身份还跟着音走()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100, new NoteId(1)),
            new Note(64, 480, 240, 100, new NoteId(2))));

        var edited = _editor.MoveNotes(song, new[] { Ref(0, 0) }, 960, 0);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 480, 960 }), "前提：数组重排了");
            Assert.That(Ids(edited, 0), Is.EqualTo(new[] { 2, 1 }),
                "身份跟音走：被挪过去的那个音现在是第二个，但它还是 1 号");
            Assert.That(edited.Tracks[0].Notes[1].Pitch, Is.EqualTo(60), "1 号就是被挪的那个音");
        });
    }

    /// <summary>改时值（含改起点）不换身份。</summary>
    [Test]
    public void 改时值之后身份不换()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100, new NoteId(1)),
            new Note(64, 480, 240, 100, new NoteId(2))));

        var edited = _editor.SetNoteSpan(song, Ref(0, 1), 240, 960);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 0, 240 }), "前提：起点真的被改了");
            Assert.That(Ids(edited, 0), Is.EqualTo(new[] { 1, 2 }), "改时值不换身份");
        });
    }

    /// <summary>删掉一个音，别人的身份不受影响：删音符不重编号，缺几个号无所谓。</summary>
    [Test]
    public void 删掉一个音别人的身份不受影响()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100, new NoteId(3)),
            new Note(64, 480, 240, 100, new NoteId(7))));

        // 坐标得手写（Ref 那个帮手发的号照位置来）：删的是排在最前面、号却是 3 的那个音
        var edited = _editor.DeleteNotes(song, new[] { new NoteRef(0, new NoteId(3)) });

        Assert.That(Ids(edited, 0), Is.EqualTo(new[] { 7 }),
            "删掉的是 3 号那个音，7 号原样留着");
    }

    /// <summary>跨过左切口剪出来的那一截是新音，发新号：从剪之前那一轨的最大号往上发，4 号之后是 5。</summary>
    [Test]
    public void 跨过左切口的左截是新身份()
    {
        var song = SongOf(Map(), Melody(new Note(60, 1440, 720, 100, new NoteId(4))));

        var edited = _editor.CutRange(song, 0, 1920, 3840);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 1440 }), "前提：确实剪出了一截");
            Assert.That(Ids(edited, 0), Is.EqualTo(new[] { 5 }), "碎片是新音，发新号");
        });
    }

    /// <summary>伸出右切口挪回来的那一截同样是新音，发新号。</summary>
    [Test]
    public void 伸出右切口挪回来的那截是新身份()
    {
        var song = SongOf(Map(), Melody(new Note(60, 3360, 720, 100, new NoteId(4))));

        var edited = _editor.CutRange(song, 0, 1920, 3840);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 1920 }), "前提：确实挪回来了一截");
            Assert.That(Ids(edited, 0), Is.EqualTo(new[] { 5 }), "碎片是新音，发新号");
        });
    }

    /// <summary>剪一刀之后：前移的音身份不变，剪出来的碎片发新号，整轨之内不重号。</summary>
    [Test]
    public void 剪一刀之后前移的不换身份碎片换新身份()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100, new NoteId(1)),        // 整个在切口之前
            new Note(62, 480, 1920, 100, new NoteId(2)),     // 跨过左切口 → 剪断，左截是新音
            new Note(64, 1440, 3840, 100, new NoteId(3)),    // 整个切口被它盖住 → 剪断，左截是新音
            new Note(65, 1920, 240, 100, new NoteId(4)),     // 整个在切口里 → 没了
            new Note(67, 3360, 720, 100, new NoteId(5)),     // 伸出右切口 → 挪回来，那截是新音
            new Note(69, 3840, 240, 100, new NoteId(6)),     // 正好从右切口起步 → 前移，身份不变
            new Note(72, 4320, 240, 100, new NoteId(7))));   // 整个在切口之后 → 前移，身份不变

        var edited = _editor.CutRange(song, 0, 1920, 3840);
        var ids = Ids(edited, 0);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 0, 480, 1440, 1920, 1920, 2400 }),
                "前提：这一刀的形状和上面那条「只减不增」的测试一致");
            Assert.That(ids, Is.EqualTo(new[] { 1, 8, 9, 10, 6, 7 }),
                "1/6/7 是前移的音（身份不变），8/9/10 是这一刀剪出来的碎片（新发的）");
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Length), "一轨之内身份不重号");
        });
    }

    /// <summary>新号是「现有最大号 + 1」而不是「这一轨有几个音」。</summary>
    [Test]
    public void 剪出来的新身份从现有最大号往上发()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100, new NoteId(2)),
            new Note(62, 120, 240, 100, new NoteId(9)),      // 号不连续：中间那个早就删掉了
            new Note(64, 3360, 720, 100, new NoteId(3))));   // 伸出右切口 → 新号

        var edited = _editor.CutRange(song, 0, 1920, 3840);

        Assert.That(Ids(edited, 0), Is.EqualTo(new[] { 2, 9, 10 }),
            "新号接着最大的 9 往上发（按「有几个音」发会发成 4，撞上早就没了的那几个号）");
    }

    // ==================== 改轨名 ====================

    [Test]
    public void 改名之后名字变了别的字段原样()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100)),
            Bass(new Note(40, 0, 240, 100)));

        var edited = _editor.RenameTrack(song, 0, "口琴声部");

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].Name, Is.EqualTo("口琴声部"));
            Assert.That(edited.Tracks[0].Notes, Is.SameAs(song.Tracks[0].Notes), "音符一个字节都不动");
            Assert.That(edited.Tracks[0].TrackIndex, Is.EqualTo(song.Tracks[0].TrackIndex), "出处编号不动");
            Assert.That(edited.Tracks[0].Channel, Is.EqualTo(song.Tracks[0].Channel));
            Assert.That(edited.Tracks[0].Program, Is.EqualTo(song.Tracks[0].Program));
            Assert.That(edited.Tracks[1], Is.SameAs(song.Tracks[1]), "别的轨原样复用");
            Assert.That(edited.TempoMap, Is.SameAs(song.TempoMap));
        });
    }

    /// <summary>名字两端的空白会被去掉（粘过来的名字常带尾空格）。</summary>
    [Test]
    public void 名字两端的空白被去掉()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        var edited = _editor.RenameTrack(song, 0, "  低音声部  ");

        Assert.That(edited.Tracks[0].Name, Is.EqualTo("低音声部"));
    }

    /// <summary>改成原来同一个名字等于没改，返回同一个引用。</summary>
    [Test]
    public void 改成原来同一个名字返回同一份曲子()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        Assert.That(_editor.RenameTrack(song, 0, "主旋律"), Is.SameAs(song));
    }

    /// <summary>去掉两端空白之后和原来一样，同样是没改。</summary>
    [Test]
    public void 只多了几个空格的名字等于没改()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        Assert.That(_editor.RenameTrack(song, 0, " 主旋律 "), Is.SameAs(song));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\t")]
    public void 名字去掉空白后什么都不剩就抛中文错(string name)
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        var ex = Assert.Throws<ArgumentException>(() => _editor.RenameTrack(song, 0, name));

        Assert.That(ex!.Message, Does.Contain("轨名"), "错误消息得是给人看的中文");
    }

    [TestCase(-1)]
    [TestCase(1)]
    public void 改名时轨下标越界抛中文错(int trackIndex)
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _editor.RenameTrack(song, trackIndex, "新名字"));

        Assert.That(ex!.Message, Does.Contain("越界"));
    }

    // ==================== 删轨 ====================

    /// <summary>
    /// 删中间一条：剩下的轨顺序对，而且 <see cref="Track.TrackIndex"/> 不重编号
    /// （它是轨块的出处标记，不是排名）。
    /// </summary>
    [Test]
    public void 删中间一条剩下的轨顺序对且出处编号没变()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100)),
            Bass(new Note(40, 0, 240, 100)),
            Third(new Note(50, 0, 240, 100)));

        var edited = _editor.DeleteTrack(song, 1);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks, Has.Count.EqualTo(2));
            Assert.That(edited.Tracks[0], Is.SameAs(song.Tracks[0]), "前面的轨原样复用");
            Assert.That(edited.Tracks[1], Is.SameAs(song.Tracks[2]), "后面的轨往前挪一格，还是原来那一个对象");
            Assert.That(edited.Tracks.Select(t => t.TrackIndex), Is.EqualTo(new[] { 0, 2 }),
                "出处编号原样：删掉 1 号不会把 2 号改成 1 号");
            Assert.That(edited.Tracks.Select(t => t.Name), Is.EqualTo(new[] { "主旋律", "鼓点" }));
            Assert.That(edited.TempoMap, Is.SameAs(song.TempoMap), "速度表不动");
        });
    }

    /// <summary>只剩一条轨时照样删，空曲子是合法状态。</summary>
    [Test]
    public void 只剩一条轨时照样删()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        var edited = _editor.DeleteTrack(song, 0);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks, Is.Empty);
            Assert.That(edited.EndTick, Is.EqualTo(0));
            Assert.That(edited.TotalSeconds, Is.EqualTo(0));
        });
    }

    [Test]
    public void 一条一条删到一条不剩也不崩()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100)),
            Bass(new Note(40, 0, 240, 100)),
            Third(new Note(50, 0, 240, 100)));

        var two = _editor.DeleteTrack(song, 1);
        var one = _editor.DeleteTrack(two, 1);
        var none = _editor.DeleteTrack(one, 0);

        Assert.Multiple(() =>
        {
            Assert.That(two.Tracks.Select(t => t.Name), Is.EqualTo(new[] { "主旋律", "鼓点" }));
            Assert.That(one.Tracks.Select(t => t.Name), Is.EqualTo(new[] { "主旋律" }));
            Assert.That(none.Tracks, Is.Empty);
            Assert.That(none.TempoMap, Is.SameAs(song.TempoMap), "删到空也留着速度表");
        });
    }

    [TestCase(-1)]
    [TestCase(2)]
    public void 删轨时轨下标越界抛中文错(int trackIndex)
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)), Bass(new Note(40, 0, 240, 100)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _editor.DeleteTrack(song, trackIndex));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("越界"), "错误消息得是给人看的中文");
            Assert.That(ex.Message, Does.Contain("2 条轨"), "合法范围也写出来");
        });
    }

    /// <summary>已经空了的曲子上再删轨照样越界抛。</summary>
    [Test]
    public void 空曲子上删轨也抛()
    {
        var empty = _editor.DeleteTrack(SongOf(Map(), Melody(new Note(60, 0, 240, 100))), 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => _editor.DeleteTrack(empty, 0));
    }

    // ==================== 撤销装饰器 ====================

    /// <summary>这几条命令在撤销装饰器上各占一格：盯的是转发时有没有顺手调 <c>Record</c>。</summary>
    [Test]
    public void 新命令在撤销装饰器上各占一格()
    {
        var editor = new UndoableSongEditor(new SongEditor());
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(64, 480, 240, 100)));

        var moved = editor.MoveNotes(song, new[] { Ref(0, 0) }, 240, 0);
        var spanned = editor.SetNoteSpan(moved, Ref(0, 0), 240, 120);
        var deleted = editor.DeleteNotes(spanned, new[] { Ref(0, 1) });
        var renamed = editor.RenameTrack(deleted, 0, "低音");
        var dropped = editor.DeleteTrack(renamed, 0);

        Assert.Multiple(() =>
        {
            Assert.That(dropped.Tracks, Is.Empty, "五条真的都生效了");
            Assert.That(editor.Undo(), Is.SameAs(renamed), "先撤回删轨");
            Assert.That(editor.Undo(), Is.SameAs(deleted));
            Assert.That(editor.Undo(), Is.SameAs(spanned));
            Assert.That(editor.Undo(), Is.SameAs(moved));
            Assert.That(editor.Undo(), Is.SameAs(song), "一路撤回到最初那一份");
            Assert.That(editor.CanUndo, Is.False);
        });
    }

    /// <summary>没改的那几下不占撤销格子。</summary>
    [Test]
    public void 新命令没改时装饰器也不记账()
    {
        var editor = new UndoableSongEditor(new SongEditor());
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        Assert.Multiple(() =>
        {
            Assert.That(editor.MoveNotes(song, new[] { Ref(0, 0) }, 0, 0), Is.SameAs(song));
            Assert.That(editor.SetNoteSpan(song, Ref(0, 0), 0, 240), Is.SameAs(song));
            Assert.That(editor.DeleteNotes(song, Array.Empty<NoteRef>()), Is.SameAs(song));
            Assert.That(editor.RenameTrack(song, 0, "主旋律"), Is.SameAs(song));
            Assert.That(editor.CanUndo, Is.False, "一下都没改，一格都不该占");
        });
    }

    // ==================== 帮手 ====================

    private static Song SongOf(TempoMap map, params Track[] tracks) => new(tracks, map);

    private static TempoMap Map() => new(TimeDivision.PulsesPerQuarter(480));

    private static Track Melody(params Note[] notes) => new(0, 0, "主旋律", 24, Numbered(notes));

    private static Track Bass(params Note[] notes) => new(1, 1, "贝斯", 33, Numbered(notes));

    private static Track Third(params Note[] notes) => new(2, 9, "鼓点", 0, Numbered(notes));

    /// <summary>
    /// 给没写身份的音按数组顺序发 1..N 号（和导入那条路的 <c>NoteIdentity.AssignInOrder</c> 是同一件事）；
    /// 显式写好号的音一个都不动。
    /// </summary>
    private static Note[] Numbered(Note[] notes)
    {
        var numbered = new Note[notes.Length];
        for (int i = 0; i < notes.Length; i++)
            numbered[i] = notes[i].Id == NoteId.None ? notes[i] with { Id = new NoteId(i + 1) } : notes[i];

        return numbered;
    }

    /// <summary>
    /// 第 <paramref name="track"/> 条轨上第 <paramref name="index"/> 个音（数组序，0 起）的坐标。
    /// 号是 <see cref="Numbered"/> 发的，第 i 个音就是 i+1 号；真实代码里的号来自模型，不这么算。
    /// </summary>
    private static NoteRef Ref(int track, int index) => new(track, new NoteId(index + 1));

    /// <summary>第 <paramref name="track"/> 条轨的前 <paramref name="count"/> 个音，按数组顺序。</summary>
    private static NoteRef[] All(int track, int count)
        => Enumerable.Range(0, count).Select(i => Ref(track, i)).ToArray();

    private static long[] Starts(Song song, int track)
        => song.Tracks[track].Notes.Select(n => n.StartTick).ToArray();

    private static int[] Pitches(Song song, int track)
        => song.Tracks[track].Notes.Select(n => n.Pitch).ToArray();

    /// <summary>第 <paramref name="track"/> 条轨的身份号，按数组顺序（比的是 <c>Id.Value</c>）。</summary>
    private static int[] Ids(Song song, int track)
        => song.Tracks[track].Notes.Select(n => n.Id.Value).ToArray();

    /// <summary>相邻音起点之间的间距。</summary>
    private static long[] Gaps(Song song, int track)
    {
        var starts = Starts(song, track);
        return starts.Zip(starts.Skip(1), (a, b) => b - a).ToArray();
    }
}

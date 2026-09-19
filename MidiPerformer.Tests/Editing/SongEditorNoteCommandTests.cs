using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Editing;
using NUnit.Framework;

namespace MidiPerformer.Tests.Editing;

/// <summary>
/// 音符级的编辑命令（挪 / 改时值 / 删 / 剪一段 / 改名 / 删轨）的**外部行为** —— 还是 S2 缝：
/// 进去一份 <see cref="Song"/>，出来一份 <see cref="Song"/>，一个私有字段都不碰。
///
/// 与 <c>SongEditorTests</c>（改 BPM / 移调）分开一个文件，是因为这两条命令的**危险形状不一样**：
/// 改 BPM 只动速度表，音符一个字节都不碰，最坏的错法是「无形地改坏了时间换算」；
/// 这里这几条是真在音符数组上动手，最坏的错法是**把数组顺序弄乱**（
/// <see cref="Track.Notes"/> 承诺按起点升序，破了之后每个按下标认音的地方都认错音，
/// 而且一声不吭）。所以下面「整组一起夹」和「越过邻居要重排」各占一组测试。
///
/// 每条命令都有两条共同的不变量，这里逐条盯着：
/// <list type="number">
/// <item>没改就返回传进来的**同一个引用**（装饰器拿它当「这条命令改没改」的判据，
/// 返回内容一样的新对象会让撤销栈里攒下按了没反应的格子）；</item>
/// <item>没被碰到的 <see cref="Track"/> **原样复用引用**（数组可以是新的，元素该是旧的）。</item>
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

        var edited = _editor.MoveNotes(song, new[] { new NoteRef(0, 0) }, 240, 2);

        var note = edited.Tracks[0].Notes[0];
        Assert.Multiple(() =>
        {
            Assert.That(note.StartTick, Is.EqualTo(720));
            Assert.That(note.Pitch, Is.EqualTo(62));
            Assert.That(note.LengthTicks, Is.EqualTo(240), "时值不跟着挪");
            Assert.That(note.Velocity, Is.EqualTo(90), "力度也不跟着挪");
        });
    }

    /// <summary>一组音挪的是**同一个量** —— 所以相对位置（和弦的形状、两声部之间的错位）一个都不变。</summary>
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

        var edited = _editor.MoveNotes(song, new[] { new NoteRef(0, 0), new NoteRef(1, 0) }, 240, 12);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].Notes[0], Is.EqualTo(new Note(72, 240, 240, 100)));
            Assert.That(edited.Tracks[1].Notes[0], Is.EqualTo(new Note(52, 720, 240, 100)));
        });
    }

    /// <summary>
    /// 把**整条轨的音**一起挪，彼此先后当然一个都不变（挪的是同一个量）。
    ///
    /// 但要看清这条**证明不了什么**：它挪的是全部三个音，谁也越不过谁。
    /// 「挪同一个量 → 顺序不会变」这个推理只在**选中的那几个音之间**成立 ——
    /// 一个选中的音照样能越过一个**没被选中**的音，那种情形钉在下面
    /// <c>挪动越过没选中的音之后数组重排了</c> 里。这条命令**是要重排的**。
    /// </summary>
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
            // 音高是刻意打乱的：数组顺序本来就只按起点排，跟音高无关
            Assert.That(Pitches(edited, 0), Is.EqualTo(new[] { 73, 61, 66 }), "谁在谁前面一个都没变");
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 240, 360, 480 }), "仍然按起点升序");
        });
    }

    /// <summary>
    /// **挪动也会越过邻居** —— 越过的不是选中的同伴，而是**没被选中**的那些。
    ///
    /// 这是「一组音挪的是同一个量，所以顺序不会变」那句话漏掉的那一半：
    /// 只选中 <c>A@0</c> 往右挪 960，中间那个没被选中的 <c>B@480</c> 就跑到 A 前面去了。
    /// 不重排的话数组是 <c>[A@960, B@480]</c>，<see cref="Track.Notes"/> 的升序承诺当场破掉，
    /// 而且一切照常跑、照常画，只在导出时写成「后一个音先响」的事件序列 ——
    /// 同一个音高上的 note-on / note-off 一乱，发到游戏里就是漏音或卡音。
    /// </summary>
    [Test]
    public void 挪动越过没选中的音之后数组重排了()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100),
            new Note(64, 480, 240, 100)));

        // 只挪第一个音，一口气越过第二个 —— 第二个没被选中，不在 notes 里
        var edited = _editor.MoveNotes(song, new[] { new NoteRef(0, 0) }, 960, 0);

        Assert.Multiple(() =>
        {
            Assert.That(Starts(edited, 0), Is.EqualTo(new long[] { 480, 960 }), "重排回起点升序");
            Assert.That(Pitches(edited, 0), Is.EqualTo(new[] { 64, 60 }), "没动的那个跑到前面去了");
        });
    }

    // ==================== 挪音符：整组一起夹 ====================

    /// <summary>
    /// 拖到最左边时**整组一起夹住**：整组只挪得动「最小起点」那么多，形状保住。
    ///
    /// 逐个夹的话三个音会一起叠在 tick 0 上变成一摞，和弦当场变单音 ——
    /// 这是这条命令最容易写错的地方，也是「整组一起夹」这个说法存在的全部理由。
    /// </summary>
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

    /// <summary>整组本来就贴着左边界：夹完等于没挪，返回的还是传进来那一个引用。</summary>
    [Test]
    public void 整组贴着左边界时挪不动返回同一份曲子()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(64, 480, 240, 100)));

        Assert.That(_editor.MoveNotes(song, All(0, 2), -100, 0), Is.SameAs(song));
    }

    /// <summary>往右挪整组顶到音高 0：最低音停住，组内高音跟着少挪（音程保住）。</summary>
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

    /// <summary>往高挪整组顶到音高 127：最高音停住，组内低音跟着少挪（音程保住）。</summary>
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

    /// <summary>
    /// 没被碰到的轨**连音符数组都原样复用**，动过的那条才是新的。
    ///
    /// 这一条是那份 spec 的核心不变量：一步编辑真正留下的只有动过的那一条轨，
    /// 撤销栈因此不是「一摞快照」。
    /// </summary>
    [Test]
    public void 没被碰到的轨原样复用引用()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100)),
            Bass(new Note(40, 0, 240, 100)),
            Third(new Note(50, 0, 240, 100)));

        var edited = _editor.MoveNotes(song, new[] { new NoteRef(1, 0) }, 240, 0);

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
    /// 越界的 <see cref="NoteRef"/> 抛，而不是「这个音不存在，跳过」。
    ///
    /// <see cref="NoteRef"/> 只在它被算出来的那一份 <see cref="Song"/> 上有效，拿旧下标来用是
    /// 调用方的 bug。悄悄跳过的话，用户看到的是「拖了五个音只有一个动了」，却没有任何地方报错。
    ///
    /// 增量为 0 时**也照抛**：同一个坏坐标不该一会儿没事一会儿炸 ——
    /// 「没改就还回来同一个」说的是结果，不是「跳过所有检查」。
    /// </summary>
    [Test]
    public void 挪动时越界的音符坐标抛中文错()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        var byTrack = Assert.Throws<ArgumentOutOfRangeException>(
            () => _editor.MoveNotes(song, new[] { new NoteRef(2, 0) }, 10, 0));
        var byIndex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _editor.MoveNotes(song, new[] { new NoteRef(0, 5) }, 10, 0));
        var byZeroDelta = Assert.Throws<ArgumentOutOfRangeException>(
            () => _editor.MoveNotes(song, new[] { new NoteRef(0, 5) }, 0, 0));

        Assert.Multiple(() =>
        {
            Assert.That(byTrack!.Message, Does.Contain("越界"), "错误消息得是给人看的中文");
            Assert.That(byTrack.Message, Does.Contain("2"), "把实际值写出来");
            Assert.That(byTrack.Message, Does.Contain("1 条轨"), "合法范围也写出来");
            Assert.That(byIndex!.Message, Does.Contain("越界"));
            Assert.That(byIndex.Message, Does.Contain("5"));
            Assert.That(byZeroDelta!.Message, Does.Contain("越界"));
        });
    }

    // ==================== 改时值 ====================

    [Test]
    public void 拉长一个音()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 240, 100)));

        var edited = _editor.SetNoteSpan(song, new NoteRef(0, 0), 480, 960);

        Assert.That(edited.Tracks[0].Notes[0], Is.EqualTo(new Note(60, 480, 960, 100)));
    }

    [Test]
    public void 缩短一个音()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 960, 100)));

        var edited = _editor.SetNoteSpan(song, new NoteRef(0, 0), 480, 240);

        Assert.That(edited.Tracks[0].Notes[0], Is.EqualTo(new Note(60, 480, 240, 100)), "起点不动，只有时值变短");
    }

    /// <summary>
    /// 起点左移、尾巴钉住：界面拖左边缘时算的就是 <c>start + len</c> 不变，
    /// 这里断的是命令本身收下这两个数之后老实照办。
    /// </summary>
    [Test]
    public void 起点左移尾巴钉住()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 480, 100)));

        var edited = _editor.SetNoteSpan(song, new NoteRef(0, 0), 240, 720);

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
            Assert.That(_editor.SetNoteSpan(song, new NoteRef(0, 0), 480, 0).Tracks[0].Notes[0].LengthTicks,
                Is.EqualTo(1), "0 不算时值");
            Assert.That(_editor.SetNoteSpan(song, new NoteRef(0, 0), 480, -100).Tracks[0].Notes[0].LengthTicks,
                Is.EqualTo(1));
        });
    }

    [Test]
    public void 起点夹到0()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 480, 100)));

        var edited = _editor.SetNoteSpan(song, new NoteRef(0, 0), -500, 480);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].Notes[0].StartTick, Is.EqualTo(0), "起点不能是负的");
            Assert.That(edited.Tracks[0].Notes[0].LengthTicks, Is.EqualTo(480), "夹的是起点，时值不跟着缩");
        });
    }

    /// <summary>
    /// 起点 + 时值不能溢出成负数：一个绕回去的终点会让这个音跑到曲子开头之前，
    /// 画不出来、导不出去，而且一路没人报错。
    /// </summary>
    [Test]
    public void 起点加时值不溢出()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100)));

        var stretched = _editor.SetNoteSpan(song, new NoteRef(0, 0), long.MaxValue - 10, 480);
        var pinned = _editor.SetNoteSpan(song, new NoteRef(0, 0), long.MaxValue, 480);

        Assert.Multiple(() =>
        {
            Assert.That(stretched.Tracks[0].Notes[0].StartTick, Is.EqualTo(long.MaxValue - 10),
                "起点是用户钉住的那一头，不动它");
            Assert.That(stretched.Tracks[0].Notes[0].LengthTicks, Is.EqualTo(10), "收时值到装得下为止");
            Assert.That(stretched.Tracks[0].Notes[0].EndTick, Is.EqualTo(long.MaxValue));

            // 起点已经贴在 long.MaxValue 上：连 1 个 tick 都塞不下，只好反过来把起点退一格
            Assert.That(pinned.Tracks[0].Notes[0].StartTick, Is.EqualTo(long.MaxValue - 1));
            Assert.That(pinned.Tracks[0].Notes[0].LengthTicks, Is.EqualTo(1), "「时值至少 1」不让给溢出");
        });
    }

    /// <summary>
    /// **改时值可能让音符越过邻居** —— 那时这条轨的数组必须重排回「按起点升序」。
    ///
    /// 这一条是这几条命令里最容易漏、也最难发现的一步：不重排的话一切照常跑、照常画，
    /// 只是后面每个「按下标认音」的地方都认错了音（选中、删除、拖动全都错位），
    /// 而且报不出任何错。所以单独盯着它。
    /// </summary>
    [Test]
    public void 改时值越过后面的邻居之后数组重排了()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100),      // 甲：被挪到乙丙之间
            new Note(64, 480, 240, 100),    // 乙
            new Note(67, 960, 240, 100)));  // 丙

        var edited = _editor.SetNoteSpan(song, new NoteRef(0, 0), 700, 240);

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
    /// 挪到前面也一样重排，而且**同起点的两个音保持原来的先后**（<c>OrderBy</c> 是稳定排序）。
    ///
    /// 稳定性不是花边：它让「一个音都没越过邻居」这种最常见的改动之后数组一个下标都不变，
    /// 于是界面那套「改完重新算选中集」不用面对无谓的洗牌。
    /// </summary>
    [Test]
    public void 挪到最前面时也重排同起点的音保持原来的先后()
    {
        var song = SongOf(Map(), Melody(
            new Note(60, 0, 240, 100),      // 甲：本来就在 0
            new Note(64, 480, 240, 100),    // 乙
            new Note(67, 960, 240, 100)));  // 丙：被挪到 0，和甲撞在一起

        var edited = _editor.SetNoteSpan(song, new NoteRef(0, 2), 0, 240);

        var notes = edited.Tracks[0].Notes;
        Assert.Multiple(() =>
        {
            Assert.That(notes.Select(n => n.StartTick), Is.EqualTo(new long[] { 0, 0, 480 }));
            Assert.That(notes.Select(n => n.Pitch), Is.EqualTo(new[] { 60, 67, 64 }),
                "两个 0 起点的音按原来的先后排：甲在丙前面");
        });
    }

    /// <summary>改完和原来一模一样：返回同一个引用（装饰器靠它判断这一下要不要记一笔）。</summary>
    [Test]
    public void 时值本来就是这一段时返回同一份曲子()
    {
        var song = SongOf(Map(), Melody(new Note(60, 480, 240, 100)));

        Assert.Multiple(() =>
        {
            Assert.That(_editor.SetNoteSpan(song, new NoteRef(0, 0), 480, 240), Is.SameAs(song));
            // 夹完正好等于原来：一个 1 tick 的音，起点要 -5、时值要 -5，夹完还是 (0, 1)
            var one = SongOf(Map(), Melody(new Note(60, 0, 1, 100)));
            Assert.That(_editor.SetNoteSpan(one, new NoteRef(0, 0), -5, -5), Is.SameAs(one), "夹完等于没夹");
        });
    }

    [Test]
    public void 改时值只动目标轨别的轨原样复用()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100)),
            Bass(new Note(40, 0, 240, 100)));

        var edited = _editor.SetNoteSpan(song, new NoteRef(0, 0), 0, 480);

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
            () => _editor.SetNoteSpan(song, new NoteRef(9, 0), 0, 240));

        Assert.That(ex!.Message, Does.Contain("越界"));
    }

    // ==================== 删音符 ====================

    [Test]
    public void 删一个音()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(64, 480, 240, 100)));

        var edited = _editor.DeleteNotes(song, new[] { new NoteRef(0, 0) });

        Assert.That(edited.Tracks[0].Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 64 }), "删掉的是说到的那个");
    }

    [Test]
    public void 删一组音只删说到的那些()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100), new Note(64, 480, 240, 100), new Note(67, 960, 240, 100)),
            Bass(new Note(40, 0, 240, 100)));

        var edited = _editor.DeleteNotes(song, new[] { new NoteRef(0, 0), new NoteRef(0, 2), new NoteRef(1, 0) });

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 64 }), "没说到的不动");
            Assert.That(edited.Tracks[1].NoteCount, Is.EqualTo(0));
            Assert.That(edited.Tracks[0], Is.Not.SameAs(song.Tracks[0]));
        });
    }

    /// <summary>
    /// 同一个音在 <c>notes</c> 里说三遍还是删那一个。
    ///
    /// 界面横拖出来的选中集本来就是「顺手并起来的」，重叠是常态。不去重不会多删音符
    /// （删的是同一个音），但会让「还剩几个」这类按集合算出来的数对不上。
    /// </summary>
    [Test]
    public void 重复的音符坐标只说一遍()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(64, 480, 240, 100)));

        var edited = _editor.DeleteNotes(
            song, new[] { new NoteRef(0, 0), new NoteRef(0, 0), new NoteRef(0, 0) });

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].NoteCount, Is.EqualTo(1));
            Assert.That(edited.Tracks[0].Notes[0].Pitch, Is.EqualTo(64));
        });
    }

    /// <summary>
    /// 删光一条轨的音符之后**轨还在**：轨是声部，空声部和没有声部是两回事，
    /// 用户把一条轨上的音全删了，那条轨还该留着等他往上放新的 —— 删轨是另一条命令。
    /// </summary>
    [Test]
    public void 删光一条轨的音符之后轨还在()
    {
        var song = SongOf(Map(),
            Melody(new Note(60, 0, 240, 100)),
            Bass(new Note(40, 0, 240, 100)));

        var edited = _editor.DeleteNotes(song, new[] { new NoteRef(0, 0) });

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
    public void 删音时越界的音符坐标抛中文错()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => _editor.DeleteNotes(song, new[] { new NoteRef(0, 0), new NoteRef(0, 7) }));

        Assert.That(ex!.Message, Does.Contain("越界"));
    }

    // ==================== 剪一段（连时间一起抽走） ====================

    /// <summary>
    /// 这条命令的正身：中间那段抽走，后面的音**提前落下来**，不是留一段空白。
    ///
    /// 和 <see cref="SongEditor.DeleteNotes"/> 摆在一起看最清楚 —— 同一条轨、同一段区间：
    /// 删音符留下「第 2 小节空着，第 3 小节的东西还在第 3 小节」，
    /// 剪一段留下「第 2 小节整个没了，第 3 小节的东西挪到第 2 小节」。
    /// 用户说的「不是清除音符，是自动拼接」就是这条。
    /// </summary>
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
    /// <b>只剪这一条轨</b>，别的轨一个字节都不动 —— 于是从这一刀往后，这条轨和别的轨**永久错位**。
    ///
    /// 那是这个功能的定义，不是副作用：要的就是「把这声部里多余的那段剪掉，剩下的接上」。
    /// 跟着来的两个事实一并钉在这里：别的轨原样复用引用；
    /// <see cref="Song.EndTick"/> 是**所有轨**的最大值，剪一条不会让整曲变短。
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

    /// <summary>跨过左切口的音在切口处剪断：左边那截留下，右边那截本来就在要抽走的那段里。</summary>
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

    /// <summary>
    /// 从切口里伸出右边的音：剪下伸出去的那一截，**挪到左切口接上**。
    ///
    /// 这条和下面「整个切口被一个音盖住」是一对，差别只在**起点在不在切口里**：
    /// 起点在切口里，它留在左切口之前的部分就不存在，右边那截是唯一救得回来的东西。
    /// </summary>
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

    /// <summary>
    /// 一个音把整个切口盖住：只在左切口剪断，右边那截**丢掉**，不挪回来。
    ///
    /// 挪回来的话它会紧贴着左截 —— 一个音变成两个，「剪」就成了「分裂」。
    /// 这条命令的不变量是**音符数只减不增**，代价是一个长音会被剪短（这里从 3840 只剩 960）。
    /// </summary>
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

    /// <summary>
    /// 一条混着各种形状的轨剪一刀，跑完盯着两条不变量：
    /// <b>音符数只减不增</b>（剪，不是分裂），<b>数组仍然按起点升序</b>。
    ///
    /// 第二条要特别看，因为这条命令**结尾没有重排**（挪音符和改时值那两条都有一句 <c>OrderBy</c>）：
    /// 新的起点是旧起点的单调不减函数，有序数组过一遍出来还有序。这条测试就是那句话的证据 ——
    /// 哪天实现里把分派顺序改错了，升序这里立刻红。
    /// </summary>
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
            // 67 和 69 都落到 1920 上：一个是从切口里伸出去那截挪回来的，一个是正好从右切口起步
            // 前移过来的。同起点本来就是合法的（和弦就是这样），关键是**两个都在、顺序没乱**。
            Assert.That(starts, Is.EqualTo(new long[] { 0, 480, 1440, 1920, 1920, 2400 }));
        });
    }

    /// <summary>
    /// 剪了等于没剪：规矩和 <c>DeleteNotes</c> 收到空集一样，是**正常输入**不是错误 ——
    /// 两个框填成同一小节、或者剪到这条轨的尾巴之外去，都该安安静静什么也不发生。
    ///
    /// 「返回同一个引用」在这里是硬要求，不是优化：装饰器拿它当「这条命令改没改」的判据，
    /// 返回一份内容一样的新对象会让撤销栈里攒下按了没反应的格子。
    /// </summary>
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

    /// <summary>剪空一条轨之后轨还在，和 <c>DeleteNotes</c> 一个道理：空声部不是没有声部。</summary>
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

    /// <summary>
    /// 负的起点夹到 0，不抛：这两个 tick 是**从用户填的小节号算出来的**，
    /// 不是调用方写死的参数 —— 算出来的坐标一律夹住，写死的参数才抛（和挪音符的规矩一致）。
    /// </summary>
    [Test]
    public void 负的起点夹到零()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(64, 1920, 240, 100)));

        Assert.That(
            Starts(_editor.CutRange(song, 0, -500, 1920), 0),
            Is.EqualTo(Starts(_editor.CutRange(song, 0, 0, 1920), 0)));
    }

    /// <summary>
    /// 终点在起点之前是**调用方的错**，直接抛。
    ///
    /// 界面上两个框填反了由界面换过来（那是要照顾的输入），换过来还反着，
    /// 就说明算小节边界那段代码坏了 —— 那时候悄悄换成「不改」或者「照字面剪」，
    /// 用户看到的是「点了没反应」或者「剪错了地方」，而没有任何地方报错。
    /// </summary>
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

    /// <summary>名字两端的空白会被去掉 —— 从别处粘过来的名字常带一个尾空格。</summary>
    [Test]
    public void 名字两端的空白被去掉()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        var edited = _editor.RenameTrack(song, 0, "  低音声部  ");

        Assert.That(edited.Tracks[0].Name, Is.EqualTo("低音声部"));
    }

    /// <summary>改成原来同一个名字就是没改：装饰器不该为它记一格撤销。</summary>
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
    /// 删中间一条：剩下的轨顺序对，而且 <see cref="Track.TrackIndex"/> **不重编号**。
    ///
    /// 那是「来自文件里第几个轨块」的出处标记 —— 同一个轨块切出来的两个声道共享它，
    /// 是这条轨的身份，不是它在列表里的排名。重编号等于把这条亲缘关系悄悄抹掉。
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

    /// <summary>只剩一条轨时照样删 —— 空曲子是合法状态（撤销拿得回来）。</summary>
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

    /// <summary>已经空了的曲子上再删轨仍然是越界抛，不是「没什么可删的，就算了」。</summary>
    [Test]
    public void 空曲子上删轨也抛()
    {
        var empty = _editor.DeleteTrack(SongOf(Map(), Melody(new Note(60, 0, 240, 100))), 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => _editor.DeleteTrack(empty, 0));
    }

    // ==================== 撤销装饰器 ====================

    /// <summary>
    /// 这几条新命令在装饰器上也各占一格撤销 —— 装饰器一行都不用为它们改：
    /// 撤销横切在所有命令外面，命令本身一行都不知道有它。
    ///
    /// 这条盯的是「转发的时候有没有顺手调 <c>Record</c>」：漏了的话命令照常生效，
    /// 只是撤不回来 —— 用户按 Ctrl+Z 会发现自己那一下白按了。
    /// </summary>
    [Test]
    public void 新命令在撤销装饰器上各占一格()
    {
        var editor = new UndoableSongEditor(new SongEditor());
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100), new Note(64, 480, 240, 100)));

        var moved = editor.MoveNotes(song, new[] { new NoteRef(0, 0) }, 240, 0);
        var spanned = editor.SetNoteSpan(moved, new NoteRef(0, 0), 240, 120);
        var deleted = editor.DeleteNotes(spanned, new[] { new NoteRef(0, 1) });
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

    /// <summary>没改的那几下不占格子（和 BPM / 移调同一条规矩）。</summary>
    [Test]
    public void 新命令没改时装饰器也不记账()
    {
        var editor = new UndoableSongEditor(new SongEditor());
        var song = SongOf(Map(), Melody(new Note(60, 0, 240, 100)));

        Assert.Multiple(() =>
        {
            Assert.That(editor.MoveNotes(song, new[] { new NoteRef(0, 0) }, 0, 0), Is.SameAs(song));
            Assert.That(editor.SetNoteSpan(song, new NoteRef(0, 0), 0, 240), Is.SameAs(song));
            Assert.That(editor.DeleteNotes(song, Array.Empty<NoteRef>()), Is.SameAs(song));
            Assert.That(editor.RenameTrack(song, 0, "主旋律"), Is.SameAs(song));
            Assert.That(editor.CanUndo, Is.False, "一下都没改，一格都不该占");
        });
    }

    // ==================== 帮手 ====================

    private static Song SongOf(TempoMap map, params Track[] tracks) => new(tracks, map);

    private static TempoMap Map() => new(TimeDivision.PulsesPerQuarter(480));

    private static Track Melody(params Note[] notes) => new(0, 0, "主旋律", 24, notes);

    private static Track Bass(params Note[] notes) => new(1, 1, "贝斯", 33, notes);

    private static Track Third(params Note[] notes) => new(2, 9, "鼓点", 0, notes);

    /// <summary>第 <paramref name="track"/> 条轨的前 <paramref name="count"/> 个音，按数组顺序。</summary>
    private static NoteRef[] All(int track, int count)
        => Enumerable.Range(0, count).Select(i => new NoteRef(track, i)).ToArray();

    private static long[] Starts(Song song, int track)
        => song.Tracks[track].Notes.Select(n => n.StartTick).ToArray();

    private static int[] Pitches(Song song, int track)
        => song.Tracks[track].Notes.Select(n => n.Pitch).ToArray();

    /// <summary>相邻音起点之间的间距 —— 「整组挪的是同一个量」看得见的那一面。</summary>
    private static long[] Gaps(Song song, int track)
    {
        var starts = Starts(song, track);
        return starts.Zip(starts.Skip(1), (a, b) => b - a).ToArray();
    }
}

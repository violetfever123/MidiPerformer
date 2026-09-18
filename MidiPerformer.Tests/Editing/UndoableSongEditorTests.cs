using MidiPerformer.Core.Model;
using MidiPerformer.Core.Ports.Inbound;
using MidiPerformer.Core.UseCases.Editing;
using NUnit.Framework;

namespace MidiPerformer.Tests.Editing;

/// <summary>
/// 撤销装饰器的**外部行为**（S2 缝）。
///
/// 撤销栈本身是私有的，这里一个字段都不碰：只从外面看「撤了几步、撤到哪一份谱」。
/// 判据用的是**逐字段比较**而不是引用 —— 引用相等是实现细节，而「撤销之后谱面回到改之前」
/// 是给用户看的事实。两者这次恰好都成立，但红的理由该是可读的那一个。
///
/// 每条命令都把 <c>Transpose</c> 设成一个**认得出第几步**的数，
/// 于是「撤到底停在第几步」这种断言写得出来（栈封顶那条全靠它）。
/// </summary>
public class UndoableSongEditorTests
{
    // ==================== 撤销 / 重做 ====================

    [Test]
    public void 撤销回到改前重做回到撤销前()
    {
        var editor = NewEditor();
        var initial = SongAt(0);
        var once = editor.SetTranspose(initial, 0, 5);

        Assert.Multiple(() =>
        {
            Assert.That(editor.CanUndo, Is.True, "改过了，撤得动");
            Assert.That(editor.CanRedo, Is.False, "还没撤，没得重做");
        });

        var back = editor.Undo();

        AssertSameSong("撤销之后", initial, back);
        Assert.Multiple(() =>
        {
            Assert.That(editor.CanUndo, Is.False, "撤到改之前了");
            Assert.That(editor.CanRedo, Is.True);
        });

        var forward = editor.Redo();

        AssertSameSong("重做之后", once, forward);
        Assert.Multiple(() =>
        {
            Assert.That(forward, Is.SameAs(once), "重做拿到的是当初那一个，不是又算了一遍");
            Assert.That(editor.CanUndo, Is.True);
            Assert.That(editor.CanRedo, Is.False);
        });
    }

    [Test]
    public void 撤销重做只在两条链上前后走不改谱面()
    {
        var editor = NewEditor();
        var initial = SongAt(0);
        var s1 = editor.SetTranspose(initial, 0, 1);
        var s2 = editor.SetBpm(s1, 90);

        // 撤两步再重做两步，来回两遍 —— 只看落点，「走过几趟」不该影响结果
        for (int round = 0; round < 2; round++)
        {
            AssertSameSong($"第 {round + 1} 轮撤到 s1", s1, editor.Undo());
            AssertSameSong($"第 {round + 1} 轮撤到初始", initial, editor.Undo());
            AssertSameSong($"第 {round + 1} 轮重做到 s1", s1, editor.Redo());
            AssertSameSong($"第 {round + 1} 轮重做到 s2", s2, editor.Redo());
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(10)]
    public void 连撤N步回到初始状态(int steps)
    {
        var editor = NewEditor();
        var initial = SongAt(0);
        var song = initial;
        for (int i = 1; i <= steps; i++) song = editor.SetTranspose(song, 0, i);

        Song? state = song;
        for (int i = 0; i < steps; i++) state = editor.Undo();

        AssertSameSong($"{steps} 步撤到底", initial, state);
        Assert.Multiple(() =>
        {
            Assert.That(editor.CanUndo, Is.False, "撤到底了");
            Assert.That(editor.CanRedo, Is.True);
            Assert.That(editor.Undo(), Is.Null, "再撤就是空的");
        });

        // 撤到底之后一路重做，回到新改的那一份
        state = editor.Redo();
        for (int i = 1; i < steps; i++) state = editor.Redo();
        AssertSameSong($"{steps} 步重做到底", song, state);
    }

    // ==================== 空栈 ====================

    /// <summary>栈空时 Undo / Redo 是**什么也不做**，不是抛异常：界面上那两颗按钮虽然会置灰，
    /// 键盘快捷方式（Ctrl+Z）却随时可能被按到，那儿没有能拦的按钮。</summary>
    [Test]
    public void 栈空时撤销重做什么都不做()
    {
        var editor = NewEditor();

        Assert.Multiple(() =>
        {
            Assert.That(editor.Undo(), Is.Null);
            Assert.That(editor.Redo(), Is.Null);
            Assert.That(editor.CanUndo, Is.False);
            Assert.That(editor.CanRedo, Is.False);
        });

        // 撤销栈空了但重做栈有货：这时再撤还是空的，而且**不能**把重做栈搅乱
        var once = editor.SetTranspose(SongAt(0), 0, 3);
        editor.Undo();

        Assert.Multiple(() =>
        {
            Assert.That(editor.Undo(), Is.Null);
            Assert.That(editor.Redo(), Is.SameAs(once), "空撤那一下不该动重做栈");
        });
    }

    // ==================== 新命令清掉重做链 ====================

    [Test]
    public void 撤销之后又改新的重做链就没了()
    {
        var editor = NewEditor();
        editor.SetTranspose(SongAt(0), 0, 3);
        var back = editor.Undo()!;
        Assert.That(editor.CanRedo, Is.True);

        // 撤回去之后又改了别的：原来那条重做链已经不在同一条时间线上了
        var s2 = editor.SetTranspose(back, 0, 7);

        Assert.Multiple(() =>
        {
            Assert.That(editor.CanRedo, Is.False);
            Assert.That(editor.Redo(), Is.Null);
            Assert.That(editor.CanUndo, Is.True);
        });

        AssertSameSong("撤一步回到「撤销后那一份」", back, editor.Undo());
        Assert.Multiple(() =>
        {
            // 撤销栈上只剩「撤销后那一份 → 新改的那一份」这一格：原来那条链整条作废了，
            // 不会撤着撤着又撤到刚才那条被抛弃的时间线上去
            Assert.That(editor.CanUndo, Is.False);
            Assert.That(editor.Undo(), Is.Null);
        });
    }

    // ==================== 栈封顶 ====================

    /// <summary>
    /// 撤销栈封顶 <see cref="UndoableSongEditor.MaxUndoSteps"/> 步，最老的被丢掉。
    ///
    /// 不封顶的话，一晚上连着调速度就能攒出几百份整曲的引用 —— 那是一整个曲子的内存，
    /// 而用户永远不会撤到 500 步之前。
    /// </summary>
    [Test]
    public void 撤销栈封顶一百步最老的先丢()
    {
        var editor = NewEditor();
        var song = SongAt(0);
        for (int i = 1; i <= UndoableSongEditor.MaxUndoSteps + 50; i++) song = editor.SetTranspose(song, 0, i);

        Song? state = null;
        int steps = 0;
        while (editor.Undo() is { } undone) { state = undone; steps++; }

        Assert.Multiple(() =>
        {
            Assert.That(steps, Is.EqualTo(UndoableSongEditor.MaxUndoSteps), "最多只撤得动这么多步");
            Assert.That(state!.Tracks[0].Transpose, Is.EqualTo(50),
                "撤到底停在「被丢掉的那 50 步之后」——第 50 步那个状态");
            Assert.That(editor.CanUndo, Is.False);
        });

        // 丢的是撤的深度，不是重做：重做链上仍然是完整的 100 步
        int forward = 0;
        while (editor.Redo() is not null) forward++;
        Assert.That(forward, Is.EqualTo(UndoableSongEditor.MaxUndoSteps));
    }

    [Test]
    public void 栈封顶的边界上不多不少()
    {
        var editor = NewEditor();
        var song = SongAt(0);
        for (int i = 1; i <= UndoableSongEditor.MaxUndoSteps; i++) song = editor.SetTranspose(song, 0, i);

        int steps = 0;
        Song? state = null;
        while (editor.Undo() is { } undone) { state = undone; steps++; }

        Assert.Multiple(() =>
        {
            Assert.That(steps, Is.EqualTo(UndoableSongEditor.MaxUndoSteps), "正好一百步，一步都不该丢");
            Assert.That(state!.Tracks[0].Transpose, Is.EqualTo(0), "撤到底就是最初那一份");
        });
    }

    // ==================== 没改的命令不记账 ====================

    /// <summary>
    /// 按了等于没按的命令（速度填成原来那个数、移调按到 0）不该占一格撤销。
    ///
    /// 占了的话，用户连按十下 Ctrl+Z 会有好几下「什么也没发生」——
    /// 看上去像撤销坏了，而不是像他确实改过那么多次。
    /// </summary>
    [Test]
    public void 没改的命令不记账()
    {
        var editor = NewEditor();
        var song = SongAt(3);

        var same = editor.SetTranspose(song, 0, 3);

        Assert.Multiple(() =>
        {
            Assert.That(same, Is.SameAs(song), "没改就还回来同一个引用");
            Assert.That(editor.CanUndo, Is.False, "这一下不该占一格撤销");
            Assert.That(editor.Undo(), Is.Null);
        });

        // 速度同理：空速度表 = 全曲 120，填 120 等于没改
        var byBpm = editor.SetBpm(song, 120);

        Assert.Multiple(() =>
        {
            Assert.That(byBpm, Is.SameAs(song));
            Assert.That(editor.CanUndo, Is.False);
        });
    }

    [Test]
    public void 改了一次之后没改的那几下不占栈()
    {
        var editor = NewEditor();
        var initial = SongAt(0);
        var edited = editor.SetTranspose(initial, 0, 4);
        editor.SetTranspose(edited, 0, 4);          // 等于没按
        editor.SetBpm(edited, 120);                  // 也等于没按

        Assert.That(editor.Undo(), Is.SameAs(initial), "一下就撤回到改之前");
        Assert.That(editor.CanUndo, Is.False);
    }

    // ==================== Reset ====================

    /// <summary>
    /// 换一首曲子必须 <c>Reset()</c>：撤销栈里装的是**上一首**的 <see cref="Song"/>，
    /// 不清的话，装了新曲子之后按 Ctrl+Z 会把上一首捞出来铺到界面上 —— 数据没错，
    /// 只是「撤着撤着换了一首歌」，谁遇上都要懵。
    /// </summary>
    [Test]
    public void Reset之后两个栈都空了()
    {
        var editor = NewEditor();
        editor.SetTranspose(SongAt(0), 0, 5);
        editor.Undo();
        Assert.That(editor.CanRedo, Is.True, "先确认真有东西可清");

        editor.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(editor.CanUndo, Is.False);
            Assert.That(editor.CanRedo, Is.False);
            Assert.That(editor.Undo(), Is.Null);
            Assert.That(editor.Redo(), Is.Null);
        });
    }

    [Test]
    public void 换曲子时Reset之后撤销不会撤到上一首()
    {
        var editor = NewEditor();
        var first = SongAt(0);
        editor.SetTranspose(first, 0, 5);

        editor.Reset();                              // 装了新曲子

        var second = SongAt(0, "另一首");
        var edited = editor.SetTranspose(second, 0, 1);

        AssertSameSong("撤销落在新曲子上", second, editor.Undo());
        Assert.That(editor.CanUndo, Is.False, "没有上一首的账可撤");
        Assert.That(edited, Is.Not.SameAs(second));
    }

    [Test]
    public void Reset之后还能接着用()
    {
        var editor = NewEditor();
        editor.SetTranspose(SongAt(0), 0, 5);
        editor.Reset();

        var song = SongAt(0);
        editor.SetBpm(song, 90);

        Assert.Multiple(() =>
        {
            Assert.That(editor.CanUndo, Is.True, "Reset 只是清账，不是把装饰器废掉");
            Assert.That(editor.Undo(), Is.SameAs(song));
        });
    }

    // ==================== 转发 ====================

    /// <summary>
    /// 装饰器**原样转发**：参数一个不改、内层交出来的那一份原样返回。
    ///
    /// 这一条用的是一个手写的记录型假编辑器（NUnit 这边没有 mock 库，也不打算为一个装饰器引一个）。
    /// 之所以要这么一条：装饰器最坏的坏法是「忘了转发」或者「把参数改了一下再转发」——
    /// 前者结果永远不变（界面看上去像点了没反应），后者是悄悄改了用户填的数。
    /// 两者都不会抛异常，只有盯住「内层到底收到了什么」才看得见。
    /// </summary>
    [Test]
    public void 每条命令都原样转给内层并记上账()
    {
        var inner = new RecordingSongEditor();
        var editor = new UndoableSongEditor(inner);
        var song = SongAt(0);

        // 每调完一条就立刻看内层收到了什么：假编辑器只留最后一笔，看晚了就被下一条盖掉了
        var byBpm = editor.SetBpm(song, 96);

        Assert.Multiple(() =>
        {
            Assert.That(inner.LastSong, Is.SameAs(song), "内层收到的就是调用方交的那一份");
            Assert.That(inner.LastBpm, Is.EqualTo(96), "速度原样转过去");
            Assert.That(byBpm, Is.SameAs(inner.LastResult), "返回的是内层产出的那一份");
        });

        var byTranspose = editor.SetTranspose(byBpm, 2, -12);

        Assert.Multiple(() =>
        {
            Assert.That(inner.LastSong, Is.SameAs(byBpm), "第二条命令把上一条的产出原样交了过去");
            Assert.That(inner.LastTrack, Is.EqualTo(2), "轨下标原样转过去");
            Assert.That(inner.LastSemitones, Is.EqualTo(-12), "半音数原样转过去");
            Assert.That(byTranspose, Is.SameAs(inner.LastResult));
        });

        // 内层每次交出来一份新的，装饰器就该记两笔账，能一路撤回最初那一份
        Assert.Multiple(() =>
        {
            Assert.That(editor.Undo(), Is.SameAs(byBpm));
            Assert.That(editor.Undo(), Is.SameAs(song));
            Assert.That(editor.CanUndo, Is.False);
        });
    }

    // ==================== 帮手 ====================

    private static UndoableSongEditor NewEditor() => new(new SongEditor());

    /// <summary>一份认得出「是第几步」的曲子：<c>Transpose</c> 就是它的编号。</summary>
    private static Song SongAt(int transpose, string name = "主旋律") =>
        new(
            new[]
            {
                new Track(0, 0, name, 24, new[] { new Note(60, 0, 480, 100) }, transpose),
            },
            new TempoMap(TimeDivision.PulsesPerQuarter(480)));

    /// <summary>
    /// 逐字段比两份谱子。为什么不用引用相等：引用相等是**实现**的判据（装饰器拿它判断改没改），
    /// 而这里要断的是**给用户看的事实** —— 撤销之后谱面确实回到了改之前。
    /// </summary>
    private static void AssertSameSong(string what, Song expected, Song? actual)
    {
        Assert.That(actual, Is.Not.Null, $"{what}：不该是空");
        Assert.Multiple(() =>
        {
            Assert.That(actual!.Tracks, Has.Count.EqualTo(expected.Tracks.Count), $"{what}：轨数");
            for (int i = 0; i < expected.Tracks.Count; i++)
                Assert.That(actual.Tracks[i], Is.EqualTo(expected.Tracks[i]), $"{what}：第 {i} 条轨（逐字段，含音符）");
            Assert.That(actual.TempoMap.TempoChanges, Is.EqualTo(expected.TempoMap.TempoChanges), $"{what}：速度表");
            Assert.That(actual.TempoMap.Division, Is.EqualTo(expected.TempoMap.Division), $"{what}：分辨率");
        });
    }

    /// <summary>
    /// 记录型的假编辑器：把收到的参数记下来，返回一份**新的** <see cref="Song"/>
    /// （= 「这条命令改过了」）。假得足够小，读它一眼就知道它想证明什么。
    /// </summary>
    private sealed class RecordingSongEditor : ISongEditor
    {
        public Song? LastSong { get; private set; }
        public Song? LastResult { get; private set; }
        public double LastBpm { get; private set; }
        public int LastTrack { get; private set; }
        public int LastSemitones { get; private set; }

        public Song SetBpm(Song song, double beatsPerMinute)
        {
            LastSong = song;
            LastBpm = beatsPerMinute;
            return LastResult = new Song(song.Tracks, song.TempoMap);
        }

        public Song SetTranspose(Song song, int trackIndex, int semitones)
        {
            LastSong = song;
            LastTrack = trackIndex;
            LastSemitones = semitones;
            return LastResult = new Song(song.Tracks, song.TempoMap);
        }
    }
}

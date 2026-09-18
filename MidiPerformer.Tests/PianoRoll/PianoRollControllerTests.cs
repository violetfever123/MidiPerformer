using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Controllers;
using MidiPerformer.Core.Model;
using NUnit.Framework;

namespace MidiPerformer.Tests.PianoRoll;

/// <summary>
/// 卷帘的脑子：视图位置、选中、音域自适应、标灰判据。
///
/// 这些用例跑得起来，是因为 <see cref="PianoRollController"/> **不认识 Avalonia**：
/// 视口是按控件宽高现算的一份纯数据，所以「某个尺寸下点某个像素」不用起窗口就能写。
///
/// 用例里凡是「视图滚到某处」的断言，都拿 16 小节的曲子 —— 8 小节的曲子上「滚到第 6 小节」
/// 会被合法的上界夹住，测出来的就不是「跳转对不对」，而是「夹取对不对」了（夹取另有专门用例）。
/// </summary>
public class PianoRollControllerTests
{
    private const long Bar = 1920;

    private static Song SongOf(params Track[] tracks)
        => new(tracks, new TempoMap(TimeDivision.PulsesPerQuarter(480)));

    private static Track Melody(params Note[] notes) => new(0, 0, "主旋律", 24, notes);

    private static Track Bass(int transpose, params Note[] notes)
        => new(1, 1, "贝斯", 33, notes, transpose);

    /// <summary>每小节一个音的曲子，够滚、够跳、够算小节数。</summary>
    private static Song BarsOf(int bars)
    {
        var notes = new List<Note>(bars);
        for (int bar = 0; bar < bars; bar++) notes.Add(new Note(60 + bar % 5, Bar * bar, 480, 100));
        return SongOf(Melody(notes.ToArray()));
    }

    // ==================== 小节与视图位置 ====================

    [Test]
    public void 整曲小节数按最后一个音向上取整()
    {
        var song = SongOf(Melody(new Note(60, 0, 480, 100), new Note(60, Bar * 3 + 100, 480, 100)));

        Assert.That(new PianoRollController(song).BarCount, Is.EqualTo(4));
    }

    [Test]
    public void 空曲也有一小节()
    {
        var controller = new PianoRollController(SongOf(Melody()));

        Assert.Multiple(() =>
        {
            Assert.That(controller.BarCount, Is.EqualTo(1));
            Assert.That(controller.BarNoteCounts, Has.Count.EqualTo(1));
            Assert.That(controller.MaxBarNoteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void 视图不会滚过最后四小节()
    {
        var controller = new PianoRollController(BarsOf(8));

        controller.SetViewStart(Bar * 999);

        Assert.Multiple(() =>
        {
            Assert.That(controller.ViewStartTick, Is.EqualTo(Bar * 4),
                "上界是「最后 4 小节正好铺满一屏」，再多拖只会让谱面缩在左边");
            Assert.That(controller.ViewStartBar, Is.EqualTo(8 - PianoRollGeometry.BarsVisible));
        });
    }

    [Test]
    public void 视图不会滚到曲子前面()
    {
        var controller = new PianoRollController(BarsOf(8));

        controller.SetViewStart(-Bar * 5);

        Assert.That(controller.ViewStartTick, Is.EqualTo(0));
    }

    [Test]
    public void 跳到指定小节()
    {
        var controller = new PianoRollController(BarsOf(16));

        controller.SeekBar(6);

        Assert.That(controller.ViewStartBar, Is.EqualTo(5), "「跳到第 6 小节」= 视图左边缘正好是第 6 小节");
    }

    [Test]
    public void 越界的小节号夹到首尾()
    {
        var controller = new PianoRollController(BarsOf(16));

        controller.SeekBar(999);
        Assert.That(controller.ViewStartBar, Is.EqualTo(16 - PianoRollGeometry.BarsVisible),
            "输 999 的意思是「去最后」，不是「报个错」");

        controller.SeekBar(-3);
        Assert.That(controller.ViewStartTick, Is.EqualTo(0));
    }

    [Test]
    public void 凑够四小节才允许往后滚()
    {
        // 只有 5 小节的曲子：视图最远只能到第 2 小节，因为一屏就要 4 小节
        var controller = new PianoRollController(SongOf(Melody(new Note(60, 0, Bar * 5, 100))));

        controller.SetViewStart(Bar * 99);

        Assert.That(controller.ViewStartTick, Is.EqualTo(Bar), "5 - 4 = 1，从第 2 小节起");
    }

    [Test]
    public void 把某一小节摆到屏幕中间()
    {
        var controller = new PianoRollController(BarsOf(16));

        controller.CenterOnBar(5);

        Assert.That(controller.ViewStartTick, Is.EqualTo(Bar * 3), "第 6 小节居中 = 左边缘退两小节");
    }

    [Test]
    public void 某小节的落点一定落在小节线上()
    {
        var controller = new PianoRollController(BarsOf(16));

        Assert.Multiple(() =>
        {
            Assert.That(controller.TickOfBarClamped(3), Is.EqualTo(Bar * 3));
            Assert.That(controller.TickOfBarClamped(999), Is.EqualTo(Bar * 15), "越界夹到最后一小节");
            Assert.That(controller.TickOfBarClamped(-1), Is.EqualTo(0));
        });
    }

    [Test]
    public void 跟播放头滚只往前走()
    {
        var controller = new PianoRollController(BarsOf(16));
        controller.SetViewStart(Bar * 4);

        // 播放头退到三分之一线左边：视图不动，否则画面会来回蹭
        controller.Follow(Bar * 4, 0.32);
        Assert.That(controller.ViewStartTick, Is.EqualTo(Bar * 4));

        // 播放头跑到前面去：视图跟上，把它留在三分之一处
        controller.Follow(Bar * 12, 0.32);
        var viewport = controller.ViewportOf(0, 800, 200);
        // 视图起点取整到整数 tick，一个 tick 才 0.1px —— 半个像素的容差足够说明「就在三分之一处」
        Assert.That(PianoRollGeometry.XAtTick(viewport, Bar * 12), Is.EqualTo(800 * 0.32).Within(0.5));
    }

    [Test]
    public void 停止时把视图对齐到小节线()
    {
        var controller = new PianoRollController(BarsOf(16));
        controller.SetViewStart(Bar * 2 + 500);

        controller.SnapViewToBar();

        Assert.That(controller.ViewStartTick, Is.EqualTo(Bar * 2), "按下停止就是要改东西，不该让你面对半截小节");
    }

    // ==================== 键盘定位 ====================

    [Test]
    public void 方向键按时间在所有轨的音符之间走()
    {
        // 第 2 条轨那个音比第 1 条轨的早：顺序必须是「按时间」，不是「按轨」
        var controller = new PianoRollController(SongOf(
            Melody(new Note(60, Bar, 480, 100)),
            Bass(0, new Note(40, 0, 480, 100))));

        Assert.Multiple(() =>
        {
            Assert.That(controller.MoveSelection(1)?.Track, Is.EqualTo(1), "先到最早的那个音");
            Assert.That(controller.MoveSelection(1)?.Track, Is.EqualTo(0));
            Assert.That(controller.MoveSelection(1)?.Track, Is.EqualTo(0), "到头了停在最后一个音上，不绕回去");
            Assert.That(controller.MoveSelection(-1)?.Track, Is.EqualTo(1));
            Assert.That(controller.MoveSelection(-1)?.Track, Is.EqualTo(1), "往回走到头也一样停住");
        });
    }

    [Test]
    public void 选中的音会滚进视野()
    {
        var controller = new PianoRollController(BarsOf(16));

        for (int i = 0; i < 7; i++) controller.MoveSelection(1);

        Assert.Multiple(() =>
        {
            Assert.That(controller.Selection, Is.Not.Null);
            Assert.That(controller.ViewStartBar, Is.EqualTo(6), "视图左边缘对齐到它所在的小节");
        });
    }

    [Test]
    public void 一个音都没有时定位不崩()
    {
        var controller = new PianoRollController(SongOf(Melody()));

        Assert.Multiple(() =>
        {
            Assert.That(controller.MoveSelection(1), Is.Null);
            Assert.That(controller.MoveSelection(-1), Is.Null);
            Assert.That(controller.DescribeSelection(), Is.Null);
        });
    }

    [Test]
    public void 读音符的音高小节拍位时值()
    {
        var controller = new PianoRollController(SongOf(Melody(new Note(64, Bar + 960, 480, 100))));

        controller.MoveSelection(1);
        var info = controller.DescribeSelection();

        Assert.Multiple(() =>
        {
            Assert.That(info, Is.Not.Null);
            Assert.That(info!.Value.Pitch, Is.EqualTo(64));
            Assert.That(info.Value.Bar, Is.EqualTo(2), "第 2 小节（1 起）");
            Assert.That(info.Value.BeatInBar, Is.EqualTo(3.0).Within(1e-9), "小节内第 3 拍（1 起）");
            Assert.That(info.Value.LengthBeats, Is.EqualTo(1.0).Within(1e-9));
        });
    }

    [Test]
    public void 读的是移调之后的音高()
    {
        // 卷帘上看到的、耳朵听到的、读数条上写的，必须是同一个音高
        var controller = new PianoRollController(SongOf(Bass(-12, new Note(60, 0, 480, 100))));

        controller.MoveSelection(1);

        Assert.That(controller.DescribeSelection()!.Value.Pitch, Is.EqualTo(48));
    }

    [Test]
    public void 下标越界时读数条拿到的是空的()
    {
        var controller = new PianoRollController(SongOf(Melody(new Note(60, 0, 480, 100))));

        Assert.Multiple(() =>
        {
            Assert.That(controller.Describe(0, 99), Is.Null);
            Assert.That(controller.Describe(9, 0), Is.Null, "悬停时轨刚好被换掉就会碰上");
        });
    }

    // ==================== 音域自适应 ====================

    [Test]
    public void 音域按这条轨自己的音自适应()
    {
        var controller = new PianoRollController(SongOf(
            Melody(new Note(60, 0, 480, 100), new Note(64, 480, 480, 100)),
            Bass(0, new Note(40, 0, 480, 100), new Note(43, 480, 480, 100))));

        var melody = controller.PitchRangeOf(0);
        var bass = controller.PitchRangeOf(1);

        Assert.Multiple(() =>
        {
            Assert.That(melody.Low, Is.GreaterThan(bass.High),
                "两条轨各显示各的音域：贝斯轨不该白占旋律那三个八度的高度");
            Assert.That(melody.Low, Is.LessThanOrEqualTo(60));
            Assert.That(melody.High, Is.GreaterThanOrEqualTo(64));
        });
    }

    [Test]
    public void 音域算的是移调之后的音高()
    {
        // 移调只影响听到的音高，而卷帘显示的正是听到的那个
        var controller = new PianoRollController(SongOf(
            Melody(new Note(60, 0, 480, 100)),
            Bass(-12, new Note(60, 0, 480, 100))));

        Assert.That(controller.PitchRangeOf(1).High, Is.EqualTo(controller.PitchRangeOf(0).High - 12));
    }

    [Test]
    public void 空轨的音域也能算出来()
    {
        var controller = new PianoRollController(SongOf(Melody()));

        var (low, high) = controller.PitchRangeOf(0);

        Assert.Multiple(() =>
        {
            Assert.That(low, Is.GreaterThanOrEqualTo(0));
            Assert.That(high, Is.LessThanOrEqualTo(127));
            Assert.That(high, Is.GreaterThan(low), "一个音都没有也要给几行，不能是一条零高度的轨");
        });
    }

    [Test]
    public void 音高0也是合法音域不会被当成还没算()
    {
        // 移调 -60 把 C4 压到音高 0，最低显示音高正好落在 0 上。
        // 拿 (0, 0) 当「还没算过」的哨兵，这条轨的音域就会被当成没算过而判错
        var controller = new PianoRollController(SongOf(Bass(-60, new Note(60, 0, 480, 100))));

        var range = controller.PitchRangeOf(0);

        Assert.Multiple(() =>
        {
            Assert.That(range.Low, Is.EqualTo(0));
            Assert.That(range.High, Is.GreaterThan(0));
        });
    }

    // ==================== 标灰 ====================

    [Test]
    public void 灰显判据与可演奏范围一致()
    {
        // 跨五个八度：口琴只有「基准八度 ±1 加最高两个音」，必然有音弹不出来
        var controller = new PianoRollController(SongOf(Melody(
            new Note(36, 0, 480, 100), new Note(48, 480, 480, 100), new Note(60, 960, 480, 100),
            new Note(72, 1440, 480, 100), new Note(84, 1920, 480, 100))));

        var flags = controller.InRangeFlagsOf(0);

        Assert.Multiple(() =>
        {
            Assert.That(flags, Has.Count.EqualTo(5), "逐音对应，一个不能少 —— 少一个后面对不上号");
            Assert.That(flags.Any(f => !f), Is.True);
            Assert.That(flags.Any(f => f), Is.True);
        });
    }

    [Test]
    public void 一个八度之内全都能弹()
    {
        var controller = new PianoRollController(SongOf(Melody(
            new Note(60, 0, 480, 100), new Note(64, 480, 480, 100), new Note(67, 960, 480, 100))));

        Assert.That(controller.InRangeFlagsOf(0), Is.All.True);
    }

    [Test]
    public void 标灰的音仍然画在卷帘上()
    {
        // 「超出可演奏范围」和「音域自适应」是两件事：灰音还在这一屏里，只是画成灰的 ——
        // 判据来自 NoteMapper（游戏里弹不出来），不是「看不见」
        var controller = new PianoRollController(SongOf(Melody(
            new Note(36, 0, 480, 100), new Note(48, 480, 480, 100), new Note(60, 960, 480, 100),
            new Note(72, 1440, 480, 100), new Note(84, 1920, 480, 100))));

        var range = controller.PitchRangeOf(0);
        var flags = controller.InRangeFlagsOf(0);
        var grey = controller.Song.Tracks[0].Notes
            .Where((_, i) => !flags[i])
            .Select(n => n.Pitch)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(grey, Is.Not.Empty);
            Assert.That(grey.All(p => p >= range.Low && p <= range.High), Is.True,
                "灰掉的音仍然落在这一轨显示的音域里");
        });
    }

    [Test]
    public void 标灰标记算一次就够()
    {
        // 每帧每个音都要问一次，所以必须缓存
        var controller = new PianoRollController(SongOf(Melody(new Note(60, 0, 480, 100))));

        Assert.That(controller.InRangeFlagsOf(0), Is.SameAs(controller.InRangeFlagsOf(0)));
    }

    // ==================== 命中判定 ====================

    [Test]
    public void 在卷帘上点中一个音()
    {
        var controller = new PianoRollController(SongOf(Melody(new Note(60, 0, 480, 100))));
        var viewport = controller.ViewportOf(0, 800, 200);
        // 落点往音符块里再进 8px：贴着左边缘那一段算「头」，这里要的是「身体」
        double x = PianoRollGeometry.XAtTick(viewport, 240) + 8;
        double y = PianoRollGeometry.YAtPitch(viewport, 60) + viewport.RowHeight / 2;

        var hit = controller.HitTest(0, viewport, x, y, out int index);

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.EqualTo(PianoRollGeometry.RollHit.Body));
            Assert.That(index, Is.EqualTo(0), "下标要能回到模型里的那个音");
        });
    }

    [Test]
    public void 点空白处什么都没命中()
    {
        var controller = new PianoRollController(SongOf(Melody(new Note(60, 0, 480, 100))));
        var viewport = controller.ViewportOf(0, 800, 200);

        var hit = controller.HitTest(0, viewport, 700, 100, out int index);

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.EqualTo(PianoRollGeometry.RollHit.None));
            Assert.That(index, Is.EqualTo(-1));
        });
    }

    [Test]
    public void 命中判定用的是移调之后的音高()
    {
        var controller = new PianoRollController(SongOf(Bass(-12, new Note(60, 0, 480, 100))));
        var viewport = controller.ViewportOf(0, 800, 200);
        double x = PianoRollGeometry.XAtTick(viewport, 240) + 8;

        // 点的是屏幕上 48 那一行（移调之后的音高），不是模型里写的 60
        double y = PianoRollGeometry.YAtPitch(viewport, 48) + viewport.RowHeight / 2;

        Assert.Multiple(() =>
        {
            Assert.That(controller.HitTest(0, viewport, x, y, out int index),
                Is.EqualTo(PianoRollGeometry.RollHit.Body));
            Assert.That(index, Is.EqualTo(0));
        });
    }

    [Test]
    public void 屏幕外的音不参与命中()
    {
        var controller = new PianoRollController(BarsOf(8));
        var viewport = controller.ViewportOf(0, 800, 200);
        double y = PianoRollGeometry.YAtPitch(viewport, 60) + viewport.RowHeight / 2;

        // 横坐标 1010 已经出了卷帘的右边缘（宽 800）。第 5 小节那个音要是参与判定，
        // 它的块正好落在这儿 —— 屏幕外的东西点了不该有反应
        Assert.That(controller.HitTest(0, viewport, 1010, y, out int index),
            Is.EqualTo(PianoRollGeometry.RollHit.None));
        Assert.That(index, Is.EqualTo(-1));
    }

    // ==================== 导航条的柱子 ====================

    [Test]
    public void 每小节的音符数多轨合计()
    {
        var controller = new PianoRollController(SongOf(
            Melody(new Note(60, 0, 480, 100), new Note(62, Bar, 480, 100), new Note(64, Bar, 480, 100)),
            Bass(0, new Note(40, 0, 480, 100))));

        Assert.Multiple(() =>
        {
            Assert.That(controller.BarNoteCounts[0], Is.EqualTo(2), "第 1 小节：旋律一个 + 贝斯一个");
            Assert.That(controller.BarNoteCounts[1], Is.EqualTo(2));
            Assert.That(controller.BarNoteCounts.Sum(), Is.EqualTo(4), "一个音都不该漏");
            Assert.That(controller.MaxBarNoteCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void 小节按拍号切而不是按固定格数()
    {
        // 3/4 拍：一小节 1440 tick。按 4/4 去切的话，音会落到错的小节里
        var song = new Song(
            new[] { Melody(new Note(60, 1440, 480, 100), new Note(62, 1440 * 2, 480, 100)) },
            new TempoMap(TimeDivision.PulsesPerQuarter(480), null,
                new[] { new TimeSignatureChange(0, 3, 4) }));
        var controller = new PianoRollController(song);

        Assert.Multiple(() =>
        {
            Assert.That(controller.TicksPerBar, Is.EqualTo(1440));
            Assert.That(controller.BarNoteCounts[1], Is.EqualTo(1), "第 2 小节有一个音");
            Assert.That(controller.BarNoteCounts[2], Is.EqualTo(1), "第 3 小节有一个音");
        });
    }

    // ==================== 视口 ====================

    [Test]
    public void 视口按控件的宽高现算()
    {
        var controller = new PianoRollController(BarsOf(16));
        controller.SetViewStart(Bar);

        var viewport = controller.ViewportOf(0, 800, 200);

        Assert.Multiple(() =>
        {
            Assert.That(viewport.Width, Is.EqualTo(800));
            Assert.That(viewport.Height, Is.EqualTo(200));
            Assert.That(viewport.ViewStartTick, Is.EqualTo(Bar));
            Assert.That(viewport.TicksPerBar, Is.EqualTo(Bar));
            Assert.That(viewport.TicksVisible, Is.EqualTo(controller.TicksVisible));
            Assert.That(viewport.LowPitch, Is.EqualTo(controller.PitchRangeOf(0).Low));
            Assert.That(viewport.HighPitch, Is.EqualTo(controller.PitchRangeOf(0).High));
        });
    }
}

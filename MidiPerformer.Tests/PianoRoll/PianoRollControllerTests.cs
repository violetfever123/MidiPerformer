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
    public void 方向键只在焦点轨里走()
    {
        // 第 2 条轨那个音比第 1 条轨的早。从前那版是「在所有轨的音符之间按时间跳」，
        // 于是第一步就会跳到第 2 条轨上 —— 按着按着莫名其妙换轨，正是 18 要去掉的东西
        var controller = new PianoRollController(SongOf(
            Melody(new Note(60, Bar, 480, 100)),
            Bass(0, new Note(40, 0, 480, 100))));

        Assert.Multiple(() =>
        {
            Assert.That(controller.MoveSelection(1)?.Track, Is.EqualTo(0), "焦点在第 1 条，就只走第 1 条");
            Assert.That(controller.FocusedTrack, Is.EqualTo(0), "定位不改焦点");
            Assert.That(controller.MoveSelection(1)?.Bar, Is.EqualTo(2));
            Assert.That(controller.MoveSelection(1)?.Bar, Is.EqualTo(2), "这条轨只有那一个音，到头了停住不绕回去");

            controller.SetFocusedTrack(1);
            Assert.That(controller.MoveSelection(-1)?.Track, Is.EqualTo(1), "换焦点之后走的是新的那条轨");
            Assert.That(controller.MoveSelection(-1)?.Bar, Is.EqualTo(1));
        });
    }

    [Test]
    public void 同刻的音从上往下走()
    {
        // 同一个起点上三个音。卷帘上高音画在上头，所以「下一个」是往下走（音高降序）——
        // 数组故意按音高升序给，好让「照着数组顺序走」那版和这一版分得开
        var controller = new PianoRollController(SongOf(
            Melody(new Note(60, 0, 480, 100), new Note(64, 0, 480, 100), new Note(67, 0, 480, 100))));

        Assert.Multiple(() =>
        {
            Assert.That(controller.MoveSelection(1)?.Pitch, Is.EqualTo(67), "最上面那个先来");
            Assert.That(controller.MoveSelection(1)?.Pitch, Is.EqualTo(64));
            Assert.That(controller.MoveSelection(1)?.Pitch, Is.EqualTo(60));
            Assert.That(controller.MoveSelection(1)?.Pitch, Is.EqualTo(60), "到底了停住");

            Assert.That(controller.MoveSelection(-1)?.Pitch, Is.EqualTo(64), "往回是往上");
        });
    }

    [Test]
    public void 焦点轨和选中集分家时从这条轨的开头重新起算()
    {
        // Ctrl+↑/↓ 换焦点不动选中集，于是会出现「焦点在轨 1、选中的音在轨 2」。
        // 这时候两个方向都该落在**轨 1 的第一个音**上 —— 那正是「回到我正在弄的这条轨」的意思
        var controller = new PianoRollController(SongOf(
            Melody(new Note(60, Bar, 480, 100), new Note(62, Bar * 2, 480, 100)),
            Bass(0, new Note(40, Bar * 5, 480, 100))));

        controller.SelectOnly(new NoteRef(1, 0));
        controller.SetFocusedTrack(0);

        Assert.Multiple(() =>
        {
            Assert.That(controller.MoveSelection(-1)?.Pitch, Is.EqualTo(60), "Ctrl+← 回到这条轨的开头");
            Assert.That(controller.Selection!.Value.Track, Is.EqualTo(0), "选中集落到焦点轨上");
        });

        controller.SelectOnly(new NoteRef(1, 0));
        controller.SetFocusedTrack(0);
        Assert.That(controller.MoveSelection(1)?.Pitch, Is.EqualTo(60), "Ctrl+→ 也从头起算，不是找最近的");
    }

    [Test]
    public void 焦点轨一个音都没有时定位不崩()
    {
        // 空轨也是能聚焦的（展开、改名、删除都长在轨道头上）。
        // 这时候按 Ctrl+←/→ 什么都不该发生 —— 尤其不该跳到别的轨上去
        var controller = new PianoRollController(SongOf(
            Melody(),
            Bass(0, new Note(40, Bar * 3, 480, 100))));

        Assert.Multiple(() =>
        {
            Assert.That(controller.MoveSelection(1), Is.Null);
            Assert.That(controller.MoveSelection(-1), Is.Null);
            Assert.That(controller.Selection, Is.Null);
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

    // ==================== 网格 ====================

    [Test]
    public void 网格是十六分音符且构造时就算好()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new PianoRollController(BarsOf(4)).GridTicks, Is.EqualTo(120), "480 PPQ 的四分之一拍");

            var song = new Song(
                new[] { Melody(new Note(60, 0, 480, 100)) },
                new TempoMap(TimeDivision.PulsesPerQuarter(960)));
            Assert.That(new PianoRollController(song).GridTicks, Is.EqualTo(240), "分辨率变了网格跟着变");
        });
    }

    // ==================== 多选 ====================

    [Test]
    public void 点一个音就只选中它一个()
    {
        var controller = new PianoRollController(ThreeNotes());

        controller.SelectOnly(new NoteRef(0, 1));

        Assert.Multiple(() =>
        {
            Assert.That(controller.SelectedNotes, Is.EqualTo(new[] { new NoteRef(0, 1) }));
            Assert.That(controller.Selection?.Track, Is.EqualTo(0));
            Assert.That(controller.Selection?.Note, Is.EqualTo(1));
            Assert.That(controller.IsSelected(new NoteRef(0, 1)), Is.True);
            Assert.That(controller.IsSelected(new NoteRef(0, 0)), Is.False);
        });
    }

    [Test]
    public void 选一个会顶掉之前的一整组()
    {
        var controller = new PianoRollController(ThreeNotes());
        controller.SetSelection(new[] { new NoteRef(0, 0), new NoteRef(0, 1) });

        controller.SelectOnly(new NoteRef(0, 2));

        Assert.That(controller.SelectedNotes, Is.EqualTo(new[] { new NoteRef(0, 2) }), "「只选它」就是只剩它");
    }

    [Test]
    public void 框选一组音符()
    {
        var controller = new PianoRollController(ThreeNotes());
        var box = new[] { new NoteRef(0, 0), new NoteRef(0, 1), new NoteRef(0, 2) };

        controller.SetSelection(box);

        Assert.Multiple(() =>
        {
            Assert.That(controller.SelectedNotes, Is.EqualTo(box), "顺序原样保留，一条轨里就是时间顺序");
            Assert.That(controller.Selection?.Note, Is.EqualTo(2), "主选中是最后加进去的那个");
            Assert.That(controller.IsSelected(new NoteRef(0, 1)), Is.True, "一组里的每一个都在选中集里");
        });
    }

    [Test]
    public void 主选中与选中集始终一致()
    {
        var controller = new PianoRollController(ThreeNotes());

        Assert.Multiple(() =>
        {
            Assert.That(controller.Selection, Is.Null);
            Assert.That(controller.SelectedNotes, Is.Empty);
        });

        controller.ExtendSelection(new NoteRef(0, 0));
        controller.ExtendSelection(new NoteRef(0, 2));
        var primary = controller.Selection!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(controller.SelectedNotes, Has.Count.EqualTo(2), "加一个就是往组里加，不是替换");
            Assert.That(primary.Note, Is.EqualTo(2), "最后加的那个是主选中");
            Assert.That(controller.SelectedNotes, Does.Contain(new NoteRef(primary.Track, primary.Note)),
                "主选中永远指在选中集里 —— 它不是另存的一份状态");
        });

        controller.ClearSelection();

        Assert.Multiple(() =>
        {
            Assert.That(controller.Selection, Is.Null);
            Assert.That(controller.SelectedNotes, Is.Empty);
        });
    }

    [Test]
    public void 加一个已经选中的音不会把主选中顶掉()
    {
        var controller = new PianoRollController(ThreeNotes());
        controller.SetSelection(new[] { new NoteRef(0, 0), new NoteRef(0, 1) });

        controller.ExtendSelection(new NoteRef(0, 0));

        Assert.Multiple(() =>
        {
            Assert.That(controller.SelectedNotes, Is.EqualTo(new[] { new NoteRef(0, 0), new NoteRef(0, 1) }),
                "Shift 点一个已经选中的音，意思是「留着它」，不是「再来一份」");
            Assert.That(controller.Selection?.Note, Is.EqualTo(1), "主选中也不该被它顶掉");
        });

        controller.ExtendSelection(new NoteRef(0, 9));

        Assert.That(controller.SelectedNotes, Has.Count.EqualTo(2), "加一个不存在的音等于没加");
    }

    [Test]
    public void 越界的音符被静默丢掉()
    {
        // 曲子刚被换掉、轨刚被删掉的时候会碰上 —— 那不是错误，不该抛（和 RestoreSelection 一条规矩）
        var controller = new PianoRollController(ThreeNotes());

        controller.SelectOnly(new NoteRef(0, 99));
        Assert.That(controller.SelectedNotes, Is.Empty, "点了个不存在的音 = 什么都没选中");

        controller.SelectOnly(new NoteRef(9, 0));
        Assert.That(controller.SelectedNotes, Is.Empty, "轨下标越界也一样");

        controller.SelectOnly(new NoteRef(0, -1));
        Assert.That(controller.SelectedNotes, Is.Empty, "负数下标也不是音");
    }

    [Test]
    public void 整体替换时越界的和重复的都丢掉()
    {
        var controller = new PianoRollController(ThreeNotes());

        controller.SetSelection(new[]
        {
            new NoteRef(0, 0), new NoteRef(0, 99), new NoteRef(-1, 0), new NoteRef(0, 0), new NoteRef(0, 1)
        });

        Assert.That(controller.SelectedNotes, Is.EqualTo(new[] { new NoteRef(0, 0), new NoteRef(0, 1) }),
            "越界的丢掉；重复的只留一个 —— 这一串要原样交给 MoveNotes，同一个音出现两次就会被挪两倍距离");
    }

    [Test]
    public void 放回选中就是只选它一个()
    {
        var controller = new PianoRollController(ThreeNotes());
        controller.SetSelection(new[] { new NoteRef(0, 0), new NoteRef(0, 1) });

        controller.RestoreSelection(0, 1);
        Assert.Multiple(() =>
        {
            Assert.That(controller.SelectedNotes, Is.EqualTo(new[] { new NoteRef(0, 1) }));
            Assert.That(controller.Selection?.Note, Is.EqualTo(1));
        });

        controller.RestoreSelection(0, 99);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Selection, Is.Null, "越界当没选中 —— 08 时就是这个行为，没变");
            Assert.That(controller.SelectedNotes, Is.Empty);
        });
    }

    // ==================== 聚焦轨 ====================

    /// <summary>三条轨，每条一个音，够上下走；音摆在靠后的小节上，视图也滚得动。</summary>
    private static Song ThreeTracks() => SongOf(
        Melody(new Note(60, Bar * 6, 480, 100)),
        Bass(0, new Note(48, Bar * 6, 480, 100)),
        new Track(2, 9, "鼓点", 0, new[] { new Note(36, Bar * 6, 480, 100) }));

    [Test]
    public void 初始聚焦在第一条()
    {
        Assert.That(new PianoRollController(BarsOf(4)).FocusedTrack, Is.EqualTo(0));
    }

    /// <summary>
    /// 聚焦挪没挪看得出来。
    ///
    /// 卷帘上「点音符 → 焦点跟随」那条路靠这个返回值决定要不要喊一声：
    /// 点在自己已经聚焦的那条轨上是常事（连着点几个音），每次都喊的话，
    /// 收到的那一头会把整窗重画一遍。
    /// </summary>
    [Test]
    public void 聚焦挪没挪看得出来()
    {
        var controller = new PianoRollController(ThreeTracks());

        Assert.Multiple(() =>
        {
            Assert.That(controller.SetFocusedTrack(0), Is.False, "本来就在这条轨上");
            Assert.That(controller.SetFocusedTrack(2), Is.True);
            Assert.That(controller.SetFocusedTrack(2), Is.False, "已经挪过来了");
            Assert.That(controller.SetFocusedTrack(99), Is.False, "越界夹回最后一条，夹完还是原地");
            Assert.That(controller.FocusedTrack, Is.EqualTo(2));
        });
    }

    [Test]
    public void 上下各走一条()
    {
        var controller = new PianoRollController(ThreeTracks());
        var none = new bool[3];

        Assert.Multiple(() =>
        {
            Assert.That(controller.MoveFocusedTrack(1, none), Is.EqualTo(1));
            Assert.That(controller.MoveFocusedTrack(1, none), Is.EqualTo(2));
            Assert.That(controller.MoveFocusedTrack(-1, none), Is.EqualTo(1));
            Assert.That(controller.FocusedTrack, Is.EqualTo(1), "属性跟得上");
        });
    }

    /// <summary>收起来的那几条不落 —— 卷帘是藏着的，高亮挪过去等于挪到一个看不见的地方。</summary>
    [Test]
    public void 跳过收起来的轨()
    {
        var controller = new PianoRollController(ThreeTracks());

        Assert.That(controller.MoveFocusedTrack(1, new[] { false, true, false }), Is.EqualTo(2),
            "中间那条收着，直接落到第 3 条");
    }

    /// <summary>到头就停住，不绕回另一头（和 MoveSelection 同一条规矩）。</summary>
    [Test]
    public void 走到头不绕回去()
    {
        var controller = new PianoRollController(ThreeTracks());
        var none = new bool[3];
        controller.SetFocusedTrack(2);

        Assert.That(controller.MoveFocusedTrack(1, none), Is.EqualTo(2), "已经在最后一条，再往下没有了");

        controller.SetFocusedTrack(0);

        Assert.That(controller.MoveFocusedTrack(-1, none), Is.EqualTo(0), "第一条再往上也没有");
    }

    [Test]
    public void 这个方向上只剩收起来的轨就原地不动()
    {
        var controller = new PianoRollController(ThreeTracks());
        controller.SetFocusedTrack(0);

        Assert.That(controller.MoveFocusedTrack(1, new[] { false, true, true }), Is.EqualTo(0));
    }

    /// <summary>
    /// 聚焦正好落在一条收起来的轨上时（用户先聚焦、再把它收起来）照样走得开：
    /// 走的是「从当前位置往这个方向找第一条没收起来的」，所以不需要先把聚焦挪开。
    /// </summary>
    [Test]
    public void 从一条收起来的轨上也能走开()
    {
        var controller = new PianoRollController(ThreeTracks());
        var collapsed = new[] { false, true, false };

        Assert.Multiple(() =>
        {
            controller.SetFocusedTrack(1);
            Assert.That(controller.MoveFocusedTrack(1, collapsed), Is.EqualTo(2));
            controller.SetFocusedTrack(1);
            Assert.That(controller.MoveFocusedTrack(-1, collapsed), Is.EqualTo(0));
        });
    }

    [Test]
    public void 设聚焦时越界夹住不抛()
    {
        var controller = new PianoRollController(ThreeTracks());

        controller.SetFocusedTrack(99);
        int high = controller.FocusedTrack;
        controller.SetFocusedTrack(-5);

        Assert.Multiple(() =>
        {
            Assert.That(high, Is.EqualTo(2), "往上越界夹到最后一条");
            Assert.That(controller.FocusedTrack, Is.EqualTo(0), "往下越界夹到第一条");
        });
    }

    /// <summary>一条轨都没有时落在 0：没轨可指，但读数得有个值（删光所有轨那一下会碰上）。</summary>
    [Test]
    public void 零轨时聚焦落在零()
    {
        var controller = new PianoRollController(NoTracks());
        controller.SetFocusedTrack(3);

        Assert.That(controller.FocusedTrack, Is.EqualTo(0));
    }

    /// <summary>折叠表比轨数短时，缺的那些当没收起来 —— 表是控件给的，短了不该炸。</summary>
    [Test]
    public void 折叠表比轨数短时缺的当没收起来()
    {
        var controller = new PianoRollController(ThreeTracks());

        Assert.That(controller.MoveFocusedTrack(1, new[] { false }), Is.EqualTo(1));
    }

    [Test]
    public void 第一条没收起来的轨()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PianoRollController.FirstExpanded(new[] { false, false }), Is.EqualTo(0));
            Assert.That(PianoRollController.FirstExpanded(new[] { true, false, false }), Is.EqualTo(1),
                "第一条收着，落到第二条");
            Assert.That(PianoRollController.FirstExpanded(new[] { true, true }), Is.EqualTo(0),
                "全收着也要有个落点，不然高亮一条都不落");
            Assert.That(PianoRollController.FirstExpanded(Array.Empty<bool>()), Is.EqualTo(0),
                "一条轨都没有");
        });
    }

    /// <summary>
    /// 换聚焦**只管「手搭在哪条轨上」**：选中集、视图、播放头一个都不动。
    ///
    /// 这条是刻意的（见 <see cref="PianoRollController.FocusedTrack"/>）：想改哪条和想听哪条
    /// 是两个意思，绑在一起的话，为了改一条轨就得先把别的静音。
    /// </summary>
    [Test]
    public void 换聚焦不动选中集也不动视图()
    {
        var controller = new PianoRollController(ThreeTracks());
        controller.SelectOnly(new NoteRef(0, 0));
        controller.SetViewStart(Bar * 2);
        long view = controller.ViewStartTick;

        controller.MoveFocusedTrack(1, new bool[3]);

        Assert.Multiple(() =>
        {
            Assert.That(controller.SelectedNotes.ToArray(), Is.EqualTo(new[] { new NoteRef(0, 0) }));
            Assert.That(controller.ViewStartTick, Is.EqualTo(view));
        });
    }

    // ==================== 区间查询 ====================

    /// <summary>
    /// 一条轨五个音，起点严格递增，专门把「贴边」的几种情形摆出来。
    /// 索引与占用的半开区间：
    /// 0:[0,1920) 1:[480,4320) 2:[1920,2400) 3:[2160,2400) 4:[2400,2880) 5:[2880,3360)
    /// </summary>
    private static Song RangeSong() => SongOf(Melody(
        new Note(60, 0, Bar, 100),
        new Note(62, 480, Bar * 2, 100),
        new Note(64, Bar, 480, 100),
        new Note(65, Bar + 240, 240, 100),
        new Note(67, Bar + 480, 480, 100),
        new Note(69, Bar + 960, 480, 100)));

    [Test]
    public void 与区间相交的音都算在区间里()
    {
        var controller = new PianoRollController(RangeSong());

        var hits = controller.NotesInRange(0, Bar, Bar + 480);

        Assert.Multiple(() =>
        {
            Assert.That(hits, Is.EqualTo(new[] { new NoteRef(0, 1), new NoteRef(0, 2), new NoteRef(0, 3) }),
                "按音符数组的顺序给出来（也就是时间顺序）");
            Assert.That(hits, Does.Contain(new NoteRef(0, 1)),
                "**从框左边伸进来的长音**：只判「起点落在区间里」的话它会原地留着，而用户框住它就是要删掉它");
            Assert.That(hits, Does.Contain(new NoteRef(0, 2)), "起点正好落在框的左边缘：算在内");
            Assert.That(hits, Does.Not.Contain(new NoteRef(0, 0)),
                "尾巴正好抵着框的左边缘：EndTick 不含，一丝都不重叠");
            Assert.That(hits, Does.Not.Contain(new NoteRef(0, 4)), "起点正好落在框的右边缘：右边缘是开的");
            Assert.That(hits, Does.Not.Contain(new NoteRef(0, 5)), "起在框后面");
        });
    }

    [Test]
    public void 区间查询只看给的那条轨()
    {
        var controller = new PianoRollController(SongOf(
            Melody(new Note(60, Bar, 480, 100)),
            Bass(0, new Note(40, Bar, 480, 100))));

        Assert.Multiple(() =>
        {
            Assert.That(controller.NotesInRange(0, Bar, Bar + 480), Is.EqualTo(new[] { new NoteRef(0, 0) }));
            Assert.That(controller.NotesInRange(1, Bar, Bar + 480), Is.EqualTo(new[] { new NoteRef(1, 0) }));
        });
    }

    [Test]
    public void 空区间和颠倒的区间都查不出东西()
    {
        var controller = new PianoRollController(RangeSong());

        Assert.Multiple(() =>
        {
            // 空白处横拖删音时，「点了空白但没拖」是一次零长度的手势。
            // 要是让它把光标底下那个音算进来，一次误点就删掉了一个音
            Assert.That(controller.NotesInRange(0, Bar + 240, Bar + 240), Is.Empty, "零长度的框");
            Assert.That(controller.NotesInRange(0, Bar + 480, Bar), Is.Empty, "endTick < startTick");
        });
    }

    [Test]
    public void 区间在曲子之外或者轨不存在时是空的()
    {
        var controller = new PianoRollController(RangeSong());

        Assert.Multiple(() =>
        {
            Assert.That(controller.NotesInRange(0, Bar * 10, Bar * 12), Is.Empty, "曲子末尾之后");
            Assert.That(controller.NotesInRange(9, 0, Bar * 4), Is.Empty, "没有这条轨");
            Assert.That(controller.NotesInRange(-1, 0, Bar * 4), Is.Empty);
        });
    }

    [Test]
    public void 框出来的音可以直接当选中集用()
    {
        var controller = new PianoRollController(RangeSong());

        controller.SetSelection(controller.NotesInRange(0, Bar, Bar + 480));
        var primary = controller.Selection!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(controller.SelectedNotes, Has.Count.EqualTo(3));
            Assert.That(primary.Track, Is.EqualTo(0));
            Assert.That(controller.SelectedNotes, Does.Contain(new NoteRef(primary.Track, primary.Note)));
        });
    }

    // ==================== 带 ref 的命中判定 ====================

    [Test]
    public void 命中判定同时给出音符的坐标()
    {
        var controller = new PianoRollController(SongOf(Melody(new Note(60, 0, 480, 100))));
        var viewport = controller.ViewportOf(0, 800, 200);
        double x = PianoRollGeometry.XAtTick(viewport, 240) + 8;
        double y = PianoRollGeometry.YAtPitch(viewport, 60) + viewport.RowHeight / 2;

        var hit = controller.HitTestRef(0, viewport, x, y, out var note);
        var indexHit = controller.HitTest(0, viewport, x, y, out int index);

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.EqualTo(PianoRollGeometry.RollHit.Body));
            Assert.That(hit, Is.EqualTo(indexHit), "同一个点，两个方法必须给同一个答案");
            Assert.That(note, Is.EqualTo(new NoteRef(0, index)), "ref 和下标得指着同一个音");
        });
    }

    [Test]
    public void 没命中时的ref是个明确不存在的坐标()
    {
        var controller = new PianoRollController(SongOf(Melody(new Note(60, 0, 480, 100))));
        var viewport = controller.ViewportOf(0, 800, 200);

        var hit = controller.HitTestRef(0, viewport, 700, 100, out var note);

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.EqualTo(PianoRollGeometry.RollHit.None));
            Assert.That(note, Is.EqualTo(new NoteRef(-1, -1)), "和 HitTest 的 -1 一个意思");
            Assert.That(note, Is.Not.EqualTo(default(NoteRef)),
                "default 正好是 (0, 0) —— 一个完全合法的音，谁忘了判就会默默选中第一个音");
            Assert.That(controller.IsSelected(note), Is.False);
        });
    }

    [Test]
    public void 音叠在一起时命中的是数组里靠前的那个()
    {
        // 同一条轨里两个同音高的音叠着（后一个在前一个结束前就起）。
        // 这条不是「对」，是**钉住现状**：带 ref 的那份必须和原版给同一个音，
        // 否则界面高亮的那个和命令动的那个会是两个音。
        // 要改成「取视觉上压在最上面的那个」，得两处一起改。
        var controller = new PianoRollController(SongOf(Melody(
            new Note(60, 0, 960, 100), new Note(60, 480, 960, 100))));
        var viewport = controller.ViewportOf(0, 800, 200);
        double x = PianoRollGeometry.XAtTick(viewport, 600);
        double y = PianoRollGeometry.YAtPitch(viewport, 60) + viewport.RowHeight / 2;

        var hit = controller.HitTest(0, viewport, x, y, out int index);
        var refHit = controller.HitTestRef(0, viewport, x, y, out var note);

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.EqualTo(PianoRollGeometry.RollHit.Body), "这个点两个音都盖得住");
            Assert.That(index, Is.EqualTo(0), "给的是数组里靠前的那个（也就是起点更早的）");
            Assert.That(refHit, Is.EqualTo(hit));
            Assert.That(note, Is.EqualTo(new NoteRef(0, 0)), "带 ref 的那份必须给同一个音");
        });
    }

    // ==================== 整组位移的夹法 ====================

    /// <summary>
    /// 这一组用例盯的是**界面和命令共用的那一份**夹法：拖动预览、方向键微调、
    /// <c>SongEditor.MoveNotes</c> 三处说的是同一件事，走样了就会「预览画到东、落点在西」。
    ///
    /// 尤其是**整组一起夹**而不是逐个夹 —— 逐个夹会把拖到边界的一组音压成一摞。
    /// </summary>
    [Test]
    public void 没顶到边界时位移原样放过()
    {
        var controller = new PianoRollController(
            SongOf(Melody(new Note(60, 1000, 480, 100), new Note(64, 1500, 480, 100))));

        var clamped = controller.ClampMoveDelta(
            new[] { new NoteRef(0, 0), new NoteRef(0, 1) }, -120, 3);

        Assert.Multiple(() =>
        {
            Assert.That(clamped.DeltaTicks, Is.EqualTo(-120));
            Assert.That(clamped.DeltaPitch, Is.EqualTo(3));
        });
    }

    [Test]
    public void 往左顶到头时整组按最小的那个音缩住()
    {
        // 靠前的那个音在 100，靠后的在 500 —— 想整组左移 480，只有 100 能让
        var controller = new PianoRollController(
            SongOf(Melody(new Note(60, 100, 480, 100), new Note(64, 500, 480, 100))));

        var clamped = controller.ClampMoveDelta(
            new[] { new NoteRef(0, 0), new NoteRef(0, 1) }, -480, 0);

        Assert.That(clamped.DeltaTicks, Is.EqualTo(-100), "整组一起挪的量由最靠前的那个决定");
    }

    [Test]
    public void 整组一起夹不会把两个音压成一摞()
    {
        // 逐个夹的话两个音都会落到 0，相对位置（差 400）就没了
        var controller = new PianoRollController(
            SongOf(Melody(new Note(60, 100, 480, 100), new Note(64, 500, 480, 100))));

        var clamped = controller.ClampMoveDelta(
            new[] { new NoteRef(0, 0), new NoteRef(0, 1) }, -99999, 0);

        var notes = controller.Song.Tracks[0].Notes;
        Assert.Multiple(() =>
        {
            Assert.That(clamped.DeltaTicks, Is.EqualTo(-100));
            Assert.That(notes[0].StartTick + clamped.DeltaTicks, Is.EqualTo(0));
            Assert.That(notes[1].StartTick + clamped.DeltaTicks, Is.EqualTo(400), "差还是 400");
        });
    }

    [Test]
    public void 音高两头都夹在零到一百二十七之间()
    {
        var controller = new PianoRollController(
            SongOf(Melody(new Note(2, 0, 480, 100), new Note(126, 480, 480, 100))));

        var low = controller.ClampMoveDelta(new[] { new NoteRef(0, 0) }, 0, -12);
        var high = controller.ClampMoveDelta(new[] { new NoteRef(0, 1) }, 0, 12);

        Assert.Multiple(() =>
        {
            Assert.That(low.DeltaPitch, Is.EqualTo(-2), "音高 2 再降 12 会出下界");
            Assert.That(high.DeltaPitch, Is.EqualTo(1), "音高 126 再升 12 会出上界");
        });
    }

    [Test]
    public void 音高顶到上界时整组一起缩()
    {
        var controller = new PianoRollController(
            SongOf(Melody(new Note(120, 0, 480, 100), new Note(127, 480, 480, 100))));

        var clamped = controller.ClampMoveDelta(
            new[] { new NoteRef(0, 0), new NoteRef(0, 1) }, 0, 12);

        Assert.That(clamped.DeltaPitch, Is.EqualTo(0), "最高的那个已经贴着 127，整组就升不动");
    }

    [Test]
    public void 空集和过期的ref都当不存在()
    {
        var controller = new PianoRollController(SongOf(Melody(new Note(60, 1000, 480, 100))));

        Assert.Multiple(() =>
        {
            // 空集：没有可夹的东西，增量原样回去
            var empty = controller.ClampMoveDelta(Array.Empty<NoteRef>(), -500, 9);
            Assert.That(empty, Is.EqualTo((-500L, 9)));

            // 过期（下标越界、轨越界）：这一份曲子里没有这个音，当它不在组里 ——
            // 于是剩下的那个音说了算，而不是整组一起被一个不存在的音夹住
            var stale = controller.ClampMoveDelta(
                new[] { new NoteRef(0, 99), new NoteRef(5, 0), new NoteRef(0, 0) }, -500, 0);
            Assert.That(stale.DeltaTicks, Is.EqualTo(-500), "1000 够让 500，越界的那些不参与");
        });
    }

    [Test]
    public void 全组都认不出来时原样返回()
    {
        var controller = new PianoRollController(SongOf(Melody(new Note(60, 1000, 480, 100))));

        var clamped = controller.ClampMoveDelta(new[] { new NoteRef(3, 7) }, -500, 9);

        Assert.That(clamped, Is.EqualTo((-500L, 9)));
    }

    // ==================== 零轨 ====================

    /// <summary>一条轨都没有的曲子：把轨全删光之后就是这个样子。</summary>
    private static Song NoTracks() => SongOf();

    [Test]
    public void 零轨的曲子上什么都不崩()
    {
        var controller = new PianoRollController(NoTracks());
        var viewport = controller.ViewportOf(0, 800, 200);
        var range = controller.PitchRangeOf(0);

        Assert.Multiple(() =>
        {
            Assert.That(controller.BarCount, Is.EqualTo(1), "空曲也得有一小节");
            Assert.That(controller.BarNoteCounts, Has.Count.EqualTo(1));
            Assert.That(controller.MaxBarNoteCount, Is.EqualTo(0));
            Assert.That(controller.GridTicks, Is.EqualTo(120), "网格只看分辨率，没有轨也照样有");

            Assert.That(range.High, Is.GreaterThanOrEqualTo(range.Low), "没有轨也得给一个画得出来的中性音域");
            Assert.That(viewport.HighPitch, Is.EqualTo(range.High), "视口跟着走，不抛");

            Assert.That(controller.InRangeFlagsOf(0), Is.Empty);
            Assert.That(controller.NotesInRange(0, 0, Bar), Is.Empty);
            Assert.That(controller.HitTest(0, viewport, 10, 10, out int index),
                Is.EqualTo(PianoRollGeometry.RollHit.None));
            Assert.That(index, Is.EqualTo(-1));
            Assert.That(controller.HitTestRef(0, viewport, 10, 10, out var miss),
                Is.EqualTo(PianoRollGeometry.RollHit.None));
            Assert.That(miss, Is.EqualTo(new NoteRef(-1, -1)));
            Assert.That(controller.Describe(0, 0), Is.Null);
            Assert.That(controller.DescribeSelection(), Is.Null);
            Assert.That(controller.MoveSelection(1), Is.Null);
            Assert.That(controller.MoveSelection(-1), Is.Null);
            Assert.That(controller.IsSelected(new NoteRef(0, 0)), Is.False);
            Assert.That(controller.Selection, Is.Null);
            Assert.That(controller.SelectedNotes, Is.Empty);
        });

        // 这几个没有返回值，点一遍就是为了「不抛」
        controller.SelectOnly(new NoteRef(0, 0));
        controller.SetSelection(controller.NotesInRange(0, 0, Bar));
        controller.ExtendSelection(new NoteRef(0, 0));
        controller.ClearSelection();
        controller.RestoreSelection(0, 0);
        controller.SetViewStart(Bar * 3);
        controller.SeekBar(2);
        controller.CenterOnBar(1);
        controller.SnapViewToBar();
        controller.Follow(Bar, 0.32);
    }

    [Test]
    public void 没有这条轨时按中性音域画一张空谱面()
    {
        // 删轨之后界面手里那张控制器这一帧还会被问一次（重画那条轨）——
        // 那不是错误，是「谱面空了」，所以给答案而不是抛
        var controller = new PianoRollController(SongOf(Melody(new Note(60, 0, 480, 100))));

        var viewport = controller.ViewportOf(7, 800, 200);

        Assert.Multiple(() =>
        {
            Assert.That(viewport.HighPitch, Is.GreaterThanOrEqualTo(viewport.LowPitch));
            Assert.That(controller.InRangeFlagsOf(7), Is.Empty);
            Assert.That(controller.NotesInRange(7, 0, Bar), Is.Empty);
        });
    }

    /// <summary>每小节一个音的短曲子 —— 多选那几条用例只关心「第几个音」。</summary>
    private static Song ThreeNotes() => BarsOf(3);
}

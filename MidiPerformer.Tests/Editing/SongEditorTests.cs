using MidiPerformer.Adapters;
using MidiPerformer.Adapters.Controllers;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Editing;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Tests.Corpus;
using NUnit.Framework;

namespace MidiPerformer.Tests.Editing;

/// <summary>
/// 编辑命令的对外行为：进去一份 Song，出来一份 Song。
/// 改哪一格就只动哪一格，音符一律按同一份对象复用。
/// </summary>
public class SongEditorTests
{
    /// <summary>480 tick/四分音符、4/4 的一小节。</summary>
    private const long Bar = 1920;

    private readonly SongEditor _editor = new();

    // ==================== 改 BPM：音符一个字节都不动 ====================

    [Test]
    public void 改BPM后音符一个字节都没动()
    {
        var song = SongOf(
            Map(480, new TempoChange(0, 400_000)),
            Melody(new Note(60, 0, 480, 100), new Note(64, 480, 240, 90)),
            Bass(0, new Note(40, 0, 960, 80)));

        var edited = _editor.SetBpm(song, 76);

        Assert.That(edited.Tracks, Has.Count.EqualTo(song.Tracks.Count));
        for (int i = 0; i < song.Tracks.Count; i++)
        {
            Assert.Multiple(() =>
            {
                // 值那一半：逐字段相等
                Assert.That(edited.Tracks[i].Notes, Is.EqualTo(song.Tracks[i].Notes), $"第 {i} 条轨的音符");
                // 对象那一半：同一份数组
                Assert.That(edited.Tracks[i].Notes, Is.SameAs(song.Tracks[i].Notes), $"第 {i} 条轨拿的是同一份音符数组");
                Assert.That(edited.Tracks[i], Is.SameAs(song.Tracks[i]), $"第 {i} 条轨本身也原样复用");
            });
        }
    }

    [Test]
    public void 改BPM只动速度表()
    {
        // 400000 微秒/四分音符 = 150 BPM（500000 是默认值，构造器会把它丢掉）
        var song = SongOf(Map(480, new TempoChange(0, 400_000)), Melody(new Note(60, 0, 480, 100)));

        var edited = _editor.SetBpm(song, 76);

        Assert.Multiple(() =>
        {
            Assert.That(edited, Is.Not.SameAs(song), "真的改了，所以不是同一份");
            Assert.That(edited.TempoMap.BeatsPerMinuteAt(0), Is.EqualTo(76).Within(0.001), "就是用户填的那个数");
            Assert.That(edited.TempoMap.TempoChanges[0].Tick, Is.EqualTo(0), "变速点还在原地");
            Assert.That(edited.TempoMap.TempoChanges[0].MicrosecondsPerQuarterNote,
                Is.Not.EqualTo(400_000), "这一条微秒数真的被改了");
            Assert.That(edited.TempoMap.Division, Is.EqualTo(song.TempoMap.Division), "分辨率不跟着变");
            Assert.That(edited.TempoMap.TimeSignatureChanges, Is.EqualTo(song.TempoMap.TimeSignatureChanges),
                "变拍事件原样带过去");
        });
    }

    /// <summary>
    /// 速度表里没有 tick 0 事件时，基础速度住在 <c>TempoMap</c> 的默认值（120）里，
    /// 必须补一条 tick 0 事件才改得到。
    /// </summary>
    [Test]
    public void 速度表空着时补一条tick0的基础速度()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100)));

        var edited = _editor.SetBpm(song, 76);

        Assert.Multiple(() =>
        {
            Assert.That(edited.TempoMap.TempoChanges, Has.Count.EqualTo(1));
            Assert.That(edited.TempoMap.TempoChanges[0].Tick, Is.EqualTo(0));
            Assert.That(edited.TempoMap.BeatsPerMinuteAt(0), Is.EqualTo(76).Within(0.001));
            Assert.That(edited.TempoMap.SecondsAt(480), Is.GreaterThan(song.TempoMap.SecondsAt(480)),
                "120 → 76 是变慢，同样的 tick 花掉更多秒");
        });
    }

    /// <summary>变速点不在 tick 0 时，开头那段的速度是默认值，也得被改到。</summary>
    [Test]
    public void 第一条变速事件不在开头时开头也被改到()
    {
        var song = SongOf(Map(480, new TempoChange(960, 250_000)), Melody(new Note(60, 0, 480, 100)));

        var edited = _editor.SetBpm(song, 60);

        Assert.Multiple(() =>
        {
            Assert.That(edited.TempoMap.BeatsPerMinuteAt(0), Is.EqualTo(60).Within(0.001));
            Assert.That(edited.TempoMap.BeatsPerMinuteAt(959), Is.EqualTo(60).Within(0.001), "开头那一段整体变慢");
            // 原来 960 起是 240BPM（默认 120 的两倍），缩放之后仍然是开头的两倍
            Assert.That(edited.TempoMap.BeatsPerMinuteAt(960), Is.EqualTo(120).Within(0.001));
        });
    }

    // ==================== 改 BPM：变速曲目的快慢关系 ====================

    /// <summary>变速曲目按同一个比例缩放每一条，段与段之间的快慢关系保住。</summary>
    [Test]
    public void 变速曲目按同一比例缩放快慢关系保住()
    {
        // 400000 = 150 BPM，800000 = 75 BPM：第二段是第一段的一半（tick 960 处）
        var song = SongOf(
            Map(480, new TempoChange(0, 400_000), new TempoChange(960, 800_000)),
            Melody(new Note(60, 0, 480, 100)));

        var edited = _editor.SetBpm(song, 100);

        var before = song.TempoMap.TempoChanges;
        var after = edited.TempoMap.TempoChanges;

        Assert.Multiple(() =>
        {
            Assert.That(after.Select(c => c.Tick), Is.EqualTo(before.Select(c => c.Tick)), "变速点一个都不挪");
            Assert.That(after, Has.Count.EqualTo(before.Count), "不增不减");
            Assert.That(edited.TempoMap.BeatsPerMinuteAt(0), Is.EqualTo(100).Within(0.001));
            Assert.That(edited.TempoMap.BeatsPerMinuteAt(960), Is.EqualTo(50).Within(0.001),
                "第二段仍然是开头的一半 —— 关系没变");
        });
    }

    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.VariableTempoFiles))]
    public void 变速语料改BPM后段与段的关系不变(string path)
    {
        var song = MidiReader.Read(path);
        var before = song.TempoMap.TempoChanges;
        Assert.That(before, Is.Not.Empty, "变速语料得有速度事件");

        var edited = _editor.SetBpm(song, 76);
        var byTick = edited.TempoMap.TempoChanges.ToDictionary(c => c.Tick);

        string file = Path.GetFileName(path);
        double head = song.TempoMap.BeatsPerMinuteAt(0);

        // 只比原来的每一条还在不在原地、是否被同一个倍数乘过 ——
        // 不比条数：等于默认速度的那条会被构造器去掉，「原来没有 tick 0 事件」的曲子会多出一条。
        foreach (var c in before)
        {
            Assert.That(byTick.ContainsKey(c.Tick), Is.True, $"{file}：tick {c.Tick} 的变速点不见了");
            double expected = c.BeatsPerMinute * (76.0 / head);
            double actual = byTick[c.Tick].BeatsPerMinute;
            Assert.That(actual, Is.EqualTo(expected).Within(expected * 1e-4),
                $"{file}：tick {c.Tick} 的变速点，快慢该被同一个倍数乘过");
        }

        Assert.That(edited.TempoMap.BeatsPerMinuteAt(0), Is.EqualTo(76).Within(0.01),
            $"{file}：开头就是用户填的那个数");
    }

    // ==================== 改 BPM：秒数与卷帘刻度 ====================

    /// <summary>整曲的秒数按「旧速度 : 新速度」缩放。</summary>
    [TestCase(75, 2.0)]     // 慢一半 → 秒数翻倍
    [TestCase(300, 0.5)]    // 快一倍 → 秒数减半
    public void 整曲秒数按新旧速度之比缩放(double bpm, double expectedRatio)
    {
        // 400000 微秒/四分音符 = 150 BPM
        var song = SongOf(Map(480, new TempoChange(0, 400_000)), Melody(new Note(60, 0, Bar * 4, 100)));
        long tick = Bar * 4;

        double before = song.TempoMap.SecondsAt(tick);
        double after = _editor.SetBpm(song, bpm).TempoMap.SecondsAt(tick);

        Assert.That(after / before, Is.EqualTo(expectedRatio).Within(1e-9));
    }

    /// <summary>
    /// 改 BPM 不让音符在卷帘上移动：卷帘是 tick 轴，音符的 tick 与小节刻度都不受速度影响。
    /// </summary>
    [Test]
    public void 改BPM不改变卷帘上的小节刻度与音符位置()
    {
        var song = SongOf(Map(480, new TempoChange(0, 400_000)), Melody(
            new Note(60, 0, 480, 100), new Note(64, Bar + 960, 240, 100)));

        var edited = _editor.SetBpm(song, 76);

        var before = new PianoRollController(song);
        var after = new PianoRollController(edited);

        Assert.Multiple(() =>
        {
            Assert.That(after.TicksPerBar, Is.EqualTo(before.TicksPerBar));
            Assert.That(after.BarCount, Is.EqualTo(before.BarCount), "小节数只由 tick 决定");
            Assert.That(PianoRollGeometry.BarTicks(edited.TempoMap),
                Is.EqualTo(PianoRollGeometry.BarTicks(song.TempoMap)));
        });

        // 同一个 tick 在两个控制器里的横坐标完全相同（视口各自取自对应的控制器）
        var beforeView = before.ViewportOf(0, 800, 200);
        var afterView = after.ViewportOf(0, 800, 200);
        Assert.Multiple(() =>
        {
            Assert.That(PianoRollGeometry.XAtTick(afterView, Bar + 960),
                Is.EqualTo(PianoRollGeometry.XAtTick(beforeView, Bar + 960)));
            Assert.That(afterView.LowPitch, Is.EqualTo(beforeView.LowPitch), "音域也不受影响");
            Assert.That(afterView.HighPitch, Is.EqualTo(beforeView.HighPitch));
        });
    }

    // ==================== 移调 ====================

    [Test]
    public void 移调只改轨的移调音符一点没动()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100), new Note(64, 480, 480, 100)));

        var edited = _editor.SetTranspose(song, 0, -12);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].Transpose, Is.EqualTo(-12));
            Assert.That(edited.Tracks[0].Notes, Is.EqualTo(song.Tracks[0].Notes), "逐字段相等");
            Assert.That(edited.Tracks[0].Notes, Is.SameAs(song.Tracks[0].Notes), "而且就是同一份");
            Assert.That(edited.Tracks[0].Notes.Select(n => n.Pitch), Is.EqualTo(new[] { 60, 64 }),
                "音符里写着的还是原始音高 —— 移调只在播放和导出时叠加");
            Assert.That(edited.TempoMap, Is.SameAs(song.TempoMap), "速度表不动");
        });
    }

    [Test]
    public void 移调只动目标轨别的轨一个字节不变()
    {
        var song = SongOf(
            Map(),
            Melody(new Note(60, 0, 480, 100)),
            Bass(0, new Note(40, 0, 480, 100)),
            Third(new Note(72, 0, 480, 100)));

        var edited = _editor.SetTranspose(song, 1, 12);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[1].Transpose, Is.EqualTo(12));
            Assert.That(edited.Tracks[0], Is.SameAs(song.Tracks[0]), "第 0 条轨原样");
            Assert.That(edited.Tracks[2], Is.SameAs(song.Tracks[2]), "第 2 条轨原样");
            Assert.That(edited.Tracks[1].Notes, Is.SameAs(song.Tracks[1].Notes), "目标轨的音符也原样");
        });
    }

    [Test]
    public void 移调是绝对赋值不是增量()
    {
        var song = SongOf(Map(), Bass(3, new Note(60, 0, 480, 100)));

        var edited = _editor.SetTranspose(song, 0, -5);

        Assert.That(edited.Tracks[0].Transpose, Is.EqualTo(-5), "不是 3 + (-5) = -2");
    }

    // ==================== 音色 ====================

    /// <summary>换音色只换轨上那一格，音符一个字节都不动；9 号声道的鼓轨也照改。</summary>
    [Test]
    public void 换音色只改轨的音色音符一点没动()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100), new Note(64, 480, 480, 100)));

        var edited = _editor.SetProgram(song, 0, 22);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[0].Program, Is.EqualTo(22), "换成了口琴");
            Assert.That(edited.Tracks[0].Notes, Is.SameAs(song.Tracks[0].Notes), "音符是同一份，不是凑巧相等");
            Assert.That(edited.Tracks[0].Transpose, Is.EqualTo(song.Tracks[0].Transpose), "移调不动");
            Assert.That(edited.TempoMap, Is.SameAs(song.TempoMap), "速度表不动");
        });
    }

    [Test]
    public void 换音色只动目标轨别的轨一个字节不变()
    {
        var song = SongOf(
            Map(),
            Melody(new Note(60, 0, 480, 100)),
            Bass(0, new Note(40, 0, 480, 100)),
            Third(new Note(72, 0, 480, 100)));   // 第 9 声道（鼓）

        var edited = _editor.SetProgram(song, 2, 16);

        Assert.Multiple(() =>
        {
            Assert.That(edited.Tracks[2].Program, Is.EqualTo(16), "鼓轨也照改");
            Assert.That(edited.Tracks[0], Is.SameAs(song.Tracks[0]));
            Assert.That(edited.Tracks[1], Is.SameAs(song.Tracks[1]));
        });
    }

    [Test]
    public void 音色没变时返回同一份曲子()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100)));   // 24

        Assert.That(_editor.SetProgram(song, 0, 24), Is.SameAs(song));
    }

    /// <summary>越界抛异常，不夹到 0..127。</summary>
    [TestCase(-1)]
    [TestCase(128)]
    [TestCase(9999)]
    public void 非法音色号抛中文错(int program)
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _editor.SetProgram(song, 0, program));

        Assert.That(ex!.Message, Does.Contain("音色"), "错误消息得是给人看的中文");
    }

    [TestCase(0)]
    [TestCase(127)]
    public void 边界上的音色号是合法的(int program)
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100)));

        Assert.That(_editor.SetProgram(song, 0, program).Tracks[0].Program, Is.EqualTo(program));
    }

    [TestCase(-1)]
    [TestCase(3)]
    public void 换音色时轨下标越界也抛中文错(int trackIndex)
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _editor.SetProgram(song, trackIndex, 22));

        Assert.That(ex!.Message, Does.Contain("越界"), "错误消息得是给人看的中文");
    }

    // ==================== 没改就还回来同一个 ====================

    /// <summary>「改没改」看引用是不是同一个，所以没改必须还回来原来那一个。</summary>
    [TestCase(0)]
    [TestCase(12)]
    [TestCase(-12)]
    public void 移调值没变时返回同一份曲子(int semitones)
    {
        var song = SongOf(Map(), Bass(semitones, new Note(60, 0, 480, 100)));

        Assert.That(_editor.SetTranspose(song, 0, semitones), Is.SameAs(song));
    }

    [Test]
    public void 速度没变时返回同一份曲子()
    {
        // tick 0 上的 300000 微秒 = 200 BPM
        var song = SongOf(Map(480, new TempoChange(0, 300_000)), Melody(new Note(60, 0, 480, 100)));

        Assert.That(_editor.SetBpm(song, 200), Is.SameAs(song));
    }

    /// <summary>
    /// 空速度表（全曲 120）设成 120：补出来的 tick 0 事件正好等于默认值，会被
    /// <c>TempoMap</c> 的构造器丢掉，结果一样，所以仍然算「没改」。
    /// </summary>
    [Test]
    public void 空速度表设成120也是没改()
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100)));

        Assert.That(_editor.SetBpm(song, 120), Is.SameAs(song));
    }

    // ==================== 越界与非法输入 ====================

    [TestCase(0.0)]
    [TestCase(-1.0)]
    [TestCase(1000.1)]
    [TestCase(1e9)]
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void 非法速度抛中文错(double beatsPerMinute)
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _editor.SetBpm(song, beatsPerMinute));

        Assert.That(ex!.Message, Does.Contain("速度"), "错误消息得是给人看的中文");
    }

    [TestCase(1.0)]
    [TestCase(1000.0)]
    public void 边界上的速度是合法的(double beatsPerMinute)
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100)));

        var edited = _editor.SetBpm(song, beatsPerMinute);

        Assert.That(edited.TempoMap.BeatsPerMinuteAt(0), Is.EqualTo(beatsPerMinute).Within(0.01));
    }

    [TestCase(-1)]
    [TestCase(3)]
    public void 轨下标越界抛中文错(int trackIndex)
    {
        var song = SongOf(Map(), Melody(new Note(60, 0, 480, 100)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _editor.SetTranspose(song, trackIndex, 12));

        Assert.That(ex!.Message, Does.Contain("越界"), "错误消息得是给人看的中文");
    }

    // ==================== 真实语料 ====================

    /// <summary>整份真实语料扫一遍：任何一首改完 BPM，音符都逐字段相等。</summary>
    [Test]
    public void 每首语料改完BPM音符都逐字段相等()
    {
        MidiCorpus.AssertCorpusPresent();

        int songs = 0, notes = 0;
        foreach (var path in MidiCorpus.Files)
        {
            string file = Path.GetFileName(path);
            var song = MidiReader.Read(path);
            var edited = _editor.SetBpm(song, 76);

            Assert.That(edited.Tracks, Has.Count.EqualTo(song.Tracks.Count), $"{file}：轨数");
            for (int i = 0; i < song.Tracks.Count; i++)
                Assert.That(edited.Tracks[i].Notes, Is.EqualTo(song.Tracks[i].Notes),
                    $"{file}：第 {i} 条轨（{song.Tracks[i].Name}）的音符");

            Assert.That(edited.TempoMap.BeatsPerMinuteAt(0), Is.EqualTo(76).Within(0.01), $"{file}：开头速度");

            songs++;
            notes += song.Tracks.Sum(t => t.NoteCount);
        }

        Assert.Multiple(() =>
        {
            // 防止逐条比对在空壳曲子上空转
            Assert.That(songs, Is.GreaterThan(50), "语料条数不对，检查 drywetmidi 仓库");
            Assert.That(notes, Is.GreaterThan(50_000), "扫过的音符太少，这条基本在空转");
        });
    }

    // ==================== 帮手 ====================

    private static Song SongOf(TempoMap map, params Track[] tracks) => new(tracks, map);

    private static TempoMap Map(int pulsesPerQuarterNote = 480, params TempoChange[] changes)
        => new(TimeDivision.PulsesPerQuarter(pulsesPerQuarterNote), changes);

    private static Track Melody(params Note[] notes) => new(0, 0, "主旋律", 24, notes);

    private static Track Bass(int transpose, params Note[] notes) => new(1, 1, "贝斯", 33, notes, transpose);

    private static Track Third(params Note[] notes) => new(2, 9, "鼓点", 0, notes);
}

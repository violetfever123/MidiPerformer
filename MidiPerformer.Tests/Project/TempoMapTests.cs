using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MidiPerformer.Core.Model;
using MidiPerformer.Core.UseCases.Project;
using MidiPerformer.Tests.Corpus;
using NUnit.Framework;
// DryWetMidi 也有同名的 MidiReader / TempoMap / TimeDivision，加别名区分
using MidiReader = MidiPerformer.Core.UseCases.Project.MidiReader;
using ModelTempoMap = MidiPerformer.Core.Model.TempoMap;
using ModelTimeDivision = MidiPerformer.Core.Model.TimeDivision;

namespace MidiPerformer.Tests.Project;

/// <summary>
/// 速度表：tick ⇄ 秒。
/// 最要紧的一条是 <see cref="与DryWetMidi逐位一致"/> —— 全链对拍要求我们算出的秒数和原版
/// （DryWetMidi 的换算路径）逐位相同，差一个 ULP 事件表的浮点时间戳就对不上。
/// </summary>
public class TempoMapTests
{
    [Test]
    public void 语料目录在()
    {
        MidiCorpus.AssertCorpusPresent();
        Assert.That(MidiCorpus.Files.Count, Is.GreaterThan(50), "语料条数不对，检查 drywetmidi 仓库");
    }

    // ==================== 逐位一致 ====================

    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void 与DryWetMidi逐位一致(string path)
    {
        var file = MidiFile.Read(path);
        var dryTempoMap = file.GetTempoMap();
        var song = MidiReader.Read(path);

        var ticks = SampleTicks(file);
        Assert.That(ticks, Is.Not.Empty, "语料里一个音符都没有，采样不到 tick");

        foreach (long tick in ticks)
        {
            // 原版 MidiLoader.TicksToSeconds 就是这一行，逐位比
            double expected = TimeConverter.ConvertTo<MetricTimeSpan>(tick, dryTempoMap).TotalSeconds;
            double actual = song.TempoMap.SecondsAt(tick);

            // Is.EqualTo(double) 默认带容差，逐位比得自己钉死
            Assert.That(BitConverter.DoubleToInt64Bits(actual), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected)),
                $"{Path.GetFileName(path)} tick={tick}：我们 {actual:R}，DryWetMidi {expected:R}");
        }
    }

    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.TestFiles))]
    public void tick到秒再回tick是恒等(string path)
    {
        var song = MidiReader.Read(path);
        var ticks = SampleTicks(MidiFile.Read(path));

        foreach (long tick in ticks)
        {
            double seconds = song.TempoMap.SecondsAt(tick);
            long back = song.TempoMap.TickAt(seconds);
            Assert.That(back, Is.EqualTo(tick),
                $"{Path.GetFileName(path)} tick={tick} → {seconds:R}s → tick={back}");
        }
    }

    // ==================== 变速曲目 ====================

    [Test]
    public void 变速点前后的事件时间都对()
    {
        // 480 tick/四分音符；0 起 120BPM（默认，不出现在事件里），第 960 tick 起改 60BPM
        // ⇒ 前 960 tick = 1 秒，之后每个四分音符 1 秒
        var map = new ModelTempoMap(
            ModelTimeDivision.PulsesPerQuarter(480),
            new[] { new TempoChange(960, 1_000_000) });

        Assert.Multiple(() =>
        {
            Assert.That(map.SecondsAt(0), Is.EqualTo(0.0));
            Assert.That(map.SecondsAt(480), Is.EqualTo(0.5), "变速点之前");
            Assert.That(map.SecondsAt(960), Is.EqualTo(1.0), "变速点本身");
            Assert.That(map.SecondsAt(1440), Is.EqualTo(2.0), "变速点之后");
            Assert.That(map.SecondsAt(1920), Is.EqualTo(3.0), "变速点之后更远");
            // 变速点两侧的斜率必须不同
            Assert.That(map.SecondsAt(960 + 480) - map.SecondsAt(960),
                Is.GreaterThan(map.SecondsAt(480) - map.SecondsAt(0)));
        });
    }

    [TestCaseSource(typeof(MidiCorpus), nameof(MidiCorpus.VariableTempoFiles))]
    public void 变速语料确实被读成了变速(string path)
    {
        var song = MidiReader.Read(path);
        var distinct = song.TempoMap.TempoChanges
            .Select(c => c.MicrosecondsPerQuarterNote)
            .Append(ModelTempoMap.DefaultMicrosecondsPerQuarterNote)
            .Distinct()
            .Count();
        Assert.That(distinct, Is.GreaterThan(1), $"{Path.GetFileName(path)} 应该是一首变速曲");
    }

    [Test]
    public void 变速曲目在变速点两侧积分连续不跳变()
    {
        var map = new ModelTempoMap(
            ModelTimeDivision.PulsesPerQuarter(480),
            new[] { new TempoChange(960, 1_000_000), new TempoChange(1920, 250_000) });

        // 以 1ms 的步长扫过整条曲线：每一步的 tick 增量都该是连续的小量，变速点处只变斜率
        long previous = map.TickAt(0);
        for (int ms = 1; ms <= 4000; ms++)
        {
            long now = map.TickAt(ms / 1000.0);
            long step = now - previous;
            Assert.That(step, Is.GreaterThanOrEqualTo(0), $"{ms}ms 处时间倒流了");
            // 最快一档 250000us/四分音符 = 240BPM，480 tick/四分音符 → 1ms ≈ 1.92 tick；
            // 6 留了 3 倍余量，超过就是分段边界算错了
            Assert.That(step, Is.LessThanOrEqualTo(6), $"{ms}ms 处 tick 跳了 {step}");
            previous = now;
        }
    }

    // ==================== 速度显示 ====================

    [Test]
    public void 默认速度是120BPM()
    {
        var map = new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480));
        Assert.That(map.BeatsPerMinuteAt(0), Is.EqualTo(120.0).Within(1e-9));
        Assert.That(map.TempoChanges, Is.Empty, "tick 0 上的默认速度不该被当成一条变速事件");
    }

    [Test]
    public void 取某tick处的速度()
    {
        var map = new ModelTempoMap(
            ModelTimeDivision.PulsesPerQuarter(480),
            new[] { new TempoChange(0, 300_000), new TempoChange(960, 500_000) });

        Assert.Multiple(() =>
        {
            Assert.That(map.BeatsPerMinuteAt(0), Is.EqualTo(200.0).Within(1e-9));
            Assert.That(map.BeatsPerMinuteAt(959), Is.EqualTo(200.0).Within(1e-9));
            Assert.That(map.BeatsPerMinuteAt(960), Is.EqualTo(120.0).Within(1e-9));
        });
    }

    // ==================== 越界 ====================

    /// <summary>
    /// 装不下的 tick 要炸出中文错误，不能静默溢出一个垃圾秒数（照 DryWetMidi 的
    /// "Time span is too big." 搬过来）。
    /// </summary>
    [Test]
    public void tick大到装不下时报错不静默溢出()
    {
        var map = new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480));

        var ex = Assert.Throws<InvalidOperationException>(() => map.SecondsAt(long.MaxValue));
        Assert.That(ex!.Message, Does.Contain("太大"), "错误消息得是给人看的中文");
    }

    /// <summary>
    /// 秒 → tick 的越界与 NaN 拦截：<c>(long)NaN</c> 在 C# 里是未定义值
    /// （实测 −8854437155380584 这种垃圾），进了播放器出不来。
    /// </summary>
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity)]
    [TestCase(double.MaxValue)]
    [TestCase(1e300)]
    public void 秒数太大或不是数时报错不静默溢出(double seconds)
    {
        var ppq = new ModelTempoMap(ModelTimeDivision.PulsesPerQuarter(480));
        var smpte = new ModelTempoMap(ModelTimeDivision.Smpte(25, 40));

        Assert.Multiple(() =>
        {
            Assert.That(() => ppq.TickAt(seconds), Throws.TypeOf<InvalidOperationException>(), $"PPQ {seconds:R}");
            Assert.That(() => smpte.TickAt(seconds), Throws.TypeOf<InvalidOperationException>(), $"SMPTE {seconds:R}");
        });
    }

    [Test]
    public void 越界检查没有误伤正常范围()
    {
        var map = new ModelTempoMap(
            ModelTimeDivision.PulsesPerQuarter(480),
            new[] { new TempoChange(960, 1_000_000) });

        Assert.Multiple(() =>
        {
            Assert.That(map.SecondsAt(0), Is.EqualTo(0.0));
            Assert.That(map.SecondsAt(480), Is.EqualTo(0.5));
            Assert.That(map.SecondsAt(960), Is.EqualTo(1.0));
            Assert.That(map.TickAt(0.0), Is.EqualTo(0));
            Assert.That(map.TickAt(0.5), Is.EqualTo(480));
            Assert.That(map.TickAt(1.0), Is.EqualTo(960));
            // 负 tick / 负秒不是越界，只是没人用
            Assert.That(map.SecondsAt(-480), Is.EqualTo(-0.5).Within(1e-12));
            Assert.That(map.TickAt(-0.5), Is.EqualTo(-480));
        });
    }

    // ==================== SMPTE ====================

    [Test]
    public void SMPTE按帧换算与速度表无关()
    {
        // 25 帧/秒、每帧 40 tick → 1000 tick/秒；速度事件对 SMPTE 毫无影响
        var map = new ModelTempoMap(
            ModelTimeDivision.Smpte(25, 40),
            new[] { new TempoChange(0, 250_000) });

        Assert.Multiple(() =>
        {
            Assert.That(map.SecondsAt(1000), Is.EqualTo(1.0).Within(1e-12));
            Assert.That(map.SecondsAt(500), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(map.TickAt(2.0), Is.EqualTo(2000));
        });
    }

    // ==================== 采样 ====================

    /// <summary>
    /// 采样点：每个音符的起止 tick，再均匀撒一批中间点（变速点常落在没有音符的地方）。
    /// </summary>
    private static List<long> SampleTicks(MidiFile file)
    {
        var ticks = new SortedSet<long> { 0 };
        long maxTick = 0;

        foreach (var chunk in file.GetTrackChunks())
        {
            foreach (var n in chunk.GetNotes())
            {
                ticks.Add(n.Time);
                ticks.Add(n.EndTime);
                if (n.EndTime > maxTick) maxTick = n.EndTime;
            }
        }

        long step = Math.Max(1, maxTick / 400);
        for (long t = 0; t <= maxTick; t += step) ticks.Add(t);

        return ticks.ToList();
    }
}

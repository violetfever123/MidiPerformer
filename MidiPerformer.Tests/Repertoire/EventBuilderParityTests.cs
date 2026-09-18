using NUnit.Framework;
using Original = HarpAutoPlayer.Engine;
using Ported = MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Tests.Repertoire;

/// <summary>
/// 移植保真度对拍 —— 这是「逐字复刻」这句话的**证明**。
///
/// 同一个音符序列喂给两边：一边是原版 <c>harmonica-auto-player@a14335c</c> 的代码本体
/// （测试工程直接 ProjectReference 引用原版工程，跑的不是副本），一边是我们
/// <c>Core/UseCases/Perform/Repertoire/</c> 里那份。逐条比事件的**时间 / 键 / 按下还是松开**。
///
/// 谁改动了移植文件，这里立刻变红。
///
/// **对拍全程不发任何按键**：两边都只调"建表"入口（原版的 <c>BuildScheduleForTest</c>、
/// 我们的 <c>EventBuilder.Build</c>），它们是纯函数，没有一行碰到 SendInput。
/// 原版的 <c>Play</c> / <c>Execute</c> 一次都没有被调用。
///
/// 语料是**手工构造的音符，不是 MIDI 文件** —— 这一层验的是移植切得对不对，
/// 真实 MIDI 语料的全链对拍在 02。
/// </summary>
public class EventBuilderParityTests
{
    /// <summary>与两边类型都无关的中立音符描述，喂之前各自 materialize 一份。</summary>
    public readonly record struct NoteSpec(
        int Pitch, double Start, double End, char Key, bool Sharp, int Slot, bool InRange);

    // ============================ 语料 ============================

    private static NoteSpec N(double start, double end, char key,
        int slot = 0, bool sharp = false, int pitch = 60)
        => new(pitch, start, end, key, sharp, slot, true);

    /// <summary>手写边界用例：同刻起音 / 连奏重叠 / 跨八度 / 升半音 / 最高两个音 / 超范围 / 空轨。</summary>
    public static IEnumerable<TestCaseData> BoundaryCorpus()
    {
        static TestCaseData C(string name, params NoteSpec[] notes)
            => new TestCaseData((object)notes).SetName(name);

        yield return C("空轨");

        yield return C("单音", N(0, 0.5, 'Z'));

        // 口琴是单音乐器：同刻起音本来只能吹响一个，后一个要顺延而不是把前音压没。
        yield return C("同刻起音（不同键）", N(1.0, 1.4, 'Z'), N(1.0, 1.4, 'C'));
        yield return C("同刻起音（同键）", N(1.0, 1.4, 'Z'), N(1.0, 1.4, 'Z'));
        yield return C("三音同刻", N(1.0, 1.2, 'Z'), N(1.0, 1.5, 'X'), N(1.0, 1.1, 'C'));

        // 连奏：前音还在响、后音才起 —— 不算多声部，靠槽位排开。
        yield return C("连奏重叠", N(0, 0.5, 'Z'), N(0.3, 0.8, 'X'));
        yield return C("前音长后音短且被包住", N(0, 2.0, 'Z'), N(0.5, 0.6, 'X'));
        yield return C("零时长音", N(0.5, 0.5, 'Z'));
        yield return C("一毫秒音", N(0, 0.001, 'Z'), N(0.002, 0.5, 'X'));
        yield return C("长休止", N(0, 0.5, 'Z'), N(5.0, 5.4, 'X'));

        // 跨八度：修饰键（鼠标左/右）的按下与松开必须排在音键之前。
        yield return C("跨八度 低→基准→高→基准",
            N(0, 0.3, 'Z', -1), N(0.4, 0.7, 'Z', 0), N(0.8, 1.1, 'Z', 1), N(1.2, 1.5, 'Z', 0));
        yield return C("跨八度同刻起音", N(0, 0.4, 'Z', -1), N(0, 0.4, 'X', 1));

        // 升半音：鼠标中键。
        yield return C("升半音切换",
            N(0, 0.3, 'C', 0, true), N(0.4, 0.7, 'C', 0, false), N(0.8, 1.1, 'D', 0, true));
        yield return C("升半音 + 跨八度同时切换",
            N(0, 0.3, 'C', -1, true), N(0.4, 0.7, 'C', 1, false), N(0.8, 1.1, 'D', 0, true));

        // 最高两个音：高高音 do / #do 都用逗号键，落在 High 槽位。
        yield return C("最高两个音", N(0, 0.3, ',', 1, false), N(0.4, 0.7, ',', 1, true));
        yield return C("最高两个音夹在跨八度中间",
            N(0, 0.3, 'Z', 0), N(0.4, 0.7, ',', 1, false), N(0.8, 1.1, ',', 1, true));

        // 超出可演奏范围（InRange=false）：建表这一段不按 InRange 过滤，两边必须一致地处理。
        yield return C("超范围（无键位，移调后超出 MIDI 音域）",
            new NoteSpec(-1, 0, 0.4, ' ', false, 0, false), N(0.5, 0.9, 'Z'));
        yield return C("超范围（有键位，最高只能到高高音#do）",
            new NoteSpec(108, 0, 0.4, ',', true, 1, false), N(0.5, 0.9, 'Z'));
        yield return C("音域两端 pitch 0 与 127",
            new NoteSpec(0, 0, 0.3, 'Z', false, -1, false),
            new NoteSpec(127, 0.4, 0.7, ',', false, 1, false));

        // 挤到下限：同键极密，会走"时值被压到下限"那条分支。
        {
            var dense = new NoteSpec[12];
            for (int i = 0; i < dense.Length; i++)
                dense[i] = N(i * 0.02, i * 0.02 + 0.05, 'Z');
            yield return C("同键极密（12 音 × 20ms）", dense);
        }
        {
            var dense = new NoteSpec[8];
            for (int i = 0; i < dense.Length; i++)
                dense[i] = N(i * 0.005, i * 0.005 + 0.004, Ported.PlayKeys.Keys[i % 7]);
            yield return C("极快音阶（8 音 × 5ms）", dense);
        }
    }

    /// <summary>随机生成语料：同刻、重叠、长音盖短音、修饰键来回切，都按概率出现。</summary>
    public static IEnumerable<TestCaseData> RandomCorpus()
    {
        for (int seed = 0; seed < 200; seed++)
            yield return new TestCaseData((object)Generate(seed)).SetName($"随机语料 seed={seed}");
    }

    private static NoteSpec[] Generate(int seed)
    {
        var rng = new Random(seed);
        int count = rng.Next(1, 25);
        var specs = new NoteSpec[count];

        double cursor = 0;
        for (int i = 0; i < count; i++)
        {
            // 四分之一的机会与前音同刻起音
            double start = cursor + (rng.Next(4) == 0 ? 0 : rng.NextDouble() * 0.2);
            double length = 0.005 + rng.NextDouble() * 0.8;
            char key = rng.Next(8) == 0
                ? Ported.PlayKeys.TopKey
                : Ported.PlayKeys.Keys[rng.Next(Ported.PlayKeys.Keys.Length)];
            int slot = rng.Next(5) switch { 0 => -1, 1 or 2 => 1, _ => 0 };
            bool sharp = rng.Next(5) == 0;
            bool inRange = rng.Next(20) != 0;

            specs[i] = new NoteSpec(
                36 + rng.Next(61), start, start + length, key, sharp, slot, inRange);

            // 步进常常小于上一个音的时值 → 大面积重叠
            cursor = start + rng.NextDouble() * 0.3;
        }

        return specs;
    }

    // ============================ 断言 ============================

    [TestCaseSource(nameof(BoundaryCorpus))]
    public void 手写边界用例_两边事件表逐条相等(NoteSpec[] specs) => AssertParity(specs);

    [TestCaseSource(nameof(RandomCorpus))]
    public void 随机语料_两边事件表逐条相等(NoteSpec[] specs) => AssertParity(specs);

    /// <summary>三档 InputTiming 各跑一遍（稳健 / 标准 / 极限）。</summary>
    private static void AssertParity(IReadOnlyList<NoteSpec> specs)
    {
        foreach (var (name, originalTiming, portedTiming) in Timings())
        {
            var expected = BuildWithOriginal(specs, originalTiming);
            var actual = BuildWithPorted(specs, portedTiming);

            // 防止"两边都建出空表 → 相等 → 绿"的假绿
            if (specs.Count > 0)
                Assert.That(expected, Is.Not.Empty,
                    $"{name}：原版对 {specs.Count} 个音符建出了空事件表，语料本身有问题");

            Assert.That(actual, Is.EqualTo(expected),
                $"{name}：{specs.Count} 个音符，两边事件表不一致");
        }
    }

    private static IEnumerable<(string Name, Original.InputTiming Original, Ported.InputTiming Ported)> Timings()
    {
        yield return ("稳健", Original.InputTiming.Safe, Ported.InputTiming.Safe);
        yield return ("标准", Original.InputTiming.Standard, Ported.InputTiming.Standard);
        yield return ("极限", Original.InputTiming.Aggressive, Ported.InputTiming.Aggressive);
    }

    /// <summary>原版：直接跑原版工程本体导出的公开测试入口。</summary>
    private static (double T, int Kind, char Code, bool Down)[] BuildWithOriginal(
        IReadOnlyList<NoteSpec> specs, Original.InputTiming timing)
    {
        var engine = new Original.PlaybackEngine();
        return engine.BuildScheduleForTest(MaterializeOriginal(specs), timing);
    }

    /// <summary>我们这边：走移植进来的 EventBuilder。</summary>
    private static (double T, int Kind, char Code, bool Down)[] BuildWithPorted(
        IReadOnlyList<NoteSpec> specs, Ported.InputTiming timing)
    {
        var builder = new Ported.EventBuilder { Timing = timing };
        var (events, _) = builder.Build(
            MaterializePorted(specs), Ported.EventBuilder.ModState.None);

        return events.Select(e => (e.T, e.Kind, e.Code, e.Down)).ToArray();
    }

    private static List<Original.MappedNote> MaterializeOriginal(IReadOnlyList<NoteSpec> specs)
        => specs.Select(s => new Original.MappedNote
        {
            Pitch = s.Pitch,
            Start = s.Start,
            End = s.End,
            Key = s.Key,
            Sharp = s.Sharp,
            OctaveSlot = (Original.Slot)s.Slot,
            InRange = s.InRange,
        }).ToList();

    private static List<Ported.MappedNote> MaterializePorted(IReadOnlyList<NoteSpec> specs)
        => specs.Select(s => new Ported.MappedNote
        {
            Pitch = s.Pitch,
            Start = s.Start,
            End = s.End,
            Key = s.Key,
            Sharp = s.Sharp,
            OctaveSlot = (Ported.Slot)s.Slot,
            InRange = s.InRange,
        }).ToList();
}

using System.Reflection;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.App.SelfTest;

/// <summary>
/// 【开发用】纯逻辑自检：拿几条手写的边界语料跑 <see cref="EventBuilder.Build"/>，
/// 断言事件表的关键不变量。
///
/// **为什么要有它**：exe 开 <c>PublishTrimmed</c>，裁剪会改变行为，而 NUnit 跑的是**没裁剪**的构建，
/// 证明不了发布产物。只有真拿裁剪后的 exe 跑一次才算数 —— 这就是这个类的全部用途。
/// 全量用例（S1–S4、对拍）在 MidiPerformer.Tests，这里只留几条冒烟用例，不重复那一千条。
///
/// 用法：设环境变量 <c>MIDIPERFORMER_SELFTEST=1</c> 启动程序，或在值里给一个报告文件路径：
/// <c>MIDIPERFORMER_SELFTEST=E:\tmp\selftest.txt</c>；不给路径就写到
/// <c>%TEMP%\midiperformer-selftest.txt</c>；报告一律 <c>.txt</c>（环境变量驱动的路径只在开发机上用，
/// 别写出奇怪的后缀）。取值 <c>0</c> / <c>false</c> / <c>no</c> / <c>off</c> 等于没设，程序照常开窗。
///
/// 走这条路时程序**不创建窗口、不注册热键、不碰按键与 MIDI 设备**（只调 Core 里那几个纯函数），
/// 跑完直接 <c>Environment.Exit</c>，退出码 0 = 全过，1 = 有用例失败。
/// 外面由 <c>tools/run-selftest.ps1</c> 驱动，脚本按这份报告的 PASS / FAIL 行判定。
/// </summary>
internal static class PerformerSelfTest
{
    public const string EnvVar = "MIDIPERFORMER_SELFTEST";

    private const string ReportName = "midiperformer-selftest.txt";

    /// <summary>
    /// 裁剪时钉住的程序集（<c>TrimmerRootAssembly</c>）。
    /// **与 MidiPerformer.App.csproj 里那份名单一一对应，改一边就得改另一边** ——
    /// 这里是"裁剪到底有没有把它们留下来"的运行时一侧证据。
    /// </summary>
    private static readonly string[] PinnedAssemblies =
    {
        "MidiPerformer",                 // App 程序集（AssemblyName 是 MidiPerformer）
        "Avalonia", "Avalonia.Base", "Avalonia.Controls", "Avalonia.Desktop",
        "Avalonia.Markup", "Avalonia.Markup.Xaml", "Avalonia.Skia",
        "Avalonia.Themes.Fluent", "Avalonia.Win32",
        "Melanchall.DryWetMidi",
    };

    /// <summary>三档时序各跑一遍。标准档是用例自己那条断言的档位，另两档只过不变量。</summary>
    private static readonly InputTiming[] Timings =
        { InputTiming.Safe, InputTiming.Standard, InputTiming.Aggressive };

    private static readonly List<string> Lines = new();
    private static int _failed;

    public static bool Requested
    {
        get
        {
            string v = Environment.GetEnvironmentVariable(EnvVar) ?? "";
            return v.Length > 0 && !IsOffValue(v);
        }
    }

    /// <summary>
    /// 「明确关掉」的取值：0 / false / no / off（不分大小写）。
    /// 与仓库其它开关的 <c>=="1"</c> 约定保持一致：设成 0 就是关掉，而不是"不建窗口直接退出"。
    /// </summary>
    private static bool IsOffValue(string value)
    {
        string v = value.Trim();
        return v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase)
            || v.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>环境变量给出的自定义报告路径（没有就是空串）。</summary>
    private static string CustomPath(string value)
    {
        string v = value.Trim();
        // "1" 是约定的「开」，没有路径含义
        if (v == "1" || IsOffValue(v)) return "";
        return v.Contains('\\') || v.Contains('/') ? v : "";
    }

    /// <summary>跑完全部用例，返回进程退出码（0 全过 / 1 有用例失败）。</summary>
    public static int Run()
    {
        string value = Environment.GetEnvironmentVariable(EnvVar) ?? "";
        string custom = CustomPath(value);
        string path = custom.Length > 0 ? custom : Path.Combine(Path.GetTempPath(), ReportName);
        // 报告一律写成 .txt：路径是给脚本 grep 的，别写出无从判断的后缀
        if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Path.GetTempPath(), ReportName);

        Lines.Add($"演奏器内置自检 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Lines.Add($"进程：{Environment.ProcessPath}");
        Lines.Add($"运行时：{Environment.Version}；单文件发布：{(IsSingleFile() ? "是" : "否（开发构建）")}");
        Lines.Add($"语料：{Cases().Count} 个手写用例；时序档位：{string.Join(" / ", Timings.Select(t => t.Name))}（不变量三档各跑一遍）");
        Lines.Add("");

        try
        {
            TestCorpus();
            TestPinnedAssemblies();
        }
        catch (Exception ex)
        {
            _failed++;
            Lines.Add("FAIL 自检抛异常：" + ex);
        }

        Lines.Add("");
        Lines.Add(_failed == 0 ? "结果：全部通过" : $"结果：{_failed} 项失败");

        string text = string.Join(Environment.NewLine, Lines);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        }
        catch { /* 写不出去也要把退出码给对 */ }
        try { Console.WriteLine(text); } catch { }

        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 单文件发布时 <c>Assembly.Location</c> 是空串（程序集不再以文件形式存在），
    /// 拿它当"这是不是发布产物"的判据。只写进报告，不做断言 —— 开发构建跑同一套自检也得过。
    /// IL3000 是"别把 Location 当路径用"的裁剪告警，这里要的恰恰是它为空这件事，所以就地关掉。
    /// </summary>
    private static bool IsSingleFile()
    {
#pragma warning disable IL3000
        return string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);
#pragma warning restore IL3000
    }

    // ================= 语料 =================

    /// <summary>一个冒烟用例：手搓的语料 + 建表起点 + 它自己那条断言。</summary>
    /// <param name="Notes">映射后的音符。手搓，不读文件。</param>
    /// <param name="StartMods">建表起点（游戏侧当前的修饰键真实状态）。</param>
    /// <param name="SameInstantPacked">
    /// 语料里含「同刻起音」：口琴是单音乐器，前音必须让位给后音，
    /// <c>Build</c> 会把前音压到「跨过一帧」—— 所以这一例的按住时长下限是一帧，不是 MinHoldMs。
    /// </param>
    /// <param name="Verify">这一例自己的断言，只跑标准档（免得同一句话在报告里出现三遍）。</param>
    private sealed record SmokeCase(
        string Name,
        MappedNote[] Notes,
        EventBuilder.ModState StartMods,
        bool SameInstantPacked,
        Action<CaseRun> Verify);

    /// <summary>一次建表的结果，供用例自己的断言用。</summary>
    private sealed record CaseRun(
        string CaseName,
        InputTiming Timing,
        List<EventBuilder.PhysicalEvent> Events,
        double Total,
        EventBuilder Builder);

    /// <summary>手搓一个映射后的音符：起点、终点、音键、八度槽位、是否升半音。</summary>
    private static MappedNote N(double start, double end, char key, int slot = 0, bool sharp = false,
        int pitch = 60, bool inRange = true)
        => new()
        {
            Pitch = pitch,
            Start = start,
            End = end,
            Key = key,
            Sharp = sharp,
            OctaveSlot = (Slot)slot,
            InRange = inRange,
        };

    /// <summary>
    /// 手写边界用例：空轨 / 同刻起音 / 连奏重叠 / 跨八度（修饰键）/ 升半音 / 同键重触发 /
    /// 超范围空拍 / 起点残留。都在 spec 的 S3 边界清单里，只是**不搬 1000 条**过来。
    /// </summary>
    private static List<SmokeCase> Cases() => new()
    {
        new("空轨", Array.Empty<MappedNote>(), EventBuilder.ModState.None, false,
            r => Check("空轨：建出空事件表，总时长 0",
                r.Events.Count == 0 && r.Total == 0, $"事件 {r.Events.Count} 条，总时长 {r.Total:F4}s")),

        new("单音", new[] { N(0, 0.5, 'Z') }, EventBuilder.ModState.None, false,
            r => Check("单音：一次按下 + 一次松开，按住 = 谱面时值",
                r.Events.Count == 2 && r.Events[0].Down && !r.Events[1].Down
                && r.Events[1].T > r.Events[0].T && Math.Abs(r.Events[1].T - 0.5) < 1e-9,
                Dump(r))),

        // 口琴是单音乐器：同刻起音本来只能吹响一个。靠"缩短前音"腾位置就会产生零时长按键，
        // 游戏按帧采样时整段被吃掉 → 后音必须顺延，而不是把前音压没。
        new("同刻起音（不同键）", new[] { N(1.0, 1.4, 'Z'), N(1.0, 1.4, 'C') }, EventBuilder.ModState.None, true,
            r => Check("同刻起音：后音顺延到前音之后（Z 松 → C 按，时间不重叠）",
                r.Events.Count == 4
                && r.Events[0].Down && r.Events[0].Code == 'Z'
                && !r.Events[1].Down && r.Events[1].Code == 'Z'
                && r.Events[2].Down && r.Events[2].Code == 'C'
                && r.Events[2].T > r.Events[0].T
                && ProbeSawSqueeze(r.Builder),
                Dump(r))),

        new("连奏重叠", new[] { N(0, 0.5, 'Z'), N(0.3, 0.8, 'X') }, EventBuilder.ModState.None, false,
            r => Check("连奏重叠：前音在后音按下之前抬起（槽位排开，单音不叠）",
                r.Events.Count == 4 && r.Events[0].Down && r.Events[0].Code == 'Z'
                && !r.Events[1].Down && r.Events[1].Code == 'Z'
                && r.Events[2].Down && r.Events[2].Code == 'X'
                && r.Events[2].T >= r.Events[1].T,
                Dump(r))),

        // 跨八度走鼠标左右键：拖到旧实现里，这一簇会被挤进 20ms 内被游戏折成一帧 → 漏音。
        new("跨八度（修饰键）",
            new[] { N(0, 0.3, 'Z', -1), N(0.4, 0.7, 'Z', 0), N(0.8, 1.1, 'Z', 1), N(1.2, 1.5, 'Z', 0) },
            EventBuilder.ModState.None, false,
            r => Check("跨八度：左键、右键各按一次松一次，最后回到基准（不留按住的修饰键）",
                ModCount(r, EventBuilder.K_MouseLeft, true) == 1
                && ModCount(r, EventBuilder.K_MouseLeft, false) == 1
                && ModCount(r, EventBuilder.K_MouseRight, true) == 1
                && ModCount(r, EventBuilder.K_MouseRight, false) == 1,
                Dump(r))),

        new("升半音（中键）", new[] { N(0, 0.3, 'C', 0, sharp: true), N(0.4, 0.7, 'C') },
            EventBuilder.ModState.None, false,
            r => Check("升半音：中键按一次松一次，后半句不带中键",
                ModCount(r, EventBuilder.K_MouseMiddle, true) == 1
                && ModCount(r, EventBuilder.K_MouseMiddle, false) == 1
                && r.Events.Any(e => e.Kind == EventBuilder.K_Key && e.Code == 'C' && e.Down
                                     && e.T > 0.35),
                Dump(r))),

        new("同键重触发（间隔 0.5s）", new[] { N(0, 0.3, 'Z'), N(0.5, 0.8, 'Z') },
            EventBuilder.ModState.None, false,
            r => Check("同键重触发：同一根键两次按下的间隔不小于 RetriggerMs",
                Downs(r, 'Z').Count == 2
                && Downs(r, 'Z')[1] - Downs(r, 'Z')[0] >= TimeSpan.FromMilliseconds(r.Timing.RetriggerMs).TotalSeconds - 1e-9,
                Dump(r))),

        // 超范围音（移调后超出 MIDI 音域）在映射那一步就已经是空拍：Build 不过滤 InRange
        //（过滤在派发侧），这里只保证空拍不会把后面的可演奏音带坏。
        new("超范围空拍", new[] { N(0, 0.4, ' ', pitch: -1, inRange: false), N(0.5, 0.9, 'Z') },
            EventBuilder.ModState.None, false,
            r => Check("超范围：空拍之后的可演奏音仍按时按下",
                r.Events.Any(e => e.Kind == EventBuilder.K_Key && e.Code == 'Z' && e.Down
                                  && Math.Abs(e.T - 0.5) < 1e-9),
                Dump(r))),

        // 上一轮中断残留：游戏侧还按着左右键。起点必须先把它们全松开，否则这一轮第一个音的八度是错的。
        new("起点残留（左右键都按着）", new[] { N(0, 0.3, 'Z') },
            new EventBuilder.ModState(true, true, false), false,
            r => Check("起点残留：先把左右键全部松开，再按音键",
                r.Events.Count >= 3
                && r.Events[0].Kind == EventBuilder.K_MouseLeft && !r.Events[0].Down
                && r.Events[1].Kind == EventBuilder.K_MouseRight && !r.Events[1].Down
                && r.Events[2].Kind == EventBuilder.K_Key && r.Events[2].Down,
                Dump(r))),
    };

    /// <summary>同刻起音那一例必须留下"时值被压到下限"的诊断计数，否则说明让位逻辑没生效。</summary>
    private static bool ProbeSawSqueeze(EventBuilder b) => b.Probe.MinUpLimited >= 1;

    // ================= 跑语料 =================

    private static void TestCorpus()
    {
        var cases = Cases();
        var report = new InvariantReport();

        foreach (var c in cases)
        {
            foreach (var timing in Timings)
            {
                // 每个用例、每档时序都用一个新建的 EventBuilder：它带 _physHeldKey 这类上一轮的状态，
                // 复用同一个实例会让"起点残留"那一例的语料互相污染。
                var builder = new EventBuilder { Timing = timing };
                var (events, total) = builder.Build(c.Notes, c.StartMods);

                Inspect(c, timing, events, report);

                // 用例自己那条断言只跑标准档（默认档，界面默认选的那一档）
                if (timing == InputTiming.Standard)
                {
                    var run = new CaseRun(c.Name, timing, events, total, builder);
                    Lines.Add($"  用例「{c.Name}」标准档事件表：{Dump(run)}");
                    c.Verify(run);
                }
            }
        }

        Lines.Add("");

        // 不变量按"条"汇总成一行 PASS / FAIL：9 个用例 × 3 档全过才给 PASS，
        // 失败时明细里带得出是哪一例哪一档 —— 报告要短，也要能定位。
        Check("不变量：零时长按键为 0", report.ZeroLength.Count == 0, Detail(report.ZeroLength, $"{report.Presses} 次按下"));
        Check("不变量：每个按下都有配对的松开", report.Unpaired.Count == 0, Detail(report.Unpaired, $"{report.Presses} 次按下"));
        Check("不变量：修饰键先松后按，且比音键至少早一帧",
            report.ModLead.Count == 0 && report.ModOrder.Count == 0,
            Detail(report.ModLead.Concat(report.ModOrder).ToList(), ModDetail(report)));
        Check("不变量：同键两次按下间隔 ≥ RetriggerMs", report.Retrigger.Count == 0,
            Detail(report.Retrigger, Observed(report.ObservedMinRetriggerMs, "最小同键间隔")));
        Check("不变量：每次按下至少按住一帧（frame + 1ms，零时长按键的来源）", report.Hold.Count == 0,
            Detail(report.Hold, Observed(report.ObservedMinHoldMs, "最小按住")));
        Check("不变量：没被同刻起音顶掉时，按住时长 ≥ MinHoldMs", report.NaturalHold.Count == 0,
            Detail(report.NaturalHold, Observed(report.ObservedMinNaturalHoldMs, "最小按住")));

        Lines.Add("");
        Lines.Add($"  观测：音键按下 {report.Presses} 次；"
                  + Observed(report.ObservedMinModLeadMs, "最小修饰键提前量") + "；"
                  + Observed(report.ObservedMinModGapMs, "最小修饰键间隔") + "；"
                  + Observed(report.ObservedMinRetriggerMs, "最小同键间隔") + "；"
                  + Observed(report.ObservedMinHoldMs, "最小按住"));
    }

    // ================= 不变量 =================

    /// <summary>一次自检里全部不变量违规与观测到的最小值（9 个用例 × 3 档时序累计）。</summary>
    private sealed class InvariantReport
    {
        public readonly List<string> ZeroLength = new();
        public readonly List<string> Unpaired = new();
        public readonly List<string> ModLead = new();
        public readonly List<string> ModOrder = new();
        public readonly List<string> Retrigger = new();
        public readonly List<string> Hold = new();
        public readonly List<string> NaturalHold = new();

        public int Presses;

        /// <summary>修饰键按下 → 音键按下，两者相隔多少（不变量「比音键至少早一帧」看的量）。</summary>
        public double ObservedMinModLeadMs = double.MaxValue;

        /// <summary>修饰键按下 → 下一次修饰键按下。**和上面那个不是一个量**，别并成一个字段。</summary>
        public double ObservedMinModGapMs = double.MaxValue;

        public double ObservedMinRetriggerMs = double.MaxValue;
        public double ObservedMinHoldMs = double.MaxValue;
        public double ObservedMinNaturalHoldMs = double.MaxValue;
    }

    /// <summary>
    /// 逐条过一遍事件表，收集不变量违规。断言的是 spec 里 S3 那五条：
    /// 零时长按键为 0 / 每个按下都有配对的松开 / 修饰键先松后按且比音键早至少一帧 /
    /// 同键重触发 ≥ RetriggerMs / 每次按下至少按住一帧。
    ///
    /// **按住时长的下限是"一帧 + 1ms"，不是 MinHoldMs**：<c>Build</c> 里那条下限是
    /// <c>minUpT = frame + 0.001</c>，MinHoldMs 只是配置项；同刻起音时前音必须被压到"跨过一帧"，
    /// 否则整段音被游戏吃掉。所以 MinHoldMs 只在"没被同刻起音顶掉"的用例上单独断言一条。
    ///
    /// 只配对音键（K_Key）：修饰键是"状态"，一轮结束时停在按下状态是正常的（派发侧负责收尾），
    /// 拿它当"没配对"会误报。
    /// </summary>
    private static void Inspect(SmokeCase c, InputTiming timing, List<EventBuilder.PhysicalEvent> evs,
        InvariantReport r)
    {
        string where = $"{c.Name} / {timing.Name}档";
        double frame = timing.FrameMs;
        double retrigMs = Math.Max(timing.RetriggerMs, frame);
        double holdFloorMs = frame + 1.0;      // = Build 里的 minUpT

        var open = new Dictionary<char, double>();
        var lastDown = new Dictionary<char, double>();
        double lastModPressT = double.NaN;

        for (int i = 0; i < evs.Count; i++)
        {
            var e = evs[i];

            if (e.Kind != EventBuilder.K_Key)
            {
                // 同刻的修饰键事件：先松开所有不该按的，再按下该按的。
                // 顺序反了半音/八度切换时按下会被松开吃掉（同刻不依赖排序稳定性，靠的就是这个顺序）。
                for (int j = i + 1; j < evs.Count && Math.Abs(evs[j].T - e.T) < 1e-9; j++)
                    if (evs[j].Kind != EventBuilder.K_Key && e.Down && !evs[j].Down)
                        r.ModOrder.Add($"{where}：{e.T:F4}s 同刻里修饰键「按下」排在了「松开」前面");

                if (e.Down)
                {
                    // 两次修饰键按下之间隔多远。**记到自己的字段上**：它不是「提前量」——
                    // 跨八度那一例里左键抬起、右键按下挨得很近，并进 ObservedMinModLeadMs
                    // 会让报告里那行「最小修饰键提前量」变成一个跟提前量无关的数。
                    if (!double.IsNaN(lastModPressT))
                        Observed(r, ref r.ObservedMinModGapMs, (e.T - lastModPressT) * 1000.0);
                    lastModPressT = e.T;
                }
                continue;
            }

            if (e.Down)
            {
                // 修饰键要比音键早至少一帧。只看"按下"：起点残留那一例的清理式"松开"与音键同刻，
                // 那是启动态的一次性动作，不是每音的调度路径。
                if (!double.IsNaN(lastModPressT))
                {
                    double leadMs = (e.T - lastModPressT) * 1000.0;
                    Observed(r, ref r.ObservedMinModLeadMs, leadMs);
                    if (leadMs < frame - 1e-6)
                        r.ModLead.Add($"{where}：键 {e.Code} 在 {e.T:F4}s 按下时，修饰键才按下 {leadMs:F1}ms（< 一帧 {frame:F1}ms）");
                }

                if (lastDown.TryGetValue(e.Code, out double prev))
                {
                    double gapMs = (e.T - prev) * 1000.0;
                    Observed(r, ref r.ObservedMinRetriggerMs, gapMs);
                    if (gapMs < retrigMs - 1e-6)
                        r.Retrigger.Add($"{where}：键 {e.Code} 两次按下只隔 {gapMs:F1}ms（< {retrigMs:F1}ms）");
                }

                lastDown[e.Code] = e.T;
                open[e.Code] = e.T;
                r.Presses++;
                continue;
            }

            if (!open.TryGetValue(e.Code, out double downT))
            {
                r.Unpaired.Add($"{where}：键 {e.Code} 在 {e.T:F4}s 松开，前面没有对应的按下");
                continue;
            }
            open.Remove(e.Code);

            double holdMs = (e.T - downT) * 1000.0;
            if (holdMs <= 0) r.ZeroLength.Add($"{where}：键 {e.Code} 在 {downT:F4}s 按下、{e.T:F4}s 松开（零时长）");
            Observed(r, ref r.ObservedMinHoldMs, holdMs);
            if (holdMs < holdFloorMs - 1e-6)
                r.Hold.Add($"{where}：键 {e.Code} 只按住了 {holdMs:F1}ms（< 一帧 + 1ms = {holdFloorMs:F1}ms）");
            else if (!c.SameInstantPacked)
            {
                Observed(r, ref r.ObservedMinNaturalHoldMs, holdMs);
                if (holdMs < timing.MinHoldMs - 1e-6)
                    r.NaturalHold.Add($"{where}：键 {e.Code} 只按住了 {holdMs:F1}ms（< MinHoldMs {timing.MinHoldMs:F0}ms）");
            }
        }

        foreach (var kv in open)
            r.Unpaired.Add($"{where}：键 {kv.Key} 在 {kv.Value:F4}s 按下后没有松开");
    }

    private static void Observed(InvariantReport r, ref double field, double valueMs)
    {
        if (valueMs < field) field = valueMs;
    }

    // ================= 裁剪 =================

    /// <summary>
    /// 钉住的程序集在裁剪后还在不在。
    ///
    /// 这一条只有自检能验：NUnit 跑的是没裁剪的构建，那儿的程序集永远都在文件旁边。
    /// 名字与 csproj 的 <c>TrimmerRootAssembly</c> 名单对应，改一边要改另一边。
    /// 自检不建窗口，所以这里只验"类型还在、成员没被裁掉"，不验它们跑起来对不对。
    /// </summary>
    private static void TestPinnedAssemblies()
    {
        var missing = new List<string>();
        foreach (string name in PinnedAssemblies)
        {
            try { Assembly.Load(name); }
            catch (Exception ex) { missing.Add($"{name}（{ex.GetType().Name}）"); }
        }
        Check("发布：钉住的程序集都还在（裁掉了就会加载失败）",
            missing.Count == 0,
            missing.Count == 0 ? $"{PinnedAssemblies.Length} 个" : string.Join(" | ", missing));

        // DryWetMidi 是按"大量用反射"钉住的：类型在，且它的公开方法没被裁掉。
        // 这里只用反射按名字取，不在 App 里直接引用这个包 —— spec 规定 Core 的 SongProject
        // 是唯一允许出现 DryWetMidi 的地方。
        var t = Type.GetType("Melanchall.DryWetMidi.Core.MidiFile, Melanchall.DryWetMidi");
        bool hasRead = t != null && t.GetMethods().Any(m => m.Name == "Read");
        Check("发布：DryWetMidi 的类型与公开方法没被裁掉", hasRead,
            t == null ? "Melanchall.DryWetMidi.Core.MidiFile 取不到" : "MidiFile.Read 在");
    }

    // ================= 小工具 =================

    /// <summary>事件表的一行文本：时间 + 是哪个键/鼠标键 + 按下还是松开。报告里当证据留档。</summary>
    private static string Describe(EventBuilder.PhysicalEvent e)
    {
        string what = e.Kind switch
        {
            EventBuilder.K_MouseLeft => "左键",
            EventBuilder.K_MouseRight => "右键",
            EventBuilder.K_MouseMiddle => "中键",
            _ => e.Code == ' ' ? "空拍" : e.Code.ToString(),
        };
        return $"{e.T:F4}s {what}{(e.Down ? "按下" : "松开")}";
    }

    private static string Dump(CaseRun r)
        => r.Events.Count == 0
            ? "（空）"
            : string.Join(" | ", r.Events.Select(Describe));

    private static int ModCount(CaseRun r, int kind, bool down)
        => r.Events.Count(e => e.Kind == kind && e.Down == down);

    private static List<double> Downs(CaseRun r, char key)
        => r.Events.Where(e => e.Kind == EventBuilder.K_Key && e.Code == key && e.Down)
                   .Select(e => e.T).ToList();

    /// <summary>观测值 → 一行文字；一次都没观测到就说"本档没有"。</summary>
    private static string Observed(double valueMs, string label)
        => double.IsPositiveInfinity(valueMs) || valueMs == double.MaxValue
            ? $"{label} 未观测到"
            : $"{label} {valueMs:F1}ms";

    /// <summary>违规明细：最多列 3 条，其余折成"还有 N 条"。</summary>
    private static string Detail(List<string> violations, string okDetail)
    {
        if (violations.Count == 0) return okDetail;
        var head = string.Join(" ; ", violations.Take(3));
        return violations.Count > 3 ? $"{head} ; 还有 {violations.Count - 3} 条" : head;
    }

    private static string ModDetail(InvariantReport r)
        => r.ObservedMinModLeadMs == double.MaxValue ? "本档没有修饰键事件" : Observed(r.ObservedMinModLeadMs, "最小提前量");

    // ================= 断言 =================

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failed++;
        Lines.Add((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? $"   [{detail}]" : ""));
    }
}

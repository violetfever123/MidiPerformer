namespace MidiPerformer.Core.UseCases.Perform.Repertoire;

/// <summary>
/// 事件表构建器。
///
/// **逐字移植自 harmonica-auto-player@a14335c 的 <c>Engine/PlaybackEngine.cs</c>**：
/// 只切出原 <c>BuildSchedule</c> 方法本体（改名 <see cref="Build"/>），外加
/// <see cref="PhysicalEvent"/> · <see cref="ModState"/> · 四个 <c>K_*</c> 常量。
/// **<c>PlaybackEngine</c> 本体没有搬**——它那套线程、暂停恢复、播放中实时移调是给
/// 「设备实时输入」和「播放中换谱」用的，我们不需要；派发由我们自己的 Dispatcher 负责。
///
/// 方法本体与 <c>Probe</c> / <c>Timing</c> / <c>_physHeldKey</c> 三个成员逐行一致，
/// 所以这里的字段名、局部变量名、注释都保持原样，不要"顺手整理"。
/// 改这些文件是对拍立刻变红的信号，见 `MidiPerformer.Tests` 的移植保真度对拍。
/// </summary>
public sealed class EventBuilder
{
    /// <summary>
    /// 一个待派发的物理输入事件。T 为**音乐时间**（秒，与速度无关）。
    /// 用可变类而非 record，是为了在派发时回填"真正发出的时刻"，供时序诊断使用。
    /// </summary>
    public sealed class PhysicalEvent
    {
        public double T;
        public readonly int Kind;
        public readonly char Code;
        public readonly bool Down;
        public readonly string Label;

        public PhysicalEvent(double t, int kind, char code, bool down, string label)
        {
            T = t; Kind = kind; Code = code; Down = down; Label = label;
        }
    }

    public const int K_Key = 0;
    public const int K_MouseLeft = 1;
    public const int K_MouseRight = 2;
    public const int K_MouseMiddle = 3;

    /// <summary>输入时序预算（物理毫秒）。构建前设置，构建时读取。</summary>
    public InputTiming Timing { get; set; } = InputTiming.Standard;

    /// <summary>
    /// 调度决策追踪输出口（null = 不追踪）。
    /// 原版里这一行裹在 <c>#if HARP_TEST</c> 里；移植时去掉宏，让真机验证随时能开追踪。
    /// </summary>
    public static Action<string>? TraceSink;

    /// <summary>本轮的输入时序诊断（构建时统计）。</summary>
    public InputTimingProbe Probe { get; } = new();

    /// <summary>修饰键的真实按下状态（避免"我以为按着"与游戏实际状态不一致）。</summary>
    public readonly record struct ModState(bool L, bool R, bool M)
    {
        public static ModState None => new(false, false, false);
    }

    /// <summary>音键当前是否真的处于按下状态（供停止/跳转后与游戏对表）。</summary>
    private char _physHeldKey = '\0';

    /// <summary>
    /// 把一个主旋律音符序列压成物理键盘/鼠标事件时间表（音乐时间，秒，与速度无关）。
    /// 旧实现把所有最小间隔写成 12ms / 8ms 这类远小于一帧的硬编码值，
    /// 把"松开前音 + 八度键 + 中键 + 本音按下"全挤在 20ms 内，游戏按帧采样时整簇被折叠、
    /// 排在末尾的音键被吃掉 → 漏音。本实现所有最小间隔改用 InputTiming 的**物理毫秒**：
    /// 修饰键比音键早 ModLeadMs 且音键至少晚一帧；同键两次按下 ≥ RetriggerMs；
    /// 每次按下至少按住一帧。
    ///
    /// 音符之间用**槽位**排开，而不是只靠"前音抬起 → 后音按下"的间隔：
    /// 与前音重叠（含同刻起音）的音顺延到前音之后，时值不变。口琴是单音乐器，
    /// 同刻起音本来只能吹响一个；靠"缩短前音"去腾位置，就会产生零时长按键 ——
    /// 游戏按帧采样时一帧都读不到，整段音被吃掉。
    /// </summary>
    public (List<PhysicalEvent>, double) Build(
        IReadOnlyList<MappedNote> notes, ModState startMods)
    {
        var evs = new List<PhysicalEvent>();
        if (notes.Count == 0) return (evs, 0);

        var ordered = notes
            .OrderBy(n => n.Start)
            .ThenBy(n => n.End)
            .ToList();

        double frame = Timing.FrameMs / 1000.0;
        double modLead = Math.Max(Timing.ModLeadMs / 1000.0, frame);   // 修饰键至少提前一帧
        // 重触发间隔：至少要跨过"抬起被采样到"的那一帧，同时不小于配置值
        double retrig = Math.Max(Timing.RetriggerMs / 1000.0, frame);
        // 最短按住时刻：比一帧再多一点余量。
        // 只有刚好一帧时，若按下刚好落在帧边界上，整个按住区间可能一个帧点都不含 → 游戏读不到。
        // 加 1ms 余量后，区间内一定落得进至少一个帧点。
        double minUpT = frame + 0.001;

        // —— 修饰键状态机（起点 = 游戏侧当前真实状态）——
        Slot heldSlot = startMods.L ? Slot.Low : startMods.R ? Slot.High : Slot.Mid;
        bool heldSharp = startMods.M;

        // 起点若同时按着左右键（异常残留），先全部释放
        if (startMods.L && startMods.R)
        {
            evs.Add(new PhysicalEvent(0, K_MouseLeft, ' ', false, ""));
            evs.Add(new PhysicalEvent(0, K_MouseRight, ' ', false, ""));
            heldSlot = Slot.Mid;
        }

        // 起点若还按着音键（上一轮中断残留），先松开
        if (_physHeldKey != '\0')
        {
            evs.Add(new PhysicalEvent(0, K_Key, _physHeldKey, false, ""));
            _physHeldKey = '\0';
        }

        void EmitModifiers(bool wantL, bool wantR, bool wantM, double modT)
        {
            // 顺序固定：先松开所有不该按的（左、右、中），再按下所有该按的。
            // 这样即便同刻也不依赖排序稳定性，且半音切换时"松"先于"按"。
            if (heldSlot == Slot.Low && !wantL)
                evs.Add(new PhysicalEvent(modT, K_MouseLeft, ' ', false, ""));
            if (heldSlot == Slot.High && !wantR)
                evs.Add(new PhysicalEvent(modT, K_MouseRight, ' ', false, ""));
            if (heldSharp && !wantM)
                evs.Add(new PhysicalEvent(modT, K_MouseMiddle, ' ', false, ""));

            if (wantL && heldSlot != Slot.Low)
                evs.Add(new PhysicalEvent(modT, K_MouseLeft, ' ', true, ""));
            if (wantR && heldSlot != Slot.High)
                evs.Add(new PhysicalEvent(modT, K_MouseRight, ' ', true, ""));
            if (wantM && !heldSharp)
                evs.Add(new PhysicalEvent(modT, K_MouseMiddle, ' ', true, ""));
        }

        char? heldKey = null;
        double heldDownT = 0;      // 前音实际按下时刻
        double heldUpT = 0;        // 前音实际抬起时刻
        var lastDown = new Dictionary<char, double>();

        // 槽位起点：本音必须晚于上一个音（口琴是单音），槽位终点 = 前音实际抬起时刻。
        double slotStart = 0;

        foreach (var n in ordered)
        {
            double baseStart = Math.Max(0, n.Start);
            double endT = Math.Max(baseStart, n.End);      // 谱面结束时刻（保留原时值）
            double duration = endT - baseStart;

            // ① 本音最早能按下的时刻：
            //    - baseStart：谱面时刻（不与前音重叠时完全按原谱）
            //    - slotStart：前音实际抬起时刻（口琴是单音，重叠音必须排开）
            //    - 前音按下 + minUpT：保证前音能跨过一个帧点，被游戏采样到。
            //      少了这条，紧随其后的音就会把前音的时值压成 0 → 游戏整段读不到 → 漏音。
            double t = Math.Max(baseStart, slotStart);
            if (heldKey != null && t < heldDownT + minUpT) t = heldDownT + minUpT;
            endT = t + duration;

            bool wantL = n.OctaveSlot == Slot.Low;
            bool wantR = n.OctaveSlot == Slot.High;
            bool wantM = n.Sharp;

            // ② 同一根音键的重触发间隔（旧版只给 12ms，短于一帧 → 两音粘连）
            double downT = t;
            if (lastDown.TryGetValue(n.Key, out double prevDown) && downT < prevDown + retrig)
                downT = prevDown + retrig;

            // 若顺延已超过本音结束时刻，就按"本音可用时长"临时收敛重触发间隔：
            // 极快段落宁可留一点粘连风险，也不能把整段音推没（时值会归零）。
            if (lastDown.TryGetValue(n.Key, out prevDown) && downT > endT)
            {
                double avail = Math.Max(0, endT - prevDown);
                double effRetrig = Math.Max(frame, Math.Min(retrig, avail));
                downT = Math.Max(t, Math.Min(endT, prevDown + effRetrig));
            }

            // ③ 前音抬起：最早是它的谱面结束时刻，最晚是本音按下时刻。
            //    ①保证了这个区间至少跨过一个帧点，所以不会再出现 upT == downT 的零时长按键。
            if (heldKey is char prev)
            {
                double upT = Math.Min(heldUpT, downT);
                upT = Math.Min(downT, Math.Max(upT, heldDownT + minUpT));   // 至少跨一个帧点，且不越过本音
                if (upT < heldUpT - 1e-9) Probe.OnMinUpLimited();

                evs.Add(new PhysicalEvent(upT, K_Key, prev, false, ""));
                if (upT > slotStart) slotStart = upT;
                heldKey = null;
            }

            // ④ 修饰键切换：提前 modLead 发出，并保证音键至少晚于一帧
            if (wantL != (heldSlot == Slot.Low) ||
                wantR != (heldSlot == Slot.High) ||
                wantM != heldSharp)
            {
                double modT = Math.Max(0, downT - modLead);
                EmitModifiers(wantL, wantR, wantM, modT);
                heldSlot = wantL ? Slot.Low : wantR ? Slot.High : Slot.Mid;
                heldSharp = wantM;
                if (downT < modT + frame) downT = modT + frame;
            }

            // ⑤ 修饰键提前量若把本音按下推后了，整段跟着后移，时值不变。
            //    不能只推按下不推抬起：那会把时值压没，又变成零时长按键。
            if (downT > t)
            {
                double shift = downT - t;
                t += shift;
                endT += shift;
            }

            evs.Add(new PhysicalEvent(downT, K_Key, n.Key, true,
                NoteMapper.Describe(n, withTime: false)));
            TraceSink?.Invoke($"  音 {n.Key} 谱面 {baseStart:F4}→{baseStart + duration:F4} 槽位 {t:F4}→{endT:F4} "
                              + $"实发 down={downT:F4}"
                              + $"{(lastDown.ContainsKey(n.Key) ? $" prevDown={lastDown[n.Key]:F4}" : "")}");
            heldKey = n.Key;
            heldDownT = downT;
            heldUpT = endT;
            lastDown[n.Key] = downT;
        }

        if (heldKey is char last)
        {
            double upT = Math.Max(heldUpT, heldDownT + minUpT);
            evs.Add(new PhysicalEvent(upT, K_Key, last, false, ""));
        }

        for (int i = 0; i < evs.Count; i++)
        {
            if (evs[i].T < 0) evs[i].T = 0;
        }
        // 稳定排序：同刻事件保持"先修饰键、后音键"的插入顺序
        evs = evs.OrderBy(e => e.T).ToList();
        double total = evs.Count == 0 ? 0 : evs[^1].T;
        return (evs, total);
    }
}

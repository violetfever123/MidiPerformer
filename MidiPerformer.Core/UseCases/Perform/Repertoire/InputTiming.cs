namespace MidiPerformer.Core.UseCases.Perform.Repertoire;

/// <summary>输入时序预算（全部为物理毫秒，与播放速度无关）。</summary>
public sealed record InputTiming
{
    /// <summary>游戏采样帧长估计。</summary>
    public double FrameMs { get; init; } = 16.7;

    /// <summary>修饰键（鼠标左/右/中键）比音键提前的时间。</summary>
    public double ModLeadMs { get; init; } = 40.0;      // ≈ 2.5 帧 @60fps

    /// <summary>同一根音键两次按下的最小间隔。</summary>
    public double RetriggerMs { get; init; } = 45.0;    // ≈ 3 帧 @60fps

    /// <summary>音键最短按住时长。</summary>
    public double MinHoldMs { get; init; } = 45.0;      // ≈ 3 帧 @60fps

    /// <summary>前音抬起 → 后音按下之间的最小间隔。</summary>
    public double ReleaseGapMs { get; init; } = 40.0;   // ≈ 2.5 帧 @60fps

    /// <summary>提前派发的物理时间。须 ≥ ModLeadMs + FrameMs。</summary>
    public double LeadMs { get; init; } = 57.0;         // ≥ 40 + 16.7

    /// <summary>档位中文名（界面用）。</summary>
    public string Name { get; init; } = "标准";

    /// <summary>稳健档：30fps、掉帧或高负载时用。</summary>
    public static InputTiming Safe => new()
    {
        Name = "稳健",
        FrameMs = 33.3,
        ModLeadMs = 70,
        RetriggerMs = 80,
        MinHoldMs = 80,
        ReleaseGapMs = 70,
        LeadMs = 104         // ≥ 70 + 33.3
    };

    /// <summary>标准档：60fps 默认。</summary>
    public static InputTiming Standard => new();

    /// <summary>极限档：高帧率、跟极快的歌时用，时间余量最小。</summary>
    public static InputTiming Aggressive => new()
    {
        Name = "极限",
        FrameMs = 8.0,
        ModLeadMs = 20,
        RetriggerMs = 22,
        MinHoldMs = 22,
        ReleaseGapMs = 18,
        LeadMs = 28          // ≥ 20 + 8
    };

    /// <summary>按界面下拉框序号取档位。</summary>
    public static InputTiming FromIndex(int index) => index switch
    {
        0 => Safe,
        2 => Aggressive,
        _ => Standard
    };

    public static string[] Names => new[] { "稳健（30fps / 卡顿）", "标准（60fps 推荐）", "极限（高帧率）" };
}

/// <summary>输入时序诊断：统计音符按下时的修饰键提前量、同键重触发间隔与按住时长。</summary>
public sealed class InputTimingProbe
{
    private readonly object _gate = new();

    private double _lastModMusicT = double.NegativeInfinity;
    private double _lastKeyDownMusicT = double.NegativeInfinity;
    private char _lastKey = '\0';
    private int _heldMods;

    public int TotalNoteOn { get; private set; }
    public int ModLeadTooShort { get; private set; }    // 修饰键提前量不足一帧
    public int ModLeadZero { get; private set; }        // 修饰键与音键同刻
    public int RetriggerTooShort { get; private set; }  // 同键重触发间隔不足
    public int MinHoldTooShort { get; private set; }    // 音键按住时长不足一帧
    public double MinModLeadMs { get; private set; } = double.MaxValue;

    /// <summary>时值被压缩到最短按住下限的音数。</summary>
    public int MinUpLimited { get; private set; }

    /// <summary>前音为后音让位，时值被压到了下限。</summary>
    public void OnMinUpLimited() => MinUpLimited++;

    /// <summary>修饰键状态变化（按音乐时间）。</summary>
    public void OnModifier(double musicT, bool down)
    {
        lock (_gate)
        {
            _lastModMusicT = musicT;
            _heldMods += down ? 1 : -1;
            if (_heldMods < 0) _heldMods = 0;
        }
    }

    /// <summary>音键按下。</summary>
    public void OnNoteOn(char key, double musicT)
    {
        lock (_gate)
        {
            TotalNoteOn++;
            if (_lastKey == key)
            {
                double gapMs = (musicT - _lastKeyDownMusicT) * 1000.0;
                if (gapMs < 45.0) RetriggerTooShort++;
            }
            if (!double.IsNegativeInfinity(_lastModMusicT))
            {
                double leadMs = (musicT - _lastModMusicT) * 1000.0;
                if (leadMs < MinModLeadMs) MinModLeadMs = leadMs;
                if (leadMs < 16.7) ModLeadTooShort++;
                if (leadMs < 0.5) ModLeadZero++;
            }
            _lastKey = key;
            _lastKeyDownMusicT = musicT;
        }
    }

    /// <summary>音键抬起。</summary>
    public void OnNoteOff(char key, double musicT)
    {
        lock (_gate)
        {
            if (_lastKey == key)
            {
                double holdMs = (musicT - _lastKeyDownMusicT) * 1000.0;
                if (holdMs < 16.7) MinHoldTooShort++;
            }
        }
    }

    /// <summary>跳转/换谱后重置。</summary>
    public void Reset(double musicT)
    {
        lock (_gate)
        {
            _lastModMusicT = double.NegativeInfinity;
            _heldMods = 0;
            _lastKey = '\0';
            _lastKeyDownMusicT = double.NegativeInfinity;
        }
    }

    public string Summary()
    {
        lock (_gate)
        {
            if (TotalNoteOn == 0) return "时序诊断：无音符";
            double minLead = MinModLeadMs == double.MaxValue ? 0 : MinModLeadMs;
            return $"时序诊断：音符 {TotalNoteOn} 个；" +
                   $"修饰键提前量<16.7ms 的 {ModLeadTooShort} 个（同刻 {ModLeadZero} 个）；" +
                   $"同键重触发<45ms 的 {RetriggerTooShort} 个；" +
                   $"按住<16.7ms 的 {MinHoldTooShort} 个；" +
                   $"时值被压到下限的 {MinUpLimited} 个；" +
                   $"最小修饰键提前量 {minLead:F1}ms";
        }
    }
}

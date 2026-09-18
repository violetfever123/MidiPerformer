using System.Diagnostics;
using System.Runtime.InteropServices;
using MidiPerformer.Core.Ports.Outbound;
using MidiPerformer.Core.UseCases.Perform.Repertoire;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 试听出声的网关：winmm → **Microsoft GS Wavetable Synth**（系统自带的软波表）。
///
/// 实现的是出站端口 <see cref="IAudioSink"/>：用例层只说要放哪几个音，怎么出声是这儿的事。
/// 出声没法断言，所以这一层是**明确不测**的那一类 —— 正因如此它要尽可能薄：
/// 这里只有「开设备、按时间点发 NoteOn / NoteOff、停的时候全部松开」三件事，
/// 一点时间积分的数学都没有（那是 <c>SongWalker</c> 的活，和演奏器共用）。
///
/// **单音色**：<see cref="IAudioSink"/> 的签名只有音符，没有声道和音色，
/// 所以这一张用一个音色把多轨的音一起发出去（构造时给的那个 <c>Program</c>，默认 0 号大钢琴）。
/// 每轨各用各的音色要等接口长出声道这一维，归 08。
///
/// 不去标 <c>[SupportedOSPlatform("windows")]</c>：整个程序本来就只发 Windows 单文件 exe（见 spec），
/// 标上去只会在每个调用点上换来一串平台分析警告，换不来任何真的保护。
/// </summary>
public sealed class WinmmPreview : IAudioSink, IDisposable
{
    /// <summary>winmm 的「用系统默认输出设备」，也就是 GS 软波表。</summary>
    private const uint MidiMapper = 0xFFFFFFFF;

    private const int StatusNoteOff = 0x80;
    private const int StatusNoteOn = 0x90;
    private const int StatusController = 0xB0;
    private const int StatusProgramChange = 0xC0;

    /// <summary>CC 123 = All Notes Off。</summary>
    private const int ControllerAllNotesOff = 123;

    /// <summary>试听的力度。硬件合成器不看这个也不会变哑，给个中间值就行。</summary>
    private const int PreviewVelocity = 100;

    private readonly object _gate = new();
    private readonly int _channel;
    private readonly int _program;

    /// <summary>最近一次 <see cref="Play"/> 给的那批音。跳转之后要从新位置重排，所以得留着。</summary>
    private IReadOnlyList<MappedNote> _notes = Array.Empty<MappedNote>();

    /// <summary>播到哪儿了（音乐时间，秒）。<see cref="Seek"/> 改它，<see cref="Play"/> 从它起算。</summary>
    private double _position;

    private IntPtr _handle;
    private bool _openAttempted;
    private Thread? _worker;
    private int _generation;

    /// <param name="program">要用的 GM 音色号（0 起）。默认 0 号大钢琴。</param>
    /// <param name="channel">发到哪个声道。默认 0。</param>
    public WinmmPreview(int program = 0, int channel = 0)
    {
        _program = Math.Clamp(program, 0, 127);
        _channel = Math.Clamp(channel, 0, 15);
    }

    /// <summary>设备现在是不是开着。没装软波表（精简过的系统、某些虚拟机镜像）时是 false。</summary>
    public bool Available
    {
        get { lock (_gate) return EnsureOpen(); }
    }

    /// <summary>
    /// 从当前进度把这批音发出去。音符时间是**音乐时间**（秒，与播放倍速无关），
    /// 已经在 <c>MappedNote.Start/End</c> 里了，所以这儿只管按时间戳往下走。
    /// </summary>
    public void Play(IReadOnlyList<MappedNote> notes)
    {
        lock (_gate)
        {
            StopWorker();
            _notes = notes;

            if (notes.Count == 0 || !EnsureOpen()) return;

            int generation = ++_generation;
            double from = _position;
            var worker = new Thread(() => Run(from, generation))
            {
                IsBackground = true,
                Name = "试听播放"
            };
            _worker = worker;
            worker.Start();
        }
    }

    /// <summary>
    /// 停：**把所有正在响的音都松开**（<c>midiOutReset</c> 就是这件事），
    /// 然后把工作线程放掉。无循环，放完也是走这里。
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopWorker();
            ResetDevice();
        }
    }

    /// <summary>把播放位置挪到指定音乐时间。播放中跳转会让调度从新位置重排。</summary>
    public void Seek(double musicSeconds)
    {
        lock (_gate)
        {
            _position = Math.Max(0, musicSeconds);

            // 播放中跳转：把正在响的收掉，再按新位置重新排一遍
            if (_worker is null) return;
            var notes = _notes;
            StopWorker();
            if (notes.Count == 0 || !EnsureOpen()) return;

            int generation = ++_generation;
            double from = _position;
            var worker = new Thread(() => Run(from, generation))
            {
                IsBackground = true,
                Name = "试听播放"
            };
            _worker = worker;
            worker.Start();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopWorker();
            ResetDevice();
            if (_handle != IntPtr.Zero) MidiOutClose(_handle);
            _handle = IntPtr.Zero;
        }
    }

    // ==================== 调度线程 ====================

    /// <summary>
    /// 按时间戳把事件发出去。**合成了一个事件表再走**，所以时间上是单调的，不用回头。
    /// 一个音在起点之前就已经开始（「从当前位置播放」落在音中间）时，从当前位置起算 ——
    /// 听得见半截音，总比整段前奏凭空响一下好。
    /// </summary>
    private void Run(double from, int generation)
    {
        var watch = Stopwatch.StartNew();
        var events = BuildEvents(from);

        // 同一个音高叠着响的时候不能互相掐断：NoteOff 只有数到最后一个才真发。
        // 多轨堆在一起，同一个音高撞车是常事（两条轨都弹 C4），
        // 少了这一层，先结束的那个音会把后一个也按掉，听着像随机漏音。
        var held = new Dictionary<int, int>();

        foreach (var e in events)
        {
            if (generation != Volatile.Read(ref _generation)) return;

            WaitUntil(watch, from, e.T, generation);
            if (generation != Volatile.Read(ref _generation)) return;

            if (e.Down)
            {
                held.TryGetValue(e.Pitch, out int count);
                held[e.Pitch] = count + 1;
                Send(StatusNoteOn, e.Pitch, e.Velocity);
            }
            else
            {
                if (!held.TryGetValue(e.Pitch, out int count)) continue;
                if (count > 1)
                {
                    held[e.Pitch] = count - 1;
                    continue;
                }
                held.Remove(e.Pitch);
                Send(StatusNoteOff, e.Pitch, 0);
            }
        }

        // 放完了：把还挂着的音收干净。线程自己退出，不留尾音（无循环）。
        ResetDevice();
    }

    private readonly record struct NoteEvent(double T, bool Down, int Pitch, int Velocity);

    private List<NoteEvent> BuildEvents(double from)
    {
        var events = new List<NoteEvent>();
        foreach (var note in _notes)
        {
            if (note.End <= from) continue;
            if (note.Pitch is < 0 or > 127) continue;

            events.Add(new NoteEvent(Math.Max(note.Start, from), true, note.Pitch, PreviewVelocity));
            events.Add(new NoteEvent(note.End, false, note.Pitch, 0));
        }

        // 同刻先排抬键：一个音刚好在另一个音起音时结束，抬键排在按键前面才对得上
        events.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.Down.CompareTo(b.Down));
        return events;
    }

    /// <summary>
    /// 等到音乐时刻 <paramref name="target"/>。
    ///
    /// 粗糙的那一段交给 <c>Thread.Sleep</c>（够省电），最后 2ms 改成自旋 ——
    /// Windows 的 Sleep 粒度能到 15ms，全交给它会听出明显的抢拍 / 拖沓。
    /// 自旋只转两毫秒，而且每一步都在看取消标记，停的时候不会赖着不走。
    /// </summary>
    private void WaitUntil(Stopwatch watch, double from, double target, int generation)
    {
        double remaining = target - (from + watch.Elapsed.TotalSeconds);
        if (remaining > 0.002) Thread.Sleep((int)((remaining - 0.002) * 1000));

        while (from + watch.Elapsed.TotalSeconds < target)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            Thread.SpinWait(200);
        }
    }

    // ==================== 设备 ====================

    /// <summary>
    /// 开设备（只试一次）。旋钮是**懒开**的：没导入曲子的时候不必占着声卡，
    /// 没装软波表的机器也不该在启动那一刻就崩。
    /// </summary>
    private bool EnsureOpen()
    {
        if (_handle != IntPtr.Zero) return true;
        if (_openAttempted) return false;
        _openAttempted = true;

        if (MidiOutOpen(out IntPtr handle, MidiMapper, IntPtr.Zero, IntPtr.Zero, 0) != 0) return false;
        _handle = handle;

        Send(StatusProgramChange, _program, 0);
        return true;
    }

    private void Send(int status, int data1, int data2)
    {
        IntPtr handle = _handle;
        if (handle == IntPtr.Zero) return;

        // winmm 的那条老约定：状态 + 数据都塞进一个 DWORD 里
        uint message = (uint)(status | _channel | (data1 << 8) | (data2 << 16));
        MidiOutShortMsg(handle, message);
    }

    /// <summary>把设备上所有还按着的音放掉。停、跳转、线程收尾都走它。</summary>
    private void ResetDevice()
    {
        IntPtr handle = _handle;
        if (handle == IntPtr.Zero) return;

        Send(StatusController, ControllerAllNotesOff, 0);
        MidiOutReset(handle);
    }

    /// <summary>叫停工作线程并等它走干净。**调用方必须已经拿着锁**。</summary>
    private void StopWorker()
    {
        Interlocked.Increment(ref _generation);
        Thread? worker = _worker;
        _worker = null;
        // 等一小会儿：线程在自旋里每步都看取消标记，正常情况下立刻就出来了。
        // 等到超时也只是多等一下，不会卡死界面 —— 它是后台线程。
        worker?.Join(200);
    }

    // ==================== winmm ====================

    [DllImport("winmm.dll", EntryPoint = "midiOutOpen")]
    private static extern int MidiOutOpen(out IntPtr handle, uint deviceId, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll", EntryPoint = "midiOutShortMsg")]
    private static extern int MidiOutShortMsg(IntPtr handle, uint message);

    [DllImport("winmm.dll", EntryPoint = "midiOutReset")]
    private static extern int MidiOutReset(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiOutClose")]
    private static extern int MidiOutClose(IntPtr handle);
}

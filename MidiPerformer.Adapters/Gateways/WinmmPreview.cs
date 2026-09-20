using System.Diagnostics;
using System.Runtime.InteropServices;
using MidiPerformer.Core.Ports.Outbound;

namespace MidiPerformer.Adapters.Gateways;

/// <summary>
/// 试听出声的网关：winmm → Microsoft GS Wavetable Synth（系统自带的软波表），实现出站端口 <see cref="IAudioSink"/>。
/// 只做三件事：开设备、按时间点发 NoteOn / NoteOff、停时全部松开，时间轴数学在 <c>SongWalker</c> 里。
/// 每个音自带声道与音色（见 <see cref="PreviewNote"/>），音色号变了才补一条 <c>ProgramChange</c>。
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

    /// <summary>试听的力度；硬件合成器不看这个也不会变哑，给个中间值就行。</summary>
    private const int PreviewVelocity = 100;

    /// <summary>MIDI 的声道数；每个声道一条「上次发过去的音色」的账。</summary>
    private const int ChannelCount = 16;

    private readonly object _gate = new();

    /// <summary>最近一次 <see cref="Play"/> 给的那批音；跳转之后要从新位置重排，所以得留着。</summary>
    private IReadOnlyList<PreviewNote> _notes = Array.Empty<PreviewNote>();

    /// <summary>播到哪儿了（音乐时间，秒）。<see cref="Seek"/> 改它，<see cref="Play"/> 从它起算。</summary>
    private double _position;

    private IntPtr _handle;
    private bool _openAttempted;
    private Thread? _worker;
    private int _generation;

    /// <summary>设备现在是不是开着。没装软波表（精简过的系统、某些虚拟机镜像）时是 false。</summary>
    public bool Available
    {
        get { lock (_gate) return EnsureOpen(); }
    }

    /// <summary>
    /// 从当前进度把这批音发出去；音符时间是音乐时间（秒，与播放倍速无关），已经在 <c>PreviewNote.Note.Start/End</c> 里了。
    /// </summary>
    public void Play(IReadOnlyList<PreviewNote> notes)
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

    /// <summary>停：把所有正在响的音都松开（<c>midiOutReset</c> 就是这件事），然后把工作线程放掉。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopWorker();
            ResetDevice();
        }
    }

    /// <summary>把播放位置挪到指定音乐时间；播放中跳转会让调度从新位置重排。</summary>
    public void Seek(double musicSeconds)
    {
        lock (_gate)
        {
            _position = Math.Max(0, musicSeconds);

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
    /// 按时间戳把事件发出去；先合成一张事件表，所以时间单调，不用回头。音在起点之前已开始（从当前位置播放落在音中间）时从当前位置起算。
    /// </summary>
    private void Run(double from, int generation)
    {
        var watch = Stopwatch.StartNew();
        var events = BuildEvents(from);

        // 同一音高叠着响时 NoteOff 要数到最后一个才真发；键里带声道，否则另一个声道同音高的音会被吞掉。
        var held = new Dictionary<(int Channel, int Pitch), int>();

        // 每个声道上一次发过去的音色，-1 = 还没发过；每跑一趟都从零重发，因为整批重排后设备上是什么音色说不准。
        var sentProgram = new int[ChannelCount];
        Array.Fill(sentProgram, -1);

        foreach (var e in events)
        {
            if (generation != Volatile.Read(ref _generation)) return;

            WaitUntil(watch, from, e.T, generation);
            if (generation != Volatile.Read(ref _generation)) return;

            if (e.Down)
            {
                // 换音色要在按键之前：反过来的话，这一批的头一个音还是上一个音色
                if (sentProgram[e.Channel] != e.Program)
                {
                    Send(StatusProgramChange, e.Channel, e.Program, 0);
                    sentProgram[e.Channel] = e.Program;
                }

                var key = (e.Channel, e.Pitch);
                held.TryGetValue(key, out int count);
                held[key] = count + 1;
                Send(StatusNoteOn, e.Channel, e.Pitch, e.Velocity);
            }
            else
            {
                var key = (e.Channel, e.Pitch);
                if (!held.TryGetValue(key, out int count)) continue;
                if (count > 1)
                {
                    held[key] = count - 1;
                    continue;
                }
                held.Remove(key);
                Send(StatusNoteOff, e.Channel, e.Pitch, 0);
            }
        }

        ResetDevice();
    }

    /// <summary><paramref name="Program"/> 只有按键那一条有意义（抬键不看音色）。</summary>
    private readonly record struct NoteEvent(
        double T, bool Down, int Channel, int Pitch, int Velocity, int Program);

    private List<NoteEvent> BuildEvents(double from)
    {
        var events = new List<NoteEvent>();
        foreach (var preview in _notes)
        {
            var note = preview.Note;
            if (note.End <= from) continue;
            // 越界的声道 / 音高会拼成另一条 MIDI 指令，而且不会有任何地方报错，在这里挡掉。
            if (note.Pitch is < 0 or > 127) continue;
            if (preview.Channel is < 0 or > 15) continue;

            int program = Math.Clamp(preview.Program, 0, 127);
            events.Add(new NoteEvent(
                Math.Max(note.Start, from), true, preview.Channel, note.Pitch, PreviewVelocity, program));
            events.Add(new NoteEvent(note.End, false, preview.Channel, note.Pitch, 0, program));
        }

        // 同刻先排抬键：一个音刚好在另一个音起音时结束，抬键排在按键前面才对得上
        events.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.Down.CompareTo(b.Down));
        return events;
    }

    /// <summary>
    /// 等到音乐时刻 <paramref name="target"/>：粗的那一段交给 <c>Thread.Sleep</c>，最后 2ms 改成自旋，因为 Windows 的 Sleep 粒度能到 15ms。
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
    /// 开设备，只试一次（懒开）：没导入曲子的时候不必占着声卡，没装软波表的机器也不该在启动那一刻就崩；开的时候不发换音色，那条由调度线程发。
    /// </summary>
    private bool EnsureOpen()
    {
        if (_handle != IntPtr.Zero) return true;
        if (_openAttempted) return false;
        _openAttempted = true;

        if (MidiOutOpen(out IntPtr handle, MidiMapper, IntPtr.Zero, IntPtr.Zero, 0) != 0) return false;
        _handle = handle;
        return true;
    }

    private void Send(int status, int channel, int data1, int data2)
    {
        IntPtr handle = _handle;
        if (handle == IntPtr.Zero) return;

        // winmm 的老约定：状态与数据都塞进一个 DWORD 里
        uint message = (uint)(status | channel | (data1 << 8) | (data2 << 16));
        MidiOutShortMsg(handle, message);
    }

    /// <summary>
    /// 把设备上所有还按着的音放掉；停、跳转、线程收尾都走它。All Notes Off 是声道消息，必须每个声道各发一条。
    /// </summary>
    private void ResetDevice()
    {
        IntPtr handle = _handle;
        if (handle == IntPtr.Zero) return;

        for (int channel = 0; channel < ChannelCount; channel++)
            Send(StatusController, channel, ControllerAllNotesOff, 0);

        MidiOutReset(handle);
    }

    /// <summary>叫停工作线程并等它走干净。调用方必须已经拿着锁。</summary>
    private void StopWorker()
    {
        Interlocked.Increment(ref _generation);
        Thread? worker = _worker;
        _worker = null;
        // 后台线程在自旋里每步都看取消标记，正常立刻就退出了；等到超时也只是多等一下，不会卡住界面。
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

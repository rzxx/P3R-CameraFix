using p3rpc.camfix.Template;
using Reloaded.Hooks.Definitions;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace p3rpc.camfix;

/// <summary>
/// Diagnostic-only hook for P3R's native FldCameraRotParam update helper.
/// The game-thread callback performs no I/O and no per-sample allocation.
/// A timer drains the preallocated buffer to CSV from a thread-pool thread.
/// </summary>
internal sealed unsafe class CameraFilterTrace : IDisposable
{
    private const string FilterSignature =
        "48 83 EC 28 F3 0F 10 59 18 0F 28 C2 0F 54 05 ?? ?? ?? ?? 0F 28 E9 0F 2F 05 ?? ?? ?? ??";

    private readonly Reloaded.Mod.Interfaces.ILogger _logger;
    private readonly Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks _hooks;
    private readonly nint _baseAddress;
    private readonly TraceSlot[] _slots;
    private readonly StreamWriter _writer;
    private readonly Timer _flushTimer;
    private readonly object _writerLock = new();

    private IHook<UpdateCameraAxisDelegate>? _filterHook;
    private int _reserved;
    private int _read;
    private int _dropped;
    private bool _disposed;

    public CameraFilterTrace(ModContext context, nint baseAddress)
    {
        _logger = context.Logger;
        _hooks = context.Hooks!;
        _baseAddress = baseAddress;

        int capacity = Math.Clamp(Mod.Configuration.TraceCapacity, 1024, 2_000_000);
        _slots = new TraceSlot[capacity];

        string modDirectory = context.ModLoader.GetDirectoryForModId(context.ModConfig.ModId);
        string traceDirectory = Path.Combine(modDirectory, "ResearchTraces");
        Directory.CreateDirectory(traceDirectory);
        string tracePath = Path.Combine(traceDirectory, $"camera-filter-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        _writer = new StreamWriter(new FileStream(
            tracePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan));
        _writer.WriteLine($"# stopwatch_frequency={Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)}");
        _writer.WriteLine($"# process_start_utc={Process.GetCurrentProcess().StartTime.ToUniversalTime():O}");
        _writer.WriteLine($"# image_base=0x{baseAddress:X}");
        _writer.WriteLine("sequence,qpc_enter,qpc_exit,os_thread,param_address,speed,acceleration,deceleration,press,release,current_speed_before,timer_before,delta_time,input,output,current_speed_after,timer_after");
        _writer.Flush();

        _flushTimer = new Timer(_ => FlushReady(), null, 500, 500);
        _logger.WriteLine($"[P3R CamFix] Camera filter trace armed: {tracePath} (capacity {capacity}).");

        context.StartupScanner.AddMainModuleScan(FilterSignature, result =>
        {
            if (!result.Found)
            {
                _logger.WriteLine("[P3R CamFix] Camera axis filter signature not found; trace disabled.", System.Drawing.Color.Red);
                return;
            }

            nint address = _baseAddress + result.Offset;
            _filterHook = _hooks.CreateHook<UpdateCameraAxisDelegate>(UpdateCameraAxis, address);
            _filterHook.Activate();
            _logger.WriteLine($"[P3R CamFix] Camera axis filter hook active at P3R.exe+0x{result.Offset:X}.");
        });
    }

    private float UpdateCameraAxis(nint paramAddress, float deltaTime, float input)
    {
        long enter = Stopwatch.GetTimestamp();
        float speed = ReadFloat(paramAddress, 0x00);
        float acceleration = ReadFloat(paramAddress, 0x04);
        float deceleration = ReadFloat(paramAddress, 0x08);
        float press = ReadFloat(paramAddress, 0x0C);
        float release = ReadFloat(paramAddress, 0x10);
        float currentSpeedBefore = ReadFloat(paramAddress, 0x14);
        float timerBefore = ReadFloat(paramAddress, 0x18);

        float output = _filterHook!.OriginalFunction(paramAddress, deltaTime, input);

        long exit = Stopwatch.GetTimestamp();
        int index = Interlocked.Increment(ref _reserved) - 1;
        if ((uint)index >= (uint)_slots.Length)
        {
            Interlocked.Increment(ref _dropped);
            return output;
        }

        ref TraceSlot slot = ref _slots[index];
        slot.Sequence = index;
        slot.QpcEnter = enter;
        slot.QpcExit = exit;
        slot.OsThread = Native.GetCurrentThreadId();
        slot.ParamAddress = paramAddress;
        slot.Speed = speed;
        slot.Acceleration = acceleration;
        slot.Deceleration = deceleration;
        slot.Press = press;
        slot.Release = release;
        slot.CurrentSpeedBefore = currentSpeedBefore;
        slot.TimerBefore = timerBefore;
        slot.DeltaTime = deltaTime;
        slot.Input = input;
        slot.Output = output;
        slot.CurrentSpeedAfter = ReadFloat(paramAddress, 0x14);
        slot.TimerAfter = ReadFloat(paramAddress, 0x18);
        Volatile.Write(ref slot.Ready, 1);
        return output;
    }

    private void FlushReady()
    {
        if (_disposed)
            return;

        lock (_writerLock)
        {
            if (!_disposed)
                DrainReadyLocked();
        }
    }

    private void DrainReadyLocked()
    {
        int limit = Math.Min(Volatile.Read(ref _reserved), _slots.Length);
        while (_read < limit)
        {
            ref TraceSlot slot = ref _slots[_read];
            if (Volatile.Read(ref slot.Ready) == 0)
                break;

            WriteSlot(slot);
            _read++;
        }

        _writer.Flush();
    }

    private void WriteSlot(in TraceSlot slot)
    {
        _writer.Write(slot.Sequence.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.QpcEnter.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.QpcExit.ToString(CultureInfo.InvariantCulture));
        _writer.Write(','); _writer.Write(slot.OsThread.ToString(CultureInfo.InvariantCulture));
        _writer.Write(",0x"); _writer.Write(slot.ParamAddress.ToString("X", CultureInfo.InvariantCulture));
        WriteFloat(slot.Speed);
        WriteFloat(slot.Acceleration);
        WriteFloat(slot.Deceleration);
        WriteFloat(slot.Press);
        WriteFloat(slot.Release);
        WriteFloat(slot.CurrentSpeedBefore);
        WriteFloat(slot.TimerBefore);
        WriteFloat(slot.DeltaTime);
        WriteFloat(slot.Input);
        WriteFloat(slot.Output);
        WriteFloat(slot.CurrentSpeedAfter);
        WriteFloat(slot.TimerAfter);
        _writer.WriteLine();
    }

    private void WriteFloat(float value)
    {
        _writer.Write(',');
        _writer.Write(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static float ReadFloat(nint address, int offset) => *(float*)(address + offset);

    public void Dispose()
    {
        if (_disposed)
            return;

        _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        lock (_writerLock)
        {
            if (_disposed)
                return;

            DrainReadyLocked();
            _disposed = true;
            _writer.WriteLine($"# captured={Math.Min(_read, _slots.Length).ToString(CultureInfo.InvariantCulture)}");
            _writer.WriteLine($"# dropped={Volatile.Read(ref _dropped).ToString(CultureInfo.InvariantCulture)}");
            _writer.Dispose();
        }

        _flushTimer.Dispose();
    }

    private delegate float UpdateCameraAxisDelegate(nint paramAddress, float deltaTime, float input);

    private struct TraceSlot
    {
        public int Ready;
        public int Sequence;
        public long QpcEnter;
        public long QpcExit;
        public uint OsThread;
        public nint ParamAddress;
        public float Speed;
        public float Acceleration;
        public float Deceleration;
        public float Press;
        public float Release;
        public float CurrentSpeedBefore;
        public float TimerBefore;
        public float DeltaTime;
        public float Input;
        public float Output;
        public float CurrentSpeedAfter;
        public float TimerAfter;
    }

    private static class Native
    {
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }
}
